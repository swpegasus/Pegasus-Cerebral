using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

internal sealed class OwnerSnapshotUnavailableException(string code) : IOException("Owner model inspection is unavailable: " + code + ".")
{
    internal string Code { get; } = code;
}

internal static class OwnerSnapshotJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static JsonDocument Parse(byte[] bytes, int maximumBytes)
    {
        if (bytes is null || bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new OwnerSnapshotUnavailableException("document_byte_bound");
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            ValidateEscapedScalars(bytes);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
        {
            throw new OwnerSnapshotUnavailableException("invalid_json_or_unicode");
        }
    }

    internal static Dictionary<string, JsonElement> Fields(JsonElement value, IReadOnlySet<string>? allowed = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new OwnerSnapshotUnavailableException("object_required");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateObject())
        {
            if (!fields.TryAdd(item.Name, item.Value) || !aliases.Add(item.Name))
                throw new OwnerSnapshotUnavailableException("duplicate_or_case_alias_field");
            if (allowed is not null && !allowed.Contains(item.Name))
                throw new OwnerSnapshotUnavailableException("unknown_field");
        }
        return fields;
    }

    internal static JsonElement Required(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : throw new OwnerSnapshotUnavailableException("required_field_missing");

    internal static string Text(Dictionary<string, JsonElement> fields, string name, int maximum, bool nullable, out bool isNull)
    {
        var value = Required(fields, name);
        isNull = value.ValueKind == JsonValueKind.Null;
        if (isNull && nullable) return "";
        if (value.ValueKind != JsonValueKind.String) throw new OwnerSnapshotUnavailableException("string_type_required");
        var text = value.GetString()!;
        if (text.Length > maximum) throw new OwnerSnapshotUnavailableException("string_bound");
        return text;
    }

    internal static int Integer(Dictionary<string, JsonElement> fields, string name)
    {
        var value = Required(fields, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new OwnerSnapshotUnavailableException("integer_type_required");
        return number;
    }

    internal static bool Boolean(Dictionary<string, JsonElement> fields, string name)
    {
        var value = Required(fields, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new OwnerSnapshotUnavailableException("boolean_type_required");
        return value.GetBoolean();
    }

    internal static (RemotePreparedModelBinding Binding, string State) Binding(OwnerSnapshotRawModelRow row)
    {
        using var document = Parse(row.Body, OwnerModelSnapshotCodec.MaximumModelBodyBytes);
        var fields = Fields(document.RootElement);
        if (fields.ContainsKey("schemaVersion") && Integer(fields, "schemaVersion") != 1)
            throw new OwnerSnapshotUnavailableException("registration_schema_unsupported");
        var id = Text(fields, "id", 128, false, out _);
        if (id != row.Id) throw new OwnerSnapshotUnavailableException("storage_body_id_mismatch");
        OwnerModelSnapshotCodec.ValidateIdentifier(id);
        var path = Text(fields, "path", 32768, false, out _);
        var template = Text(fields, "chatTemplate", 65536, true, out var templateNull);
        var projector = Text(fields, "visionProjectorPath", 32768, true, out var projectorNull);
        var state = Text(fields, "state", 64, false, out _);
        if (state.Length == 0 || state.Any(char.IsControl)) throw new OwnerSnapshotUnavailableException("invalid_persisted_state");
        var array = Required(fields, "stopSequences");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 128)
            throw new OwnerSnapshotUnavailableException("stop_array_required");
        var stops = new List<string>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw new OwnerSnapshotUnavailableException("stop_string_required");
            stops.Add(value.GetString()!);
        }
        var binding = new RemotePreparedModelBinding(id, path, templateNull ? null : template, stops.AsReadOnly(),
            Integer(fields, "maximumContextTokens"), Integer(fields, "parallelRequests"), Integer(fields, "gpuLayers"),
            projectorNull ? null : projector);
        try { binding.Validate(); }
        catch (ArgumentException) { throw new OwnerSnapshotUnavailableException("invalid_binding_contract"); }
        return (binding, state);
    }

    private static void ValidateEscapedScalars(ReadOnlySpan<byte> bytes)
    {
        var inside = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'"') { inside = !inside; continue; }
            if (!inside || bytes[index] != (byte)'\\') continue;
            if (++index >= bytes.Length) return; // JSON framing validation supplies the classified failure.
            if (bytes[index] != (byte)'u') continue;
            var value = Hex4(bytes, index + 1);
            if (value is >= 0xdc00 and <= 0xdfff)
                throw new OwnerSnapshotUnavailableException("unpaired_escaped_surrogate");
            if (value is >= 0xd800 and <= 0xdbff)
            {
                if (index + 10 >= bytes.Length || bytes[index + 5] != (byte)'\\' || bytes[index + 6] != (byte)'u' ||
                    Hex4(bytes, index + 7) is < 0xdc00 or > 0xdfff)
                    throw new OwnerSnapshotUnavailableException("unpaired_escaped_surrogate");
                index += 10;
            }
            else index += 4;
        }
    }

    private static int Hex4(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length) throw new OwnerSnapshotUnavailableException("invalid_unicode_escape");
        var result = 0;
        for (var index = offset; index < offset + 4; index++)
        {
            var current = bytes[index];
            var digit = current is >= (byte)'0' and <= (byte)'9' ? current - '0' :
                current is >= (byte)'a' and <= (byte)'f' ? current - 'a' + 10 :
                current is >= (byte)'A' and <= (byte)'F' ? current - 'A' + 10 : -1;
            if (digit < 0) throw new OwnerSnapshotUnavailableException("invalid_unicode_escape");
            result = result * 16 + digit;
        }
        return result;
    }
}
