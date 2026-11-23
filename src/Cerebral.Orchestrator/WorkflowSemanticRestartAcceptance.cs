using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>
    /// Real Worker plus three fresh shown source-desktop generations. Proves that a retained form
    /// survives close/reopen and that an answer whose acknowledgement was lost can be reconciled
    /// through the Worker's durable receipt rather than a desktop-process cache.
    /// </summary>
    public static int RunWorkflowSemanticRestartAcceptance(Application app, LocalWorkerEndpoint endpoint,
        string dataRoot, string runId)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            var exit = 0;
            try { await VerifyWorkflowSemanticRestartAsync(endpoint, dataRoot, runId); }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally { app.Shutdown(exit); }
        };
        return app.Run();
    }

    private static async Task VerifyWorkflowSemanticRestartAsync(LocalWorkerEndpoint endpoint, string dataRoot, string runId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-workflow-semantic-restart-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        using var owner = new CerebralClient(endpoint);
        try
        {
            var host = await owner.GetAsync<HostInfo>("health", cancellationToken: token);
            Check(Path.GetFullPath(host.DataDirectory).Equals(Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase),
                "The semantic restart acceptance connected to the wrong Worker data root.");
            var original = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: token);
            Check(original.Submission.DefinitionId == "example.review:review" && original.Status == WorkflowStatus.Waiting &&
                  original.Questions?.Fields.Select(x => x.Id).Order().SequenceEqual(["label", "style"]) == true,
                "The acceptance requires the removable example.review two-field form.");

            async Task<(OrchestratorWindow Window, DesktopControlServer Server, DesktopControlResponse Opened)> OpenPhase(string name)
            {
                var phaseRoot = Path.Combine(root, name);
                var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(phaseRoot, "plugins")),
                    Path.Combine(phaseRoot, "plugin-data"));
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
                Check(new WindowInteropHelper(window).Handle != IntPtr.Zero && window.IsVisible,
                    $"Desktop phase '{name}' did not surface a real visible WPF window.");
                _ = await window.InspectServiceUiAsync(endpoint, dataRoot, null).WaitAsync(token);
                Check(window.ServiceConnected && window.workflowWorkspace is not null,
                    $"Desktop phase '{name}' did not connect its real Work surface to the isolated Worker.");
                var server = await DesktopControlServer.StartAsync(window, Path.Combine(root, "control"), token);
                var opened = await Call(server, "workflow.open", new { runId }, token: token);
                Check(opened.Success && window.Tabs.SelectedItem is TabItem { Tag: "work" },
                    $"Desktop phase '{name}' did not semantically open Work.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
                return (window, server, opened);
            }

            async Task ClosePhase((OrchestratorWindow Window, DesktopControlServer Server, DesktopControlResponse Opened) phase)
            {
                await phase.Server.DisposeAsync();
                phase.Window.Close();
                await appDispatcher().InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
                Check(!File.Exists(Path.Combine(root, "control", "Desktop", "control.json")),
                    "A closed desktop generation retained its semantic-control descriptor.");
            }

            Dispatcher appDispatcher() => Application.Current.Dispatcher;

            var phaseA = await OpenPhase("phase-a");
            var first = phaseA.Opened.Data;
            var questionId = first.GetProperty("questionId").GetString()!;
            var questionRevision = first.GetProperty("questionRevision").GetInt64();
            Check(first.GetProperty("questionCount").GetInt32() == 2 &&
                  Find<TextBox>(phaseA.Window.workflowWorkspace!, "WorkflowAnswer-label") is { IsVisible: true } &&
                  Find<ComboBox>(phaseA.Window.workflowWorkspace!, "WorkflowAnswer-style") is { IsVisible: true },
                "The first shown desktop did not render the real label and style controls.");
            Check(!VisibleText(phaseA.Window).Contains("INTERNAL-ASSIGNMENT-NOT-FOR-TRANSCRIPT", StringComparison.Ordinal),
                "Private workflow assignment leaked into the human desktop.");
            await ClosePhase(phaseA);

            var retained = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: token);
            Check(retained.Status == WorkflowStatus.Waiting && retained.Revision == original.Revision &&
                  retained.Questions?.Id == questionId && retained.Questions.Revision == questionRevision,
                "Closing the desktop changed the durable unanswered form.");

            var phaseB = await OpenPhase("phase-b");
            Check(phaseB.Opened.Data.GetProperty("questionId").GetString() == questionId &&
                  phaseB.Opened.Data.GetProperty("questionRevision").GetInt64() == questionRevision &&
                  Find<TextBox>(phaseB.Window.workflowWorkspace!, "WorkflowAnswer-label") is { IsVisible: true } &&
                  Find<ComboBox>(phaseB.Window.workflowWorkspace!, "WorkflowAnswer-style") is { IsVisible: true },
                "A fresh shown desktop did not recover the same durable form.");
            var answerId = Guid.NewGuid().ToString("N");
            var answerArguments = JsonSerializer.Serialize(new
            {
                runId, questionId, questionRevision,
                answers = new { label = "Semantic passage", style = "detailed" }
            }, CerebralJson.Options);
            await SendWithoutReadingAsync(phaseB.Server.DescriptorPath, "workflow.answer", answerId, answerArguments, token);
            WorkflowRun completed = retained;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                completed = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: token);
                if (completed.IsTerminal) break;
                await Task.Delay(50, token);
            }
            Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("label").GetString() == "Semantic passage" &&
                  completed.Result?.GetProperty("style").GetString() == "detailed" && completed.Result?.GetProperty("wordCount").GetInt32() == 3,
                "The disconnected semantic answer did not complete the durable review workflow.");
            await ClosePhase(phaseB);

            var phaseC = await OpenPhase("phase-c");
            Check(phaseC.Opened.Data.GetProperty("status").GetString() == "completed" &&
                  phaseC.Opened.Data.GetProperty("questionCount").GetInt32() == 0,
                "The third desktop did not reopen the current completed projection.");
            var retry = await DesktopControlClient.ExecuteAsync(phaseC.Server.DescriptorPath, "workflow.answer", answerId,
                answerArguments, token);
            Check(retry.Success && retry.State == "accepted", "A fresh desktop could not reconcile the exact durable answer receipt.");
            var afterRetry = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: token);
            Check(afterRetry.Revision == completed.Revision && afterRetry.Status == WorkflowStatus.Completed,
                "The exact answer retry created another durable mutation or rolled the run backwards.");
            var changed = await DesktopControlClient.ExecuteAsync(phaseC.Server.DescriptorPath, "workflow.answer", answerId,
                JsonSerializer.Serialize(new
                {
                    runId, questionId, questionRevision,
                    answers = new { label = "Changed", style = "detailed" }
                }, CerebralJson.Options), token);
            Check(changed.State == "conflict", "Changed input reused the accepted semantic answer request ID.");
            await phaseC.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            var rendered = VisibleText(phaseC.Window.workflowWorkspace!);
            Check(Find<TextBox>(phaseC.Window.workflowWorkspace!, "WorkflowAnswer-label") is null &&
                  Find<ComboBox>(phaseC.Window.workflowWorkspace!, "WorkflowAnswer-style") is null,
                "The completed workflow reopened editable answer controls.");
            Check(rendered.Contains("label: Semantic passage", StringComparison.OrdinalIgnoreCase) &&
                  rendered.Contains("style: detailed", StringComparison.OrdinalIgnoreCase) &&
                  rendered.Contains("3 whitespace-delimited words", StringComparison.OrdinalIgnoreCase),
                "The fresh desktop did not render the committed answers and review result.");
            Check(!rendered.Contains("INTERNAL-ASSIGNMENT-NOT-FOR-TRANSCRIPT", StringComparison.Ordinal),
                "Private workflow assignment leaked after answer reconciliation.");
            await ClosePhase(phaseC);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                workflowSemanticRestartChecks = checks,
                evidence = "real Worker pipe, three shown source-desktop generations, same-user semantic pipe, lost acknowledgement and durable receipt retry",
                runId, questionId, questionRevision, answerRequestId = answerId,
                windowShown = true, modelExecuted = false, installedStateChanged = false
            }));
        }
        finally
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(root);
                var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(full).StartsWith("cerebral-workflow-semantic-restart-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid semantic-restart cleanup path.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
            catch (IOException) { }
        }
    }

    private static Task<DesktopControlResponse> Call(DesktopControlServer server, string operation, object arguments,
        string? requestId = null, CancellationToken token = default) => DesktopControlClient.ExecuteAsync(
        server.DescriptorPath, operation, requestId ?? Guid.NewGuid().ToString("N"),
        JsonSerializer.Serialize(arguments, CerebralJson.Options), token);

    private static async Task SendWithoutReadingAsync(string descriptorPath, string operation, string requestId,
        string argumentsJson, CancellationToken token)
    {
        var endpoint = JsonSerializer.Deserialize<DesktopControlEndpoint>(await File.ReadAllTextAsync(descriptorPath, token), CerebralJson.Options)
            ?? throw new InvalidDataException("The desktop-control descriptor is empty.");
        DesktopControlProtocol.Validate(endpoint);
        using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
        var request = new DesktopControlRequest(endpoint.InstanceId, requestId, operation, document.RootElement.Clone());
        DesktopControlProtocol.Validate(request, endpoint.InstanceId);
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(token);
        await LocalWorkerProtocol.WriteAsync(pipe, request, token);
        await pipe.FlushAsync(token);
        // Dispose before reading the response. The desktop may have committed, so the owner must
        // inspect durable Worker state and keep this exact request ID for reconciliation.
    }

    private static T? Find<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        if (root is T candidate && AutomationProperties.GetAutomationId(candidate) == automationId) return candidate;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index), automationId) is { } match) return match;
        return null;
    }

    private static string VisibleText(DependencyObject root)
    {
        var values = new List<string>();
        void Read(DependencyObject item)
        {
            if (item is FrameworkElement { Visibility: not Visibility.Visible }) return;
            switch (item)
            {
                case TextBlock block when !string.IsNullOrWhiteSpace(block.Text): values.Add(block.Text); break;
                case TextBox box when !string.IsNullOrWhiteSpace(box.Text): values.Add(box.Text); break;
                case RichTextBox rich:
                    var text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
                    if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
                    break;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) Read(VisualTreeHelper.GetChild(item, index));
        }
        Read(root);
        return string.Join("\n", values);
    }
}
