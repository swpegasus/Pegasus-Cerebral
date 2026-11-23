using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static WorkflowStep? PrepareScaffoldSchemaAfterBroadInventory(Input input, HarnessState before,
        HarnessState after, WorkflowEffect effect, ToolData? data)
    {
        // This is contract preparation only. The just-completed broad inventory must itself be
        // the classified admission; retained readiness, an exact root/target lookup, or a path in
        // the prompt cannot initiate a lookup. Normal tool dispatch still owns every permission,
        // argument, path, identity, and current provider check before scaffolding can execute.
        if (input.AutomaticPluginTestPresentationId is not null ||
            before.Phase != "tool" || before.PendingName != "plugin.projects" ||
            PendingToolArguments(before) is not { ValueKind: JsonValueKind.Object } arguments ||
            !IsBroadInventoryArguments(arguments) ||
            data is not { Truncated: false, Evidence: { ProviderId: "cerebral.plugin-sdk",
                Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } evidence } ||
            evidence.RequestId is { } requestId && requestId != effect.Id ||
            after.ScaffoldReadiness is not { Ready: true } readiness ||
            readiness.EffectId != effect.Id ||
            !HasScaffoldReadinessEvidence(after, readiness, allowLegacy: false) ||
            after.Selected.Any(tool => tool.Name == "plugin.scaffold") ||
            !after.Discovery.Any(tool => tool.Name == "plugin.scaffold"))
            return null;

        // Related-schema hints are useful for explicit model selections, but this host handoff is
        // deliberately one exact lookup. It creates no semantic receipt or successful milestone.
        return RequestCatalog(input, after, ["plugin.scaffold"], prepareRelated: false);
    }

    private static PluginScaffoldReadiness? CurrentScaffoldReadiness(HarnessState state, WorkflowEffect effect,
        ToolData? data, bool successful, bool inventoryParsed, out PluginInventoryReceiptKind? inventoryKind)
    {
        inventoryKind = null;
        if (state.PendingName is not ("plugin.projects" or "plugin.scaffold")) return state.ScaffoldReadiness;
        inventoryKind = PluginInventoryReceiptKind.Invalidates;
        if (state.PendingName == "plugin.projects" && successful && inventoryParsed &&
            data is { Truncated: false, Evidence: { ProviderId: "cerebral.plugin-sdk",
                Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } } &&
            PendingToolArguments(state) is { } arguments)
        {
            try
            {
                using var document = JsonDocument.Parse(data.Content);
                var root = document.RootElement;
                UniqueProperties(root);
                if (!arguments.TryGetProperty("root", out _) && HasInventoryIdentityEnvelope(root))
                {
                    if (!CompleteIdentitySummaryAdmission(state, effect, data, root, arguments, out var incompleteReason))
                        return new(false, effect.Id, incompleteReason);
                    inventoryKind = PluginInventoryReceiptKind.BroadAdmission;
                    return new(true, effect.Id,
                        "Complete identity summary for this run; detailed rows may be limited. Provider will recheck every current source and identity.");
                }
                var ready = root.TryGetProperty("scaffoldReady", out var availability) &&
                    availability.ValueKind == JsonValueKind.True;
                var complete = root.TryGetProperty("limited", out var limited) &&
                    limited.ValueKind == JsonValueKind.False;
                if (ready && complete && root.TryGetProperty("discoverySummaryOnly", out var summary))
                {
                    if (!arguments.TryGetProperty("root", out _) && summary.ValueKind == JsonValueKind.True)
                    {
                        inventoryKind = PluginInventoryReceiptKind.BroadAdmission;
                        return new(true, effect.Id, "Complete broad discovery; provider will recheck admission.");
                    }
                    if (arguments.TryGetProperty("root", out _) && summary.ValueKind == JsonValueKind.False &&
                        state.ScaffoldReadiness is { Ready: true } prior &&
                        HasScaffoldReadinessEvidence(state, prior, allowLegacy: false))
                    {
                        // The provider confirms retained availability, not a new catalog. The parsed
                        // exact scope already matched the requested root; preserve the original proof.
                        inventoryKind = PluginInventoryReceiptKind.ExactReadOnly;
                        return prior with { Reason = "Complete broad admission retained across exact reads; provider will recheck current roots and identities." };
                    }
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                // Unknown, contradictory, or malformed envelopes never create or preserve admission.
            }
        }
        return new(false, effect.Id, "Use an inspected source or obtain complete broad discovery before a new scaffold.");
    }

    private static PluginScaffoldReadiness? ScaffoldReadinessAfterExactRead(HarnessState state, WorkflowEffect effect) =>
        // A denial's latest-receipt anchor follows a confirmed exact read. Only classified broad
        // admission survives that read; legacy true admission was valid solely while its anchor was latest.
        state.ScaffoldReadiness switch
        {
            null => null,
            { Ready: false } denied => denied with { EffectId = effect.Id },
            { Ready: true } admitted when HasScaffoldReadinessEvidence(state, admitted, allowLegacy: false) => admitted,
            _ => new(false, effect.Id, "Exact source inspection did not confirm classified broad scaffold admission.")
        };

    private static bool HasScaffoldReadinessEvidence(HarnessState state, PluginScaffoldReadiness readiness, bool allowLegacy)
    {
        var relevant = state.ToolReceipts.Select((receipt, index) => (receipt, index))
            .Where(item => item.receipt.Name is "plugin.projects" or "plugin.scaffold").ToArray();
        var references = relevant.Where(item => item.receipt.EffectId == readiness.EffectId).ToArray();
        if (references.Length != 1) return false;
        var anchor = references[0];
        if (!readiness.Ready) return relevant.Last().index == anchor.index;
        if (anchor.receipt.Name != "plugin.projects" ||
            anchor.receipt.State != nameof(WorkflowEffectState.Succeeded) ||
            anchor.receipt.Outcome != nameof(ToolExecutionOutcome.Succeeded) || anchor.receipt.Truncated)
            return false;
        if (anchor.receipt.InventoryKind != PluginInventoryReceiptKind.BroadAdmission)
            // Old saved markers retain only their former precise latest-receipt check. They cannot
            // acquire a new permission to survive an unclassified intervening inventory event.
            return allowLegacy && anchor.receipt.InventoryKind is null && relevant.Last().index == anchor.index;
        return relevant.Where(item => item.index > anchor.index).All(item =>
            item.receipt.Name == "plugin.projects" &&
            item.receipt.InventoryKind == PluginInventoryReceiptKind.ExactReadOnly &&
            item.receipt.State == nameof(WorkflowEffectState.Succeeded) &&
            item.receipt.Outcome == nameof(ToolExecutionOutcome.Succeeded) && !item.receipt.Truncated);
    }
}
