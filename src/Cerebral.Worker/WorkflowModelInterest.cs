using System.Text.Json;

namespace Cerebral.Worker;

/// <summary>Frozen model/compactor bindings, not mutable profile choices or tool activity text.</summary>
internal static class WorkflowModelInterest
{
    public static bool UsesLocalModel(JsonElement settings, string modelId)
    {
        if (settings.ValueKind != JsonValueKind.Object) return true;
        if (settings.TryGetProperty("profile", out var profile))
        {
            if (Matches(profile, modelId)) return true;
            return settings.TryGetProperty("compactor", out var compactor) && compactor.ValueKind != JsonValueKind.Null &&
                (compactor.ValueKind != JsonValueKind.Object || !compactor.TryGetProperty("profile", out var helper) || Matches(helper, modelId));
        }
        return Matches(settings, modelId); // Older host-owned admission snapshot.
    }

    private static bool Matches(JsonElement profile, string id)
    {
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("modelId", out var model) || model.ValueKind != JsonValueKind.String ||
            !profile.TryGetProperty("modelWorkerId", out var worker) || worker.ValueKind != JsonValueKind.String)
            return true;
        return worker.GetString() == "local" && model.GetString() == id;
    }
}
