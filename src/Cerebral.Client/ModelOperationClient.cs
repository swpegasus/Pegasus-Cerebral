using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed record ModelOperationConnection(string Identity, long Generation);
public sealed record ModelOperationClientSnapshot(ModelOperationConnection Connection,
    ModelOperationSubmissionRequest? Request = null, ModelOperationBatchStatus? Batch = null,
    bool Busy = false, bool Uncertain = false, string? Error = null);
public sealed class ModelOperationConnectionChangedException() : InvalidOperationException(
    "The captured Cerebral owner connection changed. Inspect the original service/request; this client will not reroute.");

/// <summary>
/// One captured owner transport. Mutable requests are frozen before dispatch and retained after
/// uncertain acknowledgement. The owning UI supplies the exact connection-generation fence.
/// </summary>
public sealed class ModelOperationClient
{
    public const int MaximumArtifactRangeBytes = 256 * 1024;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly Func<bool> isCurrent;
    private readonly object gate = new();
    private readonly SemaphoreSlim submission = new(1, 1);
    private ModelOperationClientSnapshot snapshot;
    private long selection;
    public event EventHandler? Changed;

    public ModelOperationClient(CerebralClient owner, ModelOperationConnection connection, Func<bool> isCurrent)
        : this((request, token) => owner.CommandAsync(request.Operation, request.Arguments,
            cancellationToken: token), connection, isCurrent) { ArgumentNullException.ThrowIfNull(owner); }
    public ModelOperationClient(Func<CommandRequest, CancellationToken, Task<CommandResult>> command,
        ModelOperationConnection connection, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.Identity) || connection.Identity.Length > 256 || connection.Identity.Any(char.IsControl) || connection.Generation < 1)
            throw new ArgumentException("An exact captured owner connection and generation are required.");
        this.command = command ?? throw new ArgumentNullException(nameof(command));
        this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        snapshot = new(connection);
    }
    public ModelOperationClientSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool ConnectionCurrent => isCurrent();

    public async Task<ModelOperationCatalog> ListAsync(CancellationToken token = default)
    {
        var catalog = Read<ModelOperationCatalog>(await CallAsync("models.operations.list", new { }, token).ConfigureAwait(false));
        ValidateCatalog(catalog); return catalog;
    }
    public async Task<ModelOperationDescription> DescribeAsync(string modelId, CancellationToken token = default)
    {
        Identifier(modelId);
        var description = Read<ModelOperationDescription>(await CallAsync("models.operations.describe", new { modelId }, token).ConfigureAwait(false));
        ValidateCatalog(new(description.Version, [description.Model]));
        if (description.Model.ModelId != modelId || description.SettingsSchema.ValueKind != JsonValueKind.Object ||
            description.SettingsSchema.GetRawText().Length > 65_536) throw new InvalidDataException("Invalid model settings description.");
        return description;
    }
    public async Task<IReadOnlyList<ModelOperationBatchStatus>> HistoryAsync(int limit = 100, CancellationToken token = default)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = Read<ModelOperationBatchStatus[]>(await CallAsync("models.operations.history", new { limit }, token).ConfigureAwait(false));
        if (result.Length > limit) throw new InvalidDataException("Model history exceeded its requested bound.");
        foreach (var batch in result) ValidateBatch(batch);
        return Array.AsReadOnly(result);
    }

    public async Task<ModelOperationClientSnapshot> SubmitAsync(ModelOperationSubmissionRequest request, CancellationToken token = default)
    {
        var frozen = FreezeSubmission(ModelOperationContract.Freeze(request));
        EnsureCurrent();
        if (!await submission.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A batch submission is already waiting for acknowledgement.");
        var dispatched = false;
        try
        {
            if (Snapshot.Uncertain) throw new InvalidOperationException("Reconcile the original uncertain request before submitting another batch.");
            Select(Snapshot with { Request = frozen, Batch = null, Busy = true, Uncertain = false, Error = null });
            EnsureCurrent(); dispatched = true;
            var batch = Read<ModelOperationBatchStatus>(await CallAsync("models.operations.submit", frozen, token).ConfigureAwait(false));
            ValidateBatch(batch, frozen);
            Publish(Snapshot with { Batch = FreezeBatch(batch), Uncertain = false, Error = null });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { Uncertain = Snapshot.Uncertain || dispatched, Error = SafeError(error) });
            throw;
        }
        finally { Publish(Snapshot with { Busy = false }); submission.Release(); }
        return Snapshot;
    }

    /// <summary>Reads the original batch or request index; missing does not clear an uncertain submission.</summary>
    public async Task<ModelOperationClientSnapshot> RefreshAsync(CancellationToken token = default)
    {
        var (current, generation) = Capture();
        if (current.Batch is null && current.Request is null) throw new InvalidOperationException("No batch/request is selected.");
        try
        {
            var arguments = current.Batch is { } batch ? (object)new { id = batch.Id } : new { requestId = current.Request!.RequestId };
            var data = await CallAsync("models.operations.get", arguments, token).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Null) return Snapshot;
            var observed = Read<ModelOperationBatchStatus>(data);
            ValidateBatch(observed, current.Request);
            if (current.Batch is not null && current.Batch.Id != observed.Id) throw new InvalidDataException("Batch identity changed during inspection.");
            UpdateSelected(generation, value => value with { Batch = FreezeBatch(observed), Uncertain = false, Error = null });
        }
        catch (Exception error) { Publish(Snapshot with { Error = SafeError(error) }); throw; }
        return Snapshot;
    }

    public async Task<ModelOperationClientSnapshot> OpenAsync(string batchId, CancellationToken token = default)
    {
        Identifier(batchId); EnsureCurrent();
        var (_, generation) = Capture();
        if (Snapshot.Busy || Snapshot.Uncertain) throw new InvalidOperationException("Reconcile the current pending request before opening another batch.");
        var batch = Read<ModelOperationBatchStatus>(await CallAsync("models.operations.get", new { id = batchId }, token).ConfigureAwait(false));
        ValidateBatch(batch);
        if (batch.Id != batchId) throw new InvalidDataException("The service returned another batch.");
        Select(Snapshot with { Request = null, Batch = FreezeBatch(batch), Error = null }, generation, requireIdle: true);
        return Snapshot;
    }

    public async Task<ModelOperationClientSnapshot> CancelAsync(CancellationToken token = default)
    {
        var current = Snapshot;
        var batch = current.Batch ?? throw new InvalidOperationException("Resolve the original request to a batch before cancelling it.");
        if (!await submission.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A model-operation mutation is already waiting for acknowledgement.");
        Publish(Snapshot with { Busy = true });
        try
        {
            var observed = Read<ModelOperationBatchStatus>(await CallAsync("models.operations.cancel", new { id = batch.Id }, token).ConfigureAwait(false));
            ValidateBatch(observed, current.Request);
            if (observed.Id != batch.Id) throw new InvalidDataException("Cancellation returned another batch.");
            Publish(Snapshot with { Batch = FreezeBatch(observed), Error = null });
        }
        catch (Exception error) { Publish(Snapshot with { Uncertain = true, Error = SafeError(error) }); throw; }
        finally { Publish(Snapshot with { Busy = false }); submission.Release(); }
        return Snapshot;
    }

    /// <summary>Explicit new work for failed items only. Unknown, cancelled and successful items are never replayed.</summary>
    public async Task<ModelOperationSubmissionRequest> CreateFailedRetryAsync(string newRequestId, CancellationToken token = default)
    {
        Identifier(newRequestId); EnsureCurrent();
        if (Snapshot.Busy || Snapshot.Uncertain) throw new InvalidOperationException("Reconcile the selected batch before retrying any failed items.");
        var (captured, generation) = Capture();
        var batch = captured.Batch ?? throw new InvalidOperationException("Select a completed batch first.");
        ValidateBatch(batch);
        if (newRequestId == batch.RequestId) throw new ArgumentException("A failed-item retry requires a new request identity.");
        var failed = batch.Items.Where(x => x.State == ModelOperationState.Failed).Select(x => x.ItemId).ToHashSet(StringComparer.Ordinal);
        if (failed.Count == 0) throw new InvalidOperationException("The batch has no explicitly failed items.");
        var original = captured.Request ?? Read<ModelOperationSubmissionRequest>(await CallAsync("models.operations.request", new { id = batch.Id }, token).ConfigureAwait(false));
        RequireSelection(generation);
        ModelOperationContract.Validate(original); ValidateBatch(batch, original);
        var items = original.Items.Where(x => failed.Contains(x.ItemId)).Select(x => x with { ItemId = Guid.NewGuid().ToString("N") }).ToArray();
        var result = ModelOperationContract.Freeze(original with { RequestId = newRequestId, Items = items });
        return result;
    }

    /// <summary>Writes only to a caller-owned fresh staging stream. Caller publishes it after successful verification.</summary>
    public async Task DownloadAsync(ModelOperationArtifactReference reference, Stream destination, CancellationToken token = default)
    {
        ModelOperationContract.Validate(reference);
        if (!destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0)
            throw new ArgumentException("Use a fresh seekable staging stream; the client does not overwrite or publish a destination.");
        var actual = Read<ModelOperationArtifactReference>(await CallAsync("resources.model-artifacts.describe",
            new { id = reference.ArtifactId, revision = reference.Revision }, token).ConfigureAwait(false));
        ModelOperationContract.Validate(actual);
        if (actual != reference) throw new InvalidDataException("Artifact metadata no longer identifies the selected immutable object.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < reference.Bytes)
        {
            var count = (int)Math.Min(MaximumArtifactRangeBytes, reference.Bytes - offset);
            var data = await CallAsync("resources.model-artifacts.read", new { id = reference.ArtifactId, revision = reference.Revision, offset, count }, token).ConfigureAwait(false);
            var bytes = CerebralJson.Read<byte[]>(data);
            if (bytes.Length != count) throw new InvalidDataException("The artifact range did not match the exact requested interval.");
            EnsureCurrent();
            await destination.WriteAsync(bytes, token).ConfigureAwait(false); hash.AppendData(bytes); offset += bytes.Length;
        }
        await destination.FlushAsync(token).ConfigureAwait(false); EnsureCurrent();
        if (offset != reference.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Sha256)
            throw new InvalidDataException("The downloaded artifact did not match its committed length and SHA-256. Preserve staging; do not publish it.");
    }

    private async Task<JsonElement> CallAsync(string operation, object arguments, CancellationToken token)
    {
        EnsureCurrent();
        var result = await command(new(operation, CerebralJson.Element(arguments)), token).ConfigureAwait(false);
        EnsureCurrent();
        if (!result.Success) throw new InvalidOperationException(OperatorMcpRegistry.RedactDiagnostic(result.Error ?? "Cerebral rejected the model operation."));
        return result.Data.Clone();
    }
    private void EnsureCurrent() { if (!isCurrent()) throw new ModelOperationConnectionChangedException(); }
    private (ModelOperationClientSnapshot Snapshot, long Generation) Capture() { lock (gate) return (snapshot, selection); }
    private void RequireSelection(long generation)
    {
        lock (gate) if (selection != generation) throw new InvalidOperationException("The selected model batch changed while this response was in flight; current work was preserved.");
    }
    private void Select(ModelOperationClientSnapshot value, long? expected = null, bool requireIdle = false)
    {
        lock (gate)
        {
            if (expected is not null && selection != expected || requireIdle && (snapshot.Busy || snapshot.Uncertain))
                throw new InvalidOperationException("The selected batch changed or has pending work; current work was preserved.");
            selection++; Volatile.Write(ref snapshot, value);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateSelected(long expected, Func<ModelOperationClientSnapshot, ModelOperationClientSnapshot> update)
    {
        lock (gate)
        {
            if (selection != expected) throw new InvalidOperationException("The selected model batch changed while this response was in flight; current work was preserved.");
            Volatile.Write(ref snapshot, update(snapshot));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Publish(ModelOperationClientSnapshot value)
    {
        lock (gate) Volatile.Write(ref snapshot, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private static T Read<T>(JsonElement data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data.GetRawText());
        if (bytes.Length > ModelOperationContract.MaximumRequestBytes) throw new InvalidDataException("Model-operation response exceeds its bound.");
        if (typeof(T) == typeof(ModelOperationSubmissionRequest) || typeof(T) == typeof(ModelOperationArtifactReference))
            return ModelOperationContract.Read<T>(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate model-operation response field.");
        }
        return JsonSerializer.Deserialize<T>(bytes, ModelOperationContract.Json) ?? throw new InvalidDataException("Model-operation response is null.");
    }
    private static ModelOperationSubmissionRequest FreezeSubmission(ModelOperationSubmissionRequest value) => value with
    {
        Items = Array.AsReadOnly(value.Items.Select(x => x with { Inputs = Array.AsReadOnly(x.Inputs.ToArray()) }).ToArray())
    };
    private static ModelOperationBatchStatus FreezeBatch(ModelOperationBatchStatus value) => value with
    {
        Items = Array.AsReadOnly(value.Items.Select(x => x with { Result = x.Result is null ? null : x.Result with
            { Outputs = Array.AsReadOnly(x.Result.Outputs.ToArray()) } }).ToArray())
    };
    private static string SafeError(Exception error)
    {
        var message = OperatorMcpRegistry.RedactDiagnostic(error.Message);
        return message.Length <= 2048 ? message : "The model-operation client could not confirm a bounded result.";
    }
    private static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '+' or '-')))
            throw new ArgumentException("A bounded model-operation identity is required.");
    }
    private static void ValidateCatalog(ModelOperationCatalog value)
    {
        if (value.Version != ModelOperationContract.Version || value.Models is null || value.Models.Count > 512 || value.Models.Any(x => x is null) || value.Models.Select(x => x.ModelId).Distinct().Count() != value.Models.Count)
            throw new InvalidDataException("Invalid bounded model-operation catalog.");
        foreach (var model in value.Models)
        {
            Identifier(model.ModelId); Identifier(model.WorkerInstallationId);
            if (string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 256 || string.IsNullOrWhiteSpace(model.State) || model.State.Length > 64 ||
                model.Profiles is null || model.Profiles.Count > 64 || model.UnavailableReason?.Length > 2048)
                throw new InvalidDataException("Invalid model-operation readiness entry.");
            if (model.Runtime is not null) ModelOperationContract.Validate(model.Runtime);
            foreach (var profile in model.Profiles)
            {
                ModelOperationContract.Validate(profile.Profile);
                if (profile.Qualified && string.IsNullOrWhiteSpace(profile.QualificationId)) throw new InvalidDataException("Qualified profile is missing its qualification identity.");
            }
        }
    }
    public static void ValidateBatch(ModelOperationBatchStatus value, ModelOperationSubmissionRequest? request = null)
    {
        Identifier(value.Id); Identifier(value.RequestId); Identifier(value.ModelId);
        if (!ModelOperationContract.Operations.Contains(value.Operation) || value.RequestSha256.Length != 64 || value.RequestSha256.Any(x => !char.IsAsciiHexDigit(x) || char.IsUpper(x)) ||
            string.IsNullOrWhiteSpace(value.State) || value.State.Length > 64 || value.Items is null || value.Items.Count is < 1 or > ModelOperationContract.MaximumBatchItems ||
            value.Items.Any(x => x is null) || value.Items.Select(x => x.ItemId).Distinct().Count() != value.Items.Count ||
            value.Items.Select(x => x.JobId).Distinct().Count() != value.Items.Count || value.Items.Select(x => x.EffectId).Distinct().Count() != value.Items.Count ||
            value.CreatedAt == default || value.UpdatedAt < value.CreatedAt)
            throw new InvalidDataException("Invalid model-operation batch status.");
        foreach (var item in value.Items)
        {
            Identifier(item.ItemId); Identifier(item.JobId); Identifier(item.EffectId);
            if (!Enum.IsDefined(item.State) || item.Stage?.Length > 128 || item.Error?.Message.Length > 2048)
                throw new InvalidDataException("Invalid model-operation item status.");
            if (item.State == ModelOperationState.Succeeded && item.Result is null)
                throw new InvalidDataException("Succeeded item is missing its exact result and admitted output references.");
            if (item.Result is { } result)
            {
                ModelOperationContract.Validate(result);
                if (result.JobId != item.JobId || result.EffectId != item.EffectId || result.Binding.ModelId != value.ModelId || result.State != item.State)
                    throw new InvalidDataException("Item result is unrelated to its exact model/job/effect.");
            }
        }
        if (request is not null)
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(ModelOperationContract.Write(request)));
            if (request.RequestId != value.RequestId || request.ModelId != value.ModelId || request.Operation != value.Operation || digest != value.RequestSha256 ||
                !request.Items.Select(x => x.ItemId).SequenceEqual(value.Items.Select(x => x.ItemId)))
                throw new InvalidDataException("The batch receipt does not match the immutable submitted request.");
        }
    }
}
