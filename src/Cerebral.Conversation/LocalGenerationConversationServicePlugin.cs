using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Explicit export only; existing package/default selection is not migrated.</summary>
public sealed class LocalGenerationConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    public const string ModuleId = ConversationServicePlugin.ModuleId;
    // Package producer identity; every exported saved definition keeps its own version.
    public const string ModuleVersion = "1.0.20";
    private readonly PreparedInterventionConversationServicePlugin previous = new();
    // Retain every earlier factory exactly once; only the last edition consumes a local choice.
    public IReadOnlyList<IAgentHarness> Harnesses { get; } = Array.AsReadOnly<IAgentHarness>(
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateContextContributionEdition(),
            ConversationAgentHarness.CreateWorkflowProgressEdition(),
            ConversationAgentHarness.CreateToolContextProjectionEdition(),
            ConversationAgentHarness.CreatePreparedInterventionEdition(),
            ConversationAgentHarness.CreateToolCatalogPageEdition(), ConversationAgentHarness.CreateLocalGenerationEdition(),
            ConversationAgentHarness.CreateRepeatedSuccessGuardEdition(),
            ConversationAgentHarness.CreateRepeatPreparedEdition(),
            ConversationAgentHarness.CreateOwnerVerifiedPreparedEdition()]);
    public IReadOnlyList<IToolExtension> ToolExtensions => previous.ToolExtensions;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => previous.CommandExtensions;
    public void Initialize(IServicePluginHost host) => previous.Initialize(host);
    public void Dispose() => previous.Dispose();
}
