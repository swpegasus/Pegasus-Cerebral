using Cerebral.PluginLifetime;

namespace Cerebral.ChatIconsPlugin;

public sealed partial class ChatIconsPlugin
{
    private bool hostUnavailable;
    private readonly PluginCleanup cleanup = new();
    private bool CallbacksRetired => disposed || hostUnavailable;
    private void RetireHostCallbacks()
    {
        if (hostUnavailable) return;
        hostUnavailable = true;
        cleanup.Attempt(() => Host.ConversationChanged -= ConversationChanged);
        cleanup.Attempt(() => settingsLifetime.Cancel());
        cleanup.Attempt(ClosePicker);
        cleanup.Attempt(CloseInlinePickers);
    }
    private void ReportHostStatus(string message)
    {
        if (!CallbacksRetired && !PluginHostAccess.TryNotify(() => Host.ReportStatus(message))) RetireHostCallbacks();
    }
    private void RefreshHostPresentation()
    {
        if (!CallbacksRetired && !PluginHostAccess.TryNotify(Host.RefreshPresentation)) RetireHostCallbacks();
    }
}
