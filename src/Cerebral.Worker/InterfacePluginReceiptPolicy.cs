using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

/// <summary>Validates owner-produced installed package coordinates without loading or executing native plugin code.</summary>
internal static class InterfacePluginReceiptPolicy
{
    internal const int MaximumMounts = WorkflowContract.MaximumPluginTestMounts;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static InterfacePluginReceipt Validate(InterfaceRequest request, InterfacePluginReceipt plugin)
    {
        if (request.Kind != "plugin")
            throw new ArgumentException("Only a successful native plugin presentation can publish an installed plugin receipt.");
        if (plugin.SchemaVersion != 1)
            throw new ArgumentException("Installed plugin receipts require schema version 1.");
        NativeDesktopPluginPackage.ValidateId(plugin.Id);
        if (string.IsNullOrWhiteSpace(plugin.Version) || plugin.Version.Length > 64 ||
            plugin.Version.Any(char.IsControl) || !Version.TryParse(plugin.Version, out _))
            throw new ArgumentException("An installed plugin receipt needs a bounded valid version.");
        if (string.IsNullOrWhiteSpace(plugin.Generation) || plugin.Generation.Length > 160)
            throw new ArgumentException("An installed plugin receipt needs a bounded immutable generation.");
        var prefix = Regex.Replace(plugin.Version, "[^A-Za-z0-9._-]", "-", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)) + "-";
        if (!plugin.Generation.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(plugin.Generation[prefix.Length..], "N", out var generationId) || generationId == Guid.Empty)
            throw new ArgumentException("The installed plugin generation must be its version followed by the exact catalog generation ID.");

        var manifestPath = CanonicalManifestPath(plugin.ManifestPath);
        var directory = Path.GetDirectoryName(manifestPath)!;
        var idDirectory = Path.GetDirectoryName(directory);
        var packagesDirectory = idDirectory is null ? null : Path.GetDirectoryName(idDirectory);
        var repository = packagesDirectory is null ? null : Path.GetDirectoryName(packagesDirectory);
        if (!string.Equals(Path.GetFileName(directory), plugin.Generation, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(idDirectory), plugin.Id, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(packagesDirectory), ".packages", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(repository))
            throw new ArgumentException("Installed plugin receipts must name .packages/<id>/<generation>/desktop-plugin.json.");

        if (plugin.Mounts is null || plugin.Mounts.Count > MaximumMounts)
            throw new ArgumentException("An installed plugin receipt has too many or no mount coordinates.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in plugin.Mounts)
        {
            var qualifiedPrefix = plugin.Id + ".";
            if (mount is null || mount.Slot != "sidebar" || string.IsNullOrWhiteSpace(mount.Id) ||
                mount.Id.Length > 129 || mount.Id.Any(char.IsControl) ||
                !mount.Id.StartsWith(qualifiedPrefix, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(mount.Id[qualifiedPrefix.Length..]) ||
                mount.Id.Length - qualifiedPrefix.Length > 64 || !ids.Add(mount.Id))
                throw new ArgumentException("Plugin control mounts must be unique bounded sidebar identifiers qualified by the installed plugin ID.");
        }

        try
        {
            // Inspect checks the exact manifest, its adjacent DLL and every existing ancestor for links.
            // It never loads the DLL. The original source coordinate is still the admitted owner request.
            var installed = NativeDesktopPluginPackage.Inspect(manifestPath);
            var source = NativeDesktopPluginPackage.Inspect(request.Path);
            if (installed.Id != plugin.Id || installed.Version != plugin.Version ||
                source.Id != plugin.Id || source.Version != plugin.Version)
                throw new InvalidDataException("The installed plugin receipt does not match the current installed and requested manifest identity.");
            var activePath = Path.Combine(repository, ".settings", plugin.Id + ".active.json");
            RejectLinks(activePath);
            using var input = File.OpenRead(activePath);
            if (input.Length is < 2 or > 4096)
                throw new InvalidDataException("The native plugin active-generation setting is invalid or too large.");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            var active = document.RootElement;
            if (active.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The native plugin active-generation setting is invalid.");
            var fields = active.EnumerateObject().ToArray();
            if (fields.Length != 1 || !fields[0].Name.Equals("Generation", StringComparison.OrdinalIgnoreCase) ||
                fields[0].Value.ValueKind != JsonValueKind.String || fields[0].Value.GetString() != plugin.Generation)
                throw new InvalidDataException("The installed plugin receipt is not the catalog's current active generation.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The installed plugin receipt references invalid package or active-generation metadata.", error);
        }
        // Detach the retained coordinates from a caller-owned list. Retrying a terminal receipt compares
        // these immutable values before touching source files that may have since been replaced or removed.
        return plugin with { ManifestPath = manifestPath, Mounts = plugin.Mounts.ToArray() };
    }

    internal static bool Equivalent(InterfacePluginReceipt? left, InterfacePluginReceipt? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.SchemaVersion != right.SchemaVersion || left.Id != right.Id || left.Version != right.Version ||
            left.Generation != right.Generation || !string.Equals(left.ManifestPath, right.ManifestPath, PathComparison) ||
            left.ControlTestsAllowed != right.ControlTestsAllowed || left.Mounts is null || right.Mounts is null ||
            left.Mounts.Count != right.Mounts.Count || left.Mounts.Count > MaximumMounts)
            return false;
        return left.Mounts.OrderBy(item => item?.Slot, StringComparer.Ordinal)
            .ThenBy(item => item?.Id, StringComparer.Ordinal)
            .SequenceEqual(right.Mounts.OrderBy(item => item?.Slot, StringComparer.Ordinal)
                .ThenBy(item => item?.Id, StringComparer.Ordinal));
    }

    private static string CanonicalManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new ArgumentException("An installed plugin manifest needs an exact bounded absolute path without traversal.");
        var canonical = Path.GetFullPath(path);
        if (!canonical.Equals(path, PathComparison) ||
            !Path.GetFileName(canonical).Equals(NativeDesktopPluginPackage.ManifestFileName, StringComparison.Ordinal))
            throw new ArgumentException("Use the exact canonical installed desktop-plugin.json path.");
        return canonical;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installed plugin receipt metadata cannot traverse a link or junction.");
    }
}
