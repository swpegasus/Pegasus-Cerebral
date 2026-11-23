using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    // Only identities live here. Text remains once in its original, bounded contribution.
    private sealed record PendingRepairContextState(string ProposalContributionId, string ProposalBodySha256,
        ProgrammingEditFingerprint Fingerprint, string[] SourceContributionIds, RepairRequirementOverride[] RequirementOverrides);
    private sealed record RepairRequirementOverride(string ContributionId, long SourceRevision,
        string IdentitySha256, ContextRequirement PriorRequirement);

    private static bool IsOriginalRepairProposal(ContextContribution source, ProgrammingEditFingerprint fingerprint)
    {
        if (source.Owner != "pegasus.programming" || source.Category != "model-output" || !source.Complete ||
            source.ExactReadHandle is null || source.Id != "model." + source.ExactReadHandle ||
            source.Messages.Count != 1 || source.Messages[0].Role != "assistant" ||
            source.Messages[0].Content is not [TextContentPart text] || !ProjectionComplete(text.Text)) return false;
        try
        {
            using var document = JsonDocument.Parse(text.Text, new JsonDocumentOptions { MaxDepth = 32 });
            var action = document.RootElement;
            ValidateAction(action);
            if (RequiredText(action, "action") != "tool" || RequiredText(action, "name") != RangeEdit) return false;
            var args = action.GetProperty("arguments");
            ValidateRangeEditArguments(args);
            return fingerprint.Matches(ProgrammingEditProposal.Read(args, closedShape: false)
                .Fingerprint(RequiredText(args, "path"), RequiredText(args, "expectedSha256")));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static ContextContribution? OriginalRepairProposal(HarnessState state)
    {
        if (state.PendingRepairContext is not { Fingerprint: { } } pending ||
            !IsRangeHash(pending.ProposalBodySha256 ?? "")) return null;
        var matches = state.Contributions.Where(source => source.Id == pending.ProposalContributionId).ToArray();
        return matches.Length == 1 && IsOriginalRepairProposal(matches[0], pending.Fingerprint) &&
            TextHash(((TextContentPart)matches[0].Messages[0].Content[0]).Text) == pending.ProposalBodySha256 ? matches[0] : null;
    }

    private static bool TryStageRepairContext(HarnessState state, ProgrammingEditFingerprint fingerprint, out HarnessState next)
    {
        if (state.PendingRepairContext is { } active && active.Fingerprint.Matches(fingerprint) && OriginalRepairProposal(state) is not null)
        {
            next = RefreshRepairContext(state);
            return true;
        }
        next = RetireRepairContext(state);
        var original = next.Contributions.LastOrDefault(source => IsOriginalRepairProposal(source, fingerprint));
        if (original is null) return false;
        next = RefreshRepairContext(next with { PendingRepairContext = new(original.Id,
            TextHash(((TextContentPart)original.Messages[0].Content[0]).Text), fingerprint, [], []) });
        return true;
    }

    private static HashSet<string> ProtectedRepairContributions(HarnessState state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (state.PendingRepairContext is not { } pending || OriginalRepairProposal(state) is null) return ids;
        ids.Add(pending.ProposalContributionId);
        ids.UnionWith(pending.SourceContributionIds ?? []);
        // Reconsideration needs the actual matching preparation evidence after a gap read too.
        if (state.StructurePreparation is { Outcome: "succeeded", ReceiptId: { } receipt, Proposal: { } fingerprint } &&
            pending.Fingerprint.Matches(fingerprint))
            foreach (var source in state.Contributions.Where(source => source.Owner == "pegasus.programming" &&
                source.Category == "tool-result" && source.ExactReadHandle == receipt)) ids.Add(source.Id);
        return ids;
    }

    private static string RepairContributionIdentity(ContextContribution source) =>
        TextHash(JsonSerializer.Serialize(source with { Requirement = ContextRequirement.Reducible }, Json));

    // Restore only this repair's own overrides before an existing selection/filter policy runs.
    // Revision and full immutable contribution identity prevent clobbering a replacement with its old flag.
    private static HarnessState RestoreRepairRequirements(HarnessState state)
    {
        if (state.PendingRepairContext is not { } pending) return state;
        var overrides = pending.RequirementOverrides ?? [];
        return state with
        {
            PendingRepairContext = pending with { RequirementOverrides = [] },
            Contributions = state.Contributions.Select(source =>
            {
                var prior = overrides.FirstOrDefault(item => item.ContributionId == source.Id &&
                    item.SourceRevision == source.SourceRevision && item.IdentitySha256 == RepairContributionIdentity(source));
                return prior is not null && source.Requirement == ContextRequirement.Required
                    ? source with { Requirement = prior.PriorRequirement } : source;
            }).ToArray()
        };
    }

    private static HarnessState RefreshRepairContext(HarnessState original)
    {
        if (original.PendingRepairContext is null) return original;
        var state = RestoreRepairRequirements(original);
        if (state.PendingRepairContext is not { } pending || OriginalRepairProposal(state) is null)
            return RetireRepairContext(state);
        var fingerprint = pending.Fingerprint;
        var candidates = ActiveSources(state).Where(source => SameSource(source, fingerprint.Path, fingerprint.SourceSha256) &&
            source.PhysicalLineCount > 0).ToArray();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Length != 0)
        {
            var first = Math.Max(1, fingerprint.StartLine - 1);
            var last = (int)Math.Min(PhysicalLast(state, candidates[^1]), (long)fingerprint.StartLine + fingerprint.LineCount);
            long cursor = first;
            // Cover only currently present parts of this unit. Gaps remain gaps for RepairGap;
            // pick farthest coverage, breaking ties by smaller original bodies, never join text.
            while (cursor <= last)
            {
                var choice = candidates.Where(source => source.StartLine <= cursor &&
                        (long)source.StartLine + source.PhysicalLineCount > cursor)
                    .OrderByDescending(source => Math.Min(last, (long)source.StartLine + source.PhysicalLineCount - 1))
                    .ThenBy(source => Encoding.UTF8.GetByteCount(((TextContentPart)state.Contributions
                        .Single(item => item.Id == source.ContributionId).Messages[0].Content[0]).Text))
                    .ThenBy(source => source.ContributionId, StringComparer.Ordinal).FirstOrDefault();
                if (choice is null)
                {
                    cursor = candidates.Where(source => source.StartLine > cursor && source.StartLine <= last)
                        .Select(source => source.StartLine).DefaultIfEmpty(last + 1).Min();
                    continue;
                }
                selectedIds.Add(choice.ContributionId);
                cursor = (long)choice.StartLine + choice.PhysicalLineCount;
            }
        }
        var updated = state with { PendingRepairContext = pending with { SourceContributionIds = selectedIds.Order(StringComparer.Ordinal).ToArray() } };
        var protectedIds = ProtectedRepairContributions(updated);
        var changes = new List<RepairRequirementOverride>();
        var contributions = state.Contributions.Select(source =>
        {
            if (!protectedIds.Contains(source.Id) || source.Requirement == ContextRequirement.Required) return source;
            changes.Add(new(source.Id, source.SourceRevision, RepairContributionIdentity(source), source.Requirement));
            return source with { Requirement = ContextRequirement.Required };
        }).ToArray();
        var changed = contributions.Where((source, index) => source.Requirement != original.Contributions[index].Requirement).Any();
        return updated with
        {
            PendingRepairContext = updated.PendingRepairContext! with { RequirementOverrides = changes.ToArray() },
            Contributions = contributions,
            ContextRevision = changed ? checked(original.ContextRevision + 1) : original.ContextRevision
        };
    }

    private static HarnessState RetireRepairContext(HarnessState state)
    {
        if (state.PendingRepairContext is not { } pending) return state;
        var restored = RestoreRepairRequirements(state);
        return restored with
        {
            PendingRepairContext = null,
            Contributions = restored.Contributions.Where(source => source.Id != pending.ProposalContributionId).ToArray(),
            ContextRevision = checked(state.ContextRevision + 1)
        };
    }
}
