using System.Diagnostics;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Consumes an existing generated package read-only; supplies no module source or model output.</summary>
public static class GeneratedWorkflowDesktopLiveTests
{
    public static async Task RunAsync(string desktopDll, string packageRepository)
    {
        desktopDll = Path.GetFullPath(desktopDll); packageRepository = Path.GetFullPath(packageRepository);
        if (!File.Exists(desktopDll)) throw new FileNotFoundException("Build the source desktop first.", desktopDll);
        if (!File.Exists(Path.Combine(packageRepository, "acceptance.cerebral.generated-review", "service-plugin.json")))
            throw new DirectoryNotFoundException("The retained generated service package is absent.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-generated-desktop-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? worker = null; Process? desktop = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            var data = Path.Combine(root, "data");
            var store = new StateStore(data); var configuration = new Configuration(store);
            var settings = configuration.Get(); settings.McpServers.Clear(); settings.BackgroundTasks.Clear(); configuration.Save(settings);
            store.Put("agent", "reviewer", new AgentProfile { Id = "reviewer", Name = "Cerebral", Role = "lead",
                FollowsDefaultModel = true, WorkerId = "local", Capabilities = ["diagnostics"], AllowedRoots = [root] });
            var endpoint = new LocalWorkerEndpoint("Cerebral.GeneratedDesktop." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            ProcessStartInfo StartInfo(string dll)
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(dll)! };
                start.ArgumentList.Add(dll); return start;
            }
            var start = StartInfo(typeof(CommandDispatcher).Assembly.Location);
            foreach (var arg in new[] { "--local-run", "--data", data, "--pipe-name", endpoint.PipeName,
                "--pipe-instance", endpoint.InstanceId, "--service-plugins", packageRepository }) start.ArgumentList.Add(arg);
            worker = Process.Start(start) ?? throw new IOException("Generated workflow Worker fixture failed to launch.");
            using var client = new CerebralClient(endpoint);
            var healthy = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (worker.HasExited) throw new IOException("Worker fixture exited before health; inspect " + Path.Combine(data, "local-worker.log"));
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probe.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var host = await client.GetAsync<HostInfo>("health", cancellationToken: probe.Token);
                    if (!host.DataDirectory.Equals(data, StringComparison.OrdinalIgnoreCase)) throw new IOException("Wrong fixture data root.");
                    healthy = true; break;
                }
                catch (Exception error) when ((error is OperationCanceledException or LocalWorkerCommandUncertainException) && !deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
            if (!healthy) throw new TimeoutException("Worker fixture did not become ready.");
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest("generated-desktop-review", "generated-desktop-conversation",
                "reviewer", "acceptance.cerebral.generated-review:review", "1.0.0", "Review this four-word passage.",
                "INTERNAL-GENERATED-ASSIGNMENT", CerebralJson.Element(new { text = "one two three four" })), cancellationToken: deadline.Token);
            for (var attempt = 0; run.Questions is null && attempt < 100; attempt++)
            {
                if (run.IsTerminal) throw new InvalidOperationException("Generated workflow stopped before its question: " + run.Error);
                await Task.Delay(100, deadline.Token);
                run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = run.Id }, cancellationToken: deadline.Token);
            }
            if (run.Questions?.Fields.Count != 2) throw new InvalidOperationException("Actual generated module did not reach its two-field question.");
            var uiStart = StartInfo(desktopDll); uiStart.RedirectStandardOutput = uiStart.RedirectStandardError = true;
            foreach (var arg in new[] { "--check-workflow-control-service", endpoint.PipeName, endpoint.InstanceId, data, run.Id }) uiStart.ArgumentList.Add(arg);
            desktop = Process.Start(uiStart) ?? throw new IOException("Desktop-control fixture failed to launch.");
            var stdout = desktop.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = desktop.StandardError.ReadToEndAsync(deadline.Token);
            await desktop.WaitForExitAsync(deadline.Token);
            var output = await stdout; var errors = await stderr;
            if (desktop.ExitCode != 0) throw new InvalidOperationException("Generated workflow desktop-control acceptance failed: " + output + errors);
            Console.WriteLine(output);
            var completed = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = run.Id }, cancellationToken: deadline.Token);
            if (completed.Status != WorkflowStatus.Completed || completed.Result?.GetProperty("wordCount").GetInt32() != 4)
                throw new InvalidOperationException("The separate Worker connection did not observe the completed four-word result.");
            var recent = await client.GetAsync<WorkflowRecentPage>("jobs.workflow.recent", new { limit = 1 }, cancellationToken: deadline.Token);
            if (recent.Runs.Single().Id != run.Id || recent.Runs[0].Status != WorkflowStatus.Completed)
                throw new InvalidOperationException("The completed generated run was missing from durable recent history.");
            var stopped = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            if (!stopped.Success) throw new InvalidOperationException(stopped.Error);
            await worker.WaitForExitAsync(deadline.Token);
            if (worker.ExitCode != 0) throw new IOException("Worker fixture did not shut down cleanly.");
            Console.WriteLine("Generated package -> isolated Worker -> desktop semantic Work question -> durable result acceptance passed; no model, installation or saved user settings.");
        }
        finally
        {
            foreach (var process in new[] { desktop, worker })
            {
                if (process is null) continue;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(cleanup.Token);
                }
                process.Dispose();
            }
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-generated-desktop-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }
}
