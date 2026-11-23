using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in native package/Worker composition acceptance; no model, owner data or Cortex calls.</summary>
internal static class ConversationPackageLiveTests
{
    public static async Task RunAsync(string workerDll, string candidateManifest, string evidencePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named-pipe acceptance requires Windows.");
        candidateManifest = Path.GetFullPath(candidateManifest);
        evidencePath = Path.GetFullPath(evidencePath);
        workerDll = Path.GetFullPath(workerDll);
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var engineRoot = Path.Combine(Path.GetDirectoryName(workerDll)!, "Engines");
        if (Path.GetFileName(workerDll) != "Cerebral.Worker.dll" || !File.Exists(workerDll) ||
            !File.Exists(dotnet) || !File.Exists(Path.ChangeExtension(workerDll, ".runtimeconfig.json")) ||
            !Directory.Exists(engineRoot)) throw new IOException("The private Worker runtime or engine package is missing.");
        var candidate = ServicePluginRuntime.InspectPackage(Path.GetDirectoryName(candidateManifest)!, candidateManifest,
            ConversationWorkflowModule.ModuleId, requireMatchingDirectory: false);
        if (candidate.Version != "1.0.2") throw new InvalidDataException("Expected the conversation 1.0.2 candidate.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-conversation-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var children = new List<Process>();
        var checks = 0;
        var evidenceWritten = false;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Conversation package acceptance: " + message);
            checks++;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var data = Path.Combine(root, "data");
            var repository = Path.Combine(root, "repository");
            var state = new StateStore(data);
            var configuration = new Configuration(state);
            var settings = configuration.Get();
            settings.ModelDirectory = Path.Combine(root, "models");
            settings.ProjectsDirectory = Path.Combine(root, "projects");
            settings.SharedDirectory = Path.Combine(root, "shared");
            settings.McpServers.Clear(); settings.Workers.Clear(); settings.BackgroundTasks.Clear(); settings.Executables.Clear();
            settings.CortexNamespaces = []; settings.CortexWriteNamespaces = []; settings.ExternalCommandsEnabled = false;
            configuration.Save(settings);
            // Native loading is verified in the actual child hosts below. Keeping another candidate
            // load context in this async fixture would unnecessarily retain its DLL during cleanup.
            SqliteConnection.ClearAllPools();
            var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.PackageCheck." + Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"));
            ServicePackageLifecycleStatus staged;
            ServicePackageLifecycleStatus selected;
            ServicePackageLifecycleStatus builtinRestarted;
            var builtin = StartWorker();
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, builtin);
                var before = await client.GetAsync<ServicePackageLifecycleStatus>("service-packages.inspect", new { id = candidate.Id }, cancellationToken: deadline.Token);
                Check(before.ObservedLoaded && before.ObservedVersion == ConversationWorkflowModule.ModuleVersion,
                    "actual Worker reports its selected built-in policy before native staging");
                staged = await client.GetAsync<ServicePackageLifecycleStatus>("service-packages.stage",
                    new { manifestPath = candidateManifest, expectedRevision = before.Revision }, cancellationToken: deadline.Token);
                Check(staged.StagedVersion == candidate.Version && staged.StagedGeneration is not null &&
                    staged.ObservedVersion == ConversationWorkflowModule.ModuleVersion && staged.DesiredActive &&
                    !staged.RestartRequired && !Directory.Exists(Path.Combine(data, "PluginData", candidate.Id)),
                    "real native staging preserves built-in policy without initializing candidate code");
                var discovery = await client.GetAsync<JsonElement>("sdk.discover", cancellationToken: deadline.Token);
                Check(discovery.GetProperty("workflows").EnumerateArray().Count(item =>
                    item.GetProperty("id").GetString() == ConversationWorkflowModule.DefinitionId) == 1,
                    "staging leaves exactly one existing chat definition available");
                await StopWorker(client, builtin);
            }
            var stagedRestart = StartWorker();
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, stagedRestart);
                builtinRestarted = await client.GetAsync<ServicePackageLifecycleStatus>("service-packages.inspect", new { id = candidate.Id }, cancellationToken: deadline.Token);
                Check(builtinRestarted.ObservedLoaded && builtinRestarted.ObservedVersion == ConversationWorkflowModule.ModuleVersion &&
                    builtinRestarted.StagedGeneration == staged.StagedGeneration && !builtinRestarted.RestartRequired,
                    "actual restart retains built-in policy and the staged native candidate");
                selected = await client.GetAsync<ServicePackageLifecycleStatus>("service-packages.activate",
                    new { id = candidate.Id, expectedRevision = builtinRestarted.Revision }, cancellationToken: deadline.Token);
                Check(selected.DesiredVersion == candidate.Version && selected.DesiredGeneration == staged.StagedGeneration &&
                    selected.ObservedVersion == ConversationWorkflowModule.ModuleVersion && selected.RestartRequired,
                    "activation selects an immutable native generation without replacing executing code");
                await StopWorker(client, stagedRestart);
            }
            var first = StartWorker();
            var firstStartedUtc = first.StartTime.ToUniversalTime();
            JsonElement inspected;
            JsonElement plugin;
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, first);
                var discovery = await client.GetAsync<JsonElement>("sdk.discover", cancellationToken: deadline.Token);
                Check(discovery.GetProperty("workflows").EnumerateArray().Count(item =>
                    item.GetProperty("id").GetString() == ConversationWorkflowModule.DefinitionId &&
                    item.GetProperty("version").GetString() == ConversationWorkflowModule.DefinitionVersion) == 1,
                    "actual Worker Program starts and discovers one native chat definition");
                inspected = await client.GetAsync<JsonElement>("service-packages.inspect", new { id = candidate.Id }, cancellationToken: deadline.Token);
                Check(inspected.GetProperty("observedLoaded").GetBoolean() &&
                    inspected.GetProperty("observedVersion").GetString() == candidate.Version &&
                    inspected.GetProperty("observedGeneration").GetString() == selected.DesiredGeneration &&
                    !inspected.GetProperty("restartRequired").GetBoolean(), "native pipe reports desired/observed generation convergence");
                var diagnostics = await client.GetAsync<JsonElement>("diagnostics", cancellationToken: deadline.Token);
                plugin = diagnostics.GetProperty("servicePlugins").EnumerateArray().Single(item => item.GetProperty("id").GetString() == candidate.Id).Clone();
                Check(plugin.GetProperty("loaded").GetBoolean() && plugin.GetProperty("version").GetString() == candidate.Version,
                    "real Worker diagnostics agree with native package selection");
                Check((await client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: deadline.Token)).Count == 0,
                    "isolated acceptance loads no inference model");
                await StopWorker(client, first);
            }
            var second = StartWorker();
            using (var client = new CerebralClient(endpoint))
            {
                await WaitHealth(client, second);
                var retained = await client.GetAsync<JsonElement>("service-packages.inspect", new { id = candidate.Id }, cancellationToken: deadline.Token);
                Check(retained.GetProperty("observedGeneration").GetString() == selected.DesiredGeneration &&
                    retained.GetProperty("observedLoaded").GetBoolean() && first.HasExited && second.Id != first.Id,
                    "a second actual Worker process restores the immutable selected generation");
                await StopWorker(client, second);
            }
            var sdkHashes = new[] { "Cerebral.Core.dll", "Cerebral.Contracts.dll", "Cerebral.Engine.Contracts.dll" }
                .ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(workerDll)!, name)))));
            using (var evidence = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(evidence, new
                {
                    test = "actual-conversation-package-worker-pipe", at = DateTimeOffset.UtcNow, checks,
                    manifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidateManifest))),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(candidateManifest)!, candidate.Assembly)))),
                    sdkHashes, staged, builtinRestarted, selected, inspected, plugin, definitionVersion = ConversationWorkflowModule.DefinitionVersion,
                    firstPid = first.Id, firstStartedUtc, secondPid = second.Id, cleanExit = children.All(child => child.HasExited && child.ExitCode == 0),
                    processes = children.Select(child => new { pid = child.Id, startUtc = child.StartTime.ToUniversalTime(), child.ExitCode }),
                    modelInference = false, ownerDataAccess = false, cortexCalls = false
                }, CerebralJson.Options, deadline.Token);
            evidenceWritten = true;
            Console.WriteLine($"Conversation package: {checks} checks passed with actual native staging/loading, four real Worker processes and named-pipe transport; no model or owner data.");

            Process StartWorker()
            {
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(workerDll)! };
                foreach (var argument in new[] { workerDll, "--local-run", "--data", data, "--service-plugins", repository,
                    "--engines", engineRoot, "--pipe-name", endpoint.PipeName, "--pipe-instance", endpoint.InstanceId })
                    start.ArgumentList.Add(argument);
                var child = Process.Start(start) ?? throw new IOException("Private Worker did not start.");
                children.Add(child); return child;
            }
            async Task WaitHealth(CerebralClient client, Process child)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    if (child.HasExited) throw new IOException("Private Worker exited before health; inspect its isolated local-worker.log.");
                    try
                    {
                        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
                        Check(health.LocalRun && health.InstallationId == settings.InstallationId,
                            "native endpoint is bound to this isolated installation"); return;
                    }
                    catch (IOException) { }
                    catch (TimeoutException) { }
                    await Task.Delay(100, deadline.Token);
                }
                throw new TimeoutException("Private Worker health was not ready.");
            }
            async Task StopWorker(CerebralClient client, Process child)
            {
                var response = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
                Check(response.Success, "owned Worker accepts graceful local shutdown");
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await child.WaitForExitAsync(stop.Token);
                Check(child.ExitCode == 0, "owned Worker exits cleanly");
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
                        child.Kill(entireProcessTree: true);
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await child.WaitForExitAsync(stop.Token);
                    }
                }
                finally { child.Dispose(); }
            }
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("cerebral-conversation-package-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned package fixture.");
            // Retain a failed fixture for bounded log diagnosis; successful evidence permits cleanup.
            if (evidenceWritten) Directory.Delete(root, recursive: true);
            else Console.WriteLine("Failed package fixture retained for diagnosis: " + root);
        }
    }
}
