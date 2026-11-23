using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// A narrow model-visible projection of the Worker's existing passive metrics. The owner
/// diagnostics retain their richer identities and remain owner-only.
/// </summary>
public sealed class AgentMetricsToolExtension(StateStore store, Func<WorkerWorkflows> workflows,
    Func<JobScheduler> scheduler, ModelRuntime runtime) : IToolExtension
{
    public const string ToolName = "diagnostics.metrics";
    private const int MaximumReturnedRows = 16;
    private const int MaximumResultBytes = 24 * 1024;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ToolName,
            "Read bounded current Cerebral execution, local-slot and inference status, plus numeric generation telemetry from this accepted conversation only. Capacity counters are sampled separately; unavailable rates are not zero. This tool cannot change settings, reserve a slot, inspect another chat's content or run an owner command.",
            CerebralJson.Element(new { type = "object", properties = new { }, additionalProperties = false }),
            "diagnostics")
    ];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name != ToolName || call.Arguments.ValueKind != JsonValueKind.Object ||
            call.Arguments.EnumerateObject().Any())
            throw new ArgumentException("Metrics accepts an empty object.");
        ValidateCurrentEffect(context);
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionMetricsSnapshot? execution = null;
        ManagedSlotObservationPage? slots = null;
        InferenceObservationSnapshot? inference = null;
        GenerationTelemetrySnapshot? generation = null;
        try { execution = scheduler().ObserveExecutionMetrics(ReadInstallationId(cancellationToken)); }
        catch (Exception error) when (Unavailable(error)) { }
        cancellationToken.ThrowIfCancellationRequested();
        try { slots = runtime.ObserveManagedSlots(MaximumReturnedRows); }
        catch (Exception error) when (Unavailable(error)) { }
        cancellationToken.ThrowIfCancellationRequested();
        try { inference = runtime.Accounting.Observation.Snapshot(); }
        catch (Exception error) when (Unavailable(error)) { }
        cancellationToken.ThrowIfCancellationRequested();
        try { generation = runtime.GenerationTelemetrySnapshot(); }
        catch (Exception error) when (Unavailable(error)) { }
        cancellationToken.ThrowIfCancellationRequested();

        var value = Project(context.SessionId, context.JobId, execution, slots, inference, generation);
        var result = JsonSerializer.Serialize(value, CerebralJson.Options);
        if (Encoding.UTF8.GetByteCount(result) > MaximumResultBytes)
            throw new InvalidOperationException("The bounded metrics result exceeded its output limit.");
        return Task.FromResult(new ToolResult(true, result)
        {
            Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.metrics",
                State: execution is null || slots is null || inference is null || generation is null ? "partial" : "observed")
        });
    }

    private void ValidateCurrentEffect(ToolExecutionContext context)
    {
        RequireHostContext(context);
        var run = workflows().GetOwned(context.JobId, context.Profile);
        ValidateRunContext(run, context);
        var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
        var effective = WorkerExecutionSnapshot.EffectiveProfile(store, snapshot, run.Submission.ProfileId);
        var current = store.Get<AgentProfile>("agent", run.Submission.ProfileId);
        if (current is null || !HasGrant(snapshot.Profile, current) ||
            !WorkerExecutionSnapshot.AllowsTool(effective, ToolName) ||
            !effective.Capabilities.Contains("diagnostics", StringComparer.Ordinal) ||
            !WorkerExecutionSnapshot.AllowsTool(context.Profile, ToolName) ||
            !context.Profile.Capabilities.Contains("diagnostics", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The accepted diagnostics grant is unavailable.");
    }

    internal static void ValidateRunContext(WorkflowRun run, ToolExecutionContext context)
    {
        RequireHostContext(context);
        if (run.Submission.ConversationId != context.SessionId || run.IsTerminal || run.CancelRequested ||
            run.Submission.RequestId != context.JobId || run.Submission.ProfileId != context.Profile.Id ||
            run.Effect is not { } effect || effect.Id != context.EffectId ||
            effect.CommandId != WorkerWorkflowEffects.ToolCommand || effect.CommandVersion != 1 ||
            effect.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
            throw new UnauthorizedAccessException("The current effect does not own this conversation's metrics read.");
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(effect.Arguments);
        if (saved.Name != ToolName || saved.Arguments.ValueKind != JsonValueKind.Object ||
            saved.Arguments.EnumerateObject().Any())
            throw new UnauthorizedAccessException("The saved effect names a different metrics request.");
    }

    // Use the ordinary tool grant at admission and now. The durable effect check below
    // keeps a direct call or a newly installed tool outside an accepted assignment.
    internal static bool HasGrant(AgentProfile accepted, AgentProfile current) =>
        WorkerExecutionSnapshot.AllowsTool(accepted, ToolName) &&
        WorkerExecutionSnapshot.AllowsTool(current, ToolName) &&
        accepted.Capabilities.Contains("diagnostics", StringComparer.Ordinal) &&
        current.Capabilities.Contains("diagnostics", StringComparer.Ordinal);

    internal static void RequireHostContext(ToolExecutionContext context)
    {
        if (!context.EnforceProfileScope || string.IsNullOrWhiteSpace(context.SessionId) ||
            string.IsNullOrWhiteSpace(context.JobId) || string.IsNullOrWhiteSpace(context.EffectId))
            throw new UnauthorizedAccessException("Metrics requires a host-owned durable conversation effect.");
    }

    private string ReadInstallationId(CancellationToken token) => store.WithOwnerSnapshotState(false, state =>
    {
        if (state.ConfigurationBody is null) throw new InvalidDataException("Metrics owner identity is unavailable.");
        using var document = OwnerSnapshotJson.Parse(state.ConfigurationBody, StateStore.MaximumSnapshotConfigurationBytes);
        var fields = OwnerSnapshotJson.Fields(document.RootElement);
        var id = OwnerSnapshotJson.Text(fields, "installationId", 128, false, out _);
        OwnerSnapshotConfiguration.RequireInstallationId(id);
        return id;
    }, token);

    private static bool Unavailable(Exception error) => error is InvalidOperationException or InvalidDataException or
        IOException or ObjectDisposedException;

    internal static object Project(string conversationId, string requestId,
        ExecutionMetricsSnapshot? execution, ManagedSlotObservationPage? slots,
        InferenceObservationSnapshot? inference, GenerationTelemetrySnapshot? generation)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Metrics require an accepted conversation and request.");
        // The host snapshots are bounded and retain only current/recent observations.
        // Prefer their newest same-chat rows; an accepted request can inspect its chat's
        // recent context without acquiring identities or content from another chat.
        var chatRows = inference?.Rows.Where(row => row.Execution?.ConversationId == conversationId)
            .OrderByDescending(row => row.ObservedAt).ThenBy(row => row.RowId, StringComparer.Ordinal).ToArray() ?? [];
        var chatActive = generation?.Active.Where(row => row.Binding?.ConversationId == conversationId)
            .OrderByDescending(row => row.At).ThenBy(row => row.LaneId, StringComparer.Ordinal).ToArray() ?? [];
        var chatRecent = generation?.Recent.Where(row => row.Binding?.ConversationId == conversationId)
            .OrderByDescending(row => row.At).ThenBy(row => row.LaneId, StringComparer.Ordinal).ToArray() ?? [];
        return new
        {
            schema = "cerebral.agent-metrics/1",
            scope = "current and retained recent local observations for this accepted conversation, plus sampled aggregate capacity; source retention applies and no other conversation rows are returned",
            execution = execution is null ? (object)new { state = "unavailable" } : new
            {
                state = execution.State, sampledAt = execution.SampledAt, execution.Partial, execution.Truncated,
                execution.ObservedLocalExecutionCount, execution.LocalWorkingAgentCount,
                execution.ObservedRemoteDispatchOrReconciliationCount, execution.ObservedMaintenanceCount,
                source = execution.ProcessGenerationScope
            },
            slots = slots is null ? (object)new { state = "unavailable" } : new
            {
                state = slots.State, sampledAt = slots.SampledAt, slots.Truncated,
                slots.ModelAdmissionOpen,
                models = slots.Models.Take(MaximumReturnedRows).Select((row, index) => new
                {
                    observationIndex = index, state = row.InstanceState, row.AllocatedSlots,
                    row.ManagedAvailableSlots, row.ManagedQuarantinedSlots
                }).ToArray(),
                omittedModels = Math.Max(0, slots.Models.Count - MaximumReturnedRows),
                source = "individually sampled local managed allocators; not an atomic admission fence"
            },
            inference = inference is null ? (object)new { state = "unavailable" } : new
            {
                state = inference.State, sampledAt = inference.SampledAt, inference.Partial,
                truncated = inference.Truncated || chatRows.Length > MaximumReturnedRows,
                inference.AccountingCoverageComplete, inference.ExecutionAdmissionOpen,
                inference.ModelAdmissionOpen, inference.Counts.ObservedKnownExecutions,
                inference.Counts.CompleteExecutions, inference.Counts.ObservedKnownWorkingProfileIdentities,
                inference.Counts.CompleteWorkingProfileIdentities,
                chatRows = chatRows.Take(MaximumReturnedRows).Select(row => new
                {
                    requestScope = row.Execution?.RequestId == requestId ? "current" : "earlier-in-chat",
                    observedAt = row.ObservedAt,
                    row.Kind, row.Phase, row.NativeSlotId, row.CancellationRequested,
                    budget = row.Budget is null ? null : new
                    {
                        row.Budget.InputTokens, row.Budget.EffectiveContext,
                        row.Budget.DesiredResponse, row.Budget.Response,
                        row.Budget.ReasoningReserve, row.Budget.SafetyReserve,
                        countBasis = CountBasis(row.Budget.CountBasis)
                    },
                    cache = row.Cache is null ? null : new
                    {
                        row.Cache.PreparedInputTokens, row.Cache.CachedTokens,
                        row.Cache.ProcessedTokens, row.Cache.PromptMilliseconds
                    },
                    terminal = row.Terminal is null ? null : new
                    {
                        row.Terminal.Finish, row.Terminal.CancellationConfirmed,
                        row.Terminal.ProviderError, row.Terminal.EvaluatedTokens,
                        row.Terminal.GeneratedTokens,
                        generatedTokenScope = GeneratedTokenScope(row.Terminal.GeneratedTokenScope),
                        row.Terminal.ReasoningTokens,
                        row.Terminal.At
                    },
                    row.ProcessedInputTokens, row.OutputTokens, row.Release,
                    progress = new { row.ProgressSource.State, row.ProgressSource.ObservedAt,
                        row.ProgressSource.AgeMilliseconds }
                }).ToArray(),
                omittedChatRows = Math.Max(0, chatRows.Length - MaximumReturnedRows),
                source = "bounded captured local inference ownership; remote and missing rows unobserved"
            },
            generation = generation is null ? (object)new { state = "unavailable" } : new
            {
                state = chatActive.Length + chatRecent.Length == 0 ? "no-local-bound-samples" : "observed",
                sampledAt = generation.At,
                truncated = generation.Truncated || chatActive.Length > MaximumReturnedRows ||
                    chatRecent.Length > MaximumReturnedRows,
                active = chatActive.Take(MaximumReturnedRows).Select(row => Rate(row, requestId)).ToArray(),
                recent = chatRecent.Take(MaximumReturnedRows).Select(row => Rate(row, requestId)).ToArray(),
                omittedActive = Math.Max(0, chatActive.Length - MaximumReturnedRows),
                omittedRecent = Math.Max(0, chatRecent.Length - MaximumReturnedRows),
                source = "only numeric provider telemetry bound to this conversation; host retains at most 128 recent samples for up to five minutes, remote rates unavailable"
            }
        };
    }

    // These are host-defined labels, not unbounded provider text. Unknown future labels
    // stay explicit until this model-visible projection is reviewed for them.
    private static string CountBasis(string value) => value is
        "exact-native-template-token-ids" or "provider-declared-unclassified" ? value : "unavailable";

    private static string GeneratedTokenScope(string value) => value == "provider-unspecified"
        ? value : "unavailable";

    private static object Rate(GenerationTelemetrySample row, string requestId) => new
    {
        requestScope = row.Binding?.RequestId == requestId ? "current" : "earlier-in-chat",
        row.State, row.Phase, row.InputTokens, row.OutputTokens,
        row.InputTokensPerSecond, row.OutputTokensPerSecond, row.ElapsedMilliseconds,
        observedAt = row.At
    };
}
