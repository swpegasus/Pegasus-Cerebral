using Cerebral.Contracts.Agents;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Explicit candidate entry through the existing harness-module adapter. The default
/// service entry and accepted legacy edition remain available; application selection is separate.</summary>
public sealed class ContextContributionConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    private readonly ConversationServicePlugin legacy = new();
    public const string ModuleId = ConversationServicePlugin.ModuleId;
    public const string ModuleVersion = ConversationServicePlugin.ModuleVersion;
    public IReadOnlyList<IAgentHarness> Harnesses { get; } =
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateContextContributionEdition()];
    public IReadOnlyList<IToolExtension> ToolExtensions => legacy.ToolExtensions;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => legacy.CommandExtensions;
    public void Initialize(IServicePluginHost host) => legacy.Initialize(host);
    public void Dispose() => legacy.Dispose();
}
