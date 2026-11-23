using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Tools;

internal static class ToolPolicy
{
    internal const int MaxOutput = 32_768;
    internal const int MaxInput = 1_048_576;
    internal const string RangeEditToolName = "file.edit-range";
    internal static readonly StringComparison PathComparison = ScopedFilePolicy.PathComparison;

    public static void Grant(AgentProfile profile, string name, string capability)
    {
        // Deliberately exact grants: advertising a server or installing a tool grants no execution authority.
        if (!AllowsTool(profile, name) || !profile.Capabilities.Contains(capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"Tool '{name}' requires its exact AllowedTools entry and capability '{capability}'.");
    }
    // This private capability is an explicit opt-in; existing wildcard profiles must not
    // discover or execute it before their consumer supports its mutation bookkeeping.
    public static bool AllowsTool(AgentProfile profile, string name) =>
        profile.AllowedTools.Contains(name, StringComparer.Ordinal) ||
        name != RangeEditToolName && profile.AllowedTools.Contains("*", StringComparer.Ordinal);

    public static string PathInScope(string input, ToolExecutionContext context, bool mustExist = false) =>
        ScopedFilePolicy.PathInScope(input, context, mustExist);

    public static string PathInScope(string input, ToolExecutionContext context, ScopedPathAccess access, bool mustExist = false) =>
        ScopedFilePolicy.PathInScope(input, context, access, mustExist);

    public static bool IsWithin(string path, string root) => ScopedFilePolicy.IsWithin(path, root);

    public static void RejectLinks(string path) => ScopedFilePolicy.RejectLinks(path);

    public static string Text(JsonElement args, string name, string? fallback = null) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback ?? throw new ArgumentException($"'{name}' must be a string.");
    public static int Number(JsonElement args, string name, int fallback, int min, int max) =>
        args.TryGetProperty(name, out var value) ? value.TryGetInt32(out var n) && n >= min && n <= max ? n : throw new ArgumentException($"'{name}' must be between {min} and {max}.") : fallback;
    public static bool Boolean(JsonElement args, string name, bool fallback = false) => args.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;
    public static string[] Strings(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ?
        v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ArgumentException($"'{name}' must contain strings.")).ToArray() : [];
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static ToolResult Result(object data, bool preserveStringEnds = false)
    {
        var node = JsonSerializer.SerializeToNode(data, CerebralJson.Options)!;
        var text = node.ToJsonString(CerebralJson.Options); var truncated = false;
        while (text.Length > MaxOutput)
        {
            truncated = true;
            var strings = new List<(JsonNode Parent, string? Key, int Index, string Value)>(); var arrays = new List<JsonArray>();
            void Visit(JsonNode? current)
            {
                if (current is JsonObject obj) foreach (var pair in obj)
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var s)) strings.Add((obj, pair.Key, 0, s)); else Visit(pair.Value);
                }
                else if (current is JsonArray array)
                {
                    arrays.Add(array);
                    for (var i = 0; i < array.Count; i++) if (array[i] is JsonValue value && value.TryGetValue<string>(out var s)) strings.Add((array, null, i, s)); else Visit(array[i]);
                }
            }
            Visit(node);
            var largest = strings.OrderByDescending(x => x.Value.Length).FirstOrDefault();
            if (largest.Value is { Length: > 128 })
            {
                var length = largest.Value.Length / 2;
                // Process errors commonly occur at the end. JSON escaping may require another
                // reduction after capture; retain both ends rather than losing those diagnostics.
                const string omitted = "\n[output omitted; showing beginning and end]\n";
                var retained = length - omitted.Length;
                var reduced = preserveStringEnds
                    ? largest.Value[..(retained / 2)] + omitted + largest.Value[^(retained - retained / 2)..]
                    : largest.Value[..length];
                if (largest.Parent is JsonObject obj) obj[largest.Key!] = reduced; else ((JsonArray)largest.Parent)[largest.Index] = reduced;
            }
            else if (arrays.OrderByDescending(x => x.Count).FirstOrDefault(x => x.Count > 0) is { } array) array.RemoveAt(array.Count - 1);
            else return new(false, "Tool result envelope exceeds the output limit.", true);
            text = node.ToJsonString(CerebralJson.Options);
        }
        return new(true, text, truncated);
    }
    public static ToolResult Limit(string content, bool success = true) => content.Length <= MaxOutput ? new(success, content) : new(success, content[..MaxOutput], true);
    public static CancellationTokenSource Timeout(JsonElement args, CancellationToken ct, int fallback = 30)
    {
        // Extension policy is host-owned but must remain inside the same bounded deadline contract.
        if (fallback is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(fallback),
            "The tool's default timeout must be between 1 and 600 seconds.");
        var seconds = Number(args, "timeoutSeconds", fallback, 1, 600);
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }
    public static Encoding TextEncoding(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return new UnicodeEncoding(false, true, true);
        if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
    }
    public static string Decode(byte[] bytes)
    {
        var encoding = TextEncoding(bytes); var preamble = encoding.GetPreamble();
        return encoding.GetString(bytes, bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0, bytes.Length - (bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0));
    }
}
