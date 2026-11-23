using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Private UI lease for one review of one exact composer owner and revision.</summary>
internal sealed class ComposerRefinementLease : IDisposable
{
    private readonly TextBox composer;
    private readonly Func<DesktopDraftSnapshot> read;
    private readonly Action stale;
    private readonly bool priorReadOnly;
    private bool applying;
    private bool disposed;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenRegistration cancelled;
    internal DesktopDraftSnapshot Original { get; }
    internal CancellationToken Token { get; }
    internal bool IsCurrent => !disposed && !Token.IsCancellationRequested && read() == Original;

    internal ComposerRefinementLease(TextBox composer, Func<DesktopDraftSnapshot> read, Action stale, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.composer = composer; this.read = read; this.stale = stale;
        Original = read(); priorReadOnly = composer.IsReadOnly;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); Token = lifetime.Token;
        composer.IsReadOnly = true;
        composer.TextChanged += Changed;
        cancelled = cancellationToken.Register(() =>
        {
            void CancelReview() { if (disposed) return; Dispose(); stale(); }
            if (composer.Dispatcher.CheckAccess()) CancelReview();
            else if (!composer.Dispatcher.HasShutdownStarted) composer.Dispatcher.BeginInvoke(CancelReview);
        });
    }

    internal DesktopOperationResult Apply(DesktopDraftUpdate update)
    {
        if (disposed || Token.IsCancellationRequested || read() != Original || update.Binding != Original.Binding || update.ExpectedRevision != Original.Revision)
        {
            Dispose();
            return new(DesktopOperationState.Rejected, "The composer owner or draft changed during refinement. Current text was preserved.");
        }
        applying = true;
        try { composer.Text = update.Text; return new(DesktopOperationState.Completed); }
        finally { applying = false; }
    }

    private void Changed(object sender, TextChangedEventArgs args)
    {
        if (disposed || applying) return;
        Dispose(); stale();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; composer.TextChanged -= Changed;
        composer.IsReadOnly = priorReadOnly;
        cancelled.Unregister();
        lifetime.Cancel(); lifetime.Dispose();
        // Never restore an old draft over a newer owner/editor change, and do not move its caret.
    }
}

internal sealed class ComposerRefinementSlot
{
    internal ComposerRefinementLease? Current { get; private set; }
    internal ComposerRefinementLease Begin(TextBox composer, Func<DesktopDraftSnapshot> read, Action stale, CancellationToken cancellationToken = default)
    {
        Release();
        return Current = new(composer, read, stale, cancellationToken);
    }
    internal bool IsCurrent(ComposerRefinementLease lease) => ReferenceEquals(Current, lease) && lease.IsCurrent;
    internal void Release(ComposerRefinementLease? expected = null)
    {
        if (expected is not null && !ReferenceEquals(expected, Current)) return;
        var previous = Current; Current = null; previous?.Dispose();
    }
}
