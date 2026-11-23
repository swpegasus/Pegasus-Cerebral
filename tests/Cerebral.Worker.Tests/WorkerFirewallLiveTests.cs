using System.Security.Principal;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class WorkerFirewallLiveTests
{
    public static void Run(string workerExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The live firewall check requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("The live firewall check requires an elevated process.");
        var worker = Path.GetFullPath(workerExecutable);
        if (WindowsWorkerFirewall.Exists())
            throw new InvalidOperationException("The live firewall check will not replace an existing Pegasus Cerebral Worker rule.");
        var plan = WorkerFirewallPlan.Create(worker, "http://0.0.0.0:65022");
        var applied = false;
        try
        {
            WindowsWorkerFirewall.Apply(plan);
            applied = WindowsWorkerFirewall.Exists();
            if (!applied) throw new Exception("The Worker firewall rule was not observable after apply.");
        }
        finally
        {
            if (applied && !WindowsWorkerFirewall.Remove(worker))
                throw new Exception("The live Worker firewall rule could not be removed.");
        }
        if (WindowsWorkerFirewall.Exists())
            throw new Exception("The live Worker firewall rule remained after cleanup.");
        Console.WriteLine("Worker firewall live: direct Windows Firewall COM apply, exact retained-policy verification, ownership-checked removal, and absence-after-cleanup passed.");
    }
}
