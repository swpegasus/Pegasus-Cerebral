using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    private static readonly JsonSerializerOptions ToolProjectionWireJson = new(CerebralJson.Options) { WriteIndented = false };
    internal Task<WorkerReceiptProjectionAdmissionsData> RestoreReceiptProjectionAdmissionsAsync(
        WorkflowSubmission accepted, long contextRevision, long exclusionRevision, CancellationToken token) =>
        contextContributions.GetReceiptProjectionAdmissionsAsync(ExactAcceptedContext(accepted),
            contextRevision, exclusionRevision, token);

    private IWorkflowModule AddToolContextProjection(IWorkflowModule module)
    {
        if (module.Id != WorkerWorkflowEffects.ModuleId) return module;
        if (module.Commands.Any(command => command.Descriptor.Id == WorkerToolContextProjectionProtocol.CommandId))
            throw new InvalidDataException("The host projection command is already registered.");
        return new ToolProjectionModule(module.Id, module.Workflows,
            Array.AsReadOnly(module.Commands.Append(new ToolProjectionHandler(this)).ToArray()));
    }

    private sealed record ToolProjectionModule(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;

    private sealed class ToolProjectionHandler(WorkerWorkflows owner) : IWorkflowCommandHandler
    {
        // The ordinary runtime owns dispatch/accounting and the remaining active-time ceiling.
        // The specific admitted provider's existing timeout is applied inside this outer ceiling.
        public WorkflowCommandDescriptor Descriptor { get; } = new(
            WorkerToolContextProjectionProtocol.CommandId, WorkerToolContextProjectionProtocol.CommandVersion,
            "Project one exact committed same-run tool receipt through an admitted pure context facet.",
            Schema("""
                {"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "contributionId":{"type":"string"},"toolEffectId":{"type":"string"},
                "previousProjectionEffectId":{"oneOf":[{"type":"string"},{"type":"null"}]},
                "slot":{"type":"object"},"priorState":{},"contextRevision":{"type":"integer","minimum":0},
                "exclusionRevision":{"type":"integer","minimum":0}},
                "required":["schemaVersion","contributionId","toolEffectId","previousProjectionEffectId",
                "slot","priorState","contextRevision","exclusionRevision"],"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "link":{"type":"object"},"disposition":{"type":"string","enum":["stateOnly","publish","blocked"]},
                "nextState":{},"slotUpdate":{"oneOf":[{"type":"object"},{"type":"null"}]},
                "providerResult":{"oneOf":[{"type":"object"},{"type":"null"}]},
                "reason":{"oneOf":[{"type":"string"},{"type":"null"}]}},
                "required":["schemaVersion","link","disposition","nextState","slotUpdate","providerResult","reason"],
                "additionalProperties":false}
                """), [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 3600);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.ProjectToolContextAsync(context, false, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.ProjectToolContextAsync(context, true, token);

        private static JsonElement Schema(string value)
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
    }

    private async Task<WorkflowCommandResult> ProjectToolContextAsync(
        WorkflowCommandContext context, bool reconcile, CancellationToken token)
    {
        var entered = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var accepted = ExactAcceptedContext(context.Submission);
            var command = WorkerToolContextProjectionProtocol.ReadCommandRequest(context.Arguments);
            var saved = RequireToolProjectionIntent(context, accepted, reconcile);
            var admissions = HistoricalContextAdmissions(saved);
            RequireToolProjectionLedger(saved, command, admissions, token);
            var admission = admissions.Single(item => item.Descriptor.Id == command.ContributionId);
            var intent = WorkerToolContextProjectionEvidence.FindIntent(Journal, saved, context.EffectId,
                Journal.GetRevision(accepted.ConversationId), token);
            var origin = WorkerToolContextProjectionEvidence.ReadOrigin(Journal, Store, saved, intent, admission, token);
            if (!JsonElement.DeepEquals(CerebralJson.Element(origin.Command), context.Arguments))
                throw InvalidToolProjection();
            var tool = Read<WorkerWorkflowEffects.ToolArguments>(origin.Source.Arguments);
            if (saved.State.GetProperty("pendingToolContextProjection").GetProperty("toolName").GetString() != tool.Name)
                throw InvalidToolProjection();
            var providerCommand = Registry.GetCommand(admission.Descriptor.Provider.Id,
                admission.Descriptor.Provider.Version).Descriptor;
            if (!HasCapabilities(accepted, providerCommand.RequiredCapabilities)) throw InvalidToolProjection();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(providerCommand.TimeoutSeconds));
            var selected = contextContributions.GetToolProjectionContext(accepted, command.ContributionId, tool.Name,
                command.ContextRevision, command.ExclusionRevision, bounded.Token);
            var request = WorkerToolContextProjectionProtocol.FreezeRequest(new(
                WorkflowToolContextProjectionContract.SchemaVersion, selected.Facts, admission.Selection,
                origin.Link, tool.Name, origin.Source, command.PriorState, origin.PublicationRequest));
            entered = true;
            // Await actual settlement. A timeout never releases capacity while package code runs.
            var raw = await contextContributions.InvokeToolProjectionAsync(selected, request, bounded.Token)
                .ConfigureAwait(false);
            bounded.Token.ThrowIfCancellationRequested();
            var current = RequireToolProjectionIntent(context, accepted, reconcile);
            RequireToolProjectionLedger(current, command, admissions, bounded.Token);
            if (!JsonElement.DeepEquals(saved.State, current.State)) throw InvalidToolProjection();
            var result = WorkerToolContextProjectionProtocol.MakeResult(admission.Descriptor.Provider,
                request, raw, WorkerToolContextProjectionProtocol.ReceiptId(context.EffectId));
            bounded.Token.ThrowIfCancellationRequested();
            var output = new WorkflowCommandResult(result.Disposition == WorkflowToolContextProjectionDisposition.Blocked
                    ? WorkflowEffectState.Failed : WorkflowEffectState.Succeeded,
                JsonSerializer.SerializeToElement(result, ToolProjectionWireJson), result.Reason,
                WorkerToolContextProjectionProtocol.ReceiptId(context.EffectId));
            if (JsonSerializer.SerializeToUtf8Bytes(output, ToolProjectionWireJson).Length >
                WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes) throw InvalidToolProjection();
            return output;
        }
        catch (OperationCanceledException)
        {
            return new(entered || reconcile ? WorkflowEffectState.Unknown : WorkflowEffectState.Cancelled,
                Error: "Tool context projection was cancelled; no new provider state or source was adopted.");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or OverflowException or FormatException)
        {
            return new(entered || reconcile ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed,
                Error: "The exact tool context projection could not be admitted; prior state and sources remain authoritative.");
        }
    }

    private WorkflowRun RequireToolProjectionIntent(WorkflowCommandContext context, WorkflowSubmission accepted, bool reconcile)
    {
        if (context.RunId != accepted.RequestId) throw InvalidToolProjection();
        var saved = Store.Get(accepted.RequestId);
        if (!WorkerToolContextProjectionAdmission.SupportsConsumer(saved.Submission,
                WorkerExecutionSnapshot.Read(saved.Submission.SettingsSnapshot)) ||
            saved.Effect is not { } effect || effect.Id != context.EffectId ||
            effect.CommandId != WorkerToolContextProjectionProtocol.CommandId ||
            effect.CommandVersion != WorkerToolContextProjectionProtocol.CommandVersion ||
            effect.State != (reconcile ? WorkflowEffectState.Unknown : WorkflowEffectState.Dispatched) ||
            !JsonElement.DeepEquals(effect.Arguments, context.Arguments) ||
            !JsonElement.DeepEquals(CerebralJson.Element(saved.Submission), CerebralJson.Element(accepted)))
            throw InvalidToolProjection();
        return saved;
    }

    /// <summary>Read only the new edition's closed owned ledger. Other harness fields are not interpreted.
    /// The submitted private state is compared to the durable row, not to its echoed result link.</summary>
    private void RequireToolProjectionLedger(WorkflowRun run, WorkflowToolContextProjectionCommandRequest command,
        IReadOnlyList<WorkerContextContributionAdmission> admissions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = run.State;
        if (state.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(state.GetRawText()) > WorkflowContract.MaximumStateBytes ||
            state.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() !=
                state.EnumerateObject().Count()) throw InvalidToolProjection();
        var ledger = state.GetProperty("toolContextProjections");
        ClosedToolProjection(ledger, "schemaVersion", "runId", "items");
        if (ledger.GetProperty("schemaVersion").GetInt32() != 1 || ledger.GetProperty("runId").GetString() != run.Id)
            throw InvalidToolProjection();
        var declared = admissions.Where(item => item.ProjectionDescriptor is not null).ToArray();
        var rows = ledger.GetProperty("items");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != declared.Length ||
            rows.GetArrayLength() > WorkflowToolContextProjectionContract.MaximumPendingContributions)
            throw InvalidToolProjection();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement target = default;
        foreach (var row in rows.EnumerateArray())
        {
            ClosedToolProjection(row, "id", "status", "reason", "provider", "projectionDescriptor", "providerState",
                "lastProjectionEffectId", "slotSourceRevision", "slotProjectionEffectId", "lastLink");
            var id = row.GetProperty("id").GetString();
            var admission = declared.SingleOrDefault(item => item.Descriptor.Id == id) ?? throw InvalidToolProjection();
            if (id is null || !seen.Add(id) ||
                row.GetProperty("status").GetString() != JsonNamingPolicy.CamelCase.ConvertName(admission.Selection.Disposition.ToString()) ||
                !JsonElement.DeepEquals(row.GetProperty("reason"), CerebralJson.Element(admission.Selection.Reason)) ||
                !JsonElement.DeepEquals(row.GetProperty("provider"), CerebralJson.Element(admission.Descriptor.Provider)) ||
                !JsonElement.DeepEquals(row.GetProperty("projectionDescriptor"), CerebralJson.Element(admission.ProjectionDescriptor)))
                throw InvalidToolProjection();
            if (row.GetProperty("providerState").ValueKind != JsonValueKind.Null &&
                (Encoding.UTF8.GetByteCount(row.GetProperty("providerState").GetRawText()) >
                    WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes ||
                JsonSerializer.SerializeToUtf8Bytes(row.GetProperty("providerState"), ToolProjectionWireJson).Length >
                    WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes)) throw InvalidToolProjection();
            if (id == command.ContributionId) target = row;
        }
        if (target.ValueKind != JsonValueKind.Object ||
            NullableText(target.GetProperty("lastProjectionEffectId")) != command.PreviousProjectionEffectId ||
            NullableRevision(target.GetProperty("slotSourceRevision")) != command.Slot.CurrentSourceRevision ||
            !WorkerToolContextProjectionEvidence.SameState(NullableJson(target.GetProperty("providerState")), command.PriorState))
            throw InvalidToolProjection();
        if (command.PreviousProjectionEffectId is null)
        {
            if (target.GetProperty("lastLink").ValueKind != JsonValueKind.Null ||
                target.GetProperty("slotProjectionEffectId").ValueKind != JsonValueKind.Null ||
                command.Slot.CurrentSourceRevision is not null || command.PriorState is not null) throw InvalidToolProjection();
        }
        else
        {
            var previous = Store.GetEffectResult(run.Id, command.PreviousProjectionEffectId);
            if (previous.State != WorkflowEffectState.Succeeded || previous.Data is null ||
                !JsonElement.DeepEquals(target.GetProperty("lastLink"),
                    CerebralJson.Element(WorkerToolContextProjectionProtocol.ReadResult(previous.Data.Value).Link)))
                throw InvalidToolProjection();
        }
        var pending = state.GetProperty("pendingToolContextProjection");
        ClosedToolProjection(pending, "toolEffectId", "toolName", "contributionIds", "index", "commandRequest", "continuation");
        var toolName = pending.GetProperty("toolName").GetString();
        var queue = pending.GetProperty("contributionIds");
        var index = pending.GetProperty("index").GetInt32();
        var expected = declared.Where(item => item.Selection.Disposition == WorkflowContextDisposition.Ready &&
            item.ProjectionDescriptor!.ToolNames.Contains(toolName!, StringComparer.Ordinal)).Select(item => item.Descriptor.Id).ToArray();
        if (queue.ValueKind != JsonValueKind.Array || queue.GetArrayLength() is < 1 or > 4 ||
            index < 0 || index >= queue.GetArrayLength() ||
            !queue.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected) ||
            queue[index].GetString() != command.ContributionId ||
            pending.GetProperty("toolEffectId").GetString() != command.ToolEffectId ||
            !JsonElement.DeepEquals(pending.GetProperty("commandRequest"), CerebralJson.Element(command)) ||
            state.GetProperty("contextRevision").GetInt64() != command.ContextRevision ||
            state.GetProperty("exclusionRevision").GetInt64() != command.ExclusionRevision)
            throw InvalidToolProjection();

        // Prove the active slot against its actual publishing host receipt, not an owner/category search.
        var admissionTarget = declared.Single(item => item.Descriptor.Id == command.ContributionId);
        var publicationId = NullableText(target.GetProperty("slotProjectionEffectId"));
        var sources = state.GetProperty("contributions");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > ContextValidation.MaximumSourceCount)
            throw InvalidToolProjection();
        if (state.TryGetProperty("checkpoint", out var checkpoint) && checkpoint.ValueKind != JsonValueKind.Null)
        {
            if (checkpoint.ValueKind != JsonValueKind.Object ||
                !checkpoint.TryGetProperty("id", out var checkpointId) || checkpointId.ValueKind != JsonValueKind.String ||
                checkpointId.GetString() == command.Slot.Id ||
                !checkpoint.TryGetProperty("coveredSources", out var covered) || covered.ValueKind != JsonValueKind.Array ||
                covered.EnumerateArray().Any(item => item.GetProperty("id").GetString() == command.Slot.Id))
                throw InvalidToolProjection();
        }
        var matches = sources.EnumerateArray().Where(item => item.GetProperty("id").GetString() == command.Slot.Id).ToArray();
        if (command.Slot.CurrentSourceRevision is null)
        {
            if (publicationId is not null || matches.Length != 0) throw InvalidToolProjection();
        }
        else
        {
            if (publicationId is null || matches.Length != 1) throw InvalidToolProjection();
            var savedPublication = Store.GetEffectResult(run.Id, publicationId);
            if (savedPublication.State != WorkflowEffectState.Succeeded || savedPublication.Data is null ||
                savedPublication.ProviderReceiptId is null) throw InvalidToolProjection();
            var publicationIntent = WorkerToolContextProjectionEvidence.FindIntent(Journal, run, publicationId,
                Journal.GetRevision(run.Submission.ConversationId), token);
            var origin = WorkerToolContextProjectionEvidence.ReadOrigin(Journal, Store, run, publicationIntent,
                admissionTarget, token);
            var verified = WorkerToolContextProjectionProtocol.ValidateResult(admissionTarget.Descriptor.Provider,
                origin.PublicationRequest, origin.Link,
                WorkerToolContextProjectionProtocol.ReadResult(savedPublication.Data.Value), savedPublication.ProviderReceiptId);
            if (verified.Disposition != WorkflowToolContextProjectionDisposition.Publish ||
                verified.SlotUpdate!.SourceRevision != command.Slot.CurrentSourceRevision ||
                !JsonElement.DeepEquals(matches[0], CerebralJson.Element(verified.ProviderResult!.Window.Contributions.Single())))
                throw InvalidToolProjection();
        }
    }

    private static void ClosedToolProjection(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidToolProjection();
        var allowed = fields.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw InvalidToolProjection();
        if (seen.Count != fields.Length) throw InvalidToolProjection();
    }

    private static string? NullableText(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } text
            ? text : throw InvalidToolProjection();
    private static long? NullableRevision(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) && revision > 0
            ? revision : throw InvalidToolProjection();
    private static JsonElement? NullableJson(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.Clone();
    private static InvalidDataException InvalidToolProjection() => new("The saved tool projection intent or owned ledger is invalid.");
}
