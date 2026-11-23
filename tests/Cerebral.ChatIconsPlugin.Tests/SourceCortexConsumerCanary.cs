using System.IO;
using System.Text;
using System.Text.Json;
using Cerebral.ChatIconsPlugin;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin.Tests;

/// <summary>Opt-in, actual Source owner-pipe reads. No startup, settings, registration, grant or Source cache mutations.</summary>
internal static class SourceCortexConsumerCanary
{
    private sealed record Pin(string Name, string Ref, long? Size = null, string? Sha256 = null, string? License = null);
    private static readonly Pin[] Pins =
    [
        new("baseline", "8aa0f2aae3b8d97cdd6e43560b82f54a59f70f4863f42d904ad8890c69fbb5c3"),
        new("openmoji", "8d6f5eeff951a744f6c0c6bc1c184073eebad5103069b4da42349633230c2b35", 562,
            "3ec8eaf57aa838f3351561e122387d8ba8ce73169ef145c4b7e5203c5f6508da", "CC-BY-SA-4.0"),
        new("fluent", "54170fbcae494273425782b565a84a39b7db9a7afbc6494ac6e2c314f6cfb988", 10552,
            "0b791d107a3bdabee90e6b10edaac4a4753e85ba4e941d8c578979ba877d9909")
    ];

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This canary requires Windows Source.");
        var source = Path.GetFullPath(DesktopInstancePaths.DefaultSourceDataDirectory);
        var declarationPath = Path.Combine(source, "Desktop", "connection.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var declarationBytes = await ReadDeclarationAsync(declarationPath, token);
        using var declaration = JsonDocument.Parse(declarationBytes, new JsonDocumentOptions { MaxDepth = 16 });
        var data = declaration.RootElement;
        if (data.GetProperty("transport").GetString() != "pipe" ||
            !IsSource(data.GetProperty("localDataDirectory").GetString(), source))
            throw new InvalidOperationException("The default Source connection is not its explicit owner pipe.");
        var endpoint = data.GetProperty("localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)
            ?? throw new InvalidOperationException("The Source owner endpoint is absent.");
        endpoint.Validate();
        using var client = new CerebralClient(endpoint);
        if (!client.IsLocalPipe) throw new InvalidOperationException("No owner HTTP fallback is permitted.");
        var before = await HealthAsync(client, source, token);
        var bridge = new OwnerServiceBridge(client);
        var library = new CortexIconLibrary(bridge);
        var providers = await library.ProvidersAsync(token);
        if (providers.Count != 1)
            throw new InvalidOperationException("The canary requires exactly one existing selected native Source provider.");
        var provider = providers.Single().Id;
        Print(new { phase = "owner-binding", evidenceLevel = "actual native owner pipe", sourceDefaultRootVerified = true,
            connectionDeclarationReadOnly = true, providerCount = providers.Count, transport = providers.Single().Transport });

        var scratchParent = Path.GetFullPath(Path.GetTempPath());
        var scratch = Path.Combine(scratchParent, "Cerebral-Cortex-Consumer-Live-" + Guid.NewGuid().ToString("N"));
        RejectLinks(scratchParent);
        Directory.CreateDirectory(scratch);
        try
        {
            var cache = new CortexIconCache(scratch);
            foreach (var pin in Pins)
            {
                // A direct exact read seeds the selected preview coordinates; the consumer then exact-reads and rechecks them.
                var preview = await SeedSelectedAsync(bridge, provider, pin.Ref, token);
                var metadata = await library.MetadataAsync(provider, preview, token);
                if (pin.Size is { } expectedSize && metadata.Preview.Size != expectedSize ||
                    pin.Sha256 is { } expectedHash && metadata.Sha256 != expectedHash ||
                    pin.License is { } expectedLicense && metadata.License != expectedLicense)
                    throw new InvalidDataException("An independently pinned imported asset identity changed.");
                var bytes = await library.BytesAsync(provider, metadata, token);
                if (metadata.Preview.MimeType != "image/svg+xml" || CortexIconContent.Preview(metadata, bytes) is not null)
                    throw new InvalidDataException("The pinned original SVG renderer boundary changed.");
                if (!metadata.Reference.Contains("cortex://" + pin.Ref, StringComparison.Ordinal) ||
                    !metadata.Reference.Contains(metadata.Preview.Meaning, StringComparison.Ordinal) ||
                    !metadata.Reference.Contains(metadata.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The consumer reference lost exact identity or semantic meaning.");
                // Print each completed reference before discovery, preserving truthful partial acceptance if the store becomes busy.
                Print(new { phase = "exact-selected-reference", asset = pin.Name, reference = pin.Ref,
                    evidenceLevel = "actual Source owner pipe through IDesktopServiceHost/CortexIconLibrary",
                    id = metadata.Preview.Id, size = bytes.Length, sha256 = metadata.Sha256,
                    independentSizeAndHashPin = pin.Size is not null && pin.Sha256 is not null,
                    baselineEvidence = pin.Name == "baseline" ? "exact ref plus observed metadata/full-content consistency; no independent size/hash pin" : null,
                    metadataVerified = true, chunkAndFullHashVerified = true, semanticReferenceVerified = true,
                    license = metadata.License, licenseDetailsRef = metadata.DetailsRef,
                    artworkPreview = "SVG renderer pending; original bytes verified only", offscreenControls = "not exercised" });
                await cache.SaveAsync(provider, metadata, bytes, token);
                var cached = await cache.ReadAsync(provider, metadata, token);
                if (cached is null || !cached.SequenceEqual(bytes))
                    throw new InvalidDataException("The selected actual content did not round-trip its owned temporary cache.");
                Print(new { phase = "selected-temporary-cache", asset = pin.Name,
                    evidenceLevel = "local filesystem verification using actual selected native content",
                    ownedTemporaryFiles = true, roundTripVerified = true, fullHash = metadata.Sha256,
                    sourceFileWrites = 0, offscreenControls = "not exercised" });
            }

            CortexIconPage page;
            try { page = await library.SearchAsync(provider, CortexIconLibrary.SearchUri("check"), token); }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                var reason = error.Message.Contains("asset_busy", StringComparison.Ordinal) ? "asset_busy" : "search_unverified";
                Print(new { phase = "bounded-search", verified = false, evidenceLevel = "actual native discovery attempt",
                    reason, automaticRetry = false });
                throw new InvalidOperationException("Bounded native search remains unverified.");
            }
            Print(new { phase = "bounded-search", evidenceLevel = "actual native four-result metadata discovery",
                verified = true, itemCount = page.Items.Count, limit = CortexIconLibrary.PageLimit,
                nextUriValidated = page.NextUri is not null, nextPageRead = false, artworkHydratedFromSearch = false });
            var after = await HealthAsync(client, source, token);
            var afterDeclaration = await ReadDeclarationAsync(declarationPath, token);
            if (!afterDeclaration.SequenceEqual(declarationBytes) || before.Version != after.Version || before.Protocol != after.Protocol ||
                before.InstallationId != after.InstallationId)
                throw new InvalidOperationException("The Source owner generation or declaration changed during acceptance.");
            Print(new { phase = "complete", evidenceLevel = "actual native consumer integration plus owned temporary cache",
                exactAssets = Pins.Length, sourceOwnerBindingUnchanged = true, sourceFileWrites = 0, sourceMutationCommands = 0,
                credentialFilesRead = 0, credentialFieldsUsed = 0, credentialOutput = false, clipboardCalls = 0, modelCalls = 0,
                serviceBridgeCommands = bridge.Commands, offscreenControls = "not exercised", installedPickerInteraction = "not exercised" });
        }
        finally
        {
            var resolved = Path.GetFullPath(scratch);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(scratchParent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Cerebral-Cortex-Consumer-Live-", StringComparison.Ordinal))
                throw new IOException("The owned temporary cleanup target changed.");
            RejectLinks(resolved);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task<CortexIconPreview> SeedSelectedAsync(OwnerServiceBridge bridge, string provider, string reference,
        CancellationToken token)
    {
        var uri = "cortex://" + reference;
        var result = await bridge.ExecuteServiceCommandAsync("cortex.assets.read", new { serverId = provider, uri }, token);
        if (!result.Success) throw new IOException("Exact selected resource bootstrap failed.");
        var contents = result.Data.GetProperty("contents");
        if (contents.GetArrayLength() != 1 || contents[0].GetProperty("uri").GetString() != uri ||
            contents[0].GetProperty("mimeType").GetString() != "application/json")
            throw new InvalidDataException("Exact selected bootstrap envelope changed.");
        var text = contents[0].GetProperty("text").GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > CortexIconLibrary.MaximumMetadataBytes)
            throw new InvalidDataException("Exact selected bootstrap exceeded the metadata bound.");
        using var body = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var item = body.RootElement;
        if (item.GetProperty("ref").GetString() != reference || item.GetProperty("namespace").GetString() != CortexIconLibrary.Namespace)
            throw new InvalidDataException("Exact selected bootstrap changed its pinned reference.");
        return new(item.GetProperty("id").GetString()!, reference, item.GetProperty("title").GetString()!,
            item.GetProperty("meaning").GetString()!, item.GetProperty("mimeType").GetString()!, item.GetProperty("size").GetInt64(),
            item.TryGetProperty("text", out var alternative) ? alternative.GetString()! : "");
    }

    private static async Task<HostInfo> HealthAsync(CerebralClient client, string source, CancellationToken token)
    {
        var result = await client.CommandAsync("health", cancellationToken: token);
        if (!result.Success) throw new IOException("Source owner health unavailable.");
        var health = result.Data.Deserialize<HostInfo>(CerebralJson.Options)
            ?? throw new InvalidDataException("Source health result is absent.");
        if (!health.LocalRun || !IsSource(health.DataDirectory, source))
            throw new InvalidOperationException("The owner endpoint does not own the default Source data root.");
        return health;
    }
    private static bool IsSource(string? candidate, string source) => candidate is not null && Path.IsPathFullyQualified(candidate) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)), source, StringComparison.OrdinalIgnoreCase);
    private static async Task<byte[]> ReadDeclarationAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length is < 1 or > 16384) throw new InvalidDataException("Unexpected Source connection declaration size.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token);
        if (input.ReadByte() != -1) throw new IOException("Source connection declaration changed during its read.");
        return bytes;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Source canary paths cannot traverse link/reparse targets.");
    }
    private static void Print(object evidence) => Console.WriteLine(JsonSerializer.Serialize(evidence, CerebralJson.Options));

    private sealed class OwnerServiceBridge(CerebralClient client) : IDesktopServiceHost
    {
        public int Commands { get; private set; }
        public async Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments,
            CancellationToken cancellationToken = default)
        {
            if (operation is not ("cortex.assets.providers" or "cortex.assets.read"))
                throw new InvalidOperationException("The live bridge permits only the two native asset read commands.");
            ++Commands;
            var result = await client.CommandAsync(operation, arguments, cancellationToken: cancellationToken);
            return new(result.Success, result.Data, result.Success ? null : "source_native_asset_read_failed");
        }
    }
}
