using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;
using static Cerebral.Worker.SourceRecoveryManifest;

namespace Cerebral.Worker.Tests;

public sealed record SourceRecoveryStartupCheckResult(int StateUnitChecks, int RouterChecks, int UserPipeChecks,
    string ReleaseEvidenceTier, bool ProductionStartupExercised, bool OrdinaryRuntimeAttached);

/// <summary>
/// State units use explicitly injected deterministic evidence. Transport fixtures use the real
/// Worker router/server and CerebralClient on a fresh owned user pipe, with no ordinary composition.
/// </summary>
public static class SourceRecoveryStartupTests
{
    public static async Task<SourceRecoveryStartupCheckResult> CheckSourceRecoveryStartupFixturesAsync(string privateRoot)
    {
        var unit = await CheckFenceUnitsAsync().ConfigureAwait(false);
        var router = await CheckRouterAsync(privateRoot).ConfigureAwait(false);
        var pipe = await CheckUserPipeAsync(privateRoot).ConfigureAwait(false);
        return new(unit, router, pipe, "deterministic-state-unit-only", false, false);
    }

    private static async Task<int> CheckFenceUnitsAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { Assert(condition, label); count++; }
        void Refuse(Action action, string label)
        {
            try { action(); throw new InvalidOperationException("fixture_expected_refusal:" + label); }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException ||
                error is InvalidOperationException && !error.Message.StartsWith("fixture_", StringComparison.Ordinal))
            { count++; }
        }

        using (var fence = new SourceRecoveryFence())
        using (var wait = new CancellationTokenSource())
        {
            var release = fence.WaitForReleaseAsync(wait.Token);
            var request = fence.WaitForRequestAsync(wait.Token);
            Check(!fence.IsOpen && fence.State == "inspecting" && !release.IsCompleted && !request.IsCompleted, "initial_closed");
            await Task.Delay(20).ConfigureAwait(false);
            Check(!fence.IsOpen && !release.IsCompleted && !request.IsCompleted, "timer_cannot_open");
            wait.Cancel();
            try { await release.ConfigureAwait(false); throw new InvalidOperationException("fixture_wait_cancel_missing"); }
            catch (OperationCanceledException) { count++; }
            Check(!fence.IsOpen && fence.State == "inspecting", "wait_cancel_not_release");
        }
        using (var fence = new SourceRecoveryFence())
        using (var caller = new CancellationTokenSource())
        {
            caller.Cancel();
            Refuse(() => fence.AcceptResume(caller.Token), "pre_cancelled_no_request");
            Check(fence.State == "inspecting" && !fence.IsOpen, "pre_cancelled_inspecting");
            var ticket = fence.AcceptResume(CancellationToken.None);
            Check(ReferenceEquals(ticket, await fence.WaitForRequestAsync(CancellationToken.None)), "one_ticket_signal");
            Check(fence.State == "composition-requested" && !fence.IsOpen, "accepted_still_closed");
            Refuse(() => fence.AcceptResume(CancellationToken.None), "second_request_refused");
            var foreign = new SourceRecoveryResumeTicket(CancellationToken.None);
            Refuse(() => fence.BeginValidation(foreign), "foreign_validation_refused");
            Refuse(() => fence.Release(foreign, UnitEvidence()), "foreign_release_refused");
            Check(fence.State == "composition-requested" && !fence.IsOpen, "foreign_ticket_no_state_change");
            fence.BeginValidation(ticket);
            Check(fence.State == "validating" && !fence.IsOpen, "validation_still_closed");
            fence.Release(ticket, UnitEvidence());
            Check(fence.IsOpen && fence.State == "open", "deterministic_state_unit_release");
            await fence.WaitForReleaseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            count++;
            fence.Fail(ticket, "private arbitrary fault");
            Check(fence.IsOpen, "late_fail_does_not_revoke_confirmed_release");
            Refuse(() => fence.AcceptResume(CancellationToken.None), "no_second_after_open");
            fence.Dispose();
            Check(!fence.IsOpen && fence.State == "stopped", "disposed_open_closes_admission");
            Refuse(() => fence.BeginValidation(ticket), "disposed_late_validation");
            Refuse(() => fence.Release(ticket, UnitEvidence()), "disposed_late_release");
        }
        foreach (var invalid in InvalidEvidence())
        {
            using var fence = new SourceRecoveryFence();
            var ticket = fence.AcceptResume(CancellationToken.None); fence.BeginValidation(ticket);
            Refuse(() => fence.Release(ticket, invalid), "invalid_unit_evidence");
            Check(!fence.IsOpen && fence.State == "failed" && fence.Failure == "source_recovery_validation_failed", "invalid_evidence_closed");
            Refuse(() => fence.AcceptResume(CancellationToken.None), "no_retry_failed");
            Refuse(() => fence.Release(ticket, UnitEvidence()), "valid_late_evidence_not_reopen");
        }
        using (var fence = new SourceRecoveryFence())
        using (var caller = new CancellationTokenSource())
        {
            var ticket = fence.AcceptResume(caller.Token); fence.BeginValidation(ticket); caller.Cancel();
            Check(fence.State == "failed" && !fence.IsOpen && fence.Failure == "source_recovery_resume_cancelled", "accepted_cancel_failed");
            Refuse(() => fence.Release(ticket, UnitEvidence()), "cancelled_release_refused");
            Refuse(() => fence.AcceptResume(CancellationToken.None), "cancelled_no_second");
            await Task.Delay(20).ConfigureAwait(false);
            Check(!fence.IsOpen, "cancelled_timer_closed");
        }
        using (var fence = new SourceRecoveryFence())
        {
            var ticket = fence.AcceptResume(CancellationToken.None);
            fence.Fail(ticket, "private diagnostic must not be retained");
            Check(fence.Failure == "source_recovery_validation_failed", "failure_allowlist");
            fence.Dispose(); fence.Fail(ticket, "source_recovery_initialization_failed");
            Check(fence.State == "stopped" && !fence.IsOpen, "late_cleanup_stopped");
        }
        return count;
    }

    // These booleans are injected solely to exercise the process state machine; no Engine is constructed.
    private static SourceRecoveryReleaseEvidence UnitEvidence() =>
        new(HoldId, 1, 4, 5, "resumed", false, false, true, false, 0, 0, 0, true, true, true);
    private static IEnumerable<SourceRecoveryReleaseEvidence> InvalidEvidence()
    {
        var value = UnitEvidence();
        return
        [
            value with { HoldId = Guid.NewGuid().ToString("N") }, value with { Generation = 2 },
            value with { Revision = 2 }, value with { EngineEpoch = 6 }, value with { HoldState = "holding" },
            value with { EnginePaused = true }, value with { OriginalPriorEnginePaused = true },
            value with { EngineHealthy = false }, value with { EnginePending = true },
            value with { EngineEvaluations = 1 }, value with { UnreleasedContexts = 1 },
            value with { CapturedWorkCount = 1 }, value with { BaselineRowsMatched = false },
            value with { PassiveInitializersVerified = false }, value with { HostStarted = false }
        ];
    }

    private static async Task<int> CheckRouterAsync(string privateRoot)
    {
        var count = 0;
        void Check(bool condition, string label) { Assert(condition, label); count++; }
        var fixture = Fresh(privateRoot, reviewed: false);
        var manifest = fixture.Parse();
        var preflight = SourceRecoveryPreflight.Verify(manifest, CancellationToken.None);
        var before = fixture.DataDigests();
        await using var startup = new SourceRecoveryStartup(manifest, preflight, new(true));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = startup.WaitForOwnerResumeAsync(wait.Token);
        var settings = startup.ReadDetachedSettingsWhileClosed();
        settings!.Name = "private detached mutation";
        Check(startup.ReadDetachedSettingsWhileClosed()!.Name == preflight.Settings.Name, "settings_detached_each_read");
        Check(!request.IsCompleted && startup.Fence.State == "inspecting", "constructor_no_resume_signal");
        var health = await startup.ExecuteAsync(Command("health"));
        Check(health.Success && health.Data.GetProperty("installationId").GetString() == manifest.InstallationId, "health_owner_identity");
        Check(!health.Data.GetRawText().Contains(SourceRecoveryPreflightTests.Fixture.Secret, StringComparison.Ordinal), "health_no_private_settings");
        var status = await startup.ExecuteAsync(Command("source.recovery.status"));
        Check(status.Success && !status.Data.GetProperty("admissionOpen").GetBoolean() &&
            status.Data.GetProperty("runtimeValidation").GetString() == "deferred", "status_runtime_deferred");
        var engine = await startup.ExecuteAsync(Command("engine.status"));
        Check(engine.Success && engine.Data.GetProperty("active").ValueKind == JsonValueKind.Null &&
            engine.Data.GetProperty("validation").GetString()!.Contains("unobserved", StringComparison.Ordinal), "engine_not_healthy_claim");
        var held = await startup.ExecuteAsync(Command("engine.work-hold.status"));
        Check(held.Success && held.Data.GetProperty("state").GetString() == "held" &&
            held.Data.GetProperty("revision").GetInt64() == 2 && held.Data.GetProperty("work").GetArrayLength() == 0, "exact_held_status");
        foreach (var operation in new[] { "health", "engine.status", "source.recovery.status", "engine.work-hold.status" })
        {
            var response = await startup.ExecuteAsync(Command(operation, profile: "lead"));
            Check(!response.Success && response.Error == "source_recovery_owner_required", "profile_refused");
            response = await startup.ExecuteAsync(Command(operation, Json("{\"extra\":true}")));
            Check(!response.Success && response.Error == "source_recovery_command_invalid", "inspection_empty_args_only");
        }
        foreach (var arguments in InvalidResumeArguments())
        {
            var response = await startup.ExecuteAsync(Command("engine.work-hold.resume", arguments));
            Check(!response.Success && response.Error == "source_recovery_command_invalid", "resume_exact_arguments");
        }
        var reviewDenied = await startup.ExecuteAsync(Command("engine.work-hold.resume", ResumeArguments()));
        Check(!reviewDenied.Success && reviewDenied.Error == "source_recovery_initializer_review_required", "review_required_before_signal");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var response = await startup.ExecuteAsync(Command("engine.work-hold.resume", ResumeArguments()), cancelled.Token);
            Check(!response.Success && response.Error == "source_recovery_cancelled", "pre_cancelled_router");
        }
        foreach (var operation in ForbiddenOperations)
        {
            var response = await startup.ExecuteAsync(Command(operation));
            Check(!response.Success && response.Error == "source_recovery_held", "forbidden_direct_router");
        }
        Check(before.SequenceEqual(fixture.DataDigests()) && !request.IsCompleted && !startup.Fence.IsOpen,
            "inspection_no_db_change_no_resume_signal");
        fixture.ChangeHold(x => { x["state"] = "resumed"; x["revision"] = 4; }, repin: false);
        var changed = await startup.ExecuteAsync(Command("engine.work-hold.status"));
        Check(!changed.Success && changed.Error == "hold_mismatch" && !startup.Fence.IsOpen && !request.IsCompleted,
            "marker_edit_cannot_open_or_report_fresh_held");

        foreach (var change in new Action<SourceRecoveryPreflightTests.Fixture>[]
        {
            x => x.Sql(x.EnginePath, "UPDATE workflow_runs SET payload='private drift' WHERE id='fixture-00'"),
            x => File.AppendAllText(Path.Combine(x.RoleRoot("runtime"), "llama-server.exe"), " drift"),
            x => x.ChangeHold(h => h["revision"] = 3, repin: false),
            x => File.AppendAllText(x.ManifestPath, " ")
        })
        {
            var current = Fresh(privateRoot, reviewed: true);
            var currentManifest = current.Parse();
            var currentPreflight = SourceRecoveryPreflight.Verify(currentManifest, CancellationToken.None);
            await using var router = new SourceRecoveryStartup(currentManifest, currentPreflight, new(true));
            using var signalLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var signal = router.WaitForOwnerResumeAsync(signalLimit.Token);
            change(current);
            var response = await router.ExecuteAsync(Command("engine.work-hold.resume", ResumeArguments()));
            Check(!response.Success && !signal.IsCompleted && !router.Fence.IsOpen && router.Fence.State == "inspecting",
                "drift_refused_before_signal");
        }

        var timerFixture = Fresh(privateRoot, reviewed: false);
        var timerManifest = timerFixture.Parse(); var stopped = 0; var local = new LocalRunControl(true) { Stop = () => Interlocked.Increment(ref stopped) };
        var timerStartup = new SourceRecoveryStartup(timerManifest, SourceRecoveryPreflight.Verify(timerManifest, CancellationToken.None), local);
        var stop = await timerStartup.ExecuteAsync(Command("service.stop-local"));
        Check(stop.Success && !timerStartup.Fence.IsOpen, "stop_reply_not_open");
        await timerStartup.DisposeAsync();
        await Task.Delay(350).ConfigureAwait(false);
        Check(stopped == 0 && timerStartup.Fence.State == "stopped" && !timerStartup.Fence.IsOpen, "late_timer_after_dispose_inert");
        var afterDispose = await timerStartup.ExecuteAsync(Command("health"));
        Check(!afterDispose.Success && afterDispose.Error == "source_recovery_stopped", "disposed_router_refused");

        var signalFixture = Fresh(privateRoot, reviewed: true); var signalManifest = signalFixture.Parse();
        await using (var signalStartup = new SourceRecoveryStartup(signalManifest,
            SourceRecoveryPreflight.Verify(signalManifest, CancellationToken.None), new(true)))
        using (var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var reply = signalStartup.ExecuteAsync(Command("engine.work-hold.resume", ResumeArguments()), caller.Token);
            var ticket = await signalStartup.WaitForOwnerResumeAsync(caller.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!reply.IsCompleted && !signalStartup.Fence.IsOpen && signalStartup.Fence.State == "composition-requested", "exact_request_signals_only");
            var second = await signalStartup.ExecuteAsync(Command("engine.work-hold.resume", ResumeArguments()));
            Check(!second.Success && !signalStartup.Fence.IsOpen, "direct_second_request_refused");
            signalStartup.BeginValidation(ticket);
            try { signalStartup.CompleteResume(ticket, UnitEvidence() with { BaselineRowsMatched = false }, new { unitOnly = true }, CancellationToken.None); }
            catch (InvalidOperationException) { count++; }
            Check(signalStartup.Fence.State == "failed" && !signalStartup.Fence.IsOpen, "invalid_completion_not_open");
            signalStartup.FailResume(ticket, "source_recovery_validation_failed");
            var failed = await reply.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!failed.Success && failed.Error == "source_recovery_resume_unconfirmed", "validation_failure_settled");
        }
        return count;
    }

    private static async Task<int> CheckUserPipeAsync(string privateRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned user pipe fixtures require Windows.");
        var count = 0;
        void Check(bool condition, string label) { Assert(condition, label); count++; }
        var fixture = Fresh(privateRoot, reviewed: false);
        var manifest = fixture.Parse();
        var before = fixture.DataDigests();
        var stopped = 0; var local = new LocalRunControl(true) { Stop = () => Interlocked.Increment(ref stopped) };
        await using (var startup = new SourceRecoveryStartup(manifest, SourceRecoveryPreflight.Verify(manifest, CancellationToken.None), local))
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var endpoint = new LocalWorkerEndpoint(manifest.PipeName, manifest.PipeInstanceId);
            try { await startup.StartPipeAsync(new(endpoint.PipeName, Guid.NewGuid().ToString("N")), new EventHub(), limit.Token);
                throw new InvalidOperationException("fixture_endpoint_refusal_missing"); }
            catch (InvalidOperationException error) when (error.Message == "source_recovery_pipe_identity_changed") { count++; }
            await startup.StartPipeAsync(endpoint, new EventHub(), limit.Token);
            using var client = new CerebralClient(endpoint);
            Check(client.IsLocalPipe, "real_client_is_pipe");
            var health = await client.GetAsync<HostInfo>("health", cancellationToken: limit.Token);
            Check(health.InstallationId == manifest.InstallationId && health.DataDirectory == fixture.Root && health.LocalRun,
                "real_pipe_health_identity");
            var recovery = await client.CommandAsync("source.recovery.status", cancellationToken: limit.Token);
            Check(recovery.Success && recovery.Data.GetProperty("phase").GetString() == "inspecting" &&
                !recovery.Data.GetProperty("admissionOpen").GetBoolean() && recovery.Data.GetProperty("runtimeValidation").GetString() == "deferred",
                "real_pipe_recovery_deferred");
            var engine = await client.CommandAsync("engine.status", cancellationToken: limit.Token);
            Check(engine.Success && engine.Data.GetProperty("active").ValueKind == JsonValueKind.Null &&
                engine.Data.GetProperty("slots").GetArrayLength() == 0 && engine.Data.GetProperty("validation").GetString()!.Contains("unobserved", StringComparison.Ordinal),
                "real_pipe_no_engine_ready_claim");
            var hold = await client.CommandAsync("engine.work-hold.status", cancellationToken: limit.Token);
            Check(hold.Success && hold.Data.GetProperty("holdId").GetString() == HoldId &&
                hold.Data.GetProperty("requestId").GetString() == RequestId && hold.Data.GetProperty("generation").GetInt64() == 1 &&
                hold.Data.GetProperty("revision").GetInt64() == 2 && hold.Data.GetProperty("engineEpoch").GetInt64() == 5 &&
                hold.Data.GetProperty("work").GetArrayLength() == 0, "real_pipe_exact_empty_hold");
            foreach (var operation in ForbiddenOperations)
            {
                var denied = await client.CommandAsync(operation, cancellationToken: limit.Token);
                Check(!denied.Success && denied.Error == "source_recovery_held", "real_pipe_forbidden");
            }
            var owner = await client.CommandAsync("health", profileId: "lead", cancellationToken: limit.Token);
            Check(!owner.Success && owner.Error == "source_recovery_owner_required", "real_pipe_profile_refused");
            var extra = await client.CommandAsync("health", new { extra = true }, cancellationToken: limit.Token);
            Check(!extra.Success && extra.Error == "source_recovery_command_invalid", "real_pipe_empty_args");
            foreach (var malformed in new[] { Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5.5,\"expectedGeneration\":1,\"expectedRevision\":2}"),
                Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":9223372036854775808,\"expectedGeneration\":1,\"expectedRevision\":2}") })
            {
                var denied = await client.CommandAsync("engine.work-hold.resume", malformed, cancellationToken: limit.Token);
                Check(!denied.Success && denied.Error == "source_recovery_command_invalid", "real_pipe_numeric_format_refusal");
            }
            var review = await client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: limit.Token);
            Check(!review.Success && review.Error == "source_recovery_initializer_review_required", "real_pipe_review_gate");
            Check(before.SequenceEqual(fixture.DataDigests()) && !startup.Fence.IsOpen && startup.Fence.State == "inspecting",
                "real_pipe_denials_no_effects_no_signal");
            var stop = await client.CommandAsync("service.stop-local", cancellationToken: limit.Token);
            Check(stop.Success && !startup.Fence.IsOpen, "real_pipe_stop_reply");
            await WaitUntilAsync(() => startup.Shutdown.IsCancellationRequested && Volatile.Read(ref stopped) == 1, TimeSpan.FromSeconds(2));
            count++;
            var stoppedReply = await client.CommandAsync("health", cancellationToken: limit.Token);
            Check(!stoppedReply.Success && stoppedReply.Error == "source_recovery_stopped", "real_pipe_shutdown_refused");
        }

        foreach (var action in new Action<SourceRecoveryPreflightTests.Fixture>[]
        {
            x => x.Sql(x.EnginePath, "UPDATE workflow_runs SET revision=revision+1 WHERE id='fixture-01'"),
            x => File.AppendAllText(Path.Combine(x.RoleRoot("worker"), "Cerebral.Worker.dll"), " changed")
        })
        {
            var drift = Fresh(privateRoot, reviewed: true); var driftManifest = drift.Parse();
            await using var startup = new SourceRecoveryStartup(driftManifest,
                SourceRecoveryPreflight.Verify(driftManifest, CancellationToken.None), new(true));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await startup.StartPipeAsync(new(driftManifest.PipeName, driftManifest.PipeInstanceId), new EventHub(), limit.Token);
            using var client = new CerebralClient(new LocalWorkerEndpoint(driftManifest.PipeName, driftManifest.PipeInstanceId));
            var signal = startup.WaitForOwnerResumeAsync(limit.Token); action(drift);
            var denied = await client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: limit.Token);
            Check(!denied.Success && !signal.IsCompleted && !startup.Fence.IsOpen, "real_pipe_drift_before_signal");
        }

        var cancellationFixture = Fresh(privateRoot, reviewed: true); var cancellationManifest = cancellationFixture.Parse();
        var cancellationBefore = cancellationFixture.DataDigests();
        await using (var startup = new SourceRecoveryStartup(cancellationManifest,
            SourceRecoveryPreflight.Verify(cancellationManifest, CancellationToken.None), new(true)))
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var caller = new CancellationTokenSource())
        {
            var endpoint = new LocalWorkerEndpoint(cancellationManifest.PipeName, cancellationManifest.PipeInstanceId);
            await startup.StartPipeAsync(endpoint, new EventHub(), limit.Token); using var client = new CerebralClient(endpoint);
            var reply = client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: caller.Token);
            var ticket = await startup.WaitForOwnerResumeAsync(limit.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Check(startup.Fence.State == "composition-requested" && !startup.Fence.IsOpen && !reply.IsCompleted, "real_pipe_exact_request_still_closed");
            caller.Cancel();
            try { await reply.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("fixture_client_cancel_missing"); }
            catch (LocalWorkerCommandUncertainException error)
            {
                Check(error.Operation == "engine.work-hold.resume" && caller.IsCancellationRequested &&
                    error.Message.Contains("not replayed", StringComparison.Ordinal), "real_pipe_cancel_uncertain_no_replay");
            }
            await WaitUntilAsync(() => startup.Fence.State == "failed", TimeSpan.FromSeconds(2));
            Check(!startup.Fence.IsOpen && startup.Fence.Failure == "source_recovery_resume_cancelled", "real_pipe_disconnect_fails_request");
            var second = await client.CommandAsync("engine.work-hold.resume", ResumeArguments(), cancellationToken: limit.Token);
            Check(!second.Success && ReferenceEquals(ticket, await startup.WaitForOwnerResumeAsync(limit.Token)), "real_pipe_no_second_ticket");
            Check(cancellationBefore.SequenceEqual(cancellationFixture.DataDigests()), "real_pipe_cancel_no_marker_write");
            await Task.Delay(30).ConfigureAwait(false);
            Check(!startup.Fence.IsOpen, "real_pipe_cancel_no_timer_open");
        }
        return count;
    }

    private static readonly string[] ForbiddenOperations =
    [
        "models.list", "models.load", "models.unload", "models.binding.snapshot", "models.snapshot",
        "settings.get", "settings.save", "plugins.list", "service.plugins.list", "service.plugins.prepare",
        "engine.work-hold.begin", "engine.work-hold.cancel", "engine.resume", "jobs.submit", "workflow.submit", "unknown.operation"
    ];
    private static IEnumerable<JsonElement> InvalidResumeArguments()
    {
        yield return Json("{}"); yield return Json("null"); yield return Json("[]");
        yield return Json("{\"holdId\":\"different\",\"expectedEpoch\":5,\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":6,\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5.5,\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":9223372036854775808,\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":\"5\",\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":null,\"expectedEpoch\":5,\"expectedGeneration\":1,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5,\"expectedGeneration\":2,\"expectedRevision\":2}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5,\"expectedGeneration\":1,\"expectedRevision\":4}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5,\"expectedGeneration\":1,\"expectedRevision\":2,\"extra\":true}");
        yield return Json("{\"holdId\":\"297e00ce8fb04042a1bdb4a16eda22a4\",\"expectedEpoch\":5,\"expectedEpoch\":5,\"expectedGeneration\":1,\"expectedRevision\":2}");
    }
    private static JsonElement ResumeArguments() => CerebralJson.Element(new { holdId = HoldId, expectedEpoch = 5L, expectedGeneration = 1L, expectedRevision = 2L });
    private static CommandRequest Command(string operation, JsonElement? arguments = null, string? profile = null) =>
        new(operation, arguments ?? Json("{}"), profile);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static SourceRecoveryPreflightTests.Fixture Fresh(string privateRoot, bool reviewed)
    {
        var fixture = new SourceRecoveryPreflightTests.Fixture(CanonicalPath(privateRoot));
        if (reviewed)
        {
            var evidence = Path.Combine(fixture.Root, "passive-review.txt");
            File.WriteAllText(evidence, "private fixture provenance; no initializer executed", new UTF8Encoding(false));
            fixture.ChangeManifest(x => x["passiveInitializerReview"] = new JsonObject
            {
                ["state"] = "reviewed", ["evidencePath"] = evidence, ["evidenceSha256"] = Digest(File.ReadAllBytes(evidence)),
                ["packagePins"] = new JsonArray(((JsonArray)x["files"]!).Where(y =>
                    y!["role"]!.GetValue<string>() is "engine" or "service").Select(y => y!.DeepClone()).ToArray())
            });
        }
        return fixture;
    }
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan maximum)
    {
        using var deadline = new CancellationTokenSource(maximum);
        while (!condition()) await Task.Delay(10, deadline.Token).ConfigureAwait(false);
    }
    private static void Assert(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("source_recovery_startup_fixture_failed:" + label); }
}
