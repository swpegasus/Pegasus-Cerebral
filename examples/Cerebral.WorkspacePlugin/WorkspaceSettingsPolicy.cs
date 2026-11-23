using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.PackageSettings;

namespace Cerebral.WorkspacePlugin;

internal sealed record WorkspacePreferences(bool PlainResponses = false);

/// <summary>One global display preference; no draft, clipboard or conversation content is stored.</summary>
internal sealed class WorkspaceSettingsPolicy : IPluginCompleteSettingsPolicy
{
    internal const string PluginId = "pegasus.workspace-actions";
    internal const string PackageVersion = "1.0.0";
    internal const int MaximumBytes = 4096;
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            PackagePreferenceJson.Document(incoming, PluginId);
            // Portable restore must preserve destination fields this package version cannot interpret.
            if (portable && currentValues is { } current) _ = Decode(current);
            return new(true, 1, Encode(Decode(incoming.Values)));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, 1, JsonSerializer.SerializeToElement(new { }), "workspace-settings-unsupported"); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    { PackagePreferenceJson.Document(current, PluginId); return new(1, Encode(Decode(current.Values))); }
    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    { PackagePreferenceJson.Document(current, PluginId); return new(1, Encode(Decode(current.Values))); }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) => Admit(incoming, currentValues, false);
    internal static WorkspacePreferences Decode(JsonElement raw)
    {
        PackagePreferenceJson.Shape(raw, MaximumBytes, "plainResponses");
        var value = raw.GetProperty("plainResponses");
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw PackagePreferenceJson.Unsupported();
        return new(value.GetBoolean());
    }
    internal static JsonElement Encode(WorkspacePreferences value) => JsonSerializer.SerializeToElement(new { plainResponses = value.PlainResponses });
}
