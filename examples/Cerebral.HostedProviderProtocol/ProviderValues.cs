using System.Text.Json;
namespace Cerebral.HostedProviderProtocol;

// Source-linked, package-private data; no shared CLR configuration object crosses a plugin boundary.
internal sealed record ProviderModel(JsonElement Declaration);
internal sealed record ProviderProfile(string Id, int Revision, bool Enabled, string? CredentialReferenceId,
    int? CredentialReferenceRevision, IReadOnlyList<ProviderModel> Models, int ConfiguredMaxConcurrentRequests);
internal sealed record ProviderDependencies(HttpMessageHandler Handler,
    Func<string, int, CancellationToken, ValueTask<string?>> ResolveCredential, Action<JsonElement>? PrivateFragmentSink);

internal sealed class ProviderFailure(string code) : Exception("The hosted provider request was rejected.")
{ internal string Code { get; } = code; }

internal static class ValueContract
{
    internal static string Text(JsonElement value, string key, int max, bool empty = false)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text ||
            (!empty && text.Length == 0) || text.Length > max || text.Any(char.IsControl))
            throw new InvalidDataException("A provider identity value is invalid.");
        _ = ProviderJson.Utf8.GetByteCount(text); return text;
    }
    internal static int Integer(JsonElement value, string key, int minimum, int maximum)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException("A provider integer value is invalid.");
        return number;
    }
    internal static bool Flag(JsonElement value, string key) => value.GetProperty(key).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("A provider boolean value is invalid.") };
    internal static string Hex(JsonElement value, string key, int length)
    {
        var text = Text(value, key, length);
        if (text.Length != length || text.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new InvalidDataException("A provider fingerprint is invalid.");
        return text;
    }
    internal static void Version(JsonElement value) => _ = Integer(value, "schemaVersion", 1, 1);
    internal static string? NullableText(JsonElement value, string key, int max)
        => value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(value, key, max);
    internal static int? NullableInteger(JsonElement value, string key, int max)
        => value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Integer(value, key, 1, max);
    internal static string Enum(JsonElement value, string key, params string[] choices)
    {
        var text = Text(value, key, 128);
        if (!choices.Contains(text, StringComparer.Ordinal)) throw new InvalidDataException("A provider enum value is invalid.");
        return text;
    }
    internal static void Configuration(JsonElement value)
    {
        ProviderJson.Fields(value, "id", "revision");
        _ = Text(value, "id", 128); _ = Integer(value, "revision", 1, int.MaxValue);
    }
    internal static void Terminal(JsonElement value)
    {
        ProviderJson.Validate(value, 131_072);
        ProviderJson.Fields(value, "schemaVersion", "callId", "requestFingerprint", "providerRequestFingerprint", "outcome",
            "requestSent", "terminalConfirmed", "cancellationConfirmed", "text", "schemaGuarantee", "usage", "receipt", "failureCode", "outputFormat");
        Version(value); _ = Hex(value, "callId", 32); _ = Hex(value, "requestFingerprint", 64);
        if (value.GetProperty("providerRequestFingerprint").ValueKind != JsonValueKind.Null) _ = Hex(value, "providerRequestFingerprint", 64);
        _ = Enum(value, "outcome", "completed", "length", "refused", "failed", "cancelled", "unknown");
        _ = Flag(value, "requestSent"); _ = Flag(value, "terminalConfirmed"); _ = Flag(value, "cancellationConfirmed");
        if (value.GetProperty("text").ValueKind != JsonValueKind.String ||
            ProviderJson.Utf8.GetByteCount(value.GetProperty("text").GetString()!) > 98_304)
            throw new InvalidDataException("The previous terminal text is invalid.");
        _ = Enum(value, "schemaGuarantee", "none", "supportedObjectSchema", "carrierOnly");
        _ = Enum(value, "outputFormat", "text", "objectJsonSchema", "jsonStringCarrier");
        if (value.GetProperty("failureCode").ValueKind != JsonValueKind.Null)
            _ = Enum(value, "failureCode", "configurationMissing", "configurationStale", "packageUnavailable", "capabilityUnsupported",
                "schemaUnsupported", "countUnavailable", "contextLimit", "outputLimit", "rateLimited", "providerUnavailable",
                "protocolMalformed", "cancelledBeforeDispatch", "uncertainOutcome", "providerRefusal");
        var usage = value.GetProperty("usage");
        ProviderJson.Fields(usage, "inputTokens", "totalOutputTokens", "visibleOutputTokens", "reasoningTokens", "cacheReadInputTokens", "cacheWriteInputTokens");
        foreach (var item in usage.EnumerateObject())
            if (item.Value.ValueKind != JsonValueKind.Null) _ = Integer(usage, item.Name, 0, int.MaxValue);
        var receipt = value.GetProperty("receipt"); ProviderJson.Fields(receipt, "requestId", "responseId", "actualModel");
        foreach (var item in receipt.EnumerateObject()) _ = NullableText(receipt, item.Name, 256);
    }

    internal static ProviderProfile Profile(JsonElement value)
    {
        ProviderJson.Validate(value, 65_536);
        ProviderJson.Fields(value, "id", "revision", "enabled", "credentialReferenceId", "credentialReferenceRevision", "models", "configuredMaxConcurrentRequests");
        var id = Text(value, "id", 128); var revision = Integer(value, "revision", 1, int.MaxValue);
        var credential = NullableText(value, "credentialReferenceId", 128);
        var credentialRevision = NullableInteger(value, "credentialReferenceRevision", int.MaxValue);
        if ((credential is null) != (credentialRevision is null)) throw new InvalidDataException("An exact credential reference revision is required.");
        var models = value.GetProperty("models");
        if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 128) throw new InvalidDataException("The model declaration list is invalid.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            ProviderJson.Fields(model, "key", "label", "revision", "content", "countQuality", "effortValues", "supportsReasoningOff",
                "supportsTemperature", "declaredContextTokens", "declaredMaximumOutputTokens", "limitProvenance", "responseFormats");
            if (!keys.Add(Text(model, "key", 128))) throw new InvalidDataException("Model declaration keys must be unique.");
            _ = Text(model, "label", 256); _ = NullableText(model, "revision", 128);
            StringArray(model, "content", 1, 1, 4, ["text"]);
            StringArray(model, "effortValues", 0, 16, 32);
            StringArray(model, "responseFormats", 1, 3, 32, ["text", "objectJsonSchema", "jsonStringCarrier"]);
            _ = Enum(model, "countQuality", "providerReported", "providerEstimate", "unavailable");
            _ = Flag(model, "supportsReasoningOff"); _ = Flag(model, "supportsTemperature");
            _ = NullableInteger(model, "declaredContextTokens", 1_048_576);
            _ = NullableInteger(model, "declaredMaximumOutputTokens", 131_072);
            _ = Enum(model, "limitProvenance", "providerDocumentation", "providerReported", "configured", "unknown");
        }
        return new(id, revision, Flag(value, "enabled"), credential, credentialRevision,
            Array.AsReadOnly(models.EnumerateArray().Select(x => new ProviderModel(x.Clone())).ToArray()),
            Integer(value, "configuredMaxConcurrentRequests", 1, 16));
    }
    private static void StringArray(JsonElement value, string key, int min, int max, int length, string[]? choices = null)
    {
        var items = value.GetProperty(key);
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() < min || items.GetArrayLength() > max)
            throw new InvalidDataException("A provider declaration array is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } s || s.Length is < 1 || s.Length > length ||
                s.Any(char.IsControl) || !seen.Add(s) || choices is not null && !choices.Contains(s, StringComparer.Ordinal))
                throw new InvalidDataException("A provider declaration item is invalid.");
    }
}

// A deliberately small common supported subset. Unsupported constraints are rejected, never removed.
internal static class ObjectSchema
{
    internal static void Validate(JsonElement schema)
    {
        ProviderJson.Validate(schema, 65_536); Check(schema, 0);
        if (schema.GetProperty("type").ValueKind != JsonValueKind.String || schema.GetProperty("type").GetString() != "object")
            throw new ProviderFailure("schemaUnsupported");
    }
    private static void Check(JsonElement schema, int depth)
    {
        if (depth > 16 || schema.ValueKind != JsonValueKind.Object) throw new ProviderFailure("schemaUnsupported");
        var type = schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var allowed = new HashSet<string>(["type", "description", "title", "enum"], StringComparer.Ordinal);
        if (type == "object") allowed.UnionWith(["properties", "required", "additionalProperties"]);
        if (type == "array") allowed.Add("items");
        if (type is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null") ||
            schema.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ProviderFailure("schemaUnsupported");
        foreach (var key in new[] { "description", "title" })
            if (schema.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.String) throw new ProviderFailure("schemaUnsupported");
        if (schema.TryGetProperty("enum", out var e) && (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() is < 1 or > 128 ||
            e.EnumerateArray().Any(v => v.ValueKind is JsonValueKind.Array or JsonValueKind.Object)))
            throw new ProviderFailure("schemaUnsupported");
        if (type == "array") { if (!schema.TryGetProperty("items", out var item)) throw new ProviderFailure("schemaUnsupported"); Check(item, depth + 1); }
        if (type != "object") return;
        if (!schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array)
            throw new ProviderFailure("schemaUnsupported");
        var names = props.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length > 128 || req.GetArrayLength() != names.Length ||
            req.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String) ||
            req.EnumerateArray().Select(r => r.GetString()!).Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(n => !req.EnumerateArray().Any(r => r.GetString() == n))) throw new ProviderFailure("schemaUnsupported");
        foreach (var prop in props.EnumerateObject()) Check(prop.Value, depth + 1);
    }
}
