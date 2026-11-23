using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real binary admission and existing engine initialization, confined to disposable state.
/// No owner database, installed service, inference or remote worker is opened by this fixture.</summary>
internal static class WorkerRuntimeContractTests
{
    private const string BaselineContractHash = "64c0ad47e3595c1193af81da8b34d33cf3a6a4fcd4445632683c508286a650ea";
    private const string AdmissionMessage = "This host requires the checkpoint source-map contract.";

    public static async Task RunAsync(string oldContractsPath, string oldEngineRoot, string sourceSelectionPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires secured Windows named pipes.");
        foreach (var path in new[] { oldContractsPath, oldEngineRoot, sourceSelectionPath })
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Acceptance inputs must be exact absolute paths.");
            ScopedFilePolicy.RejectLinks(path);
        }
        if (!File.Exists(oldContractsPath) || Hash(oldContractsPath) != BaselineContractHash)
            throw new InvalidOperationException("The preserved old Contracts binary does not match the approved baseline.");
        if (!Directory.Exists(oldEngineRoot) || !File.Exists(sourceSelectionPath))
            throw new FileNotFoundException("Existing engine packages and their exact selection are required.");
        var selectionBytes = File.ReadAllBytes(sourceSelectionPath);
        if (selectionBytes.Length > 8192) throw new InvalidOperationException("Engine selection exceeds the fixture input bound.");
        var selection = Encoding.UTF8.GetString(selectionBytes).Split('\n')
            .Select(line => line.Trim()).Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var selectedSlot = selection["slot"];
        var expectedEpoch = long.Parse(selection["epoch"], CultureInfo.InvariantCulture);
        if (selectedSlot is not ("A" or "B") || selection["pending"] != "False" || selection["paused"] != "False" ||
            selection["known-slot"] != selectedSlot)
            throw new InvalidOperationException("This fixture requires an unpaused, confirmed existing engine selection.");
        var sourceSnapshot = Snapshot(oldEngineRoot);
        var sourceSelectionHash = Hash(sourceSelectionPath);
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-runtime-contract-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        LocalWorkerHostTests.ChildWorker? child = null;
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        try
        {
            var mixedProgram = Path.Combine(scratch, "mixed-program");
            CopyRuntime(mixedProgram);
            File.Copy(oldContractsPath, Path.Combine(mixedProgram, "Cerebral.Contracts.dll"), overwrite: true);
            var emptyPlugins = Path.Combine(scratch, "empty-plugins");
            var emptyEngines = Path.Combine(scratch, "empty-engines");
            Directory.CreateDirectory(emptyPlugins); Directory.CreateDirectory(emptyEngines);
            var missingData = Path.Combine(scratch, "must-remain-absent");
            var rejected = await RunRejectedAsync(mixedProgram, missingData, emptyPlugins, emptyEngines, deadline.Token);
            Check(rejected.ExitCode == 1 && rejected.Output.Contains(AdmissionMessage, StringComparison.Ordinal),
                "actual mixed-binary Worker exits with the actionable checkpoint contract admission error; exit=" + rejected.ExitCode + "; output=" + rejected.Output[..Math.Min(rejected.Output.Length, 2048)]);
            Check(!Directory.Exists(missingData), "rejected startup leaves a nonexistent owner data root absent");

            var existingData = Path.Combine(scratch, "must-remain-unchanged");
            Directory.CreateDirectory(Path.Combine(existingData, "Engine"));
            Directory.CreateDirectory(Path.Combine(existingData, "PluginData", "sentinel"));
            // Invalid SQLite sentinels distinguish early admission from opening or migrating owner stores.
            File.WriteAllText(Path.Combine(existingData, "cerebral.db"), "Owner state must not be opened.");
            File.WriteAllText(Path.Combine(existingData, "engine.db"), "Journal state must not be opened.");
            File.WriteAllBytes(Path.Combine(existingData, "Engine", "selection.ini"), selectionBytes);
            File.WriteAllText(Path.Combine(existingData, "PluginData", "sentinel", "note.txt"), "Keep café.\nSecond line.");
            var before = Snapshot(existingData);
            rejected = await RunRejectedAsync(mixedProgram, existingData, emptyPlugins, emptyEngines, deadline.Token);
            Check(rejected.ExitCode == 1 && rejected.Output.Contains(AdmissionMessage, StringComparison.Ordinal),
                "existing owner state is rejected for contract mismatch before SQLite or engine initialization");
            Check(Snapshot(existingData).SequenceEqual(before), "all existing owner files, hashes and directories remain unchanged");

            var copiedEngines = Path.Combine(scratch, "preserved-engines");
            CopyPackages(oldEngineRoot, copiedEngines);
            Check(Snapshot(copiedEngines).SequenceEqual(sourceSnapshot), "copied A and B packages exactly match the existing engine bytes");
            var positiveData = Path.Combine(scratch, "positive-data");
            LocalWorkerHostTests.Seed(positiveData, scratch, 9);
            Directory.CreateDirectory(Path.Combine(positiveData, "Engine"));
            File.WriteAllBytes(Path.Combine(positiveData, "Engine", "selection.ini"), selectionBytes);
            SqliteConnection.ClearAllPools();
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(positiveData, emptyPlugins, deadline.Token, copiedEngines);
            using var client = new CerebralClient(child.Endpoint);
            var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
            Check(health.LocalRun && Path.GetFullPath(health.DataDirectory) == positiveData,
                "new actual Worker identifies the exact isolated owner root over its secured pipe");
            var initial = await client.GetAsync<EngineStatus>("engine.status", cancellationToken: deadline.Token);
            Check(!initial.Paused && initial.Error is null && initial.Epoch == expectedEpoch &&
                initial.Active?.Slot == selectedSlot && initial.Active.PackageSha256 == selection["package"] &&
                initial.Active.ConfigurationSha256 == selection["configuration"] &&
                initial.KnownGoodSlot == selectedSlot && !initial.CandidatePending,
                "new host natively initializes the preserved selected engine and retains its epoch and exact package pins");
            await client.GetAsync<EngineStatus>("engine.pause", new { expectedEpoch }, cancellationToken: deadline.Token);
            var alternate = selectedSlot == "A" ? "B" : "A";
            var expectedPackage = initial.Slots.Single(slot => slot.Slot == alternate).Package!;
            var candidate = await client.GetAsync<EngineStatus>("engine.activate", new { slot = alternate, expectedEpoch }, cancellationToken: deadline.Token);
            Check(candidate.Error is null && candidate.Active?.Slot == alternate &&
                candidate.Active.PackageSha256 == expectedPackage.PackageSha256 &&
                candidate.Epoch == expectedEpoch + 1 && candidate.Paused && candidate.CandidatePending &&
                candidate.KnownGoodSlot == selectedSlot,
                "new host natively initializes the other preserved engine as an unpromoted candidate with its original fallback");
            Check(!(await client.CommandAsync("engine.promote", new { expectedEpoch = candidate.Epoch }, cancellationToken: deadline.Token)).Success,
                "startup health alone cannot promote the alternate engine");
            Check((await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token)).Success,
                "isolated new Worker accepts normal owner shutdown");
            await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 0, "isolated Worker exits normally after existing-engine acceptance");
            await child.DisposeAsync(); child = null;
            Check(Snapshot(copiedEngines).SequenceEqual(sourceSnapshot), "engine switching did not rewrite either copied package");
            Check(Hash(oldContractsPath) == BaselineContractHash && Hash(sourceSelectionPath) == sourceSelectionHash &&
                Snapshot(oldEngineRoot).SequenceEqual(sourceSnapshot), "all original baseline binaries, engines and owner selection remain unchanged");
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!ScopedFilePolicy.IsWithin(scratch, temporaryRoot) || !Path.GetFileName(scratch).StartsWith("cerebral-runtime-contract-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact test-owned root.");
            Directory.Delete(scratch, recursive: true);
        }
        Console.WriteLine($"Worker runtime contract checks passed: {checks} (real mixed-binary process rejection and native preserved A/B initialization; no owner data, inference or deployment).");
    }

    private static void CopyRuntime(string destination)
    {
        var source = Path.GetDirectoryName(typeof(CommandDispatcher).Assembly.Location)!;
        Directory.CreateDirectory(destination);
        var files = Directory.EnumerateFiles(source).Where(path => Path.GetExtension(path) is ".dll" or ".json").Take(257).ToArray();
        if (files.Length > 256 || files.Sum(path => new FileInfo(path).Length) > 128 * 1024 * 1024)
            throw new InvalidOperationException("Worker runtime fixture exceeds its copy bound.");
        foreach (var file in files) { ScopedFilePolicy.RejectLinks(file); File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); }
        // RID-specific managed dependencies (including ServiceController) are resolved before admission.
        // Preserve the actual child runtime closure instead of attributing a missing fixture dependency to the guard.
        var runtimeRoot = Path.Combine(source, "runtimes");
        if (Directory.Exists(runtimeRoot))
        {
            long bytes = files.Sum(path => new FileInfo(path).Length);
            foreach (var item in Walk(runtimeRoot, 512))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, item));
                if (Directory.Exists(item)) { Directory.CreateDirectory(target); continue; }
                bytes += new FileInfo(item).Length;
                if (bytes > 128 * 1024 * 1024) throw new InvalidOperationException("Worker runtime fixture exceeds its copy bound.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(item, target);
            }
        }
    }

    private static void CopyPackages(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        long bytes = 0; var count = 0;
        foreach (var slot in new[] { "A", "B" })
        {
            var slotRoot = Path.Combine(source, slot);
            ScopedFilePolicy.RejectLinks(slotRoot);
            foreach (var item in Walk(slotRoot, 128))
            {
                ScopedFilePolicy.RejectLinks(item);
                var target = Path.Combine(destination, Path.GetRelativePath(source, item));
                if (Directory.Exists(item)) { Directory.CreateDirectory(target); continue; }
                bytes += new FileInfo(item).Length;
                if (++count > 128 || bytes > 32 * 1024 * 1024) throw new InvalidOperationException("Existing engine fixture exceeds its copy bound.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(item, target);
            }
        }
    }

    private static string[] Snapshot(string root)
    {
        var items = Walk(root, 512);
        return items.Select(path =>
        {
            ScopedFilePolicy.RejectLinks(path);
            return Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "|directory" : "|file|" + Hash(path));
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> Walk(string root, int limit)
    {
        ScopedFilePolicy.RejectLinks(root);
        var pending = new Stack<string>(); pending.Push(root);
        var items = new List<string>();
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ScopedFilePolicy.RejectLinks(path);
                if (items.Count == limit) throw new InvalidOperationException("Filesystem fixture exceeds its entry bound.");
                items.Add(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
        return items;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<(int ExitCode, string Output)> RunRejectedAsync(string program, string data,
        string plugins, string engines, CancellationToken cancellationToken)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet)) dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet)) throw new FileNotFoundException("The local .NET host executable is unavailable.");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = program
        };
        foreach (var argument in new[] { Path.Combine(program, "Cerebral.Worker.dll"), "--local-run", "--data", data,
            "--pipe-name", "Pegasus.Cerebral.ContractTest." + Guid.NewGuid().ToString("N"),
            "--pipe-instance", Guid.NewGuid().ToString("N"), "--service-plugins", plugins, "--engines", engines })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Mixed-binary child did not start.");
        var output = CaptureAsync(process.StandardOutput); var error = CaptureAsync(process.StandardError);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + "\n" + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader)
    {
        var captured = new StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) return captured.ToString();
            captured.Append(buffer, 0, read);
            if (captured.Length > 16 * 1024) captured.Remove(0, captured.Length - 16 * 1024);
        }
    }
}
