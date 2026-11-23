using System.Globalization;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Supplied from host-owned consumer/profile provenance, never from model input.</summary>
public enum PersonalityConsumerKind { UserFacingConversation, Helper, Compactor, Auditor, Programming }
public sealed record PersonalityConsumer(string ProfileId, string Role, PersonalityConsumerKind Kind);

public enum PersonalityAdmissionStatus { NotApplicable, Disabled, Ready, Unavailable }

/// <summary>Package-local admission outcome. An absent optional style never supplies a substitute persona.</summary>
public sealed record PersonalityAdmissionResult(PersonalityAdmissionStatus Status,
    PersonalityAdmission? Admission = null, string? Reason = null);

/// <summary>
/// Captures body-free stable preset metadata for host-admitted owner chat work. It does not authorize
/// callers, bind packages, dispatch effects, or modify instructions. The host must freeze the optional
/// package graph and persist the resulting request alongside its immutable accepted submission.
/// </summary>
public static class PersonalityAdmissionEngine
{
    public static async Task<PersonalityAdmissionResult> CaptureAsync(PersonalityServicePlugin? plugin,
        WorkflowSubmission submission, PersonalityConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();
        // Reject helper/compactor/service lineage before even accessing optional storage.
        if (!IsOwnerConversation(submission, consumer))
            return new(PersonalityAdmissionStatus.NotApplicable, Reason: "scope-excluded");
        if (plugin is null)
            return new(PersonalityAdmissionStatus.Unavailable, Reason: "component-unavailable");
        ValidateIdentity(submission);
        try
        {
            var value = await plugin.ExecuteAsync("personality.settings", WorkflowContract.EmptyObject, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var settings = PersonalityContract.Element(value!);
            PersonalityServicePlugin.ValidateArguments(settings);
            PersonalityServicePlugin.Fields(settings,
                ["schemaVersion", "revision", "sha256", "enabled", "selectedPresetId", "selection", "provider", "schemas", "retention"]);
            if (settings.GetProperty("schemaVersion").GetInt32() != PersonalityContract.SchemaVersion ||
                settings.GetProperty("revision").GetInt64() < 0)
                throw new InvalidDataException("Unsupported personality settings.");
            var settingsHash = settings.GetProperty("sha256").GetString();
            RequireHash(settingsHash);
            var enabled = settings.GetProperty("enabled");
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid personality availability.");
            var selection = settings.GetProperty("selection");
            if (!enabled.GetBoolean())
            {
                if (selection.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Disabled personality must omit selection.");
                return new(PersonalityAdmissionStatus.Disabled);
            }
            if (selection.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Enabled personality requires an exact source.");
            PersonalityServicePlugin.Fields(selection,
                ["presetId", "providerId", "providerVersion", "referenceKind", "contributionId", "sourceId",
                 "sourceRevision", "sourceSha256", "requirement", "beforeCurrentInput", "priority", "maximumUtf8Bytes"]);
            var id = selection.GetProperty("presetId").GetString();
            var revision = selection.GetProperty("sourceRevision").GetInt64();
            var hash = selection.GetProperty("sourceSha256").GetString();
            RequireHash(hash);
            var sourceId = selection.GetProperty("sourceId").GetString();
            if (!PersonalityContract.ValidId(id) || revision < 1 ||
                settings.GetProperty("selectedPresetId").GetString() != id ||
                sourceId != $"personality/v1/{id}/{revision.ToString(CultureInfo.InvariantCulture)}/{hash}" ||
                selection.GetProperty("providerId").GetString() != PersonalityContract.ProviderId ||
                selection.GetProperty("providerVersion").GetInt32() != PersonalityContract.ProviderVersion ||
                selection.GetProperty("referenceKind").GetString() != PersonalityContract.ReferenceKind ||
                selection.GetProperty("contributionId").GetString() != PersonalityContract.ContributionId ||
                selection.GetProperty("requirement").GetString() != "Optional" ||
                !selection.GetProperty("beforeCurrentInput").GetBoolean() ||
                selection.GetProperty("priority").GetInt32() != 0 ||
                selection.GetProperty("maximumUtf8Bytes").GetInt32() != PersonalityContract.MaximumUtf8Bytes)
                throw new InvalidDataException("Personality selection does not match its exact contract.");
            cancellationToken.ThrowIfCancellationRequested();
            var admission = new PersonalityAdmission(submission, consumer, sourceId!, revision, hash!,
                settings.GetProperty("revision").GetInt64(), settingsHash!);
            _ = admission.ToSnapshot(); // Admission must fit its durable bound before it is reported ready.
            return new(PersonalityAdmissionStatus.Ready, admission);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No settings text, paths, private bodies or parser excerpts enter diagnostics.
            return new(PersonalityAdmissionStatus.Unavailable, Reason: "selection-unavailable");
        }
    }

    internal static bool IsOwnerConversation(WorkflowSubmission submission, PersonalityConsumer consumer) =>
        consumer.Kind == PersonalityConsumerKind.UserFacingConversation && consumer.Role == "lead" &&
        consumer.ProfileId == submission.ProfileId && submission.DefinitionId == ConversationWorkflowContract.DefinitionId &&
        submission.ParentRunId is null &&
        submission.Caller is { Kind: EventActorKind.User, Id: "owner" };

    internal static void ValidateIdentity(WorkflowSubmission submission)
    {
        ValidateIdentifier(submission.RequestId, nameof(submission.RequestId), 256);
        ValidateIdentifier(submission.ConversationId, nameof(submission.ConversationId), 256);
        ValidateIdentifier(submission.ProfileId, nameof(submission.ProfileId), 128);
        ValidateIdentifier(submission.DefinitionVersion, nameof(submission.DefinitionVersion), 64);
    }

    internal static void ValidateAcceptedTarget(WorkflowSubmission submission)
    {
        ValidateIdentity(submission);
        if (submission.ProjectId is not null)
            ValidateIdentifier(submission.ProjectId, nameof(submission.ProjectId), 128);
        if (submission.ConversationSequence < 0)
            throw new ArgumentException("The accepted conversation sequence must be nonnegative.");
    }

    private static void ValidateIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("The accepted target requires a bounded identifier.", name);
    }

    internal static void RequireHash(string? hash)
    {
        if (hash is not { Length: 64 } ||
            hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("A canonical source hash is required.");
    }
}

/// <summary>
/// Immutable selected-source seed captured before the host transaction. Final ProjectId and history
/// sequence deliberately stay outside this seed: WorkflowStore supplies those at acceptance.
/// </summary>
public sealed class PersonalityAdmission
{
    public const string SnapshotContract = "pegasus.personality.admission-seed";
    public const int SnapshotVersion = 1;
    public const int MaximumSnapshotUtf8Bytes = 4096;
    private readonly string requestId, conversationId, profileId, definitionVersion;
    private readonly PersonalityConsumer consumer;

    internal PersonalityAdmission(WorkflowSubmission submission, PersonalityConsumer consumer, string sourceId,
        long sourceRevision, string sourceSha256, long settingsRevision, string settingsSha256)
    {
        this.consumer = consumer;
        requestId = submission.RequestId; conversationId = submission.ConversationId;
        profileId = submission.ProfileId; definitionVersion = submission.DefinitionVersion;
        SourceId = sourceId; SourceRevision = sourceRevision; SourceSha256 = sourceSha256;
        SettingsRevision = settingsRevision; SettingsSha256 = settingsSha256;
    }

    public string SourceId { get; }
    public long SourceRevision { get; }
    public string SourceSha256 { get; }
    public long SettingsRevision { get; }
    public string SettingsSha256 { get; }

    internal bool MatchesIdentity(WorkflowSubmission submission, PersonalityConsumer expectedConsumer) =>
        PersonalityAdmissionEngine.IsOwnerConversation(submission, expectedConsumer) && expectedConsumer == consumer &&
        submission.RequestId == requestId && submission.ConversationId == conversationId &&
        submission.ProfileId == profileId && submission.DefinitionVersion == definitionVersion;

    /// <summary>
    /// Body-free seed for the host-owned accepted snapshot. It is not authorization or a signature.
    /// Exact optional package/dependency generations and migration readiness remain host-owned.
    /// </summary>
    public JsonElement ToSnapshot()
    {
        var snapshot = PersonalityContract.Element(new
        {
            contract = SnapshotContract, version = SnapshotVersion,
            identity = new { requestId, conversationId, profileId,
                definitionId = ConversationWorkflowContract.DefinitionId, definitionVersion,
                consumerRole = consumer.Role, consumerKind = "userFacingConversation" },
            selection = new
            {
                providerId = PersonalityContract.ProviderId, providerVersion = PersonalityContract.ProviderVersion,
                referenceKind = PersonalityContract.ReferenceKind, contributionId = PersonalityContract.ContributionId,
                sourceId = SourceId, sourceRevision = SourceRevision, sourceSha256 = SourceSha256,
                settingsRevision = SettingsRevision, settingsSha256 = SettingsSha256,
                requirement = "optional", priority = 0, beforeCurrentInput = true,
                maximumUtf8Bytes = PersonalityContract.MaximumUtf8Bytes
            }
        });
        if (PersonalityContract.Utf8.GetByteCount(snapshot.GetRawText()) > MaximumSnapshotUtf8Bytes)
            throw new InvalidDataException("The personality source seed exceeds its durable byte bound.");
        return snapshot;
    }

    /// <summary>
    /// Decode only trusted host-persisted seed metadata for the same known request/profile/consumer.
    /// Does not access settings, storage, defaults, projected ProjectId or projected history sequence.
    /// Malformed or mismatched preserved state fails closed instead of recapturing live selection.
    /// </summary>
    public static PersonalityAdmission Restore(JsonElement snapshot, WorkflowSubmission submission,
        PersonalityConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(submission); ArgumentNullException.ThrowIfNull(consumer);
        PersonalityAdmissionEngine.ValidateIdentity(submission);
        try
        {
            if (!PersonalityAdmissionEngine.IsOwnerConversation(submission, consumer) ||
                snapshot.ValueKind != JsonValueKind.Object ||
                PersonalityContract.Utf8.GetByteCount(snapshot.GetRawText()) > MaximumSnapshotUtf8Bytes)
                throw new InvalidDataException();
            PersonalityServicePlugin.Fields(snapshot, ["contract", "version", "identity", "selection"]);
            if (snapshot.GetProperty("contract").GetString() != SnapshotContract ||
                snapshot.GetProperty("version").GetInt32() != SnapshotVersion)
                throw new InvalidDataException();
            var identity = snapshot.GetProperty("identity"); var selection = snapshot.GetProperty("selection");
            if (identity.ValueKind != JsonValueKind.Object || selection.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            PersonalityServicePlugin.Fields(identity, ["requestId", "conversationId", "profileId",
                "definitionId", "definitionVersion", "consumerRole", "consumerKind"]);
            PersonalityServicePlugin.Fields(selection, ["providerId", "providerVersion", "referenceKind",
                "contributionId", "sourceId", "sourceRevision", "sourceSha256", "settingsRevision", "settingsSha256",
                "requirement", "priority", "beforeCurrentInput", "maximumUtf8Bytes"]);
            if (identity.GetProperty("requestId").GetString() != submission.RequestId ||
                identity.GetProperty("conversationId").GetString() != submission.ConversationId ||
                identity.GetProperty("profileId").GetString() != submission.ProfileId ||
                identity.GetProperty("definitionId").GetString() != ConversationWorkflowContract.DefinitionId ||
                identity.GetProperty("definitionVersion").GetString() != submission.DefinitionVersion ||
                identity.GetProperty("consumerRole").GetString() != consumer.Role ||
                identity.GetProperty("consumerKind").GetString() != "userFacingConversation")
                throw new InvalidDataException();
            if (selection.GetProperty("providerId").GetString() != PersonalityContract.ProviderId ||
                selection.GetProperty("providerVersion").GetInt32() != PersonalityContract.ProviderVersion ||
                selection.GetProperty("referenceKind").GetString() != PersonalityContract.ReferenceKind ||
                selection.GetProperty("contributionId").GetString() != PersonalityContract.ContributionId ||
                selection.GetProperty("requirement").GetString() != "optional" ||
                selection.GetProperty("priority").GetInt32() != 0 ||
                !selection.GetProperty("beforeCurrentInput").GetBoolean() ||
                selection.GetProperty("maximumUtf8Bytes").GetInt32() != PersonalityContract.MaximumUtf8Bytes)
                throw new InvalidDataException();
            var sourceId = selection.GetProperty("sourceId").GetString();
            var sourceRevision = selection.GetProperty("sourceRevision").GetInt64();
            var sourceHash = selection.GetProperty("sourceSha256").GetString();
            var settingsRevision = selection.GetProperty("settingsRevision").GetInt64();
            var settingsHash = selection.GetProperty("settingsSha256").GetString();
            PersonalityAdmissionEngine.RequireHash(sourceHash); PersonalityAdmissionEngine.RequireHash(settingsHash);
            var parts = sourceId?.Split('/');
            if (parts is not { Length: 5 } || !PersonalityContract.ValidId(parts[2]) ||
                sourceRevision < 1 || settingsRevision < 0 ||
                sourceId != $"personality/v1/{parts[2]}/{sourceRevision.ToString(CultureInfo.InvariantCulture)}/{sourceHash}")
                throw new InvalidDataException();
            var restored = new PersonalityAdmission(submission, consumer, sourceId!, sourceRevision, sourceHash!,
                settingsRevision, settingsHash!);
            _ = restored.ToSnapshot(); // Raw Unicode input must also fit the canonical durable encoding.
            return restored;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The preserved personality source seed is invalid or mismatched.");
        }
    }

    /// <summary>
    /// Call only after acceptance. acceptedPersistedSeed must be extracted by the host from this exact
    /// authenticated submission's durable snapshot. Equality rejects a newer capture on an idempotent retry.
    /// This codec cannot authenticate caller-provided JSON or replace host package/grant checks.
    /// </summary>
    public BoundPersonalityAdmission BindAccepted(WorkflowSubmission accepted, PersonalityConsumer consumer,
        JsonElement acceptedPersistedSeed)
    {
        ArgumentNullException.ThrowIfNull(accepted); ArgumentNullException.ThrowIfNull(consumer);
        PersonalityAdmissionEngine.ValidateAcceptedTarget(accepted);
        if (!MatchesIdentity(accepted, consumer))
            throw new InvalidOperationException("The personality seed belongs to a different accepted identity.");
        var persisted = Restore(acceptedPersistedSeed, accepted, consumer);
        if (persisted.ToSnapshot().GetRawText() != ToSnapshot().GetRawText())
            throw new InvalidOperationException("The captured personality seed differs from accepted persisted state.");
        return new BoundPersonalityAdmission(this, accepted, consumer);
    }

    public static BoundPersonalityAdmission RestoreAccepted(JsonElement snapshot, WorkflowSubmission accepted,
        PersonalityConsumer consumer) => Restore(snapshot, accepted, consumer).BindAccepted(accepted, consumer, snapshot);
}

/// <summary>Final target binding from the host's authoritative accepted submission; never provisional metadata.</summary>
public sealed class BoundPersonalityAdmission
{
    private readonly PersonalityAdmission seed;
    private readonly string? projectId;
    private readonly long conversationSequence;
    private readonly PersonalityConsumer consumer;
    internal BoundPersonalityAdmission(PersonalityAdmission seed, WorkflowSubmission accepted, PersonalityConsumer consumer)
    {
        this.seed = seed; this.consumer = consumer;
        projectId = accepted.ProjectId; conversationSequence = accepted.ConversationSequence;
    }

    /// <summary>The host owns dispatch, normalization, current grants and package-generation checks.</summary>
    public ContextProviderRequest CreateRequest(WorkflowSubmission accepted, PersonalityConsumer consumer,
        long contextRevision, long exclusionRevision)
    {
        ArgumentNullException.ThrowIfNull(accepted); ArgumentNullException.ThrowIfNull(consumer);
        if (contextRevision < 0 || exclusionRevision < 0)
            throw new ArgumentException("Context revisions must be nonnegative.");
        if (!seed.MatchesIdentity(accepted, consumer) || consumer != this.consumer ||
            accepted.ProjectId != projectId || accepted.ConversationSequence != conversationSequence)
            throw new InvalidOperationException("Personality binding belongs to a different final accepted target.");
        var reference = new ContextProviderReference(PersonalityContract.ContributionId, PersonalityContract.ReferenceKind,
            seed.SourceId, seed.SourceRevision, ContextRequirement.Optional, Priority: 0, BeforeCurrentInput: true);
        return new(PersonalityContract.ProviderId, PersonalityContract.ProviderVersion, Array.AsReadOnly(new[] { reference }),
            PersonalityContract.MaximumUtf8Bytes, contextRevision, exclusionRevision);
    }
}
