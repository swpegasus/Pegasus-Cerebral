using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.AppearanceThemePlugin;

/// <summary>Data-only appearance configuration through the existing authenticated owner command bridge.</summary>
public sealed class AppearanceThemePlugin : IDesktopPlugin, IDesktopConfigurationPlugin
{
    private readonly CancellationTokenSource lifetime = new();
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private bool disposed;
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("appearance", "Appearance", CreatePanel)];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    public void Initialize(IDesktopPluginHost pluginHost)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        host = pluginHost;
        service = pluginHost as IDesktopServiceHost;
    }

    private FrameworkElement CreatePanel()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var panel = new StackPanel { Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(Text("Appearance", 22));
        panel.Children.Add(Text("Color presets apply to all shared desktop controls. Keep using System, Light or Dark in the normal appearance settings; each preset supplies both palettes.", 14, true));
        var selected = Text("Loading the current preset…", 16);
        AutomationProperties.SetAutomationId(selected, "Appearance.SelectedPreset");
        panel.Children.Add(selected);
        var status = Text("", 13, true);
        AutomationProperties.SetAutomationId(status, "Appearance.Status");
        var candidateLabel = Text("Import a portable theme JSON file to preview it before applying.", 14, true);
        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 12) };
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var import = Button("Import preset…", "Appearance.Import");
        var apply = Button("Apply imported preset", "Appearance.Apply");
        var export = Button("Export current preset…", "Appearance.Export");
        var reset = Button("Restore Blue White", "Appearance.Reset");
        var refresh = Button("Refresh", "Appearance.Refresh");
        foreach (var button in new[] { import, apply, export, reset, refresh }) actions.Children.Add(button);
        panel.Children.Add(actions); panel.Children.Add(candidateLabel); panel.Children.Add(swatches); panel.Children.Add(status);
        var viewLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ThemeSelection? current = null;
        JsonElement? imported = null;
        bool busy = false, loaded = false, viewClosed = false;
        void SetAvailability()
        {
            var available = !busy && service is not null && !viewLifetime.IsCancellationRequested;
            import.IsEnabled = export.IsEnabled = refresh.IsEnabled = available;
            reset.IsEnabled = available && current is not null;
            apply.IsEnabled = available && current is not null && imported is not null;
        }
        void Show(ThemeSelection value)
        {
            current = value;
            selected.Text = "Current preset: " + value.Preset.GetProperty("name").GetString();
            if (imported is null) ShowSwatches(swatches, value.Preset);
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (busy || viewLifetime.IsCancellationRequested) return;
            busy = true; SetAvailability();
            try { await action(viewLifetime.Token); }
            catch (OperationCanceledException) when (viewLifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                status.Text = "Appearance change was not confirmed: " + error.Message;
                host?.ReportStatus(status.Text);
            }
            finally { busy = false; SetAvailability(); }
        }
        async Task Refresh(CancellationToken token)
        {
            Show(await GetAsync(token)); status.Text = "Current preset loaded.";
        }
        import.Click += async (_, _) => await Run(async token =>
        {
            var dialog = new OpenFileDialog { Title = "Import appearance preset", Filter = "Cerebral themes (*.CerebralTheme;*.json)|*.CerebralTheme;*.json", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            imported = await PortableThemeFile.ReadAsync(dialog.FileName, token);
            candidateLabel.Text = "Imported: " + DisplayName(imported.Value) + ". Apply to validate and select it.";
            ShowSwatches(swatches, imported.Value);
            status.Text = "Preview only. The saved preset is unchanged.";
        });
        apply.Click += async (_, _) => await Run(async token =>
        {
            if (current is null || imported is null) return;
            var accepted = await SetAsync(current.Revision, imported.Value, token);
            imported = null; candidateLabel.Text = "Imported preset selected.";
            Show(accepted); status.Text = "Preset applied.";
        });
        reset.Click += async (_, _) => await Run(async token =>
        {
            if (current is null) return;
            var accepted = await SetAsync(current.Revision, null, token);
            imported = null; candidateLabel.Text = "Blue White is the built-in fallback preset.";
            Show(accepted); status.Text = "Blue White restored.";
        });
        refresh.Click += async (_, _) => await Run(Refresh);
        export.Click += async (_, _) => await Run(async token =>
        {
            // Read effective state again, so another window's change is included in the export.
            var value = await GetAsync(token); Show(value);
            var dialog = new SaveFileDialog { Title = "Export appearance preset", Filter = "Cerebral theme (*.CerebralTheme)|*.CerebralTheme|JSON (*.json)|*.json", FileName = DisplayName(value.Preset) + ".CerebralTheme", DefaultExt = ".CerebralTheme", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog() != true) return;
            await PortableThemeFile.WriteAsync(dialog.FileName, value.Preset, token);
            status.Text = "Current preset exported.";
        });
        panel.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            await Run(Refresh);
        };
        panel.Unloaded += (_, _) =>
        {
            if (viewClosed) return;
            viewClosed = true;
            viewLifetime.Cancel(); viewLifetime.Dispose();
        };
        SetAvailability();
        if (service is null) status.Text = "This host does not offer the appearance owner command bridge.";
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private async Task<ThemeSelection> GetAsync(CancellationToken token) =>
        ReadState(await CommandAsync("interface.theme.get", new { }, token));

    private async Task<ThemeSelection> SetAsync(long expectedRevision, JsonElement? preset, CancellationToken token) =>
        ReadState(await CommandAsync("interface.theme.set", new { expectedRevision, preset }, token));

    private async Task<JsonElement> CommandAsync(string operation, object arguments, CancellationToken token)
    {
        var target = service ?? throw new InvalidOperationException("The appearance owner command bridge is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var result = await target.ExecuteServiceCommandAsync(operation, arguments, timeout.Token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The appearance command failed.");
        return result.Data;
    }

    private static ThemeSelection ReadState(JsonElement data)
    {
        var revision = data.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("The appearance revision is invalid.");
        var preset = data.GetProperty("preset").Clone();
        if (preset.ValueKind != JsonValueKind.Object || !preset.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 100 } ||
            Encoding.UTF8.GetByteCount(preset.GetRawText()) > PortableThemeFile.MaximumBytes)
            throw new InvalidDataException("The Worker returned an invalid appearance preset.");
        return new(revision, preset);
    }

    private static string DisplayName(JsonElement preset) =>
        preset.ValueKind == JsonValueKind.Object && preset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? (name.GetString() ?? "Unnamed preset")[..Math.Min(name.GetString()?.Length ?? 0, 100)] : "Unvalidated preset";

    private static void ShowSwatches(Panel panel, JsonElement preset)
    {
        panel.Children.Clear();
        if (preset.ValueKind != JsonValueKind.Object) return;
        foreach (var mode in new[] { "light", "dark" })
        {
            if (!preset.TryGetProperty(mode, out var palette) || palette.ValueKind != JsonValueKind.Object) continue;
            foreach (var token in new[] { "window", "surface", "ink", "accent", "selection" })
            {
                if (!palette.TryGetProperty(token, out var color) || color.ValueKind != JsonValueKind.String ||
                    color.GetString() is not { Length: 7 } value || value[0] != '#' || value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")) continue;
                var item = new StackPanel { Margin = new Thickness(0, 0, 10, 4), Width = 80 };
                item.Children.Add(new Border { Height = 24, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)), CornerRadius = new CornerRadius(5) });
                item.Children.Add(Text(mode + " " + token, 11, true));
                panel.Children.Add(item);
            }
        }
    }

    private static TextBlock Text(string text, double size, bool muted = false)
    {
        var view = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        view.SetResourceReference(TextBlock.ForegroundProperty, muted ? DesktopThemeResourceKeys.MutedTextBrush : DesktopThemeResourceKeys.TextBrush);
        return view;
    }
    private static Button Button(string text, string id)
    {
        var view = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8), MinHeight = 34 };
        AutomationProperties.SetAutomationId(view, id);
        return view;
    }
    private sealed record ThemeSelection(long Revision, JsonElement Preset);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); lifetime.Dispose(); host = null; service = null;
    }
}

/// <summary>Bounded portable data files. The Worker owns strict semantic/schema/contrast validation at apply.</summary>
public static class PortableThemeFile
{
    public const int MaximumBytes = 16 * 1024;
    public static async Task<JsonElement> ReadAsync(string path, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Theme files must contain at most 16 KiB of JSON.");
        var buffer = new byte[MaximumBytes + 1];
        var count = 0;
        while (count <= MaximumBytes)
        {
            var read = await file.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
            if (count > MaximumBytes) throw new InvalidDataException("The theme file grew beyond 16 KiB.");
        }
        using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A theme file must be a JSON object.");
        return document.RootElement.Clone();
    }
    public static async Task WriteAsync(string path, JsonElement preset, CancellationToken token = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
        if (preset.ValueKind != JsonValueKind.Object || bytes.Length > MaximumBytes) throw new InvalidDataException("The exported theme is not bounded JSON data.");
        // A same-directory temporary file keeps a cancelled export from replacing the user's previous file.
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
