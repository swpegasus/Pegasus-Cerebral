using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // The immutable effect retains the actual field. Recovery needs only its bounded identity
    // and the original expectation indexes that were lines rather than whole control values.
    private sealed record InvokeFieldLineSetup(string WholeFieldSha256, int WholeFieldCharacters,
        int WholeFieldUtf8Bytes, IReadOnlyList<int> MissingExpectationIndexes);

    private static InvokeFieldLineSetup? DeriveInvokeFieldLineSetup(HarnessState state, WorkflowEffect effect, ToolData data,
        JsonElement body, string presentation, string mount, string? button, IReadOnlyList<string>? expected)
    {
        if (expected is null || button is null ||
            state.NativeControlFailures.Any(item => item.InvokeFieldLineSetup is null &&
                item.PresentationId == presentation && item.MountId == mount && item.Operation == "invoke" &&
                item.ButtonName == button && PluginControlAssertions.SameExpectedTexts(item.ExpectedTexts, expected)) ||
            !CompleteAssertedInvoke(effect, data, body, mount, button, failed: true, out var observed) ||
            !body.TryGetProperty("assertion", out var assertion)) return null;
        var visible = ExactControlTexts(observed.After!).ToHashSet(StringComparer.Ordinal);
        var missingIndexes = Enumerable.Range(0, expected.Count).Where(index => !visible.Contains(expected[index])).ToArray();
        if (missingIndexes.Length == 0 || missingIndexes.Any(index => expected[index].IndexOfAny(['\r', '\n']) >= 0) ||
            !assertion.TryGetProperty("missingTexts", out var missingValue)) return null;
        var returnedMissing = PluginControlAssertions.CopyExpectedTexts("invoke", missingValue.Deserialize<string[]>(Json));
        if (returnedMissing is null ||
            !returnedMissing.SequenceEqual(missingIndexes.Select(index => expected[index]), StringComparer.Ordinal)) return null;

        foreach (var field in observed.After!.Fields.Where(field => field.Visible && !field.Limited &&
                     field.Text.IndexOfAny(['\r', '\n']) >= 0))
        {
            // A corrected declaration must fit the existing assertion contract together with
            // every originally valid whole field/label value. Oversized output is no setup proof.
            var corrected = expected.Where((_, index) => !missingIndexes.Contains(index)).Append(field.Text).ToArray();
            if (!ValidControlExpectations("invoke", corrected)) continue;
            var lines = field.Text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToHashSet(StringComparer.Ordinal);
            if (missingIndexes.All(index => lines.Contains(expected[index])))
                return new(ControlTextHash(field.Text), field.Text.Length, Encoding.UTF8.GetByteCount(field.Text), missingIndexes);
        }
        return null;
    }

    private static bool CompleteAssertedInvoke(WorkflowEffect effect, ToolData data, JsonElement body,
        string mount, string button, bool failed, out PluginControlObservation observed)
    {
        observed = null!;
        if (data.Name != "plugin.controls" || data.Evidence?.ProviderId != "cerebral.plugin.controls" ||
            data.Truncated || data.Evidence.Partial is true ||
            !TryText(body, "receipt", 256, out var receipt) || receipt != data.Evidence.RequestId ||
            (effect.State, data.Evidence.Outcome) != (failed
                ? (WorkflowEffectState.Failed, ToolExecutionOutcome.Failed)
                : (WorkflowEffectState.Succeeded, ToolExecutionOutcome.Succeeded)) ||
            !TryText(body, "status", 32, out var status) || status != (failed ? "failed" : "completed") ||
            !TryText(body, "nativeStatus", 32, out var native) || native != "completed" ||
            !body.TryGetProperty("assertion", out var assertion) || !CurrentNativeAssertionPolicy(assertion) ||
            !TryText(assertion, "status", 32, out var assertionStatus) || assertionStatus != (failed ? "failed" : "passed") ||
            !TryCompleteControlObservation(body, mount, out observed) ||
            !observed.ClickDelivered || observed.TextSet || observed.InstanceRecreated ||
            observed.Before.Buttons.Count(item => item.Name == button) != 1 ||
            !observed.Before.Buttons.Any(item => item.Name == button && item.Enabled && item.Visible))
            return false;
        return true;
    }

    private static bool CorrectedInvokeFieldLineSetup(HarnessState state, NativeControlFailure failure,
        WorkflowEffect effect, ToolData data, JsonElement body, string presentation, string mount,
        string operation, string? button, IReadOnlyList<string>? expected, string? source, string? fingerprint)
    {
        if (failure.InvokeFieldLineSetup is not { } setup || operation != "invoke" || expected is null || button is null ||
            failure.PresentationId != presentation || failure.MountId != mount || failure.ButtonName != button ||
            source is null || failure.SourceManifest is null || !PathEquals(source, failure.SourceManifest) ||
            fingerprint is null || !string.Equals(fingerprint, failure.PackageFingerprint, StringComparison.OrdinalIgnoreCase) ||
            effect.Id == failure.EffectId || state.ToolReceipts.Any(item => item.EffectId == effect.Id) ||
            !FailedInvokeSetupReceipt(state, failure) ||
            !CompleteAssertedInvoke(effect, data, body, mount, button, failed: false, out var observed) ||
            expected.Any(value => !ExactControlTexts(observed.After!).Contains(value, StringComparer.Ordinal)) ||
            failure.ExpectedTexts!.Where((_, index) => !setup.MissingExpectationIndexes.Contains(index))
                .Any(value => !expected.Contains(value, StringComparer.Ordinal))) return false;

        return expected.Any(value => value.Length == setup.WholeFieldCharacters &&
            Encoding.UTF8.GetByteCount(value) == setup.WholeFieldUtf8Bytes &&
            ControlTextHash(value) == setup.WholeFieldSha256 &&
            observed.After!.Fields.Any(field => field.Visible && !field.Limited &&
                string.Equals(field.Text, value, StringComparison.Ordinal)));
    }

    private static bool FailedInvokeSetupReceipt(HarnessState state, NativeControlFailure failure)
    {
        var matches = state.ToolReceipts.Where(item => item.EffectId == failure.EffectId).ToArray();
        return matches.Length == 1 && matches[0] is
            { Name: "plugin.controls", State: "Failed", Outcome: "Failed", Truncated: false };
    }

    private static bool ValidInvokeFieldLineSetup(HarnessState state, NativeControlFailure failure)
    {
        if (failure.InvokeFieldLineSetup is not { } setup) return true; // Legacy failures retain strict matching.
        if (failure.Operation != "invoke" || failure.MissingBeforeExpectations || failure.SetupHadEditableFields ||
            !failure.CompleteObservation || failure.ExpectedTexts is not { Count: > 0 } expected ||
            !ValidPackageFingerprint(setup.WholeFieldSha256) ||
            setup.WholeFieldSha256 != setup.WholeFieldSha256.ToLowerInvariant() ||
            setup.WholeFieldCharacters is < 2 or > 256 ||
            setup.WholeFieldUtf8Bytes < setup.WholeFieldCharacters ||
            setup.WholeFieldUtf8Bytes > setup.WholeFieldCharacters * 3 ||
            setup.MissingExpectationIndexes is not { Count: > 0 and <= 8 } indexes ||
            indexes.Any(index => index < 0 || index >= expected.Count || expected[index].IndexOfAny(['\r', '\n']) >= 0) ||
            !indexes.SequenceEqual(indexes.Distinct().Order()) ||
            expected.Count - indexes.Count + 1 > 8 ||
            expected.Where((_, index) => !indexes.Contains(index)).Sum(Encoding.UTF8.GetByteCount) +
                setup.WholeFieldUtf8Bytes > 2048 ||
            !FailedInvokeSetupReceipt(state, failure)) return false;
        return true;
    }
}
