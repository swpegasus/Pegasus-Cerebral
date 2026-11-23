using System.IO;
using System.Net.Http;
using Cerebral.Client;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private async Task<DesktopOperationResult> SubmitExplicitDesktopWorkflow(DesktopWorkflowSubmission input, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        if (input.Binding != PluginBinding())
            return new(DesktopOperationState.Rejected, "The connection or conversation changed. Reopen the plugin action.", input.RequestId);
        if (ControlUncertain || NativeMutationPending)
            return new(DesktopOperationState.Rejected, "Confirm the current conversation action before submitting plugin work.", input.RequestId);
        var workspace = workflowWorkspace;
        if (workspace is null || input.Target is not { } target)
            return new(DesktopOperationState.Rejected, "The workflow details view is unavailable.", input.RequestId);
        if (workspace.MutationPending)
            return new(DesktopOperationState.Rejected, "Wait for the pending workflow request's receipt.", input.RequestId);
        if (workspace.Snapshot?.MutationUncertain == true && workspace.Snapshot.RunId != input.RequestId)
            return new(DesktopOperationState.Rejected, "Open Workflow details from the application menu and refresh or retry its unconfirmed request first.", input.RequestId);

        // No await precedes profile/binding capture and dispatch. BoundCommand selects the current client
        // atomically on this dispatcher, and its connection epoch rejects any later cross-service retry.
        var profileId = workspace.ProfileForRequest(input.RequestId) ?? ConversationProfile().Id;
        var request = new WorkflowConversationSubmission(input.RequestId, profileId, target.DefinitionId,
            target.DefinitionVersion, input.VisibleText, target.Input.Clone(), input.Context, target.StepLimit);
        SelectPage("work");
        var beforeSubmission = workspace.Snapshot;
        WorkflowConversationSnapshot accepted;
        string? refreshWarning = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
            accepted = await workspace.SubmitNewWorkflowAsync(request, linked.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or
            ArgumentException or OperationCanceledException or TimeoutException)
        {
            if (!ReferenceEquals(beforeSubmission, workspace.Snapshot) && ConfirmedNativeSubmission(workspace.Snapshot, input.RequestId) && workspace.Snapshot is { } receipt)
            {
                accepted = receipt;
                refreshWarning = "The workflow was accepted; refresh Workflow details to update its display. " + error.Message;
            }
            else if (error is WorkflowConversationCommandException || workspace.Snapshot?.MutationUncertain != true ||
                ReferenceEquals(beforeSubmission, workspace.Snapshot) && error is (ArgumentException or InvalidOperationException))
                return new(DesktopOperationState.Rejected, error.Message, input.RequestId);
            else
                return new(DesktopOperationState.Uncertain,
                    "The workflow receipt is unconfirmed. Open Workflow details from the application menu and use Retry request with its retained ID. " + error.Message,
                    input.RequestId, input.RequestId);
        }
        // Work owns its form, activity and retry controls. The Chat draft is unrelated to this submission.
        return new(DesktopOperationState.Accepted, refreshWarning ?? "The workflow was accepted in Work.",
            input.RequestId, accepted.RunId);
    }
}
