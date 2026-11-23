using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Actual isolated Worker and generated module, controlled through the real desktop pipe. No inference.</summary>
    public async Task<int> CheckWorkflowControlServiceAsync(LocalWorkerEndpoint endpoint, string dataRoot, string runId)
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var controlRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-work-control-service-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        DesktopControlServer? server = null;
        using var client = new CerebralClient(endpoint);
        try
        {
            var host = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
            Check(Path.GetFullPath(host.DataDirectory).Equals(Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase),
                "The acceptance must connect to its exact isolated Worker data root.");
            _ = await InspectServiceUiAsync(endpoint, dataRoot, null).WaitAsync(deadline.Token);
            Check(ServiceConnected && workflowWorkspace is not null, "The actual desktop must connect to the isolated Worker.");
            prompt.Text = "Keep this unrelated Chat draft";
            var chatId = CurrentConversationId;
            var before = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
            Check(before.Submission.DefinitionId == "acceptance.cerebral.generated-review:review" &&
                before.Submission.DefinitionVersion == "1.0.0" && before.Questions?.Fields.Count == 2,
                "The saved run must belong to the actual retained generated module and its two-field question.");
            server = await DesktopControlServer.StartAsync(this, controlRoot, deadline.Token);
            Task<DesktopControlResponse> Call(string operation, object arguments, string? id = null) =>
                DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation, id ?? Guid.NewGuid().ToString("N"),
                    JsonSerializer.Serialize(arguments, CerebralJson.Options), deadline.Token);
            var opened = await Call("workflow.open", new { runId });
            Check(opened.Success && opened.Data.GetProperty("questionCount").GetInt32() == 2 &&
                Tabs.SelectedItem is TabItem { Tag: "work" }, "Semantic open must display the generated Work form.");
            Check(opened.Data.GetProperty("questions").GetProperty("fields").EnumerateArray()
                .Select(x => x.GetProperty("id").GetString()).Order().SequenceEqual(new[] { "label", "style" }),
                "The semantic form must carry the module's actual label and style fields.");
            Check(!opened.Data.GetRawText().Contains("INTERNAL-GENERATED-ASSIGNMENT", StringComparison.Ordinal),
                "Private workflow assignment must not leak through desktop snapshots.");
            var questionId = opened.Data.GetProperty("questionId").GetString()!;
            var questionRevision = opened.Data.GetProperty("questionRevision").GetInt64();
            var answers = new { label = "Generated passage", style = "detailed" };
            var stale = await Call("workflow.answer", new { runId, questionId, questionRevision = questionRevision + 1, answers });
            Check(stale.State == "rejected", "A stale question revision must be rejected on the real service route.");
            var incomplete = await Call("workflow.answer", new { runId, questionId, questionRevision, answers = new { label = "Incomplete" } });
            Check(incomplete.State == "rejected", "An incomplete two-field answer must be rejected.");
            var unchanged = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
            Check(unchanged.Revision == before.Revision && unchanged.Questions?.Answers.Count == 0,
                "Rejected semantic answers must not mutate the durable run.");
            var answerId = Guid.NewGuid().ToString("N");
            var arguments = new { runId, questionId, questionRevision, answers };
            var accepted = await Call("workflow.answer", arguments, answerId);
            Check(accepted.State == "accepted", "The generated module's actual answer request must be accepted.");
            DesktopControlResponse inspected;
            do
            {
                inspected = await Call("workflow.inspect", new { runId });
                if (!inspected.Success) throw new IOException(inspected.Error ?? "Semantic workflow inspection failed.");
                if (inspected.Data.GetProperty("status").GetString() == "completed") break;
                if (inspected.Data.GetProperty("status").GetString() is "failed" or "cancelled")
                    throw new IOException("The generated workflow did not complete: " + inspected.Data.GetRawText());
                await Task.Delay(100, deadline.Token);
            } while (true);
            var result = inspected.Data.GetProperty("result");
            Check(result.GetProperty("wordCount").GetInt32() == 4 && result.GetProperty("label").GetString() == "Generated passage" &&
                result.GetProperty("style").GetString() == "detailed", "The generated command must execute and return its actual four-word result.");
            Check(inspected.Data.GetProperty("questionCount").GetInt32() == 0 && workflowWorkspace!.Snapshot!.Run!.Questions is null,
                "Completed Work must collapse its answered form.");
            var completed = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
            var repeated = await Call("workflow.answer", arguments, answerId);
            var afterRetry = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
            Check(repeated.State == "accepted" && JsonElement.DeepEquals(repeated.Data, accepted.Data) && afterRetry.Revision == completed.Revision,
                "Exact semantic retry returns the original receipt without another durable mutation.");
            Check((await Call("workflow.answer", new { runId, questionId, questionRevision, answers = new { label = "Changed", style = "detailed" } }, answerId)).State == "conflict",
                "A changed answer cannot reuse the accepted request ID.");
            Check(prompt.Text == "Keep this unrelated Chat draft" && CurrentConversationId == chatId,
                "Generated Work must not replace the user's Chat draft or conversation.");
            Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("wordCount").GetInt32() == 4,
                "A separate Worker query must confirm the durable result.");
            await server.DisposeAsync(); server = null;
            Check(!File.Exists(Path.Combine(controlRoot, "Desktop", "control.json")), "The owned desktop-control endpoint must be removed.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, generatedWorkflowDesktopControlChecks = checks,
                evidence = "actual generated service DLL, isolated Worker pipe, desktop-control pipe and native Work adapter",
                runId, questionId, questionRevision, answerRequestId = answerId, wordCount = 4,
                windowShown = IsVisible, modelExecuted = false, installedStateChanged = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel();
            if (server is not null) await server.DisposeAsync();
            if (Directory.Exists(controlRoot)) Directory.Delete(controlRoot, true);
        }
    }
}
