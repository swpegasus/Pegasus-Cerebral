using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.AutoPromptPlugin;

internal sealed record AutoPromptPreferences(bool Enabled, string? ProfileId, long? ProfileRevision)
{
    internal static AutoPromptPreferences Default => new(false, null, null);
    internal bool Configured => Enabled && ProfileId is not null && ProfileRevision is > 0;
    internal JsonElement Values() => JsonSerializer.SerializeToElement(new { enabled = Enabled, profileId = ProfileId, profileRevision = ProfileRevision });
    internal static AutoPromptPreferences Read(JsonElement value)
    {
        ExactFields(value, "enabled", "profileId", "profileRevision");
        var enabled = value.GetProperty("enabled").GetBoolean();
        var id = value.GetProperty("profileId"); var revision = value.GetProperty("profileRevision");
        if (id.ValueKind == JsonValueKind.Null && revision.ValueKind == JsonValueKind.Null) return new(enabled, null, null);
        if (id.ValueKind != JsonValueKind.String || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number < 1)
            throw new ArgumentException("Select an exact profile and revision together.");
        var text = id.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || text == "*")
            throw new ArgumentException("The profile reference is invalid.");
        return new(enabled, text, number);
    }
    internal static void ExactFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal))) throw new ArgumentException("Unsupported AutoPrompt preference fields.");
    }
}

internal sealed class AutoPromptSettingsPolicy : IPluginSettingsPolicy
{
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            Coordinate(incoming);
            AutoPromptPreferences value;
            if (portable)
            {
                AutoPromptPreferences.ExactFields(incoming.Values, "enabled");
                var current = currentValues is { } saved ? AutoPromptPreferences.Read(saved) : AutoPromptPreferences.Default;
                value = current with { Enabled = incoming.Values.GetProperty("enabled").GetBoolean() };
            }
            else value = AutoPromptPreferences.Read(incoming.Values);
            return new(true, 1, value.Values());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        { return new(false, 1, AutoPromptPreferences.Default.Values(), "AutoPrompt preferences have an unsupported coordinate, version or shape."); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        Coordinate(current);
        var value = AutoPromptPreferences.Read(current.Values);
        return new(1, JsonSerializer.SerializeToElement(new { enabled = value.Enabled }), value.ProfileId is null ? 0 : 2);
    }
    private static void Coordinate(PluginSettingsDocument document)
    {
        if (document.PluginId != AutoPromptPlugin.PackageId || document.Scope != "global" || document.ScopeId is not null ||
            (document.SourcePackageVersion is not ("1.0.0" or "1.0.1") && document.SourcePackageVersion != AutoPromptPlugin.PackageVersion) ||
            document.SettingsVersion != 1 ||
            document.Values.GetRawText().Length > 4096) throw new ArgumentException("Unsupported AutoPrompt settings coordinate.");
    }
}
