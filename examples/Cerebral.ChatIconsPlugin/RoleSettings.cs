using System.Collections.ObjectModel;
using System.IO;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

/// <summary>User choices map logical presentation roles to stable asset identities; never execution outcomes.</summary>
public static class RoleSettings
{
    public const string FileName = "roles.ini";
    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Go"] = "pegasus.chat-icons.status.working",
            ["Stop"] = "pegasus.chat-icons.status.cancelled",
            ["Good"] = "pegasus.chat-icons.status.success",
            ["NoGood"] = "pegasus.chat-icons.status.error",
            ["GoodToGo"] = "pegasus.chat-icons.status.success"
        });

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return Defaults;
        return ParseLegacy(DesktopPluginFiles.ReadUtf8(path, 8192));
    }

    internal static IReadOnlyDictionary<string, string> ParseLegacy(string text)
    {
        if (new System.Text.UTF8Encoding(false, true).GetByteCount(text) > 8192)
            throw new InvalidDataException("Icon role settings exceed their bound.");
        var lines = text.TrimStart('\uFEFF').Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith(';')).ToArray();
        if (lines.Length != 7 || lines[0] != "[roles]" || lines[1] != "contract=pegasus.chat-icons.roles/1")
            throw new InvalidDataException("Unsupported icon role settings.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(2))
        {
            var equals = line.IndexOf('=');
            if (equals < 1 || !values.TryAdd(line[..equals], line[(equals + 1)..])) throw new InvalidDataException("Duplicate or invalid role setting.");
        }
        Validate(values);
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> values)
    {
        Validate(values);
        DesktopPluginFiles.WriteUtf8Atomically(path, "[roles]\ncontract=pegasus.chat-icons.roles/1\n" +
            string.Join("\n", Defaults.Keys.Select(role => role + "=" + values[role])) + "\n", 8192);
    }

    internal static void Validate(IReadOnlyDictionary<string, string> values)
    {
        var keys = DesktopIcons.Definitions.Select(icon => "pegasus.chat-icons." + icon.Key).ToHashSet(StringComparer.Ordinal);
        if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Defaults.Keys) || values.Values.Any(value => !keys.Contains(value)))
            throw new InvalidDataException("Map every known role to one exact built-in icon identity.");
    }
}
