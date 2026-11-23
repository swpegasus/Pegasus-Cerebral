using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using static Cerebral.Worker.SourceRecoveryManifest;

namespace Cerebral.Worker.Tests;

public sealed record SourceRecoveryConfigurationCheckResult(int ConfigurationChecks, string EvidenceTier,
    string PhaseTransitionMechanism, bool ProductionSourceStartupExercised);

/// <summary>
/// Actual Configuration and StateStore reads against private SQLite. A callback returning null
/// explicitly models release; this component fixture neither opens a Source host nor releases a fence.
/// All credential/grant changes use labeled fixture values and are never printed or returned.
/// </summary>
public static class SourceRecoveryConfigurationTests
{
    public static SourceRecoveryConfigurationCheckResult CheckSourceRecoveryConfigurationFixtures(string privateRoot)
    {
        privateRoot = CanonicalPath(privateRoot);
        if (!Directory.Exists(privateRoot)) Directory.CreateDirectory(privateRoot);
        CheckExistingPath(privateRoot, true);
        var checks = new Checks();
        CheckFreshSettings(privateRoot, checks);
        CheckMissingAndChangedIdentity(privateRoot, checks);
        CheckInitialAttachment(privateRoot, checks);
        return new(checks.Count, "actual-SQLite-Configuration-component-fixture",
            "Trusted detached callback returns null to model release; no live Source or fence transition.", false);
    }

    private static void CheckFreshSettings(string root, Checks checks)
    {
        var fixture = new Fixture(root);
        var closed = true;
        var configuration = new Configuration(fixture.Store);
        configuration.AttachSourceRecoverySnapshot(() => closed ? Clone(fixture.Verified) : null);
        var originalSaved = fixture.Records();
        var snapshot = configuration.Get();
        checks.That(snapshot.Name == fixture.Verified.Name && snapshot.Revision == fixture.Verified.Revision,
            "closed_reads_verified_snapshot_even_when_saved_legacy_candidates_differ");
        snapshot.Name = "private returned-object edit";
        snapshot.Executables.Add(new() { Id = "private-returned-only", Path = "private-returned-only" });
        checks.That(configuration.Get().Name == fixture.Verified.Name &&
            configuration.Get().Executables.All(item => item.Id != "private-returned-only"),
            "closed_callback_returns_detached_snapshot_each_read");
        checks.That(fixture.Records().SequenceEqual(originalSaved), "closed_reads_do_not_mutate_actual_sqlite_records");
        var firstOwnerSave = Clone(fixture.Saved);
        firstOwnerSave.Name = "private owner update while fenced";
        firstOwnerSave.Revision = 34;
        fixture.Store.Put("settings", "host", firstOwnerSave);
        checks.That(configuration.Get().Name == fixture.Verified.Name,
            "closed_phase_retains_verified_snapshot_instead_of_unverified_saved_update");
        var beforeReleaseReads = fixture.Records();
        closed = false;
        var current = configuration.Get();
        checks.That(current.Name == firstOwnerSave.Name && current.Revision == 34,
            "null_callback_reads_fresh_actual_saved_host_after_modeled_release");
        checks.That(current.DefaultModelId == "" && current.Executables.Count == 0 &&
            current.ModelDirectory == "" && current.SharedDirectory == "" && current.ToolsDirectory == "" &&
            current.LlamaServerPath == "", "released_get_does_not_discover_default_paths_models_or_executables");
        current.Name = "private detached released edit";
        current.Executables.Add(new() { Id = "released-returned-only", Path = "released-returned-only" });
        var next = configuration.Get();
        checks.That(next.Name == firstOwnerSave.Name && next.Executables.Count == 0,
            "released_get_materializes_fresh_saved_object_without_returned_object_aliasing");
        for (var index = 0; index < 5; index++) _ = configuration.Get();
        checks.That(fixture.Records().SequenceEqual(beforeReleaseReads),
            "repeated_released_get_keeps_every_host_profile_body_and_revision_identical");
        var lead = fixture.Store.Get<AgentProfile>("agent", "legacy-lead")!;
        var helper = fixture.Store.Get<AgentProfile>("agent", "legacy-helper")!;
        checks.That(lead.Name == "Orchestrator" && lead.Revision == 7 && !lead.FollowsDefaultModel &&
            helper.Revision == 9 && !helper.FollowsDefaultModel,
            "released_get_preserves_legacy_name_profile_revisions_and_default_following_flags");
        checks.That(lead.AllowedRoots.SequenceEqual(fixture.OriginalRoots) && helper.AllowedRoots.SequenceEqual(fixture.OriginalRoots),
            "released_get_does_not_replace_old_managed_project_root_in_profiles");
        checks.That(!Directory.Exists(fixture.ConfiguredProjectsRoot) && !Directory.Exists(fixture.LegacyProjectsRoot),
            "released_get_creates_no_project_directories");
        checks.That(fixture.EngineDigest() == fixture.InitialEngineDigest, "configuration_reads_leave_all_fixture_workflow_storage_unchanged");

        var secondOwnerSave = Clone(next);
        secondOwnerSave.Revision = 41;
        secondOwnerSave.Name = "private latest owner name";
        secondOwnerSave.AccessKey = "private-fixture-owner-key-new-and-never-projected";
        secondOwnerSave.ExternalCommandsEnabled = true;
        secondOwnerSave.AllowLan = true;
        secondOwnerSave.DefaultModelId = "owner-chosen-model";
        secondOwnerSave.DefaultModelWorkerId = "owner-chosen-binding";
        secondOwnerSave.Executables = [new() { Id = "owner-saved-tool", Path = Path.Combine(fixture.Files.Root, "owner-saved-tool.exe") }];
        fixture.Store.Put("settings", "host", secondOwnerSave);
        var ownerLead = CloneProfile(lead);
        ownerLead.Name = "private owner-selected lead name";
        ownerLead.Revision = 20;
        fixture.Store.Put("agent", ownerLead.Id, ownerLead);
        var afterExplicitOwnerSave = fixture.Records();
        var fresh = configuration.Get();
        checks.That(fresh.Name == secondOwnerSave.Name && fresh.Revision == 41 &&
            fresh.DefaultModelId == secondOwnerSave.DefaultModelId && fresh.DefaultModelWorkerId == secondOwnerSave.DefaultModelWorkerId,
            "next_released_get_observes_explicit_latest_owner_settings_and_binding");
        checks.That(fresh.AccessKey == secondOwnerSave.AccessKey && fresh.AccessKey != fixture.Verified.AccessKey &&
            fresh.ExternalCommandsEnabled && fresh.AllowLan && fresh.Executables.Single().Id == "owner-saved-tool",
            "fresh_read_does_not_retain_stale_fixture_key_grant_or_executable_snapshot");
        checks.That(fixture.Store.Get<AgentProfile>("agent", "legacy-lead")!.Name == ownerLead.Name &&
            fixture.Store.Get<AgentProfile>("agent", "legacy-lead")!.Revision == 20,
            "owner_selected_profile_name_and_revision_remain_untouched");
        for (var index = 0; index < 5; index++) _ = configuration.Get();
        checks.That(fixture.Records().SequenceEqual(afterExplicitOwnerSave),
            "repeated_get_after_owner_save_adds_no_discovery_or_migration_writes");
        var blankProjects = Clone(fresh);
        blankProjects.Revision = 42;
        blankProjects.ProjectsDirectory = "";
        fixture.Store.Put("settings", "host", blankProjects);
        var beforeBlankReads = fixture.Records();
        var blank = configuration.Get();
        checks.That(blank.ProjectsDirectory == "" && blank.Revision == 42 && !Directory.Exists(fixture.LegacyProjectsRoot),
            "blank_saved_project_path_is_preserved_without_lazy_default_directory_creation");
        checks.That(fixture.Records().SequenceEqual(beforeBlankReads), "blank_saved_paths_do_not_trigger_persisted_normalization");
    }

    private static void CheckMissingAndChangedIdentity(string root, Checks checks)
    {
        foreach (var scenario in new[] { "different", "empty", "missing", "malformed" })
        {
            var fixture = new Fixture(root);
            var closed = true;
            var configuration = new Configuration(fixture.Store);
            configuration.AttachSourceRecoverySnapshot(() => closed ? Clone(fixture.Verified) : null);
            closed = false;
            if (scenario == "missing") fixture.Store.Delete("settings", "host");
            else if (scenario == "malformed") fixture.Files.Sql(fixture.Files.MainPath,
                "UPDATE records SET body='private malformed fixture' WHERE kind='settings' AND id='host'");
            else
            {
                var changed = Clone(fixture.Saved);
                changed.InstallationId = scenario == "empty" ? "" : Guid.NewGuid().ToString("N");
                fixture.Store.Put("settings", "host", changed);
            }
            var before = fixture.Records();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try { _ = configuration.Get(); throw new InvalidOperationException("fixture_expected_configuration_refusal:" + scenario); }
                catch (InvalidOperationException error) when (error.Message == "source_recovery_configuration_identity_changed")
                { checks.That(scenario != "malformed", scenario + "_identity_refusal"); }
                catch (JsonException) { checks.That(scenario == "malformed", "malformed_saved_host_refused_without_default_fallback"); }
            }
            checks.That(fixture.Records().SequenceEqual(before), scenario + "_failure_does_not_heal_or_migrate_saved_records");
            checks.That(!Directory.Exists(fixture.ConfiguredProjectsRoot) && !Directory.Exists(fixture.LegacyProjectsRoot),
                scenario + "_failure_creates_no_default_directories");
            checks.That(fixture.EngineDigest() == fixture.InitialEngineDigest, scenario + "_failure_preserves_workflow_storage");
        }
    }

    private static void CheckInitialAttachment(string root, Checks checks)
    {
        var fixture = new Fixture(root);
        var configuration = new Configuration(fixture.Store);
        var before = fixture.Records();
        try { configuration.AttachSourceRecoverySnapshot(() => null); throw new InvalidOperationException("fixture_missing_initial_snapshot_accepted"); }
        catch (InvalidOperationException error) when (error.Message == "source_recovery_configuration_unavailable") { checks.Count++; }
        checks.That(fixture.Records().SequenceEqual(before), "missing_initial_snapshot_attachment_writes_nothing");
        configuration.AttachSourceRecoverySnapshot(() => Clone(fixture.Verified));
        try { configuration.AttachSourceRecoverySnapshot(() => Clone(fixture.Verified)); throw new InvalidOperationException("fixture_duplicate_configuration_attachment_accepted"); }
        catch (InvalidOperationException error) when (error.Message == "source_recovery_configuration_already_bound") { checks.Count++; }
        checks.That(configuration.Get().InstallationId == fixture.Verified.InstallationId && fixture.Records().SequenceEqual(before),
            "failed_duplicate_attachment_preserves_original_binding_and_records");
    }

    private static HostSettings Clone(HostSettings settings) => CerebralJson.Read<HostSettings>(CerebralJson.Element(settings));
    private static AgentProfile CloneProfile(AgentProfile profile) => CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
    private sealed class Checks
    {
        internal int Count;
        internal void That(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("source_recovery_configuration_fixture_failed:" + label); Count++; }
    }
    private sealed class Fixture
    {
        internal readonly SourceRecoveryPreflightTests.Fixture Files;
        internal readonly StateStore Store;
        internal readonly HostSettings Verified;
        internal readonly HostSettings Saved;
        internal string LegacyProjectsRoot => Path.Combine(Files.Root, "Projects");
        internal string ConfiguredProjectsRoot => Path.Combine(Files.Root, "owner-configured-projects");
        internal IReadOnlyList<string> OriginalRoots => [LegacyProjectsRoot, Path.Combine(Files.Root, "custom-root")];
        internal readonly string InitialEngineDigest;
        internal Fixture(string privateRoot)
        {
            Files = new SourceRecoveryPreflightTests.Fixture(privateRoot);
            Verified = SourceRecoveryPreflight.Verify(Files.Parse(), CancellationToken.None).Settings;
            InitialEngineDigest = EngineDigest();
            Store = new StateStore(Files.Root, deferDiagnosticPruning: true);
            Saved = Clone(Verified);
            Saved.Revision = 33;
            Saved.Name = "private legacy normalization candidates";
            Saved.ModelDirectory = ""; Saved.SharedDirectory = ""; Saved.ToolsDirectory = "";
            Saved.LlamaServerPath = ""; Saved.ProjectsDirectory = ConfiguredProjectsRoot;
            Saved.DefaultModelId = ""; Saved.DefaultModelWorkerId = "local"; Saved.Executables = [];
            Store.Put("settings", "host", Saved);
            Store.Put("agent", "legacy-lead", new AgentProfile { Id = "legacy-lead", Revision = 7, Name = "Orchestrator",
                Role = "lead", WorkerId = "local", ModelWorkerId = "local", ModelId = "legacy-model",
                FollowsDefaultModel = false, AllowedRoots = OriginalRoots.ToList() });
            Store.Put("agent", "legacy-helper", new AgentProfile { Id = "legacy-helper", Revision = 9, Name = "private helper",
                Role = "helper", WorkerId = "local", ModelWorkerId = "local", ModelId = "legacy-model",
                FollowsDefaultModel = false, AllowedRoots = OriginalRoots.ToList() });
        }
        internal string[] Records()
        {
            using var connection = Files.Open(Files.MainPath); using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind,id,body FROM records ORDER BY kind,id";
            using var reader = command.ExecuteReader(); var result = new List<string>();
            while (reader.Read()) result.Add(Digest(SourceRecoveryPreflight.EncodePythonTriplets(["kind", "id", "body"],
                [reader.GetValue(0), reader.GetValue(1), reader.GetValue(2)])));
            return result.ToArray();
        }
        internal string EngineDigest() => Digest(File.ReadAllBytes(Files.EnginePath));
    }
}
