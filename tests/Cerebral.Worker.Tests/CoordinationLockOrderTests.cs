using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real StateStore/controller gates and coordination plan commit; no model, network or Worker process.</summary>
internal static class CoordinationLockOrderTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-coordination-lock-order-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        try
        {
            var state = new StateStore(root); var configuration = new Configuration(state); var events = new EventHub();
            var settings = configuration.Get(); settings.QueueCapacity = 20; settings.MaxConcurrentJobs = 2; configuration.Save(settings);
            state.Put("model", "fixture", new ModelRecord
            {
                Id = "fixture", Name = "Fixture", Path = Path.Combine(root, "not-a-loaded-model.fixture"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            foreach (var (id, role) in new[] { ("lead", "lead"), ("helper", "helper") })
                state.Put("agent", id, new AgentProfile
                {
                    Id = id, Name = id, Role = role, Revision = 1, ModelId = "fixture",
                    FollowsDefaultModel = false, FollowsModelBudgets = false, AutomaticCompaction = false,
                    ContextTokens = 8192, ResponseTokens = 1024, MaxSteps = 24, MaxSeconds = 60,
                    AllowedRoots = [root], AllowedTools = [], Capabilities = ["agents.delegate"]
                });
            var engineRoot = Path.Combine(root, "Engines");
            EngineHostTests.Pack(engineRoot, "A", true);
            await using var engine = new EngineController(engineRoot, Path.Combine(root, "EngineState"));
            await engine.InitializeAsync();
            var models = new NeverModels(); var tools = new NoTools();
            WorkerWorkflows? host = null;
            var effects = new WorkerWorkflowEffects(state, configuration, models, tools, workflowHost: () => host!);
            host = new(state, configuration, events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                tools, engine: engine);
            var parent = await host.SubmitAsync(new("lock-order-parent", "lock-order-context", "lead",
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Return independent bounded helper evidence.", null, WorkflowContract.EmptyObject), null, default);
            var helperRevision = state.Get<AgentProfile>("agent", "helper")!.Revision;
            WorkerWorkflowEffects.AgentAssignment[] assignments = [new("helper", helperRevision, "Return HELPER_ONLY evidence.")];
            var arguments = CerebralJson.Element(new { assignments });
            var handler = effects.Commands.Single(value => value.Descriptor.Id == WorkerWorkflowEffects.DelegateCommand);
            var claimed = host.Store.TryClaim(parent.Id)!;
            var prepared = host.Store.ApplyStep(parent.Id, claimed.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(handler.Descriptor.Id, 1, arguments)), handler.Descriptor);
            var dispatched = host.Store.DispatchEffect(parent.Id, prepared.Revision);
            await using var unusedRuntime = new ModelRuntime(state, configuration.Get, events);
            using var scheduler = new JobScheduler(state, configuration, new UnusedExecutor(), unusedRuntime, events,
                NullLogger<JobScheduler>.Instance, host);
            var context = new WorkflowCommandContext(dispatched.Effect!.Id, dispatched.Submission, arguments) { RunId = parent.Id };
            async Task<EngineController.DecisionLease> Decision() => await engine.EvaluateAsync(dispatched.Id, dispatched.Revision,
                "cerebral.agents.plan", 1, arguments,
                _ => throw new InvalidOperationException("Planning must use the actual default engine."), default);

            // Reflect only this fixture's controller gate, so a failing former ordering can be detected
            // with a bounded TryEnter rather than leaving two permanently deadlocked test threads.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engineGate = typeof(EngineController).GetField("gate", flags)!.GetValue(engine)!;
            var commitPlan = typeof(WorkerWorkflowEffects).GetMethod("CommitAgentPlan", flags)!;
            object Commit(EngineController.DecisionLease decision, WorkflowRun expectedParent,
                IReadOnlyList<WorkerWorkflowEffects.AgentAssignment>? planned = null, WorkflowCommandContext? selectedContext = null)
            {
                try { return commitPlan.Invoke(effects, [host, selectedContext ?? context, expectedParent, planned ?? assignments, decision])!; }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
            bool EngineReadableWhileStoreHeld(Action commit, Action? actualSchedulerRead = null)
            {
                Exception? failure = null;
                using var entered = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    entered.Set();
                    try { commit(); }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true, Name = "coordination-lock-order-commit" };
                var joined = false;
                try
                {
                    var readable = state.Locked(() =>
                    {
                        thread.Start();
                        if (!entered.Wait(TimeSpan.FromSeconds(5)) ||
                            !SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0 || !thread.IsAlive,
                                TimeSpan.FromSeconds(5)) || !thread.IsAlive)
                            throw new InvalidOperationException("Commit thread did not reach bounded gate contention.");
                        var taken = Monitor.TryEnter(engineGate, TimeSpan.FromSeconds(1));
                        if (!taken) return false;
                        try
                        {
                            var readable = !engine.IsPaused;
                            actualSchedulerRead?.Invoke();
                            return readable;
                        }
                        finally { Monitor.Exit(engineGate); }
                    });
                    joined = thread.Join(TimeSpan.FromSeconds(5));
                    if (!joined) throw new InvalidOperationException("Commit did not settle after the test released StateStore.");
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    return readable;
                }
                finally
                {
                    // StateStore.Locked has unwound before cleanup, including any failed assertion.
                    if (!joined && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("Fixture commit thread remained blocked after all owned gates released.");
                }
            }

            using (var former = await Decision())
                Check(!EngineReadableWhileStoreHeld(() => former.Commit(_ => state.Locked(() => true))),
                    "Bounded negative control detects the former engine-to-store cycle without hanging.");
            Check(engine.Status().ActiveEvaluations == 0, "Negative-control decision is released after bounded cleanup.");

            var committedBefore = engine.Status().CommittedDecisions;
            using (var decision = await Decision())
            {
                var idleChecks = 0;
                Check(EngineReadableWhileStoreHeld(() => Commit(decision, dispatched), () =>
                {
                    Check(host.Store.ListReady().Count == 0, "Idle admission regression has no queued workflow prerequisite.");
                    typeof(JobScheduler).GetMethod("StartIdleCompaction", flags)!.Invoke(scheduler, [CancellationToken.None]);
                    idleChecks++;
                }), "Actual coordination plan commit waits for StateStore before taking the engine gate.");
                Check(idleChecks == 1, "Actual StartIdleCompaction can pass its store-to-engine read while plan commit waits.");
                Check(engine.Status().CommittedDecisions == committedBefore + 1,
                    "Exactly one engine decision commits the durable coordination plan.");
                var plan = state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText();
                Check(plan.Contains("HELPER_ONLY", StringComparison.Ordinal) && host.Store.ChildRunIds(parent.Id).Count == 0,
                    "The actual plan persists while child admission remains outside this commit boundary.");
                try { Commit(decision, dispatched); throw new InvalidOperationException("A committed decision ran twice."); }
                catch (InvalidOperationException error) when (error.Message.Contains("already committed", StringComparison.Ordinal)) { checks++; }
                Check(state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText() == plan &&
                    engine.Status().CommittedDecisions == committedBefore + 1,
                    "Repeated decision rejection leaves both plan bytes and commit count unchanged.");
            }

            var expectedCommits = committedBefore + 1;
            Check(state.WithTransactionFreeGate(() => typeof(StateStore).GetField("transactionConnection", flags)!.GetValue(state) is null),
                "The outer store gate-only helper opens no SQLite transaction.");
            var ambientContext = new WorkflowCommandContext("ambient-" + Guid.NewGuid().ToString("N"),
                dispatched.Submission, arguments) { RunId = parent.Id };
            using (var ambient = await Decision())
            {
                state.Locked(() =>
                {
                    try { Commit(ambient, dispatched, selectedContext: ambientContext); throw new InvalidOperationException("Ambient transaction was accepted."); }
                    catch (InvalidOperationException error) when (error.Message.Contains("refuses an ambient", StringComparison.Ordinal)) { checks++; }
                    Check(engine.Status().CommittedDecisions == expectedCommits &&
                        state.Get<Dictionary<string, JsonElement>>("coordination-plan", ambientContext.EffectId) is null,
                        "Ambient-transaction refusal reaches neither the engine commit nor plan persistence.");
                    return true;
                });
                Commit(ambient, dispatched, selectedContext: ambientContext);
                expectedCommits++;
                Check(engine.Status().CommittedDecisions == expectedCommits &&
                    state.Get<Dictionary<string, JsonElement>>("coordination-plan", ambientContext.EffectId) is not null,
                    "An ambient-refused lease can commit once after its caller transaction ends.");
            }

            var failedContext = new WorkflowCommandContext("commit-failure-" + Guid.NewGuid().ToString("N"),
                dispatched.Submission, arguments) { RunId = parent.Id };
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "cerebral.db"), ForeignKeys = true }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE lock_order_parent(id INTEGER PRIMARY KEY);
                    CREATE TABLE lock_order_child(id INTEGER PRIMARY KEY,
                        parent_id INTEGER REFERENCES lock_order_parent(id) DEFERRABLE INITIALLY DEFERRED);
                    CREATE TRIGGER lock_order_deferred_failure AFTER INSERT ON records
                    WHEN NEW.kind='coordination-plan' AND NEW.id='{failedContext.EffectId}'
                    BEGIN INSERT INTO lock_order_child(id,parent_id) VALUES(1,404); END;
                    """;
                command.ExecuteNonQuery();
            }
            using (var failure = await Decision())
            {
                try { Commit(failure, dispatched, selectedContext: failedContext); throw new InvalidOperationException("Deferred commit fault did not occur."); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19)
                {
                    Check(error.StackTrace?.Contains("SqliteTransaction.Commit", StringComparison.Ordinal) == true,
                        "Deferred foreign-key failure occurs at actual SQLite COMMIT, after plan callback returns.");
                }
                Check(engine.Status().CommittedDecisions == expectedCommits &&
                    state.Get<Dictionary<string, JsonElement>>("coordination-plan", failedContext.EffectId) is null,
                    "Failed SQLite COMMIT leaves the engine decision uncommitted and no durable plan.");
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(root, "cerebral.db"), ForeignKeys = true }.ToString()))
                {
                    connection.Open();
                    using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM lock_order_child";
                    Check((long)count.ExecuteScalar()! == 0, "Failed COMMIT rolls back the complete triggered transaction.");
                    using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER lock_order_deferred_failure";
                    command.ExecuteNonQuery();
                }
                Commit(failure, dispatched, selectedContext: failedContext);
                expectedCommits++;
                Check(engine.Status().CommittedDecisions == expectedCommits &&
                    state.Get<Dictionary<string, JsonElement>>("coordination-plan", failedContext.EffectId) is not null,
                    "The same uncommitted lease can commit exactly once after fixture-only failure removal.");
            }

            var acceptedPlan = state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText();
            var changed = new[] { assignments[0] with { Instruction = "MUST_NOT_COMMIT" } };
            using (var stale = await Decision())
            {
                // Synthetic envelope drift tests the real unchanged controller epoch fence; no engine
                // activation, owner operation or production lifecycle action is performed.
                var requestField = typeof(EngineController.DecisionLease).GetField("request", flags)!;
                var original = (EngineRequest)requestField.GetValue(stale)!;
                requestField.SetValue(stale, original with { ActivationEpoch = original.ActivationEpoch + 1 });
                try
                {
                    try { Commit(stale, dispatched, changed); throw new InvalidOperationException("A stale engine decision committed."); }
                    catch (EngineEvaluationException) { checks++; }
                }
                finally { requestField.SetValue(stale, original); }
            }
            Check(state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText() == acceptedPlan &&
                engine.Status().CommittedDecisions == expectedCommits,
                "Stale engine epoch rejection performs no plan write or committed-decision increment.");

            using (var revision = await Decision())
            {
                host.Store.Cancel(parent.Id);
                try { Commit(revision, dispatched, changed); throw new InvalidOperationException("A changed parent committed."); }
                catch (WorkflowConflictException) { checks++; }
            }
            Check(state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText() == acceptedPlan,
                "Parent revision/cancellation is rechecked inside the store-and-engine fence.");

            using (var faulted = await Decision())
            {
                faulted.Reject("Task-owned fault injection before coordination commit.");
                try { Commit(faulted, dispatched, changed); throw new InvalidOperationException("A faulted engine decision committed."); }
                catch (EngineEvaluationException) { checks++; }
            }
            Check(state.Get<JsonElement>("coordination-plan", context.EffectId).GetRawText() == acceptedPlan &&
                engine.Status().CommittedDecisions == expectedCommits,
                "Faulted engine rejection preserves the original plan and once-only decision count.");
            Check(models.Calls == 0 && tools.Executions == 0 && engine.Status().ActiveEvaluations == 0,
                "Fixture closes every real decision lease with no model, semantic-tool, network or Worker process.");
            Console.WriteLine($"Coordination lock order: {checks} deterministic real-gate/StateStore/engine assertions passed; no model or Worker process.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-coordination-lock-order-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe lock-order fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private sealed class UnusedExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("No legacy executor is permitted in this fixture.");
    }

    private sealed class NeverModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("coordination-lock-fixture", "1.0.0", ["text"]);
        public int Calls;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No model execution is permitted in this fixture."); }
    }
    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("No semantic tool execution is permitted in this fixture."); }
    }
}
