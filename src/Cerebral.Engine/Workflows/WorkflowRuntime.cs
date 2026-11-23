using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cerebral.Engine.Activation;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Routing;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Routing;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Workflows;

/// <summary>
/// Executes one claimed workflow inside the host scheduler's slot. It owns no queue or background
/// service. Every externally observable effect has a committed identity before its handler is called.
/// </summary>
public sealed class WorkflowRuntime(WorkflowStore store, WorkflowRegistry registry,
    Func<WorkflowSubmission, IReadOnlyList<string>, bool>? capabilityCheck = null,
    Func<WorkflowSubmission, string, string?>? bindingCheck = null,
    ResponseOutputPipeline? responseOutput = null,
    Action<AttributedOutputDiagnostic>? outputDiagnostic = null,
    EngineController? engine = null, WorkflowAuditPipeline? auditPipeline = null,
    Func<WorkflowSubmission, WorkflowAuditPolicy>? auditPolicy = null)
{
    public WorkflowStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));
    public WorkflowRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));
    private static readonly JsonSerializerOptions EngineJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<WorkflowRun> RunAsync(string id, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (engine?.IsPaused == true) return Store.Get(id);
            // Host shutdown is not an owner cancellation. Unclaimed work remains queued for restart.
            if (cancellationToken.IsCancellationRequested) return Store.Get(id);
            var run = Store.TryClaim(id);
            if (run is null) return Store.Get(id);

            while (run.Status == WorkflowStatus.Running)
            {
                // Owner steering can advance the revision without taking this scheduler's claim.
                run = Store.Get(id);
                if (run.Status != WorkflowStatus.Running) break;
                if (cancellationToken.IsCancellationRequested) return Interrupted(run);
                if (!Registry.TryGetDefinition(run.Submission.DefinitionId, run.Submission.DefinitionVersion, out var definition))
                    return Hold(run, "The exact workflow definition version is unavailable. Restore its module before resuming.");
                if (BindingFailure(run.Submission, definition.Descriptor.Id) is { } definitionFailure)
                    return Hold(run, definitionFailure);
                if (!Permitted(run.Submission, definition.Descriptor.RequiredCapabilities))
                    return Hold(run, "The workflow's saved and current capability grants do not permit this operation.");

                if (engine?.IsPaused == true && run.Effect?.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
                    return YieldForEnginePause(run);

                // A crash before dispatch leaves this committed intent safe to perform once. Advancing
                // the definition again would create a different effect and lose the original identity.
                if (run.Effect?.State == WorkflowEffectState.Prepared)
                {
                    run = await ExecutePreparedAsync(run, cancellationToken).ConfigureAwait(false);
                    if (run.Status == WorkflowStatus.Running) continue;
                    break;
                }
                if (run.Effect?.State is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown)
                    return Hold(run, "The previous effect has an uncertain outcome and requires reconciliation.");
                if (run.Steps >= run.Submission.EffectiveStepLimit)
                    return Fail(run, new InvalidOperationException("Workflow step budget is exhausted. Start a new authorized run with an appropriate budget."));

                try
                {
                    WorkflowSchema.Validate(run.Submission.Input, definition.Descriptor.InputSchema);
                    using var decision = engine is null ? null : await EvaluateDecisionAsync(definition, run, cancellationToken).ConfigureAwait(false);
                    var step = (decision is null ? definition.Advance(run) : ReadDecision(decision))
                        ?? throw new InvalidOperationException("The workflow returned no transition.");
                    WorkflowCommandDescriptor? command = null;
                    if (step.Next is InvokeWorkflowCommand invoke)
                    {
                        if (!Registry.TryGetCommand(invoke.CommandId, invoke.CommandVersion, out var handler))
                            return Hold(run, "The requested command version is unavailable. Restore its module before resuming.");
                        command = handler.Descriptor;
                        if (BindingFailure(run.Submission, command.Id) is { } commandFailure)
                            return Hold(run, commandFailure);
                        WorkflowSchema.Validate(invoke.Arguments, command.InputSchema);
                    }
                    ResponseOutputOutcome? routedOutput = null;
                    // Isolated advisory results stay on their typed owner channel. Response hooks
                    // and auditors must not introduce another agent, context or public chat turn.
                    if (step.Next is CompleteWorkflow complete && responseOutput is not null && run.Submission.IsolatedText is null)
                    {
                        routedOutput = await responseOutput.RouteResponseAsync(new(run.Id,
                            run.Submission.ConversationId, run.Id,
                            [new StructuredContentPart("workflow.result", complete.Result.Clone())],
                            new(ModelInclusion.Evidence)), cancellationToken).ConfigureAwait(false);
                        if (routedOutput.Disposition == RouteDisposition.Hold)
                            return Hold(run, routedOutput.Reason ?? "A response hook held the result before commit.");
                        if (routedOutput.Disposition == RouteDisposition.Reject)
                            return Fail(run, new InvalidOperationException(routedOutput.Reason ?? "A response hook rejected the result before commit."));
                    }
                    WorkflowAuditCommit? audit = null;
                    if (auditPipeline is not null && run.Submission.IsolatedText is null && step.Next is CompleteWorkflow or InvokeWorkflowCommand { CommandId: "cerebral.host:delegate" })
                    {
                        var purpose = step.Next is CompleteWorkflow ? "component_evidence_check" : "coordinator_decision_check";
                        audit = await auditPipeline.ReviewAsync(run, step, Store.JournalRevision(run.Submission.ConversationId),
                            decision?.Decision.ActivationEpoch ?? 0, auditPolicy?.Invoke(run.Submission) ?? new(), purpose, cancellationToken).ConfigureAwait(false);
                    }
                    var commit = decision is null ? Store.ApplyStepWithOutput(run.Id, run.Revision, step, command, routedOutput, audit)
                        : decision.Commit(_ => Store.ApplyStepWithOutput(run.Id, run.Revision, step, command, routedOutput, audit));
                    run = commit.Run;
                    if (commit.Output is { } output && responseOutput is not null && run.Submission.IsolatedText is null)
                    {
                        var observation = await responseOutput.ObserveCommittedAsync(output, cancellationToken).ConfigureAwait(false);
                        if (outputDiagnostic is not null)
                            foreach (var diagnostic in observation.Diagnostics)
                                try { outputDiagnostic(diagnostic); } catch { /* Diagnostics cannot change a committed result. */ }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Interrupted(run); }
                catch (EnginePausedException) { return YieldForEnginePause(run); }
                catch (EngineEvaluationException) { return YieldForEnginePause(run); }
                catch (WorkflowConflictException) { run = Store.Get(id); continue; }
                catch (Exception error) when (IsOrdinaryFailure(error)) { return Fail(run, error); }
            }

            if (run.Status != WorkflowStatus.Queued) return run;
            // CompleteEffect queued the next pure transition. Reclaim through the store so cancellation
            // and another scheduler's claim cannot be overwritten by this continuation.
        }
    }

    private Task<EngineController.DecisionLease> EvaluateDecisionAsync(IWorkflowDefinition definition, WorkflowRun run, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(run, EngineJson);
        return engine!.EvaluateAsync(run.Id, run.Revision, "cerebral.workflow.transition", 1, payload, token =>
        {
            token.ThrowIfCancellationRequested();
            var frozen = payload.Deserialize<WorkflowRun>(EngineJson) ?? throw new InvalidDataException("Workflow snapshot is missing.");
            return Task.FromResult(JsonSerializer.SerializeToElement(definition.Advance(frozen), EngineJson));
        }, ct);
    }

    private static WorkflowStep ReadDecision(EngineController.DecisionLease decision)
    {
        try
        {
            var step = decision.Decision.Payload.Deserialize<WorkflowStep>(EngineJson);
            if (step is null || step.Next is null || step.State.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Engine returned an incomplete workflow transition.");
            return step;
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            decision.Reject(BoundedError(ex)); throw new EngineEvaluationException(BoundedError(ex));
        }
    }

    private WorkflowRun YieldForEnginePause(WorkflowRun run) =>
        SaveFailureAfterSteering(run, current => Store.YieldForEnginePause(current.Id, current.Revision));

    /// <summary>Inspects an existing effect only; the scheduler owns subsequent queued execution.</summary>
    public async Task<WorkflowRun> ReconcileAsync(string id, CancellationToken cancellationToken = default)
    {
        var run = Store.Get(id);
        if (run.IsTerminal || run.Effect?.State != WorkflowEffectState.Unknown)
            throw new WorkflowConflictException("Only an uncertain, inactive effect can be reconciled.");
        var effect = run.Effect;
        if (!Registry.TryGetCommand(effect.CommandId, effect.CommandVersion, out var handler))
            return Hold(run, "The exact command version is unavailable for reconciliation.");
        if (BindingFailure(run.Submission, effect.CommandId) is { } bindingFailure)
            return Hold(run, bindingFailure);
        if (!Permitted(run.Submission, handler.Descriptor.RequiredCapabilities))
            return Hold(run, "Current and saved grants do not allow reconciliation of this command.");

        var result = await InvokeAsync(handler, run, reconcile: true, cancellationToken).ConfigureAwait(false);
        return await CommitResultAsync(run, result.Result, result.ElapsedMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles one exact inactive effect without writing a replacement uncertainty or continuing
    /// the workflow. The host owns receipt lookup admission; this layer only fences durable state.
    /// </summary>
    public async Task<WorkflowRun> ReconcileConfirmedAsync(string id, long expectedRevision, string expectedEffectId,
        Action<WorkflowRun>? commitAdmission = null, CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (string.IsNullOrWhiteSpace(expectedEffectId) || expectedEffectId.Length > 128 ||
            expectedEffectId.Any(char.IsWhiteSpace) || expectedEffectId.Any(char.IsControl))
            throw new ArgumentException("Supply an exact effect identity.", nameof(expectedEffectId));
        cancellationToken.ThrowIfCancellationRequested();
        var run = Store.Get(id);
        RequireReconciliationTuple(run, expectedRevision, expectedEffectId);
        var handler = RequireReconciliationHandler(run);

        // Invoke exactly once. Only a retained, validated confirmation enters the commit loop.
        var measured = await InvokeAsync(handler, run, reconcile: true, cancellationToken,
            confirmedReconciliation: true).ConfigureAwait(false);
        var result = measured.Result;
        if (result.State == WorkflowEffectState.Unknown)
            throw new WorkflowConflictException(result.Error ?? "Receipt lookup did not confirm the original outcome.");

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Store.Get(id);
            // A transaction may have committed before its caller observed a SQLite failure. This
            // branch can only read back an equivalent receipt from the immediate committed revision;
            // CompleteReconciledEffect cannot write when that original expected revision is stale.
            var readbackOnly = attempt > 0 && current.Revision == expectedRevision + 1 &&
                current.Effect?.Id == expectedEffectId &&
                current.Effect.State is WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or WorkflowEffectState.Cancelled;
            if (!readbackOnly)
            {
                RequireReconciliationTuple(current, expectedRevision, expectedEffectId);
                if (!ReferenceEquals(handler, RequireReconciliationHandler(current)))
                    throw new WorkflowConflictException("The registered reconciliation handler changed.");
                // Keep this outside the SQLite retry catch. An admission exception is a refusal,
                // even if its type happens to be SqliteException; it is never a provider retry.
                commitAdmission?.Invoke(current);
                cancellationToken.ThrowIfCancellationRequested();
            }
            try
            {
                return Store.CompleteReconciledEffect(id, expectedRevision, expectedEffectId,
                    result, measured.ElapsedMilliseconds);
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RequireReconciliationTuple(WorkflowRun run, long expectedRevision, string expectedEffectId)
    {
        if (run.Revision != expectedRevision || run.Status != WorkflowStatus.Waiting ||
            run.Effect?.Id != expectedEffectId || run.Effect.State != WorkflowEffectState.Unknown)
            throw new WorkflowConflictException("The expected inactive uncertain run/effect tuple changed.");
    }

    private IWorkflowCommandHandler RequireReconciliationHandler(WorkflowRun run)
    {
        var effect = run.Effect!;
        if (!Registry.TryGetCommand(effect.CommandId, effect.CommandVersion, out var handler))
            throw new WorkflowConflictException("The exact command version is unavailable for reconciliation.");
        if (BindingFailure(run.Submission, effect.CommandId) is { } failure)
            throw new WorkflowConflictException(failure);
        if (!Permitted(run.Submission, handler.Descriptor.RequiredCapabilities))
            throw new WorkflowConflictException("Current and saved grants do not allow reconciliation of this command.");
        return handler;
    }

    /// <summary>Queues an explicitly resumed run without bypassing the host scheduler's capacity.</summary>
    public Task<WorkflowRun> ResumeAsync(string id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Store.Resume(id, expectedRevision));
    }

    /// <summary>Rejects an inactive prepared intent only when its exact definition can plan from that signal.</summary>
    public WorkflowRun RejectPrepared(WorkflowPreparedRejectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store.RejectPrepared(request, RequirePreparedRejectionSupport);
    }

    /// <summary>Separately permits one exact newly prepared effect under its own exhausted budget key.</summary>
    public WorkflowRun PermitPreparedOnce(WorkflowPreparedPermitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store.PermitPreparedOnce(request, run =>
        {
            RequirePreparedRejectionSupport(run);
            var effect = run.Effect!;
            if (!Registry.TryGetCommand(effect.CommandId, effect.CommandVersion, out var handler) ||
                BindingFailure(run.Submission, effect.CommandId) is not null ||
                !Permitted(run.Submission, handler.Descriptor.RequiredCapabilities))
                throw new WorkflowConflictException("The exact command version, binding or grants are unavailable.");
            WorkflowSchema.Validate(effect.Arguments, handler.Descriptor.InputSchema);
            return handler.GetFailureBudgetKey(new(effect.Id, run.Submission, effect.Arguments) { RunId = run.Id });
        });
    }

    private void RequirePreparedRejectionSupport(WorkflowRun run)
    {
        if (!Registry.TryGetDefinition(run.Submission.DefinitionId, run.Submission.DefinitionVersion,
                out var definition) ||
            BindingFailure(run.Submission, definition.Descriptor.Id) is not null ||
            !Permitted(run.Submission, definition.Descriptor.RequiredCapabilities) ||
            definition is not IWorkflowPreparedRejectionSupport support ||
            !support.CanRejectPrepared(run))
            throw new WorkflowConflictException("The exact accepted definition does not support prepared rejection.");
    }

    private async Task<WorkflowRun> ExecutePreparedAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        var effect = run.Effect!;
        if (!Registry.TryGetCommand(effect.CommandId, effect.CommandVersion, out var handler))
            return Hold(run, "The exact command version is unavailable. Restore its module before resuming.");
        if (BindingFailure(run.Submission, effect.CommandId) is { } bindingFailure)
            return Hold(run, bindingFailure);
        if (!Permitted(run.Submission, handler.Descriptor.RequiredCapabilities))
            return Hold(run, "Current and saved capability grants do not allow this command.");
        var remaining = run.Submission.ActiveTimeLimitSeconds * 1000L - run.ActiveMilliseconds;
        if (remaining < 1) return Hold(run, "The active execution time budget is exhausted. Start a new authorized request to continue.");
        try
        {
            WorkflowSchema.Validate(effect.Arguments, handler.Descriptor.InputSchema);
            if (handler is IContextProviderCommandHandler contextProvider)
                _ = ContextProviderNormalizer.ReadRequest(contextProvider.Provider, effect.Arguments);
            var key = handler.GetFailureBudgetKey(new(effect.Id, run.Submission, effect.Arguments) { RunId = run.Id });
            if (key is not null && (!run.Submission.FailureLimits.TryGetValue(key, out var limit) ||
                run.FailedEffects.GetValueOrDefault(key) >= limit &&
                !WorkflowStore.HasAvailablePreparedPermit(run, key)))
                return Hold(run, "The command's failed-attempt limit has been reached or was not admitted. User intervention is required.");
            cancellationToken.ThrowIfCancellationRequested();
            run = engine is null ? Store.DispatchEffect(run.Id, run.Revision,
                (int)Math.Min(remaining, handler.Descriptor.TimeoutSeconds * 1000L), key)
                : engine.Dispatch(() => Store.DispatchEffect(run.Id, run.Revision,
                    (int)Math.Min(remaining, handler.Descriptor.TimeoutSeconds * 1000L), key));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Interrupted(run); }
        catch (EnginePausedException) { return YieldForEnginePause(run); }
        catch (WorkflowConflictException) { return Store.Get(run.Id); }
        catch (Exception error) when (IsOrdinaryFailure(error)) { return Fail(run, error); }

        var result = await InvokeAsync(handler, run, reconcile: false, cancellationToken).ConfigureAwait(false);
        return await CommitResultAsync(run, result.Result, result.ElapsedMilliseconds).ConfigureAwait(false);
    }

    private sealed record MeasuredResult(WorkflowCommandResult Result, long ElapsedMilliseconds);

    private static async Task<MeasuredResult> InvokeAsync(IWorkflowCommandHandler handler, WorkflowRun run,
        bool reconcile, CancellationToken cancellationToken, bool confirmedReconciliation = false)
    {
        // Rejected provider output is not confirmation of the original operation. Preserve the
        // legacy execution/id-only failure result, but let confirmed-only reconciliation refuse it.
        WorkflowCommandResult RejectProviderOutput(WorkflowCommandResult value, string error) =>
            confirmedReconciliation ? Unknown("Context provider result was rejected: " + error)
                : RejectedProvider(value, error);
        var started = Stopwatch.GetTimestamp();
        MeasuredResult Measured(WorkflowCommandResult value) => new(value, Math.Max(1, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(reconcile
            ? Math.Min(60_000, handler.Descriptor.TimeoutSeconds * 1000) : run.Effect!.ReservedExecutionMilliseconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var context = new WorkflowCommandContext(run.Effect!.Id, run.Submission, run.Effect.Arguments)
            { RunId = run.Id, PreviousEffect = reconcile ? run.Effect : null };
        try
        {
            ContextProviderRequest? contextRequest = handler is IContextProviderCommandHandler contextProvider
                ? ContextProviderNormalizer.ReadRequest(contextProvider.Provider, context.Arguments) : null;
            // Do not use WaitAsync: releasing the scheduler slot while a noncooperative command still
            // runs can exceed resource limits and allow a second effect to overlap the first.
            var result = await (reconcile ? handler.ReconcileAsync(context, linked.Token)
                : handler.ExecuteAsync(context, linked.Token)).ConfigureAwait(false);
            if (confirmedReconciliation) linked.Token.ThrowIfCancellationRequested();
            if (result is null) return Measured(Unknown("The command returned no outcome; inspect the existing effect before retrying."));
            if (result.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or
                WorkflowEffectState.Cancelled or WorkflowEffectState.Unknown))
                return Measured(Unknown("The command returned an invalid completion state; inspect the existing effect before retrying."));
            if (result.State == WorkflowEffectState.Succeeded && result.Data is null)
                return Measured(handler is IContextProviderCommandHandler
                    ? RejectProviderOutput(result, "A successful context provider returned no resolution.")
                    : Unknown("The command reported success without its required result; inspect the existing effect before retrying."));
            if (result.Data is { } data)
            {
                try { WorkflowSchema.Validate(data, handler.Descriptor.OutputSchema); }
                catch (ArgumentException error) when (handler is IContextProviderCommandHandler &&
                                                       result.State == WorkflowEffectState.Succeeded)
                { return Measured(RejectProviderOutput(result, BoundedError(error))); }
            }
            if (result.Error?.Length > 4096 || result.ProviderReceiptId?.Length > 512)
                return Measured(handler is IContextProviderCommandHandler && result.State == WorkflowEffectState.Succeeded
                    ? RejectProviderOutput(result, "A successful context provider returned oversized outcome metadata.")
                    : Unknown("The command returned oversized outcome metadata; inspect the existing effect before retrying."));
            if (handler is IContextProviderCommandHandler provider)
            {
                if (result.ConversationCheckpoint is not null)
                    return Measured(RejectProviderOutput(result,
                        "A read-only context provider cannot adopt a conversation checkpoint."));
                if (result.State == WorkflowEffectState.Succeeded)
                {
                    if (string.IsNullOrWhiteSpace(result.ProviderReceiptId))
                        return Measured(RejectProviderOutput(result,
                            "A successful context provider must return a bounded provider receipt."));
                    try
                    {
                        var resolution = ContextProviderNormalizer.ReadResolution(result.Data!.Value);
                        var normalized = ContextProviderNormalizer.Normalize(provider.Provider, contextRequest!, resolution,
                            context.EffectId, result.ProviderReceiptId);
                        result = result with { Data = ContextProviderNormalizer.ToElement(normalized) };
                    }
                    catch (ArgumentException error)
                    {
                        return Measured(RejectProviderOutput(result, BoundedError(error)));
                    }
                }
            }
            // Use the persistence encoding bound as well as the raw schema bound: JSON escaping can
            // increase a plugin result's serialized size. Reject invalid output before CommitResult.
            WorkflowStore.ValidateResult(result);
            // A confirmed result remains truthful even if cancellation was requested just before it
            // arrived. The store applies CancelRequested without erasing the actual effect outcome.
            if (confirmedReconciliation) linked.Token.ThrowIfCancellationRequested();
            return Measured(result with { Data = result.Data?.Clone() });
        }
        catch (OperationCanceledException)
        {
            return Measured(Unknown(timeout.IsCancellationRequested
                ? "The command timed out after dispatch; its effects require reconciliation."
                : "The command was interrupted after dispatch; its effects require reconciliation."));
        }
        catch (Exception error) when (IsOrdinaryFailure(error))
        {
            return Measured(Unknown("The command did not return a verified outcome: " + BoundedError(error)));
        }
    }

    private async Task<WorkflowRun> CommitResultAsync(WorkflowRun run, WorkflowCommandResult result, long? elapsedMilliseconds = null)
    {
        // Retry only this idempotent receipt transaction, never the already executed provider.
        // Retaining its confirmed result is independent of owner/shutdown cancellation.
        for (var attempt = 0; ; attempt++)
        {
            try { return CommitResult(run, result, elapsedMilliseconds); }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1))).ConfigureAwait(false);
            }
        }
    }

    private WorkflowRun CommitResult(WorkflowRun run, WorkflowCommandResult result, long? elapsedMilliseconds = null)
    {
        var effectId = run.Effect!.Id;
        try { return Store.CompleteEffect(run.Id, effectId, result, elapsedMilliseconds); }
        catch (WorkflowConflictException error)
        {
            var current = Store.Get(run.Id);
            if (result.ConversationCheckpoint is null || current.Effect is not { } currentEffect || currentEffect.Id != effectId ||
                currentEffect.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
                return current;
            // A confirmed command result can fail checkpoint admission. Retain its data and the
            // explicit rejection; losing that outcome would falsely turn a store conflict into an
            // uncertain provider operation. The retained baseline is never replaced on this path.
            var rejected = result with { State = WorkflowEffectState.Failed, ConversationCheckpoint = null,
                Error = "The confirmed command result could not adopt its conversation checkpoint: " + BoundedError(error) };
            try { return Store.CompleteEffect(run.Id, effectId, rejected, elapsedMilliseconds); }
            catch (WorkflowConflictException) { return Store.Get(run.Id); }
        }
    }

    private bool Permitted(WorkflowSubmission submission, IReadOnlyList<string> capabilities) =>
        capabilityCheck is not null ? capabilityCheck(submission, capabilities) : capabilities.Count == 0;

    private string? BindingFailure(WorkflowSubmission submission, string contributionId) =>
        bindingCheck?.Invoke(submission, contributionId);

    private WorkflowRun Interrupted(WorkflowRun run) => Hold(run, WorkflowStore.InterruptedWaitReason);

    private WorkflowRun Hold(WorkflowRun run, string reason) =>
        SaveFailureAfterSteering(run, current => Store.Hold(current.Id, current.Revision, reason));

    private WorkflowRun Fail(WorkflowRun run, Exception error) =>
        SaveFailureAfterSteering(run, current => Store.Fail(current.Id, current.Revision, BoundedError(error)));

    private WorkflowRun SaveFailureAfterSteering(WorkflowRun run, Func<WorkflowRun, WorkflowRun> save)
    {
        while (true)
        {
            try { return save(run); }
            catch (WorkflowConflictException)
            {
                var current = Store.Get(run.Id);
                // Each steering append increments both counters once. Retry only that bounded
                // conflict; cancellation, answers or another execution transition retain ownership.
                var accepted = current.Steering.Count - run.Steering.Count;
                if (accepted <= 0 || current.Revision - run.Revision != accepted) return current;
                run = current;
            }
        }
    }

    private static WorkflowCommandResult Unknown(string error) => new(WorkflowEffectState.Unknown, Error: error);
    private static WorkflowCommandResult RejectedProvider(WorkflowCommandResult result, string error) =>
        new(WorkflowEffectState.Failed, Error: "Context provider result was rejected: " + error,
            ProviderReceiptId: result.ProviderReceiptId);
    private static string BoundedError(Exception error) => error.Message.Length <= 3072 ? error.Message : error.Message[..3072];
    private static bool IsOrdinaryFailure(Exception error) => error is not (OutOfMemoryException or AccessViolationException);
}
