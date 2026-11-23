using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.ChatIconsPlugin;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin.Tests;

/// <summary>Explicit, narrow Source filesystem admission. The caller owns subsequent runtime inspection.</summary>
internal static class SourceChatIconsInstaller
{
    private const string Repository = @"C:\Development\Pegasus-Cerebral";
    private static readonly string Artifacts = Path.Combine(Repository, ".tmp", "icon-native-consumer-20261008");
    private static readonly string ReviewedManifest = Path.Combine(Artifacts, "bin", "Cerebral.ChatIconsPlugin", "release", "desktop-plugin.json");
    private const string ResourceSource = "Resources/Icons/chat-v1";
    private const string ResourceDestination = "Icons/chat-v1";
    private const int MaximumFiles = 128;
    private const long MaximumBytes = 16 * 1024 * 1024;

    internal static void Run(string manifestArgument)
    {
        var phase = "not started";
        string? installedManifest = null;
        var admitted = false;
        var committed = false;
        var enabled = false;
        try
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(manifestArgument) ||
                !SamePath(manifestArgument, ReviewedManifest))
                throw new InvalidOperationException("Only the exact reviewed isolated Source manifest is accepted.");
            RejectLinks(ReviewedManifest);
            var sourceRoot = Path.GetDirectoryName(ReviewedManifest)!;
            var identity = ValidateNative(ReviewedManifest);
            ValidateResourceManifest(Path.Combine(sourceRoot, CerebralPluginPackageContract.ResourceManifestFileName));
            var localPackRoot = Path.Combine(sourceRoot, ResourceSource.Replace('/', Path.DirectorySeparatorChar));
            var pack = ValidatePack(localPackRoot);
            var sourceFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(ReviewedManifest);
            var resourceFingerprint = FileTreeFingerprint(localPackRoot);
            var assemblyHash = FileHash(Path.Combine(sourceRoot, identity.Assembly));

            // ForSource is a pure explicit binding, unlike Current/UseSource or installation defaults.
            // These derived paths are the tuple verified by root's actual Source instance.inspect.
            var binding = DesktopInstancePaths.ForSource(DesktopInstancePaths.DefaultSourceDataDirectory, Repository);
            var paths = binding.ProductPaths();
            var catalog = new DesktopPluginCatalog(binding.PluginDirectory);
            var resourceRoot = Path.Combine(paths.SharedDirectory, ResourceDestination.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(binding.DataDirectory); RejectLinks(catalog.RootDirectory); RejectLinks(resourceRoot);
            RequireAbsentPlugin(catalog);
            RequireCompatibleResource(resourceRoot, pack, resourceFingerprint);
            phase = "reviewed source validated";
            Print(new { phase, evidenceLevel = "read-only package metadata, entrypoint and content inspection",
                reviewedManifest = ReviewedManifest, id = identity.Id, version = identity.Version,
                sourceFingerprint, assemblySha256 = assemblyHash, packCatalogSha256 = pack.CatalogSha256,
                packContentSha256 = pack.ContentSha256, resourceFingerprint,
                sourceDefaultRoot = binding.DataDirectory, pluginDirectory = catalog.RootDirectory, productPaths = paths,
                sourceCalls = 0, nativeCodeLoadedByHelper = false });

            RejectLinks(Artifacts);
            if (!Directory.Exists(Artifacts)) throw new DirectoryNotFoundException("The reviewed task artifact directory is absent.");
            var packagePath = Path.Combine(Artifacts, "chat-icons-source-reviewed-" + Guid.NewGuid().ToString("N") + CerebralPluginPackageContract.Extension);
            if (File.Exists(packagePath) || Directory.Exists(packagePath)) throw new IOException("The new export path must be absent.");
            var info = new DesktopPluginInfo(identity.Id, identity.Name, identity.Version, identity.Description,
                false, sourceRoot, ReviewedManifest, null);
            CerebralPluginPackage.Export(info, packagePath, "none", paths);
            var packageHash = FileHash(packagePath);
            phase = "settings-free package exported";
            Print(new { phase, packagePath, packageSha256 = packageHash, bytes = new FileInfo(packagePath).Length,
                settingsMode = "none", sourceFilesChanged = false });

            using var prepared = CerebralPluginPackage.Prepare(packagePath);
            ValidatePrepared(prepared, sourceFingerprint, assemblyHash, pack, resourceFingerprint);
            var candidateFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prepared.DesktopManifestPath);
            phase = "prepared package admitted";
            Print(new { phase, candidateFingerprint, preparedManifest = prepared.DesktopManifestPath,
                resources = prepared.Inspection.Package.Resources, portableSettingsPresent = false,
                sourceFilesChanged = false, entrypointValidatedWithoutLoading = true });

            // Recheck preservation immediately before the reversible SDK resource publication.
            RequireAbsentPlugin(catalog);
            RequireCompatibleResource(resourceRoot, pack, resourceFingerprint);
            using (var resources = prepared.BeginResourceInstallation(paths))
            {
                resources.Publish();
                RequirePackIdentity(ValidatePack(resourceRoot), pack);
                if (FileTreeFingerprint(resourceRoot) != resourceFingerprint)
                    throw new InvalidDataException("Published shared resource bytes changed identity.");
                var installed = catalog.Install(prepared.DesktopManifestPath);
                installedManifest = installed.ManifestPath;
                admitted = true;
                if (installed.Enabled || installed.Error is not null || installed.Id != ChatIconsPlugin.PluginId || installed.Version != "1.0.0")
                    throw new InvalidDataException("The admitted native generation must remain disabled and valid.");
                _ = ValidateNative(installed.ManifestPath);
                if (NativeDesktopPluginPackage.ComputeFingerprint(installed.ManifestPath) != candidateFingerprint)
                    throw new InvalidDataException("The installed native payload differs from the prepared package.");
                RequirePackIdentity(ValidatePack(resourceRoot), pack);
                if (FileTreeFingerprint(resourceRoot) != resourceFingerprint)
                    throw new InvalidDataException("Shared resource integrity changed before admission commit.");
                phase = "native generation installed disabled";
                Print(new { phase, installedManifest, installedDirectory = installed.Directory, candidateFingerprint,
                    enabled = false, resourceRoot, resourceFingerprint, resourcesCommitted = false,
                    runtime = "not inspected" });
                resources.Commit();
                committed = true;
            }
            phase = "native and shared resource admission committed";
            Print(new { phase, installedManifest, resourceRoot, packCatalogSha256 = pack.CatalogSha256,
                packContentSha256 = pack.ContentSha256, resourceFingerprint, enabled = false });

            // Schema 1 has neither dependencies nor an exclusive group, so this enablement does not disable alternatives.
            catalog.SetEnabled(ChatIconsPlugin.PluginId, true);
            var final = catalog.List().Single(item => item.Id == ChatIconsPlugin.PluginId);
            if (!final.Enabled || final.Error is not null || !SamePath(final.ManifestPath, installedManifest!) ||
                NativeDesktopPluginPackage.ComputeFingerprint(final.ManifestPath) != candidateFingerprint)
                throw new InvalidDataException("Final Source enablement or immutable payload identity is unverified.");
            enabled = true;
            phase = "complete";
            Print(new { phase, evidenceLevel = "actual SDK package/resource filesystem admission and enablement",
                id = final.Id, version = final.Version, enabled, outputManifest = final.ManifestPath,
                outputDirectory = final.Directory, nativePayloadFingerprint = candidateFingerprint,
                assemblySha256 = assemblyHash, exportedPackage = packagePath, packageSha256 = packageHash,
                sharedResource = resourceRoot, resourceFingerprint, packCatalogSha256 = pack.CatalogSha256,
                packContentSha256 = pack.ContentSha256, portableSettingsImported = false,
                sourceCalls = 0, runtime = "not inspected; root must verify plugin load and surface separately",
                pickerInteraction = "not exercised", clipboardCalls = 0, modelCalls = 0 });
        }
        catch (Exception error)
        {
            // Never print arbitrary SDK exception bodies, and never auto-retry a potentially partial admission.
            Print(new { phase = "source-install-failed", lastCompletedPhase = phase, installedManifest,
                nativeGenerationAdmitted = admitted, resourcesCommitted = committed, enablementVerified = enabled,
                failureType = error.GetType().Name, automaticRetry = false, runtime = "not inspected",
                nextAction = "Review the printed phase proofs and current catalog before any retry." });
            throw;
        }
    }

    private static DesktopPluginManifest ValidateNative(string manifestPath)
    {
        var catalog = new DesktopPluginCatalog(Path.GetDirectoryName(manifestPath)!);
        var identity = catalog.Inspect(manifestPath);
        if (identity.SchemaVersion != 1 || identity.Id != ChatIconsPlugin.PluginId || identity.Name != "Chat Icons" ||
            identity.Version != "1.0.0" || identity.Assembly != "Cerebral.ChatIconsPlugin.dll" ||
            identity.EntryType != "Cerebral.ChatIconsPlugin.ChatIconsPlugin")
            throw new InvalidDataException("The reviewed native manifest identity or entrypoint changed.");
        var files = NativeDesktopPluginPackage.EnumeratePackageFiles(Path.GetDirectoryName(manifestPath)!);
        if (files.Count > MaximumFiles || files.Sum(path => new FileInfo(path).Length) > MaximumBytes)
            throw new InvalidDataException("The reviewed Chat Icons payload exceeds its bounded installer size.");
        NativeDesktopPluginPackage.ValidateEntryPoint(NativeDesktopPluginPackage.Inspect(manifestPath));
        return identity;
    }

    private static void ValidateResourceManifest(string path)
    {
        RejectLinks(path);
        if (new FileInfo(path).Length is < 1 or > 4096) throw new InvalidDataException("Unexpected resource declaration size.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
        var value = document.RootElement;
        ExactFields(value, "schemaVersion", "resources");
        if (value.GetProperty("schemaVersion").GetInt32() != 1 ||
            value.GetProperty("resources").ValueKind != JsonValueKind.Array || value.GetProperty("resources").GetArrayLength() != 1)
            throw new InvalidDataException("Only the reviewed single shared icon resource is accepted.");
        var resource = value.GetProperty("resources")[0];
        ExactFields(resource, "id", "kind", "source", "destination");
        if (resource.GetProperty("id").GetString() != "chat-icons" || resource.GetProperty("kind").GetString() != "shared" ||
            resource.GetProperty("source").GetString() != ResourceSource || resource.GetProperty("destination").GetString() != ResourceDestination)
            throw new InvalidDataException("The reviewed shared resource coordinates changed.");
    }

    private static void ValidatePrepared(PreparedCerebralPluginPackage prepared, string sourceFingerprint,
        string assemblyHash, AssetPack pack, string resourceFingerprint)
    {
        var package = prepared.Inspection.Package;
        var resources = package.Resources;
        if (package.SchemaVersion != 2 || package.Id != ChatIconsPlugin.PluginId || package.Version != "1.0.0" ||
            package.SettingsMode != "none" || package.SettingsEntry is not null || prepared.Settings is not null ||
            resources is not { Count: 1 } || resources[0].Id != "chat-icons" || resources[0].Kind != "shared" ||
            resources[0].Source != ResourceSource || resources[0].Destination != ResourceDestination)
            throw new InvalidDataException("Prepared package settings or shared resource admission changed.");
        var fullManifest = Path.Combine(prepared.Directory, "plugin", DesktopPluginCatalog.ManifestFileName);
        _ = ValidateNative(fullManifest);
        if (NativeDesktopPluginPackage.ComputeFingerprint(fullManifest) != sourceFingerprint)
            throw new InvalidDataException("Exported full package differs from the reviewed source file set.");
        var stagedResource = Path.Combine(prepared.Directory, "plugin", ResourceSource.Replace('/', Path.DirectorySeparatorChar));
        RequirePackIdentity(ValidatePack(stagedResource), pack);
        if (FileTreeFingerprint(stagedResource) != resourceFingerprint)
            throw new InvalidDataException("Prepared shared pack file set differs from its reviewed source.");
        _ = ValidateNative(prepared.DesktopManifestPath);
        if (FileHash(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "Cerebral.ChatIconsPlugin.dll")) != assemblyHash)
            throw new InvalidDataException("Prepared native assembly changed integrity.");
    }

    private static AssetPack ValidatePack(string root)
    {
        var pack = AssetPack.Load(root);
        if (pack.Id != ChatIconsPlugin.PluginId || pack.Version != "1.0.0")
            throw new InvalidDataException("The local shared pack identity changed.");
        ChatIconsPlugin.ValidatePickerPack(pack);
        return pack;
    }

    private static void RequirePackIdentity(AssetPack actual, AssetPack expected)
    {
        if (actual.CatalogSha256 != expected.CatalogSha256 || actual.ContentSha256 != expected.ContentSha256)
            throw new InvalidDataException("Shared pack content differs from the reviewed local pack.");
    }

    private static void RequireAbsentPlugin(DesktopPluginCatalog catalog)
    {
        if (catalog.List().Any(item => item.Id.Equals(ChatIconsPlugin.PluginId, StringComparison.OrdinalIgnoreCase)) ||
            Directory.Exists(Path.Combine(catalog.RootDirectory, ChatIconsPlugin.PluginId)) ||
            Directory.Exists(Path.Combine(catalog.RootDirectory, ".packages", ChatIconsPlugin.PluginId)))
            throw new IOException("An existing Chat Icons package or retained generation must not be overwritten.");
    }

    private static void RequireCompatibleResource(string root, AssetPack pack, string expectedFingerprint)
    {
        RejectLinks(root);
        if (File.Exists(root)) throw new IOException("The shared resource destination is not a directory.");
        if (!Directory.Exists(root)) return;
        RequirePackIdentity(ValidatePack(root), pack);
        if (FileTreeFingerprint(root) != expectedFingerprint)
            throw new IOException("The existing shared pack contains differing or additional files and must be preserved.");
    }

    private static string FileTreeFingerprint(string root)
    {
        var files = NativeDesktopPluginPackage.EnumeratePackageFiles(root);
        if (files.Count > MaximumFiles || files.Sum(path => new FileInfo(path).Length) > MaximumBytes)
            throw new InvalidDataException("The shared pack exceeds the installer file/byte bounds.");
        var identity = files.Select(path => new { name = Path.GetRelativePath(root, path).Replace('\\', '/'), sha256 = FileHash(path) })
            .OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, CerebralJson.Options))).ToLowerInvariant();
    }

    private static string FileHash(string path)
    {
        RejectLinks(path);
        using var input = File.OpenRead(path);
        var length = input.Length;
        if (length > MaximumBytes) throw new InvalidDataException("An installer input exceeds its byte bound.");
        var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (input.Length != length) throw new IOException("An installer input changed while hashing.");
        return digest;
    }

    private static void ExactFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A reviewed resource object is required.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("Reviewed resource fields must be exact and unique.");
    }

    private static bool SamePath(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installer paths cannot traverse link/reparse targets.");
    }

    private static void Print(object proof) => Console.WriteLine(JsonSerializer.Serialize(proof, CerebralJson.Options));
}
