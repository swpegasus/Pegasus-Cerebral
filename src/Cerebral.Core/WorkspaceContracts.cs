namespace Cerebral.Core;

public sealed class ExecutionEvidence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "tool";
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string Summary { get; set; } = "";
    public string? Command { get; set; }
    public int MessageSequence { get; set; }
    public string? SourceSessionId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? RequestId { get; set; }
    public string? Resolution { get; set; }
}
public sealed class TaskFailure
{
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "open";
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class NavigationState
{
    public int Revision { get; set; }
    public List<NavigationNode> Nodes { get; set; } = [];
}
public sealed class NavigationNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "folder";
    public string Title { get; set; } = "";
    public string? ParentId { get; set; }
    public string? TargetId { get; set; }
    public string? Path { get; set; }
    /// <summary>The project path is reserved in navigation but is not created until the first file operation needs it.</summary>
    public bool CreateWhenUsed { get; set; }
    public bool Pinned { get; set; }
    public int Order { get; set; }
}
public sealed record ApplicationObservation(string RequestId, string ClaimId, int ProcessId,
    DateTimeOffset ProcessStartedAt, string Status, int? ExitCode = null, string? Output = null);
