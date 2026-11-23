using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static class PrivateMetricsCombinedTests
{
    private const string PackageId = "pegasus.metrics";
    private static readonly (string Name, string Sha256, long Bytes)[] PackageFiles =
    [
        ("service-plugin.json", "f0544f1616192cf16e1f4ba653b3c8565eb573362329615dac735cc6c112a3c6", 522),
        ("Cerebral.MetricsPlugin.Service.dll", "87af3f09cd70d17176bdeda62e141dd110d99888bfb1d0018d87d0f3491278a2", 66048),
        ("Cerebral.MetricsPlugin.Shared.dll", "f07e11f1b31ede54d09d2d8e7dbd1f7c6f81ca62c6df0ed70f4fc2077c835491", 59392),
        ("Cerebral.MetricsPlugin.Service.deps.json", "8249277234fde501f821fe1c90785df2774122c0dd6c68a377420c4194253527", 939)
    ];
    private static readonly (string Name, string Sha256, long Bytes)[] GeneratedPackageFiles =
    [
        ("service-plugin.json", "f0544f1616192cf16e1f4ba653b3c8565eb573362329615dac735cc6c112a3c6", 522),
        ("Cerebral.MetricsPlugin.Service.dll", "19cd71c7d5555566f3e8be8ae10502b3b0abe732eda0be34ec7c01d3e0ab2658", 64000),
        ("Cerebral.MetricsPlugin.Shared.dll", "6314cc5ae753d85bcb4eebfe4bc08b58a65b752a9917651306149d62b2e8c451", 179712),
        ("Cerebral.MetricsPlugin.Service.deps.json", "8249277234fde501f821fe1c90785df2774122c0dd6c68a377420c4194253527", 939)
    ];
    private static readonly (string Path, string Sha256, long Bytes)[] HostPins =
    [
        ("src/Cerebral.Core/bin/Release/net10.0/Cerebral.Core.dll", "659222b8452cec8632f75137ec539c5bc805662a28248d354d7da992bba36fef", 814080),
        ("src/Cerebral.Core/ServiceScalarGenerationObservation.cs", "d1d83a22f5c356a5a43e95e06d8025513c26c8fec573767792ce4a9ca80988ef", 2284),
        ("src/Cerebral.Worker/ServicePluginRuntime.cs", "8e3e93a912769c264276cbe4abb9c94d1be318dbb87a6019c36399e4b296500e", 82486),
        ("build/Core/OwnerQuiescence/MetricsHostFacetCandidate/RequiredHostFacetAdmission/host-facet-admission.source-check.json",
            "55a3ab92d6f3173dfb0591e4f821ebbcd2204bb13dc88fba1e39ed77355a8fd3", 3251)
    ];

    internal static void Run(string workspace, string assembledPackage, string fixtureParent) =>
        RunPinned(workspace, assembledPackage, fixtureParent, PackageFiles);

    internal static void RunGenerated(string workspace, string assembledPackage, string fixtureParent) =>
        RunPinned(workspace, assembledPackage, fixtureParent, GeneratedPackageFiles);

    private static void RunPinned(string workspace, string assembledPackage, string fixtureParent,
        (string Name, string Sha256, long Bytes)[] packageFiles)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(assembledPackage) ||
            !Path.IsPathFullyQualified(fixtureParent))
            throw new ArgumentException("Exact absolute workspace, assembled package, and fixture parent are required.");
        workspace = Path.GetFullPath(workspace);
        assembledPackage = Path.GetFullPath(assembledPackage);
        fixtureParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        if (!Directory.Exists(fixtureParent) || !assembledPackage.StartsWith(workspace + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !fixtureParent.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The assembled package and fixture parent must be existing workspace locations.");
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Private Metrics combined fixture: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        static void Pin(string path, string sha256, long bytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != bytes ||
                !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The exact reviewed input changed: " + path);
        }
        foreach (var input in HostPins)
        {
            Pin(Path.Combine(workspace, input.Path.Replace('/', Path.DirectorySeparatorChar)), input.Sha256, input.Bytes);
            checks++;
        }
        foreach (var input in packageFiles)
        {
            Pin(Path.Combine(assembledPackage, input.Name), input.Sha256, input.Bytes);
            checks++;
        }
        var root = Path.Combine(fixtureParent, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var completed = false;
        try
        {
            var package = Path.Combine(root, "packages", PackageId);
            Directory.CreateDirectory(package);
            foreach (var input in packageFiles)
            {
                var copied = Path.Combine(package, input.Name);
                File.Copy(Path.Combine(assembledPackage, input.Name), copied, false);
                Pin(copied, input.Sha256, input.Bytes);
                checks++;
            }
            var manifest = ServicePluginRuntime.InspectPackage(package,
                Path.Combine(package, ServicePluginRuntime.ManifestFileName));
            Check(manifest.SchemaVersion == 2 && manifest.Version == "0.2.0" &&
                  manifest.RequiredHostFacets?.SequenceEqual([ServicePluginContract.ScalarGenerationHostFacetV1]) == true,
                "actual assembled manifest declares the admitted versioned host facet");
            var data = Path.Combine(root, "data");
            var events = new EventHub();
            var context = ExerciseScope(root, package, data, events, Check);
            for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (context.IsAlive) Thread.Sleep(25);
            }
            Check(!context.IsAlive, "private service load context actually unloads after pump retirement");
            completed = true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Private Metrics combined fixture retained at " + root +
                " after failure: " + error.Message, error);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Path.GetDirectoryName(root) != fixtureParent ||
                !Path.GetFileName(root).StartsWith("fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside its exact task-owned parent.");
            if (completed && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Private Metrics combined: {checks} actual loader/owner-command/deterministic EventHub checks passed; no Worker process, model, GPU or network.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseScope(string root, string package, string data,
        EventHub events, Action<bool, string> check) =>
        ExerciseAsync(root, package, data, events, check).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExerciseAsync(string root, string package, string data,
        EventHub events, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var plugins = new ServicePluginRuntime(Path.GetDirectoryName(package)!, data, events);
        check(plugins.Status.Single() is { Id: PackageId, Loaded: true } &&
              plugins.PackageBindings.Single() is { Id: PackageId, Loaded: true } &&
              plugins.ToolExtensions.Count == 0 && events.ScalarGenerationSubscriberCount == 0,
            "actual private service loads with capture off, no reader and no agent tool");
        var context = AssemblyLoadContext.All.Single(value => value.IsCollectible && value.Assemblies.Any(assembly =>
            assembly.Location.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        var weak = new WeakReference(context);

        var store = new StateStore(Path.Combine(root, "state"));
        var configuration = new Configuration(store);
        await using var models = new ModelRuntime(store, configuration.Get, events);
        await using var tools = new ToolDispatcher(configuration.Get);
        var workflows = new WorkerWorkflows(store, configuration, events, [], []);
        using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), models, events,
            NullLogger<JobScheduler>.Instance, workflows);
        var dispatcher = new CommandDispatcher(store, configuration, models, scheduler, tools, events,
            servicePlugins: plugins, workflows: workflows);
        async Task<JsonElement> Owner(string operation, object args)
        {
            var result = await dispatcher.ExecuteAsync(new(operation, CerebralJson.Element(args)), deadline.Token);
            if (!result.Success) throw new InvalidOperationException(operation + ": " + result.Error);
            if (result.Data.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(operation + " did not cross the owner command boundary as JSON.");
            return result.Data;
        }
        var status = await Owner("metrics-history.status", new { });
        check(!status.GetProperty("captureActive").GetBoolean() &&
              !status.GetProperty("settings").GetProperty("values").GetProperty("captureEnabled").GetBoolean() &&
              events.ScalarGenerationSubscriberCount == 0,
            "owner status reports lazy capture-off default and no subscription");
        var enabled = await Owner("metrics-history.settings.set", new
        {
            expectedRevision = 0, values = new { captureEnabled = true, retentionDays = 30, maximumBytes = 64L * 1024 * 1024 }
        });
        check(enabled.GetProperty("revision").GetInt64() == 1 &&
              events.ScalarGenerationSubscriberCount == 1,
            "owner enable opens exactly one real scalar host subscription");
        status = await Owner("metrics-history.status", new { });
        check(status.GetProperty("captureActive").GetBoolean() &&
              status.GetProperty("sourceState").GetString() == "available",
            "owner status distinguishes requested capture from active source");

        var installation = "fixture-installation";
        var process = Guid.NewGuid().ToString("N");
        var lane = Guid.NewGuid().ToString("N");
        var secret = "private-chat-request-run-effect-agent";
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        GenerationTelemetrySample Sample(int output, string state, int revision) => new(
            "cerebral.generation-metrics/1", lane, "model-instance", "fixture-model", "fixture-adapter",
            new(secret, secret, secret, secret, secret, secret), state, "output",
            10, output, 2.0, 4.5, 100L * revision, observedAt.AddMilliseconds(revision));
        events.PublishScalarGeneration(installation, process, Sample(2, "active", 1), ScalarGenerationTargetOriginV1.Local);
        events.PublishScalarGeneration(installation, process, Sample(5, "active", 2), ScalarGenerationTargetOriginV1.Local);
        events.PublishScalarGeneration(installation, process, Sample(7, "Stop", 3), ScalarGenerationTargetOriginV1.Local);
        var query = new { from = observedAt.AddMinutes(-1), to = observedAt.AddMinutes(1), limit = 100 };
        JsonElement result;
        while (true)
        {
            result = await Owner("metrics-history.query", query);
            var rows = result.GetProperty("rows");
            if (rows.GetArrayLength() == 1 &&
                rows[0].GetProperty("providerObservedOutputTokens").GetInt64() == 7) break;
            await Task.Delay(25, deadline.Token);
        }
        var row = result.GetProperty("rows")[0];
        var summary = result.GetProperty("summary");
        check(row.GetProperty("state").GetString() == "Completed" &&
              row.GetProperty("providerObservedInputTokens").GetInt64() == 10 &&
              row.GetProperty("confirmedOutputTokens").ValueKind == JsonValueKind.Null &&
              summary.GetProperty("providerObservedOutputTokens").GetProperty("value").GetInt64() == 7 &&
              summary.GetProperty("outputTokens").GetProperty("value").ValueKind == JsonValueKind.Null,
            "same-lane cumulative progress replaces earlier revisions and confirmed usage stays unknown");
        check(!result.GetRawText().Contains(secret, StringComparison.Ordinal) &&
              result.GetProperty("schema").GetString() is not null,
            "owner query crosses as JSON without private workflow correlation");

        events.PublishScalarGeneration(installation, process, Sample(-1, "active", 4),
            ScalarGenerationTargetOriginV1.Local);
        status = await Owner("metrics-history.status", new { });
        check(status.GetProperty("coverage").GetProperty("sourceDropped").GetInt64() >= 1 &&
              status.GetProperty("coverage").GetProperty("partial").GetBoolean(),
            "host-rejected numeric frame remains visible as incomplete source coverage");
        var disabled = await Owner("metrics-history.settings.set", new
        {
            expectedRevision = 1, values = new { captureEnabled = false, retentionDays = 30, maximumBytes = 64L * 1024 * 1024 }
        });
        check(disabled.GetProperty("revision").GetInt64() == 2 &&
              events.ScalarGenerationSubscriberCount == 0,
            "owner opt-out detaches the real host subscription");
        result = await Owner("metrics-history.query", query);
        check(result.GetProperty("rows").GetArrayLength() == 1 &&
              result.GetProperty("rows")[0].GetProperty("providerObservedOutputTokens").GetInt64() == 7,
            "opt-out retains the bounded history row");
        var reenabled = await Owner("metrics-history.settings.set", new
        {
            expectedRevision = 2, values = new { captureEnabled = true, retentionDays = 30, maximumBytes = 64L * 1024 * 1024 }
        });
        check(reenabled.GetProperty("revision").GetInt64() == 3 &&
              events.ScalarGenerationSubscriberCount == 1,
            "a second owner opt-in opens one new host lease");
        plugins.Dispose();
        check(events.ScalarGenerationSubscriberCount == 0, "host retirement detaches its leased reader");
        var lockPath = Path.Combine(data, PackageId, "history.lock");
        while (true)
        {
            try
            {
                using var exclusive = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) { await Task.Delay(25, deadline.Token); }
        }
        check(true, "actual asynchronous reader and maintenance retirement releases root writer lock");
        return weak;
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("The combined Metrics fixture never admits generation.");
    }
}
