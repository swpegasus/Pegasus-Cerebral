using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly StackPanel interfaceRequests = new();
    private readonly Dictionary<string, ApplicationOutputWindow> applicationWindows = new(StringComparer.Ordinal);
    private readonly InterfaceReviewStateStore interfaceReviewStates = new();
    private readonly InterfaceReviewRefreshGate interfaceReviewRefresh = new();

    private async Task RefreshInterfaceRequests()
    {
        var refreshGeneration = interfaceReviewRefresh.Begin();
        if (CurrentConversationId is not { } sessionId)
        {
            interfaceRequests.Children.Clear();
            interfaceReviewStates.Clear();
            return;
        }
        var binding = PluginBinding();
        var connection = ServiceConnectionIdentity;
        var connectionVersion = knowledgeConnectionVersion;
        var requests = await Get<List<InterfaceRequest>>("interface.list", new { sessionId, limit = 32 });
        if (!interfaceReviewRefresh.IsCurrent(refreshGeneration) || CurrentConversationId != sessionId || PluginBinding() != binding ||
            ServiceConnectionIdentity != connection || knowledgeConnectionVersion != connectionVersion) return;
        var actionable = ActionableInterfaceRequests(requests).ToArray();
        var sourceRuns = new Dictionary<string, WorkflowRun?>();
        foreach (var request in actionable.Where(item => item.Kind == "plugin" && item.Status == "pending" && item.Source == "workflow"))
        {
            sourceRuns[request.Id] = await ReadPendingPluginSourceRunAsync(request);
            if (!interfaceReviewRefresh.IsCurrent(refreshGeneration) || CurrentConversationId != sessionId || PluginBinding() != binding ||
                ServiceConnectionIdentity != connection || knowledgeConnectionVersion != connectionVersion) return;
        }
        var retainedStateIds = new List<string>(actionable.Length);
        interfaceRequests.Children.Clear();
        var recordedOutcomeIds = AddInterfaceOutcomeRecoveryCards(requests, sessionId);
        foreach (var request in actionable.Where(request => !recordedOutcomeIds.Contains(request.Id)))
        {
            var sourceReview = request.Kind == "plugin" && request.Status == "pending" && request.Source == "workflow"
                ? PendingPluginSourceReview(request, sourceRuns.GetValueOrDefault(request.Id), ConversationProjectId, allowVerifiedWork: true)
                : null;
            var installAvailable = sourceReview?.CanInstall ?? true;
            var repairEligible = request.Status == "failed" && IsPluginRepairCandidate(request);
            DesktopPluginManifest? reviewedManifest = null;
            DesktopPluginInfo? installedPlugin = null;
            PluginChangeCorrection? correction = null;
            Exception? packageReviewError = null;
            if (request.Kind == "plugin" && request.Status == "pending")
            {
                try
                {
                    reviewedManifest = desktopPluginCatalog.Inspect(request.Path);
                    installedPlugin = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == reviewedManifest.Id);
                    correction = CapturePluginChangeCorrection(request, reviewedManifest, binding, connection, connectionVersion);
                }
                catch (Exception ex) { packageReviewError = ex; }
            }
            var repairConsentEligible = CanOfferPluginRepairConsent(request);
            var reviewAction = InterfaceReviewAction(request, repairEligible);
            var identity = InterfaceReviewStateIdentity.Create(request, binding, connection, connectionVersion,
                reviewAction, reviewedManifest, installedPlugin, packageReviewError);
            var reviewState = interfaceReviewStates.Resolve(identity, request.Status == "pending");
            retainedStateIds.Add(request.Id);
            var card = new StackPanel();
            card.Children.Add(Ui.Text(request.Kind == "plugin" && request.Status == "pending"
                ? installAvailable ? "Proposed desktop plugin change" : "Previous plugin request"
                : $"Open {request.Kind}: {Path.GetFileName(request.Path)}", 16));
            if (sourceReview is not null)
                card.Children.Add(Ui.Text(sourceReview.SourceLine, 12, Ui.Muted));
            card.Children.Add(Ui.Text(sourceReview is { CanInstall: false } ? sourceReview.Explanation : request.Status == "pending"
                ? request.Kind == "plugin" ? "Awaiting your review. Installation has not been approved." : "Ready to show in this interface." :
                request.Status == "claimed" ? "An interface claimed this request. Check the existing window before opening anything again." :
                "Could not open: " + request.Error, 12,
                sourceReview is { CanInstall: false } ? Ui.ErrorText : Ui.Muted));
            if (reviewedManifest is not null)
                card.Children.Add(PendingPluginChangeDetails(request, reviewedManifest, installedPlugin,
                    desktopPluginCatalog.RootDirectory, desktopPluginDataRoot ?? DesktopPluginCatalog.DefaultDataDirectory,
                    installAvailable));
            else if (packageReviewError is not null)
                card.Children.Add(Ui.Text("Package review failed: " + packageReviewError.Message, 11, Ui.ErrorBorder));
            CheckBox? controlTests = null;
            if (request.Kind == "plugin" && ((request.Status == "pending" && installAvailable) || repairConsentEligible))
            {
                controlTests = new CheckBox { Content = request.Status == "failed"
                        ? "Allow Cerebral to repair this failed review and test a later approved replacement"
                        : "Allow Cerebral to test this plugin’s controls and repair failures within the original request",
                    IsChecked = reviewState.ControlTestsAllowed, Margin = new Thickness(0, 6, 0, 6),
                    ToolTip = request.Status == "failed"
                        ? "Schedules one bounded source-repair turn in this conversation. It does not retry installation or approve a replacement."
                        : "Applies only to this reviewed generation and original request. Any repaired replacement requires separate owner review." };
                AutomationProperties.SetAutomationId(controlTests, "ControlTests-" + request.Id);
                card.Children.Add(controlTests);
            }
            if (request.Status == "pending")
            {
                if (correction is not null)
                {
                    var correct = Ui.Button(installAvailable ? "Correct request" : "Prepare a new request",
                        () => Run(() => CorrectPluginChangeRequestAsync(request, correction, newRequest: !installAvailable)));
                    correct.ToolTip = installAvailable
                        ? "Add an editable correction to this conversation's unchanged draft. This does not approve or install the package."
                        : "Add an editable new-request draft to this conversation. The earlier request cannot be approved, retried, or installed from this card.";
                    AutomationProperties.SetAutomationId(correct, "CorrectRequest-" + request.Id);
                    card.Children.Add(correct);
                }
                if (installAvailable)
                {
                    var open = Ui.Button(request.Kind == "plugin" ? "Review and install" : "Open now",
                        () => Run(() => ApplyInterfaceRequest(request, controlTestsAllowed: controlTests?.IsChecked == true,
                            expectedReviewIdentity: identity)), true);
                    AutomationProperties.SetAutomationId(open, "OpenRequest-" + request.Id); card.Children.Add(open);
                }
            }
            else if (request.Status == "failed" && request.Kind == "plugin")
            {
                if (request.PluginTests is { } repairIntent)
                    card.Children.Add(Ui.Text(
                        repairIntent.Mode == InterfacePluginTestModes.RepairBeforeInstall && repairIntent.Status == "pending"
                            ? "Cerebral repair is queued. The failed review remains unchanged."
                            : "Cerebral continuation: " + (repairIntent.Message ?? repairIntent.Status), 11, Ui.Muted));
                else if (repairConsentEligible)
                {
                    var repairButton = Ui.Button("Ask Cerebral to repair", () => Run(async () =>
                    {
                        var capturedSessionId = request.SessionId;
                        if (!ServiceIsLocal)
                            throw new InvalidOperationException(
                                "Open the failed review from its original local conversation before scheduling repair.");
                        RequireCurrentReview(identity, request);
                        using var ownerClient = CreateIndependentClient();
                        await ownerClient.GetAsync<InterfaceRequest>("interface.repair", new
                        {
                            id = request.Id,
                            sessionId = capturedSessionId,
                            controlTestsAllowed = controlTests?.IsChecked == true
                        });
                        if (!ServiceIsLocal)
                            throw new InvalidOperationException(
                                "Connection or conversation changed while repair was scheduled. Reopen the failed review in its original connection.");
                        RequireCurrentReview(identity, request);
                        await RefreshInterfaceRequests();
                    }), true);
                    InterfaceReviewStateAdapter.BindConsentAction(controlTests!, repairButton);
                    AutomationProperties.SetAutomationId(repairButton, "RepairRequest-" + request.Id); card.Children.Add(repairButton);
                }
                else card.Children.Add(Ui.Text(
                    request.FailureStage is not null
                        ? "This failure occurred in Cerebral's presentation pipeline. The failure is retained; plugin source repair is not scheduled."
                        : "This failure has no completed owner-review evidence for automatic repair. Start a new explicit request.",
                    11, Ui.Muted));
            }
            var expander = new Expander { Header = sourceReview is { CanInstall: false }
                ? "Previous plugin request · cannot install"
                : $"{(request.Kind == "plugin" ? "Review" : "Open")} {request.Kind} · {request.Status}", Content = new Border
                {
                    Child = card, Padding = new Thickness(12), BorderThickness = new Thickness(1),
                    BorderBrush = Ui.Line, Background = Ui.Surface, CornerRadius = new CornerRadius(5)
                },
                Margin = new Thickness(0, 3, 0, 6) };
            AutomationProperties.SetAutomationId(expander, "InterfaceRequest-" + request.Id);
            InterfaceReviewStateAdapter.Bind(expander, controlTests, reviewState);
            interfaceRequests.Children.Add(expander);
        }
        RenderNativePluginManagementReviews();
        interfaceReviewStates.RetainOnly(retainedStateIds);
        await TryChatAuthorizedPendingPluginsAsync(requests, recordedOutcomeIds);
        await RefreshConversationCommunicationAsync(refreshGeneration, sessionId, binding, connection, connectionVersion);
    }

    private static bool IsPluginRepairCandidate(InterfaceRequest request) =>
        request.Source == "workflow" && request.Kind == "plugin" && request.Status == "failed" &&
        (request.FailureStage is null or InterfaceFailureStage.NativeLoad or InterfaceFailureStage.ContributionValidation) &&
        request.ClaimId is not null && request.ClaimedAt is not null && request.CompletedAt is not null &&
        !string.IsNullOrWhiteSpace(request.Error) && request.Plugin is null && request.Resource is null &&
        request.PackageFingerprint is { Length: 64 } fingerprint &&
        fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CanOfferPluginRepairConsent(InterfaceRequest request) =>
        IsPluginRepairCandidate(request) && request.PluginTests is null;

    private static IEnumerable<InterfaceRequest> ActionableInterfaceRequests(IReadOnlyList<InterfaceRequest> requests)
    {
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return requests.Where(request => request.Status is "pending" or "claimed" or "failed")
            .Where(request => request.Status != "failed" || !requests.Any(completed =>
                completed.Status == "completed" && completed.CreatedAt > request.CreatedAt &&
                completed.JobId == request.JobId && completed.Kind == request.Kind &&
                completed.Path.Equals(request.Path, pathComparison)))
            .Take(5);
    }
    private async Task InterfaceEventAsync(ServiceEvent item)
    {
        if (ObserveChatPluginInstallCompletion(item))
        {
            await RefreshInterfaceRequests();
            return;
        }
        if (item.Type != "interface.updated") return;
        var request = item.Data.Deserialize<InterfaceRequest>(CerebralJson.Options);
        if (request is null) return;
        if (request.StopRequested && applicationWindows.TryGetValue(request.Id, out var runningWindow)) runningWindow.StopApplication();
        if (CurrentConversationId is null || request.SessionId != CurrentConversationId) return;
        try
        {
            // Only a newly arriving live request in the selected conversation is automatic.
            // Reconnected pending requests are shown for an explicit click, never replayed.
            if (request.Status == "pending" && request.Kind != "plugin") await ApplyInterfaceRequest(request);
            else
            {
                await RefreshInterfaceRequests();
            }
        }
        catch (Exception ex) { SetStatus("Interface request: " + ex.Message, true); }
    }
    private async Task<ChatPluginInstallApplyResult> ApplyInterfaceRequest(InterfaceRequest request, bool nativePluginApproved = false,
        string? explicitOwnerSessionId = null, bool controlTestsAllowed = false, bool activateWindow = true,
        InterfaceReviewStateIdentity? expectedReviewIdentity = null, Action<string>? claimIdSelected = null,
        Func<bool>? currentOwner = null, CancellationToken? reviewToken = null,
        ChatPluginInstallApproval? chatInstallAuthorization = null)
    {
        try
        {
            if (!ServiceIsLocal)
                throw new InvalidOperationException("Open this request from an interface on the worker computer. Remote paths are not local paths.");
            var selectedOwnerSessionId = explicitOwnerSessionId ?? CurrentConversationId;
            if (selectedOwnerSessionId != request.SessionId) return ChatPluginInstallApplyResult.NotStarted(request.Id);
            if (explicitOwnerSessionId is not null &&
                !InterfaceOwnerIsSelected(explicitOwnerSessionId, CurrentConversationId, workflowWorkspace?.Snapshot?.Run?.Id))
                throw new InvalidOperationException("The selected conversation or Work item changed before plug-in review. Reopen its original owner and retry.");
            if (expectedReviewIdentity is not null) RequireCurrentReview(expectedReviewIdentity, request);
            if (currentOwner is not null && !currentOwner())
                throw new InvalidOperationException("The original owner or selection changed before plugin review.");
            if (chatInstallAuthorization is not null)
            {
                if (!nativePluginApproved || controlTestsAllowed || request.Kind != "plugin" || request.Source != "workflow" ||
                    chatInstallAuthorization.RequestId != request.Id || chatInstallAuthorization.ConversationId != request.SessionId ||
                    chatInstallAuthorization.PackageFingerprint != request.PackageFingerprint)
                    throw new InvalidOperationException("Chat installation authorization cannot grant control tests or another request.");
                if (!await VerifyPendingPluginSourceForClickAsync(request, expectedReviewIdentity)) return ChatPluginInstallApplyResult.NotStarted(request.Id);
            }
            if (request.Kind == "plugin" && !nativePluginApproved && request.Source == "workflow" &&
                !await VerifyPendingPluginSourceForClickAsync(request, expectedReviewIdentity, allowVerifiedWork: true)) return ChatPluginInstallApplyResult.NotStarted(request.Id);
            if (request.Kind == "plugin" && !nativePluginApproved)
            {
                var manifest = desktopPluginCatalog.Inspect(request.Path);
                var accepted = MessageBox.Show(this,
                    $"Install and enable {manifest.Name} {manifest.Version}?\n\nPackage: {manifest.Id}\nSource: {request.Path}\n\n{DesktopPluginCatalog.NativeTrustNotice}",
                    "Review native Cerebral plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!accepted)
                {
                    SetStatus("Plugin review remains pending. No package was installed or enabled.");
                    await RefreshInterfaceRequests();
                    return ChatPluginInstallApplyResult.NotStarted(request.Id);
                }
                if (request.Source == "workflow" &&
                    !await VerifyPendingPluginSourceForClickAsync(request, expectedReviewIdentity, allowVerifiedWork: true)) return ChatPluginInstallApplyResult.NotStarted(request.Id);
                if (expectedReviewIdentity is not null) RequireCurrentReview(expectedReviewIdentity, request);
            }
        }
        catch (Exception error) when (chatInstallAuthorization is not null && error is not OutOfMemoryException and not StackOverflowException)
        {
            // All work above is preflight; no reservation, claim or effect has been admitted.
            return ChatPluginInstallApplyResult.NotStarted(request.Id,
                currentOwner?.Invoke() == false ? ChatPluginInstallNotStartedReason.WaitingForOwnerChat : ChatPluginInstallNotStartedReason.ManualReview);
        }
        var capturedSessionId = request.SessionId;
        var endpoint = ServiceConnectionIdentity;
        Cerebral.Client.CerebralClient ownerClient;
        try { ownerClient = CreateIndependentClient(); }
        catch (Exception error) when (chatInstallAuthorization is not null && error is not OutOfMemoryException and not StackOverflowException)
        { return ChatPluginInstallApplyResult.NotStarted(request.Id); }
        var leaseGate = new object(); var commandFinished = false; var applicationFinished = true;
        void ReleaseClient()
        {
            lock (leaseGate) { if (commandFinished && applicationFinished) ownerClient.Dispose(); }
        }
        try
        {
            var claimId = Guid.NewGuid().ToString("N");
            // Semantic owner review uses this coordinate only to classify its own later receipt.
            // Selection of an ID is not proof that the claim or installation began.
            claimIdSelected?.Invoke(claimId);
            DesktopInterfaceOutcome? retainedOutcome = null;
            if (request.Kind == "plugin")
            {
                var serviceKey = interfaceOutcomeServiceKey ??
                    throw new InvalidOperationException("Reconnect to verify the local Worker identity before reviewing a plugin.");
                if (!interfaceOutcomeJournal.Reserve(request, claimId, serviceKey, controlTestsAllowed))
                    throw new InvalidOperationException("This plugin attempt already has retained delivery state; confirm its existing outcome without executing it again.");
                retainedOutcome = interfaceOutcomeJournal.Read(serviceKey, request.Id)!;
            }
            var claimArguments = chatInstallAuthorization is null ? CerebralJson.Element(new { id = request.Id, claimId }) :
                CerebralJson.Element(new { id = request.Id, claimId, chatInstallAuthorization = new
                {
                    conversationId = request.SessionId, permissionRevision = chatInstallAuthorization.PermissionRevision,
                    decisionId = chatInstallAuthorization.DecisionId
                } });
            var claim = await ownerClient.GetAsync<InterfaceClaimResult>("interface.claim", claimArguments,
                cancellationToken: reviewToken ?? Lifetime.Token);
            if (!claim.Claimed)
            {
                if (retainedOutcome is not null && claim.Request.ClaimId != claimId)
                    interfaceOutcomeJournal.DiscardUnstarted(retainedOutcome);
                return claim.Request.ClaimId != claimId
                    ? ChatPluginInstallApplyResult.NotStarted(request.Id)
                    : ChatPluginInstallApplyResult.Uncertain(request.Id, claimId);
            }
            request = claim.Request;
            if (retainedOutcome is not null)
                DesktopInterfaceOutcomeJournal.RequireMatchingRequest(retainedOutcome, request);
            bool success = false; string? error = null; string? message = null; string? resourceRequestId = null;
            InterfaceFailureStage? failureStage = null;
            InterfacePluginReceipt? plugin = null;
            InterfaceResourceReceipt? resource = null;
            try
            {
                if (expectedReviewIdentity is not null)
                {
                    RequireCurrentReviewOwner(expectedReviewIdentity);
                    if (!expectedReviewIdentity.MatchesClaimedRequest(request))
                        throw new InvalidOperationException(
                            "The reviewed request changed before its claim completed. Reopen the current review before continuing.");
                }
                if (currentOwner is not null && !currentOwner())
                    throw new InvalidOperationException("The original owner or selection changed after plugin claim.");
                if (request.Kind == "application")
                {
                    applicationFinished = false;
                    ApplicationOutputWindow window;
                    try
                    {
                        window = new ApplicationOutputWindow(request.Path, async (pid, started, status, exit, output) =>
                        {
                            try { await ownerClient.GetAsync<InterfaceRequest>("interface.observe", new ApplicationObservation(request.Id, claimId, pid, started, status, exit, output)); }
                            finally
                            {
                                if (status is "exited" or "failed" or "stopped") { lock (leaseGate) applicationFinished = true; ReleaseClient(); }
                            }
                        });
                    }
                    catch { applicationFinished = true; throw; }
                    applicationWindows[request.Id] = window;
                    window.Closed += (_, _) => applicationWindows.Remove(request.Id);
                    window.Show();
                    message = $"Opened application output window and started {request.Path} as process {window.ProcessId}. Actual runtime status and errors are reported separately to this conversation.";
                }
                else if (request.Kind == "plugin")
                {
                    if (chatInstallAuthorization is not null)
                    {
                        await RequireChatPluginInstallEffectAsync(ownerClient, request, claimId, chatInstallAuthorization,
                            reviewToken ?? Lifetime.Token);
                        if (currentOwner is not null && !currentOwner())
                            throw new InvalidOperationException("The native chat owner changed before installation.");
                    }
                    var receipt = await PresentDesktopPluginAsync(request, ownerClient,
                        reviewToken ?? Lifetime.Token, controlTestsAllowed, activateWindow);
                    if (currentOwner is not null && !currentOwner())
                        throw new InvalidOperationException("The original owner or selection changed during plugin presentation.");
                    message = receipt.Message; resourceRequestId = receipt.ResourceRequestId; plugin = receipt.Plugin; resource = receipt.Resource;
                }
                else
                {
                    if (CurrentConversationId != capturedSessionId || ServiceConnectionIdentity != endpoint)
                        throw new InvalidOperationException("Connection or conversation changed before presentation. Reopen the requested file in its original connection.");
                    sharedFiles.IsChecked = false; filePath.Text = request.Path; SelectPage("files");
                    if (request.Kind == "folder") await ListFiles(); else await ReadFile();
                    message = $"Opened {request.Kind} {request.Path} in the Cerebral Files tab.";
                }
                success = true;
            }
            catch (Exception ex)
            {
                var diagnostic = ex.Message.Replace('\0', '\ufffd');
                error = diagnostic[..Math.Min(2000, diagnostic.Length)];
                failureStage = (ex as DesktopPluginPresentationException)?.Stage;
            }
            InterfaceRequest confirmedFinal;
            if (retainedOutcome is not null)
            {
                retainedOutcome = interfaceOutcomeJournal.SaveCompletion(retainedOutcome,
                    new(request.Id, claimId, success, error, message, resourceRequestId, plugin,
                        controlTestsAllowed, failureStage, resource));
                try
                {
                    var confirmed = await ownerClient.GetAsync<InterfaceRequest>("interface.complete",
                        retainedOutcome.Completion!.CommandArguments(), cancellationToken: Lifetime.Token);
                    interfaceOutcomeJournal.Confirm(retainedOutcome, confirmed);
                    confirmedFinal = confirmed;
                }
                catch (Exception deliveryError)
                {
                    SetStatus("The desktop plugin outcome is recorded, but confirmation is pending: " + deliveryError.Message, true);
                    if (CurrentConversationId == capturedSessionId) await RefreshInterfaceRequests();
                    return ChatPluginInstallApplyResult.Uncertain(request.Id, claimId);
                }
            }
            else confirmedFinal = await ownerClient.GetAsync<InterfaceRequest>("interface.complete", new
                { id = request.Id, claimId, success, error, message, resourceRequestId, plugin, controlTestsAllowed, failureStage });
            if (CurrentConversationId == capturedSessionId) { await RefreshInterfaceRequests(); SetStatus(success ? message! : "Could not open: " + error, !success); }
            return ChatPluginInstallApplyResult.Confirmed(request, claimId, confirmedFinal);
        }
        finally { lock (leaseGate) commandFinished = true; ReleaseClient(); }
    }

    private static bool InterfaceOwnerIsSelected(string ownerId, string? conversationId, string? workflowRunId) =>
        !string.IsNullOrWhiteSpace(ownerId) && (ownerId == conversationId || ownerId == workflowRunId);

    private static string InterfaceReviewAction(InterfaceRequest request, bool repairEligible) =>
        request.Kind == "plugin" && request.Status == "pending"
            ? "plugin-install-review"
            : request.Kind == "plugin" && request.Status == "failed" && repairEligible && request.PluginTests is not null
                ? "plugin-repair-status"
                : repairEligible
                    ? "plugin-repair-consent"
                    : request.Kind + ":" + request.Status;

    private void RequireCurrentReviewOwner(InterfaceReviewStateIdentity identity)
    {
        if (!identity.MatchesOwner(PluginBinding(), ServiceConnectionIdentity, knowledgeConnectionVersion,
                CurrentConversationId))
            throw new InvalidOperationException(
                "The conversation, binding, or service connection changed. Reopen the current review before continuing.");
    }

    private void RequireCurrentReview(InterfaceReviewStateIdentity identity, InterfaceRequest request)
    {
        RequireCurrentReviewOwner(identity);
        DesktopPluginManifest? manifest = null;
        DesktopPluginInfo? installed = null;
        Exception? reviewError = null;
        if (request.Kind == "plugin" && request.Status == "pending")
        {
            try
            {
                manifest = desktopPluginCatalog.Inspect(request.Path);
                installed = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == manifest.Id);
            }
            catch (Exception ex) { reviewError = ex; }
        }
        var repairEligible = request.Status == "failed" && IsPluginRepairCandidate(request);
        var current = InterfaceReviewStateIdentity.Create(request, PluginBinding(), ServiceConnectionIdentity,
            knowledgeConnectionVersion, InterfaceReviewAction(request, repairEligible), manifest, installed, reviewError);
        if (current != identity)
            throw new InvalidOperationException(
                "The request, package, or installed plugin changed. Reopen the current review before continuing.");
    }
}
