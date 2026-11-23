using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly Dictionary<string, TextBox> advancedSettingsValues = new(StringComparer.Ordinal);
    private readonly TextBlock questionSettingsSummary = Ui.Text("", 13, Ui.Muted);
    private readonly ScrollViewer retentionContentScroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly TextBlock setupHint = Ui.Text("", 14, Ui.Muted);
    internal string SetupInventoryTextForChecks => setupHint.Text;
    private readonly TextBlock defaultModelSummary = Ui.Text("Add a model to choose what Pegasus Cerebral uses.", 13, Ui.Muted);
    private readonly TextBlock fullAccessState = Ui.Text("Checking access status…", 14, Ui.Muted);
    private readonly TextBlock fullAccessDetails = Ui.Text("", 13, Ui.Muted);
    private readonly TextBlock fullAccessFailure = Ui.Text("", 13, Ui.ErrorText);
    private readonly CheckBox fullAccessEnabled = new() { Content = "Allow PAO Full Access", IsEnabled = false };
    private Button replaceFullAccessAuthorization = null!;
    private FullAccessStatus? confirmedFullAccess;
    private bool loadingFullAccess, changingFullAccess;
    private bool fullAccessAvailable;
    private readonly ComboBox appearanceTheme = new() { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox messageTimeFormat = new() { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox recentChatLimitInput = Ui.Input();
    private Button? appearanceThemeSettings;
    private readonly TextBlock appearanceThemeSettingsHelp = Ui.Text("Checking the Appearance plugin…", 12, Ui.Muted);
    private readonly CheckBox startWithWindows = new() { Content = "Start Cerebral at sign-in" };
    private readonly TextBlock windowsStartupAvailability = Ui.Text("", 12, Ui.Muted);
    private bool loadingDesktopSettings;

    private UIElement SettingsPage()
    {
        var content = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(startWithWindows, "ConfigurationWindowsStartup");
        System.Windows.Automation.AutomationProperties.SetAutomationId(windowsStartupAvailability, "ConfigurationWindowsStartupAvailability");
        windowsStartupAvailability.TextWrapping = TextWrapping.Wrap;
        windowsStartupAvailability.Visibility = Visibility.Collapsed;
        content.Children.Add(LabeledSettingsRow("Windows startup", startWithWindows,
            "For this Windows user. Changes apply immediately; closing the window keeps Cerebral in the notification area.",
            windowsStartupAvailability));
        startWithWindows.Checked += (_, _) => ChangeWindowsStartup(true);
        startWithWindows.Unchecked += (_, _) => ChangeWindowsStartup(false);
        if (!isolatedSettingsPresetFixture) RefreshWindowsStartup();
        else
        {
            startWithWindows.IsEnabled = false;
            windowsStartupAvailability.Text = "Windows startup is unavailable in this isolated Configuration check.";
            windowsStartupAvailability.Visibility = Visibility.Visible;
            SetSettingsControlHelp(startWithWindows, windowsStartupAvailability.Text);
        }
        return FocusedSettingsPage("General", "Startup preferences for this Windows user.", content);
    }

    private UIElement AppearanceSettingsPage()
    {
        InitializeAppearanceThemePicker();
        var content = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(LabeledSettingsRow("Color mode", appearanceTheme, InterfacePreferenceHelp(InterfacePreferencePolicy.AppearanceTheme)));
        appearanceThemeSettings = Ui.Button("Open theme settings…", () => Run(() => ConfigureDesktopPluginById("pegasus.appearance")));
        appearanceThemeSettings.IsEnabled = false;
        content.Children.Add(LabeledSettingsRow("Theme colors",
            Ui.Bar(appearanceThemeSettings, appearanceThemeSettingsHelp),
            "Color presets are managed by the Appearance plugin. Color mode above remains available independently."));
        RefreshAppearanceThemeAction();
        return FocusedSettingsPage("Appearance", "Appearance preferences for Cerebral and Configuration.", content);
    }

    private UIElement ChatSettingsPage()
    {
        var content = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(LabeledSettingsRow("Message time format", messageTimeFormat, InterfacePreferenceHelp(InterfacePreferencePolicy.MessageTimeFormat)));
        content.Children.Add(LabeledSettingsRow("Recent general chats", recentChatLimitInput,
            InterfacePreferenceHelp(InterfacePreferencePolicy.RecentChatLimit)));
        questionSettingsSummary.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(LabeledSettingsRow("Unanswered questions",
            Ui.Bar(questionSettingsSummary, WorkerSettingsButton("Edit…", EditQuestionSettings)),
            "Worker-wide optional-question behavior. Default: keep waiting. The dialog saves valid changes automatically; Done waits for the save result."));
        RefreshInterfacePreferenceControls();
        return FocusedSettingsPage("Chats", "Message display and question preferences.", content);
    }

    private static FrameworkElement LabeledSettingsRow(string title, FrameworkElement control, string? help = null,
        TextBlock? availability = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new Label { Content = title, Target = control, Padding = new Thickness(0, 4, 14, 0),
            FontSize = 13, Foreground = Ui.Ink, VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetLabeledBy(control, label);
        row.Children.Add(label);
        var field = new StackPanel();
        control.Margin = new Thickness(0);
        field.Children.Add(control);
        if (!string.IsNullOrEmpty(help))
        {
            SetSettingsControlHelp(control, help);
            var hint = Ui.Text(help, 12, Ui.Muted);
            hint.Margin = new Thickness(0, 5, 0, 0);
            field.Children.Add(hint);
        }
        if (availability is not null)
        {
            availability.Margin = new Thickness(0, 5, 0, 0);
            field.Children.Add(availability);
        }
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }

    private UIElement BackupSettingsPage()
    {
        var content = new StackPanel();
        content.Children.Add(SettingsSection("Configuration export and restore",
            "Choose a portable preset or complete settings when supported. Restore previews included preferences and retains a confirmation receipt.",
            Ui.Bar(Ui.Button("Backup configuration…", () => Run(BackupConfiguration)),
                Ui.Button("Restore configuration…", () => Run(RestoreConfiguration)),
                Ui.Button("Check last restore", () => Run(CheckLastRestore)))));
        content.Children.Add(SettingsPresetsContent());
        content.Children.Add(Ui.Text("Named presets include supported plugin preferences and window layout. Complete exports require all eligible preferences to be covered. Credentials, model files, conversations and domain content remain separate.", 13, Ui.Muted));
        content.Children.Add(SettingsSection("Start with fresh defaults",
            "Extract a new portable Cerebral package into a different empty folder and open Start-CerebralPortable.cmd there. The new folder gets its own Data and UserData; your existing Cerebral folder, disabled registrations, models and conversations stay in place. No in-place reset is available while the current Worker owns its data.",
            Ui.Text("To bring selected settings into the fresh workspace, export configuration from the old workspace first. In the new Configuration window, use Restore configuration to preview the file and confirm its exact changes. Leave Cortex unconfigured unless you deliberately choose it in setup.", 13, Ui.Muted)));
        return FocusedSettingsPage("Backup & restore", "Transfer or recover the core configuration.", content);
    }

    private UIElement FoldersSettingsPage()
    {
        var content = new StackPanel();
        content.Children.Add(ReadOnlySettingsRows(
            ("folders.projects", "Projects folder", true),
            ("folders.models", "Model repository", true),
            ("folders.tools", "Portable tools", true),
            ("folders.shared", "Shared files", true),
            ("folders.runtime", "llama-server", true)));
        content.Children.Add(Ui.Bar(WorkerSettingsButton("Edit folders & runtime…", EditRepositorySettings)));
        return FocusedSettingsPage("Folders & runtime", "Paths belong to the Worker. Editing a path does not move its files.", content);
    }

    private UIElement ResourceSettingsPage()
    {
        var content = new StackPanel();
        content.Children.Add(ReadOnlySettingsRows(
            ("resources.ownerQueue", "Owner queue limit", false),
            ("resources.peerQueue", "Peer queue limit", false),
            ("resources.concurrent", "Simultaneous jobs", false),
            ("resources.ownerSlots", "Reserved owner slots", false),
            ("resources.workflowExpiry", "Interrupted workflow expiry", false),
            ("resources.memory", "Memory reserve (MiB)", false),
            ("resources.processors", "Logical processors", true),
            ("resources.processorLimit", "Processor count limit", false),
            ("resources.modelIdle", "Model idle unload", false),
            ("resources.attempts", "Executable attempts", false)));
        content.Children.Add(Ui.Bar(WorkerSettingsButton("Edit resources & queues…", EditQueueSettings)));
        return FocusedSettingsPage("Resources & queues", "Queue, memory and processor limits. Processor changes apply after the Worker restarts.", content);
    }

    private UIElement ReadOnlySettingsRows(params (string Key, string Label, bool Wide)[] definitions)
    {
        var form = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var definition in definitions)
        {
            var value = Ui.Input();
            value.IsReadOnly = true;
            value.IsReadOnlyCaretVisible = true;
            value.Text = "Unavailable";
            SetSettingsControlHelp(value, SettingsFieldHelp(definition.Key) + "\nConnect to a Worker to read its saved value.");
            value.TextWrapping = TextWrapping.NoWrap;
            value.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            value.MinWidth = definition.Wide ? 240 : 120;
            value.MaxWidth = 620;
            value.Width = definition.Wide ? double.NaN : 160;
            value.HorizontalAlignment = definition.Wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetAutomationId(value, "ConfigurationSetting." + definition.Key);
            advancedSettingsValues.Add(definition.Key, value);
            var row = LabeledSettingsRow(definition.Label, value);
            row.Margin = new Thickness(0, 0, 0, 8);
            form.Children.Add(row);
        }
        return form;
    }

    private void SetReadOnlySettingsValue(string key, string value)
    {
        var field = advancedSettingsValues[key];
        field.Text = value;
        SetSettingsControlHelp(field, SettingsFieldHelp(key) + "\nSaved value: " + value);
    }

    private UIElement AccessSettingsPage()
    {
        var content = new StackPanel { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(fullAccessEnabled, "ConfigurationFullAccessEnabled");
        System.Windows.Automation.AutomationProperties.SetAutomationId(fullAccessState, "ConfigurationFullAccessState");
        System.Windows.Automation.AutomationProperties.SetAutomationId(fullAccessDetails, "ConfigurationFullAccessDetails");
        System.Windows.Automation.AutomationProperties.SetAutomationId(fullAccessFailure, "ConfigurationFullAccessFailure");
        fullAccessFailure.Visibility = Visibility.Collapsed;
        fullAccessEnabled.Checked += (_, _) => RequestFullAccessChange(true);
        fullAccessEnabled.Unchecked += (_, _) => RequestFullAccessChange(false);
        content.Children.Add(LabeledSettingsRow("System access", fullAccessEnabled,
            "Allows Cortex-authorized PAO operations beyond normal folder limits. Agent tool and capability permissions still apply."));
        content.Children.Add(fullAccessState);
        fullAccessDetails.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(fullAccessDetails);
        fullAccessFailure.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(fullAccessFailure);
        content.Children.Add(Ui.Bar(
            Ui.Button("Open Cortex tray", () => Run(() => { OpenPaoAccessSetup(); return Task.CompletedTask; })),
            Ui.Button("Refresh status", () => Run(() => RefreshFullAccess()))));
        replaceFullAccessAuthorization = Ui.Button("Replace private authorization…", () => Run(RotateFullAccess));
        replaceFullAccessAuthorization.IsEnabled = false;
        System.Windows.Automation.AutomationProperties.SetAutomationId(replaceFullAccessAuthorization, "ConfigurationReplaceAuthorization");
        content.Children.Add(new Expander
        {
            Header = "Advanced", Margin = new Thickness(0, 18, 0, 0),
            Content = new StackPanel { Children =
            {
                Ui.Text("Replace Cerebral's saved private credential if Cortex was reinstalled or the authorization was revoked. This reconnects the application; it does not change agent permissions.", 13, Ui.Muted),
                replaceFullAccessAuthorization
            } }
        });
        return FocusedSettingsPage("System access",
            "Control Cerebral's authorization to use Cortex system operations on this machine.", content);
    }

    private UIElement RetentionSettingsPage()
    {
        var content = new StackPanel();
        content.Children.Add(ReadOnlySettingsRows(
            ("history.jobs", "Job history limit", false),
            ("history.jobDays", "Job history days", false),
            ("history.tools", "Tool-call history limit", false),
            ("history.toolDays", "Tool-call history days", false),
            ("history.engineEvents", "Engine event limit", false),
            ("history.engineMemory", "Engine activity (MiB)", false),
            ("history.engineDays", "Engine activity days", false),
            ("history.cacheCollection", "Cache collection", false),
            ("history.cacheThreshold", "Cache change (%)", false)));
        content.Children.Add(Ui.Bar(WorkerSettingsButton("Edit history & collection…", EditRetentionSettings)));
        content.Children.Add(PromptCacheDiagnosticsCard());
        retentionContentScroller.Content = content;
        return Ui.Page("History & cache", "Rolling history limits. Conversations and recovery state are retained separately.", retentionContentScroller);
    }

    private static UIElement FocusedSettingsPage(string title, string description, UIElement content) =>
        Ui.Page(title, description, new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

    private static UIElement SettingsSection(string title, string description, params UIElement[] controls)
    {
        var section = new StackPanel();
        section.Children.Add(Ui.Text(title, 18));
        section.Children.Add(Ui.Text(description, 13, Ui.Muted));
        foreach (var control in controls)
        {
            if (control is FrameworkElement element) element.Margin = new Thickness(0, 10, 0, 0);
            section.Children.Add(control);
        }
        return new Border
        {
            Child = section, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 18), Margin = new Thickness(0, 0, 0, 18)
        };
    }

    private void RefreshWindowsStartup()
    {
        loadingDesktopSettings = true;
        try
        {
            startWithWindows.IsEnabled = WindowsStartupRegistration.IsAvailable;
            startWithWindows.IsChecked = WindowsStartupRegistration.IsEnabled();
            var explanation = startWithWindows.IsEnabled
                ? "Launch the installed Cerebral tray instance after this user signs in."
                : "Install Pegasus Cerebral before enabling automatic startup.";
            SetSettingsControlHelp(startWithWindows, explanation);
            windowsStartupAvailability.Text = startWithWindows.IsEnabled ? "" : explanation;
            windowsStartupAvailability.Visibility = startWithWindows.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { loadingDesktopSettings = false; }
    }

    private void ChangeWindowsStartup(bool enabled)
    {
        if (loadingDesktopSettings) return;
        try
        {
            WindowsStartupRegistration.SetEnabled(enabled);
            SetStatus(enabled ? "Pegasus Cerebral will start in the notification area after sign-in." : "Automatic Cerebral startup is off.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            RefreshWindowsStartup();
            SetStatus("Windows startup could not be changed: " + error.Message, true);
        }
    }

    private async Task RefreshFullAccess(bool clearFailure = true)
    {
        try
        {
            var data = await Get<JsonElement>("access.full.status");
            var status = data.Deserialize<FullAccessStatus>(CerebralJson.Options)
                ?? throw new InvalidDataException("The Worker did not return an access status.");
            var available = !data.TryGetProperty("available", out var availability) || availability.GetBoolean();
            var reason = data.TryGetProperty("unavailableReason", out var diagnostic) &&
                diagnostic.ValueKind == JsonValueKind.String ? diagnostic.GetString() : null;
            ApplyFullAccessStatus(status, available, reason);
            if (clearFailure) fullAccessFailure.Visibility = Visibility.Collapsed;
        }
        catch
        {
            SetFullAccessUnavailable();
            throw;
        }
    }

    private void ApplyFullAccessStatus(FullAccessStatus status, bool available = true, string? unavailableReason = null)
    {
        confirmedFullAccess = status;
        fullAccessAvailable = available;
        loadingFullAccess = true;
        try { fullAccessEnabled.IsChecked = status.Active; }
        finally { loadingFullAccess = false; }
        fullAccessEnabled.IsEnabled = available && !changingFullAccess;
        replaceFullAccessAuthorization.IsEnabled = available && status.Active && !changingFullAccess;
        fullAccessState.Text = !available ? "Unavailable for this Worker" : status.Active
            ? status.Remembered ? "Enabled · saved for Worker restarts" : "Enabled · this Worker session"
            : "Disabled · normal folder limits apply";
        fullAccessState.Foreground = status.Active ? Ui.ErrorBorder : Ui.Muted;
        fullAccessState.FontWeight = status.Active ? FontWeights.Bold : FontWeights.Normal;
        fullAccessDetails.Text = unavailableReason ?? status.Message ?? (status.Active
            ? "Cortex authorizes these operations through its private system-service pipe."
            : "Cerebral can continue using its normally permitted tools and project folders.");
        fullAccessState.ToolTip = null;
    }

    private void SetFullAccessUnavailable()
    {
        confirmedFullAccess = null;
        fullAccessAvailable = false;
        loadingFullAccess = true;
        try { fullAccessEnabled.IsChecked = null; }
        finally { loadingFullAccess = false; }
        fullAccessEnabled.IsEnabled = false;
        replaceFullAccessAuthorization.IsEnabled = false;
        fullAccessState.Text = "Access status unavailable";
        fullAccessState.Foreground = Ui.ErrorText;
        fullAccessDetails.Text = "Refresh status after restoring the local Worker connection. No access change is confirmed.";
    }

    private void RequestFullAccessChange(bool enabled)
    {
        if (loadingFullAccess || changingFullAccess || !fullAccessAvailable || confirmedFullAccess is not { } current) return;
        // Restore the last confirmed value before entering the shared operation gate.
        // A busy window or rejected command must not leave an unconfirmed checkbox selected.
        ApplyFullAccessStatus(current);
        if (enabled != current.Active)
            Run(() => ChangeFullAccess(enabled ? "access.full.enable" : "access.full.disable",
                enabled ? "Enabling system access…" : "Disabling system access…"));
    }

    private void OpenPaoAccessSetup()
    {
        var executable = InstalledCortexDiscovery.FindInstalledTray()
            ?? throw new FileNotFoundException("Install Pegasus Cortex before opening its tray.");
        var launch = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        launch.ArgumentList.Add("--dashboard");
        using var process = Process.Start(launch)
            ?? throw new IOException("The Cortex tray could not be opened.");
        SetStatus("Cortex tray opened.");
    }

    private async Task RotateFullAccess()
    {
        if (!fullAccessAvailable || confirmedFullAccess?.Active != true) return;
        if (!Confirm("Replace Cerebral's private Cortex authorization?\n\nThis revokes the previous application credential, saves its replacement, and reconnects Cerebral. Active system operations may be interrupted. Agent permissions and project folders are not changed."))
            return;
        await ChangeFullAccess("access.full.rotate", "Replacing private authorization…");
    }

    private async Task ChangeFullAccess(string operationName, string pendingText)
    {
        changingFullAccess = true;
        fullAccessEnabled.IsEnabled = false;
        replaceFullAccessAuthorization.IsEnabled = false;
        fullAccessFailure.Visibility = Visibility.Collapsed;
        fullAccessState.Text = pendingText;
        try
        {
            var result = await Command(operationName);
            var status = result.Data.Deserialize<FullAccessStatus>(CerebralJson.Options)
                ?? throw new InvalidDataException("The Worker did not return an access status.");
            ApplyFullAccessStatus(status);
            SetStatus(operationName == "access.full.rotate" ? "Private Cortex authorization replaced."
                : status.Active ? "PAO Full Access enabled." : "PAO Full Access disabled.");
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Refresh only observes state. Do not replay authorization changes after a failure.
            try { await RefreshFullAccess(clearFailure: false); }
            catch { SetFullAccessUnavailable(); }
            fullAccessFailure.Text = "System access could not be changed: " + error.Message;
            fullAccessFailure.Visibility = Visibility.Visible;
            SetStatus(fullAccessFailure.Text, true);
        }
        finally
        {
            changingFullAccess = false;
            fullAccessEnabled.IsEnabled = fullAccessAvailable && confirmedFullAccess is not null;
            replaceFullAccessAuthorization.IsEnabled = fullAccessAvailable && confirmedFullAccess?.Active == true;
        }
    }
    private void RefreshDefaultModelPicker()
    {
        if (configuredModels.FirstOrDefault(x => x.Id == settings.DefaultModelId && loadedModelWorkerId == settings.DefaultModelWorkerId) is not { } model)
        {
            defaultModelSummary.Text = string.IsNullOrWhiteSpace(settings.DefaultModelId) ? "Add a model to choose what Pegasus Cerebral uses." : $"Cerebral default: {settings.DefaultModelId} on {ModelWorkerName(settings.DefaultModelWorkerId)}. Open that machine in the model library to view its capacity.";
            return;
        }
        defaultModelSummary.Text = ConciseModelSummary(model) + "\nCurrent Pegasus Cerebral default on " + ModelWorkerName(settings.DefaultModelWorkerId) + ".";
    }
    private void RefreshSettingsSummary()
    {
        SetReadOnlySettingsValue("folders.projects", settings.ProjectsDirectory);
        SetReadOnlySettingsValue("folders.models", settings.ModelDirectory);
        SetReadOnlySettingsValue("folders.tools", settings.ToolsDirectory);
        SetReadOnlySettingsValue("folders.shared", settings.SharedDirectory);
        SetReadOnlySettingsValue("folders.runtime", settings.LlamaServerPath);
        SetReadOnlySettingsValue("resources.ownerQueue", settings.QueueCapacity.ToString("N0"));
        SetReadOnlySettingsValue("resources.peerQueue", settings.PeerQueueCapacity.ToString("N0"));
        SetReadOnlySettingsValue("resources.concurrent", settings.MaxConcurrentJobs.ToString("N0"));
        SetReadOnlySettingsValue("resources.ownerSlots", settings.ReservedOwnerSlots.ToString("N0"));
        SetReadOnlySettingsValue("resources.workflowExpiry", settings.WorkflowIdleExpiryDays == 0
            ? "Disabled" : settings.WorkflowIdleExpiryDays.ToString("N0") + " days");
        SetReadOnlySettingsValue("resources.memory", settings.ReservedMemoryMb.ToString("N0"));
        SetReadOnlySettingsValue("resources.processors", settings.AllowedLogicalProcessors.Count == 0
            ? "All except 0" : string.Join(", ", settings.AllowedLogicalProcessors.Order()));
        SetReadOnlySettingsValue("resources.processorLimit", settings.MaxLogicalProcessorCount == 0
            ? "No additional limit" : settings.MaxLogicalProcessorCount.ToString("N0"));
        SetReadOnlySettingsValue("resources.modelIdle", settings.DefaultModelIdleMinutes == 0
            ? "Never" : settings.DefaultModelIdleMinutes.ToString("N0") + " min");
        SetReadOnlySettingsValue("resources.attempts", settings.MaxExecutableAttemptsPerAssignment.ToString("N0"));
        SetReadOnlySettingsValue("history.jobs", settings.JobHistoryLimit.ToString("N0"));
        SetReadOnlySettingsValue("history.jobDays", settings.JobHistoryDays.ToString("N0"));
        SetReadOnlySettingsValue("history.tools", settings.ActivityHistoryLimit.ToString("N0"));
        SetReadOnlySettingsValue("history.toolDays", settings.ActivityHistoryDays.ToString("N0"));
        SetReadOnlySettingsValue("history.engineEvents", settings.EngineEventHistoryLimit.ToString("N0"));
        SetReadOnlySettingsValue("history.engineMemory", settings.EngineEventHistoryMegabytes.ToString("N0"));
        SetReadOnlySettingsValue("history.engineDays", settings.EngineEventHistoryDays.ToString("N0"));
        SetReadOnlySettingsValue("history.cacheCollection", settings.PromptCacheDiagnosticsEnabled ? "Enabled" : "Disabled");
        SetReadOnlySettingsValue("history.cacheThreshold", settings.PromptCacheDiagnosticsChangePercentagePoints.ToString("N0"));
        questionSettingsSummary.Text = settings.QuestionTimeoutSeconds == 0
            ? "Wait indefinitely"
            : $"{settings.QuestionTimeoutSeconds:N0} seconds · {settings.QuestionTimeoutAction}";
    }

    private async Task EditRepositorySettings()
    {
        var session = await BeginRegistrationEditAsync();
        var current = session.Settings;
        var form = new FieldsDialog(this, "Folders & model runtime", "Paths are on the selected service machine. Changing a folder does not move or duplicate existing files.")
            .Add("projects", "Default projects folder", current.ProjectsDirectory)
            .ActionButton("Browse projects folder…", f => PickFolder(f, "projects"))
            .Add("models", "Model repository folder", current.ModelDirectory)
            .ActionButton("Browse model repository…", f => PickFolder(f, "models"))
            .ActionButton("Use this desktop's existing Cerebral models", f =>
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "Models");
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("No previous desktop model folder was found.");
                f.SetValue("models", path);
            })
            .Add("tools", "Shared portable tools folder", current.ToolsDirectory)
            .ActionButton("Browse portable tools folder…", f => PickFolder(f, "tools"))
            .Add("shared", "Shared files folder", current.SharedDirectory)
            .Add("runtime", "llama-server executable", current.LlamaServerPath)
            .ActionButton("Browse runtime…", f =>
            {
                var picker = new OpenFileDialog { Title = "Select llama-server", Filter = "llama-server|llama-server.exe|All files|*.*" };
                if (picker.ShowDialog(f) == true) f.SetValue("runtime", picker.FileName);
            });
        await ShowAutoSavedSettingsAsync(session, form, baseline =>
        {
            var candidate = CopyHostSettings(baseline);
            candidate.ProjectsDirectory = form.Value("projects"); candidate.ModelDirectory = form.Value("models");
            candidate.ToolsDirectory = form.Value("tools"); candidate.SharedDirectory = form.Value("shared"); candidate.LlamaServerPath = form.Value("runtime");
            return candidate;
        });
    }
    private static void PickFolder(FieldsDialog form, string key)
    {
        var picker = new OpenFolderDialog { Title = "Select folder on this computer", InitialDirectory = Directory.Exists(form.Value(key)) ? form.Value(key) : "" };
        if (picker.ShowDialog(form) == true) form.SetValue(key, picker.FolderName);
    }
    private async Task EditQueueSettings()
    {
        var session = await BeginRegistrationEditAsync();
        var current = session.Settings;
        var form = new FieldsDialog(this, "Resources & queues",
            "The queue contains unfinished work only. Processor changes apply after the Worker restarts. A blank allowed list reserves logical processor 0 for the operating system; enter 0 explicitly if Cerebral may use it.")
            .Add("capacity", "Maximum unfinished owner jobs (including running and waiting)", current.QueueCapacity.ToString())
            .Add("peerCapacity", "Maximum unfinished peer jobs", current.PeerQueueCapacity.ToString())
            .Add("concurrency", "Simultaneous jobs", current.MaxConcurrentJobs.ToString())
            .Add("ownerSlots", "Slots reserved when owner work is waiting", current.ReservedOwnerSlots.ToString())
            .Add("workflowExpiry", "Idle days before interrupted workflows expire (0 = disabled)", current.WorkflowIdleExpiryDays.ToString())
            .Add("processorMaximum", "Maximum logical processors (0 = no additional maximum)", current.MaxLogicalProcessorCount.ToString())
            .Add("processors", "Allowed logical processors (comma-separated; blank = all except 0)", string.Join(", ", current.AllowedLogicalProcessors.Order()))
            .Add("memory", "Memory to keep free for the system (MiB)", current.ReservedMemoryMb.ToString())
            .Add("modelIdle", "Default model idle unload minutes (0 = never)", current.DefaultModelIdleMinutes.ToString())
            .Add("executableAttempts", "Maximum launches of the same executable per assignment", current.MaxExecutableAttemptsPerAssignment.ToString());
        await ShowAutoSavedSettingsAsync(session, form, baseline =>
        {
            var candidate = CopyHostSettings(baseline);
            candidate.QueueCapacity = form.Number("capacity", 1, 1000); candidate.PeerQueueCapacity = form.Number("peerCapacity", 1, 1000);
            candidate.MaxConcurrentJobs = form.Number("concurrency", 1, 16); candidate.ReservedOwnerSlots = form.Number("ownerSlots", 0, 16);
            candidate.WorkflowIdleExpiryDays = form.Number("workflowExpiry", 0, HostSettings.MaximumWorkflowIdleExpiryDays);
            if (candidate.ReservedOwnerSlots > candidate.MaxConcurrentJobs) throw new ArgumentException("Reserved owner slots cannot exceed simultaneous jobs.");
            candidate.MaxLogicalProcessorCount = form.Number("processorMaximum", 0, 1024);
            candidate.AllowedLogicalProcessors = ParseLogicalProcessors(form.Value("processors"));
            candidate.ReservedMemoryMb = form.Number("memory", 0, 1_048_576);
            candidate.DefaultModelIdleMinutes = form.Number("modelIdle", 0, 10_080);
            candidate.MaxExecutableAttemptsPerAssignment = form.Number("executableAttempts", 1, 20);
            return candidate;
        });
    }

    private async Task EditQuestionSettings()
    {
        var session = await BeginRegistrationEditAsync();
        var current = session.Settings;
        var form = new FieldsDialog(this, "Question behavior", "A zero timeout keeps waiting. Timeout behavior applies to unanswered optional questions.")
            .Add("timeout", "Question timeout (seconds; 0 = wait)", current.QuestionTimeoutSeconds.ToString())
            .Choice("action", "Unanswered optional questions", ["wait", "assume"], current.QuestionTimeoutAction);
        await ShowAutoSavedSettingsAsync(session, form, baseline =>
        {
            var candidate = CopyHostSettings(baseline);
            candidate.QuestionTimeoutSeconds = form.Number("timeout", 0, 86_400);
            candidate.QuestionTimeoutAction = form.Selection("action");
            return candidate;
        });
    }

    private async Task EditRetentionSettings()
    {
        var session = await BeginRegistrationEditAsync();
        var current = session.Settings;
        var form = new FieldsDialog(this, "History & collection",
            "Job and tool-call history are rolling views. Conversations and required recovery state are separate. Prompt-cache measurements use rolling 24-hour retention and record only availability or threshold changes.")
            .Add("history", "Maximum jobs shown in history", current.JobHistoryLimit.ToString())
            .Add("days", "Days shown in history", current.JobHistoryDays.ToString())
            .Add("activity", "Maximum recorded tool calls per conversation", current.ActivityHistoryLimit.ToString())
            .Add("activityDays", "Days to keep recorded tool calls", current.ActivityHistoryDays.ToString())
            .Add("engineEvents", "Maximum rolling Engine events per conversation", current.EngineEventHistoryLimit.ToString())
            .Add("engineMegabytes", "Maximum rolling Engine activity per conversation (MiB)", current.EngineEventHistoryMegabytes.ToString())
            .Add("engineDays", "Days to keep rolling Engine activity", current.EngineEventHistoryDays.ToString())
            .Choice("cacheDiagnostics", "Collect optional prompt-cache diagnostics (rolling 24 hours)", ["enabled", "disabled"], current.PromptCacheDiagnosticsEnabled ? "enabled" : "disabled")
            .Add("cacheThreshold", "Cache diagnostic change threshold (1–100%; 5% default)", current.PromptCacheDiagnosticsChangePercentagePoints.ToString());
        await ShowAutoSavedSettingsAsync(session, form, baseline =>
        {
            var candidate = CopyHostSettings(baseline);
            candidate.JobHistoryLimit = form.Number("history", 1, 10_000);
            candidate.JobHistoryDays = form.Number("days", 1, 3650);
            candidate.ActivityHistoryLimit = form.Number("activity", 100, 50_000);
            candidate.ActivityHistoryDays = form.Number("activityDays", 1, 3650);
            candidate.EngineEventHistoryLimit = form.Number("engineEvents", 1, 1_000_000);
            candidate.EngineEventHistoryMegabytes = form.Number("engineMegabytes", 1, 16_384);
            candidate.EngineEventHistoryDays = form.Number("engineDays", 1, 3650);
            candidate.PromptCacheDiagnosticsEnabled = form.Selection("cacheDiagnostics") == "enabled";
            candidate.PromptCacheDiagnosticsChangePercentagePoints = form.Number("cacheThreshold", 1, 100);
            return candidate;
        });
    }

    private static List<int> ParseLogicalProcessors(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Allowed logical processors must be comma-separated processor identifiers.");
        var processors = new List<int>(tokens.Length);
        foreach (var token in tokens)
            if (!int.TryParse(token, out var processor) || processor is < 0 or > 1023)
                throw new ArgumentException("Allowed logical processors use identifiers from 0 through 1023.");
            else processors.Add(processor);
        if (processors.Distinct().Count() != processors.Count)
            throw new ArgumentException("Allowed logical processors cannot contain duplicates.");
        processors.Sort();
        return processors;
    }

    private static HostSettings CopyHostSettings(HostSettings value) =>
        CerebralJson.Read<HostSettings>(CerebralJson.Element(value));

    private async Task ShowAutoSavedSettingsAsync(RegistrationEditSession session, FieldsDialog form,
        Func<HostSettings, HostSettings> buildCandidate)
    {
        await using var autosave = new ExpectedRevisionAutoSave<HostSettings>(CopyHostSettings(session.Settings),
            buildCandidate,
            (candidate, token) => SaveHostSettingsSnapshotAsync(session, candidate, token),
            _ => RefreshSettingsSummary(),
            state =>
            {
                var error = state.Phase is ExpectedRevisionAutoSavePhase.Invalid or ExpectedRevisionAutoSavePhase.Conflict or ExpectedRevisionAutoSavePhase.Failed;
                form.SetInlineStatus(state.Message, error);
            }, cancellationToken: Lifetime.Token);
        form.WhenAnyInputChanged(autosave.Changed)
            .ConfigureSubmitAsync("Done", autosave.FlushAsync);
        if (ShowRegistrationDialog(session, form))
            _ = await autosave.FlushAsync();
    }
    private async Task ShowJobHistory()
    {
        var list = await Get<List<AgentJob>>("jobs.history");
        Ui.ShowText(this, "Recent job outcomes", string.Join("\n\n", list.Select(x => $"{x.FinishedAt:g} · {x.Status} · {x.ProfileId}\n{x.Result ?? x.Error ?? x.Outcome ?? x.Id}")));
    }
    private async Task OfferSetupIfNeeded(bool refresh = false, bool scanRepository = true,
        CerebralClient? expectedClient = null, long? expectedEpoch = null)
    {
        var owner = expectedClient ?? Client;
        var epoch = expectedEpoch ?? queueConnectionEpoch;
        bool Current() => IsConnected && epoch == queueConnectionEpoch && ReferenceEquals(owner, Client);
        if (!Current()) return;
        var profiles = await owner.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: Lifetime.Token);
        if (!Current()) return;
        var configured = profiles.Any(x => x.Role == "lead");
        if (configured && !refresh) { setupHint.Text = "Cerebral is configured. Helpers and model resources are managed in Agents and Models."; return; }
        var registered = await owner.GetAsync<List<ModelRecord>>("models.list", cancellationToken: Lifetime.Token);
        if (!Current()) return;
        string discovered = "use Refresh setup inventory to scan the repository";
        if (scanRepository)
        {
            try
            {
                var scan = await owner.GetAsync<ModelRepositoryScan>("models.scan", cancellationToken: Lifetime.Token);
                discovered = $"{scan.Models.Count(model => model.Error is null && model.RegisteredId is null)} unregistered GGUF file(s) in {scan.Directory}" +
                    (scan.Models.Any(model => model.Error is not null) || scan.Issues.Count > 0
                        ? $"; {scan.Models.Count(model => model.Error is not null)} unreadable model(s), {scan.Issues.Count} scan issue(s) — inspect Add models"
                        : "") +
                    (scan.Truncated ? " (512-file scan limit reached)" : "");
            }
            catch (Exception error) { discovered = "Model repository scan unavailable: " + error.Message; }
            if (!Current()) return;
        }
        var bundledRoot = BundledDesktopPluginPaths.DirectoryPath;
        var bundled = Directory.Exists(bundledRoot)
            ? Directory.EnumerateDirectories(bundledRoot).Count(directory => File.Exists(Path.Combine(directory, DesktopPluginCatalog.ManifestFileName))) : 0;
        IReadOnlyList<DesktopPluginInfo> desktop = desktopPluginItems;
        var desktopDiagnostic = "";
        if (scanRepository)
        {
            try
            {
                if (desktopCatalog.TryList(out var observed)) desktop = observed;
                else desktopDiagnostic = " (publication in progress; showing last observed desktop plugins)";
            }
            catch (Exception error) { desktopDiagnostic = " (catalog unavailable: " + error.Message + "; showing last observed desktop plugins)"; }
        }
        var services = (serviceExtensions.ItemsSource as IEnumerable<ServiceExtensionInventory>)?.ToArray() ?? [];
        var desktopProblems = desktop.Where(x => x.Error is not null).Select(x => x.Id).Take(4).ToArray();
        var serviceProblems = services.Where(x => x.Error is not null).Select(x => x.Id).Take(4).ToArray();
        var cortexStatus = settings.McpServers.Any(IsEligibleFirstRunCortex)
            ? "Native Cortex is configured; inspect its live connection before use."
            : settings.McpServers.Any(connection => connection.Id.Equals("cortex", StringComparison.OrdinalIgnoreCase) ||
                connection.CortexNative is not null || connection.CortexNativeRequired)
            ? "An existing Cortex route needs review. Automatic setup will not replace or reissue it."
            : scanRepository ? CortexFirstRunDiscovery.InspectRegistered().Message
            : settings.InitialCortexDiscovery?.State switch
            {
                "compatible" => "Compatible Cortex was detected. Set up Cerebral to review the native connection offer, or open Cortex configuration.",
                "incompatible" => "The initial Cortex installation check needs attention; use Refresh setup inventory to recheck it.",
                _ => "Use Refresh setup inventory to check for a local Cortex installation."
            };
        if (!Current()) return;
        setupHint.Text = (configured ? "Setup inventory · " : "First run · ") +
            (string.IsNullOrWhiteSpace(settings.LlamaServerPath) ? "llama.cpp runtime missing; choose it in Folders & runtime" :
                "llama.cpp runtime configured; model loading will verify it") +
            $"\nModels: {registered.Count} registered; {discovered}." +
            $"\nDesktop plugins: {desktop.Count} installed, {bundled} bundled in this payload{desktopDiagnostic}" +
            (desktopProblems.Length == 0 ? "." : $"; needs attention: {string.Join(", ", desktopProblems)}.") +
            $" Service extensions: {services.Count(x => x.Loaded)} loaded, {services.Length} reported" +
            (serviceProblems.Length == 0 ? "." : $"; needs attention: {string.Join(", ", serviceProblems)}.") +
            "\nUse Add models to scan/import without loading. Set up Cerebral explicitly loads one selected model and creates local agents. " +
            cortexStatus;
        if (!refresh) SelectTab("Models");
    }
    private Task SetupOrchestrator() => SetupOrchestrator(null);
    private async Task SetupOrchestrator(string? preferredModelId)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to the selected Cerebral Worker before first-run setup.");
        var setupClient = Client;
        var setupEpoch = queueConnectionEpoch;
        var setupTransport = ServiceConnection.Transport;
        var setupEndpoint = ServiceConnection.LocalEndpoint;
        var setupData = ServiceConnection.LocalDataDirectory;
        bool SetupCurrent() => IsConnected && ReferenceEquals(Client, setupClient) &&
            queueConnectionEpoch == setupEpoch && ServiceConnection.Transport == setupTransport &&
            ServiceConnection.LocalEndpoint == setupEndpoint && ServiceConnection.LocalDataDirectory == setupData;
        void RequireSetupCurrent()
        {
            if (!SetupCurrent()) throw new InvalidOperationException("The selected Cerebral Worker changed during first-run setup. Reopen setup for the current connection; no model or agent command was replayed.");
        }
        async Task<T> SetupRead<T>(string operation, object? arguments = null)
        {
            RequireSetupCurrent();
            var value = await setupClient.GetAsync<T>(operation, arguments, cancellationToken: Lifetime.Token);
            RequireSetupCurrent();
            return value;
        }
        async Task SetupCommand(string operation, object arguments)
        {
            RequireSetupCurrent();
            var reply = await setupClient.CommandAsync(operation, arguments, cancellationToken: Lifetime.Token);
            RequireSetupCurrent();
            if (!reply.Success) throw new InvalidOperationException(reply.Error ?? "The selected Worker refused first-run setup.");
        }
        await RefreshSettings();
        RequireSetupCurrent();
        if ((await SetupRead<List<AgentProfile>>("agents.list")).Any(x => x.Role == "lead"))
        { SetStatus("Cerebral is already configured. Edit its profile in Agents."); return; }
        if (string.IsNullOrWhiteSpace(settings.LlamaServerPath))
        {
            SetStatus("Choose the installed llama.cpp runtime folder before downloading a model. Keep llama-server and its companion libraries together.");
            await EditRepositorySettings();
            RequireSetupCurrent();
            if (string.IsNullOrWhiteSpace(settings.LlamaServerPath)) return;
        }
        var list = await SetupRead<List<ModelRecord>>("models.list");
        if (list.Count == 0)
        {
            var form = new FieldsDialog(this, "Choose the first model", "Reuse existing files before downloading. A single model can serve Cerebral and several helpers with separate contexts.", "Continue") { Height = 350 };
            form.Choice("source", "Model source", [FirstRunSetupCoordinator.ScanSource, FirstRunSetupCoordinator.ImportSource, FirstRunSetupCoordinator.DownloadSource], FirstRunSetupCoordinator.ScanSource);
            if (form.ShowDialog() != true) return;
            await FirstRunSetupCoordinator.SelectModelSourceAsync(form.Selection("source"), RequireSetupCurrent,
                () => ScanModels(RequireSetupCurrent), ImportModel, () => BrowseHuggingFace(RequireSetupCurrent));
            list = await SetupRead<List<ModelRecord>>("models.list"); if (list.Count == 0) return;
        }
        var choices = ModelChoices(list);
        var suggestedId = list.Any(x => x.Id == preferredModelId) ? preferredModelId :
            list.Any(x => x.Id == settings.DefaultModelId) ? settings.DefaultModelId :
            list.FirstOrDefault(x => x.Name.Contains("Qwen3.5-9B", StringComparison.OrdinalIgnoreCase))?.Id ?? list[0].Id;
        var suggested = choices.First(x => x.Value == suggestedId).Key;
        // First-run setup offers owner-local enrollment only for a fresh admitted binding.
        // A saved route of any state is inspected or left for explicit review, never reissued.
        var localPaths = DesktopInstancePaths.Current;
        var admittedCortexWorker = ManagementProcess.UsesRegisteredInstallation ||
            localPaths.Portable && PortablePackageAdmission.IsValidatedAt(localPaths.InstallDirectory);
        var offerCortex = admittedCortexWorker && ExistingWorkerAttachment is null &&
            !settings.McpServers.Any(connection => connection.Id.Equals("cortex", StringComparison.OrdinalIgnoreCase) ||
                connection.CortexNative is not null || connection.CortexNativeRequired) &&
            CortexFirstRunDiscovery.InspectRegistered().State == "compatible";
        var cortexSetupIssue = await FirstRunSetupCoordinator.OfferOptionalCortexAsync(offerCortex,
            RequireSetupCurrent, () => ConnectLocalCortexAutomatically(optionalFirstRun: true), RefreshSettings);
        var cortexChoices = new Dictionary<string, string?> { ["None — configure Cortex separately"] = null };
        var inspectedCortex = await FirstRunSetupCoordinator.InspectEligibleCortexAsync(
            settings.McpServers.Where(IsEligibleFirstRunCortex).ToArray(), RequireSetupCurrent,
            provider => SetupRead<CortexConnectionReport>("cortex.connection.inspect", new { serverId = provider.Id }));
        var detectedCortex = inspectedCortex.Ready;
        cortexSetupIssue ??= inspectedCortex.Issue;
        foreach (var provider in detectedCortex) cortexChoices["Native Cortex · " + provider.Id] = provider.Id;
        var selectedCortex = detectedCortex.Count == 1 ? "Native Cortex · " + detectedCortex[0].Id : cortexChoices.Keys.First();
        var setupDescription = "The selected model must load successfully. Cerebral uses its detected runtime and recommended context automatically, then creates lead, coding, analysis, writing and compaction profiles that share one model load. File access starts at the configured project and shared folders." +
            (cortexSetupIssue is null ? "" : "\n\n" + cortexSetupIssue + " Select None for Cortex to continue setting up Cerebral without it.");
        var setup = new FieldsDialog(this, "Set up Cerebral", setupDescription, "Load & create agents") { Height = cortexSetupIssue is null ? 390 : 460 };
        setup.Choice("model", "Model", choices.Keys, suggested)
            .Choice("cortex", "Cortex provider", cortexChoices.Keys, selectedCortex);
        if (setup.ShowDialog() != true) return;
        RequireSetupCurrent();
        var selected = list.First(x => x.Id == choices[setup.Selection("model")]);
        var contextTokens = Math.Min(PreferredContext(selected), selected.NativeContextTokens ?? 1048576);
        var selectedCortexId = cortexChoices[setup.Selection("cortex")];
        await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(selectedCortexId, RequireSetupCurrent,
            async () =>
            {
                if (selected.ContextTokens != contextTokens)
                {
                    if (selected.State is "loaded" or "loading") await SetupCommand("models.unload", new { id = selected.Id });
                    selected.ContextTokens = contextTokens;
                    await SetupCommand("models.save", selected);
                }
                SetStatus("Loading the Cerebral model. Readiness requires a successful runtime response…");
                await SetupCommand("models.load", new { id = selected.Id });
            },
            id => SetupCommand("agents.bootstrap", new { modelId = selected.Id, cortexServerId = id }));
        await RefreshSettings(); RequireSetupCurrent();
        await RefreshAgents(); RequireSetupCurrent();
        await RefreshModels(); RequireSetupCurrent();
        await RefreshInstructionTargets(); RequireSetupCurrent();
        setupHint.Text = "Cerebral and its helpers are configured. They share the selected loaded model with independent contexts.";
        SetStatus("Cerebral model loaded and default agents created. Open Pegasus Cerebral to begin.");
        await ResumeBackgroundSetupIfRequested();
    }
}
