namespace Cerebral.Core;

public sealed class ActivityEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public string SessionId { get; set; } = "";
    public string ActorSessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string RootJobId { get; set; } = "";
    public int TurnSequence { get; set; }
    public string ProfileId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Arguments { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Truncated { get; set; }
}

public sealed class ActivityTurnSummary
{
    public int TurnSequence { get; set; }
    public int Count { get; set; }
    public ActivityEntry Latest { get; set; } = new();
}

public sealed class ActivityPage
{
    public List<ActivityEntry> Entries { get; set; } = [];
    public long? NextAfterSequence { get; set; }
    public int TotalCount { get; set; }
    /// <summary>Present after this conversation has rolled diagnostic rows; absence means no recorded pruning.</summary>
    public ActivityRetentionInfo? Retention { get; set; }
}

public sealed class ActivityRetentionInfo
{
    public string SessionId { get; set; } = "";
    public long PrunedEntries { get; set; }
    public long FirstPrunedSequence { get; set; }
    public long LastPrunedSequence { get; set; }
    public DateTimeOffset LastPrunedAt { get; set; }
    public int MaximumEntries { get; set; }
    public int MaximumDays { get; set; }
}
