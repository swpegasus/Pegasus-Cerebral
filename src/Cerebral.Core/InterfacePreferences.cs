namespace Cerebral.Core;

using System.Globalization;

/// <summary>
/// Portable, service-owned interface choices. Window geometry and conversation-specific state stay local to the
/// desktop or conversation. Nullable values let an existing local desktop seed a new service without making a
/// restored default indistinguishable from an explicit user choice.
/// </summary>
public sealed class InterfacePreferences
{
    public int Revision { get; set; }
    public bool? EnterSends { get; set; }
    public bool? NavigationVisible { get; set; }
    public bool? PanelsVisible { get; set; }
    public string? SelectedSurface { get; set; }
    public string? AppearanceTheme { get; set; }
    public string? MessageTimeFormat { get; set; }
    public int? RecentChatLimit { get; set; }
}

public static class InterfacePreferencePolicy
{
    public const string EnterSends = "enterSends";
    public const string NavigationVisible = "navigationVisible";
    public const string PanelsVisible = "panelsVisible";
    public const string SelectedSurface = "selectedSurface";
    public const string AppearanceTheme = "appearanceTheme";
    public const string MessageTimeFormat = "messageTimeFormat";
    public const string RecentChatLimit = "recentChatLimit";
    public static readonly IReadOnlySet<string> Surfaces = new HashSet<string>(StringComparer.Ordinal)
        { "chat", "work", "files", "tools" };

    public static void Validate(InterfacePreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Revision < 0 || value.SelectedSurface is { } surface && !Surfaces.Contains(surface) ||
            value.AppearanceTheme is { } appearance && !AppearanceThemePolicy.Choices.Contains(appearance) ||
            value.MessageTimeFormat is { } time && !MessageTimeFormatPolicy.Choices.Contains(time) ||
            value.RecentChatLimit is { } recent && !RecentChatPolicy.IsValid(recent))
            throw new ArgumentException("Interface preferences contain an unsupported value.");
    }
}

/// <summary>Visible recent general chats only. Durable history, pins and project membership are untouched.</summary>
public static class RecentChatPolicy
{
    public const int DefaultLimit = 20;
    public const int MinimumLimit = 1;
    public const int MaximumLimit = 200;
    public static bool IsValid(int value) => value is >= MinimumLimit and <= MaximumLimit;
    public static int Effective(int? value) => value is null ? DefaultLimit :
        IsValid(value.Value) ? value.Value : throw new ArgumentException("Recent chat limit must be between 1 and 200.");
}

public static class MessageTimeFormatPolicy
{
    public const string TwelveHour = "12-hour";
    public const string TwentyFourHour = "24-hour";
    public static readonly IReadOnlySet<string> Choices = new HashSet<string>(StringComparer.Ordinal)
        { TwelveHour, TwentyFourHour };

    public static string Effective(string? value) => value is null ? TwelveHour :
        Choices.Contains(value) ? value : throw new ArgumentException("The message time format is unsupported.");

    public static string Format(DateTimeOffset value, string? preference) =>
        value.ToLocalTime().ToString(Effective(preference) == TwentyFourHour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
}

/// <summary>Stable built-in appearance IDs. Future skin sources can resolve their own definitions behind these consumers.</summary>
public static class AppearanceThemePolicy
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public static readonly IReadOnlySet<string> Choices = new HashSet<string>(StringComparer.Ordinal)
        { System, Light, Dark };

    public static string Effective(string? value) => value is null || value == System ? System :
        Choices.Contains(value) ? value : throw new ArgumentException("The appearance theme is unsupported.");
}
