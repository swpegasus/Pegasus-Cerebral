using System.Collections.ObjectModel;
using System.IO;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Local presentation meanings resolve to stable asset IDs. Execution state remains host-owned.</summary>
public static class SemanticIconSettings
{
    public const string FileName = "icon-meanings.ini";
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Message"] = DesktopIconKeys.Message, ["Assistant"] = DesktopIconKeys.Assistant, ["Tool"] = DesktopIconKeys.Tool,
            ["Question"] = DesktopIconKeys.Awaiting, ["Working"] = DesktopIconKeys.Working, ["Success"] = DesktopIconKeys.Success,
            ["Error"] = DesktopIconKeys.Error, ["Cancelled"] = DesktopIconKeys.Cancelled, ["CPU"] = DesktopIconKeys.Info,
            ["GPU"] = DesktopIconKeys.Working, ["Attachment"] = DesktopIconKeys.Attachment
        }.ToDictionary(pair => pair.Key, pair => ChatIconsPlugin.PluginId + "." + pair.Value, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return Defaults;
        return ParseLegacy(DesktopPluginFiles.ReadUtf8(path, 8192));
    }

    internal static IReadOnlyDictionary<string, string> ParseLegacy(string text)
    {
        if (new System.Text.UTF8Encoding(false, true).GetByteCount(text) > 8192)
            throw new InvalidDataException("Semantic icon settings exceed their bound.");
        var lines = text.TrimStart('\uFEFF').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length != Defaults.Count + 2 || lines[0] != "[meanings]" || lines[1] != "contract=pegasus.chat-icons.meanings/1")
            throw new InvalidDataException("Unsupported semantic icon settings.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(2))
        {
            var marker = line.IndexOf('=');
            if (marker < 1 || !values.TryAdd(line[..marker], line[(marker + 1)..])) throw new InvalidDataException("Duplicate or invalid icon meaning.");
        }
        Validate(values);
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> values)
    {
        Validate(values);
        DesktopPluginFiles.WriteUtf8Atomically(path, "[meanings]\ncontract=pegasus.chat-icons.meanings/1\n" +
            string.Join("\n", Defaults.Keys.Select(key => key + "=" + values[key])) + "\n", 8192);
    }

    internal static void Validate(IReadOnlyDictionary<string, string> values)
    {
        var assets = DesktopIcons.Definitions.Select(icon => ChatIconsPlugin.PluginId + "." + icon.Key).ToHashSet(StringComparer.Ordinal);
        if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Defaults.Keys) || values.Values.Any(id => !assets.Contains(id)))
            throw new InvalidDataException("Map every known meaning to an exact local icon asset ID.");
    }
}
