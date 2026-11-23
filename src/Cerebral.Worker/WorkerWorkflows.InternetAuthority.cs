using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    internal (ManagedInternetAuthority Authority, long RootRevision) InternetAuthority(
        WorkflowSubmission submission, string runId, string effectId, string toolName, string fingerprint, bool requireCurrentEffect = true)
    {
        var run = Store.Get(runId);
        if (run.IsTerminal || run.CancelRequested || run.Submission.RequestId != submission.RequestId ||
            run.Submission.ConversationId != submission.ConversationId || run.Submission.ProfileId != submission.ProfileId ||
            run.Submission.DefinitionId != submission.DefinitionId || run.Submission.DefinitionVersion != submission.DefinitionVersion ||
            requireCurrentEffect && run.Effect?.Id != effectId)
            throw new InternetAccessException("internet-source-stale", "The exact accepted Internet source effect is no longer active.");
        var member = OwnedContexts.Owner(submission.ConversationId);
        string mainId; string? contextId = null;
        if (member is not null)
        {
            var source = OwnedSourceBinding(submission) ?? throw new InternetAccessException("internet-owner-missing", "The owned Main source is unavailable.");
            var sourceRun = Store.Get(source.SourceRunId);
            if (!ConversationRolePolicy.IsMain(sourceRun.Submission) || sourceRun.IsTerminal || sourceRun.CancelRequested ||
                submission.ParentRunId is { } parentId && (Store.Get(parentId).IsTerminal || Store.Get(parentId).CancelRequested))
                throw new InternetAccessException("internet-source-stopped", "The owning Main/coordinator source no longer admits network work.");
            mainId = member.MainConversationId; contextId = member.ContextId;
        }
        else if (ConversationRolePolicy.IsMain(submission)) mainId = submission.ConversationId;
        else throw new InternetAccessException("internet-main-required", "Managed Internet requires an authenticated native Main or its owned context.");
        var root = Store.GetConversation(ConversationWorkflowModule.DefinitionId, mainId)
            ?? throw new InternetAccessException("internet-main-missing", "The owning Main is unavailable.");
        var original = Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, mainId)
            ?? throw new InternetAccessException("internet-main-missing", "The owning Main has no accepted origin.");
        if (!ConversationRolePolicy.IsMain(original) || root.ProfileId != original.ProfileId || root.ProjectId != original.ProjectId)
            throw new InternetAccessException("internet-main-invalid", "The Main identity no longer matches its accepted origin.");
        var binding = OwnedContexts.Read(mainId, root.ProfileId, root.ProjectId);
        if (binding.AdmissionClosed) throw new InternetAccessException("internet-main-closed", "The owning Main no longer admits work.");
        return (new(1, configuration.Get().InstallationId, mainId, contextId, submission.ConversationId,
            runId, effectId, toolName, fingerprint), binding.Revision);
    }
    internal WorkflowRun InternetQuestionOwner(WorkflowSubmission submission, string runId) =>
        OwnedSourceBinding(submission) is { } source ? Store.Get(source.SourceRunId) : Store.Get(runId);

    internal MainContextSet InternetRoot(string mainId)
    {
        Configuration.ValidateId(mainId);
        var root = Store.GetConversation(ConversationWorkflowModule.DefinitionId, mainId)
            ?? throw new KeyNotFoundException("The Main chat was not found.");
        var original = Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, mainId)
            ?? throw new UnauthorizedAccessException("The Main origin is unavailable.");
        if (!ConversationRolePolicy.IsMain(original) || original.ProfileId != root.ProfileId || original.ProjectId != root.ProjectId)
            throw new UnauthorizedAccessException("Internet policy requires the exact accepted Main identity.");
        return OwnedContexts.Read(mainId, root.ProfileId, root.ProjectId);
    }
}
