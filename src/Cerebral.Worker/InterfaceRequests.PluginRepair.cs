using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class InterfaceRequests
{
    private static bool IsSourceRepairFailureStage(InterfaceFailureStage? stage) =>
        stage is InterfaceFailureStage.NativeLoad or InterfaceFailureStage.ContributionValidation;

    /// <summary>
    /// Persists explicit owner consent to repair the exact source package whose immutable review
    /// already failed. This never reclaims, retries, installs, mounts, or controls the package.
    /// </summary>
    public InterfaceRequest SchedulePluginRepair(string id, string sessionId, bool controlTestsAllowed)
    {
        if (!controlTestsAllowed)
            throw new UnauthorizedAccessException("Repairing a failed native plugin review requires the owner's explicit test/repair opt-in.");
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = Get(id);
            ValidatePluginRepairCandidate(request, sessionId, validateCurrentPackage: false);
            if (request.PluginTests is { } retained)
            {
                if (retained.Mode == RepairBeforeInstallMode &&
                    retained.RequestId == PluginTestPrefix + request.Id)
                    return request;
                throw new InvalidOperationException("This failed review already has a different retained continuation outcome.");
            }
            ValidatePluginRepairPackage(request);
            var continuation = CreatePluginContinuation(request, RepairBeforeInstallMode);
            if (continuation.Status == "blocked")
                throw new InvalidOperationException(continuation.Message ?? "The repair lineage is unavailable.");
            request.PluginTests = continuation;
            store.Put(RecordKind, request.Id, request);
            changed = true;
            return request;
        });
        if (changed) Publish(result);
        return result;
    }

    private InterfacePluginTestContinuation? CreatePluginRepairContinuation(InterfaceRequest request,
        bool controlTestsAllowed)
    {
        if (!controlTestsAllowed) return null;
        ValidatePluginRepairCandidate(request, request.SessionId);
        return CreatePluginContinuation(request, RepairBeforeInstallMode);
    }

    private InterfacePluginTestContinuation CreateBlockedPluginRepairContinuation(InterfaceRequest request,
        string message) => new(PluginTestPrefix + request.Id, request.JobId, 1, "blocked",
        Message: Limit(message), Mode: RepairBeforeInstallMode);

    private static void ValidatePluginRepairCandidate(InterfaceRequest request, string sessionId,
        bool validateCurrentPackage = true)
    {
        if (request.SessionId != sessionId)
            throw new UnauthorizedAccessException("The failed plugin review belongs to another conversation.");
        if (request.Source != "workflow" || request.Kind != "plugin" || request.Status != "failed")
            throw new InvalidOperationException("Only an exact failed native workflow plugin review can schedule source repair.");
        if (request.ClaimId is null || request.ClaimedAt is null || request.CompletedAt is null ||
            string.IsNullOrWhiteSpace(request.Error))
            throw new InvalidOperationException("The retained failure does not prove a completed owner review attempt.");
        if (request.Plugin is not null || request.Resource is not null)
            throw new InvalidDataException("A failed review cannot also contain an installed plugin or capture receipt.");
        if (request.FailureStage is { } stage)
        {
            if (!Enum.IsDefined(stage))
                throw new InvalidDataException("The retained native plugin failure stage is unsupported.");
            if (!IsSourceRepairFailureStage(stage))
                throw new InvalidOperationException(
                    "The retained failure is in package installation or presentation infrastructure, not native source loading or contribution validation. Keep this receipt and start a fresh owner review after the infrastructure issue is corrected.");
        }
        if (request.PackageFingerprint is not { Length: 64 } fingerprint ||
            fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("The failed review has no valid immutable package fingerprint.");
        if (validateCurrentPackage) ValidatePluginRepairPackage(request);
    }

    private static void ValidatePluginRepairPackage(InterfaceRequest request)
    {
        if (NativeDesktopPluginPackage.ComputeFingerprint(request.Path) != request.PackageFingerprint)
            throw new InvalidOperationException("The reviewed package bytes changed after the failed attempt. Queue a new explicit owner request for the current package.");
    }
}
