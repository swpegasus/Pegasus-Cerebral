using System.Net;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real file pins/SQLite with deterministic native protocol fixtures; no installed model or process launches.</summary>
public static class ImageRoleReadinessTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-image-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Image readiness: " + message); checks++; }
        async Task<bool> Reject(Func<Task> action)
        {
            try { await action(); return false; }
            catch (Exception e) when (e is InvalidOperationException or IOException or NotSupportedException or OperationCanceledException or JsonException) { return true; }
        }
        try
        {
            var model = new ModelRecord { Id="vision", Name="Fixture", Path=Path.Combine(root,"weights.gguf"),
                VisionProjectorPath=Path.Combine(root,"projector.gguf"), ContextTokens=8192, MaximumContextTokens=8192,
                RecommendedContextTokens=8192, AutomaticContextSizing=false, ParallelRequests=1 };
            File.WriteAllBytes(model.Path, "GGUF"u8.ToArray());
            File.WriteAllBytes(model.VisionProjectorPath, "GGUF"u8.ToArray());
            var server = Path.Combine(root,"runtime.exe"); File.WriteAllText(server,"unexecuted fixture");
            var profile = new AgentProfile { Id="vision-role", Name="Image helper", ModelId=model.Id,
                RequiredModelCapabilities=[ModelCapabilityIds.ImageInput], AutomaticCompaction=false,
                ContextTokens=8192, ResponseTokens=512, FollowsModelBudgets=false, AllowedRoots=[root] };
            model.CapabilityStatus = [new() { Id=ModelCapabilityIds.ImageInput, Usable=true, RuntimeStatus="available" }];
            Check(!ModelCapabilityPolicy.Assess(model).Single(x=>x.Id==ModelCapabilityIds.ImageInput).Usable,
                "caller-supplied model status is never live proof");
            Check(await Reject(()=>{ ModelCapabilityPolicy.EnsureCompatible(profile,model); return Task.CompletedTask; }),
                "image assignment without native evidence fails");
            ModelCapabilityPolicy.EnsureConfigured(profile,model);
            Check(profile.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput),
                "configuration validation retains an idle image role");

            using var protocol = new Protocol();
            using var http = new HttpClient(protocol) { BaseAddress=new Uri("http://localhost/") };
            var pool = new LlamaSlotPool(1);
            Task<ModelImageVerification> Probe(CancellationToken token) =>
                NativeImageVerification.ProbeAsync(http,pool,model,"instance-one",()=>false,token);
            var result = await Probe(default);
            Check(result.InputTokens==112 && result.InstanceId=="instance-one" && protocol.Generated==1,
                "probe uses native preparation and a confirmed exact-count image generation");
            Check(protocol.ImageSent && pool.AvailableCount==1, "real bounded image bytes reach protocol and slot is released");
            var imageAccounting = new ModelExecutionAccounting(coverageComplete: true);
            protocol.OnCompletion = () =>
            {
                var row = imageAccounting.Observation.Snapshot().Rows.Single(value => value.Kind == "ImageVerification" && value.Release == "unobserved");
                Check(row.InstanceId == "instance-one" && row.LeaseId is { Length: 32 } && row.NativeSlotId == 0,
                    "vision check publishes its actual retained physical lease and native slot");
            };
            _ = await NativeImageVerification.ProbeAsync(http, pool, model, "instance-one", () => false, default, imageAccounting);
            protocol.OnCompletion = null;
            Check(imageAccounting.Snapshot().Entries.Length == 0 && pool.AvailableCount == 1,
                "completed vision check retires its accounting row and returns the shared slot");

            foreach (var bad in new[] { "length", "count", "wrong", "truncated", "malformed" })
            {
                protocol.Mode=bad;
                Check(await Reject(async()=>{ await Probe(default); }), "reject incomplete or incorrect probe: "+bad);
                Check(pool.AvailableCount==1, "failed probe releases slot: "+bad);
            }
            protocol.Mode="good";
            if (OperatingSystem.IsWindows())
            {
                using (var pins = new NativeImageVerification(model,server))
                {
                    Check(pins.Matches(model,server), "same Windows file identities match");
                    var resized=CerebralJson.Read<ModelRecord>(CerebralJson.Element(model)); resized.ContextTokens=4096;
                    Check(pins.Matches(resized,server), "automatic context sizing does not invalidate component identity");
                    resized.ChatTemplate="changed";
                    Check(!pins.Matches(resized,server), "changed template invalidates live binding");
                    Check(await Reject(()=>{ File.WriteAllText(model.Path,"changed"); return Task.CompletedTask; }),
                        "live weights cannot be overwritten");
                    Check(await Reject(()=>{ File.Delete(model.VisionProjectorPath); return Task.CompletedTask; }),
                        "live projector cannot be replaced");
                    var before=protocol.Generated;
                    var first=await pins.EnsureAsync(Probe,()=>pins.Matches(model,server),default);
                    var second=await pins.EnsureAsync(Probe,()=>true,default);
                    Check(first==second && protocol.Generated==before+1, "successful live instance verifies only once");
                }
                File.WriteAllBytes(model.VisionProjectorPath,"GGUF"u8.ToArray());
                Check(File.Exists(model.VisionProjectorPath), "unload/dispose releases component pin");
                using (var fresh = new NativeImageVerification(model,server))
                {
                    var before=protocol.Generated;
                    await fresh.EnsureAsync(Probe,()=>true,default);
                    Check(protocol.Generated==before+1, "a fresh instance cannot inherit previous proof");
                }
                using (var cancelled = new NativeImageVerification(model,server))
                {
                    using var cts=new CancellationTokenSource(); cts.Cancel();
                    var before=protocol.Generated;
                    Check(await Reject(async()=>{ await cancelled.EnsureAsync(Probe,()=>true,cts.Token); }) && protocol.Generated==before,
                        "caller cancellation performs no probe");
                    await cancelled.EnsureAsync(Probe,()=>true,default);
                    Check(cancelled.Current is not null, "caller cancellation does not poison later verification");
                }
                using (var timedOut = new NativeImageVerification(model,server))
                {
                    var attempts=0;
                    Task<ModelImageVerification> Timeout(CancellationToken _) { attempts++; throw new OperationCanceledException(); }
                    Check(await Reject(async()=>{ await timedOut.EnsureAsync(Timeout,()=>true,default); }),
                        "internal timeout becomes actionable readiness failure");
                    Check(await Reject(async()=>{ await timedOut.EnsureAsync(Timeout,()=>true,default); }) && attempts==1,
                        "failed automatic probe cannot loop on one instance");
                }
                using (var concurrent = new NativeImageVerification(model,server))
                {
                    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var attempts=0;
                    async Task<ModelImageVerification> Slow(CancellationToken token)
                    { attempts++; entered.TrySetResult(); await release.Task.WaitAsync(token); return result; }
                    var one=concurrent.EnsureAsync(Slow,()=>true,default);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var two=concurrent.EnsureAsync(Slow,()=>true,default);
                    release.TrySetResult(); await Task.WhenAll(one,two);
                    Check(attempts==1, "concurrent readiness requests share a completed probe");
                }
                using (var retired = new NativeImageVerification(model,server))
                {
                    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var pending=retired.EnsureAsync(async token =>
                    { entered.TrySetResult(); await release.Task.WaitAsync(token); return result; },()=>true,default);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); retired.Dispose(); release.TrySetResult();
                    Check(await Reject(async()=>{ await pending; }) && retired.Current is null,
                        "completion after unload cannot publish usable proof");
                }
            }

            var state=new StateStore(Path.Combine(root,"state"));
            var configuration=new Configuration(state);
            state.Put("model",model.Id,model); state.Put("agent",profile.Id,profile);
            Check(ModelProfileBudgets.Resolve(state,model,4096).Single().RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput),
                "load fitting can update budgets of an image-required profile");
            var events=new EventHub();
            await using var runtime=new ModelRuntime(state,configuration.Get,events);
            await using var actualTools=new ToolDispatcher(configuration.Get);
            var executor=new NoExecutor();
            using var scheduler=new JobScheduler(state,configuration,executor,runtime,events,NullLogger<JobScheduler>.Instance);
            var commands=new CommandDispatcher(state,configuration,runtime,scheduler,actualTools,events);
            profile.Name="Renamed while idle";
            var saved=await commands.ExecuteAsync(new("agents.save",CerebralJson.Element(profile)));
            Check(saved.Success && !runtime.IsLoaded(model.Id) && state.Get<AgentProfile>("agent",profile.Id)!.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput),
                "an existing idle image role stays editable without hidden inference");
            var noComponent=CerebralJson.Read<ModelRecord>(CerebralJson.Element(model)); noComponent.VisionProjectorPath=null;
            state.Put("model",model.Id,noComponent);
            var newProfile=CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); newProfile.Id="new-role"; newProfile.Revision=0;
            var denied=await commands.ExecuteAsync(new("agents.save",CerebralJson.Element(newProfile)));
            Check(!denied.Success && state.Get<AgentProfile>("agent",newProfile.Id) is null && !runtime.IsLoaded(model.Id),
                "a new image role cannot use forged metadata or absent components");
            var queued=await commands.ExecuteAsync(new("jobs.submit",CerebralJson.Element(new
                { requestId="image-gate-job", profileId=profile.Id, prompt="Inspect the accepted image." })));
            Check(queued.Success && state.Get<AgentJob>("job","image-gate-job") is { Status:"queued" } &&
                !runtime.IsLoaded(model.Id), "existing image role can queue while unloaded; readiness belongs to execution");
            try
            {
                await scheduler.StartAsync(default);
                using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while(state.Get<AgentJob>("job","image-gate-job") is { Status:"queued" or "running" })
                    await Task.Delay(25,wait.Token);
                var finished=state.Get<AgentJob>("job","image-gate-job")!;
                Check(finished.Status=="failed" && finished.Error?.Contains("Configure the matching image projector",StringComparison.Ordinal)==true &&
                    executor.Calls==0 && !runtime.IsLoaded(model.Id),
                    "real scheduler rejects missing image readiness before invoking the executor or inference");
            }
            finally
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await scheduler.StopAsync(stop.Token);
            }
            state.Put("model",model.Id,model);
            profile=state.Get<AgentProfile>("agent",profile.Id)!;

            var fakeModels=new Models();
            var fakeTools=new Tools();
            var snapshot=await WorkerExecutionSnapshot.CaptureAsync(state,profile,configuration,fakeTools,[],default);
            var submission=new WorkflowSubmission("request","chat",profile.Id,"fixture:chat","1.0.0","exact text",null,
                WorkflowContract.EmptyObject,snapshot);
            var calls=0; var held=false; var disposed=0;
            Task<IDisposable?> Ready(WorkflowSubmission accepted,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Check(accepted.ProfileId==profile.Id && accepted.ConversationId=="chat", "readiness scope is accepted host identity");
                calls++; held=true; return Task.FromResult<IDisposable?>(new Lease(()=>{held=false;disposed++;}));
            }
            fakeModels.BeforePrepare=()=>Check(held,"readiness lease spans preparation");
            fakeModels.BeforeGenerate=()=>Check(held,"readiness lease spans generation");
            var host=new WorkerWorkflowEffects(state,configuration,fakeModels,fakeTools,requiredCapabilityAdmission:Ready);
            var handler=host.Commands.Single(x=>x.Descriptor.Id==WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            WorkflowCommandContext Context(object? args=null)=>new("effect",submission,
                CerebralJson.Element(args??new WorkerWorkflowEffects.GenerateArguments([],ToolNames:[])));
            var generated=await handler.ExecuteAsync(Context(),default);
            Check(generated.State==WorkflowEffectState.Succeeded && calls==1 && disposed==1 && !held,
                "verified callback lease is released after complete task generation");
            var missing=new WorkerWorkflowEffects(state,configuration,fakeModels,fakeTools).Commands.Single(x=>x.Descriptor.Id==WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            Check((await missing.ExecuteAsync(Context(),default)).State==WorkflowEffectState.Failed,
                "image-required execution without host verification fails closed");
            var changed=CerebralJson.Read<ModelRecord>(CerebralJson.Element(model)); changed.ChatTemplate="changed"; state.Put("model",model.Id,changed);
            Check((await handler.ExecuteAsync(Context(),default)).State==WorkflowEffectState.Failed && calls==1,
                "stale accepted model is rejected before verification can load");
            state.Put("model",model.Id,model);
            using(var cts=new CancellationTokenSource())
            {
                cts.Cancel();
                Check((await handler.ExecuteAsync(Context(),cts.Token)).State==WorkflowEffectState.Cancelled && calls==1,
                    "cancelled workflow never starts readiness");
            }
            fakeModels.FailPrepare=true;
            Check((await handler.ExecuteAsync(Context(),default)).State==WorkflowEffectState.Failed && disposed==2 && !held,
                "preparation failure releases readiness lease");
            Check(!runtime.AssessCapabilities(model).Single(x=>x.Id==ModelCapabilityIds.ImageInput).Usable,
                "synthetic protocol evidence cannot populate a real runtime instance");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
        Console.WriteLine($"Image role readiness: {checks} deterministic protocol/file/SQLite checks passed; no installed model inference.");
    }

    private sealed class Lease(Action release):IDisposable { public void Dispose()=>release(); }
    private sealed class Protocol:HttpMessageHandler
    {
        public string Mode="good";
        public int Generated;
        public bool ImageSent;
        public Action? OnCompletion;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            string body; var type="application/json";
            switch(request.RequestUri!.AbsolutePath)
            {
                case "/slots": body="""[{"id":0,"n_ctx":8192,"is_processing":false}]"""; break;
                case "/v1/models": body="""{"models":[{"capabilities":["multimodal"]}]}"""; break;
                case "/v1/chat/completions/input_tokens": body="""{"object":"response.input_tokens","input_tokens":112}"""; break;
                case "/apply-template": body="""{"prompt":"exact probe input"}"""; break;
                case "/completion":
                    Generated++;
                    using(var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                        ImageSent=json.RootElement.GetProperty("prompt").GetProperty("multimodal_data")[0].GetString()!.Length>100;
                    OnCompletion?.Invoke();
                    var answer=Mode=="malformed" ? "not-json" : Mode=="wrong" ? """{"left":"blue","right":"red"}""" : """{"left":"red","right":"blue"}""";
                    body="data: "+JsonSerializer.Serialize(new { content=answer,id_slot=0,stop=true,stop_type=Mode=="length"?"limit":"eos",
                        tokens_predicted=12,tokens_evaluated=Mode=="count"?113:112,truncated=Mode=="truncated" })+"\n\n";
                    type="text/event-stream"; break;
                default: throw new Exception("Unexpected probe route");
            }
            return new(HttpStatusCode.OK) { Content=new StringContent(body,Encoding.UTF8,type) };
        }
    }
    private sealed class NoExecutor:IAgentExecutor
    {
        public Func<SubmitJobRequest,AgentJob>? SubmitHelper {get;set;}
        public Func<bool>? ForegroundPending {get;set;}
        public int Calls;
        public Task RunAsync(AgentJob job,CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new Exception("No agent execution in fixture."); }
    }
    private sealed class Tools:IToolDispatcher
    {
        public bool FullAccessActive=>false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile,CancellationToken token)=>Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call,ToolExecutionContext context,CancellationToken token)=>throw new Exception("No tool execution.");
    }
    private sealed class Models:IModelRuntimeAdapter
    {
        public Action? BeforePrepare,BeforeGenerate;
        public bool FailPrepare;
        public ModelAdapterDescriptor Descriptor {get;}=new("fixture","1.0.0",["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BeforePrepare?.Invoke();
            if(FailPrepare) throw new InvalidOperationException("fixture preparation failed");
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this,request));
        }
        private sealed class Prepared(Models owner,ModelGenerationRequest request):IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata {get;}=new("fixture","fixture","1.0.0",request.ModelId,42,request.ContextLimit,request.ResponseLimit,"fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText,CancellationToken token)
            { owner.BeforeGenerate?.Invoke(); return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop,"reply",2,42)); }
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
}
