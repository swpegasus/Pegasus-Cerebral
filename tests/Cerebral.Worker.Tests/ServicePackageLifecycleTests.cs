using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ServicePackageLifecycleTests
{
    public static async Task RunAsync()
    {
        await ServicePackageStageIdentityTests.RunAsync();
        var root = Path.Combine(Path.GetTempPath(), "cerebral-service-package-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = Path.Combine(root, "repository");
            var data = Path.Combine(root, "data");
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            var manifest = ReviewManifest();
            var fixture = PrepareUnloadableReviewFixture(root);

            var selected = StageAndSelect(repository, data, store, manifest, fixture);
            Check(selected.Revision == 2 && selected.DesiredActive && selected.DesiredVersion == "1.0.0" &&
                  selected.DesiredGeneration is not null && selected.RestartRequired,
                "an accepted package identity selects an immutable desired generation by exact revision");
            Check(!Directory.Exists(Path.Combine(data, "example.review")),
                "staging and desired-state selection do not initialize candidate code");

            var loaded = ExerciseLoadedGeneration(repository, data, store, fixture, selected);
            WaitForUnload(loaded.Contexts, loaded.Runtime);
            Check(loaded.Selected.Revision == 4 && loaded.Selected.DesiredGeneration != selected.DesiredGeneration &&
                  loaded.Selected.RestartRequired,
                "an update cannot replace the observed generation in-process and blocks fresh work until restart");

            var deactivated = ObserveAndDeactivate(repository, data, store, loaded.Selected);
            WaitForUnload(deactivated.Contexts, deactivated.Runtime);
            Check(!deactivated.Status.DesiredActive && deactivated.Status.RestartRequired,
                "deactivation changes desired state while retaining truthful observed state until restart");

            RemoveObservedInactive(repository, data, store, deactivated.Status);
            Check(Directory.Exists(Path.Combine(data, "example.review")),
                "package removal preserves package-owned data");
            Check(ServicePackageLifecycle.OperationDescriptors.Count == 6 &&
                  ServicePackageLifecycle.OperationDescriptors.All(operation => operation.OwnerOnly),
                "all service package lifecycle transport operations are owner-only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("Service package lifecycle checks passed (actual Review package code-free admission plus deterministic executable fixture; no model or installer).");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ServicePackageLifecycleStatus StageAndSelect(string repository, string data,
        WorkflowStore store, string reviewManifest, string fixtureManifest)
    {
        using var runtime = new ServicePluginRuntime(repository, data, new EventHub());
        var lifecycle = new ServicePackageLifecycle(repository, runtime, new EventHub());
        lifecycle.Attach(store);
        var initial = lifecycle.Inspect("example.review");
        Check(initial is { Revision: 0, State: "inactive", ObservedLoaded: false },
            "an absent package has revision zero and no observed code");
        var staged = lifecycle.Stage(reviewManifest, initial.Revision);
        Check(staged is { Revision: 1, State: "staged", DesiredActive: false, ObservedLoaded: false } &&
              staged.StagedVersion == "1.0.0" && staged.StagedGeneration is not null,
            "stage validates and copies the real Review package without selecting it");
        Throws(() => lifecycle.Stage(reviewManifest, initial.Revision), "changed");
        var removedReview = lifecycle.Remove("example.review", staged.Revision);
        Check(removedReview.State == "absent", "the code-free Review candidate can be removed before activation");
        var fixtureStaged = lifecycle.Stage(fixtureManifest, lifecycle.Inspect("example.review").Revision);
        return lifecycle.Activate("example.review", fixtureStaged.Revision);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ServicePackageLifecycleStatus Selected, List<WeakReference> Contexts, WeakReference Runtime) ExerciseLoadedGeneration(
        string repository, string data, WorkflowStore store, string manifest, ServicePackageLifecycleStatus expected)
    {
        var runtime = new ServicePluginRuntime(repository, data, new EventHub());
        var runtimeReference = new WeakReference(runtime);
        try
        {
            var binding = runtime.PackageBindings.Single(item => item.Id == "example.review");
            Check(binding is { Loaded: true, Version: "1.0.0" } && binding.Generation == expected.DesiredGeneration,
                "a new Worker composition loads the exact selected deterministic Review-identity generation");
            var lifecycle = new ServicePackageLifecycle(repository, runtime, new EventHub());
            lifecycle.Attach(store);
            var observed = lifecycle.Inspect("example.review");
            Check(!observed.RestartRequired && observed.ObservedGeneration == observed.DesiredGeneration,
                "desired and observed generation converge after normal recomposition");

            var staged = lifecycle.Stage(manifest, observed.Revision);
            var pinned = store.Submit(Submission("pinned-run", "pinned-chat", runtime.PackageBindings));
            Check(!pinned.IsTerminal, "fixture created an active durable run with an exact package generation snapshot");
            Throws(() => lifecycle.Activate("example.review", staged.Revision), "active durable workflow");
            Check(lifecycle.Inspect("example.review").DesiredGeneration == expected.DesiredGeneration,
                "a selected generation remains unchanged while a durable run pins it");
            store.Cancel(pinned.Id);

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var mutationStarted = new ManualResetEventSlim();
            var admission = Task.Run(() => lifecycle.Admit("example.review:review", "race-run", () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Race fixture did not release durable admission.");
                return store.Submit(Submission("race-run", "race-chat", runtime.PackageBindings));
            }));
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Race fixture did not enter durable admission.");
            var mutation = Task.Run(() =>
            {
                mutationStarted.Set();
                try { lifecycle.Activate("example.review", staged.Revision); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            if (!mutationStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Race fixture did not start mutation.");
            Thread.Sleep(100);
            Check(!mutation.IsCompleted,
                "lifecycle mutation waits while fresh admission holds the shared gate through durable creation");
            release.Set();
            var admitted = admission.GetAwaiter().GetResult();
            var mutationError = mutation.GetAwaiter().GetResult();
            Check(!admitted.IsTerminal && mutationError?.Message.Contains("active durable workflow", StringComparison.Ordinal) == true,
                "admission winning the race commits its pin before mutation can recheck active work");
            store.Cancel(admitted.Id);

            var selected = lifecycle.Activate("example.review", staged.Revision);
            Check(lifecycle.AdmissionProblem("example.review:review")?.Contains("not yet observed", StringComparison.Ordinal) == true,
                "fresh admissions stop while selected and executing generations differ");
            var recovered = lifecycle.Admit("example.review:review", "race-run", () =>
                store.Submit(Submission("race-run", "race-chat", runtime.PackageBindings)));
            Check(recovered.Status == WorkflowStatus.Cancelled,
                "an exact lost-response retry remains recoverable while fresh admissions are gated");
            return (selected, PackageContexts(repository), runtimeReference);
        }
        finally { runtime.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ServicePackageLifecycleStatus Status, List<WeakReference> Contexts, WeakReference Runtime) ObserveAndDeactivate(
        string repository, string data, WorkflowStore store, ServicePackageLifecycleStatus expected)
    {
        var runtime = new ServicePluginRuntime(repository, data, new EventHub());
        var runtimeReference = new WeakReference(runtime);
        try
        {
            var lifecycle = new ServicePackageLifecycle(repository, runtime, new EventHub());
            lifecycle.Attach(store);
            var observed = lifecycle.Inspect("example.review");
            Check(observed.DesiredGeneration == expected.DesiredGeneration && observed.ObservedGeneration == expected.DesiredGeneration &&
                  !observed.RestartRequired && lifecycle.AdmissionProblem("example.review:review") is null,
                "the updated generation becomes admissible only after it is actually observed");
            var status = lifecycle.Deactivate("example.review", observed.Revision);
            Check(lifecycle.AdmissionProblem("example.review:review")?.Contains("deactivated", StringComparison.Ordinal) == true,
                "deactivation immediately rejects fresh work from that contribution graph");
            return (status, PackageContexts(repository), runtimeReference);
        }
        finally { runtime.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveObservedInactive(string repository, string data, WorkflowStore store,
        ServicePackageLifecycleStatus expected)
    {
        using var runtime = new ServicePluginRuntime(repository, data, new EventHub());
        Check(runtime.PackageBindings.All(item => item.Id != "example.review"),
            "a new Worker composition does not load a desired-inactive package");
        var lifecycle = new ServicePackageLifecycle(repository, runtime, new EventHub());
        lifecycle.Attach(store);
        var inactive = lifecycle.Inspect("example.review");
        Check(inactive.Revision == expected.Revision && !inactive.ObservedLoaded && !inactive.RestartRequired,
            "desired inactive and observed unloaded converge before removal");
        var removed = lifecycle.Remove("example.review", inactive.Revision);
        Check(removed.State == "absent" && !Directory.Exists(Path.Combine(repository, ".packages", "example.review")) &&
              !File.Exists(Path.Combine(repository, ".lifecycle", "example.review.json")),
            "remove deletes managed package generations and lifecycle state after unload");
    }

    private static WorkflowSubmission Submission(string id, string conversationId,
        IReadOnlyList<ServicePluginPackageBinding> bindings) =>
        new(id, conversationId, "owner", "example.review:review", "1.0.0", "Review this passage.", null,
            WorkflowContract.EmptyObject, CerebralJson.Element(new { packageBindings = bindings }));

    private static List<WeakReference> PackageContexts(string repository) => AssemblyLoadContext.All
        .Where(context => context.IsCollectible && context.Assemblies.Any(assembly =>
            !string.IsNullOrEmpty(assembly.Location) && assembly.Location.StartsWith(repository + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        .Select(context => new WeakReference(context)).ToList();

    private static void WaitForUnload(IReadOnlyList<WeakReference> contexts, WeakReference runtime)
    {
        for (var attempt = 0; attempt < 20 && contexts.Any(context => context.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (contexts.Any(context => context.IsAlive)) Thread.Sleep(25);
        }
        Check(!runtime.IsAlive && contexts.All(context => !context.IsAlive),
            "the previous service package generation unloaded before lifecycle cleanup (runtimeAlive=" + runtime.IsAlive + ")");
    }

    private static string ReviewManifest()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "examples", "Cerebral.ReviewPlugin", "bin", "Release", "net10.0",
                    ServicePluginRuntime.ManifestFileName);
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("Build the actual Review service package in Release before running lifecycle acceptance.");
    }

    private static string PrepareUnloadableReviewFixture(string root)
    {
        var package = Path.Combine(root, "fixture-source", "example.review");
        Directory.CreateDirectory(package);
        var assembly = Path.GetFileName(typeof(DependencyFixturePlugin).Assembly.Location);
        File.Copy(typeof(DependencyFixturePlugin).Assembly.Location, Path.Combine(package, assembly));
        var manifest = new ServicePluginManifest
        {
            Id = "example.review", Name = "Review lifecycle fixture", Version = "1.0.0", Assembly = assembly,
            EntryType = typeof(DependencyFixturePlugin).FullName!
        };
        var path = Path.Combine(package, ServicePluginRuntime.ManifestFileName);
        var document = System.Text.Json.JsonSerializer.SerializeToNode(manifest, CerebralJson.Options)!.AsObject();
        document.Remove("portableSettings");
        File.WriteAllText(path, document.ToJsonString(CerebralJson.Options));
        return path;
    }

    private static void Throws(Action action, string expected)
    {
        try { action(); }
        catch (Exception error) when (error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("FAIL: expected rejection containing '" + expected + "'.");
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
