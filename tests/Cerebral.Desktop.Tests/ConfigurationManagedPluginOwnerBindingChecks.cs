using System.IO;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Orchestrator;

namespace Cerebral.Desktop.Tests;

internal static class ConfigurationManagedPluginOwnerBindingChecks
{
    internal static (int Checks, bool ReparseChecked) Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CerebralManagedOwner-" + Guid.NewGuid().ToString("N"));
        var workerRoot = Path.Combine(root, "WorkerData");
        var mainRoot = Path.Combine(root, "MainState");
        var otherMainRoot = Path.Combine(root, "OtherMainState");
        var childRoot = Path.Combine(root, "ConfigurationState");
        var mainProgramRoot = Path.Combine(root, "MainProgram");
        var catalog = Path.Combine(mainRoot, "Plugins", "Desktop");
        var pluginData = Path.Combine(mainRoot, "PluginData");
        var externalCatalog = Path.Combine(root, "ExternalCatalog");
        var externalPluginData = Path.Combine(root, "ExternalPluginData");
        Directory.CreateDirectory(workerRoot);
        Directory.CreateDirectory(catalog);
        Directory.CreateDirectory(pluginData);
        Directory.CreateDirectory(externalCatalog);
        Directory.CreateDirectory(externalPluginData);
        Directory.CreateDirectory(otherMainRoot);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                checks++;
                return;
            }
            throw new InvalidOperationException(message);
        }
        static string[] Args(string worker, string desktop, string instance = "11111111111111111111111111111111") =>
            ["--attach-existing", "cerebral-managed-owner-fixture", instance, worker, desktop, "fixture-installation"];
        var reparseChecked = false;
        try
        {
            var main = ExistingWorkerDesktopOptions.FromArguments(Args(workerRoot, mainRoot))!;
            var child = ExistingWorkerDesktopOptions.FromArguments(Args(workerRoot, childRoot))!;
            var mainPaths = DesktopInstancePaths.UseAttachedExisting(mainRoot, mainProgramRoot);
            var legacy = ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(Args(workerRoot, childRoot));
            Check(legacy.Options == child && legacy.OwnerBinding is null,
                "The unchanged six-argument attach-existing binding must remain valid without a managed owner.");

            var binding = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, child, catalog, pluginData, mainPaths);
            var otherGenerationChild = ExistingWorkerDesktopOptions.FromArguments(
                Args(workerRoot, childRoot, "22222222222222222222222222222222"))!;
            Reject(() => ConfigurationManagedPluginOwnerBinding.CreateForMain(main, otherGenerationChild,
                    catalog, pluginData, mainPaths),
                "Main must reject a child bound to another Worker generation before launch.");
            Reject(() => ConfigurationManagedPluginOwnerBinding.CreateForMain(main, child,
                    catalog, pluginData, DesktopInstancePaths.ForSource(otherMainRoot, mainProgramRoot)),
                "Main's selected product paths must match the stated Main UI state.");
            var argv = Args(workerRoot, childRoot).ToList();
            binding.AppendArguments(argv);
            Check(argv.Count == 14 && argv[6] == ConfigurationManagedPluginOwnerBinding.ArgumentName,
                "A managed launch must add exactly one closed versioned suffix.");
            var parsed = ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(argv.ToArray());
            Check(parsed.Options == child && parsed.OwnerBinding is not null &&
                parsed.OwnerBinding.MainUiStateRoot == mainRoot && parsed.OwnerBinding.CatalogRoot == catalog &&
                parsed.OwnerBinding.PluginDataRoot == pluginData &&
                parsed.OwnerBinding.MainInstallDirectory == mainPaths.InstallDirectory &&
                parsed.OwnerBinding.MainBaseDirectory == mainPaths.BaseDirectory &&
                parsed.OwnerBinding.MainPortable == mainPaths.Portable &&
                parsed.OwnerBinding.OwnerBindingId == binding.OwnerBindingId,
                "The managed suffix must preserve the exact Worker, child UI, and Main plugin roots.");
            parsed.OwnerBinding.RequireForChild(child);
            checks++;
            Check(parsed.OwnerBinding.ProductPaths() == mainPaths.ProductPaths(),
                "Default managed product paths must match Main's selected pure path projection.");
            var projects = Path.Combine(root, "Projects");
            var models = Path.Combine(root, "Models");
            var tools = Path.Combine(root, "ConfiguredTools");
            var shared = Path.Combine(root, "Shared");
            Check(parsed.OwnerBinding.ProductPaths(projects, models, tools, shared) ==
                mainPaths.ProductPaths(projects, models, tools, shared),
                "Worker-provided path overrides must project identically in Main and Configuration.");
            var entry = Path.Combine(mainProgramRoot, "Cerebral.Integrator.dll");
            var legacyStart = ConfigurationLaunchEntry.BuildStartInfo(entry, child, "dotnet.exe");
            Check(legacyStart.ArgumentList.Count == 7 && legacyStart.ArgumentList[0] == entry &&
                legacyStart.ArgumentList.Skip(1).SequenceEqual(Args(workerRoot, childRoot)),
                "Legacy Configuration launch must retain the exact six existing arguments.");
            var managedStart = ConfigurationLaunchEntry.BuildStartInfo(entry, child, "dotnet.exe", binding);
            Check(managedStart.ArgumentList.Count == 15 && managedStart.ArgumentList[0] == entry &&
                managedStart.ArgumentList.Skip(1).SequenceEqual(argv),
                "Managed Configuration launch must append the exact closed suffix after the legacy arguments.");

            var equivalent = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, child,
                catalog.ToUpperInvariant() + Path.DirectorySeparatorChar, pluginData.ToUpperInvariant(), mainPaths);
            Check(equivalent.OwnerBindingId == binding.OwnerBindingId,
                "Equivalent Windows path casing and trailing separators must retain one owner identity.");
            var external = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, child,
                externalCatalog, externalPluginData, mainPaths);
            var externalArgs = Args(workerRoot, childRoot).ToList();
            external.AppendArguments(externalArgs);
            var parsedExternal = ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(externalArgs.ToArray());
            Check(parsedExternal.OwnerBinding?.CatalogRoot == externalCatalog &&
                parsedExternal.OwnerBinding.PluginDataRoot == externalPluginData &&
                parsedExternal.OwnerBinding.OwnerBindingId == external.OwnerBindingId,
                "Explicit Main-owned plugin roots may be separate local directories outside Main UI state.");
            var otherMain = ExistingWorkerDesktopOptions.FromArguments(Args(workerRoot, otherMainRoot))!;
            var otherBinding = ConfigurationManagedPluginOwnerBinding.CreateForMain(otherMain, child,
                Path.Combine(otherMainRoot, "Plugins", "Desktop"), Path.Combine(otherMainRoot, "PluginData"),
                DesktopInstancePaths.ForSource(otherMainRoot, mainProgramRoot));
            Check(otherBinding.OwnerBindingId != binding.OwnerBindingId &&
                child.GetApplicationId("Integrator") == parsed.Options.GetApplicationId("Integrator"),
                "The same Configuration UI key must distinguish different managed owners without a second key.");

            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(argv.Take(13).ToArray()),
                "A partial suffix must be rejected.");
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments([.. argv, "extra"]),
                "An extra or duplicate suffix must be rejected.");
            var unknown = argv.ToArray(); unknown[6] = "--managed-plugin-owner-v2";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(unknown),
                "An unknown suffix must be rejected.");
            var wrongHash = argv.ToArray(); wrongHash[13] = new string('0', 64);
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(wrongHash),
                "A wrong Worker fingerprint must be rejected.");
            var wrongGeneration = argv.ToArray(); wrongGeneration[2] = "22222222222222222222222222222222";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(wrongGeneration),
                "A different Worker generation must be rejected before effects.");
            var missing = argv.ToArray(); missing[8] = "";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(missing),
                "A missing managed root must be rejected.");
            var workerOverlap = argv.ToArray(); workerOverlap[7] = Path.Combine(workerRoot, "Main");
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(workerOverlap),
                "A managed Main root inside Worker data must be rejected.");
            var catalogWorkerOverlap = argv.ToArray(); catalogWorkerOverlap[8] = Path.Combine(workerRoot, "Plugins");
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(catalogWorkerOverlap),
                "A managed catalog inside Worker data must be rejected.");
            var childOverlap = argv.ToArray(); childOverlap[7] = childRoot;
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(childOverlap),
                "A Main root overlapping Configuration UI state must be rejected.");
            var dataChildOverlap = argv.ToArray(); dataChildOverlap[9] = Path.Combine(childRoot, "PluginData");
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(dataChildOverlap),
                "Managed plugin data inside Configuration UI state must be rejected.");
            var normalUserRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pegasus", "Cerebral");
            var selectedUserRoot = Path.Combine(root, "RegisteredUsers", "SyntheticOwner");
            var overlapMethod = typeof(ExistingWorkerDesktopOptions).GetMethod("OverlapsNormalUserData",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            bool OverlapsNormal(string candidate) => (bool)overlapMethod.Invoke(null,
                new object[] { candidate, selectedUserRoot })!;
            Check(OverlapsNormal(normalUserRoot) && OverlapsNormal(selectedUserRoot) &&
                OverlapsNormal(Path.Combine(selectedUserRoot, "Ui")) &&
                OverlapsNormal(Path.GetDirectoryName(selectedUserRoot)!) && !OverlapsNormal(mainRoot),
                "Private owner roots reject both legacy and selected ordinary user data without reading registration.");
            var mainNormalUser = externalArgs.ToArray(); mainNormalUser[7] = normalUserRoot;
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(mainNormalUser),
                "The managed parser must reject a global Main UI root even with separate private catalog and data.");
            var catalogNormalUser = argv.ToArray(); catalogNormalUser[8] = Path.Combine(normalUserRoot, "Plugins", "Desktop");
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(catalogNormalUser),
                "A managed catalog under normal Cerebral user data must be rejected.");
            var dataNormalUserAncestor = argv.ToArray(); dataNormalUserAncestor[9] = Path.GetDirectoryName(normalUserRoot)!;
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(dataNormalUserAncestor),
                "Managed plugin data that contains normal Cerebral user data must be rejected.");
            var catalogOverlap = argv.ToArray(); catalogOverlap[8] = pluginData;
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(catalogOverlap),
                "The catalog and plugin data roots must not overlap.");
            var unc = argv.ToArray(); unc[7] = @"\\server\share\Main";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(unc),
                "UNC managed roots must be rejected.");
            var ads = argv.ToArray(); ads[8] = catalog + ":stream";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(ads),
                "Alternate stream managed roots must be rejected.");
            var badInstall = argv.ToArray(); badInstall[10] = @"\\server\share\MainProgram";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(badInstall),
                "A nonlocal Main installation root must be rejected.");
            var badPortable = argv.ToArray(); badPortable[12] = "maybe";
            Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(badPortable),
                "A malformed Main portability value must be rejected.");

            // Link creation depends on this host's Windows developer or symlink permission.
            var link = Path.Combine(root, "LinkedMain");
            try { Directory.CreateSymbolicLink(link, mainRoot); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
            if (Directory.Exists(link))
            {
                var linked = argv.ToArray(); linked[7] = link;
                Reject(() => ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(linked),
                    "A linked managed root must be rejected.");
                reparseChecked = true;
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return (checks, reparseChecked);
    }
}
