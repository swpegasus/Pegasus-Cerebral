using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

/// <summary>Real stdio pipes against a deterministic silent child; no installed provider is invoked.</summary>
internal static class McpCancellationTests
{
    public const string FixtureArgument = "--fixture-mcp-cancellation";

    public static async Task<int> RunFixtureAsync(string marker)
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (!request.TryGetProperty("id", out var id)) continue;
            var method = request.GetProperty("method").GetString();
            if (method == "tools/call")
            {
                var input = request.GetProperty("params").GetProperty("arguments");
                var receipt = new { processId = Environment.ProcessId, rid = input.TryGetProperty("rid", out var rid) ? rid.GetString() : null };
                await File.WriteAllTextAsync(marker + ".tmp", JsonSerializer.Serialize(receipt));
                File.Move(marker + ".tmp", marker);
                // Deliberately stop consuming stdin and produce no response. Only the test's owned
                // transport abort should release its pending anonymous-pipe read.
                await Task.Delay(TimeSpan.FromMinutes(10));
                return 0;
            }
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "cancellation-fixture", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "pao", description = "Cancellation fixture", inputSchema = new
                {
                    type = "object", properties = new
                    {
                        op = new { type = "string", @enum = new[] { "read_lines" } }, src = new { type = "string" },
                        line = new { type = "integer" }, count = new { type = "integer" }, rid = new { type = "string" }
                    }
                } } } },
                _ => new { }
            };
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
            Console.Out.Flush();
        }
        return 0;
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directMarker = Path.Combine(root, "mcp-direct-cancel.json");
        using (var cancellation = new CancellationTokenSource())
        {
            await using var client = await McpStdioClient.ConnectAsync(Registration(directMarker), default);
            var request = client.CallToolAsync("pao", CerebralJson.Element(new { op = "read_lines", rid = "direct-cancel" }), cancellation.Token);
            await WaitForMarkerAsync(directMarker, request);
            var watch = Stopwatch.StartNew();
            await cancellation.CancelAsync();
            var cancelled = false;
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && watch.Elapsed < TimeSpan.FromSeconds(5), "silent MCP pipe request cancels promptly after actual dispatch");
            var reuseRejected = false;
            try { await client.CallToolAsync("pao", CerebralJson.Element(new { op = "read_lines" }), default); }
            catch (IOException) { reuseRejected = true; }
            check(reuseRejected, "interrupted MCP transport cannot silently accept another request");
        }
        using (var marker = JsonDocument.Parse(await File.ReadAllTextAsync(directMarker)))
            check(Exited(marker.RootElement.GetProperty("processId").GetInt32()), "cancelled MCP fixture child is stopped during transport cleanup");

        var workflowMarker = Path.Combine(root, "mcp-workflow-cancel.json");
        var source = Path.Combine(root, "mcp-cancel-source.txt");
        await File.WriteAllTextAsync(source, "fixture source");
        var settings = new HostSettings { McpServers = [Registration(workflowMarker)] };
        var profile = new AgentProfile
        {
            Id = "cancel-agent", AllowedRoots = [root], AllowedTools = ["file.read"],
            Capabilities = ["filesystem.read", "mcp.cortex-cancel"]
        };
        await using var tools = new ToolDispatcher(() => settings);
        using var stop = new CancellationTokenSource();
        const string effectId = "workflow-cancel-effect";
        var execution = tools.ExecuteAsync(new("file.read", CerebralJson.Element(new { path = source })),
            new(profile, "conversation", "assignment", root, EffectId: effectId) { EnforceProfileScope = true }, stop.Token);
        await WaitForMarkerAsync(workflowMarker, execution);
        var elapsed = Stopwatch.StartNew();
        await stop.CancelAsync();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        check(elapsed.Elapsed < TimeSpan.FromSeconds(5) && result.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            result.Evidence is { RequestId: effectId, ProviderId: "mcp.cortex-cancel", JobId: null },
            "workflow cancellation retains the dispatched RID and unknown broker outcome");
        using var finalMarker = JsonDocument.Parse(await File.ReadAllTextAsync(workflowMarker));
        check(finalMarker.RootElement.GetProperty("rid").GetString() == effectId &&
            Exited(finalMarker.RootElement.GetProperty("processId").GetInt32()),
            "workflow effect identity reaches the fixture and only its owned stdio child is stopped");
    }

    private static McpRegistration Registration(string marker)
    {
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange([FixtureArgument, marker]);
        return new() { Id = "cortex-cancel", Enabled = true, Executable = Environment.ProcessPath!, Arguments = arguments, ViaUserConnector = false };
    }

    private static async Task WaitForMarkerAsync(string marker, Task request)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            if (request.IsCompleted) { await request; throw new InvalidOperationException("MCP fixture request completed before dispatch marker."); }
            if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Silent MCP fixture did not receive the request.");
            await Task.Delay(25);
        }
    }

    private static bool Exited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
