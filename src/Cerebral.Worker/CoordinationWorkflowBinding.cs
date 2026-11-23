using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Owner configuration selects a helper's feature workflow; model assignments never carry grants or schemas.</summary>
internal sealed record CoordinationWorkflowBinding(string DefinitionId, string DefinitionVersion, JsonElement Input)
{
    internal const string SettingsKey = "cerebral.coordination";

    internal static CoordinationWorkflowBinding Read(AgentProfile profile, string? parentProjectId)
    {
        if (!profile.PluginSettings.TryGetValue(SettingsKey, out var value))
            return new(ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                parentProjectId is null ? WorkflowContract.EmptyObject :
                    CerebralJson.Element(new { projectId = parentProjectId }));

        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > 8192)
            throw new ArgumentException("Helper workflow binding must be an object of at most 8 KiB.");
        var members = value.EnumerateObject().Select(property => property.Name).ToArray();
        string[] expected = ["schemaVersion", "definitionId", "definitionVersion", "input"];
        if (members.Length != expected.Length || members.Distinct(StringComparer.Ordinal).Count() != members.Length ||
            members.Except(expected, StringComparer.Ordinal).Any() ||
            !value.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            !value.TryGetProperty("definitionId", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { Length: > 0 and <= 256 } definitionId || definitionId.Contains('\0') ||
            !value.TryGetProperty("definitionVersion", out var revision) || revision.ValueKind != JsonValueKind.String ||
            revision.GetString() is not { Length: > 0 and <= 64 } definitionVersion || definitionVersion.Contains('\0') ||
            !value.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Helper workflow binding requires schemaVersion 1, exact definitionId/definitionVersion and object input.");

        var binding = new CoordinationWorkflowBinding(definitionId, definitionVersion, input.Clone());
        ValidateProject(binding, parentProjectId);
        // Conversation's feature context needs the same project as the host-owned lineage.
        // Other feature schemas receive only their configured input, never a synthetic projectId.
        if (definitionId == ConversationWorkflowModule.DefinitionId && parentProjectId is not null &&
            (!input.TryGetProperty("projectId", out var project) || project.ValueKind == JsonValueKind.Null))
        {
            var scoped = input.EnumerateObject().ToDictionary(property => property.Name,
                property => (object)property.Value.Clone(), StringComparer.Ordinal);
            scoped["projectId"] = parentProjectId;
            binding = binding with { Input = CerebralJson.Element(scoped) };
        }
        return binding;
    }

    // A helper's working directory is execution context, not a path grant. Inherit only
    // the accepted built-in Conversation input; feature workflow JSON has other semantics.
    internal CoordinationWorkflowBinding InheritConversationWorkingDirectory(WorkflowSubmission parent)
    {
        if (DefinitionId != ConversationWorkflowModule.DefinitionId ||
            DefinitionVersion != ConversationWorkflowModule.DefinitionVersion ||
            parent.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            parent.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion ||
            Input.TryGetProperty("workingDirectory", out _))
            return this;

        var directory = ConversationRootPolicy.ReadDirectory(parent.Input);
        if (directory is null) return this;
        var scoped = Input.EnumerateObject().ToDictionary(property => property.Name,
            property => (object)property.Value.Clone(), StringComparer.Ordinal);
        scoped["workingDirectory"] = CerebralJson.Element(directory);
        return this with { Input = CerebralJson.Element(scoped) };
    }

    internal static string? CollaborationDirectory(WorkflowSubmission source, string? targetProjectId,
        WorkflowSubmission? target)
    {
        if (source.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            source.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion) return null;
        var directory = ConversationRootPolicy.ReadDirectory(source.Input);
        if (directory is null) return null;
        if (source.ProjectId != targetProjectId)
            throw new UnauthorizedAccessException("A Main working directory cannot cross a collaboration project boundary.");
        if (target is not null)
        {
            var targetDirectory = ConversationRootPolicy.ReadDirectory(target.Input);
            if (targetDirectory is null || !string.Equals(directory, targetDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new WorkflowConflictException("The target has a different or explicitly unbound working directory. Resolve its binding before delegation.");
        }
        return directory;
    }

    internal static void ValidateProject(CoordinationWorkflowBinding binding, string? parentProjectId)
    {
        if (binding.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            !binding.Input.TryGetProperty("projectId", out var project) || project.ValueKind == JsonValueKind.Null)
            return;
        if (project.ValueKind != JsonValueKind.String || project.GetString() != parentProjectId)
            throw new UnauthorizedAccessException("A helper workflow cannot substitute the parent project scope.");
    }
}


// Communication is never ownership. Only this host creates these bindings for fresh contexts.
// This bounded record is also the future lifecycle/export boundary; no deletion is implemented here.
internal sealed record OwnedConversationContext(string MainConversationId, string ContextId,
    string ConversationId, string ProfileId, string? ProjectId, string DefinitionId,
    string DefinitionVersion, string Role, string DisplayName);
internal sealed record MainContextSet(string MainConversationId, string ProfileId, string? ProjectId,
    long Revision, bool AdmissionClosed, IReadOnlyList<OwnedConversationContext> Members);

internal sealed class MainContextBindings(StateStore store)
{
    private const string Roots = "coordination-contexts", Owners = "coordination-owner";
    internal const int MaximumMembers = 64;

    internal MainContextSet Read(string mainId, string profileId, string? projectId)
    {
        var value = store.Get<MainContextSet>(Roots, mainId) ??
            new(mainId, profileId, projectId, 0, false, []);
        if (value.MainConversationId != mainId || value.ProfileId != profileId ||
            value.ProjectId != projectId || value.Members.Count > MaximumMembers ||
            value.Members.Select(x => x.ContextId).Distinct(StringComparer.Ordinal).Count() != value.Members.Count ||
            value.Members.Select(x => x.ConversationId).Distinct(StringComparer.Ordinal).Count() != value.Members.Count)
            throw new UnauthorizedAccessException("The Main context ownership binding changed or is invalid.");
        foreach (var member in value.Members)
            if (member.MainConversationId != mainId || member.ProjectId != projectId ||
                member.ConversationId == mainId || member.Role is not ("coordinator" or "worker") ||
                store.Get<OwnedConversationContext>(Owners, member.ConversationId) != member)
                throw new UnauthorizedAccessException("An owned context no longer matches its Main binding.");
        return value;
    }

    internal OwnedConversationContext? Owner(string conversationId) =>
        store.Get<OwnedConversationContext>(Owners, conversationId);

    internal OwnedConversationContext Require(string mainId, string profileId, string? projectId, string contextId)
    {
        var root = Read(mainId, profileId, projectId);
        if (root.AdmissionClosed) throw new InvalidOperationException("This Main context no longer admits work or context reuse.");
        return root.Members.SingleOrDefault(x => x.ContextId == contextId) ??
            throw new UnauthorizedAccessException("The worker context does not belong to this Main chat.");
    }

    internal OwnedConversationContext Reserve(string mainId, string rootProfileId, string? projectId,
        string profileId, string definitionId, string definitionVersion, string role, string label,
        string? reuseContextId = null) => store.Locked(() =>
    {
        Configuration.ValidateId(mainId); Configuration.ValidateId(profileId);
        if (role is not ("coordinator" or "worker") || string.IsNullOrWhiteSpace(label) ||
            label.Length > 128 || label.Any(char.IsControl))
            throw new ArgumentException("An owned context requires a bounded readable label and host role.");
        var root = Read(mainId, rootProfileId, projectId);
        if (root.AdmissionClosed) throw new InvalidOperationException("This Main context no longer admits new work.");
        var member = reuseContextId is null ? null :
            root.Members.SingleOrDefault(x => x.ContextId == reuseContextId) ??
                throw new UnauthorizedAccessException("The context handle is not owned by this Main.");
        if (member is not null && (member.Role != role || member.ProfileId != profileId ||
            member.DefinitionId != definitionId || member.DefinitionVersion != definitionVersion))
            throw new UnauthorizedAccessException("A context cannot change profile, role or workflow binding.");
        if (member is null && root.Members.Count == MaximumMembers)
            throw new InvalidOperationException("This Main reached its bounded owned-context limit.");
        member = member is null
            ? new(mainId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), profileId,
                projectId, definitionId, definitionVersion, role, label)
            : member with { DisplayName = label };
        var members = root.Members.Where(x => x.ContextId != member.ContextId).Append(member).ToArray();
        store.Put(Owners, member.ConversationId, member);
        store.Put(Roots, mainId, root with { Revision = checked(root.Revision + 1), Members = members });
        return member;
    });
}

internal sealed record CoordinatorSourceBinding(string TargetRunId, string SourceRunId, string MainConversationId);
