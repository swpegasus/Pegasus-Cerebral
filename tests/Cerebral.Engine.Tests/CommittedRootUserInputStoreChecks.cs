using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

/// <summary>Focused real local SQLite transaction checks for committed root User input.</summary>
public static class CommittedRootUserInputStoreChecks
{
    private static int checks;
    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        checks++;
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static WorkflowSubmission Input(string conversation, string text = "accepted", string? id = null) =>
        new(id ?? Id(), conversation, "lead", "fixture.committed-input:chat", "1.0.0", text, null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
    private static bool Rejected<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string Database;
        internal readonly EventJournal Journal;
        internal readonly WorkflowStore Store;
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Cerebral-CommittedInput-" + Id()));
            Database = Path.Combine(Root, "events.db");
            Journal = new(Database);
            Store = new(Journal);
        }
        internal WorkflowStore Reopen() => new(new EventJournal(Database));
        public void Dispose()
        {
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(Root), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("Cerebral-CommittedInput-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
            // EventJournal owns no persistent connection and disables pooling.
            Directory.Delete(Root, recursive: true);
        }
    }
    private static CerebralEvent Message(Fixture f, string conversation, string turn,
        EventActorKind actor, EventSourceKind source, string text)
    {
        var item = new CerebralEvent(Id(), conversation, turn, 0, CerebralEventTypes.Message,
            new(actor), [new TextContentPart(text)],
            new(ModelInclusion.Required, HumanVisibility.Transcript),
            new(source), DateTimeOffset.UtcNow);
        return f.Journal.Append(item, f.Journal.GetRevision(conversation)).Event;
    }
    private static CerebralEvent ExactMessage(Fixture f, WorkflowSubmissionCommitResult result) =>
        f.Journal.ReadPage(result.Run.Submission.ConversationId, limit: 200).Events.Single(e =>
            e.Type == CerebralEventTypes.Message && e.TurnId == result.Run.Id);
    private static void ExactReceipt(Fixture f, WorkflowSubmissionCommitResult result, long before)
    {
        var r = result.FirstRootUserInput ?? throw new InvalidOperationException("Missing first root User receipt.");
        var e = ExactMessage(f, result);
        var s = result.Run.Submission;
        Check(r.HasCoherentRootUserEventBinding, "Receipt binding must be coherent.");
        Check(r.RequestId == result.Run.Id && r.ConversationId == s.ConversationId && r.ProfileId == s.ProfileId &&
            r.DefinitionId == s.DefinitionId && r.DefinitionVersion == s.DefinitionVersion && r.ProjectId == s.ProjectId &&
            r.ParentRunId == s.ParentRunId, "Receipt must use committed accepted run coordinates.");
        Check(r.ConversationSequenceBeforeMessage == before && s.ConversationSequence == before &&
            r.EventSequence == before + 1, "Receipt must use the actual pre-message journal sequence.");
        Check(r.EventId == e.Id && r.EventTurnId == e.TurnId && r.EventSequence == e.Sequence &&
            r.EventType == e.Type && r.ActorKind == e.Actor.Kind && r.SourceKind == e.Provenance.Source,
            "Receipt must identify the exact appended CerebralEvent.");
        Check(r.UserText == s.UserText && ((TextContentPart)e.Content[0]).Text == s.UserText,
            "Receipt UserText must equal accepted persisted text and the message's first text part.");
        Check(f.Store.Get(result.Run.Id).Submission.UserText == r.UserText,
            "A returned receipt must be readable from a separately opened post-commit store connection.");
    }
    private sealed record Attempt(WorkflowSubmissionCommitResult? Result, bool Conflict);
    private static async Task<Attempt[]> Race(WorkflowStore first, WorkflowSubmission left,
        WorkflowStore second, WorkflowSubmission right)
    {
        using var barrier = new Barrier(3);
        Task<Attempt> Start(WorkflowStore store, WorkflowSubmission submission) => Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Race fixture did not start.");
            try { return new Attempt(store.SubmitWithCommittedRootUserInput(submission), false); }
            catch (WorkflowConflictException) { return new Attempt(null, true); }
        });
        var a = Start(first, left);
        var b = Start(second, right);
        var synchronized = barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        // Always settle both tasks before releasing their barrier or deleting their database.
        var results = await Task.WhenAll(a, b);
        Check(synchronized, "Concurrent submissions must meet the common start barrier.");
        return results;
    }
    public static async Task<int> RunAsync()
    {
        checks = 0;
        await ConcurrentSameRequest();
        await ConcurrentDifferentRequests();
        FirstUserAfterNonUserHistory();
        DuplicateAndLaterUserOmitted();
        RejectionAndRollbackOmitted();
        ChildAndNonUserOmitted();
        LegacyApiCompatibility();
        ExactUnicodeAndCharacterLimit();
        MissingOrForeignRunBindingIsNotFirst();
        return checks;
    }
    private static async Task ConcurrentSameRequest()
    {
        using var f = new Fixture();
        var input = Input("same-request");
        var results = await Race(f.Store, input, f.Reopen(), input);
        Check(results.All(x => !x.Conflict && x.Result is not null), "Equivalent concurrent RID retries must both return the committed run.");
        Check(results.Count(x => x.Result!.FirstRootUserInput is not null) == 1,
            "Only the winning equivalent RID transaction may emit a receipt.");
        Check(f.Journal.ReadPage(input.ConversationId).Events.Count(e => e.Type == CerebralEventTypes.Message) == 1,
            "Equivalent concurrent retries must append exactly one message.");
        ExactReceipt(f, results.Single(x => x.Result!.FirstRootUserInput is not null).Result!, 0);
    }
    private static async Task ConcurrentDifferentRequests()
    {
        using var f = new Fixture();
        var a = Input("competing-requests");
        var b = Input(a.ConversationId);
        var results = await Race(f.Store, a, f.Reopen(), b);
        Check(results.Count(x => x.Conflict) == 1 && results.Count(x => x.Result is not null) == 1,
            "Two new RIDs in one conversation must have one commit and one active-run rejection.");
        var winner = results.Single(x => x.Result is not null).Result!;
        ExactReceipt(f, winner, 0);
        Check(f.Journal.ReadPage(a.ConversationId).Events.Count(e => e.Type == CerebralEventTypes.Message) == 1,
            "Losing concurrent RID must leave no message or observation.");
    }
    private static void FirstUserAfterNonUserHistory()
    {
        using var f = new Fixture();
        const string conversation = "non-user-history";
        var service = f.Store.Submit(Input(conversation) with
            { Caller = new(EventActorKind.Service, "fixture.service"), ProjectId = "project" });
        f.Store.Cancel(service.Id);
        Message(f, conversation, "system-history", EventActorKind.System, EventSourceKind.System, "System history.");
        var before = f.Journal.GetRevision(conversation);
        var first = f.Store.SubmitWithCommittedRootUserInput(Input(conversation, "First human input.") with { ProjectId = "project" });
        Check(before > 0 && first.FirstRootUserInput is not null,
            "Prior Service/system history must not disqualify the first root User message.");
        ExactReceipt(f, first, before);
    }
    private static void DuplicateAndLaterUserOmitted()
    {
        using var f = new Fixture();
        var input = Input("duplicates");
        var first = f.Store.SubmitWithCommittedRootUserInput(input);
        var revision = f.Journal.GetRevision(input.ConversationId);
        var duplicate = f.Reopen().SubmitWithCommittedRootUserInput(input);
        Check(duplicate.FirstRootUserInput is null && duplicate.Run.Id == first.Run.Id &&
            f.Journal.GetRevision(input.ConversationId) == revision, "An equivalent RID must not re-observe or append.");
        f.Store.Cancel(first.Run.Id);
        var before = f.Journal.GetRevision(input.ConversationId);
        var later = f.Store.SubmitWithCommittedRootUserInput(Input(input.ConversationId, "Later input.") with
            { DefinitionId = "fixture.other:chat" });
        Check(later.FirstRootUserInput is null, "Later root User must be omitted even across definition changes.");
        var e = ExactMessage(f, later);
        var merelyCoherent = new CommittedRootUserInputReceipt(later.Run.Id, input.ConversationId, input.ProfileId,
            later.Run.Submission.DefinitionId, input.DefinitionVersion, null, before, e.Id, e.TurnId, e.Sequence,
            e.Type, e.Actor.Kind, e.Provenance.Source, null, later.Run.Submission.UserText);
        Check(merelyCoherent.HasCoherentRootUserEventBinding,
            "A later adjacent User message may be coherent; coherence alone must not establish firstness.");
    }
    private static void RejectionAndRollbackOmitted()
    {
        using var f = new Fixture();
        var input = Input("rejections");
        var first = f.Store.SubmitWithCommittedRootUserInput(input);
        var before = f.Journal.GetRevision(input.ConversationId);
        Check(Rejected<WorkflowConflictException>(() => f.Store.SubmitWithCommittedRootUserInput(input with { UserText = "Changed RID body." })),
            "A mismatched RID must throw.");
        Check(Rejected<WorkflowConflictException>(() => f.Store.SubmitWithCommittedRootUserInput(Input(input.ConversationId))),
            "Overlapping new work must throw.");
        Check(f.Journal.GetRevision(input.ConversationId) == before, "Rejections must append no new input.");
        var oversized = Input("pre-transaction-size-rejection", new string('\u754C', 128 * 1024)) with
            { AssignmentText = new string('\u754C', 128 * 1024) };
        WorkflowSubmissionCommitResult? escaped = null;
        Check(Rejected<InvalidDataException>(() => escaped = f.Store.SubmitWithCommittedRootUserInput(oversized)),
            "Oversized combined submission must reject during the immutable-input copy.");
        Check(escaped is null && f.Store.TryGet(oversized.RequestId) is null &&
            f.Journal.GetRevision(oversized.ConversationId) == 0,
            "Pre-transaction rejection must expose no result, run or event.");

        var rollback = Input("late-rollback", "Committed only if the whole transaction succeeds.") with
            { AssignmentText = "Assignment must roll back with its User message." };
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = f.Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // The trigger proves both appends occurred before deliberately failing the run insert.
        // It exists only in this fixture's private database; production admission is unchanged.
        command.CommandText = $$"""
            CREATE TRIGGER fixture_reject_committed_input BEFORE INSERT ON workflow_runs
            WHEN NEW.conversation_id='late-rollback' AND NEW.id='{{rollback.RequestId}}'
            BEGIN
                SELECT CASE WHEN
                    (SELECT COUNT(*)=2 AND COUNT(DISTINCT json_extract(payload,'$.type'))=2
                        FROM conversation_events
                        WHERE conversation_id=NEW.conversation_id
                          AND json_extract(payload,'$.turnId')=NEW.id
                          AND json_extract(payload,'$.type') IN ('message','workflow.assignment'))
                    THEN RAISE(ABORT,'fixture_after_message_and_assignment')
                    ELSE RAISE(ABORT,'fixture_missing_input_events') END;
            END;
            """;
        command.ExecuteNonQuery();
        var rejectedAfterAppends = false;
        try
        {
            escaped = f.Store.SubmitWithCommittedRootUserInput(rollback);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19 &&
            error.Message.Contains("fixture_after_message_and_assignment", StringComparison.Ordinal))
        {
            rejectedAfterAppends = true;
        }
        finally
        {
            command.CommandText = "DROP TRIGGER fixture_reject_committed_input;";
            command.ExecuteNonQuery();
        }
        Check(rejectedAfterAppends, "Run insert must fail only after the exact message and assignment append.");
        Check(escaped is null && f.Store.TryGet(rollback.RequestId) is null &&
            f.Journal.GetRevision(rollback.ConversationId) == 0,
            "A rolled-back write must expose no result, run, event, or consumed first-user state.");
        command.CommandText = """
            SELECT COUNT(*) FROM conversation_events WHERE conversation_id=$conversation
                AND json_extract(payload,'$.turnId')=$run;
            """;
        command.Parameters.AddWithValue("$conversation", rollback.ConversationId);
        command.Parameters.AddWithValue("$run", rollback.RequestId);
        Check((long)command.ExecuteScalar()! == 0, "Rollback must leave no exact request's event rows.");
        var recovery = f.Store.SubmitWithCommittedRootUserInput(rollback);
        ExactReceipt(f, recovery, 0);
    }
    private static void ChildAndNonUserOmitted()
    {
        using var f = new Fixture();
        foreach (var actor in new[] { EventActorKind.Agent, EventActorKind.Service })
        {
            var input = Input("non-user-" + actor) with { Caller = new(actor, "fixture.caller") };
            var result = f.Store.SubmitWithCommittedRootUserInput(input);
            Check(result.FirstRootUserInput is null && ExactMessage(f, result).Actor.Kind == actor,
                "Admitted Agent/Service input must never be observed as User input.");
        }
        var parent = f.Store.Submit(Input("parent"));
        var child = Input("child") with
        {
            ParentRunId = parent.Id,
            Caller = new(EventActorKind.Service, "cerebral.coordination")
        };
        var committed = f.Store.SubmitWithCommittedRootUserInput(child);
        Check(committed.FirstRootUserInput is null && committed.Run.Submission.ParentRunId == parent.Id,
            "A committed child must have no root User observation.");
        Message(f, child.ConversationId, committed.Run.Id, EventActorKind.User, EventSourceKind.User, "Owner input on a known child.");
        f.Store.Cancel(committed.Run.Id);
        var beforeRoot = f.Journal.GetRevision(child.ConversationId);
        var rootAfterChild = f.Store.SubmitWithCommittedRootUserInput(Input(child.ConversationId, "First actual root input."));
        ExactReceipt(f, rootAfterChild, beforeRoot);
        Check(rootAfterChild.FirstRootUserInput is not null,
            "A positively bound same-conversation child User event must not consume root firstness.");
        var forged = child with { RequestId = Id(), ConversationId = "forged-child", Caller = new(EventActorKind.User) };
        Check(Rejected<WorkflowConflictException>(() => f.Store.SubmitWithCommittedRootUserInput(forged)) &&
            f.Store.TryGet(forged.RequestId) is null && f.Journal.GetRevision(forged.ConversationId) == 0,
            "A User caller cannot bypass child admission to obtain an observation.");
        var system = Input("invalid-system") with { Caller = new(EventActorKind.System) };
        Check(Rejected<ArgumentException>(() => f.Store.SubmitWithCommittedRootUserInput(system)) &&
            f.Journal.GetRevision(system.ConversationId) == 0, "Invalid System workflow caller must reject without receipt.");
    }
    private static void LegacyApiCompatibility()
    {
        using var f = new Fixture();
        var input = Input("legacy");
        WorkflowRun old = f.Store.Submit(input);
        WorkflowRun retried = f.Store.Submit(input, null, "unchanged-settings");
        Check(old.Id == retried.Id && f.Store.SubmitWithCommittedRootUserInput(input).FirstRootUserInput is null,
            "Both old overloads retain WorkflowRun and exact duplicate semantics.");
        f.Store.Cancel(old.Id);
        Check(f.Store.SubmitWithCommittedRootUserInput(Input(input.ConversationId), null, "unchanged-settings").FirstRootUserInput is null,
            "Old API root input must suppress a later first-input observation.");
        Check(typeof(WorkflowStore).GetMethod(nameof(WorkflowStore.Submit), [typeof(WorkflowSubmission)])!.ReturnType == typeof(WorkflowRun) &&
            typeof(WorkflowStore).GetMethod(nameof(WorkflowStore.Submit),
                [typeof(WorkflowSubmission), typeof(ConversationIdleCheckpoint), typeof(string)])!.ReturnType == typeof(WorkflowRun),
            "Both old public Submit signatures must keep their return type.");
    }
    private static void ExactUnicodeAndCharacterLimit()
    {
        using var f = new Fixture();
        var texts = new[]
        {
            "Keep e\u0301 distinct from \u00E9; \U0001F9E0; \u754C; CRLF\r\nand LF\n.",
            new string('\u754C', 128 * 1024),
            string.Concat(Enumerable.Repeat("\U0001F9E0", 64 * 1024))
        };
        for (var i = 0; i < texts.Length; i++)
        {
            var input = Input("unicode-" + i, texts[i]);
            var result = f.Store.SubmitWithCommittedRootUserInput(input);
            ExactReceipt(f, result, 0);
            Check(result.FirstRootUserInput!.UserText == texts[i], "Accepted Unicode must remain exact, without normalization or truncation.");
            if (i != 0)
                Check(texts[i].Length == 128 * 1024 && Encoding.UTF8.GetByteCount(texts[i]) > 128 * 1024,
                    "Existing bound is UTF-16 characters, not a new UTF-8 byte cap.");
        }
        WorkflowRun oldUnicode = f.Store.Submit(Input("old-unicode-limit", texts[1]));
        Check(oldUnicode.Submission.UserText == texts[1],
            "Old API and observation API must both accept exact Unicode at the same character boundary.");
        var tooLong = Input("too-long", new string('x', 128 * 1024 + 1));
        Check(Rejected<ArgumentException>(() => f.Store.SubmitWithCommittedRootUserInput(tooLong)) &&
            f.Journal.GetRevision(tooLong.ConversationId) == 0, "New API must preserve the exact character-limit refusal.");
        Check(Rejected<ArgumentException>(() => f.Store.Submit(tooLong with { RequestId = Id(), ConversationId = "old-too-long" })),
            "Old API must retain the same character-limit refusal.");
    }
    private static void MissingOrForeignRunBindingIsNotFirst()
    {
        using var f = new Fixture();
        const string conversation = "unbound-user";
        Message(f, conversation, "unbound-turn", EventActorKind.User, EventSourceKind.User, "Earlier durable User input.");
        var before = f.Journal.GetRevision(conversation);
        var result = f.Store.SubmitWithCommittedRootUserInput(Input(conversation));
        Check(result.FirstRootUserInput is null && result.Run.Submission.ConversationSequence == before,
            "An earlier unbound User Message conservatively prevents first-input credit.");
        var foreignParent = f.Store.Submit(Input("foreign-run-home"));
        var foreign = f.Store.Submit(Input("foreign-run-child-home") with
            { ParentRunId = foreignParent.Id, Caller = new(EventActorKind.Service, "cerebral.coordination") });
        const string foreignConversation = "foreign-run-history";
        Message(f, foreignConversation, foreign.Id, EventActorKind.User, EventSourceKind.User, "User event with foreign run turnId.");
        var foreignBefore = f.Journal.GetRevision(foreignConversation);
        var foreignResult = f.Store.SubmitWithCommittedRootUserInput(Input(foreignConversation));
        Check(foreignResult.FirstRootUserInput is null && foreignResult.Run.Submission.ConversationSequence == foreignBefore,
            "A run from another conversation must not suppress prior-root detection through a false child join.");
    }
}
