using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private async Task<DesktopControlResponse> InvokePluginActionFromSemanticControlAsync(DesktopControlRequest request,
        CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (semanticAttachInvocation.Value is null)
            throw new InvalidOperationException("Plugin semantic control requires the original expected selection.");
        RequireSemanticAttachCurrent();
        var id = RequiredString(request.Arguments, "id", 160);
        var runtime = desktopPlugins ?? throw new InvalidOperationException("Desktop plugins are unavailable.");
        var host = desktopPluginHost ?? throw new InvalidOperationException("The original desktop plugin host is unavailable.");
        var owner = desktopPluginOwner ?? throw new InvalidOperationException("The original desktop plugin owner is unavailable.");
        var action = runtime.ComposerActions.SingleOrDefault(item =>
            string.Equals(item.PluginId + ":" + item.Id, id, StringComparison.Ordinal)) ??
            throw new InvalidOperationException("The requested plugin action is not registered in this desktop generation.");
        var authority = CaptureSemanticAttachAuthority() ??
            throw new InvalidOperationException("Plugin semantic control requires a captured current owner.");

        bool Current()
        {
            Dispatcher.VerifyAccess();
            if (!authority() || !ReferenceEquals(desktopPlugins, runtime) || !ReferenceEquals(desktopPluginHost, host) ||
                !ReferenceEquals(desktopPluginOwner, owner) || !owner.ObserveMatchesOwner()) return false;
            var matches = runtime.ComposerActions.Where(item =>
                string.Equals(item.PluginId + ":" + item.Id, id, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 && ReferenceEquals(matches[0], action);
        }

        var dispatched = false;
        using var fence = new SemanticPluginActionFence(Dispatcher, Current, token, Lifetime.Token);
        try
        {
            fence.RequireCurrent();
            // Reuse the normal handler and its hosted callback/package invocation lifetime.
            // Only this private expected envelope is removed; its original capture stays above.
            var normal = request with { Arguments = CerebralJson.Element(new { id }) };
            dispatched = true;
            var response = await HandleDesktopControlAsync(normal, fence.Token);
            fence.RequireCurrent();
            if (!response.Success || response.Error is not null)
                return PluginActionUnconfirmed(request, id);
            return DesktopControlProtocol.Result(request, "accepted", new
            {
                id, status = "action-callback-returned",
                visibilityVerified = false, focusVerified = false, backendPersistenceVerified = false,
                scope = "hosted-callback-task"
            });
        }
        catch (Exception) when (dispatched)
        {
            // Callback entry, cancellation or a missing reply can follow an effect. Neither
            // the current snapshot nor an exception can prove absence or authorize replay.
            return PluginActionUnconfirmed(request, id);
        }
    }

    private static DesktopControlResponse PluginActionUnconfirmed(DesktopControlRequest request, string id) =>
        DesktopControlProtocol.Result(request, "uncertain", new
        {
            id, status = "unconfirmed", visibilityVerified = false,
            focusVerified = false, backendPersistenceVerified = false
        }, "Plugin action outcome is unconfirmed. Inspect the original action state before another explicit action; no automatic replay was sent.");

    // A cooperative queued callback checks this token at execution. The observer does
    // not block or rewrite UI operations; it cancels authority that is no longer current.
    // It covers the returned hosted Task, not detached work escaping that Task.
    private sealed class SemanticPluginActionFence : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Func<bool> current;
        private readonly CancellationTokenSource cancellation;
        private Exception? failure;

        internal SemanticPluginActionFence(Dispatcher dispatcher, Func<bool> current,
            CancellationToken requestToken, CancellationToken lifetime)
        {
            dispatcher.VerifyAccess();
            this.dispatcher = dispatcher; this.current = current;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestToken, lifetime);
            dispatcher.Hooks.OperationStarted += OperationStarted;
        }

        internal CancellationToken Token => cancellation.Token;

        internal void RequireCurrent()
        {
            dispatcher.VerifyAccess();
            cancellation.Token.ThrowIfCancellationRequested();
            if (failure is not null || !current())
                throw new InvalidOperationException("The original plugin action owner, selection or registration changed.");
        }

        private void OperationStarted(object? sender, DispatcherHookEventArgs args)
        {
            // Hooks run before a queued callback. Never let cancellation observers fault
            // the dispatcher; retain the failure and fail the semantic result closed.
            try { if (!current()) cancellation.Cancel(); }
            catch (Exception error)
            {
                failure ??= error;
                try { cancellation.Cancel(); } catch (Exception secondary) { failure ??= secondary; }
            }
        }

        public void Dispose()
        {
            dispatcher.VerifyAccess();
            dispatcher.Hooks.OperationStarted -= OperationStarted;
            cancellation.Dispose();
        }
    }
}
