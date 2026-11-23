using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Selects one conversation policy, preserving the identity of the admitted package.</summary>
internal static class ConversationModuleSelection
{
    internal sealed record Selection(IReadOnlyList<IWorkflowModule> Modules,
        IReadOnlyList<ServicePluginPackageBinding> Packages,
        IReadOnlyList<ServicePluginPackageBinding> HostBuiltInPackages);

    public static Selection Compose(IEnumerable<IWorkflowModule> nativeModules,
        IEnumerable<ServicePluginPackageBinding> nativePackages)
    {
        var modules = nativeModules.ToArray();
        var packages = nativePackages.ToArray();
        var selected = packages.Where(package => package.Id == ConversationWorkflowModule.ModuleId).ToArray();
        var conversations = modules.Where(module => module.Id == ConversationWorkflowModule.ModuleId).ToArray();
        if (selected.Length > 1 || conversations.Length > 1)
            throw new InvalidDataException("Conversation policy has duplicate module or package identities.");

        if (selected.Length == 0)
        {
            if (conversations.Length != 0)
                throw new InvalidDataException("The native conversation module has no admitted package binding.");
            var fallback = ConversationWorkflowModule.PackageBinding;
            return new(Array.AsReadOnly(modules.Append(new ConversationWorkflowModule()).ToArray()),
                Array.AsReadOnly(packages.Append(fallback).ToArray()), Array.AsReadOnly(new[] { fallback }));
        }

        // A failed selected package must stay unavailable. Substituting the built-in policy would
        // misrepresent the accepted generation and could resume a checkpoint against different code.
        if (!selected[0].Loaded)
        {
            if (conversations.Length != 0)
                throw new InvalidDataException("An unavailable conversation package exported a workflow module.");
            return new(Array.AsReadOnly(modules), Array.AsReadOnly(packages), []);
        }

        if (conversations.Length != 1 || !conversations[0].Workflows.Any(workflow =>
                workflow.Descriptor.Id == ConversationWorkflowModule.DefinitionId &&
                workflow.Descriptor.Version == ConversationWorkflowModule.DefinitionVersion))
            throw new InvalidDataException("The selected conversation package does not export the required chat definition.");
        return new(Array.AsReadOnly(modules), Array.AsReadOnly(packages), []);
    }
}
