using Cerebral.Contracts.Agents;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Explicit receipt-context export. Installing/selecting this candidate is a separate host
/// decision; existing default and initial-context edition factories keep their original behavior.</summary>
public sealed class ToolContextProjectionConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    private readonly ConversationServicePlugin legacy = new();
    public const string ModuleId = ConversationServicePlugin.ModuleId;
    public const string ModuleVersion = ConversationServicePlugin.ModuleVersion;
    public IReadOnlyList<IAgentHarness> Harnesses { get; } =
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateContextContributionEdition(),
            ConversationAgentHarness.CreateToolContextProjectionEdition()];
    public IReadOnlyList<IToolExtension> ToolExtensions => legacy.ToolExtensions;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => legacy.CommandExtensions;
    public void Initialize(IServicePluginHost host) => legacy.Initialize(host);
    public void Dispose() => legacy.Dispose();
}
