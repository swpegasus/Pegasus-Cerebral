using System.Reflection;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Controlled checks of the real connection transition; no transport call or window is opened.</summary>
public static class ConnectedWindowConnectionChecks
{
    /// <summary>The supplied window must reject connection replacement while it owns a pending or uncertain mutation.</summary>
    public static async Task<int> VerifyRejectedSwitchAsync(ConnectedWindow window) =>
        await VerifyRejectedTransitionAsync(window, sameEndpoint: false) +
        await VerifyRejectedTransitionAsync(window, sameEndpoint: true);

    private static async Task<int> VerifyRejectedTransitionAsync(ConnectedWindow window, bool sameEndpoint)
    {
        window.Dispatcher.VerifyAccess();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ConnectedWindow);
        var clientField = type.GetField("client", flags)!;
        var connectionField = type.GetField("activeConnection", flags)!;
        var revisionField = type.GetField("appliedRevision", flags)!;
        var streamField = type.GetField("stream", flags)!;
        var connectedProperty = type.GetProperty("IsConnected", flags)!;
        var clientProperty = type.GetProperty("Client", flags)!;
        var originalConnected = (bool)connectedProperty.GetValue(window)!;
        var originalStream = streamField.GetValue(window);
        var originalClient = clientField.GetValue(window);
        var originalConnection = (ConnectionPreferences)connectionField.GetValue(window)!;
        var originalRevision = revisionField.GetValue(window);
        using var fixtureStream = new CancellationTokenSource();
        var fixtureToken = fixtureStream.Token;
        var cancellationObserved = false;
        using var cancellation = fixtureToken.Register(() => cancellationObserved = true);
        var checks = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); checks++; }
        try
        {
            // Seed only connection lifecycle state. The production owner still makes the rejection;
            // the private method is invoked directly to avoid saved settings or network access.
            connectedProperty.SetValue(window, true);
            streamField.SetValue(window, fixtureStream);
            var target = sameEndpoint ? originalConnection :
                new ConnectionPreferences { Url = originalConnection.Url == "http://127.0.0.1:1" ? "http://127.0.0.1:2" : "http://127.0.0.1:1" };
            var snapshot = new ConnectionSnapshot(target, "fixture-rejected-switch", true, null);
            var rejected = false;
            try
            {
                await ((Task)type.GetMethod("ConnectCoreAsync", flags)!.Invoke(window, [snapshot, false])!).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Pending or uncertain owner state must reject connection replacement before transport work, including the same endpoint.");
            Check(window.ServiceConnected && ReferenceEquals(clientProperty.GetValue(window), originalClient),
                "Rejected switching must preserve a usable original client through the guarded Client property.");
            Check(ReferenceEquals(clientField.GetValue(window), originalClient) && ReferenceEquals(connectionField.GetValue(window), originalConnection),
                "The original client and connection identity must remain unchanged.");
            Check(Equals(revisionField.GetValue(window), originalRevision),
                "A rejected saved connection must not be marked as applied.");
            Check(ReferenceEquals(streamField.GetValue(window), fixtureStream) && !cancellationObserved && !fixtureToken.IsCancellationRequested &&
                fixtureStream.Token.CanBeCanceled, "Rejected switching must neither cancel nor dispose the original event stream.");
            Check(window.ServiceStatusText.StartsWith("Connection change deferred:", StringComparison.Ordinal),
                "A rejected switch must explain its deferred state instead of claiming the original service is unavailable.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, rejectedConnectionChecks = checks, sameEndpoint,
                evidence = "controlled real ConnectedWindow transition with existing owner guards", networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            streamField.SetValue(window, originalStream);
            connectedProperty.SetValue(window, originalConnected);
        }
    }
}
