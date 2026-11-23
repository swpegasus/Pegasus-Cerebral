using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

internal static class LogicalContextAffinityChecks
{
    private static readonly List<string> passed = [];
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); passed.Add(label); }
    private static LocalContextAffinityKey Key(char c) => new(new string(c,64));
    private static async Task Refuses<T>(Func<Task> action, string label) where T:Exception
    { try { await action(); } catch(T) { Check(true,label); return; } throw new InvalidOperationException(label); }

    internal static async Task RunAsync()
    {
        await ArbiterAsync(); await BindingsAsync(); await PreparedAsync(); await LeaseRaceAsync();
        Check(OwnershipRoleObservationChecks.Run().Length > 0,"existing ownership/unknown projection checks remain accepted");
        Console.WriteLine(JsonSerializer.Serialize(new { schema="pegasus.logical-context-affinity-checks/1",
            success=true, assertions=passed.Count, labels=passed,
            evidenceTier="managed arbiter/host-binding unit tests and actual compiled prepared-reasoning objects with deterministic in-memory HTTP; no live native/model/GPU/network",
            liveModelLoads=0, liveGenerations=0, hardwareCalls=0, networkCalls=0, cacheContentsOrPhysicalReleaseProved=false }));
    }
    private static async Task ArbiterAsync()
    {
        var a=Key('a'); var b=Key('b'); var c=Key('c');
        var pool=new InferenceSlotArbiter(2);
        var sa=await pool.AcquireForContextAsync(InferencePriority.Normal,default,a);
        var sb=await pool.AcquireForContextAsync(InferencePriority.Normal,default,b);
        pool.Return(sb);pool.Return(sa);
        Check(await pool.AcquireForContextAsync(InferencePriority.Normal,default,a)==sa,"matching idle context bypasses return order");
        Check(await pool.AcquireForContextAsync(InferencePriority.Normal,default,b)==sb,"other matching idle context retains its slot");
        pool.Return(sb);
        Check(await pool.AcquireForContextAsync(InferencePriority.Normal,default,a)==sb,"busy preferred slot never reserves other idle capacity");
        pool.Return(sa);pool.Return(sb);pool.Complete();

        var fifo=new InferenceSlotArbiter(2);
        var f0=await fifo.AcquireAsync(InferencePriority.Normal,default);
        var f1=await fifo.AcquireAsync(InferencePriority.Normal,default);
        fifo.Return(f1);fifo.Return(f0);
        Check(await fifo.AcquireAsync(InferencePriority.Normal,default)==f1,"unbound acquisition preserves original FIFO");
        Check(await fifo.AcquireAsync(InferencePriority.Normal,default)==f0,"unbound acquisition keeps remaining FIFO order");
        fifo.Return(f0);fifo.Return(f1);fifo.Complete();

        var queue=new InferenceSlotArbiter(1);
        var held=await queue.AcquireForContextAsync(InferencePriority.Normal,default,a);
        var normal=queue.AcquireForContextAsync(InferencePriority.Normal,default,a);
        var interactive=queue.AcquireForContextAsync(InferencePriority.Interactive,default,b);
        queue.Return(held);
        Check(await interactive==held&&!normal.IsCompleted,"affinity does not bypass existing interactive waiter priority");
        queue.Return(held);Check(await normal==held,"normal waiter remains live after priority handoff");
        using var cancel=new CancellationTokenSource();
        var cancelled=queue.AcquireForContextAsync(InferencePriority.Normal,cancel.Token,b);
        var following=queue.AcquireForContextAsync(InferencePriority.Normal,default,c);
        cancel.Cancel();await Refuses<OperationCanceledException>(async()=>await cancelled,"queued cancellation consumes no returned capacity");
        queue.Return(held);Check(await following==held,"cancelled head cannot strand the next waiter");
        queue.InvalidateContext(held);
        Check(queue.AvailableCount==0,"affinity invalidation never confirms capacity release");
        queue.Return(held);Check(queue.AvailableCount==1,"only actual return restores available capacity");
        await Refuses<InvalidOperationException>(()=>{queue.Return(held);return Task.CompletedTask;},"duplicate return cannot create capacity");
        queue.Complete();await Refuses<ChannelClosedException>(async()=>await queue.AcquireForContextAsync(InferencePriority.Normal,default,a),"retired instance rejects affinity acquisition");

        var invalid=new InferenceSlotArbiter(2);
        var i0=await invalid.AcquireForContextAsync(InferencePriority.Normal,default,a);
        var i1=await invalid.AcquireForContextAsync(InferencePriority.Normal,default,b);
        invalid.Return(i1);invalid.Return(i0);invalid.InvalidateContext(i0);
        Check(await invalid.AcquireForContextAsync(InferencePriority.Normal,default,a)==i1,"invalidated idle affinity cannot be selected as a cache promise");
        invalid.Return(i1);invalid.Complete();
        using var preCancelled=new CancellationTokenSource();preCancelled.Cancel();
        var early=new InferenceSlotArbiter(1);
        await Refuses<OperationCanceledException>(async()=>await early.AcquireForContextAsync(InferencePriority.Normal,preCancelled.Token,a),"pre-cancelled context changes no allocation");
        Check(early.AvailableCount==1,"pre-cancelled request leaves all capacity available");early.Complete();
    }

    private static WorkerExecutionSnapshot.Snapshot Snapshot(string owner="owner-a",int revision=1,string worker="local")
    {
        var profile=new AgentProfile { Id=owner,Revision=revision,Name="Fixture",WorkerId="local",
            ModelWorkerId=worker,ModelId="fixture-model",ReasoningMode="on",ReasoningTokens=128 };
        var model=new WorkerExecutionSnapshot.ModelBinding("fixture-model",Path.GetFullPath("fixture.gguf"),null,[],32768,5,0,
            ModelWorkerId:worker,ModelWorkerInstallationId:worker=="local"?null:new string('a',32));
        return new(WorkerExecutionSnapshot.Version,profile,"","",model,[],[],[],[],false,"fixture-routing",[],[]);
    }
    private static WorkflowSubmission Submission(WorkerExecutionSnapshot.Snapshot snapshot,string conversation="conversation-a",string request="request-a") =>
        new(request,conversation,snapshot.Profile.Id,"fixture:workflow","1.0.0","synthetic-input",null,
            CerebralJson.Element(new { }),CerebralJson.Element(snapshot));
    private static ModelGenerationRequest Request(string input="first") =>
        new("fixture-model",[new("user",[new TextContentPart(input)])],32768,32,Temperature:0) { ReasoningTokens=128 };

    private static async Task BindingsAsync()
    {
        var snapshot=Snapshot();var submission=Submission(snapshot);
        var raw=new CaptureAdapter();
        var bound=WorkerLogicalContextAffinity.ForSubmission(raw,submission);
        var request=Request();await using(await bound.PrepareAsync(request,default)) { }
        var first=raw.Last;
        var multimodalRaw=new CaptureMultimodalAdapter();
        var multimodalBound=WorkerLogicalContextAffinity.ForSubmission(multimodalRaw,submission);
        Check(multimodalBound is IMultimodalModelRuntimeAdapter,"local binding preserves the multimodal provider facet");
        IReadOnlyList<RemotePreparedImageInput> images=[new(0,0,"image/png",1,1,[1,2,3])];
        using var imageCancellation=new CancellationTokenSource();
        await using(var imagePrepared=await ((IMultimodalModelRuntimeAdapter)multimodalBound).PrepareMultimodalAsync(request,images,imageCancellation.Token))
        {
            Check(ReferenceEquals(multimodalRaw.Request,request)&&ReferenceEquals(multimodalRaw.Images,images)&&multimodalRaw.Token==imageCancellation.Token,
                "multimodal binding forwards exact request/images/cancellation token");
            Check(multimodalRaw.Last==first&&ReferenceEquals(imagePrepared,multimodalRaw.Prepared),"multimodal preparation uses the same context and returns its exact preparation");
        }
        Check(WorkerLogicalContextAffinity.Current is null,"multimodal preparation restores ambient context");
        Check(first is not null&&ReferenceEquals(raw.Request,request),"host binding preserves exact request object/budgets");
        Check(WorkerLogicalContextAffinity.Current is null,"successful preparation restores ambient context");
        await using(await WorkerLogicalContextAffinity.ForSubmission(raw,submission with { RequestId="next",UserText="changed" }).PrepareAsync(Request("changed"),default)) { }
        Check(raw.Last==first,"changed request/user input in one accepted logical context preserves affinity");
        await using(await WorkerLogicalContextAffinity.ForSubmission(raw,Submission(snapshot,"conversation-b")).PrepareAsync(Request(),default)) { }
        Check(raw.Last!=first,"different conversations cannot collide");
        await using(await WorkerLogicalContextAffinity.ForSubmission(raw,Submission(Snapshot("owner-b"))).PrepareAsync(Request(),default)) { }
        Check(raw.Last!=first,"same conversation label under different profiles cannot collide");
        await using(await WorkerLogicalContextAffinity.ForSubmission(raw,Submission(Snapshot(revision:2))).PrepareAsync(Request(),default)) { }
        Check(raw.Last!=first,"accepted profile revision change invalidates prior binding");

        var compactor=snapshot with { Compactor=new(snapshot.Profile,"",snapshot.Model!) };
        await using(await WorkerLogicalContextAffinity.ForCompactor(raw,Submission(compactor),compactor).PrepareAsync(Request(),default)) { }
        Check(raw.Last!=first,"compactor purpose is distinct even for same profile/model/conversation");
        var remote=WorkerLogicalContextAffinity.ForSubmission(raw,Submission(Snapshot(worker:"peer")));
        Check(ReferenceEquals(remote,raw),"remote provider route does not receive local affinity wrapper");
        var unconfigured=snapshot with { Model=null };
        Check(ReferenceEquals(WorkerLogicalContextAffinity.ForSubmission(raw,Submission(unconfigured)),raw),"unconfigured provider route keeps existing behavior");
        Check(ReferenceEquals(WorkerLogicalContextAffinity.ForSubmission(raw,Submission(Snapshot(revision:0))),raw),"unversioned legacy owner keeps FIFO without a fabricated identity");
        await Refuses<InvalidOperationException>(()=>{WorkerLogicalContextAffinity.ForSubmission(raw,submission with { ProfileId="foreign" });return Task.CompletedTask;},"foreign admitted profile binding refuses before preparation");

        await Refuses<InvalidDataException>(()=>{WorkerLogicalContextAffinity.ForSubmission(raw,submission with { ConversationId="bad id" });return Task.CompletedTask;},"whitespace conversation identity refuses before preparation");
        await Refuses<InvalidDataException>(()=>{WorkerLogicalContextAffinity.ForSubmission(raw,submission with { ConversationId="bad\ud800" });return Task.CompletedTask;},"invalid Unicode conversation identity refuses before hashing");

        raw.Fail=true;using(WorkerLogicalContextAffinity.Enter(Key('f')))
        {
            await Refuses<IOException>(async()=>{await bound.PrepareAsync(Request(),default);},"provider preparation failure remains failure");
            Check(WorkerLogicalContextAffinity.Current==Key('f'),"failed nested preparation restores caller context");
        }
        Check(WorkerLogicalContextAffinity.Current is null,"nested scope leaves no context on caller flow");
        var x=new CaptureAdapter();var y=new CaptureAdapter();
        var bx=WorkerLogicalContextAffinity.ForSubmission(x,submission);
        var by=WorkerLogicalContextAffinity.ForSubmission(y,Submission(snapshot,"conversation-y"));
        var both=await Task.WhenAll(bx.PrepareAsync(Request(),default),by.PrepareAsync(Request(),default));
        foreach(var p in both)await p.DisposeAsync();
        Check(x.Last!=y.Last&&WorkerLogicalContextAffinity.Current is null,"concurrent preparations retain separate async-flow contexts");
    }

    private sealed class CaptureAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; }=new("fixture.adapter","1",["text"],true);
        internal LocalContextAffinityKey? Last;internal ModelGenerationRequest? Request;internal bool Fail;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,CancellationToken token)
        {
            var before=WorkerLogicalContextAffinity.Current;await Task.Yield();token.ThrowIfCancellationRequested();
            if(before!=WorkerLogicalContextAffinity.Current)throw new InvalidOperationException("context-flow-changed");
            Last=before;Request=request;if(Fail)throw new IOException("synthetic-prepare-failure");
            return new EmptyPrepared(request);
        }
    }
    private sealed class CaptureMultimodalAdapter:IModelRuntimeAdapter,IMultimodalModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor {get;}=new("fixture.multimodal","1",["text","image"],true);
        internal LocalContextAffinityKey? Last; internal ModelGenerationRequest? Request;
        internal IReadOnlyList<RemotePreparedImageInput>? Images; internal CancellationToken Token;
        internal IPreparedModelGeneration? Prepared;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,CancellationToken token)=>throw new InvalidOperationException("text-fallback-forbidden");
        public async Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,IReadOnlyList<RemotePreparedImageInput> images,CancellationToken token)
        {
            await Task.Yield(); token.ThrowIfCancellationRequested(); Last=WorkerLogicalContextAffinity.Current;
            Request=request;Images=images;Token=token;return Prepared=new EmptyPrepared(request);
        }
    }
    private sealed class EmptyPrepared(ModelGenerationRequest request):IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata { get; }=new("fixture","fixture.adapter","1",request.ModelId,100,32768,32,"fixture");
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? callback,CancellationToken token)=>throw new NotSupportedException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private static async Task PreparedAsync()
    {
        var slots=new LlamaSlotPool(5);var transport=new MemoryNative();using var http=new HttpClient(transport) { BaseAddress=new Uri("http://fixture.invalid/") };
        var source=new ActualPreparedAdapter(slots,http);var snapshot=Snapshot();
        var adapters=Enumerable.Range(0,5).Select(i=>WorkerLogicalContextAffinity.ForSubmission(source,Submission(snapshot,"conversation-"+i))).ToArray();
        var prepared=new IPreparedModelGeneration[5];
        try
        {
            for(var i=0;i<5;i++)prepared[i]=await adapters[i].PrepareAsync(Request("first-"+i),default);
            Check(slots.AvailableCount==0,"five actual prepared reasoning objects retain all five leases");
            foreach(var i in new[] {1,2,3,4,0})
            {
                var callbacks=0;var result=await prepared[i].GenerateAsync(_=>callbacks++,default);
                Check(result.Finish==ModelGenerationFinish.Stop&&result.GeneratedTokens==2&&result.EvaluatedTokens==100&&callbacks==1,
                    "actual compiled prepared reasoning adopts one terminal and known return lane"+i);
                await prepared[i].DisposeAsync();
            }
            Check(slots.AvailableCount==5&&source.Releases==5,"real prepared returns restore capacity once after reordered completion");
            for(var i=0;i<5;i++)prepared[i]=await adapters[i].PrepareAsync(Request("next-user-input-"+i),default);
            for(var i=0;i<5;i++)
            { await prepared[i].GenerateAsync(null,default);await prepared[i].DisposeAsync(); }
            Check(transport.GenerationSlots.Take(5).SequenceEqual(new[] {1,2,3,4,0}),"first generation order reaches actual named native slot field");
            Check(transport.GenerationSlots.Skip(5).SequenceEqual(Enumerable.Range(0,5)),"same logical contexts regain slots0..4 despite reordered return and changed input");
            Check(slots.AvailableCount==5&&source.Releases==10,"repeated actual prepared sequence releases all ten leases exactly once");
            Check(transport.Counts==10&&transport.Generations==10,"affinity adds no count/generation request or replay");
        }
        finally
        { foreach(var p in prepared)if(p is not null)await p.DisposeAsync();slots.Complete(); }

        var quarantine=new LlamaSlotPool(1);var released=0;var unknowns=0;
        LlamaSlotPool.Lease lease;using(WorkerLogicalContextAffinity.Enter(Key('a')))
            lease=await quarantine.AcquireAsync(()=>released++,default,_=>unknowns++);
        lease.Quarantine("synthetic-unknown");lease.Dispose();
        Check(quarantine.AvailableCount==0&&quarantine.Quarantined.Count==1&&released==0&&unknowns==1,"unknown quarantine clears preference without releasing consumed slot or accounting callback");
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Refuses<OperationCanceledException>(async()=>await quarantine.AcquireAsync(()=>released++,stop.Token),"quarantined capacity cannot be borrowed by a matching context");
        quarantine.Complete();
        var faulted=new LlamaSlotPool(1);var faultedReturns=0;
        var badLease=await faulted.AcquireAsync(()=>faultedReturns++,default,_=>throw new IOException("synthetic-quarantine-callback"));
        var waiter=faulted.AcquireAsync(()=>faultedReturns++,default);
        await Refuses<IOException>(()=>{badLease.Quarantine("synthetic-unknown");return Task.CompletedTask;},"quarantine callback failure remains visible");
        badLease.Dispose();
        await Refuses<IOException>(async()=>await waiter.WaitAsync(TimeSpan.FromSeconds(1)),"all quarantined slots close existing waiters conservatively");
        Check(faultedReturns==0&&faulted.AvailableCount==0&&faulted.Quarantined.Count==1,"failed quarantine callback cannot return capacity or lose unknown state");
        faulted.Complete();
    }
    private sealed class ActualPreparedAdapter(LlamaSlotPool pool,HttpClient http):IModelRuntimeAdapter
    {
        internal int Releases;
        public ModelAdapterDescriptor Descriptor=>LlamaPreparedReasoningGeneration.Adapter;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,CancellationToken token)
        {
            var slot=await pool.AcquireAsync(()=>Releases++,token,nativeCacheCompatible:false);
            return await LlamaPreparedReasoningGeneration.PrepareAsync(http,slot,"fixture-instance",request,[],()=>false,token);
        }
    }
    private sealed class MemoryNative:HttpMessageHandler
    {
        internal int Counts,Generations;internal List<int> GenerationSlots=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsolutePath=="/slots")
                return Json(Enumerable.Range(0,5).Select(id=>new {id,n_ctx=32768,is_processing=false}));
            using var input=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if(request.RequestUri.AbsolutePath=="/v1/chat/completions/input_tokens")
            {Counts++;return Json(new {object_="unused",input_tokens=100},"{\"object\":\"response.input_tokens\",\"input_tokens\":100}");}
            if(request.RequestUri.AbsolutePath!="/v1/chat/completions")throw new InvalidOperationException("unexpected-memory-route");
            Generations++;GenerationSlots.Add(input.RootElement.GetProperty("id_slot").GetInt32());
            var final=JsonSerializer.Serialize(new {id="fixture-completion",choices=new[] {new {index=0,delta=new {content="fixture"},finish_reason="stop"}}});
            var usage=JsonSerializer.Serialize(new {id="fixture-completion",choices=Array.Empty<object>(),usage=new {prompt_tokens=100,completion_tokens=2,total_tokens=102}});
            return new(HttpStatusCode.OK) {Content=new StringContent("data: "+final+"\n\ndata: "+usage+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
        private static HttpResponseMessage Json<T>(T value,string? literal=null)=>new(HttpStatusCode.OK)
            {Content=new StringContent(literal??JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    }
    private static async Task LeaseRaceAsync()
    {
        for(var i=0;i<24;i++)
        {
            var pool=new LlamaSlotPool(1);var callbacks=0;
            var lease=await pool.AcquireAsync(()=>Interlocked.Increment(ref callbacks),default);
            await Task.WhenAll(Task.Run(()=>lease.Quarantine("synthetic-race")),Task.Run(lease.Dispose));
            lease.Dispose();
            Check(pool.AvailableCount==1&&callbacks==1&&pool.Quarantined.Count==0||
                pool.AvailableCount==0&&callbacks==0&&pool.Quarantined.Count==1,"quarantine/dispose race preserves one known return or retained unknown "+i);
            pool.Complete();
        }
        var late=new LlamaSlotPool(1);var releases=0;
        var first=await late.AcquireAsync(()=>releases++,default);first.Dispose();
        var second=await late.AcquireAsync(()=>releases++,default);first.Quarantine("late-old-callback");
        Check(!second.IsQuarantined&&late.Quarantined.Count==0&&late.AvailableCount==0,"stale completed lease cannot poison a later borrower");
        second.Dispose();Check(releases==2&&late.AvailableCount==1,"later borrower retains its independent exact release");late.Complete();
    }
}
