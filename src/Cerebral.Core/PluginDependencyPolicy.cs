namespace Cerebral.Core;

/// <summary>Implementation-independent package dependency declaration shared by package inspectors and hosts.</summary>
public sealed record PluginDependencyDeclaration(string Id, string? ExactVersion, string? MinimumVersion,
    string? MaximumVersionExclusive, bool Optional);

public static class PluginDependencyPolicy
{
    public const int MaximumDependencies = 32;

    public static void Validate(PluginDependencyDeclaration dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        NativeDesktopPluginPackage.ValidateId(dependency.Id);
        var exact = dependency.ExactVersion is not null;
        var minimum = dependency.MinimumVersion is not null;
        var maximum = dependency.MaximumVersionExclusive is not null;
        if (exact == (minimum || maximum) || exact && !PluginVersion.TryParse(dependency.ExactVersion, out _) ||
            minimum && !PluginVersion.TryParse(dependency.MinimumVersion, out _) ||
            maximum && !PluginVersion.TryParse(dependency.MaximumVersionExclusive, out _) ||
            minimum && maximum && PluginVersion.Parse(dependency.MinimumVersion!).CompareTo(PluginVersion.Parse(dependency.MaximumVersionExclusive!)) >= 0)
            throw new InvalidDataException("Dependency '" + dependency.Id + "' requires an exact SemVer or a nonempty minimum-inclusive/maximum-exclusive range.");
    }

    public static bool Compatible(PluginDependencyDeclaration dependency, string version)
    {
        if (!PluginVersion.TryParse(version, out var parsed)) return false;
        if (dependency.ExactVersion is not null)
            return string.Equals(dependency.ExactVersion, version, StringComparison.Ordinal);
        return (dependency.MinimumVersion is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MinimumVersion)) >= 0) &&
               (dependency.MaximumVersionExclusive is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MaximumVersionExclusive)) < 0);
    }
}
