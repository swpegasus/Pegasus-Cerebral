using System.Text.Json;
using Cerebral.Core;
using Cerebral.PermissionUi;

namespace Cerebral.Integrator;

internal static class ProfileRegisteredCatalog
{
    internal static async Task<ProfilePermissionCatalog> ReadAsync(
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch,
        Func<bool> current, CancellationToken token)
    {
        var catalog = await RegisteredPermissionCatalog.ReadAsync(async (args, cancellation) =>
        {
            var result = await dispatch(new("tools.registered.catalog", CerebralJson.Element(args)), cancellation);
            if (!result.Success) throw new InvalidOperationException(result.Error ??
                "The selected Worker does not support the registered permission catalog.");
            return result.Data;
        }, current, token);
        var toolRows = catalog.Items.Where(x => x.Kind == "tool").ToArray();
        // Display-only metadata. An undefined schema is never advertised or executed by this editor.
        var tools = toolRows.Select(x => new ToolDescriptor(x.Id, x.Detail, default, x.Capability!)).ToArray();
        var caps = catalog.Items.Where(x => x.Kind == "capability").ToArray();
        var descriptions = caps.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.Detail)
            ? "Allows this capability for the profile. Each tool also needs its own exact or wildcard grant."
            : x.Detail, StringComparer.Ordinal);
        var complete = catalog.UnavailableServers.Length == 0;
        var status = complete ? "Registered tools and capabilities on the selected local Worker." :
            "Some registered providers are unavailable: " + string.Join(", ", catalog.UnavailableServers) + ".";
        return new(tools, complete, status, caps.Select(x => x.Id).ToArray(), descriptions);
    }
}
