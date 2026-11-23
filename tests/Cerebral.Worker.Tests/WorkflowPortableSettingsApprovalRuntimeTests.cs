using System.Text.Json;
using System.Runtime.CompilerServices;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class WorkflowPortableSettingsApprovalRuntimeTests
{
    private const string PluginId = "fixture.portable";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-portable-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Exercise(root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 9) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(50); }
            }
        }
        Console.WriteLine("Workflow portable-settings approval checks passed (real loaded fixture and SQLite; no model/browser/network).");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exercise(string root)
    {
            var repository = Path.Combine(root, "repository");
            var data = Path.Combine(root, "plugin-data");
            var state = new StateStore(Path.Combine(root, "state"));
            var sourceManifest = PrepareFixture(root);
            using (var beforeLoad = new ServicePluginRuntime(repository, data, new EventHub()))
            {
                var lifecycle = new ServicePackageLifecycle(repository, beforeLoad, new EventHub());
                lifecycle.Attach(new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db"))));
                var staged = lifecycle.Stage(sourceManifest, 0);
                var selected = lifecycle.Activate(PluginId, staged.Revision);
                Check(selected.DesiredActive && selected.RestartRequired, "fixture requires a normal loaded generation");
            }

            using var loaded = new ServicePluginRuntime(repository, data, new EventHub());
            var owner = new ServicePackageLifecycle(repository, loaded, new EventHub());
            var review = owner.Inspect(PluginId, state).WorkflowSettings
                ?? throw new Exception("FAIL: loaded portable settings review unavailable");
            Check(review is { ApprovalRevision: 0, ActiveApproval: null } &&
                  review.DeclaredFields.SequenceEqual(["style", "tone"]),
                "loaded package exposes exact owner review facts before approval");
            var profile = new AgentProfile { Id = "portable-profile", WorkerId = "local" };
            using (var document = JsonDocument.Parse("""{"style":"private-marker"}"""))
                profile.PluginSettings[PluginId] = document.RootElement.Clone();
            state.Put("agent", profile.Id, profile);

            var firstRequest = new WorkflowPortableSettingsApprovalRequest(PluginId, review.PackageVersion,
                review.LoadedGeneration, review.PackageFingerprint, review.SchemaFingerprint,
                ["style"], 0, "portable-approval-a");
            var first = owner.ApproveWorkflowSettings(state, firstRequest);
            Check(!first.Replayed && first.CurrentlyActive && first.Approval.Revision == 1,
                "first exact owner approval commits revision one");
            var replay = owner.ApproveWorkflowSettings(state, firstRequest);
            Check(replay.Replayed && replay.CurrentlyActive &&
                  replay.Approval.Revision == first.Approval.Revision &&
                  replay.Approval.RequestId == first.Approval.RequestId &&
                  owner.Inspect(PluginId, state).WorkflowSettings?.ApprovalRevision == 1,
                "same request ID and digest return the original receipt without a revision bump");
            Refuse(() => owner.ApproveWorkflowSettings(state,
                firstRequest with { ReviewedFields = ["tone"] }), "changed input with same request ID");
            Refuse(() => owner.ApproveWorkflowSettings(state,
                firstRequest with { RequestId = "portable-approval-stale" }), "new request ID with stale CAS");

            var secondRequest = firstRequest with
            { RequestId = "portable-approval-b", ExpectedRevision = 1, ReviewedFields = ["tone"] };
            var second = owner.ApproveWorkflowSettings(state, secondRequest);
            Check(!second.Replayed && second.Approval.Revision == 2 && second.CurrentlyActive,
                "new reviewed selection requires the current approval revision");
            var superseded = owner.ApproveWorkflowSettings(state, firstRequest);
            Check(superseded.Replayed && !superseded.CurrentlyActive &&
                  superseded.Approval.Revision == 1 && owner.Inspect(PluginId, state).WorkflowSettings?.ApprovalRevision == 2,
                "old exact receipt remains recoverable but cannot claim current approval");

            var thirdRequest = secondRequest with
            { RequestId = "portable-approval-rollback", ExpectedRevision = 2 };
            using (var connection = OpenState(state))
            {
                Execute(connection, """
                    CREATE TRIGGER deny_portable_receipt BEFORE INSERT ON records
                    WHEN NEW.kind = 'service-workflow-settings-approval-receipt' AND NEW.id = 'portable-approval-rollback'
                    BEGIN SELECT RAISE(ABORT, 'fixture receipt insert failure'); END;
                    """);
                Refuse(() => owner.ApproveWorkflowSettings(state, thirdRequest),
                    "receipt insert failure aborts the approval transaction");
                Check(owner.Inspect(PluginId, state).WorkflowSettings?.ApprovalRevision == 2 &&
                      state.Get<WorkflowPortableSettingsApprovalReceipt>(
                          WorkflowPortableSettingsApprovals.ReceiptKind, thirdRequest.RequestId) is null,
                    "failed second write rolls back both approval and receipt");
                Execute(connection, "DROP TRIGGER deny_portable_receipt;");
            }
            var third = owner.ApproveWorkflowSettings(state, thirdRequest);
            Check(third.Approval.Revision == 3 && !third.Replayed,
                "exact request can succeed after the failed transaction is removed");

            Refuse(() => owner.ApproveWorkflowSettings(state, thirdRequest with
            { RequestId = "portable-wrong-generation", ExpectedRevision = 3,
              LoadedGeneration = review.LoadedGeneration + "-wrong" }), "loaded generation drift");
            Refuse(() => owner.ApproveWorkflowSettings(state, thirdRequest with
            { RequestId = "portable-wrong-schema", ExpectedRevision = 3,
              SchemaFingerprint = new string('c', 64) }), "schema fingerprint drift");
            var loadedFacts = loaded.LoadedWorkflowPortableSettingsForPackage(PluginId)
                ?? throw new Exception("FAIL: loaded package facts vanished");
            var managedManifest = Path.Combine(loadedFacts.PackageDirectory, ServicePluginRuntime.ManifestFileName);
            var original = File.ReadAllBytes(managedManifest);
            try
            {
                File.AppendAllText(managedManifest, " ");
                Refuse(() => owner.ApproveWorkflowSettings(state, thirdRequest with
                { RequestId = "portable-changed-bytes", ExpectedRevision = 3 }),
                    "loaded package content drift");
            }
            finally { File.WriteAllBytes(managedManifest, original); }
            Check(owner.Inspect(PluginId, state).WorkflowSettings?.ApprovalRevision == 3,
                "all identity and byte-drift refusals preserve the current approval");
            using (var connection = OpenState(state))
            {
                var approvalBody = Body(connection, WorkflowPortableSettingsApprovals.ApprovalKind, PluginId);
                var receiptBody = Body(connection, WorkflowPortableSettingsApprovals.ReceiptKind, thirdRequest.RequestId);
                Check(!approvalBody.Contains("private-marker", StringComparison.Ordinal) &&
                      !receiptBody.Contains("private-marker", StringComparison.Ordinal) &&
                      !approvalBody.Contains("reviewedValues", StringComparison.Ordinal),
                    "durable approval and receipt store selected names and identity, not profile values");
            }
    }

    private static string PrepareFixture(string root)
    {
        var package = Path.Combine(root, "source", PluginId);
        Directory.CreateDirectory(package);
        var assembly = Path.GetFileName(typeof(PortableSettingsFixturePlugin).Assembly.Location);
        File.Copy(typeof(PortableSettingsFixturePlugin).Assembly.Location, Path.Combine(package, assembly));
        var manifest = new ServicePluginManifest
        {
            SchemaVersion = 2, Id = PluginId, Name = "Portable approval fixture", Version = "1.0.0",
            Assembly = assembly, EntryType = typeof(PortableSettingsFixturePlugin).FullName!,
            PortableSettings = new ServicePluginPortableSettings
            {
                Version = 1,
                Fields =
                [
                    new() { Name = "style", Type = "string", MaximumUtf8Bytes = 64 },
                    new() { Name = "tone", Type = "string", MaximumUtf8Bytes = 64 }
                ]
            }
        };
        var path = Path.Combine(package, ServicePluginRuntime.ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
        return path;
    }

    private static SqliteConnection OpenState(StateStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Body(SqliteConnection connection, string kind, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? throw new Exception("FAIL: durable approval row missing");
    }

    private static void Refuse(Action action, string label)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or SqliteException)
        {
            if (error.Message.Contains("private-marker", StringComparison.Ordinal))
                throw new Exception("FAIL: approval refusal exposed a profile value");
            return;
        }
        throw new Exception("FAIL: expected refusal for " + label);
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new Exception("FAIL: " + label);
    }
}

/// <summary>Collectible fixture tests an actual loaded portable-settings facet without browser effects.</summary>
public sealed class PortableSettingsFixturePlugin : IServicePlugin, IServiceWorkflowPortableSettingsValidator
{
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public int WorkflowPortableSettingsValidatorVersion => ServiceWorkflowPortableSettingsValidatorContract.Version;
    public void ValidateWorkflowPortableSettings(JsonElement approvedProjectedValues)
    {
        if (approvedProjectedValues.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Approved fixture values must be an object.");
    }
    public void Initialize(IServicePluginHost host) { }
    public void Dispose() { }
}
