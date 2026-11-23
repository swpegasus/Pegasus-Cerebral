using System.IO;
using System.Reflection;
using System.Windows.Controls;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;

internal static partial class Program
{
    private static Task<DesktopOperationResult> OpenPluginSettingsForCheckAsync(AiDictationPlugin plugin) =>
        (Task<DesktopOperationResult>)typeof(AiDictationPlugin).GetMethod("OpenSettingsFromControlsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null)!;

    private static async Task<int> RunSettingsNavigationGuardChecksAsync()
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        TestHost Host(string suffix) => new() { Surface = DesktopPluginSurface.Orchestrator,
            DataDirectory = Path.Combine(Path.GetTempPath(), "cerebral-dictation-navigation-" + suffix + "-" + Guid.NewGuid().ToString("N")) };
        var uncertainHost = Host("uncertain");
        var uncertain = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        uncertainHost.SettingsResponder = (_, _) => uncertain.Task;
        using (var plugin = new AiDictationPlugin())
        {
            plugin.Initialize(uncertainHost);
            var pending = OpenPluginSettingsForCheckAsync(plugin);
            var duplicate = await OpenPluginSettingsForCheckAsync(plugin);
            Check(uncertainHost.SettingsRequests.Count == 1 && duplicate.State == DesktopOperationState.Accepted,
                "A pending advanced settings request dispatches only once.");
            uncertain.SetException(new IOException("Fixture post-dispatch acknowledgement failure."));
            var result = await pending;
            Check(result.State == DesktopOperationState.Uncertain && uncertainHost.SettingsRequests.Count == 1,
                "An unconfirmed settings result does not replay.");
            Check(result.Message?.Contains("may have opened", StringComparison.OrdinalIgnoreCase) == true,
                "Unconfirmed settings navigation explains its possible effect.");
        }
        var retiringHost = Host("retire");
        var late = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        retiringHost.SettingsResponder = (_, _) => late.Task;
        using (var plugin = new AiDictationPlugin())
        {
            plugin.Initialize(retiringHost);
            var pending = OpenPluginSettingsForCheckAsync(plugin);
            plugin.Dispose();
            Check(retiringHost.SettingsRequests.Count == 1, "Retirement preserves the single original settings dispatch.");
            late.SetResult(new(DesktopOperationState.Completed));
            Check((await pending).State == DesktopOperationState.Uncertain, "A late acknowledgement cannot restore a retired settings request.");
            Check((await OpenPluginSettingsForCheckAsync(plugin)).State == DesktopOperationState.Rejected && retiringHost.SettingsRequests.Count == 1,
                "Retired controls cannot navigate again.");
        }
        var revokingHost = Host("reentrant");
        using (var plugin = new AiDictationPlugin())
        {
            plugin.Initialize(revokingHost);
            var first = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
            var second = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
            revokingHost.SettingsAvailabilityReader = _ => { plugin.Dispose(); return new(false, "The owner retired."); };
            typeof(AiDictationPlugin).GetMethod("RefreshTargetViews", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null);
            Check((await OpenPluginSettingsForCheckAsync(plugin)).State == DesktopOperationState.Rejected && revokingHost.SettingsRequests.Count == 0,
                "Reentrant owner retirement during presentation cannot revive settings navigation.");
            first.Dispose(); second.Dispose();
        }
        return checks;
    }
}
