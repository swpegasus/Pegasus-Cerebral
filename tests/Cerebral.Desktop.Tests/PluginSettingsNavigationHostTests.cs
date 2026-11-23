using System.IO;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class PluginSettingsNavigationHostTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        var adapter = new DesktopPluginHostAdapter(dispatcher, _ => { });
        using (var oldScope = adapter.CreateScope("example.plugin", "Example", Path.GetTempPath()))
            Check(oldScope is not IDesktopPluginSettingsNavigationHost,
                "A host without the configured route must not advertise the optional facet.");

        var availabilityCalls = 0;
        var openCalls = 0;
        string? observedId = null, observedDirectory = null, observedPanel = null;
        CancellationToken observedToken = default;
        adapter.ConfigureSettingsNavigation((id, directory, panel) =>
        {
            availabilityCalls++;
            observedId = id; observedDirectory = directory; observedPanel = panel;
            return new(panel == "settings", panel == "settings" ? null : "Unavailable panel.");
        }, (id, directory, panel, token) =>
        {
            openCalls++;
            observedId = id; observedDirectory = directory; observedPanel = panel; observedToken = token;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        });
        var duplicateRejected = false;
        try { adapter.ConfigureSettingsNavigation((_, _, _) => new(true), (_, _, _, _) =>
            Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed))); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected, "The route can only be bound once.");
        var scope = adapter.CreateScope("example.plugin", "Example", Path.GetTempPath());
        Check(scope is IDesktopPluginSettingsNavigationHost, "The configured host advertises the optional facet.");
        var navigation = (IDesktopPluginSettingsNavigationHost)scope;
        Check(!navigation.GetSettingsNavigationAvailability("bad/panel").Available && availabilityCalls == 0,
            "Invalid panel IDs fail before reaching the host callback.");
        Check(!navigation.GetSettingsNavigationAvailability("other").Available && availabilityCalls == 1,
            "The host callback may decline a syntactically valid panel.");
        var refused = await navigation.OpenSettingsAsync("other");
        Check(refused.State == DesktopOperationState.Rejected && openCalls == 0,
            "A declined panel is not opened.");
        var completed = await navigation.OpenSettingsAsync("settings");
        Check(completed.State == DesktopOperationState.Completed && openCalls == 1 &&
            observedId == "example.plugin" && observedPanel == "settings" &&
            observedDirectory == Path.GetFullPath(Path.GetTempPath()),
            "The scoped host binds its own plugin identity and package directory.");
        Check(!observedToken.IsCancellationRequested, "The active call receives a live linked token.");
        scope.Dispose();
        var disposedRejected = false;
        try { _ = navigation.GetSettingsNavigationAvailability("settings"); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "A retired scope cannot report navigation availability.");
        disposedRejected = false;
        try { _ = await navigation.OpenSettingsAsync("settings"); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected && openCalls == 1, "A retired scope cannot dispatch another navigation.");
        var binding = new DesktopConversationBinding("fixture", null);
        var rejected = Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected));
        var combined = new DesktopPluginHostAdapter(dispatcher,
            () => new(binding, "Fixture", null, null, false), () => new(binding, 0, ""),
            (_, _) => rejected, (_, _) => rejected, (_, _) => rejected, (_, _) => rejected,
            _ => { }, responseBalance: () => new(binding, null, 2048, 512),
            setResponseBalance: (_, _, _) => rejected);
        combined.ConfigureSettingsNavigation((_, _, _) => new(true), (_, _, _, _) =>
            Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed)));
        using (var both = combined.CreateScope("example.plugin", "Example", Path.GetTempPath()))
            Check(both is IDesktopConversationSettingsHost && both is IDesktopPluginSettingsNavigationHost,
                "Navigation and conversation settings remain independently available on the Main host.");
        var pending = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken = default;
        var retiring = new DesktopPluginHostAdapter(dispatcher, _ => { });
        retiring.ConfigureSettingsNavigation((_, _, _) => new(true), (_, _, _, token) =>
        {
            pendingToken = token;
            return pending.Task;
        });
        var retiredScope = retiring.CreateScope("example.plugin", "Example", Path.GetTempPath());
        var late = ((IDesktopPluginSettingsNavigationHost)retiredScope).OpenSettingsAsync("settings");
        Check(!late.IsCompleted && pendingToken.CanBeCanceled, "The in-flight route is bound to the scope lifetime.");
        retiredScope.Dispose();
        Check(pendingToken.IsCancellationRequested, "Retiring the plugin cancels the in-flight navigation token.");
        pending.SetResult(new(DesktopOperationState.Completed));
        Check((await late).State == DesktopOperationState.Uncertain,
            "A callback that ignores retirement cannot publish a late Completed result.");
        return checks;
    }
}
