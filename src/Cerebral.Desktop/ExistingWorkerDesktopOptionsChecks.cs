using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Pure component/Worker identity checks; no files, instance handles, connections or windows are opened.</summary>
public static class ExistingWorkerDesktopOptionsChecks
{
    public static int Verify()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var endpoint = new LocalWorkerEndpoint("cerebral-shared-options-fixture", "00112233445566778899aabbccddeeff");
        var options = new ExistingWorkerDesktopOptions(endpoint, @"C:\Fixture\Worker", @"C:\Fixture\Desktop", "fixture-installation");
        const string rootHash = "1BDC2639B558B322613CC5EA6B1F3B222722C3567CA04D832A5AEA0772E6C2A5";
        Check(options.ApplicationIdentity == DesktopApplicationIdentity.Application(
                "attach-existing:15F619D3C05D9664389AB37DDCDBAF5CF0800B1067B8A936257D6164E80443EC"),
            "Shared attachment preserves the existing Worker identity hash exactly.");
        Check(options.GetApplicationId("Orchestrator") == "Pegasus.Cerebral.Orchestrator.Attached." + rootHash,
            "Orchestrator preserves its existing desktop application ID exactly.");
        Check(options.GetApplicationId("Integrator") == "Pegasus.Cerebral.Integrator.Attached." + rootHash &&
              options.GetApplicationId("Integrator") != options.GetApplicationId("Orchestrator"),
            "Desktop components use distinct instance IDs for the same private desktop state root.");
        Check(options.GetApplicationId("Integrator.Tools-2") == "Pegasus.Cerebral.Integrator.Tools-2.Attached." + rootHash,
            "The shared instance contract accepts ASCII component letters, digits, periods and hyphens.");

        var otherDesktop = options with { DesktopDataDirectory = @"C:\Fixture\OtherDesktop" };
        Check(options.ApplicationIdentity == otherDesktop.ApplicationIdentity &&
              options.ApplicationIdentity.Matches(otherDesktop.ApplicationIdentity),
            "Separate desktop components/state roots retain the same exact Worker binding identity.");
        Check(options.GetApplicationId("Orchestrator") != otherDesktop.GetApplicationId("Orchestrator") &&
              options.GetApplicationId("Integrator") != otherDesktop.GetApplicationId("Integrator"),
            "Separate desktop state roots do not share either component's instance ID.");
        Check(options.GetApplicationId("Integrator") ==
              (options with { DesktopDataDirectory = @"c:\fixture\desktop" }).GetApplicationId("Integrator"),
            "Desktop root case normalization preserves the existing Windows identity contract.");
        Check(options.ApplicationIdentity == (options with { WorkerDataDirectory = @"c:\fixture\worker" }).ApplicationIdentity,
            "Worker root case normalization preserves the existing Worker identity contract.");

        foreach (var changedBinding in new[]
        {
            options with { Endpoint = endpoint with { PipeName = "cerebral-shared-options-other" } },
            options with { Endpoint = endpoint with { InstanceId = "ffeeddccbbaa99887766554433221100" } },
            options with { WorkerDataDirectory = @"C:\Fixture\OtherWorker" },
            options with { InstallationId = "other-installation" }
        })
            Check(options.ApplicationIdentity != changedBinding.ApplicationIdentity,
                "Changing any captured Worker binding field must change the requested Worker identity.");

        foreach (var component in new string[] { null!, "", " ", "a b", "a/b", @"a\b", "a_b", "a:b", "a\nb", "a\0b", "İntegrator", "☃" })
        {
            var refused = false;
            try { _ = options.GetApplicationId(component); }
            catch (ArgumentException error) when (error.ParamName == "component") { refused = true; }
            Check(refused, "Unsafe or empty desktop components must be rejected before instance creation.");
        }

        return checks;
    }
}
