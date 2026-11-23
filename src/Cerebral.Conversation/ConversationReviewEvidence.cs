using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private sealed record NativeReviewFailure(string PresentationId, string SourceManifest,
        string PackageFingerprint, string EffectId);

    private static HarnessState AdoptFailedNativeReviewEvidence(Input input, HarnessState state,
        WorkflowEffect effect, ToolData? data, string runId)
    {
        if (state.PendingName != "interface.status" || data is null || data.Truncated ||
            data.Evidence is not { ProviderId: "cerebral.interface", Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } ||
            !SuccessfulEffect(effect, data) || Encoding.UTF8.GetByteCount(data.Content) > 32 * 1024 ||
            input.AutomaticPluginTestPresentationId is not { } presentation ||
            input.HostPluginTestMountIds.Count != 0 ||
            PendingToolArguments(state) is not { } arguments ||
            !TryText(arguments, "requestId", 32, out var requested) || requested != presentation ||
            data.Evidence.RequestId != presentation) return state;
        try
        {
            using var parsed = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var body = parsed.RootElement;
            UniqueProperties(body);
            if (body.ValueKind != JsonValueKind.Object ||
                !TryText(body, "requestId", 32, out var returned) || returned != presentation ||
                !TryText(body, "status", 32, out var status) || status != "failed" ||
                !TryText(body, "kind", 32, out var kind) || kind != "plugin" ||
                !TryText(body, "failureStage", 32, out var stage) || stage is not ("NativeLoad" or "ContributionValidation") ||
                !TryText(body, "sourceManifest", MaximumBindingPathCharacters, out var source) ||
                !TryCanonicalAbsolutePath(source, out var canonicalSource) ||
                !TryText(body, "packageFingerprint", 64, out var fingerprint) || !ValidPackageFingerprint(fingerprint) ||
                !TryText(body, "error", 4096, out _) ||
                body.TryGetProperty("plugin", out var plugin) && plugin.ValueKind != JsonValueKind.Null ||
                body.TryGetProperty("resource", out var resource) && resource.ValueKind != JsonValueKind.Null ||
                !body.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Object ||
                !TryText(tests, "mode", 32, out var mode) || mode != InterfacePluginTestModes.RepairBeforeInstall ||
                !TryText(tests, "requestId", 256, out var requestId) || requestId != runId) return state;
            var prior = state.NativeReviewFailure;
            if (prior is not null && (prior.PresentationId != presentation ||
                !PathEquals(prior.SourceManifest, canonicalSource) ||
                !string.Equals(prior.PackageFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                return state;
            return state with { NativeReviewFailure = new(presentation, canonicalSource, fingerprint.ToLowerInvariant(), effect.Id) };
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return state; }
    }

    private static WorkflowStep? RequireNativeReviewReplacement(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is null ||
            state.NativeReviewFailure is null &&
            (input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal) ||
             !new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" }
                 .All(name => input.RequiredSuccessfulTools.Contains(name, StringComparer.Ordinal)))) return null;
        var diagnostic = state.NativeReviewFailure is { } review
            ? "The failed native review identifies " + review.SourceManifest + " with fingerprint " + review.PackageFingerprint +
              " (receipt " + review.EffectId + "). A repair must build after that evidence, verify different bytes at the same source manifest, and present them for separate owner review."
            : "No complete interface.status evidence identifies this exact failed review's source package and repair-before-install continuation. Read its exact requestId once; do not infer identity from assignment text or a missing mount list.";
        if (state.NativeControlFinalCorrections >= MaximumRepairs)
            return Finish(state, false, "Automatic plugin repair has no evidenced replacement after two completion corrections. " + diagnostic);
        return Repair(input, state with { NativeControlFinalCorrections = state.NativeControlFinalCorrections + 1 },
            diagnostic + " An unrelated source, unchanged package, or successful tool receipt cannot prove this repair. Use blocked for an actual limitation; do not claim installation or a functional pass.");
    }

    private static void ValidateNativeReviewFailure(HarnessState state)
    {
        if (state.NativeReviewFailure is not { } failure) return;
        if (!Guid.TryParseExact(failure.PresentationId, "N", out var presentation) || presentation == Guid.Empty ||
            !CanonicalStoredPath(failure.SourceManifest) || !ValidPackageFingerprint(failure.PackageFingerprint) ||
            !ValidEffectId(failure.EffectId) ||
            !state.ToolReceipts.Any(item => item.EffectId == failure.EffectId && item.Name == "interface.status" &&
                item.State == nameof(WorkflowEffectState.Succeeded) && item.Outcome == nameof(ToolExecutionOutcome.Succeeded) && !item.Truncated))
            throw new ArgumentException("The retained failed native-review identity has no matching status receipt.");
    }
}
