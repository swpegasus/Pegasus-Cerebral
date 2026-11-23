using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cerebral.PersonalityPlugin.Tests;
internal static partial class Program
{
    private sealed partial class WorkerPipeRun
    {
        internal static int IsolatedReaderMain()
        {
            try { RunIsolatedReaderChecksAsync().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine("FAIL: isolated profile reader " + error.GetType().Name + ": " + error.Message); return 1; }
        }

        private static async Task RunIsolatedReaderChecksAsync()
        {
            var retained = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-fixtures", "worker-working");
            var synthetic = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-fixtures", "profile-byte-reader");
            var syntheticData = Path.Combine(synthetic, "data");
            var reportPath = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-profile-byte-reader-verification.json");
            var logPath = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-profile-byte-reader-tests.log");
            var temporary = reportPath + ".part";
            var results = new List<object>();
            var labels = new List<string>();
            var closure = new List<object>();
            var retainedBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var retainedUnchanged = false;
            var syntheticCreated = false;
            var reportSinksAdmitted = false;
            var syntheticRemoved = false;
            object? retainedResult = null;
            object? nativeModule = null;
            Exception? failure = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var cancellation = deadline.Token;
            var timer = Stopwatch.StartNew();

            static async Task WriteNew(string path, string text, CancellationToken cancellation)
            {
                NoLinks(path);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
                await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation).ConfigureAwait(false);
                await output.FlushAsync(cancellation).ConfigureAwait(false);
            }

            void Pass(string name, object? detail = null)
            {
                labels.Add(name); results.Add(new { name, passed = true, detail });
                Console.WriteLine("PASS: " + name);
            }

            async Task SetSynthetic(string? body, bool missing = false, bool duplicate = false)
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(syntheticData, "cerebral.db"),
                    Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
                }.ToString());
                await connection.OpenAsync(cancellation).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "DELETE FROM records; INSERT INTO records(kind,id,body) VALUES('agent',$id,$body);";
                command.Parameters.AddWithValue("$id", missing ? "unselected-profile" : ContextProfileId);
                command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                if (duplicate)
                {
                    command.CommandText = "INSERT INTO records(kind,id,body) VALUES('agent',$id,$body);";
                    await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }
            }

            async Task ExpectBytes(string name, string body, int revision)
            {
                await SetSynthetic(body).ConfigureAwait(false);
                var expected = Encoding.UTF8.GetBytes(body);
                var actual = await ReadContextProfileRowValueAsync(syntheticData, cancellation).ConfigureAwait(false);
                var hash = Convert.ToHexStringLower(SHA256.HashData(expected));
                if (!actual.Payload.AsSpan().SequenceEqual(expected) || actual.Revision != revision || actual.Sha256 != hash)
                    throw new InvalidDataException("Synthetic bytes/revision/hash differ for " + name + ".");
                Pass(name, new { actual.Revision, utf8Bytes = actual.Payload.Length, sha256 = actual.Sha256 });
            }

            async Task ExpectRefusal(string name, string? body, Type type, string? message = null, bool missing = false, bool duplicate = false)
            {
                await SetSynthetic(body, missing, duplicate).ConfigureAwait(false);
                try
                {
                    _ = await ReadContextProfileRowValueAsync(syntheticData, cancellation).ConfigureAwait(false);
                    throw new InvalidOperationException("Reader accepted the invalid synthetic case " + name + ".");
                }
                catch (Exception error) when (error.GetType() == type &&
                    (message is null || error.Message.Contains(message, StringComparison.Ordinal)) &&
                    (error is not SqliteException sqlite || sqlite.SqliteErrorCode == 1))
                {
                    Pass(name, new { errorType = error.GetType().Name, expectedReason = message });
                }
            }

            Dictionary<string, string> SnapshotRetained()
            {
                SafeTree(retained);
                var files = Directory.EnumerateFiles(retained, "*", SearchOption.AllDirectories).Take(513).ToArray();
                if (files.Length > 512) throw new IOException("Retained fixture exceeds the bounded inventory.");
                return files.ToDictionary(path => path, FileHash, StringComparer.OrdinalIgnoreCase);
            }

            bool SameRetained()
            {
                var after = SnapshotRetained();
                return after.Count == retainedBefore.Count &&
                    retainedBefore.All(x => after.TryGetValue(x.Key, out var sha) && sha == x.Value);
            }

            try
            {
                NoLinks(retained); NoLinks(synthetic); NoLinks(reportPath); NoLinks(logPath); NoLinks(temporary);
                if (!Directory.Exists(retained) || Directory.Exists(synthetic) || File.Exists(synthetic) ||
                    File.Exists(reportPath) || Directory.Exists(reportPath) ||
                    File.Exists(logPath) || Directory.Exists(logPath) || File.Exists(temporary) || Directory.Exists(temporary))
                    throw new IOException("Isolated reader source or output collision; preserve existing state.");
                reportSinksAdmitted = true;
                foreach (var pin in RuntimePins.Where(x => Path.GetFileName(x.Path) is
                    "Microsoft.Data.Sqlite.dll" or "SQLitePCLRaw.core.dll" or "SQLitePCLRaw.provider.e_sqlite3.dll" or
                    "SQLitePCLRaw.batteries_v2.dll" or "e_sqlite3.dll"))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(pin.Path));
                    var actual = FileHash(path);
                    if (actual != pin.Sha256) throw new IOException("Isolated reader dependency pin differs.");
                    closure.Add(new { path, sha256 = actual });
                }
                if (closure.Count != 5 || !typeof(SqliteConnection).Assembly.Location.Equals(
                    Path.Combine(AppContext.BaseDirectory, "Microsoft.Data.Sqlite.dll"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The isolated reader did not load its exact pinned managed closure.");
                retainedBefore = SnapshotRetained();
                var retainedRead = await ReadContextProfileRowValueAsync(Path.Combine(retained, "data"), cancellation).ConfigureAwait(false);
                if (retainedRead.Payload.Length != 1043 || retainedRead.Revision != 1 ||
                    retainedRead.Sha256 != "9219f20778f2d98353d86a02e319b988e0c11c9f63efea5742e928ae3f2d173e")
                    throw new InvalidDataException("Retained profile bytes/revision/hash do not match independent evidence.");
                retainedResult = new { retainedRead.Revision, utf8Bytes = retainedRead.Payload.Length, sha256 = retainedRead.Sha256 };
                Pass("actual pinned value reader returns exact retained profile bytes/revision/hash", retainedResult);
                using (var current = Process.GetCurrentProcess())
                {
                    var modules = current.Modules.Cast<ProcessModule>().Where(x =>
                        x.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var expected = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
                    if (modules.Length != 1 || !Path.GetFullPath(modules[0].FileName).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                        FileHash(modules[0].FileName) != "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e")
                        throw new IOException("The loaded native reader is not the exact pinned local e_sqlite3.");
                    nativeModule = new { path = modules[0].FileName, sha256 = FileHash(modules[0].FileName) };
                }
                Pass("actual loaded native SQLite module matches accepted bytes", nativeModule);

                Directory.CreateDirectory(synthetic); syntheticCreated = true;
                Directory.CreateDirectory(syntheticData);
                NoLinks(synthetic); NoLinks(syntheticData);
                await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(syntheticData, "cerebral.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
                }.ToString()))
                {
                    await connection.OpenAsync(cancellation).ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandTimeout = 5;
                    // No unique key in this hostile fixture, so the reader's second-row refusal is exercised.
                    command.CommandText = "CREATE TABLE records(kind TEXT NOT NULL,id TEXT NOT NULL,body TEXT);";
                    await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }
                await ExpectBytes("synthetic UTF-8 nonASCII bytes are exact, with same-row revision/hash", "{\"revision\":7,\"name\":\"π 雪 😀 café\"}", 7).ConfigureAwait(false);
                const string prefix = "{\"revision\":1,\"padding\":\"";
                const string suffix = "\"}";
                var padding = 128 * 1024 - Encoding.UTF8.GetByteCount(prefix + suffix);
                await ExpectBytes("exact 128KiB profile payload is admitted", prefix + new string('x', padding) + suffix, 1).ConfigureAwait(false);
                await ExpectRefusal("oversized payload fails the length-first bound", prefix + new string('x', padding + 1) + suffix,
                    typeof(InvalidDataException), "exceeds its byte bound").ConfigureAwait(false);
                await ExpectRefusal("empty payload is rejected by SQLite JSON validation before value materialization", "",
                    typeof(SqliteException), "malformed JSON").ConfigureAwait(false);
                await ExpectRefusal("null payload fails the length guard", null, typeof(InvalidDataException), "payload is null").ConfigureAwait(false);
                await ExpectRefusal("missing exact profile row is rejected", "{\"revision\":1}",
                    typeof(InvalidDataException), "row is unavailable", missing: true).ConfigureAwait(false);
                await ExpectRefusal("nonpositive revision is rejected", "{\"revision\":0}",
                    typeof(InvalidDataException), "identity/revision is inconsistent").ConfigureAwait(false);
                await ExpectRefusal("revision outside Int32 is rejected", "{\"revision\":2147483648}", typeof(OverflowException)).ConfigureAwait(false);
                await ExpectRefusal("a second matching row is rejected", "{\"revision\":1}",
                    typeof(InvalidDataException), "identity/revision is inconsistent", duplicate: true).ConfigureAwait(false);
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try
                    {
                        _ = await ReadContextProfileRowValueAsync(syntheticData, cancelled.Token).ConfigureAwait(false);
                        throw new InvalidOperationException("Cancelled reader was accepted.");
                    }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                    {
                        Pass("cancelled reader refuses before opening its database");
                    }
                }
                retainedUnchanged = SameRetained();
                if (!retainedUnchanged) throw new IOException("The preserved fatal fixture changed during isolated reading.");
                Pass("all preserved fatal fixture files remain byte-identical", new { files = retainedBefore.Count });
            }
            catch (Exception error) { failure = error; }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (retainedBefore.Count != 0)
                {
                    try
                    {
                        retainedUnchanged = SameRetained();
                        if (!retainedUnchanged) throw new IOException("The preserved fatal fixture changed before finalization.");
                    }
                    catch (Exception error) { failure ??= error; }
                }
                if (syntheticCreated && failure is null)
                {
                    try
                    {
                        var exact = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-fixtures", "profile-byte-reader");
                        if (!Path.GetFullPath(synthetic).Equals(exact, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Synthetic cleanup target changed.");
                        SafeTree(synthetic); cleanup.Token.ThrowIfCancellationRequested();
                        Directory.Delete(synthetic, true); syntheticRemoved = !Directory.Exists(synthetic);
                        Pass("only the owned synthetic reader fixture is removed");
                    }
                    catch (Exception error) { failure ??= error; }
                }
                try
                {
                    if (!reportSinksAdmitted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
                    var report = new
                    {
                        schemaVersion = 1, taskId = "10000000-0000-4000-8000-000000000002",
                        mode = "isolated-profile-reader", status = failure is null ? "passed" : "failed",
                        assertions = labels.Count, elapsedSeconds = timer.Elapsed.TotalSeconds, labels, results,
                        retainedResult, retainedFixtureFiles = retainedBefore.Count, retainedUnchanged,
                        syntheticCreated, syntheticRemoved, closure, nativeModule,
                        workerStarts = 0, windowsCreated = 0, modelCalls = 0, networkCalls = 0,
                        failure = failure is null ? null : new { type = failure.GetType().Name, message = failure.Message },
                        limits = new[] { "Actual isolated pinned SQLite byte-reader checks only; no full Worker/context capture or model-facing proof.",
                            "Direct computed-value access avoids the unrelated streaming metadata path; no claim to repair or trace native metadata implementation.",
                            "Empty TEXT is rejected by SQLite JSON validation; the pre-copy numeric bound is exercised by oversized valid JSON.",
                            "30-second work plus10-second cleanup are cooperative, not an independently enforced hard wall-clock limit." }
                    };
                    NoLinks(reportPath); NoLinks(logPath); NoLinks(temporary);
                    if (File.Exists(temporary) || Directory.Exists(temporary)) throw new IOException("Reader report staging collision.");
                    await WriteNew(logPath, string.Join("\n", labels), cleanup.Token).ConfigureAwait(false);
                    await WriteNew(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cleanup.Token).ConfigureAwait(false);
                    NoLinks(reportPath); NoLinks(temporary); File.Move(temporary, reportPath, false);
                }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
