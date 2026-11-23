using Cerebral.Core;

namespace Cerebral.Client;

// Only connection refusal means "stopped". Timeouts, denial and identity mismatches must not start a replacement.
public interface IServiceStartupOperations
{
    Task<HostInfo?> ProbeWorkerAsync(string url, string accessKey, CancellationToken token);
    Task<bool> IsWorkerInstalledAsync(CancellationToken token);
    Task StartInstalledWorkerAsync(CancellationToken token);
    Task StartLocalWorkerAsync(string url, CancellationToken token);
    Task<ConnectorStatus> ConnectorStatusAsync(CancellationToken token);
    Task<ConnectorStatus> StartConnectorAsync(CancellationToken token);
}

public sealed record ServiceStartupResult(string Worker, string Connector, string Message, bool Warning = false);

public sealed class ServiceStartup(IServiceStartupOperations operations)
{
    public async Task<ServiceStartupResult> EnsureAsync(string url, string accessKey, bool enabled, CancellationToken token)
    {
        if (!enabled) return new("manual", "manual", "Automatic startup is disabled in Integrator connection settings.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("A valid saved service origin is required before startup.");
        if (!endpoint.IsLoopback) return new("remote", "unchanged", "Connected remote machines keep their own lifecycle; no local service was started.");
        if (endpoint.Scheme != "http") throw new ArgumentException("Automatic local startup requires the configured loopback HTTP worker address.");
        token.ThrowIfCancellationRequested();
        var host = await operations.ProbeWorkerAsync(url, accessKey, token);
        var worker = "already-running";
        if (host is not null) ValidateHost(host);
        else
        {
            if (await operations.IsWorkerInstalledAsync(token))
            {
                await operations.StartInstalledWorkerAsync(token);
                worker = "installed-started";
            }
            else
            {
                await operations.StartLocalWorkerAsync(url, token);
                worker = "local-started";
            }
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
            ready.CancelAfter(TimeSpan.FromSeconds(20));
            while (host is null)
            {
                ready.Token.ThrowIfCancellationRequested();
                host = await operations.ProbeWorkerAsync(url, accessKey, ready.Token);
                if (host is null) await Task.Delay(200, ready.Token);
            }
            ValidateHost(host);
        }
        try
        {
            var connector = await operations.ConnectorStatusAsync(token);
            if (connector.State == "stopped" && connector.ProviderCount > 0)
                connector = await operations.StartConnectorAsync(token);
            var inactive = connector.State == "not-configured" || connector.State == "stopped" && connector.ProviderCount == 0;
            var warning = connector.State != "running" && !inactive;
            return new(worker, connector.State,
                "Worker ready. " + (connector.State == "running" ? "The configured user connector is running." :
                inactive ? "No configured user providers need startup." : "User connector: " + connector.State + ". " + connector.Message) +
                " Closing Integrator leaves started services running.", warning);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(worker, "unavailable", "Worker ready; the user connector could not be started: " + ex.Message +
                " Inspect User-session connector. Worker operation is independent.", true);
        }
    }
    private static void ValidateHost(HostInfo host)
    {
        if (host.Product != "Pegasus Cerebral" || host.Protocol != 1)
            throw new InvalidOperationException("A different or incompatible service owns the saved address. No replacement was started.");
    }
}
