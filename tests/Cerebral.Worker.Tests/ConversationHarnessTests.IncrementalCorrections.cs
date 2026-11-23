using System.Text.Json.Nodes;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunIncrementalCorrectionChecks(Action<bool, string> check)
    {
        PreparedModelMetadata Budget(int response) => new("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model",
            128, 2048, response, "deterministic-fixture") { SafetyReserveTokens = 128 };
        var changed = new Fixture();
        changed.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(256));
        changed.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(256));
        changed.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(512));
        check(changed.Is("generate") && changed.Step.State.GetProperty("repairs").GetInt32() == 3,
            "changed measured generation capacity gets another correction instead of a blanket two-error stop");
        changed.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(256));
        check(changed.Step.Next is CompleteWorkflow { Success: false } && changed.Summary.Contains("same unresolved model failure"),
            "alternating generation budgets cannot erase an earlier repeated-limit failure");

        var offset = new Fixture();
        offset.ModelRaw("{"); offset.ModelRaw("   {"); offset.ModelRaw("      {");
        check(offset.Step.Next is CompleteWorkflow { Success: false },
            "changing malformed JSON byte offsets does not count as a changed failure");
        var cap = new Fixture();
        for (var index = 0; index < 8; index++) cap.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(256 + index * 64));
        check(cap.Is("generate") && cap.Step.State.GetProperty("repairs").GetInt32() == 8,
            "distinct measured corrections remain bounded without a complete action");
        cap.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(1024));
        check(cap.Step.Next is CompleteWorkflow { Success: false } && cap.Summary.Contains("eight action corrections"),
            "the overall action-correction backstop stops diagnostic churn");

        var tampered = new Fixture(); tampered.ModelRaw("{");
        var state = JsonNode.Parse(tampered.Step.State.GetRawText())!.AsObject();
        state["actionCorrections"]!.AsArray()[0]!["attempts"] = 0;
        tampered.RestoreState(CerebralJson.Element(state)); tampered.ModelRaw("{");
        check(tampered.Step.Next is CompleteWorkflow { Success: false } && tampered.Summary.Contains("ledger is invalid"),
            "invalid retained correction counters cannot replenish the recovery window");
        var legacy = new Fixture(); legacy.ModelRaw("{");
        var legacyState = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        legacyState.Remove("actionCorrections"); legacyState["repairs"] = 2;
        legacy.RestoreState(CerebralJson.Element(legacyState)); legacy.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(512));
        check(legacy.Step.Next is CompleteWorkflow { Success: false } && legacy.Summary.Contains("retained two"),
            "an old in-flight correction window does not receive new attempts implicitly");

        var clamped = new Fixture(history: [Past("clamped-prior-work", 60, "Prior completed work.")]);
        var clampedBudget = Budget(256) with { InputTokens = 1024, DesiredResponseBudget = 512 };
        for (var index = 0; index < 2; index++)
        {
            clamped.ModelRaw("{", ModelGenerationFinish.Length, prepared: clampedBudget);
            check(clamped.Is("compact") && clamped.Step.State.GetProperty("repairs").GetInt32() == index + 1,
                "clamped output-limit recovery records its correction before compaction");
            clamped.CompactSuccess(before: clampedBudget with { InputTokens = 1800 }, after: clampedBudget);
        }
        clamped.ModelRaw("{", ModelGenerationFinish.Length, prepared: clampedBudget);
        check(clamped.Step.Next is CompleteWorkflow { Success: false },
            "compaction cannot replenish repeated incomplete-generation recovery at unchanged capacity");

        var unknown = new Fixture();
        unknown.ModelRaw("{");
        unknown.Model(new { action = "tool", name = "not.advertised", arguments = new { } });
        unknown.ModelRaw("{");
        unknown.Model(new { action = "tool", name = "not.advertised", arguments = new { } });
        unknown.ModelRaw("{");
        check(unknown.Step.Next is CompleteWorkflow { Success: false },
            "semantically rejected tool proposals cannot erase an earlier malformed-action failure");

        var selected = new Fixture(); selected.Describe("file.read"); selected.Catalog(exact: true);
        selected.Describe("file.read"); selected.Describe("file.read"); selected.Describe("file.read");
        check(selected.Step.Next is CompleteWorkflow { Success: false },
            "an already selected schema cannot reset corrections through rejected describe proposals");

        var units = new Fixture(history: [Past("original-goal", 60, "Build the complete multi-section deliverable.")]);
        units.Select("file.write", CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } },
            required = new[] { "path", "content" }, additionalProperties = false
        }));
        for (var index = 0; index < 6; index++)
        {
            units.ModelRaw("{", ModelGenerationFinish.Length, prepared: Budget(256));
            units.Model(new { action = "tool", name = "file.write", arguments = new { path = $"part-{index}.txt", content = $"Completed unit {index}." } });
            check(units.Is("tool") && units.Step.State.GetProperty("repairs").GetInt32() == 0 &&
                !units.Step.State.TryGetProperty("actionCorrections", out _), "a complete chunk resets general model corrections for the next unit");
            units.Tool(true, $"Saved exact unit {index}.");
            check(units.Is("generate"), "a confirmed unit continues the same assignment beyond two total iterations");
            if (index is 1 or 3)
            {
                units.RejectContext(Cerebral.Contracts.Context.ContextProjectionStatus.NeedsCompaction);
                units.CompactSuccess();
                check(units.Is("generate") && units.Context.Checkpoint is not null,
                    "an incremental assignment continues after separate compaction checkpoints");
            }
        }
        check(units.Step.State.GetProperty("toolReceipts").GetArrayLength() == 6 &&
            units.SystemText.Contains("Smaller context means smaller units") && units.SystemText.Contains("Review saved units separately"),
            "all six unit receipts survive compaction alongside generic incremental and short-review framing");
        units.Model(new { action = "final", text = "All six saved units are available for review." });
        check(units.Step.Next is CompleteWorkflow { Success: true },
            "an incremental fixture finishes with a compact reference response rather than regenerating its artifact");
    }
}
