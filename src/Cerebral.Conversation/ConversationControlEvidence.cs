using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumControlFailures = 8;
    private sealed record NativeControlFailure(string PresentationId, string MountId, string? ButtonName,
        string EffectId, string Diagnostic, string? SourceManifest = null, string? PackageFingerprint = null,
        string Operation = "invoke", IReadOnlyList<string>? ExpectedTexts = null,
        string? FieldId = null, string? ExpectedTextSha256 = null, string? TextSha256 = null,
        int? ExpectedTextUtf8Bytes = null, int? TextUtf8Bytes = null, bool CompleteObservation = false,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        bool MissingBeforeExpectations = false,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        bool SetupHadEditableFields = false,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        InvokeFieldLineSetup? InvokeFieldLineSetup = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        bool PackageIdentityIncomplete = false);

    private static HarnessState AdoptNativeControlEvidence(Input input, HarnessState state, WorkflowEffect effect, ToolData? data, string runId)
    {
        state = AdoptFailedNativeReviewEvidence(input, state, effect, data, runId);
        if (state.PendingName != "plugin.controls" || data is null ||
            data.Evidence?.ProviderId != "cerebral.plugin.controls" || data.Truncated ||
            Encoding.UTF8.GetByteCount(data.Content) > 32 * 1024 ||
            PendingToolArguments(state) is not { } arguments ||
            !TryText(arguments, "presentationId", 32, out var presentation) ||
            !TryText(arguments, "mountId", 129, out var mount) ||
            !TryText(arguments, "operation", 16, out var operation) || operation is not ("inspect" or "setText" or "invoke" or "reopen")) return state;
        string? button = null;
        if (operation == "invoke" && !TryText(arguments, "buttonName", 256, out button) ||
            operation == "reopen" && arguments.TryGetProperty("buttonName", out _)) return state;
        // Resolve only from the host-owned run identity; returned payloads cannot nominate an approval.
        // The Worker retains authorization, ownership and consent checks before executing this reference.
        if (presentation == PluginControlReferences.ReviewedReference)
        {
            if (!PluginControlReferences.TryResolveReviewedPresentationId(runId, out presentation)) return state;
        }
        else if (!Guid.TryParseExact(presentation, "N", out _)) return state;
        try
        {
            var expected = ReadControlExpectations(arguments, operation);
            var edit = ReadControlTextEdit(arguments, operation);
            var expectedHash = edit is null ? null : ControlTextHash(edit.ExpectedText);
            var replacementHash = edit is null ? null : ControlTextHash(edit.Text);
            using var parsed = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var body = parsed.RootElement;
            UniqueProperties(body);
            if (body.ValueKind != JsonValueKind.Object ||
                !TryText(body, "receipt", 256, out var receipt) || receipt != data.Evidence.RequestId ||
                !TryText(body, "presentationId", 32, out var returnedPresentation) || returnedPresentation != presentation ||
                !TryText(body, "mountId", 129, out var returnedMount) || returnedMount != mount ||
                !TryText(body, "operation", 16, out var returnedOperation) || returnedOperation != operation ||
                !TryText(body, "status", 32, out var status)) return state;
            if (expected is not null &&
                (!body.TryGetProperty("assertion", out var assertion) || assertion.ValueKind != JsonValueKind.Object ||
                 !PluginControlAssertions.SameExpectedTexts(expected, ReadControlExpectations(assertion, operation)) ||
                 !TryText(assertion, "status", 32, out var assertionStatus) ||
                 (status, assertionStatus) is not (("completed", "passed") or ("failed", "failed") or ("uncertain", "inconclusive")))) return state;
            var sourceManifest = TryText(body, "sourceManifest", MaximumBindingPathCharacters, out var source) &&
                TryCanonicalAbsolutePath(source, out var canonicalSource) ? canonicalSource : null;
            var packageFingerprint = TryText(body, "packageFingerprint", 64, out var fingerprint) && ValidPackageFingerprint(fingerprint)
                ? fingerprint.ToLowerInvariant() : null;
            var sourceManifestAbsent = !body.TryGetProperty("sourceManifest", out var sourceManifestProperty) ||
                sourceManifestProperty.ValueKind == JsonValueKind.Null;
            var packageFingerprintAbsent = !body.TryGetProperty("packageFingerprint", out var packageFingerprintProperty) ||
                packageFingerprintProperty.ValueKind == JsonValueKind.Null;
            // Current receipts either omit both legacy identity fields or provide one complete,
            // valid immutable-package pair. Malformed or partial pairs may retain a new failure,
            // but cannot replace prior evidence, earn coverage, clear a failure, or masquerade
            // as the historical absent shape.
            var admissiblePackageIdentity = sourceManifestAbsent && packageFingerprintAbsent ||
                !sourceManifestAbsent && !packageFingerprintAbsent &&
                sourceManifest is not null && packageFingerprint is not null;
            // Coverage belongs only to this approved automatic QA. Ordinary successful
            // control calls must not accumulate a completion-policy ledger or its bound.
            if (input.AutomaticPluginTestPresentationId == presentation &&
                input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) &&
                input.HostPluginTestMountIds.Contains(mount, StringComparer.Ordinal))
                state = AdoptNativeControlCoverage(state, effect, data, body, presentation, mount, operation, edit);
            if (operation == "inspect") return state;
            // A proved stale compare-and-set precondition rejected before native delivery is
            // failed setup, not observed plugin behavior. Preserve prior failures and the receipt.
            if (operation == "setText" && sourceManifest is not null && packageFingerprint is not null &&
                NoEffectStaleControlTextPrecondition(effect, data, body, mount, edit)) return state;
            bool RetainDifferentFailure(NativeControlFailure item) =>
                item.PresentationId != presentation || item.MountId != mount || item.Operation != operation || item.ButtonName != button ||
                !PluginControlAssertions.SameExpectedTexts(item.ExpectedTexts, expected) ||
                !SameControlEdit(item, edit, expectedHash, replacementHash);
            var matchingFailure = state.NativeControlFailures.Any(item => !RetainDifferentFailure(item));
            var remaining = admissiblePackageIdentity
                ? state.NativeControlFailures.Where(RetainDifferentFailure).ToArray()
                : state.NativeControlFailures.ToArray();
            if (SuccessfulEffect(effect, data) && status == "completed" &&
                (operation != "setText" || CompletedControlTextEdit(body, mount, edit)))
            {
                if (!admissiblePackageIdentity) return state;
                // A reviewed replacement has a new presentation ID. Only its exact verified
                // bytes and matching control can resolve the failed earlier generation.
                remaining = state.NativeControlFailures.Where(item => item.MissingBeforeExpectations
                    ? !CorrectedReopenSetup(item, presentation, mount, operation, body, expected, sourceManifest, packageFingerprint)
                    : item.InvokeFieldLineSetup is not null
                        ? !CorrectedInvokeFieldLineSetup(state, item, effect, data, body, presentation, mount,
                            operation, button, expected, sourceManifest, packageFingerprint)
                        : item.MountId != mount || item.Operation != operation || item.ButtonName != button ||
                      !PluginControlAssertions.SameExpectedTexts(item.ExpectedTexts, expected) ||
                      !SameControlEdit(item, edit, expectedHash, replacementHash) ||
                      (item.PresentationId == presentation
                          ? !SameRetainedControlPackage(item, sourceManifest, packageFingerprint,
                              sourceManifestAbsent, packageFingerprintAbsent)
                          : !ReplacementControlMatches(state, item, presentation, sourceManifest, packageFingerprint))).ToArray();
                return state with { NativeControlFailures = remaining,
                    NativeControlFinalCorrections = remaining.Length < state.NativeControlFailures.Count ? 0 : state.NativeControlFinalCorrections };
            }
            if (status is not ("rejected" or "uncertain" or "failed")) return state;
            if (!admissiblePackageIdentity && matchingFailure) return state;
            var diagnostic = body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "Native control did not complete." : "Native control did not complete.";
            diagnostic = BoundNativeControlDiagnostic(diagnostic,
                operation == "invoke" && sourceManifest is not null && packageFingerprint is not null &&
                InvokeLineEndingRepresentationMismatch(effect, data, body, mount, button, expected)
                    ? InvokeLineEndingRepresentationAdvice
                    : null);
            if (remaining.Length >= MaximumControlFailures)
                throw new InvalidOperationException("The bounded unresolved native-control failure limit was reached.");
            var invalidSetup = operation == "reopen" && sourceManifest is not null && packageFingerprint is not null &&
                MissingReopenPrecondition(effect, data, body, mount, expected) &&
                !state.NativeControlFailures.Any(item => !item.MissingBeforeExpectations &&
                    item.PresentationId == presentation && item.MountId == mount && item.Operation == operation &&
                    PluginControlAssertions.SameExpectedTexts(item.ExpectedTexts, expected));
            var setupEditable = invalidSetup &&
                (TryCompleteControlObservation(body, mount, out var setupObservation) &&
                 setupObservation.Before.Fields.Any(field => field.Visible && !field.ReadOnly) ||
                 state.NativeControlFailures.Any(item => item.MissingBeforeExpectations && item.SetupHadEditableFields &&
                     item.PresentationId == presentation && item.MountId == mount && item.Operation == operation &&
                     PluginControlAssertions.SameExpectedTexts(item.ExpectedTexts, expected) &&
                     item.SourceManifest is not null && sourceManifest is not null && PathEquals(item.SourceManifest, sourceManifest) &&
                     string.Equals(item.PackageFingerprint, packageFingerprint, StringComparison.OrdinalIgnoreCase)));
            return state with { NativeControlFailures =
                [.. remaining, new(presentation, mount, button, effect.Id, diagnostic, sourceManifest, packageFingerprint, operation, expected,
                    edit?.FieldId, expectedHash, replacementHash,
                    edit is null ? null : Encoding.UTF8.GetByteCount(edit.ExpectedText),
                    edit is null ? null : Encoding.UTF8.GetByteCount(edit.Text),
                    sourceManifest is not null && packageFingerprint is not null &&
                    CompleteNativePackageObservation(effect, data, body, mount),
                    invalidSetup, setupEditable,
                    operation == "invoke" && sourceManifest is not null && packageFingerprint is not null
                        ? DeriveInvokeFieldLineSetup(state, effect, data, body, presentation, mount, button, expected) : null,
                    PackageIdentityIncomplete: !admissiblePackageIdentity)] };
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return state; }
    }

    private static WorkflowStep? AutomaticReplacementOwnerReviewHandoff(Input input, HarnessState state)
    {
        // The exact automatic continuation ends at the separately reviewed replacement, before
        // another model action can invalidate its current-generation proof. Explicit original or
        // provider obligations and accepted owner steering/answers retain their ordinary completion path.
        if (!input.AutomaticReviewHandoffEligible || input.AutomaticPluginTestPresentationId is null ||
            state.PendingQuestions is not null ||
            state.Contributions.Any(source => !source.BeforeCurrentInput &&
                source.Owner == ConversationServicePlugin.ModuleId && source.Category == "user-answers") ||
            state.Checkpoint is { BeforeCurrentInput: false } checkpoint &&
                checkpoint.CoveredSources.Any(source => source.Id.StartsWith("answers.", StringComparison.Ordinal)) ||
            !ReplacementAwaitingOwnerReview(input, state) || MissingSuccessfulTools(input, state).Length != 0)
            return null;
        return RequireNativeControlResolution(input, state);
    }

    private static WorkflowStep? RequireNativeControlResolution(Input input, HarnessState state)
    {
        // An automatic follow-up may queue a different, canonically built package after either an
        // installed control observation or a failed review. The queued bytes cross a separate
        // owner-review boundary and remain untested. End truthfully instead of requiring effects
        // against the prior review identity.
        if (ReplacementAwaitingOwnerReview(input, state))
        {
            var retained = state.NativeControlFailures.Count == 0 ? "" : "\n" + string.Join("\n",
                state.NativeControlFailures.Select(item =>
                    item.MountId + " / " + (item.ButtonName ?? item.Operation) + ": " + item.Diagnostic +
                    (item.ExpectedTexts is null ? "" : " Expected text: " + string.Join(" | ", item.ExpectedTexts)) +
                    " (receipt " + item.EffectId + ")"));
            return Finish(state, true,
                "A replacement plugin was built, verified, and submitted for separate owner review. " +
                "This run has not established approval, installation, or functional testing of the replacement. " +
                "Evidence from the prior review does not describe the queued package. A failed review is not an installed approval or control receipt. " +
                "Any retained failures remain unverified and unresolved until that replacement is approved and tested." + retained);
        }
        if (RequireNativeReviewReplacement(input, state) is { } reviewCorrection) return reviewCorrection;
        if (state.NativeControlFailures.Count == 0) return RequireNativeControlCoverage(input, state);
        var facts = string.Join("\n", state.NativeControlFailures.Select(item =>
            item.MountId + " / " + (item.ButtonName ?? item.Operation) + ": " + item.Diagnostic +
                (item.ExpectedTexts is null ? "" : " Expected text: " + string.Join(" | ", item.ExpectedTexts)) + " (receipt " + item.EffectId + ")"));
        if (state.NativeControlFinalCorrections >= MaximumRepairs)
            return Finish(state, false, "Unresolved native plugin control failures remain after two completion corrections.\n" + facts);
        var setupAdvice = state.NativeControlFailures.Any(item => item.MissingBeforeExpectations)
            ? "\nA reopen marked invalid setup declared text absent from its complete before-view; that is inconclusive, not a proved plugin defect. " +
              "Inspect the current field, set a changed bounded value, perform the requested save action, and reopen with its exact whole current value in expectedTexts. " +
              "A later fully passed declared reopen on the same approved presentation, mount and package can resolve only this setup category; source mutation is not required. " +
              "A testless, static-only, limited or uncertain observation cannot establish editable persistence. "
            : "";
        var invokeSetupAdvice = state.NativeControlFailures.Any(item => item.InvokeFieldLineSetup is not null)
            ? "\nAn invoke marked incorrect multiline assertion setup declared separate lines inside one complete visible field. " +
              "Use that retained receipt's exact whole after-field as one expectedTexts value and keep every originally valid whole-field or label expectation. " +
              "A later fully passed declared invocation on the same approved presentation, mount, button, source and package can resolve only this setup category; source mutation is not required. "
            : "";
        var failureAdvice = state.NativeControlFailures.Any(item => !item.MissingBeforeExpectations && item.InvokeFieldLineSetup is null)
            ? "\nA failed declared assertion does not by itself prove a plugin defect. Compare its expected values with the complete actual before/after observation and the original accepted behavior criteria before changing source. " +
              "For each transformation, establish fresh input different from its intended after-value; after a reset, re-establish that test input before repeating the transformation. " +
              "If the declaration was mistaken, describe the criterion-based correction and retained failed receipt; do not claim the failure is resolved by a different passing expectation. " +
              "An unrelated success or a weaker/different declaration does not resolve retained failures. Repeat their same declared expectations after a proved source repair. " +
              "When observed behavior violates the original criteria, repair the existing requested source, build and verify a replacement with a different fingerprint, and present it for owner review. Changed bytes do not prove correctness. " +
              "If sound expectations or a source defect cannot be established, use blocked and state the exact remaining evidence gap. "
            : "";
        return Repair(input, state with { NativeControlFinalCorrections = state.NativeControlFinalCorrections + 1 },
            "A successful final is not accepted while these native plugin control failures remain unresolved:\n" + facts +
            setupAdvice + invokeSetupAdvice + failureAdvice + "If testing or repair cannot proceed, use blocked and state the actual limitation.");
    }

    private static bool ReplacementControlMatches(HarnessState state, NativeControlFailure failure,
        string presentation, string? sourceManifest, string? packageFingerprint) =>
        state.PluginWork is { } work && work.PresentRequestId == presentation &&
        sourceManifest is not null && PathEquals(sourceManifest, work.Build.BuiltManifest) &&
        packageFingerprint is not null &&
        string.Equals(packageFingerprint, work.PresentedPackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
        VerifiedReplacementAfter(state, work, failure);

    private static bool SameRetainedControlPackage(NativeControlFailure failure,
        string? sourceManifest, string? packageFingerprint,
        bool sourceManifestAbsent, bool packageFingerprintAbsent) =>
        !failure.PackageIdentityIncomplete &&
        ((failure.SourceManifest is null && failure.PackageFingerprint is null &&
         sourceManifestAbsent && packageFingerprintAbsent) ||
        (failure.SourceManifest is not null && sourceManifest is not null &&
         PathEquals(failure.SourceManifest, sourceManifest) && ValidPackageFingerprint(failure.PackageFingerprint) &&
         packageFingerprint is not null && ValidPackageFingerprint(packageFingerprint) &&
         string.Equals(failure.PackageFingerprint, packageFingerprint, StringComparison.OrdinalIgnoreCase)));

    private static bool VerifiedReplacementAfter(HarnessState state, PluginWorkBinding work, NativeControlFailure failure) =>
        DifferentReplacementAwaitingOwnerReview(state, work, failure) &&
        ReceiptAfter(state, work.BuildEffectId!, failure.EffectId);

    private static bool ReplacementAwaitingOwnerReview(Input input, HarnessState state)
    {
        if (state.PluginWork is not { } work) return false;
        if (input.AutomaticPluginTestPresentationId is not { } presentation)
            return state.NativeControlFailures.Count != 0 && state.NativeControlFailures.All(failure =>
                DifferentReplacementAwaitingOwnerReview(state, work, failure));
        if (!CanonicalReplacementAwaitingOwnerReview(state, work, presentation)) return false;

        // Queueing any new package cannot discharge installed QA. Retained native evidence must
        // identify this exact reviewed source and bytes, and the repair must follow that evidence.
        var observations = state.NativeControlCoverage.Where(item => item.PresentationId == presentation &&
            input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal)).ToArray();
        var failures = state.NativeControlFailures;
        if (failures.Any(failure => failure.PresentationId != presentation || !failure.CompleteObservation ||
            !input.HostPluginTestMountIds.Contains(failure.MountId, StringComparer.Ordinal) ||
            !VerifiedReplacementAfter(state, work, failure))) return false;
        if (observations.Length != 0 || failures.Count != 0)
            return observations.All(item => ReviewedPackageReplacedAfter(state, work,
                item.SourceManifest, item.PackageFingerprint, item.EffectId));

        // A failed pre-install review has no native controls. Its separate, provider-proved
        // repair intent identifies source bytes; an absent QA mount list alone proves nothing.
        return state.NativeReviewFailure is { } review && review.PresentationId == presentation &&
            ReviewedPackageReplacedAfter(state, work, review.SourceManifest, review.PackageFingerprint, review.EffectId);
    }

    private static bool ReviewedPackageReplacedAfter(HarnessState state, PluginWorkBinding work,
        string sourceManifest, string packageFingerprint, string effectId) =>
        PathEquals(sourceManifest, work.Build.BuiltManifest) &&
        !string.Equals(packageFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
        work.BuildEffectId is not null && ReceiptAfter(state, work.BuildEffectId, effectId);

    private static bool CompleteNativePackageObservation(WorkflowEffect effect, ToolData data, JsonElement body, string mount) =>
        data.Evidence?.Partial is not true &&
        (effect.State, data.Evidence?.Outcome) is
            (WorkflowEffectState.Succeeded, ToolExecutionOutcome.Succeeded) or
            (WorkflowEffectState.Failed, ToolExecutionOutcome.Failed) &&
        TryText(body, "nativeStatus", 32, out var native) && native == "completed" &&
        TryCompleteControlObservation(body, mount, out _);

    private static bool CanonicalReplacementAwaitingOwnerReview(HarnessState state, PluginWorkBinding work,
        string exactOldPresentation) =>
        work.SourceGeneration > 0 && work.SuccessfulBuildGeneration == work.SourceGeneration &&
        work.VerifiedGeneration == work.SourceGeneration && work.PresentedGeneration == work.SourceGeneration &&
        work.BuildEffectId is not null && work.VerifyEffectId is not null && work.PresentEffectId is not null &&
        work.PresentRequestId is not null && work.PresentRequestId != exactOldPresentation &&
        ValidPackageFingerprint(work.VerifiedPackageFingerprint) && ValidPackageFingerprint(work.PresentedPackageFingerprint) &&
        string.Equals(work.VerifiedPackageFingerprint, work.PresentedPackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
        ReceiptAfter(state, work.VerifyEffectId, work.BuildEffectId) &&
        ReceiptAfter(state, work.PresentEffectId, work.VerifyEffectId);

    private static bool DifferentReplacementAwaitingOwnerReview(HarnessState state, PluginWorkBinding work,
        NativeControlFailure failure) =>
        work.SourceGeneration > 0 && work.SuccessfulBuildGeneration == work.SourceGeneration &&
        work.VerifiedGeneration == work.SourceGeneration && work.PresentedGeneration == work.SourceGeneration &&
        work.BuildEffectId is not null && work.VerifyEffectId is not null && work.PresentEffectId is not null &&
        work.PresentRequestId is not null && failure.PresentationId != work.PresentRequestId &&
        failure.SourceManifest is not null && PathEquals(failure.SourceManifest, work.Build.BuiltManifest) &&
        ValidPackageFingerprint(failure.PackageFingerprint) && ValidPackageFingerprint(work.VerifiedPackageFingerprint) &&
        ValidPackageFingerprint(work.PresentedPackageFingerprint) &&
        string.Equals(work.VerifiedPackageFingerprint, work.PresentedPackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(failure.PackageFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase) &&
        ReceiptAfter(state, work.VerifyEffectId, work.BuildEffectId) &&
        ReceiptAfter(state, work.PresentEffectId, work.VerifyEffectId);

    private static string[] DeferredSuccessfulTools(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is null ||
            !input.HostAddedRequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) ||
            state.CompletionMilestones.ContainsKey("plugin.controls") ||
            !ReplacementAwaitingOwnerReview(input, state)) return [];
        return ["plugin.controls"];
    }

    private static object? ReplacementAwaitingOwnerReviewProjection(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is null ||
            !ReplacementAwaitingOwnerReview(input, state) ||
            state.PluginWork is not { } work) return null;
        return new
        {
            state = "pending-owner-review",
            requestId = work.PresentRequestId,
            packageFingerprint = work.PresentedPackageFingerprint,
            approved = false,
            installed = false,
            functionallyTested = false,
            reviewedPresentationId = input.AutomaticPluginTestPresentationId,
            note = "Prior review evidence does not test this queued package. A failed review is not an installed approval or presentation."
        };
    }

    private static bool ReceiptAfter(HarnessState state, string later, string earlier)
    {
        var receipts = state.ToolReceipts.ToArray();
        var first = Array.FindIndex(receipts, item => item.EffectId == earlier);
        return first >= 0 && Array.FindIndex(receipts, item => item.EffectId == later) > first;
    }

    private static string[]? ReadControlExpectations(JsonElement arguments, string operation) =>
        PluginControlAssertions.CopyExpectedTexts(operation, arguments.TryGetProperty("expectedTexts", out var value)
            ? value.Deserialize<string[]>(Json) ?? throw new ArgumentException("Control expectations cannot be null.") : null);

    private static bool ValidControlExpectations(string operation, IReadOnlyList<string>? expected)
    {
        try { PluginControlAssertions.CopyExpectedTexts(operation, expected); return true; }
        catch (ArgumentException) { return false; }
    }

    private static void ValidateNativeControlFailures(HarnessState state)
    {
        ValidateNativeReviewFailure(state);
        if (state.NativeControlFinalCorrections is < 0 or > MaximumRepairs || state.NativeControlFailures is null || state.NativeControlFailures.Count > MaximumControlFailures ||
            state.NativeControlFailures.Any(item => item is null ||
                !Guid.TryParseExact(item.PresentationId, "N", out _) ||
                item.MountId is not { Length: > 0 and <= 129 } || item.MountId.Any(char.IsControl) ||
                item.Operation is not ("invoke" or "reopen" or "setText") ||
                item.Operation == "invoke" && (item.ButtonName is not { Length: > 0 and <= 256 } || item.ButtonName.Any(char.IsControl)) ||
                item.Operation is ("reopen" or "setText") && item.ButtonName is not null ||
            !ValidRetainedControlEdit(item) || !ValidControlExpectations(item.Operation, item.ExpectedTexts) || !ValidInvokeFieldLineSetup(state, item) ||
                !ValidEffectId(item.EffectId) || item.Diagnostic is null || item.Diagnostic.Length > 512 ||
                item.SourceManifest is not null && !CanonicalStoredPath(item.SourceManifest) ||
                item.PackageFingerprint is not null && !ValidPackageFingerprint(item.PackageFingerprint) ||
                item.PackageIdentityIncomplete && item.SourceManifest is not null && item.PackageFingerprint is not null ||
                item.CompleteObservation && (item.SourceManifest is null || item.PackageFingerprint is null) ||
                item.MissingBeforeExpectations && (!item.CompleteObservation || item.Operation != "reopen" ||
                    item.ExpectedTexts is not { Count: > 0 }) ||
                item.SetupHadEditableFields && !item.MissingBeforeExpectations) ||
            state.NativeControlFailures.Select(item => (item.PresentationId, item.MountId, item.Operation, item.ButtonName,
                item.FieldId, item.ExpectedTextSha256, item.TextSha256, item.ExpectedTextUtf8Bytes, item.TextUtf8Bytes,
                item.ExpectedTexts is null ? null : string.Join("\0", item.ExpectedTexts))).Distinct().Count() !=
                state.NativeControlFailures.Count)
            throw new ArgumentException("The retained native-control failures are invalid.");
    }
}
