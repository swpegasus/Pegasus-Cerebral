using Cerebral.Core;

namespace Cerebral.Worker;

public sealed class WorkspaceManagement(StateStore store, EventHub events, JobScheduler scheduler,
    Cerebral.Engine.Persistence.WorkflowStore? workflows = null)
{
    public NavigationState GetNavigation() => store.Get<NavigationState>("navigation", "sidebar") ?? new();
    public NavigationState SaveNavigation(NavigationState value) => store.Locked(() =>
    {
        if (value.Nodes.Count > 1000) throw new ArgumentException("Keep sidebar organization within 1000 entries.");
        var old = GetNavigation();
        if (old.Revision != value.Revision) throw new InvalidOperationException("Sidebar changed. Refresh before saving; the existing organization was preserved.");
        var retainedProjects = value.Nodes.Where(x => x.Kind == "project").Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var removedProjects = old.Nodes.Where(x => x.Kind == "project" && !retainedProjects.Contains(x.Id)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (workflows is not null && removedProjects.Any(id => workflows.HasConversationForProject(ConversationWorkflowModule.DefinitionId, id)))
            throw new InvalidOperationException("This project still has durable conversations. Keep its navigation entry until those conversations can be moved. Nothing was removed.");
        var bound = store.List<TaskSession>("session").Where(x => x.ProjectId is not null && removedProjects.Contains(x.ProjectId)).Take(6).ToList();
        if (bound.Count > 0) throw new InvalidOperationException("This project still has bound conversations, including any helper sessions. Rebind them or unbind with sessions.project (id, projectId=null) before removing the project: " +
            string.Join(", ", bound.Select(x => x.Id)) + ". No project or conversation was removed.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in value.Nodes)
        {
            Configuration.ValidateId(node.Id);
            if (!ids.Add(node.Id)) throw new ArgumentException("Duplicate sidebar entry.");
            ValidateTitle(node.Title);
            if (node.Kind is not ("folder" or "project" or "conversation" or "agent")) throw new ArgumentException("Unknown sidebar kind.");
            if (node.Kind == "conversation" && (node.TargetId is null || (store.GetSessionMetadata(node.TargetId) is null && workflows?.GetConversation(ConversationWorkflowModule.DefinitionId, node.TargetId) is null))) throw new ArgumentException("Sidebar conversation no longer exists.");
            if (node.Kind == "agent" && (node.TargetId is null || store.Get<AgentProfile>("agent", node.TargetId) is null)) throw new ArgumentException("Sidebar agent no longer exists.");
            if (node.Path is not null && (!Path.IsPathFullyQualified(node.Path) || node.Path.Contains('\0'))) throw new ArgumentException("Project paths must be absolute.");
        }
        var index = value.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var node in value.Nodes)
        {
            var seen = new HashSet<string>([node.Id], StringComparer.Ordinal); var parent = node.ParentId; var depth = 0;
            while (parent is not null)
            {
                if (!index.TryGetValue(parent, out var owner) || owner.Kind is not ("folder" or "project")) throw new ArgumentException("Only existing folders and projects can contain sidebar entries.");
                if (!seen.Add(parent) || ++depth > 12) throw new ArgumentException("Sidebar hierarchy contains a cycle or exceeds twelve levels.");
                parent = owner.ParentId;
            }
        }
        value.Revision++; store.Put("navigation", "sidebar", value); events.Publish("navigation.updated", null, new { value.Revision }); return value;
    });
    public TaskSession Rename(string id, string title) => store.Locked(() =>
    {
        ValidateTitle(title);
        var session = store.GetSessionMetadata(id) ?? throw new ArgumentException("Conversation not found.");
        session.Title = title.Trim(); session.Revision++; store.Put("session", id, session);
        var navigation = GetNavigation(); var changed = false;
        foreach (var node in navigation.Nodes.Where(x => x.Kind == "conversation" && x.TargetId == id)) { node.Title = session.Title; changed = true; }
        if (changed) { navigation.Revision++; store.Put("navigation", "sidebar", navigation); }
        events.Publish("session.updated", null, new { id });
        return store.GetSession(id, messageLimit: 200)!;
    });
    private static void ValidateTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200 || text.Any(char.IsControl)) throw new ArgumentException("Use a title of 1–200 characters without control characters.");
    }
    public List<TaskFailure> Failures(string sessionId)
    {
        var ids = scheduler.ConversationJobIds(sessionId);
        var jobs = store.List<AgentJob>("job").Where(x => ids.Contains(x.Id)).ToList();
        var failures = new List<TaskFailure>();
        // Parent jobs retain helper evidence under the same immutable evidence ID. Display one
        // failure at its source coordinates, while honoring a resolution recorded on any copy.
        foreach (var group in jobs.SelectMany(job => job.Evidence.Where(e => !e.Success).Select(e => new { Job = job, Evidence = e }))
            .GroupBy(x => x.Evidence.Id, StringComparer.Ordinal))
        {
            var sourceSessionId = group.Select(x => x.Evidence.SourceSessionId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var owner = group.FirstOrDefault(x => x.Job.SessionId == sourceSessionId) ??
                group.OrderByDescending(x => x.Job.ParentJobId is not null).ThenBy(x => x.Job.CreatedAt).First();
            var evidence = owner.Evidence;
            var resolution = group.Select(x => x.Evidence.Resolution).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            failures.Add(new TaskFailure
            {
                Id = evidence.Id, JobId = owner.Job.Id, SessionId = sourceSessionId ?? owner.Job.SessionId,
                Source = evidence.Kind + ": " + evidence.Name, Message = evidence.Summary, CreatedAt = evidence.At,
                Status = resolution is null ? "open" : "resolved", Resolution = resolution
            });
        }
        foreach (var job in jobs.Where(x => x.Status is "failed" or "interrupted" && !string.IsNullOrWhiteSpace(x.Error) && !x.Evidence.Any(e => !e.Success)))
        {
            var resolution = store.Get<string>("failure-resolution", job.Id);
            failures.Add(new() { Id = job.Id, JobId = job.Id, SessionId = job.SessionId, Source = "assignment", Message = job.Error!,
                CreatedAt = job.FinishedAt ?? job.CreatedAt, Status = string.IsNullOrWhiteSpace(resolution) ? "open" : "resolved", Resolution = resolution });
        }
        return failures.OrderByDescending(x => x.CreatedAt).DistinctBy(x => x.Id, StringComparer.Ordinal).Take(100).ToList();
    }
    public object ResolveFailure(string id, string note) => store.Locked(() =>
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 2000 || note.Contains('\0')) throw new ArgumentException("Give a short resolution note.");
        var jobs = store.List<AgentJob>("job");
        var copies = jobs.Where(job => job.Evidence.Any(e => e.Id == id && !e.Success)).ToList();
        if (copies.Count > 0)
        {
            // Resolving this ID must also resolve the parent's imported copy; otherwise the
            // failure disappears from this list while still preventing the lead from completing.
            foreach (var job in copies)
            {
                foreach (var evidence in job.Evidence.Where(e => e.Id == id && !e.Success)) evidence.Resolution = "User reviewed: " + note;
                store.Put("job", job.Id, job);
                events.Publish("failures.updated", job.Id, new { id, sessionId = job.SessionId });
            }
        }
        else
        {
            var job = jobs.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("Failure not found.");
            store.Put("failure-resolution", id, note);
            events.Publish("failures.updated", job.Id, new { id, sessionId = job.SessionId });
        }
        return new { id, resolved = true };
    });
}
