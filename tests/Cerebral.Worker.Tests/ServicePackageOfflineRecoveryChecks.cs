using System.Security.Cryptography;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static class ServicePackageOfflineRecoveryChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CerebralOfflineRecoveryTests-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        try
        {
            var data = Path.Combine(root, "data");
            var repository = Path.Combine(root, "repository");
            var preserved = Path.Combine(root, "preserved");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(preserved);
            Directory.CreateDirectory(Path.Combine(repository, ".lifecycle"));
            Directory.CreateDirectory(Path.Combine(data, "Engine"));
            var holdId = Guid.NewGuid().ToString("N");
            var holdRequestId = Guid.NewGuid().ToString("N");
            var jobId = Guid.NewGuid().ToString("N");
            var workflowId = Guid.NewGuid().ToString("N");
            var conversationId = Guid.NewGuid().ToString("N");
            var store = new StateStore(data);
            store.Put("settings", "host", new { installationId = "fixture-install" });
            store.Put("job", jobId, new { status = "completed", sessionId = conversationId });
            store.Put("owner-work-hold", "intentional", new IntentionalWorkHoldRecord
            {
                HoldId = holdId, RequestId = holdRequestId, Generation = 1, Revision = 2,
                RequestedEpoch = 5, Mode = "finish-current", State = "holding",
                PauseCommitted = true, Work =
                [
                    new IntentionalHeldWork("job", jobId, conversationId, false),
                    new IntentionalHeldWork("workflow", workflowId, conversationId, false)
                ]
            });
            _ = new EventJournal(Path.Combine(data, "engine.db"));
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "engine.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO workflow_runs(id,request_id,conversation_id,profile_id,status,revision,submission,payload)
                    VALUES($id,$request,$conversation,'fixture-profile','Completed',1,'{}',
                        '{"effect":{"state":"Succeeded"}}')
                    """;
                command.Parameters.AddWithValue("$id", workflowId);
                command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$conversation", conversationId);
                command.ExecuteNonQuery();
            }
            var manifest = """
                {"schemaVersion":1,"contract":"pegasus.cerebral.service","apiVersion":1,
                "id":"pegasus.conversation","name":"Fixture Conversation","version":"1.0.9",
                "description":"Disposable offline recovery fixture",
                "assembly":"fixture.dll","entryType":"Fixture.Conversation"}
                """;
            File.WriteAllText(Path.Combine(preserved, "service-plugin.json"), manifest);
            File.WriteAllText(Path.Combine(preserved, "fixture.dll"), "fixture-assembly");
            for (var i = 0; i < 6; i++)
                File.WriteAllText(Path.Combine(preserved, "asset-" + i + ".txt"), "asset-" + i);
            var pins = Directory.EnumerateFiles(preserved).Select(path => new OfflineRecoveryFilePin(
                Path.GetFileName(path), new FileInfo(path).Length, Hash(path)))
                .OrderBy(pin => pin.RelativePath, StringComparer.Ordinal).ToArray();
            var fingerprint = ServicePackageLifecycle.FingerprintPackage(preserved);
            var statePath = Path.Combine(repository, ".lifecycle", "pegasus.conversation.json");
            void State(long revision, string version, string generation) =>
                File.WriteAllText(statePath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, id = "pegasus.conversation", revision, desiredActive = true,
                    desiredVersion = version, desiredGeneration = generation,
                    stagedVersion = (string?)null, stagedGeneration = (string?)null
                }, CerebralJson.Options));
            State(18, "1.1.0", "1.1.0-broken");
            // The current selected generation intentionally has no directory. A recovery that
            // insists on its bytes would be unable to repair this real failure mode.
            var requestId = Guid.NewGuid().ToString("N");
            var request = new ServicePackageOfflineRecoveryRequest(
                "cerebral.service-package-offline-recovery/1", requestId, data, repository,
                "fixture-install", "pegasus.conversation", 18, Hash(statePath),
                "1.1.0", "1.1.0-broken", "1.0.9", preserved, fingerprint, pins,
                new(holdId, holdRequestId, 1, 2, 5),
                new(999999, 1, Path.Combine(root, "stopped.exe"), "fixture-owner", 1,
                    "fixture.pipe", Guid.NewGuid().ToString("N")),
                new(Path.Combine(root, "maintenance.exe"), new string('a', 64),
                    new string('b', 64), "fixture-owner", 1));
            var digest = new string('c', 64);
            var result = ServicePackageLifecycle.RecoverOffline(request, digest);
            Check(result.State == "applied" && result.Revision == 19 &&
                  result.Generation == "1.0.9-recovery-" + requestId,
                "The old package must become the selected revision after a missing new package.");
            Check(File.Exists(result.IntentPath) && File.Exists(result.PreparedResultPath),
                "Intent and prepared result must survive the lifecycle write.");
            Check(ServicePackageLifecycle.FingerprintPackage(Path.Combine(repository, ".packages",
                    "pegasus.conversation", result.Generation)) == fingerprint,
                "The restored generation must contain the exact preserved bytes.");
            var selectedHash = Hash(statePath);
            var replay = ServicePackageLifecycle.RecoverOffline(request, digest);
            Check(replay == result && Hash(statePath) == selectedHash,
                "A lost-reply replay must be read-only and return the original selection.");
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "engine.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET payload='{\"effect\":{\"state\":\"unknown\"}}' WHERE id=$id";
                command.Parameters.AddWithValue("$id", workflowId);
                command.ExecuteNonQuery();
            }
            Reject(() => ServicePackageLifecycle.RecoverOffline(request, digest));
            Check(Hash(statePath) == selectedHash,
                "A captured terminal workflow with an unknown effect must refuse without state mutation.");
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "engine.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET payload='{\"effect\":{\"state\":\"Succeeded\"}}' WHERE id=$id";
                command.Parameters.AddWithValue("$id", workflowId);
                command.ExecuteNonQuery();
            }
            var preparedId = Guid.NewGuid().ToString("N");
            var preparedRequest = request with
            {
                RequestId = preparedId, ExpectedCurrentRevision = 19,
                ExpectedCurrentLifecycleSha256 = selectedHash, ExpectedCurrentVersion = "1.0.9",
                ExpectedCurrentGeneration = result.Generation
            };
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "engine.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET payload='{\"effect\":{\"state\":\"prepared\"}}' WHERE id=$id";
                command.Parameters.AddWithValue("$id", workflowId);
                command.ExecuteNonQuery();
            }
            Reject(() => ServicePackageLifecycle.RecoverOffline(preparedRequest, new string('1', 64)));
            Check(Hash(statePath) == selectedHash &&
                  !File.Exists(Path.Combine(repository, ".lifecycle", ".recovery", preparedId + ".intent.json")),
                "A terminal workflow with a prepared effect must refuse before recovery intent or lifecycle mutation.");
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "engine.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET payload='{\"effect\":{\"state\":\"succeeded\"}}' WHERE id=$id";
                command.Parameters.AddWithValue("$id", workflowId);
                command.ExecuteNonQuery();
            }
            var staleId = Guid.NewGuid().ToString("N");
            var stale = request with { RequestId = staleId };
            Reject(() => ServicePackageLifecycle.RecoverOffline(stale, new string('d', 64)));
            Check(!File.Exists(Path.Combine(repository, ".lifecycle", ".recovery", staleId + ".intent.json")) &&
                  Hash(statePath) == selectedHash,
                "A stale lifecycle revision must refuse before intent or mutation.");
            var badId = Guid.NewGuid().ToString("N");
            var badSource = request with
            {
                RequestId = badId, ExpectedCurrentRevision = 19,
                ExpectedCurrentLifecycleSha256 = selectedHash, ExpectedCurrentVersion = "1.0.9",
                ExpectedCurrentGeneration = result.Generation,
                PreservedFiles = pins.Select((pin, index) => index == 0 ?
                    pin with { Sha256 = new string('0', 64) } : pin).ToArray()
            };
            Reject(() => ServicePackageLifecycle.RecoverOffline(badSource, new string('e', 64)));
            Check(!File.Exists(Path.Combine(repository, ".lifecycle", ".recovery", badId + ".intent.json")) &&
                  Hash(statePath) == selectedHash,
                "Changed old bytes must refuse before intent or mutation.");
            File.Delete(result.PreparedResultPath); // Only this disposable fixture's result.
            Reject(() => ServicePackageLifecycle.RecoverOffline(request, digest));
            Check(Hash(statePath) == selectedHash,
                "An incomplete prior result must not replay or replace lifecycle state.");
            var activeId = Guid.NewGuid().ToString("N");
            store.Put("job", activeId, new { status = "running" });
            var activeRequest = badSource with { RequestId = Guid.NewGuid().ToString("N"), PreservedFiles = pins };
            Reject(() => ServicePackageLifecycle.RecoverOffline(activeRequest, new string('f', 64)));
            Check(Hash(statePath) == selectedHash,
                "Real active legacy work must block an offline package selection.");
            store.Delete("job", activeId);
            File.WriteAllText(Path.Combine(data, "service.lock"), "");
            using var owner = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("The test host has no process path.");
            var workerDll = typeof(ServicePackageOfflineRecovery).Assembly.Location;
            var cliRequest = request with
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ExpectedCurrentRevision = 19,
                ExpectedCurrentLifecycleSha256 = selectedHash,
                ExpectedCurrentVersion = "1.0.9",
                ExpectedCurrentGeneration = result.Generation,
                StoppedWorker = new(999999, 1, Path.Combine(root, "historical-worker.exe"),
                    owner.User!.Value, self.SessionId, "Cerebral.Fixture.Absent." + Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N")),
                MaintenanceCode = new(host, Hash(host), Hash(workerDll), owner.User!.Value, self.SessionId)
            };
            var requestPath = Path.Combine(root, "offline-request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(cliRequest, CerebralJson.Options));
            (int Exit, string Output, string Error) InvokeCli()
            {
                var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { workerDll, "--local-run", "--data", data,
                    "--service-plugins", repository, "--service-package-recover", requestPath })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new IOException("The isolated recovery CLI did not start.");
                if (!child.WaitForExit(30_000))
                    throw new TimeoutException("The isolated recovery CLI did not finish.");
                return (child.ExitCode, child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd());
            }
            var beforeCliHash = Hash(statePath);
            var changedCode = cliRequest with
            {
                MaintenanceCode = cliRequest.MaintenanceCode with { WorkerDllSha256 = new string('0', 64) }
            };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(changedCode, CerebralJson.Options));
            var refusedCode = InvokeCli();
            Check(refusedCode.Exit != 0 && Hash(statePath) == beforeCliHash,
                "A changed maintenance Worker DLL must refuse before lifecycle mutation.");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(cliRequest, CerebralJson.Options));
            using (var heldLock = new FileStream(Path.Combine(data, "service.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                var refusedOwner = InvokeCli();
                Check(refusedOwner.Exit != 0 && Hash(statePath) == beforeCliHash,
                    "A held normal Worker lock must block offline package recovery.");
            }
            var firstCli = InvokeCli();
            var firstState = firstCli.Exit == 0 && firstCli.Error.Length == 0
                ? JsonDocument.Parse(firstCli.Output).RootElement.GetProperty("state").GetString() : null;
            Check(firstCli.Exit == 0 && firstCli.Error.Length == 0 &&
                  firstState == "applied",
                "The real maintenance executable must finish the isolated selection.");
            var cliStateHash = Hash(statePath);
            var replayCli = InvokeCli();
            Check(replayCli.Exit == 0 && replayCli.Error.Length == 0 &&
                  replayCli.Output == firstCli.Output && Hash(statePath) == cliStateHash,
                "The real maintenance executable must reconcile a lost reply without a second write.");
            void Foreign(string id, params (string Id, string Version)[] required)
            {
                var directory = Path.Combine(repository, id);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "fixture.dll"), "foreign-fixture");
                File.WriteAllText(Path.Combine(directory, ServicePluginRuntime.ManifestFileName),
                    JsonSerializer.Serialize(new ServicePluginManifest
                    {
                        Id = id, Name = "Foreign fixture", Version = "1.0.0",
                        Description = "Disposable selected graph fixture", Assembly = "fixture.dll",
                        EntryType = "Fixture.Foreign",
                        Dependencies = required.Select(value => new ServicePluginDependency
                        { Id = value.Id, ExactVersion = value.Version }).ToList()
                    }, CerebralJson.Options));
            }
            var graphRequest = cliRequest with
            {
                RequestId = Guid.NewGuid().ToString("N"), ExpectedCurrentRevision = 20,
                ExpectedCurrentLifecycleSha256 = cliStateHash,
                ExpectedCurrentGeneration = "1.0.9-recovery-" + cliRequest.RequestId
            };
            Foreign("fixture.a", ("fixture.b", "2.0.0"));
            Foreign("fixture.b");
            Reject(() => ServicePackageLifecycle.RecoverOffline(graphRequest, new string('2', 64)));
            Check(Hash(statePath) == cliStateHash &&
                  !File.Exists(Path.Combine(repository, ".lifecycle", ".recovery",
                      graphRequest.RequestId + ".intent.json")),
                "An incompatible required foreign dependency must refuse before recovery intent or lifecycle mutation.");
            Foreign("fixture.a", ("fixture.b", "1.0.0"));
            Foreign("fixture.b", ("fixture.a", "1.0.0"));
            graphRequest = graphRequest with { RequestId = Guid.NewGuid().ToString("N") };
            Reject(() => ServicePackageLifecycle.RecoverOffline(graphRequest, new string('3', 64)));
            Check(Hash(statePath) == cliStateHash &&
                  !File.Exists(Path.Combine(repository, ".lifecycle", ".recovery",
                      graphRequest.RequestId + ".intent.json")),
                "A selected foreign dependency cycle must refuse before recovery intent or lifecycle mutation.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var prefix = Path.Combine(Path.GetTempPath(), "CerebralOfflineRecoveryTests-");
            if (root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { return; }
        throw new InvalidOperationException("An invalid recovery request was accepted.");
    }
}
