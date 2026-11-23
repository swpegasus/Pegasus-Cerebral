using System.Windows;
using System.Windows.Interop;

namespace Cerebral.Desktop;

/// <summary>In-memory activation plus one never-shown WPF state check. Requires STA; no named objects, signals or focus; its own HWND must stay zero.</summary>
public static class DesktopApplicationActivationChecks
{
    public static int Verify()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Existing activation checks require an STA thread.");
        const string applicationId = "Pegasus.Cerebral.Integrator.Attached.Fixture";
        var identity = DesktopApplicationIdentity.Application("fixture-exact-worker-binding");
        var owner = new DesktopApplicationInstance.OwnerDescriptor(1, identity, 123, 456);
        var checks = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException("Existing activation: " + label); checks++; }
        bool Run(Channel channel, string id = applicationId, DesktopApplicationIdentity? request = null) =>
            DesktopApplicationInstance.TryActivateExistingCore(id, request ?? identity, value =>
            { channel.ApplicationId = value; channel.Events.Add("open"); return channel; });

        var success = new Channel(owner);
        Check(Run(success), "a matching live existing owner receives a request");
        Check(success.ApplicationId == applicationId && success.Request == identity && success.SignalPid == owner.ProcessId,
            "the exact component identity and observed live owner are retained");
        Check(success.Events.SequenceEqual(["open", "read-ready", "live", "held", "enter", "read-now", "live", "held", "write",
            "read-now", "live", "held", "signal", "exit", "dispose"]), "owner checks bracket the write and signal under the gate");
        Check(success.Disposed && !success.GateHeld, "successful submission releases the gate and all channel resources");

        foreach (var invalid in new string[] { null!, "", "bad name", "bad/path", new('x', 257) })
        {
            var channel = new Channel(owner);
            Check(!Run(channel, invalid) && channel.Events.Count == 0, "invalid identifiers never open a channel");
        }
        var opened = false;
        Check(!DesktopApplicationInstance.TryActivateExistingCore(applicationId, null, _ =>
            { opened = true; return new Channel(owner); }) && !opened, "a missing requested identity cannot open resources");
        foreach (var error in new Exception[] { new WaitHandleCannotBeOpenedException(), new UnauthorizedAccessException(),
            new System.IO.IOException(), new System.Security.SecurityException(), new PlatformNotSupportedException() })
            Check(!DesktopApplicationInstance.TryActivateExistingCore(applicationId, identity, _ => throw error),
                "missing, inaccessible or unsupported existing resources report unavailable");

        foreach (var invalid in new DesktopApplicationInstance.OwnerDescriptor?[]
        {
            null, owner with { Version = 2 }, owner with { ProcessId = 0 }, owner with { ProcessStartUtcTicks = 0 },
            owner with { Identity = null! }, owner with { Identity = DesktopApplicationIdentity.Application("different-worker") }
        })
        {
            var channel = new Channel(invalid);
            Check(!Run(channel) && channel.Request is null && channel.SignalPid is null && channel.Disposed &&
                !channel.Events.Contains("enter"), "missing, malformed or mismatched descriptors refuse before gate acquisition");
        }
        var dead = new Channel(owner) { Live = false };
        Check(!Run(dead) && dead.Request is null && dead.SignalPid is null && dead.Disposed,
            "a stale PID/start descriptor cannot signal an owner");
        foreach (var state in new[] { "unheld", "abandoned" })
        {
            var stale = new Channel(owner) { OwnerState = state };
            Check(!Run(stale) && stale.Request is null && stale.SignalPid is null && stale.Disposed &&
                stale.ProbeAcquisitions == 1 && stale.ProbeReleases == 1 && !stale.Events.Contains("enter"),
                "a live descriptor without an owned primary mutex is unavailable and its probe is released: " + state);
        }
        var busy = new Channel(owner) { Enter = false };
        Check(!Run(busy) && busy.Request is null && busy.SignalPid is null && !busy.Events.Contains("exit") && busy.Disposed,
            "a busy request gate neither writes nor releases an unowned gate");

        foreach (var changed in new[] { owner with { ProcessId = 124 }, owner with { ProcessStartUtcTicks = 457 },
            owner with { Identity = DesktopApplicationIdentity.Application("different-worker") }, owner with { Version = 2 } })
        {
            var channel = new Channel(owner, changed);
            Check(!Run(channel) && channel.Request is null && channel.SignalPid is null &&
                channel.Events.Contains("exit") && channel.Disposed, "owner replacement before writing refuses and releases the gate");
        }
        var vanished = new Channel(owner, null);
        Check(!Run(vanished) && vanished.Request is null && vanished.Disposed && !vanished.GateHeld,
            "a vanished owner under the gate cannot receive a request");
        var diedUnderGate = new Channel(owner) { LiveChecks = new Queue<bool>([true, false]) };
        Check(!Run(diedUnderGate) && diedUnderGate.Request is null && diedUnderGate.Disposed && !diedUnderGate.GateHeld,
            "liveness is rechecked under the gate before writing");
        var releasedUnderGate = new Channel(owner) { HeldChecks = new Queue<bool>([true, false]) };
        Check(!Run(releasedUnderGate) && releasedUnderGate.Request is null && releasedUnderGate.SignalPid is null &&
            releasedUnderGate.Disposed && !releasedUnderGate.GateHeld,
            "primary ownership is rechecked under the request gate before writing");

        var changedAfterWrite = new Channel(owner, owner, owner with { ProcessStartUtcTicks = 457 });
        Check(!Run(changedAfterWrite) && changedAfterWrite.Request == identity && changedAfterWrite.SignalPid is null &&
            changedAfterWrite.Disposed && !changedAfterWrite.GateHeld, "post-write owner drift is unconfirmed and is never signalled");
        var diedAfterWrite = new Channel(owner) { LiveChecks = new Queue<bool>([true, true, false]) };
        Check(!Run(diedAfterWrite) && diedAfterWrite.Request == identity && diedAfterWrite.SignalPid is null &&
            diedAfterWrite.Disposed && !diedAfterWrite.GateHeld, "post-write liveness loss never becomes a focus claim");
        var releasedAfterWrite = new Channel(owner) { HeldChecks = new Queue<bool>([true, true, false]) };
        Check(!Run(releasedAfterWrite) && releasedAfterWrite.Request == identity && releasedAfterWrite.SignalPid is null &&
            releasedAfterWrite.Disposed && !releasedAfterWrite.GateHeld,
            "primary release after writing remains unconfirmed and is not signalled");
        var unsignalled = new Channel(owner) { SignalResult = false };
        Check(!Run(unsignalled) && unsignalled.Request == identity && unsignalled.Disposed && !unsignalled.GateHeld,
            "an unsuccessful signal remains unconfirmed without replay");
        foreach (var stage in new[] { "read", "enter", "write", "signal", "exit", "dispose" })
        {
            var channel = new Channel(owner) { FailAt = stage };
            Check(!Run(channel) && channel.Disposed && !channel.GateHeld,
                "ordinary channel failure remains unavailable and deterministically cleans up: " + stage);
            if (stage is "read" or "enter" or "write")
                Check(channel.SignalPid is null, "a pre-signal failure cannot submit activation: " + stage);
        }

        var application = Application.Current;
        var savedMainWindow = application?.MainWindow;
        var savedShutdownMode = application?.ShutdownMode;
        Window? window = null;
        try
        {
            if (application is not null) application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var unshownWindow = new Window { WindowState = WindowState.Minimized, ShowActivated = false, ShowInTaskbar = false };
            window = unshownWindow;
            bool Unshown() => !unshownWindow.IsLoaded && !unshownWindow.IsVisible && PresentationSource.FromVisual(unshownWindow) is null &&
                new WindowInteropHelper(unshownWindow).Handle == IntPtr.Zero;
            Check(Unshown(), "the minimized fixture has no loaded presentation or native window");
            Check(DesktopApplicationInstance.RestoreMinimizedState(unshownWindow) && unshownWindow.WindowState == WindowState.Normal,
                "the actual Show state helper restores minimized windows to Normal");
            Check(Unshown(), "restoring state alone never shows or creates a native window");
            Check(!DesktopApplicationInstance.RestoreMinimizedState(unshownWindow) && unshownWindow.WindowState == WindowState.Normal,
                "an already normal window is left unchanged");
            unshownWindow.WindowState = WindowState.Maximized;
            Check(!DesktopApplicationInstance.RestoreMinimizedState(unshownWindow) && unshownWindow.WindowState == WindowState.Maximized,
                "maximized state is preserved rather than replaced by the focus helper");
            Check(Unshown(), "normal and maximized checks remain unshown without a native presentation");
        }
        finally
        {
            try { window?.Close(); }
            finally
            {
                if (application is not null)
                {
                    application.MainWindow = savedMainWindow;
                    application.ShutdownMode = savedShutdownMode!.Value;
                }
            }
        }
        return checks;
    }

    private sealed class Channel(params DesktopApplicationInstance.OwnerDescriptor?[] owners)
        : DesktopApplicationInstance.IExistingActivationChannel
    {
        internal readonly List<string> Events = [];
        internal string? ApplicationId;
        internal DesktopApplicationIdentity? Request;
        internal int? SignalPid;
        internal bool Live = true, Enter = true, SignalResult = true, Disposed, GateHeld;
        internal string? FailAt;
        internal Queue<bool>? LiveChecks;
        internal Queue<bool>? HeldChecks;
        internal string OwnerState = "held";
        internal int ProbeAcquisitions, ProbeReleases;
        private int index;

        public DesktopApplicationInstance.OwnerDescriptor? ReadOwner(bool waitForReady)
        {
            Events.Add(waitForReady ? "read-ready" : "read-now"); Fail("read");
            return owners.Length == 0 ? null : owners[Math.Min(index++, owners.Length - 1)];
        }
        public bool OwnerIsCurrent(DesktopApplicationInstance.OwnerDescriptor value)
        { Events.Add("live"); return LiveChecks is { Count: > 0 } ? LiveChecks.Dequeue() : Live; }
        public bool OwnerIsHeld()
        {
            Events.Add("held");
            var state = HeldChecks is { Count: > 0 } ? HeldChecks.Dequeue() ? "held" : "unheld" : OwnerState;
            return DesktopApplicationInstance.ProbeHeldOwner(() =>
            {
                if (state == "held") return false;
                ProbeAcquisitions++;
                if (state == "abandoned") throw new AbandonedMutexException();
                return true;
            }, () => ProbeReleases++);
        }
        public bool TryEnterRequestGate()
        { Events.Add("enter"); Fail("enter"); return GateHeld = Enter; }
        public void ExitRequestGate()
        { Events.Add("exit"); GateHeld = false; Fail("exit"); }
        public void WriteRequest(DesktopApplicationIdentity value)
        { Events.Add("write"); Fail("write"); Request = value; }
        public bool Signal(int ownerProcessId)
        { Events.Add("signal"); Fail("signal"); SignalPid = ownerProcessId; return SignalResult; }
        public void Dispose() { Events.Add("dispose"); Disposed = true; Fail("dispose"); }
        private void Fail(string stage)
        { if (FailAt == stage) throw new System.IO.IOException("Synthetic activation channel failure."); }
    }
}
