using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.PluginLifecycle;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record SemanticInterfaceDecision(string Id, string PackageId, string PackageVersion,
        string PackageFingerprint, string RequestSha256, string ReviewSha256, string BindingGeneration,
        bool ObservedControlTestsAllowed, bool ControlTestsAllowed, bool ConfirmInstall);

    private sealed record SemanticInterfaceCandidate(InterfaceRequest Request, InterfaceReviewStateIdentity Identity,
        DesktopPluginManifest? Manifest, DesktopPluginInfo? Installed, InterfaceReviewCardState? Card,
        WorkflowRun? Origin, string? Blocker);

    private async Task<DesktopControlResponse> InspectSemanticInterfaceReviewsAsync(
        DesktopControlRequest request, CancellationToken token)
    {
        var id = request.Arguments.TryGetProperty("id", out var selected)
            ? JsonString(selected, "id", 128, allowEmpty: false) : null;
        var candidates = await ReadSemanticInterfaceCandidatesAsync(token);
        return DesktopControlProtocol.Result(request, "completed", new
        {
            schema = "pegasus.semantic-interface-owner-review/1",
            semanticSelection = SemanticAttachSelection(),
            scope = "selected native chat and current local owner; at most five actual review cards",
            notice = DesktopPluginCatalog.NativeTrustNotice,
            explicitDecision = "interface.owner.review requires a new, typed owner confirmation and an explicit control-test choice. Inspection grants nothing.",
            reviews = candidates.Where(item => id is null || item.Request.Id == id).Select(item => new
            {
                id = item.Request.Id,
                conversationId = item.Request.SessionId,
                projectId = item.Origin?.Submission.ProjectId,
                source = item.Request.Source,
                sourceRunId = item.Request.JobId,
                sourceStatus = item.Origin?.Status.ToString().ToLowerInvariant() ?? "unavailable",
                profileId = item.Request.ProfileId,
                status = item.Request.Status,
                manifestPath = item.Request.Path,
                generatedSourceRoot = item.Request.GeneratedSourceRoot,
                packageFingerprint = item.Request.PackageFingerprint,
                package = item.Manifest is null ? null : new
                {
                    item.Manifest.Id, item.Manifest.Name, item.Manifest.Version,
                    item.Manifest.Description, item.Manifest.ExclusiveGroup
                },
                installed = item.Installed is null ? null : new
                {
                    item.Installed.Id, item.Installed.Version, item.Installed.Enabled,
                    generation = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Installed.Directory)),
                    item.Installed.ManifestPath,
                    needsAttention = item.Installed.Error is not null
                },
                card = new
                {
                    present = item.Card is not null,
                    expanded = item.Card?.IsExpanded,
                    controlTestsAllowed = item.Card?.ControlTestsAllowed
                },
                bindingGeneration = item.Identity.BindingGeneration,
                requestSha256 = item.Identity.RequestSha256,
                reviewSha256 = item.Identity.ReviewSha256,
                eligible = item.Blocker is null,
                blocker = item.Blocker
            }).ToArray()
        });
    }

    private async Task<DesktopControlResponse> ReviewSemanticInterfaceAsync(
        DesktopControlRequest request, CancellationToken token)
    {
        var decision = ReadSemanticInterfaceDecision(request.Arguments);
        var candidates = await ReadSemanticInterfaceCandidatesAsync(token);
        var selected = candidates.SingleOrDefault(item => item.Request.Id == decision.Id)
            ?? throw new InvalidOperationException("The exact review card is absent from the selected chat.");
        RequireSemanticInterfaceDecision(selected, decision);
        RequireRenderedSemanticInterfaceCard(selected);
        if (!decision.ConfirmInstall)
            return DesktopControlProtocol.Result(request, "rejected", new { id = decision.Id, installed = false },
                "The typed owner decision declined installation. The exact request remains pending.");
        RequireSemanticAttachCurrent();
        RequireCurrentReview(selected.Identity, selected.Request);
        token.ThrowIfCancellationRequested();

        // This same-user semantic endpoint is an owner decision route; no model-visible
        // semantic tool or Worker grant is added. The typed decision replaces the visible Yes/No dialog only for
        // this exact reviewed card; ApplyInterfaceRequest retains the normal journal, claim,
        // immutable install/load, completion and unknown-outcome recovery sequence.
        var authority = CaptureSemanticAttachAuthority()
            ?? throw new InvalidOperationException("Plugin review requires the captured semantic owner.");
        string? ownClaimId = null;
        try
        {
            await ApplyInterfaceRequest(selected.Request, nativePluginApproved: true,
                explicitOwnerSessionId: selected.Request.SessionId,
                controlTestsAllowed: decision.ControlTestsAllowed, activateWindow: false,
                expectedReviewIdentity: selected.Identity, claimIdSelected: id => ownClaimId = id,
                currentOwner: authority, reviewToken: token);
            RequireSemanticAttachCurrent();
            token.ThrowIfCancellationRequested();
            using var owner = CreateIndependentClient();
            var final = await owner.GetAsync<InterfaceRequest>("interface.get",
                new { id = decision.Id, sessionId = selected.Request.SessionId }, cancellationToken: token);
            RequireSemanticAttachCurrent();
            return ClassifySemanticInterfaceOutcome(request, selected, decision, ownClaimId, final);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // After entering the normal owner flow, absence of a reply is not absence of an
            // install. The retained outcome card is the only recovery route; never replay.
            return DesktopControlProtocol.Result(request, "uncertain", new
            {
                id = decision.Id, claimId = ownClaimId, code = "review_outcome_unconfirmed",
                next = "Inspect the original interface request and retained outcome; do not resubmit this decision."
            }, "The owner review outcome is unconfirmed. Inspect its retained request and outcome before another action.");
        }
    }

    private async Task<IReadOnlyList<SemanticInterfaceCandidate>> ReadSemanticInterfaceCandidatesAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        var selection = SemanticAttachSelection();
        var connection = ServiceConnection;
        var client = SemanticAttachClient();
        var serviceKey = interfaceOutcomeServiceKey;
        if (!ServiceIsLocal || !IsNativeChat || selection.ConversationId is null || client is null ||
            string.IsNullOrWhiteSpace(selection.ConnectionIdentity) || serviceKey is null)
            throw new InvalidOperationException("Open the original native chat on its owning local Worker before reviewing plugins.");
        void Current()
        {
            token.ThrowIfCancellationRequested();
            RequireSemanticAttachCurrent();
            if (!ServiceIsLocal || !IsNativeChat || selection != SemanticAttachSelection() ||
                !ReferenceEquals(connection, ServiceConnection) || !ReferenceEquals(client, SemanticAttachClient()) ||
                serviceKey != interfaceOutcomeServiceKey)
                throw new InvalidOperationException("The selected chat or owner changed during interface inspection.");
        }

        Current();
        using var owner = CreateIndependentClient();
        var page = await owner.GetAsync<List<InterfaceRequest>>("interface.list",
            new { sessionId = selection.ConversationId, limit = 20 }, cancellationToken: token);
        Current();
        var candidates = new List<SemanticInterfaceCandidate>();
        foreach (var item in ActionableInterfaceRequests(page).Where(item => item.Kind == "plugin" && item.Status == "pending"))
        {
            token.ThrowIfCancellationRequested();
            DesktopPluginManifest? manifest = null;
            DesktopPluginInfo? installed = null;
            Exception? inspectionError = null;
            try
            {
                manifest = desktopPluginCatalog.Inspect(item.Path);
                installed = desktopPluginCatalog.List().SingleOrDefault(plugin => plugin.Id == manifest.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException) { inspectionError = error; }
            Current();
            var identity = InterfaceReviewStateIdentity.Create(item, PluginBinding(),
                ServiceConnectionIdentity, knowledgeConnectionVersion, InterfaceReviewAction(item, false),
                manifest, installed, inspectionError);
            var displayed = interfaceRequests.Children.OfType<Expander>().Any(card =>
                AutomationProperties.GetAutomationId(card) == "InterfaceRequest-" + item.Id);
            var cardState = displayed ? interfaceReviewStates.Current(item.Id) : null;
            string? blocker = null;
            if (inspectionError is not null || manifest is null) blocker = "package-inspection-unavailable";
            else if (installed?.Error is not null) blocker = "installed-package-needs-attention";
            else if (cardState is null || cardState.Identity != identity) blocker = "review-card-changed-or-not-open";
            else if (item.Source != "workflow") blocker = "workflow-origin-required";
            else if (item.ClaimId is not null || item.CompletedAt is not null || item.PluginTests is not null ||
                     item.StopRequested || item.RepairScheduled || item.Error is not null || item.FailureStage is not null)
                blocker = "request-no-longer-pending";
            else if (!IsSha256(item.PackageFingerprint)) blocker = "package-fingerprint-unavailable";
            else
            {
                try
                {
                    if (NativeDesktopPluginPackage.ComputeFingerprint(item.Path) != item.PackageFingerprint)
                        blocker = "package-bytes-changed";
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { blocker = "package-fingerprint-unavailable"; }
            }
            Current();
            if (blocker is null && interfaceOutcomeJournal.Read(serviceKey, item.Id) is not null)
                blocker = "retained-outcome-needs-confirmation";
            WorkflowRun? origin = null;
            if (item.Source == "workflow")
            {
                try { origin = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = item.JobId }, cancellationToken: token); }
                catch (Exception error) when (error is not OperationCanceledException)
                { blocker ??= "source-run-unavailable"; }
                Current();
            }
            blocker ??= SemanticInterfaceOriginBlocker(item, origin, selection.ProjectId);
            candidates.Add(new(item, identity, manifest, installed, cardState, origin, blocker));
        }
        Current();
        return candidates;
    }

    private static SemanticInterfaceDecision ReadSemanticInterfaceDecision(JsonElement arguments)
    {
        EnsureSemanticArguments(arguments, "id", "packageId", "packageVersion", "packageFingerprint",
            "requestSha256", "reviewSha256", "bindingGeneration", "observedControlTestsAllowed",
            "controlTestsAllowed", "confirmInstall", "expected");
        if (arguments.EnumerateObject().Count() != 11)
            throw new ArgumentException("Owner review requires every exact inspected coordinate and both explicit choices.");
        var decision = new SemanticInterfaceDecision(
            RequiredString(arguments, "id", 128), RequiredString(arguments, "packageId", 128),
            RequiredString(arguments, "packageVersion", 64), RequiredString(arguments, "packageFingerprint", 64),
            RequiredString(arguments, "requestSha256", 64), RequiredString(arguments, "reviewSha256", 64),
            RequiredString(arguments, "bindingGeneration", 128),
            RequiredBool(arguments, "observedControlTestsAllowed"),
            RequiredBool(arguments, "controlTestsAllowed"), RequiredBool(arguments, "confirmInstall"));
        if (!IsSha256(decision.PackageFingerprint) || !IsSha256(decision.RequestSha256) || !IsSha256(decision.ReviewSha256))
            throw new ArgumentException("Owner review fingerprints must be exact lowercase SHA-256 values.");
        return decision;
    }

    private static void RequireSemanticInterfaceDecision(SemanticInterfaceCandidate candidate,
        SemanticInterfaceDecision decision)
    {
        if (candidate.Blocker == "source-workflow-definition-unsupported")
            throw new InvalidOperationException(ChatPluginInstallDefinitionCompatibility.UnsupportedMessage);
        if (candidate.Blocker is not null || candidate.Manifest is null || candidate.Card is null ||
            candidate.Request.Id != decision.Id || candidate.Request.Status != "pending" ||
            candidate.Request.PackageFingerprint != decision.PackageFingerprint ||
            candidate.Manifest.Id != decision.PackageId || candidate.Manifest.Version != decision.PackageVersion ||
            candidate.Identity.RequestSha256 != decision.RequestSha256 ||
            candidate.Identity.ReviewSha256 != decision.ReviewSha256 ||
            candidate.Identity.BindingGeneration != decision.BindingGeneration ||
            candidate.Card.ControlTestsAllowed != decision.ObservedControlTestsAllowed)
            throw new InvalidOperationException("The selected review card, package, source or consent changed. Inspect it again before a new decision.");
    }

    private void RequireRenderedSemanticInterfaceCard(SemanticInterfaceCandidate candidate)
    {
        Dispatcher.VerifyAccess();
        if (candidate.Card is null ||
            !ReferenceEquals(interfaceReviewStates.Current(candidate.Request.Id), candidate.Card) ||
            !interfaceRequests.Children.OfType<Expander>().Any(card =>
                AutomationProperties.GetAutomationId(card) == "InterfaceRequest-" + candidate.Request.Id))
            throw new InvalidOperationException("The visible review card changed. Inspect it again before a new decision.");
    }

    private static string? SemanticInterfaceOriginBlocker(InterfaceRequest request, WorkflowRun? origin,
        string? selectedProjectId, bool allowVerifiedWork = false)
    {
        if (request.Source != "workflow") return "workflow-origin-required";
        if (origin is null) return "source-run-unavailable";
        // Worker admits and fingerprints Work presentations. Only the explicit owner UI may offer the review.
        var manualWork = allowVerifiedWork && request.Kind == "plugin" && request.PackageFingerprint is { Length: 64 } &&
            origin.Submission.DefinitionId != ConversationWorkflowContract.DefinitionId &&
            origin.Submission.ConversationId == origin.Id && origin.Status == WorkflowStatus.Completed;
        if (!manualWork && !ChatPluginInstallDefinitionCompatibility.Supports(
                origin.Submission.DefinitionId, origin.Submission.DefinitionVersion))
            return "source-workflow-definition-unsupported";
        if (origin.Id != request.JobId || origin.Submission.RequestId != request.JobId ||
            origin.Submission.ConversationId != request.SessionId || origin.Submission.ProfileId != request.ProfileId ||
            origin.Submission.ProjectId != selectedProjectId ||
            origin.Submission.ParentRunId is not null ||
            origin.Submission.Caller?.Kind != Cerebral.Contracts.EventActorKind.User ||
            origin.CancelRequested || origin.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.Cancelling)
            return "source-run-ineligible";
        return null;
    }

    private static DesktopControlResponse ClassifySemanticInterfaceOutcome(DesktopControlRequest request,
        SemanticInterfaceCandidate selected, SemanticInterfaceDecision decision, string? ownClaimId,
        InterfaceRequest final)
    {
        if (ownClaimId is null || final.Id != selected.Request.Id || final.SessionId != selected.Request.SessionId ||
            final.JobId != selected.Request.JobId || final.ProfileId != selected.Request.ProfileId ||
            final.Path != selected.Request.Path || final.PackageFingerprint != selected.Request.PackageFingerprint ||
            final.ClaimId != ownClaimId)
            return DesktopControlProtocol.Result(request, "uncertain", new { id = decision.Id, code = "claim_not_confirmed" },
                "The exact owner claim could not be confirmed. Inspect retained outcome; do not replay.");
        if (final.Status == "completed" && final.Plugin is { SchemaVersion: 1 } plugin &&
            plugin.Id == decision.PackageId && plugin.Version == decision.PackageVersion &&
            plugin.ControlTestsAllowed == decision.ControlTestsAllowed && final.CompletedAt is not null)
            return DesktopControlProtocol.Result(request, "completed", new
            {
                id = decision.Id, final.Status, final.ClaimId, final.Plugin,
                controlTestsAllowed = decision.ControlTestsAllowed,
                pluginTests = final.PluginTests,
                automaticQa = final.PluginTests?.Status ?? "not-scheduled"
            });
        if (final.Status == "failed" && final.CompletedAt is not null)
            return DesktopControlProtocol.Result(request, "failed", new
            {
                id = decision.Id, final.Status, final.ClaimId, final.FailureStage, final.Error
            }, "The original owner review failed. Inspect its retained outcome; do not replay the install.");
        return DesktopControlProtocol.Result(request, "uncertain", new
        {
            id = decision.Id, final.Status, final.ClaimId, code = "completion_not_confirmed"
        }, "The owner review has no confirmed final receipt. Inspect its retained outcome; do not replay.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
