using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Only a new live effect is eligible. Reopening a window never replays pending/claimed controls.
    private async Task PluginControlEventAsync(ServiceEvent item)
    {
        if (item.Type != "plugin.control.updated") return;
        var pending = item.Data.Deserialize<PluginControlRequest>(CerebralJson.Options);
        if (pending is null || pending.Status != "pending" || pending.SessionId != CurrentConversationId ||
            !ServiceIsLocal || pending.ExpiresAt <= DateTimeOffset.UtcNow) return;
        var endpoint = ServiceConnectionIdentity;
        using var owner = CreateIndependentClient();
        var claimId = Guid.NewGuid().ToString("N");
        var claim = await owner.GetAsync<PluginControlClaim>("interface.controls.claim",
            new { id = pending.Id, sessionId = pending.SessionId, claimId });
        if (!claim.Claimed) return;
        var request = claim.Request;
        var status = "rejected"; string? error = null;
        Cerebral.Core.PluginControlSnapshot? before = null;
        PluginControlObservation? observation = null;
        var nativeEffectStarted = false;
        var clickDelivered = false;
        var instanceRecreated = false;
        var textSet = false;
        try
        {
            void RequireCurrent()
            {
                if (CurrentConversationId != request.SessionId || ServiceConnectionIdentity != endpoint ||
                    !request.Plugin.ControlTestsAllowed || !request.Plugin.Mounts.Any(x => x.Slot == "sidebar" && x.Id == request.MountId))
                    throw new InvalidOperationException("The approved conversation, connection, or plugin mount changed.");
                RequirePluginControlPackageCurrent(desktopPlugins ??
                    throw new InvalidOperationException("The desktop plugin runtime is unavailable."), request);
            }
            RequireCurrent();
            var tab = desktopSidebarTabs.Items.OfType<TabItem>().SingleOrDefault(x => Equals(x.Tag, request.MountId)) ??
                throw new InvalidOperationException("The owner-approved sidebar is no longer mounted.");
            SelectPage("chat"); activityRequested = true; UpdateConversationLayout();
            desktopSidebarTabs.SelectedItem = tab; UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, Lifetime.Token);
            // Recheck service-owned cancellation, grants and the claim after the asynchronous layout
            // wait. This is a preflight, not an atomic reservation across service and desktop processes.
            _ = await owner.GetAsync<PluginControlRequest>("interface.controls.ready",
                new { id = request.Id, sessionId = request.SessionId, claimId });
            RequireCurrent();
            using var backgroundRender = Cerebral.Desktop.DesktopBackgroundRenderScope.Begin(this);
            UpdateLayout();
            if (DateTimeOffset.UtcNow >= request.ExpiresAt)
                throw new InvalidOperationException("The control deadline passed before native delivery; no control was changed.");
            var view = SelectedPluginControlView(request.MountId);
            before = PluginControlSnapshot(request.MountId, view);
            if (request.Operation == "setText")
            {
                var edit = request.TextEdit ?? throw new InvalidOperationException("The native text request is incomplete.");
                var prepared = PreparePluginControlTextEdit(view, edit);
                nativeEffectStarted = true;
                prepared.Provider.SetValue(edit.Text); textSet = true;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, Lifetime.Token);
            }
            else if (request.Operation == "invoke")
            {
                var button = FindPluginControlButton(view, request.ButtonName!);
                if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("The named plugin button is hidden or disabled.");
                var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider ??
                    throw new InvalidOperationException("The named button has no native Invoke pattern.");
                nativeEffectStarted = true;
                provider.Invoke(); clickDelivered = true;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, Lifetime.Token);
            }
            else if (request.Operation == "reopen")
            {
                var runtime = desktopPlugins ?? throw new InvalidOperationException("The desktop plugin runtime is unavailable.");
                var packageDirectory = Path.GetDirectoryName(request.Plugin.ManifestPath) ??
                    throw new InvalidOperationException("The reviewed plugin package directory is unavailable.");
                runtime.ValidateCanReopenExact(request.Plugin.Id, packageDirectory, request.Plugin.Version);
                nativeEffectStarted = true;
                var reopened = runtime.ReopenExact(request.Plugin.Id, packageDirectory, request.Plugin.Version);
                instanceRecreated = reopened.InstanceRecreated;
                if (!instanceRecreated)
                    throw new InvalidOperationException(reopened.Error ?? "The native plugin did not create a new instance.");
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, Lifetime.Token);
            }
            RequireCurrent();
            var afterView = SelectedPluginControlView(request.MountId);
            if (request.Operation == "reopen")
            {
                if (ReferenceEquals(view, afterView))
                    throw new InvalidOperationException("The plugin instance changed without creating a new mounted view.");
            }
            else if (!ReferenceEquals(view, afterView))
                throw new InvalidOperationException("The mounted view changed during the control operation.");
            var after = PluginControlSnapshot(request.MountId, afterView);
            observation = new(before, after, clickDelivered, instanceRecreated) { TextSet = textSet };
            status = "completed";
        }
        catch (Exception ex)
        {
            status = nativeEffectStarted ? "uncertain" : "rejected";
            error = ex.Message[..Math.Min(ex.Message.Length, 2000)];
            if (before is not null && observation is null)
                observation = new(before, null, clickDelivered, instanceRecreated) { TextSet = textSet };
        }
        await owner.GetAsync<PluginControlRequest>("interface.controls.complete",
            new { id = request.Id, sessionId = request.SessionId, claimId, status, observation, error });
        if (CurrentConversationId == request.SessionId && status != "completed")
            SetStatus("Plugin control: " + error, true);
    }

    private void RequirePluginControlPackageCurrent(DesktopPluginRuntime runtime, PluginControlRequest request)
    {
        RequireDesktopPluginReceiptCurrent(runtime, request.Plugin);
        if (request.PackageFingerprint is null)
        {
            if (request.Operation is "reopen" or "setText")
                throw new InvalidOperationException("This older control receipt has no immutable package fingerprint and cannot mutate a native plugin instance.");
            return;
        }
        var current = NativeDesktopPluginPackage.ComputeFingerprint(request.Plugin.ManifestPath);
        if (!string.Equals(current, request.PackageFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed plugin bytes changed after control admission. No native control was delivered.");
    }
}
