using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private async Task<int> CheckPluginChangeReviewCardsAsync(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var binding = new DesktopConversationBinding("review-generation", "review-chat");
        const string endpoint = "review-fixture-pipe";
        const int connectionVersion = 4;
        InterfaceRequest Request(string sessionId = "review-chat") => new()
        {
            Id = "review-request", SessionId = sessionId, JobId = "review-run", ProfileId = "review-profile",
            Source = "workflow", Kind = "plugin", Status = "pending",
            Path = @"C:\fixture\build\desktop-plugin.json",
            GeneratedSourceRoot = @"C:\fixture\authoring",
            PackageFingerprint = new string('a', 64)
        };
        var request = Request();
        var manifest = new DesktopPluginManifest
        {
            Id = "fixture-note", Name = "Fixture note", Version = "2.0.0",
            Description = "A native note with saved data.", ExclusiveGroup = "fixture-provider"
        };
        var draft = new DesktopDraftSnapshot(binding, 12, "Keep my draft exactly.\nΔ café\n");
        var captured = new PluginChangeCorrection(request.Id, request.SessionId, request.Path, request.PackageFingerprint,
            manifest.Id, manifest.Name, manifest.Version, draft, endpoint, connectionVersion);
        var replacements = 0;
        var executionCalls = 0;
        var serviceCalls = 0;
        var host = new DesktopPluginHostAdapter(Dispatcher,
            () => new(draft.Binding, "Fixture review", null, null, false), () => draft,
            (update, token) =>
            {
                token.ThrowIfCancellationRequested();
                replacements++;
                draft = new(draft.Binding, draft.Revision + 1, update.Text);
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            },
            (_, _) => { executionCalls++; throw new InvalidOperationException("Correction cannot submit."); },
            (_, _) => { executionCalls++; throw new InvalidOperationException("Correction cannot steer."); },
            (_, _) => { executionCalls++; throw new InvalidOperationException("Correction cannot stop."); },
            _ => { }, serviceCommand: (_, _, _) =>
            {
                serviceCalls++;
                throw new InvalidOperationException("Correction cannot call the service.");
            });

        Task<DesktopOperationResult> Place(PluginChangeCorrection? capture = null, InterfaceRequest? candidate = null,
            DesktopConversationBinding? selected = null, string connection = endpoint, int version = connectionVersion,
            bool local = true, bool newRequest = false) => PlacePluginChangeCorrectionAsync(capture ?? captured,
                candidate ?? request, host, local, selected ?? binding, connection, version, cancellationToken, newRequest);

        var original = draft.Text;
        var placed = await Place();
        Check(placed.State == DesktopOperationState.Completed && replacements == 1 &&
            draft.Text.StartsWith(original + "\n\n", StringComparison.Ordinal) &&
            draft.Text.EndsWith("Change the proposal as follows:\n", StringComparison.Ordinal) &&
            draft.Text.Contains("fixture-note, 2.0.0", StringComparison.Ordinal) &&
            draft.Text.Contains(request.Path, StringComparison.Ordinal),
            "A plugin correction preserves the exact Unicode/multiline draft and adds editable instructions for the exact reviewed target.");
        Check(request.Status == "pending" && request.ClaimId is null && request.Plugin is null &&
            executionCalls == 0 && serviceCalls == 0,
            "A correction neither approves, claims, installs, enables, submits, steers, or performs a service operation.");
        var acceptedDraft = draft.Text;
        Check((await Place()).State == DesktopOperationState.Rejected && replacements == 1 && draft.Text == acceptedDraft,
            "The same correction cannot replace a draft whose revision changed after the first insertion.");

        draft = captured.Draft with { Revision = captured.Draft.Revision + 1, Text = "My newly typed text" };
        Check((await Place()).State == DesktopOperationState.Rejected && replacements == 1 && draft.Text == "My newly typed text",
            "Typing after card creation rejects the old correction and preserves the newer draft.");
        draft = captured.Draft with { Revision = captured.Draft.Revision + 1 };
        Check((await Place()).State == DesktopOperationState.Rejected && replacements == 1,
            "Editing back to identical text still rejects the stale draft revision.");
        draft = captured.Draft;
        Check((await Place(selected: binding with { Generation = "reconnected" })).State == DesktopOperationState.Rejected &&
            (await Place(selected: binding with { SessionId = "other-chat" })).State == DesktopOperationState.Rejected &&
            (await Place(connection: "other-service")).State == DesktopOperationState.Rejected &&
            (await Place(version: connectionVersion + 1)).State == DesktopOperationState.Rejected &&
            (await Place(local: false)).State == DesktopOperationState.Rejected && replacements == 1,
            "Connection generation, conversation, service endpoint/version, and remote service changes reject before editing.");

        Action<InterfaceRequest>[] invalidations =
        [
            value => value.Id = "other-request",
            value => value.SessionId = "other-chat",
            value => value.Path = @"C:\other\desktop-plugin.json",
            value => value.PackageFingerprint = new string('b', 64),
            value => value.Status = "claimed",
            value => value.Status = "completed",
            value => value.Kind = "application"
        ];
        foreach (var invalidate in invalidations)
        {
            var changed = Request();
            invalidate(changed);
            Check((await Place(candidate: changed)).State == DesktopOperationState.Rejected && replacements == 1 &&
                draft.Text == captured.Draft.Text && executionCalls == 0 && serviceCalls == 0,
                "A changed review request remains unmodified: " + changed.Id + "/" + changed.Status + "/" + changed.Kind);
        }
        draft = new(binding, 20, new string('x', DesktopPluginContract.MaxTextChars));
        var crowded = captured with { Draft = draft };
        Check((await Place(capture: crowded)).State == DesktopOperationState.Rejected && replacements == 1 &&
            draft.Text.Length == DesktopPluginContract.MaxTextChars,
            "A correction cannot truncate a full existing draft to make room.");

        draft = new(binding, 21, "");
        var emptyCapture = captured with { Draft = draft };
        Check((await Place(capture: emptyCapture)).State == DesktopOperationState.Completed && replacements == 2 &&
            draft.Text.StartsWith("Correction for the proposed plugin", StringComparison.Ordinal),
            "An empty composer receives an editable correction without synthetic prefix whitespace.");

        draft = new(binding, 30, "My unsent draft\nΔ café");
        var newRequestCapture = captured with { Draft = draft };
        var prepared = await Place(capture: newRequestCapture, newRequest: true);
        Check(prepared.State == DesktopOperationState.Completed && replacements == 3 &&
            draft.Text.StartsWith(newRequestCapture.Draft.Text + "\n\n", StringComparison.Ordinal) &&
            draft.Text.Contains("Earlier request " + request.Id + " cannot be installed", StringComparison.Ordinal) &&
            draft.Text.Contains("Create a distinct request from a new run", StringComparison.Ordinal) &&
            !draft.Text.Contains("Keep installation pending", StringComparison.Ordinal) &&
            request.Status == "pending" && request.ClaimId is null && executionCalls == 0 && serviceCalls == 0,
            "The new-request route appends an editable distinct proposal without replaying or changing the earlier request.");
        var preparedText = draft.Text;
        Check((await Place(capture: newRequestCapture, newRequest: true)).State == DesktopOperationState.Rejected &&
            replacements == 3 && draft.Text == preparedText,
            "A second new-request click cannot duplicate the draft after its revision changed.");
        draft = newRequestCapture.Draft with { Revision = 31, Text = "An independently changed draft" };
        Check((await Place(capture: newRequestCapture, newRequest: true)).State == DesktopOperationState.Rejected &&
            replacements == 3 && draft.Text == "An independently changed draft",
            "The new-request route refuses to overwrite a newer owner draft.");
        draft = newRequestCapture.Draft;
        var claimedNewRequest = Request();
        claimedNewRequest.Status = "claimed";
        Check((await Place(capture: newRequestCapture, selected: binding with { Generation = "other" }, newRequest: true)).State == DesktopOperationState.Rejected &&
            (await Place(capture: newRequestCapture, connection: "other-service", newRequest: true)).State == DesktopOperationState.Rejected &&
            (await Place(capture: newRequestCapture, candidate: claimedNewRequest, newRequest: true)).State == DesktopOperationState.Rejected &&
            replacements == 3 && draft.Text == newRequestCapture.Draft.Text,
            "The new-request route refuses stale binding, service connection, and claimed original request before editing.");

        var details = PendingPluginChangeDetails(request, manifest, null, @"D:\Cerebral\Plugins", @"E:\CerebralData\PluginData");
        var labels = details.Children.OfType<TextBlock>().Select(item => item.Text).ToArray();
        Check(labels.Contains("Proposed action: Install and enable", StringComparer.Ordinal) &&
            labels.Any(text => text.Contains(manifest.Id, StringComparison.Ordinal)) &&
            labels.Contains("Reviewed manifest: " + request.Path, StringComparer.Ordinal) &&
            labels.Contains("Authoring source: " + request.GeneratedSourceRoot, StringComparer.Ordinal) &&
            labels.Contains(@"Plugin library: D:\Cerebral\Plugins", StringComparer.Ordinal) &&
            labels.Contains(@"Plugin data: E:\CerebralData\PluginData\fixture-note", StringComparer.Ordinal),
            "The pending card identifies the exact proposed package and dynamic source, library, and data roots.");
        Check(labels.Any(text => text.Contains("preserves the authoring source and existing plugin data", StringComparison.Ordinal)) &&
            labels.Contains(DesktopPluginCatalog.NativeTrustNotice, StringComparer.Ordinal) &&
            labels.Any(text => text.Contains("disables other enabled providers", StringComparison.Ordinal)) &&
            labels.Contains("Reviewed package fingerprint: " + request.PackageFingerprint, StringComparer.Ordinal),
            "The card discloses data preservation, native full trust, exclusive-provider effects, and the retained reviewed fingerprint.");
        Check(details.Children.OfType<TextBlock>().All(item => item.TextWrapping == TextWrapping.Wrap),
            "Long proposal paths and trust text use the common readable wrapping text controls.");

        var installed = new DesktopPluginInfo(manifest.Id, manifest.Name, "1.0.0", "", false, "old-generation", "old-manifest", null);
        var updateLabels = PendingPluginChangeDetails(request, manifest, installed, "library", "data")
            .Children.OfType<TextBlock>().Select(item => item.Text).ToArray();
        Check(updateLabels.Contains("Proposed action: Update package and enable", StringComparer.Ordinal) &&
            updateLabels.Contains("Currently installed: 1.0.0 · disabled", StringComparer.Ordinal),
            "An existing target shows the proposed update and its actual current enablement.");
        var refreshLabels = PendingPluginChangeDetails(request, manifest, installed with { Version = manifest.Version },
            "library", "data").Children.OfType<TextBlock>().Select(item => item.Text).ToArray();
        Check(refreshLabels.Contains("Proposed action: Refresh reviewed build and enable", StringComparer.Ordinal),
            "A same-version reviewed rebuild identifies refresh instead of claiming a new install.");

        var reviewStates = new InterfaceReviewStateStore();
        var reviewIdentity = InterfaceReviewStateIdentity.Create(request, binding, endpoint, connectionVersion,
            "plugin-install-review", manifest, installedPlugin: null, packageReviewError: null);
        Check(reviewIdentity.MatchesOwner(binding, endpoint, connectionVersion, request.SessionId) &&
            !reviewIdentity.MatchesOwner(binding with { Generation = "other-generation" }, endpoint, connectionVersion,
                request.SessionId),
            "Review consent is bound to the exact conversation binding and service connection generation.");
        var claimedRequest = Request();
        claimedRequest.CreatedAt = request.CreatedAt;
        claimedRequest.Status = "claimed";
        Check(reviewIdentity.MatchesClaimedRequest(claimedRequest),
            "A claim with the exact immutable reviewed request coordinates retains the visible consent boundary.");
        claimedRequest.PackageFingerprint = new string('b', 64);
        Check(!reviewIdentity.MatchesClaimedRequest(claimedRequest),
            "A claim whose reviewed package coordinates changed cannot reuse visible consent.");
        var failedReview = Request();
        failedReview.Status = "failed";
        failedReview.ClaimId = "review-claim";
        failedReview.ClaimedAt = failedReview.CreatedAt.AddSeconds(1);
        failedReview.CompletedAt = failedReview.CreatedAt.AddSeconds(2);
        failedReview.Error = "Native view creation failed.";
        failedReview.FailureStage = InterfaceFailureStage.NativeLoad;
        Check(CanOfferPluginRepairConsent(failedReview),
            "A retained eligible failed review without a continuation may offer explicit repair consent.");
        failedReview.PluginTests = new InterfacePluginTestContinuation(failedReview.Id, failedReview.JobId, 1,
            Mode: InterfacePluginTestModes.RepairBeforeInstall);
        Check(IsPluginRepairCandidate(failedReview) && !CanOfferPluginRepairConsent(failedReview) &&
            InterfaceReviewAction(failedReview, repairEligible: true) == "plugin-repair-status",
            "A retained repair continuation renders status only and cannot receive another consent checkbox or repair action.");
        var firstState = reviewStates.Resolve(reviewIdentity, defaultExpanded: true);
        var firstExpander = new Expander();
        var firstConsent = new CheckBox();
        InterfaceReviewStateAdapter.Bind(firstExpander, firstConsent, firstState);
        Check(firstExpander.IsExpanded && firstConsent.IsChecked == false &&
            firstState.IsExpanded && !firstState.ControlTestsAllowed,
            "A newly rendered pending review starts expanded without granting native control testing consent.");
        firstExpander.IsExpanded = false;
        firstConsent.IsChecked = true;
        Check(!firstState.IsExpanded && firstState.ControlTestsAllowed,
            "Actual WPF expansion and checkbox changes update only the exact review snapshot state.");

        var refreshedState = reviewStates.Resolve(
            InterfaceReviewStateIdentity.Create(request, binding, endpoint, connectionVersion,
                "plugin-install-review", manifest, installedPlugin: null, packageReviewError: null),
            defaultExpanded: true);
        var refreshedExpander = new Expander();
        var refreshedConsent = new CheckBox();
        InterfaceReviewStateAdapter.Bind(refreshedExpander, refreshedConsent, refreshedState);
        Check(ReferenceEquals(firstState, refreshedState) && !refreshedExpander.IsExpanded && refreshedConsent.IsChecked == true,
            "Refreshing the exact same request, connection, conversation generation and manifest preserves expansion and explicit consent.");

        var changedManifest = new DesktopPluginManifest
        {
            Id = manifest.Id, Name = manifest.Name, Version = "2.0.1", Description = manifest.Description,
            Assembly = manifest.Assembly, EntryType = manifest.EntryType, ExclusiveGroup = manifest.ExclusiveGroup
        };
        InterfaceReviewStateIdentity[] changedReviewIdentities =
        [
            reviewIdentity with { SessionId = "other-chat" },
            reviewIdentity with { Status = "claimed" },
            reviewIdentity with { PackageFingerprint = new string('b', 64) },
            reviewIdentity with { BindingGeneration = "other-generation" },
            reviewIdentity with { ConnectionIdentity = "other-service" },
            reviewIdentity with { ConnectionVersion = connectionVersion + 1 },
            reviewIdentity with { Action = "plugin-repair-consent" },
            reviewIdentity with { AuthorizationSha256 = new string('b', 64) },
            reviewIdentity with { RequestSha256 = new string('b', 64) },
            InterfaceReviewStateIdentity.Create(request, binding, endpoint, connectionVersion,
                "plugin-install-review", changedManifest, installedPlugin: null, packageReviewError: null),
            InterfaceReviewStateIdentity.Create(request, binding, endpoint, connectionVersion,
                "plugin-install-review", manifest, installed, packageReviewError: null),
            InterfaceReviewStateIdentity.Create(request, binding, endpoint, connectionVersion,
                "plugin-install-review", manifest, installedPlugin: null,
                packageReviewError: new InvalidOperationException("Package bytes changed."))
        ];
        foreach (var changedIdentity in changedReviewIdentities)
        {
            var changedStore = new InterfaceReviewStateStore();
            var retained = changedStore.Resolve(reviewIdentity, defaultExpanded: true);
            var staleExpander = new Expander();
            var staleConsent = new CheckBox();
            InterfaceReviewStateAdapter.Bind(staleExpander, staleConsent, retained);
            staleExpander.IsExpanded = false;
            staleConsent.IsChecked = true;
            var changed = changedStore.Resolve(changedIdentity, defaultExpanded: changedIdentity.Status == "pending");
            var changedExpander = new Expander();
            var changedConsent = new CheckBox();
            InterfaceReviewStateAdapter.Bind(changedExpander, changedConsent, changed);
            var changedRepair = new Button();
            InterfaceReviewStateAdapter.BindConsentAction(changedConsent, changedRepair);
            Check(!ReferenceEquals(retained, changed) && !changed.ControlTestsAllowed && changedConsent.IsChecked == false &&
                !changedRepair.IsEnabled && changedExpander.IsExpanded == (changedIdentity.Status == "pending"),
                "A changed request, action, status, package, installed state, inspection result, manifest, binding generation or connection receives fresh non-authorizing UI state.");
            staleConsent.IsChecked = false;
            staleConsent.IsChecked = true;
            Check(!changed.ControlTestsAllowed && !changedRepair.IsEnabled,
                "A detached stale checkbox cannot authorize or enable an action on a newly identified review card.");
        }
        var repairState = reviewStates.Resolve(reviewIdentity, defaultExpanded: true);
        var preservedRepairConsent = new CheckBox();
        var preservedRepairButton = new Button();
        InterfaceReviewStateAdapter.Bind(new Expander(), preservedRepairConsent, repairState);
        InterfaceReviewStateAdapter.BindConsentAction(preservedRepairConsent, preservedRepairButton);
        Check(preservedRepairConsent.IsChecked == true && preservedRepairButton.IsEnabled,
            "A same-snapshot refresh restores explicit consent before enabling the newly rendered repair action.");
        reviewStates.RetainOnly(["other-request"]);
        var returnedState = reviewStates.Resolve(reviewIdentity, defaultExpanded: true);
        Check(!ReferenceEquals(returnedState, repairState) && !returnedState.ControlTestsAllowed,
            "Removing a review card prunes its UI state so a later card with the same request identity cannot restore stale consent.");

        var refreshGate = new InterfaceReviewRefreshGate();
        var olderGeneration = refreshGate.Begin();
        var olderResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ApplyWhenCurrent(long generation, Task response)
        {
            await response;
            return refreshGate.IsCurrent(generation);
        }
        var olderApply = ApplyWhenCurrent(olderGeneration, olderResponse.Task);
        var newerGeneration = refreshGate.Begin();
        var newerResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerApply = ApplyWhenCurrent(newerGeneration, newerResponse.Task);
        newerResponse.SetResult();
        Check(await newerApply, "The newest in-flight interface refresh may apply its response.");
        olderResponse.SetResult();
        Check(!await olderApply,
            "An older interface response that completes after a newer refresh cannot repaint obsolete review cards.");

        // Exercise the new owner-only semantic contract with the same review identity used by
        // the real card. These checks never claim a Worker request or install a native package.
        var semanticArguments = CerebralJson.Element(new
        {
            id = request.Id, packageId = manifest.Id, packageVersion = manifest.Version,
            packageFingerprint = request.PackageFingerprint, requestSha256 = reviewIdentity.RequestSha256,
            reviewSha256 = reviewIdentity.ReviewSha256, bindingGeneration = reviewIdentity.BindingGeneration,
            observedControlTestsAllowed = false, controlTestsAllowed = true, confirmInstall = true,
            expected = new { conversationId = request.SessionId, projectId = (string?)null,
                selectionVersion = 7L, connectionVersion, connectionIdentity = endpoint }
        });
        var decision = ReadSemanticInterfaceDecision(semanticArguments);
        var semanticCard = new InterfaceReviewCardState(reviewIdentity, isExpanded: true);
        var source = new WorkflowRun
        {
            Id = request.JobId,
            Submission = new WorkflowSubmission(request.JobId, request.SessionId, request.ProfileId,
                ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
                "", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
                Caller: new(Cerebral.Contracts.EventActorKind.User, "owner")),
            Status = WorkflowStatus.Completed
        };
        var candidate = new SemanticInterfaceCandidate(request, reviewIdentity, manifest, null,
            semanticCard, source, null);
        static bool Refuses(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
        }
        Check(SemanticInterfaceOriginBlocker(request, source, null) is null &&
            SemanticInterfaceOriginBlocker(request, source with { Status = WorkflowStatus.Failed }, null) == "source-run-ineligible" &&
            SemanticInterfaceOriginBlocker(request, source with { CancelRequested = true }, null) == "source-run-ineligible" &&
            SemanticInterfaceOriginBlocker(request, source with { Submission = source.Submission with { DefinitionVersion = "other" } }, null) == "source-workflow-definition-unsupported" &&
            SemanticInterfaceOriginBlocker(request, source, "other-project") == "source-run-ineligible",
            "Semantic owner review admits only the exact live/finished source chat and project, never a failed or cancelled origin.");
        var eligibleSource = PendingPluginSourceReview(request, source, null);
        var failedSource = PendingPluginSourceReview(request, source with { Status = WorkflowStatus.Failed }, null);
        Check(eligibleSource.CanInstall && eligibleSource.SourceLine.Contains("completed", StringComparison.Ordinal) &&
            eligibleSource.SourceLine.Contains(request.Id[..8], StringComparison.Ordinal) &&
            eligibleSource.SourceLine.Contains(request.JobId[..8], StringComparison.Ordinal),
            "A completed source retains the normal review action and identifies its exact request and source run.");
        Check(!failedSource.CanInstall && failedSource.SourceLine.Contains("failed", StringComparison.Ordinal) &&
            failedSource.Explanation.Contains("separate eligible review", StringComparison.Ordinal) &&
            !PendingPluginSourceReview(request, null, null).CanInstall &&
            !PendingPluginSourceReview(request, source with { Status = WorkflowStatus.Cancelled }, null).CanInstall &&
            !PendingPluginSourceReview(request, source, "other-project").CanInstall,
            "Failed, cancelled, unavailable, and mismatched source runs do not offer installation.");
        var workRequest = Request(source.Id);
        var workSource = source with { Submission = source.Submission with
            { DefinitionId = "fixture:work", ConversationId = source.Id } };
        Check(PendingPluginSourceReview(workRequest, workSource, null, allowVerifiedWork: true).CanInstall &&
            !PendingPluginSourceReview(workRequest, workSource, null).CanInstall &&
            !PendingPluginSourceReview(workRequest, workSource with { Status = WorkflowStatus.Running }, null, allowVerifiedWork: true).CanInstall &&
            !PendingPluginSourceReview(workRequest, workSource, "other-project", allowVerifiedWork: true).CanInstall,
            "Only a completed exact owner Work run can offer manual plugin review. Chat automation remains scoped to Chat.");
        var previousLabels = PendingPluginChangeDetails(request, manifest, null, "library", "data", installAvailable: false)
            .Children.OfType<TextBlock>().Select(item => item.Text).ToArray();
        Check(previousLabels.Any(text => text.Contains(manifest.Id, StringComparison.Ordinal)) &&
            previousLabels.Contains("Reviewed manifest: " + request.Path, StringComparer.Ordinal) &&
            previousLabels.Any(text => text.StartsWith("Prepare a new request adds editable instructions", StringComparison.Ordinal)) &&
            !previousLabels.Any(text => text.StartsWith("Proposed action:", StringComparison.Ordinal) ||
                text.StartsWith("Approval copies", StringComparison.Ordinal) ||
                text.StartsWith("Correct request", StringComparison.Ordinal)),
            "An ineligible request identifies its package and new-request draft route without suggesting installation of the old request.");
        RequireSemanticInterfaceDecision(candidate, decision);
        var renderedState = interfaceReviewStates.Resolve(reviewIdentity, defaultExpanded: true);
        var renderedCard = new Expander();
        AutomationProperties.SetAutomationId(renderedCard, "InterfaceRequest-" + request.Id);
        interfaceRequests.Children.Add(renderedCard);
        try
        {
            RequireRenderedSemanticInterfaceCard(candidate with { Card = renderedState });
            Check(Refuses(() => RequireRenderedSemanticInterfaceCard(candidate)),
                "A decision cannot use a detached card state even while a matching card is visible.");
        }
        finally { interfaceRequests.Children.Remove(renderedCard); }
        Check(Refuses(() => RequireRenderedSemanticInterfaceCard(candidate with { Card = renderedState })),
            "A card removed after inspection refuses before claiming the request.");
        interfaceReviewStates.RetainOnly([]);
        Check(decision.ControlTestsAllowed && !decision.ObservedControlTestsAllowed && decision.ConfirmInstall,
            "A typed owner decision can explicitly choose control testing while proving the inspected checkbox was initially off.");
        Check(Refuses(() => RequireSemanticInterfaceDecision(candidate, decision with { ReviewSha256 = new string('b', 64) })) &&
            Refuses(() => RequireSemanticInterfaceDecision(candidate, decision with { PackageFingerprint = new string('b', 64) })) &&
            Refuses(() => RequireSemanticInterfaceDecision(candidate, decision with { BindingGeneration = "other" })) &&
            Refuses(() => RequireSemanticInterfaceDecision(candidate with { Blocker = "retained-outcome-needs-confirmation" }, decision)),
            "Changed package/review/generation coordinates and retained uncertain outcomes refuse before the normal owner flow.");
        semanticCard.ControlTestsAllowed = true;
        Check(Refuses(() => RequireSemanticInterfaceDecision(candidate, decision)),
            "A checkbox change after inspection invalidates the observed card state.");
        semanticCard.ControlTestsAllowed = false;
        var malformed = CerebralJson.Element(new
        {
            id = request.Id, packageId = manifest.Id, packageVersion = manifest.Version,
            packageFingerprint = request.PackageFingerprint, requestSha256 = "BAD", reviewSha256 = reviewIdentity.ReviewSha256,
            bindingGeneration = reviewIdentity.BindingGeneration, observedControlTestsAllowed = false,
            controlTestsAllowed = true, confirmInstall = true,
            expected = new { conversationId = request.SessionId, projectId = (string?)null,
                selectionVersion = 7L, connectionVersion, connectionIdentity = endpoint }
        });
        Check(Refuses(() => ReadSemanticInterfaceDecision(malformed)) &&
            DesktopControlServer.ModeCapabilities(false, true).GetProperty("admittedOperations").EnumerateArray()
                .Any(item => item.GetString() == "interface.owner.review") &&
            !DesktopControlServer.ModeCapabilities(false, false).GetProperty("admittedOperations").EnumerateArray()
                .Any(item => item.GetString() == "interface.owner.review"),
            "Malformed decisions refuse and only the optional semantic-attach capability advertises owner review.");
        var semanticRequest = new DesktopControlRequest("fixture-instance", "fixture-decision", "interface.owner.review", semanticArguments);
        InterfaceRequest Outcome(string status, string claim) => new()
        {
            Id = request.Id, SessionId = request.SessionId, JobId = request.JobId, ProfileId = request.ProfileId,
            Source = request.Source, Kind = request.Kind, Path = request.Path,
            PackageFingerprint = request.PackageFingerprint, ClaimId = claim, Status = status,
            CompletedAt = status is "completed" or "failed" ? DateTimeOffset.UtcNow : null,
            Plugin = status == "completed" ? new InterfacePluginReceipt(manifest.Id, manifest.Version,
                "generation", "reviewed-manifest", [], ControlTestsAllowed: true) : null,
            Error = status == "failed" ? "Native load failed" : null
        };
        Check(ClassifySemanticInterfaceOutcome(semanticRequest, candidate, decision, "own-claim", Outcome("completed", "own-claim")).State == "completed" &&
            ClassifySemanticInterfaceOutcome(semanticRequest, candidate, decision, "own-claim", Outcome("failed", "own-claim")).State == "failed" &&
            ClassifySemanticInterfaceOutcome(semanticRequest, candidate, decision, "own-claim", Outcome("claimed", "own-claim")).State == "uncertain" &&
            ClassifySemanticInterfaceOutcome(semanticRequest, candidate, decision, "own-claim", Outcome("completed", "other-claim")).State == "uncertain",
            "Only this exact claim's completed or failed receipt is final; pending and another owner's claim remain uncertain without replay.");
        Check(executionCalls == 0 && serviceCalls == 0 && request.Status == "pending",
            "Rendering and correction checks execute no installation or service mutation.");
        return checks;
    }
}
