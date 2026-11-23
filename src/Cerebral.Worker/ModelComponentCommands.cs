using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private bool HasModelAssignments(string modelId)
    {
        if (store.ActiveJobs().Any(job => UsesModel(job, modelId))) return true;
        if (workflows is null) return false;
        long cursor = 0;
        do
        {
            // Bounded pages retain only accepted settings long enough to compare bindings.
            var page = workflows.Store.ListActivePage(8, cursor);
            foreach (var run in page.Runs)
            {
                // A cancelled uncertain effect remains visible until its receipt is reconciled, but it can
                // never resume model execution. Retaining that audit state must not pin the old model binding.
                if (run.CancelRequested) continue;
                var value = run.Submission.SettingsSnapshot;
                if (value.TryGetProperty("resolvedSettingsVersion", out _))
                {
                    var accepted = WorkerExecutionSnapshot.Read(value);
                    if (accepted.Profile.ModelWorkerId == "local" && accepted.Model?.Id == modelId ||
                        accepted.Compactor?.Profile.ModelWorkerId == "local" && accepted.Compactor.Model.Id == modelId) return true;
                }
                else if (Optional(value, "modelWorkerId") == "local" && Optional(value, "modelId") == modelId)
                    return true; // Older host-owned non-runtime snapshot; never consult the mutable current profile.
            }
            if (page.NextAfterRow is not { } next) return false;
            cursor = next;
        } while (true);
    }

    private async Task<ModelRecord> ConfigureModelComponentAsync(JsonElement args, CancellationToken ct)
    {
        if (args.EnumerateObject().Any(x => x.Name is not ("id" or "visionProjectorPath")) ||
            args.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1) ||
            !args.TryGetProperty("visionProjectorPath", out var path) || path.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("Model component configuration requires id and visionProjectorPath (absolute path or null to clear).");
        var id = Text(args, "id"); Configuration.ValidateId(id);
        var selected = ModelComponentConfiguration.NormalizePath(path.ValueKind == JsonValueKind.Null ? null : path.GetString());
        await modelFiles.WaitAsync(ct);
        try
        {
            var saved = await runtime.WithUnloadedModelAsync(id, current => store.Locked(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (HasModelAssignments(id))
                    throw new InvalidOperationException("Finish or cancel this model's assignments before changing its image component.");
                var updated = CerebralJson.Read<ModelRecord>(CerebralJson.Element(current));
                updated.VisionProjectorPath = selected;
                return SaveModel(updated);
            }), ct);
            events.Publish("model.updated", null, saved);
            events.Publish("agents.updated", null, new { modelId = saved.Id, budgetsResolved = true });
            return saved;
        }
        finally { modelFiles.Release(); }
    }

    private async Task<ModelRecord> DownloadModelComponentAsync(JsonElement args, CancellationToken ct)
    {
        if (args.EnumerateObject().Any(x => x.Name is not ("id" or "url" or "fileName")) ||
            args.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new ArgumentException("Image component download requires only id, url and fileName.");
        var id = Text(args, "id"); Configuration.ValidateId(id);
        var uri = new Uri(Text(args, "url"));
        if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use an HTTP(S) image component download without credentials in its URL.");
        var destination = ModelDestination(Text(args, "fileName"));

        await modelFiles.WaitAsync(ct);
        var created = false;
        try
        {
            _ = await runtime.WithUnloadedModelAsync(id, current =>
            {
                if (HasModelAssignments(id))
                    throw new InvalidOperationException("Finish or cancel this model's assignments before downloading its image component.");
                if (Path.GetFullPath(current.Path).Equals(destination,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("The image component must use a different file from the model weights.");
                return true;
            }, ct);

            await DownloadFileAsync(uri, destination, id + ":image-component", ct);
            created = true;
            var saved = await runtime.WithUnloadedModelAsync(id, current => store.Locked(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (HasModelAssignments(id))
                    throw new InvalidOperationException("Work was accepted while the image component downloaded. The downloaded component was not adopted; retry after that work finishes.");
                var updated = CerebralJson.Read<ModelRecord>(CerebralJson.Element(current));
                updated.VisionProjectorPath = destination;
                return SaveModel(updated);
            }), ct);
            events.Publish("model.updated", null, saved);
            events.Publish("agents.updated", null, new { modelId = saved.Id, budgetsResolved = true });
            return saved;
        }
        catch
        {
            if (created && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally { modelFiles.Release(); }
    }
}
