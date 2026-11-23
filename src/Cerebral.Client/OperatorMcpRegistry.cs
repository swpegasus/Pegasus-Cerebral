using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>One explicitly admitted operator command. This catalog is not an arbitrary Worker relay.</summary>
public sealed record OperatorMcpOperation(string Id, string Description, bool Mutating,
    IReadOnlyList<string> Arguments, IReadOnlyList<string> RequiredArguments, string Target = "worker")
{
    /// <summary>The owning module can supply its exact versioned schema; this is not an execution grant.</summary>
    public JsonElement? InputSchema { get; init; }
}

public sealed record OperatorMcpCallResult(string Operation, string State, JsonElement Data,
    string? Error = null, bool ReconciliationRequired = false);

/// <summary>
/// Caller identity belongs to the captured owner transport. Neither an argument nor a returned
/// descriptor can change that identity. The legacy saved-profile MCP adapter is independent.
/// </summary>
public sealed class OperatorMcpRegistry : IDisposable
{
    public const int MaximumArgumentBytes = 256 * 1024;
    public const int MaximumResultBytes = 1024 * 1024;
    private readonly Func<string, JsonElement, CancellationToken, Task<CommandResult>> command;
    private readonly Func<string, string, string, JsonElement, CancellationToken, Task<OperatorMcpCallResult>>? desktop;
    private readonly IDisposable? transport;
    private readonly FrozenDictionary<string, OperatorMcpOperation> operations;
    private static readonly HashSet<string> AuthorityFields = new(StringComparer.OrdinalIgnoreCase)
        { "owner", "caller", "callerProfileId", "callerSid", "principal", "authority", "accessKey", "peerToken", "authorization" };
    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessKey", "peerToken", "secret", "secretDigest", "password", "apiKey", "clientSecret", "authorization",
        "credential", "credentials", "token", "privateKey", "sshIdentityFile", "sshKnownHostsFile", "connectionString",
        "apiToken", "hfToken", "huggingFaceToken", "bearerToken", "secretKey", "credentialValue", "authentication",
        "auth", "configuration", "providerConfiguration", "backendConfiguration", "rawSettings",
        "pluginSettings", "headers", "environment", "assignmentText", "privateAssignment", "modelContext", "systemPrompt"
    };
    private static readonly FrozenSet<string> NormalizedHiddenFields = HiddenFields.Select(NormalizeField).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] AgentMutableFields =
    ["name", "purpose", "role", "modelId", "followsDefaultModel", "workerId", "remoteProfileId", "modelWorkerId",
     "contextTokens", "responseTokens", "followsModelBudgets", "reasoningMode", "reasoningTokens", "temperature",
     "maxSteps", "maxSeconds", "memoryBudgetMb", "idleMinutes", "automaticCompaction", "compactionProfileId",
     "compactionReductionPercent", "compactionTriggerPercent", "allowedRoots", "allowedTools", "capabilities", "requiredModelCapabilities"];

    public OperatorMcpRegistry(Func<string, JsonElement, CancellationToken, Task<CommandResult>> command,
        Func<string, string, string, JsonElement, CancellationToken, Task<OperatorMcpCallResult>>? desktop = null,
        IEnumerable<OperatorMcpOperation>? additionalOperations = null, IDisposable? transport = null)
    {
        this.command = command ?? throw new ArgumentNullException(nameof(command));
        this.desktop = desktop; this.transport = transport;
        var catalog = DefaultOperations().Concat(additionalOperations ?? []).ToArray();
        if (catalog.Length > 512) throw new ArgumentException("Operator catalog exceeds its declaration bound.");
        foreach (var item in catalog)
        {
            if (item is null || item.Arguments is null || item.RequiredArguments is null ||
                item.Arguments.Count > 128 || item.RequiredArguments.Count > 128 ||
                string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 || item.Id.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 1024 ||
                item.Arguments.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128 || x.Any(char.IsControl)) ||
                item.InputSchema is { } schema && (schema.ValueKind != JsonValueKind.Object || JsonBytes(schema) > 65_536) ||
                item.Target is not ("worker" or "main" or "configuration") ||
                item.Arguments.Distinct(StringComparer.Ordinal).Count() != item.Arguments.Count ||
                item.RequiredArguments.Except(item.Arguments, StringComparer.Ordinal).Any())
                throw new ArgumentException("Invalid operator operation declaration.");
        }
        if (catalog.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Length)
            throw new ArgumentException("Duplicate operator operation declaration.");
        operations = catalog.Select(x => x with
        {
            Arguments = Array.AsReadOnly(x.Arguments.ToArray()), RequiredArguments = Array.AsReadOnly(x.RequiredArguments.ToArray()),
            InputSchema = x.InputSchema?.Clone()
        }).ToFrozenDictionary(x => x.Id, StringComparer.Ordinal);
    }

    public static OperatorMcpRegistry ForInstalledWorker(
        Func<string, string, string, JsonElement, CancellationToken, Task<OperatorMcpCallResult>>? desktop = null,
        IEnumerable<OperatorMcpOperation>? additionalOperations = null)
    {
        // This factory captures the protected enrollment/pipe transport once. Failure must not
        // choose loopback HTTP, a different Worker, or a caller-supplied profile as a fallback.
        var client = CerebralClient.ForInstalledWorker();
        try
        {
            return new((operation, arguments, token) => client.CommandAsync(operation, arguments,
                cancellationToken: token), desktop, additionalOperations, client);
        }
        catch { client.Dispose(); throw; }
    }

    public JsonElement Discover() => CerebralJson.Element(new
    {
        mode = "installed-owner", transport = "captured-owner-client", operations = operations.Values.OrderBy(x => x.Id)
            .Select(x => new { id = x.Id, x.Description, x.Mutating, target = x.Target,
                transportAvailable = x.Target == "worker" || desktop is not null, readiness = "inspect owning service before effects" }),
        limitations = new[]
        {
            "Discovery is not readiness, authorization for an unrelated task, or proof of completed effects.",
            "Main and Configuration retain their own admitted command coverage and selection/connection guards.",
            "Configuration domain actions are unavailable until their owning registry admits them.",
            "Use immutable artifact references and the product transfer client; never inline large model outputs.",
            "Inspect original durable state after uncertain acknowledgement; do not automatically repeat mutations."
        }
    });

    public JsonElement Describe(string id)
    {
        var definition = Get(id);
        var properties = definition.Arguments.ToDictionary(x => x, _ => (object)new { });
        return CerebralJson.Element(new
        {
            id = definition.Id, definition.Description, definition.Mutating, target = definition.Target,
            inputSchema = definition.InputSchema ?? CerebralJson.Element(new { type = "object", properties, required = definition.RequiredArguments, additionalProperties = false }),
            schemaCoverage = definition.InputSchema is null ? "field-level; owning handler validates typed values" : "owner-supplied versioned schema",
            authority = "captured owner connection; tool arguments cannot select caller identity",
            transportAvailable = definition.Target == "worker" || desktop is not null
        });
    }

    /// <summary>Dedicated bounded control capacity must remain available during long mutations.</summary>
    public bool IsControlOperation(string id) => operations.TryGetValue(id, out var operation) &&
        (!operation.Mutating || id.EndsWith(".cancel", StringComparison.Ordinal) || id == "sessions.stop");

    public async Task<OperatorMcpCallResult> CallAsync(string id, JsonElement arguments, CancellationToken token = default)
    {
        var definition = Get(id);
        ValidateArguments(arguments, definition);
        // Clone before awaiting: the JSON document that carried this call is owned by the MCP reader.
        arguments = arguments.Clone();
        var mutationMayHaveDispatched = false;
        try
        {
            if (definition.Target != "worker")
            {
                if (desktop is null) return Result(id, "unavailable", error: "The trusted desktop endpoint was not configured.");
                var requestId = RequiredString(arguments, "requestId");
                var nested = arguments.TryGetProperty("arguments", out var inner) ? inner : CerebralJson.Element(new { });
                if (nested.ValueKind != JsonValueKind.Object) throw new ArgumentException("Desktop arguments must be an object.");
                return await desktop(definition.Target, id[(id.IndexOf('.') + 1)..], requestId, nested.Clone(), token).ConfigureAwait(false);
            }
            CommandResult response;
            if (id == "settings.inspect")
            {
                response = await command("settings.get", CerebralJson.Element(new { }), token).ConfigureAwait(false);
                if (response.Success) response = CommandResult.Ok(SettingsProjection(response.Data));
            }
            else if (id is "agents.create" or "agents.update")
            {
                var saved = await SaveAgentAsync(id, arguments, () => mutationMayHaveDispatched = true, token).ConfigureAwait(false);
                response = saved;
            }
            else
            {
                mutationMayHaveDispatched = definition.Mutating;
                response = await command(id, arguments, token).ConfigureAwait(false);
            }
            var safe = Sanitize(response.Data, includeInstructions: id.StartsWith("agents.instructions.", StringComparison.Ordinal));
            if (JsonBytes(safe) > MaximumResultBytes)
                return Result(id, definition.Mutating ? "uncertain" : "failed", error: "The result exceeded the MCP bound. Inspect paginated or compact state before any mutation retry.",
                    reconciliation: definition.Mutating);
            return new(id, response.Success ? id.EndsWith(".submit", StringComparison.Ordinal) ? "accepted" : "completed" : mutationMayHaveDispatched ? "failed" : "rejected", safe,
                response.Success ? null : SafeError(response.Error), !response.Success && mutationMayHaveDispatched);
        }
        catch (OperationCanceledException)
        {
            return Result(id, definition.Mutating ? "uncertain" : "cancelled", error: definition.Mutating
                ? "The caller stopped waiting. The original operation may have effects; inspect its durable request before retrying."
                : "Inspection cancelled.", reconciliation: definition.Mutating);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result(id, definition.Mutating ? "uncertain" : "failed", error:
                "The captured owner transport did not confirm a bounded response. Reconcile the original operation; no fallback or replay occurred.", reconciliation: definition.Mutating);
        }
    }

    private async Task<CommandResult> SaveAgentAsync(string operation, JsonElement arguments, Action dispatching, CancellationToken token)
    {
        var id = RequiredString(arguments, "id");
        if (!arguments.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Agent changes must be an object.");
        ValidateClosedObject(changes, AgentMutableFields, []);
        var listed = await command("agents.list", CerebralJson.Element(new { }), token).ConfigureAwait(false);
        if (!listed.Success) return listed;
        if (listed.Data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid owner agent catalog.");
        var matches = listed.Data.EnumerateArray().Where(x => x.TryGetProperty("id", out var value) && value.GetString() == id).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Ambiguous agent identity.");
        JsonObject value;
        if (operation == "agents.create")
        {
            if (matches.Length != 0) return CommandResult.Fail("The agent already exists. Inspect it before another request.");
            value = JsonSerializer.SerializeToNode(new AgentProfile { Id = id }, CerebralJson.Options)!.AsObject();
        }
        else
        {
            if (matches.Length != 1) return CommandResult.Fail("Agent not found.");
            if (!arguments.TryGetProperty("expectedRevision", out var revision) || !revision.TryGetInt32(out var expected) ||
                matches[0].GetProperty("revision").GetInt32() != expected)
                return CommandResult.Fail("The agent revision changed. Refresh before saving.");
            // Preserve undisclosed plugin/credential-reference fields in the trusted process.
            // The service repeats the revision check atomically when saving.
            value = JsonNode.Parse(matches[0].GetRawText())!.AsObject();
        }
        foreach (var change in changes.EnumerateObject()) value[change.Name] = JsonNode.Parse(change.Value.GetRawText());
        dispatching();
        return await command("agents.save", JsonSerializer.SerializeToElement(value, CerebralJson.Options), token).ConfigureAwait(false);
    }

    private static JsonElement SettingsProjection(JsonElement input)
    {
        string[] allowed = ["name", "installationId", "coordinator", "allowLan", "externalCommandsEnabled", "modelIdleMinutes",
            "modelDirectory", "projectDirectory", "memoryReserveMb", "maxConcurrentJobs", "maxLogicalProcessorCount", "logicalProcessorIds"];
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid owner settings response.");
        var result = new JsonObject();
        foreach (var item in input.EnumerateObject().Where(x => allowed.Contains(x.Name, StringComparer.Ordinal)))
            result[item.Name] = JsonNode.Parse(item.Value.GetRawText());
        return JsonSerializer.SerializeToElement(result, CerebralJson.Options);
    }

    public static JsonElement Sanitize(JsonElement input, bool includeInstructions = false)
    {
        JsonNode? Visit(JsonElement element, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Operator result nesting exceeds the bound.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var result = new JsonObject();
                foreach (var item in element.EnumerateObject())
                {
                    if (NormalizedHiddenFields.Contains(NormalizeField(item.Name)) || !includeInstructions && item.Name.Equals("instructions", StringComparison.OrdinalIgnoreCase)) continue;
                    result[item.Name] = Visit(item.Value, depth + 1);
                }
                return result;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var result = new JsonArray();
                foreach (var item in element.EnumerateArray()) result.Add(Visit(item, depth + 1));
                return result;
            }
            if (element.ValueKind == JsonValueKind.String) return JsonValue.Create(RedactDiagnostic(element.GetString() ?? ""));
            return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonNode.Parse(element.GetRawText());
        }
        return JsonSerializer.SerializeToElement(Visit(input, 0), CerebralJson.Options);
    }

    private OperatorMcpOperation Get(string id) => operations.TryGetValue(id, out var value) ? value :
        throw new ArgumentException("This operation is not in the operator MCP catalog. Discover supported controls first.");
    private static void ValidateArguments(JsonElement input, OperatorMcpOperation operation)
    {
        if (JsonBytes(input) > MaximumArgumentBytes) throw new ArgumentException("Operator arguments exceed 256 KiB.");
        ValidateClosedObject(input, operation.Arguments, operation.RequiredArguments);
        foreach (var field in input.EnumerateObject())
            if (AuthorityFields.Contains(field.Name)) throw new ArgumentException("Tool input cannot select operator authority.");
    }
    public static void ValidateClosedObject(JsonElement input, IEnumerable<string> allowed, IEnumerable<string> required)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be an object.");
        var fields = input.EnumerateObject().ToArray();
        if (fields.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length ||
            fields.Any(x => !allowed.Contains(x.Name, StringComparer.Ordinal)) || required.Any(x => !input.TryGetProperty(x, out _)))
            throw new ArgumentException("Arguments contain unknown, duplicate or missing fields.");
    }
    private static string RequiredString(JsonElement input, string field) => input.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text && !text.Any(char.IsControl)
        ? text : throw new ArgumentException("A bounded " + field + " is required.");
    private static int JsonBytes(JsonElement input) => System.Text.Encoding.UTF8.GetByteCount(input.GetRawText());
    private static string NormalizeField(string name) => new(name.Where(char.IsAsciiLetterOrDigit).ToArray());
    private static string SafeError(string? error) => string.IsNullOrWhiteSpace(error) ? "The owning service rejected this operation." :
        error.Length <= 1024 && !error.Any(x => x == '\0') ? RedactDiagnostic(error) : "The owning service rejected this operation; inspect its bounded diagnostic receipt.";
    public static string RedactDiagnostic(string value)
    {
        // Defense in depth for an owning diagnostic that accidentally echoes a conventional
        // credential assignment. Settings projections remain allowlisted, not regex-based.
        try
        {
            value = Regex.Replace(value, @"(?i)\b(Bearer)\s+\S+", "$1 [redacted]", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(?i)\b(access[_-]?key|api[_-]?key|peer[_-]?token|secret|password)\s*[:=]\s*\S+", "$1=[redacted]", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            return Regex.Replace(value, @"\b(?:sk-[A-Za-z0-9_-]{12,}|hf_[A-Za-z0-9]{10,})", "[redacted]", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic redacted]"; }
    }
    private static OperatorMcpCallResult Result(string id, string state, object? data = null, string? error = null, bool reconciliation = false) =>
        new(id, state, CerebralJson.Element(data ?? new { }), error, reconciliation);
    public void Dispose() => transport?.Dispose();

    private static IEnumerable<OperatorMcpOperation> DefaultOperations()
    {
        static OperatorMcpOperation Op(string id, bool write, string[] fields, string[]? required = null) =>
            new(id, "Use the owning Cerebral handler for " + id + "; acceptance is distinct from completed work.", write, fields, required ?? []);
        foreach (var id in new[] { "health", "resources", "models.list", "models.runtime.status", "agents.list", "jobs.list", "sessions.list", "queue.active-summaries" })
            yield return Op(id, false, []);
        yield return Op("settings.inspect", false, []);
        foreach (var id in new[] { "models.inspect", "jobs.get", "sessions.get" }) yield return Op(id, false, ["id"], ["id"]);
        foreach (var id in new[] { "models.load", "models.unload", "jobs.cancel", "sessions.stop", "sessions.compact", "agents.delete" })
            yield return Op(id, true, ["id"], ["id"]);
        yield return Op("agents.create", true, ["id", "changes"], ["id", "changes"]);
        yield return Op("agents.update", true, ["id", "expectedRevision", "changes"], ["id", "expectedRevision", "changes"]);
        yield return Op("agents.instructions.get", false, ["profileId"], ["profileId"]);
        yield return Op("agents.instructions.save", true, ["profileId", "content", "expectedRevision"], ["profileId", "content", "expectedRevision"]);
        foreach (var id in new[] { "jobs.workflow.active-summaries", "jobs.workflow.active", "jobs.workflow.recent" })
            yield return Op(id, false, ["limit", "afterRow", "beforeRow", "snapshotRow"]);
        foreach (var id in new[] { "jobs.workflow.get", "jobs.workflow.cancel", "jobs.workflow.reconcile" })
            yield return Op(id, id != "jobs.workflow.get", ["id"], ["id"]);
        yield return Op("jobs.workflow.submit", true, ["requestId", "conversationId", "profileId", "definitionId", "definitionVersion", "userText", "assignmentText", "acceptedContent", "input", "stepLimit", "ownerProjectId", "localGeneration"],
            ["requestId", "conversationId", "profileId", "definitionId", "definitionVersion", "userText", "input"]);
        yield return Op("jobs.workflow.resume", true, ["id", "expectedRevision"], ["id", "expectedRevision"]);
        yield return Op("jobs.workflow.steer", true, ["requestId", "runId", "text", "assignmentText"], ["requestId", "runId", "text"]);
        yield return Op("jobs.workflow.events", false, ["id", "afterSequence", "snapshotSequence", "limit"], ["id"]);
        yield return Op("jobs.workflow.events.previous", false, ["id", "beforeSequence", "snapshotSequence", "limit"], ["id"]);
        yield return Op("jobs.workflow.receipt", false, ["runId", "effectId", "offset", "limit", "expectedRevision"], ["runId", "effectId"]);
        yield return Op("engine.status", false, []);
        foreach (var id in new[] { "engine.pause", "engine.resume", "engine.rollback", "engine.promote" })
            yield return Op(id, true, ["expectedEpoch"], ["expectedEpoch"]);
        yield return Op("engine.activate", true, ["slot", "expectedEpoch"], ["slot", "expectedEpoch"]);
        foreach (var target in new[] { "main", "configuration" })
        {
            var reads = target == "main"
                ? new[] { "instance.inspect", "capabilities.inspect", "snapshot.inspect", "conversation.list", "activity.inspect", "activity.detail", "workflow.inspect", "plugin.surface.inspect" }
                : new[] { "instance.inspect", "capabilities.inspect", "snapshot.inspect", "pages.list", "page.inspect" };
            var writes = target == "main"
                ? new[] { "window.activate", "window.capture", "conversation.new", "conversation.open", "conversation.history.back", "conversation.history.forward", "conversation.workflow.select", "composer.set", "message.send", "workflow.open", "wait.settled" }
                : new[] { "page.open", "window.activate" };
            foreach (var id in reads.Concat(writes))
                yield return new(target + "." + id, "Call the existing " + target + " semantic registry. Its exact expectation and native identity guards remain authoritative.",
                    writes.Contains(id), ["requestId", "arguments"], ["requestId", "arguments"], target);
        }
    }
}
