using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class PeerAccessTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-peer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = new PeerHarness(Path.Combine(root, "access"), maximumSlots: 2);
            var token = host.Peers.Create(CerebralJson.Element(new { name = "Brother's 5080", profileIds = new[] { "shared" }, maxConcurrentJobs = 2 }));
            var stored = host.Store.List<PeerAccessGrant>("peer-grant").Single();
            var storedJson = JsonSerializer.Serialize(stored, CerebralJson.Options);
            Check(!storedJson.Contains(token.Token, StringComparison.Ordinal) && stored.SecretDigest.Length > 20,
                "receiver stores only a peer secret digest");
            Check(host.Peers.TryAuthenticate(token.Token, out var authenticated) && authenticated!.Id == token.GrantId,
                "show-once token authenticates its exact grant");
            Check(!host.Peers.TryAuthenticate(token.Token[..^1] + (token.Token[^1] == 'A' ? 'B' : 'A'), out _),
                "modified peer token is rejected");

            var catalogResult = await host.Peers.ExecuteAsync(authenticated!, Command("peer.catalog"), default);
            var catalog = CerebralJson.Read<PeerCatalog>(catalogResult.Data);
            Check(catalogResult.Success && catalog.Agents.Count == 1 && catalog.Agents[0].Id == "shared" &&
                catalog.Models.Count == 1 && catalog.Models[0].Id == "fixture",
                "peer catalog exposes only shared receiver contributions");
            var admin = await host.Peers.ExecuteAsync(authenticated!, Command("settings.get"), default);
            Check(!admin.Success, "peer route denies owner configuration commands");

            var delegated = host.Store.Get<AgentProfile>("agent", "shared")!;
            delegated = CerebralJson.Read<AgentProfile>(CerebralJson.Element(delegated));
            delegated.AllowedRoots = [];
            var submission = new SubmitJobRequest
            {
                RequestId = "peer-owned", ProfileId = "shared", Prompt = "paired work",
                DelegatedProfile = delegated, InitialMessages = []
            };
            var submitted = await host.Peers.ExecuteAsync(authenticated!, Command("jobs.submit", submission), default);
            var peerJob = CerebralJson.Read<AgentJob>(submitted.Data);
            Check(submitted.Success && peerJob.PeerGrantId == token.GrantId,
                "peer submission is durably attributed to the authenticated grant");

            var second = host.Peers.Create(CerebralJson.Element(new { name = "Other peer", profileIds = new[] { "shared" } }));
            host.Peers.TryAuthenticate(second.Token, out var other);
            var crossed = await host.Peers.ExecuteAsync(other!, Command("jobs.get", new { id = peerJob.Id }), default);
            var absent = await host.Peers.ExecuteAsync(other!, Command("jobs.get", new { id = "absent" }), default);
            Check(!crossed.Success && crossed.Error == absent.Error && crossed.Error == PeerAccessControl.UnavailableAssignmentMessage,
                "one peer cannot read or infer another peer's assignment identity");

            host.Store.Put("agent", "administrator", new AgentProfile
            {
                Id = "administrator", Name = "administrator", ModelId = "fixture", Role = "helper",
                Capabilities = ["administration"], AllowedRoots = [], AllowedTools = []
            });
            var dispatcher = new CommandDispatcher(host.Store, host.Configuration, host.Runtime, host.Scheduler,
                host.Tools, host.Events, peerAccess: host.Peers);
            var agentGrantAttempt = await dispatcher.ExecuteAsync(new CommandRequest("peer.grants.list",
                CerebralJson.Element(new { }), "administrator"));
            Check(!agentGrantAttempt.Success, "peer grants remain owner-interface-only even for an administration-capable agent");

            var drained = host.Peers.Drain(token.GrantId, true);
            var drainSubmit = await host.Peers.ExecuteAsync(authenticated!, Command("jobs.submit", new SubmitJobRequest
            {
                RequestId = "peer-after-drain", ProfileId = "shared", Prompt = "new work",
                DelegatedProfile = delegated, InitialMessages = []
            }), default);
            Check(drained.Draining && !drainSubmit.Success, "draining keeps existing receipts available and rejects new work");
            host.Peers.Drain(token.GrantId, false);
            var rotated = host.Peers.Rotate(token.GrantId);
            Check(!host.Peers.TryAuthenticate(token.Token, out _) && host.Peers.TryAuthenticate(rotated.Token, out authenticated),
                "rotation invalidates the previous secret without changing the grant");
            var metrics = await host.Peers.MetricsAsync();
            Check(metrics.Host.Product == "Pegasus Cerebral" && metrics.Queue.MaximumSlots == 2 && metrics.SampledAt != default,
                "peer metrics report bounded host, queue and resource state");
            await VerifyHttpRouteAsync(host, rotated.Token, Check);
            var revoked = host.Peers.Revoke(token.GrantId);
            Check(revoked.RevokedAt is not null && !host.Peers.TryAuthenticate(rotated.Token, out _),
                "revocation immediately closes the peer credential");

            await using var priority = new PeerHarness(Path.Combine(root, "priority"), maximumSlots: 1);
            var priorityToken = priority.Peers.Create(CerebralJson.Element(new { name = "Priority peer", profileIds = new[] { "shared" } }));
            priority.Peers.TryAuthenticate(priorityToken.Token, out var priorityGrant);
            var priorityDelegated = CerebralJson.Read<AgentProfile>(CerebralJson.Element(priority.Store.Get<AgentProfile>("agent", "shared")!));
            priorityDelegated.AllowedRoots = [];
            priority.Scheduler.SubmitPeer(new() { RequestId = "older-peer", ProfileId = "shared", Prompt = "peer", DelegatedProfile = priorityDelegated, InitialMessages = [] }, priorityGrant!);
            priority.Scheduler.Submit(new() { RequestId = "newer-owner", ProfileId = "owner", Prompt = "owner" });
            await priority.StartAsync();
            await Until(() => priority.Scheduler.Get("newer-owner").Status == "completed" && priority.Scheduler.Get("older-peer").Status == "completed", "owner-priority jobs");
            Check(priority.Executor.Started.Take(2).SequenceEqual(new[] { "newer-owner", "older-peer" }),
                "owner work is admitted before an older peer assignment");

            await using var borrowing = new PeerHarness(Path.Combine(root, "borrowing"), maximumSlots: 2, hold: true);
            var borrowToken = borrowing.Peers.Create(CerebralJson.Element(new { name = "Borrowing peer", profileIds = new[] { "shared" }, maxConcurrentJobs = 2 }));
            borrowing.Peers.TryAuthenticate(borrowToken.Token, out var borrowGrant);
            var borrowDelegated = CerebralJson.Read<AgentProfile>(CerebralJson.Element(borrowing.Store.Get<AgentProfile>("agent", "shared")!));
            borrowDelegated.AllowedRoots = [];
            borrowing.Scheduler.SubmitPeer(new() { RequestId = "borrow-one", ProfileId = "shared", Prompt = "one", DelegatedProfile = borrowDelegated, InitialMessages = [] }, borrowGrant!);
            borrowing.Scheduler.SubmitPeer(new() { RequestId = "borrow-two", ProfileId = "shared", Prompt = "two", DelegatedProfile = borrowDelegated, InitialMessages = [] }, borrowGrant!);
            await borrowing.StartAsync();
            await Until(() => borrowing.Scheduler.QueueMetrics().PeerRunning == 2, "idle owner slot borrowing");
            Check(borrowing.Scheduler.QueueMetrics().PeerRunning == 2,
                "peers borrow the reserved owner slot while owner work is idle");
            borrowing.Scheduler.Submit(new() { RequestId = "owner-arrived", ProfileId = "owner", Prompt = "owner arrived" });
            await Until(() => borrowing.Scheduler.Get("owner-arrived").Status == "completed", "owner after peer safe-boundary yield");
            Check(borrowing.Executor.Started.Contains("owner-arrived") && borrowing.Scheduler.QueueMetrics().OwnerQueued == 0,
                "queued owner work causes paired work to yield and takes the released slot");

            await using var expiring = new PeerHarness(Path.Combine(root, "expiry"), maximumSlots: 1, hold: true);
            var expiringToken = expiring.Peers.Create(CerebralJson.Element(new
                { name = "Temporary peer", profileIds = new[] { "shared" }, expiresAt = DateTimeOffset.UtcNow.AddSeconds(1).ToString("O") }));
            expiring.Peers.TryAuthenticate(expiringToken.Token, out var expiringGrant);
            var expiringProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(expiring.Store.Get<AgentProfile>("agent", "shared")!));
            expiringProfile.AllowedRoots = [];
            expiring.Scheduler.SubmitPeer(new() { RequestId = "expires-running", ProfileId = "shared", Prompt = "temporary", DelegatedProfile = expiringProfile, InitialMessages = [] }, expiringGrant!);
            await expiring.StartAsync();
            await Until(() => expiring.Scheduler.Get("expires-running").Status == "running", "expiring peer started");
            await Until(() => expiring.Scheduler.Get("expires-running").Status == "cancelled", "expired peer cancellation");
            Check(expiring.Scheduler.Get("expires-running").Status == "cancelled",
                "grant expiry cancels already accepted peer work instead of leaving it inaccessible");

            await VerifyActualWorkerAsync(Path.Combine(root, "actual-worker"), Check);

            Console.WriteLine($"RESULT: {assertions} peer-access assertions passed. HTTP transport, SQLite, token hashing and scheduler admission are real; model execution is deterministic fake evidence.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static CommandRequest Command(string operation, object? arguments = null) =>
        new(operation, CerebralJson.Element(arguments ?? new { }));

    private static async Task VerifyHttpRouteAsync(PeerHarness host, string peerToken, Action<bool, string> check)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/api/peer/command", async (HttpContext context, CommandRequest request, CancellationToken token) =>
        {
            if (!host.Peers.TryAuthenticate(context.Request.Headers[PeerAccessControl.HeaderName].FirstOrDefault(), out var grant))
                return Results.Unauthorized();
            return Results.Json(await host.Peers.ExecuteAsync(grant!, request, token), CerebralJson.Options);
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = host.Configuration.Get();
            settings.Workers.Add(new WorkerRegistration { Id = "loop-peer", Name = "Loopback peer", Url = address, PeerToken = peerToken });
            host.Configuration.Save(settings);
            using var client = new CerebralClient(settings.Workers.Single(x => x.Id == "loop-peer"));
            var identity = await client.GetAsync<PeerHostInfo>("health");
            var metrics = await client.GetAsync<PeerWorkerMetrics>("peer.metrics");
            check(identity.Product == "Pegasus Cerebral" && metrics.Host.Protocol == 1,
                "CerebralClient uses the peer HTTP route and authenticates with the scoped header");

            var dispatcher = new CommandDispatcher(host.Store, host.Configuration, host.Runtime, host.Scheduler,
                host.Tools, host.Events, peerAccess: host.Peers);
            var probe = await dispatcher.ExecuteAsync(Command("workers.probe", new { id = "loop-peer" }));
            var result = CerebralJson.Read<WorkerProbeResult>(probe.Data);
            check(probe.Success && result.Host.Name == identity.Name && result.RoundTripLatencyMilliseconds >= 0 &&
                result.Catalog is { Agents.Count: 1, Models.Count: 1 } && result.Catalog.Models[0].Id == "fixture",
                "owner probe measures a real peer command round trip and returns peer-safe resources and availability");

            using var invalid = new CerebralClient(new WorkerRegistration
                { Id = "invalid", Url = address, PeerToken = peerToken[..^1] + (peerToken[^1] == 'A' ? 'B' : 'A') });
            var rejected = false;
            try { await invalid.GetAsync<PeerHostInfo>("health"); }
            catch (HttpRequestException) { rejected = true; }
            check(rejected, "peer HTTP route rejects an invalid token before dispatch");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task VerifyActualWorkerAsync(string dataPath, Action<bool, string> check)
    {
        Directory.CreateDirectory(dataPath);
        var store = new StateStore(dataPath);
        var configuration = new Configuration(store);
        var settings = configuration.Get();
        settings.SharedDirectory = Path.Combine(dataPath, "shared");
        settings.ModelDirectory = Path.Combine(dataPath, "models");
        settings.ProjectsDirectory = Path.Combine(dataPath, "projects");
        configuration.Save(settings);
        store.Put("agent", "shared", new AgentProfile
        {
            Id = "shared", Name = "Shared fixture", Purpose = "Executable peer acceptance", Role = "helper",
            ModelId = "fixture", WorkerId = "local", ModelWorkerId = "local", AllowedRoots = [], AllowedTools = [], Capabilities = ["diagnostics"]
        });

        int port;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var url = $"http://127.0.0.1:{port}";
        var workerDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Cerebral.Worker", "bin", "Release", "net10.0", "Cerebral.Worker.dll"));
        if (!File.Exists(workerDll)) throw new FileNotFoundException("Build the Release Worker before peer acceptance.", workerDll);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet))
            dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
            throw new FileNotFoundException("The local .NET host executable is unavailable.", dotnet);
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(workerDll)!
        };
        start.ArgumentList.Add(workerDll); start.ArgumentList.Add("--data"); start.ArgumentList.Add(dataPath);
        start.ArgumentList.Add("--url"); start.ArgumentList.Add(url); start.ArgumentList.Add("--local-run");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The disposable Worker process did not start.");
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(1) };
            var ready = false;
            for (var attempt = 0; attempt < 100 && !ready; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("The disposable Worker exited before becoming ready. Inspect " + Path.Combine(dataPath, "local-worker.log"));
                try { using var response = await http.GetAsync("/health"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (!ready) await Task.Delay(50);
            }
            if (!ready) throw new TimeoutException("The disposable Worker did not become ready.");

            using var owner = new CerebralClient(new WorkerRegistration { Id = "actual-owner", Url = url });
            var issued = await owner.GetAsync<PeerGrantToken>("peer.grants.create",
                new { name = "Actual endpoint", profileIds = new[] { "shared" }, maxConcurrentJobs = 1 });
            using var peer = new CerebralClient(new WorkerRegistration { Id = "actual-peer", Url = url, PeerToken = issued.Token });
            var identity = await peer.GetAsync<PeerHostInfo>("health");
            var metrics = await peer.GetAsync<PeerWorkerMetrics>("peer.metrics");
            var catalog = await peer.GetAsync<PeerCatalog>("peer.catalog");
            check(identity.Product == "Pegasus Cerebral" && metrics.Host.Name == settings.Name && catalog.Agents.Single().Id == "shared",
                "the built Worker executable accepts a real scoped peer route without exposing the owner command surface");
            await owner.CommandAsync("service.stop-local");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(stop.Token);
            check(process.ExitCode == 0, "the disposable Worker exits cleanly through its owner-only stop command");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task Until(Func<bool> predicate, string message, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.ElapsedMilliseconds > milliseconds) throw new Exception("TIMEOUT: " + message); await Task.Delay(25); }
    }

    private sealed class PeerExecutor(bool hold) : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public ConcurrentQueue<string> Started { get; } = new();
        public async Task RunAsync(AgentJob job, CancellationToken token)
        {
            Started.Enqueue(job.Id);
            if (hold && job.PeerGrantId is not null)
            {
                while (!token.IsCancellationRequested && ForegroundPending?.Invoke() != true) await Task.Delay(20, token);
                if (ForegroundPending?.Invoke() == true)
                {
                    job.Status = "queued"; job.WaitingReason = "Owner work arrived; paired work yielded at a safe test boundary."; return;
                }
            }
            job.Result = "done";
        }
    }

    private sealed class PeerHarness : IAsyncDisposable
    {
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public EventHub Events { get; } = new();
        public ModelRuntime Runtime { get; }
        public PeerExecutor Executor { get; }
        public ToolDispatcher Tools { get; }
        public JobScheduler Scheduler { get; }
        public PeerAccessControl Peers { get; }
        private bool started;

        public PeerHarness(string path, int maximumSlots, bool hold = false)
        {
            Store = new(path); Configuration = new(Store);
            var settings = Configuration.Get(); settings.MaxConcurrentJobs = maximumSlots; settings.ReservedOwnerSlots = 1; settings.PeerQueueCapacity = 8; Configuration.Save(settings);
            Runtime = new(Store, Configuration.Get, Events); Tools = new(Configuration.Get); Executor = new(hold);
            Scheduler = new(Store, Configuration, Executor, Runtime, Events, NullLogger<JobScheduler>.Instance);
            Peers = new(Store, Configuration, Scheduler);
            var fixture = Path.Combine(path, "fixture.gguf"); File.WriteAllBytes(fixture, "GGUF"u8.ToArray());
            Store.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = fixture, DeclaredType = "model", MetadataSource = "fixture" });
            Store.Put("agent", "owner", Profile("owner", path));
            Store.Put("agent", "shared", Profile("shared", path));
        }

        private static AgentProfile Profile(string id, string path) => new()
        {
            Id = id, Name = id, ModelId = "fixture", Role = id == "owner" ? "lead" : "helper",
            AllowedRoots = [path], AllowedTools = ["file.read"], Capabilities = ["filesystem.read"], MaxSteps = 10, MaxSeconds = 30
        };

        public async Task StartAsync() { if (started) return; started = true; await Scheduler.StartAsync(default); }
        public async ValueTask DisposeAsync()
        {
            if (started) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await Scheduler.StopAsync(timeout.Token); }
            Scheduler.Dispose(); await Runtime.DisposeAsync(); await Tools.DisposeAsync();
        }
    }
}
