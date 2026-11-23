using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Optional chat adapter. Assets and their portable catalog remain independent of the desktop SDK.</summary>
public sealed partial class ChatIconsPlugin : DesktopPluginBase, IDesktopTextEntryPlugin, IDesktopConfigurationPlugin, IDesktopMountPlugin
{
    public const string PluginId = "pegasus.chat-icons";
    public const string PluginName = "Chat Icons";
    public const string PanelMessage = "Chat Icons loaded";
    private Window? picker;
    private PickerTarget? pickerTarget;
    private DesktopProductPaths? productPaths;
    private string? packDirectory;
    private string? settingsPath;
    private string? dataDirectory;
    private bool disposed;

    public IReadOnlyList<DesktopTextEntryAction> TextEntryActions =>
        [new("icons", "Icons", OpenPickerAsync, "Choose a symbol to insert at the cursor, or copy a portable icon reference.")];

    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("chat-icons", "Chat Icons", CreateConfigurationView)];

    public override void Initialize(IDesktopPluginHost pluginHost)
    {
        base.Initialize(pluginHost);
        productPaths = (pluginHost as IDesktopProductEnvironment)?.ProductPaths;
        packDirectory = ResolvePackDirectory(productPaths, (pluginHost as IDesktopPluginEnvironment)?.PackageDirectory);
        if (pluginHost is IDesktopPluginEnvironment environment)
        {
            dataDirectory = environment.DataDirectory;
            settingsPath = Path.Combine(environment.DataDirectory, RoleSettings.FileName);
        }
        Host.ConversationChanged += ConversationChanged;
        ConnectSettings(pluginHost);
    }

    /// <summary>Fresh decoration only; message IDs, original text, role, history and model input remain host-owned.</summary>
    public override DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message)
    {
        if (CallbacksRetired) return null;
        var meaning = message.Role.ToLowerInvariant() switch
        {
            "user" => "Message",
            "assistant" => "Assistant",
            "tool" => "Tool",
            _ => null
        };
        if (meaning is null) return null;
        var key = LoadMeaningMappings()[meaning][(PluginId.Length + 1)..];
        var icon = DesktopIcons.Create(key, 16, productPaths);
        icon.Margin = new Thickness(0, 2, 8, 2);
        return new(Decoration: icon);
    }

    public static string? ResolvePackDirectory(DesktopProductPaths? paths, string? package)
    {
        var candidates = new List<string>();
        if (paths is not null)
        {
            candidates.Add(Path.Combine(paths.SharedDirectory, "Icons", "chat-v1"));
            candidates.Add(Path.Combine(paths.InstallDirectory, "Resources", "Icons", "chat-v1"));
        }
        if (package is not null) candidates.Add(Path.Combine(package, "Resources", "Icons", "chat-v1"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "chat-v1"));
        return candidates.Where(Path.IsPathFullyQualified)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "catalog.ini")));
    }

    private async Task OpenPickerAsync(IDesktopTextEntry target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(CallbacksRetired, this);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshCentralSettingsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(CallbacksRetired, this);
        if (target.Id == "composer")
        {
            if (OpenInlinePicker(target, cancellationToken) is { } popupClosed) await popupClosed;
            else ReportHostStatus("The Symbols accessory is no longer available. Reopen Icons after it is mounted.");
            return;
        }
        ClosePicker();
        var binding = Host.GetConversation().Binding;
        CloseInlinePickers();
        var capture = new PickerTarget(target, binding, cancellationToken);
        pickerTarget = capture;
        var token = capture.Token;
        var inserting = false;
        var window = new Window
        {
            Title = "Chat Icons", Width = 680, Height = 590, MinWidth = 540, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
        };
        // This independent utility window uses a matching OS foreground/background pair, including high contrast.
        window.SetResourceReference(Control.BackgroundProperty, SystemColors.WindowBrushKey);
        window.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
        if (Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        picker = window;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new TextBlock
        {
            Text = "Choose a symbol for this text field",
            FontSize = 20, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var hint = new TextBlock
        {
            Text = "Insert replaces the current selection. SVG artwork stays a project asset; the composer receives only the shown Unicode symbol. Nothing is sent automatically.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(status, "Icon picker status");
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status);
        var close = new Button { Content = "Close", MinWidth = 80, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        Task Insert(ChatIconAsset asset) => InsertText(asset.Text, asset.Label);
        async Task InsertText(string text, string label)
        {
            if (inserting || !ReferenceEquals(pickerTarget, capture)) return;
            if (!CaptureAvailable(capture)) { window.Close(); return; }
            inserting = true; window.IsEnabled = false;
            try
            {
                var result = await InsertAssetAsync(Host, target, binding, text, token);
                status.Text = result.Message ?? (IsSuccess(result) ? "Inserted " + label + "." : "Insertion did not complete.");
                if (IsSuccess(result)) window.Close();
            }
            catch (OperationCanceledException) { status.Text = "This text field is no longer available."; }
            catch (Exception error) { status.Text = "Could not insert: " + error.Message; }
            finally { window.IsEnabled = true; inserting = false; }
        }
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        CortexIconLibraryPicker? libraryPicker = null;
        if (Host is IDesktopServiceHost service)
        {
            libraryPicker = new(service, Host as IDesktopClipboardHost, dataDirectory, token,
                text => InsertText(text, "selected Cortex symbol"));
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Local icons", Content = scroll });
            var libraryTab = new TabItem { Header = "Cortex library", Content = libraryPicker.View };
            tabs.Items.Add(libraryTab);
            tabs.SelectionChanged += async (_, args) =>
            {
                if (ReferenceEquals(args.Source, tabs) && ReferenceEquals(tabs.SelectedItem, libraryTab))
                {
                    status.Visibility = Visibility.Collapsed; hint.Visibility = Visibility.Collapsed;
                    await libraryPicker.ActivateAsync();
                }
                else if (ReferenceEquals(args.Source, tabs))
                { status.Visibility = Visibility.Visible; hint.Visibility = Visibility.Visible; }
            };
            layout.Children.Add(tabs);
        }
        else layout.Children.Add(scroll);
        window.Content = layout;

        try
        {
            if (packDirectory is null) throw new FileNotFoundException("The portable icon catalog is missing. The embedded message icons remain available.");
            var pack = AssetPack.Load(packDirectory);
            ValidatePickerPack(pack);
            var roleRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var (role, key) in LoadRoles())
            {
                var asset = pack.Find(key);
                var roleButton = new Button { Content = role + "  " + asset.Text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 4, 8, 4), ToolTip = asset.Key };
                AutomationProperties.SetName(roleButton, "Insert " + role + " symbol");
                roleButton.Click += async (_, _) => await Insert(asset);
                roleRow.Children.Add(roleButton);
            }
            list.Children.Add(roleRow);
            async Task CopyReference(ChatIconAsset asset)
            {
                try
                {
                    if (Host is not IDesktopClipboardHost clipboard) return;
                    var result = await clipboard.SetClipboardTextAsync(pack.Reference(asset), token);
                    status.Text = result.Message ?? (IsSuccess(result) ? "Copied the portable icon reference." : "Reference was not copied.");
                }
                catch (OperationCanceledException) { status.Text = "Copy cancelled."; }
                catch (Exception error) { status.Text = "Could not copy reference: " + error.Message; }
            }
            list.Children.Add(new NativeIconPicker(pack.Assets, Insert, CreatePreview,
                Host is IDesktopClipboardHost ? CopyReference : null));
            status.Text = "MIT licensed Fluent System Icons. Licensing accompanies project exports.";
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or System.Xml.XmlException or KeyNotFoundException)
        {
            status.Text = "Icon catalog unavailable: " + error.Message;
            ReportHostStatus(status.Text);
        }
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        monitor.Tick += (_, _) => { if (!CaptureAvailable(capture, !inserting)) window.Close(); };
        var cancellation = token.Register(() =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(() => { if (ReferenceEquals(pickerTarget, capture)) window.Close(); });
        });
        window.Closed += (_, _) =>
        {
            monitor.Stop(); cancellation.Dispose();
            try { libraryPicker?.Dispose(); }
            finally
            {
                capture.Dispose();
                if (picker == window) { picker = null; pickerTarget = null; }
                closed.TrySetResult();
            }
        };
        try { window.Show(); monitor.Start(); await closed.Task; }
        finally { if (ReferenceEquals(pickerTarget, capture)) ClosePicker(); }
    }

    public static void ValidatePickerPack(AssetPack pack)
    {
        if (pack.Id != PluginId || DesktopIcons.Definitions.Any(icon =>
            !pack.Assets.Any(asset => asset.Key == PluginId + "." + icon.Key && asset.Alias == icon.Key)))
            throw new InvalidDataException("The chat picker requires its complete eleven-icon semantic catalog; individual exports remain usable project assets.");
    }

    private IReadOnlyDictionary<string, string> LoadRoles()
    {
        if (presentationSettings.Current is { } shared) return shared.Roles;
        if (settingsPath is null) return RoleSettings.Defaults;
        try { return RoleSettings.Load(settingsPath); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportHostStatus("Chat Icons uses default role symbols because settings could not be read: " + error.Message);
            return RoleSettings.Defaults;
        }
    }

    private FrameworkElement CreateConfigurationView() =>
        new ChatIconsSettingsEditor(settingsClient, PrepareExistingLocalChoices,
            SetCentralPreferences, settingsLifetime.Token,
            Host is IDesktopClipboardHost ? CopyMeaningReferencesAsync : null, SettingsChoiceGlyphs()).View;

    private FrameworkElement CreatePreview(string alias)
    {
        if (DesktopIcons.Definitions.Any(icon => icon.Key == alias))
        {
            var icon = DesktopIcons.Create(alias, 24, productPaths);
            // Standalone picker matches the OS utility-window palette; chat decorations keep the host brush.
            icon.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
            return icon;
        }
        return new TextBlock { Text = "◇", FontSize = 24, ToolTip = "Portable icon preview unavailable; the asset reference remains usable." };
    }

    public static async Task<DesktopOperationResult> InsertAssetAsync(IDesktopPluginHost host, IDesktopTextEntry target,
        DesktopConversationBinding binding, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host.GetConversation().Binding != binding)
            return new(DesktopOperationState.Rejected, "Conversation changed; open the picker in the current text field.");
        if (target is IDesktopTextEntryLifetime lifetime && lifetime.AvailabilityToken.IsCancellationRequested)
            return new(DesktopOperationState.Rejected, "This text field is no longer available.");
        // The host applies current selection, limits and stale-entry checks. No draft replacement or submission is synthesized.
        var result = await target.InsertAsync(text, cancellationToken);
        if (IsSuccess(result) && target is IDesktopTextEntryFocus focus)
        {
            // Focus is advisory after an insertion has committed; never report that edit as failed or replay it.
            try { await focus.FocusAsync(cancellationToken); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return result with { Message = "Symbol inserted; text-field focus was not restored." }; }
        }
        return result;
    }

    private static bool IsSuccess(DesktopOperationResult result) => result.State is DesktopOperationState.Completed or DesktopOperationState.Accepted;
    private void ConversationChanged(object? sender, DesktopConversationChangedEventArgs args) { ClosePicker(); CloseInlinePickers(); }
    private void ClosePicker() { picker?.Close(); pickerTarget?.Dispose(); pickerTarget = null; }
    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cleanup.Attempt(() => Host.ConversationChanged -= ConversationChanged);
        cleanup.Attempt(() => settingsLifetime.Cancel());
        cleanup.Attempt(ClosePicker); cleanup.Attempt(CloseInlinePickers);
        cleanup.Attempt(settingsLifetime.Dispose);
        cleanup.Attempt(() => base.Dispose());
        cleanup.Complete();
    }
}
