using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Tools;

internal static class CortexPolicyTests
{
    public static async Task RunAsync(ToolDispatcher dispatcher, HostSettings settings, ToolExecutionContext context,
        Action<bool, string> check, string serverId, Action<string?> selectSearchVariant, Func<ToolResult, JsonElement> receivedArguments)
    {
        string Tool(string name) => "mcp." + serverId + "." + name;
        context.Profile.AllowedTools.AddRange([Tool("cortex_search"), Tool("cortex_status"), Tool("cortex_namespaces"), Tool("cortex_start"), Tool("cortex_read")]);
        Task<ToolResult> Call(string name, object arguments) => dispatcher.ExecuteAsync(new(Tool(name), CerebralJson.Element(arguments)), context, default);
        var savedReads = settings.CortexNamespaces; var savedWrites = settings.CortexWriteNamespaces;
        settings.CortexNamespaces = []; settings.CortexWriteNamespaces = [];
        check((await Call("cortex_status", new { })).Success, "Cortex status works with no configured data namespaces");
        check((await Call("cortex_namespaces", new { afterNamespace = "cursor.outside-project" })).Success, "namespace inventory cursor is not treated as a data access target");
        check(!(await Call("cortex_search", new { text = "fixture" })).Success, "empty read scope still denies data retrieval");
        settings.CortexNamespaces = savedReads; settings.CortexWriteNamespaces = savedWrites;
        JsonElement Echo(ToolResult result)
        {
            if (!result.Success) throw new InvalidOperationException("No successful native fixture call to inspect: " + result.Content);
            return receivedArguments(result);
        }
        async Task<JsonElement> SearchLimit() => (await dispatcher.ListAsync(context.Profile, default))
            .Single(tool => tool.Name == Tool("cortex_search")).InputSchema.GetProperty("properties").GetProperty("limit");
        async Task RejectedLimit(object? limit, string message)
        {
            var denied = await Call("cortex_search", new { text = "fixture", limit });
            check(!denied.Success && denied.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ProviderId: "cerebral" }, message);
        }
        var advertised = await SearchLimit();
        check(advertised.GetProperty("minimum").GetInt32() == 1 && advertised.GetProperty("maximum").GetInt32() == 8 && advertised.GetProperty("default").GetInt32() == 8,
            "Cortex search catalog intersects provider max32/default10 with host max8");
        var omitted = await Call("cortex_search", new { text = "fixture" });
        check(omitted.Success && Echo(omitted).GetProperty("limit").GetInt32() == 8, "omitted search limit dispatches the advertised default8");
        var eight = await Call("cortex_search", new { text = "fixture", limit = 8 });
        check(eight.Success && Echo(eight).GetProperty("limit").GetInt32() == 8, "explicit search limit8 reaches the provider unchanged");
        await RejectedLimit(0, "search limit0 is rejected before provider dispatch");
        await RejectedLimit(9, "search limit9 is rejected before provider dispatch");
        await RejectedLimit(null, "explicit null search limit is rejected rather than treated as omitted");
        try
        {
            selectSearchVariant("cortex-limit-narrow");
            var narrow = await SearchLimit();
            check(narrow.GetProperty("minimum").GetInt32() == 2 && narrow.GetProperty("maximum").GetInt32() == 4 && narrow.GetProperty("default").GetInt32() == 3,
                "narrower provider min2/max4/default3 remains intact");
            var narrowDefault = await Call("cortex_search", new { text = "fixture" });
            check(narrowDefault.Success && Echo(narrowDefault).GetProperty("limit").GetInt32() == 3, "omitted search limit uses the narrower provider default3");
            await RejectedLimit(1, "provider minimum2 is enforced before dispatch");
            await RejectedLimit(5, "provider maximum4 is enforced before dispatch");
            selectSearchVariant("cortex-limit-enum");
            var enumDefault = await Call("cortex_search", new { text = "fixture" });
            check(enumDefault.Success && Echo(enumDefault).GetProperty("limit").GetInt32() == 5, "default selects an admitted integer rather than an enum gap");
            await RejectedLimit(3, "provider enum gaps remain rejected before dispatch");
            selectSearchVariant("cortex-limit-exclusive");
            var exclusive = await SearchLimit();
            var exclusiveDefault = await Call("cortex_search", new { text = "fixture" });
            check(exclusive.GetProperty("minimum").GetInt32() == 4 && exclusive.GetProperty("maximum").GetInt32() == 6 &&
                exclusiveDefault.Success && Echo(exclusiveDefault).GetProperty("limit").GetInt32() == 6,
                "fractional and exclusive provider bounds admit only their supported host integers");
            await RejectedLimit(7, "exclusive provider maximum remains enforced before dispatch");
            foreach (var variant in new[] { "cortex-limit-disjoint", "cortex-limit-malformed" })
            {
                selectSearchVariant(variant);
                check(!(await dispatcher.ListAsync(context.Profile, default)).Any(tool => tool.Name == Tool("cortex_search")),
                    "incompatible search limit contract is not advertised: " + variant);
                await RejectedLimit(8, "incompatible search limit contract rejects dispatch: " + variant);
            }
        }
        finally { selectSearchVariant(null); await dispatcher.ListAsync(context.Profile, default); }
        foreach (var space in savedReads)
        {
            check(!(await Call("cortex_read", new { @namespace = space, recordType = "task", recordId = "another-session" })).Success,
                "typed task reads preserve session ownership in " + space);
            check((await Call("cortex_read", new { @namespace = space, recordType = "task", recordId = context.SessionId, revision = 2 })).Success,
                "own typed task revision can be read in " + space);
        }
        var batch = new[] {
            new { @namespace = savedReads[0], recordType = "knowledge", recordId = "guidance", revision = 1 },
            new { @namespace = savedReads[^1], recordType = "task", recordId = "another-session", revision = 2 }
        };
        check(!(await Call("cortex_read", new { records = batch })).Success, "one foreign task coordinate rejects the whole exact-read batch");
        batch[1] = new { @namespace = savedReads[^1], recordType = "task", recordId = context.SessionId, revision = 2 };
        check((await Call("cortex_read", new { records = batch, maxUtf8Bytes = 64 * 1024 })).Success, "bounded mixed typed reads retain explicit namespaces and revision pins");
        check((await Call("cortex_read", new { records = Enumerable.Range(0, 8).Select(index => new { @namespace = savedReads[0], recordType = "knowledge", recordId = "guidance-" + index, revision = 1 }).ToArray() })).Success, "eight exact-read coordinates remain admitted");
        check(!(await Call("cortex_read", new { records = Enumerable.Repeat(batch[0], 9).ToArray() })).Success, "exact-read batch limit is eight");
        check(!(await Call("cortex_read", new { @namespace = "outside", recordType = "knowledge", recordId = "guidance" })).Success, "typed reads reject a namespace outside the read scope");
        check(!(await Call("cortex_read", new { @namespace = savedReads[0], recordType = "Task", recordId = "another-session" })).Success, "task ownership cannot be bypassed with alternate enum case");
        check(!(await Call("cortex_search", new { namespaces = new object[] { 4 }, text = "x" })).Success, "malformed namespace values fail before provider dispatch");
        var large = await Call("cortex_read", new { @namespace = savedReads[0], recordType = "knowledge", recordId = "large", maxUtf8Bytes = 64 * 1024 });
        using var exact = JsonDocument.Parse(large.Content);
        check(large.Success && !large.Truncated && exact.RootElement.GetProperty("structuredContent").GetProperty("record").GetProperty("content").GetString()!.Length == 50000,
            "exact-read transport preserves a complete record above the old 32 KiB output limit");
        check(!(await Call("cortex_read", new { @namespace = savedReads[0], recordType = "knowledge", recordId = "large", maxUtf8Bytes = 256 * 1024 + 1 })).Success, "exact-read transport budget remains bounded");
        check((await Call("cortex_start", new { intent = "fixture", maxUtf8Bytes = 8192 })).Success &&
            !(await Call("cortex_start", new { intent = "fixture", maxUtf8Bytes = 8193 })).Success, "start previews use their own 8 KiB maximum");
        foreach (var id in new[] { "wrong-id", "record-id", "wrong-type", "wrong-namespace", "effective-namespaces", "wrong-revision", "missing-revision",
            "zero-revision", "record-revision", "missing-body", "partial", "truncated", "excerpt", "metadata-only", "not-complete", "conflicting-copy" })
        {
            var rejected = await Call("cortex_read", new { @namespace = savedReads[0], recordType = "knowledge", recordId = "exact-" + id, revision = 2 });
            using var retained = JsonDocument.Parse(rejected.Content);
            check(!rejected.Success && rejected.Evidence is { Outcome: ToolExecutionOutcome.Unknown, Partial: true } &&
                retained.RootElement.TryGetProperty("structuredContent", out _), "exact single read rejects " + id + " while retaining original JSON");
        }
        check((await Call("cortex_read", new { @namespace = savedReads[0], recordType = "knowledge", recordId = "exact-complete", revision = 2 })).Success,
            "a complete typed knowledge response preserves explicit complete/false-partial metadata");
        check((await Call("cortex_read", new { @namespace = savedReads[0], recordType = "task", recordId = context.SessionId, revision = 77 })).Success,
            "a completed task body uses taskId/status and does not require active knowledge state");
        foreach (var id in new[] { "batch-missing", "batch-duplicate", "batch-identity", "batch-revision", "batch-partial" })
        {
            var rejected = await Call("cortex_read", new { records = new[] {
                new { @namespace = savedReads[0], recordType = "knowledge", recordId = "exact-" + id, revision = 2 },
                new { @namespace = savedReads[^1], recordType = "knowledge", recordId = "guidance-second", revision = 2 }
            } });
            check(!rejected.Success && rejected.Evidence is { Outcome: ToolExecutionOutcome.Unknown, Partial: true }, "exact batch rejects " + id);
        }
        // Native framing carries one payload. Exercise the multi-copy defense purely
        // as well as retaining the malformed native-envelope rejection above.
        var conflictRequest = CerebralJson.Element(new
        { @namespace = savedReads[0], recordType = "knowledge", recordId = "exact-conflicting-copy", revision = 2 });
        var validator = typeof(ToolDispatcher).GetMethod("ValidateExactCortexRead",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Exact-read admission validator is unavailable.");
        var conflictRejected = false;
        try { validator.Invoke(null, [conflictRequest, CerebralJson.Element(FixtureRead(conflictRequest))]); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException)
        { conflictRejected = true; }
        check(conflictRejected, "pure exact-read admission rejects conflicting complete typed copies");

    }

    // Deliberate provider contracts for the bounded catalog/default/dispatch policy checks.
    internal static object FixtureSearchLimit(string? variant) => variant switch
    {
        "cortex-limit-narrow" => new { type = "integer", minimum = 2, maximum = 4, @default = 3 },
        "cortex-limit-enum" => new { type = "integer", minimum = 1, maximum = 32, @default = 4, @enum = new[] { 2, 5, 20 } },
        "cortex-limit-exclusive" => new { type = "integer", minimum = 2.2m, maximum = 7.8m, exclusiveMinimum = 3, exclusiveMaximum = 7, @default = 10 },
        "cortex-limit-disjoint" => new { type = "integer", minimum = 9, maximum = 32, @default = 10 },
        "cortex-limit-malformed" => new { type = "integer", minimum = "invalid", maximum = 32, @default = 10 },
        _ => new { type = "integer", minimum = 1, maximum = 32, @default = 10 }
    };

    // Shapes verified against installed Cortex single knowledge/task reads and its batch items union.
    // Mutations below are deliberate provider fixture defects, not live Cortex behavior.
    internal static object FixtureRead(JsonElement arguments)
    {
        JsonObject Record(JsonElement coordinate)
        {
            var type = coordinate.GetProperty("recordType").GetString()!;
            var id = coordinate.GetProperty("recordId").GetString()!;
            var revision = coordinate.TryGetProperty("revision", out var pin) ? pin.GetInt64() : 2;
            return new JsonObject
            {
                ["namespace"] = coordinate.GetProperty("namespace").GetString(),
                [type == "task" ? "taskId" : "recordId"] = id, ["revision"] = revision,
                [type == "task" ? "status" : "state"] = type == "task" ? revision == 77 ? "completed" : "open" : "active",
                ["title"] = "Exact fixture", ["content"] = id == "large" ? new string('x', 50000) : "FULL BODY; words metadata-only and truncated are ordinary source text."
            };
        }
        JsonObject Item(JsonElement coordinate) => new()
        {
            ["coordinate"] = new JsonObject
            {
                ["namespace"] = coordinate.GetProperty("namespace").GetString(), ["recordType"] = coordinate.GetProperty("recordType").GetString(),
                ["recordId"] = coordinate.GetProperty("recordId").GetString(), ["revision"] = coordinate.TryGetProperty("revision", out var pin) ? pin.GetInt64() : 2
            },
            ["record"] = Record(coordinate)
        };
        JsonObject payload;
        if (arguments.TryGetProperty("records", out var requested))
        {
            var coordinates = requested.EnumerateArray().ToArray();
            var items = new JsonArray(coordinates.Select(value => (JsonNode)Item(value)).ToArray());
            payload = new JsonObject { ["effectiveNamespaces"] = JsonSerializer.SerializeToNode(coordinates.Select(value => value.GetProperty("namespace").GetString()).Distinct()), ["items"] = items };
            switch (coordinates[0].GetProperty("recordId").GetString())
            {
                case "exact-batch-missing": items.RemoveAt(items.Count - 1); break;
                case "exact-batch-duplicate": items[1] = items[0]!.DeepClone(); break;
                case "exact-batch-identity": items[1]!["record"]!["recordId"] = "another-record"; break;
                case "exact-batch-revision": items[1]!["record"]!["revision"] = 3; break;
                case "exact-batch-partial": items[1]!["truncated"] = true; break;
            }
        }
        else
        {
            var record = Record(arguments);
            payload = new JsonObject
            {
                ["effectiveNamespaces"] = new JsonArray(arguments.GetProperty("namespace").GetString()),
                ["recordType"] = arguments.GetProperty("recordType").GetString(), ["recordId"] = arguments.GetProperty("recordId").GetString(),
                ["revision"] = record["revision"]!.DeepClone(), ["record"] = record
            };
            switch (arguments.GetProperty("recordId").GetString())
            {
                case "exact-wrong-id": payload["recordId"] = "another-record"; break;
                case "exact-record-id": record["recordId"] = "another-record"; break;
                case "exact-wrong-type": payload["recordType"] = "task"; break;
                case "exact-wrong-namespace": record["namespace"] = "outside"; break;
                case "exact-effective-namespaces": payload["effectiveNamespaces"] = new JsonArray("outside"); break;
                case "exact-wrong-revision": payload["revision"] = 3; record["revision"] = 3; break;
                case "exact-missing-revision": payload.Remove("revision"); record.Remove("revision"); break;
                case "exact-zero-revision": payload["revision"] = 0; record["revision"] = 0; break;
                case "exact-record-revision": record["revision"] = 3; break;
                case "exact-missing-body": record.Remove("content"); break;
                case "exact-partial": payload["partial"] = true; break;
                case "exact-truncated": record["truncated"] = true; break;
                case "exact-excerpt": record["excerpt"] = true; break;
                case "exact-metadata-only": payload["status"] = "metadata-only"; break;
                case "exact-not-complete": payload["complete"] = false; break;
                case "exact-complete": payload["complete"] = true; record["partial"] = false; break;
            }
        }
        if (!arguments.TryGetProperty("records", out _) && arguments.GetProperty("recordId").GetString() == "exact-conflicting-copy")
        {
            var conflicting = payload.DeepClone(); conflicting["record"]!["content"] = "DIFFERENT BODY";
            return new { isError = false, structuredContent = payload, content = new[] { new { type = "text", text = conflicting.ToJsonString() } } };
        }
        return new { isError = false, structuredContent = payload, content = Array.Empty<object>() };
    }

    internal static async Task RunOwnerPolicyAsync(ToolDispatcher dispatcher, HostSettings settings,
        ToolExecutionContext context, Action<bool, string> Check, string serverId, Func<ToolResult, JsonElement> receivedArguments)
    {
        var profile = context.Profile;
        string Tool(string name) => "mcp." + serverId + "." + name;
        Task<ToolResult> Call(string name, object arguments) =>
            dispatcher.ExecuteAsync(new(name, CerebralJson.Element(arguments)), context, default);
        JsonElement Echo(ToolResult result)
        {
            if (!result.Success) throw new InvalidOperationException("No successful native fixture call to inspect: " + result.Content);
            return receivedArguments(result);
        }
        Check(!(await Call(Tool("cortex_search"), new { text = "x", namespaces = new[] { "private.unauthorized" } })).Success, "Cortex namespace boundary");
        var cortex = await Call(Tool("cortex_search"), new { text = "x" });
        Check(cortex.Success && cortex.Content.Contains("8192") && cortex.Content.Contains("pegasus-cerebral"), "Cortex adds bounded explicit retrieval scope");
        profile.AllowedTools.Add(Tool("cortex_knowledge"));
        profile.AllowedTools.AddRange([Tool("cortex_task"), Tool("cortex_finish"), Tool("cortex_future_operation")]);
        var taskReads = new List<ToolResult>();
        foreach (var action in new[] { "list", "get", "history" }) taskReads.Add(await Call(Tool("cortex_task"), new { action, @namespace = "knowledge.shared" }));
        Check(taskReads.All(x => x.Success && Echo(x).GetProperty("taskId").GetString() == context.SessionId), "Cortex task list/get/history use readable namespaces without write authority and bind the session");
        var knowledgeReads = new List<ToolResult>();
        foreach (var action in new[] { "read", "analyze" }) knowledgeReads.Add(await Call(Tool("cortex_knowledge"), new { action, @namespace = "knowledge.shared" }));
        Check(knowledgeReads.All(x => x.Success), "Cortex knowledge read/analyze use readable namespaces without write authority");
        var taskWrites = new List<ToolResult>();
        foreach (var action in new[] { "create", "assign", "claim", "update", "block", "complete", "reopen", "archive", "dependencyAdd", "dependencyRemove" }) taskWrites.Add(await Call(Tool("cortex_task"), new { action, @namespace = "project.pegasus-cerebral" }));
        Check(taskWrites.All(x => !x.Success && x.Content.Contains("cortex.write", StringComparison.Ordinal)), "every current Cortex task mutation requires write authority");
        var knowledgeWrites = new List<ToolResult>();
        foreach (var action in new[] { "create", "revise", "supersede", "archive" }) knowledgeWrites.Add(await Call(Tool("cortex_knowledge"), new { action, @namespace = "project.pegasus-cerebral" }));
        Check(knowledgeWrites.All(x => !x.Success && x.Content.Contains("cortex.write", StringComparison.Ordinal)), "every current Cortex knowledge mutation requires write authority");
        Check(!(await Call(Tool("cortex_finish"), new { taskAction = "none", @namespace = "project.pegasus-cerebral" })).Success, "Cortex finish remains a mutation when taskAction is none");
        Check(!(await Call(Tool("cortex_task"), new { action = "get", @namespace = "knowledge.shared", taskId = "different-session" })).Success, "Cortex task read cannot silently substitute another session identifier");
        Check(!(await Call(Tool("cortex_knowledge"), new { action = "create", @namespace = "project.pegasus-cerebral" })).Success, "Cortex write capability required");
        profile.Capabilities.Add("cortex.write");
        Check(!(await Call(Tool("cortex_knowledge"), new { action = "create", @namespace = "knowledge.shared" })).Success, "Cortex write namespaces separate from readable namespaces");
        var createKnowledge = new { action = "create", @namespace = "project.pegasus-cerebral", recordId = "fixture-record", title = "Fixture", content = "Bounded fixture", kind = "observation", tier = "verified", provenance = new { source = "agent" }, idempotencyKey = "fixture-create" };
        var createdKnowledge = await Call(Tool("cortex_knowledge"), createKnowledge);
        Check(createdKnowledge.Success && JsonElement.DeepEquals(Echo(createdKnowledge), CerebralJson.Element(createKnowledge)), "Cortex explicit write payload is admitted unchanged without post-approval defaults");
        var moduleProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
        moduleProfile.PluginSettings["fixture.module"] = CerebralJson.Element(new { mode = "reference" });
        var ordinaryBookWrite = await dispatcher.ExecuteAsync(new(Tool("cortex_knowledge"), CerebralJson.Element(createKnowledge)), context with { Profile = moduleProfile }, default);
        Check(ordinaryBookWrite.Success && JsonElement.DeepEquals(Echo(ordinaryBookWrite), CerebralJson.Element(createKnowledge)),
            "Module profile metadata does not impose a second Cortex policy; project settings remain authoritative for task continuity");
        Check(!(await Call(Tool("cortex_task"), new { action = "update", @namespace = "knowledge.shared" })).Success && !(await Call(Tool("cortex_finish"), new { taskAction = "none", @namespace = "knowledge.shared" })).Success, "Cortex task and finish writes cannot use read-only namespaces even with write authority");
        var defaultWrite = await Call(Tool("cortex_task"), new { action = "update" });
        Check(!defaultWrite.Success && defaultWrite.Content.Contains("explicit", StringComparison.Ordinal), "Cortex mutations require explicit write coordinates instead of injected defaults");
        Check(!(await Call(Tool("cortex_future_operation"), new { })).Success, "unmapped Cortex operation fails closed despite tool and write grants");
        object Finish(params string[] contents) => new { taskAction = "none", @namespace = "project.pegasus-cerebral", taskId = context.SessionId,
            expectedRevision = 2, idempotencyKey = "fixture-finish", checkpoint = "Fixture checkpoint", evidence = "Deterministic fixture",
            knowledge = contents.Select((content, index) => new { recordId = "fixture-" + index, idempotencyKey = "fixture-write-" + index,
                title = "Fixture", content, kind = "observation", tier = "verified", tags = Array.Empty<string>(), provenance = new { source = "agent" } }).ToArray() };
        var exactNested = await Call(Tool("cortex_finish"), Finish(new string('\u00e9', 24 * 1024)));
        var oversizedNested = await Call(Tool("cortex_finish"), Finish(new string('\u00e9', 24 * 1024 + 1)));
        Check(exactNested.Success && !oversizedNested.Success && oversizedNested.Content.Contains("24 Ki characters", StringComparison.Ordinal), "nested Cortex bodies enforce the installed 24 Ki character boundary");
        Check((await Call(Tool("cortex_finish"), Finish(new string('a', 4096), new string('b', 4096)))).Success, "Cortex finish preserves batches of individually bounded knowledge records");

    }


    internal static object[] FixtureOperations(string? variant) =>
    [
        new { name = "cortex_search", description = "fixture", inputSchema = new { type = "object", properties = new { text = new { type = "string" }, namespaces = new { type = "array" }, maxUtf8Bytes = new { type = "integer" }, limit = CortexPolicyTests.FixtureSearchLimit(variant) } } },
        new { name = "cortex_knowledge", description = "fixture", inputSchema = new { type = "object", properties = new { action = new { type = "string" }, @namespace = new { type = "string" }, maxUtf8Bytes = new { type = "integer" } } } },
        new { name = "cortex_task", description = "fixture", inputSchema = new { type = "object", properties = new { action = new { type = "string" }, @namespace = new { type = "string" }, taskId = new { type = "string" }, maxUtf8Bytes = new { type = "integer" } } } },
        new { name = "cortex_finish", description = "fixture", inputSchema = new { type = "object", properties = new { taskAction = new { type = "string" }, @namespace = new { type = "string" }, taskId = new { type = "string" }, maxUtf8Bytes = new { type = "integer" }, knowledge = new { type = "array" } } } },
        new { name = "cortex_start", description = "fixture", inputSchema = new { type = "object", properties = new { namespaces = new { type = "array" }, intent = new { type = "string" }, taskId = new { type = "string" }, maxUtf8Bytes = new { type = "integer" } } } },
        new { name = "cortex_read", description = "fixture", inputSchema = new { type = "object" } },
        new { name = "cortex_status", description = "fixture", inputSchema = new { type = "object", additionalProperties = false } },
        new { name = "cortex_namespaces", description = "fixture", inputSchema = new { type = "object", properties = new { afterNamespace = new { type = "string" }, limit = new { type = "integer" }, maxUtf8Bytes = new { type = "integer" } } } },
        new { name = "cortex_future_operation", description = "fixture unmapped operation", inputSchema = new { type = "object" } }
    ];

    internal static object FixtureCall(string name, JsonElement arguments)
    {
        var echo = new[] { new { type = "text", text = arguments.GetRawText() } };
        if (!CortexOperationPolicy.IsMutation(name, arguments)) return new { content = echo, isError = false };
        object Record(JsonElement item, string idName) => new Dictionary<string, object?>
        {
            ["namespace"] = arguments.GetProperty("namespace").GetString(), [idName] = item.GetProperty(idName).GetString(),
            ["revision"] = (item.TryGetProperty("expectedRevision", out var revision) ? revision.GetInt64() : 0) + 1
        };
        var receipt = new Dictionary<string, object?> { ["effectiveNamespaces"] = new[] { arguments.GetProperty("namespace").GetString() }, ["disposition"] = "appended" };
        if (name == "cortex_knowledge") receipt["record"] = Record(arguments, "recordId");
        else
        {
            receipt["task"] = Record(arguments, "taskId");
            if (name == "cortex_finish") receipt["knowledge"] = arguments.GetProperty("knowledge").EnumerateArray().Select(item => Record(item, "recordId")).ToArray();
        }
        return new { content = echo, structuredContent = receipt, isError = false };
    }


    public static async Task<int> RunPreDiscoveryAsync(Func<string[], List<string>> fixtureArguments)
    {
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS: " + label); }
        var profile = new AgentProfile
        { AllowedTools = ["mcp.legacy.cortex_status", "mcp.legacy.echo"], Capabilities = ["mcp.legacy"] };
        foreach (var connector in new[] { false, true })
        {
            var registration = new McpRegistration
            {
                Id = "legacy", Enabled = true, ViaUserConnector = connector,
                Executable = "must-not-launch", Url = connector ? "invalid://must-not-contact" : null
            };
            var settings = new HostSettings { McpServers = [registration] };
            await using var dispatcher = new ToolDispatcher(() => settings);
            var context = new ToolExecutionContext(profile, "fixture-session", "fixture-run");
            var semantic = await dispatcher.ExecuteAsync(new("mcp.legacy.cortex_status", CerebralJson.Element(new { })), context, default);
            Check(!semantic.Success && semantic.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                semantic.Content.Contains("enrolled native pipe", StringComparison.Ordinal),
                "Cortex semantic routing refuses a legacy transport before discovery");
            var scope = new CortexAccessScope("fixture-project", "legacy", [], null, true);
            var scoped = await dispatcher.ExecuteAsync(new("mcp.legacy.echo", CerebralJson.Element(new { text = "fixture" })),
                context with { Cortex = scope }, default);
            Check(!scoped.Success && scoped.Content.Contains("enrolled native pipe", StringComparison.Ordinal),
                "an authenticated Cortex-bound server requires a pipe even for a non-prefixed operation");
            var ungranted = await dispatcher.ExecuteAsync(new("mcp.legacy.cortex_status", CerebralJson.Element(new { })),
                context with { Profile = new AgentProfile { AllowedTools = [], Capabilities = [] } }, default);
            Check(!ungranted.Success && !ungranted.Content.Contains("enrolled native pipe", StringComparison.Ordinal),
                "transport classification never supplies the missing tool grant");
            registration.CortexNativeRequired = true;
            var marked = await dispatcher.ExecuteAsync(new("mcp.legacy.echo", CerebralJson.Element(new { text = "fixture" })), context, default);
            Check(!marked.Success && marked.Content.Contains("enrolled native pipe", StringComparison.Ordinal),
                "an explicit native-required marker refuses a generic-looking legacy route");
        }

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-cortex-prediscovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var entered = Path.Combine(root, "entered"); var release = Path.Combine(root, "release"); var called = Path.Combine(root, "called");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ToolResult>? pending = null;
        try
        {
            var registration = new McpRegistration
            {
                Id = "generic", Enabled = true, ViaUserConnector = false, Executable = Environment.ProcessPath!,
                Arguments = fixtureArguments(["--fixture-mcp-hold-discovery", entered, release, called])
            };
            var settings = new HostSettings { McpServers = [registration] };
            var generic = new AgentProfile { AllowedTools = ["mcp.generic.echo"], Capabilities = ["mcp.generic"] };
            var context = new ToolExecutionContext(generic, "fixture-session", "fixture-run");
            var call = new ToolCall("mcp.generic.echo", CerebralJson.Element(new { text = "generic-control" }));
            await using var dispatcher = new ToolDispatcher(() => settings);
            var fingerprint = CortexOperationPolicy.ProviderFingerprint(registration);
            pending = dispatcher.ExecuteAsync(call, context, deadline.Token);
            for (var attempt = 0; attempt < 250 && !File.Exists(entered) && !pending.IsCompleted; attempt++)
                await Task.Delay(20, deadline.Token);
            Check(File.Exists(entered), "controlled generic provider reached its held discovery boundary");
            registration.CortexNativeRequired = true;
            Check(CortexOperationPolicy.ProviderFingerprint(registration) == fingerprint,
                "the marker-race regression does not rely on a broad fingerprint contract change");
            await File.WriteAllTextAsync(release, "release", deadline.Token);
            var refused = await pending.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Check(!refused.Success && refused.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                refused.Content.Contains("enrolled native pipe", StringComparison.Ordinal) && !File.Exists(called),
                "a false-to-true native marker during discovery prevents tools/call before dispatch");

            registration.CortexNativeRequired = false;
            var control = await dispatcher.ExecuteAsync(call, context, deadline.Token);
            Check(control.Success && File.Exists(called),
                "an unrelated unmarked generic MCP provider retains normal discovery and execution");
            var otherScope = new CortexAccessScope("fixture-project", "another-cortex", [], null, true);
            Check((await dispatcher.ExecuteAsync(call, context with { Cortex = otherScope }, deadline.Token)).Success,
                "a different bound Cortex server does not impose its transport on generic MCP");
        }
        finally
        {
            if (!File.Exists(release)) await File.WriteAllTextAsync(release, "release");
            if (pending is not null && !pending.IsCompleted)
            {
                deadline.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { }
            }
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var exact = Path.GetFullPath(root);
            if (!exact.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(exact).StartsWith("cerebral-cortex-prediscovery-", StringComparison.Ordinal))
                throw new InvalidOperationException("The fixture cleanup escaped its exact temporary root.");
            Directory.Delete(exact, recursive: true);
        }
        return checks;
    }

}
