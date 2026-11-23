using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static class ServiceHostFacetAdmissionTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-host-facet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            checks++;
        }
        void Refuse(Action action, string fragment)
        {
            try { action(); }
            catch (InvalidDataException error) when (error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                checks++;
                return;
            }
            throw new InvalidOperationException("Expected host-facet refusal containing: " + fragment);
        }
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var assembly = typeof(ScalarFacetFixturePlugin).Assembly.Location;
            File.Copy(assembly, Path.Combine(source, Path.GetFileName(assembly)));
            var manifestPath = Path.Combine(source, ServicePluginRuntime.ManifestFileName);
            var manifest = JsonSerializer.SerializeToNode(new ServicePluginManifest
            {
                Id = "fixture.host-facet", Name = "Host facet fixture", Version = "1.0.0",
                Assembly = Path.GetFileName(assembly), EntryType = typeof(DependencyFixturePlugin).FullName!
            }, CerebralJson.Options)!.AsObject();
            manifest.Remove("portableSettings");
            void Write() => File.WriteAllText(manifestPath, manifest.ToJsonString(CerebralJson.Options));
            Write();
            var legacy = ServicePluginRuntime.InspectPackage(source, manifestPath, requireMatchingDirectory: false);
            Check(legacy.RequiredHostFacets is null && !manifest.ContainsKey("requiredHostFacets"),
                "schema-1 legacy manifest has no serialized requirement");
            var refusedRepository = Path.Combine(root, "refused-repository");
            var refusedPackage = Path.Combine(refusedRepository, legacy.Id);
            Directory.CreateDirectory(refusedPackage);
            File.Copy(assembly, Path.Combine(refusedPackage, Path.GetFileName(assembly)));
            var refusedManifest = (JsonObject)manifest.DeepClone();
            refusedManifest["schemaVersion"] = 2;
            refusedManifest["requiredHostFacets"] = new JsonArray("unknown.host/1");
            File.WriteAllText(Path.Combine(refusedPackage, ServicePluginRuntime.ManifestFileName),
                refusedManifest.ToJsonString(CerebralJson.Options));
            var refusedData = Path.Combine(root, "refused-data");
            using (var refused = new ServicePluginRuntime(refusedRepository, refusedData, new EventHub()))
                Check(refused.Status.Single() is { Loaded: false, Error: { } error } &&
                      error.Contains("unknown.host/1", StringComparison.Ordinal) &&
                      !Directory.Exists(Path.Combine(refusedData, legacy.Id)),
                    "runtime refuses unsupported facet before loading or initializing package code");

            var repository = Path.Combine(root, "repository");
            var data = Path.Combine(root, "data");
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "workflow.db")));
            using (var empty = new ServicePluginRuntime(repository, data, new EventHub()))
            {
                var lifecycle = new ServicePackageLifecycle(repository, empty, new EventHub());
                lifecycle.Attach(store);
                var staged = lifecycle.Stage(manifestPath, 0);
                var selected = lifecycle.Activate(legacy.Id, staged.Revision);
                Check(selected.DesiredVersion == "1.0.0" && !Directory.Exists(Path.Combine(data, legacy.Id)),
                    "legacy package selects without executing plugin code");
            }
            using (var running = new ServicePluginRuntime(repository, data, new EventHub()))
            {
                Check(running.PackageBindings.Single().Loaded &&
                      File.Exists(Path.Combine(data, legacy.Id, "dependency-state.txt")),
                    "legacy package initializes under the current loader");
                var lifecycle = new ServicePackageLifecycle(repository, running, new EventHub());
                lifecycle.Attach(store);
                var initial = lifecycle.Inspect(legacy.Id);
                var retained = Snapshot(repository);

                manifest["schemaVersion"] = 2;
                manifest["version"] = "1.0.1";
                manifest["entryType"] = typeof(ScalarFacetFixturePlugin).FullName!;
                foreach (var (value, fragment) in new (JsonNode?, string)[]
                {
                    (null, "between 1 and 8"),
                    (new JsonArray(), "between 1 and 8"),
                    (new JsonArray(""), "invalid bounded"),
                    (new JsonArray("unknown.host/1"), "unavailable"),
                    (new JsonArray(ServicePluginContract.ScalarGenerationHostFacetV1,
                        ServicePluginContract.ScalarGenerationHostFacetV1), "duplicate"),
                    (new JsonArray(true), "invalid bounded")
                })
                {
                    manifest["requiredHostFacets"] = value?.DeepClone();
                    Write();
                    Refuse(() => ServicePluginRuntime.InspectPackage(source, manifestPath, requireMatchingDirectory: false), fragment);
                    Refuse(() => lifecycle.Stage(manifestPath, initial.Revision), fragment);
                    Check(Same(retained, Snapshot(repository)) && running.PackageBindings.Single().Loaded &&
                          lifecycle.Inspect(legacy.Id).DesiredVersion == "1.0.0",
                        "invalid facet declaration leaves old candidate, selection and loaded plugin intact");
                }
                manifest["schemaVersion"] = 1;
                manifest["requiredHostFacets"] = new JsonArray(ServicePluginContract.ScalarGenerationHostFacetV1);
                Write();
                Refuse(() => lifecycle.Stage(manifestPath, initial.Revision), "schema 1 cannot");
                Check(Same(retained, Snapshot(repository)), "schema-1 facet declaration cannot mutate the repository");

                manifest["schemaVersion"] = 2;
                manifest.Remove("requiredHostFacets");
                Write();
                Check(ServicePluginRuntime.InspectPackage(source, manifestPath, requireMatchingDirectory: false)
                          .RequiredHostFacets is null,
                    "schema-2 packages without a facet requirement retain ordinary admission");
                manifest["coreMinimum"] = "1.0.0";
                Write();
                Refuse(() => lifecycle.Stage(manifestPath, initial.Revision), "unsupported or invalid fields");
                Check(Same(retained, Snapshot(repository)), "undeclared coreMinimum cannot bypass strict manifest admission");
                manifest.Remove("coreMinimum");
                manifest["requiredHostFacets"] = new JsonArray(ServicePluginContract.ScalarGenerationHostFacetV1);
                Write();
                var accepted = ServicePluginRuntime.InspectPackage(source, manifestPath, requireMatchingDirectory: false);
                Check(accepted.RequiredHostFacets!.SequenceEqual([ServicePluginContract.ScalarGenerationHostFacetV1]),
                    "schema-2 manifest admits the exact supported versioned facet");
                var stagedFacet = lifecycle.Stage(manifestPath, initial.Revision);
                Check(stagedFacet.StagedVersion == "1.0.1" && !File.Exists(Path.Combine(data, legacy.Id, "scalar-facet-initialized.txt")),
                    "valid requirement stages without executing candidate code");
                var candidatePath = Path.Combine(repository, ".packages", legacy.Id, stagedFacet.StagedGeneration!, ServicePluginRuntime.ManifestFileName);
                var candidateBytes = File.ReadAllBytes(candidatePath);
                var candidate = JsonNode.Parse(candidateBytes)!.AsObject();
                candidate["requiredHostFacets"] = new JsonArray("unknown.host/1");
                File.WriteAllText(candidatePath, candidate.ToJsonString(CerebralJson.Options));
                var tampered = Snapshot(repository);
                Refuse(() => lifecycle.Activate(legacy.Id, stagedFacet.Revision), "unavailable");
                Check(Same(tampered, Snapshot(repository)) && lifecycle.Inspect(legacy.Id).DesiredVersion == "1.0.0",
                    "activation refuses a changed unsupported candidate before retiring old selection");
                File.WriteAllBytes(candidatePath, candidateBytes);
                var selectedFacet = lifecycle.Activate(legacy.Id, stagedFacet.Revision);
                Check(selectedFacet.DesiredVersion == "1.0.1" && selectedFacet.RestartRequired &&
                      !File.Exists(Path.Combine(data, legacy.Id, "scalar-facet-initialized.txt")),
                    "valid facet selection still waits for Worker recomposition");
            }
            var selectedContext = LoadSelectedFacet(repository, data, legacy.Id, Check);
            for (var attempt = 0; attempt < 12 && selectedContext.IsAlive; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (selectedContext.IsAlive) Thread.Sleep(25);
            }
            Check(!selectedContext.IsAlive &&
                  File.Exists(Path.Combine(data, legacy.Id, "scalar-facet-disposed.txt")),
                "coherent candidate disposes its scalar lease and unloads its collectible context");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine($"Service host facet admission: {checks} deterministic CPU checks passed; no model, network or installed package effect.");
    }

    private static SortedDictionary<string, string> Snapshot(string directory) => Directory.Exists(directory)
        ? new(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))), StringComparer.Ordinal)
        : new(StringComparer.Ordinal);

    private static bool Same(SortedDictionary<string, string> left, SortedDictionary<string, string> right) =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadSelectedFacet(string repository, string data, string id,
        Action<bool, string> check)
    {
        using var current = new ServicePluginRuntime(repository, data, new EventHub());
        var selected = current.PackageBindings.Single();
        check(selected is { Loaded: true, Version: "1.0.1" } &&
              File.Exists(Path.Combine(data, id, "scalar-facet-initialized.txt")),
            "coherent Core and Worker load the required facet consumer");
        var context = AssemblyLoadContext.All.Single(value => value.IsCollectible && value.Assemblies.Any(assembly =>
            assembly.Location.Contains(selected.Generation!, StringComparison.Ordinal)));
        return new WeakReference(context);
    }
}

public sealed class ScalarFacetFixturePlugin : IServicePlugin
{
    private string? dataDirectory;
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host)
    {
        if (host is not IServiceScalarGenerationHostV1 scalar)
            throw new InvalidOperationException("The required scalar generation host facet was not supplied.");
        using var lease = scalar.OpenScalarGenerationObservations();
        dataDirectory = host.DataDirectory;
        File.WriteAllText(Path.Combine(host.DataDirectory, "scalar-facet-initialized.txt"), "initialized");
    }
    public void Dispose()
    {
        if (dataDirectory is not null)
            File.WriteAllText(Path.Combine(dataDirectory, "scalar-facet-disposed.txt"), "disposed");
    }
}
