using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.PackageSettings;

namespace Cerebral.ResourcePausePlugin;

/// <summary>Desired local model identity only; admission and restore never activate resources.</summary>
internal sealed class ResourcePauseSettingsPolicy : IPluginCompleteSettingsPolicy
{
    internal const string PluginId = "pegasus.resource-pause";
    internal const string PackageVersion = "1.1.5";
    internal const int MaximumBytes = 4096;
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            PackagePreferenceJson.Document(incoming, PluginId);
            ResourcePausePreferences value;
            if (portable)
            {
                PackagePreferenceJson.Shape(incoming.Values, MaximumBytes);
                value = currentValues is { } current ? Decode(current) : new(null);
            }
            else value = Decode(incoming.Values);
            return new(true, 1, Encode(value));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, 1, JsonSerializer.SerializeToElement(new { }), "resource-pause-settings-unsupported"); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId); _ = Decode(current.Values);
        return new(1, JsonSerializer.SerializeToElement(new { }), 1);
    }
    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId);
        return new(1, Encode(Decode(current.Values)));
    }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) =>
        Admit(incoming, currentValues, false);
    internal static ResourcePausePreferences Decode(JsonElement raw)
    {
        PackagePreferenceJson.Shape(raw, MaximumBytes, "preferredLocalModelId");
        var member = raw.GetProperty("preferredLocalModelId");
        if (member.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw PackagePreferenceJson.Unsupported();
        var value = new ResourcePausePreferences(member.GetString()); Validate(value); return value;
    }
    internal static JsonElement Encode(ResourcePausePreferences value)
    { Validate(value); return JsonSerializer.SerializeToElement(new { preferredLocalModelId = value.PreferredLocalModelId }); }
    private static void Validate(ResourcePausePreferences value)
    {
        if (value is null) throw PackagePreferenceJson.Unsupported();
        if (value.PreferredLocalModelId is { } id) ResourcePausePreferenceStore.ValidateId(id);
    }
}
