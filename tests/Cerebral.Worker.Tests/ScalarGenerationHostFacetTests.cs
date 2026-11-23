using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class ScalarGenerationHostFacetTests
{
    public static async Task<int> RunAsync(string privateParent)
    {
        if (!Path.IsPathFullyQualified(privateParent))
            throw new ArgumentException("An absolute private fixture parent is required.");
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Scalar generation facet: " + label);
            checks++;
        }
        var installation = "fixture-installation";
        var process = Guid.NewGuid().ToString("N");
        var lane = Guid.NewGuid().ToString("N");
        var secret = "secret-chat-request-run-effect-agent-worker";
        GenerationTelemetrySample Sample() => new("cerebral.generation-metrics/1", lane,
            "model-instance", "model-a", "adapter-a",
            new(secret, secret, secret, secret, secret, secret), "active", "output",
            14, 3, 2.5, 4.5, 250, DateTimeOffset.UtcNow);

        var events = new EventHub();
        using (var reader = events.SubscribeScalarGeneration())
        {
            events.PublishScalarGeneration(installation, process, Sample(), ScalarGenerationTargetOriginV1.Local);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var values = reader.ReadAsync(timeout.Token).GetAsyncEnumerator();
            Check(await values.MoveNextAsync(), "numeric sample delivered");
            var row = values.Current;
            Check(row.Sequence == 1 && row.ProducerInstallationId == installation &&
                row.ProducerProcessGenerationId == process && row.LaneId == lane &&
                row.ModelId == "model-a" && row.ModelInstanceId == "model-instance" &&
                row.TargetOrigin == ScalarGenerationTargetOriginV1.Local,
                "producer, lane, and model identities remain separate");
            Check(row.ProviderObservedInputTokens == 14 && row.ProviderObservedOutputTokens == 3 &&
                row.ProviderInputTokensPerSecond == 2.5 && row.ProviderOutputTokensPerSecond == 4.5 &&
                row.ElapsedMilliseconds == 250, "numeric provider sample retained without confirmed-usage claim");
            var json = JsonSerializer.Serialize(row);
            Check(!json.Contains(secret, StringComparison.Ordinal) &&
                !json.Contains("Binding", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("Conversation", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("Request", StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("Effect", StringComparison.OrdinalIgnoreCase),
                "entire correlation binding excluded");
            events.PublishScalarGeneration(installation, process, Sample() with { OutputTokensPerSecond = double.NaN });
            events.PublishScalarGeneration(installation, process, Sample() with { InputTokens = -1 });
            events.PublishScalarGeneration(installation, "wrong-generation", Sample());
            events.PublishScalarGeneration(installation, process, Sample() with { Schema = "future-schema" });
            events.PublishScalarGeneration(installation, process, Sample(), (ScalarGenerationTargetOriginV1)99);
            Check(reader.DroppedRecords == 5, "invalid rates, counts, producer generation, schema, and origin recorded as gaps");
            events.PublishScalarGeneration(installation, process, Sample() with { State = "Stop" }, ScalarGenerationTargetOriginV1.Local);
            Check(await values.MoveNextAsync() && values.Current.Sequence > row.Sequence &&
                values.Current.State == "Stop", "terminal sample has a later feed revision");
            events.PublishScalarGeneration(installation, process, Sample(), ScalarGenerationTargetOriginV1.Remote);
            Check(await values.MoveNextAsync() && values.Current.TargetOrigin == ScalarGenerationTargetOriginV1.Remote,
                "qualified remote target stays distinct from local producer");
            events.PublishScalarGeneration(installation, process, Sample());
            Check(await values.MoveNextAsync() && values.Current.TargetOrigin == ScalarGenerationTargetOriginV1.Unknown,
                "unqualified target remains unknown");
        }
        Check(events.ScalarGenerationSubscriberCount == 0, "reader detaches on disposal");

        using (var full = events.SubscribeScalarGeneration())
        {
            for (var i = 0; i <= EventHub.ScalarGenerationQueueCapacity; i++)
                events.PublishScalarGeneration(installation, process, Sample());
            Check(full.DroppedRecords == 1, "full queue drops without waiting or replacing old samples");
        }
        Check(events.ScalarGenerationSubscriberCount == 0, "full reader detaches");

        Check(WorkerWorkflowEffects.QualifiedGenerationTargetOrigin("model-a", "local", null,
            "local", "model-a") == ScalarGenerationTargetOriginV1.Local,
            "accepted and prepared local route qualifies local target");
        Check(WorkerWorkflowEffects.QualifiedGenerationTargetOrigin("model-a", "worker-b", "install-b",
            "worker-b", "model-a") == ScalarGenerationTargetOriginV1.Remote,
            "accepted remote installation and prepared model qualify remote target");
        Check(WorkerWorkflowEffects.QualifiedGenerationTargetOrigin("model-a", "worker-b", null,
            "worker-b", "model-a") == ScalarGenerationTargetOriginV1.Unknown &&
            WorkerWorkflowEffects.QualifiedGenerationTargetOrigin("model-a", "worker-b", "install-b",
            "worker-c", "model-a") == ScalarGenerationTargetOriginV1.Unknown &&
            WorkerWorkflowEffects.QualifiedGenerationTargetOrigin("model-a", "local", null,
            "local", "model-c") == ScalarGenerationTargetOriginV1.Unknown,
            "missing remote installation or mismatched route/model cannot be guessed");
        var selectedBinding = new ModelRuntimeSupportBinding(installation, "backend-a", "adapter-a", "1",
            "selected-model", "selected.scope/1", new string('a', 64), "selected-instance");
        var selectedPolicy = new ModelExecutionPolicy(1, "selected-policy", 1, selectedBinding,
            "selected-runtime", "text", false, 1, 4096, 1, 1024, "selected-software");
        var selectedPrepared = new PreparedModelMetadata("selected-instance", "adapter-a", "1",
            "selected-model", 12, 4096, 128, "fixture-count");
        Check(WorkerWorkflowEffects.QualifiedSelectedLocalGenerationTargetOrigin(selectedPolicy,
            selectedPrepared, installation) == ScalarGenerationTargetOriginV1.Local,
            "selected local policy joins its own prepared target independent of default model");
        Check(WorkerWorkflowEffects.QualifiedSelectedLocalGenerationTargetOrigin(selectedPolicy with
            { Binding = selectedBinding with { WorkerInstallationId = "other-installation" } },
            selectedPrepared, installation) == ScalarGenerationTargetOriginV1.Unknown &&
            WorkerWorkflowEffects.QualifiedSelectedLocalGenerationTargetOrigin(selectedPolicy,
                selectedPrepared with { InstanceId = "other-instance" }, installation) == ScalarGenerationTargetOriginV1.Unknown &&
            WorkerWorkflowEffects.QualifiedSelectedLocalGenerationTargetOrigin(selectedPolicy,
                selectedPrepared with { ModelId = "default-model" }, installation) == ScalarGenerationTargetOriginV1.Unknown,
            "selected local origin refuses foreign installation, instance, or prepared model");

        var hostType = typeof(ServicePluginRuntime).GetNestedType("Host", BindingFlags.NonPublic)!;
        var constructor = hostType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var host = (IServiceScalarGenerationHostV1)constructor.Invoke(
            ["fixture.package", "fixture-package", "fixture-data",
                Array.Empty<ServicePluginDependencyBinding>(), events, null, null]);
        var first = host.OpenScalarGenerationObservations();
        var second = host.OpenScalarGenerationObservations();
        Check(events.ScalarGenerationSubscriberCount == 2, "per-package host opens bounded readers");
        var refusedThird = false;
        try { using var excess = host.OpenScalarGenerationObservations(); }
        catch (InvalidOperationException) { refusedThird = true; }
        Check(refusedThird, "per-package reader limit enforced");
        first.Dispose();
        using var replacement = host.OpenScalarGenerationObservations();
        Check(events.ScalarGenerationSubscriberCount == 2, "disposed reader slot can be reused");
        var publishing = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) events.PublishScalarGeneration(installation, process, Sample());
        });
        ((IDisposable)host).Dispose();
        await publishing;
        Check(events.ScalarGenerationSubscriberCount == 0, "host retirement detaches all readers during publication");
        var refusedAfterRetirement = false;
        try { using var after = host.OpenScalarGenerationObservations(); }
        catch (ObjectDisposedException) { refusedAfterRetirement = true; }
        Check(refusedAfterRetirement, "retired host refuses new readers");
        second.Dispose();

        // The loaded package deliberately forgets its lease. The real host must
        // detach it during runtime disposal before the collectible load context retires.
        var packageRoot = Path.Combine(Path.GetFullPath(privateParent), "scalar-fixture-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(packageRoot, "packages", "fixture.scalar-metrics");
        Directory.CreateDirectory(package);
        var fixtureAssembly = typeof(ScalarGenerationLeakyFixture).Assembly.Location;
        File.Copy(fixtureAssembly, Path.Combine(package, Path.GetFileName(fixtureAssembly)));
        File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName),
            JsonSerializer.Serialize(new ServicePluginManifest
            {
                Id = "fixture.scalar-metrics", Name = "Scalar lease fixture", Version = "1.0.0",
                Assembly = Path.GetFileName(fixtureAssembly), EntryType = typeof(ScalarGenerationLeakyFixture).FullName!
            }, CerebralJson.Options));
        using (var loaded = new ServicePluginRuntime(Path.GetDirectoryName(package)!,
            Path.Combine(packageRoot, "data"), events))
        {
            Check(loaded.Status.Count == 1 && loaded.Status[0].Loaded &&
                events.ScalarGenerationSubscriberCount == 1,
                "actual collectible service package opens scalar lease");
            var racing = Task.Run(() =>
            {
                for (var i = 0; i < 300; i++) events.PublishScalarGeneration(installation, process, Sample());
            });
            loaded.Dispose();
            await racing;
        }
        Check(events.ScalarGenerationSubscriberCount == 0,
            "actual package unload retires forgotten lease during publication");

        var dispatcher = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        var authorize = typeof(CommandDispatcher).GetMethod("Authorize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var admin = new AgentProfile { Id = "admin", Capabilities = ["administration"] };
        var denied = false;
        try { authorize.Invoke(dispatcher, ["metrics-history.status", admin, CerebralJson.Element(new { })]); }
        catch (TargetInvocationException error) when (error.InnerException is UnauthorizedAccessException) { denied = true; }
        Check(denied, "administration agent cannot read metrics history");
        denied = false;
        try { authorize.Invoke(dispatcher, ["metrics-history.settings.set", admin, CerebralJson.Element(new { })]); }
        catch (TargetInvocationException error) when (error.InnerException is UnauthorizedAccessException) { denied = true; }
        Check(denied, "administration agent cannot change metrics capture");
        authorize.Invoke(dispatcher, ["metrics-history.status", null, CerebralJson.Element(new { })]);
        Check(true, "owner authorization reaches existing plugin command route");
        return checks;
    }
}

public sealed class ScalarGenerationLeakyFixture : IServicePlugin
{
    private IScalarGenerationSubscriptionV1? lease;
    public void Initialize(IServicePluginHost host) =>
        lease = ((IServiceScalarGenerationHostV1)host).OpenScalarGenerationObservations();
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Dispose() { /* Deliberately rely on the host's package retirement boundary. */ }
}
