using Cerebral.Contracts.Models;
using System.Text.Json;

namespace Cerebral.Contracts.Context;

public enum ContextRequirement { Required, Optional, Reducible }

/// <summary>A host-attributed projection of durable source content. Required content is never dropped or
/// summarized. Complete=false identifies an excerpt; ExactReadHandle locates the original when available.</summary>
public sealed record ContextContribution(
    string Id, string Owner, string Category, long SourceRevision, ContextRequirement Requirement,
    IReadOnlyList<ModelInputMessage> Messages, string? ExactReadHandle = null, bool Complete = true, int Priority = 0,
    bool BeforeCurrentInput = false);

public sealed record ContextSourceReference(string Id, long Revision);

/// <summary>Host-authored source metadata, separate from identity equality and generated summaries.
/// Complete describes the original projection, not semantic fidelity of its summary. Handles are opaque
/// reference data; they neither grant access nor imply a successful operation.</summary>
public sealed record ContextSourceLocator(string Id, long Revision, string Owner, string Category,
    string? ExactReadHandle, bool Complete);

/// <summary>Source mappings and effect identity are supplied by the host, never inferred from generated
/// summary text. Covered sources are retained durably outside the active projection.</summary>
public sealed record ContextCheckpoint(
    string Id, long ContextRevision, long ExclusionRevision, IReadOnlyList<ContextSourceReference> CoveredSources,
    string Summary, string CompactionEffectId, bool BeforeCurrentInput = false)
{
    /// <summary>Empty for legacy checkpoints whose attribution was not saved. New candidates require
    /// one exact host-authored locator per covered source, in the same order.</summary>
    public IReadOnlyList<ContextSourceLocator> SourceLocators { get; init; } = [];
}

/// <summary>Frozen source manifest for one model step. Accepted checkpoint sources must not also appear
/// as contributions. Excluding a source invalidates checkpoints from an older exclusion revision.</summary>
public sealed record ContextWindow(
    long Revision, long ExclusionRevision, IReadOnlyList<ContextContribution> Contributions,
    ContextCheckpoint? Checkpoint = null);

public enum ContextProjectionStatus { Ready, NeedsCompaction, RequiredOverflow }

public sealed record ContextOmission(string Id, string Reason);

/// <summary>Measured assembly outcome. Omissions concern this projection only and do not remove durable
/// originals. Metadata is the selected full projection count for Ready/NeedsCompaction, and the exact
/// required-floor diagnostic for RequiredOverflow.</summary>
public sealed record ContextProjection(
    ContextProjectionStatus Status, long ContextRevision, long ExclusionRevision,
    IReadOnlyList<ContextSourceReference> IncludedSources, IReadOnlyList<ContextOmission> Omissions,
    PreparedModelMetadata? Metadata, string? Reason = null);

/// <summary>Owns the exact prepared lease used for generation. A non-ready projection has no lease.</summary>
public sealed record ContextPreparation(IPreparedModelGeneration? Prepared, ContextProjection Projection) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Prepared?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Pure validation shared by host and Contracts-only harnesses. It never adopts a checkpoint.</summary>
public static class ContextValidation
{
    public const int MaximumSourceCount = 128;
    public const int MaximumSummaryCharacters = 32_768;
    public const int MaximumCheckpointBytes = 98_304;

    public static void ValidateWindow(ContextWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Revision < 0 || window.ExclusionRevision < 0)
            throw new ArgumentException("Context and exclusion revisions cannot be negative.", nameof(window));
        if (window.Contributions is null || window.Contributions.Count > MaximumSourceCount)
            throw new ArgumentException("A context window requires a bounded source list.", nameof(window));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<ModelInputMessage>();
        if (window.Checkpoint is { } checkpoint)
        {
            ValidateCheckpoint(checkpoint);
            if (checkpoint.ContextRevision > window.Revision || checkpoint.ExclusionRevision != window.ExclusionRevision)
                throw new ArgumentException("The checkpoint is from a future context or a stale exclusion revision.", nameof(window));
            foreach (var source in checkpoint.CoveredSources) ids.Add(source.Id);
        }
        foreach (var source in window.Contributions)
        {
            if (source is null) throw new ArgumentException("Context sources cannot be null.", nameof(window));
            ValidateReference(new(source.Id, source.SourceRevision));
            ContractValidation.Identifier(source.Owner, nameof(source.Owner), 128);
            ContractValidation.Identifier(source.Category, nameof(source.Category), 64);
            ContractValidation.OptionalText(source.ExactReadHandle, nameof(source.ExactReadHandle), 2048);
            if (!Enum.IsDefined(source.Requirement)) throw new ArgumentException("Unknown context requirement.", nameof(window));
            if (!ids.Add(source.Id)) throw new ArgumentException("Context source IDs must be unique and cannot overlap checkpoint coverage.", nameof(window));
            if (source.Messages is null || source.Messages.Count is < 1 or > 1000)
                throw new ArgumentException("Context sources require bounded messages.", nameof(window));
            foreach (var message in source.Messages)
            {
                if (message is null || message.Role == "system")
                    throw new ArgumentException("Context contributions cannot introduce system messages.", nameof(window));
                messages.Add(message);
            }
        }
        if (ids.Count > MaximumSourceCount)
            throw new ArgumentException("The combined context and checkpoint source manifest exceeds 128 entries.", nameof(window));
        if (messages.Count > 0)
            new ModelGenerationRequest("context-validation", messages, 512, 1).Validate();
    }

    /// <summary>Exact host-authored manifest for the next compaction, in stable source order. A prior
    /// checkpoint is one durable source whose own retained receipt preserves its earlier coverage. Rolling
    /// it up by checkpoint identity prevents long tool runs from carrying every historical coordinate in
    /// every later generation. Required anchors and optional retrievals are excluded from summary authority.</summary>
    public static IReadOnlyList<ContextSourceReference> GetCoveredManifest(ContextWindow window)
    {
        ValidateWindow(window);
        var previous = window.Checkpoint is { } checkpoint
            ? new[] { new ContextSourceReference(checkpoint.Id, checkpoint.ContextRevision) }
            : [];
        return Array.AsReadOnly(previous.Concat(window.Contributions
            .Where(source => source.Requirement == ContextRequirement.Reducible)
            .Select(source => new ContextSourceReference(source.Id, source.SourceRevision))).ToArray());
    }

    /// <summary>Bounded companion mapping for the next checkpoint. A prior checkpoint is one source;
    /// its full document retains the previous mappings. Legacy attribution stays unknown, and no exact
    /// handle is invented for a legacy checkpoint that may not have a retained adoption event.</summary>
    public static IReadOnlyList<ContextSourceLocator> GetCoveredLocators(ContextWindow window)
    {
        ValidateWindow(window);
        var previous = window.Checkpoint is { } checkpoint
            ? new[] { new ContextSourceLocator(checkpoint.Id, checkpoint.ContextRevision, "cerebral.engine",
                "context.checkpoint", checkpoint.SourceLocators.Count > 0
                    ? (checkpoint.BeforeCurrentInput ? checkpoint.Id : checkpoint.CompactionEffectId) : null, true) }
            : [];
        return Array.AsReadOnly(previous.Concat(window.Contributions
            .Where(source => source.Requirement == ContextRequirement.Reducible)
            .Select(source => new ContextSourceLocator(source.Id, source.SourceRevision, source.Owner,
                source.Category, source.ExactReadHandle, source.Complete))).ToArray());
    }

    public static void ValidateCandidate(ContextWindow window, ContextCheckpoint candidate, string expectedEffectId)
    {
        var expected = GetCoveredManifest(window);
        ValidateCheckpoint(candidate);
        ContractValidation.Identifier(expectedEffectId, nameof(expectedEffectId), 256);
        if (candidate.ContextRevision != window.Revision || candidate.ExclusionRevision != window.ExclusionRevision ||
            !string.Equals(candidate.CompactionEffectId, expectedEffectId, StringComparison.Ordinal))
            throw new ArgumentException("Checkpoint context, exclusion revision, and compaction effect must exactly match the accepted source window.", nameof(candidate));
        if (!candidate.CoveredSources.SequenceEqual(expected))
            throw new ArgumentException("Checkpoint coverage must exactly match the host-authored reducible source manifest.", nameof(candidate));
        if (!candidate.SourceLocators.SequenceEqual(GetCoveredLocators(window)))
            throw new ArgumentException("Checkpoint locators must exactly match the host-authored reducible source metadata.", nameof(candidate));
        if (candidate.BeforeCurrentInput != CheckpointBeforeCurrentInput(window))
            throw new ArgumentException("Checkpoint placement must match its host-authored source manifest.", nameof(candidate));
    }

    /// <summary>A mixed summary precedes current input when it includes earlier turns. This locates a
    /// neutral reference summary, not a claim that original transcript chronology survived compaction.</summary>
    public static bool CheckpointBeforeCurrentInput(ContextWindow window) =>
        window.Checkpoint?.BeforeCurrentInput == true || window.Contributions.Any(source =>
            source.Requirement == ContextRequirement.Reducible && source.BeforeCurrentInput);

    private static void ValidateCheckpoint(ContextCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ContractValidation.Identifier(checkpoint.Id, nameof(checkpoint.Id), 256);
        ContractValidation.Identifier(checkpoint.CompactionEffectId, nameof(checkpoint.CompactionEffectId), 256);
        if (checkpoint.ContextRevision < 0 || checkpoint.ExclusionRevision < 0)
            throw new ArgumentException("Checkpoint revisions cannot be negative.", nameof(checkpoint));
        ContractValidation.Text(checkpoint.Summary, nameof(checkpoint.Summary), MaximumSummaryCharacters);
        if (checkpoint.CoveredSources is null || checkpoint.CoveredSources.Count is < 1 or > MaximumSourceCount)
            throw new ArgumentException("Checkpoint coverage requires between 1 and 128 source references.", nameof(checkpoint));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in checkpoint.CoveredSources)
        {
            ValidateReference(source);
            if (!ids.Add(source.Id)) throw new ArgumentException("Checkpoint source IDs cannot repeat, even at different revisions.", nameof(checkpoint));
        }
        if (checkpoint.SourceLocators is null ||
            checkpoint.SourceLocators.Count != 0 && checkpoint.SourceLocators.Count != checkpoint.CoveredSources.Count)
            throw new ArgumentException("Checkpoint locators must be absent for legacy data or map every covered source.", nameof(checkpoint));
        for (var index = 0; index < checkpoint.SourceLocators.Count; index++)
        {
            var locator = checkpoint.SourceLocators[index];
            if (locator is null || locator.Id != checkpoint.CoveredSources[index].Id ||
                locator.Revision != checkpoint.CoveredSources[index].Revision)
                throw new ArgumentException("Checkpoint locator order and identity must match coverage.", nameof(checkpoint));
            ContractValidation.Identifier(locator.Owner, nameof(locator.Owner), 128);
            ContractValidation.Identifier(locator.Category, nameof(locator.Category), 64);
            ContractValidation.OptionalText(locator.ExactReadHandle, nameof(locator.ExactReadHandle), 2048);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(checkpoint).Length > MaximumCheckpointBytes)
            throw new ArgumentException("Checkpoint metadata and summary exceed the 96 KiB serialized limit.", nameof(checkpoint));
    }

    private static void ValidateReference(ContextSourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ContractValidation.Identifier(source.Id, nameof(source.Id), 256);
        if (source.Revision < 0) throw new ArgumentException("Source revisions cannot be negative.", nameof(source));
    }
}
