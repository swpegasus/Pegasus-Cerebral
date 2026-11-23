using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private const string FinalizationFeedbackCategory = "finalization-feedback";
    private sealed class FinalizationPendingRepairException(string message) : ArgumentException(message);
    private sealed class FinalizationBlockedException(string message) : ArgumentException(message);

    // A failed SDK operation is an effect observation, not a failed child process. Its original
    // receipt and required diagnostic stay bound to the accepted assignment across subsequent reads.
    private sealed record FinalizationFailureObservation(string Name, string Root, string ProjectFile,
        string Configuration, string Manifest, string EffectId, string BuildReceiptId,
        int SourceMutationGeneration, string RelevantScopeSha256, IReadOnlyList<string> RelevantPaths,
        string ContributionId, string BodySha256, bool Complete, bool Uncertain,
        string? RepairEffectId = null, int? RepairMutationGeneration = null)
    {
        public IReadOnlyList<FinalizationReadCandidate> LaterSources { get; init; } = [];
    }
    private sealed record FinalizationReadCandidate(string Path, string Sha256, string EvidenceKey, string ReceiptId);

    // A newly inspected source is eligible only after a complete exact file.read from the
    // accepted project tree. The read records a possible target; it never counts as a repair.
    private static bool ProjectSourcePath(Input input, string path)
    {
        var projectDirectory = Path.GetDirectoryName(input.FullProjectPath)!;
        if (!Path.GetExtension(input.FullProjectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(path) || !Within(path, projectDirectory) || !Within(path, input.Root) ||
            !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(projectDirectory, path);
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RetainsFinalizationFeedback(HarnessState state, ContextContribution source) =>
        state.FinalizationFailure is { } failed && source.Id == failed.ContributionId &&
        source.Owner == "pegasus.programming" && source.Category == FinalizationFeedbackCategory;

    private static HarnessState ClearFinalizationFeedback(HarnessState state)
    {
        if (state.FinalizationFailure is null) return state;
        return state with { FinalizationFailure = null,
            Contributions = state.Contributions.Where(source => !RetainsFinalizationFeedback(state, source)).ToArray(),
            ContextRevision = checked(state.ContextRevision + 1) };
    }

    private static IReadOnlyList<string> RelevantFinalizationPaths(Input input, HarnessState state)
    {
        var projectDirectory = Path.GetDirectoryName(input.FullProjectPath)!;
        var paths = state.ReadHistory.Select(item => item.Span?.Path)
            .Concat(state.ReadProjectionReferences.Select(item => item.Source?.Path))
            .Append(state.WorkingSource?.Path).Append(input.FullProjectPath);
        return paths.Where(path => path is not null && Path.IsPathFullyQualified(path) && SourcePath(path) &&
                Within(path, projectDirectory) && Within(path, input.Root))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).Take(MaximumReadMappings + 1).ToArray();
    }

    private static string FinalizationScopeHash(int sourceMutationGeneration, IReadOnlyList<string> paths) =>
        TextHash(JsonSerializer.Serialize(new { sourceMutationGeneration, paths }, Json));

    private static bool ConfirmedFinalizationSuccess(WorkflowEffect effect, ToolData? data) =>
        effect.State == WorkflowEffectState.Succeeded && data is { Truncated: false, FollowUpRequired: false,
            Evidence: { Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } evidence } &&
        (evidence.RequestId is null || evidence.RequestId == effect.Id);

    private sealed record InterfaceFinalizationProof(string PackageFingerprint, string? RequestId);
    private sealed record ConfirmedPresentation(string PackageFingerprint, string RequestId, string EffectId);

    // The interface request ID identifies the queued owner review, not the workflow effect.
    // Check both typed channels against the accepted package before granting queue credit.
    private static InterfaceFinalizationProof? ConfirmedInterfaceFinalization(Input input, HarnessState state,
        WorkflowEffect effect, ToolData? data)
    {
        if (effect.State != WorkflowEffectState.Succeeded || data is not { Truncated: false, FollowUpRequired: false,
                Evidence: { Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } evidence } ||
            !ProjectionComplete(data.Content) || input.PresentManifest is null) return null;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var result = document.RootElement;
            if (state.PendingName == VerifyPlugin)
            {
                if (evidence.ProviderId != "cerebral.plugin-sdk" || evidence.State != "verified" ||
                    evidence.RequestId is not null || evidence.JobId is not null || evidence.ExitCode is not null ||
                    !ExactFields(result, "root", "pluginId", "desktop", "service", "verified") ||
                    !JsonPath(result, "root", input.Root) ||
                    !RequiredScalar(result, "pluginId", out var pluginId) || pluginId.Length > 128 ||
                    !result.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
                    !result.TryGetProperty("desktop", out var desktop) ||
                    !ExactFields(desktop, "manifest", "assembly", "packageFingerprint") ||
                    !JsonPath(desktop, "manifest", input.PresentManifest) ||
                    !RequiredScalar(desktop, "assembly", out var assembly) || !Path.IsPathFullyQualified(assembly) ||
                    !Within(assembly, input.Root) || !Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    !RequiredScalar(desktop, "packageFingerprint", out var fingerprint) || !LowerHash(fingerprint) ||
                    !result.TryGetProperty("service", out var service) ||
                    service.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) return null;
                return new(fingerprint, null);
            }
            if (state.PendingName == PresentPlugin)
            {
                if (evidence.ProviderId != "cerebral.interface" || evidence.State is not null ||
                    evidence.JobId is not null || evidence.ExitCode is not null ||
                    !ExactFields(result, "requestId", "status", "kind", "packageFingerprint", "message") ||
                    !RequiredScalar(result, "requestId", out var requestId) ||
                    !Guid.TryParseExact(requestId, "N", out var parsed) || parsed == Guid.Empty ||
                    requestId == effect.Id || evidence.RequestId != requestId ||
                    !RequiredScalar(result, "status", out var status) || status != "pending" ||
                    !RequiredScalar(result, "kind", out var kind) || kind != "plugin" ||
                    !RequiredScalar(result, "packageFingerprint", out var fingerprint) || !LowerHash(fingerprint) ||
                    fingerprint != state.VerifiedPackageFingerprint ||
                    state.ConfirmedPresentations?.Any(item => item.PackageFingerprint == fingerprint ||
                        item.RequestId == requestId || item.EffectId == effect.Id) == true ||
                    !RequiredScalar(result, "message", out _)) return null;
                return new(fingerprint, requestId);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static bool ExactFields(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == names.Length &&
        value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == names.Length &&
        value.EnumerateObject().All(item => names.Contains(item.Name, StringComparer.Ordinal));

    private static bool RequiredScalar(JsonElement value, string name, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } actual || actual.Any(char.IsControl)) return false;
        text = actual;
        return true;
    }

    private static bool JsonPath(JsonElement value, string name, string expected) =>
        RequiredScalar(value, name, out var path) && Path.IsPathFullyQualified(path) && SamePath(path, expected);

    private static bool LowerHash(string value) => value.Length == 64 &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool ValidInterfacePresentationState(HarnessState state) =>
        state.PluginVerified == (state.VerifiedPackageFingerprint is not null) &&
        (state.VerifiedPackageFingerprint is null || LowerHash(state.VerifiedPackageFingerprint)) &&
        state.PluginPresented == (state.PresentationRequestId is not null) &&
        (!state.PluginPresented || state.PluginVerified &&
            Guid.TryParseExact(state.PresentationRequestId, "N", out var parsed) && parsed != Guid.Empty) &&
        (state.ConfirmedPresentations is null || state.ConfirmedPresentations.All(item => item is not null &&
            item.PackageFingerprint is { } fingerprint && LowerHash(fingerprint) && Guid.TryParseExact(item.RequestId, "N", out var id) && id != Guid.Empty &&
            !string.IsNullOrWhiteSpace(item.EffectId) && item.EffectId.Length <= 128 && !item.EffectId.Any(char.IsControl)) &&
            state.ConfirmedPresentations.Select(item => item.PackageFingerprint).Distinct(StringComparer.Ordinal).Count() == state.ConfirmedPresentations.Count &&
            state.ConfirmedPresentations.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count() == state.ConfirmedPresentations.Count &&
            state.ConfirmedPresentations.Select(item => item.EffectId).Distinct(StringComparer.Ordinal).Count() == state.ConfirmedPresentations.Count &&
            (!state.PluginPresented || state.ConfirmedPresentations.Any(item => item.PackageFingerprint == state.VerifiedPackageFingerprint &&
                item.RequestId == state.PresentationRequestId)));

    private static HarnessState ObserveFinalization(Input input, HarnessState before, HarnessState next,
        WorkflowEffect effect, ToolData? data, bool confirmedSuccess)
    {
        if (!input.GuardFinalization) return next;
        if (before.PendingName is VerifyPlugin or PresentPlugin)
        {
            if (confirmedSuccess) return ClearFinalizationFeedback(next);
            next = ClearFinalizationFeedback(next);
            var knownRejection = effect.State == WorkflowEffectState.Failed &&
                data is { Truncated: false, FollowUpRequired: false,
                    Evidence: { Outcome: ToolExecutionOutcome.Rejected or ToolExecutionOutcome.Failed, Partial: not true } evidence } &&
                (evidence.RequestId is null || evidence.RequestId == effect.Id);
            var uncertain = !knownRejection;
            var build = before.Checks.LastOrDefault(check => check.Kind == "build" && check.Configuration == input.Configuration);
            var paths = RelevantFinalizationPaths(input, before);
            var diagnostic = JsonSerializer.Serialize(new { name = before.PendingName, receiptId = effect.Id,
                effectState = effect.State.ToString(), outcome = data?.Evidence?.Outcome.ToString(),
                error = effect.Error, content = data?.Content, data?.Truncated, data?.FollowUpRequired,
                classification = uncertain ? "unconfirmed-outcome-no-replay" : before.PendingName == PresentPlugin
                    ? "presentation-rejected-owner-action-required" : "verification-rejected-source-repair-required",
                qualification = "This original SDK result is reference data. It grants no verification or presentation credit. Inspect the exact effect receipt for omitted content." }, Json);
            var id = "finalization-feedback." + effect.Id;
            next = AppendProjection(next, id, FinalizationFeedbackCategory, "user", diagnostic,
                ContextRequirement.Required, effect.Id, complete: ProjectionComplete(diagnostic));
            var contribution = next.Contributions.Single(source => source.Id == id);
            var body = ((TextContentPart)contribution.Messages.Single().Content.Single()).Text;
            return next with { FinalizationFailure = new(before.PendingName!, input.Root, input.FullProjectPath,
                input.Configuration, input.PresentManifest!, effect.Id, build?.ReceiptId ?? "",
                before.SourceMutationGeneration, FinalizationScopeHash(before.SourceMutationGeneration, paths), paths,
                id, TextHash(body), contribution.Complete, uncertain) };
        }
        if (before.PendingName is { } name && before.PendingArguments is { } arguments && data is not null &&
            next.FinalizationFailure is { Name: VerifyPlugin, Uncertain: false } failure &&
            ProgrammingMutationProof.Read(name, arguments, effect.State, effect.Id, data.Content,
                data.Truncated, data.FollowUpRequired, data.Evidence) is { } changed &&
            (failure.RelevantPaths.Any(path => SamePath(path, changed.Path)) ||
                failure.LaterSources.Any(source => SamePath(source.Path, changed.Path) &&
                    arguments.TryGetProperty("expectedSha256", out var preimage) &&
                    preimage.ValueKind == JsonValueKind.String &&
                    source.Sha256.Equals(preimage.GetString(), StringComparison.OrdinalIgnoreCase))))
            return next with { FinalizationFailure = failure with { RepairEffectId = effect.Id,
                RepairMutationGeneration = next.SourceMutationGeneration } };
        if (before.PendingName == "file.read" && next.FinalizationFailure is { Name: VerifyPlugin, Uncertain: false } prior)
            next = next with { FinalizationFailure = prior with { LaterSources = prior.LaterSources
                .Where(candidate => HasFinalizationRead(next, candidate)).ToArray() } };
        if (before.PendingName == "file.read" && next.FinalizationFailure is { Name: VerifyPlugin, Uncertain: false } observed &&
            CurrentWorkingSource(next) is { } source && source.ReceiptId == effect.Id &&
            ProjectSourcePath(input, source.Path) &&
            !observed.RelevantPaths.Any(path => SamePath(path, source.Path)) &&
            !observed.LaterSources.Any(candidate => SamePath(candidate.Path, source.Path)) &&
            observed.LaterSources.Count < MaximumReadMappings)
            return next with { FinalizationFailure = observed with { LaterSources =
                [.. observed.LaterSources, new FinalizationReadCandidate(source.Path, source.Sha256, source.EvidenceKey, effect.Id)] } };
        return next;
    }

    private static bool HasFinalizationRead(HarnessState state, FinalizationReadCandidate candidate) =>
        state.ReadHistory.Any(read => read.EvidenceKey == candidate.EvidenceKey && read.Span is { } span &&
            SamePath(span.Path, candidate.Path) && span.Sha256.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase));

    private static void RequireFinalizationProgress(Input input, HarnessState state, string name)
    {
        if (!input.GuardFinalization || name is not (VerifyPlugin or PresentPlugin) ||
            state.FinalizationFailure is not { } failure) return;
        if (failure.Name == PresentPlugin || failure.Uncertain)
            throw new FinalizationBlockedException("The original " + failure.Name + " outcome at receipt " + failure.EffectId +
                " is rejected or uncertain. Presentation may already be queued; no automatic retry was dispatched. " +
                "Inspect its retained diagnostic and resolve the owner/interface authority before a new authorized run.");
        var currentBuild = state.Checks.LastOrDefault(check => check.Kind == "build" && check.Configuration == input.Configuration);
        if (failure.RepairEffectId is null || failure.RepairMutationGeneration is null ||
            failure.RepairMutationGeneration <= failure.SourceMutationGeneration ||
            currentBuild is null || currentBuild.ReceiptId == failure.BuildReceiptId)
            throw new FinalizationPendingRepairException("plugin.verify already rejected the observed source at receipt " +
                failure.EffectId + ". Inspect its retained diagnostic, make a confirmed changed-byte repair to an observed " +
                "verification input, then obtain a new passing build for the accepted project and configuration. " +
                "Reads, lookups, compaction, no-op or unrelated writes, and unchanged rebuilds do not unlock verification.");
    }

    private static string FinalizationStopReason(Input input, HarnessState state, WorkflowEffect effect)
    {
        if (!input.GuardFinalization || state.FinalizationFailure is not { } failure || failure.EffectId != effect.Id)
            return effect.Error ?? "The tool did not return a confirmed outcome; it was not replayed.";
        return failure.Uncertain
            ? "The " + failure.Name + " outcome at receipt " + failure.EffectId +
                " is incomplete or uncertain; presentation may already be queued. No replay was requested. Inspect the original receipt and resolve its outcome before a new authorized run."
            : "plugin.present rejected the exact accepted manifest at receipt " + failure.EffectId +
                ". Its original diagnostic remains required context. Resolve the owner/interface authority; source edits or builds alone cannot authorize another presentation.";
    }

    private static bool ValidFinalizationFailureState(Input input, HarnessState state)
    {
        if (state.FinalizationFailure is not { } failure) return true;
        if (input.PresentManifest is null || failure.Name is not (VerifyPlugin or PresentPlugin) ||
            string.IsNullOrWhiteSpace(failure.Root) || string.IsNullOrWhiteSpace(failure.ProjectFile) ||
            string.IsNullOrWhiteSpace(failure.Manifest) ||
            !SamePath(failure.Root, input.Root) || !SamePath(failure.ProjectFile, input.FullProjectPath) ||
            failure.Configuration != input.Configuration || !SamePath(failure.Manifest, input.PresentManifest) ||
            string.IsNullOrWhiteSpace(failure.EffectId) || failure.EffectId.Length > 128 || failure.EffectId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(failure.BuildReceiptId) || failure.BuildReceiptId.Length > 128 ||
            failure.BuildReceiptId.Any(char.IsControl) || state.PluginPresented ||
            failure.Name == VerifyPlugin && state.PluginVerified ||
            failure.SourceMutationGeneration < 0 || failure.SourceMutationGeneration > state.SourceMutationGeneration ||
            !ProgrammingEvidence.IsHash(failure.RelevantScopeSha256) || !ProgrammingEvidence.IsHash(failure.BodySha256) ||
            failure.RelevantPaths is null || failure.RelevantPaths.Count is < 1 or > MaximumReadMappings + 1 ||
            failure.RelevantPaths.Any(path => path is null || !Path.IsPathFullyQualified(path) || !SourcePath(path) ||
                !Within(path, Path.GetDirectoryName(input.FullProjectPath)!) || !Within(path, input.Root)) ||
            !failure.RelevantPaths.Any(path => SamePath(path, input.FullProjectPath)) ||
            failure.RelevantPaths.Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != failure.RelevantPaths.Count ||
            failure.RelevantScopeSha256 != FinalizationScopeHash(failure.SourceMutationGeneration, failure.RelevantPaths) ||
            failure.LaterSources is null || failure.LaterSources.Count > MaximumReadMappings ||
            failure.LaterSources.Any(candidate => candidate is null || string.IsNullOrWhiteSpace(candidate.Path) ||
                !ProjectSourcePath(input, candidate.Path) ||
                !ProgrammingEvidence.IsHash(candidate.Sha256) || !ProgrammingEvidence.IsHash(candidate.EvidenceKey) ||
                string.IsNullOrWhiteSpace(candidate.ReceiptId) || candidate.ReceiptId.Length > 128 ||
                candidate.ReceiptId.Any(char.IsControl) || !HasFinalizationRead(state, candidate) ||
                failure.RelevantPaths.Any(path => SamePath(path, candidate.Path))) ||
            failure.LaterSources.Select(candidate => candidate.Path).Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != failure.LaterSources.Count ||
            failure.ContributionId != "finalization-feedback." + failure.EffectId ||
            (failure.Uncertain || failure.Name == PresentPlugin) && failure.RepairEffectId is not null ||
            failure.RepairEffectId is null && failure.RepairMutationGeneration is not null ||
            failure.RepairEffectId is not null && (failure.RepairEffectId.Length > 128 ||
                failure.RepairEffectId.Any(char.IsControl)) ||
            failure.RepairEffectId is not null && (failure.RepairMutationGeneration is null ||
                failure.RepairMutationGeneration <= failure.SourceMutationGeneration ||
                failure.RepairMutationGeneration > state.SourceMutationGeneration)) return false;
        var matches = state.Contributions.Where(source => RetainsFinalizationFeedback(state, source)).ToArray();
        return matches.Length == 1 && matches[0].Requirement == ContextRequirement.Required &&
            matches[0].ExactReadHandle == failure.EffectId && matches[0].Complete == failure.Complete &&
            matches[0].Messages.Count == 1 && matches[0].Messages[0].Role == "user" &&
            matches[0].Messages[0].Content is [TextContentPart text] &&
            TextHash(text.Text) == failure.BodySha256;
    }

    private static string FinalizationFailureAdvice(HarnessState state) => state.FinalizationFailure is not { } failure ? "" :
        "\nOriginal finalization receipt " + failure.EffectId + " for " + failure.Name +
        " remains required context. Its diagnostic is tied to the accepted project, configuration, manifest and observed source generation. " +
        (failure.Name == PresentPlugin || failure.Uncertain
            ? "Do not retry: owner/interface rejection or uncertain presentation requires an explicit outcome and authority resolution."
            : "A complete exact read of existing .cs source inside the accepted project tree can identify another repair target. " +
              "Only a proved changed-byte repair to a bound target followed by a fresh passing build can unlock plugin.verify. " +
              "Reads alone, lookups, compaction, no-op or unrelated writes, and unchanged rebuilds have no repair credit. " +
              "Unproven new-file membership is not repair credit.");
}
