using Cerebral.Client;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class WindowsUserLaunchTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows user launch checks"); return; }
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        var context = WindowsUserLaunch.InspectCurrentToken();
        Check(context.SessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId, "Native token session must match this process.");
        Check(context.Integrity > 0, "Native token integrity must be read successfully.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-launch-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var version = typeof(WindowsUserLaunch).Assembly.GetName().Version!.ToString(3);
            void Rejected(string path)
            {
                try { WindowsUserLaunch.ValidateConfigurationTarget(path); throw new InvalidOperationException("Invalid Configuration installation accepted."); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FileNotFoundException) { checks++; }
            }
            Rejected("relative-install");
            Rejected(root);
            var marker = Path.Combine(root, "installation.ini");
            foreach (var body in new[]
            {
                "[Cerebral]\nProduct=AnotherProduct\nPortable=0\nVersion=" + version,
                "[Cerebral]\nProduct=PegasusCerebral\nPortable=1\nVersion=" + version,
                "[Cerebral]\nProduct=PegasusCerebral\nPortable=0\nVersion=0.0.0",
                "[Cerebral]\nProduct=PegasusCerebral\nProduct=PegasusCerebral\nPortable=0\nVersion=" + version,
                "[Cerebral]\nProduct=PegasusCerebral\nPortable=0\nVersion=" + version
            })
            {
                File.WriteAllText(marker, body);
                Rejected(root);
            }
            foreach (var args in new[]
            {
                new[] { "manage", "launch-configuration" },
                new[] { "manage", "launch-configuration", "--directory", root, "--argument", "unexpected" },
                new[] { "manage", "inspect-configuration-launch", "--executable", "another.exe" },
                new[] { "manage", "inspect-configuration-launch", "--directory", "" }
            })
                Check(await ManagementCommands.TryRunAsync(args) == 1, "Launch commands must reject generic or malformed arguments.");
            Console.WriteLine($"PASS: {checks} Configuration launch checks; native current token session={context.SessionId}, elevated={context.Elevated}, integrity={context.Integrity}. No application was launched; elevated linked-token creation is not exercised.");
        }
        finally
        {
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != expectedParent || !Path.GetFileName(root).StartsWith("cerebral-launch-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected launch test cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }
}
