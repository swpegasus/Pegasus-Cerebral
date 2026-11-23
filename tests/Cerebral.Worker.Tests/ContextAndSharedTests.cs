using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ContextAndSharedTests
{
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-context-shared-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var local = new ContextHarness(Path.Combine(root, "local"));
            await CheckExclusionAsync(local);
            await CheckSharedAsync(local);
            await CheckLocalControlAsync(local);
            await using var remote = new ContextHarness(Path.Combine(root, "remote"));
            await CheckRemoteSharedAsync(local, remote);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckLocalControlAsync(ContextHarness fixture)
    {
        var installed = await fixture.Command("service.stop-local", new { });
        Check(!installed.Success && !fixture.Commands.Health().LocalRun, "local stop command cannot stop a regular or installed worker");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new LocalRunControl(true) { Stop = () => stopped.TrySetResult() };
        var commands = fixture.LocalCommands(lifecycle);
        var denied = await commands.ExecuteAsync(new("service.stop-local", CerebralJson.Element(new { }), fixture.Profile.Id));
        Check(!denied.Success && !stopped.Task.IsCompleted, "ordinary profile grants cannot stop a local worker");
        fixture.Store.Put("job", "local-stop-active", new AgentJob { Id = "local-stop-active", Status = "running" });
        var busy = await commands.ExecuteAsync(new("service.stop-local", CerebralJson.Element(new { })));
        Check(!busy.Success && !stopped.Task.IsCompleted, "local stop preserves active assignments and asks to finish or cancel first");
        fixture.Store.Delete("job", "local-stop-active");
        var accepted = await commands.ExecuteAsync(new("service.stop-local", CerebralJson.Element(new { })));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(accepted.Success && commands.Health().LocalRun, "idle local stop schedules graceful shutdown after accepting the command");
    }

    private static async Task CheckExclusionAsync(ContextHarness fixture)
    {
        var session = new TaskSession
        {
            Id = "context-session", ProfileId = fixture.Profile.Id, Revision = 10,
            Checkpoint = "OLD-CHECKPOINT", CheckpointThroughSequence = 7,
            RemoteSessionIds = new() { ["remote"] = "old-remote-context" },
            Messages =
            [
                new() { Sequence = 1, Role = "user", Content = "KEEP-FIRST" },
                new() { Sequence = 2, Role = "assistant", Content = "KEEP-ANSWER" },
                new() { Sequence = 3, Role = "user", Content = "REMOVE-REQUEST" },
                new() { Sequence = 4, Role = "assistant", Content = "REMOVE-PLAN" },
                new() { Sequence = 5, Role = "user", Origin = "tool", ToolName = "file.read", Content = "REMOVE-TOOL" },
                new() { Sequence = 6, Role = "user", Origin = "helper", Content = "REMOVE-HELPER" },
                new() { Sequence = 7, Role = "assistant", Content = "REMOVE-ANSWER" },
                new() { Sequence = 8, Role = "user", Content = "KEEP-NEXT" },
                new() { Sequence = 9, Role = "assistant", Content = "KEEP-NEXT-ANSWER" }
            ]
        };
        fixture.Store.Put("session", session.Id, session);
        var result = await fixture.Command("sessions.exclude", new { id = session.Id, sequence = 3 });
        Check(result.Success, "context exclusion command succeeds on an idle session");
        var saved = fixture.Store.Get<TaskSession>("session", session.Id)!;
        Check(saved.Messages.Where(x => x.ExcludedFromContext).Select(x => x.Sequence).SequenceEqual([3, 4, 5, 6, 7])
            && saved.Messages.Count == 9, "exclude removes a user turn and its tool/helper replies until the next actual user turn without deleting history");
        Check(saved.Checkpoint is null && saved.CheckpointThroughSequence == 0 && saved.RemoteSessionIds.Count == 0 && saved.Revision == 11,
            "exclude invalidates derived checkpoint and remote session bindings");
        var assembled = AgentEngine.Assemble(saved, "SYSTEM");
        Check(assembled.Select(x => x.Content).SequenceEqual(["SYSTEM", "KEEP-FIRST", "KEEP-ANSWER", "KEEP-NEXT", "KEEP-NEXT-ANSWER"]),
            "working context omits excluded content and the invalidated checkpoint");

        fixture.Store.Put("job", "active", new AgentJob { Id = "active", ProfileId = fixture.Profile.Id, SessionId = session.Id, Status = "running" });
        var rejected = await fixture.Command("sessions.exclude", new { id = session.Id, sequence = 1 });
        Check(!rejected.Success && fixture.Store.Get<TaskSession>("session", session.Id)!.Revision == saved.Revision,
            "active-session exclusion is rejected without changing saved context");
        fixture.Store.Delete("job", "active");
        var one = await fixture.Command("sessions.exclude", new { id = session.Id, sequence = 8, includeFollowing = false });
        saved = fixture.Store.Get<TaskSession>("session", session.Id)!;
        Check(one.Success && saved.Messages.Single(x => x.Sequence == 8).ExcludedFromContext && !saved.Messages.Single(x => x.Sequence == 9).ExcludedFromContext,
            "single-message exclusion preserves following messages when explicitly requested");
        var outsider = new AgentProfile { Id = "outsider", Capabilities = ["shared.read"] }; fixture.Store.Put("agent", outsider.Id, outsider);
        var denied = await fixture.Commands.ExecuteAsync(new("sessions.exclude", CerebralJson.Element(new { id = session.Id, sequence = 1 }), outsider.Id));
        Check(!denied.Success && !fixture.Store.Get<TaskSession>("session", session.Id)!.Messages[0].ExcludedFromContext,
            "a different agent cannot exclude another profile's conversation");
    }

    private static async Task CheckSharedAsync(ContextHarness fixture)
    {
        var written = await fixture.Tool("shared.write", new { path = "notes.txt", content = "alpha\r\nbeta" });
        var path = Path.Combine(fixture.SharedDirectory, "notes.txt");
        Check(written.Success && await File.ReadAllTextAsync(path) == "alpha\r\nbeta", "shared relative write saves the actual file under the configured shared root");
        var listing = await fixture.Tool("shared.list", new { path = "." });
        using var listed = JsonDocument.Parse(listing.Content);
        Check(listing.Success && listed.RootElement.GetProperty("entries").EnumerateArray().Any(x => x.GetProperty("path").GetString() == "notes.txt")
            && !listing.Content.Contains(fixture.SharedDirectory, StringComparison.Ordinal), "shared listing returns portable relative paths");
        var read = await fixture.Tool("shared.read", new { path = "notes.txt" });
        using var content = JsonDocument.Parse(read.Content);
        Check(read.Success && content.RootElement.GetProperty("path").GetString() == "notes.txt" && content.RootElement.GetProperty("content").GetString() == "alpha\r\nbeta",
            "shared read returns actual content with a relative file identity");
        var collision = await fixture.Tool("shared.write", new { path = "notes.txt", content = "wrong" });
        Check(!collision.Success && await File.ReadAllTextAsync(path) == "alpha\r\nbeta", "shared collision preserves the existing file by default");
        foreach (var invalid in new[] { "../escape.txt", "nested/../../escape.txt", "/absolute.txt", "C:\\outside.txt" })
        {
            var escaped = await fixture.Tool("shared.write", new { path = invalid, content = "escape" });
            Check(!escaped.Success, "shared write rejects non-confined path: " + invalid);
        }
        var readOnly = CerebralJson.Read<AgentProfile>(CerebralJson.Element(fixture.Profile)); readOnly.Capabilities = ["shared.read"];
        var denied = await fixture.Tools.ExecuteAsync(new("shared.write", CerebralJson.Element(new { path = "denied.txt", content = "x" })), new(readOnly, "session", "job"), default);
        Check(!denied.Success && !File.Exists(Path.Combine(fixture.SharedDirectory, "denied.txt")), "shared.write capability denial prevents a filesystem change");
        var edit = await fixture.Tool("shared.edit", new { path = "notes.txt", oldText = "alpha", newText = "gamma", expectedSha256 = content.RootElement.GetProperty("sha256").GetString() });
        Check(edit.Success && await File.ReadAllTextAsync(path) == "gamma\r\nbeta", "shared editing preserves encoding/newlines through the ordinary file handler");
    }

    private static async Task CheckRemoteSharedAsync(ContextHarness caller, ContextHarness host)
    {
        // Real HTTP and both real tool dispatchers; the fixture only supplies transport hosting.
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = new List<string>();
        app.MapPost("/api/command", async (HttpContext context) =>
        {
            var command = await context.Request.ReadFromJsonAsync<CommandRequest>(CerebralJson.Options, context.RequestAborted) ?? throw new IOException("Missing command.");
            lock (requests) requests.Add(command.Operation);
            await context.Response.WriteAsJsonAsync(await host.Commands.ExecuteAsync(command, context.RequestAborted), CerebralJson.Options, context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            var settings = caller.Configuration.Get(); settings.SharedFilesWorkerId = "remote";
            settings.Workers.Add(new() { Id = "remote", Url = app.Urls.Single(), AccessKey = "fixture" }); caller.Configuration.Save(settings);
            var write = await caller.Tool("shared.write", new { path = "remote.txt", content = "written through remote shared host" });
            Check(write.Success && File.Exists(Path.Combine(host.SharedDirectory, "remote.txt")) && !File.Exists(Path.Combine(caller.SharedDirectory, "remote.txt")),
                "remote shared write uses the configured host over loopback HTTP without writing locally");
            var read = await caller.Tool("shared.read", new { path = "remote.txt" });
            using var content = JsonDocument.Parse(read.Content);
            Check(read.Success && content.RootElement.GetProperty("path").GetString() == "remote.txt" && content.RootElement.GetProperty("content").GetString() == "written through remote shared host",
                "remote shared read returns portable path and actual remote content");
            Check(requests.Contains("health") && requests.Contains("shared.call"), "remote shared route probes service identity before invoking shared commands");
            var restricted = host.Store.Get<AgentProfile>("agent", host.Profile.Id)!; restricted.Capabilities = ["shared.read"]; host.Store.Put("agent", restricted.Id, restricted);
            var denied = await caller.Tool("shared.write", new { path = "remote-denied.txt", content = "x" });
            Check(!denied.Success && !File.Exists(Path.Combine(host.SharedDirectory, "remote-denied.txt")), "remote shared host enforces its own saved profile grants");
        }
        finally { await app.StopAsync(); }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("These tests do not execute a model.");
    }
    private sealed class ContextHarness : IAsyncDisposable
    {
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ToolDispatcher Tools { get; }
        public CommandDispatcher Commands { get; }
        public AgentProfile Profile { get; }
        public string SharedDirectory { get; }
        private readonly ModelRuntime runtime;
        private readonly JobScheduler scheduler;
        public ContextHarness(string path)
        {
            Store = new(path); Configuration = new(Store); var settings = Configuration.Get(); SharedDirectory = settings.SharedDirectory; Configuration.Save(settings);
            var events = new EventHub(); runtime = new(Store, Configuration.Get, events); Tools = new(Configuration.Get);
            scheduler = new(Store, Configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            Commands = new(Store, Configuration, runtime, scheduler, Tools, events);
            Profile = new() { Id = "shared-agent", Name = "Shared test agent", ModelId = "unused", AllowedTools = ["shared.list", "shared.read", "shared.write", "shared.edit", "shared.mkdir"], Capabilities = ["shared.read", "shared.write"] };
            Store.Put("agent", Profile.Id, Profile);
        }
        public CommandDispatcher LocalCommands(LocalRunControl control) => new(Store, Configuration, runtime, scheduler, Tools, new EventHub(), control);
        public Task<CommandResult> Command(string operation, object arguments) => Commands.ExecuteAsync(new(operation, CerebralJson.Element(arguments)));
        public Task<ToolResult> Tool(string name, object arguments) => Tools.ExecuteAsync(new(name, CerebralJson.Element(arguments)), new(Profile, "session", "job"), default);
        public async ValueTask DisposeAsync() { scheduler.Dispose(); await runtime.DisposeAsync(); await Tools.DisposeAsync(); }
    }
}
