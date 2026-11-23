using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

public static class ToolEvidenceTests
{
    public static async Task RunStressAsync(int iterations = 500)
    {
        if (iterations is < 10 or > 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-tool-plugin-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new HostSettings();
            var profile = new AgentProfile
            {
                Id = "plugin-stress", AllowedRoots = [root],
                AllowedTools = ["fixture.effect", "math.calculate"],
                Capabilities = ["diagnostics", "math.calculate"]
            };
            await using var dispatcher = new ToolDispatcher(() => settings, [new EvidenceExtension()], Path.Combine(root, "host-state"));
            var context = new ToolExecutionContext(profile, "stress-session", "stress-job", root);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetTotalMemory(true);
            var stopwatch = Stopwatch.StartNew();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var failed = await dispatcher.ExecuteAsync(new("fixture.effect", Json("{\"mode\":\"throw\"}")), context, default);
                if (failed.Success || failed.EffectiveOutcome != ToolExecutionOutcome.Unknown || failed.Content.Length > 8_192)
                    throw new InvalidOperationException("A throwing extension escaped its bounded failure result during stress.");
                if (iteration % 10 == 0)
                {
                    var healthy = await dispatcher.ExecuteAsync(new("math.calculate", Json("{\"expression\":\"6*7\"}")), context, default);
                    if (!healthy.Success || !healthy.Content.Contains("42", StringComparison.Ordinal))
                        throw new InvalidOperationException("A healthy built-in tool stopped responding after a plugin failure.");
                }
            }
            stopwatch.Stop();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var growth = Math.Max(0, GC.GetTotalMemory(true) - before);
            if (growth >= 64L * 1024 * 1024)
                throw new InvalidOperationException("Throwing extension stress exceeded the bounded 64 MiB managed-memory growth limit.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, iterations, elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                averageMilliseconds = stopwatch.Elapsed.TotalMilliseconds / iterations, managedGrowthBytes = growth,
                healthyProbes = (iterations + 9) / 10 }, CerebralJson.Options));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-tool-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var response = Json("{}");
        var disconnect = false;
        var providerCalls = 0;
        var lastProviderArguments = WorkflowContract.EmptyObject;
        var descriptor = new ToolDescriptor("mcp.pao.pao", "Deterministic PAO receipt fixture", Json("""
            {"type":"object","properties":{"op":{"type":"string","enum":["help","mkdir","read_lines","wr","replace","job_stat"]},"rid":{"type":"string"},"jid":{"type":"string"},"name":{"type":"string"},"dst":{"type":"string"},"src":{"type":"string"},"line":{"type":"integer"},"count":{"type":"integer"},"text":{"type":"string"},"old":{"type":"string"},"enc":{"type":"string"},"sha":{"type":"string"}},"required":["op"],"additionalProperties":false}
            """), "mcp.pao");
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            using var request = await JsonDocument.ParseAsync(http.Request.Body);
            if (request.RootElement.GetProperty("action").GetString() == "list")
            {
                await http.Response.WriteAsJsonAsync(new[] { descriptor }, CerebralJson.Options);
                return;
            }
            Interlocked.Increment(ref providerCalls);
            lastProviderArguments = request.RootElement.GetProperty("arguments").Clone();
            if (disconnect) { http.Abort(); return; }
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
                Id = "evidence-fixture", AllowedRoots = [root],
                AllowedTools = ["mcp.pao.pao", "file.mkdir", "file.write", "file.edit", "file.read", "math.calculate", "fixture.effect"],
                Capabilities = ["mcp.pao", "diagnostics", "filesystem.write", "filesystem.read", "math.calculate"]
            };
            await using var dispatcher = new ToolDispatcher(() => settings, [new EvidenceExtension()], Path.Combine(root, "host-state"));
            var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job", root);
            async Task<ToolResult> Receipt(string body)
            {
                response = CerebralJson.Element(new { isError = false, structuredContent = Json(body) });
                return await dispatcher.ExecuteAsync(new("mcp.pao.pao", Json("{\"op\":\"help\"}")), context, default);
            }
            var success = await Receipt("""{"ok":true,"rid":"request-123","jid":"job-456","state":"done","partial":false,"exitCode":0,"op":"help","data":{"message":"complete"},"rootCredential":"fixture-private-value"}""");
            using (var content = JsonDocument.Parse(success.Content))
                Check(success.Success && success.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
                      success.Evidence is { ProviderId: "mcp.pao", RequestId: "request-123", JobId: "job-456", State: "done", Partial: false, ExitCode: 0 } &&
                      !content.RootElement.TryGetProperty("rid", out _) && !content.RootElement.TryGetProperty("jid", out _) &&
                      !content.RootElement.TryGetProperty("state", out _) && !JsonSerializer.Serialize(success, CerebralJson.Options).Contains("fixture-private-value", StringComparison.Ordinal),
                    "compact PAO content retains host receipt identifiers and terminal evidence without known credential fields");
            foreach (var state in new[] { "accepted", "queued", "running", "waiting", "attention" })
            {
                var pending = await Receipt($$"""{"ok":true,"rid":"request-pending","jid":"job-pending","state":"{{state}}","partial":false,"op":"help"}""");
                Check(!pending.Success && pending.EffectiveOutcome == ToolExecutionOutcome.Unknown && pending.Evidence is { JobId: "job-pending", RequestId: "request-pending" },
                    "PAO " + state + " does not establish completion or lose the existing job identity");
            }
            var partial = await Receipt("""{"ok":false,"rid":"request-partial","jid":"job-partial","state":"failed","partial":true,"exitCode":7,"err":"copy_incomplete"}""");
            Check(!partial.Success && partial.Evidence is { Outcome: ToolExecutionOutcome.Failed, Partial: true, ExitCode: 7, JobId: "job-partial" },
                "a terminal partial failure preserves the failed outcome and its partial-effect evidence");
            var processFailure = await Receipt("""{"ok":true,"state":"done","partial":false,"exitCode":0,"op":"dotnet_build","data":{"exitCode":1,"stdout":"build failed"}}""");
            Check(!processFailure.Success && processFailure.Evidence is { Outcome: ToolExecutionOutcome.Failed, ExitCode: 1 },
                "a completed provider wrapper cannot hide the child build's nonzero exit code");
            var cancelled = await Receipt("""{"ok":false,"jid":"cancelled-job","state":"cancelled","partial":true,"err":"cancelled"}""");
            Check(!cancelled.Success && cancelled.Evidence is { Outcome: ToolExecutionOutcome.Cancelled, JobId: "cancelled-job", Partial: true },
                "confirmed cancellation retains partial-effect information rather than claiming no effects");
            foreach (var body in new[]
            {
                """{"ok":true,"jid":"ambiguous-job","partial":false}""",
                """{"ok":true,"jid":"ambiguous-job","state":"done","partial":false,"err":"failure"}""",
                """{"ok":true,"jid":"ambiguous-job","state":"done","partial":false,"exitCode":7}""",
                """{"ok":true,"jid":"ambiguous-job","state":"done","partial":"false"}""",
                """{"ok":true,"jid":"ambiguous-job","state":"done","partial":true}""",
                """{"ok":false,"jid":"ambiguous-job","state":"failed","partial":true,"data":{"timed_out":true}}"""
            })
            {
                var ambiguous = await Receipt(body);
                Check(!ambiguous.Success && ambiguous.EffectiveOutcome == ToolExecutionOutcome.Unknown && ambiguous.Evidence?.JobId == "ambiguous-job",
                    "incomplete, conflicting or timed-out receipts stay unconfirmed while retaining their job identity");
            }
            response = Json("""{"isError":true,"structuredContent":{"ok":true,"jid":"conflicting-transport","state":"done","partial":false}}""");
            var transportConflict = await dispatcher.ExecuteAsync(new("mcp.pao.pao", Json("{\"op\":\"help\"}")), context, default);
            Check(!transportConflict.Success && transportConflict.Evidence is { Outcome: ToolExecutionOutcome.Unknown, JobId: "conflicting-transport" },
                "an affirmative nested receipt cannot override a conflicting transport failure");
            response = CerebralJson.Element(new { isError = false,
                structuredContent = Json("""{"ok":true,"jid":"first-receipt","state":"done","partial":false}"""),
                content = new[] { new { type = "text", text = """{"ok":false,"jid":"other-receipt","state":"failed","partial":true}""" } } });
            var duplicateConflict = await dispatcher.ExecuteAsync(new("mcp.pao.pao", Json("{\"op\":\"help\"}")), context, default);
            Check(!duplicateConflict.Success && duplicateConflict.EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "conflicting structured and text receipt copies cannot establish a successful outcome");
            response = Json("""{"isError":true,"content":[{"type":"text","text":"{\"error\":\"pao_arguments_invalid\",\"message\":\"Fixture input was rejected.\"}"}]}""");
            var rejected = await dispatcher.ExecuteAsync(new("mcp.pao.pao", Json("{\"op\":\"help\"}")), context, default);
            Check(!rejected.Success && rejected.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "an explicit provider argument-admission rejection differs from an uncertain dispatched effect");

            response = Json("""{"isError":false,"structuredContent":{"ok":true,"jid":"accepted-mutation","rid":"mutation-request","state":"accepted","partial":false,"op":"mkdir"}}""");
            var destination = Path.Combine(root, "must-not-exist");
            var acceptedMutation = await dispatcher.ExecuteAsync(new("file.mkdir", CerebralJson.Element(new { path = destination })), context, default);
            Check(!acceptedMutation.Success && acceptedMutation.Evidence is { Outcome: ToolExecutionOutcome.Unknown, JobId: "accepted-mutation" } && !Directory.Exists(destination),
                "an accepted mapped mutation is not repeated through a native fallback");
            disconnect = true;
            var disconnected = await dispatcher.ExecuteAsync(new("file.mkdir", CerebralJson.Element(new { path = destination })), context, default);
            disconnect = false;
            Check(!disconnected.Success && disconnected.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ProviderId: "mcp.pao" } && !Directory.Exists(destination),
                "a real loopback disconnect after dispatch remains unknown and never triggers native mutation");

            var mutationContext = context with { EffectId = "mutation-effect-1" };
            var mappedWritePath = Path.Combine(root, "legacy-provider-write.txt");
            var returnedHash = new string('B', 64);
            var writeCall = new ToolCall("file.write", CerebralJson.Element(new
                { path = mappedWritePath, content = "legacy create request", collision = "fail" }));
            JsonElement MutationReceipt(string operation, object data) => CerebralJson.Element(new
            {
                ok = true, rid = mutationContext.EffectId, jid = "mutation-job", state = "done",
                partial = false, op = operation, data
            });
            var writeReceipt = MutationReceipt("wr", new
                { dst = mappedWritePath, enc = "utf8", bom = false, sha = returnedHash, len = 18 });
            async Task<ToolResult> Mutation(ToolCall call, JsonElement receipt)
            {
                response = CerebralJson.Element(new { isError = false, structuredContent = receipt });
                return await dispatcher.ExecuteAsync(call, mutationContext, default);
            }

            // The current provider wr contract cannot preserve create-only or revision-checked
            // replacement. These semantic calls must execute natively before provider discovery.
            var nativeWritePath = Path.Combine(root, "native-write-unicode.txt");
            const string nativeContent = "Unicode requested text: \u6f22\ud83d\ude42";
            var nativeBytes = Encoding.UTF8.GetBytes(nativeContent);
            var nativeHash = Convert.ToHexString(SHA256.HashData(nativeBytes)).ToLowerInvariant();
            var beforeMutation = providerCalls;
            var nativeCreate = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeWritePath, content = nativeContent, collision = "fail" })), writeReceipt);
            var nativeCreateData = Json(nativeCreate.Content);
            Check(nativeCreate.Success && nativeCreateData.GetProperty("path").GetString() == nativeWritePath &&
                  nativeCreateData.GetProperty("sha256").GetString() == nativeHash &&
                  nativeCreateData.GetProperty("bytes").GetInt32() == nativeBytes.Length &&
                  (await File.ReadAllBytesAsync(nativeWritePath)).SequenceEqual(nativeBytes) &&
                  providerCalls == beforeMutation,
                "semantic create uses exact scoped native Unicode bytes before submitting any unconditional provider wr");
            var nativeCollision = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeWritePath, content = "must not overwrite", collision = "fail" })), writeReceipt);
            Check(nativeCollision.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                  (await File.ReadAllBytesAsync(nativeWritePath)).SequenceEqual(nativeBytes) &&
                  providerCalls == beforeMutation,
                "native create-only collision rejects with no write and zero provider calls");
            var staleWrite = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeWritePath, content = "stale replacement", collision = "replace", expectedSha256 = new string('0', 64) })), writeReceipt);
            Check(staleWrite.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                  (await File.ReadAllBytesAsync(nativeWritePath)).SequenceEqual(nativeBytes) &&
                  providerCalls == beforeMutation,
                "semantic replacement with a stale preimage rejects natively without mutation or provider submission");
            const string replacementContent = "Revision-checked native replacement";
            var replacementWrite = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeWritePath, content = replacementContent, collision = "replace", expectedSha256 = nativeHash })), writeReceipt);
            Check(replacementWrite.Success && await File.ReadAllTextAsync(nativeWritePath) == replacementContent &&
                  Json(replacementWrite.Content).GetProperty("sha256").GetString() ==
                      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replacementContent))).ToLowerInvariant() &&
                  providerCalls == beforeMutation,
                "matching semantic replacement retains the native current-SHA contract and actual resulting receipt");
            var skippedWrite = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeWritePath, content = "must not overwrite", collision = "skip" })), writeReceipt);
            Check(skippedWrite.Success && Json(skippedWrite.Content).GetProperty("skipped").GetBoolean() &&
                  await File.ReadAllTextAsync(nativeWritePath) == replacementContent && providerCalls == beforeMutation,
                "semantic skip remains a confirmed native no-op and does not submit provider wr");
            var nativeSkipPath = Path.Combine(root, "native-skip-create.txt");
            var skippedCreate = await Mutation(new("file.write", CerebralJson.Element(new
                { path = nativeSkipPath, content = "new skip-policy destination", collision = "skip" })), writeReceipt);
            Check(skippedCreate.Success && await File.ReadAllTextAsync(nativeSkipPath) == "new skip-policy destination" &&
                  providerCalls == beforeMutation,
                "skip policy creates an absent destination through the native writer only");
            var missingReplace = await Mutation(new("file.write", CerebralJson.Element(new
                { path = mappedWritePath, content = "must not create", collision = "replace", expectedSha256 = nativeHash })), writeReceipt);
            Check(missingReplace.EffectiveOutcome == ToolExecutionOutcome.Rejected && !File.Exists(mappedWritePath) &&
                  providerCalls == beforeMutation,
                "semantic replacement requires an existing destination before native dispatch");
            var narrowRoot = Path.Combine(root, "narrow");
            Directory.CreateDirectory(narrowRoot);
            var narrowProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            narrowProfile.AllowedRoots = [narrowRoot];
            var outsideNarrow = Path.Combine(root, "outside-narrow.txt");
            var outOfScope = await dispatcher.ExecuteAsync(new("file.write", CerebralJson.Element(new
                { path = outsideNarrow, content = "must not escape", collision = "fail" })),
                mutationContext with { Profile = narrowProfile, WorkingDirectory = narrowRoot, FullAccess = true }, default);
            Check(outOfScope.EffectiveOutcome == ToolExecutionOutcome.Rejected && !File.Exists(outsideNarrow) &&
                  providerCalls == beforeMutation,
                "native semantic write keeps FullAccess=false confinement even when the caller has Full Access");

            var mappedEditPath = Path.Combine(root, "provider-edit-unicode.txt");
            const string originalEditText = "Before\u6f22";
            await File.WriteAllTextAsync(mappedEditPath, originalEditText, new UTF8Encoding(false));
            var preimage = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalEditText))).ToLowerInvariant();
            var editCall = new ToolCall("file.edit", CerebralJson.Element(new
            {
                path = mappedEditPath, oldText = "Before", newText = "After", expectedSha256 = preimage
            }));
            var editReceipt = MutationReceipt("replace", new
            {
                dst = mappedEditPath, sha_before = preimage, sha = returnedHash, count = 1,
                enc = "utf8", bom = false, len = 21
            });
            beforeMutation = providerCalls;
            var unchangedEditCall = new ToolCall("file.edit", CerebralJson.Element(new
                { path = mappedEditPath, oldText = "Before", newText = "Before", expectedSha256 = preimage }));
            var unchangedEdit = await Mutation(unchangedEditCall, editReceipt);
            Check(unchangedEdit.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                  unchangedEdit.Content.Contains("unchanged replacement is not a source edit", StringComparison.Ordinal) &&
                  await File.ReadAllTextAsync(mappedEditPath) == originalEditText && providerCalls == beforeMutation,
                "unchanged semantic edit rejects before provider discovery or native mutation");
            var semanticEdit = await Mutation(editCall, editReceipt);
            var editData = Json(semanticEdit.Content);
            Check(semanticEdit.Success && editData.GetProperty("path").GetString() == mappedEditPath &&
                  editData.GetProperty("sha256").GetString() == returnedHash &&
                  editData.GetProperty("bytes").GetInt32() == 21 &&
                  semanticEdit.Evidence is { ProviderId: "mcp.pao", RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                  !editData.TryGetProperty("lines", out _) && !editData.TryGetProperty("words", out _) &&
                  lastProviderArguments.GetProperty("sha").GetString() == preimage &&
                  lastProviderArguments.GetProperty("count").GetInt32() == 1 &&
                  await File.ReadAllTextAsync(mappedEditPath) == originalEditText &&
                  providerCalls == beforeMutation + 1,
                "mapped PAO edit requires one provider-confirmed replacement with an actual matching preimage and retained job identity");

            foreach (var invalidMutation in new[]
            {
                (Field: "dst", Value: (JsonNode?)JsonValue.Create(Path.Combine(root, "foreign.txt"))),
                (Field: "dst", Value: (JsonNode?)JsonValue.Create("relative.txt")),
                (Field: "sha", Value: (JsonNode?)JsonValue.Create("invalid-hash")),
                (Field: "len", Value: (JsonNode?)JsonValue.Create(-1)),
                (Field: "len", Value: (JsonNode?)JsonValue.Create("21")),
                (Field: "enc", Value: (JsonNode?)JsonValue.Create("ascii")),
                (Field: "bom", Value: (JsonNode?)JsonValue.Create(true)),
                (Field: "lines", Value: (JsonNode?)JsonValue.Create(-1)),
                (Field: "sha_before", Value: (JsonNode?)JsonValue.Create(new string('c', 64))),
                (Field: "count", Value: (JsonNode?)JsonValue.Create(2))
            })
            {
                var changed = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
                changed["data"]![invalidMutation.Field] = invalidMutation.Value;
                beforeMutation = providerCalls;
                var invalid = await Mutation(editCall, Json(changed.ToJsonString(CerebralJson.Options)));
                Check(!invalid.Success && invalid.Evidence is { Outcome: ToolExecutionOutcome.Unknown,
                          ProviderId: "mcp.pao", RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                      providerCalls == beforeMutation + 1 && await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                    "malformed or foreign PAO edit " + invalidMutation.Field + " stays unconfirmed with actual identity and no fallback");
            }
            foreach (var missingField in new[] { "dst", "sha", "len", "enc", "bom", "sha_before", "count" })
            {
                var changed = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
                changed["data"]!.AsObject().Remove(missingField);
                beforeMutation = providerCalls;
                var invalid = await Mutation(editCall, Json(changed.ToJsonString(CerebralJson.Options)));
                Check(!invalid.Success && invalid.Evidence is { Outcome: ToolExecutionOutcome.Unknown, JobId: "mutation-job" } &&
                      providerCalls == beforeMutation + 1 && await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                    "mapped edit missing provider " + missingField + " cannot borrow completion evidence from its request");
            }
            foreach (var invalidJobId in new string?[] { null, "", "invalid job", new string('j', 257) })
            {
                var changed = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
                if (invalidJobId is null) changed.Remove("jid");
                else changed["jid"] = invalidJobId;
                beforeMutation = providerCalls;
                var invalid = await Mutation(editCall, Json(changed.ToJsonString(CerebralJson.Options)));
                Check(!invalid.Success && invalid.Evidence is { Outcome: ToolExecutionOutcome.Unknown,
                          ProviderId: "mcp.pao", RequestId: "mutation-effect-1", JobId: null } &&
                      providerCalls == beforeMutation + 1 && await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                    "mapped edit requires a valid retained JobId without losing the actual request/provider evidence");
            }
            var pendingEditReceipt = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
            pendingEditReceipt["state"] = "accepted";
            beforeMutation = providerCalls;
            var pendingEdit = await Mutation(editCall, Json(pendingEditReceipt.ToJsonString(CerebralJson.Options)));
            Check(!pendingEdit.Success && pendingEdit.Evidence is { Outcome: ToolExecutionOutcome.Unknown, State: "accepted", JobId: "mutation-job" } &&
                  providerCalls == beforeMutation + 1 && await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                "strong-looking edit fields cannot convert an accepted provider job into completion or trigger native replay");
            var partialEditReceipt = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
            partialEditReceipt["partial"] = true;
            var partialEdit = await Mutation(editCall, Json(partialEditReceipt.ToJsonString(CerebralJson.Options)));
            Check(!partialEdit.Success && partialEdit.Evidence is { Outcome: ToolExecutionOutcome.Unknown, Partial: true, JobId: "mutation-job" } &&
                  await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                "semantic projection preserves an unconfirmed partial edit instead of fabricating a complete flat receipt");
            var wrongOperation = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
            wrongOperation["op"] = "mkdir";
            Check((await Mutation(editCall, Json(wrongOperation.ToJsonString(CerebralJson.Options)))).EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "a confirmed provider receipt for another operation cannot establish semantic edit completion");
            var wrongRequest = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
            wrongRequest["rid"] = "foreign-effect";
            var foreignRequest = await Mutation(editCall, Json(wrongRequest.ToJsonString(CerebralJson.Options)));
            Check(foreignRequest.Evidence is { Outcome: ToolExecutionOutcome.Unknown, RequestId: "foreign-effect", JobId: "mutation-job" },
                "semantic projection never overrides existing foreign-request receipt rejection");
            var optionalCounts = JsonNode.Parse(editReceipt.GetRawText())!.AsObject();
            optionalCounts["data"]!["lines"] = 2; optionalCounts["data"]!["words"] = 3;
            var counted = Json((await Mutation(editCall, Json(optionalCounts.ToJsonString(CerebralJson.Options)))).Content);
            Check(counted.GetProperty("lines").GetInt32() == 2 && counted.GetProperty("words").GetInt32() == 3,
                "optional provider-supplied display counts are preserved exactly when present");

            response = CerebralJson.Element(new { isError = false, structuredContent = writeReceipt });
            beforeMutation = providerCalls;
            var directWrite = await dispatcher.ExecuteAsync(new("mcp.pao.pao", CerebralJson.Element(new
                { op = "wr", dst = mappedWritePath, text = "direct typed request", enc = "utf-8" })), mutationContext, default);
            var directData = Json(directWrite.Content);
            Check(directWrite.Success && directData.GetProperty("op").GetString() == "wr" &&
                  directData.GetProperty("data").GetProperty("dst").GetString() == mappedWritePath &&
                  !directData.TryGetProperty("path", out _) && !File.Exists(mappedWritePath) && providerCalls == beforeMutation + 1,
                "direct mcp PAO keeps its raw wr contract without claiming semantic create-only or revision-checked behavior");

            var savedMutation = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", mutationContext.EffectId, "mutation-job");
            async Task<ToolResult> RecoverMutation(ToolCall call, JsonElement original)
            {
                response = CerebralJson.Element(new
                {
                    isError = false, structuredContent = new
                    {
                        ok = true, rid = "status-query", jid = "status-job", state = "done", partial = false,
                        op = "job_stat", data = new { rec = original }
                    }
                });
                return await dispatcher.ReconcileAsync(call, mutationContext, savedMutation, default);
            }
            beforeMutation = providerCalls;
            var recoveredMutation = await RecoverMutation(editCall, editReceipt);
            var recoveredData = Json(recoveredMutation.Content);
            Check(recoveredMutation.Success && recoveredData.GetProperty("path").GetString() == mappedEditPath &&
                  recoveredData.GetProperty("sha256").GetString() == returnedHash &&
                  recoveredMutation.Evidence is { RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                  providerCalls == beforeMutation + 1 && lastProviderArguments.GetProperty("op").GetString() == "job_stat" &&
                  await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                "mapped edit recovery projects only the matched original receipt without replaying execution");
            beforeMutation = providerCalls;
            var unchangedRecovery = await RecoverMutation(unchangedEditCall, editReceipt);
            Check(unchangedRecovery.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
                  unchangedRecovery.Evidence is { RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                  providerCalls == beforeMutation && await File.ReadAllTextAsync(mappedEditPath) == originalEditText,
                "legacy unchanged edit recovery cannot establish a source edit or dispatch even a status lookup");

            foreach (var legacyCollision in new[] { "fail", "skip" })
            {
                var legacyWrite = legacyCollision == "fail" ? writeCall : new ToolCall("file.write", CerebralJson.Element(new
                    { path = mappedWritePath, content = "legacy skip request", collision = legacyCollision }));
                beforeMutation = providerCalls;
                var legacyResult = await RecoverMutation(legacyWrite, writeReceipt);
                Check(legacyResult.Evidence is { Outcome: ToolExecutionOutcome.Unknown,
                          ProviderId: "mcp.pao", RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                      !legacyResult.Success && !File.Exists(mappedWritePath) && providerCalls == beforeMutation + 1,
                    "legacy wr recovery cannot claim " + legacyCollision + " collision semantics without absent-destination proof");
            }
            var legacyReplace = new ToolCall("file.write", CerebralJson.Element(new
                { path = mappedWritePath, content = "legacy replacement", collision = "replace", expectedSha256 = preimage }));
            beforeMutation = providerCalls;
            var missingBefore = await RecoverMutation(legacyReplace, writeReceipt);
            Check(missingBefore.Evidence is { Outcome: ToolExecutionOutcome.Unknown,
                      ProviderId: "mcp.pao", RequestId: "mutation-effect-1", JobId: "mutation-job" } &&
                  !missingBefore.Success && !File.Exists(mappedWritePath) && providerCalls == beforeMutation + 1,
                "legacy wr replacement recovery cannot assume the requested preimage was actually enforced");
            var foreignBeforeReceipt = JsonNode.Parse(writeReceipt.GetRawText())!.AsObject();
            foreignBeforeReceipt["data"]!["sha_before"] = new string('c', 64);
            var foreignBefore = await RecoverMutation(legacyReplace, Json(foreignBeforeReceipt.ToJsonString(CerebralJson.Options)));
            Check(foreignBefore.EffectiveOutcome == ToolExecutionOutcome.Unknown && !File.Exists(mappedWritePath),
                "legacy wr replacement recovery rejects an actual provider preimage that differs from the accepted SHA");
            var confirmedBeforeReceipt = JsonNode.Parse(writeReceipt.GetRawText())!.AsObject();
            confirmedBeforeReceipt["data"]!["sha_before"] = preimage;
            var confirmedBefore = await RecoverMutation(legacyReplace, Json(confirmedBeforeReceipt.ToJsonString(CerebralJson.Options)));
            Check(confirmedBefore.Success && Json(confirmedBefore.Content).GetProperty("sha256").GetString() == returnedHash &&
                  confirmedBefore.Evidence is { RequestId: "mutation-effect-1", JobId: "mutation-job" } && !File.Exists(mappedWritePath),
                "legacy replacement recovery uses an actual matched provider preimage and never replays the write");

            var deniedProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            deniedProfile.Capabilities.Clear();
            var denied = await dispatcher.ExecuteAsync(new("math.calculate", Json("{\"expression\":\"1+2\"}")), context with { Profile = deniedProfile }, default);
            Check(!denied.Success && denied.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "local capability rejection is identified before any execution is dispatched");
            var native = await dispatcher.ExecuteAsync(new("math.calculate", Json("{\"expression\":\"1+2\"}")), context, default);
            var nativeFailed = await dispatcher.ExecuteAsync(new("fixture.effect", Json("{\"mode\":\"failed\"}")), context, default);
            Check(native.Success && native.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
                  !nativeFailed.Success && nativeFailed.EffectiveOutcome == ToolExecutionOutcome.Failed,
                "legacy native success and explicit returned failure retain their existing terminal meaning");
            var nativeCancelled = await dispatcher.ExecuteAsync(new("fixture.effect", Json("{\"mode\":\"cancelled\"}")), context, default);
            var nativeThrown = await dispatcher.ExecuteAsync(new("fixture.effect", Json("{\"mode\":\"throw\"}")), context, default);
            Check(nativeCancelled.EffectiveOutcome == ToolExecutionOutcome.Cancelled && nativeThrown.EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "extension cancellation evidence is preserved while an exception after opaque dispatch stays unknown");
            var timeout = await dispatcher.ExecuteAsync(new("fixture.effect", Json("{\"mode\":\"wait\",\"timeoutSeconds\":1}")), context, default);
            Check(timeout.EffectiveOutcome == ToolExecutionOutcome.Unknown && !timeout.Content.Contains("processes were stopped", StringComparison.Ordinal),
                "opaque provider timeout does not claim its processes were stopped");
            Console.WriteLine($"Tool evidence: {checks} checks passed (deterministic provider responses over real loopback HTTP, plus task-scoped native filesystem, arithmetic and extension fixtures).");
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class EvidenceExtension : IToolExtension
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = [new("fixture.effect", "Outcome fixture", Schema(), "diagnostics")];
        private static JsonElement Schema() { using var document = JsonDocument.Parse("{\"type\":\"object\"}"); return document.RootElement.Clone(); }
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var mode = call.Arguments.GetProperty("mode").GetString();
            if (mode == "throw") throw new InvalidOperationException("Fixture effect lost its outcome after dispatch.");
            if (mode == "wait") await Task.Delay(Timeout.Infinite, cancellationToken);
            return mode == "cancelled" ? new(false, "Fixture cancellation.") { Evidence = new(ToolExecutionOutcome.Cancelled, "fixture") }
                : new(false, "Fixture returned a terminal failure.");
        }
    }
}
