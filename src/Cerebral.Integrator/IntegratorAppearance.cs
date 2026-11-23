using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly SemaphoreSlim appearancePreferenceWrites = new(1, 1);
    private const string InvalidRecentChatLimitStatus = "Recent chats shown must be a whole number between 1 and 200. Your saved preference was not changed.";
    private bool hydratingAppearanceTheme;
    private bool appearancePreferencesSupported;
    private CerebralClient? appearancePreferencesClient;
    private bool CanEditInterfacePreferences => IsConnected && appearancePreferencesSupported &&
        ReferenceEquals(Client, appearancePreferencesClient);

    private void InitializeAppearanceThemePicker()
    {
        appearanceTheme.DisplayMemberPath = nameof(AppearanceChoice.Name);
        appearanceTheme.SelectedValuePath = nameof(AppearanceChoice.Id);
        appearanceTheme.ItemsSource = new[]
        {
            new AppearanceChoice(AppearanceThemePolicy.System, "System"),
            new AppearanceChoice(AppearanceThemePolicy.Light, "Light"),
            new AppearanceChoice(AppearanceThemePolicy.Dark, "Dark")
        };
        AutomationProperties.SetAutomationId(appearanceTheme, "ConfigurationAppearanceTheme");
        hydratingAppearanceTheme = true;
        appearanceTheme.SelectedValue = AppearanceThemePolicy.System;
        hydratingAppearanceTheme = false;
        appearanceTheme.SelectionChanged += (_, _) =>
        {
            if (hydratingAppearanceTheme || !CanEditInterfacePreferences || appearanceTheme.SelectedValue is not string value) return;
            Ui.ApplyAppearanceTheme(value);
            QueueAppearanceThemeSave(value);
        };
        messageTimeFormat.DisplayMemberPath = nameof(AppearanceChoice.Name);
        messageTimeFormat.SelectedValuePath = nameof(AppearanceChoice.Id);
        messageTimeFormat.ItemsSource = new[]
        {
            new AppearanceChoice(MessageTimeFormatPolicy.TwelveHour, "12-hour · 3:45 PM"),
            new AppearanceChoice(MessageTimeFormatPolicy.TwentyFourHour, "24-hour · 15:45")
        };
        AutomationProperties.SetAutomationId(messageTimeFormat, "ConfigurationMessageTimeFormat");
        hydratingAppearanceTheme = true;
        messageTimeFormat.SelectedValue = MessageTimeFormatPolicy.TwelveHour;
        hydratingAppearanceTheme = false;
        messageTimeFormat.SelectionChanged += (_, _) =>
        {
            if (hydratingAppearanceTheme || !CanEditInterfacePreferences || messageTimeFormat.SelectedValue is not string value) return;
            QueueInterfacePreferenceSave(InterfacePreferencePolicy.MessageTimeFormat, value, "message time format");
        };
        recentChatLimitInput.MinWidth = 100;
        recentChatLimitInput.MaxWidth = 180;
        recentChatLimitInput.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        recentChatLimitInput.Text = RecentChatPolicy.DefaultLimit.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetAutomationId(recentChatLimitInput, "ConfigurationRecentChatLimit");
        AutomationProperties.SetName(recentChatLimitInput, "Recent general chats shown");
        recentChatLimitInput.LostKeyboardFocus += (_, _) => SaveRecentChatLimit();
        ServiceStatusChanged += (_, _) => RefreshInterfacePreferenceControls();
        recentChatLimitInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SaveRecentChatLimit(); e.Handled = true; }
        };
    }

    private async Task HydrateAppearanceThemeAsync()
    {
        var captured = Client;
        appearancePreferencesClient = null;
        RefreshInterfacePreferenceControls();
        var response = await captured.CommandAsync("interface.preferences.get", cancellationToken: Lifetime.Token);
        // A reply from a previous connection must never hydrate the current owner\'s controls.
        if (!ReferenceEquals(Client, captured) || Lifetime.IsCancellationRequested) return;
        if (!response.Success)
        {
            if (string.Equals(response.Error, "Unknown command: interface.preferences.get", StringComparison.Ordinal))
            {
                appearancePreferencesSupported = false;
                appearancePreferencesClient = captured;
                RefreshInterfacePreferenceControls();
                return;
            }
            throw new InvalidOperationException(response.Error ?? "Appearance preferences could not be loaded.");
        }

        appearancePreferencesSupported = true;
        var saved = CerebralJson.Read<InterfacePreferences>(response.Data);
        InterfacePreferencePolicy.Validate(saved);
        var value = saved.AppearanceTheme ?? AppearanceThemePolicy.System;
        hydratingAppearanceTheme = true;
        try
        {
            appearanceTheme.SelectedValue = value;
            messageTimeFormat.SelectedValue = MessageTimeFormatPolicy.Effective(saved.MessageTimeFormat);
            recentChatLimitInput.Text = RecentChatPolicy.Effective(saved.RecentChatLimit).ToString(CultureInfo.InvariantCulture);
            Ui.ApplyAppearanceTheme(value);
        }
        finally
        {
            hydratingAppearanceTheme = false;
            appearancePreferencesClient = captured;
            RefreshInterfacePreferenceControls();
        }
    }

    private void SaveRecentChatLimit()
    {
        if (hydratingAppearanceTheme || !CanEditInterfacePreferences) return;
        if (!int.TryParse(recentChatLimitInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            !RecentChatPolicy.IsValid(value))
        {
            SetStatus(InvalidRecentChatLimitStatus, true);
            return;
        }
        QueueInterfacePreferenceSave(InterfacePreferencePolicy.RecentChatLimit, value, "recent chat limit");
    }

    private void QueueAppearanceThemeSave(string value)
        => QueueInterfacePreferenceSave(InterfacePreferencePolicy.AppearanceTheme, value, "appearance");

    private void QueueInterfacePreferenceSave(string name, object value, string label)
    {
        if (!CanEditInterfacePreferences)
        {
            RefreshInterfacePreferenceControls();
            SetStatus("This interface preference cannot be saved on the current Worker. Read its saved preferences after reconnecting.", true);
            return;
        }
        CerebralClient captured;
        try { captured = CreateIndependentClient(); }
        catch (InvalidOperationException) { return; }
        _ = SaveInterfacePreferenceAsync(captured, Client, name, value, label);
    }

    private async Task SaveInterfacePreferenceAsync(CerebralClient captured, CerebralClient owner,
        string name, object value, string label)
    {
        using (captured)
        {
            try
            {
                await appearancePreferenceWrites.WaitAsync(Lifetime.Token);
                try
                {
                    var result = await captured.CommandAsync("interface.preferences.set",
                        new { name, value }, cancellationToken: Lifetime.Token);
                    if (!result.Success) throw new InvalidOperationException(result.Error ?? "Interface preference save failed.");
                    if (name == InterfacePreferencePolicy.RecentChatLimit && value is int savedLimit)
                        await Dispatcher.InvokeAsync(() =>
                        {
                            // Clear only this validation message after the exact valid save succeeds.
                            // A newer connection or another asynchronous failure owns its own status.
                            if (IsConnected && ReferenceEquals(Client, owner) && Status.Text == InvalidRecentChatLimitStatus &&
                                int.TryParse(recentChatLimitInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var currentLimit) &&
                                currentLimit == savedLimit)
                                SetStatus("Recent chats shown preference saved.");
                        });
                }
                finally { appearancePreferenceWrites.Release(); }
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsConnected && ReferenceEquals(Client, owner))
                        SetStatus("The " + label + " changed for this window but could not be saved: " + error.Message, true);
                });
            }
        }
    }

    private sealed record AppearanceChoice(string Id, string Name);
}
