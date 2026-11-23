using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Engine.Persistence;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Activation;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;
public static class EngineCapabilityCheckTests
{
 public static async Task RunAsync()
 {
  var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
  async Task Reject(Func<Task> action,string name){var bad=false;try{await action();}catch(Exception x)when(x is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or InvalidDataException){bad=true;}Check(bad,name);}
  var hash=new string('a',64);
  var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"engine-check-fixture"));
  EngineCheckProfilePin Pin(string id,int rev,string role,int context)=>
   new(id,rev,role,"local","model","worker-remote",context,1024,"off",0);
  var lead=Pin("lead",3,"lead",31744);var alpha=Pin("00-engine-check-alpha",10,"helper",31744);var beta=Pin("00-engine-check-beta",2,"helper",48128);
  var id=Guid.NewGuid().ToString("N");
  var plan=new EngineCapabilityCheckPlan(1,id,"A",5,hash,hash,"owner",root,"pegasus.conversation:chat","1.0.1",id,
   Guid.NewGuid().ToString("N"),lead,alpha,beta);
  var client=new FixtureClient(plan);
  var inspect=await EngineCapabilityCheck.InspectAsync(client,plan,default);
  Check(inspect.ExactEnginePinsMatch&&inspect.ExactProfilePinsMatch&&inspect.TargetSelected,"inspect compares exact owner/engine/profile route and revision pins");
  Check(client.Calls.SequenceEqual(new[]{"health","engine.status","agents.list"}),"inspect performs reads only and never modifies helper profiles");
  Check(alpha.ContextTokens!=beta.ContextTokens,"distinct historical helper contexts remain explicit");
  var release=new EngineCapabilityCheckRelease("10000000-0000-4000-8000-000000000003",
   EngineCapabilityCheck.PlanSha256(plan),DateTimeOffset.UtcNow.AddSeconds(-1),DateTimeOffset.UtcNow.AddMinutes(1),hash,true);
  var consumed=0;
  var receipt=await EngineCapabilityCheck.SubmitOnceAsync(client,plan,release,()=>consumed++,default);
  Check(consumed==1&&receipt.SubmittedOnce&&!receipt.CompletionVerified,"one accepted submission does not claim completed capability");
  Check(client.Calls.Count(x=>x=="jobs.workflow.submit")==1&&client.Calls.All(x=>x!="agents.save"&&!x.StartsWith("models.")&&!x.StartsWith("engine.activate")),
   "one reusable process does not save profiles/switch/load/promote");
  var planB=plan with{TargetSlot="B",CheckId=Guid.NewGuid().ToString("N")};planB=planB with{RequestId=planB.CheckId};
  var clientB=new FixtureClient(planB);
  var releaseB=release with{PlanSha256=EngineCapabilityCheck.PlanSha256(planB)};
  Check((await EngineCapabilityCheck.SubmitOnceAsync(clientB,planB,releaseB,()=>{},default)).SubmittedOnce,"same parameterized contract supports already active B");
  clientB.Status=clientB.Status with{Paused=true};
  await Reject(()=>EngineCapabilityCheck.SubmitOnceAsync(clientB,planB,releaseB,()=>throw new Exception("must not consume"),default),
   "paused target refuses before consumption without automatic resume");
  await Reject(()=>EngineCapabilityCheck.SubmitOnceAsync(client,plan,release with{PlanSha256=new string('b',64)},()=>{},default),"wrong Root plan digest refuses");
  client.Profiles[1].Revision++;
  await Reject(()=>EngineCapabilityCheck.SubmitOnceAsync(client,plan,release,()=>{},default),"changed exact helper revision cannot inherit old check");
  Check(client.Calls.Count(x=>x=="jobs.workflow.submit")==1,"rejected changed profile never resubmits accepted work");
  await Reject(()=>{EngineCapabilityCheck.Validate(plan with{RequestId="engine-coordination-gb10-20261008"});return Task.CompletedTask;},"old fixed canary identity cannot become fresh trial");
  await Reject(()=>{EngineCapabilityCheck.Validate(plan with{Alpha=alpha with{Id="B"}});return Task.CompletedTask;},"helper identity is never relabeled engine B");
  client.Profiles[1].Revision=10;
  var retained=await EngineCapabilityCheck.ReadRetainedAsync(client,plan,plan.RequestId,default);
  Check(!retained.SubmittedOnce&&!retained.CompletionVerified,"retained read does not mutate or fabricate child proof");
  Check(client.Calls.Count(x=>x=="jobs.workflow.submit")==1,"retained inspection never submits again");
  client.Status=client.Status with{Epoch=6};
  Check(!(await EngineCapabilityCheck.InspectAsync(client,plan,default)).ExactEnginePinsMatch,"epoch drift is visible instead of normalized");
  client.Status=client.Status with{Epoch=5};
  var proof=new ProofClient(plan);
  var proved=await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default);
  Check(proved.CompletionVerified&&!proved.SubmittedOnce,"exact completed parent and two isolated accepted child results qualify bounded retained proof");
  var originalAlpha=proof.Runs["child-alpha"];
  proof.Runs["child-alpha"]=originalAlpha with{Submission=originalAlpha.Submission with{SettingsSnapshot=WorkflowContract.EmptyObject}};
  Check(!(await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default)).CompletionVerified,"unrecognized old child snapshot stays unverified");
  proof.Runs["child-alpha"]=originalAlpha with{Submission=originalAlpha.Submission with{ParentRunId="unrelated"}};
  Check(!(await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default)).CompletionVerified,"foreign child lineage cannot prove a helper result");
  proof.Runs["child-alpha"]=originalAlpha with{Result=CerebralJson.Element(new{text="Did not compute ALPHA_RESULT: 5"})};
  Check(!(await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default)).CompletionVerified,"quoted or denied marker is not an actual exact helper result");
  proof.Runs["child-alpha"]=originalAlpha;
  var genuine=proof.Page;
  proof.Page=new([],1,null);
  Check(!(await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default)).CompletionVerified,"missing delegation result remains unverified");
  proof.Page=genuine with{Events=[genuine.Events[0] with{Type=CerebralEventTypes.Message}]};
  Check(!(await EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default)).CompletionVerified,"arbitrary runIds in unrelated event data cannot identify admitted children");
  proof.Page=genuine with{Events=[genuine.Events[0] with{ConversationId="foreign"}]};
  await Reject(()=>EngineCapabilityCheck.ReadRetainedAsync(proof,plan,plan.RequestId,default),"foreign journal conversation fails before child adoption");
  proof.Page=genuine;
  Check(!proof.Calls.Contains("jobs.workflow.submit"),"proof collection never submits or repeats provider work");
  Console.WriteLine($"EngineCapabilityCheckTests: {checks} deterministic plan/read/submit fixtures; no Source/provider calls.");
 }
 private sealed class FixtureClient(EngineCapabilityCheckPlan plan):IEngineCapabilityCheckClient
 {
  public List<string> Calls{get;}=[];
  public List<AgentProfile> Profiles{get;}=new[]{plan.Lead,plan.Alpha,plan.Beta}.Select(pin=>new AgentProfile{Id=pin.Id,Revision=pin.Revision,Role=pin.Role,
   WorkerId=pin.WorkerId,ModelId=pin.ModelId,ModelWorkerId=pin.ModelWorkerId,ContextTokens=pin.ContextTokens,ResponseTokens=pin.ResponseTokens,
   ReasoningMode=pin.ReasoningMode,ReasoningTokens=pin.ReasoningTokens}).ToList();
  public EngineStatus Status{get;set;}=new(false,plan.ExpectedEpoch,0,0,false,
   new(plan.TargetSlot,"fixture","1.0","Fixture","fixture.dll",plan.ExpectedPackageSha256,plan.ExpectedConfigurationSha256,plan.ExpectedPackageSha256),
   plan.TargetSlot,null,[new(plan.TargetSlot,true,false,new(plan.TargetSlot,"fixture","1.0","Fixture","fixture.dll",plan.ExpectedPackageSha256,
    plan.ExpectedConfigurationSha256,plan.ExpectedPackageSha256),null)],0,null);
  public Task<T> GetAsync<T>(string operation,object arguments,CancellationToken token)
  {
   Calls.Add(operation);
   object result=operation switch{
    "health"=>new HostInfo("Pegasus Cerebral","fixture",1,"fixture","fixture","fixture",true,plan.ExpectedDataDirectory,true,plan.ExpectedOwnerInstallationId),
    "engine.status"=>Status,"agents.list"=>Profiles,
    "jobs.workflow.submit" or "jobs.workflow.get"=>new WorkflowRun{Id=plan.RequestId,Revision=1,Status=WorkflowStatus.Queued,
      Submission=new(plan.RequestId,plan.ConversationId,plan.Lead.Id,plan.DefinitionId,plan.DefinitionVersion,"fixture",null,
       WorkflowContract.EmptyObject,WorkflowContract.EmptyObject)},
    _=>throw new InvalidOperationException("Unexpected fixture operation")};
   return Task.FromResult((T)result);
  }
 }
 private sealed class ProofClient:IEngineCapabilityCheckClient
 {
  private readonly EngineCapabilityCheckPlan plan;
  public List<string> Calls{get;}=[];
  public Dictionary<string,WorkflowRun> Runs{get;}=new(StringComparer.Ordinal);
  public JournalPage Page{get;set;}
  public ProofClient(EngineCapabilityCheckPlan p)
  {
   plan=p;
   Runs[p.RequestId]=Run(p.RequestId,p.Lead,p.RequestId,p.ConversationId,null,"joined");
   Runs["child-alpha"]=Run("child-alpha",p.Alpha,"child-alpha","child-alpha",p.RequestId,"ALPHA_RESULT: 5");
   Runs["child-beta"]=Run("child-beta",p.Beta,"child-beta","child-beta",p.RequestId,"BETA_RESULT: 3");
   var result=new WorkflowCommandResult(WorkflowEffectState.Succeeded,CerebralJson.Element(new{runIds=new[]{"child-alpha","child-beta"}}));
   var item=new CerebralEvent(Guid.NewGuid().ToString("N"),p.ConversationId,p.RequestId,1,CerebralEventTypes.ToolResult,
    new(EventActorKind.Tool,WorkerWorkflowEffects.DelegateCommand),
    [new StructuredContentPart("workflow.command.result",CerebralJson.Element(result))],
    new(ModelInclusion.Evidence,HumanVisibility.Activity),
    new(EventSourceKind.Tool,WorkerWorkflowEffects.DelegateCommand,JobId:p.RequestId),DateTimeOffset.UtcNow,
    CerebralJson.Element(new ToolResultData("effect",WorkerWorkflowEffects.DelegateCommand,true)),CorrelationId:"effect");
   Page=new([item],1,null);
  }
  private WorkflowRun Run(string id,EngineCheckProfilePin pin,string request,string conversation,string? parent,string text)
  {
   var profile=new AgentProfile{Id=pin.Id,Revision=pin.Revision,Role=pin.Role,WorkerId=pin.WorkerId,ModelId=pin.ModelId,
    ModelWorkerId=pin.ModelWorkerId,ContextTokens=pin.ContextTokens,ResponseTokens=pin.ResponseTokens,
    ReasoningMode=pin.ReasoningMode,ReasoningTokens=pin.ReasoningTokens};
   var model=new WorkerExecutionSnapshot.ModelBinding(pin.ModelId,"/fixture/model.gguf",null,[],48128,2,-1,
    ModelWorkerId:pin.ModelWorkerId,ModelWorkerInstallationId:"fixture-remote-owner");
   var snapshot=new WorkerExecutionSnapshot.Snapshot(3,profile,"fixture","fixture",model,[],[],[],[],false,"fixture",[],[]);
   return new WorkflowRun{Id=id,Revision=4,Status=WorkflowStatus.Completed,Result=CerebralJson.Element(new{text}),
    Submission=new(request,conversation,pin.Id,plan.DefinitionId,plan.DefinitionVersion,"fixture",null,
     WorkflowContract.EmptyObject,CerebralJson.Element(snapshot)){ParentRunId=parent}};
  }
  public Task<T> GetAsync<T>(string operation,object arguments,CancellationToken token)
  {
   Calls.Add(operation);
   object result=operation switch{
    "health"=>new HostInfo("Pegasus Cerebral","fixture",1,"fixture","fixture","fixture",true,plan.ExpectedDataDirectory,true,plan.ExpectedOwnerInstallationId),
    "jobs.workflow.get"=>Runs[CerebralJson.Element(arguments).GetProperty("id").GetString()!],
    "jobs.workflow.events"=>Page,
    _=>throw new InvalidOperationException("Unexpected proof fixture operation")};
   return Task.FromResult((T)result);
  }
 }

}
