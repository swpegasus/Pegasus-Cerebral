using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PersonalityPlugin;
public sealed partial class PersonalityPlugin : DesktopPluginBase, IDesktopConfigurationPlugin, IDesktopMountPlugin
{
    public const string PluginId = "pegasus.personality";
    private IDesktopServiceHost? service;
    private IDesktopPluginHost? ownerHost;
    private PersonalitySettingsStore? store;
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("personality-presets", "Personality", CreateConfigurationView)];

    public override void Initialize(IDesktopPluginHost pluginHost)
    {
        base.Initialize(pluginHost);
        ownerHost = pluginHost;
        service = pluginHost as IDesktopServiceHost;
        if (pluginHost is IDesktopPluginEnvironment environment)
            store = new(environment.DataDirectory);
    }

    private FrameworkElement CreateConfigurationView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var title = Text("Personality preset", 22);
        var explanation = Text("Manage shared custom styles and the shared default. Choose the personality for each chat from Main’s Personality selector. Templates here fill a custom draft; Save is explicit. Accepted work keeps its saved selection, and optional style may be omitted for context space.", 13);
        var enabled = new CheckBox
        {
            Content = "Enable this shared default where supported",
            Margin = new Thickness(0, 4, 0, 10),
            IsEnabled = false
        };
        var selector = new ComboBox
        {
            DisplayMemberPath = nameof(PersonalityOwnerPreset.Name),
            SelectedValuePath = nameof(PersonalityOwnerPreset.Id),
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 10),
            IsEnabled = false
        };
        var templateSelector = new ComboBox
        {
            DisplayMemberPath = nameof(PersonalityTemplate.Name),
            SelectedValuePath = nameof(PersonalityTemplate.PackageKey),
            ItemsSource = PersonalityTemplates.All,
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 8),
            IsEnabled = false,
            ToolTip = "Choose a template to fill a custom draft, then Save. Existing saved styles are preserved."
        };
        var savedLabel = new Label { Content = "Saved style", Target = selector, Padding = new Thickness(0, 0, 0, 4) };
        var templateLabel = new Label { Content = "Start from a template", Target = templateSelector, Padding = new Thickness(0, 0, 0, 4) };
        var capacity = Text("", 12, DesktopThemeResourceKeys.MutedTextBrush);
        AutomationProperties.SetName(selector, "Saved personality style");
        AutomationProperties.SetName(templateSelector, "Start a personality draft from a template");
        AutomationProperties.SetName(capacity, "Personality style capacity");
        var name = new TextBox
        {
            MaxLength = 80,
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 8),
            IsEnabled = false
        };
        var preview = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 100,
            MaxHeight = 280,
            MaxLength = 4096,
            IsEnabled = false,
            Margin = new Thickness(0, 0, 0, 5)
        };
        var size = Text("", 12, DesktopThemeResourceKeys.MutedTextBrush);
        Button Action(string label) => new()
        {
            Content = label,
            MinWidth = 84,
            IsEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 5, 10, 5)
        };
        var add = Action("New preset");
        var save = Action("Save");
        var cancel = Action("Reload / discard");
        var status = Text("", 12, DesktopThemeResourceKeys.MutedTextBrush);
        status.Margin = new Thickness(0, 10, 0, 0);
        var buttons = new WrapPanel();
        foreach (var item in new[]
        {
            add,
            save,
            cancel
        }

        )
            buttons.Children.Add(item);
        foreach (var pair in new (FrameworkElement View, string Id)[]
        {
            (enabled, "PersonalityEnabled"),
            (selector, "PersonalityPreset"),
            (templateSelector, "PersonalityTemplate"),
            (capacity, "PersonalityCapacity"),
            (name, "PersonalityName"),
            (preview, "PersonalityPreview"),
            (size, "PersonalitySize"),
            (add, "PersonalityNew"),
            (save, "PersonalitySave"),
            (cancel, "PersonalityCancel"),
            (status, "PersonalityStatus")
        }

        )
            AutomationProperties.SetAutomationId(pair.View, pair.Id);
        foreach (var item in new FrameworkElement[]
        {
            title,
            explanation,
            enabled,
            savedLabel,
            selector,
            templateLabel,
            templateSelector,
            capacity,
            Text("Style name", 13),
            name,
            Text("Style text", 13),
            preview,
            size,
            buttons,
            status
        }

        )
            panel.Children.Add(item);
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        PersonalityEditorSnapshot? snapshot = null;
        CancellationTokenSource? lifetime = null;
        int generation = 0;
        bool loading = false;
        PersonalityEditor Editor() => new(service!, store!, () => ownerHost!.GetConversation().Binding);
        void Status(string text, bool error = false)
        {
            status.Text = Bounded(text);
            status.SetResourceReference(TextBlock.ForegroundProperty, error ? DesktopThemeResourceKeys.ErrorTextBrush : DesktopThemeResourceKeys.MutedTextBrush);
        }

        void Ready(bool ready)
        {
            enabled.IsEnabled = selector.IsEnabled = name.IsEnabled = preview.IsEnabled = save.IsEnabled = ready;
            templateSelector.IsEnabled = ready && snapshot is not null;
            var blocked = NewDraftBlock();
            add.IsEnabled = ready && snapshot is not null && blocked is null;
            add.ToolTip = blocked ?? "Create a new custom preset from the current wording.";
            capacity.Text = snapshot is null ? "" :
                $"Saved custom styles: {snapshot.Settings.CustomPresets.Count} / 8. Prepared custom styles: {snapshot.Sources.CustomPresetCount} / 8. Retained versions: {snapshot.Sources.RetainedCustomVersions} / 32. No styles or versions are removed automatically.";
            cancel.IsEnabled = lifetime is not null && !disposed;
        }

        string? NewDraftBlock()
        {
            if (snapshot is null) return "Reload saved styles before creating a draft.";
            if (snapshot.Settings.CustomPresets.Count >= 8)
                return "Eight custom presets are saved. Choose an existing custom preset to edit it.";
            if (snapshot.Sources.CustomPresetCount >= 8)
                return "Eight prepared custom styles are retained. A new style cannot be prepared; existing styles were preserved.";
            if (snapshot.Sources.RetainedCustomVersions >= 32)
                return "The 32 retained style versions are full. A new style cannot be prepared; existing versions were preserved.";
            return null;
        }

        void Size()
        {
            size.Text = $"{preview.Text.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries).Length} words · {System.Text.Encoding.UTF8.GetByteCount(preview.Text)} / 1024 UTF-8 bytes";
        }

        void Present()
        {
            if (selector.SelectedItem is not PersonalityOwnerPreset preset)
                return;
            loading = true;
            name.Text = preset.Name;
            preview.Text = preset.Text;
            name.IsReadOnly = preview.IsReadOnly = preset.ReadOnly;
            Size();
            loading = false;
        }

        async Task Reload(string completion)
        {
            if (lifetime is null)
                return;
            var token = lifetime.Token;
            var version = ++generation;
            snapshot = null;
            Ready(false);
            Status("Loading saved styles…");
            try
            {
                if (service is null || store is null || ownerHost is null)
                    throw new NotSupportedException("This host does not expose the personality owner service and plugin data.");
                var loaded = await Editor().LoadAsync(token);
                if (version != generation || token.IsCancellationRequested)
                    return;
                snapshot = loaded;
                loading = true;
                selector.ItemsSource = loaded.Presets;
                selector.SelectedValue = loaded.SelectedPresetId;
                enabled.IsChecked = loaded.Enabled;
                loading = false;
                Present();
                Ready(true);
                Status(PersonalityInstructionLayer.HasLayer(loaded.Lead.Content)
                    ? "A previous style section is present. Save explicitly migrates that owned section; other lead instructions and the previous local selection are preserved. " + loaded.LegacyNotice
                    : loaded.LegacyNotice ?? completion);
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)when (error is not OutOfMemoryException)
            {
                if (version != generation)
                    return;
                loading = false;
                snapshot = null;
                Ready(false);
                Status(error.Message, true);
            }
        }

        void Dirty()
        {
            if (!loading)
            {
                Size();
                Status("Unsaved shared style. Save preserves this definition and shared default; choose it separately in each Main chat. Accepted work keeps its saved selection.");
            }
        }

        selector.SelectionChanged += (_, _) =>
        {
            if (!loading)
            {
                Present();
                Dirty();
            }
        };
        enabled.Checked += (_, _) => Dirty();
        enabled.Unchecked += (_, _) => Dirty();
        name.TextChanged += (_, _) => Dirty();
        preview.TextChanged += (_, _) => Dirty();
        add.Click += (_, _) =>
        {
            if (snapshot is null)
                return;
            if (NewDraftBlock() is { } blocked)
            {
                Status(blocked, true);
                return;
            }
            var custom = new PersonalityOwnerPreset("style-" + Guid.NewGuid().ToString("N"), "Custom style", preview.Text, 0, "", false);
            loading = true;
            selector.ItemsSource = snapshot.Presets.Append(custom).ToArray();
            selector.SelectedValue = custom.Id;
            loading = false;
            Present();
            Dirty();
            name.Focus();
            name.SelectAll();
        };
        templateSelector.SelectionChanged += (_, _) =>
        {
            if (loading || !templateSelector.IsEnabled || templateSelector.SelectedItem is not PersonalityTemplate template)
                return;
            try
            {
                var baseline = snapshot;
                if (baseline is null || lifetime is null || lifetime.IsCancellationRequested || disposed)
                    return;
                if (ownerHost is null || ownerHost.GetConversation().Binding != baseline.Owner)
                {
                    Status("The configuration owner changed. Reload before choosing a template; the current draft was preserved.", true);
                    return;
                }
                var matches = baseline.Settings.CustomPresets.Where(value =>
                    value.Name == template.Name && value.Text == template.Text).ToArray();
                var selectedId = selector.SelectedValue as string;
                var definition = matches.SingleOrDefault(value => value.Id == selectedId);
                if (definition is null && matches.Length > 1)
                {
                    Status("More than one saved style exactly matches this template. Choose a matching saved style explicitly; the current selection and draft were preserved.", true);
                    return;
                }
                definition ??= matches.SingleOrDefault();
                PersonalityOwnerPreset chosen;
                if (definition is not null)
                    chosen = baseline.Presets.Single(value => value.Id == definition.Id && !value.ReadOnly);
                else
                {
                    // Re-selecting the current unsaved template keeps its one draft identity.
                    if (selector.SelectedItem is PersonalityOwnerPreset current &&
                        !baseline.Settings.CustomPresets.Any(value => value.Id == current.Id) &&
                        current.Name == template.Name && current.Text == template.Text &&
                        name.Text == template.Name && preview.Text == template.Text)
                    {
                        Status("This template is already an unsaved draft. Save explicitly to keep it for the next new turn.");
                        return;
                    }
                    if (NewDraftBlock() is { } blocked)
                    {
                        Status(blocked, true);
                        return;
                    }
                    chosen = new("style-" + Guid.NewGuid().ToString("N"), template.Name, template.Text, 0, "", false);
                }
                loading = true;
                selector.ItemsSource = definition is null ? baseline.Presets.Append(chosen).ToArray() : baseline.Presets;
                selector.SelectedValue = chosen.Id;
                loading = false;
                Present();
                Status(definition is null
                    ? "Template filled an unsaved custom draft. Enablement is unchanged. Save explicitly to keep it for the next new turn."
                    : "An exact saved custom style is selected; no copy was created. Save explicitly to choose it for the next new turn.");
            }
            finally
            {
                loading = true;
                templateSelector.SelectedIndex = -1;
                loading = false;
            }
        };
        save.Click += async (_, _) =>
        {
            if (snapshot is null || selector.SelectedValue is not string selected || lifetime is null)
                return;
            var baseline = snapshot;
            var token = lifetime.Token;
            var version = ++generation;
            var chosenEnabled = enabled.IsChecked == true;
            var chosenName = name.Text;
            var chosenText = preview.Text;
            // Local input errors have no effect to reconcile; keep the unsaved draft editable.
            try { PersonalityEditor.ValidateDraft(selected, chosenName, chosenText); }
            catch (Exception error) when (error is System.IO.InvalidDataException or System.Text.EncoderFallbackException)
            {
                Status(error.Message, true);
                return;
            }
            Ready(false);
            cancel.IsEnabled = false;
            Status("Saving the shared style and reconciling its previous shared section…");
            try
            {
                var saved = await Editor().SaveAsync(baseline, chosenEnabled, selected, chosenName, chosenText, token);
                if (version != generation || token.IsCancellationRequested)
                    return;
                snapshot = saved;
                loading = true;
                selector.ItemsSource = saved.Presets;
                selector.SelectedValue = saved.Settings.SelectedPresetId;
                enabled.IsChecked = saved.Settings.Enabled;
                loading = false;
                Present();
                Ready(true);
                Status("Saved shared style and default. Choose this style separately in each Main chat. Accepted or restored work keeps its saved selection; optional style may be omitted for context space.");
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)when (error is not OutOfMemoryException)
            {
                if (version != generation)
                    return;
                snapshot = null;
                Ready(false);
                Status(error.Message + " Reload to inspect the actual state before another save; a partial or uncertain operation must not be replayed.", true);
            }
        };
        cancel.Click += async (_, _) => await Reload("Unsaved changes discarded; current saved styles are shown.");
        scroll.Loaded += async (_, _) =>
        {
            if (lifetime is not null || disposed)
                return;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            await Reload("Loaded. Choose a style, then Save.");
        };
        scroll.Unloaded += (_, _) =>
        {
            generation++;
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            snapshot = null;
            Ready(false);
        };
        panel.Children.Add(CreatePendingView(() => Reload("Reloaded current preferences after import.")));
        return scroll;
    }

    private static TextBlock Text(string text, double size, string brush = DesktopThemeResourceKeys.TextBrush)
    {
        var value = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return value;
    }

    private static string Bounded(string text) => string.IsNullOrWhiteSpace(text) ? "The personality operation failed." : text.Length <= 700 ? text : text[..700] + "…";
    public override void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        RetireComposerViews();
        shutdown.Cancel();
        shutdown.Dispose();
        base.Dispose();
    }
}
