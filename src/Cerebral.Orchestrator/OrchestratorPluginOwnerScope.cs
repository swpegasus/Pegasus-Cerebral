using Cerebral.Client;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

// A package generation retains the verified owner it was opened against, even across reconnects.
internal sealed record OrchestratorPluginOwnerState(CerebralClient? Client, string Identity, long Epoch, bool Online);

internal sealed class OrchestratorPluginOwnerScope(OrchestratorPluginOwnerState captured,
    Func<OrchestratorPluginOwnerState> current, CancellationToken lifetime)
{
    private int retired;
    internal void Retire() => Interlocked.Exchange(ref retired, 1);

    // Diagnostics may observe a stale owner but must not retire it or refresh its views.
    internal bool ObserveMatchesOwner() => Volatile.Read(ref retired) == 0 && CapturedOwnerIsCurrent();

    private bool CapturedOwnerIsCurrent()
    {
        var now = current();
        return !lifetime.IsCancellationRequested && ReferenceEquals(captured.Client, now.Client) &&
            captured.Identity == now.Identity && captured.Epoch == now.Epoch && captured.Online == now.Online;
    }

    internal bool MatchesOwner()
    {
        if (Volatile.Read(ref retired) != 0) return false;
        if (!CapturedOwnerIsCurrent()) Retire();
        return Volatile.Read(ref retired) == 0;
    }

    internal async Task<DesktopServiceCommandResult> Dispatch(string operation, object arguments, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        linked.Token.ThrowIfCancellationRequested();
        if (!MatchesOwner())
            return new(false, default, "The Chat owner changed. Reopen this view before another operation.");
        if (!captured.Online || captured.Client is null)
            return new(false, default, "Connect before opening plugin settings.");

        // Never resolve the mutable window client here. Its transport retains its own owner guards.
        var result = await captured.Client.CommandAsync(operation, arguments, cancellationToken: linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!MatchesOwner())
            throw new InvalidOperationException("The previous owner may have accepted this operation. Reopen to inspect its state; do not replay it on the new owner.");
        return new(result.Success, result.Data, result.Error);
    }
}
