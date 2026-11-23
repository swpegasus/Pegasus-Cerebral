using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ConfigurationConnectionBackupTests
{
    public static Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-connection-backup-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        string Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
            { checks++; Console.WriteLine("PASS: " + label); return error.Message; }
            throw new Exception("FAIL: " + label);
        }
        try
        {
            var source = Seed(Path.Combine(root, "source"), "SOURCE-PRIVATE", root);
            var sourceHost = source.Get<HostSettings>("settings", "host")!;
            sourceHost.WorkflowIdleExpiryDays = 14;
            source.Put("settings", "host", sourceHost);
            var sourceBefore = Saved(source);
            var exported = new ConfigurationBackups(source, () => false).Export(true);
            var document = exported.Document;
            var connections = document.Connections!;
            var json = JsonSerializer.Serialize(exported, CerebralJson.Options);
            Check(document.SchemaVersion == ConfigurationBackupContract.SchemaVersion && connections.Workers.Count == 1 && connections.Executables.Count == 1 && connections.McpServers.Count == 1,
                "v3 exports typed worker, executable and MCP declarations");
            Check(!json.Contains("SOURCE-PRIVATE", StringComparison.Ordinal) && !json.Contains("accessKey", StringComparison.Ordinal) &&
                !json.Contains("sshIdentityFile", StringComparison.Ordinal) && !json.Contains("prefixArguments", StringComparison.Ordinal) &&
                connections.Executables[0].ArgumentsOmitted && connections.McpServers[0].ArgumentsOmitted,
                "actual backup export omits credentials, SSH key-file references and opaque arguments");
            Check(exported.Attention.Count(x => x.Code == "opaque-arguments-omitted") == 2 && Saved(source) == sourceBefore &&
                !Directory.Exists(Path.Combine(root, "provider-files")),
                "export reports omissions without changing SQL or creating referenced program directories");
            Check(connections.SharedFilesWorkerId == "remote" && connections.CortexNamespaces.SequenceEqual(["project.fixture", "knowledge.shared"]) &&
                connections.CortexWriteNamespaces.SequenceEqual(["project.fixture"]),
                "typed shared-host and Cortex namespace references are retained");

            var same = Seed(Path.Combine(root, "same"), "DESTINATION-PRIVATE", root);
            var sameHost = same.Get<HostSettings>("settings", "host")!;
            sameHost.Executables.Add(new() { Id = "unrelated", Path = Path.Combine(root, "unchanged.exe"), PrefixArguments = ["UNRELATED-PRIVATE"] });
            same.Put("settings", "host", sameHost);
            var notifications = 0;
            var sameBackups = new ConfigurationBackups(same, () => false, () => notifications++);
            var sameBefore = Saved(same);
            var preview = sameBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true);
            Check(Saved(same) == sameBefore && !JsonSerializer.Serialize(preview, CerebralJson.Options).Contains("DESTINATION-PRIVATE", StringComparison.Ordinal),
                "restore preview is read-only and never exposes retained local credentials");
            sameBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true)), true);
            var restoredSame = same.Get<HostSettings>("settings", "host")!;
            Check(document.Host.WorkflowIdleExpiryDays == 14 && restoredSame.WorkflowIdleExpiryDays == 14,
                "a current backup restores the workflow expiry policy");
            Check(!restoredSame.Workers[0].RequiresConfiguration && restoredSame.Workers[0].AccessKey == "DESTINATION-PRIVATE-worker" &&
                restoredSame.Workers[0].SshIdentityFile == "DESTINATION-PRIVATE-ssh-key" && restoredSame.AccessKey == "DESTINATION-PRIVATE-host",
                "matching endpoint and SSH bindings retain destination-only credentials");
            Check(restoredSame.Executables.Single(x => x.Id == "tool").PrefixArguments.SequenceEqual(["DESTINATION-PRIVATE-prefix"]) &&
                !restoredSame.Executables.Single(x => x.Id == "tool").RequiresConfiguration &&
                restoredSame.McpServers[0].Arguments.SequenceEqual(["DESTINATION-PRIVATE-mcp"]) &&
                restoredSame.McpServers[0].Enabled && !restoredSame.McpServers[0].RequiresConfiguration,
                "matching ready executable and provider bindings retain destination-only arguments and enablement");
            Check(restoredSame.Executables.Single(x => x.Id == "unrelated").PrefixArguments.SequenceEqual(["UNRELATED-PRIVATE"]) &&
                notifications == 1, "connection upsert preserves unrelated bindings and notifies only after commit");

            var fresh = Seed(Path.Combine(root, "fresh"), "FRESH-PRIVATE", root, registrations: false);
            var freshBackups = new ConfigurationBackups(fresh, () => false);
            var freshPreview = freshBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true);
            Check(freshPreview.Attention.Count(x => x.Code == "local-configuration-required") == 3,
                "new connection declarations are explicitly presented for local configuration");
            freshBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(freshPreview.PreviewId, freshPreview.ExpectedHostRevision, true)), true);
            var freshHost = fresh.Get<HostSettings>("settings", "host")!;
            Check(freshHost.Workers.Single() is { RequiresConfiguration: true, AccessKey.Length: 0, SshIdentityFile: null } &&
                freshHost.Executables.Single() is { RequiresConfiguration: true, PrefixArguments.Count: 0 } &&
                freshHost.McpServers.Single() is { RequiresConfiguration: true, Enabled: false, Arguments.Count: 0 },
                "newly restored registrations persist inactive without copied secrets or implicit activation");
            Check(freshHost.SharedFilesWorkerId == "remote" && !Directory.Exists(Path.Combine(root, "provider-files")),
                "restored inactive shared-host reference is retained without provisioning provider paths");

            var changedDocument = document with
            {
                Connections = connections with
                {
                    Workers = [connections.Workers[0] with { Url = "http://127.0.0.1:6129/" }],
                    Executables = [connections.Executables[0] with { Path = Path.Combine(root, "provider-files", "different-tool.exe") }],
                    McpServers = [connections.McpServers[0] with { Executable = Path.Combine(root, "provider-files", "different-mcp.exe") }]
                },
                Host = document.Host with { QueueCapacity = 19 }
            };
            var changedBackups = new ConfigurationBackups(same, () => false);
            Apply(changedBackups, changedDocument);
            var changedHost = same.Get<HostSettings>("settings", "host")!;
            Check(changedHost.Workers.Single() is { RequiresConfiguration: true, AccessKey.Length: 0, SshIdentityFile: null } &&
                changedHost.Executables.Single(x => x.Id == "tool") is { RequiresConfiguration: true, PrefixArguments.Count: 0 } &&
                changedHost.McpServers.Single() is { RequiresConfiguration: true, Enabled: false, Arguments.Count: 0 },
                "same-ID changed identities cannot inherit local secret bindings or become active");
            Check(changedHost.QueueCapacity == 19 && changedHost.AccessKey == "DESTINATION-PRIVATE-host",
                "connection changes and ordinary settings commit together while host authorization remains local");

            var v2 = document with { SchemaVersion = 2, Connections = null, InterfacePreferences = null, Skills = null, Settings = null,
                Agents = document.Agents.Select(agent => agent with { PluginSettings = null }).ToArray(),
                Host = document.Host with { QueueCapacity = 23, WorkflowIdleExpiryDays = null } };
            var v2BeforeConnections = ConnectionsSaved(changedHost);
            var legacyBackups = new ConfigurationBackups(same, () => false);
            var legacyPreview = legacyBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(v2)), true);
            Check(legacyPreview.Attention.Any(x => x.Section == "connections" && x.Code == "not-supported"),
                "v2 backup preview explicitly preserves local connection declarations");
            legacyBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(legacyPreview.PreviewId, legacyPreview.ExpectedHostRevision, true)), true);
            Check(ConnectionsSaved(same.Get<HostSettings>("settings", "host")!) == v2BeforeConnections &&
                same.Get<HostSettings>("settings", "host") is { QueueCapacity: 23, WorkflowIdleExpiryDays: 14 },
                "restoring v2 settings preserves existing connections, pending flags and the expiry policy");

            var validationStore = Seed(Path.Combine(root, "validation"), "VALIDATION-PRIVATE", root);
            var validation = new ConfigurationBackups(validationStore, () => false);
            var validationBefore = Saved(validationStore);
            foreach (var invalid in new ConfigurationBackupDocument[]
            {
                document with { Connections = null },
                document with { SchemaVersion = 2 },
                document with { Connections = connections with { Workers = [connections.Workers[0], connections.Workers[0]] } },
                document with { Connections = connections with { Workers = [connections.Workers[0] with { Url = "https://user:REJECT-PRIVATE@example.invalid/" }] } },
                document with { Connections = connections with { Workers = [connections.Workers[0] with { SshPort = 70000 }] } },
                document with { Connections = connections with { Executables = [connections.Executables[0] with { Path = "bad\0path" }] } },
                document with { Connections = connections with { McpServers = [connections.McpServers[0] with { EndpointOmitted = true }] } },
                document with { Connections = connections with { McpServers = [connections.McpServers[0] with { ViaUserConnector = false }] } },
                document with { Connections = connections with { SharedFilesWorkerId = "absent-worker" } },
                document with { Connections = connections with { CortexNamespaces = ["project.fixture", "project.fixture"] } },
                document with { Connections = connections with { CortexWriteNamespaces = ["invalid\nnamespace"] } },
                document with { Connections = connections with { Workers = Enumerable.Range(0, 129).Select(index => connections.Workers[0] with { Id = "worker" + index }).ToArray() } }
            })
            {
                var message = Reject(() => validation.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(invalid)), true),
                    "invalid typed connection declaration is rejected before admission");
                Check(!message.Contains("REJECT-PRIVATE", StringComparison.Ordinal) && Saved(validationStore) == validationBefore,
                    "rejected connection preview neither echoes secret-like values nor partially writes state");
            }
            var unknown = JsonSerializer.SerializeToNode(new ConfigurationRestorePreviewRequest(document), CerebralJson.Options)!.AsObject();
            unknown["document"]!["connections"]!["workers"]![0]!["accessKey"] = "UNSUPPORTED-PRIVATE";
            var unknownMessage = Reject(() => validation.Preview(CerebralJson.Element(unknown), true), "portable worker objects reject credential fields");
            Check(!unknownMessage.Contains("UNSUPPORTED-PRIVATE", StringComparison.Ordinal) && Saved(validationStore) == validationBefore,
                "strict unknown-field rejection leaves all records unchanged");
            var opaque = JsonSerializer.SerializeToNode(new ConfigurationRestorePreviewRequest(document), CerebralJson.Options)!.AsObject();
            opaque["document"]!["connections"]!["executables"]![0]!["prefixArguments"] = new JsonArray("UNSUPPORTED-PRIVATE");
            Reject(() => validation.Preview(CerebralJson.Element(opaque), true), "portable executable objects reject opaque argv fields");

            var stale = validation.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true);
            var rotated = validationStore.Get<HostSettings>("settings", "host")!;
            rotated.Workers[0].AccessKey = "ROTATED-PRIVATE";
            validationStore.Put("settings", "host", rotated);
            var afterRotation = Saved(validationStore);
            Reject(() => validation.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stale.PreviewId, stale.ExpectedHostRevision, true)), true),
                "a local credential rotation invalidates a previously admitted restore preview");
            Check(Saved(validationStore) == afterRotation, "stale preview apply preserves the rotated credential and all other records");

            var rollback = Seed(Path.Combine(root, "rollback"), "ROLLBACK-PRIVATE", root);
            var rollbackNotifications = 0;
            var rollbackBackups = new ConfigurationBackups(rollback, () => false, () => rollbackNotifications++);
            var rollbackBefore = Saved(rollback);
            var rollbackPreview = rollbackBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(changedDocument)), true);
            Sql(rollback, """
                CREATE TRIGGER connection_backup_fixture_abort
                BEFORE UPDATE ON records
                WHEN NEW.kind='settings' AND NEW.id='host'
                BEGIN SELECT RAISE(ABORT, 'connection backup fixture rollback'); END;
                """);
            Reject(() => rollbackBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(rollbackPreview.PreviewId, rollbackPreview.ExpectedHostRevision, true)), true),
                "an actual SQLite failure aborts the connection restore transaction");
            Check(Saved(rollback) == rollbackBefore && rollbackNotifications == 0,
                "failed commit rolls back agent, model, prompt, connection and host settings without notifying");
            Sql(rollback, "DROP TRIGGER connection_backup_fixture_abort;");
            rollbackBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(rollbackPreview.PreviewId, rollbackPreview.ExpectedHostRevision, true)), true);
            Check(rollback.Get<HostSettings>("settings", "host")!.Workers[0].RequiresConfiguration && rollbackNotifications == 1,
                "the same unapplied preview can commit after the transient SQL failure is removed");

            Console.WriteLine($"Configuration connection backup: {checks} SQLite/policy integration checks passed; no provider, network, model or installed-service execution.");
            return Task.CompletedTask;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("cerebral-connection-backup-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact connection backup fixture.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Apply(ConfigurationBackups backups, ConfigurationBackupDocument document)
    {
        var preview = backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true);
        backups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true)), true);
    }

    private static StateStore Seed(string directory, string marker, string root, bool registrations = true)
    {
        var store = new StateStore(directory);
        var host = new HostSettings
        {
            Revision = 7, AccessKey = marker + "-host", DefaultModelId = "model",
            ModelDirectory = Path.Combine(directory, "models"), ProjectsDirectory = Path.Combine(directory, "projects"),
            SharedDirectory = Path.Combine(directory, "shared"), SharedFilesWorkerId = registrations ? "remote" : "local",
            CortexNamespaces = ["project.fixture", "knowledge.shared"], CortexWriteNamespaces = ["project.fixture"]
        };
        if (registrations)
        {
            host.Workers = [new()
            {
                Id = "remote", Name = "Remote", Url = "http://127.0.0.1:6123/", AccessKey = marker + "-worker",
                SshHost = "fixture.local", SshUser = "developer", SshPort = 2222,
                SshIdentityFile = marker + "-ssh-key", InstallDirectory = "/opt/pegasus"
            }];
            host.Executables = [new() { Id = "tool", Path = Path.Combine(root, "provider-files", "tool.exe"), PrefixArguments = [marker + "-prefix"] }];
            host.McpServers = [new()
            {
                Id = "provider", Executable = Path.Combine(root, "provider-files", "mcp.exe"), Arguments = [marker + "-mcp"],
                ViaUserConnector = true, Url = "http://127.0.0.1:6124/", Enabled = true
            }];
        }
        store.Put("settings", "host", host);
        store.Put("model", "model", new ModelRecord { Id = "model", Name = "Model", Path = Path.Combine(host.ModelDirectory, "model.gguf") });
        store.Put("agent", "agent", new AgentProfile { Id = "agent", Revision = 2, Name = "Agent", ModelId = "model", AllowedRoots = [host.ProjectsDirectory] });
        new SystemInstructions(store).UpsertForRestore("system", "Fixture shared guidance");
        return store;
    }

    private static string ConnectionsSaved(HostSettings host) => JsonSerializer.Serialize(new
    {
        host.Workers, host.Executables, host.McpServers, host.SharedFilesWorkerId, host.CortexNamespaces, host.CortexWriteNamespaces
    }, CerebralJson.Options);
    private static string Saved(StateStore store) => JsonSerializer.Serialize(new
    {
        host = store.Get<HostSettings>("settings", "host"),
        agents = store.List<AgentProfile>("agent").OrderBy(x => x.Id, StringComparer.Ordinal),
        models = store.List<ModelRecord>("model").OrderBy(x => x.Id, StringComparer.Ordinal),
        prompts = new SystemInstructions(store).CaptureEffective()
    }, CerebralJson.Options);
    private static void Sql(StateStore store, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
