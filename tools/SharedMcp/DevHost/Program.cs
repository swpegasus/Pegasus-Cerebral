using System.Net;
using System.Text.Json;
using Pegasus.SharedMcp;

if (args.Length != 1) { Console.Error.WriteLine("Usage: DevHost <explicit-backends.json>"); return 2; }
var config = JsonSerializer.Deserialize<HostConfig>(await File.ReadAllTextAsync(Path.GetFullPath(args[0])),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
    ?? throw new ArgumentException("Missing host configuration.");
var backends = new Dictionary<string, IMcpDispatcher>(StringComparer.Ordinal);
try
{
    if (config.Https is { } https && (string.IsNullOrWhiteSpace(https.ServerName) || https.ServerCertificate is null || https.CertificateAuthority is null))
        throw new CertificateReferenceException("https_configuration_incomplete");
    using var serverCertificate = config.Https?.ServerCertificate.Resolve(requirePrivateKey: true);
    using var certificateAuthority = config.Https?.CertificateAuthority.Resolve(requirePrivateKey: false);
    foreach (var (product, item) in config.Products)
        backends.Add(product, new StdioMcpDispatcher(new(item.Executable, item.Arguments, item.WorkingDirectory)
            { CortexMultiplex = item.CortexMultiplex }));
    await using var server = await SharedMcpServer.StartAsync(new()
    {
        BindAddress = IPAddress.Parse(config.BindAddress), Port = config.Port, AllowedPeerCidrs = config.AllowedPeerCidrs,
        ServerCertificate = serverCertificate, ServerName = config.Https?.ServerName,
        QualificationAuthority = certificateAuthority,
        Trace = config.TraceEnabled ? trace => Console.WriteLine(JsonSerializer.Serialize(trace)) : null
    }, backends);
    if (certificateAuthority is not null)
    {
        var qualification = server.LastTlsQualification ?? throw new InvalidOperationException("HTTPS startup qualification is missing.");
        Console.WriteLine(JsonSerializer.Serialize(new { eventName = "https-loopback-qualification", validUnderSelectedCA = qualification.Success,
            qualification.Code, qualification.ClientTrustObservation, codexConnectionVerified = false }));
        if (!qualification.Success) return 3;
    }
    Console.WriteLine($"Development MCP endpoint: {server.Address}mcp/<product>; products={string.Join(',', backends.Keys)}");
    var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };
    await shutdown.Task;
}
catch (CertificateReferenceException error)
{
    Console.Error.WriteLine("MCP startup rejected: " + error.Code + "; no certificate was exported or trusted.");
    return 2;
}
catch (McpTlsQualificationException error)
{
    Console.Error.WriteLine("MCP HTTPS startup rejected: " + error.Code + "; no trust was installed and no product call was admitted.");
    return 3;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine("MCP startup rejected: explicit configuration, server certificate/name, or listener configuration is invalid; no trust change was made.");
    return 2;
}
finally { foreach (var backend in backends.Values) await backend.DisposeAsync(); }
return 0;

sealed class HostConfig
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 47831;
    public string[] AllowedPeerCidrs { get; init; } = ["127.0.0.0/8", "::1/128"];
    public bool TraceEnabled { get; init; }
    public HttpsConfig? Https { get; init; }
    public Dictionary<string, BackendConfig> Products { get; init; } = [];
}
sealed class HttpsConfig
{
    public required string ServerName { get; init; }
    public required WindowsCertificateReference ServerCertificate { get; init; }
    public required WindowsCertificateReference CertificateAuthority { get; init; }
}
sealed class BackendConfig
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public bool CortexMultiplex { get; init; }
}
