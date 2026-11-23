using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ServicePackageStageIdentityTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(temporary, "cerebral-stage-identity-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var assembly = typeof(ServicePackageStageSentinel).Assembly.Location;
            File.Copy(assembly, Path.Combine(source, Path.GetFileName(assembly)));
            var manifest = new ServicePluginManifest
            {
                Id = "fixture.guarded", Name = "Guarded stage fixture", Version = "1.0.0",
                Assembly = Path.GetFileName(assembly), EntryType = typeof(ServicePackageStageSentinel).FullName!
            };
            var json = JsonSerializer.SerializeToNode(manifest, CerebralJson.Options)!.AsObject();
            json.Remove("portableSettings");
            var manifestPath = Path.Combine(source, ServicePluginRuntime.ManifestFileName);
            File.WriteAllText(manifestPath, json.ToJsonString(CerebralJson.Options));
            var repositoryParent = Path.Combine(root, "repository-parent");
            Directory.CreateDirectory(repositoryParent);
            var repository = Path.Combine(repositoryParent, "packages");
            var pluginData = Path.Combine(root, "plugin-data");
            Directory.CreateDirectory(pluginData);
            File.WriteAllText(Path.Combine(pluginData, "owner-marker.txt"), "retained owner data");
            var savedData = Snapshot(pluginData);
            using var native = new ServicePluginRuntime(repository, pluginData, new EventHub());
            var lifecycleEvents = new EventHub();
            var observation = lifecycleEvents.Subscribe();
            var lifecycle = new ServicePackageLifecycle(repository, native, lifecycleEvents);
            Check(typeof(ServicePackageLifecycle).GetMethod("Stage", [typeof(string), typeof(long)]) is not null,
                "the original two-argument CLR method remains available");
            Reject(() => lifecycle.Stage(manifestPath, 0, "fixture.other"));
            Check(!Directory.Exists(repository) && Same(savedData, Snapshot(pluginData)),
                "mismatch creates no candidate or lifecycle repository and preserves plugin data");
            Reject(() => lifecycle.Stage(manifestPath, 0, "Fixture.guarded"));
            Check(!Directory.Exists(repository), "reviewed identity comparison is ordinal");
            var staged = lifecycle.Stage(manifestPath, 0, manifest.Id);
            Check(staged.Revision == 1 && staged.StagedGeneration is not null && !staged.ObservedLoaded,
                "a matching reviewed identity stages immutable code without loading it");
            Check(File.Exists(Path.Combine(repository, ".packages", manifest.Id, staged.StagedGeneration!, manifest.Assembly)),
                "the matching candidate is copied into its distinct generation");
            var retained = Snapshot(repository);
            Reject(() => lifecycle.Stage(manifestPath, staged.Revision, "fixture.other"));
            Check(Same(retained, Snapshot(repository)) && Same(savedData, Snapshot(pluginData)),
                "mismatch preserves every retained candidate/state/data byte");
            Reject(() => lifecycle.Stage(manifestPath, 0, manifest.Id));
            Check(Same(retained, Snapshot(repository)), "stale exact revision remains rejected without changes");
            var legacy = lifecycle.Stage(manifestPath, staged.Revision);
            Check(legacy.Revision == 2, "the old source caller stages through unchanged legacy semantics");
            var explicitNull = lifecycle.Stage(manifestPath, legacy.Revision, null);
            Check(explicitNull.Revision == 3, "a nullable internal guard preserves the legacy path");

            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store);
            var events = new EventHub();
            await using var models = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            var workflows = new WorkerWorkflows(store, configuration, events, [], []);
            lifecycle.Attach(workflows.Store);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, workflows);
            var dispatcher = new CommandDispatcher(store, configuration, models, scheduler, tools, events,
                workflows: workflows, servicePackageLifecycle: lifecycle);
            var described = await dispatcher.ExecuteAsync(new("sdk.describe", CerebralJson.Element(new { id = "service-packages.stage" })));
            Check(described.Success, "the actual updated owner dispatcher advertises the stage operation");
            var descriptor = described.Data;
            var schema = descriptor.GetProperty("inputSchema");
            var identity = schema.GetProperty("properties").GetProperty("expectedId");
            Check(descriptor.GetProperty("ownerOnly").GetBoolean() && descriptor.GetProperty("version").GetInt32() == 1,
                "stage remains owner-only on the existing operation version");
            Check(identity.GetProperty("type").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["string", "null"]) &&
                identity.GetProperty("minLength").GetInt32() == 1 && identity.GetProperty("maxLength").GetInt32() == 100,
                "advertised optional identity bounds match string/null forwarding");
            Check(schema.GetProperty("required").EnumerateArray().Select(x => x.GetString())
                    .SequenceEqual(["manifestPath", "expectedRevision"]) &&
                !schema.GetProperty("additionalProperties").GetBoolean(),
                "existing required fields and closed schema remain preserved");
            var before = Snapshot(repository);
            var rejected = await dispatcher.ExecuteAsync(new("service-packages.stage",
                CerebralJson.Element(new { manifestPath, expectedRevision = 3, expectedId = "fixture.other" })));
            Check(!rejected.Success && Same(before, Snapshot(repository)), "actual owner forwarding rejects identity mismatch before changes");
            foreach (var invalid in new object[] { 3, true, new { id = manifest.Id }, new[] { manifest.Id }, "", "\0", " " })
            {
                var result = await dispatcher.ExecuteAsync(new("service-packages.stage",
                    CerebralJson.Element(new { manifestPath, expectedRevision = 3, expectedId = invalid })));
                Check(!result.Success && Same(before, Snapshot(repository)), "malformed non-null identity cannot become an unguarded stage");
            }
            store.Put("agent", "administrator", new AgentProfile { Id = "administrator", Name = "Fixture administrator", Capabilities = ["administration"] });
            var forbidden = await dispatcher.ExecuteAsync(new("service-packages.stage",
                CerebralJson.Element(new { manifestPath, expectedRevision = 3, expectedId = manifest.Id }), "administrator"));
            Check(!forbidden.Success && forbidden.Error?.Contains("owner interface", StringComparison.OrdinalIgnoreCase) == true &&
                Same(before, Snapshot(repository)), "administration agents still cannot stage packages");
            var hidden = await dispatcher.ExecuteAsync(new("sdk.describe", CerebralJson.Element(new { id = "service-packages.stage" }), "administrator"));
            Check(!hidden.Success, "agent discovery does not expose owner-only stage capability");
            var matched = await dispatcher.ExecuteAsync(new("service-packages.stage",
                CerebralJson.Element(new { manifestPath, expectedRevision = 3, expectedId = manifest.Id })));
            Check(matched.Success && matched.Data.GetProperty("revision").GetInt64() == 4,
                "actual owner forwarding adopts the matching guard");
            var old = await dispatcher.ExecuteAsync(new("service-packages.stage", CerebralJson.Element(new { manifestPath, expectedRevision = 4 })));
            Check(old.Success && old.Data.GetProperty("revision").GetInt64() == 5, "the old wire caller still works");
            var nullWire = await dispatcher.ExecuteAsync(new("service-packages.stage",
                CerebralJson.Element(new { manifestPath, expectedRevision = 5, expectedId = (string?)null })));
            Check(nullWire.Success && nullWire.Data.GetProperty("revision").GetInt64() == 6, "explicit-null wire caller remains legacy");
            Check(Same(savedData, Snapshot(pluginData)), "all stage and owner checks preserve unrelated plugin data");
            Check(lifecycle.Inspect(manifest.Id).ActiveRuns == 0, "staging did not create workflow work or pins");

            var beforeStatusFailure = lifecycle.Inspect(manifest.Id);
            var malformedRun = workflows.Store.Submit(new Cerebral.Contracts.Workflows.WorkflowSubmission(
                "stage-status-failure", "stage-status-failure-chat", "owner", "unrelated.fixture:sample", "1.0.0",
                "Deterministic invalid retained-binding fixture.", null,
                Cerebral.Contracts.Workflows.WorkflowContract.EmptyObject,
                Cerebral.Contracts.Workflows.WorkflowContract.EmptyObject));
            try
            {
                var failedReply = await dispatcher.ExecuteAsync(new("service-packages.stage",
                    CerebralJson.Element(new { manifestPath, expectedRevision = beforeStatusFailure.Revision, expectedId = manifest.Id })));
                Check(!failedReply.Success, "the actual host can report failure from post-commit status calculation");
                using var committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, ".lifecycle", manifest.Id + ".json")));
                Check(committed.RootElement.GetProperty("revision").GetInt64() == beforeStatusFailure.Revision + 1 &&
                    committed.RootElement.GetProperty("stagedGeneration").GetString() != beforeStatusFailure.StagedGeneration,
                    "a generic failed reply can coexist with a committed new candidate/state");
            }
            finally { workflows.Store.Cancel(malformedRun.Id); } // Cancel only the invalid row created by this fixture.
            var inspectedAfterFailure = await dispatcher.ExecuteAsync(new("service-packages.inspect", CerebralJson.Element(new { id = manifest.Id })));
            Check(inspectedAfterFailure.Success && inspectedAfterFailure.Data.GetProperty("revision").GetInt64() == beforeStatusFailure.Revision + 1,
                "read-only current-state inspection observes the committed candidate without repeating stage");
            Check(Same(savedData, Snapshot(pluginData)), "post-commit status failure preserves unrelated plugin data");
            File.WriteAllText(Path.Combine(repositoryParent, "parent-marker.txt"), "parent must remain intact");
            foreach (var unsafeId in new[] { ".", ".." })
            {
                // Refuse to exercise lifecycle operations unless this exact runtime already rejects the ID.
                var denied = false;
                try { ServicePluginRuntime.ValidatePackageId(unsafeId); }
                catch (InvalidDataException) { denied = true; }
                Check(denied, "the runtime denies the exact dot segment before any path operation");
                while (observation.Reader.TryRead(out _)) { }
                json["id"] = unsafeId;
                File.WriteAllText(manifestPath, json.ToJsonString(CerebralJson.Options));
                var parentBefore = Snapshot(repositoryParent);
                Reject(() => lifecycle.Inspect(unsafeId));
                Reject(() => lifecycle.Activate(unsafeId, 0));
                Reject(() => lifecycle.Deactivate(unsafeId, 0));
                Reject(() => lifecycle.Remove(unsafeId, 0));
                Reject(() => lifecycle.Stage(manifestPath, 0));
                Reject(() => lifecycle.Stage(manifestPath, 0, unsafeId));
                Check(Same(parentBefore, Snapshot(repositoryParent)), "invalid ID leaves repository and its fixture parent byte-identical");
                Check(!observation.Reader.TryRead(out _), "invalid ID emits no lifecycle event");
                json["id"] = manifest.Id;
                File.WriteAllText(manifestPath, json.ToJsonString(CerebralJson.Options));
            }
            ServicePluginRuntime.ValidatePackageId("normal.dotted-package_1");
            Check(true, "established dotted IDs remain valid");
            lifecycleEvents.Unsubscribe(observation.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the exact test temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine($"Stage identity: {checks} actual filesystem/SQLite/direct-dispatch checks passed; no network, model or Source activation.");
    }

    private static SortedDictionary<string, string> Snapshot(string directory) => Directory.Exists(directory)
        ? new(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))), StringComparer.Ordinal)
        : new(StringComparer.Ordinal);

    private static bool Same(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual) =>
        expected.SequenceEqual(actual);

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected stage rejection.");
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("No generation is admitted by this test.");
    }
}

public sealed class ServicePackageStageSentinel : IServicePlugin
{
    public ServicePackageStageSentinel() => throw new InvalidOperationException("Staging must not construct plugin code.");
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) => throw new InvalidOperationException("Staging must not initialize plugin code.");
    public void Dispose() { }
}
