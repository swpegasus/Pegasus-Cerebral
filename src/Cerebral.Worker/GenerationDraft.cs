namespace Cerebral.Worker;

// Private observation only: Content is untrusted public provider-channel JSON, not UI text.
// The existing workflow preview parser is the sole public projection boundary.
internal sealed record GenerationDraftBinding(string ConversationId, string RequestId, string RunId,
    string EffectId, string AgentId, string WorkerId);
internal enum GenerationDraftKind { Content, Unavailable }
internal sealed record GenerationDraftSample(GenerationDraftBinding Binding, long Sequence,
    GenerationDraftKind Kind, string? Content = null);
internal interface IBoundGenerationDraft
{
    bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer);
}

// One preparation, one optional binding, no retained text or asynchronous publication queue.
// Callbacks are serialized with completion so none can begin after the channel closes.
internal sealed class GenerationDraftChannel
{
    private readonly object gate = new();
    private GenerationDraftBinding? binding;
    private Action<GenerationDraftSample>? observer;
    private bool started, closed, bound;
    private long sequence;

    internal bool TryBind(GenerationDraftBinding value, Action<GenerationDraftSample> target)
    {
        if (value is null || target is null || new[] { value.ConversationId, value.RequestId, value.RunId,
                value.EffectId, value.AgentId, value.WorkerId }.Any(id =>
                id is not { Length: > 0 and <= 256 } || id.Any(char.IsControl))) return false;
        lock (gate)
        {
            if (bound || started || closed) return false;
            bound = true; binding = value; observer = target;
            return true;
        }
    }

    internal void Start() { lock (gate) started = true; }
    internal void Append(string fragment)
    {
        lock (gate)
        {
            if (!started || closed || observer is null || fragment.Length == 0) return;
            try { observer(new(binding!, ++sequence, GenerationDraftKind.Content, fragment)); }
            catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
            {
                // A failed observer is permanently disabled; it never changes native inference.
                CloseCore(invalidate: true);
            }
        }
    }
    internal void Complete(bool accepted)
    {
        lock (gate) CloseCore(invalidate: !accepted);
    }
    private void CloseCore(bool invalidate)
    {
        if (closed) return;
        closed = true;
        var target = observer; observer = null;
        if (invalidate && target is not null)
        {
            try { target(new(binding!, ++sequence, GenerationDraftKind.Unavailable)); }
            catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
        }
        binding = null;
    }
}

