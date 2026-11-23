using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Host-only optional facet. Domain selection/codecs never cross into Core or Engine.</summary>
internal sealed class PersonalityWorkflowContextContribution(PersonalityServicePlugin plugin) : IWorkflowContextContribution
{
    public WorkflowContextContributionDescriptor Descriptor { get; } = new(
        WorkflowContextContributionContract.SchemaVersion, PersonalityContract.ContributionId,
        PersonalityContextProvider.Description, WorkflowContextHistoryProjection.ReferenceOnly,
        Array.AsReadOnly(new[] { new WorkflowContextLegacyFormat(4,
            Array.AsReadOnly(new[] { "personalityAdmission", "personalityInstructions", "personalityMigration" })) }));

    public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        plugin.RequireAdmissionAvailability();
        if (!PersonalityContributionCodec.Applicable(facts))
            return Task.FromResult(Absent(WorkflowContextDisposition.Omitted, "scope-excluded"));
        try
        {
            var captured = plugin.CaptureChat(facts.Identity.ConversationId);
            if (!captured.Selection.Enabled)
                return Task.FromResult(Absent(WorkflowContextDisposition.Omitted, "disabled"));
            PersonalityContributionCodec.KnownOrigin(facts);
            if (captured.Source is null)
                return Task.FromResult(Absent(WorkflowContextDisposition.Unavailable, "source-unprepared"));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(PersonalityChatContributionCodec.Capture(facts, captured.Selection, captured.Source));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or ArgumentException or JsonException or NotSupportedException or PersonalityChatFailure)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Absent(WorkflowContextDisposition.Unavailable, "selection-unavailable"));
        }
    }

    public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
        WorkflowContextSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        plugin.RequireAdmissionAvailability();
        if (selection.Disposition != WorkflowContextDisposition.Ready) return;
        PersonalityContributionCodec.KnownOrigin(accepted);
        PersonalityContributionCodec.KnownOrigin(current);
        if (!PersonalityContributionCodec.Applicable(current) || current.WorkerId != accepted.WorkerId ||
            current.InstallationId != accepted.InstallationId || !SameIdentity(accepted.Identity, current.Identity))
            throw new InvalidDataException("Accepted personality ownership or origin changed.");
        if (PersonalityChatContributionCodec.IsChat(selection)) _ = PersonalityChatContributionCodec.Decode(accepted, selection);
        else _ = PersonalityContributionCodec.Decode(accepted, selection);
        token.ThrowIfCancellationRequested();
    }

    public Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
        WorkflowContextSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        plugin.RequireAdmissionAvailability();
        PersonalityContributionCodec.KnownOrigin(facts.Accepted);
        if (PersonalityChatContributionCodec.IsChat(selection))
        {
            var pin = PersonalityChatContributionCodec.Decode(facts.Accepted, selection);
            PersonalityAdmissionEngine.ValidateAcceptedTarget(PersonalityContributionCodec.Submission(facts.Accepted) with
                { ProjectId = facts.ProjectId, ConversationSequence = facts.ConversationSequence });
            if (facts.ContextRevision < 0 || facts.ExclusionRevision < 0)
                throw new InvalidDataException("Invalid accepted context target.");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(PersonalityChatContributionCodec.Template(pin, facts.ContextRevision, facts.ExclusionRevision));
        }
        var admission = PersonalityContributionCodec.Decode(facts.Accepted, selection);
        var accepted = PersonalityContributionCodec.Submission(facts.Accepted) with
        { ProjectId = facts.ProjectId, ConversationSequence = facts.ConversationSequence };
        var consumer = PersonalityContributionCodec.Consumer(facts.Accepted);
        var bound = admission.BindAccepted(accepted, consumer, admission.ToSnapshot());
        var request = bound.CreateRequest(accepted, consumer, facts.ContextRevision, facts.ExclusionRevision);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(request);
    }

    public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token)
        => PersonalityLegacyContextReader.Read(facts, token);

    internal static WorkflowContextSelection Absent(WorkflowContextDisposition disposition, string? reason)
        => new(disposition, reason, null, null, null);

    private static bool SameIdentity(WorkflowContextIdentity left, WorkflowContextIdentity right) =>
        left.RequestId == right.RequestId && left.ConversationId == right.ConversationId &&
        left.ProfileId == right.ProfileId && left.DefinitionId == right.DefinitionId &&
        left.DefinitionVersion == right.DefinitionVersion && left.ParentRunId == right.ParentRunId &&
        left.Caller?.Kind == right.Caller?.Kind && left.Caller?.Id == right.Caller?.Id;
}

internal static class PersonalityContributionCodec
{
    internal const string Contract = "pegasus.personality.context-seed";

    internal static bool Applicable(WorkflowContextFacts facts) => facts.ProfileRole == "lead" &&
        facts.WorkerId == "local" && facts.Identity.DefinitionId == ConversationWorkflowContract.DefinitionId &&
        facts.Identity.ParentRunId is null && facts.Identity.Caller is { Kind: EventActorKind.User, Id: "owner" };

    internal static void KnownOrigin(WorkflowContextFacts facts)
    {
        if (!Applicable(facts) || facts.InstallationId is not { Length: > 0 and <= 128 } ||
            facts.InstallationId.Any(char.IsControl))
            throw new InvalidDataException("Personality execution requires known accepted owner origin.");
    }

    internal static WorkflowSubmission Submission(WorkflowContextFacts facts) =>
        new(facts.Identity.RequestId, facts.Identity.ConversationId, facts.Identity.ProfileId,
            facts.Identity.DefinitionId, facts.Identity.DefinitionVersion, string.Empty, null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: facts.Identity.Caller is { } actor ? new(actor.Kind, actor.Id) : null)
        { ParentRunId = facts.Identity.ParentRunId };

    internal static PersonalityConsumer Consumer(WorkflowContextFacts facts) =>
        new(facts.Identity.ProfileId, facts.ProfileRole, PersonalityConsumerKind.UserFacingConversation);

    internal static ContextProviderRequest Template(PersonalityAdmission seed, long context = 0, long exclusion = 0) =>
        new(PersonalityContract.ProviderId, PersonalityContract.ProviderVersion,
            Array.AsReadOnly(new[] { new ContextProviderReference(PersonalityContract.ContributionId,
                PersonalityContract.ReferenceKind, seed.SourceId, seed.SourceRevision,
                ContextRequirement.Optional, Priority: 0, BeforeCurrentInput: true) }),
            PersonalityContract.MaximumUtf8Bytes, context, exclusion);

    internal static string Hash(JsonElement value) =>
        PersonalityContract.Digest(JsonSerializer.SerializeToUtf8Bytes(value, PersonalityContract.Json));

    internal static PersonalityAdmission Decode(WorkflowContextFacts facts, WorkflowContextSelection selection)
    {
        try
        {
            if (selection.Disposition != WorkflowContextDisposition.Ready || selection.Payload is not { } payload ||
                selection.PayloadDigest is not { } digest || selection.Request is null)
                throw new InvalidDataException();
            Boundary(payload, WorkflowContextContributionContract.MaximumPayloadUtf8Bytes);
            PersonalityAdmissionEngine.RequireHash(digest);
            if (Hash(payload) != digest) throw new InvalidDataException();
            JsonElement inner;
            if (payload.GetProperty("contract").GetString() == PersonalityAdmission.SnapshotContract)
                inner = payload; // Original legacy seed/digest remains unchanged.
            else
            {
                PersonalityServicePlugin.Fields(payload,
                    ["contract", "version", "settingsRevision", "settingsSha256", "seed", "seedSha256"]);
                if (payload.GetProperty("contract").GetString() != Contract ||
                    payload.GetProperty("version").GetInt32() != 1 ||
                    payload.GetProperty("settingsRevision").GetInt64() < 0) throw new InvalidDataException();
                PersonalityAdmissionEngine.RequireHash(payload.GetProperty("settingsSha256").GetString());
                inner = payload.GetProperty("seed");
                PersonalityAdmissionEngine.RequireHash(payload.GetProperty("seedSha256").GetString());
                if (Hash(inner) != payload.GetProperty("seedSha256").GetString()) throw new InvalidDataException();
            }
            Boundary(inner, PersonalityAdmission.MaximumSnapshotUtf8Bytes);
            var seed = PersonalityAdmission.Restore(inner, Submission(facts), Consumer(facts));
            // The original private canonical digest is independent of the new outer envelope.
            if (Hash(seed.ToSnapshot()) != Hash(inner) ||
                !JsonElement.DeepEquals(PersonalityContract.Element(Template(seed)),
                    PersonalityContract.Element(selection.Request)))
                throw new InvalidDataException();
            return seed;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("The accepted personality contribution is invalid or mismatched."); }
    }

    internal static void Boundary(JsonElement value, int bytes)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            PersonalityContract.Utf8.GetByteCount(value.GetRawText()) > bytes)
            throw new InvalidDataException("Personality compatibility state exceeds its bound.");
        var count = 0;
        Visit(value, 0);
        void Visit(JsonElement node, int depth)
        {
            if (++count > 512 || depth > 16) throw new InvalidDataException("Invalid personality compatibility shape.");
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in node.EnumerateObject())
                {
                    if (!names.Add(item.Name)) throw new InvalidDataException("Duplicate personality fields.");
                    Visit(item.Value, depth + 1);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var child in node.EnumerateArray()) Visit(child, depth + 1);
        }
    }
}
