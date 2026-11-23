using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated SQLite, synthetic numeric observations, no provider or live runtime.</summary>
public static class PromptCacheDiagnosticsTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-prompt-cache-diagnostics-" + Guid.NewGuid().ToString("N"));
        var assertions = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Prompt-cache diagnostics: " + message); assertions++; }
        void Reject(Action action, string message)
        { try { action(); } catch (ArgumentException) { Check(true, message); return; } Check(false, message); }
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var store = new StateStore(Path.Combine(root, "history"));
            PromptCacheDiagnosticEntry Entry(DateTimeOffset at, int cached = 500, double milliseconds = 100,
                string worker = "local", int input = 1000) => new(at, "model", worker, "llama", input, cached,
                    input - cached, milliseconds, "reported", null);

            Check(store.RecordPromptCacheDiagnostic(Entry(now), 5), "first numeric observation establishes the lane baseline");
            Check(!store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(1)), 5), "identical metrics do not create repeated history rows");
            Check(!store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(2), cached: 530, milliseconds: 110), 5),
                "sub-threshold cache/timing jitter is suppressed");
            var first = store.ReadPromptCacheDiagnostics(at: now.AddSeconds(3));
            Check(first.Count == 1 && first[0].At == now, "suppressed observations never renew the retained timestamp");
            Check(store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(4), cached: 550), 5),
                "exact five-point reuse change is recorded against the retained baseline");
            Check(!store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(5), cached: 560, milliseconds: 120), 5),
                "prefill changes below the absolute 25ms floor are suppressed");
            Check(store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(6), cached: 550, milliseconds: 125), 5),
                "prefill changes at the absolute floor are recorded when the relative threshold is met");
            Check(store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(7), worker: "remote-worker"), 5),
                "a distinct worker has an independent baseline for the same model");
            var unavailable = Entry(now.AddSeconds(8)) with
            { CachedTokens = null, ProcessedPromptTokens = null, PromptProcessingMilliseconds = null,
              State = "unavailable", Diagnostic = "not-reported" };
            Check(store.RecordPromptCacheDiagnostic(unavailable, 5), "reported-to-unavailable transition is retained");
            Check(!store.RecordPromptCacheDiagnostic(unavailable with { At = now.AddSeconds(9) }, 5),
                "repeated missing provider diagnostics do not spam history");
            Check(store.RecordPromptCacheDiagnostic(Entry(now.AddSeconds(10), cached: 0), 5),
                "a reported zero hit count is distinct from unavailable diagnostics");
            Check(store.ReadPromptCacheDiagnostics(1, now.AddSeconds(10)).Single().ReuseRatio == 0,
                "zero cache utilization is measured rather than reported as unavailable");
            Reject(() => store.ReadPromptCacheDiagnostics(201, now), "reads reject an unbounded requested history");
            Reject(() => store.RecordPromptCacheDiagnostic(Entry(now) with { PromptProcessingMilliseconds = double.NaN }, 5),
                "non-finite provider timings cannot enter storage");
            Reject(() => store.RecordPromptCacheDiagnostic(Entry(now) with { Diagnostic = "untrusted prompt text" }, 5),
                "arbitrary provider diagnostic text cannot enter storage");
            Reject(() => store.RecordPromptCacheDiagnostic(Entry(now) with { CachedTokens = 1001 }, 5),
                "impossible cached-token counts cannot enter storage");
            Reject(() => store.RecordPromptCacheDiagnostic(Entry(now) with { ProcessedPromptTokens = 501 }, 5),
                "contradictory cached and newly processed totals cannot enter storage");
            Reject(() => store.RecordPromptCacheDiagnostic(unavailable with { State = "reported", Diagnostic = null }, 5),
                "a reported state must contain an actual numeric provider observation");
            Check(store.ReadPromptCacheDiagnostics(at: now.AddHours(24).AddSeconds(10)).Count == 0,
                "the exact rolling 24-hour boundary removes every observation without an inference request");

            var countStore = new StateStore(Path.Combine(root, "count"));
            for (var index = 0; index <= PromptCacheDiagnostics.MaximumEntries; index++)
                countStore.RecordPromptCacheDiagnostic(Entry(now.AddMilliseconds(index), cached: index % 2 == 0 ? 1000 : 0), 5);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(countStore.DirectoryPath, "cerebral.db")}"))
            {
                connection.Open(); using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM prompt_cache_diagnostics";
                Check(Convert.ToInt32(count.ExecuteScalar()) == 2048, "fixed row cap holds under many meaningful changes");
            }
            Check(countStore.ReadPromptCacheDiagnostics(200, now.AddSeconds(10)).Count == 200,
                "history response stays bounded even at storage capacity");

            var staleRoot = Path.Combine(root, "startup");
            var stale = new StateStore(staleRoot);
            stale.RecordPromptCacheDiagnostic(Entry(DateTimeOffset.UtcNow.AddHours(-25)), 5);
            Check(new StateStore(staleRoot).ReadPromptCacheDiagnostics().Count == 0,
                "startup removes expired rows before a diagnostic read");

            var malformedRoot = Path.Combine(root, "malformed-optional-schema");
            var healthyCore = new StateStore(malformedRoot);
            healthyCore.Put("fixture", "marker", new { value = "healthy core state" });
            healthyCore.Put("session", "preserved-session", new TaskSession { Id = "preserved-session", ProfileId = "lead",
                Messages = [new() { Sequence = 1, Content = "Preserved accepted request", Role = "user", Origin = "user" }] });
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(malformedRoot, "cerebral.db")}"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE prompt_cache_diagnostics; CREATE TABLE prompt_cache_diagnostics(incompatible TEXT)";
                command.ExecuteNonQuery();
            }
            var reopenedCore = new StateStore(malformedRoot);
            Check(reopenedCore.Get<System.Text.Json.JsonElement>("fixture", "marker").GetProperty("value").GetString() == "healthy core state" &&
                reopenedCore.GetSession("preserved-session")!.Messages.Single().Content == "Preserved accepted request",
                "an incompatible optional diagnostic schema cannot block startup or change healthy records and accepted requests");
            var malformedReadFailed = false;
            try { PromptCacheDiagnostics.Read(reopenedCore, new Configuration(reopenedCore)); }
            catch (SqliteException) { malformedReadFailed = true; }
            Check(malformedReadFailed, "owner reads preserve the incompatible optional schema as an actionable diagnostic failure");

            var configured = new StateStore(Path.Combine(root, "settings"));
            var configuration = new Configuration(configured);
            var settings = configuration.Get();
            Check(settings.PromptCacheDiagnosticsEnabled && settings.PromptCacheDiagnosticsChangePercentagePoints == 5,
                "old settings use optional collection with the sensible five-percent default");
            var metadata = new PreparedModelMetadata("instance", "llama", "1", "model", 1000, 8192, 1024, "fixture");
            var result = new ModelGenerationResult(ModelGenerationFinish.Stop, "PRIVATE PROMPT AND OUTPUT", 2, 1000)
            { PromptCache = new(1000, 500, 500, 100) };
            Check(PromptCacheDiagnostics.Capture(configured, configuration, "local", metadata, result, now),
                "collector stores provider-reported numeric fields without the model text");
            var fingerprint = WorkerExecutionSnapshot.RoutingFingerprint(settings);
            settings.PromptCacheDiagnosticsEnabled = false;
            settings.PromptCacheDiagnosticsChangePercentagePoints = 10;
            configuration.Save(settings);
            Check(!PromptCacheDiagnostics.Capture(configured, configuration, "local", metadata,
                    result with { PromptCache = new(1000, 900, 100, 200) }, now.AddSeconds(1)) &&
                PromptCacheDiagnostics.Read(configured, configuration, at: now.AddSeconds(1)).Entries.Count == 1,
                "disabling stops collection immediately while retained diagnostics expire normally");
            Check(WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()) == fingerprint,
                "diagnostic settings never invalidate a bound inference or workflow route");
            var reopened = new Configuration(new StateStore(configured.DirectoryPath)).Get();
            Check(!reopened.PromptCacheDiagnosticsEnabled && reopened.PromptCacheDiagnosticsChangePercentagePoints == 10,
                "optional toggle and threshold survive settings round-trip and reopen");
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(configured.DirectoryPath, "cerebral.db")}"))
            {
                connection.Open(); using var query = connection.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM prompt_cache_diagnostics WHERE body LIKE '%PRIVATE%'";
                Check(Convert.ToInt32(query.ExecuteScalar()) == 0,
                    "diagnostic rows contain no model prompt/output text or raw provider errors");
            }
            foreach (var threshold in new[] { 0, 101 })
            {
                var bad = configuration.Get(); bad.PromptCacheDiagnosticsChangePercentagePoints = threshold;
                Reject(() => configuration.Save(bad), "settings reject unsupported change thresholds");
            }
            var enabled = configuration.Get(); enabled.PromptCacheDiagnosticsEnabled = true; configuration.Save(enabled);
            Check(PromptCacheDiagnostics.Capture(configured, configuration, "local", metadata,
                    result with { PromptCache = null }, now.AddSeconds(2)) &&
                PromptCacheDiagnostics.Read(configured, configuration, at: now.AddSeconds(2)).Entries[0] is
                    { State: "unavailable", CachedTokens: null, Diagnostic: "not-reported" },
                "old remote-provider responses remain explicitly unavailable without fabricated zeros");
            Check(PromptCacheDiagnostics.Capture(configured, configuration, "local", metadata,
                    result with { PromptCache = new(1000, 9999, Diagnostic: "untrusted secret") }, now.AddSeconds(3)) &&
                PromptCacheDiagnostics.Read(configured, configuration, at: now.AddSeconds(3)).Entries[0].Diagnostic == "invalid-provider-telemetry",
                "malformed optional data becomes a fixed availability code without retaining raw text");
            var durable = new TaskSession { Id = "keep", ProfileId = "lead", Messages =
                [new() { Sequence = 1, Content = "Durable chat", Role = "user", Origin = "user" }] };
            configured.Put("session", durable.Id, durable);
            var journal = new EventJournal(Path.Combine(configured.DirectoryPath, "engine.db"));
            var sweeper = new RetentionCoordinator(journal, configured, configuration, new EventHub());
            var sweep = await sweeper.RunSweepAsync(now.AddHours(25));
            Check(sweep.FailureCount == 0 && configured.ReadPromptCacheDiagnostics(at: now.AddHours(25)).Count == 0 &&
                configured.GetSession("keep")!.Messages.Single().Content == "Durable chat",
                "periodic idle sweeping removes cache diagnostics while preserving the accepted conversation");
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(configured.DirectoryPath, "cerebral.db")}"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE prompt_cache_diagnostics"; command.ExecuteNonQuery();
            }
            Check(!PromptCacheDiagnostics.Capture(configured, configuration, "local", metadata, result, now) &&
                result.Finish == ModelGenerationFinish.Stop && result.GeneratedTokens == 2,
                "optional storage failure cannot replace confirmed inference evidence or trigger a repeat");
            var readFailed = false;
            try { PromptCacheDiagnostics.Read(configured, configuration); }
            catch (SqliteException) { readFailed = true; }
            Check(readFailed, "owner diagnostic reads preserve an actionable storage failure");
            Console.WriteLine($"Prompt-cache diagnostics: {assertions} deterministic SQLite/settings assertions passed; no live inference or cache benchmark.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-prompt-cache-diagnostics-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
