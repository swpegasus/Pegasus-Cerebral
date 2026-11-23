using System.Diagnostics;
using System.IO;
using Cerebral.Desktop;

namespace Cerebral.Desktop.Tests;

/// <summary>Two real process writers race one synthetic connection revision. No product
/// preference path, installed Worker, service, or registry value is used.</summary>
internal static class ConnectionStoreConcurrencyChecks
{
    internal static int RunChild(string[] args)
    {
        if (args.Length != 6 || args[0] != "--connection-store-writer-child")
            throw new ArgumentException("The connection writer fixture requires its exact arguments.");
        var path = Path.GetFullPath(args[1]);
        var ready = Path.GetFullPath(args[4]);
        var release = Path.GetFullPath(args[5]);
        var directory = Path.GetDirectoryName(path)!;
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!directory.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("cerebral-connection-cas-", StringComparison.Ordinal) ||
            Path.GetFileName(path) != "connection.json")
            throw new ArgumentException("The writer child requires its isolated temp connection file.");
        if (Path.GetDirectoryName(path) != Path.GetDirectoryName(ready) ||
            Path.GetDirectoryName(path) != Path.GetDirectoryName(release))
            throw new ArgumentException("Fixture control paths must share one isolated directory.");
        File.WriteAllText(ready, "ready");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Fixture release was not received.");
            Thread.Sleep(10);
        }
        try
        {
            _ = ConnectionStore.SaveFromIntegrator(new() { Url = args[3] }, args[2], path);
            Console.WriteLine("committed");
            return 0;
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Saved connection settings changed.", StringComparison.Ordinal))
        {
            Console.WriteLine("revision-conflict");
            return 3;
        }
    }

    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-connection-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "connection.json");
        var release = Path.Combine(root, "release");
        Process? first = null;
        Process? second = null;
        try
        {
            var initial = ConnectionStore.Read(path);
            if (initial.Saved || initial.Revision != "default") throw new InvalidOperationException("Synthetic connection was not empty.");
            first = StartChild(path, initial.Revision, "http://127.0.0.1:16131", Path.Combine(root, "ready-a"), release);
            second = StartChild(path, initial.Revision, "http://127.0.0.1:16132", Path.Combine(root, "ready-b"), release);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(root, "ready-a")) || !File.Exists(Path.Combine(root, "ready-b")))
            {
                if (first.HasExited || second.HasExited || wait.Elapsed > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("Both connection writer processes did not reach the barrier.");
                Thread.Sleep(10);
            }
            File.WriteAllText(release, "go");
            if (!first.WaitForExit(15000) || !second.WaitForExit(15000))
                throw new TimeoutException("Connection writer fixture did not settle.");
            var exits = new[] { first.ExitCode, second.ExitCode }.OrderBy(x => x).ToArray();
            if (!exits.SequenceEqual([0, 3]))
                throw new InvalidOperationException("Both same-revision writers committed or a fixture child failed: " +
                    first.StandardOutput.ReadToEnd() + first.StandardError.ReadToEnd() +
                    second.StandardOutput.ReadToEnd() + second.StandardError.ReadToEnd());
            var winner = ConnectionStore.Read(path);
            if (!winner.Saved || winner.Error is not null ||
                winner.Preferences?.Url is not ("http://127.0.0.1:16131" or "http://127.0.0.1:16132"))
                throw new InvalidOperationException("The one committed connection was not retained exactly.");
            var staleRefused = false;
            try { _ = ConnectionStore.SaveFromIntegrator(new() { Url = "http://127.0.0.1:16134" }, initial.Revision, path); }
            catch (InvalidOperationException) { staleRefused = true; }
            if (!staleRefused || ConnectionStore.Read(path).Revision != winner.Revision)
                throw new InvalidOperationException("A stale revision changed the committed connection.");
            // The parent threw while holding the lease. A current CAS in the same process must acquire it now.
            var next = ConnectionStore.SaveFromIntegrator(new() { Url = "http://127.0.0.1:16133" }, winner.Revision, path);
            if (!next.Saved || next.Preferences?.Url != "http://127.0.0.1:16133")
                throw new InvalidOperationException("The save lease remained held after a revision conflict.");
            return 6;
        }
        finally
        {
            File.WriteAllText(release, "go");
            foreach (var child in new[] { first, second })
            {
                if (child is null) continue;
                _ = child.WaitForExit(15000);
                child.Dispose();
            }
            var canonical = Path.GetFullPath(root);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cerebral-connection-cas-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside the exact temp directory.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static Process StartChild(string path, string revision, string url, string ready, string release)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable is unavailable.");
        if (!Path.GetFileName(executable).Equals("Cerebral.Desktop.Tests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the connection concurrency fixture from its compiled apphost.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in new[] { "--connection-store-writer-child", path, revision, url, ready, release })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Connection writer fixture child did not start.");
    }
}
