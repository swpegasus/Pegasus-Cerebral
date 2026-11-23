using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private const string OwnerSteeringCategory = "owner-correction";
    private const string OwnerAssignmentCategory = "owner-correction-assignment";
    private static readonly UTF8Encoding OwnerTextEncoding = new(false, true);

    private static bool IsOwnerSteering(ContextContribution source) =>
        source.Category is OwnerSteeringCategory or OwnerAssignmentCategory;

    private static bool OwnerSteeringConfirmed(HarnessState state, ContextProjection projection) =>
        state.Contributions.Where(IsOwnerSteering).All(source =>
            projection.IncludedSources.Count(included => included.Id == source.Id && included.Revision == source.SourceRevision) == 1 &&
            !projection.Omissions.Any(omission => omission.Id == source.Id));

    private static ContextContribution OwnerContribution(WorkflowSteeringEntry entry, bool assignment)
    {
        var id = (assignment ? "owner.assignment." : "owner.correction.") + entry.Sequence + "." + TextHash(entry.RequestId);
        return new(id, "pegasus.programming", assignment ? OwnerAssignmentCategory : OwnerSteeringCategory,
            1, ContextRequirement.Required, [Message("user", assignment ? entry.AssignmentText! : entry.Text)], Complete: true);
    }

    // The store owns authorization/idempotency. This validates the detached history and our
    // retained cursor/body identities before using them; metadata cannot stand in for lost text.
    private static IReadOnlyList<WorkflowSteeringEntry> PendingOwnerSteering(AgentHarnessSnapshot snapshot, HarnessState state)
    {
        var entries = snapshot.Run.Steering;
        if (entries is null || state.Contributions is null || entries.Count > 32 ||
            state.OwnerSteeringCount < 0 || state.OwnerSteeringCount > entries.Count)
            throw new ArgumentException("The saved owner-correction sequence is inconsistent.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var required = new List<ContextContribution>();
        var bytes = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.Sequence != index + 1 || string.IsNullOrWhiteSpace(entry.RequestId) ||
                entry.RequestId.Length > 256 || entry.RequestId.Any(char.IsControl) || !ids.Add(entry.RequestId) || entry.AcceptedAt == default)
                throw new ArgumentException("The owner-correction history has an invalid identity or order.");
            _ = OwnerTextEncoding.GetByteCount(entry.RequestId);
            ValidateOwnerText(entry.Text, required: true);
            ValidateOwnerText(entry.AssignmentText, required: false);
            bytes = checked(bytes + OwnerTextEncoding.GetByteCount(entry.Text) + OwnerTextEncoding.GetByteCount(entry.AssignmentText ?? ""));
            if (bytes > 64 * 1024) throw new ArgumentException("Owner-correction history exceeds 64 KiB.");
            if (index >= state.OwnerSteeringCount) continue;
            required.Add(OwnerContribution(entry, assignment: false));
            if (entry.AssignmentText is { Length: > 0 }) required.Add(OwnerContribution(entry, assignment: true));
        }
        var retained = state.Contributions.Where(IsOwnerSteering).ToArray();
        if (retained.Length != required.Count || required.Any(expected =>
            retained.Count(actual => actual.Id == expected.Id &&
                JsonElement.DeepEquals(Element(actual), Element(expected))) != 1))
            throw new ArgumentException("An incorporated owner correction lost its exact required body or identity.");
        return entries.Skip(state.OwnerSteeringCount).ToArray();
    }

    private static void ValidateOwnerText(string? text, bool required)
    {
        if (text is null && !required) return;
        if (text is null || required && string.IsNullOrWhiteSpace(text) || text.Contains('\0') ||
            OwnerTextEncoding.GetByteCount(text) > 8192)
            throw new ArgumentException("Each owner-correction field requires valid bounded UTF-8 text (at most 8 KiB).");
    }

    private static WorkflowStep ReconsiderOwnerSteering(Input input, HarnessState state,
        IReadOnlyList<WorkflowSteeringEntry> entries, string? supersededGeneration = null)
    {
        // Do not mutate evidence or counters for an action that never dispatched. Pending
        // structural intent is superseded, but all actual source/check receipts remain valid.
        var retired = RetireRepairContext(state);
        var basis = retired with
        {
            Verifying = false, Finalizing = false, CompletionDraft = null, CompletionDraftReceipt = null,
            StructuralIntent = null, StructurePreparation = null,
            Contributions = retired.Contributions.Where(source => source.Category != "model-output").ToArray()
        };
        if (basis.ContextRevision > long.MaxValue - entries.Count)
            return Finish(basis, false, "Owner correction was not incorporated: its context revision would overflow.");
        var next = basis;
        foreach (var entry in entries)
        {
            var sources = next.Contributions.Append(OwnerContribution(entry, assignment: false));
            if (entry.AssignmentText is { Length: > 0 }) sources = sources.Append(OwnerContribution(entry, assignment: true));
            next = next with { Contributions = sources.ToArray(), OwnerSteeringCount = entry.Sequence,
                ContextRevision = checked(next.ContextRevision + 1) };
        }
        // The ordinary excerpt helper is deliberately not used for owner text. Overflow must
        // preserve the prior safe state and unadvanced cursor, never silently accept a prefix.
        if (!Fits(next)) return Finish(basis, false, "Owner correction was not incorporated: exact required owner text exceeds the durable state bound. The prior cursor and verified receipts remain saved.");
        if (supersededGeneration is not null)
            next = AppendProjection(next, "owner.superseded." + supersededGeneration, "superseded-generation", "user",
                "New owner correction arrived during generation. Its exact generation receipt is " + supersededGeneration +
                "; no action from that response was dispatched. Reconsider the accepted task with the required owner correction.",
                ContextRequirement.Reducible, supersededGeneration);
        if (!Fits(next)) return Finish(basis, false, "Owner correction was not incorporated: its exact context exceeds the durable state bound. The prior cursor remains saved.");
        if (state.Phase == "start" && input.ContractTopic is not null)
            return RequestTool(input, next, PluginContract, Element(new { topic = input.ContractTopic }));
        return RequestModel(input, next);
    }
}
