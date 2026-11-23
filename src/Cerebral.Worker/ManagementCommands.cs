using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.ServiceProcess;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;
using Cerebral.Client;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

/// <summary>Management remains callable when the HTTP worker is unavailable.</summary>
public static class ManagementCommands
{
    public const string ServiceName = "PegasusCerebral";
    internal const string ServiceStartMode = "delayed-auto";

    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "manage") return null;
        try
        {
            string action = args.ElementAtOrDefault(1) ?? "help";
            string? Option(string name) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
            bool Flag(string name) => args.Contains(name, StringComparer.Ordinal);
            if (action == "help")
            {
                Console.WriteLine("manage status|start|stop|install|deploy|uninstall|remote|download-installer|verify\n" +
                    "deploy/install --installer PATH (or --url URL) [--directory PATH] [--portable] [--silent] [--wait]\n" +
                    "uninstall [--directory PATH] [--portable] [--silent]\n" +
                    "download-installer --url URL [--destination PATH]\nverify --data PATH [--url URL] [--expected-version VERSION]\n" +
                    "remote --profile worker.json --action status|start|stop|deploy [--source Workers/linux-arm64/Worker] [--sudo]\n" +
                    "pair-worker --profile worker.json (imports one scoped peer connection through the installed owner pipe)\n" +
                    "peer-smoke --id WORKER-ID [--remote-profile PROFILE-ID] (creates/updates one mapped helper and runs a bounded assignment)\n" +
                    "provision-standalone --data PATH --name NAME --listen-url http://0.0.0.0:PORT [--auto-port] (stopped service, installer bootstrap)\n" +
                    "finalize-standalone --data PATH --advertised-host HOST --output FILE.CerebralWorker (pins selected port and writes a one-time invitation)\n" +
                    "register-http-service --data PATH (elevated Windows standalone Worker service registration)\n" +
                    "configure-firewall --data PATH|remove-firewall (elevated, private-profile local-subnet Worker rule)\n" +
                    "connector --action status|start|stop|provision [--connector-data PATH] [--worker PATH] [--settings PATH --key-file PATH]\n" +
                    "cortex-locate\ncortex-prerequisite [--directory PATH] [--data-directory PATH]\n" +
                    "inspect-engine-packages --directory PATH (read-only static engine package validation; no plugin loading)\n" +
                    "installation-paths (read-only resolved core locations)\n" +
                    "project-path --data PATH --directory PATH [--projects PATH] (read-only)\n" +
                    "set-project-path --data PATH --directory PATH [--projects PATH] [--models PATH] [--tools PATH] [--shared PATH] (stopped service, installer only)\n" +
                    "prepare-user-data --directory PATH (installer only)\n" +
                    "install-model-package --data PATH --package PATH (running service; package must be staged below data)\n" +
                    "require-model --data PATH (running service; verifies at least one usable registered model)\n" +
                    "launch-configuration|inspect-configuration-launch --directory PATH\n" +
                    "inspect-owner-pipe --owner-sid SID --data PATH (read-only setup preview)\n" +
                    "register-owner-pipe --owner-sid SID --confirmed-owner-sid SID --data PATH (elevated, stopped service)\n" +
                    "Deployment uses NSIS; register/remove-service remain legacy lifecycle hooks for installations predating the protected owner pipe.");
                return 0;
            }
            if (action == "inspect-engine-packages")
            {
                if (args.Length != 4 || args[2] != "--directory")
                    throw new ArgumentException("Use inspect-engine-packages with one absolute --directory and no other options.");
                var report = await EnginePackageInspection.InspectAsync(args[3]);
                Console.WriteLine(JsonSerializer.Serialize(report, CerebralJson.Options));
                return 0;
            }
            if (action is "cortex-locate" or "cortex-prerequisite")
            {
                var options = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var index = 2; index < args.Length; index += 2)
                {
                    if (action == "cortex-locate" || index + 1 >= args.Length ||
                        args[index] is not ("--directory" or "--data-directory") || !options.TryAdd(args[index], args[index + 1]))
                        throw new ArgumentException("Use cortex-locate without options, or cortex-prerequisite with explicit --directory and/or --data-directory paths.");
                }
                var installed = InstalledCortexDiscovery.ReadRegistered()
                    ?? throw new InvalidDataException("Install Pegasus Cortex before installing Cerebral.");
                object report = action == "cortex-locate" ? installed :
                    CortexPrerequisite.Inspect(installed, options.GetValueOrDefault("--directory"), options.GetValueOrDefault("--data-directory"));
                Console.WriteLine(JsonSerializer.Serialize(report, CerebralJson.Options)); return 0;
            }
            if (action == "installation-paths")
            {
                if (args.Length != 2) throw new ArgumentException("Use installation-paths without options.");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    @base = InstallationPaths.BaseDirectory,
                    install = InstallationPaths.InstallDirectory,
                    data = InstallationPaths.DataDirectory,
                    tools = InstallationPaths.ToolsDirectory,
                    userData = InstallationPaths.CurrentUserDataDirectory,
                    portable = InstallationPaths.IsPortable
                }, CerebralJson.Options));
                return 0;
            }
            if (action is "launch-configuration" or "inspect-configuration-launch")
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Configuration requires Windows.");
                if (args.Length != 4 || args[2] != "--directory" || string.IsNullOrWhiteSpace(args[3]))
                    throw new ArgumentException("Use launch-configuration or inspect-configuration-launch with one --directory path and no application arguments.");
                object result = action == "launch-configuration" ? WindowsUserLaunch.OpenConfiguration(args[3]) : WindowsUserLaunch.Inspect(args[3]);
                Console.WriteLine(JsonSerializer.Serialize(result, CerebralJson.Options)); return 0;
            }
            if (action is "project-path" or "set-project-path") return await ProjectPathAsync(action, args);
            if (action is "inspect-owner-pipe" or "register-owner-pipe")
                return await OwnerPipeRegistrationAsync(action, args);
            if (action == "prepare-user-data") return await PrepareUserDataAsync(
                Option("--directory") ?? throw new ArgumentException("Provide --directory for the per-user data root."));
            if (action == "install-model-package") return await InstallModelPackageAsync(
                Option("--data") ?? throw new ArgumentException("Provide --data for the installed service."),
                Option("--package") ?? throw new ArgumentException("Provide --package with the staged Cerebral model package."));
            if (action == "require-model") return await RequireModelAsync(
                Option("--data") ?? throw new ArgumentException("Provide --data for the installed service."));
            if (action == "connector") return await ConnectorAsync(Option("--action") ?? "status", Option("--connector-data"), Option("--settings"), Option("--key-file"), Option("--worker"));
            if (action == "remote") return await RemoteAsync(Option("--profile"), Option("--action") ?? "status", Option("--source"), Flag("--sudo"));
            if (action == "pair-worker") return await PairWorkerAsync(Option("--profile"));
            if (action == "peer-smoke") return await PeerSmokeAsync(Option("--id"), Option("--remote-profile"));
            if (action == "provision-standalone") return ProvisionStandalone(args);
            if (action == "finalize-standalone") return FinalizeStandalone(args);
            if (action == "register-http-service") return await RegisterHttpServiceAsync(args);
            if (action == "configure-firewall") return ConfigureFirewall(args);
            if (action == "remove-firewall") return RemoveFirewall(args);
            if (action == "download-installer")
            {
                var downloaded = await DownloadInstallerAsync(Option("--url") ?? throw new ArgumentException("Provide --url."), Option("--destination"));
                Console.WriteLine(JsonSerializer.Serialize(new { downloaded = true, path = downloaded }, CerebralJson.Options));
                return 0;
            }
            if (action == "verify")
            {
                await VerifyHealthAsync(Option("--data") ?? throw new ArgumentException("Provide --data for the installed service."), Option("--url"), Option("--expected-version"));
                return 0;
            }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local service management uses Windows. On Linux use systemctl or setup/install-linux.sh.");
            if (action is "install" or "deploy" or "uninstall")
            {
                var directory = Path.GetFullPath(Option("--directory") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pegasus", "Cerebral"));
                string? installer = action == "uninstall" ? Path.Combine(directory, "Uninstall.exe") : Option("--installer");
                if (action != "uninstall" && Option("--url") is { } url) installer = await DownloadInstallerAsync(url);
                if (installer is null) throw new ArgumentException("Provide --installer PATH or --url URL.");
                installer = Path.GetFullPath(installer);
                if (!File.Exists(installer) || !installer.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Installer executable does not exist.");
                var portable = Flag("--portable");
                var wait = Flag("--wait");
                if (wait && Path.GetFullPath(AppContext.BaseDirectory).StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Cannot wait while executing from the installation being updated. Omit --wait so this process releases its files.");
                var start = new ProcessStartInfo(installer) { UseShellExecute = true, Verb = portable ? "open" : "runas", WorkingDirectory = Path.GetDirectoryName(installer)! };
                // NSIS requires /D to be the last raw argument, without quotes, even for spaces.
                if (directory.Contains('"') || directory.Contains('\r') || directory.Contains('\n')) throw new ArgumentException("Invalid installation directory.");
                start.Arguments = (portable && action != "uninstall" ? "/PORTABLE " : "") + (Flag("--silent") ? "/S " : "") + (action == "uninstall" ? "" : "/D=" + directory);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Installer did not launch.");
                Console.WriteLine(JsonSerializer.Serialize(new { installerLaunched = true, processId = process.Id, completed = false, message = "Close Pegasus Cerebral Configuration and Pegasus Cerebral during update. The NSIS installer reports completion or failure." }, CerebralJson.Options));
                if (wait) { await process.WaitForExitAsync(); return process.ExitCode; }
                return 0;
            }
            if (action == "status")
            {
                using var service = new ServiceController(ServiceName);
                try { Console.WriteLine(JsonSerializer.Serialize(new { service = ServiceName, installed = true, state = service.Status.ToString() }, CerebralJson.Options)); }
                catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 }) { Console.WriteLine("{\"service\":\"PegasusCerebral\",\"installed\":false}"); }
                return 0;
            }
            if (action is "stop" or "start" or "remove-service")
            {
                using var service = new ServiceController(ServiceName);
                try
                {
                    var status = service.Status;
                    if (action == "start")
                    {
                        if (status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40));
                        if (status != ServiceControllerStatus.Running)
                        {
                            if (status != ServiceControllerStatus.StartPending) service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(40));
                        }
                    }
                    else if (status != ServiceControllerStatus.Stopped)
                    {
                        if (status != ServiceControllerStatus.StopPending) service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40));
                    }
                }
                catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 } && action != "start") { return 0; }
                if (action == "remove-service") return await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["delete", ServiceName]);
                Console.WriteLine($"{ServiceName}: {(action == "start" ? "Running" : "Stopped")}");
                return 0;
            }
            if (action == "register")
            {
                var data = Path.GetFullPath(Option("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pegasus", "Cerebral"));
                if (data == Path.GetPathRoot(data)) throw new ArgumentException("A product-specific data folder is required.");
                Directory.CreateDirectory(data);
                // LocalService needs only the product's mutable state, not machine-wide privileges.
                var acl = await RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"), [data, "/grant", "*S-1-5-19:(OI)(CI)M"]);
                if (acl != 0) return acl;
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
                var binary = QuoteWindows(exe) + " --data " + QuoteWindows(data);
                var installed = false;
                using (var service = new ServiceController(ServiceName))
                {
                    try { _ = service.Status; installed = true; }
                    catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
                }
                return await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), [installed ? "config" : "create", ServiceName, "binPath=", binary, "start=", ServiceStartMode, "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Pegasus Cerebral"]);
            }
            throw new ArgumentException($"Unknown management action: {action}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = error.Message }, CerebralJson.Options));
            return 1;
        }
    }

    private static int ProvisionStandalone(string[] args)
    {
        var options = ExactOptions(args, ["--data", "--name", "--listen-url"], ["--auto-port"]);
        var data = ProductDataPath(options["--data"]);
        RequireInstallerAuthority();
        RequireStoppedServiceIfWindows();
        var configuration = new Configuration(new StateStore(data));
        var saved = configuration.ProvisionStandalone(options["--name"], options["--listen-url"], options.ContainsKey("--auto-port"));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            provisioned = true,
            state = configuration.ManagementStatus().State,
            saved.InstallationId,
            saved.Name,
            saved.ListenUrl,
            saved.AutoSelectAvailablePort,
            expiresAt = saved.ProvisioningExpiresAt
        }, CerebralJson.Options));
        return 0;
    }

    private static int FinalizeStandalone(string[] args)
    {
        var options = ExactOptions(args, ["--data", "--advertised-host", "--output"], []);
        var data = ProductDataPath(options["--data"]);
        RequireInstallerAuthority();
        var configuration = new Configuration(new StateStore(data));
        var status = configuration.ManagementStatus();
        if (status.State == "assigned")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                finalized = false,
                state = status.State,
                message = "The Worker is already assigned. Setup preserved its Cerebral controller and did not emit another bootstrap credential."
            }, CerebralJson.Options));
            return 0;
        }
        var output = Path.GetFullPath(options["--output"]);
        if (!output.EndsWith(WorkerProvisioningPackage.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provisioning output must use the .CerebralWorker extension.");
        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Provisioning output folder does not exist.");
        var host = options["--advertised-host"].Trim();
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            host is "0.0.0.0" or "127.0.0.1" or "localhost")
            throw new ArgumentException("Advertised host must be a concrete LAN DNS name or IPv4 address.");
        var receipt = Path.Combine(data, WorkerListenEndpoint.ReceiptFileName);
        if (!File.Exists(receipt)) throw new InvalidOperationException("The Worker has not published its selected endpoint. Start and verify it before finalizing provisioning.");
        var selected = File.ReadLines(receipt).FirstOrDefault()?.Trim()
            ?? throw new InvalidDataException("The Worker endpoint receipt is empty.");
        if (!Uri.TryCreate(selected, UriKind.Absolute, out var listener) || listener.Host != "0.0.0.0" || listener.Port is < 1 or > 65535)
            throw new InvalidDataException("The Worker endpoint receipt is not a standalone LAN listener.");
        var saved = configuration.PinStandaloneEndpoint(listener.GetLeftPart(UriPartial.Authority));
        var provisioningId = saved.ProvisioningId ?? throw new InvalidOperationException("The standalone Worker is already assigned.");
        var expires = saved.ProvisioningExpiresAt ?? throw new InvalidOperationException("The standalone Worker has no active provisioning invitation.");
        var package = new WorkerProvisioningPackage("worker-" + saved.InstallationId[..12], saved.Name,
            $"http://{host}:{listener.Port}/", saved.InstallationId, provisioningId, expires, saved.AccessKey);
        if (File.Exists(output))
        {
            var existing = WorkerProvisioningPackage.Parse(File.ReadAllBytes(output));
            if (existing != package)
                throw new IOException("The provisioning output already exists for different Worker state. Choose a new output name or remove the obsolete invitation explicitly.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                finalized = true,
                invitation = output,
                endpoint = package.Url,
                expiresAt = package.ExpiresAt,
                containsBearerCredential = true,
                reused = true
            }, CerebralJson.Options));
            return 0;
        }
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(package.Serialize());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            finalized = true,
            invitation = output,
            endpoint = package.Url,
            expiresAt = package.ExpiresAt,
            containsBearerCredential = true
        }, CerebralJson.Options));
        return 0;
    }

    private static async Task<int> RegisterHttpServiceAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service registration is available only on Windows.");
        var options = ExactOptions(args, ["--data"], []);
        var data = ProductDataPath(options["--data"]);
        RequireInstallerAuthority();
        var configuration = new Configuration(new StateStore(data));
        var settings = configuration.Get();
        if (!settings.StandaloneWorker || settings.Coordinator || !settings.AllowLan)
            throw new InvalidOperationException("Provision the standalone LAN Worker before registering its service.");
        var installed = false;
        using (var service = new ServiceController(ServiceName))
        {
            try
            {
                installed = true;
                if (service.Status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException("Stop the Cerebral Worker before registering or updating its HTTP service.");
            }
            catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
            { installed = false; }
        }
        Directory.CreateDirectory(data);
        if (await RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"), [data, "/grant", "*S-1-5-19:(OI)(CI)M"]) != 0)
            throw new IOException("Could not grant the LocalService Worker access to its data directory.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable path is unavailable.");
        var binary = QuoteWindows(executable) + " --data " + QuoteWindows(data);
        if (await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"),
                [installed ? "config" : "create", ServiceName, "binPath=", binary, "start=", ServiceStartMode, "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Pegasus Cerebral Worker"]) != 0)
            throw new IOException("The standalone Cerebral Worker service could not be registered.");
        Console.WriteLine(JsonSerializer.Serialize(new { registered = true, service = ServiceName, data, startMode = ServiceStartMode, started = false }, CerebralJson.Options));
        return 0;
    }

    private static int ConfigureFirewall(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall provisioning is available only on Windows.");
        var options = ExactOptions(args, ["--data"], []);
        var data = ProductDataPath(options["--data"]);
        RequireInstallerAuthority();
        var settings = new Configuration(new StateStore(data)).Get();
        if (!settings.StandaloneWorker || settings.Coordinator || !settings.AllowLan)
            throw new InvalidOperationException("Only a provisioned standalone LAN Worker can receive the remote Worker firewall rule.");
        var receipt = Path.Combine(data, WorkerListenEndpoint.ReceiptFileName);
        if (!File.Exists(receipt)) throw new FileNotFoundException("The Worker has not recorded its finalized listening endpoint.", receipt);
        var endpoint = File.ReadAllText(receipt).Trim();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable path is unavailable.");
        var plan = WorkerFirewallPlan.Create(executable, endpoint);
        WindowsWorkerFirewall.Apply(plan);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            configured = true,
            rule = plan.RuleName,
            profile = "Private",
            remoteAddresses = plan.RemoteAddresses,
            protocol = "TCP",
            localPort = plan.LocalPort,
            program = plan.ProgramPath
        }, CerebralJson.Options));
        return 0;
    }

    private static int RemoveFirewall(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Firewall provisioning is available only on Windows.");
        _ = ExactOptions(args, [], []);
        RequireInstallerAuthority();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable path is unavailable.");
        var removed = WindowsWorkerFirewall.Remove(executable);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            removed,
            rule = WorkerFirewallPlan.OwnedRuleName,
            program = executable
        }, CerebralJson.Options));
        return 0;
    }

    private static Dictionary<string, string> ExactOptions(string[] args, IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> flags)
    {
        var allowed = required.Concat(flags).ToHashSet(StringComparer.Ordinal);
        var flagSet = flags.ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowed.Contains(name) || values.ContainsKey(name))
                throw new ArgumentException("Standalone Worker provisioning received an unknown or duplicate option: " + name);
            if (flagSet.Contains(name)) { values[name] = "true"; continue; }
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Standalone Worker provisioning option requires a value: " + name);
            values[name] = args[index];
        }
        if (required.Any(name => !values.ContainsKey(name)))
            throw new ArgumentException("Standalone Worker provisioning requires: " + string.Join(", ", required));
        return values;
    }

    private static string ProductDataPath(string value)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (path == Path.GetPathRoot(path)) throw new ArgumentException("A product-specific Worker data directory is required.");
        return path;
    }

    private static void RequireInstallerAuthority()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Standalone Worker provisioning requires elevated setup.");
    }

    private static void RequireStoppedServiceIfWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var service = new ServiceController(ServiceName);
        try
        {
            if (service.Status != ServiceControllerStatus.Stopped)
                throw new InvalidOperationException("Stop the Cerebral Worker before changing bootstrap provisioning.");
        }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
    }

    private static async Task<int> OwnerPipeRegistrationAsync(string action, string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed owner-pipe registration requires Windows.");
        var allowed = action == "inspect-owner-pipe"
            ? new HashSet<string>(["--owner-sid", "--data"], StringComparer.Ordinal)
            : new HashSet<string>(["--owner-sid", "--confirmed-owner-sid", "--data"], StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !allowed.Contains(args[index]) ||
                !options.TryAdd(args[index], args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException(action + " requires each documented option exactly once and accepts no other arguments.");
        if (!allowed.All(options.ContainsKey) || options.Count != allowed.Count)
            throw new ArgumentException(action + " requires each documented option exactly once.");
        var ownerSid = options["--owner-sid"];
        if (action == "register-owner-pipe" && options["--confirmed-owner-sid"] != ownerSid)
            throw new InvalidOperationException("The confirmed Cerebral owner SID does not match the previewed owner.");
        var data = Path.GetFullPath(options["--data"]);
        if (data == Path.GetPathRoot(data)) throw new ArgumentException("A product-specific data folder is required.");
        var worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Cerebral.Worker.exe"));
        var existing = InstalledWorkerBindingStore.ReadRegistered();
        var plan = InstalledWorkerRegistration.Prepare(ownerSid, worker, data, existing);
        if (action == "inspect-owner-pipe")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ready = true,
                owner = plan.Enrollment.Owner.DisplayName,
                ownerSid = plan.Enrollment.Owner.Sid,
                service = plan.Enrollment.Binding.ServiceName,
                worker = plan.Enrollment.Binding.WorkerExecutable,
                data = plan.Enrollment.Binding.DataDirectory,
                preservesBindingId = plan.Enrollment.PreservesBindingId,
                changesMachine = false,
                next = "Show this owner and these paths for confirmation before invoking register-owner-pipe."
            }, CerebralJson.Options));
            return 0;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Registering the installed owner pipe requires elevated setup.");
        var installed = false;
        using (var service = new ServiceController(ServiceName))
        {
            try
            {
                var status = service.Status; installed = true;
                if (status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException("Stop the Cerebral Worker before registering or updating its owner pipe.");
            }
            catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
        }
        Directory.CreateDirectory(data);
        var acl = await RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            [data, "/grant", "*S-1-5-19:(OI)(CI)M"]);
        if (acl != 0) throw new IOException("Could not grant the LocalService Worker access to its data directory.");
        var configured = await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"),
            [installed ? "config" : "create", ServiceName, "binPath=", plan.ServiceCommandLine,
                "start=", ServiceStartMode, "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Pegasus Cerebral"]);
        if (configured != 0)
            throw new IOException("The installed Cerebral service could not be configured for its owner pipe.");
        var observed = InstalledWorkerWindows.ReadService();
        if (observed.State != InstalledWorkerServiceState.Stopped || observed.ServiceName != plan.Enrollment.Binding.ServiceName ||
            observed.AccountSid != plan.Enrollment.Binding.ServiceAccountSid ||
            !InstalledWorkerBinding.PathEquals(observed.Executable, plan.Enrollment.Binding.WorkerExecutable, false) ||
            !InstalledWorkerBinding.PathEquals(observed.DataDirectory, plan.Enrollment.Binding.DataDirectory, true))
            throw new IOException("The stopped Windows service does not match the prepared Cerebral owner-pipe plan; enrollment was not written.");
        var enrolled = InstalledWorkerEnrollment.WriteRegistered(plan.Enrollment);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            registered = true,
            owner = plan.Enrollment.Owner.DisplayName,
            ownerSid = enrolled.OwnerSid,
            service = enrolled.ServiceName,
            worker = enrolled.WorkerExecutable,
            data = enrolled.DataDirectory,
            preservesBindingId = plan.Enrollment.PreservesBindingId,
            started = false,
            message = "The stopped service and protected owner binding match. Start the service as a separate lifecycle step."
        }, CerebralJson.Options));
        return 0;
    }

    private static async Task<int> ProjectPathAsync(string action, string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installer project-path commands require Windows.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index += 2)
            if (index + 1 >= args.Length || args[index] is not ("--data" or "--directory" or "--projects" or "--models" or "--tools" or "--shared") ||
                !options.TryAdd(args[index], args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("Use project-path or set-project-path with one explicit --data, --directory and optional --projects, --models, --tools and --shared paths.");
        if (!options.TryGetValue("--data", out var dataValue) || !options.TryGetValue("--directory", out var installValue))
            throw new ArgumentException("Both --data and --directory are required.");
        var cortex = InstalledCortexDiscovery.ReadRegistered() ?? throw new InvalidDataException("Install Pegasus Cortex before configuring Cerebral installation paths.");
        var install = InstalledCortexDiscovery.ValidateLocalDirectory(InstalledCortexDiscovery.ValidateSeparateDirectory(cortex, installValue));
        var data = InstalledCortexDiscovery.ValidateLocalDirectory(InstalledCortexDiscovery.ValidateSeparateDirectory(cortex, dataValue));
        if (DirectoryInside(data, install) || DirectoryInside(install, data))
            throw new ArgumentException("Service data must remain separate from replaceable program files.");
        var previous = ReadInstallerSettings(data);
        var requested = options.GetValueOrDefault("--projects") ??
            InstallationPaths.RegisteredProjectsDirectory(install, data) ?? previous?.ProjectsDirectory;
        if (string.IsNullOrWhiteSpace(requested)) requested = DefaultInstalledProjectsDirectory();
        var projects = ValidateInstallerProjectDirectory(requested, data, install, cortex);
        if (action == "project-path")
        {
            // One bounded path is the NSIS field value. Errors go to stderr and return a nonzero exit.
            Console.WriteLine(projects); return 0;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Setting installation paths requires the elevated Cerebral installer.");
        using (var service = new ServiceController(ServiceName))
        {
            try
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException("Stop the Cerebral worker before changing installation project paths.");
            }
            catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
        }
        Directory.CreateDirectory(data);
        // This is the same ownership lock as Program.cs; it also rejects a source-run worker on this store.
        using var stateLock = new FileStream(Path.Combine(data, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        previous = ReadInstallerSettings(data);
        // Without an explicit change, re-read after acquiring ownership so an update preserves the latest setting.
        if (!options.ContainsKey("--projects"))
            requested = InstallationPaths.RegisteredProjectsDirectory(install, data) ??
                (string.IsNullOrWhiteSpace(previous?.ProjectsDirectory) ? DefaultInstalledProjectsDirectory() : previous.ProjectsDirectory);
        projects = ValidateInstallerProjectDirectory(requested, data, install, cortex);
        Directory.CreateDirectory(projects);
        projects = ValidateInstallerProjectDirectory(projects, data, install, cortex);
        var models = ValidateInstallerMutableDirectory(options.GetValueOrDefault("--models") ?? previous?.ModelDirectory ?? Path.Combine(data, "models"), install, cortex, "Model");
        var tools = ValidateInstallerMutableDirectory(options.GetValueOrDefault("--tools") ?? previous?.ToolsDirectory ?? Path.Combine(data, "Tools"), install, cortex, "Portable tool");
        var shared = ValidateInstallerMutableDirectory(options.GetValueOrDefault("--shared") ?? previous?.SharedDirectory ?? Path.Combine(data, "Shared"), install, cortex, "Shared");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(shared);
        foreach (var (path, label) in new[] { (projects, "project"), (models, "model"), (tools, "portable tool"), (shared, "shared") })
        {
            var acl = await RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"), [path, "/grant", "*S-1-5-19:(OI)(CI)M"]);
            if (acl != 0) throw new IOException($"Could not grant the worker access to the selected {label} folder. The prior saved locations are unchanged.");
        }
        var store = new StateStore(data);
        var settings = previous ?? new HostSettings
        {
            AccessKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
            ModelDirectory = Path.Combine(data, "models"), ToolsDirectory = Path.Combine(data, "Tools"), SharedDirectory = Path.Combine(data, "Shared")
        };
        var bundledRuntime = Path.Combine(install, "Applications", "Worker", "runtime",
            OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        if (File.Exists(bundledRuntime) && (string.IsNullOrWhiteSpace(settings.LlamaServerPath) || !File.Exists(settings.LlamaServerPath)))
            settings.LlamaServerPath = bundledRuntime;
        // Preserve the complete saved settings and change this one installer-owned selection only.
        settings.ProjectsDirectory = projects;
        settings.ModelDirectory = models;
        settings.ToolsDirectory = tools;
        settings.SharedDirectory = shared;
        new Configuration(store).SaveInstalledPaths(settings);
        Console.WriteLine(JsonSerializer.Serialize(new { configured = true, projectsDirectory = projects, modelDirectory = models, toolsDirectory = tools, sharedDirectory = shared }, CerebralJson.Options));
        return 0;
    }

    private static string DefaultInstalledProjectsDirectory()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(systemRoot))
            throw new InvalidOperationException("Windows did not provide a local system-drive root for the default Cerebral workspace.");
        return Path.Combine(systemRoot, "CerebralProjects");
    }

    private static string ValidateInstallerMutableDirectory(string value, string install, InstalledCortexLocation cortex, string label)
    {
        var path = InstalledCortexDiscovery.ValidateLocalDirectory(value);
        var installPath = InstalledCortexDiscovery.ValidateLocalDirectory(install);
        if (DirectoryInside(path, installPath) || DirectoryInside(installPath, path))
            throw new ArgumentException($"{label} files must remain outside replaceable program files.");
        InstalledCortexDiscovery.ValidateSeparateDirectory(cortex, path);
        if (path == Path.GetPathRoot(path)) throw new ArgumentException($"{label} files require a product-specific directory.");
        return path;
    }

    private static async Task<int> InstallModelPackageAsync(string dataValue, string packageValue)
    {
        var data = InstalledCortexDiscovery.ValidateLocalDirectory(dataValue);
        var packagePath = Path.GetFullPath(packageValue);
        if (!ModelPackageContract.IsPackagePath(packagePath) || !File.Exists(packagePath))
            throw new ArgumentException($"The staged package must be an existing {ModelPackageContract.PrimaryExtension} file.");
        if (!DirectoryInside(packagePath, data))
            throw new ArgumentException("Stage the model package below the service data directory so LocalService can read it.");

        ModelPackage package;
        long weightBytes;
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            if (archive.Entries.Count is < 2 or > 3 || archive.GetEntry("manifest.json") is not { } metadata || archive.GetEntry("model.gguf") is not { } weights)
                throw new InvalidDataException("The staged file is not a Cerebral model package.");
            if (metadata.Length > 65_536) throw new InvalidDataException("Model package manifest exceeds 64 KiB.");
            await using var stream = metadata.Open();
            package = await JsonSerializer.DeserializeAsync<ModelPackage>(stream, CerebralJson.Options)
                ?? throw new InvalidDataException("Model package manifest is empty.");
            ModelPackageContract.ValidateLayout(package, archive.Entries.Select(x => x.FullName));
            if (!package.Package.Starter || string.IsNullOrWhiteSpace(package.Package.Id))
                throw new InvalidDataException("The adjacent package must be a named Pegasus Cerebral starter model package.");
            weightBytes = weights.Length;
        }

        var settings = ReadInstallerSettings(data) ?? throw new InvalidDataException("The service has not created its settings yet.");
        using var client = CreateManagementClient(data, settings);
        var models = await client.GetAsync<List<ModelRecord>>("models.list");
        var candidates = models.Where(x => (x.VisionProjectorPath is not null) == (package.Version == 2));
        var selected = candidates.FirstOrDefault(x => x.PackageId == package.Package.Id && x.PackageVersion == package.Package.Version)
            ?? candidates.FirstOrDefault(x => x.SizeBytes == weightBytes && x.Name.Equals(package.Model.Name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(package.Model.Quantization) || x.Quantization.Equals(package.Model.Quantization, StringComparison.OrdinalIgnoreCase)));
        var imported = false;
        if (selected is null)
        {
            selected = await client.GetAsync<ModelRecord>("models.package.import", new { path = packagePath });
            imported = true;
        }

        var agents = await client.GetAsync<List<AgentProfile>>("agents.list");
        if (agents.Any(x => x.Role == "lead"))
            _ = await client.GetAsync<JsonElement>("models.default.switch", new { modelId = selected.Id });
        else
        {
            selected = await client.GetAsync<ModelRecord>("models.load", new { id = selected.Id });
            _ = await client.GetAsync<List<AgentProfile>>("agents.bootstrap", new { modelId = selected.Id });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            installed = true,
            imported,
            modelId = selected.Id,
            selected.Name,
            packageId = package.Package.Id,
            packageVersion = package.Package.Version,
            selected.License,
            selected.UpstreamModelUrl,
            configuredAsDefault = true
        }, CerebralJson.Options));
        return 0;
    }

    private static async Task<int> RequireModelAsync(string dataValue)
    {
        var data = InstalledCortexDiscovery.ValidateLocalDirectory(dataValue);
        var settings = ReadInstallerSettings(data) ?? throw new InvalidDataException("The service has not created its settings yet.");
        using var client = CreateManagementClient(data, settings);
        var models = await client.GetAsync<List<ModelRecord>>("models.list");
        var usable = UsableModels(models);
        string? recoveryError = null;
        if (usable.Count == 0)
        {
            // A prior runtime-path failure is retained on the model record. Once
            // setup has repaired the bundled runtime path, one real load is the
            // strongest bounded proof that the existing registration is usable.
            var candidate = models.FirstOrDefault(model => model.Id == settings.DefaultModelId && ModelFilesExist(model))
                ?? models.FirstOrDefault(ModelFilesExist);
            if (candidate is not null)
            {
                try
                {
                    _ = await client.GetAsync<ModelRecord>("models.load", new { id = candidate.Id });
                    models = await client.GetAsync<List<ModelRecord>>("models.list");
                    usable = UsableModels(models);
                }
                catch (Exception error) { recoveryError = error.Message; }
            }
        }
        if (usable.Count == 0)
            throw new InvalidDataException("Pegasus Cerebral requires at least one registered model whose local model file is available and loadable." +
                (string.IsNullOrWhiteSpace(recoveryError) ? "" : " Runtime verification failed: " + recoveryError));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            usableModels = usable.Count,
            models = usable.Select(model => new { model.Id, model.Name, model.PackageId, model.PackageVersion }).ToList()
        }, CerebralJson.Options));
        return 0;
    }

    private static bool ModelFilesExist(ModelRecord model) =>
        !string.IsNullOrWhiteSpace(model.Path) && File.Exists(model.Path) &&
        (string.IsNullOrWhiteSpace(model.VisionProjectorPath) || File.Exists(model.VisionProjectorPath));

    private static List<ModelRecord> UsableModels(IEnumerable<ModelRecord> models) =>
        models.Where(model => string.IsNullOrWhiteSpace(model.Error) && ModelFilesExist(model)).ToList();

    /// <summary>
    /// Installer management commands must use the transport owned by the registered service.
    /// An installed owner-pipe service intentionally has no HTTP listener, so falling back to
    /// its saved HTTP preference would make model import and verification fail after health had
    /// already succeeded over the protected pipe.
    /// </summary>
    private static CerebralClient CreateManagementClient(string data, HostSettings settings)
    {
        if (OperatingSystem.IsWindows() && InstalledWorkerBindingStore.ReadRegistered() is { } binding &&
            InstalledWorkerBinding.PathEquals(data, binding.DataDirectory, true))
            return CerebralClient.ForInstalledWorker();

        var address = new UriBuilder(settings.ListenUrl);
        if (address.Host is "0.0.0.0" or "::" or "[::]") address.Host = "127.0.0.1";
        return new CerebralClient(address.Uri.AbsoluteUri, settings.AccessKey);
    }

    private static async Task<int> PrepareUserDataAsync(string directoryValue)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Per-user installer paths require Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Preparing the installed user-data root requires the elevated Cerebral installer.");
        var directory = InstalledCortexDiscovery.ValidateLocalDirectory(directoryValue);
        if (directory == Path.GetPathRoot(directory)) throw new ArgumentException("A product-specific user-data root is required.");
        Directory.CreateDirectory(directory);
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var result = await RunAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"), [directory, "/grant", $"*{sid}:(OI)(CI)M"]);
        if (result != 0) throw new IOException("Could not grant the installing user access to the configured per-user data root.");
        Console.WriteLine(JsonSerializer.Serialize(new { prepared = true, directory }, CerebralJson.Options));
        return 0;
    }

    private static HostSettings? ReadInstallerSettings(string data)
    {
        var database = Path.Combine(data, "cerebral.db");
        if (!File.Exists(database)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind='settings' AND id='host'";
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<HostSettings>(json, CerebralJson.Options) ?? throw new InvalidDataException("The saved host settings are invalid.")
            : null;
    }

    // Public for focused path fixtures; validation performs no filesystem or access-control mutations.
    public static string ValidateInstallerProjectDirectory(string projects, string data, string install, InstalledCortexLocation? cortex = null)
    {
        var path = InstalledCortexDiscovery.ValidateLocalDirectory(projects);
        var dataPath = InstalledCortexDiscovery.ValidateLocalDirectory(data);
        var installPath = InstalledCortexDiscovery.ValidateLocalDirectory(install);
        if (DirectoryInside(path, installPath) || DirectoryInside(installPath, path))
            throw new ArgumentException("Project files must remain outside the complete replaceable Cerebral installation.");
        if (DirectoryInside(dataPath, path))
            throw new ArgumentException("Choose a project folder below or separate from service data, not the data root or an ancestor.");
        if (cortex is not null) InstalledCortexDiscovery.ValidateSeparateDirectory(cortex, path);
        // Never grant LocalService project access over OS/program trees or broad personal/shared roots.
        foreach (var protectedTree in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            InstalledCortexDiscovery.ValidateSeparateDirectory(new(protectedTree, protectedTree), path);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profilesRoot = Path.GetDirectoryName(profile);
        var broadRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), profile, profilesRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        foreach (var root in broadRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (path.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!)), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a dedicated project folder rather than a shared, profile or system data root.");
        if (profilesRoot is not null && Path.GetDirectoryName(path)?.Equals(
            InstalledCortexDiscovery.ValidateLocalDirectory(profilesRoot), StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Choose a dedicated project subfolder, not a Windows user profile.");
        return path;
    }

    private static bool DirectoryInside(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task<int> ConnectorAsync(string action, string? dataDirectory, string? settingsPath, string? keyPath, string? workerPath)
    {
        var manager = new UserConnectorManager(dataDirectory);
        ConnectorStatus status;
        switch (action)
        {
            case "status": status = await manager.StatusAsync(); break;
            case "start": status = await manager.StartAsync(workerPath ?? Path.Combine(AppContext.BaseDirectory, "Cerebral.Worker.exe")); break;
            case "stop": status = await manager.StopAsync(); break;
            case "provision":
                if (settingsPath is null || keyPath is null || !Path.IsPathFullyQualified(settingsPath) || !Path.IsPathFullyQualified(keyPath))
                    throw new ArgumentException("Connector provisioning requires absolute --settings and --key-file paths.");
                if (new FileInfo(settingsPath).Length > 1024 * 1024 || new FileInfo(keyPath).Length > 4096)
                    throw new ArgumentException("Connector provisioning input exceeds its limit.");
                var config = JsonSerializer.Deserialize<ConnectorConfiguration>(await File.ReadAllTextAsync(settingsPath), CerebralJson.Options)
                    ?? throw new ArgumentException("Invalid connector configuration.");
                status = await manager.ProvisionAsync(config, (await File.ReadAllTextAsync(keyPath)).Trim());
                break;
            default: throw new ArgumentException("Connector action must be status, provision, start or stop.");
        }
        Console.WriteLine(JsonSerializer.Serialize(status, CerebralJson.Options)); return 0;
    }

    private static string QuoteWindows(string value)
    {
        if (value.Contains('"') || value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Invalid path.");
        var trailingSlashes = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailingSlashes) + "\"";
    }

    private static async Task VerifyHealthAsync(string data, string? explicitUrl, string? requiredVersion = null)
    {
        var directory = Path.GetFullPath(data);
        var installedBinding = OperatingSystem.IsWindows() ? InstalledWorkerBindingStore.ReadRegistered() : null;
        if (installedBinding is not null && explicitUrl is null &&
            InstalledWorkerBinding.PathEquals(directory, installedBinding.DataDirectory, true))
        {
            var pipeDeadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (true)
            {
                try
                {
                    using var client = CerebralClient.ForInstalledWorker();
                    var info = await client.GetAsync<HostInfo>("health");
                    VerifyHealthIdentity(info, directory, requiredVersion);
                    Console.WriteLine(JsonSerializer.Serialize(new { verified = true, transport = "installed-pipe", info.Product, info.Version, info.DataDirectory }, CerebralJson.Options));
                    return;
                }
                catch (Exception e) when (DateTimeOffset.UtcNow < pipeDeadline && e is IOException or System.TimeoutException or TaskCanceledException)
                {
                    await Task.Delay(1000);
                }
            }
        }

        HostSettings? settings = null;
        var database = Path.Combine(directory, "cerebral.db");
        if (File.Exists(database))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM records WHERE kind='settings' AND id='host'";
            if (await command.ExecuteScalarAsync() is string json) settings = JsonSerializer.Deserialize<HostSettings>(json, CerebralJson.Options);
        }
        var url = new UriBuilder(explicitUrl ?? settings?.ListenUrl ?? CerebralDefaults.WorkerUrl);
        if (url.Host is "0.0.0.0" or "::" or "[::]") url.Host = "127.0.0.1";
        using var http = new HttpClient { BaseAddress = url.Uri, Timeout = TimeSpan.FromSeconds(2) };
        if (!string.IsNullOrEmpty(settings?.AccessKey)) http.DefaultRequestHeaders.Add("X-Cerebral-Key", settings.AccessKey);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (true)
        {
            try
            {
                var info = await http.GetFromJsonAsync<HostInfo>("health", CerebralJson.Options) ?? throw new IOException("Empty health response.");
                VerifyHealthIdentity(info, directory, requiredVersion);
                Console.WriteLine(JsonSerializer.Serialize(new { verified = true, transport = "http", info.Product, info.Version, info.DataDirectory }, CerebralJson.Options));
                return;
            }
            catch (Exception e) when (DateTimeOffset.UtcNow < deadline && e is HttpRequestException or TaskCanceledException) { await Task.Delay(1000); }
        }
    }

    private static void VerifyHealthIdentity(HostInfo info, string directory, string? requiredVersion)
    {
        var expectedVersion = typeof(ManagementCommands).Assembly.GetName().Version!.ToString(3);
        if (requiredVersion is not null && expectedVersion != requiredVersion)
            throw new InvalidDataException("The installed worker payload version does not match this installer.");
        if (info.Product != "Pegasus Cerebral" || info.Protocol != 1 || info.Version.Split('+')[0] != expectedVersion ||
            !Path.GetFullPath(info.DataDirectory).Equals(directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The responding service does not match this installation's product, version, or data directory.");
    }

    private static async Task<string> DownloadInstallerAsync(string source, string? explicitDestination = null)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("An HTTP(S) installer URL without embedded credentials is required.");
        var destination = explicitDestination is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "downloads", "Cerebral-Setup.exe") : Path.GetFullPath(explicitDestination);
        if (!destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Installer destination must have an .exe extension.");
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var partial = destination + ".partial";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Never expose a partial response as an executable or replace a usable prior download.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                const long maxBytes = 2L * 1024 * 1024 * 1024;
                if (response.Content.Headers.ContentLength is > maxBytes) throw new InvalidDataException("Installer exceeds the 2 GB download limit.");
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var buffer = new byte[81_920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        if (file.Length + read > maxBytes) throw new InvalidDataException("Installer exceeds the 2 GB download limit.");
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                    if (response.Content.Headers.ContentLength is { } expected && file.Length != expected) throw new IOException("Installer download was incomplete.");
                }
                await using (var file = File.OpenRead(partial))
                {
                    if (file.Length < 128 || file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("Response is not a Windows installer executable.");
                }
                File.Move(partial, destination, true);
                return destination;
            }
            catch (Exception e) when (attempt < 2 && RetryDownload(e))
            {
                if (File.Exists(partial)) File.Delete(partial);
                Console.Error.WriteLine($"Installer download interrupted; retry {attempt + 1}/2. Installed files have not changed.");
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
            }
            catch { if (File.Exists(partial)) File.Delete(partial); throw; }
        }
    }

    private static bool RetryDownload(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        IOException => true,
        TaskCanceledException => true,
        _ => false
    };

    private static async Task<int> RemoteAsync(string? profileFile, string action, string? source, bool sudo)
    {
        if (profileFile is null) throw new ArgumentException("Provide --profile with a WorkerRegistration JSON file.");
        var worker = JsonSerializer.Deserialize<WorkerRegistration>(await File.ReadAllTextAsync(profileFile), CerebralJson.Options) ?? throw new ArgumentException("Invalid worker profile.");
        PortableRegistrationPolicy.RequireReady(worker);
        var target = worker.SshUser + "@" + worker.SshHost;
        List<string> SshArgs() { var a = RemoteTransportArguments(worker, false); a.Add(target); return a; }
        if (action is "status" or "start" or "stop")
        {
            var arguments = SshArgs();
            arguments.Add((sudo ? "sudo -n " : "") + "systemctl " + action + " pegasus-cerebral");
            return await RunAsync("ssh", arguments);
        }
        if (action != "deploy") throw new ArgumentException("Remote action must be status, start, stop, or deploy.");
        if (!sudo) throw new ArgumentException("Remote system service deployment requires explicit --sudo and an existing sudo grant.");
        if (source is null || !File.Exists(Path.Combine(source, "Cerebral.Worker"))) throw new ArgumentException("Provide --source pointing to the Linux ARM64 Worker publish folder.");
        var install = worker.InstallDirectory ?? "/opt/pegasus-cerebral";
        if (!Regex.IsMatch(install, "^/[a-zA-Z0-9_./-]+$") || install.Contains("..") || install.Count(x => x == '/') < 2) throw new ArgumentException("Use a product-specific absolute Linux installation directory without spaces or traversal.");
        var helper = Path.Combine(Directory.GetParent(Path.GetFullPath(source))!.FullName, "install-linux.sh");
        if (!File.Exists(helper)) throw new ArgumentException("install-linux.sh must accompany the Linux publish directory.");
        // One reusable staging directory. Interrupted copies cannot touch the installed worker.
        if (new DirectoryInfo(source).Name != "Worker") throw new ArgumentException("The publish source directory must be named Worker.");
        var stage = ".cerebral-deploy";
        var prepare = SshArgs(); prepare.Add("mkdir -p " + stage);
        if (await RunAsync("ssh", prepare) != 0) return 1;
        var transfer = RemoteTransportArguments(worker, true); transfer.Insert(0, "-r");
        transfer.AddRange([Path.GetFullPath(source), helper, target + ":" + stage + "/"]);
        if (await RunAsync("scp", transfer, TimeSpan.FromMinutes(15)) != 0) throw new IOException("Remote transfer failed. Installed files are unchanged; rerun deployment after restoring the connection.");
        var listener = RemoteServiceListenUrl(worker);
        var deploy = SshArgs(); deploy.Add("sudo -n sh " + stage + "/install-linux.sh deploy " + stage + "/Worker " + install + " " + worker.SshUser + " " + listener);
        var result = await RunAsync("ssh", deploy, TimeSpan.FromMinutes(3));
        if (result == 0)
        {
            var cleanup = SshArgs();
            cleanup.Add("rm -rf -- .cerebral-deploy/Worker .cerebral-deploy/install-linux.sh");
            await RunAsync("ssh", cleanup);
        }
        return result;
    }

    internal static List<string> RemoteTransportArguments(WorkerRegistration worker, bool scp)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (worker.SshHost is null || !Regex.IsMatch(worker.SshHost, "^[a-zA-Z0-9][a-zA-Z0-9.:-]*$") ||
            worker.SshUser is null || !Regex.IsMatch(worker.SshUser, "^[a-zA-Z_][a-zA-Z0-9_-]*$") ||
            worker.SshPort is < 1 or > 65535)
            throw new ArgumentException("A valid explicit SSH host, user, and port are required.");
        var arguments = new List<string>
        {
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "StrictHostKeyChecking=yes",
            scp ? "-P" : "-p", worker.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(worker.SshKnownHostsFile))
        {
            var knownHosts = Path.GetFullPath(worker.SshKnownHostsFile);
            if (!File.Exists(knownHosts)) throw new ArgumentException("The configured SSH known-hosts file does not exist.");
            arguments.AddRange(["-o", "UserKnownHostsFile=" + knownHosts]);
        }
        if (!string.IsNullOrWhiteSpace(worker.SshIdentityFile))
        {
            var identity = Path.GetFullPath(worker.SshIdentityFile);
            if (!File.Exists(identity)) throw new ArgumentException("The configured SSH identity file does not exist.");
            arguments.AddRange(["-o", "IdentitiesOnly=yes", "-i", identity]);
        }
        return arguments;
    }

    internal static string RemoteServiceListenUrl(WorkerRegistration worker)
    {
        if (!Uri.TryCreate(worker.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.Trim('/').Length != 0 || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Remote deployment requires an explicit HTTP worker origin without credentials, path, query, or fragment.");
        return $"http://{(endpoint.IsLoopback ? "127.0.0.1" : "0.0.0.0")}:{endpoint.Port}";
    }

    private static async Task<int> PairWorkerAsync(string? profileFile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Cerebral owner-pipe pairing requires Windows.");
        if (string.IsNullOrWhiteSpace(profileFile)) throw new ArgumentException("Provide --profile with a local WorkerRegistration JSON file.");
        var path = Path.GetFullPath(profileFile);
        if (!File.Exists(path)) throw new ArgumentException("Worker profile file does not exist.");
        var worker = JsonSerializer.Deserialize<WorkerRegistration>(await File.ReadAllTextAsync(path), CerebralJson.Options)
            ?? throw new ArgumentException("Invalid worker profile.");
        if (worker.RequiresConfiguration || string.IsNullOrWhiteSpace(worker.PeerToken))
            throw new ArgumentException("Pairing requires an active registration and a scoped peer token.");
        if (!string.IsNullOrEmpty(worker.AccessKey))
            throw new ArgumentException("Do not import a remote owner access key. Use the scoped peer token.");
        PortableRegistrationPolicy.Validate(new ConfigurationBackupWorker(worker.Id, worker.Name, worker.Url,
            worker.SshHost, worker.SshUser, worker.SshPort, worker.InstallDirectory));
        using var client = LocalWorkerClient.ForInstalledWorker();
        var settings = await InstalledGetAsync<HostSettings>(client, "settings.get", new { });
        settings.Workers.RemoveAll(item => item.Id == worker.Id);
        settings.Workers.Add(worker);
        await InstalledGetAsync<object>(client, "settings.save", settings);
        var probe = await InstalledGetAsync<WorkerProbeResult>(client, "workers.probe", new { id = worker.Id });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            paired = worker.Id, probe.Host, probe.Metrics.Resources, probe.Metrics.Queue,
            probe.RoundTripLatencyMilliseconds,
            availableAgents = probe.Catalog?.Agents.Count,
            availableModels = probe.Catalog?.Models.Count
        }, CerebralJson.Options));
        return 0;
    }

    private static async Task<int> PeerSmokeAsync(string? workerId, string? requestedRemoteProfile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Cerebral owner-pipe verification requires Windows.");
        if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Provide --id with the paired Worker ID.");
        Configuration.ValidateId(workerId);
        using var owner = LocalWorkerClient.ForInstalledWorker();
        var settings = await InstalledGetAsync<HostSettings>(owner, "settings.get", new { });
        var worker = settings.Workers.SingleOrDefault(item => item.Id == workerId)
            ?? throw new ArgumentException("Paired Worker is not registered in the installed Cerebral service.");
        if (string.IsNullOrWhiteSpace(worker.PeerToken)) throw new InvalidOperationException("The Worker has no scoped peer token.");
        using var remote = new CerebralClient(worker);
        var catalog = await remote.GetAsync<PeerCatalog>("peer.catalog");
        var contribution = requestedRemoteProfile is null ? catalog.Agents.FirstOrDefault() :
            catalog.Agents.SingleOrDefault(item => item.Id == requestedRemoteProfile);
        if (contribution is null) throw new InvalidOperationException("The peer grant exposes no matching remote agent profile.");
        var remoteModel = catalog.Models.SingleOrDefault(item => item.Id == contribution.ModelId)
            ?? throw new InvalidOperationException("The shared remote agent has no visible model binding.");
        var localId = "peer-" + worker.Id;
        if (localId.Length > 100) localId = localId[..100];
        var agents = await InstalledGetAsync<List<AgentProfile>>(owner, "agents.list", new { });
        var existing = agents.SingleOrDefault(item => item.Id == localId);
        var context = Math.Clamp(remoteModel.ContextTokens, 4096, 1_048_576);
        var response = Math.Clamp(Math.Min(512, context / 4), AgentTokenLimits.MinimumResponseTokens, AgentTokenLimits.MaximumResponseTokens);
        var profile = new AgentProfile
        {
            Id = localId, Revision = existing?.Revision ?? 0, Name = worker.Name + " remote helper",
            Purpose = "Bounded remote execution through the scoped " + worker.Name + " peer grant.",
            Instructions = "Follow the requested bounded task and return a direct result.", Role = "helper",
            ModelId = contribution.ModelId, WorkerId = worker.Id, ModelWorkerId = worker.Id,
            RemoteProfileId = contribution.Id, ContextTokens = context, ResponseTokens = response,
            FollowsModelBudgets = false, ReasoningMode = "off", ReasoningTokens = AgentTokenLimits.MinimumReasoningTokens,
            Temperature = 0, MaxSteps = 1, MaxSeconds = 180, Capabilities = [], AllowedTools = [], RequiredModelCapabilities = []
        };
        await InstalledGetAsync<AgentProfile>(owner, "agents.save", profile);
        var requestId = "peer-smoke-" + Guid.NewGuid().ToString("N");
        var job = await InstalledGetAsync<AgentJob>(owner, "jobs.submit", new SubmitJobRequest
        {
            RequestId = requestId, ProfileId = localId,
            Prompt = "Return one short sentence confirming that this assignment executed on the GB10 ARM64 peer.",
            Title = "GB10 ARM64 peer smoke test", Kind = "agent"
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            while (job.FinishedAt is null && job.Status is not ("completed" or "failed" or "cancelled"))
            {
                await Task.Delay(1000, timeout.Token);
                job = await InstalledGetAsync<AgentJob>(owner, "jobs.get", new { id = job.Id }, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await InstalledGetAsync<AgentJob>(owner, "jobs.cancel", new { id = job.Id });
            throw new System.TimeoutException("The bounded peer smoke assignment did not finish within five minutes and was cancelled.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            worker = worker.Id, remoteProfile = contribution.Id, model = contribution.ModelId,
            job = job.Id, job.Status, job.Result, job.Error, job.ExecutionStartedAt, job.FinishedAt
        }, CerebralJson.Options));
        return job.Status == "completed" ? 0 : 1;
    }

    private static async Task<T> InstalledGetAsync<T>(LocalWorkerClient client, string operation, object arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await client.CommandAsync(new(operation, CerebralJson.Element(arguments)), cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? operation + " failed.");
        return CerebralJson.Read<T>(result.Data);
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Process could not start.");
        var output = ReadTailAsync(process.StandardOutput);
        var error = ReadTailAsync(process.StandardError);
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new System.TimeoutException($"{Path.GetFileName(executable)} timed out. Inspect current state before retrying."); }
        var stdout = await output; var stderr = await error;
        Console.Write(stdout.Length > 16_384 ? stdout[^16_384..] : stdout);
        Console.Error.Write(stderr.Length > 16_384 ? stderr[^16_384..] : stderr);
        return process.ExitCode;
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[2048]; int read; var omitted = false;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > 16_384) { text.Remove(0, text.Length - 16_384); omitted = true; }
        }
        return (omitted ? "[earlier output omitted]\n" : "") + text;
    }
}
