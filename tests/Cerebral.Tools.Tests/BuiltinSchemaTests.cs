using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

internal static class BuiltinSchemaTests
{
    public static async Task RunAsync(ToolDispatcher dispatcher, ToolExecutionContext context, HostSettings settings, Action<bool, string> check)
    {
        var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(context.Profile));
        profile.AllowedTools = ["*"];
        profile.Capabilities.AddRange(["git.read", "git.write", "shared.read", "shared.write"]);
        var descriptors = (await dispatcher.ListAsync(profile, default)).ToDictionary(x => x.Name);
        JsonElement Field(string tool, string field) => descriptors[tool].InputSchema.GetProperty("properties").GetProperty(field);
        bool Bounds(string tool, string field, int minimum, int maximum, int value)
        {
            var rule = Field(tool, field);
            return rule.GetProperty("type").GetString() == "integer"
                && rule.GetProperty("minimum").GetInt32() == minimum
                && rule.GetProperty("maximum").GetInt32() == maximum
                && rule.GetProperty("default").GetInt32() == value;
        }
        Task<ToolResult> Call(string name, object arguments) => dispatcher.ExecuteAsync(new(name, CerebralJson.Element(arguments)), context with { Profile = profile }, default);
        var process = descriptors["process.run"];
        check(process.Description.Contains("-File", StringComparison.Ordinal) && process.Description.Contains("-Command", StringComparison.Ordinal)
            && process.Description.Contains("--project", StringComparison.Ordinal)
            && process.Description.Contains("existing file", StringComparison.Ordinal) && process.Description.Contains("file.write", StringComparison.Ordinal)
            && process.InputSchema.GetProperty("required").EnumerateArray().Any(x => x.GetString() == "arguments")
            && Field("process.run", "arguments").GetProperty("items").GetProperty("type").GetString() == "string"
            && Field("process.run", "arguments").GetProperty("maxItems").GetInt32() == 256, "process schema requires explicit bounded argv and teaches .NET and script-file workflows");
        check(descriptors["file.write"].Description.Contains("file.read", StringComparison.Ordinal)
            && descriptors["file.edit"].Description.Contains("file.read", StringComparison.Ordinal)
            && descriptors["file.append"].Description.Contains("file.inspect", StringComparison.Ordinal)
            && descriptors["file.write"].Description.Contains("expectedSha256", StringComparison.Ordinal)
            && Field("file.write", "collision").GetProperty("default").GetString() == "fail"
            && Field("file.write", "collision").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["fail", "skip", "replace"])
            && descriptors["file.edit"].InputSchema.GetProperty("required").EnumerateArray().Any(x => x.GetString() == "expectedSha256")
            && descriptors["file.append"].InputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet().IsSupersetOf(["expectedSha256", "expectedBytes"])
            && Field("file.edit", "oldText").GetProperty("minLength").GetInt32() == 1, "file schemas teach read-hash-replace and exact-edit contracts");
        check(descriptors["shared.write"].Description.Contains("shared.read", StringComparison.Ordinal)
            && descriptors["shared.write"].Description.Contains("shared.mkdir", StringComparison.Ordinal)
            && descriptors.ContainsKey("shared.inspect") && descriptors.ContainsKey("shared.append")
            && !descriptors["shared.write"].Description.Contains("file.read", StringComparison.Ordinal), "shared descriptors retain the shared read, append, and mkdir workflow");
        check(Bounds("file.read", "startLine", 1, 1_000_000, 1) && Bounds("file.read", "lineCount", 1, 1000, 200)
            && Bounds("file.list", "limit", 1, 500, 100) && Bounds("file.search", "limit", 1, 500, 100)
            && !Field("file.search", "recursive").GetProperty("default").GetBoolean()
            && !Field("file.search", "ignoreCase").GetProperty("default").GetBoolean(), "file numeric bounds and search defaults are advertised");
        check(new[] { "process.run", "git.status", "git.diff", "git.run", "http.fetch" }.All(x => Bounds(x, "timeoutSeconds", 1, 600, 30))
            && Bounds("http.fetch", "minimumIntervalMs", 250, 60_000, 1000), "execution timeout and HTTP pacing schema match handler limits");

        var path = Path.Combine(context.WorkingDirectory!, "schema-hash-workflow.txt");
        check((await Call("file.write", new { path, content = "before" })).Success, "schema workflow creates with default collision policy");
        var initial = await Call("file.read", new { path });
        using var initialJson = JsonDocument.Parse(initial.Content);
        var initialHash = initialJson.RootElement.GetProperty("sha256").GetString();
        var missingHash = await Call("file.write", new { path, content = "wrong", collision = "replace" });
        check(!missingHash.Success && await File.ReadAllTextAsync(path) == "before", "replacement without read hash still fails without changing file");
        check((await Call("file.write", new { path, content = "after", collision = "replace", expectedSha256 = initialHash })).Success, "schema read-hash-replace workflow succeeds");
        var current = await Call("file.read", new { path });
        using var currentJson = JsonDocument.Parse(current.Content);
        var currentHash = currentJson.RootElement.GetProperty("sha256").GetString();
        check((await Call("file.edit", new { path, oldText = "after", newText = "finished", expectedSha256 = currentHash })).Success
            && !(await Call("file.write", new { path, content = "stale", collision = "replace", expectedSha256 = initialHash })).Success
            && await File.ReadAllTextAsync(path) == "finished", "schema read-hash-edit workflow succeeds and stale replacement stays denied");
        var inspected = await Call("file.inspect", new { path });
        using var inspectedJson = JsonDocument.Parse(inspected.Content);
        var inspectedHash = inspectedJson.RootElement.GetProperty("sha256").GetString();
        var inspectedBytes = inspectedJson.RootElement.GetProperty("bytes").GetInt32();
        check(inspected.Success && inspectedJson.RootElement.GetProperty("words").GetInt32() == 1
            && !inspectedJson.RootElement.TryGetProperty("content", out _), "file inspection returns compact progress without file content");
        check(!(await Call("file.append", new { path, content = " wrong", expectedSha256 = initialHash, expectedBytes = inspectedBytes })).Success
            && await File.ReadAllTextAsync(path) == "finished", "append rejects a stale preimage without changing the artifact");
        var appended = await Call("file.append", new { path, content = "\r\nnext section words", expectedSha256 = inspectedHash, expectedBytes = inspectedBytes });
        using var appendedJson = JsonDocument.Parse(appended.Content);
        check(appended.Success && appendedJson.RootElement.GetProperty("words").GetInt32() == 4
            && appendedJson.RootElement.GetProperty("lines").GetInt32() == 2
            && await File.ReadAllTextAsync(path) == "finished\r\nnext section words", "hash-checked append reports cumulative long-form progress");
        var replay = await Call("file.append", new { path, content = "\r\nnext section words", expectedSha256 = inspectedHash, expectedBytes = inspectedBytes });
        using var replayJson = JsonDocument.Parse(replay.Content);
        check(replay.Success && replayJson.RootElement.GetProperty("alreadyApplied").GetBoolean()
            && await File.ReadAllTextAsync(path) == "finished\r\nnext section words", "exact append replay is idempotent and does not duplicate text");

        foreach (var (label, encoding) in new (string Label, Encoding Encoding)[]
        {
            ("utf8-bom", new UTF8Encoding(true, true)),
            ("utf16-le", new UnicodeEncoding(false, true, true)),
            ("utf16-be", new UnicodeEncoding(true, true, true))
        })
        {
            var encodedPath = Path.Combine(context.WorkingDirectory!, $"schema-append-{label}.txt");
            const string initialText = "héllo";
            const string addition = "\r\nβeta two";
            var initialBytes = encoding.GetPreamble().Concat(encoding.GetBytes(initialText)).ToArray();
            var expectedBytes = initialBytes.Concat(encoding.GetBytes(addition)).ToArray();
            await File.WriteAllBytesAsync(encodedPath, initialBytes);
            var encodedInspection = await Call("file.inspect", new { path = encodedPath });
            using var encodedInspectionJson = JsonDocument.Parse(encodedInspection.Content);
            var encodedHash = encodedInspectionJson.RootElement.GetProperty("sha256").GetString();
            var encodedLength = encodedInspectionJson.RootElement.GetProperty("bytes").GetInt32();
            var encodedAppend = await Call("file.append", new { path = encodedPath, content = addition, expectedSha256 = encodedHash, expectedBytes = encodedLength });
            using var encodedAppendJson = JsonDocument.Parse(encodedAppend.Content);
            check(encodedInspection.Success && encodedLength == initialBytes.Length && encodedAppend.Success
                && encodedAppendJson.RootElement.GetProperty("bytes").GetInt32() == expectedBytes.Length
                && encodedAppendJson.RootElement.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant()
                && (await File.ReadAllBytesAsync(encodedPath)).SequenceEqual(expectedBytes), $"{label} append preserves its single BOM and reports physical bytes");
            var encodedReplay = await Call("file.append", new { path = encodedPath, content = addition, expectedSha256 = encodedHash, expectedBytes = encodedLength });
            using var encodedReplayJson = JsonDocument.Parse(encodedReplay.Content);
            check(encodedReplay.Success && encodedReplayJson.RootElement.GetProperty("alreadyApplied").GetBoolean()
                && (await File.ReadAllBytesAsync(encodedPath)).SequenceEqual(expectedBytes), $"{label} exact replay recognizes only the encoded suffix without adding another BOM");

            var changedBytes = expectedBytes.Concat(encoding.GetBytes(" changed")).ToArray();
            await File.WriteAllBytesAsync(encodedPath, changedBytes);
            check(!(await Call("file.append", new { path = encodedPath, content = addition, expectedSha256 = encodedHash, expectedBytes = encodedLength })).Success
                && (await File.ReadAllBytesAsync(encodedPath)).SequenceEqual(changedBytes), $"{label} replay rejects a later mutation and leaves every byte unchanged");
        }

        if (OperatingSystem.IsWindows())
        {
            var lockedPath = Path.Combine(context.WorkingDirectory!, "schema-append-locked.txt");
            await File.WriteAllTextAsync(lockedPath, "locked original", new UTF8Encoding(false, true));
            var lockedInspection = await Call("file.inspect", new { path = lockedPath });
            using var lockedInspectionJson = JsonDocument.Parse(lockedInspection.Content);
            var lockedHash = lockedInspectionJson.RootElement.GetProperty("sha256").GetString();
            var lockedBytes = lockedInspectionJson.RootElement.GetProperty("bytes").GetInt32();
            await using var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var lockedAppend = await Call("file.append", new { path = lockedPath, content = " appended", expectedSha256 = lockedHash, expectedBytes = lockedBytes });
            check(!lockedAppend.Success && await File.ReadAllTextAsync(lockedPath) == "locked original"
                && !Directory.EnumerateFiles(context.WorkingDirectory!, ".cerebral-*.tmp").Any(), "failed atomic append replacement preserves the original and removes its temporary file");
        }

        var savedSharedDirectory = settings.SharedDirectory;
        var savedSharedWorker = settings.SharedFilesWorkerId;
        try
        {
            settings.SharedDirectory = Path.Combine(context.WorkingDirectory!, "shared-schema");
            settings.SharedFilesWorkerId = "local";
            var sharedWrite = await Call("shared.write", new { path = "journal.txt", content = "shared start" });
            var sharedInspect = await Call("shared.inspect", new { path = "journal.txt" });
            using var sharedInspectJson = JsonDocument.Parse(sharedInspect.Content);
            var sharedHash = sharedInspectJson.RootElement.GetProperty("sha256").GetString();
            var sharedBytes = sharedInspectJson.RootElement.GetProperty("bytes").GetInt32();
            var sharedAppend = await Call("shared.append", new { path = "journal.txt", content = "\ncontinued", expectedSha256 = sharedHash, expectedBytes = sharedBytes });
            using var sharedAppendJson = JsonDocument.Parse(sharedAppend.Content);
            check(sharedWrite.Success && sharedInspect.Success && sharedAppend.Success
                && sharedAppendJson.RootElement.GetProperty("path").GetString() == "journal.txt"
                && await File.ReadAllTextAsync(Path.Combine(settings.SharedDirectory, "journal.txt")) == "shared start\ncontinued", "local shared inspect and append stay relative to the configured shared folder");
            check(!(await Call("shared.inspect", new { path = "../schema-hash-workflow.txt" })).Success
                && !(await Call("shared.append", new { path, content = "escape", expectedSha256 = sharedHash, expectedBytes = sharedBytes })).Success,
                "shared inspect and append reject traversal and absolute local paths");
        }
        finally
        {
            settings.SharedDirectory = savedSharedDirectory;
            settings.SharedFilesWorkerId = savedSharedWorker;
        }

        var invalidRead = new[]
        {
            new { path, startLine = 0, lineCount = 1 }, new { path, startLine = 1_000_001, lineCount = 1 },
            new { path, startLine = 1, lineCount = 0 }, new { path, startLine = 1, lineCount = 1001 }
        };
        check((await Call("file.read", new { path, startLine = 1, lineCount = 1000 })).Success
            && (await Task.WhenAll(invalidRead.Select(x => Call("file.read", x)))).All(x => !x.Success), "advertised read boundary succeeds and out-of-range values fail");
        check((await Call("file.list", new { path = context.WorkingDirectory, limit = 500 })).Success
            && (await Call("file.search", new { path, text = "finished", limit = 500 })).Success
            && !(await Call("file.list", new { path = context.WorkingDirectory, limit = 501 })).Success
            && !(await Call("file.search", new { path, text = "finished", limit = 0 })).Success, "advertised list/search boundaries agree with execution");
        var invalidTimeouts = new List<ToolResult>();
        foreach (var tool in new[] { "process.run", "git.status", "git.diff", "git.run", "http.fetch" })
        {
            foreach (var value in new[] { 0, 601 })
                invalidTimeouts.Add(await Call(tool, new Dictionary<string, object?> { ["path"] = context.WorkingDirectory, ["workingDirectory"] = context.WorkingDirectory, ["executableId"] = "missing", ["arguments"] = Array.Empty<string>(), ["url"] = "http://127.0.0.1:1/", ["timeoutSeconds"] = value }
                    .Where(x => descriptors[tool].InputSchema.GetProperty("properties").TryGetProperty(x.Key, out _)).ToDictionary(x => x.Key, x => x.Value)));
        }
        check(invalidTimeouts.All(x => !x.Success && x.Content.Contains("between 1 and 600", StringComparison.Ordinal)), "all advertised execution timeout boundaries are enforced before dispatch");
        check(!(await Call("http.fetch", new { url = "http://127.0.0.1:1/", minimumIntervalMs = 249 })).Success
            && !(await Call("http.fetch", new { url = "http://127.0.0.1:1/", minimumIntervalMs = 60_001 })).Success, "HTTP pacing values outside the advertised bounds are rejected before networking");

        var script = Path.Combine(context.WorkingDirectory!, "schema-powershell.ps1");
        check((await Call("file.write", new { path = script, content = "param([string]$Value)\nWrite-Output $Value\n" })).Success, "script-file workflow creates an explicit scoped script");
        var registration = new ExecutableRegistration { Id = "schema-powershell", Path = Path.Combine(context.WorkingDirectory!, "pwsh.exe") };
        settings.Executables.Add(registration);
        try
        {
            var badArguments = new[]
            {
                new[] { "-Command", "Write-Output unsafe" },
                new[] { "-Command", "Write-Output unsafe", "-File", script },
                new[] { "-EncodedCommand", "eAA=", "-File", script },
                new[] { "-c", "Write-Output unsafe", "-File", script },
                new[] { "-File", context.WorkingDirectory! },
                new[] { "-File", Path.Combine(context.WorkingDirectory!, "missing.ps1") },
                new[] { "-File", Path.Combine(context.WorkingDirectory!, "..", "outside.ps1") }
            };
            var denied = await Task.WhenAll(badArguments.Select(arguments => Call("process.run", new { executableId = registration.Id, workingDirectory = context.WorkingDirectory, arguments })));
            string[] expectedErrors = ["requires -File", "only startup options", "only startup options", "only startup options", "not a directory", "does not exist", "Parent traversal"];
            check(denied.Select((result, index) => !result.Success && result.Content.Contains(expectedErrors[index], StringComparison.Ordinal)).All(x => x), "PowerShell preflight rejects command modes, directories, missing scripts and root escapes");
            var installedPwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (OperatingSystem.IsWindows() && File.Exists(installedPwsh))
            {
                registration.Path = installedPwsh;
                var result = await Call("process.run", new { executableId = registration.Id, workingDirectory = context.WorkingDirectory, arguments = new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Value", "script-file-ok" } });
                check(result.Success && result.Content.Contains("script-file-ok", StringComparison.Ordinal), "real PowerShell executes the advertised scoped -File workflow");
            }
        }
        finally { settings.Executables.Remove(registration); }
    }
}
