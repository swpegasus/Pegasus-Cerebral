using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

// Detached preparation shared by ordinary model saves and capacity choices. File/header
// observations are deliberately outside this pure calculation and never persisted implicitly.
internal sealed record PreparedModelSave(ModelRecord Model, IReadOnlyList<AgentProfile> Profiles)
{
    internal static PreparedModelSave Prepare(ModelRecord model, IEnumerable<AgentProfile> profiles,
        int? automaticContextTokens = null)
    {
        var copy = Copy(model);
        ValidateResourceSettings(copy);
        return new(copy, ModelProfileBudgets.ResolveDetached(profiles, copy, automaticContextTokens));
    }

    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, CerebralJson.Options), CerebralJson.Options)
        ?? throw new InvalidDataException("Model save inputs could not be copied.");

    internal static void ValidateResourceSettings(ModelRecord model)
    {
        if (model.ContextTokens is < 512 or > 1048576 || model.MaximumContextTokens is < 0 or > 1048576 ||
            model.MaximumContextTokens is > 0 and < 512 || model.ParallelRequests is < 1 or > 16 ||
            model.GpuLayers < -1 || model.IdleMinutes is < -1 or > 10080)
            throw new ArgumentException("Invalid model resource settings.");
        if (model.ChatTemplate?.Length > 32000 || model.ChatTemplate?.Contains('\0') == true ||
            model.StopSequences is null || model.StopSequences.Count > 16 ||
            model.StopSequences.Any(x => string.IsNullOrEmpty(x) || x.Length > 256 || x.Contains('\0')))
            throw new ArgumentException("Invalid chat template or stop sequences.");
    }

    // Existing configuration capability admission reads GGUF headers. It may update its detached
    // model argument; capacity callers pass a copy so existing unrelated metadata stays unchanged.
    internal static void ObserveCapabilities(PreparedModelSave prepared, bool preserveModelMetadata = true)
    {
        var observed = preserveModelMetadata ? Copy(prepared.Model) : prepared.Model;
        _ = ModelComponentConfiguration.NormalizePath(observed.VisionProjectorPath);
        ModelRuntime.ValidateProjector(observed.VisionProjectorPath);
        foreach (var profile in prepared.Profiles) ModelCapabilityPolicy.EnsureConfigured(profile, observed);
    }
}
