using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

public sealed class ConnectorConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string ListenUrl { get; set; } = ConnectorProtocol.DefaultUrl;
    public List<McpRegistration> McpServers { get; set; } = [];
    public CortexNativeRelay? CortexRelay { get; set; }
}

public sealed record ConnectorHealth(string Product, int Protocol, string InstanceId, int ProcessId,
    DateTimeOffset StartedAt, string ConfigurationSha256, IReadOnlyList<string> ServerIds, bool Stopping);
public sealed record ConnectorStopRequest(string InstanceId);
public sealed record ConnectorStopReply(int Protocol, string InstanceId, bool Stopping);
public sealed record ConnectorInstance(string InstanceId, string ConfigurationSha256, DateTimeOffset RequestedAt);
public sealed record ConnectorStatus(string State, string Url, string Message, int ProviderCount,
    string? InstanceId = null, int? ProcessId = null);

public static class ConnectorProtocol
{
    public const string Product = "Pegasus Cerebral User Connector";
    public const int Version = 1;
    public const string DefaultUrl = "http://127.0.0.1:6124/";
    public const string StableCortexMcpArgument = "stdio";
    public const int MaxConfigurationBytes = 131_072;
    public const int MaxControlBytes = 16_384;

    public static Uri ValidateUrl(string url)
    {
        if (url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Connector address must be an explicit loopback HTTP origin such as http://127.0.0.1:6124/.");
        return uri;
    }

    public static void ValidateKey(string key)
    {
        if (key is null || key.Length < 16 || Encoding.UTF8.GetByteCount(key) > 4096 || key != key.Trim() || key.Any(char.IsControl))
            throw new ArgumentException("Connector access requires at least 16 characters and at most 4096 UTF-8 bytes, without surrounding whitespace or control characters.");
    }

    public static void ValidateConfiguration(ConnectorConfiguration configuration)
    {
        if (configuration is null || configuration.SchemaVersion != 1 || configuration.McpServers is null
            || configuration.McpServers.Count > 100 || JsonSerializer.SerializeToUtf8Bytes(configuration, CerebralJson.Options).Length > MaxConfigurationBytes)
            throw new ArgumentException("Connector configuration requires schemaVersion 1, at most 100 providers and at most 128 KiB.");
        ValidateUrl(configuration.ListenUrl);
        configuration.CortexRelay?.Validate();
        if (configuration.McpServers.Count(x => x?.CortexNative is not null) > 8)
            throw new ArgumentException("One user connector supports at most eight native Cortex provider bindings.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in configuration.McpServers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 64
                || server.Id.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')) || !ids.Add(server.Id))
                throw new ArgumentException("Connector provider IDs must be unique bounded ASCII identifiers.");
            PortableRegistrationPolicy.RequireReady(server);
            if (server.CortexNativeRequired && server.CortexNative is null)
                throw new ArgumentException("This Cortex definition requires a native owner-session binding and cannot use MCP.");
            if (!server.Enabled || server.ViaUserConnector || !string.IsNullOrEmpty(server.Url))
                throw new ArgumentException("Connector definitions must be enabled direct providers without a forwarding URL.");
            if (server.CortexNative is { } native)
            {
                native.Validate();
                if (native.Relay is not null || configuration.CortexRelay is null || native.SessionId != configuration.CortexRelay.SessionId || server.Arguments is null || server.Arguments.Count != 0)
                    throw new ArgumentException("Native Cortex connector definitions require a matching local relay and no stdio arguments or nested forwarding.");
            }
            if (string.IsNullOrWhiteSpace(server.Executable) || server.Executable.Length > 4096
                || !Path.IsPathFullyQualified(server.Executable) || server.Executable.Any(char.IsControl))
                throw new ArgumentException("Connector providers require explicit absolute executable paths on this computer.");
            if (server.Arguments is null || server.Arguments.Count > 128
                || server.Arguments.Any(x => x is null || x.Length > 4096 || x.Contains('\0'))
                || server.Arguments.Sum(x => x.Length) > 32_768)
                throw new ArgumentException("Connector provider arguments exceed their bounds.");
        }
    }

    // Connector startup definitions use direct stdio while callers use a forwarding URL.
    // Bind executable identity independently of those transport details.
    public static string ExecutionFingerprint(McpRegistration registration)
    {
        var executable = Path.GetFullPath(registration.Executable);
        if (OperatingSystem.IsWindows()) executable = executable.ToUpperInvariant();
        object identity = registration.CortexNative is null
            ? new { registration.Id, Executable = executable, registration.Arguments }
            : new { registration.Id, Executable = executable, registration.Arguments, CortexNative = registration.CortexNative with { Relay = null } };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, CerebralJson.Options))).ToLowerInvariant();
    }

    public static string ConfigurationHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void ValidateHealth(ConnectorHealth health, ConnectorInstance expected)
    {
        if (health is null || expected is null || !Guid.TryParseExact(expected.InstanceId, "N", out _)
            || expected.ConfigurationSha256 is null || expected.ConfigurationSha256.Length != 64 || expected.ConfigurationSha256.Any(x => !char.IsAsciiHexDigit(x))
            || health.Product != Product || health.Protocol != Version || health.InstanceId != expected.InstanceId
            || health.ConfigurationSha256 != expected.ConfigurationSha256 || health.ProcessId <= 0)
            throw new InvalidOperationException("The responding connector does not match the recorded product, protocol, instance and configuration. No process was started or stopped.");
    }
}
