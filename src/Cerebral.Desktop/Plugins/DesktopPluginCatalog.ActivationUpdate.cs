using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

public sealed partial class DesktopPluginCatalog
{
    // One bounded host recovery record per plugin. It is evidence and a cleanup pin, not a
    // crash-recovery command: an interrupted operation is never automatically replayed.
    private sealed record ActivationRecord(int SchemaVersion, string Operation, string Id, string PreviousDirectory,
        string PreviousFingerprint, string? CandidateDirectory, string? CandidateFingerprint,
        string State, string? Diagnostic);
    private string ActivationPath(string id) => Path.Combine(RootDirectory, ".settings", id + ".activation-update.json");

    private ActivationRecord? ReadActivationRecord(string id)
    {
        ValidateId(id);
        var path = ActivationPath(id); CheckAncestors(path);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length > 8192) throw new InvalidDataException("The native activation recovery record is too large.");
        var record = JsonSerializer.Deserialize<ActivationRecord>(input, Json)
            ?? throw new InvalidDataException("The native activation recovery record is invalid.");
        if (record.SchemaVersion != 1 || record.Id != id || !Guid.TryParseExact(record.Operation, "N", out _) ||
            record.State is not ("prepared" or "published" or "committed" or "restored" or "incomplete" or "aborted"))
            throw new InvalidDataException("The native activation recovery identity or state is invalid.");
        ValidateActivationDirectory(record.PreviousDirectory);
        if (record.CandidateDirectory is { } candidate) ValidateActivationDirectory(candidate);
        RequireManagementHash(record.PreviousFingerprint, nameof(record));
        if (record.CandidateFingerprint is { } fingerprint) RequireManagementHash(fingerprint, nameof(record));
        return record;
    }

    private void ValidateActivationDirectory(string? relative)
    {
        if (relative is null || relative.Length is < 1 or > 512 || Path.IsPathRooted(relative) ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new InvalidDataException("The native activation recovery directory is invalid.");
        _ = ContainedPath(RootDirectory, relative);
    }

    private void WriteActivationRecord(ActivationRecord record, string? expectedOperation)
    {
        var current = ReadActivationRecord(record.Id);
        if (current?.Operation != expectedOperation)
            throw new InvalidOperationException("The native activation recovery owner changed; no recovery effect was repeated.");
        var path = ActivationPath(record.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal bool IsActivationBlocked(string id)
    {
        var record = ReadActivationRecord(id);
        return record is not null && record.State is "prepared" or "published" or "incomplete";
    }

    private bool IsActivationRetained(string id, string directory)
    {
        var record = ReadActivationRecord(id);
        if (record is null || record.State is "committed" or "aborted") return false;
        var relative = Path.GetRelativePath(RootDirectory, Path.GetFullPath(directory));
        return relative.Equals(record.PreviousDirectory, StringComparison.OrdinalIgnoreCase) ||
            relative.Equals(record.CandidateDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private string ActivationSelectionState(string id)
    {
        var item = ReadManagementItem(id);
        if (item.Error is not null || item.PackageFingerprint is null)
            throw new InvalidDataException("The selected native plugin is invalid or unavailable.");
        // Exact content plus metadata rejects cooperating owner writes, including an enablement
        // toggle back to the same Boolean. Immutable generation names also prevent selection ABA.
        var paths = new[] { ActivePath(id), Path.Combine(RootDirectory, ".settings", id + ".json"), RemovedPath(id) };
        var stamps = paths.Select(path =>
        {
            CheckAncestors(path);
            var info = new FileInfo(path);
            return info.Exists ? info.Length + ":" + info.CreationTimeUtc.Ticks + ":" + info.LastWriteTimeUtc.Ticks : "missing";
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ComputeManagementCatalogFingerprint([item]) + "\0" + string.Join("\0", stamps)))).ToLowerInvariant();
    }

    internal ActivationUpdate BeginActivationUpdate(string manifestPath)
    {
        using var publication = BeginPublication();
        var manifest = Inspect(manifestPath);
        ValidateExternalSource(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        var previous = List().SingleOrDefault(item => item.Id == manifest.Id)
            ?? throw new DirectoryNotFoundException("Install the native plugin before applying an update.");
        if (previous.Error is not null) throw new InvalidDataException("Repair the installed native plugin before updating it.");
        if (!Version.TryParse(previous.Version, out var installed) || !Version.TryParse(manifest.Version, out var candidate) || candidate <= installed)
            throw new InvalidOperationException("An activation update must increase the installed package version.");
        var prior = ReadActivationRecord(previous.Id);
        if (prior is not null && prior.State is not ("committed" or "restored" or "aborted"))
            throw new InvalidOperationException("An earlier native update has incomplete or interrupted recovery. Inspect its retained recovery record and package generations before taking another lifecycle action.");
        return new(this, previous, manifest, prior?.Operation);
    }

    internal sealed class ActivationUpdate : IDisposable
    {
        private readonly DesktopPluginCatalog catalog;
        private readonly DesktopPluginManifest manifest;
        private readonly List<FileStream> retained = [];
        private readonly string previousState;
        private readonly string? previousGeneration;
        private readonly byte[]? previousEnablement;
        private ActivationRecord record;
        private string? candidateState;
        private bool restoreAttempted;
        private bool disposed;
        internal DesktopPluginInfo Previous { get; }
        internal DesktopPluginInfo? Candidate { get; private set; }
        internal bool SelectionRestored { get; private set; }
        internal string PreviousFingerprint => record.PreviousFingerprint;
        internal string? CandidateFingerprint => record.CandidateFingerprint;

        internal ActivationUpdate(DesktopPluginCatalog catalog, DesktopPluginInfo previous,
            DesktopPluginManifest manifest, string? priorOperation)
        {
            this.catalog = catalog; this.manifest = manifest; Previous = previous;
            previousState = catalog.ActivationSelectionState(previous.Id);
            previousGeneration = catalog.ReadActive(previous.Id)?.Generation;
            var enabledPath = Path.Combine(catalog.RootDirectory, ".settings", previous.Id + ".json");
            previousEnablement = File.Exists(enabledPath) ? File.ReadAllBytes(enabledPath) : null;
            record = new(1, Guid.NewGuid().ToString("N"), previous.Id,
                Path.GetRelativePath(catalog.RootDirectory, previous.Directory),
                NativeDesktopPluginPackage.ComputeFingerprint(previous.ManifestPath), null, null, "prepared", null);
            try
            {
                // These are independent OS handles, not protection inferred from an old ALC.
                // They deny write/delete until native acceptance or the one compensation attempt settles.
                foreach (var path in PackageFiles(previous.Directory))
                    retained.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (catalog.ActivationSelectionState(previous.Id) != previousState)
                    throw new InvalidOperationException("The prior native selection changed during recovery preparation.");
                catalog.WriteActivationRecord(record, priorOperation);
            }
            catch { foreach (var handle in retained) handle.Dispose(); throw; }
        }

        internal DesktopPluginInfo Publish(Func<DesktopPluginInfo> publish, bool enableCandidate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var publication = catalog.BeginPublication();
            RequireState(previousState);
            // Validate before SetEnabled so this operation cannot disable an unrelated exclusive alternative.
            if (enableCandidate) catalog.ValidateEnablement(Previous.Id, manifest);
            var candidate = publish(); // Existing strict Update/import admission remains authoritative.
            Candidate = candidate;
            if (candidate.Id != Previous.Id || candidate.Version != manifest.Version)
                throw new InvalidOperationException("The publisher returned an unexpected native candidate.");
            candidateState = catalog.ActivationSelectionState(candidate.Id);
            record = record with { CandidateDirectory = Path.GetRelativePath(catalog.RootDirectory, candidate.Directory),
                CandidateFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(candidate.ManifestPath), State = "published" };
            foreach (var path in PackageFiles(candidate.Directory))
                retained.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            catalog.WriteActivationRecord(record, record.Operation);
            if (enableCandidate && !candidate.Enabled)
            {
                catalog.SetEnabled(candidate.Id, true); candidate = candidate with { Enabled = true }; Candidate = candidate;
                candidateState = catalog.ActivationSelectionState(candidate.Id);
            }
            return candidate;
        }

        internal void RequireCandidate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var observation = catalog.BeginObservation();
            if (candidateState is null) throw new InvalidOperationException("Native candidate publication is unconfirmed.");
            RequireState(candidateState);
        }

        private void RequireState(string state)
        {
            if (catalog.ReadActivationRecord(Previous.Id)?.Operation != record.Operation ||
                catalog.ActivationSelectionState(Previous.Id) != state)
                throw new InvalidOperationException("The exact native plugin selection, enablement, package bytes or recovery owner changed. Recovery did not overwrite another actor.");
        }

        internal void Commit()
        {
            // Acceptance evidence does not change selection. Already-admitted candidate callbacks
            // may retain readers; exclude other publishers without canceling healthy native work.
            using var observation = catalog.BeginObservation();
            RequireState(candidateState ?? throw new InvalidOperationException("Native candidate publication is unconfirmed."));
            catalog.WriteActivationRecord(record with { State = "committed" }, record.Operation);
            record = record with { State = "committed" };
        }

        internal void RestorePreviousSelection()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (restoreAttempted) throw new InvalidOperationException("Native update compensation was already attempted; inspect the retained result instead of replaying it.");
            restoreAttempted = true;
            using var publication = catalog.BeginPublication();
            RequireState(candidateState ?? throw new InvalidOperationException("Native candidate publication is unconfirmed."));
            if (NativeDesktopPluginPackage.ComputeFingerprint(Previous.ManifestPath) != record.PreviousFingerprint)
                throw new InvalidOperationException("The retained prior native package changed. Recovery refused to select it.");
            catalog.RequireCompatibleEnabledDependents(Previous.Id, Previous.Version);
            if (Previous.Enabled) catalog.ValidateEnablement(Previous.Id, catalog.Inspect(Previous.ManifestPath));
            using var enablement = new PreparedPluginFileInstallation([
                (Path.Combine(catalog.RootDirectory, ".settings", Previous.Id + ".json"), previousEnablement)
            ]);
            enablement.Publish();
            // This typed transition selects only this operation's captured, validated generation;
            // it is not a public arbitrary-version/downgrade or raw pointer restoration API.
            if (previousGeneration is null) File.Delete(catalog.ActivePath(Previous.Id));
            else catalog.WriteActive(Previous.Id, previousGeneration);
            SelectionRestored = true; // Record the effect before any subsequent fallible observation.
            enablement.Commit();
            candidateState = catalog.ActivationSelectionState(Previous.Id);
        }

        internal void RequireRestored()
        {
            using var observation = catalog.BeginObservation();
            if (!SelectionRestored) throw new InvalidOperationException("Prior native selection restoration is unconfirmed.");
            RequireState(candidateState!);
        }

        internal void RecordRecovery(bool runtimeRestored)
        {
            // Diagnostic-only state may advance alongside native cleanup readers. The observation
            // still excludes catalog publishers; no selection/enablement effect occurs here.
            using var observation = catalog.BeginObservation();
            if (SelectionRestored) RequireState(candidateState!);
            catalog.WriteActivationRecord(record with {
                State = SelectionRestored && runtimeRestored ? "restored" : "incomplete",
                Diagnostic = SelectionRestored && runtimeRestored ? "candidate_activation_failed_previous_restored" : "native_recovery_incomplete"
            }, record.Operation);
            record = record with { State = SelectionRestored && runtimeRestored ? "restored" : "incomplete" };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Before-publication cancellation/rejection may close cleanly only while exact prior
            // selection still agrees. An unconfirmed publication retains its prepared record.
            try
            {
                if (Candidate is null && record.State == "prepared")
                {
                    using var publication = catalog.BeginPublication();
                    if (catalog.ActivationSelectionState(Previous.Id) == previousState)
                        catalog.WriteActivationRecord(record with { State = "aborted" }, record.Operation);
                }
            }
            finally { foreach (var handle in retained) handle.Dispose(); }
        }
    }
}
