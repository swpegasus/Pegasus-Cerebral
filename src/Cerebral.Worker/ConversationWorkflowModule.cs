using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

/// <summary>Default conversation policy is a replaceable harness, composed through the same module registry.</summary>
public sealed class ConversationWorkflowModule : IWorkflowModule
{
    public const string ModuleId = ConversationWorkflowContract.ModuleId;
    public const string ModuleVersion = ConversationWorkflowContract.ModuleVersion;
    public const string DefinitionId = ConversationWorkflowContract.DefinitionId;
    public const string DefinitionVersion = ConversationWorkflowContract.DefinitionVersion;
    public string Id => ModuleId;
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new AgentWorkflowDefinition(new ConversationAgentHarness())];
    public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    public static ServicePluginPackageBinding PackageBinding => new(ModuleId, ModuleVersion, true,
        [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, null, null,
            false, WorkerWorkflowEffects.ModuleVersion, true)]);
}
