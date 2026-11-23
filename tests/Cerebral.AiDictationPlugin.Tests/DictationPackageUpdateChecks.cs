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
    private static async Task RunPackageUpdateChecksAsync(Dispatcher dispatcher)
    {
        const string id = "pegasus.ai-dictation";
        string held = @"C:\Development\Pegasus-Cerebral\build\Configurator\S17InterfaceFollowupHeld";
        string baseline = @"C:\Development\Pegasus-Cerebral\build\Installation\Alpha145f-S16\win-x64\Plugins\Desktop\pegasus.ai-dictation";
        string root = Path.Combine(held, "Checks", "PackageUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine(JsonSerializer.Serialize(new { state = "INTENT", root, scope = "disposable plugin catalog; no installed update" }));
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        static string H(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static void CopyCode(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string leaf in new[] { "desktop-plugin.json", "Cerebral.AiDictationPlugin.dll", "Cerebral.AiDictationPlugin.deps.json", "Cerebral.Speech.dll" })
                File.Copy(Path.Combine(from, leaf), Path.Combine(to, leaf), overwrite: false);
        }
        var paths = new DesktopProductPaths(Path.Combine(root, "Program"), Path.Combine(root, "Data"), Path.Combine(root, "User"),
            Path.Combine(root, "Projects"), Path.Combine(root, "Models"), Path.Combine(root, "Tools"), Path.Combine(root, "Shared"));
        string data = Path.Combine(root, "PluginData");
        string oldSource = Path.Combine(root, "OldCode"), newSource = Path.Combine(root, "NewCode");
        CopyCode(baseline, oldSource);
        CopyCode(AppContext.BaseDirectory, newSource);
        var catalog = new DesktopPluginCatalog(Path.Combine(root, "Catalog"));
        var installed = catalog.Install(Path.Combine(oldSource, "desktop-plugin.json"));
        Check(installed.Version == "1.1.8", "The fixture baseline must be the exact S16 dictation version.");
        catalog.SetEnabled(id, true);
        var preferences = new TestHost { DataDirectory = data };
        var saved = AiDictationSettingsPolicy.Document(new AiDictationSettings { InputBoostDb = 4.5, SilenceWaitSeconds = 2.5 })
            with { SourcePackageVersion = "1.1.8" };
        await preferences.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
            new PluginSettingsTransferSaveRequest(new(saved, 0, OwnerValidated: true)));
        string originalState = JsonSerializer.Serialize(preferences.Settings);
        string note = Path.Combine(data, id, "fixture-note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, "Retain existing plugin-owned data across code updates.", new UTF8Encoding(false));
        string noteHash = H(note);
        var errors = new List<string>();
        var host = new DesktopPluginHostAdapter(dispatcher, errors.Add, data,
            serviceCommand: preferences.ExecuteServiceCommandAsync, productPaths: () => paths);
        using var runtime = new DesktopPluginRuntime(catalog, host, dispatcher);
        runtime.LoadEnabled();
        Check(runtime.Status.Single() is { Loaded: true, Error: null }, "The exact S16 plugin must activate before updating.");
        Check(runtime.GetLoadedPackage(id)?.Version == "1.1.8", "The prior native generation must be selected.");

        // The supported archive format permits packages with no resource manifest.
        // This local update intentionally includes code, no model/tool assets, and no settings.
        var sourceCatalog = new DesktopPluginCatalog(Path.Combine(root, "SourceCatalog"));
        var candidate = sourceCatalog.Install(Path.Combine(newSource, "desktop-plugin.json"));
        string packages = Path.Combine(held, "Packages");
        Directory.CreateDirectory(packages);
        string package = Path.Combine(root, "AI-Dictation-1.1.9-code-only.CerebralPlugin");
        CerebralPluginPackage.Export(candidate, package, "none", paths, data);
        using var prepared = CerebralPluginPackage.Prepare(package);
        Check(prepared.Inspection.Package.Version == "1.1.9" && prepared.Settings is null &&
            (prepared.Inspection.Package.Resources?.Count ?? 0) == 0, "The update must contain no settings or assets.");
        string fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prepared.DesktopManifestPath);
        var reviewed = catalog.List().Single();
        await prepared.UpdateReviewedAsync(catalog, runtime, paths, data, id, fingerprint, () => true,
            () => Check(runtime.GetLoadedPackage(id)?.Version == "1.1.9" && runtime.Status.Single().Error is null,
                "The new native generation must activate before acceptance."), CancellationToken.None, reviewed);
        Check(catalog.List().Single() is { Version: "1.1.9", Enabled: true }, "The update must preserve enablement.");
        Check(H(note) == noteHash && JsonSerializer.Serialize(preferences.Settings) == originalState,
            "A code-only update must retain the saved preference record and plugin-owned file exactly.");
        var panel = runtime.Panels.Single().View;
        T Find<T>(DependencyObject item, string automationId) where T : FrameworkElement
        {
            if (item is T match && AutomationProperties.GetAutomationId(match) == automationId) return match;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            {
                try { return Find<T>(child, automationId); } catch (KeyNotFoundException) { }
            }
            throw new KeyNotFoundException(automationId);
        }
        await WaitAsync(() => Find<Slider>(panel, "AiDictationSilenceWait").Value == 2.5);
        Check(Find<Slider>(panel, "AiDictationSilenceWait").Value == 2.5, "The new panel must adopt existing saved values.");

        string invalidSource = Path.Combine(root, "InvalidCode");
        CopyCode(AppContext.BaseDirectory, invalidSource);
        string invalidManifest = Path.Combine(invalidSource, "desktop-plugin.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(invalidManifest))!;
        manifest["version"] = JsonSerializer.SerializeToElement("1.1.10");
        manifest["entryType"] = JsonSerializer.SerializeToElement("Fixture.MissingEntryType");
        File.WriteAllText(invalidManifest, JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
        var invalidCatalog = new DesktopPluginCatalog(Path.Combine(root, "InvalidSourceCatalog"));
        var invalid = invalidCatalog.Install(invalidManifest);
        string badPackage = Path.Combine(root, "AI-Dictation-1.1.10-invalid.CerebralPlugin");
        CerebralPluginPackage.Export(invalid, badPackage, "none", paths, data);
        using var rejected = CerebralPluginPackage.Prepare(badPackage);
        bool refused = false;
        try
        {
            await rejected.UpdateReviewedAsync(catalog, runtime, paths, data, id,
                NativeDesktopPluginPackage.ComputeFingerprint(rejected.DesktopManifestPath), () => true, () => { },
                CancellationToken.None, catalog.List().Single());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            refused = true;
            Console.WriteLine("Expected invalid activation refusal: " + error.Message);
        }
        Check(refused, "A package with a missing entry type must fail activation.");
        Check(catalog.List().Single() is { Version: "1.1.9", Enabled: true } &&
            runtime.GetLoadedPackage(id)?.Version == "1.1.9" && runtime.Status.Single().Error is null,
            "Failed activation must restore the selected native generation and runtime.");
        Check(H(note) == noteHash && JsonSerializer.Serialize(preferences.Settings) == originalState,
            "Activation recovery must retain saved settings and unrelated plugin data.");
        string candidatePackage = Path.Combine(packages, "AI-Dictation-1.1.9-code-only.CerebralPlugin");
        File.Copy(package, candidatePackage, overwrite: false);
        Check(H(candidatePackage) == H(package), "The held package must match the exact rehearsed archive.");
        var result = new
        {
            state = "passed", checks, root, package = candidatePackage, originalPackage = package, packageSha256 = H(package),
            baseline = "1.1.8", accepted = "1.1.9", refused = "1.1.10", runtimeRecovered = true,
            settingsPreserved = true, pluginDataPreserved = true, assetsChanged = false, installedEffects = false,
            ownerCurrent = "test-only true predicate; live ownership not proven", serviceOwner = "strict in-memory fixture"
        };
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(result), new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}

