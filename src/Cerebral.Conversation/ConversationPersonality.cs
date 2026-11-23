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
    private const string PersonalityProvider = "pegasus.personality:context.resolve";
    private const string PersonalityKind = "personality.behavior";
    private const string PersonalityContribution = "pegasus.personality:style";
    private const int PersonalityProviderVersion = 1, PersonalityBytes = 2048;
    private static readonly UTF8Encoding PersonalityUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PersonalityJson = new(Json)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    // This metadata arrives only through the confirmed host history effect, never public Input.
    private sealed record PersonalityAdmissionData(string Status, ContextProviderRequest? Request);
    private sealed record PersonalityState(string Status, ContextProviderRequest? Request)
    {
        public bool Attempted { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Outcome { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EffectId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderReceiptId { get; init; }
    }

    private static void ValidatePersonalityStateEnvelope(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        try
        {
            var fields = state.EnumerateObject().Where(property =>
                property.Name.Equals("personality", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0) return;
            if (fields.Length != 1 || fields[0].Name != "personality")
                throw new ArgumentException();
            if (fields[0].Value.ValueKind == JsonValueKind.Null) return;
            var value = fields[0].Value;
            ObjectFields(value, ["status", "request", "attempted"], ["outcome", "effectId", "providerReceiptId"]);
            ValidatePersonalityRequestJson(value.GetProperty("request"));
            _ = value.Deserialize<PersonalityState>(PersonalityJson) ?? throw new ArgumentException();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("The saved personality metadata is invalid."); }
    }

    private static PersonalityState? ReadPersonalityAdmission(JsonElement result, HarnessState state)
    {
        try
        {
            var fields = result.EnumerateObject().Where(property =>
                property.Name.Equals("personality", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0) return state.Personality;
            if (fields.Length != 1 || fields[0].Name != "personality") throw new ArgumentException();
            var value = fields[0].Value;
            if (value.ValueKind == JsonValueKind.Null) return state.Personality;
            // Later history can supply prior evidence, but cannot choose another style for this run.
            if (state.HistoryLoaded || state.Personality is not null) throw new ArgumentException();
            ObjectFields(value, ["status", "request"], []);
            ValidatePersonalityRequestJson(value.GetProperty("request"));
            var admission = value.Deserialize<PersonalityAdmissionData>(PersonalityJson)
                ?? throw new ArgumentException();
            ValidatePersonalityAdmission(admission.Status, admission.Request);
            return new(admission.Status, admission.Request is null ? null : CopyPersonalityRequest(admission.Request));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("The first host history returned invalid personality admission metadata."); }
    }

    private static void ValidatePersonalityRequestJson(JsonElement request)
    {
        if (request.ValueKind == JsonValueKind.Null) return;
        ObjectFields(request, ["providerId", "providerVersion", "references", "maximumUtf8Bytes",
            "contextRevision", "exclusionRevision"], []);
        var references = request.GetProperty("references");
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != 1)
            throw new ArgumentException();
        var reference = references[0];
        ObjectFields(reference, ["id", "kind", "sourceId", "expectedRevision", "requirement",
            "priority", "beforeCurrentInput"], []);
        if (reference.GetProperty("requirement").ValueKind != JsonValueKind.String ||
            reference.GetProperty("requirement").GetString() != "optional") throw new ArgumentException();
        UniqueProperties(request);
        if (PersonalityUtf8.GetByteCount(request.GetRawText()) > 4096) throw new ArgumentException();
    }

    private static void ValidatePersonalityAdmission(string status, ContextProviderRequest? request)
    {
        if (status is not ("ready" or "notApplicable" or "disabled" or "unavailable" or "migrationPending" or "legacy") ||
            (status == "ready") != (request is not null)) throw new ArgumentException();
        if (request is null) return;
        if (request.ProviderId != PersonalityProvider || request.ProviderVersion != PersonalityProviderVersion ||
            request.MaximumUtf8Bytes != PersonalityBytes || request.ContextRevision < 0 || request.ExclusionRevision < 0 ||
            request.References is not { Count: 1 }) throw new ArgumentException();
        var reference = request.References[0];
        if (reference is null || reference.Id != PersonalityContribution || reference.Kind != PersonalityKind ||
            reference.ExpectedRevision is not >= 1 || reference.Requirement != ContextRequirement.Optional ||
            reference.Priority != 0 || !reference.BeforeCurrentInput) throw new ArgumentException();
        ValidatePersonalityIdentifier(reference.SourceId);
    }

    private static ContextProviderRequest CopyPersonalityRequest(ContextProviderRequest request) =>
        request with { References = Array.AsReadOnly(request.References.Select(reference => reference with { }).ToArray()) };

    private static void ValidatePersonalityState(HarnessState state)
    {
        try
        {
            var styles = state.Contributions.Where(source => source.Id == PersonalityContribution).ToArray();
            if (state.Personality is not { } personality)
            {
                // A legacy state has neither a pending personality phase nor an adopted reserved style.
                if (state.Phase == "personality" || styles.Length != 0) throw new ArgumentException();
                return;
            }
            ValidatePersonalityAdmission(personality.Status, personality.Request);
            if (!state.HistoryLoaded || state.Phase == "personality" && !personality.Attempted)
                throw new ArgumentException();
            if (personality.Status != "ready")
            {
                if (personality.Attempted || personality.Outcome is not null || personality.EffectId is not null ||
                    personality.ProviderReceiptId is not null || styles.Length != 0) throw new ArgumentException();
                return;
            }
            if (personality.Outcome is null)
            {
                if (personality.EffectId is not null || personality.ProviderReceiptId is not null || styles.Length != 0 ||
                    personality.Attempted && state.Phase != "personality") throw new ArgumentException();
                if (personality.Attempted)
                {
                    if (state.PendingArguments is not { } pendingArguments) throw new ArgumentException();
                    ValidatePersonalityRequestJson(pendingArguments);
                    var pending = pendingArguments.Deserialize<ContextProviderRequest>(PersonalityJson)
                        ?? throw new ArgumentException();
                    var expected = personality.Request! with
                        { ContextRevision = state.ContextRevision, ExclusionRevision = state.ExclusionRevision };
                    if (!JsonElement.DeepEquals(Element(pending), Element(expected))) throw new ArgumentException();
                }
                return;
            }
            if (!personality.Attempted || personality.EffectId is null) throw new ArgumentException();
            ValidatePersonalityIdentifier(personality.EffectId);
            if (personality.Outcome == "received")
            {
                if (personality.ProviderReceiptId != personality.EffectId || styles.Length != 1) throw new ArgumentException();
                ValidatePersonalityContribution(styles[0], personality.Request!.References[0]);
            }
            else if (personality.Outcome != "sourceUnavailable" || personality.ProviderReceiptId is not null || styles.Length != 0)
                throw new ArgumentException();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("The saved personality attempt or receipt is inconsistent."); }
    }

    private static WorkflowStep? TryRequestPersonality(HarnessState state)
    {
        if (!state.HistoryLoaded || state.Personality is not { Status: "ready", Attempted: false, Request: { } selection })
            return null;
        return CreatePersonalityStep(state, selection);
    }

    private static WorkflowStep RebaseUndispatchedPersonality(HarnessState state)
    {
        if (state.Phase != "personality" || state.Personality is not
            { Status: "ready", Attempted: true, Outcome: null, Request: { } selection })
            throw new ArgumentException("The newly selected personality request is inconsistent.");
        return CreatePersonalityStep(state, selection);
    }

    private static WorkflowStep CreatePersonalityStep(HarnessState state, ContextProviderRequest selection)
    {
        // Selection is immutable; only the active host-owned window revisions are rebased.
        var request = CopyPersonalityRequest(selection) with
            { ContextRevision = state.ContextRevision, ExclusionRevision = state.ExclusionRevision };
        var arguments = Element(request);
        var next = state with
        {
            Phase = "personality", PendingArguments = arguments,
            Personality = state.Personality! with { Attempted = true }
        };
        // Mark attempted in the returned durable state before dispatch. No model progress/correction reset.
        return Fits(next) && EnvelopeFits(arguments)
            ? new(Element(next), new InvokeWorkflowCommand(request.ProviderId, request.ProviderVersion, arguments))
            : ContextFull(state);
    }

    private static WorkflowStep ContinueInitialConversation(Input input, HarnessState state)
    {
        if (state.ContextContributions is not null) return ContinueContextContributions(input, state);
        if (TryRequestPersonality(state) is { } personality) return personality;
        if (state.CatalogLoaded) return RequestModel(input, state);
        var arguments = Element(new { });
        return Invoke(state with { Phase = "catalog", PendingArguments = arguments }, Catalog, arguments);
    }

    private static WorkflowStep ReceivePersonality(Input input, HarnessState state, WorkflowEffect effect)
    {
        try
        {
            if (state.Personality is not { Status: "ready", Attempted: true, Outcome: null, Request: { } selection } ||
                effect.CommandId != PersonalityProvider || effect.CommandVersion != PersonalityProviderVersion)
                throw new ArgumentException();
            MatchPending(state, effect);
            ValidatePersonalityIdentifier(effect.Id);
            if (effect.State == WorkflowEffectState.Failed && effect.Result is { } failure)
            {
                ObjectFields(failure, ["failureKind"], []);
                if (failure.GetProperty("failureKind").ValueKind == JsonValueKind.String &&
                    failure.GetProperty("failureKind").GetString() == "source-unavailable" &&
                    effect.ProviderReceiptId is null)
                {
                    // Preserve the actual failed receipt. Only this declared optional omission is allowed.
                    var omitted = state with
                    {
                        PendingArguments = null,
                        Personality = state.Personality with { Outcome = "sourceUnavailable", EffectId = effect.Id }
                    };
                    return ContinueInitialConversation(input, omitted);
                }
            }
            if (effect.State != WorkflowEffectState.Succeeded || effect.Result is not { } result ||
                effect.ProviderReceiptId != effect.Id)
                return Finish(state, false, "Personality context has no matching confirmed outcome. No replacement or retry was requested.");
            ObjectFields(result, ["providerId", "providerVersion", "window"], []);
            var data = result.Deserialize<ContextProviderResult>(PersonalityJson) ?? throw new ArgumentException();
            if (data.ProviderId != PersonalityProvider || data.ProviderVersion != PersonalityProviderVersion ||
                data.Window is not { Checkpoint: null, Contributions.Count: 1 } window ||
                window.Revision != state.ContextRevision || window.ExclusionRevision != state.ExclusionRevision ||
                PersonalityUtf8.GetByteCount(result.GetRawText()) > 8192) throw new ArgumentException();
            ContextValidation.ValidateWindow(window);
            ValidatePersonalityContribution(window.Contributions[0], selection.References[0]);
            var next = state with
            {
                PendingArguments = null, ContextRevision = checked(state.ContextRevision + 1),
                Contributions = [.. state.Contributions, .. window.Contributions],
                Personality = state.Personality with
                    { Outcome = "received", EffectId = effect.Id, ProviderReceiptId = effect.ProviderReceiptId }
            };
            ContextValidation.ValidateWindow(Window(next));
            ValidatePersonalityState(next);
            return Fits(next) ? ContinueInitialConversation(input, next) : ContextFull(state);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return Finish(state, false, "The personality result did not match its immutable request and receipt. No replacement or retry was requested."); }
    }

    private static void ValidatePersonalityContribution(ContextContribution source, ContextProviderReference reference)
    {
        if (source is null || source.Id != reference.Id || source.Owner != PersonalityProvider || source.Category != reference.Kind ||
            source.SourceRevision != reference.ExpectedRevision || source.Requirement != ContextRequirement.Optional ||
            source.Priority != 0 || !source.BeforeCurrentInput || !source.Complete ||
            string.IsNullOrWhiteSpace(source.ExactReadHandle) || source.ExactReadHandle.Length > 2048 ||
            source.Messages is not { Count: 1 } || source.Messages[0] is not { Role: "user", Content.Count: 1 } message ||
            message.Content[0] is not TextContentPart text || string.IsNullOrWhiteSpace(text.Text) ||
            PersonalityUtf8.GetByteCount(text.Text) > PersonalityBytes) throw new ArgumentException();
    }

    private static void ValidatePersonalityIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) throw new ArgumentException();
    }

    private static object PersonalityOutcomeProjection(PersonalityState state) => new
        { state.Status, state.Attempted, state.Outcome, state.EffectId, state.ProviderReceiptId };
}
