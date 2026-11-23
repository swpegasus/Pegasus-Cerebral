using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Controlled races in real isolated transports/SQLite. No synthetic successful launch is a provider claim.</summary>
public static class OwnerModelSnapshotRaceTests
{
    private const string WorkerId = "123456781234123412341234567890ab";
    private const string ModelId = "snapshot-model";
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options) { WriteIndented = false };

    public static async Task<int> RunAsync(string fixtureRoot)
    {
        var parent = Path.GetFullPath(fixtureRoot);
        var allowed = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\.tmp\core-10000000-0000-4000-8000-000000000001\snapshot-candidate");
        if (!parent.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Race fixtures must stay beneath the exact candidate.");
        var root = Path.Combine(parent, "owner-snapshot-races-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
        try
        {
            foreach (var transport in new[] { "http", "pipe" })
            foreach (var change in new[] { "worker", "controller", "key" })
            {
                await using var host = new OwnerModelSnapshotPathTests.Fixture(Path.Combine(root, transport + "-" + change), "source");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await host.StartAsync(pipe: true, deadline.Token);
                var lifecycle = Lifecycle(host.Runtime);
                await lifecycle.WaitAsync(deadline.Token);
                Task<CommandResult> pending;
                var admitted = host.Commands.OwnerSnapshotVerifiedAdmissions;
                using var client = new CerebralClient(host.PipeEndpoint!);
                try
                {
                    pending = transport == "pipe" ? client.CommandAsync(OwnerModelSnapshotService.Operation, Request(host.ControllerId).Arguments,
                        cancellationToken: deadline.Token) : HttpResultAsync(host, Request(host.ControllerId), deadline.Token);
                    await UntilAsync(() => host.Commands.OwnerSnapshotVerifiedAdmissions > admitted, deadline.Token);
                    Check(host.Store.OwnerSnapshotModelRowsRead == 0, "authenticated race waiter has not enumerated models");
                    ChangeConfiguration(host.Store, change);
                }
                catch { lifecycle.Release(); throw; }
                var before = host.Capture(); // The deliberate fixture mutation is excluded from request effects.
                lifecycle.Release();
                var result = await pending.WaitAsync(deadline.Token);
                Check(!result.Success && result.Error == "owner_configuration_changed",
                    transport + " rejects captured " + change + " drift before model reads");
                host.VerifyNoEffects(before, expectModelRead: false, Check, transport + " owner drift");
                if (change == "key")
                {
                    before = host.Capture();
                    var denied = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(Request(host.ControllerId)), host.OwnerKey, deadline.Token);
                    Check(!denied.Result!.Success && denied.Status == System.Net.HttpStatusCode.Unauthorized, "rotated prior key cannot authenticate a new HTTP request");
                    host.VerifyNoEffects(before, false, Check, "old credential after rotation");
                }
            }

            await using (var host = new OwnerModelSnapshotPathTests.Fixture(Path.Combine(root, "cancel"), "source"))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await host.StartAsync(true, deadline.Token);
                foreach (var transport in new[] { "http", "pipe" })
                {
                    var lifecycle = Lifecycle(host.Runtime);
                    await lifecycle.WaitAsync(deadline.Token);
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    using var client = new CerebralClient(host.PipeEndpoint!);
                    var before = host.Capture();
                    var admitted = host.Commands.OwnerSnapshotVerifiedAdmissions;
                    var pending = transport == "pipe" ? client.CommandAsync(OwnerModelSnapshotService.Operation,
                        Request(host.ControllerId).Arguments, cancellationToken: cancel.Token) :
                        HttpResultAsync(host, Request(host.ControllerId), cancel.Token);
                    try
                    {
                        await UntilAsync(() => host.Commands.OwnerSnapshotVerifiedAdmissions > admitted, deadline.Token);
                        cancel.Cancel();
                        try { await pending; throw new Exception("Expected cancelled transport request."); }
                        catch (OperationCanceledException) { Check(true, transport + " cancellation observed by caller"); }
                        catch (LocalWorkerCommandUncertainException error) when (transport == "pipe" && error.InnerException is OperationCanceledException)
                        { Check(true, "pipe cancellation preserves existing uncertain-outcome/no-replay contract"); }
                        await Task.Delay(80, deadline.Token);
                    }
                    finally { lifecycle.Release(); }
                    await Task.Delay(80, deadline.Token);
                    host.VerifyNoEffects(before, false, Check, transport + " cancellation while lifecycle busy");
                }
            }

            await using (var host = new OwnerModelSnapshotPathTests.Fixture(Path.Combine(root, "shutdown"), "source"))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await host.StartAsync(true, deadline.Token);
                var lifecycle = Lifecycle(host.Runtime);
                await lifecycle.WaitAsync(deadline.Token);
                var admitted = host.Commands.OwnerSnapshotVerifiedAdmissions;
                var pending = HttpResultAsync(host, Request(host.ControllerId), deadline.Token);
                Task shutdown;
                try
                {
                    await UntilAsync(() => host.Commands.OwnerSnapshotVerifiedAdmissions > admitted, deadline.Token);
                    shutdown = host.Runtime.DisposeAsync().AsTask(); // Marks epoch before waiting the held lifecycle.
                    var disposed = typeof(ModelRuntime).GetField("ownerRuntimeDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    await UntilAsync(() => (bool)disposed.GetValue(host.Runtime)!, deadline.Token);
                }
                catch { lifecycle.Release(); throw; }
                var before = host.Capture();
                lifecycle.Release();
                var result = await pending.WaitAsync(deadline.Token);
                await shutdown.WaitAsync(deadline.Token);
                Check(!result.Success && result.Error == "runtime_snapshot_unavailable", "actual concurrent runtime shutdown rejects before retained model inspection");
                host.VerifyNoEffects(before, false, Check, "shutdown admission");
            }

            await using (var host = new OwnerModelSnapshotPathTests.Fixture(Path.Combine(root, "deadline"), "source"))
            {
                using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await host.StartAsync(true, outer.Token);
                var lifecycle = Lifecycle(host.Runtime);
                await lifecycle.WaitAsync(outer.Token);
                var before = host.Capture();
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var message = new HttpRequestMessage(HttpMethod.Post, host.Address + OwnerModelSnapshotService.HttpPath)
                { Content = new ByteArrayContent(Envelope(Request(host.ControllerId))) };
                message.Headers.Add("X-Cerebral-Key", host.OwnerKey);
                using var client = new CerebralClient(host.PipeEndpoint!);
                try
                {
                    var httpPending = http.SendAsync(message, outer.Token);
                    var pipePending = client.CommandAsync(OwnerModelSnapshotService.Operation, Request(host.ControllerId).Arguments,
                        cancellationToken: outer.Token);
                    using var response = await httpPending;
                    var httpResult = JsonSerializer.Deserialize<CommandResult>(await response.Content.ReadAsByteArrayAsync(outer.Token), CerebralJson.Options)!;
                    CommandResult? pipeResult = null;
                    var pipeClosedAtDeadline = false;
                    try { pipeResult = await pipePending; }
                    catch (LocalWorkerCommandUncertainException error) when (error.InnerException is IOException)
                    { pipeClosedAtDeadline = true; }
                    Check(response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable &&
                        !httpResult.Success && httpResult.Error == "snapshot_deadline", "actual HTTP server deadline rejects busy lifecycle");
                    Check(pipeClosedAtDeadline || pipeResult is { Success: false, Error: "snapshot_deadline" },
                        "actual pipe deadline refuses inspection and preserves truthful close/no-replay outcome");
                    host.VerifyNoEffects(before, false, Check, "whole-path server deadlines");
                }
                finally { lifecycle.Release(); }
            }

            // Actual external SQLite commits inside the read callback prove data_version detects
            // changes to original bodies independently of count/UpdatedAt or typed reserialization.
            var storeRoot = Path.Combine(root, "retained");
            var store = new StateStore(storeRoot);
            store.Put("settings", "host", new { marker = "fixture" });
            store.Put("model", ModelId, new ModelRecord { Id = ModelId, Path = "/synthetic/model.gguf" });
            var reads = store.OwnerSnapshotModelRowsRead;
            try
            {
                _ = store.WithOwnerSnapshotState(true, state =>
                {
                    Check(state.Models.Count == 1, "actual read-only snapshot has the original raw coordinate");
                    using var external = Open(store);
                    using var update = external.CreateCommand();
                    update.CommandText = "UPDATE records SET body=body || ' ' WHERE kind='model' AND id=$id";
                    update.Parameters.AddWithValue("$id", ModelId);
                    Check(update.ExecuteNonQuery() == 1, "controlled external connection commits a raw-body-only change");
                    return state;
                }, CancellationToken.None);
                throw new Exception("Expected changed retained snapshot rejection.");
            }
            catch (OwnerSnapshotUnavailableException error)
            { Check(error.Code == "retained_snapshot_changed", "same-connection data_version refuses raw-body race with unchanged coordinate/count"); }
            Check(store.OwnerSnapshotModelRowsRead == reads + 1, "retained race performs exactly one model-row read");
            var first = store.WithOwnerSnapshotState(true, state => OwnerModelSnapshotCodec.CatalogFence(state.Models), CancellationToken.None);
            using (var external = Open(store))
            using (var update = external.CreateCommand())
            {
                update.CommandText = "UPDATE records SET body=body || ' ' WHERE kind='model' AND id=$id";
                update.Parameters.AddWithValue("$id", ModelId); update.ExecuteNonQuery();
            }
            var second = store.WithOwnerSnapshotState(true, state => OwnerModelSnapshotCodec.CatalogFence(state.Models), CancellationToken.None);
            Check(first != second, "complete catalog fence changes for conservative raw body changes");

            Console.WriteLine($"Owner snapshot races: {checks} assertions passed in actual isolated HTTP/same-user pipe and controlled SQLite races. Synthetic owner settings; no provider/model launch, installed enrollment or live owner used.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("owner-snapshot-races-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe race fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static SemaphoreSlim Lifecycle(ModelRuntime runtime) =>
        (SemaphoreSlim)typeof(ModelRuntime).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
    private static CommandRequest Request(string controller) => new(OwnerModelSnapshotService.Operation,
        CerebralJson.Element(new { schemaVersion = 1, expectedWorkerInstallationId = WorkerId,
            expectedControllerInstallationId = controller, modelId = ModelId }));
    private static byte[] Envelope(CommandRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Compact);
    private static async Task<CommandResult> HttpResultAsync(OwnerModelSnapshotPathTests.Fixture host, CommandRequest request, CancellationToken token)
        => (await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), host.OwnerKey, token)).Result
            ?? throw new Exception("Missing complete command result.");
    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(4))
                throw new TimeoutException("The actual request did not reach its expected admission boundary.");
            await Task.Delay(10, token);
        }
    }
    private static SqliteConnection Open(StateStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open(); return connection;
    }
    private static void ChangeConfiguration(StateStore store, string change)
    {
        using var connection = Open(store);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT body FROM records WHERE kind='settings' AND id='host'";
        var body = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        if (change == "key") body["accessKey"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        else if (change == "worker") body["installationId"] = "323456781234123412341234567890ab";
        else
        {
            body["standaloneWorker"] = true; body["coordinator"] = false;
            body["managementControllerId"] = "323456781234123412341234567890ab";
        }
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE records SET body=$body WHERE kind='settings' AND id='host'";
        update.Parameters.AddWithValue("$body", body.ToJsonString(Compact));
        if (update.ExecuteNonQuery() != 1) throw new Exception("Synthetic owner coordinate missing.");
    }
}
