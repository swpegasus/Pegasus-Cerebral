using System.IO;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Pure owner-view contract and selection checks. No service, model, window or filesystem work.
    internal static int CheckEmbeddedActivityScopeContracts()
    {
        var count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { count++; return; }
            throw new InvalidOperationException(message);
        }
        var main = new SharedActivityMember("root-a", "root-a", "main", "Main");
        var worker = new SharedActivityMember("opaque-worker-1", "source-worker-1", "worker", "Same readable name");
        var other = new SharedActivityMember("opaque-worker-2", "source-worker-2", "worker", "Same readable name");
        var roster = new SharedActivityMembersReply(1, "root-a", 3, false, [main, worker, other]);
        ValidateSharedActivityMembers(roster, "root-a"); count++;
        Reject(() => ValidateSharedActivityMembers(roster, "root-b"), "A foreign root must reject.");
        Reject(() => ValidateSharedActivityMembers(roster with { Members = [main, worker, worker] }, "root-a"), "Duplicate stable members must reject.");
        Reject(() => ValidateSharedActivityMembers(roster with { Members = [worker] }, "root-a"), "A missing exact Main member must reject.");
        var next = new SharedActivityCursor("root-a", worker.ContextId, 3, 100, 200);
        var emptyFiltered = new SharedActivityPage(1, "root-a", worker.ContextId, 3, [], 200, null, next, 100);
        ValidateSharedActivityPage(emptyFiltered, roster, worker, null); count++;
        Check(emptyFiltered.Events.Count == 0 && emptyFiltered.NextCursor is not null,
            "An empty filtered raw page can still have continuation; it does not imply complete history.");
        foreach (var invalid in new[]
        {
            emptyFiltered with { MainConversationId = "root-b" }, emptyFiltered with { ContextId = other.ContextId },
            emptyFiltered with { RootRevision = 4 }, emptyFiltered with { ScannedEvents = 101 },
            emptyFiltered with { NextCursor = next with { ContextId = other.ContextId } },
            emptyFiltered with { NextCursor = next with { RootRevision = 2 } },
            emptyFiltered with { NextCursor = next with { AfterSequence = 0 } }
        }) Reject(() => ValidateSharedActivityPage(invalid, roster, worker, null), "A mismatched root/member/revision/bound must reject.");
        Reject(() => ValidateSharedActivityPage(emptyFiltered with { SnapshotSequence = 201 }, roster, worker, next),
            "A continued read cannot drift to a different journal snapshot.");
        var tail = new SharedActivityCursor("root-a", worker.ContextId, 3, 200, null);
        var encodedTail = CerebralJson.Element(tail);
        Check(encodedTail.TryGetProperty("snapshotSequence", out var snapshotValue) && snapshotValue.ValueKind == System.Text.Json.JsonValueKind.Null,
            "The returned tail bookmark preserves its explicit null through the normal command serializer.");
        var exhausted = emptyFiltered with { NextCursor = null, TailCursor = tail };
        ValidateSharedActivityPage(exhausted, roster, worker, next); count++;
        var refreshed = exhausted with { SnapshotSequence = 250, TailCursor = tail with { AfterSequence = 250 } };
        ValidateSharedActivityPage(refreshed, roster, worker, tail); count++;
        Check(tail.AfterSequence == 200 && tail.SnapshotSequence is null && refreshed.SnapshotSequence == 250,
            "Only the exact returned tail bookmark starts a new bounded snapshot after the completed high-water mark.");
        Reject(() => ValidateSharedActivityPage(exhausted with { TailCursor = tail with { ContextId = other.ContextId } }, roster, worker, next),
            "A foreign-member tail bookmark cannot be adopted.");
        Reject(() => ValidateSharedActivityPage(exhausted with { TailCursor = tail with { AfterSequence = 199 } }, roster, worker, next),
            "A tail bookmark must equal the actual completed journal high-water mark.");
        Reject(() => ValidateSharedActivityPage(exhausted with { NextCursor = next }, roster, worker, null),
            "A page cannot provide a pinned continuation and an exhausted-snapshot tail together.");

        var state = new ConversationPanelStackState(); state.Bind("owner", "root-a");
        Check(!state.AcceptHandoff("owner", "root-a", "main-run", "task", null, "coordinator-run", true, false) && state.MainExpanded,
            "Discovery or Send alone is not accepted coordinator work.");
        Check(state.AcceptHandoff("owner", "root-a", "main-run", "task", "main-run", "coordinator-run", true, true) &&
            state.IsExpanded("coordinator-run") && !state.MainExpanded,
            "Actual accepted work opens its coordinator even when the Main editor retains a draft or residual focus.");
        state.SetMainExpanded(true); var manualVersion = state.SelectionVersion;
        Check(!state.AcceptHandoff("owner", "root-a", "main-run", "task", "main-run", "coordinator-run", true, false) &&
            state.MainExpanded && state.SelectionVersion == manualVersion,
            "An ordinary accepted-graph refresh preserves explicit manual Main steering.");
        state.SetExpanded("activity", true);
        Check(state.ReturnToMain("task", "main-run", "question:1") && state.MainExpanded && !state.IsExpanded("activity"),
            "A new question returns from Activity to Main once.");
        state.SetExpanded("worker", true);
        Check(!state.ReturnToMain("task", "main-run", "question:1") && state.IsExpanded("worker"),
            "Repeating the same attention cannot undo a later manual worker selection.");
        Check(state.ReturnToMain("task", "main-run", "result:2") && state.MainExpanded,
            "A distinct new result returns Main for user communication.");
        Check(!state.AcceptHandoff("foreign-owner", "root-a", "main-run", "task", "main-run", "coordinator-run", true, false),
            "A graph from another owner cannot drive selection.");
        state.Bind("owner", "empty-root-b");
        Check(state.MainExpanded && !state.IsFollowingSourceRun("main-run") && !state.IsExpanded("worker"),
            "A new empty chat starts at Main without adopting old selection or handoff identity.");
        return count;
    }
}
