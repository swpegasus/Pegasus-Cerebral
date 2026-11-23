using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

internal sealed class WorkerContextContributionProviderGuard(IContextProviderCommandHandler inner,
    Func<WorkflowCommandContext, bool, CancellationToken, Task<WorkflowCommandResult>> invoke)
    : IContextProviderCommandHandler
{
    public ContextProviderDescriptor Provider => inner.Provider;
    public WorkflowCommandDescriptor Descriptor => inner.Descriptor;
    public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
        invoke(context, false, token);
    public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
        invoke(context, true, token);

    internal static IWorkflowModule Wrap(IWorkflowModule module, IReadOnlyList<string> providerIds,
        Func<IContextProviderCommandHandler, WorkflowCommandContext, bool, CancellationToken, Task<WorkflowCommandResult>> invoke)
    {
        if (!module.Commands.Any(command => providerIds.Contains(command.Descriptor.Id, StringComparer.Ordinal))) return module;
        var commands = module.Commands.Select(command =>
        {
            if (!providerIds.Contains(command.Descriptor.Id, StringComparer.Ordinal)) return command;
            if (command is not IContextProviderCommandHandler provider || provider.Provider.Id != command.Descriptor.Id ||
                provider.Provider.Version != command.Descriptor.Version)
                throw new InvalidDataException("The registered package context provider contract is invalid.");
            return (IWorkflowCommandHandler)new WorkerContextContributionProviderGuard(provider,
                (context, reconcile, token) => invoke(provider, context, reconcile, token));
        }).ToArray();
        return new Module(module.Id, module.Workflows, Array.AsReadOnly(commands));
    }

    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
}
