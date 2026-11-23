using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in actual model execution against supplied local binaries; owns only its temporary state and child.</summary>
public static class PreparedModelLiveTests
{
    public static async Task RunAsync(string modelPath, string serverPath)
    {
        modelPath = Path.GetFullPath(modelPath); serverPath = Path.GetFullPath(serverPath);
        if (!File.Exists(modelPath) || !File.Exists(serverPath)) throw new FileNotFoundException("Supply an existing local GGUF and llama-server executable.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-prepared-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root);
            var settings = new HostSettings { LlamaServerPath = serverPath, ReservedMemoryMb = 1024 };
            store.Put("model", "test-model", new ModelRecord
            {
                Id = "test-model", Name = "Local acceptance model", Path = modelPath, SizeBytes = new FileInfo(modelPath).Length,
                ContextTokens = 4096, MaximumContextTokens = 4096, AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            await using var runtime = new ModelRuntime(store, () => settings, new EventHub());
            IModelRuntimeAdapter adapter = runtime;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            ModelGenerationRequest Request(string text, int response = 64, JsonElement? schema = null) => new("test-model",
                [new("system", [new TextContentPart("Follow the user's instruction concisely. Do not add explanations unless asked.")]),
                 new("user", [new TextContentPart(text)])], 4096, response, Temperature: 0, ResponseSchema: schema);
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"status":{"type":"string","enum":["ready"]}},"required":["status"],"additionalProperties":false}""");
            await using var prepared = await adapter.PrepareAsync(Request("Return a JSON object with status equal to ready.", schema: schema.RootElement), timeout.Token);
            Check(prepared.Metadata.InputTokens > 0 && prepared.Metadata.ResponseBudget > 0 &&
                prepared.Metadata.InputTokens + prepared.Metadata.ResponseBudget <= prepared.Metadata.EffectiveContext,
                "provider preparation measures the actual token payload within loaded context");
            var result = await prepared.GenerateAsync(null, timeout.Token);
            if (result.Finish != ModelGenerationFinish.Stop)
                throw new Exception($"Real model did not complete: {result.Finish}; {result.Error}; partial characters={result.Text.Length}.");
            using var generated = JsonDocument.Parse(result.Text);
            Check(result.Finish == ModelGenerationFinish.Stop && result.GeneratedTokens is > 0 &&
                generated.RootElement.GetProperty("status").GetString() == "ready",
                "real local model returns requested structured content and actual terminal usage");
            Console.WriteLine($"Observed model result: preparedInput={prepared.Metadata.InputTokens}, evaluated={result.EvaluatedTokens}, output={result.GeneratedTokens}, finish={result.Finish}.");
            var reused = false;
            try { await prepared.GenerateAsync(null, timeout.Token); }
            catch (InvalidOperationException) { reused = true; }
            Check(reused, "a prepared generation cannot be executed a second time");
            await prepared.DisposeAsync();

            await using (var limited = await adapter.PrepareAsync(Request("Write a detailed list of twenty different imaginary planets. Begin with the first planet's name.", 1), timeout.Token))
            {
                var cut = await limited.GenerateAsync(null, timeout.Token);
                Check(cut.Finish == ModelGenerationFinish.Length && cut.GeneratedTokens == 1 && cut.Text.Length > 0,
                    "actual response limit retains partial output and reports Length rather than task completion");
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            await using (var cancelled = await adapter.PrepareAsync(Request("Write a long uninterrupted story about a robot exploring fifty planets, describing each in detail.", 1024), timeout.Token))
            {
                var stopped = await cancelled.GenerateAsync(_ => cancellation.Cancel(), cancellation.Token);
                Check(stopped.Finish == ModelGenerationFinish.Cancelled && stopped.CancellationConfirmed,
                    "cancelling actual inference confirms that the owned slot stopped");
            }
            await using (var after = await adapter.PrepareAsync(Request("Return a JSON object with status equal to ready.", schema: schema.RootElement), timeout.Token))
            {
                var continued = await after.GenerateAsync(null, timeout.Token);
                Check(after.Metadata.InstanceId == prepared.Metadata.InstanceId && continued.Finish == ModelGenerationFinish.Stop,
                    "same loaded model accepts a fresh assignment after confirmed cancellation");
            }
            await runtime.UnloadAsync("test-model", timeout.Token);
            Check(!runtime.IsLoaded("test-model"), "actual test-owned model process unloads");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine("Prepared model: 7 real local inference/lifecycle checks passed. No agent workflow or image claim.");
    }

    internal const string ConformanceSchema = """
        {"type":"object","properties":{"content":{"type":"string","minLength":1,"maxLength":8},"completeScene":{"type":"boolean"},"completionReason":{"type":"string","maxLength":16}},"required":["content","completeScene","completionReason"],"additionalProperties":false}
        """;

    /// <summary>One isolated 128-token inference whose prompt intentionally conflicts with maxLength.</summary>
    public static async Task RunSchemaConformanceAsync(string modelPath, string serverPath)
    {
        modelPath = Path.GetFullPath(modelPath); serverPath = Path.GetFullPath(serverPath);
        if (!File.Exists(modelPath) || !File.Exists(serverPath)) throw new FileNotFoundException("Supply an existing local GGUF and llama-server executable.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-prepared-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root);
            var settings = new HostSettings { LlamaServerPath = serverPath, ReservedMemoryMb = 1024 };
            store.Put("model", "schema-probe", new ModelRecord
            {
                Id = "schema-probe", Name = "Isolated schema conformance", Path = modelPath, SizeBytes = new FileInfo(modelPath).Length,
                ContextTokens = 4096, MaximumContextTokens = 4096, AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            await using var runtime = new ModelRuntime(store, () => settings, new EventHub());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await runtime.LoadAsync("schema-probe", timeout.Token);
            // Test-only transport observation of this exclusively owned runtime. No production
            // access seam, shared server, model binding or sampling setting is modified.
            var entries = (System.Collections.IEnumerable)typeof(ModelRuntime).GetField("instances", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
            var entry = entries.Cast<object>().Single();
            var instance = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
            var httpField = instance.GetType().GetField("Http")!;
            var originalHttp = (HttpClient)httpField.GetValue(instance)!;
            using var observed = new SchemaObservationHandler();
            using var observedHttp = new HttpClient(observed, disposeHandler: false) { BaseAddress = originalHttp.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
            ModelGenerationResult result;
            PreparedModelMetadata metadata;
            httpField.SetValue(instance, observedHttp);
            try
            {
                using var schema = JsonDocument.Parse(ConformanceSchema);
                var requestedContent = new string('A', 64);
                var prompt = "Return exactly one JSON object with three fields. content must be this exact 64-character string, without shortening it: " +
                    requestedContent + ". completeScene must be false and completionReason must be an empty string. No markdown or explanation.";
                IModelRuntimeAdapter adapter = runtime;
                await using var prepared = await adapter.PrepareAsync(new("schema-probe",
                    [new("system", [new TextContentPart("Follow the user's instruction exactly.")]), new("user", [new TextContentPart(prompt)])],
                    4096, 128, Temperature: 0, ResponseSchema: schema.RootElement), timeout.Token);
                metadata = prepared.Metadata;
                result = await prepared.GenerateAsync(null, timeout.Token);
            }
            finally { httpField.SetValue(instance, originalHttp); }
            await runtime.UnloadAsync("schema-probe", timeout.Token);

            using var expected = JsonDocument.Parse(ConformanceSchema);
            var exactWireSchema = observed.Submitted is { } wire && wire.TryGetProperty("json_schema", out var sentSchema) &&
                JsonElement.DeepEquals(sentSchema, expected.RootElement) && wire.GetProperty("n_predict").GetInt32() == 128;
            int? contentCharacters = null; bool? completeScene = null; int? reasonCharacters = null;
            var validObject = false;
            try
            {
                using var output = JsonDocument.Parse(result.Text);
                var value = output.RootElement;
                validObject = value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(x => x.Name)
                    .Order(StringComparer.Ordinal).SequenceEqual(new[] { "completeScene", "completionReason", "content" });
                if (validObject && value.GetProperty("content").ValueKind == JsonValueKind.String &&
                    value.GetProperty("completeScene").ValueKind is JsonValueKind.False or JsonValueKind.True &&
                    value.GetProperty("completionReason").ValueKind == JsonValueKind.String)
                {
                    contentCharacters = value.GetProperty("content").GetString()!.EnumerateRunes().Count();
                    completeScene = value.GetProperty("completeScene").GetBoolean();
                    reasonCharacters = value.GetProperty("completionReason").GetString()!.EnumerateRunes().Count();
                }
                else validObject = false;
            }
            catch (JsonException) { }
            var grammar = observed.GenerationSettings is { } generationSettings && generationSettings.TryGetProperty("grammar", out var grammarValue) &&
                grammarValue.ValueKind == JsonValueKind.String ? grammarValue.GetString() : null;
            bool? grammarLazy = observed.GenerationSettings is { } settingsValue && settingsValue.TryGetProperty("grammar_lazy", out var lazyValue) &&
                lazyValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? lazyValue.GetBoolean() : null;
            var boundedRangeGrammar = grammar?.Contains("char{1,8}", StringComparison.Ordinal) == true;
            var passed = result.Finish == ModelGenerationFinish.Stop && validObject && contentCharacters is >= 1 and <= 8 && reasonCharacters <= 16 && exactWireSchema && boundedRangeGrammar;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                probe = "prepared-native-maxLength", success = passed, requestedCharacters = 64, minimumCharacters = 1, maximumCharacters = 8,
                responseLimit = 128, exactWireSchema, metadata, result.Finish, result.GeneratedTokens, result.EvaluatedTokens,
                result.Error, outputCharacters = result.Text.Length, validObject, contentCharacters, completeScene, reasonCharacters,
                outputPreview = result.Text[..Math.Min(result.Text.Length, 256)],
                effectiveGrammarPresent = !string.IsNullOrEmpty(grammar), boundedRangeGrammar, grammarCharacters = grammar?.Length,
                grammarPreview = grammar is null ? null : grammar[..Math.Min(grammar.Length, 512)], grammarLazy,
                observed.TerminalObserved, observed.CompletionCalls,
                diagnosticChange = "Only generation_settings appended to response_fields; response buffered within 128 KiB for observation.",
                modelUnloaded = !runtime.IsLoaded("schema-probe")
            }));
            Check(passed, "the actual prepared native sampler enforces maxLength against a conflicting prompt, with unchanged request schema");
            Check(!runtime.IsLoaded("schema-probe") && observed.CompletionCalls == 1, "one bounded conformance generation finished and its isolated model unloaded");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class SchemaObservationHandler : DelegatingHandler
    {
        public JsonElement? Submitted { get; private set; }
        public JsonElement? GenerationSettings { get; private set; }
        public bool TerminalObserved { get; private set; }
        public int CompletionCalls { get; private set; }
        public SchemaObservationHandler() : base(new SocketsHttpHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var completion = request.RequestUri?.AbsolutePath == "/completion";
            if (completion)
            {
                CompletionCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (Encoding.UTF8.GetByteCount(body) > 65536) throw new InvalidDataException("Conformance request exceeded its diagnostic bound.");
                using var document = JsonDocument.Parse(body);
                Submitted = document.RootElement.Clone();
                var expanded = JsonNode.Parse(body)!;
                ((JsonArray)expanded["response_fields"]!).Add("generation_settings");
                request.Content.Dispose();
                request.Content = new StringContent(expanded.ToJsonString(), Encoding.UTF8, "application/json");
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (!completion) return response;
            try
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
                using var captured = new MemoryStream();
                using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[4096];
                while (await input.ReadAsync(buffer, cancellationToken) is var read && read != 0)
                {
                    if (captured.Length + read > 131072) throw new InvalidDataException("Conformance response exceeded its diagnostic bound.");
                    captured.Write(buffer, 0, read);
                }
                var text = Encoding.UTF8.GetString(captured.ToArray());
                foreach (var line in text.Split('\n'))
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") continue;
                    using var frame = JsonDocument.Parse(data);
                    if (frame.RootElement.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True)
                    {
                        TerminalObserved = true;
                        if (frame.RootElement.TryGetProperty("generation_settings", out var settings)) GenerationSettings = settings.Clone();
                    }
                }
                response.Content.Dispose();
                response.Content = new StringContent(text, Encoding.UTF8, mediaType);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
}
