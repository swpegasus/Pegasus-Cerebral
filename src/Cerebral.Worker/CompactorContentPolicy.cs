namespace Cerebral.Worker;

/// <summary>Generic checkpoint-content policy for compaction. A null target retains the proportional
/// history policy. An explicit positive target selects concise content independently of the complete
/// response allowance; accepted/live model budgets and context capacity still narrow both.</summary>
public sealed record CompactorContentPolicy(int? CheckpointTargetTokens = null);
