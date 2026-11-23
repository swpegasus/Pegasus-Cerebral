using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Contracts.Context;

/// <summary>Opt-in receipt projection protocol. These declarations alone do not add host support.
/// Admission/collection schema 1 is unchanged. A contribution using this facet MUST advertise
/// base WorkflowContextContributionDescriptor.SchemaVersion 2; older hosts must reject it rather
/// than silently erase the facet. A supporting host requires the facet on that SAME admitted object.</summary>
public static class WorkflowToolContextProjectionContract
{
    public const int SchemaVersion = 1;
    public const int RequiredContributionDescriptorSchemaVersion = 2;
    public const int MaximumSlots = 1;
    public const int MaximumToolNames = 64;
    public const int MaximumPendingContributions = WorkflowContextContributionContract.MaximumContributions;
    public const int MaximumProviderStateUtf8Bytes = WorkflowContextContributionContract.MaximumPayloadUtf8Bytes;
    public const int MaximumCommandRequestUtf8Bytes = 16 * 1024;
    public const int MaximumPublicationUtf8Bytes = 32 * 1024;
    public const int MaximumCommandResultUtf8Bytes = 64 * 1024;
    public const int MaximumCallbackInputUtf8Bytes = 320 * 1024;
    public const int MaximumReasonCharacters = 1024;

    // Bounds apply to complete serialized values, including escaping, metadata and handles.
    // Check raw and reserialized callback/durable result envelopes against the result cap.
    // Publication also obeys the admitted provider/template byte limit. Existing 128 KiB workflow
    // state/arguments and context admission/Fits limits still apply; none of these caps enlarge them.
    // Callback input is transient: the durable request carries only an anchor, not copied receipts.
    // Reject overflow explicitly without clipping, demoting Required sources or claiming completeness.
}

/// <summary>Separate, immutable v2 publication policy, never a mutation of the v1 Restore request.
/// ToolNames contains distinct ordinal exact semantic tool names, not prefixes, aliases or package claims.
/// PublicationTemplate names the SAME admitted provider/version and exactly one fixed own reference;
/// its SourceId, kind, requirement, priority and placement are fixed at admission. ExpectedRevision
/// is null in the template. Only the host derives current context/exclusion and next source revision.
/// Admission does not require an active publication; StateOnly preserves absence and first Publish
/// inserts revision 1. A Ready v2 selection still retains its base Request for freeze/Restore
/// compatibility. The receipt-aware edition separately admits/schedules this facet; it never sends
/// its base Request or PublicationTemplate through v1 once-only initial provider attempts. On a
/// supporting host, an unsupported consumer may retain an explicitly Omitted v2 selection with no
/// request/payload/digest and exclude it from its execution view. Every non-Omitted v2 selection
/// requires the receipt-aware edition; an applicable Ready v2 requirement must never be skipped.
/// Reuse the admitted provider's existing timeout, grants and limits; this is no new execution lane.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionDescriptor(
    int SchemaVersion,
    IReadOnlyList<string> ToolNames,
    ContextProviderRequest PublicationTemplate);

/// <summary>Optional facet on the same package-owned contribution instance, not a second provider.
/// The callback is deterministic and store-free for the frozen request. It has no model, tool,
/// package-write, store or host-service handles. Cancellation and package retirement are respected;
/// a late result cannot authorize adoption. Provider-specific observation/supersession policy stays
/// in the package. Reconciliation may recompute only this same pure projection, never the source tool.</summary>
public interface IWorkflowToolContextProjectionContribution : IWorkflowContextContribution
{
    WorkflowToolContextProjectionDescriptor ProjectionDescriptor { get; }

    Task<WorkflowToolContextProjectionResolution> ProjectAsync(
        WorkflowToolContextProjectionRequest request, CancellationToken cancellationToken);
}

/// <summary>Zero is invalid, so a missing disposition cannot become a successful state-only result.
/// Wire readers require a declared string value (stateOnly, publish or blocked), never an integer.</summary>
public enum WorkflowToolContextProjectionDisposition { Unspecified, StateOnly, Publish, Blocked }

/// <summary>Host ledger expectation for the single admitted own slot. Null revision means no active
/// publication; it does not authorize replacement of an existing source. A nonnull revision must
/// match the ledger-owned source exactly. Publication uses checked current + 1, starting at 1.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextSlotExpectation(string Id, long? CurrentSourceRevision);

/// <summary>Exact source revision adopted on Publish. No clear, deletion, demotion or foreign-slot
/// operation exists. This update is committed atomically with provider state and the projection anchor.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextSlotUpdate(string Id, long SourceRevision);

/// <summary>Ordinary host-command arguments authored by the accepted workflow, never model input.
/// Run/owner/submission/package identity derive from WorkflowCommandContext and accepted admission.
/// Resolve ToolEffectId only within that run; require an actual committed tool effect, a declared
/// semantic name, and a known terminal result. Reject self/projection/non-tool/foreign/Unknown inputs.
/// PreviousProjectionEffectId and Slot must match the separate own-slot ledger; no v1 attempt reset.
/// PriorState must equal the exact ledger state, including null, before invocation; it is bounded
/// opaque package data and grants no authority. The echoed link alone is not proof of state equality.
/// Newly undispatched requests may rebase context/exclusion after steering under the consumer contract.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionCommandRequest(
    int SchemaVersion,
    string ContributionId,
    string ToolEffectId,
    string? PreviousProjectionEffectId,
    WorkflowToolContextSlotExpectation Slot,
    JsonElement? PriorState,
    long ContextRevision,
    long ExclusionRevision);

/// <summary>Host-authored value of an exact same-run committed effect, from its saved intent/result.
/// This is NOT a lossless raw ToolResult and is NOT reconstructed from a model-facing ToolData view.
/// Arguments is the COMPLETE saved WorkflowEffect.Arguments, including the tool wrapper; Data is
/// saved WorkflowCommandResult.Data / WorkflowEffect.Result. Preserve these values and state/error/
/// provider receipt without inferring omitted raw fields. ConversationCheckpoint and the mutable
/// current run revision are deliberately excluded.
///
/// Digest schema 1: write a compact UTF-8 JSON object with Utf8JsonWriter's default encoder, validation
/// enabled, no BOM or trailing newline, and these exact property names/order, including null fields:
/// schemaVersion, runId, effectId, commandId, commandVersion, arguments, state, data, error,
/// providerReceiptId. Write integer values as JSON integers, strings with WriteStringValue, nullable
/// fields as JSON null when absent, and Arguments/Data with JsonElement.WriteTo without reordering
/// properties or reparsing numbers. State is exactly "Succeeded", "Failed" or "Cancelled".
/// SavedEffectSha256 is lowercase 64-character SHA-256 of those bytes. It is a defined host-serialized
/// saved-effect digest, not a hash of database bytes, a full WorkflowCommandResult or raw ToolResult.
/// Execute/Reconcile use the same codec and saved values; nested ordering/representation is not
/// semantically canonicalized. Retain the old codec or version it when encoding semantics change.
/// The host computes it after exact receipt resolution; callers never fabricate it from a tool view.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextSavedEffect(
    int SchemaVersion,
    string RunId,
    string EffectId,
    string CommandId,
    int CommandVersion,
    JsonElement Arguments,
    WorkflowEffectState State,
    JsonElement? Data,
    string? Error,
    string? ProviderReceiptId);

/// <summary>Exact input linkage, authored by the host and echoed unchanged by the package/result.
/// ProjectionEffectId is this ordinary host command; ToolEffectId identifies the prior physical tool.
/// The saved-effect digest is unrelated to any package-private selection/state codec digest.
/// Package generation/contribution ownership is proved by accepted admission, not inferred from names.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionLink(
    int SchemaVersion,
    string ContributionId,
    string RunId,
    string ProjectionEffectId,
    string ToolEffectId,
    string SavedEffectSha256,
    string? PreviousProjectionEffectId,
    WorkflowToolContextSlotExpectation Slot);

/// <summary>Pure callback input. Facts/Selection are the exact accepted values; the host rechecks
/// current package/grants and freezes all JSON/collections before invocation. ToolName is extracted
/// and verified from the exact saved intent, never accepted as a provider/model identity claim.
/// PublicationRequest derives only from the admitted template plus the host's checked next slot
/// revision and current context/exclusion revisions. It confers no new retrieval authority.
/// The complete callback input has its own transient bound, including prior state and metadata;
/// oversize receipts are rejected intact before invocation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionRequest(
    int SchemaVersion,
    WorkflowContextAcceptedFacts Facts,
    WorkflowContextSelection Selection,
    WorkflowToolContextProjectionLink Link,
    string ToolName,
    WorkflowToolContextSavedEffect SavedEffect,
    JsonElement? PriorState,
    ContextProviderRequest PublicationRequest);

/// <summary>Raw package projection, never a host-normalized result or an exact-read handle.
/// StateOnly: Resolution null, Reason null; adopt NextState while preserving the existing source
/// byte-for-byte, including Required classification, revision, content and handle, or preserves absence.
/// Publish: nonnull Resolution containing exactly the admitted requested own slot; Reason null.
/// NextState may be null on either success, meaning next-null opaque state, never source deletion.
/// Blocked: Resolution and NextState null, nonblank bounded Reason; adopt neither state nor source.
/// Null/empty sources never clear a slot. Source mutations can update opaque state while leaving the
/// prior actual observations visible as historical evidence; only package policy can supersede them.
/// The host validates linkage/disposition/bounds and normalizes Publish with the admitted provider,
/// exact derived request and this projection effect's receipt before any consumer adoption.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionResolution(
    int SchemaVersion,
    WorkflowToolContextProjectionLink Link,
    WorkflowToolContextProjectionDisposition Disposition,
    JsonElement? NextState,
    ContextProviderResolution? Resolution,
    string? Reason);

/// <summary>Closed durable host output. ProviderResult and SlotUpdate are present only on Publish,
/// with the exact single own-slot ID/checked next revision; all other outcomes require both null.
/// Normalized window context/exclusion revisions match the command arguments. Opaque state never
/// enters model/system context. StateOnly preserves context and may update opaque state.
/// Blocked requires null NextState and a
/// bounded reason; it is not successful uptake and cannot resume dependent preparation as success.
/// The consumer verifies exact current intent/link/normalization proof before atomic adoption.
/// Initial insertion must not collide with task, foreign, checkpoint or initial contribution sources;
/// replacement targets only the exact ledger-owned slot, never an owner/category-wide source set.
/// ReferenceOnly history must cover the whole command/result envelope, including opaque state.
/// This wrapper needs an explicit authenticated host-origin adapter; it does not weaken existing
/// direct-provider exact-read proof or let the package mint handles.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolContextProjectionResult(
    int SchemaVersion,
    WorkflowToolContextProjectionLink Link,
    WorkflowToolContextProjectionDisposition Disposition,
    JsonElement? NextState,
    WorkflowToolContextSlotUpdate? SlotUpdate,
    ContextProviderResult? ProviderResult,
    string? Reason);

// Closed wire readers must reject unknown/missing fields, null required values, duplicate names,
// unknown/numeric enum values, invalid identities/revisions/digests, depth overflow and excess bytes.
// Attributes do not supply all those checks. Positional records/IReadOnlyList are shallow: host code
// clones/freezes nested JSON and collections, validates the full outcome matrix and rechecks binding.
// These DTOs define no command registration, scheduler, delivery journal, runtime hook or persistence.
