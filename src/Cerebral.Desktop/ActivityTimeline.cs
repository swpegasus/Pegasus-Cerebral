namespace Cerebral.Desktop;

public enum ActivityTimelineRole { Unspecified, Main, Coordinator, Worker }
public enum ActivityOperationKind { Unclassified, Internal, Handoff, ExecutionTool }

/// <summary>A host-supplied context label, never the recorded actor or authority to execute.</summary>
public sealed record ActivityContextAttribution(string OwnerIdentity, string OwningMainConversationId,
    string MemberCoordinate, string ConversationId, string RunId, ActivityTimelineRole Role)
{
    public string? DisplayLabel { get; init; }
    public string Provenance => "Owner-bound context roster; display label is not recorded caller identity.";

    internal bool Matches(string owner, string? main, string? member, string conversation, string run,
        ActivityTimelineRole role) =>
        new[] { OwnerIdentity, OwningMainConversationId, MemberCoordinate, ConversationId, RunId }
            .All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl)) &&
        OwnerIdentity == owner && OwningMainConversationId == main && MemberCoordinate == member &&
        ConversationId == conversation && RunId == run && Role == role &&
        Role is ActivityTimelineRole.Main or ActivityTimelineRole.Coordinator or ActivityTimelineRole.Worker &&
        (Role != ActivityTimelineRole.Main || ConversationId == OwningMainConversationId && MemberCoordinate == OwningMainConversationId) &&
        (DisplayLabel is null || DisplayLabel.Length <= 512 && !DisplayLabel.Any(char.IsControl));

    internal string Caption => Role + (string.IsNullOrWhiteSpace(DisplayLabel) ||
        DisplayLabel.Trim().Equals(Role.ToString(), StringComparison.OrdinalIgnoreCase)
            ? "" : " · " + DisplayLabel.Trim()) + " (context)";
}

/// <summary>An exact journal turn interval, not a request to select or mutate its conversation.</summary>
public sealed record ActivityTurnTarget(string ConversationId, string RunId)
{
    public long? FirstSequence { get; init; }
    public long? LastSequence { get; init; }
}

public sealed record ActivityTurnFocus(string OwnerIdentity, string ConversationId, string RunId)
{
    public long? FirstSequence { get; init; }
    public long? LastSequence { get; init; }
}

/// <summary>Host-supplied correlation, never an authorization or a chat selection.</summary>
public sealed record ActivityTimelineContext(string OwnerIdentity,
    ActivityTimelineRole Role = ActivityTimelineRole.Unspecified, string? TaskId = null, string? TaskTitle = null)
{
    public string? OwningMainConversationId { get; init; }
    public string? MemberCoordinate { get; init; }
    // Both additions are opt-in; old callers retain their original display and classification.
    public ActivityContextAttribution? ContextAttribution { get; init; }
    public bool ClassifyOperations { get; init; }
}

/// <summary>Already redacted public evidence; the timeline never fetches or executes an operation.</summary>
public sealed record ActivityTimelineItem(string Key, string OwnerIdentity, string ConversationId,
    string RunId, string? TaskId, ActivityTimelineRole Role, string Agent, string Action, string Tool,
    string State, string RequestedAction, string Outcome, string Evidence,
    IReadOnlyList<string> EventIds, long Sequence, DateTimeOffset At,
    string OperationType = "Operation not recorded", string FileName = "—", string FilePath = "—",
    string Runtime = "Not recorded", string Route = "Not recorded", string TargetRole = "No file target",
    string Correlation = "Recorded event", string? TaskTitle = null)
{
    // Display ownership is separate from the immutable source conversation/run.
    // A linked member is not evidence that its underlying storage may be deleted.
    public string? OwningMainConversationId { get; init; }
    public string? MemberCoordinate { get; init; }
    public long FirstSequence { get; init; }
    public ActivityContextAttribution? ContextAttribution { get; init; }
    public ActivityOperationKind? OperationKind { get; init; }
    private ActivityContextAttribution? DisplayContext => ContextAttribution is { } context &&
        context.Matches(OwnerIdentity, OwningMainConversationId, MemberCoordinate, ConversationId, RunId, Role)
            ? context : null;
    public string AgentDisplay => DisplayContext?.Caption ?? Agent;
    public string AgentDisplayTooltip => DisplayContext is { } context
        ? $"{context.Caption}\nRecorded actor: {Agent}\n{context.Provenance}\n" +
          $"Owner: {context.OwnerIdentity}\nMain: {context.OwningMainConversationId}\n" +
          $"Member: {context.MemberCoordinate}\nChat: {context.ConversationId}\nRun: {context.RunId}"
        : Agent;
    public string OperationDisplay => OperationKind is { } kind
        ? (kind == ActivityOperationKind.ExecutionTool ? "Execution tool" : kind.ToString()) + " · " +
          (kind == ActivityOperationKind.Handoff ? Tool switch
          {
              "cerebral.host:coordinate-owned" => "Coordinate work",
              "cerebral.host:delegate" => "Delegate workers",
              _ => OperationType
          } : OperationType)
        : OperationType;
    public string Caption => $"{Agent} | {Action} | [{Tool}] | {State}";
    public string TimeState => $"{At.ToLocalTime():HH:mm:ss} · {State}";
    public string ActorContext => $"{Agent} · {Role} · " + (TaskTitle is { Length: > 0 } title ? title :
        TaskId is { Length: > 0 } task ? "task " + ShortId(task) : "chat " + ShortId(ConversationId));
    public string ActorContextTooltip => $"{Agent} · {Role}\nTask: {TaskId ?? "Not recorded"}\nChat: {ConversationId}\nRun: {RunId}";
    public string RowText => string.Join("\t", TimeState, Agent, OperationType, Tool, FileName, FilePath);
    private static string ShortId(string id) => id.Length <= 16 ? id : id[..8] + "…" + id[^4..];
}

public sealed record ActivityTimelineMember(string OwningMainConversationId, string Coordinate, string Label);

public sealed record ActivityTimelineSnapshot(IReadOnlyList<ActivityTimelineItem> Items,
    bool HasEarlier, int OmittedCount = 0, string? CoverageNote = null,
    int? TotalCount = null, long? PrunedCount = null)
{
    public const int MaximumItems = 1000;
    public IReadOnlyList<ActivityTimelineMember> Members { get; init; } = [];
}

public sealed record ActivityOperationSummary(string Action, string Tool,
    string OperationType = "Operation not recorded", string FileName = "—", string FilePath = "—",
    string Runtime = "Not recorded", string Route = "Not recorded", string TargetRole = "No file target");
