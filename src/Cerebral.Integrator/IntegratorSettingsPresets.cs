using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly ComboBox settingsPresetPicker = new() { MinWidth = 260, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock settingsPresetDetails = Ui.Text("Connect to load named settings presets.", 13, Ui.Muted);
    private readonly TextBlock settingsLayoutStatus = Ui.Text("Window and page layout uses the current host's supported state. Full docking and floating panels are not available.", 13, Ui.Muted);
    private readonly List<Button> settingsPresetActions = [];
    private SettingsPresetClient? settingsPresetClient;
    private string? settingsLayoutBackendIdentity;
    private SettingsPresetCapabilities? settingsPresetCapabilities;
    private bool refreshingSettingsPresets;
    private bool changingSettingsPreset;
    private string settingsPresetDraftName = "";
    private readonly List<SettingsPresetClient> retiredSettingsPresetSessions = [];
    private readonly bool isolatedSettingsPresetFixture;
    private Func<bool, string?>? settingsPresetFixtureFilePicker = null;

    private UIElement SettingsPresetsContent()
    {
        AutomationProperties.SetAutomationId(settingsPresetPicker, "ConfigurationSettingsPresetPicker");
        AutomationProperties.SetName(settingsPresetPicker, "Named settings preset");
        settingsPresetPicker.DisplayMemberPath = nameof(SettingsPresetSummary.Name);
        settingsPresetPicker.SelectionChanged += (_, _) => UpdateSettingsPresetDetails();
        ServiceStatusChanged += (_, _) =>
        {
            if (settingsPresetClient is { IsCurrent: false })
            {
                settingsPresetCapabilities = null; settingsPresetPicker.ItemsSource = null;
                settingsLayoutTimer?.Stop();
                settingsPresetDetails.Text = "The service connection changed. Refresh presets for the intended service.";
            }
            SetSettingsPresetAvailability();
        };
        AutomationProperties.SetAutomationId(settingsPresetDetails, "ConfigurationSettingsPresetDetails");
        var content = new StackPanel();
        content.Children.Add(LabeledSettingsRow("Named preset", settingsPresetPicker));
        var actions = new WrapPanel();
        void Add(string text, string id, Func<Task> action)
        {
            var button = Ui.Button(text, () => Run(action));
            AutomationProperties.SetAutomationId(button, id);
            button.IsEnabled = false; settingsPresetActions.Add(button); actions.Children.Add(button);
        }
        Add("Save current as…", "ConfigurationSettingsPresetCapture", CaptureSettingsPresetAsync);
        Add("Restore selected…", "ConfigurationSettingsPresetRestore", RestoreSettingsPresetAsync);
        Add("Export selected…", "ConfigurationSettingsPresetExport", ExportSettingsPresetAsync);
        Add("Import preset…", "ConfigurationSettingsPresetImport", ImportSettingsPresetAsync);
        content.Children.Add(actions);
        var refresh = Ui.Button("Refresh presets", () => Run(RefreshSettingsPresetsAsync));
        AutomationProperties.SetAutomationId(refresh, "ConfigurationSettingsPresetRefresh");
        content.Children.Add(refresh);
        content.Children.Add(settingsPresetDetails);
        content.Children.Add(settingsLayoutStatus);
        return SettingsSection("Named settings presets",
            "Save Cerebral preferences, participating plugin settings and supported window layout together. Portable exports include declared nonsecret settings; unavailable plugin settings remain pending.", content);
    }

    private SettingsPresetClient CaptureSettingsPresetClient()
    {
        var captured = Client; var connection = queueConnectionEpoch;
        return new((operation, arguments, token) => captured.CommandAsync(operation, arguments, cancellationToken: token),
            () => IsConnected && connection == queueConnectionEpoch && ReferenceEquals(captured, Client), settingsLayoutBackendIdentity);
    }

    private async Task RefreshSettingsPresetsAsync()
    {
        if (refreshingSettingsPresets || !IsConnected) return;
        refreshingSettingsPresets = true; SetSettingsPresetAvailability();
        var selectedId = (settingsPresetPicker.SelectedItem as SettingsPresetSummary)?.Id;
        try
        {
            var session = settingsPresetClient;
            if (session is null || !session.IsCurrent)
            {
                if (session?.Uncertainty is { } prior)
                    settingsPresetDetails.Text = $"The previous service change for {prior.Name} remains unconfirmed; it will not be resent.";
                if (session?.UnconfirmedHistory.Count > 0)
                {
                    if (retiredSettingsPresetSessions.Count >= 8)
                        throw new InvalidOperationException("This window retains unconfirmed changes from eight earlier service sessions. Inspect those original services before opening a fresh window.");
                    retiredSettingsPresetSessions.Add(session);
                }
                session = CaptureSettingsPresetClient();
                settingsPresetClient = session;
                settingsPresetCapabilities = null; settingsPresetPicker.ItemsSource = null;
            }
            var capability = await session.DescribeAsync(Lifetime.Token);
            var presets = await session.ListAsync(Lifetime.Token);
            var submitted = session.Uncertainty;
            var current = session.ReconcileFromValidatedList(presets);
            if (!session.IsCurrent || !ReferenceEquals(session, settingsPresetClient)) return;
            settingsPresetCapabilities = capability;
            settingsPresetPicker.ItemsSource = presets.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id).ToArray();
            settingsPresetPicker.SelectedItem = presets.FirstOrDefault(x => x.Id == (current?.Id ?? selectedId)) ?? presets.FirstOrDefault();
            await HydrateSettingsLayoutAsync(session);
            UpdateSettingsPresetDetails();
            if (retiredSettingsPresetSessions.Count > 0)
                settingsPresetDetails.Text += $"\nUnconfirmed change identities from {retiredSettingsPresetSessions.Count} earlier service session(s) are retained; none will be replayed.";
            if (submitted is not null && session.LastReconciliationWasAbsent)
                SetStatus($"No current preset is listed for {submitted.Name}. The original change could still commit later. Its identity remains retained and spent; a new change uses a new identity.");
            else if (current is not null) SetStatus($"Current stored preset {current.Name}, revision {current.Revision}, was read. The lost reply was not replayed.");
        }
        catch (SettingsPresetsUnavailableException)
        {
            settingsPresetCapabilities = null;
            settingsPresetDetails.Text = "Named settings presets are unavailable on this service. The existing configuration backup and restore actions remain available.";
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            settingsPresetDetails.Text = "Named presets could not be refreshed. Existing backup and restore remain available. " + error.Message;
        }
        finally { refreshingSettingsPresets = false; SetSettingsPresetAvailability(); }
    }

    private void UpdateSettingsPresetDetails()
    {
        if (settingsPresetClient?.Uncertainty is { } unknown)
        {
            settingsPresetDetails.Text = $"Change unconfirmed for {unknown.Name}. Refresh on the original service before making another change.";
            SetSettingsPresetAvailability(); return;
        }
        if (settingsPresetPicker.SelectedItem is not SettingsPresetSummary selected)
        {
            settingsPresetDetails.Text = settingsPresetCapabilities is null ? "Connect to load named settings presets." : "No named preset is selected. Save the current settings or import a portable preset.";
            SetSettingsPresetAvailability(); return;
        }
        var coverage = selected.Coverage;
        var capture = coverage.CapturedComplete ? "Saved settings already stored in Cerebral" : "Imported saved settings";
        var portable = coverage.PortableComplete ? "Portable export includes all eligible portable settings" : $"Export leaves out {coverage.OmittedPortableDocuments} plugin settings group(s); review the omissions when exporting";
        settingsPresetDetails.Text = $"{capture}. {portable}. Settings awaiting compatible plugin support: {coverage.PendingPluginDocuments}.\nPlugins that still save settings in separate files may need preset support.\nUpdated {selected.UpdatedAt.ToLocalTime():g} · revision {selected.Revision}.";
        SetSettingsPresetAvailability();
    }

    private void SetSettingsPresetAvailability()
    {
        var ready = IsConnected && settingsPresetCapabilities is not null && settingsPresetClient?.IsCurrent == true &&
            settingsPresetClient.Uncertainty is null && !refreshingSettingsPresets && !changingSettingsPreset;
        for (var index = 0; index < settingsPresetActions.Count; index++)
            settingsPresetActions[index].IsEnabled = ready && (index is 0 or 3 || settingsPresetPicker.SelectedItem is SettingsPresetSummary) &&
                (index != 0 || !persistCentralSettingsLayout || settingsLayoutSession is { Supported: true, Blocked: false });
        settingsPresetPicker.IsEnabled = !refreshingSettingsPresets && !changingSettingsPreset;
    }

    private SettingsPresetClient RequireSettingsPresetClient()
    {
        var client = settingsPresetClient;
        if (client is null || !client.IsCurrent || settingsPresetCapabilities is null)
            throw new InvalidOperationException("Refresh presets for the intended service before making this change.");
        return client;
    }

    private SettingsPresetSummary SelectedSettingsPreset() => settingsPresetPicker.SelectedItem as SettingsPresetSummary
        ?? throw new InvalidOperationException("Select a named settings preset first.");

    private async Task CaptureSettingsPresetAsync()
    {
        var client = RequireSettingsPresetClient();
        var name = PromptSettingsPresetName("Save current settings as", settingsPresetDraftName);
        if (name is null) return;
        settingsPresetDraftName = name;
        if (!client.IsCurrent || !ReferenceEquals(client, settingsPresetClient))
            throw new InvalidOperationException("The connection changed. The preset name is retained; open Save again for the intended service.");
        var matches = (settingsPresetPicker.ItemsSource as IEnumerable<SettingsPresetSummary> ?? [])
            .Where(x => x.Name == name).ToArray();
        var selected = settingsPresetPicker.SelectedItem as SettingsPresetSummary;
        if (matches.Length > 1 && selected?.Name != name) throw new InvalidOperationException("More than one preset has that name. Select the intended preset or use a new name.");
        var existing = selected?.Name == name ? selected : matches.SingleOrDefault();
        if (existing is not null && !Confirm($"Replace the saved settings in {existing.Name}, revision {existing.Revision}?")) return;
        await ChangeSettingsPresetAsync(async () =>
        {
            await FlushSettingsLayoutAsync(client);
            var saved = await client.CaptureAsync(existing?.Id ?? Guid.NewGuid().ToString("N"), name, existing?.Revision ?? 0, Lifetime.Token);
            await RefreshSettingsPresetsAsync();
            SelectSettingsPreset(saved.Id);
            settingsPresetDraftName = "";
            SetStatus($"Settings preset {saved.Name} saved at revision {saved.Revision}.");
        });
    }

    private async Task ImportSettingsPresetAsync()
    {
        var client = RequireSettingsPresetClient();
        var selectedFile = SelectSettingsPresetFile(export: false);
        if (selectedFile is null) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Import again.");
        await using var file = new FileStream(selectedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > ConfigurationBackupContract.MaximumUtf8Bytes) throw new InvalidDataException("The preset exceeds the supported size.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, Lifetime.Token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        // Purpose supplies labels only; Core still validates the unchanged original file.
        var importedPurpose = SettingsExportReview.Purpose(CerebralJson.Read<ConfigurationBackupDocument>(document.RootElement));
        var importedLabel = importedPurpose == SettingsTransferPurpose.Complete ? "Complete settings preset" : "Portable preset";
        var name = PromptSettingsPresetName("Name the imported settings preset", Path.GetFileNameWithoutExtension(selectedFile));
        if (name is null) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Import again.");
        // Original JSON crosses the service boundary intact. Desktop parsing cannot remove unknown fields before strict admission.
        await ChangeSettingsPresetAsync(async () =>
        {
            var saved = await client.ImportAsync(Guid.NewGuid().ToString("N"), name, 0, document.RootElement, Lifetime.Token);
            await RefreshSettingsPresetsAsync(); SelectSettingsPreset(saved.Id);
            SetStatus($"{importedLabel} {saved.Name} imported. It has not been applied.");
        });
    }

    private async Task ExportSettingsPresetAsync()
    {
        var client = RequireSettingsPresetClient(); var selected = SelectedSettingsPreset();
        var availability = await client.DescribeCompleteSettingsAsync(Lifetime.Token);
        var purpose = ChooseSettingsExportPurpose(availability);
        if (purpose is null) return;
        var exported = await client.ExportAsync(selected.Id, purpose, Lifetime.Token);
        var description = $"Export {selected.Name}. " + SettingsExportReview.Description(exported);
        var title = purpose == SettingsTransferPurpose.Complete ? "Review complete settings export" : "Review portable settings export";
        if (!ReviewSettingsPreset(title, exported.Attention, description, "Save export…", PresetCoreReviewText(exported.Document))) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Export again.");
        var selectedFile = SelectSettingsPresetFile(export: true, purpose);
        if (selectedFile is null) return;
        if (!client.IsCurrent) throw new InvalidOperationException("The connection changed. Open Export again.");
        await SaveConfigurationDocumentAsync(exported.Document, selectedFile);
        SetStatus($"{SettingsExportReview.Label(purpose)} for {selected.Name} exported.");
    }

    private async Task RestoreSettingsPresetAsync()
    {
        if (pendingRestore?.Operation.Snapshot.Uncertain == true) { await CheckLastRestore(); return; }
        var session = RequireSettingsPresetClient(); var selected = SelectedSettingsPreset();
        var connection = queueConnectionEpoch; var ownerClient = Client; var client = CreateIndependentClient(); var transferred = false;
        try
        {
            var portable = await session.ExportAsync(selected.Id, Lifetime.Token);
            var checkedSelection = await session.GetAsync(selected.Id, Lifetime.Token);
            if (checkedSelection.Revision != selected.Revision)
                throw new InvalidOperationException("The selected preset changed. Refresh and review its current revision.");
            var preview = await session.PreviewAsync(selected, Lifetime.Token);
            var summary = preview.Settings;
            var detail = $"Restore {selected.Name}, revision {selected.Revision}. Review the included public instructions, paths and grants below. The saved preset keeps its original settings purpose; this public review copy does not change it. Existing model files and domain content stay in their owned stores. Finish active work and unload models before applying.";
            if (summary is not null)
                detail += $"\nPlugin documents to apply: {summary.AppliedPluginDocuments}; pending: {summary.PendingPluginDocuments}; layouts: {summary.RestoredLayouts}.";
            if (!ReviewSettingsPreset("Review settings preset restore", preview.Attention, detail, "Apply preset", PresetCoreReviewText(portable.Document))) return;
            if (!session.IsCurrent || connection != queueConnectionEpoch || !ReferenceEquals(ownerClient, Client))
                throw new InvalidOperationException("The connection changed. This preset review was not applied.");
            transferred = true;
            await ApplyReviewedConfigurationRestoreAsync(preview, client, connection, ownerClient);
            if (pendingRestore?.Operation.Snapshot.Result is null) return;
            try
            {
                await HydrateSettingsLayoutAsync(session, force: true);
                await RefreshSettingsPresetsAsync();
            }
            catch (Exception error)
            {
                SetStatus("Settings committed, but the window or preset display could not refresh: " + error.Message, true);
            }
        }
        finally { if (!transferred) client.Dispose(); }
    }

    private async Task ChangeSettingsPresetAsync(Func<Task> action)
    {
        changingSettingsPreset = true; SetSettingsPresetAvailability();
        try { await action(); }
        finally { changingSettingsPreset = false; UpdateSettingsPresetDetails(); }
    }

    private void SelectSettingsPreset(string id)
    {
        settingsPresetPicker.SelectedItem = (settingsPresetPicker.ItemsSource as IEnumerable<SettingsPresetSummary>)?.FirstOrDefault(x => x.Id == id);
    }

    private string? PromptSettingsPresetName(string title, string initial)
    {
        var input = Ui.Input(initial); input.MaxLength = SettingsPreferenceContract.MaximumNameCharacters;
        var error = Ui.Text("", 12, Ui.ErrorText);
        var dialog = new Window { Owner = this, Icon = this.Icon, Title = title, Width = 480, Height = 260, MinWidth = 340, MinHeight = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var submit = Ui.Button("Save", () => dialog.DialogResult = true, true);
        void Validate()
        {
            try { SettingsPresetClient.NormalizeName(input.Text); error.Text = ""; submit.IsEnabled = true; }
            catch (ArgumentException exception) { error.Text = exception.Message; submit.IsEnabled = false; }
        }
        input.TextChanged += (_, _) => Validate();
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Ui.Text(title, 20)); panel.Children.Add(new Label { Content = "Preset name", Target = input });
        panel.Children.Add(input); panel.Children.Add(error);
        panel.Children.Add(Ui.Bar(submit, Ui.Button("Cancel", () => dialog.DialogResult = false)));
        dialog.Content = panel; WindowSizing.Attach(dialog); Validate();
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? SettingsPresetClient.NormalizeName(input.Text) : null;
    }

    private bool ReviewSettingsPreset(string title, IReadOnlyList<ConfigurationBackupAttention> attention, string description, string action, string? coreReview = null) =>
        CreateSettingsPresetReviewDialog(title, attention, description, action, coreReview, this).ShowDialog() == true;

    private static Window CreateSettingsPresetReviewDialog(string title, IReadOnlyList<ConfigurationBackupAttention> attention,
        string description, string action, string? coreReview, Window? owner)
    {
        var dialog = new Window { Owner = owner, Icon = owner?.Icon ?? Branding.IconFor("Integrator"), Title = title, Width = 660, Height = 440, MinWidth = 440, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel();
        var reviewed = new CheckBox { Content = "I reviewed the settings scope, instructions, paths and access grants.", Margin = new Thickness(0, 14, 0, 12) };
        var submit = Ui.Button(action, () => dialog.DialogResult = true, true); submit.IsEnabled = false;
        reviewed.Checked += (_, _) => submit.IsEnabled = true; reviewed.Unchecked += (_, _) => submit.IsEnabled = false;
        footer.Children.Add(reviewed); footer.Children.Add(Ui.Bar(submit, Ui.Button("Cancel", () => dialog.DialogResult = false)));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(Ui.Text(title, 22)); content.Children.Add(Ui.Text(description, 13));
        foreach (var item in attention) content.Children.Add(Ui.Text("• " + item.Message + (item.Id is { Length: > 0 } id ? " (" + id + ")" : ""), 12, Ui.Muted));
        if (coreReview is not null)
            content.Children.Add(new Expander { Header = "Included core preferences, instructions, paths and grants",
                Content = new TextBox { Text = coreReview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        WindowSizing.Attach(dialog);
        return dialog;
    }

    internal static string PresetCoreReviewText(ConfigurationBackupDocument document, ConfigurationRestorePathOverrides? restorePaths = null) => SettingsExportReview.CoreText(document, restorePaths);

    private string? SelectSettingsPresetFile(bool export, string? purpose = null)
    {
        if (isolatedSettingsPresetFixture)
            return (settingsPresetFixtureFilePicker ?? throw new InvalidOperationException("The owned fixture has no explicit file selection."))(export);
        if (export)
        {
            var picker = new SaveFileDialog { Title = purpose == SettingsTransferPurpose.Complete ? "Export complete settings" : "Export a portable settings preset", Filter = "Cerebral configuration (*.json)|*.json",
                FileName = purpose == SettingsTransferPurpose.Complete ? "Pegasus-Cerebral-complete-settings.json" : "Pegasus-Cerebral-settings.json", AddExtension = true, OverwritePrompt = true };
            return picker.ShowDialog(this) == true ? picker.FileName : null;
        }
        var input = new OpenFileDialog { Title = "Import a portable settings preset", Filter = "Cerebral configuration (*.json)|*.json", CheckFileExists = true };
        return input.ShowDialog(this) == true ? input.FileName : null;
    }

    private async Task SaveConfigurationDocumentAsync(ConfigurationBackupDocument document, string selectedPath)
    {
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false });
        if (Encoding.UTF8.GetByteCount(json) > ConfigurationBackupContract.MaximumUtf8Bytes)
            throw new InvalidDataException("The portable configuration exceeds the supported size.");
        var path = Path.GetFullPath(selectedPath); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), Lifetime.Token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
