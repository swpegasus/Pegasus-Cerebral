using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private const string SettingsPanelId = "ai-dictation-settings";
    private CancellationTokenSource? settingsNavigationCancellation;

    private DesktopSettingsNavigationAvailability ReadSettingsNavigationAvailability()
    {
        if (!CallbacksAvailable) return new(false, "AI Dictation is no longer available. Reload the plugin before opening its settings.");
        if (host is not IDesktopPluginSettingsNavigationHost navigation)
            return new(false, "Opening plugin settings from this host is work in progress. Open Configuration > Desktop plugins > AI Dictation.");
        try { return navigation.GetSettingsNavigationAvailability(SettingsPanelId) ??
            new(false, "AI Dictation settings navigation is unavailable. Open Configuration > Desktop plugins > AI Dictation."); }
        catch (ObjectDisposedException)
        {
            hostUnavailable = true; RetireControls();
            return new(false, "The AI Dictation host is no longer available. Reload the plugin before opening its settings.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportHostStatus("AI Dictation settings navigation is unavailable.");
            return new(false, "AI Dictation settings navigation is unavailable. Open Configuration > Desktop plugins > AI Dictation.");
        }
    }

    private async Task<DesktopOperationResult> OpenSettingsFromControlsAsync()
    {
        if (settingsNavigationCancellation is not null)
            return new(DesktopOperationState.Accepted, "An AI Dictation settings request is already pending.");
        var availability = ReadSettingsNavigationAvailability();
        if (!availability.Available || host is not IDesktopPluginSettingsNavigationHost navigation)
        {
            if (CallbacksAvailable) ReportHostStatus(availability.Message ?? "AI Dictation settings are unavailable.");
            return new(DesktopOperationState.Rejected, availability.Message);
        }

        // The scoped host owns plugin/installed-generation identity and the pinned
        // Configuration launch. The plugin supplies only its declared panel ID.
        using var attempt = new CancellationTokenSource();
        settingsNavigationCancellation = attempt;
        try
        {
            var result = await navigation.OpenSettingsAsync(SettingsPanelId, attempt.Token);
            if (attempt.IsCancellationRequested || !CallbacksAvailable)
                return new(DesktopOperationState.Uncertain, "Configuration may have opened before this request retired. Inspect Configuration before trying again.");
            if (result.State != DesktopOperationState.Completed)
                ReportHostStatus(result.Message ?? (result.State == DesktopOperationState.Uncertain
                    ? "Configuration may have opened; AI Dictation settings were not confirmed. Check Configuration before retrying."
                    : "AI Dictation settings have not been confirmed open."));
            return result;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested || !CallbacksAvailable)
        {
            return new(DesktopOperationState.Uncertain, "Configuration may have opened before this request was canceled. Inspect Configuration before trying again.");
        }
        catch (ObjectDisposedException)
        {
            hostUnavailable = true; RetireControls();
            return new(DesktopOperationState.Uncertain, "Configuration may have opened before the host became unavailable. Inspect Configuration before trying again.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Once the host was invoked, an exception cannot prove that navigation had no effect.
            // Preserve explicit host Failed results above; every unproven outcome needs inspection.
            const string uncertain = "Configuration may have opened, but AI Dictation settings could not be confirmed. Inspect Configuration before trying again.";
            if (CallbacksAvailable) ReportHostStatus(uncertain);
            return new(DesktopOperationState.Uncertain, uncertain);
        }
        finally
        {
            if (ReferenceEquals(settingsNavigationCancellation, attempt)) settingsNavigationCancellation = null;
        }
    }

    // Retire before disposing views so a queued route cannot outlive this plugin's controls.
    private void CancelSettingsNavigation() => settingsNavigationCancellation?.Cancel();
}
