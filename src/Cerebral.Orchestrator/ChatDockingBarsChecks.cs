using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

/// <summary>Unshown controls and deterministic state/record fixtures only. No Main, Worker, model or native window.</summary>
public static class ChatDockingBarsChecks
{
    public static int Verify()
    {
        var count=OrchestratorWindow.CheckEmbeddedActivityScopeContracts();
        void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); count++; }
        var root=new Grid(); ChatDockAppearance.Apply(root);
        var panels=new[]{"Main","Coordinator","Workers","Tasks / To-do","Activity"}.Select(name=>new Expander { Header=name,Content=new TextBlock {Text=name+" body"} }).ToArray();
        using(var accordion=new WindowPanelAccordion())
        {
            foreach(var panel in panels) { ChatDockAppearance.Configure(panel,root); root.Children.Add(panel); accordion.Register(panel); }
            accordion.Open(panels[0]);
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[0].IsExpanded,"One Main bar must start open.");
            panels[0].IsExpanded=false;
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[0].IsExpanded,"Clicking the selected bar cannot close the last body.");
            accordion.Open(panels[2]);
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[2].IsExpanded,"Selecting Workers closes Main and leaves one Workers body.");
            panels[2].IsExpanded=false;
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[2].IsExpanded,"The selected Workers bar remains open.");
            accordion.Open(panels[3]); panels[3].IsExpanded=false;
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[3].IsExpanded,"Tasks participates in exact-one selection and cannot close itself.");
            accordion.Open(panels[4]); accordion.Open(panels[4]);
            Check(panels.Count(panel=>panel.IsExpanded)==1&&panels[4].IsExpanded,"Repeated Activity selection is stable.");
            accordion.Unregister(panels[4]);
            Check(panels.Count(panel=>panel.IsExpanded)==1&&!panels[4].IsExpanded,"Removing the selected bar closes its body and restores a registered fallback.");
            Check(panels.All(panel=>panel.Style is not null),"All docking bars use the pane-scoped header template.");
        }
        var state=new ConversationPanelStackState(); state.Bind("owner","main");
        Check(state.AcceptHandoff("owner","main","main-run","task","main-run","coordinator",true,false),"An accepted matching handoff selects Coordinator.");
        state.SetExpanded("workers",true); var version=state.SelectionVersion;
        Check(!state.AcceptHandoff("owner","main","main-run","task","main-run","coordinator",true,false)&&state.IsExpanded("workers")&&state.SelectionVersion==version,
            "Refresh of an accepted handoff does not steal manual Workers selection.");
        state.SetExpanded("workers",false);
        Check(state.IsExpanded("workers"),"A negative toggle does not create a zero-panel state.");
        Check(state.ReturnCurrentMain("main-run","question:1")&&state.MainExpanded,"A new actual question returns to Main.");
        Check(!state.ReturnCurrentMain("main-run","question:1"),"Repeated attention is not repeated navigation.");
        var completed=new CollaborationViewRun("done","context-done","profile-a",null,"coordinator",WorkflowStatus.Completed,5);
        var active=new CollaborationViewRun("active","context-active","profile-b",null,"coordinator",WorkflowStatus.Running,2);
        var at=DateTimeOffset.UnixEpoch.AddSeconds(12);
        var terminal=new CerebralEvent("terminal","context-done","done",12,CerebralEventTypes.WorkflowTransition,
            new(EventActorKind.Service,"cerebral.engine"),[],new(ModelInclusion.Never,HumanVisibility.Activity),
            new(EventSourceKind.Service,"cerebral.engine",JobId:"done"),at,CerebralJson.Element(new {runId="done",status="Completed"}));
        var run=new WorkflowConversationRun("done","context-done","profile-a","fixture","fixture","1",5,WorkflowStatus.Completed,
            null,null,null,null,false,null,null,1);
        var snapshot=new WorkflowConversationSnapshot("context-done","done",run,[terminal],12,false,false,false,null);
        Check(WorkerDockProjection.Completion(completed,snapshot)==at,"Completion comes from the exact terminal engine event.");
        Check(WorkerDockProjection.Completion(completed,snapshot with {ConversationId="foreign"}) is null,"Foreign context cannot supply completion.");
        Check(WorkerDockProjection.Completion(completed,snapshot with {Run=run with {Revision=4}}) is null,"An older snapshot cannot supply current completion.");
        Check(WorkerDockProjection.Completion(completed,snapshot with {Events=[terminal with {Actor=new(EventActorKind.Agent,"profile-a")} ]}) is null,
            "An agent-authored terminal-looking payload cannot supply the engine timestamp.");
        var rows=new ObservableCollection<WorkerDockRow> {
            new("task",completed,"Same profile",at),new("task",active,"Same profile",null),
            new("task",completed with {RunId="unknown",ConversationId="context-unknown"},"Same profile",null) };
        var view=CollectionViewSource.GetDefaultView(rows);
        WorkerDockProjection.Sort(view,true,nameof(WorkerDockRow.Agent),ListSortDirection.Ascending);
        Check(view.Cast<WorkerDockRow>().First().RunId=="active","Active-first sorting retains exact row identity even with identical display names.");
        WorkerDockProjection.Sort(view,false,nameof(WorkerDockRow.CompletedAt),ListSortDirection.Descending);
        Check(view.Cast<WorkerDockRow>().First().RunId=="done","Completion ordering uses recorded times and keeps unknown times distinct.");
        Check(rows[2].CompletionText.Contains("Not recorded",StringComparison.Ordinal),"Missing completion is explicit and not replaced by current time.");
        var task=new CollaborationViewTask("task-a",7,CollaborationTaskStatus.Waiting,"responsible-context","different-profile",null,"target-run",2,at);
        var target=TaskPromptTarget.Capture("main","main",task);
        var message=target.Message("Please retire this task after reviewing its current state.");
        Check(message.Contains("task-a",StringComparison.Ordinal)&&message.Contains("revision: 7",StringComparison.Ordinal)&&
            message.Contains("responsible-context",StringComparison.Ordinal)&&message.Contains("different-profile",StringComparison.Ordinal),
            "Task requests retain actual task/revision and responsible context/profile without making responsibility an exclusive lock.");
        var refused=false;
        try {TaskPromptTarget.Capture("main","foreign",task);} catch(InvalidOperationException) {refused=true;}
        Check(refused,"A foreign Main task page cannot bind a change request.");
        Check(TaskPromptTarget.Capture("main","main",null).Message("Add a new item").Contains("owning chat's task list",StringComparison.Ordinal),
            "Whole-list additions are requests through Main, not invented task writes.");
        Check(task.Revision==7&&task.Status==CollaborationTaskStatus.Waiting,"Building a request does not optimistically alter the saved task record.");
        return count;
    }
}
