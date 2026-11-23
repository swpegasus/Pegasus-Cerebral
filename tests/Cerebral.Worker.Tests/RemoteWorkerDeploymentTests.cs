using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class RemoteWorkerDeploymentTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }

        Check(CerebralDefaults.WorkerPort == 2667 && CerebralDefaults.WorkerUrl == "http://127.0.0.1:2667",
            "new Workers use configurable port 2667 as the shared default");
        var checkedPorts = new List<int>();
        var selected = WorkerListenEndpoint.Resolve("http://0.0.0.0:2667", true, (_, port) =>
        {
            checkedPorts.Add(port);
            return port == 2669;
        });
        Check(selected == "http://0.0.0.0:2669" && checkedPorts.SequenceEqual([2667, 2668, 2669]),
            "automatic Worker selection advances deterministically to the first available port");
        Check(WorkerListenEndpoint.Resolve("http://127.0.0.1:7123", false) == "http://127.0.0.1:7123",
            "automatic selection can be disabled for an exact configured port");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-remote-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(ManagementCommands.ServiceStartMode == "delayed-auto",
                "Worker service registration uses automatic delayed startup independent of the Cerebral desktop");
            var firewallExecutable = Path.Combine(root, "Cerebral.Worker.exe");
            File.WriteAllText(firewallExecutable, "fixture");
            var firewall = WorkerFirewallPlan.Create(firewallExecutable, "http://0.0.0.0:2669");
            Check(firewall is
                {
                    RuleName: WorkerFirewallPlan.OwnedRuleName,
                    RuleGroup: WorkerFirewallPlan.OwnedRuleGroup,
                    LocalPort: 2669,
                    Profiles: WorkerFirewallPlan.PrivateProfile,
                    RemoteAddresses: "LocalSubnet"
                } && firewall.ProgramPath == Path.GetFullPath(firewallExecutable),
                "remote Worker firewall policy is program-bound, private-profile, local-subnet and exact-port");
            Check(Rejects(() => WorkerFirewallPlan.Create(firewallExecutable, "http://127.0.0.1:2669")),
                "local owner-pipe and loopback Workers cannot receive the LAN firewall rule");

            var identity = Path.Combine(root, "worker identity");
            var knownHosts = Path.Combine(root, "worker known hosts");
            File.WriteAllText(identity, "fixture");
            File.WriteAllText(knownHosts, "fixture");
            var worker = new WorkerRegistration
            {
                Id = "gb10", Name = "GB10", Url = "http://10.10.10.15:6123/",
                SshHost = "10.10.10.15", SshUser = "codexsvc", SshPort = 22,
                SshIdentityFile = identity, SshKnownHostsFile = knownHosts
            };

            var ssh = ManagementCommands.RemoteTransportArguments(worker, false);
            var scp = ManagementCommands.RemoteTransportArguments(worker, true);
            Check(ssh.Contains("StrictHostKeyChecking=yes") && ssh.Contains("UserKnownHostsFile=" + Path.GetFullPath(knownHosts)) &&
                  ssh.Contains("IdentitiesOnly=yes") && ssh.Contains(Path.GetFullPath(identity)) && ssh.Contains("-p"),
                "SSH lifecycle calls pin the host identity and the configured client identity using separate arguments");
            Check(scp.Contains("StrictHostKeyChecking=yes") && scp.Contains("UserKnownHostsFile=" + Path.GetFullPath(knownHosts)) &&
                  scp.Contains("IdentitiesOnly=yes") && scp.Contains("-P"),
                "SCP deployment uses the same pinned trust policy and its correct port option");
            Check(ManagementCommands.RemoteServiceListenUrl(worker) == "http://0.0.0.0:6123",
                "a registered LAN origin maps to the explicit wildcard service listener on its registered port");
            worker.Url = "http://127.0.0.1:7123/";
            Check(ManagementCommands.RemoteServiceListenUrl(worker) == "http://127.0.0.1:7123",
                "a loopback registration remains loopback-only during deployment");
            worker.Url = "https://10.10.10.15:6123/";
            Check(Rejects(() => ManagementCommands.RemoteServiceListenUrl(worker)),
                "deployment refuses an HTTPS claim because the installer has no certificate binding contract");
            worker.Url = "http://10.10.10.15:6123/path";
            Check(Rejects(() => ManagementCommands.RemoteServiceListenUrl(worker)),
                "deployment refuses a non-origin Worker URL instead of silently changing its meaning");
            worker.Url = "http://10.10.10.15:6123/";
            File.Delete(knownHosts);
            Check(Rejects(() => ManagementCommands.RemoteTransportArguments(worker, false)),
                "a configured but missing pinned host-key database fails before launching OpenSSH");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Remote Worker deployment: {assertions} trust and listener policy checks passed; no network request or remote mutation was used.");
    }
}
