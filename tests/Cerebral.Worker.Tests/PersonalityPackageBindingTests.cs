using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Package graph and lifecycle fixtures only; no Personality provider or model is dispatched.</summary>
public static class PersonalityPackageBindingTests
{
    private const string Persona = "pegasus.personality";
    private const string Provider = Persona + ":context.resolve";
    private const string Basis = "fixture.personality-base";
    private const string Optional = "fixture.personality-optional";
    private const string Conversation = "fixture.conversation:chat";
    private static readonly ServicePluginPackageBinding ConversationPackage = new("fixture.conversation", "1.0.0", true, []);
    private static readonly ServicePluginPackageBinding HostPackage = new("cerebral.host", "1.0.0", true, []);

    public static int Run(string? fixtureParent = null)
    {
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            count++;
        }
        void Refuses(Action action, string fragment, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException)
            {
                Check(error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), message);
                return;
            }
            throw new Exception("FAIL: " + message);
        }

        CheckCapturedGraphs(Check, Refuses);
        if (fixtureParent is not null && !Path.IsPathFullyQualified(fixtureParent))
            throw new ArgumentException("The private fixture parent must be absolute.");
        var temporary = Path.GetFullPath(fixtureParent ?? Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(temporary, "cerebral-personality-package-bindings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            CheckLifecycle(Path.Combine(root, "lifecycle"), Check, Refuses);
            CheckDependencyDeactivation(Path.Combine(root, "dependency"), Check, Refuses);
            using var missing = new Fixture(Path.Combine(root, "missing"), includePersona: false);
            Check(missing.Lifecycle.AdmissionProblem(Conversation) is null,
                "legacy admission does not opt unrelated optional packages into the graph");
            Check(missing.Lifecycle.AdmissionProblem(Conversation, [Provider])?.Contains("unavailable", StringComparison.Ordinal) == true,
                "an explicitly ready additional package must actually be loaded");
            var calls = 0;
            Refuses(() => missing.Lifecycle.Admit(Conversation, "missing-ready", [Provider], () => ++calls),
                "unavailable", "missing ready package refuses fresh admission");
            Check(calls == 0 && missing.Store.TryGet("missing-ready") is null,
                "missing ready package refuses before the durable callback");
        }
        catch (Exception error)
        {
            // Preserve the actual assertion/admission failure even if Windows still holds a
            // collectible copied fixture DLL during cleanup.
            Console.WriteLine("Personality package binding functional failure: " + error.GetType().Name);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            // This deletion is confined to the exact random fixture directory created above.
            if (Path.GetFullPath(root) != root || !root.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Fixture cleanup path escaped its temporary parent.");
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("Personality package binding cleanup deferred until process exit: " +
                    root + " (" + error.GetType().Name + "). Parent must clean this exact private fixture path.");
            }
        }
        Console.WriteLine($"Personality package bindings: {count} CPU/SQLite/managed-fixture checks passed; no provider or model dispatch.");
        return count;
    }

    private static void CheckCapturedGraphs(Action<bool, string> check, Action<Action, string, string> refuses)
    {
        var optional = new ServicePluginDependencyBinding(Optional, "1.0.0", null, null, true, null, false, "Not installed.");
        var basis = new ServicePluginPackageBinding(Basis, "1.0.0", true, [], Generation: "1.0.0-basis-a");
        var persona = new ServicePluginPackageBinding(Persona, "1.0.2", true,
            [new(Basis, null, "1.0.0", "2.0.0", false, basis.Version, true), optional], Generation: "1.0.2-persona-a");
        WorkflowPackageBindings Graph(ServicePluginPackageBinding p, ServicePluginPackageBinding b,
            params ServicePluginPackageBinding[] other) => new([ConversationPackage, HostPackage, p, b, .. other]);
        var graph = Graph(persona, basis);
        var ordinary = graph.Capture(Conversation);
        check(ordinary.Select(item => item.Id).SequenceEqual(["cerebral.host", "fixture.conversation"]),
            "old capture keeps only definition/host/dependency roots");
        check(graph.Capture(Conversation, []).SequenceEqual(ordinary), "empty extras preserve the old capture shape");
        var accepted = graph.Capture(Conversation, [Provider, Persona, Provider]);
        check(accepted.Select(item => item.Id).SequenceEqual(
                accepted.Select(item => item.Id).Order(StringComparer.Ordinal)) && accepted.Select(item => item.Id).Distinct().Count() == 4,
            "explicit package/contribution roots are deduplicated and deterministically ordered");
        var pinned = accepted.Single(item => item.Id == Persona);
        check(pinned.Version == "1.0.2" && pinned.Generation == "1.0.2-persona-a" && pinned.Loaded,
            "ready Persona captures the exact loaded package version and generation");
        check(accepted.Any(item => item.Id == Basis && item.Generation == basis.Generation) &&
              !accepted.Any(item => item.Id == Optional) && pinned.Dependencies.Single(item => item.Id == Optional) == optional,
            "available dependency closure is captured while unavailable optional metadata stays pinned");
        var submission = Submission("captured", accepted);
        check(graph.Check(submission, Provider) is null, "the accepted provider is admitted through the captured graph");
        check(graph.Check(Submission("ordinary", ordinary), Provider) is not null,
            "an uncaptured optional provider cannot become an accepted command");
        check(Graph(persona with { Version = "1.0.3" }, basis).Check(submission, Provider) is not null,
            "a changed optional package version refuses continuation");
        check(Graph(persona with { Generation = "1.0.2-persona-b" }, basis).Check(submission, Provider) is not null,
            "a changed optional package generation refuses continuation");
        check(Graph(persona, basis with { Generation = "1.0.0-basis-b" }).Check(submission, Provider) is not null,
            "a changed available dependency generation refuses continuation");
        var upgraded = basis with { Version = "1.1.0" };
        check(Graph(persona with { Dependencies = [persona.Dependencies[0] with { ResolvedVersion = upgraded.Version }, optional] },
                upgraded).Check(submission, Provider) is not null,
            "a compatible dependency upgrade still differs from the accepted immutable graph");
        check(new WorkflowPackageBindings([ConversationPackage, HostPackage, basis]).Check(submission, Provider) is not null,
            "removing the ready optional package refuses continuation");
        check(Graph(persona with { Loaded = false }, basis).Check(submission, Provider) is not null,
            "an accepted optional package becoming unloaded refuses continuation");
        var nowAvailable = optional with { Available = true, ResolvedVersion = "1.0.0", Reason = null };
        check(Graph(persona with { Dependencies = [persona.Dependencies[0], nowAvailable] }, basis,
                new ServicePluginPackageBinding(Optional, "1.0.0", true, [])).Check(submission, Provider) is not null,
            "an unavailable optional dependency becoming available changes the saved graph");
        check(Graph(persona with { Dependencies = [persona.Dependencies[0], optional with { ExactVersion = "1.1.0" }] },
                basis).Check(submission, Provider) is not null,
            "an unavailable optional dependency constraint remains part of the immutable binding");
        check(Graph(persona with { Dependencies = [persona.Dependencies[0], optional with { Reason = "Offline now." }] },
                basis).Check(submission, Provider) is null,
            "optional diagnostic wording does not change package semantics");
        refuses(() => new WorkflowPackageBindings([ConversationPackage, HostPackage, basis]).Capture(Conversation, [Provider]),
            "unavailable", "a missing explicit optional root is refused rather than omitted");
        refuses(() => Graph(persona with { Loaded = false }, basis).Capture(Conversation, [Provider]),
            "unavailable", "an unloaded explicit optional root is refused rather than omitted");
        refuses(() => new WorkflowPackageBindings([ConversationPackage, HostPackage, persona]).Capture(Conversation, [Provider]),
            "mismatched", "an available dependency without its loaded package binding is refused");
        refuses(() => Graph(persona, basis with { Loaded = false }).Capture(Conversation, [Provider]),
            "unavailable", "an available dependency that is not loaded is refused");
        refuses(() => Graph(persona with { Dependencies = [persona.Dependencies[0] with { Available = false }, optional] },
                basis).Capture(Conversation, [Provider]),
            "required", "a declared unavailable required dependency cannot be a ready graph");
        refuses(() => Graph(persona with { Dependencies = [persona.Dependencies[0] with { ResolvedVersion = "1.1.0" }, optional] },
                basis).Capture(Conversation, [Provider]),
            "mismatched", "resolved dependency version must match the exact captured target binding");
        refuses(() => graph.Capture(Conversation, [""]), "bounded", "empty additional identities are refused");
        refuses(() => graph.Capture(Conversation, ["pegasus.personality:context.resolve:other"]), "bounded",
            "ambiguous additional contribution identities are refused");
        refuses(() => graph.Capture(Conversation, [Provider + "\0"]), "bounded", "control characters in additional identities are refused");
        refuses(() => graph.Capture(Conversation, Enumerable.Repeat(Provider, 17).ToArray()), "16",
            "the additional root input is bounded before graph capture");
        var dependencies = new List<ServicePluginDependencyBinding> { persona.Dependencies[0], optional };
        var frozen = Graph(persona with { Dependencies = dependencies }, basis);
        dependencies.Clear();
        check(frozen.Capture(Conversation, [Provider]).Single(item => item.Id == Persona).Dependencies.Count == 2,
            "capture owns its immutable dependency metadata rather than a mutable caller list");
    }

    private static void CheckLifecycle(string root, Action<bool, string> check, Action<Action, string, string> refuses)
    {
        using var fixture = new Fixture(root, includePersona: true);
        var initial = fixture.Lifecycle.Inspect(Persona);
        check(initial.ObservedLoaded && initial.ObservedVersion == "1.0.2" && initial.ObservedGeneration == "legacy",
            "managed fixture runtime actually loads the Persona-identity 1.0.2 package");
        check(fixture.Lifecycle.AdmissionProblem(Conversation, [Provider]) is null,
            "loaded explicit optional graph can enter the lifecycle gate");
        var staged = fixture.Lifecycle.Stage(fixture.Candidate, initial.Revision, Persona);
        check(staged.StagedGeneration is not null && staged.DesiredGeneration == initial.ObservedGeneration,
            "staging alone retains the currently observed generation");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var mutationStarted = new ManualResetEventSlim();
        var admission = Task.Run(() => fixture.Lifecycle.Admit(Conversation, "race-ready", [Provider], () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture durable callback was not released.");
            return fixture.Store.Submit(Submission("race-ready", fixture.Bindings.Capture(Conversation, [Provider])));
        }));
        Task<Exception?>? mutation = null;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture did not enter durable admission.");
            mutation = Task.Run(() =>
            {
                mutationStarted.Set();
                try { fixture.Lifecycle.Activate(Persona, staged.Revision); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            if (!mutationStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture did not begin lifecycle mutation.");
            check(!mutation.Wait(TimeSpan.FromMilliseconds(100)),
                "desired-generation mutation waits for the additional-root durable callback under the same gate");
        }
        finally { release.Set(); }
        var admitted = admission.GetAwaiter().GetResult();
        var mutationError = mutation!.GetAwaiter().GetResult();
        check(!admitted.IsTerminal && mutationError is InvalidOperationException &&
              mutationError.Message.Contains("active durable workflow", StringComparison.Ordinal),
            "admission winning the race persists the Persona pin before activation rechecks active runs");
        var accepted = admitted.Submission.SettingsSnapshot.GetRawText();
        check(admitted.Submission.SettingsSnapshot.GetProperty("packageBindings").EnumerateArray().Any(item =>
                item.GetProperty("id").GetString() == Persona && item.GetProperty("version").GetString() == "1.0.2"),
            "the actual SQLite accepted submission retains the exact additional package binding");
        check(new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db"))).TryGet(admitted.Id)!
                .Submission.SettingsSnapshot.GetRawText() == accepted,
            "the accepted optional package graph survives a fresh SQLite store read");
        refuses(() => fixture.Lifecycle.Deactivate(Persona, staged.Revision), "active durable workflow",
            "active additional package pins also block deactivation");

        fixture.Store.Cancel(admitted.Id);
        var selected = fixture.Lifecycle.Activate(Persona, staged.Revision);
        check(selected.DesiredGeneration != initial.ObservedGeneration &&
              fixture.Lifecycle.AdmissionProblem(Conversation, [Provider])?.Contains("not yet observed", StringComparison.Ordinal) == true,
            "a generation change winning before admission refuses fresh additional-root work until restart");
        var calls = 0;
        refuses(() => fixture.Lifecycle.Admit(Conversation, "after-generation", [Provider], () => ++calls), "not yet observed",
            "fresh admission refuses a desired/observed generation mismatch");
        check(calls == 0 && fixture.Store.TryGet("after-generation") is null,
            "generation mismatch prevents durable callback and new persisted work");
        var recovered = fixture.Lifecycle.Admit(Conversation, admitted.Id, [Provider], () =>
            fixture.Store.Submit(Submission(admitted.Id, admitted.Submission.SettingsSnapshot
                .GetProperty("packageBindings").Deserialize<ServicePluginPackageBinding[]>(CerebralJson.Options)!)));
        check(recovered.Status == WorkflowStatus.Cancelled && recovered.Submission.SettingsSnapshot.GetRawText() == accepted,
            "exact duplicate recovery retains the accepted graph while fresh additional admissions are gated");

        var deactivated = fixture.Lifecycle.Deactivate(Persona, selected.Revision);
        check(!deactivated.DesiredActive && fixture.Lifecycle.AdmissionProblem(Conversation, [Provider])?
                .Contains("deactivated", StringComparison.Ordinal) == true,
            "deactivation winning first immediately refuses a ready additional root");
        refuses(() => fixture.Lifecycle.Admit(Conversation, "after-deactivation", [Provider], () => ++calls), "deactivated",
            "deactivated optional root refuses fresh work instead of silently dropping style");
        check(calls == 0 && fixture.Lifecycle.AdmissionProblem(Conversation) is null,
            "extra-root refusal preserves ordinary legacy admission behavior");
        var ordinary = fixture.Lifecycle.Admit(Conversation, "ordinary-after-deactivation", () =>
            fixture.Store.Submit(Submission("ordinary-after-deactivation", fixture.Bindings.Capture(Conversation))));
        check(!ordinary.IsTerminal && !ordinary.Submission.SettingsSnapshot.GetProperty("packageBindings")
                .EnumerateArray().Any(item => item.GetProperty("id").GetString() == Persona),
            "legacy Admit and Capture remain usable without opting in the optional Persona package");
    }

    private static void CheckDependencyDeactivation(string root, Action<bool, string> check,
        Action<Action, string, string> refuses)
    {
        using var fixture = new Fixture(root, includePersona: true);
        var prior = fixture.Lifecycle.Inspect(Basis);
        fixture.Lifecycle.Deactivate(Basis, prior.Revision);
        check(fixture.Lifecycle.AdmissionProblem(Conversation, [Provider])?.Contains(Basis, StringComparison.Ordinal) == true,
            "additional-root lifecycle admission checks desired state of available dependencies");
        var calls = 0;
        refuses(() => fixture.Lifecycle.Admit(Conversation, "dependency-off", [Provider], () => ++calls), "deactivated",
            "a deactivated available dependency refuses ready Persona admission");
        check(calls == 0 && fixture.Store.TryGet("dependency-off") is null,
            "dependency deactivation refuses before a new durable record exists");
    }

    private static WorkflowSubmission Submission(string id, IReadOnlyList<ServicePluginPackageBinding> packages) =>
        new(id, id + "-chat", "fixture-profile", Conversation, "1.0.0", "Private fixture input.", null,
            WorkflowContract.EmptyObject, CerebralJson.Element(new { packageBindings = packages }));

    private sealed class Fixture : IDisposable
    {
        internal ServicePluginRuntime Runtime { get; }
        internal ServicePackageLifecycle Lifecycle { get; }
        internal WorkflowStore Store { get; }
        internal WorkflowPackageBindings Bindings { get; }
        internal string Candidate { get; }

        internal Fixture(string root, bool includePersona)
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            WritePackage(Path.Combine(repository, Basis), Basis, "1.0.0", []);
            if (includePersona) WritePackage(Path.Combine(repository, Persona), Persona, "1.0.2", Dependencies());
            Candidate = WritePackage(Path.Combine(root, "candidate", Persona), Persona, "1.0.2", Dependencies());
            Runtime = new(repository, Path.Combine(root, "data"), new EventHub());
            Store = new(new EventJournal(Path.Combine(root, "engine.db")));
            Lifecycle = new(repository, Runtime, new EventHub(), [ConversationPackage, HostPackage]);
            Lifecycle.Attach(Store);
            Bindings = new([.. Runtime.PackageBindings, ConversationPackage, HostPackage]);
        }
        public void Dispose() => Runtime.Dispose();

        private static List<ServicePluginDependency> Dependencies() =>
            [new() { Id = Basis, ExactVersion = "1.0.0" },
             new() { Id = Optional, ExactVersion = "1.0.0", Optional = true }];
        private static string WritePackage(string directory, string id, string version, List<ServicePluginDependency> dependencies)
        {
            Directory.CreateDirectory(directory);
            var assembly = Path.GetFileName(typeof(PersonalityPackageBindingFixturePlugin).Assembly.Location);
            File.Copy(typeof(PersonalityPackageBindingFixturePlugin).Assembly.Location, Path.Combine(directory, assembly));
            var manifest = new ServicePluginManifest
            {
                Id = id, Name = "Passive package binding fixture", Version = version, Assembly = assembly,
                EntryType = typeof(PersonalityPackageBindingFixturePlugin).FullName!, Dependencies = dependencies
            };
            var path = Path.Combine(directory, ServicePluginRuntime.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
            return path;
        }
    }
}

/// <summary>Only deterministic managed loading is exercised; this fixture exports no provider, tools or commands.</summary>
public sealed class PersonalityPackageBindingFixturePlugin : IServicePlugin
{
    public void Initialize(IServicePluginHost host) { }
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Dispose() { }
}
