using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private sealed record ApprovedControlReference(string RequestId, string SourceRoot, string SourceManifest,
        string PackageFingerprint, IReadOnlyList<string> MountIds, string EffectId);

    private static ApprovedControlReference? ReadApprovedControlReference(Input input, PluginWorkBinding work,
        WorkflowEffect effect, ToolData? data, JsonElement? arguments)
    {
        // This is a lookup coordinate from the exact host inventory, never a test pass or a grant.
        // The native control host independently rechecks ownership, consent and current package bytes.
        if (work.Build.Component != "desktop" || arguments is not { } requested ||
            !TryActionPath(input, requested, "root", out var root) || !PathEquals(root, work.Build.Root) ||
            !SuccessfulEffect(effect, data) || data is not { Truncated: false,
                Evidence: { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } } ||
            data.Evidence.RequestId is { } receipt && receipt != effect.Id ||
            Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 32 });
            var body = document.RootElement;
            UniqueProperties(body);
            if (!body.TryGetProperty("approvedControlReference", out var value) || value.ValueKind != JsonValueKind.Object ||
                value.EnumerateObject().Any(property => property.Name is not
                    ("schemaVersion" or "requestId" or "status" or "sourceRoot" or "sourceManifest" or "packageFingerprint" or "mountIds")) ||
                !value.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 1 ||
                !TryText(value, "status", 16, out var status) || status != "completed" ||
                !TryText(value, "requestId", 32, out var id) || !ValidPresentationRequestId(id) ||
                !TryText(value, "sourceRoot", MaximumBindingPathCharacters, out var sourceRoot) ||
                !TryText(value, "sourceManifest", MaximumBindingPathCharacters, out var manifest) ||
                !TryText(value, "packageFingerprint", 64, out var fingerprint) || !ValidPackageFingerprint(fingerprint) ||
                !value.TryGetProperty("mountIds", out var mounts) || mounts.ValueKind != JsonValueKind.Array ||
                mounts.GetArrayLength() is < 1 or > WorkflowContract.MaximumPluginTestMounts ||
                !mounts.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) return null;
            var result = new ApprovedControlReference(id, sourceRoot, manifest, fingerprint,
                mounts.EnumerateArray().Select(item => item.GetString()!).ToArray(), effect.Id);
            return ValidApprovedControlReference(work.Build, result) ? result : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool ValidApprovedControlReference(PluginBuildCandidate build, ApprovedControlReference reference) =>
        build.Component == "desktop" && ValidPresentationRequestId(reference.RequestId) &&
        CanonicalStoredPath(reference.SourceRoot) && PathEquals(reference.SourceRoot, build.Root) &&
        CanonicalStoredPath(reference.SourceManifest) && PathEquals(reference.SourceManifest, build.BuiltManifest) &&
        ValidPackageFingerprint(reference.PackageFingerprint) && ValidEffectId(reference.EffectId) &&
        reference.MountIds is { Count: > 0 } && reference.MountIds.Count <= WorkflowContract.MaximumPluginTestMounts &&
        reference.MountIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 129 && !id.Any(char.IsControl)) &&
        reference.MountIds.Distinct(StringComparer.Ordinal).Count() == reference.MountIds.Count;

    private static void ValidateApprovedControlReference(HarnessState state, PluginWorkBinding work)
    {
        if (work.ApprovedControlReference is { } reference &&
            (!ValidApprovedControlReference(work.Build, reference) || !state.ToolReceipts.Any(receipt =>
                receipt.EffectId == reference.EffectId && receipt.Name == "plugin.projects" &&
                receipt.State == "Succeeded" && receipt.Outcome == "Succeeded" && !receipt.Truncated)))
            throw new ArgumentException("The saved approved plugin reference has no matching exact inspection receipt.");
    }

    private static object? ApprovedControlReferenceAnchor(PluginWorkBinding work) =>
        work.ApprovedControlReference is not { } reference ? null : new
        {
            referenceOnly = true, schemaVersion = 1, requestId = reference.RequestId, status = "completed",
            sourceRoot = reference.SourceRoot, sourceManifest = reference.SourceManifest,
            packageFingerprint = reference.PackageFingerprint, mountIds = reference.MountIds,
            inspectionEffectId = reference.EffectId, functionallyTested = false
        };
}
