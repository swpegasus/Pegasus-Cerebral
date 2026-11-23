using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

/// <summary>Private owner presentation/preferences. File grants remain profile-owned;
/// a saved working path affects future admissions only.</summary>
internal sealed class ConversationProperties(StateStore store, WorkerWorkflows workflows,
    EventHub events, IToolDispatcher tools, string? workerInstallationId = null)
{
    internal const string RecordKind = "conversation-properties";

    public sealed record Settings(long Revision, DateTimeOffset? UpdatedAt, int? ResponseSharePercent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ChatToolPermissions.Policy? ToolPermissionPolicy = null)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool WorkingDirectoryOverrideSet { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkingDirectory { get; init; }
    }
    public sealed record PermissionSnapshot(string Id, string WorkerId, string? WorkerInstallationId,
        string ProfileId, string? ProjectId,
        long SettingsRevision, int ProfileRevision, string AuthorityFingerprint,
        ChatToolPermissions.Policy? Policy);
    public sealed record CatalogItem(string Kind, string Id, string? Capability, string? Description,
        string? Source, string? DescriptorFingerprint, IReadOnlyList<string> SecondaryRequirements,
        bool Available, bool? ModelVisible, bool ProfileAllowed, string SavedChoice,
        string EffectiveState, string? EffectiveBlockedReason);
    public sealed record CatalogPage(string Id, string WorkerId, string? WorkerInstallationId,
        string ProfileId, string? ProjectId, long SettingsRevision,
        int ProfileRevision, string AuthorityFingerprint, string Generation, int Offset,
        IReadOnlyList<CatalogItem> Items, int? NextOffset, IReadOnlyList<string> UnavailableServers);
    public sealed record Snapshot(string Id, string Title, string ProfileId, string ProfileName,
        string ProfileRole, string? ProjectId, string RootState, string? Root,
        IReadOnlyList<string> CurrentRoots, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Tools,
        bool CurrentFullAccess, string PropertyState, string SettingsSource,
        long Revision, DateTimeOffset? UpdatedAt, int? ResponseSharePercent);

    public Snapshot Get(string id)
    {
        ValidateId(id);
        return store.Locked(() => Project(EnsureConversation(id), ReadStored(store, id)));
    }

    public Settings ReadSettings(string id)
    {
        ValidateId(id);
        return store.Locked(() => { EnsureConversation(id); return ReadStored(store, id) ?? new(0, null, null); });
    }

    // Admission may be the first native turn. Reading an absent preference never creates a chat.
    internal static Settings ReadAdmissionSettings(StateStore store, string id)
    {
        ValidateId(id);
        return ReadStored(store, id) ?? new(0, null, null);
    }

    public Snapshot Save(string id, long expectedRevision, int? responseSharePercent) =>
        Save(id, expectedRevision, responseSharePercent, workingDirectorySpecified: false, workingDirectory: null);

    public Snapshot Save(string id, long expectedRevision, int? responseSharePercent,
        bool workingDirectorySpecified, string? workingDirectory)
    {
        ValidateId(id);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ValidateShare(responseSharePercent);
        var selected = workingDirectorySpecified ? ConversationRootPolicy.NormalizeDirectory(workingDirectory) : null;
        var saved = store.Locked(() =>
        {
            var conversation = EnsureConversation(id);
            var current = ReadStored(store, id);
            if ((current?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("The conversation properties changed. Refresh them before saving.");
            if (selected is not null)
            {
                var profile = store.Get<AgentProfile>("agent", conversation.ProfileId)
                    ?? throw new KeyNotFoundException("The chat's profile is unavailable.");
                if (profile.Id != conversation.ProfileId || profile.WorkerId != "local")
                    throw new UnauthorizedAccessException("The chat's path owner profile is unavailable.");
                _ = ToolDispatcher.ValidateWorkingDirectory(selected, profile);
            }
            // A no-op neither creates an absent record nor changes revision/time. CAS still applies.
            if (current?.ResponseSharePercent == responseSharePercent &&
                (!workingDirectorySpecified || current?.WorkingDirectoryOverrideSet == true &&
                 SameDirectory(current.WorkingDirectory, selected)))
                return (Value: Project(conversation, current), Changed: false, PathsRevision: (long?)null);
            var next = new Settings(checked(expectedRevision + 1), DateTimeOffset.UtcNow,
                responseSharePercent, current?.ToolPermissionPolicy)
            {
                WorkingDirectoryOverrideSet = workingDirectorySpecified || current?.WorkingDirectoryOverrideSet == true,
                WorkingDirectory = workingDirectorySpecified ? selected : current?.WorkingDirectory
            };
            var projection = Project(conversation, next);
            long? pathsRevision = null;
            if (workingDirectorySpecified && !SameDirectory(current?.WorkingDirectoryOverrideSet == true
                    ? current.WorkingDirectory : ConversationPathGrants.Root(store, workflows.Store, id,
                        conversation.ProfileId, conversation.ProjectId), selected))
                pathsRevision = ConversationPathGrants.AdvanceForWorkingDirectoryChange(store, id,
                    conversation.ProfileId, conversation.ProjectId);
            store.Put(RecordKind, id, next);
            return (Value: projection, Changed: true, PathsRevision: pathsRevision);
        });
        if (saved.Changed)
            events.Publish("conversation.properties.updated", null, new { conversationId = id, revision = saved.Value.Revision });
        if (saved.PathsRevision is { } pathRevision)
            events.Publish("conversation.paths.updated", null, new { conversationId = id, revision = pathRevision });
        return saved.Value;
    }

    public PermissionSnapshot GetPermissions(string id)
    {
        ValidateId(id);
        return store.Locked(() =>
        {
            var (conversation, profile) = PermissionScope(id);
            return PermissionProjection(conversation, profile, ReadStored(store, id));
        });
    }

    public PermissionSnapshot SavePermissions(string id, long expectedRevision,
        string expectedAuthorityFingerprint, string expectedCatalogGeneration,
        IReadOnlyList<ChatToolPermissions.Patch> patches, RegisteredToolCatalog catalog)
    {
        ValidateId(id);
        if (expectedRevision < 0 || !ChatToolPermissions.ValidHex(expectedAuthorityFingerprint, 64) ||
            expectedCatalogGeneration != catalog.Generation || patches is null || patches.Count is < 1 or > 128)
            throw new ArgumentException("The exact chat, catalog and bounded permission patch are required.");
        var saved = store.Locked(() =>
        {
            var (conversation, profile) = PermissionScope(id);
            var current = ReadStored(store, id);
            if ((current?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("Chat Properties changed. Refresh before saving permissions.");
            var authority = ChatToolPermissions.AuthorityFingerprint(profile);
            if (authority != expectedAuthorityFingerprint)
                throw new InvalidOperationException("The profile's permission authority changed. Refresh before saving.");
            var prior = current?.ToolPermissionPolicy;
            if (prior is not null) ChatToolPermissions.Validate(prior, id, profile.Id, conversation.ProjectId);
            var toolChoices = prior?.Tools.ToDictionary(x => x.Id, StringComparer.Ordinal) ??
                new Dictionary<string, ChatToolPermissions.Choice>(StringComparer.Ordinal);
            var capChoices = prior?.Capabilities.ToDictionary(x => x.Id, StringComparer.Ordinal) ??
                new Dictionary<string, ChatToolPermissions.Choice>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var patch in patches)
            {
                if (patch is null || patch.Kind is not ("tool" or "capability") ||
                    !ChatToolPermissions.ValidId(patch.Id) || patch.Id == "*" ||
                    patch.Decision is not (ChatToolPermissions.Inherit or ChatToolPermissions.Allow or ChatToolPermissions.Deny) ||
                    !seen.Add(patch.Kind + ":" + patch.Id))
                    throw new ArgumentException("Permission changes require distinct exact tool or capability IDs.");
                var choices = patch.Kind == "tool" ? toolChoices : capChoices;
                if (patch.Decision == ChatToolPermissions.Inherit)
                {
                    if (patch.DescriptorFingerprint is not null) throw new ArgumentException("Inheritance has no descriptor binding.");
                    choices.Remove(patch.Id);
                    continue;
                }
                var registered = patch.Kind == "tool"
                    ? catalog.Tools.SingleOrDefault(x => x.Descriptor.Name == patch.Id) : null;
                if (patch.Kind == "tool" && registered is null ||
                    patch.Kind == "capability" && !ChatToolPermissions.IsToolCapability(patch.Id, catalog) &&
                    !profile.Capabilities.Contains(patch.Id, StringComparer.Ordinal))
                    throw new ArgumentException("A new chat permission must name a currently registered tool or capability.");
                if (patch.Decision == ChatToolPermissions.Allow)
                {
                    if (patch.Kind == "tool" && patch.DescriptorFingerprint != registered!.SourceFingerprint ||
                        patch.Kind == "capability" && patch.DescriptorFingerprint is not null)
                        throw new ArgumentException("The approved tool descriptor changed. Refresh the catalog.");
                    if (patch.Kind == "capability" &&
                        !profile.Capabilities.Contains(patch.Id, StringComparer.Ordinal) &&
                        !ChatToolPermissions.CanAddCapability(patch.Id, catalog))
                        throw new UnauthorizedAccessException("This secondary capability remains a profile ceiling.");
                    choices[patch.Id] = new(patch.Id, ChatToolPermissions.Allow, authority,
                        patch.Kind == "tool" ? registered!.SourceFingerprint : null);
                }
                else
                {
                    if (patch.DescriptorFingerprint is not null) throw new ArgumentException("A denial has no descriptor binding.");
                    choices[patch.Id] = new(patch.Id, ChatToolPermissions.Deny, null, null);
                }
            }
            var tools = toolChoices.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var capabilities = capChoices.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            if (prior is null && tools.Length == 0 && capabilities.Length == 0 ||
                prior is not null && prior.Tools.SequenceEqual(tools) && prior.Capabilities.SequenceEqual(capabilities))
                return (Value: PermissionProjection(conversation, profile, current), Changed: false);
            var nextPolicy = new ChatToolPermissions.Policy(ChatToolPermissions.Schema, id, profile.Id,
                conversation.ProjectId, checked((prior?.Generation ?? 0) + 1), Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow, ChatToolPermissions.OwnerProvenance, tools, capabilities);
            ChatToolPermissions.Validate(nextPolicy, id, profile.Id, conversation.ProjectId);
            var next = new Settings(checked(expectedRevision + 1), DateTimeOffset.UtcNow,
                current?.ResponseSharePercent, nextPolicy)
            {
                WorkingDirectoryOverrideSet = current?.WorkingDirectoryOverrideSet == true,
                WorkingDirectory = current?.WorkingDirectory
            };
            store.Put(RecordKind, id, next);
            return (Value: PermissionProjection(conversation, profile, next), Changed: true);
        });
        if (saved.Changed)
            events.Publish("conversation.permissions.updated", null,
                new { conversationId = id, revision = saved.Value.SettingsRevision,
                    generation = saved.Value.Policy?.Generation });
        return saved.Value;
    }

    public CatalogPage Catalog(string id, RegisteredToolCatalog catalog, int offset, int limit,
        string? expectedGeneration, long? expectedSettingsRevision, int? expectedProfileRevision)
    {
        ValidateId(id);
        if (offset < 0 || limit is < 1 or > 100 ||
            offset > 0 && (expectedGeneration is null || expectedSettingsRevision is null || expectedProfileRevision is null))
            throw new ArgumentException("Catalog pages require a bounded offset and stable generation.");
        return store.Locked(() =>
        {
            var (conversation, profile) = PermissionScope(id);
            var settings = ReadStored(store, id);
            var policy = settings?.ToolPermissionPolicy;
            if (policy is not null) ChatToolPermissions.Validate(policy, id, profile.Id, conversation.ProjectId);
            if (expectedGeneration is not null && expectedGeneration != catalog.Generation ||
                expectedSettingsRevision is not null && expectedSettingsRevision != (settings?.Revision ?? 0) ||
                expectedProfileRevision is not null && expectedProfileRevision != profile.Revision)
                throw new InvalidOperationException("The chat, profile or registered tool catalog changed between pages.");
            var toolChoices = policy?.Tools.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
            var capChoices = policy?.Capabilities.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
            var authority = ChatToolPermissions.AuthorityFingerprint(profile);
            var captured = ChatToolPermissions.Capture(profile, policy, catalog, id, conversation.ProjectId);
            var knownTools = catalog.Tools.Select(x => x.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
            var knownCaps = catalog.Tools.Select(x => x.Descriptor.Capability).ToHashSet(StringComparer.Ordinal);
            knownCaps.UnionWith(profile.Capabilities);
            if (ChatToolPermissions.IsToolCapability("cortex.write", catalog)) knownCaps.Add("cortex.write");
            var rows = new List<CatalogItem>();
            foreach (var entry in catalog.Tools)
            {
                var descriptor = entry.Descriptor;
                toolChoices.TryGetValue(descriptor.Name, out var choice);
                var baseline = ChatToolPermissions.BaselineTool(profile, descriptor.Name);
                capChoices.TryGetValue(descriptor.Capability, out var capabilityChoice);
                var profileCap = profile.Capabilities.Contains(descriptor.Capability, StringComparer.Ordinal);
                var effective = captured.Effective.AllowedTools.Contains(descriptor.Name, StringComparer.Ordinal) &&
                    captured.Effective.Capabilities.Contains(descriptor.Capability, StringComparer.Ordinal);
                string? reason = choice?.Decision == ChatToolPermissions.Deny ? "chat-tool-deny" :
                    capabilityChoice?.Decision == ChatToolPermissions.Deny ? "chat-capability-deny" :
                    choice?.Decision == ChatToolPermissions.Allow &&
                    (!baseline || !profileCap) &&
                    (choice.AuthorityFingerprint != authority || choice.DescriptorFingerprint != entry.SourceFingerprint) ||
                    capabilityChoice?.Decision == ChatToolPermissions.Allow &&
                    !profileCap && capabilityChoice.AuthorityFingerprint != authority ? "owner-approval-stale" :
                    !baseline && choice?.Decision != ChatToolPermissions.Allow ? "tool-not-granted" :
                    !profileCap && capabilityChoice?.Decision != ChatToolPermissions.Allow ? "capability-not-granted" :
                    !profileCap && choice?.Decision != ChatToolPermissions.Allow ? "exact-tool-approval-required" :
                    descriptor.Name == "file.copy" &&
                    (!captured.Effective.AllowedTools.Contains("file.read", StringComparer.Ordinal) ||
                     !captured.Effective.Capabilities.Contains("filesystem.read", StringComparer.Ordinal))
                        ? "secondary-read-requirement" : effective ? null : "not-granted";
                rows.Add(new("tool", descriptor.Name, descriptor.Capability,
                    descriptor.Description[..Math.Min(descriptor.Description.Length, 1000)],
                    entry.Source, entry.SourceFingerprint,
                    descriptor.Name == "file.copy" ? ["file.read", "filesystem.read"] :
                    entry.Source.StartsWith("mcp:", StringComparison.Ordinal) &&
                    descriptor.Name.Split('.').Last().StartsWith("cortex_", StringComparison.Ordinal)
                        ? ["cortex.write for mutations"] : [], true, descriptor.ModelVisible, baseline,
                    choice?.Decision ?? ChatToolPermissions.Inherit,
                    reason is null ? baseline && profileCap ? "profile" : "owner-exception" : "blocked", reason));
            }
            foreach (var name in knownCaps)
            {
                capChoices.TryGetValue(name, out var choice);
                var baseline = profile.Capabilities.Contains(name, StringComparer.Ordinal);
                string? reason = choice?.Decision == ChatToolPermissions.Deny ? "chat-capability-deny" :
                    choice?.Decision == ChatToolPermissions.Allow && !baseline &&
                    choice.AuthorityFingerprint != authority
                        ? "owner-approval-stale" :
                    !baseline && choice?.Decision == ChatToolPermissions.Allow &&
                    !ChatToolPermissions.CanAddCapability(name, catalog) ? "secondary-profile-ceiling" :
                    !baseline && choice?.Decision != ChatToolPermissions.Allow ? "capability-not-granted" : null;
                rows.Add(new("capability", name, null, null, null, null, [], true, null, baseline,
                    choice?.Decision ?? ChatToolPermissions.Inherit,
                    reason is null ? baseline ? "profile" : "owner-exception" : "blocked", reason));
            }
            foreach (var choice in policy?.Tools ?? [])
                if (!knownTools.Contains(choice.Id)) rows.Add(new("tool", choice.Id, null, null, null,
                    choice.DescriptorFingerprint, [], false, null, false, choice.Decision,
                    "unavailable", "unregistered-id"));
            foreach (var choice in policy?.Capabilities ?? [])
                if (!knownCaps.Contains(choice.Id)) rows.Add(new("capability", choice.Id, null, null,
                    null, null, [], false, null, false, choice.Decision,
                    "unavailable", "unregistered-id"));
            var ordered = rows.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id,
                StringComparer.Ordinal).ToArray();
            if (offset > ordered.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            return new CatalogPage(id, "local", workerInstallationId, profile.Id,
                conversation.ProjectId, settings?.Revision ?? 0,
                profile.Revision, authority, catalog.Generation, offset, ordered.Skip(offset).Take(limit).ToArray(),
                offset + limit < ordered.Length ? offset + limit : null, catalog.UnavailableServers);
        });
    }

    private (ConversationSummary Conversation, AgentProfile Profile) PermissionScope(string id)
    {
        var conversation = EnsureConversation(id);
        var original = workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id)
            ?? throw new InvalidOperationException("Chat tool permissions require an accepted first native turn.");
        if (original.ProfileId != conversation.ProfileId || original.ProjectId != conversation.ProjectId)
            throw new InvalidDataException("The original chat owner and saved conversation disagree.");
        var profile = store.Get<AgentProfile>("agent", conversation.ProfileId)
            ?? throw new KeyNotFoundException("The chat's profile is unavailable.");
        if (profile.Id != conversation.ProfileId || profile.WorkerId != "local")
            throw new InvalidDataException("The chat's current profile identity is invalid.");
        return (conversation, profile);
    }

    private PermissionSnapshot PermissionProjection(ConversationSummary conversation,
        AgentProfile profile, Settings? settings) => new(conversation.Id, "local", workerInstallationId,
            profile.Id,
            conversation.ProjectId, settings?.Revision ?? 0, profile.Revision,
            ChatToolPermissions.AuthorityFingerprint(profile), settings?.ToolPermissionPolicy);

    private ConversationSummary EnsureConversation(string id)
    {
        if (store.GetSessionMetadata(id) is not null)
            throw new NotSupportedException("Properties require a saved native conversation. The previous conversation route is unsupported.");
        return workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, id)
            ?? throw new KeyNotFoundException("The saved native conversation was not found.");
    }

    private static Settings? ReadStored(StateStore store, string id)
    {
        var raw = store.Get<JsonElement>(RecordKind, id);
        if (raw.ValueKind == JsonValueKind.Undefined) return null;
        if (raw.ValueKind != JsonValueKind.Object || raw.GetRawText().Length > 512 * 1024)
            throw new InvalidDataException("Stored conversation properties are invalid or too large.");
        var value = raw.Deserialize<Settings>(new JsonSerializerOptions(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ??
            throw new InvalidDataException("Stored conversation properties are empty.");
        if (value is not null)
        {
            if (value.Revision < 1 || value.UpdatedAt is null)
                throw new InvalidDataException("Stored conversation properties have an invalid revision or timestamp.");
            if (value.ResponseSharePercent is { } share && (share < 5 || share > 50))
                throw new InvalidDataException("Stored conversation response share is outside the supported range.");
            if (!value.WorkingDirectoryOverrideSet && value.WorkingDirectory is not null ||
                value.WorkingDirectory is { } directory &&
                !string.Equals(ConversationRootPolicy.NormalizeDirectory(directory), directory, StringComparison.Ordinal))
                throw new InvalidDataException("Stored conversation working path is invalid.");
            if (value.ToolPermissionPolicy is { } policy)
                ChatToolPermissions.Validate(policy, policy.ConversationId, policy.ProfileId, policy.ProjectId);
        }
        return value;
    }

    private Snapshot Project(ConversationSummary conversation, Settings? settings)
    {
        var profile = store.Get<AgentProfile>("agent", conversation.ProfileId)
            ?? throw new KeyNotFoundException("The conversation's current agent profile is unavailable.");
        if (profile.Id != conversation.ProfileId || profile.AllowedRoots is null || profile.Capabilities is null || profile.AllowedTools is null)
            throw new InvalidDataException("The conversation's current profile has invalid identity or permission data.");
        // Exact grants are never truncated into a misleading permission display. Match the private
        // presentation bounds before copying arrays or committing any new preference revision.
        if (profile.AllowedRoots.Count > 1024 || profile.Capabilities.Count > 1024 || profile.AllowedTools.Count > 1024)
            throw new InvalidDataException("Current profile permissions exceed the bounded Properties display.");
        var original = workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, conversation.Id);
        if (original is not null && (original.ProfileId != conversation.ProfileId || original.ProjectId != conversation.ProjectId))
            throw new InvalidDataException("The original conversation submission does not match its saved identity.");
        var root = settings?.WorkingDirectoryOverrideSet == true ? settings.WorkingDirectory :
            ConversationPathGrants.Root(store, workflows.Store, conversation.Id,
                conversation.ProfileId, conversation.ProjectId);
        var result = new Snapshot(conversation.Id, conversation.Title, conversation.ProfileId, profile.Name, profile.Role,
            conversation.ProjectId, root is null ? "unbound" : "bound", root,
            profile.AllowedRoots.ToArray(), profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(),
            tools.FullAccessActive, settings is null ? "default" : "saved",
            settings?.ResponseSharePercent is null ? "profile" : "conversation",
            settings?.Revision ?? 0, settings?.UpdatedAt, settings?.ResponseSharePercent);
        if (JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > 512 * 1024)
            throw new InvalidDataException("Current Properties exceed the bounded presentation size.");
        return result;
    }

    private static void ValidateShare(int? share)
    {
        if (share is { } value && (value < 5 || value > 50))
            throw new ArgumentOutOfRangeException(nameof(share), "Response share must be between 5 and 50 percent or null to inherit the profile.");
    }

    private static bool SameDirectory(string? left, string? right) => left is null || right is null
        ? left == right : string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Configuration.ValidateId(id);
    }
}
