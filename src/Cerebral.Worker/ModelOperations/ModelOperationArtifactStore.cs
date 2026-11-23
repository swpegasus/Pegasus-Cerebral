using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Worker.ModelOperations;

/// <summary>New binary-artifact budget; ordinary attachment admission is deliberately unchanged.</summary>
public sealed record ModelArtifactBudget(long FileBytes = 512L * 1024 * 1024,
    long BatchBytes = 2L * 1024 * 1024 * 1024, long ProfileBytes = 8L * 1024 * 1024 * 1024,
    long WorkerBytes = 32L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (FileBytes <= 0 || FileBytes > ModelOperationContract.MaximumArtifactBytes ||
            BatchBytes < FileBytes || BatchBytes > ModelOperationContract.MaximumJobArtifactBytes ||
            ProfileBytes < BatchBytes || WorkerBytes < ProfileBytes)
            throw new ArgumentException("Model artifact budgets must be positive and nested file/batch/profile/worker limits.");
    }
}

/// <summary>Public metadata never contains the Worker storage path or credentials.</summary>
public sealed record ModelArtifactInfo(string Id, int Revision, string WorkerInstallationId,
    string JobId, string EffectId, string Role, string MediaType, string MediaProfile,
    long Length, string Sha256, DateTimeOffset CreatedAt);

internal sealed record StoredModelArtifact(ModelArtifactInfo Info, string? OwnerProfileId, string RelativeFile);
internal sealed record ModelArtifactReservation(string Id, string WorkerInstallationId, string JobId,
    string EffectId, string? OwnerProfileId, string Role, string MediaType, string MediaProfile,
    long Length, string Sha256, DateTimeOffset CreatedAt, string State);

// The durable host batch, rather than an item-selected string, supplies aggregate quota ownership.
internal static class ModelOperationRecordKinds
{
    internal const string Batch = "model-operation-batch";
}

/// <summary>Bytes or reservation cleanup cannot be confirmed. The caller must retain uncertainty.</summary>
public sealed class ModelArtifactCleanupUncertainException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// Host-owned immutable artifacts. Reservations are durable and charged while staging; a lost
/// writer never makes its partial bytes visible or releases its quota without reconciliation.
/// </summary>
public sealed class ModelOperationArtifactStore
{
    private const string ArtifactKind = "model-operation-artifact";
    private const string ReservationKind = "model-operation-artifact-reservation";
    public const int MaximumRangeBytes = 256 * 1024;
    private readonly StateStore store;
    private readonly string root;
    private readonly ModelArtifactBudget budget;
    private readonly string installationId;

    public ModelOperationArtifactStore(StateStore store, string installationId, ModelArtifactBudget? budget = null)
    {
        this.store = store;
        this.installationId = Identifier(installationId);
        this.budget = budget ?? new();
        this.budget.Validate();
        root = Path.Combine(store.DirectoryPath, "Artifacts", "ModelOperations");
        Ordinary(root);
        Directory.CreateDirectory(root);
        Ordinary(root);
    }

    public async Task<ModelArtifactInfo> CommitAsync(string artifactId, string jobId, string effectId,
        string? ownerProfileId, string role, string mediaType, string mediaProfile, long length,
        string sha256, Stream source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        artifactId = Identifier(artifactId); jobId = Identifier(jobId); effectId = Identifier(effectId);
        if (ownerProfileId is not null) Identifier(ownerProfileId);
        role = Identifier(role);
        if (mediaType is not ("model/gltf-binary" or "image/png" or "image/jpeg" or "image/webp" or "application/json"))
            throw new ArgumentException("Unsupported model artifact media type.");
        if (mediaProfile is not ("cerebral.gltf.pbr/1" or "cerebral.image.preview/1" or "cerebral.model.provenance/1"))
            throw new ArgumentException("Unsupported model artifact media profile.");
        if (length <= 0 || length > budget.FileBytes || !IsDigest(sha256))
            throw new ArgumentException("Artifact requires a bounded length and lowercase SHA-256.");
        var staging = FilePath(artifactId + ".partial");
        var final = FilePath(artifactId + ".blob");
        var reservation = new ModelArtifactReservation(artifactId, installationId, jobId, effectId,
            ownerProfileId, role, mediaType, mediaProfile, length, sha256, DateTimeOffset.UtcNow, "staging");
        var existing = store.Locked(() =>
        {
            var previous = store.Get<StoredModelArtifact>(ArtifactKind, artifactId);
            if (previous is not null)
            {
                RequireIdentity(previous, reservation);
                return previous.Info;
            }
            if (store.Get<ModelArtifactReservation>(ReservationKind, artifactId) is not null)
                throw new InvalidOperationException("Artifact staging exists. Reconcile the original writer; never replay it automatically.");
            CheckQuota(reservation);
            store.Put(ReservationKind, artifactId, reservation);
            return null;
        });
        if (existing is not null) return existing;
        var created = false;
        try
        {
            Ordinary(staging); Ordinary(final);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                MaximumRangeBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                var buffer = new byte[MaximumRangeBytes];
                long count = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > length - count) throw new InvalidDataException("Artifact exceeded its admitted length.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    count += read;
                }
                if (count != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256)
                    throw new InvalidDataException("Artifact bytes differ from the admitted length or digest.");
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            Ordinary(staging); Ordinary(final);
            ModelOperationArtifactValidation.Validate(staging, mediaType, mediaProfile, token);
            await using (var admitted = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (admitted.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(admitted, token).ConfigureAwait(false)) != sha256)
                    throw new InvalidDataException("Artifact identity changed during media validation.");
            token.ThrowIfCancellationRequested();
            // Metadata is committed only after immutable bytes have been published. If the
            // process dies between these steps, the reservation retains the charge and identity.
            File.Move(staging, final, overwrite: false);
            var info = new ModelArtifactInfo(artifactId, 1, installationId, jobId, effectId, role,
                mediaType, mediaProfile, length, sha256, reservation.CreatedAt);
            store.Locked(() =>
            {
                var current = store.Get<ModelArtifactReservation>(ReservationKind, artifactId);
                if (current != reservation) throw new InvalidOperationException("Artifact reservation changed before commit.");
                store.Put(ArtifactKind, artifactId, new StoredModelArtifact(info, ownerProfileId, artifactId + ".blob"));
                store.Delete(ReservationKind, artifactId);
                return true;
            });
            return info;
        }
        catch (Exception original)
        {
            // A published file with no acknowledgement is uncertain, not a safe failed write.
            // Keep its durable reservation so quota and identity cannot be reused.
            if (created && !File.Exists(final))
            {
                try
                {
                    Ordinary(staging); Ordinary(final);
                    if (File.Exists(staging)) File.Delete(staging);
                    if (File.Exists(staging) || Directory.Exists(staging) || File.Exists(final) || Directory.Exists(final))
                        throw new IOException("Artifact partial cleanup is unconfirmed.");
                    store.Locked(() => { store.Delete(ReservationKind, artifactId); return true; });
                }
                catch (Exception cleanup)
                {
                    throw new ModelArtifactCleanupUncertainException("Artifact cleanup is unconfirmed; preserve its reservation and reconcile before replay.",
                        new AggregateException(original, cleanup));
                }
                throw;
            }
            throw new ModelArtifactCleanupUncertainException("Artifact publication or staging ownership is unconfirmed; preserve its reservation and reconcile before replay.", original);
        }
    }

    public ModelArtifactInfo Describe(string id, int revision, string? ownerProfileId)
        => Authorized(id, revision, ownerProfileId).Info;

    public async Task<byte[]> ReadAsync(string id, int revision, string? ownerProfileId, long offset,
        int count, CancellationToken token = default)
    {
        var item = Authorized(id, revision, ownerProfileId);
        if (offset < 0 || count is < 1 or > MaximumRangeBytes || offset > item.Info.Length || count > item.Info.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "Artifact range must fit the exact committed object and 256 KiB limit.");
        var path = FilePath(item.RelativeFile);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            MaximumRangeBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length != item.Info.Length) throw new InvalidDataException("Committed artifact length changed.");
        stream.Position = offset;
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Copies to a host-chosen job snapshot and rechecks the entire immutable identity.</summary>
    internal async Task CopyInputAsync(string id, int revision, string? ownerProfileId, string destination,
        CancellationToken token)
    {
        var item = Authorized(id, revision, ownerProfileId);
        await using var input = new FileStream(FilePath(item.RelativeFile), FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MaximumRangeBytes]; long length = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > item.Info.Length - length) throw new InvalidDataException("Committed artifact grew during input snapshot.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            length += read;
        }
        if (length != item.Info.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != item.Info.Sha256)
            throw new InvalidDataException("Committed artifact identity changed during input snapshot.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    public IReadOnlyList<ModelArtifactInfo> List(string jobId, string? ownerProfileId)
    {
        Identifier(jobId);
        return store.List<StoredModelArtifact>(ArtifactKind).Where(x => x.Info.JobId == jobId &&
            (ownerProfileId is null || x.OwnerProfileId == ownerProfileId)).Select(x => x.Info).ToArray();
    }

    private StoredModelArtifact Authorized(string id, int revision, string? ownerProfileId)
    {
        var value = store.Get<StoredModelArtifact>(ArtifactKind, Identifier(id)) ?? throw new KeyNotFoundException("Artifact not found.");
        if (value.Info.Revision != revision || value.Info.WorkerInstallationId != installationId)
            throw new InvalidOperationException("Artifact revision or Worker installation differs.");
        if (ownerProfileId is not null && ownerProfileId != value.OwnerProfileId)
            throw new UnauthorizedAccessException("Artifact belongs to another caller profile.");
        return value;
    }

    private void CheckQuota(ModelArtifactReservation next)
    {
        // Called inside the same StateStore transaction as reservation admission. Recompute scopes
        // from host durable identities, including all earlier items and unresolved staging. A
        // duplicate job or changed effect is corruption/ambiguity, never permission to pick a group.
        var scopes = new Dictionary<string, (string BatchId, string EffectId, string? OwnerProfileId)>(StringComparer.Ordinal);
        foreach (var batch in store.List<StoredModelOperationBatch>(ModelOperationRecordKinds.Batch))
        {
            Identifier(batch.Status.Id);
            foreach (var item in batch.Status.Items)
            {
                Identifier(item.JobId); Identifier(item.EffectId);
                if (!scopes.TryAdd(item.JobId, (batch.Status.Id, item.EffectId, batch.Status.OwnerProfileId)))
                    throw new InvalidDataException("A model artifact job has ambiguous durable batch ownership.");
            }
        }
        string Group(string jobId, string effectId, string? ownerProfileId)
        {
            if (!scopes.TryGetValue(jobId, out var scope)) return "input:" + jobId;
            if (scope.EffectId != effectId || scope.OwnerProfileId != ownerProfileId)
                throw new InvalidDataException("Artifact effect or owner differs from its durable host batch item.");
            return "batch:" + scope.BatchId;
        }
        var nextGroup = Group(next.JobId, next.EffectId, next.OwnerProfileId);
        var committed = store.List<StoredModelArtifact>(ArtifactKind).Select(x =>
            (Group: Group(x.Info.JobId, x.Info.EffectId, x.OwnerProfileId), x.OwnerProfileId, x.Info.Length));
        var staged = store.List<ModelArtifactReservation>(ReservationKind).Select(x =>
            (Group: Group(x.JobId, x.EffectId, x.OwnerProfileId), x.OwnerProfileId, x.Length));
        var all = committed.Concat(staged).ToArray();
        static long Total(IEnumerable<long> values) => values.Aggregate(0L, (sum, value) => checked(sum + value));
        if (Total(all.Select(x => x.Length)) > budget.WorkerBytes - next.Length ||
            Total(all.Where(x => x.OwnerProfileId == next.OwnerProfileId).Select(x => x.Length)) > budget.ProfileBytes - next.Length ||
            Total(all.Where(x => x.Group == nextGroup).Select(x => x.Length)) > budget.BatchBytes - next.Length)
            throw new InvalidOperationException("Model artifact quota exhausted, including retained staging. Remove or reconcile owned data explicitly.");
        var volume = new DriveInfo(Path.GetPathRoot(root)!);
        if (volume.AvailableFreeSpace < next.Length) throw new IOException("Insufficient free storage for the admitted artifact.");
    }

    private static void RequireIdentity(StoredModelArtifact previous, ModelArtifactReservation next)
    {
        var x = previous.Info;
        if (x.WorkerInstallationId != next.WorkerInstallationId || x.JobId != next.JobId || x.EffectId != next.EffectId ||
            previous.OwnerProfileId != next.OwnerProfileId || x.Role != next.Role || x.MediaType != next.MediaType ||
            x.MediaProfile != next.MediaProfile || x.Length != next.Length || x.Sha256 != next.Sha256)
            throw new InvalidOperationException("Artifact identity is already bound to different immutable content.");
    }

    private string FilePath(string name)
    {
        if (Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/')) throw new ArgumentException("Invalid artifact storage identity.");
        var path = Path.Combine(root, name); Ordinary(path); return path;
    }

    internal static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Use a bounded opaque model-operation identity.");
        if (value is "." or ".." || value.EndsWith('.')) throw new ArgumentException("Invalid model-operation identity.");
        return value;
    }
    internal static bool IsDigest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void Ordinary(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                // Attributes also detect dangling links hidden by File.Exists/Directory.Exists.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked model-operation storage paths are not admitted.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
