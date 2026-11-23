using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

public sealed record DesktopPluginInfo(string Id, string Name, string Version, string Description, bool Enabled,
    string Directory, string ManifestPath, string? Error);

/// <summary>Per-user native packages and enablement, independent from service-owned MCP provider registrations.</summary>
public sealed partial class DesktopPluginCatalog
{
    public const string ManifestFileName = NativeDesktopPluginPackage.ManifestFileName;
    private const string ActiveSuffix = ".active.json";
    private const string PackagesDirectoryName = ".packages";
    private const int MaximumRetainedGenerations = 8;
    public const string NativeTrustNotice = "Native desktop plugins run inside Pegasus Cerebral as your Windows user. They can access your files, network and installed applications. Enable only plugins you trust. Service operations exposed by the host still use the application's normal checks.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string DefaultDirectory => DesktopInstancePaths.Current.PluginDirectory;
    public static string DefaultDataDirectory => DesktopInstancePaths.Current.PluginDataDirectory;
    public string RootDirectory { get; }

    public DesktopPluginCatalog(string? rootDirectory = null) => RootDirectory = Path.GetFullPath(rootDirectory ?? DefaultDirectory);

    internal IDisposable BeginPublication() =>
        DesktopPluginPublicationLease.TryAcquire(RootDirectory, publication: true) ?? throw DesktopPluginPublicationLease.Busy();

    internal IDisposable? TryBeginObservation() =>
        DesktopPluginPublicationLease.TryAcquire(RootDirectory, publication: false);

    private IDisposable BeginObservation() => TryBeginObservation() ?? throw DesktopPluginPublicationLease.Busy();

    /// <summary>Returns false only for a live cooperating publisher; callers retain their current
    /// projection and retry. Invalid storage still throws instead of looking like an empty catalog.</summary>
    public bool TryList(out IReadOnlyList<DesktopPluginInfo> plugins)
    {
        plugins = [];
        if (!Directory.Exists(RootDirectory)) return true;
        using var observation = TryBeginObservation();
        if (observation is null) return false;
        plugins = List();
        return true;
    }

    // One selected immutable directory identifies the loaded version without rescanning every
    // manifest for each callback. The caller holds an independent observation across its invocation.
    internal bool IsCurrentSelection(string id, string packageDirectory)
    {
        using var observation = BeginObservation();
        ValidateId(id);
        CheckAncestors(RootDirectory);
        return !IsRemoved(id) && ReadEnabled(id) && ResolvePackageDirectory(id).Equals(
            Path.GetFullPath(packageDirectory), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DesktopPluginInfo> List()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        using var observation = BeginObservation();
        CheckAncestors(RootDirectory);
        var result = new List<DesktopPluginInfo>();
        var ids = Directory.EnumerateDirectories(RootDirectory)
            .Select(Path.GetFileName).OfType<string>().Where(id => !id.StartsWith('.')).ToHashSet(StringComparer.Ordinal);
        var settings = Path.Combine(RootDirectory, ".settings");
        if (Directory.Exists(settings))
            foreach (var active in Directory.EnumerateFiles(settings, "*" + ActiveSuffix, SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileName(active)[..^ActiveSuffix.Length];
                try { ValidateId(id); ids.Add(id); } catch (ArgumentException) { }
            }
        foreach (var id in ids.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsRemoved(id)) continue;
            var folder = Path.Combine(RootDirectory, id);
            var manifestPath = Path.Combine(folder, ManifestFileName);
            try
            {
                ValidateId(id);
                folder = ResolvePackageDirectory(id); manifestPath = Path.Combine(folder, ManifestFileName);
                var manifest = Inspect(manifestPath);
                if (manifest.Id != id) throw new InvalidDataException("The manifest ID does not match its installed folder.");
                result.Add(new(id, manifest.Name, manifest.Version, manifest.Description, ReadEnabled(id), folder, manifestPath, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(id, id, "", "", false, folder, manifestPath, ex.Message)); }
        }
        return result;
    }

    public DesktopPluginManifest Inspect(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        CheckAncestors(path);
        var manifest = NativeDesktopPluginPackage.Inspect(path);
        return new DesktopPluginManifest
        {
            SchemaVersion = manifest.SchemaVersion, Contract = DesktopPluginContract.Id, ApiVersion = DesktopPluginContract.Version,
            Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, Description = manifest.Description,
            Assembly = manifest.Assembly, EntryType = manifest.EntryType,
            Dependencies = manifest.SchemaVersion == 1 ? null : manifest.Dependencies.Select(item => new DesktopPluginDependency
            {
                Id = item.Id, ExactVersion = item.ExactVersion, MinimumVersion = item.MinimumVersion,
                MaximumVersionExclusive = item.MaximumVersionExclusive, Optional = item.Optional
            }).ToList(),
            DefaultEnabled = manifest.SchemaVersion >= 3 ? manifest.DefaultEnabled : null,
            ExclusiveGroup = manifest.SchemaVersion >= 3 ? manifest.ExclusiveGroup : null,
            ExclusivePriority = manifest.SchemaVersion >= 3 ? manifest.ExclusivePriority : null
        };
    }

    /// <summary>Copies one reviewed package into an immutable generation, disabled by default.</summary>
    public DesktopPluginInfo Install(string manifestPath) => Install(manifestPath, null);

    public DesktopPluginInfo Install(string manifestPath, string? expectedFingerprint)
    {
        var sourcePath = Path.GetFullPath(manifestPath);
        var manifest = Inspect(sourcePath);
        if (!Path.GetFileName(sourcePath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the package's desktop-plugin.json file.");
        var source = Path.GetDirectoryName(sourcePath)!;
        using var publication = BeginPublication();
        if (IsInstalled(manifest.Id)) throw new IOException("This native plugin is already installed. Use Update package to publish a newer generation.");
        ValidateExternalSource(source);
        CleanupInactivePackages(manifest.Id);
        DetachRemovedLegacy(manifest.Id);
        var files = PackageFiles(source);
        Directory.CreateDirectory(RootDirectory); CheckAncestors(RootDirectory);
        var temporary = Path.Combine(RootDirectory, ".incoming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(temporary, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, false);
            }
            var copied = Inspect(Path.Combine(temporary, ManifestFileName));
            if (copied.Id != manifest.Id || copied.Version != manifest.Version)
                throw new IOException("The source manifest changed during import. Review the package again.");
            RequirePreparedFingerprint(Path.Combine(temporary, ManifestFileName), expectedFingerprint);
            // Removal and residual enablement choices join the same rollback boundary as
            // resources/presets. Select the code last; there is no fallible read after selection.
            var state = new PreparedPluginFileInstallation([
                (RemovedPath(manifest.Id), null),
                (ActivationPath(manifest.Id), null),
                (Path.Combine(RootDirectory, ".settings", manifest.Id + ".json"),
                    System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Enablement(false), Json)))
            ]);
            try
            {
                state.Publish();
                var publishedDirectory = PublishGeneration(manifest.Id, manifest.Version, temporary);
                state.Commit();
                state.Dispose();
                return new(manifest.Id, manifest.Name, manifest.Version, manifest.Description, false,
                    publishedDirectory, Path.Combine(publishedDirectory, ManifestFileName), null);
            }
            catch (Exception publicationError)
            {
                try { state.Dispose(); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Native plugin installation failed and state rollback was incomplete.",
                        publicationError, rollbackError);
                }
                throw;
            }
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    /// <summary>
    /// Stages and validates a newer reviewed package before atomically selecting its immutable generation.
    /// A running desktop can release the previous generation and load the new one without restarting.
    /// </summary>
    public DesktopPluginInfo Update(string manifestPath) => Update(manifestPath, null);

    public DesktopPluginInfo Update(string manifestPath, string? expectedFingerprint)
    {
        var sourcePath = Path.GetFullPath(manifestPath);
        var manifest = Inspect(sourcePath);
        var source = Path.GetDirectoryName(sourcePath)!;
        ValidateExternalSource(source);
        using var publication = BeginPublication();
        var current = List().SingleOrDefault(item => item.Id == manifest.Id) ??
            throw new DirectoryNotFoundException("The native plugin is not installed. Install it before applying an update.");
        if (current.Error is not null) throw new InvalidDataException("The installed native plugin is invalid: " + current.Error);
        if (!Version.TryParse(current.Version, out var installedVersion) || !Version.TryParse(manifest.Version, out var candidateVersion))
            throw new InvalidDataException("The installed or candidate native plugin version is invalid.");
        if (candidateVersion <= installedVersion)
            throw new InvalidOperationException($"The update must increase the package version above {current.Version}; candidate {manifest.Version} was not applied.");
        RequireCompatibleEnabledDependents(manifest.Id, manifest.Version);
        return ReplacePackage(sourcePath, manifest, current.Enabled, expectedFingerprint);
    }

    /// <summary>
    /// Publishes a separately reviewed rebuild of the currently selected package version as a new
    /// immutable generation. This is the explicit development refresh path; ordinary updates must
    /// still increase the semantic version through <see cref="Update"/>.
    /// </summary>
    public DesktopPluginInfo RefreshReviewed(string manifestPath) => RefreshReviewed(manifestPath, null);

    public DesktopPluginInfo RefreshReviewed(string manifestPath, string? expectedFingerprint)
    {
        var sourcePath = Path.GetFullPath(manifestPath);
        var manifest = Inspect(sourcePath);
        var source = Path.GetDirectoryName(sourcePath)!;
        ValidateExternalSource(source);
        using var publication = BeginPublication();
        var current = List().SingleOrDefault(item => item.Id == manifest.Id) ??
            throw new DirectoryNotFoundException("The native plugin is not installed. Install it before refreshing a reviewed rebuild.");
        if (current.Error is not null) throw new InvalidDataException("The installed native plugin is invalid: " + current.Error);
        if (!string.Equals(current.Version, manifest.Version, StringComparison.Ordinal))
            throw new InvalidOperationException($"A reviewed development refresh must keep the installed package version {current.Version}; candidate {manifest.Version} requires the normal update path.");
        RequireCompatibleEnabledDependents(manifest.Id, manifest.Version);
        return ReplacePackage(sourcePath, manifest, current.Enabled, expectedFingerprint);
    }

    /// <summary>
    /// Seeds an application-shipped package on first use and refreshes it when the shipped version is newer.
    /// A user's disable or removal choice is retained.
    /// </summary>
    public DesktopPluginInfo? SeedBundled(string manifestPath, bool? enableOnFirstInstall = null)
    {
        var manifest = Inspect(manifestPath);
        using var publication = BeginPublication();
        var removed = RemovedPath(manifest.Id); CheckAncestors(removed);
        CleanupRetiredPackages(manifest.Id);
        if (File.Exists(removed)) return null;
        if (IsInstalled(manifest.Id))
        {
            var current = List().SingleOrDefault(item => item.Id == manifest.Id) ?? throw new InvalidDataException("The installed bundled plugin package is invalid.");
            if (!Version.TryParse(current.Version, out var installedVersion) || !Version.TryParse(manifest.Version, out var bundledVersion))
                throw new InvalidDataException("The bundled plugin version is invalid.");
            return bundledVersion > installedVersion ? ReplacePackage(manifestPath, manifest, current.Enabled) : current;
        }
        try
        {
            var installed = Install(manifestPath);
            if (enableOnFirstInstall ?? manifest.DefaultEnabled ?? true)
            {
                var installedId = installed.Id; SetEnabled(installedId, true); installed = List().Single(item => item.Id == installedId);
            }
            return installed;
        }
        catch (IOException) when (IsInstalled(manifest.Id))
        {
            // Another Cerebral desktop process completed the same first-run seed.
            return List().SingleOrDefault(item => item.Id == manifest.Id);
        }
    }

    private DesktopPluginInfo ReplacePackage(string manifestPath, DesktopPluginManifest expected, bool enabled, string? expectedFingerprint = null)
    {
        if (enabled) ValidateEnablement(expected.Id, expected);
        var source = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var incoming = Path.Combine(RootDirectory, ".incoming-" + Guid.NewGuid().ToString("N"));
        CleanupInactivePackages(expected.Id);
        Directory.CreateDirectory(incoming);
        try
        {
            foreach (var file in PackageFiles(source))
            {
                var target = Path.Combine(incoming, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
            }
            var copied = Inspect(Path.Combine(incoming, ManifestFileName));
            if (copied.Id != expected.Id || copied.Version != expected.Version)
                throw new IOException("The native plugin package changed while it was being prepared.");
            RequirePreparedFingerprint(Path.Combine(incoming, ManifestFileName), expectedFingerprint);
            var publishedDirectory = PublishGeneration(expected.Id, expected.Version, incoming);
            // Preserve the existing enablement file; rewriting it after selection creates a
            // needless failure point after the immutable generation has already become active.
            return new(expected.Id, expected.Name, expected.Version, expected.Description, enabled,
                publishedDirectory, Path.Combine(publishedDirectory, ManifestFileName), null);
        }
        finally { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }
    }

    private static void RequirePreparedFingerprint(string manifestPath, string? expectedFingerprint)
    {
        if (expectedFingerprint is not null && NativeDesktopPluginPackage.ComputeFingerprint(manifestPath) != expectedFingerprint)
            throw new IOException("The copied native package differs from its admitted candidate fingerprint. The original selection was preserved; review the changed source again.");
    }

    private void CleanupRetiredPackages(string id)
    {
        foreach (var directory in RetiredPackageDirectories(id))
            _ = TryDeleteInactivePackage(id, directory);
    }

    private void CleanupInactivePackages(string id)
    {
        CleanupRetiredPackages(id);
        var active = IsRemoved(id) ? null : ReadActive(id)?.Generation;
        var versions = PackageStore(id); CheckAncestors(versions);
        if (Directory.Exists(versions))
            foreach (var directory in Directory.EnumerateDirectories(versions, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals(active, StringComparison.Ordinal)) continue;
                _ = TryDeleteInactivePackage(id, directory);
            }
        var legacy = Path.Combine(RootDirectory, id);
        if ((active is not null || IsRemoved(id)) && Directory.Exists(legacy))
        {
            _ = TryDeleteInactivePackage(id, legacy);
        }
        var retained = PackageRetirementDirectories(id).Count;
        if (retained >= MaximumRetainedGenerations)
            throw new IOException($"The plugin has {retained} retained generations that are still in use. Restart Cerebral to release them before applying another refresh.");
    }

    private string PublishGeneration(string id, string version, string incoming)
    {
        var store = PackageStore(id); Directory.CreateDirectory(store); CheckAncestors(store);
        var generation = SafeVersion(version) + "-" + Guid.NewGuid().ToString("N");
        var destination = Path.Combine(store, generation);
        Directory.Move(incoming, destination);
        try { WriteActive(id, generation); }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            throw;
        }
        return destination;
    }

    private string ResolvePackageDirectory(string id)
    {
        ValidateId(id);
        if (IsRemoved(id)) throw new DirectoryNotFoundException("The native plugin has been removed.");
        if (ReadActive(id) is { } active)
        {
            var store = PackageStore(id);
            var directory = ContainedPath(store, active.Generation);
            if (!Path.GetDirectoryName(directory)!.Equals(store, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("The selected native plugin generation is unavailable.");
            return directory;
        }
        var legacy = Path.Combine(RootDirectory, id);
        if (!Directory.Exists(legacy)) throw new DirectoryNotFoundException("The native plugin package is unavailable.");
        return legacy;
    }

    private bool IsInstalled(string id) => !IsRemoved(id) && (File.Exists(ActivePath(id)) || Directory.Exists(Path.Combine(RootDirectory, id)));
    private string PackageStore(string id) => Path.Combine(RootDirectory, PackagesDirectoryName, id);
    private string ActivePath(string id) => Path.Combine(RootDirectory, ".settings", id + ActiveSuffix);
    private static string SafeVersion(string version) => Regex.Replace(version, "[^A-Za-z0-9._-]", "-");

    private ActiveGeneration? ReadActive(string id)
    {
        var path = ActivePath(id); CheckAncestors(path);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length > 4096) throw new InvalidDataException("The native plugin active-generation setting is too large.");
        var state = JsonSerializer.Deserialize<ActiveGeneration>(input, Json) ?? throw new InvalidDataException("The native plugin active-generation setting is invalid.");
        if (string.IsNullOrWhiteSpace(state.Generation) || state.Generation.Length > 160 ||
            state.Generation.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || state.Generation is "." or "..")
            throw new InvalidDataException("The native plugin active generation is invalid.");
        return state;
    }

    private void WriteActive(string id, string generation)
    {
        var path = ActivePath(id); var settings = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(settings); CheckAncestors(settings);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ActiveGeneration(generation), Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ValidateExternalSource(string source)
    {
        var root = Path.TrimEndingDirectorySeparator(RootDirectory);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (root.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a plugin package folder outside the installed native plugin repository.");
    }

    public void SetEnabled(string id, bool enabled)
    {
        ValidateId(id);
        using var publication = BeginPublication();
        if (!IsInstalled(id)) throw new DirectoryNotFoundException("The native plugin is not installed.");
        if (enabled)
        {
            ValidateEnablement(id);
            DisableExclusiveAlternatives(id);
        }
        else RequireNoEnabledDependents(id);
        WriteEnablement(id, enabled);
    }

    /// <summary>
    /// Repairs an older settings repository that has more than one enabled provider in an exclusive group.
    /// The highest manifest priority wins; equal priorities are resolved deterministically by plugin ID.
    /// </summary>
    public IReadOnlyList<string> ReconcileExclusiveGroups()
    {
        using var publication = BeginPublication();
        var installed = List().Where(item => item.Enabled && item.Error is null)
            .Select(item => (Info: item, Manifest: Inspect(item.ManifestPath)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Manifest.ExclusiveGroup))
            .ToArray();
        var disabled = new List<string>();
        foreach (var group in installed.GroupBy(item => item.Manifest.ExclusiveGroup!, StringComparer.Ordinal))
        {
            var ranked = group.OrderByDescending(item => item.Manifest.ExclusivePriority ?? 0)
                .ThenBy(item => item.Info.Id, StringComparer.Ordinal).ToArray();
            foreach (var loser in ranked.Skip(1)) RequireNoEnabledDependents(loser.Info.Id);
            foreach (var loser in ranked.Skip(1))
            {
                WriteEnablement(loser.Info.Id, false);
                disabled.Add(loser.Info.Id);
            }
        }
        return disabled.AsReadOnly();
    }

    private void DisableExclusiveAlternatives(string id)
    {
        var selected = List().Single(item => item.Id == id);
        var manifest = Inspect(selected.ManifestPath);
        if (string.IsNullOrWhiteSpace(manifest.ExclusiveGroup)) return;
        var conflicts = List().Where(item => item.Enabled && item.Error is null && item.Id != id)
            .Where(item => string.Equals(Inspect(item.ManifestPath).ExclusiveGroup, manifest.ExclusiveGroup, StringComparison.Ordinal))
            .ToArray();
        foreach (var conflict in conflicts) RequireNoEnabledDependents(conflict.Id);
        foreach (var conflict in conflicts) WriteEnablement(conflict.Id, false);
    }

    private void WriteEnablement(string id, bool enabled)
    {
        CheckAncestors(RootDirectory);
        var stateDirectory = Path.Combine(RootDirectory, ".settings");
        Directory.CreateDirectory(stateDirectory); CheckAncestors(stateDirectory);
        var destination = Path.Combine(stateDirectory, id + ".json");
        CheckAncestors(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Enablement(enabled), Json));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Remove(string id) => _ = RemoveWithResult(id);

    private bool ReadEnabled(string id)
    {
        var path = Path.Combine(RootDirectory, ".settings", id + ".json");
        CheckAncestors(path);
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        if (input.Length > 4096) throw new InvalidDataException("The native plugin enable setting is too large.");
        return JsonSerializer.Deserialize<Enablement>(input, Json)?.Enabled ?? false;
    }

    private string RemovedPath(string id) => Path.Combine(RootDirectory, ".settings", id + ".removed");

    private void ValidateEnablement(string id, DesktopPluginManifest? candidate = null)
    {
        var packages = List().Where(item => item.Error is null).ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!packages.TryGetValue(id, out var root)) throw new InvalidDataException("The native plugin package is invalid.");
        var manifests = packages.ToDictionary(item => item.Key, item => Inspect(item.Value.ManifestPath), StringComparer.Ordinal);
        if (candidate is not null)
        {
            if (candidate.Id != id) throw new InvalidDataException("Candidate enablement identity does not match.");
            packages[id] = root with { Version = candidate.Version };
            manifests[id] = candidate;
            if (!string.IsNullOrWhiteSpace(candidate.ExclusiveGroup) &&
                packages.Values.Any(item => item.Id != id && item.Enabled &&
                    string.Equals(manifests[item.Id].ExclusiveGroup, candidate.ExclusiveGroup, StringComparison.Ordinal)))
                throw new InvalidOperationException("The update conflicts with another enabled plugin in its exclusive group.");
        }
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string current)
        {
            if (!visiting.Add(current)) throw new InvalidOperationException("Native plugin dependency cycle includes " + current + ".");
            if (visited.Contains(current)) { visiting.Remove(current); return; }
            var manifest = manifests[current];
            foreach (var dependency in manifest.Dependencies ?? [])
            {
                var declaration = Declaration(dependency);
                var available = packages.TryGetValue(dependency.Id, out var target) && (target.Enabled || dependency.Id == id) &&
                    PluginDependencyPolicy.Compatible(declaration, target.Version);
                if (!available)
                {
                    if (!dependency.Optional)
                        throw new InvalidOperationException("Required native plugin dependency '" + dependency.Id + "' is missing, disabled, invalid, or version-incompatible.");
                    continue;
                }
                Visit(dependency.Id);
            }
            visiting.Remove(current); visited.Add(current);
        }
        Visit(root.Id);
    }

    private void RequireNoEnabledDependents(string id)
    {
        var installed = List();
        var target = installed.SingleOrDefault(item => item.Id == id);
        if (target is null) return;
        var dependents = installed.Where(item => item.Enabled && item.Error is null && item.Id != id)
            .Where(item => (Inspect(item.ManifestPath).Dependencies ?? []).Any(dependency => !dependency.Optional &&
                dependency.Id == id && PluginDependencyPolicy.Compatible(Declaration(dependency), target.Version)))
            .Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
        if (dependents.Length != 0)
            throw new InvalidOperationException("Disable required dependent plugins first: " + string.Join(", ", dependents) + ".");
    }

    private void RequireCompatibleEnabledDependents(string id, string candidateVersion)
    {
        var incompatible = List().Where(item => item.Enabled && item.Error is null && item.Id != id)
            .Where(item => (Inspect(item.ManifestPath).Dependencies ?? []).Any(dependency => !dependency.Optional &&
                dependency.Id == id && !PluginDependencyPolicy.Compatible(Declaration(dependency), candidateVersion)))
            .Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
        if (incompatible.Length != 0)
            throw new InvalidOperationException("The update is incompatible with enabled dependent plugins: " + string.Join(", ", incompatible) + ".");
    }

    internal static PluginDependencyDeclaration Declaration(DesktopPluginDependency value) =>
        new(value.Id, value.ExactVersion, value.MinimumVersion, value.MaximumVersionExclusive, value.Optional);

    internal static string ContainedPath(string root, string relative) => NativeDesktopPluginPackage.ContainedPath(root, relative);

    private static void ValidateId(string id) => NativeDesktopPluginPackage.ValidateId(id);

    private static void CheckAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Native plugin packages and settings cannot traverse a link or junction.");
    }

    private static IReadOnlyList<string> PackageFiles(string root) => NativeDesktopPluginPackage.EnumeratePackageFiles(root);

    private sealed record Enablement(bool Enabled);
    private sealed record ActiveGeneration(string Generation);
}
