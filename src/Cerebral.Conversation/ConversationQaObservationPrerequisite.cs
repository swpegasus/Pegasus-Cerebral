using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumInitialQaObservations = 4;
    private sealed record InitialQaObservation(string PresentationId, string MountId, string? EffectId = null);

    private static bool NeedsInitialQaObservation(Input input) =>
        input.AutomaticPluginTestPresentationId is not null && input.HostPluginTestMountIds.Count != 0 &&
        input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal);

    private static bool HasInitialQaControlSchema(HarnessState state) =>
        state.Selected.Any(item => item.Name == "plugin.controls" && item.InputSchema is { ValueKind: JsonValueKind.Object });

    private static bool InitialQaSchemaPreparation(Input input, HarnessState state, IReadOnlyList<string> names) =>
        NeedsInitialQaObservation(input) && !HasInitialQaControlSchema(state) && names.Contains("plugin.controls", StringComparer.Ordinal);

    private static HarnessState RetainInitialQaSchemaPreparation(Input input, HarnessState state) =>
        state.Phase == "describe" && state.PendingArguments is { } pending &&
        InitialQaSchemaPreparation(input, state, Strings(pending.GetProperty("names")))
            ? state with { InitialQaSchemaLookupAttempted = true, PrimaryCatalogNames = [] } : state;

    private static WorkflowStep? InitialQaObservationPrerequisite(Input input, HarnessState state)
    {
        if (!state.CatalogLoaded || input.AutomaticPluginTestPresentationId is not { } presentation ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) ||
            input.HostPluginTestMountIds.Count == 0 || state.NativeReviewFailure is not null ||
            state.InitialQaObservations.Count >= MaximumInitialQaObservations) return null;
        var mount = input.HostPluginTestMountIds.FirstOrDefault(id =>
            !state.InitialQaObservations.Any(item => item.PresentationId == presentation && item.MountId == id) &&
            !state.NativeControlCoverage.Any(item => item.PresentationId == presentation && item.MountId == id &&
                HasQaObservationReceipt(state, item.EffectId, failed: false)));
        if (mount is null) return null;
        // Legacy completed decisions and deferred catalog actions enter here directly.
        // Reserve the same bounded receipt/source headroom before committing inspect intent.
        if (ContextPressurePrerequisite(input, state) is { } pressure) return pressure;
        if (!HasInitialQaControlSchema(state))
        {
            if (state.InitialQaSchemaLookupAttempted)
                return Finish(state, false, "The one initial QA control-schema preparation attempt did not provide a usable plugin.controls schema. " +
                    "Its actual catalog evidence is retained; no native observation or automatic schema retry was dispatched.");
            if (state.Descriptions >= 16 || state.Selected.Any(item => item.Name == "plugin.controls"))
                return Finish(state, false, "Initial QA observation has no usable plugin.controls schema within the existing sixteen-selection limit. " +
                    "No native observation or automatic schema retry was dispatched.");
            return RequestCatalog(input, state, ["plugin.controls"], prepareRelated: false);
        }
        // Only the already accepted review/mount coordinates choose this read-only semantic
        // request. The normal Worker rechecks owner consent, grants and immutable package identity.
        // Mark its durable intent before dispatch; a failed/limited result is never auto-retried.
        var arguments = Element(new
        {
            name = "plugin.controls",
            arguments = new { presentationId = PluginControlReferences.ReviewedReference, mountId = mount, operation = "inspect" },
            workingDirectory = input.WorkingDirectory
        });
        var next = state with
        {
            Phase = "tool", PendingName = "plugin.controls", PendingArguments = arguments,
            PendingGeneration = null, DeferredToolName = null, DeferredToolArguments = null,
            InitialQaObservations = [.. state.InitialQaObservations, new(presentation, mount)]
        };
        return Fits(next) ? Invoke(next, Tool, arguments) : ContextFull(state);
    }

    private static object? InitialQaObservationProjection(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is not { } presentation ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal)) return null;
        var notAttempted = input.HostPluginTestMountIds.Where(mount =>
            !state.InitialQaObservations.Any(item => item.MountId == mount) &&
            !state.NativeControlCoverage.Any(item => item.PresentationId == presentation && item.MountId == mount &&
                HasQaObservationReceipt(state, item.EffectId, failed: false))).ToArray();
        var limited = state.InitialQaObservations.Count >= MaximumInitialQaObservations && notAttempted.Length != 0;
        return new
        {
            automaticAttemptLimit = MaximumInitialQaObservations, attempts = state.InitialQaObservations,
            schemaLookupAttempted = state.InitialQaSchemaLookupAttempted,
            notAttemptedMountIds = notAttempted,
            limitation = limited ? "The bounded automatic observation prelude has reached its four-attempt limit. These remaining approved mounts have no bootstrap observation; use explicit native-control actions when their requested behavior needs inspection." : null
        };
    }

    private static HarnessState RetainInitialQaObservation(HarnessState before, HarnessState after, WorkflowEffect effect)
    {
        if (before.InitialQaObservations.LastOrDefault() is not { EffectId: null } attempt ||
            !InitialQaObservationPending(before, attempt)) return after;
        return after with
        {
            InitialQaObservations = after.InitialQaObservations.Select(item => item == attempt
                ? item with { EffectId = effect.Id } : item).ToArray()
        };
    }

    private static bool InitialQaObservationPending(HarnessState state, InitialQaObservation attempt) =>
        state.Phase == "tool" && state.PendingName == "plugin.controls" &&
        PendingToolArguments(state) is { } arguments &&
        TryText(arguments, "presentationId", 32, out var presentation) && presentation == PluginControlReferences.ReviewedReference &&
        TryText(arguments, "mountId", 129, out var mount) && mount == attempt.MountId &&
        TryText(arguments, "operation", 16, out var operation) && operation == "inspect";

    private static void ValidateInitialQaObservations(Input input, HarnessState state)
    {
        if (state.InitialQaSchemaLookupAttempted && !NeedsInitialQaObservation(input))
            throw new ArgumentException("The initial QA schema preparation has no accepted installed-QA continuation binding.");
        if (state.InitialQaObservations is null || state.InitialQaObservations.Count > MaximumInitialQaObservations ||
            state.InitialQaObservations.Any(item => item is null) ||
            state.InitialQaObservations.Select(item => item.MountId).Distinct(StringComparer.Ordinal).Count() != state.InitialQaObservations.Count ||
            state.InitialQaObservations.Any(item => item is null ||
                item.PresentationId != input.AutomaticPluginTestPresentationId ||
                !input.HostPluginTestMountIds.Contains(item.MountId, StringComparer.Ordinal) ||
                !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) ||
                (item.EffectId is null
                    ? item != state.InitialQaObservations.Last() || !InitialQaObservationPending(state, item)
                    : !ValidEffectId(item.EffectId) || state.ToolReceipts.SingleOrDefault(receipt => receipt.EffectId == item.EffectId) is not
                        { Name: "plugin.controls", State: "Succeeded" or "Failed" })))
            throw new ArgumentException("The initial automatic QA observations have no exact accepted mount and retained effect binding.");
    }
}
