using System.Text.Json;

namespace Cerebral.Orchestrator;

// Presentation facts only. A hold observation is never permission to resume or load anything.
internal static class SystemStatusPosture
{
    internal sealed record Hold(bool Active, bool EnginePaused, string State);
    internal sealed record Presentation(string Caption, string Details);

    internal static Hold? ReadHold(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 256 * 1024 ||
                value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
                value.GetProperty("contract").GetString() != "cerebral.owner-work-hold/1") return null;
            var state = value.GetProperty("state").GetString();
            if (state is not ("none" or "holding" or "held" or "resuming" or "cancelling" or "resumed" or "cancelled" or "faulted") ||
                value.GetProperty("generation").GetInt64() < 0 || value.GetProperty("revision").GetInt64() < 0 ||
                value.GetProperty("engineEpoch").GetInt64() < 0) return null;
            var active = state is "holding" or "held" or "resuming" or "cancelling" or "faulted";
            if (active && (!Guid.TryParseExact(value.GetProperty("holdId").GetString(), "N", out var id) || id == Guid.Empty ||
                value.GetProperty("generation").GetInt64() < 1 || value.GetProperty("revision").GetInt64() < 1)) return null;
            if (state == "none" && (value.GetProperty("holdId").ValueKind != JsonValueKind.Null ||
                value.GetProperty("generation").GetInt64() != 0 || value.GetProperty("revision").GetInt64() != 0)) return null;
            return new(active, value.GetProperty("enginePaused").GetBoolean(), state);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }

    internal static Presentation Show(Hold? hold, int? loadedModels)
    {
        var state = hold is { Active: true, State: "faulted" } ? "Work hold needs review" :
            hold is { Active: true } ? "Work hold active" : hold is { EnginePaused: true } ? "Engine paused" :
                hold is null ? "Hold status unavailable" : "Work hold off";
        var models = loadedModels switch
        { 0 => "No local model loaded", > 0 => loadedModels + " local model" + (loadedModels == 1 ? " loaded" : "s loaded"), _ => "Local model status unavailable" };
        var detail = hold is { Active: true } ? "An intentional work hold keeps queued work from starting. The hold has not been changed." :
            hold is { EnginePaused: true } ? "The engine is paused. Work remains paused." :
                hold is null ? "The current Worker did not provide a usable hold observation; no running or ready state is inferred." :
                    "No active intentional work hold was observed. This does not establish model readiness or reserve capacity.";
        detail += loadedModels == 0 ? " No local model is loaded, so zero local inference slots are expected." :
            loadedModels > 0 ? " Loaded models and free slots do not prove that inference is running." :
                " Loaded-model state is unknown; missing telemetry is not zero.";
        return new(state + " · " + models, detail);
    }
}
