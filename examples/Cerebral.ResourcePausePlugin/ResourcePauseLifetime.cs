using Cerebral.PluginLifetime;

namespace Cerebral.ResourcePausePlugin;

public sealed partial class ResourcePausePlugin
{
    private bool hostUnavailable;
    private readonly PluginCleanup cleanup = new();
    private bool CallbacksRetired => disposed || hostUnavailable;
    private void ReportHostStatus(string detail)
    {
        if (CallbacksRetired) return;
        if (PluginHostAccess.TryNotify(() => host?.ReportStatus(detail))) return;
        hostUnavailable = true;
        cleanup.Attempt(() => lifetime.Cancel());
        cleanup.Attempt(DisposeStatusRefresh);
        cleanup.Attempt(ClearStatusBalloons);
    }
}
