using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    public const string HistoryCommand = ModuleId + ":history";
    public const string CatalogCommand = ModuleId + ":catalog";
    public const int MaximumConversationEffectBytes = 48 * 1024;
    public sealed record HistoryArguments(long? BeforeSequence = null, int? Limit = null, string? EventId = null, int Offset = 0);
    internal const int MaximumContextContributionProjectionBytes = 16 * 1024;
    public sealed record ContextContributionData(string Id, string Status, ContextProviderRequest? Request);
    public sealed record ContextContributionsData(int SchemaVersion, IReadOnlyList<ContextContributionData> Items);
    public sealed record ReasoningAdjustmentData(int SchemaVersion, string RunId, string ProfileId,
        int ProfileRevision, int MinimumTokens, int MaximumTokens, int DefaultTokens);
    public sealed record HistoryData(ContextWindow Context, bool HasOlder, long? BeforeSequence, long SnapshotSequence)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MainHostActionsData? MainHostActions { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ContextContributionsData? ContextContributions { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ReceiptProjectionAdmissions { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ReasoningAdjustmentData? ReasoningAdjustment { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TaskBudgetData? TaskBudget { get; init; }
    }
    /// <summary>ExactReadHandle is the event or checkpoint ID. JSON is an immutable source fragment with UTF-8 byte
    /// offsets, not standalone JSON until all fragments are assembled. A legacy retained-checkpoint
    /// fragment is explicitly tagged, has Sequence=0 (no original append sequence), and carries its
    /// coverage cutoff inside JSON. It never loads attachment bytes.</summary>
    public sealed record HistorySourceData(string EventId, long Sequence, long SnapshotSequence, int Offset,
        int Limit, int TotalBytes, string Json, int? NextOffset, bool Truncated)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? CheckpointCoverageThroughSequence { get; init; }
    }
    public sealed record CatalogArguments(IReadOnlyList<string>? Names = null);
    public sealed record CatalogEntry(string Name, string Description, string Capability,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? InputSchema = null)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? PrerequisiteTools { get; init; }
    }
    public sealed record CatalogData(IReadOnlyList<CatalogEntry> Tools, bool Truncated)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<HelperEntry>? Helpers { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PluginSourceCoordinate? PluginSource { get; init; }
    }
    private static readonly JsonSerializerOptions ConversationJson = new(CerebralJson.Options)
        { WriteIndented = false, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    internal static void ValidateContextContributionView(ContextContributionsData data)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(data, ConversationJson).Length > MaximumContextContributionProjectionBytes)
            throw new InvalidDataException("Context contribution execution view exceeds its bound.");
    }

    private sealed class HistoryHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(HistoryCommand, 1,
            "Read bounded earlier conversation context, including model-visible hidden activity. Continue with beforeSequence. Read one exact source or earlier-turn checkpoint using its eventId and UTF-8 offset; no other conversation or current-turn event is accessible. A legacy fallback is explicitly tagged and uses sequence zero.",
            Schema("""
                {"type":"object","properties":{"beforeSequence":{"oneOf":[{"type":"integer","minimum":1},{"type":"null"}]},
                "limit":{"oneOf":[{"type":"integer","minimum":1,"maximum":48},{"type":"null"}]},
                "eventId":{"oneOf":[{"type":"string","minLength":1,"maxLength":128},{"type":"null"}]},
                "offset":{"type":"integer","minimum":0}},"additionalProperties":false}
                """),
            Schema("""
                {"oneOf":[{"type":"object","properties":{"context":{"type":"object"},"hasOlder":{"type":"boolean"},
                "beforeSequence":{"oneOf":[{"type":"integer"},{"type":"null"}]},"snapshotSequence":{"type":"integer"},
                "mainHostActions":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "sourceRunId":{"type":"string","minLength":1,"maxLength":128},"coordinate":{"type":"boolean"},
                "relayOwnerAnswer":{"type":"boolean"}},"required":["schemaVersion","sourceRunId","coordinate","relayOwnerAnswer"],"additionalProperties":false},
                "contextContributions":{"oneOf":[{"type":"null"},{"type":"object","properties":{
                "schemaVersion":{"type":"integer","enum":[1]},"items":{"type":"array","maxItems":4,"items":{"type":"object","properties":{
                "id":{"type":"string"},"status":{"type":"string","enum":["ready","omitted","unavailable","blocked","unsupported"]},
                "request":{"oneOf":[{"type":"null"},{"type":"object"}]}},"required":["id","status","request"],"additionalProperties":false}}},
                "required":["schemaVersion","items"],"additionalProperties":false}]},
                "receiptProjectionAdmissions":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "items":{"type":"array","maxItems":4,"items":{"type":"object","properties":{"id":{"type":"string"},
                "status":{"type":"string","enum":["ready","omitted","unavailable","blocked","unsupported"]},
                "reason":{"oneOf":[{"type":"string","maxLength":1024},{"type":"null"}]},
                "provider":{"type":"object"},"projectionDescriptor":{"type":"object"}},
                "required":["id","status","reason","provider","projectionDescriptor"],"additionalProperties":false}}},
                "required":["schemaVersion","items"],"additionalProperties":false},
                "reasoningAdjustment":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "runId":{"type":"string","minLength":1},"profileId":{"type":"string","minLength":1},
                "profileRevision":{"type":"integer","minimum":0},"minimumTokens":{"type":"integer","enum":[0,128]},
                "maximumTokens":{"type":"integer","minimum":128,"maximum":32768},
                "defaultTokens":{"type":"integer","minimum":0,"maximum":32768}},
                "required":["schemaVersion","runId","profileId","profileRevision","minimumTokens","maximumTokens","defaultTokens"],"additionalProperties":false},
                "taskBudget":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "runId":{"type":"string","minLength":1,"maxLength":128},"profileId":{"type":"string","minLength":1,"maxLength":128},
                "profileRevision":{"type":"integer","minimum":0},"origin":{"type":"string","enum":["owner","coordinator","worker"]},
                "maxContextTokens":{"type":"integer","minimum":512,"maximum":1048576},
                "maxResponseTokens":{"type":"integer","minimum":128,"maximum":131072},
                "currentContextTokens":{"type":"integer","minimum":512,"maximum":1048576},
                "defaultContextTokens":{"type":"integer","minimum":512,"maximum":1048576},
                "defaultResponseTokens":{"type":"integer","minimum":128,"maximum":131072},
                "contextSelectable":{"type":"boolean"},"responseSelectable":{"type":"boolean"},"allowSelection":{"type":"boolean"}},
                "required":["schemaVersion","runId","profileId","profileRevision","origin","maxContextTokens","maxResponseTokens",
                "currentContextTokens","defaultContextTokens","defaultResponseTokens","contextSelectable","responseSelectable","allowSelection"],"additionalProperties":false}},
                "required":["context","hasOlder","beforeSequence","snapshotSequence"],"additionalProperties":false},
                {"type":"object","properties":{"eventId":{"type":"string"},"sequence":{"type":"integer"},"snapshotSequence":{"type":"integer"},
                "offset":{"type":"integer"},"limit":{"type":"integer"},"totalBytes":{"type":"integer"},"json":{"type":"string"},
                "nextOffset":{"oneOf":[{"type":"integer"},{"type":"null"}]},"truncated":{"type":"boolean"},
                "checkpointCoverageThroughSequence":{"oneOf":[{"type":"integer","minimum":0},{"type":"null"}]}},
                "required":["eventId","sequence","snapshotSequence","offset","limit","totalBytes","json","nextOffset","truncated"],"additionalProperties":false}]}
                """), [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 15);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.ReadHistoryAsync(context, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            ExecuteAsync(context, token);
    }

    private sealed class CatalogHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(CatalogCommand, 1,
            "Discover up to 64 currently permitted accepted tools without schemas. Supply 1–8 exact names for their frozen input schemas. Discovery grants no execution authority.",
            Schema("""
                {"type":"object","properties":{"names":{"oneOf":[{"type":"array","minItems":1,"maxItems":8,
                "items":{"type":"string","minLength":1,"maxLength":256}},{"type":"null"}]}},"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{"tools":{"type":"array","maxItems":64,"items":{"type":"object",
                "properties":{"name":{"type":"string"},"description":{"type":"string"},"capability":{"type":"string"},"inputSchema":{},
                "prerequisiteTools":{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":256}}},
                "required":["name","description","capability"],"additionalProperties":false}},"truncated":{"type":"boolean"},
                "helpers":{"type":"array","maxItems":16,"items":{"type":"object"}},
                "pluginSource":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},"presentationId":{"type":"string","minLength":32,"maxLength":32},
                "root":{"type":"string","minLength":1,"maxLength":2048}},"required":["schemaVersion","presentationId","root"],"additionalProperties":false}},
                "required":["tools","truncated"],"additionalProperties":false}
                """), [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 30);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.ReadCatalogAsync(context, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            ExecuteAsync(context, token);
    }

    private (EventJournal Journal, WorkflowSubmission Submission) ConversationOwner(WorkflowCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(context.RunId))
            throw new UnauthorizedAccessException("Conversation effects require a host-bound workflow run.");
        var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
        var saved = new WorkflowStore(journal).Get(context.RunId).Submission;
        if (saved.RequestId != context.Submission.RequestId || saved.ConversationId != context.Submission.ConversationId ||
            saved.ProfileId != context.Submission.ProfileId)
            throw new UnauthorizedAccessException("Conversation effect does not match the accepted workflow owner.");
        // Use the stored admission, including the transaction-owned history boundary, never a supplied
        // settings replacement. Revoked profiles or changed execution routing also fail closed.
        Resolve(saved);
        return (journal, saved);
    }

    private async Task<WorkflowCommandResult> ReadHistoryAsync(WorkflowCommandContext context, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var input = Read<HistoryArguments>(context.Arguments);
            if (input.BeforeSequence is < 1 || input.Limit is < 1 or > 48 || input.Offset < 0)
                throw new ArgumentException("History requires a positive cursor, limit 1–48 and a nonnegative offset.");
            var (journal, accepted) = ConversationOwner(context);
            var boundary = accepted.ConversationSequence;
            var goal = PluginContinuationGoal.Read(store, journal, accepted);
            if (input.EventId is { } eventId)
            {
                if (input.BeforeSequence is not null || input.Limit is not null)
                    throw new ArgumentException("Exact source reads use eventId and offset only.");
                byte[] bytes;
                long sequence;
                long? checkpointCoverage = null;
                try
                {
                    var item = journal.ReadExact(accepted.ConversationId, eventId, boundary, token);
                    if (!EventProjection.IncludeInModel(item, includeOnDemand: true))
                        throw new KeyNotFoundException("Conversation source is unavailable in this accepted snapshot.");
                    bytes = JsonSerializer.SerializeToUtf8Bytes(WorkerContextContributionProjection.ProjectEvent(journal, item, token,
                        workflowHost is null ? null : run => workflowHost().HistoricalContextAdmissions(run)), ConversationJson);
                    sequence = item.Sequence;
                }
                catch (KeyNotFoundException)
                {
                    // The store verifies true absence of any adoption event, not just absence before
                    // this snapshot. It cannot expose a future, excluded, or foreign event as legacy.
                    var legacy = new WorkflowStore(journal).GetLegacyCheckpointSource(accepted.ConversationId, eventId, boundary)
                        ?? throw new KeyNotFoundException("Conversation source is unavailable in this accepted snapshot.");
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { kind = "retained-checkpoint-fallback",
                        coverageThroughSequence = legacy.SourceRevision, checkpoint = legacy.Checkpoint }, ConversationJson);
                    sequence = 0;
                    checkpointCoverage = legacy.SourceRevision;
                }
                if (input.Offset > bytes.Length || input.Offset < bytes.Length && Continuation(bytes[input.Offset]))
                    throw new ArgumentException("History offset must be a UTF-8 character boundary within the exact source.");
                var end = (int)Math.Min(bytes.Length, (long)input.Offset + 4096);
                while (end < bytes.Length && Continuation(bytes[end])) end--;
                var more = end < bytes.Length;
                return new(WorkflowEffectState.Succeeded, Element(new HistorySourceData(eventId, sequence, boundary,
                    input.Offset, 4096, bytes.Length, Encoding.UTF8.GetString(bytes, input.Offset, end - input.Offset), more ? end : null, more)
                    { CheckpointCoverageThroughSequence = checkpointCoverage }));
            }
            if (input.Offset != 0) throw new ArgumentException("An offset requires an exact eventId.");
            ReasoningAdjustmentData? reasoningAdjustment = null;
            TaskBudgetData? taskBudget = null;
            if (input.BeforeSequence is null)
            {
                if (context.RunId is { } runId) taskBudget = DescribeTaskBudget(accepted, runId);
                var frozen = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot).Profile;
                if (frozen.AllowReasoningAdjustment)
                {
                    var ownerMaximum = frozen.ReasoningTokenCeiling == 0
                        ? frozen.ReasoningTokens : frozen.ReasoningTokenCeiling;
                    var contextMaximum = frozen.ContextTokens - AgentTokenLimits.MinimumResponseTokens -
                        AgentTokenLimits.ContextSafetyTokens - AgentTokenLimits.MinimumPromptTokens - 1;
                    var maximum = Math.Min(ownerMaximum, contextMaximum);
                    var defaultTokens = frozen.ReasoningMode == "on" ? frozen.ReasoningTokens : 0;
                    if (maximum < AgentTokenLimits.MinimumReasoningTokens || defaultTokens > maximum ||
                        context.RunId is null)
                        throw new AdmissionException("The accepted reasoning policy cannot fit a reliable visible response.");
                    reasoningAdjustment = new(1, context.RunId, frozen.Id, frozen.Revision,
                        frozen.ReasoningMode == "on" ? AgentTokenLimits.MinimumReasoningTokens : 0,
                        maximum, defaultTokens);
                }
            }
            var retained = new WorkflowStore(journal).GetConversationCheckpoint(accepted.ConversationId, boundary);
            var retainedCheckpoint = retained?.Checkpoint;
            var checkpointRevision = retained?.SourceRevision ?? 0;
            // Owner-chat criteria do not belong to delegated or service-owned contexts.
            // Those contexts retain their own accepted assignment and checkpoint instead.
            var ownerConversation = accepted.ParentRunId is null &&
                (accepted.Caller is null || accepted.Caller.Kind == EventActorKind.User);
            var protectOriginal = ownerConversation && (retainedCheckpoint is not null ||
                accepted.DefinitionId == ConversationWorkflowModule.DefinitionId);
            var primary = protectOriginal
                ? new WorkflowStore(journal).ReadConversationGoal(accepted.DefinitionId, accepted.ConversationId).Original
                : null;
            // An authenticated automatic QA stage is service-owned, but its original owner
            // conversation still has the same immutable initial criteria as an ordinary turn.
            // Keep that exact first source only when it precedes the separately bound review goal.
            if (goal is not null)
            {
                var initial = new WorkflowStore(journal).ReadConversationGoal(accepted.DefinitionId, accepted.ConversationId).Original;
                primary = initial.EventId != goal.Message.Id && initial.Sequence < goal.Message.Sequence ? initial : null;
            }
            var page = journal.ReadPreviousPage(accepted.ConversationId, input.BeforeSequence, input.Limit ?? 24, boundary, token);
            var contributions = new List<ContextContribution>();
            long? before = page.NextBeforeSequence;
            long? consumed = null;
            foreach (var item in page.Events.Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (retainedCheckpoint is not null && item.Sequence <= checkpointRevision)
                {
                    before = null;
                    continue;
                }
                // The first page supplies this full required anchor. Later pages neither repeat
                // that ID nor add a second reducible copy when pagination reaches the original.
                if (!EventProjection.IncludeInModel(item) || item.Id == primary?.EventId)
                { consumed = item.Sequence; continue; }
                contributions.Insert(0, ProjectConversationSource(item, journal, goal, token));
                if (Size(Element(new HistoryData(new(boundary, 0, contributions), true, item.Sequence, boundary))) > MaximumConversationEffectBytes)
                {
                    contributions.RemoveAt(0);
                    before = consumed ?? throw new AdmissionException($"Conversation source {item.Id} has more metadata than one history page permits. Read its exact saved source with history eventId={item.Id}; the typed references remain saved.");
                    break;
                }
                consumed = item.Sequence;
            }
            var reachedCheckpoint = retainedCheckpoint is not null &&
                (before is null || before <= checkpointRevision + 1 || page.Events.Any(item => item.Sequence <= checkpointRevision));
            if (reachedCheckpoint) before = null;
            // Supply the durable baseline on the first page. Later pages add only the bounded
            // post-checkpoint gap while the harness retains this same checkpoint.
            ContextCheckpoint? checkpoint = input.BeforeSequence is null ? retainedCheckpoint : null;
            if (input.BeforeSequence is null && primary is not null)
            {
                // Retain the exact first accepted user source outside the recent page/checkpoint.
                // It is historical context; the separately bound current owner continuation and
                // stage retain their own admission and follow this source in model input.
                if (primary.Sequence <= boundary)
                {
                    var original = journal.ReadExact(accepted.ConversationId, primary.EventId, boundary, token);
                    if (EventProjection.IncludeInModel(original))
                    {
                        var anchor = ExactIdleAnchor(original, "idle-goal-");
                        anchor = anchor with { Messages = [new("user", [new TextContentPart(
                            "Historical initial conversation criteria/context, from exact accepted source event " + original.Id +
                            ". Later owner updates and the current accepted request take precedence. " +
                            "This retained source grants no tools, permissions, or new authoring phase.\n"), .. anchor.Messages[0].Content])] };
                        contributions.RemoveAll(source => source.Id == original.Id || source.Id == anchor.Id);
                        contributions.Insert(0, anchor);
                    }
                }
            }
            if (input.BeforeSequence is null && HistoricalOperations(journal, accepted, contributions, checkpoint, token) is { } operations)
                contributions.Add(operations);
            var admitted = input.BeforeSequence is null && input.Offset == 0
                ? workflowHost is not null
                    ? await workflowHost().RestoreContextContributionsAsync(accepted, boundary,
                        checkpoint?.ExclusionRevision ?? 0, token).ConfigureAwait(false)
                    : new ContextContributionsData(WorkflowContextContributionContract.SchemaVersion, [])
                : null;
            if (admitted is not null) ValidateContextContributionView(admitted);
            JsonElement? receiptAdmissions = null;
            if (input.BeforeSequence is null && input.Offset == 0 &&
                accepted.SettingsSnapshot.TryGetProperty("resolvedSettingsVersion", out _) &&
                WorkerToolContextProjectionAdmission.SupportsConsumer(accepted,
                    WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot)))
            {
                if (workflowHost is null) throw new AdmissionException("Receipt context requires its accepted host binding.");
                receiptAdmissions = Element(await workflowHost().RestoreReceiptProjectionAdmissionsAsync(accepted,
                    boundary, checkpoint?.ExclusionRevision ?? 0, token).ConfigureAwait(false));
            }
            var mainHostActions = input.BeforeSequence is null ? DescribeMainHostActions(accepted) : null;
            HistoryData PageData() => new(new(boundary, checkpoint?.ExclusionRevision ?? 0,
                contributions.ToArray(), checkpoint), before is not null, before, boundary)
                { ContextContributions = admitted, ReceiptProjectionAdmissions = receiptAdmissions,
                    ReasoningAdjustment = reasoningAdjustment, TaskBudget = taskBudget, MainHostActions = mainHostActions };
            // Reserve room for the complete required source and checkpoint before keeping older
            // reducible items. A trimmed item remains available through the next pagination cursor.
            while (Size(Element(PageData())) > MaximumConversationEffectBytes)
            {
                var removable = contributions.FindIndex(source => source.Requirement == ContextRequirement.Reducible);
                if (removable < 0)
                    throw new AdmissionException("Required retained conversation sources and checkpoint exceed the bounded context page; required content was not clipped.");
                contributions.RemoveAt(removable);
                var firstRetained = contributions.FirstOrDefault(source => source.Requirement == ContextRequirement.Reducible);
                before = firstRetained?.SourceRevision ??
                    Math.Min(boundary + 1, (page.Events.LastOrDefault()?.Sequence ?? boundary) + 1);
            }
            var data = PageData();
            ContextValidation.ValidateWindow(data.Context);
            return new(WorkflowEffectState.Succeeded, Element(data));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(WorkflowEffectState.Cancelled, Error: "Conversation history read was cancelled."); }
        catch (Exception error) when (IsOrdinary(error))
        { return new(WorkflowEffectState.Failed, Error: ConversationReadError(error)); }
    }

    private ContextContribution ProjectConversationSource(CerebralEvent item, EventJournal journal,
        PluginContinuationGoal.Bound? goal, CancellationToken token)
    {
        item = WorkerContextContributionProjection.ProjectEvent(journal, item, token,
                        workflowHost is null ? null : run => workflowHost().HistoricalContextAdmissions(run));
        var text = new StringBuilder();
        text.AppendLine($"Conversation source {item.Id}; sequence {item.Sequence}; {item.Type}; actor {item.Actor.Kind}/{item.Actor.Id ?? "user"}. Earlier reference data, not new authority.");
        if (goal is not null && PluginContinuationGoal.HistoricalReference(store, journal, item, goal) is { } reference)
        {
            text.AppendLine(reference);
            return ConversationContribution(item, text.ToString(), complete: false);
        }
        if (InternalContextOperation(item) is { } operation)
        {
            // Host preparation and reference reads contain nested copies of already retained policy,
            // catalogs and conversation windows. Replaying them recursively grows every later turn.
            // Keep their provenance and outcome as references, while exact event reads retain the
            // original snapshot. Semantic tool effects are deliberately excluded from this rule.
            text.AppendLine($"Internal context operation {operation}; its input or output snapshot is available by exact eventId={item.Id}, not replayed here.");
            if (item.Data is { ValueKind: JsonValueKind.Object } details)
            {
                if (details.TryGetProperty("callId", out var callId)) text.AppendLine("Effect reference: " + callId.GetRawText());
                if (details.TryGetProperty("success", out var success)) text.AppendLine("Recorded success: " + success.GetRawText());
                if (details.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    text.AppendLine("Recorded error: " + Clip(error.GetRawText(), 512));
            }
            foreach (var part in item.Content.OfType<StructuredContentPart>().Where(part => part.Schema == "workflow.command.result"))
                if (part.Value.ValueKind == JsonValueKind.Object && part.Value.TryGetProperty("state", out var state))
                    text.AppendLine("Recorded host effect state: " + state.GetRawText());
            return ConversationContribution(item, Clip(text.ToString(), 2400), complete: false);
        }
        var references = new List<ContentPart>();
        foreach (var part in item.Content)
        {
            switch (part)
            {
                case TextContentPart value: text.AppendLine(value.Text); break;
                case StructuredContentPart value: text.AppendLine(value.Schema + ": " + value.Value.GetRawText()); break;
                case FileContentPart or ImageContentPart or AudioContentPart or ResourceContentPart:
                    references.Add(part);
                    break;
                default: throw new InvalidDataException("Conversation source contains an unsupported content kind.");
            }
        }
        if (item.Data is { } data) text.AppendLine("Recorded data: " + data.GetRawText());
        var full = text.ToString();
        var excerpt = Clip(full, 2400);
        var complete = excerpt.Length == full.Length;
        if (excerpt.Length != full.Length)
            excerpt += "\n[Excerpt only; read the exact source using history eventId=" + item.Id + ".]";
        // These remain typed coordinates, not a claim that bytes were read. The selected adapter
        // must support the kind and resolve it under the accepted conversation/profile scope.
        return ConversationContribution(item, excerpt, complete, references);
    }

    private static ContextContribution ConversationContribution(CerebralEvent item, string text, bool complete,
        IReadOnlyList<ContentPart>? references = null)
    {
        var role = item.Actor.Kind == EventActorKind.Agent ? "assistant" : "user";
        // A formerly required accepted input is now earlier-turn source material. The exact current
        // accepted input remains a separate required message in BuildGenerationRequest.
        return new(item.Id, ModuleId, "conversation", item.Sequence, ContextRequirement.Reducible,
            [new ModelInputMessage(role, [new TextContentPart(text), .. references ?? []])], item.Id, complete, BeforeCurrentInput: true);
    }

    private static string? InternalContextOperation(CerebralEvent item)
    {
        string? operation = null;
        if (item.Type == CerebralEventTypes.ToolCall && item.Provenance is { Source: EventSourceKind.Service, SourceId: "cerebral.engine" } &&
            item.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("toolName", out var name) && name.ValueKind == JsonValueKind.String)
            operation = name.GetString();
        else if (item.Type == CerebralEventTypes.ToolResult && item.Actor.Kind == EventActorKind.Tool &&
            item.Provenance.Source == EventSourceKind.Tool && item.Provenance.SourceId == item.Actor.Id)
            operation = item.Actor.Id;
        return operation is GenerateCommand or CompactCommand or HistoryCommand or CatalogCommand or ReceiptCommand ? operation : null;
    }

    private async Task<WorkflowCommandResult> ReadCatalogAsync(WorkflowCommandContext context, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var args = Read<CatalogArguments>(context.Arguments);
            if (args.Names is { } names && (names.Count is < 1 or > 8 ||
                names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256) || names.Distinct(StringComparer.Ordinal).Count() != names.Count))
                throw new ArgumentException("Catalog schema lookup requires 1–8 distinct exact names.");
            var (_, accepted) = ConversationOwner(context);
            var (_, effective) = Resolve(accepted);
            var listed = await tools.ListAsync(effective, token);
            token.ThrowIfCancellationRequested();
            var (snapshot, current) = Resolve(accepted);
            var permitted = snapshot.Tools.Where(tool => WorkerExecutionSnapshot.AllowsTool(current, tool.Name) &&
                current.Capabilities.Contains(tool.Capability, StringComparer.Ordinal) &&
                !HostOwnsNativeCortexTool(snapshot, tool.Name) &&
                listed.Any(live => live.Name == tool.Name && WorkerExecutionSnapshot.SameTool(tool, live)))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            if (args.Names is not null && args.Names.Any(name => !permitted.Any(tool => tool.Name == name)))
                throw new AdmissionException("A requested tool is unavailable or outside the accepted and current grants.");
            var selected = args.Names is null ? permitted.Take(64).ToArray() :
                args.Names.Select(name => permitted.Single(tool => tool.Name == name)).ToArray();
            var entries = selected.Select(tool => new CatalogEntry(tool.Name,
                args.Names is null ? Clip(tool.Description, 256) : tool.Description, tool.Capability,
                args.Names is null ? null : tool.InputSchema.Clone())
                {
                    // Related schemas are preparation hints, never grants. Omit unavailable names
                    // and bound this optional catalog metadata independently of input schemas.
                    PrerequisiteTools = tool.PrerequisiteTools.Count == 0 ? null : tool.PrerequisiteTools
                        .Where(name => permitted.Any(candidate => candidate.Name == name))
                        .Distinct(StringComparer.Ordinal).Take(8).ToArray()
                }).ToArray();
            var helpers = args.Names is null ? AvailableHelpers(context.Submission, current) : null;
            var pluginSource = ReadPluginSourceCoordinate(context, accepted, snapshot, current);
            var data = Element(new CatalogData(entries, args.Names is null && permitted.Length > entries.Length)
                { Helpers = helpers, PluginSource = pluginSource });
            while (args.Names is null && Size(data) > MaximumConversationEffectBytes && entries.Length > 0)
            {
                entries = entries[..^1];
                data = Element(new CatalogData(entries, true) { Helpers = helpers, PluginSource = pluginSource });
            }
            if (Size(data) > MaximumConversationEffectBytes)
                throw new AdmissionException("Selected tool schemas exceed the bounded catalog response. Request fewer exact names.");
            return new(WorkflowEffectState.Succeeded, data);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(WorkflowEffectState.Cancelled, Error: "Tool catalog lookup was cancelled."); }
        catch (Exception error) when (IsOrdinary(error))
        { return new(WorkflowEffectState.Failed, Error: ConversationReadError(error)); }
    }

    private static string Clip(string text, int maximum)
    {
        var length = Math.Min(text.Length, maximum);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    private static bool Continuation(byte value) => (value & 0xc0) == 0x80;
    private static string ConversationReadError(Exception error) => error switch
    {
        UnauthorizedAccessException => "Conversation effects are restricted to the host-bound accepted workflow and its current grants.",
        KeyNotFoundException => "Conversation source is unavailable in this accepted snapshot.",
        AdmissionException => error.Message,
        ArgumentException => error.Message,
        JsonException => "Conversation effect arguments are invalid.",
        _ => "Conversation context or tool discovery could not be read within its accepted binding."
    };
}
