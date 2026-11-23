using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cerebral.Auditing.Laya;

public sealed record LayaReadiness(string PackageVersion, string PackageWheelSha256, string CheckpointRevision,
    string WeightsSha256, string OriginalInputsSha256, string EffectiveInputsSha256, string DependencyLockSha256,
    string RunnerSha256, string RendererSha256, string RuntimeIdentity, string Device, bool CudaVisible,
    int TorchThreads, int InteropThreads, long ResidentMemoryBytes, long PeakMemoryBytes, long PrivateMemoryBytes,
    long PeakPrivateBytes, double LoadMilliseconds, double Temperature, string ReadinessIdentity,
    string QuestionDefinition, string QuestionSha256, IReadOnlyList<string> FixedOptionOrder,
    int MaximumInputTokens, int HeadMaximumTokens);

internal sealed record LayaPreparation(string StateSha256, string HostContextSha256, string QuestionSha256, string EncodedIdsSha256,
    int ContextTokens, int HostContextTokens, int InputTokens, int HeadTokens, IReadOnlyList<int> MarkerPositions, string ReadinessIdentity);
internal sealed record LayaPrediction(LayaPreparation Preparation, string Choice, IReadOnlyList<double> Probabilities,
    double Confidence, double AnswerConfidence, double ActionProbability, double ElapsedMilliseconds, double Temperature);
/// <summary>Validated provider measurements only; absent counts remain unknown. No packet text is retained.</summary>
public sealed record LayaRejectionDiagnostic(string Code, int? ContextTokens, int? HostContextTokens, int? InputTokens)
{
    public string? RequestId { get; init; }
    internal string Summary()
    {
        static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        return $"Native Laya rejected {Code}; contextTokens={Count(ContextTokens)}, hostContextTokens={Count(HostContextTokens)}, inputTokens={Count(InputTokens)}. No assessment was accepted.";
    }
}
internal sealed class LayaRejectedException(string code, int? contextTokens = null, int? hostContextTokens = null, int? inputTokens = null) : IOException(code)
{
    public string Code => Diagnostic.Code;
    public LayaRejectionDiagnostic Diagnostic { get; } = new(code, contextTokens, hostContextTokens, inputTokens);
}

internal static class LayaProtocol
{
    public const string Version = "cerebral.laya.cpu/1";
    public const int MaximumLineBytes = 65_536;
    public const string Question = "{\"t\":\"choice\",\"ins\":\"Assess the explicit criterion using the recorded observations; absent criteria or decisive evidence mean insufficient_evidence.\",\"crit\":{\"met\":\"Recorded evidence supports the explicit criterion.\",\"gap\":\"Recorded evidence contradicts the explicit criterion.\",\"insufficient_evidence\":\"The criterion or decisive evidence is missing.\"}}";
    public static readonly string[] Options = ["met", "gap", "insufficient_evidence"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool IsHash(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    public static string Canonical(object value)
    {
        using var source = JsonDocument.Parse(Serialize(value));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, source.RootElement);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); Write(writer, item.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    public static JsonDocument Parse(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes) throw new InvalidDataException("Laya protocol frame exceeds its byte bound.");
        var document = JsonDocument.Parse(line, new() { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            { if (!keys.Add(item.Name)) throw new InvalidDataException("Laya protocol has duplicated fields."); Unique(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    public static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(x => x.Name).Except(names, StringComparer.Ordinal).Any() ||
            names.Any(name => !value.TryGetProperty(name, out _))) throw new InvalidDataException("Laya protocol frame has missing or unknown fields.");
    }
    public static string Text(JsonElement value, string name, int maximum = 256)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("Laya protocol text field is invalid.");
        return text;
    }
    public static string Digest(JsonElement value, string name)
    { var text = Text(value, name, 64); return IsHash(text) ? text : throw new InvalidDataException("Laya protocol digest is invalid."); }
    public static long Integer(JsonElement value, string name, long minimum = 0, long maximum = long.MaxValue)
    {
        if (!value.TryGetProperty(name, out var field) || !field.TryGetInt64(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException("Laya protocol integer is invalid.");
        return number;
    }
    public static double Number(JsonElement value, string name, double minimum = 0, double maximum = double.MaxValue)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < minimum || number > maximum) throw new InvalidDataException("Laya protocol measurement is invalid.");
        return number;
    }
    public static void Envelope(JsonElement value, string kind, string? id = null)
    {
        if (Text(value, "protocol") != Version || Text(value, "kind") != kind || id is not null && Text(value, "id", 128) != id)
            throw new InvalidDataException("Laya protocol response does not match its exact request.");
    }
    public static void Rejection(JsonElement value, string id)
    {
        if (value.TryGetProperty("kind", out var kind) && kind.GetString() == "rejected")
        {
            var allowed = new[] { "protocol", "kind", "id", "errorCode", "contextTokens", "hostContextTokens", "inputTokens" };
            if (value.EnumerateObject().Select(x => x.Name).Except(allowed).Any()) throw new InvalidDataException("Laya rejection has unknown fields.");
            Envelope(value, "rejected", id);
            var code = Text(value, "errorCode", 64);
            if (!code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new InvalidDataException("Laya rejection code is invalid.");
            int? Count(string name) => value.TryGetProperty(name, out var count) && count.ValueKind != JsonValueKind.Null
                ? checked((int)Integer(value, name, 0, 1_048_576)) : null;
            throw new LayaRejectedException(code, Count("contextTokens"), Count("hostContextTokens"), Count("inputTokens"));
        }
    }
    public static LayaReadiness Ready(JsonElement value, LayaConfiguration config)
    {
        Fields(value, "protocol", "kind", "packageVersion", "packageWheelSha256", "checkpointRevision", "weightsSha256",
            "originalInputsSha256", "effectiveInputsSha256", "dependencyLockSha256", "runnerSha256", "rendererSha256",
            "runtimeIdentity", "device", "cudaVisible", "torchThreads", "interopThreads", "residentMemoryBytes", "peakMemoryBytes",
            "privateMemoryBytes", "peakPrivateBytes", "loadMilliseconds", "temperature", "readinessIdentity", "questionDefinition",
            "questionSha256", "fixedoptionOrder", "maximumInputTokens", "headMaximumTokens");
        Envelope(value, "ready");
        var result = new LayaReadiness(Text(value, "packageVersion"), Digest(value, "packageWheelSha256"), Text(value, "checkpointRevision"),
            Digest(value, "weightsSha256"), Digest(value, "originalInputsSha256"), Digest(value, "effectiveInputsSha256"),
            Digest(value, "dependencyLockSha256"), Digest(value, "runnerSha256"), Digest(value, "rendererSha256"), Digest(value, "runtimeIdentity"),
            Text(value, "device"), value.GetProperty("cudaVisible").GetBoolean(), (int)Integer(value, "torchThreads", 1, 16),
            (int)Integer(value, "interopThreads", 1, 4), Integer(value, "residentMemoryBytes", 1), Integer(value, "peakMemoryBytes", 1),
            Integer(value, "privateMemoryBytes", 1), Integer(value, "peakPrivateBytes", 1), Number(value, "loadMilliseconds"),
            Number(value, "temperature", .01, 100), Digest(value, "readinessIdentity"), Text(value, "questionDefinition", 4096),
            Digest(value, "questionSha256"), value.GetProperty("fixedoptionOrder").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            (int)Integer(value, "maximumInputTokens", 1, 512), (int)Integer(value, "headMaximumTokens", 1, 256));
        if (result.PackageVersion != LayaConfiguration.PackageVersion || result.PackageWheelSha256 != LayaConfiguration.WheelSha256 ||
            result.CheckpointRevision != LayaConfiguration.CheckpointRevision || result.WeightsSha256 != LayaConfiguration.WeightsSha256 ||
            result.OriginalInputsSha256 != config.OriginalInputsSha256 || result.EffectiveInputsSha256 != config.EffectiveInputsSha256 ||
            result.DependencyLockSha256 != config.DependencyLockSha256 || result.RunnerSha256 != config.RunnerSha256 ||
            result.RendererSha256 != config.RendererSha256 ||
            result.Device != "cpu" || result.CudaVisible || result.TorchThreads != config.TorchThreads || result.InteropThreads != config.InteropThreads ||
            result.QuestionDefinition != Question || result.QuestionSha256 != Hash(Question) || !result.FixedOptionOrder.SequenceEqual(Options) ||
            result.MaximumInputTokens != 512 || result.HeadMaximumTokens != 256 || result.PeakMemoryBytes < result.ResidentMemoryBytes ||
            result.PeakPrivateBytes < result.PrivateMemoryBytes)
            throw new InvalidDataException("Laya readiness does not match the frozen stock CPU admission.");
        var excluded = new[] { "readinessIdentity", "residentMemoryBytes", "peakMemoryBytes", "privateMemoryBytes", "peakPrivateBytes", "loadMilliseconds" };
        var identity = value.EnumerateObject().Where(p => !excluded.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone());
        if (Hash(Canonical(identity)) != result.ReadinessIdentity) throw new InvalidDataException("Laya readiness identity digest does not match its immutable measurements.");
        return result;
    }
    public static LayaPreparation Prepared(JsonElement value, string id, string state, string hostContext, LayaReadiness ready, string kind = "prepared")
    {
        Rejection(value, id); Envelope(value, kind, id);
        var result = new LayaPreparation(Digest(value, "stateSha256"), Digest(value, "hostContextSha256"), Digest(value, "questionSha256"), Digest(value, "encodedIdsSha256"),
            (int)Integer(value, "contextTokens", 1, 512), (int)Integer(value, "hostContextTokens", 1, 1_048_576),
            (int)Integer(value, "inputTokens", 1, 512), (int)Integer(value, "headTokens", 1, 256),
            value.GetProperty("markerPositions").EnumerateArray().Select(x => x.GetInt32()).ToArray(), Digest(value, "readinessIdentity"));
        if (result.StateSha256 != Hash(state) || result.HostContextSha256 != Hash(hostContext) || result.QuestionSha256 != ready.QuestionSha256 || result.ReadinessIdentity != ready.ReadinessIdentity ||
            result.InputTokens < result.ContextTokens || result.MarkerPositions.Count != 3 || result.MarkerPositions[0] < 0 ||
            result.MarkerPositions[0] >= result.MarkerPositions[1] || result.MarkerPositions[1] >= result.MarkerPositions[2] ||
            result.MarkerPositions[2] >= result.InputTokens)
            throw new InvalidDataException("Laya tokenizer preflight is not bound to this complete untruncated criterion.");
        return result;
    }
}
