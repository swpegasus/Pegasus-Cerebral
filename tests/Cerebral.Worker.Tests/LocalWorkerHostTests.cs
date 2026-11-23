using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cerebral.Client;
using Cerebral.Core;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual child Worker, named pipe, dispatcher and SQLite acceptance. All writable state is
/// test-owned. No installed service, model inference, remote Worker or PAO command is used.</summary>
public static class LocalWorkerHostTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Worker host acceptance requires Windows named-pipe security.");
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-local-pipe-host-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var occupiedHttp = new TcpListener(IPAddress.Loopback, 0);
        occupiedHttp.Server.ExclusiveAddressUse = true;
        occupiedHttp.Start();
        var httpPort = ((IPEndPoint)occupiedHttp.LocalEndpoint).Port;
        var dataPath = Path.Combine(scratch, "data");
        var pluginPath = Path.Combine(scratch, "empty-service-plugins");
        Directory.CreateDirectory(pluginPath);
        ChildWorker? child = null;
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        try
        {
            Seed(dataPath, scratch, httpPort);
            SqliteConnection.ClearAllPools();
            child = await ChildWorker.StartAsync(dataPath, pluginPath, deadline.Token);
            using (var client = new LocalWorkerClient(child.Endpoint))
            {
                var health = await ReadAsync<HostInfo>(client, "health", new { }, deadline.Token);
                Check(health.Product == LocalWorkerProtocol.Product && health.Protocol == LocalWorkerProtocol.Version && health.LocalRun &&
                    Path.GetFullPath(health.DataDirectory).Equals(Path.GetFullPath(dataPath), StringComparison.OrdinalIgnoreCase),
                    "real pipe-only Worker identifies its product, protocol, local mode and isolated data root");
                var diagnostics = await ReadAsync<JsonElement>(client, "diagnostics", new { }, deadline.Token);
                var processorDiagnostics = diagnostics.GetProperty("processors");
                var requestedProcessors = processorDiagnostics.GetProperty("requestedLogicalProcessors").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var effectiveProcessors = processorDiagnostics.GetProperty("effectiveLogicalProcessors").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var processorPolicyApplied = processorDiagnostics.GetProperty("applied").GetBoolean();
                var degradedReason = processorDiagnostics.GetProperty("degradedReason");
                Check(processorDiagnostics.GetProperty("availableLogicalProcessorCount").GetInt32() >= 1 &&
                      effectiveProcessors.Length >= 1 && requestedProcessors.All(processor => processor != 0) &&
                      (processorPolicyApplied
                          ? effectiveProcessors.SequenceEqual(requestedProcessors) && processorDiagnostics.GetProperty("directChildrenInheritPolicy").GetBoolean()
                          : degradedReason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(degradedReason.GetString())),
                    "real Worker diagnostics expose requested and effective processor sets, reserve processor 0 by default, and report any non-fatal degradation");
                Check(occupiedHttp.LocalEndpoint is IPEndPoint endpoint && endpoint.Port == httpPort,
                    "pipe-only Worker starts while its configured HTTP port remains exclusively occupied by this test");

                var baseline = await ReadAsync<HostSettings>(client, "settings.get", new { }, deadline.Token);
                var leadBefore = await ReadAsync<SystemInstructionDocument>(client, "instructions.get", new { role = "lead" }, deadline.Token);
                var promptText = "Saved Cerebral guidance " + Guid.NewGuid().ToString("N");
                var leadSaved = await ReadAsync<SystemInstructionDocument>(client, "instructions.save",
                    new { role = "lead", content = promptText, expectedSha256 = leadBefore.Sha256 }, deadline.Token);
                var helperBefore = await ReadAsync<SystemInstructionDocument>(client, "instructions.get", new { role = "helper" }, deadline.Token);
                await ReadAsync<SystemInstructionDocument>(client, "instructions.save",
                    new { role = "helper", content = "", expectedSha256 = helperBefore.Sha256 }, deadline.Token);
                Check(leadSaved.Content == promptText && !Directory.Exists(Path.Combine(dataPath, "Instructions")),
                    "owner instruction saves cross the pipe into SQLite without materializing Markdown files");
                var profileBefore = await ReadAsync<AgentInstructionDocument>(client, "agents.instructions.get",
                    new { profileId = "pipe-reviewer" }, deadline.Token);
                var profileSaved = await ReadAsync<AgentInstructionDocument>(client, "agents.instructions.save",
                    new AgentInstructionSaveRequest(profileBefore.ProfileId, "Real pipe profile instructions", profileBefore.Revision), deadline.Token);
                var staleProfileSave = await CallAsync(client, "agents.instructions.save",
                    new AgentInstructionSaveRequest(profileBefore.ProfileId, "Stale text", profileBefore.Revision), deadline.Token);
                Check(profileSaved.Content == "Real pipe profile instructions" && profileSaved.Revision == profileBefore.Revision + 1 &&
                    !staleProfileSave.Success, "narrow profile prompt save crosses the real pipe and rejects a stale revision");
                var deniedProfileSave = await CallAsync(client, "agents.instructions.save",
                    new AgentInstructionSaveRequest(profileBefore.ProfileId, "Agent rewrite", profileSaved.Revision), deadline.Token, "pipe-admin");
                Check(!deniedProfileSave.Success, "administration-capable agent cannot edit owner prompt settings across the pipe");
                var backup = await ReadAsync<ConfigurationBackupExport>(client, "configuration.backup.export", new { }, deadline.Token);
                Check(backup.Document.Prompts?.Single(x => x.Role == "lead").Content == promptText &&
                    backup.Document.Prompts.Single(x => x.Role == "helper").Content == "",
                    "real portable export includes saved role guidance and intentional empty layers");
                Check(backup.Document.Format == ConfigurationBackupContract.Format && backup.Document.SchemaVersion == ConfigurationBackupContract.SchemaVersion &&
                    backup.Document.Host.QueueCapacity == baseline.QueueCapacity && backup.RequiresTextReview,
                    "configuration export crosses the real pipe with the current portable settings and explicit text-review requirement");
                Check(backup.Document.Paths.ModelDirectory == baseline.ModelDirectory && backup.Document.Paths.ProjectsDirectory == baseline.ProjectsDirectory &&
                    backup.Document.Paths.SharedDirectory == baseline.SharedDirectory && backup.Document.Models.All(model => IsWithin(model.Path, scratch)),
                    "backup paths and unloaded model references remain inside isolated test storage");

                var changedCapacity = baseline.QueueCapacity == 1000 ? 999 : baseline.QueueCapacity + 1;
                var baselineRevision = baseline.Revision;
                baseline.QueueCapacity = changedCapacity;
                var changed = await ReadAsync<HostSettings>(client, "settings.save", baseline, deadline.Token);
                Check(changed.QueueCapacity == changedCapacity && changed.Revision == baselineRevision + 1,
                    "owner settings save reaches the actual dispatcher and persists the changed queue setting");
                var observed = await ReadAsync<HostSettings>(client, "settings.get", new { }, deadline.Token);
                Check(observed.QueueCapacity == changedCapacity && observed.Revision == changed.Revision,
                    "a separate pipe command observes the committed settings revision");

                foreach (var profileId in new[] { "pipe-reviewer", "pipe-admin", "missing-profile" })
                {
                    var denied = await CallAsync(client, "configuration.backup.export", new { }, deadline.Token, profileId);
                    Check(!denied.Success, "configuration export denies explicit profile authority: " + profileId);
                }
                await ReadAsync<SystemInstructionDocument>(client, "instructions.save",
                    new { role = "lead", content = "Temporary replacement guidance", expectedSha256 = leadSaved.Sha256 }, deadline.Token);
                backup = backup with { Document = backup.Document with { Connections = backup.Document.Connections! with
                {
                    Workers = [.. backup.Document.Connections.Workers,
                        new ConfigurationBackupWorker("restored-worker", "Pending restored worker", "http://127.0.0.1:9")]
                } } };
                var previewRequest = new ConfigurationRestorePreviewRequest(backup.Document);
                var deniedPreview = await CallAsync(client, "configuration.restore.preview", previewRequest, deadline.Token, "pipe-admin");
                Check(!deniedPreview.Success, "an administration-capable agent cannot create an owner configuration restore preview");
                var preview = await ReadAsync<ConfigurationRestorePreview>(client, "configuration.restore.preview", previewRequest, deadline.Token);
                Check(preview.ExpectedHostRevision == changed.Revision && preview.RequiresTextReview && preview.Mode == "upsert" &&
                    !string.IsNullOrWhiteSpace(preview.PreviewId) && preview.ExpiresAt > DateTimeOffset.UtcNow,
                    "owner restore preview binds the exact current revision and reviewable candidate");
                var applyRequest = new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, ReviewedFreeText: true);
                var deniedApply = await CallAsync(client, "configuration.restore.apply", applyRequest, deadline.Token, "pipe-reviewer");
                Check(!deniedApply.Success, "a nonadministrative saved profile cannot apply an owner restore candidate");
                var restored = await ReadAsync<ConfigurationRestoreResult>(client, "configuration.restore.apply", applyRequest, deadline.Token);
                var restoreStatus = await ReadAsync<ConfigurationRestoreStatus>(client, "configuration.restore.status",
                    new ConfigurationRestoreStatusRequest(preview.PreviewId), deadline.Token);
                Check(restoreStatus.State == "committed" && restoreStatus.Receipt?.PreviewId == preview.PreviewId &&
                    JsonSerializer.Serialize(restoreStatus.Receipt.Result, CerebralJson.Options) == JsonSerializer.Serialize(restored, CerebralJson.Options),
                    "owner reads the exact committed restore receipt over the actual pipe");
                var deniedStatus = await CallAsync(client, "configuration.restore.status",
                    new ConfigurationRestoreStatusRequest(preview.PreviewId), deadline.Token, "pipe-admin");
                Check(!deniedStatus.Success, "an administrative agent cannot read owner restore receipts");
                observed = await ReadAsync<HostSettings>(client, "settings.get", new { }, deadline.Token);
                Check(restored.HostRevision > changed.Revision && observed.Revision == restored.HostRevision &&
                    observed.QueueCapacity == backup.Document.Host.QueueCapacity,
                    "reviewed restore atomically restores the exported queue setting and advances the persisted revision");
                Check(observed.ListenUrl == changed.ListenUrl && observed.AccessKey == changed.AccessKey &&
                    observed.ModelDirectory == backup.Document.Paths.ModelDirectory && observed.SharedDirectory == backup.Document.Paths.SharedDirectory,
                    "portable restore preserves local transport credentials and the selected isolated paths");

                var restoredWorker = observed.Workers.Single(x => x.Id == "restored-worker");
                var pendingProbe = await CallAsync(client, "workers.probe", new { id = restoredWorker.Id }, deadline.Token);
                Check(restoredWorker.RequiresConfiguration && restoredWorker.AccessKey.Length == 0 &&
                    !pendingProbe.Success && pendingProbe.Error!.Contains("requires owner review", StringComparison.Ordinal),
                    "actual dispatcher preserves pending restored worker and refuses its probe before sending a request");
                var restoredLead = await ReadAsync<SystemInstructionDocument>(client, "instructions.get", new { role = "lead" }, deadline.Token);
                Check(restored.PromptRoles?.Count == 4 && restoredLead.Content == promptText &&
                    restoredLead.Sha256 != leadSaved.Sha256,
                    "restore brings prompt content back with a new revision in the same settings transaction");

                var firstInstance = child.Endpoint.InstanceId;
                await StopAsync(client, child, deadline.Token);
                Check(child.ExitCode == 0, "service.stop-local gracefully exits the first actual Worker process");
                await child.DisposeAsync(); child = null;
                child = await ChildWorker.StartAsync(dataPath, pluginPath, deadline.Token);
                using var restarted = new LocalWorkerClient(child.Endpoint);
                Check(child.Endpoint.InstanceId != firstInstance, "restarted Worker has a new pipe instance identity");
                var afterRestart = await ReadAsync<HostSettings>(restarted, "settings.get", new { }, deadline.Token);
                Check(afterRestart.QueueCapacity == backup.Document.Host.QueueCapacity && afterRestart.Revision == restored.HostRevision &&
                    afterRestart.ModelDirectory == observed.ModelDirectory && afterRestart.SharedDirectory == observed.SharedDirectory,
                    "a new Worker process reads the restored settings and exact revision from the same SQLite state");
                var reopenedStatus = await ReadAsync<ConfigurationRestoreStatus>(restarted, "configuration.restore.status",
                    new ConfigurationRestoreStatusRequest(preview.PreviewId), deadline.Token);
                Check(reopenedStatus.State == "committed" &&
                    JsonSerializer.Serialize(reopenedStatus.Receipt, CerebralJson.Options) == JsonSerializer.Serialize(restoreStatus.Receipt, CerebralJson.Options),
                    "the exact restore receipt survives a real Worker process restart");
                var repeatedRestore = await ReadAsync<ConfigurationRestoreResult>(restarted, "configuration.restore.apply", applyRequest, deadline.Token);
                var afterRetry = await ReadAsync<HostSettings>(restarted, "settings.get", new { }, deadline.Token);
                Check(JsonSerializer.Serialize(repeatedRestore, CerebralJson.Options) == JsonSerializer.Serialize(restored, CerebralJson.Options) &&
                    afterRetry.Revision == restored.HostRevision,
                    "exact restore retry after restart returns the original result without another settings commit");
                var afterBackup = await ReadAsync<ConfigurationBackupExport>(restarted, "configuration.backup.export", new { }, deadline.Token);
                Check(JsonSerializer.Serialize(afterBackup.Document.Host, CerebralJson.Options) ==
                      JsonSerializer.Serialize(backup.Document.Host, CerebralJson.Options) && afterBackup.Document.Paths == backup.Document.Paths,
                    "export after restart reproduces the restored portable host settings and path references");
                Check(afterBackup.Document.Prompts?.SequenceEqual(backup.Document.Prompts!) == true,
                    "all restored prompt layers are reproduced after actual Worker restart");
                var reopenedLead = await ReadAsync<SystemInstructionDocument>(restarted, "instructions.get", new { role = "lead" }, deadline.Token);
                Check(reopenedLead.Content == promptText && reopenedLead.Sha256 == restoredLead.Sha256,
                    "prompt revision and content survive actual process replacement");
                var reopenedProfile = await ReadAsync<AgentInstructionDocument>(restarted, "agents.instructions.get",
                    new { profileId = "pipe-reviewer" }, deadline.Token);
                Check(reopenedProfile.Content == profileSaved.Content && reopenedProfile.Revision > profileSaved.Revision,
                    "profile prompt content survives real restore and process replacement with the restored profile revision");
                Check(afterBackup.Document.Connections is not null &&
                    JsonSerializer.Serialize(afterBackup.Document.Connections, CerebralJson.Options) ==
                    JsonSerializer.Serialize(backup.Document.Connections, CerebralJson.Options),
                    "portable connection declarations survive actual restore and process replacement");
                await StopAsync(restarted, child, deadline.Token);
                Check(child.ExitCode == 0, "restarted pipe-only Worker also shuts down through the normal local stop command");
            }
        }
        catch (Exception error)
        {
            var diagnostics = child is null ? "No child is currently running." : await child.DiagnosticsAsync();
            throw new InvalidOperationException("Local Worker pipe host acceptance failed: " + error.Message + "\n" + diagnostics, error);
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
            // Delete only the exact temporary tree created by this fixture, after its child has exited.
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!IsWithin(scratch, temporaryRoot) || !Path.GetFileName(scratch).StartsWith("cerebral-local-pipe-host-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test-owned temporary directory.");
            Directory.Delete(scratch, recursive: true);
        }
        Console.WriteLine($"Local Worker host: {checks} real child-process / named-pipe / dispatcher / SQLite checks passed. No model inference, installed service, UI or PAO workflow claim.");
    }

    internal static void Seed(string dataPath, string scratch, int httpPort)
    {
        var store = new StateStore(dataPath);
        var configuration = new Configuration(store);
        var settings = configuration.Get();
        settings.ListenUrl = "http://127.0.0.1:" + httpPort;
        settings.AllowLan = false;
        settings.ModelDirectory = Path.Combine(scratch, "models");
        settings.ProjectsDirectory = Path.Combine(scratch, "projects");
        settings.SharedDirectory = Path.Combine(scratch, "shared");
        settings.BackgroundTasksEnabled = false;
        settings.BackgroundTasks = [];
        settings.McpServers = [];
        settings.Workers = [];
        Directory.CreateDirectory(settings.ModelDirectory);
        configuration.Save(settings);
        // Registration metadata is sufficient for profile authorization checks. There is deliberately
        // no GGUF file, download or loaded model; restore reports the missing local reference.
        var model = new ModelRecord
        {
            Id = "pipe-test-model", Name = "Unloaded pipe-test registration", Path = Path.Combine(settings.ModelDirectory, "not-present.gguf"),
            State = "unloaded", AutomaticContextSizing = false, ContextTokens = 4096, MaximumContextTokens = 4096
        };
        store.Put("model", model.Id, model);
        foreach (var id in new[] { "pipe-reviewer", "pipe-admin" })
        {
            var profile = new AgentProfile
            {
                Id = id, Name = id, ModelId = model.Id, ContextTokens = 4096, ResponseTokens = 1024,
                FollowsModelBudgets = false, AutomaticCompaction = false, AllowedRoots = [settings.ProjectsDirectory], AllowedTools = [],
                Capabilities = id == "pipe-admin" ? ["administration"] : ["diagnostics"]
            };
            Configuration.ValidateProfile(profile);
            store.Put("agent", profile.Id, profile);
        }
    }

    private static async Task<CommandResult> CallAsync(LocalWorkerClient client, string operation, object arguments,
        CancellationToken cancellationToken, string? profileId = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await client.CommandAsync(new(operation, CerebralJson.Element(arguments), profileId), deadline.Token);
    }

    private static async Task<T> ReadAsync<T>(LocalWorkerClient client, string operation, object arguments, CancellationToken cancellationToken)
    {
        var result = await CallAsync(client, operation, arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(operation + " failed: " + result.Error);
        return CerebralJson.Read<T>(result.Data);
    }

    private static async Task StopAsync(LocalWorkerClient client, ChildWorker child, CancellationToken cancellationToken)
    {
        var result = await CallAsync(client, "service.stop-local", new { }, cancellationToken);
        if (!result.Success) throw new InvalidOperationException("Local Worker stop was rejected: " + result.Error);
        await child.WaitForExitAsync(cancellationToken);
    }

    private static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal sealed class ChildWorker : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string dataPath;
        private readonly BoundedLog output = new(), error = new();
        private readonly Task outputPump, errorPump;
        private int disposed;
        public LocalWorkerEndpoint Endpoint { get; }
        public int ExitCode => process.ExitCode;

        private ChildWorker(Process process, string dataPath, LocalWorkerEndpoint endpoint)
        {
            this.process = process; this.dataPath = dataPath; Endpoint = endpoint;
            outputPump = CaptureAsync(process.StandardOutput, output);
            errorPump = CaptureAsync(process.StandardError, error);
        }

        public static async Task<ChildWorker> StartAsync(string dataPath, string pluginPath, CancellationToken cancellationToken,
            string? enginePath = null, string? workerExecutable = null)
        {
            var assembly = typeof(CommandDispatcher).Assembly.Location;
            if (!File.Exists(assembly) || !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
                throw new FileNotFoundException("Build the Worker and Worker.Tests Release outputs before running the child-host acceptance fixture.");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrWhiteSpace(dotnet))
                dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
                throw new FileNotFoundException("The local .NET host executable is unavailable.");
            if (workerExecutable is not null && (!Path.IsPathFullyQualified(workerExecutable) || !File.Exists(workerExecutable) ||
                !Path.GetFileName(workerExecutable).Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Choose the exact existing Worker executable for process-identity acceptance.");
            if (workerExecutable is not null) ScopedFilePolicy.RejectLinks(workerExecutable);
            var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.HostTest." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo(workerExecutable ?? dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(workerExecutable ?? assembly)!
            };
            if (workerExecutable is null) start.ArgumentList.Add(assembly);
            foreach (var argument in new[] { "--local-run", "--data", dataPath, "--pipe-name", endpoint.PipeName,
                "--pipe-instance", endpoint.InstanceId, "--service-plugins", pluginPath }) start.ArgumentList.Add(argument);
            if (enginePath is not null) { start.ArgumentList.Add("--engines"); start.ArgumentList.Add(enginePath); }
            var child = new ChildWorker(Process.Start(start) ?? throw new InvalidOperationException("The isolated Worker process did not start."), dataPath, endpoint);
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(25));
                using var client = new LocalWorkerClient(endpoint);
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (child.process.HasExited) throw new InvalidOperationException("Worker exited during startup with code " + child.process.ExitCode + ".\n" + await child.DiagnosticsAsync());
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var result = await client.CommandAsync(new("health", CerebralJson.Element(new { })), probe.Token);
                        if (!result.Success) throw new InvalidOperationException("Worker rejected its startup health query: " + result.Error);
                        return child;
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                    {
                        // Only the side-effect-free health query is repeated while the process starts.
                        startup.Token.ThrowIfCancellationRequested();
                        await Task.Delay(100, startup.Token);
                    }
                }
            }
            catch (Exception failure)
            {
                var diagnostics = await child.DiagnosticsAsync();
                await child.DisposeAsync();
                throw new InvalidOperationException("Isolated Worker startup failed: " + failure.Message + "\n" + diagnostics, failure);
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(outputPump, errorPump).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        public async Task<string> DiagnosticsAsync()
        {
            var local = "";
            var path = Path.Combine(dataPath, "local-worker.log");
            try
            {
                if (File.Exists(path))
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    file.Seek(Math.Max(0, file.Length - 16 * 1024), SeekOrigin.Begin);
                    using var reader = new StreamReader(file);
                    var buffer = new char[16 * 1024];
                    var length = await reader.ReadAsync(buffer);
                    local = new string(buffer, 0, length);
                }
            }
            catch (IOException) { local = "Local Worker log was unavailable."; }
            return "Bounded child output:\n" + output.Snapshot() + "\nBounded child error:\n" + error.Snapshot() + "\nBounded local Worker log:\n" + local;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(stop.Token);
                await Task.WhenAll(outputPump, errorPump).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { process.Dispose(); }
        }

        private static async Task CaptureAsync(StreamReader reader, BoundedLog target)
        {
            var buffer = new char[1024];
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read == 0) return;
                target.Append(buffer.AsSpan(0, read));
            }
        }
    }

    private sealed class BoundedLog
    {
        private readonly StringBuilder text = new();
        public void Append(ReadOnlySpan<char> value)
        {
            lock (text)
            {
                text.Append(value);
                if (text.Length > 16 * 1024) text.Remove(0, text.Length - 16 * 1024);
            }
        }
        public string Snapshot() { lock (text) return text.ToString(); }
    }
}
