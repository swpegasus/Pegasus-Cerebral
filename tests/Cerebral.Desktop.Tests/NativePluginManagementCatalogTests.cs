using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class NativePluginManagementCatalogTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Native plugin management: " + message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.NativePluginManagement-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed");
        var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        try
        {
            string Package(string id, string name, string version = "1.0.0",
                IReadOnlyList<DesktopPluginDependency>? dependencies = null,
                string? exclusiveGroup = null, bool runnable = false)
            {
                var directory = Path.Combine(sources, id); Directory.CreateDirectory(directory);
                var assembly = Path.Combine(directory, "Fixture.dll");
                if (runnable) File.Copy(typeof(NativePluginManagementFixturePlugin).Assembly.Location, assembly, true);
                else File.WriteAllBytes(assembly, [0x4d, 0x5a, 0x01, 0x02]);
                var manifest = new DesktopPluginManifest
                {
                    SchemaVersion = exclusiveGroup is null ? 2 : 3,
                    Contract = DesktopPluginContract.Id,
                    ApiVersion = DesktopPluginContract.Version,
                    Id = id,
                    Name = name,
                    Version = version,
                    Description = "Temporary host-management fixture; owner data is not used.",
                    Assembly = "Fixture.dll",
                    EntryType = runnable ? typeof(NativePluginManagementFixturePlugin).FullName! : "Fixture.Plugin",
                    Dependencies = dependencies?.ToList() ?? [],
                    DefaultEnabled = exclusiveGroup is null ? null : false,
                    ExclusiveGroup = exclusiveGroup,
                    ExclusivePriority = exclusiveGroup is null ? null : 0
                };
                var path = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
                File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
                return path;
            }

            NativePluginSelection Selection(NativePluginCatalogSnapshot snapshot, string id)
            {
                var row = snapshot.Plugins.Single(item => item.Id == id);
                return new(row.Id, row.Enabled,
                    row.Generation ?? throw new InvalidOperationException("Fixture package lacks an immutable generation."),
                    row.PackageFingerprint ?? throw new InvalidOperationException("Fixture package lacks a package fingerprint."));
            }

            var catalog = new DesktopPluginCatalog(installed);
            var providerId = "fixture.management-provider";
            var consumerId = "fixture.management-consumer";
            var firstId = "fixture.management-first";
            var secondId = "fixture.management-second";
            var runtimeId = "fixture.management-runtime";
            catalog.Install(Package(providerId, "Management provider"));
            catalog.Install(Package(consumerId, "Management consumer", dependencies:
            [new DesktopPluginDependency { Id = providerId, MinimumVersion = "1.0.0", MaximumVersionExclusive = "2.0.0" }]));
            catalog.Install(Package(firstId, "Management first", exclusiveGroup: "fixture.management-choice"));
            catalog.Install(Package(secondId, "Management second", exclusiveGroup: "fixture.management-choice"));
            catalog.Install(Package(runtimeId, "Management runtime", runnable: true));
            var ownerData = Path.Combine(root, "owner-data", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(ownerData)!); File.WriteAllText(ownerData, "preserve owner data");

            var initial = catalog.GetManagementSnapshot();
            Check(initial.Plugins.Count == 5 && initial.Omitted == 0 && initial.Plugins.All(item =>
                    item is { Enabled: false, Loaded: false, Error: null } && item.Generation is not null && item.PackageFingerprint is not null),
                "A real immutable catalog projects exact disabled package identities without paths.");
            Check(initial.CatalogFingerprint.Length == 64 && initial.CatalogFingerprint.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'),
                "The complete catalog identity is a lowercase SHA-256 value.");
            Check(catalog.GetManagementSnapshot("management").CatalogFingerprint == initial.CatalogFingerprint &&
                  catalog.GetManagementSnapshot(null, [new(runtimeId, false, @"C:\private\plugin.dll failed")]).CatalogFingerprint == initial.CatalogFingerprint,
                "Query and runtime observations do not change the complete catalog fingerprint.");
            var redacted = catalog.GetManagementSnapshot(runtimeId,
                [new(runtimeId, false, @"C:\private\plugin.dll failed")]).Plugins.Single();
            Check(!redacted.Loaded && redacted.Error == "The native plugin did not load in the current desktop runtime." &&
                  !redacted.Error.Contains("private", StringComparison.OrdinalIgnoreCase),
                "Runtime failures retain load truth without exposing provider paths.");
            Check(Throws<ArgumentException>(() => catalog.GetManagementSnapshot(new string('x', 101))) &&
                  Throws<ArgumentException>(() => catalog.GetManagementSnapshot(null,
                      [new(runtimeId, false, null), new(runtimeId, true, null)])),
                "Queries and runtime observations are bounded and duplicate identities fail closed.");

            var dependencyRejected = false;
            try { catalog.SetEnabledExact(initial.CatalogFingerprint, Selection(initial, consumerId), true); }
            catch (InvalidOperationException) { dependencyRejected = true; }
            Check(dependencyRejected && catalog.GetManagementSnapshot().CatalogFingerprint == initial.CatalogFingerprint,
                "Dependency rejection occurs before any enablement write.");
            var providerResult = catalog.SetEnabledExact(initial.CatalogFingerprint, Selection(initial, providerId), true);
            Check(providerResult.ChangedPluginIds.SequenceEqual([providerId]) &&
                  providerResult.Catalog.Plugins.Single(item => item.Id == providerId).Enabled,
                "Exact CAS enables one dependency provider and reports its actual changed identity.");
            var withProvider = catalog.GetManagementSnapshot();
            _ = catalog.SetEnabledExact(withProvider.CatalogFingerprint, Selection(withProvider, consumerId), true);
            var withConsumer = catalog.GetManagementSnapshot();
            Check(Throws<InvalidOperationException>(() => catalog.SetEnabledExact(withConsumer.CatalogFingerprint,
                    Selection(withConsumer, providerId), false)) &&
                  catalog.GetManagementSnapshot(providerId).Plugins.Single().Enabled,
                "An enabled required dependent prevents provider disablement before mutation.");

            var beforeFirst = catalog.GetManagementSnapshot();
            _ = catalog.SetEnabledExact(beforeFirst.CatalogFingerprint, Selection(beforeFirst, firstId), true);
            var beforeSecond = catalog.GetManagementSnapshot();
            var firstPackageFingerprint = beforeSecond.Plugins.Single(item => item.Id == firstId).PackageFingerprint;
            var activeBytes = File.ReadAllBytes(Path.Combine(installed, ".settings", firstId + ".active.json"));
            var secondResult = catalog.SetEnabledExact(beforeSecond.CatalogFingerprint, Selection(beforeSecond, secondId), true);
            Check(secondResult.ChangedPluginIds.SequenceEqual([firstId, secondId]) &&
                  !catalog.GetManagementSnapshot(firstId).Plugins.Single().Enabled &&
                  catalog.GetManagementSnapshot(secondId).Plugins.Single().Enabled,
                "Exclusive enablement returns both the disabled alternative and enabled target.");
            Check(catalog.GetManagementSnapshot(firstId).Plugins.Single().PackageFingerprint == firstPackageFingerprint &&
                  File.ReadAllBytes(Path.Combine(installed, ".settings", firstId + ".active.json")).SequenceEqual(activeBytes) &&
                  File.ReadAllText(ownerData) == "preserve owner data",
                "Enablement changes preserve immutable package selection and unrelated owner data.");

            var partialBefore = catalog.GetManagementSnapshot();
            _ = catalog.SetEnabledExact(partialBefore.CatalogFingerprint, Selection(partialBefore, firstId), true);
            var partialTarget = catalog.GetManagementSnapshot();
            var targetSetting = Path.Combine(installed, ".settings", secondId + ".json");
            using (var targetLock = new FileStream(targetSetting, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                DesktopPluginManagementMutationException? partial = null;
                try { _ = catalog.SetEnabledExact(partialTarget.CatalogFingerprint, Selection(partialTarget, secondId), true); }
                catch (DesktopPluginManagementMutationException error) { partial = error; }
                Check(partial is { StateMayHaveChanged: true },
                    "A failure after the first exclusive-setting write is explicitly uncertain rather than a pre-effect rejection.");
            }
            Check(!catalog.GetManagementSnapshot(firstId).Plugins.Single().Enabled,
                "The typed uncertain result corresponds to an actual completed exclusive side-effect.");
            catalog.SetEnabled(firstId, true);

            var runtimeBefore = catalog.GetManagementSnapshot();
            _ = catalog.SetEnabledExact(runtimeBefore.CatalogFingerprint, Selection(runtimeBefore, runtimeId), true);
            var binding = new DesktopConversationBinding("management-runtime", "fixture");
            var draft = new DesktopDraftSnapshot(binding, 0, "");
            Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(binding, "Native management", null, null, false), () => draft,
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { },
                pluginDataRoot: Path.Combine(root, "runtime-data"));
            using (var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher))
            {
                runtime.LoadEnabled();
                var actualStatus = runtime.Status.Single(item => item.Id == runtimeId);
                var runtimeProjection = catalog.GetManagementSnapshot(runtimeId, runtime.Status).Plugins.Single();
                Check(actualStatus is { Loaded: true, Error: null } && runtimeProjection is { Enabled: true, Loaded: true, Error: null },
                    "A real temporary plugin runtime projects enabled and loaded as independent truthful states.");
            }

            var stale = catalog.GetManagementSnapshot();
            catalog.SetEnabled(runtimeId, false);
            Check(Throws<InvalidOperationException>(() => catalog.SetEnabledExact(stale.CatalogFingerprint,
                    Selection(stale, secondId), true)) && !catalog.GetManagementSnapshot(secondId).Plugins.Single().Enabled,
                "A stale full-catalog fingerprint rejects before changing the selected target.");

            // Add supported legacy packages directly to exercise projection limits without publishing
            // hundreds of immutable generations. Each remains a complete real package file set.
            void LegacyPackage(string id, string name, string? exclusiveGroup = null, bool enabled = false)
            {
                var directory = Path.Combine(installed, id); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "Fixture.dll"), [0x4d, 0x5a, 0x01, 0x02]);
                var manifest = new DesktopPluginManifest
                {
                    SchemaVersion = exclusiveGroup is null ? 2 : 3,
                    Contract = DesktopPluginContract.Id, ApiVersion = DesktopPluginContract.Version,
                    Id = id, Name = name, Version = "1.0.0", Description = "Bounded projection fixture",
                    Assembly = "Fixture.dll", EntryType = "Fixture.Plugin", Dependencies = [],
                    DefaultEnabled = exclusiveGroup is null ? null : false,
                    ExclusiveGroup = exclusiveGroup,
                    ExclusivePriority = exclusiveGroup is null ? null : 0
                };
                File.WriteAllText(Path.Combine(directory, DesktopPluginCatalog.ManifestFileName),
                    JsonSerializer.Serialize(manifest, CerebralJson.Options));
                if (enabled)
                {
                    Directory.CreateDirectory(Path.Combine(installed, ".settings"));
                    File.WriteAllText(Path.Combine(installed, ".settings", id + ".json"), "{\"enabled\":true}");
                }
            }
            LegacyPackage("fixture.match", "Exact fixture match");
            for (var index = 0; index < 101; index++)
                LegacyPackage("fixture.management-extra-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                    "fixture.match related " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    index < 100 ? "fixture.management-choice" : null, enabled: index < 100);
            var bounded = catalog.GetManagementSnapshot();
            var exactQuery = catalog.GetManagementSnapshot("fixture.match");
            Check(bounded.Plugins.Count == 100 && bounded.Omitted == 7 &&
                  exactQuery.Plugins.Count == 100 && exactQuery.Omitted == 2 && exactQuery.Plugins[0].Id == "fixture.match" &&
                  bounded.CatalogFingerprint == exactQuery.CatalogFingerprint,
                "A complete fingerprint backs a 100-row projection, and exact ID matches precede substring matches.");
            var oversizedBefore = catalog.GetManagementSnapshot();
            var oversizedTarget = catalog.GetManagementSnapshot(firstId);
            Check(Throws<InvalidOperationException>(() => catalog.SetEnabledExact(oversizedBefore.CatalogFingerprint,
                      Selection(oversizedTarget, firstId), true)) &&
                  catalog.GetManagementSnapshot().CatalogFingerprint == oversizedBefore.CatalogFingerprint,
                "A selected target plus 100 exclusive side effects rejects before writes because the complete result cannot fit 100 rows.");
            var largeBefore = catalog.GetManagementSnapshot();
            var largeRuntime = catalog.GetManagementSnapshot(runtimeId);
            var largeMutation = catalog.SetEnabledExact(largeBefore.CatalogFingerprint, Selection(largeRuntime, runtimeId), true);
            Check(largeMutation.Catalog.Plugins.Count == 100 && largeMutation.Catalog.Plugins[0].Id == runtimeId &&
                  largeMutation.Catalog.Omitted == 7 && largeMutation.ChangedPluginIds.SequenceEqual([runtimeId]),
                "A bounded mutation result retains its exact target even when the complete catalog exceeds 100 rows.");

            var invalidManifest = catalog.List().Single(item => item.Id == secondId).ManifestPath;
            var validSelection = Selection(catalog.GetManagementSnapshot(secondId), secondId);
            File.WriteAllText(invalidManifest, "{}");
            var invalid = catalog.GetManagementSnapshot(secondId);
            var invalidRow = invalid.Plugins.Single();
            Check(invalidRow.Error == "The installed native plugin package is invalid or unavailable." &&
                  invalidRow.PackageFingerprint is null && !invalidRow.Error.Contains(root, StringComparison.OrdinalIgnoreCase),
                "Malformed installed package state is path-free and cannot masquerade as a selectable identity.");
            Check(Throws<InvalidDataException>(() => catalog.SetEnabledExact(invalid.CatalogFingerprint, validSelection, false)) &&
                  File.ReadAllText(ownerData) == "preserve owner data",
                "Invalid catalog state fails closed before any exact enablement mutation or owner-data change.");

            return checks;
        }
        finally
        {
            // The real collectible runtime may keep its package image mapped until the unload
            // request reaches a collection boundary; the exact random fixture remains test-owned.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}

/// <summary>Real collectible fixture loaded only by the task-owned catalog/runtime test.</summary>
public sealed class NativePluginManagementFixturePlugin : DesktopPluginBase { }
