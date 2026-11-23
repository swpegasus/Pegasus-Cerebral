using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

public sealed record CortexIconProvider(string Id, string Transport);
public sealed record CortexIconPreview(string Id, string Ref, string Title, string Meaning, string MimeType, long Size, string Text)
{
    public string MetadataUri => "cortex://" + Ref;
    public override string ToString() => Title + (Text.Length == 0 ? "" : "  " + Text);
}
public sealed record CortexIconPage(IReadOnlyList<CortexIconPreview> Items, string? NextUri);
public sealed record CortexIconMetadata(CortexIconPreview Preview, string Namespace, string Sha256,
    string License, string Source, string Pack, string PackVersion, string DetailsRef, string RawJson)
{
    public string Reference => Preview.MetadataUri + "\n" + Preview.Title + ": " + Preview.Meaning +
        (Preview.Text.Length == 0 ? "" : "\nText: " + Preview.Text) +
        "\nnamespace=" + Namespace + "; id=" + Preview.Id + "; sha256=" + Sha256 +
        "; size=" + Preview.Size + "; mimeType=" + Preview.MimeType +
        "\nlicense=" + License + "; source=" + Source +
        (DetailsRef.Length == 0 ? "" : "\nLicense/provenance details: cortex://" + DetailsRef);
}

/// <summary>Selected-only native service consumer. Opaque parameters are never interpreted or executed.</summary>
public sealed class CortexIconLibrary
{
    public const string Namespace = "knowledge.shared";
    public const string Contract = "pegasus.assets/2";
    public const int PageLimit = 4;
    public const int MaximumSearchTerms = 8;
    public const int MaximumCursorCharacters = 256;
    public const int ChunkBytes = 65536;
    public const int MaximumAssetBytes = 2 * 1024 * 1024;
    public const int MaximumMetadataBytes = 32768;
    public const int MaximumEnvelopeBytes = 128 * 1024;
    private const string SearchPrefix = "pegasus-asset://v2/search/" + Namespace + "?";
    private readonly IDesktopServiceHost host;
    public CortexIconLibrary(IDesktopServiceHost host) => this.host = host;

    public async Task<IReadOnlyList<CortexIconProvider>> ProvidersAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await host.ExecuteServiceCommandAsync("cortex.assets.providers", new { }, token);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new IOException(result.Error ?? "Cortex library providers unavailable.");
        BoundObject(result.Data, 8192);
        var providers = Required(result.Data, "providers");
        if (providers.ValueKind != JsonValueKind.Array || providers.GetArrayLength() > 8)
            throw new InvalidDataException("Invalid or oversized provider list.");
        var items = providers.EnumerateArray().Select(item =>
            new CortexIconProvider(Text(item, "id", 128), Text(item, "transport", 32))).ToArray();
        if (items.Any(item => item.Transport != "cortex-native-pipe") ||
            items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidDataException("Providers must have distinct native Cortex identities.");
        return items;
    }

    public static string SearchUri(string terms)
    {
        ValidateTerms(terms);
        return SearchPrefix + "q=" + Uri.EscapeDataString(terms) + "&limit=" + PageLimit;
    }

    public static void ValidatePageUri(string value)
    {
        if (value.Length > 2048 || !value.StartsWith(SearchPrefix, StringComparison.Ordinal) || value.Contains('#'))
            throw new InvalidDataException("Page URI must remain in the selected shared namespace.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value[SearchPrefix.Length..].Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals < 1 || part.IndexOf('=', equals + 1) >= 0 || !fields.TryAdd(part[..equals], DecodeQuery(part[(equals + 1)..])))
                throw new InvalidDataException("Invalid page query.");
        }
        if (fields.Keys.Except(new[] { "q", "limit", "after" }, StringComparer.Ordinal).Any() ||
            !fields.TryGetValue("q", out var terms) || !fields.TryGetValue("limit", out var limit) || limit != "4" ||
            fields.TryGetValue("after", out var cursor) && (cursor.Length is < 1 or > MaximumCursorCharacters || cursor.Any(char.IsControl)))
            throw new InvalidDataException("Page query changed the bounded search contract.");
        ValidateTerms(terms);
    }

    private static void ValidateTerms(string terms)
    {
        if (terms.Length > 256 || terms.Any(char.IsControl) ||
            terms.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaximumSearchTerms)
            throw new InvalidDataException("Search requires at most eight terms and 256 characters without controls.");
        _ = new UTF8Encoding(false, true).GetByteCount(terms);
    }

    private static string DecodeQuery(string value)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < value.Length; ++i)
        {
            var character = value[i];
            if (character == '%')
            {
                if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var decoded))
                    throw new InvalidDataException("Malformed resource query escape.");
                bytes.WriteByte(decoded); i += 2;
            }
            else if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~') bytes.WriteByte((byte)character);
            else throw new InvalidDataException("Resource query values require canonical escaping.");
        }
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (text.Any(char.IsControl)) throw new InvalidDataException("Resource query contains controls.");
        return text;
    }

    public async Task<CortexIconPage> SearchAsync(string provider, string uri, CancellationToken token)
    {
        ValidatePageUri(uri);
        var data = TextData(await ReadAsync(provider, uri, token), uri);
        RequireContract(data);
        if (Text(data, "namespace", 63) != Namespace) throw new InvalidDataException("Search namespace changed.");
        var items = Required(data, "items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageLimit)
            throw new InvalidDataException("Search exceeded its four-result limit.");
        var previews = items.EnumerateArray().Select(ParsePreview).ToArray();
        if (previews.Select(item => item.Ref).Distinct(StringComparer.Ordinal).Count() != previews.Length)
            throw new InvalidDataException("Duplicate selected reference.");
        foreach (var (item, preview) in items.EnumerateArray().Zip(previews))
            if (Text(item, "uri", 80) != preview.MetadataUri) throw new InvalidDataException("Search reference URI mismatch.");
        var next = Optional(data, "nextUri", 2048);
        if (next.Length != 0)
        {
            ValidatePageUri(next);
            var currentQuery = uri[(uri.IndexOf('?') + 1)..].Split('&').Single(x => x.StartsWith("q=", StringComparison.Ordinal));
            var nextQuery = next[(next.IndexOf('?') + 1)..].Split('&').Single(x => x.StartsWith("q=", StringComparison.Ordinal));
            if (currentQuery != nextQuery || !next.Contains("&after=", StringComparison.Ordinal))
                throw new InvalidDataException("Next page changed the search terms or lacks its cursor.");
        }
        return new(previews, next.Length == 0 ? null : next);
    }

    public async Task<CortexIconMetadata> MetadataAsync(string provider, CortexIconPreview selected, CancellationToken token)
    {
        Hash(selected.Ref);
        var data = TextData(await ReadAsync(provider, selected.MetadataUri, token), selected.MetadataUri);
        RequireContract(data);
        var preview = ParsePreview(data);
        if (preview.Id != selected.Id || preview.Ref != selected.Ref ||
            preview.Size != selected.Size || preview.MimeType != selected.MimeType)
            throw new InvalidDataException("Exact metadata does not match the selected preview identity.");
        if (Text(data, "namespace", 63) != Namespace ||
            Text(data, "metadataUri", 80) != selected.MetadataUri ||
            Text(data, "bytesUri", 100) != selected.MetadataUri + "?part=bytes")
            throw new InvalidDataException("Exact metadata coordinates changed.");
        var sha = Text(data, "sha256", 64); Hash(sha);
        var details = Optional(data, "detailsRef", 64);
        if (details.Length != 0)
        {
            Hash(details);
            if (Text(data, "detailsUri", 80) != "cortex://" + details) throw new InvalidDataException("Related reference URI mismatch.");
        }
        var source = Optional(data, "source", 2048);
        if (source.Length != 0 && (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme != "https" || sourceUri.UserInfo.Length != 0))
            throw new InvalidDataException("Source must be a credential-free HTTPS reference.");
        return new(preview, Namespace, sha, Optional(data, "license", 128), source,
            Optional(data, "pack", 127), Optional(data, "packVersion", 64), details, data.GetRawText());
    }

    public async Task<byte[]> BytesAsync(string provider, CortexIconMetadata metadata, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Hash(metadata.Preview.Ref); Hash(metadata.Sha256);
        if (metadata.Namespace != Namespace) throw new InvalidDataException("Selected namespace changed.");
        if (metadata.Preview.Size is < 1 or > MaximumAssetBytes)
            throw new InvalidDataException("Selected content exceeds the 2 MiB consumer limit or is empty.");
        using var output = new MemoryStream(checked((int)metadata.Preview.Size));
        for (var offset = 0; offset < metadata.Preview.Size; offset += ChunkBytes)
        {
            var uri = metadata.Preview.MetadataUri + "?part=bytes&offset=" + offset + "&length=" + ChunkBytes;
            var result = await ReadAsync(provider, uri, token);
            var content = Content(result, uri);
            if (Text(content, "mimeType", 100) != metadata.Preview.MimeType || content.TryGetProperty("text", out _))
                throw new InvalidDataException("Selected byte media type or encoding changed.");
            var meta = Required(result, "_meta"); RequireContract(meta);
            if (Text(meta, "namespace", 63) != metadata.Namespace || Text(meta, "id", 127) != metadata.Preview.Id ||
                Text(meta, "ref", 64) != metadata.Preview.Ref || Integer(meta, "offset") != offset ||
                Integer(meta, "totalSize") != metadata.Preview.Size || Text(meta, "contentSha256", 64) != metadata.Sha256)
                throw new InvalidDataException("Selected chunk identity changed.");
            var expectedLength = (int)Math.Min(ChunkBytes, metadata.Preview.Size - offset);
            var blob = Text(content, "blob", 4 * ((ChunkBytes + 2) / 3), controls: false);
            var bytes = Convert.FromBase64String(blob);
            if (bytes.Length != expectedLength || Integer(meta, "length") != expectedLength ||
                Digest(bytes) != Text(meta, "sha256", 64) ||
                Required(meta, "complete").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                Required(meta, "complete").GetBoolean() != (offset == 0 && bytes.Length == metadata.Preview.Size))
                throw new InvalidDataException("Selected chunk length, digest or completion mismatch.");
            output.Write(bytes);
        }
        token.ThrowIfCancellationRequested();
        var all = output.ToArray();
        if (all.Length != metadata.Preview.Size || Digest(all) != metadata.Sha256)
            throw new InvalidDataException("Full selected content does not match exact metadata.");
        return all;
    }

    private async Task<JsonElement> ReadAsync(string provider, string uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 128 || provider.Any(char.IsControl))
            throw new InvalidDataException("Explicit provider identity required.");
        var result = await host.ExecuteServiceCommandAsync("cortex.assets.read", new { serverId = provider, uri }, token);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new IOException(result.Error ?? "Cortex asset resource unavailable.");
        BoundObject(result.Data, MaximumEnvelopeBytes);
        return result.Data;
    }

    private static JsonElement Content(JsonElement envelope, string uri)
    {
        var contents = Required(envelope, "contents");
        if (contents.ValueKind != JsonValueKind.Array || contents.GetArrayLength() != 1)
            throw new InvalidDataException("One exact resource content is required.");
        var content = contents[0];
        if (Text(content, "uri", 2048) != uri) throw new InvalidDataException("Resource response URI mismatch.");
        return content;
    }

    private static JsonElement TextData(JsonElement envelope, string uri)
    {
        var content = Content(envelope, uri);
        if (Text(content, "mimeType", 100) != "application/json" || content.TryGetProperty("blob", out _))
            throw new InvalidDataException("Expected bounded metadata text.");
        var text = Text(content, "text", MaximumMetadataBytes, controls: false);
        if (Encoding.UTF8.GetByteCount(text) > MaximumMetadataBytes) throw new InvalidDataException("Metadata exceeds 32 KiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        BoundObject(document.RootElement, MaximumMetadataBytes);
        RejectDuplicates(document.RootElement);
        var data = document.RootElement.Clone();
        if (data.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new IOException("Cortex resource unavailable: " + Optional(data, "error", 128));
        return data;
    }

    private static CortexIconPreview ParsePreview(JsonElement item)
    {
        var id = Text(item, "id", 127);
        if (!char.IsAsciiLetterLower(id[0]) || id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException("Invalid asset identifier.");
        var reference = Text(item, "ref", 64); Hash(reference);
        var type = Text(item, "mimeType", 100);
        if (!type.Contains('/') || type.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '.' and not '+' and not '-'))
            throw new InvalidDataException("Invalid selected media type.");
        var size = Integer(item, "size");
        if (size < 0) throw new InvalidDataException("Negative selected size.");
        return new(id, reference, Text(item, "title", 128), Text(item, "meaning", 512), type, size, Optional(item, "text", 32));
    }

    private static void RequireContract(JsonElement data)
    {
        if (Text(data, "contract", 32) != Contract) throw new InvalidDataException("Unsupported Cortex asset contract.");
    }
    private static void BoundObject(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > maximum)
            throw new InvalidDataException("Invalid or oversized service resource object.");
        RejectDuplicates(value);
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate resource property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static JsonElement Required(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value :
            throw new InvalidDataException("Missing resource field: " + name);
    private static long Integer(JsonElement item, string name)
    {
        var value = Required(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number :
            throw new InvalidDataException("Invalid integer resource field: " + name);
    }
    private static string Text(JsonElement item, string name, int maximum, bool controls = true)
    {
        var value = Required(item, name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid text resource field: " + name);
        var text = value.GetString()!;
        if (text.Length is < 1 || text.Length > maximum || controls && text.Any(char.IsControl))
            throw new InvalidDataException("Unbounded or empty resource field: " + name);
        return text;
    }
    private static string Optional(JsonElement item, string name, int maximum) =>
        item.TryGetProperty(name, out _) ? Text(item, name, maximum) : "";
    private static void Hash(string value)
    {
        if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid pinned digest or reference.");
    }
    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
