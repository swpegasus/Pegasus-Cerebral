using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static class ToolGenerationEffectsTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Generation plan effects: " + message); checks++; Console.WriteLine("PASS: " + message); }
        using var fixture = new Fixture();
        var plan = new ToolGenerationRequest("Produce one complete café payload.", "fixture.sink",
            CerebralJson.Element(new { destination = "approved-fixture", operationId = "exact-operation" }),
            "text", MaxTokens: 768, CompleteAssignment: false, MaxAttempts: 3);
        var accepted = new ToolResult(true, "The provider prepared this exact generation plan.", Generation: plan)
            { Evidence = new(ToolExecutionOutcome.Succeeded, "fixture.provider", RequestId: "provider-plan-1") };
        fixture.Tools.Result = accepted;
        var (run, result) = await fixture.ExecuteAsync();
        var data = Data(result);
        WorkflowSchema.Validate(result.Data!.Value, fixture.Handler.Descriptor.OutputSchema);
        Check(result.State == WorkflowEffectState.Succeeded && result.Error is null && data.FollowUpRequired &&
            data.Generation is { } retained && retained.Prompt == plan.Prompt && retained.SinkToolName == plan.SinkToolName &&
            retained.ContentProperty == "text" && retained.MaxAttempts == 3 && !retained.CompleteAssignment &&
            JsonElement.DeepEquals(retained.SinkArguments, plan.SinkArguments),
            "complete successful provider plan survives the durable host result exactly without a legacy failure");
        var committed = fixture.Workflows.CompleteEffect(run.Id, run.Effect!.Id, result, 1);
        var reopened = new WorkflowStore(new EventJournal(Path.Combine(fixture.Store.DirectoryPath, "engine.db"))).Get(run.Id);
        Check(committed.Effect?.Result is { } && reopened.Effect?.Result is { } saved &&
            saved.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options)!.Generation?.Prompt == plan.Prompt &&
            fixture.Tools.Executions == 1 && fixture.Tools.SinkExecutions == 0 && fixture.Models.Preparations == 0,
            "actual SQLite result/reopen retains the plan while the effect executes neither a model nor its sink");
        var older = CerebralJson.Element(new { name = "fixture.plan", content = "older result", truncated = false,
            evidence = (object?)null, followUpRequired = false });
        Check(older.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options)?.Generation is null,
            "older ordinary durable tool results remain compatible without a synthesized generation plan");

        async Task Reject(ToolResult value, string expected, string message)
        {
            fixture.Reset(); fixture.Tools.Result = value;
            var (_, rejected) = await fixture.ExecuteAsync();
            Check(rejected.State == WorkflowEffectState.Failed && Data(rejected) is { Generation: null, FollowUpRequired: false } &&
                rejected.Error?.Contains(expected, StringComparison.Ordinal) == true && fixture.Tools.SinkExecutions == 0,
                message);
            if (rejected.Data is { } body) WorkflowSchema.Validate(body, fixture.Handler.Descriptor.OutputSchema);
        }
        await Reject(accepted with { Truncated = true }, "complete successful",
            "truncated provider result cannot authorize executable generation follow-up");
        await Reject(accepted with { Evidence = accepted.Evidence! with { Partial = true } }, "complete successful",
            "provider-authenticated partial evidence cannot authorize executable generation follow-up");
        await Reject(accepted with { Success = false }, "complete successful",
            "inconsistent provider success/evidence cannot authorize a generation plan");
        await Reject(accepted with { Success = false, Evidence = new(ToolExecutionOutcome.Failed, "fixture.provider") }, "complete successful",
            "failed originating tool retains actionable failure instead of dispatching a follow-up");
        await Reject(accepted with { Generation = plan with { Prompt = "invalid\0prompt" } }, "without null",
            "malformed generation guidance is rejected before a durable plan is retained");
        await Reject(accepted with { Generation = plan with { SinkArguments = CerebralJson.Element(new { text = "prepopulated" }) } }, "pre-populate",
            "generation plan cannot smuggle already populated payload into its sink arguments");
        await Reject(accepted with { Generation = plan with { Prompt = new string('p', 128 * 1024 + 1) } }, "1-128",
            "provider generation guidance keeps its shared Core byte bound");
        await Reject(accepted with { Content = new string('r', 100_000), Generation = plan with { Prompt = new string('p', 40_000) } }, "durable workflow limit",
            "individually valid plan and result that exceed the whole receipt bound are dropped without truncating executable data");
        await Reject(accepted with { Content = new string('r', 300_000) }, "durable workflow limit",
            "oversized originating result cannot be trimmed into an accepted executable generation plan");

        fixture.Reset(); fixture.Tools.Result = accepted; fixture.Tools.HideSink = true;
        var unavailable = await fixture.ExecuteAsync();
        Check(unavailable.Result.State == WorkflowEffectState.Failed && Data(unavailable.Result).Generation is null &&
            unavailable.Result.Error!.Contains("unavailable", StringComparison.Ordinal),
            "unavailable current sink descriptor prevents plan admission without executing the sink");
        fixture.Reset(); fixture.Tools.Result = accepted; fixture.Tools.ChangeSink = true;
        var changed = await fixture.ExecuteAsync();
        Check(changed.Result.State == WorkflowEffectState.Failed && Data(changed.Result).Generation is null &&
            changed.Result.Error!.Contains("descriptor changed", StringComparison.Ordinal),
            "a changed current sink descriptor cannot replace its frozen accepted contract");
        fixture.Reset(); fixture.Tools.Result = accepted with { Generation = plan with { SinkToolName = "fixture.new-sink" } };
        fixture.Tools.IncludeNewSink = true;
        var newSink = await fixture.ExecuteAsync();
        Check(newSink.Result.State == WorkflowEffectState.Failed && Data(newSink.Result).Generation is null &&
            newSink.Result.Error!.Contains("accepted/current", StringComparison.Ordinal),
            "newly available sinks cannot widen the originating workflow's accepted catalog");
        fixture.Reset(); fixture.Tools.Result = accepted;
        fixture.UpdateProfile(profile => profile.AllowedTools = ["fixture.plan"]);
        var revokedTool = await fixture.ExecuteAsync();
        Check(revokedTool.Result.State == WorkflowEffectState.Failed && Data(revokedTool.Result).Generation is null,
            "revoked current sink tool grant prevents executable plan retention");
        fixture.Reset(); fixture.Tools.Result = accepted;
        fixture.UpdateProfile(profile => profile.Capabilities.Remove("sink.write"));
        var revokedCapability = await fixture.ExecuteAsync();
        Check(revokedCapability.Result.State == WorkflowEffectState.Failed && Data(revokedCapability.Result).Generation is null,
            "revoked current sink capability prevents executable plan retention");
        fixture.Reset(); fixture.Tools.Result = accepted;
        fixture.Tools.OnPlanList = () => fixture.UpdateProfile(profile => profile.Capabilities.Remove("sink.write"));
        var overlapped = await fixture.ExecuteAsync();
        Check(overlapped.Result.State == WorkflowEffectState.Failed && Data(overlapped.Result).Generation is null &&
            overlapped.Result.Error!.Contains("revoked during", StringComparison.Ordinal),
            "sink revocation during asynchronous catalog admission is rechecked after discovery");
        fixture.Reset(); fixture.Tools.Result = accepted;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var beforeExecution = await fixture.ExecuteAsync(cancelled.Token);
        Check(beforeExecution.Result.State == WorkflowEffectState.Cancelled && fixture.Tools.Executions == 0,
            "pre-cancelled generation-plan effect never calls the originating provider");
        fixture.Reset(); fixture.Tools.Result = accepted;
        using var duringAdmission = new CancellationTokenSource();
        fixture.Tools.OnPlanList = duringAdmission.Cancel;
        var cancelledPlan = await fixture.ExecuteAsync(duringAdmission.Token);
        Check(cancelledPlan.Result.State == WorkflowEffectState.Failed && Data(cancelledPlan.Result).Generation is null &&
            cancelledPlan.Result.Error!.Contains("cancelled after the provider returned", StringComparison.Ordinal) &&
            fixture.Tools.Executions == 1 && fixture.Tools.SinkExecutions == 0 && fixture.Models.Preparations == 0,
            "cancellation after confirmed provider completion discards unexecuted plan while retaining the actual provider receipt");
        Console.WriteLine($"Tool generation plans: {checks} isolated SQLite/deterministic dispatcher checks passed; no native inference or sink execution.");
        return checks;
    }

    private static WorkerWorkflowEffects.ToolData Data(WorkflowCommandResult result) =>
        result.Data?.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options) ?? throw new InvalidOperationException("Expected bounded tool data.");

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Cerebral.ToolGenerationPlans-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public AgentProfile Profile { get; }
        public Tools Tools { get; } = new();
        public Models Models { get; } = new();
        public EventJournal Journal { get; }
        public WorkflowStore Workflows { get; }
        public IWorkflowCommandHandler Handler { get; }
        private readonly JsonElement snapshot;
        public Fixture()
        {
            Directory.CreateDirectory(root); Store = new(Path.Combine(root, "data")); Configuration = new(Store); _ = Configuration.Get();
            Profile = new() { Id = "generation-plan-lead", Role = "lead", ModelId = "fixture-model", FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 768, AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["plan.read", "sink.write"] };
            Store.Put("agent", Profile.Id, Profile);
            Store.Put("model", Profile.ModelId, new ModelRecord { Id = Profile.ModelId, Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192 });
            snapshot = WorkerExecutionSnapshot.CaptureAsync(Store, Profile, Configuration, Tools,
                [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default).GetAwaiter().GetResult();
            Journal = new(Path.Combine(Store.DirectoryPath, "engine.db")); Workflows = new(Journal);
            Handler = new WorkerWorkflowEffects(Store, Configuration, Models, Tools).Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        }
        public void Reset()
        { Store.Put("agent", Profile.Id, Profile); Tools.Reset(); }
        public void UpdateProfile(Action<AgentProfile> update)
        { var profile = Store.Get<AgentProfile>("agent", Profile.Id)!; update(profile); Store.Put("agent", profile.Id, profile); }
        public async Task<(WorkflowRun Run, WorkflowCommandResult Result)> ExecuteAsync(CancellationToken token = default)
        {
            var id = Guid.NewGuid().ToString("N");
            var run = Workflows.Submit(new(id, id + "-conversation", Profile.Id, "fixture:plan", "1.0.0", "Complete the accepted request.", null,
                WorkflowContract.EmptyObject, snapshot));
            run = Workflows.TryClaim(run.Id)!;
            var arguments = CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments("fixture.plan", WorkflowContract.EmptyObject));
            run = Workflows.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1, arguments)), Handler.Descriptor);
            run = Workflows.DispatchEffect(run.Id, run.Revision);
            var result = await Handler.ExecuteAsync(new(run.Effect!.Id, run.Submission, arguments) { RunId = run.Id }, token);
            return (run, result);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary fixture.");
            Directory.Delete(root, true);
        }
    }
    private sealed class Tools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public ToolResult Result { get; set; } = new(true, "ordinary");
        public int Executions, SinkExecutions;
        public bool HideSink, ChangeSink, IncludeNewSink;
        public Action? OnPlanList;
        public void Reset() { Executions = SinkExecutions = 0; HideSink = ChangeSink = IncludeNewSink = false; OnPlanList = null; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (Executions > 0) OnPlanList?.Invoke(); token.ThrowIfCancellationRequested();
            var values = new List<ToolDescriptor> { new("fixture.plan", "Produce a validated generation plan.", CerebralJson.Element(new { type = "object" }), "plan.read") };
            if (!HideSink) values.Add(new("fixture.sink", ChangeSink ? "Changed sink contract." : "Consume a generated payload.",
                CerebralJson.Element(new { type = "object", properties = new { text = new { type = "string" } } }), "sink.write"));
            if (IncludeNewSink) values.Add(new("fixture.new-sink", "New sink after admission.", CerebralJson.Element(new { type = "object" }), "sink.write"));
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>(values);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Executions++;
            if (call.Name != "fixture.plan") { SinkExecutions++; throw new InvalidOperationException("This fixture must not execute a sink."); }
            return Task.FromResult(Result);
        }
    }
    private sealed class Models : IModelRuntimeAdapter
    {
        public int Preparations;
        public ModelAdapterDescriptor Descriptor { get; } = new("no-inference-fixture", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; throw new InvalidOperationException("Plan admission must never prepare or execute a model."); }
    }
}
