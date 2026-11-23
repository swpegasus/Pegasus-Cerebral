using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly SemaphoreSlim interfacePreferenceWrites = new(1, 1);
    private bool hydratingInterfacePreferences;
    private bool portableInterfacePreferencesSupported = true;
    private string messageTimeFormat = MessageTimeFormatPolicy.TwelveHour;

    private async Task HydratePortableInterfacePreferencesAsync()
    {
        var response = await Client.CommandAsync("interface.preferences.get", cancellationToken: Lifetime.Token);
        if (!response.Success)
        {
            if (IsMissingPortableInterfacePreferenceCommand(response))
            {
                portableInterfacePreferencesSupported = false;
                return;
            }
            throw new InvalidOperationException(response.Error ?? "Interface preferences could not be loaded.");
        }
        portableInterfacePreferencesSupported = true;
        var saved = CerebralJson.Read<InterfacePreferences>(response.Data);
        InterfacePreferencePolicy.Validate(saved);
        if (saved.Revision == 0 && saved.EnterSends is null && saved.NavigationVisible is null &&
            saved.PanelsVisible is null && saved.SelectedSurface is null && saved.AppearanceTheme is null && saved.MessageTimeFormat is null && saved.RecentChatLimit is null)
        {
            // Adopt the signed-in user's existing local choices once when this service has no portable record.
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.EnterSends, value = enterSends.IsChecked == true });
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.NavigationVisible, value = workspaceRequested });
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.PanelsVisible, value = activityRequested });
            var surface = Tabs.SelectedItem is System.Windows.Controls.TabItem { Tag: string selected } &&
                          InterfacePreferencePolicy.Surfaces.Contains(selected) ? selected : "chat";
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.SelectedSurface, value = surface });
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.AppearanceTheme, value = AppearanceThemePolicy.System });
            await Get<InterfacePreferences>("interface.preferences.set", new
                { name = InterfacePreferencePolicy.MessageTimeFormat, value = MessageTimeFormatPolicy.TwelveHour });
            return;
        }

        ApplyPortableInterfacePreferences(saved);
    }

    private void ApplyPortableInterfacePreferences(InterfacePreferences saved, bool persistLocal = true)
    {
        InterfacePreferencePolicy.Validate(saved);
        hydratingInterfacePreferences = true;
        try
        {
            Ui.ApplyAppearanceTheme(saved.AppearanceTheme ?? AppearanceThemePolicy.System);
            messageTimeFormat = MessageTimeFormatPolicy.Effective(saved.MessageTimeFormat);
            recentChatLimit = RecentChatPolicy.Effective(saved.RecentChatLimit);
            RenderNavigation();
            if (saved.EnterSends is { } enter)
            {
                enterSends.IsChecked = enter;
                if (persistLocal) UiPreferenceStore.WriteBool("Cerebral.EnterSends", enter);
            }
            if (saved.NavigationVisible is { } navigation)
            {
                workspaceRequested = navigation;
                if (persistLocal) UiPreferenceStore.WriteBool("Cerebral.NavigationVisible", navigation);
            }
            if (saved.PanelsVisible is { } panels)
            {
                activityRequested = panels;
                if (persistLocal) UiPreferenceStore.WriteBool("Cerebral.PanelsVisible", panels);
            }
            if (saved.SelectedSurface is { } surface && pages.ContainsKey(surface))
            {
                SelectPage(surface);
                if (persistLocal) UiPreferenceStore.WriteString("Cerebral.Surface", surface);
            }
            UpdateConversationLayout();
            if (session is not null) RenderSession();
        }
        finally { hydratingInterfacePreferences = false; }
    }

    private void SavePortableInterfaceBool(string name, string localKey, bool value)
    {
        UiPreferenceStore.WriteBool(localKey, value);
        QueuePortableInterfacePreference(name, value);
    }

    private void SavePortableInterfaceString(string name, string localKey, string value)
    {
        UiPreferenceStore.WriteString(localKey, value);
        QueuePortableInterfacePreference(name, value);
    }

    private void SelectAppearanceTheme(string value)
    {
        Ui.ApplyAppearanceTheme(value);
        QueuePortableInterfacePreference(InterfacePreferencePolicy.AppearanceTheme, value);
    }

    private void QueuePortableInterfacePreference(string name, object value)
    {
        if (hydratingInterfacePreferences || !IsConnected || !portableInterfacePreferencesSupported) return;
        CerebralClient captured;
        try { captured = CreateIndependentClient(); }
        catch (InvalidOperationException) { return; }
        _ = SavePortableInterfacePreferenceAsync(captured, name, value);
    }

    private static bool IsMissingPortableInterfacePreferenceCommand(CommandResult result) =>
        !result.Success && string.Equals(result.Error, "Unknown command: interface.preferences.get", StringComparison.Ordinal);

    private async Task SavePortableInterfacePreferenceAsync(CerebralClient captured, string name, object value)
    {
        using (captured)
        {
            try
            {
                await interfacePreferenceWrites.WaitAsync(Lifetime.Token);
                try
                {
                    var result = await captured.CommandAsync("interface.preferences.set", new { name, value }, cancellationToken: Lifetime.Token);
                    if (!result.Success) throw new InvalidOperationException(result.Error ?? "Interface preference save failed.");
                }
                finally { interfacePreferenceWrites.Release(); }
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                await Dispatcher.InvokeAsync(() => SetStatus("The interface preference remains local but could not be added to configuration backup: " + error.Message, true));
            }
        }
    }
}
