using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private sealed record InitialFailedReviewRead(string PresentationId, string? EffectId = null);

    private static bool NeedsFailedReviewRead(Input input) =>
        input.AutomaticPluginTestPresentationId is not null && input.HostPluginTestMountIds.Count == 0 &&
        !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) &&
        new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" }
            .All(name => input.RequiredSuccessfulTools.Contains(name, StringComparer.Ordinal));

    private static WorkflowStep? InitialFailedReviewReadPrerequisite(Input input, HarnessState state)
    {
        if (!NeedsFailedReviewRead(input) || !state.CatalogLoaded || state.NativeReviewFailure is not null ||
            state.InitialFailedReviewRead is not null) return null;
        // Legacy completed decisions and deferred tool selections need the same source/receipt
        // headroom as a fresh request, before any durable read intent is committed.
        if (ContextPressurePrerequisite(input, state) is { } pressure) return pressure;
        if (!state.Selected.Any(item => item.Name == "interface.status" && item.InputSchema is { ValueKind: JsonValueKind.Object }))
        {
            // Older saved runs may lack the exact schema. Preserve the normal lookup quota and
            // retain one preparation attempt so an unavailable schema cannot create an auto-loop.
            if (state.InitialFailedReviewSchemaLookupAttempted) return null;
            return RequestCatalog(input, state with { InitialFailedReviewSchemaLookupAttempted = true },
                ["interface.status"], prepareRelated: false);
        }
        var presentation = input.AutomaticPluginTestPresentationId!;
        var arguments = InitialFailedReviewReadArguments(input, presentation);
        var next = state with
        {
            Phase = "tool", PendingName = "interface.status", PendingArguments = arguments,
            PendingGeneration = null, DeferredToolName = null, DeferredToolArguments = null,
            InitialFailedReviewRead = new(presentation)
        };
        // The accepted continuation chooses only this exact read coordinate. Worker dispatch
        // still enforces normal grants/owner identity; only the strict receipt adopter earns proof.
        return Fits(next) ? Invoke(next, Tool, arguments) : ContextFull(state);
    }

    private static JsonElement InitialFailedReviewReadArguments(Input input, string presentation) => Element(new
    {
        name = "interface.status", arguments = new { requestId = presentation }, workingDirectory = input.WorkingDirectory
    });

    private static object? InitialFailedReviewReadProjection(Input input, HarnessState state) => !NeedsFailedReviewRead(input)
        ? null : new
        {
            automaticAttemptLimit = 1, attempt = state.InitialFailedReviewRead,
            schemaLookupAttempted = state.InitialFailedReviewSchemaLookupAttempted,
            completeFailedReviewEvidenceAdopted = state.NativeReviewFailure is not null,
            limitation = state.InitialFailedReviewRead is { EffectId: not null } && state.NativeReviewFailure is null
                ? "The exact failed-review read was attempted without complete admissible evidence. Its actual receipt is retained; there is no automatic retry or inferred source/package identity."
                : state.InitialFailedReviewSchemaLookupAttempted && state.InitialFailedReviewRead is null
                    ? "The one schema preparation attempt did not provide an exact read. No failed-review identity has been inferred."
                    : null
        };

    private static HarnessState RetainInitialFailedReviewRead(Input input, HarnessState before, HarnessState after, WorkflowEffect effect)
    {
        if (!NeedsFailedReviewRead(input)) return after;
        // A legacy exact read may already be dispatched without the new intent field. Its real
        // correlated terminal effect consumes the one attempt, even when failed or incomplete;
        // an unrelated coordinate never consumes this repair's accepted exact read.
        var attempt = before.InitialFailedReviewRead ?? new(input.AutomaticPluginTestPresentationId!);
        return attempt.EffectId is null && InitialFailedReviewReadPending(input, before, attempt)
            ? after with { InitialFailedReviewRead = attempt with { EffectId = effect.Id } } : after;
    }

    private static bool InitialFailedReviewReadPending(Input input, HarnessState state, InitialFailedReviewRead attempt) =>
        state.Phase == "tool" && state.PendingName == "interface.status" && state.PendingArguments is { } pending &&
        JsonElement.DeepEquals(pending, InitialFailedReviewReadArguments(input, attempt.PresentationId));

    private static void ValidateInitialFailedReviewRead(Input input, HarnessState state)
    {
        if (state.InitialFailedReviewRead is null && !state.InitialFailedReviewSchemaLookupAttempted) return;
        if (!NeedsFailedReviewRead(input) || state.InitialFailedReviewRead is { } attempt &&
            (attempt.PresentationId != input.AutomaticPluginTestPresentationId ||
             (attempt.EffectId is null
                 ? !InitialFailedReviewReadPending(input, state, attempt)
                 : !ValidEffectId(attempt.EffectId) || state.ToolReceipts.SingleOrDefault(receipt => receipt.EffectId == attempt.EffectId) is not
                     { Name: "interface.status", State: "Succeeded" or "Failed" })))
            throw new ArgumentException("The initial failed-review read has no exact accepted repair continuation and retained effect binding.");
    }
}
