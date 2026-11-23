using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private sealed record ObservedSpan(string Path, string Sha256, int StartLine, int PhysicalLineCount);
    private sealed record SourceInterval(int StartLine, int EndLine);
    private sealed record SourceReadCoverage(string Path, string Sha256, int StartLine, int PhysicalVisibleThrough,
        int AlreadyPresentPhysicalLines, int NewlyPresentPhysicalLines, int ReintroducedPhysicalLines,
        int? NewToRetainedHistoryPhysicalLines, string Classification);
    private sealed record SourceRange(int StartLine, int LineCount);

    // One validator owns the mapping used by selection, coverage and guidance. Metadata and historical
    // observations can describe an original, but only its exact active body can establish visibility.
    private static WorkingSource? MappedSource(HarnessState state, ReadProjectionReference reference, bool required = false)
    {
        var source = reference.Source ?? (state.WorkingSource?.ContributionId == reference.ContributionId ? state.WorkingSource : null);
        if (source is null || !CanonicalSourcePath(source.Path) ||
            !SourcePath(source.Path) || !IsRangeHash(source.Sha256 ?? "") || !IsRangeHash(source.EvidenceKey ?? "") ||
            source.StartLine < 1 || source.VisibleLineCount is < 1 or > 1000 ||
            source.PhysicalLineCount < 0 || source.PhysicalLineCount > source.VisibleLineCount ||
            source.PhysicalLineCount < source.VisibleLineCount - 1 ||
            (long)source.StartLine + source.VisibleLineCount - 1 > source.TotalReadLines ||
            source.RequestedStartLine != source.StartLine || source.RequestedLineCount is < 1 or > 1000 ||
            source.VisibleLineCount > source.RequestedLineCount ||
            source.ContributionId != reference.ContributionId || source.RequestSignature != reference.Signature ||
            source.EvidenceKey != reference.EvidenceKey ||
            reference.MappingSha256 != TextHash(JsonSerializer.Serialize(source, Json)) || !Visible(state, reference))
            return null;
        var matches = state.Contributions.Where(item => item.Id == source.ContributionId).ToArray();
        var contribution = matches.Length == 1 ? matches[0] : null;
        return contribution is { Category: "tool-result" } && contribution.ExactReadHandle == source.ReceiptId &&
            (required ? contribution.Requirement == ContextRequirement.Required :
                contribution.Requirement is ContextRequirement.Required or ContextRequirement.Reducible)
            ? source : null;
    }

    private static WorkingSource[] ActiveSources(HarnessState state) => state.ReadProjectionReferences
        .TakeLast(MaximumReadMappings).Select(reference => MappedSource(state, reference))
        .OfType<WorkingSource>().ToArray();

    private static bool SameSource(WorkingSource source, string path, string hash) =>
        SamePath(source.Path, path) && source.Sha256 == hash;

    private static SourceInterval[] MergeIntervals(IEnumerable<ObservedSpan> spans)
    {
        var merged = new List<SourceInterval>();
        foreach (var span in spans.Where(ValidSpan).OrderBy(item => item.StartLine))
        {
            var end = checked(span.StartLine + span.PhysicalLineCount - 1);
            if (merged.Count > 0 && (long)span.StartLine <= (long)merged[^1].EndLine + 1)
                merged[^1] = merged[^1] with { EndLine = Math.Max(merged[^1].EndLine, end) };
            else merged.Add(new(span.StartLine, end));
        }
        return merged.ToArray();
    }

    private static bool CanonicalSourcePath(string path)
    {
        try { return !string.IsNullOrEmpty(path) && !path.Any(char.IsControl) &&
            Path.IsPathFullyQualified(path) && SamePath(Path.GetFullPath(path), path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool ValidSpan(ObservedSpan span) => CanonicalSourcePath(span.Path) && IsRangeHash(span.Sha256 ?? "") && span.StartLine > 0 &&
        span.PhysicalLineCount is > 0 and <= 1000 && (long)span.StartLine + span.PhysicalLineCount - 1 <= int.MaxValue;
    private static ObservedSpan Span(WorkingSource source) => new(source.Path, source.Sha256, source.StartLine, source.PhysicalLineCount);
    private static bool ContainsLine(IEnumerable<SourceInterval> spans, int line) => spans.Any(span => span.StartLine <= line && span.EndLine >= line);

    private static SourceReadCoverage MeasureReadCoverage(HarnessState before, WorkingSource source)
    {
        var active = MergeIntervals(ActiveSources(before).Where(item => SameSource(item, source.Path, source.Sha256)).Select(Span));
        var historical = MergeIntervals(before.ReadHistory.Select(item => item.Span).OfType<ObservedSpan>()
            .Where(item => SamePath(item.Path, source.Path) && item.Sha256 == source.Sha256));
        var historyKnown = !before.ReadHistoryTruncated && before.ReadHistory.All(item => item.Span is { } span && ValidSpan(span));
        var already = 0; var reintroduced = 0; var historicalLines = 0;
        for (var offset = 0; offset < source.PhysicalLineCount; offset++)
        {
            var line = checked(source.StartLine + offset);
            var present = ContainsLine(active, line); var observed = ContainsLine(historical, line);
            if (present) already++;
            if (observed) historicalLines++;
            if (!present && observed) reintroduced++;
        }
        var added = source.PhysicalLineCount - already;
        var classification = added == 0 ? "already-visible-overlap" :
            reintroduced == added ? "reacquired" :
            historyKnown ? "extends-current-coverage" : "extends-current-coverage-history-unknown";
        return new(source.Path, source.Sha256, source.StartLine, source.StartLine + source.PhysicalLineCount - 1,
            already, added, reintroduced, historyKnown ? source.PhysicalLineCount - historicalLines : null, classification);
    }

    private static SourceRange? FirstGap(SourceInterval[] intervals, int first, int last)
    {
        if (first < 1 || last < first) return null;
        long next = first;
        foreach (var span in intervals)
        {
            if (span.EndLine < next) continue;
            if (span.StartLine > next) return new((int)next, (int)Math.Min(60, Math.Min(last, span.StartLine - 1) - next + 1));
            next = Math.Max(next, (long)span.EndLine + 1);
            if (next > last) return null;
        }
        return next <= last ? new((int)next, (int)Math.Min(60, last - next + 1)) : null;
    }

    private sealed record RepairWork(int First, int Last, string Basis, bool SourceComplete);
    private sealed record SourceCoveragePlan(WorkingSource Selected, WorkingSource[] Sources,
        SourceInterval[] Intervals, SourceRange? NextUncoveredRange, SourceRange? EarlierUncoveredRange,
        RepairWork? RepairWork, bool AllPhysicalSourceLinesCovered, bool TerminalCoordinateMayBeEmpty);

    // Progress and rejected-read advice use the same exact-body/preimage plan. This is derived
    // metadata only; it neither caches source text nor establishes semantic or check progress.
    private static SourceCoveragePlan PlanSourceCoverage(HarnessState state, WorkingSource selected)
    {
        var sources = ActiveSources(state).Where(item => SameSource(item, selected.Path, selected.Sha256)).ToArray();
        var intervals = MergeIntervals(sources.Select(Span));
        var extent = PhysicalExtent(state, selected);
        var repair = RepairInterval(state, selected);
        var repairGap = repair is { } work ? FirstGap(intervals, work.First, work.Last) : null;
        var through = selected.StartLine + selected.PhysicalLineCount - 1;
        var after = through < extent.Last ? FirstGap(intervals, through + 1, extent.Last) : null;
        var earlier = FirstGap(intervals, 1, selected.StartLine - 1);
        return new(selected, sources, intervals, repair is not null ? repairGap : after ?? earlier, earlier,
            repair is { } range ? new(range.First, range.Last, range.Basis, repairGap is null) : null,
            FirstGap(intervals, 1, extent.Last) is null, extent.TerminalMayBeEmpty);
    }

    private static SourceCoveragePlan? SourcePlanForPath(HarnessState state, string path)
    {
        var latest = ActiveSources(state).LastOrDefault(source => SamePath(source.Path, path));
        if (latest is null) return null;
        var selected = CurrentWorkingSource(state);
        return PlanSourceCoverage(state, selected is not null && SameSource(selected, path, latest.Sha256) ? selected : latest);
    }

    private static string SourceReadAdvice(SourceCoveragePlan plan)
    {
        if (plan.NextUncoveredRange is { } missing)
            return $"Current exact source coverage needs file.read startLine={missing.StartLine}, lineCount={missing.LineCount}. " +
                (plan.TerminalCoordinateMayBeEmpty && missing.StartLine == plan.Selected.TotalReadLines && missing.LineCount == 1
                    ? "This last producer coordinate may be a terminal empty segment; it is not an editable physical line without source evidence. "
                    : "") +
                "Use this missing range rather than a broad head request. Existing covered source adds no edit or check credit.";
        var coverage = plan.AllPhysicalSourceLinesCovered
            ? "All physical source lines for this current preimage are present in the active exact bodies; there is no missing source range. "
            : "The current repair work unit and its boundary anchors are present in active exact bodies; no unrelated source sweep is required. ";
        return coverage + "Use the retained source for a bounded correction when justified, or use the configured compiler check for diagnostic evidence. Source coverage is not edit or build progress.";
    }

    private static object? SourceGuidance(HarnessState state)
    {
        if (CurrentWorkingSource(state) is not { } selected) return null;
        var plan = PlanSourceCoverage(state, selected);
        return new
        {
            selected.Path, selected.Sha256, selected.ReceiptId, selected.StartLine,
            physicalVisibleThrough = selected.StartLine + selected.PhysicalLineCount - 1,
            selected.RequestedStartLine, selected.RequestedLineCount,
            activePhysicalIntervals = plan.Intervals.Take(8).ToArray(), omittedIntervalCount = Math.Max(0, plan.Intervals.Length - 8),
            plan.NextUncoveredRange, plan.EarlierUncoveredRange, plan.RepairWork,
            plan.AllPhysicalSourceLinesCovered, plan.TerminalCoordinateMayBeEmpty,
            inspectionAdvice = SourceReadAdvice(plan),
            selectableSources = plan.Sources.TakeLast(4).Select(item => new
            {
                item.ReceiptId, item.StartLine, physicalVisibleThrough = item.StartLine + item.PhysicalLineCount - 1,
                selected = item.ContributionId == selected.ContributionId,
                complete = state.Contributions.Single(source => source.Id == item.ContributionId).Complete
            }).ToArray(),
            qualification = "Exact bodies in the active manifest only; a non-Ready preparation delivered no source to inference. Gaps are optional navigation, not a survey requirement or edit span."
        };
    }

    private static WorkflowStep FocusSource(Input input, HarnessState state, JsonElement action)
    {
        state = RestoreRepairRequirements(state);
        var receipt = RequiredText(action, "receiptId");
        var selected = CurrentWorkingSource(state);
        var candidate = ActiveSources(state).SingleOrDefault(source => source.ReceiptId == receipt);
        if (selected is null || candidate is null || candidate.PhysicalLineCount < 1 ||
            !SameSource(candidate, selected.Path, selected.Sha256) || !Within(candidate.Path, input.Root))
            return Repair(input, state, "focus-source requires an original source body still present for the current path and preimage. Missing bodies require an explicit source read; no receipt was fetched.", "");
        if (candidate.ContributionId == selected.ContributionId)
            return Repair(input, state, "That original source window is already selected. Selection alone adds no source, edit or check progress.", "");
        var sources = ActiveSources(state).Select(source => source.ContributionId).ToHashSet(StringComparer.Ordinal);
        var next = state with
        {
            WorkingSource = candidate,
            ContextRevision = checked(state.ContextRevision + 1),
            Contributions = state.Contributions.Where(source => source.Category != "model-output" ||
                source.Id == OriginalRepairProposal(state)?.Id).Select(source =>
                sources.Contains(source.Id) ? source with
                { Requirement = source.Id == candidate.ContributionId ? ContextRequirement.Required : ContextRequirement.Reducible } : source).ToArray()
        };
        // Selection replaces one original body; no companion window, text copy, receipt or progress
        // credit is added. The normal host preparation remains the exact token-floor authority.
        return Fits(next) ? RequestModel(input, next) : ContextFull(state);
    }
}
