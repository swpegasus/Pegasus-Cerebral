using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class WorkerProvisioningTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-worker-provisioning-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) { return true; }
        }

        try
        {
            Directory.CreateDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var invitation = new WorkerProvisioningPackage("worker-1", "Garage % Worker = ARM64",
                "http://garage-worker:2667", "installation-1", "provisioning-1", now.AddHours(1), new string('A', 48));
            var encoded = new UTF8Encoding(false).GetBytes(invitation.Serialize());
            var parsed = WorkerProvisioningPackage.Parse(encoded);
            Check(parsed == invitation, "Worker invitation round-trips through the bounded INI contract");
            Check(parsed.ToPendingRegistration(new string('B', 48)) is { RequiresConfiguration: true, AccessKey.Length: 48 },
                "an imported invitation starts as a pending connection with a replacement management key");
            Check(Rejects(() => WorkerProvisioningPackage.Parse(Encoding.UTF8.GetBytes(invitation.Serialize() + "Unexpected=value\r\n"))) &&
                  Rejects(() => WorkerProvisioningPackage.Parse(Encoding.UTF8.GetBytes(invitation.Serialize().Replace(
                      "WorkerId=worker-1\r\n", "WorkerId=worker-1\r\nWorkerId=again\r\n", StringComparison.Ordinal)))) &&
                  Rejects(() => (invitation with { Url = "http://0.0.0.0:2667" }).Validate()) &&
                  Rejects(() => (invitation with { ExpiresAt = now.AddMinutes(-2) }).Validate()),
                "invitation parsing rejects unknown, duplicate, wildcard and expired input");

            var store = new StateStore(root);
            var configuration = new Configuration(store);
            var provisioned = configuration.ProvisionStandalone("Garage Worker", "http://0.0.0.0:2667", true, now);
            Check(provisioned.StandaloneWorker && !provisioned.Coordinator && provisioned.AllowLan && provisioned.AutoSelectAvailablePort &&
                  provisioned.ProvisioningExpiresAt == now.AddDays(7) && provisioned.AccessKey.Length >= 24 &&
                  configuration.ManagementStatus().State == "awaiting-claim",
                "new standalone Worker provisioning creates one bounded unassigned bootstrap state");
            var preserved = configuration.ProvisionStandalone("Ignored repair name", "http://0.0.0.0:2999", false, now.AddHours(1));
            Check(preserved.ProvisioningId == provisioned.ProvisioningId && preserved.AccessKey == provisioned.AccessKey &&
                  preserved.ListenUrl == "http://0.0.0.0:2667",
                "repair before claim preserves the invitation already issued to the owner");
            var pinned = configuration.PinStandaloneEndpoint("http://0.0.0.0:2669");
            Check(pinned.ListenUrl == "http://0.0.0.0:2669" && !pinned.AutoSelectAvailablePort,
                "setup can pin the actual port selected by the Worker before emitting its invitation");

            var controllerId = "cerebral-controller-1";
            var managementKey = new string('C', 48);
            var assigned = configuration.ClaimStandalone(provisioned.ProvisioningId!, controllerId, "Main Cerebral", managementKey, now.AddHours(2));
            Check(assigned is { State: "assigned", ControllerInstallationId: "cerebral-controller-1", ControllerName: "Main Cerebral",
                      ProvisioningExpiresAt: null } && configuration.Get().AccessKey == managementKey &&
                  configuration.Get().ProvisioningId is null,
                "claim atomically replaces the bootstrap credential and records the managing Cerebral installation");
            Check(configuration.ClaimStandalone(provisioned.ProvisioningId!, controllerId, "Main Cerebral", managementKey,
                      now.AddHours(2)).State == "assigned",
                "retrying a completed claim with the replacement credential is idempotent");
            Check(Rejects(() => configuration.ClaimStandalone(provisioned.ProvisioningId!, "different-controller", "Other",
                      new string('D', 48), now.AddHours(2))),
                "a claimed Worker rejects takeover by another Cerebral installation");

            var forbidden = configuration.Get();
            forbidden.AccessKey = new string('E', 48);
            Check(Rejects(() => configuration.Save(forbidden)) && configuration.Get().AccessKey == managementKey,
                "generic settings save cannot replace standalone Worker ownership or its management credential");
            var repaired = configuration.ProvisionStandalone("Repair must preserve assignment", "http://0.0.0.0:3000", true, now.AddDays(1));
            Check(repaired.ManagementControllerId == controllerId && repaired.AccessKey == managementKey &&
                  repaired.ListenUrl == "http://0.0.0.0:2669",
                "Worker repair and upgrade preserve an established Cerebral assignment");
            Check(Rejects(() => configuration.PinStandaloneEndpoint("http://0.0.0.0:2670")),
                "an assigned Worker cannot be silently rebound through bootstrap finalization");

            var ordinary = new Configuration(new StateStore(Path.Combine(root, "integrated")));
            Check(ordinary.ManagementStatus().State == "integrated",
                "an ordinary full Cerebral installation is distinguished from a standalone Worker");
            Check(Rejects(() => ordinary.ProvisionStandalone("Must not replace Cerebral", "http://0.0.0.0:2667", true, now)),
                "standalone provisioning cannot convert an existing full Cerebral data store");
            Console.WriteLine($"Worker provisioning: {checks} checks passed (real SQLite and temporary files; no service or firewall mutation).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Worker provisioning fixture root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static async Task RunLiveAsync(string workerAssembly)
    {
        var worker = Path.GetFullPath(workerAssembly);
        if (!File.Exists(worker) || !Path.GetFileName(worker).Equals("Cerebral.Worker.dll", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Provide the built Cerebral.Worker.dll for live provisioning acceptance.", worker);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-worker-provisioning-live-" + Guid.NewGuid().ToString("N")));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(root);
            var configuration = new Configuration(new StateStore(root));
            var port = AvailableWildcardPort();
            var provisioned = configuration.ProvisionStandalone("Live provisioned Worker", $"http://0.0.0.0:{port}", false);
            var address = FindLanAddress();
            var url = $"http://{address}:{port}";
            var start = new ProcessStartInfo(FindDotnet())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(worker)!
            };
            start.ArgumentList.Add(worker);
            start.ArgumentList.Add("--data"); start.ArgumentList.Add(root);
            process = Process.Start(start) ?? throw new InvalidOperationException("Live Worker process did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var health = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new Uri(url + "/"), Timeout = TimeSpan.FromSeconds(1) };
            health.DefaultRequestHeaders.Add("X-Cerebral-Key", provisioned.AccessKey);
            while (true)
            {
                if (process.HasExited)
                    throw new InvalidOperationException("Live Worker exited before readiness: " + await process.StandardError.ReadToEndAsync());
                try
                {
                    using var response = await health.GetAsync("health", deadline.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (Exception error) when ((error is HttpRequestException || error is TaskCanceledException) && !deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }

            using (var noKey = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                       { BaseAddress = new Uri(url + "/"), Timeout = TimeSpan.FromSeconds(2) })
            using (var unauthorized = await noKey.GetAsync("health", deadline.Token))
                if (unauthorized.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("The LAN Worker accepted an unauthenticated health request.");
            var wrongKeyRejected = false;
            try
            {
                using var wrong = new CerebralClient(url, new string('W', 48));
                _ = await wrong.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: deadline.Token);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { wrongKeyRejected = true; }
            if (!wrongKeyRejected) throw new InvalidOperationException("The LAN Worker accepted an incorrect management key.");

            using var bootstrap = new CerebralClient(url, provisioned.AccessKey);
            var waiting = await bootstrap.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: deadline.Token);
            if (waiting.State != "awaiting-claim" || waiting.WorkerInstallationId != provisioned.InstallationId)
                throw new InvalidOperationException("Live Worker did not expose its exact pending management identity.");
            var replacement = new string('R', 48);
            var claimed = await bootstrap.GetAsync<WorkerManagementStatus>("worker.management.claim", new
            {
                provisioningId = provisioned.ProvisioningId,
                controllerInstallationId = "live-controller",
                controllerName = "Live Cerebral",
                replacementManagementKey = replacement
            }, cancellationToken: deadline.Token);
            var bootstrapRetired = false;
            try { _ = await bootstrap.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: deadline.Token); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { bootstrapRetired = true; }
            if (!bootstrapRetired) throw new InvalidOperationException("The LAN Worker retained its bootstrap credential after claim.");
            using var controller = new CerebralClient(url, replacement);
            var confirmed = await controller.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: deadline.Token);
            var settings = await controller.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
            if (claimed.State != "assigned" || confirmed.ControllerInstallationId != "live-controller" ||
                settings.ManagementControllerId != "live-controller" || settings.AccessKey != replacement)
                throw new InvalidOperationException("Live Worker claim was not durable through the HTTP command dispatcher.");
            Console.WriteLine("Worker provisioning live: real Worker process, non-loopback LAN HTTP route, required-key middleware, command dispatch, SQLite claim, bootstrap retirement and replacement-key confirmation passed. Windows Firewall policy was not changed.");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            process?.Dispose();
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected live Worker provisioning fixture root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int AvailableWildcardPort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindLanAddress() => Dns.GetHostAddresses(Dns.GetHostName())
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))?.ToString()
        ?? throw new InvalidOperationException("Live Worker provisioning acceptance requires one non-loopback IPv4 address.");

    private static string FindDotnet()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")
                : "/usr/bin/dotnet",
            Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? process : null
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            ?? throw new FileNotFoundException("The .NET host for live Worker acceptance was not found.");
    }
}
