using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real private SQLite/file checks of database-birth evidence. Existing-ledger fixtures
/// represent retained older state; the new-store path never receives a caller freshness flag or seeded ledger.</summary>
internal static class StateStoreFreshOwnershipTests
{
    private const string FixturePrefix = "fresh-ownership-";
    private const string Marker = ".fresh-ownership-fixture";
    private const string Guard = ".state-store-create.lock";
    private const string LedgerKind = "remote-prepared-routes";
    private const string LedgerId = "quarantine";

    internal static async Task<int> RunAsync(string fixtureParent)
    {
        if (!Path.IsPathFullyQualified(fixtureParent))
            throw new ArgumentException("Supply the exact absolute private fixture parent.", nameof(fixtureParent));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        Directory.CreateDirectory(parent);
        ScopedFilePolicy.RejectLinks(parent);
        var root = Path.Combine(parent, FixturePrefix + Guid.NewGuid().ToString("N"));
        RequireOwned(root, parent);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Marker), Path.GetFileName(root), Encoding.UTF8);
        var checks = new Checks();
        try
        {
            FreshAndAllowedOwnerFiles(root, checks);
            ExistingAbsence(root, checks);
            ExistingLedgers(root, checks);
            PriorOwnerArtifacts(root, checks);
            BeforeCommitFailure(root, checks);
            await ConcurrentConstructorsAsync(root, parent, checks);
            await BeforeCommitCrashAsync(root, parent, checks);
            return checks.Count;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            RequireOwned(root, parent);
            RejectTreeLinks(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void FreshAndAllowedOwnerFiles(string root, Checks checks)
    {
        var directory = Path.Combine(root, "fresh");
        Directory.CreateDirectory(directory);
        byte[] lockBody = [1, 2, 3, 4];
        byte[] logBody = Encoding.UTF8.GetBytes("private fixture log only\n");
        File.WriteAllBytes(Path.Combine(directory, "service.lock"), lockBody);
        File.WriteAllBytes(Path.Combine(directory, "local-worker.log"), logBody);
        StateStore store;
        // Main owns service.lock before constructing StateStore. The short creation lease
        // must therefore use a separate file and permit these exact preexisting owner files.
        using (var owner = new FileStream(Path.Combine(directory, "service.lock"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
            store = new StateStore(directory);
        checks.Check(CoreObjectCount(directory) == 6 && RecordCount(directory) == 1 && IsEmptyLedger(ReadLedger(directory)),
            "a genuinely created database commits its three core tables three indexes and one valid empty retained ledger");
        checks.Check(File.ReadAllBytes(Path.Combine(directory, "service.lock")).SequenceEqual(lockBody) &&
                     File.ReadAllBytes(Path.Combine(directory, "local-worker.log")).SequenceEqual(logBody),
            "existing Main service lock and local log are permitted and preserved by fresh creation");
        var accounting = ModelExecutionAccounting.For(store, coverageComplete: true, initiallyClosed: true);
        var before = accounting.Snapshot();
        checks.Check(IsObservedEmpty(Observe(store)) && IsClosed(accounting.Snapshot()) &&
                     accounting.Snapshot().ProcessGenerationId == before.ProcessGenerationId &&
                     accounting.Snapshot().Revision == before.Revision,
            "fresh retained ownership is observed empty while current-process model accounting remains explicitly closed");
        checks.Refuse(() => { using var lease = accounting.Begin(ModelOwnershipKind.LocalLoad, "never-loaded"); },
            error => error is InvalidOperationException && error.Message == "model_admission_closed",
            "creating empty retained ownership does not grant local model admission");
        var original = ReadLedger(directory);
        SqliteConnection.ClearAllPools();
        var reopened = new StateStore(directory);
        checks.Check(ReadLedger(directory) == original && IsObservedEmpty(Observe(reopened)) && RecordCount(directory) == 1,
            "reopening a fresh committed store retains its exact ledger without another bootstrap record");
    }

    private static void ExistingAbsence(string root, Checks checks)
    {
        foreach (var kind in new[] { "legacy-records", "zero-byte", "schema-empty" })
        {
            var directory = Path.Combine(root, kind);
            Directory.CreateDirectory(directory);
            if (kind == "legacy-records") CreateExistingRecords(directory);
            else if (kind == "zero-byte") File.WriteAllBytes(Database(directory), []);
            else Execute(directory, "PRAGMA user_version=17;", create: true);
            var store = new StateStore(directory);
            checks.Check(!ReadLedger(directory).Present && IsUnobserved(Observe(store)),
                "an existing " + kind + " database never gains fresh ownership proof from absent tables or records");
            var reopened = new StateStore(directory);
            checks.Check(!ReadLedger(directory).Present && IsUnobserved(Observe(reopened)) &&
                         (kind != "legacy-records" || ReadSentinel(directory) == "retained-before-bootstrap"),
                "reopen preserves missing ownership and prior data in existing " + kind + " state");
        }
    }

    private static void ExistingLedgers(string root, Checks checks)
    {
        var cases = new (string Name, string? Body, string State, int? Unknown)[]
        {
            ("known-empty", " { \"version\" : 1, \"entries\" : [ ] } \n", "observed", 0),
            ("reserved", ReservedLedger(unknown: false), "observed", 1),
            ("unknown", ReservedLedger(unknown: true), "observed", 1),
            ("corrupt", "{invalid retained JSON", "unavailable", null),
            ("json-null", "null", "unavailable", null),
            ("sql-null", null, "unavailable", null),
            ("unsupported", "{\"version\":2,\"entries\":[]}", "unavailable", null)
        };
        foreach (var item in cases)
        {
            var directory = Path.Combine(root, "ledger-" + item.Name);
            CreateExistingRecords(directory);
            InsertLedger(directory, item.Body);
            var original = ReadLedger(directory);
            var store = new StateStore(directory);
            var observed = Observe(store);
            checks.Check(ReadLedger(directory) == original && ReadSentinel(directory) == "retained-before-bootstrap" &&
                         observed.GetProperty("state").GetString() == item.State &&
                         NullableUnknown(observed) == item.Unknown &&
                         observed.GetProperty("complete").GetBoolean() == (item.State == "observed") &&
                         observed.GetProperty("reservations").GetArrayLength() == (item.Unknown == 1 ? 1 : 0),
                "construction byte-preserves " + item.Name + " ownership and its actual observed or unavailable classification");
            SqliteConnection.ClearAllPools();
            var reopened = new StateStore(directory);
            checks.Check(ReadLedger(directory) == original &&
                         JsonElement.DeepEquals(observed, Observe(reopened)) && ReadSentinel(directory) == "retained-before-bootstrap",
                "reopen never replaces " + item.Name + " retained ownership with empty proof");
        }
    }

    private static void PriorOwnerArtifacts(string root, Checks checks)
    {
        foreach (var relative in new[]
                 { "cerebral.db-wal", "cerebral.db-shm", "cerebral.db-journal", "engine.db", "Engine/selection.ini" })
        {
            var directory = Path.Combine(root, "orphan-" + relative.Replace('/', '-').Replace('.', '-'));
            var artifact = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            var bytes = Encoding.UTF8.GetBytes("retained prior-owner fixture: " + relative);
            File.WriteAllBytes(artifact, bytes);
            checks.Refuse(() => _ = new StateStore(directory),
                error => error is IOException && error.Message.StartsWith(
                    "Fresh state-store creation is unavailable while prior owner state exists:", StringComparison.Ordinal),
                "prior owner artifact " + relative + " refuses fresh creation rather than asserting empty ownership");
            checks.Check(!File.Exists(Database(directory)) && File.ReadAllBytes(artifact).SequenceEqual(bytes),
                "refusal preserves " + relative + " exactly and creates no primary database");
        }
    }

    private static void BeforeCommitFailure(string root, Checks checks)
    {
        var directory = Path.Combine(root, "transaction-failure");
        var reserved = 0; var beforeCommit = 0;
        checks.Refuse(() => _ = new StateStore(directory, deferDiagnosticPruning: true, initializationObserver: phase =>
        {
            if (phase == StateStoreInitializationPhase.FreshDatabaseReserved) reserved++;
            if (phase != StateStoreInitializationPhase.BeforeFreshInitializationCommit) return;
            beforeCommit++;
            throw new FixtureInterruption();
        }), error => error is FixtureInterruption,
            "an interruption inside the real fresh initialization transaction prevents constructor success");
        checks.Check(reserved == 1 && beforeCommit == 1 && File.Exists(Database(directory)) &&
                     CoreObjectCount(directory) == 0 && !ReadLedger(directory).Present,
            "precommit failure rolls back core schema and ledger together without deleting the reserved database");
        var calledOnExisting = false;
        var reopened = new StateStore(directory, deferDiagnosticPruning: true,
            initializationObserver: _ => calledOnExisting = true);
        checks.Check(!calledOnExisting && !ReadLedger(directory).Present && IsUnobserved(Observe(reopened)),
            "an interrupted existing database cannot manufacture another creation witness or ownership proof on reopen");
    }

    private static async Task ConcurrentConstructorsAsync(string root, string parent, Checks checks)
    {
        var directory = Path.Combine(root, "concurrent");
        await using var first = StartChild(parent, root, "concurrent", "hold-reserved");
        await WaitForBarrierAsync(first, root, "concurrent.hold-reserved.phase", TimeSpan.FromSeconds(10));
        // Observe only file metadata here: opening SQLite while the constructor has not yet
        // entered it would let the fixture itself participate in database initialization.
        checks.Check(File.Exists(Database(directory)) && new FileInfo(Database(directory)).Length == 0 &&
                     File.Exists(Path.Combine(directory, Guard)),
            "the first helper owns genuine database reservation before publishing any committed ownership proof");
        checks.Refuse(() =>
        {
            using var lease = new FileStream(Path.Combine(directory, Guard), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
        }, error => error is IOException && ((error.HResult & 0xffff) is 32 or 33 ||
                                             !OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 11),
            "the fresh helper actually holds the exclusive cross-process creation lease");
        await using var second = StartChild(parent, root, "concurrent", "plain");
        await WaitForBarrierAsync(second, root, "concurrent.plain.started", TimeSpan.FromSeconds(3));
        checks.Check(!second.Process.HasExited && !File.Exists(Path.Combine(root, "concurrent.plain.done")),
            "a second actual helper cannot finish opening the store while the first creation lease is held");
        await first.Process.StandardInput.WriteLineAsync("continue");
        await first.Process.StandardInput.FlushAsync();
        await first.RequireSuccessAsync();
        await second.RequireSuccessAsync();
        checks.Check(File.Exists(Path.Combine(root, "concurrent.hold-reserved.done")) &&
                     File.Exists(Path.Combine(root, "concurrent.plain.done")) && RecordCount(directory) == 1 &&
                     IsEmptyLedger(ReadLedger(directory)) && IsObservedEmpty(Observe(new StateStore(directory))),
            "both constructor processes finish through one committed empty ledger with no duplicate or lost initialization");
    }

    private static async Task BeforeCommitCrashAsync(string root, string parent, Checks checks)
    {
        var directory = Path.Combine(root, "crash");
        await using var child = StartChild(parent, root, "crash", "hold-before-commit");
        await WaitForBarrierAsync(child, root, "crash.hold-before-commit.phase", TimeSpan.FromSeconds(10));
        checks.Check(File.Exists(Database(directory)) && !File.Exists(Path.Combine(root, "crash.hold-before-commit.done")),
            "the crash fixture reaches the real uncommitted schema and ledger boundary in an exact owned helper");
        child.Process.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        checks.Check(child.Process.HasExited && child.Process.ExitCode != 0 &&
                     CoreObjectCount(directory) == 0 && !ReadLedger(directory).Present,
            "killing the helper before commit leaves no durable schema or empty-ledger claim after SQLite recovery");
        var calledOnExisting = false;
        var reopened = new StateStore(directory, deferDiagnosticPruning: true,
            initializationObserver: _ => calledOnExisting = true);
        checks.Check(!calledOnExisting && !ReadLedger(directory).Present && IsUnobserved(Observe(reopened)),
            "crash recovery treats the reserved file as existing uncertainty instead of retrying fresh ownership bootstrap");
    }

    internal static void RunChild(string fixtureParent, string fixtureRoot, string relative, string mode)
    {
        RequireOwned(fixtureRoot, fixtureParent);
        ScopedFilePolicy.RejectLinks(fixtureRoot);
        if (File.ReadAllText(Path.Combine(fixtureRoot, Marker), Encoding.UTF8) != Path.GetFileName(fixtureRoot) ||
            relative is not ("concurrent" or "crash") || mode is not ("plain" or "hold-reserved" or "hold-before-commit") ||
            relative == "crash" && mode != "hold-before-commit")
            throw new ArgumentException("The helper requires the exact owned creation/crash fixture and declared mode.");
        var prefix = Path.Combine(fixtureRoot, relative + "." + mode);
        File.WriteAllText(prefix + ".started", "started");
        var target = mode == "hold-before-commit"
            ? StateStoreInitializationPhase.BeforeFreshInitializationCommit
            : StateStoreInitializationPhase.FreshDatabaseReserved;
        var store = new StateStore(Path.Combine(fixtureRoot, relative), deferDiagnosticPruning: true,
            initializationObserver: phase =>
            {
                if (mode == "plain" || phase != target) return;
                File.WriteAllText(prefix + ".phase", phase.ToString());
                if (Task.Run(Console.ReadLine).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() != "continue")
                    throw new FixtureInterruption();
            });
        if (!IsObservedEmpty(Observe(store))) throw new InvalidOperationException("Helper construction did not observe its committed empty ledger.");
        File.WriteAllText(prefix + ".done", "observed-empty");
    }

    private static OwnedChild StartChild(string parent, string root, string relative, string mode)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Current .NET host path is unavailable.");
        if (!Path.IsPathFullyQualified(host) || !File.Exists(host) ||
            !Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated helper requires this runner's exact dotnet host, with no executable search.");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "--fresh-ownership-child", parent, root, relative, mode }) start.ArgumentList.Add(argument);
        return new OwnedChild(Process.Start(start) ?? throw new InvalidOperationException("Could not launch the exact test helper."));
    }

    private static async Task WaitForBarrierAsync(OwnedChild child, string root, string name, TimeSpan timeout)
    {
        var path = Path.Combine(root, name);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (child.Process.HasExited)
                throw new InvalidOperationException("Owned test helper exited before its creation barrier: " +
                    await child.DescribeExitAsync());
            if (watch.Elapsed >= timeout) throw new TimeoutException("Owned test helper did not reach its bounded creation barrier.");
            await Task.Delay(10);
        }
    }

    private sealed class OwnedChild(Process process) : IAsyncDisposable
    {
        internal Process Process { get; } = process;
        private readonly Task<string> output = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        internal Task WaitForExitAsync() => Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task RequireSuccessAsync()
        {
            await WaitForExitAsync();
            var stdout = await output; var stderr = await errors;
            if (Process.ExitCode != 0 || stdout.Length != 0 || stderr.Length != 0)
                throw new InvalidOperationException("Owned StateStore helper did not complete quietly: " + await DescribeExitAsync());
        }
        internal async Task<string> DescribeExitAsync()
        {
            await WaitForExitAsync();
            var stdout = await output; var stderr = await errors;
            // Helpers handle only authored private fixture paths and bodies, never credentials.
            // Retain enough bounded error text to distinguish a constructor failure from a barrier timeout.
            return $"exit {Process.ExitCode}; stdout {stdout.Length}; stderr {stderr.Length} characters; " +
                   stderr[..Math.Min(stderr.Length, 2048)];
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await WaitForExitAsync();
                _ = await output; _ = await errors;
            }
            finally { Process.Dispose(); }
        }
    }

    private static JsonElement Observe(StateStore store) =>
        CerebralJson.Element(new RemoteWorkerManagement(new Configuration(store)).ReadPreparedOwnership());
    private static bool IsObservedEmpty(JsonElement value) => value.GetProperty("state").GetString() == "observed" &&
        value.GetProperty("complete").GetBoolean() && NullableUnknown(value) == 0 && value.GetProperty("reservations").GetArrayLength() == 0;
    private static bool IsUnobserved(JsonElement value) => value.GetProperty("state").GetString() == "unobserved" &&
        !value.GetProperty("complete").GetBoolean() && NullableUnknown(value) is null && value.GetProperty("reservations").GetArrayLength() == 0;
    private static int? NullableUnknown(JsonElement value) => value.GetProperty("unknown").ValueKind == JsonValueKind.Null
        ? null : value.GetProperty("unknown").GetInt32();
    private static bool IsClosed(ModelAccountingSnapshot value) => !value.AdmissionOpen && value.CoverageComplete &&
        value.AdmissionReason == "explicit-owner-release-required" && value.Entries.Length == 0;

    private sealed record LedgerRow(bool Present, string Type, string Hex);
    private static LedgerRow ReadLedger(string directory)
    {
        using var connection = Connect(directory);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='records'";
        if ((long)command.ExecuteScalar()! == 0) return new(false, "missing", "");
        command.CommandText = "SELECT typeof(body),hex(CAST(body AS BLOB)) FROM records WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", LedgerKind); command.Parameters.AddWithValue("$id", LedgerId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(true, reader.GetString(0), reader.GetString(1)) : new(false, "missing", "");
    }
    private static bool IsEmptyLedger(LedgerRow row)
    {
        if (!row.Present || row.Type != "text") return false;
        using var document = JsonDocument.Parse(Convert.FromHexString(row.Hex));
        var value = document.RootElement;
        return value.EnumerateObject().Count() == 2 && value.GetProperty("version").GetInt32() == 1 &&
               value.GetProperty("entries").ValueKind == JsonValueKind.Array && value.GetProperty("entries").GetArrayLength() == 0;
    }
    private static int CoreObjectCount(string directory) => checked((int)Scalar(directory, """
        SELECT COUNT(*) FROM sqlite_schema WHERE
          (type='table' AND name IN ('records','sessions','session_messages')) OR
          (type='index' AND name IN ('jobs_by_status','activity_by_turn','session_messages_recent'))
        """));
    private static long RecordCount(string directory) => Scalar(directory, "SELECT COUNT(*) FROM records");
    private static long Scalar(string directory, string sql)
    {
        using var connection = Connect(directory); using var command = connection.CreateCommand();
        command.CommandText = sql; return (long)command.ExecuteScalar()!;
    }
    private static string? ReadSentinel(string directory)
    {
        using var connection = Connect(directory); using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind='fixture-retained' AND id='sentinel'";
        return command.ExecuteScalar() as string;
    }
    private static void CreateExistingRecords(string directory)
    {
        Directory.CreateDirectory(directory);
        // A nullable older table allows direct proof that an SQL NULL is not silently repaired.
        Execute(directory, """
            CREATE TABLE records(kind TEXT NOT NULL,id TEXT NOT NULL,body TEXT,PRIMARY KEY(kind,id));
            INSERT INTO records(kind,id,body) VALUES('fixture-retained','sentinel','retained-before-bootstrap');
            """, create: true);
    }
    private static void InsertLedger(string directory, string? body)
    {
        using var connection = Connect(directory); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO records(kind,id,body) VALUES($kind,$id,$body)";
        command.Parameters.AddWithValue("$kind", LedgerKind); command.Parameters.AddWithValue("$id", LedgerId);
        command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value); command.ExecuteNonQuery();
    }
    private static string ReservedLedger(bool unknown) => JsonSerializer.Serialize(new
    {
        version = 1, entries = new[] { new
        {
            id = new string('1', 32), route = new
            {
                installationId = new string('2', 32), endpointHash = new string('3', 64),
                modelId = "fixture-model", bindingHash = new string('4', 64)
            }, unknown, startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        } }
    }, CerebralJson.Options);
    private static void Execute(string directory, string sql, bool create = false)
    {
        using var connection = Connect(directory, create); using var command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static SqliteConnection Connect(string directory, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database(directory), Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        connection.Open(); return connection;
    }
    private static string Database(string directory) => Path.Combine(directory, "cerebral.db");
    private sealed class FixtureInterruption : Exception { }
    private sealed class Checks
    {
        internal int Count { get; private set; }
        internal void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Fresh ownership fixture: " + label);
            Count++; Console.WriteLine("PASS: " + label);
        }
        internal void Refuse(Action action, Func<Exception, bool> expected, string label)
        {
            try { action(); }
            catch (Exception error) when (expected(error)) { Check(true, label); return; }
            throw new InvalidOperationException("Fresh ownership fixture expected refusal: " + label);
        }
    }
    private static void RequireOwned(string root, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(parent) ||
            !full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, comparison) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^fresh-ownership-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refusing work or cleanup outside the exact owned fresh-ownership fixture.");
    }
    private static void RejectTreeLinks(string root)
    {
        ScopedFilePolicy.RejectLinks(root);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing recursive fixture cleanup across a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }
}
