using System.IO;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

/// <summary>Bind the reserved publication candidate to the exact normally claimed owner review.</summary>
internal static class ChatPluginInstallPackageAdmission
{
    internal static string RequireReviewedCandidate(InterfaceRequest request, string? candidateFingerprint)
    {
        // Both workflow and legacy native reviews already require a package fingerprint at normal
        // claim. Historical or malformed reviews need a new reviewed request, never a fallback.
        if (request.Kind != "plugin" || !Valid(request.PackageFingerprint) || !Valid(candidateFingerprint))
            throw new InvalidDataException("The native plugin review has no valid exact package fingerprint. Verify the current package and queue a new owner review.");
        if (!string.Equals(request.PackageFingerprint, candidateFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The reserved native plugin package changed after review. No package was installed or loaded; verify the current package and queue a new owner review.");
        return request.PackageFingerprint!;
    }

    private static bool Valid(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
