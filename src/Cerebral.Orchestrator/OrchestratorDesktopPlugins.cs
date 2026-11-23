using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record DesktopPluginPresentationReceipt(string Message, string? ResourceRequestId, InterfacePluginReceipt Plugin,
        InterfaceResourceReceipt? Resource = null);
    // Preserve the failed phase separately from prose. Infrastructure failures must not ask the
    // model to rewrite a plugin that already loaded successfully.
    private sealed class DesktopPluginPresentationException(InterfaceFailureStage stage, Exception cause)
        : InvalidOperationException($"Plugin review failed during {stage}: {cause.Message}", cause)
    {
        internal InterfaceFailureStage Stage { get; } = stage;
    }
    private DesktopPluginRuntime? desktopPlugins;
    // Includes older runtime generations: a timed-out Exit only re-observes their original cleanup.
    private Task desktopPluginRetirement = Task.CompletedTask;
    internal IReadOnlyList<DesktopHostedAction> DesktopTrayActions =>
        desktopPlugins?.ActionsFor(DesktopMountSlots.Menus) ?? [];
    private readonly DesktopPluginCatalog desktopPluginCatalog;
    private readonly string? desktopPluginDataRoot;
    private readonly DispatcherTimer pluginRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string desktopPluginCatalogState = "";
    private readonly List<DesktopTextEntryMount> desktopTextEntryMounts = [];
    private readonly TextBlock desktopInputModeLabel = Ui.Text("Input mode", 12, Ui.Muted);
    private readonly ComboBox desktopInputModeSelection = new() { MinWidth = 135, MaxWidth = 220, Margin = new Thickness(5, 0, 8, 0) };
    private readonly WrapPanel desktopInputQuickToggles = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock desktopInputQuickStatus = Ui.Text("", 11, Ui.Muted);
    private readonly WrapPanel desktopInputModeBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Border desktopInputGuidanceHost = new() { Visibility = Visibility.Collapsed, BorderBrush = Ui.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
    private readonly Dictionary<string, string> desktopInputModeByConversation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> desktopInputQuickModesByConversation = new(StringComparer.Ordinal);
    private bool mountingDesktopInputModes;
    private bool desktopPluginsStarted;
    private Exception? desktopPluginStartupMountError;
    private static bool IsAttachedDraftFixtureProcess() =>
        Environment.GetCommandLineArgs().Contains("--attached-draft-fixture", StringComparer.Ordinal);

    private FrameworkElement DesktopInputModeView()
    {
        if (desktopInputModeBar.Children.Count > 0) return desktopInputModeBar;
        desktopInputModeBar.Children.Add(desktopInputModeLabel);
        desktopInputModeBar.Children.Add(desktopInputModeSelection);
        desktopInputModeBar.Children.Add(desktopInputQuickToggles);
        desktopInputModeBar.Children.Add(desktopInputQuickStatus);
        AutomationProperties.SetAutomationId(desktopInputModeSelection, "DesktopInputMode");
        AutomationProperties.SetName(desktopInputModeSelection, "Conversation input mode");
        AutomationProperties.SetAutomationId(desktopInputGuidanceHost, "DesktopInputGuidance");
        desktopInputModeSelection.SelectionChanged += (_, _) =>
        {
            if (mountingDesktopInputModes || desktopInputModeSelection.SelectedItem is not ComboBoxItem { Tag: DesktopInputModeChoice choice }) return;
            desktopInputModeByConversation[DesktopInputConversationKey()] = choice.Key;
            SyncDesktopInputQuickToggles();
            ClearDesktopInputGuidance();
            SetStatus(choice.Mode is null ? "Direct input mode selected." : choice.Label + " input guidance selected.");
        };
        return desktopInputModeBar;
    }
    private UIElement DesktopSidebarView()
    {
        desktopSidebarTabs.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        desktopSidebarTabs.VerticalContentAlignment = VerticalAlignment.Stretch;
        desktopSidebarTabs.BorderThickness = new Thickness(0);
        desktopSidebarTabs.Background = Ui.Surface;
        desktopSidebarTabs.Foreground = Ui.Ink;
        var tabStyle = Ui.ExtendStyle<TabItem>();
        tabStyle.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Surface));
        tabStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Ink));
        tabStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Line));
        tabStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1, 1, 1, 0)));
        tabStyle.Triggers.Add(new Trigger { Property = TabItem.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Ui.Accent), new Setter(Control.ForegroundProperty, Ui.OnAccent),
                new Setter(Control.FontWeightProperty, FontWeights.SemiBold) } });
        tabStyle.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BorderBrushProperty, Ui.Accent) } });
        desktopSidebarTabs.ItemContainerStyle = tabStyle;
        AutomationProperties.SetAutomationId(desktopSidebarTabs, "DesktopSidebarTabs");
        AutomationProperties.SetName(desktopSidebarTabs, "Plugin sidebar tabs");
        return desktopSidebarTabs;
    }
    private void CreateDesktopPluginRuntime()
    {
        CreateDesktopPluginHost();
        var runtime = new DesktopPluginRuntime(desktopPluginCatalog, desktopPluginHost!, Dispatcher);
        desktopPlugins = runtime;
        runtime.LoadProgress += (_, progress) =>
        {
            if (!ReferenceEquals(desktopPlugins, runtime)) return;
            ReportStartupProgress("Plugins", progress.State switch
            {
                "loading" => $"Loading {progress.Id}…",
                "loaded" => $"Loaded {progress.Id}.",
                "starting" => $"{progress.Id} · {progress.Diagnostic}",
                _ => $"{progress.Id} could not load: {progress.Diagnostic}"
            }, progress.State == "failed");
        };
        runtime.Changed += (_, _) =>
        {
            if (!ReferenceEquals(desktopPlugins, runtime)) return;
            try { MountDesktopPlugins(); _ = RefreshConversationWorkspaceAsync(); }
            catch (Exception error)
            {
                desktopPluginStartupMountError ??= error;
                ReportStartupProgress("Plugins", error.GetType().Name + ": " + error.Message, true);
                SetStatus("Plugin contributions could not be mounted: " + error.Message, true);
            }
        };
        // Composer controls are built before the first runtime, and their registration outlives
        // later owner replacements. Give each current owner its own revocable target.
        foreach (var mount in desktopTextEntryMounts.Where(value => !value.Question).ToList())
            MountDesktopTextEntryActions(mount);
    }

    private void InitializeDesktopPlugins()
    {
        CreateDesktopPluginRuntime();
        pluginRefreshTimer.Tick += (_, _) =>
        {
            try
            {
                if (!desktopPluginCatalog.TryList(out var plugins)) return;
                var state = string.Join("|", plugins.Select(x => x.Id + ":" + x.Version + ":" + x.Enabled + ":" + x.Error));
                if (state == desktopPluginCatalogState) return;
                desktopPluginCatalogState = state; ReloadDesktopPlugins();
            }
            catch (Exception ex) { pluginRefreshTimer.Stop(); SetStatus("Plugin refresh failed: " + ex.Message + ". Use Reload plugins to retry.", true); }
        };
        Loaded += (_, _) => BeginDesktopPluginStartup();
    }

    private void BeginDesktopPluginStartup()
    {
        // Normal splash startup calls this before Show. A later real Loaded event
        // must not create another plugin generation or repeat startup callbacks.
        // Diagnostic construction checks must never load user-installed native code.
        if (desktopPluginsStarted || IsAttachedDraftFixtureProcess() ||
            Environment.GetCommandLineArgs().Any(x => x.StartsWith("--check-", StringComparison.Ordinal) || x == "--diagnostics")) return;
        desktopPluginsStarted = true;
        ReportStartupProgress("Plugins", "Discovering installed desktop plugins…");
        SeedBundledDesktopPlugins();
        ReloadDesktopPlugins(); pluginRefreshTimer.Start();
        var status = desktopPlugins?.Status ?? [];
        var failures = status.Count(item => !item.Loaded);
        ReportStartupProgress("Plugins", !IsConnected
            ? "Waiting for the verified owner connection before loading desktop plugins."
            : failures == 0 ? $"Mounted {status.Count} desktop plugin package(s)."
            : $"Mounted available plugins; {failures} package(s) need attention.", failures > 0);
    }
    private void SeedBundledDesktopPlugins()
    {
        var root = BundledDesktopPluginPaths.DirectoryPath;
        if (!Directory.Exists(root)) return;
        foreach (var manifest in Directory.EnumerateFiles(root, DesktopPluginCatalog.ManifestFileName, SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            try { desktopPluginCatalog.SeedBundled(manifest, BundledDesktopPluginPaths.EnableOnFirstInstall(manifest)); }
            catch (Exception ex) { SetStatus($"Bundled plugin {Path.GetFileName(Path.GetDirectoryName(manifest))} could not be prepared: " + ex.Message, true); }
        }
        try { _ = desktopPluginCatalog.ReconcileExclusiveGroups(); }
        catch (Exception ex) { SetStatus("Alternative plugin providers could not be reconciled: " + ex.Message, true); }
    }
    private void ReloadDesktopPlugins()
    {
        // The task-owned attach fixture has no permission to discover or load bundled code.
        if (IsAttachedDraftFixtureProcess()) return;
        try
        {
            EnsureDesktopPluginOwner();
            if (desktopPlugins is null || !IsConnected || Lifetime.IsCancellationRequested ||
                desktopPluginOwner?.MatchesOwner() != true) return;
            desktopPlugins.LoadEnabled();
            if (!Environment.GetCommandLineArgs().Any(x => x.StartsWith("--check-", StringComparison.Ordinal) || x == "--diagnostics")) pluginRefreshTimer.Start();
        }
        catch (Exception ex) { ReportDesktopPluginRefreshError(ex); }
    }

    private async Task<DesktopPluginPresentationReceipt> PresentDesktopPluginAsync(InterfaceRequest request,
        CerebralClient ownerClient, CancellationToken cancellationToken, bool controlTestsAllowed = false, bool activateWindow = true)
    {
        Dispatcher.VerifyAccess();
        var failureStage = InterfaceFailureStage.PackageInspection;
        try
        {
            var manifest = desktopPluginCatalog.Inspect(request.Path);
            var runtime = desktopPlugins ?? throw new InvalidOperationException("The native desktop plugin runtime is unavailable.");
            using var admission = runtime.ReserveForPublication(request.Path, enabled: true);
            // Reserve may observe newer mutable source bytes than the Worker pre-effect recheck.
            // Fail before any catalog mutation or native load, and retain the copy-time check below.
            var reviewedFingerprint = ChatPluginInstallPackageAdmission.RequireReviewedCandidate(
                request, admission.CandidateFingerprint(manifest.Id));
            var installed = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == manifest.Id);
            failureStage = InterfaceFailureStage.PackageInstall;
            var newlyInstalled = installed is null;
            var updated = false;
            var refreshed = false;
            if (installed is null)
                installed = desktopPluginCatalog.Install(request.Path, reviewedFingerprint);
            else if (installed.Error is not null)
                throw new InvalidOperationException("The installed native plugin is invalid. Repair or remove it in Pegasus Cerebral Configuration before applying an update.");
            else if (installed.Version != manifest.Version)
            {
                installed = await runtime.UpdateReviewedAsync(request.Path, reviewedFingerprint, enableCandidate: true,
                    () => ReferenceEquals(desktopPlugins, runtime) && desktopPluginOwner?.MatchesOwner() == true &&
                        IsConnected && !Lifetime.IsCancellationRequested,
                    () =>
                    {
                        MountDesktopPlugins();
                        if (!runtime.Panels.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.WorkspacePanels.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.StatusItems.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.NewConversationViews.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.ComposerAccessories.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.WorkspaceTrees.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.InputModes.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.ComposerCommands.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.ComposerActions.Any(item => item.PluginId == manifest.Id) &&
                            !runtime.TextEntryActions.Any(item => item.PluginId == manifest.Id))
                            throw new InvalidOperationException("The native update has no supported Orchestrator contribution.");
                    }, cancellationToken, admission);
                updated = true;
            }
            else
            {
                // Owner approval is tied to this exact external manifest. Rebuilding during development
                // may intentionally retain the semantic version, so publish a fresh immutable generation
                // rather than silently reusing stale installed bytes.
                installed = desktopPluginCatalog.RefreshReviewed(request.Path, reviewedFingerprint);
                refreshed = true;
            }
            if (!installed.Enabled) desktopPluginCatalog.SetEnabled(installed.Id, true);

            if (!updated) { runtime.LoadEnabled(admission); MountDesktopPlugins(); }
            failureStage = InterfaceFailureStage.NativeLoad;
            var load = runtime.Status.SingleOrDefault(item => item.Id == manifest.Id);
            if (load is null || !load.Loaded)
                throw new InvalidOperationException("The native plugin did not load: " + (load?.Error ?? "no runtime contribution was reported"));
            failureStage = InterfaceFailureStage.InstalledGenerationChanged;
            var pluginReceipt = CreateDesktopPluginReceipt(runtime, installed, controlTestsAllowed);
            failureStage = InterfaceFailureStage.ContributionValidation;
            var panel = runtime.Panels.FirstOrDefault(item => item.PluginId == manifest.Id);
            var statusItem = runtime.StatusItems.FirstOrDefault(item => item.PluginId == manifest.Id);
            var newConversationView = runtime.NewConversationViews.FirstOrDefault(item => item.PluginId == manifest.Id);
            var composerAccessory = runtime.ComposerAccessories.FirstOrDefault(item => item.PluginId == manifest.Id);
            var workspaceTree = runtime.WorkspaceTrees.FirstOrDefault(item => item.PluginId == manifest.Id);
            if (panel is null && statusItem is null && newConversationView is null && composerAccessory is null && workspaceTree is null)
            {
                var inputModes = runtime.InputModes.Where(item => item.PluginId == manifest.Id).ToArray();
                var composerCommands = runtime.ComposerCommands.Where(item => item.PluginId == manifest.Id).ToArray();
                var actions = runtime.ComposerActions.Where(item => item.PluginId == manifest.Id).ToArray();
                var textEntryActions = runtime.TextEntryActions.Where(item => item.PluginId == manifest.Id).ToArray();
                var labels = inputModes.Select(mode => mode.Label)
                    .Concat(actions.Select(action => action.Label))
                    .Concat(textEntryActions.Select(action => action.Label))
                    .Concat(composerCommands.Select(command => command.Id))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
                if (labels.Length == 0)
                    throw new InvalidOperationException("The native plugin loaded but did not contribute a supported Orchestrator mount, action, command, or input mode.");
                failureStage = InterfaceFailureStage.ViewBinding;
                SelectPage("chat"); UpdateConversationLayout();
                if (activateWindow)
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Show(); Activate();
                }
                var nonvisualVerb = newlyInstalled ? "Installed and enabled" : updated ? "Updated and enabled" : refreshed ? "Refreshed and enabled" : "Enabled and reused";
                failureStage = InterfaceFailureStage.InstalledGenerationChanged;
                RequireDesktopPluginReceiptCurrent(runtime, pluginReceipt);
                return new($"{nonvisualVerb} native plugin {manifest.Name} {manifest.Version}; activated {string.Join(", ", labels)}. No screenshot attachment is produced for a nonvisual contribution.", null, pluginReceipt);
            }

            failureStage = InterfaceFailureStage.ViewBinding;
            FrameworkElement captureView;
            string mountedKind;
            string mountedTitle;
            if (panel is not null)
            {
                var key = panel.PluginId + "." + panel.Id;
                var tab = desktopSidebarTabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Tag, key)) ??
                    throw new InvalidOperationException("The native plugin panel was not mounted in the Cerebral sidebar.");
                desktopSidebarTabs.SelectedItem = tab;
                captureView = panel.View;
                mountedKind = "sidebar panel";
                mountedTitle = panel.Title;
            }
            else if (statusItem is not null)
            {
                captureView = statusItem.View;
                mountedKind = "status-bar view";
                mountedTitle = statusItem.Title;
            }
            else if (newConversationView is not null)
            {
                captureView = newConversationView.View;
                mountedKind = "new-conversation start view";
                mountedTitle = newConversationView.Title;
            }
            else if (composerAccessory is not null)
            {
                captureView = composerAccessory.View;
                mountedKind = "composer accessory";
                mountedTitle = composerAccessory.Title;
            }
            else
            {
                captureView = workspaceTree!.View;
                mountedKind = "workspace view";
                mountedTitle = workspaceTree.Title;
            }

            SelectPage("chat");
            if (panel is not null) activityRequested = true;
            UpdateConversationLayout();
            using var backgroundRender = activateWindow ? null : DesktopBackgroundRenderScope.Begin(this);
            if (activateWindow)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show(); Activate();
            }
            failureStage = InterfaceFailureStage.ViewCapture;
            UpdateLayout(); captureView.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, cancellationToken);
            failureStage = InterfaceFailureStage.InstalledGenerationChanged;
            RequireDesktopPluginReceiptCurrent(runtime, pluginReceipt);
            failureStage = InterfaceFailureStage.ViewCapture;
            var png = DesktopPanelCapture.CapturePng(captureView);
            var resourceRequestId = "interface-" + request.Id;
            await using var source = new MemoryStream(png, writable: false);
            failureStage = InterfaceFailureStage.AttachmentImport;
            var transfer = new AttachmentTransferClient(new(resourceRequestId, request.ProfileId, request.SessionId,
                "PluginView-" + manifest.Id + ".png", source.Length), ownerClient);
            var snapshot = await transfer.UploadAsync(source, cancellationToken);
            var attachment = snapshot.Attachment ?? throw new InvalidOperationException(
                "The plugin view was rendered, but its attachment commit is not yet confirmed. Reconcile the retained import before retrying any presentation.");
            // The attachment transfer yields the dispatcher. A catalog refresh may replace the loaded package
            // during that time, so do not issue a successful control binding for whichever version is now active.
            failureStage = InterfaceFailureStage.InstalledGenerationChanged;
            RequireDesktopPluginReceiptCurrent(runtime, pluginReceipt);
            var verb = newlyInstalled ? "Installed and enabled" : updated ? "Updated and enabled" : refreshed ? "Refreshed and enabled" : "Enabled and reused";
            return new($"{verb} native plugin {manifest.Name} {manifest.Version}; mounted {mountedKind} {mountedTitle}; captured the actual mounted view as attachment {attachment.Id} revision {attachment.Revision}.",
                resourceRequestId, pluginReceipt, new(attachment.Id, attachment.Revision, attachment.MediaType, attachment.OriginalName, attachment.Length));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (error is DesktopPluginActivationException) failureStage = InterfaceFailureStage.NativeLoad;
            throw new DesktopPluginPresentationException(failureStage, error);
        }
    }

    private InterfacePluginReceipt CreateDesktopPluginReceipt(DesktopPluginRuntime runtime,
        DesktopPluginInfo installed, bool controlTestsAllowed)
    {
        Dispatcher.VerifyAccess();
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed.Directory));
        var generation = Path.GetFileName(directory);
        var store = Path.Combine(desktopPluginCatalog.RootDirectory, ".packages", installed.Id);
        if (!string.Equals(Path.GetDirectoryName(directory), store, StringComparison.OrdinalIgnoreCase) ||
            generation.Length is < 1 or > 160)
            throw new InvalidOperationException("The reviewed plugin does not have an immutable installed package generation.");

        // Only publish identifiers of panels actually attached to this host. The control bridge currently
        // supports sidebar panels; a native contribution in another slot is not an implicit control grant.
        var mounts = runtime.Panels.Where(item => item.PluginId == installed.Id)
            .Where(panel => desktopSidebarTabs.Items.OfType<TabItem>().Any(tab =>
                Equals(tab.Tag, panel.PluginId + "." + panel.Id) &&
                tab.Content is ContentControl content && ReferenceEquals(content.Content, panel.View)))
            .Select(panel => new InterfacePluginMountReceipt("sidebar", panel.PluginId + "." + panel.Id))
            .Distinct().OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (mounts.Length > DesktopMountContract.MaximumContributionsPerPlugin + 8)
            throw new InvalidOperationException("The installed plugin has too many mounted sidebar contributions for a bounded receipt.");
        var receipt = new InterfacePluginReceipt(installed.Id, installed.Version, generation,
            Path.GetFullPath(installed.ManifestPath), mounts, controlTestsAllowed);
        RequireDesktopPluginReceiptCurrent(runtime, receipt);
        return receipt;
    }

    private void RequireDesktopPluginReceiptCurrent(DesktopPluginRuntime runtime, InterfacePluginReceipt receipt)
    {
        Dispatcher.VerifyAccess();
        var installed = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == receipt.Id);
        var loaded = runtime.GetLoadedPackage(receipt.Id);
        var expectedManifest = Path.Combine(desktopPluginCatalog.RootDirectory, ".packages", receipt.Id,
            receipt.Generation, DesktopPluginCatalog.ManifestFileName);
        if (installed is null || installed.Error is not null || !installed.Enabled ||
            !string.Equals(installed.Version, receipt.Version, StringComparison.Ordinal) ||
            !string.Equals(installed.ManifestPath, receipt.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.ManifestPath, expectedManifest, StringComparison.OrdinalIgnoreCase) ||
            loaded is not { } package || !string.Equals(package.Version, receipt.Version, StringComparison.Ordinal) ||
            !string.Equals(package.Directory, installed.Directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed or loaded plugin generation changed during presentation. Reconcile this owner request and its retained capture before reviewing the current package again.");
    }

    private void MountDesktopPlugins()
    {
        InvalidateComposerSuggestion();
        if (Lifetime.IsCancellationRequested || desktopPluginDisposalRequested || desktopPluginOwnerRefreshPending) return;
        desktopPluginMountDepth++;
        try { MountDesktopPluginContributions(); }
        finally
        {
            desktopPluginMountDepth--;
            DrainDeferredDesktopPluginOwnerRefresh();
        }
    }

    private void DrainDeferredDesktopPluginOwnerRefresh()
    {
        if (desktopPluginMountDepth != 0 || !desktopPluginOwnerRefreshPending) return;
        desktopPluginOwnerRefreshPending = false;
        if (Lifetime.IsCancellationRequested || desktopPluginDisposalRequested) DisposeDesktopPlugins();
        else DesktopPluginOwnerChanged(this, EventArgs.Empty);
    }

    private void MountDesktopPluginContributions()
    {
        var selectedSidebarKey = (desktopSidebarTabs.SelectedItem as TabItem)?.Tag as string;
        RemoveDesktopPluginSidebarTabs(); desktopPluginActions.Children.Clear(); desktopPluginActions.Visibility = Visibility.Collapsed; ClearDesktopAccessoryViews();
        ClearDesktopWorkspaceTrees(); ClearDesktopInputGuidance(); MountDesktopStatusItems();
        foreach (var mount in desktopTextEntryMounts.ToList()) mount.ResetActions();
        if (workspaceActionRow is not null)
            foreach (var button in workspaceActionRow.Children.OfType<Button>().Where(x => Equals(x.Tag, "desktop-plugin-workspace")).ToList()) workspaceActionRow.Children.Remove(button);
        if (desktopPlugins is null) { ClearPluginWorkPanels(); return; }
        MountDesktopStatusItems();
        MountDesktopInputModes();
        MountPluginWorkPanels();
        foreach (var panel in desktopPlugins.Panels)
        {
            // Detach retained views from their previous host before mounting after catalog refresh.
            if (panel.View.Parent is ContentControl old) old.Content = null;
            var key = panel.PluginId + "." + panel.Id;
            var content = new ContentControl
            {
                Content = panel.View,
                Background = Ui.Surface,
                Foreground = Ui.Ink,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            var tab = Ui.TabPage(panel.Title, content, new Thickness(10, 7, 10, 7));
            tab.Tag = key; tab.ToolTip = panel.Title;
            AutomationProperties.SetAutomationId(tab, "DesktopSidebarTab." + key);
            AutomationProperties.SetName(tab, panel.Title + " plugin sidebar");
            desktopPluginSidebarTabs.Add(tab);
            desktopSidebarTabs.Items.Add(tab);
        }
        foreach (var action in desktopPlugins.ActionsFor(DesktopMountSlots.ComposerActions))
        {
            var button = Ui.Button(action.Label, () => Run(() => action.ExecuteAsync(Lifetime.Token)));
            button.ToolTip = action.ToolTip; desktopPluginActions.Children.Add(button);
        }
        desktopPluginActions.Visibility = desktopPluginActions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var accessory in desktopPlugins.ComposerAccessories)
        {
            if (accessory.View.Parent is ContentControl old) old.Content = null;
            var frame = DesktopMountLayout.AccessoryFrame(accessory.View);
            AutomationProperties.SetAutomationId(frame, "PluginComposerAccessory-" + accessory.PluginId + "-" + accessory.Id);
            AutomationProperties.SetName(frame, accessory.Title + " composer accessory");
            desktopPluginAccessories.Children.Add(frame);
        }
        desktopPluginAccessories.Visibility = desktopPluginAccessories.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var branch in desktopPlugins.WorkspaceTrees)
        {
            if (branch.View.Parent is ContentControl old) old.Content = null;
            var frame = DesktopMountLayout.WorkspaceTreeFrame(branch.Title, branch.View);
            AutomationProperties.SetAutomationId(frame, "PluginWorkspaceTree-" + branch.PluginId + "-" + branch.Id);
            AutomationProperties.SetName(frame, branch.Title + " plugin workspace branch");
            desktopWorkspaceTrees.Children.Add(frame);
        }
        desktopWorkspaceTrees.Visibility = desktopWorkspaceTrees.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var mount in desktopTextEntryMounts.ToList()) MountDesktopTextEntryActions(mount);
        if (workspaceActionRow is not null)
            foreach (var action in desktopPlugins.ActionsFor(DesktopMountSlots.WorkspaceActions))
            {
                var button = Ui.Button(action.Label, () => Run(() => action.ExecuteAsync(Lifetime.Token)));
                button.Padding = new Thickness(8, 5, 8, 5); button.MinHeight = 29; button.FontSize = 12;
                button.Margin = new Thickness(0, 0, 5, 5); button.ToolTip = action.ToolTip; button.Tag = "desktop-plugin-workspace";
                AutomationProperties.SetAutomationId(button, "PluginWorkspaceAction-" + action.PluginId + "-" + action.Id);
                workspaceActionRow.Children.Add(button);
            }
        if (selectedSidebarKey is not null)
            desktopSidebarTabs.SelectedItem = desktopSidebarTabs.Items.OfType<TabItem>().FirstOrDefault(x => Equals(x.Tag, selectedSidebarKey)) ?? desktopSidebarTabs.Items.OfType<TabItem>().FirstOrDefault();
        else if (desktopSidebarTabs.SelectedItem is null)
            desktopSidebarTabs.SelectedItem = desktopSidebarTabs.Items.OfType<TabItem>().FirstOrDefault();
        var failures = desktopPlugins.Status.Where(x => x.Error is not null).ToList();
        var previousPluginDiagnostic = desktopPluginLoadDiagnostic;
        desktopPluginLoadDiagnostic = failures.Count > 0
            ? "Desktop plugin load failed: " + string.Join("; ", failures.Take(3).Select(x => $"{x.Id}: {x.Error}")) +
                ". Review Desktop plugins in Pegasus Cerebral Configuration."
            : null;
        if (desktopPluginLoadDiagnostic is not null) SetStatus(desktopPluginLoadDiagnostic, true);
        else if (previousPluginDiagnostic is not null &&
            string.Equals(ServiceStatusText, previousPluginDiagnostic, StringComparison.Ordinal))
            SetStatus("Desktop plugins loaded.");
        if (session is not null) RenderSession();
        else RenderWelcome();
    }

    private FrameworkElement RegisterDesktopTextEntry(TextBox input, string id, string purpose, bool question = false)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var indicator = new TextEntryActionIndicator(id);
        indicator.Status.Margin = new Thickness(5, 0, 5, 0); indicator.Status.MaxWidth = question ? 520 : 280;
        indicator.Status.TextWrapping = TextWrapping.NoWrap; indicator.Status.TextTrimming = TextTrimming.CharacterEllipsis;
        indicator.Status.VerticalAlignment = VerticalAlignment.Center;
        var view = new WrapPanel { Orientation = Orientation.Horizontal, Margin = question ? new Thickness(0, 7, 0, 0) : new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        view.Children.Add(panel); view.Children.Add(indicator.View);
        var mount = new DesktopTextEntryMount(input, panel, indicator, id, purpose, question);
        desktopTextEntryMounts.Add(mount); MountDesktopTextEntryActions(mount);
        return view;
    }

    private async Task<DesktopOperationResult?> PrepareComposerTextEntrySubmissionAsync(string requestId,
        CancellationToken cancellationToken)
    {
        var mount = desktopTextEntryMounts.FirstOrDefault(value =>
            !value.Question && string.Equals(value.Id, "composer", StringComparison.Ordinal));
        if (mount?.Target is null) return null;
        foreach (var preparation in desktopPlugins?.TextEntrySubmissionPreparations ?? [])
        {
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            var authority = CaptureSemanticAttachAuthority();
            var scopedTarget = authority is null ? null : mount.Target.WithAdditionalAuthority(authority);
            DesktopOperationResult result;
            Exception? primary = null;
            try
            {
                result = await preparation.PrepareAsync(scopedTarget ?? mount.Target, cancellationToken);
                RequireSemanticAttachCurrent();
                EnsureSemanticAttachSubmissionAllowed();
            }
            catch (Exception error) { primary = error; throw; }
            finally { scopedTarget?.DisposePreserving(primary); }
            if (result.State is DesktopOperationState.Completed or DesktopOperationState.Accepted) continue;
            return result with { RequestId = requestId };
        }
        if (!mount.Indicator.HasPendingAction) return null;
        var detail = string.IsNullOrWhiteSpace(mount.Indicator.PendingMessage)
            ? "A text-entry action is still updating this message."
            : mount.Indicator.PendingMessage!.Trim();
        return new(DesktopOperationState.Rejected,
            detail + " Finish or cancel it, then send after its text appears in the message box.", requestId);
    }

    private void ResetQuestionDesktopTextEntries()
    {
        foreach (var mount in desktopTextEntryMounts.Where(x => x.Question).ToList())
        {
            mount.Dispose(); desktopTextEntryMounts.Remove(mount);
        }
    }

    private void MountDesktopTextEntryActions(DesktopTextEntryMount mount)
    {
        var owner = desktopPluginOwner;
        var runtime = desktopPlugins;
        var host = desktopPluginHost;
        bool OwnerCurrent() => !Lifetime.IsCancellationRequested && !desktopPluginDisposalRequested &&
            !desktopPluginOwnerRefreshPending && !revokingDesktopTextEntryTargets &&
            ReferenceEquals(desktopPluginOwner, owner) && ReferenceEquals(desktopPlugins, runtime) &&
            owner?.ObserveMatchesOwner() == true;
        if (runtime is null || mount.Lifetime.IsCancellationRequested || !OwnerCurrent()) return;
        mount.ResetActions();
        // Reset and focus publication can invoke plugin code. A resumed mount must not restore a
        // target for an owner retired by those callbacks while native teardown remains deferred.
        if (mount.Lifetime.IsCancellationRequested || !OwnerCurrent()) return;
        var target = new WpfDesktopTextEntry(mount.Input, mount.Id, mount.Purpose,
            () => !mount.Lifetime.IsCancellationRequested && (!mount.Question || mount.Input.IsLoaded),
            OwnerCurrent, mount.Lifetime.Token, mount.Indicator.Report);
        mount.SetTarget(target, focused =>
        {
            if (focused && OwnerCurrent()) host?.PublishFocusedTextEntry(target);
            else if (ReferenceEquals(host?.FocusedTextEntry, target)) host.PublishFocusedTextEntry(null);
        });
        if (target.IsKeyboardFocusWithin) host?.PublishFocusedTextEntry(target);
        var slot = mount.Question ? DesktopMountSlots.AnswerActions : DesktopMountSlots.ComposerActions;
        foreach (var action in runtime.TextEntryActionsFor(slot, mount.Id))
        {
            if (!OwnerCurrent()) break;
            var button = Ui.Button(action.Label, () => Run(() => action.ExecuteAsync(target, Lifetime.Token)));
            button.Padding = new Thickness(9, 5, 9, 5); button.MinHeight = 29; button.FontSize = 12;
            button.Margin = new Thickness(0, 0, 5, 0); button.ToolTip = action.ToolTip;
            AutomationProperties.SetAutomationId(button, "PluginTextEntryAction-" + action.PluginId + "-" + action.Id + "-" + mount.Id);
            mount.Indicator.Register(action.PluginId, action.Id, button);
            mount.Actions.Children.Add(button);
        }
        if (!OwnerCurrent())
        {
            target.RevokeAuthority();
            target.CancelAvailability();
        }
    }
    private void RemoveDesktopPluginSidebarTabs()
    {
        foreach (var tab in desktopPluginSidebarTabs)
        {
            tab.Content = null;
            desktopSidebarTabs.Items.Remove(tab);
        }
        desktopPluginSidebarTabs.Clear();
        if (desktopSidebarTabs.SelectedItem is null)
            desktopSidebarTabs.SelectedItem = desktopSidebarTabs.Items.OfType<TabItem>().FirstOrDefault();
    }

    private void ClearDesktopAccessoryViews()
    {
        foreach (var frame in desktopPluginAccessories.Children.OfType<Border>().ToList()) frame.Child = null;
        desktopPluginAccessories.Children.Clear(); desktopPluginAccessories.Visibility = Visibility.Collapsed;
    }

    private void ClearDesktopWorkspaceTrees()
    {
        foreach (var expander in desktopWorkspaceTrees.Children.OfType<Expander>().ToList())
        {
            if (expander.Content is ScrollViewer scroll) scroll.Content = null;
            expander.Content = null;
        }
        desktopWorkspaceTrees.Children.Clear(); desktopWorkspaceTrees.Visibility = Visibility.Collapsed;
    }

    private string DesktopInputConversationKey() => CurrentConversationId ?? "new:" + pluginGeneration;
    private static string DesktopInputModeKey(DesktopHostedInputMode mode) => mode.PluginId + "." + mode.Id;

    private void MountDesktopInputModes()
    {
        mountingDesktopInputModes = true;
        try
        {
            var wanted = desktopInputModeByConversation.GetValueOrDefault(DesktopInputConversationKey(), "direct");
            desktopInputModeSelection.Items.Clear();
            desktopInputQuickToggles.Children.Clear();
            desktopInputQuickStatus.Text = "";
            desktopInputModeSelection.Items.Add(new ComboBoxItem { Content = "Direct", Tag = new DesktopInputModeChoice("direct", "Direct", null), ToolTip = "Submit the exact message without plugin guidance." });
            foreach (var mode in desktopPlugins?.InputModes.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase) ?? Enumerable.Empty<DesktopHostedInputMode>())
            {
                var key = DesktopInputModeKey(mode);
                var choice = new DesktopInputModeChoice(key, mode.Label, mode);
                if (mode.QuickToggle)
                {
                    var toggle = new CheckBox { Content = mode.Label, Tag = choice, ToolTip = mode.ToolTip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
                    AutomationProperties.SetAutomationId(toggle, "DesktopInputModeToggle." + key);
                    toggle.Checked += async (_, _) => { if (!mountingDesktopInputModes) await EnableDesktopQuickModeAsync(toggle, choice); };
                    toggle.Unchecked += (_, _) =>
                    {
                        if (mountingDesktopInputModes) return;
                        DesktopQuickModes().Remove(key); ClearDesktopInputGuidance(); desktopInputQuickStatus.Text = "";
                        SetStatus(mode.Label + " disabled.");
                    };
                    desktopInputQuickToggles.Children.Add(toggle);
                }
                else desktopInputModeSelection.Items.Add(new ComboBoxItem { Content = mode.Label, Tag = choice, ToolTip = mode.ToolTip });
            }
            desktopInputModeSelection.SelectedItem = desktopInputModeSelection.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x => x.Tag is DesktopInputModeChoice choice && choice.Key == wanted) ?? desktopInputModeSelection.Items[0];
            if (desktopInputModeSelection.SelectedItem is ComboBoxItem { Tag: DesktopInputModeChoice selected })
            {
                desktopInputModeByConversation[DesktopInputConversationKey()] = selected.Key;
                SyncDesktopInputQuickToggles();
            }
            desktopInputModeLabel.Visibility = desktopInputModeSelection.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            desktopInputModeSelection.Visibility = desktopInputModeSelection.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            desktopInputModeBar.Visibility = desktopInputModeSelection.Items.Count > 1 || desktopInputQuickToggles.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { mountingDesktopInputModes = false; }
    }

    private HashSet<string> DesktopQuickModes()
    {
        var key = DesktopInputConversationKey();
        if (!desktopInputQuickModesByConversation.TryGetValue(key, out var selected))
            desktopInputQuickModesByConversation[key] = selected = new(StringComparer.Ordinal);
        return selected;
    }

    private async Task EnableDesktopQuickModeAsync(CheckBox toggle, DesktopInputModeChoice choice)
    {
        if (choice.Mode is null) return;
        toggle.IsEnabled = false; desktopInputQuickStatus.Text = "Loading…";
        try
        {
            var ready = await SetDesktopQuickModeAsync(choice.Mode, true, Lifetime.Token);
            desktopInputQuickStatus.Text = ready; SetStatus(ready);
        }
        catch (Exception ex)
        {
            DesktopQuickModes().Remove(choice.Key);
            var prior = mountingDesktopInputModes; mountingDesktopInputModes = true; toggle.IsChecked = false; mountingDesktopInputModes = prior;
            desktopInputQuickStatus.Text = "Unavailable"; SetStatus(choice.Label + " could not be enabled: " + ex.Message, true);
        }
        finally { toggle.IsEnabled = true; }
    }

    private async Task<string> SetDesktopQuickModeAsync(DesktopHostedInputMode mode, bool enabled,
        CancellationToken cancellationToken)
    {
        var key = DesktopInputModeKey(mode);
        var selected = DesktopQuickModes();
        if (!enabled)
        {
            selected.Remove(key); ClearDesktopInputGuidance(); desktopInputQuickStatus.Text = "";
            var disabled = mode.Label + " disabled."; SetStatus(disabled); return disabled;
        }
        if (selected.Contains(key)) return mode.Label + " is ready.";
        var prepared = mode.PrepareAsync is null ? null : await mode.PrepareAsync(cancellationToken);
        selected.Add(key);
        var ready = prepared ?? (IsPromptReviewMode(mode) ? mode.Label + " selected for the next message." : mode.Label + " is ready.");
        desktopInputQuickStatus.Text = ready; SetStatus(ready); return ready;
    }

    private void SyncDesktopInputQuickToggles()
    {
        var prior = mountingDesktopInputModes; mountingDesktopInputModes = true;
        try { var selected = DesktopQuickModes(); foreach (var toggle in desktopInputQuickToggles.Children.OfType<CheckBox>()) toggle.IsChecked = toggle.Tag is DesktopInputModeChoice choice && selected.Contains(choice.Key); }
        finally { mountingDesktopInputModes = prior; }
    }

    private void RestoreDesktopInputMode()
    {
        ClearDesktopInputGuidance();
        if (desktopInputModeBar.Children.Count > 0) MountDesktopInputModes();
    }

    private void CarryDesktopInputModeToSession(string sessionId)
    {
        if (desktopInputModeSelection.SelectedItem is ComboBoxItem { Tag: DesktopInputModeChoice choice })
            desktopInputModeByConversation[sessionId] = choice.Key;
        if (desktopInputQuickModesByConversation.TryGetValue(DesktopInputConversationKey(), out var quick))
            desktopInputQuickModesByConversation[sessionId] = new(quick, StringComparer.Ordinal);
    }

    private async Task<DesktopOperationResult?> InterceptDesktopInputAsync(DesktopSubmission submission, bool steering, CancellationToken cancellationToken)
    {
        if (composerRefinementLeases.Current is not null)
            return new(DesktopOperationState.Rejected, "Apply or cancel the current refinement before sending.", submission.RequestId);
        var modes = new List<DesktopHostedInputMode>();
        if (desktopInputModeSelection.SelectedItem is ComboBoxItem { Tag: DesktopInputModeChoice { Mode: { } primary } }) modes.Add(primary);
        var quick = DesktopQuickModes();
        modes.AddRange((desktopPlugins?.InputModes ?? []).Where(x => x.QuickToggle && quick.Contains(DesktopInputModeKey(x))));
        if (modes.Count == 0) { ClearDesktopInputGuidance(); return null; }
        var conversation = PluginConversation();
        if (conversation.Binding != submission.Binding) throw new InvalidOperationException("The conversation changed before input guidance could run.");
        foreach (var mode in modes)
        {
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            SetStatus("Applying " + mode.Label + " input guidance…");
            var oneShot = IsPromptReviewMode(mode);
            var review = !steering && oneShot;
            if (oneShot)
            {
                // Consume before work starts. A cancelled or failed one-shot never silently retries on Send.
                quick.Remove(DesktopInputModeKey(mode)); SyncDesktopInputQuickToggles();
            }
            var reviewLease = review ? BeginComposerRefinement(submission, cancellationToken) : null;
            DesktopInputInterception result;
            try
            {
                RequireSemanticAttachCurrent();
                EnsureSemanticAttachSubmissionAllowed();
                result = await mode.InterceptAsync(new(conversation, submission, steering), reviewLease?.Token ?? cancellationToken);
                RequireSemanticAttachCurrent();
                EnsureSemanticAttachSubmissionAllowed();
                cancellationToken.ThrowIfCancellationRequested();
                if (submission.Binding != PluginBinding()) throw new InvalidOperationException("The conversation changed while input guidance was running.");
                if (reviewLease is not null && !composerRefinementLeases.IsCurrent(reviewLease))
                    throw new OperationCanceledException("This refinement session was cleared or its original draft changed.");
            }
            catch { if (reviewLease is not null) ReleaseComposerRefinement(reviewLease); throw; }
            switch (result.State)
            {
                case DesktopInputInterceptionState.Pass:
                    if (reviewLease is not null) ReleaseComposerRefinement(reviewLease);
                    continue;
                case DesktopInputInterceptionState.Hold:
                    if (review && result.Guidance is null)
                    {
                        ClearDesktopInputGuidance();
                        return new(DesktopOperationState.Rejected, "The refinement preview was unavailable; the original draft was preserved.", submission.RequestId);
                    }
                    desktopInputGuidanceHost.Child = new ScrollViewer
                    {
                        Content = result.Guidance,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        MaxHeight = Math.Max(220, Math.Min(430, ActualHeight * .46))
                    };
                    desktopInputGuidanceHost.Visibility = Visibility.Visible;
                    UpdateDesktopInputGuidanceLayout();
                    var held = result.Message ?? "Complete the guidance before submitting this message.";
                    SetStatus(mode.Label + ": " + held);
                    return new(DesktopOperationState.Accepted, mode.Label + ": " + held, submission.RequestId);
                default:
                    ClearDesktopInputGuidance();
                    var rejected = result.Message ?? "The selected input guidance cancelled this submission.";
                    SetStatus(mode.Label + ": " + rejected, true);
                    return new(DesktopOperationState.Rejected, mode.Label + ": " + rejected, submission.RequestId);
            }
        }
        ClearDesktopInputGuidance(); return null;
    }

    private async Task<DesktopComposerCommandApplication> ApplyDesktopComposerCommandsAsync(DesktopSubmission submission,
        bool steering, CancellationToken cancellationToken)
    {
        var commands = desktopPlugins?.ComposerCommands ?? [];
        if (commands.Count == 0) return new(submission.Text, false, null);
        var conversation = PluginConversation();
        if (conversation.Binding != submission.Binding)
            throw new InvalidOperationException("The conversation changed before its composer command could run.");
        foreach (var command in commands)
        {
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            var result = await command.ExecuteAsync(new(conversation, submission, steering), cancellationToken);
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            if (submission.Binding != PluginBinding())
                throw new InvalidOperationException("The conversation changed while its composer command was running.");
            if (result.State == DesktopComposerCommandState.Pass) continue;
            var remainder = submission.Text[result.ConsumedPrefixCharacters..];
            return result.State == DesktopComposerCommandState.Handled
                ? new("", true, result.Message)
                : new(remainder, false, result.Message);
        }
        return new(submission.Text, false, null);
    }

    private void ClearDesktopInputGuidance()
    {
        ReleaseComposerRefinement();
        desktopInputGuidanceHost.Child = null;
        desktopInputGuidanceHost.Visibility = Visibility.Collapsed;
        UpdateDesktopInputGuidanceLayout();
        RenderAttachmentDrafts();
    }
    private void AddDesktopMessagePresentation(StackPanel body, FrameworkElement original, SessionMessage message)
    {
        var presentation = desktopPlugins?.Present(PluginMessage(message));
        body.Children.Add(presentation?.Replacement ?? original);
        if (presentation?.Decoration is { } decoration) body.Children.Add(decoration);
    }
    private void RevokeDesktopTextEntryTargets()
    {
        Dispatcher.VerifyAccess();
        var targets = desktopTextEntryMounts.Select(mount => mount.Target).OfType<WpfDesktopTextEntry>().Distinct().ToList();
        // Cancellation invokes plugin callbacks synchronously. Revoke every target first so a
        // callback on one cannot use another target whose cancellation has not yet been delivered.
        foreach (var target in targets) target.RevokeAuthority();
        if (revokingDesktopTextEntryTargets) return;
        revokingDesktopTextEntryTargets = true;
        List<Exception>? errors = null;
        try
        {
            foreach (var target in targets)
                try { target.CancelAvailability(); }
                catch (Exception error) { (errors ??= []).Add(error); }
        }
        finally { revokingDesktopTextEntryTargets = false; }
        if (errors is not null)
            throw new AggregateException("Desktop text-entry cancellation callbacks failed.", errors);
    }

    private void DisposeDesktopPlugins(bool preservePersistentTextEntries = false)
    {
        Dispatcher.VerifyAccess();
        InvalidateComposerSuggestion();
        if (!preservePersistentTextEntries) desktopPluginDisposalRequested = true;
        desktopPluginOwner?.Retire();
        pluginRefreshTimer.Stop();
        if (desktopPluginMountDepth != 0)
        {
            // Authority and cancellation cannot wait for native callbacks to stop pumping.
            // Leave subscriptions, controls and runtime teardown to the outer mount's finally.
            desktopPluginOwnerRefreshPending = true;
            RevokeDesktopTextEntryTargets();
            return;
        }
        if (disposingDesktopPlugins)
        {
            RevokeDesktopTextEntryTargets();
            return;
        }
        disposingDesktopPlugins = true;
        desktopPluginOwnerRefreshPending = false;
        var previous = desktopPlugins;
        desktopPlugins = null; desktopPluginHost = null; desktopPluginOwner = null;
        Exception? teardownFailure = null;
        try
        {
            List<Exception>? textEntryErrors = null;
            try { RevokeDesktopTextEntryTargets(); }
            catch (Exception error) { (textEntryErrors ??= []).Add(error); }
            foreach (var mount in desktopTextEntryMounts.ToList())
            {
                var retain = preservePersistentTextEntries && !desktopPluginDisposalRequested && !mount.Question;
                try
                {
                    // Keep the host-owned composer view, not the previous owner's target or actions.
                    if (retain) mount.ResetActions();
                    else mount.Dispose();
                }
                catch (Exception error) { (textEntryErrors ??= []).Add(error); }
                finally { if (!retain) desktopTextEntryMounts.Remove(mount); }
            }
            ClearPrimaryStatusControls();
            RemoveDesktopPluginSidebarTabs(); desktopPluginActions.Children.Clear(); desktopPluginActions.Visibility = Visibility.Collapsed;
            ClearDesktopAccessoryViews(); ClearDesktopWorkspaceTrees(); ClearDesktopInputGuidance(); ClearPluginWorkPanels();
            if (workspaceActionRow is not null)
                foreach (var button in workspaceActionRow.Children.OfType<Button>().Where(x => Equals(x.Tag, "desktop-plugin-workspace")).ToList()) workspaceActionRow.Children.Remove(button);
            if (textEntryErrors is not null)
                throw new AggregateException("Desktop text-entry targets could not be fully retired.", textEntryErrors);
        }
        catch (Exception error) { teardownFailure = error; throw; }
        finally
        {
            // A native view's teardown failure must still enter the existing runtime retirement path.
            Exception? disposalFailure = null;
            try { previous?.Dispose(); }
            catch (Exception error) { disposalFailure = error; if (teardownFailure is null) throw; }
            finally
            {
                desktopPluginRetirement = Task.WhenAll(
                    desktopPluginRetirement.IsCompletedSuccessfully ? Task.CompletedTask : desktopPluginRetirement,
                    teardownFailure is null ? Task.CompletedTask : Task.FromException(teardownFailure),
                    disposalFailure is null ? Task.CompletedTask : Task.FromException(disposalFailure),
                    previous is null ? Task.CompletedTask : previous.ObserveRetirementAsync());
                disposingDesktopPlugins = false;
                // A close callback may arrive after a persistent registration was retained.
                // Promote that completed replacement cleanup to full cleanup exactly once.
                if (preservePersistentTextEntries && desktopPluginDisposalRequested)
                    try { DisposeDesktopPlugins(); }
                    catch (Exception error)
                    {
                        desktopPluginRetirement = Task.WhenAll(desktopPluginRetirement, Task.FromException(error));
                        if (teardownFailure is null && disposalFailure is null) throw;
                    }
            }
        }
    }

    private async Task AwaitDesktopPluginsForFullExitAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (desktopPluginMountDepth != 0 || disposingDesktopPlugins || changingDesktopPluginOwner)
            throw new InvalidOperationException("Desktop extension mounting is still active; Exit did not stop the Worker.");
        if (desktopPlugins is not null) DisposeDesktopPlugins();
        if (desktopPlugins is not null)
            throw new InvalidOperationException("Desktop extension retirement was deferred; Exit did not stop the Worker.");
        // This bounds observation only; synchronous native Dispose callbacks have no hard deadline.
        try { await desktopPluginRetirement.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException(
                "Desktop extension cleanup is still pending. Main and Worker remain open; extension controls are unavailable until the original cleanup settles. Retry Exit after settlement.", error);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Desktop extension cleanup observation was canceled. Main and Worker remain open; no cleanup was replayed.", error);
        }
    }

    private sealed record DesktopInputModeChoice(string Key, string Label, DesktopHostedInputMode? Mode);
    private sealed record DesktopComposerCommandApplication(string Text, bool Handled, string? Message);

    private sealed class DesktopTextEntryMount(TextBox input, WrapPanel actions, TextEntryActionIndicator indicator,
        string id, string purpose, bool question) : IDisposable
    {
        public TextBox Input { get; } = input;
        public WrapPanel Actions { get; } = actions;
        public TextEntryActionIndicator Indicator { get; } = indicator;
        public string Id { get; } = id;
        public string Purpose { get; } = purpose;
        public bool Question { get; } = question;
        public CancellationTokenSource Lifetime { get; } = new();
        public WpfDesktopTextEntry? Target { get; private set; }
        private bool disposed;
        public void SetTarget(WpfDesktopTextEntry target, Action<bool> focusChanged)
        {
            Target?.Dispose(); Target = target; target.FocusChanged += focusChanged;
        }
        public void ResetActions()
        {
            var previous = Target; Target = null;
            try { previous?.Dispose(); }
            finally { Actions.Children.Clear(); Indicator.Reset(); }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (!Lifetime.IsCancellationRequested) Lifetime.Cancel(); }
            finally
            {
                try { ResetActions(); }
                finally { Lifetime.Dispose(); }
            }
        }
    }

    private sealed class WpfDesktopTextEntry :
        IDesktopTextEntry, IDesktopTextEntryActivitySink, IDesktopTextEntryLifetime, IDesktopTextEntryFocus, IDisposable
    {
        private readonly TextBox input;
        private readonly string id;
        private readonly string purpose;
        private readonly Func<bool> available;
        private readonly Func<bool> ownerCurrent;
        private readonly Func<bool>? additionalAuthority;
        private readonly CancellationTokenSource availabilityLifetime;
        private readonly CancellationToken availabilityToken;
        private readonly CancellationTokenRegistration mountLifetimeRegistration;
        private readonly Action<DesktopTextEntryActivity> reportActivity;
        private volatile bool disposed;
        private volatile bool authorityRevoked;
        private int availabilityCancellationState;
        private int availabilitySourceDisposed;
        public event Action<bool>? FocusChanged;
        public WpfDesktopTextEntry(TextBox input, string id, string purpose, Func<bool> available,
            Func<bool> ownerCurrent, CancellationToken availabilityToken, Action<DesktopTextEntryActivity> reportActivity)
            : this(input, id, purpose, available, ownerCurrent, availabilityToken, reportActivity, null) { }

        private WpfDesktopTextEntry(TextBox input, string id, string purpose, Func<bool> available,
            Func<bool> ownerCurrent, CancellationToken availabilityToken, Action<DesktopTextEntryActivity> reportActivity,
            Func<bool>? additionalAuthority)
        {
            this.input = input; this.id = id; this.purpose = purpose; this.available = available;
            this.ownerCurrent = ownerCurrent; this.additionalAuthority = additionalAuthority;
            availabilityLifetime = new CancellationTokenSource();
            this.availabilityToken = availabilityLifetime.Token; this.reportActivity = reportActivity;
            // Route parent cancellation through the same reentrancy guard as owner retirement.
            mountLifetimeRegistration = availabilityToken.Register(CancelAvailability);
            input.IsKeyboardFocusWithinChanged += OnFocusChanged;
        }

        // A semantic callback receives a short-lived view; the persistent ordinary target is unchanged.
        internal WpfDesktopTextEntry WithAdditionalAuthority(Func<bool> authority)
        {
            ArgumentNullException.ThrowIfNull(authority);
            return new(input, id, purpose, available, HasAuthority, availabilityToken, reportActivity, authority);
        }

        public string Id => id;
        public string Purpose => purpose;
        public string Text => ReadCurrent(() => input.Text);
        public int SelectionStart => ReadCurrent(() => input.SelectionStart);
        public int SelectionLength => ReadCurrent(() => input.SelectionLength);
        public CancellationToken AvailabilityToken => availabilityToken;
        public bool IsKeyboardFocusWithin => Read(() => HasAuthority() && input.IsKeyboardFocusWithin);
        internal bool IsAvailableForPlugin => Read(() => HasAuthority() && available() &&
            input.IsLoaded && input.IsEnabled && !input.IsReadOnly);
        private bool HasAuthority() => !authorityRevoked && !disposed && !availabilityToken.IsCancellationRequested &&
            ownerCurrent() && (additionalAuthority?.Invoke() ?? true);

        public void ReportActivity(DesktopTextEntryActivity activity)
        {
            ArgumentNullException.ThrowIfNull(activity);
            if (authorityRevoked || disposed || availabilityToken.IsCancellationRequested || input.Dispatcher.HasShutdownStarted) return;
            void ReportCurrent()
            {
                if (HasAuthority()) reportActivity(activity);
            }
            if (input.Dispatcher.CheckAccess()) ReportCurrent();
            else input.Dispatcher.BeginInvoke(ReportCurrent, DispatcherPriority.Normal);
        }

        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text); cancellationToken.ThrowIfCancellationRequested();
            if (input.Dispatcher.CheckAccess()) return Task.FromResult(Insert(text));
            return input.Dispatcher.InvokeAsync(() => Insert(text), DispatcherPriority.Normal, cancellationToken).Task;
        }

        public Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Dispatcher.CheckAccess()) return Task.FromResult(Focus());
            return input.Dispatcher.InvokeAsync(Focus, DispatcherPriority.Normal, cancellationToken).Task;
        }

        private DesktopOperationResult Focus()
        {
            if (!HasAuthority() || !available() || !input.IsEnabled || input.IsReadOnly)
                return new(DesktopOperationState.Rejected, "That text entry is no longer available.");
            input.Focus();
            return input.IsKeyboardFocusWithin
                ? new(DesktopOperationState.Completed)
                : new(DesktopOperationState.Rejected, "The text entry could not receive keyboard focus.");
        }

        private DesktopOperationResult Insert(string text)
        {
            if (!HasAuthority() || !available() || !input.IsEnabled || input.IsReadOnly)
                return new(DesktopOperationState.Rejected, "That text entry is no longer available.");
            if (input.Text.Length - input.SelectionLength + text.Length > DesktopPluginContract.MaxTextChars)
                return new(DesktopOperationState.Rejected, "The dictated text would exceed the desktop text limit.");
            var start = input.SelectionStart;
            input.SelectedText = text; input.CaretIndex = start + text.Length; input.SelectionLength = 0; input.Focus();
            return new(DesktopOperationState.Completed);
        }

        private T ReadCurrent<T>(Func<T> read) => Read(() =>
        {
            ObjectDisposedException.ThrowIf(!HasAuthority(), this);
            return read();
        });
        private T Read<T>(Func<T> read) => input.Dispatcher.CheckAccess() ? read() : input.Dispatcher.Invoke(read);
        private void OnFocusChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (HasAuthority()) FocusChanged?.Invoke(args.NewValue is true);
        }
        internal void RevokeAuthority() => authorityRevoked = true;

        internal void CancelAvailability()
        {
            if (Interlocked.CompareExchange(ref availabilityCancellationState, 1, 0) != 0) return;
            try { availabilityLifetime.Cancel(); }
            finally
            {
                Volatile.Write(ref availabilityCancellationState, 2);
                if (disposed) DisposeAvailabilitySource();
            }
        }

        private void DisposeAvailabilitySource()
        {
            if (Interlocked.Exchange(ref availabilitySourceDisposed, 1) == 0) availabilityLifetime.Dispose();
        }

        internal void DisposePreserving(Exception? primary)
        {
            if (primary is null) { Dispose(); return; }
            try { Dispose(); }
            catch (Exception cleanup)
            {
                AttachExistingStartup.PreserveSemanticCleanupFailure(primary, "text-entry-view-dispose", cleanup);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            var wasFocused = input.IsKeyboardFocusWithin;
            authorityRevoked = true;
            disposed = true; input.IsKeyboardFocusWithinChanged -= OnFocusChanged;
            try { CancelAvailability(); }
            finally
            {
                // A cancellation callback may reenter cleanup. Its outer Cancel must finish
                // before the source is disposed; authority has already been revoked.
                mountLifetimeRegistration.Dispose();
                if (Volatile.Read(ref availabilityCancellationState) != 1) DisposeAvailabilitySource();
                try { if (wasFocused) FocusChanged?.Invoke(false); }
                finally { FocusChanged = null; }
            }
        }
    }
}
