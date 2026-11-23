using Cerebral.Contracts.Agents;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Explicit opt-in package export. Separate entry types keep existing package editions
/// immutable; the application's default entry and definition selection are unchanged.</summary>
public sealed class WorkflowProgressConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    private readonly ConversationServicePlugin legacy = new();
    public const string ModuleId = ConversationServicePlugin.ModuleId;
    public const string ModuleVersion = ConversationServicePlugin.ModuleVersion;
    public IReadOnlyList<IAgentHarness> Harnesses { get; } =
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateWorkflowProgressEdition()];
    public IReadOnlyList<IToolExtension> ToolExtensions => legacy.ToolExtensions;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => legacy.CommandExtensions;
    public void Initialize(IServicePluginHost host) => legacy.Initialize(host);
    public void Dispose() => legacy.Dispose();
}
