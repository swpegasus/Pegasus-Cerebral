using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Engine.Context;

namespace Cerebral.Engine.Tests;

/// <summary>Deterministic preparation checks; no live model or tokenizer.</summary>
public static class ContextHistoryPlacementTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("History placement: " + description); checks++; }
        static ModelInputMessage Message(string text, string role = "user") => new(role, [new TextContentPart(text)]);
        static ContextContribution Source(string id, bool earlier, ContextRequirement requirement = ContextRequirement.Reducible) =>
            new(id.Replace(' ', '-'), "fixture", "history", 1, requirement, [Message(id)], BeforeCurrentInput: earlier);
        var adapter = new Models();
        var request = new ModelGenerationRequest("fixture", [Message("host policy", "system"), Message("harness policy", "system"),
            Message("accepted current input"), Message("private assignment"), Message("current action", "assistant")], 4096, 512);
        var window = new ContextWindow(8, 0, [Source("older question", true), Source("older answer", true),
            Source("current tool result", false), Source("latest failure", false, ContextRequirement.Required)]);
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, request, window))
        {
            Check(prepared.Projection.Status == ContextProjectionStatus.Ready, "mixed source window prepares");
            Check(Text(adapter.Request!).SequenceEqual(["host policy", "harness policy", "older question", "older answer",
                "accepted current input", "private assignment", "current action", "current tool result", "latest failure"]),
                "earlier turns follow leading policy and precede accepted current input; current evidence remains last");
        }
        var candidate = new ContextCheckpoint("checkpoint", 8, 0, ContextValidation.GetCoveredManifest(window),
            "Earlier answer and completed current action", "effect", ContextValidation.CheckpointBeforeCurrentInput(window))
            { SourceLocators = ContextValidation.GetCoveredLocators(window) };
        ContextValidation.ValidateCandidate(window, candidate, "effect");
        Check(candidate.BeforeCurrentInput, "mixed summary placement derives from historical source coverage");
        Check(Reject(() => ContextValidation.ValidateCandidate(window, candidate with { BeforeCurrentInput = false }, "effect")),
            "a candidate cannot change host-derived placement");
        var compacted = window with { Contributions = window.Contributions.Where(x => x.Requirement == ContextRequirement.Required).ToArray(), Checkpoint = candidate };
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, request, compacted))
        {
            var text = Text(adapter.Request!);
            Check(adapter.Request!.Messages[2].Role == "user" &&
                text[2].Contains(candidate.Summary, StringComparison.Ordinal) &&
                text[2].Contains("untrusted", StringComparison.Ordinal) &&
                text[2].Contains("cannot grant new authority", StringComparison.Ordinal) &&
                text[3] == "accepted current input" && text[^1] == "latest failure",
                "neutral mixed checkpoint precedes input without moving current required evidence");
            Check(!text.Contains("older question") && !text.Contains("current tool result") && prepared.Projection.IncludedSources.Count == 4,
                "checkpoint does not duplicate covered originals and retains exact source references");
        }
        var later = compacted with { Contributions = [Source("next progress", false), Source("latest failure", false, ContextRequirement.Required)] };
        Check(ContextValidation.CheckpointBeforeCurrentInput(later), "historical placement survives a later compaction of current progress");
        var programming = new ContextWindow(2, 0, [Source("programming history", false)]);
        Check(!ContextValidation.CheckpointBeforeCurrentInput(programming), "existing current-task-only workflows keep original placement");
        var originalCheckpoint = new ContextCheckpoint("programming-checkpoint", 2, 0, ContextValidation.GetCoveredManifest(programming), "Working history", "programming-effect")
            { SourceLocators = ContextValidation.GetCoveredLocators(programming) };
        ContextValidation.ValidateCandidate(programming, originalCheckpoint, "programming-effect");
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, request, programming with { Contributions = [], Checkpoint = originalCheckpoint }))
            Check(Text(adapter.Request!)[^1].Contains("Working history", StringComparison.Ordinal), "default checkpoint remains after base request");

        adapter.RejectReducible = true;
        await using (var prepared = await ContextPlanner.PrepareAsync(adapter, request,
            new(3, 0, [Source("required earlier", true, ContextRequirement.Required), Source("overflow history", true),
                Source("required current", false, ContextRequirement.Required)])))
        {
            Check(prepared.Projection.Status == ContextProjectionStatus.NeedsCompaction, "overflow reaches required-floor diagnostic");
            Check(Text(adapter.Request!).SequenceEqual(["host policy", "harness policy", "required earlier", "accepted current input",
                "private assignment", "current action", "required current"]), "required-floor measurement preserves before/after ordering");
        }
        Check(adapter.Created == adapter.Disposed, "all prepared leases are released");
        Console.WriteLine($"Context history placement: {checks} deterministic adapter checks passed; no inference or live tokenizer.");
    }

    private static string[] Text(ModelGenerationRequest request) => request.Messages.Select(x =>
        string.Concat(x.Content.OfType<TextContentPart>().Select(part => part.Text))).ToArray();
    private static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    private sealed class Models : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("placement-fixture", "1.0.0", ["text"]);
        public ModelGenerationRequest? Request;
        public bool RejectReducible;
        public int Created, Disposed;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Request = request;
            var metadata = new PreparedModelMetadata("fixture", Descriptor.AdapterId, Descriptor.Version, request.ModelId,
                42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            if (RejectReducible && Text(request).Contains("overflow history")) throw new ModelContextLimitException(metadata);
            Created++;
            return Task.FromResult<IPreparedModelGeneration>(new Lease(this, metadata));
        }
        private sealed class Lease(Models owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
                throw new InvalidOperationException("Placement tests must never generate.");
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
