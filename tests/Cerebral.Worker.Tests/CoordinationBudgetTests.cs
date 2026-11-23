using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual coordination admission/runtime and owned SQLite with deterministic workflow effects.
/// No Worker process, network, model inference or semantic tool execution.</summary>
internal static class CoordinationBudgetTests
{
    private const string ProgrammingId = "pegasus.programming:develop";
    private const string ThirdPartyId = "fixture.budget:transitions";
    private const string BindingKey = "cerebral.coordination";
    private const string FixtureVersion = "1.0.0";

    internal static async Task<int> RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cerebral-coordination-budget-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            var state = new StateStore(root);
            var configuration = new Configuration(state);
            var events = new EventHub();
            var settings = configuration.Get();
            settings.QueueCapacity = 100;
            // Pin the fixture default before creating its first lead. Otherwise Get's legacy
            // implicit-default migration rewrites helper revisions after assignments are captured.
            settings.DefaultModelId = "budget-fixture";
            settings.DefaultModelWorkerId = "local";
            state.Put("model", "budget-fixture", new ModelRecord
            {
                Id = "budget-fixture", Name = "Unloaded deterministic budget fixture",
                Path = Path.Combine(root, "not-loaded.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            configuration.Save(settings);

            var engineRoot = Path.Combine(root, "Engines");
            EngineHostTests.Pack(engineRoot, "A", true);
            await using var engine = new EngineController(engineRoot, Path.Combine(root, "EngineState"));
            await engine.InitializeAsync();
            var models = new NeverModels();
            var tools = new NoTools();
            var programming = new FixtureModule("pegasus.programming", ProgrammingId,
                "pegasus.programming:budget-probe");
            var thirdParty = new FixtureModule("fixture.budget", ThirdPartyId,
                "fixture.budget:budget-probe", includeRecursiveDefinitions: true);
            WorkerWorkflows? host = null;
            var effects = new WorkerWorkflowEffects(state, configuration, models, tools, workflowHost: () => host!);
            host = new WorkerWorkflows(state, configuration, events,
                [new ConversationWorkflowModule(), effects, programming, thirdParty],
                [ConversationWorkflowModule.PackageBinding,
                    new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []),
                    new(programming.Id, FixtureVersion, true, [], Generation: "budget-programming-fixture"),
                    new(thirdParty.Id, FixtureVersion, true, [], Generation: "budget-third-party-fixture")],
                tools, engine: engine);
            var handler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.DelegateCommand);

            AgentProfile SaveProfile(string id, string role, int maximumActions, int maximumSeconds = 37,
                string? definitionId = null, int completeAfter = 0)
            {
                var profile = new AgentProfile
                {
                    Id = id, Name = id, Purpose = "Isolated coordination budget fixture", Role = role,
                    Revision = 1, ModelId = "budget-fixture", FollowsDefaultModel = false,
                    FollowsModelBudgets = false, AutomaticCompaction = false,
                    ContextTokens = 8192, ResponseTokens = 1024,
                    MaxSteps = maximumActions, MaxSeconds = maximumSeconds,
                    AllowedRoots = [root], AllowedTools = [],
                    Capabilities = ["agents.delegate", "agents.collaborate"]
                };
                if (definitionId is not null)
                    profile.PluginSettings[BindingKey] = Binding(definitionId, completeAfter);
                state.Put("agent", profile.Id, profile);
                return profile;
            }

            async Task<Invocation> Prepare(string name, int parentActions,
                WorkerWorkflowEffects.AgentAssignment[] assignments)
            {
                var lead = SaveProfile("lead-" + name, "lead", parentActions, maximumSeconds: 11);
                var parent = await host.SubmitAsync(new("budget-" + name, "budget-" + name + "-chat", lead.Id,
                    ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                    "Return independent bounded fixture evidence.", null, WorkflowContract.EmptyObject,
                    StepLimit: WorkflowContract.MaximumSteps), null, token);
                var arguments = CerebralJson.Element(new { assignments });
                WorkflowSchema.Validate(arguments, handler.Descriptor.InputSchema);
                var claimed = host.Store.TryClaim(parent.Id)
                    ?? throw new InvalidOperationException("Fixture parent could not be claimed.");
                var prepared = host.Store.ApplyStep(parent.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject,
                        new InvokeWorkflowCommand(handler.Descriptor.Id, handler.Descriptor.Version, arguments)),
                    handler.Descriptor);
                var dispatched = host.Store.DispatchEffect(parent.Id, prepared.Revision);
                var context = new WorkflowCommandContext(dispatched.Effect!.Id, dispatched.Submission, arguments)
                    { RunId = parent.Id };
                return new(dispatched, context, assignments);
            }

            async Task<(Invocation Invocation, WorkflowRun Child)> Admit(string name, int parentActions,
                AgentProfile helper, int expectedPolicy)
            {
                WorkerWorkflowEffects.AgentAssignment[] assignments =
                    [new(helper.Id, helper.Revision, "Return only this independent fixture result.")];
                var invocation = await Prepare(name, parentActions, assignments);
                var result = await handler.ExecuteAsync(invocation.Context, token);
                Check(result.State == WorkflowEffectState.Succeeded,
                    name + ": delegate admission must succeed. " + result.Error);
                var childIds = host.Store.ChildRunIds(invocation.Parent.Id);
                Check(childIds.Count == 1, name + ": exactly one child is admitted.");
                var child = host.Store.Get(childIds.Single());
                Check(child.Submission.StepLimit == WorkflowContract.MaximumSteps,
                    name + ": coordination requests the engine maximum rather than borrowing the parent limit.");
                Check(child.Submission.PolicyStepLimit == expectedPolicy &&
                    child.Submission.EffectiveStepLimit == expectedPolicy,
                    name + ": the helper's workflow-specific policy determines its effective transition limit.");
                Check(invocation.Parent.Submission.StepLimit == WorkflowContract.MaximumSteps &&
                    invocation.Parent.Submission.PolicyStepLimit == Math.Min(WorkflowContract.MaximumSteps, parentActions * 4),
                    name + ": the parent's separately frozen policy is retained.");
                Check(child.Submission.ActiveTimeLimitSeconds == helper.MaxSeconds &&
                    child.Submission.ActiveTimeLimitSeconds != invocation.Parent.Submission.ActiveTimeLimitSeconds,
                    name + ": the helper keeps its own time budget.");
                var snapshot = WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot);
                Check(snapshot.Profile.Revision == helper.Revision && !snapshot.Profile.FollowsDefaultModel &&
                    snapshot.Profile.MaxSteps == helper.MaxSteps && snapshot.Profile.MaxSeconds == helper.MaxSeconds &&
                    snapshot.Profile.Id == helper.Id && snapshot.Profile.Role == "helper" &&
                    !snapshot.Capabilities.Contains("agents.delegate") && !snapshot.Capabilities.Contains("agents.collaborate") &&
                    !snapshot.Profile.Capabilities.Contains("agents.delegate") &&
                    !snapshot.Profile.Capabilities.Contains("agents.collaborate"),
                    name + ": the accepted helper profile retains its own limits and loses recursive grants.");
                Check(child.Submission.ParentRunId == invocation.Parent.Id &&
                    child.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" } &&
                    child.Submission.ConversationId != invocation.Parent.Submission.ConversationId,
                    name + ": host lineage, service provenance and separate context remain intact.");
                return (invocation, child);
            }

            // The exact Programming definition identity matters: a similarly named third-party
            // fixture would not exercise the host's built-in action-to-transition conversion.
            var largeHelper = SaveProfile("program-64", "helper", 64, definitionId: ProgrammingId, completeAfter: 64);
            var first = await Admit("parent-12", 12, largeHelper, 256);
            var largerParent = await Admit("parent-80", 80, largeHelper, 256);
            Check(first.Child.Submission.EffectiveStepLimit == largerParent.Child.Submission.EffectiveStepLimit &&
                first.Invocation.Parent.Submission.EffectiveStepLimit == 48 &&
                largerParent.Invocation.Parent.Submission.EffectiveStepLimit == 320,
                "Changing only the parent's budget does not change the helper's 256-transition allowance.");

            var smallerHelper = SaveProfile("program-3", "helper", 3, definitionId: ProgrammingId, completeAfter: 64);
            var smaller = await Admit("smaller-helper", 80, smallerHelper, 12);
            var ceilingHelper = SaveProfile("program-ceiling", "helper", 10_000, definitionId: ProgrammingId);
            await Admit("global-ceiling", 12, ceilingHelper, WorkflowContract.MaximumSteps);
            var thirdPartyHelper = SaveProfile("third-party-20", "helper", 20, definitionId: ThirdPartyId);
            await Admit("third-party-units", 12, thirdPartyHelper, 20);
            var conversationHelper = SaveProfile("conversation-64", "helper", 64);
            await Admit("conversation-units", 12, conversationHelper, 256);

            var commandsBefore = programming.Command.Executions;
            var completed = await host.Runtime.RunAsync(first.Child.Id, token);
            Check(completed.Status == WorkflowStatus.Completed && completed.Steps == 65 && completed.Steps > 48,
                "A deterministic Programming-identity child actually commits beyond the old inherited 48-step cutoff.");
            Check(programming.Command.Executions - commandsBefore == 64,
                "The successful runtime case uses exactly its 64 deterministic fixture effects.");
            commandsBefore = programming.Command.Executions;
            var exhausted = await host.Runtime.RunAsync(smaller.Child.Id, token);
            Check(exhausted.Status == WorkflowStatus.Failed && exhausted.Steps == 12 &&
                exhausted.Error?.Contains("step budget", StringComparison.Ordinal) == true,
                "The real runtime still exhausts the smaller helper's own 12-transition policy.");
            Check(programming.Command.Executions - commandsBefore == 12,
                "No fixture effect escapes the smaller helper's policy limit.");

            void SeedPlan(Invocation invocation, params CoordinationWorkflowBinding[] bindings) =>
                state.Put("coordination-plan", invocation.Context.EffectId, new
                {
                    parentRunId = invocation.Parent.Id,
                    assignments = invocation.Assignments,
                    workflows = bindings
                });

            // This is a bounded persisted-plan fixture, not evidence of engine plan commit.
            // The legacy child itself goes through real child admission with its accepted limit.
            var legacyHelper = SaveProfile("legacy-program-64", "helper", 64, definitionId: ProgrammingId, completeAfter: 64);
            WorkerWorkflowEffects.AgentAssignment[] legacyAssignments =
                [new(legacyHelper.Id, legacyHelper.Revision, "Preserve this previously accepted bounded child.")];
            var legacy = await Prepare("legacy-48", 12, legacyAssignments);
            var legacyBinding = host.ResolveHelperWorkflow(legacyHelper, null);
            SeedPlan(legacy, legacyBinding);
            var legacyId = "child-" + legacy.Context.EffectId + "-0";
            var legacyChild = await host.SubmitChildAsync(new(legacyId, legacyId, legacyHelper.Id,
                legacyBinding.DefinitionId, legacyBinding.DefinitionVersion, legacyAssignments[0].Instruction,
                "Previously admitted fixture child.", legacyBinding.Input, StepLimit: 48),
                legacy.Parent.Id, legacyHelper.Revision, token);
            Check(legacyChild.Submission.StepLimit == 48 && legacyChild.Submission.PolicyStepLimit == 256 &&
                legacyChild.Submission.EffectiveStepLimit == 48,
                "The legacy fixture is a genuinely admitted child with the old 48-transition request limit.");
            var childBefore = CerebralJson.Element(legacyChild).GetRawText();
            var planBefore = state.Get<JsonElement>("coordination-plan", legacy.Context.EffectId).GetRawText();
            legacyHelper.Revision++;
            legacyHelper.MaxSteps = 1;
            legacyHelper.PluginSettings[BindingKey] = Binding(ThirdPartyId, 0);
            state.Put("agent", legacyHelper.Id, legacyHelper);
            var decisionsBefore = engine.Status().CommittedDecisions;
            var effectsBefore = programming.Command.Executions + thirdParty.Command.Executions;
            foreach (var reconcile in new[] { false, true })
            {
                var replay = reconcile
                    ? await handler.ReconcileAsync(legacy.Context, token)
                    : await handler.ExecuteAsync(legacy.Context, token);
                Check(replay.State == WorkflowEffectState.Succeeded &&
                    replay.Data!.Value.GetProperty("runIds").EnumerateArray().Select(value => value.GetString())
                        .SequenceEqual(new[] { legacyId }) &&
                    replay.ProviderReceiptId == legacy.Context.EffectId,
                    "Legacy " + (reconcile ? "reconciliation" : "execution retry") + " retrieves the existing child identity.");
                Check(CerebralJson.Element(host.Store.Get(legacyId)).GetRawText() == childBefore &&
                    state.Get<JsonElement>("coordination-plan", legacy.Context.EffectId).GetRawText() == planBefore &&
                    host.Store.ChildRunIds(legacy.Parent.Id).SequenceEqual(new[] { legacyId }),
                    "Replay leaves the complete legacy child, budget, revision and saved plan unchanged.");
            }
            Check(engine.Status().CommittedDecisions == decisionsBefore &&
                programming.Command.Executions + thirdParty.Command.Executions == effectsBefore,
                "Legacy replay neither replans nor executes a workflow effect.");

            async Task Reject(string name, AgentProfile helper, Action<Invocation>? afterPrepare = null,
                string? expectedError = null)
            {
                WorkerWorkflowEffects.AgentAssignment[] assignments =
                    [new(helper.Id, helper.Revision, "Reject this changed or inadmissible fixture binding.")];
                var invocation = await Prepare(name, 12, assignments);
                afterPrepare?.Invoke(invocation);
                var before = engine.Status().CommittedDecisions;
                var result = await handler.ExecuteAsync(invocation.Context, token);
                Check(result.State == WorkflowEffectState.Failed && !string.IsNullOrWhiteSpace(result.Error) &&
                    (expectedError is null || result.Error.Contains(expectedError, StringComparison.Ordinal)),
                    name + ": admission must reject the exact invalid condition. " + result.Error);
                Check(host.Store.ChildRunIds(invocation.Parent.Id).Count == 0,
                    name + ": a rejected assignment creates no child.");
                Check(engine.Status().CommittedDecisions == before,
                    name + ": rejection does not commit a new engine plan.");
            }

            var stale = SaveProfile("stale-revision", "helper", 64, definitionId: ProgrammingId);
            await Reject("stale-revision", stale, _ =>
            {
                stale.Revision++;
                state.Put("agent", stale.Id, stale);
            }, "revision changed");

            var invalid = SaveProfile("invalid-binding", "helper", 64, definitionId: ProgrammingId);
            invalid.PluginSettings[BindingKey] = CerebralJson.Element(new
                { schemaVersion = 1, definitionId = ProgrammingId, definitionVersion = FixtureVersion });
            state.Put("agent", invalid.Id, invalid);
            await Reject("invalid-binding", invalid, expectedError: "requires schemaVersion");

            var changedBinding = SaveProfile("changed-binding", "helper", 64, definitionId: ProgrammingId);
            await Reject("changed-binding", changedBinding, invocation =>
            {
                SeedPlan(invocation, host.ResolveHelperWorkflow(changedBinding, null));
                // Simulate an out-of-band edit without a revision increment. The saved binding
                // comparison must still reject changed work before any missing child is admitted.
                changedBinding.PluginSettings[BindingKey] = Binding(ProgrammingId, 1);
                state.Put("agent", changedBinding.Id, changedBinding);
            }, "changed after planning");

            foreach (var capability in new[] { "delegate", "collaborate" })
            {
                var recursive = SaveProfile("recursive-" + capability, "helper", 64,
                    definitionId: "fixture.budget:recursive-" + capability);
                await Reject("recursive-" + capability, recursive, expectedError: "lacks required workflow capabilities");
            }

            Check(models.Calls == 0 && tools.Executions == 0,
                "All budget, runtime, legacy replay and rejection fixtures perform no model or semantic-tool execution.");
            Check(engine.Status().Error is null, "Expected budget and admission failures leave the engine healthy.");
            Console.WriteLine($"Coordination budget checks passed: {checks} (actual engine/admission/runtime/owned SQLite; deterministic workflow effects; no Worker process, network, model or semantic tools).");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("cerebral-coordination-budget-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe coordination budget fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private static JsonElement Input(int completeAfter) => CerebralJson.Element(new { completeAfter });

    private static JsonElement Binding(string definitionId, int completeAfter) => CerebralJson.Element(new
    {
        schemaVersion = 1,
        definitionId,
        definitionVersion = FixtureVersion,
        input = Input(completeAfter)
    });

    private sealed record Invocation(WorkflowRun Parent, WorkflowCommandContext Context,
        WorkerWorkflowEffects.AgentAssignment[] Assignments);

    private sealed class FixtureModule : IWorkflowModule
    {
        public string Id { get; }
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; }
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Command];
        public ProbeCommand Command { get; }

        public FixtureModule(string moduleId, string definitionId, string commandId,
            bool includeRecursiveDefinitions = false)
        {
            Id = moduleId;
            Command = new ProbeCommand(commandId);
            var definitions = new List<IWorkflowDefinition> { new ProbeDefinition(definitionId, commandId) };
            if (includeRecursiveDefinitions)
            {
                definitions.Add(new ProbeDefinition(moduleId + ":recursive-delegate", commandId, "agents.delegate"));
                definitions.Add(new ProbeDefinition(moduleId + ":recursive-collaborate", commandId, "agents.collaborate"));
            }
            Workflows = definitions;
        }
    }

    private sealed class ProbeDefinition(string definitionId, string commandId, string? requiredCapability = null)
        : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(definitionId, FixtureVersion,
            "Deterministic transition-budget fixture",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { completeAfter = new { type = "integer", minimum = 0, maximum = WorkflowContract.MaximumSteps } },
                required = new[] { "completeAfter" }, additionalProperties = false
            }),
            requiredCapability is null ? [] : [requiredCapability]);

        public WorkflowStep Advance(WorkflowRun snapshot) =>
            snapshot.Steps >= snapshot.Submission.Input.GetProperty("completeAfter").GetInt32()
                ? new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new { text = "BUDGET_FIXTURE_COMPLETE" })))
                : new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(commandId, 1, WorkflowContract.EmptyObject));
    }

    private sealed class ProbeCommand(string commandId) : IWorkflowCommandHandler
    {
        public int Executions;
        public WorkflowCommandDescriptor Descriptor { get; } = new(commandId, 1,
            "Deterministic fixture receipt; no semantic side effects.",
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            [], WorkflowReplayPolicy.Idempotent);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Executions++;
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
        }

        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            throw new InvalidOperationException("Budget fixture effects must not require reconciliation.");
    }

    private sealed class NeverModels : IModelRuntimeAdapter
    {
        public int Calls;
        public ModelAdapterDescriptor Descriptor { get; } = new("coordination-budget-fixture", "1.0.0", ["text"]);

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("No model execution is permitted by the coordination budget fixture.");
        }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("No semantic tool execution is permitted by the coordination budget fixture.");
        }
    }
}
