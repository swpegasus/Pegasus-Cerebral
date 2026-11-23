using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

/// <summary>Unexecuted source for real temporary-SQLite guarded rename and candidate paging checks.</summary>
public static class SourceBoundConversationRenameChecks
{
    private static int checks;
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static WorkflowSubmission Input(string conversation, string text = "Source text") =>
        new(Id(), conversation, "lead", "fixture.source:chat", "1.0.0", text, null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
    private static bool Rejected<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Cerebral-SourceBoundRename-" + Id()));
        internal readonly string Database;
        internal readonly EventJournal Journal;
        internal readonly WorkflowStore Store;
        internal Fixture() { Database = Path.Combine(Root, "events.db"); Journal = new(Database); Store = new(Journal); }
        internal WorkflowStore Reopen() => new(new EventJournal(Database));
        internal long Scalar(string sql, params (string Name, object Value)[] arguments)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        internal void Sql(string sql, params (string Name, object Value)[] arguments)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(Root), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("Cerebral-SourceBoundRename-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its root.");
            Directory.Delete(Root, recursive: true);
        }
    }
    private sealed record Case(WorkflowRun Source, WorkflowRun Work, SourceBoundConversationRenameGuard Guard);
    private static Case Create(Fixture f, string? text = null, string? project = null)
    {
        var committed = f.Store.SubmitWithCommittedRootUserInput(Input("source-" + Id(), text ?? "Initial root User input.") with { ProjectId = project });
        var receipt = committed.FirstRootUserInput!;
        var work = f.Store.Submit(Input("work-" + Id(), "Package-owned fixture prompt.") with
            { DefinitionId = "fixture.background:title", Caller = new(EventActorKind.Service, "fixture.package"), ProjectId = project });
        var title = f.Store.GetConversation(receipt.DefinitionId, receipt.ConversationId)!;
        var rename = new ConversationRenameRequest(Id(), receipt.DefinitionId, receipt.ConversationId, title.TitleRevision, "Generated title");
        var guard = new SourceBoundConversationRenameGuard(rename, work.Id, work.Submission.ConversationId,
            receipt.ConversationId, receipt.RequestId, receipt.EventId, receipt.EventSequence, Hash(receipt.UserText),
            receipt.DefinitionId, receipt.DefinitionVersion, receipt.ProjectId, title.Title, title.TitleRevision);
        return new(committed.Run, work, guard);
    }
    private static SourceBoundConversationRenameGuard Fresh(SourceBoundConversationRenameGuard guard) =>
        guard with { Rename = guard.Rename with { RequestId = Id() } };
    public static async Task<int> RunAsync()
    {
        checks = 0;
        AppliedReceiptAndOwnerCompatibility();
        KnownSkipsAndImmutableIdentity();
        FirstSourceAndFullUnicodeHash();
        ReadOnlyReceiptAndStorageFailure();
        ReadyCandidatePartitions();
        await HumanRenameRace();
        return checks;
    }
    private static void AppliedReceiptAndOwnerCompatibility()
    {
        using var f = new Fixture(); var c = Create(f);
        var runBefore = JsonSerializer.Serialize(f.Store.Get(c.Source.Id));
        var eventsBefore = JsonSerializer.Serialize(f.Journal.ReadPage(c.Guard.SourceConversationId));
        Check(f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard) is null, "A missing guarded receipt is unknown, not replayed.");
        var first = f.Store.RenameConversationFromSource(c.Guard);
        Check(first.Disposition == SourceBoundConditionalRenameDisposition.Applied &&
            first.Receipt == new ConversationRenameResult(c.Guard.SourceConversationId, c.Guard.Rename.Title, 1),
            "Exact first-source guard applies the existing title CAS.");
        var retry = f.Reopen().RenameConversationFromSource(c.Guard);
        Check(retry.Disposition == SourceBoundConditionalRenameDisposition.AlreadyApplied && retry.Receipt == first.Receipt,
            "Exact guarded retry returns its original receipt without a second revision.");
        Check(f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard) == retry,
            "Read-only exact receipt reconciliation returns AlreadyApplied.");
        Check(Rejected<WorkflowConflictException>(() => f.Store.RenameConversation(c.Guard.Rename)),
            "Owner API explicitly rejects guarded-origin receipt without deserializing it as an owner request.");
        var human = f.Store.RenameConversation(c.Guard.Rename with { RequestId = Id(), ExpectedTitleRevision = 1, Title = "Human title" });
        Check(human.TitleRevision == 2 && f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard) == retry &&
            f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.Title == "Human title",
            "Known guarded receipt survives later human rename without reverting current title.");
        Check(JsonSerializer.Serialize(f.Store.Get(c.Source.Id)) == runBefore &&
            JsonSerializer.Serialize(f.Journal.ReadPage(c.Guard.SourceConversationId)) == eventsBefore,
            "Guarded and owner title changes alter neither source run nor context history.");

        using var other = new Fixture(); var ownerCase = Create(other);
        var owner = other.Store.RenameConversation(ownerCase.Guard.Rename);
        var conflict = other.Store.RenameConversationFromSource(ownerCase.Guard);
        Check(conflict is { Disposition: SourceBoundConditionalRenameDisposition.Skipped, SkipCode: "rename-request-conflict" } &&
            other.Store.ReadSourceBoundConversationRenameReceipt(ownerCase.Guard) is null &&
            other.Store.RenameConversation(ownerCase.Guard.Rename) == owner,
            "Plain owner receipt stays exact and unchanged when a guarded request reuses its RID.");
    }
    private static void KnownSkipsAndImmutableIdentity()
    {
        using var f = new Fixture(); var c = Create(f, project: "project-one");
        var variants = new[]
        {
            c.Guard with { WorkRequestId = Id() },
            c.Guard with { WorkConversationId = "wrong-work" },
            c.Guard with { SourceRequestId = Id() },
            c.Guard with { SourceEventId = Id() },
            c.Guard with { SourceEventSequence = c.Guard.SourceEventSequence + 1 },
            c.Guard with { SourceInputSha256 = new string('0', 64) },
            c.Guard with { SourceDefinitionId = "other:definition" },
            c.Guard with { SourceDefinitionVersion = "different" },
            c.Guard with { SourceProjectId = "other-project" },
            c.Guard with { CapturedSourceTitle = "wrong captured default" },
            c.Guard with { CapturedTitleRevision = 1, Rename = c.Guard.Rename with { ExpectedTitleRevision = 1 } },
            c.Guard with { Rename = c.Guard.Rename with { ConversationId = "other-source" } }
        };
        foreach (var variant in variants)
        {
            var guard = Fresh(variant);
            var skipped = f.Store.RenameConversationFromSource(guard);
            Check(skipped.Disposition == SourceBoundConditionalRenameDisposition.Skipped && skipped.Receipt is null &&
                skipped.SkipCode is { Length: > 0 and <= 64 }, "Known immutable guard miss produces bounded Skipped.");
            Check(f.Store.RenameConversationFromSource(guard) == skipped &&
                f.Store.ReadSourceBoundConversationRenameReceipt(guard) == skipped, "Exact known skip is retained without reevaluation.");
        }
        Check(f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.TitleRevision == 0,
            "All guard misses leave the title untouched.");
        var sourceChanged = c.Guard with { SourceInputSha256 = new string('1', 64) };
        Check(f.Store.RenameConversationFromSource(sourceChanged).Disposition == SourceBoundConditionalRenameDisposition.Skipped,
            "Initial mismatched request can reserve an immutable Skipped receipt.");
        Check(f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard) is null &&
            f.Store.RenameConversationFromSource(c.Guard).SkipCode == "rename-request-conflict",
            "Full guard, not only Rename fields, participates in exact receipt identity.");
        var moved = Fresh(c.Guard);
        f.Sql("UPDATE workflow_conversations SET project_id=$project WHERE conversation_id=$conversation;",
            ("$project", "project-two"), ("$conversation", c.Guard.SourceConversationId));
        var projectSkip = f.Store.RenameConversationFromSource(moved);
        Check(projectSkip.SkipCode == "source-project-changed", "Current project is rechecked inside the mutation transaction.");
        f.Sql("UPDATE workflow_conversations SET project_id=$project WHERE conversation_id=$conversation;",
            ("$project", "project-one"), ("$conversation", c.Guard.SourceConversationId));
        Check(f.Store.RenameConversationFromSource(moved) == projectSkip, "A known project skip stays known after later project movement.");
        f.Store.RenameConversation(c.Guard.Rename with { RequestId = Id(), Title = "Human first" });
        var humanGuard = Fresh(c.Guard);
        var humanSkip = f.Store.RenameConversationFromSource(humanGuard);
        f.Store.RenameConversation(c.Guard.Rename with { RequestId = Id(), ExpectedTitleRevision = 1, Title = "Human second" });
        Check(humanSkip.SkipCode == "title-changed" && f.Store.ReadSourceBoundConversationRenameReceipt(humanGuard) == humanSkip &&
            f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.Title == "Human second",
            "A known human-title skip is immutable and receipt-only reconciliation preserves later human edits.");
    }
    private static void FirstSourceAndFullUnicodeHash()
    {
        using var f = new Fixture();
        var text = string.Concat(Enumerable.Repeat("界\U0001F9E0e\u0301\r\n", 200));
        var c = Create(f, text);
        var read = f.Store.ReadFirstCommittedRootUserInput(c.Guard.SourceConversationId);
        Check(read is not null && read.RequestId == c.Source.Id && read.EventId == c.Guard.SourceEventId &&
            read.UserText == text && Hash(read.UserText) == c.Guard.SourceInputSha256,
            "Source read preserves exact winning event, combining forms, supplementary Unicode, CRLF and full text.");
        var excerptHash = Fresh(c.Guard) with { SourceInputSha256 = Hash(text[..160]) };
        Check(f.Store.RenameConversationFromSource(excerptHash).SkipCode == "source-mismatch",
            "Hash of a display excerpt cannot substitute for full accepted input hash.");
        f.Store.Cancel(c.Source.Id);
        var later = f.Store.Submit(Input(c.Guard.SourceConversationId, "Later root User.") with { DefinitionId = "other:definition" });
        var laterEvent = f.Journal.ReadPage(c.Guard.SourceConversationId).Events.Single(e => e.TurnId == later.Id && e.Type == CerebralEventTypes.Message);
        var laterGuard = Fresh(c.Guard) with
        {
            SourceRequestId = later.Id, SourceEventId = laterEvent.Id, SourceEventSequence = laterEvent.Sequence,
            SourceDefinitionId = later.Submission.DefinitionId, SourceDefinitionVersion = later.Submission.DefinitionVersion,
            SourceInputSha256 = Hash(later.Submission.UserText),
            Rename = Fresh(c.Guard).Rename with { DefinitionId = later.Submission.DefinitionId }
        };
        Check(f.Store.RenameConversationFromSource(laterGuard).Disposition == SourceBoundConditionalRenameDisposition.Skipped &&
            f.Store.ReadFirstCommittedRootUserInput(c.Guard.SourceConversationId)!.RequestId == c.Source.Id,
            "Conversation-wide firstness prevents later root input across definitions from becoming the source.");

        using var history = new Fixture();
        var conversation = "history";
        var service = history.Store.Submit(Input(conversation, "Earlier service text.") with { Caller = new(EventActorKind.Service, "fixture.service") });
        history.Store.Cancel(service.Id);
        var first = history.Store.SubmitWithCommittedRootUserInput(Input(conversation, "First actual User."));
        Check(history.Store.ReadFirstCommittedRootUserInput(conversation) == first.FirstRootUserInput,
            "Earlier Service history is allowed and retains the actual nonzero pre-message sequence.");
        var childParent = history.Store.Submit(Input("child-parent"));
        var child = history.Store.Submit(Input("child-source") with
            { ParentRunId = childParent.Id, Caller = new(EventActorKind.Service, "cerebral.coordination") });
        var childUser = new CerebralEvent(Id(), "child-source", child.Id, 0, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart("User input on child.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UtcNow);
        history.Journal.Append(childUser, history.Journal.GetRevision("child-source"));
        history.Store.Cancel(child.Id);
        var actualRoot = history.Store.SubmitWithCommittedRootUserInput(Input("child-source"));
        Check(history.Store.ReadFirstCommittedRootUserInput("child-source") == actualRoot.FirstRootUserInput,
            "Only a positively joined same-conversation child is excluded from source firstness.");
        var foreignUser = childUser with { Id = Id(), ConversationId = "foreign-child-history", Sequence = 0 };
        history.Journal.Append(foreignUser, 0);
        history.Store.Submit(Input("foreign-child-history"));
        Check(history.Store.ReadFirstCommittedRootUserInput("foreign-child-history") is null,
            "A foreign child turn binding does not hide earlier root User history.");
        var largeText = new string('界', 128 * 1024);
        var large = history.Store.SubmitWithCommittedRootUserInput(Input("large-source", largeText));
        Check(history.Store.ReadFirstCommittedRootUserInput("large-source")!.UserText == largeText &&
            Encoding.UTF8.GetByteCount(largeText) > 128 * 1024, "Source read adds no UTF8 cap or Unicode truncation.");
        var unknown = new CerebralEvent(Id(), "unbound", "unbound-turn", 0, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart("Earlier unbound User.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UtcNow);
        history.Journal.Append(unknown, 0);
        history.Store.Submit(Input("unbound"));
        Check(history.Store.ReadFirstCommittedRootUserInput("unbound") is null,
            "Unverifiable earlier User history cannot be skipped to a later convenient source.");
    }
    private static void ReadOnlyReceiptAndStorageFailure()
    {
        using var f = new Fixture(); var c = Create(f);
        var rid = c.Guard.Rename.RequestId;
        f.Sql($"""
            CREATE TRIGGER guarded_receipt_abort BEFORE INSERT ON workflow_conversation_renames
            WHEN NEW.request_id='{rid}'
            BEGIN
                SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM workflow_conversation_titles
                    WHERE conversation_id='{c.Guard.SourceConversationId}' AND revision=1)
                    THEN RAISE(ABORT,'fixture expected title update before receipt') END;
                SELECT RAISE(ABORT,'fixture receipt failure');
            END;
            """);
        SqliteException? receiptFailure = null;
        try { f.Store.RenameConversationFromSource(c.Guard); }
        catch (SqliteException error) { receiptFailure = error; }
        Check(receiptFailure is not null && receiptFailure.Message.Contains("fixture receipt failure", StringComparison.Ordinal) &&
            !receiptFailure.Message.Contains("expected title update", StringComparison.Ordinal) &&
            f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.TitleRevision == 0 &&
            f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard) is null,
            "Uncertain receipt failure propagates and rolls back title plus receipt atomically.");
        f.Sql("DROP TRIGGER guarded_receipt_abort;");
        var applied = f.Store.RenameConversationFromSource(c.Guard);
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString()))
        {
            writer.Open(); using var reserved = writer.BeginTransaction(deferred: false);
            Check(f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard)?.Receipt == applied.Receipt,
                "Receipt reconciliation remains a read while another connection owns the writer reservation.");
        }
        var countBefore = f.Scalar("SELECT COUNT(*) FROM workflow_conversation_renames;");
        var mismatch = c.Guard with { WorkConversationId = "different-immutable-work" };
        Check(f.Store.ReadSourceBoundConversationRenameReceipt(mismatch) is null &&
            f.Scalar("SELECT COUNT(*) FROM workflow_conversation_renames;") == countBefore,
            "Mismatched receipt lookup neither mutates nor replays.");
        f.Sql("UPDATE workflow_conversation_renames SET response=$response WHERE request_id=$id;",
            ("$response", "{\"disposition\":999}"), ("$id", rid));
        Check(Rejected<InvalidDataException>(() => f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard)) &&
            Rejected<InvalidDataException>(() => f.Store.RenameConversationFromSource(c.Guard)),
            "Corrupt stored result propagates as uncertainty, never Skipped or Applied.");
    }
    private static void ReadyCandidatePartitions()
    {
        using var f = new Fixture();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 105; i++)
        {
            var marker = (i % 3) switch
            {
                0 => JsonSerializer.SerializeToElement(new { sourceBoundBackground = (object?)null }),
                1 => JsonSerializer.SerializeToElement(new { sourceBoundBackground = "malformed-present" }),
                _ => JsonSerializer.SerializeToElement(new { sourceBoundBackground = new { schemaVersion = 1 } })
            };
            var run = f.Store.Submit(Input("background-" + i) with { SettingsSnapshot = marker });
            expected.Add(run.Id);
        }
        var foreground = f.Store.Submit(Input("foreground-after-105") with
            { Input = JsonSerializer.SerializeToElement(new { sourceBoundBackground = new { forged = true } }) });
        Check(f.Store.ListReady().Count == 100 && !f.Store.ListReady().Any(r => r.Id == foreground.Id),
            "Legacy ready behavior stays unchanged and demonstrates the old first-page concealment.");
        var foregroundPage = f.Store.ListReadyPage(false);
        Check(foregroundPage.Runs.Count == 1 && foregroundPage.Runs[0].Id == foreground.Id,
            "Marker-absent foreground candidates are selected independently of 105 background rows.");
        var seen = new HashSet<string>(StringComparer.Ordinal); long cursor = 0; var pages = 0;
        while (true)
        {
            var page = f.Store.ListReadyPage(true, 40, cursor);
            foreach (var run in page.Runs) Check(seen.Add(run.Id) && expected.Contains(run.Id), "Candidate cursor never repeats or crosses partitions.");
            pages++;
            if (page.NextAfterRow is not { } after) break;
            Check(after > cursor, "Candidate cursor advances.");
            cursor = after;
            Check(pages <= 3, "Candidate fixture pagination is bounded.");
        }
        Check(seen.SetEquals(expected) && !seen.Contains(foreground.Id),
            "Object/null/malformed-present markers all stay candidate rows; lookalike public Input is not authority.");
        Check(Rejected<ArgumentOutOfRangeException>(() => f.Store.ListReadyPage(false, 201)) &&
            Rejected<ArgumentOutOfRangeException>(() => f.Store.ListReadyPage(true, afterRow: -1)), "Existing ready-page bounds are preserved.");
    }
    private static async Task HumanRenameRace()
    {
        using var f = new Fixture(); var c = Create(f);
        using var barrier = new Barrier(3);
        var background = Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return f.Reopen().RenameConversationFromSource(c.Guard);
        });
        var human = Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            try { return f.Reopen().RenameConversation(c.Guard.Rename with { RequestId = Id(), Title = "Human race title" }); }
            catch (WorkflowConflictException) { return null; }
        });
        var synchronized = barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        await Task.WhenAll(background, human);
        Check(synchronized, "Rename race starts at one barrier.");
        if (human.Result is not null)
            Check(background.Result.Disposition == SourceBoundConditionalRenameDisposition.Skipped &&
                f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.Title == "Human race title",
                "A human rename that commits first wins the existing CAS.");
        else
        {
            Check(background.Result.Disposition == SourceBoundConditionalRenameDisposition.Applied, "Exactly one revision-zero contender applies.");
            var current = f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!;
            f.Store.RenameConversation(c.Guard.Rename with { RequestId = Id(), ExpectedTitleRevision = current.TitleRevision, Title = "Human refreshed title" });
            Check(f.Store.ReadSourceBoundConversationRenameReceipt(c.Guard)?.Disposition == SourceBoundConditionalRenameDisposition.AlreadyApplied &&
                f.Store.GetConversation(c.Guard.SourceDefinitionId, c.Guard.SourceConversationId)!.Title == "Human refreshed title",
                "Known background receipt never reverts a later refreshed human rename.");
        }
    }
}
