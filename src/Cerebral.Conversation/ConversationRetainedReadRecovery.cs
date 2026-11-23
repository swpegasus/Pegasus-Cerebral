using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Versioned at new-run admission. Missing state keeps the legacy policy and spent counters;
    // neither steering nor an upgraded package replenishes an accepted run's recovery window.
    private sealed record RetainedReadRecovery(int SchemaVersion, string RunId, int ReuseLimit)
    {
        public int Reuses { get; init; }
        public IReadOnlyList<RetainedEvidenceUse> EvidenceUses { get; init; } = [];
        public IReadOnlyList<string> ObservedOutcomes { get; init; } = [];
        public string LastProgress { get; init; } = "initial";
    }

    private sealed record RetainedEvidenceUse(string EffectId, int Uses);

    private static void ValidateRetainedReadRecovery(HarnessState state, string runId)
    {
        if (state.ReadRecovery is not { } recovery) return;
        if (recovery.SchemaVersion != 1 || recovery.RunId != runId ||
            recovery.ReuseLimit != MaximumActionCorrections || recovery.Reuses < 0 ||
            recovery.Reuses > recovery.ReuseLimit || recovery.EvidenceUses is null ||
            recovery.ObservedOutcomes is null || recovery.EvidenceUses.Count > recovery.ReuseLimit ||
            recovery.EvidenceUses.Any(use => use is null || use.EffectId is not { Length: > 0 and <= 128 } ||
                use.Uses < 1 || use.Uses > recovery.ReuseLimit ||
                !state.ToolReceipts.Any(receipt => receipt.EffectId == use.EffectId &&
                    (receipt.State == "Succeeded" || state.FailedBuildRecovery is not null && ReusableFailedBuildReceipt(receipt)))) ||
            recovery.EvidenceUses.Select(use => use.EffectId).Distinct(StringComparer.Ordinal).Count() != recovery.EvidenceUses.Count ||
            recovery.EvidenceUses.Sum(use => use.Uses) != recovery.Reuses ||
            recovery.ObservedOutcomes.Count > MaximumLookups ||
            recovery.ObservedOutcomes.Any(hash => !ValidPackageFingerprint(hash)) ||
            recovery.ObservedOutcomes.Distinct(StringComparer.Ordinal).Count() != recovery.ObservedOutcomes.Count ||
            recovery.LastProgress is not ("initial" or "new-read-evidence" or "source-change" or "verified-milestone"))
            throw new ArgumentException("The saved retained-read recovery policy is invalid or belongs to another run.");
    }

    private static WorkflowStep ReuseRetainedRead(Input input, HarnessState state, string name,
        JsonElement arguments, string priorEffect, ContextContribution retainedRead)
    {
        var recovery = state.ReadRecovery!;
        var diagnostic = RetainedReadCorrection(state, name, arguments, priorEffect, retainedRead);
        if (recovery.Reuses >= recovery.ReuseLimit)
            return Finish(state, false,
                "The retained-evidence recovery window is exhausted without new evidence or verified work progress. " +
                "The saved receipts and failed attempts are preserved. Narrow the actual blocker before a new run. " + diagnostic);

        // A covered subrange or spelling/default alias resolves to the same actual receipt. It is
        // evidence reuse, not a malformed action, a fresh read, or a completed tool milestone.
        var (updated, sameEvidenceUses) = IncrementRetainedEvidenceUse(recovery, priorEffect);
        recovery = updated;
        var status = JsonSerializer.Serialize(new
        {
            kind = "retained-evidence-reuse", policyVersion = recovery.SchemaVersion,
            receipt = priorEffect, freshRead = false, toolDispatched = false,
            reuse = recovery.Reuses, sameEvidenceUses, remaining = recovery.ReuseLimit - recovery.Reuses,
            lastProgress = recovery.LastProgress, projectionComplete = retainedRead.Complete
        }, Json);
        var guidance = sameEvidenceUses >= MaximumRepairs || recovery.Reuses == recovery.ReuseLimit
            ? " This evidence is unchanged. Choose a different supported action that obtains missing evidence or advances the requested work; " +
              "if none is possible, return blocked with the exact missing capability or precondition. Argument rewording and cached lookups are not progress."
            : " The original retained projection is available again below its original receipt identity; use it to choose the next action.";
        if (name == "plugin.projects" && state.ScaffoldReadiness?.Ready != true)
            guidance += " New-scaffold admission is unavailable. An inspected existing example does not select the user's new target. " +
                "For incomplete broad discovery, use only a continuation token supplied by the actual result and supported by the selected exact schema. " +
                "If complete identity coverage cannot be obtained, report that discovery blocker; repeating the cached inventory or inspecting an unrelated root does not establish global absence.";
        var next = state with
        {
            ReadRecovery = recovery, PendingPluginTargetAction = null,
            // Re-present the original body without duplicating it, changing its revision/completeness,
            // or fabricating a new read result. Current source/build evidence stays required too.
            Contributions = [.. state.Contributions.Where(source => source.Id != retainedRead.Id),
                retainedRead with { Requirement = ContextRequirement.Required }]
        };
        next = Append(next, "read-reuse." + state.ContextRevision + "." + recovery.Reuses,
            "retained-read-recovery", "user", status + "\n" + diagnostic + guidance,
            ContextRequirement.Required, priorEffect);
        return RequestModel(input, next);
    }

    private static (RetainedReadRecovery Recovery, int SameEvidenceUses) IncrementRetainedEvidenceUse(
        RetainedReadRecovery recovery, string effectId)
    {
        var uses = recovery.EvidenceUses.ToList();
        var index = uses.FindIndex(use => use.EffectId == effectId);
        var count = index < 0 ? 1 : uses[index].Uses + 1;
        if (index < 0) uses.Add(new(effectId, count));
        else uses[index] = uses[index] with { Uses = count };
        return (recovery with { Reuses = recovery.Reuses + 1, EvidenceUses = uses.ToArray() }, count);
    }

    private static HarnessState ObserveRetainedReadProgress(HarnessState before, HarnessState after,
        WorkflowEffect effect, ToolData? data, string? readKey)
    {
        if (after.ReadRecovery is not { } recovery) return after;
        // New failed-build recovery uses the actual stock file producer contract, which
        // permits absent operation Evidence. Saved pre-107 runs keep their old progress gate.
        var confirmed = after.FailedBuildRecovery is null
            ? effect.State == WorkflowEffectState.Succeeded && data is { Evidence: { Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } }
            : SuccessfulEffect(effect, data) && data is { Truncated: false } &&
                (data.Evidence?.RequestId is null || data.Evidence?.RequestId == effect.Id);
        if (!confirmed || data is null) return after;
        string? progress = null;
        if (before.PluginWork is { } oldWork && after.PluginWork is { } work && SameBuild(oldWork.Build, work.Build))
        {
            if (work.SourceGeneration > oldWork.SourceGeneration) progress = "source-change";
            else if (work.SuccessfulBuildGeneration == work.SourceGeneration && work.BuildEffectId is not null &&
                    work.SuccessfulBuildGeneration != oldWork.SuccessfulBuildGeneration ||
                work.VerifiedGeneration == work.SourceGeneration && work.VerifyEffectId is not null &&
                    work.VerifiedGeneration != oldWork.VerifiedGeneration ||
                work.PresentedGeneration == work.SourceGeneration && work.PresentEffectId is not null &&
                    work.PresentedGeneration != oldWork.PresentedGeneration)
                progress = "verified-milestone";
        }
        if (after.KnownFileRevisions.Any(current => before.KnownFileRevisions.Any(prior =>
                PathEquals(prior.Path, current.Path) && !string.Equals(prior.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))))
            progress = "source-change";
        if (after.FailedBuildRecovery is not null && before.PendingName is "file.write" or "file.edit" or "file.append" or "file.copy" &&
            before.PluginWork is { } inputBefore && after.PluginWork is { } inputAfter && SameBuild(inputBefore.Build, inputAfter.Build) &&
            after.ToolReceipts.LastOrDefault()?.CanonicalProof is { InputRecipeSha256: not null, SourceRevision: not null } &&
            CanonicalInputIdentity(ReadCanonicalInputs(before, inputBefore.Build)) != CanonicalInputIdentity(ReadCanonicalInputs(after, inputAfter.Build)))
            progress = "source-change";
        if (progress is not null)
            recovery = recovery with { ObservedOutcomes = [] };
        else if (readKey is not null)
        {
            var signature = RetainedReadOutcomeHash(data.Name, data.Content);
            // Keep a cumulative bounded set until verified work changes. Do not evict it: rotating
            // A/B requests, receipt IDs or fresh inspections must not replenish a stalled loop.
            // At saturation additional reads remain available, but no longer grant a fresh window.
            if (recovery.ObservedOutcomes.Count < MaximumLookups &&
                !recovery.ObservedOutcomes.Contains(signature, StringComparer.Ordinal))
            {
                recovery = recovery with { ObservedOutcomes = [.. recovery.ObservedOutcomes, signature] };
                progress = "new-read-evidence";
            }
        }
        if (progress is null) return after;
        return after with
        {
            ReadRecovery = recovery with { Reuses = 0, EvidenceUses = [], LastProgress = progress },
            Contributions = after.Contributions.Select(source => source.Category is "retained-read-recovery" or "failed-build-recovery" or "failed-build-evidence"
                ? source with { Requirement = ContextRequirement.Reducible } : source).ToArray()
        };
    }

    private static string RetainedReadOutcomeHash(string name, string content)
    {
        JsonElement body;
        try { body = Parse(content); }
        catch (JsonException) { body = Element(content); }
        if (name == "plugin.projects" && body.ValueKind == JsonValueKind.Object)
        {
            try
            {
                body = Element(body.EnumerateObject().Where(property => property.Name is not
                    ("inventoryId" or "inventoryJobId" or "inventoryPage" or "continuation"))
                    .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal));
            }
            catch (ArgumentException)
            {
                // A malformed duplicate-key envelope is still an actual retained result. Its
                // admission stays denied; digest it as opaque data without losing that receipt.
                body = Element(content);
            }
        }
        // Canonical object ordering ignores cosmetic JSON changes; neither args nor effect IDs
        // enter this identity. A changed request that returns the same evidence is not progress.
        return AdmissionArgumentHash(Element(new { name, content = body }));
    }
}
