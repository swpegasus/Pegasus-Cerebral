using System.Reflection;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Actual offscreen WPF controls driven by deterministic journal/stream events; no provider or network.</summary>
public static class WorkflowCompactActivityChecks
{
    public static async Task<int> VerifyAsync(string? imagePath = null)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        long sequence = 1;
        var events = new List<CerebralEvent>
        {
            new("compact-original", "compact-chat", "compact-run", sequence++, CerebralEventTypes.Message,
                new(EventActorKind.User), [new TextContentPart("Review the selected skill and build the small example.")],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now)
        };
        CerebralEvent Call(string id, string tool, object args) => new(id, "compact-chat", "compact-run", sequence++, CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent,"lead","Cerebral"), [], new(ModelInclusion.Never,HumanVisibility.Activity), new(EventSourceKind.Agent), now,
            CerebralJson.Element(new ToolCallData(id, "cerebral.host:tool", CerebralJson.Element(new { name=tool, arguments=args }))));
        CerebralEvent Receipt(string id, string callId, string state, bool success, string summary) => new(id,"compact-chat","compact-run",sequence++,CerebralEventTypes.ToolResult,
            new(EventActorKind.Tool),[new StructuredContentPart("workflow.command.result",CerebralJson.Element(new { state, data=new { summary } }))],
            new(ModelInclusion.Never,HumanVisibility.Activity),new(EventSourceKind.Tool),now,
            CerebralJson.Element(new ToolResultData(callId,"cerebral.host:tool",success,DurationMilliseconds:120)));
        events.Add(Call("slow-call","file.read",new { path="Example/slow.txt" }));
        events.Add(Call("skill-call","skills.read",new { skillId="example-build", path="Example/SKILL.md" }));
        events.Add(Receipt("skill-result","skill-call","Succeeded",true,"Read the selected example-build skill."));
        events.Add(Call("read-call","file.read",new { path="Example/Program.cs",startLine=1,lineCount=20 }));
        events.Add(Receipt("read-result","read-call","Succeeded",true,"Read 20 source lines."));
        events.Add(Call("build-call","process.run",new { executableId="dotnet", arguments=new[]{"build","Example.csproj"} }));
        events.Add(Receipt("build-result","build-call","Failed",false,"Compiler reported CS0246 in the fixture."));
        events.Add(Call("unknown-call","file.write",new { path="Example/output.txt" }));
        events.Add(Receipt("unknown-result","unknown-call","Unknown",false,"The operation has no confirmed terminal receipt."));
        events.Add(Call("cancel-call","process.run",new { executableId="dotnet",arguments=new[]{"test"} }));
        events.Add(Receipt("cancel-result","cancel-call","Cancelled",false,"Cancellation was confirmed for this fixture call."));
        var counts = WorkflowWorkspace.RecordedActivityOutcomes(events);
        Check(counts is { Completed:2,Failed:1,Cancelled:1,Unconfirmed:1,Observed:true },"Confirmed failures, cancellation and Unknown must remain distinct counts.");
        var late = Receipt("unknown-resolved","unknown-call","Succeeded",true,"The explicitly inspected fixture operation is complete.");
        var resolved = WorkflowWorkspace.RecordedActivityOutcomes(events.Append(late));
        Check(resolved is { Completed:3,Failed:1,Cancelled:1,Unconfirmed:0 },"A later exact operation receipt updates its outcome without counting it twice.");
        Check(!WorkflowWorkspace.RecordedActivityOutcomes(events.Where(x=>x.Type==CerebralEventTypes.ToolCall)).Observed,"No receipt observations must not imply zero confirmed failures or completion.");
        var saved = new WorkflowRun { Id="compact-run",Revision=1,Status=WorkflowStatus.Running,
            Submission=new("compact-run","compact-chat","lead","fixture:compact","1.0.0",((TextContentPart)events[0].Content.Single()).Text,null,WorkflowContract.EmptyObject,WorkflowContract.EmptyObject),
            Effect=new("model-effect","cerebral.host:generate",1,WorkflowContract.EmptyObject,WorkflowReplayPolicy.Never,WorkflowEffectState.Dispatched),CreatedAt=now,UpdatedAt=now };
        Task<CommandResult> Dispatch(CommandRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get"=>CommandResult.Ok(saved),
                "jobs.workflow.events.previous"=>CommandResult.Ok(new { events,snapshotSequence=events[^1].Sequence,nextBeforeSequence=(long?)null }),
                "jobs.workflow.events"=>CommandResult.Ok(new { events=new[]{events[0]},snapshotSequence=events[^1].Sequence,nextAfterSequence=(long?)null }),
                _=>throw new InvalidOperationException("Unexpected compact fixture command "+request.Operation)
            });
        }
        using var workspace = new WorkflowWorkspace(Dispatch,()=>[],(_,_)=>{},embedded:true);
        workspace.BeginConversation("compact-chat");
        await workspace.OpenRunSnapshotAsync("compact-run");
        var frame = new Border { Background=Ui.Surface,Padding=new Thickness(14),Child=workspace };
        using var source = new HwndSource(new HwndSourceParameters("Cerebral compact activity fixture")
        { Width=740,Height=580,WindowStyle=unchecked((int)0x80000000),PositionX=-32000,PositionY=-32000 }) { RootVisual=frame };
        async Task Flush()
        {
            await workspace.Dispatcher.InvokeAsync(()=>{frame.Measure(new Size(740,580));frame.Arrange(new Rect(0,0,740,580));frame.UpdateLayout();},DispatcherPriority.Loaded);
            await workspace.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        }
        await Flush();
        var activity=Descendants(workspace).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="WorkflowActivity");
        Check(!Descendants(workspace).OfType<Expander>().Any(x=>AutomationProperties.GetAutomationId(x)=="WorkflowActivity") &&
            !Descendants(workspace).Any(x=>AutomationProperties.GetAutomationId(x) is "WorkflowActivityViewport" or "WorkflowActivityHistory"),
            "Chat contains only the activity summary action, with no expansion chevron, detail box or inline history controls.");
        Check(activity.Content.ToString()!.Contains("2 completed",StringComparison.Ordinal)&&activity.Content.ToString()!.Contains("1 failed",StringComparison.Ordinal)&&
            activity.Content.ToString()!.Contains("1 unconfirmed",StringComparison.Ordinal)&&activity.Content.ToString()!.Contains("— tok/s",StringComparison.Ordinal),"The summary retains actual recorded counts and an unobserved rate.");
        var navigation=new List<ActivityTurnTarget>();workspace.ActivityNavigationRequested+=navigation.Add;
        Check(new System.Windows.Automation.Peers.ButtonAutomationPeer(activity).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is not null,
            "The summary is a normal keyboard/invoke-accessible button rather than an expandable control.");
        Check(navigation.Count==0,"Layout and polling do not navigate or request focus.");
        activity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(navigation.Count == 1 && navigation[0] is { ConversationId: "compact-chat", RunId: "compact-run", FirstSequence: 2, LastSequence: 11 },
            "Explicit activation requests only the exact conversation and accepted journal-turn interval represented by the line.");

        var payload=new JsonObject { ["schema"]="cerebral.generation-preview/1",["conversationId"]="compact-chat",["requestId"]="compact-run",["runId"]="compact-run",
            ["effectId"]="model-effect",["agentId"]="lead",["phase"]="draft",["kind"]="final",["text"]="The response appears here as public text arrives.\n\nSelect the activity summary to review this turn on the Activity page.",
            ["operation"]=null,["truncated"]=false,["elapsedMilliseconds"]=100,["finish"]=null };
        workspace.Notify(new(100,"workflow.generation.preview","compact-run",CerebralJson.Element(payload),now));await Flush();
        Check(workspace.GenerationPreviewSnapshot is { Visible:true,Kind:"final",MessageId:"answer:compact-run:model-effect" },"A streamed answer has one stable exact run/effect message identity.");
        var host=(Border)typeof(WorkflowWorkspace).GetField("generationPreviewHost",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(workspace)!;
        Check(host.Parent is StackPanel flow && AutomationProperties.GetAutomationId(flow.Parent) == "WorkflowTranscript","Public answer text belongs to the main conversation viewport.");
        if(imagePath is not null)
        {
            var bitmap=new RenderTargetBitmap(740,580,96,96,PixelFormats.Pbgra32);bitmap.Render(frame);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=new FileStream(imagePath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);encoder.Save(file);
        }
        events.Add(Receipt("slow-late-receipt","slow-call","Succeeded",true,"An earlier call has its first confirmed receipt."));
        await workspace.InspectRunAsync("compact-run");await Flush();
        var timeline=workspace.CaptureActivityTimeline(new("compact-owner"));
        Check(timeline.Items.Single(item=>item.EventIds.Contains("slow-call")).EventIds.Contains("slow-late-receipt") &&
            activity.Content.ToString()!.Contains("3 completed",StringComparison.Ordinal) && navigation.Count==1,
            "A late receipt remains paired with its exact original operation and updates the summary without navigating.");
        frame.Measure(new Size(410,580));frame.Arrange(new Rect(0,0,410,580));frame.UpdateLayout();
        var header=Descendants(activity).OfType<TextBlock>().First(x=>x.Text==activity.Content.ToString());
        Check(header.TextWrapping==TextWrapping.Wrap && header.ActualHeight>24 && header.ActualWidth<410,
            "The complete counts/rate header wraps in a narrow conversation instead of clipping fields.");
        var bodyMethod=typeof(WorkflowWorkspace).GetMethod("EventBody",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var publicStructured=events[0] with { Content=[new StructuredContentPart("fixture.measurements",CerebralJson.Element(new { customVoltage=12.4, customCounter=17, apiKey="fixture-public-secret" }))],Data=null };
        var publicBody=(UIElement)bodyMethod.Invoke(workspace,[publicStructured])!;
        var publicText=string.Join('\n',Descendants(publicBody).OfType<TextBox>().Select(box=>box.Text));
        Check(publicText.Contains("customVoltage: 12.4",StringComparison.Ordinal)&&publicText.Contains("customCounter: 17",StringComparison.Ordinal)&&
            !publicText.Contains("fixture-public-secret",StringComparison.Ordinal),
            "Arbitrary explicit public structured fields remain readable while credential fields are redacted.");
        foreach(var metadata in new[] { CerebralJson.Element("internal scalar"),CerebralJson.Element(new[]{"internal array"}),CerebralJson.Element(null) })
        {
            var scalarEvent=events[0] with { Data=metadata };
            var body=(StackPanel)bodyMethod.Invoke(workspace,[scalarEvent])!;
            Check(body.Children.Count==1,"A contract-valid scalar, array or null event metadata value cannot abort its public content rendering.");
        }
        workspace.BeginConversation("another-chat");activity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(navigation.Count==1,"A detached summary from an old chat cannot navigate after the workspace binding changes.");
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
