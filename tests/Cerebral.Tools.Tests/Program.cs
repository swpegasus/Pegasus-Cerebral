using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

if (args.Length == 2 && args[0] == "--plugin-projects-projection-probe")
    return await PluginProjectsProjectionProbe.RunAsync(args[1]);

if (args.Length == 1 && args[0] == "--service-sdk-reference-only")
{
    var referenceRoot = Path.Combine(Path.GetTempPath(), "cerebral-service-sdk-reference-" + Guid.NewGuid().ToString("N"));
    var checks = 0;
    try
    {
        await ServiceSdkReferenceTests.RunAsync(referenceRoot, (passed, message) =>
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        });
        Console.WriteLine($"Service SDK reference checks passed ({checks}; isolated copied SDK and generated service build, no installed plugin).");
        return 0;
    }
    finally { if (Directory.Exists(referenceRoot)) Directory.Delete(referenceRoot, recursive: true); }
}

if (args.Length == 1 && args[0] == "--scoped-path-access-only")
{
    var checks = await ScopedPathAccessTests.RunAsync();
    Console.WriteLine($"Scoped path access checks passed ({checks}; isolated native files and deterministic PAO validator; no installed provider or process execution).");
    return 0;
}
if (args.Length == 1 && args[0] == "--application-authorization-only")
{
    var authorizationRoot = Path.Combine(Path.GetTempPath(), "cerebral-application-authorization-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(authorizationRoot);
    var authorizationChecks = 0;
    try
    {
        await ApplicationAuthorizationTests.RunAsync(authorizationRoot, (passed, message) =>
        {
            if (!passed) throw new InvalidOperationException(message);
            authorizationChecks++;
            Console.WriteLine("PASS: " + message);
        });
        Console.WriteLine($"Application authorization checks passed ({authorizationChecks}; production eligibility preflight and injected local-pipe fixture; no installed provider dispatch).");
        return 0;
    }
    finally { Directory.Delete(authorizationRoot, recursive: true); }
}
if (args.FirstOrDefault() == "--probe-installed-cortex") return await InstalledCortexProbe.RunAsync();
if (args.Length == 2 && args[0] == "--native-cortex-interop") return await CortexNativeInteropProbe.RunAsync(args[1]);
if (args.FirstOrDefault() == "--source-native-cortex-read")
{
    if (args.Length != 4) { Console.WriteLine("Usage: --source-native-cortex-read <absolute Source/local-pipe.json> <absolute Source root> <expected installation ID>"); return 1; }
    return await SourceNativeCortexReadProbe.RunAsync(args[1], args[2], args[3]);
}
if (args.Length == 1 && args[0] == "--pao-terminal-failures-only")
{
    var checks = PaoTerminalFailureTests.Run();
    Console.WriteLine($"PAO terminal failure checks passed ({checks}; deterministic receipt/classification fixtures; no provider dispatch).");
    return 0;
}
if (args.Length == 1 && args[0] == "--builtin-dispatch-identity-only")
{
    await BuiltinDispatchIdentityTests.RunAsync();
    Console.WriteLine("Built-in dispatch identity checks passed (late extension collision, unchanged normal result bytes; no semantic process or provider call).");
    return 0;
}
if (args.Length == 1 && args[0] == "--pao-mapping-only")
{
    var mappingRoot = Path.Combine(Path.GetTempPath(), "cerebral-pao-mapping-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(mappingRoot);
    var checks = 0;
    try
    {
        await PaoMappingChecksAsync(mappingRoot, (passed, message) =>
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
            Console.WriteLine("PASS: " + message);
        });
        Console.WriteLine($"PAO mapping checks passed ({checks}; deterministic stdio receipts and isolated native files, no installed provider).");
        return 0;
    }
    finally { Directory.Delete(mappingRoot, recursive: true); }
}
if (args.Length == 1 && args[0] == "--text-edits-only")
{
    var editRoot = Path.Combine(Path.GetTempPath(), "cerebral-tools-text-edits-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(editRoot);
    var checks = 0;
    try
    {
        await TextEditChecksAsync(editRoot, (passed, message) =>
        {
            if (!passed) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        });
        Console.WriteLine($"Text edit checks passed ({checks}; pure replacement and scoped native filesystem checks, no installed provider).");
        return 0;
    }
    finally
    {
        var full = Path.GetFullPath(editRoot);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full).StartsWith("cerebral-tools-text-edits-", StringComparison.Ordinal))
            Directory.Delete(full, recursive: true);
    }
}
if (args.Length == 1 && args[0] == "--cortex-read-policy-only")
{
    var policyChecks = await CortexNativeTransportTests.RunAsync(policyOnly: true);
    Console.WriteLine(JsonSerializer.Serialize(new { suite = "cortex-native-policy", checks = policyChecks,
        evidence = "owned Windows pipe/current-process peer/synthetic DPAPI key; deterministic policy; no installed Cortex, profile or model" }));
    return 0;
}
if (args.Length == 1 && args[0] == "--cortex-pre-discovery-only")
{
    var guardChecks = await CortexPolicyTests.RunPreDiscoveryAsync(values => FixtureArguments(values));
    Console.WriteLine(JsonSerializer.Serialize(new { suite = "cortex-pre-discovery", checks = guardChecks,
        evidence = "legacy-route guards and controlled generic MCP child-process race; no installed Cortex, profile or network" }));
    return 0;
}
if (args.Length == 1 && args[0] == "--cortex-pipe-only")
{
    var checks = await ProviderBindingTests.RunPipeOnlyAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        suite = "cortex-pipe-only", checks,
        evidence = "deterministic dispatcher guards; no process, pipe, network, credential or profile access"
    }));
    return 0;
}
if (args.Length == 1 && args[0] == "--cortex-assets-only")
{
    await CortexAssetResourceTests.RunAsync();
    return 0;
}
if (args.Length == 1 && args[0] == "--cortex-native-only")
{
    await CortexNativeTransportTests.RunAsync();
    await ProviderBindingTests.RunAsync((passed, message) => { if (!passed) throw new Exception(message); Console.WriteLine("PASS: " + message); });
    return 0;
}
if (args.Length == 1 && args[0] == "--process-admission-only")
{
    var checks = await ProcessAdmissionTests.RunAsync();
    Console.WriteLine($"Process admission checks passed ({checks}; deterministic dispatcher checks, no process launched).");
    return 0;
}
if (args.Length == 2 && args[0] == McpCancellationTests.FixtureArgument) return await McpCancellationTests.RunFixtureAsync(args[1]);
if (args.Length == 1 && args[0] == "--mcp-cancellation-only")
{
    var cancellationRoot = Path.Combine(Path.GetTempPath(), "cerebral-mcp-cancellation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cancellationRoot);
    var checks = 0;
    try
    {
        await McpCancellationTests.RunAsync(cancellationRoot, (passed, message) =>
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        });
        Console.WriteLine($"MCP cancellation checks passed ({checks}; deterministic stdio fixture, no installed provider).");
        return 0;
    }
    finally { Directory.Delete(cancellationRoot, recursive: true); }
}

if (args.FirstOrDefault() == "--fixture-output") { Console.Write(new string('x', 100_000)); Console.Error.Write(new string('e', 100_000)); return 7; }
if (args.FirstOrDefault() == "--fixture-diagnostics")
{
    Console.Write("build beginning café\r\n" + new string('w', 100_000) + "\r\nerror CS8618: final compiler diagnostic\r\n");
    Console.Error.Write("stderr beginning\r\n" + new string('e', 100_000) + "\r\nfinal stderr diagnostic\r\n");
    return 1;
}
if (args.FirstOrDefault() == "--fixture-short-output")
{
    Console.Write("unchanged café\r\nSecond line.\r\n");
    Console.Error.Write("short diagnostic\r\n");
    return 0;
}
if (args.FirstOrDefault() == "--process-output-only")
{
    var shortOutput = await ManagedProcess.RunAsync(Environment.ProcessPath!, FixtureArguments("--fixture-short-output"), Environment.CurrentDirectory, default);
    if (shortOutput.ExitCode != 0 || shortOutput.Truncated || shortOutput.Output != "unchanged café\r\nSecond line.\r\n" ||
        shortOutput.Error != "short diagnostic\r\n") throw new InvalidOperationException("Short process output must remain exact.");
    var diagnostics = await ManagedProcess.RunAsync(Environment.ProcessPath!, FixtureArguments("--fixture-diagnostics"), Environment.CurrentDirectory, default);
    if (diagnostics.ExitCode != 1 || !diagnostics.Truncated || diagnostics.Output.Length > 16_384 ||
        diagnostics.Error.Length > 16_384 || !diagnostics.Output.StartsWith("build beginning café\r\n", StringComparison.Ordinal) ||
        !diagnostics.Output.EndsWith("error CS8618: final compiler diagnostic\r\n", StringComparison.Ordinal) ||
        !diagnostics.Error.StartsWith("stderr beginning\r\n", StringComparison.Ordinal) ||
        !diagnostics.Error.EndsWith("final stderr diagnostic\r\n", StringComparison.Ordinal) ||
        !diagnostics.Output.Contains("[output omitted;", StringComparison.Ordinal) ||
        !diagnostics.Error.Contains("[output omitted;", StringComparison.Ordinal))
        throw new InvalidOperationException("Bounded process capture must preserve actual final diagnostics and exit status.");
    var captureSettings = new HostSettings
    { Executables = [new() { Id = "capture-fixture", Path = Environment.ProcessPath!, PrefixArguments = FixtureArguments() }] };
    var captureProfile = new AgentProfile
    { AllowedRoots = [Environment.CurrentDirectory], AllowedTools = ["process.run"], Capabilities = ["process.execute"] };
    await using var captureDispatcher = new ToolDispatcher(() => captureSettings);
    var envelope = await captureDispatcher.ExecuteAsync(new("process.run", CerebralJson.Element(new
    { executableId = "capture-fixture", workingDirectory = Environment.CurrentDirectory, arguments = new[] { "--fixture-diagnostics" } })),
        new(captureProfile, "capture-fixture", "capture-run", Environment.CurrentDirectory), default);
    using var captured = JsonDocument.Parse(envelope.Content);
    if (envelope.Success || !envelope.Truncated || envelope.Content.Length > 32_768 ||
        envelope.Evidence is not { Outcome: ToolExecutionOutcome.Failed, ExitCode: 1 } ||
        !captured.RootElement.GetProperty("stdout").GetString()!.EndsWith("error CS8618: final compiler diagnostic\r\n", StringComparison.Ordinal) ||
        !captured.RootElement.GetProperty("stderr").GetString()!.EndsWith("final stderr diagnostic\r\n", StringComparison.Ordinal))
        throw new InvalidOperationException("Serialized process evidence must preserve the final failures within its JSON bound.");
    Console.WriteLine("Process output checks passed (exact short streams, bounded prefix/tail capture, and real dispatcher JSON evidence; fixture processes, no installed provider).");
    return 0;
}
if (args.FirstOrDefault() == "--fixture-wait") { await Task.Delay(TimeSpan.FromMinutes(10)); return 0; }
if (args.FirstOrDefault() == "--fixture-descendant")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in FixtureArguments("--fixture-wait")) start.ArgumentList.Add(argument);
    var child = Process.Start(start)!;
    if (args.Length > 1) { await File.WriteAllTextAsync(args[1] + ".tmp", child.Id.ToString()); File.Move(args[1] + ".tmp", args[1]); }
    Console.WriteLine(child.Id); Console.Out.Flush(); await Task.Delay(TimeSpan.FromMinutes(10)); return 0;
}
if (args.FirstOrDefault() is "--fixture-mcp" or "--fixture-mcp-rpc-denied" or "--fixture-mcp-malformed" or
    "--fixture-mcp-hold-discovery" or "--fixture-mcp-hold-call-connect")
{
    async Task HoldAsync()
    {
        var marker = args[1]; var release = args[2];
        await File.WriteAllTextAsync(marker, "held");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The fixture hold was not released.");
            await Task.Delay(20);
        }
    }
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var json = JsonDocument.Parse(line); var request = json.RootElement;
        if (!request.TryGetProperty("id", out var id)) continue;
        var method = request.GetProperty("method").GetString();
        if (args[0] == "--fixture-mcp-hold-discovery" && method == "tools/list") await HoldAsync();
        if (args[0] == "--fixture-mcp-hold-call-connect" && method == "initialize")
        {
            var first = args[1] + ".first";
            try { using var claim = new FileStream(first, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(first)) { await HoldAsync(); }
        }
        if (method == "tools/call" &&
            (args[0] == "--fixture-mcp-hold-discovery" || args[0] == "--fixture-mcp-hold-call-connect"))
            await File.WriteAllTextAsync(args[3], "called");
        if (method == "initialize" && request.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString() != ApplicationVersion.Current)
            throw new InvalidOperationException("MCP client version does not match the current Pegasus Cerebral product version.");
        if (args[0] == "--fixture-mcp-rpc-denied" && method == "tools/list")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32001, message = "fixture access denied" } })); continue;
        }
        object result = method switch
        {
            "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "fixture", version = "1" } },
            "tools/list" => new { tools = new object[] {
                new { name = "echo", description = "fixture", inputSchema = new { type = "object", required = new[] { "text" }, properties = new { text = new { type = "string" } }, additionalProperties = false } },
                new { name = "pao", description = "fixture PAO denial", inputSchema = new { type = "object", properties = new { op = new { type = "string", @enum = new[] { "read_lines", "mkdir", "wr", "replace", "dotnet_build", "dotnet_test", "help", "job_stat" } }, name = new { type = "string" }, src = new { type = "string" }, dst = new { type = "string" }, cwd = new { type = "string" }, text = new { type = "string" }, old = new { type = "string" }, sha = new { type = "string" }, cfg = new { type = "string" }, enc = new { type = "string" }, rid = new { type = "string" }, jid = new { type = "string" }, line = new { type = "integer" }, count = new { type = "integer" }, tmo = new { type = "integer" }, arg = new { type = "array", items = new { type = "string" } } } } },

            }.Concat(CortexPolicyTests.FixtureOperations(args.ElementAtOrDefault(1))).ToArray() },
            "tools/call" when request.GetProperty("params").GetProperty("name").GetString() == "pao" => PaoFixtureResult(request.GetProperty("params").GetProperty("arguments"), args[0] == "--fixture-mcp-malformed" ? "invalid" : args.ElementAtOrDefault(1), args.ElementAtOrDefault(2)),
            "tools/call" when request.GetProperty("params").GetProperty("name").GetString() is "cortex_task" or "cortex_knowledge" or "cortex_finish" => CortexFixtureResult(request.GetProperty("params")),
            "tools/call" when request.GetProperty("params").GetProperty("name").GetString() == "cortex_read" =>
                CortexPolicyTests.FixtureRead(request.GetProperty("params").GetProperty("arguments")),
            "tools/call" => new { content = new[] { new { type = "text", text = request.GetProperty("params").GetProperty("arguments").GetRawText() } }, isError = false },
            _ => new { }
        };
        Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
    }
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "cerebral-tools-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var settings = new HostSettings { AccessKey = Guid.NewGuid().ToString("N"), Executables = [new() { Id = "fixture", Path = Environment.ProcessPath!, PrefixArguments = FixtureArguments() }, new() { Id = "dotnet", Path = Environment.ProcessPath!, PrefixArguments = FixtureArguments() }] };
var names = new[] { "file.read", "file.inspect", "file.list", "file.search", "file.write", "file.append", "file.edit", "file.mkdir", "process.run", "http.fetch" };
var profile = new AgentProfile { AllowedRoots = [root], AllowedTools = [.. names, "mcp.fixture.echo", "mcp.fixture.cortex_search"], Capabilities = ["filesystem.read", "filesystem.write", "process.execute", "network.http", "mcp.fixture"] };
var context = new ToolExecutionContext(profile, "session-one", "job-one", root);
await using var dispatcher = new ToolDispatcher(() => settings);
var passed = 0; var skipped = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
Task<ToolResult> Call(string name, object arguments, CancellationToken ct = default) => dispatcher.ExecuteAsync(new(name, CerebralJson.Element(arguments)), context, ct);
try
{
    if (args.Length == 1 && args[0] == "--plugin-initialization-only")
    {
        await PluginInitializationMetadataTests.RunAsync(root, Check);
        Console.WriteLine($"Plugin initialization metadata checks passed ({passed}; real compiled isolated fixtures, no plugin entry execution).");
        return 0;
    }
    if (args.Length == 1 && args[0] == "--plugin-ordinary-scope-denial-only")
    {
        await PluginScaffoldTests.RunOrdinaryScopeDenialOnlyAsync(root, Check);
        Console.WriteLine($"Plugin ordinary scope denial checks passed ({passed}; isolated native scope rejection, no protected provider call).");
        return 0;
    }
    if (args.Length == 1 && args[0] == "--plugin-source-inventory-only")
    {
        await PluginScaffoldTests.RunSourceInventoryOnlyAsync(root, Check);
        Console.WriteLine($"Plugin source inventory checks passed ({passed}; isolated source/identity fixtures, no installed plugin or inference).");
        return 0;
    }
    if (args.Length == 1 && args[0] == "--plugin-scaffold-purpose-only")
    {
        await PluginScaffoldTests.RunPurposeNamingChecksAsync(Path.Combine(root, "purpose-naming"), Check);
        Console.WriteLine($"Plugin scaffold purpose checks passed ({passed}; isolated source and real local component builds, no installed plugin).");
        return 0;
    }
    await PluginScaffoldTests.RunAsync(root, Check);
    await PluginInitializationMetadataTests.RunAsync(root, Check);
    if (args.Length == 1 && args[0] == "--plugin-scaffold-only")
    {
        Console.WriteLine($"Plugin scaffold checks passed ({passed}; isolated source/package fixtures with real dotnet builds, no installed plugin).");
        return 0;
    }
    await BuiltinSchemaTests.RunAsync(dispatcher, context, settings, Check);
    await MathCalculatorTests.RunAsync(dispatcher, Check);
    await ApplicationAuthorizationTests.RunAsync(root, Check);
    await McpCancellationTests.RunAsync(root, Check);
    await TextEditChecksAsync(root, Check);
    Check((await dispatcher.ListAsync(profile, default)).Count == names.Length, "only exact granted builtins advertised");
    var deniedProfile = new AgentProfile { AllowedRoots = [root], AllowedTools = ["file.write"], Capabilities = ["filesystem.read"] };
    Check(!(await dispatcher.ExecuteAsync(new("file.write", CerebralJson.Element(new { path = Path.Combine(root, "denied.txt"), content = "x" })), context with { Profile = deniedProfile }, default)).Success, "capability denial before write");
    Check(!(await Call("file.write", new { path = Path.Combine(root, "..", "escape.txt"), content = "x" })).Success, "parent traversal denied");
    Check(!(await Call("file.write", new { path = root + "-escape/file.txt", content = "x" })).Success, "root prefix escape denied");
    var file = Path.Combine(root, "test.txt");
    Check((await Call("file.write", new { path = file, content = "alpha\r\nbeta" })).Success, "new file write");
    Check(!(await Call("file.write", new { path = file, content = "wrong" })).Success && await File.ReadAllTextAsync(file) == "alpha\r\nbeta", "default collision preserves destination");
    var read = await Call("file.read", new { path = file });
    using var details = JsonDocument.Parse(read.Content); var hash = details.RootElement.GetProperty("sha256").GetString();
    Check(read.Success && read.Content.Contains("alpha"), "read returns text and SHA");
    Check(details.RootElement.GetProperty("content").GetString() == "alpha\r\nbeta", "complete read preserves original CRLF text");
    Check(!(await Call("file.edit", new { path = file, oldText = "alpha", newText = "wrong", expectedSha256 = "stale" })).Success, "stale preimage denied");
    Check(!(await Call("file.edit", new { path = file, oldText = "alpha", newText = "alpha", expectedSha256 = hash })).Success &&
          await File.ReadAllTextAsync(file) == "alpha\r\nbeta", "unchanged edit is rejected without rewriting the file");
    var missingEdit = await Call("file.edit", new { path = file, oldText = "missing", newText = "wrong", expectedSha256 = hash });
    Check(!missingEdit.Success && missingEdit.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
          missingEdit.Content.Contains("0 exact matches", StringComparison.Ordinal) &&
          await File.ReadAllTextAsync(file) == "alpha\r\nbeta", "missing-target edit carries a corrective rejection and preserves the actual file");
    Check((await Call("file.edit", new { path = file, oldText = "alpha", newText = "gamma", expectedSha256 = hash })).Success && await File.ReadAllTextAsync(file) == "gamma\r\nbeta", "exact edit preserves newlines");
    Check((await Call("file.search", new { path = root, text = "gamma", recursive = true })).Content.Contains("test.txt"), "literal scoped search");
    Check((await Call("file.read", new { path = file, lineCount = 1 })).Truncated, "partial line read declares truncation");
    try
    {
        var link = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(link, Path.GetTempPath());
        Check(!(await Call("file.write", new { path = Path.Combine(link, "escape.txt"), content = "x" })).Success, "symbolic link escape denied");
        Directory.Delete(link);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped++; Console.WriteLine("SKIP: symlink creation unavailable: " + ex.GetType().Name); }
    Check(!(await Call("process.run", new { executableId = "unknown", workingDirectory = root })).Success, "unregistered executable denied");
    var process = await Call("process.run", new { executableId = "fixture", workingDirectory = root, arguments = new[] { "--fixture-output" } });
    Check(!process.Success && process.Truncated && process.Content.Length <= 32768 &&
          process.Evidence is { Outcome: ToolExecutionOutcome.Failed, ExitCode: 7 }, "real process output bounded with exit status");
    using (JsonDocument.Parse(process.Content)) { Check(true, "truncated process envelope remains valid JSON"); }
    using (var cancellation = new CancellationTokenSource(300))
    {
        var watch = Stopwatch.StartNew(); var cancelled = false;
        try { await Call("process.run", new { executableId = "fixture", workingDirectory = root, arguments = new[] { "--fixture-wait" } }, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && watch.Elapsed < TimeSpan.FromSeconds(5), "real process cancellation completes promptly");
    }
    using (var cancellation = new CancellationTokenSource())
    {
        var pidPath = Path.Combine(root, "descendant.pid");
        var run = Call("process.run", new { executableId = "fixture", workingDirectory = root, arguments = new[] { "--fixture-descendant", pidPath } }, cancellation.Token);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++) await Task.Delay(30);
        Check(File.Exists(pidPath), "descendant fixture started");
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath)); cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        var alive = false; try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; } catch (ArgumentException) { }
        Check(!alive, "process cancellation kills descendant");
    }
    var wildcard = new AgentProfile { AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["filesystem.read"] };
    Check((await dispatcher.ListAsync(wildcard, default)).Count == 4, "explicit wildcard tools retains capability filtering");
    var fake = new McpRegistration { Id = "fixture", Executable = Environment.ProcessPath!, Arguments = FixtureArguments("--fixture-mcp"), Enabled = true, ViaUserConnector = false };
    settings.McpServers.Add(fake);
    Check((await dispatcher.ListAsync(profile, default)).Any(x => x.Name == "mcp.fixture.echo"), "real stdio fixture MCP discovery");
    Check((await Call("mcp.fixture.echo", new { text = "hello" })).Content.Contains("hello"), "real stdio fixture MCP tool call");
    Check(!(await Call("mcp.fixture.echo", new { unknown = "x" })).Success, "live MCP schema required field enforced");
    passed += await CortexNativeTransportTests.RunAsync(policyOnly: true);
    await ProviderBindingTests.RunAsync(Check);
    await CortexAssetResourceTests.RunAsync();
    settings.McpServers.Add(new() { Id = "pao", Executable = Environment.ProcessPath!, Arguments = FixtureArguments("--fixture-mcp"), Enabled = true, ViaUserConnector = false });
    profile.AllowedTools.Add("mcp.pao.pao"); profile.Capabilities.AddRange(["mcp.pao", "diagnostics"]);
    Check(!(await dispatcher.ListAsync(profile, default)).Any(x => x.Name == "mcp.pao.pao") &&
          (await dispatcher.ListAsync(profile, default)).Any(x => x.Name == "file.write"),
        "agent catalogs hide the raw PAO gateway while retaining Cerebral semantic tools");
    await PaoMappingChecksAsync(root, Check);
    var nestedBuildRoot = Path.Combine(root, "nested-build");
    var nestedDesktop = Path.Combine(nestedBuildRoot, "Desktop"); Directory.CreateDirectory(nestedDesktop);
    var nestedProject = Path.Combine(nestedDesktop, "Child.csproj"); await File.WriteAllTextAsync(nestedProject, "<Project />");
    var missingProject = await Call("process.run", new { executableId = "dotnet", arguments = new[] { "build", "--configuration", "Release" }, workingDirectory = nestedBuildRoot, timeoutSeconds = 30 });
    Check(!missingProject.Success && missingProject.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
          missingProject.Content.Contains(nestedProject, StringComparison.Ordinal) &&
          missingProject.Content.Contains("No build was dispatched", StringComparison.Ordinal),
        "a missing parent-directory project is rejected with its bounded child project hint and never dispatched");
    PaoTerminalFailureTests.Run(Check);
    var paoDenied = await Call("mcp.pao.pao", new { op = "help", name = "denied" });
    Check(!paoDenied.Success && paoDenied.Content.Contains("user_dispatch_required") && paoDenied.Content.Contains("fixture-job"), "direct PAO execution denial remains a failure with its reason and receipt despite successful MCP transport");
    var paoSucceeded = await Call("mcp.pao.pao", new { op = "help", name = "succeeded" });
    using (var compactReceipt = JsonDocument.Parse(paoSucceeded.Content))
        Check(paoSucceeded.Success && compactReceipt.RootElement.GetProperty("ok").GetBoolean() &&
            !compactReceipt.RootElement.TryGetProperty("structuredContent", out _) && !compactReceipt.RootElement.TryGetProperty("content", out _) &&
            !compactReceipt.RootElement.TryGetProperty("jid", out _) && !compactReceipt.RootElement.TryGetProperty("state", out _),
            "direct PAO confirmed execution returns one compact receipt without routine transport or recovery metadata");
    var paoRead = await Call("mcp.pao.pao", new { op = "read_lines", name = "read-lines", src = file, line = 3, count = 2 });
    using (var compactRead = JsonDocument.Parse(paoRead.Content))
        Check(paoRead.Success && paoRead.Truncated && compactRead.RootElement.GetProperty("path").GetString() == file &&
            compactRead.RootElement.GetProperty("startLine").GetInt32() == 3 && compactRead.RootElement.GetProperty("totalLines").GetInt32() == 9 &&
            compactRead.RootElement.GetProperty("content").GetString() == "alpha\nbeta" && !paoRead.Content.Contains("entries", StringComparison.Ordinal),
            "PAO read_lines projects numbered provider entries into one bounded text block");
    var transportDenied = await Call("mcp.pao.pao", new { op = "help", name = "transport-denied" });
    Check(!transportDenied.Success && transportDenied.Content.Contains("pao_transport_error", StringComparison.Ordinal), "PAO transport failure cannot be overridden by an affirmative nested receipt");
    Check(!(await Call("mcp.pao.pao", new { op = "help", name = "text-denied" })).Success, "legacy PAO text receipt denial is honored");
    foreach (var malformed in new[] { "transport-malformed", "transport-null" })
    {
        var outcome = await Call("mcp.pao.pao", new { op = "help", name = malformed });
        Check(!outcome.Success && outcome.Content.Contains("pao_receipt_missing"), "direct PAO malformed transport flag remains unconfirmed: " + malformed);
    }
    var missingReceipt = await Call("mcp.pao.pao", new { op = "help", name = "missing" });
    Check(!missingReceipt.Success && missingReceipt.Content.Contains("pao_receipt_missing"), "missing PAO receipt cannot confirm execution");
    Check(!(await Call("mcp.pao.pao", new { op = "help", name = "invalid" })).Success, "malformed PAO receipt cannot confirm execution");
    var providerInvalid = await Call("mcp.pao.pao", new { op = "help", name = "provider-invalid" });
    Check(!providerInvalid.Success && providerInvalid.Content.Contains("pao_arguments_invalid") && !providerInvalid.Content.Contains("structuredContent"),
        "provider validation failures project one concise error instead of the nested MCP transport envelope");
    Check(!(await Call("mcp.pao.pao", new { op = "help", name = "too-wide" })).Success, "a confirmed PAO receipt cannot mask failure to produce a bounded result envelope");
    profile.AllowedTools.Remove("mcp.pao.pao"); profile.Capabilities.Remove("mcp.pao"); profile.Capabilities.Remove("diagnostics");
    var denialPath = Path.Combine(root, "must-not-exist");
    Check(!(await Call("file.mkdir", new { path = denialPath })).Success && !Directory.Exists(denialPath), "PAO denial never falls back to native mutation");
    settings.McpServers.Single(x => x.Id == "pao").Arguments = FixtureArguments("--fixture-mcp-malformed");
    var mappedMalformed = await Call("file.mkdir", new { path = denialPath });
    Check(!mappedMalformed.Success && mappedMalformed.Content.Contains("pao_receipt_missing") && !Directory.Exists(denialPath), "mapped PAO malformed receipt reports unconfirmed outcome without native mutation");
    foreach (var malformed in new[] { "transport-malformed", "transport-null", "svc-missing", "svc-partial", "svc-success", "svc-state", "svc-op", "svc-transport" })
    {
        settings.McpServers.Single(x => x.Id == "pao").Arguments = FixtureArguments("--fixture-mcp", malformed);
        var outcome = await Call("file.mkdir", new { path = denialPath });
        Check(!outcome.Success && !Directory.Exists(denialPath), "mapped PAO malformed or contradictory receipt cannot succeed or trigger native mutation: " + malformed);
    }
    settings.McpServers.Single(x => x.Id == "pao").Arguments = FixtureArguments("--fixture-mcp", "svc-unsubmitted");
    var fallbackPath = Path.Combine(root, "confirmed-native-fallback");
    Check((await Call("file.mkdir", new { path = fallbackPath })).Success && Directory.Exists(fallbackPath), "validated PAO pre-submission unavailability allows the native equivalent");
    settings.McpServers.Single(x => x.Id == "pao").Arguments = FixtureArguments("--fixture-mcp-rpc-denied");
    Check(!(await Call("file.mkdir", new { path = denialPath })).Success && !Directory.Exists(denialPath), "PAO JSON-RPC discovery denial never falls back to native mutation");
    settings.McpServers.RemoveAll(x => x.Id == "pao");
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build(); app.MapGet("/", () => new string('x', 100_000)); await app.StartAsync();
    var url = app.Urls.Single(); var timer = Stopwatch.StartNew();
    var first = await Call("http.fetch", new { url, minimumIntervalMs = 300 }); var second = await Call("http.fetch", new { url, minimumIntervalMs = 300 });
    Check(first.Success && first.Truncated && second.Truncated && timer.ElapsedMilliseconds >= 250, "real loopback HTTP pacing and response bounds");
    await app.StopAsync();
    fake.ViaUserConnector = true; fake.Url = "http://127.0.0.1:" + FreePort() + "/";
    Check(!(await Call("mcp.fixture.echo", new { text = "x" })).Success, "disconnected user connector explicit failure");
    settings.McpServers.Add(new() { Id = "pao", Executable = Environment.ProcessPath!, Arguments = FixtureArguments("--fixture-mcp-rpc-denied"), Enabled = true, ViaUserConnector = true, Url = fake.Url });
    var configPath = Path.Combine(root, "connector.json"); var keyPath = Path.Combine(root, "connector.key");
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings, CerebralJson.Options)); await File.WriteAllTextAsync(keyPath, settings.AccessKey);
    using var connectorStop = new CancellationTokenSource();
    var connector = ToolDispatcher.RunConnectorAsync(["--settings", configPath, "--key", keyPath, "--listen", fake.Url], connectorStop.Token);
    try
    {
        var ready = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, new Uri(fake.Url).Port); ready = true; break; }
            catch (SocketException) { await Task.Delay(50); }
        }
        Check(ready, "user connector loopback startup");
        Check((await Call("mcp.fixture.echo", new { text = "connector echo" })).Content.Contains("connector echo"), "user connector routes authenticated MCP without recursion");
        fake.Arguments = FixtureArguments("--different-registered-arguments");
        var staleArguments = await Call("mcp.fixture.echo", new { text = "must not execute" });
        Check(!staleArguments.Success && staleArguments.Content.Contains("403", StringComparison.Ordinal), "connector rejects different provider argv before launching its startup provider");
        fake.Arguments = FixtureArguments("--fixture-mcp");
        var originalExecutable = fake.Executable; fake.Executable = Path.Combine(root, "different-provider.exe");
        var staleExecutable = await Call("mcp.fixture.echo", new { text = "must not execute" });
        Check(!staleExecutable.Success && staleExecutable.Content.Contains("403", StringComparison.Ordinal), "connector rejects different provider executable before launch");
        fake.Executable = originalExecutable;
        Check((await Call("mcp.fixture.echo", new { text = "restored binding" })).Success, "matching restored connector binding remains usable");
        Check(!(await Call("file.mkdir", new { path = denialPath })).Success && !Directory.Exists(denialPath), "user connector PAO JSON-RPC denial prevents native fallback");
        settings.AccessKey = "incorrect-access-key";
        Check(!(await Call("mcp.fixture.echo", new { text = "denied" })).Success, "user connector authentication enforced");
        Check(!(await Call("file.mkdir", new { path = denialPath })).Success && !Directory.Exists(denialPath), "user connector PAO authentication denial prevents native fallback");
    }
    finally { connectorStop.Cancel(); await connector; }
    Console.WriteLine($"RESULT: {passed} passed; {skipped} skipped. MCP tests use a deterministic fixture; HTTP/process/connector checks use real local runtimes.");
    return 0;
}
finally
{
    var full = Path.GetFullPath(root); var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    if (full.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Path.GetFileName(full).StartsWith("cerebral-tools-tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
}
static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
static async Task PaoMappingChecksAsync(string root, Action<bool, string> check)
{
    var mappingRoot = Path.Combine(root, "pao-mapping-files"); Directory.CreateDirectory(mappingRoot);
    var server = new McpRegistration { Id = "pao", Executable = Environment.ProcessPath!,
        Arguments = FixtureArguments("--fixture-mcp"), Enabled = true, ViaUserConnector = false };
    var settings = new HostSettings { AccessKey = Guid.NewGuid().ToString("N"), McpServers = [server],
        Executables = [new() { Id = "dotnet", Path = Environment.ProcessPath!, PrefixArguments = FixtureArguments() }] };
    var profile = new AgentProfile { AllowedRoots = [mappingRoot], AllowedTools = ["file.write", "file.edit", "process.run"],
        Capabilities = ["filesystem.write", "process.execute"] };
    var context = new ToolExecutionContext(profile, "mapping-session", "mapping-job", mappingRoot) { EffectId = "mapping-effect" };
    await using var dispatcher = new ToolDispatcher(() => settings);
    Task<ToolResult> Call(string name, object arguments) => dispatcher.ExecuteAsync(new(name, CerebralJson.Element(arguments)), context, default);
    string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    var writePath = Path.Combine(mappingRoot, "native-write.txt");
    var writeBytes = Encoding.UTF8.GetBytes("typed semantic write caf\u00e9");
    // An invalid PAO fixture cannot serve writes: create-only writes must remain native before discovery.
    server.Arguments = FixtureArguments("--fixture-mcp-malformed");
    var write = await Call("file.write", new { path = writePath, content = Encoding.UTF8.GetString(writeBytes) });
    using (var document = JsonDocument.Parse(write.Content))
        check(write.Success && document.RootElement.GetProperty("path").GetString() == writePath &&
            document.RootElement.GetProperty("sha256").GetString() == Hash(writeBytes) &&
            document.RootElement.GetProperty("bytes").GetInt32() == writeBytes.Length &&
            (await File.ReadAllBytesAsync(writePath)).SequenceEqual(writeBytes),
            "semantic create-only write keeps its native path and exact receipt with PAO configured");
    var collision = await Call("file.write", new { path = writePath, content = "must not replace" });
    check(!collision.Success && collision.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
        (await File.ReadAllBytesAsync(writePath)).SequenceEqual(writeBytes),
        "native create-only collision preserves destination bytes instead of falling through to PAO");
    var skip = await Call("file.write", new { path = writePath, content = "must not replace", collision = "skip" });
    check(skip.Success && skip.Content.Contains("\"skipped\": true", StringComparison.Ordinal) &&
        (await File.ReadAllBytesAsync(writePath)).SequenceEqual(writeBytes),
        "native skip write retains the existing destination and truthful skipped state");

    server.Arguments = FixtureArguments("--fixture-mcp");
    var editPath = Path.Combine(mappingRoot, "mapped-edit.txt"); var editBytes = Encoding.UTF8.GetBytes("alpha beta");
    await File.WriteAllBytesAsync(editPath, editBytes);
    var editBefore = File.GetLastWriteTimeUtc(editPath);
    var editArguments = new { path = editPath, oldText = "beta", newText = "gamma", expectedSha256 = Hash(editBytes) };
    var edit = await Call("file.edit", editArguments);
    using (var document = JsonDocument.Parse(edit.Content))
        check(edit.Success && document.RootElement.GetProperty("path").GetString() == editPath &&
            document.RootElement.GetProperty("sha256").GetString() == Hash(Encoding.UTF8.GetBytes("alpha gamma")) &&
            document.RootElement.GetProperty("bytes").GetInt32() == Encoding.UTF8.GetByteCount("alpha gamma") &&
            edit.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "mcp.pao", RequestId: "mapping-effect", JobId: "fixture-mapped-replace" } &&
            !document.RootElement.TryGetProperty("op", out _) && !document.RootElement.TryGetProperty("enc", out _) &&
            !document.RootElement.TryGetProperty("lines", out _) && !document.RootElement.TryGetProperty("words", out _) &&
            (await File.ReadAllBytesAsync(editPath)).SequenceEqual(editBytes) && File.GetLastWriteTimeUtc(editPath) == editBefore,
            "typed PAO edit validates UTF-8, exact operands, count and preimage then projects only returned semantic metadata");
    foreach (var incomplete in new[] { "mapping-missing-sha", "mapping-wrong-preimage" })
    {
        server.Arguments = FixtureArguments("--fixture-mcp", incomplete);
        var unknown = await Call("file.edit", editArguments);
        check(!unknown.Success && unknown.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ProviderId: "mcp.pao", JobId: "fixture-mapped-replace" } &&
            unknown.Content.Contains("pao_mutation_receipt_invalid", StringComparison.Ordinal) &&
            (await File.ReadAllBytesAsync(editPath)).SequenceEqual(editBytes) && File.GetLastWriteTimeUtc(editPath) == editBefore,
            "unproved PAO edit receipt remains unknown with no native fallback: " + incomplete);
    }

    var recoveryPath = Path.Combine(mappingRoot, "unconfirmed-write.txt");
    var receiptPath = Path.Combine(mappingRoot, "recovery-receipt.json");
    var recoveryEvidence = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", "mapping-effect", "fixture-write-job");
    object WriteReceipt(string? before) => new { ok = true, op = "wr", state = "done", rid = "mapping-effect", jid = "fixture-write-job", partial = false,
        data = new { dst = recoveryPath, sha = Hash(writeBytes), len = writeBytes.Length, enc = "utf8", bom = false, sha_before = before } };
    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(WriteReceipt(Hash(editBytes))));
    server.Arguments = FixtureArguments("--fixture-mcp", "mapping-recovery", receiptPath);
    foreach (var policy in new[] { "fail", "skip" })
    {
        var unknown = await dispatcher.ReconcileAsync(new("file.write", CerebralJson.Element(new
            { path = recoveryPath, content = "legacy request", collision = policy })), context, recoveryEvidence, default);
        check(!unknown.Success && unknown.Evidence is { Outcome: ToolExecutionOutcome.Unknown, ProviderId: "mcp.pao", RequestId: "mapping-effect", JobId: "fixture-write-job" } &&
            unknown.Content.Contains("pao_mutation_receipt_invalid", StringComparison.Ordinal) && !File.Exists(recoveryPath),
            "legacy PAO write recovery cannot prove " + policy + " semantics or replay a write");
    }
    var replace = new ToolCall("file.write", CerebralJson.Element(new
        { path = recoveryPath, content = "legacy request", collision = "replace", expectedSha256 = Hash(editBytes) }));
    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(WriteReceipt(null)));
    var missingBefore = await dispatcher.ReconcileAsync(replace, context, recoveryEvidence, default);
    check(!missingBefore.Success && missingBefore.EffectiveOutcome == ToolExecutionOutcome.Unknown && !File.Exists(recoveryPath),
        "legacy replacement recovery cannot borrow a preimage from the requested SHA");
    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(WriteReceipt(Hash(editBytes))));
    var provenReplace = await dispatcher.ReconcileAsync(replace, context, recoveryEvidence, default);
    using (var document = JsonDocument.Parse(provenReplace.Content))
        check(provenReplace.Success && document.RootElement.GetProperty("path").GetString() == recoveryPath &&
            document.RootElement.GetProperty("sha256").GetString() == Hash(writeBytes) &&
            document.RootElement.GetProperty("bytes").GetInt32() == writeBytes.Length &&
            provenReplace.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, JobId: "fixture-write-job" } && !File.Exists(recoveryPath),
            "legacy replacement recovery projects an actual matched provider preimage without replay");

    server.Arguments = FixtureArguments("--fixture-mcp");
    var unicodePath = Path.Combine(mappingRoot, "unicode-edit.txt"); await File.WriteAllTextAsync(unicodePath, "alpha beta", Encoding.Unicode);
    var unicodeBytes = await File.ReadAllBytesAsync(unicodePath);
    var unicodeEdit = await Call("file.edit", new { path = unicodePath, oldText = "beta", newText = "gamma", expectedSha256 = Hash(unicodeBytes) });
    var unicodeAfter = await File.ReadAllBytesAsync(unicodePath);
    check(unicodeEdit.Success && Encoding.Unicode.GetString(unicodeAfter, Encoding.Unicode.GetPreamble().Length, unicodeAfter.Length - Encoding.Unicode.GetPreamble().Length) == "alpha gamma" &&
        unicodeAfter.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()),
        "UTF-16 edit preserves its BOM through the native encoding-preserving path");
    var project = Path.Combine(mappingRoot, "Mapped.csproj"); await File.WriteAllTextAsync(project, "<Project />");
    var build = await Call("process.run", new { executableId = "dotnet", arguments = new[] { "build", "Mapped.csproj", "-c", "Debug" }, workingDirectory = mappingRoot, timeoutSeconds = 30 });
    check(build.Success && build.Content.Contains("dotnet_build", StringComparison.Ordinal) &&
        build.Content.Contains(project.Replace("\\", "\\\\"), StringComparison.Ordinal),
        "recognized dotnet build still maps to typed PAO with its exact project path");
}

static async Task TextEditChecksAsync(string root, Action<bool, string> check)
{
    bool Rejects(string content, string oldText, string newText, string diagnostic)
    {
        try { TextEdits.ReplaceOne(content, oldText, newText); }
        catch (ArgumentException error) { return error.Message.Contains(diagnostic, StringComparison.Ordinal); }
        return false;
    }
    check(TextEdits.ReplaceOne("alpha\r\nbeta\r\n", "alpha\nbeta", "alpha\ngamma") == "alpha\r\ngamma\r\n",
        "unique edit bridges LF input to CRLF source without changing other lines");
    check(TextEdits.ReplaceOne("alpha\nbeta\n", "alpha\r\nbeta", "alpha\r\ngamma") == "alpha\ngamma\n",
        "unique edit bridges CRLF input to LF source without changing other lines");
    check(TextEdits.ReplaceOne("alpha\nbeta\n", "alpha\nbeta", "alpha\r\nbeta") == "alpha\r\nbeta\n",
        "an exact-match edit can still make a material newline change");
    check(Rejects("alpha", "alpha", "alpha", "unchanged"), "an exact unchanged replacement is rejected");
    check(Rejects("alpha\r\nbeta\r\n", "alpha\nbeta", "alpha\r\nbeta", "unchanged"),
        "LF fallback rejects a replacement normalized to the same CRLF text");
    check(Rejects("alpha\nbeta\n", "alpha\r\nbeta", "alpha\nbeta", "unchanged"),
        "CRLF fallback rejects a replacement normalized to the same LF text");
    check(Rejects("a\r\nb\r\na\r\nb", "a\nb", "changed", "multiple exact matches"),
        "newline normalization does not weaken unique-match requirement");
    check(Rejects("a\r\nb\r\na\r\nb", "a\nb", "a\r\nb", "multiple exact matches"),
        "an ambiguous normalized unchanged replacement retains the unique-match rejection");
    check(Rejects("alpha", "missing", "changed", "0 exact matches"),
        "an absent edit target is distinguished from an ambiguous target");
    check(Rejects("alpha\r\nbeta\r\n", "missing\ntext", "missing\r\ntext", "0 exact matches"),
        "an absent normalized unchanged replacement retains the absent-target rejection");
    check(Rejects("alpha\r\nbeta\ngamma", "alpha\nbeta", "changed", "0 exact matches"),
        "newline fallback does not fuzz mixed line endings");

    var path = Path.Combine(root, "text-edit-fallback.txt");
    var originalBytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n");
    await File.WriteAllBytesAsync(path, originalBytes);
    File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    var originalWriteTime = File.GetLastWriteTimeUtc(path);
    var hash = Convert.ToHexString(SHA256.HashData(originalBytes));
    var profile = new AgentProfile { AllowedRoots = [root], AllowedTools = ["file.edit"], Capabilities = ["filesystem.write"] };
    var context = new ToolExecutionContext(profile, "text-edit-session", "text-edit-job", root);
    await using var dispatcher = new ToolDispatcher(() => new HostSettings());
    Task<ToolResult> Edit(string oldText, string newText, string preimage) => dispatcher.ExecuteAsync(
        new("file.edit", CerebralJson.Element(new { path, oldText, newText, expectedSha256 = preimage })), context, default);
    var rejected = await Edit("alpha\nbeta", "alpha\r\nbeta", hash);
    check(!rejected.Success && rejected.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
        rejected.Content.Contains("unchanged", StringComparison.Ordinal),
        "native file.edit rejects the normalized unchanged replacement before delivery");
    check((await File.ReadAllBytesAsync(path)).SequenceEqual(originalBytes) && File.GetLastWriteTimeUtc(path) == originalWriteTime,
        "normalized unchanged native edit preserves exact bytes and last-write time");
    var stale = await Edit("alpha\nbeta", "alpha\ngamma", new string('0', 64));
    check(!stale.Success && stale.Content.Contains("Preimage hash changed", StringComparison.Ordinal) &&
        (await File.ReadAllBytesAsync(path)).SequenceEqual(originalBytes) && File.GetLastWriteTimeUtc(path) == originalWriteTime,
        "newline fallback does not bypass native preimage verification");
    var changed = await Edit("alpha\nbeta", "alpha\ngamma", hash);
    check(changed.Success && changed.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
        await File.ReadAllTextAsync(path) == "alpha\r\ngamma\r\n", "a real normalized native edit still succeeds and preserves CRLF");
}
static List<string> FixtureArguments(params string[] arguments)
{
    // Environment.ProcessPath identifies the host when the suite runs as `dotnet Tests.dll`.
    // Relaunch the entry assembly explicitly in that case; an apphost executable needs no prefix.
    return string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
        ? [System.Reflection.Assembly.GetExecutingAssembly().Location, .. arguments] : [.. arguments];
}
static JsonElement Echo(ToolResult result) { using var envelope = JsonDocument.Parse(result.Content); using var echo = JsonDocument.Parse(envelope.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!); return echo.RootElement.Clone(); }

static object PaoFixtureResult(JsonElement arguments, string? forcedName = null, string? recoveryReceiptPath = null)
{
    var name = forcedName ?? (arguments.TryGetProperty("name", out var value) ? value.GetString() : "denied");
    if (name == "provider-invalid") return new { isError = true, content = new[] { new { type = "text", text = "{\"error\":\"pao_arguments_invalid\",\"message\":\"Fixture rejected an unknown PAO field.\"}" } } };
    if (forcedName == "mapping-recovery")
    {
        if (arguments.GetProperty("op").GetString() != "job_stat" || arguments.GetProperty("jid").GetString() != "fixture-write-job")
            throw new InvalidOperationException("Recovery fixture permits only the exact existing job status lookup.");
        using var original = JsonDocument.Parse(File.ReadAllText(recoveryReceiptPath!));
        return new { isError = false, structuredContent = new { ok = true, op = "job_stat", state = "done",
            data = new { rec = original.RootElement.Clone() } }, content = Array.Empty<object>() };
    }
    if ((forcedName is null || forcedName is "mapping-missing-sha" or "mapping-wrong-preimage") &&
        !arguments.TryGetProperty("name", out _) && arguments.TryGetProperty("op", out var typedOperation) &&
        typedOperation.GetString() is "wr" or "replace" or "dotnet_build" or "dotnet_test")
    {
        if (typedOperation.GetString() is "wr" or "replace")
        {
            // This provider is read-only. It returns deterministic publication metadata after checking
            // the actual typed inputs; local destination bytes deliberately remain unchanged.
            var operation = typedOperation.GetString()!;
            var destination = arguments.GetProperty("dst").GetString()!;
            var before = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
            var published = arguments.GetProperty("text").GetString()!;
            if (arguments.GetProperty("enc").GetString() != "utf-8") throw new InvalidOperationException("Expected explicit UTF-8 mapping.");
            if (operation == "replace")
            {
                if (arguments.GetProperty("old").GetString() != "beta" || published != "gamma" ||
                    arguments.GetProperty("count").GetInt32() != 1 || before is null ||
                    arguments.GetProperty("sha").GetString() != Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant())
                    throw new InvalidOperationException("Mapped edit did not preserve exact operands, count and preimage.");
                published = Encoding.UTF8.GetString(before).Replace("beta", "gamma", StringComparison.Ordinal);
            }
            var bytes = Encoding.UTF8.GetBytes(published);
            var data = new Dictionary<string, object?> { ["dst"] = destination,
                ["sha"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["len"] = bytes.Length,
                ["enc"] = "utf8", ["bom"] = false,
                ["sha_before"] = before is null ? null : Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant() };
            if (operation == "replace") data["count"] = 1;
            if (forcedName == "mapping-missing-sha") data.Remove("sha");
            if (forcedName == "mapping-wrong-preimage") data["sha_before"] = new string('c', 64);
            return new { isError = false, structuredContent = new { ok = true, state = "done", op = operation,
                jid = "fixture-mapped-" + operation, rid = arguments.TryGetProperty("rid", out var request) ? request.GetString() : null,
                partial = false, data }, content = Array.Empty<object>() };
        }
        var mappedReceipt = new { ok = true, state = "done", op = typedOperation.GetString(),
            jid = "fixture-mapped-" + typedOperation.GetString(), rid = arguments.TryGetProperty("rid", out var mappedRequest) ? mappedRequest.GetString() : null,
            partial = false, data = JsonSerializer.Deserialize<object>(arguments.GetRawText()) };
        return new { isError = false, structuredContent = mappedReceipt, content = Array.Empty<object>() };
    }
    if (name == "read-lines")
    {
        var readReceipt = new
        {
            ok = true, jid = "fixture-read", state = "done", op = "read_lines", partial = false,
            data = new { src = arguments.GetProperty("src").GetString(), line = 3, count = 2, total = 9, next = 5,
                entries = new[] { new { line = 3, text = "alpha", truncated = false }, new { line = 4, text = "beta", truncated = false } },
                truncated = false, has_more = true, sha = "fixture-hash" }
        };
        return new { isError = false, structuredContent = readReceipt, content = new[] { new { type = "text", text = JsonSerializer.Serialize(readReceipt) } } };
    }
    if (name == "too-wide") return new { isError = false, structuredContent = new { ok = true, state = "done", partial = false, data = Enumerable.Range(0, 4000).ToDictionary(x => "item" + x, x => x) }, content = Array.Empty<object>() };
    if (name == "missing") return new { isError = false, content = new[] { new { type = "text", text = "Fixture transport delivered without execution evidence." } } };
    if (name == "invalid") return new { isError = false, structuredContent = "not a receipt", content = Array.Empty<object>() };
    if (name is "transport-malformed" or "transport-null")
        return new { isError = name == "transport-null" ? null : "true", structuredContent = new { ok = true }, content = Array.Empty<object>() };
    if (name?.StartsWith("svc-", StringComparison.Ordinal) == true)
    {
        var unavailable = new Dictionary<string, object?> { ["ok"] = false, ["state"] = "failed", ["err"] = "svc_down", ["partial"] = false, ["op"] = arguments.GetProperty("op").GetString() };
        if (name == "svc-missing") unavailable.Remove("ok");
        if (name == "svc-partial") unavailable["partial"] = true;
        if (name == "svc-success") unavailable["ok"] = true;
        if (name == "svc-state") unavailable["state"] = "attention";
        if (name == "svc-op") unavailable["op"] = "run";
        return new Dictionary<string, object?> { ["isError"] = name == "svc-transport" ? "true" : true, ["structuredContent"] = unavailable, ["content"] = Array.Empty<object>() };
    }
    var receipt = new { ok = name is "succeeded" or "transport-denied", state = name is "succeeded" or "transport-denied" ? "done" : "failed", err = name is "succeeded" or "transport-denied" ? null : "user_dispatch_required", jid = "fixture-job", partial = false };
    if (name == "text-denied") return new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(receipt) } } };
    return new { isError = name == "transport-denied", structuredContent = receipt, content = Array.Empty<object>() };
}

static object CortexFixtureResult(JsonElement parameters) =>
    CortexPolicyTests.FixtureCall(parameters.GetProperty("name").GetString()!, parameters.GetProperty("arguments"));
