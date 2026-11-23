using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

// Runs only in a fresh private STA qualification process. It constructs unshown WPF windows
// over empty owned catalogs; it never starts a Worker or enters normal product startup.
internal static class ConfigurationManagedPluginConsumerChecks
{
    internal static int VerifyLegacy()
    {
        if (Application.Current is not null)
            throw new InvalidOperationException("Run the legacy consumer fixture in a fresh private process.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-managed-legacy-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        var childUi = Path.Combine(root, "configuration-ui");
        Directory.CreateDirectory(worker);
        var options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing",
            "Pegasus.Cerebral.Fixture." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            worker, childUi, "fixture-installation"])!;
        Application? app = null;
        IntegratorWindow? window = null;
        var checks = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new InvalidOperationException("Legacy Configuration consumer: " + label);
            checks++; Console.WriteLine("PASS legacy Configuration consumer: " + label);
        }
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = IntegratorAttachedWorkerStartup.CreateWindow(options);
            Ui.ApplyTheme(app);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var catalog = (DesktopPluginCatalog)typeof(IntegratorWindow).GetField("desktopCatalog", flags)!.GetValue(window)!;
            var data = (string)typeof(IntegratorWindow).GetField("desktopPluginDataRoot", flags)!.GetValue(window)!;
            var paths = (DesktopProductPaths)typeof(IntegratorWindow).GetMethod("ConfigurationProductPaths", flags)!.Invoke(window, null)!;
            Check(catalog.RootDirectory == Path.Combine(childUi, "Plugins", "Desktop"),
                "legacy catalog remains below its own UI root");
            Check(data == Path.Combine(childUi, "PluginData") && paths.UserDataDirectory == childUi,
                "legacy storage and product paths remain self-owned");
            Check(DesktopInstancePaths.Current.UserDataDirectory == childUi &&
                ScopedFilePolicy.IsWithin(ConnectionStore.FilePath, childUi),
                "legacy preferences remain on their unchanged private state");
            Check(window.ExistingWorkerAttachment is { } attached && attached.Endpoint == options.Endpoint &&
                attached.WorkerDataDirectory == worker && attached.InstallationId == options.InstallationId,
                "legacy factory retains its exact Worker binding");
            Check(!window.IsVisible && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero &&
                !window.ServiceConnected, "legacy factory fixture has no shown window or normal Worker startup");
            Console.WriteLine($"Legacy Configuration consumer fixture: {checks} checks passed; no product window shown.");
            return checks;
        }
        finally
        {
            try { window?.Close(); }
            finally
            {
                app?.Shutdown();
                var full = Path.GetFullPath(root);
                if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(full).StartsWith("cerebral-managed-legacy-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Legacy fixture cleanup escaped its owned temporary root.");
                Directory.Delete(full, recursive: true);
            }
        }
    }

    internal static int Verify()
    {
        if (Application.Current is not null)
            throw new InvalidOperationException("Run the managed consumer fixture in a fresh private process.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-managed-consumer-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        var mainUi = Path.Combine(root, "main-ui");
        var childUi = Path.Combine(root, "configuration-ui");
        var catalog = Path.Combine(root, "explicit-catalog");
        var data = Path.Combine(root, "explicit-plugin-data");
        Directory.CreateDirectory(worker);
        var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Fixture." + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var main = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", endpoint.PipeName, endpoint.InstanceId,
            worker, mainUi, "fixture-installation"])!;
        string[] legacy = ["--attach-existing", endpoint.PipeName, endpoint.InstanceId, worker, childUi, "fixture-installation"];
        var child = ExistingWorkerDesktopOptions.FromArguments(legacy)!;
        var selectedPaths = DesktopInstancePaths.ForManagedPluginOwnerSnapshot(mainUi,
            Path.Combine(root, "main-install"), Path.Combine(root, "main-base"), true);
        var owner = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, child, catalog, data, selectedPaths);
        var arguments = legacy.ToList();
        owner.AppendArguments(arguments);
        var managed = arguments.ToArray();
        var checks = 0;
        var skipped = new List<string>();
        Application? app = null;
        IntegratorWindow? window = null;
        string? link = null;
        void Check(bool success, string label)
        {
            if (!success) throw new InvalidOperationException("Managed Configuration consumer: " + label);
            checks++;
            Console.WriteLine("PASS managed Configuration consumer: " + label);
        }
        void NoChildEffects(string label) => Check(Application.Current is null && !Directory.Exists(childUi) &&
            !Directory.Exists(catalog) && !Directory.Exists(data),
            label + ": no private UI/plugin roots or Application");

        try
        {
            Check(managed.Length == 14, "producer binding yields the agreed closed managed14 form");
            var parsed = ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(managed);
            Check(parsed.Options == child && parsed.OwnerBinding?.OwnerBindingId == owner.OwnerBindingId,
                "consumer accepts the shared producer binding without a second parser");
            Check(ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(legacy).OwnerBinding is null,
                "legacy six-argument startup remains self-owned");
            Check(!ScopedFilePolicy.IsWithin(catalog, mainUi) && !ScopedFilePolicy.IsWithin(data, mainUi),
                "fixture uses actual independent external owner roots");
            NoChildEffects("valid parsing");
            var entry = typeof(Program).GetMethod("RunWpf", BindingFlags.Static | BindingFlags.NonPublic)!;
            void Refuse(string label, string[] supplied)
            {
                var refused = false;
                try { entry.Invoke(null, [supplied]); }
                catch (TargetInvocationException error) when (error.InnerException is ArgumentException or IOException)
                { refused = true; }
                Check(refused, label + ": actual startup rejects the invalid binding");
                NoChildEffects(label);
            }
            foreach (var orphan in new[]
            {
                new[] { ConfigurationManagedPluginOwnerBinding.ArgumentName },
                new[] { "--managed-plugin-owner-unknown", "orphan" },
                new[] { "--MANAGED-PLUGIN-OWNER-V1" },
                new[] { "--MaNaGeD-PlUgIn-OwNeR-v999" },
                managed[6..]
            })
            {
                var attachedCalls = 0;
                var standaloneCalls = 0;
                var refused = false;
                try
                {
                    Program.DispatchStartup(orphan,
                        supplied => { attachedCalls++; return IntegratorAttachedWorkerStartup.Run(supplied); },
                        _ => { standaloneCalls++; return 91; });
                }
                catch (ArgumentException) { refused = true; }
                Check(refused && attachedCalls == 1 && standaloneCalls == 0,
                    "orphan private suffix dispatches once to validation and never reaches default startup");
                NoChildEffects("orphan private dispatch");
                Refuse("orphan private suffix through actual RunWpf", orphan);
            }
            foreach (var normal in new[] { Array.Empty<string>(), new[] { "--inspect-desktop-instance" } })
            {
                var attachedCalls = 0; var standaloneCalls = 0;
                var result = Program.DispatchStartup(normal,
                    _ => { attachedCalls++; return 82; }, _ => { standaloneCalls++; return 83; });
                Check(result == 83 && standaloneCalls == 1 && attachedCalls == 0,
                    "normal/default dispatch retains its existing standalone route");
                NoChildEffects("injected normal dispatch");
            }
            Refuse("partial suffix", managed[..^1]);
            Refuse("duplicate suffix", [.. managed, ConfigurationManagedPluginOwnerBinding.ArgumentName]);
            var wrongFlag = (string[])managed.Clone(); wrongFlag[6] = "--managed-plugin-owner-unknown";
            Refuse("unknown suffix", wrongFlag);
            var missingCatalog = (string[])managed.Clone(); missingCatalog[8] = "";
            Refuse("missing catalog member", missingCatalog);
            var missingData = (string[])managed.Clone(); missingData[9] = "";
            Refuse("missing data member", missingData);
            var wrongWorker = (string[])managed.Clone(); wrongWorker[2] = Guid.NewGuid().ToString("N");
            Refuse("wrong Worker generation", wrongWorker);
            var sharedUi = (string[])managed.Clone(); sharedUi[7] = childUi;
            Refuse("overlapping Main and Configuration UI state", sharedUi);
            var workerCatalog = (string[])managed.Clone(); workerCatalog[8] = worker;
            Refuse("catalog overlaps Worker data", workerCatalog);
            var unc = (string[])managed.Clone(); unc[8] = @"\\fixture-invalid\share\catalog";
            Refuse("UNC owner catalog", unc);
            var ads = (string[])managed.Clone(); ads[9] = data + ":stream";
            Refuse("alternate-stream plugin data", ads);
            var invalidPortable = (string[])managed.Clone(); invalidPortable[12] = "TRUE";
            Refuse("noncanonical portable flag", invalidPortable);

            try
            {
                var target = Path.Combine(root, "link-target"); Directory.CreateDirectory(target);
                link = Path.Combine(root, "linked-catalog");
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                skipped.Add("reparse creation unavailable: " + error.GetType().Name);
                Console.WriteLine("UNAVAILABLE managed Configuration consumer: reparse fixture creation");
                link = null;
            }
            if (link is not null)
            {
                var linked = (string[])managed.Clone(); linked[8] = link;
                Refuse("reparse owner catalog", linked);
                Directory.Delete(link); link = null;
            }

            var wrongChild = child with { Endpoint = endpoint with { InstanceId = Guid.NewGuid().ToString("N") } };
            var factoryRefused = false;
            try { _ = IntegratorAttachedWorkerStartup.CreateWindow(wrongChild, owner); }
            catch (ArgumentException) { factoryRefused = true; }
            Check(factoryRefused, "internal factory revalidates the owner before creating a window");
            NoChildEffects("internal factory wrong generation");

            var defaults = owner.ProductPaths();
            var expectedDefaults = selectedPaths.ProductPaths();
            Check(defaults == expectedDefaults, "managed default projection uses the authoritative Main product snapshot");

            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = IntegratorAttachedWorkerStartup.CreateWindow(child, owner);
            Ui.ApplyTheme(app);
            Check(!window.IsVisible && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "factory remains an unshown WPF fixture without an HWND or normal startup");
            Check(window.ExistingWorkerAttachment is { } fixedWorker && fixedWorker.Endpoint == endpoint &&
                fixedWorker.WorkerDataDirectory == worker && fixedWorker.InstallationId == "fixture-installation",
                "factory retains the exact existing Worker binding");
            Check(DesktopInstancePaths.Current.UserDataDirectory == childUi &&
                ScopedFilePolicy.IsWithin(ConnectionStore.FilePath, childUi),
                "preferences and receiver context retain independent Configuration UI state");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var actualCatalog = (DesktopPluginCatalog)typeof(IntegratorWindow).GetField("desktopCatalog", flags)!.GetValue(window)!;
            var actualData = (string)typeof(IntegratorWindow).GetField("desktopPluginDataRoot", flags)!.GetValue(window)!;
            Check(actualCatalog.RootDirectory == catalog && actualData == data,
                "actual window catalog and storage use the paired explicit Main owner roots");
            var settings = new HostSettings
            {
                ProjectsDirectory = Path.Combine(root, "projects"),
                ModelDirectory = Path.Combine(root, "models"),
                ToolsDirectory = Path.Combine(root, "tools"),
                SharedDirectory = Path.Combine(root, "shared")
            };
            typeof(IntegratorWindow).GetField("settings", flags)!.SetValue(window, settings);
            var pathsMethod = typeof(IntegratorWindow).GetMethod("ConfigurationProductPaths", flags)!;
            var projected = (DesktopProductPaths)pathsMethod.Invoke(window, null)!;
            Check(projected.DataDirectory == mainUi && projected.UserDataDirectory == mainUi &&
                projected.InstallDirectory == selectedPaths.InstallDirectory &&
                projected.BaseDirectory == selectedPaths.BaseDirectory && projected.Portable,
                "actual managed plugin projection uses Main data/install/base/portable");
            Check(projected.ProjectsDirectory == settings.ProjectsDirectory && projected.ModelsDirectory == settings.ModelDirectory &&
                projected.ToolsDirectory == settings.ToolsDirectory && projected.SharedDirectory == settings.SharedDirectory,
                "actual managed projection retains the same Worker's current dynamic settings");
            Check(DesktopInstancePaths.Current.UserDataDirectory == childUi && !window.ServiceConnected,
                "plugin projection leaves UI context and disconnected fixture Worker unchanged");
            window.Close(); window = null;

            Check(typeof(IntegratorWindow).GetConstructor(flags, binder: null,
                [typeof(bool), typeof(bool), typeof(DesktopPluginCatalog), typeof(string), typeof(bool)], modifiers: null) is not null,
                "existing internal constructor metadata remains available to legacy callers");
            Console.WriteLine($"Managed Configuration consumer fixture: {checks} checks passed; unavailable={skipped.Count}; no product window shown.");
            return checks;
        }
        finally
        {
            try { window?.Close(); }
            finally
            {
                app?.Shutdown();
                if (link is not null && Directory.Exists(link)) Directory.Delete(link);
                var full = Path.GetFullPath(root);
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (!ScopedFilePolicy.IsWithin(full, temp) ||
                    !Path.GetFileName(full).StartsWith("cerebral-managed-consumer-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Fixture cleanup escaped its owned temporary root.");
                Directory.Delete(full, recursive: true);
            }
        }
    }
}
