using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using System.Reflection;

namespace Cerebral.Worker.Tests;

internal static class CortexFirstRunDiscoveryTests
{
    public static void Run(Action<bool, string> check)
    {
        var absent = CortexFirstRunDiscovery.Inspect(null);
        check(absent.State == "unavailable" && absent.ExecutablePath is null,
            "absent local Cortex registration has no provider path");
        var incompatible = new CortexFirstRunCandidate("incompatible", "repair", null);
        check(incompatible.SaveInitialTo(new HostSettings()),
            "incompatible local Cortex can retain an informational status");

        var executable = @"C:\Fixture\Cortex\PegasusCortex.exe";
        var compatible = new CortexFirstRunCandidate("compatible", "finish enrollment", executable);
        var settings = new HostSettings();
        check(compatible.SaveInitialTo(settings), "compatible local Cortex observation is recorded in new Cerebral settings");
        check(settings.InitialCortexDiscovery is { State: "compatible" } marker && marker.ExecutablePath == executable &&
              settings.McpServers.Count == 0,
            "discovery metadata never becomes a selectable native provider");
        var selector = typeof(ToolDispatcher).GetMethod("SelectHostUserPaoProvider", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Archive transport selector is missing.");
        check(selector.Invoke(null, [settings]) is null,
            "ordinary local archive routing is retained with discovery metadata only");
        check(!compatible.SaveInitialTo(settings) && settings.McpServers.Count == 0,
            "repeated discovery preserves the initial observation without adding a provider");

        var existing = new HostSettings { McpServers = [new() { Id = "cortex", Executable = @"C:\Existing\PegasusCortex.exe", Enabled = true }] };
        check(compatible.SaveInitialTo(existing) && existing.McpServers.Single().Executable == @"C:\Existing\PegasusCortex.exe",
            "initial observation does not replace an existing Cortex registration");
        var otherNative = new HostSettings { McpServers = [new() { Id = "my-cortex", CortexNativeRequired = true }] };
        check(compatible.SaveInitialTo(otherNative) && otherNative.McpServers.Count == 1 && otherNative.McpServers[0].CortexNativeRequired,
            "initial observation leaves a genuinely selected native transport unchanged");
        var disabledRefused = false;
        try { _ = selector.Invoke(null, [otherNative]); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException inner &&
            inner.Message.Contains("provider is disabled", StringComparison.Ordinal)) { disabledRefused = true; }
        check(disabledRefused, "genuine disabled native archive provider still refuses legacy fallback");
    }
}
