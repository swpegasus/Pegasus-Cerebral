using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed class ProjectKnowledge(StateStore store, Configuration configuration, EventHub events)
{
    public NavigationNode Project(string id)
    {
        Configuration.ValidateId(id);
        return (store.Get<NavigationState>("navigation", "sidebar") ?? new()).Nodes.SingleOrDefault(x => x.Id == id && x.Kind == "project")
            ?? throw new ArgumentException("Project binding no longer names an existing sidebar project. Choose a project in the conversation.");
    }
    public ProjectKnowledgeSettings Get(string projectId)
    {
        Project(projectId);
        var saved = store.Get<ProjectKnowledgeSettings>("project-knowledge", projectId);
        if (saved is not null) return saved;
        var host = configuration.Get();
        var enabled = host.McpServers.Where(x => x.Enabled).ToList();
        var provider = enabled.FirstOrDefault(x => x.Id.Equals("cortex", StringComparison.OrdinalIgnoreCase))
            ?? (enabled.Count == 1 ? enabled[0] : null);
        if (provider is null || host.CortexNamespaces.Count == 0) return new() { ProjectId = projectId };
        // A newly added project inherits the service's already reviewed read scope. Writes remain disabled
        // until explicitly selected, and author approval remains on if they are enabled later.
        var defaults = new ProjectKnowledgeSettings
        {
            ProjectId = projectId, Revision = 1, CortexServerId = provider.Id,
            ReadNamespaces = host.CortexNamespaces.Distinct(StringComparer.Ordinal).Take(8).ToList(),
            AutomaticRetrieval = true, AutomaticContinuity = false, RequireKnowledgeApproval = true
        };
        store.Put("project-knowledge", projectId, defaults);
        events.Publish("project.knowledge.updated", null, new { defaults.ProjectId, defaults.Revision, automatic = true });
        return defaults;
    }
    public List<ProjectKnowledgeSettings> List() => store.List<ProjectKnowledgeSettings>("project-knowledge")
        .Where(x => (store.Get<NavigationState>("navigation", "sidebar") ?? new()).Nodes.Any(n => n.Id == x.ProjectId && n.Kind == "project")).ToList();
    public ProjectKnowledgeSettings Save(ProjectKnowledgeSettings value) => store.Locked(() =>
    {
        Project(value.ProjectId); Validate(value);
        var current = store.Get<ProjectKnowledgeSettings>("project-knowledge", value.ProjectId);
        if ((current?.Revision ?? 0) != value.Revision) throw new InvalidOperationException("Project knowledge settings changed. Refresh before saving; the current settings were preserved.");
        value = CerebralJson.Read<ProjectKnowledgeSettings>(CerebralJson.Element(value));
        value.ReadNamespaces = value.ReadNamespaces.Distinct(StringComparer.Ordinal).ToList();
        value.Revision++; store.Put("project-knowledge", value.ProjectId, value);
        events.Publish("project.knowledge.updated", null, new { value.ProjectId, value.Revision }); return value;
    });
    public void Validate(ProjectKnowledgeSettings value)
    {
        var host = configuration.Get();
        if (value.Revision < 0) throw new ArgumentException("Project settings revision cannot be negative.");
        if (string.IsNullOrWhiteSpace(value.CortexServerId) || !host.McpServers.Any(x => x.Id == value.CortexServerId))
            throw new ArgumentException("Choose a configured Cortex MCP connection.");
        if (value.ReadNamespaces is null || value.ReadNamespaces.Count is < 1 or > 8 || value.ReadNamespaces.Any(x => !host.CortexNamespaces.Contains(x, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("Project read namespaces must be an explicit selection of one to eight host-allowed Cortex namespaces.");
        if (value.AutomaticContinuity && (value.WriteNamespace is null || !value.ReadNamespaces.Contains(value.WriteNamespace, StringComparer.Ordinal)))
            throw new ArgumentException("Automatic Cortex continuity requires a write namespace that is also selected for reading.");
        if (value.WriteNamespace is not null && !host.CortexWriteNamespaces.Contains(value.WriteNamespace, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Project write namespace is outside the host Cortex write allowlist.");
    }
    /// <summary>Host-resolved project scope; an unconfigured project never inherits a wider operation at execution.</summary>
    public CortexAccessScope ScopeFor(string projectId, bool approvedKnowledgeWrite = false)
    {
        Configuration.ValidateId(projectId);
        var project = (store.Get<NavigationState>("navigation", "sidebar") ?? new()).Nodes
            .SingleOrDefault(node => node.Id == projectId && node.Kind == "project");
        // Workflow project lineage can exist without a desktop sidebar entry. It has no implicit Cortex grant.
        if (project is null) return new(projectId, "", [], null, true, false, "unavailable");
        var value = Get(project.Id);
        // Intersect again at execution time: a later host restriction cannot be bypassed by old project settings.
        var host = configuration.Get();
        return new CortexAccessScope(project.Id, value.CortexServerId,
            value.ReadNamespaces.Intersect(host.CortexNamespaces, StringComparer.Ordinal).ToArray(),
            value.WriteNamespace is { } target && host.CortexWriteNamespaces.Contains(target, StringComparer.Ordinal) ? target : null,
            value.RequireKnowledgeApproval, approvedKnowledgeWrite,
            host.McpServers.SingleOrDefault(x => x.Id == value.CortexServerId && x.Enabled) is { } provider
                ? CortexOperationPolicy.ProviderFingerprint(provider) : "unavailable");
    }
    public ToolExecutionContext ContextFor(TaskSession session, AgentProfile profile, string jobId, bool approvedKnowledgeWrite = false)
    {
        if (session.ProjectId is null) return new(profile, session.Id, jobId, profile.AllowedRoots.FirstOrDefault());
        var project = Project(session.ProjectId);
        var scope = ScopeFor(project.Id, approvedKnowledgeWrite);
        string? directory = null;
        if (project.Path is not null)
            try { directory = ToolDispatcher.ValidateWorkingDirectory(project.Path, profile); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        // Missing/ungranted project paths never fall back to another project's working directory.
        return new(profile, session.Id, jobId, directory, scope);
    }
    public void RestrictUnmappedRemoteGrant(TaskSession session, AgentProfile grant)
    {
        if (session.ProjectId is null) return;
        var provider = Get(session.ProjectId).CortexServerId;
        if (string.IsNullOrWhiteSpace(provider)) return;
        grant.Capabilities.RemoveAll(x => x == "mcp." + provider);
        grant.AllowedTools.RemoveAll(x => x.StartsWith("mcp." + provider + ".", StringComparison.Ordinal));
    }
    public TaskSession BindSession(string id, string? projectId) => store.Locked(() =>
    {
        var session = store.GetSessionMetadata(id) ?? throw new ArgumentException("Conversation not found.");
        var jobs = store.List<AgentJob>("job");
        var related = jobs.Where(x => x.SessionId == id).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var job in jobs.Where(x => x.ParentJobId is not null && related.Contains(x.ParentJobId)))
                expanded |= related.Add(job.Id);
        }
        if (jobs.Any(x => related.Contains(x.Id) && JobScheduler.IsActive(x)))
            throw new InvalidOperationException("Wait for or stop the active conversation before changing its project.");
        if (projectId is not null) Project(projectId);
        ValidateGrouping(session.Id, projectId);
        if (session.ProjectId != projectId)
        {
            session.ProjectId = projectId; session.RemoteSessionIds.Clear(); session.Revision++;
            store.Put("session", session.Id, session);
        }
        GroupConversation(session);
        events.Publish("session.updated", null, new { id });
        return store.GetSession(id, messageLimit: 200)!;
    });
    public void ValidateGrouping(string sessionId, string? projectId)
    {
        if (projectId is null) return;
        var navigation = store.Get<NavigationState>("navigation", "sidebar") ?? new();
        if (navigation.Nodes.Count >= 1000 && !navigation.Nodes.Any(x => x.Kind == "conversation" && x.TargetId == sessionId))
            throw new InvalidOperationException("The sidebar has reached its 1000-entry limit.");
    }
    public void GroupConversation(TaskSession session)
    {
        var navigation = store.Get<NavigationState>("navigation", "sidebar") ?? new();
        var nodes = navigation.Nodes.Where(x => x.Kind == "conversation" && x.TargetId == session.Id).ToList();
        if (nodes.Count == 0 && session.ProjectId is not null)
        {
            if (navigation.Nodes.Count >= 1000) throw new InvalidOperationException("The sidebar has reached its 1000-entry limit.");
            var node = new NavigationNode { Kind = "conversation", TargetId = session.Id, Title = session.Title, ParentId = session.ProjectId };
            navigation.Nodes.Add(node); nodes.Add(node);
        }
        if (nodes.Count == 0) return;
        foreach (var node in nodes) node.ParentId = session.ProjectId;
        navigation.Revision++; store.Put("navigation", "sidebar", navigation);
        events.Publish("navigation.updated", null, new { navigation.Revision });
    }
}
