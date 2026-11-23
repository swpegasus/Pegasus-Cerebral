using System.IO;

namespace Cerebral.Desktop.Plugins;

public sealed record DesktopPluginUpdateRecovery(string PluginId, string PreviousVersion,
    string PreviousDirectory, string PreviousFingerprint, string CandidateVersion,
    string CandidateDirectory, string? CandidateFingerprint, bool PreviousSelectionRestored,
    bool PreviousRuntimeRestored, string? RecoveryError);

/// <summary>The approved candidate failed. Recovery is a separate result, never update success.</summary>
public sealed class DesktopPluginActivationException(Exception failure, DesktopPluginUpdateRecovery recovery)
    : InvalidOperationException(DesktopPluginRuntime.ActivationFailureMessage(failure, recovery), failure)
{
    public DesktopPluginUpdateRecovery Recovery { get; } = recovery;
}

/// <summary>Preserves cancellation while retaining the one recovery attempt's outcome.</summary>
public sealed class DesktopPluginActivationCanceledException(OperationCanceledException failure, DesktopPluginUpdateRecovery recovery)
    : OperationCanceledException(DesktopPluginRuntime.ActivationFailureMessage(failure, recovery), failure, failure.CancellationToken)
{
    public DesktopPluginUpdateRecovery Recovery { get; } = recovery;
}

public sealed partial class DesktopPluginRuntime
{
    private int activationUpdateDepth;
    private string? activationUpdateId;

    /// <summary>Strict higher-version publication plus this owner's native activation boundary.
    /// Does not undo plugin data, committed archive resources/presets, arbitrary native effects,
    /// or activation in another host process. Same-version refresh remains a separate operation.</summary>
    public Task<DesktopPluginInfo> UpdateReviewedAsync(string manifestPath, string? expectedFingerprint,
        bool enableCandidate, Func<bool> ownerCurrent, Action validateActivation, CancellationToken cancellationToken,
        DesktopPluginLoadReservation? reservedAdmission = null) =>
        ApplyReviewedUpdateAsync(manifestPath, expectedFingerprint, enableCandidate, ownerCurrent,
            validateActivation, fingerprint => catalog.Update(manifestPath, fingerprint), cancellationToken, reservedAdmission);

    internal async Task<DesktopPluginInfo> ApplyReviewedUpdateAsync(string manifestPath, string? expectedFingerprint,
        bool enableCandidate, Func<bool> ownerCurrent, Action validateActivation,
        Func<string, DesktopPluginInfo> publish, CancellationToken cancellationToken,
        DesktopPluginLoadReservation? reservedAdmission = null)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(ownerCurrent); ArgumentNullException.ThrowIfNull(validateActivation);
        void RequireOwner()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ownerCurrent()) throw new InvalidOperationException("The native plugin runtime owner changed; no lifecycle effect was replayed.");
        }
        RequireOwner(); cancellationToken.ThrowIfCancellationRequested();
        if (activationUpdateDepth != 0 || changingContributions || loadingDepth != 0)
            throw new InvalidOperationException("A native activation or construction is already in progress in this owner.");
        using var update = catalog.BeginActivationUpdate(manifestPath);
        var previous = update.Previous;
        if (previous.Enabled && !LoadedExact(previous))
            throw new InvalidOperationException("The previous selected native package is not working in this owner. Refresh and inspect it before attempting a hot update.");
        var affected = UpdateClosure(previous.Id);
        using var candidateAdmission = reservedAdmission ?? (previous.Enabled || enableCandidate ? ReserveForPublication(manifestPath, true) : null);
        var actualFingerprint = candidateAdmission?.CandidateFingerprint(previous.Id) ??
            Cerebral.Core.NativeDesktopPluginPackage.ComputeFingerprint(manifestPath);
        if (expectedFingerprint is not null && actualFingerprint != expectedFingerprint)
            throw new IOException("The reviewed native candidate changed before publication.");
        // Reserve capacity for recovery before retiring anything, rather than discovering after
        // failure that the previous native assembly cannot be admitted again.
        using var recoveryAdmission = previous.Enabled ? ReserveForPublication(previous.ManifestPath, true) : null;
        activationUpdateId = previous.Id;
        activationUpdateDepth++;
        try
        {
            RequireOwner(); cancellationToken.ThrowIfCancellationRequested();
            DesktopPluginInfo? candidate = null;
            try
            {
                candidate = update.Publish(() => publish(actualFingerprint), enableCandidate);
                RequireOwner(); update.RequireCandidate(); cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Enabled)
                {
                    Disable(previous.Id);
                    await SettleUpdateRetirements(affected, cancellationToken);
                    RequireOwner(); update.RequireCandidate(); cancellationToken.ThrowIfCancellationRequested();
                    LoadEnabled(candidateAdmission); // Exactly one candidate native activation attempt.
                    RequireActivated(candidate, affected);
                    validateActivation();
                    RequireOwner(); update.RequireCandidate(); cancellationToken.ThrowIfCancellationRequested();
                }
                update.Commit();
                return candidate;
            }
            catch (Exception failure) when (update.Candidate is not null)
            {
                candidate = update.Candidate;
                string? recoveryError = null;
                var runtimeRestored = false;
                try
                {
                    RequireOwner(); update.RequireCandidate();
                    var current = GetLoadedPackage(previous.Id);
                    if (current is { } active && !SamePackage(active, candidate) && !SamePackage(active, previous))
                        throw new InvalidOperationException("A different native instance owns this plugin; recovery did not retire it.");
                    Disable(previous.Id);
                    // Cancellation after publication requests recovery; it cannot skip confirmed
                    // cleanup or authorize a second candidate attempt. Recovery itself is bounded.
                    await SettleUpdateRetirements(affected, CancellationToken.None);
                    RequireOwner(); update.RequireCandidate();
                    update.RestorePreviousSelection();
                    if (previous.Enabled)
                    {
                        update.RequireRestored();
                        LoadEnabled(recoveryAdmission);
                        RequireActivated(previous, affected);
                        validateActivation();
                    }
                    RequireOwner(); update.RequireRestored();
                    runtimeRestored = previous.Enabled ? LoadedExact(previous) : GetLoadedPackage(previous.Id) is null;
                    update.RecordRecovery(runtimeRestored);
                }
                catch (Exception recoveryFailure)
                {
                    recoveryError = BoundError(Error(recoveryFailure));
                    try { update.RecordRecovery(false); }
                    catch (Exception recordFailure)
                    { recoveryError = BoundError(recoveryError + " Recovery evidence could not advance: " + Error(recordFailure)); }
                }
                var recovery = new DesktopPluginUpdateRecovery(previous.Id, previous.Version, previous.Directory,
                    update.PreviousFingerprint, candidate.Version, candidate.Directory, update.CandidateFingerprint,
                    update.SelectionRestored, runtimeRestored, recoveryError);
                if (failure is OperationCanceledException canceled)
                    throw new DesktopPluginActivationCanceledException(canceled, recovery);
                throw new DesktopPluginActivationException(failure, recovery);
            }
        }
        finally { activationUpdateDepth--; activationUpdateId = null; }
    }

    internal bool UsesCatalog(DesktopPluginCatalog expected) => ReferenceEquals(catalog, expected);

    private bool LoadedExact(DesktopPluginInfo package) =>
        GetLoadedPackage(package.Id) is { } loadedPackage && SamePackage(loadedPackage, package);

    private static bool SamePackage((string Directory, string Version) actual, DesktopPluginInfo package) =>
        actual.Version == package.Version && actual.Directory.Equals(package.Directory, StringComparison.OrdinalIgnoreCase);

    private string[] UpdateClosure(string id)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal) { id };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in loaded.Values)
                if (!affected.Contains(item.Id) && item.ResolvedProviders.Any(binding =>
                        binding.Value is not null && affected.Contains(binding.Key)))
                    changed |= affected.Add(item.Id);
        } while (changed);
        return affected.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task SettleUpdateRetirements(IReadOnlyCollection<string> affected, CancellationToken cancellationToken)
    {
        var pending = retiring.Values.Where(item => affected.Contains(item.Id)).ToArray();
        if (pending.Length != 0)
        {
            var results = await Task.WhenAll(pending.Select(item => item.Retirement.Task))
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (results.FirstOrDefault(error => error is not null) is { } failure)
                throw new InvalidOperationException("Native cleanup did not settle: " + failure);
        }
        foreach (var id in affected)
            if (ProcessRetirementBlock(id) is { } blocked) throw new InvalidOperationException(blocked);
    }

    private void RequireActivated(DesktopPluginInfo target, IReadOnlyCollection<string> affected)
    {
        if (!LoadedExact(target))
            throw new InvalidOperationException("The candidate native package did not activate: " +
                (Status.SingleOrDefault(item => item.Id == target.Id)?.Error ?? "the exact selected generation is not loaded"));
        var selections = catalog.List().Where(item => affected.Contains(item.Id) && item.Enabled).ToArray();
        foreach (var selected in selections)
            if (selected.Error is not null || !LoadedExact(selected))
                throw new InvalidOperationException("An affected native contribution did not activate: " + selected.Id);
    }

    internal static string ActivationFailureMessage(Exception failure, DesktopPluginUpdateRecovery recovery) =>
        "The native plugin update failed: " + BoundError(Error(failure)) + " " +
        (recovery.PreviousSelectionRestored && recovery.PreviousRuntimeRestored && recovery.RecoveryError is null
            ? "The previous package selection and this owner's runtime were restored; the candidate update remains failed."
            : "Previous-package recovery is incomplete or unconfirmed. Inspect the retained recovery record and generations; do not repeat the update automatically.") +
        " Plugin data and arbitrary native or external effects were not rolled back.";
}
