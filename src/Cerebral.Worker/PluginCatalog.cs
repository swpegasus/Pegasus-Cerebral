using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Declarative registry over existing MCP configuration; installation never launches an executable.</summary>
public sealed class PluginCatalog(StateStore store, Configuration configuration)
{
    public const int MaxManifestBytes = 65_536;
    private sealed record InstalledPlugin(PluginManifest Manifest, DateTimeOffset InstalledAt);
    private static T Copy<T>(T value) => CerebralJson.Read<T>(CerebralJson.Element(value));

    public IReadOnlyList<PluginSummary> List() => store.Locked(() =>
    {
        var servers = configuration.Get().McpServers;
        return (IReadOnlyList<PluginSummary>)store.List<InstalledPlugin>("plugin").Take(100).Select(item =>
        {
            var manifest = item.Manifest;
            var server = servers.FirstOrDefault(x => x.Id == manifest.Server.Id);
            return new PluginSummary(manifest.Id, manifest.Name, manifest.Version, manifest.Protocol, manifest.Kind, manifest.Description,
                server is not null, server?.Enabled == true, server is not null && !SameRegistration(server, manifest.Server), item.InstalledAt);
        }).ToList();
    });

    public PluginInspection Validate(PluginManifest manifest)
    {
        ValidateManifest(manifest);
        var normalized = Copy(manifest);
        normalized.Server.Enabled = false;
        return new(normalized, null, $"mcp.{normalized.Server.Id}", $"mcp.{normalized.Server.Id}.", false,
            ["Installation registers this MCP connection disabled and grants no agent access.",
             "Enable it in Tools & MCP, then grant its capability and exact tool names to selected profiles.",
             "Executable paths and arguments apply on the configured execution host. User-session providers require a running connector."]);
    }

    public PluginInspection Inspect(string id) => store.Locked(() =>
    {
        ValidateId(id);
        var installed = store.Get<InstalledPlugin>("plugin", id) ?? throw new ArgumentException("Extension is not installed.");
        var server = configuration.Get().McpServers.FirstOrDefault(x => x.Id == installed.Manifest.Server.Id);
        var inspection = Validate(installed.Manifest);
        return inspection with { ConfiguredServer = server, ConfigurationChanged = server is not null && !SameRegistration(server, installed.Manifest.Server) };
    });

    public PluginInspection Install(PluginManifest manifest)
    {
        var accepted = Validate(manifest).Manifest;
        return store.Locked(() =>
        {
            var current = store.Get<InstalledPlugin>("plugin", accepted.Id);
            if (current is not null)
            {
                if (JsonSerializer.Serialize(current.Manifest, CerebralJson.Options) != JsonSerializer.Serialize(accepted, CerebralJson.Options))
                    throw new InvalidOperationException("Extension ID is already installed with a different manifest. Remove the old declaration before changing it.");
                return Inspect(accepted.Id);
            }
            if (store.List<InstalledPlugin>("plugin").Count >= 100) throw new InvalidOperationException("Extension catalog limit is 100.");
            var settings = configuration.Get();
            if (settings.McpServers.Any(x => x.Id == accepted.Server.Id)) throw new InvalidOperationException("The MCP server ID is already configured. Existing registrations are never replaced by extension installation.");
            settings.McpServers.Add(Copy(accepted.Server));
            configuration.Save(settings);
            store.Put("plugin", accepted.Id, new InstalledPlugin(accepted, DateTimeOffset.UtcNow));
            return Inspect(accepted.Id);
        });
    }

    public PluginRemoval Remove(string id) => store.Locked(() =>
    {
        ValidateId(id);
        var installed = store.Get<InstalledPlugin>("plugin", id) ?? throw new ArgumentException("Extension is not installed.");
        var settings = configuration.Get();
        var serverId = installed.Manifest.Server.Id;
        var server = settings.McpServers.FirstOrDefault(x => x.Id == serverId);
        if (server?.Enabled == true) throw new InvalidOperationException("Disable this MCP connection in Tools & MCP before removing its extension.");
        if (store.List<AgentProfile>("agent").Any(x => x.Capabilities.Contains($"mcp.{serverId}", StringComparer.Ordinal)
            || x.AllowedTools.Any(tool => tool.StartsWith($"mcp.{serverId}.", StringComparison.Ordinal))))
            throw new InvalidOperationException("Remove this extension's capability and tool grants from configured agents before removing it.");
        if (server is not null && !SameRegistration(server, installed.Manifest.Server))
            throw new InvalidOperationException("The MCP registration changed after installation. Inspect it, then remove that registration explicitly in Tools & MCP before removing this declaration.");
        if (server is not null) { settings.McpServers.Remove(server); configuration.Save(settings); }
        store.Delete("plugin", id);
        return new PluginRemoval(id, server is not null);
    });

    private static bool SameRegistration(McpRegistration first, McpRegistration second) =>
        first.Id == second.Id && first.Executable == second.Executable && first.ViaUserConnector == second.ViaUserConnector
        && first.Url == second.Url && first.CortexNative == second.CortexNative && first.CortexNativeRequired == second.CortexNativeRequired && first.Arguments is not null && second.Arguments is not null && first.Arguments.SequenceEqual(second.Arguments, StringComparer.Ordinal);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new ArgumentException("Extension and MCP IDs require 1-64 ASCII letters, digits, hyphens or underscores.");
    }
    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest is null || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(manifest, CerebralJson.Options)) > MaxManifestBytes)
            throw new ArgumentException("Extension manifest must be at most 64 KiB.");
        if (manifest.SchemaVersion != 1 || manifest.Protocol != 1 || manifest.Kind != "mcp")
            throw new ArgumentException("Only manifest schemaVersion=1, protocol=1 and kind=mcp are supported.");
        ValidateId(manifest.Id);
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128 || manifest.Name.Any(char.IsControl))
            throw new ArgumentException("Extension name requires 1-128 characters without control characters.");
        if (manifest.Version is null || manifest.Version.Length > 64 || !Regex.IsMatch(manifest.Version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("Use a bounded version such as 1.0.0 or 1.0.0-preview.");
        if (manifest.Description is null || manifest.Description.Length > 2048 || manifest.Description.Contains('\0'))
            throw new ArgumentException("Extension description must be at most 2048 characters.");
        var server = manifest.Server ?? throw new ArgumentException("An MCP server registration is required.");
        if (server.CortexNative is not null || server.CortexNativeRequired) throw new ArgumentException("Native Cortex enrollment is an owner connection setting, not an MCP plugin declaration.");
        ValidateId(server.Id);
        if (server.Id != manifest.Id) throw new ArgumentException("The MCP server ID must match the extension ID.");
        if (string.IsNullOrWhiteSpace(server.Executable) || server.Executable.Length > 4096 || !Path.IsPathFullyQualified(server.Executable) || server.Executable.Any(char.IsControl))
            throw new ArgumentException("Use an explicit absolute executable path on the provider's host.");
        if (server.Arguments is null || server.Arguments.Count > 128 || server.Arguments.Any(x => x is null || x.Length > 4096 || x.Contains('\0')) || server.Arguments.Sum(x => x.Length) > 32_768)
            throw new ArgumentException("MCP arguments must be separate bounded strings (128 arguments, 32768 characters total).");
        if (server.ViaUserConnector)
        {
            if (server.Url is null || server.Url.Length > 2048 || !Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)
                || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("User-connector providers require an explicit loopback HTTP origin such as http://127.0.0.1:6124.");
        }
        else if (!string.IsNullOrEmpty(server.Url)) throw new ArgumentException("Direct MCP providers use stdio; omit their URL.");
    }
}
