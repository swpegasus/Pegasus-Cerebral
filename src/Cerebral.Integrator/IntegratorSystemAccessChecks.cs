using System.Windows;
using System.Windows.Automation;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    // These native-control fixtures never enable, replace, revoke, or read a real credential.
    private int CheckSystemAccessControls()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        var previous = confirmedFullAccess;
        try
        {
            Check(AutomationProperties.GetAutomationId(fullAccessEnabled) == "ConfigurationFullAccessEnabled",
                "System access must expose one labeled enabled-state control.");
            ApplyFullAccessStatus(new(false, Message: "Fixture: system access is disabled."));
            Check(fullAccessEnabled.IsChecked == false && fullAccessEnabled.IsEnabled &&
                !replaceFullAccessAuthorization.IsEnabled, "A disabled receipt must leave replacement unavailable.");
            Check(fullAccessDetails.Text.Contains("Fixture", StringComparison.Ordinal) && fullAccessState.ToolTip is null,
                "Access details must be visible on the page rather than hidden in a tooltip.");
            ApplyFullAccessStatus(new(true, Remembered: true, Message: "Fixture: saved authorization."));
            Check(fullAccessEnabled.IsChecked == true && replaceFullAccessAuthorization.IsEnabled &&
                fullAccessState.Text.Contains("saved", StringComparison.Ordinal), "Saved authorization must be distinguished from a session.");
            changingFullAccess = true;
            ApplyFullAccessStatus(new(true, Message: "Fixture: connected session."));
            Check(!fullAccessEnabled.IsEnabled && !replaceFullAccessAuthorization.IsEnabled,
                "An in-flight authorization mutation must not accept another toggle or replacement.");
            changingFullAccess = false;
            ApplyFullAccessStatus(new(true, Message: "Fixture: connected session."));
            Check(fullAccessState.Text.Contains("session", StringComparison.Ordinal),
                "A session-only receipt must not claim persistence.");
            ApplyFullAccessStatus(new(false), available: false, unavailableReason: "Fixture: installed Worker required.");
            Check(!fullAccessEnabled.IsEnabled && !replaceFullAccessAuthorization.IsEnabled &&
                fullAccessDetails.Text.Contains("installed Worker", StringComparison.Ordinal),
                "A known unsupported Worker must explain its mode and block unavailable mutations.");
            SetFullAccessUnavailable();
            Check(confirmedFullAccess is null && fullAccessEnabled.IsChecked is null &&
                !fullAccessEnabled.IsEnabled && !replaceFullAccessAuthorization.IsEnabled,
                "Unknown state must not be presented as an enabled or disabled success.");
            Check(fullAccessDetails.Text.Contains("No access change is confirmed", StringComparison.Ordinal),
                "Unknown state must describe recovery without replaying authorization.");
        }
        finally
        {
            changingFullAccess = false;
            if (previous is not null) ApplyFullAccessStatus(previous);
            else SetFullAccessUnavailable();
        }
        return checks;
    }
}
