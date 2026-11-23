using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private sealed record CompilerDiagnostic(string Path, string Code, int Line, int Column, string Message,
        string ReceiptId, int EditGeneration, string? ObservedSourceSha256);
    private sealed record MutationFocus(string Path, string Sha256, int StartLine, int EditGeneration);
    private sealed record StructuralIntent(string Path, string Sha256, int StartLine, int LineCount);
    private sealed record StructurePreparation(string Path, string Sha256, int FocusLine, string? ReceiptId,
        string Outcome, bool Recovered = false, int? PhysicalLines = null, ProgrammingEditFingerprint? Proposal = null);
    private static CompilerDiagnostic? ReadCompilerDiagnostic(Input input, HarnessState state, ProgrammingCompilerBatch batch, string receipt)
    {
        if (!batch.Complete) return null;
        var row = batch.Diagnostics.FirstOrDefault(item => item.Path is { } candidate &&
            item.Line is not null && item.Column is not null && CanonicalSourcePath(candidate) && Within(candidate, input.Root));
        if (row?.Path is not { } path) return null;
        var observed = state.LastMutation is { } mutation && mutation.EditGeneration == state.EditCount && SamePath(mutation.Path, path)
            ? mutation.Sha256 : ActiveSources(state).LastOrDefault(source => SamePath(source.Path, path))?.Sha256;
        var detail = row.Message;
        if (detail.Length > 160) { detail = detail[..160]; if (char.IsHighSurrogate(detail[^1])) detail = detail[..^1]; }
        return new(path, row.Code, row.Line!.Value, row.Column!.Value, detail, receipt, state.EditCount, observed);
    }

    private static bool StructuralError(CompilerDiagnostic diagnostic) =>
        diagnostic.Code is "CS1513" or "CS1514" or "CS1022" or "CS1026" or "CS1003" or "CS1002";
    private static CompilerDiagnostic? ActiveDiagnostic(HarnessState state, string path, string hash)
    {
        var diagnostic = state.LastFailedBuild?.Diagnostic;
        return diagnostic is not null && diagnostic.EditGeneration == state.EditCount && SamePath(diagnostic.Path, path) &&
            (diagnostic.ObservedSourceSha256 is null || diagnostic.ObservedSourceSha256 == hash) ? diagnostic : null;
    }
    private static SourceInterval[] ActiveIntervals(HarnessState state, string path, string hash) =>
        MergeIntervals(ActiveSources(state).Where(source => SameSource(source, path, hash)).Select(Span));
    private static (int Last, bool TerminalMayBeEmpty) PhysicalExtent(HarnessState state, WorkingSource source)
    {
        var prepared = state.StructurePreparation;
        if (prepared is { Outcome: "succeeded", PhysicalLines: { } count } &&
            SamePath(prepared.Path, source.Path) && prepared.Sha256 == source.Sha256) return (count, false);
        var terminal = ActiveSources(state).Where(item => SameSource(item, source.Path, source.Sha256) &&
            item.TotalReadLines == source.TotalReadLines &&
            (long)item.StartLine + item.VisibleLineCount - 1 == item.TotalReadLines).ToArray();
        var terminalEmpty = terminal.Any(item => item.VisibleLineCount > item.PhysicalLineCount);
        return (source.TotalReadLines - (terminalEmpty ? 1 : 0), terminal.Length == 0);
    }
    private static int PhysicalLast(HarnessState state, WorkingSource source) => PhysicalExtent(state, source).Last;
    private static (int First, int Last, string Basis)? RepairInterval(HarnessState state, WorkingSource source)
    {
        var last = PhysicalLast(state, source);
        if (last < 1) return null;
        if (state.StructuralIntent is { } intent && SamePath(intent.Path, source.Path) && intent.Sha256 == source.Sha256)
            return (Math.Max(1, intent.StartLine - 1), (int)Math.Min(last, (long)intent.StartLine + intent.LineCount), "proposed-structural-range-and-boundary-anchors");
        if (ActiveDiagnostic(state, source.Path, source.Sha256) is { } diagnostic)
        {
            var first = Math.Max(1, Math.Min(last, diagnostic.Line) - 20);
            return (first, (int)Math.Min(last, (long)first + 59), "last-compiler-diagnostic-context");
        }
        return null;
    }
    private static SourceRange? RepairGap(HarnessState state, WorkingSource source)
    {
        var interval = RepairInterval(state, source);
        return interval is { } work ? FirstGap(ActiveIntervals(state, source.Path, source.Sha256), work.First, work.Last) : null;
    }
    private static object? RepairAdvice(HarnessState state)
    {
        var diagnostic = state.LastFailedBuild?.Diagnostic;
        if (diagnostic?.EditGeneration != state.EditCount) diagnostic = null;
        return diagnostic is null && state.StructuralIntent is null && state.StructurePreparation is null ? null : new
        {
            diagnostic, state.LastMutation, state.StructuralIntent, state.StructurePreparation,
            pendingRepairContext = state.PendingRepairContext is not { } pending ? null : new
            {
                pending.ProposalContributionId, pending.ProposalBodySha256, pending.Fingerprint, pending.SourceContributionIds
            },
            qualification = "Exact bounded compiler facts from the failed check; observed SHA is source correlation, not a full build-input hash. Geometry, read history and a preparation receipt are not current source text or semantic safety."
        };
    }

    // A proposal causes read-only preparation, never a deferred mutation replay. The model must
    // reconsider it after the new evidence. Existing selected tools and all normal budgets apply.
    private static WorkflowStep? PrepareStructuralRange(Input input, HarnessState state, JsonElement arguments)
    {
        var path = RequiredText(arguments, "path"); var hash = RequiredText(arguments, "expectedSha256").ToLowerInvariant();
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !input.Tools.Contains(ProgrammingSourceStructure.Name, StringComparer.Ordinal) ||
            !(string.IsNullOrWhiteSpace(arguments.GetProperty("newText").GetString()) ||
              ActiveDiagnostic(state, path, hash) is { } diagnostic && StructuralError(diagnostic))) return null;
        var source = ActiveSources(state).LastOrDefault(item => SameSource(item, path, hash));
        if (source is null)
            return Repair(input, state, "Structural C# repair needs an exact active file.read projection for the proposed preimage. Read the needed source; no mutation was dispatched.", "");
        var proposal = ProgrammingEditProposal.Read(arguments, closedShape: false);
        var fingerprint = proposal.Fingerprint(path, hash);
        var first = proposal.StartLine; var count = proposal.LineCount;
        var next = state with { StructuralIntent = new(path, hash, first, count) };
        var last = PhysicalLast(next, source);
        if ((long)first + count - 1 > last)
            return Repair(input, RetireRepairContext(next), "The proposed structural range exceeds the known physical source lines. Reconsider its boundaries; no mutation was dispatched.", "");
        if (!TryStageRepairContext(next, fingerprint, out next))
            return Repair(input, next, "Structural preparation needs the complete original model-authored proposal in the bounded active context. Submit a smaller complete proposal; no cached or reconstructed edit was dispatched.", "");
        var focus = Math.Clamp(first, 1, Math.Max(1, last - 1));
        var prepared = next.StructurePreparation;
        var matching = prepared is not null && SamePath(prepared.Path, path) && prepared.Sha256 == hash && prepared.FocusLine == focus && fingerprint.Matches(prepared.Proposal);
        if (!matching)
        {
            next = next with { StructurePreparation = new(path, hash, focus, null, "requested", Proposal: fingerprint) };
            return RequestTool(input, next, ProgrammingSourceStructure.Name, Element(new { path, expectedSha256 = hash, focusLine = focus,
                proposedEdit = new { startLine = first, lineCount = count, newText = proposal.NewText } }));
        }
        if (prepared!.Outcome != "succeeded")
            return Finish(next, false, "The selected structural preparation did not succeed for this preimage. Its receipt is " +
                (prepared.ReceiptId ?? "unconfirmed") + ". No mutation or automatic preparation retry was dispatched.");
        if (RepairGap(next, source) is { } missing)
            return RequestTool(input, next, "file.read", Element(new { path, startLine = missing.StartLine, lineCount = missing.LineCount }));
        return null;
    }

    private static HarnessState ObserveRepairEffect(HarnessState before, HarnessState next, WorkflowEffect effect, ToolData? data, bool successful)
    {
        if (before.PendingName == ProgrammingSourceStructure.Name && before.PendingArguments is { } args)
        {
            var path = RequiredText(args, "path"); var hash = RequiredText(args, "expectedSha256").ToLowerInvariant();
            var focus = args.TryGetProperty("focusLine", out var value) ? value.GetInt32() : 1;
            var proposal = args.TryGetProperty("proposedEdit", out var draft)
                ? ProgrammingEditProposal.Read(draft).Fingerprint(path, hash) : null;
            var observation = new StructurePreparation(path, hash, focus, effect.Id, "failed", Proposal: proposal);
            if (successful && data is { Truncated: false } && data.Evidence?.Partial != true && System.Text.Encoding.UTF8.GetByteCount(data.Content) <= 4096)
                try
                {
                    using var document = JsonDocument.Parse(data.Content);
                    var root = document.RootElement;
                    ValidateUniqueProperties(root);
                    if (root.GetProperty("schema").GetString() == "program.source.structure/1" &&
                        SamePath(RequiredText(root, "path"), path) && RequiredText(root, "sha256").Equals(hash, StringComparison.OrdinalIgnoreCase) &&
                        root.GetProperty("focusLine").GetInt32() == focus &&
                        root.GetProperty("physicalLines").GetInt32() is >= 0 and <= 1_000_000 &&
                        focus <= Math.Max(1, root.GetProperty("physicalLines").GetInt32()) &&
                        (proposal is null || (long)proposal.StartLine + proposal.LineCount - 1 <= root.GetProperty("physicalLines").GetInt32()) &&
                        root.GetProperty("recovered").ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        (proposal is null ? !root.TryGetProperty("proposedEdit", out var supplied) || supplied.ValueKind == JsonValueKind.Null :
                            root.TryGetProperty("proposedEdit", out var evidence) && evidence.ValueKind == JsonValueKind.Object &&
                            ProgrammingSourceStructure.ConfirmsProposal(evidence, proposal)))
                        observation = observation with { Outcome = "succeeded", Recovered = root.GetProperty("recovered").GetBoolean(),
                            PhysicalLines = root.GetProperty("physicalLines").GetInt32() };
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException) { }
            next = next with { StructurePreparation = observation };
            if (observation.Outcome != "succeeded") next = RetireRepairContext(next);
        }
        if (successful && before.PendingName == RangeEdit && data is not null)
        {
            using var document = JsonDocument.Parse(data.Content); // Already admitted by ConfirmsRangeEdit.
            var result = document.RootElement;
            next = next with { LastMutation = new(RequiredText(result, "path"), RequiredText(result, "sha256").ToLowerInvariant(),
                result.GetProperty("startLine").GetInt32(), next.EditCount) };
        }
        if (successful && before.PendingName == "file.read" && CurrentWorkingSource(next) is { } source)
        {
            if (next.LastFailedBuild is { Diagnostic: { ObservedSourceSha256: { } observed } compiler } failed &&
                SamePath(compiler.Path, source.Path) && observed != source.Sha256)
                next = next with { LastFailedBuild = failed with { Diagnostic = null } };
            if (next.PendingRepairContext is { } pending && SamePath(pending.Fingerprint.Path, source.Path) &&
                pending.Fingerprint.SourceSha256 != source.Sha256) next = RetireRepairContext(next);
            if (next.StructuralIntent is { } intent && SamePath(intent.Path, source.Path) && intent.Sha256 != source.Sha256)
                next = next with { StructuralIntent = null };
            if (next.StructurePreparation is { } prep && SamePath(prep.Path, source.Path) && prep.Sha256 != source.Sha256)
                next = next with { StructurePreparation = null };
        }
        return next;
    }

    private static string? ClippedReadAlias(HarnessState state, JsonElement arguments)
    {
        var range = FileReadRange(arguments);
        var active = ActiveSources(state);
        var latest = active.LastOrDefault(item => SamePath(item.Path, range.Path));
        if (latest is null) return null;
        var source = active.LastOrDefault(item => item.ProjectionLimited && SameSource(item, range.Path, latest.Sha256) &&
            item.StartLine == range.StartLine && range.LineCount > item.VisibleLineCount);
        if (source is null) return null;
        var plan = SourcePlanForPath(state, range.Path)!; // The same validated active source established the guard.
        return $"This changed broad request would repeat the retained clipped source prefix {source.StartLine}-{source.StartLine + source.PhysicalLineCount - 1}; it does not reveal the omitted lines. " +
            SourceReadAdvice(plan) + $" Retained prefix receipt: {source.ReceiptId}. Missing bodies may be reacquired; no read was dispatched.";
    }

    private sealed class RetainedProjectionException(string message) : ArgumentException(message);
}
