using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.ReviewPlugin;
using Cerebral.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class WorkflowWorkerTests
{
    public static void RunStepLimitContractChecks()
    {
        var schema = WorkerWorkflows.OperationDescriptors.Single(x => x.Id == "jobs.workflow.submit").InputSchema;
        var stepSchema = schema.GetProperty("properties").GetProperty("stepLimit");
        Check(stepSchema.GetProperty("maximum").GetInt32() == WorkflowContract.MaximumSteps,
            "submit schema advertises the public durable step ceiling");
        // The full transport descriptor includes nullable unions outside the Engine schema subset.
        // Check its numeric boundary and the actual Client submission gate independently.
        static WorkflowConversationSubmission Submission(int stepLimit) => new(
            "step-ceiling-request", "step-ceiling-profile", "example.review", "1.0.0",
            "Check the advertised submission boundary.", CerebralJson.Element(new { }), StepLimit: stepLimit);
        WorkflowSchema.Validate(CerebralJson.Element(512), stepSchema);
        var admitted = WorkflowConversationClient.FreezeSubmission(Submission(512));
        Check(admitted.StepLimit == 512, "submit schema and Client both admit the public 512-step ceiling");
        var schemaRejected = false;
        try { WorkflowSchema.Validate(CerebralJson.Element(513), stepSchema); }
        catch (ArgumentException) { schemaRejected = true; }
        var clientRejected = false;
        try { WorkflowConversationClient.FreezeSubmission(Submission(513)); }
        catch (ArgumentException) { clientRejected = true; }
        Check(schemaRejected && clientRejected, "submit schema and Client both reject 513 steps above the public ceiling");
    }

    public static async Task RunAsync()
    {
        RunStepLimitContractChecks();
        Check(WorkerWorkflows.PolicyStepLimitFor(ConversationWorkflowModule.DefinitionId, 20) == 80 &&
              WorkerWorkflows.PolicyStepLimitFor(ConversationWorkflowModule.DefinitionId, 80) == 320 &&
              WorkerWorkflows.PolicyStepLimitFor(ConversationWorkflowModule.DefinitionId, 200) == WorkflowContract.MaximumSteps,
            "conversation agent-action budgets expand to bounded durable transition budgets");
        Check(WorkerWorkflows.PolicyStepLimitFor("pegasus.programming:develop", 20) == 80 &&
              WorkerWorkflows.PolicyStepLimitFor("pegasus.programming:develop", 80) == 320 &&
              WorkerWorkflows.PolicyStepLimitFor("pegasus.programming:develop", 200) == WorkflowContract.MaximumSteps,
            "Programming agent-action budgets expand to bounded durable transition budgets");
        Check(WorkerWorkflows.PolicyStepLimitFor("example.review", 20) == 20,
            "explicit non-conversation workflows retain their declared step semantics");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var config = new Configuration(store); var events = new EventHub();
            var profile = new AgentProfile { Id = "reviewer", Name = "Review", Capabilities = ["diagnostics"] };
            store.Put("agent", profile.Id, profile);
            using var plugin = new ExampleReviewPlugin();
            var pluginData = Path.Combine(root, "review-data");
            plugin.Initialize(new ReviewPluginTestHost(pluginData, AppContext.BaseDirectory));
            ServicePluginPackageBinding[] bindings = [new(plugin.Id, "1.0.0", true, [])];
            var workflows = new WorkerWorkflows(store, config, events, [plugin], bindings);
            await using var models = new ModelRuntime(store, config.Get, events);
            await using var tools = new ToolDispatcher(config.Get);
            using var scheduler = new JobScheduler(store, config, new NoModelExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, workflows);
            var commands = new CommandDispatcher(store, config, models, scheduler, tools, events, workflows: workflows);
            async Task<CommandResult> Command(string op, object input, string? caller = null) =>
                await commands.ExecuteAsync(new(op, CerebralJson.Element(input), caller));
            var request = Request("worker-one", "review-conversation");
            var accepted = await Command("jobs.workflow.submit", request, profile.Id);
            Check(accepted.Success, "shared dispatcher admits valid workflow: " + accepted.Error);
            var settings = config.Get(); settings.QueueCapacity = 1; config.Save(settings);
            profile.Name = "Renamed review"; profile.Revision++; store.Put("agent", profile.Id, profile);
            var retry = await Command("jobs.workflow.submit", request, profile.Id);
            Check(retry.Success && CerebralJson.Read<WorkflowRun>(retry.Data).Id == request.RequestId,
                "duplicate lookup precedes full queue and current profile rename");
            Check(!(await Command("jobs.workflow.submit", request with { UserText = "different" }, profile.Id)).Success,
                "same request identifier cannot change accepted work");
            Check(!(await Command("jobs.workflow.submit", Request("overflow", "another"))).Success,
                "workflow admission observes configured shared queue bound");
            Check(!(await Command("jobs.workflow.submit", new { request.RequestId, caller = "owner" })).Success,
                "external submission cannot supply host caller metadata");
            await scheduler.StartAsync(default);
            var waiting = await WaitAsync(() => workflows.Store.Get(request.RequestId), x => x.Questions is not null);
            Check(waiting.Questions!.Fields.Count == 2 && waiting.Status == WorkflowStatus.Waiting,
                "normal hosted scheduler persists two question fields then releases its slot");
            var answers = Answer(waiting, "answer-one");
            profile.Capabilities.Add("administration"); store.Put("agent", profile.Id, profile);
            Check(!(await Command("service-packages.inspect", new { id = "example.review" }, profile.Id)).Success,
                "administration agents cannot invoke host-owned service package lifecycle commands");
            Check(!(await Command("jobs.workflow.answer", answers, profile.Id)).Success,
                "agent administration cannot impersonate the owner answering a question");
            store.Put("agent", "other", new AgentProfile { Id = "other", Name = "Other", Capabilities = ["administration"] });
            Check(!(await Command("jobs.workflow.get", new { id = request.RequestId }, "other")).Success,
                "another profile cannot inspect the workflow even with administrative grants");
            Check(!(await Command("jobs.workflow.recent", new { limit = 2 }, profile.Id)).Success &&
                !(await Command("jobs.workflow.recent", new { limit = 2 }, "other")).Success,
                "recent workflow listing remains owner-only even for administrative agents");
            Check(!(await Command("conversations.recent", new { limit = 2 }, profile.Id)).Success &&
                !(await Command("conversations.get", new { id = request.ConversationId }, "other")).Success,
                "Chat navigation routes remain owner-only before administrative grants");
            Check(CerebralJson.Read<ConversationRecentPage>((await Command("conversations.recent", new { limit = 2 })).Data).Conversations.Count == 0,
                "ordinary Chat navigation excludes specialized workflow runs");
            Check(!(await Command("conversations.protected", new { limit = 2 }, profile.Id)).Success &&
                !(await Command("conversations.protected", new { limit = 2 }, "other")).Success,
                "protected summaries deny both owning and foreign administrative agents");
            Check(!(await Command("conversations.protected", new { limit = 2, pinnedIds = new[] { request.ConversationId } })).Success,
                "owner summary requests cannot supply invented protected identities");
            Check(CerebralJson.Read<ConversationRecentPage>((await Command("conversations.protected", new { limit = 2 })).Data).Conversations.Count == 0,
                "protected summaries exclude specialized workflow runs");
            var summaries = CerebralJson.Read<WorkflowRecentPage>((await Command("jobs.workflow.recent", new { limit = 1 })).Data);
            Check(summaries.Runs.Count == 1 && summaries.Runs[0].Id == request.RequestId &&
                summaries.Runs[0].Status == WorkflowStatus.Waiting,
                "owner recent summaries expose current state without loading the conversation");
            var activeRows = CerebralJson.Read<WorkflowActiveSummaryPage>((await Command("jobs.workflow.active-summaries", new { limit = 1 })).Data);
            Check(activeRows.Runs.Count == 1 && activeRows.Runs[0].Id == request.RequestId &&
                activeRows.Runs[0].Status == WorkflowStatus.Waiting, "owner active summaries include waiting workflow without execution state");
            Check(!(await Command("jobs.workflow.active-summaries", new { limit = 1 }, profile.Id)).Success &&
                !(await Command("queue.active-summaries", new { limit = 1 }, "other")).Success,
                "active workflow and legacy summary listings deny administrative agents");
            var legacyRows = CerebralJson.Read<LegacyJobSummaryPage>((await Command("queue.active-summaries", new { limit = 1 })).Data);
            Check(legacyRows.Jobs.Count == 0 && legacyRows.NextAfterRow is null, "owner legacy summaries read the empty queue without workflow duplication");
            Check((await Command("jobs.workflow.answer", answers)).Success, "owner answers accepted through shared commands");
            var completed = await WaitAsync(() => workflows.Store.Get(request.RequestId), x => x.IsTerminal);
            Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("wordCount").GetInt32() == 3,
                "example plugin actually measures text and commits its completed result");
            Check((await Command("jobs.workflow.answer", answers)).Success,
                "lost answer response can be retried after completion without repeating effect");
            var page = workflows.Journal.ReadPage(request.ConversationId);
            Check(page.Events.Count(x => x.Type == CerebralEventTypes.ToolCall) == 1,
                "one committed effect across request and answer retries");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(pluginData, "review.db") }.ToString()))
            {
                connection.Open();
                using var record = connection.CreateCommand();
                record.CommandText = "SELECT COUNT(*), MIN(request_id), MIN(label) FROM review_receipts;";
                using var reader = record.ExecuteReader(); reader.Read();
                Check(reader.GetInt64(0) == 1 && reader.GetString(1) == request.RequestId && reader.GetString(2) == "Passage",
                    "the package owns one durable SQL domain record across exact answer retries");
            }
            Check(page.Events.Any(x => x.Projection.Human == HumanVisibility.Hidden && x.Projection.Model == ModelInclusion.Required),
                "private workflow instructions remain required model context independently of display");
            var recent = CerebralJson.Read<JournalPreviousPage>((await Command("jobs.workflow.events.previous", new { id = request.RequestId, limit = 2 }, profile.Id)).Data);
            Check(recent.Events.Select(x => x.Sequence).SequenceEqual(page.Events.TakeLast(2).Select(x => x.Sequence)) && recent.NextBeforeSequence is not null,
                "shared commands retrieve the latest bounded events without reading all saved history");
            Check(!(await Command("jobs.workflow.events.previous", new { id = request.RequestId }, "other")).Success,
                "backward event pages retain the same conversation ownership check");
            var discovery = await Command("sdk.discover", new { });
            Check(discovery.Success && discovery.Data.GetProperty("operations").GetArrayLength() > 0 &&
                new[] { "operations", "workflows", "commands" }.All(kind => discovery.Data.GetProperty(kind).EnumerateArray()
                    .All(x => x.TryGetProperty("id", out _) && x.TryGetProperty("description", out _) && !x.TryGetProperty("inputSchema", out _))),
                "discovery is concise and leaves executable schemas to sdk.describe");
            var description = await Command("sdk.describe", new { id = "jobs.workflow.submit" });
            Check(description.Success && description.Data.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("requestId", out _),
                "selected transport schema is available on demand");
            var next = await Command("jobs.workflow.submit", Request("cancel-two", "cancel-conversation"));
            Check(next.Success, "completed work releases queue capacity");
            Check((await Command("jobs.workflow.cancel", new { id = "cancel-two" })).Success,
                "cancel uses scheduler command and persists cancellation");
            Check(workflows.Store.Get("cancel-two").Status == WorkflowStatus.Cancelled, "cancelled queued/question work is terminal");
            await scheduler.StopAsync(default);
            Check(new WorkerWorkflows(store, config, events, [plugin], bindings).Store.Get(request.RequestId).Result?.GetProperty("wordCount").GetInt32() == 3,
                "new engine instance reads durable outcome without transcript replay");
            void AddSidebarChat(string id, string chat)
            {
                var row = workflows.Store.Submit(new WorkflowSubmission(id, chat, profile.Id,
                    ConversationWorkflowModule.DefinitionId, "1.0.0", chat, "PRIVATE-NAVIGATION",
                    WorkflowContract.EmptyObject, CerebralJson.Element(new { })));
                workflows.Store.Cancel(row.Id);
            }
            AddSidebarChat("old-pin", "pinned-sidebar-chat");
            AddSidebarChat("old-project-group", "grouped-sidebar-chat");
            AddSidebarChat("old-folder-group", "folder-sidebar-chat");
            store.Put("navigation", "sidebar", new NavigationState { Nodes = [
                new() { Id = "project", Kind = "project", Title = "Project" },
                new() { Id = "folder", Kind = "folder", Title = "Plain folder" },
                new() { Id = "project-folder", Kind = "folder", Title = "Nested project folder", ParentId = "project" },
                new() { Id = "pin", Kind = "conversation", Title = "Pinned", TargetId = "pinned-sidebar-chat", Pinned = true },
                new() { Id = "group", Kind = "conversation", Title = "Project grouped", TargetId = "grouped-sidebar-chat", ParentId = "project-folder" },
                new() { Id = "general-group", Kind = "conversation", Title = "Folder grouped", TargetId = "folder-sidebar-chat", ParentId = "folder" }
            ] });
            for (var index = 0; index < 45; index++) AddSidebarChat("fresh-sidebar-" + index, "fresh-sidebar-chat-" + index);
            var sidebarRecent = CerebralJson.Read<ConversationRecentPage>((await Command("conversations.recent", new { limit = 20, generalOnly = true })).Data);
            var sidebarProtected = CerebralJson.Read<ConversationRecentPage>((await Command("conversations.protected", new { limit = 200, snapshotRow = sidebarRecent.SnapshotRow })).Data);
            Check(sidebarRecent.Conversations.Count == 20 &&
                sidebarProtected.Conversations.Select(item => item.Id).ToHashSet().SetEquals(["pinned-sidebar-chat", "grouped-sidebar-chat"]),
                "server navigation preserves old pins and nested project-grouped chats beyond the newest20 ordinary rows");
            Check(workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, "grouped-sidebar-chat")?.ProjectId is null &&
                workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, "folder-sidebar-chat") is not null,
                "sidebar protection does not mutate knowledge binding or delete rolled-out plain-folder chats");
            store.Delete("navigation", "sidebar");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine("Workflow Worker integration checks passed (deterministic plugin, no model inference).");
    }

    public static async Task RunLiveAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? child = null;
        try
        {
            var dataPath = Path.Combine(root, "data");
            var store = new StateStore(dataPath); var config = new Configuration(store);
            config.Get();
            store.Put("agent", "reviewer", new AgentProfile { Id = "reviewer", Name = "Review", Capabilities = ["diagnostics"] });
            var pluginRoot = PreparePlugin(root);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var url = "http://127.0.0.1:" + port;
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
            async Task<CommandResult> Send(CommandRequest request, CancellationToken token)
            {
                using var response = await http.PostAsJsonAsync("/api/command", request, CerebralJson.Options, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<CommandResult>(CerebralJson.Options, token)
                    ?? throw new Exception("Empty command result.");
            }
            async Task<JsonElement> Call(string operation, object arguments)
            {
                var result = await Send(new CommandRequest(operation, CerebralJson.Element(arguments)), default);
                if (!result.Success) throw new Exception(operation + ": " + result.Error);
                return result.Data;
            }
            async Task Start()
            {
                var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                if (string.IsNullOrEmpty(dotnet)) dotnet = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "dotnet";
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                foreach (var value in new[] { typeof(CommandDispatcher).Assembly.Location, "--local-run", "--data", dataPath, "--url", url, "--service-plugins", pluginRoot }) start.ArgumentList.Add(value);
                child = Process.Start(start) ?? throw new Exception("Worker process did not start.");
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(25))
                {
                    if (child.HasExited) throw new Exception("Worker exited: " + await File.ReadAllTextAsync(Path.Combine(dataPath, "local-worker.log")));
                    try { using var health = await http.GetAsync("/health"); if (health.IsSuccessStatusCode) return; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                throw new TimeoutException("Worker did not become healthy.");
            }
            await Start();
            var diagnostics = await Call("diagnostics", new { });
            var reviewStatus = diagnostics.GetProperty("servicePlugins").EnumerateArray()
                .SingleOrDefault(item => item.GetProperty("id").GetString() == ExampleReviewPlugin.ModuleId);
            Check(reviewStatus.ValueKind == JsonValueKind.Object && reviewStatus.GetProperty("loaded").GetBoolean(),
                "real Worker loads the complete review package: " +
                (reviewStatus.ValueKind == JsonValueKind.Object && reviewStatus.TryGetProperty("error", out var loadError) ? loadError.GetString() : "missing status"));
            var attachmentBytes = Encoding.UTF8.GetBytes("A durable review note imported separately from the visible request.");
            await using var attachmentSource = new MemoryStream(attachmentBytes, writable: false);
            var transfer = new AttachmentTransferClient(new("review-attachment-one", "reviewer", "http-conversation",
                "review-note.txt", attachmentSource.Length), Send);
            var attachment = (await transfer.UploadAsync(attachmentSource)).Attachment
                ?? throw new Exception("The real Worker did not return a committed text attachment reference.");
            Check(attachment.OriginalName == "review-note.txt" && attachment.MediaType == "text/plain" && attachment.Length == attachmentBytes.Length,
                "real Worker imports the immutable text attachment before workflow admission");
            var request = Request("http-one", "http-conversation", attachment);
            var submitted = CerebralJson.Read<WorkflowRun>(await Call("jobs.workflow.submit", request));
            Check(submitted.Submission.AcceptedContent is [FileContentPart { Attachment: var accepted }] &&
                accepted.Id == attachment.Id && accepted.Revision == attachment.Revision,
                "workflow admission retains the imported file as typed supplemental content outside UserText");
            var waiting = await WaitAsync(async () => CerebralJson.Read<WorkflowRun>(await Call("jobs.workflow.get", new { id = submitted.Id })), x => x.Questions is not null);
            Check(waiting.Questions!.Fields.Count == 2, "real Worker HTTP route reaches persisted questions");
            // Deliberately terminate only this test-owned process to exercise crash/reconnect persistence.
            child!.Kill(entireProcessTree: true); await child.WaitForExitAsync(); child.Dispose(); child = null;
            await Start();
            var afterRestart = CerebralJson.Read<WorkflowRun>(await Call("jobs.workflow.get", new { id = request.RequestId }));
            Check(afterRestart.Questions?.Id == waiting.Questions.Id, "real Worker restart retains the exact pending form");
            Check(afterRestart.Submission.AcceptedContent is [FileContentPart { Attachment: var restarted }] &&
                restarted.Id == attachment.Id && restarted.Revision == attachment.Revision,
                "real Worker restart retains the accepted attachment coordinate without embedding its bytes");
            await Call("jobs.workflow.submit", request);
            var answer = Answer(afterRestart, "http-answer");
            await Call("jobs.workflow.answer", answer);
            var completed = await WaitAsync(async () => CerebralJson.Read<WorkflowRun>(await Call("jobs.workflow.get", new { id = request.RequestId })), x => x.IsTerminal);
            Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("wordCount").GetInt32() == 3,
                "real restarted Worker executes loaded plugin and returns measured result");
            await Call("jobs.workflow.answer", answer);
            var page = CerebralJson.Read<JournalPage>(await Call("jobs.workflow.events", new { id = request.RequestId, limit = 200 }));
            Check(page.Events.Count(x => x.Type == CerebralEventTypes.ToolCall) == 1,
                "real HTTP retries and restart preserve one effect identity");
            await Call("service.stop-local", new { });
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child!.WaitForExitAsync(stopTimeout.Token);
            Check(child.ExitCode == 0, "real local Worker stops cleanly after accepted work completes");
            var database = Directory.GetFiles(dataPath, "review.db", SearchOption.AllDirectories).Single();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                connection.Open(); using var record = connection.CreateCommand();
                record.CommandText = "SELECT COUNT(*), MIN(request_id) FROM review_receipts;";
                using var reader = record.ExecuteReader(); reader.Read();
                Check(reader.GetInt64(0) == 1 && reader.GetString(1) == request.RequestId,
                    "the restarted real plugin retains exactly one package-owned SQL record");
            }
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } child.Dispose(); }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Real Worker process / loopback HTTP / SQLite / DLL module checks passed. No LLM or UI claim.");
    }

    private static SubmitWorkflowRequest Request(string id, string conversation, AttachmentReference? attachment = null) => new(id, conversation, "reviewer",
        ExampleReviewPlugin.DefinitionId, "1.0.0", "Measure this passage.", "Internal workflow assignment: preserve the original passage.",
        CerebralJson.Element(new { text = "one two three" }))
        { AcceptedContent = attachment is null ? [] : [new FileContentPart(attachment)] };
    private static WorkflowAnswerRequest Answer(WorkflowRun run, string id) => new(id, run.Id, run.Questions!.Id,
        run.Questions.Revision, new Dictionary<string, string> { ["label"] = "Passage", ["style"] = "concise" });
    private static string PreparePlugin(string root)
    {
        var plugins = Path.Combine(root, "plugins"); var package = Path.Combine(plugins, "example.review");
        Directory.CreateDirectory(package);
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Pegasus.Cerebral.slnx"))) cursor = cursor.Parent;
        var output = cursor is null ? null : Path.Combine(cursor.FullName, "examples", "Cerebral.ReviewPlugin", "bin", "Release", "net10.0");
        if (output is null || !File.Exists(Path.Combine(output, "Cerebral.ReviewPlugin.dll")))
            throw new FileNotFoundException("Build the Release review SDK example before the live Worker acceptance.");
        foreach (var source in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(output, source);
            var destination = Path.Combine(package, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        return plugins;
    }
    private static Task<WorkflowRun> WaitAsync(Func<WorkflowRun> read, Func<WorkflowRun, bool> condition) => WaitAsync(() => Task.FromResult(read()), condition);
    private static async Task<WorkflowRun> WaitAsync(Func<Task<WorkflowRun>> read, Func<WorkflowRun, bool> condition)
    {
        var watch = Stopwatch.StartNew(); WorkflowRun? last = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(12)) { last = await read(); if (condition(last)) return last; await Task.Delay(50); }
        throw new TimeoutException($"Workflow did not reach expected state. Last state: {last?.Status}; waiting={last?.WaitingReason}; error={last?.Error}; effect={last?.Effect?.State}:{last?.Effect?.Error}");
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    private sealed class NoModelExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("No model execution is expected in this test.");
    }
}
