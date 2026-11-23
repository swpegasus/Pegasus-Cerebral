using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Cerebral.Desktop;

/// <summary>A local boundary for passive view/focus/scroll interactions, never commands or runtime dispatch.</summary>
public static class PassiveDesktopInteraction
{
    public static bool TryRun(string interaction, Action action, Action<string> report,
        Action? recover = null, Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interaction);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(report);
        try { action(); return true; }
        catch (Exception error) when (Recoverable(error))
        {
            WriteDiagnostic(interaction, error, diagnostic);
            // Recovery only retires a failed view callback or resets its private follow flags.
            // A reporting failure must not recursively retry the banner or the original action.
            try { recover?.Invoke(); }
            catch (Exception recoveryError) when (Recoverable(recoveryError))
            { WriteDiagnostic(interaction + ".recovery", recoveryError, diagnostic); }
            try { report("Could not update " + interaction + ". Retry the click or scroll manually; you can keep using this view."); }
            catch (Exception reportError) when (Recoverable(reportError))
            { WriteDiagnostic(interaction + ".report", reportError, diagnostic); }
            return false;
        }
    }

    public static void Defer(Dispatcher dispatcher, string interaction, Action action,
        Action<string> report, Action? recover = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        TryRun(interaction, () => dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => TryRun(interaction, action, report, recover))), report, recover);
    }

    // Deliberately closed: unrelated dispatcher failures and fatal runtime exceptions remain
    // governed by their existing host path, rather than a global unhandled-exception swallow.
    private static bool Recoverable(Exception error) =>
        error is ArgumentException or InvalidOperationException or NotSupportedException;

    private static void WriteDiagnostic(string interaction, Exception error, Action<string>? diagnostic)
    {
        // Messages, file/line paths and invocation values can contain user data. Record only
        // bounded host coordinates and method names; never serialize the exception or payload.
        var methods = new StackTrace(error, fNeedFileInfo: false).GetFrames().Take(8)
            .Select(frame => frame.GetMethod())
            .OfType<System.Reflection.MethodBase>()
            .Select(method =>
            {
                var coordinate = method.DeclaringType?.FullName + "." + method.Name;
                return coordinate[..Math.Min(coordinate.Length, 160)];
            }).ToArray();
        var exceptionType = error.GetType().FullName ?? error.GetType().Name;
        var entry = JsonSerializer.Serialize(new
        {
            kind = "desktop.passive-interaction.failed",
            interaction = interaction[..Math.Min(interaction.Length, 80)],
            exceptionType = exceptionType[..Math.Min(exceptionType.Length, 160)],
            error.HResult,
            methods
        });
        if (diagnostic is not null) { diagnostic(entry); return; }
        try { Console.Error.WriteLine(entry); }
        catch (Exception logError) when (logError is IOException or InvalidOperationException or NotSupportedException) { }
        try { Trace.TraceError("{0}", entry); }
        catch (Exception logError) when (logError is IOException or InvalidOperationException or NotSupportedException) { }
    }
}
