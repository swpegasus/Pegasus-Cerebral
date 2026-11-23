using System.Collections;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

public static partial class ToolEffectIdentityTests
{
    private static async Task VerifyDeferredNativeAsync(string root, Action<bool, string> check)
    {
        const string effect = "deferred-effect";
        foreach (var name in new[] { "git.status", "git.diff", "dotnet.build", "dotnet.test" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root);
            var call = fixture.Call(name);
            var result = await fixture.Dispatcher.ExecuteAsync(call, fixture.Context(effect), default);
            var calls = fixture.Calls.ToArray();
            check(result.Success && result.Evidence is { RequestId: effect, JobId: "original-job", State: "done" } &&
                calls.Count(item => item.Arguments.GetProperty("op").GetString() != "job_stat") == 1 &&
                calls[0].Arguments.GetProperty("rid").GetString() == effect &&
                calls[0].Arguments.GetProperty("wait").ValueKind == JsonValueKind.False &&
                calls[0].Arguments.GetProperty("tmo").GetInt32() == 5,
                name + " dispatches the original RID once with wait=false and preserves its terminal JID");
            check(calls.Length == 3 && calls.Skip(1).All(item =>
                    item.Arguments.GetProperty("op").GetString() == "job_stat" &&
                    item.Arguments.GetProperty("jid").GetString() == "original-job" &&
                    !item.Arguments.TryGetProperty("rid", out _)) &&
                calls[2].At - calls[1].At >= TimeSpan.FromMilliseconds(250),
                name + " treats queued/running as incomplete and paces only exact-jid queries with their own identity");
        }
        await using (var fixture = await DeferredNativeFixture.CreateAsync(root, "accepted"))
        {
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.Success && fixture.Calls.Count == 3 && result.Evidence?.State == "done",
                "accepted acknowledgement also waits for the original terminal receipt");
        }
        foreach (var mode in new[] { "failed", "partial", "child-failed" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, mode);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                result.Evidence is { RequestId: effect, JobId: "original-job", ExitCode: 7 } &&
                (mode != "partial" || result.Evidence.Partial is true) && fixture.Calls.Count == 3,
                mode + " preserves the delayed original process failure instead of accepting lookup-wrapper success");
        }
        foreach (var mode in new[] { "wrong-rec-rid", "wrong-rec-jid", "wrong-rec-op", "bad-wrapper", "missing-rec", "wrapper-running", "missing-wrapper-rid", "reused-wrapper-rid" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, mode);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Unknown && result.Evidence is
                { ProviderId: "mcp.pao", RequestId: effect, JobId: "original-job", State: "queued" } && fixture.Calls.Count == 2,
                mode + " cannot resolve or replace the acknowledged original job identity");
        }
        foreach (var mode in new[] { "foreign-ack-rid", "foreign-ack-op", "missing-ack-jid", "unknown-ack-state", "duplicate-ack",
                     "ack-ok-false", "ack-error", "ack-partial", "ack-partial-type", "ack-exit-type", "ack-timed-out", "ack-transport-error" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, mode);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Unknown && fixture.Calls.Count == 1 &&
                (mode != "foreign-ack-rid" || result.Evidence?.RequestId == "foreign-effect") &&
                (mode != "missing-ack-jid" || result.Evidence?.JobId is null),
                mode + " stops before lookup and preserves actual returned identity without inventing acceptance");
        }
        foreach (var code in new[] { "pao_arguments_invalid", "invalid_arguments", "user_dispatch_required", "permission_denied", "access_denied", "svc_down", "operation_not_allowed" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, "admission:" + code);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(!result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ProviderId: "mcp.pao", RequestId: effect, JobId: null } &&
                fixture.Calls.Count == 1 && fixture.Calls.Single().Arguments.GetProperty("wait").ValueKind == JsonValueKind.False,
                code + " remains a complete pre-submission rejection without a job, polling or native fallback");
        }
        await using (var fixture = await DeferredNativeFixture.CreateAsync(root, "admission-transport-error"))
        {
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(!result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Rejected, RequestId: effect, JobId: null } && fixture.Calls.Count == 1,
                "an authenticated transport-marked admission refusal preserves its complete original rejection");
        }
        foreach (var mode in new[] { "admission-foreign-rid", "admission-foreign-op", "admission-partial", "admission-missing-partial",
                     "admission-partial-type", "admission-unknown-error", "admission-execution-failed", "admission-timed-out", "admission-duplicate" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, mode);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Unknown && fixture.Calls.Count == 1 &&
                result.Evidence is { ProviderId: "mcp.pao", JobId: null } &&
                (mode != "admission-foreign-rid" || result.Evidence.RequestId == "foreign-effect"),
                mode + " cannot claim a complete original pre-submission refusal or trigger lookup/fallback");
        }
        foreach (var mode in new[] { "cancel-status", "disconnect-status", "registration-change", "deadline" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, mode);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(timeout: mode == "deadline" ? 1 : 5),
                fixture.Context(effect), fixture.Caller.Token);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Unknown && result.Evidence is
                { ProviderId: "mcp.pao", RequestId: effect, JobId: "original-job" } &&
                fixture.Calls.Count == 2 && fixture.Calls.Count(item => item.Arguments.GetProperty("op").GetString() != "job_stat") == 1,
                mode + " retains acknowledged JID after interruption without replay, fallback or provider rerouting");
        }
        await using (var fixture = await DeferredNativeFixture.CreateAsync(root, "disconnect-admission"))
        {
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.Evidence is { Outcome: ToolExecutionOutcome.Unknown, RequestId: effect, JobId: null } && fixture.Calls.Count == 1,
                "disconnect before acknowledgement remains RID-only unknown with no job scan or replay");
        }
        await using (var fixture = await DeferredNativeFixture.CreateAsync(root, "immediate"))
        {
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.Success && result.Evidence?.JobId == "original-job" && fixture.Calls.Count == 1,
                "an exact terminal deferred acknowledgement needs no lookup");
        }
        foreach (var schema in new[] { "omitted-wait", "string-wait", "true-only-wait", "duplicate-wait", "no-job-stat", "missing-jid", "wrong-jid", "status-required-cwd", "missing-tmo" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, "immediate", schema);
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(effect), default);
            check(result.Success && fixture.Calls.Count == 1 && !fixture.Calls.Single().Arguments.TryGetProperty("wait", out _) &&
                (schema != "missing-tmo" || !fixture.Calls.Single().Arguments.TryGetProperty("tmo", out _)),
                schema + " preserves the legacy synchronous native request shape");
        }
        await using (var fixture = await DeferredNativeFixture.CreateAsync(root, "immediate"))
        {
            var result = await fixture.Dispatcher.ExecuteAsync(fixture.Call(), fixture.Context(null), default);
            check(result.Success && fixture.Calls.Count == 1 && !fixture.Calls.Single().Arguments.TryGetProperty("wait", out _) &&
                !fixture.Calls.Single().Arguments.TryGetProperty("rid", out _),
                "legacy contexts without an original effect RID keep their provider-generated synchronous identity");
        }
        foreach (var name in new[] { "git.status", "git.diff", "dotnet.build", "dotnet.test" })
        {
            await using var fixture = await DeferredNativeFixture.CreateAsync(root, "wrong-rec-op");
            var saved = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", effect, "original-job");
            var recovered = await fixture.Dispatcher.ReconcileAsync(fixture.Call(name), fixture.Context(effect), saved, default);
            check(recovered.EffectiveOutcome == ToolExecutionOutcome.Unknown && recovered.Evidence?.JobId == "original-job" &&
                fixture.Calls.Count == 1 && fixture.Calls.Single().Arguments.GetProperty("op").GetString() == "job_stat",
                name + " reconciliation cannot accept a same-RID/same-JID receipt for another operation");
        }
        await VerifyDeferredCompatibilityTransportsAsync(root, check);
    }

    private static async Task VerifyDeferredCompatibilityTransportsAsync(string root, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This compatibility fixture requires Windows owner pipes.");
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"op":{"type":"string","enum":["git_status","job_stat"]},"rid":{"type":"string"},"jid":{"type":"string"},"cwd":{"type":"string"},"tmo":{"type":"integer"},"wait":{"type":"boolean"}},"required":["op"],"additionalProperties":false}
            """);
        var schema = schemaDocument.RootElement.Clone();
        var profile = new AgentProfile { AllowedRoots = [root], AllowedTools = ["git.status"], Capabilities = ["git.read"] };
        var context = new ToolExecutionContext(profile, "compatibility-session", "compatibility-job", root, EffectId: "compatibility-effect") { EnforceProfileScope = true };
        var call = new ToolCall("git.status", CerebralJson.Element(new { path = root, timeoutSeconds = 5 }));
        var calls = new ConcurrentQueue<JsonElement>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using (var app = builder.Build())
        {
            var descriptor = new ToolDescriptor("mcp.pao.pao", "Capable connector fixture", schema, "mcp.pao");
            app.MapPost("/mcp", async (HttpContext http) =>
            {
                using var request = await JsonDocument.ParseAsync(http.Request.Body);
                if (request.RootElement.GetProperty("action").GetString() == "list")
                { await http.Response.WriteAsJsonAsync(new[] { descriptor }, CerebralJson.Options); return; }
                var arguments = request.RootElement.GetProperty("arguments").Clone(); calls.Enqueue(arguments);
                await http.Response.WriteAsJsonAsync(new { isError = false, structuredContent = Receipt(
                    arguments.GetProperty("rid").GetString()!, "connector-job", arguments.GetProperty("op").GetString()!) }, CerebralJson.Options);
            });
            await app.StartAsync();
            try
            {
                var settings = new HostSettings { AccessKey = new string('a', 64), McpServers =
                    [new() { Id = "pao", Enabled = true, ViaUserConnector = true, Url = app.Urls.Single(), Executable = "never-launched" }] };
                await using var dispatcher = new ToolDispatcher(() => settings);
                var result = await dispatcher.ExecuteAsync(call, context, default);
                check(result.Success && calls.Count == 1 && !calls.Single().TryGetProperty("wait", out _) &&
                    calls.Single().GetProperty("tmo").GetInt32() == 5,
                    "a capable non-native user connector remains synchronous without deferred status polling");
            }
            finally { await app.StopAsync(); }
        }
        calls.Clear();
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        var endpoint = "cerebral-deferred-relay-fixture-" + Guid.NewGuid().ToString("N");
        var relay = new CortexNativeRelay(endpoint, owner.AccountSid, owner.SessionId, owner.Executable);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Task.Run(async () =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This relay fixture requires Windows owner pipes.");
            try
            {
                for (var connection = 0; connection < 2; connection++)
                {
                    using var pipe = InstalledWorkerWindows.CreateServer(endpoint, owner.AccountSid, InstalledWorkerBinding.LocalServiceSid, connection == 0);
                    ready.TrySetResult();
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var request = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.RequestBytes, stop.Token);
                    var id = request.GetProperty("id").GetString();
                    object result;
                    if (request.GetProperty("action").GetString() == "list")
                        result = new { operations = new[] { new { name = "pao", description = "Capable relay fixture", inputSchema = schema } } };
                    else
                    {
                        var arguments = request.GetProperty("arguments").Clone(); calls.Enqueue(arguments);
                        result = new { isError = false, structuredContent = Receipt(arguments.GetProperty("rid").GetString()!,
                            "relay-job", arguments.GetProperty("op").GetString()!) };
                    }
                    await CortexNativeProtocol.WriteAsync(pipe, new { version = 1, id, ok = true, result }, CortexNativeProtocol.RelayResponseBytes, stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var native = new CortexNativeConnection(owner.Executable, Path.Combine(root, "never-read-relay-credential"), owner.SessionId, relay);
            var settings = new HostSettings { AccessKey = new string('a', 64), McpServers =
                [new() { Id = "pao", Enabled = true, ViaUserConnector = true, Executable = owner.Executable, CortexNative = native }] };
            await using var dispatcher = new ToolDispatcher(() => settings);
            var result = await dispatcher.ExecuteAsync(call, context, default);
            check(result.Success && calls.Count == 1 && !calls.Single().TryGetProperty("wait", out _),
                "a capable native user relay remains synchronous without the new direct-owner deferred path");
        }
        finally { stop.Cancel(); await service; }
    }

    private sealed class DeferredNativeFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        public readonly CancellationTokenSource Caller = new();
        private readonly NamedPipeServerStream _server;
        private readonly Task _service;
        private readonly string _root;
        private readonly string _mode;
        private readonly JsonElement _schema;
        private string _originalOperation = "git_status";
        private string _originalRid = "deferred-effect";
        private int _statusCalls;
        public ConcurrentQueue<(JsonElement Arguments, DateTimeOffset At)> Calls { get; } = new();
        public McpRegistration Registration { get; }
        public ToolDispatcher Dispatcher { get; }
        private readonly AgentProfile _profile;

        private DeferredNativeFixture(string root, string mode, JsonElement schema, NamedPipeServerStream server,
            NamedPipeClientStream client, CortexNativeConnection native)
        {
            _root = root; _mode = mode; _schema = schema; _server = server;
            Registration = new() { Id = "pao", Enabled = true, ViaUserConnector = false, Executable = Environment.ProcessPath!, CortexNative = native };
            var settings = new HostSettings
            {
                McpServers = [Registration],
                Executables = [new() { Id = "dotnet", Path = Environment.ProcessPath! }]
            };
            _profile = new()
            {
                Id = "deferred-native-fixture", AllowedRoots = [root],
                AllowedTools = ["git.status", "git.diff", "process.run"], Capabilities = ["git.read", "process.execute"]
            };
            Dispatcher = new(() => settings);
            // Inject an already-authenticated task-owned fixture connection. No real provider,
            // enrollment, credential, application authorization or installed service is touched.
            var clientType = typeof(ToolDispatcher).Assembly.GetType("Cerebral.Tools.CortexNativePipeClient")!;
            var nativeClient = Activator.CreateInstance(clientType, native)!;
            clientType.GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nativeClient, client);
            var entryType = typeof(ToolDispatcher).GetNestedType("NativeClientEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, CortexOperationPolicy.ProviderFingerprint(Registration), nativeClient)!;
            var clients = (IDictionary)typeof(ToolDispatcher).GetField("_nativeCortexClients", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Dispatcher)!;
            clients.Add(Registration.Id, entry);
            _service = ServeAsync();
        }

        public static async Task<DeferredNativeFixture> CreateAsync(string root, string mode = "normal", string schemaMode = "normal")
        {
            var endpoint = "cerebral-deferred-fixture-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var accepting = server.WaitForConnectionAsync(timeout.Token);
                await client.ConnectAsync(timeout.Token); await accepting;
                var native = new CortexNativeConnection(Environment.ProcessPath!, Path.Combine(root, "never-read-credential"), 0, PipeName: endpoint);
                var properties = new JsonObject
                {
                    ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("git_status", "git_diff", "dotnet_build", "dotnet_test", "job_stat") },
                    ["rid"] = new JsonObject { ["type"] = "string" }, ["jid"] = new JsonObject { ["type"] = "string" },
                    ["cwd"] = new JsonObject { ["type"] = "string" }, ["src"] = new JsonObject { ["type"] = "string" },
                    ["arg"] = new JsonObject { ["type"] = "array" }, ["tmo"] = new JsonObject { ["type"] = "integer" },
                    ["wait"] = new JsonObject { ["type"] = "boolean" }
                };
                if (schemaMode == "omitted-wait") properties.Remove("wait");
                if (schemaMode == "string-wait") properties["wait"]!["type"] = "string";
                if (schemaMode == "true-only-wait") properties["wait"]!["enum"] = new JsonArray(true);
                if (schemaMode == "duplicate-wait") properties["WAIT"] = new JsonObject { ["type"] = "boolean" };
                if (schemaMode == "no-job-stat") properties["op"]!["enum"] = new JsonArray("git_status", "git_diff", "dotnet_build", "dotnet_test");
                if (schemaMode == "missing-jid") properties.Remove("jid");
                if (schemaMode == "wrong-jid") properties["jid"]!["type"] = "integer";
                if (schemaMode == "missing-tmo") { properties.Remove("tmo"); properties.Remove("wait"); }
                var required = schemaMode == "status-required-cwd" ? new[] { "op", "cwd" } : new[] { "op" };
                var schema = CerebralJson.Element(new { type = "object", properties, required, additionalProperties = false });
                await File.WriteAllTextAsync(Path.Combine(root, "DeferredFixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                return new(root, mode, schema, server, client, native);
            }
            catch { server.Dispose(); client.Dispose(); throw; }
        }

        public ToolExecutionContext Context(string? effect) => new(_profile, "deferred-session", "deferred-assignment", _root, EffectId: effect) { EnforceProfileScope = true };
        public ToolCall Call(string name = "git.status", int timeout = 5) => name.StartsWith("dotnet.", StringComparison.Ordinal)
            ? new("process.run", CerebralJson.Element(new { executableId = "dotnet", arguments = new[] { name[7..], Path.Combine(_root, "DeferredFixture.csproj") }, workingDirectory = _root, timeoutSeconds = timeout }))
            : new(name, CerebralJson.Element(new { path = _root, timeoutSeconds = timeout }));

        private object Original(string state, bool ok = true, bool partial = false, int exit = 0, string? error = null,
            string? rid = null, string? jid = "original-job", string? op = null, int? childExit = null) =>
            new { ok, rid = rid ?? _originalRid, jid, state, partial, exitCode = exit, op = op ?? _originalOperation,
                err = error, data = childExit is { } child
                    ? (object)new { exit = child, message = "Deterministic original receipt." }
                    : new { message = "Deterministic original receipt." } };

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var request = await CortexNativeProtocol.ReadAsync(_server, CortexNativeProtocol.RequestBytes, _stop.Token);
                    var id = request.GetProperty("id").GetString();
                    if (request.GetProperty("op").GetString() == "catalog")
                    {
                        await CortexNativeProtocol.WriteAsync(_server, new { id, ok = true, result = new
                        { operations = new[] { new { name = "pao", description = "Deferred fixture", inputSchema = _schema } } } }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                        continue;
                    }
                    var arguments = request.GetProperty("arguments").Clone();
                    Calls.Enqueue((arguments, DateTimeOffset.UtcNow));
                    var op = arguments.GetProperty("op").GetString();
                    object response;
                    if (op != "job_stat")
                    {
                        _originalOperation = op!; _originalRid = arguments.TryGetProperty("rid", out var rid) ? rid.GetString()! : "legacy-generated";
                        if (_mode == "disconnect-admission") return;
                        response = Original(_mode == "immediate" ? "done" : _mode == "unknown-ack-state" ? "unrecognized" : _mode == "accepted" ? "accepted" : "queued",
                            ok: _mode != "ack-ok-false", partial: _mode == "ack-partial", error: _mode == "ack-error" ? "execution_error" : null,
                            rid: _mode == "foreign-ack-rid" ? "foreign-effect" : null,
                            jid: _mode == "missing-ack-jid" ? null : "original-job", op: _mode == "foreign-ack-op" ? "mkdir" : null);
                        if (_mode is "duplicate-ack" or "ack-partial-type" or "ack-exit-type" or "ack-timed-out")
                        {
                            var malformed = JsonNode.Parse(JsonSerializer.Serialize(response, CerebralJson.Options),
                                new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
                            if (_mode == "duplicate-ack") malformed["RID"] = _originalRid;
                            if (_mode == "ack-partial-type") malformed["partial"] = "false";
                            if (_mode == "ack-exit-type") malformed["exitCode"] = "0";
                            if (_mode == "ack-timed-out") malformed["data"]!["timed_out"] = true;
                            response = malformed;
                        }
                        if (_mode.StartsWith("admission", StringComparison.Ordinal))
                        {
                            var code = _mode.StartsWith("admission:", StringComparison.Ordinal) ? _mode[10..] : "permission_denied";
                            var refused = JsonNode.Parse(JsonSerializer.Serialize(Original("rejected", ok: false, error: code), CerebralJson.Options),
                                new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
                            refused.Remove("jid");
                            if (_mode == "admission-foreign-rid") refused["rid"] = "foreign-effect";
                            if (_mode == "admission-foreign-op") refused["op"] = "mkdir";
                            if (_mode == "admission-partial") refused["partial"] = true;
                            if (_mode == "admission-missing-partial") refused.Remove("partial");
                            if (_mode == "admission-partial-type") refused["partial"] = "false";
                            if (_mode == "admission-unknown-error") { refused["state"] = "failed"; refused["err"] = "unknown_error"; }
                            if (_mode == "admission-execution-failed") { refused["state"] = "failed"; refused["err"] = "compile_failed"; refused["exitCode"] = 7; }
                            if (_mode == "admission-timed-out") refused["data"]!["timed_out"] = true;
                            if (_mode == "admission-duplicate") refused["RID"] = _originalRid;
                            response = refused;
                        }
                    }
                    else
                    {
                        _statusCalls++;
                        if (_mode == "disconnect-status") return;
                        if (_mode == "cancel-status") { Caller.Cancel(); await Task.Delay(Timeout.Infinite, _stop.Token); }
                        if (_mode == "registration-change") Registration.CortexNative = Registration.CortexNative! with { PipeName = "changed-endpoint" };
                        var original = _mode switch
                        {
                            "wrong-rec-rid" => Original("done", rid: "foreign-effect"),
                            "wrong-rec-jid" => Original("done", jid: "foreign-job"),
                            "wrong-rec-op" => Original("done", op: "mkdir"),
                            "failed" when _statusCalls > 1 => Original("failed", ok: false, exit: 7, error: "compile_failed"),
                            "partial" when _statusCalls > 1 => Original("failed", ok: false, partial: true, exit: 7, error: "partial_result"),
                            "child-failed" when _statusCalls > 1 => Original("done", childExit: 7),
                            _ => Original(_statusCalls == 1 || _mode == "deadline" ? "running" : "done")
                        };
                        response = new { ok = _mode != "bad-wrapper",
                            rid = _mode == "missing-wrapper-rid" ? null : _mode == "reused-wrapper-rid" ? _originalRid : "query-rid-" + _statusCalls,
                            jid = "query-job-" + _statusCalls,
                            state = _mode == "wrapper-running" ? "running" : "done", partial = false, exitCode = 0, op = "job_stat",
                            err = _mode == "bad-wrapper" ? "lookup_failed" : null,
                            data = new { rec = _mode == "missing-rec" ? null : original } };
                    }
                    if (_mode is "ack-transport-error" or "admission-transport-error" && op != "job_stat")
                        await CortexNativeProtocol.WriteAsync(_server, new { id, ok = false, error = response }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                    else
                        await CortexNativeProtocol.WriteAsync(_server, new { id, ok = true, result = response }, CortexNativeProtocol.ResponseBytes, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { _server.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _server.Dispose();
            await Dispatcher.DisposeAsync(); await _service;
            Caller.Dispose(); _stop.Dispose();
        }
    }
}
