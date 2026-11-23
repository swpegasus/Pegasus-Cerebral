namespace Cerebral.Worker;

/// <summary>
/// Orders a synchronous model-selected action with user input for the same conversation. The callback must only
/// perform the short retained-state mutation that makes the action official; model, tool, and other asynchronous
/// work belongs after this boundary.
/// </summary>
internal static class AgentActionAdoption
{
    public static RevisionedActionAdoption<T> TryAdopt<T>(StateStore store, string sessionId, int expectedRevision,
        Func<T> mutation)
    {
        return store.LinearizedSession<RevisionedActionAdoption<T>>(sessionId,
            () => store.Locked<RevisionedActionAdoption<T>>(() =>
        {
            var session = store.Get<Cerebral.Core.TaskSession>("session", sessionId)
                ?? throw new ArgumentException("Session not found.");
            if (session.Revision != expectedRevision) return new RevisionedActionAdoption<T>(false, default!);
            return new RevisionedActionAdoption<T>(true, mutation());
        }));
    }
}

internal readonly record struct RevisionedActionAdoption<T>(bool RevisionMatched, T Value);
