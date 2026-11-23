using Cerebral.Orchestrator;

var checks = 0;
void Check(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
    checks++;
}
var state = new ConversationPanelStackState();
state.Bind("owner-a", "main-chat");
Check(!state.AcceptHandoff("owner-b", "main-chat", "main-run", "task", "main-run", "lead", true, false),
    "Another owner cannot change the displayed chat.");
Check(!state.AcceptHandoff("owner-a", "other-chat", "main-run", "task", "main-run", "lead", true, false),
    "Another chat cannot collapse Main.");
Check(!state.AcceptHandoff("owner-a", "main-chat", "new-run", "task", "old-run", "lead", true, false),
    "A historical task cannot steal the current turn.");
Check(!state.AcceptHandoff("owner-a", "main-chat", "main-run", "task", null, "lead", true, false) &&
      !state.AcceptHandoff("owner-a", "main-chat", "main-run", "task", "main-run", "lead", false, false),
    "Unbound or incomplete handoffs do not collapse Main.");
Check(state.MainExpanded && state.AcceptHandoff("owner-a", "main-chat", "main-run", "task", "main-run", "lead", true, false) &&
      !state.MainExpanded && state.IsExpanded("lead"), "A current accepted handoff follows the coordinator.");
state.SetExpanded("worker-one", true);
Check(!state.MainExpanded && state.IsExpanded("worker-one") && !state.IsExpanded("lead"),
    "Opening a worker closes Main and the coordinator.");
state.SetExpanded("worker-two", true);
Check(!state.MainExpanded && state.IsExpanded("worker-two") && !state.IsExpanded("worker-one"),
    "Opening another source closes the previous worker.");
state.SetExpanded("worker-one", false);
Check(state.IsExpanded("worker-two"), "Collapsing an unselected source preserves the selected source.");
state.SetMainExpanded(true);
Check(state.MainExpanded && !state.IsExpanded("worker-one") && !state.IsExpanded("worker-two") && !state.IsExpanded("lead"),
    "Opening Main closes every team source.");
Check(!state.AcceptHandoff("owner-a", "main-chat", "main-run", "task", "main-run", "lead", true, false) && state.MainExpanded,
    "A background refresh cannot undo a manual Main expansion.");
Check(!state.ReturnToMain("different-task", "main-run", "question-1") &&
      !state.ReturnToMain("task", "different-run", "question-1"), "Questions from another handoff cannot retarget Main.");
state.SetExpanded("worker-two", true);
Check(state.ReturnToMain("task", "main-run", "question-1") && state.MainExpanded && !state.IsExpanded("lead") && !state.IsExpanded("worker-two"),
    "An exact new clarification opens only Main, including when a worker was selected.");
state.SetExpanded("worker-one", true);
Check(!state.ReturnToMain("task", "main-run", "question-1") && !state.MainExpanded && state.IsExpanded("worker-one"),
    "Repeated polling of a question preserves a later manual source choice.");
Check(state.ReturnToMain("task", "main-run", "question-2") && state.MainExpanded && !state.IsExpanded("worker-one"),
    "A genuinely new question opens only Main again.");
state.SetExpanded("worker-two", true);
Check(state.AcceptHandoff("owner-a", "main-chat", "second-run", "second-task", "second-run", "second-lead", true, true) &&
      state.MainExpanded && !state.IsExpanded("second-lead") && !state.IsExpanded("worker-two"),
    "Preserving a nonempty draft keeps only Main open during handoff.");
state.SetExpanded("worker-two", true);
Check(!state.AcceptHandoff("owner-a", "main-chat", "second-run", "second-task", "second-run", "second-lead", true, true) && state.IsExpanded("worker-two"),
    "Repeated accepted graphs cannot undo a manual source choice, even with preserve-Main requested.");
Check(state.ReturnCurrentMain("second-run", "owner-question") && state.MainExpanded && !state.IsExpanded("worker-two"),
    "A current Main question returns only Main even while the team graph is temporarily unavailable.");
Check(state.AcceptHandoff("owner-a", "main-chat", "third-run", "third-task", "third-run", "third-lead", true, false) &&
      !state.MainExpanded && state.IsExpanded("third-lead") && !state.IsExpanded("second-lead"),
    "A new accepted handoff opens only its coordinator.");
Check(state.ReturnCurrentMain("third-run", "completed") && state.MainExpanded && !state.IsExpanded("third-lead"),
    "A real current completion returns only Main.");
state.SetExpanded("worker-two", true);
state.Bind("owner-a", "main-chat");
Check(state.IsExpanded("worker-two"), "An unchanged owner binding preserves local panel choices.");
state.Bind("owner-a", "another-chat");
Check(state.MainExpanded && !state.IsExpanded("worker-two") && !state.ReturnToMain("second-task", "second-run", "done"),
    "A chat switch clears old handoff and expansion ownership.");
state.SetMainExpanded(false);
Check(!state.MainExpanded && !state.IsExpanded("worker-two"), "Manual collapse may leave every panel closed.");
Console.WriteLine($"{checks} workspace panel state unit checks passed; no shown UI, Worker, model or provider.");
