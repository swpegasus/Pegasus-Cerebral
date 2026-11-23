using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class ServiceStartupTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS: " + name); }
        async Task<bool> Fails(Fixture operations, CancellationToken token = default)
        {
            try { await new ServiceStartup(operations).EnsureAsync("http://127.0.0.1:6123", "", true, token); return false; }
            catch (Exception) { return true; }
        }
        var running = new Fixture();
        var result = await new ServiceStartup(running).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Worker == "already-running" && running.InstalledQueries == 0 && running.LocalStarts == 0 && running.InstalledStarts == 0,
            "an already responding worker is reused without a second launch or installation lookup");
        var local = new Fixture { Probes = new([null, Fixture.Ready]), Connector = new("stopped", "http://127.0.0.1:6124", "Configured", 1) };
        result = await new ServiceStartup(local).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Worker == "local-started" && result.Connector == "running" && local.LocalStarts == 1 && local.ConnectorStarts == 1,
            "an uninstalled stopped worker and configured connector start through their existing lifecycle operations");
        await new ServiceStartup(local).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(local.LocalStarts == 1 && local.ConnectorStarts == 1, "reopening Integrator reuses both running services");
        var installed = new Fixture { Installed = true, Probes = new([null, Fixture.Ready]) };
        result = await new ServiceStartup(installed).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Worker == "installed-started" && installed.InstalledStarts == 1 && installed.LocalStarts == 0,
            "a registered Windows service is started instead of creating a development worker");
        var denied = new Fixture { Installed = true, Probes = new([null]), InstalledFailure = new UnauthorizedAccessException("Fixture service-start denial.") };
        Check(await Fails(denied) && denied.InstalledStarts == 1 && denied.LocalStarts == 0,
            "failed installed-service start does not silently launch a replacement worker");
        var wrong = new Fixture { Probes = new([Fixture.Ready with { Product = "Another product" }]) };
        Check(await Fails(wrong) && wrong.LocalStarts == 0 && wrong.InstalledQueries == 0,
            "an incompatible responding product prevents any automatic startup");
        var timeout = new Fixture { ProbeFailure = new TaskCanceledException("Fixture probe timeout.") };
        Check(await Fails(timeout) && timeout.InstalledQueries == 0 && timeout.LocalStarts == 0,
            "a timeout is not treated as connection refusal or evidence that no service exists");
        var disabled = new Fixture();
        result = await new ServiceStartup(disabled).EnsureAsync("http://127.0.0.1:6123", "", false, default);
        Check(result.Worker == "manual" && disabled.ProbeCount == 0 && disabled.ConnectorQueries == 0,
            "saved manual startup preference bypasses all automatic service operations");
        var remote = new Fixture();
        result = await new ServiceStartup(remote).EnsureAsync("http://192.0.2.1:6123", "fixture-key", true, default);
        Check(result.Worker == "remote" && remote.ProbeCount == 0 && remote.LocalStarts == 0 && remote.ConnectorQueries == 0,
            "a remote connection cannot trigger a local worker or connector launch");
        var noProviders = new Fixture { Connector = new("stopped", "http://127.0.0.1:6124", "No providers", 0) };
        result = await new ServiceStartup(noProviders).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(!result.Warning && noProviders.ConnectorStarts == 0, "an empty connector is not started or provisioned");
        var connectorError = new Fixture { Connector = new("stopped", "http://127.0.0.1:6124", "Configured", 1),
            ConnectorFailure = new IOException("Fixture provider configuration error.") };
        result = await new ServiceStartup(connectorError).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Warning && result.Worker == "already-running" && result.Connector == "unavailable",
            "optional connector startup failure is reported while the independent worker remains usable");
        var unconfirmed = new Fixture { Connector = new("stopped", "http://127.0.0.1:6124", "Configured", 1),
            ConnectorStartResult = new("stopped", "http://127.0.0.1:6124", "Startup not confirmed", 1) };
        result = await new ServiceStartup(unconfirmed).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Warning && result.Connector == "stopped" && result.Message.Contains("Startup not confirmed"),
            "a connector start that returns stopped remains visible as an unconfirmed start");
        var starting = new Fixture { Connector = new("starting", "http://127.0.0.1:6124", "Startup pending", 1) };
        result = await new ServiceStartup(starting).EnsureAsync("http://127.0.0.1:6123", "", true, default);
        Check(result.Warning && starting.ConnectorStarts == 0, "an already starting connector is not launched again");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = new Fixture();
        Check(await Fails(before, cancelled.Token) && before.ProbeCount == 0, "closing before startup prevents new lifecycle work");
        using var closing = new CancellationTokenSource();
        var after = new Fixture { Probes = new([null]), AfterLocalStart = closing.Cancel };
        Check(await Fails(after, closing.Token) && after.LocalStarts == 1 && after.StartedWorker,
            "closing during the readiness wait cancels waiting without reversing the accepted worker start");
        Console.WriteLine($"Service startup: {checks} deterministic lifecycle checks passed; no service, provider or model was launched.");
    }

    private sealed class Fixture : IServiceStartupOperations
    {
        public static HostInfo Ready => new("Pegasus Cerebral", "fixture", 1, "Fixture", "x64", "fixture", true, Path.GetTempPath(), true);
        public Queue<HostInfo?> Probes = new();
        public bool Installed, StartedWorker;
        public int ProbeCount, InstalledQueries, InstalledStarts, LocalStarts, ConnectorQueries, ConnectorStarts;
        public Exception? ProbeFailure, InstalledFailure, ConnectorFailure;
        public ConnectorStatus? ConnectorStartResult;
        public Action? AfterLocalStart;
        public ConnectorStatus Connector = new("not-configured", "http://127.0.0.1:6124", "Not configured", 0);
        public Task<HostInfo?> ProbeWorkerAsync(string url, string accessKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ProbeCount++;
            if (ProbeFailure is not null) throw ProbeFailure;
            return Task.FromResult(Probes.Count > 0 ? Probes.Dequeue() : Ready);
        }
        public Task<bool> IsWorkerInstalledAsync(CancellationToken token) { InstalledQueries++; return Task.FromResult(Installed); }
        public Task StartInstalledWorkerAsync(CancellationToken token) { InstalledStarts++; if (InstalledFailure is not null) throw InstalledFailure; StartedWorker = true; return Task.CompletedTask; }
        public Task StartLocalWorkerAsync(string url, CancellationToken token) { LocalStarts++; StartedWorker = true; AfterLocalStart?.Invoke(); return Task.CompletedTask; }
        public Task<ConnectorStatus> ConnectorStatusAsync(CancellationToken token) { ConnectorQueries++; return Task.FromResult(Connector); }
        public Task<ConnectorStatus> StartConnectorAsync(CancellationToken token) { ConnectorStarts++; if (ConnectorFailure is not null) throw ConnectorFailure; Connector = ConnectorStartResult ?? Connector with { State = "running" }; return Task.FromResult(Connector); }
    }
}
