using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Actual current-user temporary pipe/Windows identity checks. The LocalService policy
/// examples are synthetic; this test does not enroll an owner or run an installed service.</summary>
public static class InstalledWorkerWindowsTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: installed Worker native primitives require Windows."); return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The fixture requires an actual Windows account SID.");
        if (!identity.IsAuthenticated || identity.IsSystem || process.SessionId <= 0)
        {
            Console.WriteLine("SKIP: native caller admission checks require an authenticated interactive user."); return;
        }
        var checks = 0; var nativeChecks = 0; var fixtureChecks = 0;
        void Native(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label); checks++; nativeChecks++;
        }
        void Fixture(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label); checks++; fixtureChecks++;
        }
        void Denied(Action action, string label, bool native)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { checks++; if (native) nativeChecks++; else fixtureChecks++; return; }
            throw new InvalidOperationException("FAIL: " + label);
        }
        var otherSid = sid == "S-1-5-21-100-200-300-1001" ? "S-1-5-21-100-200-300-1002" : "S-1-5-21-100-200-300-1001";
        var binding = new InstalledWorkerBinding(1, "123456781234123412341234567890ab", sid,
            InstalledWorkerBinding.ExpectedServiceName, InstalledWorkerBinding.LocalServiceSid,
            @"C:\Fixture\Worker\Cerebral.Worker.exe", @"C:\Fixture\Data");
        binding.Validate();
        var owner = InstalledWorkerEnrollment.ResolveOwner(sid);
        Native(owner.Sid == sid && !string.IsNullOrWhiteSpace(owner.DisplayName),
            "Enrollment resolves the selected actual SID as an individual Windows user for owner confirmation.");
        var prepared = InstalledWorkerEnrollment.Prepare(sid, binding.WorkerExecutable, binding.DataDirectory);
        Native(prepared.Owner == owner && !prepared.PreservesBindingId && prepared.Binding.OwnerSid == sid,
            "Fresh enrollment prepares a nonsecret owner binding without using an elevated-token claim.");
        var preserved = InstalledWorkerEnrollment.Prepare(sid, @"C:\Updated\Worker\Cerebral.Worker.exe", @"C:\Updated\Data", prepared.Binding);
        Native(preserved.PreservesBindingId && preserved.Binding.BindingId == prepared.Binding.BindingId &&
               preserved.Binding.WorkerExecutable == @"C:\Updated\Worker\Cerebral.Worker.exe",
            "Upgrade enrollment preserves the owner and installation identity while allowing explicit path updates.");
        var registration = InstalledWorkerRegistration.Prepare(sid, @"C:\Program Files\Pegasus\Cerebral\Applications\Worker\Cerebral.Worker.exe",
            @"C:\ProgramData\Pegasus\Cerebral", prepared.Binding);
        Native(registration.Enrollment.PreservesBindingId && registration.Enrollment.Binding.BindingId == prepared.Binding.BindingId &&
               registration.ServiceCommandLine == "\"C:\\Program Files\\Pegasus\\Cerebral\\Applications\\Worker\\Cerebral.Worker.exe\" --data \"C:\\ProgramData\\Pegasus\\Cerebral\" --installed-pipe",
            "Owner-pipe registration prepares the exact bounded service command while preserving the enrolled installation identity.");

        // Same-user kernel objects exercise the native calls without granting LocalService rights
        // or pretending that the current process is the installed service.
        var pipeName = "Cerebral.NativeFixture." + Guid.NewGuid().ToString("N");
        await using (var server = InstalledWorkerWindows.CreateServer(pipeName, sid, sid))
        {
            var waiting = server.WaitForConnectionAsync(deadline.Token);
            await using var client = await InstalledWorkerWindows.ConnectClientAsync(pipeName, deadline.Token);
            await waiting.WaitAsync(deadline.Token);
            var serverPeer = InstalledWorkerWindows.ObserveServer(client);
            var serverEndpoint = InstalledWorkerWindows.ObserveServerEndpoint(client);
            var clientPeer = InstalledWorkerWindows.ObserveClient(server);
            Native(serverPeer.Process.ProcessId == Environment.ProcessId && clientPeer.Process.ProcessId == Environment.ProcessId,
                "Both native pipe PIDs must identify the actual fixture process.");
            Native(serverPeer.Process.AccountSid == sid && clientPeer.Process.AccountSid == sid,
                "Both native pipe peers must identify the actual process account token.");
            Native(serverEndpoint.ProcessId == Environment.ProcessId && serverEndpoint.SessionId == process.SessionId,
                "The limited-rights server endpoint observation must identify the same native pipe process and session.");
            Native(serverPeer.Process.SessionId == process.SessionId && clientPeer.Process.SessionId == process.SessionId,
                "Both native pipe sessions must match the actual process session.");
            Native(serverPeer.Process.CreationTimeFileTime == process.StartTime.ToUniversalTime().ToFileTimeUtc()
                && serverPeer.Process.CreationTimeFileTime == clientPeer.Process.CreationTimeFileTime,
                "Native process creation time must match the actual process and be stable on both pipe ends.");
            Native(string.Equals(serverPeer.Process.Executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                && clientPeer.Process.Executable == serverPeer.Process.Executable,
                "Native executable identity must describe the real process, not hello JSON.");
            InstalledWorkerWindows.RequirePipeSecurity(serverPeer.Protection, sid, sid);
            InstalledWorkerWindows.RequirePipeSecurity(serverEndpoint.Protection, sid, sid);
            InstalledWorkerWindows.RequirePipeSecurity(clientPeer.Protection, sid, sid);
            Native(serverPeer.Protection.OwnerSid == sid && serverPeer.Protection.DaclProtected && serverPeer.Protection.Entries.Count == 1,
                "The created pipe must have its exact protected current-user test ACL.");
            Denied(() => InstalledWorkerWindows.RequirePipeSecurity(serverPeer.Protection, InstalledWorkerBinding.LocalServiceSid, sid),
                "An ordinary fixture pipe must not qualify as a LocalService-owned pipe.", native: true);

            // This is the bounded nonsecret first read required before inspecting the pipe token.
            await client.WriteAsync(new byte[] { 0x43 }, deadline.Token);
            var hello = new byte[1]; await server.ReadExactlyAsync(hello, deadline.Token);
            var caller = InstalledWorkerWindows.RequireOwnerCaller(server, binding);
            Native(caller.UserSid == sid && caller.SessionId == process.SessionId && caller.Authenticated && !caller.NetworkLogon && !caller.Anonymous,
                "Owner admission must read the actual identification token and native client session.");
            using (var remaining = WindowsIdentity.GetCurrent(ifImpersonating: true))
                Native(remaining is null, "Caller inspection must return to the service's own identity before continuing.");
            Denied(() => InstalledWorkerWindows.RequireOwnerCaller(server, binding with { OwnerSid = otherSid }),
                "Another enrolled owner must reject the observed caller token.", native: true);
            using (var remaining = WindowsIdentity.GetCurrent(ifImpersonating: true))
                Native(remaining is null, "Rejected owner admission must also revert caller impersonation.");
            await server.WriteAsync(new byte[] { 0x4f }, deadline.Token);
            await client.ReadExactlyAsync(hello, deadline.Token);
            Native(hello[0] == 0x4f, "Explicit client-only native access rights must support a real duplex exchange.");

            var firstRejected = false;
            try { using var duplicate = InstalledWorkerWindows.CreateServer(pipeName, sid, sid, first: true); }
            catch (IOException) { firstRejected = true; }
            Native(firstRejected, "An existing listener must exclude a second first-instance server.");
        }

        // An actual object with a distinct designated client verifies its ACL mask, without creating
        // another Windows account or claiming that this process runs as that account.
        await using (var ownerOnly = InstalledWorkerWindows.CreateServer("Cerebral.NativeAcl." + Guid.NewGuid().ToString("N"), sid, otherSid))
        {
            var protection = InstalledWorkerWindows.ReadPipeSecurity(ownerOnly.SafePipeHandle);
            InstalledWorkerWindows.RequirePipeSecurity(protection, sid, otherSid);
            Native(protection.Entries.Single(x => x.Sid == otherSid).AccessMask == InstalledWorkerWindows.ClientPipeAccess
                && (protection.Entries.Single(x => x.Sid == otherSid).AccessMask & 0x4) == 0,
                "The actual designated-client ACE must omit permission to create pipe server instances.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var rejected = false;
            try { using var unexpected = await InstalledWorkerWindows.ConnectClientAsync("Cerebral.Cancelled." + Guid.NewGuid().ToString("N"), cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Native(rejected, "Cancelled connection admission must stop without opening or retrying a command.");
        }

        var expected = new InstalledWorkerPipeProtection(InstalledWorkerBinding.LocalServiceSid, true, true,
            [new(InstalledWorkerBinding.LocalServiceSid, InstalledWorkerWindows.ServicePipeAccess), new(otherSid, InstalledWorkerWindows.ClientPipeAccess)]);
        InstalledWorkerWindows.RequirePipeSecurity(expected, InstalledWorkerBinding.LocalServiceSid, otherSid);
        Fixture(true, "Synthetic LocalService ACL policy accepts the exact intended descriptor.");
        Denied(() => InstalledWorkerWindows.RequirePipeSecurity(expected with { DaclProtected = false }, InstalledWorkerBinding.LocalServiceSid, otherSid),
            "An inherited/unprotected synthetic service ACL must fail closed.", native: false);
        Denied(() => InstalledWorkerWindows.RequirePipeSecurity(expected with { OwnerSid = sid }, InstalledWorkerBinding.LocalServiceSid, otherSid),
            "A synthetic descriptor cannot claim the enrolled owner as the service object owner.", native: false);
        Denied(() => InstalledWorkerWindows.RequirePipeSecurity(expected with
        {
            Entries = [new(InstalledWorkerBinding.LocalServiceSid, InstalledWorkerWindows.ServicePipeAccess), new(otherSid, InstalledWorkerWindows.ClientPipeAccess | 0x4)]
        }, InstalledWorkerBinding.LocalServiceSid, otherSid), "Synthetic owner server-creation rights must be rejected.", native: false);
        Denied(() => InstalledWorkerWindows.RequirePipeSecurity(expected with
        {
            Entries = [.. expected.Entries, new("S-1-1-0", InstalledWorkerWindows.ClientPipeAccess)]
        }, InstalledWorkerBinding.LocalServiceSid, otherSid), "A synthetic Everyone access grant must be rejected.", native: false);
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, installedWorkerWindowsChecks = checks, nativeChecks, fixtureChecks,
            evidence = "actual current-user temporary Windows pipes, peer metadata, ACLs and caller token; synthetic LocalService ACL policy",
            installedServiceTested = false, registryChanged = false, serviceChanged = false }));
    }
}
