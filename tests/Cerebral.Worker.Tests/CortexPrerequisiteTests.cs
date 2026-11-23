using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Installed-path/catalog fixtures only. Does not register Cortex, execute providers or install programs.</summary>
public static class CortexPrerequisiteTests
{
    public static async Task RunAsync()
    {
        CortexPathAliasTests.Run();
        var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); passed++; Console.WriteLine("PASS: " + message); }
        void Reject(Action action, string message)
        {
            var rejected = false;
            try { action(); } catch (Exception error) when (error is InvalidDataException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, message);
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-prerequisite-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Custom Cortex"); var data = Path.Combine(root, "Cortex Data");
        Directory.CreateDirectory(install); Directory.CreateDirectory(data);
        try
        {
            var location = InstalledCortexDiscovery.FromRegistration(install, data)!;
            var roles = new JsonArray();
            foreach (var (role, component) in new[]
            {
                ("mcp", "semantics"), ("host", "execution"), ("service", "execution"), ("pao", "execution"),
                ("process-host", "execution"), ("ui-host", "execution"), ("launcher", "launcher"), ("setup", "installer")
            })
            {
                var relative = $"versions/{component}/{CortexPrerequisite.MinimumRelease}/{role}.exe";
                var file = Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "path-only fixture; not an executable");
                roles.Add(new JsonObject { ["role"] = role, ["component"] = component, ["componentVersion"] = CortexPrerequisite.MinimumRelease, ["relativePath"] = relative });
            }
            File.WriteAllText(location.ExecutablePath, "path-only fixture; not an executable");
            Directory.CreateDirectory(Path.GetDirectoryName(location.PaoPath)!); File.WriteAllText(location.PaoPath, "path-only fixture; not an executable");
            var catalogPath = Path.Combine(data, "install", "active.v1.json"); Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            var staleLegacyPath = Path.Combine(install, "state", "install", "active.v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(staleLegacyPath)!);
            File.WriteAllText(staleLegacyPath, "{\"contract\":\"stale-legacy-catalog\"}");
            var baseline = new JsonObject
            {
                ["contract"] = "pegasus-cortex-product-catalog", ["version"] = 1, ["product"] = "Pegasus Cortex",
                ["channel"] = "Prod Alpha", ["executionProtocol"] = "1.0", ["cortexMcpSchema"] = 2, ["paoSchema"] = 3,
                ["releaseVersion"] = CortexPrerequisite.MinimumRelease,
                ["stateSchemas"] = new JsonObject { ["install"] = 1, ["semantic"] = 2, ["execution"] = 1, ["userExecution"] = 1, ["tray"] = 1 },
                ["roles"] = roles
            };
            void Catalog(Action<JsonObject>? edit = null)
            {
                var value = baseline.DeepClone().AsObject(); edit?.Invoke(value); File.WriteAllText(catalogPath, value.ToJsonString());
            }
            void RejectCatalog(Action<JsonObject> edit, string message) { Catalog(edit); Reject(() => CortexPrerequisite.Inspect(location), message); }
            Check(InstalledCortexDiscovery.FindInstalledTray(null) is null, "missing Cortex registration offers no guessed tray");
            Reject(() => InstalledCortexDiscovery.FindInstalledTray(location), "a CLI-only installation does not fall back when the registered tray is missing");
            Directory.CreateDirectory(Path.GetDirectoryName(location.NativeHostPath)!);
            File.WriteAllText(location.NativeHostPath, "path-only fixture; not an executable");
            Check(InstalledCortexDiscovery.FindInstalledTray(location) == location.NativeHostPath,
                "the tray resolver honors a custom registered installation root");
            Check(InstalledCortexDiscovery.ValidateExplicitPath(location.ExecutablePath) == location.ExecutablePath,
                "tray discovery preserves the separate CLI/MCP executable contract");
            Reject(() => InstalledCortexDiscovery.FindInstalledTray(new("relative", data)),
                "tray discovery rejects a relative installation binding");
            Check(InstalledCortexDiscovery.FromRegistration(null, null) is null, "missing Cortex registration remains absent");
            Reject(() => InstalledCortexDiscovery.FromRegistration(null, data), "orphaned data registration is rejected");
            Reject(() => InstalledCortexDiscovery.FromRegistration("relative", data), "relative registered install location is rejected");
            Reject(() => InstalledCortexDiscovery.FromRegistration(install, ""), "empty registered data location never falls back");
            Reject(() => InstalledCortexDiscovery.FromRegistration(Path.Combine(install, "..", "other"), data), "relative segments in registered paths are rejected");
            Reject(() => InstalledCortexDiscovery.FromRegistration(@"\\server\share\Cortex", data), "network registration is not a local prerequisite");
            Check(InstalledCortexDiscovery.FromRegistration(install, null)!.DataDirectory == Path.Combine(install, "state"), "absent legacy data value retains Cortex's declared install-root state");
            Reject(() => CortexPrerequisite.Inspect(null), "missing prerequisite cannot report ready");
            Catalog();
            var firstRun = CortexFirstRunDiscovery.Inspect(location);
            Check(firstRun.State == "compatible" && firstRun.ExecutablePath == location.ExecutablePath,
                "compatible installed metadata yields an informational first-run discovery");
            var overflowRelease = "2147483648.2026.10.27.04.21.A";
            Check(CortexFirstRunDiscovery.Inspect(location with { ReleaseVersion = overflowRelease }).State == "incompatible",
                "overflowed optional Cortex release registration is an incompatible observation");
            Catalog(c => c["releaseVersion"] = overflowRelease);
            Check(CortexFirstRunDiscovery.Inspect(location).State == "incompatible",
                "overflowed active Cortex release does not abort first-run discovery");
            Catalog();
            var destination = Path.Combine(root, "Cerebral"); var cerebralData = Path.Combine(root, "Cerebral Data");
            var report = CortexPrerequisite.Inspect(location, destination, cerebralData);
            Check(report.Ready && report.InstallDirectory == install && report.ExecutablePath == location.ExecutablePath && report.PaoPath == location.PaoPath,
                "valid custom installation resolves registered stable paths");
            Check(CortexPrerequisite.Inspect(location with { ReleaseVersion = CortexPrerequisite.MinimumRelease }).Ready,
                "matching ReleaseVersion registration agrees with the active product catalog");
            Reject(() => CortexPrerequisite.Inspect(location with { ReleaseVersion = "1.2026.09.27.09.31.A" }),
                "mismatched ReleaseVersion registration requires repair");
            if (OperatingSystem.IsWindows())
            {
                var shortInstall = CortexPathAliasTests.ShortPath(install);
                var shortData = CortexPathAliasTests.ShortPath(data);
                var aliasReport = CortexPrerequisite.Inspect(new(shortInstall, shortData), destination, cerebralData);
                Check(aliasReport.Ready && aliasReport.InstallDirectory == shortInstall && aliasReport.DataDirectory == shortData,
                    "catalog validation preserves registered path aliases through component checks");
            }
            Check(!Directory.Exists(destination) && !Directory.Exists(cerebralData) &&
                !Directory.Exists(Path.Combine(data, "users")),
                "prerequisite inspection writes no destination or Cortex semantic data");
            foreach (var conflict in new[] { install, Path.Combine(install, "Cerebral"), root, data, Path.Combine(data, "Cerebral") })
                Reject(() => CortexPrerequisite.Inspect(location, conflict), "overlapping product destination is rejected: " + Path.GetFileName(conflict));
            Reject(() => CortexPrerequisite.Inspect(location, destination, data), "service data cannot overlap Cortex data");
            Check(CortexPrerequisite.Inspect(location, install + "-Cerebral").Ready, "sibling name prefix does not count as path containment");
            RejectCatalog(c => c["releaseVersion"] = "1.2026.09.10.19.02.A", "old overall Cortex release is rejected");
            foreach (var roleName in new[] { "host", "service", "pao", "process-host", "ui-host" })
                RejectCatalog(c => c["roles"]!.AsArray().Single(x => x!["role"]!.GetValue<string>() == roleName)!["componentVersion"] = "1.2026.08.28.18.10.A",
                    "new catalog cannot hide an old execution component: " + roleName);
            Catalog(c => c["roles"]![0]!["componentVersion"] = "1.2026.09.10.19.02.A");
            Check(CortexPrerequisite.Inspect(location).Ready, "compatible semantic component can keep its independent version");
            RejectCatalog(c => c["contract"] = "other-product-catalog", "wrong catalog identity is rejected");
            RejectCatalog(c => c["version"] = 2, "unsupported catalog contract version is rejected");
            RejectCatalog(c => c["product"] = "Other product", "wrong registered product is rejected");
            RejectCatalog(c => c["paoSchema"] = 2, "unsupported PAO schema is rejected");
            RejectCatalog(c => c["cortexMcpSchema"] = 3, "unsupported semantic MCP schema is rejected");
            RejectCatalog(c => c["executionProtocol"] = "2.0", "unsupported execution protocol is rejected");
            RejectCatalog(c => c["stateSchemas"]!["install"] = 0, "invalid state schema inventory is rejected");
            RejectCatalog(c => c["roles"]!.AsArray().RemoveAt(0), "missing component role is rejected");
            RejectCatalog(c => c["roles"]![0] = c["roles"]![1]!.DeepClone(), "duplicate role cannot replace a required component");
            RejectCatalog(c => c["roles"]![0]!["component"] = "execution", "role/component mismatch is rejected");
            RejectCatalog(c => c["roles"]![0]!["relativePath"] = "../outside.exe", "component path traversal is rejected");
            RejectCatalog(c => c["roles"]![0]!["relativePath"] = "state/forged.exe", "mutable state cannot supply a product executable");
            RejectCatalog(c => c["roles"]![0]!["relativePath"] = location.ExecutablePath, "absolute component role path is rejected");
            Catalog();
            var componentFile = Path.Combine(install, roles[1]!["relativePath"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            File.Delete(componentFile); Reject(() => CortexPrerequisite.Inspect(location), "missing execution component is rejected");
            File.WriteAllText(componentFile, "path-only fixture; not an executable");
            File.Delete(location.PaoPath); Reject(() => CortexPrerequisite.Inspect(location), "missing stable PAO entry point is rejected");
            File.WriteAllText(location.PaoPath, "path-only fixture; not an executable");
            File.WriteAllText(catalogPath, "{"); Reject(() => CortexPrerequisite.Inspect(location), "malformed catalog JSON is rejected");
            Check(CortexFirstRunDiscovery.Inspect(location).State == "incompatible",
                "malformed installed catalog is not treated as a first-run provider");
            File.WriteAllText(catalogPath, new string('x', 65537)); Reject(() => CortexPrerequisite.Inspect(location), "oversized catalog is bounded");
            File.WriteAllText(catalogPath, baseline.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal));
            Reject(() => CortexPrerequisite.Inspect(location), "duplicate catalog properties are rejected");
            Check(await ManagementCommands.TryRunAsync(["manage", "cortex-prerequisite", "--skip-cortex"]) != 0, "management has no prerequisite bypass option");
            Check(await ManagementCommands.TryRunAsync(["manage", "cortex-locate", "--directory", destination]) != 0, "location discovery does not accept a registration override");
        }
        finally
        {
            var exact = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(exact), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(exact).StartsWith("cerebral-prerequisite-", StringComparison.Ordinal))
                throw new InvalidOperationException("Prerequisite fixture cleanup escaped its temporary root.");
            Directory.Delete(exact, true);
        }
        Console.WriteLine($"RESULT: {passed} Cortex prerequisite checks passed (path/catalog fixtures; no installation, registry changes or provider execution).");
    }
}
