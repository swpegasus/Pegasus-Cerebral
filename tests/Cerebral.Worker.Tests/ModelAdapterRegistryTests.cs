using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker.Tests;

// Targeted deterministic checks against the normal Worker's internal registry.
internal static class ModelAdapterRegistryTests
{
    private static readonly List<string> checks = [];
    private static readonly AdapterRequirements Text = new(["text"]);
    private static ModelGenerationRequest Request() => new("shared-model",
        [new("user", [new TextContentPart("one two three")])], 1024, 64);
    private static AdapterBinding Binding(string backend, string instance, string adapter = "fixture.prepared.text") =>
        new(backend, "shared-model", instance, adapter, "1.0.0");
    private static ModelAdapterDescriptor Facet(bool reasoning = false) => new(
        reasoning ? "fixture.prepared.reasoning" : "fixture.prepared.text", "1.0.0", ["text", "structured"], reasoning);
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Reject(Func<Task> action, string name, string? code = null)
    {
        try { await action(); }
        catch (Exception e) when (code is null || e is RegistryException r && r.Code == code)
        { checks.Add(name); return; }
        throw new InvalidOperationException(name);
    }
    private static Task Action(Action action) { action(); return Task.CompletedTask; }
    private static void Register(AdapterRegistry registry, AdapterBinding b, FixtureAdapter adapter, FixtureOwner? owner = null,
        IReadOnlyList<ModelAdapterDescriptor>? facets = null) => registry.Register(b.BackendId, b.ModelId, b.InstanceId,
            adapter, owner ?? adapter.Owner, facets ?? [Facet(), Facet(true)]);

    internal static async Task<string[]> RunAsync()
    {
        checks.Clear();
        await RunPromotionAsync();
        return checks.ToArray();
    }

    private static async Task RunPromotionAsync()
    {
        var registry = new AdapterRegistry(); var a = Binding("backend-a", "instance-a"); var b = Binding("backend-b", "instance-b");
        var alpha = new FixtureAdapter(a); var beta = new FixtureAdapter(b);
        Register(registry, a, alpha); Register(registry, b, beta);
        var first = new AdapterCommandDispatch(registry); var second = new AdapterCommandDispatch(registry);
        Check(registry.CreateAdapter(a, Text).Descriptor.AdapterId != alpha.Descriptor.AdapterId, "explicit_prepared_facet_differs_from_facade");
        foreach (var bad in new[] { a with { BackendId = "unknown" }, a with { InstanceId = "instance-b" },
            a with { ModelId = "unknown" }, a with { AdapterId = "unknown" }, a with { AdapterVersion = "9" } })
            await Reject(() => Action(() => registry.CreateAdapter(bad, Text)), "unknown_or_mismatched_full_binding_rejected");
        await Reject(() => Action(() => registry.CreateAdapter(a, new(["image"]))), "unsupported_capability_before_provider", "capability_unsupported");
        Check(alpha.PrepareCalls == 0 && beta.PrepareCalls == 0, "selection_refusals_have_no_provider_calls");
        await Reject(() => first.PrepareAsync(a, Text, Request() with { ModelId = "other" }, default), "request_model_cannot_retarget_factory", "request_model_mismatch");
        var ah = await first.PrepareAsync(a, Text, Request(), default); var bh = await second.PrepareAsync(b, Text, Request(), default);
        Check(ah.HandleId != bh.HandleId && ah.Metadata.InstanceId != bh.Metadata.InstanceId && ah.Metadata.ModelId == bh.Metadata.ModelId,
            "same_model_id_independent_backend_instance_handles");
        Check(ReferenceEquals(ah.Metadata, alpha.Last!.Metadata) && ah.Metadata.CountBasis == alpha.Last.Metadata.CountBasis,
            "provider_metadata_count_basis_reference_preserved");
        await Reject(() => second.GenerateAsync(ah, a, null, default), "foreign_callback_owner_cannot_generate", "preparation_owner_or_binding_mismatch");
        await Reject(() => second.ReleaseAsync(ah, a).AsTask(), "foreign_callback_owner_cannot_release", "preparation_owner_or_binding_mismatch");
        await Reject(() => first.GenerateAsync(ah, b, null, default), "different_instance_cannot_borrow_preparation", "preparation_owner_or_binding_mismatch");
        Check(alpha.Last.GenerateCalls == 0 && alpha.Last.DisposeCalls == 0, "borrow_refused_before_provider_effect");
        alpha.ReturnExisting = beta.Last;
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "provider_cannot_return_another_instances_owned_object", "provider_preparation_object_reused");
        Check(beta.Last!.DisposeCalls == 0 && registry.Observe(b).RegistryReservations == 1,
            "rejected_aliased_preparation_cannot_dispose_existing_owner"); alpha.ReturnExisting = null;

        alpha.Last.BlockGeneration = true; beta.Last!.BlockGeneration = true;
        using var cancel = new CancellationTokenSource();
        var ag = first.GenerateAsync(ah, a, null, cancel.Token); var bg = second.GenerateAsync(bh, b, null, default);
        await Task.WhenAll(alpha.Last.Started.Task, beta.Last.Started.Task);
        cancel.Cancel(); var cancelled = await ag;
        Check(cancelled.Finish == ModelGenerationFinish.Cancelled && cancelled.CancellationConfirmed, "provider_confirmed_cancel_is_preserved");
        await first.ReleaseAsync(ah, a);
        Check(!bg.IsCompleted && beta.Last.DisposeCalls == 0 && registry.Observe(b).RegistryReservations == 1,
            "cancel_release_one_does_not_touch_other_instance");
        beta.Last.FinishGate.TrySetResult(); var complete = await bg;
        Check(ReferenceEquals(complete, beta.Last.Returned) && complete.EvaluatedTokens == bh.Metadata.InputTokens,
            "provider_result_usage_reference_preserved");
        await second.ReleaseAsync(bh, b);
        await Reject(() => second.GenerateAsync(bh, b, null, default), "disposed_preparation_cannot_generate", "lease_disposing_or_retired");
        await first.ReleaseAsync(ah, a); Check(alpha.Last.DisposeCalls == 1, "logical_release_is_idempotent");
        Check(ah.Observe().LogicalRetirement && ah.Observe().PhysicalReleaseConfirmed is null, "logical_retirement_is_not_native_release_proof");
        alpha.ReturnExisting = alpha.Last;
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "provider_cannot_reuse_retired_one_use_preparation", "provider_preparation_object_reused");
        Check(alpha.Last!.DisposeCalls == 1, "retired_alias_rejection_does_not_repeat_disposal"); alpha.ReturnExisting = null;

        var uh = await first.PrepareAsync(a, Text, Request(), default); alpha.Last!.BlockGeneration = true; alpha.Last.UnknownOnCancellation = true;
        using var unknownCancel = new CancellationTokenSource(); var ug = first.GenerateAsync(uh, a, null, unknownCancel.Token);
        await alpha.Last.Started.Task; unknownCancel.Cancel(); var unknown = await ug;
        Check(unknown.Finish == ModelGenerationFinish.Unknown && !unknown.CancellationConfirmed && ReferenceEquals(unknown, alpha.Last.Returned),
            "cancellation_does_not_upgrade_provider_unknown");
        await first.ReleaseAsync(uh, a);
        Check(uh.Observe().ProviderFinish == ModelGenerationFinish.Unknown && uh.Observe().PhysicalReleaseConfirmed is null,
            "unknown_remains_unknown_after_dispose");

        var used = await first.PrepareAsync(a, Text, Request(), default); _ = await first.GenerateAsync(used, a, null, default);
        await Reject(() => first.GenerateAsync(used, a, null, default), "one_use_generation_preserved", "lease_already_consumed");
        await first.ReleaseAsync(used, a);
        var reason = a with { AdapterId = "fixture.prepared.reasoning" };
        var rh = await first.PrepareAsync(reason, new(["text"], true), Request() with { ReasoningTokens = 128 }, default);
        Check(rh.Metadata.AdapterId == reason.AdapterId && rh.Metadata.ReasoningReserveTokens == 128, "explicit_reasoning_facet_and_reserve");
        await first.ReleaseAsync(rh, reason);

        alpha.OverrideMetadata = metadata => metadata with { InstanceId = b.InstanceId };
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "foreign_provider_instance_metadata_rejected", "prepared_identity_mismatch");
        Check(alpha.Last!.DisposeCalls == 1 && registry.Observe(a).RegistryReservations == 0, "rejected_instance_preparation_disposed_not_adopted");
        alpha.OverrideMetadata = metadata => metadata with { AdapterVersion = "2.0.0" };
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "foreign_prepared_adapter_version_rejected", "prepared_identity_mismatch");
        alpha.OverrideMetadata = metadata => metadata with { InputTokens = 999999 };
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "existing_metadata_budget_validator_reused");
        Check(alpha.Last!.DisposeCalls == 1, "invalid_budget_preparation_disposed"); alpha.OverrideMetadata = null;
        alpha.ContextLimit = true;
        ModelContextLimitException? limited = null;
        try { _ = await first.PrepareAsync(a, Text, Request(), default); } catch (ModelContextLimitException e) { limited = e; }
        Check(ReferenceEquals(limited, alpha.LastContextLimit) && limited!.Metadata.InputTokens == 1000,
            "existing_context_limit_exception_and_measured_metadata_preserved"); alpha.ContextLimit = false;

        var priorDescriptor = alpha.Descriptor; alpha.Descriptor = priorDescriptor with { Version = "changed" };
        await Reject(() => first.PrepareAsync(a, Text, Request(), default), "facade_descriptor_drift_fenced", "adapter_descriptor_changed"); alpha.Descriptor = priorDescriptor;
        var driftLease = await first.PrepareAsync(a, Text, Request(), default); alpha.Descriptor = priorDescriptor with { SupportsReasoning = false };
        await Reject(() => first.GenerateAsync(driftLease, a, null, default), "descriptor_drift_after_prepare_fenced", "adapter_descriptor_changed");
        Check(alpha.Last!.GenerateCalls == 0, "drift_fails_before_generation"); alpha.Descriptor = priorDescriptor; await first.ReleaseAsync(driftLease, a);
        var changedMetadata = await first.PrepareAsync(a, Text, Request(), default);
        alpha.Last!.Metadata = alpha.Last.Metadata with { InstanceId = b.InstanceId };
        await Reject(() => first.GenerateAsync(changedMetadata, a, null, default), "prepared_metadata_cannot_retarget_after_prepare", "prepared_metadata_changed");
        Check(alpha.Last.GenerateCalls == 0 && changedMetadata.Metadata.InstanceId == a.InstanceId, "prepared_identity_snapshot_retained");
        await first.ReleaseAsync(changedMetadata, a);

        await ValidatedMetadataSnapshot(); await SynchronousReentrancy(); await RegistryWrapperAliases();
        await Reject(() => Action(() => registry.Register("estimate", "model", "instance", alpha, new FixtureOwner(), [Facet()], PreparationKind.EstimatedTokens)),
            "hosted_estimate_excluded_from_exact_prepared_contract", "exact_preparation_required");
        var noReasoning = new FixtureAdapter(a with { BackendId = "no-reasoning" });
        noReasoning.Descriptor = noReasoning.Descriptor with { SupportsReasoning = false };
        await Reject(() => Action(() => Register(registry, a with { BackendId = "no-reasoning" }, noReasoning)),
            "prepared_facet_cannot_enlarge_facade_capability", "facet_not_supported_by_facade");
        await Reject(() => Action(() => Register(registry, a with { InstanceId = "other" }, alpha, alpha.Owner)),
            "ambiguous_shared_instance_lifecycle_owner_refused", "instance_owner_reused");
        var captured = registry.CreateAdapter(a, Text); var retirement = await registry.RetireAsync(a);
        Check(retirement.Admission == InstanceAdmission.RegistryRetired && retirement.InstanceDisposeReturned && retirement.PhysicalStopConfirmed is null,
            "instance_dispose_return_not_claimed_physical_stop");
        await Reject(() => captured.PrepareAsync(Request(), default), "captured_factory_cannot_prepare_retired_instance", "instance_admission_closed");
        await Reject(() => Action(() => Register(registry, a, new FixtureAdapter(a))), "retired_full_identity_cannot_be_rebound", "instance_identity_reused");
        _ = await registry.RetireAsync(a); Check(alpha.Owner.DisposeCalls == 1, "instance_retirement_idempotent");
        Check(beta.Owner.DisposeCalls == 0, "retiring_one_backend_does_not_dispose_other"); _ = await registry.RetireAsync(b);
    }

    private static async Task ValidatedMetadataSnapshot()
    {
        var registry = new AdapterRegistry(); var b = Binding("getter", "getter-instance");
        var provider = new FixtureAdapter(b)
        {
            ConfigurePrepared = prepared =>
            {
                var valid = prepared.Metadata;
                prepared.MetadataSequence = read => read == 1 ? valid : valid with { InstanceId = "stable-foreign-instance" };
                prepared.MetadataReads = 0;
            }
        };
        Register(registry, b, provider); var owner = new AdapterCommandDispatch(registry);
        var handle = await owner.PrepareAsync(b, Text, Request(), default);
        Check(provider.Last!.MetadataReads == 1 && handle.Metadata.InstanceId == b.InstanceId,
            "lease_adopts_the_single_validated_metadata_getter_value");
        await Reject(() => owner.GenerateAsync(handle, b, null, default), "stable_foreign_second_metadata_value_fenced_before_generate", "prepared_metadata_changed");
        Check(provider.Last.GenerateCalls == 0 && handle.Metadata.InstanceId == b.InstanceId,
            "metadata_constructor_window_cannot_adopt_foreign_identity");
        await owner.ReleaseAsync(handle, b); _ = await registry.RetireAsync(b);
    }

    private static async Task SynchronousReentrancy()
    {
        var registry = new AdapterRegistry(); var b = Binding("reentrant", "reentrant-instance");
        var provider = new FixtureAdapter(b); Register(registry, b, provider); var owner = new AdapterCommandDispatch(registry);
        var handle = await owner.PrepareAsync(b, Text, Request(), default);
        provider.Last!.BlockGeneration = true; provider.Last.EmitBeforeFirstAwait = true; provider.Last.IgnoreDisposeCancellation = true;
        Task? reentrantRelease = null;
        var generating = owner.GenerateAsync(handle, b, _ => { reentrantRelease = owner.ReleaseAsync(handle, b).AsTask(); }, default);
        Check(reentrantRelease is not null && !generating.IsCompleted && !reentrantRelease.IsCompleted,
            "synchronous_ontext_release_observes_published_generation_completion");
        Check(provider.Last.DisposeCalls == 1 && registry.Observe(b).RegistryReservations == 1 && !handle.Observe().LogicalRetirement,
            "synchronous_reentrant_release_cannot_retire_active_invocation");
        provider.Last.FinishGate.TrySetResult(); _ = await generating; await reentrantRelease!;
        Check(handle.Observe().LogicalRetirement && registry.Observe(b).RegistryReservations == 0,
            "reentrant_cleanup_retires_only_after_exact_generation_settles");
        var unused = await owner.PrepareAsync(b, Text, Request(), default); Task? nestedRelease = null;
        provider.Last!.OnDispose = () => { nestedRelease = owner.ReleaseAsync(unused, b).AsTask(); };
        var releasing = owner.ReleaseAsync(unused, b).AsTask(); await releasing;
        Check(ReferenceEquals(releasing, nestedRelease) && provider.Last.DisposeCalls == 1 && unused.Observe().LogicalRetirement,
            "synchronous_dispose_reentrancy_reuses_published_cleanup_completion");
        var cancelled = await owner.PrepareAsync(b, Text, Request(), default); provider.Last!.EmitBeforeFirstAwait = true;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelledTask = owner.GenerateAsync(cancelled, b, _ => throw new OperationCanceledException(cancellation.Token), default);
        await Reject(async () => { _ = await cancelledTask; }, "synchronous_callback_cancellation_propagates_task_cancellation");
        Check(cancelledTask.IsCanceled && cancelled.Observe().ProviderFinish == ModelGenerationFinish.Unknown && cancelled.Observe().ProviderCancellationConfirmed is null,
            "callback_failure_does_not_manufacture_provider_cancel_confirmation");
        await owner.ReleaseAsync(cancelled, b); _ = await registry.RetireAsync(b);
    }

    private static async Task RegistryWrapperAliases()
    {
        var registry = new AdapterRegistry(); var a = Binding("wrapper-a", "wrapper-instance-a"); var b = Binding("wrapper-b", "wrapper-instance-b");
        var alpha = new FixtureAdapter(a); var beta = new FixtureAdapter(b); Register(registry, a, alpha); Register(registry, b, beta);
        var rightful = new AdapterCommandDispatch(registry); var rejected = new AdapterCommandDispatch(registry);
        var owned = await rightful.PrepareAsync(b, Text, Request(), default);
        alpha.ReturnExisting = owned.Lease;
        await Reject(() => rejected.PrepareAsync(a, Text, Request(), default), "active_registry_wrapper_cannot_be_reclaimed_by_provider", "provider_returned_registry_owned_lease");
        Check(beta.Last!.DisposeCalls == 0 && !owned.Observe().LogicalRetirement && registry.Observe(b).RegistryReservations == 1,
            "active_wrapper_alias_rejection_leaves_original_wrapper_raw_and_owner_untouched");
        _ = await rightful.GenerateAsync(owned, b, null, default); await rightful.ReleaseAsync(owned, b);
        await Reject(() => rejected.PrepareAsync(a, Text, Request(), default), "retired_registry_wrapper_cannot_be_reclaimed_by_provider", "provider_returned_registry_owned_lease");
        Check(beta.Last.DisposeCalls == 1 && owned.Observe().LogicalRetirement && registry.Observe(b).RegistryReservations == 0,
            "retired_wrapper_alias_rejection_does_not_repeat_cleanup");
        alpha.ReturnExisting = null; _ = await registry.RetireAsync(a); _ = await registry.RetireAsync(b);
    }
}

internal sealed class FixtureOwner : IAsyncDisposable
{
    public int DisposeCalls;
    public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCalls); return ValueTask.CompletedTask; }
}
internal sealed class FixtureAdapter(AdapterBinding binding) : IModelRuntimeAdapter
{
    public ModelAdapterDescriptor Descriptor { get; set; } = new("fixture.facade." + binding.BackendId, "9.0.0", ["text", "structured"], true);
    public readonly FixtureOwner Owner = new();
    public readonly TaskCompletionSource PrepareEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool PausePreparation, IgnorePrepareCancellation, ContextLimit, FailPreparedDisposal;
    public int PrepareCalls;
    public ModelGenerationRequest? LastRequest;
    public FixturePrepared? Last;
    public ModelContextLimitException? LastContextLimit;
    public Func<PreparedModelMetadata, PreparedModelMetadata>? OverrideMetadata;
    public IPreparedModelGeneration? ReturnExisting;
    public Action<FixturePrepared>? ConfigurePrepared;
    public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
    {
        PrepareCalls++; LastRequest = request; PrepareEntered.TrySetResult();
        if (PausePreparation)
        {
            if (IgnorePrepareCancellation) await PrepareGate.Task; else await PrepareGate.Task.WaitAsync(token);
        }
        if (ReturnExisting is not null) return ReturnExisting;
        if (!IgnorePrepareCancellation) token.ThrowIfCancellationRequested();
        var count = request.Messages.SelectMany(x => x.Content).OfType<TextContentPart>()
            .Sum(x => x.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var metadata = new PreparedModelMetadata(binding.InstanceId,
            request.ReasoningTokens == 0 ? "fixture.prepared.text" : "fixture.prepared.reasoning", "1.0.0", request.ModelId,
            ContextLimit ? 1000 : count, request.ContextLimit, ContextLimit ? 0 : request.ResponseLimit,
            "CPU fixture-only word counting; no native tokenizer or physical reservation")
            { DesiredResponseBudget = request.ResponseLimit, ReasoningReserveTokens = request.ReasoningTokens };
        if (ContextLimit) { LastContextLimit = new(metadata); throw LastContextLimit; }
        Last = new(OverrideMetadata?.Invoke(metadata) ?? metadata) { FailDispose = FailPreparedDisposal };
        ConfigurePrepared?.Invoke(Last); return Last;
    }
}
internal sealed class FixturePrepared(PreparedModelMetadata metadata) : IPreparedModelGeneration
{
    private PreparedModelMetadata metadata = metadata;
    public int MetadataReads;
    public Func<int, PreparedModelMetadata>? MetadataSequence;
    public PreparedModelMetadata Metadata { get => MetadataSequence?.Invoke(++MetadataReads) ?? metadata; set => metadata = value; }
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource FinishGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource disposal = new();
    public bool BlockGeneration, UnknownOnCancellation, IgnoreDisposeCancellation, FailDispose, EmitBeforeFirstAwait;
    public Action? OnDispose;
    public int GenerateCalls, DisposeCalls;
    public ModelGenerationResult? Returned;
    public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
    {
        GenerateCalls++; Started.TrySetResult();
        if (EmitBeforeFirstAwait) onText?.Invoke("fixture-synchronous-visible");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
        if (BlockGeneration)
        {
            try { await FinishGate.Task.WaitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                return Returned = new(UnknownOnCancellation ? ModelGenerationFinish.Unknown : ModelGenerationFinish.Cancelled,
                    "", CancellationConfirmed: !UnknownOnCancellation);
            }
        }
        if (!EmitBeforeFirstAwait) onText?.Invoke("fixture-visible");
        return Returned = new(ModelGenerationFinish.Stop, "fixture-visible", 1, Metadata.InputTokens);
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref DisposeCalls) != 1) throw new InvalidOperationException("fixture_duplicate_disposal");
        OnDispose?.Invoke();
        if (FailDispose) throw new IOException("fixture_cleanup_failure");
        if (!IgnoreDisposeCancellation) disposal.Cancel();
        return ValueTask.CompletedTask;
    }
}
