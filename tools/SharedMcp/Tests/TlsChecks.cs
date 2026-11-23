using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.SharedMcp;

/// <summary>In-memory certificates and owned temporary Windows keys for actual TLS; no trust-store changes.</summary>
internal static class TlsChecks
{
    /// <summary>
    /// Explicit opt-in installed-certificate check. Run only after the coordinator authorizes the
    /// machine Root prerequisite. This path never creates certificates, changes trust, or selects a Worker.
    /// </summary>
    public static async Task<int> RunConfiguredAsync(string configurationPath)
    {
        var stage = "configuration";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            if (!Path.IsPathFullyQualified(configurationPath) || configurationPath.Length > 240)
                throw new ArgumentException("An explicit bounded absolute configuration path is required.");
            await using var file = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 2 or > 65536) throw new ArgumentException("Configuration exceeds its bound.");
            using var document = await JsonDocument.ParseAsync(file, new JsonDocumentOptions { MaxDepth = 16 }, token);
            var configuration = document.RootElement;
            var https = configuration.GetProperty("https");
            var serverName = https.GetProperty("serverName").GetString();
            if (serverName is not ("localhost" or "127.0.0.1" or "::1"))
                throw new ArgumentException("This configured check admits only loopback server names.");
            var bind = configuration.TryGetProperty("bindAddress", out var bindValue)
                ? IPAddress.Parse(bindValue.GetString()!) : IPAddress.Loopback;
            if (!IPAddress.IsLoopback(bind)) throw new ArgumentException("This configured check binds only loopback.");
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            };
            var leafReference = https.GetProperty("serverCertificate").Deserialize<WindowsCertificateReference>(jsonOptions)
                ?? throw new ArgumentException("Missing server certificate reference.");
            var caReference = https.GetProperty("certificateAuthority").Deserialize<WindowsCertificateReference>(jsonOptions)
                ?? throw new ArgumentException("Missing public CA reference.");
            if (leafReference.Admit(true) != (StoreLocation.CurrentUser, StoreName.My) ||
                caReference.Admit(false) != (StoreLocation.LocalMachine, StoreName.Root))
                throw new ArgumentException("The configured check requires CurrentUser/My server and LocalMachine/Root CA references.");
            var products = configuration.GetProperty("products");
            if (products.ValueKind != JsonValueKind.Object || products.EnumerateObject().Count() != 1)
                throw new ArgumentException("The configured check admits exactly one Cortex backend.");
            var backendConfig = products.GetProperty("cortex");
            var executable = backendConfig.GetProperty("executable").GetString()!;
            var expectedExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pegasus", "Cortex", "PegasusCortex.exe");
            var arguments = backendConfig.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
            var workingDirectory = backendConfig.GetProperty("workingDirectory").GetString()!;
            if (!Path.IsPathFullyQualified(executable) || !Path.GetFullPath(executable).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase) ||
                arguments.Length != 1 || arguments[0] != "stdio" || !backendConfig.GetProperty("cortexMultiplex").GetBoolean())
                throw new ArgumentException("Only the existing installed Cortex STDIO entry point with multiplexing is admitted.");

            stage = "certificate_references";
            using var leaf = leafReference.Resolve(requirePrivateKey: true);
            using var ca = caReference.Resolve(requirePrivateKey: false);
            McpTlsQualificationResult qualification;
            var toolCount = 0;
            var statusSucceeded = false;
            // These nested lifetimes close HTTP/session/listener/backend before any success output.
            await using (var backend = new StdioMcpDispatcher(new(executable, ["stdio"], workingDirectory) { CortexMultiplex = true }))
            {
                stage = "automatic_https_qualification";
                // Test-only: a fresh ephemeral port avoids occupying the configured development service port.
                await using (var listener = await SharedMcpServer.StartAsync(new()
                {
                    BindAddress = bind, Port = 0, ServerName = serverName,
                    ServerCertificate = leaf, QualificationAuthority = ca,
                    RequestTimeout = TimeSpan.FromSeconds(20)
                }, new Dictionary<string, IMcpDispatcher> { ["cortex"] = backend }, token))
                {
                    qualification = listener.LastTlsQualification ?? throw new InvalidOperationException("Qualification is missing.");
                    if (!qualification.Success || qualification.ClientTrustObservation != "trusted_by_this_process_system_policy")
                        throw new McpTlsQualificationException("ordinary_system_trust_required");
                    var systemPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.System, DisableCertificateDownloads = true,
                        RevocationMode = X509RevocationMode.NoCheck, VerificationFlags = X509VerificationFlags.NoFlag
                    };
                    systemPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                    using var handler = new SocketsHttpHandler
                    {
                        UseProxy = false, AllowAutoRedirect = false,
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            AllowTlsResume = false, CertificateChainPolicy = systemPolicy,
                            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                            {
                                if (errors != SslPolicyErrors.None || certificate is null) return false;
                                using var presented = new X509Certificate2(certificate);
                                return presented.RawData.AsSpan().SequenceEqual(leaf.RawData) &&
                                    presented.MatchesHostname(serverName, allowWildcards: false, allowCommonName: false);
                            }
                        },
                        ConnectCallback = async (_, cancellation) =>
                        {
                            var socket = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                            try
                            {
                                await socket.ConnectAsync(new IPEndPoint(bind, listener.Address.Port), cancellation);
                                return new NetworkStream(socket, ownsSocket: true);
                            }
                            catch { socket.Dispose(); throw; }
                        }
                    };
                    using var http = new HttpClient(handler)
                    { BaseAddress = listener.Address, Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2300000 };
                    string? session = null;
                    async Task<JsonObject?> Send(JsonObject? message, HttpStatusCode expected, string method = "POST")
                    {
                        using var request = new HttpRequestMessage(new HttpMethod(method), "mcp/cortex");
                        request.Headers.Accept.ParseAdd("application/json");
                        request.Headers.Accept.ParseAdd("text/event-stream");
                        if (session is not null)
                        { request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-06-18"); }
                        if (message is not null) request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
                        using var response = await http.SendAsync(request, token);
                        if (response.StatusCode != expected) throw new InvalidOperationException("Unexpected MCP HTTP outcome.");
                        if (session is null && response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.Single();
                        var text = await response.Content.ReadAsStringAsync(token);
                        return text.Length == 0 ? null : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })?.AsObject();
                    }
                    JsonObject Request(int? id, string method, JsonObject? parameters = null)
                    {
                        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters ?? new() };
                        if (id is not null) request["id"] = id.Value;
                        return request;
                    }
                    stage = "https_mcp_initialize";
                    var initialized = await Send(Request(1, "initialize", new()
                    {
                        ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                        ["clientInfo"] = new JsonObject { ["name"] = "configured-tls-qualification", ["version"] = "0" }
                    }), HttpStatusCode.OK);
                    if (session is null || initialized?["id"]?.GetValue<int>() != 1 || initialized?["result"] is not JsonObject)
                        throw new InvalidOperationException("MCP initialization did not establish a session.");
                    await Send(Request(null, "notifications/initialized"), HttpStatusCode.Accepted);
                    stage = "https_tools_list";
                    var listed = await Send(Request(2, "tools/list"), HttpStatusCode.OK);
                    if (listed?["id"]?.GetValue<int>() != 2 || listed?["result"]?["tools"] is not JsonArray tools ||
                        !tools.Any(tool => tool?["name"]?.GetValue<string>() == "cortex_status"))
                        throw new InvalidOperationException("The Cortex status tool was not advertised.");
                    toolCount = tools.Count;
                    stage = "https_cortex_status";
                    var status = await Send(Request(3, "tools/call", new() { ["name"] = "cortex_status", ["arguments"] = new JsonObject() }), HttpStatusCode.OK);
                    if (status?["id"]?.GetValue<int>() != 3 || status?["result"] is not JsonObject result || result["isError"]?.GetValue<bool>() == true)
                        throw new InvalidOperationException("The read-only Cortex status call failed.");
                    statusSucceeded = true;
                    stage = "session_teardown";
                    await Send(null, HttpStatusCode.NoContent, "DELETE");
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true, validUnderSelectedCA = qualification.Success, qualification.ClientTrustObservation,
                serverCertificateId = leafReference.CertificateId, caCertificateId = caReference.CertificateId,
                toolCount, cortexStatusSucceeded = statusSucceeded, ownedListenerAndBackendDisposed = true,
                codexConnectionVerified = false
            }));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or
            IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException or OperationCanceledException or
            McpTlsQualificationException or CertificateReferenceException or NotSupportedException)
        {
            var code = error is McpTlsQualificationException qualification ? qualification.Code :
                error is CertificateReferenceException reference ? reference.Code :
                error is OperationCanceledException ? "configured_tls_deadline" : "configured_tls_check_failed";
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, stage, code, codexConnectionVerified = false }));
            return 2;
        }
    }

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            passed++;
        }

        // These checks call only pure reference admission: no Windows store is opened or changed.
        var publicPin = new string('a', 64);
        var machineRoot = new WindowsCertificateReference { Location = "LocalMachine", Store = "Root", CertificateId = publicPin };
        Check(machineRoot.Admit(requirePrivateKey: false) == (StoreLocation.LocalMachine, StoreName.Root),
            "exact public CA reference admits LocalMachine/Root without accessing the store");
        void RejectReference(WindowsCertificateReference reference, bool privateKey, string label)
        {
            try { reference.Admit(privateKey); }
            catch (CertificateReferenceException error) when (error.Code == "certificate_reference_invalid")
            { Check(true, label); return; }
            throw new InvalidOperationException("FAILED: " + label);
        }
        RejectReference(machineRoot, true, "LocalMachine/Root cannot select a server private key");
        var machineMy = new WindowsCertificateReference { Location = "LocalMachine", Store = "My", CertificateId = publicPin };
        RejectReference(machineMy, false, "LocalMachine public selection is restricted to Root");
        RejectReference(machineMy, true, "LocalMachine/My cannot select a server private key");
        RejectReference(new() { Location = "LocalMachine", Store = "CertificateAuthority", CertificateId = publicPin }, false,
            "LocalMachine intermediate store is outside the admitted public CA reference");
        Check(new WindowsCertificateReference { CertificateId = publicPin }.Admit(requirePrivateKey: true) == (StoreLocation.CurrentUser, StoreName.My),
            "server private-key reference remains CurrentUser/My");
        foreach (var store in new[] { "My", "Root", "CertificateAuthority" })
            Check(new WindowsCertificateReference { Store = store, CertificateId = publicPin }.Admit(requirePrivateKey: false) ==
                (StoreLocation.CurrentUser, Enum.Parse<StoreName>(store)), "existing CurrentUser public reference preserved: " + store);
        RejectReference(new() { Location = "LocalMachine", Store = "Root", CertificateId = "not-a-sha256-pin" }, false,
            "machine public CA reference still requires an exact lowercase SHA256 pin");

        using var rsa = new Certificates();
        using var unrelatedCa = new Certificates();
        using var dispatcher = new FixtureDispatcher();
        var products = new Dictionary<string, IMcpDispatcher> { ["fixture"] = dispatcher };
        var defaults = new McpServerOptions();
        var defaultPolicy = new PrivateNetworkPolicy(defaults.AllowedPeerCidrs);
        Check(IPAddress.IsLoopback(defaults.BindAddress) &&
            defaultPolicy.Allows(IPAddress.Loopback) &&
            !defaultPolicy.Allows(IPAddress.Parse("192.168.1.42")) &&
            !defaultPolicy.Allows(IPAddress.Parse("100.64.0.42")),
            "default listener and peers exclude LAN/VPN");

        var valid = rsa.Leaf("localhost", includeSan: true);
        var anotherValid = rsa.Leaf("localhost", includeSan: true);
        await using (var server = await SharedMcpServer.StartAsync(new()
        {
            Port = 0, ServerName = "localhost", ServerCertificate = valid
        }, products, token))
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, server.Address.Port);
            var success = await McpTlsQualification.ProbeAsync(endpoint, "localhost", rsa.Ca, valid, token);
            Check(success.Success && server.Address.Scheme == "https" && server.Address.Host == "localhost",
                "RSA HTTPS listener passes real DNS SAN/CA/leaf qualification: " + success.Code + "; address=" + server.Address);
            Check(success.ValidUnderSelectedCa && success.ClientTrustObservation == "selected_ca_not_normally_trusted_here",
                "selected custom CA success remains distinct from ordinary system trust");
            Check((await InitializeOverHttps(endpoint, rsa.Ca, token)).StartsWith("HTTP/1.1 503", StringComparison.Ordinal) && dispatcher.InitializeCalls == 0,
                "TLS product dispatch is unavailable before qualification");
            var readiness = await server.QualifyLoopbackAsync(rsa.Ca, token);
            Check(readiness.Success && (await InitializeOverHttps(endpoint, rsa.Ca, token)).StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && dispatcher.InitializeCalls == 1,
                "real HTTPS MCP initialize reaches the injected dispatcher only after qualification");

            var wrongCa = await McpTlsQualification.ProbeAsync(endpoint, "localhost", unrelatedCa.Ca, valid, token);
            Check(!wrongCa.Success && wrongCa.Code == "certificate_chain_rejected",
                "wrong selected CA rejects actual TLS: " + wrongCa.Code);

            var wrongName = await McpTlsQualification.ProbeAsync(endpoint, "other.localhost", rsa.Ca, valid, token);
            Check(!wrongName.Success && wrongName.Code == "certificate_name_mismatch",
                "wrong exact SAN name rejects actual TLS: " + wrongName.Code);

            var wrongPin = await McpTlsQualification.ProbeAsync(endpoint, "localhost", rsa.Ca, anotherValid, token);
            Check(!wrongPin.Success && wrongPin.Code == "certificate_unexpected_leaf",
                "different expected leaf rejects actual TLS: " + wrongPin.Code);
        }

        var validIp = valid;
        await using (var server = await SharedMcpServer.StartAsync(new()
        {
            Port = 0, ServerCertificate = validIp
        }, products, token))
        {
            var result = await McpTlsQualification.ProbeAsync(
                new(IPAddress.Loopback, server.Address.Port), "127.0.0.1", rsa.Ca, validIp, token);
            Check(result.Success && server.Address.Scheme == "https" && server.Address.Host == "127.0.0.1",
                "HTTPS listener passes real IP SAN qualification with default bind name: " + result.Code);
        }

        var expired = rsa.Leaf("localhost", includeSan: true, expired: true);
        var codeSigning = rsa.Leaf("localhost", includeSan: true, serverAuth: false);
        var cnOnly = rsa.Leaf("localhost", includeSan: false);
        var wildcard = rsa.Leaf("*.localhost", includeSan: true);
        foreach (var (certificate, name, label, expectedCode) in new[]
        {
            (expired, "localhost", "expired leaf", "certificate_expired_or_not_yet_valid"),
            (codeSigning, "localhost", "code-signing-only EKU", "certificate_server_auth_required"),
            (cnOnly, "localhost", "CN-only leaf without SAN", "certificate_name_mismatch"),
            (wildcard, "host.localhost", "wildcard SAN", "certificate_name_mismatch")
        })
        {
            await using var raw = new RawHttpsFixture(certificate, token);
            var result = await McpTlsQualification.ProbeAsync(raw.Endpoint, name, rsa.Ca, certificate, token);
            Check(!result.Success && result.Code == expectedCode,
                label + " rejects through actual TLS client: " + result.Code);
        }

        async Task RejectStart(X509Certificate2 certificate, string name, string label)
        {
            try
            {
                await using var unexpected = await SharedMcpServer.StartAsync(new()
                {
                    Port = 0, ServerName = name, ServerCertificate = certificate
                }, products, token);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or
                AuthenticationException or CryptographicException)
            {
                Check(true, label);
                return;
            }
            throw new InvalidOperationException("FAILED: " + label);
        }

        await RejectStart(expired, "localhost", "listener rejects expired server certificate before use");
        await RejectStart(codeSigning, "localhost", "listener rejects code-signing-only server certificate");
        await RejectStart(cnOnly, "localhost", "listener rejects CN-only certificate");
        using var publicOnly = X509CertificateLoader.LoadCertificate(valid.RawData);
        await RejectStart(publicOnly, "localhost", "listener rejects certificate without private key");
        await RejectStart(valid, "other.localhost", "listener rejects advertised name absent from SAN");

        // Dispose before reporting success so named-key deletion and reopen refusal are evidence.
        rsa.Dispose();
        unrelatedCa.Dispose();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed,
            temporaryUserCngKeysDeletedAndReopenRefused = rsa.DeletedTemporaryKeys + unrelatedCa.DeletedTemporaryKeys,
            evidence = "real loopback HTTPS with in-memory RSA certificates and owned temporary nonexportable Windows user CNG key containers, explicitly deleted with reopen refusal verified; no product calls, certificate/trust-store changes, certificate files, private-key exports or external network"
        }));
        return 0;
    }

    private static async Task<string> InitializeOverHttps(IPEndPoint endpoint, X509Certificate2 ca, CancellationToken token)
    {
        using var tcp = new TcpClient(endpoint.AddressFamily);
        await tcp.ConnectAsync(endpoint.Address, endpoint.Port, token);
        using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust, DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck, VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.CustomTrustStore.Add(ca);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", CertificateChainPolicy = policy, AllowTlsResume = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        }, token); // Normal platform name/chain validation; no replacement validation callback.
        const string json = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tls-fixture","version":"0"}}}""";
        var bytes = Encoding.UTF8.GetBytes($"POST /mcp/fixture HTTP/1.1\r\nHost: localhost:{endpoint.Port}\r\nAccept: application/json, text/event-stream\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n" + json);
        await tls.WriteAsync(bytes, token);
        await tls.FlushAsync(token);
        using var result = new MemoryStream();
        var one = new byte[1];
        while (result.Length < 256)
        {
            if (await tls.ReadAsync(one, token) == 0) break;
            result.WriteByte(one[0]);
            if (one[0] == (byte)'\n') break;
        }
        return Encoding.ASCII.GetString(result.ToArray());
    }

    private sealed class Certificates : IDisposable
    {
        private readonly List<AsymmetricAlgorithm> keys = [];
        private readonly List<X509Certificate2> certificates = [];
        private readonly List<(string Name, CngKey Key)> temporaryUserKeys = [];
        private bool disposed;
        public X509Certificate2 Ca { get; }
        public int DeletedTemporaryKeys { get; private set; }

        public Certificates()
        {
            var request = Request("CN=Ephemeral MCP TLS fixture CA", Key());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-4), DateTimeOffset.UtcNow.AddDays(30));
            certificates.Add(Ca);
        }

        public X509Certificate2 Leaf(string name, bool includeSan, bool serverAuth = true, bool expired = false)
        {
            var key = Key(tlsServer: true);
            var request = Request("CN=" + name, key);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.3") }, true));
            if (includeSan)
            {
                var san = new SubjectAlternativeNameBuilder();
                if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
                else
                {
                    san.AddDnsName(name);
                    if (name == "localhost") san.AddIpAddress(IPAddress.Loopback);
                }
                request.CertificateExtensions.Add(san.Build());
            }
            var now = DateTimeOffset.UtcNow;
            using var issued = request.Create(Ca, now.AddDays(expired ? -3 : -1),
                now.AddDays(expired ? -2 : 7), RandomNumberGenerator.GetBytes(16));
            var leaf = issued.CopyWithPrivateKey((RSA)key);
            certificates.Add(leaf);
            return leaf;
        }

        private AsymmetricAlgorithm Key(bool tlsServer = false)
        {
            AsymmetricAlgorithm key;
            if (tlsServer && OperatingSystem.IsWindows())
            {
                // Schannel needs a reopenable key. This exact unique current-user container is
                // fixture-owned, nonexportable, never put in a certificate store, and deleted below.
                var name = "Pegasus.SharedMcp.TlsFixture." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
                var parameters = new CngKeyCreationParameters
                {
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                    KeyCreationOptions = CngKeyCreationOptions.None,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption
                };
                parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
                var ownedKey = CngKey.Create(CngAlgorithm.Rsa, name, parameters);
                temporaryUserKeys.Add((name, ownedKey));
                if (ownedKey.IsEphemeral || ownedKey.ExportPolicy != CngExportPolicies.None)
                    throw new CryptographicException("TLS fixture key ownership/export policy was not retained.");
                key = new RSACng(ownedKey);
            }
            else key = RSA.Create(2048);
            keys.Add(key);
            return key;
        }

        private static CertificateRequest Request(string subject, AsymmetricAlgorithm key) =>
            new(subject, (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var certificate in certificates) certificate.Dispose();
            foreach (var key in keys) key.Dispose();
            var failed = false;
            if (OperatingSystem.IsWindows()) foreach (var (name, key) in temporaryUserKeys)
            {
                try
                {
                    key.Delete();
                    key.Dispose();
                    if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.None))
                        throw new CryptographicException("TLS fixture key remained after deletion.");
                    try
                    {
                        using var unexpected = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.None);
                        failed = true;
                    }
                    catch (CryptographicException error) when (error.HResult is unchecked((int)0x80090016) or unchecked((int)0x80090011))
                    {
                        DeletedTemporaryKeys++;
                    }
                }
                catch (CryptographicException) { failed = true; }
                finally { key.Dispose(); }
            }
            if (failed) throw new InvalidOperationException("TLS fixture temporary-key deletion/reopen refusal could not be verified.");
        }
    }

    /// <summary>The raw listener deliberately permits bad leaves so the actual client must reject them.</summary>
    private sealed class RawHttpsFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime;
        private readonly Task serving;
        public IPEndPoint Endpoint { get; }

        public RawHttpsFixture(X509Certificate2 certificate, CancellationToken cancellationToken)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(8));
            listener.Start(1);
            Endpoint = (IPEndPoint)listener.LocalEndpoint;
            serving = ServeAsync(certificate);
        }

        private async Task ServeAsync(X509Certificate2 certificate)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, lifetime.Token);
                // Bound the HTTPS request and return a recognizable MCP GET method response.
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await tls.ReadAsync(one, lifetime.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 &&
                        header[^2] == 13 && header[^1] == 10) break;
                }
                if (header.Count >= 8192) throw new InvalidDataException("Fixture HTTPS request exceeded its bound.");
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 405 Method Not Allowed\r\nAllow: POST, DELETE\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(response, lifetime.Token);
                await tls.FlushAsync(lifetime.Token);
            }
            catch (Exception error) when (error is AuthenticationException or IOException or
                OperationCanceledException or SocketException)
            {
                // The expected client-side certificate refusal closes this one-use fixture.
            }
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { lifetime.Dispose(); }
        }
    }

    private sealed class FixtureDispatcher : IMcpDispatcher, IDisposable
    {
        public int InitializeCalls;
        public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InitializeCalls);
            return Task.FromResult(new JsonObject
        {
            ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "tls-fixture", ["version"] = "fixture" }
            });
        }
        public Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken) =>
            Task.FromResult(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone(), ["result"] = new JsonObject() });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
