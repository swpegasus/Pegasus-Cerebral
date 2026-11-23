using System.Reflection;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

// Source-prepared deterministic fixtures. Real control code is used for early admission,
// sealed accounting, pin disposal, fence and registry retirement. The provider below is
// explicitly fake. There is NO successful native load, tokenizer, HTTP, GPU or physical
// release proof here, and no duplicate loading algorithm.
internal static class NativeReadinessChecks
{
    internal static async Task<string[]> RunAsync(string root)
    {
        var passed = new List<string>();
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("native_readiness_check:" + name); passed.Add(name); }
        await AdmissionFailuresAsync(Path.GetFullPath(root), Check);
        await OwnershipAsync(Check);
        await RetirementAsync(Check);
        return passed.ToArray();
    }

    private static async Task<Exception> FailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected injected or admission failure.");
    }
    private static Exception Failure(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected refusal.");
    }

    private static async Task AdmissionFailuresAsync(string root, Action<bool, string> check)
    {
        var store = new StateStore(Path.Combine(root, "state"));
        var model = new ModelRecord
        {
            Id = "ready-fixture", Path = Path.Combine(root, "deliberately-missing.gguf"),
            ChatTemplate = "{{ fixture }}", StopSequences = ["stop", "", "stop"],
            MaximumContextTokens = 32768, ContextTokens = 32768,
            RecommendedContextTokens = 32768, ParallelRequests = 1, GpuLayers = 0,
            AutomaticContextSizing = true, State = "unloaded"
        };
        store.Put("model", model.Id, model);
        var settingsCalls = 0;
        var runtime = new ModelRuntime(store, () => { settingsCalls++; throw new InvalidOperationException("No provider/settings expected."); }, new EventHub());
        try
        {
            var accepted = RemotePreparedInference.Bind(model);
            var changes = new RemotePreparedModelBinding[]
            {
                accepted with { Id = "changed-model-id" },
                accepted with { Path = accepted.Path + ".changed" },
                accepted with { ChatTemplate = "changed" },
                accepted with { StopSequences = ["", "stop", "stop"] },
                accepted with { MaximumContextTokens = 16384 },
                accepted with { ParallelRequests = 2 },
                accepted with { GpuLayers = 1 },
                accepted with { VisionProjectorPath = Path.Combine(root, "changed-projector.gguf") }
            };
            foreach (var changed in changes)
            {
                var error = await FailureAsync(async () => { await runtime.AcquireReadyRegisteredNativeTextAsync(changed, CancellationToken.None); });
                check(error is IRegisteredNativeReadinessFailure failure &&
                    failure.Observation.Cleanup == RegisteredNativeReadinessCleanup.KnownSettled &&
                    failure.Observation.OwnedAccounting.Count == 0 &&
                    runtime.Accounting.Snapshot().Entries.Length == 0 &&
                    store.Get<ModelRecord>("model", model.Id)!.State == "unloaded",
                    "actual_ready_rejects_changed_complete_definition_before_load_" + Array.IndexOf(changes, changed));
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var cancelled = await FailureAsync(async () => { await runtime.AcquireReadyRegisteredNativeTextAsync(accepted, cancel.Token); });
            check(cancelled is RegisteredNativeReadinessCanceledException cancelledFailure &&
                cancelledFailure.CancellationToken == cancel.Token &&
                cancelledFailure.OriginalException is OperationCanceledException original && original.CancellationToken == cancel.Token &&
                cancelledFailure.Observation.AllowsFreshAcceptedAttempt &&
                runtime.Accounting.Snapshot().Entries.Length == 0, "actual_ready_precancel_preserves_original_token_and_no_effect");

            var first = (IRegisteredNativeReadinessFailure)await FailureAsync(async () =>
                { await runtime.AcquireReadyRegisteredNativeTextAsync(accepted, CancellationToken.None); });
            var second = (IRegisteredNativeReadinessFailure)await FailureAsync(async () =>
                { await runtime.AcquireReadyRegisteredNativeTextAsync(accepted, CancellationToken.None); });
            check(first.OriginalException is FileNotFoundException && second.OriginalException is FileNotFoundException &&
                first.Observation.AllowsFreshAcceptedAttempt && second.Observation.AllowsFreshAcceptedAttempt &&
                first.Observation.AttemptId != second.Observation.AttemptId &&
                first.Observation.OwnedAccounting is [{ Kind: ModelOwnershipKind.LocalLoad, Retired: true }] &&
                settingsCalls == 0 && runtime.Accounting.Snapshot().Entries.Length == 0,
                "actual_shared_loader_missing_file_failure_settles_and_distinct_fresh_attempt_is_not_sticky");

            // Hold the ACTUAL existing gate solely to make the DTO alias boundary deterministic.
            var lifecycle = (SemaphoreSlim)typeof(ModelRuntime).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            var callerStops = accepted.StopSequences.ToList();
            await lifecycle.WaitAsync();
            Task<RegisteredNativeReadinessLease> waiting;
            try
            {
                waiting = runtime.AcquireReadyRegisteredNativeTextAsync(accepted with { StopSequences = callerStops }, CancellationToken.None);
                callerStops[0] = "changed after await"; callerStops.Clear();
            }
            finally { lifecycle.Release(); }
            var frozenError = (IRegisteredNativeReadinessFailure)await FailureAsync(async () => { await waiting; });
            check(frozenError.OriginalException is FileNotFoundException &&
                frozenError.Observation.AllowsFreshAcceptedAttempt && settingsCalls == 0,
                "actual_ready_freezes_ordered_stops_before_waiting_for_real_lifecycle");

            // Inject only a fixture-database UPDATE failure. This executes the real shared
            // loader's failed-state Save path without adding a production test callback.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fixture_ready_save_failure BEFORE UPDATE ON records WHEN NEW.kind='model' AND NEW.id='ready-fixture' BEGIN SELECT RAISE(ABORT,'injected readiness failed-state save'); END;";
                command.ExecuteNonQuery();
                try
                {
                    var saveFailure = (IRegisteredNativeReadinessFailure)await FailureAsync(async () =>
                        { await runtime.AcquireReadyRegisteredNativeTextAsync(accepted, CancellationToken.None); });
                    check(saveFailure.OriginalException is FileNotFoundException &&
                        saveFailure.CleanupException is SqliteException &&
                        saveFailure.Observation.AllowsFreshAcceptedAttempt &&
                        runtime.Accounting.Snapshot().Entries.Length == 0,
                        "actual_shared_loader_failed_state_save_error_preserves_original_and_secondary_failure");
                }
                finally
                {
                    command.CommandText = "DROP TRIGGER fixture_ready_save_failure;";
                    command.ExecuteNonQuery();
                }
            }

            var fingerprint = CommandDispatcher.TargetModelFingerprint.Capture(model);
            var fitted = CerebralJson.Read<ModelRecord>(CerebralJson.Element(model)); fitted.ContextTokens = 8192;
            check(fingerprint.MatchesConfirmedLoad(fitted, fitted) &&
                RemotePreparedInference.Bind(fitted).MaximumContextTokens == 32768,
                "existing_confirmed_load_fingerprint_allows_only_operational_automatic_context_fit");
            var maximumEdit = CerebralJson.Read<ModelRecord>(CerebralJson.Element(fitted)); maximumEdit.MaximumContextTokens = 16384;
            check(!fingerprint.MatchesConfirmedLoad(maximumEdit, maximumEdit),
                "existing_confirmed_load_fingerprint_keeps_maximum_context_strict");
        }
        finally { await runtime.DisposeAsync(); }
    }

    private static async Task OwnershipAsync(Action<bool, string> check)
    {
        var accounting = new ModelExecutionAccounting(coverageComplete: true);
        var foreign = new ModelExecutionAccounting(coverageComplete: true);
        NativeReadinessOwnershipScope snapshot;
        ModelExecutionAccounting.ModelOwnership retained;
        using (snapshot = new(accounting, "owned"))
        {
            var load = accounting.Begin(ModelOwnershipKind.LocalLoad, "owned");
            retained = accounting.Begin(ModelOwnershipKind.FittingProcess, "owned");
            var otherModel = accounting.Begin(ModelOwnershipKind.LocalContext, "other");
            var otherOwner = foreign.Begin(ModelOwnershipKind.LocalContext, "owned");
            load.Complete(); retained.Unknown("injected-fitting-unknown");
            snapshot.Seal();
            check(snapshot.Observe() is { Cleanup: RegisteredNativeReadinessCleanup.RetainedUnknown, IdentityCoverageComplete: true } &&
                snapshot.Observe().OwnedAccounting.Select(value => value.EntryId).ToHashSet().SetEquals([load.Id, retained.Id]),
                "collector_reports_exact_owned_rows_and_excludes_other_owner_and_model");
            otherModel.Complete(); otherOwner.Complete();
            check(!snapshot.Observe().AllowsFreshAcceptedAttempt, "unrelated_retirement_cannot_refund_exact_unknown_fitter");
            retained.Complete();
            check(snapshot.Observe().AllowsFreshAcceptedAttempt, "later_exact_owned_retirement_can_be_observed_without_snapshot_difference");
            var count = accounting.Snapshot().Entries.Length;
            check(Failure(() => accounting.Begin(ModelOwnershipKind.LocalLoad, "owned")) is InvalidOperationException &&
                accounting.Snapshot().Entries.Length == count, "sealed_inherited_scope_refuses_before_accounting_row_insertion");
        }

        using (var saturated = new NativeReadinessOwnershipScope(accounting, "bounded", maximum: 1))
        {
            var first = accounting.Begin(ModelOwnershipKind.LocalLoad, "bounded");
            var second = accounting.Begin(ModelOwnershipKind.FittingProcess, "bounded");
            first.Complete(); second.Complete(); saturated.Seal();
            check(!saturated.Observe().IdentityCoverageComplete &&
                saturated.Observe().Cleanup == RegisteredNativeReadinessCleanup.RetainedUnknown &&
                saturated.Observe().OwnedAccounting.Count == 1, "saturation_remains_unknown_even_when_observed_rows_retire");
        }
        using (var overlapping = new NativeReadinessOwnershipScope(accounting, "overlap"))
        {
            using var ticket = NativeReadinessOwnershipScope.BeforeBegin(accounting, "overlap");
            overlapping.Seal(); ticket!.Dispose();
            check(!overlapping.Observe().IdentityCoverageComplete && !overlapping.Observe().AllowsFreshAcceptedAttempt,
                "seal_overlapping_in_progress_begin_permanently_loses_coverage");
        }

        using (var resident = new NativeReadinessOwnershipScope(accounting, "resident"))
        {
            var context = accounting.Begin(ModelOwnershipKind.LocalContext, "resident");
            context.Phase(ModelOwnershipPhase.Ready, effectStarted: true); resident.ConfirmResident(context); resident.Seal();
            check(resident.Observe().Cleanup == RegisteredNativeReadinessCleanup.KnownResident &&
                resident.Observe().AllowsFreshAcceptedAttempt && !context.IsRetired,
                "known_resident_is_explicit_retained_context_not_a_physical_release_claim");
            context.Unknown("injected-stop-unconfirmed");
            check(!resident.Observe().AllowsFreshAcceptedAttempt, "later_exact_resident_unknown_is_never_a_known_resident_refund");
            context.Complete();
        }
        using (var failures = new NativeReadinessOwnershipScope(accounting, "failure"))
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var primary = new OperationCanceledException(cancellation.Token);
            var cleanup = new IOException("injected cleanup replaces primary at loader boundary");
            NativeReadinessOwnershipScope.CaptureLoaderFailure(accounting, "failure", primary);
            var observed = failures.Wrap(cleanup);
            check(observed is RegisteredNativeReadinessCanceledException typed &&
                ReferenceEquals(typed.OriginalException, primary) && ReferenceEquals(typed.CleanupException, cleanup) &&
                typed.CancellationToken == cancellation.Token && typed.Observation.AllowsFreshAcceptedAttempt,
                "actual_failure_wrapper_preserves_witnessed_original_cancel_and_separate_cleanup_error");
        }

        var blocker = accounting.Begin(ModelOwnershipKind.LocalContext, "blocked"); blocker.Unknown("injected-old-context");
        using (var blocked = new NativeReadinessOwnershipScope(accounting, "blocked"))
        {
            NativeReadinessOwnershipScope.BlockedBy(accounting, blocker.ReadEntry()!); blocked.Seal();
            var failure = (IRegisteredNativeReadinessFailure)blocked.Wrap(new IOException("injected refusal"));
            check(failure.Observation.OwnedAccounting.Count == 0 &&
                failure.Observation.BlockingAccounting is [{ EntryId: var blockedId, Retired: false }] && blockedId == blocker.Id &&
                !failure.Observation.AllowsFreshAcceptedAttempt, "prior_exact_unknown_dependency_is_distinct_from_attempt_owned_rows");
            blocker.Complete();
            check(failure.Observation.AllowsFreshAcceptedAttempt, "only_exact_dependency_retirement_allows_cleanup_refresh");
        }

        var allowLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception> late;
        using (var inherited = new NativeReadinessOwnershipScope(accounting, "late"))
        {
            late = Task.Run(async () =>
            {
                await allowLate.Task;
                return Failure(() => accounting.Begin(ModelOwnershipKind.LocalLoad, "late"));
            });
            inherited.Seal();
        }
        allowLate.TrySetResult();
        check(await late.WaitAsync(TimeSpan.FromSeconds(3)) is InvalidOperationException &&
            accounting.Snapshot().Entries.Length == 0, "inherited_background_context_cannot_create_late_owned_effect_after_seal");

        var fixture = new Composition();
        using (var pinned = new NativeReadinessOwnershipScope(accounting, "pin"))
        {
            var pin = accounting.Begin(ModelOwnershipKind.CapabilityLease, "pin");
            pin.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
            pinned.Captured("fixture.runtime", "fixture.instance", pin.Id); pinned.Seal();
            var calls = 0;
            var lease = new RegisteredNativeReadinessLease(fixture.Service, pinned, () => { Interlocked.Increment(ref calls); pin.Complete(); });
            check(lease.Observe().Cleanup == RegisteredNativeReadinessCleanup.Active, "real_ready_lease_reports_active_temporary_pin");
            await Task.WhenAll(lease.DisposeAsync().AsTask(), lease.DisposeAsync().AsTask());
            check(calls == 1 && lease.Observe().AllowsFreshAcceptedAttempt && fixture.Service.Observe().RegistrationOpen,
                "real_ready_lease_releases_pin_once_and_does_not_dispose_cached_registration");
        }
        using (var release = new NativeReadinessOwnershipScope(accounting, "release-error"))
        {
            var pin = accounting.Begin(ModelOwnershipKind.CapabilityLease, "release-error");
            pin.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
            release.Captured("fixture.runtime", "fixture.instance", pin.Id); release.Seal();
            var original = new IOException("injected pin release reporting failure");
            var calls = 0;
            var lease = new RegisteredNativeReadinessLease(fixture.Service, release, () =>
                { Interlocked.Increment(ref calls); pin.Complete(); throw original; });
            var first = await FailureAsync(async () => await lease.DisposeAsync());
            var second = await FailureAsync(async () => await lease.DisposeAsync());
            check(ReferenceEquals(first, second) && first is IRegisteredNativeReadinessFailure failure &&
                ReferenceEquals(failure.OriginalException, original) && failure.Observation.AllowsFreshAcceptedAttempt && calls == 1,
                "real_ready_lease_retains_same_original_release_failure_after_actual_pin_retirement");
        }
        _ = await fixture.Lifetime.BeginRetirement();
    }

    private static async Task RetirementAsync(Action<bool, string> check)
    {
        var fixture = new Composition();
        var callback = fixture.Fence.Enter();
        var retirement = fixture.Lifetime.BeginRetirement();
        check(!retirement.IsCompleted && !fixture.Service.Observe().RegistrationOpen &&
            fixture.Ownership.IsUnknown && ReferenceEquals(retirement, fixture.Lifetime.BeginRetirement()),
            "real_instance_facet_retirement_closes_admission_returns_pending_without_waiting_callbacks_and_is_single_owner");
        check(Failure(() => fixture.Lifetime.RequireUsable(false)) is InvalidOperationException &&
            Failure(() => fixture.Fence.Enter()) is InvalidOperationException,
            "same_closed_cached_facet_refuses_reuse_without_replacement");
        callback.Dispose();
        check(await retirement.WaitAsync(TimeSpan.FromSeconds(3)) is null && fixture.Ownership.IsRetired &&
            fixture.Service.Observe().Registry.Admission == InstanceAdmission.RegistryRetired &&
            fixture.Service.Observe().Registry.PhysicalStopConfirmed is null,
            "real_fence_and_registry_settle_logically_without_claiming_physical_stop");

        var faulted = new Composition(failDispose: true);
        var adapter = ((IRegisteredNativeWorkflowSource)faulted.Service).CreateWorkflowAdapter();
        var prepared = await adapter.PrepareAsync(new(faulted.Service.Binding.ModelId,
            [new("user", [new TextContentPart("fixture")])], 2048, 128), CancellationToken.None);
        var cleanup = await FailureAsync(async () => await prepared.DisposeAsync());
        check(cleanup is RegisteredWorkflowCleanupException && faulted.Service.Observe().ActiveCallbacks == 0 &&
            faulted.Service.Observe().Registry is { Admission: InstanceAdmission.CleanupFaulted, RegistryReservations: 1 },
            "actual_facade_fake_provider_cleanup_failure_drops_callback_but_retains_registry_owner");
        using (var later = new NativeReadinessOwnershipScope(faulted.Accounting, faulted.Service.Binding.ModelId))
        {
            var load = faulted.Accounting.Begin(ModelOwnershipKind.LocalLoad, faulted.Service.Binding.ModelId);
            var refusal = Failure(() => faulted.Lifetime.RequireUsable(false));
            load.Complete();
            var failed = (IRegisteredNativeReadinessFailure)later.Wrap(refusal);
            check(failed.Observation.OwnedAccounting is [{ EntryId: var loadId, Retired: true }] && loadId == load.Id &&
                failed.Observation.BlockingAccounting is [{ EntryId: var cacheId, Phase: ModelOwnershipPhase.Unknown }] &&
                cacheId == faulted.Ownership.Id && !failed.Observation.AllowsFreshAcceptedAttempt,
                "later_attempt_load_retirement_never_refunds_exact_old_cached_cleanup_fault");
        }
        var failedRetirement = await faulted.Lifetime.BeginRetirement().WaitAsync(TimeSpan.FromSeconds(3));
        check(failedRetirement is not null && faulted.Ownership.IsUnknown && !faulted.Ownership.IsRetired &&
            Failure(() => faulted.Lifetime.RequireUsable(false)) is InvalidOperationException,
            "callback_zero_never_erases_cleanup_faulted_reservation_or_refunds_same_instance");

        var closed = new Composition();
        await closed.Service.DisposeAsync();
        using (var clean = new NativeReadinessOwnershipScope(closed.Accounting, closed.Service.Binding.ModelId))
        {
            var failure = (IRegisteredNativeReadinessFailure)clean.Wrap(Failure(() => closed.Lifetime.RequireUsable(false)));
            check(failure.Observation.AllowsFreshAcceptedAttempt && failure.Observation.OwnedAccounting.Count == 0 &&
                !closed.Ownership.IsRetired && closed.Service.Observe().Registry.Admission == InstanceAdmission.RegistryRetired,
                "clean_closed_pre_effect_refusal_does_not_claim_cached_owner_or_native_process_released");
        }
        _ = await closed.Lifetime.BeginRetirement();
    }

    private sealed class Composition
    {
        internal ModelExecutionAccounting Accounting { get; } = new(coverageComplete: true);
        internal NativeRegistrationFence Fence { get; } = new();
        internal ModelExecutionAccounting.ModelOwnership Ownership { get; }
        internal RegisteredNativeGenerationService Service { get; }
        internal NativeRegistrationLifetime Lifetime { get; }
        internal Composition(bool failDispose = false)
        {
            var accounting = Accounting;
            var binding = new AdapterBinding("fixture.readiness", "fixture.model", Guid.NewGuid().ToString("N"), "fixture.text", "1.0.0");
            var provider = new Provider(binding, failDispose);
            var registry = new AdapterRegistry();
            registry.Register(binding.BackendId, binding.ModelId, binding.InstanceId, provider, Fence, [provider.Descriptor]);
            Service = new(registry, binding, Fence, 2048, "fixture.runtime", accounting.ProcessGenerationId);
            Ownership = accounting.Begin(ModelOwnershipKind.CapabilityLease, binding.ModelId, requiresAdmission: false);
            Ownership.Bind(instanceId: binding.InstanceId); Ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
            Lifetime = new(Service, Fence, Ownership);
        }
    }
    private sealed class Provider(AdapterBinding binding, bool failDispose) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new(binding.AdapterId, binding.AdapterVersion, ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            request.Validate(); token.ThrowIfCancellationRequested();
            var metadata = LlamaPreparedGeneration.ResolveBudget(binding.InstanceId, Descriptor, request, 3, 2048,
                "Injected deterministic count; no tokenizer or native context proof.");
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(metadata, failDispose));
        }
    }
    private sealed class Prepared(PreparedModelMetadata metadata, bool failDispose) : IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata { get; } = metadata;
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
            throw new InvalidOperationException("This fixture never generates.");
        public ValueTask DisposeAsync() => failDispose ? ValueTask.FromException(new IOException("injected retained cleanup")) : ValueTask.CompletedTask;
    }
}
