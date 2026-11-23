using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

public static class CerebralPluginPackageContract
{
    public const string Contract = "pegasus.cerebral.plugin";
    public const int SchemaVersion = 2;
    public const int HostFacetsSchemaVersion = 3;
    public const int LegacySchemaVersion = 1;
    public const string Extension = ".CerebralPlugin";
    public const string ManifestName = "cerebral-plugin.json";
    public const string DesktopManifestEntry = "plugin/desktop-plugin.json";
    public const string SettingsEntry = "settings.json";
    public const string DefaultSettingsFileName = "portable-settings.default.json";
    public const string CurrentSettingsFileName = "portable-settings.json";
    public const string ResourceManifestFileName = "plugin-resources.json";
    public const int MaximumEntries = NativeDesktopPluginPackage.MaximumEntries;
    public const int MaximumDirectories = NativeDesktopPluginPackage.MaximumDirectories;
    public const int MaximumDepth = NativeDesktopPluginPackage.MaximumDepth;
    public const long MaximumExpandedBytes = NativeDesktopPluginPackage.MaximumExpandedBytes;
    public const int MaximumSettingsBytes = 64 * 1024;
}

public sealed class CerebralPluginPackageManifest
{
    public int SchemaVersion { get; set; } = CerebralPluginPackageContract.SchemaVersion;
    public string Contract { get; set; } = CerebralPluginPackageContract.Contract;
    public string Kind { get; set; } = "desktop";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Payload { get; set; } = CerebralPluginPackageContract.DesktopManifestEntry;
    public string SettingsMode { get; set; } = "none";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SettingsEntry { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RequiredHostFacets { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CerebralPluginPackageResource>? Resources { get; set; }
}

public sealed class CerebralPluginPackageResource
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}

public sealed class CerebralPluginResourceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public List<CerebralPluginPackageResource> Resources { get; set; } = [];
}

public sealed record CerebralPluginPackageInspection(CerebralPluginPackageManifest Package,
    DesktopPluginManifest Desktop, long PackageBytes, long ExpandedBytes, int FileCount);

/// <summary>
/// A reviewed, bounded extraction. Native code is never loaded by preparation; callers explicitly install the
/// contained desktop package disabled and may then apply the optional portable settings document.
/// </summary>
public sealed class PreparedCerebralPluginPackage : IDisposable
{
    private bool disposed;
    private readonly IReadOnlyDictionary<string, string> resourceSources;
    private readonly string desktopManifestPath;

    internal PreparedCerebralPluginPackage(string directory, string desktopManifestPath,
        CerebralPluginPackageInspection inspection, byte[]? settings, IReadOnlyDictionary<string, string> resourceSources)
    {
        Directory = directory;
        this.desktopManifestPath = desktopManifestPath;
        Inspection = inspection;
        Settings = settings;
        this.resourceSources = resourceSources;
    }

    public string Directory { get; }
    public CerebralPluginPackageInspection Inspection { get; }
    public byte[]? Settings { get; }
    public string DesktopManifestPath => desktopManifestPath;

    public PreparedCerebralPluginSettingsInstallation BeginPortableSettingsInstallation(string dataRoot) =>
        new(dataRoot, Inspection.Package.Id, Settings);

    public void ApplyPortableSettings(string dataRoot)
    {
        using var installation = BeginPortableSettingsInstallation(dataRoot);
        installation.Publish();
        installation.Commit();
    }

    /// <summary>Reviewed archive update with this runtime owner's native activation recovery.
    /// Committed resources/presets and plugin-written data are not rewound after native activation.</summary>
    public Task<DesktopPluginInfo> UpdateReviewedAsync(DesktopPluginCatalog catalog, DesktopPluginRuntime runtime,
        DesktopProductPaths productPaths, string dataRoot, string? expectedId, string? expectedFingerprint,
        Func<bool> ownerCurrent, Action validateActivation, CancellationToken cancellationToken)
        => UpdateReviewedCoreAsync(catalog, runtime, productPaths, dataRoot, expectedId, expectedFingerprint,
            ownerCurrent, validateActivation, cancellationToken, null);

    public Task<DesktopPluginInfo> UpdateReviewedAsync(DesktopPluginCatalog catalog, DesktopPluginRuntime runtime,
        DesktopProductPaths productPaths, string dataRoot, string? expectedId, string? expectedFingerprint,
        Func<bool> ownerCurrent, Action validateActivation, CancellationToken cancellationToken, DesktopPluginInfo reviewedSelection)
    {
        ArgumentNullException.ThrowIfNull(reviewedSelection);
        return UpdateReviewedCoreAsync(catalog, runtime, productPaths, dataRoot, expectedId, expectedFingerprint,
            ownerCurrent, validateActivation, cancellationToken, reviewedSelection);
    }

    private Task<DesktopPluginInfo> UpdateReviewedCoreAsync(DesktopPluginCatalog catalog, DesktopPluginRuntime runtime,
        DesktopProductPaths productPaths, string dataRoot, string? expectedId, string? expectedFingerprint,
        Func<bool> ownerCurrent, Action validateActivation, CancellationToken cancellationToken, DesktopPluginInfo? reviewedSelection)
    {
        if (!runtime.UsesCatalog(catalog))
            throw new InvalidOperationException("The update catalog does not belong to this native runtime.");
        return runtime.ApplyReviewedUpdateAsync(DesktopManifestPath, expectedFingerprint, false,
            ownerCurrent, validateActivation,
            fingerprint => PublishReviewed(catalog, productPaths, dataRoot, expectedId, fingerprint,
                requireReviewedSelection: reviewedSelection is not null, reviewedSelection), cancellationToken);
    }

    /// <summary>Process-failure rollback for resources/preset before the final native code selection.
    /// This is not a crash-recovery journal or an operating-system sandbox.</summary>
    public DesktopPluginInfo InstallReviewed(DesktopPluginCatalog catalog, DesktopProductPaths productPaths,
        string dataRoot, string? expectedId = null)
        => InstallReviewed(catalog, productPaths, dataRoot, expectedId, null);

    public DesktopPluginInfo InstallReviewed(DesktopPluginCatalog catalog, DesktopProductPaths productPaths,
        string dataRoot, string? expectedId, string? expectedFingerprint)
        => PublishReviewed(catalog, productPaths, dataRoot, expectedId, expectedFingerprint, false, null);

    public DesktopPluginInfo InstallNewReviewed(DesktopPluginCatalog catalog, DesktopProductPaths productPaths,
        string dataRoot, string? expectedFingerprint)
        => PublishReviewed(catalog, productPaths, dataRoot, null, expectedFingerprint, true, null);

    public IReadOnlyList<string> ResourceDestinations(DesktopProductPaths productPaths) =>
        (Inspection.Package.Resources ?? []).Select(resource => CerebralPluginPackage.ContainedResourcePath(
            CerebralPluginPackage.ResourceRoot(productPaths, resource.Kind), resource.Destination)).ToArray();

    private DesktopPluginInfo PublishReviewed(DesktopPluginCatalog catalog, DesktopProductPaths productPaths,
        string dataRoot, string? expectedId, string? expectedFingerprint, bool requireReviewedSelection,
        DesktopPluginInfo? reviewedSelection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var package = Inspection.Package;
        if (expectedId is not null && !string.Equals(package.Id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException($"The selected package is {package.Id}; choose an update for {expectedId}.");
        using var publication = catalog.BeginPublication();
        var current = catalog.List().SingleOrDefault(item => item.Id == package.Id);
        if (requireReviewedSelection && current != reviewedSelection)
            throw new InvalidOperationException("The installed plugin changed during review. Open the package again to review the current selection.");
        if (current is not null)
        {
            if (current.Error is not null)
                throw new InvalidDataException("The installed package must be repaired before updating it: " + current.Error);
            if (!Version.TryParse(current.Version, out var installed) || !Version.TryParse(package.Version, out var candidate) ||
                candidate <= installed)
                throw new InvalidOperationException($"The package version must be newer than installed version {current.Version}; candidate {package.Version} was not applied.");
        }
        // Both preparations finish without replacing existing content. Candidate admission is
        // last; a failure in staging, resources, presets, or catalog validation restores prior data.
        var destinations = ResourceDestinations(productPaths);
        var protectedPaths = new List<string> { catalog.RootDirectory };
        if (Settings is not null) protectedPaths.Add(CerebralPluginPackage.ContainedResourcePath(Path.GetFullPath(dataRoot),
            package.Id + "/" + CerebralPluginPackageContract.CurrentSettingsFileName));
        CerebralPluginPackage.ValidateTransactionDestinations(destinations, protectedPaths);
        var resources = BeginResourceInstallation(productPaths);
        // Dispose each uncommitted participant explicitly on failure so one recovery error
        // cannot hide the publication error or prevent another participant's restoration.
        PreparedCerebralPluginSettingsInstallation? preset = null;
        try
        {
            preset = BeginPortableSettingsInstallation(dataRoot);
            resources.Publish();
            preset.Publish();
            var result = current is null ? catalog.Install(DesktopManifestPath, expectedFingerprint) : catalog.Update(DesktopManifestPath, expectedFingerprint);
            preset.Commit();
            resources.Commit();
            preset.Dispose();
            resources.Dispose();
            return result;
        }
        catch (Exception publicationError)
        {
            var failures = new List<Exception> { publicationError };
            try { preset?.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { resources.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 1)
                throw new AggregateException("Reviewed plugin installation failed and rollback was incomplete; recovery artifacts were retained.",
                    failures);
            throw;
        }
    }

    /// <summary>
    /// Stages every declared product resource without changing its destination. Publish is reversible until Commit,
    /// which lets the caller restore prior resources if native package admission fails.
    /// </summary>
    public PreparedCerebralPluginResourceInstallation BeginResourceInstallation(DesktopProductPaths productPaths)
    {
        ArgumentNullException.ThrowIfNull(productPaths);
        var resources = Inspection.Package.Resources ?? [];
        if (resources.Count == 0) return PreparedCerebralPluginResourceInstallation.Empty;
        var transaction = Guid.NewGuid().ToString("N");
        var pending = new List<PreparedCerebralPluginResourceInstallation.PendingResource>();
        try
        {
            var resolved = resources.Select(resource =>
            {
                var source = resourceSources[resource.Id];
                if (!System.IO.Directory.Exists(source)) throw new InvalidDataException($"Plugin resource source is missing: {resource.Id}.");
                var root = CerebralPluginPackage.ResourceRoot(productPaths, resource.Kind);
                var destination = CerebralPluginPackage.ContainedResourcePath(Path.GetFullPath(root), resource.Destination);
                return (Resource: resource, Source: source, Destination: destination);
            }).ToArray();
            CerebralPluginPackage.ValidateResolvedDestinations(resolved.Select(item => item.Destination).ToArray());
            foreach (var item in resolved)
            {
                var resource = item.Resource;
                var source = item.Source;
                var destination = item.Destination;
                var stage = destination + ".cerebral-staging-" + transaction;
                var previous = destination + ".cerebral-previous-" + transaction;
                if (System.IO.Directory.Exists(stage) || File.Exists(stage) ||
                    System.IO.Directory.Exists(previous) || File.Exists(previous))
                    throw new IOException("A plugin resource staging path already exists.");
                CerebralPluginPackage.RejectLinks(Path.GetDirectoryName(destination)!);
                var pendingResource = new PreparedCerebralPluginResourceInstallation.PendingResource(
                    resource.Id, destination, stage, previous);
                pending.Add(pendingResource);
                System.IO.Directory.CreateDirectory(stage);
                CopyDirectory(source, stage);
            }
            return new(pending);
        }
        catch
        {
            foreach (var item in pending)
                if (System.IO.Directory.Exists(item.Stage)) System.IO.Directory.Delete(item.Stage, true);
            throw;
        }
    }

    public void InstallResources(DesktopProductPaths productPaths)
    {
        using var installation = BeginResourceInstallation(productPaths);
        installation.Publish();
        installation.Commit();
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in System.IO.Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            System.IO.Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}

/// <summary>A staged multi-resource replacement. Dispose rolls published resources back until Commit succeeds.</summary>
public sealed class PreparedCerebralPluginResourceInstallation : IDisposable
{
    internal sealed class PendingResource(string id, string destination, string stage, string previous)
    {
        public string Id { get; } = id;
        public string Destination { get; } = destination;
        public string Stage { get; } = stage;
        public string Previous { get; } = previous;
        public bool PreviousMoved { get; set; }
        public bool Published { get; set; }
    }

    private readonly IReadOnlyList<PendingResource> resources;
    private bool published;
    private bool committed;
    private bool disposed;

    internal static PreparedCerebralPluginResourceInstallation Empty => new([]);
    internal PreparedCerebralPluginResourceInstallation(IReadOnlyList<PendingResource> resources) => this.resources = resources;

    public void Publish()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (published) throw new InvalidOperationException("Plugin resources were already published.");
        try
        {
            foreach (var item in resources)
            {
                CerebralPluginPackage.RejectLinks(item.Destination);
                CerebralPluginPackage.RejectLinks(item.Stage);
                CerebralPluginPackage.RejectLinks(item.Previous);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                if (File.Exists(item.Destination))
                    throw new IOException($"Plugin resource destination is a file: {item.Id}.");
                if (System.IO.Directory.Exists(item.Destination))
                {
                    System.IO.Directory.Move(item.Destination, item.Previous);
                    item.PreviousMoved = true;
                }
                System.IO.Directory.Move(item.Stage, item.Destination);
                item.Published = true;
            }
            published = true;
        }
        catch (Exception publishError)
        {
            try { Rollback(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Plugin resource publication failed and its rollback was incomplete.", publishError, rollbackError);
            }
            throw;
        }
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!published) throw new InvalidOperationException("Publish plugin resources before committing them.");
        committed = true;
        // Publication is already complete. Old generations are cleanup-only from this point; a lock must not
        // turn a successful replacement into a false rollback claim.
        foreach (var item in resources)
            try { if (System.IO.Directory.Exists(item.Previous)) System.IO.Directory.Delete(item.Previous, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void Rollback()
    {
        Exception? failure = null;
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            var item = resources[index];
            try
            {
                if (item.PreviousMoved && !System.IO.Directory.Exists(item.Previous))
                    throw new IOException("Prior plugin resource is missing; recovery is required at " + item.Previous);
                CerebralPluginPackage.RejectLinks(item.Destination);
                CerebralPluginPackage.RejectLinks(item.Previous);
                if (item.Published && System.IO.Directory.Exists(item.Destination))
                    System.IO.Directory.Delete(item.Destination, true);
                if (item.PreviousMoved)
                    System.IO.Directory.Move(item.Previous, item.Destination);
                item.Published = false;
                item.PreviousMoved = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failure ??= error; }
        }
        published = false;
        if (failure is not null) throw new IOException("A prior plugin resource could not be restored.", failure);
    }

    public void Dispose()
    {
        if (disposed) return;
        // Failed restoration must retain every recovery path and allow retry after its lock is released.
        if (!committed) Rollback();
        foreach (var item in resources)
        {
            try { if (System.IO.Directory.Exists(item.Stage)) System.IO.Directory.Delete(item.Stage, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (committed)
                try { if (System.IO.Directory.Exists(item.Previous)) System.IO.Directory.Delete(item.Previous, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        disposed = true;
    }
}

public static class CerebralPluginPackage
{
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = true };
    private static readonly HashSet<string> ResourceFields = new(StringComparer.Ordinal)
        { "id", "kind", "source", "destination" };
    private static readonly HashSet<string> ManifestFieldsV1 = new(StringComparer.Ordinal)
    {
        "schemaVersion", "contract", "kind", "id", "name", "version", "description", "payload", "settingsMode", "settingsEntry"
    };
    private static readonly HashSet<string> ManifestFieldsV2 = new(ManifestFieldsV1, StringComparer.Ordinal) { "resources" };
    private static readonly HashSet<string> ManifestFieldsV3 = new(ManifestFieldsV2, StringComparer.Ordinal) { "requiredHostFacets" };
    public const string SubmissionLifecycleHostFacet = "cerebral.desktop.text-entry-submission-lifecycle/1";

    private static List<string> RequiredHostFacets(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var executable = new PEReader(stream);
        var metadata = executable.GetMetadataReader();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            if (metadata.GetString(type.Namespace) != "Cerebral.Desktop.Plugins" ||
                metadata.GetString(type.Name) is not (nameof(IDesktopTextEntrySubmissionLifecyclePlugin) or nameof(DesktopTextEntrySubmissionAcknowledgment)) ||
                type.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if (metadata.GetString(reference.Name) == typeof(IDesktopPlugin).Assembly.GetName().Name)
                return [SubmissionLifecycleHostFacet];
        }
        return [];
    }

    public static PreparedCerebralPluginPackage Prepare(string packagePath)
    {
        var source = Path.GetFullPath(packagePath);
        if (!source.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new InvalidDataException($"Select an existing {CerebralPluginPackageContract.Extension} package.");
        var temporary = Path.Combine(Path.GetTempPath(), "Pegasus-Cerebral-Plugin-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temporary);
        try
        {
            using var archive = BoundedContentZipArchive.OpenRead(source);
            var files = ValidateEntries(archive.Files);
            var manifestEntry = files.SingleOrDefault(x => x.FullName.Equals(CerebralPluginPackageContract.ManifestName, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"The package must contain one root {CerebralPluginPackageContract.ManifestName}.");
            var package = ReadManifest(archive, manifestEntry);
            archive.ExtractToNewDirectory(temporary);
            var pluginRoot = Path.Combine(temporary, "plugin");
            var resources = package.Resources ?? [];
            ValidateResources(resources, pluginRoot);
            var describedResources = ReadResourceManifest(pluginRoot, out var hasResourceManifest);
            if (package.SchemaVersion >= CerebralPluginPackageContract.SchemaVersion &&
                (!SameResources(resources, describedResources) || resources.Count != 0 && !hasResourceManifest))
                throw new InvalidDataException("The package resources must exactly match plugin-resources.json.");
            var desktopRoot = PrepareDesktopPayload(temporary, pluginRoot, resources);
            var desktopManifestPath = Path.Combine(desktopRoot, NativeDesktopPluginPackage.ManifestFileName);
            var desktopIdentity = NativeDesktopPluginPackage.Inspect(desktopManifestPath);
            var required = RequiredHostFacets(Path.Combine(desktopRoot, desktopIdentity.Assembly));
            if (required.Except(package.RequiredHostFacets ?? [], StringComparer.Ordinal).Any())
                throw new InvalidDataException("The plugin assembly requires a host facet not declared by this package. Export it again with its compatible host.");
            if (package.Id != desktopIdentity.Id || package.Name != desktopIdentity.Name || package.Version != desktopIdentity.Version ||
                package.Description != desktopIdentity.Description)
                throw new InvalidDataException("The outer package identity does not exactly match desktop-plugin.json.");
            byte[]? settings = null;
            if (package.SettingsEntry is not null)
            {
                var settingsEntry = files.SingleOrDefault(x => x.FullName.Equals(package.SettingsEntry, StringComparison.Ordinal))
                    ?? throw new InvalidDataException("The package settings entry is missing.");
                settings = archive.ReadEntryBytes(settingsEntry, CerebralPluginPackageContract.MaximumSettingsBytes);
                ValidateSettings(settings);
            }
            var desktop = new DesktopPluginManifest
            {
                SchemaVersion = desktopIdentity.SchemaVersion,
                Contract = DesktopPluginContract.Id,
                ApiVersion = DesktopPluginContract.Version,
                Id = desktopIdentity.Id,
                Name = desktopIdentity.Name,
                Version = desktopIdentity.Version,
                Description = desktopIdentity.Description,
                Assembly = desktopIdentity.Assembly,
                EntryType = desktopIdentity.EntryType,
                Dependencies = desktopIdentity.SchemaVersion == 1 ? null : desktopIdentity.Dependencies.Select(item => new DesktopPluginDependency
                {
                    Id = item.Id, ExactVersion = item.ExactVersion, MinimumVersion = item.MinimumVersion,
                    MaximumVersionExclusive = item.MaximumVersionExclusive, Optional = item.Optional
                }).ToList(),
                DefaultEnabled = desktopIdentity.SchemaVersion >= 3 ? desktopIdentity.DefaultEnabled : null,
                ExclusiveGroup = desktopIdentity.SchemaVersion >= 3 ? desktopIdentity.ExclusiveGroup : null,
                ExclusivePriority = desktopIdentity.SchemaVersion >= 3 ? desktopIdentity.ExclusivePriority : null
            };
            var resourceSources = resources.ToDictionary(resource => resource.Id,
                resource => ContainedResourcePath(pluginRoot, resource.Source), StringComparer.Ordinal);
            return new(temporary, desktopManifestPath,
                new(package, desktop, new FileInfo(source).Length, files.Sum(x => x.Length), files.Count), settings, resourceSources);
        }
        catch
        {
            if (System.IO.Directory.Exists(temporary)) System.IO.Directory.Delete(temporary, true);
            throw;
        }
    }

    public static void Export(DesktopPluginInfo plugin, string destination, string settingsMode,
        DesktopProductPaths productPaths, string? pluginDataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(productPaths);
        destination = Path.GetFullPath(destination);
        if (!destination.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Plugin package output must end in {CerebralPluginPackageContract.Extension}.");
        if (File.Exists(destination)) throw new IOException("Destination already exists. Choose a new package name.");
        if (!System.IO.Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Choose an existing export folder.");
        settingsMode = settingsMode.Trim().ToLowerInvariant();
        if (settingsMode is not ("none" or "defaults" or "current"))
            throw new ArgumentException("Plugin package settings mode must be none, defaults, or current.");

        var root = Path.GetFullPath(plugin.Directory);
        RejectLinks(root);
        if (destination.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export the package outside the installed plugin directory.");
        var files = EnumerateBoundedFiles(root);
        var resources = ReadResourceManifest(root);
        var packagedResourceRoots = resources.ToDictionary(resource => resource.Id,
            resource => ContainedResourcePath(root, resource.Source), StringComparer.Ordinal);
        var archiveSources = files.Select(file =>
        {
            var resource = packagedResourceRoots.Values.Any(resourceRoot => IsSameOrDescendant(file, resourceRoot));
            return (Relative: Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), Path: file, Resource: resource);
        }).ToList();
        foreach (var resource in resources)
        {
            var packaged = packagedResourceRoots[resource.Id];
            if (System.IO.Directory.Exists(packaged)) continue;
            var baseRoot = ResourceRoot(productPaths, resource.Kind);
            var installedResource = ContainedResourcePath(baseRoot, resource.Destination);
            if (!System.IO.Directory.Exists(installedResource))
                throw new InvalidDataException($"Installed resource {resource.Id} is missing and cannot be exported.");
            foreach (var file in EnumerateBoundedFiles(installedResource))
            {
                var relative = Path.GetRelativePath(installedResource, file).Replace(Path.DirectorySeparatorChar, '/');
                archiveSources.Add((resource.Source.TrimEnd('/', '\\').Replace('\\', '/') + "/" + relative, file, true));
            }
        }
        if (archiveSources.Select(x => x.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archiveSources.Count)
            throw new InvalidDataException("Plugin resource entries overlap existing package files.");
        var identity = NativeDesktopPluginPackage.Inspect(plugin.ManifestPath);
        byte[]? settings = settingsMode switch
        {
            "defaults" => ReadOptionalSettings(Path.Combine(root, CerebralPluginPackageContract.DefaultSettingsFileName)),
            "current" => ReadOptionalSettings(Path.Combine(Path.GetFullPath(pluginDataRoot ?? DesktopPluginCatalog.DefaultDataDirectory), plugin.Id,
                CerebralPluginPackageContract.CurrentSettingsFileName)) ??
                ReadOptionalSettings(Path.Combine(root, CerebralPluginPackageContract.DefaultSettingsFileName)),
            _ => null
        };
        if (settingsMode != "none" && settings is null) settings = "{}"u8.ToArray();
        if (settings is not null) ValidateSettings(settings);
        var manifest = new CerebralPluginPackageManifest
        {
            Id = identity.Id, Name = identity.Name, Version = identity.Version, Description = identity.Description,
            SettingsMode = settingsMode, SettingsEntry = settings is null ? null : CerebralPluginPackageContract.SettingsEntry,
            Resources = resources
        };
        var requiredFacets = RequiredHostFacets(Path.Combine(root, identity.Assembly));
        if (requiredFacets.Count != 0)
        {
            manifest.SchemaVersion = CerebralPluginPackageContract.HostFacetsSchemaVersion;
            manifest.RequiredHostFacets = requiredFacets;
        }
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var metadataEntries = 1 + (settings is null ? 0 : 1);
        if (archiveSources.Count == 0 || archiveSources.Count + metadataEntries > CerebralPluginPackageContract.MaximumEntries)
            throw new InvalidDataException("The desktop plugin package contains an unsupported number of files.");
        ValidateArchivePaths(archiveSources.Select(x => "plugin/" + x.Relative));
        long expanded = manifestBytes.Length + (settings?.Length ?? 0);
        foreach (var file in archiveSources.Select(x => x.Path))
        {
            RejectLinks(file);
            expanded = checked(expanded + new FileInfo(file).Length);
            if (expanded > CerebralPluginPackageContract.MaximumExpandedBytes)
                throw new InvalidDataException($"The desktop plugin package exceeds {CerebralPluginPackageContract.MaximumExpandedBytes / 1048576:N0} MiB.");
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                using (var metadata = archive.CreateEntry(CerebralPluginPackageContract.ManifestName, CompressionLevel.Optimal).Open())
                    metadata.Write(manifestBytes);
                foreach (var file in archiveSources)
                {
                    var entry = archive.CreateEntry("plugin/" + file.Relative,
                        file.Resource ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var input = File.OpenRead(file.Path); using var target = entry.Open(); input.CopyTo(target);
                }
                if (settings is not null)
                {
                    using var target = archive.CreateEntry(CerebralPluginPackageContract.SettingsEntry, CompressionLevel.Optimal).Open();
                    target.Write(settings);
                }
            }
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IReadOnlyList<ZipArchiveEntry> ValidateEntries(IReadOnlyList<ZipArchiveEntry> files)
    {
        if (files.Count < 2)
            throw new InvalidDataException($"A plugin package must contain 2–{CerebralPluginPackageContract.MaximumEntries:N0} bounded entries.");
        if (files.Count(x => x.FullName.Equals(CerebralPluginPackageContract.ManifestName, StringComparison.Ordinal)) != 1 ||
            files.Count(x => x.FullName.Equals(CerebralPluginPackageContract.DesktopManifestEntry, StringComparison.Ordinal)) != 1 ||
            files.Any(x => x.FullName != CerebralPluginPackageContract.ManifestName && x.FullName != CerebralPluginPackageContract.SettingsEntry &&
                           !x.FullName.StartsWith("plugin/", StringComparison.Ordinal)))
            throw new InvalidDataException("Plugin packages contain only the root package manifest, one desktop payload, and optional portable settings.");
        return files;
    }

    private static List<CerebralPluginPackageResource> ReadResourceManifest(string pluginRoot) =>
        ReadResourceManifest(pluginRoot, out _);

    private static List<CerebralPluginPackageResource> ReadResourceManifest(string pluginRoot, out bool exists)
    {
        var path = Path.Combine(pluginRoot, CerebralPluginPackageContract.ResourceManifestFileName);
        exists = File.Exists(path);
        if (!exists) return [];
        RejectLinks(path);
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Plugin resource manifest exceeds 64 KiB.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
        var properties = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToArray() : [];
        if (document.RootElement.ValueKind != JsonValueKind.Object || properties.Length != 2 ||
            properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            !properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["schemaVersion", "resources"]) ||
            !document.RootElement.TryGetProperty("resources", out var resourceArray))
            throw new InvalidDataException("Plugin resource manifest requires only schemaVersion and resources.");
        ValidateResourceArray(resourceArray);
        var manifest = document.RootElement.Deserialize<CerebralPluginResourceManifest>(Json) ??
            throw new InvalidDataException("Plugin resource manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported plugin resource manifest version.");
        ValidateResourceDeclarations(manifest.Resources);
        return manifest.Resources;
    }

    private static CerebralPluginPackageManifest ReadManifest(BoundedContentZipArchive archive, ZipArchiveEntry entry)
    {
        var bytes = archive.ReadEntryBytes(entry, 64 * 1024);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The plugin package manifest must be an object.");
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) || !schemaElement.TryGetInt32(out var schema) ||
            schema is not (CerebralPluginPackageContract.LegacySchemaVersion or CerebralPluginPackageContract.SchemaVersion or CerebralPluginPackageContract.HostFacetsSchemaVersion))
            throw new InvalidDataException("The plugin package schema version is unsupported.");
        var expected = schema switch { 1 => ManifestFieldsV1, 2 => ManifestFieldsV2, _ => ManifestFieldsV3 };
        if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            properties.Any(x => !expected.Contains(x.Name)) ||
            expected.Except(properties.Select(x => x.Name), StringComparer.Ordinal).Any(x => x != "settingsEntry"))
            throw new InvalidDataException("The plugin package manifest contains missing, duplicate, or unsupported fields.");
        if (schema >= CerebralPluginPackageContract.SchemaVersion)
        {
            if (!document.RootElement.TryGetProperty("resources", out var resourceArray))
                throw new InvalidDataException("Plugin package schema 2 requires a resources array.");
            ValidateResourceArray(resourceArray);
        }
        if (schema == CerebralPluginPackageContract.HostFacetsSchemaVersion)
        {
            var facets = document.RootElement.GetProperty("requiredHostFacets");
            if (facets.ValueKind != JsonValueKind.Array || facets.GetArrayLength() is < 1 or > 8)
                throw new InvalidDataException("Plugin package schema 3 requires one to eight exact host facets.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in facets.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 96 } facet ||
                    !seen.Add(facet) || !DesktopPluginRuntime.SupportsRequiredHostFacet(facet))
                    throw new InvalidDataException("The plugin package requires a duplicate, invalid or unsupported host facet.");
            }
        }
        var value = document.RootElement.Deserialize<CerebralPluginPackageManifest>(Json) ?? throw new InvalidDataException("The plugin package manifest is empty.");
        if (value.SchemaVersion != schema || value.Contract != CerebralPluginPackageContract.Contract ||
            value.Kind != "desktop" || value.Payload != CerebralPluginPackageContract.DesktopManifestEntry ||
            value.SettingsMode is not ("none" or "defaults" or "current") ||
            value.SettingsEntry != (value.SettingsMode == "none" ? null : CerebralPluginPackageContract.SettingsEntry) ||
            schema == CerebralPluginPackageContract.LegacySchemaVersion && value.Resources is not null ||
            schema >= CerebralPluginPackageContract.SchemaVersion && value.Resources is null)
            throw new InvalidDataException("Unsupported plugin package contract, kind, payload, or settings mode.");
        NativeDesktopPluginPackage.ValidateId(value.Id);
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 100 || value.Description.Length > 2000 || !Version.TryParse(value.Version, out _))
            throw new InvalidDataException("The plugin package identity or version is invalid.");
        return value;
    }

    private static void ValidateResources(IReadOnlyList<CerebralPluginPackageResource> resources, string pluginRoot)
    {
        ValidateResourceDeclarations(resources);
        foreach (var resource in resources)
        {
            var source = ContainedResourcePath(pluginRoot, resource.Source);
            if (!System.IO.Directory.Exists(source)) throw new InvalidDataException($"Plugin resource directory is missing: {resource.Id}.");
        }
    }

    private static void ValidateResourceDeclarations(IReadOnlyList<CerebralPluginPackageResource> resources)
    {
        if (resources.Count > 32) throw new InvalidDataException("A plugin package may declare at most 32 resources.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();
        var destinations = new List<(string Kind, string Path)>();
        foreach (var resource in resources)
        {
            NativeDesktopPluginPackage.ValidateId(resource.Id);
            if (!ids.Add(resource.Id) || resource.Kind is not ("tool" or "model" or "data" or "shared"))
                throw new InvalidDataException("Plugin resource IDs must be unique and use a supported resource kind.");
            var source = NormalizeResourcePath(resource.Source);
            var destination = NormalizeResourcePath(resource.Destination);
            if (sources.Any(existing => RelativePathsOverlap(existing, source)))
                throw new InvalidDataException("Plugin resource source directories cannot overlap.");
            if (destinations.Any(existing => existing.Kind == resource.Kind && RelativePathsOverlap(existing.Path, destination)))
                throw new InvalidDataException("Plugin resource destinations cannot overlap within one product root.");
            sources.Add(source); destinations.Add((resource.Kind, destination));
        }
    }

    private static void ValidateResourceArray(JsonElement resources)
    {
        if (resources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Plugin resources must be an array.");
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each plugin resource must be an object.");
            var properties = resource.EnumerateObject().ToArray();
            if (properties.Length != ResourceFields.Count ||
                properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                !properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(ResourceFields) ||
                properties.Any(x => x.Value.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("Each plugin resource requires only string id, kind, source, and destination fields.");
        }
    }

    private static bool SameResources(IReadOnlyList<CerebralPluginPackageResource> left,
        IReadOnlyList<CerebralPluginPackageResource> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind &&
            pair.First.Source == pair.Second.Source && pair.First.Destination == pair.Second.Destination);

    private static string PrepareDesktopPayload(string temporary, string pluginRoot,
        IReadOnlyList<CerebralPluginPackageResource> resources)
    {
        if (resources.Count == 0) return pluginRoot;
        var resourceRoots = resources.Select(resource => ContainedResourcePath(pluginRoot, resource.Source)).ToArray();
        var desktopRoot = Path.Combine(temporary, "desktop");
        System.IO.Directory.CreateDirectory(desktopRoot);
        foreach (var file in EnumerateBoundedFiles(pluginRoot))
        {
            if (resourceRoots.Any(resourceRoot => IsSameOrDescendant(file, resourceRoot))) continue;
            var target = Path.Combine(desktopRoot, Path.GetRelativePath(pluginRoot, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
        return desktopRoot;
    }

    internal static string ResourceRoot(DesktopProductPaths paths, string kind)
    {
        var root = kind switch
        {
            "tool" => paths.ToolsDirectory,
            "model" => paths.ModelsDirectory,
            "data" => paths.DataDirectory,
            "shared" => paths.SharedDirectory,
            _ => throw new InvalidDataException($"Unsupported plugin resource kind: {kind}.")
        };
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException($"The configured {kind} resource root must be an absolute path.");
        return Path.GetFullPath(root);
    }

    internal static void ValidateResolvedDestinations(IReadOnlyList<string> destinations)
    {
        for (var left = 0; left < destinations.Count; left++)
            for (var right = left + 1; right < destinations.Count; right++)
                if (IsSameOrDescendant(destinations[left], destinations[right]) ||
                    IsSameOrDescendant(destinations[right], destinations[left]))
                    throw new InvalidDataException("Configured plugin resource destinations cannot overlap.");
    }

    internal static void ValidateTransactionDestinations(IReadOnlyList<string> destinations, IReadOnlyList<string> protectedPaths)
    {
        foreach (var destination in destinations)
            foreach (var protectedPath in protectedPaths)
                if (IsSameOrDescendant(destination, protectedPath) || IsSameOrDescendant(protectedPath, destination))
                    throw new InvalidDataException("Plugin resources cannot overlap code selection or portable-settings transaction paths.");
    }

    private static string NormalizeResourcePath(string relative)
    {
        _ = ContainedResourcePath(Path.GetFullPath(Path.GetTempPath()), relative);
        return string.Join('/', relative.Split(['/', '\\']));
    }

    private static bool RelativePathsOverlap(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return fullPath.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateBoundedFiles(string root)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (!System.IO.Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var directories = 0;
        while (pending.TryPop(out var directory))
        {
            if (directory.Depth > CerebralPluginPackageContract.MaximumDepth ||
                ++directories > CerebralPluginPackageContract.MaximumDirectories)
                throw new InvalidDataException("The plugin package contains too many or excessively deep directories.");
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory.Path))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Plugin packages cannot contain links or junctions.");
                if (System.IO.Directory.Exists(entry)) pending.Push((entry, directory.Depth + 1));
                else
                {
                    result.Add(entry);
                    if (result.Count > CerebralPluginPackageContract.MaximumEntries)
                        throw new InvalidDataException($"Plugin packages may contain at most {CerebralPluginPackageContract.MaximumEntries:N0} files.");
                }
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static void ValidateArchivePaths(IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            if (parts.Length > CerebralPluginPackageContract.MaximumDepth)
                throw new InvalidDataException($"Plugin package paths may contain at most {CerebralPluginPackageContract.MaximumDepth} segments.");
            for (var index = 1; index < parts.Length; index++) directories.Add(string.Join('/', parts.Take(index)));
        }
        if (directories.Count > CerebralPluginPackageContract.MaximumDirectories)
            throw new InvalidDataException($"Plugin packages may contain at most {CerebralPluginPackageContract.MaximumDirectories:N0} directories.");
    }

    internal static string ContainedResourcePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Plugin resource paths must be contained relative paths.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin resource path leaves its configured root.");
        return full;
    }

    private static byte[]? ReadOptionalSettings(string path)
    {
        if (!File.Exists(path)) return null;
        RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length > CerebralPluginPackageContract.MaximumSettingsBytes) throw new InvalidDataException("Portable plugin settings exceed 64 KiB.");
        return File.ReadAllBytes(path);
    }

    private static void ValidateSettings(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new InvalidDataException("Portable plugin settings must be one JSON object with unique fields.");
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Plugin packages cannot traverse a link or junction.");
    }
}
