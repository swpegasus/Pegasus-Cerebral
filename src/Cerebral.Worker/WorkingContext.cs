using System.Text;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed record WorkingContextFit(List<ModelMessage> Messages, int PromptTokens, int OutputTokens,
    int StoredCheckpointCharacters, int ProjectedCheckpointCharacters)
{
    public bool CheckpointProjected => ProjectedCheckpointCharacters < StoredCheckpointCharacters;
}

public static class WorkingContext
{
    public const int MaxToolContextBytes = 8 * 1024;
    public static int CheckpointWordTarget(int ownerContextTokens) => Math.Clamp(ownerContextTokens / 24, 120, 400);
    public static bool IsUserRequest(SessionMessage message) => message.Role == "user" && message.Origin == "user" && message.ToolName is null;
    public static string WithProvenance(SessionMessage message, bool bounded = false) =>
        $"Source message {message.Sequence}; role={message.Role}; origin={message.Origin}; tool={message.ToolName ?? "none"}:\n" +
        (bounded ? ExecutionReliability.Diagnostic(message.Content, 2000) : ModelContent(message));

    private static string ModelContent(SessionMessage message)
    {
        var sourceBytes = Encoding.UTF8.GetByteCount(message.Content);
        if (IsUserRequest(message) || (message.ToolName is null && message.Origin is not ("helper" or "service")) ||
            sourceBytes <= MaxToolContextBytes) return message.Content;
        // Keep complete tool/helper/service records in the session/UI. Only their model projection is
        // shortened, using the same excerpt for ordinary generation and compaction.
        var kind = message.ToolName is not null ? "tool response" : message.Origin == "helper" ? "helper result" : "service record";
        var prefix = $"Model-context excerpt of saved {kind}; source message {message.Sequence}; origin={ExecutionReliability.Clip(message.Origin, 200)}; tool={ExecutionReliability.Clip(message.ToolName ?? "none", 200)}; originalUtf8Bytes={sourceBytes}. " +
            "The full saved response is unchanged. The text/JSON below is incomplete and is not an exact read of the entire record.\n[Begin excerpt]\n";
        const string suffix = "\n[End excerpt; remaining saved text omitted.]";
        var excerpt = CortexContext.BoundUtf8(message.Content, MaxToolContextBytes - Encoding.UTF8.GetByteCount(prefix + suffix));
        return prefix + excerpt + suffix;
    }

    public static List<ModelMessage> Assemble(TaskSession session, string system, int startSequence = 0) =>
        Assemble(session, system, startSequence, null);

    internal static List<ModelMessage> Assemble(TaskSession session, string system, int startSequence,
        JobAssignmentContext? assignment) =>
        AssembleWithCheckpoint(session, system, startSequence, session.Checkpoint ?? "", assignment);

    public static List<ModelMessage> AssembleWithCheckpoint(TaskSession session, string system, int startSequence, string checkpoint)
        => AssembleWithCheckpoint(session, system, startSequence, checkpoint, null);

    internal static List<ModelMessage> AssembleWithCheckpoint(TaskSession session, string system, int startSequence,
        string checkpoint, JobAssignmentContext? assignment) =>
        AssembleProjection(session, system, startSequence, checkpoint, session.CheckpointThroughSequence, assignment);

    private static List<ModelMessage> AssembleProjection(TaskSession session, string system, int startSequence, string checkpoint,
        int checkpointThroughSequence, JobAssignmentContext? assignment)
    {
        var result = new List<ModelMessage> { new("system", system) };
        if (assignment is not null) result.Add(assignment.ToModelMessage());
        var included = session.Messages.Where(x => !x.ExcludedFromContext).ToList();
        if (checkpointThroughSequence > 0 || !string.IsNullOrEmpty(checkpoint))
        {
            if (!string.IsNullOrEmpty(checkpoint))
                result.Add(new("user", "Generated context checkpoint (reference data, may contain mistaken interpretations; verify diagnoses against the preserved source evidence):\n" + checkpoint));
            var anchors = included.Where(x => IsUserRequest(x) && x.Sequence == startSequence).ToList();
            if (included.LastOrDefault(IsUserRequest) is { } latest && !anchors.Any(x => x.Sequence == latest.Sequence)) anchors.Add(latest);
            foreach (var anchor in anchors.Where(x => x.Sequence <= checkpointThroughSequence))
                result.Add(new("user", "Service-preserved user request, verbatim.\n" + WithProvenance(anchor)));
            var decisive = included.Where(x => x.Sequence >= startSequence && x.Sequence <= checkpointThroughSequence &&
                x.ToolName is not null && (x.ToolName == "process.run" || x.Content.Contains("success=False", StringComparison.Ordinal))).TakeLast(4);
            foreach (var evidence in decisive)
                result.Add(new("user", "Service-preserved tool evidence; bounded excerpt of the original record.\n" + WithProvenance(evidence, bounded: true)));
        }
        result.AddRange(included.Where(x => x.Sequence > checkpointThroughSequence)
            .Select(x => new ModelMessage(x.Role == "assistant" ? "assistant" : "user", ModelContent(x))));
        return result;
    }

    /// <summary>
    /// Projects only the generated checkpoint to the room left after required guidance, exact request anchors,
    /// current messages, the safety reserve, and the selected response allowance. The stored checkpoint is unchanged.
    /// </summary>
    public static async Task<WorkingContextFit> FitCheckpointAsync(TaskSession session, string system, int startSequence,
        string? knowledge, int contextTokens, int responseTokens, string reasoningMode, int reasoningTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
        => await FitCheckpointAsync(session, system, startSequence, knowledge, null, contextTokens, responseTokens,
            reasoningMode, reasoningTokens, countTokens, token);

    internal static async Task<WorkingContextFit> FitCheckpointAsync(TaskSession session, string system, int startSequence,
        string? knowledge, JobAssignmentContext? assignment, int contextTokens, int responseTokens, string reasoningMode,
        int reasoningTokens, Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
        => await FitProjectionAsync(session, system, startSequence, knowledge, session.CheckpointThroughSequence,
            session.Checkpoint ?? "", assignment, contextTokens, responseTokens, reasoningMode, reasoningTokens, countTokens, token);

    /// <summary>
    /// Measures the smallest prompt a normal model step could receive after one compaction pass. This does not
    /// generate or adopt a checkpoint, so the caller can avoid a costly compaction that cannot improve the fit.
    /// </summary>
    public static async Task<WorkingContextFit?> FitAfterCompactionFloorAsync(TaskSession session, string system, int startSequence,
        string? knowledge, int contextTokens, int responseTokens, string reasoningMode, int reasoningTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
        => await FitAfterCompactionFloorAsync(session, system, startSequence, knowledge, null, contextTokens, responseTokens,
            reasoningMode, reasoningTokens, countTokens, token);

    internal static async Task<WorkingContextFit?> FitAfterCompactionFloorAsync(TaskSession session, string system, int startSequence,
        string? knowledge, JobAssignmentContext? assignment, int contextTokens, int responseTokens, string reasoningMode,
        int reasoningTokens, Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
    {
        var active = session.Messages.Where(x => !x.ExcludedFromContext && x.Sequence > session.CheckpointThroughSequence).ToList();
        var compact = active.Take(Math.Max(0, active.Count - 2)).ToList();
        if (compact.Count == 0) return null;
        return await FitProjectionAsync(session, system, startSequence, knowledge, compact[^1].Sequence, "",
            assignment, contextTokens, responseTokens, reasoningMode, reasoningTokens, countTokens, token);
    }

    private static async Task<WorkingContextFit> FitProjectionAsync(TaskSession session, string system, int startSequence,
        string? knowledge, int checkpointThroughSequence, string stored, JobAssignmentContext? assignment, int contextTokens, int responseTokens,
        string reasoningMode, int reasoningTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
    {
        async Task<WorkingContextFit> Evaluate(string checkpoint)
        {
            var messages = AssembleProjection(session, system, startSequence, checkpoint, checkpointThroughSequence, assignment);
            if (!string.IsNullOrWhiteSpace(knowledge)) messages.Insert(1, new("user", knowledge));
            var promptTokens = await countTokens(messages, token);
            var outputTokens = AgentPrompt.OutputTokenLimit(contextTokens, promptTokens, responseTokens, reasoningMode, reasoningTokens);
            return new(messages, promptTokens, outputTokens, stored.Length, checkpoint.Length);
        }

        var full = await Evaluate(stored);
        if (stored.Length == 0 || full.OutputTokens == responseTokens) return full;

        // No checkpoint body is the lower bound. Request anchors, decisive evidence, and current messages still remain,
        // but a dependent phrase such as "write that story" may need facts from the checkpoint. Try the configured
        // response share first, then a useful multi-step action reserve, then the reliable structured-action minimum;
        // at each level retain the largest checkpoint excerpt that fits.
        var empty = await Evaluate("");
        var balancedActionTokens = Math.Min(responseTokens,
            Math.Max(AgentPrompt.MinimumActionTokens, Math.Min(512, responseTokens / 2)));
        foreach (var requiredOutput in new[] { responseTokens, balancedActionTokens, AgentPrompt.MinimumActionTokens }.Distinct())
        {
            if (empty.OutputTokens < requiredOutput) continue;
            WorkingContextFit? best = null;
            var low = 1;
            var high = stored.Length;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = await Evaluate(ProjectCheckpoint(stored, middle));
                if (candidate.OutputTokens >= requiredOutput)
                {
                    best = candidate;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            if (best is not null) return best;
        }
        return empty;
    }

    internal static List<ModelMessage> CurrentTurnMessages(TaskSession session, int startSequence,
        JobAssignmentContext? assignment)
    {
        var current = new TaskSession
        {
            Messages = session.Messages.Where(x => !x.ExcludedFromContext && x.Sequence >= startSequence).ToList()
        };
        return Assemble(current, "", startSequence, assignment);
    }

    public static string ProjectCheckpoint(string checkpoint, int maximumCharacters)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (checkpoint.Length <= maximumCharacters) return checkpoint;
        if (maximumCharacters == 0) return "";
        const string marker = "\n[Checkpoint middle omitted to fit the active model context.]\n";
        if (maximumCharacters <= marker.Length + 2) return ExecutionReliability.Clip(checkpoint, maximumCharacters);
        var content = maximumCharacters - marker.Length;
        var head = content * 2 / 3;
        var tail = content - head;
        return checkpoint[..head] + marker + checkpoint[^tail..];
    }
}
