using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

public sealed partial class ChatIconsPlugin
{
    private readonly List<InlinePicker> inlinePickers = [];
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [new DesktopMountView(new("symbol-picker", DesktopMountSlots.ComposerAccessories)
        { Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } }, "Symbols", CreateInlinePicker)];

    public IReadOnlyDictionary<string, string> MeaningMappings => LoadMeaningMappings();

    private IReadOnlyDictionary<string, string> LoadMeaningMappings()
    {
        if (presentationSettings.Current is { } shared) return shared.Meanings;
        if (dataDirectory is null) return SemanticIconSettings.Defaults;
        try { return SemanticIconSettings.Load(Path.Combine(dataDirectory, SemanticIconSettings.FileName)); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ReportHostStatus("Icon meanings use local defaults because settings could not be read: " + error.Message);
            return SemanticIconSettings.Defaults;
        }
    }

    private FrameworkElement CreateInlinePicker()
    {
        // Only the compact anchor belongs inside the host's 280 x 44 clipping boundary.
        // Popup content owns a separate presentation source and never expands that mount.
        var root = new Grid { Width = 108, Height = 36, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(root, "ChatIcons.Accessory");
        var anchor = new ToggleButton { Content = "Symbols ▴", Height = 32, Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Choose Icons beside a text field to open its symbol picker." };
        ChatIconsToggleStyle.Apply(anchor);
        AutomationProperties.SetAutomationId(anchor, "ChatIcons.InlinePicker");
        AutomationProperties.SetName(anchor, "Symbols picker");
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Top, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None };
        AutomationProperties.SetAutomationId(popup, "ChatIcons.Popup");
        root.Children.Add(anchor); root.Children.Add(popup);
        var view = new InlinePicker(root, anchor, popup, CaptureAvailable, cleanup);
        inlinePickers.Add(view);
        anchor.Click += (_, _) =>
        {
            if (anchor.IsChecked != true) view.Close();
            else if (view.Target is null)
            {
                anchor.IsChecked = false;
                ReportHostStatus("Choose Icons beside the intended text field first.");
            }
        };
        popup.Closed += (_, _) => view.Close();
        root.Loaded += (_, _) => { if (!inlinePickers.Contains(view)) inlinePickers.Add(view); };
        root.Unloaded += (_, _) => { view.Close(); inlinePickers.Remove(view); };
        root.IsVisibleChanged += (_, _) => { if (!root.IsVisible) view.Close(); };
        return root;
    }

    private FrameworkElement CreatePopupBody(InlinePicker view, PickerTarget capture, out NativeIconPicker? symbols)
    {
        var status = new TextBlock { Text = "Insert into " + capture.Entry.Purpose + " · target locked until insertion or close",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(status, "ChatIcons.PickerStatus");
        var body = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = new Button { Content = "Close", MinWidth = 64, Padding = new Thickness(7, 3, 7, 3) };
        AutomationProperties.SetAutomationId(close, "ChatIcons.Close");
        close.Click += (_, _) => view.Close();
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Symbols", FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status);
        var panel = new Border { Child = body, Width = Math.Min(540, Math.Max(1, SystemParameters.WorkArea.Width - 24)),
            Height = Math.Min(500, Math.Max(1, SystemParameters.WorkArea.Height - 24)), BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ActiveBorderBrush, CornerRadius = new CornerRadius(4) };
        panel.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        panel.SetResourceReference(TextElement.ForegroundProperty, SystemColors.WindowTextBrushKey);
        AutomationProperties.SetAutomationId(panel, "ChatIcons.Picker");
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        panel.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { view.Close(); args.Handled = true; } };

        async Task InsertText(string text, string label)
        {
            // Every rendered control closes over this exact capture. A late click or library callback
            // from a dismissed popup cannot select a newer session's target.
            if (!ReferenceEquals(view.Target, capture) || view.Inserting) return;
            if (!CaptureAvailable(capture))
            { view.Close(); ReportHostStatus("The target changed. Reopen Icons beside the intended field."); return; }
            view.Inserting = true; panel.IsEnabled = false;
            try
            {
                var result = await InsertAssetAsync(Host, capture.Entry, capture.Binding, text, capture.Token);
                if (!ReferenceEquals(view.Target, capture)) return;
                status.Text = result.Message ?? (IsSuccess(result) ? "Inserted " + label + "." : "The symbol was not inserted.");
                // End this capture even on a rejected or uncertain result. No automatic replay.
                view.Close();
            }
            catch (OperationCanceledException) { if (ReferenceEquals(view.Target, capture)) view.Close(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (ReferenceEquals(view.Target, capture)) view.Close();
                if (!CallbacksRetired) ReportHostStatus("Symbol insertion was not confirmed: " + error.Message);
            }
            finally
            {
                panel.IsEnabled = true;
                if (ReferenceEquals(view.Target, capture)) view.Inserting = false;
            }
        }
        async Task CopyReference(ChatIconAsset asset, AssetPack pack)
        {
            if (!ReferenceEquals(view.Target, capture) || !CaptureAvailable(capture)) { view.Close(); return; }
            try
            {
                if (Host is not IDesktopClipboardHost clipboard) return;
                var result = await clipboard.SetClipboardTextAsync(pack.Reference(asset), capture.Token);
                if (ReferenceEquals(view.Target, capture)) status.Text = result.Message ?? "Copied the icon reference.";
            }
            catch (OperationCanceledException) { if (ReferenceEquals(view.Target, capture)) view.Close(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (ReferenceEquals(view.Target, capture)) status.Text = "Reference copy failed: " + error.Message; }
        }
        symbols = null;
        try
        {
            if (packDirectory is null) throw new FileNotFoundException("The local symbol catalog is unavailable.");
            var pack = AssetPack.Load(packDirectory); ValidatePickerPack(pack);
            symbols = new NativeIconPicker(pack.Assets, asset => InsertText(asset.Text, asset.Label), CreatePreview,
                Host is IDesktopClipboardHost ? asset => CopyReference(asset, pack) : null);
            if (Host is IDesktopServiceHost service)
            {
                var tabs = new TabControl();
                tabs.Items.Add(new TabItem { Header = "Symbols", Content = symbols });
                var libraryTab = new TabItem { Header = "Library" }; tabs.Items.Add(libraryTab);
                tabs.SelectionChanged += async (_, args) =>
                {
                    if (!ReferenceEquals(args.Source, tabs) || !ReferenceEquals(tabs.SelectedItem, libraryTab)) return;
                    if (!ReferenceEquals(view.Target, capture) || !CaptureAvailable(capture)) { view.Close(); return; }
                    var library = view.Library ??= new(service, Host as IDesktopClipboardHost, dataDirectory,
                        capture.Token, text => InsertText(text, "library symbol"));
                    libraryTab.Content = library.View;
                    await library.ActivateAsync();
                };
                body.Children.Add(tabs);
            }
            else body.Children.Add(symbols);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        { status.Text = "Symbols unavailable: " + error.Message; }
        return panel;
    }

    private Task? OpenInlinePicker(IDesktopTextEntry target, CancellationToken cancellationToken)
    {
        var view = inlinePickers.FirstOrDefault(item => item.Root.IsLoaded && item.Root.IsVisible);
        if (view is null || target.Id != "composer") return null;
        ClosePicker(); CloseInlinePickers();
        var capture = new PickerTarget(target, Host.GetConversation().Binding, cancellationToken);
        try
        {
            var body = CreatePopupBody(view, capture, out var symbols);
            return view.Open(capture, body, symbols is null ? null : symbols.FocusSearch);
        }
        catch { view.Close(); capture.Dispose(); throw; }
    }

    private bool CaptureAvailable(PickerTarget capture, bool matchText = true)
    {
        if (CallbacksRetired || capture.Token.IsCancellationRequested) return false;
        try { return Host.GetConversation().Binding == capture.Binding && (!matchText || capture.Matches()); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private void CloseInlinePickers()
    {
        foreach (var view in inlinePickers.ToArray()) view.Close();
    }

    private sealed class InlinePicker
    {
        internal FrameworkElement Root { get; }
        private readonly ToggleButton anchor;
        private readonly Popup popup;
        private readonly Func<PickerTarget, bool, bool> available;
        private readonly Cerebral.PluginLifetime.PluginCleanup cleanup;
        private readonly DispatcherTimer monitor;
        private CancellationTokenRegistration cancellation;
        private TaskCompletionSource? closed;
        private Window? owner;
        private bool closing;
        internal PickerTarget? Target { get; private set; }
        internal bool Inserting { get; set; }
        internal CortexIconLibraryPicker? Library { get; set; }

        internal InlinePicker(FrameworkElement root, ToggleButton anchor, Popup popup, Func<PickerTarget, bool, bool> available, Cerebral.PluginLifetime.PluginCleanup cleanup)
        {
            Root = root; this.anchor = anchor; this.popup = popup; this.available = available; this.cleanup = cleanup;
            monitor = new DispatcherTimer(DispatcherPriority.Background, root.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            monitor.Tick += (_, _) => { if (Target is not { } capture || !available(capture, !Inserting)) Close(); };
        }
        internal Task Open(PickerTarget capture, FrameworkElement body, Action? focus)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closed = completion; Target = capture; Inserting = false; popup.Child = body;
            owner = Window.GetWindow(Root);
            if (owner is not null)
            {
                owner.Closed += OwnerUnavailable; owner.Deactivated += OwnerUnavailable;
                owner.LocationChanged += OwnerUnavailable; owner.SizeChanged += OwnerResized;
            }
            cancellation = capture.Token.Register(() =>
            {
                if (!Root.Dispatcher.HasShutdownStarted)
                    Root.Dispatcher.BeginInvoke(() => { if (ReferenceEquals(Target, capture)) Close(); });
            });
            if (!available(capture, true)) { Close(); return completion.Task; }
            anchor.IsChecked = true; popup.IsOpen = true; monitor.Start();
            Root.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (ReferenceEquals(Target, capture) && popup.IsOpen) focus?.Invoke();
            });
            // Keeping the action alive also keeps the host's captured invocation/owner cancellation linked.
            return completion.Task;
        }
        private void OwnerUnavailable(object? sender, EventArgs args) => Close();
        private void OwnerResized(object sender, SizeChangedEventArgs args) => Close();
        internal void Close()
        {
            if (closing) return;
            closing = true;
            try
            {
                var completion = closed; closed = null;
                var previous = Target; Target = null;
                var previousOwner = owner; owner = null;
                var library = Library; Library = null;
                cleanup.Attempt(monitor.Stop);
                cleanup.Attempt(cancellation.Dispose); cancellation = default;
                if (previousOwner is not null)
                {
                    previousOwner.Closed -= OwnerUnavailable; previousOwner.Deactivated -= OwnerUnavailable;
                    previousOwner.LocationChanged -= OwnerUnavailable; previousOwner.SizeChanged -= OwnerResized;
                }
                cleanup.Attempt(() => popup.IsOpen = false);
                cleanup.Attempt(() => anchor.IsChecked = false);
                cleanup.Attempt(() => popup.Child = null);
                if (library is not null) cleanup.Attempt(library.Dispose);
                if (previous is not null) cleanup.Attempt(previous.Dispose);
                completion?.TrySetResult();
            }
            finally { closing = false; }
        }
    }

    private sealed class PickerTarget : IDisposable
    {
        private readonly CancellationTokenSource lifetime;
        private readonly string text;
        private readonly int start, length;
        private bool disposed;
        internal IDesktopTextEntry Entry { get; }
        internal DesktopConversationBinding Binding { get; }
        internal CancellationToken Token { get; }
        internal PickerTarget(IDesktopTextEntry entry, DesktopConversationBinding binding, CancellationToken cancellationToken)
        {
            Entry = entry; Binding = binding; text = entry.Text; start = entry.SelectionStart; length = entry.SelectionLength;
            lifetime = entry is IDesktopTextEntryLifetime available
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, available.AvailabilityToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = lifetime.Token;
        }
        internal bool Matches() => Entry.Text == text && Entry.SelectionStart == start && Entry.SelectionLength == length;
        public void Dispose() { if (disposed) return; disposed = true; try { lifetime.Cancel(); } finally { lifetime.Dispose(); } }
    }
}
