using System.IO;
using System.Security;
using System.Windows;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Focused pure checks and unshown STA controls; no package, Worker or saved-file mutation.</summary>
public static class MainStartupPluginRecoveryChecks
{
    public static async Task<string> RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); passed++; }
        Exception Callback(Exception cause, bool running = false, string id = "pegasus.ai-dictation") =>
            new DesktopPluginStartupException(id, cause, running);
        var capabilityFailure = Callback(new InvalidDataException("Found invalid data while decoding."));
        Check(MainStartupPluginRecovery.Eligible(MainStartupMode.Installed, capabilityFailure), "Settled known optional failure is actionable.");
        Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.SourceLocal, capabilityFailure), "Source startup cannot change installed plugin enablement.");
        Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.AttachedExisting, capabilityFailure), "Read-only attachment cannot change plugin enablement.");
        Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.Installed, Callback(new InvalidOperationException(), running: true)), "Unsettled callbacks cannot offer recovery.");
        Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.Installed, Callback(new InvalidOperationException(), id: "cerebral.core")), "Core is never a disable target.");
        Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.Installed, new InvalidDataException()), "Unknown invalid data is never classified as a plugin recovery.");
        foreach (var error in new Exception[] { new SecurityException(), new UnauthorizedAccessException(),
                     new BadImageFormatException(), new FileLoadException(), new FileNotFoundException(), new TypeLoadException(),
                     new MissingMethodException(), new AggregateException(new SecurityException()) })
            Check(!MainStartupPluginRecovery.Eligible(MainStartupMode.Installed, Callback(error)), "Required security/assembly failure stays blocking: " + error.GetType().Name);

        var mutations = 0; var retries = 0;
        var choice = new MainStartupPluginRecovery("AI Dictation", () => mutations++);
        var window = CerebralStartupWindow.ForInstalled();
        window.ConfigureRetry(() => { retries++; window.BeginAttempt(); return Task.CompletedTask; });
        window.ShowFailure(new MainStartupException("Plugins / preferences", capabilityFailure, pluginRecovery: choice));
        Check(window.PluginRecoveryActionVisible && window.RetryActionVisible, "Settled cleanup result exposes explicit choices.");
        Check(mutations == 0 && retries == 0 && !choice.Consumed, "Displaying the prompt has no effect.");
        Check(choice.Explanation.Contains("Disabling is saved", StringComparison.Ordinal) &&
            choice.Explanation.Contains("preferences are preserved", StringComparison.Ordinal) &&
            choice.Explanation.Contains("Restoring preferences is unavailable", StringComparison.Ordinal), "Consequences and unavailable reset are explicit.");
        Check(window.DiagnosticText.Contains("Found invalid data while decoding.", StringComparison.Ordinal), "Original cause remains in bounded diagnostic details.");
        await window.DisableOptionalPluginAsync();
        Check(mutations == 1 && retries == 1 && choice.Consumed && !window.PluginRecoveryActionVisible, "Explicit disable is followed by one fresh startup attempt.");
        await window.DisableOptionalPluginAsync();
        Check(mutations == 1 && retries == 1, "Duplicate input cannot repeat the change.");
        var refusedRepeat = false;
        try { choice.Disable(); } catch (InvalidOperationException) { refusedRepeat = true; }
        Check(refusedRepeat && mutations == 1, "The exact recovery choice is single use independently of the UI.");

        var cleanupChoice = new MainStartupPluginRecovery("AI Dictation", () => mutations++);
        window.ShowFailure(new MainStartupException("Cleanup", capabilityFailure, retryAllowed: false, pluginRecovery: cleanupChoice));
        Check(!window.PluginRecoveryActionVisible && !window.RetryActionVisible, "Cleanup or callback refusal suppresses both retry and disable.");
        await window.DisableOptionalPluginAsync();
        Check(!cleanupChoice.Consumed && mutations == 1, "Hidden unsafe recovery cannot be invoked programmatically.");

        var uncertain = new MainStartupPluginRecovery("AI Dictation", () => throw new MainStartupException(
            "AI Dictation · disable outcome unconfirmed", new IOException("Unconfirmed write"), retryAllowed: false));
        window.ShowFailure(new MainStartupException("Plugins / preferences", capabilityFailure, pluginRecovery: uncertain));
        await window.DisableOptionalPluginAsync();
        Check(uncertain.Consumed && retries == 1 && !window.PluginRecoveryActionVisible && !window.RetryActionVisible,
            "An uncertain write is consumed and cannot continue or retry.");

        var attached = CerebralStartupWindow.ForAttached();
        attached.ConfigureRetry(() => Task.CompletedTask);
        attached.ShowFailure(new MainStartupException("Plugins", capabilityFailure, pluginRecovery: cleanupChoice));
        Check(!attached.PluginRecoveryActionVisible, "Attached startup never grants a mutation choice.");
        window.Close(); attached.Close();
        return passed + " focused assertions passed (pure policy and unshown STA WPF controls; injected disable/retry, no saved-file or runtime actions).";
    }
}
