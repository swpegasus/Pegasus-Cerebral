using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// Deterministic source checks only. Registry/facade/lifetime checks use an explicitly fake
// provider with the actual slot pool. Protocol checks call the actual existing reasoning
// implementation through an in-memory HTTP handler; no network/model/GPU/process exists.
internal static class RegisteredReasoningIntegrationChecks
{
    internal static async Task<string[]> RunAsync()
    {
        var passed = new List<string>();
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("registered_reasoning_check:" + name); passed.Add(name); }
        RegistryAdmission(Check);
        await SelectionAndCancellationAsync(Check);
        await CleanupFaultAsync(Check);
        await RetirementAndStaleAsync(Check);
        await PreparedProtocolAsync(Check);
        return passed.ToArray();
    }
    private static ModelGenerationRequest Request(string model, bool reasoning) => new(model,
        [new("user", [new TextContentPart("fixture body")])], 2048, 256)
        { ReasoningTokens = reasoning ? 128 : 0 };
    private static IRegisteredNativeWorkflowPreparation View(IPreparedModelGeneration prepared) =>
        (IRegisteredNativeWorkflowPreparation)prepared;
    private static async Task<Exception> FailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected deterministic refusal.");
    }

    private static void RegistryAdmission(Action<bool, string> check)
    {
        var binding = new AdapterBinding("fixture.registry", "fixture.reasoning.model", Guid.NewGuid().ToString("N"),
            LlamaPreparedGeneration.Adapter.AdapterId, LlamaPreparedGeneration.Adapter.Version);
        var off = LlamaPreparedGeneration.Adapter with { SupportsReasoning = false };
        var provider = new FakeProvider(binding, new(2));
        var accepted = new AdapterRegistry(); var fence = new NativeRegistrationFence();
        accepted.Register(binding.BackendId, binding.ModelId, binding.InstanceId, provider, fence,
            [off, LlamaPreparedReasoningGeneration.Adapter]);
        _ = accepted.CreateAdapter(binding, new(["text"]));
        _ = accepted.CreateAdapter(binding with { AdapterId = LlamaPreparedReasoningGeneration.Adapter.AdapterId,
            AdapterVersion = LlamaPreparedReasoningGeneration.Adapter.Version }, new(["text"], Reasoning: true));
        check(accepted.Observe(binding).RegistryReservations == 0 && provider.Descriptor.SupportsReasoning,
            "real_registry_accepts_both_exact_facets_under_explicit_reasoning_dispatcher_facade_without_preparation");
        RegistryException? refusal = null;
        try
        {
            new AdapterRegistry().Register(binding.BackendId, binding.ModelId, binding.InstanceId,
                new UnsupportedProvider(off), new NativeRegistrationFence(), [off, LlamaPreparedReasoningGeneration.Adapter]);
        }
        catch (RegistryException error) { refusal = error; }
        check(refusal?.Code == "facet_not_supported_by_facade",
            "real_registry_still_rejects_reasoning_facet_under_genuinely_off_only_facade");
    }
    private sealed class UnsupportedProvider(ModelAdapterDescriptor descriptor) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = descriptor;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Unsupported fixture facade must refuse before provider.");
    }

    private static async Task SelectionAndCancellationAsync(Action<bool, string> check)
    {
        await using var fixture = new Composition();
        var source = (IRegisteredNativeWorkflowSource)fixture.Service;
        var off = source.CreateWorkflowAdapter();
        var explicitOff = source.CreateWorkflowAdapter(false);
        var on = source.CreateWorkflowAdapter(true);
        check(off.Binding == explicitOff.Binding && off.Binding == fixture.Service.Binding &&
            !off.Descriptor.SupportsReasoning && on.Descriptor == LlamaPreparedReasoningGeneration.Adapter &&
            on.Binding.InstanceId == off.Binding.InstanceId && on.Binding.ModelId == off.Binding.ModelId &&
            on.Binding.AdapterId == LlamaPreparedReasoningGeneration.Adapter.AdapterId &&
            on.Binding != off.Binding, "same_entry_typed_selection_keeps_default_off_and_immutable_exact_reasoning_identity");
        var onWithOff = await FailureAsync(async () => { await on.PrepareAsync(Request(off.Binding.ModelId, false), default); });
        var offWithOn = await FailureAsync(async () => { await off.PrepareAsync(Request(off.Binding.ModelId, true), default); });
        var invalid = await FailureAsync(async () =>
            { await on.PrepareAsync(Request(off.Binding.ModelId, true) with { ReasoningTokens = 1 }, default); });
        check(onWithOff is NotSupportedException && offWithOn is NotSupportedException &&
            invalid is ArgumentOutOfRangeException && fixture.Provider.Created.Count == 0 &&
            fixture.Service.Observe().ActiveCallbacks == 0 && fixture.Pool.AvailableCount == 2,
            "mode_mismatch_and_invalid_reasoning_reserve_refuse_before_provider_or_slot");
        var direct = await FailureAsync(async () =>
        {
            await fixture.Service.GenerateAsync(off.Binding.ModelId, [new("user", "fixture")], 128, 0, false,
                null, default, reasoningMode: "on", reasoningTokens: 128);
        });
        check(direct is NotSupportedException && fixture.Provider.Created.Count == 0 &&
            fixture.Service.Binding == off.Binding, "legacy_registered_direct_service_stays_off_only_before_provider");

        fixture.Provider.HoldGeneration = true;
        var a = await off.PrepareAsync(Request(off.Binding.ModelId, false), default);
        var b = await on.PrepareAsync(Request(on.Binding.ModelId, true), default);
        try
        {
            var pa = fixture.Provider.Created[0]; var pb = fixture.Provider.Created[1];
            check(a.Metadata.AdapterId == off.Binding.AdapterId && b.Metadata.AdapterId == on.Binding.AdapterId &&
                a.Metadata.InstanceId == b.Metadata.InstanceId && b.Metadata.ReasoningReserveTokens == 128 &&
                View(a).Observe().Binding == off.Binding && View(b).Observe().Binding == on.Binding &&
                fixture.Service.Observe().Registry.RegistryReservations == 2 &&
                fixture.Service.Observe().ActiveCallbacks == 2 && fixture.Pool.AvailableCount == 0,
                "same_entry_off_on_preparations_keep_exact_handle_bindings_and_share_actual_allocator_owners");
            using var cancellation = new CancellationTokenSource();
            var ga = a.GenerateAsync(null, default);
            var gb = b.GenerateAsync(null, cancellation.Token);
            await Task.WhenAll(pa.Entered.Task, pb.Entered.Task).WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var cancelled = await gb.WaitAsync(TimeSpan.FromSeconds(3));
            check(cancelled.Finish == ModelGenerationFinish.Cancelled && cancelled.CancellationConfirmed &&
                !ga.IsCompleted && fixture.Pool.AvailableCount == 1 &&
                fixture.Service.Observe().Registry.RegistryReservations == 2,
                "fake_provider_on_cancellation_settles_only_its_slot_without_releasing_other_facet_registry_owner");
            pa.Allow.TrySetResult();
            var completed = await ga.WaitAsync(TimeSpan.FromSeconds(3));
            check(completed.Finish == ModelGenerationFinish.Stop && completed.Text == "fixture visible" &&
                fixture.Service.Binding == off.Binding && View(b).Observe().Binding == on.Binding,
                "other_facet_generation_and_selected_observation_survive_independent_fake_provider_cancellation");
        }
        finally
        {
            foreach (var prepared in fixture.Provider.Created) prepared.Allow.TrySetResult();
            await a.DisposeAsync(); await b.DisposeAsync();
        }
        check(fixture.Service.Observe().Registry.RegistryReservations == 0 &&
            fixture.Service.Observe().Registry.LogicalRetirements == 2 &&
            fixture.Service.Observe().ActiveCallbacks == 0 && fixture.Pool.AvailableCount == 2,
            "off_on_release_uses_each_captured_handle_binding_and_retires_both_logical_owners_once");
    }

    private static async Task CleanupFaultAsync(Action<bool, string> check)
    {
        var fixture = new Composition();
        var off = fixture.Service.CreateWorkflowAdapter(false);
        var on = fixture.Service.CreateWorkflowAdapter(true);
        var a = await off.PrepareAsync(Request(off.Binding.ModelId, false), default);
        fixture.Provider.FailNextDispose = true;
        var b = await on.PrepareAsync(Request(on.Binding.ModelId, true), default);
        var cleanup = await FailureAsync(async () => await b.DisposeAsync());
        check(cleanup is RegisteredWorkflowCleanupException failure &&
            failure.RegistryCleanup is { LogicalRetirement: false, DelegatedDisposeReturned: false } &&
            failure.Registry is { Admission: InstanceAdmission.CleanupFaulted, RegistryReservations: 2 } &&
            View(b).Observe().Binding == on.Binding && fixture.Service.Observe().ActiveCallbacks == 1,
            "reasoning_fake_provider_cleanup_failure_retains_exact_selected_handle_and_shared_entry_fault");
        var unaffected = await a.GenerateAsync(null, default);
        await a.DisposeAsync();
        check(unaffected.Finish == ModelGenerationFinish.Stop && fixture.Service.Observe().ActiveCallbacks == 0 &&
            fixture.Service.Observe().Registry is { Admission: InstanceAdmission.CleanupFaulted, RegistryReservations: 1 } &&
            fixture.Pool.Quarantined.Count == 1, "other_already_owned_facet_can_settle_without_refunding_reasoning_cleanup_unknown");
        var calls = fixture.Provider.Created.Count;
        var offRefusal = await FailureAsync(async () => { await off.PrepareAsync(Request(off.Binding.ModelId, false), default); });
        var onRefusal = await FailureAsync(async () => { await on.PrepareAsync(Request(on.Binding.ModelId, true), default); });
        check(offRefusal is RegistryException && onRefusal is RegistryException &&
            fixture.Provider.Created.Count == calls, "one_cleanup_fault_refuses_new_preparations_on_both_facets_without_replacement");
        var retirement = await fixture.Lifetime.BeginRetirement().WaitAsync(TimeSpan.FromSeconds(3));
        check(retirement is not null && fixture.Ownership.IsUnknown && !fixture.Ownership.IsRetired &&
            fixture.Service.Observe().Registry.RegistryReservations == 1,
            "callback_zero_and_retirement_attempt_do_not_erase_shared_cleanup_faulted_reservation");
        // The intentionally faulted fixture retains its exact registry/accounting owner.
    }

    private static async Task RetirementAndStaleAsync(Action<bool, string> check)
    {
        var old = new Composition();
        var off = old.Service.CreateWorkflowAdapter();
        var on = old.Service.CreateWorkflowAdapter(true);
        var a = await off.PrepareAsync(Request(off.Binding.ModelId, false), default);
        var b = await on.PrepareAsync(Request(on.Binding.ModelId, true), default);
        var retirement = old.Lifetime.BeginRetirement();
        check(!retirement.IsCompleted && old.Ownership.IsUnknown && !old.Service.Observe().RegistrationOpen,
            "same_lifetime_retirement_closes_both_facets_and_waits_without_optimistic_owner_release");
        await a.DisposeAsync();
        check(!retirement.IsCompleted && old.Service.Observe().ActiveCallbacks == 1,
            "retiring_off_facet_alone_does_not_finish_shared_reasoning_owner");
        await b.DisposeAsync();
        check(await retirement.WaitAsync(TimeSpan.FromSeconds(3)) is null && old.Ownership.IsRetired &&
            old.Service.Observe().Registry.Admission == InstanceAdmission.RegistryRetired,
            "same_lifetime_retires_only_after_both_actual_wrapper_cleanup_paths_settle");
        await using var replacement = new Composition();
        var fresh = replacement.Service.CreateWorkflowAdapter(true);
        var oldFailure = await FailureAsync(async () => { await on.PrepareAsync(Request(on.Binding.ModelId, true), default); });
        await using var prepared = await fresh.PrepareAsync(Request(fresh.Binding.ModelId, true), default);
        check(oldFailure is InvalidOperationException && replacement.Provider.Created.Count == 1 &&
            prepared.Metadata.InstanceId == fresh.Binding.InstanceId &&
            prepared.Metadata.InstanceId != on.Binding.InstanceId && old.Provider.Created.Count == 2,
            "old_closed_binding_never_follows_distinct_fixture_generation_and_fresh_binding_is_exact");
        var runtimeChanged = false; var instanceChanged = false;
        try { NativeRegistrationGuard.Require(false, true, true, true, false, true); }
        catch (InvalidOperationException) { runtimeChanged = true; }
        try { NativeRegistrationGuard.Require(true, false, true, true, false, true); }
        catch (InvalidOperationException) { instanceChanged = true; }
        check(runtimeChanged && instanceChanged,
            "existing_native_generation_guard_still_refuses_runtime_and_exact_instance_mismatch");
    }

    private static async Task PreparedProtocolAsync(Action<bool, string> check)
    {
        using (var fixture = new ProtocolFixture { HoldFirstCount = true })
        {
            var messages = new List<ModelInputMessage> { new("user", [new TextContentPart("frozen reasoning body")]) };
            using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
            var request = Request("fixture.protocol", true) with
                { Messages = messages, ResponseSchema = schema.RootElement, AllowResponseClamping = true };
            var preparing = fixture.PrepareAsync(request);
            await fixture.CountEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            messages.Clear(); schema.Dispose(); fixture.AllowCount.TrySetResult();
            await using var prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(3));
            check(prepared.Metadata.AdapterId == LlamaPreparedReasoningGeneration.Adapter.AdapterId &&
                prepared.Metadata.InputTokens == 1700 && prepared.Metadata.ResponseBudget == 92 &&
                prepared.Metadata.ReasoningReserveTokens == 128 && prepared.Metadata.CombinedGenerationBudget == 220 &&
                fixture.Counted.Count == 2 && fixture.Counted[1].GetProperty("max_tokens").GetInt32() == 220,
                "actual_reasoning_preparer_counts_exact_final_body_and_recounts_clamped_combined_budget");
            var callbacks = new List<string>(); var publishedAfterRelease = false;
            var result = await prepared.GenerateAsync(text =>
                { publishedAfterRelease = fixture.Pool.AvailableCount == 1 && fixture.SlotReads >= 2; callbacks.Add(text); }, default);
            check(result.Finish == ModelGenerationFinish.Stop && result.EvaluatedTokens == 1700 &&
                result.GeneratedTokens == 7 && result.Text == ProtocolFixture.Visible &&
                callbacks.SequenceEqual([ProtocolFixture.Visible]) && publishedAfterRelease &&
                !JsonSerializer.Serialize(result).Contains(ProtocolFixture.Hidden, StringComparison.Ordinal),
                "actual_reasoning_parser_discards_hidden_delta_and_publishes_only_visible_final_after_confirmed_idle");
            var body = fixture.Generated ?? throw new InvalidOperationException("Missing fixture generation body.");
            check(JsonElement.DeepEquals(body, fixture.Counted[1]) &&
                body.GetProperty("messages")[0].GetProperty("content").GetString() == "frozen reasoning body" &&
                body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() &&
                body.GetProperty("reasoning_budget_tokens").GetInt32() == 128 &&
                body.GetProperty("stop").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "stop", "", "stop" }) &&
                body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString() == "object" &&
                body.GetProperty("cache_prompt").GetBoolean(),
                "actual_reasoning_body_schema_stops_mode_and_cache_policy_are_frozen_across_count_and_generation");
        }
        foreach (var scenario in new[] { "tagged", "input-mismatch", "length" })
        {
            using var fixture = new ProtocolFixture { Scenario = scenario };
            await using var prepared = await fixture.PrepareAsync(Request("fixture.protocol", true) with { AllowResponseClamping = true });
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            check(result.Finish == (scenario == "length" ? ModelGenerationFinish.Length : ModelGenerationFinish.Unknown) &&
                result.Text == "" && callbacks.Count == 0 && fixture.Pool.AvailableCount == 1,
                "actual_reasoning_parser_never_publishes_nonadopted_" + scenario);
        }
        using (var fixture = new ProtocolFixture { RejectCount = true })
        {
            var error = await FailureAsync(async () => { await fixture.PrepareAsync(Request("fixture.protocol", true)); });
            check(error is LlamaProtocolException && fixture.Generated is null && fixture.Pool.AvailableCount == 1,
                "actual_reasoning_unsupported_exact_count_fails_closed_without_generation_or_off_fallback");
        }
    }

    private sealed class Composition : IAsyncDisposable
    {
        internal readonly ModelExecutionAccounting Accounting = new(coverageComplete: true);
        internal readonly LlamaSlotPool Pool = new(2);
        internal readonly FakeProvider Provider;
        internal readonly RegisteredNativeGenerationService Service;
        internal readonly NativeRegistrationLifetime Lifetime;
        internal readonly ModelExecutionAccounting.ModelOwnership Ownership;
        internal Composition()
        {
            var binding = new AdapterBinding("fixture.registered", "fixture.reasoning.model", Guid.NewGuid().ToString("N"),
                LlamaPreparedGeneration.Adapter.AdapterId, LlamaPreparedGeneration.Adapter.Version);
            Provider = new(binding, Pool);
            var registry = new AdapterRegistry(); var fence = new NativeRegistrationFence();
            var off = LlamaPreparedGeneration.Adapter with { SupportsReasoning = false };
            registry.Register(binding.BackendId, binding.ModelId, binding.InstanceId, Provider, fence,
                [off, LlamaPreparedReasoningGeneration.Adapter]);
            Service = new(registry, binding, fence, 2048, "fixture.runtime", Accounting.ProcessGenerationId);
            Ownership = Accounting.Begin(ModelOwnershipKind.CapabilityLease, binding.ModelId, requiresAdmission: false);
            Ownership.Bind(instanceId: binding.InstanceId); Ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
            Lifetime = new(Service, fence, Ownership);
        }
        public async ValueTask DisposeAsync()
        {
            var error = await Lifetime.BeginRetirement().WaitAsync(TimeSpan.FromSeconds(3));
            if (error is not null) throw error;
        }
    }
    private sealed class FakeProvider(AdapterBinding binding, LlamaSlotPool pool) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter with { SupportsReasoning = true };
        internal readonly List<FakePrepared> Created = [];
        internal bool HoldGeneration, FailNextDispose;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            request.Validate(); token.ThrowIfCancellationRequested();
            var slot = await pool.AcquireAsync(() => { }, token);
            try
            {
                var selected = request.ReasoningTokens == 0 ? LlamaPreparedGeneration.Adapter : LlamaPreparedReasoningGeneration.Adapter;
                var metadata = LlamaPreparedGeneration.ResolveBudget(binding.InstanceId, selected, request, 3, 2048,
                    "Injected deterministic count; not a tokenizer/native proof.");
                var prepared = new FakePrepared(metadata, slot, FailNextDispose, HoldGeneration);
                FailNextDispose = false; Created.Add(prepared); return prepared;
            }
            catch { slot.Dispose(); throw; }
        }
    }
    private sealed class FakePrepared : IPreparedModelGeneration
    {
        private readonly LlamaSlotPool.Lease slot;
        private readonly bool failDispose, hold;
        private readonly CancellationTokenSource stopping = new();
        private Task<ModelGenerationResult>? generation;
        private int disposed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Allow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreparedModelMetadata Metadata { get; }
        internal FakePrepared(PreparedModelMetadata metadata, LlamaSlotPool.Lease slot, bool failDispose, bool hold)
        { Metadata = metadata; this.slot = slot; this.failDispose = failDispose; this.hold = hold; }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
            generation = GenerateCoreAsync(onText, token);
        private async Task<ModelGenerationResult> GenerateCoreAsync(Action<string>? onText, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
            Entered.TrySetResult();
            try
            {
                if (hold) await Allow.Task.WaitAsync(linked.Token);
                linked.Token.ThrowIfCancellationRequested(); onText?.Invoke("fixture visible");
                return new(ModelGenerationFinish.Stop, "fixture visible", 4, 3);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            { return new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true); }
            finally { slot.Dispose(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (failDispose) { slot.Quarantine("Injected fake provider cleanup unknown."); throw new IOException("injected cleanup unknown"); }
            stopping.Cancel();
            try { if (generation is not null) await generation; }
            finally { slot.Dispose(); stopping.Dispose(); }
        }
    }

    internal sealed class ProtocolFixture : HttpMessageHandler
    {
        internal const string Hidden = "synthetic-hidden-fixture-delta";
        internal const string Visible = "{\"action\":\"final\"}";
        internal readonly LlamaSlotPool Pool = new(1);
        internal readonly List<JsonElement> Counted = [];
        internal readonly TaskCompletionSource CountEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowCount = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldFirstCount, RejectCount, SplitDraft, IdleUnconfirmed;
        internal string? DraftVisible;
        internal string Scenario = "";
        internal int SlotReads;
        internal JsonElement? Generated;
        private readonly HttpClient http;
        internal ProtocolFixture()
        { http = new(this, disposeHandler: false) { BaseAddress = new Uri("http://fixture.invalid/") }; }
        internal async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request)
        {
            var slot = await Pool.AcquireAsync(() => { }, default);
            return await LlamaPreparedReasoningGeneration.PrepareAsync(http, slot, "fixture.protocol.instance", request,
                ["stop", "", "stop"], () => false, default,
                new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(5)));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/slots")
            {
                SlotReads++;
                return JsonResponse(new[] { new { id = 0, n_ctx = 2048, is_processing = IdleUnconfirmed && SlotReads > 1 } });
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var frozen = body.RootElement.Clone();
            if (path == "/v1/chat/completions/input_tokens")
            {
                Counted.Add(frozen); CountEntered.TrySetResult();
                if (HoldFirstCount && Counted.Count == 1) await AllowCount.Task.WaitAsync(token);
                if (RejectCount) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{}}") };
                return JsonResponse(new { @object = "response.input_tokens", input_tokens = 1700 });
            }
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected in-memory fixture endpoint.");
            Generated = frozen;
            var input = Scenario == "input-mismatch" ? 1701 : 1700;
            var output = Scenario == "length" ? frozen.GetProperty("max_tokens").GetInt32() :
                Scenario == "usage-overflow" ? frozen.GetProperty("max_tokens").GetInt32() + 1 : 7;
            var visible = Scenario switch
            {
                "tagged" => "{\"action\":\"<think>bad</think>\"}",
                "escaped-tagged" => "{\"action\":\"final\",\"text\":\"\\u003cthink\\u003esynthetic\\u003c/think\\u003e\"}",
                "malformed" => "{\"action\":\"final\",\"text\":",
                _ => DraftVisible ?? Visible
            };
            var finish = Scenario == "length" ? "length" : "stop";
            string Frame(object delta, string? reason) => "data: " + JsonSerializer.Serialize(new
                { id = "fixture-completion", choices = new[] { new { index = 0, delta, finish_reason = reason } } }) + "\n\n";
            var split = Math.Max(1, visible.Length - 5);
            var publicFrames = SplitDraft
                ? Frame(new { content = visible[..split] }, null) + Frame(new { content = visible[split..] }, null)
                : Frame(new { content = visible }, null);
            var invalidFrame = Scenario == "protocol"
                ? "data: {\"id\":\"changed-completion\",\"choices\":[{\"index\":0,\"delta\":{}}]}\n\n" : "";
            var stream = Frame(new { reasoning_content = Hidden }, null) +
                publicFrames + invalidFrame + Frame(new { }, finish) +
                "data: " + JsonSerializer.Serialize(new { id = "fixture-completion", choices = Array.Empty<object>(),
                    usage = new { prompt_tokens = input, completion_tokens = output, total_tokens = input + output } }) +
                "\n\ndata: [DONE]\n\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
        private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { AllowCount.TrySetResult(); http.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
