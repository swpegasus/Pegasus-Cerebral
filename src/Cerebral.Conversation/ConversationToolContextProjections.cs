using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string ToolContextProject = "cerebral.host:tool-context-project";
    private const int MaximumToolContextControlBytes = 16 * 1024;
    private sealed record ToolContextAdmissions(int SchemaVersion, IReadOnlyList<ToolContextAdmission> Items);
    private sealed record ToolContextAdmission(string Id, string Status, string? Reason,
        ContextProviderDescriptor Provider, WorkflowToolContextProjectionDescriptor ProjectionDescriptor);
    private sealed record ToolContextProjectionState(int SchemaVersion, string RunId, IReadOnlyList<ToolContextItem> Items);
    private sealed record ToolContextItem(string Id, string Status, string? Reason,
        ContextProviderDescriptor Provider, WorkflowToolContextProjectionDescriptor ProjectionDescriptor,
        JsonElement? ProviderState = null, string? LastProjectionEffectId = null, long? SlotSourceRevision = null,
        string? SlotProjectionEffectId = null, WorkflowToolContextProjectionLink? LastLink = null);
    private sealed record PendingToolContextProjection(string ToolEffectId, string ToolName,
        IReadOnlyList<string> ContributionIds, int Index, WorkflowToolContextProjectionCommandRequest CommandRequest,
        ToolContextContinuation Continuation);
    private sealed record ToolContextContinuation(string Kind, string? CommandId = null, int CommandVersion = 0,
        JsonElement? Arguments = null, string? Summary = null, bool Success = false, string? Error = null,
        WorkflowAction? Action = null);

    private static HarnessState ReadToolContextAdmissions(JsonElement result, HarnessState state)
    {
        var fields = result.EnumerateObject().Where(p => p.Name.Equals("receiptProjectionAdmissions", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != "receiptProjectionAdmissions")
            throw new ArgumentException("Invalid receipt projection admission field.");
        if (fields.Length == 0)
            return state.ToolContextProjections is null ? state :
                throw new ArgumentException("The receipt-aware edition requires explicit receipt projection admission.");
        var raw = fields[0].Value;
        ToolContextObject(raw, ["schemaVersion", "items"]);
        if (raw.GetProperty("schemaVersion").GetInt32() != 1 || raw.GetProperty("items").ValueKind != JsonValueKind.Array ||
            ContextContributionUtf8.GetByteCount(raw.GetRawText()) > MaximumContextAdmissionBytes)
            throw new ArgumentException("Invalid receipt projection admission.");
        foreach (var item in raw.GetProperty("items").EnumerateArray()) ValidateToolContextAdmissionJson(item);
        var admission = raw.Deserialize<ToolContextAdmissions>(ContextContributionJson) ?? throw new ArgumentException();
        if (state.ToolContextProjections is not { } collection)
            return admission.Items.All(i => i.Status == "omitted") ? state : throw new ArgumentException("This edition does not support active receipt projection contributions.");
        var items = admission.Items.Select(a => new ToolContextItem(a.Id, a.Status, a.Reason, a.Provider, a.ProjectionDescriptor)).ToArray();
        var candidate = state with { ToolContextProjections = collection with { Items = Array.AsReadOnly(items) } };
        ValidateToolContextAdmission(candidate);
        items = items.Select(item => item with
        {
            Provider = FreezeToolContextProvider(item.Provider),
            ProjectionDescriptor = item.ProjectionDescriptor with
            {
                ToolNames = Array.AsReadOnly(item.ProjectionDescriptor.ToolNames.ToArray()),
                PublicationTemplate = FreezeContextContributionRequest(item.ProjectionDescriptor.PublicationTemplate)
            }
        }).ToArray();
        candidate = candidate with { ToolContextProjections = collection with { Items = Array.AsReadOnly(items) } };
        if (state.HistoryLoaded)
        {
            if (!JsonElement.DeepEquals(ToolContextAdmissionView(state), ToolContextAdmissionView(candidate)))
                throw new ArgumentException("Historical pagination changed receipt projection admission.");
            return state;
        }
        if (collection.Items.Count != 0) throw new ArgumentException("Receipt projection admission was already selected.");
        return candidate;
    }

    private static ContextProviderDescriptor FreezeToolContextProvider(ContextProviderDescriptor p) =>
        p with { SupportedReferenceKinds = Array.AsReadOnly(p.SupportedReferenceKinds.ToArray()) };

    private static JsonElement ToolContextAdmissionView(HarnessState state) => Element(new
    {
        schemaVersion = 1,
        items = state.ToolContextProjections!.Items.Select(i => new { i.Id, i.Status, i.Reason, i.Provider, i.ProjectionDescriptor })
    });

    private static void ValidateToolContextAdmissionJson(JsonElement value)
    {
        ToolContextObject(value, ["id", "status", "reason", "provider", "projectionDescriptor"]);
        ToolContextObject(value.GetProperty("provider"), ["id", "version", "description", "supportedReferenceKinds", "maxReferences", "maxUtf8Bytes"]);
        var descriptor = value.GetProperty("projectionDescriptor");
        ToolContextObject(descriptor, ["schemaVersion", "toolNames", "publicationTemplate"]);
        ValidateContextContributionRequestJson(descriptor.GetProperty("publicationTemplate"));
    }

    private static void ValidateToolContextAdmission(HarnessState state)
    {
        var collection = state.ToolContextProjections ?? throw new ArgumentException();
        if (collection.SchemaVersion != 1 || collection.Items is null || collection.Items.Count > WorkflowToolContextProjectionContract.MaximumPendingContributions ||
            collection.Items.Count + (state.ContextContributions?.Items.Count ?? 0) > WorkflowContextContributionContract.MaximumContributions)
            throw new ArgumentException("The receipt projection collection exceeds its admitted bound.");
        var ids = (state.ContextContributions?.Items.Select(i => i.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        var sourceIds = (state.ContextContributions?.Items.SelectMany(i => i.Request?.References.Select(r => r.Id) ?? []) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in collection.Items)
        {
            if (item is null) throw new ArgumentException();
            ValidateContextContributionToken(item.Id, 256);
            if (!ids.Add(item.Id) || item.Status is not ("ready" or "omitted" or "unavailable" or "blocked" or "unsupported") ||
                item.Reason is { Length: > WorkflowToolContextProjectionContract.MaximumReasonCharacters } ||
                item.Reason?.Contains('\0') == true || item.Status == "ready" && item.Reason is not null)
                throw new ArgumentException("Invalid receipt projection identity or status.");
            var p = item.Provider ?? throw new ArgumentException();
            ValidateContextContributionToken(p.Id, 128);
            if (p.Version < 1 || p.Description is null || p.Description.Length > 4096 ||
                p.MaxReferences is < 1 or > ContextProviderContract.MaximumReferences ||
                p.MaxUtf8Bytes is < 1 or > ContextProviderContract.MaximumUtf8Bytes ||
                p.SupportedReferenceKinds is null || p.SupportedReferenceKinds.Count is < 1 or > ContextProviderContract.MaximumReferenceKinds ||
                p.SupportedReferenceKinds.Distinct(StringComparer.Ordinal).Count() != p.SupportedReferenceKinds.Count)
                throw new ArgumentException("Invalid receipt projection provider.");
            foreach (var kind in p.SupportedReferenceKinds) ValidateContextContributionToken(kind, 64);
            var descriptor = item.ProjectionDescriptor ?? throw new ArgumentException();
            if (descriptor.SchemaVersion != 1 || descriptor.ToolNames is null ||
                descriptor.ToolNames.Count is < 1 or > WorkflowToolContextProjectionContract.MaximumToolNames ||
                descriptor.ToolNames.Distinct(StringComparer.Ordinal).Count() != descriptor.ToolNames.Count)
                throw new ArgumentException("Invalid receipt projection tool filter.");
            foreach (var name in descriptor.ToolNames)
            {
                ValidateContextContributionToken(name, 256);
                if (name == ToolContextProject) throw new ArgumentException("Projection cannot observe itself.");
            }
            var request = descriptor.PublicationTemplate ?? throw new ArgumentException();
            if (request.ProviderId != p.Id || request.ProviderVersion != p.Version ||
                request.ContextRevision < 0 || request.ExclusionRevision < 0 ||
                request.MaximumUtf8Bytes < 1 || request.MaximumUtf8Bytes > p.MaxUtf8Bytes ||
                request.References is not { Count: 1 })
                throw new ArgumentException("Invalid receipt publication template.");
            var reference = request.References[0];
            ValidateContextContributionToken(reference.Id, 256);
            ValidateContextContributionToken(reference.SourceId, 256);
            ValidateContextContributionToken(reference.Kind, 64);
            if (!sourceIds.Add(reference.Id) || reference.ExpectedRevision is not null ||
                !p.SupportedReferenceKinds.Contains(reference.Kind, StringComparer.Ordinal) || !Enum.IsDefined(reference.Requirement) ||
                reference.Priority is < 0 or > 1_000_000)
                throw new ArgumentException("Invalid or colliding receipt publication slot.");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(ToolContextAdmissionView(state), Json).Length > MaximumContextAdmissionBytes)
            throw new ArgumentException("Receipt projection admission exceeds its bound.");
    }

    private static void ValidateToolContextStateEnvelope(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "toolContextProjections", "pendingToolContextProjection" })
        {
            var fields = state.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != name) throw new ArgumentException("Invalid receipt state field.");
            if (fields.Length == 0 || fields[0].Value.ValueKind == JsonValueKind.Null) continue;
            var raw = fields[0].Value;
            if (name == "toolContextProjections")
            {
                ToolContextObject(raw, ["schemaVersion", "runId", "items"]);
                foreach (var item in raw.GetProperty("items").EnumerateArray())
                {
                    ToolContextObject(item, ["id", "status", "reason", "provider", "projectionDescriptor", "providerState",
                        "lastProjectionEffectId", "slotSourceRevision", "slotProjectionEffectId", "lastLink"]);
                    ValidateToolContextAdmissionJson(Element(new
                    {
                        id = item.GetProperty("id"), status = item.GetProperty("status"), reason = item.GetProperty("reason"),
                        provider = item.GetProperty("provider"), projectionDescriptor = item.GetProperty("projectionDescriptor")
                    }));
                    if (item.GetProperty("lastLink").ValueKind != JsonValueKind.Null) ValidateToolContextLinkJson(item.GetProperty("lastLink"));
                }
            }
            else
            {
                ToolContextObject(raw, ["toolEffectId", "toolName", "contributionIds", "index", "commandRequest", "continuation"]);
                ValidateToolContextCommandJson(raw.GetProperty("commandRequest"));
                ToolContextObject(raw.GetProperty("continuation"), ["kind", "commandId", "commandVersion", "arguments", "summary", "success", "error", "action"]);
            }
        }
    }

    private static void ValidateToolContextProjectionState(HarnessState state)
    {
        if (state.ToolContextProjections is not { } collection)
        {
            if (state.PendingToolContextProjection is not null) throw new ArgumentException("Receipt projection has no edition marker.");
            return;
        }
        ValidateContextContributionToken(collection.RunId, 256);
        ValidateToolContextAdmission(state);
        if (!state.HistoryLoaded && collection.Items.Count != 0) throw new ArgumentException("Receipt admission preceded history.");
        foreach (var item in collection.Items)
        {
            ValidateToolContextOpaqueState(item.ProviderState);
            var reference = item.ProjectionDescriptor.PublicationTemplate.References[0];
            var sources = state.Contributions.Where(s => s.Id == reference.Id).ToArray();
            if (state.Checkpoint?.Id == reference.Id || state.Checkpoint?.CoveredSources.Any(s => s.Id == reference.Id) == true)
                throw new ArgumentException("Receipt projection slot cannot overlap checkpoint coverage.");
            if ((item.LastProjectionEffectId is null) != (item.LastLink is null) ||
                (item.SlotSourceRevision is null) != (item.SlotProjectionEffectId is null) || item.SlotSourceRevision is < 1)
                throw new ArgumentException("Receipt projection ledger is inconsistent.");
            if (item.LastLink is null && item.ProviderState is not null)
                throw new ArgumentException("Opaque receipt state has no prior projection.");
            if (item.SlotProjectionEffectId is { } slotEffect) ValidateContextContributionToken(slotEffect, 256);
            if (item.LastLink is { } link)
            {
                ValidateToolContextLink(link);
                if (link.ContributionId != item.Id || link.RunId != collection.RunId || link.ProjectionEffectId != item.LastProjectionEffectId ||
                    link.Slot.Id != reference.Id ||
                    (item.SlotSourceRevision ?? 0) < (link.Slot.CurrentSourceRevision ?? 0) ||
                    (item.SlotSourceRevision ?? 0) - (link.Slot.CurrentSourceRevision ?? 0) > 1 ||
                    (item.SlotProjectionEffectId == item.LastProjectionEffectId
                        ? item.SlotSourceRevision != (link.Slot.CurrentSourceRevision ?? 0) + 1
                        : item.SlotSourceRevision != link.Slot.CurrentSourceRevision))
                    throw new ArgumentException("Receipt projection ledger linkage changed.");
            }
            if (item.SlotSourceRevision is { } revision)
            {
                if (item.Status != "ready" || item.LastLink is null || sources.Length != 1)
                    throw new ArgumentException("Active receipt slot has no owning ledger.");
                ValidateContextContributionSource(sources[0], item.Provider.Id, reference with { ExpectedRevision = revision });
                ValidateToolContextHandle(sources[0], item.Provider, reference, item.SlotProjectionEffectId!, null);
            }
            else if (sources.Length != 0) throw new ArgumentException("Inactive receipt slot collides with an existing source.");
            if (item.Status != "ready" && (item.ProviderState is not null || item.LastLink is not null))
                throw new ArgumentException("Unready receipt contribution has execution state.");
        }
        if (state.PendingToolContextProjection is not { } pending) return;
        ValidateContextContributionToken(pending.ToolEffectId, 256);
        ValidateContextContributionToken(pending.ToolName, 256);
        if (pending.ContributionIds is null || pending.ContributionIds.Count is < 1 or > WorkflowToolContextProjectionContract.MaximumPendingContributions ||
            pending.ContributionIds.Distinct(StringComparer.Ordinal).Count() != pending.ContributionIds.Count ||
            pending.Index < 0 || pending.Index >= pending.ContributionIds.Count ||
            !state.ToolReceipts.Any(r => r.EffectId == pending.ToolEffectId && r.Name == pending.ToolName && r.State is "Succeeded" or "Failed"))
            throw new ArgumentException("Invalid pending receipt projection queue.");
        foreach (var id in pending.ContributionIds)
            if (!collection.Items.Any(i => i.Id == id && i.Status == "ready" &&
                i.ProjectionDescriptor.ToolNames.Contains(pending.ToolName, StringComparer.Ordinal)))
                throw new ArgumentException("Pending receipt projection is not admitted for this tool.");
        var current = collection.Items.Single(i => i.Id == pending.ContributionIds[pending.Index]);
        var expected = ToolContextCommand(state, pending, current);
        if (!JsonElement.DeepEquals(Element(expected), Element(pending.CommandRequest)))
            throw new ArgumentException("Pending receipt projection differs from its ledger.");
        ValidateToolContextContinuation(state, pending.Continuation);
        ToolContextBound(Element(pending.CommandRequest), WorkflowToolContextProjectionContract.MaximumCommandRequestUtf8Bytes);
    }

    private static WorkflowStep ReceiveToolAndProject(Input input, HarnessState state, WorkflowEffect effect, string runId, string requestId)
    {
        if (state.PendingToolContextProjection is not null) throw new ArgumentException("A receipt projection is already pending.");
        var step = ReceiveTool(input, state, effect, runId, requestId);
        if (state.ToolContextProjections is null) return step;
        var adopted = step.State.Deserialize<HarnessState>(Json) ?? throw new ArgumentException();
        var receipt = adopted.ToolReceipts.SingleOrDefault(r => r.EffectId == effect.Id);
        if (receipt is null || receipt.State is not ("Succeeded" or "Failed")) return step;
        var ids = adopted.ToolContextProjections!.Items.Where(i => i.Status == "ready" &&
            i.ProjectionDescriptor.ToolNames.Contains(receipt.Name, StringComparer.Ordinal) &&
            i.LastLink?.ToolEffectId != effect.Id).Select(i => i.Id).ToArray();
        if (ids.Length == 0) return step;
        var continuation = CaptureToolContextContinuation(adopted, step.Next);
        var pending = new PendingToolContextProjection(effect.Id, receipt.Name, Array.AsReadOnly(ids), 0, null!, continuation);
        return RequestToolContextProjection(adopted with { PendingToolContextProjection = pending });
    }

    private static WorkflowToolContextProjectionCommandRequest ToolContextCommand(HarnessState state,
        PendingToolContextProjection pending, ToolContextItem item) => new(1, item.Id, pending.ToolEffectId,
        item.LastProjectionEffectId, new(item.ProjectionDescriptor.PublicationTemplate.References[0].Id, item.SlotSourceRevision),
        item.ProviderState?.Clone(), state.ContextRevision, state.ExclusionRevision);

    private static WorkflowStep RequestToolContextProjection(HarnessState state)
    {
        var pending = state.PendingToolContextProjection ?? throw new ArgumentException();
        var item = state.ToolContextProjections!.Items.Single(i => i.Id == pending.ContributionIds[pending.Index]);
        var request = ToolContextCommand(state, pending, item);
        var next = state with { PendingToolContextProjection = pending with { CommandRequest = request } };
        ValidateToolContextProjectionState(next);
        var args = Element(request);
        return Fits(next) && EnvelopeFits(args)
            ? new(Element(next), new InvokeWorkflowCommand(ToolContextProject, 1, args))
            : ContextFull(state);
    }

    private static WorkflowStep ReceiveToolContextProjection(Input input, HarnessState state, WorkflowEffect effect)
    {
        var pending = state.PendingToolContextProjection ?? throw new ArgumentException();
        var collection = state.ToolContextProjections ?? throw new ArgumentException();
        ValidateToolContextProjectionState(state);
        if (effect.CommandId != ToolContextProject || effect.CommandVersion != 1 ||
            !JsonElement.DeepEquals(effect.Arguments, Element(pending.CommandRequest)))
            throw new ArgumentException("Receipt projection result has no exact pending intent.");
        // A failed projection retains the physical tool and every old source/state. Blocked Data
        // is parsed here, before ordinary failure routing; it never becomes a tool observation.
        if (effect.Result is not { } raw)
            return Finish(state, false, effect.Error ?? "Receipt projection did not return a confirmed result; no dependent preparation was requested.");
        ToolContextBound(raw, WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes);
        ValidateToolContextResultJson(raw);
        var result = raw.Deserialize<WorkflowToolContextProjectionResult>(ContextContributionJson) ?? throw new ArgumentException();
        ValidateToolContextResultLink(state, pending, effect, result.Link);
        var item = collection.Items.Single(i => i.Id == pending.ContributionIds[pending.Index]);
        var kind = result.Disposition;
        if (kind == WorkflowToolContextProjectionDisposition.Blocked)
        {
            if (effect.State != WorkflowEffectState.Failed || result.NextState is not null || result.ProviderResult is not null ||
                result.SlotUpdate is not null || string.IsNullOrWhiteSpace(result.Reason) ||
                result.Reason.Length > WorkflowToolContextProjectionContract.MaximumReasonCharacters || result.Reason.Contains('\0'))
                throw new ArgumentException("Invalid blocked receipt projection.");
            return Finish(state, false, "Receipt projection blocked dependent preparation: " + result.Reason);
        }
        if (effect.State != WorkflowEffectState.Succeeded || string.IsNullOrWhiteSpace(effect.ProviderReceiptId) || result.Reason is not null ||
            kind is not (WorkflowToolContextProjectionDisposition.StateOnly or WorkflowToolContextProjectionDisposition.Publish))
            return Finish(state, false, effect.Error ?? "Receipt projection failed or is unconfirmed; prior evidence remains retained.");
        ValidateContextContributionReceipt(effect.ProviderReceiptId);
        ValidateToolContextOpaqueState(result.NextState);
        var updated = item with { ProviderState = result.NextState?.Clone(), LastProjectionEffectId = effect.Id, LastLink = result.Link };
        var sources = state.Contributions;
        var contextRevision = state.ContextRevision;
        if (kind == WorkflowToolContextProjectionDisposition.StateOnly)
        {
            if (result.SlotUpdate is not null || result.ProviderResult is not null)
                throw new ArgumentException("State-only receipt projection cannot change a source.");
        }
        else
        {
            var reference = item.ProjectionDescriptor.PublicationTemplate.References[0];
            var revision = checked((item.SlotSourceRevision ?? 0) + 1);
            if (result.SlotUpdate is not { } update || update.Id != reference.Id || update.SourceRevision != revision ||
                result.ProviderResult is not { } provider || provider.ProviderId != item.Provider.Id ||
                provider.ProviderVersion != item.Provider.Version || provider.Window is not { Checkpoint: null } window ||
                window.Revision != state.ContextRevision || window.ExclusionRevision != state.ExclusionRevision ||
                window.Contributions is not { Count: 1 })
                throw new ArgumentException("Receipt publication differs from its fixed own-slot request.");
            ToolContextBound(Element(provider), Math.Min(WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes,
                item.ProjectionDescriptor.PublicationTemplate.MaximumUtf8Bytes));
            var request = FreezeContextContributionRequest(item.ProjectionDescriptor.PublicationTemplate) with
            {
                ContextRevision = state.ContextRevision, ExclusionRevision = state.ExclusionRevision,
                References = Array.AsReadOnly(new[] { reference with { ExpectedRevision = revision } })
            };
            ContextValidation.ValidateWindow(window);
            ValidateContextContributionSources(window, request, raw.GetProperty("providerResult").GetProperty("window").GetProperty("contributions"));
            var source = window.Contributions[0];
            ValidateToolContextHandle(source, item.Provider, reference, effect.Id, effect.ProviderReceiptId);
            var existing = sources.Where(s => s.Id == reference.Id).ToArray();
            if (state.Checkpoint?.Id == reference.Id || state.Checkpoint?.CoveredSources.Any(s => s.Id == reference.Id) == true ||
                (item.SlotSourceRevision is null ? existing.Length != 0 :
                    existing.Length != 1 || existing[0].Owner != item.Provider.Id || existing[0].SourceRevision != item.SlotSourceRevision))
                throw new ArgumentException("Receipt publication does not own its replacement target.");
            var frozen = FreezeContextContributionWindow(window).Contributions[0];
            sources = Array.AsReadOnly(sources.Where(s => s.Id != reference.Id).Append(frozen).ToArray());
            updated = updated with { SlotSourceRevision = revision, SlotProjectionEffectId = effect.Id };
            contextRevision = checked(contextRevision + 1);
        }
        var items = collection.Items.Select(i => i.Id == updated.Id ? updated : i).ToArray();
        var next = state with
        {
            Contributions = sources, ContextRevision = contextRevision,
            ToolContextProjections = collection with { Items = Array.AsReadOnly(items) }
        };
        ContextValidation.ValidateWindow(Window(next));
        if (!Fits(next)) return Finish(state, false, "Receipt publication exceeded the durable state bound; prior source/state remain retained.");
        if (pending.Index + 1 < pending.ContributionIds.Count)
            return RequestToolContextProjection(next with { PendingToolContextProjection = pending with { Index = pending.Index + 1 } });
        next = next with { PendingToolContextProjection = null };
        ValidateToolContextProjectionState(next);
        return ResumeToolContextContinuation(input, next, pending.Continuation);
    }

    private static ToolContextContinuation CaptureToolContextContinuation(HarnessState state, WorkflowAction action)
    {
        ToolContextContinuation continuation;
        if (action is InvokeWorkflowCommand invoke)
        {
            if (invoke.CommandId == Generate && state.Phase == "model") continuation = new("model");
            else if (invoke.CommandId == Generate && state.Phase == "generated-model") continuation = new("generated");
            else if (invoke.CommandId == Compact) continuation = new("compact");
            else if (state.PendingArguments is { } args && JsonElement.DeepEquals(args, invoke.Arguments))
                continuation = new("pending-command", invoke.CommandId, invoke.CommandVersion);
            else continuation = new("command", invoke.CommandId, invoke.CommandVersion, invoke.Arguments.Clone());
        }
        else if (action is CompleteWorkflow complete)
        {
            ToolContextObject(complete.Result, ["summary", "toolReceipts"]);
            if (!JsonElement.DeepEquals(complete.Result.GetProperty("toolReceipts"), Element(state.ToolReceipts)))
                throw new ArgumentException("Deferred completion changed its retained tool ledger.");
            continuation = new("complete", Summary: complete.Result.GetProperty("summary").GetString(),
                Success: complete.Success, Error: complete.Error);
        }
        else continuation = new("action", Action: action);
        ValidateToolContextContinuation(state, continuation);
        return continuation;
    }

    private static void ValidateToolContextContinuation(HarnessState state, ToolContextContinuation continuation)
    {
        ToolContextBound(Element(continuation), MaximumToolContextControlBytes);
        if (continuation.Kind is not ("model" or "generated" or "compact" or "pending-command" or "command" or "complete" or "action") ||
            continuation.Kind == "model" && state.Phase != "model" ||
            continuation.Kind == "generated" && (state.Phase != "generated-model" || state.PendingGeneration is null || state.PendingArguments is null) ||
            continuation.Kind == "compact" && state.Phase != "compact" ||
            continuation.Kind == "pending-command" && state.PendingArguments is null ||
            (continuation.Kind is "pending-command" or "command") && (string.IsNullOrWhiteSpace(continuation.CommandId) || continuation.CommandVersion < 1 ||
                continuation.CommandId == ToolContextProject) ||
            continuation.Kind == "command" && continuation.Arguments is null ||
            continuation.Kind == "complete" && continuation.Summary is null ||
            continuation.Kind == "action" && continuation.Action is not (AskWorkflowQuestions or WaitForWorkflowRuns))
            throw new ArgumentException("Invalid deferred receipt projection continuation.");
    }

    private static WorkflowStep ResumeToolContextContinuation(Input input, HarnessState state, ToolContextContinuation continuation) =>
        continuation.Kind switch
        {
            "model" => RequestModel(input, state),
            "compact" => ResumeToolContextCompaction(input, state),
            "generated" => ResumeRawToolContextGeneration(state),
            "pending-command" => new(Element(state), new InvokeWorkflowCommand(continuation.CommandId!, continuation.CommandVersion, state.PendingArguments!.Value)),
            "command" => new(Element(state), new InvokeWorkflowCommand(continuation.CommandId!, continuation.CommandVersion, continuation.Arguments!.Value)),
            "complete" => new(Element(state), new CompleteWorkflow(Element(new { summary = continuation.Summary, toolReceipts = state.ToolReceipts }), continuation.Success, continuation.Error)),
            "action" => new(Element(state), continuation.Action!),
            _ => throw new ArgumentException("Invalid receipt projection continuation.")
        };

    private static WorkflowStep ResumeToolContextCompaction(Input input, HarnessState state)
    {
        // This intent was prepared but never dispatched. Rebase its window without resetting
        // any retry/repair counter or pretending a prior compaction was executed.
        var prepared = PrepareGenerationEnvelope(input, state);
        var next = prepared.State with { LastCompactionRevision = prepared.State.ContextRevision };
        return Fits(next) && EnvelopeFits(prepared.Arguments)
            ? new(Element(next), new InvokeWorkflowCommand(Compact, 1, prepared.Arguments)) : ContextFull(state);
    }

    private static WorkflowStep ResumeRawToolContextGeneration(HarnessState state)
    {
        var args = AddRawToolContext(state, state.PendingArguments ?? throw new ArgumentException());
        var next = state with { PendingArguments = args };
        return Fits(next) && EnvelopeFits(args) ? new(Element(next), new InvokeWorkflowCommand(Generate, 1, args)) : ContextFull(state);
    }

    private static JsonElement AddRawToolContext(HarnessState state, JsonElement arguments)
    {
        if (state.ToolContextProjections is null) return arguments;
        // Raw artifact generation retains its original prompt/response format. Carry only the
        // receipt-owned Required sources as context; never import the chat action JSON policy.
        var window = new ContextWindow(state.ContextRevision, state.ExclusionRevision, ToolContextRequiredSources(state));
        ContextValidation.ValidateWindow(window);
        var values = arguments.EnumerateObject().Where(p => p.Name != "context")
            .ToDictionary(p => p.Name, p => (object)p.Value.Clone(), StringComparer.Ordinal);
        values.Add("context", window);
        return Element(values);
    }

    private static IReadOnlyList<ContextContribution> ToolContextRequiredSources(HarnessState state)
    {
        var ids = state.ToolContextProjections?.Items.Where(i => i.SlotSourceRevision is not null)
            .Select(i => i.ProjectionDescriptor.PublicationTemplate.References[0].Id).ToHashSet(StringComparer.Ordinal) ?? [];
        return Array.AsReadOnly(state.Contributions.Where(s => ids.Contains(s.Id) && s.Requirement == ContextRequirement.Required).ToArray());
    }

    private static void ValidateRawToolContextResult(HarnessState state, GenerationData data)
    {
        if (state.ToolContextProjections is null) return;
        if (data.Context is not { Status: ContextProjectionStatus.Ready } projection ||
            projection.ContextRevision != state.ContextRevision || projection.ExclusionRevision != state.ExclusionRevision ||
            projection.IncludedSources is null || projection.Omissions is not { Count: 0 })
            throw new ArgumentException("Raw generation did not confirm its exact receipt-context window.");
        var required = ToolContextRequiredSources(state);
        if (projection.IncludedSources.Count != required.Count ||
            required.Any(source => !projection.IncludedSources.Contains(new ContextSourceReference(source.Id, source.SourceRevision))))
            throw new ArgumentException("Raw generation did not confirm every exact Required receipt source.");
    }

    private static void ValidateToolContextResultLink(HarnessState state, PendingToolContextProjection pending,
        WorkflowEffect effect, WorkflowToolContextProjectionLink link)
    {
        ValidateToolContextLink(link);
        var request = pending.CommandRequest;
        if (link.ContributionId != request.ContributionId || link.RunId != state.ToolContextProjections!.RunId ||
            link.ProjectionEffectId != effect.Id || link.ToolEffectId != pending.ToolEffectId ||
            link.PreviousProjectionEffectId != request.PreviousProjectionEffectId ||
            !JsonElement.DeepEquals(Element(link.Slot), Element(request.Slot)))
            throw new ArgumentException("Receipt projection changed its exact source/state linkage.");
    }

    private static void ValidateToolContextLink(WorkflowToolContextProjectionLink link)
    {
        if (link is null || link.SchemaVersion != 1 || link.Slot is null) throw new ArgumentException();
        foreach (var id in new[] { link.ContributionId, link.RunId, link.ProjectionEffectId, link.ToolEffectId, link.Slot.Id })
            ValidateContextContributionToken(id, 256);
        if (link.PreviousProjectionEffectId is { } previous) ValidateContextContributionToken(previous, 256);
        if (link.Slot.CurrentSourceRevision is < 1 || link.SavedEffectSha256 is not { Length: 64 } hash ||
            hash.Any(c => !char.IsAsciiHexDigit(c) || c is >= 'A' and <= 'F'))
            throw new ArgumentException("Invalid receipt projection link.");
    }

    private static void ValidateToolContextHandle(ContextContribution source, ContextProviderDescriptor provider,
        ContextProviderReference reference, string effectId, string? receiptId)
    {
        var parts = source.ExactReadHandle?.Split('.') ?? [];
        if (parts.Length != 7 || parts[0] != "cerebral-context:v1" || parts[2] != provider.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            parts[6] != source.SourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Invalid normalized receipt context handle.");
        string Untoken(string token)
        {
            var raw = token.Replace('-', '+').Replace('_', '/');
            try
            {
                var bytes = Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '='));
                if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != token)
                    throw new ArgumentException("Noncanonical receipt context handle token.");
                return ContextContributionUtf8.GetString(bytes);
            }
            catch (FormatException error) { throw new ArgumentException("Invalid receipt context handle token.", error); }
        }
        var savedReceipt = Untoken(parts[4]);
        ValidateContextContributionReceipt(savedReceipt);
        if (Untoken(parts[1]) != provider.Id || Untoken(parts[3]) != effectId || Untoken(parts[5]) != reference.SourceId ||
            receiptId is not null && savedReceipt != receiptId)
            throw new ArgumentException("Receipt context handle has foreign provenance.");
    }

    private static void ValidateToolContextOpaqueState(JsonElement? state)
    {
        if (state is { } value) ToolContextBound(value, WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes);
    }
    private static void ToolContextBound(JsonElement value, int maximum)
    {
        if (ContextContributionUtf8.GetByteCount(value.GetRawText()) > maximum ||
            JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > maximum)
            throw new ArgumentException("Receipt context value exceeds its complete UTF-8 bound.");
    }
    private static void ToolContextObject(JsonElement value, string[] fields)
    {
        ContextContributionUniqueFields(value);
        if (value.EnumerateObject().Count() != fields.Length || fields.Any(f => !value.TryGetProperty(f, out _)) ||
            value.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Invalid closed receipt context object.");
    }
    private static void ValidateToolContextCommandJson(JsonElement raw)
    {
        ToolContextObject(raw, ["schemaVersion", "contributionId", "toolEffectId", "previousProjectionEffectId", "slot", "priorState", "contextRevision", "exclusionRevision"]);
        ToolContextObject(raw.GetProperty("slot"), ["id", "currentSourceRevision"]);
    }
    private static void ValidateToolContextLinkJson(JsonElement raw)
    {
        ToolContextObject(raw, ["schemaVersion", "contributionId", "runId", "projectionEffectId", "toolEffectId", "savedEffectSha256", "previousProjectionEffectId", "slot"]);
        ToolContextObject(raw.GetProperty("slot"), ["id", "currentSourceRevision"]);
    }
    private static void ValidateToolContextResultJson(JsonElement raw)
    {
        ToolContextObject(raw, ["schemaVersion", "link", "disposition", "nextState", "slotUpdate", "providerResult", "reason"]);
        if (raw.GetProperty("schemaVersion").GetInt32() != 1 || raw.GetProperty("disposition").ValueKind != JsonValueKind.String ||
            raw.GetProperty("disposition").GetString() is not ("stateOnly" or "publish" or "blocked")) throw new ArgumentException();
        ValidateToolContextLinkJson(raw.GetProperty("link"));
        if (raw.GetProperty("slotUpdate").ValueKind != JsonValueKind.Null)
            ToolContextObject(raw.GetProperty("slotUpdate"), ["id", "sourceRevision"]);
        if (raw.GetProperty("providerResult").ValueKind != JsonValueKind.Null)
        {
            var provider = raw.GetProperty("providerResult");
            ToolContextObject(provider, ["providerId", "providerVersion", "window"]);
            var window = provider.GetProperty("window");
            ToolContextObject(window, ["revision", "exclusionRevision", "contributions", "checkpoint"]);
            if (window.GetProperty("contributions").ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Receipt publication requires an actual source array.");
        }
    }
}
