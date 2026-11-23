using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

/// <summary>Actual installed local model and PAO with a disposable Chat database.
/// Invoke directly, outside PAO's serialized mutation queue, because the inner file tool uses PAO.</summary>
public static partial class ConversationWorkflowLiveTests
{
    public static async Task RunAsync(bool steeringOnly = false)
    {
        var modelPath = ProgrammingWorkflowLiveTests.DefaultModelPath;
        var serverPath = ProgrammingWorkflowLiveTests.DefaultServerPath;
        var cortexPath = ProgrammingWorkflowLiveTests.DefaultCortexPath;
        foreach (var path in new[] { modelPath, serverPath, cortexPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Live acceptance prerequisite is missing.", path);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-chat-live-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var success = false; var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            var note = "violet-engine-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllTextAsync(Path.Combine(project, "notes.txt"), "Acceptance marker: " + note);
            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store); var settings = configuration.Get();
            settings.LlamaServerPath = serverPath; settings.ReservedMemoryMb = 1024;
            settings.MaxConcurrentJobs = 1; settings.ExternalCommandsEnabled = true;
            settings.ModelDirectory = Path.Combine(root, "models"); settings.ProjectsDirectory = project;
            settings.SharedDirectory = Path.Combine(root, "shared"); settings.CortexNamespaces = []; settings.CortexWriteNamespaces = [];
            settings.McpServers = [new() { Id = "cortex", Enabled = true, ViaUserConnector = false,
                Executable = cortexPath, Arguments = [ConnectorProtocol.StableCortexMcpArgument] }];
            configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "chat-live", Name = "Cerebral live acceptance", Role = "lead", ModelId = "chat-live-model",
                WorkerId = "local", ModelWorkerId = "local", FollowsModelBudgets = false,
                ContextTokens = 16384, ResponseTokens = 2048, ReasoningMode = "off", Temperature = 0,
                MaxSteps = 64, MaxSeconds = 300, AllowedRoots = [project],
                AllowedTools = ["file.read"], Capabilities = ["filesystem.read"],
                Instructions = "Follow the conversation harness schema. Give brief direct answers. Use tools only for requested work and honor real user questions."
            };
            Configuration.ValidateProfile(profile); store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Name = "Installed Qwen Chat acceptance", Path = modelPath,
                SizeBytes = new FileInfo(modelPath).Length, ContextTokens = 16384, MaximumContextTokens = 16384,
                RecommendedContextTokens = 16384, AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            var events = new EventHub();
            await using var tools = new ToolDispatcher(configuration.Get);
            await using var models = new ModelRuntime(store, configuration.Get, events);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var host = new WorkerWorkflowEffects(store, configuration, models, tools);
            var steeringGate = steeringOnly ? new SteeringGateModule(host) : null;
            WorkerWorkflows Open() => new(store, configuration, events, [new ConversationWorkflowModule(), (IWorkflowModule?)steeringGate ?? host],
                [ConversationWorkflowModule.PackageBinding, new(host.Id, WorkerWorkflowEffects.ModuleVersion, true, [])], tools);
            var workflows = Open(); var conversationId = Guid.NewGuid().ToString("N");
            async Task<WorkflowRun> Submit(string text, string? hidden = null)
            {
                var request = new SubmitWorkflowRequest(Guid.NewGuid().ToString("N"), conversationId, profile.Id,
                    ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion, text, hidden,
                    CerebralJson.Element(new { workingDirectory = project, responseSharePercent = 20 }), StepLimit: 64);
                var accepted = await workflows.SubmitAsync(request, null, deadline.Token);
                Check(accepted.Status == WorkflowStatus.Queued, "Chat request admitted");
                var result = await workflows.Runtime.RunAsync(accepted.Id, deadline.Token);
                Print(workflows.Journal, conversationId, accepted.Submission.ConversationSequence);
                Console.WriteLine("CHAT: " + result.Status + "; " + (result.Error ?? Summary(result)));
                return result;
            }
            if (steeringOnly)
            {
                await VerifySteeringAsync(workflows, steeringGate!, profile, conversationId, project, deadline.Token, Check);
                await models.UnloadAsync(profile.ModelId, deadline.Token);
                Check(!models.IsLoaded(profile.ModelId), "test-owned model runtime unloads");
                Console.WriteLine($"Conversation steering live acceptance: {checks} checks passed with actual local model and SQLite; a controlled delivery gate held the first generation result while owner steering was accepted. No installed service or desktop rendering exercised.");
                success = true;
                return;
            }
            var first = await Submit("Remember the project codename Quartz Badger. Reply exactly: Quartz Badger recorded. Include nothing else. No tools needed.",
                "The private release label is riverglass-72. Retain it for this conversation; give it only when the user asks for the release label.");
            Check(first.Status == WorkflowStatus.Completed && Summary(first).Contains("Quartz Badger", StringComparison.OrdinalIgnoreCase),
                "real model answers directly without delegation or unnecessary questions");
            Check(!Summary(first).Contains("riverglass-72", StringComparison.OrdinalIgnoreCase),
                "release label has not leaked into the first visible answer; later recall requires hidden source context");
            Check(!Events(workflows.Journal, conversationId).Any(e => e.Type == CerebralEventTypes.ToolCall &&
                e.Data is { } d && CerebralJson.Read<ToolCallData>(d).ToolName == WorkerWorkflowEffects.ToolCommand),
                "simple first answer used no project tool");

            workflows = Open();
            var second = await Submit("What project codename did I give you, and what is the private release label? Answer briefly with both.");
            Check(second.Status == WorkflowStatus.Completed && Summary(second).Contains("Quartz Badger", StringComparison.OrdinalIgnoreCase) &&
                Summary(second).Contains("riverglass-72", StringComparison.OrdinalIgnoreCase),
                "fresh host facade restores prior visible input and hidden assignment to real model context");
            var third = await Submit("Read notes.txt in the provided workingDirectory with file.read and give me the exact acceptance marker from it. Use the actual file; do not guess.");
            Check(third.Status == WorkflowStatus.Completed && Summary(third).Contains(note, StringComparison.Ordinal),
                "real model discovers the file schema, executes the file tool and uses actual contents");
            Check(Events(workflows.Journal, conversationId).Where(e => e.Type == CerebralEventTypes.ToolResult)
                .SelectMany(e => e.Content.OfType<StructuredContentPart>())
                .Where(p => p.Schema == "workflow.command.result")
                .Select(p => CerebralJson.Read<WorkflowCommandResult>(p.Value))
                .Any(r => r.State == WorkflowEffectState.Succeeded && r.Data is { } data &&
                    data.TryGetProperty("name", out var name) && name.GetString() == "file.read" &&
                    CerebralJson.Read<WorkerWorkflowEffects.ToolData>(data).Evidence?.ProviderId == "mcp.cortex"),
                "file result is backed by an actual Cortex PAO receipt");
            var fourth = await Submit("I need you to ask me two required free-text questions before proceeding: owner (record owner's name) and destination (record destination). Use fields with exactly those IDs. Do not guess these values or create any file. After I answer, repeat my two values briefly.");
            Check(fourth.Status == WorkflowStatus.Waiting && fourth.Questions is { Fields.Count: 2 } &&
                fourth.Questions.Fields.Select(q => q.Id).Order().SequenceEqual(new[] { "destination", "owner" }),
                "real model emits one durable form containing two required questions");
            var question = fourth.Questions!;
            workflows = Open();
            workflows.Answer(new(Guid.NewGuid().ToString("N"), fourth.Id, question.Id, question.Revision,
                new Dictionary<string, string> { ["owner"] = "Nova", ["destination"] = "Vault B" }), null);
            var answered = await workflows.Runtime.RunAsync(fourth.Id, deadline.Token);
            Check(answered.Status == WorkflowStatus.Completed && Summary(answered).Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                Summary(answered).Contains("Vault B", StringComparison.OrdinalIgnoreCase),
                "answered form resumes after fresh host facade and reaches an actual model reply");
            Check(Open().Store.GetConversation(ConversationWorkflowModule.DefinitionId, conversationId)?.LatestRunId == fourth.Id,
                "latest Chat navigation opens the saved final run");
            await models.UnloadAsync(profile.ModelId, deadline.Token);
            Check(!models.IsLoaded(profile.ModelId), "test-owned model runtime unloads");
            Console.WriteLine($"Conversation live acceptance: {checks} checks passed with actual installed model, tokenizer, PAO read and local SQLite. Desktop rendering, installer and LAN were not exercised.");
            success = true;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (success)
            {
                var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(root).StartsWith("cerebral-chat-live-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid Chat acceptance cleanup root.");
                Directory.Delete(root, true);
            }
            else Console.WriteLine("RETAINED FOR DIAGNOSIS: " + root);
        }
    }

    private static string Summary(WorkflowRun run) =>
        run.Result is { } result && result.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "";

    private static IReadOnlyList<CerebralEvent> Events(EventJournal journal, string conversation)
    {
        var result = new List<CerebralEvent>(); long after = 0; long? snapshot = null;
        do
        {
            var page = journal.ReadPage(conversation, after, 100, snapshot); snapshot ??= page.SnapshotSequence;
            result.AddRange(page.Events);
            if (result.Count > 1024) throw new InvalidDataException("Live acceptance event inspection exceeded its bound.");
            if (page.NextAfterSequence is not { } next) break;
            after = next;
        } while (true);
        return result;
    }

    private static void Print(EventJournal journal, string conversation, long after)
    {
        foreach (var part in Events(journal, conversation).Where(e => e.Sequence > after && e.Type == CerebralEventTypes.ToolResult)
            .SelectMany(e => e.Content.OfType<StructuredContentPart>()).Where(p => p.Schema == "workflow.command.result"))
        {
            var result = CerebralJson.Read<WorkflowCommandResult>(part.Value);
            if (result.Data is not { } data || !data.TryGetProperty("generation", out _)) continue;
            var measured = CerebralJson.Read<WorkerWorkflowEffects.GenerationData>(data);
            Console.WriteLine($"MODEL: {measured.Generation.Finish}; input={measured.Prepared.InputTokens}; output={measured.Generation.GeneratedTokens}; response={measured.Prepared.ResponseBudget}");
        }
    }
}
