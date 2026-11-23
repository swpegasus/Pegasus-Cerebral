using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Read-only actual Source/installed-provider canary. Reads two exact public artwork
/// references and bounded metadata discovery. Never changes registration, grants or models.</summary>
internal static class SourceCortexAssetAcceptance
{
    private const string Baseline = "8aa0f2aae3b8d97cdd6e43560b82f54a59f70f4863f42d904ad8890c69fbb5c3";
    private const string OpenMoji = "8d6f5eeff951a744f6c0c6bc1c184073eebad5103069b4da42349633230c2b35";
    private const string OpenMojiSha = "3ec8eaf57aa838f3351561e122387d8ba8ce73169ef145c4b7e5203c5f6508da";

    internal static async Task RunAsync(CerebralClient client, CancellationToken token)
    {
        var providers = await client.GetAsync<JsonElement>("cortex.assets.providers", new { }, cancellationToken: token);
        var selected = providers.GetProperty("providers").EnumerateArray().ToArray();
        if (selected.Length != 1 || selected[0].GetProperty("transport").GetString() != "cortex-native-pipe")
            throw new InvalidOperationException("The canary requires the one already selected native Source provider.");
        var serverId = selected[0].GetProperty("id").GetString()!;
        var evidence = new List<object>();
        foreach (var reference in new[] { Baseline, OpenMoji })
        {
            var uri = "cortex://" + reference;
            var resource = await Read(uri);
            var metadata = Metadata(resource, uri);
            if (metadata.GetProperty("contract").GetString() != "pegasus.assets/2" ||
                metadata.GetProperty("namespace").GetString() != "knowledge.shared" ||
                metadata.GetProperty("ref").GetString() != reference ||
                metadata.GetProperty("metadataUri").GetString() != uri || metadata.GetProperty("bytesUri").GetString() != uri + "?part=bytes")
                throw new InvalidDataException("Selected metadata did not match its exact reference and namespace.");
            var total = metadata.GetProperty("size").GetInt64();
            var sha = metadata.GetProperty("sha256").GetString();
            if (total is < 1 or > 65536) throw new InvalidDataException("The public small-icon canary must fit the default chunk.");
            if (reference == OpenMoji && (total != 562 || sha != OpenMojiSha || metadata.GetProperty("license").GetString() != "CC-BY-SA-4.0"))
                throw new InvalidDataException("The admitted OpenMoji public identity changed.");
            var bytes = await Read(uri + "?part=bytes");
            var content = bytes.GetProperty("contents");
            if (content.GetArrayLength() != 1 || content[0].GetProperty("uri").GetString() != uri + "?part=bytes" ||
                content[0].GetProperty("mimeType").GetString() != metadata.GetProperty("mimeType").GetString())
                throw new InvalidDataException("Selected bytes did not retain the requested URI.");
            var data = Convert.FromBase64String(content[0].GetProperty("blob").GetString()!);
            var proof = bytes.GetProperty("_meta");
            var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (data.Length != total || digest != sha || proof.GetProperty("ref").GetString() != reference ||
                proof.GetProperty("contract").GetString() != "pegasus.assets/2" ||
                proof.GetProperty("id").GetString() != metadata.GetProperty("id").GetString() ||
                proof.GetProperty("namespace").GetString() != "knowledge.shared" ||
                proof.GetProperty("offset").GetInt64() != 0 || proof.GetProperty("length").GetInt64() != total ||
                proof.GetProperty("totalSize").GetInt64() != total || proof.GetProperty("sha256").GetString() != digest ||
                proof.GetProperty("contentSha256").GetString() != digest || !proof.GetProperty("complete").GetBoolean())
                throw new InvalidDataException("Selected byte/chunk/full-asset integrity failed.");
            var maximum = await Read(uri + "?part=bytes&length=131072");
            if (maximum.GetProperty("_meta").GetProperty("length").GetInt64() != total)
                throw new InvalidDataException("A maximum chunk request did not clamp to the exact asset size.");
            evidence.Add(new { reference, id = metadata.GetProperty("id").GetString(), size = total, sha256 = digest,
                mediaType = metadata.GetProperty("mimeType").GetString(), defaultChunk = 65536, maximumChunk = 131072,
                metadataVerified = true, bytesVerified = true, artworkRendering = "not tested" });
        }
        var searchUri = "pegasus-asset://v2/search/knowledge.shared?q=check&limit=4";
        var search = Metadata(await Read(searchUri), searchUri);
        if (search.GetProperty("namespace").GetString() != "knowledge.shared" || search.GetProperty("items").GetArrayLength() > 4 ||
            search.GetProperty("scanned").GetInt32() > 512)
            throw new InvalidDataException("Search did not preserve its explicit scope and preview bound.");
        foreach (var invalid in new[] { "cortex://" + Baseline + "?part=bytes&length=131073", "cortex://" + Baseline + "?part=bytes&offset=1" })
        {
            var result = await client.CommandAsync("cortex.assets.read", new { serverId, uri = invalid }, cancellationToken: token);
            if (result.Success) throw new InvalidOperationException("An invalid consumer chunk selector was accepted.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { source = "actual enrolled Source Worker/native Cortex owner pipe", evidence,
            searchItems = search.GetProperty("items").GetArrayLength(), searchScanned = search.GetProperty("scanned").GetInt32(),
            invalidSelectorsRejected = 2, modelCalls = 0, mcpFallback = false }, CerebralJson.Options));

        Task<JsonElement> Read(string uri) => client.GetAsync<JsonElement>("cortex.assets.read", new { serverId, uri }, cancellationToken: token);
    }

    private static JsonElement Metadata(JsonElement resource, string uri)
    {
        var contents = resource.GetProperty("contents");
        if (contents.GetArrayLength() != 1 || contents[0].GetProperty("uri").GetString() != uri ||
            contents[0].GetProperty("mimeType").GetString() != "application/json")
            throw new InvalidDataException("Expected one exact resource metadata body.");
        var text = contents[0].GetProperty("text").GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > 32768) throw new InvalidDataException("Metadata exceeded its bound.");
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidDataException("Provider resource unavailable: " + doc.RootElement.GetProperty("error").GetString());
        return doc.RootElement.Clone();
    }
}
