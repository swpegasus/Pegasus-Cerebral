using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

public static partial class ToolEffectIdentityTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-effect-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var calls = new ConcurrentQueue<JsonElement>();
        JsonElement? responseOverride = null;
        var disconnect = false;
        var descriptor = new ToolDescriptor("mcp.pao.pao", "Execution identity fixture", Json("""
            {"type":"object","properties":{"op":{"type":"string","enum":["help","mkdir","job_stat"]},"rid":{"type":"string"},"jid":{"type":"string"},"dst":{"type":"string"}},"required":["op"],"additionalProperties":false}
            """), "mcp.pao");
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body);
            if (document.RootElement.GetProperty("action").GetString() == "list")
            { await http.Response.WriteAsJsonAsync(new[] { descriptor }, CerebralJson.Options); return; }
            var arguments = document.RootElement.GetProperty("arguments").Clone();
            calls.Enqueue(arguments);
            if (disconnect) { http.Abort(); return; }
            var response = responseOverride ?? CerebralJson.Element(new { isError = false, structuredContent = Receipt(
                arguments.TryGetProperty("rid", out var id) ? id.GetString()! : "legacy-provider-request", "provider-job", arguments.GetProperty("op").GetString()!) });
            await http.Response.WriteAsJsonAsync(response, CerebralJson.Options);
        });
        await app.StartAsync();
        try
        {
            var settings = new HostSettings
            {
                AccessKey = "owned-loopback-fixture",
                McpServers = [new() { Id = "pao", Enabled = true, ViaUserConnector = true, Url = app.Urls.Single(), Executable = "fixture-not-launched" }]
            };
            var profile = new AgentProfile
            {
                Id = "effect-fixture", AllowedRoots = [root],
                AllowedTools = ["mcp.pao.pao", "file.mkdir", "math.calculate"],
                Capabilities = ["mcp.pao", "diagnostics", "filesystem.write", "math.calculate"]
            };
            await using var dispatcher = new ToolDispatcher(() => settings);
            var context = new ToolExecutionContext(profile, "session", "job", root, EffectId: "effect-identity-1") { EnforceProfileScope = true };
            var destination = Path.Combine(root, "provider-owned-directory");
            var call = new ToolCall("file.mkdir", CerebralJson.Element(new { path = destination }));
            var direct = new ToolCall("mcp.pao.pao", Json("{\"op\":\"help\"}"));
            var mapped = await dispatcher.ExecuteAsync(call, context, default);
            Check(mapped.Success && mapped.Evidence is { RequestId: "effect-identity-1", JobId: "provider-job" } &&
                  calls.Last().GetProperty("rid").GetString() == context.EffectId && !Directory.Exists(destination),
                "mapped PAO receives the durable host effect RID and retains the provider JID without native replay");
            var explicitPao = await dispatcher.ExecuteAsync(direct, context, default);
            Check(explicitPao.Success && calls.Last().GetProperty("rid").GetString() == context.EffectId,
                "direct PAO dispatch receives the same host-owned identity");
            var before = calls.Count;
            var overrideAttempt = await dispatcher.ExecuteAsync(new("mcp.pao.pao", Json("{\"op\":\"help\",\"rid\":\"agent-selected\"}")), context, default);
            Check(overrideAttempt.EffectiveOutcome == ToolExecutionOutcome.Rejected && calls.Count == before,
                "an agent cannot replace the workflow request identity before provider admission");
            foreach (var id in new[] { "", "bad identity", new string('a', 129), "\u00e9" })
            {
                var invalid = await dispatcher.ExecuteAsync(call, context with { EffectId = id }, default);
                Check(invalid.EffectiveOutcome == ToolExecutionOutcome.Rejected && calls.Count == before,
                    "invalid or unbounded host effect identity is rejected before dispatch");
            }
            var legacy = await dispatcher.ExecuteAsync(direct, context with { EffectId = null }, default);
            Check(legacy.Success && !calls.Last().TryGetProperty("rid", out _), "legacy contexts retain provider-generated request identities");
            var originalConstructor = typeof(ToolExecutionContext).GetConstructor([typeof(AgentProfile), typeof(string), typeof(string), typeof(string), typeof(CortexAccessScope), typeof(bool)]);
            var restored = CerebralJson.Read<ToolExecutionContext>(CerebralJson.Element(context));
            Check(originalConstructor is not null && restored.EffectId == context.EffectId && restored.EnforceProfileScope,
                "old compiled context construction and new JSON context roundtrip remain compatible");

            responseOverride = CerebralJson.Element(new { isError = false, structuredContent = Receipt("another-request", "actual-other-job", "mkdir") });
            var mismatch = await dispatcher.ExecuteAsync(call, context, default);
            Check(mismatch.EffectiveOutcome == ToolExecutionOutcome.Unknown && mismatch.Evidence is { RequestId: "another-request", JobId: "actual-other-job" } && !Directory.Exists(destination),
                "a provider identity mismatch remains unknown and retains actual returned IDs");
            responseOverride = Json("""{"isError":false,"structuredContent":{"ok":true,"jid":"pending-job","rid":"effect-identity-1","state":"accepted","partial":false,"op":"mkdir"}}""");
            var pending = await dispatcher.ExecuteAsync(call, context, default);
            Check(pending.EffectiveOutcome == ToolExecutionOutcome.Unknown && pending.Evidence?.JobId == "pending-job" && !Directory.Exists(destination),
                "an accepted identified mutation cannot fall back to native execution");
            responseOverride = Json("""{"isError":true,"structuredContent":{"ok":false,"jid":"wrong-denial","rid":"another-request","state":"failed","partial":false,"op":"mkdir","err":"svc_down"}}""");
            var wrongDenial = await dispatcher.ExecuteAsync(call, context, default);
            Check(wrongDenial.EffectiveOutcome == ToolExecutionOutcome.Unknown && !Directory.Exists(destination),
                "another request's pre-admission rejection cannot authorize native fallback");
            responseOverride = Json("""{"isError":true,"structuredContent":{"ok":false,"jid":"matching-denial","rid":"effect-identity-1","state":"failed","partial":false,"op":"mkdir","err":"svc_down"}}""");
            var fallback = await dispatcher.ExecuteAsync(call, context, default);
            Check(fallback.Success && Directory.Exists(destination), "only a matching complete pre-submission rejection allows one native equivalent");
            Directory.Delete(destination);
            disconnect = true;
            var disconnected = await dispatcher.ExecuteAsync(call, context, default);
            disconnect = false;
            Check(disconnected.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ProviderId: "mcp.pao", RequestId: "effect-identity-1", JobId: null } && !Directory.Exists(destination),
                "lost transport retains the submitted host RID without inventing provider acceptance or repeating the mutation");

            var saved = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", context.EffectId, "existing-job");
            object Status(object existing) => new { isError = false, structuredContent = new { ok = true, rid = "status-query", jid = "status-job", state = "done", partial = false, op = "job_stat", data = new { rec = existing } } };
            responseOverride = CerebralJson.Element(Status(Receipt(context.EffectId!, "existing-job", "mkdir")));
            before = calls.Count;
            var reconciled = await dispatcher.ReconcileAsync(call, context, saved, default);
            Check(reconciled.Success && reconciled.Evidence is { RequestId: "effect-identity-1", JobId: "existing-job" } && calls.Count == before + 1 &&
                  calls.Last().GetProperty("op").GetString() == "job_stat" && calls.Last().GetProperty("jid").GetString() == "existing-job" &&
                  !calls.Last().TryGetProperty("rid", out _) && !Directory.Exists(destination),
                "recovery inspects only the exact original job and does not reuse its RID for the status query");
            var alreadyWritten = Path.Combine(root, "original-write.txt");
            await File.WriteAllTextAsync(alreadyWritten, "The original effect already wrote this file.");
            responseOverride = CerebralJson.Element(Status(Receipt(context.EffectId!, "existing-job", "wr")));
            var recoveredWrite = await dispatcher.ReconcileAsync(new("mcp.pao.pao", CerebralJson.Element(new { op = "wr", dst = alreadyWritten, text = "initial creation" })), context, saved, default);
            Check(recoveredWrite.Success && await File.ReadAllTextAsync(alreadyWritten) == "The original effect already wrote this file.",
                "receipt recovery does not reapply collision preconditions or rewrite an already completed original write");
            responseOverride = CerebralJson.Element(Status(new { ok = true, rid = context.EffectId, jid = "existing-job", state = "running", partial = false, op = "mkdir" }));
            var stillRunning = await dispatcher.ReconcileAsync(call, context, saved, default);
            Check(stillRunning.EffectiveOutcome == ToolExecutionOutcome.Unknown && stillRunning.Evidence?.State == "running",
                "successful status lookup does not convert a running original job into completion");
            responseOverride = CerebralJson.Element(Status(new { ok = false, rid = context.EffectId, jid = "existing-job", state = "failed", partial = true, exitCode = 7, op = "mkdir", err = "copy_incomplete" }));
            var failed = await dispatcher.ReconcileAsync(call, context, saved, default);
            Check(failed.Evidence is { Outcome: ToolExecutionOutcome.Failed, Partial: true, ExitCode: 7 }, "recovery retains the original partial failure and exit code");
            responseOverride = CerebralJson.Element(Status(Receipt("other-effect", "existing-job", "mkdir")));
            var wrongReceipt = await dispatcher.ReconcileAsync(call, context, saved, default);
            Check(wrongReceipt.EffectiveOutcome == ToolExecutionOutcome.Unknown, "a retained receipt with another RID cannot resolve this effect");
            responseOverride = CerebralJson.Element(Status(Receipt(context.EffectId!, "different-job", "mkdir")));
            Check((await dispatcher.ReconcileAsync(call, context, saved, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "a retained receipt with another JID cannot resolve this effect");
            before = calls.Count;
            Check((await dispatcher.ReconcileAsync(call, context, saved with { JobId = null }, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown && calls.Count == before,
                "RID-only recovery stays unknown without blind listing or dispatch");
            Check((await dispatcher.ReconcileAsync(call, context, saved with { RequestId = "other-effect" }, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown && calls.Count == before,
                "a mismatched saved request identity is rejected before status lookup");
            Check((await dispatcher.ReconcileAsync(call, context, saved with { ProviderId = "cerebral.process" }, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown && calls.Count == before,
                "unsupported native recovery never replays the tool");
            settings.McpServers[0].Enabled = false;
            Check((await dispatcher.ReconcileAsync(call, context, saved, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown && calls.Count == before,
                "missing original provider does not select another execution route");
            IToolDispatcher unsupported = new LegacyDispatcher();
            Check((await unsupported.ReconcileAsync(call, context, saved, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "legacy dispatcher implementations receive a safe default recovery seam");
            if (OperatingSystem.IsWindows())
            {
                await VerifyPipeAsync(root, profile, Check);
                await VerifyDeferredNativeAsync(root, Check);
            }
            else Console.WriteLine("SKIP: Windows application-pipe and deferred ordinary-native fixtures");
            Console.WriteLine($"Tool effect identity: {checks} checks passed (deterministic provider receipts over loopback HTTP and a preauthenticated pipe fixture; no installed provider changes).");
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static object Receipt(string rid, string jid, string op) => new { ok = true, rid, jid, state = "done", partial = false, exitCode = 0, op, data = new { message = "complete" } };

    private static async Task VerifyPipeAsync(string root, AgentProfile profile, Action<bool, string> check)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "cerebral-effect-fixture-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(lifetime.Token);
        await client.ConnectAsync(lifetime.Token); await accepting;
        var calls = new ConcurrentQueue<JsonElement>();
        var service = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var header = new byte[8]; await server.ReadExactlyAsync(header, lifetime.Token);
                    var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                    if (size is < 1 or > 65536) throw new InvalidDataException("Invalid fixture frame length.");
                    var body = new byte[size]; await server.ReadExactlyAsync(body, lifetime.Token);
                    using var document = JsonDocument.Parse(body);
                    var request = document.RootElement.Clone(); calls.Enqueue(request);
                    object result = request.GetProperty("op").GetString() == "job"
                        ? new { ok = true, rid = "query-rid", jid = "query-jid", state = "done", partial = false, data = new { rec = Receipt("pipe-effect", "pipe-job", "mkdir") } }
                        : Receipt(request.GetProperty("rid").GetString()!, "pipe-job", request.GetProperty("op").GetString()!);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(result);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
                    await server.WriteAsync(header, lifetime.Token); await server.WriteAsync(payload, lifetime.Token); await server.FlushAsync(lifetime.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException) { }
        });
        try
        {
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), stateDirectory: Path.Combine(root, "pipe-state"));
            // Supply an already-authenticated fixture session. No credential is issued or saved,
            // and no installed Cortex pipe, authorization settings or machine service is touched.
            var authorizationField = typeof(ToolDispatcher).GetField("_applicationAuthorization", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((CortexApplicationAuthorization)authorizationField.GetValue(dispatcher)!).DisposeAsync();
            var authorization = new CortexApplicationAuthorization(Path.Combine(root, "pipe-state"),
                _ => Task.FromResult<Stream>(client));
            authorizationField.SetValue(dispatcher, authorization);
            typeof(CortexApplicationAuthorization).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(authorization, client);
            var context = new ToolExecutionContext(profile, "pipe-session", "pipe-assignment", root, EffectId: "pipe-effect") { EnforceProfileScope = true };
            var call = new ToolCall("file.mkdir", CerebralJson.Element(new { path = Path.Combine(root, "pipe-owned-directory") }));
            var result = await dispatcher.ExecuteAsync(call, context, lifetime.Token);
            check(result.Success && calls.IsEmpty,
                "project-scoped operations do not depend on or consume the protected Full Access session");
            var outside = new ToolCall("file.mkdir", CerebralJson.Element(new { path = root + "-outside" }));
            var before = calls.Count;
            var scoped = await dispatcher.ExecuteAsync(outside, context, lifetime.Token);
            check(scoped.EffectiveOutcome == ToolExecutionOutcome.Rejected && calls.Count == before,
                "later global Full Access cannot widen an accepted scoped workflow");
            var explicitlyAllowed = await dispatcher.ExecuteAsync(outside, context with { FullAccess = true }, lifetime.Token);
            check(explicitlyAllowed.Success && explicitlyAllowed.Evidence is { ProviderId: "cortex.application.pipe", RequestId: "pipe-effect", JobId: "pipe-job" } &&
                  calls.Count == before + 1 && calls.Last().GetProperty("rid").GetString() == "pipe-effect",
                "an explicitly accepted out-of-scope operation uses the protected pipe with the stable host RID");
            var reconciled = await dispatcher.ReconcileAsync(outside, context with { FullAccess = true }, explicitlyAllowed.Evidence, lifetime.Token);
            check(reconciled.Success && calls.Last().GetProperty("op").GetString() == "job" &&
                  calls.Last().GetProperty("arg").GetProperty("jid").GetString() == "pipe-job" && !calls.Last().TryGetProperty("rid", out _),
                "application-pipe recovery issues only an exact read-only job query with its own identity");
        }
        finally { lifetime.Cancel(); await service; }
    }

    private sealed class LegacyDispatcher : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("Recovery must never dispatch this tool.");
    }
}
