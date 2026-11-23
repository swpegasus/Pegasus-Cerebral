using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker;

/// <summary>Private owner choices for a saved native chat. Profile and host authority remain separate.</summary>
internal static class ChatToolPermissions
{
    internal const int Schema = 1;
    internal const string Inherit = "inherit";
    internal const string Allow = "allow";
    internal const string Deny = "deny";
    internal const string OwnerProvenance = "installation-owner/chat-properties";
    private const int MaximumStoredBytes = 512 * 1024;

    internal sealed record Choice(string Id, string Decision, string? AuthorityFingerprint,
        string? DescriptorFingerprint);
    internal sealed record Policy(int SchemaVersion, string ConversationId, string ProfileId,
        string? ProjectId, long Generation, string DecisionId, DateTimeOffset UpdatedAt,
        string Provenance,
        IReadOnlyList<Choice> Tools, IReadOnlyList<Choice> Capabilities);
    internal sealed record Patch(string Kind, string Id, string Decision, string? DescriptorFingerprint);
    internal sealed record ToolException(string Name, string SourceFingerprint);
    internal sealed record Binding(int SchemaVersion, string ConversationId, string OwnerProfileId,
        string ProfileId, string? ParentRunId,
        string? ProjectId, long PolicyGeneration, string? DecisionId, string AuthorityFingerprint,
        IReadOnlyList<string> BaseTools, IReadOnlyList<string> BaseCapabilities,
        IReadOnlyList<ToolException> ToolExceptions, IReadOnlyList<string> CapabilityExceptions);

    internal static string AuthorityFingerprint(AgentProfile profile)
    {
        // Only authority-bearing fields enter this epoch. Name, model and presentation edits do not
        // invalidate an owner's exact chat exception; permission, role and execution-ceiling edits do.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id, profile.WorkerId, profile.RemoteProfileId, profile.ModelWorkerId, profile.Role,
            profile.MaxSteps, profile.MaxSeconds, profile.MemoryBudgetMb,
            roots = profile.AllowedRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            tools = profile.AllowedTools.OrderBy(x => x, StringComparer.Ordinal),
            capabilities = profile.Capabilities.OrderBy(x => x, StringComparer.Ordinal),
            requiredModelCapabilities = profile.RequiredModelCapabilities.OrderBy(x => x, StringComparer.Ordinal),
            pluginSettings = profile.PluginSettings.OrderBy(x => x.Key, StringComparer.Ordinal)
        }, CerebralJson.Options);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static bool BaselineTool(AgentProfile profile, string name) =>
        profile.AllowedTools.Contains(name, StringComparer.Ordinal) ||
        name != "file.edit-range" && profile.AllowedTools.Contains("*", StringComparer.Ordinal);

    internal static bool IsToolCapability(string name, RegisteredToolCatalog catalog) =>
        catalog.Tools.Any(x => x.Descriptor.Capability == name) || name == "cortex.write" &&
        catalog.Tools.Any(x => x.Source.StartsWith("mcp:", StringComparison.Ordinal) &&
            x.Descriptor.Name.Split('.').Last().StartsWith("cortex_", StringComparison.Ordinal));

    // These capabilities control host or delegation authority outside an individual tool call.
    // A chat may deny them but cannot add them beyond its reusable profile.
    internal static bool CanAddCapability(string name, RegisteredToolCatalog catalog) =>
        name is not ("cortex.write" or "administration" or "agents.delegate" or "agents.collaborate") &&
        catalog.Tools.Any(x => x.Descriptor.Capability == name);

    internal static Policy Validate(Policy policy, string conversationId, string profileId, string? projectId)
    {
        if (policy.SchemaVersion != Schema || policy.ConversationId != conversationId ||
            policy.ProfileId != profileId || policy.ProjectId != projectId || policy.Generation < 1 ||
            policy.UpdatedAt == default || !Guid.TryParseExact(policy.DecisionId, "N", out var decision) ||
            decision == Guid.Empty ||
            policy.Provenance != OwnerProvenance ||
            policy.Tools is null || policy.Capabilities is null)
            throw new InvalidDataException("Stored chat tool permissions have invalid identity or revision.");
        Configuration.ValidateId(policy.ConversationId);
        Configuration.ValidateId(policy.ProfileId);
        if (policy.ProjectId is not null) Configuration.ValidateId(policy.ProjectId);
        ValidateChoices(policy.Tools, true);
        ValidateChoices(policy.Capabilities, false);
        if (JsonSerializer.SerializeToUtf8Bytes(policy, CerebralJson.Options).Length > MaximumStoredBytes)
            throw new InvalidDataException("Stored chat tool permissions exceed the bounded record size.");
        return policy;
    }

    internal static Policy? Read(JsonElement value, string conversationId, string profileId, string? projectId)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > MaximumStoredBytes)
            throw new InvalidDataException("Stored chat tool permissions are invalid or too large.");
        var strict = new JsonSerializerOptions(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        return Validate(value.Deserialize<Policy>(strict) ?? throw new InvalidDataException(
            "Stored chat tool permissions are empty."), conversationId, profileId, projectId);
    }

    internal static Binding ReadBinding(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > MaximumStoredBytes)
            throw new InvalidDataException("Accepted chat permission binding is invalid or too large.");
        var strict = new JsonSerializerOptions(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var binding = value.Deserialize<Binding>(strict) ?? throw new InvalidDataException(
            "Accepted chat permission binding is empty.");
        if (binding.SchemaVersion != Schema || string.IsNullOrWhiteSpace(binding.ConversationId) ||
            string.IsNullOrWhiteSpace(binding.OwnerProfileId) ||
            string.IsNullOrWhiteSpace(binding.ProfileId) || !ValidHex(binding.AuthorityFingerprint, 64) ||
            binding.PolicyGeneration < 0 || binding.BaseTools is null || binding.BaseCapabilities is null ||
            binding.ToolExceptions is null || binding.CapabilityExceptions is null ||
            binding.PolicyGeneration == 0 && binding.DecisionId is not null ||
            binding.PolicyGeneration > 0 &&
            (!Guid.TryParseExact(binding.DecisionId, "N", out var bindingDecision) ||
             bindingDecision == Guid.Empty) ||
            binding.BaseTools.Any(x => x != "*" && !ValidId(x)) ||
            binding.BaseCapabilities.Any(x => !ValidId(x)) ||
            binding.BaseTools.Distinct(StringComparer.Ordinal).Count() != binding.BaseTools.Count ||
            binding.BaseCapabilities.Distinct(StringComparer.Ordinal).Count() != binding.BaseCapabilities.Count ||
            binding.ToolExceptions.Any(x => !ValidId(x.Name) || !ValidHex(x.SourceFingerprint, 64)) ||
            binding.CapabilityExceptions.Any(x => !ValidId(x)) ||
            binding.ToolExceptions.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != binding.ToolExceptions.Count ||
            binding.CapabilityExceptions.Distinct(StringComparer.Ordinal).Count() != binding.CapabilityExceptions.Count)
            throw new InvalidDataException("Accepted chat permission binding has invalid authority fields.");
        Configuration.ValidateId(binding.ConversationId);
        Configuration.ValidateId(binding.OwnerProfileId);
        Configuration.ValidateId(binding.ProfileId);
        if (binding.ParentRunId is not null) Configuration.ValidateId(binding.ParentRunId);
        if (binding.ProjectId is not null) Configuration.ValidateId(binding.ProjectId);
        return binding;
    }

    internal static (AgentProfile Effective, Binding Binding) Capture(AgentProfile profile, Policy? policy,
        RegisteredToolCatalog catalog, string conversationId, string? projectId)
    {
        if (policy is not null) Validate(policy, conversationId, profile.Id, projectId);
        var fingerprint = AuthorityFingerprint(profile);
        var toolChoices = policy?.Tools.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
        var capChoices = policy?.Capabilities.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
        var allowedTools = new List<string>();
        var exceptionTools = new List<ToolException>();
        var exceptionCaps = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = profile.Capabilities.Where(x => !Denied(capChoices, x)).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in catalog.Tools)
        {
            var name = entry.Descriptor.Name;
            var cap = entry.Descriptor.Capability;
            if (Denied(toolChoices, name) || Denied(capChoices, cap)) continue;
            var exactToolAllow = ActiveAllow(toolChoices, name, fingerprint, entry.SourceFingerprint);
            var exactCapAllow = ActiveAllow(capChoices, cap, fingerprint, null) &&
                CanAddCapability(cap, catalog);
            var baseTool = BaselineTool(profile, name);
            var baseCap = profile.Capabilities.Contains(cap, StringComparer.Ordinal);
            // A capability exception never turns a profile wildcard into a grant for a future tool.
            if (!(baseTool || exactToolAllow) || !(baseCap || exactCapAllow) ||
                !baseCap && !exactToolAllow) continue;
            allowedTools.Add(name);
            if (exactToolAllow && (!baseTool || !baseCap))
                exceptionTools.Add(new(name, entry.SourceFingerprint));
            if (!baseCap && exactCapAllow)
            {
                exceptionCaps.Add(cap);
                capabilities.Add(cap);
            }
        }
        var effective = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
        effective.AllowedTools = allowedTools;
        effective.Capabilities = capabilities.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var binding = new Binding(Schema, conversationId, profile.Id, profile.Id, null, projectId,
            policy?.Generation ?? 0, policy?.DecisionId, fingerprint,
            profile.AllowedTools.ToArray(), profile.Capabilities.ToArray(),
            exceptionTools.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(),
            exceptionCaps.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        return (effective, binding);
    }

    internal static Binding Baseline(AgentProfile profile, string conversationId, string? projectId) =>
        new(Schema, conversationId, profile.Id, profile.Id, null, projectId, 0, null,
            AuthorityFingerprint(profile), profile.AllowedTools.ToArray(), profile.Capabilities.ToArray(), [], []);

    internal static Binding Child(AgentProfile selected, Binding parent, string parentRunId) =>
        new(Schema, parent.ConversationId, parent.OwnerProfileId, selected.Id, parentRunId,
            parent.ProjectId, parent.PolicyGeneration, parent.DecisionId, AuthorityFingerprint(selected),
            selected.AllowedTools.ToArray(), selected.Capabilities.ToArray(), [], []);

    internal static AgentProfile Effective(StateStore store, AgentProfile accepted, IReadOnlyList<ToolDescriptor> selected,
        Binding binding)
    {
        var current = store.Get<AgentProfile>("agent", binding.ProfileId)
            ?? throw new UnauthorizedAccessException("The chat's selected profile is unavailable.");
        if (current.Id != accepted.Id || current.Id != binding.ProfileId || current.WorkerId != "local")
            throw new UnauthorizedAccessException("The chat's execution profile changed identity.");
        var settings = ConversationProperties.ReadAdmissionSettings(store, binding.ConversationId);
        var policy = settings.ToolPermissionPolicy;
        if (policy is not null) Validate(policy, binding.ConversationId, binding.OwnerProfileId, binding.ProjectId);
        var toolChoices = policy?.Tools.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
        var capChoices = policy?.Capabilities.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? [];
        var currentFingerprint = AuthorityFingerprint(current);
        var exceptionTools = binding.ToolExceptions.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var exceptionCaps = binding.CapabilityExceptions.ToHashSet(StringComparer.Ordinal);
        var baseProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(accepted));
        baseProfile.AllowedTools = binding.BaseTools.ToList();
        baseProfile.Capabilities = binding.BaseCapabilities.ToList();
        var permittedTools = new List<string>();
        var permittedCaps = new HashSet<string>(binding.BaseCapabilities.Intersect(current.Capabilities,
            StringComparer.Ordinal).Where(x => accepted.Capabilities.Contains(x, StringComparer.Ordinal) &&
                !Denied(capChoices, x)), StringComparer.Ordinal);
        foreach (var descriptor in selected)
        {
            var name = descriptor.Name;
            var cap = descriptor.Capability;
            if (Denied(toolChoices, name) || Denied(capChoices, cap)) continue;
            var toolException = exceptionTools.TryGetValue(name, out var exception) &&
                binding.AuthorityFingerprint == currentFingerprint &&
                ActiveAllow(toolChoices, name, currentFingerprint, exception.SourceFingerprint);
            var capException = exceptionCaps.Contains(cap) && toolException &&
                binding.AuthorityFingerprint == currentFingerprint &&
                ActiveAllow(capChoices, cap, currentFingerprint, null);
            var baseTool = BaselineTool(baseProfile, name) && BaselineTool(current, name);
            var baseCap = accepted.Capabilities.Contains(cap, StringComparer.Ordinal) &&
                binding.BaseCapabilities.Contains(cap, StringComparer.Ordinal) &&
                current.Capabilities.Contains(cap, StringComparer.Ordinal);
            if (!(baseTool || toolException) || !(baseCap || capException)) continue;
            permittedTools.Add(name);
            if (capException) permittedCaps.Add(cap);
        }
        var effective = CerebralJson.Read<AgentProfile>(CerebralJson.Element(accepted));
        effective.AllowedTools = permittedTools;
        effective.Capabilities = permittedCaps.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (binding.ParentRunId is { } parentRunId)
        {
            var engine = new WorkflowStore(new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")));
            var parent = engine.Get(parentRunId).Submission;
            if (parent.ConversationId != binding.ConversationId || parent.ProfileId != binding.OwnerProfileId ||
                parent.ParentRunId is not null)
                throw new UnauthorizedAccessException("The accepted child permission lineage changed.");
            var parentSnapshot = WorkerExecutionSnapshot.Read(parent.SettingsSnapshot);
            var parentEffective = WorkerExecutionSnapshot.EffectiveProfile(store, parentSnapshot, parent.ProfileId);
            effective.AllowedTools = effective.AllowedTools.Intersect(parentEffective.AllowedTools,
                StringComparer.Ordinal).ToList();
            effective.Capabilities = effective.Capabilities.Intersect(parentEffective.Capabilities,
                StringComparer.Ordinal).ToList();
        }
        // Current roots and other non-tool ceilings are applied by WorkerExecutionSnapshot.
        return effective;
    }

    internal static AgentProfile NarrowHistorical(AgentProfile effective, Policy? policy,
        string conversationId, string profileId, string? projectId)
    {
        if (policy is null) return effective;
        Validate(policy, conversationId, profileId, projectId);
        var deniedTools = policy.Tools.Where(x => x.Decision == Deny).Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        var deniedCaps = policy.Capabilities.Where(x => x.Decision == Deny).Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        var narrowed = CerebralJson.Read<AgentProfile>(CerebralJson.Element(effective));
        narrowed.AllowedTools = narrowed.AllowedTools.Where(x => !deniedTools.Contains(x)).ToList();
        narrowed.Capabilities = narrowed.Capabilities.Where(x => !deniedCaps.Contains(x)).ToList();
        return narrowed;
    }

    private static bool ActiveAllow(Dictionary<string, Choice> choices, string id, string authority,
        string? descriptor) => choices.TryGetValue(id, out var choice) && choice.Decision == Allow &&
        choice.AuthorityFingerprint == authority && choice.DescriptorFingerprint == descriptor;
    private static bool Denied(Dictionary<string, Choice> choices, string id) =>
        choices.TryGetValue(id, out var choice) && choice.Decision == Deny;

    internal static void ValidateChoices(IReadOnlyList<Choice> choices, bool tools)
    {
        if (choices.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != choices.Count ||
            choices.Any(x => !ValidId(x.Id) || x.Id == "*" || x.Decision is not (Allow or Deny) ||
                x.Decision == Allow && (!ValidHex(x.AuthorityFingerprint, 64) ||
                    tools && !ValidHex(x.DescriptorFingerprint, 64) ||
                    !tools && x.DescriptorFingerprint is not null) ||
                x.Decision == Deny && (x.AuthorityFingerprint is not null || x.DescriptorFingerprint is not null)))
            throw new InvalidDataException("Chat permission choices have duplicate or invalid exact IDs.");
    }

    internal static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } &&
        value.All(c => c is >= '!' and <= '~' and not '*');
    internal static bool ValidHex(string? value, int length) => value is not null && value.Length == length &&
        value.All(Uri.IsHexDigit);
}
