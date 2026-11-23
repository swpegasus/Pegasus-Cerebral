using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Tools;

/// <summary>Real owned child processes and deterministic local MCP receipts; no installed provider or app.</summary>
internal static class ExpectedProcessOutcomeTests
{
    private static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;
    private static readonly string DotnetPath = Environment.ProcessPath!;
    private static int _checks;
    private static readonly List<string> Cases = [];
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("Expected process outcome: " + label);
        _checks++; Cases.Add(label);
        Console.WriteLine("CHECK " + _checks + ": " + label);
    }
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }

    public static async Task<object> RunAsync(string root, string baselineToolsPath)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) throw new ArgumentException("A fresh absolute scratch root is required.");
        Directory.CreateDirectory(root);
        var settings = new HostSettings { Executables = [new() { Id = "dotnet", Path = DotnetPath }] };
        var profile = new AgentProfile { Id = "owned-process-fixture", AllowedTools = ["process.run"],
            AllowedRoots = [root], Capabilities = ["process.execute"] };
        var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job", root, EffectId: "owned-effect") { EnforceProfileScope = true };
        await using var dispatcher = new ToolDispatcher(() => settings, stateDirectory: Path.Combine(root, "state"));
        var marker = Path.Combine(root, "child-starts.txt");
        ToolCall Call(int actual, int? expected = null, int delay = 0, int timeout = 10, bool large = false)
        {
            var args = new JsonObject { ["executableId"] = "dotnet", ["workingDirectory"] = root,
                ["arguments"] = new JsonArray(AssemblyPath, "--owned-child", actual.ToString(CultureInfo.InvariantCulture), marker,
                    delay.ToString(CultureInfo.InvariantCulture), large ? "large" : "small"), ["timeoutSeconds"] = timeout };
            if (expected is { } code) args["expectedExitCode"] = code;
            return new("process.run", CerebralJson.Element(args));
        }
        int Starts()
        {
            if (!File.Exists(marker)) return 0;
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Count(line => line == "started");
        }
        void Completed(ToolResult result, int actual, bool success, string label)
        {
            using var content = JsonDocument.Parse(result.Content);
            Check(result.Success == success && result.EffectiveOutcome == (success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed) &&
                result.Evidence is { ProviderId: "cerebral.process", State: "completed", RequestId: null, JobId: null } &&
                result.Evidence.ExitCode == actual && content.RootElement.GetProperty("exitCode").GetInt32() == actual &&
                content.RootElement.GetProperty("stdout").GetString() == "owned stdout Ω\n" &&
                content.RootElement.GetProperty("stderr").GetString() == "owned stderr λ\n", label);
        }
        var omittedZero = await dispatcher.ExecuteAsync(Call(0), context, default);
        var explicitZero = await dispatcher.ExecuteAsync(Call(0, 0), context, default);
        Completed(omittedZero, 0, true, "omitted zero succeeds only after the real child completes");
        Check(CerebralJson.Element(omittedZero).GetRawText() == CerebralJson.Element(explicitZero).GetRawText(), "explicit zero preserves the entire default result bytes");
        var omittedOne = await dispatcher.ExecuteAsync(Call(1), context, default);
        var expectedOne = await dispatcher.ExecuteAsync(Call(1, 1), context, default);
        Completed(omittedOne, 1, false, "default nonzero remains a semantic failure with actual streams and exit");
        Completed(expectedOne, 1, true, "expected one and actual one succeed with actual exit one retained");
        Check(omittedOne.Content == expectedOne.Content && omittedOne.Truncated == expectedOne.Truncated &&
            omittedOne.Evidence! with { Outcome = ToolExecutionOutcome.Succeeded } == expectedOne.Evidence,
            "opting in changes only Success and Evidence.Outcome, not payload or other evidence");
        Completed(await dispatcher.ExecuteAsync(Call(0, 1), context, default), 0, false, "unexpected zero fails when one was required");
        Completed(await dispatcher.ExecuteAsync(Call(2, 1), context, default), 2, false, "unexpected two fails when one was required");
        Completed(await dispatcher.ExecuteAsync(Call(255, 255), context, default), 255, true, "upper bound 255 is an exact actual child expectation");

        // Compare against the immutable accepted Tools payload, sharing only already-loaded Core DTOs.
        const string baselineHash = "4a02e5235a9d9f976594c22438dbbcbc997d8184fe682069931129140258cd22";
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(baselineToolsPath))).ToLowerInvariant() == baselineHash,
            "default compatibility baseline is the exact previously accepted Tools payload");
        var baselineContext = new AssemblyLoadContext("expected-process-baseline", isCollectible: true);
        try
        {
            var baselineAssembly = baselineContext.LoadFromAssemblyPath(baselineToolsPath);
            var baseline = (IToolDispatcher)Activator.CreateInstance(baselineAssembly.GetType("Cerebral.Tools.ToolDispatcher")!,
                new object?[] { (Func<HostSettings>)(() => settings), null, Path.Combine(root, "baseline-state") })!;
            try
            {
                foreach (var code in new[] { 0, 1 })
                {
                    var old = await baseline.ExecuteAsync(Call(code), context, default);
                    var current = code == 0 ? omittedZero : omittedOne;
                    Check(CerebralJson.Element(old).GetRawText() == CerebralJson.Element(current).GetRawText(),
                        "omitted expectation is byte-identical to accepted Tools at actual exit " + code);
                }
            }
            finally { await ((IAsyncDisposable)baseline).DisposeAsync(); }
        }
        finally { baselineContext.Unload(); }

        var invalid = new[] { "-1", "256", "2147483648", "1.0", "1e0", "\"1\"", "true", "null", "[]", "{}" };
        var before = Starts();
        foreach (var raw in invalid)
        {
            var node = JsonNode.Parse(Call(1).Arguments.GetRawText())!.AsObject(); node["expectedExitCode"] = JsonNode.Parse(raw);
            var result = await dispatcher.ExecuteAsync(new("process.run", CerebralJson.Element(node)), context, default);
            Check(!result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ExitCode: null } && Starts() == before,
                "invalid expectation refuses before any child: " + raw);
        }
        var duplicate = Call(1).Arguments.GetRawText()[..^1] + ",\"expectedExitCode\":1,\"expectedExitCode\":1}";
        Check((await dispatcher.ExecuteAsync(new("process.run", Json(duplicate)), context, default)).EffectiveOutcome == ToolExecutionOutcome.Rejected && Starts() == before,
            "duplicate expectation fields refuse before any child");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var result = await dispatcher.ExecuteAsync(Call(1, 1), context, cancelled.Token);
            Check(!result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Cancelled, ExitCode: null } && Starts() == before,
                "pre-start cancellation cannot become expected success");
        }
        var timeoutResult = await dispatcher.ExecuteAsync(Call(1, 1, delay: 10000, timeout: 1), context, default);
        Check(!timeoutResult.Success && timeoutResult.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ExitCode: null },
            "timeout after dispatch retains unknown rather than expected success");
        before = Starts();
        using (var cancelAfterStart = new CancellationTokenSource())
        {
            var task = dispatcher.ExecuteAsync(Call(1, 1, delay: 10000), context, cancelAfterStart.Token);
            for (var n = 0; n < 200 && Starts() == before; n++) await Task.Delay(10);
            Check(Starts() == before + 1, "cancellation fixture proves exactly one real child started");
            cancelAfterStart.Cancel();
            var result = await task;
            Check(!result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ExitCode: null } && Starts() == before + 1,
                "post-start cancellation preserves unknown and never replays the process");
        }
        settings.Executables.Add(new() { Id = "unstartable", Path = Path.Combine(root, "absent-executable.exe") });
        var unstartable = Call(1, 1).Arguments.GetRawText().Replace("\"dotnet\"", "\"unstartable\"", StringComparison.Ordinal);
        var startFailure = await dispatcher.ExecuteAsync(new("process.run", Json(unstartable)), context, default);
        Check(!startFailure.Success && startFailure.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ExitCode: null } && Starts() == before + 1,
            "an attempted native start setup failure is unknown with no invented child exit");
        var largeResult = await dispatcher.ExecuteAsync(Call(1, 1, large: true), context, default);
        Check(largeResult.Truncated && largeResult.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ExitCode: 1, State: "completed" } &&
            largeResult.Content.Contains("output omitted", StringComparison.Ordinal), "completed expected child retains output truncation explicitly");

        // A poisoned provider launch marker proves unsupported mapped expectations stop before even discovery.
        var providerLog = Path.Combine(root, "provider-operations.txt");
        settings.McpServers = [new() { Id = "pao", Enabled = true, ViaUserConnector = false, Executable = DotnetPath,
            Arguments = [AssemblyPath, "--owned-provider", providerLog, "complete"] }];
        var project = Path.Combine(root, "fixture.csproj"); await File.WriteAllTextAsync(project, "<Project />");
        ToolCall Mapped(string verb, int? expectation)
        {
            var node = new JsonObject { ["executableId"] = "dotnet", ["workingDirectory"] = root,
                ["arguments"] = new JsonArray(verb, project), ["timeoutSeconds"] = 10 };
            if (expectation is { } code) node["expectedExitCode"] = code;
            return new("process.run", CerebralJson.Element(node));
        }
        foreach (var verb in new[] { "build", "test" })
        {
            var result = await dispatcher.ExecuteAsync(Mapped(verb, 1), context, default);
            Check(result.EffectiveOutcome == ToolExecutionOutcome.Rejected && result.Content.Contains("nothing was dispatched", StringComparison.Ordinal) && !File.Exists(providerLog),
                "nonzero mapped " + verb + " refuses before provider discovery or submission");
            var recovered = await dispatcher.ReconcileAsync(Mapped(verb, 1), context,
                new(ToolExecutionOutcome.Unknown, "mcp.pao", context.EffectId, "retained-job"), default);
            Check(!recovered.Success && recovered.EffectiveOutcome == ToolExecutionOutcome.Unknown && !File.Exists(providerLog),
                "unsupported mapped recovery performs no status call or replay: " + verb);
        }
        var mappedDefault = await dispatcher.ExecuteAsync(Mapped("build", null), context, default);
        var mappedZero = await dispatcher.ExecuteAsync(Mapped("build", 0), context, default);
        Check(mappedDefault.Success && CerebralJson.Element(mappedDefault).GetRawText() == CerebralJson.Element(mappedZero).GetRawText() &&
            File.ReadAllLines(providerLog).Count(x => x == "dotnet_build") == 2,
            "mapped omitted/zero use unchanged provider input and byte-identical terminal result");
        var saved = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", context.EffectId, "retained-job");
        var recoveredDefault = await dispatcher.ReconcileAsync(Mapped("build", null), context, saved, default);
        var recoveredZero = await dispatcher.ReconcileAsync(Mapped("build", 0), context, saved, default);
        Check(recoveredDefault.Success && CerebralJson.Element(recoveredDefault).GetRawText() == CerebralJson.Element(recoveredZero).GetRawText() &&
            File.ReadAllLines(providerLog).Count(x => x == "job_stat") == 2,
            "recovery default bytes and exact original RID/JID remain unchanged with no execution replay");
        foreach (var scenario in new[] { "child-failed", "partial", "timeout", "mismatch", "malformed", "pending" })
        {
            settings.McpServers[0].Arguments[^1] = scenario;
            var result = await dispatcher.ExecuteAsync(Mapped("build", 0), context, default);
            Check(!result.Success && result.EffectiveOutcome != ToolExecutionOutcome.Succeeded,
                "default mapped route preserves refusal for " + scenario + " receipt");
        }
        _checks += PaoTerminalFailureTests.Run((value, label) => Cases.Add("existing strict receipt: " + label));
        Check(typeof(ToolDispatcher).GetInterface("Cerebral.Tools.IJobReceiptLookup") is not null,
            "accepted exact file-effect receipt lookup overlay remains in candidate Tools");
        return new { schema = "pegasus.expected-process-outcome-checks/1", success = true, checks = _checks, cases = Cases,
            realOwnedChildExecution = true, deterministicMcpStdio = true, installedProviderCalls = 0, modelCalls = 0, appCalls = 0,
            limitations = "Owned child and local fake-provider fixtures; no installed native provider or live workflow acceptance." };
    }

    public static async Task<int> ChildAsync(string[] args)
    {
        var code = int.Parse(args[1], CultureInfo.InvariantCulture);
        await File.AppendAllTextAsync(args[2], "started\n");
        await Task.Delay(int.Parse(args[3], CultureInfo.InvariantCulture));
        Console.Write(args[4] == "large" ? new string('x', 100000) : "owned stdout Ω\n");
        Console.Error.Write("owned stderr λ\n");
        return code;
    }

    public static async Task<int> ProviderAsync(string[] args)
    {
        await File.AppendAllTextAsync(args[1], "started\n");
        var scenario = args[2];
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line); var request = document.RootElement;
            if (!request.TryGetProperty("id", out var id)) continue;
            var method = request.GetProperty("method").GetString();
            object result;
            if (method == "initialize") result = new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "owned-fixture", version = "1" } };
            else if (method == "tools/list") result = new { tools = new[] { new { name = "pao", description = "deterministic owned receipt fixture", inputSchema = Json("""
                {"type":"object","properties":{"op":{"type":"string","enum":["dotnet_build","dotnet_test","job_stat"]},"src":{"type":"string"},"cwd":{"type":"string"},"rid":{"type":"string"},"jid":{"type":"string"},"tmo":{"type":"integer"},"wait":{"type":"boolean"}},"required":["op"],"additionalProperties":false}
                """) } } };
            else
            {
                var input = request.GetProperty("params").GetProperty("arguments");
                var op = input.GetProperty("op").GetString()!;
                await File.AppendAllTextAsync(args[1], op + "\n");
                object Receipt(string rid, string job, string operation) => new { ok = true, rid, jid = job,
                    state = scenario == "pending" ? "running" : "done", partial = scenario == "partial", exitCode = 0, op = operation,
                    data = new { exitCode = scenario == "child-failed" ? 1 : 0, timedOut = scenario == "timeout", stdout = "fixture stdout", stderr = "" } };
                var receipt = op == "job_stat"
                    ? new { ok = true, rid = "lookup-request", jid = "lookup-job", state = "done", partial = false, exitCode = 0, op,
                        data = new { rec = Receipt("owned-effect", input.GetProperty("jid").GetString()!, "dotnet_build") } }
                    : (object)Receipt(scenario == "mismatch" ? "another-effect" : input.GetProperty("rid").GetString()!, "retained-job", op);
                result = scenario == "malformed" ? new { isError = false, structuredContent = Json("{\"ok\":\"true\"}") }
                    : (object)new { isError = false, structuredContent = receipt };
            }
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
        }
        return 0;
    }
}
