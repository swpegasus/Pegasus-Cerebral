using Cerebral.Contracts.Workflows;

namespace Cerebral.PluginLifecycle;

/// <summary>
/// Compatibility for an already-admitted native conversation origin. This is neither workflow
/// admission nor a current registry requirement: completed work survives benign registry changes.
/// Root/user, grants, project, paths, package bytes and owner/claim checks remain with each caller.
/// Kept in one lifecycle-owned source compiled into Main and Worker; no cross-process assembly dependency.
/// </summary>
internal static class ChatPluginInstallDefinitionCompatibility
{
    // This exact edition is produced by ConversationAgentHarness.ToolContextProjectionVersion.
    // Qualification joins the producer and Main selector constants to catch declaration drift.
    internal const string DetailedDefinitionVersion = "1.0.3-tool-context.1";

    internal static bool Supports(string? definitionId, string? definitionVersion) =>
        definitionId == ConversationWorkflowContract.DefinitionId &&
        (definitionVersion == ConversationWorkflowContract.DefinitionVersion ||
         definitionVersion == DetailedDefinitionVersion);

    internal const string UnsupportedMessage =
        "This source workflow definition is unsupported for native-plugin lifecycle review. " +
        "Only an admitted root user conversation using the supported base or detailed workflow is eligible. " +
        "Inspect the original run and the application's compatible definition set; do not recreate completed work or bypass review.";
}
