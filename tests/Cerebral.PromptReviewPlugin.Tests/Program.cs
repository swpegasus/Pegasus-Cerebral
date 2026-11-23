using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.PromptReviewPlugin;

internal static class Program
{
 private static int checks;
 internal static int CheckCount => checks;
 [STAThread] private static int Main() => RunAll();
 internal static int RunAll()
 {
  try { _=new Application(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Run(); AuthoritativeCorrections(); RoutingAndSchema(); CancelInitialInference(); LateReviewCleanup(false); LateReviewCleanup(true); Console.WriteLine($"Prompt Review: {checks} deterministic draft/CAS/private-conversation/WPF checks passed; no real model, Worker, main chat or owner mutation."); return 0; }
  catch(Exception e){Console.Error.WriteLine(e);return 1;}
 }
 private static void LateReviewCleanup(bool error)
 {
  using var plugin=new PromptReviewPlugin(); using var cancelled=new CancellationTokenSource();
  var delayed=new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
  var host=new Host { Generate=(number, _) => number==2 ? delayed.Task : Task.FromResult(new DesktopServiceCommandResult(true,JsonSerializer.SerializeToElement(new { text=JsonSerializer.Serialize(new { prompt="Refined draft "+number,message="Fixture reply." }) }))) };
  plugin.Initialize(host); var mode=plugin.InputModes.Single();
  var first=mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"old",host.Text),false),cancelled.Token).Result;
  var nodes=Nodes(first.Guidance!).ToArray();
  nodes.OfType<TextBox>().Single(box=>AutomationProperties.GetAutomationId(box)=="PromptReview.Correction").Text="A delayed correction";
  var refine=nodes.OfType<Button>().Single(button=>AutomationProperties.GetAutomationId(button)=="PromptReview.Refine"); Click(refine);
  Check(refine.Content?.ToString()=="Refining…","The old review is awaiting its fixture reply");
  cancelled.Cancel();
  var newer=mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"new",host.Text),false),default).Result;
  if(error) delayed.SetException(new InvalidOperationException("Late fixture failure"));
  else delayed.SetResult(new DesktopServiceCommandResult(true,JsonSerializer.SerializeToElement(new { text=JsonSerializer.Serialize(new { prompt="Old response",message="Old response" }) })));
  var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(3);
  while(refine.Content?.ToString()!="Refine" && DateTime.UtcNow<deadline) { var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame); }
  Check(refine.Content?.ToString()=="Refine" && host.Dismissals==0,"Late "+(error?"failure":"cancellation")+" cannot dismiss newer guidance");
  Click(Nodes(newer.Guidance!).OfType<Button>().Single(button=>AutomationProperties.GetAutomationId(button)=="PromptReview.UseDraft"));
  Check(host.Text=="Refined draft 3" && host.Dismissals==1,"The newer review remains applicable after old cleanup");
 }
 private static void Run()
 {
  using var plugin=new PromptReviewPlugin();
  var host=new Host(); plugin.Initialize(host);
  var mode=plugin.InputModes.Single();
  var input=new DesktopInputRequest(host.GetConversation(),new(host.Binding,"r1",host.Text),false);
  var held=mode.InterceptAsync(input,default).GetAwaiter().GetResult();
  Check(held.State==DesktopInputInterceptionState.Hold && host.Submissions==0 && host.Text=="Original draft","Review holds without sending or rewriting");
  var view=held.Guidance!;
  var nodes=Nodes(view).ToArray();
  Check(!nodes.OfType<Button>().Any(b=>b.Content?.ToString()?.StartsWith("Send")==true),"No send controls in refinement");
  var feedback=nodes.OfType<TextBox>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.Correction");
  feedback.Text="Make it more concise.";
  Click(nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.Refine"));
  Check(host.Calls.Count==2 && host.Text=="Original draft" && host.Submissions==0,"Back-and-forth stays outside main draft and chat");
  var discussion=nodes.OfType<TextBox>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.Conversation").Text;
  Check(discussion.Contains("Make it more concise.") && discussion.Contains("Updated the draft."),"Refinement exchange visible");
  Click(nodes.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.UseDraft"));
  Check(host.Text=="Refined draft 2" && host.Revision==2 && host.Dismissals==1 && host.Submissions==0,"Approval replaces original exact draft and returns without send");
  Check(mode.PrepareAsync is null,"Selecting refinement has no background model-loading preparation");
  Check(mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"r3",host.Text),false),default).Result.State==DesktopInputInterceptionState.Hold,"A newly selected review always reviews; host consumes the one-shot selection before the later Send");
  // A concurrent edit cannot be hidden by the review's eventual replacement.
  var current=mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"r4",host.Text),false),default).Result;
  host.ExternalEdit("New owner text");
  Click(Nodes(current.Guidance!).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.UseDraft"));
  Check(host.Text=="New owner text" && host.Submissions==0,"Concurrent draft edit preserved");
  Check(Nodes(current.Guidance!).OfType<TextBlock>().Any(b=>b.Text.Contains("draft changed")),"Conflict shown in review");
  Click(Nodes(current.Guidance!).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="PromptReview.Cancel"));
  Check(host.Text=="New owner text" && host.Submissions==0,"Cancel preserves original/current composer");
  var bad=new Host{Malformed=true}; using var invalid=new PromptReviewPlugin();invalid.Initialize(bad);
  Check(invalid.InputModes.Single().InterceptAsync(new(bad.GetConversation(),new(bad.Binding,"bad",bad.Text),false),default).Result.State==DesktopInputInterceptionState.Cancel && bad.Submissions==0,"Malformed structured reply fails without submission");
  var cancelled=new CancellationToken(true);
  Check(mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"cancel",host.Text),false),cancelled).Result.State==DesktopInputInterceptionState.Cancel,"Cancelled inference leaves no job");
  var steering=mode.InterceptAsync(new(host.GetConversation(),new(host.Binding,"steer",host.Text),true),default).Result;
  Check(steering.State==DesktopInputInterceptionState.Pass,"Steering preserved");
 }


 private static void AuthoritativeCorrections()
 {
  foreach(var directEdit in new[]{false,true})
  {
   using var plugin=new PromptReviewPlugin(); var host=new Host{Text="Calculate the reset date."}; plugin.Initialize(host);
   var review=plugin.InputModes.Single().InterceptAsync(new(host.GetConversation(),new(host.Binding,"authored",host.Text),false),default).Result;
   var nodes=Nodes(review.Guidance!).ToArray();
   var editor=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Interpretation");
   var correction=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Correction");
   const string authored="Calculate the reset date.\nDo not use tools.\nDo not solve the calculation.\nPreserve exact names and paths.";
   if(directEdit){editor.Text=authored;correction.Text="Make the wording concise.";}else correction.Text="Do not use tools. Do not solve the calculation. Preserve exact names and paths.";
   var expectedDraft=editor.Text; var expectedCorrection=correction.Text;
   host.Reply=JsonSerializer.Serialize(new{prompt=authored,message="Kept your constraints."});
   Click(nodes.OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Refine"));
   Check(editor.Text==authored,"Later "+(directEdit?"direct edit":"feedback")+" remains authoritative through refinement");
   var request=host.Calls.Last().GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
   Check(request.Contains(expectedDraft) && request.Contains(expectedCorrection),"Refinement sends the actual edited draft and latest correction");
   Check(host.Text=="Calculate the reset date." && host.Submissions==0,"Refinement preserves the composer until Apply");
  }
  foreach(var correction in new[]{"Current value: 8", "Use 8 instead of 0."})
  {
   using var plugin=new PromptReviewPlugin();var host=new Host{Text="Report the current value. Current value: 0."};plugin.Initialize(host);
   var review=plugin.InputModes.Single().InterceptAsync(new(host.GetConversation(),new(host.Binding,"status",host.Text),false),default).Result;
   var nodes=Nodes(review.Guidance!).ToArray();
   nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Correction").Text=correction;
   host.Reply=JsonSerializer.Serialize(new{prompt="Report the current value. Current value: 8.",message="Corrected the value."});
   Click(nodes.OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Refine"));
   var text=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Interpretation").Text;
   Check(text.Contains("Current value: 8") && !text.Contains("Current value: 0"),"Later explicit or unlabeled value correction cannot restore a stale original fact");
  }
  using(var plugin=new PromptReviewPlugin())
  {
   var host=new Host{Text="Report the current value. Current value: 0."};plugin.Initialize(host);
   var review=plugin.InputModes.Single().InterceptAsync(new(host.GetConversation(),new(host.Binding,"edited-status",host.Text),false),default).Result;
   var nodes=Nodes(review.Guidance!).ToArray();var editor=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Interpretation");
   editor.Text="Report the current value. Current value: 9.";
   nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Correction").Text="Keep it concise.";
   host.Reply=JsonSerializer.Serialize(new{prompt=editor.Text,message="Kept your edited value."});
   Click(nodes.OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="PromptReview.Refine"));
   Check(editor.Text.Contains("Current value: 9") && !editor.Text.Contains("Current value: 0"),"A directly edited current value supersedes the original fact");
  }
 }

 private static DesktopInputInterception Review(Host host)
 {
  using var plugin=new PromptReviewPlugin(); plugin.Initialize(host);
  return plugin.InputModes.Single().InterceptAsync(new(host.GetConversation(),new(host.Binding,"route",host.Text),false),default).GetAwaiter().GetResult();
 }
 private static void RoutingAndSchema()
 {
  var local=new Host(); Check(Review(local).State==DesktopInputInterceptionState.Hold,"Configured local clarifier returns a review");
  var args=local.Calls.Single(); var schema=args.GetProperty("responseSchema");
  Check(local.Operations.SequenceEqual(new[]{"agents.list","inference.generate"}) && args.GetProperty("modelId").GetString()=="textmetrics-qwen35-9b" && !args.TryGetProperty("workerId",out _),"Exact local model route has no load, worker routing, or agent-work submission");
  Check(args.GetProperty("json").GetBoolean() && schema.GetProperty("type").GetString()=="object" && !schema.GetProperty("additionalProperties").GetBoolean() &&
   schema.GetProperty("required").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"prompt","message"}),"Generation requests the exact two-field structured response");
  Check(schema.GetProperty("properties").GetProperty("prompt").GetProperty("maxLength").GetInt32()==16384 &&
   schema.GetProperty("properties").GetProperty("message").GetProperty("maxLength").GetInt32()==4096 &&
   args.GetProperty("maxTokens").GetInt32()==1024 && args.GetProperty("reasoningMode").GetString()=="off","Schema and inference bounds remain explicit");
  Check(args.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!.Contains("Original draft"),"Original request reaches the model unchanged");
  var remote=new Host{ModelWorkerId="gb10"}; Check(Review(remote).State==DesktopInputInterceptionState.Hold,"Configured remote clarifier returns a review");
  Check(remote.Operations.SequenceEqual(new[]{"agents.list","workers.inference.generate"}) && remote.Calls.Single().GetProperty("workerId").GetString()=="gb10","Remote model tuple never loads or falls back locally");
  foreach(var host in new[]{new Host{GenerationFailure=true},new Host{ModelWorkerId="gb10",GenerationFailure=true}})
  {
   Check(Review(host).State==DesktopInputInterceptionState.Cancel && host.Text=="Original draft" && host.Revision==1 && host.Submissions==0 && host.Calls.Count==1,"Provider failure preserves the draft and performs no fallback or submission");
  }
  foreach(var host in new[]{new Host{ProfileId="lead"},new Host{ModelId=""},new Host{ModelWorkerId=null},new Host{ModelWorkerId=""},new Host{ListFailure=true}})
  {
   Check(Review(host).State==DesktopInputInterceptionState.Cancel && host.Operations.SequenceEqual(new[]{"agents.list"}) && host.Text=="Original draft","Missing or invalid clarifier configuration fails before generation");
  }
  var implicitLocal=new Host{IncludeWorker=false};
  Check(Review(implicitLocal).State==DesktopInputInterceptionState.Hold && implicitLocal.Operations.Last()=="inference.generate","Absent legacy worker field defaults to local");
  var invalidReplies=new[]{
   "not json", "[]", "{\"prompt\":\"Missing message\"}",
   "{\"prompt\":17,\"message\":\"Wrong type\"}",
   JsonSerializer.Serialize(new{prompt="Valid",message="Reply",extra=true}),
   JsonSerializer.Serialize(new{prompt=" ",message="Reply"}),
   JsonSerializer.Serialize(new{prompt="Valid",message=" "}),
   JsonSerializer.Serialize(new{prompt=new string('p',16385),message="Reply"}),
   JsonSerializer.Serialize(new{prompt="Valid",message=new string('m',4097)}),
   new string('x',24577)};
  foreach(var reply in invalidReplies)
  {
   var bad=new Host{Reply=reply};
   Check(Review(bad).State==DesktopInputInterceptionState.Cancel && bad.Text=="Original draft" && bad.Revision==1 && bad.Submissions==0,"Invalid structured reply preserves the original draft");
  }
  var oversized=new Host{Text=new string('x',16385)};
  Check(Review(oversized).State==DesktopInputInterceptionState.Cancel && oversized.Calls.Count==0 && oversized.Revision==1,"Oversized input fails before generation");
  using var diagnostics=JsonDocument.Parse(PromptReviewPluginDiagnostics.RunDeterministicChecks());
  Check(diagnostics.RootElement.GetProperty("success").GetBoolean() && !diagnostics.RootElement.GetProperty("modelInvoked").GetBoolean(),"Reviewer-only constraints are removed while explicit user constraints and supplied status survive");
 }
 private static void CancelInitialInference()
 {
  using var plugin=new PromptReviewPlugin(); using var cancel=new CancellationTokenSource();
  var pending=new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
  var host=new Host{Generate=(_,_)=>pending.Task}; plugin.Initialize(host);
  var task=plugin.InputModes.Single().InterceptAsync(new(host.GetConversation(),new(host.Binding,"cancel-inflight",host.Text),false),cancel.Token);
  Check(!task.IsCompleted,"Initial inference can remain in flight without changing the draft");
  cancel.Cancel();
  Check(host.GenerationToken.IsCancellationRequested,"Cancellation reaches the actual generation token");
  pending.SetResult(new(true,JsonSerializer.SerializeToElement(new{text=JsonSerializer.Serialize(new{prompt="Late reply",message="Late reply"})})));
  var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(3);
  while(!task.IsCompleted && DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  Check(task.IsCompleted && task.Result is {State:DesktopInputInterceptionState.Cancel,Guidance:null} && host.Text=="Original draft" && host.Submissions==0,"Late initial response after cancellation creates no card or replacement");
 }

 private static IEnumerable<DependencyObject> Nodes(DependencyObject root){yield return root;foreach(var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var n in Nodes(c))yield return n;}
 private static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
 private static void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);checks++;}
 private sealed class Host:IDesktopPluginHost,IDesktopServiceHost
 {
  internal DesktopConversationBinding Binding=new("fixture","chat");
  internal string Text="Original draft"; internal long Revision=1; internal int Submissions,Dismissals;internal bool Malformed;
  internal List<JsonElement> Calls=[];
  internal List<string> Operations=[];
  internal string ProfileId="clarifier", ModelId="textmetrics-qwen35-9b";
  internal string? ModelWorkerId="local", Reply;
  internal bool IncludeWorker=true, ListFailure, GenerationFailure;
  internal CancellationToken GenerationToken;
  internal Func<int,CancellationToken,Task<DesktopServiceCommandResult>>? Generate;
  public DesktopConversationSnapshot GetConversation()=>new(Binding,"Fixture",null,null,false);
  public DesktopDraftSnapshot GetDraft()=>new(Binding,Revision,Text);
  public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged {add{} remove{}}
  public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted {add{} remove{}}
  internal void ExternalEdit(string text){Text=text;Revision++;}
  public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update,CancellationToken ct=default){ct.ThrowIfCancellationRequested();if(update.Binding!=Binding ||update.ExpectedRevision!=Revision)return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,"Conflict"));if(Text!=update.Text){Text=update.Text;Revision++;}return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));}
  public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission input,CancellationToken ct=default){Submissions++;throw new InvalidOperationException("A refinement must never submit.");}
  public Task<DesktopOperationResult> SteerAsync(DesktopSubmission input,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding,CancellationToken ct=default)=>throw new NotSupportedException();
  public void DismissInputGuidance()=>Dismissals++;
  public void ReportStatus(string text){}
  public void RefreshPresentation(){}
  public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation,object args,CancellationToken ct=default)
  {
   ct.ThrowIfCancellationRequested();
   Operations.Add(operation);
   if(operation=="agents.list")
   {
    if(ListFailure)return Task.FromResult(new DesktopServiceCommandResult(false,default,"Fixture profiles unavailable."));
    object profiles=IncludeWorker ? new[]{new{id=ProfileId,modelId=ModelId,modelWorkerId=ModelWorkerId}} : (object)new[]{new{id=ProfileId,modelId=ModelId}};
    return Task.FromResult(new DesktopServiceCommandResult(true,JsonSerializer.SerializeToElement(profiles)));
   }
   if(operation is not ("inference.generate" or "workers.inference.generate"))throw new InvalidOperationException("Unexpected "+operation);
   GenerationToken=ct;
   Calls.Add(JsonSerializer.SerializeToElement(args));
   if(GenerationFailure)return Task.FromResult(new DesktopServiceCommandResult(false,default,"Fixture provider failure."));
   if(Generate is not null)return Generate(Calls.Count,ct);
   var text=Reply ?? (Malformed?"{\"prompt\":\"incomplete\"}":JsonSerializer.Serialize(new{prompt="Refined draft "+Calls.Count,message=Calls.Count==1?"First interpretation.":"Updated the draft."}));
   return Task.FromResult(new DesktopServiceCommandResult(true,JsonSerializer.SerializeToElement(new{text})));
  }
 }
}
