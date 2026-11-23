using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Programming;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Opt-in real model/PAO acceptance. Uses a task-owned repository scratch project and database;
/// does not read installed Cerebral credentials or alter installed configuration or services.
/// Launch this test executable directly in the signed-in user session with a hidden process
/// (UseShellExecute=false, CreateNoWindow=true), never as a queued PAO run. The workflow calls
/// PAO mutations through the same serialized FIFO; an outer PAO job would hold that queue while
/// waiting for its own child calls. This is test entrypoint orchestration, not a product fallback.
/// Allow time beyond the execution deadline for cancellation and model/process cleanup.
/// </summary>
public static class ProgrammingWorkflowLiveTests
{
    public const string DefaultModelPath = @"C:\ProgramData\Pegasus\Cerebral\models\qwen35-9b.gguf";
    public const string DefaultServerPath = @"C:\Program Files\Pegasus\Cerebral\Applications\Worker\runtime\llama-server.exe";
    public const string DefaultCortexPath = @"C:\Program Files\Pegasus\Cortex\PegasusCortex.exe";
    public const int ExecutionTimeoutSeconds = 260;
    private const string ExpectedOutput = "Hello, durable Cerebral!";
    private const string ModelId = "programming-live-model";
    private const string ProjectFile = "Hello.csproj";

    public static async Task RunAsync(string modelPath = DefaultModelPath, string serverPath = DefaultServerPath,
        string cortexPath = DefaultCortexPath, CancellationToken cancellationToken = default, bool requireCompaction = false, string? desktopDll = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Cortex acceptance requires Windows.");
        modelPath = RequireFile(modelPath); serverPath = RequireFile(serverPath); cortexPath = RequireFile(cortexPath);
        var dotnet = RequireFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        if (desktopDll is not null) desktopDll = RequireFile(desktopDll);
        var repository = FindRepository();
        var scratchParent = Path.Combine(repository, "tests", ".scratch");
        var ownedName = "programming-workflow-live-" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(scratchParent, ownedName));
        RejectLinksThrough(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The unique acceptance directory already exists.");
        Directory.CreateDirectory(root);
        WorkflowRun? completedForDesktop = null;
        var success = false;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++; Console.WriteLine("PASS: " + message);
        }

        try
        {
            var projectRoot = Path.Combine(root, "project");
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, ProjectFile), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, cancellationToken);
            var brokenSource = "Console.WritLine(\"Hello, durable Cerebral!\");\n";
            if (requireCompaction)
                brokenSource += string.Concat(Enumerable.Range(1, 180).Select(i =>
                    $"// Design note {i}: preserve the console greeting and the existing framework; no packages or extra output belong in this project.\n"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Program.cs"), brokenSource, cancellationToken);
            // This framework-only fixture needs no network package source. Restore still runs normally.
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);

            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store);
            var settings = configuration.Get();
            settings.LlamaServerPath = serverPath;
            settings.ReservedMemoryMb = 1024;
            settings.MaxConcurrentJobs = 1;
            settings.MaxExecutableAttemptsPerAssignment = 2;
            settings.ExternalCommandsEnabled = true;
            settings.BackgroundTasks.Clear();
            if (desktopDll is not null) settings.DefaultModelId = ModelId;
            settings.ModelDirectory = Path.Combine(root, "models");
            settings.ProjectsDirectory = projectRoot;
            settings.SharedDirectory = Path.Combine(root, "shared");
            settings.CortexNamespaces = [];
            settings.CortexWriteNamespaces = [];
            settings.Executables = [new() { Id = "dotnet", Path = dotnet }];
            settings.McpServers = [new()
            {
                Id = "cortex", Enabled = true, ViaUserConnector = false, Executable = cortexPath,
                Arguments = [ConnectorProtocol.StableCortexMcpArgument]
            }];
            configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "programming-live", Name = "Programming live acceptance", Role = desktopDll is null ? "helper" : "lead", ModelId = ModelId,
                WorkerId = "local", ModelWorkerId = "local", FollowsModelBudgets = false,
                ContextTokens = requireCompaction ? 8192 : 16384, ResponseTokens = requireCompaction ? 1536 : 3072, ReasoningMode = "off", Temperature = 0,
                MaxSteps = 96, MaxSeconds = 900,
                Instructions = desktopDll is null
                    ? "Perform the accepted development task through the Programming harness. Follow its JSON action schema, inspect actual tool evidence, and never invent successful execution."
                    : "Follow the current accepted harness action schema. Inspect actual tool evidence and never invent successful execution. Answer ordinary questions directly without unnecessary tools.",
                AllowedRoots = [projectRoot],
                AllowedTools = ["file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run"],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute"]
            };
            Configuration.ValidateProfile(profile);
            store.Put("agent", profile.Id, profile);
            store.Put("model", ModelId, new ModelRecord
            {
                Id = ModelId, Name = "Installed Qwen programming acceptance", Path = modelPath,
                SizeBytes = new FileInfo(modelPath).Length, ContextTokens = requireCompaction ? 8192 : 16384, MaximumContextTokens = 16384,
                RecommendedContextTokens = 16384, AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });

            var events = new EventHub();
            // No application-authorization state directory: ordinary user execution only.
            await using (var tools = new ToolDispatcher(configuration.Get))
            await using (var models = new ModelRuntime(store, configuration.Get, events))
            using (var plugin = new ProgrammingServicePlugin())
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // This fixture owns the execution deadline. Callers may cancel earlier, but should
                // not wrap it in a shorter hard process timeout that would prevent receipt cleanup.
                deadline.CancelAfter(TimeSpan.FromSeconds(ExecutionTimeoutSeconds));
                var harness = plugin.Harnesses.Single(x => x.Descriptor.Id == ProgrammingAgentHarness.Id);
                var module = new HarnessModule(new AgentWorkflowDefinition(harness));
                var host = new WorkerWorkflowEffects(store, configuration, models, tools);
                using var packageManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(repository, "src", "Cerebral.Programming", "service-plugin.json"), deadline.Token));
                ServicePluginPackageBinding[] bindings =
                [
                    new(module.Id, packageManifest.RootElement.GetProperty("version").GetString()!, true, []),
                    new(host.Id, WorkerWorkflowEffects.ModuleVersion, true, [])
                ];
                IWorkflowModule[] modules = desktopDll is null ? [module, host] : [module, host, new ConversationWorkflowModule()];
                if (desktopDll is not null) bindings = [.. bindings, ConversationWorkflowModule.PackageBinding];
                var workflows = new WorkerWorkflows(store, configuration, events, modules, bindings, tools);
                var request = new SubmitWorkflowRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), profile.Id,
                    harness.Descriptor.Id, harness.Descriptor.Version,
                    $"Fix the existing {ProjectFile} console project in {projectRoot}. Read {ProjectFile} and Program.cs first. " +
                    "Run one initial Release build before editing so you can inspect the real compiler failure. " +
                    "Then repair the source, build Release successfully, and run the same project in Release with --no-build. " +
                    $"The application must print exactly '{ExpectedOutput}' on one line. Preserve this text and project framework. " +
                    "Use the existing project and registered dotnet tool. Use --disable-build-servers on build. " +
                    "Do not create a replacement project, download dependencies, ask preference questions, or claim completion without actual receipts.",
                    null, CerebralJson.Element(new
                    {
                        rootPath = projectRoot, projectFile = ProjectFile,
                        selectedTools = profile.AllowedTools, requiredChecks = new[] { "build", "run" }
                    }), StepLimit: 96);
                if (desktopDll is not null)
                {
                    var seed = await workflows.SubmitAsync(new SubmitWorkflowRequest(
                        Guid.NewGuid().ToString("N"), request.ConversationId, profile.Id,
                        ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                        "This conversation is for a small console project. Reply exactly: Ready for the project.",
                        null, CerebralJson.Element(new { workingDirectory = projectRoot }), StepLimit: 12),
                        actor: null, deadline.Token);
                    var initialChat = await workflows.Runtime.RunAsync(seed.Id, deadline.Token);
                    Check(initialChat.Status == WorkflowStatus.Completed && initialChat.Result is not null,
                        "the same conversation starts with a real ordinary Chat turn before its Programming assignment");
                }
                var admitted = await workflows.SubmitAsync(request, actor: null, deadline.Token);
                Check(admitted.Status == WorkflowStatus.Queued && admitted.Submission.SettingsSnapshot.TryGetProperty("resolvedSettingsVersion", out _),
                    "asynchronous admission persists the real frozen model/tool configuration");
                var repeated = await workflows.SubmitAsync(request, actor: null, deadline.Token);
                Check(repeated.Id == admitted.Id && repeated.Revision == admitted.Revision,
                    "repeating the accepted request returns the existing durable workflow");

                var execution = workflows.Runtime.RunAsync(admitted.Id, deadline.Token);
                long shownThrough = 0;
                WorkflowRun finished;
                try
                {
                    while (!execution.IsCompleted)
                    {
                        await Task.WhenAny(execution, Task.Delay(1000, CancellationToken.None));
                        PrintProgress(workflows.Journal, request.ConversationId, ref shownThrough);
                    }
                    finished = await execution;
                }
                finally
                {
                    // If observing the journal fails, drain the exact execution before disposing its providers.
                    if (!execution.IsCompleted)
                    {
                        await deadline.CancelAsync();
                        await execution;
                    }
                }
                PrintProgress(workflows.Journal, request.ConversationId, ref shownThrough);
                Console.WriteLine($"Workflow outcome={finished.Status}; steps={finished.Steps}; activeMs={finished.ActiveMilliseconds}; " +
                    $"reason={Bound(finished.Error ?? finished.WaitingReason ?? "none", 700)}");
                if (finished.Status != WorkflowStatus.Completed)
                {
                    // Only this test's model process and synthetic inputs are present here.
                    foreach (var instance in CerebralJson.Element(models.Diagnostics()).EnumerateArray())
                        foreach (var line in instance.GetProperty("log").EnumerateArray().TakeLast(6))
                            Console.WriteLine("MODEL DIAGNOSTIC: " + Bound(line.GetString() ?? "", 800));
                }
                var evidence = ReadEvidence(workflows.Journal, request.ConversationId);
                Check(finished.Status == WorkflowStatus.Completed,
                    "real model-driven Programming workflow reaches durable Completed state");
                Check(evidence.Any(x => IsRead(x, Path.Combine(projectRoot, "Program.cs"))),
                    "actual source read returns a matching installed PAO receipt");
                Check(evidence.Any(x => IsRead(x, Path.Combine(projectRoot, ProjectFile))),
                    "actual project read returns a matching installed PAO receipt");
                var edits = evidence.Where(x => (x.Tool.Name is "file.edit" or "file.write") &&
                    x.Receipt.State == WorkflowEffectState.Succeeded && IsPao(x) && HasPath(x.Arguments, Path.Combine(projectRoot, "Program.cs"))).ToArray();
                Check(edits.Length > 0 && await File.ReadAllTextAsync(Path.Combine(projectRoot, "Program.cs"), cancellationToken) != brokenSource,
                    "the model repairs the actual source through installed PAO");
                var lastEdit = edits.Max(x => x.Sequence);
                Check(evidence.Any(x => IsDotnet(x, "build", projectRoot) && IsPao(x) &&
                    x.Receipt.State == WorkflowEffectState.Failed && x.Tool.Evidence?.ExitCode is not null and not 0 && x.Sequence < lastEdit),
                    "a genuine failed compiler receipt precedes the repair");
                var builds = evidence.Where(x => IsDotnet(x, "build", projectRoot) && IsPao(x) &&
                    x.Receipt.State == WorkflowEffectState.Succeeded && x.Tool.Evidence?.ExitCode == 0 && x.Sequence > lastEdit).ToArray();
                Check(builds.Length > 0, "installed PAO confirms a successful build after the last source repair");
                var runs = evidence.Where(x => IsDotnet(x, "run", projectRoot) &&
                    x.Receipt.State == WorkflowEffectState.Succeeded && x.Tool.Evidence is { ProviderId: "cerebral.process", ExitCode: 0 } &&
                    x.Sequence > lastEdit).ToArray();
                Check(runs.Any(x => ExpectedStdout(x.Tool.Content)),
                    "native hidden dotnet execution confirms exit zero and the exact expected application stdout");
                Check(builds.Min(x => x.Sequence) < runs.Max(x => x.Sequence), "verified build precedes the successful execution");
                if (requireCompaction)
                {
                    var results = ReadContextResults(workflows.Journal, request.ConversationId);
                    var checkpoints = results.Where(x => x.Receipt.State == WorkflowEffectState.Succeeded &&
                        x.Receipt.Data is { } data && data.TryGetProperty("checkpoint", out _)).ToArray();
                    Check(checkpoints.Length > 0, "real model compaction produces at least one durable measured checkpoint");
                    Check(checkpoints.All(x =>
                    {
                        var compact = x.Receipt.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
                        return compact.Before.InputTokens > compact.After.InputTokens && compact.Passes > 0 &&
                            compact.Usage.All(pass => pass.Finish == ModelGenerationFinish.Stop);
                    }), "every adopted checkpoint retains real tokenizer and completed generation evidence");
                    Check(checkpoints.All(checkpoint => results.Any(x => x.Sequence > checkpoint.Sequence &&
                        x.Receipt.State == WorkflowEffectState.Succeeded && x.Receipt.Data is { } data && data.TryGetProperty("generation", out _))),
                        "each adopted checkpoint is followed by normal real model generation");
                }

                var reconnected = new WorkerWorkflows(new StateStore(store.DirectoryPath), configuration, new EventHub(), modules, bindings, tools);
                var saved = reconnected.GetOwned(admitted.Id, actor: null);
                Check(saved.Status == WorkflowStatus.Completed && saved.Revision == finished.Revision &&
                    saved.Result?.GetRawText() == finished.Result?.GetRawText(),
                    "a fresh host facade reopens the same completed SQLite workflow and result");
                var replay = await reconnected.SubmitAsync(request, actor: null, deadline.Token);
                Check(replay.Id == finished.Id && replay.Revision == finished.Revision,
                    "reconnect resubmission does not execute a second assignment");
                using var unloading = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await models.UnloadAsync(ModelId, unloading.Token);
                Check(!models.IsLoaded(ModelId), "the test-owned inference process unloads after the workflow");
                completedForDesktop = finished;
                Console.WriteLine($"Programming workflow: {checks} real local acceptance checks passed. Model inference and PAO file/build execution were real; final dotnet run used Cerebral's native process adapter. In-process DLL harness admission was used; installer, LAN and desktop rendering were not tested.");
            }
            if (desktopDll is not null)
                await ProgrammingWorkflowDesktopLiveTests.ContinueAsync(desktopDll, repository, root, completedForDesktop!, cancellationToken);
            success = true;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (success) RemoveOwnedDirectory(root, scratchParent, ownedName);
            else Console.WriteLine("RETAINED: actual workflow database, receipts and source for diagnosis at " + root + ". No installed configuration was changed.");
        }
    }

    private sealed class HarnessModule(IWorkflowDefinition definition) : IWorkflowModule
    {
        public string Id => "pegasus.programming";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed record Evidence(long Sequence, string EffectId, JsonElement Arguments,
        WorkflowCommandResult Receipt, WorkerWorkflowEffects.ToolData Tool);

    private static IReadOnlyList<(long Sequence, WorkflowCommandResult Receipt)> ReadContextResults(EventJournal journal, string conversation)
    {
        var results = new List<(long, WorkflowCommandResult)>();
        long after = 0; long? snapshot = null;
        while (true)
        {
            var page = journal.ReadPage(conversation, after, 100, snapshot);
            snapshot ??= page.SnapshotSequence;
            foreach (var item in page.Events.Where(x => x.Type == CerebralEventTypes.ToolResult))
            {
                var part = item.Content.OfType<StructuredContentPart>().Single(x => x.Schema == "workflow.command.result");
                results.Add((item.Sequence, CerebralJson.Read<WorkflowCommandResult>(part.Value)));
                if (results.Count > 256) throw new InvalidDataException("Acceptance receipt inspection exceeded its bound.");
            }
            if (page.NextAfterSequence is not { } next) return results;
            after = next;
        }
    }

    private static IReadOnlyList<Evidence> ReadEvidence(EventJournal journal, string conversation)
    {
        var calls = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var results = new List<Evidence>();
        long after = 0; long? snapshot = null; var count = 0;
        do
        {
            var page = journal.ReadPage(conversation, after, 100, snapshot);
            snapshot ??= page.SnapshotSequence;
            foreach (var item in page.Events)
            {
                if (++count > 1024) throw new InvalidDataException("Live acceptance exceeded its bounded event inspection limit.");
                if (item.Type == CerebralEventTypes.ToolCall && item.Data is { } callData)
                {
                    var call = CerebralJson.Read<ToolCallData>(callData);
                    if (call.ToolName == WorkerWorkflowEffects.ToolCommand) calls[call.CallId] = call.Arguments.Clone();
                }
                if (item.Type != CerebralEventTypes.ToolResult || item.CorrelationId is not { } id || !calls.TryGetValue(id, out var args)) continue;
                var part = item.Content.OfType<StructuredContentPart>().Single(x => x.Schema == "workflow.command.result");
                var receipt = CerebralJson.Read<WorkflowCommandResult>(part.Value);
                if (receipt.Data is not { } data) continue;
                results.Add(new(item.Sequence, id, args.GetProperty("arguments").Clone(), receipt,
                    CerebralJson.Read<WorkerWorkflowEffects.ToolData>(data)));
            }
            if (page.NextAfterSequence is not { } next) break;
            after = next;
        } while (true);
        return results;
    }

    private static void PrintProgress(EventJournal journal, string conversation, ref long after)
    {
        var page = journal.ReadPage(conversation, after, 100);
        foreach (var item in page.Events)
        {
            after = item.Sequence;
            if (item.Type != CerebralEventTypes.ToolResult) continue;
            var part = item.Content.OfType<StructuredContentPart>().Single(x => x.Schema == "workflow.command.result");
            var receipt = CerebralJson.Read<WorkflowCommandResult>(part.Value);
            if (receipt.Data is { } data && data.TryGetProperty("generation", out _))
            {
                var generation = CerebralJson.Read<WorkerWorkflowEffects.GenerationData>(data);
                Console.WriteLine($"MODEL: {generation.Generation.Finish}; input={generation.Prepared.InputTokens}; output={generation.Generation.GeneratedTokens}; responseBudget={generation.Prepared.ResponseBudget}.");
            }
            else if (receipt.Data is { } compactData && compactData.TryGetProperty("checkpoint", out _))
            {
                var compact = CerebralJson.Read<WorkerWorkflowEffects.CompactData>(compactData);
                Console.WriteLine($"COMPACTION: {receipt.State}; before={compact.Before.InputTokens}; after={compact.After.InputTokens}; passes={compact.Passes}.");
            }
            else if (receipt.Data is { } toolData && toolData.TryGetProperty("name", out _))
            {
                var tool = CerebralJson.Read<WorkerWorkflowEffects.ToolData>(toolData);
                Console.WriteLine($"TOOL: {tool.Name}; {receipt.State}; provider={tool.Evidence?.ProviderId}; exit={tool.Evidence?.ExitCode}; rid={tool.Evidence?.RequestId}; jid={tool.Evidence?.JobId}.");
            }
            else Console.WriteLine($"EFFECT: {receipt.State}; {Bound(receipt.Error ?? "no result data", 500)}");
        }
    }

    private static bool IsPao(Evidence item) => item.Tool.Evidence is { ProviderId: "mcp.cortex", JobId.Length: > 0 } evidence &&
        evidence.RequestId == item.EffectId;
    private static bool IsRead(Evidence item, string path) => item.Tool.Name == "file.read" &&
        item.Receipt.State == WorkflowEffectState.Succeeded && IsPao(item) && HasPath(item.Arguments, path);
    private static bool HasPath(JsonElement args, string path) => args.TryGetProperty("path", out var value) &&
        value.ValueKind == JsonValueKind.String && string.Equals(Path.GetFullPath(value.GetString()!), path, StringComparison.OrdinalIgnoreCase);
    private static bool IsDotnet(Evidence item, string verb, string root) => item.Tool.Name == "process.run" &&
        item.Arguments.GetProperty("executableId").GetString() == "dotnet" &&
        string.Equals(Path.GetFullPath(item.Arguments.GetProperty("workingDirectory").GetString()!), root, StringComparison.OrdinalIgnoreCase) &&
        item.Arguments.GetProperty("arguments").EnumerateArray().FirstOrDefault().GetString() == verb;
    private static bool ExpectedStdout(string content)
    {
        using var document = JsonDocument.Parse(content);
        return document.RootElement.TryGetProperty("stdout", out var stdout) &&
            stdout.GetString()?.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') == ExpectedOutput;
    }
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
    private static string RequireFile(string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) ? full : throw new FileNotFoundException("A required installed acceptance input is missing.", full);
    }
    private static string FindRepository()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "Cerebral.Worker", "Cerebral.Worker.csproj")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "tests", "Cerebral.Worker.Tests"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run this opt-in acceptance from the Cerebral source repository.");
    }
    private static void RejectLinksThrough(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Acceptance directories must not traverse a reparse point.");
    }
    private static void RemoveOwnedDirectory(string root, string parent, string expectedName)
    {
        var canonical = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(canonical), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(canonical), expectedName, StringComparison.Ordinal))
            throw new IOException("Refusing cleanup outside the exact owned acceptance directory.");
        RejectLinksThrough(canonical);
        var pending = new Stack<string>(); pending.Push(canonical);
        while (pending.TryPop(out var directory))
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An acceptance artifact became a reparse point; retain the directory for inspection.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        Directory.Delete(canonical, recursive: true);
    }
}
