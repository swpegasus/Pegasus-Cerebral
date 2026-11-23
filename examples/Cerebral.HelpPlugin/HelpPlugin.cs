using System.Windows;
using Cerebral.Desktop.Plugins;

namespace Cerebral.HelpPlugin;

/// <summary>Passive display only: no host commands, subscriptions, profiles, storage or model work.</summary>
public sealed class HelpPlugin : DesktopPluginBase, IDesktopMountPlugin
{
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new("help", DesktopMountSlots.SidebarTabs)
        {
            Order = 90000,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Help", CreateHelpView)
    ];

    private FrameworkElement CreateHelpView()
    {
        _ = Host; // Enforce initialization/disposal without invoking a host operation.
        return new HelpView();
    }
}
