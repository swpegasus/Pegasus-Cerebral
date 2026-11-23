using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class PluginDependencyTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-plugin-dependencies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var contexts = RunScope(root);
            for (var attempt = 0; attempt < 10 && contexts.Any(x => x.IsAlive); attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (contexts.Any(x => x.IsAlive)) Thread.Sleep(25);
            }
            if (contexts.Any(x => x.IsAlive)) throw new Exception("Dependency fixture context remained alive after disposal.");
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        await ServicePackageLifecycleTests.RunAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> RunScope(string root)
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        using (var noPlugins = new ServicePluginRuntime(Path.Combine(root, "absent-packages"),
                   Path.Combine(root, "absent-data"), new EventHub()))
        {
            Check(noPlugins.Status.Count == 0 && noPlugins.PackageBindings.Count == 0 &&
                  noPlugins.ToolExtensions.Count == 0 && noPlugins.WorkflowModules.Count == 0,
                "an absent optional plugin repository produces an empty contribution set without blocking the core host");
        }
        var precedence = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1" };
        Check(precedence.Zip(precedence.Skip(1)).All(pair => PluginVersion.Parse(pair.First).CompareTo(PluginVersion.Parse(pair.Second)) < 0) &&
              PluginVersion.Parse("1.2.3+build.1").CompareTo(PluginVersion.Parse("1.2.3+build.2")) == 0,
            "SemVer orders numeric prereleases correctly and excludes build metadata from precedence");
        Check(new[] { "1", "1.2", "1.2.3.4", "01.2.3", "1.2.3-alpha.01", "1.2.3-", "1.2.3+", "1.2.3-α", "1.2.3+bad+extra", "4294967296.0.0" }
                .All(value => !PluginVersion.TryParse(value, out _)) && PluginVersion.TryParse("1.2.3-alpha.99999999999999999999+001", out _),
            "package versions reject malformed and unbounded numeric cores without truncating long prerelease numbers");

        var packages = Path.Combine(root, "packages");
        var data = Path.Combine(root, "data");
        void Package(string id, string version = "1.0.0", params ServicePluginDependency[] dependencies)
        {
            var package = Path.Combine(packages, id);
            Directory.CreateDirectory(package);
            var assemblyName = Path.GetFileName(typeof(DependencyFixturePlugin).Assembly.Location);
            File.Copy(typeof(DependencyFixturePlugin).Assembly.Location, Path.Combine(package, assemblyName));
            var manifest = JsonSerializer.SerializeToNode(new ServicePluginManifest
            {
                Id = id, Name = "Dependency fixture", Version = version, Assembly = assemblyName,
                EntryType = typeof(DependencyFixturePlugin).FullName!, Dependencies = dependencies.ToList()
            }, CerebralJson.Options)!.AsObject();
            // Schema 1 does not carry the schema-2-only member, even as null.
            manifest.Remove("portableSettings");
            File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName), manifest.ToJsonString(CerebralJson.Options));
        }
        static ServicePluginDependency Exact(string id, string version = "1.0.0", bool optional = false) =>
            new() { Id = id, ExactVersion = version, Optional = optional };
        Package("a.consumer", "1.0.0", new ServicePluginDependency { Id = "z.base", MinimumVersion = "1.2.0-alpha.2", MaximumVersionExclusive = "2.0.0" });
        Package("b.optional", "1.0.0", Exact("absent.optional", optional: true), Exact("z.base", "9.0.0", true));
        Package("c.missing", "1.0.0", Exact("absent.required"));
        Package("d.transitive", "1.0.0", Exact("c.missing"));
        Package("e.exclusive", "1.0.0", new ServicePluginDependency { Id = "z.base", MinimumVersion = "1.0.0", MaximumVersionExclusive = "1.2.0-beta.1" });
        Package("f.failure");
        Package("g.failed-dependent", "1.0.0", Exact("f.failure"));
        Package("h.cycle", "1.0.0", Exact("i.cycle"));
        Package("i.cycle", "1.0.0", Exact("h.cycle"));
        Package("j.cycle-dependent", "1.0.0", Exact("h.cycle"));
        Package("k.optional-cycle", "1.0.0", Exact("h.cycle", optional: true));
        Package("l.overlap-cycle", "1.0.0", Exact("m.overlap-cycle"), Exact("n.overlap-cycle"));
        Package("m.overlap-cycle", "1.0.0", Exact("l.overlap-cycle"));
        Package("n.overlap-cycle", "1.0.0", Exact("m.overlap-cycle", optional: true));
        Package("o.duplicate", "1.0.0", Exact("z.base"), Exact("z.base"));
        Package("p.empty-range", "1.0.0", new ServicePluginDependency { Id = "z.base", MinimumVersion = "2.0.0", MaximumVersionExclusive = "1.0.0" });
        Package("q.ambiguous-range", "1.0.0", new ServicePluginDependency { Id = "z.base", ExactVersion = "1.0.0", MinimumVersion = "1.0.0" });
        Package("r.optional-present", "1.0.0", Exact("z.base", "1.2.0-beta.1", true));
        Package("s.four-parts", "1.0.0.0");
        Package("t.metadata", "1.0.0", Exact("z.metadata", "1.0.0+different"));
        Package("u.independent");
        Package("v.too-many", "1.0.0", Enumerable.Range(0, 33).Select(index => Exact("fixture." + index, optional: true)).ToArray());
        Package("w.unconstrained", "1.0.0", new ServicePluginDependency { Id = "z.base" });
        Package("x.private-binding");
        Package("y.bad-settings");
        Package("y.duplicate-manifest");
        Package("y.missing-schema");
        Package("y.missing-version");
        Package("y.wrong-case-version");
        Package("y.nested-duplicate-settings");
        Package("y.schema-one-null");
        Package("y.missing-settings-version");
        Package("z.base", "1.2.0-beta.1");
        Package("z.metadata", "1.0.0+source");
        var portableManifestPath = Path.Combine(packages, "z.base", ServicePluginRuntime.ManifestFileName);
        var portableManifest = JsonSerializer.Deserialize<ServicePluginManifest>(File.ReadAllText(portableManifestPath), CerebralJson.Options)!;
        portableManifest.SchemaVersion = 2;
        portableManifest.PortableSettings = new()
        {
            Version = 3,
            Fields =
            [
                new() { Name = "enabled", Type = "boolean" },
                new() { Name = "mode", Type = "string", Required = true, MaximumUtf8Bytes = 32, AllowedValues = ["brief", "detailed"] },
                new() { Name = "limits", Type = "string-list", MaximumUtf8Bytes = 16, MaximumItems = 4 }
            ]
        };
        File.WriteAllText(portableManifestPath, JsonSerializer.Serialize(portableManifest, CerebralJson.Options));
        var invalidSettingsPath = Path.Combine(packages, "y.bad-settings", ServicePluginRuntime.ManifestFileName);
        var invalidSettings = JsonSerializer.Deserialize<ServicePluginManifest>(File.ReadAllText(invalidSettingsPath), CerebralJson.Options)!;
        invalidSettings.SchemaVersion = 2;
        invalidSettings.PortableSettings = new()
        {
            Fields = [new() { Name = "duplicate", Type = "boolean" }, new() { Name = "duplicate", Type = "boolean" }]
        };
        File.WriteAllText(invalidSettingsPath, JsonSerializer.Serialize(invalidSettings, CerebralJson.Options));
        string ManifestPath(string id) => Path.Combine(packages, id, ServicePluginRuntime.ManifestFileName);
        var duplicateManifestPath = ManifestPath("y.duplicate-manifest");
        var duplicateManifest = File.ReadAllText(duplicateManifestPath);
        File.WriteAllText(duplicateManifestPath, duplicateManifest.Insert(duplicateManifest.IndexOf('{') + 1,
            "\"SchemaVersion\":1,"));
        var missingSchemaPath = ManifestPath("y.missing-schema");
        var missingSchema = JsonNode.Parse(File.ReadAllText(missingSchemaPath))!.AsObject();
        missingSchema.Remove("schemaVersion");
        File.WriteAllText(missingSchemaPath, missingSchema.ToJsonString(CerebralJson.Options));
        var missingVersionPath = ManifestPath("y.missing-version");
        var missingVersion = JsonNode.Parse(File.ReadAllText(missingVersionPath))!.AsObject();
        missingVersion.Remove("version");
        File.WriteAllText(missingVersionPath, missingVersion.ToJsonString(CerebralJson.Options));
        var wrongCaseVersionPath = ManifestPath("y.wrong-case-version");
        var wrongCaseVersion = JsonNode.Parse(File.ReadAllText(wrongCaseVersionPath))!.AsObject();
        wrongCaseVersion["Version"] = wrongCaseVersion["version"]!.DeepClone();
        wrongCaseVersion.Remove("version");
        File.WriteAllText(wrongCaseVersionPath, wrongCaseVersion.ToJsonString(CerebralJson.Options));
        var nestedDuplicatePath = ManifestPath("y.nested-duplicate-settings");
        var nestedDuplicate = JsonNode.Parse(File.ReadAllText(nestedDuplicatePath))!.AsObject();
        nestedDuplicate["schemaVersion"] = 2;
        nestedDuplicate["portableSettings"] = new JsonObject
        {
            ["version"] = 1,
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "enabled", ["type"] = "boolean" })
        };
        var nestedDuplicateText = nestedDuplicate.ToJsonString(CerebralJson.Options);
        var portableStart = nestedDuplicateText.IndexOf("\"portableSettings\"", StringComparison.Ordinal);
        var nestedVersion = nestedDuplicateText.IndexOf("\"version\": 1", portableStart, StringComparison.Ordinal);
        File.WriteAllText(nestedDuplicatePath, nestedDuplicateText.Insert(nestedVersion, "\"Version\": 1, "));
        var schemaOneNullPath = ManifestPath("y.schema-one-null");
        var schemaOneNull = JsonNode.Parse(File.ReadAllText(schemaOneNullPath))!.AsObject();
        schemaOneNull["portableSettings"] = null;
        File.WriteAllText(schemaOneNullPath, schemaOneNull.ToJsonString(CerebralJson.Options));
        var missingSettingsVersionPath = ManifestPath("y.missing-settings-version");
        var missingSettingsVersion = JsonNode.Parse(File.ReadAllText(missingSettingsVersionPath))!.AsObject();
        missingSettingsVersion["schemaVersion"] = 2;
        missingSettingsVersion["portableSettings"] = new JsonObject
        {
            ["fields"] = new JsonArray(new JsonObject
                { ["name"] = "enabled", ["type"] = "boolean", ["required"] = false })
        };
        File.WriteAllText(missingSettingsVersionPath, missingSettingsVersion.ToJsonString(CerebralJson.Options));

        List<string> successful;
        List<WeakReference> contexts;
        using (var plugins = new ServicePluginRuntime(packages, data, new EventHub()))
        {
            var packageBindings = plugins.PackageBindings;
            successful = plugins.Status.Where(x => x.Loaded).Select(x => x.Id).ToList();
            if (!successful.SequenceEqual(["z.base", "a.consumer", "b.optional", "k.optional-cycle", "r.optional-present", "u.independent", "z.metadata"]))
                Console.WriteLine("Plugin admission diagnostics: " + string.Join(" | ", plugins.Status.Select(x =>
                    x.Id + "=" + (x.Loaded ? "loaded" : x.Error))));
            Check(successful.SequenceEqual(["z.base", "a.consumer", "b.optional", "k.optional-cycle", "r.optional-present", "u.independent", "z.metadata"]),
                "the real DLL loader initializes a deterministic dependency order and leaves unrelated packages available");
            Check(plugins.Status.Single(x => x.Id == "a.consumer").Loaded &&
                  packageBindings.Single(x => x.Id == "a.consumer").Dependencies.Single() is { Available: true, ResolvedVersion: "1.2.0-beta.1" },
                "required range resolution records the exact loaded prerelease version");
            var optional = packageBindings.Single(x => x.Id == "b.optional");
            Check(optional.Loaded && optional.Dependencies.Count == 2 && optional.Dependencies.All(x => !x.Available && x.Optional && x.Reason is not null) &&
                  optional.Dependencies.Single(x => x.Id == "z.base").ResolvedVersion == "1.2.0-beta.1" &&
                  File.ReadAllText(Path.Combine(data, "b.optional", "dependency-state.txt")).Contains("z.base:False:1.2.0-beta.1", StringComparison.Ordinal),
                "missing and incompatible optional dependencies are unavailable in both diagnostics and the plugin host");
            Check(packageBindings.Single(x => x.Id == "r.optional-present").Dependencies.Single().Available,
                "an installed compatible optional dependency is available before its consumer initializes");
            Check(new[] { "c.missing", "d.transitive", "e.exclusive", "g.failed-dependent", "j.cycle-dependent", "t.metadata" }
                    .All(id => !plugins.Status.Single(x => x.Id == id).Loaded && !File.Exists(Path.Combine(data, id, "dependency-state.txt"))),
                "missing, exclusive upper bound, failed, cyclic, and exact-metadata mismatches stop dependent initialization");
            Check(new[] { "h.cycle", "i.cycle", "l.overlap-cycle", "m.overlap-cycle", "n.overlap-cycle" }
                    .All(id => plugins.Status.Single(x => x.Id == id).Error!.Contains("cycle", StringComparison.OrdinalIgnoreCase)) &&
                  packageBindings.Single(x => x.Id == "k.optional-cycle").Dependencies.Single() is { Available: false, Optional: true },
                "cycles including overlapping optional edges block all cycle members but not optional consumers outside the cycle");
            Check(new[] { "o.duplicate", "p.empty-range", "q.ambiguous-range", "s.four-parts", "v.too-many", "w.unconstrained" }
                    .All(id => !plugins.Status.Single(x => x.Id == id).Loaded && !Directory.Exists(Path.Combine(data, id))),
                "duplicate, ambiguous, unbounded, oversized and malformed metadata is rejected before plugin construction");
            Check(plugins.Status.Single(x => x.Id == "x.private-binding") is { Loaded: false, Error: { } privateError } &&
                  privateError.Contains("Cerebral.Tools", StringComparison.Ordinal),
                "a package cannot silently bind an undeclared private assembly from the Worker default load context");
            Check(packageBindings is not ServicePluginPackageBinding[] && packageBindings.All(x => x.Dependencies is not ServicePluginDependencyBinding[]),
                "resolved package snapshots do not expose mutable dependency arrays");
            Check(plugins.PortableSettingsSchemas.Single() is { Key: "z.base", Value: { Version: 3 } } schema &&
                  schema.Value.Fields.Count == 3 && schema.Value.Fields.All(field => field.AllowedValues is not List<string>),
                "schema 2 exposes an immutable validated portable-settings declaration without executing settings code");
            Check(!plugins.Status.Single(x => x.Id == "y.bad-settings").Loaded &&
                  !plugins.PortableSettingsSchemas.ContainsKey("y.bad-settings"),
                "invalid portable-settings declarations fail before plugin construction or publication");
            Check(new[] { "y.duplicate-manifest", "y.missing-schema", "y.missing-version", "y.wrong-case-version",
                        "y.nested-duplicate-settings", "y.schema-one-null", "y.missing-settings-version" }
                    .All(id => !plugins.Status.Single(x => x.Id == id).Loaded && !Directory.Exists(Path.Combine(data, id))),
                "duplicate, case-aliased, omitted and null manifest contract fields fail before plugin construction");
            contexts = AssemblyLoadContext.All.Where(x => x.IsCollectible && x.Assemblies.Any(a =>
                a.Location.StartsWith(packages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new WeakReference(x)).ToList();
        }
        var stopped = File.ReadAllLines(Path.Combine(data, "order.log")).Where(x => x.StartsWith("stop ", StringComparison.Ordinal))
            .Select(x => x[5..]).Where(successful.Contains).ToList();
        Check(stopped.SequenceEqual(successful.AsEnumerable().Reverse()), "plugins dispose consumers before their dependencies");
        Console.WriteLine($"Plugin dependencies: {checks} checks passed (real loader with native deterministic fixture DLLs; no model or external provider).");
        return contexts;
    }
}

/// <summary>Host-neutral fixture exercises actual collectible loading and dependency availability.</summary>
public sealed class DependencyFixturePlugin : IServicePlugin
{
    private string? pluginId;
    private string? log;
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host)
    {
        pluginId = host.PluginId;
        log = Path.Combine(Path.GetDirectoryName(host.DataDirectory)!, "order.log");
        File.AppendAllLines(log, ["start " + pluginId]);
        if (pluginId == "f.failure") throw new InvalidOperationException("Deliberate dependency fixture initialization failure.");
        if (pluginId == "x.private-binding") _ = PrivateHostAssembly();
        File.WriteAllLines(Path.Combine(host.DataDirectory, "dependency-state.txt"),
            host.Dependencies.Select(x => x.Id + ":" + x.Available + ":" + x.ResolvedVersion));
    }
    public void Dispose()
    {
        if (log is not null) File.AppendAllLines(log, ["stop " + pluginId]);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string PrivateHostAssembly() => typeof(Cerebral.Tools.ToolDispatcher).FullName!;
}
