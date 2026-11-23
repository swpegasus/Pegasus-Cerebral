using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class PluginCatalogTests
{
    public static Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-plugin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        void Denied(Action action, string name)
        {
            var rejected = false; try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        PluginManifest Manifest(string id = "fixture") => new()
        {
            Id = id, Name = "Fixture extension", Version = "1.0.0", Description = "Declarative test fixture",
            Server = new() { Id = id, Executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "fixture.exe" : "fixture"), ViaUserConnector = false }
        };
        try
        {
            var store = new StateStore(directory); var settings = new Configuration(store); var catalog = new PluginCatalog(store, settings);
            var initial = Manifest();
            initial.Server.Enabled = true;
            var preview = catalog.Validate(initial);
            Check(!preview.Manifest.Server.Enabled && initial.Server.Enabled && catalog.List().Count == 0, "plugin validation is nonmutating and previews disabled registration");
            var installed = catalog.Install(initial);
            Check(installed.ConfiguredServer is { Enabled: false } && catalog.List().Single().Id == "fixture" && settings.Get().McpServers.Single().Enabled == false, "plugin install atomically registers a disabled MCP provider");
            Check(catalog.Inspect("fixture").RequiredCapability == "mcp.fixture" && catalog.Inspect("fixture").ToolNamePrefix == "mcp.fixture.", "plugin inspection explains existing grants and tool names");
            var reopenedStore = new StateStore(directory);
            Check(new PluginCatalog(reopenedStore, new Configuration(reopenedStore)).List().Count == 1, "plugin declaration survives StateStore reopen");
            catalog.Install(initial);
            Check(settings.Get().McpServers.Count == 1 && catalog.List().Count == 1, "identical plugin install is idempotent");
            var revised = Manifest(); revised.Version = "1.0.1";
            Denied(() => catalog.Install(revised), "plugin ID cannot silently replace a different installed manifest");
            var collision = settings.Get(); collision.McpServers.Add(new() { Id = "existing", Executable = initial.Server.Executable, ViaUserConnector = false }); settings.Save(collision);
            Denied(() => catalog.Install(Manifest("existing")), "plugin install preserves an existing MCP registration");
            Check(catalog.List().Count == 1 && settings.Get().McpServers.Count == 2, "rejected plugin conflict leaves declaration and config unchanged");
            var identity = Manifest("identity"); identity.Server.Id = "different";
            Denied(() => catalog.Validate(identity), "plugin and provider identities must match");
            var future = Manifest("future"); future.Protocol = 2;
            Denied(() => catalog.Validate(future), "unknown extension protocol is rejected");
            future = Manifest("future"); future.SchemaVersion = 2;
            Denied(() => catalog.Validate(future), "unknown manifest schema is rejected");
            future = Manifest("future"); future.Kind = "assembly";
            Denied(() => catalog.Validate(future), "dynamic assembly plugin kind is rejected");
            var connector = Manifest("connector"); connector.Server.ViaUserConnector = true; connector.Server.Url = "http://192.0.2.1:6124";
            Denied(() => catalog.Validate(connector), "user connector manifest cannot select a non-loopback endpoint");
            var enabled = settings.Get(); enabled.McpServers.Single(x => x.Id == "fixture").Enabled = true; settings.Save(enabled);
            Check(catalog.List().Single().Enabled && !catalog.List().Single().ConfigurationChanged, "normal explicit enable is reflected without registration drift");
            Denied(() => catalog.Remove("fixture"), "enabled plugin registration cannot be removed");
            enabled = settings.Get(); enabled.McpServers.Single(x => x.Id == "fixture").Enabled = false; settings.Save(enabled);
            store.Put("agent", "grant-holder", new AgentProfile { Id = "grant-holder", Capabilities = ["mcp.fixture"], AllowedTools = ["mcp.fixture.echo"] });
            Denied(() => catalog.Remove("fixture"), "plugin removal requires revoking the profile capability and tool grants");
            store.Delete("agent", "grant-holder");
            var drifted = settings.Get(); drifted.McpServers.Single(x => x.Id == "fixture").Arguments = ["changed"]; settings.Save(drifted);
            Check(catalog.Inspect("fixture").ConfigurationChanged, "edited provider configuration is reported as drift");
            Denied(() => catalog.Remove("fixture"), "plugin removal preserves an independently edited registration");
            drifted = settings.Get(); drifted.McpServers.RemoveAll(x => x.Id == "fixture"); settings.Save(drifted);
            Check(!catalog.Remove("fixture").RegistrationRemoved && settings.Get().McpServers.Single().Id == "existing", "removing a detached declaration preserves unrelated providers");
            catalog.Install(Manifest("clean"));
            Check(catalog.Remove("clean").RegistrationRemoved && catalog.List().Count == 0, "disabled unused plugin removes its own unchanged registration");
            Console.WriteLine($"RESULT: {passed} declarative plugin checks passed.");
            return Task.CompletedTask;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
