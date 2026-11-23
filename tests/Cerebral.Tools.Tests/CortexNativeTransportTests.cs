using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;

internal static class CortexNativeTransportTests
{
    // Actual Windows pipe/process identity and DPAPI, with a deterministic semantic fixture.
    // This does not claim an installed Cortex, real persistence or model execution.
    public static async Task<int> RunAsync(bool policyOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This transport acceptance requires Windows.");
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        var classifier = typeof(ToolDispatcher).Assembly.GetType("Cerebral.Tools.CortexNativePipeClient", throwOnError: true)!
            .GetMethod("ClosedFailureNumbers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Native failure classifier is unavailable.");
        ValueTuple<int?, int> ClosedNumbers(Exception error) =>
            (ValueTuple<int?, int>)classifier.Invoke(null, new object[] { error })!;
        var directWindows = new System.ComponentModel.Win32Exception(5);
        var directNumbers = ClosedNumbers(directWindows);
        Check(directNumbers.Item1 == 5 && directNumbers.Item2 == directWindows.HResult,
            "native diagnostic retains a direct numeric Windows error and original HResult");
        var wrappedWindows = new IOException("fixture", new System.ComponentModel.Win32Exception(231));
        var wrappedNumbers = ClosedNumbers(wrappedWindows);
        Check(wrappedNumbers.Item1 == 231 && wrappedNumbers.Item2 == wrappedWindows.HResult,
            "native diagnostic retains one wrapped numeric Windows error and outer HResult");
        var plainIo = new IOException("fixture");
        var plainNumbers = ClosedNumbers(plainIo);
        Check(plainNumbers.Item1 is null && plainNumbers.Item2 == plainIo.HResult,
            "native diagnostic reports original HResult without inventing a Windows error");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-native-cortex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        var endpoint = "cerebral-native-cortex-fixture-" + Guid.NewGuid().ToString("N");
        var keyFile = Path.Combine(root, "fixture.key");
        var clientId = "cerebral-fixture"; var keyId = new string('a', 32); var key = new string('b', 64);
        var managedClientId = "cerebral-" + new string('a', 32);
        var installedRoot = Path.Combine(root, "InstalledCortex");
        var legacyData = Path.Combine(installedRoot, "state");
        var relocatedData = Path.Combine(root, "RegisteredCortexData");
        var installedHost = Path.Combine(installedRoot, "bin", "host", "PegasusCortex.Host.exe");
        var oldManagedKey = Path.Combine(legacyData, "users", owner.AccountSid, "config", "connections", managedClientId + ".key");
        var managed = new CortexNativeConnection(installedHost, oldManagedKey, owner.SessionId);
        var relocated = CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid,
            new InstalledCortexLocation(installedRoot, relocatedData));
        var newManagedKey = Path.Combine(relocatedData, "users", owner.AccountSid, "config", "connections", managedClientId + ".key");
        Check(relocated.EffectivePath == newManagedKey && relocated.ManagedClientId == managedClientId,
            "only the exact old managed key reference maps to Cortex's separately registered data root");
        Check(CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid,
                new InstalledCortexLocation(installedRoot, legacyData)).EffectivePath == oldManagedKey &&
            CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid,
                new InstalledCortexLocation(installedRoot, legacyData + Path.DirectorySeparatorChar)).ManagedClientId is null &&
            CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid, null).EffectivePath == oldManagedKey,
            "missing or unchanged Cortex DataLocation preserves legacy key admission");
        var manual = new CortexNativeConnection(Environment.ProcessPath!, keyFile, owner.SessionId, PipeName: endpoint);
        Check(CortexManagedCredentialLocator.Resolve(manual, owner.AccountSid,
                new InstalledCortexLocation(installedRoot, relocatedData)).EffectivePath == keyFile &&
            CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid + "-999",
                new InstalledCortexLocation(installedRoot, relocatedData)).EffectivePath == oldManagedKey &&
            CortexManagedCredentialLocator.Resolve(managed, owner.AccountSid,
                new InstalledCortexLocation(Path.Combine(root, "OtherInstall"), relocatedData)).EffectivePath == oldManagedKey,
            "manual, wrong-owner and wrong-installation credentials remain literal");
        var wrongHost = managed with { HostExecutable = Path.Combine(installedRoot, "bin", "host", "Other.exe") };
        Check(CortexManagedCredentialLocator.Resolve(wrongHost, owner.AccountSid,
                new InstalledCortexLocation(installedRoot, relocatedData)).EffectivePath == oldManagedKey,
            "a different saved host cannot acquire managed credential relocation");
        var legacyEnrolledKey = Path.Combine(legacyData, "users", owner.AccountSid, "config", "connections", clientId + ".key");
        var legacyEnrolled = managed with { CredentialFile = legacyEnrolledKey };
        var relocatedLegacy = CortexManagedCredentialLocator.Resolve(legacyEnrolled, owner.AccountSid,
            new InstalledCortexLocation(installedRoot, relocatedData));
        Check(relocatedLegacy.ManagedClientId == clientId && relocatedLegacy.EffectivePath ==
            Path.Combine(relocatedData, "users", owner.AccountSid, "config", "connections", clientId + ".key"),
            "an older bounded enrolled client ID in the exact managed tree relocates without re-enrollment");
        var otherClient = managed with { CredentialFile = Path.Combine(legacyData, "users", owner.AccountSid,
            "config", "connections", "bad.name.key") };
        Check(CortexManagedCredentialLocator.Resolve(otherClient, owner.AccountSid,
                new InstalledCortexLocation(installedRoot, relocatedData)).EffectivePath == otherClient.CredentialFile,
            "an unrecognized client ID cannot acquire managed relocation");
        CortexManagedCredentialLocator.RequireManagedClientId(relocated, managedClientId);
        CortexManagedCredentialLocator.RequireManagedClientId(
            CortexManagedCredentialLocator.Resolve(manual, owner.AccountSid, new InstalledCortexLocation(installedRoot, relocatedData)), clientId);
        var wrongManagedIdRefused = false;
        try { CortexManagedCredentialLocator.RequireManagedClientId(relocated, clientId); }
        catch (UnauthorizedAccessException) { wrongManagedIdRefused = true; }
        var wrongLegacyIdRefused = false;
        try { CortexManagedCredentialLocator.RequireManagedClientId(relocatedLegacy, managedClientId); }
        catch (UnauthorizedAccessException) { wrongLegacyIdRefused = true; }
        Check(wrongManagedIdRefused && wrongLegacyIdRefused,
            "relocated protected keys with a different modern or legacy client ID are refused before hello");
        const string privateFixture = "FIXTURE-PRIVATE-DATA";
        const string privateError = "FIXTURE-PRIVATE-ERROR\nDETAIL";
        const string privateCode = "FIXTURE-PRIVATE-CODE\nDETAIL";
        const string privateIdentity = "FIXTURE-PRIVATE-IDENTITY\nDETAIL";
        const string privateCredential = "FIXTURE-PRIVATE-CREDENTIAL\nDETAIL";
        ProtectFile(keyFile, CerebralJson.Element(new { clientId, keyId, apiKey = key }));
        var connections = 0; var catalog = 0; var calls = 0; var writes = 0; var pings = 0; var mode = "ready"; var pingMode = "ready"; var denyHello = false;
        var helloMode = "ready";
        using var sessionAccepted = new SemaphoreSlim(0);
        var listenerReadiness = new System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource>();
        TaskCompletionSource ReadyForAdmission(int ordinal) => listenerReadiness.GetOrAdd(ordinal,
            _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var helloEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helloRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NamedPipeServerStream? currentSession = null; TaskCompletionSource? currentSessionClosed = null;
        var pingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? afterReadDispatch = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new List<Task>();
        string? searchVariant = null;
        var receivedArguments = CerebralJson.Element(new { });
        var receivedByResult = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task Serve(NamedPipeServerStream pipe, TaskCompletionSource closed)
        {
            using (pipe)
            using (var close = stop.Token.Register(pipe.Dispose))
            {
                try
                {
                    var hello = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.HelloBytes, stop.Token);
                    Check(hello.GetProperty("op").GetString() == "hello" && hello.GetProperty("contract").GetString() == CortexNativeProtocol.Contract &&
                        !hello.TryGetProperty("jsonrpc", out _) && hello.GetProperty("apiKey").GetString() == key, "native hello uses the exact contract and protected fixture key without MCP initialize");
                    if (denyHello)
                    {
                        await CortexNativeProtocol.WriteAsync(pipe, new { ok = false, error = new { code = "client_auth_failed" } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                        return;
                    }
                    switch (helloMode)
                    {
                        case "slow":
                            helloEntered.TrySetResult(); await helloRelease.Task.WaitAsync(stop.Token); return;
                        case "drop": return;
                        case "bad-frame":
                            await pipe.WriteAsync(Raw(2, 0, "{}"), stop.Token); await pipe.FlushAsync(stop.Token); return;
                        case "array":
                            await CortexNativeProtocol.WriteAsync(pipe, new[] { privateIdentity }, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                        case "null":
                            await CortexNativeProtocol.WriteAsync(pipe, (object?)null, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                        case "missing-ok":
                            await CortexNativeProtocol.WriteAsync(pipe, new { error = new { code = privateCode, message = privateError } }, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                        case "invalid-ok":
                            await CortexNativeProtocol.WriteAsync(pipe, new { ok = privateError, product = privateIdentity }, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                        case "auth-denied": case "contract-denied": case "unknown-denied":
                            var denialCode = helloMode == "auth-denied" ? "client_auth_failed" : helloMode == "contract-denied" ? "contract_mismatch" : privateCode;
                            await CortexNativeProtocol.WriteAsync(pipe, new { ok = false, error = new { code = denialCode, message = privateError }, product = privateIdentity }, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                        case "identity-type":
                            await CortexNativeProtocol.WriteAsync(pipe, new { ok = true, session = privateIdentity }, CortexNativeProtocol.ResponseBytes, stop.Token); return;
                    }
                    await CortexNativeProtocol.WriteAsync(pipe, new { ok = true, contract = CortexNativeProtocol.Contract,
                        product = helloMode == "identity" ? privateIdentity : "Pegasus Cortex", version = "fixture-1", ownerSid = owner.AccountSid, session = owner.SessionId,
                        clientId, state = "ready", storageKind = "sqlite-v2", maximumRequestBytes = CortexNativeProtocol.RequestBytes,
                        maximumResponseBytes = CortexNativeProtocol.ResponseBytes, maximumConnections = 24, network = false }, CortexNativeProtocol.ResponseBytes, stop.Token);
                    while (true)
                    {
                        var request = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.RequestBytes, stop.Token);
                        var id = request.GetProperty("id").GetString();
                        var op = request.GetProperty("op").GetString();
                        if (op == "ping")
                        {
                            pings++;
                            Check(request.EnumerateObject().All(value => value.Name is "id" or "op"), "lease checks use only the fixed native ping envelope");
                            if (pingMode == "drop") return;
                            if (pingMode == "slow") { pingEntered.TrySetResult(); await pingRelease.Task.WaitAsync(stop.Token); }
                            if (pingMode is "denied" or "revoked")
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = false, error = new { code = pingMode == "denied" ? "client_auth_failed" : "client_revoked_or_expired" } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                            else if (pingMode == "bad-frame")
                            {
                                await pipe.WriteAsync(Raw(2, 0, "{}"), stop.Token); await pipe.FlushAsync(stop.Token);
                            }
                            else
                                await CortexNativeProtocol.WriteAsync(pipe, new { id = pingMode == "wrong-id" ? "another-probe" : id, ok = true,
                                    result = new { state = pingMode == "bad-state" ? "unavailable" : "ready" } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                        }
                        else if (op == "catalog")
                        {
                            catalog++;
                            IEnumerable<object> operations = policyOnly
                                ? CortexPolicyTests.FixtureOperations(searchVariant)
                                : CortexConnectionContract.CoreTools.Select(name => (object)new { name, description = "fixture", inputSchema = new { type = "object" } });
                            await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = true, result = new { operations, defaults = new { readNamespaces = new[] { "project.fixture" }, writeNamespaces = new[] { "project.fixture" }, pao = true }, namespacePolicy = "selection", resources = Array.Empty<object>(), resourceTemplates = Array.Empty<object>() } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                        }
                        else if (op == "call")
                        {
                            calls++;
                            if (policyOnly)
                            {
                                var tool = request.GetProperty("name").GetString()!;
                                receivedArguments = request.GetProperty("arguments").Clone();
                                var fixture = CerebralJson.Element(tool == "cortex_read"
                                    ? CortexPolicyTests.FixtureRead(receivedArguments)
                                    : CortexPolicyTests.FixtureCall(tool, receivedArguments));
                                // Retain the malformed nested result for the transport rejection
                                // case; the specific multi-copy defense is also checked purely.
                                var policyResult = tool == "cortex_read" &&
                                    receivedArguments.TryGetProperty("recordId", out var selectedId) &&
                                    selectedId.GetString() == "exact-conflicting-copy"
                                    ? fixture
                                    : fixture.TryGetProperty("structuredContent", out var structured) ? structured : receivedArguments;
                                // Bind each assertion to the request that produced its result,
                                // including collections of several completed policy reads.
                                var projected = CerebralJson.Element(new { isError = false, structuredContent = policyResult });
                                receivedByResult[projected.GetRawText()] = receivedArguments.Clone();
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = true, result = policyResult },
                                    CortexNativeProtocol.ResponseBytes, stop.Token);
                                continue;
                            }
                            if (request.GetProperty("name").GetString() == "cortex_knowledge")
                            {
                                writes++;
                                Check(mode == "write-drop", "fixture observes one admitted mutation before dropping its acknowledgement");
                                return;
                            }
                            if (request.GetProperty("name").GetString() == "cortex_read")
                            {
                                using var read = JsonDocument.Parse(JsonSerializer.Serialize(CortexPolicyTests.FixtureRead(request.GetProperty("arguments"))));
                                afterReadDispatch?.Invoke();
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = true, result = read.RootElement.GetProperty("structuredContent") },
                                    CortexNativeProtocol.ResponseBytes, stop.Token);
                                continue;
                            }
                            Check(!request.TryGetProperty("method", out _) && request.GetProperty("name").GetString() == "cortex_status", "native calls carry typed name and arguments rather than tools/call JSON-RPC");
                            if (mode == "drop") return;
                            if (mode == "slow") { slow.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
                            if (mode == "denied")
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = false, error = new { code = "client_auth_failed" } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                            else if (mode == "large")
                            {
                                var empty = new { id, ok = true, result = new { text = "" } };
                                var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
                                await CortexNativeProtocol.WriteAsync(pipe, new { id, ok = true, result = new { text = new string('x', CortexNativeProtocol.ResponseBytes - overhead) } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                            }
                            else
                                await CortexNativeProtocol.WriteAsync(pipe, new { id = mode == "wrong-id" ? "another-request" : id, ok = true,
                                    result = new { product = "Pegasus Cortex", version = "fixture-1", state = "ready", storageKind = "sqlite-v2", network = false, service = false, text = "café\nSecond line." } }, CortexNativeProtocol.ResponseBytes, stop.Token);
                        }
                        else throw new InvalidOperationException("Unexpected native fixture operation.");
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { closed.TrySetResult(); }
            }
        }
        var listener = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    // The serial driver holds at most its base, probe and connector sessions plus this pending listener.
                    var pipe = InstalledWorkerWindows.CreateServer(endpoint, owner.AccountSid, owner.AccountSid,
                        first: connections == 0, maximumInstances: 9);
                    try
                    {
                        var accepting = pipe.WaitForConnectionAsync(stop.Token);
                        ReadyForAdmission(connections + 1).TrySetResult(); started.TrySetResult();
                        await accepting;
                    }
                    catch { pipe.Dispose(); throw; }
                    connections++;
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    currentSession = pipe; currentSessionClosed = closed; sessions.Add(Serve(pipe, closed));
                    sessionAccepted.Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(sessions); }
        });
        async Task CloseCurrentSessionAsync()
        {
            var pipe = currentSession ?? throw new InvalidOperationException("No fixture session is available to close.");
            var closed = currentSessionClosed ?? throw new InvalidOperationException("No fixture session completion is available.");
            pipe.Dispose(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var native = new CortexNativeConnection(Environment.ProcessPath!, keyFile, owner.SessionId, PipeName: endpoint);
            var server = new McpRegistration { Id = "cortex", Executable = Path.Combine(root, "never-launched.exe"), Enabled = true, ViaUserConnector = false, CortexNative = native };
            var host = new HostSettings { McpServers = [server], CortexNamespaces = ["project.fixture"], CortexWriteNamespaces = ["project.fixture"] };
            var profile = new AgentProfile { AllowedTools = ["mcp.cortex.cortex_status"], Capabilities = ["mcp.cortex"] };
            var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job");
            await using var dispatcher = new ToolDispatcher(() => host);
            var call = new ToolCall("mcp.cortex.cortex_status", CerebralJson.Element(new { }));
            if (policyOnly)
            {
                host.CortexNamespaces = ["knowledge.shared", "project.pegasus-cerebral"];
                host.CortexWriteNamespaces = ["project.pegasus-cerebral"];
                JsonElement Received(ToolResult result) => receivedByResult.TryGetValue(result.Content, out var arguments)
                    ? arguments.Clone() : throw new InvalidOperationException("Native policy result has no matching captured request.");
                await CortexPolicyTests.RunAsync(dispatcher, host, context, Check, server.Id,
                    variant => searchVariant = variant, Received);
                await CortexPolicyTests.RunOwnerPolicyAsync(dispatcher, host, context, Check, server.Id, Received);
                Console.WriteLine($"RESULT: {checks} native Cortex policy assertions passed (owned Windows pipe/current-process peer/synthetic DPAPI key; no installed Cortex, profile or model).");
                return checks;
            }
            var report = await dispatcher.InspectCortexAsync("cortex", default);
            var diagnosticMessage = report.Message ?? "";
            foreach (var marker in new[] { privateFixture, privateError, privateCode, privateIdentity, privateCredential, keyId, key })
                diagnosticMessage = diagnosticMessage.Replace(marker, "[redacted]", StringComparison.Ordinal);
            diagnosticMessage = new string(diagnosticMessage.Take(512).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            Console.WriteLine($"DIAGNOSTIC: state={report.State}; transport={report.Transport}; mcpIdentityNull={report.McpIdentity is null}; nativeIdentityNull={report.NativeIdentity is null}; contractMatches={report.NativeIdentity?.Contract == CortexNativeProtocol.Contract}; message={diagnosticMessage}");
            Check(report.State == "ready" && report.Transport == "cortex-native-pipe" && report.McpIdentity is null && report.NativeIdentity?.Contract == CortexNativeProtocol.Contract,
                "owner diagnostic verifies native identity, full catalog and status without MCP or a provider process launch");
            var result = await dispatcher.ExecuteAsync(call, context, default);
            using var typedResult = JsonDocument.Parse(result.Content);
            Check(result.Success && typedResult.RootElement.GetProperty("structuredContent").GetProperty("text").GetString() == "café\nSecond line." && connections == 1 && catalog >= 2,
                "sequential calls reuse one authenticated native connection and preserve Unicode typed results");
            var before = calls;
            var noGrant = new AgentProfile { AllowedTools = [], Capabilities = [] };
            before = calls;
            Check(!(await dispatcher.ExecuteAsync(call, context with { Profile = noGrant }, default)).Success && calls == before,
                "native transport does not bypass agent grants");
            profile.AllowedTools.Add("mcp.cortex.cortex_read");
            Task<ToolResult> Read(string id) => dispatcher.ExecuteAsync(new("mcp.cortex.cortex_read",
                CerebralJson.Element(new { @namespace = "project.fixture", recordType = "knowledge", recordId = id, revision = 2 })), context, default);
            var full = await Read("guidance");
            Check(full.Success && !full.Truncated && full.Content.Contains("FULL BODY", StringComparison.Ordinal),
                "native complete typed record is accepted with its original body and revision");
            foreach (var defect in new[] { "wrong-id", "wrong-revision", "missing-revision", "missing-body", "truncated", "metadata-only" })
            {
                var read = await Read("exact-" + defect);
                using var retained = JsonDocument.Parse(read.Content);
                Check(!read.Success && read.Evidence is { Outcome: ToolExecutionOutcome.Unknown, Partial: true } &&
                    retained.RootElement.TryGetProperty("structuredContent", out _), "native exact read rejects " + defect + " without losing provider JSON");
            }
            afterReadDispatch = () => server.Enabled = false;
            var rebound = await Read("guidance");
            Check(!rebound.Success && rebound.Evidence?.State?.Contains("changed during discovery", StringComparison.Ordinal) == true,
                "native provider change after dispatch prevents adoption of the returned body");
            server.Enabled = true;
            afterReadDispatch = () => host.CortexNamespaces = [];
            var restricted = await Read("guidance");
            Check(!restricted.Success && restricted.Evidence?.State?.Contains("No Cortex namespaces", StringComparison.Ordinal) == true,
                "host namespace restriction during a native read prevents adoption of the returned body");
            host.CortexNamespaces = ["project.fixture"]; afterReadDispatch = null;
            mode = "denied";
            Check(!(await dispatcher.ExecuteAsync(call, context, default)).Success, "native authentication error remains a failed operation");
            mode = "ready";
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success && connections == 2, "rejected session is retired before a later independent request reconnects");
            mode = "wrong-id";
            result = await dispatcher.ExecuteAsync(call, context, default);
            Check(!result.Success, "a response for another request is rejected and the stream retired");
            mode = "drop"; before = calls;
            result = await dispatcher.ExecuteAsync(call, context, default);
            Check(!result.Success && calls == before + 1, "disconnect after submission never replays the native call");
            mode = "ready";
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success, "next independent request can reconnect after an uncertain call");
            server.CortexNative = native with { HostExecutable = keyFile };
            before = calls;
            Check(!(await dispatcher.ExecuteAsync(call, context, default)).Success && calls == before, "OS-observed host executable mismatch is rejected before an operation");
            server.CortexNative = native;
            mode = "slow"; using var cancel = new CancellationTokenSource();
            var pending = dispatcher.ExecuteAsync(call, context, cancel.Token);
            await slow.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            var cancelled = false; try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "owner cancellation terminates a pending native exchange promptly");
            mode = "ready";
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success, "a later independent call restores a session after cancellation");
            var connectionBefore = connections; var pingBefore = pings;
            report = await dispatcher.InspectCortexAsync("cortex", default);
            Check(report.State == "ready" && connections == connectionBefore && pings == pingBefore + 1,
                "a reused identity inspection proves its current lease with one ping and no new authentication");

            // Close the already authenticated server session deterministically. This is the
            // resident idle-expiry I/O boundary without a five-minute sleep or a live provider.
            await CloseCurrentSessionAsync(); connectionBefore = connections; pingBefore = pings;
            var catalogBefore = catalog; var callsBefore = calls;
            report = await dispatcher.InspectCortexAsync("cortex", default);
            Check(report.State == "ready" && report.NativeIdentity?.ClientId == clientId && connections == connectionBefore + 1 &&
                pings == pingBefore + 1 && catalog == catalogBefore + 1 && calls == callsBefore + 1,
                "one inspection recovers an idle-closed session with one fresh authenticated lease before catalog/status");

            foreach (var rejection in new[] { "denied", "revoked" })
            {
                pingMode = rejection; connectionBefore = connections; pingBefore = pings; catalogBefore = catalog; callsBefore = calls;
                report = await dispatcher.InspectCortexAsync("cortex", default);
                Check(report.State != "ready" && connections == connectionBefore && pings == pingBefore + 1 && catalog == catalogBefore && calls == callsBefore,
                    rejection + " lease fails closed without reconnecting or submitting semantic work");
                pingMode = "ready";
                Check((await dispatcher.ExecuteAsync(call, context, default)).Success && connections == connectionBefore + 1,
                    "a later independent request can authenticate after the " + rejection + " lease was retired");
            }

            foreach (var defect in new[] { "wrong-id", "bad-state", "bad-frame" })
            {
                pingMode = defect; connectionBefore = connections; pingBefore = pings; catalogBefore = catalog; callsBefore = calls;
                var rejected = false; var untaggedInvalidDataEscaped = false;
                try { report = await dispatcher.InspectCortexAsync("cortex", default); rejected = report.State != "ready"; }
                catch (InvalidDataException) { rejected = true; untaggedInvalidDataEscaped = true; }
                Check(untaggedInvalidDataEscaped, "untagged malformed lease data retains its existing exception behavior for " + defect);
                Check(rejected && connections == connectionBefore && pings == pingBefore + 1 && catalog == catalogBefore && calls == callsBefore,
                    "malformed " + defect + " lease reply fails closed without a replacement session");
                pingMode = "ready";
                Check((await dispatcher.ExecuteAsync(call, context, default)).Success && connections == connectionBefore + 1,
                    "malformed " + defect + " stream is retired before a later independent call");
            }

            pingMode = "drop"; connectionBefore = connections; pingBefore = pings; catalogBefore = catalog; callsBefore = calls;
            report = await dispatcher.InspectCortexAsync("cortex", default);
            Check(report.State != "ready" && connections == connectionBefore + 1 && pings == pingBefore + 2 && catalog == catalogBefore && calls == callsBefore,
                "continued lease loss permits at most one fresh authentication and never dispatches catalog/status");
            pingMode = "ready";
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success, "an independent call can recover after the bounded lease probe exhausted its one retry");

            await CloseCurrentSessionAsync(); denyHello = true; connectionBefore = connections; catalogBefore = catalog; callsBefore = calls;
            report = await dispatcher.InspectCortexAsync("cortex", default);
            Check(report.State != "ready" && connections == connectionBefore + 1 && catalog == catalogBefore && calls == callsBefore,
                "fresh authentication rejection stops the idle-session repair without retry or semantic work");
            denyHello = false;
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success, "an independent call uses normal enrollment admission after a rejected replacement hello");

            pingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); pingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pingMode = "slow"; connectionBefore = connections; catalogBefore = catalog; callsBefore = calls;
            using (var pingCancel = new CancellationTokenSource())
            {
                var inspecting = dispatcher.InspectCortexAsync("cortex", pingCancel.Token);
                await pingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); pingCancel.Cancel();
                cancelled = false; try { await inspecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && connections == connectionBefore && catalog == catalogBefore && calls == callsBefore,
                    "lease-probe cancellation propagates promptly without reconnecting or semantic dispatch");
                pingRelease.TrySetResult();
            }
            pingMode = "ready";
            Check((await dispatcher.ExecuteAsync(call, context, default)).Success, "an independent call reconnects after the cancelled lease probe released its pipe");

            profile.AllowedTools.Add("mcp.cortex.cortex_knowledge"); profile.Capabilities.Add("cortex.write");
            mode = "write-drop"; connectionBefore = connections; callsBefore = calls; var writesBefore = writes;
            var mutation = new ToolCall("mcp.cortex.cortex_knowledge", CerebralJson.Element(new { action = "create", @namespace = "project.fixture",
                recordId = "fixture-no-replay", idempotencyKey = "fixture-no-replay", title = "Fixture mutation", content = "Fixture only.",
                kind = "observation", tier = "verified", tags = Array.Empty<string>(),
                provenance = new { source = "agent", evidence = "Deterministic native fixture; no installed storage." } }));
            result = await dispatcher.ExecuteAsync(mutation, context, default);
            Check(!result.Success && result.Evidence?.Outcome == ToolExecutionOutcome.Unknown && writes == writesBefore + 1 && calls == callsBefore + 1 && connections == connectionBefore,
                "a dropped mutation acknowledgement remains unknown after exactly one write with no reauthentication or replay");
            profile.AllowedTools.Remove("mcp.cortex.cortex_knowledge"); profile.Capabilities.Remove("cortex.write"); mode = "ready";
            var archiveUnavailable = false;
            try { await dispatcher.ExecuteHostUserPaoAsync(CerebralJson.Element(new { op = "7z_test", src = keyFile }), root, default); }
            catch (InvalidOperationException error) { archiveUnavailable = error.Message.Contains("does not expose PAO", StringComparison.Ordinal); }
            Check(archiveUnavailable, "archive infrastructure stays on the selected native provider when its PAO capability is unavailable");
            var archiveUnsafe = false;
            try { await dispatcher.ExecuteHostUserPaoAsync(CerebralJson.Element(new { op = "rm", src = keyFile }), root, default); }
            catch (UnauthorizedAccessException) { archiveUnsafe = true; }
            Check(archiveUnsafe, "trusted archive infrastructure does not accept unrelated PAO operations");
            server.Enabled = false;
            var disabledArchive = false;
            try { await dispatcher.ExecuteHostUserPaoAsync(CerebralJson.Element(new { op = "7z_test", src = keyFile }), root, default); }
            catch (InvalidOperationException error) { disabledArchive = error.Message.Contains("provider is disabled", StringComparison.Ordinal); }
            Check(disabledArchive, "disabled native archive provider never activates the legacy launcher");
            server.Enabled = true;
            var malformedKeyFile = Path.Combine(root, "malformed.key");
            ProtectBytes(malformedKeyFile, Encoding.UTF8.GetBytes("{\"clientId\":\"" + privateFixture + "\" invalid}"));
            server.CortexNative = native with { CredentialFile = malformedKeyFile };
            DrainAdmissions();
            await ReadyForAdmission(connections + 1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var malformedCredentialResult = await dispatcher.ExecuteAsync(call, context, default);
            Check(await sessionAccepted.WaitAsync(TimeSpan.FromSeconds(5)), "malformed credential admits its fixture connection");
            await (currentSessionClosed ?? throw new InvalidOperationException("Missing malformed credential session.")).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!malformedCredentialResult.Success && malformedCredentialResult.Content.Contains("protected credential is not valid JSON", StringComparison.Ordinal) &&
                !malformedCredentialResult.Content.Contains(privateFixture, StringComparison.Ordinal), "decrypted credential parse failures report a safe diagnostic without a private parser excerpt");
            server.CortexNative = native;

            ToolDispatcher DiagnosticDispatcher(CortexNativeConnection selected)
                => new(() => new HostSettings { McpServers = [new() { Id = "cortex", Enabled = true,
                    Executable = server.Executable, ViaUserConnector = false, CortexNative = selected }] });
            void DrainAdmissions() { while (sessionAccepted.Wait(0)) { } }
            async Task ExpectHandshakeFailure(string label, CortexNativeConnection selected, string expectedStage, string expectedCode)
            {
                DrainAdmissions();
                var admittedBefore = connections; var semanticBefore = calls; var discoveryBefore = catalog; var leaseBefore = pings; var mutationBefore = writes;
                await ReadyForAdmission(admittedBefore + 1).Task.WaitAsync(TimeSpan.FromSeconds(5));
                await using var inspection = DiagnosticDispatcher(selected);
                var failed = await inspection.InspectCortexAsync("cortex", default).WaitAsync(TimeSpan.FromSeconds(5));
                Check(await sessionAccepted.WaitAsync(TimeSpan.FromSeconds(5)), label + " admits its single fixture connection");
                var closed = currentSessionClosed ?? throw new InvalidOperationException("Missing fixture session completion.");
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(failed.State == "provider-error" && failed.NativeIdentity is null && failed.AvailableTools.Count == 0 &&
                    failed.Message.Contains("Native handshake stage=" + expectedStage + "; code=" + expectedCode, StringComparison.Ordinal),
                    label + " projects its fixed failure stage and code");
                var publicReport = JsonSerializer.Serialize(failed, CerebralJson.Options);
                Check(new[] { privateFixture, privateError, privateCode, privateIdentity, privateCredential, keyId, key }.All(value => !publicReport.Contains(value.Split('\n')[0], StringComparison.Ordinal)),
                    label + " excludes private credential, error, code and identity canaries from the entire report");
                var summaryOffset = failed.Message.IndexOf(" Native handshake ", StringComparison.Ordinal);
                Check(summaryOffset >= 0 && failed.Message.Length - summaryOffset <= 128 && !failed.Message.Any(char.IsControl),
                    label + " keeps the closed diagnostic bounded and single-line");
                const string hresultMarker = "; hresult=";
                var hresultOffset = failed.Message.IndexOf(hresultMarker, summaryOffset, StringComparison.Ordinal);
                Check(hresultOffset > summaryOffset && failed.Message.EndsWith(".", StringComparison.Ordinal) &&
                    int.TryParse(failed.Message[(hresultOffset + hresultMarker.Length)..^1],
                        System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
                    label + " exposes only the signed numeric original HResult");
                const string windowsMarker = "; win32=";
                var windowsOffset = failed.Message.IndexOf(windowsMarker, summaryOffset, StringComparison.Ordinal);
                Check((windowsOffset < 0 || windowsOffset < hresultOffset &&
                    int.TryParse(failed.Message[(windowsOffset + windowsMarker.Length)..hresultOffset],
                        System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _)) &&
                    (expectedCode != "WindowsError" || windowsOffset >= 0),
                    label + " exposes only an optional numeric Windows error before HResult");
                Check(connections == admittedBefore + 1 && calls == semanticBefore && catalog == discoveryBefore && pings == leaseBefore && writes == mutationBefore,
                    label + " closes one connection without retry, lease probe, discovery or semantic dispatch");
            }
            // These are test-owned files and a same-process deterministic provider, never installed enrollment data.
            await ExpectHandshakeFailure("peer mismatch", native with { HostExecutable = keyFile }, "PipePeer", "AccessDenied");
            var missingCredential = Path.Combine(root, "missing-diagnostic.key");
            var emptyCredentialFile = Path.Combine(root, "empty-diagnostic.key"); await File.WriteAllBytesAsync(emptyCredentialFile, []);
            var oversizedCredentialFile = Path.Combine(root, "oversized-diagnostic.key"); await File.WriteAllBytesAsync(oversizedCredentialFile, new byte[4097]);
            var corruptCredential = Path.Combine(root, "corrupt-diagnostic.key"); await File.WriteAllBytesAsync(corruptCredential, [1, 2, 3, 4]);
            var emptyClearCredential = Path.Combine(root, "empty-clear-diagnostic.key"); ProtectBytes(emptyClearCredential, []);
            var invalidFieldsCredential = Path.Combine(root, "invalid-fields-diagnostic.key");
            ProtectFile(invalidFieldsCredential, CerebralJson.Element(new { clientId, keyId, apiKey = privateCredential }));
            foreach (var test in new[]
            {
                (missingCredential, "CredentialRead", "IoError"), (emptyCredentialFile, "CredentialRead", "InvalidData"),
                (oversizedCredentialFile, "CredentialRead", "InvalidData"), (corruptCredential, "CredentialUnprotect", "WindowsError"),
                (emptyClearCredential, "CredentialSize", "InvalidData"), (malformedKeyFile, "CredentialJson", "InvalidData"),
                (invalidFieldsCredential, "CredentialFields", "InvalidData")
            })
                await ExpectHandshakeFailure(test.Item2 + " " + test.Item3, native with { CredentialFile = test.Item1 }, test.Item2, test.Item3);
            foreach (var test in new[]
            {
                ("auth-denied", "HelloIdentity", "ClientAuthFailed"), ("contract-denied", "HelloIdentity", "ContractMismatch"),
                ("unknown-denied", "HelloIdentity", "HelloDeniedUnknown"), ("array", "HelloDecode", "HelloShapeInvalid"),
                ("null", "HelloIdentity", "HelloShapeInvalid"), ("missing-ok", "HelloIdentity", "HelloOkInvalid"),
                ("invalid-ok", "HelloIdentity", "HelloOkInvalid"), ("identity-type", "HelloDecode", "JsonInvalid"),
                ("identity", "HelloIdentity", "IdentityMismatch"), ("bad-frame", "HelloRead", "InvalidData"),
                ("drop", "HelloRead", "IoError")
            })
            {
                helloMode = test.Item1;
                await ExpectHandshakeFailure(test.Item1, native, test.Item2, test.Item3);
            }
            helloMode = "slow"; DrainAdmissions(); connectionBefore = connections; catalogBefore = catalog; callsBefore = calls;
            var cancellationPingsBefore = pings; var cancellationWritesBefore = writes;
            await ReadyForAdmission(connectionBefore + 1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await using var inspection = DiagnosticDispatcher(native);
                using var helloCancel = new CancellationTokenSource();
                var inspecting = inspection.InspectCortexAsync("cortex", helloCancel.Token);
                await helloEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await sessionAccepted.WaitAsync(TimeSpan.FromSeconds(5)), "pending hello admits one fixture connection");
                helloCancel.Cancel();
                cancelled = false; try { await inspecting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && connections == connectionBefore + 1 && catalog == catalogBefore && calls == callsBefore &&
                    pings == cancellationPingsBefore && writes == cancellationWritesBefore,
                    "fresh hello cancellation propagates without diagnostic replacement, retry or semantic dispatch");
            }
            finally { helloRelease.TrySetResult(); helloMode = "ready"; }
            await (currentSessionClosed ?? throw new InvalidOperationException("Missing cancelled fixture session.")).Task.WaitAsync(TimeSpan.FromSeconds(5));
            DrainAdmissions(); connectionBefore = connections; catalogBefore = catalog; callsBefore = calls;
            await ReadyForAdmission(connectionBefore + 1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            report = await dispatcher.InspectCortexAsync("cortex", default);
            Check(await sessionAccepted.WaitAsync(TimeSpan.FromSeconds(5)), "independent healthy inspection admits its one fixture connection");
            Check(report.State == "ready" && report.NativeIdentity?.ClientId == clientId &&
                !report.Message.Contains("Native handshake", StringComparison.Ordinal) && connections == connectionBefore + 1 &&
                catalog == catalogBefore + 1 && calls == callsBefore + 1,
                "a later independent healthy inspection preserves normal identity, catalog and status after diagnostic failures");

            var legacy = new McpRegistration { Id = "old", Executable = Environment.ProcessPath!, Arguments = ["stdio"], ViaUserConnector = true, Url = ConnectorProtocol.DefaultUrl };
            var direct = CerebralJson.Read<McpRegistration>(CerebralJson.Element(legacy)); direct.ViaUserConnector = false; direct.Url = null;
            Check(ConnectorProtocol.ExecutionFingerprint(legacy) == ConnectorProtocol.ExecutionFingerprint(direct), "legacy connector fingerprints preserve route-independent identity");
            var relay = new CortexNativeRelay("Cerebral.Native.Fixture", owner.AccountSid, owner.SessionId, Environment.ProcessPath!);
            var relayServer = CerebralJson.Read<McpRegistration>(CerebralJson.Element(server)); relayServer.ViaUserConnector = true; relayServer.CortexNative = native with { Relay = relay };
            var configuration = UserConnectorManager.CreateConfiguration([relayServer]);
            Check(configuration.CortexRelay == relay && configuration.McpServers.Single().CortexNative?.Relay is null &&
                ConnectorProtocol.ExecutionFingerprint(configuration.McpServers.Single()) == ConnectorProtocol.ExecutionFingerprint(relayServer), "connector provisions a native provider without stdio, recursive forwarding or changed execution identity");
            var exported = PortableRegistrationPolicy.ExportMcp(relayServer);
            Check(exported.NativeBindingOmitted && !CerebralJson.Element(exported).GetRawText().Contains(keyFile) &&
                PortableRegistrationPolicy.RestoreMcp(exported, null) is { Enabled: false, RequiresConfiguration: true, CortexNative: null }, "portable export excludes machine-local native bindings and cannot silently reactivate them elsewhere");
            Check(PortableRegistrationPolicy.RestoreMcp(exported, relayServer).CortexNative == relayServer.CortexNative, "same-machine restore preserves the already reviewed native binding");
            var unbound = PortableRegistrationPolicy.RestoreMcp(exported, null);
            unbound.Enabled = true; unbound.RequiresConfiguration = false;
            host.McpServers = [unbound];
            before = calls;
            var unboundResult = await dispatcher.ExecuteAsync(call, context, default);
            Check(!unboundResult.Success && calls == before && unboundResult.Content.Contains("cannot fall back to MCP", StringComparison.Ordinal), "re-enabled portable native declaration cannot silently become an MCP connection");
            host.McpServers = [server];
            var malformed = UserConnectorManager.CreateConfiguration([relayServer]);
            malformed.McpServers.Single().Arguments = null!;
            var malformedRejected = false;
            try { ConnectorProtocol.ValidateConfiguration(malformed); } catch (ArgumentException) { malformedRejected = true; }
            Check(malformedRejected, "native connector rejects missing argv as a configuration error");
            malformed = new ConnectorConfiguration { McpServers = [unbound] };
            malformedRejected = false;
            try { ConnectorProtocol.ValidateConfiguration(malformed); } catch (ArgumentException) { malformedRejected = true; }
            Check(malformedRejected, "connector cannot downgrade a native-only definition to MCP");
            mode = "ready";
            var connectorFile = Path.Combine(root, "connector.ini"); var accessFile = Path.Combine(root, "access.key");
            var access = new string('e', 32);
            configuration.ListenUrl = "http://127.0.0.1:0/";
            await File.WriteAllBytesAsync(connectorFile, JsonSerializer.SerializeToUtf8Bytes(configuration, CerebralJson.Options));
            await File.WriteAllTextAsync(accessFile, access);
            var legacyFile = Path.Combine(root, "legacy-native.ini");
            await File.WriteAllBytesAsync(legacyFile, JsonSerializer.SerializeToUtf8Bytes(new HostSettings { McpServers = [unbound] }, CerebralJson.Options));
            var legacyRejected = false;
            try { await ToolDispatcher.RunConnectorAsync(["--settings", legacyFile, "--key", accessFile], default); }
            catch (ArgumentException) { legacyRejected = true; }
            Check(legacyRejected, "legacy connector input cannot drop a restored native-only binding requirement");
            using (var connectorStop = new CancellationTokenSource())
            {
                var running = ToolDispatcher.RunConnectorAsync(["--settings", connectorFile, "--key", accessFile], connectorStop.Token);
                try
                {
                    var relayHost = new HostSettings { AccessKey = access, McpServers = [relayServer], CortexNamespaces = ["project.fixture"] };
                    await using var relayDispatcher = new ToolDispatcher(() => relayHost);
                    var relayed = await relayDispatcher.InspectCortexAsync("cortex", default);
                    Check(relayed.State == "ready" && relayed.NativeIdentity?.OwnerSid == owner.AccountSid && relayed.McpIdentity is null,
                        "actual owner-session relay carries native identity/catalog/status across both local pipes without MCP or HTTP semantic calls");
                    before = calls;
                    Check((await relayDispatcher.ExecuteAsync(call, context, default)).Success && calls == before + 1, "registered native calls execute through the authenticated relay");
                    mode = "large";
                    var largeResult = await relayDispatcher.ExecuteAsync(call, context, default);
                    Check(!largeResult.Success && largeResult.Content.Contains("Cortex semantic result exceeds the 256 KiB limit", StringComparison.Ordinal),
                        "full-size response crosses the relay while the independent semantic result limit remains enforced");
                    mode = "ready";
                    relayHost.AccessKey = new string('f', 32); before = calls;
                    Check(!(await relayDispatcher.ExecuteAsync(call, context, default)).Success && calls == before, "relay rejects the wrong owner access key without executing or falling back");
                    relayHost.AccessKey = access;
                    relayServer.CortexNative = native with { Relay = relay, CredentialFile = Path.Combine(root, "other.key") };
                    Check(!(await relayDispatcher.ExecuteAsync(call, context, default)).Success && calls == before, "relay rejects a provider fingerprint that differs from its provisioned native binding");
                }
                finally { connectorStop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            using var frame = new MemoryStream();
            await CortexNativeProtocol.WriteAsync(frame, new { text = "café\nSecond line." }, 4096, default); frame.Position = 0;
            Check((await CortexNativeProtocol.ReadAsync(frame, 4096, default)).GetProperty("text").GetString() == "café\nSecond line.", "native framing preserves exact UTF-8 multiline content");
            foreach (var bad in new[] { Raw(2, 0, "{}"), Raw(1, 1, "{}"), Raw(1, 0, "{\"a\":1,\"a\":2}"), Raw(1, 0, "{}", 99999) })
            {
                var rejected = false; try { await CortexNativeProtocol.ReadAsync(new MemoryStream(bad), 4096, default); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "native framing rejects incompatible version, duplicate properties or an oversized length before parsing");
            }
            Console.WriteLine($"RESULT: {checks} native Cortex checks passed (Windows pipe/process/DPAPI integration with deterministic semantic fixture; no installed provider or inference).");
            return checks;
        }
        finally { stop.Cancel(); await listener; Directory.Delete(root, recursive: true); }
    }

    private static byte[] Raw(ushort major, ushort minor, string json, int? declaredLength = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json); var frame = new byte[8 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, major); BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), minor);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), declaredLength ?? bytes.Length); bytes.CopyTo(frame, 8); return frame;
    }
    private static void ProtectFile(string path, JsonElement value)
        => ProtectBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static void ProtectBytes(string path, byte[] bytes)
    {
        var source = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            if (!CryptProtectData(ref source, null, 0, 0, 0, 1, out var result)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var output = new byte[result.Length]; Marshal.Copy(result.Data, output, 0, output.Length); File.WriteAllBytes(path, output); }
            finally { _ = LocalFree(result.Data); }
        }
        finally { for (var i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0); Marshal.FreeHGlobal(source.Data); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob source, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}
