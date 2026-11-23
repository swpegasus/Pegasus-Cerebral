using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    private const string AssetContract = "pegasus.assets/2";
    private const int AssetResourceBytes = 256 * 1024;
    private sealed record AssetSelector(string Uri, string? Namespace, string? Ref, bool Bytes, long Offset, int Length, int Limit);

    /// <summary>Trusted owner infrastructure only; passive asset resources are absent from
    /// model discovery and never fall back to HTTP, MCP stdio, tools or filesystem reads.</summary>
    public async Task<JsonElement> ReadCortexAssetResourceAsync(string serverId, string uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selector = ParseAssetSelector(uri);
        if (string.IsNullOrEmpty(serverId) || serverId.Length > 64 ||
            serverId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("A valid existing Cortex provider ID is required.", nameof(serverId));
        var matches = _settings().McpServers.Where(x => x.Id == serverId).ToArray();
        if (matches.Length != 1 || !matches[0].Enabled || matches[0].RequiresConfiguration)
            throw new UnauthorizedAccessException("The selected Cortex asset provider is unavailable or unconfigured.");
        var server = CerebralJson.Read<McpRegistration>(CerebralJson.Element(matches[0]));
        ValidateRegistration(server);
        if (server.CortexNative is null)
            throw new UnauthorizedAccessException("Cortex assets require the selected native binding; no fallback is permitted.");
        var fingerprint = CortexOperationPolicy.ProviderFingerprint(server);
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            var current = _settings().McpServers.Where(x => x.Id == serverId).ToArray();
            if (current.Length != 1 || !current[0].Enabled || current[0].RequiresConfiguration ||
                current[0].CortexNative is null || CortexOperationPolicy.ProviderFingerprint(current[0]) != fingerprint)
                throw new UnauthorizedAccessException("The Cortex asset provider changed during the request.");
        }
        var reply = await NativeProviderRequestAsync(server, "resource", null,
            CerebralJson.Element(new { uri }), token, RequireCurrent);
        // The same binding must still own the result, including relayed exchanges.
        RequireCurrent();
        RequireAssetUniqueKeys(reply);
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidAssetResource();
        if (!ok.GetBoolean())
        {
            var code = "resource_unavailable";
            if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String && SafeAssetCode(value.GetString()))
                code = value.GetString()!;
            if (code.Contains("auth", StringComparison.Ordinal) || code.Contains("scope", StringComparison.Ordinal) ||
                code.Contains("denied", StringComparison.Ordinal) || code.Contains("permission", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Cortex asset resource access failed: " + code + ".");
            throw new IOException("Cortex asset resource read failed: " + code + ".");
        }
        if (!reply.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(result.GetRawText()) > AssetResourceBytes)
            throw new InvalidDataException("Cortex asset resource exceeds its bound or is not a resource object.");
        ValidateAssetResource(result, selector);
        return result.Clone();
    }

    private static AssetSelector ParseAssetSelector(string uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.Length > 4096 || uri.Any(char.IsControl) || uri.Contains('#') || uri.Contains('\\'))
            throw InvalidAssetSelector();
        var queryIndex = uri.IndexOf('?');
        var path = queryIndex < 0 ? uri : uri[..queryIndex];
        var query = queryIndex < 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : ParseAssetQuery(uri[(queryIndex + 1)..]);
        if (path.StartsWith("cortex://", StringComparison.Ordinal))
        {
            var reference = path[9..];
            if (!AssetHash(reference)) throw InvalidAssetSelector();
            if (queryIndex < 0) return new(uri, null, reference, false, 0, 0, 0);
            if (!query.TryGetValue("part", out var part) || part != "bytes" || query.Keys.Any(x => x is not "part" and not "offset" and not "length"))
                throw InvalidAssetSelector();
            var offset = query.TryGetValue("offset", out var o) ? AssetNumber(o, long.MaxValue) : 0;
            var length = query.TryGetValue("length", out var l) ? AssetNumber(l, 131072) : 65536;
            if (offset % 65536 != 0 || length < 1) throw InvalidAssetSelector();
            return new(uri, null, reference, true, offset, (int)length, 0);
        }
        const string searchPrefix = "pegasus-asset://v2/search/";
        if (!path.StartsWith(searchPrefix, StringComparison.Ordinal) || !AssetName(path[searchPrefix.Length..], 63) ||
            queryIndex < 0 || !query.TryGetValue("q", out var terms) || terms.Length > 256 ||
            terms.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 8 ||
            query.Keys.Any(x => x is not "q" and not "limit" and not "after") ||
            (query.TryGetValue("after", out var after) && (after.Length is < 1 or > 256)))
            throw InvalidAssetSelector();
        var limit = query.TryGetValue("limit", out var limitText) ? AssetNumber(limitText, 16) : 4;
        if (limit < 1) throw InvalidAssetSelector();
        return new(uri, path[searchPrefix.Length..], null, false, 0, 0, (int)limit);
    }

    private static Dictionary<string, string> ParseAssetQuery(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&'))
        {
            var split = pair.IndexOf('=');
            if (split < 1 || pair.IndexOf('=', split + 1) >= 0 || !result.TryAdd(pair[..split], DecodeAssetQuery(pair[(split + 1)..])))
                throw InvalidAssetSelector();
        }
        return result;
    }

    private static string DecodeAssetQuery(string value)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                    throw InvalidAssetSelector();
                bytes.WriteByte(b); i += 2;
            }
            else if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') bytes.WriteByte((byte)c);
            else throw InvalidAssetSelector();
        }
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { throw InvalidAssetSelector(); }
        if (decoded.Any(char.IsControl)) throw InvalidAssetSelector();
        return decoded;
    }

    private static long AssetNumber(string value, long maximum)
    {
        if (value.Length == 0 || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > maximum)
            throw InvalidAssetSelector();
        return number;
    }

    private static void ValidateAssetResource(JsonElement resource, AssetSelector selector)
    {
        AssetKeys(resource, "contents", "_meta");
        if (!resource.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array || contents.GetArrayLength() != 1)
            throw InvalidAssetResource();
        var item = contents[0];
        AssetKeys(item, "uri", "mimeType", "text", "blob");
        if (AssetString(item, "uri") != selector.Uri || !AssetText(AssetString(item, "mimeType"), 256)) throw InvalidAssetResource();
        var hasText = item.TryGetProperty("text", out var text);
        var hasBlob = item.TryGetProperty("blob", out var blob);
        if (hasText == hasBlob) throw InvalidAssetResource();
        if (hasText)
        {
            if (resource.TryGetProperty("_meta", out _) || AssetString(item, "mimeType") != "application/json" || text.ValueKind != JsonValueKind.String)
                throw InvalidAssetResource();
            JsonDocument body;
            try { body = JsonDocument.Parse(text.GetString()!, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw InvalidAssetResource(); }
            using (body)
            {
                var value = body.RootElement;
                RequireAssetUniqueKeys(value);
                if (value.ValueKind != JsonValueKind.Object || AssetString(value, "contract") != AssetContract) throw InvalidAssetResource();
                if (value.TryGetProperty("ok", out var failed))
                {
                    AssetKeys(value, "contract", "ok", "error", "repairRecorded");
                    if (failed.ValueKind != JsonValueKind.False || !SafeAssetCode(AssetString(value, "error")) ||
                        (value.TryGetProperty("repairRecorded", out var repaired) && repaired.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                        throw InvalidAssetResource();
                    return; // Ordinary missing/stale/scope failures remain passive provider data.
                }
                if (selector.Bytes) throw InvalidAssetResource();
                if (selector.Namespace is { } ns)
                {
                    if (AssetString(value, "namespace") != ns || !value.TryGetProperty("items", out var items) ||
                        items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > selector.Limit ||
                        AssetLong(value, "scanned") is < 0 or > 512 || !AssetBoolean(value, "complete", out _)) throw InvalidAssetResource();
                    foreach (var entry in items.EnumerateArray())
                    {
                        if (!AssetName(AssetString(entry, "id"), 127) || !AssetHash(AssetString(entry, "ref")) ||
                            AssetString(entry, "uri") != "cortex://" + AssetString(entry, "ref") || AssetLong(entry, "size") < 0 ||
                            !AssetText(AssetString(entry, "title"), 4096) || !AssetText(AssetString(entry, "meaning"), 32768) ||
                            !AssetText(AssetString(entry, "mimeType"), 256)) throw InvalidAssetResource();
                    }
                    if (value.TryGetProperty("nextUri", out var next) && (next.ValueKind != JsonValueKind.String ||
                        ParseAssetSelector(next.GetString()!) is not { Namespace: var nextNs } || nextNs != ns)) throw InvalidAssetResource();
                }
                else if (!AssetName(AssetString(value, "id"), 127) || AssetString(value, "ref") != selector.Ref ||
                    !AssetName(AssetString(value, "namespace"), 63) || !AssetHash(AssetString(value, "sha256")) || AssetLong(value, "size") < 0 ||
                    AssetString(value, "metadataUri") != "cortex://" + selector.Ref || AssetString(value, "bytesUri") != "cortex://" + selector.Ref + "?part=bytes" ||
                    !AssetText(AssetString(value, "title"), 4096) || !AssetText(AssetString(value, "meaning"), 32768) ||
                    !AssetText(AssetString(value, "mimeType"), 256)) throw InvalidAssetResource();
            }
            return;
        }
        if (!selector.Bytes || blob.ValueKind != JsonValueKind.String || !resource.TryGetProperty("_meta", out var meta)) throw InvalidAssetResource();
        AssetKeys(meta, "contract", "namespace", "id", "ref", "offset", "length", "totalSize", "sha256", "contentSha256", "complete");
        byte[] data;
        try { data = Convert.FromBase64String(blob.GetString()!); }
        catch (FormatException) { throw InvalidAssetResource(); }
        var total = AssetLong(meta, "totalSize");
        var chunkHash = AssetString(meta, "sha256");
        if (Convert.ToBase64String(data) != blob.GetString() || data.Length > selector.Length || total < 0 || selector.Offset > total ||
            data.LongLength != Math.Min((long)selector.Length, total - selector.Offset) || AssetString(meta, "contract") != AssetContract ||
            !AssetName(AssetString(meta, "namespace"), 63) || !AssetName(AssetString(meta, "id"), 127) ||
            AssetString(meta, "ref") != selector.Ref || AssetLong(meta, "offset") != selector.Offset || AssetLong(meta, "length") != data.Length ||
            !AssetHash(chunkHash) || !AssetHash(AssetString(meta, "contentSha256")) ||
            Convert.ToHexStringLower(SHA256.HashData(data)) != chunkHash || !AssetBoolean(meta, "complete", out var complete) ||
            complete != (selector.Offset == 0 && data.LongLength == total) || (complete && chunkHash != AssetString(meta, "contentSha256")))
            throw InvalidAssetResource();
    }

    private static bool AssetText(string value, int maximum) => value.Length <= maximum && !value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
    private static bool AssetHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool AssetName(string value, int maximum) => value.Length >= 1 && value.Length <= maximum && value[0] is >= 'a' and <= 'z' &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool SafeAssetCode(string? value) => value is { Length: >= 1 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static string AssetString(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw InvalidAssetResource();
    private static long AssetLong(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : throw InvalidAssetResource();
    private static bool AssetBoolean(JsonElement value, string property, out bool boolean)
    {
        boolean = false;
        if (!value.TryGetProperty(property, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        boolean = item.GetBoolean(); return true;
    }
    private static void AssetKeys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) throw InvalidAssetResource();
    }
    private static void RequireAssetUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw InvalidAssetResource(); RequireAssetUniqueKeys(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireAssetUniqueKeys(item);
    }
    private static ArgumentException InvalidAssetSelector() => new("The Cortex asset URI is outside the fixed resource selectors.", "uri");
    private static InvalidDataException InvalidAssetResource() => new("The Cortex asset resource shape or content integrity is invalid.");
}
