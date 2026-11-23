using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.MetricsPlugin;
using Cerebral.MetricsPlugin.Desktop;
using Cerebral.MetricsPlugin.Service;

namespace Cerebral.MetricsPlugin.Tests;

internal static class Program
{
    private static readonly List<object> checks = [];
    private static readonly List<string> renders = [];
    private static string evidence = "";
    private static DateTimeOffset now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    [STAThread]
    public static int Main()
    {
        evidence = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "evidence"));
        Directory.CreateDirectory(evidence);
        var success = false;
        try
        {
            StorageChecks(); BoundaryChecks(); ServiceChecks().GetAwaiter().GetResult(); RenderChecks();
            success = true;
            var skipped = checks.Count(item => JsonSerializer.SerializeToElement(item).GetProperty("state").GetString() == "skipped");
            Console.WriteLine($"Completed {checks.Count} focused checks: {checks.Count - skipped} passed, {skipped} skipped; {renders.Count} fixture/offscreen renders."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); checks.Add(new { name = "unhandled", state = "failed", error = error.ToString() }); return 1; }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "focused-results.json"), JsonSerializer.Serialize(new
            {
                schema = "cerebral.metrics-plugin.focused-evidence/1", success, recordedUtc = DateTimeOffset.UtcNow,
                fixtureObservationTime = now, evidenceTier = "deterministic scalar fixtures + actual local file I/O + offscreen WPF rendering",
                liveHostFacet = "not connected", actualConfiguration = "not exercised", modelOrGpuCalls = 0,
                checks, renders
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAILED: " + name); checks.Add(new { name, state = "passed" }); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name); }
    private static string Root(string name) { var root = Path.Combine(evidence, "cases", name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static MetricsQuery Query(int limit = 100) => new(now.AddDays(-30), now.AddMinutes(1), Limit: limit);
    private static MetricObservation Generation(string id = "lane1", long revision = 1) => new()
    {
        InstallationId = "fixture-owner", SourceGeneration = "fixture-process1", EntityId = id, Revision = revision,
        ObservedAt = now.AddMinutes(-1), Kind = MetricKind.Generation, State = MetricState.Completed, Origin = MetricOrigin.Local,
        Basis = MetricBasis.ProviderTelemetry, ModelId = "fixture-model", WorkerId = "local", AgentId = "fixture-developer",
        ConversationId = "fixture-chat", ProjectId = "fixture-project", RunId = "fixture-run", EffectId = "fixture-effect",
        ConfirmedInputTokens = 1000, ConfirmedOutputTokens = 200, OutputTokenScope = MetricTokenScope.ProviderUnspecified,
        PreparedInputTokens = 1200, EffectiveContextTokens = 32768, ResponseBudgetTokens = 2048,
        ReasoningReserveTokens = 512, CachedTokens = 800, ProcessedTokens = 1200,
        ProviderPromptMilliseconds = 150, ProviderOutputMilliseconds = 2500, OutputTokensPerSecond = 80, GenerationHostMilliseconds = 3000
    };
    private static MetricObservation Workflow(string id = "run1", long revision = 1) => new()
    {
        InstallationId = "fixture-owner", SourceGeneration = "fixture-process1", EntityId = id, Revision = revision,
        ObservedAt = now.AddMinutes(-1), Kind = MetricKind.Workflow, State = MetricState.Completed, Origin = MetricOrigin.Local,
        Basis = MetricBasis.HostReceipt, WorkerId = "local", AgentId = "fixture-developer", ConversationId = "fixture-chat",
        ProjectId = "fixture-project", RunId = id, WorkflowActiveMilliseconds = 3100, WorkflowWallMilliseconds = 5000
    };
    private static MetricObservation Tool(string id = "effect1") => Workflow(id) with
    { Kind = MetricKind.Tool, EffectId = id, ToolName = "fixture.build", WorkflowActiveMilliseconds = null, WorkflowWallMilliseconds = null };
    private static MetricObservation Capacity(string id, DateTimeOffset at, MetricOrigin origin = MetricOrigin.Local) => new()
    {
        InstallationId = "fixture-owner", SourceGeneration = "fixture-process1", EntityId = id, Revision = 1, ObservedAt = at,
        Kind = MetricKind.Capacity, State = MetricState.Available, Origin = origin, Basis = MetricBasis.HostSnapshot,
        WorkerId = origin == MetricOrigin.Local ? "local" : "fixture-remote", ModelId = "fixture-model",
        AllocatedSlots = 5, AvailableSlots = 3, QuarantinedSlots = 1, ObservedExecutions = 2, ObservedInferenceActivity = 1
    };
    private static void StorageChecks()
    {
        var root = Root("dedup");
        using (var h = new MetricsHistory(root, () => now))
        {
            Check(h.Append(Generation()), "actual file append");
            Check(!h.Append(Generation()), "identical receipt read ignored");
            Check(!h.Append(Generation(revision: 0)), "older observation revision ignored");
            Check(!h.Append(Generation() with { ConfirmedOutputTokens = 201 }), "equal revision conflict refused");
            Check(h.Append(Generation(revision: 2) with { ConfirmedOutputTokens = 300 }), "higher revision replaces cumulative observation");
            Check(h.Query(Query(), default).Summary.OutputTokens.Value == 300, "cumulative updates do not inflate totals");
            Check(h.Append(Generation() with { SourceGeneration = "fixture-process2" }), "new process generation gets independent lane identity");
            Check(h.Query(Query(), default).Summary.Generations == 2, "process lane reuse retains both real entities");
            Check(h.Append(Workflow()), "durable workflow observed");
            Check(!h.Append(Workflow() with { SourceGeneration = "fixture-process2" }), "durable workflow reread across restart deduplicated");
            Check(h.Append(Tool()), "tool receipt observed");
            Check(!h.Append(Tool() with { SourceGeneration = "fixture-process2" }), "tool receipt reread across restart deduplicated");
            var cap1 = Capacity("sample1", now.AddMinutes(-3));
            Check(h.Append(cap1) && h.Append(Capacity("sample2", now.AddMinutes(-2)) with { ObservedExecutions = 4 }), "capacity uses distinct sample identities");
            Check(!h.Append(cap1 with { Revision = 2, ObservedAt = now }), "reused capacity identity cannot overwrite another sample time");
            var query = h.Query(Query(1), default);
            Check(query.Rows.Length == 1 && query.OmittedRows > 0 && query.Truncated, "bounded row query reports omissions");
            Check(query.Summary.ObservedExecutionPeak == 4 && query.Summary.ObservedInferencePeak == 1, "execution and inference peaks remain distinct");
            Check(query.Capacity.Length == 1 && query.Capacity[0].EntityId == "sample2", "latest capacity retained independently of row page");
            Check(h.Query(Query() with { AgentId = "foreign-agent" }, default).Rows.Length == 0, "exact agent filter");
            Check(h.Query(Query() with { ProjectId = "fixture-project" }, default).Summary.CompletedWorkflows == 1, "project filter preserves workflow count");
            Check(h.Coverage().RejectedRecords == 2 && h.Coverage().DuplicateReads >= 3, "conflicts and repeated reads counted separately");
            h.SourceStarted(); h.SourceGap(4); Check(h.Coverage().SourceDropped == 4, "source drop count visible");
        }
        using (var h = new MetricsHistory(root, () => now))
        {
            Check(h.Query(Query(), default).Summary.OutputTokens.Value == 500, "actual reopen restores deduplicated totals");
            Check(!h.Append(Workflow() with { SourceGeneration = "fixture-process3" }), "restart replay does not add completion");
            Check(h.Coverage().SourceDropped == 4 && h.Coverage().Partial, "restart retains loss marker and signals collection gap");
        }
        using (var h = new MetricsHistory(Root("quota"), () => now))
        {
            h.SaveSettings(new(0, new(false, 30, 1_048_576)), default);
            for (var i = 0; i < 1600; i++) h.Append(Generation("quota-" + i) with { ObservedAt = now.AddSeconds(-1600 + i) });
            Check(h.Coverage().StoredBytes <= 1_048_576 && h.Coverage().RetainedEntities < 1600, "actual quota pressure prunes oldest entities");
            Check(h.Query(Query(), default).Rows.Any(row => row.EntityId == "quota-1599"), "quota preserves newest observation");
        }
        using (var h = new MetricsHistory(Root("age"), () => now))
        {
            h.Append(Generation() with { ObservedAt = now.AddDays(-29) });
            h.SaveSettings(new(0, new(false, 7)), default);
            Check(h.Coverage().RetainedEntities == 0 && h.Coverage().StoredBytes == 0, "retention change removes expired owned rows");
            Check(!h.Append(Generation() with { ObservedAt = now.AddDays(-8) }), "expired late arrival not reintroduced");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var h = new MetricsHistory(Root("commit-cancellation"), () => now) { AfterSettingsCommit = cancellation.Cancel })
        {
            var result = h.SaveSettings(new(0, new(false, 7)), cancellation.Token);
            Check(cancellation.IsCancellationRequested && result.Revision == 1 && result.Values.RetentionDays == 7, "cancellation after atomic commit retains successful save result");
            Throws<OperationCanceledException>(() => h.SaveSettings(new(1, new(false, 8)), cancellation.Token), "cancellation before effect prevents settings save");
            Throws<InvalidOperationException>(() => h.SaveSettings(new(0, new(false, 8)), default), "stale settings revision refused");
        }
        foreach (var kind in new[] { "corrupt", "truncated", "oversized-line" })
        {
            var bad = Root(kind); var good = JsonSerializer.Serialize(Generation(), MetricsContract.Json) + "\n";
            var tail = kind switch { "corrupt" => "{invalid}\n", "truncated" => "{\"schemaVersion\":", _ => new string('x', MetricsContract.MaximumRecordBytes + 50) + "\n" };
            File.WriteAllText(Path.Combine(bad, "history.jsonl"), good + tail, new UTF8Encoding(false));
            var before = SHA256.HashData(File.ReadAllBytes(Path.Combine(bad, "history.jsonl")));
            using var h = new MetricsHistory(bad, () => now);
            Check(h.Coverage().State == "read-only" && h.Query(Query(), default).Summary.Generations == 1, kind + " preserves good rows with explicit read-only fault");
            Check(!h.Append(Generation("other")), kind + " cannot append over unknown bytes");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(bad, "history.jsonl")))), kind + " original bytes preserved");
        }
        var locked = Root("write-denied");
        using (var h = new MetricsHistory(locked, () => now))
        {
            h.Append(Generation());
            using var file = new FileStream(Path.Combine(locked, "history.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Check(!h.Append(Generation("denied")) && h.Coverage().State == "read-only", "actual write sharing failure faults history without escaping into workflow");
        }
        var owned = Root("single-writer");
        using (var first = new MetricsHistory(owned, () => now))
        using (var second = new MetricsHistory(owned, () => now))
            Check(second.Coverage().State == "read-only" && !second.Append(Generation()), "second collector cannot acquire private history writer");
        var linked = Root("linked-target"); var target = Path.Combine(linked, "target.jsonl"); File.WriteAllText(target, "preserve");
        try
        {
            File.CreateSymbolicLink(Path.Combine(linked, "history.jsonl"), target);
            using var h = new MetricsHistory(linked, () => now);
            Check(h.Coverage().State == "read-only" && File.ReadAllText(target) == "preserve", "linked history rejected without changing target");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { checks.Add(new { name = "linked-history actual symlink check", state = "skipped", reason = error.GetType().Name }); }
    }
    private static void BoundaryChecks()
    {
        var a = Generation() with { InstallationId = "a:Generation", EntityId = "b" };
        Check(a.Key != (Generation() with { InstallationId = "a", EntityId = "Generation:b" }).Key, "length-prefixed identifiers cannot collide by delimiter shape");
        Check(!MetricsValidation.Observation(Generation() with { AgentId = "C:\\secret\\file" }, now), "path-shaped metadata excluded");
        Check(!MetricsValidation.Observation(Generation() with { OutputTokensPerSecond = double.NaN }, now), "nonfinite rate rejected");
        Check(!MetricsValidation.Observation(Generation() with { ConfirmedInputTokens = -1 }, now), "negative counter rejected");
        Check(!MetricsValidation.Observation(Workflow() with { ConfirmedOutputTokens = 9 }, now), "workflow cannot masquerade as token consumption");
        Check(!MetricsValidation.Observation(Generation() with { Basis = MetricBasis.HostPreparedBudget }, now), "prepared budgets cannot masquerade as confirmed usage or provider rate");
        Check(!MetricsValidation.Observation(Generation() with { CachedTokens = 1300, ProcessedTokens = 1200 }, now), "contradictory cache counters refused instead of estimating hit rate");
        Check(!MetricsValidation.Observation(Generation() with { ConnectionLatencyMilliseconds = 2 }, now), "connection RTT cannot be a generation timing field");
        var json = JsonSerializer.Serialize(Generation(), MetricsContract.Json);
        foreach (var required in new[] { "kind", "state", "origin", "basis", "schemaVersion" })
        {
            using var document = JsonDocument.Parse(json);
            var missing = JsonSerializer.Serialize(document.RootElement.EnumerateObject().Where(field => field.Name != required).ToDictionary(field => field.Name, field => field.Value));
            Throws<JsonException>(() => JsonSerializer.Deserialize<MetricObservation>(missing, MetricsContract.Json), "stored " + required + " must be explicit");
        }
        Throws<JsonException>(() => JsonSerializer.Deserialize<MetricObservation>(json[..^1] + ",\"prompt\":\"do not capture\"}", MetricsContract.Json), "raw body fields refused by scalar decoder");
        using var h = new MetricsHistory(Root("semantics"), () => now);
        h.Append(Generation("visible") with { OutputTokenScope = MetricTokenScope.VisibleOnly, ConfirmedInputTokens = null });
        h.Append(Generation("reasoning") with { OutputTokenScope = MetricTokenScope.ReasoningAndVisible, ObservedAt = now.AddDays(-1) });
        h.Append(Generation("missing") with { ConfirmedInputTokens = null, ConfirmedOutputTokens = null, OutputTokensPerSecond = null });
        h.Append(Generation("active") with { State = MetricState.Active, OutputTokensPerSecond = 900, ObservedAt = now.AddSeconds(-3) });
        var result = h.Query(Query(), default);
        Check(result.Summary.OutputTokenScopes.Length == 3, "mixed output scope is explicit in aggregate");
        Check(result.Summary.InputTokens.Known == 2 && result.Summary.InputTokens.Missing == 2, "missing input counts are partial not zero");
        Check(result.Summary.LatestOutputTokensPerSecond != 900, "stale active rate cannot be reported live");
        Check(result.Trends.Length == 2 && result.Trends[0].OutputTokens.Value == 200, "trend attributes cumulative entity to observation day only");
        using var empty = new MetricsHistory(Root("empty"), () => now);
        Check(empty.Query(Query(), default).Summary.OutputTokens.Value is null, "empty history totals unavailable not zero");
        Throws<ArgumentException>(() => h.Query(Query(201), default), "oversized query page refused");
        Throws<ArgumentException>(() => h.Query(Query() with { From = now.AddYears(-2) }, default), "unbounded time query refused");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => h.Append(Generation("cancelled"), canceled.Token), "cancelled collector write has no effect");
        using var pools = new MetricsHistory(Root("capacity-pools"), () => now);
        for (var i = 0; i < 70; i++) pools.Append(Capacity("pool-sample-" + i, now.AddSeconds(-i), MetricOrigin.Remote) with { WorkerId = "fixture-remote-" + i });
        var poolResult = pools.Query(Query(1), default);
        Check(poolResult.Capacity.Length == 64 && poolResult.OmittedCapacity == 6 && poolResult.Truncated, "capacity projection is independent of mixed row page with explicit pool omissions");
        Check(pools.Query(Query() with { WorkerId = "fixture-remote-69" }, default).Capacity.Single().WorkerId == "fixture-remote-69", "exact worker filter can inspect pools outside the initial bounded projection");
        using var drops = new MetricsHistory(Root("drop-generations"), () => now);
        drops.SourceStarted(); drops.SourceGap(4); drops.SourceStarted(); drops.SourceGap(2);
        Check(drops.Coverage().SourceDropped == 6, "source drop totals accumulate across subscription generations instead of resetting");
    }
    private static async Task ServiceChecks()
    {
        using var pending = new MetricsServicePlugin(); pending.Initialize(new FixtureHost(Root("pending-service")));
        var status = await Read<MetricsStatus>(pending, "metrics-history.status");
        Check(!status.CaptureActive && !status.Settings.Values.CaptureEnabled && status.SourceState == "unavailable", "production default is capture-off with no invented runtime source");
        var updated = await Read<MetricsSettings>(pending, "metrics-history.settings.set", new MetricsSettingsChange(0, new(true)));
        status = await Read<MetricsStatus>(pending, "metrics-history.status");
        Check(updated.Revision == 1 && !status.CaptureActive && status.SourceReason == "reviewed-metrics-host-facet-not-connected", "enabling cannot fabricate connected source or observations");
        Check(pending.ToolExtensions.Count == 0, "component exposes no automatic cross-chat agent tool");
        var fixture = new FixtureSource(); using var plugin = new MetricsServicePlugin(() => fixture, root => new(root, () => now));
        plugin.Initialize(new FixtureHost(Root("fixture-service")));
        await Read<MetricsSettings>(plugin, "metrics-history.settings.set", new MetricsSettingsChange(0, new(true)));
        fixture.Send(Generation()); fixture.Send(Generation());
        MetricsQueryResult result = null!;
        for (var i = 0; i < 100; i++)
        {
            result = await Read<MetricsQueryResult>(plugin, "metrics-history.query", Query());
            if (result.Summary.Generations == 1 && result.Coverage.DuplicateReads > 0) break;
            await Task.Delay(10);
        }
        Check(result.Summary.Generations == 1 && result.Coverage.DuplicateReads == 1, "fixture service pump captures and deduplicates without desktop lifetime");
        await Read<MetricsSettings>(plugin, "metrics-history.settings.set", new MetricsSettingsChange(1, new(false)));
        status = await Read<MetricsStatus>(plugin, "metrics-history.status");
        Check(!status.CaptureActive && fixture.Disposed, "opt-out detaches fixture source before returning");
        Check((await Read<MetricsQueryResult>(plugin, "metrics-history.query", Query())).Summary.Generations == 1, "opt-out preserves existing retained history");
        try { await plugin.ExecuteAsync("metrics-history.settings.set", JsonSerializer.SerializeToElement(new { expectedRevision = 2, values = (object?)null }), default); throw new Exception("Malformed values accepted"); }
        catch (ArgumentException) { Check(true, "malformed settings Values refused before capture lifecycle changes"); }
        var retiring = new DelayedFixtureSource(); int created = 0;
        using var slow = new MetricsServicePlugin(() => { created++; return created == 1 ? retiring : new FixtureSource(); }, root => new(root, () => now));
        slow.Initialize(new FixtureHost(Root("retiring-source")));
        await Read<MetricsSettings>(slow, "metrics-history.settings.set", new MetricsSettingsChange(0, new(true)));
        await retiring.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Read<MetricsSettings>(slow, "metrics-history.settings.set", new MetricsSettingsChange(1, new(false)));
        await Read<MetricsSettings>(slow, "metrics-history.settings.set", new MetricsSettingsChange(2, new(true)));
        var waiting = await Read<MetricsStatus>(slow, "metrics-history.status");
        Check(created == 1 && waiting.SourceState == "retiring" && !waiting.CaptureActive, "detach timeout defers new source without replacing old task owner");
        retiring.Release.TrySetResult();
        await retiring.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static Task<T> Read<T>(MetricsServicePlugin plugin, string command, MetricsSettingsChange args) =>
        Read<T>(plugin, command, JsonSerializer.SerializeToElement(args, MetricsContract.TypeInfo<MetricsSettingsChange>()));
    private static Task<T> Read<T>(MetricsServicePlugin plugin, string command, MetricsQuery args) =>
        Read<T>(plugin, command, JsonSerializer.SerializeToElement(args, MetricsContract.TypeInfo<MetricsQuery>()));
    private static Task<T> Read<T>(MetricsServicePlugin plugin, string command)
    {
        using var empty = JsonDocument.Parse("{}");
        return Read<T>(plugin, command, empty.RootElement.Clone());
    }
    private static async Task<T> Read<T>(MetricsServicePlugin plugin, string command, JsonElement args)
    {
        var result = await plugin.ExecuteAsync(command, args, default);
        Check(result is JsonElement, "service returns framework-owned JSON: " + command);
        return ((JsonElement)result!).Deserialize<T>(MetricsContract.Json)!;
    }
    private static void RenderChecks()
    {
        _ = new Application();
        var root = Root("dashboard-fixture"); using var h = new MetricsHistory(root, () => now);
        for (var day = 6; day >= 0; day--) h.Append(Generation("fixture-day-" + day) with
        { ObservedAt = now.AddDays(-day).AddMinutes(-1), ConfirmedOutputTokens = 1400 + (6 - day) * 400, AgentId = day % 2 == 0 ? "fixture-developer" : "fixture-reviewer" });
        h.Append(Workflow()); h.Append(Workflow("failed-run") with { State = MetricState.Failed }); h.Append(Tool());
        h.Append(Capacity("local-offline", now.AddSeconds(-4)) with { State = MetricState.Unavailable, AllocatedSlots = null, AvailableSlots = null, QuarantinedSlots = null, ObservedExecutions = null, ObservedInferenceActivity = null });
        h.Append(Capacity("remote-ready", now.AddSeconds(-2), MetricOrigin.Remote) with { AllocatedSlots = 8, AvailableSlots = 6, QuarantinedSlots = 0, ConnectionLatencyMilliseconds = 2.5 });
        var result = h.Query(Query(), default);
        var status = new MetricsStatus("cerebral.metrics-history.status/2", new(1, new(true)), h.Coverage(), "available", "fixture-only", true);
        using var wide = new MetricsDashboardView(); wide.Present(status, result);
        Render(wide, "dashboard-fixture-wide.png", 1160, 2500, "FIXTURE ONLY · synthetic scalar observations · offscreen WPF · not a live Configuration capture");
        using var narrow = new MetricsDashboardView(); narrow.Present(status, result);
        Render(narrow, "dashboard-fixture-narrow.png", 760, 3000, "FIXTURE ONLY · narrow layout · synthetic scalar observations · not live");
        var texts = Descendants(wide).OfType<TextBlock>().Select(x => x.Text).ToArray();
        Check(texts.Any(x => x.Contains("Remote · fixture-remote")) && texts.Any(x => x.Contains("Available 6")), "remote capacity remains visible with local sample unavailable");
        Check(texts.Any(x => x.Contains("Connection latency (measured RTT): 2.5 ms")) && texts.Any(x => x.Contains("Other routing/inference cost unavailable")), "RTT is connection latency only with unmeasured route cost unavailable");
        Check(texts.Any(x => x.Contains("not measured token consumption per day")), "trend basis visible in UI");
        Check(texts.Any(x => x.Contains("Scope for confirmed counts: ProviderUnspecified")) &&
            texts.Any(x => x.Contains("Confirmed counts and provider progress are separate and are never added together")),
            "confirmed scope and separation from provider progress remain explicit");
        Check(texts.Any(x => x.Contains("withheld from reuse")), "quarantine describes held capacity without a recovery action");
        Check(Descendants(narrow).OfType<TextBlock>().All(x => double.IsFinite(x.ActualWidth) && double.IsFinite(x.ActualHeight)), "narrow offscreen text geometry finite");
        using var pending = new MetricsServicePlugin(); pending.Initialize(new FixtureHost(Root("real-unavailable-state")));
        var actualStatus = Read<MetricsStatus>(pending, "metrics-history.status").GetAwaiter().GetResult();
        var actualResult = Read<MetricsQueryResult>(pending, "metrics-history.query", Query()).GetAwaiter().GetResult();
        using var unavailable = new MetricsDashboardView(); unavailable.Present(actualStatus, actualResult);
        Render(unavailable, "dashboard-source-unavailable.png", 1000, 1800, "REAL CANDIDATE STATE · missing host facet · no runtime/model data · offscreen WPF");
        Check(Descendants(unavailable).OfType<TextBlock>().Any(x => x.Text.Contains("No recorded history. Unavailable does not mean zero")), "actual missing-facet state shows unavailable and no invented counts");
        Check(Descendants(unavailable).OfType<TextBlock>().Any(x => x.Text.Contains("No recorded workflow outcomes")), "empty workflow outcomes remain unavailable instead of successful zero claims");
        using var cancelled = new MetricsDashboardView((_, _) => Task.FromException<(MetricsStatus, MetricsQueryResult)>(new OperationCanceledException()),
            (_, _) => Task.FromException<MetricsSettings>(new OperationCanceledException()));
        cancelled.Present(actualStatus, actualResult);
        cancelled.Measure(new Size(900, 1800)); cancelled.Arrange(new Rect(0, 0, 900, 1800)); cancelled.UpdateLayout();
        cancelled.RefreshAsync().GetAwaiter().GetResult();
        Check(Descendants(cancelled).OfType<TextBlock>().Any(x => x.Text.Contains("cancelled by the connection")), "host cancellation becomes explicit UI read state");
        cancelled.SaveAsync().GetAwaiter().GetResult();
        Check(Descendants(cancelled).OfType<TextBlock>().Any(x => x.Text.Contains("settings result was not confirmed")), "host cancellation becomes unconfirmed settings state without automatic retry");
    }
    private static void Render(MetricsDashboardView view, string name, int width, int height, string label)
    {
        var page = new DockPanel { Background = Brushes.White, Width = width, Height = height };
        page.Resources[DesktopThemeResourceKeys.TextBrush] = new SolidColorBrush(Color.FromRgb(28, 44, 67));
        var banner = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(18), Background = Brushes.LightGoldenrodYellow, Foreground = Brushes.Black };
        DockPanel.SetDock(banner, Dock.Top); page.Children.Add(banner); page.Children.Add(view);
        page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); page.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); var path = Path.Combine(evidence, name);
        using (var file = File.Create(path)) png.Save(file); renders.Add(path);
        Check(new FileInfo(path).Length > 5000, "offscreen render produced: " + name);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private sealed class FixtureHost(string directory) : IServicePluginHost
    {
        public string PluginId => MetricsContract.PluginId;
        public string PackageDirectory => directory;
        public string DataDirectory => directory;
        public void Publish(string eventType, string? jobId, object? data) { }
    }
    private sealed class FixtureSource : IMetricsSource
    {
        private readonly Channel<MetricObservation> channel = Channel.CreateBounded<MetricObservation>(16);
        public bool Disposed { get; private set; }
        public string State => "available";
        public string Reason => "fixture-only";
        public long DroppedRecords => 0;
        public void Send(MetricObservation row) => channel.Writer.TryWrite(row);
        public IAsyncEnumerable<MetricObservation> ReadAsync(CancellationToken token) => channel.Reader.ReadAllAsync(token);
        public void Dispose() { Disposed = true; channel.Writer.TryComplete(); }
    }
    private sealed class DelayedFixtureSource : IMetricsSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string State => "available";
        public string Reason => "fixture-delayed-detach";
        public long DroppedRecords => 0;
        public async IAsyncEnumerable<MetricObservation> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Release.Task; } finally { Ended.TrySetResult(); }
            yield break;
        }
        public void Dispose() { }
    }
}
