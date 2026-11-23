using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

// Main-thread state only. Metadata stays in the host and is never passed to the contributor/model.
internal sealed record ComposerSuggestionAnchor(object Owner, DesktopConversationBinding Binding, string MessageId, long DraftRevision);

internal sealed class ComposerSuggestionState
{
    internal sealed class Request(ComposerSuggestionAnchor anchor, long generation) : IDisposable
    {
        internal ComposerSuggestionAnchor Anchor { get; } = anchor;
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Cancellation { get; } = new();
        public void Dispose() => Cancellation.Dispose();
    }
    private long generation;
    private Request? pending;
    private ComposerSuggestionAnchor? accepted;
    internal string? Text { get; private set; }

    internal Request Begin(ComposerSuggestionAnchor anchor)
    {
        Invalidate();
        return pending = new(anchor, generation);
    }

    internal void Invalidate()
    {
        generation++;
        var previous = pending;
        pending = null; accepted = null; Text = null;
        try { previous?.Cancellation.Cancel(); }
        catch (AggregateException) { /* Cancellation cannot preserve a stale suggestion. */ }
    }

    internal bool Complete(Request request, ComposerSuggestionAnchor current, bool emptyAndAvailable, string? text)
    {
        if (!ReferenceEquals(pending, request) || request.Generation != generation || request.Cancellation.IsCancellationRequested ||
            request.Anchor != current || !emptyAndAvailable || !DesktopComposerSuggestionContract.ValidSuggestion(text)) return false;
        pending = null; accepted = current; Text = text;
        return true;
    }

    internal bool Take(ComposerSuggestionAnchor current, bool emptyAndAvailable, out string? text)
    {
        text = accepted == current && emptyAndAvailable ? Text : null;
        Invalidate();
        return text is not null;
    }

    internal void Finish(Request request)
    {
        if (ReferenceEquals(pending, request)) pending = null;
        request.Dispose();
    }
}
