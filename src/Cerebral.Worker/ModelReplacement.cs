using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<ModelRecord> LoadModelWithFileLockAsync(string id, CancellationToken ct)
    {
        await modelFiles.WaitAsync(ct);
        try { return await runtime.LoadAsync(id, ct); }
        finally { modelFiles.Release(); }
    }

    private bool UsesModel(AgentJob job, string id)
    {
        bool Uses(AgentProfile profile) => profile.ModelWorkerId == "local" && profile.ModelId == id ||
            profile.CompactionProfileId is { } compactorId && store.Get<AgentProfile>("agent", compactorId) is { ModelWorkerId: "local" } compactor && compactor.ModelId == id;
        return Uses(job.Profile) || store.Get<AgentProfile>("agent", job.ProfileId) is { } current && Uses(current);
    }

    private async Task<ModelRecord> ReplaceModelAsync(string id, string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Use an absolute source path on the service machine.");
        await modelFiles.WaitAsync(ct);
        string? staged = null;
        var published = false;
        try
        {
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            if (model.State is "loaded" or "loading") throw new InvalidOperationException("Unload this model before replacing its weights.");
            if (store.ActiveJobs().Any(x => UsesModel(x, id)))
                throw new InvalidOperationException("Finish or cancel this model's assignments before replacing it.");
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(model.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("The source is already the model's current file.");
            await ValidateGgufAsync(source, ct);
            staged = model.Path + "." + Guid.NewGuid().ToString("N") + ".replacement";
            await using (var input = File.OpenRead(source)) await ReceiveFileAsync(input, staged, input.Length, ct);
            return await runtime.WithUnloadedModelAsync(id, current => store.Locked(() =>
            {
                ct.ThrowIfCancellationRequested();
                // Admission and the final file commit share the store gate. Auto-load uses the
                // runtime lifecycle gate, so neither path can claim these weights during replacement.
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var related = store.List<ModelRecord>("model").Where(x => Path.GetFullPath(x.Path).Equals(Path.GetFullPath(current.Path), comparison)).ToList();
                var relatedIds = related.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                if (store.ActiveJobs().Any(x => relatedIds.Any(relatedId => UsesModel(x, relatedId))))
                    throw new InvalidOperationException("New work was accepted while staging. Finish or cancel it before replacing the model.");
                if (!Path.GetFullPath(current.Path).Equals(Path.GetFullPath(model.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("The model location changed while staging. Retry from its current registration.");
                var size = new FileInfo(staged).Length;
                // The same-directory rename publishes complete weights; no retained backup is generated.
                File.Move(staged, current.Path, true); published = true;
                foreach (var item in related)
                {
                    item.SizeBytes = size; item.SourceUrl = null; item.Error = null; item.State = "unloaded";
                    ModelMetadataDefaults.Apply(item, GgufMetadataInspector.Inspect(item.Path, ct), preserveConfiguredContext: true, preserveDisplayName: true);
                    store.Put("model", item.Id, item); events.Publish("model.updated", null, item);
                }
                return related.Single(x => x.Id == id);
            }), ct);
        }
        catch (Exception ex) when (published)
        {
            throw new IOException("The complete replacement weights were published, but the registration update could not finish. Inspect the model file and registration before retrying: " + ex.Message, ex);
        }
        finally { if (staged is not null && File.Exists(staged)) File.Delete(staged); modelFiles.Release(); }
    }
}
