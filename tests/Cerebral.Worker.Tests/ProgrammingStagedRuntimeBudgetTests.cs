using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Programming;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingStagedWorkflowPolicyTests
{
    // Real WorkflowRuntime/Store and local SQLite; every model/file/process handler is deterministic.
    public static async Task<int> RunRuntimeReadReuseChecksAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Read-reuse runtime fixture: " + message);
        }
        var parent = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\build\Programming\TaskStages\artifacts\ReadReuseRuntimeFixtures");
        var scratch = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        if (!scratch.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new InvalidOperationException("Unexpected runtime fixture directory.");
        Directory.CreateDirectory(scratch);
        try
        {
            var store = new WorkflowStore(new EventJournal(Path.Combine(scratch, "engine.db")));
            var seed = new StageFixture(runtimeBaseline: true, fourFiles: true, stepLimit: 48);
            var runId = "read-reuse-runtime-" + Guid.NewGuid().ToString("N");
            var submission = seed.RuntimeSubmission with { RequestId = runId, ConversationId = runId,
                PolicyStepLimit = 48, ActiveTimeLimitSeconds = 420 };
            var scripted = new ScriptedStageEffects(store, new(seed.Files, StringComparer.Ordinal));
            var definition = new RuntimeStageDefinition();
            var registry = new WorkflowRegistry([
                new RuntimeModule("pegasus.programming", [definition], []),
                new RuntimeModule("cerebral.host", [], [
                    new RuntimeHandler("cerebral.host:generate", scripted.Generate),
                    new RuntimeHandler("cerebral.host:tool", scripted.Tool)])
            ]);
            var runtime = new WorkflowRuntime(store, registry, (_, _) => true);
            store.Submit(submission);
            var completed = await runtime.RunAsync(runId);
            Check(completed.Status == WorkflowStatus.Completed && completed.Error is null,
                "the Programming fixture reaches persisted Completed under the real48-step admission: " + completed.Error);
            Check(completed.Submission.EffectiveStepLimit == 48 && completed.Steps == 46 && scripted.Executions == 45,
                "45 effects plus the completion transition persist46 total steps");
            Check(scripted.ProofReads == 28 && scripted.ModelReads == 1 && scripted.Mutations == 3 && scripted.Processes == 6,
                "all baseline8/failed-cycle8/final-cycle12 source validations and one genuine target refresh execute");
            Check(completed.State.GetProperty("verificationCycles").GetInt32() == 2 &&
                completed.Result!.Value.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
                completed.Result.Value.GetProperty("currentBuild").GetProperty("effectId").GetString(),
                "only the fresh final check tied to its actual build establishes success");
            Check(scripted.NoopWasNotDispatched && scripted.CachedReadWasNotDispatched &&
                scripted.FailedCheckReachedNextModel && scripted.ImmutableInputsUnchanged,
                "cached/no-op choices add no tool effects; failed semantics reach repair; immutable inputs stay exact");
            var saved = store.Get(runId);
            var replay = await runtime.RunAsync(runId);
            Check(replay.Revision == saved.Revision && scripted.Executions == 45,
                "reopening the completed private fixture replays no handler");

            var boundary = new ReplyBoundaryDefinition();
            var boundaryExecutions = 0;
            var boundaryRegistry = new WorkflowRegistry([
                new RuntimeModule("fixture.budget", [boundary], []),
                new RuntimeModule("cerebral.host", [], [new RuntimeHandler("cerebral.host:generate", _ =>
                {
                    boundaryExecutions++;
                    return new(WorkflowEffectState.Succeeded, Data(new { generation = new { text = "{\"action\":\"done\"}" }, marker = "saved-only" }));
                })])
            ]);
            var boundaryRuntime = new WorkflowRuntime(store, boundaryRegistry, (_, _) => true);
            var boundaryId = "reply-boundary-" + Guid.NewGuid().ToString("N");
            store.Submit(new(boundaryId, boundaryId, "fixture", boundary.Descriptor.Id, boundary.Descriptor.Version,
                "Observe the exact step boundary.", null, Data(new { }), Data(new { }), 48)
                { PolicyStepLimit = 48, ActiveTimeLimitSeconds = 420 });
            var exhausted = await boundaryRuntime.RunAsync(boundaryId);
            Check(exhausted.Status == WorkflowStatus.Failed && exhausted.Steps == 48 && boundaryExecutions == 48 &&
                exhausted.Error == "Workflow step budget is exhausted. Start a new authorized run with an appropriate budget.",
                "the real runtime stops at exactly48 persisted effects before another Advance");
            Check(exhausted.State.GetProperty("consumed").GetInt32() == 47 &&
                exhausted.Effect is { State: WorkflowEffectState.Succeeded } effect &&
                store.GetEffectResult(boundaryId, effect.Id).Data!.Value.GetProperty("marker").GetString() == "saved-only",
                "the48th generation reply is saved but unconsumed, matching the observed boundary");
            Console.WriteLine(JsonSerializer.Serialize(new { evidenceTier = "real WorkflowRuntime/Store with local SQLite and deterministic handlers",
                stepLimit = 48, completed = completed.Status.ToString(), persistedSteps = completed.Steps, effects = scripted.Executions,
                proofReads = scripted.ProofReads, finalReplyConsumed = true, boundarySteps = exhausted.Steps,
                boundaryRepliesConsumed = exhausted.State.GetProperty("consumed").GetInt32(),
                liveModelCalls = 0, processLaunches = 0, appCalls = 0 }));
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(scratch).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime fixture cleanup escaped its exact parent.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static JsonElement Data<T>(T value) => JsonSerializer.SerializeToElement(value, Cerebral.Core.CerebralJson.Options);
    private static JsonElement RuntimeSchema => Data(new { type = "object" });
    private sealed record RuntimeModule(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private sealed class RuntimeHandler(string id, Func<WorkflowCommandContext, WorkflowCommandResult> execute) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(id, 1, "Deterministic private unit handler.",
            RuntimeSchema, RuntimeSchema, [], WorkflowReplayPolicy.Never);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(context));
        }
    }
    private sealed class RuntimeStageDefinition : IWorkflowDefinition
    {
        private readonly IAgentHarness harness = new ProgrammingServicePlugin().Harnesses.Single(
            candidate => candidate.Descriptor.Id == ProgrammingAgentHarness.StagedId);
        public WorkflowDefinitionDescriptor Descriptor
        {
            get
            {
                var d = harness.Descriptor;
                return new(d.Id, d.Version, d.Description, d.InputSchema, d.RequiredCapabilities) { SupportsSteering = true };
            }
        }
        public WorkflowStep Advance(WorkflowRun run) => harness.Advance(new(run));
    }
    private sealed class ReplyBoundaryDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.budget:reply-boundary", "1.0.0",
            "Exact runtime step-boundary fixture.", RuntimeSchema, []);
        public WorkflowStep Advance(WorkflowRun run)
        {
            var consumed = run.State.TryGetProperty("consumed", out var field) ? field.GetInt32() : 0;
            if (run.Signal.Kind == WorkflowSignalKind.EffectResult) consumed++;
            return new(Data(new { consumed }), consumed == 48 ? new CompleteWorkflow(Data(new { consumed })) :
                new InvokeWorkflowCommand("cerebral.host:generate", 1, Data(new { fixture = true })));
        }
    }
    private sealed class ScriptedStageEffects(WorkflowStore store, Dictionary<string, string> files)
    {
        private readonly Dictionary<string, string> immutable = files.Where(p => p.Key is "Checks.cs" or "App.csproj").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        private int models, checks;
        public int Executions { get; private set; }
        public int ProofReads { get; private set; }
        public int ModelReads { get; private set; }
        public int Mutations { get; private set; }
        public int Processes { get; private set; }
        public bool NoopWasNotDispatched { get; private set; }
        public bool CachedReadWasNotDispatched { get; private set; }
        public bool FailedCheckReachedNextModel { get; private set; }
        public bool ImmutableInputsUnchanged => immutable.All(p => files[p.Key] == p.Value);
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        public WorkflowCommandResult Generate(WorkflowCommandContext context)
        {
            Executions++; models++;
            var run = store.Get(context.RunId!);
            var root = run.Submission.Input.GetProperty("rootPath").GetString()!;
            object Edit(string name, string text) => new { action = "tool", name = "file.edit",
                arguments = new { path = name, oldText = files[name], newText = text, expectedSha256 = Hash(files[name]) } };
            object Read(string name) => new { action = "tool", name = "file.read", arguments = new { path = name, lineCount = 100 } };
            if (models == 3) CachedReadWasNotDispatched = ModelReads == 0;
            if (models == 6)
            {
                NoopWasNotDispatched = Mutations == 2;
                FailedCheckReachedNextModel = context.Arguments.GetProperty("context").GetRawText().Contains("FAIL fixture reject", StringComparison.Ordinal);
            }
            object action = models switch
            {
                1 => Edit("Program.cs", "class Implementation { public const int Value = 1; }\n"),
                2 => Read("Helper.cs"),
                3 => Edit("Helper.cs", "class Helper { public const int Value = 1; }\n"),
                4 => Read("Helper.cs"),
                5 => Edit("Helper.cs", files["Helper.cs"]),
                6 => Edit("Program.cs", "class Implementation { public const int Value = 2; }\n"),
                7 => new { action = "done", summary = "Ready for configured verification." },
                _ => throw new InvalidOperationException("Unexpected fixture model turn.")
            };
            var window = context.Arguments.GetProperty("context").Deserialize<ContextWindow>(Cerebral.Core.CerebralJson.Options)!;
            var prepared = new PreparedModelMetadata("fixture-instance", "fixture-adapter", "1", "fixture-model", 128, 32768, 1024, "unit")
                { SafetyReserveTokens = 128 };
            var projection = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                window.Contributions.Select(s => new ContextSourceReference(s.Id, s.SourceRevision)).ToArray(), [], prepared);
            return new(WorkflowEffectState.Succeeded, Data(new
            {
                generation = new ModelGenerationResult(ModelGenerationFinish.Stop, JsonSerializer.Serialize(action, Cerebral.Core.CerebralJson.Options)),
                outputOmitted = false, context = projection
            }));
        }
        public WorkflowCommandResult Tool(WorkflowCommandContext context)
        {
            Executions++;
            var run = store.Get(context.RunId!);
            var name = context.Arguments.GetProperty("name").GetString()!;
            var args = context.Arguments.GetProperty("arguments");
            object? evidence = null; string content; var success = true;
            if (name == "file.read")
            {
                if (run.State.GetProperty("stages").GetProperty("pending").ValueKind == JsonValueKind.String) ProofReads++; else ModelReads++;
                var path = args.GetProperty("path").GetString()!; var text = files[Path.GetFileName(path)];
                var first = args.TryGetProperty("startLine", out var start) ? start.GetInt32() : 1;
                var count = args.TryGetProperty("lineCount", out var length) ? length.GetInt32() : 200;
                var lines = text.Split('\n');
                content = JsonSerializer.Serialize(new { path, sha256 = Hash(text), startLine = first,
                    totalLines = lines.Length, content = string.Join('\n', lines.Skip(first - 1).Take(count)) }, Cerebral.Core.CerebralJson.Options);
            }
            else if (name == "file.edit")
            {
                Mutations++;
                var path = args.GetProperty("path").GetString()!; var file = Path.GetFileName(path);
                if (args.GetProperty("expectedSha256").GetString() != Hash(files[file]) || args.GetProperty("oldText").GetString() != files[file])
                    throw new InvalidOperationException("Fixture mutation preimage mismatch.");
                files[file] = args.GetProperty("newText").GetString()!;
                content = JsonSerializer.Serialize(new { path, sha256 = Hash(files[file]), bytes = Encoding.UTF8.GetByteCount(files[file]), skipped = false });
                evidence = new Cerebral.Core.ToolExecutionEvidence(Cerebral.Core.ToolExecutionOutcome.Succeeded, "fixture.mutation", context.EffectId, Partial: false);
            }
            else if (name == "process.run")
            {
                Processes++;
                var stage = run.State.GetProperty("stages").GetProperty("pending").GetString();
                var isBuild = stage is "baseline-build" or "build";
                var stderr = ""; var stdout = "Build succeeded";
                if (!isBuild) { checks++; success = checks == 3; stdout = success ? "PASS\n" : ""; stderr = checks == 1 ? "EXPECTED FAILURE\n" : success ? "" : "FAIL fixture reject\n"; }
                var exit = success ? 0 : 1;
                var expected = args.TryGetProperty("expectedExitCode", out var value) ? value.GetInt32() : 0;
                success = exit == expected;
                content = JsonSerializer.Serialize(new { exitCode = exit, stdout, stderr });
                evidence = new Cerebral.Core.ToolExecutionEvidence(success ? Cerebral.Core.ToolExecutionOutcome.Succeeded : Cerebral.Core.ToolExecutionOutcome.Failed,
                    "cerebral.process", context.EffectId, State: "completed", Partial: false, ExitCode: exit);
            }
            else throw new InvalidOperationException("Unexpected fixture tool.");
            return new(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
                Data(new { name, content, truncated = false, evidence, followUpRequired = false }),
                success ? null : "The tool returned failed. Inspect its retained result and evidence.");
        }
    }
}
