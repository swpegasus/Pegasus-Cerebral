using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Native harness, actual collectible engine strategy, SQLite and scheduler; model/token counts are deterministic doubles.</summary>
internal static class NativeCoordinationTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-native-coordination-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            var state = new StateStore(root); var config = new Configuration(state); var events = new EventHub();
            var settings = config.Get(); settings.QueueCapacity = 20; settings.MaxConcurrentJobs = 1; config.Save(settings);
            state.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192 });
            foreach (var (id, role, instructions) in new[] { ("lead", "lead", "ROLE_LEAD"), ("alpha", "helper", "ROLE_ALPHA"), ("beta", "helper", "ROLE_BETA") })
                state.Put("agent", id, new AgentProfile { Id = id, Name = id, Purpose = id + " isolated test role", Role = role,
                    Instructions = instructions, Revision = 1, ModelId = "fixture", FollowsDefaultModel = false, FollowsModelBudgets = false,
                    AutomaticCompaction = false, ContextTokens = 8192, ResponseTokens = 1024, MaxSteps = 24, MaxSeconds = 60,
                    AllowedRoots = [root], Capabilities = ["agents.delegate"], AllowedTools = [] });
            var engineRoot = Path.Combine(root, "Engines"); EngineHostTests.Pack(engineRoot, "A", true);
            await using var engine = new EngineController(engineRoot, Path.Combine(root, "EngineState"));
            await engine.InitializeAsync();
            Check(engine.SupportsWork("cerebral.agents.plan", 1), "Actual engine declares the bounded assignment-plan contract.");
            using (var rejected = await engine.EvaluateAsync("policy-check", 1, "cerebral.agents.plan", 1,
                JsonSerializer.SerializeToElement(new { assignments = Enumerable.Range(0, 9).Select(index => new { profileId = "helper-" + index }).ToArray() }),
                _ => throw new InvalidOperationException("No feature fallback."), default))
                Check(rejected.Decision.Payload.TryGetProperty("rejectedReason", out _) && engine.Status().Error is null,
                    "An oversized plan is an ordinary rejection and leaves the engine healthy.");
            var adapter = new Models(() => state.Get<AgentProfile>("agent", "alpha")!.Revision,
                () => state.Get<AgentProfile>("agent", "beta")!.Revision); var tools = new NoTools(); WorkerWorkflows? host = null;
            var effects = new WorkerWorkflowEffects(state, config, adapter, tools, workflowHost: () => host!);
            host = new WorkerWorkflows(state, config, events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], tools,
                engine: engine);
            var parent = await host.SubmitAsync(new("native-first-mate", "native-first-mate-context", "lead",
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "PARENT_ONLY_SECRET. Have alpha and beta independently report evidence; combine their results.", null, WorkflowContract.EmptyObject), null, default);
            await using var unusedRuntime = new ModelRuntime(state, config.Get, events);
            using var scheduler = new JobScheduler(state, config, new UnusedExecutor(), unusedRuntime, events,
                NullLogger<JobScheduler>.Instance, host);
            await scheduler.StartAsync(default);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!host.Store.Get(parent.Id).IsTerminal)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException("Native coordination did not settle: " +
                    string.Join("; ", host.Store.ChildRunIds(parent.Id).Prepend(parent.Id).Select(id =>
                    { var run = host.Store.Get(id); return id + " " + run.Status + " " + run.WaitingReason + " " + run.Error; })));
                await Task.Delay(25);
            }
            await scheduler.StopAsync(default);
            var final = host.Store.Get(parent.Id);
            Check(final.Status == WorkflowStatus.Completed, "Native chat parent completes through engine-managed delegation. " +
                final.Status + " " + final.WaitingReason + " " + final.Error + " Latest input: " + adapter.Inputs.LastOrDefault());
            var children = host.Store.ChildRunIds(parent.Id).Select(host.Store.Get).ToArray();
            Check(children.Length == 2 && children.All(child => child.Status == WorkflowStatus.Completed),
                "Two isolated helper contexts complete. " + string.Join("; ", adapter.Inputs.TakeLast(2)) + " Children: " +
                string.Join("; ", children.Select(child => child.Id + " " + child.Status + " " + child.Error)));
            Check(children.Select(child => child.Submission.ConversationId).Append(parent.Submission.ConversationId).Distinct().Count() == 3,
                "Root and both roles use distinct durable context windows.");
            Check(children.All(child => !WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).Capabilities.Contains("agents.delegate")),
                "Child snapshots cannot recursively delegate.");
            Check(adapter.LeadCalls == 2 && adapter.ChildCalls == 2, "Parent makes one planning and one synthesis generation with no polling model calls.");
            Check(!adapter.ChildSawParentSecret && !adapter.ChildHadDelegateGrammar, "Child model input excludes parent transcript and recursive delegation grammar.");
            Check(adapter.ParentSawBothResults, "Parent synthesis receives both attributed outcomes.");
            Check(engine.Status().CommittedDecisions > 8, "Actual replaceable engine commits strategy decisions through the host fence.");
            Console.WriteLine($"Native coordination checks passed: {checks} (actual engine/SQLite/scheduler, deterministic model; no GB10/Laya inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-native-coordination-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup target.");
            Directory.Delete(root, true);
        }
    }
    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) => throw new InvalidOperationException("No tools expected.");
    }
    private sealed class UnusedExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("No legacy model path expected.");
    }
    private sealed class Models(Func<int> alphaRevision, Func<int> betaRevision) : IModelRuntimeAdapter
    {
        private readonly Func<int> AlphaRevision = alphaRevision, BetaRevision = betaRevision;
        public ModelAdapterDescriptor Descriptor { get; } = new("coordination-fixture", "1.0.0", ["text"]);
        public int LeadCalls, ChildCalls;
        public List<string> Inputs { get; } = [];
        public bool ChildSawParentSecret, ChildHadDelegateGrammar, ParentSawBothResults;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            Task.FromResult<IPreparedModelGeneration>(new Prepared(this, request));
        private sealed class Prepared(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture", "coordination-fixture", "1.0.0", request.ModelId,
                42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var text = string.Join('\n', request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));
                owner.Inputs.Add(text[^Math.Min(1800, text.Length)..]);
                string output;
                if (text.Contains("ROLE_LEAD", StringComparison.Ordinal))
                {
                    owner.LeadCalls++;
                    if (owner.LeadCalls == 1) output = JsonSerializer.Serialize(new
                    {
                        action = "delegate", assignments = new[]
                        {
                            new { profileId = "alpha", profileRevision = owner.AlphaRevision(), instruction = "Return ALPHA_EVIDENCE for the independent alpha assignment." },
                            new { profileId = "beta", profileRevision = owner.BetaRevision(), instruction = "Return BETA_EVIDENCE for the independent beta assignment." }
                        }
                    });
                    else
                    {
                        owner.ParentSawBothResults = text.Contains("ALPHA_EVIDENCE", StringComparison.Ordinal) && text.Contains("BETA_EVIDENCE", StringComparison.Ordinal);
                        output = """{"action":"final","text":"Combined attributed helper evidence."}""";
                    }
                }
                else
                {
                    owner.ChildCalls++;
                    owner.ChildSawParentSecret |= text.Contains("PARENT_ONLY_SECRET", StringComparison.Ordinal);
                    owner.ChildHadDelegateGrammar |= request.ResponseSchema?.GetRawText().Contains("\"delegate\"", StringComparison.Ordinal) == true;
                    output = text.Contains("ROLE_ALPHA", StringComparison.Ordinal) ? """{"action":"final","text":"ALPHA_EVIDENCE"}""" : """{"action":"final","text":"BETA_EVIDENCE"}""";
                }
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, output, 24, 42));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
