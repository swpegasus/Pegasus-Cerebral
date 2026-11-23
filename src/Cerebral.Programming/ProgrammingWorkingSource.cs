using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private const int MaximumReadMappings = 64;
    private const int MaximumRetainedReadSelections = 2;

    // Metadata only. Exact text lives once, in the active contribution; a hash/receipt cannot restore it.
    private sealed record ReadProjectionReference(string Signature, string ContributionId, string BodySha256, string? EvidenceKey, string? MappingSha256, WorkingSource? Source = null);
    private sealed record ReadObservation(string Signature, string EvidenceKey, ObservedSpan? Span = null);
    private sealed record WorkingSource(string ContributionId, string ReceiptId, string Path, string Sha256,
        int StartLine, int VisibleLineCount, int PhysicalLineCount, int TotalReadLines,
        int RequestedStartLine, int RequestedLineCount, string RequestSignature, string EvidenceKey,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ProjectionLimited = false);

    private static string TextHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool SourcePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets";
    private static int Increment(int value) => value < 0 ? 1 : value == int.MaxValue ? value : value + 1;

    private static bool Visible(HarnessState state, ReadProjectionReference reference) =>
        IsRangeHash(reference.BodySha256 ?? "") && !string.IsNullOrEmpty(reference.Signature) &&
        state.Contributions.Any(source => source.Id == reference.ContributionId &&
            source.Owner == "pegasus.programming" && source.ExactReadHandle is not null &&
            source.Messages.Count == 1 && source.Messages[0].Role == "user" &&
            source.Messages[0].Content is [TextContentPart text] && TextHash(text.Text) == reference.BodySha256);

    private static WorkingSource? CurrentWorkingSource(HarnessState state)
    {
        if (state.WorkingSource is not { } working) return null;
        var reference = state.ReadProjectionReferences.FirstOrDefault(item => item.ContributionId == working.ContributionId &&
            item.Signature == working.RequestSignature && item.EvidenceKey == working.EvidenceKey);
        var mapped = reference is null ? null : MappedSource(state, reference, required: true);
        return mapped is not null && mapped == working ? mapped : null;
    }

    private static bool RepeatsVisibleRead(HarnessState state, JsonElement arguments)
    {
        var signature = FileReadSignature(arguments);
        if (state.ReadProjectionReferences.Any(reference => reference.Signature == signature && Visible(state, reference))) return true;
        // A broad request and explicit smaller request can project exactly the same source. History
        // identifies that alias only while a matching exact body remains visible. Lost bodies may reread.
        var visibleKeys = state.ReadProjectionReferences.Where(reference => Visible(state, reference) &&
            reference.EvidenceKey is not null).Select(reference => reference.EvidenceKey!).ToHashSet(StringComparer.Ordinal);
        return state.ReadHistory.Any(item => item.Signature == signature && visibleKeys.Contains(item.EvidenceKey)) ||
            CoveringWholeSource(state, arguments) is not null;
    }

    private static WorkingSource[] RetainedStagedSources(HarnessState state, string? mutationPath = null)
    {
        if (state.Stages is not { BaselineReady: true, Step: "repair" } stage ||
            mutationPath is not null && !stage.CurrentInputs.Any(file => SamePath(file.Path, mutationPath))) return [];
        return ActiveSources(state).Where(source => (mutationPath is null || !SamePath(source.Path, mutationPath)) &&
            stage.CurrentInputs.Any(file => SameSource(source, file.Path, file.Sha256))).ToArray();
    }

    private static WorkingSource? CoveringWholeSource(HarnessState state, JsonElement arguments)
    {
        // A cache selection must not normalize an invalid or extended file.read request.
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => property.Name is not ("path" or "startLine" or "lineCount")) ||
            arguments.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != arguments.EnumerateObject().Count()) return null;
        foreach (var name in new[] { "startLine", "lineCount" })
            if (arguments.TryGetProperty(name, out var field) && (field.ValueKind != JsonValueKind.Number ||
                !field.TryGetInt32(out var number) || number < 1 || name == "lineCount" && number > 1000)) return null;
        var range = FileReadRange(arguments);
        var latest = ActiveSources(state).LastOrDefault(source => SamePath(source.Path, range.Path));
        if (latest is null || range.StartLine != 1 || range.LineCount < latest.TotalReadLines ||
            state.Stages is { } stage && !stage.CurrentInputs.Any(file => SameSource(latest, file.Path, file.Sha256))) return null;
        return ActiveSources(state).LastOrDefault(source => SameSource(source, range.Path, latest.Sha256) &&
            source.StartLine == 1 && source.VisibleLineCount == source.TotalReadLines && !source.ProjectionLimited &&
            source.PhysicalLineCount > 0 && range.StartLine <= source.PhysicalLineCount &&
            state.Contributions.Single(item => item.Id == source.ContributionId).Complete);
    }

    private static WorkflowStep ReuseStagedSource(Input input, HarnessState state, WorkingSource source)
    {
        if (state.RetainedReadSelections >= MaximumRetainedReadSelections)
            return CanRequestStagedDiagnosticVerification(input, state) ? BeginStagedVerification(input, state) :
                Finish(state, false, "Repeated reads selected already retained source without source progress. No new read was dispatched; use the original receipt and current source, or stop.");
        var ids = ActiveSources(state).Select(item => item.ContributionId).ToHashSet(StringComparer.Ordinal);
        var next = state with
        {
            WorkingSource = source, RetainedReadSelections = state.RetainedReadSelections + 1,
            RepeatedReadAttempts = Increment(state.RepeatedReadAttempts), NoNewEvidenceReads = Increment(state.NoNewEvidenceReads),
            Contributions = state.Contributions.Where(item => item.Category != "model-output" || item.Id == OriginalRepairProposal(state)?.Id)
                .Select(item => ids.Contains(item.Id) ? item with
                { Requirement = item.Id == source.ContributionId ? ContextRequirement.Required : ContextRequirement.Reducible } : item).ToArray()
        };
        next = AppendProjection(next, "retained-read." + state.ContextRevision, "repair-diagnostic", "user",
            "The requested range is already visible in the original read receipt " + source.ReceiptId +
            " for " + source.Path + " at SHA-256 " + source.Sha256 +
            ". That original body is selected. No file.read ran, no source was refreshed, and no read, mutation or verification credit was added. Use the retained source for a correction, or return done for fresh policy-owned verification.",
            ContextRequirement.Required, null);
        return Fits(next) ? RequestModel(input, next) : ContextFull(state);
    }

    private static HarnessState InvalidateEditingEvidence(HarnessState state, string? mutationPath = null)
    {
        // Only a staged, admitted exact-file mutation can preserve unrelated observed preimages.
        // Target, structural and compiler mappings retire before an outcome can be uncertain.
        var retained = mutationPath is null ? new HashSet<string>(StringComparer.Ordinal) :
            RetainedStagedSources(state, mutationPath).Select(source => source.ContributionId).ToHashSet(StringComparer.Ordinal);
        state = RetireRepairContext(state);
        var working = CurrentWorkingSource(state);
        return state with
        {
            WorkingSource = working is not null && retained.Contains(working.ContributionId) ? working : null,
            StructuralIntent = null, StructurePreparation = null, LastMutation = null,
            LastFailedBuild = state.LastFailedBuild is { } failed ? failed with { Diagnostic = null } : null,
            ReadProjectionReferences = state.ReadProjectionReferences.Where(reference => retained.Contains(reference.ContributionId) ||
                mutationPath is null && reference.EvidenceKey is null && reference.ContributionId != state.WorkingSource?.ContributionId && Visible(state, reference)).ToArray(),
            Contributions = state.Contributions.Where(source => retained.Contains(source.Id) || source.Id != state.WorkingSource?.ContributionId).ToArray()
            // Logical read history is never current visibility or fresh verification evidence.
        };
    }

    private static HarnessState AfterCompaction(HarnessState state)
    {
        var references = state.ReadProjectionReferences.Where(reference => Visible(state, reference))
            .TakeLast(MaximumReadMappings).ToArray();
        var next = state with { ReadProjectionReferences = references,
            ReadSignatures = references.Select(reference => reference.Signature).Distinct(StringComparer.Ordinal).ToArray() };
        return next with { WorkingSource = CurrentWorkingSource(next) };
    }

    private static HarnessState RememberRead(Input input, HarnessState state, WorkflowEffect effect,
        ToolData data, JsonElement arguments, string? projectedContent)
    {
        var contribution = state.Contributions.Last();
        var signature = FileReadSignature(arguments);
        var working = TryWorkingSource(input, data, arguments, projectedContent, contribution.Id, effect.Id);
        var previous = CurrentWorkingSource(state);
        var reference = new ReadProjectionReference(signature, contribution.Id,
            TextHash(((TextContentPart)contribution.Messages.Single().Content.Single()).Text), working?.EvidenceKey,
            working is null ? null : TextHash(JsonSerializer.Serialize(working, Json)), working);
        // A changed preimage retires every older validated mapping for this path, even after
        // selecting another file. Other files and unknown legacy mappings retain their own rules.
        var references = state.ReadProjectionReferences.Where(item => item.Signature != signature && Visible(state, item) &&
                (working is null || MappedSource(state, item) is not { } prior ||
                    !SamePath(prior.Path, working.Path) || prior.Sha256 == working.Sha256))
            .Append(reference).TakeLast(MaximumReadMappings).ToArray();
        var lostProtectedMapping = state.PendingRepairContext is { } pending &&
            (pending.SourceContributionIds ?? []).Any(id => !references.Any(reference => reference.ContributionId == id));
        var next = state with { ReadProjectionReferences = references };
        if (lostProtectedMapping) next = RetireRepairContext(next) with { StructuralIntent = null, StructurePreparation = null };
        if (SourcePath(RequiredText(arguments, "path")))
            next = next with { WorkingSource = working };
        if (input.GuardFinalization && state.PendingRepairContext is null && working is not null &&
            previous is not null && previous.ContributionId != working.ContributionId)
            next = next with { Contributions = next.Contributions.Select(source => source.Id == previous.ContributionId &&
                source.Requirement == ContextRequirement.Required ? source with { Requirement = ContextRequirement.Reducible } : source).ToArray(),
                ContextRevision = checked(next.ContextRevision + 1) };
        if (working is null) return LostMappingAdvice(next);
        var coverage = MeasureReadCoverage(state, working);
        var retained = state.ReadHistory.Where(item => item.Signature != signature || item.EvidenceKey != working.EvidenceKey).ToArray();
        var repeated = state.ReadHistory.Any(item => item.EvidenceKey == working.EvidenceKey);
        next = next with
        {
            ReadHistory = retained.Append(new ReadObservation(signature, working.EvidenceKey, Span(working))).TakeLast(MaximumReadMappings).ToArray(),
            ReadHistoryTruncated = state.ReadHistoryTruncated || retained.Length >= MaximumReadMappings,
            LastReadCoverage = coverage,
            NoCoverageGainReads = coverage.NewlyPresentPhysicalLines == 0 ? Increment(state.NoCoverageGainReads) : 0,
            ReadObservations = Increment(state.ReadObservations),
            RepeatedReadObservations = repeated ? Increment(state.RepeatedReadObservations) : state.RepeatedReadObservations,
            NoNewEvidenceReads = repeated ? Increment(state.NoNewEvidenceReads) : 0,
            LastReadObservation = repeated ? "reacquired-previously-observed-source" : "new-to-bounded-read-history"
        };
        return LostMappingAdvice(next);

        HarnessState LostMappingAdvice(HarnessState value) => !lostProtectedMapping ? value :
            AppendProjection(value, "repair-mapping-limit." + effect.Id, "repair-diagnostic", "user",
                "The pending repair was retired at the bounded source-mapping limit. A former body no longer has its exact active mapping. Read a smaller current unit and submit a fresh proposal; no cached edit or historical coverage was used.",
                ContextRequirement.Required, effect.Id);
    }

    private static WorkingSource? TryWorkingSource(Input input, ToolData data, JsonElement arguments,
        string? projected, string contributionId, string receiptId)
    {
        if (!SourcePath(RequiredText(arguments, "path")) || projected is null || !ProjectionComplete(projected)) return null;
        // ReceiveTool already confirms the host outcome. Native file.read legitimately has null
        // Evidence (ToolResult.EffectiveOutcome falls back to Success); optional metadata, when
        // present, must still report a valid successful provider.
        if (data.Evidence is { } evidence && (evidence.Outcome != ToolExecutionOutcome.Succeeded ||
            string.IsNullOrWhiteSpace(evidence.ProviderId))) return null;
        if (data.Content.Length > 128 * 1024 || Encoding.UTF8.GetByteCount(data.Content) > 128 * 1024) return null;
        projected = projected.Replace("\r\n", "\n", StringComparison.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 32 });
            var raw = document.RootElement;
            if (raw.ValueKind != JsonValueKind.Object ||
                !raw.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !raw.TryGetProperty("sha256", out var hashValue) || hashValue.ValueKind != JsonValueKind.String ||
                !raw.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String ||
                !raw.TryGetProperty("startLine", out var startValue) || !startValue.TryGetInt32(out var start) ||
                !raw.TryGetProperty("totalLines", out var totalValue) || !totalValue.TryGetInt32(out var total))
                return null;
            var path = pathValue.GetString()!; var hash = hashValue.GetString()!;
            var request = FileReadRange(arguments);
            if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) || !SamePath(path, request.Path) ||
                !Within(Path.GetFullPath(path), input.Root) || !IsRangeHash(hash) || start != request.StartLine || total < start)
                return null;

            // Recognize the current public readable projection conservatively. Never infer a range
            // from the raw receipt's length: a complete raw read can display only a bounded prefix.
            // A changed/unknown presentation leaves an ordinary unpinned receipt, not guessed visibility.
            var header = "Path: " + path + "\nSHA-256: " + hash + "\nDisplayed source lines: ";
            if (!projected.StartsWith(header, StringComparison.Ordinal)) return null;
            var coordinatesEnd = projected.IndexOf('\n', header.Length);
            if (coordinatesEnd < 0) return null;
            var coordinate = projected[header.Length..coordinatesEnd];
            var dash = coordinate.IndexOf('-'); var of = coordinate.IndexOf(" of ", StringComparison.Ordinal);
            if (dash < 1 || of <= dash ||
                !int.TryParse(coordinate[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var displayedStart) ||
                !int.TryParse(coordinate[(dash + 1)..of], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
                !int.TryParse(coordinate[(of + 4)..], NumberStyles.None, CultureInfo.InvariantCulture, out var displayedTotal) ||
                displayedStart != start || displayedTotal != total || end < start || end > total ||
                end - start + 1 > request.LineCount) return null;
            var bodyAt = projected.IndexOf("\nSource:\n", coordinatesEnd, StringComparison.Ordinal);
            if (bodyAt < 0) return null;
            var guidance = projected[(coordinatesEnd + 1)..bodyAt];
            var source = (contentValue.GetString() ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var lines = source.Split('\n');
            var visibleCount = end - start + 1;
            if (guidance.Contains('\n') || !ValidReadGuidance(input, guidance, end, total,
                visibleCount, lines.Length)) return null;
            var body = projected[(bodyAt + "\nSource:\n".Length)..];
            if (visibleCount > lines.Length || body != string.Join('\n', lines.Take(visibleCount))) return null;
            var terminalEmpty = end == total && lines[visibleCount - 1].Length == 0;
            var key = TextHash((OperatingSystem.IsWindows() ? request.Path.ToUpperInvariant() : request.Path) + "\n" + hash.ToLowerInvariant() + "\n" +
                start.ToString(CultureInfo.InvariantCulture) + "\n" + visibleCount.ToString(CultureInfo.InvariantCulture) + "\n" + body);
            return new(contributionId, receiptId, request.Path, hash.ToLowerInvariant(), start, visibleCount,
                visibleCount - (terminalEmpty ? 1 : 0), total, request.StartLine, request.LineCount, FileReadSignature(arguments), key, lines.Length > visibleCount);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return null; }
    }

    private static bool ValidReadGuidance(Input input, string guidance, int end, int total,
        int visibleCount, int returnedCount)
    {
        // Earlier Develop editions retain their admitted parser. The guarded edition admits only
        // the current public projection's exact three forms, including a fully displayed bounded
        // range that truthfully reports additional file lines outside that request.
        if (!input.GuardFinalization) return guidance.StartsWith("Continuation: ", StringComparison.Ordinal);
        if (returnedCount > visibleCount)
            return guidance == $"Continuation: call file.read with startLine={end + 1} and lineCount={Math.Min(60, Math.Max(1, total - end))}. Do not reread this range.";
        if (end < total)
            return guidance ==
                $"Additional file lines {end + 1}-{total} exist outside this returned range; read only if the accepted request needs them.";
        return guidance == "Continuation: none; this requested source range is complete.";
    }
}
