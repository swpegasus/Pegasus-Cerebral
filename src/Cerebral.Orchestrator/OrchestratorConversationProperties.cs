using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private bool conversationPropertiesOpen;

    private bool CanOpenConversationProperties(string? id) => id is not null &&
        !exactWorkflowConversations.ContainsKey(id) &&
        (durableConversations.ContainsKey(id) ||
         !showingLegacyNavigation && !activeJobs.Any(x => x.SessionId == id) &&
         (id != CurrentConversationId || IsNativeChat && nativeChat?.Snapshot?.RunId is not null));

    private void AddConversationPropertiesMenu(ContextMenu menu, string? id)
    {
        var available = CanOpenConversationProperties(id);
        var item = new MenuItem { Header = "Properties…", IsEnabled = available,
            ToolTip = id is not null && exactWorkflowConversations.ContainsKey(id)
                ? "Ordinary-chat Properties are unavailable for this exact workflow. Review its saved run in Work."
                : available ? "View this saved chat's settings and current permissions."
                : "Properties require a saved native chat. A new draft has no saved Properties yet." };
        ToolTipService.SetShowOnDisabled(item, true);
        AutomationProperties.SetHelpText(item, (string)item.ToolTip);
        AutomationProperties.SetAutomationId(item, "ConversationProperties");
        AutomationProperties.SetName(item, "Chat properties");
        item.Click += (_, _) => { if (id is not null) Run(() => OpenConversationPropertiesAsync(id)); };
        menu.Items.Add(item);
    }

    private async Task OpenConversationPropertiesAsync(string id)
    {
        EnsureConversationControlsSettled();
        if (!CanOpenConversationProperties(id))
            throw new InvalidOperationException("Properties require a saved native chat. This draft has no saved Properties yet.");
        if (conversationPropertiesOpen) throw new InvalidOperationException("Close the current Properties window first.");
        conversationPropertiesOpen = true;
        try
        {
            // Target identity is independent of the selected chat. Keep this owner client and its
            // connection/binding through the modal dispatcher pump; never save through a replacement client.
            var binding = PluginBinding();
            var connection = ServiceConnection;
            var identity = ServiceConnectionIdentity;
            var source = knowledgeConnectionVersion;
            bool Current() => !Lifetime.IsCancellationRequested && IsConnected &&
                ReferenceEquals(connection, ServiceConnection) && identity == ServiceConnectionIdentity &&
                source == knowledgeConnectionVersion && binding == PluginBinding();
            using var captured = CreateIndependentClient();
            using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            readLifetime.CancelAfter(TimeSpan.FromSeconds(30));
            var saved = await captured.GetAsync<ConversationPropertiesSnapshot>(
                "conversations.properties.get", new { id }, cancellationToken: readLifetime.Token);
            ConversationPropertiesSnapshot.Validate(saved, id);
            if (!Current()) throw new InvalidOperationException("The connection or selected chat changed. Reopen Properties.");
            using var dialog = new ConversationPropertiesDialog(saved, Current, async (expectedRevision, share, token) =>
            {
                // The window also checks before dispatch. This boundary keeps the captured owner route exact.
                if (!Current()) throw new InvalidOperationException("The connection or selected chat changed. Reopen Properties.");
                return await captured.CommandAsync("conversations.properties.save",
                    new { id, expectedRevision, responseSharePercent = share }, cancellationToken: token);
            }, Lifetime.Token, navigation.Nodes.FirstOrDefault(node => node.Kind == "project" && node.Id == saved.ProjectId)?.Title,
            saveWorkingDirectory: async (expectedRevision, workingDirectory, token) =>
            {
                if (!Current()) throw new InvalidOperationException("The connection or selected chat changed. Reopen Properties.");
                return await captured.CommandAsync("conversations.properties.save",
                    new { id, expectedRevision, responseSharePercent = saved.ResponseSharePercent, workingDirectory },
                    cancellationToken: token);
            })
            { Owner = this, Icon = Icon };
            dialog.AttachPathEditor(token => new ConversationPathGrantsEditor(saved.Id, saved.ProfileId,
                saved.ProjectId, saved.RootState, saved.Root, () => dialog.ChildSettingsBindingCurrent,
                (target, cancellation) => captured.GetAsync<JsonElement>("conversations.paths.get",
                    new { id = target }, cancellationToken: cancellation),
                async (target, expectedRevision, grants, cancellation) =>
                {
                    if (!Current()) throw new InvalidOperationException("The Properties binding changed. Reopen it before saving paths.");
                    return await captured.CommandAsync("conversations.paths.save",
                        new { id = target, expectedRevision, grants }, cancellationToken: cancellation);
                }, token, dialog.EditWorkingDirectory));
            dialog.AttachChatPluginInstallEditor((bindingCurrent, token) =>
                ChatPluginInstallOwnerUi.Create(saved.Id, saved.ProfileId, saved.ProjectId,
                    captured, bindingCurrent, token));
            dialog.AttachPluginSettings(ServiceIsLocal
                ? desktopPlugins?.ConversationSettings ?? []
                : [], ServiceIsLocal ? null : "Per-chat plugin settings require a local owner connection.");
            try { dialog.ShowDialog(); }
            finally
            {
                // Drain even when a recoverable dialog handler fails, before its captured client is released.
                dialog.Dispose(); await dialog.PendingOperations;
            }
            if (dialog.Accepted is { } accepted && Current() && id == CurrentConversationId && IsNativeChat)
            {
                AcceptNativePreferenceSnapshot(accepted, source);
                selectedResponseSharePercent = accepted.ResponseSharePercent;
                nativePreferencePending = false; nativePreferenceEditGeneration++;
                pendingSubmission = null;
                SaveChatResponseBalance();
                RefreshConversationTokenBalance();
            }
        }
        finally { conversationPropertiesOpen = false; }
    }
}

/// <summary>Private owner-command projection, independent of the stable desktop plugin SDK.</summary>
internal sealed record ConversationPropertiesSnapshot(string Id, string Title, string ProfileId, string ProfileName,
    string ProfileRole, string? ProjectId, string RootState, string? Root, IReadOnlyList<string> CurrentRoots,
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Tools, bool CurrentFullAccess, string PropertyState,
    string SettingsSource, long Revision, DateTimeOffset? UpdatedAt, int? ResponseSharePercent)
{
    internal static void Validate(ConversationPropertiesSnapshot? value, string id)
    {
        if (value is null || value.Id != id || !Text(value.Id, 128) || !Text(value.Title, 1024) ||
            !Text(value.ProfileId, 128) || !Text(value.ProfileName, 1024) || !Text(value.ProfileRole, 64) ||
            value.ProjectId is { } project && !Text(project, 128) ||
            value.RootState is not ("bound" or "unbound") ||
            value.RootState == "bound" && !Text(value.Root, 32768) ||
            value.RootState == "unbound" && value.Root is not null ||
            value.PropertyState is not ("default" or "saved") ||
            value.SettingsSource != (value.ResponseSharePercent is null ? "profile" : "conversation") ||
            value.Revision < 0 || value.ResponseSharePercent is < 5 or > 50 ||
            value.PropertyState == "default" && (value.Revision != 0 || value.UpdatedAt is not null || value.ResponseSharePercent is not null) ||
            value.PropertyState == "saved" && (value.Revision < 1 || value.UpdatedAt is null) ||
            !List(value.CurrentRoots, 32768) || !List(value.Tools, 256) || !List(value.Capabilities, 256) ||
            JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options).Length > 512 * 1024)
            throw new InvalidDataException("The service returned invalid or mismatched chat Properties. No settings were accepted.");
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Contains('\0');
    private static bool List(IReadOnlyList<string>? values, int maximumText) => values is not null &&
        values.Count <= 1024 && values.All(value => Text(value, maximumText));
}

/// <summary>Bound native chat editor with independently saved core, path and plugin settings.</summary>
internal sealed class ConversationPropertiesDialog : Window, IDisposable
{
    private readonly ConversationPropertiesSnapshot saved;
    private readonly string? projectName;
    private readonly Func<bool> current;
    private readonly Func<long, int?, CancellationToken, Task<CommandResult>> save;
    private readonly Func<long, string?, CancellationToken, Task<CommandResult>>? saveWorkingDirectory;
    private readonly CancellationTokenSource lifetime;
    private readonly DispatcherTimer guardTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool invalidBinding;
    private bool locked;
    private bool saving;
    private bool disposed;
    private bool closed;
    private bool workingDirectoryInvalidated;
    private StackPanel settingsPanel = null!;
    private StackPanel permissionsPanel = null!;
    private ConversationPathGrantsEditor? pathEditor;
    private ChatPluginInstallPermissionEditor? chatPluginInstallEditor;
    private readonly List<ConversationPluginSettingsEditor> pluginEditors = [];
    internal TabControl PropertyTabs { get; } = new() { BorderBrush = Ui.Line, Background = Ui.Surface };
    internal CheckBox AutomaticResponse { get; } = new() { Content = "Use the profile's response balance" };
    internal ComboBox ResponseShare { get; } = new() { MinWidth = 160, MaxWidth = 220 };
    internal Button SaveButton { get; }
    internal TextBox WorkingDirectoryInput { get; } = Ui.Input("");
    internal Button SaveWorkingDirectoryButton { get; }
    internal Button CopyReferenceButton { get; } = Ui.Button("Copy reference", static () => { });
    internal TextBlock Feedback { get; } = Ui.Text("", 13);
    internal ConversationPropertiesSnapshot? Accepted { get; private set; }
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<ConversationPluginSettingsEditor> PluginSettingsEditors => pluginEditors;
    internal bool ChildSettingsBindingCurrent => !invalidBinding && !disposed && !workingDirectoryInvalidated && current();
    internal Task PendingOperations => Task.WhenAll(PendingSave, PendingSectionOperations);
    private Task PendingSectionOperations => Task.WhenAll(new[]
    {
        chatPluginInstallEditor?.PendingOperations ?? Task.CompletedTask,
        pathEditor?.PendingLoad ?? Task.CompletedTask,
        pathEditor?.PendingSave ?? Task.CompletedTask
    }.Concat(pluginEditors.SelectMany(editor => new[] { editor.PendingLoad, editor.PendingSave, editor.PendingCleanup })));


    internal ConversationPropertiesDialog(ConversationPropertiesSnapshot saved, Func<bool> current,
        Func<long, int?, CancellationToken, Task<CommandResult>> save, CancellationToken ownerLifetime, string? projectName = null,
        Func<long, string?, CancellationToken, Task<CommandResult>>? saveWorkingDirectory = null)
    {
        ConversationPropertiesSnapshot.Validate(saved, saved.Id);
        this.saved = saved; this.current = current; this.save = save;
        this.saveWorkingDirectory = saveWorkingDirectory;
        this.projectName = !string.IsNullOrWhiteSpace(projectName) && projectName.Length <= 1024 && !projectName.Any(char.IsControl)
            ? projectName : null;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        Title = "Chat Properties · " + saved.Title;
        Width = 760; Height = 650; MinWidth = 480; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "ConversationPropertiesWindow");
        AutomationProperties.SetAutomationId(PropertyTabs, "ConversationPropertiesTabs");
        AutomationProperties.SetAutomationId(AutomaticResponse, "ConversationPropertiesAutomaticResponse");
        AutomationProperties.SetAutomationId(ResponseShare, "ConversationPropertiesResponseShare");
        AutomationProperties.SetAutomationId(Feedback, "ConversationPropertiesFeedback");
        ResponseShare.ItemsSource = Enumerable.Range(5, 46).ToArray();
        ResponseShare.SelectedItem = saved.ResponseSharePercent ?? 25;
        AutomaticResponse.IsChecked = saved.ResponseSharePercent is null;
        AutomaticResponse.Checked += (_, _) => RefreshControls();
        AutomaticResponse.Unchecked += (_, _) => RefreshControls();
        SaveButton = Ui.Button("Save response balance", () => { _ = SaveAsync(); }, true);
        AutomationProperties.SetAutomationId(SaveButton, "ConversationPropertiesSave");
        WorkingDirectoryInput.Text = saved.Root ?? "";
        WorkingDirectoryInput.MaxLength = 2048;
        AutomationProperties.SetName(WorkingDirectoryInput, "Working folder");
        AutomationProperties.SetAutomationId(WorkingDirectoryInput, "ConversationPropertiesWorkingDirectory");
        SaveWorkingDirectoryButton = Ui.Button("Save working folder", () => { _ = SaveWorkingDirectoryAsync(); }, true);
        AutomationProperties.SetAutomationId(SaveWorkingDirectoryButton, "ConversationPropertiesWorkingDirectorySave");
        WorkingDirectoryInput.TextChanged += (_, _) => RefreshControls();
        var close = Ui.Button("Close", Close);
        AutomationProperties.SetAutomationId(close, "ConversationPropertiesClose");
        close.IsCancel = true;
        AddTab("General", General());
        AddTab("Settings", Settings());
        AddTab("Permissions", Permissions());
        PropertyTabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, PropertyTabs)) return;
            RefreshControls();
            if (PropertyTabs.SelectedIndex == 2 && pathEditor is not null) _ = pathEditor.LoadAsync();
        };
        PropertyTabs.SelectedIndex = 0;
        var body = new DockPanel { Margin = new Thickness(20) };
        var heading = Ui.Text(saved.Title, 22); DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(Feedback); footer.Children.Add(Ui.Bar(SaveButton, close));
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer); body.Children.Add(PropertyTabs);
        Content = body;
        guardTimer.Tick += (_, _) => RefreshControls();
        Loaded += (_, _) => { RefreshControls(); guardTimer.Start(); };
        Closed += OnDialogClosed;
        WindowSizing.Attach(this);
        RefreshControls();
    }

    private void AddTab(string title, StackPanel content)
    {
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(16) };
        var tab = Ui.TabPage(title, scroll);
        AutomationProperties.SetAutomationId(tab, "ConversationProperties." + title);
        PropertyTabs.Items.Add(tab);
    }

    private StackPanel General()
    {
        var panel = new StackPanel();
        Field(panel, "Name", saved.Title);
        Field(panel, "Agent", saved.ProfileName);
        Field(panel, "Project",
            saved.ProjectId is null ? "General chat · no project" : projectName ?? "Project name unavailable · see Details");
        panel.Children.Add(Ui.Text("Working folder", 12, Ui.Muted));
        panel.Children.Add(WorkingDirectoryInput);
        panel.Children.Add(Ui.Text("Enter the full location, including your folder name, or leave empty for no working folder. You can chat without a folder; Cerebral asks before file creation or writing needs one.", 13, Ui.Muted));
        panel.Children.Add(SaveWorkingDirectoryButton);
        panel.Children.Add(Ui.Text("Saving applies to new work and does not move existing files. A paused file request is not resumed: save the folder, then submit a new request. Permissions still apply. Reopen Properties before editing other sections.", 13, Ui.Muted));
        var reference = ChatReference.Create(saved.Id);
        Field(panel, "Chat reference", reference ?? "This chat has an exact non-GUID ID; a GUID reference is unavailable.");
        AutomationProperties.SetAutomationId(CopyReferenceButton, "ConversationPropertiesCopyReference");
        CopyReferenceButton.Click += (_, _) =>
        {
            RefreshControls();
            if (!CopyReferenceButton.IsEnabled || reference is null) return;
            try { Clipboard.SetText(reference); Feedback.Text = "Chat reference copied."; Feedback.Foreground = Ui.Muted; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { Feedback.Text = BoundedFeedback("The chat reference could not be copied. " + error.Message); Feedback.Foreground = Ui.ErrorText; }
        };
        CopyReferenceButton.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(CopyReferenceButton);
        var details = new StackPanel();
        Field(details, "Chat ID", saved.Id);
        Field(details, "Agent ID", saved.ProfileId);
        Field(details, "Agent role", saved.ProfileRole);
        if (saved.ProjectId is not null) Field(details, "Project ID", saved.ProjectId);
        Field(details, "Settings revision", saved.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(details, "Last settings update", saved.UpdatedAt?.ToLocalTime().ToString("g") ?? "Profile defaults · no chat override saved");
        var expander = new Expander { Header = "Details", Content = details, IsExpanded = false };
        AutomationProperties.SetAutomationId(expander, "ConversationPropertiesGeneralDetails");
        panel.Children.Add(expander);
        return panel;
    }

    private StackPanel Settings()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Response balance", 17));
        panel.Children.Add(Ui.Text("Choose the portion of the context reserved for the response. Saved changes apply to the next accepted assignment; existing work retains its accepted settings.", 13, Ui.Muted));
        AutomaticResponse.Margin = new Thickness(0, 6, 0, 14);
        panel.Children.Add(AutomaticResponse);
        var label = Ui.Text("Response share (%)", 13);
        AutomationProperties.SetLabeledBy(ResponseShare, label);
        panel.Children.Add(label); panel.Children.Add(ResponseShare);
        panel.Children.Add(Ui.Text("An explicit response share can be any whole percentage from 5 through 50.", 13, Ui.Muted));
        settingsPanel = panel;
        return panel;
    }

    private StackPanel Permissions()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Current saved profile permissions", 17));
        panel.Children.Add(Ui.Text("These lists show the profile's current grants. They are read-only here. An accepted run retains its admission snapshot and is also subject to current revocations.", 13, Ui.Muted));
        Field(panel, "Full access", saved.CurrentFullAccess ? "Active on the Worker" : "Inactive on the Worker");
        panel.Children.Add(Ui.Text("These profile grants remain separate from chat path access. Path restrictions govern supported tool operations; they are not an operating-system sandbox for arbitrary scripts or native plugins.", 13, Ui.Muted));
        Field(panel, "Profile path grants", saved.CurrentRoots.Count == 0 ? "None" : string.Join("\n", saved.CurrentRoots));
        Field(panel, "Tool grants", saved.Tools.Count == 0 ? "None" : string.Join("\n", saved.Tools));
        Field(panel, "Capabilities", saved.Capabilities.Count == 0 ? "None" : string.Join("\n", saved.Capabilities));
        permissionsPanel = panel;
        return panel;
    }

    internal void AttachPathEditor(Func<CancellationToken, ConversationPathGrantsEditor> create)
    {
        if (pathEditor is not null) throw new InvalidOperationException("Chat path editor is already attached.");
        pathEditor = create(lifetime.Token);
        permissionsPanel.Children.Insert(0, pathEditor);
        if (PropertyTabs.SelectedIndex == 2) _ = pathEditor.LoadAsync();
        pathEditor.RefreshBinding();
    }

    internal void AttachChatPluginInstallEditor(Func<Func<bool>, CancellationToken, ChatPluginInstallPermissionEditor> create)
    {
        if (chatPluginInstallEditor is not null) throw new InvalidOperationException("Chat installation editor is already attached.");
        chatPluginInstallEditor = create(() => ChildSettingsBindingCurrent, lifetime.Token);
        permissionsPanel.Children.Insert(0, chatPluginInstallEditor);
        chatPluginInstallEditor.RefreshBinding();
    }

    internal void AttachPluginSettings(IReadOnlyList<DesktopHostedConversationSettings> contributions, string? unavailableReason = null)
    {
        if (pluginEditors.Count != 0) throw new InvalidOperationException("Plugin settings are already attached.");
        settingsPanel.Children.Add(Ui.Text("Plugin settings", 17));
        settingsPanel.Children.Add(Ui.Text("Each plugin owns its own draft and Save/Cancel controls. Saving response balance does not save plugin edits.", 13, Ui.Muted));
        if (unavailableReason is not null || contributions.Count == 0)
        {
            settingsPanel.Children.Add(Ui.Text(unavailableReason ?? "No enabled plugin contributes per-chat settings.", 13, Ui.Muted));
            return;
        }
        var context = new DesktopConversationSettingsContext(saved.Id, saved.Title, saved.ProfileId,
            saved.ProjectId, saved.Root, lifetime.Token);
        foreach (var contribution in contributions)
        {
            var section = new ConversationPluginSettingsEditor(contribution, context,
                () => ChildSettingsBindingCurrent, lifetime.Token);
            pluginEditors.Add(section); settingsPanel.Children.Add(section);
        }
    }

    private static void Field(Panel panel, string label, string value)
    {
        panel.Children.Add(Ui.Text(label, 12, Ui.Muted));
        var field = Ui.Input(value, multiline: true);
        field.IsReadOnly = true; field.MinHeight = 32; field.MaxHeight = 150;
        field.Margin = new Thickness(0, 0, 0, 14);
        AutomationProperties.SetName(field, label);
        panel.Children.Add(field);
    }

    internal void RefreshControls()
    {
        if (disposed) return;
        if (!current() || lifetime.IsCancellationRequested) invalidBinding = true;
        if (invalidBinding && !lifetime.IsCancellationRequested) CancelLifetime();
        pathEditor?.RefreshBinding();
        chatPluginInstallEditor?.RefreshBinding();
        foreach (var editor in pluginEditors) editor.RefreshBinding();
        if (invalidBinding && !saving && !locked && Accepted is null)
        {
            Feedback.Text = "The connection or selected chat changed. Close and reopen Properties before saving.";
            Feedback.Foreground = Ui.ErrorText;
        }
        SaveButton.Visibility = PropertyTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !invalidBinding && !locked && !saving;
        WorkingDirectoryInput.IsEnabled = SaveButton.IsEnabled && saveWorkingDirectory is not null;
        SaveWorkingDirectoryButton.IsEnabled = WorkingDirectoryInput.IsEnabled &&
            WorkingDirectoryInput.Text != (saved.Root ?? "");
        CopyReferenceButton.IsEnabled = !invalidBinding && !disposed && ChatReference.Create(saved.Id) is not null;
        AutomaticResponse.IsEnabled = SaveButton.IsEnabled;
        ResponseShare.IsEnabled = SaveButton.IsEnabled && AutomaticResponse.IsChecked != true;
    }

    internal Task SaveAsync()
    {
        if (saving || locked || disposed) return PendingSave;
        PendingSave = SaveCoreAsync(workingDirectory: false);
        return PendingSave;
    }

    internal void EditWorkingDirectory()
    {
        RefreshControls();
        if (!WorkingDirectoryInput.IsEnabled) return;
        PropertyTabs.SelectedIndex = 0;
        WorkingDirectoryInput.Focus();
    }

    internal Task SaveWorkingDirectoryAsync()
    {
        if (saving || locked || disposed || saveWorkingDirectory is null) return PendingSave;
        PendingSave = SaveCoreAsync(workingDirectory: true);
        return PendingSave;
    }

    private async Task SaveCoreAsync(bool workingDirectory)
    {
        RefreshControls();
        if (invalidBinding) return;
        string? root = saved.Root;
        if (workingDirectory)
        {
            try { root = string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text) ? null :
                ConversationPathSnapshot.CanonicalPath(WorkingDirectoryInput.Text); }
            catch (ArgumentException error)
            { Feedback.Text = error.Message; Feedback.Foreground = Ui.ErrorText; return; }
            if (root == saved.Root) return;
            if (!PendingSectionOperations.IsCompleted)
            {
                Feedback.Text = "Wait for the current Properties operation to finish before changing the working folder.";
                Feedback.Foreground = Ui.ErrorText; return;
            }
        }
        int? share;
        if (workingDirectory) share = saved.ResponseSharePercent;
        else if (AutomaticResponse.IsChecked == true) share = null;
        else if (ResponseShare.SelectedItem is int selected && selected is >= 5 and <= 50) share = selected;
        else
        {
            Feedback.Text = "Select a response share from 5 through 50.";
            Feedback.Foreground = Ui.ErrorText;
            return;
        }
        saving = true; RefreshControls();
        Feedback.Text = "Saving this chat's settings…"; Feedback.Foreground = Ui.Muted;
        var dispatched = false;
        try
        {
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            requestLifetime.CancelAfter(TimeSpan.FromSeconds(30));
            requestLifetime.Token.ThrowIfCancellationRequested();
            if (!current()) { invalidBinding = true; return; }
            // A changed root invalidates child editors' captured path/plugin contexts. Preserve
            // their drafts, but require a fresh owner read before any later independent save.
            if (workingDirectory) { workingDirectoryInvalidated = true; RefreshControls(); }
            dispatched = true;
            var result = workingDirectory
                ? await saveWorkingDirectory!(saved.Revision, root, requestLifetime.Token)
                : await save(saved.Revision, share, requestLifetime.Token);
            requestLifetime.Token.ThrowIfCancellationRequested();
            if (!current()) { invalidBinding = true; throw new OperationCanceledException("The Properties target changed during save."); }
            if (!result.Success)
            {
                locked = true;
                Feedback.Text = BoundedFeedback("Settings were rejected. Close and reopen Properties to review the latest state. " + result.Error);
                Feedback.Foreground = Ui.ErrorText;
                return;
            }
            var accepted = CerebralJson.Read<ConversationPropertiesSnapshot>(result.Data);
            ConversationPropertiesSnapshot.Validate(accepted, saved.Id);
            var revision = checked(saved.Revision + (workingDirectory || saved.ResponseSharePercent != share ? 1 : 0));
            if (accepted.Revision != revision || accepted.ResponseSharePercent != share ||
                accepted.ProfileId != saved.ProfileId || accepted.ProjectId != saved.ProjectId ||
                accepted.RootState != (root is null ? "unbound" : "bound") || accepted.Root != root)
                throw new InvalidDataException("The save receipt does not match this chat's settings.");
            Accepted = accepted; locked = true;
            Feedback.Text = workingDirectory
                ? "Working folder saved for new work. Existing files were not moved and paused work was not retried. Reopen Properties before editing other sections."
                : "Saved for this chat's next accepted assignment.";
            Feedback.Foreground = Ui.Muted;
            // Other sections retain their independent drafts until their own Save or Cancel.
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            locked = dispatched;
            Feedback.Text = BoundedFeedback(dispatched
                ? "Save acceptance is unconfirmed. Close and reopen Properties to reload the saved settings before another save. " + error.Message
                : "Settings were not sent. " + error.Message);
            Feedback.Foreground = Ui.ErrorText;
        }
        finally { saving = false; RefreshControls(); }
    }

    private static string BoundedFeedback(string message) => message.Length <= 2_000
        ? message : message[..1_999] + "…";

    private void CancelLifetime()
    {
        try { lifetime.Cancel(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Feedback.Text = "A settings cancellation callback failed. Cleanup will continue.";
            Feedback.Foreground = Ui.ErrorText;
            Console.Error.WriteLine("Properties cancellation callback failed; cleanup continued.");
        }
    }

    private void OnDialogClosed(object? sender, EventArgs args)
    {
        closed = true; guardTimer.Stop();
        if (!disposed) CancelLifetime();
        pathEditor?.Dispose();
        chatPluginInstallEditor?.Dispose();
        foreach (var editor in pluginEditors) editor.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        // WPF retains even unshown windows until Close. Release the window before its lifetime
        // so application shutdown cannot invoke a handler against a disposed cancellation source.
        if (!closed) Close();
        disposed = true; guardTimer.Stop(); Closed -= OnDialogClosed;
        CancelLifetime(); lifetime.Dispose();
    }
}
