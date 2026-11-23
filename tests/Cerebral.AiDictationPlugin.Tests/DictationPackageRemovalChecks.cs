using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

internal static partial class Program
{
    private sealed record RemovalFile(string Name, string Sha256);
    private sealed record RemovalStageReceipt(string Phase, string Root, string PackageSha256, string OriginalPackageDirectory,
        string SettingsSha256, string NoteSha256, IReadOnlyList<RemovalFile> PackageFiles, int Checks, bool CleanupPending);

    private static async Task RunPackageRemovalChecksAsync(Dispatcher dispatcher, string phase, string directory, string? expectedReceiptSha)
    {
        const string id = "pegasus.ai-dictation";
        const string version = "1.1.9";
        const string packageSha = "c6039cbf6c8aa739107cda718a2073d5e1f104fa39d52d0bd0309dbd8748f8a9";
        const string held = @"C:\Development\Pegasus-Cerebral\build\Configurator\S18PluginRemovalHeld";
        const string package = @"C:\Development\Pegasus-Cerebral\build\Configurator\S17InterfaceFollowupHeld\Packages\AI-Dictation-1.1.9-code-only.CerebralPlugin";
        string root = Path.GetFullPath(directory);
        string parent = Path.Combine(held, "Cases") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _) || phase is not ("retire" or "cleanup-reimport"))
            throw new ArgumentException("Use one selected phase in a distinct owned GUID fixture directory.");
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (Hash(package) != packageSha) throw new InvalidDataException("The qualified code-only archive changed.");
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void NewJson(string leaf, object value)
        {
            using var stream = new FileStream(Path.Combine(root, leaf), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, value); stream.Flush(true);
        }
        string catalogRoot = Path.Combine(root, "Catalog"), data = Path.Combine(root, "PluginData");
        string statePath = Path.Combine(data, id, "fixture-owner-settings.json"), note = Path.Combine(data, id, "fixture-note.txt");
        var paths = new DesktopProductPaths(Path.Combine(root, "Program"), Path.Combine(root, "Data"), Path.Combine(root, "User"),
            Path.Combine(root, "Projects"), Path.Combine(root, "Models"), Path.Combine(root, "Tools"), Path.Combine(root, "Shared"));
        var catalog = new DesktopPluginCatalog(catalogRoot);
        using var prepared = CerebralPluginPackage.Prepare(package);
        Check(prepared.Inspection.Package.Id == id && prepared.Inspection.Package.Version == version &&
            prepared.Settings is null && (prepared.Inspection.Package.Resources?.Count ?? 0) == 0, "Only the qualified settings-free code archive may be installed.");
        string fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prepared.DesktopManifestPath);

        if (phase == "retire")
        {
            if (expectedReceiptSha is not null || Directory.Exists(root)) throw new InvalidOperationException("The first stage requires a fresh fixture root.");
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var installed = prepared.InstallReviewed(catalog, paths, data, id, fingerprint);
            Check(installed.Version == version && !installed.Enabled, "A new reviewed installation must start disabled.");
            var preferences = new TestHost { DataDirectory = data };
            var document = AiDictationSettingsPolicy.Document(new AiDictationSettings { InputBoostDb = 4.5, SilenceWaitSeconds = 2.5 })
                with { SourcePackageVersion = version };
            await preferences.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
                new PluginSettingsTransferSaveRequest(new(document, 0, OwnerValidated: true)));
            File.WriteAllText(statePath, JsonSerializer.Serialize(preferences.Settings), new UTF8Encoding(false));
            File.WriteAllText(note, "Keep this unrelated plugin-owned note through removal and reinstall.", new UTF8Encoding(false));
            string stateSha = Hash(statePath), noteSha = Hash(note), memoryState = JsonSerializer.Serialize(preferences.Settings);
            var errors = new List<string>();
            var host = new DesktopPluginHostAdapter(dispatcher, errors.Add, data,
                serviceCommand: preferences.ExecuteServiceCommandAsync, productPaths: () => paths);
            using var runtime = new DesktopPluginRuntime(catalog, host, dispatcher);
            catalog.SetEnabled(id, true); runtime.LoadEnabled();
            Check(runtime.GetLoadedPackage(id)?.Version == version && runtime.Status.Single().Loaded, "The real native package must activate before retirement.");
            var selected = catalog.List().Single();
            var fileHashes = Directory.EnumerateFiles(selected.Directory, "*", SearchOption.AllDirectories)
                .Select(path => new RemovalFile(Path.GetRelativePath(selected.Directory, path), Hash(path))).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            bool refused = false;
            try { catalog.RemoveWithResult(id); } catch (InvalidOperationException) { refused = true; }
            Check(refused && catalog.List().Single() == selected, "Removing an enabled plugin must refuse without changing its selection.");
            catalog.SetEnabled(id, false); runtime.Disable(id);
            Check(runtime.GetLoadedPackage(id) is null && runtime.Panels.Count == 0, "Disable must detach the real native runtime and its panels.");
            DesktopPluginRemovalResult result;
            using (var heldManifest = new FileStream(selected.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = catalog.RemoveWithResult(id);
                Check(result.CleanupPending && !string.IsNullOrWhiteSpace(result.CleanupError), "Code in use must produce an explicit pending cleanup outcome.");
                Check(catalog.List().Count == 0, "A pending cleanup must still retire the catalog selection.");
                Check(fileHashes.All(file => File.Exists(Path.Combine(selected.Directory, file.Name)) && Hash(Path.Combine(selected.Directory, file.Name)) == file.Sha256),
                    "Pending native deletion must preserve all code files, including the manifest.");
                var cleanup = catalog.CleanupRemovedPackages(1);
                Check(cleanup.Results.Count == 1 && cleanup.Results[0].CleanupPending && catalog.List().Count == 0,
                    "A bounded cleanup while files remain in use must stay pending without reviving the plugin.");
            }
            Check(Hash(statePath) == stateSha && Hash(note) == noteSha && JsonSerializer.Serialize(preferences.Settings) == memoryState,
                "Retirement and deferred cleanup must preserve fixture owner settings and unrelated plugin data.");
            NewJson("stage-a.json", new RemovalStageReceipt(phase, root, packageSha, selected.Directory, stateSha, noteSha, fileHashes, checks, result.CleanupPending));
            Console.WriteLine(JsonSerializer.Serialize(new { phase, state = "passed", root, checks, cleanupPending = true,
                catalogRemoved = true, settingsPreserved = true, dataPreserved = true, permissionOwner = "strict in-memory fixture", runtime = "real native package", installedEffects = false }));
            return;
        }

        if (!Directory.Exists(root) || expectedReceiptSha is null || expectedReceiptSha.Length != 64 ||
            File.Exists(Path.Combine(root, "stage-b.json"))) throw new InvalidOperationException("The second stage requires the exact first-stage receipt and fresh output.");
        string receiptPath = Path.Combine(root, "stage-a.json");
        if (new FileInfo(receiptPath).Length > 65536 || Hash(receiptPath) != expectedReceiptSha) throw new InvalidDataException("The retained first-stage receipt changed.");
        var prior = JsonSerializer.Deserialize<RemovalStageReceipt>(File.ReadAllText(receiptPath)) ?? throw new InvalidDataException("Missing first-stage receipt.");
        Check(prior.Phase == "retire" && prior.Root == root && prior.PackageSha256 == packageSha && prior.CleanupPending,
            "The cleanup stage must join the exact retired fixture and package.");
        Check(catalog.List().Count == 0 && Hash(statePath) == prior.SettingsSha256 && Hash(note) == prior.NoteSha256,
            "A fresh process must observe the retirement and unchanged plugin data.");
        var cleared = catalog.CleanupRemovedPackages(1);
        Check(cleared.Results.Count == 1 && !cleared.Results[0].CleanupPending && !Directory.Exists(prior.OriginalPackageDirectory),
            "After the original process exits, the standard cleanup must reclaim its inactive package bytes.");
        bool enableRefused = false;
        try { catalog.SetEnabled(id, true); } catch (Exception error) when (error is InvalidOperationException or DirectoryNotFoundException) { enableRefused = true; }
        Check(enableRefused && catalog.List().Count == 0, "Enable cannot resurrect a logically removed package.");
        var reinstalled = prepared.InstallReviewed(catalog, paths, data, id, fingerprint);
        Check(reinstalled.Version == version && !reinstalled.Enabled && reinstalled.Directory != prior.OriginalPackageDirectory,
            "Reimport must select a fresh disabled generation without inheriting retired enablement.");
        Check(Hash(statePath) == prior.SettingsSha256 && Hash(note) == prior.NoteSha256, "Reimport must retain the original data files exactly.");
        var restored = JsonSerializer.Deserialize<PluginSettingsState>(File.ReadAllText(statePath)) ?? throw new InvalidDataException("Missing fixture settings.");
        Check(restored.Active?.Coordinate.PluginId == id && restored.Active.SourcePackageVersion == version, "Only the known fixture settings may seed the test owner.");
        var owner = new TestHost { DataDirectory = data };
        await owner.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
            new PluginSettingsTransferSaveRequest(new(restored.Active!, 0, OwnerValidated: true)));
        Check(JsonSerializer.Serialize(owner.Settings) == File.ReadAllText(statePath), "The test owner must restore the exact public fixture settings record.");
        var runtimeErrors = new List<string>();
        var restoredHost = new DesktopPluginHostAdapter(dispatcher, runtimeErrors.Add, data,
            serviceCommand: owner.ExecuteServiceCommandAsync, productPaths: () => paths);
        using var restoredRuntime = new DesktopPluginRuntime(catalog, restoredHost, dispatcher);
        restoredRuntime.LoadEnabled();
        Check(restoredRuntime.GetLoadedPackage(id) is null && restoredRuntime.Panels.Count == 0, "A reimported disabled package must not mount itself.");
        catalog.SetEnabled(id, true); restoredRuntime.LoadEnabled();
        Check(restoredRuntime.GetLoadedPackage(id)?.Version == version && restoredRuntime.Status.Single() is { Loaded: true, Error: null },
            "Explicit enable after reimport must activate the new native generation.");
        var panel = restoredRuntime.Panels.Single().View;
        T Find<T>(DependencyObject item, string automationId) where T : FrameworkElement
        {
            if (item is T match && AutomationProperties.GetAutomationId(match) == automationId) return match;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            { try { return Find<T>(child, automationId); } catch (KeyNotFoundException) { } }
            throw new KeyNotFoundException(automationId);
        }
        await WaitAsync(() => Find<Slider>(panel, "AiDictationSilenceWait").Value == 2.5);
        Check(Find<Slider>(panel, "AiDictationSilenceWait").Value == 2.5, "The reinstalled native panel must adopt the preserved saved preference.");
        Check(Hash(statePath) == prior.SettingsSha256 && Hash(note) == prior.NoteSha256 && JsonSerializer.Serialize(owner.Settings) == File.ReadAllText(statePath),
            "Activation after reimport must preserve all fixture settings and unrelated plugin data.");
        NewJson("stage-b.json", new { phase, state = "passed", root, checks, priorReceiptSha256 = expectedReceiptSha,
            packageSha256 = packageSha, removedCodeCleaned = true, reinstalledDisabled = true, enabledExplicitly = true,
            restoredPreference = 2.5, settingsPreserved = true, pluginDataPreserved = true,
            runtime = "real native package", serviceOwner = "strict in-memory fixture restored from controlled test-only JSON", installedEffects = false });
        Console.WriteLine(JsonSerializer.Serialize(new { phase, state = "passed", root, checks, restoredPreference = 2.5, installedEffects = false }));
    }
}
