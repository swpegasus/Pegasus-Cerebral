using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class PluginPackageRetirementTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Package retirement: " + message); checks++; }
        bool Fails(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            { return true; }
        }
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.PackageRetirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Package(string id, string version = "1.0.0", bool dependent = false)
            {
                var source = Path.Combine(root, "sources", id, version); Directory.CreateDirectory(source);
                File.Copy(typeof(NativePluginManagementFixturePlugin).Assembly.Location, Path.Combine(source, "Fixture.dll"), true);
                File.WriteAllText(Path.Combine(source, "Fixture.deps.json"), "{}");
                File.WriteAllText(Path.Combine(source, "Fixture.pdb"), "fixture symbols");
                Directory.CreateDirectory(Path.Combine(source, "assets", "empty"));
                File.WriteAllText(Path.Combine(source, "assets", "icon.txt"), "immutable resource");
                var path = Path.Combine(source, DesktopPluginCatalog.ManifestFileName);
                File.WriteAllText(path, JsonSerializer.Serialize(new DesktopPluginManifest
                {
                    SchemaVersion = 2, Id = id, Name = id, Version = version, Assembly = "Fixture.dll",
                    EntryType = typeof(NativePluginManagementFixturePlugin).FullName!,
                    Dependencies = dependent ? [new() { Id = "retirement.provider", MinimumVersion = "1.0.0" }] : []
                }, CerebralJson.Options));
                return path;
            }
            DesktopPluginCatalog Catalog(string name) => new(Path.Combine(root, name, "catalog"));
            Dictionary<string, string> Inventory(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(directory, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
            bool Intact(string directory, Dictionary<string, string> before) =>
                Directory.Exists(directory) && before.Count == Inventory(directory).Count &&
                before.All(pair => File.Exists(Path.Combine(directory, pair.Key)) &&
                    Inventory(directory).TryGetValue(pair.Key, out var hash) && hash == pair.Value);
            void Invisible(DesktopPluginCatalog catalog, string id)
            {
                Check(catalog.List().All(item => item.Id != id) &&
                    new DesktopPluginCatalog(catalog.RootDirectory).GetManagementSnapshot().Plugins.All(item => item.Id != id),
                    "A fresh catalog does not rediscover residual code.");
                Check(File.Exists(Path.Combine(catalog.RootDirectory, ".settings", id + ".removed")) &&
                    !File.Exists(Path.Combine(catalog.RootDirectory, ".settings", id + ".active.json")) &&
                    !File.Exists(Path.Combine(catalog.RootDirectory, ".settings", id + ".json")),
                    "Logical retirement leaves its authoritative marker and removes selection/enablement.");
                Check(Fails(() => catalog.SetEnabled(id, true)), "Residual code cannot be enabled.");
            }

            var plain = Catalog("plain"); const string plainId = "retirement.plain";
            var plainSource = Package(plainId); var plainInstalled = plain.Install(plainSource);
            var saved = Path.Combine(root, "plugin-data", plainId, "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.WriteAllText(saved, "saved café\nsecond line");
            var plainResult = plain.RemoveWithResult(plainId);
            Check(!plainResult.CleanupPending && plainResult.CleanupError is null && !Directory.Exists(plainInstalled.Directory),
                "Unlocked removal retires and reclaims the complete package.");
            Invisible(plain, plainId);
            Check(File.ReadAllText(saved) == "saved café\nsecond line", "Removal preserves saved plugin data.");
            Check(plain.SeedBundled(plainSource) is null, "A removed bundled package does not reseed.");
            Check(!plain.RemoveWithResult(plainId).CleanupPending, "Retired cleanup is idempotent.");

            var locked = Catalog("locked"); const string lockedId = "retirement.locked";
            var lockedSource = Package(lockedId); var lockedInstalled = locked.Install(lockedSource);
            var lockedInventory = Inventory(lockedInstalled.Directory);
            DesktopPluginInfo reinstalled;
            using (var file = new FileStream(Path.Combine(lockedInstalled.Directory, "Fixture.dll"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = locked.RemoveWithResult(lockedId);
                Check(result.CleanupPending && result.CleanupError is not null && !result.CleanupError.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "A real sharing lock reports deferred cleanup without private paths.");
                Check(Intact(lockedInstalled.Directory, lockedInventory), "A sharing lock preserves every immutable package file.");
                Invisible(locked, lockedId);
                Check(locked.SeedBundled(lockedSource) is null && Intact(lockedInstalled.Directory, lockedInventory),
                    "Bundled cleanup retry preserves the still-locked generation without reseeding.");
                reinstalled = locked.Install(lockedSource);
                Check(!reinstalled.Enabled && reinstalled.Error is null && reinstalled.Directory != lockedInstalled.Directory &&
                    locked.List().Single().Directory == reinstalled.Directory,
                    "Explicit same-ID reinstall selects a new disabled generation while residual code stays retired.");
                Check(Intact(lockedInstalled.Directory, lockedInventory), "Reinstall does not rewrite the locked generation.");
            }
            var updated = locked.Update(Package(lockedId, "1.1.0"));
            Check(updated.Version == "1.1.0" && !updated.Enabled && !Directory.Exists(lockedInstalled.Directory),
                "Normal update prunes a released retired generation and preserves reinstall enablement.");

            var mapped = Catalog("mapped"); const string mappedId = "retirement.mapped";
            var mappedSource = Package(mappedId); var mappedInstalled = mapped.Install(mappedSource);
            var mappedInventory = Inventory(mappedInstalled.Directory);
            var mappedDll = Path.Combine(mappedInstalled.Directory, "Fixture.dll");
            var image = LoadLibraryEx(mappedDll, IntPtr.Zero, 0x20); // Image-resource mapping, no entrypoint execution.
            if (image == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                Check(Fails(() => File.Delete(mappedDll)) && File.Exists(mappedDll),
                    "The actual Windows image section blocks physical DLL deletion.");
                var result = mapped.RemoveWithResult(mappedId);
                Check(result.CleanupPending && Intact(mappedInstalled.Directory, mappedInventory),
                    "An actual mapped PE image preserves manifest, deps, symbols, DLL and resources.");
                Invisible(mapped, mappedId);
                var batch = mapped.CleanupRemovedPackages();
                Check(batch.Results.Single() is { CleanupPending: true } && batch.NextAfterId is null &&
                    Intact(mappedInstalled.Directory, mappedInventory), "Maintenance reports the mapped generation without partial deletion.");
            }
            finally { if (!FreeLibrary(image)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()); }
            var recovered = new DesktopPluginCatalog(mapped.RootDirectory).CleanupRemovedPackages();
            Check(recovered.Results.Single() is { CleanupPending: false, CleanupError: null } &&
                !Directory.Exists(mappedInstalled.Directory), "Fresh-host maintenance reclaims the full generation after image release.");

            var readOnly = Catalog("read-only"); const string readOnlyId = "retirement.read-only";
            var readOnlyInstalled = readOnly.Install(Package(readOnlyId)); var readOnlyInventory = Inventory(readOnlyInstalled.Directory);
            var readOnlyFile = Path.Combine(readOnlyInstalled.Directory, "Fixture.pdb");
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            try
            {
                Check(readOnly.RemoveWithResult(readOnlyId).CleanupPending && Intact(readOnlyInstalled.Directory, readOnlyInventory),
                    "Read-only cleanup failure preserves the complete generation.");
                Invisible(readOnly, readOnlyId);
            }
            finally { File.SetAttributes(readOnlyFile, FileAttributes.Normal); }
            Check(!readOnly.RemoveWithResult(readOnlyId).CleanupPending && !Directory.Exists(readOnlyInstalled.Directory),
                "Cleanup completes after the access blocker is released.");

            foreach (var stateName in new[] { ".active.json", ".json" })
            {
                var failed = Catalog("state-failure-" + stateName.Replace(".", "")); const string id = "retirement.failed";
                var current = failed.Install(Package(id));
                var inventory = Inventory(current.Directory);
                var state = Path.Combine(failed.RootDirectory, ".settings", id + stateName);
                var previous = File.ReadAllBytes(state);
                using (var stateLock = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Check(Fails(() => failed.RemoveWithResult(id)), "Locked publication state rejects removal.");
                    Check(!File.Exists(Path.Combine(failed.RootDirectory, ".settings", id + ".removed")) &&
                        File.ReadAllBytes(state).SequenceEqual(previous) && failed.List().Single().Directory == current.Directory &&
                        Intact(current.Directory, inventory), "Publication failure restores visibility and all prior package bytes.");
                }
                Check(!failed.RemoveWithResult(id).CleanupPending, "A rejected retirement can complete after state release.");
            }

            var legacy = Catalog("legacy"); const string legacyId = "retirement.legacy";
            var legacySource = Package(legacyId);
            var legacyDirectory = Path.Combine(legacy.RootDirectory, legacyId); Directory.CreateDirectory(legacyDirectory);
            foreach (var file in NativeDesktopPluginPackage.EnumeratePackageFiles(Path.GetDirectoryName(legacySource)!))
            {
                var destination = Path.Combine(legacyDirectory, Path.GetRelativePath(Path.GetDirectoryName(legacySource)!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var legacyInventory = Inventory(legacyDirectory);
            using (var legacyLock = new FileStream(Path.Combine(legacyDirectory, "Fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(legacy.RemoveWithResult(legacyId).CleanupPending && Intact(legacyDirectory, legacyInventory),
                    "A legacy folder can retire coherently with a mapped or sharing-locked file.");
                Invisible(legacy, legacyId);
                Check(Fails(() => legacy.Install(legacySource)) && Intact(legacyDirectory, legacyInventory) &&
                    File.Exists(Path.Combine(legacy.RootDirectory, ".settings", legacyId + ".removed")),
                    "A blocked legacy detachment rejects reinstall without clearing the marker or changing old bytes.");
            }
            var legacySymbols = Path.Combine(legacyDirectory, "Fixture.pdb");
            File.SetAttributes(legacySymbols, FileAttributes.ReadOnly);
            string retiredLegacy;
            var legacyActive = Path.Combine(legacy.RootDirectory, ".settings", legacyId + ".active.json");
            Directory.CreateDirectory(legacyActive);
            try
            {
                Check(Fails(() => legacy.Install(legacySource)), "A blocked pointer publication rejects a detached-legacy reinstall.");
                retiredLegacy = Directory.EnumerateDirectories(legacy.RootDirectory, ".retired-" + legacyId + "-*").Single();
                Check(!Directory.Exists(legacyDirectory) && Intact(retiredLegacy, legacyInventory) &&
                    File.Exists(Path.Combine(legacy.RootDirectory, ".settings", legacyId + ".removed")) && legacy.List().Count == 0,
                    "Failed pointer publication preserves the whole detached legacy generation and restores removal visibility.");
            }
            finally { Directory.Delete(legacyActive); }
            try
            {
                var installed = legacy.Install(legacySource);
                Check(!Directory.Exists(legacyDirectory) && Intact(retiredLegacy, legacyInventory) && !installed.Enabled &&
                    legacy.List().Single().Directory == installed.Directory,
                    "Reinstall selects only the fresh disabled generation after the old legacy namespace is detached.");
                var activeBytes = File.ReadAllBytes(legacyActive); File.Delete(legacyActive);
                Check(legacy.List().Count == 0, "Even missing new pointer cannot resurrect detached legacy bytes.");
                File.WriteAllBytes(legacyActive, activeBytes);
            }
            finally { File.SetAttributes(Path.Combine(retiredLegacy, "Fixture.pdb"), FileAttributes.Normal); }
            legacy.Update(Package(legacyId, "1.1.0"));
            Check(!Directory.EnumerateDirectories(legacy.RootDirectory, ".retired-" + legacyId + "-*").Any(),
                "Released legacy retirement is reclaimed by existing update pruning.");

            var dependent = Catalog("dependent");
            dependent.Install(Package("retirement.provider"));
            dependent.Install(Package("retirement.dependent", dependent: true));
            dependent.SetEnabled("retirement.provider", true); dependent.SetEnabled("retirement.dependent", true);
            var providerSetting = Path.Combine(dependent.RootDirectory, ".settings", "retirement.provider.json");
            File.WriteAllText(providerSetting, "{\"Enabled\":false}"); // Deliberate legacy-invalid enablement fixture.
            Check(Fails(() => dependent.RemoveWithResult("retirement.provider")) &&
                dependent.List().Any(item => item.Id == "retirement.provider"), "Enabled required dependent still blocks removal.");
            dependent.SetEnabled("retirement.dependent", false); dependent.RemoveWithResult("retirement.provider");

            var linked = Catalog("linked"); const string linkedId = "retirement.linked";
            var linkedInstalled = linked.Install(Package(linkedId));
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep"), "untouched");
            var link = Path.Combine(linkedInstalled.Directory, "escape");
            // Directory junctions exercise the same reparse guard without symbolic-link privileges.
            var junctionStart = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/j", link, outside }) junctionStart.ArgumentList.Add(argument);
            using (var junctionProcess = System.Diagnostics.Process.Start(junctionStart)!)
            {
                _ = junctionProcess.StandardOutput.ReadToEnd();
                var junctionError = junctionProcess.StandardError.ReadToEnd();
                junctionProcess.WaitForExit();
                Check(junctionProcess.ExitCode == 0 && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint),
                    "The fixture creates an actual directory reparse point: " + junctionError);
            }
            try
            {
                Check(Fails(() => linked.RemoveWithResult(linkedId)) &&
                    !File.Exists(Path.Combine(linked.RootDirectory, ".settings", linkedId + ".removed")) &&
                    File.ReadAllText(Path.Combine(outside, "keep")) == "untouched", "Linked package storage is rejected before logical or physical mutation.");
            }
            finally { Directory.Delete(link); }

            var stagedRoot = Path.Combine(root, "late-disposition"); Directory.CreateDirectory(stagedRoot);
            var stagedFirst = Path.Combine(stagedRoot, "first");
            var stagedSecond = Path.Combine(stagedRoot, "second");
            File.WriteAllText(stagedFirst, "first immutable bytes"); File.WriteAllText(stagedSecond, "second immutable bytes");
            const System.Reflection.BindingFlags nativeStatic = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var openNative = typeof(DesktopPluginCatalog).GetMethod("RetirementCreateFile", nativeStatic)!;
            var stageNative = typeof(DesktopPluginCatalog).GetMethod("StageInactivePackageDeletion", nativeStatic)!;
            using (var firstHandle = (Microsoft.Win32.SafeHandles.SafeFileHandle)openNative.Invoke(null,
                [stagedFirst, 0xC0010000u, 0u, IntPtr.Zero, 3u, 0x00200080u, IntPtr.Zero])!)
            using (var secondHandle = (Microsoft.Win32.SafeHandles.SafeFileHandle)openNative.Invoke(null,
                [stagedSecond, 0xC0010000u, 0u, IntPtr.Zero, 3u, 0x00200080u, IntPtr.Zero])!)
            {
                Check(!firstHandle.IsInvalid && !secondHandle.IsInvalid, "The late-failure fixture owns real exclusive delete-capable handles.");
                File.SetAttributes(stagedSecond, FileAttributes.ReadOnly);
                var failure = (string?)stageNative.Invoke(null,
                    [new Microsoft.Win32.SafeHandles.SafeFileHandle[] { firstHandle, secondHandle }]);
                Check(failure?.Contains("deferred", StringComparison.Ordinal) == true,
                    "A real late read-only disposition failure is classified without committing earlier staged deletion.");
            }
            File.SetAttributes(stagedSecond, FileAttributes.Normal);
            Check(File.ReadAllText(stagedFirst) == "first immutable bytes" &&
                File.ReadAllText(stagedSecond) == "second immutable bytes",
                "Withdrawing the successful first disposition preserves both files after all handles close.");

            var prefixed = Catalog("prefix"); const string prefixId = "retirement.alpha";
            var neighborId = prefixId + "-beta"; var neighborSource = Package(neighborId);
            var neighborRetired = Path.Combine(prefixed.RootDirectory, ".retired-" + neighborId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(neighborRetired);
            foreach (var file in NativeDesktopPluginPackage.EnumeratePackageFiles(Path.GetDirectoryName(neighborSource)!))
            {
                var destination = Path.Combine(neighborRetired, Path.GetRelativePath(Path.GetDirectoryName(neighborSource)!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var neighborInventory = Inventory(neighborRetired);
            prefixed.SeedBundled(Package(prefixId), false);
            Check(Intact(neighborRetired, neighborInventory), "Seed cleanup cannot mistake a prefix neighbor's retired generation for its own.");
            prefixed.Update(Package(prefixId, "1.1.0"));
            Check(Intact(neighborRetired, neighborInventory), "Inactive generation pruning uses the same exact ID/GUID boundary.");
            prefixed.RemoveWithResult(prefixId);
            Check(Intact(neighborRetired, neighborInventory), "Removal cannot reclaim a prefix neighbor's retired generation.");

            var bounded = Catalog("retention"); const string boundedId = "retirement.bounded";
            var generations = new List<DesktopPluginInfo>();
            var holds = new List<FileStream>();
            try
            {
                var current = bounded.Install(Package(boundedId));
                for (var index = 0; index < 8; index++)
                {
                    generations.Add(current);
                    holds.Add(new FileStream(Path.Combine(current.Directory, "Fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (index < 7) current = bounded.Update(Package(boundedId, "1." + (index + 1) + ".0"));
                }
                Check(Fails(() => bounded.Update(Package(boundedId, "1.8.0"))) &&
                    bounded.List().Single().Directory == current.Directory &&
                    generations.All(item => File.Exists(item.ManifestPath)),
                    "Blocked cleanup preserves all complete generations and the existing eight-generation retention budget.");
            }
            finally { foreach (var hold in holds) hold.Dispose(); }
            var afterRelease = bounded.Update(Package(boundedId, "1.9.0"));
            Check(afterRelease.Version == "1.9.0" && generations.Take(7).All(item => !Directory.Exists(item.Directory)),
                "Retention recovery succeeds after consumers release inactive files.");

            var batchCatalog = Catalog("batch");
            for (var index = 0; index < 3; index++)
            {
                var id = "retirement.batch-" + index;
                batchCatalog.Install(Package(id)); batchCatalog.RemoveWithResult(id);
            }
            var first = batchCatalog.CleanupRemovedPackages(2);
            var second = batchCatalog.CleanupRemovedPackages(2, first.NextAfterId);
            Check(first.Results.Count == 2 && first.NextAfterId is not null && second.Results.Count == 1 &&
                second.NextAfterId is null && first.Results.Concat(second.Results).Select(item => item.Id).Distinct().Count() == 3,
                "Bounded maintenance advances past earlier markers without starvation or duplicate IDs.");
            Check(Fails(() => batchCatalog.CleanupRemovedPackages(2, "../outside")) &&
                File.ReadAllText(Path.Combine(outside, "keep")) == "untouched", "Maintenance cursor IDs cannot escape package storage.");
            return checks;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
