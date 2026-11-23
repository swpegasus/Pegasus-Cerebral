using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

/// <summary>Owner-only chat path rules. The effective working path is a separately revisioned owner preference.</summary>
internal sealed class ConversationPathGrants(StateStore store, WorkerWorkflows workflows, EventHub events)
{
    internal const string RecordKind = "conversation-path-grants";
    internal const int MaximumExternalGrants = 32;
    private const int PreparedSchemaVersion = 2;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public sealed record Initial(string FirstRequestId, string ProfileId, int ExpectedProfileRevision,
        string? ProjectId, string? WorkingDirectory);
    internal sealed record Preparation(Initial Initial, long Revision, DateTimeOffset UpdatedAt,
        IReadOnlyList<ScopedPathGrant> Grants, string ProfileFingerprint);
    internal sealed record Stored(int SchemaVersion, string ProfileId, string? ProjectId, long Revision,
        DateTimeOffset UpdatedAt, IReadOnlyList<ScopedPathGrant> Grants)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Preparation? InitialPreparation { get; init; }
    }
    public sealed record Grant(string Path, string Access);
    public sealed record Snapshot(string Id, string ProfileId, string? ProjectId, string RootState, string? Root,
        long Revision, DateTimeOffset? UpdatedAt, IReadOnlyList<Grant> Grants, IReadOnlyList<Grant> EffectiveGrants)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Initial? Initial { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InitialState { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SharedProjectsRoot { get; init; }
    }

    public Snapshot Get(string id)
    {
        ValidateId(id);
        return store.Locked(() =>
        {
            var value = ReadStored(store, id);
            if (store.GetSessionMetadata(id) is not null)
                throw new NotSupportedException("Chat path rules require a native conversation.");
            var conversation = workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, id);
            if (conversation is null && value?.InitialPreparation is { } preparation)
                return Project(id, value.ProfileId, value.ProjectId, preparation.Initial.WorkingDirectory, value, prepared: true);
            return Project(EnsureConversation(id), value);
        });
    }

    public Snapshot Save(string id, long expectedRevision, IReadOnlyList<Grant> grants, Initial? initial = null)
    {
        ValidateId(id);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count > MaximumExternalGrants) throw new ArgumentException("A chat supports at most 32 explicit path rules.");
        var normalized = Normalize(grants.Select(grant => grant is null ? throw new ArgumentException("A path rule is required.") :
            new ScopedPathGrant(grant.Path, grant.Access switch { "read" => ScopedPathAccess.Read, "write" => ScopedPathAccess.Write,
                _ => throw new ArgumentException("Path access must be exactly read or write.") })).ToArray());
        if (initial is not null) return Prepare(id, expectedRevision, normalized, NormalizeInitial(initial));
        var saved = store.Locked(() =>
        {
            var conversation = EnsureConversation(id);
            var current = ReadStored(store, id);
            ValidateStoredIdentity(current, conversation.ProfileId, conversation.ProjectId);
            if ((current?.Revision ?? 0) != expectedRevision)
                throw new WorkflowConflictException("Chat path rules changed. Refresh them before saving.");
            var root = Root(store, workflows.Store, conversation.Id, conversation.ProfileId, conversation.ProjectId);
            var shared = Configuration.RegisteredProjectsRoot(store);
            if (IsSeparateRoot(root, shared) && normalized.Grants.Any(grant => Paths.Equals(root, grant.Path)))
                throw new ArgumentException("The current conversation working path is already a read/write entry.");
            if ((current is not null || root is not null) && Equivalent(current?.Grants ?? [], normalized.Grants))
                return (Value: Project(conversation, current), Changed: false);
            var next = new Stored(current?.SchemaVersion ?? ScopedPathAccessPolicy.Version, conversation.ProfileId, conversation.ProjectId,
                checked(expectedRevision + 1), DateTimeOffset.UtcNow, normalized.Grants)
            { InitialPreparation = current?.InitialPreparation };
            var projection = Project(conversation, next);
            RequireStoredBound(next);
            store.Put(RecordKind, id, next);
            return (Value: projection, Changed: true);
        });
        if (saved.Changed) events.Publish("conversation.paths.updated", null, new { conversationId = id, revision = saved.Value.Revision });
        return saved.Value;
    }

    private Snapshot Prepare(string id, long expectedRevision, ScopedPathAccessScope scope, Initial initial)
    {
        if (expectedRevision != 0) throw new WorkflowConflictException("Initial chat authority requires revision zero.");
        var result = store.Locked(() =>
        {
            var current = ReadStored(store, id);
            // The immutable receipt wins before current profile checks, including after acceptance.
            if (current?.InitialPreparation is { } prior)
            {
                if (!SameInitial(prior.Initial, initial) || !Equivalent(prior.Grants, scope.Grants))
                    throw new WorkflowConflictException("This draft already has a different immutable first submission preparation. Start a new conversation.");
                var receipt = current with { Revision = prior.Revision, UpdatedAt = prior.UpdatedAt, Grants = prior.Grants };
                var original = workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id);
                if (original is not null && (original.ProfileId != current.ProfileId || original.ProjectId != current.ProjectId))
                    throw new InvalidDataException("The prepared receipt conflicts with its original accepted owner.");
                var receiptRoot = original is null ? initial.WorkingDirectory : ConversationRootPolicy.ReadDirectory(original.Input);
                return (Value: Project(id, current.ProfileId, current.ProjectId, receiptRoot, receipt, prepared: original is null), Changed: false);
            }
            if (current is not null || store.GetSessionMetadata(id) is not null ||
                workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, id) is not null ||
                workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id) is not null)
                throw new WorkflowConflictException("Initial authority requires a fresh native conversation.");
            if (workflows.Store.TryGet(initial.FirstRequestId) is not null)
                throw new WorkflowConflictException("The first request identifier already belongs to accepted work.");
            var profile = RequireInitialProfile(store, initial);
            var shared = Configuration.RegisteredProjectsRoot(store);
            var effective = Scope(initial.WorkingDirectory, scope.Grants, shared);
            RequireWithinProfile(effective, profile, shared);
            if (IsSeparateRoot(initial.WorkingDirectory, shared) &&
                scope.Grants.Any(grant => Paths.Equals(initial.WorkingDirectory, grant.Path)))
                throw new ArgumentException("The initial working directory is an immutable read/write entry.");
            var now = DateTimeOffset.UtcNow;
            var preparation = new Preparation(initial, 1, now, scope.Grants, ProfileFingerprint(profile));
            var next = new Stored(PreparedSchemaVersion, initial.ProfileId, initial.ProjectId, 1, now, scope.Grants)
            { InitialPreparation = preparation };
            var projection = Project(id, next.ProfileId, next.ProjectId, initial.WorkingDirectory, next, prepared: true);
            RequireStoredBound(next);
            store.Put(RecordKind, id, next);
            return (Value: projection, Changed: true);
        });
        if (result.Changed) events.Publish("conversation.paths.updated", null, new { conversationId = id, revision = result.Value.Revision });
        return result.Value;
    }

    private ConversationSummary EnsureConversation(string id)
    {
        if (store.GetSessionMetadata(id) is not null)
            throw new NotSupportedException("Chat path rules require a saved native conversation.");
        var conversation = workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, id)
            ?? throw new KeyNotFoundException("The saved native conversation was not found.");
        if (workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id) is null)
            throw new NotSupportedException("Inherited conversations use their original owner chat's path rules.");
        return conversation;
    }

    private Snapshot Project(ConversationSummary conversation, Stored? value)
    {
        ValidateStoredIdentity(value, conversation.ProfileId, conversation.ProjectId);
        return Project(conversation.Id, conversation.ProfileId, conversation.ProjectId,
            Root(store, workflows.Store, conversation.Id, conversation.ProfileId, conversation.ProjectId), value);
    }

    private Snapshot Project(string id, string profileId, string? projectId, string? root, Stored? value, bool prepared = false)
    {
        ValidateStoredIdentity(value, profileId, projectId);
        var shared = Configuration.RegisteredProjectsRoot(store);
        var scope = Scope(root, value?.Grants ?? [], shared);
        if (value?.InitialPreparation is not null)
            scope = store.Get<AgentProfile>("agent", profileId) is { WorkerId: "local" } profile
                ? ConversationPathAuthority.Intersect(scope, ProfileScope(profile, shared)) : Normalize([]);
        var result = new Snapshot(id, profileId, projectId, prepared ? "prepared" : root is null ? "unbound" : "bound", root,
            value?.Revision ?? 0, value?.UpdatedAt, Wire(value?.Grants ?? []), Wire(scope.Grants))
        {
            Initial = value?.InitialPreparation?.Initial,
            InitialState = value?.InitialPreparation is null ? null : prepared ? "prepared" : "accepted",
            SharedProjectsRoot = shared
        };
        if (JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > 256 * 1024)
            throw new InvalidDataException("Chat path rules exceed the bounded owner projection.");
        return result;
    }

    internal static Initial NormalizeInitial(Initial initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ValidateId(initial.FirstRequestId); ValidateId(initial.ProfileId);
        if (initial.ExpectedProfileRevision < 1) throw new ArgumentException("An exact positive profile revision is required.");
        if (initial.ProjectId is not null) ValidateId(initial.ProjectId);
        string? root = null;
        if (initial.WorkingDirectory is not null)
        {
            root = ConversationRootPolicy.ReadDirectory(CerebralJson.Element(new { workingDirectory = initial.WorkingDirectory }))
                ?? throw new ArgumentException("An initial working directory must be an absolute path.");
        }
        return initial with { WorkingDirectory = root };
    }

    internal static bool SameInitial(Initial? left, Initial? right) =>
        left is null || right is null ? left == right :
        left.FirstRequestId == right.FirstRequestId && left.ProfileId == right.ProfileId &&
        left.ExpectedProfileRevision == right.ExpectedProfileRevision && left.ProjectId == right.ProjectId &&
        (left.WorkingDirectory is null || right.WorkingDirectory is null ? left.WorkingDirectory == right.WorkingDirectory :
            Paths.Equals(left.WorkingDirectory, right.WorkingDirectory));

    internal static AgentProfile RequireInitialProfile(StateStore store, Initial initial)
    {
        var profile = store.Get<AgentProfile>("agent", initial.ProfileId)
            ?? throw new ArgumentException("The selected initial profile was not found.");
        if (profile.Id != initial.ProfileId || profile.WorkerId != "local" || profile.Role is not ("lead" or "helper"))
            throw new UnauthorizedAccessException("Initial native chat requires the selected existing local lead or helper profile.");
        if (profile.Revision != initial.ExpectedProfileRevision)
            throw new WorkflowConflictException("The selected initial profile revision changed. Start a new conversation.");
        return profile;
    }

    internal static string ProfileFingerprint(AgentProfile profile) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile, CerebralJson.Options))).ToLowerInvariant();

    internal static ScopedPathAccessScope ProfileScope(AgentProfile profile, string? shared = null) =>
        Scope(null, Normalize(profile.AllowedRoots.Select(root => new ScopedPathGrant(root, ScopedPathAccess.Write))
            .ToArray()).Grants, profile.WorkerId == "local" ? shared : null);

    internal static void RequireWithinProfile(ScopedPathAccessScope scope, AgentProfile profile, string? shared = null)
    {
        var ceiling = ProfileScope(profile, shared);
        if (scope.Grants.Any(grant => !ceiling.Grants.Any(root => ScopedFilePolicy.IsWithin(grant.Path, root.Path))))
            throw new UnauthorizedAccessException("Initial chat authority exceeds the selected profile's finite roots.");
    }

    private static void RequireStoredBound(Stored value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options).Length > 256 * 1024)
            throw new InvalidDataException("Stored chat path preparation exceeds its bounded metadata limit.");
    }

    internal static Stored? ReadStored(StateStore store, string id)
    {
        ValidateId(id);
        var value = store.Get<Stored>(RecordKind, id);
        if (value is null) return null;
        if (value.SchemaVersion is not (ScopedPathAccessPolicy.Version or PreparedSchemaVersion) ||
            value.Revision < 1 || value.UpdatedAt == default || value.Grants is null || value.Grants.Count > MaximumExternalGrants ||
            (value.SchemaVersion == PreparedSchemaVersion) != (value.InitialPreparation is not null))
            throw new InvalidDataException("Stored chat path rules have invalid metadata or size.");
        ValidateId(value.ProfileId);
        if (value.ProjectId is not null) ValidateId(value.ProjectId);
        var normalized = value with { Grants = Normalize(value.Grants).Grants };
        if (value.InitialPreparation is { } preparation)
        {
            var initial = NormalizeInitial(preparation.Initial);
            if (initial.ProfileId != value.ProfileId || initial.ProjectId != value.ProjectId || preparation.Revision != 1 ||
                preparation.UpdatedAt == default || preparation.Revision > value.Revision || preparation.Grants is null ||
                preparation.Grants.Count > MaximumExternalGrants || preparation.ProfileFingerprint is not { Length: 64 } ||
                preparation.ProfileFingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Stored initial chat preparation has invalid identity or bounds.");
            normalized = normalized with { InitialPreparation = preparation with { Initial = initial, Grants = Normalize(preparation.Grants).Grants } };
        }
        if (normalized.InitialPreparation is not null) RequireStoredBound(normalized);
        return normalized;
    }

    internal static void ValidateStoredIdentity(Stored? value, string profileId, string? projectId)
    {
        if (value is not null && (value.ProfileId != profileId || value.ProjectId != projectId))
            throw new InvalidDataException("Stored chat path rules do not match the saved conversation identity.");
    }

    // A working-path edit changes the path editor's effective scope even when its explicit
    // grants do not change. Advance its CAS generation under the same state gate as Properties.
    internal static long AdvanceForWorkingDirectoryChange(StateStore store, string conversationId,
        string profileId, string? projectId)
    {
        var current = ReadStored(store, conversationId);
        ValidateStoredIdentity(current, profileId, projectId);
        var next = new Stored(current?.SchemaVersion ?? ScopedPathAccessPolicy.Version, profileId, projectId,
            checked((current?.Revision ?? 0) + 1), DateTimeOffset.UtcNow, current?.Grants ?? [])
        { InitialPreparation = current?.InitialPreparation };
        RequireStoredBound(next);
        store.Put(RecordKind, conversationId, next);
        return next.Revision;
    }

    internal static string? Root(StateStore store, WorkflowStore engine, string conversationId, string profileId, string? projectId)
    {
        var original = engine.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, conversationId);
        if (original is not null && (original.ProfileId != profileId || original.ProjectId != projectId))
            throw new InvalidDataException("The original conversation directory has conflicting identity.");
        var current = ConversationProperties.ReadAdmissionSettings(store, conversationId);
        return current.WorkingDirectoryOverrideSet ? current.WorkingDirectory :
            original is null ? null : ConversationRootPolicy.ReadDirectory(original.Input);
    }

    private static bool IsSeparateRoot(string? root, string? shared) =>
        root is not null && (shared is null || !ScopedFilePolicy.IsWithin(root, shared));

    internal static ScopedPathAccessScope Scope(string? root, IReadOnlyList<ScopedPathGrant> grants, string? shared = null) =>
        Normalize(ScopedPathAccessPolicy.IncludeChatDefaults(root, shared, grants));
    internal static ScopedPathAccessScope Normalize(IReadOnlyList<ScopedPathGrant> grants) =>
        ScopedPathAccessPolicy.Normalize(new(ScopedPathAccessPolicy.Version, grants));
    private static IReadOnlyList<Grant> Wire(IReadOnlyList<ScopedPathGrant> grants) =>
        grants.Select(grant => new Grant(grant.Path, grant.Access == ScopedPathAccess.Read ? "read" : "write")).ToArray();
    internal static bool Equivalent(IReadOnlyList<ScopedPathGrant> left, IReadOnlyList<ScopedPathGrant> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => Paths.Equals(pair.First.Path, pair.Second.Path) && pair.First.Access == pair.Second.Access);
    internal static void ValidateId(string id) { ArgumentException.ThrowIfNullOrWhiteSpace(id); Configuration.ValidateId(id); }
}

/// <summary>Private immutable path authority coordinates. Inherited runs retain the source chat, not their own settings.</summary>
internal static class ConversationPathAuthority
{
    internal sealed record Binding(int SchemaVersion, string SourceRunId, string SourceConversationId, string SourceProfileId,
        string? SourceProjectId, long Revision, ScopedPathAccessScope Scope, bool LegacySource = false)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ConversationPathGrants.Initial? Initial { get; init; }
    }
    internal sealed record Admission(Binding? Binding, string? SourceConversationId, long? Revision)
    {
        public ConversationPathGrants.Initial? FreshInitial { get; init; }
        public string? PreparationFingerprint { get; init; }
    }
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static Binding Validate(Binding value)
    {
        if (value.SchemaVersion != ScopedPathAccessPolicy.Version || value.Revision < 0)
            throw new InvalidDataException("The accepted chat path binding has invalid metadata.");
        ConversationPathGrants.ValidateId(value.SourceRunId); ConversationPathGrants.ValidateId(value.SourceConversationId);
        ConversationPathGrants.ValidateId(value.SourceProfileId);
        if (value.SourceProjectId is not null) ConversationPathGrants.ValidateId(value.SourceProjectId);
        var initial = value.Initial is null ? null : ConversationPathGrants.NormalizeInitial(value.Initial);
        if (initial is not null && (initial.ProfileId != value.SourceProfileId || initial.ProjectId != value.SourceProjectId))
            throw new InvalidDataException("The accepted initial path authority has conflicting owner identity.");
        return value with { Scope = ScopedPathAccessPolicy.Normalize(value.Scope), Initial = initial };
    }

    internal static Binding Read(JsonElement value) => Validate(value.Deserialize<Binding>(new JsonSerializerOptions(CerebralJson.Options)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
        ?? throw new InvalidDataException("The accepted chat path binding is missing."));

    internal static Admission Capture(StateStore store, WorkflowStore engine, SubmitWorkflowRequest input,
        EventActor caller, string? projectId, string? parentRunId, bool fullAccess)
    {
        if (parentRunId is not null)
        {
            var parent = engine.Get(parentRunId).Submission;
            var snapshot = WorkerExecutionSnapshot.Read(parent.SettingsSnapshot);
            var inheritedScope = Resolve(store, engine, parent, snapshot, fullAccess);
            var source = AuthoritySource(store, engine, parent);
            var current = ConversationPathGrants.ReadStored(store, source.ConversationId);
            if (inheritedScope is null) return new(null, source.ConversationId, current?.Revision ?? 0);
            var binding = snapshot.ConversationPaths is { } accepted
                ? Read(accepted) with { Scope = inheritedScope, Revision = current?.Revision ?? 0 }
                : new Binding(ScopedPathAccessPolicy.Version, source.RequestId, source.ConversationId, source.ProfileId,
                    source.ProjectId, current?.Revision ?? 0, inheritedScope, LegacySource: true);
            binding = Validate(binding);
            if (binding.Initial is not null)
            {
                var selected = store.Get<AgentProfile>("agent", input.ProfileId)
                    ?? throw new UnauthorizedAccessException("The selected descendant profile is unavailable.");
                binding = binding with { Scope = Intersect(binding.Scope,
                    ConversationPathGrants.ProfileScope(selected, Configuration.RegisteredProjectsRoot(store))) };
            }
            return new(binding, binding.SourceConversationId, binding.Revision);
        }
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId) return new(null, null, null);
        if (store.GetSessionMetadata(input.ConversationId) is not null) return new(null, null, null);
        var conversation = engine.GetConversation(ConversationWorkflowModule.DefinitionId, input.ConversationId);
        if (conversation is not null && conversation.ProfileId != input.ProfileId)
            throw new UnauthorizedAccessException("The saved conversation belongs to another path owner.");
        var original = engine.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, input.ConversationId);
        var root = original is null
            ? caller.Kind == EventActorKind.User ? ConversationRootPolicy.ReadDirectory(input.Input) : null
            : ConversationPathGrants.Root(store, engine, input.ConversationId, input.ProfileId, original.ProjectId);
        var currentRules = ConversationPathGrants.ReadStored(store, input.ConversationId);
        var boundProject = conversation?.ProjectId ?? original?.ProjectId ?? projectId;
        if (original is not null && original.ProfileId != input.ProfileId)
            throw new UnauthorizedAccessException("The original chat path owner does not match this profile.");
        ConversationPathGrants.ValidateStoredIdentity(currentRules, input.ProfileId, boundProject);
        // Existing unbound chats keep their profile-only authority until an owner explicitly saves rules.
        var shared = Configuration.RegisteredProjectsRoot(store);
        if (root is null && currentRules is null && shared is null) return new(null, input.ConversationId, 0);
        var initial = currentRules?.InitialPreparation?.Initial;
        string? preparationFingerprint = null;
        if (initial is not null && original is null)
        {
            if (caller.Kind != EventActorKind.User || caller.Id != "owner" || input.RequestId != initial.FirstRequestId ||
                input.ProfileId != initial.ProfileId || boundProject != initial.ProjectId || !SamePath(root, initial.WorkingDirectory))
                throw new UnauthorizedAccessException("The first submission does not match its exact owner-prepared draft.");
            var selected = ConversationPathGrants.RequireInitialProfile(store, initial);
            if (ConversationPathGrants.ProfileFingerprint(selected) != currentRules!.InitialPreparation!.ProfileFingerprint)
                throw new WorkflowConflictException("The selected profile changed after draft preparation. Start a new conversation.");
            ConversationPathGrants.RequireWithinProfile(ConversationPathGrants.Scope(root, currentRules.Grants, shared), selected, shared);
            preparationFingerprint = StoredFingerprint(currentRules);
        }
        else if (original is not null)
        {
            var originalInitial = original.SettingsSnapshot.TryGetProperty("conversationPaths", out var originalPaths) &&
                originalPaths.ValueKind != JsonValueKind.Null ? Read(originalPaths).Initial : null;
            if (!ConversationPathGrants.SameInitial(initial, originalInitial))
                throw new UnauthorizedAccessException("Current initial path authority does not match the original saved conversation.");
        }
        var scope = ConversationPathGrants.Scope(root, currentRules?.Grants ?? [], shared);
        if (initial is not null)
        {
            var selected = store.Get<AgentProfile>("agent", input.ProfileId)
                ?? throw new UnauthorizedAccessException("The prepared conversation profile is unavailable.");
            scope = Intersect(scope, ConversationPathGrants.ProfileScope(selected, shared));
        }
        var captured = new Binding(ScopedPathAccessPolicy.Version, input.RequestId, input.ConversationId, input.ProfileId,
            boundProject, currentRules?.Revision ?? 0, scope) { Initial = initial };
        return new(Validate(captured), input.ConversationId, captured.Revision)
        { FreshInitial = original is null ? initial : null, PreparationFingerprint = preparationFingerprint };
    }

    internal static void Recheck(StateStore store, Admission? admission)
    {
        if (admission?.SourceConversationId is { } id && admission.Revision is { } expected &&
            (ConversationPathGrants.ReadStored(store, id)?.Revision ?? 0) != expected)
            throw new WorkflowConflictException("Chat path rules changed during admission. Retry the new request with current rules.");
        if (admission?.FreshInitial is { } initial && admission.SourceConversationId is { } preparedId)
        {
            var current = ConversationPathGrants.ReadStored(store, preparedId)
                ?? throw new WorkflowConflictException("The initial chat preparation disappeared during admission.");
            var profile = ConversationPathGrants.RequireInitialProfile(store, initial);
            if (StoredFingerprint(current) != admission.PreparationFingerprint || current.InitialPreparation is null ||
                ConversationPathGrants.ProfileFingerprint(profile) != current.InitialPreparation.ProfileFingerprint)
                throw new WorkflowConflictException("The initial chat authority or selected profile changed during admission.");
        }
    }

    private static bool SamePath(string? left, string? right) => left is null || right is null ? left == right : Paths.Equals(left, right);
    private static string StoredFingerprint(ConversationPathGrants.Stored value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options))).ToLowerInvariant();

    internal static JsonElement Bind(JsonElement snapshot, Binding? binding)
    {
        if (binding is null) return snapshot;
        var node = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        node["conversationPaths"] = JsonSerializer.SerializeToNode(Validate(binding), CerebralJson.Options);
        var value = JsonSerializer.SerializeToElement(node, CerebralJson.Options);
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > WorkflowContract.MaximumStateBytes)
            throw new InvalidOperationException("Accepted chat path rules exceed the workflow settings limit.");
        return value;
    }

    internal static ScopedPathAccessScope? Resolve(StateStore store, WorkflowStore engine, WorkflowSubmission submission,
        WorkerExecutionSnapshot.Snapshot snapshot, bool fullAccess)
    {
        var source = AuthoritySource(store, engine, submission);
        if (source.DefinitionId != ConversationWorkflowModule.DefinitionId)
        {
            if (snapshot.ConversationPaths is not null)
                throw new UnauthorizedAccessException("Chat path bindings require a native conversation source.");
            return null;
        }
        if (snapshot.ConversationPaths is { } raw)
        {
            // A tool context is not an authority source. Bind the projection to the exact
            // immutable accepted submission before using any of its path entries.
            var retained = engine.Get(submission.RequestId).Submission;
            var retainedPaths = WorkerExecutionSnapshot.Read(retained.SettingsSnapshot).ConversationPaths;
            if (retained.ProfileId != submission.ProfileId || retained.ConversationId != submission.ConversationId ||
                retained.ProjectId != submission.ProjectId || retained.DefinitionId != submission.DefinitionId ||
                retained.ParentRunId != submission.ParentRunId || retainedPaths is null || !JsonElement.DeepEquals(retainedPaths.Value, raw))
                throw new UnauthorizedAccessException("The path binding does not match the exact saved submission.");
            var binding = Read(raw);
            if (binding.SourceRunId != source.RequestId || binding.SourceConversationId != source.ConversationId ||
                binding.SourceProfileId != source.ProfileId || binding.SourceProjectId != source.ProjectId || submission.ProjectId != source.ProjectId)
                throw new UnauthorizedAccessException("The accepted chat path binding belongs to a different source lineage.");
            var current = CurrentScope(store, engine, binding.SourceConversationId, binding.SourceProfileId, binding.SourceProjectId);
            var scope = Intersect(binding.Scope, current);
            if (source.RequestId != submission.RequestId)
            {
                var parentSnapshot = WorkerExecutionSnapshot.Read(source.SettingsSnapshot);
                if (parentSnapshot.ConversationPaths is { } parentBinding)
                {
                    var acceptedSource = Read(parentBinding);
                    if (acceptedSource.SourceRunId != source.RequestId || acceptedSource.SourceConversationId != source.ConversationId ||
                        acceptedSource.SourceProfileId != source.ProfileId || acceptedSource.SourceProjectId != source.ProjectId)
                        throw new UnauthorizedAccessException("The source path binding has conflicting lineage.");
                    scope = Intersect(scope, acceptedSource.Scope);
                }
                else
                {
                    if (!binding.LegacySource) throw new UnauthorizedAccessException("Inherited path authority lacks an accepted source binding.");
                    var legacy = LegacyScope(store, source, parentSnapshot, fullAccess);
                    if (legacy is not null) scope = Intersect(scope, legacy);
                }
            }
            return binding.Initial is null ? scope : ApplyInitialProfileCeilings(store, engine, submission, snapshot, source, binding, scope);
        }
        // Old frozen runs never gain a newly saved external rule. Current rules may only narrow
        // their accepted/current roots (or their already accepted and still active Full Access).
        var saved = ConversationPathGrants.ReadStored(store, source.ConversationId);
        if (saved is null) return null;
        var narrowed = CurrentScope(store, engine, source.ConversationId, source.ProfileId, source.ProjectId, includeShared: false);
        var ownLegacy = LegacyScope(store, submission, snapshot, fullAccess);
        if (ownLegacy is not null) narrowed = Intersect(narrowed, ownLegacy);
        if (source.RequestId != submission.RequestId)
        {
            var parentSnapshot = WorkerExecutionSnapshot.Read(source.SettingsSnapshot);
            var sourceLegacy = parentSnapshot.ConversationPaths is { } accepted ? Read(accepted).Scope : LegacyScope(store, source, parentSnapshot, fullAccess);
            if (sourceLegacy is not null) narrowed = Intersect(narrowed, sourceLegacy);
        }
        return narrowed;
    }

    private static ScopedPathAccessScope CurrentScope(StateStore store, WorkflowStore engine, string id, string profileId,
        string? projectId, bool includeShared = true)
    {
        if (store.GetSessionMetadata(id) is not null) throw new UnauthorizedAccessException("Native chat path authority cannot use a legacy session.");
        var conversation = engine.GetConversation(ConversationWorkflowModule.DefinitionId, id)
            ?? throw new UnauthorizedAccessException("The source native conversation is unavailable.");
        if (conversation.ProfileId != profileId || conversation.ProjectId != projectId)
            throw new UnauthorizedAccessException("Current chat path rules do not match their saved source identity.");
        if (store.Get<AgentProfile>("agent", profileId) is not { } profile || profile.Id != profileId)
            throw new UnauthorizedAccessException("The source chat path owner profile is unavailable.");
        var stored = ConversationPathGrants.ReadStored(store, id);
        ConversationPathGrants.ValidateStoredIdentity(stored, profileId, projectId);
        return ConversationPathGrants.Scope(ConversationPathGrants.Root(store, engine, id, profileId, projectId),
            stored?.Grants ?? [], includeShared ? Configuration.RegisteredProjectsRoot(store) : null);
    }

    private static ScopedPathAccessScope? LegacyScope(StateStore store, WorkflowSubmission submission,
        WorkerExecutionSnapshot.Snapshot snapshot, bool fullAccess) => snapshot.FullAccess && fullAccess ? null :
        ConversationPathGrants.Normalize(WorkerExecutionSnapshot.EffectiveProfile(store, snapshot, submission.ProfileId)
            .AllowedRoots.Select(path => new ScopedPathGrant(path, ScopedPathAccess.Write)).ToArray());

    private static WorkflowSubmission AuthoritySource(StateStore store, WorkflowStore engine, WorkflowSubmission submission)
    {
        var source = submission;
        if (source.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" })
            source = PluginContinuationGoal.PathAuthoritySource(store, engine, source);
        if (source.DefinitionId == WorkflowDataModule.DefinitionId)
        {
            var input = WorkerWorkflows.Read<WorkflowDataRunInput>(source.Input);
            source = engine.Get(input.SourceRequestId).Submission;
            if (source.DefinitionId == WorkflowDataModule.DefinitionId || source.ProfileId != submission.ProfileId || source.ProjectId != submission.ProjectId)
                throw new UnauthorizedAccessException("The data run has an invalid source path lineage.");
        }
        if (source.ParentRunId is { } parentId)
        {
            var parent = engine.Get(parentId).Submission;
            if (parent.ParentRunId is not null || parent.ProjectId != source.ProjectId ||
                source.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" })
                throw new UnauthorizedAccessException("The delegated run has an invalid source path lineage.");
            source = parent;
        }
        return source;
    }

    private static ScopedPathAccessScope ApplyInitialProfileCeilings(StateStore store, WorkflowStore engine,
        WorkflowSubmission submission, WorkerExecutionSnapshot.Snapshot snapshot, WorkflowSubmission source,
        Binding binding, ScopedPathAccessScope scope)
    {
        var original = engine.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, source.ConversationId)
            ?? throw new UnauthorizedAccessException("The initial path authority has no original saved conversation.");
        var originalSnapshot = WorkerExecutionSnapshot.Read(original.SettingsSnapshot);
        var originalInitial = originalSnapshot.ConversationPaths is { } originalPaths ? Read(originalPaths).Initial : null;
        if (!ConversationPathGrants.SameInitial(binding.Initial, originalInitial) || originalInitial is null ||
            original.RequestId != originalInitial.FirstRequestId || original.ProfileId != originalInitial.ProfileId ||
            original.ProjectId != originalInitial.ProjectId ||
            !SamePath(ConversationRootPolicy.ReadDirectory(original.Input), originalInitial.WorkingDirectory))
            throw new UnauthorizedAccessException("Initial chat authority does not match its immutable first submission.");
        var current = store.Get<AgentProfile>("agent", submission.ProfileId)
            ?? throw new UnauthorizedAccessException("The accepted profile is unavailable.");
        if (current.Id != submission.ProfileId || current.WorkerId != "local")
            throw new UnauthorizedAccessException("The initial chat local execution binding changed.");
        var shared = Configuration.RegisteredProjectsRoot(store);
        scope = Intersect(scope, ConversationPathGrants.ProfileScope(snapshot.Profile, shared));
        scope = Intersect(scope, ConversationPathGrants.ProfileScope(current, shared));
        if (source.RequestId != submission.RequestId)
        {
            var sourceSnapshot = WorkerExecutionSnapshot.Read(source.SettingsSnapshot);
            var currentSource = store.Get<AgentProfile>("agent", source.ProfileId)
                ?? throw new UnauthorizedAccessException("The initial source profile is unavailable.");
            if (currentSource.Id != source.ProfileId || currentSource.WorkerId != "local")
                throw new UnauthorizedAccessException("The initial source local execution binding changed.");
            scope = Intersect(scope, ConversationPathGrants.ProfileScope(sourceSnapshot.Profile, shared));
            scope = Intersect(scope, ConversationPathGrants.ProfileScope(currentSource, shared));
        }
        return scope;
    }

    // Both piecewise policies must permit a path. Retain every canonical boundary, including
    // nested read-only exceptions; merging only positive roots would resurrect write authority.
    internal static ScopedPathAccessScope Intersect(ScopedPathAccessScope left, ScopedPathAccessScope right)
    {
        var a = ScopedPathAccessPolicy.Normalize(left); var b = ScopedPathAccessPolicy.Normalize(right);
        ScopedPathGrant? At(ScopedPathAccessScope scope, string path) => scope.Grants
            .Where(grant => ScopedFilePolicy.IsWithin(path, grant.Path)).OrderByDescending(grant => grant.Path.Length).FirstOrDefault();
        var result = new List<ScopedPathGrant>();
        foreach (var path in a.Grants.Concat(b.Grants).Select(grant => grant.Path).Distinct(Paths))
        {
            var first = At(a, path); var second = At(b, path);
            if (first is not null && second is not null)
                result.Add(new(path, first.Access == ScopedPathAccess.Read || second.Access == ScopedPathAccess.Read ? ScopedPathAccess.Read : ScopedPathAccess.Write));
        }
        return ConversationPathGrants.Normalize(result);
    }
}
