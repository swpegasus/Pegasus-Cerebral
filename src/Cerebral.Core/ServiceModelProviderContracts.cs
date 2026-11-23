using System.Net.Http;
using System.Text.Json;

namespace Cerebral.Core;

/// <summary>
/// Host-owned activation for one admitted service-package generation and one bounded provider
/// configuration revision. Before invoking the synchronous factory, the host bounds and clones
/// Profile; it contains references, never credential values. HttpHandler is dedicated to this
/// activation and host-owned. The host resolves credentials only for an admitted call and keeps
/// the handler, resolver, fragment sink, and package generation alive until tracked calls drain.
/// </summary>
public sealed record ServiceModelProviderActivation(
    ServicePluginPackageBinding Package,
    string ProviderId,
    JsonElement Profile,
    HttpMessageHandler HttpHandler,
    Func<string, int, CancellationToken, ValueTask<string?>> ResolveCredential,
    Action<JsonElement>? PrivateFragmentSink = null);

/// <summary>
/// A configured package runtime speaks the bounded model-provider command protocol. The generic
/// Worker host owns count-quality admission, workflow adaptation, and the runtime's lifetime.
/// Dispose requests cancellation; it does not prove that in-flight ExecuteAsync calls have drained.
/// The host checks the captured package generation before every operation and retains dependencies
/// until all tracked calls settle.
/// </summary>
public interface IServiceConfiguredModelProviderRuntime : IServiceCommandExtension, IDisposable
{
}

/// <summary>
/// Optional service-plugin facet. Registration is inert; the Worker calls the factory only after
/// it has joined an exact selected package/version/generation, provider, model, and configuration.
/// The module validates the package ID, version, loaded/error state and generation, provider ID,
/// and its cloned model declarations. The host rechecks that exact captured generation per call.
/// A package compiled against this facet requires a Core SDK that contains it; an older loader
/// running that SDK may ignore the facet and keep its unconfigured describe-only path.
/// </summary>
public interface IServiceModelProviderModule
{
    int ContractVersion => 1;
    string ProviderId { get; }
    IServiceConfiguredModelProviderRuntime CreateConfiguredRuntime(ServiceModelProviderActivation activation);
}
