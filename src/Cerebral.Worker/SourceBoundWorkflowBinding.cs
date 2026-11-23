using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

// Private signature candidate for a WorkerExecutionSnapshot.Snapshot member.
// Capture occurs only in a separately admitted internal source-bound Service
// submission. Ordinary owner, agent and service SubmitWorkflowRequest paths
// cannot set this value. The complete SettingsSnapshot remains in existing
// WorkflowStore RID equality; no new WorkflowSubmission field or table is used.
internal sealed record SourceBoundWorkflowBinding(
    int SchemaVersion, string WorkRequestId, string WorkConversationId,
    SourceBoundWorkflowSourceFacts Source);

// Required optional member in WorkerExecutionSnapshot.Snapshot:
// [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
// internal SourceBoundWorkflowBinding? SourceBoundBackground { get; init; }
// Read validates its exact closed shape, bounds, source/work coherence, and
// package/profile membership. The public Input.Source is reconstructed from
// this marker and must compare equal before every host effect; public JSON is
// never accepted as authority. Editions 2-5 reject this field. Existing edition
// 6 snapshots without it remain valid; edition 6 plus this field requires the
// new reader, which fails closed on older executable versions.
