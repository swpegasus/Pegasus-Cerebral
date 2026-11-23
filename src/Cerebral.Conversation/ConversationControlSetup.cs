using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Missing before-values invalidate the declared test setup. Complete native execution
    // still does not establish either a plugin defect or a persistence pass.
    private static bool MissingReopenPrecondition(WorkflowEffect effect, ToolData data, JsonElement body,
        string mount, IReadOnlyList<string>? expected)
    {
        if (expected is null || effect.State != WorkflowEffectState.Failed || data.Evidence?.Outcome != ToolExecutionOutcome.Failed ||
            !CompleteNativePackageObservation(effect, data, body, mount) ||
            !TryCompleteControlObservation(body, mount, out var observed) || !observed.InstanceRecreated ||
            !body.TryGetProperty("assertion", out var assertion) ||
            !TryText(body, "status", 32, out var status) || status != "uncertain" ||
            !TryText(assertion, "status", 32, out var assertionStatus) || assertionStatus != "inconclusive" ||
            !CurrentNativeAssertionPolicy(assertion)) return false;
        var missing = expected.Where(value => !ExactControlTexts(observed.Before)
            .Contains(value, StringComparer.Ordinal)).ToArray();
        if (missing.Length == 0 || !assertion.TryGetProperty("missingTexts", out var missingValue)) return false;
        try
        {
            var returned = PluginControlAssertions.CopyExpectedTexts("reopen", missingValue.Deserialize<string[]>(Json));
            return returned is not null && missing.SequenceEqual(returned, StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool CorrectedReopenSetup(NativeControlFailure failure, string presentation, string mount,
        string operation, JsonElement body, IReadOnlyList<string>? expected, string? source, string? fingerprint)
    {
        if (!failure.MissingBeforeExpectations || operation != "reopen" || failure.Operation != operation ||
            failure.PresentationId != presentation || failure.MountId != mount || expected is null ||
            source is null || failure.SourceManifest is null || !PathEquals(source, failure.SourceManifest) ||
            fingerprint is null || !string.Equals(fingerprint, failure.PackageFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !TryCompleteControlObservation(body, mount, out var observed) || !observed.InstanceRecreated ||
            !TryText(body, "nativeStatus", 32, out var native) || native != "completed" ||
            !body.TryGetProperty("assertion", out var assertion) || !CurrentNativeAssertionPolicy(assertion) ||
            !TryText(assertion, "status", 32, out var status) || status != "passed" ||
            expected.Any(value => !ExactControlTexts(observed.Before).Contains(value, StringComparer.Ordinal) ||
                                  !ExactControlTexts(observed.After!).Contains(value, StringComparer.Ordinal))) return false;
        var editable = observed.Before.Fields.Where(field => field.Enabled && field.Visible && !field.ReadOnly).ToArray();
        if (editable.Length == 0) return !failure.SetupHadEditableFields;
        var declared = editable.Where(field => field.Text.Length != 0 && expected.Contains(field.Text, StringComparer.Ordinal)).ToArray();
        var oldIds = observed.Before.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        return declared.Length != 0 && declared.All(before => observed.After!.Fields.Any(after =>
            after.Enabled && after.Visible && !after.ReadOnly && !oldIds.Contains(after.Id) &&
            string.Equals(after.Text, before.Text, StringComparison.Ordinal)));
    }

    private static bool CurrentNativeAssertionPolicy(JsonElement assertion) =>
        assertion.ValueKind == JsonValueKind.Object &&
        assertion.TryGetProperty("policyVersion", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var version) &&
        version == PluginControlAssertions.CurrentPolicyVersion;

    private static IEnumerable<string> ExactControlTexts(PluginControlSnapshot snapshot) =>
        snapshot.Texts.Concat(snapshot.Fields.Where(field => field.Visible && !field.Limited).Select(field => field.Text));
}
