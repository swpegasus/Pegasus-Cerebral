using System.Text.Json;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    // Trusted desktop consumers reuse the owner connection. This does not add a model tool,
    // expose enrollment material, or make an asset reference an authorization grant.
    private object CortexAssetProviders(JsonElement arguments)
    {
        if (arguments.EnumerateObject().Any()) throw new ArgumentException("Provider listing accepts no arguments.");
        var providers = configuration.Get().McpServers.Where(server => server.Enabled && server.CortexNative is not null).ToArray();
        return new { providers = providers.Take(8).Select(server => new { server.Id, transport = "cortex-native-pipe" }).ToArray(), totalCount = providers.Length };
    }

    private Task<JsonElement> ReadCortexAssetResourceAsync(JsonElement arguments, CancellationToken token)
    {
        if (arguments.EnumerateObject().Count() != 2 || arguments.EnumerateObject().Any(item => item.Name is not ("serverId" or "uri")))
            throw new ArgumentException("Asset reads require only serverId and an exact passive resource URI.");
        return tools.ReadCortexAssetResourceAsync(Text(arguments, "serverId"), Text(arguments, "uri"), token);
    }
}
