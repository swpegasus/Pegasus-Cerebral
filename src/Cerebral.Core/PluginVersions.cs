using System.Globalization;

namespace Cerebral.Core;

/// <summary>Bounded SemVer 2.0 precedence. Build metadata is identity data, but does not affect precedence.</summary>
public sealed class PluginVersion : IComparable<PluginVersion>
{
    private readonly uint[] numbers;
    private readonly string[] prerelease;
    public string Value { get; }

    private PluginVersion(string value, uint[] numbers, string[] prerelease)
    {
        Value = value; this.numbers = numbers; this.prerelease = prerelease;
    }

    public static bool TryParse(string? value, out PluginVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > 100) return false;
        var metadata = value.Split('+');
        if (metadata.Length > 2 || metadata.Length == 2 && !Identifiers(metadata[1], false)) return false;
        var separator = metadata[0].IndexOf('-');
        var core = separator < 0 ? metadata[0] : metadata[0][..separator];
        var pre = separator < 0 ? null : metadata[0][(separator + 1)..];
        if (pre is not null && !Identifiers(pre, true)) return false;
        var components = core.Split('.');
        if (components.Length != 3) return false;
        var numbers = new uint[3];
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component.Length == 0 || component.Length > 1 && component[0] == '0' ||
                component.Any(c => !char.IsAsciiDigit(c)) ||
                !uint.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return false;
        }
        version = new(value, numbers, pre?.Split('.') ?? []);
        return true;
    }

    public static PluginVersion Parse(string value) => TryParse(value, out var version)
        ? version! : throw new InvalidDataException("Plugin versions require bounded major.minor.patch SemVer, with optional prerelease and build metadata.");

    public int CompareTo(PluginVersion? other)
    {
        if (other is null) return 1;
        for (var index = 0; index < numbers.Length; index++)
        {
            var comparison = numbers[index].CompareTo(other.numbers[index]);
            if (comparison != 0) return comparison;
        }
        if (prerelease.Length == 0 || other.prerelease.Length == 0)
            return prerelease.Length == other.prerelease.Length ? 0 : prerelease.Length == 0 ? 1 : -1;
        for (var index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
        {
            var left = prerelease[index]; var right = other.prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit); var rightNumeric = right.All(char.IsAsciiDigit);
            var comparison = leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : leftNumeric && left.Length != right.Length ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    private static bool Identifiers(string value, bool prerelease)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || identifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
            if (prerelease && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }
}
