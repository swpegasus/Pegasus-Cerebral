using System.Reflection;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal static class IntegratorConnectionStartupChecks
{
    public static int Verify()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException(message);
        }

        var missing = new ConnectionSnapshot(new(), "default", false, null);
        Check(IntegratorWindow.ResolveDefaultTransport(missing, false, true) == "installed-user-pipe",
            "An unconfigured durable desktop must select its enrolled owner Worker.");
        Check(IntegratorWindow.ResolveDefaultTransport(missing, true, true) == "pipe",
            "A source or portable Configuration must retain its isolated local root despite installed enrollment.");
        Check(IntegratorWindow.ResolveDefaultTransport(missing, true, false) == "pipe",
            "A source or portable Configuration can prepare its local Worker without installed enrollment.");
        Reject(() => IntegratorWindow.ResolveDefaultTransport(missing, false, false),
            "Missing durable enrollment must remain offline instead of selecting HTTP or another data store.");

        var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Local.fixture", "11111111111111111111111111111111");
        var savedValues = new[]
        {
            new ConnectionPreferences { Url = "http://127.0.0.1:1", AutoStartLocalServices = false },
            ConnectionPreferences.ForLocalPipe(endpoint, @"C:\fixture\data", false),
            ConnectionPreferences.ForInstalledUserPipe(false),
            ConnectionPreferences.ForInstalledPipe(false)
        };
        foreach (var value in savedValues)
        {
            var saved = new ConnectionSnapshot(value, "saved-revision", true, null);
            foreach (var isolated in new[] { true, false })
                Check(IntegratorWindow.ResolveDefaultTransport(saved, isolated, true) == value.Transport &&
                      !value.AutoStartLocalServices,
                    "Configuration must preserve an explicit " + value.Transport + " connection and disabled startup.");
        }

        var invalid = new ConnectionSnapshot(null, "invalid", true, "Invalid saved connection");
        Reject(() => IntegratorWindow.ResolveDefaultTransport(invalid, false, true),
            "Invalid saved declarations must be diagnosed before installed default selection.");
        Reject(() => IntegratorWindow.ResolveDefaultTransport(invalid, true, false),
            "Invalid saved declarations must be preserved instead of creating another local Worker.");
        Check(IntegratorWindow.InstalledDefaultMatchesDataDirectory(@"C:\fixture\Data\", @"c:\FIXTURE\data"),
            "Installed default root matching must accept normalized Windows path casing and trailing separators.");
        Check(!IntegratorWindow.InstalledDefaultMatchesDataDirectory(@"C:\fixture\Data", @"C:\fixture\OtherData"),
            "An enrolled Worker may not redirect an unconfigured desktop to another durable data root.");

        // Exercise the exact private gate shared by initial connection, manual reconnect and monitor,
        // without constructing a Window, contacting a Worker or reading/writing live preferences.
        var require = typeof(ConnectedWindow).GetMethod("RequireConnectionPreferences",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The shared Configuration connection guard is unavailable.");
        ConnectionPreferences Guard(ConnectionSnapshot snapshot, bool configuration)
        {
            try { return (ConnectionPreferences)require.Invoke(null, [snapshot, configuration])!; }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException cause)
            { throw cause; }
        }
        Reject(() => Guard(missing, true),
            "The connection monitor must refuse default HTTP while local preparation is unavailable.");
        Reject(() => Guard(invalid, true),
            "The connection monitor must refuse an invalid saved declaration.");
        foreach (var value in savedValues)
            Check(ReferenceEquals(Guard(new(value, "saved", true, null), true), value),
                "The shared guard must preserve the existing " + value.Transport + " declaration.");
        Check(ReferenceEquals(Guard(missing, false), missing.Preferences),
            "Consumer startup compatibility must remain scoped outside Configuration.");

        return checks;
    }
}
