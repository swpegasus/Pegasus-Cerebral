using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Cerebral.Worker.SourceRecoveryManifest;

namespace Cerebral.Worker.Tests;

public sealed record SourceRecoveryRuntimeCheckResult(int DiagnosticChecks, int EntryWaitChecks,
    int AttachedRuntimeChecks, int NegativeRuntimeChecks, string EvidenceTier,
    bool ProductionProgramStartupExercised, bool ProductionSourceAdmissionExercised,
    string StrategyAssemblySha256);

/// <summary>
/// Actual generic host, Worker scheduler/retention/dispatcher, EngineController and SQLite, with
/// a separately compiled CPU-only fixture strategy. No production Program or Source path is used.
/// The strategy refuses evaluation; model/peer execution sentinels are guards, not provider proof.
/// </summary>
public static class SourceRecoveryRuntimeTests
{
    private static readonly string[] Columns = ["id", "request_id", "conversation_id", "profile_id", "status", "revision", "submission", "payload"];
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public static async Task<SourceRecoveryRuntimeCheckResult> CheckSourceRecoveryRuntimeFixturesAsync(
        string privateRoot, string cpuStrategyAssembly)
    {
        privateRoot = CanonicalPath(privateRoot);
        cpuStrategyAssembly = CanonicalPath(cpuStrategyAssembly);
        if (!Directory.Exists(privateRoot)) Directory.CreateDirectory(privateRoot);
        CheckExistingPath(privateRoot, true);
        CheckExistingPath(cpuStrategyAssembly, false);
        var diagnostic = CheckDiagnosticPruning(privateRoot, cpuStrategyAssembly);
        var entry = await CheckEntryWaitsAsync(privateRoot, cpuStrategyAssembly).ConfigureAwait(false);
        var attached = await CheckAttachedRuntimeAsync(privateRoot, cpuStrategyAssembly).ConfigureAwait(false);
        attached += await CheckCommitCancellationAsync(privateRoot, cpuStrategyAssembly).ConfigureAwait(false);
        var negative = await CheckNegativeRuntimesAsync(privateRoot, cpuStrategyAssembly).ConfigureAwait(false);
        return new(diagnostic, entry, attached, negative,
            "actual-in-process-host-and-owned-user-pipe-with-CPU-fixture-strategy-and-SQLite", false, false,
            Digest(File.ReadAllBytes(cpuStrategyAssembly)));
    }

    private static int CheckDiagnosticPruning(string root, string strategy)
    {
        var checks = new Checks();
        var deferred = new Fixture(root, strategy);
        deferred.SeedDiagnostics();
        checks.That(deferred.DiagnosticCount() == 3, "three_real_sqlite_diagnostics_seeded");
        _ = new StateStore(deferred.Files.Root, deferDiagnosticPruning: true);
        checks.That(deferred.DiagnosticCount() == 3, "guarded_constructor_preserves_expired_and_future_rows");
        var ordinary = new Fixture(root, strategy);
        ordinary.SeedDiagnostics();
        _ = new StateStore(ordinary.Files.Root);
        checks.That(ordinary.DiagnosticCount() == 1, "ordinary_constructor_really_prunes_expired_and_future_rows");
        checks.That(deferred.ReadRows().SequenceEqual(deferred.Baseline), "deferred_constructor_keeps_all_fourteen_triplets");
        checks.That(ordinary.ReadRows().SequenceEqual(ordinary.Baseline), "ordinary_diagnostic_prune_does_not_change_workflow_rows");
        return checks.Count;
    }

    private static async Task<int> CheckEntryWaitsAsync(string root, string strategy)
    {
        var checks = new Checks();
        var fixture = new Fixture(root, strategy);
        fixture.SeedDiagnostics();
        await using var composition = await Composition.CreateAsync(fixture).ConfigureAwait(false);
        using var fence = new SourceRecoveryFence();
        composition.Scheduler.AttachSourceRecoveryFence(fence);
        composition.Retention.AttachSourceRecoveryFence(fence);
        composition.Store.Put("model", "stale-resident-metadata", new ModelRecord
            { Id = "stale-resident-metadata", Name = "fixture metadata only", State = "loaded" });
        var running = fixture.Run(99) with { Id = "running-entry-sentinel", Status = WorkflowStatus.Running };
        fixture.InsertRun(running);
        using var deadline = new CancellationTokenSource(Limit);
        var sweep = composition.Retention.RunSweepAsync(DateTimeOffset.UtcNow, deadline.Token);
        await composition.Host.StartAsync(deadline.Token).ConfigureAwait(false);
        await Task.Delay(120, deadline.Token).ConfigureAwait(false);
        checks.That(!fence.IsOpen && !sweep.IsCompleted, "actual_host_started_but_entry_and_manual_sweep_wait");
        checks.That(composition.Store.Get<ModelRecord>("model", "stale-resident-metadata")!.State == "loaded",
            "scheduler_startup_model_unloaded_write_has_not_run");
        checks.That(composition.Workflows.Store.Get(running.Id).Status == WorkflowStatus.Running,
            "scheduler_recover_interrupted_has_not_run");
        checks.That(fixture.DiagnosticCount() == 3, "hosted_retention_and_direct_sweep_have_not_pruned");
        checks.That(fixture.ReadRows(excludeSentinel: true).SequenceEqual(fixture.Baseline), "entry_wait_keeps_fourteen_rows");
        deadline.Cancel();
        await ExpectCancellationAsync(sweep, checks, "manual_sweep_cancelled_without_release").ConfigureAwait(false);
        using var cleanup = new CancellationTokenSource(Limit);
        await composition.Host.StopAsync(cleanup.Token).ConfigureAwait(false);
        checks.That(!fence.IsOpen && fixture.DiagnosticCount() == 3,
            "actual_host_stop_cancels_waiters_without_recovery_or_prune");
        return checks.Count;
    }

    private static async Task<int> CheckAttachedRuntimeAsync(string root, string strategy)
    {
        var checks = new Checks();
        var fixture = new Fixture(root, strategy);
        fixture.SeedDiagnostics();
        var manifest = fixture.Files.Parse();
        var preflight = SourceRecoveryPreflight.Verify(manifest, CancellationToken.None);
        var local = new LocalRunControl(true);
        await using var recovery = new SourceRecoveryStartup(manifest, preflight, local);
        local.SourceRecovery = recovery;
        using var lifetime = new CancellationTokenSource(Limit);
        var endpoint = new LocalWorkerEndpoint(manifest.PipeName, manifest.PipeInstanceId);
        await recovery.StartPipeAsync(endpoint, new EventHub(), lifetime.Token).ConfigureAwait(false);
        using var client = new CerebralClient(endpoint);
        var reply = client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: lifetime.Token);
        var ticket = await recovery.WaitForOwnerResumeAsync(lifetime.Token).WaitAsync(Limit).ConfigureAwait(false);
        recovery.BeginValidation(ticket);
        var start = new StartBoundary();
        await using var composition = await Composition.CreateAsync(fixture, recovery, local, start).ConfigureAwait(false);
        local.Stop = composition.Host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication;
        composition.Store.Put("model", "stale-resident-metadata", new ModelRecord
            { Id = "stale-resident-metadata", Name = "fixture metadata only", State = "loaded" });
        var run = SourceRecoveryRuntime.RunAsync(composition.Host, recovery, ticket, composition.Commands,
            composition.Engine, composition.Scheduler, composition.Retention, composition.Models, lifetime.Token);
        await start.Entered.Task.WaitAsync(Limit).ConfigureAwait(false);
        checks.That(recovery.Fence.State == "validating" && !recovery.Fence.IsOpen && !reply.IsCompleted,
            "real_pipe_one_request_waits_for_actual_host_start");
        var engine = composition.Engine.Status();
        checks.That(engine is { Paused: true, Epoch: 5, ActiveEvaluations: 0, CommittedDecisions: 0,
                CandidatePending: false, Error: null, Recovery: null } && engine.Active?.Id == "fixture.source-recovery.cpu",
            "actual_cpu_fixture_engine_initialized_and_healthy_at_exact_epoch");
        checks.That(fixture.ReadRows().SequenceEqual(fixture.Baseline), "actual_composition_preserves_fourteen_triplets");
        checks.That(fixture.DiagnosticCount() == 3, "composition_and_early_host_keep_all_diagnostic_rows");
        checks.That(composition.Store.Get<ModelRecord>("model", "stale-resident-metadata")!.State == "loaded",
            "scheduler_entry_still_closed_during_start_boundary");
        var slots = composition.Models.ObserveManagedSlots();
        checks.That(slots is { State: "observed", Truncated: false, ModelAdmissionOpen: false } && slots.Models.Count == 0,
            "actual_managed_observation_is_zero_local_and_closed");
        var status = await client.CommandAsync("engine.status", cancellationToken: lifetime.Token).ConfigureAwait(false);
        checks.That(status.Success && status.Data.GetProperty("epoch").GetInt64() == 5 &&
            status.Data.GetProperty("active").GetProperty("id").GetString() == "fixture.source-recovery.cpu",
            "existing_owned_pipe_reports_actual_attached_engine");
        var hold = await client.CommandAsync("engine.work-hold.status", cancellationToken: lifetime.Token).ConfigureAwait(false);
        checks.That(hold.Success && hold.Data.GetProperty("revision").GetInt64() == 2 &&
            hold.Data.GetProperty("state").GetString() == "held", "owned_pipe_reports_real_held_resolver_state");
        foreach (var operation in new[] { "models.load", "settings.get", "engine.resume", "jobs.submit", "resources.local.resume" })
        {
            var denied = await composition.Commands.ExecuteAsync(Command(operation), lifetime.Token).ConfigureAwait(false);
            checks.That(!denied.Success && denied.Error == "source_recovery_held", "attached_dispatcher_refuses_" + operation);
        }
        var owner = await client.CommandAsync(OwnerModelSnapshotService.Operation,
            new { schemaVersion = 1, expectedWorkerInstallationId = manifest.InstallationId,
                expectedControllerInstallationId = (string?)null }, cancellationToken: lifetime.Token).ConfigureAwait(false);
        checks.That(!owner.Success && owner.Error == "source_recovery_held", "real_pipe_owner_snapshot_remains_closed");
        using (var disconnected = new NamedPipeServerStream("private-runtime-guard-" + Guid.NewGuid().ToString("N")))
        {
            var attachedOwner = await ((IOwnerModelSnapshotDispatcher)composition.Commands).ExecuteOwnerSnapshotPipeAsync(
                disconnected, null, Command(OwnerModelSnapshotService.Operation), 0, lifetime.Token).ConfigureAwait(false);
            checks.That(!attachedOwner.Success && attachedOwner.Error == "source_recovery_held",
                "attached_owner_pipe_seam_refuses_before_transport_or_lazy_admission");
        }
        var attachedHttp = await composition.Commands.ExecuteOwnerSnapshotHttpAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            Command(OwnerModelSnapshotService.Operation), lifetime.Token).ConfigureAwait(false);
        checks.That(!attachedHttp.Success && attachedHttp.Error == "source_recovery_held",
            "attached_owner_http_seam_refuses_before_transport_or_lazy_admission");
        checks.That(composition.Commands.OwnerSnapshotVerifiedAdmissions == 0,
            "closed_owner_snapshot_has_no_verified_admission");
        using var sweepLifetime = new CancellationTokenSource(Limit);
        var sweep = composition.Retention.RunSweepAsync(DateTimeOffset.UtcNow, sweepLifetime.Token);
        await Task.Delay(40, lifetime.Token).ConfigureAwait(false);
        checks.That(!sweep.IsCompleted && fixture.DiagnosticCount() == 3, "attached_manual_retention_sweep_waits_for_release");
        start.Continue.TrySetResult();
        var result = await reply.WaitAsync(Limit).ConfigureAwait(false);
        checks.That(result.Success && recovery.Fence.IsOpen, "one_real_owned_pipe_resume_releases_actual_host");
        checks.That(result.Data.GetProperty("hold").GetProperty("revision").GetInt64() == 4 &&
            result.Data.GetProperty("hold").GetProperty("requestId").GetString() == RequestId &&
            !result.Data.GetProperty("originalPriorEnginePaused").GetBoolean(), "real_resolver_exact_G1_R2_to_R4_original_pause_false");
        checks.That(result.Data.GetProperty("observedSourceManagedModelInstances").GetInt32() == 0 &&
            result.Data.GetProperty("remoteModelOwnership").GetString()!.Contains("unobserved", StringComparison.Ordinal),
            "reply_distinguishes_observed_local_zero_from_remote_unobserved");
        engine = composition.Engine.Status();
        checks.That(!engine.Paused && engine.Epoch == 5 && engine.ActiveEvaluations == 0 && engine.CommittedDecisions == 0,
            "actual_engine_resumed_without_strategy_evaluation");
        await sweep.WaitAsync(Limit).ConfigureAwait(false);
        await UntilAsync(() => composition.Store.Get<ModelRecord>("model", "stale-resident-metadata")!.State == "unloaded").ConfigureAwait(false);
        checks.That(fixture.DiagnosticCount() == 1, "actual_retention_prunes_only_after_release");
        checks.That(fixture.ReadRows().SequenceEqual(fixture.Baseline), "actual_post_release_scheduler_and_resolver_preserve_all_fourteen");
        var resources = composition.Scheduler.GetResourcePauseStatus();
        checks.That(resources.State == LocalResourceState.Paused && !resources.RerouteEligibleWork &&
            resources.ModelsToReload.SequenceEqual(new[] { "retained-owner-model-id" }),
            "owner_pause_and_resident_id_intent_preserved_without_resource_resume");
        var typed = composition.Workflows.Store.ListActive(20);
        checks.That(typed.Count == 14 && typed.All(row => row.Status == WorkflowStatus.Waiting && row.WaitingForRunIds.Count == 0) &&
            typed.Count(row => row.Effect?.State == WorkflowEffectState.Unknown) == 12 && typed.Count(row => row.Effect is null) == 2,
            "actual_typed_waiting_unknown_and_null_effects_preserved");
        var second = await client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: lifetime.Token).ConfigureAwait(false);
        checks.That(!second.Success && composition.Scheduler.GetIntentionalWorkHoldStatus().Revision == 4,
            "second_resume_does_not_replay_durable_resolver");
        await Task.Delay(60, lifetime.Token).ConfigureAwait(false);
        checks.That(!run.IsCompleted && recovery.Fence.IsOpen, "reply_connection_disconnect_does_not_cancel_admitted_host");
        var settingsBefore = fixture.RecordBody("settings", "host");
        checks.That(!settingsBefore.Contains("source_recovery", StringComparison.Ordinal), "recovery_never_persists_private_fence_in_host_settings");
        composition.Host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await run.WaitAsync(Limit).ConfigureAwait(false);
        checks.That(recovery.Shutdown.IsCancellationRequested && !recovery.RuntimeAdmissionOpen,
            "normal_host_shutdown_closes_runtime_admission");
        var afterStop = await composition.Commands.ExecuteAsync(Command("models.load"), CancellationToken.None).ConfigureAwait(false);
        checks.That(!afterStop.Success && afterStop.Error == "source_recovery_held", "attached_admission_stays_closed_on_shutdown");
        using (var disconnected = new NamedPipeServerStream("private-runtime-stopped-" + Guid.NewGuid().ToString("N")))
        {
            var stoppedOwner = await ((IOwnerModelSnapshotDispatcher)composition.Commands).ExecuteOwnerSnapshotPipeAsync(
                disconnected, null, Command(OwnerModelSnapshotService.Operation), 0, CancellationToken.None).ConfigureAwait(false);
            checks.That(!stoppedOwner.Success && stoppedOwner.Error == "source_recovery_held",
                "attached_owner_snapshot_remains_closed_after_shutdown");
        }
        return checks.Count;
    }

    private static async Task<int> CheckCommitCancellationAsync(string root, string strategy)
    {
        var checks = new Checks();
        var fixture = new Fixture(root, strategy);
        fixture.SeedDiagnostics();
        var manifest = fixture.Files.Parse();
        var local = new LocalRunControl(true);
        await using var recovery = new SourceRecoveryStartup(manifest,
            SourceRecoveryPreflight.Verify(manifest, CancellationToken.None), local);
        local.SourceRecovery = recovery;
        using var caller = new CancellationTokenSource();
        using var source = new CancellationTokenSource(Limit);
        using var validation = CancellationTokenSource.CreateLinkedTokenSource(source.Token, caller.Token, recovery.Shutdown);
        var reply = recovery.ExecuteAsync(Command("engine.work-hold.resume", CerebralJson.Element(ResumeArguments())), caller.Token);
        var ticket = await recovery.WaitForOwnerResumeAsync(source.Token).WaitAsync(Limit).ConfigureAwait(false);
        recovery.BeginValidation(ticket);
        await using var composition = await Composition.CreateAsync(fixture, recovery, local).ConfigureAwait(false);
        composition.Scheduler.AttachSourceRecoveryFence(recovery.Fence);
        composition.Retention.AttachSourceRecoveryFence(recovery.Fence);
        await composition.Models.BeginPauseAsync(validation.Token).ConfigureAwait(false);
        recovery.AttachRuntime(composition.Commands, composition.Engine);
        await composition.Host.StartAsync(validation.Token).ConfigureAwait(false);
        checks.That(!recovery.RuntimeAdmissionOpen && fixture.DiagnosticCount() == 3,
            "commit_cancellation_actual_host_started_behind_closed_fence");
        var resumed = composition.Scheduler.ResolveIntentionalWorkHold(HoldId, 5, 1, 2, false);
        var saved = SourceRecoveryPreflight.VerifyPersistedState(manifest, SourceRecoveryExpectedState.Resumed, validation.Token);
        var rows = SourceRecoveryPreflight.VerifyRows(manifest, validation.Token);
        var engine = composition.Engine.Status();
        checks.That(resumed is { Revision: 4, State: "resumed", EnginePaused: false } &&
            saved.Coordinates.HoldRevision == 4 && engine is { Epoch: 5, Paused: false, Error: null, Recovery: null },
            "commit_cancellation_after_real_durable_resolver_before_publication");
        // Derive evidence from the actual attached runtime; only the cancellation is injected here.
        var evidence = new SourceRecoveryReleaseEvidence(resumed.HoldId!, resumed.Generation, resumed.Revision,
            engine.Epoch, resumed.State, engine.Paused, false, engine.Active is not null && engine.Error is null && engine.Recovery is null,
            engine.CandidatePending, engine.ActiveEvaluations, engine.UnreleasedContexts, resumed.Work.Count,
            rows.BaselineMatched, manifest.PassiveInitializerReview.State == "reviewed", true);
        checks.That(evidence.Valid, "commit_cancellation_runtime_evidence_would_otherwise_validate");
        using var abort = validation.Token.Register(() => recovery.FailResume(ticket, "source_recovery_resume_cancelled"));
        source.Cancel();
        try
        {
            recovery.CompleteResume(ticket, evidence, new { fixtureOnly = true }, validation.Token);
            throw new InvalidOperationException("fixture_cancelled_commit_opened");
        }
        catch (OperationCanceledException) { checks.Count++; }
        checks.That(!recovery.RuntimeAdmissionOpen && recovery.Fence.State == "failed",
            "linked_cancelled_validation_refuses_actual_attached_commit");
        var response = await reply.WaitAsync(Limit).ConfigureAwait(false);
        checks.That(!response.Success && response.Error == "source_recovery_resume_unconfirmed",
            "durably_resumed_but_cancelled_publication_reports_uncertain");
        checks.That(fixture.ReadRows().SequenceEqual(fixture.Baseline) && fixture.DiagnosticCount() == 3,
            "cancelled_commit_keeps_fourteen_rows_and_prevents_retention");
        var snapshot = composition.Models.ObserveManagedSlots();
        checks.That(snapshot is { State: "observed", ModelAdmissionOpen: false } && snapshot.Models.Count == 0,
            "cancelled_commit_local_model_admission_stays_closed");
        var again = await recovery.ExecuteAsync(Command("engine.work-hold.resume", CerebralJson.Element(ResumeArguments()))).ConfigureAwait(false);
        checks.That(!again.Success && composition.Scheduler.GetIntentionalWorkHoldStatus().Revision == 4,
            "cancelled_commit_never_replays_existing_durable_resume");
        return checks.Count;
    }

    private static async Task<int> CheckNegativeRuntimesAsync(string root, string strategy)
    {
        var checks = new Checks();
        foreach (var scenario in new[] { "pause-missing", "pause-faulted", "pause-reroute", "package-drift", "engine-selection-drift",
            "partial-start-failure", "caller-cancel", "validation-token-cancel", "host-shutdown" })
        {
            var fixture = new Fixture(root, strategy);
            fixture.SeedDiagnostics();
            var manifest = fixture.Files.Parse();
            var local = new LocalRunControl(true);
            await using var recovery = new SourceRecoveryStartup(manifest,
                SourceRecoveryPreflight.Verify(manifest, CancellationToken.None), local);
            local.SourceRecovery = recovery;
            using var caller = new CancellationTokenSource();
            using var lifetime = new CancellationTokenSource(Limit);
            var reply = recovery.ExecuteAsync(Command("engine.work-hold.resume", CerebralJson.Element(ResumeArguments())), caller.Token);
            var ticket = await recovery.WaitForOwnerResumeAsync(lifetime.Token).WaitAsync(Limit).ConfigureAwait(false);
            recovery.BeginValidation(ticket);
            var boundary = scenario is "partial-start-failure" or "caller-cancel" or "validation-token-cancel" or "host-shutdown"
                ? new StartBoundary { FailStart = scenario == "partial-start-failure" } : null;
            await using var composition = await Composition.CreateAsync(fixture, recovery, local, boundary).ConfigureAwait(false);
            if (scenario == "pause-missing") composition.Store.Delete(ResourcePausePersistence.RecordKind, ResourcePausePersistence.RecordId);
            if (scenario == "pause-faulted") composition.Store.Put(ResourcePausePersistence.RecordKind, ResourcePausePersistence.RecordId,
                ResourcePausePersistence.Read(composition.Store) with { State = LocalResourceState.Faulted, Detail = "fixture retained fault" });
            if (scenario == "pause-reroute") composition.Store.Put(ResourcePausePersistence.RecordKind, ResourcePausePersistence.RecordId,
                ResourcePausePersistence.Read(composition.Store) with { RerouteEligibleWork = true });
            if (scenario == "package-drift") File.AppendAllText(Path.Combine(fixture.Files.RoleRoot("engine"), "A", "engine-settings.ini"), "; fixture drift\n");
            if (scenario == "engine-selection-drift") fixture.Files.ChangeSelection("epoch", "6", repin: false);
            var run = SourceRecoveryRuntime.RunAsync(composition.Host, recovery, ticket, composition.Commands,
                composition.Engine, composition.Scheduler, composition.Retention, composition.Models, lifetime.Token);
            if (boundary is not null)
            {
                await boundary.Entered.Task.WaitAsync(Limit).ConfigureAwait(false);
                if (scenario == "caller-cancel") caller.Cancel();
                if (scenario == "validation-token-cancel") lifetime.Cancel();
                if (scenario == "host-shutdown") composition.Host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                if (scenario == "partial-start-failure") boundary.Continue.TrySetResult();
            }
            try { await run.WaitAsync(Limit).ConfigureAwait(false); throw new InvalidOperationException("fixture_missing_runtime_refusal:" + scenario); }
            catch (InvalidOperationException error) when (error.Message == "source_recovery_resume_unconfirmed") { checks.Count++; }
            var response = await reply.WaitAsync(Limit).ConfigureAwait(false);
            checks.That(!response.Success && !recovery.Fence.IsOpen && recovery.Fence.State == "failed", scenario + "_permanently_closed");
            checks.That(fixture.RecordBody("owner-work-hold", "intentional").Contains("\"revision\":2", StringComparison.Ordinal), scenario + "_original_hold_revision_retained");
            checks.That(fixture.ReadRows().SequenceEqual(fixture.Baseline), scenario + "_all_fourteen_triplets_retained");
            checks.That(fixture.DiagnosticCount() == 3, scenario + "_no_retention_prune");
            if (boundary is not null)
                checks.That(boundary.StopCalls > 0, scenario + "_partial_host_start_stopped");
            var second = await recovery.ExecuteAsync(Command("engine.work-hold.resume", CerebralJson.Element(ResumeArguments()))).ConfigureAwait(false);
            checks.That(!second.Success && !recovery.Fence.IsOpen, scenario + "_no_second_ticket_or_replay");
        }
        return checks.Count;
    }

    private static async Task ExpectCancellationAsync(Task task, Checks checks, string label)
    {
        try { await task.WaitAsync(Limit).ConfigureAwait(false); throw new InvalidOperationException("fixture_missing_cancellation:" + label); }
        catch (OperationCanceledException) { checks.Count++; }
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(10, timeout.Token).ConfigureAwait(false);
    }
    private static object ResumeArguments() => new { holdId = HoldId, expectedEpoch = 5L, expectedGeneration = 1L, expectedRevision = 2L };
    private static CommandRequest Command(string operation, JsonElement? arguments = null) => new(operation, arguments ?? CerebralJson.Element(new { }));
    private sealed class Checks
    {
        internal int Count;
        internal void That(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("source_recovery_runtime_fixture_failed:" + label); Count++; }
    }

    private sealed class StartBoundary : IHostedService
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailStart;
        internal int StopCalls;
        public async Task StartAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(token).ConfigureAwait(false);
            if (FailStart) throw new InvalidOperationException("private_fixture_start_failure");
        }
        public Task StopAsync(CancellationToken token) { Interlocked.Increment(ref StopCalls); return Task.CompletedTask; }
    }
    private sealed class RefusingExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("private_fixture_unexpected_legacy_execution");
    }
    private sealed class RefusingPeers : IResourcePausePeerProbe
    {
        public Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers, CancellationToken token) =>
            throw new InvalidOperationException("private_fixture_unexpected_peer_probe");
    }

    private sealed class Composition : IAsyncDisposable
    {
        internal required StateStore Store;
        internal required EngineController Engine;
        internal required ModelRuntime Models;
        internal required ToolDispatcher Tools;
        internal required WorkerWorkflows Workflows;
        internal required JobScheduler Scheduler;
        internal required RetentionCoordinator Retention;
        internal required CommandDispatcher Commands;
        internal required IHost Host;

        internal static async Task<Composition> CreateAsync(Fixture fixture, SourceRecoveryStartup? recovery = null,
            LocalRunControl? local = null, StartBoundary? boundary = null)
        {
            var store = new StateStore(fixture.Files.Root, deferDiagnosticPruning: true);
            var configuration = new Configuration(store);
            if (recovery is not null) configuration.AttachSourceRecoverySnapshot(recovery.ReadDetachedSettingsWhileClosed);
            var events = new EventHub();
            var engine = new EngineController(fixture.Files.RoleRoot("engine"), Path.Combine(fixture.Files.Root, "Engine"));
            await engine.InitializeAsync().ConfigureAwait(false);
            var models = new ModelRuntime(store, configuration.Get, events);
            var tools = new ToolDispatcher(configuration.Get);
            var journal = new EventJournal(fixture.Files.EnginePath);
            // No tools are injected into workflows, so NativeContinuity is absent and cannot contact Cortex.
            var workflows = new WorkerWorkflows(store, configuration, events, [], [], journal: journal, engine: engine);
            var scheduler = new JobScheduler(store, configuration, new RefusingExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, workflows, resourcePeers: new RefusingPeers());
            var retention = new RetentionCoordinator(journal, store, configuration, events, interval: TimeSpan.FromHours(1));
            var commands = new CommandDispatcher(store, configuration, models, scheduler, tools, events,
                localRun: local, workflows: workflows);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(engine);
            builder.Services.AddSingleton(scheduler);
            builder.Services.AddSingleton(retention);
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionCoordinator>());
            if (boundary is not null) builder.Services.AddSingleton<IHostedService>(boundary);
            return new() { Store = store, Engine = engine, Models = models, Tools = tools, Workflows = workflows,
                Scheduler = scheduler, Retention = retention, Commands = commands, Host = builder.Build() };
        }
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(Limit);
            try { await Host.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally
            {
                if (Host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
                else Host.Dispose();
                await Models.DisposeAsync().ConfigureAwait(false);
                await Tools.DisposeAsync().ConfigureAwait(false);
                await Engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class Fixture
    {
        internal SourceRecoveryPreflightTests.Fixture Files { get; }
        internal string[] Baseline { get; }
        private readonly StateStore seed;
        internal Fixture(string privateRoot, string strategy)
        {
            Files = new SourceRecoveryPreflightTests.Fixture(privateRoot);
            var slot = Path.Combine(Files.RoleRoot("engine"), "A");
            File.Copy(strategy, Path.Combine(slot, "engine.dll"), true);
            var config = Encoding.UTF8.GetBytes("[settings]\nmode=cpu-fixture-only\n");
            File.WriteAllBytes(Path.Combine(slot, "engine-settings.ini"), config);
            var engineIni = "[engine]\nid=fixture.source-recovery.cpu\nversion=1.0.0\nprotocol=pegasus.cerebral.engine/1\nassembly=engine.dll\nassembly-sha256=" +
                Digest(File.ReadAllBytes(strategy)) + "\nentry-type=PrivateSourceRecoveryCpuFixture.Strategy\nconfiguration-sha256=" + Digest(config) + "\n";
            File.WriteAllText(Path.Combine(slot, "engine.ini"), engineIni, new UTF8Encoding(false));
            var package = Digest(Encoding.UTF8.GetBytes(engineIni));
            var configuration = Digest(config);
            var selection = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema"] = "1", ["epoch"] = "5", ["slot"] = "A", ["package"] = package,
                ["configuration"] = configuration, ["known-slot"] = "A", ["known-package"] = package,
                ["known-configuration"] = configuration, ["pending"] = "False", ["paused"] = "True"
            };
            File.WriteAllText(Path.Combine(Files.Root, "Engine", "selection.ini"), "[selection]\n" +
                string.Join("\n", selection.Select(pair => pair.Key + "=" + pair.Value)) + "\n", new UTF8Encoding(false));
            seed = new StateStore(Files.Root, deferDiagnosticPruning: true);
            ResourcePausePersistence.SaveOfflinePause(seed, ["retained-owner-model-id"], rerouteEligibleWork: false,
                detail: "Private CPU fixture: preserve local admission closure and resident-ID intent.");
            var normalized = new Configuration(seed).Get();
            var normalizedJson = JsonSerializer.SerializeToNode(normalized, CerebralJson.Options)!.AsObject();
            Files.ChangeSettings(value =>
            {
                value.Clear(); foreach (var pair in normalizedJson) value.Add(pair.Key, pair.Value?.DeepClone());
            }, repin: true);
            // Preserve the independent golden preflight rows; the runtime database has complete schema and typed rows.
            File.Move(Files.EnginePath, Path.Combine(Files.Root, "preflight-golden-only.db"));
            _ = new EventJournal(Files.EnginePath);
            Files.Sql(Files.EnginePath, "PRAGMA user_version=7");
            for (var index = 0; index < 14; index++) InsertRun(Run(index));
            Baseline = ReadRows();
            Files.ChangeBaseline(value => value["storageRowFingerprints"] = new JsonArray(Baseline.Select((hash, index) =>
                (JsonNode)new JsonObject { ["id"] = $"fixture-{index:00}", ["status"] = "Waiting", ["revision"] = index + 1, ["sha256"] = hash }).ToArray()));
            var review = Path.Combine(Files.Root, "cpu-fixture-initializer-review.txt");
            File.WriteAllText(review, "Private test-only CPU strategy stores its host, validates its explicit fixture config, performs no effect/provider IO, and refuses evaluation. Service-role files are inert fixture pins and are never loaded.", new UTF8Encoding(false));
            Files.ChangeManifest(value =>
            {
                value["settingsRevision"] = normalized.Revision;
                value["selectedPackageSha256"] = package;
                value["selectedConfigurationSha256"] = configuration;
                value["selectionSha256"] = Digest(File.ReadAllBytes(Path.Combine(Files.Root, "Engine", "selection.ini")));
                var pins = new JsonArray();
                foreach (var role in Roles)
                    foreach (var file in Directory.GetFiles(Files.RoleRoot(role), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                        pins.Add(new JsonObject { ["role"] = role, ["relativePath"] = Path.GetRelativePath(Files.RoleRoot(role), file).Replace('\\', '/'),
                            ["length"] = new FileInfo(file).Length, ["sha256"] = Digest(File.ReadAllBytes(file)) });
                value["files"] = pins;
                value["passiveInitializerReview"] = new JsonObject { ["state"] = "reviewed", ["evidencePath"] = review,
                    ["evidenceSha256"] = Digest(File.ReadAllBytes(review)), ["packagePins"] = new JsonArray(pins.Where(pin =>
                        pin!["role"]!.GetValue<string>() is "engine" or "service").Select(pin => pin!.DeepClone()).ToArray()) };
            });
        }
        internal WorkflowRun Run(int index)
        {
            var profile = new AgentProfile { Id = "profile-fixture", Name = "CPU fixture profile", Role = "lead",
                ModelWorkerId = "local", ModelId = "unused-fixture-model", Capabilities = [], AllowedTools = [], AllowedRoots = [] };
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", null,
                [], [], [], [], false, "fixture-no-routing", [], []);
            var submission = new WorkflowSubmission($"request-{index:00}", $"conversation-{index:00}", profile.Id,
                "fixture.waiting", "1.0.0", "Private retained waiting fixture", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot));
            return new() { Id = $"fixture-{index:00}", Submission = submission, Revision = index + 1,
                Status = WorkflowStatus.Waiting, WaitingReason = "Private retained uncertainty; explicit review required.",
                Effect = index < 12 ? new($"effect-{index:00}", "fixture.unobserved", 1, WorkflowContract.EmptyObject,
                    WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown, Error: "fixture preserved unknown receipt") : null,
                CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
        }
        internal void InsertRun(WorkflowRun run)
        {
            using var connection = Files.Open(Files.EnginePath); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO workflow_runs VALUES($id,$request,$conversation,$profile,$status,$revision,$submission,$payload)";
            command.Parameters.AddWithValue("$id", run.Id); command.Parameters.AddWithValue("$request", run.Submission.RequestId);
            command.Parameters.AddWithValue("$conversation", run.Submission.ConversationId); command.Parameters.AddWithValue("$profile", run.Submission.ProfileId);
            command.Parameters.AddWithValue("$status", run.Status.ToString()); command.Parameters.AddWithValue("$revision", run.Revision);
            command.Parameters.AddWithValue("$submission", JsonSerializer.Serialize(run.Submission, CerebralJson.Options));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(run, CerebralJson.Options)); command.ExecuteNonQuery();
        }
        internal string[] ReadRows(bool excludeSentinel = false)
        {
            using var connection = Files.Open(Files.EnginePath); using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM workflow_runs" + (excludeSentinel ? " WHERE id!='running-entry-sentinel'" : "") + " ORDER BY id";
            using var reader = command.ExecuteReader(); var result = new List<string>();
            while (reader.Read()) result.Add(Digest(SourceRecoveryPreflight.EncodePythonTriplets(Columns,
                Enumerable.Range(0, 8).Select(reader.GetValue).ToArray())));
            return result.ToArray();
        }
        internal string RecordBody(string kind, string id)
        {
            using var connection = Files.Open(Files.MainPath); using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM records WHERE kind=$kind AND id=$id";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
            return (string)command.ExecuteScalar()!;
        }
        internal void SeedDiagnostics()
        {
            using var connection = Files.Open(Files.MainPath); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO prompt_cache_diagnostics(recorded_at,model_id,worker_id,adapter_id,body) VALUES(0,'fixture','local','fixture','{}'),(9223372036854775807,'fixture','local','fixture','{}'),($now,'fixture','local','fixture','{}')";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
        }
        internal long DiagnosticCount()
        {
            using var connection = Files.Open(Files.MainPath); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM prompt_cache_diagnostics"; return (long)command.ExecuteScalar()!;
        }
    }
}
