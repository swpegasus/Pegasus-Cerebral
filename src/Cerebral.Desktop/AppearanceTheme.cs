using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows.Media;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Renderer color contract, independent of preset storage and the plugin's configuration view.</summary>
public sealed record DesktopThemeDefinition(
    string Id, Color Window, Color Surface, Color RaisedSurface, Color Ink, Color Muted, Color Accent,
    Color OnAccent, Color Line, Color Hover, Color Pressed, Color Selection, Color SelectionText,
    Color CodeSurface, Color ErrorBackground, Color ErrorText, Color ErrorBorder);

public static class DesktopThemeCatalog
{
    public static DesktopThemeDefinition Light => FromPreset(DesktopThemePresetPolicy.BlueWhite, false);
    public static DesktopThemeDefinition Dark => FromPreset(DesktopThemePresetPolicy.BlueWhite, true);

    public static DesktopThemeDefinition Resolve(string? preference, Func<bool>? systemUsesLight = null) =>
        Resolve(DesktopThemePresetPolicy.BlueWhite, preference, systemUsesLight);

    public static DesktopThemeDefinition Resolve(DesktopThemePreset preset, string? preference, Func<bool>? systemUsesLight = null)
    {
        var effective = AppearanceThemePolicy.Effective(preference);
        if (effective == AppearanceThemePolicy.System)
            effective = (systemUsesLight ?? SystemUsesLightTheme)() ? AppearanceThemePolicy.Light : AppearanceThemePolicy.Dark;
        return FromPreset(preset, effective == AppearanceThemePolicy.Dark);
    }

    private static DesktopThemeDefinition FromPreset(DesktopThemePreset preset, bool dark)
    {
        var palette = dark ? preset.Dark : preset.Light;
        Color C(string token)
        {
            var value = palette[token];
            return Color.FromRgb(byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return new(dark ? AppearanceThemePolicy.Dark : AppearanceThemePolicy.Light,
            C("window"), C("surface"), C("raisedSurface"), C("ink"), C("muted"), C("accent"), C("onAccent"),
            C("line"), C("hover"), C("pressed"), C("selection"), C("selectionText"), C("codeSurface"),
            C("errorBackground"), C("errorText"), C("errorBorder"));
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
