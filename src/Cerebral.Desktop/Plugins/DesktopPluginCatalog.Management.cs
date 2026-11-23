using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

/// <summary>Signals that an exact management mutation crossed its first persistent write.
/// Callers must reload and inspect the catalog rather than retrying the request.</summary>
public sealed class DesktopPluginManagementMutationException : IOException
{
    public bool StateMayHaveChanged => true;

    internal DesktopPluginManagementMutationException(Exception inner)
        : base("The native plugin enablement update may have changed catalog state. Reload and inspect the catalog before taking another action.", inner) { }
}

public sealed partial class DesktopPluginCatalog
{
    private const int MaximumManagementEntries = 4096;
    private const int MaximumManagementProjection = 100;
    private const int MaximumManagementQueryCharacters = 100;
    private const int MaximumChangedPluginIds = 100;

    /// <summary>Returns a path-free, bounded host observation. Its fingerprint always covers the
    /// complete installed catalog; query and runtime load observations affect only projection.</summary>
    public NativePluginCatalogSnapshot GetManagementSnapshot(string? query = null,
        IReadOnlyCollection<DesktopPluginLoadStatus>? runtimeStatus = null)
    {
        query = ValidateManagementQuery(query);
        var loaded = ValidateRuntimeStatus(runtimeStatus);
        if (!Directory.Exists(RootDirectory))
            return ProjectManagementSnapshot([], loaded, query, preferredId: null);
        using var observation = BeginObservation();
        return ProjectManagementSnapshot(ReadManagementCatalog(), loaded, query, preferredId: null);
    }

    /// <summary>Changes one exact installed selection after a full-catalog compare-and-swap.
    /// Dependency, exclusivity and package admission remain the catalog's existing authority.</summary>
    public NativePluginManagementResult SetEnabledExact(string expectedCatalogFingerprint,
        NativePluginSelection selection, bool enabled,
        IReadOnlyCollection<DesktopPluginLoadStatus>? runtimeStatus = null)
    {
        RequireManagementHash(expectedCatalogFingerprint, nameof(expectedCatalogFingerprint));
        ArgumentNullException.ThrowIfNull(selection);
        ValidateId(selection.Id);
        if (selection.Generation.Length is < 1 or > 128 || selection.Generation.Any(char.IsControl))
            throw new ArgumentException("Use the exact bounded generation returned by installed plugin list.", nameof(selection));
        RequireManagementHash(selection.PackageFingerprint, nameof(selection));
        var loaded = ValidateRuntimeStatus(runtimeStatus);

        using var publication = BeginPublication();
        var before = ReadManagementCatalog();
        var fingerprint = ComputeManagementCatalogFingerprint(before);
        if (!string.Equals(fingerprint, expectedCatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed plugin catalog changed. Inspect it again before changing enablement.");
        if (before.Any(item => item.Error is not null))
            throw new InvalidDataException("Resolve invalid installed native plugin state before changing enablement.");
        var target = before.SingleOrDefault(item => item.Id == selection.Id)
            ?? throw new DirectoryNotFoundException("The selected native plugin is not installed.");
        if (target.Enabled != selection.ExpectedEnabled ||
            !string.Equals(target.Generation, selection.Generation, StringComparison.Ordinal) ||
            !string.Equals(target.PackageFingerprint, selection.PackageFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected native plugin identity or enablement changed. Inspect it again before changing enablement.");

        string[] conflicts = [];
        if (enabled)
        {
            ValidateEnablement(selection.Id);
            conflicts = FindEnabledExclusiveAlternatives(selection.Id);
            foreach (var conflict in conflicts) RequireNoEnabledDependents(conflict);
        }
        else RequireNoEnabledDependents(selection.Id);

        var changed = conflicts.Concat(target.Enabled == enabled ? [] : [selection.Id])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (changed.Prepend(selection.Id).Distinct(StringComparer.Ordinal).Count() > MaximumChangedPluginIds)
            throw new InvalidOperationException($"This enablement change affects more than {MaximumChangedPluginIds} native plugins. Resolve the catalog configuration before retrying.");

        var writeStarted = false;
        try
        {
            foreach (var conflict in conflicts)
            {
                writeStarted = true;
                WriteEnablement(conflict, false);
            }
            if (target.Enabled != enabled)
            {
                writeStarted = true;
                WriteEnablement(selection.Id, enabled);
            }
            var after = ReadManagementCatalog();
            return new(ProjectManagementSnapshot(after, loaded, query: null, preferredId: selection.Id), changed);
        }
        catch (Exception error) when (writeStarted && error is not DesktopPluginManagementMutationException)
        {
            throw new DesktopPluginManagementMutationException(error);
        }
    }

    private string[] FindEnabledExclusiveAlternatives(string id)
    {
        // List() nests on this thread's publication lease; it does not open a competing reader.
        var installed = List();
        var selected = installed.Single(item => item.Id == id);
        var manifest = Inspect(selected.ManifestPath);
        if (string.IsNullOrWhiteSpace(manifest.ExclusiveGroup)) return [];
        return installed.Where(item => item.Enabled && item.Error is null && item.Id != id)
            .Where(item => string.Equals(Inspect(item.ManifestPath).ExclusiveGroup,
                manifest.ExclusiveGroup, StringComparison.Ordinal))
            .Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<ManagementCatalogItem> ReadManagementCatalog()
    {
        CheckAncestors(RootDirectory);
        var ids = EnumerateManagementIds();
        if (ids.Count > MaximumManagementEntries)
            throw new InvalidDataException($"The native plugin catalog exceeds {MaximumManagementEntries} installed identities.");
        return ids.Select(ReadManagementItem).ToArray();
    }

    private IReadOnlyList<string> EnumerateManagementIds()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var directories = Directory.EnumerateDirectories(RootDirectory).Take(MaximumManagementEntries + 1).ToArray();
        if (directories.Length > MaximumManagementEntries)
            throw new InvalidDataException($"The native plugin catalog exceeds {MaximumManagementEntries} installed identities.");
        foreach (var id in directories.Select(Path.GetFileName).OfType<string>().Where(id => !id.StartsWith('.')))
            ids.Add(id);
        var settings = Path.Combine(RootDirectory, ".settings");
        if (Directory.Exists(settings))
        {
            var selections = Directory.EnumerateFiles(settings, "*" + ActiveSuffix, SearchOption.TopDirectoryOnly)
                .Take(MaximumManagementEntries + 1).ToArray();
            if (selections.Length > MaximumManagementEntries)
                throw new InvalidDataException($"The native plugin catalog exceeds {MaximumManagementEntries} installed identities.");
            foreach (var active in selections)
            {
                var id = Path.GetFileName(active)[..^ActiveSuffix.Length];
                try { ValidateId(id); ids.Add(id); } catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
            }
        }
        if (ids.Count > MaximumManagementEntries)
            throw new InvalidDataException($"The native plugin catalog exceeds {MaximumManagementEntries} installed identities.");
        return ids.Where(id => !IsRemoved(id)).Order(StringComparer.Ordinal).ToArray();
    }

    private ManagementCatalogItem ReadManagementItem(string id)
    {
        ValidateId(id);
        var enabledPath = Path.Combine(RootDirectory, ".settings", id + ".json");
        var activePath = ActivePath(id);
        var enabledState = ReadBoundedStateFingerprint(enabledPath, 4096);
        var activeState = ReadBoundedStateFingerprint(activePath, 4096);
        var enabled = false;
        string? generation = null;
        var name = id;
        var version = "";
        string? packageFingerprint = null;
        string? error = null;
        string packageState = "unavailable";

        try { enabled = ReadEnabled(id); }
        catch (Exception exception) when (IsManagementStorageError(exception))
        { error = "The native plugin enablement setting is invalid."; }

        ActiveGeneration? active = null;
        try
        {
            active = ReadActive(id);
            generation = active?.Generation;
            if (generation is { } selected && (selected.Length > 128 || selected.Any(char.IsControl)))
            {
                generation = null;
                error ??= "The native plugin active-generation setting is invalid.";
            }
        }
        catch (Exception exception) when (IsManagementStorageError(exception))
        { error ??= "The native plugin active-generation setting is invalid."; }

        string? directory = null;
        if (error is null || generation is not null || active is null)
        {
            try
            {
                directory = active is null
                    ? Path.Combine(RootDirectory, id)
                    : ContainedPath(PackageStore(id), active.Generation);
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
                CheckAncestors(directory);
                var manifestPath = Path.Combine(directory, ManifestFileName);
                var manifest = Inspect(manifestPath);
                if (manifest.Id != id || manifest.Name.Any(char.IsControl) || manifest.Version.Length > 100 ||
                    manifest.Version.Any(char.IsControl))
                    throw new InvalidDataException("The installed native plugin identity is invalid.");
                name = manifest.Name;
                version = manifest.Version;
                packageFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(manifestPath);
                packageState = packageFingerprint;
            }
            catch (Exception exception) when (IsManagementStorageError(exception))
            {
                error ??= "The installed native plugin package is invalid or unavailable.";
                if (directory is not null && Directory.Exists(directory))
                {
                    try { packageState = ComputeRawPackageStateFingerprint(directory) + ":" + HashDiagnostic(exception); }
                    catch (Exception rawError) when (IsManagementStorageError(rawError))
                    { packageState = HashDiagnostic(exception) + ":" + HashDiagnostic(rawError); }
                }
                else packageState = HashDiagnostic(exception);
            }
        }

        return new(id, name, version, generation, packageFingerprint, enabled, error,
            enabledState, activeState, packageState);
    }

    private static bool IsManagementStorageError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException;

    private static string ReadBoundedStateFingerprint(string path, int maximumBytes)
    {
        if (!File.Exists(path)) return "missing";
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maximumBytes) return "oversized:" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return "unreadable:" + HashDiagnostic(exception); }
    }

    private static string ComputeRawPackageStateFingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("pegasus.cerebral.native-desktop-package\0raw-state-v1\0"u8);
        Span<byte> number = stackalloc byte[8];
        var files = PackageFiles(directory).Select(path =>
                (Path: path, Name: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(number, files.Length); hash.AppendData(number);
        foreach (var file in files)
        {
            AppendHashText(hash, file.Name);
            using var input = File.OpenRead(file.Path);
            var length = input.Length;
            BinaryPrimitives.WriteInt64LittleEndian(number, length); hash.AppendData(number);
            hash.AppendData(SHA256.HashData(input));
            if (input.Length != length)
                throw new IOException("The installed native plugin package changed while it was being observed.");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashDiagnostic(Exception exception) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            exception.GetType().FullName + "\0" + exception.Message))).ToLowerInvariant();

    private static NativePluginCatalogSnapshot ProjectManagementSnapshot(
        IReadOnlyList<ManagementCatalogItem> items,
        IReadOnlyDictionary<string, DesktopPluginLoadStatus> loaded,
        string? query,
        string? preferredId)
    {
        var fingerprint = ComputeManagementCatalogFingerprint(items);
        IEnumerable<ManagementCatalogItem> matches = items;
        if (query is not null)
            matches = matches.Where(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        var ordered = matches.OrderBy(item => item.Id == preferredId ? 0 : item.Id == query ? 1 : 2)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var rows = ordered.Take(MaximumManagementProjection).Select(item =>
        {
            loaded.TryGetValue(item.Id, out var status);
            var statusError = status?.Error is null ? null : "The native plugin did not load in the current desktop runtime.";
            return new NativePluginCatalogEntry(item.Id, item.Name, item.Version,
                item.Generation, item.PackageFingerprint, item.Enabled, status?.Loaded == true,
                item.Error ?? statusError);
        }).ToArray();
        return new(fingerprint, rows, Math.Max(0, ordered.Length - rows.Length));
    }

    private static string ComputeManagementCatalogFingerprint(IReadOnlyList<ManagementCatalogItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("pegasus.cerebral.native-plugin-catalog\0identity-v1\0"u8);
        Span<byte> number = stackalloc byte[8];
        var ordered = items.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(number, ordered.Length); hash.AppendData(number);
        foreach (var item in ordered)
        {
            AppendHashText(hash, item.Id);
            AppendHashText(hash, item.Version);
            hash.AppendData(item.Enabled ? [byte.MaxValue] : [byte.MinValue]);
            AppendHashText(hash, item.Generation);
            AppendHashText(hash, item.PackageFingerprint);
            AppendHashText(hash, item.Error);
            AppendHashText(hash, item.EnablementState);
            AppendHashText(hash, item.ActiveState);
            AppendHashText(hash, item.PackageState);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashText(IncrementalHash hash, string? value)
    {
        Span<byte> number = stackalloc byte[8];
        if (value is null)
        {
            BinaryPrimitives.WriteInt64LittleEndian(number, -1); hash.AppendData(number); return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt64LittleEndian(number, bytes.Length); hash.AppendData(number); hash.AppendData(bytes);
    }

    private static string? ValidateManagementQuery(string? query)
    {
        if (query is null) return null;
        if (query.Length > MaximumManagementQueryCharacters || query.Any(char.IsControl))
            throw new ArgumentException("Installed plugin search accepts at most 100 characters without control characters.", nameof(query));
        query = query.Trim();
        return query.Length == 0 ? null : query;
    }

    private static IReadOnlyDictionary<string, DesktopPluginLoadStatus> ValidateRuntimeStatus(
        IReadOnlyCollection<DesktopPluginLoadStatus>? runtimeStatus)
    {
        if (runtimeStatus is null) return new Dictionary<string, DesktopPluginLoadStatus>(StringComparer.Ordinal);
        if (runtimeStatus.Count > MaximumManagementEntries)
            throw new ArgumentException("The desktop runtime observation exceeds the installed plugin bound.", nameof(runtimeStatus));
        var result = new Dictionary<string, DesktopPluginLoadStatus>(StringComparer.Ordinal);
        foreach (var status in runtimeStatus)
        {
            ArgumentNullException.ThrowIfNull(status);
            ValidateId(status.Id);
            if (!result.TryAdd(status.Id, status))
                throw new ArgumentException("The desktop runtime observation contains duplicate plugin identities.", nameof(runtimeStatus));
        }
        return result;
    }

    private static void RequireManagementHash(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("Use the exact lowercase SHA-256 fingerprint returned by installed plugin list.", parameter);
    }

    private sealed record ManagementCatalogItem(string Id, string Name, string Version,
        string? Generation, string? PackageFingerprint, bool Enabled, string? Error,
        string EnablementState, string ActiveState, string PackageState);
}
