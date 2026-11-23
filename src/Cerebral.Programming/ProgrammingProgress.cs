using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private sealed record FailedBuildObservation(string CommandSha256, int EditGeneration, string ReceiptId,
        int ExitCode, string DiagnosticSha256, int MatchingFailures, CompilerDiagnostic? Diagnostic = null)
    {
        public string? Configuration { get; init; }
        public string? CompilerFailureSha256 { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? MissingAssetsSha256 { get; init; }
        public int MutationGeneration { get; init; }
    }

    private const int MaximumMatchingBuildFailures = 2;

    // Only admitted build options reach this key. Reporting, restore and server switches do not
    // distinguish repeated identical compiler failures; effective Debug/Release always does.
    private static string BuildCommandIdentity(Input input, CheckRequest check) => TextHash(JsonSerializer.Serialize(new
    {
        project = OperatingSystem.IsWindows() ? input.FullProjectPath.ToUpperInvariant() : input.FullProjectPath,
        check.Configuration, kind = "build"
    }, Json));

    private static void RequireBuildProgress(Input input, HarnessState state, CheckRequest? check, JsonElement arguments)
    {
        if (check is not { Kind: "build" }) return;
        var key = BuildCommandIdentity(input, check);
        var failed = state.RepeatedBuildFailures.FirstOrDefault(item => item.CommandSha256 == key &&
            item.MutationGeneration == state.SourceMutationGeneration && item.CompilerFailureSha256 is not null &&
            item.MatchingFailures >= MaximumMatchingBuildFailures);
        if (failed is not null)
            throw new ArgumentException($"Two confirmed equivalent builds already failed with the same complete compiler diagnostic set for this project, {check.Configuration} configuration and confirmed source mutation generation {state.SourceMutationGeneration}. " +
                $"Latest failure receipt: {failed.ReceiptId}. Another equivalent build was not dispatched. Inspect the retained diagnostics and correct the project/source, or stop truthfully if no safe correction is available. " +
                "Changing verbosity, timeout, restore or build-server switches, reading, compaction and owner steering do not establish source progress.");
        if (!input.GuardPrerequisiteProgress ||
            !Strings(arguments.GetProperty("arguments")).Contains("--no-restore", StringComparer.Ordinal)) return;
        var prerequisite = state.RepeatedBuildFailures.FirstOrDefault(item => item.CommandSha256 == key &&
            item.MutationGeneration == state.SourceMutationGeneration && item.MissingAssetsSha256 is not null &&
            item.MatchingFailures >= MaximumMatchingBuildFailures);
        if (prerequisite is not null)
            throw new ArgumentException($"Two complete builds already confirmed the same NETSDK1004 missing-assets prerequisite for this project and {check.Configuration} configuration. " +
                $"Latest failure receipt: {prerequisite.ReceiptId}. Another --no-restore build was not dispatched. Run a normal build without --no-restore so the SDK can restore assets, or stop truthfully if that correction is unavailable. " +
                "Reporting, timeout and build-server switches do not establish prerequisite progress.");
    }

    private static string ContextPressureReason(ContextProjection projection)
    {
        if (projection.Metadata is not { } measured || measured.InputTokens < 0 || measured.EffectiveContext < 1 ||
            measured.DesiredResponseBudget < 1 || measured.ReasoningReserveTokens < 0 || measured.SafetyReserveTokens < 0)
            return "The host requested compaction before generation; complete measured reservation metadata was unavailable.";
        var reserved = (long)measured.InputTokens + measured.DesiredResponseBudget +
            measured.ReasoningReserveTokens + measured.SafetyReserveTokens;
        return reserved <= measured.EffectiveContext
            ? $"The host requested early compaction before generation. The full request fits its measured reservations: input={measured.InputTokens}, desiredResponse={measured.DesiredResponseBudget}, reasoning={measured.ReasoningReserveTokens}, safety={measured.SafetyReserveTokens}, context={measured.EffectiveContext}."
            : $"The full request does not fit its measured reservations; the host reports that the required floor fits. input={measured.InputTokens}, desiredResponse={measured.DesiredResponseBudget}, reasoning={measured.ReasoningReserveTokens}, safety={measured.SafetyReserveTokens}, context={measured.EffectiveContext}.";
    }

    private static HarnessState ObserveBuild(Input input, HarnessState state, HarnessState next, WorkflowEffect effect, ToolData? data)
    {
        if (ConfirmedProjectInputMutation(input, state, effect, data) is { } mutation)
        {
            var generation = Increment(state.SourceMutationGeneration);
            // A configuration-specific witness cannot retire another configuration's failure.
            // Rebase surviving lanes so the global observation generation does not bypass them.
            return next with { SourceMutationGeneration = generation, RepeatedBuildFailures = state.RepeatedBuildFailures
                .Where(item => !mutation.Configurations.Contains(item.Configuration, StringComparer.Ordinal))
                .Select(item => item with { MutationGeneration = generation }).ToArray() };
        }
        if (state.PendingCheck is not { Kind: "build" } check || state.PendingArguments is not { } arguments) return next;
        if (next.Checks.Any(item => item.Kind == "build" && item.ReceiptId == effect.Id))
            return next with { LastFailedBuild = null,
                RepeatedBuildFailures = state.RepeatedBuildFailures.Where(item => item.Configuration != check.Configuration).ToArray() };
        if (effect.State != WorkflowEffectState.Failed || data is not { FollowUpRequired: false } ||
            data.Evidence is not { Outcome: ToolExecutionOutcome.Failed, Partial: not true, ExitCode: not null } evidence ||
            evidence.ExitCode == 0 || data.Truncated) return next;
        var batch = ProgrammingCompilerDiagnostics.Parse(data.Content, evidence.ExitCode.Value);
        var compiler = batch.Fingerprint;
        // Unrecognized errors retain the legacy exact-argv advisory identity. Only a complete
        // supported compiler failure may use the broader equivalent-build policy key.
        var argv = Strings(arguments.GetProperty("arguments"));
        var missingAssets = input.GuardPrerequisiteProgress && argv.Contains("--no-restore", StringComparer.Ordinal) && compiler is null &&
            evidence.ProviderId == "cerebral.process" && evidence.State == "completed" &&
            (evidence.RequestId is null || evidence.RequestId == effect.Id)
            ? ProgrammingCompilerDiagnostics.MissingAssetsFingerprint(data.Content, evidence.ExitCode.Value, input.FullProjectPath) : null;
        var normalized = argv.Select(value => value is "-c" ? "--configuration" : value is "-v" ? "--verbosity" : value).ToArray();
        var grouped = compiler is not null || missingAssets is not null;
        var command = grouped ? BuildCommandIdentity(input, check) : TextHash(JsonSerializer.Serialize(new
            { project = input.FullProjectPath, check.Configuration, argv = normalized }, Json));
        var diagnostic = compiler ?? missingAssets ?? TextHash(effect.Error ?? "");
        var previous = grouped ? state.RepeatedBuildFailures.FirstOrDefault(item => item.CommandSha256 == command) : state.LastFailedBuild;
        var count = previous is not null && previous.CommandSha256 == command &&
            (grouped ? previous.MutationGeneration == state.SourceMutationGeneration : previous.EditGeneration == state.EditCount) &&
            previous.ExitCode == evidence.ExitCode &&
            previous.DiagnosticSha256 == diagnostic ? Increment(previous.MatchingFailures) : 1;
        var observed = new FailedBuildObservation(command, state.EditCount, effect.Id, evidence.ExitCode.Value, diagnostic, count,
            ReadCompilerDiagnostic(input, state, batch, effect.Id))
            { Configuration = check.Configuration, CompilerFailureSha256 = compiler,
                MissingAssetsSha256 = missingAssets, MutationGeneration = state.SourceMutationGeneration };
        // A failed restoring attempt with no complete classified result has not proved that
        // missing assets were repaired. Keep that prerequisite history in this edition while
        // preserving the older compiler policy's same-configuration reset on such failures.
        var lanes = state.RepeatedBuildFailures.Where(item => item.MutationGeneration == state.SourceMutationGeneration &&
            (item.Configuration != check.Configuration || input.GuardPrerequisiteProgress && !grouped && item.MissingAssetsSha256 is not null));
        return next with { LastFailedBuild = observed,
            RepeatedBuildFailures = grouped ? lanes.Append(observed).ToArray() : lanes.ToArray() };
    }

    private static object? BuildAdvice(HarnessState state) => state.LastFailedBuild is not { } failed ? null : new
    {
        failed.ReceiptId, failed.CommandSha256, failed.EditGeneration, failed.ExitCode, failed.MatchingFailures,
        sameSourceEditGeneration = failed.EditGeneration == state.EditCount,
        equivalentBuildLimit = MaximumMatchingBuildFailures,
        state.SourceMutationGeneration,
        equivalentCompilerFailures = state.RepeatedBuildFailures.Where(item => item.MutationGeneration == state.SourceMutationGeneration)
            .Select(item => new { item.Configuration, item.MatchingFailures, item.ReceiptId, item.CompilerFailureSha256,
                item.MissingAssetsSha256, item.MutationGeneration }),
        qualification = failed.MissingAssetsSha256 is null
            ? "The guard uses accepted project/configuration and separately proved input mutation generation, not legacy successful-operation EditCount; dependencies and environment may have changed. Two confirmed matching complete C# compiler failures prevent another equivalent build until a complete matching receipt proves changed bytes in the accepted project or an input bound by supported project/compiler evidence. A file extension or location alone does not prove membership. Skipped/identical or unrelated writes, reads, focus, compaction and steering do not unlock it; unconfirmed failures acquire no grouping credit."
            : "Two matching complete NETSDK1004 missing-assets failures prevent another --no-restore build for the accepted project and configuration. A normal restoring build remains available without a source edit. Reporting, timeout and build-server switches do not establish prerequisite progress; unconfirmed failures acquire no grouping credit."
    };
}
