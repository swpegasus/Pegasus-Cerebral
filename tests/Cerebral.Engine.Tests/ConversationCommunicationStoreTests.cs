using System.Text.Json;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

/// <summary>Isolated temporary SQLite checks. Never opens installed or existing product state.</summary>
public static class ConversationCommunicationStoreTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-communication-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            checks++;
        }
        static bool Rejected<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }
        static void Register(string path, params string[] ids)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO workflow_conversations(conversation_id,profile_id) VALUES($id,'fixture-profile');";
                command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        static long Version(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;"; return Convert.ToInt64(command.ExecuteScalar());
        }
        try
        {
            var path = Path.Combine(root, "communication.db");
            var journal = new EventJournal(path);
            var store = new ConversationCommunicationStore(journal);
            Register(path, "chat-a", "chat-b", "chat-c", "race-a", "race-b", "request-race-a", "request-race-b");
            var absent = store.Get("chat-b", "chat-a");
            Check(!absent.Allowed && absent.Revision == 0 && absent.PendingRequest is null &&
                absent.Pair == new ConversationCommunicationPair("chat-a", "chat-b") && store.List().Permissions.Count == 0,
                "unknown pair denies symmetrically without creating permission rows");
            Check(Version(path) == 6 &&
                Rejected<InvalidOperationException>(() => store.Request(new("missing-target", "chat-a", "missing-chat", 0))) &&
                Rejected<InvalidOperationException>(() => store.SetAllowed(new(new("chat-a", "missing-chat"), 0, true))) &&
                Version(path) == 6 && store.List().Permissions.Count == 0,
                "missing conversation metadata rejects requests and allow without advancing the feature format");

            var request = new ConversationCommunicationRequest("request-1", "chat-a", "chat-b", 0);
            var pending = store.Request(request);
            Check(!pending.Allowed && pending.Revision == 1 && pending.PendingRequest?.RequestId == request.RequestId &&
                store.Get("chat-b", "chat-a") == pending, "request creates one symmetric pending row and cannot self-grant");
            Check(Version(path) == 9 && EventJournal.SchemaVersion == 9 && Version(path) > 8,
                "first policy mutation marks version 9 beyond the preceding host's supported ceiling");
            using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workflow_prepared_requests';";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 1,
                    "version 9 initializes the preceding prepared-intervention table for safe feature ordering");
            }
            Check(store.Request(request) == pending, "lost request response retries without advancing revision");
            Check(Rejected<InvalidOperationException>(() => store.Request(request with { SourceConversationId = "chat-b", TargetConversationId = "chat-a" })) &&
                Rejected<InvalidOperationException>(() => store.Request(request with { ExpectedRevision = 1 })) &&
                Rejected<InvalidOperationException>(() => store.Request(request with { TargetConversationId = "chat-c" })),
                "request ID cannot be reused for changed source, pair or revision");
            Check(Rejected<InvalidOperationException>(() => store.Request(new("second-pending", "chat-b", "chat-a", 1))) &&
                Rejected<ConversationCommunicationRevisionException>(() => store.Request(new("stale-pending", "chat-a", "chat-b", 0))) &&
                store.Get("chat-a", "chat-b") == pending, "competing or stale requests preserve the current pending request");

            var allow = new ConversationCommunicationSetAllowedRequest(pending.Pair, pending.Revision, true, request.RequestId);
            Check(Rejected<InvalidOperationException>(() => store.SetAllowed(allow with { PendingRequestId = "wrong-request" })) &&
                store.Get("chat-a", "chat-b") == pending, "a decision for the wrong pending identity commits nothing");
            var allowed = store.SetAllowed(allow);
            Check(allowed.Allowed && allowed.Revision == 2 && allowed.PendingRequest is null &&
                new ConversationCommunicationStore(new EventJournal(path)).Get("chat-b", "chat-a") == allowed,
                "owner allow clears pending state and persists across independent reopen");
            var whileAllowed = new ConversationCommunicationRequest("already-allowed-request", "chat-b", "chat-a", allowed.Revision);
            Check(store.Request(whileAllowed) == allowed, "requesting an allowed pair creates no pending request or revision");

            var revoked = store.SetAllowed(new(new("chat-b", "chat-a"), allowed.Revision, false));
            Check(!revoked.Allowed && revoked.Revision == 3 && revoked.PendingRequest is null &&
                store.Request(request) == revoked && store.Request(whileAllowed) == revoked,
                "old requests return current denial after revoke and never reopen or regrant");
            Check(Rejected<ConversationCommunicationRevisionException>(() => store.SetAllowed(allow)) &&
                Rejected<InvalidOperationException>(() => store.Request(request with { ExpectedRevision = revoked.Revision })) &&
                !store.Get("chat-a", "chat-b").Allowed, "old allow retries and recycled request identities cannot undo revocation");

            var deniedRequest = new ConversationCommunicationRequest("request-2", "chat-b", "chat-a", revoked.Revision);
            var secondPending = store.Request(deniedRequest);
            var denied = store.SetAllowed(new(secondPending.Pair, secondPending.Revision, false, deniedRequest.RequestId));
            Check(!denied.Allowed && denied.PendingRequest is null && denied.Revision == 5 && store.Request(deniedRequest) == denied,
                "owner denial resolves a request durably without turning its retry into another request");
            var thirdPending = store.Request(new("request-3", "chat-a", "chat-b", denied.Revision));
            Check(Rejected<InvalidOperationException>(() => store.SetAllowed(new(thirdPending.Pair, thirdPending.Revision, true, deniedRequest.RequestId))) &&
                Rejected<ConversationCommunicationRevisionException>(() => store.SetAllowed(new(thirdPending.Pair, secondPending.Revision, true, "request-3"))),
                "newer requests reject both stale displayed revisions and stale request identities");
            store.SetAllowed(new(thirdPending.Pair, thirdPending.Revision, true, "request-3"));

            var contenders = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                var independent = new ConversationCommunicationStore(new EventJournal(path));
                try { independent.SetAllowed(new(new("race-a", "race-b"), 0, index == 0)); return true; }
                catch (ConversationCommunicationRevisionException) { return false; }
            })));
            Check(contenders.Count(success => success) == 1 && store.Get("race-b", "race-a").Revision == 1,
                "independent owner writers serialize so only one decision with the same revision commits");
            var requestContenders = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                var independent = new ConversationCommunicationStore(new EventJournal(path));
                try { independent.Request(new("race-request-" + index, "request-race-a", "request-race-b", 0)); return true; }
                catch (ConversationCommunicationRevisionException) { return false; }
            })));
            Check(requestContenders.Count(success => success) == 1 &&
                store.Get("request-race-a", "request-race-b") is { Allowed: false, Revision: 1, PendingRequest: not null },
                "competing new requests leave one pending request and no grant");

            var firstPage = store.List(limit: 1);
            var secondPage = store.List(firstPage.NextAfterPair, limit: 1);
            var thirdPage = store.List(secondPage.NextAfterPair, limit: 1);
            Check(firstPage.Permissions.Count == 1 && secondPage.Permissions.Count == 1 && thirdPage.Permissions.Count == 1 &&
                thirdPage.NextAfterPair is null && new[] { firstPage, secondPage, thirdPage }
                    .SelectMany(page => page.Permissions).Select(state => state.Pair).Distinct().Count() == 3,
                "pair keyset pages are bounded and cover unchanged state without omissions or duplicates");
            Check(store.List(pendingOnly: true).Permissions.Single().Pair == new ConversationCommunicationPair("request-race-a", "request-race-b"),
                "pending list excludes allowed and resolved denied pairs");
            Check(store.List(limit: 1, conversationId: "chat-b").Permissions.Single().Pair == pending.Pair &&
                store.List(limit: 1, conversationId: "chat-b").NextAfterPair is null &&
                store.List(conversationId: "unrelated-chat").Permissions.Count == 0,
                "conversation filtering happens before page limits and matches either side of the pair");

            var directoryStore = new ConversationCommunicationStore(new EventJournal(Path.Combine(root, "directory.db")));
            var workflows = new WorkflowStore(new EventJournal(Path.Combine(root, "directory.db")));
            foreach (var id in new[] { "directory-a", "directory-b", "directory-c" })
            {
                var submission = new WorkflowSubmission("run-" + id, id, "profile-" + id, "fixture:chat", "1.0.0",
                    "PRIVATE-TRANSCRIPT-TITLE", "PRIVATE-ASSIGNMENT", WorkflowContract.EmptyObject,
                    JsonSerializer.SerializeToElement(new { marker = "PRIVATE-SETTINGS" })) { ProjectId = "project-fixture" };
                workflows.Cancel(workflows.Submit(submission).Id);
            }
            var directory = directoryStore.ReadDirectory(limit: 2);
            var directoryTail = directoryStore.ReadDirectory(directory.NextAfterConversationId, limit: 2);
            Check(directory.Conversations.Select(entry => entry.ConversationId).SequenceEqual(["directory-a", "directory-b"]) &&
                directory.NextAfterConversationId == "directory-b" && directoryTail.Conversations.Single().ConversationId == "directory-c" &&
                directoryTail.NextAfterConversationId is null && directory.Conversations[0].ProfileId == "profile-directory-a" &&
                directory.Conversations[0].ProjectId == "project-fixture", "directory pages contain registered identity coordinates only");
            Check(!JsonSerializer.Serialize(directory).Contains("PRIVATE-", StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(directory).Contains("run-directory", StringComparison.Ordinal),
                "directory omits titles, run identities, messages, settings and assignment content");

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(Rejected<OperationCanceledException>(() => store.Request(new("cancelled", "cancel-a", "cancel-b", 0), cancelled.Token)) &&
                Rejected<OperationCanceledException>(() => store.SetAllowed(new(new("cancel-a", "cancel-b"), 0, true), cancelled.Token)) &&
                store.Get("cancel-a", "cancel-b").Revision == 0, "cancelled mutations commit no pair state");
            Check(Rejected<ArgumentException>(() => store.Get("same", "same")) &&
                Rejected<ArgumentException>(() => store.Get("bad id", "good")) &&
                Rejected<ArgumentException>(() => store.Get("bad\ud800", "good")) &&
                Rejected<ArgumentOutOfRangeException>(() => store.Request(new("negative", "a", "b", -1))) &&
                Rejected<ArgumentOutOfRangeException>(() => store.List(limit: 0)) &&
                Rejected<ArgumentOutOfRangeException>(() => store.List(limit: ConversationCommunicationContract.MaximumPageSize + 1)) &&
                Rejected<ArgumentOutOfRangeException>(() => store.ReadDirectory(limit: 0)),
                "invalid identities, negative revisions and out-of-bound page requests reject");

            var quotaPath = Path.Combine(root, "quota.db");
            var quota = new ConversationCommunicationStore(new EventJournal(quotaPath));
            Register(quotaPath, ["quota-source", "overflow-target", .. Enumerable.Range(0,
                ConversationCommunicationContract.MaximumPendingRequests).Select(index => "quota-target-" + index)]);
            for (var index = 0; index < ConversationCommunicationContract.MaximumPendingRequests; index++)
                quota.Request(new("quota-" + index, "quota-source", "quota-target-" + index, 0));
            var overflow = new ConversationCommunicationRequest("quota-overflow", "quota-source", "overflow-target", 0);
            Check(Rejected<InvalidOperationException>(() => quota.Request(overflow)) &&
                quota.Get("quota-source", "overflow-target").Revision == 0 &&
                quota.Request(new("quota-0", "quota-source", "quota-target-0", 0)).PendingRequest?.RequestId == "quota-0",
                "pending capacity is bounded while exact retries remain available at capacity");
            var resolved = quota.Get("quota-source", "quota-target-0");
            quota.SetAllowed(new(resolved.Pair, resolved.Revision, false, resolved.PendingRequest!.RequestId));
            Check(quota.Request(overflow).PendingRequest?.RequestId == overflow.RequestId,
                "failed capacity admission rolls back request identity so it can be admitted after resolution");

            var missingMetadata = new ConversationCommunicationStore(new EventJournal(Path.Combine(root, "metadata.db")));
            Register(Path.Combine(root, "metadata.db"), "registered-source", "registered-target");
            var metadataAllowed = missingMetadata.SetAllowed(new(new("registered-source", "registered-target"), 0, true));
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "metadata.db") + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM workflow_conversations WHERE conversation_id='registered-target';";
                command.ExecuteNonQuery();
            }
            Check(Rejected<InvalidOperationException>(() => missingMetadata.Get("registered-source", "registered-target")) &&
                !missingMetadata.SetAllowed(new(metadataAllowed.Pair, metadataAllowed.Revision, false)).Allowed,
                "missing metadata fails closed for delivery checks while owner revoke remains possible");
            using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=10;"; command.ExecuteNonQuery();
            }
            Check(Rejected<InvalidOperationException>(() => new EventJournal(path)),
                "current host rejects a future journal format rather than adopting it");

            Console.WriteLine($"Conversation communication: {checks} temporary SQLite integration assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
