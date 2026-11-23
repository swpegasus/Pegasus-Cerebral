using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Worker.ModelAdapters;
using ProtocolFixture = Cerebral.Worker.RegisteredReasoningIntegrationChecks.ProtocolFixture;

namespace Cerebral.Worker;

// Deterministic unit fixtures only. The actual reasoning parser is exercised through an
// in-memory HTTP handler; registry/slot objects are real, model/tokenizer/network/GPU are absent.
internal static class GenerationDraftChecks
{
    private const string Visible = "{\"action\":\"final\",\"text\":\"Hello draft world\"}";
    private static GenerationDraftBinding Binding(string id) =>
        new("fixture.conversation." + id, "fixture.request." + id, "fixture.run." + id,
            "fixture.effect." + id, "fixture.agent", "local");
    private static ModelGenerationRequest Request()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new("fixture.protocol", [new("user", [new TextContentPart("fixture draft request")])], 2048, 256,
            ResponseSchema: schema.RootElement.Clone()) { ReasoningTokens = 128, AllowResponseClamping = true };
    }
    internal static async Task<string[]> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("generation_draft_check:" + name); checks.Add(name); }
        await ValidAndDirectAsync(Check);
        await InvalidTerminalAsync(Check);
        await ObserverAndDisposalAsync(Check);
        await ScopeIsolationAsync(Check);
        await ForwarderLifetimeAsync(Check);
        await RegisteredBridgeAsync(Check);
        return checks.ToArray();
    }
    private static async Task ValidAndDirectAsync(Action<bool, string> check)
    {
        using var fixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible };
        await using var prepared = await fixture.PrepareAsync(Request());
        var source = (IBoundGenerationDraft)prepared;
        var samples = new List<GenerationDraftSample>(); var adopted = new List<string>();
        var early = true; var binding = Binding("valid"); var lateBindRefused = true;
        check(!source.TryBindDraft(binding with { RunId = "" }, samples.Add),
            "invalid_identity_is_unbound_without_consuming_valid_bind");
        check(source.TryBindDraft(binding, sample =>
        {
            early &= adopted.Count == 0 && fixture.SlotReads == 1 && fixture.Pool.AvailableCount == 0;
            lateBindRefused &= !source.TryBindDraft(Binding("rebind"), _ => { });
            samples.Add(sample);
        }) && !source.TryBindDraft(Binding("second"), _ => { }),
            "draft_binds_once_before_generation");
        var result = await prepared.GenerateAsync(adopted.Add, default);
        check(early && samples.Count == 2 && samples.All(x => x.Kind == GenerationDraftKind.Content) &&
            samples.Select(x => x.Sequence).SequenceEqual(new long[] { 1, 2 }) &&
            samples.All(x => x.Binding == binding) && string.Concat(samples.Select(x => x.Content)) == Visible,
            "public_fragments_arrive_before_terminal_and_keep_exact_identity_order");
        check(lateBindRefused && !source.TryBindDraft(binding, samples.Add) &&
            !JsonSerializer.Serialize(samples).Contains(ProtocolFixture.Hidden, StringComparison.Ordinal),
            "generation_and_terminal_refuse_late_binding_and_hidden_channel_never_enters_draft");
        check(result.Finish == ModelGenerationFinish.Stop && result.Text == Visible &&
            result.EvaluatedTokens == 1700 && result.GeneratedTokens == 7 &&
            adopted.SequenceEqual(new[] { Visible }) && fixture.Pool.AvailableCount == 1,
            "adopted_callback_usage_and_confirmed_idle_remain_terminal_only");
        var replay = false;
        try { await prepared.GenerateAsync(null, default); } catch (InvalidOperationException) { replay = true; }
        check(replay, "optional_draft_does_not_allow_generation_replay");

        using var directFixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible };
        await using var direct = await directFixture.PrepareAsync(Request());
        var directCallbacks = new List<string>();
        var directResult = await direct.GenerateAsync(directCallbacks.Add, default);
        check(directResult.Finish == ModelGenerationFinish.Stop && directCallbacks.SequenceEqual(new[] { Visible }),
            "unbound_direct_reasoning_call_keeps_one_legitimate_terminal_callback");
    }

    private static async Task InvalidTerminalAsync(Action<bool, string> check)
    {
        foreach (var scenario in new[] { "tagged", "escaped-tagged", "malformed", "protocol",
            "input-mismatch", "usage-overflow", "length", "cleanup" })
        {
            using var fixture = new ProtocolFixture
                { SplitDraft = true, DraftVisible = Visible, Scenario = scenario, IdleUnconfirmed = scenario == "cleanup" };
            await using var prepared = await fixture.PrepareAsync(Request());
            var samples = new List<GenerationDraftSample>(); var adopted = new List<string>();
            check(((IBoundGenerationDraft)prepared).TryBindDraft(Binding(scenario), samples.Add),
                "fixture_bind_" + scenario);
            var result = await prepared.GenerateAsync(adopted.Add, default);
            check(result.Finish == (scenario == "length" ? ModelGenerationFinish.Length : ModelGenerationFinish.Unknown) &&
                result.Text.Length == 0 && adopted.Count == 0 &&
                samples.Last().Kind == GenerationDraftKind.Unavailable && samples.Last().Content is null &&
                samples.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, samples.Count).Select(x => (long)x)),
                "nonadopted_" + scenario + "_invalidates_once_after_ordered_public_draft");
            check(scenario == "cleanup" ? fixture.Pool.AvailableCount == 0 && fixture.Pool.Quarantined.Count == 1 :
                fixture.Pool.AvailableCount == 1 && fixture.Pool.Quarantined.Count == 0,
                "native_release_or_quarantine_preserved_" + scenario);
        }
        using var cancelledFixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible };
        await using var cancelled = await cancelledFixture.PrepareAsync(Request());
        using var stop = new CancellationTokenSource();
        var cancelledSamples = new List<GenerationDraftSample>();
        ((IBoundGenerationDraft)cancelled).TryBindDraft(Binding("cancel"), sample =>
        {
            cancelledSamples.Add(sample);
            if (sample.Kind == GenerationDraftKind.Content) stop.Cancel();
        });
        var cancelledResult = await cancelled.GenerateAsync(null, stop.Token);
        check(cancelledResult.Finish == ModelGenerationFinish.Cancelled && cancelledResult.CancellationConfirmed &&
            cancelledResult.Text.Length == 0 && cancelledSamples.Last().Kind == GenerationDraftKind.Unavailable &&
            cancelledFixture.Pool.AvailableCount == 1,
            "cancellation_invalidates_provisional_text_after_confirmed_native_stop");
    }

    private static async Task ObserverAndDisposalAsync(Action<bool, string> check)
    {
        using (var fixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var kinds = new List<GenerationDraftKind>(); var adopted = new List<string>();
            ((IBoundGenerationDraft)prepared).TryBindDraft(Binding("throw"), sample =>
            {
                kinds.Add(sample.Kind);
                if (sample.Kind == GenerationDraftKind.Content) throw new InvalidOperationException("fixture observer");
            });
            var result = await prepared.GenerateAsync(adopted.Add, default);
            check(kinds.SequenceEqual(new[] { GenerationDraftKind.Content, GenerationDraftKind.Unavailable }) &&
                result.Finish == ModelGenerationFinish.Stop && adopted.SequenceEqual(new[] { Visible }) &&
                fixture.Pool.AvailableCount == 1,
                "throwing_optional_observer_disables_only_draft_and_cannot_change_terminal_adoption");
        }
        using (var fixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible })
        {
            var prepared = await fixture.PrepareAsync(Request());
            var samples = new List<GenerationDraftSample>(); Task? disposal = null;
            ((IBoundGenerationDraft)prepared).TryBindDraft(Binding("dispose"), sample =>
            {
                samples.Add(sample);
                if (sample.Kind == GenerationDraftKind.Content) disposal = prepared.DisposeAsync().AsTask();
            });
            var result = await prepared.GenerateAsync(null, default);
            await (disposal ?? throw new InvalidOperationException("Missing fixture disposal."));
            var count = samples.Count;
            await prepared.DisposeAsync();
            check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed &&
                samples.Last().Kind == GenerationDraftKind.Unavailable && samples.Count == count &&
                fixture.Pool.AvailableCount == 1 && !((IBoundGenerationDraft)prepared).TryBindDraft(Binding("late"), _ => { }),
                "disposal_during_draft_stops_once_and_prevents_late_publication_or_binding");
        }
        using (var fixture = new ProtocolFixture())
        {
            var prepared = await fixture.PrepareAsync(Request());
            var samples = new List<GenerationDraftSample>();
            ((IBoundGenerationDraft)prepared).TryBindDraft(Binding("unused"), samples.Add);
            await prepared.DisposeAsync();
            check(samples.Count == 1 && samples[0].Kind == GenerationDraftKind.Unavailable &&
                fixture.Pool.AvailableCount == 1,
                "unused_bound_preparation_disposal_retracts_and_releases_without_generation");
        }
    }

    private static async Task ScopeIsolationAsync(Action<bool, string> check)
    {
        using var firstFixture = new ProtocolFixture { DraftVisible = Visible };
        using var secondFixture = new ProtocolFixture { DraftVisible = "{\"action\":\"final\",\"text\":\"Second\"}" };
        await using var first = await firstFixture.PrepareAsync(Request());
        await using var second = await secondFixture.PrepareAsync(Request());
        RegisteredWorkflowDraftForwarder? outer;
        RegisteredWorkflowDraftForwarder? inner;
        using (var scope = RegisteredWorkflowDraftScope.Enter())
        {
            RegisteredWorkflowDraftScope.Capture(first);
            using (var nested = RegisteredWorkflowDraftScope.Enter())
            {
                RegisteredWorkflowDraftScope.Capture(second);
                inner = nested.Detach(second.Metadata);
                check(nested.Detach(second.Metadata) is null, "scope_detaches_only_once");
            }
            outer = scope.Detach(first.Metadata);
        }
        using (outer) using (inner)
        {
            var a = new List<GenerationDraftSample>(); var b = new List<GenerationDraftSample>();
            check(outer?.TryBind(Binding("outer"), a.Add) == true && inner?.TryBind(Binding("inner"), b.Add) == true,
                "nested_scopes_restore_distinct_preparations_even_when_metadata_equal");
            await Task.WhenAll(first.GenerateAsync(null, default), second.GenerateAsync(null, default));
            check(string.Concat(a.Select(x => x.Content)) == Visible &&
                string.Concat(b.Select(x => x.Content)) == secondFixture.DraftVisible &&
                a.All(x => x.Binding.RunId == Binding("outer").RunId) && b.All(x => x.Binding.RunId == Binding("inner").RunId),
                "nested_sources_publish_only_their_own_bound_identity");
        }

        using var mismatchFixture = new ProtocolFixture();
        await using var mismatch = await mismatchFixture.PrepareAsync(Request());
        using (var scope = RegisteredWorkflowDraftScope.Enter())
        {
            RegisteredWorkflowDraftScope.Capture(mismatch);
            check(scope.Detach(mismatch.Metadata with { InputTokens = mismatch.Metadata.InputTokens + 1 }) is null,
                "metadata_mismatch_discards_binder_without_guessing_preparation");
        }
        using (var scope = RegisteredWorkflowDraftScope.Enter())
        {
            RegisteredWorkflowDraftScope.Capture(mismatch); RegisteredWorkflowDraftScope.Capture(mismatch);
            check(scope.Detach(mismatch.Metadata) is null, "multiple_capture_is_ambiguous_even_with_equal_metadata");
        }
        var abandoned = RegisteredWorkflowDraftScope.Enter();
        RegisteredWorkflowDraftScope.Capture(mismatch); abandoned.Dispose();
        check(abandoned.Detach(mismatch.Metadata) is null, "disposed_prepare_scope_retains_no_binder");

        var entered = 0; var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Concurrent(string id)
        {
            using var fixture = new ProtocolFixture { DraftVisible = "{\"action\":\"final\",\"text\":\"" + id + "\"}" };
            using var scope = RegisteredWorkflowDraftScope.Enter();
            await Task.Yield();
            await using var prepared = await fixture.PrepareAsync(Request());
            RegisteredWorkflowDraftScope.Capture(prepared);
            if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var forwarder = scope.Detach(prepared.Metadata);
            var samples = new List<GenerationDraftSample>();
            if (forwarder?.TryBind(Binding(id), samples.Add) != true) return false;
            await prepared.GenerateAsync(null, default);
            return samples.All(x => x.Binding == Binding(id)) && string.Concat(samples.Select(x => x.Content)) == fixture.DraftVisible;
        }
        var outcomes = await Task.WhenAll(Concurrent("a"), Concurrent("b"));
        check(outcomes.All(x => x), "concurrent_async_prepare_scopes_never_share_last_preparation_state");

        Action<GenerationDraftSample>? captured = null;
        using var faulty = new RegisteredWorkflowDraftForwarder((_, observer) =>
        { captured = observer; throw new InvalidOperationException("fixture failed binder"); });
        var received = 0;
        check(!faulty.TryBind(Binding("faulty"), _ => received++), "binder_exception_is_optional_failure");
        captured!(new(Binding("faulty"), 1, GenerationDraftKind.Content, "ignored"));
        check(received == 0 && !faulty.TryBind(Binding("again"), _ => received++),
            "failed_binder_never_forwards_or_retries_terminal_fallback_as_draft");
    }

    private static async Task ForwarderLifetimeAsync(Action<bool, string> check)
    {
        Action<GenerationDraftSample>? callback = null;
        using var forwarder = new RegisteredWorkflowDraftForwarder((_, observer) => { callback = observer; return true; });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        check(forwarder.TryBind(Binding("drain"), _ =>
        {
            Interlocked.Increment(ref observed); entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Fixture callback was not released.");
        }), "concurrent_disposal_fixture_bound");
        var publishing = Task.Run(() => callback!(new(Binding("drain"), 1, GenerationDraftKind.Content, "fixture")));
        Task? disposing = null; var drained = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            disposing = Task.Run(() => { disposeEntered.TrySetResult(); forwarder.Dispose(); });
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // The observer is already inside its callback and cannot finish until released.
            // A correct disposal must remain incomplete throughout this bounded observation.
            drained = await Task.WhenAny(disposing, Task.Delay(40)) != disposing;
        }
        finally
        {
            release.Set();
            await publishing.WaitAsync(TimeSpan.FromSeconds(3));
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(3));
        }
        callback!(new(Binding("drain"), 2, GenerationDraftKind.Content, "late"));
        check(drained && observed == 1, "forwarder_dispose_drains_admitted_callback_and_rejects_late_invocation");

        Action<GenerationDraftSample>? reentrantCallback = null;
        using var reentrant = new RegisteredWorkflowDraftForwarder((_, observer) => { reentrantCallback = observer; return true; });
        var reentrantCount = 0;
        reentrant.TryBind(Binding("reentrant"), _ => { reentrantCount++; reentrant.Dispose(); });
        reentrantCallback!(new(Binding("reentrant"), 1, GenerationDraftKind.Content, "fixture"));
        reentrantCallback!(new(Binding("reentrant"), 2, GenerationDraftKind.Content, "late"));
        check(reentrantCount == 1, "same_observer_dispose_is_reentrant_and_closes_further_callbacks");

        var provider = new FailedReleaseProvider();
        var registry = new AdapterRegistry(); var fence = new NativeRegistrationFence();
        var off = LlamaPreparedGeneration.Adapter with { SupportsReasoning = true };
        var binding = new AdapterBinding("fixture.failed.release", "fixture.protocol", "fixture.protocol.instance",
            off.AdapterId, off.Version);
        registry.Register(binding.BackendId, binding.ModelId, binding.InstanceId, provider, fence,
            [off, LlamaPreparedReasoningGeneration.Adapter]);
        var service = new RegisteredNativeGenerationService(registry, binding, fence, 2048, "fixture.runtime", "fixture.process");
        var prepared = await service.CreateWorkflowAdapter(true).PrepareAsync(Request(), default);
        var lateSamples = new List<GenerationDraftSample>();
        check(((IBoundGenerationDraft)prepared).TryBindDraft(Binding("failed-release"), lateSamples.Add),
            "failed_release_fixture_captures_private_draft");
        var result = await prepared.GenerateAsync(null, default);
        var refused = false;
        try { await prepared.DisposeAsync(); } catch (RegisteredWorkflowCleanupException) { refused = true; }
        // The deliberately faulty provider retains its callback after a failed release.
        // The private forwarder must fence it without claiming provider cleanup succeeded.
        provider.Late!(new(Binding("failed-release"), 1, GenerationDraftKind.Content, "late fixture"));
        try { await prepared.DisposeAsync(); } catch (RegisteredWorkflowCleanupException) { }
        check(refused && result.Finish == ModelGenerationFinish.Stop && lateSamples.Count == 0 &&
            provider.Disposals == 1 && ((IRegisteredNativeWorkflowPreparation)prepared).Observe().CleanupUnconfirmed,
            "failed_release_stays_uncertain_but_forwarder_is_closed_without_retry_or_late_publication");
    }

    private static async Task RegisteredBridgeAsync(Action<bool, string> check)
    {
        using var fixture = new ProtocolFixture { SplitDraft = true, DraftVisible = Visible };
        var provider = new ProtocolProvider(fixture);
        var registry = new AdapterRegistry(); var fence = new NativeRegistrationFence();
        var off = LlamaPreparedGeneration.Adapter with { SupportsReasoning = true };
        var binding = new AdapterBinding("fixture.draft.backend", "fixture.protocol", "fixture.protocol.instance",
            off.AdapterId, off.Version);
        registry.Register(binding.BackendId, binding.ModelId, binding.InstanceId, provider, fence,
            [off, LlamaPreparedReasoningGeneration.Adapter]);
        var service = new RegisteredNativeGenerationService(registry, binding, fence, 2048,
            "fixture.runtime", "fixture.process");
        var adapter = service.CreateWorkflowAdapter(true);
        provider.FailAfterCapture = true;
        var failed = false;
        try { await adapter.PrepareAsync(Request(), default); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && fixture.Pool.AvailableCount == 1 && registry.Observe(binding).RegistryReservations == 0,
            "failed_prepare_releases_scope_and_native_fixture_without_leaking_capture");
        provider.FailAfterCapture = false;
        var prepared = await adapter.PrepareAsync(Request(), default);
        var samples = new List<GenerationDraftSample>(); var adopted = new List<string>();
        check(((IBoundGenerationDraft)prepared).TryBindDraft(Binding("registered"), samples.Add),
            "registered_preparation_recovers_pre_wrapper_draft_facet_without_raw_inner_escape");
        var result = await prepared.GenerateAsync(adopted.Add, default);
        check(result.Finish == ModelGenerationFinish.Stop && samples.Count == 2 &&
            adopted.SequenceEqual(new[] { Visible }) && !((IBoundGenerationDraft)prepared).TryBindDraft(Binding("late"), _ => { }),
            "registered_bridge_preserves_draft_identity_and_one_terminal_callback");
        await prepared.DisposeAsync();
        check(fixture.Pool.AvailableCount == 1 && registry.Observe(binding).RegistryReservations == 0,
            "registered_draft_disposal_preserves_registry_and_native_fixture_release");

        provider.Capture = false; provider.SplitLegacyCallbacks = true;
        var unsupported = await adapter.PrepareAsync(Request(), default);
        var normal = new List<string>();
        check(!((IBoundGenerationDraft)unsupported).TryBindDraft(Binding("unsupported"), _ => { }),
            "unsupported_hidden_facet_returns_false_without_adapter_id_special_case");
        await unsupported.GenerateAsync(normal.Add, default);
        await unsupported.DisposeAsync();
        check(normal.SequenceEqual(new[] { Visible[..(Visible.Length / 2)], Visible[(Visible.Length / 2)..] }) &&
            fixture.Pool.AvailableCount == 1,
            "unsupported_binding_preserves_existing_multiple_onText_callbacks_without_terminal_assumptions");
    }

    // Deliberately defective release/callback behavior. This is a unit fixture, not native cleanup proof.
    private sealed class FailedReleaseProvider : IModelRuntimeAdapter
    {
        internal Action<GenerationDraftSample>? Late;
        internal int Disposals;
        public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter with { SupportsReasoning = true };
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var metadata = LlamaPreparedGeneration.ResolveBudget("fixture.protocol.instance",
                LlamaPreparedReasoningGeneration.Adapter, request, 3, 2048, "Injected failed-release fixture count.");
            var prepared = new FailedReleasePreparation(this, metadata);
            RegisteredWorkflowDraftScope.Capture(prepared);
            return Task.FromResult<IPreparedModelGeneration>(prepared);
        }
    }
    private sealed class FailedReleasePreparation(FailedReleaseProvider provider, PreparedModelMetadata metadata)
        : IPreparedModelGeneration, IBoundGenerationDraft
    {
        public PreparedModelMetadata Metadata => metadata;
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
        {
            if (provider.Late is not null) return false;
            provider.Late = observer; return true;
        }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            onText?.Invoke(Visible);
            return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, Visible, 7, 3));
        }
        public ValueTask DisposeAsync()
        {
            provider.Disposals++;
            return new(Task.FromException(new IOException("Injected uncertain provider release.")));
        }
    }

    private sealed class ProtocolProvider(ProtocolFixture fixture) : IModelRuntimeAdapter
    {
        internal bool FailAfterCapture, Capture = true, SplitLegacyCallbacks;
        public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter with { SupportsReasoning = true };
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var prepared = await fixture.PrepareAsync(request);
            if (Capture) RegisteredWorkflowDraftScope.Capture(prepared);
            if (FailAfterCapture)
            {
                await prepared.DisposeAsync();
                throw new InvalidOperationException("fixture prepare failed after optional capture");
            }
            return new HiddenFacet(prepared, SplitLegacyCallbacks);
        }
    }
    private sealed class HiddenFacet(IPreparedModelGeneration inner, bool splitCallbacks) : IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata => inner.Metadata;
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            // This fixture only verifies legacy callback forwarding; it is not native streaming evidence.
            Action<string>? callback = onText;
            if (splitCallbacks && onText is not null)
                callback = text => { onText(text[..(text.Length / 2)]); onText(text[(text.Length / 2)..]); };
            return inner.GenerateAsync(callback, token);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
