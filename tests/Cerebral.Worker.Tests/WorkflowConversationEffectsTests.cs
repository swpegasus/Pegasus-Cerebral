using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated SQLite history with deterministic model/dispatcher doubles. No installed
/// Worker, PAO, provider connection, actual tokenizer or inference is exercised.</summary>
public static class WorkflowConversationEffectsTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-conversation-effects-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Conversation effects: " + message); checks++; }
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var configuration = new Configuration(store);
            var settings = configuration.Get();
            settings.AccessKey = "fixture-private-key-never-in-model-context";
            store.Put("settings", "host", settings);
            var profile = new AgentProfile
            {
                Id = "conversation-agent", Name = "Conversation", ModelId = "fixture-model", AutomaticCompaction = false,
                FollowsModelBudgets = false, ContextTokens = 8192, ResponseTokens = 512,
                AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["files"],
                PluginSettings = new() { ["opaque"] = Element("fixture-private-plugin-setting") }
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(root, "fixture.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            var automaticProfile = new AgentProfile
            {
                Id = "automatic-context-agent", Name = "Automatic context", ModelId = "automatic-context-model",
                AutomaticCompaction = false, FollowsModelBudgets = true, AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["files"]
            };
            store.Put("agent", automaticProfile.Id, automaticProfile);
            store.Put("model", automaticProfile.ModelId, new ModelRecord
            {
                Id = automaticProfile.ModelId, Path = Path.Combine(root, "automatic.gguf"), State = "unloaded",
                AutomaticContextSizing = true, ContextTokens = 48_128, RecommendedContextTokens = 16_384,
                NativeContextTokens = 262_144, ParallelRequests = 1
            });
            var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
            var workflows = new WorkflowStore(journal);
            var models = new Models(); var tools = new Tools();
            var bridge = new WorkerWorkflowEffects(store, configuration, models, tools);
            _ = new WorkflowRegistry([bridge]);
            var automaticSnapshotJson = await WorkerExecutionSnapshot.CaptureAsync(store,
                automaticProfile, configuration, tools, [], default);
            var automaticSnapshot = WorkerExecutionSnapshot.Read(automaticSnapshotJson);
            Check(automaticSnapshot.Profile.ContextTokens == 48_128 && automaticSnapshot.Profile.ResponseTokens == 12_032,
                "an unloaded automatic model reuses its last measured safe context for a new workflow instead of falling back to the smaller import recommendation");
            var history = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
            var catalog = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CatalogCommand && x.Descriptor.Version == 1);
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            CerebralEvent Append(string id, string conversation, string text, bool hidden = false, bool excluded = false,
                ModelInclusion inclusion = ModelInclusion.Recent, string type = CerebralEventTypes.Message, JsonElement? data = null,
                IReadOnlyList<ContentPart>? parts = null, EventActorKind? actorKind = null) => journal.Append(new(id, conversation, "earlier-turn", 0, type,
                    new(actorKind ?? (type == CerebralEventTypes.Message ? EventActorKind.User : EventActorKind.Tool), "fixture"),
                    parts ?? [new TextContentPart(text)], new(inclusion, hidden ? HumanVisibility.Hidden : HumanVisibility.Transcript),
                    new(EventSourceKind.User), DateTimeOffset.UtcNow, data, ExcludedFromContext: excluded), journal.GetRevision(conversation)).Event;
            async Task<WorkflowRun> Submit(string id, string conversation)
            {
                var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, configuration, tools,
                    [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default);
                return workflows.Submit(new(id, conversation, profile.Id, "fixture:conversation", "1.0.0", "EXACT CURRENT REQUEST",
                    "HIDDEN CURRENT ASSIGNMENT", Element(new { }), snapshot));
            }
            WorkflowCommandContext Context(WorkflowRun run, object args) => new("lookup-" + Guid.NewGuid().ToString("N"), run.Submission, Element(args)) { RunId = run.Id };
            async Task<WorkerWorkflowEffects.HistoryData> Page(WorkflowRun run, object args)
            {
                var result = await history.ExecuteAsync(Context(run, args), default);
                if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException(result.Error);
                WorkflowSchema.Validate(result.Data!.Value, history.Descriptor.OutputSchema);
                return result.Data.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            }

            // These are accepted non-owner messages, not display-card User goals. Keep real
            // caller/parent provenance and commit a checkpoint through the normal SQLite path.
            const string parentPrivate = "HISTORY-PARENT-PRIVATE-CANARY";
            var parentProfile = CerebralJson.Read<AgentProfile>(Element(profile));
            parentProfile.Id = "history-parent"; parentProfile.Role = "lead"; parentProfile.FollowsDefaultModel = false;
            parentProfile.Capabilities.Add("agents.delegate"); store.Put("agent", parentProfile.Id, parentProfile);
            var childProfile = CerebralJson.Read<AgentProfile>(Element(profile));
            childProfile.Id = "history-child"; childProfile.Role = "helper"; childProfile.FollowsDefaultModel = false;
            store.Put("agent", childProfile.Id, childProfile);
            var parentSnapshot = await WorkerExecutionSnapshot.CaptureAsync(store, parentProfile, configuration, tools,
                [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default);
            var historyParent = workflows.Submit(new("history-parent-run", "history-parent-chat", parentProfile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion, parentPrivate, null,
                Element(new { }), parentSnapshot));

            async Task CheckNonOwnerHistory(string key, EventActor caller, AgentProfile selectedProfile, string? parentId = null)
            {
                var conversation = key + "-chat";
                var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, selectedProfile, configuration, tools,
                    [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default);
                WorkflowRun Accept(string id, string text, string assignment) => workflows.Submit(new(id, conversation,
                    selectedProfile.Id, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                    text, assignment, Element(new { }), snapshot, Caller: caller) { ParentRunId = parentId });
                var prior = Accept(key + "-prior", key + "-LOCAL-SOURCE", key + "-LOCAL-ASSIGNMENT");
                var priorMessage = journal.ReadPage(conversation, prior.Submission.ConversationSequence, 1).Events.Single();
                prior = workflows.TryClaim(prior.Id)!;
                workflows.ApplyStep(prior.Id, prior.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(Element(new { text = key + "-LOCAL-RESULT" }))));
                var next = Accept(key + "-next", key + "-CURRENT-SOURCE", key + "-CURRENT-ASSIGNMENT");
                var hasUserGoal = true;
                try { workflows.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, conversation); }
                catch (KeyNotFoundException) { hasUserGoal = false; }
                Check(!hasUserGoal && priorMessage.Actor == caller && next.Submission.Caller == caller &&
                    next.Submission.ParentRunId == parentId,
                    key + ": genuine caller/parent history has no display-only User goal");
                var projected = await Page(next, new { });
                Check(projected.Context.Checkpoint is null &&
                    projected.Context.Contributions.All(item => !item.Id.StartsWith("idle-goal-", StringComparison.Ordinal)) &&
                    Text(projected.Context).Contains(key + "-LOCAL-SOURCE", StringComparison.Ordinal) &&
                    Text(projected.Context).Contains(key + "-LOCAL-ASSIGNMENT", StringComparison.Ordinal) &&
                    !Text(projected.Context).Contains(parentPrivate, StringComparison.Ordinal),
                    key + ": history keeps its local accepted sources without inventing an owner anchor or importing parent context");

                var claimed = workflows.TryClaim(next.Id)!;
                var compactDescriptor = bridge.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && item.Descriptor.Version == 1).Descriptor;
                var prepared = workflows.ApplyStep(claimed.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(WorkerWorkflowEffects.CompactCommand, 1,
                        WorkflowContract.EmptyObject)), compactDescriptor);
                var dispatched = workflows.DispatchEffect(prepared.Id, prepared.Revision, 1000);
                var checkpoint = new ContextCheckpoint(dispatched.Effect!.Id, next.Submission.ConversationSequence, 0,
                    [new(priorMessage.Id, priorMessage.Sequence)], key + "-LOCAL-CHECKPOINT", dispatched.Effect.Id, true);
                workflows.CompleteEffect(dispatched.Id, dispatched.Effect.Id,
                    new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject) { ConversationCheckpoint = checkpoint }, 1);
                var retained = workflows.GetConversationCheckpoint(conversation, next.Submission.ConversationSequence);
                var withCheckpoint = await Page(next, new { });
                Check(retained?.Checkpoint.Id == checkpoint.Id && withCheckpoint.Context.Checkpoint?.Id == checkpoint.Id &&
                    withCheckpoint.Context.Contributions.All(item => !item.Id.StartsWith("idle-goal-", StringComparison.Ordinal)) &&
                    !Text(withCheckpoint.Context).Contains(parentPrivate, StringComparison.Ordinal),
                    key + ": actual committed checkpoint remains usable without a User goal or parent-source adoption");
            }

            await CheckNonOwnerHistory("delegated-history", new(EventActorKind.Service, "cerebral.coordination"),
                childProfile, historyParent.Id);
            await CheckNonOwnerHistory("service-history", new(EventActorKind.Service, "history-fixture"), profile);
            await CheckNonOwnerHistory("agent-history", new(EventActorKind.Agent, profile.Id), profile);
            workflows.Cancel(historyParent.Id);

            var automaticRun = workflows.Submit(new("automatic-context-run", "automatic-context-chat", automaticProfile.Id,
                "fixture:conversation", "1.0.0", "USE THE MEASURED AUTOMATIC CONTEXT", null,
                Element(new { }), automaticSnapshotJson));
            var automaticGeneration = await generate.ExecuteAsync(Context(automaticRun,
                new WorkerWorkflowEffects.GenerateArguments(
                    [new("system", [new TextContentPart("AUTOMATIC CONTEXT POLICY")])], ToolNames: [])), default);
            Check(automaticGeneration.State == WorkflowEffectState.Succeeded &&
                models.Last is { ContextLimit: 48_128, ResponseLimit: 12_032 },
                "an actual generation request retains the unloaded model's measured 48,128-token context instead of collapsing to its 16,384-token import recommendation");

            var first = Append("prior-user", "chat", "EARLIER USER", inclusion: ModelInclusion.Required);
            var hidden = Append("hidden-tool", "chat", "", hidden: true, inclusion: ModelInclusion.Evidence,
                type: CerebralEventTypes.ToolResult, data: Element(new { content = "HIDDEN ACTUAL TOOL RESULT", exitCode = 0 }));
            Append("excluded", "chat", "EXCLUDED CANARY", excluded: true);
            Append("never", "chat", "NEVER CANARY", inclusion: ModelInclusion.Never);
            Append("on-demand", "chat", "ON DEMAND REFERENCE", inclusion: ModelInclusion.OnDemand);
            var run = await Submit("chat-run", "chat");
            Check(run.Submission.ConversationSequence == 5, "admission snapshots the prior journal sequence before current input");
            var later = Append("later-event", "chat", "LATER CANARY");
            var other = Append("foreign-event", "other-chat", "FOREIGN CANARY");
            var data = await Page(run, new { });
            Check(data.SnapshotSequence == 5 && data.Context.Revision == 5 && data.Context.ExclusionRevision == 0 && !data.HasOlder,
                "history remains pinned after later events arrive");
            Check(data.Context.Contributions.Select(x => x.Id).SequenceEqual([first.Id, hidden.Id]),
                "history includes hidden model evidence and omits excluded, Never and unrequested OnDemand material");
            Check(data.Context.Contributions.All(x => x.BeforeCurrentInput && x.Requirement == ContextRequirement.Reducible && x.ExactReadHandle == x.Id) &&
                data.Context.Contributions.Select(x => x.SourceRevision).SequenceEqual([1L, 2L]),
                "historical sources have chronological revision coordinates and exact source handles");
            Check(Text(data.Context).Contains("HIDDEN ACTUAL TOOL RESULT", StringComparison.Ordinal) &&
                !Text(data.Context).Contains("CANARY", StringComparison.Ordinal), "structured hidden tool results are retained in model context");
            var responseSource = Append("response-source", "output-history", "", hidden: true, inclusion: ModelInclusion.Evidence,
                type: "workflow.result.source", parts: [new StructuredContentPart("workflow.result", Element(new { summary = "ORIGINAL RESPONSE" }))],
                actorKind: EventActorKind.Agent);
            Append("response-presentation", "output-history", "", inclusion: ModelInclusion.Never, type: "workflow.result",
                parts: [new StructuredContentPart("workflow.result", Element(new { summary = "PRESENTED RESPONSE" }))], actorKind: EventActorKind.Agent);
            var responseRun = await Submit("output-history-run", "output-history");
            var responseContext = await Page(responseRun, new { });
            Check(responseContext.Context.Contributions.Single() is { } projectedResponse &&
                projectedResponse.Id == responseSource.Id && projectedResponse.Category == "conversation" &&
                projectedResponse.ExactReadHandle == responseSource.Id &&
                projectedResponse.Messages.Single().Role == "assistant" &&
                Text(responseContext.Context).Contains("ORIGINAL RESPONSE", StringComparison.Ordinal) &&
                !Text(responseContext.Context).Contains("PRESENTED RESPONSE", StringComparison.Ordinal),
                "hidden response source reaches the actual model projection as assistant context while presentation stays model-never");
            var recent = await Page(run, new { limit = 2 });
            var older = await Page(run, new { beforeSequence = recent.BeforeSequence, limit = 2 });
            var oldest = await Page(run, new { beforeSequence = older.BeforeSequence, limit = 2 });
            Check(recent.Context.Contributions.Count == 0 && recent.HasOlder && older.Context.Contributions.Single().Id == hidden.Id &&
                oldest.Context.Contributions.Single().Id == first.Id && !oldest.HasOlder,
                "pagination advances through excluded rows without losing included earlier entries");
            var invalid = await history.ExecuteAsync(Context(run, new { beforeSequence = 7 }), default);
            Check(invalid.State == WorkflowEffectState.Failed &&
                (await history.ExecuteAsync(Context(run, new { beforeSequence = 0 }), default)).State == WorkflowEffectState.Failed &&
                (await history.ExecuteAsync(Context(run, new { limit = 49 }), default)).State == WorkflowEffectState.Failed,
                "future cursors, zero cursors and oversized page requests are rejected");
            var source = await history.ExecuteAsync(Context(run, new { eventId = hidden.Id }), default);
            WorkflowSchema.Validate(source.Data!.Value, history.Descriptor.OutputSchema);
            Check(source.State == WorkflowEffectState.Succeeded && source.Data.Value.GetProperty("json").GetString()!.Contains("HIDDEN ACTUAL TOOL RESULT", StringComparison.Ordinal),
                "exact lookup reads the saved source instead of re-executing its tool");
            Check((await history.ExecuteAsync(Context(run, new { eventId = "on-demand" }), default)).State == WorkflowEffectState.Succeeded,
                "explicit exact lookup can retrieve OnDemand reference data");
            foreach (var id in new[] { "excluded", "never", other.Id, later.Id, "missing" })
            {
                var rejected = await history.ExecuteAsync(Context(run, new { eventId = id }), default);
                Check(rejected.State == WorkflowEffectState.Failed && rejected.Error == "Conversation source is unavailable in this accepted snapshot.",
                    "exact lookup denies unavailable source without disclosing whether it exists: " + id);
            }
            Check((await history.ExecuteAsync(Context(run, new { eventId = hidden.Id, beforeSequence = 2 }), default)).State == WorkflowEffectState.Failed &&
                (await history.ExecuteAsync(Context(run, new { offset = 1 }), default)).State == WorkflowEffectState.Failed,
                "exact and page cursor modes cannot be combined");
            var wrongOwner = Context(run, new { }) with { Submission = run.Submission with { ConversationId = "other-chat" } };
            Check((await history.ExecuteAsync(wrongOwner, default)).State == WorkflowEffectState.Failed &&
                (await catalog.ExecuteAsync(wrongOwner, default)).State == WorkflowEffectState.Failed &&
                (await history.ExecuteAsync(Context(run, new { }) with { RunId = null }, default)).State == WorkflowEffectState.Failed,
                "history and catalog enforce host-bound same-run conversation ownership");
            var forgedBoundary = Context(run, new { }) with { Submission = run.Submission with { ConversationSequence = later.Sequence } };
            var forgedResult = await history.ExecuteAsync(forgedBoundary, default);
            Check(forgedResult.Data!.Value.GetProperty("snapshotSequence").GetInt64() == 5,
                "a supplied submission cannot expand the saved history boundary");

            var generation = await generate.ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments(
                [new("system", [new TextContentPart("HARNESS POLICY")]), new("user", [new TextContentPart("LATEST CURRENT TOOL FEEDBACK")])],
                ToolNames: [], Context: data.Context)), default);
            var assembled = models.Last!.Messages.Select(x => string.Concat(x.Content.OfType<TextContentPart>().Select(p => p.Text))).ToArray();
            var retainedGeneration = generation.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            Check(retainedGeneration.Generation.PromptCache is null && retainedGeneration.Generation.EvaluatedTokens == 42 &&
                store.ReadPromptCacheDiagnostics().Any(x => x.ModelId == profile.ModelId && x.CachedTokens == 21),
                "cache metrics live only in rolling history; durable generation keeps full token accounting without cache telemetry");
            Check(generation.State == WorkflowEffectState.Succeeded && assembled[1] == "HARNESS POLICY" &&
                assembled[2].StartsWith("Host-provided lookup coordinates", StringComparison.Ordinal) &&
                models.Last.Messages[2].Role == "system" &&
                assembled[3].Contains("EARLIER USER", StringComparison.Ordinal) && assembled[4].Contains("HIDDEN ACTUAL TOOL RESULT", StringComparison.Ordinal) &&
                assembled[5] == "EXACT CURRENT REQUEST" && assembled[6].StartsWith("Internal workflow assignment:", StringComparison.Ordinal) &&
                assembled[6].EndsWith("HIDDEN CURRENT ASSIGNMENT", StringComparison.Ordinal) && assembled[^1] == "LATEST CURRENT TOOL FEEDBACK",
                "actual host preparation orders policy, earlier context, immutable request and latest current feedback");
            Check(!assembled[0].Contains("Accepted semantic tool descriptions", StringComparison.Ordinal) &&
                !string.Join('\n', assembled).Contains("fixture-private", StringComparison.Ordinal),
                "empty ToolNames suppresses built-in catalog injection and private settings do not enter context");

            var catalogResult = await catalog.ExecuteAsync(Context(run, new { }), default);
            WorkflowSchema.Validate(catalogResult.Data!.Value, catalog.Descriptor.OutputSchema);
            var compact = catalogResult.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogData>(CerebralJson.Options)!;
            Check(compact.Tools.Count == 64 && compact.Truncated && compact.Tools.All(x => x.InputSchema is null && x.Description.Length <= 256),
                "discovery returns bounded compact entries without schemas");
            var exactCatalog = await catalog.ExecuteAsync(Context(run, new { names = new[] { "file.read" } }), default);
            WorkflowSchema.Validate(exactCatalog.Data!.Value, catalog.Descriptor.OutputSchema);
            Check(exactCatalog.Data.Value.GetProperty("tools")[0].GetProperty("inputSchema").GetProperty("type").GetString() == "object",
                "explicit exact name returns the frozen schema");
            Check(exactCatalog.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogData>(CerebralJson.Options)!.Tools.Single()
                    .PrerequisiteTools!.SequenceEqual(["file.fixture0"]) && tools.Executions == 0,
                "exact catalog transports only permitted preparation hints without executing related tools");
            var v1FullDescription = await catalog.ExecuteAsync(Context(run, new { names = new[] { "file.fixture0" } }), default);
            Check(v1FullDescription.State == WorkflowEffectState.Succeeded &&
                  v1FullDescription.Data!.Value.GetProperty("tools")[0].GetProperty("description").GetString()!.Length == 300 &&
                  compact.Tools.Single(tool => tool.Name == "file.fixture0").Description.Length == 256,
                "ordinary exact-name catalog retains the full descriptor while its summary remains concise");
            var catalogV2 = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CatalogCommand && x.Descriptor.Version == 2);
            var firstPage = await catalogV2.ExecuteAsync(Context(run, new { }), default);
            WorkflowSchema.Validate(firstPage.Data!.Value, catalogV2.Descriptor.OutputSchema);
            var firstCatalogPage = firstPage.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(firstCatalogPage.SchemaVersion == 2 && firstCatalogPage.Mode == "page" && firstCatalogPage.Tools.Count == 64 &&
                firstCatalogPage.Truncated && firstCatalogPage.Remaining == 7 && firstCatalogPage.NextAfterName == firstCatalogPage.Tools[^1].Name &&
                firstCatalogPage.Tools.All(item => item.InputSchema is null && item.Name != "file.service-sink") &&
                firstCatalogPage.Request.ValueKind == JsonValueKind.Object && !firstCatalogPage.Request.EnumerateObject().Any(),
                "v2 page reports exact remaining rows and its last actually emitted cursor without exposing schemas or service sinks");
            var secondPage = await catalogV2.ExecuteAsync(Context(run, new { afterName = firstCatalogPage.NextAfterName }), default);
            WorkflowSchema.Validate(secondPage.Data!.Value, catalogV2.Descriptor.OutputSchema);
            var second = secondPage.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(second.Tools.Count == 7 && !second.Truncated && second.Remaining == 0 && second.NextAfterName is null &&
                second.Request.GetProperty("afterName").GetString() == firstCatalogPage.NextAfterName &&
                firstCatalogPage.Tools.Concat(second.Tools).Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == 71,
                "v2 continuation reaches every accepted model-visible name once without treating the cursor as a lease");
            var queried = await catalogV2.ExecuteAsync(Context(run, new { query = "FIXTURE69" }), default);
            var filtered = queried.Data!.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(filtered.Tools.Select(item => item.Name).SequenceEqual(["file.fixture69"]) &&
                filtered.Request.GetProperty("query").GetString() == "FIXTURE69" && !filtered.Truncated &&
                filtered.Remaining == 0 && filtered.NextAfterName is null,
                "v2 bounded case-insensitive query returns a correlated terminal page");
            var v2Exact = await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.read" } }), default);
            WorkflowSchema.Validate(v2Exact.Data!.Value, catalogV2.Descriptor.OutputSchema);
            var selectedV2 = v2Exact.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(selectedV2.Mode == "names" && selectedV2.Tools.Single().InputSchema is { } selectedSchema &&
                selectedSchema.GetProperty("type").GetString() == "object" &&
                !selectedV2.Truncated && selectedV2.Remaining == 0 && selectedV2.NextAfterName is null &&
                selectedV2.Request.GetProperty("names")[0].GetString() == "file.read" && tools.Executions == 0,
                "v2 exact names return only frozen admitted schemas and never call a semantic tool");
            var v2FullDescription = await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.fixture0" } }), default);
            Check(v2FullDescription.State == WorkflowEffectState.Succeeded &&
                  v2FullDescription.Data!.Value.GetProperty("tools")[0].GetProperty("description").GetString()!.Length == 300 &&
                  firstCatalogPage.Tools.Single(tool => tool.Name == "file.fixture0").Description.Length == 256,
                "v2 exact-name catalog preserves full argument guidance without enlarging summary pages");
            tools.HugeReadDescription = true;
            var oversizedRun = await Submit("oversized-exact-description", "oversized-exact-description");
            var oversizedV1 = await catalog.ExecuteAsync(Context(oversizedRun, new { names = new[] { "file.read" } }), default);
            var oversizedV2 = await catalogV2.ExecuteAsync(Context(oversizedRun, new { names = new[] { "file.read" } }), default);
            Check(oversizedV1.State == WorkflowEffectState.Failed && oversizedV2.State == WorkflowEffectState.Failed &&
                  tools.Executions == 0,
                "both exact-name catalogs reject descriptions exceeding the existing 48 KiB response budget");
            tools.HugeReadDescription = false;
            Check((await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.service-sink" } }), default)).State == WorkflowEffectState.Failed &&
                (await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.read", "file.read" } }), default)).State == WorkflowEffectState.Failed &&
                (await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.read" }, query = "read" }), default)).State == WorkflowEffectState.Failed &&
                (await catalogV2.ExecuteAsync(Context(run, new { query = " " }), default)).State == WorkflowEffectState.Failed &&
                tools.Executions == 0,
                "v2 refuses hidden sinks, duplicate names, mixed modes and invalid queries before any callback");
            using (var duplicatePageRequest = JsonDocument.Parse("{\"query\":\"file\",\"query\":\"read\"}"))
                Check((await catalogV2.ExecuteAsync(Context(run, duplicatePageRequest.RootElement), default)).State == WorkflowEffectState.Failed,
                    "v2 refuses duplicate request fields even when invoked directly without registry schema validation");
            tools.HeavyHints = true;
            var heavyRun = await Submit("heavy-catalog-run", "heavy-catalog");
            var heavyEffect = await catalogV2.ExecuteAsync(Context(heavyRun, new { }), default);
            WorkflowSchema.Validate(heavyEffect.Data!.Value, catalogV2.Descriptor.OutputSchema);
            var heavyPage = heavyEffect.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(heavyPage.Tools.Count is > 0 and < 64 && heavyPage.Truncated &&
                heavyPage.Remaining == 79 - heavyPage.Tools.Count &&
                heavyPage.NextAfterName == heavyPage.Tools[^1].Name &&
                JsonSerializer.SerializeToUtf8Bytes(heavyEffect.Data.Value).Length <= WorkerWorkflowEffects.MaximumConversationEffectBytes &&
                tools.Executions == 0,
                "v2 byte trimming advances only to its last emitted row and reports the exact remaining count");
            tools.HeavyHints = false;
            tools.IncludeNew = true;
            Check((await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.new" } }), default)).State == WorkflowEffectState.Failed,
                "v2 exact lookup cannot add a tool installed after workflow admission");
            Check((await catalog.ExecuteAsync(Context(run, new { names = new[] { "file.new" } }), default)).State == WorkflowEffectState.Failed,
                "newly discovered tools cannot widen an accepted catalog");
            tools.ChangeRead = true;
            Check((await catalogV2.ExecuteAsync(Context(run, new { names = new[] { "file.read" } }), default)).State == WorkflowEffectState.Failed,
                "v2 exact lookup refuses a changed live descriptor");
            Check((await catalog.ExecuteAsync(Context(run, new { names = new[] { "file.read" } }), default)).State == WorkflowEffectState.Failed,
                "changed live descriptors cannot replace accepted schemas");
            tools.ChangeRead = false;
            profile.AllowedTools = ["file.read"]; store.Put("agent", profile.Id, profile);
            var narrowed = await catalog.ExecuteAsync(Context(run, new { }), default);
            var narrowedV2 = await catalogV2.ExecuteAsync(Context(run, new { afterName = firstCatalogPage.NextAfterName }), default);
            var narrowedV2Data = narrowedV2.Data!.Value.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            Check(narrowedV2Data.Tools.Select(item => item.Name).SequenceEqual(["file.read"]) &&
                narrowedV2Data.Remaining == 0 && !narrowedV2Data.Truncated && narrowedV2Data.NextAfterName is null,
                "v2 seek position survives current grant removals without reviving earlier rows");
            Check(narrowed.Data!.Value.GetProperty("tools").GetArrayLength() == 1, "current grants narrow compact discovery");
            WorkflowSchema.Validate(narrowed.Data.Value, catalog.Descriptor.OutputSchema);
            Check(narrowed.Data.Value.Deserialize<WorkerWorkflowEffects.CatalogData>(CerebralJson.Options)!.Tools.Single()
                    .PrerequisiteTools is { Count: 0 },
                "revoked related tools disappear from preparation hints even when the parent tool remains permitted");
            tools.OnList = () => { profile.Capabilities = []; store.Put("agent", profile.Id, profile); };
            Check((await catalog.ExecuteAsync(Context(run, new { names = new[] { "file.read" } }), default)).State == WorkflowEffectState.Failed,
                "revocation during asynchronous discovery is checked again before output");
            tools.OnList = null; profile.Capabilities = ["files"]; profile.AllowedTools = ["*"]; store.Put("agent", profile.Id, profile);
            Check((await catalog.ExecuteAsync(Context(run, new { names = Array.Empty<string>() }), default)).State == WorkflowEffectState.Failed &&
                (await catalog.ExecuteAsync(Context(run, new { names = Enumerable.Repeat("file.read", 9).ToArray() }), default)).State == WorkflowEffectState.Failed,
                "schema discovery rejects empty and oversized explicit name lists");

            var unicode = string.Concat(Enumerable.Repeat("é中 ", 2300)) + "🦄 retained end";
            Append("unicode", "large", unicode);
            var large = await Submit("large-run", "large");
            var excerpt = await Page(large, new { });
            Check(excerpt.Context.Contributions.Single() is { Complete: false, ExactReadHandle: "unicode" },
                "large source projection explicitly declares an excerpt and retains exact lookup");
            var recovered = new StringBuilder(); var offset = 0; var pageCount = 0;
            while (true)
            {
                var result = await history.ExecuteAsync(Context(large, new { eventId = "unicode", offset }), default);
                if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException(result.Error);
                var fragment = result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistorySourceData>(CerebralJson.Options)!;
                Check(fragment.Offset == offset && Encoding.UTF8.GetByteCount(fragment.Json) <= 4096 && !fragment.Json.Contains('\ufffd'),
                    "exact source page respects UTF-8 boundaries");
                recovered.Append(fragment.Json);
                if (fragment.NextOffset is not { } next) break;
                if (next <= offset || ++pageCount > 20) throw new InvalidOperationException("Unbounded exact source paging.");
                offset = next;
            }
            using (var decoded = JsonDocument.Parse(recovered.ToString()))
                Check(decoded.RootElement.GetProperty("content")[0].GetProperty("text").GetString() == unicode,
                    "exact source pages reconstruct all retained Unicode content");
            var bytes = Encoding.UTF8.GetBytes(recovered.ToString());
            var continuation = Array.FindIndex(bytes, b => (b & 0xc0) == 0x80);
            Check(continuation >= 0 && (await history.ExecuteAsync(Context(large, new { eventId = "unicode", offset = continuation }), default)).State == WorkflowEffectState.Failed,
                "exact source rejects an offset inside a UTF-8 character");

            for (var index = 0; index < 60; index++) Append("bounded-" + index, "bounded", new string('x', 6000));
            var bounded = await Submit("bounded-run", "bounded");
            var boundedResult = await history.ExecuteAsync(Context(bounded, new { limit = 48 }), default);
            var boundedData = boundedResult.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            Check(JsonSerializer.SerializeToUtf8Bytes(boundedResult.Data.Value).Length <= WorkerWorkflowEffects.MaximumConversationEffectBytes &&
                boundedData.HasOlder && boundedData.Context.Contributions.Count is > 0 and < 48,
                "serialized history stays within 48 KiB with an honest earlier cursor");
            var boundedOlder = await Page(bounded, new { beforeSequence = boundedData.BeforeSequence, limit = 48 });
            Check(boundedOlder.Context.Contributions.All(sourceItem => sourceItem.SourceRevision < boundedData.Context.Contributions[0].SourceRevision),
                "byte-bound pagination resumes before the oldest returned event without duplicates");
            Append("image-reference", "binary", "", parts: [new ImageContentPart(new("image", 1, "image/png", "image.png", 32))]);
            var binary = await Page(await Submit("binary-run", "binary"), new { });
            Check(binary.Context.Contributions[0].Complete && binary.Context.Contributions[0].Messages
                .SelectMany(message => message.Content).OfType<ImageContentPart>().Single().Attachment is { Id: "image", Revision: 1 } &&
                !Text(binary.Context).Contains("image/png", StringComparison.Ordinal),
                "historical image coordinates remain typed without flattening metadata into text or claiming inference");

            // Exercise the journal's real call/result envelopes. Internal preparation and read effects
            // must retain exact evidence without feeding copied snapshots recursively into later turns.
            var priorRun = await Submit("projection-prior", "projection");
            var internalCalls = new List<(string Command, string Effect)>();
            foreach (var commandId in new[] { WorkerWorkflowEffects.GenerateCommand, WorkerWorkflowEffects.HistoryCommand,
                WorkerWorkflowEffects.CatalogCommand, WorkerWorkflowEffects.CompactCommand, WorkerWorkflowEffects.ReceiptCommand })
            {
                var claimed = workflows.TryClaim(priorRun.Id)!;
                var command = bridge.Commands.Single(handler => handler.Descriptor.Id == commandId && handler.Descriptor.Version == 1).Descriptor;
                var prepared = workflows.ApplyStep(claimed.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(commandId, 1,
                        Element(new { nestedSnapshot = "INTERNAL-SNAPSHOT-CANARY" }))), command);
                var dispatched = workflows.DispatchEffect(prepared.Id, prepared.Revision);
                workflows.CompleteEffect(dispatched.Id, dispatched.Effect!.Id, new(WorkflowEffectState.Succeeded,
                    Element(new { context = new { sourceText = "INTERNAL-SNAPSHOT-CANARY" }, catalog = "INTERNAL-SNAPSHOT-CANARY" })), 1);
                internalCalls.Add((commandId, dispatched.Effect.Id));
            }
            var semanticClaim = workflows.TryClaim(priorRun.Id)!;
            var semanticPrepared = workflows.ApplyStep(semanticClaim.Id, semanticClaim.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1,
                    Element(new WorkerWorkflowEffects.ToolArguments("file.read", Element(new { path = "ACTUAL-SEMANTIC-PATH" }))))),
                bridge.Commands.Single(handler => handler.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor);
            var semanticDispatched = workflows.DispatchEffect(semanticPrepared.Id, semanticPrepared.Revision);
            workflows.CompleteEffect(semanticDispatched.Id, semanticDispatched.Effect!.Id, new(WorkflowEffectState.Succeeded,
                Element(new WorkerWorkflowEffects.ToolData("file.read", "ACTUAL-SEMANTIC-RESULT", false,
                    new(ToolExecutionOutcome.Succeeded, "fixture", semanticDispatched.Effect.Id, ExitCode: 0)))), 1);
            var finalClaim = workflows.TryClaim(priorRun.Id)!;
            workflows.ApplyStep(finalClaim.Id, finalClaim.Revision, new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(Element(new { summary = "ACTUAL-ASSISTANT-ANSWER" }))));
            var followup = await Submit("projection-followup", "projection");
            var projected = await Page(followup, new { limit = 48 });
            var projectedText = Text(projected.Context);
            Check(!projectedText.Contains("INTERNAL-SNAPSHOT-CANARY", StringComparison.Ordinal) &&
                internalCalls.All(call => projectedText.Contains(call.Command, StringComparison.Ordinal) && projectedText.Contains(call.Effect, StringComparison.Ordinal)),
                "internal context effects remain compact provenance references without recursively replaying nested snapshots");
            Check(projectedText.Contains("ACTUAL-SEMANTIC-PATH", StringComparison.Ordinal) &&
                projectedText.Contains("ACTUAL-SEMANTIC-RESULT", StringComparison.Ordinal) &&
                projectedText.Contains("ACTUAL-ASSISTANT-ANSWER", StringComparison.Ordinal) &&
                projectedText.Contains("HIDDEN CURRENT ASSIGNMENT", StringComparison.Ordinal),
                "semantic tool arguments, real outcomes, assistant response and hidden assignment stay in model history");
            var savedInternal = journal.ReadPreviousPage("projection", limit: 100,
                snapshotSequence: followup.Submission.ConversationSequence).Events.First(item =>
                    item.Type == CerebralEventTypes.ToolCall && item.Data?.GetProperty("toolName").GetString() == WorkerWorkflowEffects.GenerateCommand);
            var exactInternal = await history.ExecuteAsync(Context(followup, new { eventId = savedInternal.Id }), default);
            Check(exactInternal.State == WorkflowEffectState.Succeeded &&
                exactInternal.Data!.Value.GetProperty("json").GetString()!.Contains("INTERNAL-SNAPSHOT-CANARY", StringComparison.Ordinal) &&
                projected.Context.Contributions.Single(item => item.Id == savedInternal.Id) is { Complete: false },
                "original internal snapshots remain available through their explicit exact source handle");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await history.ExecuteAsync(Context(run, new { }), cancelled.Token)).State == WorkflowEffectState.Cancelled &&
                (await catalog.ExecuteAsync(Context(run, new { }), cancelled.Token)).State == WorkflowEffectState.Cancelled,
                "cancelled lookup does not proceed");
            Check(tools.Executions == 0 && models.Generated == 2, "history and discovery perform no tool execution or extra generation");

            var skills = new SkillCatalog(store);
            var skill = skills.Install(new SkillManifest { Id = "fence-check", Name = "Fence check", Version = "1.0.0",
                Description = "Check instruction admission.", Triggers = ["fence-check"] },
                "FROZEN-SKILL-CANARY", "fixture", null);
            skill = skills.Configure(skill.Summary.Id, true, [new("profile", profile.Id)], skill.Summary.Revision);
            var skillSnapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, configuration, tools,
                [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], default);
            var snapshotPin = skillSnapshot.GetRawText();
            WorkflowRun SkillRun(string id) => workflows.Submit(new(id, id + "-chat", profile.Id, "fixture:conversation", "1.0.0",
                "fence-check", null, Element(new { }), skillSnapshot));
            var skillModels = new Models();
            var skillBridge = new WorkerWorkflowEffects(store, configuration, skillModels, new Tools());
            var skillGenerate = skillBridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var skillArgs = new WorkerWorkflowEffects.GenerateArguments([new("user", [new TextContentPart("fence-check")])], ToolNames: []);
            skill = skills.Configure(skill.Summary.Id, false, skill.Summary.Bindings, skill.Summary.Revision);
            var disabledInput = await skillGenerate.ExecuteAsync(Context(SkillRun("skill-disabled"), skillArgs), default);
            Check(disabledInput.State == WorkflowEffectState.Succeeded && skillModels.Generated == 1 &&
                  !skillModels.Last!.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                      .Any(part => part.Text.Contains("FROZEN-SKILL-CANARY", StringComparison.Ordinal)),
                "workflow future input filters disabled guidance from its frozen accepted snapshot");

            skill = skills.Configure(skill.Summary.Id, true, skill.Summary.Bindings, skill.Summary.Revision);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            skillModels.BeforePrepareAsync = async token =>
            { entered.TrySetResult(true); await release.Task.WaitAsync(token); };
            var disposedBefore = skillModels.Disposed;
            using (var skillDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = skillGenerate.ExecuteAsync(Context(SkillRun("skill-preparing"), skillArgs), skillDeadline.Token);
                try
                {
                    await entered.Task.WaitAsync(skillDeadline.Token);
                    skill = skills.Configure(skill.Summary.Id, false, skill.Summary.Bindings, skill.Summary.Revision);
                }
                finally { release.TrySetResult(true); }
                var rejected = await pending;
                Check(rejected.State == WorkflowEffectState.Failed && skillModels.Generated == 1 &&
                      skillModels.Disposed == disposedBefore + 1 &&
                      skillModels.Last!.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                          .Any(part => part.Text.Contains("FROZEN-SKILL-CANARY", StringComparison.Ordinal)) &&
                      skillSnapshot.GetRawText() == snapshotPin,
                    "workflow validates the actual prepared skill input, releases it after revocation and preserves the accepted snapshot");
            }

            // First-use host normalization is not an external profile edit. Keep its timing separate
            // from the real overlapping edit below so the final equality gate remains meaningful.
            var admissionStore = new StateStore(Path.Combine(root, "admission"));
            var admissionConfiguration = new Configuration(admissionStore);
            admissionConfiguration.Get();
            var lead = new AgentProfile
            {
                Id = "lead", Name = "Cerebral", Role = "lead", Revision = 7, ModelId = "admission-model",
                AutomaticCompaction = false, FollowsDefaultModel = false, FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 512, AllowedTools = ["*"], Capabilities = ["files"], AllowedRoots = [root]
            };
            admissionStore.Put("agent", lead.Id, lead);
            admissionStore.Put("model", lead.ModelId, new ModelRecord
            {
                Id = lead.ModelId, Path = Path.Combine(root, "admission-model.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            var admissionTools = new Tools();
            var admissionBridge = new WorkerWorkflowEffects(admissionStore, admissionConfiguration, new Models(), admissionTools);
            var admissionHost = new WorkerWorkflows(admissionStore, admissionConfiguration, new EventHub(),
                [admissionBridge, new ConversationWorkflowModule()],
                [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []), ConversationWorkflowModule.PackageBinding], admissionTools);
            SubmitWorkflowRequest Admission(string id) => new(id, id + "-conversation", lead.Id,
                ConversationWorkflowModule.DefinitionId, ConversationAgentHarness.Version, "A direct answer please.", null, Element(new { }));
            Check(string.IsNullOrWhiteSpace(admissionStore.Get<HostSettings>("settings", "host")!.DefaultModelId) &&
                !admissionStore.Get<AgentProfile>("agent", lead.Id)!.FollowsDefaultModel,
                "first admission fixture begins with sole lead and no normalized default model");
            var firstAdmission = await admissionHost.SubmitAsync(Admission("normalized-admission"), null, default);
            var normalizedLead = admissionStore.Get<AgentProfile>("agent", lead.Id)!;
            Check(firstAdmission.Status == WorkflowStatus.Queued && normalizedLead.FollowsDefaultModel &&
                normalizedLead.Revision == lead.Revision + 1 &&
                admissionStore.Get<HostSettings>("settings", "host")!.DefaultModelId == lead.ModelId &&
                WorkerExecutionSnapshot.Read(firstAdmission.Submission.SettingsSnapshot).Profile.Revision == normalizedLead.Revision,
                "first async admission completes lazy normalization before freezing the accepted profile");
            admissionHost.Store.Cancel(firstAdmission.Id);
            using var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            admissionTools.BeforeListAsync = async token => { listEntered.SetResult(); await releaseList.Task.WaitAsync(token); };
            var pendingAdmission = admissionHost.SubmitAsync(Admission("concurrent-profile-edit"), null, admissionDeadline.Token);
            await listEntered.Task.WaitAsync(admissionDeadline.Token);
            var editedLead = admissionStore.Get<AgentProfile>("agent", lead.Id)!;
            editedLead.Instructions = "A real profile edit while asynchronous discovery is pending.";
            editedLead.Revision++;
            admissionStore.Put("agent", editedLead.Id, editedLead);
            releaseList.SetResult();
            var concurrentRejected = false;
            try { await pendingAdmission; }
            catch (WorkflowConflictException error) when (error.Message.Contains("profile changed during admission", StringComparison.Ordinal))
            { concurrentRejected = true; }
            Check(concurrentRejected && admissionHost.Store.TryGet("concurrent-profile-edit") is null &&
                admissionHost.Journal.GetRevision("concurrent-profile-edit-conversation") == 0 &&
                admissionStore.Get<AgentProfile>("agent", lead.Id)!.Instructions == editedLead.Instructions,
                "a genuine profile edit during suspended discovery still rejects admission without inserting a run or losing the edit");
            admissionTools.BeforeListAsync = null;
            var afterEdit = await admissionHost.SubmitAsync(Admission("admission-after-edit"), null, default);
            Check(WorkerExecutionSnapshot.Read(afterEdit.Submission.SettingsSnapshot).Profile.Instructions == editedLead.Instructions,
                "a fresh admission accepts the revised profile after the rejected overlapping request");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-conversation-effects-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned test root.");
            Directory.Delete(root, recursive: true);
        }
        checks += await ToolGenerationEffectsTests.RunAsync();
        Console.WriteLine($"Conversation host bridge: {checks} SQLite/deterministic-adapter checks passed; no live inference or PAO.");
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static string Text(ContextWindow window) => string.Join('\n', window.Contributions.SelectMany(x => x.Messages)
        .SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
    private sealed class Tools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public bool IncludeNew, ChangeRead, HeavyHints, HugeReadDescription;
        public Action? OnList;
        public Func<CancellationToken, Task>? BeforeListAsync;
        public int Executions;
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OnList?.Invoke();
            if (BeforeListAsync is not null) await BeforeListAsync(token);
            var hintNames = Enumerable.Range(0, 8).Select(i => "file.hint" + i + new string('x', 55)).ToArray();
            var result = Enumerable.Range(0, 70).Select(i => new ToolDescriptor("file.fixture" + i,
                new string('d', 300), Element(new { type = "object", additionalProperties = false }), "files")
                { PrerequisiteTools = HeavyHints ? hintNames : Array.Empty<string>() }).ToList();
            if (HeavyHints)
                result.AddRange(hintNames.Select(name => new ToolDescriptor(name, "Read hint",
                    Element(new { type = "object", additionalProperties = false }), "files")));
            result.Add(new("file.read", HugeReadDescription ? new string('h', 50_000) : ChangeRead ? "changed read" : "Read a file",
                Element(new { type = "object" }), "files")
                { PrerequisiteTools = ["file.fixture0", "unavailable.tool"] });
            result.Add(new("file.service-sink", "Host-owned sink", Element(new { type = "object" }), "files")
                { ModelVisible = false });
            if (IncludeNew) result.Add(new("file.new", "New after admission", Element(new { type = "object" }), "files"));
            return result;
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("Lookup must not execute tools."); }
    }
    private sealed class Models : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("conversation-fixture", "1.0.0", ["text"]);
        public ModelGenerationRequest? Last;
        public int Generated;
        public int Disposed;
        public Func<CancellationToken, Task>? BeforePrepareAsync;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Last = request;
            if (BeforePrepareAsync is not null) await BeforePrepareAsync(token);
            return new Lease(this, request);
        }
        private sealed class Lease(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            private bool disposed;
            public PreparedModelMetadata Metadata { get; } = new("fixture", "conversation-fixture", "1.0.0", request.ModelId,
                42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            { token.ThrowIfCancellationRequested(); owner.Generated++; return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Fixture reply", 2, 42)
                { PromptCache = new(42, 21, 21, 100) }); }
            public ValueTask DisposeAsync()
            { if (!disposed) { disposed = true; owner.Disposed++; } return ValueTask.CompletedTask; }
        }
    }
}
