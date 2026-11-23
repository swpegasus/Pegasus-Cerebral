using System.IO;
using System.Security;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>A single explicit choice bound to one observed optional package, never a settings reset.</summary>
internal sealed class MainStartupPluginRecovery(string pluginName, Action disable)
{
    private int consumed;
    internal string PluginName { get; } = pluginName;
    internal bool Consumed => Volatile.Read(ref consumed) != 0;
    internal string Explanation => PluginName + " could not finish startup. Retry after correcting the problem shown below, " +
        "or disable this optional plugin and try startup again. Disabling is saved; its preferences are preserved. " +
        "You can re-enable it in Configuration. Restoring preferences is unavailable for this startup failure.";

    internal void Disable()
    {
        // A lost or partial write must never reuse the original choice, including a double click.
        if (Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("This plugin recovery choice has already been used. Inspect the current startup result.");
        disable();
    }

    internal static bool Eligible(MainStartupMode mode, Exception error) =>
        mode == MainStartupMode.Installed && error is DesktopPluginStartupException
        {
            PluginId: "pegasus.ai-dictation", CallbackStillRunning: false
        } && !HasRequiredFailure(error);

    private static bool HasRequiredFailure(Exception error)
    {
        // Only the known optional package's settled callback is eligible. Assembly admission,
        // access and security failures retain their blocking diagnostic and existing retry policy.
        Exception? cause = error;
        for (var depth = 0; cause is not null && depth < 16; depth++, cause = cause.InnerException)
            if (cause is SecurityException or UnauthorizedAccessException or BadImageFormatException or
                FileLoadException or FileNotFoundException or TypeLoadException or MissingMethodException or
                AggregateException or System.Reflection.ReflectionTypeLoadException) return true;
        return cause is not null;
    }

    internal static MainStartupPluginRecovery? Capture(MainStartupMode mode, Exception error,
        DesktopPluginRuntime runtime, DesktopPluginCatalog catalog)
    {
        if (!Eligible(mode, error)) return null;
        var id = ((DesktopPluginStartupException)error).PluginId;
        var loaded = runtime.GetLoadedPackage(id);
        var package = catalog.List().SingleOrDefault(item => item.Id == id);
        if (loaded is null || package is not { Enabled: true, Error: null } ||
            !string.Equals(loaded.Value.Directory, package.Directory, StringComparison.OrdinalIgnoreCase) ||
            loaded.Value.Version != package.Version) return null;
        // Runtime failure is the reason for this choice, not a package-admission failure.
        // Validate catalog admission independently; the exact loaded generation was checked above.
        var snapshot = catalog.GetManagementSnapshot(id);
        var selected = snapshot.Plugins.SingleOrDefault(item => item.Id == id);
        if (selected is not { Enabled: true, Error: null, Generation: not null, PackageFingerprint: not null } ||
            selected.Version != package.Version) return null;
        var selection = new NativePluginSelection(id, true, selected.Generation, selected.PackageFingerprint);
        return new("AI Dictation", () =>
        {
            NativePluginManagementResult result;
            try { result = catalog.SetEnabledExact(snapshot.CatalogFingerprint, selection, enabled: false); }
            catch (DesktopPluginManagementMutationException uncertain)
            { throw Unconfirmed(uncertain); }
            var observed = result.Catalog.Plugins.SingleOrDefault(item => item.Id == id);
            if (result.ChangedPluginIds.Count != 1 || result.ChangedPluginIds[0] != id ||
                observed is not { Enabled: false, Error: null } || observed.Generation != selection.Generation ||
                observed.PackageFingerprint != selection.PackageFingerprint)
                throw Unconfirmed(new InvalidOperationException("The exact disabled plugin outcome was not confirmed."));
        });
    }

    private static MainStartupException Unconfirmed(Exception cause) => new(
        "AI Dictation · disable outcome unconfirmed", cause, retryAllowed: false);
}
