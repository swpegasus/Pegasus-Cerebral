using System.Diagnostics;
using System.IO;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Opt-in real local Worker lifecycle acceptance, isolated from installed state and models.</summary>
public static class LocalDevelopmentPipeChecks
{
    public static async Task<int> VerifyAsync(string? workerExecutable = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local pipe lifecycle checks require Windows.");
        var executable = workerExecutable ?? ManagementProcess.WorkerPath;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Build the Worker executable before running local pipe lifecycle checks.", executable);
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-pipe-lifecycle-check-" + Guid.NewGuid().ToString("N")));
        var data = Path.Combine(scratch, "data");
        var plugins = Path.Combine(scratch, "empty-service-plugins");
        Directory.CreateDirectory(plugins);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(150));
        LocalWorkerDiscoveryRecord? owned = null;
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        async Task Rejected(Func<Task> action, string description)
        {
            var rejected = false;
            try { await action().ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or InvalidDataException or OperationCanceledException or TimeoutException) { rejected = true; }
            Check(rejected, description);
        }
        try
        {
            Check(await LocalDevelopment.DiscoverPipeAsync(deadline.Token, data).ConfigureAwait(false) is null,
                "missing local discovery performs no Worker launch");
            var first = await LocalDevelopment.StartPipeForDiagnosticsAsync(executable, data, plugins, deadline.Token).ConfigureAwait(false);
            owned = LocalWorkerDiscovery.Read(data) ?? throw new InvalidOperationException("Startup returned without durable discovery.");
            first.ToPreferences().ValidateHost(first.Host);
            Check(first.Host.LocalRun && first.DataDirectory == data && owned.Endpoint == first.Endpoint && owned.ProcessId > 0,
                "actual hidden Worker startup records exact pipe, process and data ownership");
            var discovered = await LocalDevelopment.DiscoverPipeAsync(deadline.Token, data).ConfigureAwait(false);
            Check(discovered?.Endpoint == first.Endpoint, "discovery returns the verified live generation");
            var repeated = await LocalDevelopment.StartPipeForDiagnosticsAsync(executable, data, plugins, deadline.Token).ConfigureAwait(false);
            Check(repeated.Endpoint == first.Endpoint && LocalWorkerDiscovery.Read(data) == owned,
                "repeated Start reuses the same Worker process and generation");

            using (var firstClient = new CerebralClient(first.Endpoint))
            using (var secondClient = new CerebralClient(first.Endpoint))
            {
                var health = await Task.WhenAll(firstClient.GetAsync<HostInfo>("health", cancellationToken: deadline.Token),
                    secondClient.GetAsync<HostInfo>("health", cancellationToken: deadline.Token)).ConfigureAwait(false);
                Check(firstClient.IsLocalPipe && secondClient.IsLocalPipe && health.All(x => x.LocalRun && x.DataDirectory == data),
                    "two independent CerebralClient instances execute commands through the same local pipe Worker");
                using var eventsLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                eventsLifetime.CancelAfter(TimeSpan.FromSeconds(15));
                var firstEvents = FirstSettingsEventAsync(firstClient, eventsLifetime.Token);
                var secondEvents = FirstSettingsEventAsync(secondClient, eventsLifetime.Token);
                // Public event enumeration intentionally hides the transport acknowledgement. Generate
                // distinct, accepted settings updates until both subscriptions observe one; no command
                // is retried after an unknown result, and all changed state belongs to this fixture.
                for (var update = 0; update < 8 && (!firstEvents.IsCompleted || !secondEvents.IsCompleted); update++)
                {
                    var settings = await firstClient.GetAsync<HostSettings>("settings.get", cancellationToken: eventsLifetime.Token).ConfigureAwait(false);
                    settings.QueueCapacity = 100 + update;
                    _ = await secondClient.GetAsync<HostSettings>("settings.save", settings, cancellationToken: eventsLifetime.Token).ConfigureAwait(false);
                    await Task.WhenAny(Task.WhenAll(firstEvents, secondEvents), Task.Delay(150, eventsLifetime.Token)).ConfigureAwait(false);
                }
                var notifications = await Task.WhenAll(firstEvents, secondEvents).WaitAsync(eventsLifetime.Token).ConfigureAwait(false);
                Check(notifications.All(x => x.Type == "settings.updated" && x.Sequence > 0),
                    "both CerebralClient event streams receive actual Worker settings notifications");
                var observed = await firstClient.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token).ConfigureAwait(false);
                Check(observed.QueueCapacity is >= 100 and < 108 && observed.McpServers.Count == 0 && !observed.BackgroundTasksEnabled,
                    "command and event traffic stays in isolated settings without MCP or background jobs");
                var uploadDenied = false;
                try { _ = await firstClient.UploadModelAsync(Path.Combine(scratch, "absent.gguf"), "fixture", cancellationToken: deadline.Token).ConfigureAwait(false); }
                catch (NotSupportedException) { uploadDenied = true; }
                Check(uploadDenied, "pipe model upload rejects explicitly before file access or HTTP fallback");
            }
            discovered = await LocalDevelopment.DiscoverPipeAsync(deadline.Token, data).ConfigureAwait(false);
            Check(discovered?.Endpoint == first.Endpoint && LocalWorkerDiscovery.Read(data) == owned,
                "disposing all command and event clients leaves the independent Worker running");

            await Rejected(() => LocalDevelopment.StopPipeAsync(first.Endpoint with { InstanceId = Guid.NewGuid().ToString("N") }, deadline.Token, data),
                "Stop rejects a different generation without stopping the current Worker").ConfigureAwait(false);
            var foreign = Path.Combine(scratch, "foreign-data"); Directory.CreateDirectory(foreign);
            File.Copy(Path.Combine(data, LocalWorkerDiscovery.FileName), Path.Combine(foreign, LocalWorkerDiscovery.FileName));
            await Rejected(() => LocalDevelopment.StopPipeAsync(first.Endpoint, deadline.Token, foreign),
                "Stop rejects a copied discovery record belonging to another data directory").ConfigureAwait(false);
            try
            {
                LocalWorkerDiscovery.Write(owned with { ProcessId = 0, ProcessStartUtcTicks = 0 });
                await Rejected(async () => { _ = await LocalDevelopment.StartPipeForDiagnosticsAsync(executable, data, plugins, deadline.Token).ConfigureAwait(false); },
                    "uncertain launch intent does not authorize starting a replacement Worker").ConfigureAwait(false);
            }
            finally { LocalWorkerDiscovery.Write(owned); }
            try
            {
                var unavailable = owned with { Endpoint = new("Pegasus.Cerebral.Unavailable." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")) };
                LocalWorkerDiscovery.Write(unavailable);
                using var shortProbe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                shortProbe.CancelAfter(TimeSpan.FromSeconds(1));
                await Rejected(async () => { _ = await LocalDevelopment.StartPipeForDiagnosticsAsync(executable, data, plugins, shortProbe.Token).ConfigureAwait(false); },
                    "an unresponsive recorded process is not restarted when connection waiting ends").ConfigureAwait(false);
                Check(LocalWorkerDiscovery.Read(data) == unavailable, "failed connection retains the exact uncertain generation for inspection");
            }
            finally { LocalWorkerDiscovery.Write(owned); }
            discovered = await LocalDevelopment.DiscoverPipeAsync(deadline.Token, data).ConfigureAwait(false);
            Check(discovered?.Endpoint == first.Endpoint, "the original Worker remains usable after rejected ownership and timeout attempts");

            using (var process = LocalWorkerDiscovery.FindProcess(owned) ?? throw new InvalidOperationException("Test Worker disappeared before normal shutdown."))
            {
                // An attached Process does not retain a launch handle automatically. Acquire it while
                // this exact process is alive so its exit status remains queryable after shutdown.
                _ = process.SafeHandle;
                if (process.HasExited || process.Id != owned.ProcessId ||
                    process.StartTime.ToUniversalTime().Ticks != owned.ProcessStartUtcTicks ||
                    !string.Equals(process.MainModule?.FileName, owned.Executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The retained Worker process handle no longer matches the test-owned identity.");
                await LocalDevelopment.StopPipeAsync(first.Endpoint, deadline.Token, data).ConfigureAwait(false);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                Check(process.HasExited && process.ExitCode == 0 && LocalWorkerDiscovery.Read(data) is null,
                    "graceful Stop exits the owned Worker and removes only its current discovery record");
            }
            owned = null;
            Check(await LocalDevelopment.DiscoverPipeAsync(deadline.Token, data).ConfigureAwait(false) is null,
                "discovery confirms stopped state without launching a new process");
            var restarted = await LocalDevelopment.StartPipeForDiagnosticsAsync(executable, data, plugins, deadline.Token).ConfigureAwait(false);
            owned = LocalWorkerDiscovery.Read(data)!;
            Check(restarted.Endpoint.InstanceId != first.Endpoint.InstanceId && restarted.Endpoint.PipeName != first.Endpoint.PipeName,
                "explicit restart allocates a new pipe generation over the same data directory");
            using (var client = new CerebralClient(restarted.Endpoint))
            {
                var settings = await client.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token).ConfigureAwait(false);
                Check(settings.QueueCapacity is >= 100 and < 108, "the restarted process retains the previously accepted settings");
            }
            await Rejected(() => LocalDevelopment.StopPipeAsync(first.Endpoint, deadline.Token, data),
                "an old generation cannot stop the restarted Worker").ConfigureAwait(false);
            await LocalDevelopment.StopPipeAsync(restarted.Endpoint, deadline.Token, data).ConfigureAwait(false);
            owned = null;
            Check(LocalWorkerDiscovery.Read(data) is null, "the restarted Worker also completes normal local shutdown");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Local pipe lifecycle acceptance failed: " + error.Message + "\n" + await ReadLogTailAsync(data).ConfigureAwait(false), error);
        }
        finally
        {
            // Emergency cleanup is confined to a process whose exact PID/start/executable came from
            // this unique test data directory. Production lifecycle controls never kill processes.
            owned ??= LocalWorkerDiscovery.Read(data);
            if (owned is { ProcessId: > 0 })
            {
                using var process = LocalWorkerDiscovery.FindProcess(owned);
                if (process is not null && !process.HasExited)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await LocalDevelopment.StopPipeAsync(owned.Endpoint, cleanup.Token, data).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                }
            }
            if (!scratch.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(scratch).StartsWith("cerebral-pipe-lifecycle-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact lifecycle-check directory.");
            Directory.Delete(scratch, recursive: true);
        }
        Console.WriteLine($"Local pipe lifecycle: {checks} actual Worker/client/discovery checks passed; no installed service, model inference or external tools used.");
        return checks;
    }

    private static async Task<ServiceEvent> FirstSettingsEventAsync(CerebralClient client, CancellationToken token)
    {
        await foreach (var item in client.EventsAsync(token).ConfigureAwait(false))
            if (item.Type == "settings.updated") return item;
        throw new IOException("The Worker event stream ended before an expected notification.");
    }

    private static async Task<string> ReadLogTailAsync(string data)
    {
        var path = Path.Combine(data, "local-worker.log");
        if (!File.Exists(path)) return "No local Worker log was created.";
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Seek(Math.Max(0, file.Length - 8192), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            var buffer = new char[8192];
            var length = await reader.ReadAsync(buffer).ConfigureAwait(false);
            return "Bounded Worker log:\n" + new string(buffer, 0, length);
        }
        catch (IOException) { return "The local Worker log was unavailable."; }
    }
}
