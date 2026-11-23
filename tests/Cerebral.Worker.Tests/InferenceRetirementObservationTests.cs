using Cerebral.Contracts.Models;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>In-memory accounting/observation callbacks only; no process, model, storage, network or runtime actions.</summary>
public static class InferenceRetirementObservationTests
{
    public static string[] Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passed.Add(name); Console.WriteLine("PASS: " + name);
        }
        InferenceCaptureRow Row(ModelExecutionAccounting account, string id) =>
            account.Observation.Snapshot().Rows.Single(x => x.AccountingEntryId == id);

        var owner = new ModelExecutionAccounting(coverageComplete: true);
        var cancelled = owner.Begin(ModelOwnershipKind.OutboundPreparation, "model");
        cancelled.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
        cancelled.Cancellation();
        cancelled.Unknown("remote-provider-release-unconfirmed");
        cancelled.Terminal(new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true));
        var unknown = Row(owner, cancelled.Id);
        var oldEntry = owner.Snapshot().Entries.Single();
        Check(unknown is { Phase: "unknown", UncertaintyCode: "ownership-unresolved", Release: "unobserved",
            CancellationRequested: true, Terminal.Finish: "cancelled", Terminal.CancellationConfirmed: true } &&
            owner.Snapshot().UnknownOwnership == 1, "cancellation and a confirmed terminal do not retire unresolved ownership");
        cancelled.Complete();
        var retired = Row(owner, cancelled.Id);
        Console.WriteLine($"Confirmed retirement: phase={retired.Phase}; release={retired.Release}; uncertainty={retired.UncertaintyCode ?? "null"}.");
        Check(retired is { Phase: "released", Release: "confirmed", ReleaseProof: "accounting-owner-confirmed-retirement",
            UncertaintyCode: null, CancellationRequested: true } && owner.Snapshot().Entries.Length == 0,
            "authoritative confirmed retirement clears stale ownership uncertainty after cancellation");
        Check(retired.Terminal == unknown.Terminal && retired.Terminal is
            { Finish: "cancelled", CancellationConfirmed: true, EvaluatedTokens: null, GeneratedTokens: null },
            "retirement preserves the exact cancelled terminal without inventing token totals");
        cancelled.Unknown("late-unconfirmed-callback");
        cancelled.Phase(ModelOwnershipPhase.Generating, true);
        owner.Observation.Accounting(oldEntry, long.MaxValue);
        var afterLate = Row(owner, cancelled.Id);
        Check(afterLate.Release == "confirmed" && afterLate.UncertaintyCode is null && afterLate.Terminal == retired.Terminal &&
            afterLate.AccountingRevision == retired.AccountingRevision && owner.Snapshot().Entries.Length == 0,
            "late accounting callbacks cannot resurrect ownership or replace the terminal of a retired row");
        Check(owner.Observation.SnapshotV2().Snapshot.Rows.Single().UncertaintyCode is null,
            "schema two exposes the same corrected producer facts as schema one");

        var unresolvedOwner = new ModelExecutionAccounting(coverageComplete: true);
        var unresolved = unresolvedOwner.Begin(ModelOwnershipKind.OutboundPreparation, "model");
        unresolved.Phase(ModelOwnershipPhase.Generating, true);
        unresolved.Unknown("remote-provider-release-unconfirmed"); unresolved.Terminal(null);
        unresolved.Cancellation(); unresolved.Dispose();
        var stillUnknown = unresolvedOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddDays(1)).Rows.Single();
        Check(stillUnknown is { Phase: "unknown", Release: "unobserved", ReleaseProof: null,
            UncertaintyCode: "ownership-unresolved", Terminal.Finish: "unknown", Terminal.ProviderError: true } &&
            unresolvedOwner.Snapshot().UnknownOwnership == 1,
            "genuine unresolved ownership remains fail-closed across cancellation disposal and history expiry");

        var stoppedOwner = new ModelExecutionAccounting(coverageComplete: true);
        var stopped = stoppedOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        stopped.Bind(instanceId: "exact-instance");
        stopped.Phase(ModelOwnershipPhase.Generating, true);
        stopped.Unknown("native-slot-quarantined"); stopped.Terminal(null);
        var stoppedTerminal = Row(stoppedOwner, stopped.Id).Terminal;
        var unrelated = stoppedOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        unrelated.Bind(instanceId: "other-instance"); unrelated.Phase(ModelOwnershipPhase.Preparing, true);
        unrelated.Unknown("native-slot-quarantined");
        stoppedOwner.CompleteStoppedInstance("wrong-instance");
        stoppedOwner.Observation.Stopped("exact-instance", "fixture-runtime", 1);
        Check(Row(stoppedOwner, stopped.Id) is { Phase: "quarantined", UncertaintyCode: "ownership-unresolved",
            Release: "unobserved" } && stoppedOwner.Snapshot().UnknownOwnership == 2,
            "a different stop or a model-inventory notification cannot prove exact ownership retirement");
        stoppedOwner.CompleteStoppedInstance("exact-instance");
        var stoppedRow = Row(stoppedOwner, stopped.Id);
        Check(stoppedRow is { Phase: "released", Release: "confirmed", ReleaseProof: "exact-instance-stopped",
            UncertaintyCode: null } && stoppedRow.Terminal == stoppedTerminal && stoppedRow.Terminal?.Finish == "unknown",
            "exact stopped-instance retirement clears ownership uncertainty without upgrading unknown provider termination");
        Check(Row(stoppedOwner, unrelated.Id) is { Phase: "quarantined", Release: "unobserved",
            UncertaintyCode: "ownership-unresolved" } && stoppedOwner.Snapshot().UnknownOwnership == 1,
            "confirmed retirement leaves another instance's real quarantine intact");

        var noEffectOwner = new ModelExecutionAccounting(coverageComplete: true);
        var noEffect = noEffectOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        noEffect.Cancellation(); noEffect.Dispose();
        Check(Row(noEffectOwner, noEffect.Id) is { Phase: "released", Release: "confirmed",
            ReleaseProof: "no-provider-effect", UncertaintyCode: null, Terminal: null, Budget: null, NativeSlotId: null, LeaseId: null } &&
            noEffectOwner.Snapshot().Entries.Length == 0,
            "pre-effect disposal retains distinct no-provider-effect provenance without a fabricated terminal or lease");
        var effected = noEffectOwner.Begin(ModelOwnershipKind.OutboundPreparation, "model");
        effected.Phase(ModelOwnershipPhase.Preparing, true); effected.Dispose();
        Check(Row(noEffectOwner, effected.Id) is { Phase: "unknown", Release: "unobserved", ReleaseProof: null,
            UncertaintyCode: "ownership-unresolved", Terminal: null } && noEffectOwner.Snapshot().UnknownOwnership == 1,
            "scope disposal after an effect never borrows the no-provider-effect retirement proof");

        foreach (var releaseFirst in new[] { false, true })
        {
            var terminalOwner = new ModelExecutionAccounting(coverageComplete: true);
            var ownership = terminalOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
            ownership.Phase(ModelOwnershipPhase.Generating, true); ownership.Cancellation();
            ownership.Unknown("provider-release-unconfirmed");
            var beforeRelease = terminalOwner.Snapshot().Entries.Single();
            if (releaseFirst) ownership.Complete();
            ownership.Terminal(new(ModelGenerationFinish.Stop, "not retained", GeneratedTokens: 7, EvaluatedTokens: 23)
                { PromptCache = new(23, 10, 13, 3.5) });
            var known = Row(terminalOwner, ownership.Id);
            if (!releaseFirst) ownership.Complete();
            terminalOwner.Observation.Accounting(beforeRelease, long.MaxValue);
            var final = Row(terminalOwner, ownership.Id);
            Check(final is { Phase: "released", Release: "confirmed", UncertaintyCode: null,
                Terminal.Finish: "stop", Terminal.CancellationConfirmed: false, Terminal.EvaluatedTokens: 23,
                Terminal.GeneratedTokens: 7, Cache.CachedTokens: 10, Cache.ProcessedTokens: 13 } &&
                final.Terminal == known.Terminal && final.Cache == known.Cache,
                releaseFirst ? "a late actual terminal adds facts after release without restoring ownership uncertainty" :
                    "retirement and late accounting preserve a winning completion and exact token/cache facts");
        }
        var unknownTerminalOwner = new ModelExecutionAccounting();
        var unknownTerminal = unknownTerminalOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        unknownTerminal.Phase(ModelOwnershipPhase.Generating, true);
        unknownTerminal.Unknown("provider-terminal-unconfirmed"); unknownTerminal.Terminal(null);
        var before = Row(unknownTerminalOwner, unknownTerminal.Id).Terminal;
        unknownTerminal.Complete();
        Check(Row(unknownTerminalOwner, unknownTerminal.Id) is { UncertaintyCode: null, Release: "confirmed",
            Terminal.Finish: "unknown", Terminal.ProviderError: true, Terminal.EvaluatedTokens: null, Terminal.GeneratedTokens: null } &&
            Row(unknownTerminalOwner, unknownTerminal.Id).Terminal == before,
            "confirmed ownership retirement does not promote an unknown provider terminal into successful or cancelled output");
        Console.WriteLine($"Retirement observation: {passed.Count} in-memory production accounting/registry checks passed.");
        var nativeChecks = NativeCacheObservationChecks.Run();
        Console.WriteLine($"Native cache observation: {nativeChecks} pure/injected checks passed.");
        return passed.ToArray();
    }
}
