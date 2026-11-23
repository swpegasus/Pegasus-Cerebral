using System.Text.Json;
using Cerebral.Contracts.Context;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Versioned per-chat seeds coexist with the unchanged 1.2.2 and legacy seed readers.</summary>
internal static class PersonalityChatContributionCodec
{
    internal const string Contract = "pegasus.personality.chat-seed";
    internal static bool IsChat(WorkflowContextSelection selection) => selection.Payload is { ValueKind: JsonValueKind.Object } payload &&
        payload.TryGetProperty("contract", out var contract) && contract.ValueKind == JsonValueKind.String && contract.GetString() == Contract;

    internal static WorkflowContextSelection Capture(WorkflowContextFacts facts, PersonalityChatState chat, PersonalityEffectiveSource source)
    {
        if (!chat.Enabled || chat.BaseSelection != source.BaseSelection || chat.BluntPin != source.BluntPin || chat.EffectiveSource != source.Pin)
            throw new InvalidDataException("The chat source and selection do not match.");
        var payload = PersonalityContract.Element(new
        {
            contract = Contract, version = 1, identity = Identity(facts), chatRevision = chat.Revision,
            baseSelection = chat.BaseSelection, bluntPin = chat.BluntPin, effectiveSource = source.Pin
        });
        var selected = new WorkflowContextSelection(WorkflowContextDisposition.Ready, null, payload,
            PersonalityContributionCodec.Hash(payload), Template(source.Pin));
        _ = Decode(facts, selected);
        return selected;
    }

    internal static PersonalityEffectivePin Decode(WorkflowContextFacts facts, WorkflowContextSelection selected)
    {
        try
        {
            PersonalityContributionCodec.KnownOrigin(facts);
            PersonalityAdmissionEngine.ValidateIdentity(PersonalityContributionCodec.Submission(facts));
            PersonalityChatContract.ValidateConversationId(facts.Identity.ConversationId);
            if (selected.Disposition != WorkflowContextDisposition.Ready || selected.Payload is not { } payload ||
                selected.PayloadDigest is not { } digest || selected.Request is null) throw new InvalidDataException();
            PersonalityContributionCodec.Boundary(payload, WorkflowContextContributionContract.MaximumPayloadUtf8Bytes);
            PersonalityServicePlugin.Fields(payload, ["contract", "version", "identity", "chatRevision", "baseSelection", "bluntPin", "effectiveSource"]);
            if (payload.GetProperty("contract").GetString() != Contract || payload.GetProperty("version").GetInt32() != 1 ||
                payload.GetProperty("chatRevision").GetInt64() < 1 || PersonalityContributionCodec.Hash(payload) != digest ||
                !JsonElement.DeepEquals(payload.GetProperty("identity"), Identity(facts))) throw new InvalidDataException();
            var baseValue = payload.GetProperty("baseSelection");
            PersonalityServicePlugin.Fields(baseValue, ["kind", "id", "revision", "sha256"]);
            var basePin = baseValue.Deserialize<PersonalityDefinitionPin>(PersonalityContract.Json) ?? throw new InvalidDataException();
            PersonalityChatContract.ValidatePin(basePin);
            PersonalityModifierPin? modifier = null;
            var mode = payload.GetProperty("bluntPin");
            if (mode.ValueKind != JsonValueKind.Null)
            {
                PersonalityServicePlugin.Fields(mode, ["id", "revision", "sha256"]);
                modifier = mode.Deserialize<PersonalityModifierPin>(PersonalityContract.Json) ?? throw new InvalidDataException();
                PersonalityChatContract.ValidateModifier(modifier);
            }
            var sourceValue = payload.GetProperty("effectiveSource");
            PersonalityServicePlugin.Fields(sourceValue, ["sourceId", "revision", "sha256"]);
            var source = sourceValue.Deserialize<PersonalityEffectivePin>(PersonalityContract.Json) ?? throw new InvalidDataException();
            PersonalityChatContract.ValidateEffectivePin(source);
            var descriptorHash = PersonalityContributionCodec.Hash(PersonalityContract.Element(new { baseSelection = basePin, bluntPin = modifier }));
            if (source.SourceId != "personality/v2/" + descriptorHash + "/" + source.Sha256 ||
                !JsonElement.DeepEquals(PersonalityContract.Element(Template(source)), PersonalityContract.Element(selected.Request)))
                throw new InvalidDataException();
            return source;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or KeyNotFoundException or FormatException or OverflowException or PersonalityChatFailure)
        { throw new InvalidDataException("The accepted per-chat personality seed is invalid or mismatched."); }
    }

    internal static ContextProviderRequest Template(PersonalityEffectivePin pin, long context = 0, long exclusion = 0) =>
        new(PersonalityContract.ProviderId, PersonalityContract.ProviderVersion,
            Array.AsReadOnly(new[] { new ContextProviderReference(PersonalityContract.ContributionId,
                PersonalityContract.ReferenceKind, pin.SourceId, pin.Revision, ContextRequirement.Optional, Priority: 0, BeforeCurrentInput: true) }),
            PersonalityContract.MaximumUtf8Bytes, context, exclusion);

    private static JsonElement Identity(WorkflowContextFacts facts) => PersonalityContract.Element(new
    {
        facts.Identity.RequestId, facts.Identity.ConversationId, facts.Identity.ProfileId, facts.Identity.DefinitionId,
        facts.Identity.DefinitionVersion, callerKind = facts.Identity.Caller?.Kind, callerId = facts.Identity.Caller?.Id,
        facts.Identity.ParentRunId, facts.ProfileRole, facts.WorkerId, facts.InstallationId
    });
}
