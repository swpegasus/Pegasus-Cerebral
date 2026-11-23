using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ConfigurationRestoreReceiptTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-restore-receipt-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Exception? failure = null; var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
            { checks++; return; }
            throw new Exception("FAIL: " + label);
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "main"));
            var config = new Configuration(store); _ = config.Get();
            var notifications = 0; var busy = false; var notificationSawCommit = false;
            var backup = new ConfigurationBackups(store, () => busy, () =>
            {
                notifications++;
                notificationSawCommit = new StateStore(store.DirectoryPath).Get<HostSettings>("settings", "host")!.Revision > 0;
                throw new InvalidOperationException("notification-secret-canary");
            });
            var doc = backup.Export(true).Document;
            doc = doc with { Host = doc.Host with { QueueCapacity = 37 } };
            var preview = backup.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(doc)), true);
            var request = new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true);
            var args = CerebralJson.Element(request);
            Check(backup.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(request.PreviewId)), true).State == "unknown",
                "a pending preview is not falsely reported as committed");
            var result = backup.Apply(args, true); // Intentionally lose this result at the caller boundary.
            Check(notificationSawCommit, "notification sees the committed host through a separate SQLite connection");
            Check(result.HostRevision == preview.ExpectedHostRevision + 1 && config.Get().QueueCapacity == 37 && notifications == 1,
                "notification failure does not reclassify the committed restore");
            var reopened = new StateStore(store.DirectoryPath);
            var recovered = new ConfigurationBackups(reopened, () => true, () => throw new Exception("retry must not notify"));
            var status = recovered.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(request.PreviewId)), true);
            Check(status is { State: "committed", Receipt.NotificationOutcome: "failed" } &&
                Same(status.Receipt!.Result, result), "a new store and backup instance recover the exact committed result after lost reply");
            Check(status.Receipt!.ExpectedHostRevision == request.ExpectedHostRevision && status.Receipt.ReviewedFreeText,
                "receipt binds the exact accepted fields");
            var receiptJson = JsonSerializer.Serialize(status, CerebralJson.Options);
            Check(!receiptJson.Contains("notification-secret-canary", StringComparison.Ordinal) &&
                !receiptJson.Contains("content", StringComparison.OrdinalIgnoreCase) &&
                status.MaximumRetainedReceipts == 128 && status.RetentionDays == 30,
                "status contains bounded value-free outcome and explicit retention limits");
            busy = true;
            var host = reopened.Get<HostSettings>("settings", "host")!; host.QueueCapacity = 38; host.Revision++;
            reopened.Put("settings", "host", host);
            Check(Same(recovered.CommittedResult(args, true), result) && Same(recovered.Apply(args, true), result) &&
                reopened.Get<HostSettings>("settings", "host")!.Revision == host.Revision &&
                reopened.Get<HostSettings>("settings", "host")!.QueueCapacity == 38 && notifications == 1,
                "exact retry returns the original result despite current busy state and later owner edits without another write or notification");
            Reject(() => recovered.Apply(CerebralJson.Element(request with { ExpectedHostRevision = request.ExpectedHostRevision + 1 }), true),
                "same preview ID with changed revision conflicts");
            Reject(() => recovered.Apply(CerebralJson.Element(request with { ReviewedFreeText = false }), true), "changed review flag cannot reuse the receipt");
            Reject(() => recovered.Status(CerebralJson.Element(new { }), false), "status rejects nonowner before reading input");
            Reject(() => recovered.CommittedResult(args, false), "receipt shortcut never bypasses ownership");
            Reject(() => recovered.Status(CerebralJson.Element(new { previewId = request.PreviewId, extra = true }), true), "status rejects unknown fields");
            using (var duplicate = JsonDocument.Parse("{\"previewId\":\"" + request.PreviewId + "\",\"previewId\":\"" + request.PreviewId + "\"}"))
                Reject(() => recovered.Status(duplicate.RootElement, true), "status rejects duplicate identity fields");
            var absent = Guid.NewGuid().ToString("N");
            Check(recovered.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(absent)), true).State == "unknown",
                "unknown request has no fabricated nonexecution proof");
            Reject(() => recovered.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(absent, host.Revision, true)), true),
                "missing preview after restart cannot be recreated from a receipt request");

            var rollback = new StateStore(Path.Combine(root, "rollback")); _ = new Configuration(rollback).Get();
            var rollbackBackups = new ConfigurationBackups(rollback, () => false);
            var rollbackDoc = rollbackBackups.Export(true).Document;
            var rollbackPreview = rollbackBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(
                rollbackDoc with { Host = rollbackDoc.Host with { QueueCapacity = 39 } })), true);
            var rollbackArgs = CerebralJson.Element(new ConfigurationRestoreApplyRequest(rollbackPreview.PreviewId, rollbackPreview.ExpectedHostRevision, true));
            var before = Rows(rollback);
            Sql(rollback, "CREATE TRIGGER deny_receipt BEFORE INSERT ON records WHEN NEW.kind='configuration.restore.receipt' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
            Reject(() => rollbackBackups.Apply(rollbackArgs, true), "receipt persistence failure aborts the full restore");
            Check(Rows(rollback) == before && rollbackBackups.Status(CerebralJson.Element(
                new ConfigurationRestoreStatusRequest(rollbackPreview.PreviewId)), true).State == "unknown",
                "host and prompts roll back with the failed receipt; no partial confirmation exists");
            Sql(rollback, "DROP TRIGGER deny_receipt;");
            var rollbackResult = rollbackBackups.Apply(rollbackArgs, true);
            Check(rollbackResult.HostRevision == rollbackPreview.ExpectedHostRevision + 1,
                "a locally proven rolled-back attempt retains its original preview and commits once after the failure is removed");

            var retentionStore = new StateStore(Path.Combine(root, "retention")); _ = new Configuration(retentionStore).Get();
            var clock = new TestTime();
            var retention = new ConfigurationBackups(retentionStore, () => false, clock: clock);
            ConfigurationRestoreApplyRequest? first = null, latest = null;
            for (var index = 0; index < 129; index++)
            {
                var p = retention.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(retention.Export(true).Document)), true);
                latest = new(p.PreviewId, p.ExpectedHostRevision, true); first ??= latest;
                retention.Apply(CerebralJson.Element(latest), true); clock.Now += TimeSpan.FromSeconds(1);
            }
            Check(retentionStore.List<ConfigurationRestoreReceipt>("configuration.restore.receipt").Count == 128 &&
                retention.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(first!.PreviewId)), true).State == "unknown" &&
                retention.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(latest!.PreviewId)), true).State == "committed",
                "receipt storage retains at most 128 outcomes and protects the newest acknowledged operation");
            var retainedRevision = retentionStore.Get<HostSettings>("settings", "host")!.Revision;
            Reject(() => retention.Apply(CerebralJson.Element(first), true), "evicted receipt cannot blindly reapply an expired preview");
            Check(retentionStore.Get<HostSettings>("settings", "host")!.Revision == retainedRevision, "unknown evicted outcome causes no new settings mutation");
            clock.Now += TimeSpan.FromDays(31);
            Check(retention.Status(CerebralJson.Element(new ConfigurationRestoreStatusRequest(latest!.PreviewId)), true).State == "committed",
                "status is read-only and does not delete receipts on lookup");
            var afterAge = retention.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(retention.Export(true).Document)), true);
            retention.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(afterAge.PreviewId, afterAge.ExpectedHostRevision, true)), true);
            Check(retentionStore.List<ConfigurationRestoreReceipt>("configuration.restore.receipt").Count == 1,
                "a new commit prunes receipts older than 30 days in the same bounded transaction");

            var dispatcherStore = new StateStore(Path.Combine(root, "dispatcher"));
            var configuration = new Configuration(dispatcherStore); _ = configuration.Get();
            var events = new EventHub();
            await using var runtime = new ModelRuntime(dispatcherStore, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(dispatcherStore, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var dispatcher = new CommandDispatcher(dispatcherStore, configuration, runtime, scheduler, tools, events);
            var exported = await dispatcher.ExecuteAsync(new("configuration.backup.export", CerebralJson.Element(new { })));
            var export = CerebralJson.Read<ConfigurationBackupExport>(exported.Data);
            var pr = await dispatcher.ExecuteAsync(new("configuration.restore.preview", CerebralJson.Element(new ConfigurationRestorePreviewRequest(export.Document))));
            var pv = CerebralJson.Read<ConfigurationRestorePreview>(pr.Data);
            var apply = new CommandRequest("configuration.restore.apply", CerebralJson.Element(new ConfigurationRestoreApplyRequest(pv.PreviewId, pv.ExpectedHostRevision, true)));
            var applied = await dispatcher.ExecuteAsync(apply);
            var stat = await dispatcher.ExecuteAsync(new("configuration.restore.status", CerebralJson.Element(new ConfigurationRestoreStatusRequest(pv.PreviewId))));
            Check(applied.Success && stat.Success && Same(CerebralJson.Read<ConfigurationRestoreStatus>(stat.Data).Receipt!.Result,
                CerebralJson.Read<ConfigurationRestoreResult>(applied.Data)), "owner dispatcher routes commit and authoritative status through the same backend");
            dispatcherStore.Put("agent", "admin", new AgentProfile { Id = "admin", Capabilities = ["administration"] });
            Check(!(await dispatcher.ExecuteAsync(new("configuration.restore.status", CerebralJson.Element(new ConfigurationRestoreStatusRequest(pv.PreviewId)), "admin"))).Success &&
                !(await dispatcher.ExecuteAsync(apply with { ProfileId = "admin" })).Success,
                "an administrative agent cannot read or replay owner restore receipts");
            // A loaded-state record blocks new restores, but must not block the already committed result.
            dispatcherStore.Put("model", "later", new ModelRecord { Id = "later", Path = Path.Combine(root, "never-opened.gguf"), State = "loaded" });
            var replay = await dispatcher.ExecuteAsync(apply);
            Check(replay.Success && Same(CerebralJson.Read<ConfigurationRestoreResult>(replay.Data),
                CerebralJson.Read<ConfigurationRestoreResult>(applied.Data)), "committed dispatcher retry bypasses later loaded-state admission without loading or touching a model");

            await ConfigurationRestoreClientTests.RunAsync();
            Console.WriteLine($"Configuration restore receipts: {checks} SQLite/dispatcher assertions passed; isolated data, notification failures and restart-by-reopening store. No inference, installed-state or UI interaction.");
        }
        catch (Exception e) { failure = e; throw; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-restore-receipt-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact receipt fixture directory.");
            try { Directory.Delete(root, true); }
            catch (Exception e) when (failure is not null) { throw new AggregateException(failure, e); }
        }
    }

    private static bool Same(object? a, object? b) => JsonSerializer.Serialize(a, CerebralJson.Options) == JsonSerializer.Serialize(b, CerebralJson.Options);
    private static SqliteConnection Open(StateStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db") }.ToString());
        connection.Open(); return connection;
    }
    private static void Sql(StateStore store, string text)
    {
        using var connection = Open(store); using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
    }
    private static string Rows(StateStore store)
    {
        using var connection = Open(store); using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind,id,body FROM records ORDER BY kind,id";
        using var rows = command.ExecuteReader(); var result = new List<string>();
        while (rows.Read()) result.Add(rows.GetString(0) + ":" + rows.GetString(1) + ":" + rows.GetString(2));
        return string.Join("\n", result);
    }
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken ct) => throw new InvalidOperationException("Receipt fixtures must not execute agents.");
    }
}
