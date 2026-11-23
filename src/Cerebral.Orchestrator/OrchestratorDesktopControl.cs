using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal async Task<DesktopControlResponse> HandleDesktopControlAsync(DesktopControlRequest request,
        CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        DesktopControlProtocol.Validate(request, request.InstanceId);
        switch (request.Operation)
        {
            case DesktopControlProtocol.CapabilitiesInspect:
                EnsureNoExtraArguments(request.Arguments);
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    schema = "pegasus.main-control-discovery/1",
                    capturedAtUtc = DateTimeOffset.UtcNow,
                    readiness = new
                    {
                        connected = ServiceConnected, windowVisible = IsVisible,
                        windowHandleAvailable = new WindowInteropHelper(this).Handle != IntPtr.Zero,
                        renderableLayout = Content is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 },
                        nativeConversation = IsNativeChat && nativeChat is not null,
                        pendingPreference = nativePreferencePending,
                        unknownRunEffect = nativeChat?.Snapshot?.Run is { IsTerminal: false, EffectState: WorkflowEffectState.Unknown },
                        closing = Lifetime.IsCancellationRequested
                    },
                    semanticSelection = SemanticAttachSelection(),
                    // A full snapshot can contain questions and preview text; expose only this metadata projection.
                    workflowSelection = MainChatWorkflowSelectionSnapshot(),
                    surface = Tabs.SelectedItem is TabItem { Tag: string page } ? page : null,
                    note = "Readiness is an observation, not permission or a promise that a later action will be admitted."
                });
            case "input-guidance.layout.inspect":
                return HandleInputGuidanceLayoutObservation(request, cancellationToken);
            case "workflow.open":
            case "workflow.inspect":
            case "workflow.resume":
            case "workflow.reconcile":
            case "workflow.answer":
                return await HandleDesktopWorkflowControlAsync(request, cancellationToken);
            case "instance.inspect":
                EnsureNoExtraArguments(request.Arguments);
                var instancePaths = DesktopInstancePaths.Current;
                var instancePlugins = desktopPluginCatalog.List();
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    sourceLocal = instancePaths.SourceLocal,
                    semanticFeatures = new[] { MainWorkflowDefinitionInput.Capability, "conversation.workflow.exact-select.v1" },
                    connectionPath = ConnectionStore.FilePath,
                    pluginDirectory = desktopPluginCatalog.RootDirectory,
                    pluginDataDirectory = desktopPluginDataRoot ?? DesktopPluginCatalog.DefaultDataDirectory,
                    productPaths = DesktopProductPathsSnapshot(),
                    plugins = instancePlugins.Take(100).Select(plugin => new
                    {
                        plugin.Id, plugin.Name, plugin.Version, plugin.Enabled,
                        error = plugin.Error is null ? null : plugin.Error[..Math.Min(200, plugin.Error.Length)]
                    }),
                    omittedPlugins = Math.Max(0, instancePlugins.Count - 100)
                });
            case "native-plugins.inspect":
            case "native-plugins.review":
                return await HandleNativePluginManagementControlAsync(request, cancellationToken);
            case "input-guidance.inspect":
            case "input-guidance.invoke":
            case "input-guidance.setText":
                return HandleInputGuidanceControl(request);
            case "snapshot.inspect":
                return DesktopControlProtocol.Result(request, "completed",
                    CreateDesktopControlSnapshot(OptionalBool(request.Arguments, "includeLastResponse"), includeConfigurationReceiver: true));
            case "window.activate":
                DesktopApplicationInstance.Show(this); Activate();
                return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(false));
            case "window.capture":
            {
                EnsureNoExtraArguments(request.Arguments);
                if (Content is not FrameworkElement desktopRoot)
                    throw new InvalidOperationException("The Cerebral desktop has no renderable root.");
                UpdateLayout();
                desktopRoot.UpdateLayout();
                var captureDirectory = Path.Combine(Path.GetTempPath(), "Pegasus-Cerebral", "DesktopControl");
                _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(captureDirectory);
                Directory.CreateDirectory(captureDirectory);
                _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(captureDirectory);
                var captureId = Guid.NewGuid().ToString("N");
                var capturePath = Path.Combine(captureDirectory, "desktop-" + request.InstanceId + "-" + captureId + ".png");
                var captureSelection = SemanticAttachSelection();
                var captureSurface = Tabs.SelectedItem is TabItem { Tag: string capturePage } ? capturePage : null;
                var captureHandle = new WindowInteropHelper(this).Handle;
                using var captureProcess = Process.GetCurrentProcess();
                var logicalWidth = Math.Round(desktopRoot.ActualWidth, 1);
                var logicalHeight = Math.Round(desktopRoot.ActualHeight, 1);
                var capturedAtUtc = DateTimeOffset.UtcNow;
                var capture = DesktopPanelCapture.CapturePng(desktopRoot);
                int pixelWidth, pixelHeight;
                using (var image = new MemoryStream(capture, writable: false))
                {
                    var frame = new System.Windows.Media.Imaging.PngBitmapDecoder(image,
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                    pixelWidth = frame.PixelWidth; pixelHeight = frame.PixelHeight;
                }
                var captureTemporary = capturePath + ".tmp";
                var ownsCaptureTemporary = false;
                try
                {
                    using (var output = new FileStream(captureTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.Asynchronous))
                    {
                        ownsCaptureTemporary = true;
                        await output.WriteAsync(capture, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(captureDirectory);
                    File.Move(captureTemporary, capturePath, overwrite: false);
                    ownsCaptureTemporary = false;
                }
                finally
                {
                    if (ownsCaptureTemporary)
                    {
                        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(captureDirectory);
                        File.Delete(captureTemporary);
                    }
                }
                var captureTargetCurrent = !Lifetime.IsCancellationRequested && ReferenceEquals(Content, desktopRoot) &&
                    captureSelection == SemanticAttachSelection() && new WindowInteropHelper(this).Handle == captureHandle &&
                    (Tabs.SelectedItem is TabItem { Tag: string afterCapturePage } ? afterCapturePage : null) == captureSurface;
                return DesktopControlProtocol.Result(request, captureTargetCurrent ? "completed" : "uncertain", new
                {
                    captureId, requestId = request.RequestId, capturedAtUtc,
                    method = "wpf-render-target-bitmap", nativePixels = false,
                    path = capturePath,
                    width = logicalWidth,
                    height = logicalHeight,
                    logicalUnits = "WPF DIPs", pixelWidth, pixelHeight,
                    bytes = capture.Length, sha256 = Convert.ToHexString(SHA256.HashData(capture)).ToLowerInvariant(),
                    target = new
                    {
                        instanceId = request.InstanceId, processId = Environment.ProcessId,
                        processStartUtcTicks = captureProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                        sessionId = captureProcess.SessionId,
                        windowHandle = captureHandle == IntPtr.Zero ? null : $"0x{captureHandle.ToInt64():X16}",
                        surface = captureSurface, semanticSelection = captureSelection
                    },
                    targetStillCurrent = captureTargetCurrent,
                    comparison = "AutoIt/native pixels and independent screenshot alignment are not captured by this operation."
                }, captureTargetCurrent ? null : "The captured target changed before its file receipt completed. Inspect before comparing or capturing again.");
            }
            case "composer.attach":
                EnsureNoExtraArguments(request.Arguments, "paths");
                if (!request.Arguments.TryGetProperty("paths", out var attachmentPaths) ||
                    attachmentPaths.ValueKind != JsonValueKind.Array || attachmentPaths.GetArrayLength() is < 1 or > 16)
                    throw new ArgumentException("Provide one to sixteen absolute attachment paths.");
                var selectedPaths = attachmentPaths.EnumerateArray().Select(path => JsonString(path, "path", 32768, allowEmpty: false)).ToArray();
                if (selectedPaths.Any(path => !Path.IsPathFullyQualified(path)))
                    throw new ArgumentException("Attachment paths must be absolute local desktop paths.");
                var beforeAttachmentCount = attachmentDrafts.Count;
                string? attachmentImportError = null;
                try { await AddAttachmentFilesAsync(selectedPaths); }
                catch (Exception error) when (attachmentDrafts.Count > beforeAttachmentCount &&
                    error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    // A batch can fail after an earlier import committed. Retain its receipts and
                    // report failure rather than a pre-dispatch rejection that suggests no effect.
                    attachmentImportError = error.Message[..Math.Min(error.Message.Length, 2000)];
                }
                var uncertainAttachment = attachmentDrafts.Any(item => item.Transfer.Snapshot.Uncertain);
                var readyAttachments = attachmentImportError is null && attachmentDrafts.All(item => item.Transfer.Snapshot.Attachment is not null && item.DerivedError is null);
                return DesktopControlProtocol.Result(request, uncertainAttachment ? "uncertain" : readyAttachments ? "completed" : "failed", new
                {
                    conversationId = CurrentConversationId,
                    attachments = attachmentDrafts.Select(item => new
                    {
                        name = item.DisplayName, id = item.Transfer.Snapshot.Attachment?.Id,
                        state = item.Transfer.Snapshot.Status?.State, error = item.DerivedError ?? item.Transfer.Snapshot.Error
                    }).ToArray()
                }, readyAttachments ? null : attachmentImportError ?? "Attachment selection is retained. Reconcile its import before retrying or sending.");
            case "composer.drafts.restore":
                EnsureNoExtraArguments(request.Arguments, "items");
                if (!request.Arguments.TryGetProperty("items", out var draftItems) || draftItems.ValueKind != JsonValueKind.Array ||
                    draftItems.GetArrayLength() is < 1 or > 8)
                    throw new ArgumentException("Restore one to eight retained drafts.");
                var restoredDrafts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var item in draftItems.EnumerateArray())
                {
                    EnsureNoExtraArguments(item, "id", "text");
                    var draftId = RequiredString(item, "id", 128);
                    var draftText = RequiredString(item, "text", DesktopControlProtocol.MaximumArgumentCharacters, allowEmpty: true);
                    if (!restoredDrafts.TryAdd(draftId, draftText))
                        throw new ArgumentException("A conversation appears twice in the draft batch.");
                    var currentDraft = draftId == CurrentConversationId ? prompt.Text : drafts.GetValueOrDefault(draftId, "");
                    if (currentDraft.Length > 0 && currentDraft != draftText)
                        throw new InvalidOperationException("A newer unsent draft exists. No draft was replaced.");
                }
                // Prevalidate the whole batch, then preserve drafts without navigating or submitting.
                foreach (var pair in restoredDrafts)
                {
                    RememberComposerDraft(pair.Key, pair.Value, flush: false);
                    if (pair.Key == CurrentConversationId) prompt.Text = pair.Value;
                }
                var draftsDurable = FlushComposerDrafts();
                return DesktopControlProtocol.Result(request, draftsDurable ? "completed" : "failed",
                    new { restored = restoredDrafts.Count, conversationId = CurrentConversationId, durable = draftsDurable && composerDraftStore?.Persisted is not null },
                    draftsDurable ? null : composerDraftStorageError);
            case "composer.set":
                EnsureNoExtraArguments(request.Arguments, "text");
                var text = RequiredString(request.Arguments, "text", DesktopControlProtocol.MaximumArgumentCharacters, allowEmpty: true);
                SelectPage("chat"); prompt.Text = text; prompt.CaretIndex = prompt.Text.Length; prompt.Focus();
                var composerDurable = FlushComposerDrafts();
                return DesktopControlProtocol.Result(request, composerDurable ? "completed" : "failed",
                    new { characters = prompt.Text.Length, conversationId = CurrentConversationId, durable = composerDurable && composerDraftStore?.Persisted is not null },
                    composerDurable ? null : composerDraftStorageError);
            case "conversation.new":
                await NewSemanticConversationAsync(request.Arguments, cancellationToken);
                return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(false));
            case "conversation.list":
                EnsureNoExtraArguments(request.Arguments, "limit");
                var limit = OptionalInt(request.Arguments, "limit", 1, 40) ?? 20;
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    conversations = durableConversations.Values.OrderByDescending(x => x.UpdatedAt).Take(limit)
                        .Select(x => new { x.Id, x.Title,
                            reference = exactWorkflowConversations.ContainsKey(x.Id) ? null : ChatReference.Create(x.Id),
                            x.ProjectId, x.LatestRunId, status = x.Status.ToString().ToLowerInvariant(), x.UpdatedAt })
                });
            case "conversation.open":
                EnsureNoExtraArguments(request.Arguments, "id");
                var conversationId = RequiredString(request.Arguments, "id", 128);
                await OpenSavedChatReferenceAsync(conversationId, cancellationToken);
                RequireSemanticAttachNavigationCompleted(conversationId); SelectPage("chat");
                return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(true));
            case "surface.open":
                EnsureNoExtraArguments(request.Arguments, "id");
                var surface = RequiredString(request.Arguments, "id", 32);
                SelectPage(surface);
                return DesktopControlProtocol.Result(request, "completed", new { surface });
            case "plugin.surface.inspect":
                EnsureNoExtraArguments(request.Arguments);
                return DesktopControlProtocol.Result(request, "completed", DesktopPluginSurfaceControlSnapshot());
            case "plugin.surface.controls":
            case "plugin.surface.invoke":
            case "plugin.surface.setText":
            {
                EnsureNoExtraArguments(request.Arguments, request.Operation switch
                {
                    "plugin.surface.invoke" => ["id", "name"],
                    "plugin.surface.setText" => ["id", "fieldId", "expectedText", "text"],
                    _ => ["id"]
                });
                var controlSurfaceId = RequiredString(request.Arguments, "id", 128);
                using var controlRender = DesktopBackgroundRenderScope.Begin(this);
                UpdateLayout();
                var controlSurface = SelectedPluginControlView(controlSurfaceId);
                if (request.Operation == "plugin.surface.invoke")
                {
                    var controlName = RequiredString(request.Arguments, "name", 256);
                    var button = FindPluginControlButton(controlSurface, controlName);
                    if (!button.IsVisible || !button.IsEnabled)
                        throw new InvalidOperationException("The selected plugin button is hidden or disabled.");
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                    var provider = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                        as System.Windows.Automation.Provider.IInvokeProvider ??
                        throw new InvalidOperationException("The selected plugin button has no native Invoke pattern.");
                    provider.Invoke();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken);
                    if (desktopSidebarTabs.SelectedItem is not TabItem { Tag: string afterInvokeId } || afterInvokeId != controlSurfaceId ||
                        !ReferenceEquals(controlSurface, desktopPlugins?.Panels.SingleOrDefault(item => item.PluginId + "." + item.Id == controlSurfaceId)?.View))
                        return DesktopControlProtocol.Result(request, "uncertain", new { id = controlSurfaceId },
                            "The plugin view changed after invocation. The click was delivered; inspect current state before retrying.");
                }
                else if (request.Operation == "plugin.surface.setText")
                {
                    var edit = PluginControlTextEdits.Copy("setText",
                        RequiredString(request.Arguments, "fieldId", 32),
                        RequiredString(request.Arguments, "expectedText", PluginControlTextEdits.MaximumCharacters, allowEmpty: true),
                        RequiredString(request.Arguments, "text", PluginControlTextEdits.MaximumCharacters, allowEmpty: true))!;
                    var prepared = PreparePluginControlTextEdit(controlSurface, edit);
                    prepared.Provider.SetValue(edit.Text);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken);
                    if (desktopSidebarTabs.SelectedItem is not TabItem { Tag: string afterSetId } || afterSetId != controlSurfaceId ||
                        !ReferenceEquals(controlSurface, desktopPlugins?.Panels.SingleOrDefault(item => item.PluginId + "." + item.Id == controlSurfaceId)?.View))
                        return DesktopControlProtocol.Result(request, "uncertain", new { id = controlSurfaceId },
                            "The plugin view changed after the text update. Inspect current state before retrying.");
                    var after = PluginControlSnapshot(controlSurfaceId, controlSurface);
                    var actual = after.Fields.SingleOrDefault(field => field.Id == edit.FieldId);
                    if (actual is null || actual.Limited)
                        return DesktopControlProtocol.Result(request, "uncertain", after,
                            "The native text delivery completed, but the exact after-value is unavailable. Inspect current state before any retry.");
                    if (actual.Text != edit.Text)
                        return DesktopControlProtocol.Result(request, "failed", after,
                            "The native text delivery completed, but the field did not retain the exact requested text. No automatic replay was attempted.");
                    return DesktopControlProtocol.Result(request, "completed", new
                    {
                        id = controlSurfaceId, fieldId = edit.FieldId, before = edit.ExpectedText,
                        after = actual.Text, controls = after
                    });
                }
                return DesktopControlProtocol.Result(request, "completed", PluginControlSnapshot(controlSurfaceId, controlSurface));
            }
            case "input-mode.set":
                EnsureNoExtraArguments(request.Arguments, "id", "enabled");
                var inputModeId = RequiredString(request.Arguments, "id", 160);
                var inputModeEnabled = RequiredBool(request.Arguments, "enabled");
                var inputMode = desktopPlugins?.InputModes.SingleOrDefault(item =>
                    string.Equals(item.PluginId + ":" + item.Id, inputModeId, StringComparison.Ordinal)) ??
                    throw new InvalidOperationException("The requested plug-in input mode is not mounted in this desktop generation.");
                if (!inputMode.QuickToggle)
                    throw new InvalidOperationException("Only quick-toggle input modes can be enabled through semantic desktop control.");
                var inputModeStatus = await SetDesktopQuickModeAsync(inputMode, inputModeEnabled, cancellationToken);
                SyncDesktopInputQuickToggles();
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    id = inputModeId,
                    enabled = DesktopQuickModes().Contains(DesktopInputModeKey(inputMode)),
                    status = inputModeStatus,
                    conversationId = CurrentConversationId
                });
            case "plugin.surface.open":
                EnsureNoExtraArguments(request.Arguments, "id");
                var pluginSurfaceId = RequiredString(request.Arguments, "id", 128);
                var pluginSurface = desktopSidebarTabs.Items.OfType<TabItem>()
                    .SingleOrDefault(item => string.Equals(item.Tag as string, pluginSurfaceId, StringComparison.Ordinal)) ??
                    throw new InvalidOperationException("The requested plug-in sidebar is not mounted in this desktop generation.");
                SelectPage("chat");
                activityRequested = true;
                UpdateConversationLayout();
                desktopSidebarTabs.SelectedItem = pluginSurface;
                using (DesktopBackgroundRenderScope.Begin(this))
                {
                    UpdateLayout();
                    return DesktopControlProtocol.Result(request, "completed", DesktopPluginSurfaceControlSnapshot());
                }
            case "activity.detail":
                EnsureNoExtraArguments(request.Arguments, "id", "expanded");
                if (!IsNativeChat || nativeChat is null)
                    throw new InvalidOperationException("Open a durable conversation before reviewing activity.");
                var activityEventId = RequiredString(request.Arguments, "id", 128);
                var activityExpanded = RequiredBool(request.Arguments, "expanded");
                nativeChat.SetActivityDetailExpanded(activityEventId, activityExpanded);
                return DesktopControlProtocol.Result(request, "completed", new { id = activityEventId, expanded = activityExpanded });
            case "activity.inspect":
                EnsureNoExtraArguments(request.Arguments, "limit");
                var activityLimit = OptionalInt(request.Arguments, "limit", 1, 50) ?? 20;
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    items = DesktopActivityItems().TakeLast(activityLimit),
                    viewports = nativeChat?.ActivityViewportSnapshots.TakeLast(10).ToArray() ?? [],
                    transcriptViewport = nativeChat?.TranscriptViewportSnapshot
                });
            case "interface.inspect":
                EnsureNoExtraArguments(request.Arguments, "limit", "sessionId");
                var interfaceConversationId = ResolveInterfaceSession(request.Arguments);
                var interfaceLimit = OptionalInt(request.Arguments, "limit", 1, 20) ?? 10;
                var interfaceItems = await Get<List<InterfaceRequest>>("interface.list",
                    new { sessionId = interfaceConversationId, limit = interfaceLimit });
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    conversationId = interfaceConversationId,
                    requests = interfaceItems.Select(InterfaceControlSnapshot).ToArray()
                });
            case "interface.correct":
            {
                EnsureNoExtraArguments(request.Arguments, "id", "sessionId", "packageFingerprint");
                if (!ServiceIsLocal)
                    throw new InvalidOperationException("Plugin request correction requires the owning local Worker.");
                var correctionSessionId = ResolveInterfaceSession(request.Arguments);
                var correctionRequestId = RequiredString(request.Arguments, "id", 128);
                var expectedFingerprint = RequiredString(request.Arguments, "packageFingerprint", 64);
                var correctionBinding = PluginBinding();
                var correctionConnection = ServiceConnectionIdentity;
                var correctionConnectionVersion = knowledgeConnectionVersion;
                var correctionItems = await Get<List<InterfaceRequest>>("interface.list",
                    new { sessionId = correctionSessionId, limit = 20 });
                if (CurrentConversationId != correctionSessionId || PluginBinding() != correctionBinding ||
                    ServiceConnectionIdentity != correctionConnection || knowledgeConnectionVersion != correctionConnectionVersion)
                    throw new InvalidOperationException("The selected conversation or connection changed before correction.");
                var correctionRequest = correctionItems.SingleOrDefault(item => item.Id == correctionRequestId) ??
                    throw new InvalidOperationException("The selected conversation has no matching interface request.");
                if (correctionRequest.Kind != "plugin" || correctionRequest.Status != "pending" ||
                    !string.Equals(correctionRequest.PackageFingerprint, expectedFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("The pending plugin package differs from the inspected review.");
                var correctionManifest = desktopPluginCatalog.Inspect(correctionRequest.Path);
                var correctionCapture = CapturePluginChangeCorrection(correctionRequest, correctionManifest,
                    correctionBinding, correctionConnection, correctionConnectionVersion);
                // Use the same guarded composer operation as the visible Correct request control.
                // This owner-only route cannot approve, install, or submit the resulting draft.
                var correctionResult = await CorrectPluginChangeRequestAsync(correctionRequest, correctionCapture);
                return DesktopControlProtocol.Result(request,
                    correctionResult.State == DesktopOperationState.Completed ? "completed" : "rejected",
                    new { requestId = correctionRequest.Id, conversationId = correctionSessionId,
                        characters = prompt.Text.Length, status = correctionRequest.Status }, correctionResult.Message);
            }
            case "interface.review":
                EnsureNoExtraArguments(request.Arguments, "id", "approved", "sessionId", "controlTestsAllowed");
                var reviewConversationId = ResolveInterfaceSession(request.Arguments);
                var interfaceRequestId = RequiredString(request.Arguments, "id", 128);
                var approved = RequiredBool(request.Arguments, "approved");
                var reviewItems = await Get<List<InterfaceRequest>>("interface.list",
                    new { sessionId = reviewConversationId, limit = 20 });
                var reviewed = reviewItems.SingleOrDefault(x => x.Id == interfaceRequestId) ??
                    throw new InvalidOperationException("The selected conversation has no matching interface request.");
                if (reviewed.Kind != "plugin" || reviewed.Status is not ("pending" or "failed"))
                    throw new InvalidOperationException("Only a pending or failed plugin package can be reviewed.");
                if (!approved)
                    return DesktopControlProtocol.Result(request, "rejected", InterfaceControlSnapshot(reviewed),
                        "Plugin review was declined. No package was installed or enabled.");
                if (reviewed.Status == "failed")
                    reviewed = await Get<InterfaceRequest>("interface.retry", new { id = reviewed.Id });
                await ApplyInterfaceRequest(reviewed, nativePluginApproved: true, explicitOwnerSessionId: reviewConversationId,
                    controlTestsAllowed: request.Arguments.TryGetProperty("controlTestsAllowed", out _) && RequiredBool(request.Arguments, "controlTestsAllowed"),
                    activateWindow: false);
                var completedReview = (await Get<List<InterfaceRequest>>("interface.list",
                    new { sessionId = reviewConversationId, limit = 20 })).Single(x => x.Id == reviewed.Id);
                return DesktopControlProtocol.Result(request,
                    completedReview.Status == "completed" ? "completed" : "failed",
                    InterfaceControlSnapshot(completedReview), completedReview.Error);
            case "interface.repair":
            {
                EnsureNoExtraArguments(request.Arguments, "id", "sessionId", "controlTestsAllowed");
                if (!ServiceIsLocal)
                    throw new InvalidOperationException("Plugin review repair runs only through the owning local Worker.");
                var repairConversationId = ResolveInterfaceSession(request.Arguments);
                var repairRequestId = RequiredString(request.Arguments, "id", 128);
                if (!RequiredBool(request.Arguments, "controlTestsAllowed"))
                    return DesktopControlProtocol.Result(request, "rejected", null,
                        "Plugin repair requires explicit owner test/repair consent.");
                var repairEndpoint = ServiceConnectionIdentity;
                using var repairClient = CreateIndependentClient();
                var repairItems = await repairClient.GetAsync<List<InterfaceRequest>>("interface.list",
                    new { sessionId = repairConversationId, limit = 20 });
                if (!ServiceIsLocal || ServiceConnectionIdentity != repairEndpoint ||
                    !InterfaceOwnerIsSelected(repairConversationId, CurrentConversationId,
                        workflowWorkspace?.Snapshot?.Run?.Id))
                    throw new InvalidOperationException(
                        "The connection, selected conversation, or Work item changed before plugin repair. Reopen its original owner and retry.");
                var failedReview = repairItems.SingleOrDefault(item => item.Id == repairRequestId) ??
                    throw new InvalidOperationException("The selected conversation has no matching interface request.");
                if (failedReview.Kind != "plugin" || failedReview.Status != "failed")
                    throw new InvalidOperationException("Only an exact failed plugin review can schedule source repair.");
                var scheduledRepair = await repairClient.GetAsync<InterfaceRequest>("interface.repair", new
                {
                    id = failedReview.Id,
                    sessionId = repairConversationId,
                    controlTestsAllowed = true
                });
                return DesktopControlProtocol.Result(request, "accepted", InterfaceControlSnapshot(scheduledRepair));
            }
            case "service-package.inspect":
                EnsureNoExtraArguments(request.Arguments, "id");
                return await DesktopServiceCommandAsync(request, "service-packages.inspect",
                    new { id = RequiredString(request.Arguments, "id", 64) }, cancellationToken);
            case "service-package.stage":
                EnsureNoExtraArguments(request.Arguments, "manifestPath", "expectedRevision");
                return await DesktopServiceCommandAsync(request, "service-packages.stage", new
                {
                    manifestPath = RequiredString(request.Arguments, "manifestPath", 32768),
                    expectedRevision = RequiredLong(request.Arguments, "expectedRevision", 0)
                }, cancellationToken);
            case "service-package.activate":
                EnsureNoExtraArguments(request.Arguments, "id", "expectedRevision");
                return await DesktopServiceCommandAsync(request, "service-packages.activate", new
                {
                    id = RequiredString(request.Arguments, "id", 64),
                    expectedRevision = RequiredLong(request.Arguments, "expectedRevision", 0)
                }, cancellationToken);
            case "plugin.command":
                EnsureNoExtraArguments(request.Arguments, "pluginId", "operation", "arguments");
                var commandPluginId = RequiredString(request.Arguments, "pluginId", 64);
                var commandOperation = RequiredString(request.Arguments, "operation", 128);
                if (!commandOperation.StartsWith(commandPluginId + ".", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("A plugin command must use the selected plugin's namespaced operation.");
                if (!request.Arguments.TryGetProperty("arguments", out var commandArguments) || commandArguments.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Argument 'arguments' must be a JSON object.");
                return await DesktopServiceCommandAsync(request, commandOperation, commandArguments, cancellationToken);
            case "plugin.action":
                EnsureNoExtraArguments(request.Arguments, "id");
                var pluginActionId = RequiredString(request.Arguments, "id", 160);
                var pluginAction = desktopPlugins?.ComposerActions.SingleOrDefault(item =>
                    string.Equals(item.PluginId + ":" + item.Id, pluginActionId, StringComparison.Ordinal)) ??
                    throw new InvalidOperationException("The requested plug-in action is not mounted in this desktop generation.");
                await pluginAction.ExecuteAsync(cancellationToken);
                return DesktopControlProtocol.Result(request, "completed", new
                {
                    id = pluginActionId,
                    conversationId = CurrentConversationId,
                    snapshot = DesktopControlSnapshot(false)
                });
            case "message.send":
            {
                EnsureNoExtraArguments(request.Arguments, "text", "definitionInput");
                var exactWorkflow = IsNativeChat && nativeChat?.Snapshot is { } selected &&
                    SelectedMainWorkflowIdentity(selected).Id != ConversationControlClient.ChatDefinitionId;
                JsonElement? definitionInput = request.Arguments.TryGetProperty("definitionInput", out var suppliedInput)
                    ? exactWorkflow ? MainWorkflowDefinitionInput.FreezeExact(suppliedInput) :
                        MainWorkflowDefinitionInput.Freeze(suppliedInput) : null;
                if (exactWorkflow && retainedNativeSubmission is null && definitionInput is null)
                    throw new ArgumentException("The selected exact workflow requires complete definitionInput before sending.");
                if (mainDefinitionInput.Value is not null)
                    throw new InvalidOperationException("A definition-input request cannot nest another message send.");
                var previousInput = mainDefinitionInput.Value;
                var inputLease = definitionInput is { } extra ? CaptureMainDefinitionInput(request.RequestId, extra, exactWorkflow) : null;
                try
                {
                    mainDefinitionInput.Value = inputLease;
                    EnsureSemanticAttachSubmissionAllowed();
                    if (request.Arguments.TryGetProperty("text", out var message))
                        prompt.Text = JsonString(message, "text", DesktopControlProtocol.MaximumArgumentCharacters, allowEmpty: true);
                    var sent = DefinitionInputComposerOutcome(await SubmitComposerAsync(request.RequestId));
                    SetStatus(sent.Message ?? sent.State.ToString(), sent.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain);
                    return DesktopControlProtocol.Result(request, ControlState(sent.State), new
                    {
                        sent.Message,
                        runId = sent.JobId,
                        submission = semanticSubmission?.RequestId == request.RequestId ? semanticSubmission : null,
                        snapshot = DesktopControlSnapshot(false)
                    }, sent.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain ? sent.Message : null);
                }
                finally
                {
                    inputLease?.Dispose();
                    mainDefinitionInput.Value = previousInput;
                }
            }
            case "question.answer":
                EnsureNoExtraArguments(request.Arguments, "answers");
                if (!IsNativeChat || nativeChat is null) throw new InvalidOperationException("Open a durable conversation question before answering it.");
                var answers = ReadAnswers(request.Arguments);
                _ = await nativeChat.AnswerConversationAsync(request.RequestId, answers, cancellationToken);
                SetStatus("Cerebral accepted the answer set.");
                return DesktopControlProtocol.Result(request, "accepted", DesktopControlSnapshot(false));
            case "task.stop":
                EnsureNoExtraArguments(request.Arguments);
                var stopped = await StopDesktopConversation(PluginBinding(), cancellationToken);
                SetStatus(stopped.Message ?? stopped.State.ToString(), stopped.State is not DesktopOperationState.Accepted and not DesktopOperationState.Completed);
                return DesktopControlProtocol.Result(request, ControlState(stopped.State), new
                {
                    stopped.Message,
                    runId = stopped.JobId,
                    snapshot = DesktopControlSnapshot(false)
                }, stopped.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain ? stopped.Message : null);
            case "wait.settled":
                EnsureNoExtraArguments(request.Arguments, "timeoutSeconds");
                var seconds = OptionalInt(request.Arguments, "timeoutSeconds", 1, 120) ?? 60;
                return await WaitForDesktopStateAsync(request, TimeSpan.FromSeconds(seconds), cancellationToken);
            default:
                throw new ArgumentException("Unknown desktop-control operation.", nameof(request));
        }
    }

    internal void ReportNormalDesktopControlUnavailable()
    {
        Dispatcher.VerifyAccess();
        if (!Lifetime.IsCancellationRequested)
            SetStatus("Programmatic Main control stopped. Restart Cerebral to reopen its control endpoint.", true);
    }

    private async Task<DesktopControlResponse> WaitForDesktopStateAsync(DesktopControlRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = DesktopWorkState();
            if (state is not ("submitting" or "working" or "stopping"))
                return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(true));
            await Task.Delay(200, cancellationToken);
        }
        return DesktopControlProtocol.Result(request, "uncertain", DesktopControlSnapshot(true),
            "The desktop did not reach a settled, waiting-for-input or terminal state before the requested timeout.");
    }

    private object DesktopNavigationControlSnapshot()
    {
        // Owner-only diagnostics observe the materialized sidebar without changing selection,
        // expansion, focus, paging, drafts, or model context. A bound never hides truncation.
        var rows = new List<NavigationNode>();
        var truncated = false;
        void Visit(System.Collections.IEnumerable items)
        {
            foreach (var item in items.OfType<TreeViewItem>())
            {
                if (item.Tag is NavigationNode node)
                {
                    if (rows.Count == 512) { truncated = true; return; }
                    rows.Add(node);
                }
                Visit(item.Items);
                if (truncated) return;
            }
        }
        Visit(navigationTree.Items);
        var all = EffectiveNavigation();
        var distinct = rows.DistinctBy(node => node.Id).ToArray();
        var general = distinct.Where(node => IsRecentGeneralChat(node, all)).ToArray();
        var newest = OrderNavigation(all.Where(node => IsRecentGeneralChat(node, all))).Take(recentChatLimit)
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return new
        {
            recentChatLimit,
            selectedRow = SelectedNavigationRowSnapshot(),
            projectForNewConversation = ProjectForNewConversation(),
            history = new
            {
                count = conversationHistory.Count,
                index = conversationHistory.CursorIndex,
                currentConversationId = conversationHistory.Current?.ConversationId,
                canBack = conversationHistory.CanBack,
                canForward = conversationHistory.CanForward
            },
            olderMode = readingOlderChats || showingAllLoadedChats || showingLegacyNavigation,
            searchActive = !string.IsNullOrWhiteSpace(navigationFilter.Text),
            loadedConversations = navigationSessions.Count,
            renderedGeneralChats = general.Length,
            renderedRecentGeneralChats = general.Count(node => newest.Contains(node.Id)),
            selectedOlderGeneralChats = general.Count(node => !newest.Contains(node.Id) &&
                (node.Id == selectedNavigationId || node.TargetId == CurrentConversationId)),
            renderedProtectedChats = distinct.Count(node => node.Kind == "conversation" && !IsRecentGeneralChat(node, all)),
            renderedGeneralIds = general.Take(200).Select(node => node.TargetId).ToArray(),
            generalIdsTruncated = general.Length > 200,
            truncated
        };
    }

    // Preserve the existing one-argument private helper for callers/reflection; it never samples Configuration.
    private object DesktopControlSnapshot(bool includeLastResponse) =>
        CreateDesktopControlSnapshot(includeLastResponse, includeConfigurationReceiver: false);

    private object CreateDesktopControlSnapshot(bool includeLastResponse, bool includeConfigurationReceiver)
    {
        var selectedPage = Tabs.SelectedItem is TabItem { Tag: string id } ? id : null;
        var nativeSnapshot = IsNativeChat ? nativeChat?.Snapshot : null;
        var run = nativeSnapshot?.Run;
        var displayedTranscript = IsNativeChat ? nativeChat?.DisplayedTranscript : null;
        var displayedActivity = IsNativeChat ? nativeChat?.DisplayedActivity : null;
        var activityViewports = IsNativeChat ? nativeChat?.ActivityViewportSnapshots : null;
        var windowHandle = new WindowInteropHelper(this).Handle;
        return new
        {
            processId = Environment.ProcessId,
            windowHandle = windowHandle == IntPtr.Zero ? null : $"0x{windowHandle.ToInt64():X16}",
            window = IsVisible ? IsActive ? "active" : "visible" : "hidden",
            connected = ServiceConnected,
            connectionStatus = ServiceStatusText,
            connectionTransport = ServiceConnection.Transport,
            connectionFile = ConnectionStore.FilePath,
            configuredDataDirectory = ServiceConnection.Transport == "pipe" ? ServiceConnection.LocalDataDirectory : null,
            configurationReceiver = includeConfigurationReceiver ? ConfigurationReceiverSnapshot() : null,
            surface = selectedPage,
            conversationId = CurrentConversationId,
            title = session?.Title ?? durableConversationTitle,
            semanticSelection = SemanticAttachSelection(),
            workflowSelection = MainChatWorkflowSelectionSnapshot(),
            profileSelection = FreshChatProfileSelectionSnapshot(),
            pathAuthority = FreshChatPathAuthoritySnapshot(),
            coordinates = SemanticAttachCoordinates(),
            navigation = DesktopNavigationControlSnapshot(),
            work = DesktopWorkState(),
            runId = run?.Id ?? CurrentJob()?.Id,
            questionCount = run?.Questions?.Fields.Count ?? loadedQuestionCount,
            questions = run?.Questions?.Fields.Select(x => new { x.Id, x.Label, x.Required, x.Choices, x.Multiple, x.Description,
                answered = run.Questions.Answers.ContainsKey(x.Id) }).ToArray() ?? [],
            composerCharacters = prompt.Text.Length,
            composerDrafts = ComposerDraftControlSnapshot(),
            composerVisible = conversationComposer?.Visibility == Visibility.Visible,
            activityItems = IsNativeChat ? nativeChat?.Snapshot?.Activity.Count() ?? 0 : session?.Messages.Count(x => x.ToolName is not null || x.Origin is "tool" or "helper" or "service") ?? 0,
            transcriptItems = IsNativeChat ? nativeChat?.Snapshot?.Events.Count(x => x.Projection.Human == HumanVisibility.Transcript) ?? 0 : session?.Messages.Count(x => x.Origin is "user" or "assistant") ?? 0,
            nativeChatVisible = IsNativeChat && nativeChat?.IsVisible == true,
            displayedTranscriptItems = displayedTranscript?.Count ?? 0,
            displayedTranscriptEventIds = displayedTranscript?.EventIds ?? Array.Empty<string>(),
            displayedTranscriptEventsTruncated = displayedTranscript?.Truncated ?? false,
            // Preserve the compatibility counter: the native snapshot window rolls independently of the rendered activity cache.
            toolCalls = IsNativeChat ? nativeSnapshot?.Events.Count(x => x.Type == CerebralEventTypes.ToolCall) ?? 0 : session?.Messages.Count(x => x.ToolName is not null) ?? 0,
            toolCallsScope = IsNativeChat ? "nativeSnapshotWindow" : "legacyConversation",
            snapshotSequence = nativeSnapshot?.SnapshotSequence,
            snapshotEventItems = nativeSnapshot?.Events.Count ?? 0,
            snapshotFirstEventSequence = nativeSnapshot?.Events.FirstOrDefault()?.Sequence,
            snapshotLastEventSequence = nativeSnapshot?.Events.LastOrDefault()?.Sequence,
            displayedActivityItems = displayedActivity?.Count ?? 0,
            displayedActivityFirstEventSequence = displayedActivity?.FirstSequence,
            displayedActivityLastEventSequence = displayedActivity?.LastSequence,
            historyMode = IsNativeChat && nativeChat?.IsReadingHistory == true,
            generationPreview = IsNativeChat ? nativeChat?.GenerationPreviewSnapshot : null,
            activityViewports = activityViewports?.TakeLast(WorkflowWorkspace.MaximumActivityDiagnosticViewports).ToArray() ?? Array.Empty<WorkflowActivityViewportSnapshot>(),
            activityViewportsTruncated = (activityViewports?.Count ?? 0) > WorkflowWorkspace.MaximumActivityDiagnosticViewports,
            lastResponse = includeLastResponse ? LastDesktopResponse() : null
        };
    }

    private object DesktopPluginSurfaceControlSnapshot()
    {
        var selected = desktopSidebarTabs.SelectedItem as TabItem;
        var windowHandle = new WindowInteropHelper(this).Handle;
        return new
        {
            processId = Environment.ProcessId,
            windowHandle = windowHandle == IntPtr.Zero ? null : $"0x{windowHandle.ToInt64():X16}",
            surface = Tabs.SelectedItem is TabItem { Tag: string id } ? id : null,
            selectedId = selected?.Tag as string,
            sidebarVisible = desktopSidebarTabs.IsVisible,
            sidebarWidth = Math.Round(desktopSidebarTabs.ActualWidth, 1),
            chatLogVisible = transcript.IsVisible || nativeChat?.IsVisible == true,
            chatLogHeight = Math.Round(nativeChat?.IsVisible == true ? nativeChat.ActualHeight : transcript.ActualHeight, 1),
            transcriptVisible = transcript.IsVisible,
            transcriptHeight = Math.Round(transcript.ActualHeight, 1),
            composerVisible = conversationComposer?.IsVisible == true,
            plugins = desktopPlugins?.Status.Select(item => new
            {
                item.Id,
                item.Loaded,
                item.Error
            }).ToArray() ?? [],
            statusBar = desktopPlugins?.StatusItems.Select(item => new
            {
                id = item.PluginId + ":" + item.Id,
                item.PluginId,
                item.Title,
                visible = item.View.IsVisible
            }).ToArray() ?? [],
            inputModes = desktopPlugins?.InputModes.Select(item => new
            {
                id = item.PluginId + ":" + item.Id,
                item.PluginId,
                item.Label,
                item.QuickToggle,
                enabled = item.QuickToggle
                    ? DesktopQuickModes().Contains(DesktopInputModeKey(item))
                    : desktopInputModeSelection.SelectedItem is ComboBoxItem { Tag: DesktopInputModeChoice choice } &&
                      string.Equals(choice.Key, DesktopInputModeKey(item), StringComparison.Ordinal)
            }).ToArray() ?? [],
            sidebars = desktopSidebarTabs.Items.OfType<TabItem>().Select(item => new
            {
                id = item.Tag as string,
                title = item.Header?.ToString(),
                selected = ReferenceEquals(item, selected),
                visible = item.IsVisible
            }).ToArray()
        };
    }

    private string DesktopWorkState()
    {
        if (NativeMutationPending) return "submitting";
        if (stopRequested) return "stopping";
        if (IsNativeChat && nativeChat?.Snapshot?.Run is { } run)
        {
            if (run.Questions is not null) return "waiting-input";
            if (!run.IsTerminal)
                return run.Status == Cerebral.Contracts.Workflows.WorkflowStatus.Waiting ||
                       run.EffectState == Cerebral.Contracts.Workflows.WorkflowEffectState.Unknown
                    ? "waiting" : run.CancelRequested ? "stopping" : "working";
            return run.Status.ToString().ToLowerInvariant();
        }
        if (CurrentJob() is { } job) return job.Status;
        return ServiceConnected ? "idle" : "offline";
    }

    private string? LastDesktopResponse()
    {
        if (IsNativeChat && nativeChat?.Snapshot is { } snapshot)
        {
            var item = snapshot.Transcript.LastOrDefault(x => x.Actor.Kind == EventActorKind.Agent);
            if (item is null) return null;
            var content = string.Join("\n", item.Content.Select(part => part switch
            {
                TextContentPart text => text.Text,
                StructuredContentPart { Schema: "workflow.result", Value.ValueKind: JsonValueKind.Object } structured
                    when structured.Value.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String => summary.GetString(),
                _ => null
            }).Where(x => !string.IsNullOrEmpty(x)));
            return Bounded(content, 1024);
        }
        var legacy = session?.Messages.LastOrDefault(x => x.Role == "assistant" && x.ToolName is null)?.Content;
        return legacy is null ? null : Bounded(legacy, 1024);
    }

    private IEnumerable<object> DesktopActivityItems()
    {
        if (IsNativeChat && nativeChat?.Snapshot is { } snapshot)
        {
            return snapshot.Activity.Select(item => new
            {
                item.Sequence,
                item.Type,
                actor = item.Actor.DisplayName ?? item.Actor.Id ?? item.Actor.Kind.ToString(),
                tool = ToolName(item),
                state = ToolState(item)
            }).Cast<object>().ToArray();
        }
        return session?.Messages.Where(x => x.ToolName is not null || x.Origin is "tool" or "helper" or "service")
            .Select(x => new { sequence = x.Sequence, type = x.Origin, actor = x.Role, tool = x.ToolName, state = (string?)null })
            .Cast<object>().ToArray() ?? [];
    }

    private static string? ToolName(CerebralEvent item)
    {
        if (item.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("toolName", out var tool) && tool.ValueKind == JsonValueKind.String)
            return tool.GetString();
        return item.Content.OfType<StructuredContentPart>().Select(x => x.Value)
            .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("toolName", out var name) && name.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("toolName").GetString()).FirstOrDefault();
    }

    private static string? ToolState(CerebralEvent item) => item.Content.OfType<StructuredContentPart>()
        .Select(x => x.Value).Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
        .Select(x => x.GetProperty("state").GetString()).FirstOrDefault();

    private static string Bounded(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static string ControlState(DesktopOperationState state) => state switch
    {
        DesktopOperationState.Completed => "completed",
        DesktopOperationState.Accepted => "accepted",
        DesktopOperationState.Rejected => "rejected",
        DesktopOperationState.Uncertain => "uncertain",
        DesktopOperationState.Failed => "failed",
        _ => "failed"
    };

    private static void EnsureNoExtraArguments(JsonElement arguments, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!names.Contains(property.Name)) throw new ArgumentException($"Unknown argument '{property.Name}'.");
    }

    private static string RequiredString(JsonElement arguments, string name, int maximum, bool allowEmpty = false)
    {
        if (!arguments.TryGetProperty(name, out var value)) throw new ArgumentException($"Argument '{name}' is required.");
        return JsonString(value, name, maximum, allowEmpty);
    }

    private static string JsonString(JsonElement value, string name, int maximum, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"Argument '{name}' must be a string.");
        var text = value.GetString()!;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > maximum)
            throw new ArgumentException($"Argument '{name}' must contain {(allowEmpty ? "at most" : "1–")} {maximum:N0} characters.");
        return text;
    }

    private static bool OptionalBool(JsonElement arguments, string name)
    {
        EnsureNoExtraArguments(arguments, name);
        if (!arguments.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"Argument '{name}' must be boolean.");
        return value.GetBoolean();
    }

    private static bool RequiredBool(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Argument '{name}' must be boolean.");
        return value.GetBoolean();
    }

    private object InterfaceControlSnapshot(InterfaceRequest request)
    {
        object? package = null;
        if (request.Kind == "plugin")
        {
            try
            {
                var manifest = desktopPluginCatalog.Inspect(request.Path);
                package = new { manifest.Id, manifest.Name, manifest.Version, manifest.Description };
            }
            catch (Exception ex) { package = new { error = ex.Message }; }
        }
        return new { request.Id, request.Kind, request.Path, request.Status, request.Error,
            request.Resource, request.Plugin, request.PluginTests, request.PackageFingerprint, request.GeneratedSourceRoot, package };
    }

    private string ResolveInterfaceSession(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("sessionId", out var value))
            return CurrentConversationId ?? throw new InvalidOperationException("Open a conversation before inspecting or reviewing its interface requests.");
        var sessionId = JsonString(value, "sessionId", 128, allowEmpty: false);
        if (!InterfaceOwnerIsSelected(sessionId, CurrentConversationId, workflowWorkspace?.Snapshot?.Run?.Id))
            throw new InvalidOperationException("Open the matching conversation or Work item before inspecting or reviewing its interface requests.");
        return sessionId;
    }

    private static int? OptionalInt(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new ArgumentException($"Argument '{name}' must be an integer from {minimum} through {maximum}.");
        return number;
    }

    private static long RequiredLong(JsonElement arguments, string name, long minimum)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number) || number < minimum)
            throw new ArgumentException($"Argument '{name}' must be an integer of at least {minimum}.");
        return number;
    }

    private async Task<DesktopControlResponse> DesktopServiceCommandAsync(DesktopControlRequest request,
        string operation, object arguments, CancellationToken cancellationToken)
    {
        if (!ServiceIsLocal) throw new InvalidOperationException("Service package review and plugin commands run only on the owning local Worker.");
        var result = await Client.CommandAsync(operation, arguments, cancellationToken: cancellationToken);
        if (!result.Success) return DesktopControlProtocol.Result(request, "failed", result.Data, result.Error);
        return DesktopControlProtocol.Result(request, "completed", result.Data);
    }

    private static IReadOnlyDictionary<string, string> ReadAnswers(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("answers", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Argument 'answers' must be a JSON object.");
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Length is < 1 or > 128 || property.Name.Any(char.IsWhiteSpace) || !answers.TryAdd(property.Name,
                JsonString(property.Value, "answers." + property.Name, 8192, allowEmpty: true)))
                throw new ArgumentException("Question answer IDs must be unique bounded identifiers without whitespace.");
        }
        if (answers.Count > 32) throw new ArgumentException("Supply at most 32 question answers.");
        return answers;
    }
}
