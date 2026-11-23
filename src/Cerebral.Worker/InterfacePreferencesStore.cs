using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Small owner settings record; field-level patches avoid clobbering independent desktop choices.</summary>
public sealed class InterfacePreferencesStore(StateStore store)
{
    private const string Kind = "settings";
    private const string Id = "interface";

    public InterfacePreferences Get() => store.Get<InterfacePreferences>(Kind, Id) ?? new();

    public InterfacePreferences Set(JsonElement input) => store.Locked(() =>
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("An interface preference patch requires exact name and value fields.");
        var properties = input.EnumerateObject().ToArray();
        var propertyNames = properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Length != 2 || propertyNames.Count != 2 || !propertyNames.SetEquals(["name", "value"]))
            throw new ArgumentException("An interface preference patch requires exact name and value fields.");
        var name = input.GetProperty("name");
        if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } key)
            throw new ArgumentException("The interface preference name must be a string.");
        var value = input.GetProperty("value");
        var current = Get();
        switch (key)
        {
            case InterfacePreferencePolicy.EnterSends:
                current.EnterSends = Bool(value); break;
            case InterfacePreferencePolicy.NavigationVisible:
                current.NavigationVisible = Bool(value); break;
            case InterfacePreferencePolicy.PanelsVisible:
                current.PanelsVisible = Bool(value); break;
            case InterfacePreferencePolicy.SelectedSurface:
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } surface ||
                    !InterfacePreferencePolicy.Surfaces.Contains(surface))
                    throw new ArgumentException("The selected interface surface is unsupported.");
                current.SelectedSurface = surface; break;
            case InterfacePreferencePolicy.AppearanceTheme:
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } appearance ||
                    !AppearanceThemePolicy.Choices.Contains(appearance))
                    throw new ArgumentException("The selected appearance theme is unsupported.");
                current.AppearanceTheme = appearance; break;
            case InterfacePreferencePolicy.MessageTimeFormat:
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } time ||
                    !MessageTimeFormatPolicy.Choices.Contains(time))
                    throw new ArgumentException("The selected message time format is unsupported.");
                current.MessageTimeFormat = time; break;
            case InterfacePreferencePolicy.RecentChatLimit:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var recent) ||
                    !RecentChatPolicy.IsValid(recent))
                    throw new ArgumentException("Recent chat limit requires a whole number between 1 and 200.");
                current.RecentChatLimit = recent; break;
            default:
                throw new ArgumentException("The interface preference name is unsupported.");
        }
        current.Revision = checked(current.Revision + 1);
        InterfacePreferencePolicy.Validate(current);
        store.Put(Kind, Id, current);
        return current;
    });

    private static bool Bool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException("The selected interface preference requires a Boolean value.")
    };
}
