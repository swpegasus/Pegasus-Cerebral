namespace Cerebral.Core;

/// <summary>Write includes read. A most-specific grant overrides an ancestor grant.</summary>
public enum ScopedPathAccess { Read, Write }
public sealed record ScopedPathGrant(string Path, ScopedPathAccess Access);
/// <summary>Host-owned local path ceiling, independent of reusable profile grants. This is not a process sandbox.</summary>
public sealed record ScopedPathAccessScope(int SchemaVersion, IReadOnlyList<ScopedPathGrant> Grants);

public static class ScopedPathAccessPolicy
{
    public const int Version = 1;
    public const int MaximumGrants = 128;

    /// <summary>Combines explicit chat rules with the defaults for canonical paths.</summary>
    public static IReadOnlyList<ScopedPathGrant> IncludeChatDefaults(
        string? workingDirectory, string? projectsDirectory, IReadOnlyList<ScopedPathGrant> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var grants = rules.ToList();
        if (projectsDirectory is not null &&
            !rules.Any(rule => ScopedFilePolicy.IsWithin(projectsDirectory, rule.Path)))
            grants.Add(new(projectsDirectory, ScopedPathAccess.Write));
        // The shared grant covers the starting directory. A separate write grant for it
        // would override a read-only rule on Projects or an intermediate folder.
        if (workingDirectory is not null &&
            (projectsDirectory is null || !ScopedFilePolicy.IsWithin(workingDirectory, projectsDirectory)))
            grants.Add(new(workingDirectory, ScopedPathAccess.Write));
        return grants;
    }

    public static ScopedPathAccessScope Normalize(ScopedPathAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.SchemaVersion != Version || scope.Grants is null || scope.Grants.Count > MaximumGrants)
            throw new UnauthorizedAccessException("The host path-access scope has an unsupported version or size.");
        var entries = new List<ScopedPathGrant>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var grant in scope.Grants)
        {
            if (grant is null || !Enum.IsDefined(grant.Access) || string.IsNullOrWhiteSpace(grant.Path) ||
                grant.Path.Length > 2048 || grant.Path.Any(char.IsControl) || !Path.IsPathFullyQualified(grant.Path))
                throw new UnauthorizedAccessException("Path grants require an absolute bounded path and a supported access mode.");
            // Reuse structural link/device/traversal checks without admitting any effect or grant.
            var path = Path.TrimEndingDirectorySeparator(ScopedFilePolicy.PathInScope(grant.Path,
                new ToolExecutionContext(new AgentProfile(), "path-validation", "path-validation", FullAccess: true)));
            if (!seen.Add(path)) throw new UnauthorizedAccessException("Path grants contain duplicate canonical paths.");
            entries.Add(new(path, grant.Access));
        }
        return new(Version, entries.OrderBy(x => x.Path, OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray());
    }

    public static void Require(string canonicalPath, ScopedPathAccessScope scope, ScopedPathAccess access)
    {
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        var normalized = Normalize(scope);
        var selected = normalized.Grants.Where(x => ScopedFilePolicy.IsWithin(canonicalPath, x.Path))
            .OrderByDescending(x => x.Path.Length).FirstOrDefault();
        if (selected is null || access == ScopedPathAccess.Write && selected.Access != ScopedPathAccess.Write)
            throw new UnauthorizedAccessException(access == ScopedPathAccess.Write
                ? "The path is not writable in this conversation's current access scope."
                : "The path is outside this conversation's current access scope.");
    }
}
