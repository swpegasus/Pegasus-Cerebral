using Cerebral.Core;
using Cerebral.Worker;
namespace Cerebral.Worker.Tests;
public static class AdaptiveCapacityPolicyTests
{
 public static void Run()
 {
  var count=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);count++;Console.WriteLine("PASS: "+name);}
  var model=new ModelRecord{Id="model",SizeBytes=1024L*1024*1024,ContextTokens=4096,MaximumContextTokens=48128,NativeContextTokens=131072,
   ParallelRequests=2,AutomaticContextSizing=true,GpuLayers=-1,BlockCount=32,EmbeddingLength=4096,AttentionHeadCount=32,AttentionHeadCountKv=8};
  var resources=new ResourceInfo(10,100000,[new("GB10",128000,100000,true)]);
  var hash=new string('a',64);
  var plan=AdaptiveModelCapacityPolicy.Plan(model,resources,4096,3,16384,"instance",hash);
  Check(plan.State=="trial_required"&&plan.ObservedSafeSlots is null,"configured target and memory estimate never become measured capacity");
  Check(plan.Candidates.Select(x=>x.ParallelSlots).SequenceEqual(new[]{1,2,3}),"local target stages one then two then three");
  Check(plan.Candidates.All(x=>x.Plan.MaximumContextTokens<=48128&&x.Plan.ContextTokens<=48128),"positive configured cap remains respected");
  Check(model.ParallelRequests==2&&model.ContextTokens==4096,"planning does not mutate model settings");
  Check(plan.Candidates.All(x=>x.Plan.AvailableMemoryMb==100000),"GB10 uses one shared pool without RAM plus VRAM sum");
  var proof=new ConcurrentCapacityObservation("model","instance",hash,2,2,[16384,16384],[12,11],2,2,8192,false,"real-runtime");
  var safe=AdaptiveModelCapacityPolicy.Plan(model,resources,4096,3,16384,"instance",hash,[proof]);
  Check(safe.ObservedSafeSlots==2&&safe.RequestedTargetSlots==3,"exact real overlap proves tested two slots only");
  foreach(var bad in new[]{proof with{PeakOverlappingGenerations=1},proof with{EvidenceLevel="deterministic-fixture"},
   proof with{ReleasedLeases=1},proof with{HasUnknownOutcome=true},proof with{InstanceId="other"},
   proof with{BindingSha256=new string('b',64)},proof with{PerSlotContextTokens=[8192,16384]},
   proof with{PerSlotTokensPerSecond=[double.NaN,12]},proof with{MeasuredPoolAvailableAfterMb=2048}})
   Check(AdaptiveModelCapacityPolicy.Plan(model,resources,4096,3,16384,"instance",hash,[bad]).ObservedSafeSlots is null,
    "incomplete/fake/nonoverlapping/stale/unsafe trial cannot qualify capacity");
  var absent=new ResourceInfo(10,null,[new("GPU",null,null,false,"unavailable")]);
  Check(AdaptiveModelCapacityPolicy.Plan(model,absent,4096,3,16384,"instance",hash).ObservedSafeSlots is null,"missing memory remains unavailable, not zero-safe");
  Console.WriteLine($"AdaptiveCapacityPolicyTests: {count} pure planning/observation fixtures; no model/runtime action.");
 }
}
