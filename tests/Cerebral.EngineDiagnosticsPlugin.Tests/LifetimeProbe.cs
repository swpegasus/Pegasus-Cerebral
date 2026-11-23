using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.EngineDiagnosticsPlugin.Tests;

internal static class LifetimeProbe
{
    internal static async Task RunAsync(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The probe must stay inside task-owned private artifacts.");
        var fixture = Path.Combine(root, "lifetime-" + Guid.NewGuid().ToString("N"));
        var references = await LoadThenRetireAsync(fixture);
        for (var attempt = 0; attempt < 3 && references.Any(x => x.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(100);
        }
        Console.WriteLine("Engine monitor collectible contexts still alive: " + references.Count(x => x.IsAlive));
        if (references.Count == 0 || references.Any(x => x.IsAlive)) throw new Exception("The monitor's native lifetime probe did not reclaim its context.");
        Console.WriteLine("PASS: actual monitor package/status/capture retires and its collectible context is reclaimed in a cold process.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> LoadThenRetireAsync(string root)
    {
        var packages = Path.Combine(root, "packages");
        var package = Path.Combine(packages, EngineDiagnosticsPlugin.Id);
        Directory.CreateDirectory(package);
        File.Copy(typeof(EngineDiagnosticsPlugin).Assembly.Location, Path.Combine(package, "Cerebral.EngineDiagnosticsPlugin.dll"));
        File.WriteAllText(Path.Combine(package, "service-plugin.json"), JsonSerializer.Serialize(new ServicePluginManifest
        {
            Id = EngineDiagnosticsPlugin.Id, Name = "Engine debug lifetime probe", Version = "1.0.0",
            Assembly = "Cerebral.EngineDiagnosticsPlugin.dll", EntryType = typeof(EngineDiagnosticsPlugin).FullName!
        }, CerebralJson.Options));
        var events = new EventHub();
        List<WeakReference> references;
        using (var runtime = new ServicePluginRuntime(packages, Path.Combine(root, "data"), events))
        {
            if (!runtime.Status.Single().Loaded) throw new Exception(runtime.Status.Single().Error);
            foreach (var operation in new[] { "engine-debug.set", "engine-debug.status" })
            {
                var command = runtime.CommandFor(operation)!;
                var result = await command.ExecuteAsync(operation, CerebralJson.Element(new { enabled = true }), default);
                // Exercise the same shared serializer used by the normal host command boundary.
                if (CerebralJson.Element(result).GetProperty("state").GetString() != "active")
                    throw new Exception("The actual package was not enabled.");
            }
            events.PublishDiagnostic(new(1, DateTimeOffset.UtcNow, "lifetime-chat", "request", "run", "effect", "agent", "worker", "model",
                "model.visible-fragment", "safe visible fixture output"));
            var log = Path.Combine(root, "data", EngineDiagnosticsPlugin.Id, "engine-debug.jsonl");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(log) || new FileInfo(log).Length == 0) await Task.Delay(10, deadline.Token);
            references = AssemblyLoadContext.All.Where(x => x.IsCollectible && x.Assemblies.Any(a =>
                    a.Location.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new WeakReference(x)).ToList();
        }
        if (events.HasDiagnosticSubscribers) throw new Exception("The retired runtime retained a diagnostic subscription.");
        return references;
    }
}
