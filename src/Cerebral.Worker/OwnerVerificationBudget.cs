using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

/// <summary>Pure decision over host-authored, same-run verification results. The caller must
/// first validate each result against its retained effect and closed owner admission.</summary>
internal static class OwnerVerificationBudget
{
    internal const int MaximumFailuresPerInput = 2;

    internal sealed record InputPin(string Role, string Path, string Sha256, long Bytes);
    internal sealed record Attempt(string RecipeSha256, string InputSha256, string EffectId,
        WorkflowEffectState State, bool ProcessDispatched, bool Complete);
    internal enum DecisionKind { Admit, ReuseSuccess, CorrectInputs, ReconcilePrior }
    internal sealed record Decision(DecisionKind Kind, string? EffectId = null, int FailedAttempts = 0);

    internal static string InputIdentity(string recipeSha256, IReadOnlyList<InputPin> pins)
    {
        RequireDigest(recipeSha256);
        // Stage admission already caps the complete private tree at 512 entries. The
        // input identity must not impose an unrelated smaller per-project file count.
        if (pins is null || pins.Count is < 1 or > 512)
            throw new ArgumentException("Verification input closure must contain one to 512 files.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(recipeSha256);
        foreach (var pin in pins.OrderBy(item => item.Role, StringComparer.Ordinal))
        {
            if (pin is null || pin.Role is not { Length: > 0 and <= 64 } ||
                pin.Role.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
                !Path.IsPathFullyQualified(pin.Path) || pin.Bytes is < 0 or > 64 * 1024 * 1024 ||
                !roles.Add(pin.Role) || !paths.Add(Path.GetFullPath(pin.Path)))
                throw new ArgumentException("Verification input role, path or byte bound is invalid or duplicated.");
            RequireDigest(pin.Sha256);
            var canonical = Path.GetFullPath(pin.Path);
            Add(pin.Role); Add(OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical);
            Add(pin.Sha256.ToLowerInvariant()); Add(pin.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }

    internal static Decision Evaluate(string recipeSha256, string inputSha256, IEnumerable<Attempt> sameRunAttempts,
        int maximumFailuresPerInput = MaximumFailuresPerInput)
    {
        RequireDigest(recipeSha256);
        RequireDigest(inputSha256);
        if (maximumFailuresPerInput is < 1 or > MaximumFailuresPerInput || sameRunAttempts is null)
            throw new ArgumentException("A finite admitted verification failure allowance is required.");
        var failures = 0;
        string? latestFailure = null;
        string? successful = null;
        string? uncertain = null;
        foreach (var attempt in sameRunAttempts)
        {
            if (attempt is null || attempt.EffectId is not { Length: > 0 and <= 128 })
                throw new InvalidDataException("The retained verification attempt is invalid.");
            RequireDigest(attempt.RecipeSha256);
            RequireDigest(attempt.InputSha256);
            if (attempt.RecipeSha256 != recipeSha256 || attempt.InputSha256 != inputSha256) continue;
            if (!attempt.Complete || attempt.State is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched or WorkflowEffectState.Prepared)
            {
                uncertain = attempt.EffectId;
                continue;
            }
            if (!attempt.ProcessDispatched) continue; // A preflight refusal cannot spend an execution allowance.
            if (attempt.State == WorkflowEffectState.Succeeded)
                successful = attempt.EffectId;
            if (attempt.State == WorkflowEffectState.Failed)
            {
                failures++;
                latestFailure = attempt.EffectId;
            }
        }
        if (uncertain is not null) return new(DecisionKind.ReconcilePrior, uncertain, failures);
        if (successful is not null) return new(DecisionKind.ReuseSuccess, successful, failures);
        return failures >= maximumFailuresPerInput
            ? new(DecisionKind.CorrectInputs, latestFailure, failures)
            : new(DecisionKind.Admit, null, failures);
    }

    private static void RequireDigest(string value)
    {
        if (value is not { Length: 64 } || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("An exact SHA-256 digest is required.");
    }
}
