using System.Diagnostics;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.ReviewPlugin;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual local Worker, loaded review DLL and shown WPF form. No model or installed-state claim.</summary>
public static class WorkflowDesktopLiveTests
{
    public static async Task RunAsync(string desktopDll, string? preview = null)
    {
        desktopDll = Path.GetFullPath(desktopDll);
        if (!File.Exists(desktopDll)) throw new FileNotFoundException("Build the source desktop first.", desktopDll);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-workflow-desktop-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? worker = null; Process? desktop = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var data = Path.Combine(root, "data"); var store = new StateStore(data);
            var configuration = new Configuration(store);
            var settings = configuration.Get(); settings.McpServers.Clear(); settings.BackgroundTasks.Clear(); configuration.Save(settings);
            store.Put("agent", "reviewer", new AgentProfile { Id = "reviewer", Name = "Cerebral", Role = "lead",
                FollowsDefaultModel = true, Capabilities = ["diagnostics"] });
            var packages = Path.Combine(root, "plugins"); var package = Path.Combine(packages, "example.review");
            Directory.CreateDirectory(package);
            CopyReviewPackageBinaries(package);
            File.WriteAllText(Path.Combine(package, "service-plugin.json"), """
                {"schemaVersion":1,"contract":"pegasus.cerebral.service","apiVersion":1,"id":"example.review",
                 "name":"Review example","version":"1.0.0","description":"Text review example",
                 "assembly":"Cerebral.ReviewPlugin.dll","entryType":"Cerebral.ReviewPlugin.ExampleReviewPlugin"}
                """);
            var endpoint = new LocalWorkerEndpoint("Cerebral.WorkflowDesktop." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            ProcessStartInfo StartInfo(string dll)
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(dll)! };
                start.ArgumentList.Add(dll); return start;
            }
            var start = StartInfo(typeof(CommandDispatcher).Assembly.Location);
            foreach (var arg in new[] { "--local-run", "--data", data, "--pipe-name", endpoint.PipeName,
                "--pipe-instance", endpoint.InstanceId, "--service-plugins", packages }) start.ArgumentList.Add(arg);
            worker = Process.Start(start) ?? throw new IOException("Worker fixture failed to launch.");
            using var client = new CerebralClient(endpoint);
            async Task WaitHealth()
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (worker.HasExited) throw new IOException("Worker exited: " + File.ReadAllText(Path.Combine(data, "local-worker.log")));
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    probe.CancelAfter(TimeSpan.FromMilliseconds(500));
                    try { var host = await client.GetAsync<HostInfo>("health", cancellationToken: probe.Token); if (host.DataDirectory == data) return; throw new IOException("Wrong fixture data root."); }
                    catch (Exception error) when ((error is OperationCanceledException or LocalWorkerCommandUncertainException) && !deadline.IsCancellationRequested) { }
                    await Task.Delay(100, deadline.Token);
                }
                throw new TimeoutException("Worker fixture did not become ready.");
            }
            await WaitHealth();
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest("desktop-review", "desktop-conversation",
                "reviewer", ExampleReviewPlugin.DefinitionId, "1.0.0", "Review this passage.", "INTERNAL-ASSIGNMENT-NOT-FOR-TRANSCRIPT",
                CerebralJson.Element(new { text = "one two three" })), cancellationToken: deadline.Token);
            for (var attempt = 0; run.Questions is null && attempt < 100; attempt++)
            {
                await Task.Delay(100, deadline.Token);
                run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = run.Id }, cancellationToken: deadline.Token);
            }
            if (run.Questions?.Fields.Count != 2) throw new Exception("Actual review module did not reach its two-field form.");
            var uiStart = StartInfo(desktopDll); uiStart.RedirectStandardOutput = uiStart.RedirectStandardError = true;
            foreach (var arg in new[] { "--check-workflow-semantic-restart-service", endpoint.PipeName, endpoint.InstanceId, data, run.Id })
                uiStart.ArgumentList.Add(arg);
            if (preview is not null) { uiStart.ArgumentList.Add("--preview"); uiStart.ArgumentList.Add(Path.GetFullPath(preview)); }
            desktop = Process.Start(uiStart) ?? throw new IOException("Desktop fixture failed to launch.");
            var stdout = desktop.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = desktop.StandardError.ReadToEndAsync(deadline.Token);
            await desktop.WaitForExitAsync(deadline.Token);
            var output = await stdout; var errorOutput = await stderr;
            if (desktop.ExitCode != 0) throw new Exception("Shown workflow check failed: " + output + errorOutput);
            Console.WriteLine(output);
            var completed = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = run.Id }, cancellationToken: deadline.Token);
            if (completed.Status != WorkflowStatus.Completed || completed.Result?.GetProperty("wordCount").GetInt32() != 3)
                throw new Exception("Service did not retain the actual completed result from WPF answers.");
            var recent = await client.GetAsync<WorkflowRecentPage>("jobs.workflow.recent", new { limit = 1 }, cancellationToken: deadline.Token);
            if (recent.Runs.Single().Id != run.Id || recent.Runs[0].Status != WorkflowStatus.Completed)
                throw new Exception("Completed work was missing from compact recent history.");
            var eventResult = await client.CommandAsync("jobs.workflow.events", new { id = run.Id, limit = 200 }, cancellationToken: deadline.Token);
            if (!eventResult.Success) throw new Exception(eventResult.Error);
            var events = eventResult.Data.Deserialize<JournalPage>(CerebralJson.Options)
                ?? throw new InvalidDataException("The Worker returned no event page.");
            if (events.Events.Count(x => x.Type == "workflow.answers") != 1 ||
                events.Events.Count(x => x.Type == CerebralEventTypes.ToolCall) != 1)
                throw new Exception("Semantic answer reconciliation created duplicate answer or tool-call events.");
            var stopped = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            if (!stopped.Success) throw new Exception(stopped.Error);
            await worker.WaitForExitAsync(deadline.Token);
            if (worker.ExitCode != 0) throw new Exception("Worker did not shut down cleanly.");
            var reviewDatabase = Directory.GetFiles(data, "review.db", SearchOption.AllDirectories).Single();
            using (var review = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = reviewDatabase }.ToString()))
            {
                review.Open(); using var command = review.CreateCommand();
                command.CommandText = "SELECT COUNT(*), MIN(label), MIN(style) FROM review_receipts;";
                using var reader = command.ExecuteReader(); reader.Read();
                if (reader.GetInt64(0) != 1 || reader.GetString(1) != "Semantic passage" || reader.GetString(2) != "detailed")
                    throw new Exception("Semantic answer reconciliation did not retain exactly one package-owned review record.");
            }
            Console.WriteLine("Actual Worker + review DLL + three shown WPF desktop generations + semantic lost-ack retry + SQLite result acceptance passed; no LLM, speech or installed-state changes.");
        }
        finally
        {
            foreach (var process in new[] { desktop, worker })
            {
                if (process is null) continue;
                if (!process.HasExited) { process.Kill(entireProcessTree: true); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(cleanup.Token); }
                process.Dispose();
            }
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-workflow-desktop-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }

    private static void CopyReviewPackageBinaries(string package)
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Pegasus.Cerebral.slnx"))) cursor = cursor.Parent;
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        var output = cursor is null ? null : Path.Combine(cursor.FullName, "examples", "Cerebral.ReviewPlugin", "bin", configuration, "net10.0");
        if (output is null || !File.Exists(Path.Combine(output, "Cerebral.ReviewPlugin.dll")))
            throw new FileNotFoundException("Build the matching review SDK example before the live desktop acceptance.");
        foreach (var source in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(package, Path.GetRelativePath(output, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }
}
