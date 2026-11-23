using System.ComponentModel;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

internal sealed record WorkerDockRow(string TaskId, CollaborationViewRun Coordinate, string Agent, DateTimeOffset? CompletedAt)
{
    public string RunId => Coordinate.RunId;
    public string ContextId => Coordinate.ConversationId;
    public string State => Coordinate.Status.ToString();
    public int ActiveOrder => Coordinate.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled ? 1 : 0;
    public string CompletionText => CompletedAt?.ToLocalTime().ToString("g") ??
        (ActiveOrder==0 ? "—" : "Not recorded in loaded view");
}

internal static class WorkerDockProjection
{
    internal static void Sort(ICollectionView view, bool activeFirst, string member, ListSortDirection direction)
    {
        if(member is not (nameof(WorkerDockRow.Agent) or nameof(WorkerDockRow.State) or nameof(WorkerDockRow.ContextId) or nameof(WorkerDockRow.CompletedAt)))
            throw new ArgumentException("Unsupported worker sort field.",nameof(member));
        using(view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            if(activeFirst) view.SortDescriptions.Add(new(nameof(WorkerDockRow.ActiveOrder),ListSortDirection.Ascending));
            view.SortDescriptions.Add(new(member,direction));
            view.SortDescriptions.Add(new(nameof(WorkerDockRow.ContextId),ListSortDirection.Ascending));
            view.SortDescriptions.Add(new(nameof(WorkerDockRow.RunId),ListSortDirection.Ascending));
        }
    }

    internal static DateTimeOffset? Completion(CollaborationViewRun coordinate, WorkflowConversationSnapshot? snapshot)
    {
        if(snapshot?.Run is not { IsTerminal:true } run || snapshot.RunId!=coordinate.RunId ||
            snapshot.ConversationId!=coordinate.ConversationId || run.Id!=coordinate.RunId ||
            run.ConversationId!=coordinate.ConversationId || run.ProfileId!=coordinate.ProfileId ||
            run.Revision<coordinate.Revision || run.Status!=coordinate.Status) return null;
        // Completion is a recorded terminal engine transition, never task UpdatedAt,
        // current time, a profile label, or an inferred end of a bounded activity page.
        var last=snapshot.Events.Where(item=>item.TurnId==coordinate.RunId &&
            item.ConversationId==coordinate.ConversationId && item.Type==CerebralEventTypes.WorkflowTransition)
            .OrderBy(item=>item.Sequence).LastOrDefault();
        if(last is not { Actor:{Kind:EventActorKind.Service,Id:"cerebral.engine"},
            Provenance:{Source:EventSourceKind.Service,SourceId:"cerebral.engine"} } ||
            last.Provenance.JobId!=coordinate.RunId || last.CreatedAt==default ||
            last.Data is not {ValueKind:JsonValueKind.Object} data ||
            !data.TryGetProperty("runId",out var id)||id.ValueKind!=JsonValueKind.String||id.GetString()!=coordinate.RunId||
            !data.TryGetProperty("status",out var status)||status.ValueKind!=JsonValueKind.String||status.GetString()!=run.Status.ToString())
            return null;
        return last.CreatedAt;
    }
}
