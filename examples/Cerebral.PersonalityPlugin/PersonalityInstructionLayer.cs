using System.IO;

namespace Cerebral.PersonalityPlugin;

internal static class PersonalityInstructionLayer
{
    internal const string StartMarker = "<!-- PEGASUS:PERSONALITY:START:v1 -->";
    internal const string EndMarker = "<!-- PEGASUS:PERSONALITY:END:v1 -->";
    private const string Boundary = "Optional owner-selected communication style. Current user and application instructions remain stronger; this layer grants no tools, permissions, evidence, or authority.";

    internal static string Apply(string current, PersonalityPreset preset, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(current);
        PersonalitySettings.ValidatePreset(preset);
        var range = FindOwnedRange(current);
        if (!enabled && range is null) return current;
        var prefix = range is null ? current : current[..range.Value.Start];
        var suffix = range is null ? "" : current[range.Value.End..];
        if (!enabled) return prefix + suffix;
        return prefix + (prefix.Length == 0 ? "" : "\n\n") + StartMarker + "\n" + Boundary + "\n" +
               preset.Instructions + "\n" + EndMarker + suffix;
    }

    internal static bool HasLayer(string content) => FindOwnedRange(content) is not null;

    private static (int Start, int End)? FindOwnedRange(string content)
    {
        var starts = FindAll(content, StartMarker);
        var ends = FindAll(content, EndMarker);
        if (starts.Count == 0 && ends.Count == 0) return null;
        if (starts.Count != 1 || ends.Count != 1 || starts[0] >= ends[0])
            throw new InvalidDataException("The lead prompt contains duplicate or malformed Personality Presets markers. Review it manually; no content was changed.");
        var body = content[(starts[0] + StartMarker.Length)..ends[0]];
        if (!body.StartsWith("\n" + Boundary + "\n", StringComparison.Ordinal) || !body.EndsWith('\n'))
            throw new InvalidDataException("The Personality Presets section was edited outside the preset panel. Review it manually; no content was changed.");
        var start = starts[0] >= 2 && content.AsSpan(starts[0] - 2, 2).SequenceEqual("\n\n") ? starts[0] - 2 : starts[0];
        return (start, ends[0] + EndMarker.Length);
    }

    private static List<int> FindAll(string content, string marker)
    {
        var result = new List<int>();
        for (var offset = 0; offset <= content.Length - marker.Length;)
        {
            var index = content.IndexOf(marker, offset, StringComparison.Ordinal);
            if (index < 0) break;
            result.Add(index); offset = index + marker.Length;
        }
        return result;
    }
}
