namespace Cerebral.Desktop.Plugins;

/// <summary>Evidence for the declared initialization contract, not arbitrary unreported asynchronous work.</summary>
public sealed record DesktopPluginStartupReceipt(string Id, string Version, string Contract);

public sealed class DesktopPluginStartupException(string pluginId, Exception cause, bool callbackStillRunning = false)
    : InvalidOperationException("Plugin '" + pluginId + "' did not complete its declared startup: " + cause.Message, cause)
{
    public string PluginId { get; } = pluginId;
    public bool CallbackStillRunning { get; } = callbackStillRunning;
}

public sealed partial class DesktopPluginRuntime
{
    /// <summary>Wait for the exact enabled generations already loaded by this runtime.
    /// No initialization, refresh, package activation or callback retry is performed.</summary>
    public async Task<IReadOnlyList<DesktopPluginStartupReceipt>> WaitForStartupAsync(CancellationToken cancellationToken)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using var observation = catalog.TryBeginObservation() ?? throw DesktopPluginPublicationLease.Busy();
        var selected = catalog.List().Where(item => item.Enabled).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var receipts = new List<DesktopPluginStartupReceipt>();
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadError = errors.GetValueOrDefault(package.Id);
            if (package.Error is not null || loadError is not null)
                throw new DesktopPluginStartupException(package.Id, startupErrors.GetValueOrDefault(package.Id) ??
                    new InvalidOperationException(package.Error ?? loadError ?? "The enabled package could not be loaded."));
            if (!loaded.TryGetValue(package.Id, out var owner) || owner.PackageDirectory != package.Directory || owner.Version != package.Version)
                throw new DesktopPluginStartupException(package.Id, new InvalidOperationException("The enabled package generation is not loaded."));
            var invocation = BeginInvocation(owner);
            Task? waiting = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Lifetime.Token);
            try
            {
                if (owner.Plugin is IDesktopPluginStartupReadiness participant)
                {
                    RaiseLoadProgress(new(owner.Id, "starting", "Waiting for declared preferences/startup completion."));
                    waiting = participant.WaitForStartupAsync(linked.Token) ?? throw new InvalidOperationException("The plugin returned no startup task.");
                    await waiting.WaitAsync(linked.Token);
                }
                linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
                RequireSelectedInstance(owner, new HashSet<LoadedPlugin>());
                receipts.Add(new(owner.Id, owner.Version, waiting is null ? "initialize-returned" : "startup-facet-completed"));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                errors[owner.Id] = BoundError(Error(error)); startupErrors[owner.Id] = error;
                RaiseLoadProgress(new(owner.Id, "failed", Error(error)));
                throw new DesktopPluginStartupException(owner.Id, error, waiting is { IsCompleted: false });
            }
            finally
            {
                // A participant may ignore cancellation. Keep its invocation and reader
                // lease until actual settlement, so retirement cannot unload native code
                // or a retry construct a replacement behind the outstanding callback.
                if (waiting is { IsCompleted: false }) _ = ReleaseStartupInvocationAsync(waiting, invocation);
                else invocation.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return receipts;
    }

    private static async Task ReleaseStartupInvocationAsync(Task waiting, IDisposable invocation)
    {
        try { await waiting.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Original failure is retained by the startup attempt. */ }
        finally { invocation.Dispose(); }
    }
}
