using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

// Deterministic numeric adapter fixtures; no model, provider, network or owner dataset.
internal static class GenerationTelemetryTests
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks++; }
        ProviderGenerationMeasurement? Read(string json) { using var doc = JsonDocument.Parse(json); return ProviderGenerationTelemetry.Read(doc.RootElement); }
        var progress = Read("""{"prompt_progress":{"total":100,"cache":40,"processed":70,"time_ms":20}}""");
        Check(progress is { Phase: "prefill", InputTokens: 30, InputMilliseconds: 20 }, "Only uncached prompt tokens are timed.");
        var output = Read("""{"timings":{"prompt_n":60,"prompt_ms":40,"predicted_n":12,"predicted_ms":60}}""");
        Check(output is { Phase: "output", InputTokens: 60, OutputTokens: 12, OutputMilliseconds: 60 }, "Actual provider prompt/output timing counters are separate.");
        foreach (var json in new[] {
            """{}""", """{"content":"This is not a token counter"}""",
            """{"timings":{"predicted_n":-1,"predicted_ms":20}}""",
            """{"timings":{"predicted_n":2,"predicted_ms":-1}}""",
            """{"timings":{"predicted_n":2,"predicted_ms":1e400}}""",
            """{"timings":{"predicted_n":2,"predicted_n":3,"predicted_ms":20}}""",
            """{"timings":{"predicted_n":2,"predicted_ms":20},"timings":{"predicted_n":4}}""",
            """{"prompt_progress":{"total":20,"cache":10,"processed":5,"time_ms":20}}""",
            """{"prompt_progress":{"total":20,"cache":1,"processed":21,"time_ms":20}}"""
        }) Check(Read(json) is null, "Missing/malformed numeric telemetry never becomes fabricated throughput.");
        var root = Path.Combine(Path.GetTempPath(), "Cerebral.GenerationTelemetryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(Path.Combine(root, "data")); var configuration = new Configuration(store);
            await using var runtime = new ModelRuntime(store, configuration.Get, new EventHub());
            Check(runtime.GenerationTelemetrySnapshot().Active.Count == 0, "A cold meter has no fabricated active lane.");
            var first = new Fixture(true); var second = new Fixture(true); var remote = new Fixture(false);
            await using var one = await runtime.TrackExternalUsage(first).PrepareAsync(Request(), default);
            await using var two = await runtime.TrackExternalUsage(second).PrepareAsync(Request(), default);
            await using var three = await runtime.TrackExternalUsage(remote).PrepareAsync(Request(), default);
            var observed = new List<GenerationTelemetrySample>();
            ((IBoundGenerationTelemetry)one).BindTelemetry(new("chat-a", "run-a", "run-a", "effect-a", "agent-a", "local"), observed.Add);
            var tasks = new[] { one.GenerateAsync(null, default), two.GenerateAsync(null, default), three.GenerateAsync(null, default) };
            await Task.WhenAll(first.Started.Task, second.Started.Task, remote.Started.Task);
            first.Emit(new("prefill", 30, 20, null, null));
            Check(runtime.GenerationTelemetrySnapshot().Active.Single(x => x.Binding?.RunId == "run-a").InputTokensPerSecond == 1500,
                "Input throughput uses actual provider processed count and milliseconds.");
            first.Emit(new("output", 60, 40, 12, 60)); second.Emit(new("output", 10, 10, 20, 100));
            var active = runtime.GenerationTelemetrySnapshot();
            Check(active.Active.Count == 3 && active.Active.Count(x => x.OutputTokensPerSecond == 200) == 2 &&
                active.Active.Count(x => x.Phase == "unknown" && x.OutputTokensPerSecond is null) == 1,
                "Concurrent local lanes are measured while a terminal-only remote lane remains unavailable.");
            Check(active.Active.Select(x => x.LaneId).Distinct().Count() == 3 &&
                active.Active.Single(x => x.Binding is not null).Binding!.EffectId == "effect-a",
                "Each measurement retains exact host context identity without overlapping lane IDs.");
            first.Emit(new("output", 60, 40, 11, 59));
            Check(runtime.GenerationTelemetrySnapshot().Active.Single(x => x.Binding is not null).OutputTokens == 12,
                "A regressing provider count/timer cannot rewind an admitted sample.");
            first.Complete();
            await tasks[0];
            var afterFirst = runtime.GenerationTelemetrySnapshot();
            Check(afterFirst.Active.Count == 2 && afterFirst.Recent.Count == 1 &&
                afterFirst.Recent[0].State == "Stop" && afterFirst.Active.All(x => x.LaneId != afterFirst.Recent[0].LaneId),
                "A completed current sample moves to recent exactly once and never remains active for aggregate counting.");
            second.Complete(); remote.Complete(); await Task.WhenAll(tasks);
            var final = runtime.GenerationTelemetrySnapshot();
            Check(final.Active.Count == 0 && final.Recent.Count == 3, "All terminal lanes release numeric state independently of model lease ownership.");
            Check(final.Recent.Single(x => x.AdapterId == "fixture.remote").OutputTokensPerSecond is null,
                "Confirmed remote totals do not invent a prefill/output split or live rate.");
            Check(observed.Count >= 2 && observed.First().State == "active" && observed.Last().State == "Stop",
                "Per-run observation has real active and terminal phases without model text.");
            Check(runtime.UsageTotals().ConfirmedOutputTokens == 30, "Numeric observation does not duplicate confirmed token totals.");
            var threw = new Fixture(true, throwing: true);
            await using var failure = await runtime.TrackExternalUsage(threw).PrepareAsync(Request(), default);
            var pending = failure.GenerateAsync(null, default); await threw.Started.Task; threw.Complete();
            try { await pending; throw new InvalidOperationException("Expected fixture failure."); } catch (IOException) { }
            Check(runtime.GenerationTelemetrySnapshot().Active.Count == 0 &&
                runtime.GenerationTelemetrySnapshot().Recent.Last().State == "Unknown",
                "Thrown adapter paths discard active rates and retain only truthful unknown terminal status.");
            Console.WriteLine($"Generation telemetry: {checks} deterministic numeric/adapter checks passed; no model or network.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }
    private static ModelGenerationRequest Request() => new("fixture", [new("user", [new TextContentPart("fixture")])], 4096, 128);
    private sealed class Fixture(bool measured, bool throwing = false) : IModelRuntimeAdapter, IPreparedModelGeneration, IProviderGenerationTelemetry
    {
        private Action<ProviderGenerationMeasurement>? observer;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelAdapterDescriptor Descriptor => new(measured ? "fixture.local" : "fixture.remote", "1", ["text"]);
        private readonly string instanceId = Guid.NewGuid().ToString("N");
        public PreparedModelMetadata Metadata => new(instanceId, Descriptor.AdapterId, "1", "fixture", 60, 4096, 128, "fixture");
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) => Task.FromResult<IPreparedModelGeneration>(this);
        public void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? value) { if (measured) observer = value; }
        internal void Emit(ProviderGenerationMeasurement value) => observer?.Invoke(value);
        internal void Complete() => completion.SetResult();
        public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        { Started.SetResult(); await completion.Task.WaitAsync(token); if (throwing) throw new IOException("fixture"); return new(ModelGenerationFinish.Stop, "done", 10, 60); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
