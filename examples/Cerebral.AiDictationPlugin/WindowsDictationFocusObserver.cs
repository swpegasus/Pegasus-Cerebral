using System.Diagnostics;
using System.Windows.Automation;

namespace Cerebral.AiDictationPlugin;

/// <summary>Opt-in, event-only memory of one eligible control. Never scans windows or reads text.</summary>
internal sealed class WindowsDictationFocusObserver : IDisposable
{
    private sealed record FocusEvent(AutomationElement Element, long At);
    private readonly Action<IChosenWindowsTextTarget> remember;
    private readonly Action<Exception> failed;
    private readonly Action<long> foreignFocus;
    private readonly AutomationFocusChangedEventHandler handler;
    private readonly Task registration;
    private FocusEvent? pending;
    private int disposed, registered, draining;

    internal WindowsDictationFocusObserver(Action<IChosenWindowsTextTarget> remember, Action<Exception> failed, Action<long> foreignFocus)
    {
        this.remember = remember; this.failed = failed; this.foreignFocus = foreignFocus;
        handler = FocusChanged;
        // UIA subscription work belongs on a non-UI thread. Dispose attaches cleanup to
        // this exact attempt, so a late successful registration cannot leak a listener.
        registration = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref disposed) != 0) return;
                Automation.AddAutomationFocusChangedEventHandler(handler);
                Volatile.Write(ref registered, 1);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (Volatile.Read(ref disposed) == 0) failed(error); }
        });
    }

    private void FocusChanged(object sender, AutomationFocusChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || sender is not AutomationElement element) return;
        Interlocked.Exchange(ref pending, new(element, Stopwatch.GetTimestamp()));
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref disposed) == 0 && Interlocked.Exchange(ref pending, null) is { } next)
                {
                    try
                    {
                        if (next.Element.Current.ProcessId == Environment.ProcessId) continue;
                        var chosen = new WindowsUiaTextTarget(next.Element, next.At);
                        if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref pending) is null) remember(chosen);
                    }
                    // Unsupported, protected, own-process and disappearing controls are
                    // ignored before reading contents; retain the prior eligible cursor.
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { if (Volatile.Read(ref disposed) == 0) foreignFocus(next.At); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref draining, 0);
                if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref pending) is not null) Schedule();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref pending, null);
        _ = registration.ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref registered, 0) == 0) return;
            try { Automation.RemoveAutomationFocusChangedEventHandler(handler); }
            catch (Exception error) when (error is not OutOfMemoryException) { failed(error); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
