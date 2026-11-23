using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>A durable boundary after which a selected tool is officially started and must be dispatched.</summary>
internal sealed record ToolDispatchReservation(int MessageSequence, int SessionRevision, bool PreparedLongFormAppend);

/// <summary>
/// Orders model-selected side effects with steering and question answers for the same conversation. The reservation
/// is deliberately short: it commits the exact action and any replay record, then releases both the session gate and
/// SQLite transaction before external execution begins.
/// </summary>
internal static class AgentExecutionGuard
{
    public static ToolDispatchReservation? Reserve(StateStore store, string sessionId, int expectedRevision,
        string action, string jobId, int step, string toolName, JsonElement arguments,
        LongFormObjective? appendObjective = null)
    {
        return store.LinearizedSession(sessionId, () => store.Locked(() =>
        {
            var session = store.GetSessionMetadata(sessionId) ?? throw new ArgumentException("Session not found.");
            if (session.Revision != expectedRevision) return null;
            var job = store.Get<AgentJob>("job", jobId) ?? throw new ArgumentException("Job not found.");
            if (job.SessionId != sessionId) throw new InvalidDataException("Tool action conversation identity changed.");
            if (job.CancelRequested) throw new OperationCanceledException("Task stopped before tool execution.");
            if (job.Status != "running") throw new InvalidOperationException("The assignment is no longer running.");

            var preparedAppend = false;
            if (appendObjective is not null)
            {
                var current = store.Get<LongFormState>("longform", sessionId);
                if (current is null || !LongFormWorkflow.Matches(current, appendObjective))
                    throw new InvalidOperationException("The long-form plan changed before the append could be reserved. Regenerate from current state.");
                if (!LongFormWorkflow.PreparePendingAppend(current, jobId, step, toolName, arguments)) return null;
                store.Put("longform", sessionId, current);
                preparedAppend = true;
            }

            var started = store.Append(sessionId, "assistant", action);
            return new ToolDispatchReservation(started.Sequence, started.Revision, preparedAppend);
        }));
    }
}
