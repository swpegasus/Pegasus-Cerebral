using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Desktop;

public sealed record GenerationThroughputPresentation(string Input, string Output, int ActiveLanes,
    int MeasuredLanes, string Details);

public static class GenerationThroughput
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public static GenerationThroughputPresentation Project(JsonElement value)
    {
        try
        {
            if (value.GetRawText().Length > 256 * 1024 || HasDuplicateFields(value)) throw new JsonException();
            var snapshot = value.Deserialize<GenerationMetricsEnvelope>(Json);
            if (snapshot is null || snapshot.Schema != "cerebral.generation-metrics/1" || snapshot.At == default ||
                snapshot.Active is null || snapshot.Recent is null || snapshot.Active.Count > 128 || snapshot.Recent.Count > 128 ||
                snapshot.Active.Any(item => !Valid(item) || item.State != "active") ||
                snapshot.Active.Select(item => item.LaneId).Distinct(StringComparer.Ordinal).Count() != snapshot.Active.Count) throw new JsonException();
            var active = snapshot.Active;
            var now = DateTimeOffset.UtcNow;
            var inputs = active.Where(item => item.Phase == "prefill" && now - item.At <= TimeSpan.FromSeconds(2) &&
                item.InputTokensPerSecond is not null).ToArray();
            var outputs = active.Where(item => item.Phase == "output" && now - item.At <= TimeSpan.FromSeconds(2) &&
                item.OutputTokensPerSecond is not null).ToArray();
            var unknown = active.Count - inputs.Length - outputs.Length;
            string Display(double total) => total.ToString("0.#", CultureInfo.InvariantCulture) +
                (unknown > 0 || snapshot.Truncated ? "+" : "");
            var details = "Current total across active measured generation lanes only. Completed/recent samples never contribute to current throughput. " +
                "Input is actual prompt evaluation (cache hits excluded); output includes all generated tokens, including protected reasoning. " +
                "Each rate is a provider-token/provider-time delta over the latest published interval. Missing, stale or remote telemetry is unavailable. " +
                $"Active lanes: {active.Count}; measured lanes: {inputs.Length + outputs.Length}; unavailable lanes: {unknown}.";
            return new(active.Count == 0 ? "0" : inputs.Length == 0 && unknown > 0 ? "—" : Display(inputs.Sum(x => x.InputTokensPerSecond!.Value)),
                active.Count == 0 ? "0" : outputs.Length == 0 && unknown > 0 ? "—" : Display(outputs.Sum(x => x.OutputTokensPerSecond!.Value)),
                active.Count, inputs.Length + outputs.Length, details);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { return new("—", "—", 0, 0, "Live generation throughput is unavailable from this Worker."); }
    }
    internal static bool Valid(GenerationMetricSample? value) => value is not null &&
        value.Schema == "cerebral.generation-metrics/1" && Identifier(value.LaneId) && Identifier(value.InstanceId) &&
        Identifier(value.ModelId) && Identifier(value.AdapterId) &&
        value.State is "active" or "Stop" or "Length" or "Cancelled" or "Unknown" &&
        value.Phase is "unknown" or "prefill" or "output" &&
        value.InputTokens is null or >= 0 and <= 16_777_216 &&
        value.OutputTokens is null or >= 0 and <= 16_777_216 &&
        Rate(value.InputTokensPerSecond) && Rate(value.OutputTokensPerSecond) &&
        (value.Phase == "prefill" || value.InputTokensPerSecond is null) &&
        (value.Phase == "output" || value.OutputTokensPerSecond is null) &&
        value.ElapsedMilliseconds is >= 0 and <= 86_400_000 && value.At != default &&
        (value.Binding is null || Identifier(value.Binding.ConversationId) && Identifier(value.Binding.RequestId) &&
            Identifier(value.Binding.RunId) && Identifier(value.Binding.EffectId) &&
            Identifier(value.Binding.AgentId) && Identifier(value.Binding.WorkerId));
    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
    private static bool Rate(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100_000_000;
    internal static bool HasDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateFields(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) if (HasDuplicateFields(child)) return true;
        return false;
    }
}
internal sealed record GenerationMetricBinding(string ConversationId, string RequestId, string RunId,
    string EffectId, string AgentId, string WorkerId);
internal sealed record GenerationMetricSample(string Schema, string LaneId, string InstanceId, string ModelId,
    string AdapterId, GenerationMetricBinding? Binding, string State, string Phase,
    int? InputTokens, int? OutputTokens, double? InputTokensPerSecond, double? OutputTokensPerSecond,
    long ElapsedMilliseconds, DateTimeOffset At);
internal sealed record GenerationMetricsEnvelope(string Schema, DateTimeOffset At,
    IReadOnlyList<GenerationMetricSample> Active, IReadOnlyList<GenerationMetricSample> Recent, bool Truncated);
