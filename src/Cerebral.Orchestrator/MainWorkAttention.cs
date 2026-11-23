using Cerebral.Client;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

internal enum WorkAttentionKind { Completion, Question }

internal sealed record WorkAttentionTarget(long OwnerGeneration, string ConnectionIdentity,
    string MainConversationId, string ConversationId, string RunId, long RunRevision,
    WorkAttentionKind Kind, WorkflowStatus Status, string? TaskId = null,
    string? QuestionId = null, long? QuestionRevision = null, string? QuestionFieldId = null)
{
    internal bool IsWellFormed =>
        OwnerGeneration > 0 && Id(ConnectionIdentity, 1024) && Id(MainConversationId) &&
        MainConversationId == ConversationId && Id(RunId) && RunRevision > 0 &&
        (TaskId is null || Id(TaskId)) && Enum.IsDefined(Kind) && Enum.IsDefined(Status) &&
        (Kind == WorkAttentionKind.Question
            ? Id(QuestionId) && QuestionRevision > 0 && Id(QuestionFieldId)
            : QuestionId is null && QuestionRevision is null && QuestionFieldId is null &&
              Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled);

    internal static bool Id(string? value, int maximum = 128) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

internal sealed record WorkAttentionActivationResult(bool Navigated, string Message);

/// <summary>Only identifiers and recorded status cross the notification boundary.</summary>
internal sealed record WorkAttentionRunState(string ConversationId, string RunId, long Revision,
    WorkflowStatus Status, string? QuestionId, long? QuestionRevision, IReadOnlyList<string> UnansweredFields)
{
    internal static WorkAttentionRunState From(WorkflowConversationRun run) =>
        Create(run.ConversationId, run.Id, run.Revision, run.Status, run.Questions);

    internal static WorkAttentionRunState From(WorkflowRun run) =>
        Create(run.Submission.ConversationId, run.Id, run.Revision, run.Status, run.Questions);

    private static WorkAttentionRunState Create(string conversation, string run, long revision,
        WorkflowStatus status, WorkflowQuestionSet? questions) =>
        new(conversation, run, revision, status, questions?.Id, questions?.Revision,
            questions?.Fields.Where(field => !questions.Answers.ContainsKey(field.Id))
                .Select(field => field.Id).ToArray() ?? []);

    internal bool Terminal => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled;
}

/// <summary>
/// A bounded projection of the view Main already observes. It creates no reader, timer or work.
/// View hydration seeds the baseline; only later admitted transitions can request attention.
/// </summary>
internal sealed class MainWorkAttention
{
    internal const int MaximumSeen = 256;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly Dictionary<string, (long Revision, WorkflowStatus Status, string? QuestionId, long? QuestionRevision)> revisions = new(StringComparer.Ordinal);
    private long ownerGeneration, selection;
    private string? identity, main;
    private bool baseline;

    internal int SeenCount => seen.Count;

    internal void ResetView() => baseline = false;

    internal WorkAttentionTarget? Observe(long owner, string connectionIdentity, string mainId,
        long selectionVersion, WorkAttentionRunState? state, bool needsAttention,
        bool completionEligible = true, string? taskId = null)
    {
        if (ownerGeneration != owner || identity != connectionIdentity)
        {
            ownerGeneration = owner; identity = connectionIdentity;
            seen.Clear(); seenOrder.Clear(); revisions.Clear(); baseline = false;
        }
        if (main != mainId || selection != selectionVersion)
        {
            main = mainId; selection = selectionVersion;
            revisions.Clear(); baseline = false;
        }
        if (state is null) { baseline = true; return null; }
        if (!WorkAttentionTarget.Id(mainId) || !WorkAttentionTarget.Id(state.RunId) ||
            state.ConversationId != mainId || state.Revision < 1 || !Enum.IsDefined(state.Status))
            return null;
        var revisionState = (state.Revision, state.Status, state.QuestionId, state.QuestionRevision);
        if (revisions.TryGetValue(state.RunId, out var prior) &&
            (state.Revision < prior.Revision || state.Revision == prior.Revision && revisionState != prior)) return null;
        revisions[state.RunId] = revisionState;
        if (revisions.Count > 64) revisions.Remove(revisions.Keys.First(key => key != state.RunId));

        var question = !state.Terminal && state.QuestionId is not null && state.QuestionRevision > 0 &&
            state.UnansweredFields.Count > 0;
        var kind = question ? WorkAttentionKind.Question : WorkAttentionKind.Completion;
        var target = new WorkAttentionTarget(owner, connectionIdentity, mainId, state.ConversationId,
            state.RunId, state.Revision, kind, state.Status, taskId,
            question ? state.QuestionId : null, question ? state.QuestionRevision : null,
            question ? state.UnansweredFields[0] : null);
        var key = Key(target);
        if (!baseline)
        {
            baseline = true;
            if (question || state.Terminal) Remember(key);
            return null;
        }
        if ((!question && (!state.Terminal || !completionEligible)) || !target.IsWellFormed || !Remember(key))
            return null;
        // A transition already visible in the active Main view is consumed without a balloon.
        return needsAttention ? target : null;
    }

    private bool Remember(string key)
    {
        if (!seen.Add(key)) return false;
        seenOrder.Enqueue(key);
        while (seenOrder.Count > MaximumSeen) seen.Remove(seenOrder.Dequeue());
        return true;
    }

    private static string Key(WorkAttentionTarget target) =>
        System.Text.Json.JsonSerializer.Serialize(new object?[] { target.ConversationId, target.RunId,
            target.Kind, target.Kind == WorkAttentionKind.Question ? target.QuestionId : target.Status.ToString(),
            target.Kind == WorkAttentionKind.Question ? target.QuestionRevision : null });
}

internal static class WorkAttentionActivation
{
    internal static string? Refusal(WorkAttentionTarget target, WorkAttentionRunState? state)
    {
        if (!target.IsWellFormed) return "This notification has no valid exact target.";
        if (state is null) return "The original work is unavailable or was removed. No other work was opened.";
        if (state.ConversationId != target.ConversationId || state.RunId != target.RunId ||
            state.Revision < target.RunRevision)
            return "The original work identity or revision could not be confirmed. No other work was opened.";
        if (target.Kind == WorkAttentionKind.Question)
        {
            if (state.Terminal || state.QuestionId != target.QuestionId ||
                state.QuestionRevision != target.QuestionRevision ||
                !state.UnansweredFields.Contains(target.QuestionFieldId!, StringComparer.Ordinal))
                return "That question was resolved or changed. This notification will not select a different question.";
        }
        else if (!state.Terminal || state.Status != target.Status)
            return "That completion notice is stale. The original recorded completion could not be confirmed.";
        return null;
    }

    internal static async Task<WorkAttentionActivationResult> ActivateAsync(WorkAttentionTarget target,
        Func<bool> current, Func<WorkAttentionTarget, CancellationToken, Task<WorkAttentionRunState?>> read,
        Func<WorkAttentionTarget, CancellationToken, Task<bool>> navigate, CancellationToken token)
    {
        if (!target.IsWellFormed || !current())
            return new(false, "The notification belongs to an earlier connection or view. No other work was opened.");
        token.ThrowIfCancellationRequested();
        var state = await read(target, token);
        token.ThrowIfCancellationRequested();
        if (!current()) return new(false, "The connection or selected view changed while checking the notification.");
        if (Refusal(target, state) is { } refusal) return new(false, refusal);
        var opened = await navigate(target, token);
        return opened && current()
            ? new(true, target.Kind == WorkAttentionKind.Question ? "Opened the original question." : "Opened the original completed work.")
            : new(false, "The original work could not be selected safely. Current drafts were preserved.");
    }
}
