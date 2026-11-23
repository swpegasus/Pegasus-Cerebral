using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private bool hostUnavailable, controlsRetired;
    private IDesktopTextEntry? observedFocusedTarget;
    private IDesktopTextEntry? lastCerebralTarget;
    private readonly List<Exception> teardownFailures = [];
    private bool CallbacksAvailable => !disposed && !hostUnavailable && !controlsRetired;

    // Host revocation intentionally precedes native plugin disposal. Only an actual
    // disposed-host boundary retires this instance; other failures keep their meaning.
    private bool TryReadHost<T>(Func<T> read, out T value)
    {
        value = default!;
        if (disposed || hostUnavailable) return false;
        try { value = read(); return true; }
        catch (ObjectDisposedException)
        {
            hostUnavailable = true;
            RetireControls();
            return false;
        }
    }

    private void ReportHostStatus(string message) => TryReadHost(() =>
    {
        host?.ReportStatus(message);
        return true;
    }, out _);

    // Window callbacks cannot let one cleanup failure strand subscriptions, capture,
    // or remaining controls. Preserve every failure and report them after Dispose.
    private void AttemptTeardown(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (error is not OutOfMemoryException) { teardownFailures.Add(error); }
    }

    private void RetireControls()
    {
        controlsRetired = true;
        observedFocusedTarget = null;
        lastCerebralTarget = null;
        pinnedCerebralTarget = null;
        frontMicrophoneStarting = controlActionPending = false;
        AttemptTeardown(CancelSettingsNavigation);
        AttemptTeardown(StopWindowsFocusObservation);
        AttemptTeardown(DetachControlOwner);
        AttemptTeardown(DetachControlsShortcut);
        AttemptTeardown(CancelMicrophoneTest);
        AttemptTeardown(() => playbackCancellation?.Cancel());
        AttemptTeardown(() => acceptCancellation?.Cancel());
        AttemptTeardown(() => StopAutomatic(cancelSession: true));
        automaticArmed = false;
        if (lockedTarget is WindowsDictationTarget external) AttemptTeardown(external.Dispose);
        lockedTarget = null; lockedBinding = null;
        DisposeControls();
    }
}
