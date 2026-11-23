using System.Diagnostics;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in actual source Worker composition, named pipe, SQLite restart and local Qwen
/// inference. No desktop, PAO, MCP connector, installed configuration or installer is exercised.</summary>
public static class ConversationPipeLiveTests
{
    public static async Task RunAsync(string? previewPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This local pipe/model acceptance requires Windows.");
        var modelPath = ProgrammingWorkflowLiveTests.DefaultModelPath;
        var serverPath = ProgrammingWorkflowLiveTests.DefaultServerPath;
        var workerDll = typeof(CommandDispatcher).Assembly.Location;
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        foreach (var path in new[] { modelPath, serverPath, workerDll, Path.ChangeExtension(workerDll, ".runtimeconfig.json"), dotnet })
            if (!File.Exists(path)) throw new FileNotFoundException("Build the source Worker and provide the installed local model/runtime before this opt-in acceptance.", path);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-chat-pipe-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The unique fixture root already exists.");
        Directory.CreateDirectory(root);
        var dataPath = Path.Combine(root, "data");
        var plugins = Path.Combine(root, "empty-plugins");
        var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.ChatPipe." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var children = new List<Process>();
        var checks = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Chat pipe acceptance: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            Directory.CreateDirectory(plugins);
            var store = new StateStore(dataPath);
            var configuration = new Configuration(store);
            var settings = configuration.Get();
            settings.LlamaServerPath = serverPath;
            settings.ReservedMemoryMb = 1024; settings.MaxConcurrentJobs = 1;
            settings.ModelDirectory = Path.Combine(root, "models");
            settings.ProjectsDirectory = Path.Combine(root, "projects");
            settings.SharedDirectory = Path.Combine(root, "shared");
            settings.McpServers.Clear(); settings.Workers.Clear(); settings.BackgroundTasks.Clear(); settings.Executables.Clear();
            settings.CortexNamespaces = []; settings.CortexWriteNamespaces = []; settings.ExternalCommandsEnabled = false;
            configuration.Save(settings);
            const string modelId = "pipe-chat-model", profileId = "pipe-chat-lead";
            var profile = new AgentProfile
            {
                Id = profileId, Name = "Cerebral", Role = "lead", ModelId = modelId,
                WorkerId = "local", ModelWorkerId = "local", FollowsModelBudgets = false,
                ContextTokens = 16384, ResponseTokens = 1024, ReasoningMode = "off", Temperature = 0,
                MaxSteps = 16, MaxSeconds = 120, AutomaticCompaction = false,
                AllowedTools = [], Capabilities = [], AllowedRoots = [],
                Instructions = "Follow the conversation harness response schema. Answer the user's simple question directly and briefly. Do not request unnecessary clarification or use tools."
            };
            Configuration.ValidateProfile(profile);
            store.Put("agent", profileId, profile);
            store.Put("model", modelId, new ModelRecord
            {
                Id = modelId, Name = "Installed Qwen pipe acceptance", Path = modelPath, SizeBytes = new FileInfo(modelPath).Length,
                ContextTokens = 16384, MaximumContextTokens = 16384, RecommendedContextTokens = 16384,
                AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            // All preparation writes precede startup. Subsequent work uses the actual child host pipe.
            SqliteConnection.ClearAllPools();
            var conversationId = Guid.NewGuid().ToString("N");
            var fact = "cobalt-orchid-" + Guid.NewGuid().ToString("N")[..8];
            var firstWorker = StartWorker();
            WorkflowRun first;
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, firstWorker);
                var discovery = await client.GetAsync<System.Text.Json.JsonElement>("sdk.discover", cancellationToken: deadline.Token);
                Check(discovery.GetProperty("workflows").EnumerateArray().Any(item =>
                    item.GetProperty("id").GetString() == ConversationWorkflowModule.DefinitionId),
                    "actual Worker Program composes the built-in Chat workflow without loading a fixture plugin");
                first = await Turn(client, firstWorker,
                    $"Remember this project codename for our conversation: {fact}. Reply exactly: Recorded. No tools are needed.");
                Check(Summary(first).Contains("Recorded", StringComparison.OrdinalIgnoreCase) &&
                    !Summary(first).Contains(fact, StringComparison.Ordinal),
                    "real model records the prior fact without repeating it in its visible answer");
                var evidence = await ReadTurn(client, first);
                CheckGeneration(evidence, first);
                var summary = await client.GetAsync<ConversationSummary>("conversations.get", new { id = conversationId }, cancellationToken: deadline.Token);
                Check(summary.LatestRunId == first.Id && summary.Status == WorkflowStatus.Completed && summary.ProfileId == profileId,
                    "pipe conversation summary points to the completed first turn");
                await StopWorker(client, firstWorker);
            }

            var secondWorker = StartWorker();
            Check(secondWorker.Id != firstWorker.Id && firstWorker.HasExited, "second turn starts in a different Worker process after the first exited");
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, secondWorker);
                var retained = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = first.Id }, cancellationToken: deadline.Token);
                Check(retained.Status == WorkflowStatus.Completed && Summary(retained) == Summary(first),
                    "same endpoint and state root recover the original durable result after process restart");
                var retainedEvents = await client.GetAsync<JournalPage>("jobs.workflow.events",
                    new { id = first.Id, afterSequence = 0, limit = 1 }, cancellationToken: deadline.Token);
                var compactRequest = new ConversationCompactionRequest(Guid.NewGuid().ToString("N"),
                    ConversationWorkflowModule.DefinitionId, conversationId, retainedEvents.SnapshotSequence);
                var compactReceipt = await client.GetAsync<ConversationCompactionReceipt>("conversations.compact", compactRequest,
                    cancellationToken: deadline.Token);
                var compactRetry = await client.GetAsync<ConversationCompactionReceipt>("conversations.compact", compactRequest,
                    cancellationToken: deadline.Token);
                Check(compactReceipt == compactRetry && compactReceipt.ConsumedByRunId is null,
                    "owner pipe stores one durable compaction request and exact retry returns the same receipt");
                const string followup = "What exact project codename did I give you in my preceding message? Reply with that codename only.";
                Check(!followup.Contains(fact, StringComparison.Ordinal), "follow-up prompt does not supply the expected answer");
                var second = await Turn(client, secondWorker, followup);
                Check(second.Submission.ConversationId == conversationId && second.Submission.ConversationSequence > 0 &&
                    second.Submission.ConversationCompactionRequestId == compactRequest.RequestId &&
                    Summary(second).Contains(fact, StringComparison.Ordinal),
                    "restarted real model consumes the saved compaction request and recalls the prior random fact");
                var evidence = await ReadTurn(client, second);
                CheckGeneration(evidence, second);
                var calls = evidence.Where(item => item.TurnId == second.Id && item.Type == CerebralEventTypes.ToolCall && item.Data is not null)
                    .Select(item => CerebralJson.Read<ToolCallData>(item.Data!.Value)).ToArray();
                var compactIndex = Array.FindIndex(calls, call => call.ToolName == "cerebral.host:compact");
                var generateIndex = Array.FindIndex(calls, call => call.ToolName == WorkerWorkflowEffects.GenerateCommand);
                Check(compactIndex >= 0 && generateIndex > compactIndex,
                    "one measured compaction completes before the next ordinary model response");
                Check(evidence.Where(item => item.TurnId == second.Id && item.Type == CerebralEventTypes.ToolCall && item.Data is not null)
                    .Select(item => CerebralJson.Read<ToolCallData>(item.Data!.Value))
                    .Any(call => call.ToolName == WorkerWorkflowEffects.GenerateCommand &&
                        call.Arguments.TryGetProperty("context", out var context) && context.GetRawText().Contains(fact, StringComparison.Ordinal)),
                    "saved second-turn generation request includes the prior fact in its historical context projection");
                var summary = await client.GetAsync<ConversationSummary>("conversations.get", new { id = conversationId }, cancellationToken: deadline.Token);
                var recent = await client.GetAsync<ConversationRecentPage>("conversations.recent", new { limit = 4 }, cancellationToken: deadline.Token);
                Check(summary.LatestRunId == second.Id && summary.Status == WorkflowStatus.Completed &&
                    recent.Conversations.Count == 1 && recent.Conversations[0].Id == conversationId && recent.Conversations[0].LatestRunId == second.Id,
                    "get and recent expose one conversation with its actual latest completed turn");
                await StopWorker(client, secondWorker);
            }
            var thirdWorker = StartWorker();
            Check(thirdWorker.Id != secondWorker.Id && secondWorker.HasExited,
                "a third Worker process reopens state after the compaction effect committed");
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, thirdWorker);
                var third = await Turn(client, thirdWorker,
                    "State the exact project codename from this conversation again. Reply with that codename only.");
                Check(Summary(third).Contains(fact, StringComparison.Ordinal),
                    "a later process reuses the retained checkpoint and still recalls the compacted fact");
                var evidence = await ReadTurn(client, third);
                var history = evidence.Where(item => item.TurnId == third.Id && item.Type == CerebralEventTypes.ToolResult &&
                        item.Actor.Id == WorkerWorkflowEffects.HistoryCommand)
                    .SelectMany(item => item.Content.OfType<StructuredContentPart>())
                    .Where(part => part.Schema == "workflow.command.result")
                    .Select(part => CerebralJson.Read<WorkflowCommandResult>(part.Value))
                    .Where(result => result.State == WorkflowEffectState.Succeeded && result.Data is not null)
                    .Select(result => CerebralJson.Read<WorkerWorkflowEffects.HistoryData>(result.Data!.Value))
                    .FirstOrDefault(data => data.Context.Checkpoint is not null);
                Check(history?.Context.Checkpoint is { BeforeCurrentInput: true } retainedCheckpoint &&
                    history.Context.Contributions.All(source => source.SourceRevision > retainedCheckpoint.ContextRevision),
                    "history returns the durable checkpoint plus only events newer than its source boundary");
                CheckGeneration(evidence, third);
                var orchestrator = FindOrchestratorExecutable(workerDll);
                var desktop = await RunDesktopInspectionAsync(orchestrator, endpoint, dataPath, conversationId, previewPath, deadline.Token);
                using var desktopEvidence = JsonDocument.Parse(desktop.StandardOutput.Trim());
                var desktopRoot = desktopEvidence.RootElement; var loaded = desktopRoot.GetProperty("loaded");
                Check(desktop.ExitCode == 0 && desktopRoot.GetProperty("success").GetBoolean() &&
                    desktopRoot.GetProperty("windowShown").GetBoolean() && desktopRoot.GetProperty("actualHwnd").GetBoolean() &&
                    desktopRoot.GetProperty("transport").GetString() == "authenticated-local-pipe" &&
                    !desktopRoot.GetProperty("savedConnectionApplied").GetBoolean() && !desktopRoot.GetProperty("savedConnectionChanged").GetBoolean() &&
                    loaded.GetProperty("selectedSession").GetString() == conversationId && loaded.GetProperty("messages").GetInt32() > 0 &&
                    loaded.GetProperty("renderedMessages").GetInt32() > 0,
                    "shown source Pegasus Cerebral window reconnects to the same pipe and renders the durable Qwen conversation");
                if (previewPath is not null)
                    Check(File.Exists(previewPath) && new FileInfo(previewPath).Length > 10_000,
                        "shown source Pegasus Cerebral window produces a nonempty native-DPI visual-inspection capture");
                Console.WriteLine("DESKTOP PIPE: " + desktop.StandardOutput.Trim());
                await StopWorker(client, thirdWorker);
            }
            Console.WriteLine($"Actual Chat pipe acceptance passed ({checks} checks): three Worker processes, authenticated local pipe, durable manual compaction/checkpoint reuse, real Qwen/tokenizer, persisted history recall, and a shown source Pegasus Cerebral window. PAO, MCP, LAN and installation were not exercised.");

            Process StartWorker()
            {
                var start = new ProcessStartInfo(dotnet)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(workerDll)!
                };
                foreach (var argument in new[] { workerDll, "--local-run", "--data", dataPath, "--pipe-name", endpoint.PipeName,
                    "--pipe-instance", endpoint.InstanceId, "--service-plugins", plugins }) start.ArgumentList.Add(argument);
                var child = Process.Start(start) ?? throw new IOException("The owned Worker process did not launch.");
                children.Add(child); return child;
            }

            async Task WaitHealth(CerebralClient client, Process child)
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                startup.CancelAfter(TimeSpan.FromSeconds(25));
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (child.HasExited) throw new IOException("Owned Worker exited during startup with code " + child.ExitCode + ".");
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var health = await client.GetAsync<HostInfo>("health", cancellationToken: probe.Token);
                        Check(client.IsLocalPipe && health.LocalRun &&
                            string.Equals(Path.GetFullPath(health.DataDirectory), dataPath, StringComparison.OrdinalIgnoreCase),
                            "authenticated pipe health identifies the exact owned local Worker data root");
                        return;
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        await Task.Delay(100, startup.Token);
                    }
                }
            }

            async Task<WorkflowRun> Turn(CerebralClient client, Process child, string text)
            {
                using var turnDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                turnDeadline.CancelAfter(TimeSpan.FromMinutes(2));
                var accepted = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest(
                    Guid.NewGuid().ToString("N"), conversationId, profileId, ConversationWorkflowModule.DefinitionId,
                    ConversationWorkflowModule.DefinitionVersion, text, null, CerebralJson.Element(new { responseSharePercent = 10 }), 16),
                    cancellationToken: turnDeadline.Token);
                var run = accepted;
                while (!run.IsTerminal)
                {
                    if (child.HasExited) throw new IOException("Worker exited while processing Chat with code " + child.ExitCode + ".");
                    if (run.Status == WorkflowStatus.Waiting)
                        throw new InvalidOperationException("Direct Chat unexpectedly waits: " + Bound(run.WaitingReason ?? run.Error ?? "unknown", 600));
                    await Task.Delay(250, turnDeadline.Token);
                    run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = run.Id }, cancellationToken: turnDeadline.Token);
                }
                Console.WriteLine($"CHAT PIPE: run={run.Id}; state={run.Status}; steps={run.Steps}; summary={Bound(Summary(run), 240)}");
                if (run.Status != WorkflowStatus.Completed)
                    throw new InvalidOperationException("Actual pipe Chat did not complete: " + Bound(run.Error ?? "No retained error.", 800));
                Check(true, "actual service scheduler completes a direct Chat turn within two minutes");
                return run;
            }

            async Task<IReadOnlyList<CerebralEvent>> ReadTurn(CerebralClient client, WorkflowRun run)
            {
                var result = new List<CerebralEvent>();
                var after = run.Submission.ConversationSequence; long? snapshot = null;
                for (var pageNumber = 0; pageNumber < 6; pageNumber++)
                {
                    var args = new Dictionary<string, object> { ["id"] = run.Id, ["afterSequence"] = after, ["limit"] = 100 };
                    if (snapshot is { } value) args["snapshotSequence"] = value;
                    var page = await client.GetAsync<JournalPage>("jobs.workflow.events", args, cancellationToken: deadline.Token);
                    snapshot ??= page.SnapshotSequence; result.AddRange(page.Events);
                    if (page.NextAfterSequence is not { } next) return result;
                    if (next <= after) throw new InvalidDataException("Event evidence cursor did not advance.");
                    after = next;
                }
                throw new InvalidDataException("Chat acceptance evidence exceeded six bounded event pages.");
            }

            void CheckGeneration(IReadOnlyList<CerebralEvent> evidence, WorkflowRun run)
            {
                var receipts = evidence.Where(item => item.TurnId == run.Id && item.Type == CerebralEventTypes.ToolResult)
                    .SelectMany(item => item.Content.OfType<StructuredContentPart>()).Where(part => part.Schema == "workflow.command.result")
                    .Select(part => CerebralJson.Read<WorkflowCommandResult>(part.Value)).ToArray();
                var generations = receipts.Where(receipt => receipt.State == WorkflowEffectState.Succeeded &&
                    receipt.Data is { } value && value.TryGetProperty("generation", out _))
                    .Select(receipt => CerebralJson.Read<WorkerWorkflowEffects.GenerationData>(receipt.Data!.Value)).ToArray();
                Check(generations.Length > 0 && generations.Any(item => item.Generation.Finish == ModelGenerationFinish.Stop &&
                    item.Prepared.ModelId == modelId && item.Prepared.InputTokens > 0 && item.Generation.GeneratedTokens > 0),
                    "pipe-retained generation receipt contains real model completion and measured tokenizer usage");
                Check(!evidence.Any(item => item.TurnId == run.Id && item.Type == CerebralEventTypes.ToolCall &&
                    item.Data is { } value && CerebralJson.Read<ToolCallData>(value).ToolName == WorkerWorkflowEffects.ToolCommand),
                    "direct Chat executes no semantic tools or MCP calls");
                foreach (var generation in generations)
                    Console.WriteLine($"MODEL PIPE: finish={generation.Generation.Finish}; input={generation.Prepared.InputTokens}; output={generation.Generation.GeneratedTokens}; budget={generation.Prepared.ResponseBudget}");
            }

            async Task StopWorker(CerebralClient client, Process child)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                stop.CancelAfter(TimeSpan.FromSeconds(30));
                var unload = await client.CommandAsync("models.unload", new { id = modelId }, cancellationToken: stop.Token);
                if (!unload.Success) throw new InvalidOperationException("Owned model unload failed: " + Bound(unload.Error ?? "unknown", 600));
                var stopped = await client.CommandAsync("service.stop-local", cancellationToken: stop.Token);
                if (!stopped.Success) throw new InvalidOperationException("Owned Worker stop failed: " + Bound(stopped.Error ?? "unknown", 600));
                await child.WaitForExitAsync(stop.Token);
                Check(child.ExitCode == 0, "owned model unloads and Worker exits cleanly through its local stop command");
            }
        }
        finally
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        // Emergency cleanup is confined to this fixture's exact child and descendants.
                        child.Kill(entireProcessTree: true);
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await child.WaitForExitAsync(stop.Token);
                    }
                }
                finally { child.Dispose(); }
            }
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cerebral-chat-pipe-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned Chat pipe fixture root.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record DesktopInspection(int ExitCode, string StandardOutput, string StandardError);

    private static string FindOrchestratorExecutable(string workerDll)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(workerDll)!); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Pegasus.Cerebral.slnx"))) continue;
            var path = Path.Combine(directory.FullName, "src", "Cerebral.Orchestrator", "bin", "Release", "net10.0-windows", "Cerebral.Orchestrator.exe");
            return File.Exists(path) ? path : throw new FileNotFoundException("Build the Release Pegasus Cerebral desktop before the pipe acceptance.", path);
        }
        throw new FileNotFoundException("The Pegasus Cerebral source root could not be found from the Worker output.");
    }

    private static async Task<DesktopInspection> RunDesktopInspectionAsync(string executable, LocalWorkerEndpoint endpoint,
        string dataPath, string conversationId, string? previewPath, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--check-pipe-service-ui", endpoint.PipeName, endpoint.InstanceId, dataPath, "--session", conversationId })
            start.ArgumentList.Add(argument);
        if (previewPath is not null) { start.ArgumentList.Add("--preview"); start.ArgumentList.Add(previewPath); }
        using var child = Process.Start(start) ?? throw new IOException("The source Pegasus Cerebral inspection process did not start.");
        var output = child.StandardOutput.ReadToEndAsync(token); var errors = child.StandardError.ReadToEndAsync(token);
        await child.WaitForExitAsync(token);
        var result = new DesktopInspection(child.ExitCode, await output, await errors);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The shown source Pegasus Cerebral inspection failed: " + result.StandardError[..Math.Min(2000, result.StandardError.Length)]);
        return result;
    }

    private static string Summary(WorkflowRun run) => run.Result is { } result && result.TryGetProperty("summary", out var summary)
        ? summary.GetString() ?? "" : "";
    private static string Bound(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}
