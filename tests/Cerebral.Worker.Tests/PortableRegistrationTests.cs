using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static class PortableRegistrationTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
        bool Rejects(Action action) { try { action(); return false; } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; } }
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-registration-fixture.exe"));
        var worker = new WorkerRegistration
        {
            Id = "worker.gb10", Name = "GB10", Url = "http://127.0.0.1:6123", AccessKey = "PRIVATE-WORKER-KEY",
            SshHost = "gb10.local", SshUser = "developer", SshPort = 2222, SshIdentityFile = "PRIVATE-SSH-KEY-PATH",
            SshKnownHostsFile = "PRIVATE-KNOWN-HOSTS-PATH", InstallDirectory = "/opt/pegasus"
        };
        var executable = new ExecutableRegistration { Id = "tool.fixture", Path = path, PrefixArguments = ["PRIVATE-PREFIX-TOKEN"] };
        var mcp = new McpRegistration
        {
            Id = "fixture", Executable = path, Arguments = ["PRIVATE-MCP-ARGUMENT"],
            ViaUserConnector = true, Url = "http://127.0.0.1:6124/", Enabled = true
        };
        var workerExport = PortableRegistrationPolicy.ExportWorker(worker);
        var executableExport = PortableRegistrationPolicy.ExportExecutable(executable);
        var mcpExport = PortableRegistrationPolicy.ExportMcp(mcp);
        var exported = JsonSerializer.Serialize(new { workerExport, executableExport, mcpExport }, CerebralJson.Options);
        Check(!exported.Contains("PRIVATE-", StringComparison.Ordinal) && !exported.Contains("accessKey", StringComparison.Ordinal) &&
            !exported.Contains("sshIdentityFile", StringComparison.Ordinal) && !exported.Contains("sshKnownHostsFile", StringComparison.Ordinal) &&
            executableExport.ArgumentsOmitted && mcpExport.ArgumentsOmitted,
            "portable registrations omit credential values, SSH trust paths and opaque argument values");
        var unsafeWorker = PortableRegistrationPolicy.ExportWorker(new() { Id = "bad", Name = "Bad", Url = "https://user:PRIVATE@example.test/?token=PRIVATE" });
        var unsafeMcp = PortableRegistrationPolicy.ExportMcp(new() { Id = "bad", Executable = path, Url = "http://127.0.0.1:6124/?token=PRIVATE" });
        Check(unsafeWorker.Url is null && unsafeMcp.Url is null &&
            !JsonSerializer.Serialize(new { unsafeWorker, unsafeMcp }).Contains("PRIVATE", StringComparison.Ordinal),
            "unsafe endpoint values are omitted entirely without leaking credentials or changing their meaning");
        Check(Rejects(() => PortableRegistrationPolicy.RestoreWorker(workerExport with { Url = "https://user:PRIVATE@example.test/" }, worker)) &&
            Rejects(() => PortableRegistrationPolicy.RestoreWorker(workerExport with { Url = "https://example.test/?token=PRIVATE" }, worker)) &&
            Rejects(() => PortableRegistrationPolicy.RestoreMcp(mcpExport with { Url = "http://127.0.0.1:6124/#PRIVATE" }, mcp)),
            "supplied unsafe portable endpoints fail validation");
        var unsafeDirect = PortableRegistrationPolicy.ExportMcp(new() { Id = "direct", Executable = path, Url = "http://127.0.0.1:6124/?PRIVATE", ViaUserConnector = false, Enabled = true, Arguments = ["PRIVATE"] });
        var localDirect = new McpRegistration { Id = "direct", Executable = path, ViaUserConnector = false, Enabled = true, Arguments = ["LOCAL-PRIVATE"] };
        Check(unsafeDirect.EndpointOmitted && PortableRegistrationPolicy.RestoreMcp(unsafeDirect, localDirect) is { RequiresConfiguration: true, Enabled: false, Arguments.Count: 0 },
            "an omitted unsafe URL cannot masquerade as an intentionally absent stdio endpoint");
        Check(Rejects(() => PortableRegistrationPolicy.Validate(mcpExport with { EndpointOmitted = true })),
            "an omitted endpoint marker cannot coexist with a supplied URL");
        var sameWorker = PortableRegistrationPolicy.RestoreWorker(workerExport, worker);
        Check(!sameWorker.RequiresConfiguration && sameWorker.AccessKey == worker.AccessKey && sameWorker.SshIdentityFile == worker.SshIdentityFile &&
            sameWorker.SshKnownHostsFile == worker.SshKnownHostsFile &&
            !ReferenceEquals(sameWorker, worker), "matching local worker binding retains local credentials without mutating the original");
        var renamed = PortableRegistrationPolicy.RestoreWorker(workerExport with { Name = "Renamed GB10" }, worker);
        Check(!renamed.RequiresConfiguration && renamed.AccessKey == worker.AccessKey && renamed.Name == "Renamed GB10",
            "display-name changes do not deactivate an unchanged worker endpoint");
        var moved = PortableRegistrationPolicy.RestoreWorker(workerExport with { Url = "http://127.0.0.1:6125/" }, worker);
        Check(moved.RequiresConfiguration && moved.AccessKey == "" && moved.SshIdentityFile is null && worker.AccessKey == "PRIVATE-WORKER-KEY",
            "same-ID endpoint changes never forward the old worker or SSH credential");
        var changedSsh = PortableRegistrationPolicy.RestoreWorker(workerExport with { SshHost = "another.local" }, worker);
        Check(changedSsh.RequiresConfiguration && changedSsh.AccessKey == worker.AccessKey && changedSsh.SshIdentityFile is null && changedSsh.SshKnownHostsFile is null,
            "SSH endpoint changes discard the SSH credential and pinned trust path while preserving only the unchanged API binding");
        var newWorker = PortableRegistrationPolicy.RestoreWorker(workerExport, null);
        Check(newWorker.RequiresConfiguration && newWorker.AccessKey.Length == 0 && newWorker.SshIdentityFile is null,
            "a new worker registration remains inactive until owner configuration");
        Check(PortableRegistrationPolicy.RestoreWorker(workerExport with { Url = null }, worker).RequiresConfiguration &&
            PortableRegistrationPolicy.RestoreWorker(workerExport with { Url = null }, worker).AccessKey == "",
            "an omitted endpoint cannot prove local credential identity");
        var sameExecutable = PortableRegistrationPolicy.RestoreExecutable(executableExport, executable);
        Check(!sameExecutable.RequiresConfiguration && sameExecutable.PrefixArguments.SequenceEqual(executable.PrefixArguments) &&
            !ReferenceEquals(sameExecutable.PrefixArguments, executable.PrefixArguments),
            "matching executable identity retains a detached local argument list");
        var changedExecutable = PortableRegistrationPolicy.RestoreExecutable(executableExport with { Path = path + ".changed" }, executable);
        Check(changedExecutable.RequiresConfiguration && changedExecutable.PrefixArguments.Count == 0,
            "changed executable path never inherits opaque local arguments");
        Check(PortableRegistrationPolicy.RestoreExecutable(executableExport with { ArgumentsOmitted = false }, executable) is { RequiresConfiguration: true, PrefixArguments.Count: 0 },
            "an explicit empty argument claim does not silently retain local arguments");
        var relative = new ExecutableRegistration { Id = "relative", Path = "tool.exe", PrefixArguments = ["PRIVATE"] };
        Check(PortableRegistrationPolicy.RestoreExecutable(PortableRegistrationPolicy.ExportExecutable(relative), relative) is { RequiresConfiguration: true, PrefixArguments.Count: 0 },
            "relative executable paths cannot prove a portable local identity");
        var sameMcp = PortableRegistrationPolicy.RestoreMcp(mcpExport, mcp);
        Check(!sameMcp.RequiresConfiguration && sameMcp.Enabled && sameMcp.Arguments.SequenceEqual(mcp.Arguments) &&
            !ReferenceEquals(sameMcp.Arguments, mcp.Arguments), "exact existing MCP binding keeps local enablement and detached arguments");
        var changedMcp = PortableRegistrationPolicy.RestoreMcp(mcpExport with { Url = "http://127.0.0.1:6126/" }, mcp);
        Check(changedMcp.RequiresConfiguration && !changedMcp.Enabled && changedMcp.Arguments.Count == 0,
            "connector endpoint changes stage MCP without copying private arguments");
        var disabledLocal = new McpRegistration { Id = mcp.Id, Executable = mcp.Executable, Arguments = [.. mcp.Arguments], ViaUserConnector = true, Url = mcp.Url };
        Check(PortableRegistrationPolicy.RestoreMcp(mcpExport, disabledLocal) is { RequiresConfiguration: true, Enabled: false },
            "backup enablement cannot silently activate a locally disabled provider");
        Check(PortableRegistrationPolicy.RestoreMcp(mcpExport with { WasEnabled = false }, mcp) is { RequiresConfiguration: true, Enabled: false },
            "a changed enablement declaration stages the provider disabled");
        var pending = new McpRegistration { Id = mcp.Id, Executable = mcp.Executable, Arguments = [.. mcp.Arguments], ViaUserConnector = true, Url = mcp.Url, Enabled = true, RequiresConfiguration = true };
        Check(PortableRegistrationPolicy.RestoreMcp(mcpExport, pending) is { RequiresConfiguration: true, Enabled: false, Arguments.Count: 0 },
            "an existing pending registration cannot regain active state by restore");
        Check(Rejects(() => PortableRegistrationPolicy.Validate(workerExport with { Id = "../escape" })) &&
            Rejects(() => PortableRegistrationPolicy.Validate(workerExport with { SshPort = 0 })) &&
            Rejects(() => PortableRegistrationPolicy.Validate(workerExport with { SshHost = "-ProxyCommand" })) &&
            Rejects(() => PortableRegistrationPolicy.Validate(workerExport with { SshUser = "user\ninjection" })) &&
            Rejects(() => PortableRegistrationPolicy.Validate(executableExport with { Path = "bad\0path" })) &&
            Rejects(() => PortableRegistrationPolicy.Validate(mcpExport with { ViaUserConnector = false })),
            "portable identity, text, SSH and transport validators reject invalid fields");
        PortableRegistrationPolicy.Validate(workerExport);
        PortableRegistrationPolicy.Validate(executableExport);
        Check(true, "existing dotted worker and executable identifiers remain supported");

        Check(Rejects(() => { using var client = new CerebralClient(newWorker); }), "pending workers reject before HTTP client construction or request dispatch");
        using (var client = new CerebralClient(new WorkerRegistration { Id = "local-ready", Url = "http://127.0.0.1:6123/", AccessKey = "" }))
            Check(!client.IsLocalPipe, "legacy ready loopback registrations do not newly require a nonempty key");
        Check(Rejects(() => UserConnectorManager.CreateConfiguration([pending])),
            "connector provisioning cannot erase a pending provider flag");
        var relay = new CortexNativeRelay("Fixture.Native", "S-1-5-21-fixture", 1, path);
        var nativeSource = new McpRegistration { Id = "native-fixture", Executable = path,
            Enabled = true, ViaUserConnector = true, Arguments = [],
            CortexNative = new(path, Path.Combine(Path.GetTempPath(), "fixture.key"), 1, relay) };
        var nativeProjection = UserConnectorManager.CreateConfiguration([nativeSource]);
        Check(nativeProjection.CortexRelay == relay && nativeProjection.McpServers.Single() is
            { CortexNativeRequired: true, ViaUserConnector: false } &&
            nativeProjection.McpServers.Single().CortexNative?.Relay is null &&
            !nativeSource.CortexNativeRequired,
            "native connector projection requires the pipe without changing its source registration");
        var nativeExport = PortableRegistrationPolicy.ExportMcp(nativeSource);
        var sameNative = PortableRegistrationPolicy.RestoreMcp(nativeExport, nativeSource);
        var unboundNative = PortableRegistrationPolicy.RestoreMcp(nativeExport, null);
        Check(sameNative is { Enabled: true, RequiresConfiguration: false, CortexNativeRequired: true } &&
            sameNative.CortexNative == nativeSource.CortexNative &&
            unboundNative is { Enabled: false, RequiresConfiguration: true, CortexNative: null, CortexNativeRequired: true },
            "same-machine restore retains native-only admission while an unbound restore stays inactive");
        unboundNative.Enabled = true; unboundNative.RequiresConfiguration = false;
        unboundNative.Url = ConnectorProtocol.DefaultUrl;
        Check(Rejects(() => UserConnectorManager.CreateConfiguration([unboundNative])),
            "an enabled native-only declaration cannot become a generic connector provider");
        unboundNative.Enabled = false;
        var genericProjection = UserConnectorManager.CreateConfiguration([unboundNative, mcp]);
        Check(genericProjection.McpServers.Single() is { Id: "fixture", CortexNativeRequired: false },
            "a disabled native-only declaration leaves an independently chosen MCP provider unchanged");
        Check(Rejects(() => ConnectorProtocol.ValidateConfiguration(new()
        {
            McpServers = [new() { Id = "pending", Executable = path, Enabled = true, ViaUserConnector = false, RequiresConfiguration = true }]
        })), "direct connector configuration refuses pending providers before launch");

        var host = new HostSettings
        {
            Executables = [
                new() { Id = "dotnet", Path = path, RequiresConfiguration = true },
                new() { Id = "git", Path = path, RequiresConfiguration = true }],
            McpServers = [pending], Workers = [newWorker], SharedFilesWorkerId = newWorker.Id
        };
        await using var tools = new ToolDispatcher(() => host);
        var profile = new AgentProfile
        {
            Id = "registration-test", AllowedTools = ["*"],
            Capabilities = ["process.execute", "git.read", "mcp.fixture", "shared.read", "filesystem.read"],
            AllowedRoots = [Path.GetTempPath()]
        };
        var context = new ToolExecutionContext(profile, "registration-session", "registration-job") { WorkingDirectory = Path.GetTempPath() };
        foreach (var (name, args) in new (string, object)[]
        {
            ("process.run", new { executableId = "dotnet", arguments = new[] { "build", "Never.csproj" }, workingDirectory = Path.GetTempPath() }),
            ("git.status", new { path = Path.GetTempPath() }),
            ("mcp.fixture.any", new { }),
            ("shared.read", new { path = "never.txt" })
        })
        {
            var result = await tools.ExecuteAsync(new(name, CerebralJson.Element(args)), context, default);
            Check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Rejected && result.Content.Contains("owner review", StringComparison.Ordinal),
                name + " rejects pending registration before native, PAO or remote dispatch");
        }
        var catalog = await tools.ListAsync(profile, default);
        Check(catalog.All(x => !x.Name.StartsWith("mcp.fixture.", StringComparison.Ordinal)),
            "tool discovery exposes no tools from a pending provider");
        var stopped = false;
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await tools.ExecuteAsync(new("process.run", CerebralJson.Element(new { executableId = "dotnet" })), context, cancel.Token); }
            catch (OperationCanceledException) { stopped = true; }
        }
        Check(stopped, "caller cancellation remains cancellation before pending-registration checks");
        Console.WriteLine($"Portable registrations: {assertions} policy and dispatcher-admission checks passed; no model, provider process, network request or installed service was used.");
    }
}
