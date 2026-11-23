using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

/// <summary>Native policy export with no scheduler, secondary history store or direct tool clients.</summary>
public sealed class ConversationServicePlugin : IServicePlugin, IAgentHarnessModule
{
    public const string ModuleId = ConversationWorkflowContract.ModuleId;
    public const string ModuleVersion = ConversationWorkflowContract.ModuleVersion;
    // Selection is per accepted definition version; exporting the second edition does not change defaults.
    public IReadOnlyList<IAgentHarness> Harnesses { get; } =
        [new ConversationAgentHarness(), ConversationAgentHarness.CreateToolContextProjectionEdition()];
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) { ArgumentNullException.ThrowIfNull(host); }
    public void Dispose() { }
}
