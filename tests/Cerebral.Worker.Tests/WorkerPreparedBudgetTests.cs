using System.Net;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Private host and native-protocol fixtures; no live model, provider, GPU or owner data.</summary>
internal static class WorkerPreparedBudgetTests
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Prepared budget: " + description);
            checks++;
        }

        foreach (var capacity in new[] { 8192, 16384 })
        {
            using var fixture = new HostFixture();
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(capacity);
            var result = await fixture.GenerateAsync(submission);
            var data = result.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            Check(result.State == WorkflowEffectState.Succeeded && data.Prepared.EffectiveContext == capacity &&
                data.Prepared.ReasoningReserveTokens == 2048 && data.Prepared.ResponseBudget == 3072 &&
                data.Prepared.InputTokens + data.Prepared.CombinedGenerationBudget + data.Prepared.SafetyReserveTokens <= capacity,
                "48K admission retains useful accepted reasoning and visible response at the smaller live capacity.");
            Check(fixture.Models.Requests.Count == 1 && fixture.Models.Active == 0 &&
                fixture.Profile.ContextTokens == 48128 && fixture.Profile.ReasoningTokens == 2048,
                "host narrowing does not edit the accepted profile or retain a slot.");
        }
        using (var fixture = new HostFixture(32768))
        {
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(8192);
            var result = await fixture.GenerateAsync(submission, new ContextWindow(1, 0, []));
            var projection = result.Data!.Value.Deserialize<WorkerWorkflowEffects.ContextPreparationData>(CerebralJson.Options)!.Context;
            Check(result.State == WorkflowEffectState.Failed && projection.Status == ContextProjectionStatus.RequiredOverflow &&
                projection.Metadata!.ReasoningReserveTokens == 7807 && fixture.Models.Generations == 0 && fixture.Models.Active == 0,
                "huge frozen reasoning is capped by existing policy and required overflow remains explicit without inference.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            fixture.Profile.ReasoningTokens = 1024; fixture.SaveProfiles();
            var result = await fixture.GenerateAsync(submission);
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Single().ReasoningTokens == 1024,
                "live reasoning allowance narrows a frozen reservation without increasing it.");
        }
        using (var fixture = new HostFixture())
        {
            fixture.Profile.ReasoningMode = "off"; fixture.SaveProfiles();
            var submission = await fixture.CaptureAsync();
            fixture.Profile.ReasoningMode = "on"; fixture.SaveProfiles(); fixture.SetCapacity(8192);
            var result = await fixture.GenerateAsync(submission);
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Single().ReasoningTokens == 0,
                "a later enabled profile cannot enable reasoning in an accepted non-reasoning request.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            fixture.Profile.ReasoningMode = "off"; fixture.SaveProfiles();
            var result = await fixture.GenerateAsync(submission);
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Single().ReasoningTokens == 2048,
                "the accepted reasoning-on mode remains frozen after a later profile toggle.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            var result = await fixture.GenerateAsync(submission, hostReasoningTokens: 0);
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Single().ReasoningTokens == 0,
                "the private host visual override remains zero-only.");
            fixture.Models.Requests.Clear();
            result = await fixture.GenerateAsync(submission, hostReasoningTokens: 128);
            Check(result.State == WorkflowEffectState.Failed && fixture.Models.Requests.Count == 0,
                "nonzero private host overrides fail before preparation.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            fixture.Models.SupportsReasoning = false;
            var result = await fixture.GenerateAsync(submission);
            Check(result.State == WorkflowEffectState.Failed && fixture.Models.Requests.Count == 0,
                "unsupported reasoning is rejected before preparing any slot.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            var result = await fixture.GenerateAsync(submission);
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Single() is
                { ContextLimit: 48128, ResponseLimit: 3072, ReasoningTokens: 2048 },
                "nonshrinking accepted budgets do not change or repeat preparation.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            var acceptedSettings = submission.SettingsSnapshot.GetRawText();
            var accepted = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile;
            Check(!accepted.FollowsModelBudgets && accepted.ResponseTokens == 3072,
                "the old explicit 3072-token reservation is frozen at admission.");

            var live = fixture.Store.Get<AgentProfile>("agent", fixture.Profile.Id)!;
            live.FollowsModelBudgets = true;
            fixture.Store.Put("agent", live.Id, live);
            var savedAutomatic = fixture.Store.Get<AgentProfile>("agent", live.Id)!;
            var savedAutomaticJson = Element(savedAutomatic);
            var model = fixture.Store.Get<ModelRecord>("model", "fixture")!;
            var resolvedAutomatic = AgentBudgetPolicy.Resolve(savedAutomatic, model,
                model.AutomaticContextSizing ? model.ContextTokens : null);
            Check(resolvedAutomatic.ContextTokens == 48128 && resolvedAutomatic.ResponseTokens == 12032 &&
                savedAutomatic.ResponseTokens == 3072,
                "the current automatic policy is larger without editing the stored live profile.");

            var result = await fixture.GenerateAsync(submission);
            var data = result.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Requests.Count == 1 &&
                fixture.Models.Requests.Single() is
                    { ContextLimit: 48128, ResponseLimit: 3072, ResponseSharePercent: null } &&
                data.Prepared.EffectiveContext == 48128 && data.Prepared.DesiredResponseBudget == 3072 &&
                data.Prepared.ResponseBudget == 3072 && !data.Prepared.IsBudgetClamped &&
                fixture.Models.Generations == 1 && fixture.Models.Active == 0,
                "ordinary generation keeps the old accepted reservation without override or preparation shrink.");
            Check(submission.SettingsSnapshot.GetRawText() == acceptedSettings &&
                WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile is
                    { FollowsModelBudgets: false, ResponseTokens: 3072 } &&
                JsonElement.DeepEquals(savedAutomaticJson, Element(fixture.Store.Get<AgentProfile>("agent", live.Id)!)),
                "execution changes neither the accepted snapshot nor the stored live automatic profile.");
        }

        foreach (var reasoning in new[] { 2048, 32768 })
        {
            using var native = new NativeSlotFixture(8192, reasoning == 2048 ? 300 : 100);
            var request = Request(reasoning);
            var adapter = WorkerPreparedBudgets.ForPreparation(native);
            await using var prepared = await adapter.PrepareAsync(request, default);
            var narrowed = native.Requests[1];
            RemotePreparedProtocol.ValidateMetadata(prepared.Metadata, narrowed);
            Check(native.Requests.Count == 2 && narrowed.ContextLimit == 8192 &&
                narrowed.ReasoningTokens <= request.ReasoningTokens && narrowed.ResponseLimit <= request.ResponseLimit &&
                prepared.Metadata.ReasoningReserveTokens == narrowed.ReasoningTokens &&
                prepared.Metadata.InputTokens + prepared.Metadata.CombinedGenerationBudget + 128 <= 8192,
                "actual native /slots capacity causes one freshly counted narrower request with remote-equal metadata.");
            Check(ReferenceEquals(request.Messages, narrowed.Messages) &&
                JsonElement.DeepEquals(request.ResponseSchema!.Value, narrowed.ResponseSchema!.Value) &&
                native.Counted.Count == 2 && native.CompletionCalls == 0 && native.Pool.AvailableCount == 0,
                "the composed source and schema remain exact and preparation never dispatches completion.");
            Check(native.Counted[1].GetProperty("reasoning_budget_tokens").GetInt32() == narrowed.ReasoningTokens &&
                native.Counted[1].GetProperty("max_tokens").GetInt32() == prepared.Metadata.CombinedGenerationBudget,
                "the final native body uses the final reservation and combined hard generation ceiling.");
        }
        using (var native = new NativeSlotFixture(8192, 300))
        {
            var limited = await ExpectAsync<ModelContextLimitException>(() =>
                WorkerPreparedBudgets.ForPreparation(native).PrepareAsync(Request(32768), default));
            Check(limited.Metadata.EffectiveContext == 8192 && limited.Metadata.ReasoningReserveTokens == 7807 &&
                native.Requests.Count == 2 && native.Pool.AvailableCount == 1 && native.CompletionCalls == 0,
                "a still-impossible huge reservation fails after the single retry and releases exact native slot ownership.");
        }
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var models = new CountedAdapter { Capacity = 16384, ThrowDisposal = !cancel };
            if (cancel) models.OnReady = cancellation.Cancel;
            var adapter = WorkerPreparedBudgets.ForPreparation(models);
            if (cancel) await ExpectAsync<OperationCanceledException>(() => adapter.PrepareAsync(Request(2048), cancellation.Token));
            else await ExpectAsync<IOException>(() => adapter.PrepareAsync(Request(2048), default));
            Check(models.Requests.Count == 1 && models.Active == 0 && models.Generations == 0,
                "cancellation or disposal failure prevents a second preparation and any inference.");
        }
        {
            var models = new CountedAdapter { CapacityAtAttempt = index => index == 0 ? 16384 : 8192 };
            await using var prepared = await WorkerPreparedBudgets.ForPreparation(models).PrepareAsync(Request(2048), default);
            Check(models.Requests.Count == 2 && prepared.Metadata.EffectiveContext == 8192 &&
                prepared.Metadata.InputTokens + prepared.Metadata.CombinedGenerationBudget + 128 <= 8192,
                "another capacity shrink during the retry remains measured and never causes a third preparation.");
        }

        checks += await RunCompactorBudgetAsync();

        foreach (var withContext in new[] { false, true })
        {
            using var fixture = new HostFixture();
            var submission = await fixture.CaptureAsync();
            var staged = await new ConversationToolExtension(fixture.Store, new EventHub()).ExecuteAsync(
                new(ConversationToolExtension.ResponseBalanceToolName, Element(new { responseSharePercent = 26 })),
                new(fixture.Profile, submission.ConversationId, submission.RequestId, null, FullAccess: false,
                    EffectId: "stage-balance") { EnforceProfileScope = true }, default);
            var pending = ConversationToolExtension.ReadWorkflowResponseBalance(fixture.Store, submission);
            Check(staged.Success && pending is not null, "the real staging route supplies a one-generation balance for the fixture.");
            fixture.FailBalanceDelete(true);
            var window = withContext ? new ContextWindow(1, 0, []) : null;
            var result = await fixture.GenerateAsync(submission, window);
            Check(result.State == WorkflowEffectState.Failed && fixture.Models.Active == 0 &&
                fixture.Models.Generations == 0 && fixture.Models.Requests.Count == 1 &&
                fixture.Models.Requests.Single().ResponseSharePercent == 26 &&
                ConversationToolExtension.ReadWorkflowResponseBalance(fixture.Store, submission) == pending,
                "actual SQLite consumption failure releases preparation, starts no inference and preserves the rolled-back balance.");
            var reconciled = await fixture.ReconcileGenerationAsync(submission);
            Check(reconciled.State == WorkflowEffectState.Unknown && fixture.Models.Requests.Count == 1 && fixture.Models.Generations == 0,
                "failed consumption is not replayed or promoted into inference during reconciliation.");
            fixture.FailBalanceDelete(false);
            result = await fixture.GenerateAsync(submission, window, effectId: "explicit-next-generation");
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Active == 0 && fixture.Models.Generations == 1 &&
                fixture.Models.Requests[^1].ResponseSharePercent == 26 &&
                ConversationToolExtension.ReadWorkflowResponseBalance(fixture.Store, submission) is null,
                "an explicit later generation consumes the preserved balance only after successful preparation and storage.");
            result = await fixture.GenerateAsync(submission, window, effectId: "following-generation");
            Check(result.State == WorkflowEffectState.Succeeded && fixture.Models.Active == 0 && fixture.Models.Generations == 2 &&
                fixture.Models.Requests[^1].ResponseSharePercent is null && fixture.Models.Requests[^1].ResponseLimit == 3072,
                "a consumed balance is not reused by the following generation.");
        }
        using (var fixture = new HostFixture())
        using (var cancellation = new CancellationTokenSource())
        {
            var submission = await fixture.CaptureAsync();
            var staged = await new ConversationToolExtension(fixture.Store, new EventHub()).ExecuteAsync(
                new(ConversationToolExtension.ResponseBalanceToolName, Element(new { responseSharePercent = 26 })),
                new(fixture.Profile, submission.ConversationId, submission.RequestId, null, FullAccess: false,
                    EffectId: "stage-cancelled-balance") { EnforceProfileScope = true }, default);
            var pending = ConversationToolExtension.ReadWorkflowResponseBalance(fixture.Store, submission);
            Check(staged.Success && pending is not null, "a staged balance is present before the after-preparation cancellation.");
            fixture.Models.OnReady = cancellation.Cancel;
            var result = await fixture.GenerateAsync(submission, token: cancellation.Token);
            Check(result.State == WorkflowEffectState.Cancelled && fixture.Models.Active == 0 &&
                fixture.Models.Generations == 0 && fixture.Models.Requests.Count == 1 &&
                ConversationToolExtension.ReadWorkflowResponseBalance(fixture.Store, submission) == pending,
                "cancellation immediately after preparation releases the lease without dispatching inference or consuming the pending balance.");
        }
        Console.WriteLine($"Prepared budget shrink tests passed ({checks}; deterministic host, isolated SQLite and native HTTP fixtures, no live inference).");
    }

    /// <summary>Host-only deterministic compactor fixtures. This method does not invoke the
    /// NativeSlotFixture, HTTP transport, a tokenizer, a model or a Worker process.</summary>
    internal static async Task<int> RunCompactorBudgetAsync()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Prepared compactor budget: " + description);
            checks++;
        }

        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(8192);
            fixture.Compactor.ReasoningTokens = 1024; fixture.SaveProfiles();
            var window = CompactionWindow();
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            Check(result.State == WorkflowEffectState.Succeeded && data is not null &&
                data.Usage.All(x => x.Prepared.ReasoningReserveTokens == 1024 && x.Prepared.EffectiveContext == 8192) &&
                data.After.InputTokens < data.Before.InputTokens && fixture.Models.Active == 0,
                "compactor reservations follow the same live cap and the complete checkpoint is remeasured before adoption.");
            Check(data!.Checkpoint.CoveredSources.Any(x => x.Id == "baseline") &&
                window.Checkpoint!.Summary == "OLD EXACT CHECKPOINT" && result.ConversationCheckpoint is not null,
                "successful compaction retains baseline provenance and does not mutate the existing checkpoint.");
        }
        using (var fixture = new HostFixture())
        {
            var submission = await fixture.CaptureAsync();
            fixture.Models.Capacity = 8192;
            var result = await fixture.CompactAsync(submission, CompactionWindow());
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            Check(result.State == WorkflowEffectState.Succeeded && data is not null &&
                data.Before.EffectiveContext == 8192 && data.After.EffectiveContext == 8192 &&
                data.Usage.All(x => x.Prepared.EffectiveContext == 8192 && x.Prepared.ReasoningReserveTokens == 2048) &&
                fixture.Models.Active == 0,
                "load-time slot shrink is remeasured for owner, compactor and complete checkpoint validation.");
        }
        using (var fixture = new HostFixture())
        {
            fixture.Compactor.ReasoningTokens = 32768; fixture.SaveProfiles();
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(8192);
            var window = CompactionWindow();
            var result = await fixture.CompactAsync(submission, window);
            Check(result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                fixture.Models.Generations == 0 && fixture.Models.Active == 0 && window.Checkpoint!.Summary == "OLD EXACT CHECKPOINT" &&
                fixture.Models.Requests.Where(IsCompactor).All(x => x.ReasoningTokens == 7807),
                "an oversized reasoning compactor retains the previous checkpoint and emits no partial generation.");
        }
        using (var fixture = new HostFixture(checkpointTargetTokens: 256))
        {
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(8192);
            var window = CompactionWindow();
            var originalWindow = Element(window);
            var originalSubmission = Element(submission);
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            var requests = fixture.Models.Requests.Where(IsCompactor).ToArray();
            Check(result.State == WorkflowEffectState.Succeeded && data is not null && requests.Length > 0 &&
                requests.All(request => request.ResponseLimit == 1024 && ConciseScenario(request)) &&
                data.Usage.All(item => item.Prepared.ResponseBudget == 1024) && fixture.Models.Active == 0,
                "the private 256-content-target scenario retains a complete 1024-token allowance with 112-word 768-character content.");
            Check(JsonElement.DeepEquals(originalWindow, Element(window)) &&
                JsonElement.DeepEquals(originalSubmission, Element(submission)) &&
                data!.Checkpoint.CoveredSources.SequenceEqual(ContextValidation.GetCoveredManifest(window)) &&
                data.Checkpoint.SourceLocators.SequenceEqual(ContextValidation.GetCoveredLocators(window)) &&
                requests.All(request => RequestText(request).Contains("EXACT CURRENT INPUT", StringComparison.Ordinal) &&
                    RequestText(request).Contains("EXACT ASSIGNMENT", StringComparison.Ordinal)),
                "private concise compaction retains exact goal current input coverage and source locators without mutating accepted inputs.");
        }
        using (var fixture = new HostFixture(explicitDefaultPolicy: true))
        {
            var submission = await fixture.CaptureAsync();
            fixture.SetCapacity(8192);
            var window = CompactionWindow() with
            {
                Contributions = [new("small-older", "fixture", "conversation", 3, ContextRequirement.Reducible,
                    [new("user", [new TextContentPart(new string('x', 2048))])], "small-older", BeforeCurrentInput: true)]
            };
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            var requests = fixture.Models.Requests.Where(IsCompactor).ToArray();
            Check(result.State == WorkflowEffectState.Succeeded && data is not null && requests.Length > 0 &&
                requests.All(request => request.ResponseLimit == 1024 &&
                    request.ResponseSchema!.Value.GetProperty("properties").GetProperty("checkpoint")
                        .GetProperty("maxLength").GetInt32() is >= 128 and < 768) &&
                data.Usage.All(item => item.Prepared.ResponseBudget == 1024) && fixture.Models.Active == 0,
                "explicit null content policy retains small proportional targets without shrinking the complete native response allowance.");
            Check(fixture.Profile.ResponseTokens == 3072 && fixture.Compactor.ResponseTokens == 3072 &&
                WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Compactor!.Profile.ResponseTokens == 3072,
                "private response and content limits do not edit accepted or live profile budgets.");
        }
        foreach (var clamp in new[] { (Accepted: 512, Live: 3072), (Accepted: 3072, Live: 512) })
        {
            using var fixture = new HostFixture(checkpointTargetTokens: 256);
            fixture.Compactor.ResponseTokens = clamp.Accepted; fixture.SaveProfiles();
            var submission = await fixture.CaptureAsync();
            fixture.Compactor.ResponseTokens = clamp.Live; fixture.SaveProfiles();
            fixture.SetCapacity(8192);
            var result = await fixture.CompactAsync(submission, CompactionWindow());
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            var requests = fixture.Models.Requests.Where(IsCompactor).ToArray();
            Check(result.State == WorkflowEffectState.Succeeded && data is not null && requests.Length > 0 &&
                requests.All(request => request.ResponseLimit == 512 && ConciseScenario(request)) &&
                data.Usage.All(item => item.Prepared.ResponseBudget == 512) && fixture.Models.Active == 0,
                "complete native response allowance obeys both accepted and live compactor caps without widening either authority.");
            Check(WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Compactor!.Profile.ResponseTokens == clamp.Accepted &&
                fixture.Store.Get<AgentProfile>("agent", fixture.Compactor.Id)!.ResponseTokens == clamp.Live,
                "accepted and live response caps remain unchanged after private scenario compaction.");
        }
        using (var fixture = new HostFixture())
        {
            foreach (var invalid in new[] { 0, -1 })
            {
                var refused = false;
                try { _ = new WorkerWorkflowEffects(new CompactorContentPolicy(invalid),
                    fixture.Store, fixture.Configuration, fixture.Models, new ContextCompactionTests.NoTools()); }
                catch (ArgumentOutOfRangeException) { refused = true; }
                Check(refused, "explicit nonpositive content targets refuse at the Worker constructor boundary.");
            }
        }
        var parser = WorkerCompactorBudget.Create(1024, 256, 3072, 3072, 8192);
        var scalarBoundary = string.Concat(Enumerable.Repeat("\U0001F600", 768));
        Check(scalarBoundary.Length == 1536 &&
            parser.TryReadSummary(JsonSerializer.Serialize(new { checkpoint = scalarBoundary }), out var scalarSummary) &&
            scalarSummary == scalarBoundary,
            "exact 768 supplementary Unicode scalars admit intact despite 1536 UTF16 code units.");
        var scalarOverrun = scalarBoundary + "\U0001F600";
        Check(!parser.TryReadSummary(JsonSerializer.Serialize(new { checkpoint = scalarOverrun }), out var refusedScalars) &&
            refusedScalars == "",
            "769 Unicode scalars refuse the complete object without returning any accepted prefix.");
        var wordsBoundary = string.Join("\u2003", Enumerable.Repeat("w", 112));
        Check(parser.TryReadSummary(JsonSerializer.Serialize(new { checkpoint = wordsBoundary }), out var wordSummary) &&
            wordSummary == wordsBoundary,
            "exact 112 words separated by Unicode whitespace admit with the original content unchanged.");
        Check(!parser.TryReadSummary(JsonSerializer.Serialize(new { checkpoint = wordsBoundary + " w" }), out var refusedWords) &&
            refusedWords == "",
            "113 whitespace-delimited words refuse even while character count remains within its bound.");

        using (var fixture = new HostFixture(checkpointTargetTokens: 1024))
        {
            fixture.Compactor.ReasoningMode = "off"; fixture.Compactor.ReasoningTokens = 0; fixture.SaveProfiles();
            fixture.Models.CapacityForRequest = request => IsCompactor(request) ? 512 : 8192;
            fixture.Models.CountInput = request => IsCompactor(request) ? 64 :
                request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Sum(part => part.Text.Length) / 8 + 32;
            var submission = await fixture.CaptureAsync();
            var window = CompactionWindow();
            var originalWindow = Element(window); var originalSubmission = Element(submission);
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            var generated = fixture.Models.GeneratedCompactorRequests;
            Check(result.State == WorkflowEffectState.Succeeded && data is not null && generated.Count > 0 &&
                // Existing authority also reserves 128 safety, 128 minimum prompt and one token:
                // 512 - 128 - 128 - 1 = 255 response, hence 765 content scalars and 111 words.
                generated.All(request => request.ContextLimit == 512 && request.ResponseLimit == 255 &&
                    ConciseScenario(request, expectedCharacters: 765, expectedWords: 111)) &&
                data.Usage.All(item => item.Prepared.EffectiveContext == 512 && item.Prepared.ResponseBudget == 255) &&
                fixture.Models.Requests.Any(request => IsCompactor(request) && request.ContextLimit > 512 &&
                    request.ResponseLimit == 1024 && request.ResponseSchema!.Value.GetProperty("properties")
                        .GetProperty("checkpoint").GetProperty("maxLength").GetInt32() == 3072) &&
                // Requests includes rejected attempts; PreparedRecords includes only valid leases.
                // The wrapper preserves the earlier 3072-scalar schema while narrowing budgets,
                // then the host must discard it and recompose the exact 765-scalar request above.
                fixture.Models.PreparedRecords.Any(item => IsCompactor(item.Request) &&
                    item.Request.ContextLimit == 512 && item.Request.ResponseLimit == 255 &&
                    item.Request.ResponseSchema!.Value.GetProperty("properties").GetProperty("checkpoint")
                        .GetProperty("maxLength").GetInt32() == 3072) &&
                fixture.Models.Active == 0,
                "load-time context shrink recompiles prompt schema parser and native allowance together before fake provider entry.");
            Check(JsonElement.DeepEquals(originalWindow, Element(window)) &&
                JsonElement.DeepEquals(originalSubmission, Element(submission)) &&
                data!.Checkpoint.CoveredSources.SequenceEqual(ContextValidation.GetCoveredManifest(window)),
                "load-time recomposition preserves the winning submission and exact source coverage.");
        }
        foreach (var changed in new[] { "context", "reasoning" })
        {
            using var fixture = new HostFixture(checkpointTargetTokens: 256);
            var submission = await fixture.CaptureAsync();
            var window = CompactionWindow();
            var originalWindow = Element(window); var originalSubmission = Element(submission);
            var mutationEntered = false;
            fixture.Models.OnPrepared = (request, _) =>
            {
                if (!IsCompactor(request) || mutationEntered) return;
                mutationEntered = true;
                if (changed == "context") fixture.Compactor.ContextTokens = 16384;
                else fixture.Compactor.ReasoningTokens = 1024;
                fixture.SaveProfiles();
            };
            var result = await fixture.CompactAsync(submission, window);
            Check(mutationEntered && result.State == WorkflowEffectState.Failed && result.ConversationCheckpoint is null &&
                fixture.Models.Generations == 0 && fixture.Models.Active == 0 &&
                JsonElement.DeepEquals(originalWindow, Element(window)) &&
                JsonElement.DeepEquals(originalSubmission, Element(submission)),
                "a live context or reasoning allowance change during preparation cannot authorize stale inference.");
        }
        using (var fixture = new HostFixture(checkpointTargetTokens: 1024))
        {
            fixture.Compactor.ReasoningMode = "off"; fixture.Compactor.ReasoningTokens = 0; fixture.SaveProfiles();
            fixture.Models.CapacityForRequest = request => IsCompactor(request) ? 4096 : 8192;
            fixture.Models.CountInput = request => IsCompactor(request)
                ? CompactorSourceCharacters(request) > 20000 ? 3500 : 64
                : request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Sum(part => part.Text.Length) / 8 + 32;
            fixture.Models.MetadataFactory = (request, input, capacity) =>
                LlamaPreparedGeneration.ResolveBudget("fixture-instance", fixture.Models.Descriptor,
                    IsCompactor(request) ? request with { AllowResponseClamping = true } : request,
                    input, capacity, "deterministic fixture counter");
            var submission = await fixture.CaptureAsync();
            var window = CompactionWindow(); var originalWindow = Element(window);
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            Check(result.State == WorkflowEffectState.Succeeded && data is not null &&
                fixture.Models.PreparedRecords.Any(item => IsCompactor(item.Request) &&
                    item.Metadata.ResponseBudget < item.Metadata.DesiredResponseBudget) &&
                fixture.Models.GeneratedCompactorRequests.Count > 0 &&
                fixture.Models.GeneratedCompactorRequests.All(request => request.ResponseLimit == 1024 &&
                    CompactorSourceCharacters(request) <= 20000) &&
                data.Usage.All(item => item.Prepared.ResponseBudget == 1024 &&
                    item.Prepared.DesiredResponseBudget == 1024) && fixture.Models.Active == 0,
                "an input-clamped prepared response is released and the source is reduced before any complete-allowance generation.");
            Check(JsonElement.DeepEquals(originalWindow, Element(window)) &&
                data!.Checkpoint.CoveredSources.SequenceEqual(ContextValidation.GetCoveredManifest(window)) &&
                data.Checkpoint.SourceLocators.SequenceEqual(ContextValidation.GetCoveredLocators(window)),
                "retrying smaller input slices preserves full original coverage and locators.");
        }
        using (var fixture = new HostFixture(checkpointTargetTokens: 1024))
        {
            fixture.Models.CountInput = request => IsCompactor(request) ? 64 :
                request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Sum(part => part.Text.Length) / 8 + 32;
            fixture.Models.MetadataFactory = (request, input, capacity) =>
                LlamaPreparedGeneration.ResolveBudget("fixture-instance", fixture.Models.Descriptor,
                    IsCompactor(request) ? request with { ResponseLimit = Math.Min(512, request.ResponseLimit) } : request,
                    input, capacity, "deterministic fixture counter");
            var submission = await fixture.CaptureAsync();
            var window = CompactionWindow(); var originalWindow = Element(window);
            var result = await fixture.CompactAsync(submission, window);
            var data = result.Data?.Deserialize<WorkerWorkflowEffects.CompactData>(CerebralJson.Options);
            var generated = fixture.Models.GeneratedCompactorRequests;
            Check(result.State == WorkflowEffectState.Succeeded && data is not null &&
                fixture.Models.PreparedRecords.Any(item => IsCompactor(item.Request) &&
                    item.Request.ResponseLimit == 1024 && item.Metadata.DesiredResponseBudget == 512) &&
                generated.Count > 0 && generated.All(request => request.ResponseLimit == 512 &&
                    request.ResponseSchema!.Value.GetProperty("properties").GetProperty("checkpoint")
                        .GetProperty("maxLength").GetInt32() == 1536 &&
                    RequestText(request).Contains("240 words", StringComparison.Ordinal)) &&
                data.Usage.All(item => item.Prepared.ResponseBudget == 512) && fixture.Models.Active == 0,
                "a protocol-valid lower provider allowance recomposes the complete request and content contract before entry.");
            Check(JsonElement.DeepEquals(originalWindow, Element(window)) &&
                data!.Checkpoint.CoveredSources.SequenceEqual(ContextValidation.GetCoveredManifest(window)),
                "lower provider authority preserves immutable inputs and complete covered sources.");
        }
        using (var fixture = new HostFixture(checkpointTargetTokens: 256))
        using (var cancellation = new CancellationTokenSource())
        {
            var submission = await fixture.CaptureAsync();
            var window = CompactionWindow(); var originalWindow = Element(window);
            fixture.Models.IgnoreGenerateCancellation = true;
            fixture.Models.OnPrepared = (request, _) => { if (IsCompactor(request)) cancellation.Cancel(); };
            var result = await fixture.CompactAsync(submission, window, cancellation.Token);
            Check(result.State == WorkflowEffectState.Cancelled && result.ConversationCheckpoint is null &&
                fixture.Models.Generations == 0 && fixture.Models.Active == 0 &&
                JsonElement.DeepEquals(originalWindow, Element(window)),
                "cancellation after compactor preparation dispatches no Generate even when the injected adapter ignores cancellation.");
        }
        return checks;
    }

    private static int CompactorSourceCharacters(ModelGenerationRequest request)
    {
        const string marker = "Next source slice (reference):\n";
        var text = RequestText(request);
        var start = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Fixture compactor source frame is absent.");
        return text.Length - start - marker.Length;
    }

    private static bool ConciseScenario(ModelGenerationRequest request, int expectedCharacters = 768, int expectedWords = 112) =>
        request.ResponseSchema is { } schema &&
        schema.GetProperty("type").GetString() == "object" &&
        !schema.GetProperty("additionalProperties").GetBoolean() &&
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["checkpoint"]) &&
        schema.GetProperty("properties").EnumerateObject().Single().Name == "checkpoint" &&
        schema.GetProperty("properties").GetProperty("checkpoint").GetProperty("type").GetString() == "string" &&
        schema.GetProperty("properties").GetProperty("checkpoint").GetProperty("maxLength").GetInt32() == expectedCharacters &&
        RequestText(request).Contains(expectedWords + " words", StringComparison.Ordinal);

    private static string RequestText(ModelGenerationRequest request) => string.Join("\n",
        request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));

    private static ModelGenerationRequest Request(int reasoning) => new("fixture",
        [new("user", [new TextContentPart("EXACT COMPOSED REQUIRED SOURCE")])], 48128, 3072,
        ResponseSchema: Element(new { type = "object", properties = new { answer = new { type = "string" } } }))
        { ReasoningTokens = reasoning };
    private static bool IsCompactor(ModelGenerationRequest request) => request.ResponseSchema is { } schema &&
        schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("checkpoint", out _);
    private static ContextWindow CompactionWindow() => new(7, 0,
        [new("older", "fixture", "conversation", 3, ContextRequirement.Reducible,
            [new("user", [new TextContentPart(new string('x', 32000))])], "older", BeforeCurrentInput: true)],
        new("baseline", 2, 0, [new("covered", 1)], "OLD EXACT CHECKPOINT", "baseline", BeforeCurrentInput: true));
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static JsonElement Element(object value) => CerebralJson.Element(value);

    private sealed class HostFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-budget-shrink-" + Guid.NewGuid().ToString("N"));
        internal readonly StateStore Store;
        internal readonly Configuration Configuration;
        internal readonly AgentProfile Profile;
        internal readonly AgentProfile Compactor;
        internal readonly CountedAdapter Models = new();
        private readonly ModelRecord model;
        private readonly ContextCompactionTests.NoTools tools = new();
        private readonly WorkerWorkflowEffects bridge;
        internal HostFixture(int reasoning = 2048, int? checkpointTargetTokens = null, bool explicitDefaultPolicy = false)
        {
            Directory.CreateDirectory(root); Store = new(root); Configuration = new(Store); Configuration.Get();
            Profile = new() { Id = "lead", ModelId = "fixture", FollowsModelBudgets = false, ContextTokens = 48128,
                ResponseTokens = 3072, ReasoningMode = "on", ReasoningTokens = reasoning,
                AutomaticCompaction = true, CompactionProfileId = "compactor", AllowedTools = [], Capabilities = [] };
            Compactor = new() { Id = "compactor", ModelId = "fixture", FollowsModelBudgets = false, ContextTokens = 48128,
                ResponseTokens = 3072, ReasoningMode = "on", ReasoningTokens = 2048, AllowedTools = [], Capabilities = [] };
            model = new() { Id = "fixture", Path = Path.Combine(root, "fixture.gguf"), AutomaticContextSizing = true,
                ContextTokens = 48128, MaximumContextTokens = 48128, RecommendedContextTokens = 16384 };
            SaveProfiles(); Store.Put("model", model.Id, model);
            bridge = checkpointTargetTokens is { } target
                ? new(new CompactorContentPolicy(target), Store, Configuration, Models, tools)
                : explicitDefaultPolicy
                    ? new(new CompactorContentPolicy(), Store, Configuration, Models, tools)
                    : new(Store, Configuration, Models, tools);
        }
        internal void SaveProfiles() { Store.Put("agent", Profile.Id, Profile); Store.Put("agent", Compactor.Id, Compactor); }
        internal void SetCapacity(int capacity) { model.ContextTokens = capacity; Store.Put("model", model.Id, model); Models.Capacity = capacity; }
        internal async Task<WorkflowSubmission> CaptureAsync() => new("request", "conversation", Profile.Id,
            "fixture:workflow", "1.0.0", "EXACT CURRENT INPUT", "EXACT ASSIGNMENT", Element(new { }),
            await WorkerExecutionSnapshot.CaptureAsync(Store, Profile, Configuration, tools, [], default));
        internal Task<WorkflowCommandResult> GenerateAsync(WorkflowSubmission submission, ContextWindow? window = null,
            int? hostReasoningTokens = null, string effectId = "generate", CancellationToken token = default) =>
            bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1).ExecuteAsync(
                new(effectId, submission, Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [],
                    Context: window, HostReasoningTokens: hostReasoningTokens))), token);
        internal Task<WorkflowCommandResult> ReconcileGenerationAsync(WorkflowSubmission submission) =>
            bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1).ReconcileAsync(
                new("generate", submission, Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: []))), default);
        internal void FailBalanceDelete(bool enabled)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "cerebral.db") }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = enabled ? """
                CREATE TRIGGER fixture_balance_consume_failure BEFORE DELETE ON records
                WHEN OLD.kind='workflow-response-balance'
                BEGIN SELECT RAISE(ABORT,'fixture balance consumption failure'); END;
                """ : "DROP TRIGGER fixture_balance_consume_failure;";
            command.ExecuteNonQuery();
        }
        internal Task<WorkflowCommandResult> CompactAsync(WorkflowSubmission submission, ContextWindow window, CancellationToken token = default) =>
            bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1).ExecuteAsync(
                new("compact", submission, Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: window))), token);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private sealed class CountedAdapter : IModelRuntimeAdapter
    {
        internal int Capacity = 48128, Active, Generations;
        internal bool SupportsReasoning = true, ThrowDisposal;
        internal Action? OnReady;
        internal Func<int, int>? CapacityAtAttempt;
        internal Func<ModelGenerationRequest, int>? CapacityForRequest, CountInput;
        internal Func<ModelGenerationRequest, int, int, PreparedModelMetadata>? MetadataFactory;
        internal Action<ModelGenerationRequest, PreparedModelMetadata>? OnPrepared;
        internal bool IgnoreGenerateCancellation;
        internal readonly List<ModelGenerationRequest> GeneratedCompactorRequests = [];
        internal readonly List<(ModelGenerationRequest Request, PreparedModelMetadata Metadata)> PreparedRecords = [];
        internal readonly List<ModelGenerationRequest> Requests = [];
        public ModelAdapterDescriptor Descriptor => new("budget-fixture", "1", ["text"], SupportsReasoning);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); request.Validate();
            if (Active != 0) throw new InvalidOperationException("First lease must be released before another preparation.");
            var capacity = CapacityForRequest?.Invoke(request) ?? CapacityAtAttempt?.Invoke(Requests.Count) ?? Capacity;
            Requests.Add(request);
            var input = CountInput?.Invoke(request) ?? request.Messages.SelectMany(x => x.Content).OfType<TextContentPart>().Sum(x => x.Text.Length) / 8 + 32;
            var measuredCapacity = Math.Min(capacity, request.ContextLimit);
            var metadata = MetadataFactory?.Invoke(request, input, measuredCapacity) ??
                LlamaPreparedGeneration.ResolveBudget("fixture-instance", Descriptor, request, input,
                    measuredCapacity, "deterministic fixture counter");
            RemotePreparedProtocol.ValidateMetadata(metadata, request);
            PreparedRecords.Add((request, metadata));
            Active++; OnPrepared?.Invoke(request, metadata); OnReady?.Invoke();
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata, request, IsCompactor(request)));
        }
        private sealed class Prepared(CountedAdapter owner, PreparedModelMetadata metadata, ModelGenerationRequest request, bool compactor) : IPreparedModelGeneration
        {
            private bool disposed;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                if (!owner.IgnoreGenerateCancellation) token.ThrowIfCancellationRequested();
                owner.Generations++;
                if (compactor) owner.GeneratedCompactorRequests.Add(request);
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop,
                    compactor ? "{\"checkpoint\":\"Confirmed exact prior facts and current file receipt.\"}" : "{\"answer\":\"fixture\"}",
                    16, metadata.InputTokens));
            }
            public ValueTask DisposeAsync()
            {
                if (!disposed) { disposed = true; owner.Active--; if (owner.ThrowDisposal) throw new IOException("fixture disposal failure"); }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class NativeSlotFixture(int capacity, int inputTokens) : HttpMessageHandler, IModelRuntimeAdapter
    {
        internal readonly LlamaSlotPool Pool = new(1);
        internal readonly List<ModelGenerationRequest> Requests = [];
        internal readonly List<JsonElement> Counted = [];
        internal int CompletionCalls;
        private HttpClient? http;
        public ModelAdapterDescriptor Descriptor => LlamaPreparedReasoningGeneration.Adapter;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Requests.Add(request);
            http ??= new(this, disposeHandler: false) { BaseAddress = new("http://native-budget.fixture/"), Timeout = Timeout.InfiniteTimeSpan };
            var slot = await Pool.AcquireAsync(() => { }, token);
            try
            {
                var prepared = await LlamaPreparedReasoningGeneration.PrepareAsync(http, slot, "native-fixture", request, [], () => false, token);
                RemotePreparedProtocol.ValidateMetadata(prepared.Metadata, request);
                return prepared;
            }
            catch (ModelContextLimitException error)
            { RemotePreparedProtocol.ValidateMetadata(error.Metadata, request, contextLimited: true); throw; }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/slots") return Json(new[] { new { id = 0, n_ctx = capacity, is_processing = false } });
            if (request.RequestUri.AbsolutePath == "/v1/chat/completions/input_tokens")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Counted.Add(body.RootElement.Clone());
                return Json(new { @object = "response.input_tokens", input_tokens = inputTokens });
            }
            CompletionCalls++; throw new InvalidOperationException("No completion dispatch belongs in this preparation fixture.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) http?.Dispose(); base.Dispose(disposing); }
    }
}
