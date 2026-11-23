using System.Diagnostics;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Separate source Worker and desktop, reopening the actual Programming fixture after its providers stop.</summary>
public static class ProgrammingWorkflowDesktopLiveTests
{
    public static Task RunAsync(string desktopDll, string modelPath = ProgrammingWorkflowLiveTests.DefaultModelPath,
        string serverPath = ProgrammingWorkflowLiveTests.DefaultServerPath,
        string cortexPath = ProgrammingWorkflowLiveTests.DefaultCortexPath, CancellationToken cancellationToken = default) =>
        ProgrammingWorkflowLiveTests.RunAsync(modelPath, serverPath, cortexPath, cancellationToken,
            requireCompaction: false, desktopDll: desktopDll);

    internal static async Task ContinueAsync(string desktopDll, string repository, string root,
        WorkflowRun expected, CancellationToken cancellationToken)
    {
        var expectedResult = expected.Result ?? throw new InvalidDataException("Completed Programming result is missing.");
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var data = Path.Combine(root, "state");
        var packages = Path.Combine(root, "service-plugins");
        var package = Path.Combine(packages, "pegasus.programming");
        Directory.CreateDirectory(package);
        File.Copy(typeof(ProgrammingServicePlugin).Assembly.Location, Path.Combine(package, "Cerebral.Programming.dll"));
        File.Copy(Path.Combine(repository, "src", "Cerebral.Programming", "service-plugin.json"),
            Path.Combine(package, "service-plugin.json"));
        // Close pooled handles from the first host before the source Worker takes ownership.
        SqliteConnection.ClearAllPools();
        Process? worker = null; Process? desktop = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(220));
        var endpoint = new LocalWorkerEndpoint("Cerebral.ProgrammingDesktop." + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        ProcessStartInfo StartInfo(string dll)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(dll)! };
            start.ArgumentList.Add(dll); return start;
        }
        try
        {
            var start = StartInfo(typeof(CommandDispatcher).Assembly.Location);
            foreach (var arg in new[] { "--local-run", "--data", data, "--pipe-name", endpoint.PipeName,
                "--pipe-instance", endpoint.InstanceId, "--service-plugins", packages }) start.ArgumentList.Add(arg);
            worker = Process.Start(start) ?? throw new IOException("The isolated source Worker did not start.");
            using var client = new CerebralClient(endpoint);
            var healthy = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (worker.HasExited) throw new IOException("Source Worker exited before readiness. See " + Path.Combine(data, "local-worker.log"));
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probe.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var host = await client.GetAsync<HostInfo>("health", cancellationToken: probe.Token);
                    Check(Path.GetFullPath(host.DataDirectory).Equals(Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase),
                        "The new Worker must own the exact existing isolated database.");
                    healthy = true; break;
                }
                catch (Exception error) when ((error is OperationCanceledException or LocalWorkerCommandUncertainException) && !deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
            if (!healthy) throw new TimeoutException("The new Worker did not become ready.");
            var reopened = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = expected.Id }, cancellationToken: deadline.Token);
            Check(reopened.Status == WorkflowStatus.Completed && reopened.Revision == expected.Revision &&
                JsonElement.DeepEquals(reopened.Result!.Value, expectedResult),
                "A separate source Worker must preserve the exact Programming run, revision and checked receipt IDs.");
            Check(expectedResult.GetProperty("checks").GetArrayLength() is >= 2 and <= 16,
                "The inspected verification manifest must remain bounded.");
            var receiptsBefore = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var check in expectedResult.GetProperty("checks").EnumerateArray())
            {
                var id = check.GetProperty("receiptId").GetString()!;
                var page = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                    new WorkflowReceiptRequest(expected.Id, id, Limit: 16384), cancellationToken: deadline.Token);
                Check(page.State == WorkflowEffectState.Succeeded && !page.Truncated,
                    "Each retained successful build/run receipt must remain available without rerunning the command.");
                using var receipt = JsonDocument.Parse(page.Json);
                receiptsBefore.Add(id, receipt.RootElement.Clone());
            }
            Check(expectedResult.GetProperty("checks").EnumerateArray().Select(x => x.GetProperty("kind").GetString())
                .ToHashSet(StringComparer.Ordinal).SetEquals(["build", "run"]),
                "The native phase must inherit both kinds of validated project checks.");
            var uiStart = StartInfo(desktopDll);
            uiStart.RedirectStandardOutput = uiStart.RedirectStandardError = true;
            foreach (var arg in new[] { "--check-programming-control-service", endpoint.PipeName, endpoint.InstanceId,
                data, expected.Id, expected.Submission.ConversationId }) uiStart.ArgumentList.Add(arg);
            desktop = Process.Start(uiStart) ?? throw new IOException("The isolated native desktop did not start.");
            var output = desktop.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = desktop.StandardError.ReadToEndAsync(deadline.Token);
            await desktop.WaitForExitAsync(deadline.Token);
            var stdout = await output; var stderr = await errors;
            if (desktop.ExitCode != 0) throw new InvalidOperationException("Programming desktop acceptance failed: " + stdout + stderr);
            Console.WriteLine(stdout);
            var after = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = expected.Id }, cancellationToken: deadline.Token);
            Check(after.Revision == expected.Revision && after.Status == WorkflowStatus.Completed &&
                JsonElement.DeepEquals(after.Result!.Value, expectedResult),
                "Ordinary continuation must not rewrite or replay the completed Programming assignment.");
            foreach (var (id, original) in receiptsBefore)
            {
                var page = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                    new WorkflowReceiptRequest(expected.Id, id, Limit: 16384), cancellationToken: deadline.Token);
                using var receipt = JsonDocument.Parse(page.Json);
                Check(!page.Truncated && JsonElement.DeepEquals(original, receipt.RootElement),
                    "Build/run provider evidence must remain byte-semantically unchanged after native continuation.");
            }
            var conversation = await client.GetAsync<ConversationSummary>("conversations.get",
                new { id = expected.Submission.ConversationId }, cancellationToken: deadline.Token);
            var continued = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = conversation.LatestRunId }, cancellationToken: deadline.Token);
            Check(continued.Status == WorkflowStatus.Completed && continued.Id != expected.Id &&
                continued.Submission.ConversationId == expected.Submission.ConversationId &&
                continued.Submission.DefinitionId == ConversationWorkflowModule.DefinitionId,
                "The source Worker must retain the native ordinary Chat continuation in the same conversation.");
            var unload = await client.CommandAsync("models.unload", new { id = "programming-live-model" }, cancellationToken: deadline.Token);
            Check(unload.Success, "The second host must unload its test-owned model.");
            var stopped = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            Check(stopped.Success, "The source Worker must accept orderly shutdown.");
            await worker.WaitForExitAsync(deadline.Token);
            Check(worker.ExitCode == 0, "The source Worker must exit cleanly.");
            Console.WriteLine($"Programming restart/native continuation: {checks} actual process/pipe/SQLite checks passed. Real PAO build/run evidence was retained; the later response used the ordinary Chat harness. Installed state was not changed.");
        }
        finally
        {
            // These exact child objects belong to this fixture; never terminate a process by name.
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
        }
    }
}
