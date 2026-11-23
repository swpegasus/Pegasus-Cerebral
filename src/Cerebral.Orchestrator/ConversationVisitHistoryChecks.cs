using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

internal static class ConversationVisitHistoryChecks
{
    internal static int Run()
    {
        var checks = 0;
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var history = new ConversationVisitHistory<object>(3);
        history.Record("a", null, "owner", false, false, null);
        history.Record("b", "project", "owner", false, false, null);
        var b = history.Current!;
        history.Record("b", "project", "owner", false, false, null);
        Check(history.Count == 2 && history.Current?.Sequence == b.Sequence, "A repeated visit must not duplicate history."); checks++;
        var a = history.Candidates(back: true).Single();
        Check(history.MoveTo(a.Sequence, history.Revision) && history.CanForward, "Back must expose the previous visit and Forward."); checks++;
        history.Record("c", null, "owner", false, false, null);
        Check(!history.CanForward && history.Candidates(back: true).Single().ConversationId == "a", "A new branch must discard Forward visits."); checks++;
        history.Record("d", null, "owner", false, false, null);
        history.Record("e", null, "owner", false, false, null);
        Check(history.Count == 3 && history.Candidates(back: true).Select(x => x.ConversationId).SequenceEqual(["d", "c"]),
            "The configured capacity must trim the oldest visits."); checks++;
        var unavailable = history.Candidates(back: true).First();
        var oldRevision = history.Revision;
        Check(history.RemoveUnavailable(unavailable.Sequence, oldRevision) && history.Count == 2 &&
            !history.MoveTo(unavailable.Sequence, oldRevision), "A confirmed missing target must be removed without accepting stale revision moves."); checks++;
        var local = new object();
        history.Record("draft", "project", "owner", true, false, local);
        Check(ReferenceEquals(history.Current?.LocalState, local) && history.Current.IsLocalDraft,
            "A local draft visit must retain its in-memory selection state."); checks++;
        var firstRun = new WorkflowConversationRun("first-run", "default-chat", "lead", "First message",
            ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion, 1,
            WorkflowStatus.Completed, null, null, null, null, false, null, null, 0);
        var confirmed = new WorkflowConversationSnapshot("default-chat", firstRun.Id, firstRun,
            Array.Empty<CerebralEvent>(), 0, false, false, false, firstRun.Id);
        Check(OrchestratorWindow.HasConfirmedNativeRunForHistory(confirmed, "default-chat", true) &&
            !OrchestratorWindow.HasConfirmedNativeRunForHistory(confirmed with { MutationUncertain = true }, "default-chat", true) &&
            !OrchestratorWindow.HasConfirmedNativeRunForHistory(confirmed with { RunId = "other-run" }, "default-chat", true) &&
            !OrchestratorWindow.HasConfirmedNativeRunForHistory(confirmed, "other-chat", true) &&
            !OrchestratorWindow.HasConfirmedNativeRunForHistory(confirmed, "default-chat", false),
            "Only the exact confirmed run, conversation, request and current connection may promote a draft history visit."); checks++;
        var defaultHistory = new ConversationVisitHistory<object>();
        defaultHistory.Record("previous-failed-chat", null, "same-owner", false, false, null);
        defaultHistory.Record("default-chat", null, "same-owner", true, false, local);
        var draftSequence = defaultHistory.Current!.Sequence;
        Check(defaultHistory.Current.IsLocalDraft && defaultHistory.Current.LocalState == local,
            "A default-profile New chat starts as a restorable local draft."); checks++;
        defaultHistory.Record("default-chat", null, "same-owner", false, false, null);
        Check(defaultHistory.Current?.Sequence == draftSequence && defaultHistory.Current.IsLocalDraft == false &&
            defaultHistory.Current.LocalState is null,
            "A confirmed first send must promote the same visit to its saved-conversation route."); checks++;
        var back = defaultHistory.Candidates(back: true).Single();
        Check(defaultHistory.MoveTo(back.Sequence, defaultHistory.Revision) &&
            defaultHistory.Current?.ConversationId == "previous-failed-chat",
            "Back must retain the prior conversation after promotion."); checks++;
        var forward = defaultHistory.Candidates(back: false).Single();
        Check(forward.ConversationId == "default-chat" && forward.Sequence == draftSequence &&
            !forward.IsLocalDraft && forward.LocalState is null &&
            defaultHistory.MoveTo(forward.Sequence, defaultHistory.Revision) &&
            defaultHistory.Current?.ConversationId == "default-chat",
            "Forward must target the saved conversation, not restore its old blank draft."); checks++;
        var uncertainHistory = new ConversationVisitHistory<object>();
        uncertainHistory.Record("uncertain-chat", null, "same-owner", true, false, local);
        Check(!OrchestratorWindow.HasConfirmedNativeRunForHistory(
                confirmed with { MutationUncertain = true }, "uncertain-chat", true) &&
            uncertainHistory.Current?.IsLocalDraft == true && uncertainHistory.Current.LocalState == local,
            "An uncertain or nonaccepted first request must keep its local draft visit."); checks++;
        history.Clear();
        Check(history.Count == 0 && !history.CanBack && !history.CanForward, "Connection changes must clear session history."); checks++;
        return checks;
    }
}
