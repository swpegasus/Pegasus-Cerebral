namespace Cerebral.Core;

/// <summary>Portable, instruction-only skill declaration. Skills never add tools, capabilities, paths, or authority.</summary>
public sealed class SkillManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    /// <summary>Bounded phrases used to load the full skill only when the current assignment materially matches.</summary>
    public List<string> Triggers { get; set; } = [];
}

/// <summary>Owner-selected applicability. Global has no target; other scopes require one exact role/profile/project ID.</summary>
public sealed record SkillBinding(string Scope, string? Target = null);

public sealed record SkillSummary(string Id, string Name, string Version, string Description, bool Enabled,
    bool BuiltIn, int Revision, IReadOnlyList<string> Triggers, IReadOnlyList<SkillBinding> Bindings,
    string Source);

public sealed record SkillInspection(SkillSummary Summary, string Content);

/// <summary>Immutable instruction snapshot admitted with an assignment.</summary>
public sealed record SkillSnapshot(string Id, string Name, string Version, string Description,
    IReadOnlyList<string> Triggers, string Content);
