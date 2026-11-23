using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

// Presentation state only. The existing client owns validation, mutation identity and retries.
internal sealed class MainCompletionStatus
{
    internal const int MaximumConversations = 256;
    internal const int MaximumErrorCharacters = 512;
    internal sealed record Operation(object Owner, string ConversationId, string RunId, string RequestId,
        string Kind, long Ordinal, long? RunRevision, long? MessageSequence);
    internal sealed record Observation(string ConversationId, string RunId, long Revision, string Phase,
        bool Completed, bool Failed, bool Uncertain, bool UnresolvedEffect, bool Cancelled,
        string? Error, long? MessageSequence);
    internal sealed record Presentation(string Progress, string? Error, bool ClearedError = false)
    {
        internal string Caption => Error is null ? Progress : Progress + " · Previous error: " + Error;
    }
    private sealed record Failure(string RunId, string RequestId, string Kind, long Ordinal,
        long? Revision, long? MessageSequence, bool FreshUnconfirmedSubmission, long FreshAfterSequence, string Message);
    private sealed class Conversation
    {
        internal string Progress = "Ready for your message";
        internal string? TrackedRun;
        internal string? FreshRun;
        internal long FreshOrdinal;
        internal long FreshAfterSequence;
        internal string? ArmedRun;
        internal long ArmedOrdinal;
        internal long ArmedAfterSequence;
        internal string? ObservedRun;
        internal long? ObservedRevision;
        internal long? TrackedRevision;
        internal long? ObservedMessageSequence;
        internal long RecoveryReadOrdinal;
        internal Failure? Error;
        internal Operation? Active;
    }
    private readonly Dictionary<string, Conversation> conversations = new(StringComparer.Ordinal);
    private object? owner;
    private object? continuityScope;
    private long ordinal;

    internal void Bind(object currentOwner, object? verifiedContinuityScope = null)
    {
        ArgumentNullException.ThrowIfNull(currentOwner);
        if (ReferenceEquals(owner, currentOwner)) return;
        // Only the display error crosses a verified reconnect. Mutation/read authority and
        // progress belong to the old client, and its callbacks remain invalid after Bind.
        if (verifiedContinuityScope is not null && Equals(continuityScope, verifiedContinuityScope))
        {
            foreach (var id in conversations.Keys.ToArray())
            {
                var failure = conversations[id].Error;
                if (failure is null) conversations.Remove(id);
                else conversations[id] = new Conversation
                {
                    Error = failure,
                    Progress = "Reconnected · checking saved work"
                };
            }
        }
        else { conversations.Clear(); ordinal = 0; }
        owner = currentOwner; continuityScope = verifiedContinuityScope;
    }

    internal void ArmSubmission(object currentOwner, string conversationId, string runId, long afterSequence)
    {
        if (!Current(currentOwner) || !Identifier(runId) || afterSequence < 0 || Get(conversationId, true) is not { } state) return;
        if (state.TrackedRun != runId) state.TrackedRevision = null;
        state.Active = null; state.TrackedRun = runId; state.FreshRun = runId; state.FreshOrdinal = ++ordinal; state.FreshAfterSequence = afterSequence;
        state.ArmedRun = runId; state.ArmedOrdinal = ordinal; state.ArmedAfterSequence = afterSequence;
        state.Progress = "Preparing message";
    }

    internal Operation? Begin(object currentOwner, string conversationId, string runId, string requestId,
        string kind, long? revision, long? messageSequence)
    {
        if (!Current(currentOwner) || !Identifier(runId) || !Identifier(requestId) ||
            kind is not ("submit" or "answer" or "resume") || revision is < 1 ||
            Get(conversationId, true) is not { } state) return null;
        if (state.TrackedRun != runId) state.TrackedRevision = null;
        state.TrackedRun = runId; state.TrackedRevision = Later(state.TrackedRevision, revision);
        state.Progress = kind == "answer" ? "Answer received · submitting to engine" : kind == "resume" ? "Submitting resume request" : "Submitting message";
        var operation = new Operation(currentOwner, conversationId, runId, requestId, kind, ++ordinal, revision, messageSequence);
        state.Active = operation; return operation;
    }

    internal bool ReplyReceived(Operation operation)
    {
        if (!Current(operation.Owner) || Get(operation.ConversationId, false) is not { } state ||
            !ReferenceEquals(state.Active, operation) || operation.Ordinal < state.FreshOrdinal) return false;
        state.Progress = operation.Kind switch
        {
            "answer" => "Answer saved · checking workflow",
            "resume" => "Resume saved · checking workflow",
            _ => "Submission saved · checking workflow"
        };
        return true;
    }

    internal bool Fail(Operation operation, string message, bool uncertain, long? observedRevision = null,
        long? messageSequence = null)
    {
        if (!Current(operation.Owner) || Get(operation.ConversationId, false) is not { } state ||
            !ReferenceEquals(state.Active, operation) || operation.Ordinal < state.FreshOrdinal) return false;
        // Include the latest same-run observation: completion already seen during an await is not later recovery.
        var revision = Later(operation.RunRevision, observedRevision);
        if (state.ObservedRun == operation.RunId) revision = Later(revision, state.ObservedRevision);
        if (state.TrackedRun == operation.RunId) revision = Later(revision, state.TrackedRevision);
        var sequence = messageSequence ?? operation.MessageSequence ??
            (state.ObservedRun == operation.RunId ? state.ObservedMessageSequence : null);
        state.Error = new(operation.RunId, operation.RequestId, operation.Kind, ++ordinal, revision, sequence,
            operation.Kind == "submit" && state.FreshRun == operation.RunId && revision is null,
            state.FreshAfterSequence, Bounded(message));
        state.ArmedRun = null; state.ArmedOrdinal = 0; state.RecoveryReadOrdinal = 0; state.Active = null;
        state.Progress = uncertain ? "Outcome unconfirmed · refresh saved work" : "Request failed";
        return true;
    }

    internal long BeginRead(object currentOwner, string conversationId) =>
        Current(currentOwner) && Identifier(conversationId) ? ++ordinal : 0;

    internal bool ReadCurrent(object currentOwner, string conversationId, long readOrdinal)
    {
        if (!Current(currentOwner) || readOrdinal < 1) return false;
        var state = Get(conversationId, false);
        return state is null || readOrdinal >= state.FreshOrdinal &&
            (state.Active is null || readOrdinal >= state.Active.Ordinal);
    }

    internal void ConfirmRecoveryRead(object currentOwner, string conversationId, string runId, long readOrdinal)
    {
        if (!Current(currentOwner) || Get(conversationId, false) is not { Error: { } failure } state ||
            failure.RunId != runId || !failure.FreshUnconfirmedSubmission || readOrdinal <= failure.Ordinal) return;
        // A successful exact get only arms observation; it never clears an error or replays a mutation.
        state.RecoveryReadOrdinal = readOrdinal;
    }

    internal Presentation Observe(object currentOwner, Observation observation)
    {
        if (!Current(currentOwner) || !Identifier(observation.RunId) || observation.Revision < 1)
            return new("Completion status unavailable", null);
        var state = Get(observation.ConversationId, true);
        if (state is null) return CapacityUnavailable();
        if (state.TrackedRun == observation.RunId && state.TrackedRevision > observation.Revision)
            return Present(state);
        if (state.TrackedRun == observation.RunId) state.TrackedRevision = Later(state.TrackedRevision, observation.Revision);
        if (state.ObservedRun == observation.RunId && state.ObservedRevision > observation.Revision)
            return Present(state);
        state.ObservedRun = observation.RunId; state.ObservedRevision = observation.Revision;
        state.ObservedMessageSequence = observation.MessageSequence;
        if (observation.Uncertain && state.Active is { } active && active.RunId == observation.RunId)
            Fail(active, "The saved request outcome is unconfirmed. Refresh before retrying.", true, observation.Revision, observation.MessageSequence);
        state.Progress = observation.Uncertain ? "Outcome unconfirmed · refresh saved work" : Bounded(observation.Phase);
        var cleared = false;
        if (state.Error is { } failure && observation.Completed && !observation.Uncertain &&
            !observation.UnresolvedEffect && !observation.Cancelled && string.IsNullOrWhiteSpace(observation.Error))
        {
            var sameRun = observation.RunId == failure.RunId &&
                (failure.Revision is { } previous
                    ? observation.Revision > previous
                    : failure.FreshUnconfirmedSubmission && state.RecoveryReadOrdinal > failure.Ordinal &&
                      observation.MessageSequence is { } recovered && recovered > failure.FreshAfterSequence);
            var laterSubmission = observation.RunId != failure.RunId && state.ArmedRun == observation.RunId &&
                state.ArmedOrdinal > failure.Ordinal && observation.MessageSequence is { } next &&
                next > state.ArmedAfterSequence && (failure.MessageSequence is null || next > failure.MessageSequence);
            if (sameRun || laterSubmission) { state.Error = null; cleared = true; }
        }
        if (observation.Failed && state.TrackedRun == observation.RunId &&
            (state.Error is null || state.Error.RunId != observation.RunId ||
             state.Error.Revision is null || state.Error.Revision < observation.Revision))
        {
            state.Error = new(observation.RunId, observation.RunId, "run", ++ordinal, observation.Revision,
                observation.MessageSequence, false, 0, Bounded(observation.Error ?? "The workflow failed."));
            state.ArmedRun = null; state.ArmedOrdinal = 0;
        }
        return Present(state) with { ClearedError = cleared };
    }

    internal Presentation Current(object currentOwner, string conversationId, string? progress = null)
    {
        if (!Current(currentOwner)) return new("Completion status unavailable", null);
        var state = Get(conversationId, false);
        return state is null ? conversations.Count >= MaximumConversations ? CapacityUnavailable()
            : new(progress ?? "Ready for your message", null)
            : new(progress ?? state.Progress, state.Error?.Message);
    }

    private bool Current(object currentOwner) => ReferenceEquals(owner, currentOwner);
    private Conversation? Get(string id, bool create)
    {
        if (!Identifier(id)) return null;
        if (conversations.TryGetValue(id, out var state)) return state;
        if (!create || conversations.Count >= MaximumConversations) return null;
        state = new(); conversations.Add(id, state); return state;
    }
    internal static string RunPhase(WorkflowStatus status, bool hasQuestions) => status switch
    {
        WorkflowStatus.Queued => "Queued for engine",
        WorkflowStatus.Running => "Engine running · completion pending",
        WorkflowStatus.Waiting when hasQuestions => "Waiting for your answer",
        WorkflowStatus.Waiting => "Waiting",
        WorkflowStatus.Cancelling => "Cancelling",
        WorkflowStatus.Completed => "Completed · ready for your next message",
        WorkflowStatus.Failed => "Failed",
        WorkflowStatus.Cancelled => "Cancelled",
        _ => "Completion status unavailable"
    };

    private static Presentation Present(Conversation state) => new(state.Progress, state.Error?.Message);
    private static Presentation CapacityUnavailable() => new("Completion status unavailable · display state limit reached", null);
    private static long? Later(long? first, long? second) => first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 && !value.Any(char.IsControl);
    internal static string Bounded(string value)
    {
        var text = new string(value.Where(character => !char.IsControl(character)).Take(MaximumErrorCharacters).ToArray()).Trim();
        return text.Length == 0 ? "The request did not complete successfully." : text;
    }
}
