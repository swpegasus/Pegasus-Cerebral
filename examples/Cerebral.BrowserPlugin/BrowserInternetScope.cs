using Cerebral.Core;

namespace Cerebral.BrowserPlugin;

// One host-issued invocation, never reconstructed from browser/model arguments or an old session.
internal sealed class BrowserInternetScope : IDisposable
{
    private readonly IManagedInternetSession session;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken token;
    private int disposed;

    private BrowserInternetScope(IManagedInternetSession session, ManagedInternetAuthority authority, CancellationToken cancellationToken)
    {
        this.session = session; Authority = authority;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        token = lifetime.Token;
    }

    internal ManagedInternetAuthority Authority { get; }
    internal CancellationToken Token => token;

    internal static BrowserInternetScope? Capture(ToolExecutionContext context, string tool, CancellationToken token)
    {
        if (tool is not ("browser.request" or "browser.open" or "browser.navigate" or "browser.click" or "browser.fill"))
            return null;
        var session = context.Internet;
        if (session is null)
            throw new BrowserRejectedException("managed-internet-unavailable", "This action requires a current host-managed Internet scope.");
        if (session.Version != InternetAuthorityContract.Version || session.Authority is not { SchemaVersion: 1 })
            throw new BrowserRejectedException("managed-internet-contract-mismatch", "The host-managed Internet contract is unavailable.");
        var authority = session.Authority;
        if (string.IsNullOrWhiteSpace(authority.WorkerInstallationId) || string.IsNullOrWhiteSpace(authority.MainConversationId) ||
            string.IsNullOrWhiteSpace(authority.ToolFingerprint) || string.IsNullOrWhiteSpace(context.EffectId) ||
            authority.ConversationId != context.SessionId || authority.RunId != context.JobId ||
            authority.EffectId != context.EffectId || authority.ToolName != tool)
            throw new BrowserRejectedException("managed-internet-owner-mismatch", "The Internet scope does not belong to this exact host action.");
        token.ThrowIfCancellationRequested();
        return new(session, authority, token);
    }

    internal async Task<ManagedInternetResponse> SendAsync(ManagedInternetRequest request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new InternetAccessException("managed-internet-scope-expired", "This browser action's Internet scope has ended.");
        if (session.Version != InternetAuthorityContract.Version || session.Authority != Authority)
            throw new InternetAccessException("managed-internet-owner-changed", "The host Internet scope changed before dispatch.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        // The host broker owns the only socket path, live policy/revision checks and shared scheduler.
        return await session.SendAsync(request, linked.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lifetime.Cancel(); } finally { lifetime.Dispose(); }
    }
}
