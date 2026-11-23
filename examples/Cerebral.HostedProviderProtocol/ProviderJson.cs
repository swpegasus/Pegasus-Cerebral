using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.HostedProviderProtocol;

internal static class ProviderJson
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void Validate(JsonElement value, int maximumUtf8Bytes)
    {
        if (Utf8.GetByteCount(value.GetRawText()) > maximumUtf8Bytes)
            throw new InvalidDataException("The provider value exceeds its byte bound.");
        Check(value, 0);
    }
    private static void Check(JsonElement value, int depth)
    {
        if (depth > 32) throw new InvalidDataException("The provider value exceeds its depth bound.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException("The provider value contains duplicate fields.");
                _ = Utf8.GetByteCount(item.Name); Check(item.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Check(item, depth + 1);
        else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble()))
            throw new InvalidDataException("The provider value contains an invalid number.");
    }
    public static byte[] Canonical(JsonElement value)
    {
        Validate(value, 1_048_576);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(item.Name); Write(writer, item.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("The provider value is not JSON.");
        }
    }
    public static string Fingerprint(string domain, JsonElement value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(domain + "\n"));
        hash.AppendData(Canonical(value)); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static string PortableFingerprint(JsonElement binding, JsonElement request)
        => Fingerprint("Cerebral.ModelProvider.Request/1", JsonSerializer.SerializeToElement(new { binding, request }));
    public static string RenderedFingerprint(string protocolId, JsonElement modelRequest)
        => Fingerprint("Cerebral.ModelProvider.RenderedRequest/1", JsonSerializer.SerializeToElement(new { protocolId, modelRequest }));
    public static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A provider object is required.");
        var actual = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("The provider fields do not match the value contract.");
    }
    public static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        Validate(document.RootElement, 1_048_576); return document.RootElement.Clone();
    }
}

/// <summary>Generic explicit JSON-string encoding. Original schema validation stays with the consumer.</summary>
internal static class JsonStringCarrier
{
    public static JsonElement Schema => ProviderJson.Parse(
        "{\"type\":\"object\",\"properties\":{\"valueJson\":{\"type\":\"string\"}},\"required\":[\"valueJson\"],\"additionalProperties\":false}");
    public static string Guidance(JsonElement originalSchema)
    {
        ProviderJson.Validate(originalSchema, 65_536);
        if (originalSchema.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A carrier schema object is required.");
        return "Return one JSON object with exactly one field named valueJson. Its value is a JSON-encoded string containing the requested JSON value. " +
            "The inner value must follow this original schema; the consumer validates it independently:\n" +
            Encoding.UTF8.GetString(ProviderJson.Canonical(originalSchema));
    }
    public static string Decode(string text, int maximumUtf8Bytes)
    {
        var value = ProviderJson.Parse(text); ProviderJson.Fields(value, "valueJson");
        if (value.GetProperty("valueJson").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The JSON-string carrier has an invalid value.");
        var inner = value.GetProperty("valueJson").GetString()!;
        if (ProviderJson.Utf8.GetByteCount(inner) > maximumUtf8Bytes)
            throw new InvalidDataException("The decoded carrier exceeds its byte bound.");
        _ = ProviderJson.Parse(inner); // Syntax only: no interpretation of domain/action/private schema.
        return inner;
    }
}
