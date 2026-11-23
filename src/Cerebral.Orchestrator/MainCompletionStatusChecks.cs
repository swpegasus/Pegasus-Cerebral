using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

/// <summary>Pure state fixtures: no window, dispatcher, transport, files, model or provider.</summary>
public static class MainCompletionStatusChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks++;
        }
        static MainCompletionStatus New(object owner)
        {
            var book = new MainCompletionStatus(); book.Bind(owner); return book;
        }
        static MainCompletionStatus.Observation View(string run, long revision, string phase = "Working",
            bool completed = false, bool failed = false, bool uncertain = false, bool unknown = false,
            bool cancelled = false, string? error = null, long? sequence = 1, string conversation = "chat") =>
            new(conversation, run, revision, phase, completed, failed, uncertain, unknown, cancelled, error, sequence);
        static MainCompletionStatus.Operation Begin(MainCompletionStatus book, object owner, string run,
            string kind = "submit", long? revision = 1, string request = "request", string conversation = "chat") =>
            book.Begin(owner, conversation, run, request, kind, revision, 1) ??
            throw new InvalidOperationException("fixture_operation_unavailable");
        static void Failed(MainCompletionStatus book, object owner, string run = "a")
        {
            book.ArmSubmission(owner, "chat", run, 0);
            book.Fail(Begin(book, owner, run), "earlier failure", false);
        }

        Check(MainCompletionStatus.RunPhase(WorkflowStatus.Queued, false) == "Queued for engine",
            "A saved queued run is not called active engine work.");
        Check(MainCompletionStatus.RunPhase(WorkflowStatus.Running, false) == "Engine running · completion pending",
            "Only a claimed running run is labeled engine running.");
        Check(MainCompletionStatus.RunPhase(WorkflowStatus.Waiting, true) == "Waiting for your answer",
            "A partial saved answer remains a human wait.");
        Check(MainCompletionStatus.RunPhase(WorkflowStatus.Completed, false) == "Completed · ready for your next message",
            "Only the saved terminal success is labeled complete.");

        var owner = new object(); var book = New(owner);
        Failed(book, owner);
        var acceptedAnswer = Begin(book, owner, "a", "answer", 2);
        Check(book.Current(owner, "chat").Progress == "Answer received · submitting to engine" &&
            book.Current(owner, "chat").Error is not null,
            "Answer request in flight retains a previous error.");
        Check(book.ReplyReceived(acceptedAnswer) &&
            book.Current(owner, "chat").Progress == "Answer saved · checking workflow" &&
            book.Current(owner, "chat").Error is not null,
            "Durable answer receipt is not presented as engine execution or success.");
        Check(book.Observe(owner, View("a", 2, MainCompletionStatus.RunPhase(WorkflowStatus.Queued, false))).Error is not null,
            "Saved queued work retains the previous error.");
        Check(book.Observe(owner, View("a", 3, MainCompletionStatus.RunPhase(WorkflowStatus.Running, false))).Error is not null,
            "Claimed running work retains the previous error.");
        Check(book.Observe(owner, View("a", 4, MainCompletionStatus.RunPhase(WorkflowStatus.Completed, false), completed: true)).ClearedError,
            "A later saved successful completion clears the previous active error.");
        book = New(owner);
        Failed(book, owner);
        Check(book.Observe(owner, View("a", 2, "Accepted")).Error is not null, "Acceptance does not clear completion error.");
        Check(book.Observe(owner, View("a", 3, "Generating")).Error is not null, "Generation does not clear completion error.");
        Check(book.Observe(owner, View("a", 4, uncertain: true)).Error is not null, "Unknown mutation retains error.");
        Check(book.Observe(owner, View("a", 5, completed: true, unknown: true)).Error is not null, "Unresolved effect cannot clear error.");
        Check(book.Observe(owner, View("a", 6, completed: true, cancelled: true)).Error is not null, "Cancellation cannot count as success.");
        Check(book.Observe(owner, View("a", 7, completed: true, error: "contradiction")).Error is not null, "Contradictory result cannot clear error.");
        var success = book.Observe(owner, View("a", 8, "Completed", completed: true));
        Check(success.Error is null && success.ClearedError, "Later confirmed same-run completion clears the error.");
        Check(!book.Observe(owner, View("a", 8, "Completed", completed: true)).ClearedError, "Repeated completion does not emit a second clearance.");
        _ = book.Observe(owner, View("history", 900, "Recorded"));
        Check(book.Observe(owner, View("a", 1, "Failed", failed: true, error: "stale")).Error is null,
            "A recovery high-water mark survives intervening historical run selection.");

        book = New(owner);
        var answer = Begin(book, owner, "answer-run", "answer", 5, "answer-mutation");
        book.Fail(answer, "answer failed", false);
        Check(book.Observe(owner, View("answer-run", 5, "Completed", completed: true)).Error is not null, "Same revision is not later answer recovery.");
        Check(book.Observe(owner, View("answer-run", 6, "Waiting for your answer")).Error is not null, "Waiting after an answer failure retains notice.");
        Check(book.Observe(owner, View("answer-run", 7, "Completed", completed: true)).ClearedError, "Same-run answer recovery requires no new run ID.");

        book = New(owner);
        var delayed = Begin(book, owner, "a", "answer", 5);
        _ = book.Observe(owner, View("a", 7, "Completed", completed: true));
        book.Fail(delayed, "delayed failure", true, 7);
        Check(!book.Observe(owner, View("a", 7, "Completed", completed: true)).ClearedError,
            "A completion already known at failure publication cannot immediately clear that failure.");
        Check(book.Observe(owner, View("a", 8, "Completed", completed: true)).ClearedError, "Revision newer than the failure publication can clear.");

        book = New(owner);
        book.ArmSubmission(owner, "chat", "a", 0);
        var old = Begin(book, owner, "a");
        book.Fail(old, "error A", false);
        book.ArmSubmission(owner, "chat", "b", 10);
        var newer = Begin(book, owner, "b", revision: null, request: "b");
        var before = book.Current(owner, "chat");
        Check(!book.Fail(old, "late error A", true, 20), "Stale failure explicitly refuses presenter publication.");
        Check(book.Current(owner, "chat") == before, "Old failure cannot erase a newer submission arm or active operation.");
        Check(!book.ReplyReceived(old), "Stale reply explicitly refuses observation-authority adoption.");
        Check(book.Current(owner, "chat") == before, "Old reply cannot replace a newer operation's progress.");
        Check(book.Observe(owner, View("b", 1, "Completed", completed: true, sequence: 10)).Error is not null,
            "A historical RID at or before the fresh snapshot boundary is not a new completion.");
        Check(book.Observe(owner, View("b", 1, "Completed", completed: true, sequence: 11)).ClearedError,
            "Explicit later submission can clear across runs without comparing their revision numbers.");
        book.Fail(newer, "older asynchronous tail", true);
        // This final callback is still the same exact operation, so record its latest observed revision.
        Check(!book.Observe(owner, View("b", 1, "Completed", completed: true, sequence: 11)).ClearedError,
            "A newly published same-operation error still requires later progress.");

        book = New(owner);
        book.ArmSubmission(owner, "chat", "a", 0);
        old = Begin(book, owner, "a");
        book.Fail(old, "A", false);
        book.ArmSubmission(owner, "chat", "b", 10);
        newer = Begin(book, owner, "b", revision: null);
        book.Fail(newer, "B", true);
        book.Fail(old, "late A", true);
        Check(book.Current(owner, "chat").Error == "B", "Late A cannot replace B's newer failure.");
        book.ReplyReceived(old);
        Check(book.Current(owner, "chat").Progress.Contains("unconfirmed", StringComparison.Ordinal), "Late reply does not displace newer unconfirmed state.");

        book = New(owner);
        book.ArmSubmission(owner, "chat", "a", 0);
        var staleRead = book.BeginRead(owner, "chat");
        book.ArmSubmission(owner, "chat", "b", 10);
        _ = Begin(book, owner, "b");
        Check(!book.ReadCurrent(owner, "chat", staleRead), "Read started before newer work cannot displace its observation authority.");
        var currentRead = book.BeginRead(owner, "chat");
        Check(book.ReadCurrent(owner, "chat", currentRead), "A later exact read remains eligible for validated observation.");

        book = New(owner);
        var failedAnswer = Begin(book, owner, "a", "answer", 5);
        book.Fail(failedAnswer, "A still failed", false);
        var replyRead = book.BeginRead(owner, "chat");
        var acceptedAtReply = book.ReadCurrent(owner, "chat", replyRead);
        Check(acceptedAtReply, "Exact read is current when its reply installs observation authority.");
        book.ArmSubmission(owner, "chat", "b", 10);
        // Mirror the presenter's consumption gate: reply-time eligibility cannot authorize a later stale snapshot.
        var delayedSnapshot = acceptedAtReply && book.ReadCurrent(owner, "chat", replyRead)
            ? book.Observe(owner, View("a", 6, "Completed", completed: true))
            : book.Current(owner, "chat");
        Check(!delayedSnapshot.ClearedError && delayedSnapshot.Error == "A still failed" &&
            delayedSnapshot.Progress == "Preparing message",
            "A newly armed submission retires earlier read authority before snapshot consumption.");

        book = New(owner); Failed(book, owner);
        var foreign = new object();
        _ = book.Observe(foreign, View("a", 100, completed: true));
        Check(book.Current(owner, "chat").Error is not null, "Foreign owner cannot clear original owner's error.");
        _ = book.Observe(owner, View("a", 100, completed: true, conversation: "other"));
        Check(book.Current(owner, "chat").Error is not null, "Foreign conversation cannot clear error.");
        _ = book.Observe(owner, View("historical", 1000, completed: true));
        Check(book.Current(owner, "chat").Error is not null, "Unarmed historical completion cannot clear error.");
        Check(book.Observe(owner, View("a", 2, completed: true)).ClearedError, "History revisions are not compared to the active run's revisions.");
        book.Bind(foreign);
        Check(book.Current(foreign, "chat").Error is null, "Owner lifetime reset discards old owner's display state.");
        book.Fail(old, "old owner callback", true);
        Check(book.Current(foreign, "chat").Error is null, "Delayed retired-owner callbacks cannot repopulate state.");

        book = new MainCompletionStatus(); book.Bind(owner, "fixed-worker:one");
        book.ArmSubmission(owner, "chat", "a", 0);
        var retired = Begin(book, owner, "a");
        book.Fail(retired, "earlier failure", false);
        var retiredRead = book.BeginRead(owner, "chat");
        var reconnect = new object(); book.Bind(reconnect, "fixed-worker:one");
        Check(book.Current(reconnect, "chat").Error == "earlier failure" &&
            book.Current(reconnect, "chat").Progress.Contains("Reconnected", StringComparison.Ordinal),
            "An exact same-worker reconnect retains only the previous display error.");
        Check(!book.Fail(retired, "retired failure", true) && book.Current(reconnect, "chat").Error == "earlier failure",
            "A callback from the retired owner cannot change the reconnected error.");
        Check(!book.ReadCurrent(owner, "chat", retiredRead) &&
            !book.Observe(owner, View("a", 20, completed: true)).ClearedError &&
            book.Current(reconnect, "chat").Error == "earlier failure",
            "An old read or completion observation cannot clear a reconnected error.");
        Check(!book.Observe(reconnect, View("a", 1, completed: true)).ClearedError,
            "A preexisting completion is not later recovery after reconnect.");
        Check(book.Observe(reconnect, View("a", 2, completed: true)).ClearedError,
            "A later confirmed completion in the same conversation clears across reconnect.");
        book = new MainCompletionStatus(); book.Bind(owner, "fixed-worker:one"); Failed(book, owner);
        book.Bind(reconnect, "fixed-worker:two");
        Check(book.Current(reconnect, "chat").Error is null, "A different Worker does not inherit the prior display error.");

        book = new MainCompletionStatus(); book.Bind(owner, "fixed-worker:one");
        book.ArmSubmission(owner, "chat", "fresh", 40);
        var reconnectUnknown = Begin(book, owner, "fresh", revision: null, request: "fresh");
        book.Fail(reconnectUnknown, "ACK unknown", true);
        book.Bind(reconnect, "fixed-worker:one");
        Check(book.Observe(reconnect, View("fresh", 1, completed: true, sequence: 41)).Error is not null,
            "An unconfirmed submit cannot clear on a cached completion after reconnect.");
        var reconnectRead = book.BeginRead(reconnect, "chat");
        book.ConfirmRecoveryRead(reconnect, "chat", "fresh", reconnectRead);
        Check(book.Observe(reconnect, View("fresh", 1, completed: true, sequence: 41)).ClearedError,
            "A post-reconnect exact read plus later original message can confirm the same submission.");

        book = New(owner);
        book.ArmSubmission(owner, "chat", "fresh", 40);
        var fresh = Begin(book, owner, "fresh", revision: null, request: "fresh");
        var earlyRead = book.BeginRead(owner, "chat");
        book.Fail(fresh, "ACK unknown", true);
        book.ConfirmRecoveryRead(owner, "chat", "fresh", earlyRead);
        Check(book.Observe(owner, View("fresh", 1, completed: true, sequence: 41)).Error is not null,
            "A get begun before the failure is not later recovery evidence.");
        var read = book.BeginRead(owner, "chat");
        book.ConfirmRecoveryRead(owner, "chat", "wrong-run", read);
        Check(book.Observe(owner, View("fresh", 1, completed: true, sequence: 41)).Error is not null, "Foreign get cannot arm recovery.");
        book.ConfirmRecoveryRead(owner, "chat", "fresh", read);
        Check(book.Observe(owner, View("fresh", 1, completed: true, sequence: null)).Error is not null, "Missing original message anchor remains unavailable.");
        Check(book.Observe(owner, View("fresh", 1, completed: true, sequence: 40)).Error is not null, "Preexisting original message cannot establish fresh request recovery.");
        Check(book.Observe(owner, View("fresh", 1, completed: true, sequence: 41)).ClearedError,
            "Later exact read plus a confirmed new message anchor resolves lost ACK without a replay.");

        book = New(owner);
        _ = Begin(book, owner, "a", "answer", 5);
        Check(book.Observe(owner, View("a", 5, uncertain: true)).Error is not null, "Invalid/unconfirmed client response retains an attributed active operation.");
        Check(book.Observe(owner, View("a", 6, completed: true)).ClearedError, "Read-confirmed newer completion resolves that uncertainty.");

        book = New(owner);
        Failed(book, owner);
        _ = book.Observe(owner, View("a", 2, "Failed", failed: true, error: "run failed"));
        Check(book.Current(owner, "chat").Error == "run failed", "Current workflow failure is retained independently of transport success.");
        _ = book.Observe(owner, View("historical", 100));
        Check(book.Current(owner, "chat").Error == "run failed", "Conversation navigation preserves its prior outcome.");

        book = New(owner);
        book.ArmSubmission(owner, "chat", "a", 0);
        book.Fail(Begin(book, owner, "a"), new string('x', 2000) + "\ncontrol", false);
        Check(book.Current(owner, "chat").Error is { Length: MainCompletionStatus.MaximumErrorCharacters } bounded &&
            !bounded.Any(char.IsControl), "Error text has a finite sanitized display bound.");
        for (var i = 1; i < MainCompletionStatus.MaximumConversations; i++)
            book.ArmSubmission(owner, "chat-" + i, "run-" + i, 0);
        book.ArmSubmission(owner, "overflow", "overflow-run", 0);
        Check(book.Current(owner, "overflow").Progress.Contains("limit reached", StringComparison.Ordinal),
            "Additional conversations fail visibly at the state bound.");
        Check(book.Current(owner, "chat").Error is not null, "State bound never silently evicts an existing error.");
        Check(book.Begin(owner, "chat", "a", "bad\nid", "submit", 1, 1) is null, "Malformed metadata cannot create an operation.");
        return checks;
    }
}
