namespace Cerebral.Worker.Tests;

internal static class DesktopPluginSdkBindingTests
{
    public static void RunStandalone()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-desktop-sdk-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assertions = 0;
        try
        {
            Run(root, (value, name) =>
            {
                if (!value) throw new InvalidOperationException("FAIL: " + name);
                assertions++;
                Console.WriteLine("PASS: " + name);
            });
            Console.WriteLine($"Desktop plugin SDK binding: {assertions} assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static void Run(string root, Action<bool, string> check)
    {
        var install = Path.Combine(root, "installed-desktop-sdk-binding");
        var desktop = Path.Combine(install, "Applications", "Desktop");
        Directory.CreateDirectory(desktop);
        var sdk = Path.Combine(desktop, DesktopPluginSdkBinding.AssemblyName);

        check(DesktopPluginSdkBinding.Resolve(null, localRun: false, installedPipe: true, install) is null,
            "installed Worker tolerates a worker-only deployment without a desktop SDK");
        File.WriteAllText(sdk, "fixture");
        check(DesktopPluginSdkBinding.Resolve(null, localRun: false, installedPipe: true, install) == Path.GetFullPath(sdk),
            "installed Worker discovers the exact SDK in the sibling Desktop payload");
        check(DesktopPluginSdkBinding.Resolve(null, localRun: true, installedPipe: false, install) is null,
            "source-local Worker still requires an explicit desktop SDK binding");
        check(DesktopPluginSdkBinding.Resolve(null, localRun: true, installedPipe: false, install,
                groupedPortable: true) == Path.GetFullPath(sdk),
            "admitted portable Worker discovers the grouped desktop SDK");
        check(DesktopPluginSdkBinding.Resolve(sdk, localRun: true, installedPipe: false, install) == Path.GetFullPath(sdk),
            "source-local Worker accepts one exact explicit desktop SDK binding");
        var alternate = Path.Combine(root, "sdk-override", DesktopPluginSdkBinding.AssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(alternate)!);
        File.WriteAllText(alternate, "override");
        check(DesktopPluginSdkBinding.Resolve(alternate, localRun: true, installedPipe: false, install,
                groupedPortable: true) == Path.GetFullPath(alternate),
            "explicit portable desktop SDK binding wins over the bundled SDK");

        var workerBase = Path.Combine(root, "Applications", "Worker");
        check(WorkerPluginPaths.ResolveServiceRoot(null, true, false, false, install, workerBase) ==
              Path.Combine(workerBase, "Plugins", "Service"),
            "source-local Worker retains its executable-relative service plugin default");
        check(WorkerPluginPaths.ResolveServiceRoot(null, true, false, true, install, workerBase) ==
              Path.Combine(install, "Plugins", "Service"),
            "admitted portable Worker selects the shared service plugin root");
        check(WorkerPluginPaths.ResolveServiceRoot(null, false, true, false, install, workerBase) ==
              Path.Combine(install, "Plugins", "Service"),
            "installed Worker retains the grouped service plugin root");
        var explicitPlugins = Path.Combine(root, "plugins-override");
        check(WorkerPluginPaths.ResolveServiceRoot(explicitPlugins, true, false, true, install, workerBase) ==
              explicitPlugins,
            "explicit portable service plugin root wins over the bundled root");

        var rejected = false;
        try { _ = DesktopPluginSdkBinding.Resolve(sdk, localRun: false, installedPipe: false, install); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "service and remote modes reject an explicit desktop SDK override");
    }
}
