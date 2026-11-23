using System.Net.Http.Json;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter;

    public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken) =>
        PrepareCoreAsync(request, cancellationToken);

    // Both ordinary and registered text paths use the same snapshot, native preparation,
    // accounting and usage wrapper. The optional target narrows acquisition only.
    private async Task<IPreparedModelGeneration> PrepareCoreAsync(ModelGenerationRequest request,
        CancellationToken cancellationToken, NativeRegistrationTarget? target = null,
        ModelExecutionPolicy? selectedPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Snapshot before the first await; a caller cannot mutate the measured messages/schema
        // while the model loads and silently change the submitted prompt.
        var text = selectedPolicy is null ? LlamaPreparedGeneration.TextSnapshot(request)
            : LlamaPreparedGeneration.TextSnapshot(request, selectedPolicy);
        request = request with
        {
            Messages = text.Select(message => new ModelInputMessage(message.Role, [new TextContentPart(message.Content)])).ToArray(),
            ResponseSchema = request.ResponseSchema?.Clone()
        };
        if (selectedPolicy is not null) request.Validate(selectedPolicy);
        var ownership = Accounting.Begin(ModelOwnershipKind.LocalPreparation, request.ModelId);
        var (instance, slot) = await AcquireSlotAsync(request.ModelId, cancellationToken, ownership, target,
            nativeCacheCompatible: request.ReasoningTokens == 0);
        try
        {
            IReadOnlyList<string> stops = target is null
                ? (store.Get<ModelRecord>("model", request.ModelId) ?? throw new ArgumentException("Model not found.")).StopSequences
                : target.StopSequences;
            ownership.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
            if (request.ReasoningTokens != 0) slot.InvalidateNativeCache("unsupported-reasoning-route");
            var prepared = request.ReasoningTokens == 0
                ? selectedPolicy is null
                    ? await LlamaPreparedGeneration.PrepareAsync(instance.Http, slot, instance.Id, request,
                        stops, () => HasExited(instance), cancellationToken)
                    : await LlamaPreparedGeneration.PrepareSelectedAsync(instance.Http, slot, instance.Id, request,
                        stops, () => HasExited(instance), selectedPolicy, cancellationToken)
                : await LlamaPreparedReasoningGeneration.PrepareAsync(instance.Http, slot, instance.Id, request,
                    stops, () => HasExited(instance), cancellationToken);
            // Optional draft binding must be captured before wrappers hide this private facet.
            RegisteredWorkflowDraftScope.Capture(prepared);
            var accounted = new AccountingPreparedGeneration(prepared, ownership, cancellationToken: cancellationToken);
            return TrackUsage(LocalModelExecutionPolicy.Forward(accounted, prepared));
        }
        catch { slot.Dispose(); throw; }
    }

    private static bool HasExited(Instance instance)
    {
        if (instance.Stopped) return true;
        try { return instance.Process.HasExited; }
        catch (InvalidOperationException) { return instance.Stopped; }
    }

    private async Task<(Instance Instance, LlamaSlotPool.Lease Slot)> AcquireSlotAsync(string id, CancellationToken token,
        ModelExecutionAccounting.ModelOwnership? ownership = null, NativeRegistrationTarget? target = null,
        bool nativeCacheCompatible = false)
    {
        using var cancellation = ownership?.ObserveCancellation(token) ?? default;
        Instance instance;
        try
        {
            instance = target is null
                ? await AcquireAsync(id, token)
                : await AcquireRegisteredInstanceAsync(target, id, token);
        }
        catch { ownership?.Complete(); throw; }
        ownership?.Bind(instanceId: instance.Id);
        var returned = 0;
        void Released()
        {
            if (Interlocked.Exchange(ref returned, 1) != 0) return;
            instance.Idle.Touch(); Interlocked.Decrement(ref instance.Busy); ownership?.Complete();
        }
        LlamaSlotPool.Lease? slot = null;
        try
        {
            slot = await instance.Slots.AcquireAsync(Released, token,
                cause => ownership?.Unknown("native-slot-quarantined"), nativeCacheCompatible: nativeCacheCompatible);
            ownership?.Bind(leaseId: slot.LeaseId);
            ownership?.NativeSlot(slot.LeaseId, slot.Id);
            CaptureInferenceAllocator(id, instance);
            // Optional diagnostics use the existing captured row and actual native lease.
            // Neither failed capture nor a disabled observer can change acquisition or return.
            if (nativeCacheCompatible)
            {
                var observation = ownership?.Owner.Observation;
                try
                {
                    if (observation is not null && ownership is not null &&
                        instance.Slots.NativeCacheOwner is { } owner)
                    {
                        observation.NativeCacheModel(owner);
                        if (observation.TryNativeCacheBinding(ownership.Id, owner) is { } binding)
                            slot.BindNativeCache(binding, observation.NativeCacheGeneration, observation.NativeCacheDecision);
                        else slot.InvalidateNativeCache("exact-entry-binding-unavailable");
                    }
                    else slot.InvalidateNativeCache("exact-entry-binding-unavailable");
                }
                catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
                {
                    slot.InvalidateNativeCache("capture-unavailable");
                    observation?.NativeCacheCaptureFailed();
                }
            }
            return (instance, slot);
        }
        catch
        {
            // Only a failed acquisition without a lease undoes Busy directly. Once a real
            // lease exists, its return/quarantine authority decides whether Released runs.
            if (slot is null) Released(); else slot.Dispose();
            throw;
        }
    }

    // The existing template-aware reasoning endpoint keeps its parsing/adoption rules. It uses
    // the same named slot allocator as prepared text requests, with confirmed cancellation.
    // It is deliberately not advertised as an exact prepared-token capability.
    private async Task<string> GenerateReasoningAsync(string id, IReadOnlyList<ModelMessage> messages,
        int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token,
        JsonElement? responseSchema, int reasoningTokens)
    {
        var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
        var body = ReasoningCompletion.CreateRequest(messages, maxTokens, temperature, json, responseSchema, reasoningTokens, model.StopSequences);
        var ownership = Accounting.Begin(ModelOwnershipKind.LocalPreparation, id);
        var (instance, slot) = await AcquireSlotAsync(id, token, ownership);
        ownership.Phase(ModelOwnershipPhase.Generating);
        using var transport = new CancellationTokenSource();
        var acknowledged = 0; var sent = false; var rejected = false;
        using var cancel = token.Register(() =>
        {
            if (Volatile.Read(ref acknowledged) != 0) transport.Cancel();
            else transport.CancelAfter(LlamaProtocolTiming.Default.AcceptanceAfterCancellation);
        });
        try
        {
            token.ThrowIfCancellationRequested();
            if (HasExited(instance)) throw new IOException("The prepared model instance is no longer running.");
            var status = await LlamaProtocol.SlotAsync(instance.Http, slot.Id, token);
            if (status.Processing)
            {
                slot.Quarantine("Reserved reasoning slot was already occupied. Wait for other work, then explicitly unload and reload the model.");
                throw new IOException("The reserved model slot was unexpectedly occupied; capacity was quarantined.");
            }
            slot.InvalidateNativeCache("unsupported-reasoning-route");
            body["id_slot"] = slot.Id; body["return_progress"] = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(body, options: CerebralJson.Options) };
            SkillDispatchFence.RequireCurrent();
            token.ThrowIfCancellationRequested(); sent = true;
            ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
            using var response = await instance.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transport.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var error = await LlamaProtocol.ReadJsonAsync(response.Content, 65_536, transport.Token);
                rejected = error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out _);
                throw new IOException("Model reasoning request failed with HTTP " + (int)response.StatusCode + ". Verify the runtime/model supports template-aware reasoning and structured output.");
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new IOException("Model did not return an inference event stream.");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(transport.Token));
            using var acknowledgedReader = new ReasoningAcknowledgementReader(reader, terminal =>
            {
                Volatile.Write(ref acknowledged, 1);
                if (token.IsCancellationRequested && !terminal) { transport.Cancel(); transport.Token.ThrowIfCancellationRequested(); }
            });
            return await ReasoningCompletion.ReadAsync(acknowledgedReader, json || responseSchema.HasValue, onToken, transport.Token);
        }
        finally
        {
            transport.Cancel();
            var confirmed = !sent || HasExited(instance) || ((Volatile.Read(ref acknowledged) != 0 || rejected) &&
                await LlamaProtocol.ConfirmIdleAsync(instance.Http, slot.Id, () => HasExited(instance), LlamaProtocolTiming.Default));
            if (!confirmed) slot.Quarantine("Reasoning stop could not be confirmed. Wait for other work, then explicitly unload and reload the model.");
            slot.Dispose();
            if (!confirmed) throw new IOException("Model cancellation could not be confirmed; its slot remains quarantined. Wait for other work, then explicitly unload and reload the model.");
        }
    }

    private sealed class ReasoningAcknowledgementReader(StreamReader reader, Action<bool> acknowledge) : TextReader
    {
        private readonly LlamaProtocol.BoundedLines lines = new(reader);
        private string? completionId;
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = await lines.ReadAsync(cancellationToken);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal) || line[5..].TrimStart() == "[DONE]") return line;
            using var frame = JsonDocument.Parse(line[5..]); var root = frame.RootElement;
            // OAI chat frames do not carry slot IDs. Its actual completion frame (never mere
            // headers) proves admission; all callers still send an exclusively reserved id_slot.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 and <= 256 } candidate &&
                root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1)
            {
                if (completionId is not null && completionId != candidate) throw new IOException("Model reasoning stream changed completion identity.");
                completionId = candidate;
                var choice = choices[0];
                if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    acknowledge(choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String);
            }
            return line;
        }
    }
}
