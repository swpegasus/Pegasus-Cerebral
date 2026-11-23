using Cerebral.Contracts.Agents;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Explicit package entry for prepared owner intervention. Existing entry types and
/// application defaults remain unchanged; accepted work retains its original package binding.</summary>
public sealed class PreparedInterventionConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    private readonly ConversationServicePlugin legacy = new();
    public const string ModuleId = ConversationServicePlugin.ModuleId;
    public const string ModuleVersion = "1.0.4";

    // Keep every previously exported edition exactly once; only the last edition can
    // consume a prepared owner rejection. The host still performs normal admission.
    public IReadOnlyList<IAgentHarness> Harnesses { get; } = Array.AsReadOnly<IAgentHarness>(
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateContextContributionEdition(),
            ConversationAgentHarness.CreateWorkflowProgressEdition(),
            ConversationAgentHarness.CreateToolContextProjectionEdition(),
            ConversationAgentHarness.CreatePreparedInterventionEdition()]);

    public IReadOnlyList<IToolExtension> ToolExtensions => legacy.ToolExtensions;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => legacy.CommandExtensions;
    public void Initialize(IServicePluginHost host) => legacy.Initialize(host);
    public void Dispose() => legacy.Dispose();
}
