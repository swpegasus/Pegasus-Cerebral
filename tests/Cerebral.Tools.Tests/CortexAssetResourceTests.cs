using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;

internal static class CortexAssetResourceTests
{
    private const string Reference = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Contract = "pegasus.assets/2";

    // Real Windows identity/DPAPI/framing, deterministic resource producer. No installed
    // Cortex data, network, model, asset render or process launch is exercised here.
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Asset transport checks require Windows.");
        var checks = 0;
        void Check(bool passed, string message)
        { if (!passed) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        async Task<Exception?> Failure(Func<Task> operation)
        { try { await operation(); return null; } catch (Exception error) { return error; } }

        await using var fixture = new ResourceFixture();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var settings = new HostSettings { McpServers = [fixture.Server] };
        await using var dispatcher = new ToolDispatcher(() => settings);
        var metadataUri = "cortex://" + Reference;
        var result = await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default);
        using (var body = JsonDocument.Parse(result.GetProperty("contents")[0].GetProperty("text").GetString()!))
            Check(body.RootElement.GetProperty("title").GetString() == "Fixture café" && fixture.Resources == 1 && fixture.OtherOperations == 0,
                "exact metadata crosses authenticated native pipe without catalog, MCP or a tool call");
        foreach (var uri in new[]
        {
            metadataUri + "?part=bytes", metadataUri + "?part=bytes&offset=65536&length=131072",
            "pegasus-asset://v2/search/project.fixture?q=check%20mark&limit=16",
            "pegasus-asset://v2/search/project.fixture?q=&after=opaque%3Acursor",
            "pegasus-asset://v2/search/project.fixture?q=caf%C3%A9"
        })
        {
            result = await dispatcher.ReadCortexAssetResourceAsync("cortex", uri, default);
            Check(result.GetProperty("contents")[0].GetProperty("uri").GetString() == uri, "allowed selector is preserved exactly: " + uri);
        }
        Check(fixture.Connections == 1, "passive resource reads reuse the authenticated native connection");
        fixture.Mode = "maximum-bytes";
        result = await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri + "?part=bytes&length=131072", default);
        Check(Convert.FromBase64String(result.GetProperty("contents")[0].GetProperty("blob").GetString()!).Length == 131072,
            "maximum 128 KiB byte chunk fits the independent 256 KiB resource bound");
        fixture.Mode = "empty-bytes";
        result = await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri + "?part=bytes&offset=65536", default);
        Check(result.GetProperty("_meta").GetProperty("length").GetInt32() == 0,
            "provider-reported empty end-of-asset chunk remains valid with its actual hash");
        fixture.Mode = "ready";
        result = await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri + "?part=bytes&offset=65536", default);
        Check(result.GetProperty("_meta").GetProperty("length").GetInt32() < 65536 &&
            result.GetProperty("_meta").GetProperty("totalSize").GetInt64() == 65536 + result.GetProperty("_meta").GetProperty("length").GetInt32(),
            "short final range is accepted only when it exactly reaches the reported end of the asset");
        var before = fixture.Resources;
        foreach (var invalid in new[]
        {
            "https://example.test/asset", "file:///C:/asset.png", "C:\\asset.png", "CORTEX://" + Reference,
            "cortex://" + Reference.ToUpperInvariant(), metadataUri + "/", metadataUri + "#fragment", "cortex://user@" + Reference,
            metadataUri + ":80", metadataUri + "?", metadataUri + "?part=metadata", metadataUri + "?part=bytes&part=bytes",
            metadataUri + "?part=bytes&offset=-1", metadataUri + "?part=bytes&offset=1", metadataUri + "?part=bytes&length=0",
            metadataUri + "?part=bytes&length=131073", metadataUri + "?part=bytes&offset=9223372036854775808",
            metadataUri + "?part=bytes&unknown=1", "pegasus-asset://v2/search/Project.fixture?q=x", "pegasus-asset://v2/search/project.fixture/../other?q=x",
            "pegasus-asset://v2/search/project.fixture?q=x&limit=17", "pegasus-asset://v2/search/project.fixture?q=x&limit=0",
            "pegasus-asset://v2/search/project.fixture?q=x&q=y", "pegasus-asset://v2/search/project.fixture?q=x&other=1",
            "pegasus-asset://v2/search/project.fixture?q=%", "pegasus-asset://v2/search/project.fixture?q=%C3%28",
            "pegasus-asset://v2/search/project.fixture?q=%00", "pegasus-asset://v2/search/project.fixture?q=x+y",
            "pegasus-asset://v2/search/project.fixture?q=" + new string('x', 257),
            "pegasus-asset://v2/search/project.fixture?q=a%20b%20c%20d%20e%20f%20g%20h%20i",
            "pegasus-asset://v2/search/project.fixture?q=x&after=" + new string('x', 257)
        })
            Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", invalid, default)) is ArgumentException,
                "invalid or unbounded selector is rejected before dispatch");
        Check(fixture.Resources == before, "all invalid selectors cause zero provider reads");

        fixture.Mode = "missing";
        result = await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default);
        Check(result.GetProperty("contents")[0].GetProperty("text").GetString() == "{\"contract\":\"pegasus.assets/2\",\"ok\":false,\"error\":\"asset_missing\"}",
            "ordinary missing asset remains exact passive structured failure data");
        fixture.Mode = "denied";
        var denied = await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default));
        Check(denied is UnauthorizedAccessException && denied.Message.Contains("client_auth_failed", StringComparison.Ordinal) && !denied.Message.Contains(ResourceFixture.PrivateText, StringComparison.Ordinal),
            "outer authorization failure has a bounded code without private provider error text");
        fixture.Mode = "unavailable";
        var unavailable = await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default));
        Check(unavailable is IOException && unavailable.Message.Contains("resource_unavailable", StringComparison.Ordinal) && !unavailable.Message.Contains(ResourceFixture.PrivateText, StringComparison.Ordinal),
            "outer unavailable resource remains a failed owner operation");
        foreach (var mode in new[] { "wrong-uri", "large", "duplicate-envelope", "duplicate-body", "duplicate-meta", "wrong-contract", "wrong-byte-hash", "short-before-eof" })
        {
            fixture.Mode = mode;
            var uri = mode is "wrong-byte-hash" or "short-before-eof" or "duplicate-meta" ? metadataUri + "?part=bytes" : metadataUri;
            Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", uri, default)) is InvalidDataException,
                "resource boundary rejects " + mode + " without adoption");
        }
        fixture.Mode = "wrong-id";
        Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default)) is InvalidDataException,
            "transport rejects a resource response for a different request");
        fixture.Mode = "ready";
        Check((await dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default)).ValueKind == JsonValueKind.Object,
            "a fresh independent read reconnects after invalid framing");

        before = fixture.Resources;
        fixture.Server.Enabled = false;
        Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default)) is UnauthorizedAccessException && fixture.Resources == before,
            "disabled selected native provider cannot read or invoke a fallback");
        fixture.Server.Enabled = true;
        var native = fixture.Server.CortexNative;
        fixture.Server.CortexNative = null;
        Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default)) is UnauthorizedAccessException && fixture.Resources == before,
            "legacy registration cannot launch MCP or HTTP to read an asset");
        fixture.Server.CortexNative = native;
        settings.McpServers = [fixture.Server, CerebralJson.Read<McpRegistration>(CerebralJson.Element(fixture.Server))];
        Check(await Failure(() => dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default)) is UnauthorizedAccessException && fixture.Resources == before,
            "ambiguous selected provider identity is rejected");
        settings.McpServers = [fixture.Server];

        fixture.ReplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, default);
        await fixture.ReplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Server.CortexNative = native! with { CredentialFile = Path.Combine(fixture.Root, "changed.key") };
        fixture.ReplyRelease.TrySetResult();
        Check(await Failure(async () => await pending) is UnauthorizedAccessException, "provider binding is rechecked after the response before resource adoption");
        fixture.Server.CortexNative = native;
        fixture.ReplyEntered = null; fixture.ReplyRelease = null;

        fixture.HelloEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HelloRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var fresh = new ToolDispatcher(() => settings))
        {
            before = fixture.Resources;
            var admission = fresh.ReadCortexAssetResourceAsync("cortex", metadataUri, default);
            await fixture.HelloEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Server.CortexNative = native! with { CredentialFile = Path.Combine(fixture.Root, "changed.key") };
            fixture.HelloRelease.TrySetResult();
            Check(await Failure(async () => await admission) is UnauthorizedAccessException && fixture.Resources == before,
                "provider change during connection admission prevents resource dispatch");
        }
        fixture.Server.CortexNative = native;
        fixture.HelloEntered = null; fixture.HelloRelease = null;
        fixture.ReplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancel = new CancellationTokenSource())
        {
            before = fixture.Resources;
            var cancelled = dispatcher.ReadCortexAssetResourceAsync("cortex", metadataUri, cancel.Token);
            await fixture.ReplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            Check(await Failure(async () => await cancelled.WaitAsync(TimeSpan.FromSeconds(5))) is OperationCanceledException && fixture.Resources == before + 1,
                "canceled native asset exchange never replays its request");
        }
        fixture.ReplyRelease.TrySetResult(); fixture.ReplyEntered = null; fixture.ReplyRelease = null;
        Check(fixture.OtherOperations == 0, "resource reader uses only the passive native resource operation");
        Console.WriteLine($"RESULT: {checks} Cortex asset checks passed (Windows pipe/process identity/DPAPI; deterministic resources, no installed provider).");
        return checks;
    }

    private sealed class ResourceFixture : IAsyncDisposable
    {
        public const string PrivateText = "FIXTURE-PRIVATE-ERROR-BODY";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerebral-asset-fixture-" + Guid.NewGuid().ToString("N"));
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HelloEntered, HelloRelease, ReplyEntered, ReplyRelease;
        public McpRegistration Server { get; }
        public string Mode = "ready";
        public int Resources, Connections, OtherOperations;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _sessions = [];
        private readonly Task _listener;

        public ResourceFixture()
        {
            Directory.CreateDirectory(Root);
            var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
            var endpoint = "cerebral-asset-fixture-" + Guid.NewGuid().ToString("N");
            var credential = Path.Combine(Root, "fixture.key");
            Protect(credential, JsonSerializer.SerializeToUtf8Bytes(new { clientId = "asset-fixture", keyId = new string('a', 32), apiKey = new string('b', 64) }));
            Server = new() { Id = "cortex", Enabled = true, ViaUserConnector = false, Executable = Path.Combine(Root, "never-launch.exe"),
                CortexNative = new(Environment.ProcessPath!, credential, owner.SessionId, PipeName: endpoint) };
            async Task Serve(NamedPipeServerStream pipe)
            {
                using (pipe)
                using (var close = _stop.Token.Register(pipe.Dispose))
                {
                    try
                    {
                        var hello = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.HelloBytes, _stop.Token);
                        if (hello.GetProperty("op").GetString() != "hello") throw new InvalidDataException("Unexpected fixture handshake.");
                        var helloEntered = HelloEntered; var helloRelease = HelloRelease;
                        helloEntered?.TrySetResult();
                        if (helloRelease is not null) await helloRelease.Task.WaitAsync(_stop.Token);
                        await CortexNativeProtocol.WriteAsync(pipe, new { ok = true, contract = CortexNativeProtocol.Contract, product = "Pegasus Cortex",
                            version = "asset-fixture-1", ownerSid = owner.AccountSid, session = owner.SessionId, clientId = "asset-fixture", state = "ready",
                            storageKind = "sqlite-v2", maximumRequestBytes = CortexNativeProtocol.RequestBytes, maximumResponseBytes = CortexNativeProtocol.ResponseBytes,
                            maximumConnections = 24, network = false }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                        while (true)
                        {
                            var request = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.RequestBytes, _stop.Token);
                            if (request.GetProperty("op").GetString() != "resource") { Interlocked.Increment(ref OtherOperations); throw new InvalidDataException("Unexpected fixture operation."); }
                            Interlocked.Increment(ref Resources);
                            var entered = ReplyEntered; var release = ReplyRelease;
                            entered?.TrySetResult(); if (release is not null) await release.Task.WaitAsync(_stop.Token);
                            var id = request.GetProperty("id").GetString();
                            var uri = request.GetProperty("uri").GetString()!;
                            var mode = Mode;
                            if (mode is "denied" or "unavailable")
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = false, error = new { code = mode == "denied" ? "client_auth_failed" : "resource_unavailable", message = PrivateText } }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                            else if (mode == "duplicate-envelope")
                            {
                                using var duplicate = JsonDocument.Parse("{\"id\":" + JsonSerializer.Serialize(id) +
                                    ",\"ok\":true,\"ok\":true,\"result\":" + Resource(uri, mode).GetRawText() + "}");
                                await CortexNativeProtocol.WriteAsync(pipe, duplicate.RootElement, CortexNativeProtocol.ResponseBytes, _stop.Token);
                            }
                            else await CortexNativeProtocol.WriteAsync(pipe, new { id = mode == "wrong-id" ? "wrong" : id, ok = true, result = Resource(uri, mode) }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                        }
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
            _listener = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        try { Started.TrySetResult(); await pipe.WaitForConnectionAsync(_stop.Token); }
                        catch { pipe.Dispose(); throw; }
                        Interlocked.Increment(ref Connections); _sessions.Add(Serve(pipe));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                finally { await Task.WhenAll(_sessions); }
            });
        }

        private static JsonElement Resource(string uri, string mode)
        {
            var responseUri = mode == "wrong-uri" ? "cortex://" + new string('f', 64) : uri;
            if (uri.Contains("part=bytes", StringComparison.Ordinal) && mode != "missing")
            {
                var offset = uri.Contains("offset=65536", StringComparison.Ordinal) ? 65536L : 0;
                var bytes = mode == "maximum-bytes" ? new byte[131072] : mode == "empty-bytes" ? [] : Encoding.UTF8.GetBytes("fixture café");
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var resource = CerebralJson.Element(new { contents = new[] { new { uri = responseUri, mimeType = "image/png", blob = Convert.ToBase64String(bytes) } },
                    _meta = new { contract = Contract, @namespace = "project.fixture", id = "fixture-icon", @ref = Reference, offset, length = bytes.Length,
                        totalSize = offset + bytes.Length + (mode == "short-before-eof" ? 65536 : 0),
                        sha256 = mode == "wrong-byte-hash" ? new string('f', 64) : hash, contentSha256 = hash, complete = offset == 0 && mode != "short-before-eof" } });
                if (mode == "duplicate-meta")
                {
                    var metadata = string.Join(",", resource.GetProperty("_meta").EnumerateObject().Select(p =>
                        JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText()));
                    using var duplicate = JsonDocument.Parse("{\"contents\":" + resource.GetProperty("contents").GetRawText() +
                        ",\"_meta\":{" + metadata + ",\"offset\":0}}");
                    return duplicate.RootElement.Clone();
                }
                return resource;
            }
            string text;
            if (mode == "missing") text = "{\"contract\":\"pegasus.assets/2\",\"ok\":false,\"error\":\"asset_missing\"}";
            else if (mode == "duplicate-body") text = "{\"contract\":\"pegasus.assets/2\",\"contract\":\"pegasus.assets/2\"}";
            else if (uri.StartsWith("pegasus-asset://", StringComparison.Ordinal)) text = CerebralJson.Element(new { contract = Contract, @namespace = "project.fixture",
                items = new[] { new { id = "fixture-icon", @ref = Reference, title = "Fixture café", meaning = "A passive check mark", mimeType = "image/png", size = 12,
                    uri = "cortex://" + Reference } }, scanned = 1, complete = true }).GetRawText();
            else text = CerebralJson.Element(new { contract = mode == "wrong-contract" ? "other/1" : Contract, @namespace = "project.fixture", id = "fixture-icon", @ref = Reference,
                title = "Fixture café", meaning = "A passive check mark", mimeType = "image/png", size = 12, sha256 = new string('c', 64), metadataUri = "cortex://" + Reference,
                bytesUri = "cortex://" + Reference + "?part=bytes", parameters = mode == "large" ? new string('x', 300000) : "{\"passive\":true}" }).GetRawText();
            return CerebralJson.Element(new { contents = new[] { new { uri = responseUri, mimeType = "application/json", text } } });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _listener; _stop.Dispose();
            // This exact task-owned fixture root is created above; no project/user data.
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-asset-fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }

        private static void Protect(string path, byte[] bytes)
        {
            var source = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            try
            {
                Marshal.Copy(bytes, 0, source.Data, bytes.Length);
                if (!CryptProtectData(ref source, null, 0, 0, 0, 1, out var result)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try { var output = new byte[result.Length]; Marshal.Copy(result.Data, output, 0, output.Length); File.WriteAllBytes(path, output); }
                finally { _ = LocalFree(result.Data); }
            }
            finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(source.Data, i, 0); Marshal.FreeHGlobal(source.Data); CryptographicOperations.ZeroMemory(bytes); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob source, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
        [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    }
}
