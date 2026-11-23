using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using static Cerebral.Worker.SourceRecoveryManifest;

namespace Cerebral.Worker.Tests;

/// <summary>Private CPU/SQLite fixtures. No product host, provider, model, or Source endpoint is invoked.</summary>
public static class SourceRecoveryPreflightTests
{
    private static readonly string[] Columns = ["id", "request_id", "conversation_id", "profile_id", "status", "revision", "submission", "payload"];
    // Generated independently by Python json.dumps(ensure_ascii=True,separators=(",",":")).
    private static readonly string[] PythonRows =
    [
        "973e56d331959d04f11af56c888f4bda6a9675118a954eba4362ec9dbbcdce2c", "c22a2af8c4bda26487adb8bbfee98349918a783d9409fb493a8e1db6d425fd70",
        "428f755077a87d14663bc7e9063248605eaa050cc16ef9236664a3a7b1a1cd14", "1deae135a2b531ce9ec25c9e84f6818cc8eac484ea9d9ca81d9f11b56ac73fcc",
        "5767e4a03a32d1b91ad068ea3376325e7b399e6572bd9dfe5703fde1c4d5646b", "d0cc397e835761a6fd5b54374f633a65cc7a2a626fe2bf12f9369f62f8b642a5",
        "cc82807506b16845f919e271f9757258d988fbf9eb9adbd68f8046a881163444", "ddb56a0072bd416fc37a9bed94fe8d1f1c5cf8892978847a9ca69702cfd94756",
        "19165c55bf2f0c49dc78ee7f30801739f27c50dc3dba126d32936c3f12dfa0a7", "d9cb0fcfef358df5a6f042e771415b26b8530162298bcf7da95f237ed77d907e",
        "0c8d50bcc2e5b841f823a03910ea50ebdd129152b04627635bb70213ed73ebe3", "a53abead9eeb95ebb9c547f9d67904799cf64beb7e6d5a1f62be3395ced4ec92",
        "a13e56b77046a099923e7f9594ff25165cef0547a8353f29e9e9eccdccb0b99c", "93e22dc5dd41b46a61649ebe0efe2113bcf2effcea8756894f6afdec3deb9415"
    ];

    public static int CheckSourceRecoveryPreflightFixtures(string privateFixtureRoot)
    {
        privateFixtureRoot = CanonicalPath(privateFixtureRoot);
        if (!Directory.Exists(privateFixtureRoot)) Directory.CreateDirectory(privateFixtureRoot);
        CheckExistingPath(privateFixtureRoot, true);
        var count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("source_recovery_fixture_failed"); count++; }
        void Refuse(Action action, SourceRecoveryFailure? expected = null)
        {
            try { action(); throw new InvalidOperationException("source_recovery_fixture_missing_refusal"); }
            catch (SourceRecoveryException error)
            {
                Check(error.Message == error.Code && Enum.TryParse<SourceRecoveryFailure>(error.Code, out _) &&
                    (expected is null || error.Code == expected.ToString()));
            }
        }
        Fixture Fresh() => new(privateFixtureRoot);
        void Verify(Fixture fixture) => _ = SourceRecoveryPreflight.Verify(fixture.Parse(), CancellationToken.None);

        var tricky = SourceRecoveryPreflight.EncodePythonTriplets(
            ["ascii", "unicode", "control", "blob", "null", "integer"],
            ["<>/&\"\\", "café ∆ 😀 \u007f", "\0\b\f\n\r\t\u001f",
                Enumerable.Range(0, 256).Select(x => (byte)x).ToArray(), DBNull.Value, long.MinValue]);
        Check(Digest(tricky) == "ff6445db812411cab92448ca3fae18b37a87642ad8bf70d8bb68d10ba8fedb56");
        for (var i = 0; i < 14; i++) Check(Digest(SourceRecoveryPreflight.EncodePythonTriplets(Columns, Values(i))) == PythonRows[i]);
        Refuse(() => SourceRecoveryPreflight.EncodePythonTriplets(["float"], [1.25]), SourceRecoveryFailure.row_storage_invalid);
        Refuse(() => SourceRecoveryPreflight.EncodePythonTriplets(["x"], [new string('é', 2 * 1024 * 1024)]), SourceRecoveryFailure.row_bound);

        var valid = Fresh(); var manifest = valid.Parse();
        var before = valid.DataDigests();
        var result = SourceRecoveryPreflight.Verify(manifest, CancellationToken.None);
        Check(result.Fingerprints.BaselineMatched && result.Fingerprints.Rows.Count == 14);
        Check(!result.HealthVerified && result.InitializerReview == "deferred");
        Check(result.Settings.AccessKey == Fixture.Secret && result.Settings.InstallationId == Fixture.Installation);
        Check(result.Coordinates.HoldGeneration == 1 && result.Coordinates.HoldRevision == 2 && result.Coordinates.EngineEpoch == 5);
        Check(result.Coordinates.EnginePaused && result.Coordinates.HoldState == "holding");
        Check(before.SequenceEqual(valid.DataDigests()));
        Check(!JsonSerializer.Serialize(result).Contains(Fixture.Secret, StringComparison.Ordinal));
        Check(!result.ToString().Contains(Fixture.Secret, StringComparison.Ordinal));
        Check(!manifest.ToString().Contains(Fixture.Secret, StringComparison.Ordinal));
        Check(result.Fingerprints.ReadScope.Contains("immutable", StringComparison.Ordinal));
        Check(SourceRecoveryPreflight.VerifyRows(manifest, CancellationToken.None).Rows.Count == 14);
        Check(SourceRecoveryPreflight.VerifyPersistedState(manifest, SourceRecoveryExpectedState.Held, CancellationToken.None)
            .Coordinates == result.Coordinates);
        Refuse(() => manifest.ValidateStartup(valid.Args, valid.Root, new(manifest.PipeName, manifest.PipeInstanceId),
            Path.Combine(valid.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.baseline_invalid);
        var directorySdkArgs = valid.Args; directorySdkArgs[^1] = valid.RoleRoot("sdk");
        Refuse(() => manifest.ValidateStartup(directorySdkArgs, valid.Root, new(manifest.PipeName, manifest.PipeInstanceId),
            Path.Combine(valid.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.startup_invalid);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { VerifyCancelled(); throw new InvalidOperationException("source_recovery_cancellation_missing"); }
            catch (OperationCanceledException) { count++; }
            void VerifyCancelled() => _ = SourceRecoveryPreflight.Verify(manifest, cancelled.Token);
        }

        foreach (var mutation in new Action<JsonObject>[]
        {
            x => x["unexpected"] = "private text",
            x => x["schema"] = "cerebral.source-recovery/2",
            x => x["sourceRoot"] = @"C:\fixture\..\escape",
            x => x["sourceRoot"] = @"\\foreign\share\Source",
            x => x["ownerSid"] = "S-1-5-private",
            x => x["installationId"] = new string('0', 32),
            x => x["pipeInstanceId"] = "not-a-guid",
            x => x["pipeName"] = "forbidden/pipe",
            x => x["settingsRevision"] = -1,
            x => x["settingsSha256"] = new string('A', 64),
            x => x["roots"] = new JsonArray(),
            x => x["files"] = null,
            x => x["passiveInitializerReview"]!["state"] = "healthy",
            x => x["passiveInitializerReview"]!["evidencePath"] = "unexpected"
        })
        {
            var fixture = Fresh(); fixture.ChangeManifest(mutation);
            Refuse(() => fixture.Parse());
        }
        var duplicate = Fresh();
        File.WriteAllText(duplicate.ManifestPath, File.ReadAllText(duplicate.ManifestPath).Replace(
            "\"schema\":", "\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal), Encoding.UTF8);
        Refuse(() => duplicate.Parse(), SourceRecoveryFailure.manifest_invalid);
        var changedManifest = Fresh(); var captured = changedManifest.Parse();
        File.AppendAllText(changedManifest.ManifestPath, " ");
        Refuse(() => SourceRecoveryPreflight.Verify(captured, CancellationToken.None), SourceRecoveryFailure.manifest_changed);
        var wrongOwner = Fresh(); wrongOwner.ChangeManifest(x => x["ownerSid"] = "S-1-5-21-1-2-3-9999");
        Refuse(() => Verify(wrongOwner), SourceRecoveryFailure.owner_mismatch);
        var traversal = Fresh(); traversal.ChangeManifest(x => x["files"]![0]!["relativePath"] = "../escape.dll");
        Refuse(() => traversal.Parse(), SourceRecoveryFailure.pin_invalid);
        var duplicatePin = Fresh(); duplicatePin.ChangeManifest(x => ((JsonArray)x["files"]!).Add(x["files"]![0]!.DeepClone()));
        Refuse(() => duplicatePin.Parse(), SourceRecoveryFailure.pin_invalid);

        foreach (var option in new[] { "--url", "--installed-pipe", "--local-run" })
        {
            var args = valid.Args.Concat([option]).ToArray();
            Refuse(() => manifest.ValidateStartup(args, valid.Root, new(manifest.PipeName, manifest.PipeInstanceId),
                Path.Combine(valid.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.startup_invalid);
        }
        Refuse(() => manifest.ValidateStartup(valid.Args, valid.Root, new(manifest.PipeName, Guid.NewGuid().ToString("N")),
            Path.Combine(valid.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.startup_invalid);
        Refuse(() => manifest.ValidateStartup(valid.Args, valid.RoleRoot("worker"), new(manifest.PipeName, manifest.PipeInstanceId),
            Path.Combine(valid.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.startup_invalid);
        var missingFourthSdk = Fresh();
        missingFourthSdk.ChangeManifest(x =>
        {
            var files = (JsonArray)x["files"]!;
            files.Remove(files.Single(y => y!["relativePath"]!.GetValue<string>() == "Cerebral.Desktop.Plugins.dll"));
        });
        var missingSdkManifest = missingFourthSdk.Parse();
        Refuse(() => missingSdkManifest.ValidateStartup(missingFourthSdk.Args, missingFourthSdk.Root,
            new(missingSdkManifest.PipeName, missingSdkManifest.PipeInstanceId),
            Path.Combine(missingFourthSdk.RoleRoot("worker"), "Cerebral.Worker.exe")), SourceRecoveryFailure.pin_invalid);

        foreach (var replacement in new[] { ("protocol=pegasus.cerebral.engine/1", "protocol=other"),
            ("assembly=engine.dll", "assembly=../escape.dll"), ("assembly-sha256=", "unknown-sha256=") })
        {
            var fixture = Fresh(); fixture.ChangeEngineManifest(replacement.Item1, replacement.Item2);
            Refuse(() => Verify(fixture), SourceRecoveryFailure.pin_invalid);
        }
        var reviewed = Fresh();
        var reviewPath = Path.Combine(reviewed.Root, "review-evidence.txt");
        File.WriteAllText(reviewPath, "private fixture review evidence", new UTF8Encoding(false));
        reviewed.ChangeManifest(x => x["passiveInitializerReview"] = new JsonObject
        {
            ["state"] = "reviewed", ["evidencePath"] = reviewPath, ["evidenceSha256"] = Digest(File.ReadAllBytes(reviewPath)),
            ["packagePins"] = new JsonArray(((JsonArray)x["files"]!).Where(y =>
                y!["role"]!.GetValue<string>() is "engine" or "service").Select(y => y!.DeepClone()).ToArray())
        });
        var reviewedResult = SourceRecoveryPreflight.Verify(reviewed.Parse(), CancellationToken.None);
        Check(reviewedResult.InitializerReview == "reviewed" && !reviewedResult.HealthVerified);
        File.AppendAllText(reviewPath, " changed");
        Refuse(() => Verify(reviewed), SourceRecoveryFailure.initializer_review_invalid);
        reviewed.ChangeManifest(x => ((JsonArray)x["passiveInitializerReview"]!["packagePins"]!).RemoveAt(0));
        Refuse(() => reviewed.Parse(), SourceRecoveryFailure.initializer_review_invalid);

        var largeAscii = Fresh(); var payload = new string('a', 2 * 1024 * 1024);
        using (var connection = largeAscii.Open(largeAscii.EnginePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE workflow_runs SET payload=$payload WHERE id='fixture-00'";
            command.Parameters.AddWithValue("$payload", payload); command.ExecuteNonQuery();
        }
        var largeValues = Values(0); largeValues[7] = payload;
        var largeHash = Digest(SourceRecoveryPreflight.EncodePythonTriplets(Columns, largeValues));
        largeAscii.ChangeBaseline(x => x["storageRowFingerprints"]![0]!["sha256"] = largeHash);
        Check(SourceRecoveryPreflight.VerifyRows(largeAscii.Parse(), CancellationToken.None).BaselineMatched);

        var wrongPin = Fresh(); File.WriteAllText(Path.Combine(wrongPin.RoleRoot("worker"), "Cerebral.Worker.dll"), "altered", Encoding.UTF8);
        Refuse(() => Verify(wrongPin), SourceRecoveryFailure.pin_changed);
        var extraPin = Fresh(); File.WriteAllText(Path.Combine(extraPin.RoleRoot("runtime"), "unlisted.dll"), "unlisted", Encoding.UTF8);
        Refuse(() => Verify(extraPin), SourceRecoveryFailure.pin_inventory_changed);
        var missingPin = Fresh(); File.Delete(Path.Combine(missingPin.RoleRoot("sdk"), "Cerebral.Core.dll"));
        Refuse(() => Verify(missingPin), SourceRecoveryFailure.pin_inventory_changed);
        var nestedExtra = Fresh(); Directory.CreateDirectory(Path.Combine(nestedExtra.RoleRoot("service"), "extra"));
        File.WriteAllText(Path.Combine(nestedExtra.RoleRoot("service"), "extra", "unknown.dll"), "unexpected", Encoding.UTF8);
        Refuse(() => Verify(nestedExtra), SourceRecoveryFailure.pin_inventory_changed);
        var changedBaseline = Fresh(); File.AppendAllText(changedBaseline.BaselinePath, " ");
        Refuse(() => Verify(changedBaseline), SourceRecoveryFailure.baseline_changed);
        var wrongBaselineOwner = Fresh(); wrongBaselineOwner.ChangeBaseline(x => x["projection"]!["settings"]!["installation_id"] = Guid.NewGuid().ToString("N"));
        Refuse(() => Verify(wrongBaselineOwner), SourceRecoveryFailure.baseline_invalid);
        var wrongBaselineRoot = Fresh(); wrongBaselineRoot.ChangeBaseline(x => x["projection"]!["source_root"] = wrongBaselineRoot.RoleRoot("worker"));
        Refuse(() => Verify(wrongBaselineRoot), SourceRecoveryFailure.baseline_invalid);
        var wrongBaselineCount = Fresh(); wrongBaselineCount.ChangeBaseline(x => ((JsonArray)x["storageRowFingerprints"]!).RemoveAt(0));
        Refuse(() => Verify(wrongBaselineCount));

        foreach (var mutation in new Action<JsonObject>[]
        {
            x => x["version"] = 2, x => x["holdId"] = Guid.NewGuid().ToString("N"),
            x => x["requestId"] = Guid.NewGuid().ToString("N"), x => x["generation"] = 2,
            x => x["revision"] = 3, x => x["requestedEpoch"] = 6, x => x["priorGeneration"] = 1,
            x => x["priorRevision"] = 1, x => x["mode"] = "stop-now", x => x["state"] = "faulted",
            x => x["pauseCommitted"] = false, x => ((JsonArray)x["work"]!).Add(new JsonObject { ["id"] = "unreviewed" }),
            x => x["fault"] = "private fault text", x => x["extra"] = true
        })
        {
            var fixture = Fresh(); fixture.ChangeHold(mutation, repin: true); Refuse(() => Verify(fixture));
        }
        var holdTampered = Fresh(); holdTampered.ChangeHold(x => x["revision"] = 3, repin: false);
        Refuse(() => Verify(holdTampered), SourceRecoveryFailure.hold_mismatch);
        foreach (var pair in new[] { ("epoch","6"),("slot","B"),("known-slot","B"),("pending","True"),
            ("paused","False"),("package",new string('a',64)),("configuration",new string('b',64)),
            ("known-package",new string('c',64)),("known-configuration",new string('d',64)),("schema","2") })
        {
            var fixture = Fresh(); fixture.ChangeSelection(pair.Item1, pair.Item2, repin: true);
            Refuse(() => Verify(fixture), SourceRecoveryFailure.selection_mismatch);
        }
        foreach (var mutation in new Action<JsonObject>[]
        {
            x => x["installationId"] = Guid.NewGuid().ToString("N"), x => x["revision"] = 20,
            x => x["coordinator"] = false, x => x["allowLan"] = true, x => x["standaloneWorker"] = true,
            x => x["backgroundTasksEnabled"] = true, x => x["managementControllerId"] = "other-controller",
            x => x.Remove("allowLan"), x => x["llamaServerPath"] = @"C:\unreviewed\llama-server.exe",
            x => x["unexpected"] = "private setting", x => x["workers"] = null
        })
        {
            var fixture = Fresh(); fixture.ChangeSettings(mutation, repin: true); Refuse(() => Verify(fixture));
        }
        var settingsTampered = Fresh(); settingsTampered.ChangeSettings(x => x["name"] = "altered", repin: false);
        Refuse(() => Verify(settingsTampered), SourceRecoveryFailure.settings_invalid);

        foreach (var sql in new[]
        {
            "UPDATE workflow_runs SET revision=revision+1 WHERE id='fixture-00'",
            "UPDATE workflow_runs SET payload='private changed body' WHERE id='fixture-01'",
            "UPDATE workflow_runs SET submission='private changed submission' WHERE id='fixture-02'",
            "UPDATE workflow_runs SET request_id='different-request' WHERE id='fixture-03'",
            "UPDATE workflow_runs SET status='Running' WHERE id='fixture-04'",
            "DELETE FROM workflow_runs WHERE id='fixture-05'",
            "INSERT INTO workflow_runs SELECT 'extra',request_id||'-extra',conversation_id,profile_id,status,revision,submission,payload FROM workflow_runs LIMIT 1",
            "ALTER TABLE workflow_runs ADD COLUMN unreviewed TEXT",
            "UPDATE workflow_runs SET revision=1.25 WHERE id='fixture-06'",
            "UPDATE workflow_runs SET status=NULL WHERE id='fixture-07'",
            "UPDATE workflow_runs SET payload=zeroblob(2097152) WHERE id='fixture-08'"
        })
        {
            var fixture = Fresh(); fixture.Sql(fixture.EnginePath, sql); Refuse(() => Verify(fixture));
        }
        var legacy = Fresh(); legacy.Sql(legacy.MainPath, "INSERT INTO records VALUES('job','unknown','{\"status\":\"remote-unconfirmed\"}')");
        Refuse(() => Verify(legacy), SourceRecoveryFailure.hold_mismatch);
        var malformedLegacy = Fresh(); malformedLegacy.Sql(malformedLegacy.MainPath, "INSERT INTO records VALUES('job','unknown','private malformed')");
        Refuse(() => Verify(malformedLegacy));

        var missingShm = Fresh(); File.WriteAllBytes(missingShm.MainPath + "-wal", [1, 2, 3]);
        Refuse(() => Verify(missingShm), SourceRecoveryFailure.store_sidecars_invalid);
        var directoryJournal = Fresh(); Directory.CreateDirectory(directoryJournal.EnginePath + "-journal");
        Refuse(() => Verify(directoryJournal), SourceRecoveryFailure.store_sidecars_invalid);
        var directoryWal = Fresh(); Directory.CreateDirectory(directoryWal.MainPath + "-wal");
        Directory.CreateDirectory(directoryWal.MainPath + "-shm"); Refuse(() => Verify(directoryWal));
        var rollback = Fresh(); File.WriteAllBytes(rollback.EnginePath + "-journal", [1]);
        Refuse(() => Verify(rollback), SourceRecoveryFailure.store_sidecars_invalid);
        var resumed = Fresh(); resumed.ChangeHold(x => { x["state"] = "resumed"; x["revision"] = 4; }, repin: false);
        resumed.ChangeSelection("paused", "False", repin: false);
        var resumedManifest = resumed.Parse();
        var resumedState = SourceRecoveryPreflight.VerifyPersistedState(resumedManifest, SourceRecoveryExpectedState.Resumed, CancellationToken.None);
        Check(resumedState.Coordinates.HoldRevision == 4 && !resumedState.Coordinates.EnginePaused &&
            resumedState.Coordinates.HoldState == "resumed");
        Refuse(() => SourceRecoveryPreflight.VerifyPersistedState(resumedManifest, SourceRecoveryExpectedState.Held, CancellationToken.None));
        Refuse(() => SourceRecoveryPreflight.VerifyPersistedState(resumedManifest, (SourceRecoveryExpectedState)99, CancellationToken.None),
            SourceRecoveryFailure.hold_mismatch);
        Check(SourceRecoveryPreflight.VerifyRows(resumedManifest, CancellationToken.None).BaselineMatched);

        var wal = Fresh();
        using (var writer = wal.Open(wal.EnginePath))
        {
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO workflow_runs VALUES('terminal','terminal-request','c','p','Completed',1,'{}','{}')";
            command.ExecuteNonQuery();
            var walBefore = wal.DataDigests();
            Check(File.Exists(wal.EnginePath + "-wal") && File.Exists(wal.EnginePath + "-shm"));
            Check(SourceRecoveryPreflight.VerifyRows(wal.Parse(), CancellationToken.None).BaselineMatched);
            Check(walBefore.SequenceEqual(wal.DataDigests()));
            Refuse(() => Verify(wal), SourceRecoveryFailure.store_sidecars_invalid);
            command.CommandText = "UPDATE workflow_runs SET payload='committed-WAL private change' WHERE id='fixture-00'";
            command.ExecuteNonQuery();
            Refuse(() => SourceRecoveryPreflight.VerifyRows(wal.Parse(), CancellationToken.None), SourceRecoveryFailure.rows_mismatch);
        }
        return count;
    }

    private static object[] Values(int index) => [$"fixture-{index:00}", $"request-{index:00}", $"conversation-{index:00}",
        "profile-fixture", "Waiting", (long)index + 1, "{\"a\":\"é 😀\"}", $"{{\"opaque\":\"private fixture {index:00}\"}}"];

    internal sealed class Fixture
    {
        internal const string Installation = "11111111111111111111111111111111";
        internal const string Secret = "private-fixture-credential-do-not-project";
        internal string Root { get; }
        internal string ManifestPath { get; }
        internal string BaselinePath { get; }
        internal string MainPath => Path.Combine(Root, "cerebral.db");
        internal string EnginePath => Path.Combine(Root, "engine.db");
        private readonly Dictionary<string, string> roots = new(StringComparer.Ordinal);
        private readonly JsonObject manifest;
        private JsonObject settings;
        private JsonObject hold;
        private readonly string package;
        private readonly string configuration = Digest(Encoding.UTF8.GetBytes("engine configuration fixture"));
        internal string[] Args => ["--local-run", "--source-recovery-manifest", ManifestPath, "--data", Root,
            "--pipe-name", manifest["pipeName"]!.GetValue<string>(), "--pipe-instance", manifest["pipeInstanceId"]!.GetValue<string>(),
            "--engines", RoleRoot("engine"), "--service-plugins", RoleRoot("service"), "--desktop-sdk", Path.Combine(RoleRoot("sdk"), "Cerebral.Desktop.Plugins.dll")];

        internal Fixture(string privateRoot)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows owner fixtures required.");
            Root = Path.Combine(privateRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            ManifestPath = Path.Combine(Root, "recovery.json"); BaselinePath = Path.Combine(Root, "baseline.json");
            Directory.CreateDirectory(Path.Combine(Root, "Engine"));
            foreach (var role in Roles) { roots.Add(role, Path.Combine(Root, role + "-pins")); Directory.CreateDirectory(roots[role]); }
            foreach (var name in new[] { "Cerebral.Worker.exe", "Cerebral.Worker.dll", "Cerebral.Worker.deps.json", "Cerebral.Worker.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(RoleRoot("worker"), name), "fixture " + name, Encoding.UTF8);
            foreach (var name in new[] { "Cerebral.Core.dll", "Cerebral.Contracts.dll", "Cerebral.Engine.Contracts.dll", "Cerebral.Desktop.Plugins.dll" })
                File.WriteAllText(Path.Combine(RoleRoot("sdk"), name), "fixture " + name, Encoding.UTF8);
            File.WriteAllText(Path.Combine(RoleRoot("runtime"), "llama-server.exe"), "runtime fixture", Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(RoleRoot("engine"), "A"));
            File.WriteAllText(Path.Combine(RoleRoot("engine"), "A", "engine.dll"), "engine package fixture", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(RoleRoot("engine"), "A", "engine-settings.ini"), "engine configuration fixture", new UTF8Encoding(false));
            var engineManifest = "[engine]\nid=fixture.engine\nversion=1.0.0\nprotocol=pegasus.cerebral.engine/1\nassembly=engine.dll\nassembly-sha256=" +
                Digest(Encoding.UTF8.GetBytes("engine package fixture")) + "\nentry-type=Fixture.Engine\nconfiguration-sha256=" + configuration + "\n";
            File.WriteAllText(Path.Combine(RoleRoot("engine"), "A", "engine.ini"), engineManifest, new UTF8Encoding(false));
            package = Digest(Encoding.UTF8.GetBytes(engineManifest));
            File.WriteAllText(Path.Combine(RoleRoot("service"), "service-plugin.json"), "service fixture", Encoding.UTF8);
            settings = JsonSerializer.SerializeToNode(new HostSettings
            {
                Revision = 19, InstallationId = Installation, Name = "private fixture",
                Coordinator = true, AllowLan = false, StandaloneWorker = false, BackgroundTasksEnabled = false,
                AccessKey = Secret, ModelDirectory = Root, ProjectsDirectory = Root, SharedDirectory = Root,
                ToolsDirectory = Root, LlamaServerPath = Path.Combine(RoleRoot("runtime"), "llama-server.exe")
            }, CerebralJson.Options)!.AsObject();
            hold = new JsonObject
            {
                ["version"] = 1, ["holdId"] = HoldId, ["requestId"] = RequestId,
                ["generation"] = 1, ["revision"] = 2, ["requestedEpoch"] = 5,
                ["priorGeneration"] = 0, ["priorRevision"] = 0, ["mode"] = "finish-current",
                ["state"] = "holding", ["pauseCommitted"] = true, ["work"] = new JsonArray(), ["fault"] = null
            };
            Sql(MainPath, "CREATE TABLE records(kind TEXT,id TEXT,body TEXT,PRIMARY KEY(kind,id))");
            SaveRecord("settings", "host", settings); SaveRecord("owner-work-hold", "intentional", hold);
            Sql(EnginePath, """
                PRAGMA application_id=1346585942; PRAGMA user_version=7;
                CREATE TABLE workflow_runs(id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,
                    conversation_id TEXT NOT NULL,profile_id TEXT NOT NULL,status TEXT,revision INTEGER,
                    submission TEXT NOT NULL,payload TEXT NOT NULL)
                """);
            using (var connection = Open(EnginePath))
                for (var i = 0; i < 14; i++)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO workflow_runs VALUES($a,$b,$c,$d,$e,$f,$g,$h)";
                    var values = Values(i);
                    for (var c = 0; c < 8; c++) command.Parameters.AddWithValue("$" + (char)('a' + c), values[c]);
                    command.ExecuteNonQuery();
                }
            var rows = new JsonArray();
            for (var i = 0; i < 14; i++) rows.Add(new JsonObject { ["id"] = $"fixture-{i:00}", ["status"] = "Waiting",
                ["revision"] = i + 1, ["sha256"] = PythonRows[i] });
            var baseline = new JsonObject
            {
                ["schema"] = "cerebral.core.source-retained-state/1", ["fingerprintCodec"] = FingerprintCodec,
                ["projection"] = new JsonObject { ["source_root"] = Root,
                    ["settings"] = new JsonObject { ["installation_id"] = Installation } },
                ["storageRowFingerprints"] = rows
            };
            File.WriteAllText(BaselinePath, baseline.ToJsonString(), new UTF8Encoding(false));
            WriteSelection(true);
            var filePins = new JsonArray();
            foreach (var root in roots)
                foreach (var file in Directory.GetFiles(root.Value, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    filePins.Add(new JsonObject { ["role"] = root.Key, ["relativePath"] = Path.GetRelativePath(root.Value, file).Replace('\\', '/'),
                        ["length"] = new FileInfo(file).Length, ["sha256"] = Digest(File.ReadAllBytes(file)) });
            using var identity = WindowsIdentity.GetCurrent();
            var rootNodes = new JsonArray();
            foreach (var root in roots) rootNodes.Add(new JsonObject { ["role"] = root.Key, ["path"] = root.Value });
            manifest = new JsonObject
            {
                ["schema"] = Schema, ["sourceRoot"] = Root, ["ownerSid"] = identity.User!.Value,
                ["installationId"] = Installation, ["pipeName"] = "private-fixture-" + Guid.NewGuid().ToString("N"),
                ["pipeInstanceId"] = Guid.NewGuid().ToString("N"), ["settingsRevision"] = 19,
                ["settingsSha256"] = Digest(Encoding.UTF8.GetBytes(settings.ToJsonString())),
                ["holdSha256"] = Digest(Encoding.UTF8.GetBytes(hold.ToJsonString())),
                ["selectionSha256"] = Digest(File.ReadAllBytes(Path.Combine(Root, "Engine", "selection.ini"))),
                ["selectedPackageSha256"] = package, ["selectedConfigurationSha256"] = configuration,
                ["baselinePath"] = BaselinePath, ["baselineSha256"] = Digest(File.ReadAllBytes(BaselinePath)),
                ["roots"] = rootNodes, ["files"] = filePins, ["passiveInitializerReview"] = new JsonObject
                { ["state"] = "deferred", ["evidencePath"] = null, ["evidenceSha256"] = null, ["packagePins"] = new JsonArray() }
            };
            SaveManifest();
        }

        internal string RoleRoot(string role) => roots[role];
        internal SourceRecoveryManifest Parse() => SourceRecoveryManifest.ParseFile(ManifestPath);
        internal void ChangeManifest(Action<JsonObject> change) { change(manifest); SaveManifest(); }
        internal void ChangeBaseline(Action<JsonObject> change)
        {
            var baseline = JsonNode.Parse(File.ReadAllText(BaselinePath))!.AsObject(); change(baseline);
            File.WriteAllText(BaselinePath, baseline.ToJsonString(), new UTF8Encoding(false));
            manifest["baselineSha256"] = Digest(File.ReadAllBytes(BaselinePath)); SaveManifest();
        }
        internal void ChangeEngineManifest(string oldValue, string newValue)
        {
            var path = Path.Combine(RoleRoot("engine"), "A", "engine.ini");
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue, StringComparison.Ordinal), new UTF8Encoding(false));
            var hash = Digest(File.ReadAllBytes(path));
            var pin = ((JsonArray)manifest["files"]!).Single(x => x!["role"]!.GetValue<string>() == "engine" &&
                x["relativePath"]!.GetValue<string>() == "A/engine.ini")!;
            pin["length"] = new FileInfo(path).Length; pin["sha256"] = hash;
            manifest["selectedPackageSha256"] = hash;
            var selection = Path.Combine(Root, "Engine", "selection.ini");
            File.WriteAllText(selection, File.ReadAllText(selection).Replace("package=" + package, "package=" + hash, StringComparison.Ordinal), new UTF8Encoding(false));
            manifest["selectionSha256"] = Digest(File.ReadAllBytes(selection)); SaveManifest();
        }

        internal void ChangeHold(Action<JsonObject> change, bool repin)
        {
            change(hold); SaveRecord("owner-work-hold", "intentional", hold);
            if (repin) { manifest["holdSha256"] = Digest(Encoding.UTF8.GetBytes(hold.ToJsonString())); SaveManifest(); }
        }
        internal void ChangeSettings(Action<JsonObject> change, bool repin)
        {
            change(settings); SaveRecord("settings", "host", settings);
            if (repin) { manifest["settingsSha256"] = Digest(Encoding.UTF8.GetBytes(settings.ToJsonString())); SaveManifest(); }
        }
        internal void ChangeSelection(string key, string value, bool repin)
        {
            var path = Path.Combine(Root, "Engine", "selection.ini");
            var lines = File.ReadAllLines(path).Select(x => x.StartsWith(key + "=", StringComparison.Ordinal) ? key + "=" + value : x);
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            if (repin) { manifest["selectionSha256"] = Digest(File.ReadAllBytes(path)); SaveManifest(); }
        }
        private void WriteSelection(bool paused) => File.WriteAllText(Path.Combine(Root, "Engine", "selection.ini"),
            "[selection]\nschema=1\nepoch=5\nslot=A\npackage=" + package + "\nconfiguration=" + configuration +
            "\nknown-slot=A\nknown-package=" + package + "\nknown-configuration=" + configuration +
            "\npending=False\npaused=" + paused + "\n", new UTF8Encoding(false));
        private void SaveManifest() => File.WriteAllText(ManifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
        private void SaveRecord(string kind, string id, JsonObject value)
        {
            using var connection = Open(MainPath); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO records VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$body", value.ToJsonString()); command.ExecuteNonQuery();
        }
        internal SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        internal void Sql(string path, string sql)
        { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal string[] DataDigests() => new[] { MainPath, EnginePath, MainPath + "-wal", EnginePath + "-wal",
            Path.Combine(Root, "Engine", "selection.ini") }.Where(File.Exists).Select(x => Path.GetFileName(x) + ":" + Digest(ReadShared(x))).ToArray();
        private static byte[] ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }
    }
}
