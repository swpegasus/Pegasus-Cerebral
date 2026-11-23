using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private static async Task<ModelRecord> ApplyModelMetadataAsync(ModelRecord model, bool preserveConfiguredContext, bool preserveDisplayName, CancellationToken ct)
    {
        var metadata = await Task.Run(() => GgufMetadataInspector.Inspect(model.Path, ct), ct);
        return ModelMetadataDefaults.Apply(model, metadata, preserveConfiguredContext, preserveDisplayName);
    }

    private async Task<List<ModelRecord>> ListModelsAsync(CancellationToken ct)
    {
        var models = store.List<ModelRecord>("model");
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            if (model.MetadataSource != "fallback" && !string.IsNullOrWhiteSpace(model.Architecture))
            {
                model.CapabilityStatus = [.. runtime.AssessCapabilities(model)];
                continue;
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(model.Path))
            {
                model.CapabilityStatus = [.. runtime.AssessCapabilities(model)];
                continue;
            }
            try
            {
                models[index] = await RefreshModelMetadataAsync(model, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                models[index] = store.Locked(() =>
                {
                    var current = store.Get<ModelRecord>("model", model.Id) ?? model;
                    if (current.MetadataSource == "fallback" || string.IsNullOrWhiteSpace(current.Architecture))
                    {
                        current.MetadataSource = "inspection failed";
                        current.MetadataWarning = "GGUF metadata inspection failed: " + ex.Message;
                        store.Put("model", current.Id, current);
                    }
                    return current;
                });
            }
            models[index].CapabilityStatus = [.. runtime.AssessCapabilities(models[index])];
        }
        return models;
    }

    private async Task<ModelRecord> InspectModelAsync(string id, CancellationToken ct)
    {
        var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
        var refreshed = await RefreshModelMetadataAsync(model, ct);
        refreshed.CapabilityStatus = [.. runtime.AssessCapabilities(refreshed)];
        return refreshed;
    }

    private async Task<ModelRecord> RefreshModelMetadataAsync(ModelRecord model, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = store.Get<ModelRecord>("model", model.Id) ?? throw new ArgumentException("Model not found.");
            var before = new FileInfo(snapshot.Path);
            var expectedPath = Path.GetFullPath(snapshot.Path);
            var expectedLength = before.Length;
            var expectedWrite = before.LastWriteTimeUtc;
            var inspected = CerebralJson.Read<ModelRecord>(CerebralJson.Element(snapshot));
            await ApplyModelMetadataAsync(inspected, preserveConfiguredContext: true, preserveDisplayName: true, ct);
            var after = new FileInfo(expectedPath);
            if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedWrite) continue;

            var committed = store.Locked<ModelRecord?>(() =>
            {
                var current = store.Get<ModelRecord>("model", snapshot.Id) ?? throw new ArgumentException("Model not found.");
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var currentFile = new FileInfo(current.Path);
                if (!Path.GetFullPath(current.Path).Equals(expectedPath, comparison) || !currentFile.Exists ||
                    currentFile.Length != expectedLength || currentFile.LastWriteTimeUtc != expectedWrite)
                    return null;

                CopyDetectedMetadata(current, inspected, snapshot);
                store.Put("model", current.Id, current);
                return current;
            });
            if (committed is null) continue;
            events.Publish("model.updated", null, committed);
            return committed;
        }
        throw new IOException("The model file or registration changed while its GGUF metadata was being inspected. Refresh and try again.");
    }

    private static void CopyDetectedMetadata(ModelRecord current, ModelRecord inspected, ModelRecord snapshot)
    {
        current.Architecture = inspected.Architecture;
        current.DeclaredType = inspected.DeclaredType;
        current.Quantization = inspected.Quantization;
        current.ParameterCount = inspected.ParameterCount;
        current.NativeContextTokens = inspected.NativeContextTokens;
        current.RecommendedContextTokens = inspected.RecommendedContextTokens;
        current.BlockCount = inspected.BlockCount;
        current.EmbeddingLength = inspected.EmbeddingLength;
        current.AttentionHeadCount = inspected.AttentionHeadCount;
        current.AttentionHeadCountKv = inspected.AttentionHeadCountKv;
        current.HasEmbeddedChatTemplate = inspected.HasEmbeddedChatTemplate;
        current.Purpose = inspected.Purpose;
        current.MetadataSource = inspected.MetadataSource;
        current.MetadataWarning = inspected.MetadataWarning;
        current.CapabilityStatus = [.. inspected.CapabilityStatus];
        if (string.IsNullOrWhiteSpace(snapshot.ChatTemplate) && string.IsNullOrWhiteSpace(current.ChatTemplate) && !string.IsNullOrWhiteSpace(inspected.ChatTemplate))
        {
            current.ChatTemplate = inspected.ChatTemplate;
            if (current.StopSequences.SequenceEqual(snapshot.StopSequences, StringComparer.Ordinal))
                current.StopSequences = [.. inspected.StopSequences];
        }
    }
}
