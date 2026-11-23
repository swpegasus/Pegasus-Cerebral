using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Loader;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Programming;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ProgrammingExpectedOutcomeRuntimeTests
{
    private static JsonElement Data<T>(T value) => CerebralJson.Element(value);
    private static JsonElement Schema => Data(new { type = "object" });
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Actual Worker tool handler, dispatcher, child processes, Runtime/Store and SQLite.
    // Only model choices are deterministic; no model runtime or application host is started.
    public static async Task<int> RunAsync(string evidenceDirectory, string programmingPackage)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Expected outcome runtime fixture: " + message);
        }
        var parent = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\build\Programming\TaskStages\artifacts\OutcomeRuntimeFixtures");
        var scratch = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        if (!scratch.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new InvalidOperationException("Unexpected private fixture directory.");
        var evidenceParent = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\build\Programming\TaskStages\OutcomeContractEvidence");
        evidenceDirectory = Path.GetFullPath(evidenceDirectory);
        if (!evidenceDirectory.StartsWith(evidenceParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(evidenceDirectory))
            throw new InvalidOperationException("Evidence requires a fresh child of the private outcome evidence directory.");
        Directory.CreateDirectory(scratch);
        var project = Path.Combine(scratch, "project"); Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(project, "packages"));
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Program.cs"] = "public static class Implementation { public static int Value => 0; }\n",
            ["Helper.cs"] = "public static class Helper { public static int Value => 0; }\n",
            ["Checks.cs"] = """
                using System;
                public static class Checks {
                    public static int Main(string[] args) {
                        if (args.Length == 1 && args[0] == "--always-fail") { Console.Error.Write("PROBE FAILURE\n"); return 1; }
                        if (Implementation.Value == 0 && Helper.Value == 0) { Console.Error.Write("EXPECTED FAILURE\n"); return 1; }
                        if (Implementation.Value < 2 || Helper.Value < 1) { Console.Error.Write("FAIL fixture reject\n"); return 1; }
                        Console.Out.Write("PASS\n"); return 0;
                    }
                }
                """ + "\n",
            ["App.csproj"] = """
                <Project>
                  <PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><UseAppHost>false</UseAppHost>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><UseSharedCompilation>false</UseSharedCompilation>
                    <RestoreSources>$(MSBuildThisFileDirectory)packages</RestoreSources><NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Program.cs" /><Compile Include="Helper.cs" /><Compile Include="Checks.cs" /></ItemGroup>
                  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
                </Project>
                """ + "\n"
        };
        foreach (var (name, text) in files) await File.WriteAllTextAsync(Path.Combine(project, name), text, new UTF8Encoding(false));
        try
        {
            var state = new StateStore(Path.Combine(scratch, "worker"));
            var config = new Configuration(state);
            var settings = config.Get();
            settings.McpServers = []; settings.CortexNamespaces = []; settings.CortexWriteNamespaces = [];
            settings.ModelDirectory = Path.Combine(scratch, "models"); settings.ProjectsDirectory = project;
            settings.SharedDirectory = Path.Combine(scratch, "shared");
            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet)) throw new FileNotFoundException("Private fixture requires the installed dotnet host.", dotnet);
            settings.Executables = [new() { Id = "dotnet", Path = dotnet }];
            config.Save(settings);
            var profile = new AgentProfile { Id = "expected-outcome-fixture", Name = "Expected outcome fixture", Role = "lead",
                WorkerId = "local", ModelWorkerId = "local", ModelId = "unused", FollowsModelBudgets = false, AutomaticCompaction = false,
                MaxSteps = 48, MaxSeconds = 420, AllowedRoots = [project],
                AllowedTools = ["file.read", "file.edit", "process.run"], Capabilities = ["filesystem.read", "filesystem.write", "process.execute"] };
            state.Put("agent", profile.Id, profile);
            await using var dispatcher = new ToolDispatcher(config.Get, stateDirectory: Path.Combine(scratch, "dispatcher"));
            var recording = new RecordingTools(dispatcher);
            var descriptors = (await recording.ListAsync(profile, default)).Where(t => profile.AllowedTools.Contains(t.Name)).ToArray();
            var processDescriptor = descriptors.Single(t => t.Name == "process.run");
            Check(processDescriptor.InputSchema.GetProperty("properties").TryGetProperty("expectedExitCode", out var expectedSchema) &&
                expectedSchema.GetProperty("type").GetString() == "integer" && expectedSchema.GetProperty("minimum").GetInt32() == 0 &&
                expectedSchema.GetProperty("maximum").GetInt32() == 255, "candidate exposes the selected bounded generic contract");
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
                CerebralJson.Read<AgentProfile>(Data(profile)), "", "", null, descriptors, profile.Capabilities.ToArray(),
                profile.AllowedTools.ToArray(), profile.AllowedRoots.ToArray(), false,
                WorkerExecutionSnapshot.RoutingFingerprint(config.Get()), [], []);
            var journal = new EventJournal(Path.Combine(state.DirectoryPath, "engine.db"));
            var store = new WorkflowStore(journal);
            var host = new WorkerWorkflowEffects(state, config, new NoModels(), recording);
            var actualTool = host.Commands.Single(c => c.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
            var script = new Choices(store, project);
            var packageAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(programmingPackage);
            using var plugin = (IServicePlugin)Activator.CreateInstance(packageAssembly.GetType("Cerebral.Programming.ProgrammingServicePlugin", throwOnError: true)!)!;
            var packagedHarness = ((IAgentHarnessModule)plugin).Harnesses.Single(h => h.Descriptor.Id == ProgrammingAgentHarness.StagedId);
            Check(packagedHarness.GetType().Assembly.Location == programmingPackage, "the actual candidate package supplies the exercised harness without plugin initialization");
            var definition = new StagedDefinition(packagedHarness);
            var registry = new WorkflowRegistry([
                new Module("pegasus.programming", [definition], []),
                new Module(WorkerWorkflowEffects.ModuleId, [], [actualTool, new Generate(script.Generate)])
            ]);
            var runtime = new WorkflowRuntime(store, registry, (_, _) => true);
            var id = "expected-outcome-" + Guid.NewGuid().ToString("N");
            var input = Data(new {
                rootPath = project, projectFile = "App.csproj", configuration = "Release",
                requiredInputs = files.Select(p => new { path = p.Key, sha256 = Hash(p.Value), mutable = p.Key is "Program.cs" or "Helper.cs" }).ToArray(),
                build = new { arguments = new[] { "build", "App.csproj", "-c", "Release", "--nologo", "-v", "minimal", "--disable-build-servers", "-m:1" }, timeoutSeconds = 120 },
                baseline = new { build = new { exitCode = 0 }, check = new { exitCode = 1, stdout = "", stderr = "EXPECTED FAILURE\n" } },
                check = new { assemblyPath = "bin/Release/net10.0/App.dll", timeoutSeconds = 30, stdout = "PASS\n", stderr = "" },
                requireSourceChange = true
            });
            var submission = new WorkflowSubmission(id, id, profile.Id, definition.Descriptor.Id, definition.Descriptor.Version,
                "Repair only the two declared mutable fixture files and establish exact current checks.", null, input, Data(snapshot), 48)
                { PolicyStepLimit = 48, ActiveTimeLimitSeconds = 420, FailureLimits = new Dictionary<string, int> { [WorkerWorkflowEffects.LaunchFailureBudget] = 2 } };
            store.Submit(submission);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var completed = await runtime.RunAsync(id, deadline.Token);
            Check(completed.Status == WorkflowStatus.Completed && completed.Error is null,
                "actual Worker/dispatcher fixture completes: " + completed.Error + " " + completed.WaitingReason);
            Check(completed.Steps == 46 && completed.Submission.EffectiveStepLimit == 48, "45 effects plus completion fit the unchanged48-step admission");
            Check(completed.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget) == 1 &&
                script.BaselineFailures == 0 && script.IntermediateFailures == 1, "accepted baseline costs0; unexpected intermediate costs1; final success resets nothing");
            Check(script.Models == 7 && recording.ProcessCalls == 6 && recording.FileReads == 29 && recording.Mutations == 3,
                "all fresh policy source validations and actual mutations/checks executed");
            var final = completed.Result!.Value;
            Check(final.GetProperty("baselineCheck").GetProperty("exitCode").GetInt32() == 1 &&
                final.GetProperty("currentCheck").GetProperty("exitCode").GetInt32() == 0 &&
                final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
                final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
                "raw baseline exit1 and final exit0 retain exact current build joins");
            Check(await File.ReadAllTextAsync(Path.Combine(project, "Checks.cs")) == files["Checks.cs"] &&
                await File.ReadAllTextAsync(Path.Combine(project, "App.csproj")) == files["App.csproj"],
                "immutable source and project remain exact");
            var processReceipts = recording.Receipts.ToArray();
            Check(processReceipts.Count(r => r.Arguments.TryGetProperty("expectedExitCode", out _)) == 1 &&
                processReceipts.Single(r => r.Arguments.TryGetProperty("expectedExitCode", out _)).Evidence is { ExitCode: 1, Outcome: ToolExecutionOutcome.Succeeded },
                "only the baseline check opts into accepted nonzero outcome");
            var persistedResults = new List<object>();
            var persistedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var receipt in processReceipts)
            {
                var saved = store.GetEffectResult(id, receipt.EffectId);
                var rawData = saved.Data!.Value.GetRawText();
                persistedHashes[receipt.EffectId] = Hash(rawData);
                persistedResults.Add(new { runId = id, receipt.EffectId, state = saved.State.ToString(), rawData, sha256 = Hash(rawData), saved.Error, saved.ProviderReceiptId });
                var data = saved.Data!.Value.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options)!;
                Check(data.Content == receipt.Content && !data.Truncated && !data.FollowUpRequired &&
                    data.Evidence is { State: "completed", ProviderId: "cerebral.process", ExitCode: not null } &&
                    data.Evidence.ExitCode == JsonDocument.Parse(data.Content).RootElement.GetProperty("exitCode").GetInt32() &&
                    saved.State == (receipt.Outcome == ToolExecutionOutcome.Succeeded ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed),
                    "persisted complete native receipt matches actual child result: " + receipt.EffectId);
                var key = actualTool.GetFailureBudgetKey(new(receipt.EffectId, submission,
                    Data(new { name = "process.run", arguments = receipt.Arguments, workingDirectory = project })));
                var build = receipt.Arguments.GetProperty("arguments")[0].GetString() == "build";
                Check(key == (build ? null : WorkerWorkflowEffects.LaunchFailureBudget), "actual Worker classifies the original command without an exemption stand-in");
            }

            Check(new[] { "baselineBuild", "baselineCheck", "currentBuild", "currentCheck" }.All(name => {
                var fact = final.GetProperty(name);
                return persistedHashes.GetValueOrDefault(fact.GetProperty("effectId").GetString()!) == fact.GetProperty("resultSha256").GetString();
            }), "retained exact persisted process-result bytes match all baseline/current fact hashes");

            var probe = new BudgetProbe(Path.Combine(project, "bin", "Release", "net10.0", "App.dll"), project);
            var probeRegistry = new WorkflowRegistry([new Module("fixture.outcome", [probe], []), new Module(WorkerWorkflowEffects.ModuleId, [], [actualTool])]);
            var probeRuntime = new WorkflowRuntime(store, probeRegistry, (_, _) => true);
            var probeId = "expected-outcome-cap-" + Guid.NewGuid().ToString("N");
            var probeSubmission = submission with { RequestId = probeId, ConversationId = probeId,
                DefinitionId = probe.Descriptor.Id, DefinitionVersion = probe.Descriptor.Version, Input = Data(new { }), StepLimit = 16, PolicyStepLimit = 16 };
            var beforeProbe = recording.ProcessCalls;
            store.Submit(probeSubmission);
            var held = await probeRuntime.RunAsync(probeId, deadline.Token);
            Check(held.Status == WorkflowStatus.Waiting && held.Effect is { State: WorkflowEffectState.Prepared } &&
                held.WaitingReason?.Contains("failed-attempt limit", StringComparison.Ordinal) == true,
                "third unexpected launch is held before dispatch");
            Check(held.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget) == 2 &&
                recording.ProcessCalls - beforeProbe == 2 && held.State.GetProperty("consumed").GetInt32() == 2,
                "two actual unexpected child failures consume the unchanged cap2 with no third process");
            Check(recording.Receipts.Skip(processReceipts.Length).All(r => !r.Arguments.TryGetProperty("expectedExitCode", out _) &&
                r.Evidence is { ExitCode: 1, Outcome: ToolExecutionOutcome.Failed }),
                "omitted expected exit remains default0 in actual Worker/Tools path");
            foreach (var receipt in recording.Receipts.Skip(processReceipts.Length))
            {
                var saved = store.GetEffectResult(probeId, receipt.EffectId);
                var rawData = saved.Data!.Value.GetRawText();
                persistedResults.Add(new { runId = probeId, receipt.EffectId, state = saved.State.ToString(), rawData, sha256 = Hash(rawData), saved.Error, saved.ProviderReceiptId });
            }
            var assemblyPins = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic &&
                (a.GetName().Name!.StartsWith("Cerebral.", StringComparison.Ordinal) || a.GetName().Name!.StartsWith("SQLitePCLRaw.", StringComparison.Ordinal) ||
                 a.GetName().Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) || a.GetName().Name == "Microsoft.Data.Sqlite"))
                .Select(a => a.Location).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new { path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), bytes = new FileInfo(path).Length }).ToArray();
            Directory.CreateDirectory(evidenceDirectory);
            var evidence = new { evidenceTier = "real Worker tool handler, ToolDispatcher, local child processes, WorkflowRuntime/Store and SQLite; deterministic generation choices",
                scratchRemovedAfterCapture = true, modelCalls = 0, appCalls = 0, settingsScope = "fresh private fixture only",
                completed = new { completed.Id, status = completed.Status.ToString(), completed.Steps, completed.FailedEffects, completed.Result },
                baselineFailures = script.BaselineFailures, intermediateFailures = script.IntermediateFailures,
                held = new { held.Id, status = held.Status.ToString(), held.Steps, held.FailedEffects, held.WaitingReason,
                    effectId = held.Effect?.Id, effectState = held.Effect?.State.ToString(), held.State },
                actualToolHandler = actualTool.GetType().FullName, programmingPackage, processDispatches = recording.ProcessCalls,
                receipts = recording.Receipts, persistedResults, assemblyPins, checks };
            var evidencePath = Path.Combine(evidenceDirectory, "runtime-fixture.json");
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, evidenceTier = evidence.evidenceTier,
                completedSteps = completed.Steps, baselineFailures = script.BaselineFailures, intermediateFailures = script.IntermediateFailures,
                finalFailures = completed.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget),
                capFailures = held.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget),
                processDispatches = recording.ProcessCalls, modelCalls = 0, appCalls = 0, evidencePath, checks }));
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(scratch).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Private fixture cleanup escaped its parent.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows, IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private sealed class StagedDefinition(IAgentHarness harness) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor => new(harness.Descriptor.Id, harness.Descriptor.Version,
            harness.Descriptor.Description, harness.Descriptor.InputSchema, harness.Descriptor.RequiredCapabilities) { SupportsSteering = true };
        public WorkflowStep Advance(WorkflowRun run) => harness.Advance(new(run));
    }
    private sealed class Generate(Func<WorkflowCommandContext, WorkflowCommandResult> execute) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(WorkerWorkflowEffects.GenerateCommand, 1, "Deterministic fixture choices only.", Schema, Schema, []);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(execute(context)); }
    }
    private sealed class NoModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("unused", "1", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("The private fixture must not prepare a model.");
    }
    private sealed class Choices(WorkflowStore store, string root)
    {
        public int Models { get; private set; }
        public int BaselineFailures { get; private set; } = -1;
        public int IntermediateFailures { get; private set; } = -1;
        public WorkflowCommandResult Generate(WorkflowCommandContext context)
        {
            Models++;
            var run = store.Get(context.RunId!);
            if (Models == 1) BaselineFailures = run.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget);
            if (Models == 6)
            {
                IntermediateFailures = run.FailedEffects.GetValueOrDefault(WorkerWorkflowEffects.LaunchFailureBudget);
                if (!context.Arguments.GetProperty("context").GetRawText().Contains("FAIL fixture reject", StringComparison.Ordinal))
                    throw new InvalidOperationException("Actual failed check output did not reach the next deterministic repair choice.");
            }
            object Edit(string name, int value)
            {
                var old = File.ReadAllText(Path.Combine(root, name));
                var text = name == "Program.cs" ? $"public static class Implementation {{ public static int Value => {value}; }}\n" :
                    $"public static class Helper {{ public static int Value => {value}; }}\n";
                return new { action = "tool", name = "file.edit", arguments = new { path = name, oldText = old, newText = text, expectedSha256 = Hash(old) } };
            }
            object Read(string name) => new { action = "tool", name = "file.read", arguments = new { path = name, lineCount = 100 } };
            object action = Models switch {
                1 => Edit("Program.cs", 1), 2 => Read("Helper.cs"), 3 => Edit("Helper.cs", 1), 4 => Read("Helper.cs"),
                5 => Edit("Helper.cs", 1), 6 => Edit("Program.cs", 2), 7 => new { action = "done", summary = "Ready for exact verification." },
                _ => throw new InvalidOperationException("Unexpected private fixture generation.")
            };
            var window = context.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            var prepared = new PreparedModelMetadata("fixture", "fixture", "1", "fixture", 128, 32768, 1024, "deterministic")
                { SafetyReserveTokens = 128 };
            var projection = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                window.Contributions.Select(s => new ContextSourceReference(s.Id, s.SourceRevision)).ToArray(), [], prepared);
            return new(WorkflowEffectState.Succeeded, Data(new {
                generation = new ModelGenerationResult(ModelGenerationFinish.Stop, JsonSerializer.Serialize(action, CerebralJson.Options)),
                outputOmitted = false, context = projection }));
        }
    }
    private sealed class BudgetProbe(string assembly, string root) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.outcome:cap", "1.0.0", "Unchanged launch cap probe.", Schema, []);
        public WorkflowStep Advance(WorkflowRun run)
        {
            var consumed = run.State.TryGetProperty("consumed", out var value) ? value.GetInt32() : 0;
            if (run.Signal.Kind == WorkflowSignalKind.EffectResult) consumed++;
            return new(Data(new { consumed }), new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1,
                Data(new { name = "process.run", workingDirectory = root,
                    arguments = new { executableId = "dotnet", workingDirectory = root, arguments = new[] { assembly, "--always-fail" }, timeoutSeconds = 30 } })));
        }
    }
    private sealed record Receipt(string EffectId, JsonElement Arguments, bool Success, ToolExecutionOutcome Outcome,
        string Content, bool Truncated, ToolExecutionEvidence? Evidence);
    private sealed class RecordingTools(IToolDispatcher inner) : IToolDispatcher
    {
        public List<Receipt> Receipts { get; } = [];
        public int ProcessCalls { get; private set; }
        public int FileReads { get; private set; }
        public int Mutations { get; private set; }
        public bool FullAccessActive => inner.FullAccessActive;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => inner.ListAsync(profile, token);
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            if (call.Name == "process.run") ProcessCalls++;
            if (call.Name == "file.read") FileReads++;
            if (call.Name == "file.edit") Mutations++;
            var result = await inner.ExecuteAsync(call, context, token);
            if (call.Name == "process.run") Receipts.Add(new(context.EffectId!, call.Arguments.Clone(), result.Success,
                result.EffectiveOutcome, result.Content, result.Truncated, result.Evidence));
            return result;
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence, CancellationToken token) =>
            inner.ReconcileAsync(call, context, previousEvidence, token);
    }
}
