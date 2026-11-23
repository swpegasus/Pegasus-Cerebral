using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class ConnectorPolicyTests
{
    public static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        void Denied(Action action, string name)
        {
            var denied = false; try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { denied = true; }
            Check(denied, name);
        }
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-connector-policy-" + Guid.NewGuid().ToString("N"));
        var provider = new McpRegistration { Id = "cortex", Enabled = true, ViaUserConnector = true,
            Url = ConnectorProtocol.DefaultUrl, Executable = Path.Combine(directory, "provider.exe"), Arguments = [ConnectorProtocol.StableCortexMcpArgument] };
        var disabled = new McpRegistration { Id = "disabled", Enabled = false, ViaUserConnector = true, Url = ConnectorProtocol.DefaultUrl };
        var direct = new McpRegistration { Id = "direct", Enabled = true, ViaUserConnector = false };
        var other = new McpRegistration { Id = "other", Enabled = true, ViaUserConnector = true, Executable = Environment.ProcessPath!, Arguments = [], Url = "http://127.0.0.1:6125/" };
        var unrouted = new McpRegistration { Id = "unrouted", Enabled = true, ViaUserConnector = true };
        var configuration = UserConnectorManager.CreateConfiguration([provider, disabled, direct, other, unrouted]);
        Check(configuration.McpServers.Count == 1 && configuration.McpServers[0].Id == "cortex", "connector copies only enabled providers explicitly routed to its endpoint");
        Check(configuration.McpServers[0].Enabled && !configuration.McpServers[0].ViaUserConnector && configuration.McpServers[0].Url is null, "connector provider definition executes direct stdio without recursive forwarding");
        Check(configuration.McpServers[0].Arguments.SequenceEqual([ConnectorProtocol.StableCortexMcpArgument]), "Cortex connector registration retains the explicit stdio selector");
        configuration.McpServers[0].Arguments.Add("copy-only");
        Check(provider.Arguments.Count == 1 && provider.ViaUserConnector, "connector provisioning preserves service provider definitions");
        var json = JsonSerializer.Serialize(configuration, CerebralJson.Options);
        Check(!json.Contains("accessKey", StringComparison.OrdinalIgnoreCase) && !json.Contains("allowedRoots", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("cortexNamespaces", StringComparison.OrdinalIgnoreCase), "connector configuration has no service key, agent grants or Cortex store settings");
        Denied(() => ConnectorProtocol.ValidateUrl("http://192.0.2.1:6124/"), "connector rejects non-loopback listeners");
        Denied(() => ConnectorProtocol.ValidateUrl("http://localhost:6124/"), "connector requires an explicit loopback IP address");
        Denied(() => ConnectorProtocol.ValidateUrl("http://127.0.0.1:6124/?secret=x"), "connector rejects endpoint query strings");
        Denied(() => ConnectorProtocol.ValidateUrl("http://user:password@127.0.0.1:6124/"), "connector rejects credentials in its endpoint");
        Denied(() => ConnectorProtocol.ValidateKey("short"), "connector rejects short access keys");
        Denied(() => ConnectorProtocol.ValidateKey(new string('\u00e9', 2049)), "connector key bounds apply to UTF-8 bytes");
        Denied(() => ConnectorProtocol.ValidateKey(" " + new string('x', 16)), "connector rejects key whitespace that would alter file authentication");
        var unknown = UserConnectorManager.CreateConfiguration([provider]); unknown.SchemaVersion = 2;
        Denied(() => ConnectorProtocol.ValidateConfiguration(unknown), "connector rejects unknown configuration versions");
        var recursion = UserConnectorManager.CreateConfiguration([provider]); recursion.McpServers[0].ViaUserConnector = true;
        Denied(() => ConnectorProtocol.ValidateConfiguration(recursion), "managed connector configuration rejects forwarding recursion");
        var duplicate = UserConnectorManager.CreateConfiguration([provider]); duplicate.McpServers.Add(duplicate.McpServers[0]);
        Denied(() => ConnectorProtocol.ValidateConfiguration(duplicate), "connector rejects duplicate provider IDs");
        var bytes = Encoding.UTF8.GetBytes(json); var hash = ConnectorProtocol.ConfigurationHash(bytes);
        var instance = new ConnectorInstance(Guid.NewGuid().ToString("N"), hash, DateTimeOffset.UtcNow);
        var health = new ConnectorHealth(ConnectorProtocol.Product, ConnectorProtocol.Version, instance.InstanceId, 1234,
            DateTimeOffset.UtcNow, hash, ["cortex"], false);
        ConnectorProtocol.ValidateHealth(health, instance);
        Check(true, "connector accepts its exact authenticated health identity");
        Denied(() => ConnectorProtocol.ValidateHealth(health with { Product = "Other product" }, instance), "connector rejects a different product");
        Denied(() => ConnectorProtocol.ValidateHealth(health with { Protocol = 2 }, instance), "connector rejects an incompatible protocol");
        Denied(() => ConnectorProtocol.ValidateHealth(health with { InstanceId = Guid.NewGuid().ToString("N") }, instance), "connector rejects a replacement process instance");
        Denied(() => ConnectorProtocol.ValidateHealth(health with { ConfigurationSha256 = new string('0', 64) }, instance), "connector rejects changed runtime configuration");
        Denied(() => new UserConnectorManager("relative-directory"), "connector test/custom state requires an absolute directory");
        var manager = new UserConnectorManager(directory);
        var status = await manager.StatusAsync();
        Check(status.State == "not-configured" && !Directory.Exists(directory), "unconfigured connector status does not create state or contact a provider");
        Check(!JsonSerializer.Serialize(status, CerebralJson.Options).Contains("accessKey", StringComparison.OrdinalIgnoreCase), "connector status never contains the access key");
        var blockedMove = false;
        try { UserConnectorManager.CreateConfiguration([provider, other], other.Url!, ConnectorProtocol.DefaultUrl); }
        catch (InvalidOperationException ex) { blockedMove = ex.Message.Contains("cortex", StringComparison.Ordinal); }
        Check(blockedMove, "changing connector endpoint reports enabled providers that would be left behind");
        Check(UserConnectorManager.CreateConfiguration([provider, other], ConnectorProtocol.DefaultUrl, ConnectorProtocol.DefaultUrl).McpServers.Single().Id == "cortex",
            "same connector endpoint preserves its enabled provider definitions");
        Check(UserConnectorManager.CreateConfiguration([disabled, other], other.Url!, ConnectorProtocol.DefaultUrl).McpServers.Single().Id == "other",
            "disabled old routes do not prevent an explicit connector endpoint change");
        var sourceBefore = JsonSerializer.Serialize(provider, CerebralJson.Options);
        var preflight = UserConnectorManager.CreateConfiguration([provider]);
        var missing = false;
        try { UserConnectorManager.ValidateProvisioning(preflight, new string('x', 32)); } catch (FileNotFoundException) { missing = true; }
        Check(missing && !Directory.Exists(directory), "provider preflight rejects missing executable before connector state writes");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(provider.Executable, "path-only fixture; never executed");
            UserConnectorManager.ValidateProvisioning(preflight, new string('x', 32));
            Check(JsonSerializer.Serialize(provider, CerebralJson.Options) == sourceBefore && !File.Exists(manager.ConfigurationPath),
                "shared provider preflight validates existing paths without modifying configuration or source registrations");
            var invalidKey = false;
            try { UserConnectorManager.ValidateProvisioning(preflight, "short"); } catch (ArgumentException) { invalidKey = true; }
            Check(invalidKey && !File.Exists(manager.KeyPath), "shared provider preflight rejects invalid key without writing it");
            var link = Path.Combine(directory, "linked-provider.exe");
            try
            {
                File.CreateSymbolicLink(link, provider.Executable);
                var linked = UserConnectorManager.CreateConfiguration([new McpRegistration {
                    Id = "linked", Executable = link, Arguments = [], Enabled = true, ViaUserConnector = true, Url = ConnectorProtocol.DefaultUrl }]);
                var denied = false;
                try { UserConnectorManager.ValidateProvisioning(linked, new string('x', 32)); } catch (UnauthorizedAccessException) { denied = true; }
                Check(denied && !File.Exists(manager.ConfigurationPath), "shared preflight rejects linked provider before any setup or stop operation");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.WriteLine("SKIP: connector linked-provider fixture cannot create a symbolic link."); }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"RESULT: {passed} connector policy checks passed (no network, provider or process launch).");
    }
}
