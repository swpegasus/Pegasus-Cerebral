using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker;

public static class ExecutionReliability
{
    public static string ActionForContext(JsonElement action)
    {
        var node = JsonNode.Parse(action.GetRawText())?.AsObject() ?? throw new InvalidDataException("Tool action must be a JSON object.");
        if (node["action"]?.GetValue<string>() != "tool" || node["arguments"] is not JsonObject arguments) return node.ToJsonString(CerebralJson.Options);
        RedactDispatchedText(node["name"]?.GetValue<string>(), arguments);
        return node.ToJsonString(CerebralJson.Options);
    }

    public static JsonElement ArgumentsForActivity(string name, JsonElement arguments)
    {
        var node = JsonNode.Parse(arguments.GetRawText())?.AsObject() ?? throw new InvalidDataException("Tool arguments must be a JSON object.");
        RedactDispatchedText(name, node);
        return CerebralJson.Element(node);
    }

    private static void RedactDispatchedText(string? name, JsonObject arguments)
    {
        if (name is "file.write" or "file.append" or "file.edit")
        {
            foreach (var field in new[] { "content", "newText", "oldText" })
            {
                if (arguments[field] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                arguments[field] = $"[dispatched text omitted from active context: {text.Length:N0} characters]";
            }
        }
    }

    public static string Fingerprint(string name, JsonElement arguments)
    {
        static object? Canonical(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Name, x => Canonical(x.Value)),
            JsonValueKind.Array => value.EnumerateArray().Select(Canonical).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.Clone(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + JsonSerializer.Serialize(Canonical(arguments))))).ToLowerInvariant();
    }

    public static ExecutionEvidence FromTool(string name, JsonElement arguments, ToolResult result, int sequence)
    {
        var target = arguments.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        var executable = arguments.TryGetProperty("executableId", out var exe) && exe.ValueKind == JsonValueKind.String ? exe.GetString() : null;
        var cwd = arguments.TryGetProperty("workingDirectory", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null;
        var command = JsonSerializer.Serialize(new { fingerprint = Fingerprint(name, arguments), target = TargetIdentity(arguments), path = target, executableId = executable, workingDirectory = cwd,
            arguments = arguments.TryGetProperty("arguments", out var argv) ? Clip(argv.GetRawText(), 600) : null });
        int? exitCode = null;
        try
        {
            using var json = JsonDocument.Parse(result.Content);
            if (json.RootElement.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var value)) exitCode = value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return new() { Kind = "tool", Name = name, Success = result.Success && (exitCode is null or 0), ExitCode = exitCode,
            MessageSequence = sequence, Summary = Diagnostic(result.Content, 2000), Command = command.Length <= 2000 ? command : JsonSerializer.Serialize(new { fingerprint = Fingerprint(name, arguments) }) };
    }

    public static string Diagnostic(string content, int maximum = 2000)
    {
        var jsonStart = content.StartsWith("Tool ", StringComparison.Ordinal) ? content.IndexOf('\n') + 1 : 0;
        try
        {
            using var parsed = JsonDocument.Parse(content[jsonStart..]);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                (parsed.RootElement.TryGetProperty("stdout", out _) || parsed.RootElement.TryGetProperty("stderr", out _)))
            {
                var parts = new List<string>();
                if (parsed.RootElement.TryGetProperty("exitCode", out var exit)) parts.Add("exitCode=" + exit.GetRawText());
                foreach (var field in new[] { "stdout", "stderr" })
                    if (parsed.RootElement.TryGetProperty(field, out var output) && output.ValueKind == JsonValueKind.String) parts.Add(field + ":\n" + output.GetString());
                content = string.Join("\n", parts);
            }
        }
        catch (JsonException) { }
        var lines = content.Split('\n');
        var decisive = lines.Where(x => x.Contains("error", StringComparison.OrdinalIgnoreCase) || x.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("exception", StringComparison.OrdinalIgnoreCase) || x.Contains("failed", StringComparison.OrdinalIgnoreCase) || x.Contains("exitCode", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
        if (decisive.Count == 0) return Clip(content, maximum);
        return Clip(string.Join("\n", decisive.Concat(lines.TakeLast(4)).Distinct(StringComparer.Ordinal)), maximum);
    }
    public static string Clip(string value, int maximum)
    {
        const string suffix = "\n[Source excerpt limited]";
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        return value.Length <= maximum ? value : maximum < suffix.Length ? value[..maximum] : value[..(maximum - suffix.Length)] + suffix;
    }

    public static int IdenticalFailureCount(IEnumerable<ExecutionEvidence> evidence, string name, JsonElement arguments)
    {
        var fingerprint = Fingerprint(name, arguments); var failures = 0;
        foreach (var item in evidence.Where(x => x.Kind == "tool").OrderByDescending(x => x.At))
        {
            // The same build command is a new attempt after related source changes.
            if (item.Success && IsRelevantMutation(item, name, arguments)) break;
            if (item.Name != name || Field(item.Command, "fingerprint") != fingerprint) continue;
            if (item.Success) break;
            if (item.Resolution is null) failures++;
        }
        return failures;
    }

    public static int ExecutableAttemptCount(IEnumerable<ExecutionEvidence> evidence, string name, JsonElement arguments)
    {
        if (name != "process.run" || !arguments.TryGetProperty("executableId", out var executable) || executable.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("workingDirectory", out var directory) || directory.ValueKind != JsonValueKind.String) return 0;
        var executableId = executable.GetString();
        if (IsDevelopmentOperation(executableId, arguments)) return 0;
        var workingDirectory = directory.GetString();
        return evidence.Count(item => item.Kind == "tool" && item.Name == "process.run" &&
            string.Equals(Field(item.Command, "executableId"), executableId, StringComparison.Ordinal) &&
            EqualPath(Field(item.Command, "workingDirectory"), workingDirectory));
    }

    /// <summary>
    /// Small local models sometimes put a freshly built application filename in process.run's
    /// registered executable-ID field. A visible workspace application belongs to the interface
    /// presentation path, because the service must not start an interactive process in its session.
    /// This narrow adapter accepts only an existing Windows executable with no arguments and leaves
    /// registered tools, command-line applications, and missing files on the ordinary tool path.
    /// </summary>
    public static string? WorkspaceApplicationForPresentation(string name, JsonElement arguments,
        IEnumerable<ExecutableRegistration> registrations)
    {
        if (!OperatingSystem.IsWindows() || name != "process.run" ||
            !arguments.TryGetProperty("executableId", out var executable) || executable.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("workingDirectory", out var directory) || directory.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 0)
            return null;
        var value = executable.GetString(); var workingDirectory = directory.GetString();
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(workingDirectory) ||
            registrations.Any(item => item.Id.Equals(value, StringComparison.Ordinal)) ||
            !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(workingDirectory)) return null;
        try
        {
            var path = Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(workingDirectory, value));
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    internal static bool IsDevelopmentOperation(string? executableId, JsonElement arguments)
    {
        if (!string.Equals(executableId, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            !arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array) return false;
        var verb = values.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
            ? values.EnumerateArray().First().GetString() : null;
        return verb is not null && new[] { "build", "test", "restore", "clean", "pack", "publish", "new", "sln", "add", "remove", "list", "nuget", "tool", "workload" }
            .Contains(verb, StringComparer.OrdinalIgnoreCase);
    }

    public static bool LastToolCallWasIdenticalSuccess(IEnumerable<ExecutionEvidence> evidence, string name, JsonElement arguments)
    {
        var last = evidence.LastOrDefault(x => x.Kind == "tool");
        return last is { Success: true } && last.Name == name &&
               Field(last.Command, "fingerprint") == Fingerprint(name, arguments);
    }

    private static bool IsRelevantMutation(ExecutionEvidence item, string name, JsonElement arguments)
    {
        if (item.Name is not ("file.write" or "file.append" or "file.edit" or "file.mkdir")) return false;
        var changed = Field(item.Command, "path");
        var field = name == "process.run" ? "workingDirectory" : "path";
        if (changed is null || !arguments.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) return false;
        var target = value.GetString();
        if (target is null) return false;
        if (EqualPath(changed, target)) return true;
        try
        {
            if (name == "process.run")
            {
                var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(changed).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            return item.Name == "file.mkdir" && EqualPath(changed, Path.GetDirectoryName(target));
        }
        catch (ArgumentException) { return false; }
    }

    public static List<string> ApplyResolutions(List<ExecutionEvidence> evidence, JsonElement resolutions)
    {
        if (resolutions.ValueKind != JsonValueKind.Array || resolutions.GetArrayLength() > 16) throw new ArgumentException("Provide at most 16 explicit failure resolutions.");
        var rejected = new List<string>();
        foreach (var item in resolutions.EnumerateArray())
        {
            var failureId = Text(item, "failureId"); var proofId = Text(item, "evidenceId"); var reason = Text(item, "reason");
            var failure = evidence.FirstOrDefault(x => x.Id == failureId && !x.Success);
            var proof = evidence.FirstOrDefault(x => x.Id == proofId && x.Success);
            if (reason.Length > 1000 || failure is null || proof is null || proof.At <= failure.At || !Related(failure, proof))
            {
                rejected.Add("Resolution rejected for " + failureId + ": cite later successful evidence for the same operation and project target."); continue;
            }
            failure.Resolution = $"Resolved using service evidence {proof.Id}: {reason}";
        }
        return rejected;
    }

    public static int ApplyExactSuccessfulRetry(List<ExecutionEvidence> evidence, ExecutionEvidence proof)
    {
        if (!proof.Success || proof.Kind != "tool") return 0;
        var fingerprint = Field(proof.Command, "fingerprint");
        var target = Field(proof.Command, "target");
        if (fingerprint is null) return 0;
        var resolved = 0;
        foreach (var failure in evidence.Where(x => !x.Success && string.IsNullOrWhiteSpace(x.Resolution) &&
                      x.Kind == proof.Kind && x.Name == proof.Name && x.At < proof.At &&
                      (Field(x.Command, "fingerprint") == fingerprint ||
                       CorrectedGeneratedRetry(proof.Name, target, Field(x.Command, "target")))))
        {
            failure.Resolution = $"Automatically resolved by later successful retry evidence {proof.Id} for the same validated target.";
            resolved++;
        }
        return resolved;
    }

    private static bool CorrectedGeneratedRetry(string name, string? proofTarget, string? failureTarget) =>
        name is "book.chapter.append" or "book.chapter.revise" or "book.chapter.merge" or "book.chapter.review.assess" &&
        proofTarget is not null && proofTarget == failureTarget;

    public static bool Related(ExecutionEvidence failure, ExecutionEvidence proof)
    {
        // A responsive observed window verifies startup; a wrapper or Process.Start receipt does not.
        if (failure.Kind is "application-start" or "application-exit")
            return proof.Kind is "application-ready" or "application-exit" && EqualPath(failure.Name, proof.Name);
        if (failure.Kind != proof.Kind || failure.Name != proof.Name) return false;
        if (failure.Kind != "tool") return failure.RequestId is not null && failure.RequestId == proof.RequestId;
        if (failure.Name == "process.run")
            return EqualPath(Field(failure.Command, "workingDirectory"), Field(proof.Command, "workingDirectory")) &&
                Field(failure.Command, "executableId") is { } exe && exe == Field(proof.Command, "executableId");
        var a = Field(failure.Command, "path"); var b = Field(proof.Command, "path");
        if (a is not null && b is not null)
        {
            if (EqualPath(a, b)) return true;
            try
            {
                var pa = Path.GetDirectoryName(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var pb = Path.GetDirectoryName(b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return EqualPath(pa, pb) || EqualPath(a, pb) || EqualPath(pa, b);
            }
            catch (ArgumentException) { return false; }
        }
        var targetA = Field(failure.Command, "target"); var targetB = Field(proof.Command, "target");
        if (targetA is not null && targetB is not null) return targetA == targetB;
        return Field(failure.Command, "fingerprint") is { } fingerprint && fingerprint == Field(proof.Command, "fingerprint");
    }

    private static string? TargetIdentity(JsonElement arguments)
    {
        static object? Value(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (arguments.TryGetProperty(name, out var value) && Value(value) is { } selected) fields[name] = selected;
        }
        Add("bookId");
        if (fields.Count != 0) Add("chapterId");
        if (fields.Count != 0) Add("chapterRevision");
        if (fields.Count != 0) Add("batchNumber");
        if (fields.Count != 0) Add("sceneId");
        if (fields.Count != 0) Add("passageId");
        if (fields.Count != 0) Add("firstPassageId");
        if (fields.Count != 0) Add("secondPassageId");
        if (fields.Count != 0) Add("auditId");
        if (fields.Count != 0) Add("operationId");
        if (fields.Count == 0) Add("projectId");
        if (fields.Count == 0) Add("sessionId");
        if (fields.Count == 0) Add("jobId");
        if (fields.Count == 0) Add("modelId");
        if (fields.Count == 0) Add("workerId");
        if (fields.Count == 0) Add("id");
        return fields.Count == 0 ? null : JsonSerializer.Serialize(fields);
    }

    public static string ReconcileOutcome(string requested, IEnumerable<ExecutionEvidence> evidence)
    {
        if (requested is not ("completed" or "incomplete" or "failed")) throw new ArgumentException("Final outcome must be completed, incomplete, or failed.");
        return requested == "completed" && evidence.Any(x => !x.Success && string.IsNullOrWhiteSpace(x.Resolution)) ? "incomplete" : requested;
    }

    public static IReadOnlyList<string> MissingSuccessfulToolEvidence(IEnumerable<string> requiredToolNames, IEnumerable<ExecutionEvidence> evidence)
    {
        var successful = evidence.Where(x => x.Kind == "tool" && x.Success).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        return requiredToolNames.Where(x => !string.IsNullOrWhiteSpace(x) && !successful.Contains(x))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static string EvidenceBrief(IEnumerable<ExecutionEvidence> evidence)
    {
        var items = evidence.ToList();
        var selected = items.Where(x => !x.Success && string.IsNullOrWhiteSpace(x.Resolution)).TakeLast(8)
            .Concat(items.Where(x => x.Success).TakeLast(6)).DistinctBy(x => x.Id).OrderBy(x => x.At);
        return AgentPrompt.Json(selected.Select(x => new { x.Id, x.Kind, x.Name, x.Success, x.ExitCode, Command = x.Command is null ? null : Clip(x.Command, 400),
            Summary = Clip(x.Summary, 400), x.SourceSessionId, x.MessageSequence, x.RequestId, x.Resolution }));
    }
    private static string? Field(string? json, string name)
    {
        if (json is null) return null;
        try { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
    private static bool EqualPath(string? a, string? b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        a.TrimEnd('/', '\\').Equals(b.TrimEnd('/', '\\'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new ArgumentException("Resolution requires " + name + ".");
}
