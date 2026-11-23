using System.Security.Principal;
using System.Text.Json;

namespace Cerebral.Worker.Tests;

/// <summary>Command-boundary checks that stop before registry, SCM, ACL or filesystem mutation.</summary>
public static class InstalledWorkerRegistrationCommandTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: installed Worker registration commands require Windows.");
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The fixture requires an actual Windows account SID.");
        var differentSid = ownerSid == "S-1-5-21-100-200-300-1001"
            ? "S-1-5-21-100-200-300-1002"
            : "S-1-5-21-100-200-300-1001";
        var checks = 0;

        async Task Reject(string[] arguments, string expectedMessage, string label)
        {
            var prior = Console.Error;
            using var captured = new StringWriter();
            try
            {
                Console.SetError(captured);
                var result = await ManagementCommands.TryRunAsync(arguments);
                if (result != 1 || !captured.ToString().Contains(expectedMessage, StringComparison.Ordinal))
                    throw new InvalidOperationException("FAIL: " + label);
                checks++;
            }
            finally { Console.SetError(prior); }
        }

        await Reject(["manage", "inspect-owner-pipe", "--owner-sid", ownerSid],
            "requires each documented option exactly once", "Preview rejects a missing data path.");
        await Reject(["manage", "inspect-owner-pipe", "--owner-sid", ownerSid, "--owner-sid", ownerSid,
                "--data", @"C:\Fixture\Data"],
            "requires each documented option exactly once", "Preview rejects duplicate options.");
        await Reject(["manage", "register-owner-pipe", "--owner-sid", ownerSid,
                "--confirmed-owner-sid", differentSid, "--data", @"C:\Fixture\Data"],
            "does not match the previewed owner", "Apply rejects an owner different from the confirmed preview.");
        await Reject(["manage", "register-owner-pipe", "--owner-sid", ownerSid,
                "--confirmed-owner-sid", ownerSid, "--data", @"C:\Fixture\Data", "--silent", "true"],
            "accepts no other arguments", "Apply rejects undocumented generic deployment options.");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true,
            installedWorkerRegistrationCommandChecks = checks,
            evidence = "argument and explicit-owner confirmation rejection before protected-state access",
            registryRead = false,
            registryChanged = false,
            serviceChanged = false,
            filesystemChanged = false
        }));
    }
}
