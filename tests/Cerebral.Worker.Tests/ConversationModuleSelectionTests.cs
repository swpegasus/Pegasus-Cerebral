using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker.Tests;

internal static class ConversationModuleSelectionTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { checks++; return; }
            throw new Exception("FAIL: " + message);
        }

        var fallback = ConversationModuleSelection.Compose([], []);
        Check(fallback.Modules.Count == 1 && fallback.Modules[0] is ConversationWorkflowModule &&
            fallback.Packages.Single() is { Id: ConversationWorkflowModule.ModuleId,
                Version: ConversationWorkflowModule.ModuleVersion, Loaded: true } &&
            fallback.Packages.Single().Dependencies.Single().Id == WorkerWorkflowEffects.ModuleId &&
            ReferenceEquals(fallback.HostBuiltInPackages.Single(), fallback.Packages.Single()),
            "an absent native package selects the built-in identity once");
        var builtinRegistry = new WorkflowRegistry(fallback.Modules);
        Check(builtinRegistry.Definitions.Single().Id == ConversationWorkflowModule.DefinitionId,
            "the fallback definition remains admissible");

        var native = new ConversationWorkflowModule();
        var binding = new ServicePluginPackageBinding(ConversationWorkflowModule.ModuleId, "1.0.2", true, [],
            Generation: "1.0.2-test-generation");
        var selected = ConversationModuleSelection.Compose([native], [binding]);
        Check(selected.Modules.Count == 1 && ReferenceEquals(selected.Modules[0], native) &&
            ReferenceEquals(selected.Packages.Single(), binding) && selected.HostBuiltInPackages.Count == 0,
            "a selected package keeps its exact module and package generation without a second fallback");
        Check(new WorkflowRegistry(selected.Modules).Definitions.Count == 1 &&
            System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(
                new WorkflowPackageBindings(selected.Packages).Capture(ConversationWorkflowModule.DefinitionId).Single()),
                CerebralJson.Element(binding)),
            "registry and durable binding capture agree on the selected package");

        var unavailable = binding with { Loaded = false, Error = "Candidate failed validation." };
        var failed = ConversationModuleSelection.Compose([], [unavailable]);
        Check(failed.Modules.Count == 0 && ReferenceEquals(failed.Packages.Single(), unavailable) &&
            failed.HostBuiltInPackages.Count == 0,
            "a failed selected package retains its diagnostic and receives no built-in substitution");
        Check(!new WorkflowRegistry(failed.Modules).TryGetDefinition(ConversationWorkflowModule.DefinitionId,
            ConversationWorkflowModule.DefinitionVersion, out _), "failed package cannot admit a chat");

        var other = new EmptyModule("example.other");
        var otherBinding = new ServicePluginPackageBinding(other.Id, "1.0.0", true, []);
        var mixed = ConversationModuleSelection.Compose([other, native], [otherBinding, binding]);
        Check(mixed.Modules.SequenceEqual([other, native]) && mixed.Packages.SequenceEqual([otherBinding, binding]),
            "selection preserves unrelated modules and bindings");
        Reject(() => ConversationModuleSelection.Compose([native], []), "unbound module");
        Reject(() => ConversationModuleSelection.Compose([], [binding]), "loaded package without a harness");
        Reject(() => ConversationModuleSelection.Compose([new EmptyModule(native.Id)], [binding]), "missing required chat definition");
        Reject(() => ConversationModuleSelection.Compose([native], [unavailable]), "module from unavailable package");
        Reject(() => ConversationModuleSelection.Compose([native, native], [binding]), "duplicate modules");
        Reject(() => ConversationModuleSelection.Compose([native], [binding, binding]), "duplicate bindings");
        Console.WriteLine($"Conversation module selection: {checks} deterministic checks passed (no model or live Worker).");
    }

    private sealed class EmptyModule(string id) : IWorkflowModule
    {
        public string Id => id;
        public IReadOnlyList<IWorkflowDefinition> Workflows => [];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }
}
