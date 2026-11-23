using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Opt-in native acceptance only. Isolates all desktop preferences before constructing a window.</summary>
public static class ProgrammingDesktopAcceptance
{
    public static int Run(Application app, LocalWorkerEndpoint endpoint, string dataRoot, string runId, string conversationId)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-programming-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var preferences = DesktopPreferenceDiagnostics.VerifyAndUseIsolatedProcess(root);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var exit = 0;
            var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "plugins")),
                Path.Combine(root, "plugin-data"));
            try
            {
                var checks = await window.CheckProgrammingControlServiceAsync(endpoint, dataRoot, runId, conversationId, root);
                Console.WriteLine($"Native Programming continuity: {checks} actual shown desktop/semantic pipe checks and {preferences} isolated preference checks passed.");
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally
            {
                window.Close();
                try { Directory.Delete(root, true); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                app.Shutdown(exit);
            }
        }));
        return app.Run();
    }
}

public sealed partial class OrchestratorWindow
{
    public async Task<int> CheckProgrammingControlServiceAsync(LocalWorkerEndpoint endpoint, string dataRoot,
        string runId, string conversationId, string diagnosticRoot)
    {
        Dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        using var client = new CerebralClient(endpoint);
        var host = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        Check(Path.GetFullPath(host.DataDirectory).Equals(Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase),
            "Native continuity must use the exact existing isolated Worker database.");
        _ = await InspectServiceUiAsync(endpoint, dataRoot, null).WaitAsync(deadline.Token);
        // This explicit live lane includes model reload; ordinary short inspection uses 45 seconds.
        Lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        Show();
        Check(IsVisible && ServiceConnected && workflowWorkspace is not null,
            "The source native desktop must be shown and connected.");
        await using var server = await DesktopControlServer.StartAsync(this, diagnosticRoot, deadline.Token);
        Task<DesktopControlResponse> Call(string operation, object arguments, string? id = null) =>
            DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation, id ?? Guid.NewGuid().ToString("N"),
                JsonSerializer.Serialize(arguments, CerebralJson.Options), deadline.Token);
        var saved = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
        Check(saved.Submission.DefinitionId == "pegasus.programming:develop" && saved.Submission.ConversationId == conversationId &&
            saved.Status == WorkflowStatus.Completed, "The source Worker must reopen the actual completed Programming run.");
        var savedResult = saved.Result ?? throw new InvalidDataException("The checked Programming result is missing.");
        var opened = await Call("workflow.open", new { runId });
        Check(opened.Success && opened.Data.GetProperty("runId").GetString() == runId &&
            opened.Data.GetProperty("conversationId").GetString() == conversationId &&
            opened.Data.GetProperty("status").GetString() == "completed" &&
            Tabs.SelectedItem is TabItem { Tag: "work" },
            "Semantic Work open must select the exact durable Programming run.");
        Check(JsonElement.DeepEquals(opened.Data.GetProperty("result"), savedResult),
            "Native Work must project the exact host-qualified result and its receipt identities.");
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in savedResult.GetProperty("checks").EnumerateArray())
        {
            var kind = check.GetProperty("kind").GetString()!;
            var id = check.GetProperty("receiptId").GetString()!;
            var page = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                new WorkflowReceiptRequest(runId, id, Limit: 16384), cancellationToken: deadline.Token);
            Check(page.State == WorkflowEffectState.Succeeded && !page.Truncated,
                "Native inspection must resolve each exact retained build/run receipt.");
            using var receipt = JsonDocument.Parse(page.Json);
            var tool = receipt.RootElement.GetProperty("data");
            var evidence = tool.GetProperty("evidence");
            Check(evidence.GetProperty("exitCode").GetInt32() == 0 &&
                evidence.GetProperty("providerId").GetString() == (kind == "build" ? "mcp.cortex" : "cerebral.process"),
                "The retained check must preserve the actual successful PAO build or native run provider.");
            if (kind == "run")
            {
                using var output = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
                Check(output.RootElement.GetProperty("stdout").GetString()?.Trim() == "Hello, durable Cerebral!",
                    "The actual recorded program output must remain readable after reopening.");
            }
            verified.Add(kind);
        }
        Check(verified.SetEquals(["build", "run"]), "Both required Programming checks must be retained.");
        var chat = await Call("conversation.open", new { id = conversationId });
        Check(chat.Success && chat.Data.GetProperty("conversationId").GetString() == conversationId &&
            Tabs.SelectedItem is TabItem { Tag: "chat" }, "Semantic Chat open must return to the same original ordinary conversation.");
        var originalDraft = "Unrelated draft remains separate from Work";
        _ = await Call("composer.set", new { text = originalDraft });
        _ = await Call("workflow.open", new { runId });
        Check(prompt.Text == originalDraft, "Reopening Programming Work must preserve the user's Chat draft.");
        _ = await Call("conversation.open", new { id = conversationId });
        var marker = "CONTINUED-" + Guid.NewGuid().ToString("N")[..8];
        var followup = "From the saved Programming result in this conversation, name the project file and the two successful check kinds. " +
            "Answer in one short sentence, append " + marker + ", and do not run tools or change files.";
        var requestId = Guid.NewGuid().ToString("N");
        var sent = await Call("message.send", new { text = followup }, requestId);
        Check(sent.State == "accepted" && sent.Data.GetProperty("runId").GetString() == requestId,
            "The normal native composer route must accept an ordinary continuation under its exact request ID.");
        var continued = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = requestId }, cancellationToken: deadline.Token);
        while (!continued.IsTerminal)
        {
            if (continued.Questions is not null) throw new InvalidOperationException("The direct continuation unexpectedly requires clarification.");
            await Task.Delay(200, deadline.Token);
            continued = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = requestId }, cancellationToken: deadline.Token);
        }
        Check(continued.Status == WorkflowStatus.Completed &&
            continued.Submission.DefinitionId == "pegasus.conversation:chat" &&
            continued.Submission.ConversationId == conversationId && continued.Submission.UserText == followup,
            "The native continuation must complete through ordinary Chat, preserving exact visible text and conversation ownership.");
        var summary = continued.Result?.GetProperty("summary").GetString() ?? "";
        Check(summary.Contains("Hello.csproj", StringComparison.OrdinalIgnoreCase) &&
            summary.Contains("build", StringComparison.OrdinalIgnoreCase) && summary.Contains("run", StringComparison.OrdinalIgnoreCase) &&
            summary.Contains(marker, StringComparison.Ordinal),
            "The real Chat model must use retained Programming context and the new exact continuation marker.");
        await nativeChat!.RefreshAsync();
        var inspected = await Call("snapshot.inspect", new { includeLastResponse = true });
        Check(inspected.Success && inspected.Data.GetProperty("runId").GetString() == requestId &&
            inspected.Data.GetProperty("lastResponse").GetString()?.Contains(marker, StringComparison.Ordinal) == true,
            "The normal native transcript must display the completed continuation.");
        var unchanged = await Call("workflow.inspect", new { runId });
        Check(unchanged.Success && unchanged.Data.GetProperty("revision").GetInt64() == saved.Revision &&
            JsonElement.DeepEquals(unchanged.Data.GetProperty("result"), savedResult),
            "The separate Programming result must remain unchanged after ordinary Chat.");
        Console.WriteLine(JsonSerializer.Serialize(new { programmingRunId = runId, conversationId,
            continuationRunId = requestId, checks = verified, actualModelContinuation = true,
            windowShown = IsVisible, installedStateChanged = false }));
        return checks;
    }
}
