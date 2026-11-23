using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;

// Tests use a deterministic command delegate. The final lane starts this executable as a
// real stdio process; neither lane authenticates to an installed Worker or runs a model.
if (args.FirstOrDefault() is "--desktop-control-call" or "--configuration-control-call")
{
    var fixture = JsonSerializer.Deserialize<JsonElement>(args[4]);
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, id = args[3], op = args[2], state = "completed", data = fixture, error = (string?)null }));
    return 0;
}
if (args.FirstOrDefault() == "--stdio-fixture")
{
    using var registry = new OperatorMcpRegistry(async (operation, arguments, token) =>
    {
        if (operation == "models.load") { await Task.Delay(TimeSpan.FromSeconds(30), token); return CommandResult.Ok(); }
        return CommandResult.Ok(new { fixture = true, operation, accessKey = "fixture-secret-not-for-output" });
    });
    return await OperatorMcpHost.RunAsync(registry, Console.In, Console.Out, 1);
}

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + label);
    passed++;
}
async Task Reject(Func<Task> action, string label)
{
    try { await action(); }
    catch (ArgumentException) { Check(true, label); return; }
    throw new InvalidOperationException("FAILED: " + label);
}
JsonElement J(object value) => CerebralJson.Element(value);
JsonElement Parse(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

var calls = new List<(string Operation, JsonElement Arguments)>();
var profile = new AgentProfile { Id = "worker-agent", Revision = 7, Name = "Original", ModelId = "model-a", Instructions = "private instructions",
    PluginSettings = new() { ["opaque"] = J(new { credentialReference = "keep-this-reference", apiKey = "opaque-secret-canary" }) } };
using var catalog = new OperatorMcpRegistry((operation, arguments, token) =>
{
    calls.Add((operation, arguments.Clone()));
    return Task.FromResult(operation switch
    {
        "settings.get" => CommandResult.Ok(new { name = "fixture", accessKey = "owner-secret-canary", workers = new[] { new { accessKey = "worker-secret", peerToken = "peer-secret" } }, arbitrary = "not-an-inspection-field" }),
        "agents.list" => CommandResult.Ok(new[] { profile }),
        "agents.save" => arguments.GetProperty("revision").GetInt32() != profile.Revision
            ? CommandResult.Fail("Agent changed") : CommandResult.Ok(arguments),
        _ => CommandResult.Ok(new { operation, nested = new { password = "password-canary", message = "accessKey=diagnostic-canary" } })
    });
});
var discovery = catalog.Discover().GetRawText();
Check(!discovery.Contains("private instructions") && !discovery.Contains("secret-canary"), "discovery is metadata only");
Check(discovery.Contains("Configuration domain actions are unavailable"), "coverage gaps are explicit");
Check(catalog.Describe("agents.update").GetProperty("inputSchema").GetProperty("additionalProperties").ValueKind == JsonValueKind.False, "closed operation arguments");
var settings = await catalog.CallAsync("settings.inspect", J(new { }));
Check(settings.State == "completed" && settings.Data.GetProperty("name").GetString() == "fixture", "safe settings inspection");
Check(!settings.Data.GetRawText().Contains("secret") && !settings.Data.TryGetProperty("workers", out _) && !settings.Data.TryGetProperty("arbitrary", out _), "settings inspection uses an explicit projection");
var before = calls.Count;
await Reject(() => catalog.CallAsync("settings.save", J(new { accessKey = "new" })), "raw settings writes cannot enter MCP");
await Reject(() => catalog.CallAsync("health", J(new { owner = true })), "tool JSON cannot select authority");
await Reject(() => catalog.CallAsync("health", Parse("{\"id\":1,\"ID\":2}")), "duplicate argument names are rejected");
await Reject(() => catalog.CallAsync("arbitrary.execute", J(new { command = "never run" })), "unknown operations are denied");
Check(calls.Count == before, "unadmitted requests never dispatch");
await Reject(() => catalog.CallAsync("models.load", J(new { id = new string('x', OperatorMcpRegistry.MaximumArgumentBytes) })), "oversized arguments reject before dispatch");
var mutableNames = new List<string> { "id" };
using (var frozenCatalog = new OperatorMcpRegistry((_, _, _) => Task.FromResult(CommandResult.Ok()), additionalOperations:
    [new("fixture.describe", "Trusted fixture contribution", false, mutableNames, ["id"]) { InputSchema = J(new { type = "object", properties = new { id = new { type = "string" } } }) }]))
{
    mutableNames.Add("owner");
    await Reject(() => frozenCatalog.CallAsync("fixture.describe", J(new { id = "x", owner = true })), "catalog admission freezes argument declarations");
    Check(frozenCatalog.Describe("fixture.describe").GetProperty("schemaCoverage").GetString() == "owner-supplied versioned schema", "owning module's exact schema remains distinguishable");
}
var agentList = await catalog.CallAsync("agents.list", J(new { }));
Check(!agentList.Data.GetRawText().Contains("private instructions") && !agentList.Data.GetRawText().Contains("opaque-secret"), "generic agent inspection omits private instructions and module data");
var update = await catalog.CallAsync("agents.update", J(new { id = "worker-agent", expectedRevision = 7, changes = new { name = "Renamed" } }));
var saved = calls.Last(x => x.Operation == "agents.save").Arguments;
Check(update.State == "completed" && saved.GetProperty("name").GetString() == "Renamed", "revisioned agent update reaches owning save handler");
Check(saved.GetProperty("pluginSettings").GetProperty("opaque").GetProperty("credentialReference").GetString() == "keep-this-reference", "update preserves undisclosed module fields");
Check(saved.GetProperty("instructions").GetString() == "private instructions", "update preserves separate instruction content");
Check(!update.Data.GetRawText().Contains("opaque-secret"), "saved response is redacted");
before = calls.Count(x => x.Operation == "agents.save");
var conflict = await catalog.CallAsync("agents.update", J(new { id = "worker-agent", expectedRevision = 6, changes = new { name = "Stale" } }));
Check(conflict.State == "rejected" && calls.Count(x => x.Operation == "agents.save") == before, "stale revision refuses without a save");
await Reject(() => catalog.CallAsync("agents.update", J(new { id = "worker-agent", expectedRevision = 7, changes = new { pluginSettings = new { } } })), "generic agent edits cannot overwrite undisclosed module settings");
var health = await catalog.CallAsync("health", J(new { }));
Check(!health.Data.GetRawText().Contains("password-canary") && !health.Data.GetRawText().Contains("diagnostic-canary"), "nested credentials and accidental credential diagnostics are redacted");
var conventionalSecrets = OperatorMcpRegistry.Sanitize(Parse("{\"access_key\":\"hidden-a\",\"HF_TOKEN\":\"hidden-b\",\"credentialReference\":\"retained-reference\"}"));
Check(!conventionalSecrets.GetRawText().Contains("hidden-") && conventionalSecrets.GetProperty("credentialReference").GetString() == "retained-reference", "credential spelling variants are hidden while references remain usable");
using (var failedOwner = new OperatorMcpRegistry((_, _, _) => Task.FromResult(CommandResult.Fail("Failed after dispatch"))))
{
    var failed = await failedOwner.CallAsync("models.unload", J(new { id = "model-a" }));
    Check(failed.State == "failed" && failed.ReconciliationRequired, "management failure is not proof of no effects");
}
var absentDesktop = await catalog.CallAsync("main.snapshot.inspect", J(new { requestId = "snapshot-1", arguments = new { } }));
Check(absentDesktop.State == "unavailable", "missing desktop binding does not fall back");

string? desktopOperation = null;
using var desktopCatalog = new OperatorMcpRegistry((_, _, _) => throw new InvalidOperationException("Desktop must not become a Worker call"),
    (target, operation, requestId, arguments, token) =>
    {
        desktopOperation = operation;
        Check(target == "main" && requestId == "send-1" && arguments.GetProperty("expected").GetProperty("selectionVersion").GetInt32() == 3, "desktop expectation is preserved");
        return Task.FromResult(new OperatorMcpCallResult("main." + operation, "accepted", J(new { accepted = true })));
    });
var desktopCall = await desktopCatalog.CallAsync("main.message.send", J(new { requestId = "send-1", arguments = new { expected = new { selectionVersion = 3 } } }));
Check(desktopCall.State == "accepted" && desktopOperation == "message.send", "desktop route uses canonical existing semantic operation");

if (OperatingSystem.IsWindows())
{
    var helper = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
    var entry = Path.ChangeExtension(helper, ".dll");
    using var executableFile = File.OpenRead(helper); var helperHash = Convert.ToHexString(SHA256.HashData(executableFile)).ToLowerInvariant();
    using var entryFile = File.OpenRead(entry); var entryHash = Convert.ToHexString(SHA256.HashData(entryFile)).ToLowerInvariant();
    var semantic = new OperatorMcpDesktopTransport([new("main", helper, helperHash, entryHash, Path.Combine(Path.GetDirectoryName(helper)!, "fixture-descriptor.json"))]);
    var roundTrip = await semantic.CallAsync("main", "snapshot.inspect", "unicode-1", J(new { text = "quote \" space Ω 雪" }), CancellationToken.None);
    Check(roundTrip.State == "completed" && roundTrip.Data.GetProperty("text").GetString() == "quote \" space Ω 雪", "real semantic-CLI fixture preserves argv and Unicode");
    var unavailable = await semantic.CallAsync("configuration", "page.inspect", "missing-1", J(new { }), CancellationToken.None);
    Check(unavailable.State == "unavailable", "fixed target cannot reroute to another desktop");
    var changed = new OperatorMcpDesktopTransport([new("main", helper, new string('0', 64), entryHash, Path.Combine(Path.GetDirectoryName(helper)!, "fixture-descriptor.json"))]);
    try { await changed.CallAsync("main", "snapshot.inspect", "changed-1", J(new { }), CancellationToken.None); throw new Exception("Changed pin admitted"); }
    catch (InvalidDataException) { Check(true, "changed executable pin refuses before launch"); }
}

using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
using var delayed = new OperatorMcpRegistry(async (_, _, token) => { await Task.Delay(1000, token); return CommandResult.Ok(); });
var uncertain = await delayed.CallAsync("models.load", J(new { id = "model-a" }), cancelled.Token);
Check(uncertain.State == "uncertain" && uncertain.ReconciliationRequired, "cancelled wait does not claim model stop");
var cancelledRead = await delayed.CallAsync("health", J(new { }), cancelled.Token);
Check(cancelledRead.State == "cancelled" && !cancelledRead.ReconciliationRequired, "cancelled read is distinct from uncertain effects");
using (var malformedOwner = new OperatorMcpRegistry((_, _, _) => throw new InvalidDataException("Malformed owner reply")))
{
    var malformedEffect = await malformedOwner.CallAsync("models.load", J(new { id = "model-a" }));
    Check(malformedEffect.State == "uncertain" && malformedEffect.ReconciliationRequired, "malformed reply preserves uncertain effects without crashing the adapter");
}

var inputLines = new[]
{
    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}",
    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"cerebral_call\",\"arguments\":{\"operation\":\"models.load\",\"arguments\":{\"id\":\"fixture-model\"}}}}",
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"cerebral_call\",\"arguments\":{\"operation\":\"health\",\"arguments\":{}}}}",
    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}",
    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\"}"
};
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path.");
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
start.ArgumentList.Add("--stdio-fixture");
using var child = Process.Start(start) ?? throw new InvalidOperationException("No stdio fixture process.");
var outputTask = child.StandardOutput.ReadToEndAsync(timeout.Token);
var errorTask = child.StandardError.ReadToEndAsync(timeout.Token);
try
{
    foreach (var line in inputLines) await child.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token);
    child.StandardInput.Close();
    await child.WaitForExitAsync(timeout.Token);
    var output = await outputTask;
    var errors = await errorTask;
    Check(child.ExitCode == 0 && errors.Length == 0, "real stdio fixture process completes cleanly");
    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
    Check(lines.Length == 4, "notifications do not emit responses and every request has one response");
    var response = lines.ToDictionary(x => x.GetProperty("id").GetInt32());
    Check(response[1].GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "protocol handshake");
    Check(response[2].GetProperty("result").GetProperty("structuredContent").GetProperty("state").GetString() == "uncertain", "stdio cancellation preserves uncertainty");
    Check(response[3].GetProperty("result").GetProperty("structuredContent").GetProperty("state").GetString() == "completed", "status remains responsive while mutation slot is occupied");
    Check(Array.FindIndex(lines, x => x.GetProperty("id").GetInt32() == 3) < Array.FindIndex(lines, x => x.GetProperty("id").GetInt32() == 2), "independent status reply precedes cancellation completion");
    Check(response[4].GetProperty("result").GetProperty("tools").GetArrayLength() == 3, "closed three-tool interface");
    Check(!output.Contains("fixture-secret-not-for-output"), "stdio response does not expose fixture credentials");
}
finally
{
    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
}

// Standard MCP metadata belongs to the tool-call envelope. It must not alter the
// exact closed arguments delivered to the existing operator registry.
var metadataCalls = new List<(string Operation, JsonElement Arguments)>();
using var metadataCatalog = new OperatorMcpRegistry((operation, arguments, _) =>
{
    metadataCalls.Add((operation, arguments.Clone()));
    return Task.FromResult(CommandResult.Ok());
});
async Task<JsonElement> MetadataResponse(string extraParameters)
{
    var frame = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"cerebral_call","arguments":{"operation":"models.inspect","arguments":{"id":"fixture-model"}}""" + extraParameters + "}}";
    using var metadataOutput = new StringWriter();
    var outcome = await OperatorMcpHost.RunAsync(metadataCatalog,
        new StringReader(inputLines[0] + "\n" + inputLines[1] + "\n" + frame + "\n"), metadataOutput);
    Check(outcome == 0, "metadata fixture connection completes cleanly");
    return metadataOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse)
        .Single(message => message.GetProperty("id").GetInt32() == 9);
}
var withoutMetadata = await MetadataResponse("");
var withMetadata = await MetadataResponse(""","_meta":{"progressToken":"untrusted-progress","owner":"untrusted-owner","nested":{"value":1}}""");
Check(withoutMetadata.TryGetProperty("result", out _) && withMetadata.TryGetProperty("result", out _),
    "absent and object tool-call metadata are accepted");
Check(metadataCalls.Count == 2 && metadataCalls.All(call => call.Operation == "models.inspect") &&
    metadataCalls[0].Arguments.GetRawText() == metadataCalls[1].Arguments.GetRawText() &&
    metadataCalls[1].Arguments.GetRawText() == """{"id":"fixture-model"}""",
    "untrusted metadata never changes the exact operation or registry arguments");
foreach (var invalidMetadata in new[] { "null", "[]", "\"text\"" })
{
    var rejectedMetadata = await MetadataResponse(",\"_meta\":" + invalidMetadata);
    Check(rejectedMetadata.GetProperty("error").GetProperty("code").GetInt32() == -32602 && metadataCalls.Count == 2,
        "nonobject metadata rejects without operator dispatch: " + invalidMetadata);
}
var unknownParameter = await MetadataResponse(""","_meta":{},"unknown":true""");
Check(unknownParameter.GetProperty("error").GetProperty("code").GetInt32() == -32602 && metadataCalls.Count == 2,
    "metadata admission retains closed tool-call parameters");
foreach (var duplicateMetadata in new[] { ""","_meta":{},"_meta":{}""", ""","_meta":{"x":1,"X":2}""" })
{
    var rejectedDuplicate = await MetadataResponse(duplicateMetadata);
    Check(rejectedDuplicate.GetProperty("error").GetProperty("code").GetInt32() == -32600 && metadataCalls.Count == 2,
        "duplicate envelope or nested metadata fields reject before dispatch");
}

using var invalidOutput = new StringWriter();
var malformed = await OperatorMcpHost.RunAsync(catalog, new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"method\":\"tools/call\"}\n"), invalidOutput);
Check(malformed == 0 && invalidOutput.ToString().Contains("-32600"), "duplicate envelope fields refuse before dispatch");
using var incompleteOutput = new StringWriter();
var incomplete = await OperatorMcpHost.RunAsync(catalog, new StringReader("{\"jsonrpc\":\"2.0\"}"), incompleteOutput);
Check(incomplete == 2 && incompleteOutput.ToString().Contains("framing bound"), "incomplete frame closes the connection");
using var oversizedOutput = new StringWriter();
var oversized = await OperatorMcpHost.RunAsync(catalog, new StringReader(new string('x', 512 * 1024 + 1) + "\n"), oversizedOutput);
Check(oversized == 2, "oversized frame closes before JSON/operation admission");
using var nestedOutput = new StringWriter();
var nestedInput = inputLines[0] + "\n" + inputLines[1] + "\n" + "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"cerebral_call\",\"arguments\":{\"operation\":\"health\",\"arguments\":{\"nested\":{\"a\":1,\"A\":2}}}}}\n";
before = calls.Count;
await OperatorMcpHost.RunAsync(catalog, new StringReader(nestedInput), nestedOutput);
Check(calls.Count == before && nestedOutput.ToString().Contains("-32600"), "nested duplicate JSON refuses before a handler can observe last-field-wins data");
var mutationCount = 0;
using (var duplicateCatalog = new OperatorMcpRegistry(async (_, _, token) =>
{
    Interlocked.Increment(ref mutationCount); await Task.Delay(TimeSpan.FromSeconds(30), token); return CommandResult.Ok();
}))
{
    using var duplicateOutput = new StringWriter();
    var duplicateInput = string.Join('\n', new[] { inputLines[0], inputLines[1], inputLines[2], inputLines[2], inputLines[4] }) + "\n";
    var duplicate = await OperatorMcpHost.RunAsync(duplicateCatalog, new StringReader(duplicateInput), duplicateOutput);
    Check(duplicate == 0 && mutationCount == 1 && duplicateOutput.ToString().Contains("already in flight"), "duplicate in-flight JSON-RPC identity cannot dispatch a second mutation");
    mutationCount = 0;
    using var escapedOutput = new StringWriter();
    var plainId = inputLines[2].Replace("\"id\":2", "\"id\":\"a\"");
    var escapedId = plainId.Replace("\"id\":\"a\"", "\"id\":\"\\u0061\"");
    var escapedCancel = inputLines[4].Replace("\"requestId\":2", "\"requestId\":\"a\"");
    var escapedInput = string.Join('\n', new[] { inputLines[0], inputLines[1], plainId, escapedId, escapedCancel }) + "\n";
    await OperatorMcpHost.RunAsync(duplicateCatalog, new StringReader(escapedInput), escapedOutput);
    Check(mutationCount == 1 && escapedOutput.ToString().Contains("already in flight"), "alternate JSON escapes cannot bypass in-flight request identity");
}
var originalError = Console.Error;
using var startupError = new StringWriter();
try
{
    Console.SetError(startupError);
    var invalidLaunch = await OperatorMcpHost.RunInstalledAsync(["mcp", "--operator", "--url", "http://never-used.invalid"]);
    Check(invalidLaunch == 2 && startupError.ToString().Contains("no Worker was started"), "operator mode refuses alternate HTTP launch options before opening credentials/owner transport");
}
finally { Console.SetError(originalError); }

Console.WriteLine(JsonSerializer.Serialize(new { passed, evidence = "unit/delegate boundary checks and real stdio fixture process; no installed owner, desktop, model, GPU or network execution" }));
return 0;
