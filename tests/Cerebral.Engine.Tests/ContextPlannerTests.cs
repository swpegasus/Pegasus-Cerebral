using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Engine.Context;

namespace Cerebral.Engine.Tests;

/// <summary>Exact-budget planning against a deterministic adapter fixture; no real tokenizer, inference,
/// provider, file access or SQLite calls. Adapter counts are deliberately independent of text length.</summary>
public static class ContextPlannerTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            checks++;
            Console.WriteLine("PASS: " + description);
        }
        static bool Reject(Action action)
        {
            try { action(); return false; } catch (ArgumentException) { return true; }
        }
        static async Task<bool> RejectAsync(Func<Task> action)
        {
            try { await action(); return false; } catch (ArgumentException) { return true; }
        }
        static ModelInputMessage Text(string value, string role = "user") => new(role, [new TextContentPart(value)]);
        static ContextContribution Source(string id, ContextRequirement requirement, string text, int priority = 0) =>
            new(id, "fixture.owner", "history", 1, requirement, [new("user", [new TextContentPart(text)])], Priority: priority);
        static ContextWindow Window(params ContextContribution[] sources) => new(5, 2, sources);
        var basis = new ModelGenerationRequest("fixture.model", [Text("policy", "system"), Text("exact current request")], 512, 128);
        var weights = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["policy"] = 20, ["exact current request"] = 80, ["anchor"] = 20,
            ["history"] = 500, ["low-a"] = 200, ["low-b"] = 100, ["high"] = 80,
            ["clamped history"] = 330, ["required overflow"] = 450,
            ["pressure history"] = 180, ["below pressure history"] = 170,
            ["boundary history"] = 159, ["required pressure"] = 270
        };
        FixtureAdapter Adapter() => new(weights);

        var adapter = Adapter();
        var simple = Window(Source("required", ContextRequirement.Required, "anchor"));
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis, simple))
        {
            Check(prepared.Projection.Status == ContextProjectionStatus.Ready && prepared.Prepared is not null &&
                ReferenceEquals(prepared.Prepared, adapter.Leases.Single()) && prepared.Projection.Metadata!.InputTokens == 120,
                "ready context returns the same exact prepared lease and measured adapter count");
            Check(adapter.Requests[0].Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                .Select(part => part.Text).SequenceEqual(["policy", "exact current request", "anchor"]) &&
                prepared.Projection.IncludedSources.SequenceEqual([new ContextSourceReference("required", 1)]) && adapter.Generated == 0,
                "base instructions and exact input precede required sources without dispatching inference");
            var generated = await prepared.Prepared!.GenerateAsync(null, default);
            Check(generated.Finish == ModelGenerationFinish.Stop && adapter.Generated == 1 && adapter.Requests.Count == 1,
                "generation uses the returned preparation without retokenizing or preparing again");
        }
        Check(adapter.Leases.All(lease => lease.Disposed), "disposing the preparation releases its model lease");

        adapter = Adapter();
        var optional = Window(Source("low-a", ContextRequirement.Optional, "low-a"),
            Source("required", ContextRequirement.Required, "anchor"), Source("high", ContextRequirement.Optional, "high", 10),
            Source("low-b", ContextRequirement.Optional, "low-b"));
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis, optional))
        {
            Check(prepared.Projection.Status == ContextProjectionStatus.Ready && adapter.Requests.Count == 2 &&
                prepared.Projection.Omissions.Select(item => item.Id).SequenceEqual(["low-a", "low-b"]) &&
                prepared.Projection.IncludedSources.Select(source => source.Id).SequenceEqual(["required", "high"]),
                "only the lowest-priority optional group is omitted and its omissions remain explicit");
            Check(optional.Contributions.Count == 4 && optional.Contributions[0].Messages[0].Content[0] is TextContentPart { Text: "low-a" },
                "optional projection changes never alter source history or clip its text");
        }

        adapter = Adapter();
        var reducible = Window(Source("required", ContextRequirement.Required, "anchor"), Source("history", ContextRequirement.Reducible, "history"));
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis, reducible))
        {
            Check(prepared.Prepared is null && prepared.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                prepared.Projection.Metadata is { InputTokens: 620, ResponseBudget: 0, DesiredResponseBudget: 128 } && adapter.Generated == 0,
                "oversized reducible history reports compaction after the exact required floor fits and preserves the full-input count");
            Check(prepared.Projection.IncludedSources.Select(source => source.Id).SequenceEqual(["required", "history"]),
                "compaction diagnostics describe the same full source projection as their measured input count");
            Check(adapter.Requests.Count == 2 && adapter.Requests[1].ResponseLimit == 64 && adapter.Requests[1].ResponseSharePercent is null &&
                adapter.Leases.Single().Disposed && adapter.Requests[1].Messages.Last().Content[0] is TextContentPart { Text: "anchor" },
                "required-floor probe has a fixed useful response, retains required source content and is disposed");
        }

        adapter = Adapter();
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis,
            Window(Source("required", ContextRequirement.Required, "required overflow"), Source("history", ContextRequirement.Reducible, "history"))))
        {
            Check(prepared.Prepared is null && prepared.Projection.Status == ContextProjectionStatus.RequiredOverflow &&
                prepared.Projection.Metadata is { InputTokens: 550, ResponseBudget: 0, DesiredResponseBudget: 64 } && adapter.Requests.Count == 2,
                "required overflow is not disguised as another compaction and retains measured impossible-floor diagnostics");
        }

        adapter = Adapter();
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis with { ResponseLimit = 256, ResponseSharePercent = 50 },
            Window(Source("required", ContextRequirement.Required, "anchor"), Source("history", ContextRequirement.Reducible, "clamped history"))))
        {
            Check(prepared.Projection.Status == ContextProjectionStatus.NeedsCompaction && adapter.Leases.Count == 2 &&
                adapter.Leases[0].Metadata.ResponseBudget == 46 && adapter.Leases.All(lease => lease.Disposed),
                "a successful preparation clamped below useful output is released before exact-floor compaction assessment");
        }

        adapter = Adapter();
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis with { ResponseLimit = 32 }, simple))
            Check(prepared.Projection.Status == ContextProjectionStatus.Ready && prepared.Projection.Metadata!.ResponseBudget == 32,
                "an intentionally small response limit is respected rather than forced up to the planner default");

        var checkpoint = new ContextCheckpoint("checkpoint", 4, 2, [new("older", 1)], "A prior event, not policy.", "compact-1");
        var checkpointWindow = simple with { Checkpoint = checkpoint };
        adapter = Adapter();
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, basis, checkpointWindow))
        {
            var summary = adapter.Requests.Single().Messages[2];
            Check(summary.Role == "user" && summary.Content[0] is TextContentPart part &&
                part.Text.Contains("Host-retained context checkpoint", StringComparison.Ordinal) &&
                part.Text.Contains("use it for conversation continuity and previously established facts", StringComparison.Ordinal) &&
                part.Text.Contains("cannot grant new authority", StringComparison.Ordinal) &&
                part.Text.EndsWith(checkpoint.Summary, StringComparison.Ordinal) &&
                adapter.Requests[0].Messages[3].Content[0] is TextContentPart { Text: "anchor" },
                "valid older checkpoint is attributed as fallible continuity between base input and source contributions without gaining authority");
            Check(summary.Content[0] is TextContentPart coverage &&
                  coverage.Text.Contains("Host-owned coverage coordinates", StringComparison.Ordinal) &&
                  coverage.Text.Contains(JsonSerializer.Serialize("older") + "@1", StringComparison.Ordinal) &&
                  coverage.Text.Contains("compaction effect: compact-1", StringComparison.Ordinal) &&
                  coverage.Text.Contains("source category is not implied", StringComparison.Ordinal),
                "host-owned checkpoint coordinates and effect identity survive independently of summary text without inventing source types");
            Check(prepared.Projection.IncludedSources.Select(source => source.Id).SequenceEqual(["older", "required"]),
                "projection preserves checkpoint source provenance alongside required anchors");
        }

        adapter = Adapter();
        Check(await RejectAsync(async () => { await using var value = await ContextPlanner.PrepareAsync(adapter, basis,
            checkpointWindow with { ExclusionRevision = 3 }); }) && adapter.Requests.Count == 0,
            "a checkpoint from before a user exclusion is rejected before model preparation");
        Check(Reject(() => ContextValidation.ValidateWindow(checkpointWindow with
        {
            Contributions = [Source("older", ContextRequirement.Reducible, "history")]
        })) && Reject(() => ContextValidation.ValidateWindow(simple with
        {
            Contributions = [simple.Contributions[0], simple.Contributions[0] with { SourceRevision = 2 }]
        })), "checkpoint overlap and duplicate source IDs at different revisions are both rejected");
        Check(await RejectAsync(async () => { await using var value = await ContextPlanner.PrepareAsync(adapter, basis,
            Window(new ContextContribution("late", "owner", "history", 1, ContextRequirement.Required, [Text("late policy", "system")]))); }) &&
            await RejectAsync(async () => { await using var value = await ContextPlanner.PrepareAsync(adapter,
                basis with { Messages = [Text("input"), Text("late policy", "system")] }, simple); }) && adapter.Requests.Count == 0,
            "late system messages from either source contributions or base input cannot elevate reference text");
        Check(Reject(() => ContextValidation.ValidateWindow(simple with { Revision = -1 })) &&
            Reject(() => ContextValidation.ValidateWindow(Window(Source("bad id", ContextRequirement.Optional, "anchor")))) &&
            Reject(() => ContextValidation.ValidateWindow(Window(simple.Contributions[0] with { SourceRevision = -1 }))),
            "invalid source identifiers and negative context/source revisions fail at the boundary");

        var candidateWindow = reducible with { Checkpoint = checkpoint };
        var candidate = new ContextCheckpoint("candidate", 5, 2, ContextValidation.GetCoveredManifest(candidateWindow), "Measured summary", "compact-2")
            { SourceLocators = ContextValidation.GetCoveredLocators(candidateWindow) };
        ContextPlanner.ValidateCandidate(candidateWindow, candidate, "compact-2");
        Check(candidate.CoveredSources.SequenceEqual([new ContextSourceReference("checkpoint", 4), new("history", 1)]),
            "candidate manifest rolls prior coverage into its durable checkpoint identity and retains current reducible sources");
        Check(Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { ContextRevision = 4 }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { ExclusionRevision = 1 }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate, "other-effect")),
            "stale context, stale exclusion, and foreign effect candidates cannot be adopted");
        Check(Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { CoveredSources = [new("checkpoint", 4), new("history", 2)] }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { CoveredSources = [new("checkpoint", 4), new("required", 1)] }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { CoveredSources = [new("checkpoint", 4), new("checkpoint", 5)] }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { CoveredSources = [new("history", 1), new("checkpoint", 4)] }, "compact-2")),
            "changed, required, duplicated and reordered checkpoint coverage cannot replace the host source manifest");
        Check(Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { Summary = " " }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { Summary = new('x', 32_769) }, "compact-2")) &&
            Reject(() => ContextValidation.ValidateCandidate(candidateWindow, candidate with { Summary = new('<', 20_000) }, "compact-2")),
            "candidate summaries reject empty text, excessive characters and excessive escaped serialization size");
        var many = Enumerable.Range(0, 128).Select(index => Source("source-" + index, ContextRequirement.Optional, "anchor")).ToArray();
        Check(Reject(() => ContextValidation.ValidateWindow(new(5, 2, many, checkpoint))),
            "combined checkpoint and live manifest limit is enforced rather than allowing 128 sources in each");
        var largePrior = new ContextCheckpoint("large-prior", 120, 2,
            Enumerable.Range(0, 120).Select(index => new ContextSourceReference("covered-" + index, 1)).ToArray(),
            "A durable prior checkpoint.", "compact-large");
        var rollingWindow = new ContextWindow(121, 2,
            [Source("new-history", ContextRequirement.Reducible, "history"), Source("current", ContextRequirement.Required, "anchor")],
            largePrior);
        var rollingManifest = ContextValidation.GetCoveredManifest(rollingWindow);
        var rollingCandidate = new ContextCheckpoint("rolled", 121, 2, rollingManifest,
            "Prior checkpoint and new history.", "compact-rolled")
            { SourceLocators = ContextValidation.GetCoveredLocators(rollingWindow) };
        ContextValidation.ValidateCandidate(rollingWindow, rollingCandidate, "compact-rolled");
        Check(rollingManifest.SequenceEqual([new ContextSourceReference("large-prior", 120), new("new-history", 1)]),
            "a large retained checkpoint rolls into one provenance reference before new coverage is added");


        var attributedWindow = new ContextWindow(8, 0, [
            new("exact-source", "provider.alpha", "sdk.reference", 7, ContextRequirement.Reducible,
                [Text("history")], "opaque:<source>\"café", Complete: false),
            Source("anchor-source", ContextRequirement.Required, "anchor")]);
        var attributed = new ContextCheckpoint("attributed", 8, 0, ContextValidation.GetCoveredManifest(attributedWindow),
            "Fallible generated text without source attribution.", "attributed-effect")
            { SourceLocators = ContextValidation.GetCoveredLocators(attributedWindow) };
        ContextValidation.ValidateCandidate(attributedWindow, attributed, "attributed-effect");
        Check(attributed.SourceLocators.Single() == new ContextSourceLocator("exact-source", 7, "provider.alpha",
                "sdk.reference", "opaque:<source>\"café", false) &&
              new ContextSourceReference("exact-source", 7) == attributed.CoveredSources.Single(),
            "source attribution and opaque Unicode handles are retained independently without changing identity equality");
        var locator = attributed.SourceLocators.Single();
        foreach (var altered in new[] { locator with { Owner = "other.provider" }, locator with { Category = "other.type" },
                     locator with { ExactReadHandle = "other-handle" }, locator with { Complete = true },
                     locator with { Revision = 8 }, locator with { Id = "other-source" } })
            Check(Reject(() => ContextValidation.ValidateCandidate(attributedWindow,
                attributed with { SourceLocators = [altered] }, "attributed-effect")),
                "candidate rejects altered host source metadata: " + altered);
        Check(Reject(() => ContextValidation.ValidateCandidate(attributedWindow, attributed with { SourceLocators = [] }, "attributed-effect")) &&
              Reject(() => ContextValidation.ValidateWindow(new(8, 0, [], attributed with { SourceLocators = [locator, locator] }))) &&
              Reject(() => ContextValidation.ValidateWindow(new(8, 0, [], attributed with { SourceLocators = null! }))),
            "new candidates cannot omit locators and malformed retained maps fail validation");
        var legacyJson = JsonSerializer.SerializeToNode(checkpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        legacyJson.Remove("sourceLocators");
        var legacy = legacyJson.Deserialize<ContextCheckpoint>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        ContextValidation.ValidateWindow(new(5, 2, [], legacy));
        Check(legacy.SourceLocators.Count == 0 && legacy.CoveredSources.SequenceEqual(checkpoint.CoveredSources),
            "legacy JSON loads with unknown attribution through the unchanged checkpoint constructor");
        var rolledWindow = new ContextWindow(9, 0, [Source("new-evidence", ContextRequirement.Reducible, "history")], attributed);
        var rolledLocators = ContextValidation.GetCoveredLocators(rolledWindow);
        Check(rolledLocators.Count == 2 && rolledLocators[0] == new ContextSourceLocator(attributed.Id,
                attributed.ContextRevision, "cerebral.engine", "context.checkpoint", attributed.CompactionEffectId, true) &&
              ContextValidation.GetCoveredLocators(candidateWindow)[0].ExactReadHandle is null,
            "one prior checkpoint locator bounds each roll-up and a legacy read handle is not invented");
        var oversizedLocators = Enumerable.Range(0, 60).Select(i => new ContextSourceLocator("bounded-" + i, 1,
            "provider", "evidence", new string('x', 2048), true)).ToArray();
        var oversizedCheckpoint = new ContextCheckpoint("oversized-map", 9, 0,
            oversizedLocators.Select(x => new ContextSourceReference(x.Id, x.Revision)).ToArray(),
            "Small summary with excessive locator bytes.", "oversized-map")
            { SourceLocators = oversizedLocators };
        Check(Reject(() => ContextValidation.ValidateWindow(new(9, 0, [], oversizedCheckpoint))) &&
              Reject(() => ContextValidation.ValidateWindow(new(8, 0, [],
                  attributed with { SourceLocators = [locator with { ExactReadHandle = new string('x', 2049) }] }))),
            "metadata participates in the serialized checkpoint limit and exact handles are rejected instead of clipped");

        adapter = Adapter();
        var locatorList = attributed.SourceLocators.ToList();
        var coverageList = attributed.CoveredSources.ToList();
        var metadataReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.BeforePrepare = async (_, _) => { metadataReady.SetResult(); await metadataRelease.Task; };
        var metadataPreparation = ContextPlanner.PrepareAsync(adapter, basis, new(8, 0, [],
            attributed with { SourceLocators = locatorList, CoveredSources = coverageList }));
        await metadataReady.Task;
        locatorList.Clear(); coverageList.Clear();
        metadataRelease.SetResult();
        await using (var frozenMetadata = await metadataPreparation)
        {
            var rendered = adapter.Requests.Single().Messages.SelectMany(x => x.Content).OfType<TextContentPart>()
                .Single(x => x.Text.Contains("Host-retained context checkpoint", StringComparison.Ordinal)).Text;
            Check(rendered.Contains(JsonSerializer.Serialize(attributed.SourceLocators), StringComparison.Ordinal) &&
                  rendered.Contains("Complete describes the source projection", StringComparison.Ordinal) &&
                  rendered.Contains("history eventId=checkpoint ID", StringComparison.Ordinal) &&
                  frozenMetadata.Projection.IncludedSources.Single() == new ContextSourceReference("exact-source", 7),
                "frozen escaped locator metadata reaches prepared input and cannot be changed across an await");
        }
        Check(adapter.Leases.Single().Disposed, "locator-aware preparation releases its exact lease");

        adapter = Adapter();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.BeforePrepare = async (_, _) => { ready.SetResult(); await release.Task; };
        using (var document = JsonDocument.Parse("{\"receipt\":\"exact\"}"))
        {
            var metadata = new Dictionary<string, string> { ["origin"] = "original" };
            var parts = new List<ContentPart>
            {
                new StructuredContentPart("receipt.v1", document.RootElement),
                new ImageContentPart(new("image", 1, "image/png", "view.png", 16, metadata))
            };
            var messages = new List<ModelInputMessage> { new("user", parts) };
            var sources = new List<ContextContribution> { new("receipt", "tools", "evidence", 4, ContextRequirement.Required, messages) };
            var sourceWindow = new ContextWindow(7, 3, sources);
            var preparation = ContextPlanner.PrepareAsync(adapter, basis with { ResponseSchema = document.RootElement }, sourceWindow);
            await ready.Task;
            metadata["origin"] = "changed"; parts.Clear(); messages.Clear(); sources.Clear(); document.Dispose();
            release.SetResult();
            await using var frozen = await preparation;
            var received = adapter.Requests.Single();
            Check(received.Messages.Last().Content[0] is StructuredContentPart structured && structured.Value.GetProperty("receipt").GetString() == "exact" &&
                received.Messages.Last().Content[1] is ImageContentPart image && image.Attachment.Metadata!["origin"] == "original" &&
                received.ResponseSchema!.Value.GetProperty("receipt").GetString() == "exact" && frozen.Projection.IncludedSources.Single().Revision == 4,
                "typed receipts, binary references, schema and source lists survive mutation and JSON disposal during preparation");
        }

        adapter = Adapter();
        using (var cancelled = new CancellationTokenSource())
        {
            adapter.AfterPrepare = _ => cancelled.Cancel();
            var observed = false;
            try { await using var value = await ContextPlanner.PrepareAsync(adapter, basis, simple, cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Check(observed && adapter.Leases.Single().Disposed && adapter.Generated == 0,
                "cancellation after lease acquisition releases the preparation and never becomes a compaction request");
        }
        adapter = Adapter();
        adapter.BeforePrepare = (_, _) => throw new IOException("Provider unavailable");
        var providerFailed = false;
        try { await using var value = await ContextPlanner.PrepareAsync(adapter, basis, optional); }
        catch (IOException) { providerFailed = true; }
        Check(providerFailed && adapter.Requests.Count == 1,
            "provider errors propagate instead of removing context or pretending that compaction can repair them");

        adapter = Adapter();
        adapter.BeforePrepare = (_, _) =>
        {
            if (adapter.Leases.Any(lease => !lease.Disposed))
                throw new InvalidOperationException("A prior prepared slot is still held.");
            return Task.CompletedTask;
        };
        var pressureWindow = Window(Source("current-tool", ContextRequirement.Required, "anchor"),
            Source("older-tool", ContextRequirement.Reducible, "pressure history"));
        await using (var pressure = await ContextPlanner.PrepareAsync(adapter, basis, pressureWindow, compactionTriggerPercent: 80))
        {
            Check(pressure.Prepared is null && pressure.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                pressure.Projection.Metadata is { InputTokens: 300, ResponseBudget: 128 } &&
                adapter.Requests.Count == 2 && adapter.Generated == 0 && adapter.Leases.All(lease => lease.Disposed),
                "80-percent input-capacity pressure compacts before overflow and releases both single-slot probes without inference");
            Check(adapter.Requests[1].Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                    .Select(part => part.Text).SequenceEqual(["policy", "exact current request", "anchor"]) &&
                pressure.Projection.IncludedSources.SequenceEqual([new ContextSourceReference("current-tool", 1), new("older-tool", 1)]),
                "proactive compaction preserves exact current input and latest tool evidence with full-source measured provenance");
        }
        adapter = Adapter();
        await using (var below = await ContextPlanner.PrepareAsync(adapter, basis, pressureWindow with
            { Contributions = [pressureWindow.Contributions[0], pressureWindow.Contributions[1] with
                { Messages = [Text("below pressure history")] }] }, compactionTriggerPercent: 80))
            Check(below.Prepared is not null && below.Projection.Status == ContextProjectionStatus.Ready &&
                adapter.Requests.Count == 1 && below.Projection.Metadata!.InputTokens == 290,
                "input below the configured threshold keeps the original prepared lease without extra probes");
        adapter = Adapter();
        await using (var boundary = await ContextPlanner.PrepareAsync(adapter, basis with { ContextLimit = 516 },
            Window(Source("current", ContextRequirement.Required, "anchor"),
                Source("older", ContextRequirement.Reducible, "boundary history")), compactionTriggerPercent: 75))
            Check(boundary.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                boundary.Projection.Metadata!.InputTokens == 279,
                "an exact percentage boundary triggers automatic compaction without rounding below the threshold");
        adapter = Adapter();
        await using (var disabled = await ContextPlanner.PrepareAsync(adapter, basis, pressureWindow))
            Check(disabled.Prepared is not null && adapter.Requests.Count == 1,
                "disabled proactive policy retains overflow-only planning");
        adapter = Adapter();
        await using (var currentOnly = await ContextPlanner.PrepareAsync(adapter, basis with { ResponseLimit = 256 },
            checkpointWindow, compactionTriggerPercent: 40))
            Check(currentOnly.Prepared is not null && adapter.Requests.Count == 1,
                "a fresh checkpoint and required content are not compacted again without newer reducible history");
        adapter = Adapter();
        adapter.BeforePrepare = (_, _) =>
        {
            if (adapter.Leases.Any(lease => !lease.Disposed))
                throw new InvalidOperationException("A prior prepared slot is still held.");
            return Task.CompletedTask;
        };
        await using (var requiredPressure = await ContextPlanner.PrepareAsync(adapter, basis with { ContextLimit = 1024 },
            Window(Source("current-tool", ContextRequirement.Required, "required pressure"),
                Source("older", ContextRequirement.Reducible, "pressure history")), compactionTriggerPercent: 40))
            Check(requiredPressure.Prepared is not null && requiredPressure.Projection.Status == ContextProjectionStatus.Ready &&
                adapter.Requests.Count == 3 && adapter.Requests[0].Messages.SequenceEqual(adapter.Requests[2].Messages) &&
                adapter.Leases.Take(2).All(lease => lease.Disposed),
                "required input above the trigger resumes the exact full request without a futile compaction loop");
        adapter = Adapter();
        using (var pressureCancellation = new CancellationTokenSource())
        {
            adapter.AfterPrepare = _ => { if (adapter.Leases.Count == 2) pressureCancellation.Cancel(); };
            var cancelled = false;
            try { await using var pressure = await ContextPlanner.PrepareAsync(adapter, basis, pressureWindow,
                cancellationToken: pressureCancellation.Token, compactionTriggerPercent: 80); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && adapter.Leases.All(lease => lease.Disposed) && adapter.Generated == 0,
                "cancelling a proactive floor probe releases all leases and never dispatches generation");
        }
        adapter = Adapter();
        adapter.BeforePrepare = (_, _) => adapter.Requests.Count == 2
            ? throw new IOException("Provider unavailable during floor measurement") : Task.CompletedTask;
        var pressureProviderFailed = false;
        try { await using var pressure = await ContextPlanner.PrepareAsync(adapter, basis, pressureWindow, compactionTriggerPercent: 80); }
        catch (IOException) { pressureProviderFailed = true; }
        Check(pressureProviderFailed && adapter.Leases.All(lease => lease.Disposed),
            "proactive floor-provider failure propagates without retaining a slot or pretending compaction succeeded");
        adapter = Adapter();
        Check(await RejectAsync(async () => { await using var invalid = await ContextPlanner.PrepareAsync(adapter, basis, simple,
                    compactionTriggerPercent: -1); }) &&
              await RejectAsync(async () => { await using var invalid = await ContextPlanner.PrepareAsync(adapter, basis, simple,
                    compactionTriggerPercent: 101); }) && adapter.Requests.Count == 0,
            "invalid trigger percentages fail before preparing a model");

        var preservingBasis = basis with { ResponseLimit = 256, ResponseSharePercent = 50 };
        adapter = Adapter();
        await using (var legacyClamped = await ContextPlanner.PrepareAsync(adapter, preservingBasis, pressureWindow))
            Check(legacyClamped.Prepared is not null && legacyClamped.Projection.Status == ContextProjectionStatus.Ready &&
                legacyClamped.Projection.Metadata is { InputTokens: 300, ResponseBudget: 196, DesiredResponseBudget: 256 } &&
                adapter.Requests.Count == 1 && adapter.Generated == 0,
                "legacy planning still permits a measured clamped response above its useful floor");
        Check(adapter.Leases.Single().Disposed, "legacy clamped lease remains caller-owned and is released normally");

        adapter = Adapter();
        adapter.BeforePrepare = (_, _) =>
        {
            if (adapter.Leases.Any(lease => !lease.Disposed))
                throw new InvalidOperationException("Preserving planner retained a prior single-slot lease.");
            return Task.CompletedTask;
        };
        await using (var preserving = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis, pressureWindow))
        {
            Check(preserving.Prepared is null && preserving.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                preserving.Projection.Metadata is { InputTokens: 300, ResponseBudget: 196, DesiredResponseBudget: 256 } &&
                adapter.Requests.Count == 2 && adapter.Leases.All(lease => lease.Disposed) && adapter.Generated == 0,
                "preserving planning requests compaction instead of dispatching an otherwise useful but clamped response");
            Check(adapter.Requests[1].ResponseLimit == 256 && adapter.Requests[1].ResponseSharePercent is null &&
                adapter.Requests[1].Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                    .Select(part => part.Text).SequenceEqual(["policy", "exact current request", "anchor"]) &&
                preserving.Projection.IncludedSources.SequenceEqual([new ContextSourceReference("current-tool", 1), new("older-tool", 1)]) &&
                pressureWindow.Contributions.Count == 2,
                "required-floor probe pins the measured desired response and preserves exact source content and provenance");
        }

        var desiredOverflow = Window(Source("required", ContextRequirement.Required, "required pressure"),
            Source("history", ContextRequirement.Reducible, "below pressure history"));
        adapter = Adapter();
        await using (var legacyFloor = await ContextPlanner.PrepareAsync(adapter, preservingBasis, desiredOverflow))
            Check(legacyFloor.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                adapter.Requests[1].ResponseLimit == 64 && adapter.Leases.Single().Metadata.InputTokens == 370 &&
                adapter.Leases.Single().Disposed && adapter.Generated == 0,
                "compatibility floor can still fit 64 tokens even when the selected normal response cannot fit required input");
        adapter = Adapter();
        await using (var preservingFloor = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis, desiredOverflow))
            Check(preservingFloor.Prepared is null && preservingFloor.Projection.Status == ContextProjectionStatus.RequiredOverflow &&
                preservingFloor.Projection.Metadata is { InputTokens: 370, ResponseBudget: 0, DesiredResponseBudget: 256 } &&
                preservingFloor.Projection.IncludedSources.SequenceEqual([new ContextSourceReference("required", 1)]) &&
                adapter.Requests.Count == 2 && adapter.Requests[1].ResponseLimit == 256 &&
                adapter.Leases.Count == 0 && adapter.Generated == 0,
                "required input that cannot retain the desired response reports overflow without generation or a compaction loop");

        adapter = Adapter();
        await using (var chosenSmall = await ContextPlanner.PreparePreservingResponseAsync(adapter, basis with { ResponseLimit = 32 }, simple))
            Check(chosenSmall.Prepared is not null && chosenSmall.Projection.Status == ContextProjectionStatus.Ready &&
                chosenSmall.Projection.Metadata is { ResponseBudget: 32, DesiredResponseBudget: 32 } &&
                adapter.Requests.Single().ResponseLimit == 32,
                "preserving planning respects a deliberately chosen small response rather than increasing its output allowance");
        Check(adapter.Leases.Single().Disposed && adapter.Generated == 0, "chosen small-response planning releases its lease without inference");

        var preloadBasis = basis with { ContextLimit = 2048, ResponseLimit = 1024, ResponseSharePercent = 25 };
        adapter = Adapter(); adapter.EffectiveContext = 512;
        await using (var measuredNarrow = await ContextPlanner.PreparePreservingResponseAsync(adapter, preloadBasis, simple))
            Check(measuredNarrow.Prepared is not null && measuredNarrow.Projection.Status == ContextProjectionStatus.Ready &&
                measuredNarrow.Projection.Metadata is { EffectiveContext: 512, ResponseBudget: 128, DesiredResponseBudget: 128 } &&
                adapter.Requests.Single() is { ContextLimit: 2048, ResponseLimit: 1024, ResponseSharePercent: 25 } &&
                adapter.Generated == 0,
                "fresh measured desired response at a smaller effective context is accepted without demanding the raw preload response limit");
        Check(adapter.Leases.Single().Disposed, "narrow measured response returns one exact caller-owned lease");

        adapter = Adapter(); adapter.EffectiveContext = 512;
        await using (var narrowedPressure = await ContextPlanner.PreparePreservingResponseAsync(adapter, preloadBasis, pressureWindow,
            compactionTriggerPercent: 80))
            Check(narrowedPressure.Projection.Status == ContextProjectionStatus.NeedsCompaction &&
                narrowedPressure.Projection.Metadata is { EffectiveContext: 512, ResponseBudget: 128, DesiredResponseBudget: 128 } &&
                adapter.Requests.Count == 2 && adapter.Requests[1].ResponseLimit == 128 &&
                adapter.Requests[1].ResponseSharePercent is null && adapter.Leases.All(lease => lease.Disposed) && adapter.Generated == 0,
                "proactive required-floor pressure uses the measured narrowed reserve instead of the preload ceiling");

        adapter = Adapter();
        var headroomOptional = Window(Source("low", ContextRequirement.Optional, "pressure history"),
            Source("required", ContextRequirement.Required, "anchor"), Source("high", ContextRequirement.Optional, "high", 10));
        await using (var optionalHeadroom = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis, headroomOptional))
            Check(optionalHeadroom.Prepared is not null && optionalHeadroom.Projection.Status == ContextProjectionStatus.Ready &&
                optionalHeadroom.Projection.Metadata is { InputTokens: 200, ResponseBudget: 256, DesiredResponseBudget: 256 } &&
                optionalHeadroom.Projection.Omissions.Select(item => item.Id).SequenceEqual(["low"]) &&
                optionalHeadroom.Projection.IncludedSources.Select(item => item.Id).SequenceEqual(["required", "high"]) &&
                adapter.Requests.Count == 2 && adapter.Leases[0].Metadata.ResponseBudget == 116 &&
                adapter.Leases[0].Disposed && headroomOptional.Contributions.Count == 3 && adapter.Generated == 0,
                "optional projection can free the desired response before compaction while preserving required input and explicit omissions");
        Check(adapter.Leases.All(lease => lease.Disposed), "all optional headroom preparations release their independent leases");

        adapter = Adapter();
        await using (var freshCheckpoint = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis,
            checkpointWindow, compactionTriggerPercent: 10))
        {
            Check(freshCheckpoint.Prepared is not null && freshCheckpoint.Projection.Status == ContextProjectionStatus.Ready &&
                freshCheckpoint.Projection.Metadata is { InputTokens: 130, ResponseBudget: 256, DesiredResponseBudget: 256 } &&
                adapter.Requests.Count == 1 && freshCheckpoint.Projection.IncludedSources
                    .SequenceEqual([new ContextSourceReference("older", 1), new("required", 1)]),
                "checkpoint without fresh reducible history resumes with the full desired response instead of recompacting its summary");
            var result = await freshCheckpoint.Prepared!.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && adapter.Generated == 1 && adapter.Requests.Count == 1,
                "checkpoint response uses the exact returned lease without corrective preparation");
        }
        Check(adapter.Leases.Single().Disposed, "resumed checkpoint generation releases the exact prepared lease");

        foreach (var cancelAtLease in new[] { 1, 2 })
        {
            adapter = Adapter();
            using var preservingCancellation = new CancellationTokenSource();
            adapter.AfterPrepare = _ => { if (adapter.Leases.Count == cancelAtLease) preservingCancellation.Cancel(); };
            var cancelled = false;
            try
            {
                await using var preparation = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis,
                    pressureWindow, cancellationToken: preservingCancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && adapter.Requests.Count == cancelAtLease && adapter.Leases.Count == cancelAtLease &&
                adapter.Leases.All(lease => lease.Disposed) && adapter.Generated == 0,
                "preserving-response cancellation releases every acquired lease before any inference at lease " + cancelAtLease);
        }
        adapter = Adapter();
        adapter.BeforePrepare = (_, _) =>
        {
            if (adapter.Leases.Any(lease => !lease.Disposed))
                throw new InvalidOperationException("Preserving planner retained a lease across provider failure.");
            return adapter.Requests.Count == 2
                ? throw new IOException("Provider unavailable during preserved-response floor") : Task.CompletedTask;
        };
        var preservingProviderFailed = false;
        try { await using var preparation = await ContextPlanner.PreparePreservingResponseAsync(adapter, preservingBasis, pressureWindow); }
        catch (IOException) { preservingProviderFailed = true; }
        Check(preservingProviderFailed && adapter.Requests.Count == 2 && adapter.Leases.Single().Disposed && adapter.Generated == 0,
            "provider failure during preserved-response floor remains a failure and leaves no prepared slot or generation");

        Console.WriteLine($"Context planner: {checks} deterministic adapter assertions passed; no live tokenizer or model inference.");
    }

    private sealed class FixtureAdapter(IReadOnlyDictionary<string, int> weights) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.adapter", "1.0.0", ["text", "image", "structured"]);
        public List<ModelGenerationRequest> Requests { get; } = [];
        public List<FixtureLease> Leases { get; } = [];
        public int Generated { get; private set; }
        public Func<ModelGenerationRequest, CancellationToken, Task>? BeforePrepare { get; set; }
        public Action<FixtureLease>? AfterPrepare { get; set; }
        // Deterministic actual-capacity hook; unset preserves every existing fixture budget.
        public int? EffectiveContext { get; set; }

        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (BeforePrepare is not null) await BeforePrepare(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var input = request.Messages.SelectMany(message => message.Content)
                .Sum(part => part is TextContentPart text && weights.TryGetValue(text.Text, out var tokens) ? tokens : 10);
            const int reserve = 16;
            var effective = Math.Min(request.ContextLimit, EffectiveContext ?? request.ContextLimit);
            var desired = Math.Min(request.ResponseLimit, request.ResponseSharePercent is { } share ? effective * share / 100 : request.ResponseLimit);
            var available = Math.Max(0, effective - input - reserve);
            var response = request.ResponseSharePercent is not null ? Math.Min(desired, available) : desired;
            var fits = response > 0 && input + response + reserve <= effective;
            var metadata = new PreparedModelMetadata("fixture-instance", Descriptor.AdapterId, Descriptor.Version, request.ModelId,
                input, effective, fits ? response : 0, "deterministic fixture counts")
                { DesiredResponseBudget = desired, SafetyReserveTokens = reserve };
            if (!fits) throw new ModelContextLimitException(metadata);
            var lease = new FixtureLease(metadata, () => Generated++);
            Leases.Add(lease);
            AfterPrepare?.Invoke(lease);
            return lease;
        }
    }

    private sealed class FixtureLease(PreparedModelMetadata metadata, Action generated) : IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata { get; } = metadata;
        public bool Disposed { get; private set; }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(FixtureLease));
            cancellationToken.ThrowIfCancellationRequested();
            generated();
            return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Fixture response", 2, Metadata.InputTokens));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
