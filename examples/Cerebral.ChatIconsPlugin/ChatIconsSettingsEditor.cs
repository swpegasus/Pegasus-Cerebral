using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.ChatIconsPlugin;

/// <summary>One aggregate editor session; each map's Save preserves the committed sibling map.</summary>
internal sealed class ChatIconsSettingsEditor
{
    private readonly PackageSettingsOwnerClient? client;
    private readonly Func<ChatIconPreferences> legacy;
    private readonly Action<PluginSettingsState, ChatIconPreferences> committed;
    private readonly CancellationToken packageLifetime;
    private readonly Dictionary<string, ComboBox> roles = new(StringComparer.Ordinal), meanings = new(StringComparer.Ordinal);
    private readonly Button saveRoles, saveMeanings, saveAll, reload, discard, loadLegacy, admit, copy;
    private readonly Func<CancellationToken, Task<DesktopOperationResult>>? copyReferences;
    private readonly ComboBox pending;
    private readonly TextBlock status;
    private readonly IReadOnlyDictionary<string, string>? glyphs;
    private CancellationTokenSource? mounted;
    private PluginSettingsState? baseline;
    private ChatIconPreferences saved = ChatIconsSettingsPolicy.Defaults;
    private bool hydrating, writable, uncertain, busy, mutation;
    internal FrameworkElement View { get; }

    internal ChatIconsSettingsEditor(PackageSettingsOwnerClient? client, Func<ChatIconPreferences> legacy,
        Action<PluginSettingsState, ChatIconPreferences> committed, CancellationToken packageLifetime,
        Func<CancellationToken, Task<DesktopOperationResult>>? copyReferences = null,
        IReadOnlyDictionary<string, string>? glyphs = null)
    {
        this.client = client; this.legacy = legacy; this.committed = committed; this.packageLifetime = packageLifetime;
        this.copyReferences = copyReferences; this.glyphs = glyphs;
        var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(panel, "ChatIcons.SettingsContent");
        panel.Children.Add(new TextBlock { Text = "Chat icon choices", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Choose the icons used for quick messages and activity labels.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        AddMap(panel, "Quick-insert roles", RoleSettings.Defaults, roles, "Role");
        saveRoles = Button("Save roles", "SaveRoles"); panel.Children.Add(saveRoles);
        AddMap(panel, "Default icons by meaning", SemanticIconSettings.Defaults, meanings, "Meaning");
        saveMeanings = Button("Save meanings", "SaveMeanings"); panel.Children.Add(saveMeanings);
        copy = Button("Copy meaning references", "CopyMeanings"); panel.Children.Add(copy);
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        saveAll = Button("Save all choices", "SaveAll"); reload = Button("Reload saved choices", "Reload");
        discard = Button("Discard changes", "Discard"); loadLegacy = Button("Load existing local choices", "LoadLegacy");
        foreach (var button in new[] { saveAll, reload, discard, loadLegacy }) row.Children.Add(button);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "Saved imports waiting for this package", TextWrapping = TextWrapping.Wrap });
        pending = new ComboBox { MinWidth = 320, DisplayMemberPath = nameof(PendingChoice.Label), Margin = new Thickness(0, 4, 0, 6) };
        AutomationProperties.SetAutomationId(pending, "ChatIcons.Pending"); panel.Children.Add(pending);
        admit = Button("Apply selected saved import", "AdmitPending"); panel.Children.Add(admit);
        status = new TextBlock { Text = "Loading saved icon choices…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(status, "ChatIcons.SettingsStatus"); panel.Children.Add(status);
        // Configuration supplies a finite fill area; the editor owns scrolling for its long form.
        View = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        AutomationProperties.SetAutomationId(View, "ChatIcons.Settings");
        saveRoles.Click += async (_, _) => await SaveAsync(MapChoice.Roles);
        saveMeanings.Click += async (_, _) => await SaveAsync(MapChoice.Meanings);
        saveAll.Click += async (_, _) => await SaveAsync(MapChoice.Both);
        reload.Click += async (_, _) => await ReloadAsync();
        discard.Click += async (_, _) => await ReloadAsync(discardDraft: true);
        loadLegacy.Click += (_, _) => PrepareLegacy();
        admit.Click += async (_, _) => await AdmitPendingAsync();
        copy.Click += async (_, _) => await CopyReferencesAsync();
        pending.SelectionChanged += (_, _) => UpdateControls();
        View.Loaded += async (_, _) => { Mount(); await ReloadAsync(); };
        View.Unloaded += (_, _) => Unmount();
        UpdateControls();
    }

    private void AddMap(Panel panel, string title, IReadOnlyDictionary<string, string> defaults,
        Dictionary<string, ComboBox> selectors, string kind)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new Thickness(0, 14, 0, 6) });
        var choices = DesktopIcons.Definitions.Select(icon =>
        {
            var id = ChatIconsPlugin.PluginId + "." + icon.Key;
            var label = glyphs is not null && glyphs.TryGetValue(id, out var glyph) ? glyph + "  " + icon.Label : icon.Label;
            return new AssetChoice(id, label);
        }).ToArray();
        foreach (var key in defaults.Keys)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center });
            var choice = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(AssetChoice.Label),
                SelectedValuePath = nameof(AssetChoice.Id), SelectedValue = defaults[key], MinWidth = 180 };
            AutomationProperties.SetAutomationId(choice, "ChatIcons." + kind + "." + key);
            AutomationProperties.SetName(choice, key + " icon");
            choice.SelectionChanged += (_, _) => { if (!hydrating) UpdateControls(); };
            Grid.SetColumn(choice, 1); row.Children.Add(choice); selectors.Add(key, choice); panel.Children.Add(row);
        }
    }

    internal void Mount()
    {
        if (mounted is null) mounted = CancellationTokenSource.CreateLinkedTokenSource(packageLifetime);
        UpdateControls();
    }
    internal void Unmount()
    {
        var prior = mounted; mounted = null; prior?.Cancel(); prior?.Dispose();
        // An in-flight response may have committed. Keep the draft and require a new read on remount.
        writable = false; uncertain |= mutation; mutation = false; busy = false; UpdateControls();
    }
    private bool Current(CancellationTokenSource scope) => ReferenceEquals(mounted, scope) && !scope.IsCancellationRequested;

    internal async Task ReloadAsync(bool discardDraft = false)
    {
        if (busy || mounted is not { } scope || scope.IsCancellationRequested) return;
        var dirty = Dirty();
        busy = true; UpdateControls();
        try
        {
            if (client is null) throw new InvalidOperationException("Saved icon settings are unavailable on this host. Existing local choices can be reviewed without saving.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var state = await client.ReadAsync(timeout.Token);
            if (!Current(scope)) return;
            ChatIconPreferences value;
            try
            {
                if (state.Active is { } active)
                {
                    var admission = new ChatIconsSettingsPolicy().Admit(active, null, false);
                    if (!admission.Accepted) throw new InvalidDataException();
                    value = ChatIconsSettingsPolicy.Decode(admission.Values);
                }
                else value = ChatIconsSettingsPolicy.Defaults;
            }
            catch (InvalidDataException)
            {
                baseline = state; writable = false; UpdatePending(state);
                status.Text = "The saved icon format is unsupported. Your draft and saved imports are preserved; saving defaults is disabled.";
                return;
            }
            baseline = state; saved = value; writable = true; uncertain = false;
            if (discardDraft || !dirty) Apply(value);
            UpdatePending(state);
            if (state.Active is not null) Notify(state, value);
            status.Text = dirty && !discardDraft ? "Saved choices reloaded; your unsaved draft is retained. Review it before saving."
                : state.Active is null ? "No shared icon choices are saved. Choose icons or load existing local choices, then save explicitly."
                : "Saved icon choices loaded. Opening this page does not save.";
        }
        catch (OperationCanceledException) when (!Current(scope)) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!Current(scope)) return;
            writable = false;
            status.Text = error is PackageSettingsUnavailableException or InvalidOperationException
                ? error.Message : "Saved icon choices could not be read. Your draft is preserved.";
        }
        finally { if (Current(scope)) { busy = false; mutation = false; UpdateControls(); } }
    }

    private async Task SaveAsync(MapChoice choice)
    {
        if (busy || !writable || uncertain || baseline is null || client is null || mounted is not { } scope || scope.IsCancellationRequested) return;
        var draft = Draft();
        // Never include an unsaved sibling map in a single-map Save.
        var value = choice switch
        {
            MapChoice.Roles => new ChatIconPreferences(draft.Roles, saved.Meanings),
            MapChoice.Meanings => new ChatIconPreferences(saved.Roles, draft.Meanings),
            _ => draft
        };
        await CommitAsync(scope, value, null);
    }

    private async Task CopyReferencesAsync()
    {
        if (busy || copyReferences is null || mounted is not { } scope || scope.IsCancellationRequested) return;
        busy = true; UpdateControls();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await copyReferences(timeout.Token).WaitAsync(timeout.Token);
            if (Current(scope)) status.Text = result.State is DesktopOperationState.Completed or DesktopOperationState.Accepted
                ? "Copied the current saved meaning references." : "Meaning references were not copied.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (Current(scope)) status.Text = "Meaning references were not copied."; }
        finally { if (Current(scope)) { busy = false; UpdateControls(); } }
    }

    internal void PrepareLegacy()
    {
        if (busy || mounted is null || mounted.IsCancellationRequested) return;
        try
        {
            var prepared = legacy(); _ = ChatIconsSettingsPolicy.Encode(prepared);
            Apply(prepared);
            status.Text = "Existing local choices loaded for review. Save all choices to store them; the local files remain unchanged.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { status.Text = "Existing local choices could not be read. Your draft and local files are preserved."; }
        UpdateControls();
    }

    internal async Task AdmitPendingAsync()
    {
        if (busy || !writable || uncertain || baseline is null || client is null || mounted is not { } scope ||
            scope.IsCancellationRequested || pending.SelectedItem is not PendingChoice selected) return;
        var item = baseline.Pending.Single(entry => entry.Id == selected.Id);
        var policy = new ChatIconsSettingsPolicy();
        var purpose = client.PendingPurpose(baseline, item.Id);
        var admission = purpose == SettingsTransferPurpose.Complete
            ? policy.AdmitComplete(item.Document, baseline.Active?.Values)
            : policy.Admit(item.Document, baseline.Active?.Values, purpose == SettingsTransferPurpose.Portable);
        if (!admission.Accepted)
        {
            status.Text = "This saved import uses an unsupported icon format or identity. It remains waiting, unchanged.";
            return;
        }
        await CommitAsync(scope, ChatIconsSettingsPolicy.Decode(admission.Values), item.Id);
    }

    private async Task CommitAsync(CancellationTokenSource scope, ChatIconPreferences value, string? pendingId)
    {
        if (client is null || baseline is null) return;
        busy = true; mutation = true; UpdateControls();
        try
        {
            var document = ChatIconsSettingsPolicy.Document(value);
            var policy = new ChatIconsSettingsPolicy();
            var projection = policy.ExportPortable(document);
            var completeProjection = client.ProjectComplete(policy, document);
            var completeAdmitted = false;
            if (pendingId is not null && client.PendingPurpose(baseline, pendingId) == SettingsTransferPurpose.Complete)
            {
                var selected = baseline.Pending.Single(p => p.Id == pendingId);
                var admission = policy.AdmitComplete(selected.Document, baseline.Active?.Values);
                if (!admission.Accepted) throw new InvalidDataException("The selected complete icon preferences are unsupported.");
                completeAdmitted = true;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await client.SaveAsync(baseline, 1, document.Values, projection, pendingId, timeout.Token, completeProjection, completeAdmitted);
            if (!Current(scope)) return;
            var prior = saved; baseline = result.State;
            saved = ChatIconsSettingsPolicy.Decode(result.State.Active!.Values);
            // Preserve unsaved choices in the sibling editor; only committed maps are rebased.
            Rebase(roles, prior.Roles, saved.Roles); Rebase(meanings, prior.Meanings, saved.Meanings);
            UpdatePending(result.State);
            var notified = Notify(result.State, saved);
            status.Text = (result.Changed ? "Icon choices saved." : "These icon choices were already saved.") +
                (Dirty() ? " Your unsaved choices are retained." : "") +
                (notified ? "" : " Reopen the picker to refresh presentation.");
        }
        catch (PackageSettingsSaveUncertainException error)
        {
            if (Current(scope)) { uncertain = true; status.Text = error.Message; }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current(scope)) status.Text = "Icon choices were not saved. Your draft is preserved; reload before retrying.";
        }
        finally { if (Current(scope)) { busy = false; mutation = false; UpdateControls(); } }
    }

    private void Rebase(Dictionary<string, ComboBox> selectors, IReadOnlyDictionary<string, string> prior, IReadOnlyDictionary<string, string> next)
    {
        hydrating = true;
        try
        {
            foreach (var (key, selector) in selectors)
                if (Equals(selector.SelectedValue, prior[key]) || Equals(selector.SelectedValue, next[key]))
                    selector.SelectedValue = next[key];
        }
        finally { hydrating = false; }
    }
    private ChatIconPreferences Draft() => new(Values(roles), Values(meanings));
    private static IReadOnlyDictionary<string, string> Values(Dictionary<string, ComboBox> selectors) =>
        selectors.ToDictionary(pair => pair.Key, pair => pair.Value.SelectedValue as string ?? "", StringComparer.Ordinal);
    private bool Dirty() => roles.Any(pair => !Equals(pair.Value.SelectedValue, saved.Roles[pair.Key])) ||
        meanings.Any(pair => !Equals(pair.Value.SelectedValue, saved.Meanings[pair.Key]));
    private bool Notify(PluginSettingsState state, ChatIconPreferences value)
    {
        try { committed(state, value); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }
    private void Apply(ChatIconPreferences value)
    {
        hydrating = true;
        try { foreach (var (key, control) in roles) control.SelectedValue = value.Roles[key];
            foreach (var (key, control) in meanings) control.SelectedValue = value.Meanings[key]; }
        finally { hydrating = false; }
    }
    private void UpdatePending(PluginSettingsState state)
    {
        var selected = (pending.SelectedItem as PendingChoice)?.Id;
        pending.ItemsSource = state.Pending.Select((item, index) => new PendingChoice(item.Id,
            "Saved import " + (index + 1) + " · format " + (item.Document.SettingsVersion?.ToString() ?? "unknown"))).ToArray();
        pending.SelectedItem = pending.Items.OfType<PendingChoice>().FirstOrDefault(item => item.Id == selected);
    }
    private void UpdateControls()
    {
        // Control construction can raise SelectionChanged before all buttons are assigned.
        if (admit is null) return;
        var available = mounted is { IsCancellationRequested: false } && !busy;
        var canSave = available && writable && !uncertain;
        foreach (var control in roles.Values.Concat(meanings.Values)) control.IsEnabled = available;
        saveRoles.IsEnabled = saveMeanings.IsEnabled = saveAll.IsEnabled = canSave;
        admit.IsEnabled = canSave && pending.SelectedItem is PendingChoice;
        pending.IsEnabled = available; reload.IsEnabled = discard.IsEnabled = available && client is not null;
        loadLegacy.IsEnabled = available;
        copy.IsEnabled = available && copyReferences is not null;
    }
    private static Button Button(string label, string id)
    {
        var button = new Button { Content = label, MinWidth = 90, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetAutomationId(button, "ChatIcons." + id); return button;
    }
    private enum MapChoice { Roles, Meanings, Both }
    private sealed record AssetChoice(string Id, string Label);
    private sealed record PendingChoice(string Id, string Label);
}
