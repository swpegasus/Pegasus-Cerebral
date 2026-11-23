using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in native conformance, owning only temporary state and its own model process.</summary>
public static class PreparedReasoningLiveTests
{
    public static async Task RunAsync(string modelPath, string serverPath)
    {
        modelPath = Path.GetFullPath(modelPath); serverPath = Path.GetFullPath(serverPath);
        if (!File.Exists(modelPath) || !File.Exists(serverPath)) throw new FileNotFoundException("Supply existing local GGUF and llama-server paths.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-prepared-reasoning-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Prepared reasoning live: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        try
        {
            var store = new StateStore(root);
            var settings = new HostSettings { LlamaServerPath = serverPath, ReservedMemoryMb = 1024 };
            store.Put("model", "reasoning-probe", new ModelRecord
            {
                Id = "reasoning-probe", Name = "Isolated reasoning conformance", Path = modelPath,
                SizeBytes = new FileInfo(modelPath).Length, ContextTokens = 4096, MaximumContextTokens = 4096,
                AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            await using var runtime = new ModelRuntime(store, () => settings, new EventHub());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"status":{"type":"string","enum":["ready"]}},"required":["status"],"additionalProperties":false}""");
            ModelGenerationRequest Request(string text, int response = 64, int reasoning = 512, JsonElement? constraint = null) => new("reasoning-probe",
                [new("system", [new TextContentPart("Follow the requested JSON schema. Use private reasoning only in the provider reasoning channel.")]),
                 new("user", [new TextContentPart(text)])], 4096, response, Temperature: 0, ResponseSchema: constraint)
                { ReasoningTokens = reasoning };
            var ready = Request("Return one JSON object with status equal to ready.", constraint: schema.RootElement);
            string instance;
            await using (var prepared = await runtime.PrepareAsync(ready, timeout.Token))
            {
                instance = prepared.Metadata.InstanceId;
                var metadata = prepared.Metadata;
                Check(metadata.ReasoningReserveTokens == 512 && metadata.CombinedGenerationBudget == 576 &&
                    (long)metadata.InputTokens + metadata.CombinedGenerationBudget + metadata.SafetyReserveTokens <= metadata.EffectiveContext,
                    "actual preparation reserves reasoning and visible allocation within the loaded context");
                var callbacks = new List<string>();
                var result = await prepared.GenerateAsync(callbacks.Add, timeout.Token);
                if (result.Finish != ModelGenerationFinish.Stop || result.Error is not null)
                    throw new InvalidOperationException($"Native reasoning did not complete: {result.Finish}; {result.Error}; combinedOutput={result.GeneratedTokens}; input={result.EvaluatedTokens}.");
                using var output = JsonDocument.Parse(result.Text);
                Check(output.RootElement.EnumerateObject().Count() == 1 && output.RootElement.GetProperty("status").GetString() == "ready" &&
                    result.GeneratedTokens is > 0 && result.GeneratedTokens <= metadata.CombinedGenerationBudget &&
                    result.EvaluatedTokens == metadata.InputTokens && callbacks.SequenceEqual(new[] { result.Text }),
                    "native reasoning returns schema-constrained visible output and exact terminal input/combined usage");
                Console.WriteLine($"Observed native reasoning: input={metadata.InputTokens}, evaluated={result.EvaluatedTokens}, combinedOutput={result.GeneratedTokens}, combinedCap={metadata.CombinedGenerationBudget}.");
                var reused = false;
                try { await prepared.GenerateAsync(null, timeout.Token); } catch (InvalidOperationException) { reused = true; }
                Check(reused, "prepared reasoning cannot execute twice");
            }
            await using (var limited = await runtime.PrepareAsync(Request("Write a long detailed uninterrupted story about a robot exploring fifty planets.", 1, 128), timeout.Token))
            {
                var result = await limited.GenerateAsync(null, timeout.Token);
                Check(result.Finish == ModelGenerationFinish.Length && result.GeneratedTokens == limited.Metadata.CombinedGenerationBudget &&
                    result.Text.Length == 0 && result.EvaluatedTokens == limited.Metadata.InputTokens,
                    "actual combined token ceiling reports Length and adopts no partial reasoning output");
            }
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            await using (var prepared = await runtime.PrepareAsync(Request("Write a long detailed uninterrupted story about a robot exploring fifty planets.", 2048, 128), timeout.Token))
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
                var result = await prepared.GenerateAsync(null, cancellation.Token);
                Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && result.Text.Length == 0,
                    "actual reasoning cancellation confirms stop and exposes no partial content");
            }
            await using (var after = await runtime.PrepareAsync(ready, timeout.Token))
            {
                var result = await after.GenerateAsync(null, timeout.Token);
                Check(after.Metadata.InstanceId == instance && result.Finish == ModelGenerationFinish.Stop && result.EvaluatedTokens == after.Metadata.InputTokens,
                    "the same loaded instance resumes after confirmed reasoning cancellation");
            }
            var overflow = false;
            try { await using var impossible = await runtime.PrepareAsync(ready with { ReasoningTokens = 4000 }, timeout.Token); }
            catch (ModelContextLimitException error) { overflow = error.Metadata.ReasoningReserveTokens == 4000; }
            Check(overflow, "reasoning reservation cannot overfill native context or silently disappear");
            await runtime.UnloadAsync("reasoning-probe", timeout.Token);
            Check(!runtime.IsLoaded("reasoning-probe"), "test-owned native model process unloads");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Prepared reasoning: {checks} real native inference/lifecycle checks passed; no agent/plugin or image reasoning claim.");
    }
}
