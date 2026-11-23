using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class PluginUpdateTransactionTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.UpdateTransaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DesktopProductPaths Paths(string directory, string? models = null) => new(
                Path.Combine(directory, "install"), Path.Combine(directory, "data"), Path.Combine(directory, "user"),
                Path.Combine(directory, "projects"), models ?? Path.Combine(directory, "models"), Path.Combine(directory, "tools"),
                Path.Combine(directory, "shared"));
            var source = Path.Combine(root, "source");
            var sourcePaths = Paths(Path.Combine(root, "source-products"));
            var products = Paths(Path.Combine(root, "products"));
            var data = Path.Combine(root, "plugin-data");
            var preset = Path.Combine(data, "fixture.transaction", CerebralPluginPackageContract.CurrentSettingsFileName);
            var catalog = new DesktopPluginCatalog(Path.Combine(root, "catalog"));
            var model = Path.Combine(products.ModelsDirectory, "fixture.transaction", "model");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "Fixture.Plugin.dll"), [0x4d, 0x5a, 0x01, 0x02]);
            var resource = new CerebralPluginPackageResource
                { Id = "fixture.transaction.model", Kind = "model", Source = "resources/model", Destination = "fixture.transaction/model" };
            File.WriteAllText(Path.Combine(source, CerebralPluginPackageContract.ResourceManifestFileName),
                JsonSerializer.Serialize(new CerebralPluginResourceManifest { Resources = [resource] }, CerebralJson.Options));
            var sourceModel = Path.Combine(sourcePaths.ModelsDirectory, "fixture.transaction", "model");
            Directory.CreateDirectory(sourceModel);

            string Package(string version, List<DesktopPluginDependency>? dependencies = null)
            {
                var manifest = new DesktopPluginManifest
                {
                    SchemaVersion = 2, Contract = DesktopPluginContract.Id, ApiVersion = DesktopPluginContract.Version,
                    Id = "fixture.transaction", Name = "Fixture transaction", Version = version,
                    Description = "Filesystem transaction fixture; no native code is executed.",
                    Assembly = "Fixture.Plugin.dll", EntryType = "Fixture.Plugin", Dependencies = dependencies ?? []
                };
                var manifestPath = Path.Combine(source, DesktopPluginCatalog.ManifestFileName);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, CerebralJson.Options));
                File.WriteAllText(Path.Combine(source, CerebralPluginPackageContract.DefaultSettingsFileName),
                    JsonSerializer.Serialize(new { version }));
                File.WriteAllText(Path.Combine(sourceModel, "weights.gguf"), version);
                var path = Path.Combine(root, version + "-" + Guid.NewGuid().ToString("N") + CerebralPluginPackageContract.Extension);
                CerebralPluginPackage.Export(new(manifest.Id, manifest.Name, manifest.Version, manifest.Description,
                    false, source, manifestPath, null), path, "defaults", sourcePaths, Path.Combine(root, "source-data"));
                return path;
            }
            // Real rename failure after resources/preset have published and before code selection.
            // Residual owner choices from a removed package must survive a rejected reinstall.
            foreach (var fault in new[] { "selection", "enablement" })
            {
                var failedRoot = Path.Combine(root, "failed-reinstall-" + fault);
                var failedCatalog = new DesktopPluginCatalog(Path.Combine(failedRoot, "catalog"));
                var failedProducts = Paths(Path.Combine(failedRoot, "products"));
                var failedData = Path.Combine(failedRoot, "plugin-data");
                var state = Path.Combine(failedCatalog.RootDirectory, ".settings");
                Directory.CreateDirectory(state);
                var blockedActive = Path.Combine(state, "fixture.transaction.active.json");
                if (fault == "selection") Directory.CreateDirectory(blockedActive);
                var removed = Path.Combine(state, "fixture.transaction.removed");
                var enabled = Path.Combine(state, "fixture.transaction.json");
                var removedBytes = System.Text.Encoding.UTF8.GetBytes("owner removal choice");
                var enabledBytes = System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":true}");
                File.WriteAllBytes(removed, removedBytes);
                File.WriteAllBytes(enabled, enabledBytes);
                var failedPreset = Path.Combine(failedData, "fixture.transaction",
                    CerebralPluginPackageContract.CurrentSettingsFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(failedPreset)!);
                File.WriteAllText(failedPreset, "owner preset");
                var failedModel = Path.Combine(failedProducts.ModelsDirectory, "fixture.transaction", "model");
                Directory.CreateDirectory(failedModel);
                File.WriteAllText(Path.Combine(failedModel, "weights.gguf"), "owner model");
                using var prepared = CerebralPluginPackage.Prepare(Package("1.0.0"));
                using var lockedEnablement = fault == "enablement"
                    ? new FileStream(enabled, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                var rejected = false;
                try { prepared.InstallReviewed(failedCatalog, failedProducts, failedData); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "Blocked fresh selection rejects the reinstall.");
                Check(File.Exists(removed) && File.ReadAllBytes(removed).SequenceEqual(removedBytes),
                    "Rejected reinstall restores the exact owner removal choice.");
                Check(File.ReadAllBytes(enabled).SequenceEqual(enabledBytes),
                    "Rejected reinstall restores the exact residual enablement choice.");
                Check(File.ReadAllText(failedPreset) == "owner preset", "Rejected reinstall restores the preset.");
                Check(File.ReadAllText(Path.Combine(failedModel, "weights.gguf")) == "owner model",
                    "Rejected reinstall restores the resource.");
                Check(fault == "selection" ? Directory.Exists(blockedActive) : !File.Exists(blockedActive),
                    "Rejected reinstall preserves prior selection state.");
                Check(!Directory.EnumerateFileSystemEntries(failedRoot, "*cerebral-previous-*", SearchOption.AllDirectories).Any() &&
                    !Directory.EnumerateFileSystemEntries(failedRoot, "*cerebral-staging-*", SearchOption.AllDirectories).Any(),
                    "A fully rolled-back reinstall releases its transaction recovery paths.");
            }
            {
                var freshRoot = Path.Combine(root, "failed-first-install");
                var freshCatalog = new DesktopPluginCatalog(Path.Combine(freshRoot, "catalog"));
                var freshProducts = Paths(Path.Combine(freshRoot, "products"));
                var freshData = Path.Combine(freshRoot, "plugin-data");
                var state = Path.Combine(freshCatalog.RootDirectory, ".settings");
                Directory.CreateDirectory(Path.Combine(state, "fixture.transaction.active.json"));
                using var prepared = CerebralPluginPackage.Prepare(Package("1.0.0"));
                var rejected = false;
                try { prepared.InstallReviewed(freshCatalog, freshProducts, freshData); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "A blocked first-install selection fails.");
                Check(!File.Exists(Path.Combine(state, "fixture.transaction.json")) &&
                    !File.Exists(Path.Combine(state, "fixture.transaction.removed")),
                    "A rejected first install does not invent enablement or removal choices.");
                Check(!File.Exists(Path.Combine(freshData, "fixture.transaction",
                    CerebralPluginPackageContract.CurrentSettingsFileName)), "A rejected first install removes its new preset.");
                Check(!Directory.Exists(Path.Combine(freshProducts.ModelsDirectory, "fixture.transaction", "model")),
                    "A rejected first install removes its new resource.");
            }
            {
                var settingsRoot = Path.Combine(root, "settings-recovery");
                var settingsPath = Path.Combine(settingsRoot, "fixture.transaction",
                    CerebralPluginPackageContract.CurrentSettingsFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, "owner settings");
                using var prepared = CerebralPluginPackage.Prepare(Package("1.0.0"));
                using var presetInstallation = prepared.BeginPortableSettingsInstallation(settingsRoot);
                presetInstallation.Publish();
                var candidate = File.ReadAllBytes(settingsPath);
                var previous = Directory.EnumerateFiles(Path.GetDirectoryName(settingsPath)!,
                    "*.cerebral-previous-*").Single();
                using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = false;
                    try { presetInstallation.Dispose(); } catch (IOException) { failed = true; }
                    Check(failed && File.ReadAllText(previous) == "owner settings",
                        "A locked settings rollback retains exact prior bytes.");
                    Check(File.ReadAllBytes(settingsPath).SequenceEqual(candidate),
                        "A locked settings rollback preserves its candidate until recovery.");
                }
                // A concurrent owner edit is evidence to preserve, rather than a candidate to erase.
                File.WriteAllText(settingsPath, "new owner edit");
                var conflict = false;
                try { presetInstallation.Dispose(); } catch (IOException) { conflict = true; }
                Check(conflict && File.ReadAllText(settingsPath) == "new owner edit" &&
                    File.ReadAllText(previous) == "owner settings", "A settings recovery conflict preserves both owner versions.");
                File.WriteAllBytes(settingsPath, candidate);
                presetInstallation.Dispose();
                Check(File.ReadAllText(settingsPath) == "owner settings" && !File.Exists(previous),
                    "Settings rollback completes after the conflict is explicitly resolved.");
            }
            {
                var stateRoot = Path.Combine(root, "multi-file-recovery");
                Directory.CreateDirectory(stateRoot);
                var firstPath = Path.Combine(stateRoot, "first.json");
                var secondPath = Path.Combine(stateRoot, "second.json");
                File.WriteAllText(firstPath, "first owner state");
                File.WriteAllText(secondPath, "second owner state");
                var type = typeof(DesktopPluginCatalog).Assembly.GetType(
                    "Cerebral.Desktop.Plugins.PreparedPluginFileInstallation", throwOnError: true)!;
                var changes = new (string Destination, byte[]? Contents)[]
                {
                    (firstPath, System.Text.Encoding.UTF8.GetBytes("first candidate")),
                    (secondPath, System.Text.Encoding.UTF8.GetBytes("second candidate"))
                };
                using var transaction = (IDisposable)Activator.CreateInstance(type,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null, args: [changes], culture: null)!;
                type.GetMethod("Publish")!.Invoke(transaction, null);
                var backups = Directory.EnumerateFiles(stateRoot, "*.cerebral-previous-*").ToArray();
                using (var secondLock = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var firstLock = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        IOException? failure = null;
                        try { transaction.Dispose(); } catch (IOException error) { failure = error; }
                        Check(failure?.InnerException is AggregateException { InnerExceptions.Count: 2 },
                            "Two failed file restorations retain both error causes.");
                        Check(backups.Length == 2 && backups.All(File.Exists),
                            "Two failed file restorations retain both previous versions.");
                        Check(File.ReadAllText(firstPath) == "first candidate" &&
                            File.ReadAllText(secondPath) == "second candidate", "Locked file candidates remain intact.");
                    }
                    var partialFailure = false;
                    try { transaction.Dispose(); } catch (IOException) { partialFailure = true; }
                    Check(partialFailure && File.ReadAllText(firstPath) == "first owner state" &&
                        File.ReadAllText(secondPath) == "second candidate",
                        "Recovery restores released files even when another file remains locked.");
                }
                transaction.Dispose();
                Check(File.ReadAllText(firstPath) == "first owner state" &&
                    File.ReadAllText(secondPath) == "second owner state" && !backups.Any(File.Exists),
                    "A later retry finishes only the outstanding recovery.");
            }
            {
                var reinstallRoot = Path.Combine(root, "successful-reinstall");
                var reinstallCatalog = new DesktopPluginCatalog(Path.Combine(reinstallRoot, "catalog"));
                var state = Path.Combine(reinstallCatalog.RootDirectory, ".settings");
                Directory.CreateDirectory(state);
                var removed = Path.Combine(state, "fixture.transaction.removed");
                File.WriteAllText(removed, "owner removal choice");
                File.WriteAllText(Path.Combine(state, "fixture.transaction.json"), "{\"Enabled\":true}");
                using var prepared = CerebralPluginPackage.Prepare(Package("1.0.0"));
                var installed = prepared.InstallReviewed(reinstallCatalog, Paths(Path.Combine(reinstallRoot, "products")),
                    Path.Combine(reinstallRoot, "plugin-data"));
                Check(installed.Version == "1.0.0" && !installed.Enabled && !File.Exists(removed),
                    "An explicit successful reinstall clears removal and installs disabled.");
                Check(reinstallCatalog.List().Single() is { Enabled: false, Error: null },
                    "A successful reinstall's reported state agrees with the selected catalog generation.");
            }
            using (var first = CerebralPluginPackage.Prepare(Package("1.0.0")))
            {
                var installed = first.InstallReviewed(catalog, products, data);
                Check(installed.Version == "1.0.0" && !installed.Enabled, "A fresh package is installed disabled.");
                Check(File.ReadAllText(Path.Combine(model, "weights.gguf")) == "1.0.0", "Fresh resources match.");
                Check(File.ReadAllText(preset).Contains("1.0.0", StringComparison.Ordinal), "Fresh preset matches.");
            }
            File.WriteAllText(Path.Combine(data, "fixture.transaction", "owner-note.txt"), "Keep my owner data.");
            catalog.SetEnabled("fixture.transaction", true);
            var active = Path.Combine(catalog.RootDirectory, ".settings", "fixture.transaction.active.json");
            var enablement = Path.Combine(catalog.RootDirectory, ".settings", "fixture.transaction.json");
            var priorActive = File.ReadAllBytes(active);
            var priorEnablement = File.ReadAllBytes(enablement);
            var priorPreset = File.ReadAllBytes(preset);
            void PriorIntact(string reason)
            {
                var installed = catalog.List().Single();
                Check(installed.Version == "1.0.0" && installed.Enabled, reason + ": old enabled package remains selected.");
                Check(File.ReadAllBytes(active).SequenceEqual(priorActive), reason + ": selection bytes preserved.");
                Check(File.ReadAllBytes(enablement).SequenceEqual(priorEnablement), reason + ": enablement bytes preserved.");
                Check(File.ReadAllBytes(preset).SequenceEqual(priorPreset), reason + ": preset bytes restored.");
                Check(File.ReadAllText(Path.Combine(model, "weights.gguf")) == "1.0.0", reason + ": resource restored.");
                Check(File.ReadAllText(Path.Combine(data, "fixture.transaction", "owner-note.txt")) == "Keep my owner data.",
                    reason + ": unrelated owner data preserved.");
            }
            void ExpectFailure(Action action, string reason)
            {
                var failed = false;
                try { action(); }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                { failed = true; }
                Check(failed, reason + " must reject the candidate.");
                PriorIntact(reason);
            }
            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
            using (var lockedPreset = new FileStream(preset, FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data), "Locked preset");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0",
                [new DesktopPluginDependency { Id = "missing.required", MinimumVersion = "1.0.0" }])))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data), "Required candidate dependency");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
            using (var lockedSelection = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data), "Locked selection");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data, "different.id"), "Wrong expected identity");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
            {
                var fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prepared.DesktopManifestPath);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "changed-after-admission.txt"),
                    "Changed package file set after capacity reservation.");
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data, null, fingerprint),
                    "Copy-time candidate fingerprint mismatch");
            }

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.0.0")))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, data), "Same-version package");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
                ExpectFailure(() => prepared.InstallReviewed(catalog, products, model), "Resource/preset transaction overlap");
            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
                ExpectFailure(() => prepared.InstallReviewed(catalog,
                    Paths(Path.Combine(root, "products"), catalog.RootDirectory), data), "Resource/catalog transaction overlap");

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0")))
            using (var resources = prepared.BeginResourceInstallation(products))
            {
                var parent = Path.GetDirectoryName(model)!;
                var stage = Directory.EnumerateDirectories(parent, "model.cerebral-staging-*").Single();
                resources.Publish();
                // A retained stage represents another uncommitted recovery artifact in this deterministic fixture.
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "recovery-candidate.txt"), "Keep until recovery succeeds.");
                var previous = Directory.EnumerateDirectories(parent, "model.cerebral-previous-*").Single();
                var rollbackFailed = false;
                using (var lockedResource = new FileStream(Path.Combine(model, "weights.gguf"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { resources.Dispose(); }
                    catch (IOException) { rollbackFailed = true; }
                    Check(rollbackFailed, "A locked resource reports incomplete rollback.");
                    Check(Directory.Exists(stage), "An incomplete rollback retains uncommitted staging data.");
                    Check(Directory.Exists(previous), "An incomplete rollback retains original resources.");
                    Check(File.ReadAllText(Path.Combine(model, "weights.gguf")) == "1.1.0", "Locked candidate is retained until recovery can finish.");
                }
                resources.Dispose();
                PriorIntact("Retry after releasing a rollback lock");
            }

            using (var prepared = CerebralPluginPackage.Prepare(Package("1.1.0",
                [new DesktopPluginDependency { Id = "missing.optional", MinimumVersion = "1.0.0", Optional = true }])))
            {
                var updated = prepared.InstallReviewed(catalog, products, data);
                Check(updated.Version == "1.1.0" && updated.Enabled, "A successful update preserves enablement.");
                Check(File.ReadAllBytes(enablement).SequenceEqual(priorEnablement), "Update does not rewrite enablement.");
                Check(!File.ReadAllBytes(active).SequenceEqual(priorActive), "Update selects its new immutable generation.");
                Check(File.ReadAllText(Path.Combine(model, "weights.gguf")) == "1.1.0", "Update commits resources.");
                Check(File.ReadAllText(preset).Contains("1.1.0", StringComparison.Ordinal), "Update commits preset.");
                Check(File.ReadAllText(Path.Combine(data, "fixture.transaction", "owner-note.txt")) == "Keep my owner data.",
                    "Successful update preserves unrelated owner data.");
            }
            Check(!Directory.EnumerateFileSystemEntries(root, "*cerebral-staging-*", SearchOption.AllDirectories).Any(),
                "Completed or rolled-back transactions release staging paths.");
            Check(!Directory.EnumerateFileSystemEntries(root, "*cerebral-previous-*", SearchOption.AllDirectories).Any(),
                "Completed or rolled-back transactions release backups.");
            return checks;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
