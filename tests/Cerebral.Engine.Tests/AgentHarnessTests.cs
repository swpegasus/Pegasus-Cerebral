using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

namespace Cerebral.Engine.Tests;

/// <summary>Custom policy with deterministic model/tool fixtures; no actual inference or filesystem tool execution.</summary>
public static class AgentHarnessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);

    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            count++; Console.WriteLine("PASS: " + message);
        }
        static bool Rejected<T>(Action action) where T : Exception
        {
            try { action(); return false; } catch (T) { return true; }
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-harness-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            var harness = new FixtureHarness();
            var capabilities = new List<string> { "diagnostics" };
            using (var descriptorDocument = JsonDocument.Parse(harness.Descriptor.InputSchema.GetRawText()))
            {
                harness.Descriptor = harness.Descriptor with { InputSchema = descriptorDocument.RootElement, RequiredCapabilities = capabilities };
                IAgentHarnessModule export = new HarnessExports([harness]);
                var definition = new AgentWorkflowDefinition(export.Harnesses.Single());
                capabilities.Clear();
                harness.Descriptor = harness.Descriptor with { Id = "fixture.harness:changed", Version = "2.0.0" };
                descriptorDocument.Dispose();
                Check(definition.Descriptor.Id == "fixture.harness:program" && definition.Descriptor.Version == "1.0.0" &&
                    definition.Descriptor.RequiredCapabilities.SequenceEqual(["diagnostics"]) && definition.Descriptor.InputSchema.GetProperty("type").GetString() == "object",
                    "harness adapter freezes the admitted descriptor including schema and capability collections");

                var generate = new FixtureHandler("cerebral.host:generate", context =>
                {
                    var persisted = store.Get(context.Submission.RequestId);
                    Check(persisted.Effect is { State: WorkflowEffectState.Dispatched } effect && effect.Id == context.EffectId &&
                        persisted.State.GetProperty("phase").GetString() == "model",
                        "model fixture begins only after its effect identity and continuation are durable");
                    harness.DisposeIssuedState();
                    Check(store.Get(persisted.Id).State.GetProperty("phase").GetString() == "model",
                        "stored continuation survives disposal of harness-owned JSON storage");
                    return new(WorkflowEffectState.Succeeded, Data(new { finish = "stop", text = "Read the fixture source." }), ProviderReceiptId: "fixture-model");
                });
                var tool = new FixtureHandler("cerebral.host:tool", context =>
                {
                    Check(context.Arguments.GetProperty("name").GetString() == "file.read" &&
                        store.Get(context.Submission.RequestId).Effect?.State == WorkflowEffectState.Dispatched,
                        "custom harness requests a semantic tool through the same durable effect path");
                    harness.DisposeIssuedState();
                    return new(WorkflowEffectState.Succeeded, Data(new { text = "fixture source text" }), ProviderReceiptId: "fixture-tool");
                });
                var registry = new WorkflowRegistry([new HarnessModule(definition), new HostModule([generate, tool])]);
                var runtime = new WorkflowRuntime(store, registry, (_, required) => required.All(x => x == "diagnostics"));

                var before = harness.Advances;
                var invalid = Running("invalid") with { Submission = Submission("invalid") with { Input = Data(new { ask = false }) } };
                Check(Rejected<ArgumentException>(() => definition.Advance(invalid)) && harness.Advances == before,
                    "invalid harness input is rejected before policy evaluation");
                var wrongVersion = Running("wrong-version") with { Submission = Submission("wrong-version") with { DefinitionVersion = "2.0.0" } };
                Check(Rejected<WorkflowConflictException>(() => definition.Advance(wrongVersion)) && harness.Advances == before,
                    "an accepted run cannot advance through a different harness version");

                var submitted = store.Submit(Submission("direct"));
                var completed = await runtime.RunAsync(submitted.Id);
                Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("text").GetString() == "fixture source text" &&
                    generate.Executions == 1 && tool.Executions == 1 && harness.Advances == before + 3,
                    "custom harness advances model and tool effects then completes from the actual fixture result");
                Check(harness.Snapshots.Any(x => x.Submission.AssignmentText == "Private agent assignment." && x.RemainingSteps > 0 &&
                    x.Effect?.ProviderReceiptId == "fixture-model"),
                    "harness receives private assignment, remaining steps and prior provider outcome without host handles");

                var questions = store.Submit(Submission("questions", ask: true));
                var waiting = await runtime.RunAsync(questions.Id);
                var beforeAnswer = harness.Advances;
                Check(waiting.Status == WorkflowStatus.Waiting && waiting.Questions?.Fields.Count == 2 &&
                    (await runtime.RunAsync(waiting.Id)).Revision == waiting.Revision && harness.Advances == beforeAnswer,
                    "pending harness questions remain durable and do not repeatedly invoke policy");
                var answer = new Dictionary<string, string> { ["label"] = "Accepted label", ["style"] = "concise" };
                store.Answer(new("answer", waiting.Id, waiting.Questions!.Id, waiting.Questions.Revision, answer));
                answer["label"] = "Changed after admission";
                var reopened = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
                var resumed = await new WorkflowRuntime(reopened, registry, (_, _) => true).RunAsync(waiting.Id);
                Check(resumed.Status == WorkflowStatus.Completed && harness.AnswerLabel == "Accepted label" && generate.Executions == 2 && tool.Executions == 2,
                    "reopened workflow continues a harness from committed field answers without a draft handoff");

                var cancelled = store.Submit(Submission("cancelled"));
                store.Cancel(cancelled.Id);
                before = harness.Advances;
                Check((await runtime.RunAsync(cancelled.Id)).Status == WorkflowStatus.Cancelled && harness.Advances == before && generate.Executions == 2,
                    "cancelled work never invokes the harness or a model fixture");
                Check(Rejected<WorkflowConflictException>(() => definition.Advance(Running("direct-cancel") with { CancelRequested = true })) && harness.Advances == before,
                    "direct adapter evaluation also rejects a cancellation already present in its snapshot");

                var oversized = new AgentWorkflowDefinition(new OversizedHarness());
                var badRegistry = new WorkflowRegistry([new HarnessModule(oversized)]);
                store.Submit(Submission("oversized"));
                var failed = await new WorkflowRuntime(store, badRegistry, (_, _) => true).RunAsync("oversized");
                Check(failed.Status == WorkflowStatus.Failed && failed.Steps == 0 && failed.Effect is null,
                    "oversized harness continuation fails before any effect is admitted");
            }

            using var document = JsonDocument.Parse("{\"value\":\"original\"}");
            var choices = new List<string> { "one", "two" };
            var answers = new Dictionary<string, string> { ["choice"] = "one" };
            var fields = new List<WorkflowQuestion> { new("choice", "Choice", Choices: choices) };
            var limits = new Dictionary<string, int> { ["cerebral.host:launch"] = 2 };
            var failures = new Dictionary<string, int> { ["cerebral.host:launch"] = 1 };
            var original = Running("snapshot") with
            {
                Submission = Submission("snapshot") with { ActiveTimeLimitSeconds = 90, FailureLimits = limits, StepLimit = 100, PolicyStepLimit = 12 },
                ActiveMilliseconds = 2500, FailedEffects = failures, Steps = 3,
                State = document.RootElement,
                Questions = new("set", 1, fields, answers),
                Effect = new("effect", "cerebral.host:tool", 1, document.RootElement, WorkflowReplayPolicy.Never, WorkflowEffectState.Succeeded, document.RootElement),
                Signal = new(WorkflowSignalKind.EffectResult, document.RootElement), Result = document.RootElement
            };
            var snapshot = new AgentHarnessSnapshot(original);
            choices[0] = "changed"; answers["choice"] = "changed"; fields.Clear(); document.Dispose();
            limits["cerebral.host:launch"] = 100; failures["cerebral.host:launch"] = 0;
            Check(snapshot.State.GetProperty("value").GetString() == "original" && snapshot.Questions!.Fields[0].Choices![0] == "one" &&
                snapshot.Questions.Answers["choice"] == "one" && snapshot.Effect!.Arguments.GetProperty("value").GetString() == "original" &&
                snapshot.Signal.Data.GetProperty("value").GetString() == "original" && snapshot.Run.Result!.Value.GetProperty("value").GetString() == "original",
                "harness snapshots detach nested question collections and JSON lifetimes");
            Check(Rejected<NotSupportedException>(() => ((IDictionary<string, string>)snapshot.Questions!.Answers)["choice"] = "write"),
                "harness question answers are exposed as a read-only collection");
            Check(snapshot.ActiveMilliseconds == 2500 && snapshot.RemainingActiveMilliseconds == 87_500 &&
                snapshot.FailureLimits["cerebral.host:launch"] == 2 && snapshot.FailedEffects["cerebral.host:launch"] == 1 &&
                Rejected<NotSupportedException>(() => ((IDictionary<string, int>)snapshot.FailedEffects)["cerebral.host:launch"] = 0),
                "host-owned active-time and failure budgets are detached read-only snapshot values");
            Check(snapshot.Submission.StepLimit == 100 && snapshot.EffectiveStepLimit == 12 && snapshot.RemainingSteps == 9,
                "harness uses the captured policy step cap while preserving the originally requested limit");
            Console.WriteLine($"Agent harness: {count} deterministic fixture / local SQLite assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WorkflowSubmission Submission(string id, bool ask = false) => new(id, "chat-" + id, "fixture-profile",
        "fixture.harness:program", "1.0.0", "Read the fixture source.", "Private agent assignment.",
        Data(new { text = "fixture", ask }), Data(new { instructions = "Accepted instructions" }));
    private static WorkflowRun Running(string id) => new() { Id = id, Submission = Submission(id), Revision = 1, Status = WorkflowStatus.Running };
    private sealed record HarnessExports(IReadOnlyList<IAgentHarness> Harnesses) : IAgentHarnessModule;
    private sealed record HarnessModule(IWorkflowDefinition Definition) : IWorkflowModule
    {
        public string Id => "fixture.harness";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }
    private sealed record HostModule(IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule
    {
        public string Id => "cerebral.host";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [];
    }
    private sealed class FixtureHandler(string id, Func<WorkflowCommandContext, WorkflowCommandResult> result) : IWorkflowCommandHandler
    {
        public int Executions { get; private set; }
        public WorkflowCommandDescriptor Descriptor { get; } = new(id, 1, "Deterministic harness test handler.",
            Data(new { type = "object" }), Data(new { type = "object" }), ["diagnostics"]);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Executions++;
            return Task.FromResult(result(context));
        }
    }
    private sealed class FixtureHarness : IAgentHarness
    {
        private JsonDocument? issuedState;
        public int Advances { get; private set; }
        public string? AnswerLabel { get; private set; }
        public List<AgentHarnessSnapshot> Snapshots { get; } = [];
        public AgentHarnessDescriptor Descriptor { get; set; } = HarnessDescriptor();
        public void DisposeIssuedState() { issuedState?.Dispose(); issuedState = null; }
        public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
        {
            Advances++; Snapshots.Add(snapshot);
            var phase = snapshot.State.TryGetProperty("phase", out var stored) ? stored.GetString() : null;
            if (phase is null && snapshot.Submission.Input.GetProperty("ask").GetBoolean())
                return new(Data(new { phase = "questions" }), new AskWorkflowQuestions([new("label", "Label"), new("style", "Style", Choices: ["concise", "detailed"])]));
            if (phase == "questions")
            {
                if (snapshot.Signal.Kind != WorkflowSignalKind.Answers) throw new InvalidOperationException("Expected committed answers.");
                AnswerLabel = snapshot.Signal.Data.GetProperty("label").GetString();
            }
            if (phase is null or "questions")
                return Effect("model", "cerebral.host:generate", Data(new { prompt = snapshot.Submission.UserText }));
            var result = snapshot.Signal.Data.Deserialize<WorkflowCommandResult>(Json) ?? throw new InvalidOperationException("Missing effect result.");
            if (result.State != WorkflowEffectState.Succeeded)
                return new(snapshot.State, new CompleteWorkflow(Data(new { }), Success: false, Error: result.Error ?? "Effect failed."));
            return phase == "model"
                ? Effect("tool", "cerebral.host:tool", Data(new { name = "file.read", arguments = new { path = "fixture.txt" } }))
                : new(snapshot.State, new CompleteWorkflow(result.Data!.Value));
        }
        private WorkflowStep Effect(string phase, string command, JsonElement arguments)
        {
            DisposeIssuedState(); issuedState = JsonDocument.Parse(Data(new { phase }).GetRawText());
            return new(issuedState.RootElement, new InvokeWorkflowCommand(command, 1, arguments));
        }
    }
    private sealed class OversizedHarness : IAgentHarness
    {
        public AgentHarnessDescriptor Descriptor { get; } = HarnessDescriptor();
        public WorkflowStep Advance(AgentHarnessSnapshot snapshot) => new(Data(new { text = new string('x', WorkflowContract.MaximumStateBytes) }),
            new CompleteWorkflow(Data(new { })));
    }
    private static AgentHarnessDescriptor HarnessDescriptor() => new("fixture.harness:program", "1.0.0", "Custom deterministic harness fixture.",
        Data(new
        {
            type = "object", properties = new { text = new { type = "string" }, ask = new { type = "boolean" } },
            required = new[] { "text", "ask" }, additionalProperties = false
        }), ["diagnostics"]);
}
