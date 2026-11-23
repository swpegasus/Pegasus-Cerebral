using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic host tests; token counts and generations here are supplied by a fake adapter.</summary>
public static class ContextCompactionTests
{
    public static async Task RunTaskBudgetAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-task-compaction-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
        try
        {
            var store = new StateStore(root);
            var config = new Configuration(store); config.Get();
            var profile = new AgentProfile
            {
                Id = "task-compaction", Role = "lead", ModelId = "fixture", FollowsModelBudgets = false,
                ContextTokens = 46080, ResponseTokens = 11520, ReasoningMode = "on", ReasoningTokens = 1024,
                AutomaticCompaction = true, AllowedTools = [], Capabilities = [],
                TaskBudgetPolicy = new(1, 49152, 24576, true)
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "absent.gguf"),
                ContextTokens = 46080, MaximumContextTokens = 46080, AutomaticContextSizing = false });
            var models = new MeasuredFixture { Summary = "oversized-summary" };
            bool IsCompactor(ModelGenerationRequest request) => request.ResponseSchema is { } schema &&
                schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("checkpoint", out _);
            models.CountOverride = request => IsCompactor(request) ? 500 :
                RequestText(request).Contains("old-history-body", StringComparison.Ordinal) ? 23000 :
                RequestText(request).Contains("oversized-summary", StringComparison.Ordinal) ? 21500 :
                RequestText(request).Contains("strict-summary", StringComparison.Ordinal) ? 12000 : 1000;
            var ownerRequests = new List<ModelGenerationRequest>();
            models.OnPrepare = request => { if (!IsCompactor(request)) ownerRequests.Add(request); };
            var tools = new NoTools();
            var bridge = new WorkerWorkflowEffects(store, config, models, tools);
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(store, profile, config, tools, [], default,
                requireCompactor: true);
            var submission = new WorkflowSubmission("task-compaction-run", "task-compaction-chat", profile.Id,
                "fixture:workflow", "1.0.0", "Keep the accepted task budget", null,
                Element(new { requestedContextTokens = 46080, requestedResponseTokens = 24576 }), snapshot,
                Caller: new(EventActorKind.User, "owner"));
            var journal = new Cerebral.Engine.Persistence.EventJournal(Path.Combine(root, "engine.db"));
            var accepted = new Cerebral.Engine.Persistence.WorkflowStore(journal).Submit(submission);
            var required = new ContextContribution("required", "fixture", "evidence", 1, ContextRequirement.Required,
                [new("user", [new TextContentPart("required-anchor")])]);
            var history = new ContextContribution("old", "fixture", "history", 1, ContextRequirement.Reducible,
                [new("user", [new TextContentPart("old-history-body")])], "old-effect");
            var window = new ContextWindow(2, 0, [history, required]);
            var legacyArgs = new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: window);
            var typedArgs = legacyArgs with { ContextLimit = 46080, ResponseLimit = 24576 };
            var commandJson = new JsonSerializerOptions(CerebralJson.Options)
                { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            JsonElement Envelope(WorkerWorkflowEffects.GenerateArguments args) => JsonSerializer.SerializeToElement(args, commandJson);
            WorkflowCommandContext Context(string effect, WorkerWorkflowEffects.GenerateArguments args) =>
                new(effect, accepted.Submission, Envelope(args)) { RunId = accepted.Id };
            var legacy = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1);
            var typed = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 2);
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 4);
            var legacyRejectedTyped = false;
            try { WorkflowSchema.Validate(Envelope(typedArgs), legacy.Descriptor.InputSchema); }
            catch (ArgumentException) { legacyRejectedTyped = true; }
            Check(legacyRejectedTyped, "Legacy compaction keeps its closed schema and cannot accept task overrides.");
            WorkflowSchema.Validate(Envelope(typedArgs), typed.Descriptor.InputSchema);
            Check(true, "Typed compaction uses the existing closed version-four generation input contract.");
            foreach (var malformed in new[]
            {
                Element(new { messages = Array.Empty<object>(), contextLimit = 46080 }),
                Element(new { messages = Array.Empty<object>(), contextLimit = 46080, responseLimit = 24576, responseSharePercent = 25 })
            })
            {
                var refused = false;
                try { WorkflowSchema.Validate(malformed, typed.Descriptor.InputSchema); }
                catch (ArgumentException) { refused = true; }
                Check(refused, "Incomplete or mixed legacy/typed budgets are refused by the closed v2 schema.");
            }

            // Control: a smaller input can fit the profile default while still overflowing the
            // next selected generation. This remains the legacy contract, not a valid v2 result.
            var control = await legacy.ExecuteAsync(Context("legacy-control", legacyArgs), default);
            var legacyData = control.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            Check(control.State == WorkflowEffectState.Succeeded && legacyData.Before.DesiredResponseBudget == 11520 &&
                legacyData.After.DesiredResponseBudget == 11520 && legacyData.After.InputTokens == 21500 &&
                (long)legacyData.After.InputTokens + 24576 + 1024 + legacyData.After.SafetyReserveTokens > 46080,
                "Legacy fit under the configured default does not prove fit under the selected next-generation budget.");
            ownerRequests.Clear();
            var rejected = await typed.ExecuteAsync(Context("typed-too-large", typedArgs), default);
            Check(rejected.State == WorkflowEffectState.Failed && rejected.ConversationCheckpoint is null &&
                models.Active == 0 && ownerRequests.All(request => request.ContextLimit == 46080 && request.ResponseLimit == 24576),
                "Typed compaction rejects an undersized reduction against the actual next budget without adopting a checkpoint or clamping it.");

            models.Summary = "strict-summary";
            ownerRequests.Clear();
            var result = await typed.ExecuteAsync(Context("typed-fit", typedArgs), default);
            Check(result.State == WorkflowEffectState.Succeeded, "A useful reduction can fit the admitted task budget.");
            var data = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            Check(data.Before.DesiredResponseBudget == 24576 && data.After.DesiredResponseBudget == 24576 &&
                data.After.ResponseBudget == 24576 && data.After.ReasoningReserveTokens == 1024 &&
                data.After.EffectiveContext == 46080 && data.After.InputTokens == 12000 && models.Active == 0,
                "Before and final admission retain selected context, visible output and reasoning instead of the configured default.");
            Check(ownerRequests.Count >= 4 && ownerRequests.All(request => request.ContextLimit == 46080 &&
                    request.ResponseLimit == 24576 && request.ReasoningTokens == 1024 && !request.AllowResponseClamping),
                "Full, floor, framing and final owner preparations use one exact policy without legacy capacity fallback.");
            Check(models.GeneratedCompactorRequests.All(request => request.ResponseLimit <= WorkerWorkflowEffects.MaximumCompactionResponseTokens &&
                request.ResponseLimit != 24576), "The compactor summary output keeps its separate bounded allowance.");
            var next = typedArgs with { Context = window with { Contributions = [required], Checkpoint = data.Checkpoint } };
            var beforeNext = models.Generations;
            var generated = await generate.ExecuteAsync(Context("next-generation", next), default);
            Check(generated.State == WorkflowEffectState.Succeeded && models.Generations == beforeNext + 1 && models.Active == 0,
                "The accepted candidate permits the next generation under the same budget without another pressure/compaction loop.");

            var beforeRefusal = models.PrepareCalls;
            var foreign = Context("foreign-owner", typedArgs) with
                { Submission = accepted.Submission with { ConversationId = "foreign-chat" } };
            var wrongOwner = await typed.ExecuteAsync(foreign, default);
            Check(wrongOwner.State == WorkflowEffectState.Failed && models.PrepareCalls == beforeRefusal,
                "A foreign submitted owner tuple refuses before preparation.");
            profile.TaskBudgetPolicy = profile.TaskBudgetPolicy! with { MaxResponseTokens = 20000 };
            store.Put("agent", profile.Id, profile);
            var revoked = await typed.ExecuteAsync(Context("revoked-current-ceiling", typedArgs), default);
            Check(revoked.State == WorkflowEffectState.Failed && models.PrepareCalls == beforeRefusal,
                "A tighter current owner ceiling rejects before any fake preparation or generation.");
            var uncertain = await typed.ReconcileAsync(Context("uncertain-compaction", typedArgs), default);
            Check(uncertain.State == WorkflowEffectState.Unknown && models.PrepareCalls == beforeRefusal,
                "Reconciliation preserves Unknown and never replays an uncertain compaction.");
            Check(new Cerebral.Engine.Persistence.WorkflowStore(journal).Get(accepted.Id).Revision == accepted.Revision,
                "These candidate checks never mutate the accepted workflow, checkpoint or effect receipts.");
            Console.WriteLine($"Task-budget compaction: {checks} disposable SQLite/deterministic adapter checks passed; no model, provider, installed state or network.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cerebral-task-compaction-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-context-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks++; }
        try
        {
            var store = new StateStore(root);
            var config = new Configuration(store); config.Get();
            var profile = new AgentProfile
            {
                Id = "context-fixture", ModelId = "fixture", FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 512, ReasoningMode = "off", AutomaticCompaction = true,
                CompactionReductionPercent = 50, AllowedTools = [], Capabilities = []
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "model.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, AutomaticContextSizing = false });
            var tools = new NoTools();
            var models = new MeasuredFixture();
            var diagnosticEvents = new EventHub();
            using var diagnosticLease = diagnosticEvents.SubscribeDiagnostics();
            var bridge = new WorkerWorkflowEffects(store, config, models, tools, diagnosticEvents: diagnosticEvents);
            Check(WorkerWorkflowEffects.TargetCompactionResponseTokens(12_000, 50) == 1_024 &&
                  WorkerWorkflowEffects.TargetCompactionResponseTokens(800, 50) == 400,
                "The proportional content target stays independent of the private complete-generation allowance.");
            var conciseBridge = new WorkerWorkflowEffects(new CompactorContentPolicy(256),
                store, config, models, tools, diagnosticEvents: diagnosticEvents);
            var conciseCompact = conciseBridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1);
            var generate = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var compact = bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1);
            async Task<WorkflowSubmission> Capture(bool requireCompactor = false) => new("fixture-request", "fixture-conversation", profile.Id,
                "fixture:workflow", "1.0.0", "EXACT USER REQUEST", "EXACT ASSIGNMENT", Element(new { }),
                await WorkerExecutionSnapshot.CaptureAsync(store, profile, config, tools, [], default, requireCompactor));
            var submission = await Capture();
            const string goal = "Goal: repair the failed build before retry.";
            var required = new ContextContribution("latest", "fixture", "tool", 1, ContextRequirement.Required,
                [new("user", [new TextContentPart("Build receipt exact-build failed. Retry budget remains one. Current file C:/project/Program.cs. " + goal)])]);
            var assistantHistory = new ContextContribution("assistant-response-event", "cerebral.host", "conversation", 1,
                ContextRequirement.Reducible, [new("assistant", [new TextContentPart("GB10 remote chat is working.")])],
                "assistant-response-event");
            var history = new ContextContribution("old", "fixture", "history", 1, ContextRequirement.Reducible,
                [new("user", [new TextContentPart(new string('x', 40000))])], "old-effect");
            var window = new ContextWindow(2, 0, [assistantHistory, history, required]);
            var originalWindow = Element(window);
            var originalSubmission = Element(submission);
            var focus = WorkerWorkflowEffects.BuildCompactionFocus(submission, window);
            Check(focus.Contains("EXACT USER REQUEST", StringComparison.Ordinal) &&
                  focus.Contains("EXACT ASSIGNMENT", StringComparison.Ordinal) &&
                  focus.Contains("Build receipt exact-build failed.", StringComparison.Ordinal) &&
                  focus.Contains(goal, StringComparison.Ordinal) &&
                  !focus.Contains("GB10 remote chat is working.", StringComparison.Ordinal) &&
                  focus.Length <= WorkerWorkflowEffects.MaximumCompactionFocusCharacters,
                "Compactor focus uses accepted current input and Required references without copying reducible history into the focus.");
            var longText = new string('q', 1_023) + "\uD83D\uDE00" + new string('r', 9_000);
            var longSources = Enumerable.Range(0, 12).Select(i => required with
                { Id = "long-" + i, Messages = [new("user", [new TextContentPart(longText)])] }).ToArray();
            var longFocus = WorkerWorkflowEffects.BuildCompactionFocus(submission with { UserText = longText },
                window with { Contributions = longSources });
            Check(longFocus.Length <= WorkerWorkflowEffects.MaximumCompactionFocusCharacters &&
                  longFocus.Contains("[focus excerpt truncated; full original remains unchanged]", StringComparison.Ordinal) &&
                  !longFocus.Contains('\uD83D') && !longFocus.Contains('\uDE00') &&
                  longSources.All(x => ((TextContentPart)x.Messages[0].Content[0]).Text == longText),
                "Focus bounds every excerpt and the whole frame, preserves Unicode boundaries and never clips original Required contributions.");
            var args = new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: window);
            WorkflowCommandContext Context(string id, WorkerWorkflowEffects.GenerateArguments? value = null) =>
                new(id, submission, Element(value ?? args));
            var unmeasuredDiagnostics = Element(new WorkerWorkflowEffects.CompactionDiagnostics());
            foreach (var rejectedContext in new[]
            {
                Context("diagnostics-missing-window", args with { Context = null }),
                new WorkflowCommandContext("diagnostics-invalid-json-shape", submission, Element(new { messages = "not a message list" })),
                Context("diagnostics-invalid-window", args with { Context = window with { Revision = -1 } })
            })
            {
                var preparationsBefore = models.PrepareCalls;
                var generationsBefore = models.Generations;
                var early = await compact.ExecuteAsync(rejectedContext, default);
                Check(early.State == WorkflowEffectState.Failed && early.ConversationCheckpoint is null &&
                      early.Data!.Value.TryGetProperty("diagnostics", out var numeric) &&
                      JsonElement.DeepEquals(numeric, unmeasuredDiagnostics) &&
                      early.Data.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Usage.Count == 0 &&
                      models.PrepareCalls == preparationsBefore && models.Generations == generationsBefore && models.Active == 0,
                    "Failure before validated measurement emits explicit null phases without preparing or generating: " + rejectedContext.EffectId);
            }
            var generation = await generate.ExecuteAsync(Context("first"), default);
            Check(generation.State == WorkflowEffectState.Failed && generation.Data!.Value.GetProperty("context").GetProperty("status").GetString() == "needsCompaction",
                "Exact preparation rejects history with a typed compaction outcome.");
            Check(models.Generations == 0 && models.Active == 0, "Budget rejection dispatches no inference and releases all prepared slots.");
            var result = await compact.ExecuteAsync(Context("compact-1"), default);
            if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException("Compaction fixture: " + result.Error);
            var data = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            Check(data.Passes > 1 && data.Before.InputTokens > data.After.InputTokens && models.Active == 0,
                "Measured chunking handles an oversized source and releases the single slot before the next pass.");
            Check(models.LastStructuredResponseSchema is { ValueKind: JsonValueKind.Object } schema &&
                  schema.GetProperty("type").GetString() == "object" &&
                  !schema.GetProperty("additionalProperties").GetBoolean() &&
                  schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["checkpoint"]) &&
                  schema.GetProperty("properties").EnumerateObject().Single().Name == "checkpoint" &&
                  schema.GetProperty("properties").GetProperty("checkpoint").GetProperty("type").GetString() == "string" &&
                  schema.GetProperty("properties").GetProperty("checkpoint").GetProperty("minLength").GetInt32() == 1 &&
                  schema.GetProperty("properties").GetProperty("checkpoint").GetProperty("maxLength").GetInt32() == 1536,
                "Default compaction keeps its closed proportional 1536-character checkpoint contract for the accepted 512-token content target.");
            Check(models.GeneratedCompactorRequests.Count == data.Passes &&
                  models.GeneratedCompactorRequests.All(request => request.ResponseLimit == 512 &&
                      RequestText(request).Contains("240 words", StringComparison.Ordinal)) &&
                  data.Usage.All(item => item.Prepared.ResponseBudget == 512),
                "Default compactor generation retains the accepted 512-token cap alongside its original proportional 240-word content target.");
            Check(JsonElement.DeepEquals(originalWindow, Element(window)) &&
                  JsonElement.DeepEquals(originalSubmission, Element(submission)) &&
                  ((TextContentPart)history.Messages.Single().Content.Single()).Text == new string('x', 40000),
                "Capture and summarization leave the exact accepted input goals source bodies and locators immutable.");
            Check(data.Checkpoint.CoveredSources.SequenceEqual(new[]
                { new ContextSourceReference("assistant-response-event", 1), new ContextSourceReference("old", 1) }) &&
                data.Checkpoint.CompactionEffectId == "compact-1",
                "Host manifest covers only reducible sources and binds the actual effect identity.");
            Check(data.Checkpoint.SourceLocators.SequenceEqual(ContextValidation.GetCoveredLocators(window)) &&
                  data.Checkpoint.SourceLocators.All(x => x.Id != required.Id),
                "Host retains exact source attribution and handles outside generated text while excluding Required anchors.");
            Check(models.GeneratedInputs.All(text =>
                    text.Contains("Bounded current-task focus", StringComparison.Ordinal) &&
                    text.Contains("EXACT USER REQUEST", StringComparison.Ordinal) &&
                    text.Contains("EXACT ASSIGNMENT", StringComparison.Ordinal) &&
                    text.Contains("Required source latest revision 1", StringComparison.Ordinal) &&
                    text.Contains(goal, StringComparison.Ordinal)),
                "Every compactor source slice receives the same bounded frozen current-task focus without answering it.");
            Check(models.GeneratedInputs.Any(text =>
                    text.Contains("SOURCE assistant-response-event revision 1", StringComparison.Ordinal) &&
                    text.Contains("owner cerebral.host", StringComparison.Ordinal) &&
                    text.Contains("category conversation", StringComparison.Ordinal) &&
                    text.Contains("exactReadHandle assistant-response-event", StringComparison.Ordinal) &&
                    text.Contains("ROLE assistant", StringComparison.Ordinal) &&
                    text.Contains("GB10 remote chat is working.", StringComparison.Ordinal) &&
                    text.Contains("not inherently a receipt", StringComparison.Ordinal) &&
                    text.Contains("conversation history is retrieved with history using its", StringComparison.Ordinal) &&
                    text.Contains("current-run execution evidence is retrieved with receipt using its effect ID", StringComparison.Ordinal) &&
                    !text.Contains("receipt assistant-response-event", StringComparison.Ordinal)),
                "Compaction preserves typed conversation source metadata and does not mislabel a history event as a receipt.");
            Check(result.ConversationCheckpoint is null && !data.Checkpoint.BeforeCurrentInput,
                "Same-turn tool history is compacted for the active run without becoming a later conversation baseline.");
            Check(data.Usage.All(x => x.Finish == ModelGenerationFinish.Stop), "Each retained compaction usage record has confirmed completion.");
            diagnosticLease.Dispose();
            var observed = new List<EngineDiagnosticRecord>();
            await foreach (var record in diagnosticLease.ReadAsync()) observed.Add(record);
            var compactEvents = observed.Where(x => x.EffectId == "compact-1").ToArray();
            var compactorProfile = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Compactor!.Profile;
            Check(compactEvents.Length == data.Passes * 3 + 1 && compactEvents.All(x =>
                    x.ChatId == "fixture-conversation" && x.RequestId == "fixture-request" &&
                    x.AgentId == compactorProfile.Id && x.WorkerId == compactorProfile.ModelWorkerId &&
                    x.ModelId == compactorProfile.ModelId),
                "Compactor diagnostics keep the captured chat and actual compactor identity across every bounded pass.");
            Check(compactEvents.Count(x => x.Phase == "compaction.prepared") == data.Passes &&
                  compactEvents.Count(x => x.Phase == "compaction.visible-fragment") == data.Passes &&
                  compactEvents.Count(x => x.Phase == "compaction.terminal" && x.Outcome == "Stop" &&
                      x.ElapsedMilliseconds >= 0 && x.GeneratedTokens == 24) == data.Passes &&
                  compactEvents.Last().Phase == "compaction.candidate-accepted" &&
                  compactEvents.Last().Outcome == "current-run",
                "Diagnostics distinguish visible summary output, confirmed terminal results and a current-run candidate from persisted adoption.");
            Check(compactEvents.Where(x => x.Phase == "compaction.prepared").Select(x => x.InputTokens)
                    .SequenceEqual(data.Usage.Select(x => (int?)x.Prepared.InputTokens)) &&
                  compactEvents.All(x => x.ResponseTokens > 0 && x.ContextTokens == data.Usage[0].Prepared.EffectiveContext) &&
                  compactEvents.All(x => !(x.Text ?? "").Contains("EXACT ASSIGNMENT", StringComparison.Ordinal)),
                "Diagnostic budgets are actual prepared metadata; private prompt assignment is never emitted.");
            models.PublishVisibleText = false;
            using (var terminalCapture = diagnosticEvents.SubscribeDiagnostics())
            {
                var terminalOnly = await compact.ExecuteAsync(Context("compact-terminal-only"), default);
                terminalCapture.Dispose();
                var records = new List<EngineDiagnosticRecord>();
                await foreach (var item in terminalCapture.ReadAsync()) records.Add(item);
                Check(terminalOnly.State == WorkflowEffectState.Succeeded &&
                    !records.Any(x => x.Phase == "compaction.visible-fragment") &&
                    records.Where(x => x.Phase == "compaction.terminal").All(x => !string.IsNullOrEmpty(x.Text)),
                    "Terminal-only compaction retains visible summaries at completion without simulated stream callbacks.");
                Check(records.All(x => x.ChatId == "fixture-conversation" &&
                    !(x.Text ?? "").Contains("EXACT ASSIGNMENT", StringComparison.Ordinal)),
                    "Terminal compaction capture keeps exact chat identity and excludes private prepared assignments.");
            }
            models.PublishVisibleText = true;
            var originalDefaultSummary = models.Summary;
            models.Summary = new string('d', 1024);
            var largerDefault = await compact.ExecuteAsync(Context("default-larger-summary"), default);
            Check(largerDefault.State == WorkflowEffectState.Succeeded &&
                  largerDefault.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!
                      .Checkpoint.Summary == models.Summary && models.Active == 0,
                "Default compaction still admits a complete summary above the private scenario's 768-character target without clipping it.");
            models.Summary = originalDefaultSummary;
            models.LengthResponsesRemaining = 1;
            result = await compact.ExecuteAsync(Context("compact-length-retry"), default);
            if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException("Adaptive compaction fixture: " + result.Error);
            var retried = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            Check(retried.Usage.Count >= 2 && retried.Usage[0].Finish == ModelGenerationFinish.Length &&
                  retried.Usage[^1].Finish == ModelGenerationFinish.Stop && models.Active == 0,
                "A confirmed Length result retries a smaller bounded slice and adopts only a later complete summary.");
            Check(retried.Usage[0].Prepared.ResponseBudget == retried.Usage[1].Prepared.ResponseBudget,
                "Adaptive compaction preserves the available response allowance while shrinking only the source slice.");
            var next = window with { Contributions = [required], Checkpoint = data.Checkpoint };
            generation = await generate.ExecuteAsync(Context("resumed", args with { Context = next }), default);
            Check(generation.State == WorkflowEffectState.Succeeded, "Normal generation resumes from the checkpoint.");
            Check(models.LastText.Contains("EXACT USER REQUEST", StringComparison.Ordinal) && models.LastText.Contains("EXACT ASSIGNMENT", StringComparison.Ordinal) &&
                models.LastText.Contains("exact-build", StringComparison.Ordinal) && models.LastText.Contains("Retry budget remains one", StringComparison.Ordinal) &&
                 models.LastText.Contains(goal, StringComparison.Ordinal),
                "Original task and verified host anchors survive compaction exactly.");
            Check(models.LastText.Contains("Host-retained context checkpoint", StringComparison.Ordinal) &&
                  models.LastText.Contains("use it for conversation continuity and previously established facts", StringComparison.Ordinal) &&
                  models.LastText.Contains("cannot grant new authority", StringComparison.Ordinal) &&
                  models.LastText.Contains(data.Checkpoint.Summary, StringComparison.Ordinal),
                "Generated summary remains fallible reference data while serving as conversation continuity.");

            profile.CompactionTriggerPercent = 60; store.Put("agent", profile.Id, profile);
            submission = await Capture();
            // The accepted setting must remain frozen if the owner edits the next request's policy.
            profile.CompactionTriggerPercent = 95; store.Put("agent", profile.Id, profile);
            var pressureHistory = history with
            {
                Messages = [new("user", [new TextContentPart(new string('p', 20000))])]
            };
            var pressureArgs = args with { Context = window with { Contributions = [pressureHistory, required] } };
            var beforePressureGeneration = models.Generations;
            generation = await generate.ExecuteAsync(Context("proactive-pressure", pressureArgs), default);
            Check(generation.State == WorkflowEffectState.Failed &&
                generation.Data!.Value.GetProperty("context").GetProperty("status").GetString() == "needsCompaction" &&
                models.Generations == beforePressureGeneration && models.Active == 0 &&
                generation.Error!.Contains("60%", StringComparison.Ordinal),
                "Accepted custom trigger compacts a still-fitting request before inference and releases the single slot.");
            result = await compact.ExecuteAsync(Context("proactive-pressure-compact", pressureArgs), default);
            Check(result.State == WorkflowEffectState.Succeeded && models.Active == 0,
                "Proactive pressure uses the existing bounded compactor without holding the lead slot.");
            var pressureCheckpoint = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!.Checkpoint;
            generation = await generate.ExecuteAsync(Context("proactive-pressure-resume",
                args with { Context = window with { Contributions = [required], Checkpoint = pressureCheckpoint } }), default);
            Check(generation.State == WorkflowEffectState.Succeeded &&
                models.LastText.Contains("EXACT USER REQUEST", StringComparison.Ordinal) &&
                models.LastText.Contains("Build receipt exact-build failed.", StringComparison.Ordinal),
                "Generation resumes with the new checkpoint and untouched current request and tool result.");

            profile.AutomaticCompaction = false; store.Put("agent", profile.Id, profile);
            submission = await Capture();
            generation = await generate.ExecuteAsync(Context("proactive-disabled", pressureArgs), default);
            Check(generation.State == WorkflowEffectState.Succeeded && models.Active == 0,
                "Turning automatic compaction off disables the early trigger for a fitting request.");
            profile.AutomaticCompaction = true; profile.CompactionTriggerPercent = 80;
            store.Put("agent", profile.Id, profile); submission = await Capture();

            var newerHistory = history with
            {
                Id = "newer-old",
                Messages = [new("user", [new TextContentPart(new string('y', 24000))])]
            };
            var rollingWindow = new ContextWindow(3, 0, [newerHistory, required], data.Checkpoint);
            var rollingArgs = args with { Context = rollingWindow };
            result = await compact.ExecuteAsync(Context("compact-2", rollingArgs), default);
            if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException("Rolling compaction fixture: " + result.Error);
            var rolled = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
            Check(rolled.Checkpoint.CoveredSources.SequenceEqual(
                    [new ContextSourceReference("compact-1", 2), new("newer-old", 1)]),
                "successive compaction references the prior durable checkpoint once instead of retaining every earlier source coordinate");

            var conversationTool = new ConversationToolExtension(store, new EventHub());
            var staged = await conversationTool.ExecuteAsync(
                new ToolCall(ConversationToolExtension.ResponseBalanceToolName,
                    Element(new { responseSharePercent = 26 })),
                new ToolExecutionContext(profile, submission.ConversationId, submission.RequestId, null,
                    FullAccess: false, EffectId: "balance-tool") { EnforceProfileScope = true }, default);
            Check(staged.Success && staged.Content.Contains("next model generation", StringComparison.Ordinal),
                "durable Chat can stage a bounded response balance without a legacy session");
            result = await compact.ExecuteAsync(Context("balanced-compaction", pressureArgs), default);
            var balanced = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            Check(result.State == WorkflowEffectState.Succeeded && balanced is not null &&
                  balanced.Before.DesiredResponseBudget == 2129 && balanced.After.ResponseBudget == 2129 &&
                  balanced.After.DesiredResponseBudget == 2129 &&
                  ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)?.ResponseSharePercent == 26,
                "Compaction reserves the staged normal response before and after summarizing without consuming it.");
            var balancedNext = window with { Contributions = [required], Checkpoint = balanced!.Checkpoint };
            generation = await generate.ExecuteAsync(Context("balanced-generation", args with { Context = balancedNext }), default);
            Check(generation.State == WorkflowEffectState.Succeeded && models.LastResponseSharePercent == 26 &&
                models.LastResponseLimit == 2129,
                "the staged durable balance controls exactly the next prepared model generation");
            generation = await generate.ExecuteAsync(Context("balance-consumed", args with { Context = next }), default);
            Check(generation.State == WorkflowEffectState.Succeeded && models.LastResponseSharePercent is null &&
                models.LastResponseLimit == profile.ResponseTokens,
                "the durable balance is consumed after preparation and cannot leak into later generations");

            // This fixture can deliberately clamp a response like a native prepared runtime.
            // Counts are independent deterministic evidence; they are not production estimates.
            var headroomHistory = history with { Messages = [new("user", [new TextContentPart("HEADROOM HISTORY")])] };
            var headroomWindow = window with { Contributions = [headroomHistory, required] };
            var headroomArgs = args with { Context = headroomWindow };
            models.ClampResponses = true;
            static bool HasCheckpoint(ModelGenerationRequest request) => request.Messages.SelectMany(x => x.Content)
                .OfType<TextContentPart>().Any(x => x.Text.Contains("Host-retained context checkpoint", StringComparison.Ordinal));
            static bool IsFramingProbe(ModelGenerationRequest request) => request.Messages.SelectMany(x => x.Content)
                .OfType<TextContentPart>().Any(x => x.Text.StartsWith("Host-retained context checkpoint", StringComparison.Ordinal) &&
                    x.Text.EndsWith("\nGenerated summary (fallible reference):\n.", StringComparison.Ordinal));
            static bool HasHistory(ModelGenerationRequest request) => request.Messages.SelectMany(x => x.Content)
                .OfType<TextContentPart>().Any(x => x.Text == "HEADROOM HISTORY");
            models.CountOverride = request => request.ResponseSchema is not null ? 500 :
                HasHistory(request) ? 7800 : HasCheckpoint(request) ? 300 : 200;
            var headroomBefore = models.Generations;
            generation = await generate.ExecuteAsync(Context("clamped-headroom", headroomArgs), default);
            Check(generation.State == WorkflowEffectState.Failed && models.Generations == headroomBefore &&
                  generation.Data?.GetProperty("context").GetProperty("status").GetString() == "needsCompaction" &&
                  models.Active == 0,
                "Automatic generation compacts instead of using a 264-token clamped response for a chosen 512.");
            result = await compact.ExecuteAsync(Context("headroom-compact", headroomArgs), default);
            var headroom = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            Check(result.State == WorkflowEffectState.Succeeded && headroom is { Passes: 1 } &&
                  headroom.After.ResponseBudget == 512 && headroom.After.DesiredResponseBudget == 512 &&
                  models.Generations == headroomBefore + 1 && models.Active == 0,
                "A fitting source compacts once and validates the full normal output allowance.");
            generation = await generate.ExecuteAsync(Context("headroom-resume", args with
                { Context = headroomWindow with { Contributions = [required], Checkpoint = headroom!.Checkpoint } }), default);
            var headroomGeneration = generation.Data?.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options);
            Check(generation.State == WorkflowEffectState.Succeeded && headroomGeneration!.Prepared.ResponseBudget == 512 &&
                  models.Generations == headroomBefore + 2 && models.Active == 0,
                "The next action uses the complete response reserve with no corrective compaction.");

            profile.AutomaticCompaction = false; store.Put("agent", profile.Id, profile);
            submission = await Capture();
            generation = await generate.ExecuteAsync(Context("clamped-disabled", headroomArgs), default);
            Check(generation.State == WorkflowEffectState.Succeeded &&
                  generation.Data?.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!.Prepared.ResponseBudget == 264,
                "An explicit automatic-compaction-off policy keeps useful clamped generation compatible.");
            profile.AutomaticCompaction = true; store.Put("agent", profile.Id, profile); submission = await Capture();

            models.CountOverride = request => request.ResponseSchema is not null ? 500 : 7900;
            headroomBefore = models.Generations;
            generation = await generate.ExecuteAsync(Context("required-reserve", headroomArgs), default);
            Check(generation.State == WorkflowEffectState.Failed &&
                  generation.Data?.GetProperty("context").GetProperty("status").GetString() == "requiredOverflow" &&
                  models.Generations == headroomBefore && models.Active == 0,
                "Required framing that fits a tiny response but cannot reserve the chosen response fails without inference.");
            result = await compact.ExecuteAsync(Context("required-reserve-compact", headroomArgs), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                  models.Generations == headroomBefore && models.Active == 0,
                "Compaction does not spend a summary pass on impossible required output headroom.");
            Check(result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Diagnostics is
                  { BeforeInputTokens: null, FloorInputTokens: 7900, AfterInputTokens: null,
                    AllowedHistoryTokens: null, ResidualHistoryTokens: null,
                    CoveredSourceCount: null, LocatorCount: null, SourceCount: 2,
                    CandidateSummaryScalarCount: null, CandidateSummaryWordCount: null },
                "An early RequiredOverflow identifies only its measured required floor and validated current-source count, without inventing full-request or candidate measurements.");

            models.CountOverride = request => request.ResponseSchema is not null ? 500 :
                HasHistory(request) ? 7950 : HasCheckpoint(request) ? 7570 : 7300;
            result = await compact.ExecuteAsync(Context("checkpoint-output-too-small", headroomArgs), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                  !result.Data!.Value.TryGetProperty("checkpoint", out _) && models.Active == 0,
                "A candidate that meets history reduction but leaves only 494 of 512 output tokens is rejected.");
            Check(result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Diagnostics is
                  { BeforeInputTokens: 7950, FloorInputTokens: 7300, AfterInputTokens: 7570,
                    AllowedHistoryTokens: 325, ResidualHistoryTokens: 270,
                    CoveredSourceCount: 1, LocatorCount: 1, SourceCount: 2,
                    CandidateSummaryScalarCount: > 0, CandidateSummaryWordCount: > 0 },
                "A non-ready final NeedsCompaction projection retains its actual candidate measurement and residual history independently of the missing prepared lease.");

            profile.AutomaticCompaction = false; store.Put("agent", profile.Id, profile);
            submission = (await Capture(requireCompactor: true)) with { ConversationCompactionRequestId = "explicit-fixture" };
            result = await compact.ExecuteAsync(Context("manual-compatible", headroomArgs), default);
            Check(result.State == WorkflowEffectState.Succeeded && models.Active == 0 &&
                  result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!.After.ResponseBudget == 494,
                "Deliberate manual compaction under an off automatic policy retains the compatibility output floor.");
            submission = submission with { ConversationCompactionRequestId = null };
            result = await bridge.CompactAsync(Context("idle-strict", headroomArgs), default, idleTriggerPercent: 80);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null && models.Active == 0,
                "Independent idle automatic compaction still requires normal output headroom when active-run compaction is off.");
            profile.AutomaticCompaction = true; store.Put("agent", profile.Id, profile); submission = await Capture();
            models.CountOverride = request => request.ResponseSchema is not null ? 500 :
                HasHistory(request) ? 7800 : HasCheckpoint(request) ? 300 : 200;

            async Task StageBalance(int share) => _ = await conversationTool.ExecuteAsync(
                new ToolCall(ConversationToolExtension.ResponseBalanceToolName, Element(new { responseSharePercent = share })),
                new ToolExecutionContext(profile, submission.ConversationId, submission.RequestId, null,
                    FullAccess: false, EffectId: "balance-change-fixture") { EnforceProfileScope = true }, default);
            await StageBalance(26);
            models.OnGenerate = request =>
            {
                if (request.ResponseSchema is not null) StageBalance(50).GetAwaiter().GetResult();
            };
            result = await compact.ExecuteAsync(Context("balance-changed", headroomArgs), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                  result.Error!.Contains("response balance changed", StringComparison.Ordinal) && models.Active == 0 &&
                  ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)?.ResponseSharePercent == 50,
                "Changing the pending balance during a summary retains old context and the new unconsumed preference.");
            models.OnGenerate = null;

            await StageBalance(26);
            models.OnPrepare = request =>
            {
                if (request.ResponseSchema is null && HasCheckpoint(request) && !IsFramingProbe(request))
                    StageBalance(27).GetAwaiter().GetResult();
            };
            result = await compact.ExecuteAsync(Context("balance-changed-final-prepare", headroomArgs), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                  result.Error!.Contains("response balance changed", StringComparison.Ordinal) && models.Active == 0 &&
                  ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)?.ResponseSharePercent == 27,
                "A balance change during final preparation also releases the unused owner lease without adopting the candidate.");
            models.OnPrepare = null;
            var pendingBalance = ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)!;
            ConversationToolExtension.ConsumeWorkflowResponseBalance(store, submission, pendingBalance);

            foreach (var capturedShare in new int?[] { null, 26 })
            {
                if (capturedShare is { } share) await StageBalance(share);
                models.OnPrepare = request =>
                {
                    if (request.ResponseSchema is null && HasCheckpoint(request))
                        StageBalance(27).GetAwaiter().GetResult();
                };
                var generationsBeforePreferenceChange = models.Generations;
                generation = await generate.ExecuteAsync(Context("generation-balance-race-" + capturedShare,
                    args with { Context = headroomWindow with { Contributions = [required], Checkpoint = headroom!.Checkpoint } }), default);
                Check(generation.State == WorkflowEffectState.Failed &&
                      generation.Error!.Contains("response balance changed", StringComparison.Ordinal) &&
                      models.Generations == generationsBeforePreferenceChange && models.Active == 0 &&
                      ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)?.ResponseSharePercent == 27,
                    "A new or replaced preference during preparation prevents stale inference and preserves the new row: " + capturedShare);
                models.OnPrepare = null;
                pendingBalance = ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)!;
                ConversationToolExtension.ConsumeWorkflowResponseBalance(store, submission, pendingBalance);
            }
            var expiredBalance = new ConversationToolExtension.WorkflowResponseBalance(submission.RequestId,
                submission.ConversationId, submission.ProfileId, 26, DateTimeOffset.UtcNow.AddHours(-25));
            store.Put("workflow-response-balance", submission.RequestId, expiredBalance);
            Check(!ConversationToolExtension.TryConsumeWorkflowResponseBalance(store, submission, expiredBalance) &&
                  store.Get<ConversationToolExtension.WorkflowResponseBalance>("workflow-response-balance", submission.RequestId) == expiredBalance,
                "Atomic preference consumption rejects an expired reservation without deleting an unmatched prepared record.");
            store.Delete("workflow-response-balance", submission.RequestId);

            using (var cancellation = new CancellationTokenSource())
            {
                models.OnGenerate = request => { if (request.ResponseSchema is not null) cancellation.Cancel(); };
                result = await compact.ExecuteAsync(Context("cancel-after-summary", headroomArgs), cancellation.Token);
                Check(result.State == WorkflowEffectState.Cancelled && result.ConversationCheckpoint is null &&
                      models.Active == 0, "Confirmed summary cancellation retains context and releases all prepared leases.");
                models.OnGenerate = null;
            }
            models.CountOverride = null; models.ClampResponses = false;

            var priorArgs = args with { Context = window with
            {
                Contributions = [history with { BeforeCurrentInput = true }, required]
            } };
            result = await compact.ExecuteAsync(Context("compact-prior", priorArgs), default);
            Check(result is { State: WorkflowEffectState.Succeeded,
                    ConversationCheckpoint: { BeforeCurrentInput: true, CompactionEffectId: "compact-prior" } },
                "Earlier-turn history publishes an exact store-level conversation checkpoint.");

            var before = models.Generations;
            var impossible = args with { Context = window with { Contributions = [history with { Requirement = ContextRequirement.Required }] } };
            generation = await generate.ExecuteAsync(Context("impossible", impossible), default);
            Check(generation.State == WorkflowEffectState.Failed && generation.Data!.Value.GetProperty("context").GetProperty("status").GetString() == "requiredOverflow" && models.Generations == before,
                "Impossible required floor fails before inference rather than looping through compaction.");
            result = await compact.ExecuteAsync(Context("impossible-compact", impossible), default);
            Check(result.State == WorkflowEffectState.Failed && models.Generations == before, "Compactor does not summarize mandatory content.");

            foreach (var finish in new[] { ModelGenerationFinish.Length, ModelGenerationFinish.Cancelled, ModelGenerationFinish.Unknown })
            {
                models.Finish = finish;
                result = await compact.ExecuteAsync(Context("partial-" + finish), default);
                var expected = finish == ModelGenerationFinish.Unknown ? WorkflowEffectState.Unknown :
                    finish == ModelGenerationFinish.Cancelled ? WorkflowEffectState.Cancelled : WorkflowEffectState.Failed;
                Check(result.State == expected && !result.Data!.Value.TryGetProperty("checkpoint", out _) && models.Active == 0,
                    "Partial or cancelled summary cannot become a checkpoint: " + finish);
            }
            models.Finish = ModelGenerationFinish.Stop;
            models.ThrowDuringGeneration = true;
            result = await compact.ExecuteAsync(Context("uncertain"), default);
            Check(result.State == WorkflowEffectState.Unknown && models.Active == 0, "Unconfirmed provider exception remains uncertain and releases the lease.");
            models.ThrowDuringGeneration = false;
            var previous = new WorkflowEffect("uncertain", WorkerWorkflowEffects.CompactCommand, 1, Context("uncertain").Arguments,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown, result.Data);
            var reconciled = await compact.ReconcileAsync(Context("uncertain") with { PreviousEffect = previous }, default);
            Check(reconciled.State == WorkflowEffectState.Failed && reconciled.Data?.GetRawText() == result.Data?.GetRawText() &&
                reconciled.Error!.Contains("previous checkpoint remains authoritative", StringComparison.Ordinal),
                "Interrupted compaction settles without replay and retains the previous usage receipt.");
            models.RawOutput = "{\"checkpoint\":\"unterminated";
            using var rejectedLease = diagnosticEvents.SubscribeDiagnostics();
            result = await compact.ExecuteAsync(Context("malformed"), default);
            Check(result.State == WorkflowEffectState.Failed && !result.Data!.Value.TryGetProperty("checkpoint", out _),
                "Malformed structured compaction output is never adopted as a durable checkpoint.");
            Check(result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Diagnostics is
                  { BeforeInputTokens: > 0, FloorInputTokens: > 0, AfterInputTokens: null,
                    AllowedHistoryTokens: > 0, ResidualHistoryTokens: null,
                    CoveredSourceCount: 2, LocatorCount: 2, SourceCount: 3,
                    CandidateSummaryScalarCount: null, CandidateSummaryWordCount: null },
                "Confirmed Stop with malformed JSON records prior measurements and validated coverage but no invented candidate-summary or final-request counts.");
            rejectedLease.Dispose();
            var rejectedEvents = new List<EngineDiagnosticRecord>();
            await foreach (var record in rejectedLease.ReadAsync()) rejectedEvents.Add(record);
            Check(rejectedEvents.Last().Phase == "compaction.candidate-rejected" &&
                  rejectedEvents.Last().Outcome == "Failed" &&
                  rejectedEvents.Any(x => x.Phase == "compaction.terminal" && x.Outcome == "Stop") &&
                  rejectedEvents.All(x => x.ChatId == "fixture-conversation" && x.EffectId == "malformed" &&
                      x.Phase != "compaction.candidate-accepted"),
                "A confirmed model Stop with invalid output is visibly rejected rather than logged as successful adoption.");
            foreach (var invalidJson in new[]
            {
                """{"checkpoint":"valid short reference","checkpoint":"different reference"}""",
                """Prose before {"checkpoint":"valid short reference"}""",
                """{"checkpoint":"valid short reference"} trailing prose"""
            })
            {
                models.RawOutput = invalidJson;
                result = await conciseCompact.ExecuteAsync(Context("closed-json-" + models.Generations), default);
                var refusedOutput = result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!;
                Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                      refusedOutput.Usage.Count == 1 && refusedOutput.Usage.All(x => x.Finish == ModelGenerationFinish.Stop) &&
                      refusedOutput.Diagnostics is { CandidateSummaryScalarCount: null, CandidateSummaryWordCount: null } &&
                      models.Active == 0 && JsonElement.DeepEquals(originalWindow, Element(window)),
                    "Confirmed Stop cannot admit duplicate checkpoint fields or surrounding prose and never mutates original sources.");
            }
            var conciseRequest = models.GeneratedCompactorRequests.Last();
            Check(conciseRequest.ResponseLimit == 512 &&
                  conciseRequest.ResponseSchema!.Value.GetProperty("properties").GetProperty("checkpoint")
                      .GetProperty("maxLength").GetInt32() == 768 &&
                  RequestText(conciseRequest).Contains("112 words", StringComparison.Ordinal),
                "The private 256-content-target scenario narrows schema and word guidance while retaining the accepted complete response cap.");
            models.RawOutput = null;
            models.CompletionError = "deterministic fixture terminal error";
            result = await compact.ExecuteAsync(Context("stop-with-error"), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                  result.Error!.Contains("complete confirmed summary", StringComparison.Ordinal) &&
                  result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!
                      .Usage.Single().Finish == ModelGenerationFinish.Stop && models.Active == 0,
                "A complete JSON object with Stop and an error cannot authorize checkpoint adoption.");
            models.CompletionError = null;
            foreach (var overrun in new[] { new string('s', 769), string.Join(" ", Enumerable.Repeat("w", 113)) })
            {
                models.Summary = overrun;
                result = await conciseCompact.ExecuteAsync(Context("content-overrun-" + models.Generations), default);
                Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                      result.Error!.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) &&
                      result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!
                          .Usage.Single().Finish == ModelGenerationFinish.Stop &&
                      models.Active == 0 && JsonElement.DeepEquals(originalWindow, Element(window)),
                    "Complete output over the declared character or word bound refuses without clipping or adopting any prefix.");
            }
            models.Summary = "";
            result = await compact.ExecuteAsync(Context("empty"), default);
            Check(result.State == WorkflowEffectState.Failed && !result.Data!.Value.TryGetProperty("checkpoint", out _), "Empty summary is never adopted.");
            models.Summary = "Confirmed source was read; build receipt exact-build failed; repair Program.cs before retry. 😀";
            Check(WorkerCompactorBudget.MeasureSummary("😀\talpha\u2003beta") is { Scalars: 12, Words: 3 },
                "The shared summary measurement counts supplementary Unicode as one scalar and Unicode whitespace as word separators.");
            await RunTargetAsync();
            profile.ContextTokens = 8192; profile.ResponseTokens = 512; profile.ReasoningMode = "off"; profile.ReasoningTokens = 0;
            store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "model.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, AutomaticContextSizing = false });
            submission = await Capture();
            models.CountOverride = null;
            models.Summary = "Confirmed source was read; build receipt exact-build failed; repair Program.cs before retry.";
            before = models.Generations;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                result = await compact.ExecuteAsync(Context("cancel-before"), cancellation.Token);
            }
            Check(result.State == WorkflowEffectState.Cancelled && models.Generations == before, "Cancelled preparation does not dispatch a compactor.");
            profile.ContextTokens = 32768; store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "model.gguf"),
                ContextTokens = 32768, MaximumContextTokens = 32768, AutomaticContextSizing = false });
            submission = await Capture();
            var originalSummary = models.Summary;
            models.Summary = "Confirmed source was read; build receipt exact-build failed; repair Program.cs before retry.";
            var oversizedHistory = args with { Context = new(2, 0, [
                history with { BeforeCurrentInput = true },
                required with { Messages = [new("user", [new TextContentPart(new string('r', 50_000))])] }]) };
            result = await compact.ExecuteAsync(Context("normal-history-envelope-overflow", oversizedHistory), default);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null && models.Active == 0 &&
                  result.Error!.Contains("bounded history page", StringComparison.Ordinal) &&
                  result.Data!.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!
                      .Usage.All(item => item.Finish == ModelGenerationFinish.Stop) &&
                  ((TextContentPart)oversizedHistory.Context!.Contributions.Single(item => item.Requirement == ContextRequirement.Required)
                      .Messages.Single().Content.Single()).Text.Length == 50_000,
                "Normal baseline candidate rejects its complete required-anchor/history envelope without clipping source handles.");
            models.Summary = originalSummary;
            profile.ContextTokens = 8192; store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "model.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, AutomaticContextSizing = false });
            before = models.Generations;
            profile.AutomaticCompaction = false; store.Put("agent", profile.Id, profile);
            submission = await Capture();
            result = await compact.ExecuteAsync(Context("disabled"), default);
            Check(result.State == WorkflowEffectState.Failed && models.Generations == before, "Disabled compaction is frozen at admission and not silently reenabled.");
            profile.AutomaticCompaction = true; profile.CompactionProfileId = "missing"; store.Put("agent", profile.Id, profile);
            var rejected = false;
            try { await Capture(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Missing configured compactor is not silently replaced.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Context compaction host tests passed ({checks}; fake adapter, no live inference).");
    }

    public static async Task RunTargetAsync()
    {
        // All counts and generations are supplied by the existing fake adapter, not a live tokenizer.
        var root = Path.Combine(Path.GetTempPath(), "cerebral-compaction-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks++; }
        try
        {
            var store = new StateStore(root);
            var config = new Configuration(store); config.Get();
            var profile = new AgentProfile
            {
                Id = "target-fixture", ModelId = "fixture", FollowsModelBudgets = false,
                ContextTokens = 16384, ResponseTokens = 4096, ReasoningMode = "on", ReasoningTokens = 1024,
                AutomaticCompaction = true, CompactionReductionPercent = 50, AllowedTools = [], Capabilities = []
            };
            store.Put("agent", profile.Id, profile);
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "model.gguf"),
                ContextTokens = 16384, MaximumContextTokens = 16384, AutomaticContextSizing = false });
            var tools = new NoTools();
            var models = new MeasuredFixture { Summary = "Fixture checkpoint preserves verified failure and exact handles." };
            var bridge = new WorkerWorkflowEffects(store, config, models, tools);
            var compact = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && command.Descriptor.Version == 1);
            var submission = new WorkflowSubmission("target-request", "target-conversation", profile.Id,
                "fixture:workflow", "1.0.0", "EXACT TARGET REQUEST", "EXACT TARGET ASSIGNMENT", Element(new { }),
                await WorkerExecutionSnapshot.CaptureAsync(store, profile, config, tools, [], default));
            const string requiredText = "REQUIRED EVIDENCE: fixture build failed; one retry remains; retain its exact source handle.";
            var required = new ContextContribution("target-required", "fixture", "tool", 1, ContextRequirement.Required,
                [new("user", [new TextContentPart(requiredText)])], "required-fixture-effect");
            var history = new ContextContribution("target-history", "fixture", "history", 1, ContextRequirement.Reducible,
                [new("user", [new TextContentPart("TARGET HISTORY")])], "history-fixture-effect");
            var priorCheckpoint = new ContextCheckpoint("target-prior", 1, 0,
                [new("earlier-source", 1)], "Earlier complete fixture checkpoint.", "target-prior-compaction")
                { SourceLocators = [new("earlier-source", 1, "fixture", "history", "earlier-fixture-effect", true)] };
            var window = new ContextWindow(2, 0, [history, required], priorCheckpoint);
            ContextValidation.ValidateWindow(window);
            var arguments = new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: window);
            var originalWindow = Element(window);
            var originalSubmission = Element(submission);
            WorkflowCommandContext Context(string id) => new(id, submission, Element(arguments));
            bool Unchanged() => JsonElement.DeepEquals(originalWindow, Element(window)) &&
                ReferenceEquals(window.Checkpoint, priorCheckpoint) &&
                JsonElement.DeepEquals(originalSubmission, Element(submission));
            static bool HasHistory(ModelGenerationRequest request) => request.Messages.SelectMany(message => message.Content)
                .OfType<TextContentPart>().Any(part => part.Text == "TARGET HISTORY");
            static bool HasCheckpoint(ModelGenerationRequest request) => RequestText(request).Contains("Host-retained context checkpoint", StringComparison.Ordinal);
            static bool IsFramingProbe(ModelGenerationRequest request) => request.Messages.SelectMany(message => message.Content)
                .OfType<TextContentPart>().Any(part => part.Text.StartsWith("Host-retained context checkpoint", StringComparison.Ordinal) &&
                    part.Text.EndsWith("\nGenerated summary (fallible reference):\n.", StringComparison.Ordinal));
            static bool IsFinalNormal(ModelGenerationRequest request) => request.ResponseSchema is null &&
                HasCheckpoint(request) && !HasHistory(request) && !IsFramingProbe(request);
            var beforeInput = 8995;
            var afterInput = 8108;
            models.CountOverride = request => request.ResponseSchema is not null ? 500 :
                HasHistory(request) ? beforeInput : IsFramingProbe(request) ? 7067 : HasCheckpoint(request) ? afterInput : 6967;
            ModelGenerationRequest? finalOwnerRequest = null;
            models.OnPrepare = request => { if (IsFinalNormal(request)) finalOwnerRequest = request; };
            var summaryCounts = WorkerCompactorBudget.MeasureSummary(models.Summary);
            foreach (var measuredAfter in new[] { 8108, 7981, 7800 })
            {
                afterInput = measuredAfter;
                finalOwnerRequest = null;
                var generationsBefore = models.Generations;
                var requestsBefore = models.GeneratedCompactorRequests.Count;
                var effectId = "desired-target-" + measuredAfter;
                var result = await compact.ExecuteAsync(Context(effectId), default);
                WorkflowSchema.Validate(result.Data!.Value, compact.Descriptor.OutputSchema);
                var data = result.Data.Value.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options)!;
                Check(result.State == WorkflowEffectState.Succeeded && data.Passes == 1 && data.Usage.Count == 1 &&
                    data.Usage[0].Finish == ModelGenerationFinish.Stop && models.Generations == generationsBefore + 1 &&
                    models.GeneratedCompactorRequests.Count == requestsBefore + 1 && models.Active == 0 &&
                    data.Before.InputTokens == 8995 && data.After.InputTokens == measuredAfter &&
                    data.After.EffectiveContext == 16384 && data.After.ResponseBudget == 4096 &&
                    data.After.DesiredResponseBudget == 4096 && data.After.ReasoningReserveTokens == 1024 &&
                    data.After.SafetyReserveTokens == 128,
                    "A fitting strict decrease succeeds immediately below, at, or beyond the desired reduction: " + measuredAfter);
                var attempts = result.Data.Value.GetProperty("attempts");
                Check(data.Attempts is [var attempt] && attempt.Attempt == 1 && attempt.UsageCount == 1 &&
                    attempt.BeforeInputTokens == 8995 && attempt.FloorInputTokens == 6967 &&
                    attempt.FramingProbeInputTokens == 7067 && attempt.AllowedHistoryTokens == 1014 &&
                    attempt.AfterInputTokens == measuredAfter && attempt.ResidualHistoryTokens == measuredAfter - 6967 &&
                    attempt.CandidateSummaryScalarCount == summaryCounts.Scalars &&
                    attempt.CandidateSummaryWordCount == summaryCounts.Words &&
                    attempts[0].EnumerateObject().All(field => field.Value.ValueKind == JsonValueKind.Number) &&
                    !attempts.GetRawText().Contains(models.Summary, StringComparison.Ordinal),
                    "One numeric attempt reports the actual residual relative to the desired target without summary text: " + measuredAfter);
                Check(data.Checkpoint.CompactionEffectId == effectId && data.Checkpoint.ContextRevision == window.Revision &&
                    data.Checkpoint.ExclusionRevision == window.ExclusionRevision &&
                    data.Checkpoint.CoveredSources.SequenceEqual(ContextValidation.GetCoveredManifest(window)) &&
                    data.Checkpoint.SourceLocators.SequenceEqual(ContextValidation.GetCoveredLocators(window)) &&
                    data.Checkpoint.CoveredSources.All(source => source.Id != required.Id) &&
                    finalOwnerRequest is not null && RequestText(finalOwnerRequest).Contains(requiredText, StringComparison.Ordinal) &&
                    RequestText(finalOwnerRequest).Contains("EXACT TARGET REQUEST", StringComparison.Ordinal) &&
                    RequestText(finalOwnerRequest).Contains("EXACT TARGET ASSIGNMENT", StringComparison.Ordinal) && Unchanged(),
                    "Desired-target acceptance preserves exact manifests, locators, required evidence, accepted input and the immutable source window: " + measuredAfter);
            }
            foreach (var measuredAfter in new[] { 8995, 9000 })
            {
                afterInput = measuredAfter;
                var generationsBefore = models.Generations;
                var result = await compact.ExecuteAsync(Context("no-progress-" + measuredAfter), default);
                WorkflowSchema.Validate(result.Data!.Value, compact.Descriptor.OutputSchema);
                var failure = result.Data.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!;
                Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                    !result.Data.Value.TryGetProperty("checkpoint", out _) && failure.Usage.Count == 1 &&
                    failure.Diagnostics?.AfterInputTokens == measuredAfter &&
                    models.Generations == generationsBefore + 1 && models.Active == 0 && Unchanged(),
                    "Equal or worsening measured input is refused without another inference or changing the previous checkpoint: " + measuredAfter);
            }
            beforeInput = 13000; afterInput = 12000;
            var beforeNonfit = models.Generations;
            var nonfit = await compact.ExecuteAsync(Context("smaller-but-nonfitting"), default);
            WorkflowSchema.Validate(nonfit.Data!.Value, compact.Descriptor.OutputSchema);
            Check(nonfit.State == WorkflowEffectState.Failed && nonfit.ConversationCheckpoint is null &&
                nonfit.Error!.Contains("complete normal request fit", StringComparison.Ordinal) &&
                !nonfit.Data.Value.TryGetProperty("checkpoint", out _) &&
                nonfit.Data.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Usage.Count == 1 &&
                12000 < 13000 && 12000 + 4096 + 1024 + 128 > 16384 &&
                models.Generations == beforeNonfit + 1 && models.Active == 0 && Unchanged(),
                "A smaller candidate still fails when complete response, reasoning and safety reserves do not fit");
            beforeInput = 8995; afterInput = 8108;
            var conversationTool = new ConversationToolExtension(store, new EventHub());
            async Task StageBalance() => _ = await conversationTool.ExecuteAsync(
                new ToolCall(ConversationToolExtension.ResponseBalanceToolName, Element(new { responseSharePercent = 27 })),
                new ToolExecutionContext(profile, submission.ConversationId, submission.RequestId, null,
                    FullAccess: false, EffectId: "target-balance-change") { EnforceProfileScope = true }, default);
            foreach (var mutation in new[] { "binding", "response-balance" })
            {
                var mutationApplied = false;
                models.OnPrepare = request =>
                {
                    if (mutationApplied || !IsFinalNormal(request)) return;
                    mutationApplied = true;
                    if (mutation == "binding") { profile.ModelId = "revoked-fixture"; store.Put("agent", profile.Id, profile); }
                    else StageBalance().GetAwaiter().GetResult();
                };
                var generationsBefore = models.Generations;
                var result = await compact.ExecuteAsync(Context("final-preparation-" + mutation), default);
                WorkflowSchema.Validate(result.Data!.Value, compact.Descriptor.OutputSchema);
                Check(mutationApplied && result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                    !result.Data.Value.TryGetProperty("checkpoint", out _) &&
                    result.Data.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Usage.Count == 1 &&
                    result.Error!.Contains(mutation == "binding" ? "binding" : "response balance changed", StringComparison.OrdinalIgnoreCase) &&
                    models.Generations == generationsBefore + 1 && models.Active == 0 && Unchanged(),
                    "Late protected request changes still refuse the fitting candidate and preserve the prior checkpoint: " + mutation);
                models.OnPrepare = null;
                if (mutation == "binding") { profile.ModelId = "fixture"; store.Put("agent", profile.Id, profile); }
                else ConversationToolExtension.ConsumeWorkflowResponseBalance(store, submission,
                    ConversationToolExtension.ReadWorkflowResponseBalance(store, submission)!);
            }
            foreach (var terminal in new[] { "malformed", "Length", "Unknown" })
            {
                models.RawOutput = terminal == "malformed" ? "{" : null;
                models.Finish = terminal == "Length" ? ModelGenerationFinish.Length :
                    terminal == "Unknown" ? ModelGenerationFinish.Unknown : ModelGenerationFinish.Stop;
                var generationsBefore = models.Generations;
                var result = await compact.ExecuteAsync(Context("target-terminal-" + terminal), default);
                WorkflowSchema.Validate(result.Data!.Value, compact.Descriptor.OutputSchema);
                Check(result.State == (terminal == "Unknown" ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed) &&
                    result.ConversationCheckpoint is null && !result.Data.Value.TryGetProperty("checkpoint", out _) &&
                    result.Data.Value.Deserialize<WorkerWorkflowEffects.CompactionFailureData>(CerebralJson.Options)!.Usage.Count == 1 &&
                    models.Generations == generationsBefore + 1 && models.Active == 0 && Unchanged(),
                    "A desired target never admits malformed, partial or unconfirmed output: " + terminal);
                models.RawOutput = null; models.Finish = ModelGenerationFinish.Stop;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-compaction-target-", StringComparison.Ordinal))
                throw new InvalidOperationException("The isolated compaction fixture cleanup root changed.");
            Directory.Delete(resolved, recursive: true);
        }
        Console.WriteLine($"Desired-target compaction checks passed ({checks}; deterministic fake adapter, no live tokenizer or inference).");
    }

    internal sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("No tool execution belongs in compaction.");
    }

    private sealed class MeasuredFixture : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture", "1", ["text"], SupportsReasoning: true);
        public int Active, Generations, PrepareCalls;
        public bool ThrowDuringGeneration;
        public int LengthResponsesRemaining;
        public bool PublishVisibleText = true;
        public string LastText = "";
        public int? LastResponseSharePercent;
        public int LastResponseLimit;
        public bool ClampResponses;
        public Func<ModelGenerationRequest, int>? CountOverride;
        public Func<ModelGenerationRequest, string>? SummaryForRequest;
        public Action<ModelGenerationRequest>? OnGenerate, OnPrepare;
        public List<string> GeneratedInputs { get; } = [];
        public List<ModelGenerationRequest> GeneratedCompactorRequests { get; } = [];
        public JsonElement? LastResponseSchema;
        public JsonElement? LastStructuredResponseSchema;
        public string? RawOutput, CompletionError;
        public string Summary = "Confirmed source was read; prior assistant response was exactly 'GB10 remote chat is working.'; build receipt exact-build failed; repair Program.cs before retry.";
        public ModelGenerationFinish Finish = ModelGenerationFinish.Stop;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            LastResponseSharePercent = request.ResponseSharePercent;
            LastResponseLimit = request.ResponseLimit;
            LastResponseSchema = request.ResponseSchema?.Clone();
            if (request.ResponseSchema is not null) LastStructuredResponseSchema = request.ResponseSchema.Value.Clone();
            if (Active != 0) throw new InvalidOperationException("A lead lease was held while preparing another inference.");
            var text = string.Join("\n", request.Messages.SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
            // Deterministic fake tokenization solely for tests, never production token estimation.
            OnPrepare?.Invoke(request);
            var tokens = CountOverride?.Invoke(request) ?? text.Length / 4 + 64;
            var desired = request.ResponseLimit;
            var available = Math.Max(0, request.ContextLimit - tokens - request.ReasoningTokens - 128);
            var response = ClampResponses && (request.AllowResponseClamping || request.ResponseSharePercent is not null)
                ? Math.Min(desired, available) : desired;
            var metadata = new PreparedModelMetadata("fixture-slot", "fixture", "1", request.ModelId, tokens,
                request.ContextLimit, response, "fixture tokenizer")
                { DesiredResponseBudget = desired, ReasoningReserveTokens = request.ReasoningTokens, SafetyReserveTokens = 128 };
            if (response <= 0 || tokens + response + request.ReasoningTokens + 128 > request.ContextLimit)
                throw new ModelContextLimitException(metadata with { ResponseBudget = 0 });
            Active++;
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata, text, request));
        }
        private sealed class Prepared(MeasuredFixture owner, PreparedModelMetadata metadata, string text, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            private bool disposed;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
            {
                owner.Generations++; owner.LastText = text; owner.GeneratedInputs.Add(text);
                if (request.ResponseSchema is { } responseSchema && responseSchema.GetProperty("properties").TryGetProperty("checkpoint", out _))
                    owner.GeneratedCompactorRequests.Add(request with { ResponseSchema = responseSchema.Clone() });
                owner.OnGenerate?.Invoke(request);
                if (owner.ThrowDuringGeneration) throw new IOException("private fixture provider body");
                var finish = owner.LengthResponsesRemaining > 0 ? ModelGenerationFinish.Length : owner.Finish;
                if (owner.LengthResponsesRemaining > 0) owner.LengthResponsesRemaining--;
                var output = owner.RawOutput ?? JsonSerializer.Serialize(new { checkpoint = owner.SummaryForRequest?.Invoke(request) ?? owner.Summary });
                if (owner.PublishVisibleText) onText?.Invoke(output);
                return Task.FromResult(new ModelGenerationResult(finish, output, 24, metadata.InputTokens,
                    Error: owner.CompletionError, CancellationConfirmed: finish == ModelGenerationFinish.Cancelled));
            }
            public ValueTask DisposeAsync() { if (!disposed) { owner.Active--; disposed = true; } return ValueTask.CompletedTask; }
        }
    }
    private sealed record LegacyCompactResult(ContextCheckpoint? Checkpoint, PreparedModelMetadata? Before, PreparedModelMetadata? After);
    private static string RequestText(ModelGenerationRequest request) => string.Join("\n",
        request.Messages.SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
}
