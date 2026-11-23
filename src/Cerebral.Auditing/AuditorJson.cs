using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cerebral.Auditing;

public sealed class AuditorContractException(string message) : ArgumentException(message);

internal static partial class AuditorJson
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static JsonObject ParseObject(string json, string name)
    {
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new AuditorContractException(name + " exceeds 1 MiB.");
        try { return JsonNode.Parse(json) as JsonObject ?? throw new AuditorContractException(name + " must be an object."); }
        catch (JsonException ex) { throw new AuditorContractException(name + " is not valid JSON: " + ex.Message); }
    }

    public static JsonObject Object(JsonNode? node, string name) =>
        node as JsonObject ?? throw new AuditorContractException(name + " must be an object.");

    public static JsonArray Array(JsonNode? node, string name) =>
        node as JsonArray ?? throw new AuditorContractException(name + " must be an array.");

    public static void Fields(JsonObject value, string name, IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        optional ??= System.Array.Empty<string>();
        var unknown = value.Select(item => item.Key).Except(required, StringComparer.Ordinal)
            .Except(optional, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = required.Where(field => !value.ContainsKey(field)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0) throw new AuditorContractException(name + " has unsupported fields: " + string.Join(", ", unknown));
        if (missing.Length != 0) throw new AuditorContractException(name + " is missing fields: " + string.Join(", ", missing));
    }

    public static string Text(JsonNode? node, string name, int maximum = 512)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) ||
            text.Length > maximum || text.Contains('\0'))
            throw new AuditorContractException(name + " must be nonempty bounded text.");
        return text;
    }

    public static string? OptionalText(JsonNode? node, string name, int maximum = 512) =>
        node is null ? null : Text(node, name, maximum);

    public static string Identifier(JsonNode? node, string name)
    {
        var value = Text(node, name, 128);
        if (!IdentifierPattern().IsMatch(value)) throw new AuditorContractException(name + " is not a valid stable identifier.");
        return value;
    }

    public static string? OptionalIdentifier(JsonNode? node, string name) => node is null ? null : Identifier(node, name);

    public static string Sha256(JsonNode? node, string name)
    {
        var value = Text(node, name, 64);
        if (!Sha256Pattern().IsMatch(value)) throw new AuditorContractException(name + " must be a lowercase SHA-256.");
        return value;
    }

    public static bool Boolean(JsonNode? node, string name)
    {
        if (node is not JsonValue value || !value.TryGetValue<bool>(out var result))
            throw new AuditorContractException(name + " must be a boolean.");
        return result;
    }

    public static int Integer(JsonNode? node, string name, int minimum = 0)
    {
        if (node is not JsonValue value || !value.TryGetValue<int>(out var result) || result < minimum)
            throw new AuditorContractException(name + " must be an integer >= " + minimum + ".");
        return result;
    }

    public static long Long(JsonNode? node, string name, long minimum = 0)
    {
        if (node is not JsonValue value || !value.TryGetValue<long>(out var result) || result < minimum)
            throw new AuditorContractException(name + " must be an integer >= " + minimum + ".");
        return result;
    }

    public static double Number(JsonNode? node, string name)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var result) || !double.IsFinite(result))
            throw new AuditorContractException(name + " must be a finite number.");
        return result;
    }

    public static string Enum(JsonNode? node, string name, IReadOnlyCollection<string> allowed)
    {
        var value = Text(node, name);
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new AuditorContractException(name + " is not a supported value.");
        return value;
    }

    public static IReadOnlyList<string> Identifiers(JsonNode? node, string name, bool allowEmpty = false) =>
        Strings(node, name, item => Identifier(item, name + " item"), allowEmpty);

    public static IReadOnlyList<string> Texts(JsonNode? node, string name, bool allowEmpty = false, int maximum = 256) =>
        Strings(node, name, item => Text(item, name + " item", maximum), allowEmpty);

    public static IReadOnlyList<string> Enums(JsonNode? node, string name, IReadOnlyCollection<string> allowed,
        bool allowEmpty = false) => Strings(node, name, item => Enum(item, name + " item", allowed), allowEmpty);

    private static IReadOnlyList<string> Strings(JsonNode? node, string name, Func<JsonNode?, string> convert, bool allowEmpty)
    {
        var array = Array(node, name);
        if (!allowEmpty && array.Count == 0) throw new AuditorContractException(name + " must not be empty.");
        var result = array.Select(convert).ToArray();
        if (result.Length != result.Distinct(StringComparer.Ordinal).Count())
            throw new AuditorContractException(name + " contains duplicates.");
        return result;
    }

    public static string Canonical(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, node);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash(JsonNode node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node))));

    public static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var item in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
                { writer.WritePropertyName(item.Key); WriteCanonical(writer, item.Value); }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray(); foreach (var item in array) WriteCanonical(writer, item); writer.WriteEndArray();
                break;
            default: node.WriteTo(writer); break;
        }
    }
}
