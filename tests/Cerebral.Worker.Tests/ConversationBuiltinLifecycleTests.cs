using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static class ConversationBuiltinLifecycleTests
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-conversation-builtin-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            checks++;
        }
        try { RunScope(root, Check); }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var fullRoot = Path.GetFullPath(root);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!fullRoot.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup left its temporary root.");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
        }
        Console.WriteLine($"Conversation built-in lifecycle: {checks} deterministic checks passed (isolated SQLite and native metadata fixture; no model or owner connection).");
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunScope(string root, Action<bool, string> check)
    {
        void Reject(Action action, string expected)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(expected, StringComparison.Ordinal))
            { check(true, "rejected " + expected); return; }
            throw new Exception("FAIL: expected rejection " + expected);
        }

        const string id = ConversationWorkflowModule.ModuleId;
        var repository = Path.Combine(root, "repository");
        var data = Path.Combine(root, "data");
        var candidate = Package(Path.Combine(root, "candidate"), "1.0.2");
        var downgrade = Package(Path.Combine(root, "downgrade"), "0.9.9");
        var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
        var builtIn = ConversationWorkflowModule.PackageBinding;
        using var empty = new ServicePluginRuntime(repository, data, new EventHub());
        var callerBindings = new[] { builtIn };
        var lifecycle = new ServicePackageLifecycle(repository, empty, new EventHub(), hostBuiltInPackages: callerBindings);
        callerBindings[0] = builtIn with { Version = "9.0.0" };
        lifecycle.Attach(store);
        var initial = lifecycle.Inspect(id);
        check(initial is { Revision: 0, DesiredActive: true, ObservedLoaded: true, RestartRequired: false } &&
              initial.DesiredVersion == builtIn.Version && initial.ObservedVersion == builtIn.Version,
            "the selected built-in is observed without a filesystem generation and caller metadata is frozen");
        Reject(() => lifecycle.Stage(downgrade, initial.Revision), "cannot lower");
        check(!Directory.Exists(repository), "a built-in downgrade rejection does not create repository state");
        var pin = store.Submit(Submission("builtin-pin", builtIn));
        var pinBefore = CerebralJson.Element(pin).GetRawText();
        var staged = lifecycle.Stage(candidate, initial.Revision);
        check(staged is { DesiredActive: true, ObservedLoaded: true, RestartRequired: false, State: "staged" } &&
              staged.DesiredVersion == builtIn.Version && staged.ObservedVersion == builtIn.Version && staged.StagedVersion == "1.0.2",
            "first staging retains effective built-in policy and does not request a restart");
        var statePath = Path.Combine(repository, ".lifecycle", id + ".json");
        using (var saved = JsonDocument.Parse(File.ReadAllText(statePath)))
            check(!saved.RootElement.GetProperty("desiredActive").GetBoolean() &&
                  saved.RootElement.GetProperty("desiredVersion").ValueKind == JsonValueKind.Null &&
                  saved.RootElement.GetProperty("desiredGeneration").ValueKind == JsonValueKind.Null,
                "staged-only durable state contains no fake filesystem selection for the built-in");
        check(!Directory.Exists(Path.Combine(data, id)), "staging does not initialize candidate code");
        check(lifecycle.AdmissionProblem(ConversationWorkflowModule.DefinitionId) is null,
            "staged-only state leaves frozen built-in fresh admission open");
        var admitted = lifecycle.Admit(ConversationWorkflowModule.DefinitionId, "builtin-fresh",
            () => store.Submit(Submission("builtin-fresh", builtIn)));
        check(!admitted.IsTerminal && lifecycle.Inspect(id).ActiveRuns == 2,
            "fresh durable admission continues and captures its unchanged built-in pin");
        var withoutFallback = new ServicePackageLifecycle(repository, empty, new EventHub());
        check(withoutFallback.AdmissionProblem(ConversationWorkflowModule.DefinitionId)?.Contains("deactivated", StringComparison.Ordinal) == true,
            "staged-only state cannot invent a fallback when no host selection was supplied");
        Reject(() => lifecycle.Activate(id, staged.Revision), "active durable workflow");
        Reject(() => lifecycle.Remove(id, staged.Revision), "active durable workflow");
        Reject(() => lifecycle.Deactivate(id, staged.Revision), "built-in policy cannot be deactivated");
        Reject(() => lifecycle.Stage(downgrade, staged.Revision), "cannot lower");
        check(CerebralJson.Element(store.Get(pin.Id)).GetRawText() == pinBefore && lifecycle.Inspect(id).Revision == staged.Revision,
            "rejected lifecycle changes preserve the exact active pin and candidate revision");

        using (var restarted = new ServicePluginRuntime(repository, data, new EventHub()))
        {
            var selection = ConversationModuleSelection.Compose(restarted.WorkflowModules, restarted.PackageBindings);
            var again = new ServicePackageLifecycle(repository, restarted, new EventHub(), selection.HostBuiltInPackages);
            again.Attach(store);
            check(restarted.PackageBindings.Count == 0 && selection.HostBuiltInPackages.Count == 1 &&
                  again.Inspect(id) is { DesiredActive: true, ObservedLoaded: true, RestartRequired: false, StagedVersion: "1.0.2" } &&
                  again.AdmissionProblem(ConversationWorkflowModule.DefinitionId) is null,
                "restart resolves staged-only repository to the same built-in without a missing package error");
            store.Cancel(pin.Id); store.Cancel(admitted.Id);
            var stagedDirectory = Path.Combine(repository, ".packages", id, staged.StagedGeneration!);
            var removed = again.Remove(id, staged.Revision);
            check(removed is { DesiredActive: true, ObservedLoaded: true, RestartRequired: false, StagedVersion: null } &&
                  !Directory.Exists(stagedDirectory) && !File.Exists(statePath) && File.Exists(candidate),
                "once pins drain removal discards only the staged candidate and preserves built-in policy and source bytes");
            check(again.AdmissionProblem(ConversationWorkflowModule.DefinitionId) is null,
                "candidate discard keeps built-in admission open");
        }
        using (var afterDiscard = new ServicePluginRuntime(repository, data, new EventHub()))
        {
            var selection = ConversationModuleSelection.Compose(afterDiscard.WorkflowModules, afterDiscard.PackageBindings);
            var again = new ServicePackageLifecycle(repository, afterDiscard, new EventHub(), selection.HostBuiltInPackages);
            check(again.Inspect(id) is { DesiredActive: true, ObservedLoaded: true, RestartRequired: false, StagedVersion: null },
                "restart after discard retains the same built-in policy");
        }

        var nativeRepository = Path.Combine(root, "native-repository");
        var nativeData = Path.Combine(root, "native-data");
        Package(Path.Combine(nativeRepository, id), "1.0.2");
        using (var native = new ServicePluginRuntime(nativeRepository, nativeData, new EventHub()))
        {
            var nativeStore = new WorkflowStore(new EventJournal(Path.Combine(root, "native-engine.db")));
            var actual = new ServicePackageLifecycle(nativeRepository, native, new EventHub(), [builtIn]);
            actual.Attach(nativeStore);
            var observed = actual.Inspect(id);
            check(observed is { DesiredActive: true, ObservedLoaded: true, ObservedVersion: "1.0.2" },
                "a loaded native package overrides even supplied fallback metadata");
            var inactive = actual.Deactivate(id, observed.Revision);
            check(!inactive.DesiredActive && actual.AdmissionProblem(ConversationWorkflowModule.DefinitionId)?.Contains("deactivated", StringComparison.Ordinal) == true,
                "explicit native deactivation still blocks fresh admission");
        }
        using (var nativeRestart = new ServicePluginRuntime(nativeRepository, nativeData, new EventHub()))
        {
            var actual = new ServicePackageLifecycle(nativeRepository, nativeRestart, new EventHub(), [builtIn]);
            check(actual.Inspect(id) is { DesiredActive: false, ObservedLoaded: false, RestartRequired: false } &&
                  actual.AdmissionProblem(ConversationWorkflowModule.DefinitionId)?.Contains("deactivated", StringComparison.Ordinal) == true,
                "fallback metadata cannot reactivate explicitly deactivated native desired state after restart");
        }

        var failedRepository = Path.Combine(root, "failed-repository");
        Package(Path.Combine(failedRepository, id), "1.0.2", "Example.NonexistentPlugin");
        using var failed = new ServicePluginRuntime(failedRepository, Path.Combine(root, "failed-data"), new EventHub());
        var failure = new ServicePackageLifecycle(failedRepository, failed, new EventHub(), [builtIn]);
        check(failure.Inspect(id) is { DesiredActive: false, ObservedLoaded: false, ObservedVersion: "1.0.2" } &&
              failed.PackageBindings.Single().Loaded == false &&
              ConversationModuleSelection.Compose(failed.WorkflowModules, failed.PackageBindings).HostBuiltInPackages.Count == 0,
            "failed selected native metadata cannot be masked by supplied built-in fallback");
    }

    private static WorkflowSubmission Submission(string id, ServicePluginPackageBinding binding) =>
        new(id, id + "-conversation", "fixture-owner", ConversationWorkflowModule.DefinitionId,
            ConversationWorkflowModule.DefinitionVersion, "Fixture request.", null, WorkflowContract.EmptyObject,
            CerebralJson.Element(new { packageBindings = new[] { binding } }));

    private static string Package(string directory, string version, string? entryType = null)
    {
        Directory.CreateDirectory(directory);
        var assembly = Path.GetFileName(typeof(ConversationBuiltinLifecycleFixturePlugin).Assembly.Location);
        File.Copy(typeof(ConversationBuiltinLifecycleFixturePlugin).Assembly.Location, Path.Combine(directory, assembly));
        var document = JsonSerializer.SerializeToNode(new ServicePluginManifest
        {
            Id = ConversationWorkflowModule.ModuleId, Name = "Conversation lifecycle fixture", Version = version,
            Assembly = assembly, EntryType = entryType ?? typeof(ConversationBuiltinLifecycleFixturePlugin).FullName!
        }, CerebralJson.Options)!.AsObject();
        document.Remove("portableSettings");
        var manifest = Path.Combine(directory, ServicePluginRuntime.ManifestFileName);
        File.WriteAllText(manifest, document.ToJsonString(CerebralJson.Options));
        return manifest;
    }
}

public sealed class ConversationBuiltinLifecycleFixturePlugin : IServicePlugin
{
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) => File.WriteAllText(Path.Combine(host.DataDirectory, "initialized.txt"), "Fixture initialized.");
    public void Dispose() { }
}
