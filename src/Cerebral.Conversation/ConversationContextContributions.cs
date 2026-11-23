using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string ContextContributionPhase = "context-contribution";
    private const string ContextContributionFailedPhase = "context-contribution-failed";
    private const int MaximumContextAdmissionBytes = 16 * 1024;
    private static readonly UTF8Encoding ContextContributionUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ContextContributionJson = new(Json)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // A nonnull collection is the opt-in edition marker. Legacy states remain null.
    private sealed record ContextContributionsState(int SchemaVersion, IReadOnlyList<ContextContributionAttempt> Items);
    private sealed record ContextContributionAttempt(string Id, string Status, ContextProviderRequest? Request)
    {
        public bool Attempted { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EffectId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderReceiptId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Outcome { get; init; }
    }

    private sealed record ContextContributionsAdmission(int SchemaVersion, IReadOnlyList<ContextContributionAdmission> Items);
    private sealed record ContextContributionAdmission(string Id, string Status, ContextProviderRequest? Request);

    private static HarnessState ReadContextContributionsAdmission(JsonElement result, HarnessState state)
    {
        if (state.ContextContributions is null) return state;
        try
        {
            if (result.ValueKind != JsonValueKind.Object ||
                ContextContributionUtf8.GetByteCount(result.GetRawText()) > 48 * 1024)
                throw new ArgumentException();
            var fields = result.EnumerateObject().Where(property =>
                property.Name.Equals("contextContributions", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length > 1 || fields.Length == 1 && fields[0].Name != "contextContributions")
                throw new ArgumentException();
            if (fields.Length == 0 || fields[0].Value.ValueKind == JsonValueKind.Null)
                return state;
            var value = fields[0].Value;
            ValidateContextAdmissionJson(value, attemptedState: false);
            if (ContextContributionUtf8.GetByteCount(value.GetRawText()) > MaximumContextAdmissionBytes)
                throw new ArgumentException();
            var admission = value.Deserialize<ContextContributionsAdmission>(ContextContributionJson)
                ?? throw new ArgumentException();
            var selection = new ContextContributionsState(admission.SchemaVersion,
                Array.AsReadOnly(admission.Items.Select(item => new ContextContributionAttempt(item.Id, item.Status,
                    item.Request is null ? null : FreezeContextContributionRequest(item.Request))).ToArray()));
            ValidateContextContributionsSelection(selection);
            if (state.HistoryLoaded)
            {
                // Pagination supplies old evidence, never a replacement selection for this turn.
                if (!JsonElement.DeepEquals(ContextContributionAdmissionProjection(selection),
                        ContextContributionAdmissionProjection(state.ContextContributions)))
                    throw new ArgumentException();
                return state;
            }
            if (state.ContextContributions.Items.Count != 0) throw new ArgumentException();
            return state with { ContextContributions = selection };
        }
        catch (Exception error) when (ContextContributionInvalid(error))
        { throw new ArgumentException("The host history returned invalid context contribution admission metadata."); }
    }

    private static void ValidateContextContributionStateEnvelope(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        try
        {
            var fields = state.EnumerateObject().Where(property =>
                property.Name.Equals("contextContributions", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0) return;
            if (fields.Length != 1 || fields[0].Name != "contextContributions") throw new ArgumentException();
            if (fields[0].Value.ValueKind == JsonValueKind.Null) return;
            ValidateContextAdmissionJson(fields[0].Value, attemptedState: true);
            _ = fields[0].Value.Deserialize<ContextContributionsState>(ContextContributionJson)
                ?? throw new ArgumentException();
        }
        catch (Exception error) when (ContextContributionInvalid(error))
        { throw new ArgumentException("The saved context contribution envelope is invalid."); }
    }

    private static void ValidateContextAdmissionJson(JsonElement value, bool attemptedState)
    {
        ObjectFields(value, ["schemaVersion", "items"], []);
        if (Integer(value.GetProperty("schemaVersion"), 1, 1) != WorkflowContextContributionContract.SchemaVersion)
            throw new ArgumentException();
        var items = value.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() > WorkflowContextContributionContract.MaximumContributions)
            throw new ArgumentException();
        foreach (var item in items.EnumerateArray())
        {
            ObjectFields(item, attemptedState ? ["id", "status", "request", "attempted"] : ["id", "status", "request"],
                attemptedState ? ["effectId", "providerReceiptId", "outcome"] : []);
            if (attemptedState && item.GetProperty("attempted").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException();
            ValidateContextContributionRequestJson(item.GetProperty("request"));
        }
    }

    private static void ValidateContextContributionRequestJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        ObjectFields(value, ["providerId", "providerVersion", "references", "maximumUtf8Bytes", "contextRevision", "exclusionRevision"], []);
        _ = Integer(value.GetProperty("providerVersion"), 1, int.MaxValue);
        _ = Integer(value.GetProperty("maximumUtf8Bytes"), 1, ContextProviderContract.MaximumUtf8Bytes);
        _ = Integer(value.GetProperty("contextRevision"), 0, long.MaxValue);
        _ = Integer(value.GetProperty("exclusionRevision"), 0, long.MaxValue);
        var references = value.GetProperty("references");
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() is < 1 or > ContextProviderContract.MaximumReferences)
            throw new ArgumentException();
        foreach (var reference in references.EnumerateArray())
        {
            ObjectFields(reference, ["id", "kind", "sourceId", "expectedRevision", "requirement", "priority", "beforeCurrentInput"], []);
            if (reference.GetProperty("expectedRevision") is { ValueKind: not JsonValueKind.Null } revision)
                _ = Integer(revision, 0, long.MaxValue);
            if (reference.GetProperty("requirement").ValueKind != JsonValueKind.String ||
                reference.GetProperty("requirement").GetString() is not ("required" or "optional" or "reducible") ||
                reference.GetProperty("beforeCurrentInput").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException();
            _ = Integer(reference.GetProperty("priority"), 0, 1_000_000);
        }
    }

    private static void ValidateContextContributionsSelection(ContextContributionsState collection)
    {
        if (collection.SchemaVersion != WorkflowContextContributionContract.SchemaVersion || collection.Items is null ||
            collection.Items.Count > WorkflowContextContributionContract.MaximumContributions)
            throw new ArgumentException();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var templates = new List<JsonElement>();
        foreach (var item in collection.Items)
        {
            if (item is null) throw new ArgumentException();
            ValidateContextContributionToken(item.Id, 256);
            if (!itemIds.Add(item.Id) || item.Status is not ("ready" or "omitted" or "unavailable" or "blocked" or "unsupported") ||
                (item.Status == "ready") != (item.Request is not null)) throw new ArgumentException();
            if (item.Request is not { } request) continue;
            ValidateContextContributionToken(request.ProviderId, 256);
            if (request.ProviderVersion < 1 || request.MaximumUtf8Bytes is < 1 or > ContextProviderContract.MaximumUtf8Bytes ||
                request.ContextRevision < 0 || request.ExclusionRevision < 0 || request.References is null ||
                request.References.Count is < 1 or > ContextProviderContract.MaximumReferences) throw new ArgumentException();
            var referenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in request.References)
            {
                if (reference is null) throw new ArgumentException();
                ValidateContextContributionToken(reference.Id, 256);
                ValidateContextContributionToken(reference.Kind, 64);
                ValidateContextContributionToken(reference.SourceId, 256);
                if (!referenceIds.Add(reference.Id) || reference.ExpectedRevision < 0 || !Enum.IsDefined(reference.Requirement) ||
                    reference.Priority is < 0 or > 1_000_000) throw new ArgumentException();
            }
            // Match the host's whole-template identity; distinct requests may share a provider or reference IDs.
            var template = Element(request);
            if (templates.Any(prior => JsonElement.DeepEquals(prior, template))) throw new ArgumentException();
            templates.Add(template);
        }
        if (ContextContributionUtf8.GetByteCount(ContextContributionAdmissionProjection(collection).GetRawText()) > MaximumContextAdmissionBytes)
            throw new ArgumentException();
    }

    private static JsonElement ContextContributionAdmissionProjection(ContextContributionsState collection) => Element(new
    {
        collection.SchemaVersion,
        items = collection.Items.Select(item => new { item.Id, item.Status, item.Request }).ToArray()
    });

    private static ContextProviderRequest FreezeContextContributionRequest(ContextProviderRequest request) =>
        request with { References = Array.AsReadOnly(request.References.Select(reference => reference with { }).ToArray()) };

    private static void ValidateContextContributionState(HarnessState state)
    {
        try
        {
            if (state.ContextContributions is not { } collection)
            {
                if (state.Phase is ContextContributionPhase or ContextContributionFailedPhase) throw new ArgumentException();
                return;
            }
            ContextValidation.ValidateWindow(Window(state));
            ValidateContextContributionsSelection(collection);
            if (!state.HistoryLoaded && collection.Items.Count != 0) throw new ArgumentException();
            var effectIds = new HashSet<string>(StringComparer.Ordinal);
            var open = false;
            var pending = 0;
            var completed = 0;
            foreach (var item in collection.Items)
            {
                if (item.Status != "ready")
                {
                    if (item.Attempted || item.Outcome is not null || item.EffectId is not null || item.ProviderReceiptId is not null)
                        throw new ArgumentException();
                    continue;
                }
                if (!item.Attempted)
                {
                    if (item.Outcome is not null || item.EffectId is not null || item.ProviderReceiptId is not null)
                        throw new ArgumentException();
                    open = true;
                }
                else
                {
                    if (!state.HistoryLoaded || open) throw new ArgumentException();
                    if (item.Outcome is null)
                    {
                        if (item.EffectId is not null || item.ProviderReceiptId is not null) throw new ArgumentException();
                        pending++;
                        open = true;
                        if (state.PendingArguments is not { } arguments) throw new ArgumentException();
                        ValidateContextContributionRequestJson(arguments);
                        var expected = RebasedContextContributionRequest(state, item.Request!);
                        if (!JsonElement.DeepEquals(arguments, Element(expected))) throw new ArgumentException();
                    }
                    else
                    {
                        ValidateContextContributionToken(item.EffectId!, 256);
                        if (!effectIds.Add(item.EffectId!) || !ContextContributionOutcome(item.Outcome)) throw new ArgumentException();
                        if (item.ProviderReceiptId is { } receipt) ValidateContextContributionReceipt(receipt);
                        if (item.Outcome == nameof(WorkflowEffectState.Succeeded))
                        {
                            if (item.ProviderReceiptId is null && state.Phase != ContextContributionFailedPhase) throw new ArgumentException();
                            if (state.Phase != ContextContributionFailedPhase)
                                ValidateRequiredContextContributionSources(state, item.Request!);
                        }
                        else
                        {
                            if (state.Phase != ContextContributionFailedPhase) throw new ArgumentException();
                            open = true;
                        }
                        completed++;
                    }
                }
            }
            if (state.Phase == ContextContributionPhase ? pending != 1 : pending != 0) throw new ArgumentException();
            if (state.Phase == ContextContributionFailedPhase && completed == 0) throw new ArgumentException();
        }
        catch (Exception error) when (ContextContributionInvalid(error))
        { throw new ArgumentException("The saved context contribution attempts are inconsistent."); }
    }

    private static WorkflowStep? TryRequestContextContribution(HarnessState state)
    {
        ValidateContextContributionState(state);
        if (state.Phase == ContextContributionFailedPhase)
            return Finish(state, false, "A context contribution attempt already stopped this turn. No retry or replacement was requested.");
        if (!state.HistoryLoaded || state.ContextContributions is not { } collection) return null;
        if (state.Phase == ContextContributionPhase) throw new ArgumentException("A context contribution is already pending.");
        var index = -1;
        for (var i = 0; i < collection.Items.Count; i++)
            if (collection.Items[i] is { Status: "ready", Attempted: false }) { index = i; break; }
        return index < 0 ? null : CreateContextContributionStep(state, index);
    }

    private static WorkflowStep RebaseUndispatchedContextContribution(HarnessState state)
    {
        // Called only for a newly returned, undispatched step after steering changed its window.
        if (state.Phase != ContextContributionPhase || state.ContextContributions is not { } collection)
            throw new ArgumentException("There is no new context contribution request to rebase.");
        var pending = collection.Items.Select((item, index) => (item, index))
            .Where(pair => pair.item is { Status: "ready", Attempted: true, Outcome: null }).ToArray();
        if (pending.Length != 1) throw new ArgumentException("The new context contribution request is inconsistent.");
        return CreateContextContributionStep(state, pending[0].index);
    }

    private static ContextProviderRequest RebasedContextContributionRequest(HarnessState state, ContextProviderRequest selection) =>
        FreezeContextContributionRequest(selection) with
        { ContextRevision = state.ContextRevision, ExclusionRevision = state.ExclusionRevision };

    private static WorkflowStep CreateContextContributionStep(HarnessState state, int index)
    {
        var collection = state.ContextContributions ?? throw new ArgumentException();
        var item = collection.Items[index];
        var request = RebasedContextContributionRequest(state, item.Request ?? throw new ArgumentException());
        var arguments = Element(request);
        var items = collection.Items.ToArray();
        items[index] = item with { Attempted = true };
        var next = state with
        {
            Phase = ContextContributionPhase, PendingArguments = arguments,
            ContextContributions = collection with { Items = Array.AsReadOnly(items) }
        };
        ValidateContextContributionState(next);
        // Direct dispatch preserves repairs and every other conversation counter.
        return Fits(next) && EnvelopeFits(arguments)
            ? new(Element(next), new InvokeWorkflowCommand(request.ProviderId, request.ProviderVersion, arguments))
            : ContextFull(state);
    }

    private static WorkflowStep ContinueContextContributions(Input input, HarnessState state)
    {
        if (TryRequestContextContribution(state) is { } contribution) return contribution;
        if (input.ForceCompaction && !state.InitialContextCompactionAttempted && CanCompact(Window(state)))
            return RequestCompaction(input, state with { InitialContextCompactionAttempted = true },
                "The owner requested one measured compaction after initial context contributions.");
        if (state.CatalogLoaded) return RequestModel(input, state);
        var arguments = Element(new { });
        return Invoke(state with { Phase = "catalog", PendingArguments = arguments }, Catalog, arguments);
    }

    private static WorkflowStep ReceiveContextContribution(Input input, HarnessState state, WorkflowEffect effect)
    {
        var observed = state;
        try
        {
            ValidateContextContributionState(state);
            if (state.Phase != ContextContributionPhase || state.ContextContributions is not { } collection)
                throw new ArgumentException();
            var index = collection.Items.Select((item, i) => (item, i)).Single(pair => pair.item.Attempted && pair.item.Outcome is null).i;
            var selection = collection.Items[index].Request!;
            if (effect.CommandId != selection.ProviderId || effect.CommandVersion != selection.ProviderVersion) throw new ArgumentException();
            MatchPending(state, effect);
            ValidateContextContributionToken(effect.Id, 256);
            if (!Enum.IsDefined(effect.State)) throw new ArgumentException();
            if (effect.ProviderReceiptId is { } receipt) ValidateContextContributionReceipt(receipt);
            var items = collection.Items.ToArray();
            items[index] = items[index] with
            { Outcome = effect.State.ToString(), EffectId = effect.Id, ProviderReceiptId = effect.ProviderReceiptId };
            observed = state with
            {
                Phase = ContextContributionFailedPhase, PendingArguments = null,
                ContextContributions = collection with { Items = Array.AsReadOnly(items) }
            };
            if (effect.State != WorkflowEffectState.Succeeded || effect.Result is not { } result || effect.ProviderReceiptId is null)
                return ContextContributionStop(observed, "Context contribution did not return a confirmed normalized result. No retry or replacement was requested.", state);
            if (ContextContributionUtf8.GetByteCount(result.GetRawText()) > ContextProviderContract.MaximumUtf8Bytes)
                throw new ArgumentException();
            ContextContributionObjectFields(result, ["providerId", "providerVersion", "window"]);
            _ = Integer(result.GetProperty("providerVersion"), 1, int.MaxValue);
            var rawWindow = result.GetProperty("window");
            ContextContributionObjectFields(rawWindow, ["revision", "exclusionRevision", "contributions", "checkpoint"]);
            _ = Integer(rawWindow.GetProperty("revision"), 0, long.MaxValue);
            _ = Integer(rawWindow.GetProperty("exclusionRevision"), 0, long.MaxValue);
            var data = result.Deserialize<ContextProviderResult>(ContextContributionJson) ?? throw new ArgumentException();
            if (data.ProviderId != selection.ProviderId || data.ProviderVersion != selection.ProviderVersion ||
                data.Window is not { Checkpoint: null } window || window.Revision != state.ContextRevision ||
                window.ExclusionRevision != state.ExclusionRevision) throw new ArgumentException();
            ContextValidation.ValidateWindow(window);
            ValidateContextContributionSources(window, selection, rawWindow.GetProperty("contributions"));
            // Reference identities may be reused by distinct selections, including optional empty
            // outcomes. Only sources actually returned would collide with an existing projection.
            var existingIds = state.Contributions.Select(source => source.Id)
                .Concat(state.Checkpoint?.CoveredSources.Select(source => source.Id) ?? [])
                .Concat(state.Checkpoint is { } checkpoint ? [checkpoint.Id] : []).ToHashSet(StringComparer.Ordinal);
            if (window.Contributions.Any(source => existingIds.Contains(source.Id))) throw new ArgumentException();
            var frozen = FreezeContextContributionWindow(window);
            var next = observed with
            {
                Phase = "history", ContextRevision = checked(state.ContextRevision + 1),
                Contributions = Array.AsReadOnly(state.Contributions.Concat(frozen.Contributions).ToArray())
            };
            ContextValidation.ValidateWindow(Window(next));
            ValidateContextContributionState(next);
            return Fits(next) ? ContinueInitialConversation(input, next) : ContextContributionStop(observed,
                "Context contribution exceeded the durable projection bound. Original sources and receipts remain saved; no source was clipped.", state);
        }
        catch (Exception error) when (ContextContributionInvalid(error))
        {
            return ContextContributionStop(observed,
                "The context contribution result did not match its immutable request and receipt. No retry or replacement was requested.", state);
        }
    }

    private static WorkflowStep ContextContributionStop(HarnessState observed, string reason, HarnessState fallback) =>
        Fits(observed) ? Finish(observed, false, reason) : ContextFull(fallback);

    private static void ValidateRequiredContextContributionSources(HarnessState state, ContextProviderRequest request)
    {
        // Required sources cannot disappear into a generated checkpoint. Optional and reducible
        // bodies may be omitted or compacted, so receipt metadata alone remains sufficient for them.
        foreach (var reference in request.References.Where(reference => reference.Requirement == ContextRequirement.Required))
        {
            var matching = state.Contributions.Where(source => source.Id == reference.Id).ToArray();
            if (matching.Length != 1) throw new ArgumentException();
            ValidateContextContributionSource(matching[0], request.ProviderId, reference);
        }
    }

    private static void ValidateContextContributionSources(ContextWindow window, ContextProviderRequest request, JsonElement rawSources)
    {
        if (rawSources.ValueKind != JsonValueKind.Array || window.Contributions.Count > request.References.Count)
            throw new ArgumentException();
        foreach (var raw in rawSources.EnumerateArray())
        {
            ContextContributionObjectFields(raw, ["id", "owner", "category", "sourceRevision", "requirement", "messages", "exactReadHandle", "complete", "priority", "beforeCurrentInput"]);
            if (raw.GetProperty("requirement").ValueKind != JsonValueKind.String ||
                raw.GetProperty("requirement").GetString() is not ("required" or "optional" or "reducible") ||
                raw.GetProperty("complete").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                raw.GetProperty("beforeCurrentInput").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException();
            _ = Integer(raw.GetProperty("sourceRevision"), 0, long.MaxValue);
            _ = Integer(raw.GetProperty("priority"), 0, 1_000_000);
            var messages = raw.GetProperty("messages");
            if (messages.ValueKind != JsonValueKind.Array) throw new ArgumentException();
            foreach (var message in messages.EnumerateArray())
            {
                ContextContributionObjectFields(message, ["role", "content"]);
                var content = message.GetProperty("content");
                if (content.ValueKind != JsonValueKind.Array) throw new ArgumentException();
                foreach (var part in content.EnumerateArray())
                {
                    ContextContributionUniqueFields(part);
                    if (part.TryGetProperty("attachment", out var attachment)) ContextContributionUniqueFields(attachment);
                    if (part.TryGetProperty("resource", out var resource)) ContextContributionUniqueFields(resource);
                }
            }
        }
        var references = request.References.ToDictionary(reference => reference.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in window.Contributions)
        {
            if (!seen.Add(source.Id) || !references.TryGetValue(source.Id, out var reference)) throw new ArgumentException();
            ValidateContextContributionSource(source, request.ProviderId, reference);
        }
        if (request.References.Any(reference => reference.Requirement == ContextRequirement.Required && !seen.Contains(reference.Id)))
            throw new ArgumentException();
    }

    private static void ValidateContextContributionSource(ContextContribution source, string providerId, ContextProviderReference reference)
    {
        if (source.Owner != providerId || source.Category != reference.Kind ||
            reference.ExpectedRevision is { } revision && source.SourceRevision != revision ||
            source.Requirement != reference.Requirement || source.Priority != reference.Priority ||
            source.BeforeCurrentInput != reference.BeforeCurrentInput) throw new ArgumentException();
        ValidateContextContributionText(source.ExactReadHandle!, 2048);
    }

    private static ContextWindow FreezeContextContributionWindow(ContextWindow window) => window with
    {
        Contributions = Array.AsReadOnly(window.Contributions.Select(source => source with
        {
            Messages = Array.AsReadOnly(source.Messages.Select(message => message with
            { Content = Array.AsReadOnly(message.Content.Select(FreezeContextContributionPart).ToArray()) }).ToArray())
        }).ToArray())
    };

    private static ContentPart FreezeContextContributionPart(ContentPart part) => part switch
    {
        TextContentPart text => text with { },
        StructuredContentPart structured => structured with { Value = structured.Value.Clone() },
        FileContentPart file => file with { Attachment = FreezeContextContributionAttachment(file.Attachment) },
        ImageContentPart image => image with { Attachment = FreezeContextContributionAttachment(image.Attachment) },
        AudioContentPart audio => audio with { Attachment = FreezeContextContributionAttachment(audio.Attachment) },
        ResourceContentPart resource => resource with { Resource = resource.Resource with { } },
        _ => throw new ArgumentException()
    };

    private static AttachmentReference FreezeContextContributionAttachment(AttachmentReference attachment) => attachment with
    {
        Metadata = attachment.Metadata is null ? null : new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(attachment.Metadata, StringComparer.Ordinal))
    };

    // ObjectFields intentionally traverses everything. Normalized content can contain arbitrary
    // structured values or attachment dictionaries, so enforce only the host-owned envelope here.
    private static void ContextContributionObjectFields(JsonElement value, string[] required)
    {
        ContextContributionUniqueFields(value);
        if (required.Any(name => !value.TryGetProperty(name, out _)) ||
            value.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException();
    }

    private static void ContextContributionUniqueFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException();
    }

    private static void ValidateContextContributionToken(string value, int maximum)
    {
        ValidateContextContributionText(value, maximum);
        if (!char.IsAsciiLetterOrDigit(value[0]) || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':' or '/' or '+')))
            throw new ArgumentException();
    }

    private static void ValidateContextContributionText(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0')) throw new ArgumentException();
        _ = ContextContributionUtf8.GetByteCount(value);
    }

    private static void ValidateContextContributionReceipt(string value)
    {
        ValidateContextContributionText(value, 512);
        if (ContextContributionUtf8.GetByteCount(value) > 512) throw new ArgumentException();
    }

    private static bool ContextContributionOutcome(string outcome) => outcome is nameof(WorkflowEffectState.Prepared) or
        nameof(WorkflowEffectState.Dispatched) or nameof(WorkflowEffectState.Succeeded) or nameof(WorkflowEffectState.Failed) or
        nameof(WorkflowEffectState.Cancelled) or nameof(WorkflowEffectState.Unknown);

    private static bool ContextContributionInvalid(Exception error) =>
        error is JsonException or ArgumentException or InvalidOperationException or OverflowException;

    private static object ContextContributionsOutcomeProjection(ContextContributionsState collection) => new
    {
        collection.SchemaVersion,
        items = collection.Items.Select(item => new
        { item.Id, item.Status, item.Attempted, item.Outcome, item.EffectId, item.ProviderReceiptId }).ToArray()
    };
}
