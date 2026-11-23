using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private sealed record NativeControlButtonCoverage(string Name, string ObservedEffectId, string? InvokedEffectId = null);
    private sealed record NativeControlCoverage(string PresentationId, string MountId, string SourceManifest,
        string PackageFingerprint, bool HasInteractiveControls, bool InteractionObserved, string EffectId,
        string? InteractionEffectId = null, IReadOnlyList<NativeControlButtonCoverage>? Buttons = null,
        bool ButtonNamesAmbiguous = false, bool ButtonNamesLimited = false, string? IdentityConflictEffectId = null,
        bool LegacyInteractiveButtonCoverageIncomplete = false);

    private static HarnessState AdoptNativeControlCoverage(HarnessState state, WorkflowEffect effect, ToolData data,
        JsonElement body, string presentation, string mount, string operation, PluginControlTextEdit? edit)
    {
        if (!SuccessfulEffect(effect, data) || !TryText(body, "status", 32, out var status) || status != "completed" ||
            !TryText(body, "nativeStatus", 32, out var native) || native != "completed" ||
            !TryText(body, "sourceManifest", MaximumBindingPathCharacters, out var source) ||
            !TryCanonicalAbsolutePath(source, out var canonicalSource) ||
            !TryText(body, "packageFingerprint", 64, out var fingerprint) || !ValidPackageFingerprint(fingerprint)) return state;
        var presentationCoverage = state.NativeControlCoverage.Where(item => item.PresentationId == presentation).ToArray();
        // A successful provider-correlated receipt identifies immutable package bytes even when
        // its native snapshot is limited. Such a conflict must invalidate older coverage before
        // the complete-observation gate can preserve it.
        if (presentationCoverage.Any(item => !PathEquals(item.SourceManifest, canonicalSource) ||
            !string.Equals(item.PackageFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
            return state with { NativeControlCoverage = state.NativeControlCoverage.Select(item => item.PresentationId == presentation
                ? item with { IdentityConflictEffectId = effect.Id } : item).ToArray() };
        if (!TryCompleteControlObservation(body, mount, out var observation)) return state;
        var interactive = Actionable(observation.Before) || Actionable(observation.After!);
        string? invokedButton = null;
        var delivered = operation == "setText" && CompletedControlTextEdit(body, mount, edit);
        if (operation == "invoke" && observation.ClickDelivered && !observation.TextSet &&
            PendingToolArguments(state) is { } arguments && TryText(arguments, "buttonName", 256, out var button) &&
            observation.Before.Buttons.Where(item => item.Name == button).ToArray() is [var exactButton] &&
            exactButton.Enabled && exactButton.Visible)
        {
            delivered = true;
            invokedButton = button;
        }
        var prior = presentationCoverage.FirstOrDefault(item => item.MountId == mount);
        var remaining = state.NativeControlCoverage.Where(item => item.PresentationId != presentation || item.MountId != mount).ToArray();
        if (remaining.Length >= WorkflowContract.MaximumPluginTestMounts)
            throw new InvalidOperationException("The bounded native-control observation limit was reached.");
        var observedButtons = observation.Before.Buttons.Concat(observation.After!.Buttons)
            .Where(item => item.Enabled && item.Visible).Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
        var ambiguous = prior?.ButtonNamesAmbiguous == true || AmbiguousButtons(observation.Before) || AmbiguousButtons(observation.After);
        // An older interactive record did not retain the full button inventory. A later subset
        // cannot prove which controls existed earlier, so that uncertainty remains sticky.
        var legacyInteractiveIncomplete = prior?.LegacyInteractiveButtonCoverageIncomplete == true ||
            prior is { Buttons: null, HasInteractiveControls: true };
        var retainedButtons = prior?.Buttons?.ToList() ?? [];
        var retainedButtonCount = remaining.Sum(item => item.Buttons?.Count ?? 0);
        var limited = prior?.ButtonNamesLimited == true;
        foreach (var name in observedButtons)
        {
            var index = retainedButtons.FindIndex(item => item.Name == name);
            if (index >= 0)
            {
                if (invokedButton == name && delivered && retainedButtons[index].InvokedEffectId is null)
                    retainedButtons[index] = retainedButtons[index] with { InvokedEffectId = effect.Id };
                continue;
            }
            if (retainedButtonCount + retainedButtons.Count >= WorkflowContract.MaximumPluginTestMounts)
            {
                limited = true;
                continue;
            }
            retainedButtons.Add(new(name, effect.Id, invokedButton == name && delivered ? effect.Id : null));
        }
        var priorButtons = prior?.Buttons;
        var coverageProgress = prior is null || priorButtons is null && !legacyInteractiveIncomplete ||
            interactive && !prior.HasInteractiveControls || delivered && !prior.InteractionObserved ||
            priorButtons is not null && observedButtons.Any(name => priorButtons.All(button => button.Name != name)) ||
            priorButtons is not null && invokedButton is not null && delivered &&
            priorButtons.Any(button => button.Name == invokedButton && button.InvokedEffectId is null);
        var updated = new NativeControlCoverage(presentation, mount, canonicalSource,
            fingerprint.ToLowerInvariant(), interactive || prior?.HasInteractiveControls == true,
            delivered || prior?.InteractionObserved == true, effect.Id, delivered ? effect.Id : prior?.InteractionEffectId,
            retainedButtons, ambiguous, limited, prior?.IdentityConflictEffectId, legacyInteractiveIncomplete);
        var conclusiveProgress = coverageProgress && !updated.ButtonNamesAmbiguous && !updated.ButtonNamesLimited &&
            updated.IdentityConflictEffectId is null && !updated.LegacyInteractiveButtonCoverageIncomplete;
        return state with { NativeControlCoverage = [.. remaining, updated],
            NativeControlFinalCorrections = conclusiveProgress && state.NativeControlFailures.Count == 0
                ? 0 : state.NativeControlFinalCorrections };

        static bool Actionable(PluginControlSnapshot snapshot) =>
            snapshot.Buttons.Any(item => item.Enabled && item.Visible) ||
            snapshot.Fields.Any(item => item.Enabled && item.Visible && !item.ReadOnly);
        static bool AmbiguousButtons(PluginControlSnapshot snapshot) => snapshot.Buttons
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Any(group => group.Count() > 1 && group.Any(item => item.Enabled && item.Visible));
    }

    private static WorkflowStep? RequireNativeControlCoverage(Input input, HarnessState state)
    {
        // Set only by the validated service continuation and exact plugin-tests-{approval}
        // run binding, never by model text or a tool-name obligation.
        if (input.AutomaticPluginTestPresentationId is not { } presentation ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal)) return null;
        var approved = input.HostPluginTestMountIds;
        var observed = state.NativeControlCoverage.Where(item => item.PresentationId == presentation &&
            approved.Contains(item.MountId, StringComparer.Ordinal)).ToArray();
        var unobserved = approved.Where(mount => !observed.Any(item => item.MountId == mount)).ToArray();
        var legacy = observed.Where(item => item.Buttons is null).ToArray();
        var inconclusive = observed.Where(item => item.ButtonNamesAmbiguous || item.ButtonNamesLimited ||
            item.IdentityConflictEffectId is not null || item.LegacyInteractiveButtonCoverageIncomplete).ToArray();
        var missingButtons = observed.SelectMany(item => (item.Buttons ?? []).Where(button => button.InvokedEffectId is null)
            .Select(button => (item.MountId, button.Name))).ToArray();
        var missingInteraction = observed.Where(item => item.HasInteractiveControls && !item.InteractionObserved).ToArray();
        if (approved.Count != 0 && unobserved.Length == 0 && legacy.Length == 0 && inconclusive.Length == 0 &&
            missingButtons.Length == 0 && missingInteraction.Length == 0) return null;
        var diagnostics = new List<string>();
        if (approved.Count == 0)
            diagnostics.Add("The frozen host-approved mount list is unavailable; inspection cannot establish complete reviewed-view coverage.");
        if (unobserved.Length != 0)
            diagnostics.Add("No complete current-package native observation was retained for approved mounts: " + DescribeMounts(unobserved) + ".");
        if (legacy.Length != 0)
            diagnostics.Add("Legacy coverage has no per-button inventory; obtain a fresh complete observation for: " + DescribeMounts(legacy.Select(item => item.MountId)) + ".");
        if (inconclusive.Length != 0)
            diagnostics.Add("Per-button coverage is inconclusive because package identity conflicted, enabled button names were ambiguous, the bounded ledger was exceeded, or an older interactive record lacked its full button inventory for: " + DescribeMounts(inconclusive.Select(item => item.MountId)) + ".");
        if (missingButtons.Length != 0)
            diagnostics.Add("No delivered invocation was retained for observed enabled buttons: " + DescribeButtons(missingButtons) + ".");
        if (missingInteraction.Length != 0)
            diagnostics.Add("Inspection alone did not exercise the interactive reviewed mounts: " + DescribeMounts(missingInteraction.Select(item => item.MountId)) + ".");
        var diagnostic = string.Join(" ", diagnostics);
        if (state.NativeControlFinalCorrections >= MaximumRepairs)
            return Finish(state, false, "Automatic plugin QA lacks actual interaction evidence after two completion corrections. " + diagnostic);
        return Repair(input, state with { NativeControlFinalCorrections = state.NativeControlFinalCorrections + 1 },
            diagnostic + " Inspect the exact reviewed mount and exercise each unambiguous enabled visible button through its exact invoke coordinate. " +
            "For a field-only interactive mount, use an exact setText precondition and postcondition. " +
            "This button ledger proves delivery only; verify relevant original-request inputs and boundaries with actual assertions, and never claim unobserved paths. " +
            "If the installed view cannot perform the requested behavior, repair its existing source, rebuild, verify and present a replacement for separate owner review. " +
            "Do not invoke an unsafe or unavailable control merely to satisfy coverage, repeat an already covered button, or claim a functional pass from delivery alone. " +
            "Use blocked with the exact control and reason for an actual safety, availability or ambiguity limitation.");
    }

    private static string DescribeMounts(IEnumerable<string> mounts)
    {
        var values = mounts.ToArray();
        return string.Join(", ", values.Take(8)) + (values.Length > 8 ? $"; {values.Length - 8} further mounts" : "");
    }

    private static string DescribeButtons(IEnumerable<(string MountId, string Name)> buttons)
    {
        var values = buttons.Select(item => item.MountId + " / " + item.Name).ToArray();
        return string.Join(", ", values.Take(8)) + (values.Length > 8 ? $"; {values.Length - 8} further buttons" : "");
    }

    private static bool TryCompleteControlObservation(JsonElement body, string mount, out PluginControlObservation observation)
    {
        observation = null!;
        if (!body.TryGetProperty("observation", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        try
        {
            var parsed = value.Deserialize<PluginControlObservation>(Json);
            if (parsed is null || !CompleteControlSnapshot(parsed.Before, mount) || !CompleteControlSnapshot(parsed.After, mount)) return false;
            observation = parsed; return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool CompleteControlSnapshot(PluginControlSnapshot? snapshot, string mount) =>
            snapshot is not null && snapshot.Id == mount && !snapshot.Limited &&
            snapshot.Texts is { Count: <= 20 } && snapshot.Texts.All(item => item is { Length: <= 2048 } && !item.Contains('\0')) &&
            snapshot.Texts.Sum(item => item.Length) <= 4096 &&
            snapshot.Buttons is { Count: <= 20 } && snapshot.Buttons.All(item =>
                item is not null && item.Name is { Length: > 0 and <= 256 } && !item.Name.Any(char.IsControl)) &&
            snapshot.Fields is { Count: <= 8 } && snapshot.Fields.All(item =>
                item is not null && Guid.TryParseExact(item.Id, "N", out var field) && field != Guid.Empty &&
                !item.Limited && item.Text is { Length: <= PluginControlTextEdits.MaximumCharacters } &&
                !item.Text.Contains('\0') && Encoding.UTF8.GetByteCount(item.Text) <= PluginControlTextEdits.MaximumUtf8Bytes) &&
            snapshot.Fields.Sum(item => item.Text.Length) <= PluginControlTextEdits.MaximumCharacters;

    private static bool NoEffectStaleControlTextPrecondition(WorkflowEffect effect, ToolData data,
        JsonElement body, string mount, PluginControlTextEdit? edit)
    {
        if (edit is null || data.Name != "plugin.controls" || effect.State != WorkflowEffectState.Failed ||
            data.Evidence is not { ProviderId: "cerebral.plugin.controls", Outcome: ToolExecutionOutcome.Rejected, Partial: not true } ||
            data.Truncated || !TryText(body, "nativeStatus", 32, out var native) || native != "rejected" ||
            !TryText(body, "status", 32, out var status) || status != "rejected" ||
            !body.TryGetProperty("observation", out var value) || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("clickDelivered", out var click) || click.ValueKind != JsonValueKind.False ||
            !value.TryGetProperty("textSet", out var set) || set.ValueKind != JsonValueKind.False ||
            !value.TryGetProperty("instanceRecreated", out var recreated) || recreated.ValueKind != JsonValueKind.False) return false;
        try
        {
            var observed = value.Deserialize<PluginControlObservation>(Json);
            if (observed is null || !CompleteControlSnapshot(observed.Before, mount) || observed.After is not null ||
                observed.TextSet || observed.ClickDelivered || observed.InstanceRecreated) return false;
            var fields = observed.Before.Fields.Where(field => field.Id == edit.FieldId).ToArray();
            return fields.Length == 1 && fields[0].Enabled && fields[0].Visible && !fields[0].ReadOnly &&
                !string.Equals(fields[0].Text, edit.ExpectedText, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static bool CompletedControlTextEdit(JsonElement body, string mount, PluginControlTextEdit? edit)
    {
        if (edit is null || !TryCompleteControlObservation(body, mount, out var observed) ||
            !observed.TextSet || observed.ClickDelivered) return false;
        var before = observed.Before.Fields.Where(item => item.Id == edit.FieldId).ToArray();
        var after = observed.After!.Fields.Where(item => item.Id == edit.FieldId).ToArray();
        return before.Length == 1 && after.Length == 1 && before[0].Enabled && before[0].Visible && !before[0].ReadOnly &&
            string.Equals(before[0].Text, edit.ExpectedText, StringComparison.Ordinal) &&
            string.Equals(after[0].Text, edit.Text, StringComparison.Ordinal);
    }

    private static PluginControlTextEdit? ReadControlTextEdit(JsonElement arguments, string operation) =>
        PluginControlTextEdits.Copy(operation, TextFrom(arguments, "fieldId"), TextFrom(arguments, "expectedText"), TextFrom(arguments, "text"));
    private static string? TextFrom(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string ControlTextHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool SameControlEdit(NativeControlFailure failure, PluginControlTextEdit? edit, string? expectedHash, string? replacementHash) =>
        failure.FieldId == edit?.FieldId && failure.ExpectedTextSha256 == expectedHash && failure.TextSha256 == replacementHash &&
        failure.ExpectedTextUtf8Bytes == (edit is null ? null : Encoding.UTF8.GetByteCount(edit.ExpectedText)) &&
        failure.TextUtf8Bytes == (edit is null ? null : Encoding.UTF8.GetByteCount(edit.Text));
    private static bool ValidRetainedControlEdit(NativeControlFailure failure) =>
        failure.Operation == "setText"
            ? Guid.TryParseExact(failure.FieldId, "N", out var field) && field != Guid.Empty &&
              failure.ExpectedTextSha256 is not null && ValidPackageFingerprint(failure.ExpectedTextSha256) &&
              failure.TextSha256 is not null && ValidPackageFingerprint(failure.TextSha256) &&
              failure.ExpectedTextSha256 != failure.TextSha256 &&
              failure.ExpectedTextUtf8Bytes is >= 0 and <= PluginControlTextEdits.MaximumUtf8Bytes &&
              failure.TextUtf8Bytes is >= 0 and <= PluginControlTextEdits.MaximumUtf8Bytes
            : failure.FieldId is null && failure.ExpectedTextSha256 is null && failure.TextSha256 is null &&
              failure.ExpectedTextUtf8Bytes is null && failure.TextUtf8Bytes is null;
    private static void ValidateNativeControlCoverage(HarnessState state)
    {
        if (state.NativeControlCoverage is null || state.NativeControlCoverage.Count > WorkflowContract.MaximumPluginTestMounts ||
            state.NativeControlCoverage.Any(item => item is null ||
                !Guid.TryParseExact(item.PresentationId, "N", out var presentation) || presentation == Guid.Empty ||
                item.MountId is not { Length: > 0 and <= 129 } || item.MountId.Any(char.IsControl) ||
                !CanonicalStoredPath(item.SourceManifest) || !ValidPackageFingerprint(item.PackageFingerprint) || !ValidEffectId(item.EffectId) ||
                item.InteractionObserved != (item.InteractionEffectId is not null) ||
                item.InteractionEffectId is not null && !ValidEffectId(item.InteractionEffectId) ||
                item.IdentityConflictEffectId is not null && !ValidEffectId(item.IdentityConflictEffectId) ||
                item.LegacyInteractiveButtonCoverageIncomplete && !item.HasInteractiveControls ||
                item.Buttons is null && (item.ButtonNamesAmbiguous || item.ButtonNamesLimited) ||
                item.Buttons is not null && (item.Buttons.Count > WorkflowContract.MaximumPluginTestMounts ||
                    item.Buttons.Any(button => button is null || button.Name is not { Length: > 0 and <= 256 } ||
                        button.Name.Any(char.IsControl) || !ValidEffectId(button.ObservedEffectId) ||
                        button.InvokedEffectId is not null && !ValidEffectId(button.InvokedEffectId)) ||
                    item.Buttons.Select(button => button.Name).Distinct(StringComparer.Ordinal).Count() != item.Buttons.Count ||
                    item.Buttons.Any(button => button.InvokedEffectId is not null) && !item.InteractionObserved)) ||
            state.NativeControlCoverage.Sum(item => item.Buttons?.Count ?? 0) > WorkflowContract.MaximumPluginTestMounts ||
            state.NativeControlCoverage.Select(item => (item.PresentationId, item.MountId)).Distinct().Count() != state.NativeControlCoverage.Count)
            throw new ArgumentException("The retained native-control coverage is invalid.");
    }
}
