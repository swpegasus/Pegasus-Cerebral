using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker;

/// <summary>
/// The sole recovery resolution path. It uses the real scheduler hold resolver while all
/// ordinary hosted services and command admission remain behind the process-owned barrier.
/// </summary>
internal static class SourceRecoveryRuntime
{
    internal static async Task RunAsync(IHost host, SourceRecoveryStartup recovery,
        SourceRecoveryResumeTicket ticket, CommandDispatcher commands, EngineController engine,
        JobScheduler scheduler, RetentionCoordinator retention, ModelRuntime models,
        CancellationToken token)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var stopping = lifetime.ApplicationStopping.Register(recovery.RequestShutdown);
        using var validation = CancellationTokenSource.CreateLinkedTokenSource(token, ticket.Caller,
            recovery.Shutdown, lifetime.ApplicationStopping);
        validation.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = validation.Token;
        var started = false;
        var startAttempted = false;
        using var abortValidation = ct.Register(() => recovery.FailResume(ticket, "source_recovery_resume_cancelled"));
        try
        {
            scheduler.AttachSourceRecoveryFence(recovery.Fence);
            retention.AttachSourceRecoveryFence(recovery.Fence);
            // The ordinary pause transition starts asynchronous drain work and writes model
            // state; reroute=true also probes peers. Recovery requires the owner-staged pause.
            _ = SourceRecoveryPreflight.VerifyLocalResourcePause(recovery.Manifest, ct);
            var resident = await models.BeginPauseAsync(ct).ConfigureAwait(false);
            if (resident.Count != 0)
                throw new InvalidOperationException("source_recovery_local_models_observed");
            VerifyModelPause(models);
            recovery.AttachRuntime(commands, engine);
            VerifyHeld(recovery, engine, scheduler, ct);
            startAttempted = true;
            await host.StartAsync(ct).ConfigureAwait(false);
            started = true;
            VerifyHeld(recovery, engine, scheduler, ct);
            ct.ThrowIfCancellationRequested();

            // One exact owner request. No generic dispatcher replay, implicit cancellation,
            // interrupted-row resume, or fallback generation is admitted by this path.
            var resumed = scheduler.ResolveIntentionalWorkHold(SourceRecoveryManifest.HoldId, 5, 1, 2, false);
            ct.ThrowIfCancellationRequested();
            var saved = SourceRecoveryPreflight.VerifyPersistedState(recovery.Manifest,
                SourceRecoveryExpectedState.Resumed, ct);
            var rows = SourceRecoveryPreflight.VerifyRows(recovery.Manifest, ct);
            SourceRecoveryPreflight.VerifyPinnedFiles(recovery.Manifest, ct);
            _ = SourceRecoveryPreflight.VerifyLocalResourcePause(recovery.Manifest, ct);
            var observed = engine.Status();
            var final = scheduler.GetIntentionalWorkHoldStatus();
            RequireHold(resumed, resumed: true);
            RequireHold(final, resumed: true);
            RequireEngine(recovery.Manifest, observed, paused: false);
            VerifyModelPause(models);
            ct.ThrowIfCancellationRequested();
            recovery.CompleteResume(ticket, new(final.HoldId!, final.Generation, final.Revision,
                observed.Epoch, final.State, observed.Paused, false, observed.Active is not null &&
                observed.Error is null && observed.Recovery is null, observed.CandidatePending,
                observed.ActiveEvaluations, observed.UnreleasedContexts, final.Work.Count,
                rows.BaselineMatched, recovery.Manifest.PassiveInitializerReview.State == "reviewed", started),
                new
                {
                    hold = final, manifestSha256 = recovery.Manifest.ManifestSha256,
                    installationId = recovery.Manifest.InstallationId,
                    originalPriorEnginePaused = false,
                    localModelAdmission = "paused; preserved existing durable local resource policy",
                    baselineRowsMatched = rows.BaselineMatched,
                    savedCoordinates = saved.Coordinates,
                    observedSourceManagedModelInstances = 0,
                    remoteModelOwnership = "unobserved by this local recovery path"
                }, ct);
            abortValidation.Dispose();
            validation.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // The owner may have lost a reply after the durable resolver wrote. Keep admission
            // closed and expose an uncertain outcome; a restart requires a newly reviewed contract.
            recovery.FailResume(ticket, ct.IsCancellationRequested ?
                "source_recovery_resume_cancelled" : "source_recovery_initialization_failed");
            if (startAttempted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await host.StopAsync(cleanup.Token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("source_recovery_resume_unconfirmed");
        }
        // Caller cancellation belongs to this one resolution, not the lifetime of a successfully
        // admitted host. The normal user host now owns shutdown and its persisted resource pause.
        await host.WaitForShutdownAsync(token).ConfigureAwait(false);
    }

    private static void VerifyModelPause(ModelRuntime models)
    {
        var slots = models.ObserveManagedSlots();
        if (slots.State != "observed" || slots.Truncated || slots.ModelAdmissionOpen != false || slots.Models.Count != 0)
            throw new InvalidOperationException("source_recovery_local_model_admission_unconfirmed");
    }

    private static void VerifyHeld(SourceRecoveryStartup recovery, EngineController engine,
        JobScheduler scheduler, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SourceRecoveryPreflight.VerifyPinnedFiles(recovery.Manifest, token);
        _ = SourceRecoveryPreflight.VerifyPersistedState(recovery.Manifest, SourceRecoveryExpectedState.Held, token);
        _ = SourceRecoveryPreflight.VerifyRows(recovery.Manifest, token);
        _ = SourceRecoveryPreflight.VerifyLocalResourcePause(recovery.Manifest, token);
        RequireHold(scheduler.GetIntentionalWorkHoldStatus(), resumed: false);
        RequireEngine(recovery.Manifest, engine.Status(), paused: true);
    }

    private static void RequireHold(IntentionalWorkHoldStatus value, bool resumed)
    {
        if (value.Contract != "cerebral.owner-work-hold/1" ||
            value.HoldId != SourceRecoveryManifest.HoldId || value.RequestId != SourceRecoveryManifest.RequestId ||
            value.Generation != 1 || value.Revision != (resumed ? 4 : 2) || value.EngineEpoch != 5 ||
            value.EnginePaused != !resumed || value.State != (resumed ? "resumed" : "held") ||
            value.Mode != "finish-current" || value.StartupPromptRequired || value.Fault is not null || value.Work.Count != 0)
            throw new InvalidOperationException("source_recovery_runtime_hold_mismatch");
    }

    private static void RequireEngine(SourceRecoveryManifest manifest, EngineStatus value, bool paused)
    {
        if (value.Paused != paused || value.Epoch != 5 || value.ActiveEvaluations != 0 ||
            value.CommittedDecisions != 0 || value.CandidatePending || value.UnreleasedContexts != 0 ||
            value.Error is not null || value.Recovery is not null || value.KnownGoodSlot != "A" ||
            value.Active is not { Slot: "A" } active ||
            active.PackageSha256 != manifest.SelectedPackageSha256 ||
            active.ConfigurationSha256 != manifest.SelectedConfigurationSha256)
            throw new InvalidOperationException("source_recovery_engine_unverified");
    }
}
