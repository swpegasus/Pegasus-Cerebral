using System.Text.Json;

namespace Cerebral.Worker;

// Numeric diagnostics only. These types stay inside the host and never carry model text.
internal sealed record ProviderGenerationMeasurement(string Phase, int? InputTokens, double? InputMilliseconds,
    int? OutputTokens, double? OutputMilliseconds);
internal sealed record GenerationTelemetryBinding(string ConversationId, string RequestId, string RunId,
    string EffectId, string AgentId, string WorkerId);
internal sealed record GenerationTelemetrySample(string Schema, string LaneId, string InstanceId, string ModelId,
    string AdapterId, GenerationTelemetryBinding? Binding, string State, string Phase,
    int? InputTokens, int? OutputTokens, double? InputTokensPerSecond, double? OutputTokensPerSecond,
    long ElapsedMilliseconds, DateTimeOffset At);
internal interface IProviderGenerationTelemetry
{
    void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? observer);
}
internal interface IBoundGenerationTelemetry
{
    void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer);
}

internal static class ProviderGenerationTelemetry
{
    // Cache hits are not prompt evaluation. Measure only processed-cache/time for progress,
    // or provider prompt_n/prompt_ms and predicted_n/predicted_ms. Never estimate from characters.
    internal static ProviderGenerationMeasurement? Read(JsonElement frame)
    {
        try
        {
            var timings = Field(frame, "timings");
            int? input = null, output = null; double? inputMs = null, outputMs = null;
            if (timings is { ValueKind: JsonValueKind.Object } value)
            {
                input = Count(value, "prompt_n"); inputMs = Milliseconds(value, "prompt_ms");
                output = Count(value, "predicted_n"); outputMs = Milliseconds(value, "predicted_ms");
            }
            var progress = Field(frame, "prompt_progress");
            var phase = output is > 0 ? "output" : "unknown";
            if (progress is { ValueKind: JsonValueKind.Object } prompt)
            {
                var total = Count(prompt, "total"); var cached = Count(prompt, "cache"); var processed = Count(prompt, "processed");
                var elapsed = Milliseconds(prompt, "time_ms");
                if (total is not null && cached is not null && processed is not null &&
                    cached <= processed && processed <= total && elapsed is not null)
                {
                    input = processed - cached; inputMs = elapsed;
                    if (processed < total) phase = "prefill";
                }
                else throw new InvalidDataException();
            }
            if (input is null && output is null) return null;
            return new(phase, input, inputMs, output, outputMs);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        { return null; } // Invalid optional telemetry never changes generation adoption or slot ownership.
    }
    private static JsonElement? Field(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var fields = root.EnumerateObject().Where(x => x.NameEquals(name)).ToArray();
        if (fields.Length > 1) throw new InvalidDataException();
        return fields.Length == 1 && fields[0].Value.ValueKind != JsonValueKind.Null ? fields[0].Value : null;
    }
    private static int? Count(JsonElement root, string name)
    {
        var field = Field(root, name);
        if (field is null) return null;
        if (field is not { ValueKind: JsonValueKind.Number } value || !value.TryGetInt32(out var count) ||
            count is < 0 or > 16_777_216) throw new InvalidDataException();
        return count;
    }
    private static double? Milliseconds(JsonElement root, string name)
    {
        var field = Field(root, name);
        if (field is null) return null;
        if (field is not { ValueKind: JsonValueKind.Number } value || !value.TryGetDouble(out var count) ||
            !double.IsFinite(count) || count is < 0 or > 86_400_000) throw new InvalidDataException();
        return count;
    }
    internal static void Observe(Action<ProviderGenerationMeasurement>? observer, JsonElement frame)
    {
        if (observer is null || Read(frame) is not { } value) return;
        try { observer(value); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }
}
