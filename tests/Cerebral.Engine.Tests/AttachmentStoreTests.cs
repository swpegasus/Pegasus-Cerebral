using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

/// <summary>Local SQLite/stream integration. Image fixtures exercise structural admission, not a renderer or model.</summary>
public static class AttachmentStoreTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            checks++; Console.WriteLine("PASS: " + message);
        }
        static bool Reject<T>(Action action) where T : Exception
        {
            try { action(); return false; } catch (T) { return true; }
        }
        static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); return false; } catch (T) { return true; }
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-attachment-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "engine.db");
            var journal = new EventJournal(path);
            var store = new AttachmentStore(journal);
            var access = new AttachmentAccessContext("lead", "chat-attachment");
            var bytes = Encoding.UTF8.GetBytes("An exact café transcript.\r\nSecond line.\n");
            using var input = new MemoryStream(bytes);
            var first = await store.ImportAsync(access, "import-one", "notes.txt", input);
            Check(first.Revision == 1 && Guid.TryParseExact(first.Id, "N", out _) &&
                first.OriginalName == "notes.txt" && first.MediaType == "text/plain" && first.Length == bytes.Length,
                "text import returns an opaque immutable coordinate and admitted public metadata");
            Check(input.CanRead && input.Position == input.Length, "import consumes remaining source bytes but leaves the caller-owned stream open");
            var json = JsonSerializer.Serialize(first);
            Check(!json.Contains("StorageKey", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains(root, StringComparison.Ordinal) && !json.Contains(Convert.ToBase64String(bytes), StringComparison.Ordinal),
                "attachment reference contains no storage path or original binary payload");
            var exact = store.ReadRange(access, first.Id, 1, 0, bytes.Length);
            Check(exact.Offset == 0 && exact.Bytes.Span.SequenceEqual(bytes) &&
                store.ReadText(access, first.Id, 1).Text == Encoding.UTF8.GetString(bytes),
                "range and whole-text reads preserve the original bytes, Unicode, and line endings");
            Check(store.ReadRange(access, first.Id, 1, bytes.Length, 0).Bytes.Length == 0,
                "an exact empty interval at EOF is valid");
            Check(Reject<ArgumentOutOfRangeException>(() => store.ReadRange(access, first.Id, 1, bytes.Length - 1, 2)) &&
                Reject<ArgumentOutOfRangeException>(() => store.ReadRange(access, first.Id, 1, -1, 1)) &&
                Reject<ArgumentOutOfRangeException>(() => store.ReadRange(access, first.Id, 1, long.MaxValue, 1)) &&
                Reject<ArgumentOutOfRangeException>(() => store.ReadRange(access, first.Id, 1, 0, AttachmentStore.MaximumRangeBytes + 1)),
                "negative, oversized, overflow-sized, and past-EOF intervals fail instead of clipping");
            Check(Reject<InvalidOperationException>(() => store.ReadText(access, first.Id, 1, bytes.Length - 1)),
                "text extraction rejects an insufficient full-source byte budget");
            Check(Reject<KeyNotFoundException>(() => store.Describe(access with { ProfileId = "other" }, first.Id, 1)) &&
                Reject<KeyNotFoundException>(() => store.ReadText(access with { ConversationId = "other" }, first.Id, 1)) &&
                Reject<KeyNotFoundException>(() => store.ReadRange(access, Guid.NewGuid().ToString("N"), 1, 0, 1)),
                "foreign profile, foreign conversation, and unavailable IDs cannot resolve an original");
            Check(Reject<InvalidOperationException>(() => store.Describe(access, first.Id, 2)) &&
                Reject<InvalidDataException>(() => store.ValidateReference(access, first with { MediaType = "image/png" })) &&
                Reject<InvalidDataException>(() => store.ValidateReference(access, first with { OriginalName = "renamed.txt" })) &&
                Reject<InvalidDataException>(() => store.ValidateReference(access, first with { Length = 1 })) &&
                Reject<InvalidDataException>(() => store.ValidateReference(access, first with { Metadata = new Dictionary<string, string> { ["claim"] = "trusted" } })),
                "revision and caller-supplied attachment metadata cannot override retained admission");
            Check(journal.ReadPage(access.ConversationId).Events.Count == 0 &&
                Scalar(path, "SELECT COUNT(*) FROM workflow_runs;") == 0 &&
                Scalar(path, "SELECT COUNT(*) FROM workflow_conversations WHERE conversation_id='chat-attachment' AND profile_id='lead';") == 1,
                "import reserves the future conversation owner without adding a run or transcript event");
            var reopened = new AttachmentStore(new EventJournal(path));
            using var retryBytes = new MemoryStream(bytes);
            var retry = await reopened.ImportAsync(access, "import-one", "notes.txt", retryBytes);
            Check(retry.Id == first.Id && reopened.ReadText(access, first.Id, 1).Text == store.ReadText(access, first.Id, 1).Text &&
                Scalar(path, "SELECT COUNT(*) FROM resource_attachments;") == 1 &&
                Scalar(path, "SELECT SUM(length(content)) FROM resource_attachments;") == bytes.Length,
                "restart and an exact lost-response retry retain one original BLOB and the original receipt");
            Check(reopened.FindImportReceipt(access, "import-one")?.Id == first.Id &&
                reopened.FindImportReceipt(access, "missing-request") is null &&
                reopened.FindImportReceipt(access with { ConversationId = "foreign" }, "import-one") is null &&
                reopened.FindImportReceipt(access with { ProfileId = "foreign" }, "import-one") is null &&
                Scalar(path, "SELECT COUNT(*) FROM resource_attachments;") == 1,
                "read-only receipt lookup reconciles a lost response without reopening bytes or exposing another scope");
            Check(await RejectAsync<InvalidOperationException>(() => store.ImportAsync(access, "import-one", "notes.txt", new MemoryStream("changed"u8.ToArray()))) &&
                await RejectAsync<InvalidOperationException>(() => store.ImportAsync(access, "import-one", "other.txt", new MemoryStream(bytes))) &&
                await RejectAsync<InvalidOperationException>(() => store.ImportAsync(access with { ConversationId = "wrong" }, "import-one", "notes.txt", new MemoryStream(bytes))),
                "the same import request cannot be reused for changed bytes, name, or access scope");
            Check(await RejectAsync<InvalidOperationException>(() => store.ImportAsync(access with { ProfileId = "other" }, "owner-conflict", "notes.txt", new MemoryStream(bytes))) &&
                Scalar(path, "SELECT COUNT(*) FROM resource_attachments;") == 1,
                "a different profile cannot claim an already-reserved conversation");
            var competitors = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                using var stream = new MemoryStream(bytes);
                return await new AttachmentStore(new EventJournal(path)).ImportAsync(access, "race", "race.txt", stream);
            })));
            Check(competitors[0].Id == competitors[1].Id && Scalar(path, "SELECT COUNT(*) FROM resource_attachments WHERE request_id='race';") == 1,
                "concurrent exact imports serialize to a single receipt and original");

            var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/RxQAAAAASUVORK5CYII=");
            var png = await store.ImportAsync(access, "png", "map.PNG", new MemoryStream(pngBytes));
            Check(png.MediaType == "image/png" && png.Metadata?["image.width"] == "1" && png.Metadata?["image.height"] == "1" &&
                store.ReadRange(access, png.Id, 1, 0, pngBytes.Length).Bytes.Span.SequenceEqual(pngBytes),
                "PNG structural admission records bounded dimensions and retains the exact original");
            Check(Reject<NotSupportedException>(() => store.ReadText(access, png.Id, 1)) &&
                Reject<InvalidDataException>(() => store.ValidateReference(access, png with { Metadata = new Dictionary<string, string> { ["image.width"] = "999", ["image.height"] = "1" } })),
                "images cannot be relabeled as text or forge inspected dimensions");
            var jpegBytes = new byte[]
            {
                0xff,0xd8,0xff,0xc0,0,11,8,0,1,0,1,1,1,0x11,0,
                0xff,0xda,0,8,1,1,0,0,0x3f,0,0,0xff,0xd9
            };
            var jpeg = await store.ImportAsync(access, "jpeg", "photo.jpeg", new MemoryStream(jpegBytes));
            Check(jpeg.MediaType == "image/jpeg" && jpeg.Metadata?["image.width"] == "1",
                "JPEG structural fixture is admitted through its frame dimensions, without claiming a decode");
            var overDimension = pngBytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(overDimension.AsSpan(16, 4), 20_000);
            var overPixels = pngBytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(overPixels.AsSpan(16, 4), 10_000);
            BinaryPrimitives.WriteUInt32BigEndian(overPixels.AsSpan(20, 4), 10_000);
            Check(await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "wide", "wide.png", new MemoryStream(overDimension))) &&
                await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "pixels", "pixels.png", new MemoryStream(overPixels))),
                "single-dimension and combined-pixel bombs fail admission before decoder use");
            Check(await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "wrong-signature", "bad.png", new MemoryStream(bytes))) &&
                await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "truncated", "bad.png", new MemoryStream(pngBytes[..^1]))) &&
                await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "wrong-jpeg", "bad.jpeg", new MemoryStream(pngBytes))),
                "extension-spoofed and truncated image framing is rejected");
            var executable = await store.ImportAsync(access, "exe", "app.exe", new MemoryStream(bytes));
            var archive = await store.ImportAsync(access, "archive", "stuff.zip", new MemoryStream(bytes));
            var vector = await store.ImportAsync(access, "svg", "draw.svg", new MemoryStream(bytes));
            Check(executable.MediaType == "application/octet-stream" && executable.Metadata?["file.extension"] == ".exe" &&
                archive.MediaType == "application/zip" && archive.Metadata?["handler.archive"] == "cortex.7zip" &&
                vector.MediaType == "application/octet-stream" && vector.Metadata?["file.extension"] == ".svg" &&
                Reject<NotSupportedException>(() => store.ReadText(access, executable.Id, executable.Revision)) &&
                Reject<NotSupportedException>(() => store.ReadText(access, archive.Id, archive.Revision)) &&
                Reject<NotSupportedException>(() => store.ReadText(access, vector.Id, vector.Revision)),
                "opaque executable/vector files remain inert while archive packages retain the explicit typed 7-Zip handler boundary");
            Check(await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "bad-utf8", "bad.txt", new MemoryStream([0xc3,0x28]))) &&
                await RejectAsync<InvalidDataException>(() => store.ImportAsync(access, "nul", "bad.txt", new MemoryStream([65,0,66]))),
                "invalid UTF-8 and binary NUL text are rejected without replacement decoding");
            var bomBytes = new byte[] { 0xef, 0xbb, 0xbf, 65, 10 };
            var bom = await store.ImportAsync(access, "bom", "unicode.txt", new MemoryStream(bomBytes));
            Check(store.ReadText(access, bom.Id, 1).Text == "\ufeffA\n" &&
                store.ReadRange(access, bom.Id, 1, 0, bomBytes.Length).Bytes.Span.SequenceEqual(bomBytes),
                "UTF-8 BOM and line endings remain exact rather than silently rewritten");
            var empty = await store.ImportAsync(access, "empty", "empty.txt", new MemoryStream());
            Check(empty.Length == 0 && store.ReadText(access, empty.Id, 1, 0).Text == "" &&
                store.ReadRange(access, empty.Id, 1, 0, 0).Bytes.IsEmpty,
                "empty UTF-8 files remain valid immutable sources");
            Check(await RejectAsync<ArgumentException>(() => store.ImportAsync(access, "path-name", @"C:\private\notes.txt", new MemoryStream(bytes))) &&
                await RejectAsync<ArgumentException>(() => store.ImportAsync(access, "long-name", new string('é', 130) + ".txt", new MemoryStream(bytes))) &&
                await RejectAsync<ArgumentException>(() => store.ImportAsync(access, "line-name", "line\nname.txt", new MemoryStream(bytes))),
                "original file names cannot carry paths, control characters, or unbounded UTF-8 metadata");

            var beforeFailures = Scalar(path, "SELECT COUNT(*) FROM resource_attachments;");
            var freshScope = new AttachmentAccessContext("lead", "cancelled-before-import");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(await RejectAsync<OperationCanceledException>(() => store.ImportAsync(freshScope, "cancel-before", "notes.txt", new MemoryStream(bytes), cancelled.Token)) &&
                    Reject<OperationCanceledException>(() => store.Describe(access, first.Id, 1, cancelled.Token)) &&
                    Reject<OperationCanceledException>(() => store.ReadRange(access, first.Id, 1, 0, 1, cancelled.Token)) &&
                    Reject<OperationCanceledException>(() => store.ReadText(access, first.Id, 1, cancellationToken: cancelled.Token)),
                    "pre-cancelled imports and reads stop without returning a success receipt");
            }
            using (var midway = new CancellationTokenSource())
            using (var cancelStream = new InterruptingStream(midway))
                Check(await RejectAsync<OperationCanceledException>(() => store.ImportAsync(freshScope, "cancel-mid", "notes.txt", cancelStream, midway.Token)),
                    "cancellation after partial source bytes does not admit a partial attachment");
            using (var broken = new InterruptingStream(null))
                Check(await RejectAsync<IOException>(() => store.ImportAsync(freshScope, "io-failure", "notes.txt", broken)),
                    "source read failures do not admit a partial attachment");
            using (var tooMuchText = new RepeatingStream(AttachmentStore.MaximumTextBytes + 1L))
                Check(await RejectAsync<InvalidDataException>(() => store.ImportAsync(freshScope, "too-much-text", "large.txt", tooMuchText)) &&
                    tooMuchText.ReadBytes == AttachmentStore.MaximumTextBytes + 1L,
                    "text overflow reads only the bound plus one byte before rejecting");
            using (var tooMuchImage = new RepeatingStream(AttachmentStore.MaximumAttachmentBytes + 1L))
                Check(await RejectAsync<InvalidDataException>(() => store.ImportAsync(freshScope, "too-much-image", "large.png", tooMuchImage)) &&
                    tooMuchImage.ReadBytes == AttachmentStore.MaximumAttachmentBytes + 1L,
                    "binary overflow reads only the bound plus one byte before rejecting");
            Check(Scalar(path, "SELECT COUNT(*) FROM resource_attachments;") == beforeFailures &&
                Scalar(path, "SELECT COUNT(*) FROM workflow_conversations WHERE conversation_id='cancelled-before-import';") == 0,
                "cancelled, failed, and oversized imports leave neither an original nor an owner reservation");

            var quotaPath = Path.Combine(root, "quota.db");
            var quotaJournal = new EventJournal(quotaPath);
            var quota = new AttachmentStore(quotaJournal, new(MaximumStoredBytes: 5, MaximumAttachmentsPerConversation: 1));
            var quotaScope = new AttachmentAccessContext("lead", "quota-chat");
            var quotaFirst = await quota.ImportAsync(quotaScope, "quota-one", "one.txt", new MemoryStream("123"u8.ToArray()));
            Check(await RejectAsync<InvalidOperationException>(() => quota.ImportAsync(quotaScope, "quota-count", "two.txt", new MemoryStream("4"u8.ToArray()))) &&
                await RejectAsync<InvalidOperationException>(() => quota.ImportAsync(quotaScope with { ConversationId = "quota-other" }, "quota-bytes", "two.txt", new MemoryStream("456"u8.ToArray()))),
                "aggregate stored bytes and per-conversation item limits reject new admission transactionally");
            var restricted = new AttachmentStore(quotaJournal, new(MaximumStoredBytes: 0, MaximumAttachmentsPerConversation: 0));
            var quotaRetry = await restricted.ImportAsync(quotaScope, "quota-one", "one.txt", new MemoryStream("123"u8.ToArray()));
            Check(quotaRetry.Id == quotaFirst.Id && restricted.ReadText(quotaScope, quotaFirst.Id, 1).Text == "123" &&
                Scalar(quotaPath, "SELECT COUNT(*) FROM resource_attachments;") == 1 &&
                Scalar(quotaPath, "SELECT COUNT(*) FROM workflow_conversations WHERE conversation_id='quota-other';") == 0,
                "lowered quotas preserve existing originals and exact retries without reserving failed conversations");
            Check(Reject<ArgumentOutOfRangeException>(() => new AttachmentStore(quotaJournal, new(MaximumStoredBytes: -1))) &&
                Reject<ArgumentOutOfRangeException>(() => new AttachmentStore(quotaJournal, new(MaximumAttachmentsPerConversation: -1))),
                "invalid capacity options fail before creating an attachment policy");
            await RetentionWriterIsolation(root, Check);
            Console.WriteLine($"Attachment store: {checks} local SQLite/stream integration assertions passed; no renderer, model, or pipe transport claimed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RetentionWriterIsolation(string root, Action<bool, string> check)
    {
        var noOpPath = Path.Combine(root, "retention-no-op.db");
        var noOpJournal = new EventJournal(noOpPath);
        noOpJournal.Append(Rolling("no-op", "no-op-chat", DateTimeOffset.Parse("2026-09-25T00:00:00Z")), 0);
        using (var blocker = Open(noOpPath))
        using (var writer = blocker.BeginTransaction(deferred: false))
        {
            var prune = Task.Run(() => noOpJournal.PruneRolling("no-op-chat",
                new(100, 4 * 1024 * 1024, 30), DateTimeOffset.Parse("2026-09-26T00:00:00Z")));
            var completedWhileWriterReserved = await Task.WhenAny(prune, Task.Delay(TimeSpan.FromSeconds(2))) == prune;
            writer.Rollback();
            var result = await prune;
            check(completedWhileWriterReserved && result.RemovedEvents == 0 && result.QuotaSatisfied &&
                  noOpJournal.ReadPage("no-op-chat").Events.Count == 1,
                "no-op retention analysis stays read-only and completes while an unrelated writer is reserved");
        }

        var reasonPath = Path.Combine(root, "retention-reasons.db");
        var reasonJournal = new EventJournal(reasonPath);
        var reasonAt = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
        reasonJournal.Append(Rolling("count-1", "count-chat", reasonAt), 0);
        reasonJournal.Append(Rolling("count-2", "count-chat", reasonAt), 1);
        var count = reasonJournal.PruneRolling("count-chat", new(1, 4 * 1024 * 1024, 30), reasonAt);
        reasonJournal.Append(Rolling("bytes-1", "bytes-chat", reasonAt), 0);
        var bytes = reasonJournal.PruneRolling("bytes-chat", new(100, 1, 30), reasonAt);
        reasonJournal.Append(Rolling("age-1", "age-chat", reasonAt.AddDays(-10)), 0);
        var age = reasonJournal.PruneRolling("age-chat", new(100, 4 * 1024 * 1024, 1), reasonAt);
        check(count.RemovedEvents == 1 && bytes.RemovedEvents == 1 && age.RemovedEvents == 1 &&
              reasonJournal.ReadPage("count-chat").Events.Single().Id == "count-2" &&
              reasonJournal.ReadPage("bytes-chat").Events.Count == 0 && reasonJournal.ReadPage("age-chat").Events.Count == 0,
            "two-phase cleanup preserves independent count, byte, and age retention reasons");

        var pinPath = Path.Combine(root, "retention-pin-race.db");
        var pinJournal = new EventJournal(pinPath);
        const string pinConversation = "pin-race-chat";
        const string pinRun = "pin-race-run";
        using (var setup = Open(pinPath))
        using (var command = setup.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO workflow_runs(id,request_id,conversation_id,profile_id,status,revision,submission,payload)
                VALUES($id,'pin-race-request',$conversation,'lead','completed',1,'{}','{}');
                """;
            command.Parameters.AddWithValue("$id", pinRun);
            command.Parameters.AddWithValue("$conversation", pinConversation);
            command.ExecuteNonQuery();
        }
        pinJournal.Append(Rolling("pin-race-event", pinConversation, reasonAt.AddDays(-10),
            JsonSerializer.SerializeToElement(new { runId = pinRun })), 0);
        var pinPlan = AnalyzeRolling(pinJournal, pinConversation,
            new(100, 4 * 1024 * 1024, 1), reasonAt);
        check(RollingCandidateCount(pinPlan) == 1,
            "pin-race fixture captures one eligible candidate before the workflow becomes active");
        using (var updateConnection = Open(pinPath))
        using (var update = updateConnection.CreateCommand())
        {
            update.CommandText = "UPDATE workflow_runs SET status='running' WHERE id=$id;";
            update.Parameters.AddWithValue("$id", pinRun);
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Pin-race fixture did not update its run.");
        }
        check(!ApplyRollingPlan(pinJournal, pinConversation, new(100, 4 * 1024 * 1024, 1), reasonAt, pinPlan) &&
              pinJournal.ReadPage(pinConversation).Events.Single().Id == "pin-race-event",
            "writer-phase revalidation rejects an exact analyzed plan when workflow evidence becomes active");

        var counterPath = Path.Combine(root, "retention-counter-race.db");
        var counterJournal = new EventJournal(counterPath);
        const string counterConversation = "counter-race-chat";
        for (var index = 0; index < 3; index++)
            counterJournal.Append(Rolling("counter-" + index, counterConversation, reasonAt), index);
        var counterPlan = AnalyzeRolling(counterJournal, counterConversation,
            new(1, 4 * 1024 * 1024, 30), reasonAt);
        check(RollingCandidateCount(counterPlan) == 2,
            "retention-counter fixture captures the exact two stale count-ranked candidates");
        using (var blocker = Open(counterPath))
        using (var writer = blocker.BeginTransaction(deferred: false))
        {
            long removedBytes;
            using (var measure = blocker.CreateCommand())
            {
                measure.Transaction = writer;
                measure.CommandText = "SELECT SUM(length(CAST(payload AS BLOB))) FROM conversation_events WHERE conversation_id=$conversation AND sequence<=2;";
                measure.Parameters.AddWithValue("$conversation", counterConversation);
                removedBytes = Convert.ToInt64(measure.ExecuteScalar());
            }
            using (var concurrent = blocker.CreateCommand())
            {
                concurrent.Transaction = writer;
                concurrent.CommandText = """
                    DELETE FROM conversation_events WHERE conversation_id=$conversation AND sequence<=2;
                    INSERT INTO event_retention(conversation_id,pruned_events,pruned_bytes,first_pruned_sequence,
                        last_pruned_sequence,last_pruned_at,maximum_rolling_events,maximum_rolling_bytes,maximum_age_days)
                    VALUES($conversation,2,$bytes,1,2,$at,1,4194304,30);
                    """;
                concurrent.Parameters.AddWithValue("$conversation", counterConversation);
                concurrent.Parameters.AddWithValue("$bytes", removedBytes);
                concurrent.Parameters.AddWithValue("$at", reasonAt.ToString("O"));
                concurrent.ExecuteNonQuery();
            }
            writer.Commit();
        }
        check(!ApplyRollingPlan(counterJournal, counterConversation, new(1, 4 * 1024 * 1024, 30), reasonAt, counterPlan) &&
              counterJournal.GetRetentionStatus(counterConversation).PrunedEvents == 2 &&
              counterJournal.GetRevision(counterConversation) == 3 &&
              counterJournal.ReadPage(counterConversation).Events.Single().Id == "counter-2",
            "a changed retention counter invalidates stale count ranks without rewriting stream revision or deleting again");

        var revisionPath = Path.Combine(root, "retention-revision-race.db");
        var revisionJournal = new EventJournal(revisionPath);
        const string revisionConversation = "revision-race-chat";
        revisionJournal.Append(Rolling("revision-0", revisionConversation, reasonAt), 0);
        revisionJournal.Append(Rolling("revision-1", revisionConversation, reasonAt), 1);
        var revisionPlan = AnalyzeRolling(revisionJournal, revisionConversation,
            new(1, 4 * 1024 * 1024, 30), reasonAt);
        check(RollingCandidateCount(revisionPlan) == 1,
            "stream-revision fixture captures one count-ranked candidate before an append");
        revisionJournal.Append(Rolling("revision-2", revisionConversation, reasonAt), 2);
        check(!ApplyRollingPlan(revisionJournal, revisionConversation,
                  new(1, 4 * 1024 * 1024, 30), reasonAt, revisionPlan) &&
              revisionJournal.GetRevision(revisionConversation) == 3 &&
              revisionJournal.ReadPage(revisionConversation).Events.Count == 3,
            "a stream revision append invalidates an analyzed plan before destructive apply");

        var cancelPath = Path.Combine(root, "retention-cancel-race.db");
        var cancelJournal = new EventJournal(cancelPath);
        const string cancelConversation = "cancel-race-chat";
        cancelJournal.Append(Rolling("cancel-race-event", cancelConversation, reasonAt.AddDays(-10)), 0);
        using (var blocker = Open(cancelPath))
        using (var writer = blocker.BeginTransaction(deferred: false))
        using (var cancellation = new CancellationTokenSource())
        {
            using var started = new ManualResetEventSlim();
            var prune = Task.Run(() =>
            {
                started.Set();
                return cancelJournal.PruneRolling(cancelConversation,
                    new(100, 4 * 1024 * 1024, 1), reasonAt, cancellation.Token);
            });
            started.Wait();
            cancellation.Cancel();
            writer.Rollback();
            var rejected = false;
            try { await prune; }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected && cancelJournal.ReadPage(cancelConversation).Events.Single().Id == "cancel-race-event" &&
                  cancelJournal.GetRetentionStatus(cancelConversation).PrunedEvents == 0,
                "cancellation while a planned cleanup waits for its writer rolls back deletion and retention metadata");
        }
    }

    private static object AnalyzeRolling(EventJournal journal, string conversationId, JournalRetentionLimits limits,
        DateTimeOffset now)
    {
        var method = typeof(EventJournal).GetMethod("AnalyzeRolling", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(typeof(EventJournal).FullName, "AnalyzeRolling");
        return method.Invoke(journal, [conversationId, limits, now.AddDays(-limits.MaximumAgeDays), CancellationToken.None])
               ?? throw new InvalidDataException("Rolling analysis did not return a plan.");
    }

    private static int RollingCandidateCount(object plan)
    {
        var candidates = plan.GetType().GetProperty("Candidates", BindingFlags.Instance | BindingFlags.Public)
                             ?.GetValue(plan) as System.Collections.ICollection;
        return candidates?.Count ?? throw new InvalidDataException("Rolling analysis did not expose bounded candidates.");
    }

    private static bool ApplyRollingPlan(EventJournal journal, string conversationId, JournalRetentionLimits limits,
        DateTimeOffset now, object plan)
    {
        var method = typeof(EventJournal).GetMethod("ApplyRollingPlan", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(typeof(EventJournal).FullName, "ApplyRollingPlan");
        var applied = method.Invoke(journal,
            [conversationId, limits, now, now.AddDays(-limits.MaximumAgeDays), plan, CancellationToken.None])
            ?? throw new InvalidDataException("Rolling apply did not return a result.");
        return applied.GetType().GetProperty("Applied", BindingFlags.Instance | BindingFlags.Public)
                   ?.GetValue(applied) as bool?
               ?? throw new InvalidDataException("Rolling apply result did not expose its outcome.");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private static CerebralEvent Rolling(string id, string conversation, DateTimeOffset at, JsonElement? data = null) =>
        new(id, conversation, "turn", 0, CerebralEventTypes.WorkflowTransition,
            new(EventActorKind.Service, "retention-fixture"), [new TextContentPart("bounded rolling diagnostic")],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity, RetentionPolicy.Rolling),
            new(EventSourceKind.Service, "retention-fixture"), at,
            data ?? JsonSerializer.SerializeToElement(new { reason = "fixture" }));

    private sealed class RepeatingStream(long length) : Stream
    {
        public long ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - ReadBytes);
            buffer.AsSpan(offset, read).Fill((byte)'x'); ReadBytes += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - ReadBytes);
            buffer.Span[..read].Fill((byte)'x'); ReadBytes += read; return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InterruptingStream(CancellationTokenSource? cancellation) : Stream
    {
        private int reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => reads; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads++ == 0) { buffer.Span[0] = (byte)'x'; return ValueTask.FromResult(1); }
            if (cancellation is null) throw new IOException("Synthetic selected-file read failure.");
            cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(0);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
