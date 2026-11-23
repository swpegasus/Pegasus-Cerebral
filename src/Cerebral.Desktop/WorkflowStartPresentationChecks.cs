using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Task-owned WPF HWND/automation-peer fixtures with an inert attachment and controlled catalog; no service or model.</summary>
internal static class WorkflowStartPresentationChecks
{
    internal const bool ShowsWindows = true;

    internal static async Task<int> VerifyAsync(Func<Window?, WorkflowStartForm> createForm, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(createForm);
        var application = Application.Current ?? throw new InvalidOperationException("A WPF Application is required.");
        application.Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        var savedMainWindow = application.MainWindow;
        var savedShutdownMode = application.ShutdownMode;
        var processMode = RenderOptions.ProcessRenderMode;
        var checks = 0;
        var shownWindows = 0;
        var expandedPopups = 0;
        var owners = new List<Window>();
        var forms = new List<WorkflowStartForm>();
        var sources = new List<HwndSource>();
        var openedSelectors = new List<ComboBox>();
        var cleanupErrors = new List<Exception>();
        Exception? primary = null;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Workflow start presentation: " + message);
            checks++;
        }
        HwndSource OwnSource(Window window)
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("The task-owned Window has no presentation source.");
            if (source.IsDisposed || source.Handle != handle || source.Dispatcher != window.Dispatcher || source.CompositionTarget is null)
                throw new InvalidOperationException("The task-owned Window has no current composition target of its own.");
            sources.Add(source);
            return source;
        }
        WorkflowStartForm Create(Window? owner)
        {
            var form = createForm(owner);
            forms.Add(form);
            form.Window.ShowActivated = false;
            form.Window.ShowInTaskbar = false;
            Check(new WindowInteropHelper(form.Window).Handle == IntPtr.Zero && !form.Window.IsVisible,
                "constructing the form does not create or show its child HWND");
            return form;
        }
        async Task DrainAsync()
        {
            await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            token.ThrowIfCancellationRequested();
        }
        async Task CheckSelectionNamesAsync(ComboBox selector, string expected)
        {
            selector.ApplyTemplate();
            selector.UpdateLayout();
            var peer = UIElementAutomationPeer.CreatePeerForElement(selector) as ComboBoxAutomationPeer
                ?? throw new InvalidOperationException("The selector did not create its actual WPF ComboBox peer.");
            var expansion = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                ?? throw new InvalidOperationException("The actual selector peer has no expand/collapse pattern.");
            expansion.Collapse();
            await DrainAsync();
            Check(!selector.IsDropDownOpen && peer.GetName() == expected,
                "the collapsed selector peer exposes only the selected readable label");
            openedSelectors.Add(selector);
            try
            {
                expansion.Expand();
                expandedPopups++;
                await DrainAsync();
                selector.UpdateLayout();
                Check(selector.IsDropDownOpen, "the actual selector popup is expanded for the item-peer check");
                var item = selector.ItemContainerGenerator.ContainerFromItem(selector.SelectedItem) as ComboBoxItem
                    ?? throw new InvalidOperationException("The expanded selector did not realize its selected item.");
                // The selector owns the item/data peers; a realized ComboBoxItem creates a separate wrapper peer.
                var selectedPeers = (peer.GetChildren() ?? []).Where(x =>
                    x.GetAutomationControlType() == AutomationControlType.ListItem &&
                    x.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider { IsSelected: true }).ToArray();
                Check(selectedPeers.Length == 1, "the actual expanded selector exposes exactly one selected item peer");
                var itemPeer = selectedPeers[0];
                Check(item.IsVisible && itemPeer.GetName() == expected,
                    "the expanded selected item peer exposes exactly the readable label without record/settings JSON");
                expansion.Collapse();
                await DrainAsync();
                Check(!selector.IsDropDownOpen && itemPeer.GetName() == expected && peer.GetName() == expected,
                    "collapsing retains the same readable item and selector automation names");
            }
            finally { selector.IsDropDownOpen = false; }
        }
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { cleanupErrors.Add(error); }
        }

        try
        {
            var ordinaryOwner = new Window
            {
                Title = "Task-owned ordinary workflow owner", ShowActivated = false, ShowInTaskbar = false
            };
            owners.Add(ordinaryOwner);
            var ordinarySource = OwnSource(ordinaryOwner);
            var defaultMode = ordinarySource.CompositionTarget.RenderMode;

            var unattachedOwner = new InertOwner();
            owners.Add(unattachedOwner);
            var unattachedSource = OwnSource(unattachedOwner);
            var unattachedMode = unattachedSource.CompositionTarget.RenderMode;

            var attachedOwner = new InertOwner();
            owners.Add(attachedOwner);
            var endpoint = new LocalWorkerEndpoint("cerebral-presentation-inert-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var inertDirectory = Path.Combine(Path.GetTempPath(), "cerebral-presentation-inert-" + Guid.NewGuid().ToString("N"));
            attachedOwner.UseExistingLocalWorker(endpoint, inertDirectory, "presentation-fixture-installation");
            var attachedSource = OwnSource(attachedOwner);
            var attachedMode = attachedSource.CompositionTarget.RenderMode;
            Check(attachedOwner.ExistingWorkerAttachment?.Endpoint == endpoint && attachedMode == RenderMode.SoftwareOnly,
                "the inert attached owner has the existing per-source software contract without connecting");

            var attachedForm = Create(attachedOwner);
            var attachedChildSource = OwnSource(attachedForm.Window);
            Check(!ReferenceEquals(attachedChildSource, attachedSource) &&
                attachedChildSource.Handle != attachedSource.Handle && attachedChildSource.CompositionTarget.RenderMode == RenderMode.SoftwareOnly,
                "an attached owner's child selects software composition on its own distinct HWND source");
            Check(attachedSource.CompositionTarget.RenderMode == attachedMode &&
                ordinarySource.CompositionTarget.RenderMode == defaultMode && RenderOptions.ProcessRenderMode == processMode,
                "configuring the child preserves its owner, the ordinary source and process rendering policy");

            foreach (var owner in new Window?[] { null, ordinaryOwner, unattachedOwner })
            {
                var form = Create(owner);
                var source = OwnSource(form.Window);
                Check(source.CompositionTarget.RenderMode == defaultMode && !form.Window.IsVisible,
                    "a null, ordinary or unattached owner leaves its child at the ordinary source's default rendering mode");
            }
            Check(ordinarySource.CompositionTarget.RenderMode == defaultMode &&
                unattachedSource.CompositionTarget.RenderMode == unattachedMode && attachedSource.CompositionTarget.RenderMode == attachedMode,
                "all three owner source modes survive construction of the four child cases");
            Check(owners.All(x => !x.IsVisible) && forms.All(x => !x.Window.IsVisible) &&
                attachedOwner.LoadedEvents == 0 && unattachedOwner.LoadedEvents == 0 &&
                attachedOwner.TransportDisabled && unattachedOwner.TransportDisabled,
                "EnsureHandle creates hidden sources without loading either ConnectedWindow owner");

            await attachedForm.InitializeAsync();
            attachedForm.SelectAgent("helper");
            await attachedForm.PendingRead.WaitAsync(token);
            attachedForm.SelectDefinition("fixture.workflow:run", "2.0.0");
            await attachedForm.PendingRead.WaitAsync(token);
            attachedForm.Window.Show();
            shownWindows++;
            await DrainAsync();
            Check(attachedForm.Window.IsVisible && !attachedForm.Window.ShowActivated && !attachedForm.Window.ShowInTaskbar,
                "only the task-owned child is shown without activation or a taskbar entry for real popup peers");
            await CheckSelectionNamesAsync(attachedForm.AgentSelector, "Configured helper · helper · helper");
            await CheckSelectionNamesAsync(attachedForm.DefinitionSelector, "fixture.workflow:run · 2.0.0");
            Check(owners.All(x => !x.IsVisible && !x.IsLoaded) && attachedOwner.LoadedEvents == 0 && unattachedOwner.LoadedEvents == 0 &&
                attachedOwner.TransportDisabled && unattachedOwner.TransportDisabled,
                "showing the child and its popups never shows or loads either connection owner");
            Check(attachedSource.CompositionTarget.RenderMode == attachedMode &&
                ordinarySource.CompositionTarget.RenderMode == defaultMode && unattachedSource.CompositionTarget.RenderMode == unattachedMode &&
                RenderOptions.ProcessRenderMode == processMode,
                "actual child/popup presentation preserves all owner sources and the process mode");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            foreach (var selector in openedSelectors) Cleanup(() => selector.IsDropDownOpen = false);
            for (var i = forms.Count - 1; i >= 0; i--)
            {
                var form = forms[i];
                Cleanup(form.Dispose);
                // Dispose closes a visible draft; an EnsureHandle-only Window also needs Close.
                Cleanup(form.Window.Close);
            }
            for (var i = owners.Count - 1; i >= 0; i--) Cleanup(owners[i].Close);
            Cleanup(() => application.MainWindow = savedMainWindow);
            Cleanup(() => application.ShutdownMode = savedShutdownMode);
        }

        if (primary is not null)
        {
            if (cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            cleanupErrors.Insert(0, primary);
        }
        if (cleanupErrors.Count != 0) throw new AggregateException("Task-owned presentation fixture or cleanup failed.", cleanupErrors);
        Check(sources.All(x => x.IsDisposed), "closing every task-owned Window disposes all seven initialized presentation sources");
        Check(ReferenceEquals(application.MainWindow, savedMainWindow) && application.ShutdownMode == savedShutdownMode &&
            RenderOptions.ProcessRenderMode == processMode, "verification restores the caller's Application lifetime and process mode");
        Check(shownWindows == 1 && expandedPopups == 2 && owners.Count == 3 && forms.Count == 4,
            "the declared fixture tier is exactly seven task-owned Windows, one shown child and two sequential popup expansions");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, workflowStartPresentationChecks = checks,
            evidence = "task-owned WPF HWND rendering-policy and actual collapsed/expanded automation-peer fixtures; no visual acceptance",
            taskOwnedWindows = owners.Count + forms.Count, shownWindows, expandedPopups,
            ownerWindowsShown = 0, ownerLoadedEvents = 0, serviceCalls = 0, modelCalls = 0,
            processRenderModeChanged = false
        }));
        return checks;
    }

    private sealed class InertOwner : ConnectedWindow
    {
        internal int LoadedEvents { get; private set; }
        internal bool TransportDisabled => Lifetime.IsCancellationRequested;
        internal InertOwner() : base("Orchestrator", "Task-owned presentation fixture", persistWindowPreferences: false)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            // StartInitialConnectionAsync first waits with this token, before any discovery or transport work.
            Lifetime.Cancel();
            Loaded += (_, _) => LoadedEvents++;
        }
        protected override Task ConnectedAsync(HostInfo info) =>
            throw new InvalidOperationException("The presentation fixture must never connect its owner.");
        protected override Task<object> InspectLoadedStateAsync(string? sessionId) =>
            throw new InvalidOperationException("The presentation fixture must never inspect a service.");
    }
}
