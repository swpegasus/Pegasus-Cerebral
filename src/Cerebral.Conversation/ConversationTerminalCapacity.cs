using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string TerminalCapacityPhase = "terminal-capacity-failure";
    private const string DurableProjectionFailureCode = "conversation.durable_projection_bytes_exhausted";
    private const int MaximumTerminalCapacityBytes = 64 * 1024;
    private sealed class DurableProjectionCapacityException(HarnessState attemptedState)
        : Exception("The next exact projection exceeds the durable UTF-8 byte bound.")
    {
        public HarnessState AttemptedState { get; } = attemptedState;
    }
    private sealed record TerminalCapacityIdentity(string RunId, string RequestId, string ConversationId,
        string ProfileId, string DefinitionId, string DefinitionVersion, string SubmissionSha256);
    private sealed record TerminalToolCoordinate(string EffectId, string Name, string State, string? Outcome,
        int? ExitCode, bool Truncated);
    private sealed record TerminalSourceCoordinate(string Id, string Owner, string Category, long SourceRevision,
        string? ExactReadHandle, bool Complete, string Requirement);
    private sealed record TerminalReadCoordinate(string EffectId, int Offset, long? ExpectedRevision, int RequestedLimit,
        bool Failed, string? LookupEffectId, long? ObservedRevision, string? ObservedState);

    // This wrapper is shared by every exported edition and every signal. A terminal marker
    // never reaches steering, prepared rejection, model generation or a recovery factory.
    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        if (HasTerminalCapacityMarker(snapshot.State)) return RefuseTerminalCapacity(snapshot);
        WorkflowStep step;
        try { step = AdvanceAcceptedSnapshot(snapshot); }
        catch (DurableProjectionCapacityException capacity) { step = ContextFull(capacity.AttemptedState); }
        return step.Next is CompleteWorkflow { Success: false, Error: { } error } failed &&
               failed.Result.ValueKind == JsonValueKind.Object &&
               failed.Result.TryGetProperty("diagnosticCode", out var code) &&
               code.ValueKind == JsonValueKind.String && code.GetString() == DurableProjectionFailureCode &&
               Encoding.UTF8.GetByteCount(step.State.GetRawText()) > WorkflowContract.MaximumStateBytes
            ? ProjectTerminalCapacity(snapshot, step, error) : step;
    }

    private static bool HasTerminalCapacityMarker(JsonElement state) =>
        state.ValueKind == JsonValueKind.Object &&
        (state.TryGetProperty("terminalCapacityFailure", out _) ||
         state.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String &&
             phase.GetString() == TerminalCapacityPhase);

    private static string TerminalHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static TerminalCapacityIdentity TerminalIdentity(AgentHarnessSnapshot snapshot) => new(
        snapshot.Run.Id, snapshot.Submission.RequestId, snapshot.Submission.ConversationId,
        snapshot.Submission.ProfileId, snapshot.Submission.DefinitionId, snapshot.Submission.DefinitionVersion,
        TerminalHash(JsonSerializer.SerializeToUtf8Bytes(snapshot.Submission, Json)));

    private static T[] TerminalCoordinates<T>(IEnumerable<T> values, int maximumCount, int maximumBytes)
    {
        var selected = new List<T>(); var bytes = 0;
        foreach (var value in values)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(value, Json).Length;
            if (selected.Count >= maximumCount || size > maximumBytes - bytes) continue;
            selected.Add(value); bytes += size;
        }
        return selected.ToArray();
    }

    private static WorkflowStep ProjectTerminalCapacity(AgentHarnessSnapshot snapshot, WorkflowStep attempted, string error)
    {
        var state = attempted.State.Deserialize<HarnessState>(Json)
            ?? throw new ArgumentException("The attempted byte-capacity projection is missing.");
        var originalBytes = Encoding.UTF8.GetBytes(attempted.State.GetRawText());
        var receipts = state.ToolReceipts;
        // Preserve the latest real outcome first, then failures/Unknown, then other coordinates.
        // These are handles to existing host evidence, never replacement execution receipts.
        var ordered = receipts.TakeLast(1).Concat(receipts.Reverse().Where(receipt =>
            receipt.State != "Succeeded" || receipt.Outcome != "Succeeded")).Concat(receipts.Reverse())
            .DistinctBy(receipt => receipt.EffectId);
        var tools = TerminalCoordinates(ordered.Select(receipt => new TerminalToolCoordinate(
            receipt.EffectId, receipt.Name, receipt.State, receipt.Outcome, receipt.ExitCode, receipt.Truncated)), 32, 20 * 1024);
        var sources = TerminalCoordinates(state.Contributions.Reverse()
            .OrderBy(source => source.Requirement == Cerebral.Contracts.Context.ContextRequirement.Required ? 0 : 1)
            .Select(source => new TerminalSourceCoordinate(source.Id, source.Owner, source.Category,
                source.SourceRevision, source.ExactReadHandle, source.Complete, source.Requirement.ToString())), 16, 12 * 1024);
        var reads = TerminalCoordinates(state.ReceiptPages.Reverse().Select(read => new TerminalReadCoordinate(
            read.EffectId, read.Offset, read.ExpectedRevision, read.Limit ?? PageBytes, read.Failed,
            read.Observation?.LookupEffectId, read.Observation?.RunRevision, read.Observation?.State.ToString())), 16, 12 * 1024);
        var effect = snapshot.Effect;
        var envelope = Element(new
        {
            phase = TerminalCapacityPhase,
            terminalCapacityFailure = new
            {
                schemaVersion = 1, identity = TerminalIdentity(snapshot), causeCode = DurableProjectionFailureCode,
                canResume = false, historyCheckpointBaselineClaimed = false,
                omittedWorkingProjectionBytes = originalBytes.Length, omittedWorkingProjectionSha256 = TerminalHash(originalBytes),
                omittedWorkingProjectionPersistedByThisTransition = false,
                priorAcceptedStateSha256 = TerminalHash(Encoding.UTF8.GetBytes(snapshot.State.GetRawText())),
                workingProjectionOmitted = true, checkpointOmitted = state.Checkpoint is not null,
                evidenceLocation = "Existing durable host history and effect receipts; this terminal record is reference metadata only.",
                currentHostEffect = effect is null ? null : new
                {
                    effect.Id, effect.CommandId, effect.CommandVersion, state = effect.State.ToString(),
                    resultPresent = effect.Result is not null
                },
                toolReceiptCoordinates = tools, toolReceiptCount = receipts.Count,
                toolReceiptCoordinatesOmitted = receipts.Count - tools.Length,
                sourceCoordinates = sources, contributionCount = state.Contributions.Count,
                sourceCoordinatesOmitted = state.Contributions.Count - sources.Length,
                receiptReadCoordinates = reads, receiptReadCount = state.ReceiptPages.Count,
                receiptReadCoordinatesOmitted = state.ReceiptPages.Count - reads.Length,
                receiptReadAttempts = state.ReceiptReadAttempts,
                receiptReadRequestedDataWindowBytes = state.ReceiptReadRequestedBytes
            }
        });
        if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > MaximumTerminalCapacityBytes)
            throw new ArgumentException("Terminal capacity metadata exceeded its independent 64 KiB bound.");
        return new(envelope, new CompleteWorkflow(Element(new
        {
            summary = error, causeCode = DurableProjectionFailureCode, canResume = false,
            receiptCoordinatesAreReferenceOnly = true, toolReceiptCoordinates = tools,
            toolReceiptCoordinatesOmitted = receipts.Count - tools.Length
        }), false, error));
    }

    private static WorkflowStep RefuseTerminalCapacity(AgentHarnessSnapshot snapshot)
    {
        const string refused = "conversation.terminal_capacity_cannot_resume: This run ended at its durable byte limit. " +
            "Its terminal projection cannot resume, steer, compact, reject a prepared command or replay an effect. " +
            "Use a fresh explicitly admitted request and the existing host receipts.";
        var matches = false;
        try
        {
            matches = snapshot.State.TryGetProperty("phase", out var phase) && phase.GetString() == TerminalCapacityPhase &&
                snapshot.State.TryGetProperty("terminalCapacityFailure", out var marker) && marker.ValueKind == JsonValueKind.Object &&
                marker.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number &&
                schema.TryGetInt32(out var version) && version == 1 &&
                marker.TryGetProperty("canResume", out var resume) && resume.ValueKind == JsonValueKind.False &&
                marker.TryGetProperty("causeCode", out var cause) && cause.ValueKind == JsonValueKind.String &&
                cause.GetString() is DurableProjectionFailureCode or "conversation.terminal_capacity_identity_mismatch" &&
                marker.TryGetProperty("identity", out var identity) &&
                identity.Deserialize<TerminalCapacityIdentity>(Json) == TerminalIdentity(snapshot) &&
                Encoding.UTF8.GetByteCount(snapshot.State.GetRawText()) <= MaximumTerminalCapacityBytes;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { matches = false; }
        if (matches)
            return new(snapshot.State.Clone(), new CompleteWorkflow(Element(new
            {
                summary = refused, canResume = false, identityMatched = true,
                receiptCoordinatesAreReferenceOnly = true
            }), false, refused));
        const string mismatch = "conversation.terminal_capacity_identity_mismatch: A terminal byte-capacity marker is missing, " +
            "malformed or belongs to another accepted run. No continuation or effect was dispatched.";
        return new(Element(new
        {
            phase = TerminalCapacityPhase,
            terminalCapacityFailure = new
            {
                schemaVersion = 1, identity = TerminalIdentity(snapshot),
                causeCode = "conversation.terminal_capacity_identity_mismatch", canResume = false,
                workingProjectionOmitted = true, provenanceUnavailable = true,
                historyCheckpointBaselineClaimed = false
            }
        }), new CompleteWorkflow(Element(new { summary = mismatch, canResume = false, identityMatched = false }), false, mismatch));
    }
}
