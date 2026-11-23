using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    /// <summary>Trusted archive infrastructure only. Cortex is optional, but its runtime
    /// consumer requires an enrolled native pipe and never launches a fallback CLI.</summary>
    public async Task<JsonElement> ExecuteHostUserPaoAsync(JsonElement arguments, string? workingDirectory, CancellationToken token)
    {
        var selected = SelectHostUserPaoProvider(_settings());
        var server = CerebralJson.Read<McpRegistration>(CerebralJson.Element(selected));
        ValidateRegistration(server);
        var normalized = CortexUserPaoArchiveClient.NativeArguments(arguments, workingDirectory);
        var fingerprint = CortexOperationPolicy.ProviderFingerprint(server);
        var descriptor = (await DiscoverAsync(server, token)).SingleOrDefault(x => x.Name == $"mcp.{server.Id}.pao")
            ?? throw new InvalidOperationException("The selected native Cortex connection does not expose PAO archive execution.");
        ValidateSchema(normalized, descriptor.InputSchema);
        var result = await CallProviderAsync(server, "pao", normalized, token, () =>
        {
            var current = _settings().McpServers.SingleOrDefault(x => x.Id == server.Id && x.Enabled);
            if (current is null || CortexOperationPolicy.ProviderFingerprint(current) != fingerprint)
                throw new UnauthorizedAccessException("The native Cortex archive provider changed before dispatch.");
        });
        if (!TryPaoReceipt(result, out var receipt)) throw new InvalidDataException("Native Cortex did not return a complete PAO archive receipt.");
        return receipt;
    }

    // Initial discovery cannot select a runtime transport. Missing or disabled enrollment
    // leaves archive integration unavailable; it never authorizes another process or provider.
    private static McpRegistration SelectHostUserPaoProvider(HostSettings settings)
    {
        var nativeProviders = settings.McpServers.Where(x => x.CortexNative is not null || x.CortexNativeRequired).ToArray();
        if (nativeProviders.Length == 0)
            throw new InvalidOperationException("Cortex archive service is unavailable. Configure an enrolled native pipe connection; no CLI fallback is permitted.");
        return nativeProviders.FirstOrDefault(x => x.Enabled)
            ?? throw new InvalidOperationException("The native Cortex archive provider is disabled. Enable and inspect its connection; no legacy launcher will be used.");
    }
    private sealed record NativeClientEntry(string Fingerprint, CortexNativePipeClient Client);
    private readonly Dictionary<string, NativeClientEntry> _nativeCortexClients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _nativeClientGate = new(1, 1);

    private async Task<JsonElement> NativeProviderRequestAsync(McpRegistration server, string action, string? tool,
        JsonElement? arguments, CancellationToken token, Action? beforeDispatch = null)
    {
        if (server.CortexNative is not { } native) throw new ArgumentException("Native Cortex binding is required.");
        if (server.ViaUserConnector)
        {
            var relayed = await CortexNativeRelayClient.RequestAsync(server, _settings().AccessKey, action, tool, arguments, token, beforeDispatch);
            return action == "list" ? CerebralJson.Element(CortexNativePipeClient.ParseCatalog(server.Id, relayed)) : relayed;
        }
        CortexNativePipeClient client;
        await _nativeClientGate.WaitAsync(token);
        try
        {
            var current = _settings().McpServers.Where(x => x.Enabled && !x.ViaUserConnector && x.CortexNative is not null)
                .ToDictionary(x => x.Id, CortexOperationPolicy.ProviderFingerprint, StringComparer.Ordinal);
            foreach (var old in _nativeCortexClients.ToArray())
                if (!current.TryGetValue(old.Key, out var fingerprint) || fingerprint != old.Value.Fingerprint)
                {
                    _nativeCortexClients.Remove(old.Key); await old.Value.Client.DisposeAsync();
                }
            var selected = CortexOperationPolicy.ProviderFingerprint(server);
            if (!current.TryGetValue(server.Id, out var admitted) || admitted != selected)
                throw new UnauthorizedAccessException("The native Cortex registration changed before its connection was acquired.");
            if (!_nativeCortexClients.TryGetValue(server.Id, out var entry))
            {
                if (_nativeCortexClients.Count >= 8) throw new InvalidOperationException("Cerebral's native Cortex connection capacity is full. Disable an unused provider before adding another.");
                entry = new(selected, new(native)); _nativeCortexClients.Add(server.Id, entry);
            }
            if (entry.Fingerprint != selected) throw new UnauthorizedAccessException("The native Cortex provider changed while acquiring its connection.");
            client = entry.Client;
        }
        finally { _nativeClientGate.Release(); }
        var result = await client.RequestAsync(server.Id, action, tool, arguments, token, beforeDispatch);
        return action == "list" ? CerebralJson.Element(CortexNativePipeClient.ParseCatalog(server.Id, result)) : result;
    }
    // Route discovery, semantic calls and PAO receipt lookups through the same transport
    // boundary. Grants, scope binding and effect validation remain with the caller.
    private async Task<JsonElement> CallProviderAsync(McpRegistration server, string tool,
        JsonElement arguments, CancellationToken cancellationToken, Action? beforeDispatch = null)
    {
        ValidateRegistration(server);
        if (server.CortexNative is not null)
            return await NativeProviderRequestAsync(server, "call", tool, arguments, cancellationToken, beforeDispatch);
        if (server.ViaUserConnector)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeDispatch?.Invoke();
            return await ConnectorRequestAsync(server, "call", tool, arguments, cancellationToken);
        }
        await using var client = await McpStdioClient.ConnectAsync(server, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        beforeDispatch?.Invoke();
        return await client.CallToolAsync(tool, arguments, cancellationToken);
    }

    private async Task<McpServerIdentity?> ProviderIdentityAsync(McpRegistration server, CancellationToken cancellationToken)
    {
        ValidateRegistration(server);
        if (server.ViaUserConnector)
            return (await ConnectorRequestAsync(server, "identity", null, null, cancellationToken)).Deserialize<McpServerIdentity>(CerebralJson.Options);
        await using var client = await McpStdioClient.ConnectAsync(server, cancellationToken);
        return client.ServerIdentity;
    }
}
