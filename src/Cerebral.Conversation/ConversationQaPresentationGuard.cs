using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumQaPresentationIdentities = 8;
    private static WorkflowStep? GuardAutomaticQaPresentation(Input input, HarnessState state,
        string name, JsonElement arguments)
    {
        // This is an execution prerequisite for the exact service-owned installed QA, not
        // an approval inferred from a tool name, model text, or another conversation.
        if (name != "plugin.present" || input.AutomaticPluginTestPresentationId is not { } reviewed ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) ||
            state.PluginWork is not { Build.Component: "desktop" } work ||
            !TryActionPath(input, arguments, "path", out var path) || !PathEquals(path, work.Build.BuiltManifest) ||
            work.VerifiedGeneration != work.SourceGeneration || work.VerifyEffectId is null)
            return null;
        if (state.NativeReviewFailure is { } review && review.PresentationId == reviewed &&
            input.HostPluginTestMountIds.Count == 0 && PathEquals(review.SourceManifest, path)) return null;

        var fingerprint = work.VerifiedPackageFingerprint;
        string? reason = null;
        if (ValidPackageFingerprint(fingerprint))
        {
            var duplicate = state.ToolReceipts.LastOrDefault(receipt => receipt.Presentation is { } presentation &&
                PathEquals(presentation.SourceManifest, path) &&
                string.Equals(presentation.PackageFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
                reason = "These exact package bytes already have successful plugin.present receipt " +
                    duplicate.EffectId + " and native review request " + duplicate.Presentation!.RequestId +
                    ". Preserve that request and inspect its actual status; rebuilding or verifying does not create a new review need.";
        }
        if (reason is null && work.PresentedGeneration == work.SourceGeneration && work.PresentEffectId is { } existingEffect)
            reason = "This current generation already retains completed presentation effect " + existingEffect +
                ". Its exact native review identity is not fully confirmed; inspect the retained result and actual review status before repeating. A tool success is not approval or installation.";
        if (reason is null && work.SourceGeneration == 0)
            reason = "This installed automatic QA has no confirmed material source change. Rebuilding the approved source is not a repair.";
        if (reason is null && !ValidPackageFingerprint(fingerprint))
            reason = "Current verification has no complete retained package fingerprint, so a different replacement cannot be established.";
        if (reason is null && state.ToolReceipts.Count(receipt => receipt.Presentation is not null) >= MaximumQaPresentationIdentities)
            reason = "This QA reached its bounded eight-package review identity capacity. Preserve the retained reviews and report the limitation rather than dropping duplicate proof or submitting another request.";
        if (reason is null)
        {
            var observations = state.NativeControlCoverage.Where(item => item.PresentationId == reviewed &&
                input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal)).ToArray();
            var failures = state.NativeControlFailures.Where(item => item.PresentationId == reviewed).ToArray();
            var complete = observations.Length + failures.Length != 0 &&
                observations.All(item => PathEquals(item.SourceManifest, path) &&
                    HasQaObservationReceipt(state, item.EffectId, failed: false)) &&
                failures.All(item => item.CompleteObservation && item.SourceManifest is not null &&
                    PathEquals(item.SourceManifest, path) && ValidPackageFingerprint(item.PackageFingerprint) &&
                    input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal) &&
                    HasQaObservationReceipt(state, item.EffectId, failed: true));
            if (!complete)
                reason = "No complete native package observation identifies this exact approved source and its reviewed mounts. A source edit or rebuild alone cannot establish a tested replacement.";
            else if (observations.Any(item => string.Equals(item.PackageFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) ||
                failures.Any(item => string.Equals(item.PackageFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                reason = "The verified package fingerprint still equals the approved package observed by native controls. These unchanged bytes are not a repair.";
            else if (work.BuildEffectId is null || observations.Any(item => !ReceiptAfter(state, work.BuildEffectId, item.EffectId)) ||
                failures.Any(item => !ReceiptAfter(state, work.BuildEffectId, item.EffectId)))
                reason = "The current canonical build did not follow the retained approved-package observation. Build and verify the actual repair after that evidence.";
        }
        if (reason is null) return null;

        var next = input.HostPluginTestMountIds.FirstOrDefault() is { } mount
            ? "Test the current owner-approved controls first with plugin.controls " +
                JsonSerializer.Serialize(new { presentationId = PluginControlReferences.ReviewedReference,
                    mountId = mount, operation = "inspect" }, Json) +
                ", then use its real enabled controls and evaluate the result."
            : "The frozen approved mount list is unavailable. Read this exact owner's interface.status and report that coverage limitation; do not invent mounts or permission.";
        var diagnostic = reason + " " + next +
            " Only a material source change with exact native observation and a different verified package may request replacement review. No presentation was dispatched; retained successes, failures and pending reviews are unchanged.";
        // Valid action JSON resets the ordinary syntax correction count. Keep this semantic
        // budget independent so repeated presents cannot evade it by interleaving rebuilds.
        // It never blocks useful native controls or a genuinely evidenced changed package.
        if (state.AutomaticPresentationCorrections >= MaximumRepairs)
            return Finish(state, false, "Automatic plugin QA exhausted two presentation corrections. " + diagnostic);
        return Repair(input, state with
            { AutomaticPresentationCorrections = state.AutomaticPresentationCorrections + 1 }, diagnostic);
    }

    private static bool HasQaObservationReceipt(HarnessState state, string effectId, bool failed) =>
        state.ToolReceipts.Any(receipt => receipt.EffectId == effectId && receipt.Name == "plugin.controls" &&
            !receipt.Truncated && (failed
                ? receipt.State == nameof(WorkflowEffectState.Failed) && receipt.Outcome == nameof(ToolExecutionOutcome.Failed)
                : receipt.State == nameof(WorkflowEffectState.Succeeded) && receipt.Outcome == nameof(ToolExecutionOutcome.Succeeded)));

    private static QaPresentationIdentity? ReadQaPresentationIdentity(Input input, HarnessState state,
        WorkflowEffect effect, ToolData? data)
    {
        if (input.AutomaticPluginTestPresentationId is null ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) || state.NativeReviewFailure is not null ||
            state.PendingName != "plugin.present" ||
            !SuccessfulEffect(effect, data) || data is null ||
            state.PluginWork is not { } work || work.PresentEffectId != effect.Id ||
            work.PresentedGeneration != work.SourceGeneration ||
            work.PresentRequestId is not { } request || !ValidPresentationRequestId(request) ||
            !ValidPackageFingerprint(work.PresentedPackageFingerprint) ||
            !string.Equals(work.PresentedPackageFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase))
            return null;
        // These fields were adopted only by the native-provider identity check, including
        // its exact returned request/hash. Do not reconstruct them from display text.
        return new(work.Build.BuiltManifest, work.PresentedPackageFingerprint!, request);
    }

    private static HarnessState RetainCurrentQaPresentation(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is null || state.PluginWork is not { } work ||
            work.PresentedGeneration != work.SourceGeneration || work.PresentEffectId is not { } effect ||
            work.PresentRequestId is not { } request || !ValidPresentationRequestId(request) ||
            !ValidPackageFingerprint(work.PresentedPackageFingerprint) ||
            !ValidPackageFingerprint(work.VerifiedPackageFingerprint) ||
            !string.Equals(work.PresentedPackageFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase)) return state;
        // Old admitted state can retain the complete current native coordinate while its
        // private receipt predates this field. Preserve only that actual succeeded receipt,
        // before a subsequent build/verify invalidates the current-generation binding.
        var receipt = state.ToolReceipts.SingleOrDefault(item => item.EffectId == effect &&
            item.Name == "plugin.present" && item.State == nameof(WorkflowEffectState.Succeeded) &&
            item.Outcome == nameof(ToolExecutionOutcome.Succeeded) && !item.Truncated);
        if (receipt is null || receipt.Presentation is not null) return state;
        if (state.ToolReceipts.Count(item => item.Presentation is not null) >= MaximumQaPresentationIdentities) return state;
        var identity = new QaPresentationIdentity(work.Build.BuiltManifest, work.PresentedPackageFingerprint!, request);
        return state with { ToolReceipts = state.ToolReceipts.Select(item => ReferenceEquals(item, receipt)
            ? item with { Presentation = identity } : item).ToArray() };
    }

    private static void ValidateQaPresentations(HarnessState state)
    {
        if (state.AutomaticPresentationCorrections is < 0 or > MaximumRepairs)
            throw new ArgumentException("The saved automatic QA presentation correction count is invalid.");
        if (state.ToolReceipts.Count(item => item.Presentation is not null) > MaximumQaPresentationIdentities)
            throw new ArgumentException("The saved automatic QA presentation identities exceed their bounded capacity.");
        foreach (var receipt in state.ToolReceipts.Where(item => item.Presentation is not null))
        {
            var identity = receipt.Presentation!;
            if (receipt.Name != "plugin.present" || receipt.State != nameof(WorkflowEffectState.Succeeded) ||
                receipt.Outcome != nameof(ToolExecutionOutcome.Succeeded) || receipt.Truncated ||
                !CanonicalStoredPath(identity.SourceManifest) || !ValidPackageFingerprint(identity.PackageFingerprint) ||
                !ValidPresentationRequestId(identity.RequestId))
                throw new ArgumentException("The saved automatic QA presentation identity has no compatible successful receipt.");
        }
    }

    private static HarnessState ResetQaPresentationCorrectionsAfterObservation(Input input,
        HarnessState before, HarnessState after)
    {
        if (after.AutomaticPresentationCorrections == 0 || input.AutomaticPluginTestPresentationId is not { } reviewed)
            return after;
        var coverageProgress = after.NativeControlCoverage.Any(item => item.PresentationId == reviewed &&
            input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal) &&
            !before.NativeControlCoverage.Any(prior => prior.PresentationId == reviewed && prior.MountId == item.MountId &&
                PathEquals(prior.SourceManifest, item.SourceManifest) &&
                string.Equals(prior.PackageFingerprint, item.PackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
                (!item.InteractionObserved || prior.InteractionObserved)));
        var failureProgress = after.NativeControlFailures.Any(item => item.PresentationId == reviewed && item.CompleteObservation &&
            input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal) &&
            !before.NativeControlFailures.Any(prior => prior.PresentationId == reviewed && prior.MountId == item.MountId &&
                prior.CompleteObservation && prior.SourceManifest is not null && item.SourceManifest is not null &&
                PathEquals(prior.SourceManifest, item.SourceManifest) &&
                string.Equals(prior.PackageFingerprint, item.PackageFingerprint, StringComparison.OrdinalIgnoreCase)));
        return coverageProgress || failureProgress ? after with { AutomaticPresentationCorrections = 0 } : after;
    }
}
