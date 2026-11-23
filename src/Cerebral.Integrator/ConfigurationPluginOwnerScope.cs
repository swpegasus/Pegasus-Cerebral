using Cerebral.Client;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using System.Windows.Threading;
namespace Cerebral.Integrator;

// A runtime generation never reroutes an admitted callback to a replacement owner.
internal sealed record ConfigurationPluginOwnerState(CerebralClient? Client, string Identity, long Epoch, bool Online);
internal sealed class ConfigurationPluginOwnerScope(ConfigurationPluginOwnerState captured, Func<ConfigurationPluginOwnerState> current)
{
    private readonly string generation = "configuration-owner-" + Guid.NewGuid().ToString("N");
    private int retired;
    internal bool MatchesOwner()
    {
        var now = current();
        if (!ReferenceEquals(captured.Client, now.Client) || captured.Identity != now.Identity ||
            captured.Epoch != now.Epoch || captured.Online != now.Online)
            Interlocked.Exchange(ref retired, 1);
        return Volatile.Read(ref retired) == 0;
    }
    internal DesktopConversationBinding Binding() => new(MatchesOwner() ? generation : "retired-" + generation, null);
    internal DesktopPluginHostAdapter CreateAdapter(Dispatcher dispatcher, Action<string> status, string dataRoot,
        Func<DesktopProductPaths> productPaths)
    {
        Task<DesktopOperationResult> ConfigurationOnly() => Task.FromResult(new DesktopOperationResult(
            DesktopOperationState.Rejected, "Conversation operations are unavailable in Configuration."));
        return new(dispatcher,
            () => new(Binding(), "Pegasus Cerebral Configuration", null, null, false),
            () => new(Binding(), 0, ""),
            (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(),
            status, DesktopPluginSurface.Configuration, dataRoot, Dispatch,
            productPaths: productPaths);
    }
    internal static DesktopHostedPanel[] PreparePanels(string pluginId, ConfigurationPluginOwnerScope? owner,
        DesktopPluginRuntime? runtime, Func<bool> reload, Func<DesktopPluginRuntime?> currentRuntime)
    {
        // A fresh scope may outlive a failed load while the previous runtime drains; retry that guarded admission.
        if ((owner?.MatchesOwner() != true || runtime?.Panels.Any(panel => panel.PluginId == pluginId) != true) && !reload())
            throw new InvalidOperationException("The previous plugin owner is retiring. Reopen settings after its cleanup settles.");
        var panels = currentRuntime()?.Panels.Where(panel => panel.PluginId == pluginId).ToArray() ?? [];
        if (panels.Length == 0) throw new InvalidOperationException("This plugin does not provide a Configuration panel.");
        return panels;
    }
    private async Task<DesktopServiceCommandResult> Dispatch(string operation, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!captured.Online || captured.Client is null)
            return new(false, default, "Connect before opening plugin settings.");
        if (!MatchesOwner())
            return new(false, default, "The Configuration owner changed. Reopen this panel before another operation.");
        // Capture is immutable. Client retains its own transport/installed-owner replacement guards.
        var result = await captured.Client.CommandAsync(operation, arguments, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!MatchesOwner())
            throw new InvalidOperationException("The previous owner may have accepted this operation. Reopen to inspect its state; do not replay it on the new owner.");
        return new(result.Success, result.Data, result.Error);
    }
}