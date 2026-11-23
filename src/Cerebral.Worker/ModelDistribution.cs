using System.Security.Cryptography;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Full file hashes, measured without loading a model or retaining component bytes.</summary>
internal static class ModelDistributionContent
{
    internal const long MaximumWeightsBytes = 256L * 1024 * 1024 * 1024;

    internal sealed record FileObservation(string Path, long Bytes, DateTime LastWriteAt, ModelContentIdentity Content)
    {
        internal void EnsureUnchanged()
        {
            var current = new FileInfo(Path); current.Refresh();
            if (!current.Exists || current.Length != Bytes || current.LastWriteTimeUtc != LastWriteAt)
                throw new IOException("Model content changed during verification. No distribution was confirmed; retry with stable source files.");
        }
    }

    internal sealed record Observation(ModelContentDescription Description, FileObservation Weights, FileObservation? Projector)
    {
        internal void EnsureUnchanged() { Weights.EnsureUnchanged(); Projector?.EnsureUnchanged(); }
    }

    internal static async Task<Observation> DescribeAsync(string installationId, ModelRecord model, CancellationToken token)
    {
        Configuration.ValidateId(model.Id);
        if (string.IsNullOrWhiteSpace(installationId)) throw new InvalidDataException("Model content requires a stable installation identity.");
        await CommandDispatcher.ValidateGgufAsync(model.Path, token);
        var weights = await ReadAsync(model.Path, MaximumWeightsBytes, token);
        var projector = ModelComponentConfiguration.NormalizePath(model.VisionProjectorPath);
        FileObservation? component = null;
        if (projector is not null)
        {
            ModelRuntime.ValidateProjector(projector);
            component = await ReadAsync(projector, ModelPackageContract.MaximumProjectorBytes, token);
        }
        token.ThrowIfCancellationRequested();
        weights.EnsureUnchanged(); component?.EnsureUnchanged();
        return new(new(ModelContentDescription.CurrentVersion, installationId, model.Id,
            weights.Content, component?.Content, DateTimeOffset.UtcNow), weights, component);
    }

    private static async Task<FileObservation> ReadAsync(string path, long maximumBytes, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Registered model content requires a fully qualified local file.");
        var file = new FileInfo(path); file.Refresh();
        if (!file.Exists || file.Length is < 4 || file.Length > maximumBytes)
            throw new InvalidDataException("Registered model component is missing or exceeds its admitted byte bounds.");
        var length = file.Length; var lastWriteAt = file.LastWriteTimeUtc;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = new byte[4];
        await input.ReadExactlyAsync(magic, token);
        if (!magic.AsSpan().SequenceEqual("GGUF"u8)) throw new InvalidDataException("Registered model component is not a GGUF file.");
        input.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        var observed = new FileObservation(path, length, lastWriteAt, new(length, hash));
        token.ThrowIfCancellationRequested(); observed.EnsureUnchanged();
        return observed;
    }

    internal static void Validate(ModelContentDescription? description, string installationId, string modelId)
    {
        if (description is null || description.Version != ModelContentDescription.CurrentVersion ||
            description.WorkerInstallationId != installationId || description.ModelId != modelId ||
            description.CheckedAt == default)
            throw new InvalidDataException("Worker content evidence does not match its verified installation, registered model or supported version. No distribution was confirmed.");
        Validate(description.Weights, MaximumWeightsBytes);
        if (description.Projector is not null) Validate(description.Projector, ModelPackageContract.MaximumProjectorBytes);
    }

    private static void Validate(ModelContentIdentity? identity, long maximum)
    {
        if (identity is null || identity.Bytes is < 4 || identity.Bytes > maximum ||
            identity.Sha256 is not { Length: 64 } || identity.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Worker content evidence contains an invalid measured size or SHA-256. No distribution was confirmed.");
    }

    internal static bool Matches(ModelContentDescription source, ModelContentDescription target) =>
        Same(source.Weights, target.Weights) && (source.Projector is null
            ? target.Projector is null : target.Projector is not null && Same(source.Projector, target.Projector));

    private static bool Same(ModelContentIdentity left, ModelContentIdentity right) =>
        left.Bytes == right.Bytes && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Candidate catalog metadata only narrows reads. Full matching component hashes prove reuse.</summary>
internal static class ModelDistributionCoordinator
{
    private const int MaximumCatalogModels = 256;
    private const int MaximumCandidateModels = 8;

    internal static async Task<ModelDistributionReceipt> VerifyExistingAsync(RemoteManagedWorkerSession owner,
        ModelDistributionContent.Observation source, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        owner.EnsureCurrent();
        var catalog = await owner.Client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: ct);
        if (catalog is null || catalog.Count > MaximumCatalogModels || catalog.Any(model => model is null) ||
            catalog.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            throw new InvalidDataException("Worker model catalog is invalid or exceeds its bounded management limit. No upload was attempted.");
        foreach (var model in catalog) Configuration.ValidateId(model.Id);
        var candidates = catalog.Where(model => model.SizeBytes == source.Description.Weights.Bytes)
            .OrderByDescending(model => model.Id == source.Description.ModelId).ToArray();
        if (candidates.Length > MaximumCandidateModels)
            throw new InvalidOperationException("More than eight installed models have the selected byte size. Select a worker with a narrower catalog before verifying distribution. No upload was attempted.");
        foreach (var model in candidates)
        {
            owner.EnsureCurrent();
            ModelContentDescription content;
            try
            {
                content = await owner.Client.GetAsync<ModelContentDescription>("models.content.describe", new { id = model.Id }, ct);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("This worker cannot provide registered-model content hashes. Update the standalone Worker before distributing a model or projector. No upload was attempted.", error);
            }
            ModelDistributionContent.Validate(content, owner.Management.WorkerInstallationId, model.Id);
            if (!ModelDistributionContent.Matches(source.Description, content)) continue;
            await owner.RevalidateAsync(ct);
            ct.ThrowIfCancellationRequested(); source.EnsureUnchanged();
            return new("ExistingVerified", owner.Registration.Id, owner.Management.WorkerInstallationId,
                source.Description.ModelId, model.Id, 0, false, content.Weights, content.Projector, content.CheckedAt);
        }
        throw new NotSupportedException("No installed model has the exact selected weights and image component hashes. A binary v1/v2 model package transfer is required; no upload, model load, default switch or target settings change was attempted.");
    }
}

public sealed partial class CommandDispatcher
{
    private async Task<ModelContentDescription> DescribeModelContentAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        RemoteWorkerManagement.RequireFields(args, ["id"]);
        var id = Text(args, "id"); Configuration.ValidateId(id);
        await modelFiles.WaitAsync(ct);
        try
        {
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Registered model not found.");
            if (model.Id != id) throw new InvalidDataException("Registered model identity does not match its storage key.");
            return (await ModelDistributionContent.DescribeAsync(configuration.Get().InstallationId, model, ct)).Description;
        }
        finally { modelFiles.Release(); }
    }
}
