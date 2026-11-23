using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Cerebral.Client;
using Cerebral.Contracts.Collaboration;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal sealed record TaskDockRow(string OwnerMainId, CollaborationViewTask Record, string ResponsibleProfile)
{
    public string TaskId => Record.TaskId;
    public string State => Record.Status.ToString();
    public string ResponsibleContextId => Record.TargetConversationId;
    public long Revision => Record.Revision;
}

internal sealed record TaskPromptTarget(string MainId,string? TaskId,long? Revision,string? ProfileId,string? ContextId)
{
    internal static TaskPromptTarget Capture(string mainId,string listOwner,CollaborationViewTask? task)
    {
        static bool Id(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=128&&!value.Any(char.IsControl);
        if(!Id(mainId)||mainId!=listOwner) throw new InvalidOperationException("The task list belongs to another Main chat.");
        if(task is not null&&(!Id(task.TaskId)||task.Revision<1||!Id(task.TargetProfileId)||!Id(task.TargetConversationId)))
            throw new InvalidOperationException("The saved task identity is invalid. Refresh before requesting changes.");
        return task is null?new(mainId,null,null,null,null):
            new(mainId,task.TaskId,task.Revision,task.TargetProfileId,task.TargetConversationId);
    }
    internal string Message(string request)
    {
        if(string.IsNullOrWhiteSpace(request)||request.Contains('\0')) throw new ArgumentException("Enter a task-list change request.");
        var target=TaskId is null?"Task target: the owning chat's task list.":
            $"Task ID: {TaskId}\nObserved task revision: {Revision}\nResponsible profile ID: {ProfileId}\nResponsible context ID: {ContextId}";
        var text=$"Task-list change request for owning Main chat {MainId}.\n{target}\n"+
            "Requested task-list change only; this is not a request to execute task work. Sending does not confirm a saved task update.\n\n"+request.Trim();
        if(new UTF8Encoding(false,true).GetByteCount(text)>8192) throw new ArgumentException("Keep the complete task request within 8 KB.");
        return text;
    }
}

public sealed partial class OrchestratorWindow
{
    private Expander? tasksWorkPanel;
    private const string TasksWorkPanelId="tasks";
    private readonly ObservableCollection<TaskDockRow> taskDockRows=new();
    private DataGrid? taskDockGrid;
    private TextBox? taskChangeInput;
    private TextBlock? taskDockSummary,taskChangeTarget,taskChangeStatus;
    private Button? taskChangeSend,taskChangeBind,taskChangeClear,taskLoadOlder;
    private bool taskChangeSending,taskChangeUncertain;
    private string? taskChangeRequestId,taskChangeFrozenText,taskChangeRunId;
    private bool taskChangeSteering;
    private TaskPromptOwner? taskPromptOwner;
    private sealed record TaskPromptOwner(TaskPromptTarget Target,CerebralClient Client,object Connection,
        string Identity,int Version,DesktopConversationBinding Binding);

    private FrameworkElement CreateTasksDockBody()
    {
        var body=new DockPanel();
        taskDockSummary=Ui.Text("No loaded task records for this Main chat.",12,Ui.Muted); taskDockSummary.Margin=new(8,5,8,5);
        DockPanel.SetDock(taskDockSummary,Dock.Top); body.Children.Add(taskDockSummary);
        var note=Ui.Text("Saved task records for this chat. Request additions, changes or removal through Main, then refresh after Main confirms the update.",12,Ui.Muted);
        note.TextWrapping=TextWrapping.Wrap; note.Margin=new(8,3,8,6);
        DockPanel.SetDock(note,Dock.Top); body.Children.Add(note);
        taskLoadOlder=Ui.Button("Load older task records",()=>Run(LoadOlderTeamTasksAsync));
        var refresh=Ui.Button("Refresh saved task records",()=>Run(RefreshTeamForCurrentMainAsync));
        var tools=Ui.Bar(refresh,taskLoadOlder); DockPanel.SetDock(tools,Dock.Top); body.Children.Add(tools);
        taskDockGrid=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,
            SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,
            HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.All,
            RowHeight=25,FontSize=13,ItemsSource=taskDockRows,EnableRowVirtualization=true,Background=Ui.Surface};
        ScrollViewer.SetHorizontalScrollBarVisibility(taskDockGrid,ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(taskDockGrid,"ChatTasksList");
        AutomationProperties.SetName(taskDockGrid,"Saved tasks for the owning Main chat. Read-only.");
        foreach(var (label,path,width) in new[] {
            ("Task ID",nameof(TaskDockRow.TaskId),180),("State",nameof(TaskDockRow.State),90),
            ("Responsible profile (target)",nameof(TaskDockRow.ResponsibleProfile),190),("Responsible context (target)",nameof(TaskDockRow.ResponsibleContextId),240),
            ("Revision",nameof(TaskDockRow.Revision),70),("Owning Main",nameof(TaskDockRow.OwnerMainId),200)})
        {
            var textStyle=new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(path)));
            taskDockGrid.Columns.Add(new DataGridTextColumn {Header=label,Binding=new Binding(path),SortMemberPath=path,
                Width=width,MinWidth=width,ElementStyle=textStyle});
        }
        taskDockGrid.SelectionChanged+=(_,_)=>RefreshTaskPromptControls();
        var request=new StackPanel {Margin=new(8,6,8,8)};
        request.Children.Add(Ui.Text("Request additions, changes or removal through Main",13));
        taskChangeTarget=Ui.Text("",12,Ui.Muted); taskChangeTarget.TextWrapping=TextWrapping.Wrap; request.Children.Add(taskChangeTarget);
        taskChangeInput=Ui.Input(multiline:true); taskChangeInput.MinHeight=60; taskChangeInput.MaxHeight=110; taskChangeInput.MaxLength=6000;
        AutomationProperties.SetAutomationId(taskChangeInput,"TaskChangeRequest");
        AutomationProperties.SetName(taskChangeInput,"Task-list change request to Main");
        request.Children.Add(taskChangeInput);
        taskChangeBind=Ui.Button("Use selected task / current list",()=>
        {
            if(taskChangeSending||taskChangeUncertain) return;
            try { taskPromptOwner=CaptureTaskPromptOwner(); taskChangeStatus!.Text="Target reviewed. The request draft is unchanged."; }
            catch(Exception error) when(error is InvalidOperationException) {taskChangeStatus!.Text=error.Message;}
            RefreshTaskPromptControls();
        });
        taskChangeSend=Ui.Button("Send request to Main",()=>Run(async()=>
        {
            try {await SendTaskChangeRequestAsync();}
            catch(Exception error) when(error is ArgumentException or InvalidOperationException or InvalidDataException)
            {taskChangeStatus!.Text=TaskRequestFeedback(error.Message);RefreshTaskPromptControls();}
        }),true);
        var wholeList=Ui.Button("Target whole list",()=>
        {
            if(taskChangeSending||taskChangeUncertain) return;
            taskDockGrid.SelectedItem=null;
            try {taskPromptOwner=CaptureTaskPromptOwner();taskChangeStatus!.Text="Whole-list target reviewed. The draft text is unchanged.";}
            catch(InvalidOperationException error) {taskChangeStatus!.Text=error.Message;}
            RefreshTaskPromptControls();
        });
        taskChangeClear=Ui.Button("Clear reviewed request",()=>
        {
            if(taskChangeSending||ControlUncertain||NativeMutationUncertain||TaskRetryRetained()) return;
            taskChangeUncertain=false;taskChangeRequestId=taskChangeFrozenText=taskChangeRunId=null;
            taskChangeInput.Clear();taskPromptOwner=null;
            taskChangeStatus!.Text="Request draft cleared. This action does not change saved task records.";
            RefreshTaskPromptControls();
        });
        request.Children.Add(Ui.Bar(taskChangeBind,wholeList,taskChangeSend,taskChangeClear));
        taskChangeStatus=Ui.Text("",12,Ui.Muted); taskChangeStatus.TextWrapping=TextWrapping.Wrap; request.Children.Add(taskChangeStatus);
        taskChangeInput.TextChanged+=(_,_)=>RefreshTaskPromptControls();
        DockPanel.SetDock(request,Dock.Bottom); body.Children.Add(request); body.Children.Add(taskDockGrid);
        return body;
    }

    private TaskPromptOwner CaptureTaskPromptOwner()
    {
        if(!IsConnected||!IsNativeChat||durableConversationId is not {} main||teamSourceConversationId!=main)
            throw new InvalidOperationException("Load this Main chat's saved task records before requesting a change.");
        var selected=taskDockGrid?.SelectedItem as TaskDockRow;
        if(selected is not null && (selected.OwnerMainId!=main||teamTasks.All(row=>row.TaskId!=selected.TaskId||row.Revision!=selected.Revision)))
            throw new InvalidOperationException("The selected task changed. Refresh and review its target.");
        return new(TaskPromptTarget.Capture(main,teamSourceConversationId,selected?.Record),Client,ServiceConnection,
            ServiceConnectionIdentity,knowledgeConnectionVersion,PluginBinding());
    }

    private bool TaskPromptCurrent(TaskPromptOwner owner,bool requireTaskRevision=true) =>
        IsConnected&&!Lifetime.IsCancellationRequested&&IsNativeChat&&owner.Target.MainId==durableConversationId&&
        teamSourceConversationId==owner.Target.MainId&&ReferenceEquals(owner.Client,Client)&&ReferenceEquals(owner.Connection,ServiceConnection)&&
        owner.Identity==ServiceConnectionIdentity&&owner.Version==knowledgeConnectionVersion&&owner.Binding==PluginBinding()&&
        (!requireTaskRevision||owner.Target.TaskId is null||teamTasks.Any(task=>task.TaskId==owner.Target.TaskId&&task.Revision==owner.Target.Revision&&
            task.TargetProfileId==owner.Target.ProfileId&&task.TargetConversationId==owner.Target.ContextId));

    private void RefreshTaskPromptControls()
    {
        if(taskChangeInput is null||taskChangeSend is null||taskChangeTarget is null) return;
        if(!taskChangeSending&&!taskChangeUncertain&&taskChangeInput.Text.Length==0) taskPromptOwner=null;
        if(taskPromptOwner is null&&taskChangeInput.Text.Length>0)
        {
            try {taskPromptOwner=CaptureTaskPromptOwner();}
            catch(InvalidOperationException error) {taskChangeStatus!.Text=error.Message;}
        }
        var owner=taskPromptOwner; var current=owner is not null&&TaskPromptCurrent(owner,!taskChangeUncertain);
        var selected=taskDockGrid?.SelectedItem as TaskDockRow;
        taskChangeTarget.Text=owner is null?selected is null?"Target: the current Main chat’s task list.":$"Selected target: Main {selected.OwnerMainId} · task {selected.TaskId} · revision {selected.Revision}":
            $"Owning Main: {owner.Target.MainId} · "+(owner.Target.TaskId is null?"whole list":$"task {owner.Target.TaskId} · revision {owner.Target.Revision}");
        if(owner is not null&&!current) taskChangeTarget.Text+=" · current Main or saved task revision differs";
        taskChangeInput.IsReadOnly=taskChangeSending||taskChangeUncertain||owner is not null&&!current;
        taskChangeSend.Content=taskChangeUncertain?"Retry same request":"Send request to Main";
        var valid=false;
        if(current) {try {owner!.Target.Message(taskChangeInput.Text);valid=true;}catch(ArgumentException){}}
        taskChangeSend.IsEnabled=!taskChangeSending&&current&&valid&&(!taskChangeUncertain||TaskRetryRetained());
        taskChangeClear!.IsEnabled=!taskChangeSending&&!ControlUncertain&&!NativeMutationUncertain&&!TaskRetryRetained();
        taskChangeBind!.IsEnabled=!taskChangeSending&&!taskChangeUncertain&&IsConnected&&IsNativeChat;
        if(owner is not null&&!current&&!taskChangeSending&&(taskChangeStatus!.Text.Length==0||taskChangeStatus.Text.StartsWith("Draft kept for",StringComparison.Ordinal)))
            taskChangeStatus!.Text="Draft kept for its original Main and task revision. Return there, or explicitly review the current target before sending.";
    }

    private void RefreshTaskDockRows()
    {
        if(taskDockGrid is null||taskDockSummary is null) return;
        var main=durableConversationId;
        if(!IsConnected||!IsNativeChat||main is null||teamSourceConversationId!=main)
        {
            taskDockRows.Clear(); taskDockSummary.Text="Task records are unavailable for this Main binding.";
        }
        else
        {
            var selected=(taskDockGrid.SelectedItem as TaskDockRow)?.TaskId;
            var rows=teamTasks.Select(task=>
            {
                var label=profiles.FirstOrDefault(profile=>profile.Id==task.TargetProfileId)?.Name;
                var name=!string.IsNullOrWhiteSpace(label)&&label.Length<=128&&!label.Any(char.IsControl)?label:task.TargetProfileId;
                return new TaskDockRow(main,task,name);
            }).ToArray();
            ReconcileDockRows(taskDockRows,rows,row=>row.TaskId);
            if(selected is not null) taskDockGrid.SelectedItem=taskDockRows.FirstOrDefault(row=>row.TaskId==selected);
            taskDockSummary.Text=$"{rows.Length} loaded task records for Main {main}"+(teamNextBeforeRow is null?"":" · older records available")+" · revisions are the last saved values read";
        }
        taskLoadOlder!.IsEnabled=IsConnected&&teamNextBeforeRow is not null&&teamTasks.Count<200;
        RefreshTaskPromptControls();
    }

    private bool TaskRetryRetained() => taskChangeRequestId is not null&&taskChangeFrozenText is not null&&
        (taskChangeSteering
            ? ControlUncertain&&IsCurrentConversationControl&&conversationControl?.Snapshot.Steering is {} steering&&
                steering.RequestId==taskChangeRequestId&&steering.RunId==taskChangeRunId&&steering.Text==taskChangeFrozenText
            : NativeMutationUncertain&&RetainedNativeOwnerCurrent()&&retainedNativeSubmission is {} submission&&
                submission.RequestId==taskChangeRequestId&&submission.UserText==taskChangeFrozenText);

    private static string TaskRequestFeedback(string text) => text.Length<=1600?text:text[..1599]+"…";

    private async Task SendTaskChangeRequestAsync()
    {
        RefreshTaskPromptControls();
        var owner=taskPromptOwner;
        if(taskChangeSending||owner is null||!TaskPromptCurrent(owner,!taskChangeUncertain)||taskChangeSend?.IsEnabled!=true) return;
        var retry=taskChangeUncertain;
        var request=retry?taskChangeFrozenText!:owner.Target.Message(taskChangeInput!.Text);
        if(!retry)
        {
            EnsureConversationControlsSettled();
            if(NativeMutationPending||NativeMutationUncertain||pendingSubmission is not null||retainedNativeSubmission is not null)
                throw new InvalidOperationException("Resolve the existing Main request before sending this task change.");
            if(prompt.Text==request) throw new InvalidOperationException("This exact request is already in Main's composer. Send it there.");
            taskChangeSteering=nativeChat?.Snapshot?.Run is {IsTerminal:false,CancelRequested:false};
            if(!taskChangeSteering&&attachmentDrafts.Count>0)
                throw new InvalidOperationException("Keep the existing Main attachment draft separate. Send or remove it there before a new task request.");
            if(CaptureMainChatWorkflowIntent()?.DefinitionId!=ConversationControlClient.ChatDefinitionId)
                throw new InvalidOperationException("Task change requests require the ordinary Main chat route.");
            taskChangeRequestId=Guid.NewGuid().ToString("N"); taskChangeFrozenText=request;
            taskChangeRunId=taskChangeSteering?nativeChat?.Snapshot?.RunId:null;
        }
        else if(taskChangeRequestId is null||taskChangeFrozenText is null||!TaskRetryRetained())
            throw new InvalidOperationException("The retained task request is unavailable; resolve it through Main.");
        taskChangeSending=true; taskChangeStatus!.Text=retry?"Retrying the same retained request…":"Pending Main acceptance…";
        RefreshTaskPromptControls();
        var dispatched=false;
        try
        {
            if(!TaskPromptCurrent(owner,!retry)) throw new InvalidOperationException("The Main or task binding changed before dispatch.");
            if(!retry&&taskChangeSteering&&nativeChat?.Snapshot?.RunId!=taskChangeRunId)
                throw new InvalidOperationException("Main's active run changed. Review the request before sending.");
            var input=new DesktopSubmission(owner.Binding,taskChangeRequestId!,request);
            dispatched=true;
            var result=taskChangeSteering?await SteerNativeDesktopInput(input,Lifetime.Token):
                await SubmitNativeDesktopInput(input,null,Lifetime.Token);
            if(result.RequestId!=taskChangeRequestId) throw new InvalidDataException("Main's receipt does not match this task request.");
            taskChangeUncertain=result.State==DesktopOperationState.Uncertain;
            if(!TaskPromptCurrent(owner,false)) {taskChangeStatus.Text="Result belongs to the original Main. Return there to review acceptance.";return;}
            if(result.State==DesktopOperationState.Accepted)
            {
                taskChangeUncertain=false; taskChangeInput!.Clear(); taskPromptOwner=null;
                taskChangeStatus.Text=taskChangeSteering?
                    "Guidance acceptance confirmed for the next safe Main step. The task change is still pending; review Main's response and refresh saved records.":
                    "Main message acceptance confirmed. The task change is still pending; review Main's response and refresh saved records.";
            }
            else taskChangeStatus.Text=TaskRequestFeedback((taskChangeUncertain?"Acceptance unconfirmed. Retry only this exact retained request. ":"Main did not confirm acceptance. ")+(result.Message??result.State.ToString()));
        }
        catch(Exception error) when(error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            taskChangeUncertain=dispatched||ControlUncertain||NativeMutationUncertain;
            taskChangeStatus!.Text=TaskRequestFeedback((taskChangeUncertain?"Acceptance unconfirmed; preserve the same request and review Main. ":"Request not confirmed. ")+error.Message);
        }
        finally
        {
            taskChangeSending=false;
            if(!taskChangeUncertain) {taskChangeRequestId=null;taskChangeFrozenText=null;taskChangeRunId=null;}
            RefreshTaskPromptControls();
        }
    }

    private static void ReconcileDockRows<T>(ObservableCollection<T> target,IReadOnlyList<T> rows,Func<T,string> key)
    {
        var ids=rows.Select(key).ToHashSet(StringComparer.Ordinal);
        if(ids.Count!=rows.Count) throw new InvalidDataException("Dock row identity is duplicated.");
        using(CollectionViewSource.GetDefaultView(target).DeferRefresh())
        {
            for(var i=target.Count-1;i>=0;i--) if(!ids.Contains(key(target[i]))) target.RemoveAt(i);
            foreach(var row in rows)
            {
                var index=target.ToList().FindIndex(prior=>key(prior)==key(row));
                if(index<0) target.Add(row); else if(!EqualityComparer<T>.Default.Equals(target[index],row)) target[index]=row;
            }
        }
    }
}
