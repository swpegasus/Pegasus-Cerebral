namespace Cerebral.Contracts.Workflows;

/// <summary>Owner input for the next policy boundary. A receipt confirms durable acceptance, not execution.
/// Retry an identical request ID within the same run after an uncertain acknowledgement.</summary>
public sealed record WorkflowSteeringRequest(string RequestId, string RunId, string Text, string? AssignmentText = null);
public sealed record WorkflowSteeringEntry(string RequestId, int Sequence, string Text, string? AssignmentText, DateTimeOffset AcceptedAt);
public sealed record WorkflowSteeringReceipt(string RequestId, string RunId, int Sequence, DateTimeOffset AcceptedAt);
