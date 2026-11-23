using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual production module construction and Engine schema validation over private SQLite.
/// No workflow submission, command execution, package activation, model or network call occurs.</summary>
internal static class WorkerModuleSchemaTests
{
    internal static int Run(string fixtureParent)
    {
        if (!Path.IsPathFullyQualified(fixtureParent))
            throw new ArgumentException("Supply the exact absolute private schema-fixture parent.", nameof(fixtureParent));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        Directory.CreateDirectory(parent);
        ScopedFilePolicy.RejectLinks(parent);
        var root = Path.Combine(parent, "worker-module-schema-" + Guid.NewGuid().ToString("N"));
        RequireOwned(root, parent);
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Worker module schema fixture: " + label);
            checks++;
        }
        void Accept(JsonElement value, JsonElement schema, string label)
        {
            WorkflowSchema.Validate(value, schema);
            Check(true, label);
        }
        void Refuse(Action action, string label)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException("Worker module schema fixture expected refusal: " + label);
        }

        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var events = new EventHub();
            var models = new NoCallsModel();
            var tools = new NoCallsTools();
            var conversation = new ConversationWorkflowModule();
            WorkerWorkflows? owner = null;
            var effects = new WorkerWorkflowEffects(new CompactorContentPolicy(256), state, configuration,
                models, tools, workflowHost: () => owner!);
            // The public production constructor invokes the real contribution wrapper and
            // appends WorkflowDataModule itself before the actual Registry freezes every schema.
            owner = new WorkerWorkflows(state, configuration, events, [conversation, effects],
                [ConversationWorkflowModule.PackageBinding,
                 new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                tools: null);
            var registry = owner.Registry;
            Check(registry.Definitions.Count == 2 &&
                registry.GetDefinition(ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion)
                    .Descriptor.Id == ConversationWorkflowModule.DefinitionId &&
                registry.GetDefinition(WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion)
                    .Descriptor.Id == WorkflowDataModule.DefinitionId &&
                registry.Commands.Count == effects.Commands.Count &&
                effects.Commands.All(command => registry.GetCommand(command.Descriptor.Id, command.Descriptor.Version)
                    .Descriptor.Id == command.Descriptor.Id),
                "actual selected conversation host and automatically appended data modules register every definition and command");
            var history = registry.GetCommand(WorkerWorkflowEffects.HistoryCommand, 1).Descriptor;
            Check(history.Version == 1 && !registry.TryGetCommand(WorkerWorkflowEffects.HistoryCommand, 2, out _),
                "frozen History publishes its existing command edition without silently adding another edition");
            var schema = history.OutputSchema;
            Accept(Frame(null, includeContributions: false), schema,
                "ordinary History output may omit optional context contributions");
            Accept(Frame(null), schema,
                "explicit JSON null context contributions remains supported independently of DTO omission");
            Accept(Frame(new JsonObject { ["schemaVersion"] = 1, ["items"] = new JsonArray() }), schema,
                "version one context contributions supports an empty item list");
            var one = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["items"] = new JsonArray(new JsonObject
                {
                    ["id"] = "fixture.schema:context", ["status"] = "omitted", ["request"] = null
                })
            };
            Accept(Frame(one.DeepClone()), schema,
                "version one output accepts an explicit declared disposition with a nullable request");
            var later = one.DeepClone();
            later["schemaVersion"] = 2;
            Refuse(() => WorkflowSchema.Validate(Frame(later), schema),
                "changing only the contribution schema version to two fails the frozen History output contract");
            var unknown = one.DeepClone();
            unknown["items"]![0]!["status"] = "future-status";
            Refuse(() => WorkflowSchema.Validate(Frame(unknown), schema),
                "an undeclared contribution disposition fails closed");
            var five = new JsonObject { ["schemaVersion"] = 1, ["items"] = new JsonArray() };
            for (var index = 0; index < 5; index++)
                five["items"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "fixture.schema:context-" + index, ["status"] = "omitted", ["request"] = null
                });
            Refuse(() => WorkflowSchema.Validate(Frame(five), schema),
                "one extra contribution beyond the declared four-item bound fails closed");
            using (var unsupported = JsonDocument.Parse("""{"type":"integer","const":1}"""))
                Refuse(() => WorkflowSchema.ValidateSchema(unsupported.RootElement),
                    "unsupported const remains rejected by the actual Engine schema validator");
            var compactSchema = registry.GetCommand(WorkerWorkflowEffects.CompactCommand, 1).Descriptor.OutputSchema;
            var legacyFailure = JsonSerializer.SerializeToElement(
                new WorkerWorkflowEffects.CompactionFailureData([], "fixture numeric failure"), CerebralJson.Options);
            Accept(legacyFailure, compactSchema,
                "the preserved two-argument failure constructor with null diagnostics remains valid");
            var missingLegacy = JsonNode.Parse(legacyFailure.GetRawText())!;
            missingLegacy.AsObject().Remove("diagnostics");
            Accept(JsonSerializer.SerializeToElement(missingLegacy, CerebralJson.Options), compactSchema,
                "legacy persisted failure with no diagnostics field remains valid");
            var populated = new WorkerWorkflowEffects.CompactionDiagnostics(
                BeforeInputTokens: 4000, FloorInputTokens: 600, AfterInputTokens: 2500,
                AllowedHistoryTokens: 1700, ResidualHistoryTokens: 1900,
                CoveredSourceCount: 2, LocatorCount: 2, SourceCount: 2,
                CandidateSummaryScalarCount: 94, CandidateSummaryWordCount: 13);
            var populatedFailure = JsonSerializer.SerializeToElement(
                new WorkerWorkflowEffects.CompactionFailureData([], "fixture numeric failure") { Diagnostics = populated },
                CerebralJson.Options);
            Accept(populatedFailure, compactSchema,
                "the actual frozen compaction output schema accepts all ten populated numeric phases");
            var allNullFailure = JsonSerializer.SerializeToElement(
                new WorkerWorkflowEffects.CompactionFailureData([], "fixture unmeasured failure")
                    { Diagnostics = new WorkerWorkflowEffects.CompactionDiagnostics() }, CerebralJson.Options);
            Accept(allNullFailure, compactSchema,
                "an explicitly unmeasured failure carries all ten nullable phases");
            var mixedFailure = JsonSerializer.SerializeToElement(
                new WorkerWorkflowEffects.CompactionFailureData([], "fixture measured floor only")
                    { Diagnostics = new WorkerWorkflowEffects.CompactionDiagnostics(FloorInputTokens: 7900, SourceCount: 2) },
                CerebralJson.Options);
            Accept(mixedFailure, compactSchema,
                "one measured floor coexists with explicitly unmeasured phases");
            string[] numericFields =
            [
                "beforeInputTokens", "floorInputTokens", "afterInputTokens", "allowedHistoryTokens", "residualHistoryTokens",
                "coveredSourceCount", "locatorCount", "sourceCount", "candidateSummaryScalarCount", "candidateSummaryWordCount"
            ];
            var nullPhases = allNullFailure.GetProperty("diagnostics");
            var roundtrip = populatedFailure.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!;
            Check(roundtrip.Diagnostics == populated && roundtrip.Reason == "fixture numeric failure" &&
                  roundtrip.Usage.Count == 0 && JsonElement.DeepEquals(populatedFailure,
                      JsonSerializer.SerializeToElement(roundtrip, CerebralJson.Options)) &&
                  nullPhases.EnumerateObject().Count() == 10 &&
                  nullPhases.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(numericFields) &&
                  nullPhases.EnumerateObject().All(field => field.Value.ValueKind == JsonValueKind.Null),
                "failure roundtrip retains numeric evidence and every unmeasured phase is explicitly null with no hidden summary field");
            var numericFrame = JsonNode.Parse(populatedFailure.GetRawText())!;
            foreach (var name in numericFields)
            {
                foreach (var invalid in new[] { JsonValue.Create(-1), JsonValue.Create(0.5) })
                {
                    var changed = numericFrame.DeepClone();
                    changed["diagnostics"]![name] = invalid!.DeepClone();
                    Refuse(() => WorkflowSchema.Validate(JsonSerializer.SerializeToElement(changed, CerebralJson.Options), compactSchema),
                        "every diagnostic phase rejects a negative or fractional count: " + name);
                }
            }
            var missingPhase = numericFrame.DeepClone();
            missingPhase["diagnostics"]!.AsObject().Remove("afterInputTokens");
            Refuse(() => WorkflowSchema.Validate(JsonSerializer.SerializeToElement(missingPhase, CerebralJson.Options), compactSchema),
                "a present diagnostic object cannot confuse a missing phase field with explicit null");
            foreach (var invalidType in new[] { JsonValue.Create("unmeasured"), JsonValue.Create(true) })
            {
                var changed = numericFrame.DeepClone();
                changed["diagnostics"]!["beforeInputTokens"] = invalidType!.DeepClone();
                Refuse(() => WorkflowSchema.Validate(JsonSerializer.SerializeToElement(changed, CerebralJson.Options), compactSchema),
                    "a diagnostic token count cannot be encoded as text or a boolean");
            }
            var unknownDiagnostic = numericFrame.DeepClone();
            unknownDiagnostic["diagnostics"]!["candidateSummaryText"] = "must never be emitted";
            Refuse(() => WorkflowSchema.Validate(JsonSerializer.SerializeToElement(unknownDiagnostic, CerebralJson.Options), compactSchema),
                "closed numeric diagnostics refuses an extra raw-summary field");
            var maxima = numericFrame.DeepClone();
            maxima["diagnostics"]!["beforeInputTokens"] = int.MaxValue;
            maxima["diagnostics"]!["sourceCount"] = 128;
            maxima["diagnostics"]!["candidateSummaryScalarCount"] = 65536;
            Accept(JsonSerializer.SerializeToElement(maxima, CerebralJson.Options), compactSchema,
                "the exact declared integer source and scalar count ceilings remain representable");
            foreach (var (name, tooLarge) in new (string Name, long Value)[]
            {
                ("beforeInputTokens", 2147483648L), ("sourceCount", 129L), ("candidateSummaryScalarCount", 65537L)
            })
            {
                var changed = numericFrame.DeepClone();
                changed["diagnostics"]![name] = tooLarge;
                Refuse(() => WorkflowSchema.Validate(JsonSerializer.SerializeToElement(changed, CerebralJson.Options), compactSchema),
                    "diagnostics respects the declared token source or scalar count ceiling: " + name);
            }
            Check(models.PrepareCalls == 0 && tools.ListCalls == 0 && tools.ExecuteCalls == 0 &&
                registry.ContextProviders.Count == 0 && owner.NativeContinuity is null && owner.Engine is null &&
                owner.Journal.ReadConversationIds(limit: 1).Count == 0,
                "construction and schema checks leave the journal empty and enter no adapter tool provider continuity or runtime execution path");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            RequireOwned(root, parent);
            ScopedFilePolicy.RejectLinks(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement Frame(JsonNode? contributions, bool includeContributions = true)
    {
        var value = new JsonObject
        {
            ["context"] = new JsonObject(), ["hasOlder"] = false,
            ["beforeSequence"] = null, ["snapshotSequence"] = 0
        };
        if (includeContributions) value["contextContributions"] = contributions;
        return JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    }

    private static void RequireOwned(string root, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, comparison) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^worker-module-schema-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refusing cleanup outside the exact owned schema fixture.");
    }

    private sealed class NoCallsModel : IModelRuntimeAdapter
    {
        internal int PrepareCalls;
        public ModelAdapterDescriptor Descriptor { get; } = new("schema-no-calls", "1", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            PrepareCalls++;
            throw new InvalidOperationException("No model preparation belongs in a schema construction fixture.");
        }
    }

    private sealed class NoCallsTools : IToolDispatcher
    {
        internal int ListCalls, ExecuteCalls;
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            ListCalls++;
            throw new InvalidOperationException("No tool discovery belongs in a schema construction fixture.");
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            ExecuteCalls++;
            throw new InvalidOperationException("No tool execution belongs in a schema construction fixture.");
        }
    }
}
