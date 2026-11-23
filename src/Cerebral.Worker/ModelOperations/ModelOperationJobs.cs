using System.Security.Cryptography;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Core;

namespace Cerebral.Worker.ModelOperations;

internal sealed record StoredModelOperationBatch(ModelOperationBatchStatus Status, ModelOperationSubmissionRequest Request);

/// <summary>A deliberate, effect-free host admission result, distinct from an uncertain runtime failure.</summary>
public sealed class ModelOperationAdmissionException(string code, string message, bool waiting = false) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool Waiting { get; } = waiting;
}

/// <summary>One host-issued active-operation reservation. The host retains the separate resident instance.
/// CompleteAsync must retain/quarantine ownership for an unknown runtime result.</summary>
public interface IModelOperationExecutionLease : IAsyncDisposable
{
    ModelOperationRuntimeBinding Binding { get; }
    IModelOperationRuntime Runtime { get; }
    Task CompleteAsync(ModelOperationResult result, CancellationToken token);
}

/// <summary>Same Worker policy owns grants, holds, qualified codecs, current capacity and model residency.
/// ValidateSubmission is effect-free. Prepare may load a model and must report uncertainty truthfully.</summary>
public interface IModelOperationJobAdmission
{
    void ValidateSubmission(ModelOperationSubmissionRequest request, string? ownerProfileId);
    Task<IModelOperationExecutionLease> PrepareAsync(ModelOperationSubmissionRequest batch,
        ModelOperationSubmissionItem item, ModelOperationItemStatus identity, string? ownerProfileId,
        CancellationToken token);
}

/// <summary>Durable asynchronous model batches, with one initial graphics operation active per Worker.
/// This module executes only through the host's shared admission/lifecycle seam, never its own policy.</summary>
public sealed class ModelOperationJobs
{
    private const string BatchKind = ModelOperationRecordKinds.Batch;
    private const string RequestKind = "model-operation-request";
    private readonly StateStore store;
    private readonly IModelOperationJobAdmission admission;
    private readonly EventHub events;
    private readonly object runningGate = new();
    private readonly SemaphoreSlim pump = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    public ModelOperationJobs(StateStore store, IModelOperationJobAdmission admission, EventHub events)
    { this.store = store; this.admission = admission; this.events = events; }

    public ModelOperationBatchStatus Submit(ModelOperationSubmissionRequest value, string? ownerProfileId)
    {
        var request = ModelOperationContract.Freeze(value);
        if (ownerProfileId is not null) ModelOperationArtifactStore.Identifier(ownerProfileId);
        var bytes = ModelOperationContract.Write(request);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var requestKey = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (ownerProfileId is null ? "owner" : "profile:" + ownerProfileId) + "\n" + request.RequestId)));
        var status = store.Locked(() =>
        {
            var previous = store.Get<string>(RequestKind, requestKey);
            if (previous is not null)
            {
                var found = Read(previous, ownerProfileId);
                if (found.Status.RequestSha256 != digest) throw new InvalidOperationException("Request identity already names different immutable batch content.");
                return found.Status;
            }
            admission.ValidateSubmission(request, ownerProfileId);
            var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            var items = request.Items.Select(item => new ModelOperationItemStatus(item.ItemId,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), ModelOperationState.Queued)).ToArray();
            var accepted = new ModelOperationBatchStatus(id, request.RequestId, digest, request.ModelId,
                request.Operation, ownerProfileId, "queued", false, now, now, items);
            store.Put(BatchKind, id, new StoredModelOperationBatch(accepted, request));
            store.Put(RequestKind, requestKey, id);
            return accepted;
        });
        events.Publish("models.operations.updated", status.Id, status);
        return status;
    }

    public ModelOperationBatchStatus Get(string id, string? ownerProfileId) => Read(id, ownerProfileId).Status;
    public ModelOperationSubmissionRequest GetRequest(string id, string? ownerProfileId)
        => ModelOperationContract.Freeze(Read(id, ownerProfileId).Request);
    public ModelOperationBatchStatus GetByRequest(string requestId, string? ownerProfileId)
    {
        ModelOperationArtifactStore.Identifier(requestId);
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (ownerProfileId is null ? "owner" : "profile:" + ownerProfileId) + "\n" + requestId)));
        var id = store.Get<string>(RequestKind, key) ?? throw new KeyNotFoundException("No accepted batch has this caller-scoped request identity.");
        return Read(id, ownerProfileId).Status;
    }
    public IReadOnlyList<ModelOperationBatchStatus> List(string? ownerProfileId, int limit = 100)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        return store.List<StoredModelOperationBatch>(BatchKind).Where(x => ownerProfileId is null || x.Status.OwnerProfileId == ownerProfileId)
            .OrderByDescending(x => x.Status.CreatedAt).Take(limit).Select(x => x.Status).ToArray();
    }

    public ModelOperationBatchStatus Cancel(string id, string? ownerProfileId)
    {
        var status = store.Locked(() =>
        {
            var value = Read(id, ownerProfileId);
            if (Terminal(value.Status.State) && value.Status.State != "unknown") return value.Status;
            var items = value.Status.Items.Select(x => x.State == ModelOperationState.Queued ?
                x with { State = ModelOperationState.Cancelled, Stage = "cancelled-before-dispatch" } : x).ToArray();
            var result = value.Status with { CancelRequested = true, Items = items, UpdatedAt = DateTimeOffset.UtcNow };
            result = result with { State = Summary(result) };
            store.Put(BatchKind, id, value with { Status = result }); return result;
        });
        lock (runningGate) if (running.TryGetValue(id, out var cancel)) cancel.Cancel();
        events.Publish("models.operations.updated", status.Id, status);
        return status;
    }

    /// <summary>Called once at startup. Dispatched work is never recreated after losing its controller.</summary>
    public void ReconcileInterrupted()
    {
        foreach (var value in store.List<StoredModelOperationBatch>(BatchKind))
        {
            if (!value.Status.Items.Any(x => Active(x.State))) continue;
            store.Locked(() =>
            {
                var current = Read(value.Status.Id, null);
                var items = current.Status.Items.Select(x => Active(x.State) ? x with
                { State = ModelOperationState.Unknown, Stage = "controller-reconciliation-required",
                  Error = new("worker_restart", "Controller stopped after dispatch. Inspect the exact runtime instance; no automatic replay.") } : x).ToArray();
                var status = current.Status with { Items = items, State = "unknown", UpdatedAt = DateTimeOffset.UtcNow };
                store.Put(BatchKind, status.Id, current with { Status = status }); return true;
            });
        }
    }

    /// <summary>One bounded scheduler tick; separate hosting keeps status/cancel independent of a long model call.</summary>
    public async Task<bool> RunNextAsync(CancellationToken token)
    {
        if (!await pump.WaitAsync(0, token).ConfigureAwait(false)) return false;
        try
        {
            var candidate = store.List<StoredModelOperationBatch>(BatchKind)
                .Where(x => !Terminal(x.Status.State) && !x.Status.CancelRequested)
                .OrderBy(x => x.Status.CreatedAt).FirstOrDefault(x => x.Status.Items.Any(i => i.State == ModelOperationState.Queued));
            if (candidate is null) return false;
            var claimed = store.Locked(() =>
            {
                var current = Read(candidate.Status.Id, null);
                if (Terminal(current.Status.State) || current.Status.CancelRequested) return null;
                var index = current.Status.Items.ToList().FindIndex(x => x.State == ModelOperationState.Queued);
                if (index < 0) return null;
                var items = current.Status.Items.ToArray();
                items[index] = items[index] with { State = ModelOperationState.Loading, Stage = "preparing" };
                var status = current.Status with { State = "running", Items = items, UpdatedAt = DateTimeOffset.UtcNow };
                var saved = current with { Status = status };
                store.Put(BatchKind, status.Id, saved);
                (StoredModelOperationBatch Batch, int Index)? result = (saved, index);
                return result;
            });
            if (claimed is null) return false;
            candidate = claimed.Value.Batch;
            var itemIndex = claimed.Value.Index;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (runningGate) running.Add(candidate.Status.Id, cancellation);
            IModelOperationExecutionLease? lease = null;
            ModelOperationRequest? request = null;
            var dispatched = false;
            try
            {
                var identity = candidate.Status.Items[itemIndex];
                var item = candidate.Request.Items[itemIndex];
                if (Read(candidate.Status.Id, null).Status.CancelRequested)
                {
                    UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = ModelOperationState.Cancelled, Stage = "cancelled-before-preparation" });
                    return true;
                }
                // Admission rechecks current ownership/holds/resources; a queued request is not a grant.
                lease = await admission.PrepareAsync(candidate.Request, item, identity,
                    candidate.Status.OwnerProfileId, cancellation.Token).ConfigureAwait(false);
                if (lease.Binding.ModelId != candidate.Request.ModelId)
                    throw new InvalidDataException("Runtime preparation selected a different model.");
                request = new(ModelOperationContract.Version, candidate.Request.RequestId, identity.JobId, identity.EffectId,
                    candidate.Request.Operation, lease.Binding, item.Inputs, item.Options);
                request = ModelOperationContract.Freeze(request);
                var latest = Read(candidate.Status.Id, null);
                if (latest.Status.CancelRequested) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = ModelOperationState.Running, Stage = "dispatching" });
                dispatched = true;
                var progress = new InlineProgress<ModelOperationProgress>(value =>
                {
                    ModelOperationContract.Validate(value);
                    if (value.JobId != request.JobId || value.EffectId != request.EffectId) throw new InvalidDataException("Runtime progress identity differs.");
                    if (!Active(value.State)) throw new InvalidDataException("Progress cannot substitute for an admitted terminal result.");
                    UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = value.State, Stage = value.Stage });
                });
                var result = ModelOperationContract.Freeze(await lease.Runtime.RunAsync(request, progress, cancellation.Token).ConfigureAwait(false));
                if (result.RequestId != request.RequestId || result.JobId != request.JobId || result.EffectId != request.EffectId || result.Binding != request.Binding)
                    throw new InvalidDataException("Runtime terminal result differs from the admitted operation identity.");
                if (result.State is not (ModelOperationState.Succeeded or ModelOperationState.Failed or ModelOperationState.Cancelled or ModelOperationState.Unknown))
                    throw new InvalidDataException("Runtime did not return a terminal outcome.");
                await lease.CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);
                UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = result.State, Stage = "terminal", Result = result, Error = result.Error });
            }
            catch (ModelOperationAdmissionException held) when (!dispatched && lease is null && held.Waiting)
            {
                var cancelled = Read(candidate.Status.Id, null).Status.CancelRequested;
                UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = cancelled ? ModelOperationState.Cancelled : ModelOperationState.Queued, Stage = null });
                Update(candidate.Status.Id, status => status with { WaitingReason = held.Code + ": " + held.Message });
                return false;
            }
            catch (Exception error)
            {
                var confirmed = !dispatched && lease is null && error is ModelOperationAdmissionException;
                if (request is not null && lease is not null && cancellation.IsCancellationRequested)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var result = ModelOperationContract.Freeze(await lease.Runtime.CancelAsync(new(ModelOperationContract.Version, request.RequestId,
                            request.JobId, request.EffectId, request.Binding), cleanup.Token).ConfigureAwait(false));
                        confirmed = result.Confirmed && result.JobId == request.JobId && result.EffectId == request.EffectId;
                    }
                    catch { confirmed = false; }
                }
                // Stopping inference cannot reconcile independently published/staged artifacts.
                for (Exception? cause = error; cause is not null; cause = cause.InnerException)
                    if (cause is ModelArtifactCleanupUncertainException) { confirmed = false; break; }
                var state = confirmed ? cancellation.IsCancellationRequested ? ModelOperationState.Cancelled : ModelOperationState.Failed : ModelOperationState.Unknown;
                var code = error is ModelOperationAdmissionException refusal ? refusal.Code : state == ModelOperationState.Unknown ? "runtime_outcome_unconfirmed" : "runtime_failed";
                if (confirmed && lease is not null && request is not null)
                {
                    try
                    {
                        await lease.CompleteAsync(new(ModelOperationContract.Version, request.RequestId, request.JobId,
                            request.EffectId, request.Binding, state, false, [], new(code, "Operation cancellation was confirmed.")), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { state = ModelOperationState.Unknown; code = "capacity_release_unconfirmed"; }
                }
                UpdateItem(candidate.Status.Id, itemIndex, x => x with { State = state, Stage = "terminal",
                    Error = new(code, state == ModelOperationState.Unknown ? "Execution or cleanup is unconfirmed. Preserve the original request and runtime identity; do not replay." : error.Message) });
            }
            finally
            {
                lock (runningGate) running.Remove(candidate.Status.Id);
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            }
            return true;
        }
        finally { pump.Release(); }
    }

    public bool HasActiveWork(string modelId) => store.List<StoredModelOperationBatch>(BatchKind).Any(x =>
        x.Status.ModelId == modelId && (x.Status.State == "unknown" || !Terminal(x.Status.State)));

    private StoredModelOperationBatch Read(string id, string? ownerProfileId)
    {
        var value = store.Get<StoredModelOperationBatch>(BatchKind, ModelOperationArtifactStore.Identifier(id)) ?? throw new KeyNotFoundException("Model batch not found.");
        if (ownerProfileId is not null && value.Status.OwnerProfileId != ownerProfileId) throw new UnauthorizedAccessException("Model batch belongs to another caller profile.");
        return value;
    }
    private void UpdateItem(string id, int index, Func<ModelOperationItemStatus, ModelOperationItemStatus> change) => Update(id, status =>
    {
        var items = status.Items.ToArray();
        // Delayed progress cannot revive a terminal item or overwrite its accepted receipt.
        if (!Active(items[index].State) && items[index].State != ModelOperationState.Queued) return status;
        items[index] = change(items[index]);
        return status with { Items = items, WaitingReason = null };
    });
    private void Update(string id, Func<ModelOperationBatchStatus, ModelOperationBatchStatus> change)
    {
        var status = store.Locked(() =>
        {
            var value = Read(id, null); var updated = change(value.Status);
            updated = updated with { State = Summary(updated), UpdatedAt = DateTimeOffset.UtcNow };
            store.Put(BatchKind, id, value with { Status = updated }); return updated;
        });
        events.Publish("models.operations.updated", id, status);
    }
    private static string Summary(ModelOperationBatchStatus status)
    {
        if (status.Items.Any(x => x.State == ModelOperationState.Unknown)) return "unknown";
        if (status.Items.Any(x => Active(x.State))) return status.CancelRequested ? "cancelling" : "running";
        if (status.Items.Any(x => x.State == ModelOperationState.Queued)) return "queued";
        if (status.Items.All(x => x.State == ModelOperationState.Succeeded)) return "succeeded";
        if (status.Items.All(x => x.State == ModelOperationState.Cancelled)) return "cancelled";
        return status.Items.Any(x => x.State == ModelOperationState.Succeeded) ? "partial" : "failed";
    }
    private static bool Terminal(string state) => state is "succeeded" or "partial" or "failed" or "cancelled" or "unknown";
    private static bool Active(ModelOperationState state) => state is ModelOperationState.Loading or ModelOperationState.Running or ModelOperationState.Exporting;
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
