using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Authored deterministic policy and temporary-SQLite fixtures; no native/provider/model calls.</summary>
public static class LocalGenerationEngineChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Local generation Engine: " + name); count++; }
        var policy = Policy();
        var request = new ModelGenerationRequest("fixture-model", [Text("current")], 262_144, 131_073);
        var window = new ContextWindow(1, 0, []);
        var adapter = new PolicyAdapter(policy);
        Check(Reject<ArgumentException>(() => request.Validate()), "legacy request still refuses expanded visible allocation");
        await using (var ready = await ContextPlanner.PrepareWithPolicyAsync(adapter, request, window, policy,
            minimumResponseTokens: 131_073))
        {
            Check(ready.Prepared is not null && ready.Projection.Metadata!.ResponseBudget == 131_073 &&
                adapter.Requests.Single().ResponseLimit == 131_073, "expanded response crosses v1 and half-context caps unchanged");
            Check(ready.Prepared is IModelExecutionPolicyFacet facet && facet.ExecutionPolicy == policy && adapter.Generated == 0,
                "exact policy and same prepared owner survive planning without generation");
        }
        Check(adapter.Leases.Single().Disposed == 1, "final context preparation releases its one owned lease");

        var mutableMessages = new[] { Text("current") };
        using (var schema = JsonDocument.Parse("{\"type\":\"object\"}"))
        {
            var mutableRequest = request with { Messages = mutableMessages, ResponseSchema = schema.RootElement };
            adapter = new PolicyAdapter(policy) { AfterPreparation = () => { mutableMessages[0] = Text("changed"); schema.Dispose(); } };
            await using var frozen = await ContextPlanner.PrepareWithPolicyAsync(adapter, mutableRequest, window, policy);
            Check(((TextContentPart)adapter.Requests.Single().Messages.Single().Content.Single()).Text == "current" &&
                adapter.Requests.Single().ResponseSchema!.Value.GetProperty("type").GetString() == "object",
                "policy request messages and schema are detached before the provider await");
        }

        adapter = new PolicyAdapter(policy);
        Check(await RejectAsync<ArgumentException>(() => ContextPlanner.PrepareAsync(adapter, request, window)),
            "ordinary planner stays v1 even when adapter has a policy facet");
        Check(adapter.Requests.Count == 0, "unselected legacy refusal occurs before provider preparation");
        var noFacet = new PlainAdapter();
        Check(await RejectAsync<InvalidOperationException>(() => ContextPlanner.PrepareWithPolicyAsync(
            noFacet, request, window, policy)) && noFacet.Calls == 0, "policy data alone cannot replace the selected adapter facet");

        var generic = policy with { MinimumContextTokens = 1, MaximumContextTokens = 2_000_000,
            MaximumVisibleOutputTokens = 1_900_000 };
        adapter = new PolicyAdapter(generic);
        await using (var large = await ContextPlanner.PrepareWithPolicyAsync(adapter,
            request with { ContextLimit = 1_048_577 }, window, generic))
            Check(large.Projection.Metadata!.EffectiveContext == 1_048_577,
                "generic planner metadata does not inherit remote-v1 context ceiling; fixture is not native capacity evidence");
        adapter = new PolicyAdapter(generic);
        await using (var small = await ContextPlanner.PrepareWithPolicyAsync(adapter,
            request with { ContextLimit = 256, ResponseLimit = 1 }, window, generic))
            Check(small.Prepared is not null, "generic policy minimum is not replaced by native512");

        var sources = new ContextWindow(2, 0, [Source("optional", ContextRequirement.Optional, "large")]);
        adapter = new PolicyAdapter(policy) { LargeInputTokens = 140_000 };
        await using (var omitted = await ContextPlanner.PrepareWithPolicyAsync(adapter, request, sources, policy))
            Check(omitted.Prepared is not null && adapter.Requests.Count == 2 &&
                omitted.Projection.Omissions.Single().Id == "optional" &&
                adapter.Requests.All(r => r.ResponseLimit == 131_073) && adapter.Generated == 0,
                "optional omission reuses the selected envelope and never silently recaps output");

        var reducible = new ContextWindow(3, 0, [Source("history", ContextRequirement.Reducible, "large")]);
        adapter = new PolicyAdapter(policy) { LargeInputTokens = 140_000 };
        await using (var floor = await ContextPlanner.PreparePreservingResponseWithPolicyAsync(adapter, request, reducible, policy))
            Check(floor.Prepared is null && floor.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                adapter.Requests.Last().ResponseLimit == 131_073 && adapter.Leases.All(l => l.Disposed == 1),
                "required-only probe preserves measured desired output and releases its lease");

        adapter = new PolicyAdapter(policy) { LargeInputTokens = 100_000 };
        await using (var pressure = await ContextPlanner.PrepareWithPolicyAsync(adapter, request, reducible, policy,
            compactionTriggerPercent: 50))
            Check(pressure.Projection.Status == ContextProjectionStatus.NeedsCompaction && adapter.Leases.Count == 2 &&
                adapter.Leases.All(l => l.Disposed == 1) && adapter.Generated == 0,
                "pressure and floor probes dispose before the next policy preparation");

        adapter = new PolicyAdapter(policy) { MutateMetadata = m => m with
            { ResponseBudget = 64, DesiredResponseBudget = 64 } };
        Check(await RejectAsync<InvalidOperationException>(() =>
            ContextPlanner.PreparePreservingResponseWithPolicyAsync(adapter, request, window, policy)) &&
            adapter.Requests.Single().ResponseLimit == 131_073 && adapter.Leases.Single().Disposed == 1 &&
            adapter.Generated == 0,
            "preserving policy planning refuses a lowered desired output budget and releases the unused lease");

        foreach (var mutate in new Func<PreparedModelMetadata, PreparedModelMetadata>[] {
            m => m with { InstanceId = "foreign-instance" },
            m => m with { AdapterId = "foreign-adapter" },
            m => m with { ModelId = "foreign-model" },
            m => m with { InputTokens = -1 },
            m => m with { EffectiveContext = request.ContextLimit + 1 },
            m => m with { ResponseBudget = request.ResponseLimit + 1 },
            m => m with { DesiredResponseBudget = request.ResponseLimit + 1 },
            m => m with { SafetyReserveTokens = -1 },
            m => m with { ReasoningReserveTokens = 128 },
            m => m with { InputTokens = request.ContextLimit } })
        {
            adapter = new PolicyAdapter(policy) { MutateMetadata = mutate };
            Check(await RejectAsync<InvalidOperationException>(() => ContextPlanner.PrepareWithPolicyAsync(adapter, request, window, policy)) &&
                adapter.Leases.Single().Disposed == 1 && adapter.Generated == 0,
                "invalid measured metadata is refused with owned cleanup, not adopted as a fitting request");
        }

        adapter = new PolicyAdapter(policy) { OmitPreparedPolicy = true };
        Check(await RejectAsync<InvalidOperationException>(() => ContextPlanner.PrepareWithPolicyAsync(adapter, request, window, policy)) &&
            adapter.Leases.Single().Disposed == 1, "a wrapper dropping prepared policy cannot downgrade to legacy");
        adapter = new PolicyAdapter(policy);
        adapter.AfterPreparation = () => adapter.ExecutionPolicy = policy with { PolicyRevision = 2 };
        Check(await RejectAsync<InvalidOperationException>(() => ContextPlanner.PrepareWithPolicyAsync(adapter, request, window, policy)) &&
            adapter.Leases.Single().Disposed == 1, "policy drift after await refuses and releases the preparation");
        adapter = new PolicyAdapter(policy) { ExceptionMetadata = Metadata(policy, request) with { InstanceId = "foreign-instance", ResponseBudget = 0 } };
        Check(await RejectAsync<InvalidOperationException>(() => ContextPlanner.PrepareWithPolicyAsync(adapter, request, window, policy)),
            "context-limit diagnostics must retain the exact selected instance identity");
        using (var cancelled = new CancellationTokenSource())
        {
            adapter = new PolicyAdapter(policy) { AfterPreparation = cancelled.Cancel };
            Check(await RejectAsync<OperationCanceledException>(() => ContextPlanner.PrepareWithPolicyAsync(adapter, request, window,
                policy, cancellationToken: cancelled.Token)) && adapter.Leases.Single().Disposed == 1,
                "cancellation after preparation releases unused policy capacity");
        }

        await CheckDeclarationAndRecoveryAsync(Check);
        return count;
    }

    private static async Task CheckDeclarationAndRecoveryAsync(Action<bool, string> check)
    {
        var consumer = new WorkflowLocalGenerationConsumer(1, "cerebral.host:generate", 2, "local.native.text");
        var harness = new DeclarationHarness(consumer);
        var adapted = new AgentWorkflowDefinition(harness);
        check(adapted.Descriptor.LocalGenerationConsumer == consumer &&
            !ReferenceEquals(adapted.Descriptor.LocalGenerationConsumer, consumer), "harness declaration is copied independently of receipt support");
        harness.Descriptor = harness.Descriptor with { LocalGenerationConsumer = null };
        check(adapted.Descriptor.LocalGenerationConsumer == consumer, "later harness descriptor replacement cannot mutate admitted declaration");
        var handler = new JournalHandler();
        var definition = new Definition(consumer);
        var registry = Registry(definition, handler);
        check(registry.GetDefinition(definition.Descriptor.Id, definition.Descriptor.Version).Descriptor.LocalGenerationConsumer == consumer &&
            !ReferenceEquals(registry.Definitions.Single().LocalGenerationConsumer, consumer),
            "generic non-Conversation definition is frozen without workflow identity allowlist");
        check(Reject<ArgumentException>(() => new WorkflowRegistry([new Module("fixture.local", [definition], [])])),
            "declaration requires its exact command version after all modules are collected");
        foreach (var invalid in new[] { consumer with { SchemaVersion = 2 }, consumer with { CommandVersion = 1 },
            consumer with { CommandId = "other:generate" }, consumer with { Route = "remote.text" } })
            check(Reject<ArgumentException>(() => new AgentWorkflowDefinition(new DeclarationHarness(invalid))) &&
                Reject<ArgumentException>(() => Registry(new Definition(invalid), handler)), "malformed generic consumer fails at both copy/freeze boundaries");
        var old = new AgentWorkflowDefinition(new DeclarationHarness(null));
        check(old.Descriptor.LocalGenerationConsumer is null &&
            !CerebralJson.Element(old.Descriptor).TryGetProperty("localGenerationConsumer", out _), "null declaration keeps old descriptor JSON omission");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-local-generation-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "engine.db");
            var journal = new EventJournal(database);
            var store = new WorkflowStore(journal);
            var choice = new WorkflowLocalGenerationChoice(1, "local.native.text", 262_144, 131_073);
            WorkflowSubmission Submission(string id) => new(id, id, "fixture-profile", definition.Descriptor.Id,
                definition.Descriptor.Version, "Fixture input", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject)
                { LocalGeneration = choice };
            var submission = Submission("choice-rid");
            var submitted = store.Submit(submission);
            var sequence = journal.GetRevision(submission.ConversationId);
            check(store.Submit(submission with { LocalGeneration = choice with { } }).Id == submitted.Id &&
                journal.GetRevision(submission.ConversationId) == sequence, "same RID and identical detached choice create no duplicate journal input");
            check(Reject<WorkflowConflictException>(() => store.Submit(submission with
                { LocalGeneration = choice with { VisibleOutputTokens = 131_074 } })), "same RID with retry-input choice change conflicts");
            check(Reject<WorkflowConflictException>(() => WorkflowStore.RequireEquivalentRetainedSubmission(submitted.Submission,
                submission with { LocalGeneration = null })), "retry cannot erase an accepted choice");
            check(submitted.Submission.LocalGeneration == choice, "Store roundtrip preserves exact accepted value");
            store.Cancel(submission.RequestId);
            foreach (var bad in new[] { choice with { SchemaVersion = 2 }, choice with { Route = "remote.text" },
                choice with { ContextTokens = 0 }, choice with { VisibleOutputTokens = 0 } })
                check(Reject<ArgumentException>(() => store.Submit(Submission("invalid-choice") with { LocalGeneration = bad })) &&
                    store.TryGet("invalid-choice") is null, "invalid choice is refused before durable submission");
            var legacy = Submission("legacy-choice") with { LocalGeneration = null };
            check(!CerebralJson.Element(legacy).TryGetProperty("localGeneration", out _) &&
                store.Submit(legacy).Submission.LocalGeneration is null, "old submission remains null and JSON-compatible");
            store.Cancel(legacy.RequestId);

            WorkflowRun Prepare(string id)
            {
                var queued = store.Submit(Submission(id));
                var claimed = store.TryClaim(queued.Id)!;
                return store.ApplyStep(claimed.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand("cerebral.host:generate", 2,
                        CerebralJson.Element(new { responseLimit = choice.VisibleOutputTokens }))), handler.Descriptor);
            }
            handler.Observe = id => store.Get(id).Effect?.State == WorkflowEffectState.Dispatched;
            var prepared = Prepare("prepared-recovery");
            check(prepared.Effect is { CommandVersion: 2, State: WorkflowEffectState.Prepared }, "v2 intent is durably Prepared before handler work");
            store = new WorkflowStore(new EventJournal(database));
            store.RecoverInterrupted();
            var restored = store.Get(prepared.Id);
            check(restored.Status == WorkflowStatus.Queued && restored.Effect?.Id == prepared.Effect!.Id &&
                restored.Effect.CommandVersion == 2 && restored.Submission.LocalGeneration == choice,
                "Prepared recovery preserves command identity/version/arguments and accepted choice");
            var runtime = new WorkflowRuntime(store, registry, (_, _) => true);
            var complete = await runtime.RunAsync(prepared.Id);
            check(complete.Status == WorkflowStatus.Completed && handler.Executions == 1 && handler.ObservedDurableDispatch,
                "first recovered handler call sees committed Dispatched before fake readiness/generation");
            var unknown = Prepare("dispatched-recovery");
            store.DispatchEffect(unknown.Id, unknown.Revision);
            store.RecoverInterrupted();
            var executions = handler.Executions;
            var waiting = await runtime.RunAsync(unknown.Id);
            check(waiting.Status == WorkflowStatus.Waiting && waiting.Effect?.State == WorkflowEffectState.Unknown &&
                handler.Executions == executions, "recovered Dispatched effect never re-enters execution");
            await runtime.ReconcileAsync(unknown.Id);
            check(handler.Executions == executions && handler.Reconciliations == 1 &&
                store.Get(unknown.Id).Effect?.State == WorkflowEffectState.Unknown,
                "Unknown reconciliation inspects the existing effect without fake generation");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ModelExecutionPolicy Policy() => new(1, "fixture.policy", 1,
        new("fixture-worker", "fixture-backend", "fixture-adapter", "1", "fixture-model",
            "fixture.scope/1", new string('a', 64), "fixture-instance"),
        "fixture-epoch", "text", false, 512, 1_048_576, 1, 1_048_447, "deterministic-fixture-not-hardware");
    private static ModelInputMessage Text(string text) => new("user", [new TextContentPart(text)]);
    private static ContextContribution Source(string id, ContextRequirement requirement, string text) =>
        new(id, "fixture.owner", "history", 1, requirement, [Text(text)]);
    private static PreparedModelMetadata Metadata(ModelExecutionPolicy policy, ModelGenerationRequest request, int input = 100) =>
        new(policy.Binding.InstanceId!, policy.Binding.AdapterId, policy.Binding.AdapterVersion, request.ModelId,
            input, request.ContextLimit, request.ResponseLimit, "deterministic-fixture")
        { DesiredResponseBudget = request.ResponseLimit, SafetyReserveTokens = 128 };
    private static bool Reject<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }

    private sealed class PolicyAdapter(ModelExecutionPolicy policy) : IModelRuntimeAdapter, IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; set; } = policy;
        public ModelAdapterDescriptor Descriptor { get; } = new(policy.Binding.AdapterId, policy.Binding.AdapterVersion, ["text"]);
        public List<ModelGenerationRequest> Requests { get; } = [];
        public List<PlainPreparation> Leases { get; } = [];
        public int Generated;
        public int LargeInputTokens = 140_000;
        public bool OmitPreparedPolicy;
        public Action? AfterPreparation;
        public Func<PreparedModelMetadata, PreparedModelMetadata>? MutateMetadata;
        public PreparedModelMetadata? ExceptionMetadata;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            request.Validate(ExecutionPolicy); Requests.Add(request);
            if (ExceptionMetadata is { } failure) throw new ModelContextLimitException(failure);
            var input = request.Messages.SelectMany(m => m.Content).OfType<TextContentPart>().Any(t => t.Text == "large")
                ? LargeInputTokens : 100;
            var metadata = Metadata(ExecutionPolicy, request, input);
            if ((long)input + request.ResponseLimit + 128 > request.ContextLimit)
                throw new ModelContextLimitException(metadata with { ResponseBudget = 0 });
            metadata = MutateMetadata?.Invoke(metadata) ?? metadata;
            PlainPreparation lease = OmitPreparedPolicy ? new PlainPreparation(this, metadata) : new PolicyPreparation(this, metadata, ExecutionPolicy);
            Leases.Add(lease); AfterPreparation?.Invoke();
            return Task.FromResult<IPreparedModelGeneration>(lease);
        }
    }
    private class PlainPreparation(PolicyAdapter owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata { get; } = metadata;
        public int Disposed;
        private int used;
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            if (Disposed != 0 || Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("Fixture lease is one-use.");
            owner.Generated++;
            return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "fixture"));
        }
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
    private sealed class PolicyPreparation(PolicyAdapter owner, PreparedModelMetadata metadata, ModelExecutionPolicy policy)
        : PlainPreparation(owner, metadata), IModelExecutionPolicyFacet
    { public ModelExecutionPolicy ExecutionPolicy { get; } = policy; }
    private sealed class PlainAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture-adapter", "1", ["text"]);
        public int Calls;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Calls++; throw new NotSupportedException("Must refuse the absent facet before fixture preparation."); }
    }
    private sealed class DeclarationHarness(WorkflowLocalGenerationConsumer? consumer) : IAgentHarness
    {
        public AgentHarnessDescriptor Descriptor { get; set; } = new("fixture.local:chat", "1.0.0", "Fixture declaration",
            CerebralJson.Element(new { type = "object" }), []) { LocalGenerationConsumer = consumer };
        public WorkflowStep Advance(AgentHarnessSnapshot snapshot) => new(WorkflowContract.EmptyObject,
            new CompleteWorkflow(WorkflowContract.EmptyObject));
    }
    private sealed class Definition(WorkflowLocalGenerationConsumer consumer) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.local:chat", "1.0.0", "Fixture definition",
            CerebralJson.Element(new { type = "object" }), []) { LocalGenerationConsumer = consumer };
        public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject,
            new CompleteWorkflow(WorkflowContract.EmptyObject, run.Effect?.State == WorkflowEffectState.Succeeded));
    }
    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private static WorkflowRegistry Registry(IWorkflowDefinition definition, IWorkflowCommandHandler handler) =>
        new([new Module("fixture.local", [definition], []), new Module("cerebral.host", [], [handler])]);
    private sealed class JournalHandler : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new("cerebral.host:generate", 2, "Fixture v2 generation",
            CerebralJson.Element(new { type = "object" }), CerebralJson.Element(new { type = "object" }), [], WorkflowReplayPolicy.Never);
        public Func<string, bool>? Observe;
        public int Executions, Reconciliations;
        public bool ObservedDurableDispatch;
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Executions++; ObservedDurableDispatch = Observe?.Invoke(context.RunId!) == true;
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token)
        { Reconciliations++; return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Unknown)); }
    }
}
