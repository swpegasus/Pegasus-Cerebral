using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Explicit native provider selection, four-result discovery, and exact selected-only hydration.</summary>
internal sealed class CortexIconLibraryPicker : IDisposable
{
    private readonly CortexIconLibrary library;
    private readonly IDesktopClipboardHost? clipboard;
    private readonly CortexIconCache? cache;
    private readonly CancellationToken lifetime;
    private readonly Func<string, Task> insertText;
    private readonly CortexIconSelection selection = new();
    private readonly CortexIconSelection search = new();
    private readonly ComboBox providers = new() { MinWidth = 220, DisplayMemberPath = nameof(CortexIconProvider.Id) };
    private readonly TextBox terms = new() { MaxLength = 256, MinWidth = 220, Margin = new Thickness(0, 6, 8, 6) };
    private readonly ListBox results = new() { MinHeight = 80, MaxHeight = 140, Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border artwork = new() { Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button next = new() { Content = "Next page", IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button insert = new() { Content = "Insert text", IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button copy = new() { Content = "Copy reference", IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button export = new() { Content = "Export unavailable · license bundle pending", IsEnabled = false,
        ToolTip = "Original bytes and license coordinates do not establish a complete required license/attribution bundle." };
    private CortexIconMetadata? selected;
    private string? selectedProvider;
    private string? nextUri;
    private bool activated;
    private bool disposed;
    public FrameworkElement View { get; }

    public CortexIconLibraryPicker(IDesktopServiceHost service, IDesktopClipboardHost? clipboard,
        string? dataDirectory, CancellationToken lifetime, Func<string, Task> insertText)
    {
        library = new(service); this.clipboard = clipboard; this.lifetime = lifetime; this.insertText = insertText;
        string? cacheError = null;
        if (dataDirectory is not null)
            try { cache = new(dataDirectory); }
            catch (Exception error) when (Recoverable(error)) { cacheError = "Selected icons cannot be saved locally for reuse. Library browsing remains available."; }
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = "Icon library", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Choose a library and search by name or meaning. Select an icon to see its details and available preview.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var providerRow = new WrapPanel();
        providerRow.Children.Add(providers);
        var refresh = new Button { Content = "Refresh providers", Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += async (_, _) => await RefreshProvidersAsync();
        providerRow.Children.Add(refresh); panel.Children.Add(providerRow);
        var query = new WrapPanel(); query.Children.Add(terms);
        var find = new Button { Content = "Search", Margin = new Thickness(0, 6, 0, 6) };
        find.Click += async (_, _) => await SearchAsync(null);
        next.Click += async (_, _) => await SearchAsync(nextUri);
        query.Children.Add(find); query.Children.Add(next); panel.Children.Add(query);
        AutomationProperties.SetName(providers, "Cortex native asset provider");
        AutomationProperties.SetName(terms, "Cortex asset search terms");
        AutomationProperties.SetName(results, "Four Cortex asset metadata results");
        AutomationProperties.SetName(status, "Cortex asset status");
        panel.Children.Add(results); panel.Children.Add(details); panel.Children.Add(artwork);
        var actions = new WrapPanel(); actions.Children.Add(insert); actions.Children.Add(copy); actions.Children.Add(export); panel.Children.Add(actions);
        ToolTipService.SetShowOnDisabled(export, true);
        panel.Children.Add(new TextBlock { Text = "Selected export is unavailable until the producer supplies the complete required license and attribution bundle.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(status);
        status.Text = cacheError ?? "Choose this tab to discover available native providers.";
        providers.SelectionChanged += (_, _) =>
        {
            search.Dispose(); ClearSelection(); nextUri = null; next.IsEnabled = false; results.ItemsSource = null;
            status.Text = "Choose search terms and press Search.";
        };
        results.SelectionChanged += async (_, _) =>
        {
            if (results.SelectedItem is CortexIconPreview preview && providers.SelectedItem is CortexIconProvider provider)
                await SelectAsync(provider.Id, preview);
        };
        insert.Click += async (_, _) => await RunSelectedAsync(async (metadata, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (metadata.Preview.Text.Length == 0) throw new InvalidDataException("This artwork has no text alternative.");
            await insertText(metadata.Preview.Text);
        });
        copy.Click += async (_, _) => await RunSelectedAsync(async (metadata, provider, token) =>
        {
            if (clipboard is null) return;
            var result = await clipboard.SetClipboardTextAsync("provider=" + provider + "\n" + metadata.Reference, token);
            token.ThrowIfCancellationRequested();
            if (result.State is not (DesktopOperationState.Completed or DesktopOperationState.Accepted))
                throw new IOException(result.Message ?? "Reference was not copied.");
            status.Text = "Copied exact asset and semantic reference.";
        });
        View = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        View.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
    }

    public async Task ActivateAsync()
    {
        if (activated || disposed) return;
        activated = true; await RefreshProvidersAsync();
    }

    private async Task RefreshProvidersAsync()
    {
        ClearSelection(); search.Dispose(); results.ItemsSource = null; nextUri = null; next.IsEnabled = false;
        providers.ItemsSource = null; providers.IsEnabled = false;
        var operation = search.Begin(lifetime);
        try
        {
            status.Text = "Discovering native providers…";
            var items = await library.ProvidersAsync(operation.Token);
            if (!search.IsCurrent(operation.Generation)) return;
            providers.ItemsSource = items; providers.SelectedIndex = -1;
            status.Text = items.Count == 0 ? "No enabled native Cortex asset provider is available. Local icons remain available." :
                "Select a provider explicitly, then search " + CortexIconLibrary.Namespace + ".";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Recoverable(error))
        { if (search.IsCurrent(operation.Generation)) status.Text = "Cortex library unavailable: " + error.Message; }
        finally { if (!disposed) providers.IsEnabled = true; }
    }

    private async Task SearchAsync(string? pageUri)
    {
        if (providers.SelectedItem is not CortexIconProvider provider)
        { status.Text = "Choose an available native provider first."; return; }
        ClearSelection(); results.ItemsSource = null; nextUri = null; next.IsEnabled = false;
        var operation = search.Begin(lifetime);
        try
        {
            var page = await library.SearchAsync(provider.Id, pageUri ?? CortexIconLibrary.SearchUri(terms.Text), operation.Token);
            if (!search.IsCurrent(operation.Generation)) return;
            results.ItemsSource = page.Items; nextUri = page.NextUri; next.IsEnabled = nextUri is not null;
            status.Text = page.Items.Count + " metadata results. Select one for exact metadata and verified content.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Recoverable(error))
        { if (search.IsCurrent(operation.Generation)) status.Text = "Search unavailable; refresh the search if its cursor changed: " + error.Message; }
    }

    private async Task SelectAsync(string provider, CortexIconPreview preview)
    {
        ClearSelection();
        var operation = selection.Begin(lifetime);
        try
        {
            status.Text = "Reading selected exact metadata…";
            var metadata = await library.MetadataAsync(provider, preview, operation.Token);
            if (!selection.IsCurrent(operation.Generation)) return;
            selected = metadata; selectedProvider = provider;
            details.Text = metadata.Preview.Title + "\n" + metadata.Preview.Meaning +
                "\nLicense: " + (metadata.License.Length == 0 ? "not supplied" : metadata.License) +
                "\nSource: " + (metadata.Source.Length == 0 ? "not supplied" : metadata.Source) +
                "\n" + metadata.Preview.MetadataUri +
                (metadata.DetailsRef.Length == 0 ? "" : "\nLicense/provenance: cortex://" + metadata.DetailsRef);
            insert.IsEnabled = metadata.Preview.Text.Length != 0; copy.IsEnabled = clipboard is not null;
            if (metadata.Preview.Size is < 1 or > CortexIconLibrary.MaximumAssetBytes)
            {
                status.Text = "Exact metadata available. Original content exceeds the 2 MiB consumer limit or is empty; preview/export disabled.";
                return;
            }
            var cacheNote = "";
            byte[]? bytes = null;
            if (cache is not null)
                try { bytes = await cache.ReadAsync(provider, metadata, operation.Token); }
                catch (Exception error) when (Recoverable(error)) { cacheNote = " Cache unavailable: " + error.Message; }
            if (bytes is null)
            {
                bytes = await library.BytesAsync(provider, metadata, operation.Token);
                if (!selection.IsCurrent(operation.Generation)) return;
                if (cache is not null && cacheNote.Length == 0)
                    try { await cache.SaveAsync(provider, metadata, bytes, operation.Token); }
                    catch (Exception error) when (Recoverable(error)) { cacheNote = " Cache unavailable: " + error.Message; }
            }
            if (!selection.IsCurrent(operation.Generation)) return;
            var image = CortexIconContent.Preview(metadata, bytes);
            if (image is not null)
            {
                var control = new Image { Source = image, Width = 96, Height = 96, Stretch = Stretch.Uniform,
                    ToolTip = metadata.Preview.Meaning };
                AutomationProperties.SetName(control, metadata.Preview.Title); artwork.Child = control;
                status.Text = "Selected original bytes verified; bounded raster preview available." + cacheNote;
            }
            else status.Text = (metadata.Preview.MimeType == "image/svg+xml" ?
                "Selected SVG bytes verified. Visual preview requires a reviewed SVG renderer; semantic meaning and exact resource references are available." :
                "Selected original bytes verified. This media type has no admitted visual preview; semantic meaning and references are available.") + cacheNote;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Recoverable(error))
        {
            if (!selection.IsCurrent(operation.Generation)) return;
            status.Text = "Selected resource/preview unavailable: " + error.Message;
        }
    }

    private async Task RunSelectedAsync(Func<CortexIconMetadata, string, CancellationToken, Task> action)
    {
        if (selected is not { } metadata || selectedProvider is not { } provider || disposed) return;
        // The UI handler snapshots a fresh selected identity, and provider/conversation changes cancel its epoch.
        try
        {
            var token = selection.Token;
            token.ThrowIfCancellationRequested();
            await action(metadata, provider, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Recoverable(error))
        { if (!disposed && ReferenceEquals(selected, metadata) && selectedProvider == provider) status.Text = error.Message; }
    }

    private void ClearSelection()
    {
        selection.Dispose(); selected = null; selectedProvider = null;
        insert.IsEnabled = false; copy.IsEnabled = false; export.IsEnabled = false; details.Text = ""; artwork.Child = null;
    }
    private static bool Recoverable(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or
        FormatException or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException or System.Runtime.InteropServices.ExternalException;
    public void Dispose() { if (disposed) return; disposed = true; search.Dispose(); selection.Dispose(); }
}
