using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

/// <summary>A portable color preset. These data tokens do not contain executable WPF resources or paths.</summary>
public sealed record DesktopThemePreset(int SchemaVersion, string Id, string Name,
    IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark);

/// <summary>Private owner appearance state; separate from the stable desktop plugin SDK and light/dark mode.</summary>
public sealed record DesktopThemePresetState(long Revision, DesktopThemePreset Preset);

public static class DesktopThemePresetPolicy
{
    public const int MaximumUtf8Bytes = 16 * 1024;
    public static IReadOnlyList<string> PaletteTokens { get; } = Array.AsReadOnly(new[]
    {
        "window", "surface", "raisedSurface", "ink", "muted", "accent", "onAccent", "line",
        "hover", "pressed", "selection", "selectionText", "codeSurface", "errorBackground", "errorText", "errorBorder"
    });

    public static DesktopThemePreset BlueWhite { get; } = new(1, "blue-white", "Blue White",
        Palette(["#F3F6FB", "#FFFFFF", "#F8FAFF", "#172B4D", "#52647E", "#245BCE", "#FFFFFF", "#D5DFEF",
            "#EAF1FF", "#DCE7FA", "#DBE8FF", "#15356E", "#EEF3FB", "#FEECEC", "#5C1313", "#B72D2D"]),
        Palette(["#101A2D", "#16243A", "#1E304A", "#EFF5FF", "#B4C3DA", "#80B0FF", "#0B234A", "#354A68",
            "#253D60", "#304B72", "#F3F7FF", "#102544", "#10203A", "#5B181D", "#FFF4F4", "#EB6469"]));

    public static DesktopThemePreset Parse(JsonElement document)
    {
        if (Encoding.UTF8.GetByteCount(document.GetRawText()) > MaximumUtf8Bytes)
            throw new ArgumentException("The theme preset exceeds its 16 KiB limit.");
        ExactObject(document, ["schemaVersion", "id", "name", "light", "dark"], "Theme preset");
        if (!document.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1)
            throw new ArgumentException("Theme schemaVersion must be 1.");
        var id = String(document.GetProperty("id"), 64, "Theme id");
        if (id[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            id.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("Theme id must use lowercase letters, digits, dots, hyphens or underscores.");
        var name = String(document.GetProperty("name"), 100, "Theme name");
        return new(1, id, name, ReadPalette(document.GetProperty("light"), "light"), ReadPalette(document.GetProperty("dark"), "dark"));
    }

    public static string Serialize(DesktopThemePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var json = JsonSerializer.Serialize(preset, CerebralJson.Options);
        using var document = JsonDocument.Parse(json);
        _ = Parse(document.RootElement);
        return json;
    }

    private static IReadOnlyDictionary<string, string> ReadPalette(JsonElement input, string mode)
    {
        ExactObject(input, PaletteTokens, mode + " palette");
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in PaletteTokens)
        {
            var value = String(input.GetProperty(token), 7, mode + "." + token);
            if (value.Length != 7 || value[0] != '#' || value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
                throw new ArgumentException(mode + "." + token + " must be an opaque #RRGGBB color.");
            colors.Add(token, value.ToUpperInvariant());
        }
        // Pair text with every surface that the shared templates actually use. Reject an unreadable
        // import before it changes saved state; the previous preset remains available for recovery.
        foreach (var surface in new[] { "window", "surface", "raisedSurface", "hover", "pressed", "codeSurface" })
            Contrast(colors, "ink", surface, mode);
        foreach (var surface in new[] { "window", "surface", "raisedSurface" })
            Contrast(colors, "muted", surface, mode);
        foreach (var surface in new[] { "window", "surface", "raisedSurface", "codeSurface" })
        {
            Contrast(colors, "accent", surface, mode);
            Contrast(colors, "errorText", surface, mode);
        }
        Contrast(colors, "onAccent", "accent", mode);
        Contrast(colors, "selectionText", "selection", mode);
        Contrast(colors, "errorText", "errorBackground", mode);
        return new ReadOnlyDictionary<string, string>(colors);
    }

    private static void Contrast(IReadOnlyDictionary<string, string> colors, string text, string background, string mode)
    {
        var a = Luminance(colors[text]); var b = Luminance(colors[background]);
        if ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) < 4.5)
            throw new ArgumentException(mode + "." + text + " needs at least 4.5:1 contrast against " + background + ".");
    }

    private static double Luminance(string color)
    {
        static double Channel(ReadOnlySpan<char> hex)
        {
            var value = Convert.ToInt32(hex.ToString(), 16) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.AsSpan(1, 2)) + 0.7152 * Channel(color.AsSpan(3, 2)) + 0.0722 * Channel(color.AsSpan(5, 2));
    }

    private static string String(JsonElement value, int maximum, string label)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length is < 1 || text.Length > maximum || text != text.Trim() || text.Any(char.IsControl))
            throw new ArgumentException(label + " must be bounded, nonempty text without control characters.");
        return text;
    }

    private static void ExactObject(JsonElement value, IReadOnlyCollection<string> names, string label)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException(label + " must be an object.");
        var properties = value.EnumerateObject().ToArray();
        var actual = properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Length != names.Count || actual.Count != names.Count || !actual.SetEquals(names))
            throw new ArgumentException(label + " has missing, duplicate or unsupported fields.");
    }

    private static IReadOnlyDictionary<string, string> Palette(string[] values) =>
        new ReadOnlyDictionary<string, string>(PaletteTokens.Select((key, i) => (key, value: values[i]))
            .ToDictionary(x => x.key, x => x.value, StringComparer.Ordinal));
}
