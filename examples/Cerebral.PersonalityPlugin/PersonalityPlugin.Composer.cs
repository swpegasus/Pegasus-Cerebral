using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PersonalityPlugin;

internal sealed record PersonalityChatBase(string Kind, string Id, long? Revision, string Sha256);
internal sealed record PersonalityChatChoice(PersonalityChatBase Base, string Name, bool Available, string? Diagnostic)
{
    public string Key => Base.Kind + ":" + Base.Id;
    public string Label => Name + (Base.Kind == "private" ? " (private)" : Base.Kind == "custom" ? " (custom)" : "");
}
internal sealed class PersonalityChatSaveException(string message, bool uncertain) : InvalidOperationException(message)
{
    internal bool Uncertain { get; } = uncertain;
}
internal sealed record PersonalityChatModifier(long Revision, string Sha256);
internal sealed record PersonalityChatEffective(string SourceId, long Revision, string Sha256);
internal sealed record PersonalityChatPreview(string Name, string Description, string Context, string? Sha256);
internal sealed class PersonalityChatSnapshot
{
    internal required DesktopConversationBinding Owner { get; init; }
    internal required long Revision { get; init; }
    internal required bool Enabled { get; init; }
    internal required PersonalityChatBase? Base { get; init; }
    internal required bool BluntMode { get; init; }
    internal required string? CatalogToken { get; init; }
    internal required IReadOnlyList<PersonalityChatChoice> Choices { get; init; }
    internal required bool Available { get; init; }
    internal required string? Diagnostic { get; init; }
    internal required PersonalityChatModifier? BluntPin { get; init; }
    internal required PersonalityChatEffective? EffectiveSource { get; init; }
    internal string? SaveWarning { get; set; }
    internal int Consumed;
}

public sealed partial class PersonalityPlugin
{
    private readonly SemaphoreSlim composerOperations = new(1, 1);
    private readonly List<Action> composerUnloads = [];
    private sealed record ComposerChoice(string Key, string Label, PersonalityChatChoice? Definition = null)
    {
        // UI Automation may use the data item's string instead of DisplayMemberPath.
        public override string ToString() => Label;
    }

    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new("personality-selector", DesktopMountSlots.ComposerAccessories)
        {
            Order = 20,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Personality", CreateComposerPersonalityView)
    ];

    private void RetireComposerViews()
    {
        foreach (var unload in composerUnloads.ToArray()) unload();
        composerUnloads.Clear();
    }

    private FrameworkElement CreateComposerPersonalityView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var host = ownerHost ?? throw new InvalidOperationException("The Personality host is unavailable.");
        var commands = service ?? throw new NotSupportedException("The host does not expose personality owner commands.");
        var settings = store ?? throw new NotSupportedException("The host does not expose plugin data.");
        if (host is not IDesktopPluginEnvironment { Surface: DesktopPluginSurface.Orchestrator } ||
            host is not IDesktopMountHost mounts || !mounts.GetMountCapabilities().Any(value =>
                value.Slot == DesktopMountSlots.ComposerAccessories && value.ContractVersion == 1 &&
                value.Supported && value.Kinds.Contains(DesktopMountKind.View)))
            throw new NotSupportedException("This host does not support the Main personality composer accessory.");

        var editor = new PersonalityEditor(commands, settings, () => host.GetConversation().Binding);
        var root = new Grid { Width = 274, MaxHeight = 44 };
        root.RowDefinitions.Add(new() { Height = new GridLength(28) });
        root.RowDefinitions.Add(new() { Height = new GridLength(14) });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        // Local minimums keep larger host control styles inside the compact accessory mount.
        var selector = new ComboBox
        {
            DisplayMemberPath = nameof(ComposerChoice.Label), SelectedValuePath = nameof(ComposerChoice.Key),
            MinWidth = 112, MinHeight = 24, Height = 24, Margin = new Thickness(3, 0, 3, 2),
            IsEnabled = false, MaxDropDownHeight = 360
        };
        var label = new Label { Content = "Personality", Target = selector, FontSize = 11,
            Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        label.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        var blunt = new ToggleButton
        {
            Content = "A**", Width = 29, MinWidth = 29, MinHeight = 24, Height = 24, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 0), IsEnabled = false,
            ToolTip = "A**hole mode — " + PersonalityTemplates.BluntModeClarification
        };
        var reload = new Button { Content = "↻", Width = 25, MinWidth = 25, MinHeight = 24, Height = 24,
            Padding = new Thickness(0), Margin = new Thickness(0),
            ToolTip = "Reload this chat’s actual saved personality selection", IsEnabled = false };
        var status = Text("Loading this chat’s personality…", 10, DesktopThemeResourceKeys.MutedTextBrush);
        status.Margin = new Thickness(0); status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        var preview = new Button { Content = "Preview context", Width = 82, MinWidth = 0, Height = 14,
            MinHeight = 14, Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0), FontSize = 9,
            IsEnabled = false, ToolTip = "Read the description and exact saved style/context; this does not send or change anything." };
        var footer = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(preview, Dock.Right); footer.Children.Add(preview); footer.Children.Add(status);
        AutomationProperties.SetAutomationId(root, "PersonalityComposer");
        AutomationProperties.SetAutomationId(selector, "PersonalityComposerChoice");
        AutomationProperties.SetAutomationId(blunt, "PersonalityComposerBlunt");
        AutomationProperties.SetAutomationId(reload, "PersonalityComposerReload");
        AutomationProperties.SetAutomationId(status, "PersonalityComposerStatus");
        AutomationProperties.SetAutomationId(preview, "PersonalityComposerPreview");
        AutomationProperties.SetName(selector, "This chat’s personality selection");
        AutomationProperties.SetName(blunt, "A**hole mode: constructive candid pushback for this chat");
        AutomationProperties.SetHelpText(blunt, PersonalityTemplates.BluntModeClarification);
        AutomationProperties.SetName(reload, "Reload this chat’s saved personality");
        AutomationProperties.SetName(status, "Selected personality status");
        AutomationProperties.SetName(preview, "Preview this chat’s saved personality context");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Grid.SetColumn(selector, 1); Grid.SetColumn(blunt, 2); Grid.SetColumn(reload, 3);
        Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 4);
        root.Children.Add(label); root.Children.Add(selector); root.Children.Add(blunt);
        root.Children.Add(reload); root.Children.Add(footer);

        PersonalityChatSnapshot? snapshot = null;
        DesktopConversationBinding? binding = null;
        CancellationTokenSource? lifetime = null, operation = null;
        bool attached = false, busy = false, presenting = false;
        Window? previewWindow = null;
        int generation = 0;
        const string hint = "Inclusion depends on the selected workflow and context budget. Choosing 'No personality' saves this chat with both the base and optional mode disabled for future turns; accepted or in-flight work keeps its starting style. Shared custom editing is in Configuration > Personality.";
        const string savedHint = "Saved for this chat; application not confirmed. This plugin cannot observe whether the selected workflow used the style. ";

        void Status(string message, bool error = false, bool saved = false)
        {
            status.Text = Bounded(message); status.ToolTip = (saved ? savedHint : "") + hint + "\n" + Bounded(message);
            status.SetResourceReference(TextBlock.ForegroundProperty,
                error ? DesktopThemeResourceKeys.ErrorTextBrush : DesktopThemeResourceKeys.MutedTextBrush);
        }
        void Controls()
        {
            var current = snapshot;
            var ready = attached && !disposed && !busy && current is not null;
            selector.IsEnabled = ready;
            blunt.IsEnabled = ready && current is { Enabled: true, Available: true, CatalogToken: not null } &&
                (current.Choices.Any(value => value.Base == current.Base && value.Available) ||
                 PersonalityEditor.SavedTemplate(current) is not null);
            reload.IsEnabled = attached && !disposed && !busy;
            preview.IsEnabled = ready;
            if (current is null)
            {
                const string unavailable = "Reload this chat’s saved personality before viewing its description or context.";
                selector.ToolTip = unavailable; AutomationProperties.SetHelpText(selector, unavailable);
                preview.ToolTip = unavailable;
            }
        }
        void ClosePreview()
        {
            var previous = previewWindow; previewWindow = null;
            previous?.Close();
        }
        bool Current(int version, DesktopConversationBinding expected)
        {
            if (!attached || disposed || lifetime is null || lifetime.IsCancellationRequested || version != generation) return false;
            try { return host.GetConversation().Binding == expected; }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { return false; }
        }
        void Present(PersonalityChatSnapshot current)
        {
            var choices = new List<ComposerChoice> { new("none", "No personality") };
            choices.AddRange(current.Choices.Select(value => new ComposerChoice(value.Key, value.Label, value)));
            var selected = "none";
            if (current.Enabled)
            {
                var found = current.Choices.SingleOrDefault(value => value.Base == current.Base);
                if (found is null)
                {
                    var archived = PersonalityEditor.SavedTemplate(current);
                    selected = archived is null ? "unavailable" : "saved:" + current.Base!.Id + ":" + current.Base.Revision;
                    choices.Add(new(selected, archived is null ? "Saved style unavailable" :
                        archived.Name + " (saved earlier version)", archived is null ? null :
                        new(current.Base!, archived.Name, current.Available, current.Diagnostic)));
                }
                else selected = found.Key;
            }
            presenting = true;
            try
            {
                selector.ItemsSource = choices; selector.SelectedValue = selected;
                blunt.IsChecked = current.Enabled && current.BluntMode;
            }
            finally { presenting = false; }
            var description = PersonalityEditor.ChatDescription(current);
            selector.ToolTip = description;
            AutomationProperties.SetHelpText(selector, description);
            preview.ToolTip = description + "\nPreview the exact saved context without sending or changing anything.";
            if (!current.Enabled) Status(current.Diagnostic is null ? "Saved: No personality · future turns." :
                "Saved: No personality. Library unavailable: " + current.Diagnostic, current.Diagnostic is not null, saved: true);
            else if (!current.Available)
                Status("Unavailable: " + (current.Diagnostic ?? "The saved style cannot be prepared. Choose another style or No personality."), true, saved: true);
            else
            {
                var name = current.Choices.SingleOrDefault(value => value.Base == current.Base)?.Name ??
                    PersonalityEditor.SavedTemplate(current)?.Name ?? "Saved style";
                Status("Saved: " + name + (current.BluntMode ? " + A**" : "") + " · use unverified" +
                    (current.SaveWarning is null ? "" : " " + current.SaveWarning), current.SaveWarning is not null, saved: true);
            }
        }
        void ShowPreview(PersonalityChatPreview value)
        {
            ClosePreview();
            var panel = new Grid { Margin = new Thickness(14) };
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var title = new TextBlock { Text = value.Name, FontSize = 16, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = SystemColors.WindowTextBrush };
            var description = new TextBlock { Text = value.Description, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8), Foreground = SystemColors.WindowTextBrush };
            var qualification = new TextBlock { Text = "Exact saved context preview. Application is not confirmed; the workflow or context budget may omit it. Accepted or in-flight work keeps its starting style.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = SystemColors.WindowTextBrush };
            var context = new TextBox { Text = value.Context, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MinWidth = 0, MinHeight = 80,
                Margin = new Thickness(0), Padding = new Thickness(8),
                Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush };
            var close = new Button { Content = "Close", MinWidth = 70, MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            AutomationProperties.SetAutomationId(description, "PersonalityContextDescription");
            AutomationProperties.SetAutomationId(context, "PersonalityContextText");
            AutomationProperties.SetName(context, "Exact saved personality context, read only");
            AutomationProperties.SetName(description, "Saved personality description");
            Grid.SetRow(description, 1); Grid.SetRow(qualification, 2); Grid.SetRow(context, 3); Grid.SetRow(close, 4);
            panel.Children.Add(title); panel.Children.Add(description); panel.Children.Add(qualification);
            panel.Children.Add(context); panel.Children.Add(close);
            // Use paired system brushes in this separate read-only window; Main's compact mount
            // exposes text brushes but no background contract for a new top-level surface.
            var window = new Window { Title = "Personality — saved context preview", Content = panel,
                Width = Math.Min(620, SystemParameters.WorkArea.Width), Height = Math.Min(460, SystemParameters.WorkArea.Height),
                MinWidth = 300, MinHeight = 240, ShowInTaskbar = false,
                Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush };
            var parent = Window.GetWindow(root);
            if (parent is not null) { window.Owner = parent; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            AutomationProperties.SetAutomationId(window, "PersonalityContextPreview");
            close.Click += (_, _) => window.Close();
            window.Closed += (_, _) => { if (ReferenceEquals(previewWindow, window)) previewWindow = null; };
            previewWindow = window; window.Show(); context.Focus();
        }
        async Task Preview()
        {
            if (!attached || disposed || busy || lifetime is null || snapshot is not { } baseline) return;
            ClosePreview();
            var version = ++generation;
            var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, shutdown.Token);
            operation = active; busy = true; Controls();
            bool entered = false;
            try
            {
                if (!Current(version, baseline.Owner)) throw new OperationCanceledException(active.Token);
                await composerOperations.WaitAsync(active.Token); entered = true;
                if (!Current(version, baseline.Owner)) throw new OperationCanceledException(active.Token);
                var value = await editor.PreviewChatAsync(baseline, active.Token);
                if (!Current(version, baseline.Owner)) throw new OperationCanceledException(active.Token);
                ShowPreview(value);
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested || !Current(version, baseline.Owner)) { }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (Current(version, baseline.Owner))
                { ClosePreview(); Status("Preview unavailable. " + Bounded(error.Message), true); }
            }
            finally
            {
                if (entered) composerOperations.Release();
                if (ReferenceEquals(operation, active)) operation = null;
                active.Dispose(); busy = false; Controls();
            }
        }
        async Task Run(bool save, ComposerChoice? choice = null, bool? mode = null)
        {
            if (!attached || disposed || busy || lifetime is null) return;
            ClosePreview();
            var baseline = snapshot;
            PersonalityChatBase? selected = null;
            var enabled = false; var nextMode = false;
            if (save)
            {
                if (baseline is null) return;
                try
                {
                    if (mode is { } requestedMode)
                    {
                        var existing = baseline.Choices.SingleOrDefault(value => value.Base == baseline.Base && value.Available);
                        if (!baseline.Enabled || !baseline.Available || existing is null && PersonalityEditor.SavedTemplate(baseline) is null)
                            throw new InvalidDataException("Choose an available personality before changing its mode.");
                        selected = existing?.Base ?? baseline.Base; enabled = true; nextMode = requestedMode;
                    }
                    else
                    {
                        if (choice is null) return;
                        if (choice.Key != "none")
                        {
                            var definition = choice.Definition;
                            if (definition is null || !definition.Available || baseline.CatalogToken is null ||
                                !baseline.Choices.Contains(definition) && !(definition.Base == baseline.Base && PersonalityEditor.SavedTemplate(baseline) is not null))
                                throw new InvalidDataException(choice.Definition?.Diagnostic ?? "Choose an available style or No personality.");
                            selected = definition.Base; enabled = true; nextMode = baseline.Enabled && baseline.BluntMode;
                        }
                    }
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
                { Present(baseline); Status(error.Message, true); return; }
            }
            var version = ++generation;
            var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, shutdown.Token);
            operation = active; busy = true; Controls();
            Status(save ? "Saving this chat’s selection… wait before sending." : "Loading this chat’s saved selection…");
            bool entered = false;
            DesktopConversationBinding? expected = null;
            try
            {
                expected = host.GetConversation().Binding;
                if (string.IsNullOrWhiteSpace(expected.SessionId))
                    throw new InvalidOperationException("This chat has no stable saved identity. Open or save a chat, then Reload.");
                if (save && (baseline is null || expected != baseline.Owner))
                    throw new InvalidOperationException("The chat changed. Reload before another selection.");
                binding = expected;
                await composerOperations.WaitAsync(active.Token); entered = true;
                if (!Current(version, expected)) throw new OperationCanceledException(active.Token);
                var loaded = save
                    ? await editor.SaveChatAsync(baseline ?? throw new InvalidOperationException("Reload before selecting."), enabled, selected, nextMode, active.Token)
                    : await editor.LoadChatAsync(expected, active.Token);
                if (!Current(version, expected)) throw new OperationCanceledException(active.Token);
                snapshot = loaded; Present(loaded);
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested || expected is not null && !Current(version, expected))
            {
                if (attached && !disposed && version == generation)
                { snapshot = null; Status("Chat or host changed. Reload the saved selection.", true); }
            }
            catch (PersonalityChatSaveException error)
            {
                if (attached && !disposed && version == generation)
                { snapshot = null; Status((error.Uncertain ? "Selection outcome not confirmed. " : "Selection rejected. ") + error.Message, true); }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (attached && !disposed && version == generation)
                {
                    snapshot = null;
                    Status((save ? "Selection outcome not confirmed. Reload before another change. " : "Personality unavailable. Reload. ") + error.Message, true);
                }
            }
            finally
            {
                if (entered) composerOperations.Release();
                if (ReferenceEquals(operation, active)) operation = null;
                active.Dispose(); busy = false; Controls();
            }
        }
        async void Changed(object? sender, DesktopConversationChangedEventArgs args)
        {
            ClosePreview();
            DesktopConversationBinding current;
            try { current = host.GetConversation().Binding; }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
            { generation++; snapshot = null; operation?.Cancel(); Controls(); Status("Personality host unavailable. Reload.", true); return; }
            if (current == binding) return;
            binding = current; generation++; snapshot = null; operation?.Cancel(); Controls();
            if (busy) Status("Chat changed; the prior selection is settling. Reload afterward.", true);
            else await Run(false);
        }
        void Unload()
        {
            if (!attached) return;
            attached = false; generation++; snapshot = null;
            ClosePreview();
            try { host.ConversationChanged -= Changed; }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
            operation?.Cancel(); lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            composerUnloads.Remove(Unload); Controls();
        }
        selector.SelectionChanged += async (_, _) =>
        {
            if (!presenting && selector.IsEnabled && selector.SelectedItem is ComposerChoice choice) await Run(true, choice);
        };
        blunt.Click += async (_, _) =>
        {
            if (!presenting && blunt.IsEnabled) await Run(true, mode: blunt.IsChecked == true);
        };
        reload.Click += async (_, _) => await Run(false);
        preview.Click += async (_, _) => await Preview();
        root.Loaded += async (_, _) =>
        {
            if (attached || disposed) return;
            attached = true; lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            try
            {
                host.ConversationChanged += Changed; composerUnloads.Add(Unload); Controls();
                if (busy) Status("Previous selection is settling. Reload afterward.", true);
                else await Run(false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { Unload(); Status("Personality host unavailable. Reload. " + error.Message, true); }
        };
        root.Unloaded += (_, _) => Unload();
        return root;
    }
}

internal sealed partial class PersonalityEditor
{
    internal static PersonalityTemplate? SavedTemplate(PersonalityChatSnapshot value) =>
        value.Base is { Kind: "packaged", Revision: { } revision } pin
            ? PersonalityTemplates.Exact(pin.Id, revision, pin.Sha256) : null;

    internal static string ChatDescription(PersonalityChatSnapshot value)
    {
        if (!value.Enabled) return "No personality is saved for future turns in this chat. Both the base style and A**hole mode are off.";
        var description = SavedTemplate(value)?.Description ?? (value.Base?.Kind switch
        {
            "packaged" when value.Base?.Id == "cerebral" => "Direct, candid and practical communication, with evidence, uncertainty and useful next steps.",
            "packaged" when value.Base?.Id == "succinct" => "Brief, clear answers focused on the result and essential supporting information.",
            "custom" => "Your saved custom communication style. Preview reads its exact saved revision, not the latest editable wording.",
            "private" => "A private saved style. Its body is not available through this preview; no private retrieval is attempted.",
            _ => "The exact saved personality definition is unavailable; no replacement will be selected."
        });
        return description + (value.BluntMode ? " A**hole mode adds constructive candid pushback without changing accuracy or permissions." : "");
    }

    internal async Task<PersonalityChatPreview> PreviewChatAsync(PersonalityChatSnapshot baseline, CancellationToken token = default)
    {
        Guard(baseline.Owner, token);
        RequireSameChat(baseline, await LoadChatAsync(baseline.Owner, token));
        if (!baseline.Enabled)
            return new("No personality", ChatDescription(baseline), "No personality context is contributed for future turns. Both the base style and optional mode are disabled. Accepted or in-flight work keeps its starting style.", null);
        var pin = baseline.Base ?? throw new InvalidDataException("The saved personality has no exact definition pin. Reload.");
        if (pin.Kind == "private")
            throw new NotSupportedException("Private personality context is not available through this preview. No private definition was retrieved.");

        string name, text;
        if (SavedTemplate(baseline) is { } template)
        { name = template.Name; text = template.Text; }
        else
        {
            if (pin.Kind == "packaged" && pin.Id is not ("cerebral" or "succinct"))
                throw new InvalidDataException("The exact saved packaged revision is not present in this installation. No newer wording was substituted.");
            var preset = ReadPreset(await Execute("personality.get", new { id = pin.Id, revision = pin.Revision }, baseline.Owner, token));
            if (preset.Id != pin.Id || preset.Revision != pin.Revision || preset.Sha256 != pin.Sha256 ||
                preset.ReadOnly != (pin.Kind == "packaged"))
                throw new InvalidDataException("The saved personality identity, revision or content hash does not match. No context was displayed.");
            name = preset.Name; text = preset.Text;
        }
        ValidateDraft("style-preview", name, text);
        if (Digest(Utf8.GetBytes(text)) != pin.Sha256)
            throw new InvalidDataException("The saved base content hash does not match.");
        if (baseline.BluntMode && (baseline.BluntPin is not { } mode ||
            mode.Revision != PersonalityTemplates.BluntModeRevision ||
            mode.Sha256 != Digest(Utf8.GetBytes(PersonalityTemplates.BluntModeText))))
            throw new InvalidDataException("The exact saved mode revision is unavailable. No current modifier was substituted.");
        var effective = text + (baseline.BluntMode ? "\n" + PersonalityTemplates.BluntModeText : "");
        var hash = Digest(Utf8.GetBytes(effective));
        object? modifier = baseline.BluntPin is { } admitted
            ? new { id = "blunt", revision = admitted.Revision, sha256 = admitted.Sha256 } : null;
        var descriptor = Digest(JsonSerializer.SerializeToUtf8Bytes(new { baseSelection = pin, bluntPin = modifier }, Json));
        if (baseline.EffectiveSource is not { Revision: 1 } source || source.Sha256 != hash ||
            source.SourceId != "personality/v2/" + descriptor + "/" + hash)
            throw new InvalidDataException("The saved effective context does not match its base and mode pins. No context was displayed.");
        // This is a read-only reconstruction of the existing provider's user-text wrapper,
        // not proof of model inclusion. Recheck state after the exact source read.
        RequireSameChat(baseline, await LoadChatAsync(baseline.Owner, token));
        Guard(baseline.Owner, token);
        return new(name + (baseline.BluntMode ? " + A**hole mode" : ""), ChatDescription(baseline),
            "Optional communication style reference 'effective', revision 1, SHA-256 " + hash + ":\n" + effective, hash);
    }

    private static void RequireSameChat(PersonalityChatSnapshot expected, PersonalityChatSnapshot actual)
    {
        if (expected.Owner != actual.Owner || expected.Revision != actual.Revision || expected.Enabled != actual.Enabled ||
            expected.Base != actual.Base || expected.BluntMode != actual.BluntMode || expected.BluntPin != actual.BluntPin ||
            expected.EffectiveSource != actual.EffectiveSource)
            throw new InvalidOperationException("The saved selection changed. Reload before previewing or changing it; no replacement was selected.");
    }

    internal async Task<PersonalityChatSnapshot> LoadChatAsync(DesktopConversationBinding binding, CancellationToken token)
    {
        var conversationId = ChatIdentity(binding);
        return ReadChat(await Execute("personality.chat.get", new { schemaVersion = 1, conversationId },
            binding, token, 64 * 1024), binding);
    }

    internal async Task<PersonalityChatSnapshot> SaveChatAsync(PersonalityChatSnapshot baseline, bool enabled,
        PersonalityChatBase? selected, bool bluntMode, CancellationToken token)
    {
        var conversationId = ChatIdentity(baseline.Owner);
        Guard(baseline.Owner, token);
        if (!enabled && (selected is not null || bluntMode) || enabled &&
            (selected is null || baseline.CatalogToken is null || !baseline.Choices.Any(value =>
                value.Base == selected && value.Available) &&
                !(selected == baseline.Base && baseline.Available && SavedTemplate(baseline) is not null)))
            throw new InvalidDataException("Reload before changing this chat’s personality.");
        if (Interlocked.Exchange(ref baseline.Consumed, 1) != 0)
            throw new InvalidOperationException("This selection was already attempted. Reload to reconcile it; do not replay.");

        // The disabled request deliberately omits catalog/base fields. Turning a chat off must
        // not require source preparation or a currently available library.
        object args = enabled
            ? new { schemaVersion = 1, conversationId, expectedRevision = baseline.Revision,
                enabled = true, baseSelection = selected, bluntMode, catalogToken = baseline.CatalogToken }
            : new { schemaVersion = 1, conversationId, expectedRevision = baseline.Revision,
                enabled = false, bluntMode = false };
        var reply = await Execute("personality.chat.save", args, baseline.Owner, token, 64 * 1024);
        Fields(reply, ["schemaVersion", "conversationId", "outcome", "changed", "state", "diagnostic"]);
        if (Integer(reply, "schemaVersion") != 1 || String(reply, "conversationId", 256) != conversationId)
            throw new InvalidDataException("The save acknowledgement belongs to a different chat or schema.");
        var outcome = String(reply, "outcome", 16);
        var diagnostic = ChatDiagnostic(reply.GetProperty("diagnostic"));
        if (outcome is "rejected" or "uncertain")
        {
            if (diagnostic is null || outcome == "rejected" && reply.GetProperty("changed").ValueKind != JsonValueKind.False ||
                outcome == "uncertain" && reply.GetProperty("changed").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("The personality save result is malformed. Reload to reconcile it.");
            // A rejection may still have retained an unused immutable prepared source.
            // Neither rejection nor uncertain acknowledgement permits replaying this baseline.
            throw new PersonalityChatSaveException(diagnostic + " Reload before another change.", outcome == "uncertain");
        }
        if (outcome is not ("committed" or "unchanged") ||
            Boolean(reply, "changed") != (outcome == "committed"))
            throw new InvalidDataException("The personality save was not confirmed. Reload before another change.");
        var acknowledged = ReadChat(reply.GetProperty("state"), baseline.Owner);
        var expectedRevision = outcome == "committed" ? checked(baseline.Revision + 1) : baseline.Revision;
        if (acknowledged.Revision != expectedRevision || acknowledged.Enabled != enabled ||
            acknowledged.BluntMode != (enabled && bluntMode) ||
            (enabled ? !MatchesRequested(acknowledged.Base, selected) : acknowledged.Base != baseline.Base))
            throw new InvalidDataException("The acknowledged chat selection does not match the requested change.");
        var confirmed = await LoadChatAsync(baseline.Owner, token);
        if (confirmed.Revision != acknowledged.Revision || confirmed.Enabled != acknowledged.Enabled ||
            confirmed.Base != acknowledged.Base || confirmed.BluntMode != acknowledged.BluntMode ||
            confirmed.BluntPin != acknowledged.BluntPin || confirmed.EffectiveSource != acknowledged.EffectiveSource)
            throw new InvalidOperationException("The chat selection changed during readback. Reload before another change.");
        confirmed.SaveWarning = diagnostic;
        return confirmed;
    }

    private static bool MatchesRequested(PersonalityChatBase? prepared, PersonalityChatBase? requested) =>
        prepared is { Revision: > 0 } && requested is not null && prepared.Kind == requested.Kind &&
        prepared.Id == requested.Id && prepared.Sha256 == requested.Sha256 &&
        (requested.Revision is null || prepared.Revision == requested.Revision);

    private static string ChatIdentity(DesktopConversationBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.SessionId) && Utf8.GetByteCount(binding.SessionId) <= 256
            ? binding.SessionId : throw new InvalidOperationException("This chat has no valid stable identity. Reload a saved chat.");

    private static PersonalityChatSnapshot ReadChat(JsonElement value, DesktopConversationBinding binding)
    {
        Fields(value, ["schemaVersion", "conversationId", "revision", "enabled", "baseSelection", "bluntMode",
            "bluntPin", "availability", "diagnostic", "effectiveSource", "catalogToken", "catalog"]);
        if (Integer(value, "schemaVersion") != 1 || String(value, "conversationId", 256) != ChatIdentity(binding))
            throw new InvalidDataException("The personality reply belongs to a different chat or schema.");
        var revision = Integer(value, "revision");
        var enabled = Boolean(value, "enabled"); var mode = Boolean(value, "bluntMode");
        var selection = value.GetProperty("baseSelection").ValueKind == JsonValueKind.Null
            ? null : ChatBase(value.GetProperty("baseSelection"), false);
        var availability = String(value, "availability", 16);
        if (availability is not ("none" or "ready" or "unavailable"))
            throw new InvalidDataException("Unsupported chat personality availability.");
        var diagnostic = ChatDiagnostic(value.GetProperty("diagnostic"));
        var token = value.GetProperty("catalogToken").ValueKind == JsonValueKind.Null
            ? null : String(value, "catalogToken", 256);
        if (token is { Length: 0 }) throw new InvalidDataException("Empty personality catalog token.");
        var catalog = value.GetProperty("catalog");
        if (catalog.ValueKind != JsonValueKind.Array || catalog.GetArrayLength() > 32)
            throw new InvalidDataException("The personality catalog exceeds its supported bound.");
        var choices = new List<PersonalityChatChoice>();
        foreach (var item in catalog.EnumerateArray())
        {
            Fields(item, ["kind", "id", "revision", "sha256", "name", "availability", "diagnostic"]);
            var definition = ChatBase(item, true);
            var name = String(item, "name", 320);
            if (name.Length > 80 || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A personality name is empty.");
            var state = String(item, "availability", 16);
            var reason = ChatDiagnostic(item.GetProperty("diagnostic"));
            if (state is not ("ready" or "unprepared" or "unavailable") ||
                state == "ready" && definition.Revision is null ||
                state == "unprepared" && (definition.Kind != "custom" || definition.Revision is not null) ||
                state == "unavailable" && reason is null)
                throw new InvalidDataException("Malformed catalog availability.");
            choices.Add(new(definition, name, state != "unavailable", reason));
        }
        if (choices.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != choices.Count ||
            token is null && (choices.Count != 0 || diagnostic is null))
            throw new InvalidDataException("Duplicate or unadmitted personality catalog entries.");

        PersonalityChatModifier? modifier = null;
        var modifierValue = value.GetProperty("bluntPin");
        if (modifierValue.ValueKind != JsonValueKind.Null)
        {
            Fields(modifierValue, ["id", "revision", "sha256"]);
            if (String(modifierValue, "id", 16) != "blunt" || Integer(modifierValue, "revision") < 1)
                throw new InvalidDataException("Unsupported personality mode identity.");
            modifier = new(Integer(modifierValue, "revision"), Hash(modifierValue, "sha256"));
        }
        PersonalityChatEffective? effective = null;
        var source = value.GetProperty("effectiveSource");
        if (source.ValueKind != JsonValueKind.Null)
        {
            Fields(source, ["sourceId", "revision", "sha256"]);
            var sourceId = String(source, "sourceId", 512);
            if (sourceId.Length == 0 || Integer(source, "revision") < 1)
                throw new InvalidDataException("Malformed effective personality source.");
            effective = new(sourceId, Integer(source, "revision"), Hash(source, "sha256"));
        }
        if (!enabled && (mode || modifier is not null || effective is not null || availability != "none") ||
            enabled && (selection is null || availability == "none") ||
            mode != (modifier is not null) ||
            enabled && availability == "ready" && effective is null ||
            availability == "unavailable" && diagnostic is null ||
            revision == 0 && (enabled || selection is not null))
            throw new InvalidDataException("Inconsistent chat personality state.");
        var available = availability == "ready";
        if (available && !choices.Any(item => item.Base == selection && item.Available) &&
            !(selection is { Kind: "packaged", Revision: { } savedRevision } &&
              PersonalityTemplates.Exact(selection.Id, savedRevision, selection.Sha256) is not null))
        { available = false; diagnostic ??= "The saved definition does not match the current library. Choose a style or No personality."; }
        if (modifier is { } admittedModifier && (admittedModifier.Revision != PersonalityTemplates.BluntModeRevision ||
            admittedModifier.Sha256 != Digest(Utf8.GetBytes(PersonalityTemplates.BluntModeText))))
        { available = false; diagnostic = "The installed personality mode versions differ. Reload or select No personality."; }
        return new()
        {
            Owner = binding, Revision = revision, Enabled = enabled, Base = selection, BluntMode = mode,
            CatalogToken = token, Choices = choices.AsReadOnly(), Available = available, Diagnostic = diagnostic,
            BluntPin = modifier, EffectiveSource = effective
        };
    }

    private static PersonalityChatBase ChatBase(JsonElement value, bool catalog)
    {
        if (!catalog) Fields(value, ["kind", "id", "revision", "sha256"]);
        var kind = String(value, "kind", 16);
        var id = String(value, "id", 128);
        if (kind is not ("packaged" or "custom" or "private") || id.Length == 0 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_')))
            throw new InvalidDataException("Malformed qualified personality identity.");
        long? revision = value.GetProperty("revision").ValueKind == JsonValueKind.Null
            ? null : Integer(value, "revision");
        if (revision is < 1 || revision is null && (!catalog || kind != "custom"))
            throw new InvalidDataException("Malformed personality definition revision.");
        return new(kind, id, revision, Hash(value, "sha256"));
    }

    private static string? ChatDiagnostic(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, ["code", "message"]);
        var code = String(value, "code", 80);
        var message = String(value, "message", 1024);
        if (code.Length == 0 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.')) ||
            string.IsNullOrWhiteSpace(message))
            throw new InvalidDataException("Malformed personality diagnostic.");
        return message;
    }
}
