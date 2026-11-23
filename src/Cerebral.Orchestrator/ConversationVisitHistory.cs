namespace Cerebral.Orchestrator;

// Session-only navigation coordinates. The window owns validation and reopening; this class
// only orders bounded visits and never contacts a Worker or persists conversation content.
internal sealed class ConversationVisitHistory<TLocalState> where TLocalState : class
{
    internal sealed record Visit(long Sequence, string ConversationId, string? ProjectId,
        string ConnectionIdentity, bool IsLocalDraft, bool IsLegacy, TLocalState? LocalState);

    private readonly List<Visit> visits = [];
    private long nextSequence;
    private int cursor = -1;
    private int capacity;

    internal ConversationVisitHistory(int capacity = 40) => SetCapacity(capacity);
    internal int Capacity => capacity;
    internal int Count => visits.Count;
    internal int CursorIndex => cursor;
    internal int Revision { get; private set; }
    internal Visit? Current => cursor < 0 ? null : visits[cursor];
    internal bool CanBack => cursor > 0;
    internal bool CanForward => cursor >= 0 && cursor < visits.Count - 1;

    internal void SetCapacity(int value)
    {
        if (value is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(value));
        capacity = value;
        while (visits.Count > capacity)
        {
            if (cursor > 0) { visits.RemoveAt(0); cursor--; }
            else visits.RemoveAt(visits.Count - 1);
        }
        Revision++;
    }

    internal void Clear()
    {
        visits.Clear(); cursor = -1; Revision++;
    }

    internal void Record(string conversationId, string? projectId, string connectionIdentity,
        bool isLocalDraft, bool isLegacy, TLocalState? localState)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(connectionIdentity) ||
            isLocalDraft != (localState is not null) || isLocalDraft && isLegacy)
            throw new ArgumentException("A navigation visit requires one bound conversation and a local state only for an unsent draft.");
        if (Current is { } current && current.ConversationId == conversationId &&
            current.ConnectionIdentity == connectionIdentity)
        {
            visits[cursor] = current with { ProjectId = projectId, IsLocalDraft = isLocalDraft, IsLegacy = isLegacy, LocalState = localState };
            Revision++;
            return;
        }
        if (cursor < visits.Count - 1) visits.RemoveRange(cursor + 1, visits.Count - cursor - 1);
        visits.Add(new(++nextSequence, conversationId, projectId, connectionIdentity, isLocalDraft, isLegacy, localState));
        cursor = visits.Count - 1;
        while (visits.Count > capacity) { visits.RemoveAt(0); cursor--; }
        Revision++;
    }

    internal IReadOnlyList<Visit> Candidates(bool back)
    {
        var result = new List<Visit>();
        for (var index = cursor + (back ? -1 : 1); index >= 0 && index < visits.Count; index += back ? -1 : 1)
            result.Add(visits[index]);
        return result;
    }

    internal bool MoveTo(long sequence, int expectedRevision)
    {
        if (Revision != expectedRevision) return false;
        var index = visits.FindIndex(visit => visit.Sequence == sequence);
        if (index < 0) return false;
        cursor = index; Revision++;
        return true;
    }

    internal bool RemoveUnavailable(long sequence, int expectedRevision)
    {
        if (Revision != expectedRevision) return false;
        var index = visits.FindIndex(visit => visit.Sequence == sequence);
        if (index < 0 || index == cursor) return false;
        visits.RemoveAt(index);
        if (index < cursor) cursor--;
        Revision++;
        return true;
    }
}
