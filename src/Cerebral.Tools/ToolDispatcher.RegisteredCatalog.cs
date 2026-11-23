using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>A private owner catalog. These descriptors are candidates, never execution grants.</summary>
public sealed record RegisteredToolEntry(ToolDescriptor Descriptor, string Source, string SourceFingerprint);
public sealed record RegisteredToolCatalog(IReadOnlyList<RegisteredToolEntry> Tools,
    IReadOnlyList<string> UnavailableServers, string Generation);

public sealed partial class ToolDispatcher
{
    public async Task<RegisteredToolCatalog> ListRegisteredAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var entries = new List<RegisteredToolEntry>();
        var unavailable = new List<string>();
        void Add(ToolDescriptor descriptor, string source, string sourceIdentity)
        {
            entries.Add(RegisteredEntry(descriptor, source, sourceIdentity));
        }

        foreach (var descriptor in Builtins) Add(descriptor, "builtin", "cerebral.tools");
        foreach (var descriptor in SharedBuiltins) Add(descriptor, "shared", "cerebral.tools");
        foreach (var extension in _extensions)
        {
            var assembly = extension.GetType().Assembly;
            var identity = assembly.FullName + ":" + assembly.ManifestModule.ModuleVersionId.ToString("N");
            foreach (var descriptor in extension.Tools) Add(descriptor, "extension", identity);
        }
        var servers = _settings().McpServers.Where(x => x.Enabled && !x.RequiresConfiguration)
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (servers.GroupBy(x => x.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Registered providers contain a duplicate exact identity.");
        foreach (var server in servers)
        {
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                var fingerprint = CortexOperationPolicy.ProviderFingerprint(server);
                foreach (var descriptor in await DiscoverAsync(server, linked.Token).ConfigureAwait(false))
                {
                    if (!descriptor.Name.StartsWith("mcp." + server.Id + ".", StringComparison.Ordinal) ||
                        descriptor.Capability != "mcp." + server.Id)
                        throw new InvalidDataException("The MCP server returned a descriptor outside its registration scope.");
                    if (!IsPaoGateway(server, descriptor.Name)) Add(descriptor, "mcp:" + server.Id, fingerprint);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unavailable.Add(server.Id);
                _diagnostics[server.Id] = "MCP unavailable: " + error.Message;
            }
        }
        if (entries.GroupBy(x => x.Descriptor.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Registered tools contain a duplicate semantic name.");
        linked.Token.ThrowIfCancellationRequested();
        var ordered = entries.OrderBy(x => x.Descriptor.Name, StringComparer.Ordinal).ToArray();
        // No clock or polling state enters this generation. A changed descriptor or owner route changes it.
        var generation = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            ordered.Select(x => new { x.Descriptor.Name, x.Source, x.SourceFingerprint }), CerebralJson.Options))).ToLowerInvariant();
        return new(ordered, unavailable, generation);
    }
}
