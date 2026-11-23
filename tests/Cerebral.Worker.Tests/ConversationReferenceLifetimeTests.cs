using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunPendingReferencePreservationChecks(Action<bool, string> check)
    {
        static ContextContribution[] Sources(Fixture fixture) =>
            fixture.Step.State.GetProperty("contributions").Deserialize<ContextContribution[]>(CerebralJson.Options)!;
        static ContextContribution[] Reads(Fixture fixture) =>
            Sources(fixture).Where(source => source.Category is "history-reference" or "receipt-reference").ToArray();
        static Fixture FreshHistory()
        {
            var fixture = new Fixture(history: [new("older-reducible", "fixture.history", "conversation", 1,
                ContextRequirement.Reducible, [new("assistant", [new TextContentPart("Older context may be compacted.")])],
                "older-reducible", BeforeCurrentInput: true)]);
            fixture.Model(new { action = "history", eventId = "source-qualification", offset = 0 });
            fixture.HistoryPage("Exact isolation qualification: managed tool access is not a process sandbox.");
            return fixture;
        }

        var fresh = FreshHistory();
        var original = Reads(fresh).Single();
        check(fresh.Is("generate") && original.Requirement == ContextRequirement.Required &&
              !original.BeforeCurrentInput && original.Owner == ConversationServicePlugin.ModuleId,
            "fresh exact history reaches its next preparation as a required current-turn reference");
        fresh.RejectContext(ContextProjectionStatus.NeedsCompaction);
        check(fresh.Is("compact") && fresh.Context.Contributions.Single(source => source.Id == original.Id).Requirement == ContextRequirement.Required,
            "exact context pressure cannot make an unconsumed source read reducible");
        fresh.CompactSuccess();
        check(fresh.Is("generate") && JsonElement.DeepEquals(CerebralJson.Element(Reads(fresh).Single()), CerebralJson.Element(original)) &&
              fresh.Context.Checkpoint!.CoveredSources.All(source => source.Id != original.Id),
            "compaction retains the exact fresh source and excludes it from generated checkpoint coverage");

        var paged = new Fixture();
        paged.Model(new { action = "history", eventId = "paged-source", offset = 0 });
        paged.HistoryPage(new string('x', 4096), total: 4100, next: 4096);
        check(Reads(paged).Single().Requirement == ContextRequirement.Required && !Reads(paged).Single().Complete,
            "a partial byte page is protected without being mislabeled a complete source");
        paged.Model(new { action = "history", eventId = "paged-source", offset = 4096 });
        check(Reads(paged).Single().Requirement == ContextRequirement.Required,
            "a schema-valid next-page decision does not retire earlier fragments");
        paged.HistoryPage("tail", total: 4100);
        check(Reads(paged).Length == 2 && Reads(paged).All(source => source.Requirement == ContextRequirement.Required),
            "all pending fragments survive same-category append until a non-read decision");
        paged.Describe("file.read");
        check(paged.Is("catalog") && Reads(paged).All(source => source.Requirement == ContextRequirement.Reducible),
            "a valid non-read decision releases consumed references without removing exact content");
        paged.Catalog(exact: true);
        check(paged.Is("generate") && Reads(paged).Length == 2 &&
              Reads(paged).Any(source => source.Messages.Single().Content.OfType<TextContentPart>().Single().Text.Contains("tail", StringComparison.Ordinal)),
            "consumed pages remain available as bounded reducible history");

        var malformed = FreshHistory(); malformed.ModelRaw("unfinished action");
        check(Reads(malformed).All(source => source.Requirement == ContextRequirement.Required),
            "malformed model output cannot claim pending source pages were consumed");
        var partial = FreshHistory(); partial.ModelRaw("partial action", ModelGenerationFinish.Length);
        check(Reads(partial).All(source => source.Requirement == ContextRequirement.Required),
            "a confirmed length result preserves the pending read through bounded recovery");
        var uncertain = FreshHistory(); uncertain.Uncertain();
        check(uncertain.Step.Next is CompleteWorkflow { Success: false } &&
              Reads(uncertain).All(source => source.Requirement == ContextRequirement.Required),
            "unknown generation does not silently retire the source evidence");

        var receipt = new Fixture(); receipt.Describe("file.read"); receipt.Catalog(exact: true);
        receipt.Model(new { action = "tool", name = "file.read", arguments = new { path = "fixture" } });
        receipt.Tool(false, "Actual failed read.");
        receipt.Model(new { action = "receipt", effectId = "actual-read-effect", offset = 0 });
        receipt.ReceiptPage("Failure details retained for analysis.", WorkflowEffectState.Failed);
        check(Reads(receipt).Single().Requirement == ContextRequirement.Required &&
              Sources(receipt).Single(source => source.Category == "tool-result").Requirement == ContextRequirement.Required &&
              receipt.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "fresh receipt diagnostics coexist with the real failure anchor and grant no execution credit");
        receipt.Model(new { action = "history", eventId = "other-source", offset = 0 });
        receipt.HistoryPage("Related source.");
        check(Reads(receipt).Length == 2 && Reads(receipt).All(source => source.Requirement == ContextRequirement.Required),
            "chained history and receipt reads preserve both pending reference categories");
        receipt.Model(new { action = "tool", name = "file.read", arguments = new { path = "changed-fixture" } });
        check(receipt.Is("tool") && Reads(receipt).All(source => source.Requirement == ContextRequirement.Reducible) &&
              Sources(receipt).Single(source => source.Category == "tool-result").Requirement == ContextRequirement.Required,
            "a valid repair action releases references without downgrading actual failed-tool evidence");

        var prior = new Fixture(history: [new("required-prior", ConversationServicePlugin.ModuleId, "history-reference", 1,
            ContextRequirement.Required, [new("user", [new TextContentPart("Prior required source stays required.")])],
            "required-prior", BeforeCurrentInput: true)]);
        prior.Model(new { action = "final", text = "Complete ordinary answer." });
        check(Sources(prior).Single(source => source.Id == "required-prior").Requirement == ContextRequirement.Required,
            "reference retirement never changes prior-input journal requirements");

        var foreign = FreshHistory();
        var foreignState = System.Text.Json.Nodes.JsonNode.Parse(foreign.Step.State.GetRawText())!;
        foreach (var source in foreignState["contributions"]!.AsArray())
            if (source!["category"]!.GetValue<string>() == "history-reference") source["owner"] = "other.provider";
        foreign.RestoreState(CerebralJson.Element(foreignState));
        foreign.Describe("file.read");
        check(Reads(foreign).Single().Requirement == ContextRequirement.Required,
            "retirement is limited to projections owned by this harness");
    }
}
