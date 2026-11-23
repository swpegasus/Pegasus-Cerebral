using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Loads the built title package through the real Worker service loader and exercises
/// its central settings policy in owned SQLite. No observation is delivered and no
/// workflow, model, profile, or application process is started.
/// </summary>
internal static class TitleConsumerLoadedChecks
{
    private const string PackageId = "pegasus.conversation-titles";
    private const string PackageVersion = "0.2.0";
    private const string LegacyVersion = "0.1.0";
    private const string AssemblyName = "Cerebral.ConversationTitlePlugin.dll";
    private const string SourceProfile = "fixture-source";
    private const string WorkProfile = "fixture-work";

    internal static async Task<int> RunAsync(string exactOwnedParent, string exactBuiltTitlePackageDir)
    {
        var parent = ExistingDirectory(exactOwnedParent);
        var built = ExistingDirectory(exactBuiltTitlePackageDir);
        if (Path.GetPathRoot(parent) == parent ||
            Path.GetPathRoot(built) == built ||
            IsUnder(parent, built) || IsUnder(built, parent))
            throw new ArgumentException("Title fixture paths must be distinct non-root directories.");

        // The caller pins the built artifact. This fixture copies only its two
        // ordinary loader inputs, leaving the owned root for post-child cleanup.
        var manifestPath = Path.Combine(built, ServicePluginRuntime.ManifestFileName);
        var assemblyPath = Path.Combine(built, AssemblyName);
        RequireFile(manifestPath);
        RequireFile(assemblyPath);
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
        {
            var value = manifest.RootElement;
            Check(value.GetProperty("id").GetString() == PackageId &&
                value.GetProperty("version").GetString() == PackageVersion &&
                value.GetProperty("assembly").GetString() == AssemblyName &&
                value.GetProperty("entryType").GetString() ==
                    "Cerebral.ConversationTitlePlugin.ConversationTitlePlugin",
                "built package identity and entry are exact");
        }

        var root = Path.Combine(parent, "title-consumer-loaded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repositoryPackage = Path.Combine(root, "packages", PackageId);
        Directory.CreateDirectory(repositoryPackage);
        File.Copy(manifestPath, Path.Combine(repositoryPackage, ServicePluginRuntime.ManifestFileName));
        File.Copy(assemblyPath, Path.Combine(repositoryPackage, AssemblyName));

        var checks = 1;
        checks += await ExerciseAbsentAsync(root);
        checks += await ExerciseAsync(root, "legacy-one", 1);
        checks += await ExerciseAsync(root, "legacy-two", 2);
        return checks;
    }

    private static async Task<int> ExerciseAbsentAsync(string root)
    {
        var state = new StateStore(Path.Combine(root, "absent", "state"));
        ServicePluginRuntime? runtime = null;
        ExceptionDispatchInfo? primary = null;
        try
        {
            runtime = new ServicePluginRuntime(Path.Combine(root, "packages"),
                Path.Combine(root, "absent", "plugin-data"), new EventHub(),
                (id, version, policy) => new ScopedPluginSettingsAccess(state, id, version, policy));
            var coordinate = new PluginSettingsCoordinate(PackageId, SettingsPreferenceContract.GlobalScope);
            var retained = new SettingsPreferencesStore(state, () => runtime.SettingsPolicies).GetTransfer(coordinate).State;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "title_loaded_initialization_observed",
                packages = runtime.Status.Select(item => new { item.Id, item.Version, item.Loaded,
                    error = item.Error is { Length: > 512 } error ? error[..512] : item.Error }),
                retained.Revision, hasActive = retained.Active is not null, pending = retained.Pending.Count,
                targets = runtime.CommittedRootUserInputGate.EligibleObservers(Source(SourceProfile)).Count
            }));
            Check(runtime.Status is [{ Id: PackageId, Loaded: true }] &&
                retained is { Revision: 0, Active: null, Pending.Count: 0 } &&
                runtime.CommittedRootUserInputGate.EligibleObservers(Source(SourceProfile)).Count == 0,
                "actual version-0 Initialize stays disabled and does not invent settings or a subscription");
        }
        catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            if (runtime is not null)
            {
                try
                {
                    runtime.Dispose();
                    await runtime.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error)
                {
                    if (primary is null) primary = ExceptionDispatchInfo.Capture(error);
                    else primary.SourceException.Data["fixtureCleanupFailureType"] = error.GetType().Name;
                }
            }
        }
        primary?.Throw();
        return 1;
    }

    private static async Task<int> ExerciseAsync(string root, string name, int legacySchema)
    {
        var state = new StateStore(Path.Combine(root, name, "state"));
        var coordinate = new PluginSettingsCoordinate(PackageId, SettingsPreferenceContract.GlobalScope);
        var legacyValues = legacySchema == 1
            ? CerebralJson.Element(new { enabled = true, profileId = WorkProfile, profileRevision = 1L })
            : CerebralJson.Element(new { enabled = true, profileId = WorkProfile, profileRevision = 1L,
                sourceProfileIds = new[] { SourceProfile } });
        var legacy = new PluginSettingsDocument(PackageId, coordinate.Scope, null,
            LegacyVersion, legacySchema, legacyValues);
        var seeded = new SettingsPreferencesStore(state).SaveTransfer(
            new(new(legacy, 0, OwnerValidated: true)));
        Check(seeded.Changed && seeded.State.State.Revision == 1, "legacy input is physically retained");
        var checks = 1;

        ServicePluginRuntime? runtime = null;
        ExceptionDispatchInfo? primary = null;
        try
        {
            runtime = new ServicePluginRuntime(Path.Combine(root, "packages"),
                Path.Combine(root, name, "plugin-data"), new EventHub(),
                (id, version, policy) => new ScopedPluginSettingsAccess(state, id, version, policy));
            Check(runtime.Status is [{ Id: PackageId, Version: PackageVersion, Loaded: true }] &&
                runtime.PackageBindings is [{ Id: PackageId, Version: PackageVersion, Loaded: true }],
                "real loader publishes only the built title package");
            checks++;
            Check(runtime.SettingsPolicies.TryGetValue(PackageId, out var binding) &&
                binding.PackageVersion == PackageVersion &&
                AssemblyLoadContext.GetLoadContext(binding.Policy.GetType().Assembly)?.IsCollectible == true &&
                Path.GetFullPath(binding.Policy.GetType().Assembly.Location) ==
                    Path.GetFullPath(Path.Combine(root, "packages", PackageId, AssemblyName)),
                "the central policy comes from the actual collectible loaded assembly");
            checks++;

            var gate = runtime.CommittedRootUserInputGate;
            var source = Source(SourceProfile);
            Check(runtime.HasCommittedRootUserInputObservers && gate.EligibleObservers(source).Count == 0,
                "Initialize observes retained legacy settings without enabling input delivery");
            checks++;

            var preferences = new SettingsPreferencesStore(state, () => runtime.SettingsPolicies);
            var migrated = preferences.SaveTransfer(new(new(legacy, seeded.State.State.Revision)));
            var current = migrated.State.State;
            Check(migrated.Changed && current.Revision == 2 &&
                current.Active is { SourcePackageVersion: PackageVersion, SettingsVersion: 2 } &&
                current.ValidationAuthority == "service-policy" &&
                current.Active.Values.GetProperty("enabled").GetBoolean() == false &&
                current.Active.Values.GetProperty("profileId").GetString() == WorkProfile &&
                current.Active.Values.GetProperty("profileRevision").GetInt64() == 1 &&
                current.Active.Values.GetProperty("sourceProfileIds").GetArrayLength() == 0,
                "both legacy schema editions migrate disabled with local inference references retained");
            checks++;
            var reopened = new SettingsPreferencesStore(new StateStore(state.DirectoryPath),
                () => runtime.SettingsPolicies).GetTransfer(coordinate).State;
            Check(reopened.Revision == current.Revision &&
                JsonElement.DeepEquals(reopened.Active!.Values, current.Active!.Values),
                "the migrated central document reopens at its exact revision");
            checks++;
            var noOp = preferences.SaveTransfer(new(new(current.Active!, current.Revision)));
            Check(!noOp.Changed && noOp.State.State.Revision == current.Revision,
                "an equivalent current-edition save leaves the revision unchanged");
            checks++;
            var stale = false;
            try { _ = preferences.SaveTransfer(new(new(current.Active!, current.Revision - 1))); }
            catch (InvalidOperationException) { stale = true; }
            Check(stale && preferences.GetTransfer(coordinate).State.Revision == current.Revision,
                "stale central CAS is refused without changing the selected state");
            checks++;

            var opted = new PluginSettingsDocument(PackageId, coordinate.Scope, null,
                PackageVersion, 2, CerebralJson.Element(new { enabled = true,
                    profileId = WorkProfile, profileRevision = 1L,
                    sourceProfileIds = new[] { SourceProfile } }));
            var enabled = preferences.SaveTransfer(new(new(opted, current.Revision)));
            Check(enabled.Changed && enabled.State.State.Revision == current.Revision + 1 &&
                gate.EligibleObservers(source).Count == 0,
                "saved opt-in is not a subscription before the loaded package refreshes");
            checks++;
            runtime.QueueOwnSettingsRefresh(PackageId);
            var clock = Stopwatch.StartNew();
            while (gate.EligibleObservers(source).Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            var target = gate.EligibleObservers(source).Single();
            Check(target.PackageId == PackageId &&
                gate.EligibleObservers(Source("other-source")).Count == 0,
                "actual asynchronous refresh enables only the exact configured source profile");
            checks++;
            using (var currentLease = gate.TryAcquire(target, source))
                Check(currentLease is not null, "current revision permits an actual callback lease");
            checks++;

            var disabled = preferences.SaveTransfer(new(new(opted with
            {
                Values = CerebralJson.Element(new { enabled = false,
                    profileId = WorkProfile, profileRevision = 1L,
                    sourceProfileIds = new[] { SourceProfile } })
            }, enabled.State.State.Revision)));
            using (var staleRevisionLease = gate.TryAcquire(target, source))
                Check(disabled.Changed && staleRevisionLease is null,
                    "a changed settings revision immediately refuses the previously advertised target");
            checks++;
            runtime.QueueOwnSettingsRefresh(PackageId);
            clock.Restart();
            while (gate.EligibleObservers(source).Count != 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Check(gate.EligibleObservers(source).Count == 0,
                "completed disable refresh also removes the cached discovery target");
            checks++;
            Check(preferences.GetTransfer(coordinate).State.Active!.Values
                    .GetProperty("enabled").GetBoolean() == false,
                "central policy retains the explicit disabled state");
            checks++;
        }
        catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            if (runtime is not null)
            {
                try
                {
                    runtime.Dispose();
                    await runtime.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error)
                {
                    if (primary is null) primary = ExceptionDispatchInfo.Capture(error);
                    else primary.SourceException.Data["fixtureCleanupFailureType"] = error.GetType().Name;
                }
            }
        }
        primary?.Throw();
        return checks;
    }

    private static CommittedRootUserInputSource Source(string profileId) =>
        new("source-request", "source-conversation", profileId, "pegasus.conversation:chat",
            "1.0.5-local-generation.1", null, "source-event", 1);

    private static string ExistingDirectory(string value)
    {
        if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value))
            throw new ArgumentException("Provide an existing absolute title fixture directory.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        for (var cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Title fixture directories cannot contain links.");
        return path;
    }

    private static bool IsUnder(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequireFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 2 or > 16_777_216)
            throw new ArgumentException("The built title package input is absent, linked, or oversized.");
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("Loaded title fixture: " + description);
    }
}
