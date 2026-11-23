using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private sealed class IsolatedTextDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(IsolatedTextGenerationContract.DefinitionId,
            IsolatedTextGenerationContract.DefinitionVersion, "One host-admitted isolated advisory generation.",
            Schema("""{"type":"object"}"""), []);
        public WorkflowStep Advance(WorkflowRun run)
        {
            var request = IsolatedTextGenerationAdmission.RequireSubmission(run.Submission);
            if (request.ExpiresAtUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return new(run.State, new CompleteWorkflow(Element(new { text = (string?)null }), false, "The advisory request expired."));
            if (run.Steps == 0)
                return new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(IsolatedTextGenerationContract.EffectId, 1, WorkflowContract.EmptyObject));
            if (run.Signal.Kind != WorkflowSignalKind.EffectResult)
                throw new WorkflowConflictException("An isolated request cannot start another effect.");
            var result = Read<WorkflowCommandResult>(run.Signal.Data);
            return new(run.State, new CompleteWorkflow(result.Data ?? Element(new { text = (string?)null }),
                result.State == WorkflowEffectState.Succeeded, result.Error));
        }
    }

    private sealed class IsolatedTextHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(IsolatedTextGenerationContract.EffectId, 1,
            "Generate only fixed policy plus public input through an already loaded local native model; never expand context or replay.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), Schema("""{"type":"object"}"""),
            [], WorkflowReplayPolicy.Never, 30);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            if (owner.workflowHost is null || owner.models is not ModelRuntime runtime || context.RunId != context.Submission.RequestId)
                throw new UnauthorizedAccessException("The isolated loaded-model host is unavailable.");
            var host = owner.workflowHost();
            var saved = host.Store.Get(context.RunId);
            if (saved.Effect?.Id != context.EffectId || saved.Effect.CommandId != IsolatedTextGenerationContract.EffectId ||
                saved.Submission.IsolatedText is null ||
                !System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(saved.Submission), CerebralJson.Element(context.Submission)))
                throw new UnauthorizedAccessException("The isolated effect is not the exact durable request.");
            var request = IsolatedTextGenerationAdmission.RequireSubmission(context.Submission);
            return IsolatedTextGenerationExecutor.RunAsync(request, () => host.RequireIsolatedCurrent(context.Submission),
                async cancellation =>
                {
                    var accepted = host.RequireIsolatedCurrent(context.Submission);
                    var registered = await runtime.RegisterLoadedIsolatedTextAsync(accepted.Model!, cancellation).ConfigureAwait(false);
                    return registered.CreateWorkflowAdapter(false);
                }, token);
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Unknown, context.PreviousEffect?.Result,
                "The isolated generation outcome requires review; it is never replayed."));
    }
}

internal static class IsolatedTextGenerationExecutor
{
    internal static async Task<WorkflowCommandResult> RunAsync(IsolatedTextGenerationRequest input,
        Func<WorkerExecutionSnapshot.Snapshot> validateCurrent,
        Func<CancellationToken, Task<IModelRuntimeAdapter>> loadedAdapter, CancellationToken token)
    {
        var cleanup = new WorkflowGenerationCleanup();
        ModelGenerationResult? terminal = null;
        try
        {
            token.ThrowIfCancellationRequested();
            input.Validate(DateTimeOffset.UtcNow, retained: true);
            if (input.ExpiresAtUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new OperationCanceledException("The advisory request expired.");
            var request = IsolatedTextGenerationAdmission.ModelRequest(input, validateCurrent());
            // This selector must be the existing loaded-only native registration, never ordinary
            // ModelRuntime.PrepareAsync (which may load a configured model). Tests supply a fake.
            var adapter = await loadedAdapter(token).ConfigureAwait(false);
            var prepared = await cleanup.Track(adapter).PrepareAsync(request, token).ConfigureAwait(false);
            return await cleanup.RunPreparedAsync(prepared, async () =>
            {
                token.ThrowIfCancellationRequested();
                _ = IsolatedTextGenerationAdmission.ModelRequest(input, validateCurrent());
                terminal = await prepared.GenerateAsync(null, token).ConfigureAwait(false);
                var state = terminal.Finish switch
                {
                    ModelGenerationFinish.Stop or ModelGenerationFinish.Length => WorkflowEffectState.Succeeded,
                    ModelGenerationFinish.Cancelled when terminal.CancellationConfirmed => WorkflowEffectState.Cancelled,
                    _ => WorkflowEffectState.Unknown
                };
                if (state == WorkflowEffectState.Succeeded && terminal.Error is not null) state = WorkflowEffectState.Failed;
                // A known receipt stays known even when cancellation or profile revocation makes
                // its advisory text unusable. No partial, late or length-limited output is accepted.
                var text = terminal.Finish == ModelGenerationFinish.Stop && state == WorkflowEffectState.Succeeded &&
                    !token.IsCancellationRequested && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < input.ExpiresAtUnixMilliseconds &&
                    terminal.Text.Length <= IsolatedTextGenerationContract.MaximumOutputCharacters &&
                    !terminal.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n')) ? terminal.Text : null;
                if (text is not null) _ = validateCurrent();
                return new WorkflowCommandResult(state, CerebralJson.Element(new
                { text, finish = terminal.Finish.ToString(), terminal.GeneratedTokens, terminal.EvaluatedTokens }),
                    state == WorkflowEffectState.Unknown ? WorkflowGenerationCleanup.GenerationDiagnostic : null);
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            cleanup.RecordPrimary(error);
            var state = terminal?.Finish is ModelGenerationFinish.Stop or ModelGenerationFinish.Length
                ? WorkflowEffectState.Failed : terminal is { Finish: ModelGenerationFinish.Cancelled, CancellationConfirmed: true }
                    ? WorkflowEffectState.Cancelled : cleanup.GenerationInvoked ? WorkflowEffectState.Unknown
                        : error is OperationCanceledException ? WorkflowEffectState.Cancelled : WorkflowEffectState.Failed;
            return cleanup.Apply(new(state, Error: state == WorkflowEffectState.Unknown
                ? WorkflowGenerationCleanup.GenerationDiagnostic : "The isolated request could not use its exact current profile and already loaded model. Configure the profile and load its model explicitly."));
        }
    }
}
