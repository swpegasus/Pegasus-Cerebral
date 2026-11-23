using System.IO;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Engine.Workflows;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record MainExactInputReview(JsonElement? Input, bool Cancelled);

    private async Task<MainExactInputReview?> ReviewNormalExactFollowupAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (!IsNativeChat || retainedNativeSubmission is not null) return null;
        var snapshot = nativeChat?.Snapshot ?? throw new InvalidOperationException("The current conversation is unavailable.");
        if (snapshot.Run is not { } accepted || accepted.DefinitionId == ConversationControlClient.ChatDefinitionId)
            return null;
        if (!accepted.IsTerminal || snapshot.RunId != accepted.Id || NativeMutationPending || NativeMutationUncertain)
            throw new InvalidOperationException("Finish or reconcile the accepted workflow before sending another request.");
        var visibleText = prompt.Text;
        if (string.IsNullOrWhiteSpace(visibleText) && attachmentDrafts.Count == 0)
            throw new ArgumentException("Enter a message or attach a file.");
        var selectionStart = prompt.SelectionStart;
        var selectionLength = prompt.SelectionLength;
        var responseShare = selectedResponseSharePercent;
        var projectId = ConversationProjectId;
        var client = SemanticAttachClient();
        await SelectMainExactWorkflowAsync(accepted.DefinitionId, accepted.DefinitionVersion, token);
        var intent = CaptureMainChatWorkflowIntent() ?? throw new InvalidOperationException("The exact workflow choice is unavailable.");
        RequireMainChatWorkflowIntentCurrent(intent);
        if (intent.Origin != "accepted_run" || intent.ExactAvailability is not { } exact)
            throw new InvalidOperationException("The accepted workflow has no current exact declaration.");
        var saved = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = accepted.Id }, cancellationToken: token);
        RequireMainChatWorkflowIntentCurrent(intent);
        var acceptedInput = AcceptedExactFollowupInput(saved, intent.ConversationId, accepted.Id,
            intent.ProfileId, intent.DefinitionId, intent.DefinitionVersion, exact.Definition.InputSchema);
        bool Current()
        {
            try
            {
                RequireMainChatWorkflowIntentCurrent(intent);
                return prompt.Text == visibleText && selectedResponseSharePercent == responseShare &&
                    ConversationProjectId == projectId && !NativeMutationPending && !NativeMutationUncertain;
            }
            catch (InvalidOperationException) { return false; }
        }
        if (!Current()) throw new InvalidOperationException("The request or workflow owner changed before input review.");
        JsonElement? reviewed;
        try
        {
            reviewed = await WorkflowInputReview.ShowAsync(this,
                (request, ct) => client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, ct),
                () => profiles, Current,
                new(intent.ProfileId, exact.Definition, acceptedInput, visibleText,
                    AllowEmptyUserText: attachmentDrafts.Count > 0), token);
        }
        finally
        {
            if (prompt.Text == visibleText)
                prompt.Select(Math.Min(selectionStart, prompt.Text.Length),
                    Math.Min(selectionLength, prompt.Text.Length - Math.Min(selectionStart, prompt.Text.Length)));
        }
        if (!Current()) throw new InvalidOperationException("The request or workflow owner changed during input review.");
        if (reviewed is not { } input) return new(null, true);
        var frozen = MainWorkflowDefinitionInput.FreezeExact(input);
        WorkflowSchema.Validate(frozen, exact.Definition.InputSchema);
        return new(frozen, false);
    }

    internal static JsonElement AcceptedExactFollowupInput(WorkflowRun saved, string conversationId,
        string runId, string profileId, string definitionId, string definitionVersion, JsonElement currentSchema)
    {
        if (saved is null || saved.Id != runId || saved.Submission is not { } submission ||
            submission.RequestId != runId || submission.ConversationId != conversationId ||
            submission.ProfileId != profileId || submission.DefinitionId != definitionId ||
            submission.DefinitionVersion != definitionVersion ||
            saved.Status is not (WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled))
            throw new InvalidDataException("The accepted workflow input no longer matches this conversation and owner.");
        var input = MainWorkflowDefinitionInput.FreezeExact(submission.Input);
        WorkflowSchema.Validate(input, currentSchema);
        return input;
    }
}
