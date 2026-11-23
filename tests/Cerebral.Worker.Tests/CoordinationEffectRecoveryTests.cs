using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual native child admission and durable restart/reconciliation; no model, tool or network execution.</summary>
internal static class CoordinationEffectRecoveryTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-coordination-recovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            var state = new StateStore(root); var config = new Configuration(state); var events = new EventHub();
            var settings = config.Get(); settings.QueueCapacity = 2; settings.MaxConcurrentJobs = 1; config.Save(settings);
            state.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192 });
            foreach (var (id, role) in new[] { ("lead", "lead"), ("alpha", "helper"), ("beta", "helper") })
                state.Put("agent", id, new AgentProfile { Id = id, Name = id, Purpose = "Recovery fixture", Role = role,
                    Instructions = "Return only bounded fixture evidence.", Revision = 1, ModelId = "fixture",
                    FollowsDefaultModel = false, FollowsModelBudgets = false, AutomaticCompaction = false,
                    ContextTokens = 8192, ResponseTokens = 1024, MaxSteps = 24, MaxSeconds = 60,
                    AllowedRoots = [root], Capabilities = ["agents.delegate"], AllowedTools = [] });
            var engineRoot = Path.Combine(root, "Engines"); var engineState = Path.Combine(root, "EngineState");
            EngineHostTests.Pack(engineRoot, "A", true);
            var models = new NeverModels(); var tools = new NoTools();
            string parentId = "partial-native-delegation", effectId, existingChildId, missingChildId, savedPlan;
            WorkflowRun admittedChild;

            await using (var engine = new EngineController(engineRoot, engineState))
            {
                await engine.InitializeAsync();
                WorkerWorkflows? host = null;
                var effects = new WorkerWorkflowEffects(state, config, models, tools, workflowHost: () => host!);
                host = Host(state, config, events, effects, tools, engine);
                var parent = await host.SubmitAsync(new(parentId, parentId + "-context", "lead",
                    ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                    "Have alpha and beta return independent evidence.", null, WorkflowContract.EmptyObject), null, default);
                var handler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.DelegateCommand);
                var args = CerebralJson.Element(new { assignments = new[]
                {
                    new { profileId = "alpha", profileRevision = state.Get<AgentProfile>("agent", "alpha")!.Revision,
                        instruction = "Return ALPHA_RECOVERY_EVIDENCE." },
                    new { profileId = "beta", profileRevision = state.Get<AgentProfile>("agent", "beta")!.Revision,
                        instruction = "Return BETA_RECOVERY_EVIDENCE." }
                } });
                var claimed = host.Store.TryClaim(parent.Id)!;
                var prepared = host.Store.ApplyStep(parent.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(handler.Descriptor.Id, handler.Descriptor.Version, args)),
                    handler.Descriptor);
                var dispatched = host.Store.DispatchEffect(parent.Id, prepared.Revision);
                effectId = dispatched.Effect!.Id;
                existingChildId = "child-" + effectId + "-0"; missingChildId = "child-" + effectId + "-1";
                var result = await handler.ExecuteAsync(new(effectId, dispatched.Submission, args) { RunId = parent.Id }, default);
                Check(result.State == WorkflowEffectState.Unknown && result.Error?.Contains("Queue is full", StringComparison.Ordinal) == true,
                    "Real queue capacity admits one child then leaves the second admission explicitly uncertain.");
                admittedChild = host.Store.Get(existingChildId);
                Check(host.Store.ChildRunIds(parent.Id).SequenceEqual(new[] { existingChildId }) &&
                    host.Store.TryGet(missingChildId) is null && admittedChild.Submission.ProfileId == "alpha" &&
                    admittedChild.Submission.ParentRunId == parent.Id,
                    "Only the first exact child identity is durable; no second context was admitted.");
                savedPlan = state.Get<JsonElement>("coordination-plan", effectId).GetRawText();
                Check(savedPlan.Contains("alpha", StringComparison.Ordinal) && savedPlan.Contains("beta", StringComparison.Ordinal) &&
                    result.ProviderReceiptId == effectId,
                    "The two-assignment plan and original effect receipt remain available for recovery.");
                Check(host.Store.Get(parent.Id).Effect?.State == WorkflowEffectState.Dispatched,
                    "Fixture loses the returned outcome before receipt commit, preserving the genuine crash boundary.");
            }

            // Reopen both durable stores and the engine. Capacity is now sufficient: an erroneous
            // replay would actually admit the missing child rather than being masked by the original full queue.
            var reopenedState = new StateStore(root); var reopenedConfig = new Configuration(reopenedState);
            var reopenedSettings = reopenedConfig.Get(); reopenedSettings.QueueCapacity = 20; reopenedConfig.Save(reopenedSettings);
            await using (var engine = new EngineController(engineRoot, engineState))
            {
                await engine.InitializeAsync();
                WorkerWorkflows? host = null;
                var effects = new WorkerWorkflowEffects(reopenedState, reopenedConfig, models, tools, workflowHost: () => host!);
                host = Host(reopenedState, reopenedConfig, events, effects, tools, engine);
                Check(host.Store.RecoverInterrupted() == 1 && host.Store.Get(parentId).Effect?.State == WorkflowEffectState.Unknown,
                    "Restart converts the uncommitted dispatched receipt to Unknown without fabricating a result.");
                Check(reopenedState.Get<JsonElement>("coordination-plan", effectId).GetRawText() == savedPlan &&
                    host.Store.Get(existingChildId).Revision == admittedChild.Revision,
                    "Restart preserves the exact committed plan and existing child revision.");
                var reconciled = await host.Runtime.ReconcileAsync(parentId);
                Check(reconciled.Status == WorkflowStatus.Waiting && reconciled.Effect is
                    { State: WorkflowEffectState.Unknown } receipt && receipt.Id == effectId &&
                    receipt.Error?.Contains("missing work was not replayed", StringComparison.Ordinal) == true,
                    "Read-only native reconciliation retains the original uncertain effect and reports the missing work.");
                Check(host.Store.ChildRunIds(parentId).SequenceEqual(new[] { existingChildId }) && host.Store.TryGet(missingChildId) is null,
                    "Reconciliation never recreates the missing child despite sufficient queue capacity.");
                Check(CerebralJson.Element(host.Store.Get(existingChildId)).GetRawText() == CerebralJson.Element(admittedChild).GetRawText() &&
                    reopenedState.Get<JsonElement>("coordination-plan", effectId).GetRawText() == savedPlan,
                    "Reconciliation changes neither the existing child nor its saved assignment plan.");
                var repeated = await host.Runtime.ReconcileAsync(parentId);
                Check(repeated.Effect?.Id == effectId && repeated.Revision == reconciled.Revision &&
                    host.Store.TryGet(missingChildId) is null && host.Store.ChildRunIds(parentId).Count == 1,
                    "Repeated reconciliation is receipt-idempotent and still performs no child replay.");
                Check(models.Calls == 0 && tools.Executions == 0 && engine.Status().CommittedDecisions == 0,
                    "Recovery performs no model, semantic tool or engine planning execution.");
            }
            Console.WriteLine($"Coordination effect recovery checks passed: {checks} (actual admission/engine/SQLite restart; no model or network calls).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-coordination-recovery-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe recovery fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private static WorkerWorkflows Host(StateStore state, Configuration config, EventHub events, WorkerWorkflowEffects effects,
        IToolDispatcher tools, EngineController engine) => new(state, config, events,
        [new ConversationWorkflowModule(), effects],
        [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
        tools, engine: engine);

    private sealed class NeverModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("coordination-recovery-fixture", "1.0.0", ["text"]);
        public int Calls;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No model execution is permitted by this fixture."); }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("No semantic tool execution is permitted by this fixture."); }
    }
}
