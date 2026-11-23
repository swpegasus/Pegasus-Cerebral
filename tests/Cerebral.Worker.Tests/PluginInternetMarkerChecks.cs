using System.Reflection;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

/// <summary>Pure existing-host wrapper and offline catalog checks; never invokes a provider or network.</summary>
public static class PluginInternetMarkerChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        static IToolExtension Wrap(IToolExtension inner)
        {
            var snapshot = typeof(ServicePluginRuntime).GetNestedType("ToolExtensionSnapshot", BindingFlags.NonPublic)!;
            var constructor = snapshot.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(x => x.GetParameters().Length == 2 && x.GetParameters()[0].ParameterType == typeof(IToolExtension));
            return (IToolExtension)constructor.Invoke([inner, inner.Tools]);
        }
        var aware = new Aware(); var wrapped = Wrap(aware); var legacy = Wrap(new Legacy());
        Check(wrapped is IManagedInternetToolProvider { ManagedInternetVersion: 1 },
            "existing plugin descriptor wrapper preserves explicit broker marker");
        Check(legacy is IManagedInternetToolProvider { ManagedInternetVersion: 0 },
            "legacy provider remains unavailable rather than acquiring an implicit marker");
        await using var tools = new ToolDispatcher(() => new HostSettings(), [wrapped]);
        Check(tools.ListManagedInternet().Tools.Any(x => x.Descriptor.Name == "browser.request"),
            "offline managed catalog admits wrapped v1 Browser provider");
        var profile = new AgentProfile { Id = "fixture", AllowedTools = ["browser.request"], Capabilities = ["browser.read"] };
        var result = await tools.ExecuteAsync(new("browser.request", CerebralJson.Element(new { })),
            new(profile, "chat", "run", EffectId: "effect") { EnforceProfileScope = true }, default);
        Check(!result.Success && aware.Calls == 0, "missing host scope rejects before wrapped provider dispatch");
        return checks;
    }
    private class Legacy : IToolExtension
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = [new("browser.request", "fixture", CerebralJson.Element(
            new { type = "object", properties = new { }, additionalProperties = false }), "browser.read")];
        public int Calls;
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Fixture provider must not execute."); }
    }
    private sealed class Aware : Legacy, IManagedInternetToolProvider
    { public int ManagedInternetVersion => InternetAuthorityContract.Version; }
}
