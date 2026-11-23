namespace Cerebral.HelpPlugin;

public sealed record HelpDiscoveryPoint(string Id, string Title, string Identity, string InputOutput,
    string Boundary, string Source, string HelpPageId, string HelpSectionId)
{
    public string ReferenceId => "d:" + Id;
}

/// <summary>Passive map to existing discovery ends. Help does not register or execute these routes.</summary>
public static class HelpDiscoveryCatalog
{
    public const string ContentVersion = HelpCatalog.ContentVersion;
    public static IReadOnlyList<HelpDiscoveryPoint> Points { get; } = Array.AsReadOnly(new[]
    {
        new HelpDiscoveryPoint("role", "Agent role and accepted profile",
            "AgentProfile: Id / Revision / Role / Purpose / Instructions / saved grants.",
            "Accepted agent/profile/job identity → scoped role framing; lead/helper/compactor roles.",
            "Source contract. Profile instructions do not grant capabilities or permission.",
            "src/Cerebral.Core/Contracts.cs:237; src/Cerebral.Worker/AgentPrompt.cs:686", "engine", "contract-map"),
        new HelpDiscoveryPoint("capabilities", "Permitted capability catalog",
            "cerebral.host:catalog command v1; host module 1.4.0.",
            "{} → up to 64 permitted tool summaries; {names:[1–8 exact names]} → frozen input schemas/prerequisites; truncated is explicit.",
            "Source-supported discovery requires accepted/current matching grants and live matching descriptors. Filtered or truncated results do not prove absence. The projection is not a general owner/version/source/Help directory; per-tool contract versions/output schemas are not supplied.",
            "src/Cerebral.Worker/WorkerWorkflowEffects.cs:18; src/Cerebral.Worker/WorkerWorkflowConversation.cs:102,374", "workflows", "contract-map"),
        new HelpDiscoveryPoint("skills", "Enabled skill references",
            "skills.list / skills.read; SkillManifest.SchemaVersion=1; package Version is manifest-defined.",
            "skills.list {} → enabled agent/project summaries; skills.read {id} → exact enabled instruction body.",
            "Source-supported applicable global/role/profile/project bindings. Disabled/inapplicable packages are excluded; this is not installed inventory. Skills are guidance, never new capabilities or authorization.",
            "src/Cerebral.Worker/SkillToolExtension.cs:9; src/Cerebral.Core/SkillContracts.cs:4; src/Cerebral.Worker/SkillCatalog.cs:233", "reference", "discovery"),
        new HelpDiscoveryPoint("guidance", "Task-matched skill guidance",
            "SkillCatalog.ModelGuidance; at most three matching bodies.",
            "ID/name/triggers and accepted request framing → selected shared/role/profile guidance.",
            "Source implementation; no Help registration or active runtime inspection in this package.",
            "src/Cerebral.Worker/SkillCatalog.cs:208; src/Cerebral.Worker/WorkerWorkflowEffects.cs:371", "engine", "contract-map"),
        new HelpDiscoveryPoint("desktop", "Desktop plugin contract reference",
            "plugin.contract {topic}; pegasus.cerebral.desktop / API 1.",
            "Exact topic → bounded public signatures/examples and identity/reference. Topics include desktop-base, storage, draft, mount-view, conversation/settings, composer-command.",
            "Requires a local lead profile with desktop.plugins.manage plus the descriptor's filesystem.read grant. This is not a universal agent tool. sidebar.tabs/configuration.pages exist; top document/full docking remain host gaps.",
            "src/Cerebral.Tools/PluginScaffoldToolExtension.cs:59,197,648; src/Cerebral.Core/NativeDesktopPluginPackage.cs:21", "plugins", "contract-map"),
        new HelpDiscoveryPoint("sdk", "Structured SDK discovery",
            "sdk.discover / sdk.describe; operation version 1.",
            "{} → actor-filtered operation/workflow/command IDs, versions and descriptions; {id} → exact permitted descriptor/input schema or matching workflow/command/context-provider contracts.",
            "Structured authenticated host route. Owner-only operations and required capabilities are filtered for agents; tool availability is not universal. A missing entry is not proof of no installed provider.",
            "src/Cerebral.Worker/WorkerWorkflows.cs:552,564,595; src/Cerebral.Worker/CommandDispatcher.cs", "reference", "contracts"),
        new HelpDiscoveryPoint("engine", "Selected engine descriptor",
            "IEnginePlugin.Descriptor; pegasus.cerebral.engine / protocol 1.",
            "SDK descriptor → ID/version, protocol/state format and supported input/output contracts/versions.",
            "Source SDK interface. General AI-facing engine inspection and live Help reference projection are not exposed here.",
            "src/Cerebral.Engine.Contracts/EngineContracts.cs:6,14", "engine", "contract-map")
    });

    static HelpDiscoveryCatalog()
    {
        if (Points.Count > 32 || Points.Select(point => point.Id).Distinct(StringComparer.Ordinal).Count() != Points.Count)
            throw new InvalidOperationException("Help discovery bounds or identities are invalid.");
        foreach (var point in Points)
        {
            if (new[] { point.Id, point.Title, point.Identity, point.InputOutput, point.Boundary, point.Source }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > HelpCatalog.MaximumTextCharacters))
                throw new InvalidOperationException("Help discovery text is empty or exceeds its bound.");
            if (!HelpCatalog.Pages.Any(page => page.Id == point.HelpPageId &&
                page.Sections.Any(section => section.Id == point.HelpSectionId)))
                throw new InvalidOperationException("Help discovery target is absent.");
        }
    }
}
