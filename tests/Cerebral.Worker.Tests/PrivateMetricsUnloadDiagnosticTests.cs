using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

// Two private, task-owned probes distinguish package retirement from the status serializer.
// Every strong private reference remains inside ProbeScope; collection occurs after it returns.
internal static class PrivateMetricsUnloadDiagnosticTests
{
    private const string PackageId = "pegasus.metrics";
    private static readonly (string Name, string Hash, long Bytes)[] PackageFiles =
    [
        ("service-plugin.json", "f0544f1616192cf16e1f4ba653b3c8565eb573362329615dac735cc6c112a3c6", 522),
        ("Cerebral.MetricsPlugin.Service.dll", "87af3f09cd70d17176bdeda62e141dd110d99888bfb1d0018d87d0f3491278a2", 66048),
        ("Cerebral.MetricsPlugin.Shared.dll", "f07e11f1b31ede54d09d2d8e7dbd1f7c6f81ca62c6df0ed70f4fc2077c835491", 59392),
        ("Cerebral.MetricsPlugin.Service.deps.json", "8249277234fde501f821fe1c90785df2774122c0dd6c68a377420c4194253527", 939)
    ];
    private static readonly (string Name, string Hash, long Bytes)[] GeneratedPackageFiles =
    [
        ("service-plugin.json", "f0544f1616192cf16e1f4ba653b3c8565eb573362329615dac735cc6c112a3c6", 522),
        ("Cerebral.MetricsPlugin.Service.dll", "19cd71c7d5555566f3e8be8ae10502b3b0abe732eda0be34ec7c01d3e0ab2658", 64000),
        ("Cerebral.MetricsPlugin.Shared.dll", "6314cc5ae753d85bcb4eebfe4bc08b58a65b752a9917651306149d62b2e8c451", 179712),
        ("Cerebral.MetricsPlugin.Service.deps.json", "8249277234fde501f821fe1c90785df2774122c0dd6c68a377420c4194253527", 939)
    ];
    private static readonly (string Path, string Hash, long Bytes)[] HostFiles =
    [
        ("src/Cerebral.Core/bin/Release/net10.0/Cerebral.Core.dll", "659222b8452cec8632f75137ec539c5bc805662a28248d354d7da992bba36fef", 814080),
        ("src/Cerebral.Core/ServiceScalarGenerationObservation.cs", "d1d83a22f5c356a5a43e95e06d8025513c26c8fec573767792ce4a9ca80988ef", 2284),
        ("src/Cerebral.Worker/ServicePluginRuntime.cs", "8e3e93a912769c264276cbe4abb9c94d1be318dbb87a6019c36399e4b296500e", 82486)
    ];

    internal static void Run(string workspace, string assembledPackage, string fixtureParent) =>
        RunCases(workspace, assembledPackage, fixtureParent, cacheRootOnly: false, PackageFiles);

    internal static void RunCacheRoot(string workspace, string assembledPackage, string fixtureParent) =>
        RunCases(workspace, assembledPackage, fixtureParent, cacheRootOnly: true, PackageFiles);

    internal static void RunGenerated(string workspace, string assembledPackage, string fixtureParent) =>
        RunCases(workspace, assembledPackage, fixtureParent, cacheRootOnly: false, GeneratedPackageFiles);

    private static void RunCases(string workspace, string assembledPackage, string fixtureParent, bool cacheRootOnly,
        (string Name, string Hash, long Bytes)[] packageFiles)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(assembledPackage) ||
            !Path.IsPathFullyQualified(fixtureParent))
            throw new InvalidOperationException("The three diagnostic paths must be absolute workspace locations.");
        workspace = Path.GetFullPath(workspace);
        assembledPackage = Path.GetFullPath(assembledPackage);
        fixtureParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        if (!Directory.Exists(fixtureParent) ||
            !assembledPackage.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !fixtureParent.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The three diagnostic paths must be absolute existing workspace locations.");
        foreach (var file in HostFiles) Pin(Path.Combine(workspace, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.Hash, file.Bytes);
        foreach (var file in packageFiles) Pin(Path.Combine(assembledPackage, file.Name), file.Hash, file.Bytes);

        var failed = false;
        foreach (var statusOnly in cacheRootOnly ? new[] { true } : new[] { false, true })
        {
            var label = statusOnly ? "status-json" : "default-off";
            var root = Path.Combine(fixtureParent, "diagnostic-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var package = Path.Combine(root, "packages", PackageId);
                Directory.CreateDirectory(package);
                foreach (var file in packageFiles)
                {
                    var target = Path.Combine(package, file.Name);
                    File.Copy(Path.Combine(assembledPackage, file.Name), target, false);
                    Pin(target, file.Hash, file.Bytes);
                }
                var references = ProbeScope(root, package, statusOnly);
                // The completed scope has returned, including its synchronous wait for any status task.
                for (var round = 0; round < 3; round++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Console.WriteLine($"CASE {label}: root={root}");
                foreach (var (name, weak) in references)
                    Console.WriteLine($"  {name}={(weak is null ? "not-created" : weak.IsAlive ? "alive" : "collected")}");
                if (cacheRootOnly)
                {
                    var (accessor, entries, privateKeys) = InspectMemberCache(package);
                    Console.WriteLine($"  reflectionMemberAccessor={accessor}; cacheEntries={entries}; privateAssemblyKeys={privateKeys}");
                    if (privateKeys == 0) failed = true;
                }
                else if (references["context"]?.IsAlive == true) failed = true;
            }
            catch (Exception error)
            {
                failed = true;
                Console.WriteLine($"CASE {label}: root={root}; error={error.GetType().Name}: {error.Message}");
            }
        }
        if (failed) throw new InvalidOperationException("One or both private Metrics diagnostic cases failed or retained a collectible context; roots were preserved.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Accessor, int Entries, int PrivateKeys) InspectMemberCache(string package)
    {
        // Read the framework's existing cache only. Do not invoke its GetOrAdd or Clear methods.
        var resolver = typeof(System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver);
        var accessor = resolver.GetField("s_memberAccessor", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("The runtime has no initialized reflection member accessor.");
        var outerCache = accessor.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(accessor)
            ?? throw new InvalidOperationException("The runtime member accessor has no cache field.");
        var dictionary = (IEnumerable)(outerCache.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(outerCache)
            ?? throw new InvalidOperationException("The runtime member cache has no dictionary."));
        var entries = 0;
        var privateKeys = 0;
        foreach (var pair in dictionary)
        {
            entries++;
            var key = pair.GetType().GetProperty("Key")!.GetValue(pair)!;
            var keyType = key.GetType();
            var declaringType = (Type)keyType.GetField("Item2")!.GetValue(key)!;
            var member = (MemberInfo?)keyType.GetField("Item3")!.GetValue(key);
            if (Private(declaringType.Assembly) || member?.DeclaringType is { } owner && Private(owner.Assembly))
                privateKeys++;
        }
        return (accessor.GetType().Name, entries, privateKeys);

        bool Private(Assembly assembly) =>
            assembly.Location.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, WeakReference?> ProbeScope(string root, string package, bool statusOnly)
    {
        var data = Path.Combine(root, "data");
        var events = new EventHub();
        using var plugins = new ServicePluginRuntime(Path.GetDirectoryName(package)!, data, events);
        if (plugins.Status.Single() is not { Id: PackageId, Loaded: true } ||
            plugins.PackageBindings.Single() is not { Id: PackageId, Loaded: true } ||
            events.ScalarGenerationSubscriberCount != 0)
            throw new InvalidOperationException("Private package did not load in default capture-off state.");

        var loaded = (IEnumerable)typeof(ServicePluginRuntime)
            .GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugins)!;
        var item = loaded.Cast<object>().Single();
        var itemType = item.GetType();
        var context = (AssemblyLoadContext)itemType.GetProperty("Context")!.GetValue(item)!;
        var plugin = itemType.GetProperty("Plugin")!.GetValue(item)!;
        var host = itemType.GetProperty("Host")!.GetValue(item)!;
        var pluginType = plugin.GetType();
        var shared = context.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "Cerebral.MetricsPlugin.Shared");
        var maintenance = pluginType.GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin);
        var history = pluginType.GetField("history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin);
        var capture = pluginType.GetField("capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin);
        var references = new Dictionary<string, WeakReference?>
        {
            ["context"] = new(context), ["plugin"] = new(plugin), ["serviceAssembly"] = new(pluginType.Assembly),
            ["sharedAssembly"] = shared is null ? null : new(shared), ["host"] = new(host),
            ["maintenance"] = maintenance is null ? null : new(maintenance),
            ["history"] = history is null ? null : new(history),
            ["capture"] = capture is null ? null : new(capture)
        };

        if (statusOnly)
        {
            var command = plugins.CommandFor("metrics-history.status") ?? throw new InvalidOperationException("Status command missing.");
            var result = command.ExecuteAsync("metrics-history.status", CerebralJson.Element(new { }), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (result is not JsonElement json || json.ValueKind != JsonValueKind.Object ||
                json.GetProperty("captureActive").GetBoolean() ||
                json.GetProperty("settings").GetProperty("values").GetProperty("captureEnabled").GetBoolean() ||
                events.ScalarGenerationSubscriberCount != 0)
                throw new InvalidOperationException("Status-only JSON was invalid or unexpectedly enabled capture.");
        }
        plugins.Dispose();
        if (events.ScalarGenerationSubscriberCount != 0)
            throw new InvalidOperationException("Default-off retirement retained a scalar subscription.");
        WaitForWriterRelease(Path.Combine(data, PackageId, "history.lock"));
        return references;
    }

    private static void WaitForWriterRelease(string path)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            try { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException) { Thread.Sleep(10); }
        }
        throw new TimeoutException("The actual Metrics history writer lock was not released during retirement.");
    }

    private static void Pin(string path, string hash, long bytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != bytes ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Exact diagnostic input changed: " + path);
    }
}
