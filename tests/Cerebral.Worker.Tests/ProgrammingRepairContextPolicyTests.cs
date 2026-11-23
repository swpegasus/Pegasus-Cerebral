using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    static partial void PendingRepairContextPolicy(Action<bool, string> check)
    {
        Fixture New() => new(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.edit-range", "program.source.structure", "process.run"],
            guardFinalization: true);
        void Initial(Fixture f, string text = "nextCall();", Action? beforeProposal = null)
        {
            var error = Path.Combine(f.Root, "Program.cs") + "(10,2): error CS1003: Syntax error";
            f.Dotnet("build"); f.Tool(false, error, 1, error: error);
            beforeProposal?.Invoke();
            f.Model(new
            {
                action = "tool", name = "file.edit-range", arguments = new
                {
                    path = Path.Combine(f.Root, "Program.cs"), expectedSha256 = Fixture.SourceHash(),
                    startLine = 10, lineCount = 3, newText = text
                }
            });
        }
        JsonElement Pending(Fixture f) => f.Step.State.GetProperty("pendingRepairContext");
        string Id(Fixture f) => Pending(f).GetProperty("proposalContributionId").GetString()!;
        string Text(ContextContribution source) => ((TextContentPart)source.Messages.Single().Content.Single()).Text;
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        ContextContribution Original(Fixture f) => f.Context.Contributions.Single(source => source.Id == Id(f));
        JsonElement ActionFromActualContext(Fixture f)
        {
            var original = Original(f);
            check(original.Requirement == ContextRequirement.Required && original.Complete &&
                Hash(Text(original)) == Pending(f).GetProperty("proposalBodySha256").GetString(),
                "resubmission is derived from the complete original in the actual next context, not an injected test action");
            return JsonDocument.Parse(Text(original)).RootElement.Clone();
        }
        void Absent(Fixture f, string originalId, string label)
        {
            check(Pending(f).ValueKind == JsonValueKind.Null &&
                !f.Step.State.GetProperty("contributions").EnumerateArray().Any(source => source.GetProperty("id").GetString() == originalId),
                label + " retires active proposal identity and original body");
        }
        Fixture Prepared(bool full = false)
        {
            var f = New(); f.SourceWindow(1, full ? 20 : 5); Initial(f); f.PreparedStructure(); return f;
        }

        var staged = Prepared();
        var originalId = Id(staged); var originalHash = Hash(Text(Original(staged)));
        check(staged.IsModel && staged.Step.State.GetProperty("editCount").GetInt32() == 0 &&
            staged.Context.Contributions.Count(source => source.Id == originalId) == 1 &&
            !Pending(staged).GetRawText().Contains("nextCall", StringComparison.Ordinal),
            "structure preparation retains one original action by identity, without an extra replacement cache or edit credit");
        staged.Model(ActionFromActualContext(staged));
        check(staged.IsTool && staged.PendingTool == "file.read" &&
            staged.Command.Arguments.GetProperty("arguments").GetProperty("startLine").GetInt32() == 9 &&
            staged.Command.Arguments.GetProperty("arguments").GetProperty("lineCount").GetInt32() == 5,
            "context-derived identical proposal reaches the existing exact missing-anchor read");
        staged.SourceReply(9, 5);
        check(Id(staged) == originalId && Hash(Text(Original(staged))) == originalHash &&
            staged.Step.State.GetProperty("pendingArguments").ValueKind == JsonValueKind.Null,
            "a gap read preserves the FIRST original contributor and exact body after in-flight arguments are cleared");
        var sourceIds = Pending(staged).GetProperty("sourceContributionIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var refs = staged.Step.State.GetProperty("readProjectionReferences").EnumerateArray().ToArray();
        var unitId = refs.Single(reference => reference.GetProperty("source").GetProperty("startLine").GetInt32() == 9)
            .GetProperty("contributionId").GetString()!;
        var headId = refs.Single(reference => reference.GetProperty("source").GetProperty("startLine").GetInt32() == 1)
            .GetProperty("contributionId").GetString()!;
        check(sourceIds.SequenceEqual([unitId]) &&
            staged.Context.Contributions.Single(source => source.Id == unitId).Requirement == ContextRequirement.Required &&
            staged.Context.Contributions.Single(source => source.Id == headId).Requirement == ContextRequirement.Reducible,
            "only the necessary original current-SHA unit body is protected; unrelated head evidence is reducible");
        var preparationReceipt = staged.Step.State.GetProperty("structurePreparation").GetProperty("receiptId").GetString();
        check(staged.Context.Contributions.Single(source => source.ExactReadHandle == preparationReceipt).Requirement == ContextRequirement.Required,
            "matching original boundary evidence remains available for reconsideration after a gap read");
        staged.ContextRejected(ContextProjectionStatus.NeedsCompaction); staged.CompactSuccess();
        check(staged.IsModel && Id(staged) == originalId && Hash(Text(Original(staged))) == originalHash &&
            staged.Context.Contributions.Any(source => source.Id == unitId && source.Requirement == ContextRequirement.Required) &&
            staged.Context.Contributions.All(source => source.Id != headId),
            "actual compaction adoption preserves proposal and minimal anchors by original IDs while discarding unrelated bodies");
        var action = ActionFromActualContext(staged);
        var fingerprint = Pending(staged).GetProperty("fingerprint");
        check(Hash(action.GetProperty("arguments").GetProperty("newText").GetString()!) == fingerprint.GetProperty("newTextSha256").GetString(),
            "retained original action reproduces the exact staged draft fingerprint");
        staged.Model(action);
        check(staged.IsTool && staged.PendingTool == "file.edit-range",
            "explicit resubmission from actual context dispatches the editor; actual=" +
                (staged.IsTool ? staged.PendingTool : staged.Step.Next.GetType().Name));
        check(JsonElement.DeepEquals(staged.Command.Arguments.GetProperty("arguments"), action.GetProperty("arguments")),
            "editor arguments preserve exact decoded path/preimage/range/replacement values from the original action");
        Absent(staged, originalId, "mutation dispatch before outcome");
        staged.RangeResult();
        check(staged.Step.State.GetProperty("editCount").GetInt32() == 1 && staged.IsModel,
            "one admitted existing semantic receipt grants exactly one edit, with no automatic replay");

        var split = New(); split.SourceWindow(10, 3); split.SourceWindow(9, 1);
        Initial(split); split.PreparedStructure();
        var splitId = Id(split);
        split.Model(ActionFromActualContext(split));
        check(split.PendingTool == "file.read" &&
            split.Command.Arguments.GetProperty("arguments").GetProperty("startLine").GetInt32() == 13 &&
            split.Command.Arguments.GetProperty("arguments").GetProperty("lineCount").GetInt32() == 1,
            "split unit acquires only its one missing boundary anchor");
        split.SourceReply(13, 1);
        check(Pending(split).GetProperty("sourceContributionIds").GetArrayLength() == 3,
            "split unit retains the three needed ORIGINAL bodies without copying or joining source text");
        split.ContextRejected(ContextProjectionStatus.NeedsCompaction); split.CompactSuccess();
        check(Id(split) == splitId && Pending(split).GetProperty("sourceContributionIds").EnumerateArray()
            .All(id => split.Context.Contributions.Any(source => source.Id == id.GetString() && source.Requirement == ContextRequirement.Required)),
            "split anchors survive compaction in actual context under normal required-floor admission");

        var superseded = Prepared(full: true);
        var oldId = Id(superseded);
        var revised = JsonNode.Parse(ActionFromActualContext(superseded).GetRawText())!;
        revised["arguments"]!["newText"] = revised["arguments"]!["newText"]!.GetValue<string>() + " // revised";
        superseded.Model(revised);
        var revisedId = superseded.Step.State.GetProperty("pendingRepairContext").GetProperty("proposalContributionId").GetString()!;
        check(superseded.IsTool && superseded.PendingTool == "program.source.structure" && revisedId != oldId &&
            superseded.Step.State.GetProperty("contributions").EnumerateArray().All(source => source.GetProperty("id").GetString() != oldId),
            "superseding context-derived draft replaces the original identity and requires its own preparation");
        superseded.PreparedStructure();
        var revisedAction = ActionFromActualContext(superseded);
        check(revisedAction.GetProperty("arguments").GetProperty("newText").GetString()!.EndsWith("// revised", StringComparison.Ordinal),
            "actual next context contains the superseding original, not a reconstructed old draft");

        var changed = Prepared(full: true); var changedId = Id(changed);
        changed.SourceWindow(6, 5, " changed");
        Absent(changed, changedId, "new observed preimage");
        check(changed.Step.State.GetProperty("structurePreparation").ValueKind == JsonValueKind.Null &&
            changed.Step.State.GetProperty("structuralIntent").ValueKind == JsonValueKind.Null,
            "changed source cannot inherit previous preparation or editing intent");

        var cancelled = Prepared(); var cancelId = Id(cancelled); cancelled.Cancel(); Absent(cancelled, cancelId, "cancellation");
        var overflow = Prepared(); var overflowId = Id(overflow);
        overflow.ContextRejected(ContextProjectionStatus.RequiredOverflow); Absent(overflow, overflowId, "required-context overflow");
        var blocked = Prepared(); var blockId = Id(blocked);
        blocked.Model(new { action = "blocked", reason = "No justified repair." }); Absent(blocked, blockId, "terminal model block");
        var failed = New(); failed.SourceWindow(1, 20); Initial(failed);
        var failedId = Id(failed); failed.Tool(false, "structure unavailable");
        Absent(failed, failedId, "failed preparation");
        var unknown = Prepared(full: true); var unknownId = Id(unknown);
        unknown.Model(ActionFromActualContext(unknown)); Absent(unknown, unknownId, "mutation dispatch");
        unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } &&
            unknown.Step.State.GetProperty("editCount").GetInt32() == 0 &&
            Pending(unknown).ValueKind == JsonValueKind.Null,
            "unknown mutation cannot gain edit credit or revive/replay a retired proposal");

        var success = Prepared(full: true); var successId = Id(success);
        success.Model(new { action = "done", summary = "Request verification without applying the draft." });
        check(success.IsTool && success.PendingTool == "process.run", "abandonment can follow the existing ordinary verification path");
        success.Tool(true, "Build succeeded", 0);
        check(success.Step.Next is CompleteWorkflow { Success: true }, "direct CompleteVerified success remains available");
        Absent(success, successId, "direct successful completion");

        var independent = New(); independent.SourceWindow(1, 5); independent.SourceWindow(10, 3); independent.SourceWindow(13, 1);
        var independentHead = independent.Step.State.GetProperty("readProjectionReferences").EnumerateArray()
            .Single(reference => reference.GetProperty("source").GetProperty("startLine").GetInt32() == 1).GetProperty("contributionId").GetString()!;
        var independentUnit = independent.Step.State.GetProperty("readProjectionReferences").EnumerateArray()
            .Single(reference => reference.GetProperty("source").GetProperty("startLine").GetInt32() == 10).GetProperty("contributionId").GetString()!;
        Initial(independent, beforeProposal: () => independent.ChangeState(state =>
        {
            foreach (var source in state["contributions"]!.AsArray())
                if (source!["id"]!.GetValue<string>() is var id && (id == independentHead || id == independentUnit))
                    source["requirement"] = JsonSerializer.SerializeToNode(ContextRequirement.Required, CerebralJson.Options);
            state["contributions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new ContextContribution("independent-control",
                "pegasus.programming", "independent-required", 1, ContextRequirement.Required,
                [new ModelInputMessage("user", [new TextContentPart("Independent required constraint.")])], null, true), CerebralJson.Options));
        }));
        check(independent.Step.State.GetProperty("contributions").EnumerateArray()
            .Where(source => new[] { independentHead, independentUnit, "independent-control" }.Contains(source.GetProperty("id").GetString()))
            .All(source => source.Deserialize<ContextContribution>(CerebralJson.Options)!.Requirement == ContextRequirement.Required),
            "repair refresh does not demote an independently Required source outside the unit or an unrelated Required contribution");
        check(Pending(independent).GetProperty("requirementOverrides").EnumerateArray()
            .All(row => row.GetProperty("contributionId").GetString() != independentHead &&
                row.GetProperty("contributionId").GetString() != independentUnit &&
                row.GetProperty("contributionId").GetString() != "independent-control"),
            "only flags actually promoted by this repair acquire bounded prior-Requirement metadata");
        var independentId = Id(independent); independent.Cancel(); Absent(independent, independentId, "independent-Requirement cancellation");
        check(independent.Step.State.GetProperty("contributions").EnumerateArray()
            .Where(source => new[] { independentHead, independentUnit, "independent-control" }.Contains(source.GetProperty("id").GetString()))
            .All(source => source.Deserialize<ContextContribution>(CerebralJson.Options)!.Requirement == ContextRequirement.Required),
            "retirement preserves independently Required source-unit and unrelated contributions");

        var prior = Prepared();
        prior.Model(ActionFromActualContext(prior)); prior.SourceReply(9, 5);
        var priorId = Id(prior);
        var overrides = Pending(prior).GetProperty("requirementOverrides").EnumerateArray().Select(row => row.Clone()).ToArray();
        check(overrides.Any(row => row.GetProperty("contributionId").GetString() != priorId) && overrides.All(row => row.GetProperty("identitySha256").GetString()!.Length == 64),
            "temporary pins record bounded immutable identity and prior flags without source or draft copies");
        prior.Cancel(); Absent(prior, priorId, "prior-flag cancellation");
        check(overrides.Where(row => row.GetProperty("contributionId").GetString() != priorId).All(row =>
            prior.Step.State.GetProperty("contributions").EnumerateArray().Single(source => source.GetProperty("id").GetString() ==
                row.GetProperty("contributionId").GetString()).Deserialize<ContextContribution>(CerebralJson.Options)!.Requirement ==
            row.GetProperty("priorRequirement").Deserialize<ContextRequirement>(CerebralJson.Options)),
            "still-exact surviving contributions regain their original Requirement on retirement");

        var replacedPin = Prepared();
        replacedPin.Model(ActionFromActualContext(replacedPin)); replacedPin.SourceReply(9, 5);
        var pin = Pending(replacedPin).GetProperty("requirementOverrides").EnumerateArray()
            .First(row => row.GetProperty("contributionId").GetString() != Id(replacedPin));
        var pinId = pin.GetProperty("contributionId").GetString()!;
        replacedPin.ChangeState(state =>
        {
            var contribution = state["contributions"]!.AsArray().Single(source => source!["id"]!.GetValue<string>() == pinId)!;
            contribution["sourceRevision"] = contribution["sourceRevision"]!.GetValue<long>() + 1;
        });
        replacedPin.Cancel();
        check(replacedPin.Step.State.GetProperty("contributions").EnumerateArray().Single(source =>
            source.GetProperty("id").GetString() == pinId).Deserialize<ContextContribution>(CerebralJson.Options)!.Requirement == ContextRequirement.Required,
            "retirement cannot restore a stale override onto a newer contribution with the same ID");

        var pressure = Prepared(full: true); var pressureId = Id(pressure);
        pressure.SeedMappingPressure(63);
        check(pressure.Step.State.GetProperty("readProjectionReferences").GetArrayLength() == 64,
            "mapping-pressure fixture uses the existing exact64-reference bound");
        pressure.ReadRequest(1, 1, "Overflow.cs"); pressure.OtherSourceReply("Overflow.cs");
        Absent(pressure, pressureId, "protected mapping eviction");
        check(pressure.Step.State.GetProperty("readProjectionReferences").GetArrayLength() == 64 &&
            pressure.Step.State.GetProperty("structuralIntent").ValueKind == JsonValueKind.Null &&
            pressure.Step.State.GetProperty("contributions").EnumerateArray().Any(source =>
                source.GetProperty("category").GetString() == "repair-diagnostic" &&
                source.GetRawText().Contains("source-mapping limit", StringComparison.Ordinal)),
            "mapping loss retires the pending unit with an explicit diagnostic under the same bound, never claims continuity");

        var clipped = New(); clipped.SourceWindow(1, 20); Initial(clipped, new string('x', 9000));
        check(clipped.IsModel && Pending(clipped).ValueKind == JsonValueKind.Null &&
            clipped.Step.State.GetProperty("editCount").GetInt32() == 0 &&
            clipped.Messages.Contains("complete original", StringComparison.Ordinal),
            "clipped original cannot become exact preparation through a raw-code cache or hidden reconstruction");
        var lost = Prepared(full: true); var lostId = Id(lost);
        lost.ChangeState(state =>
        {
            var contributions = state["contributions"]!.AsArray();
            contributions.Remove(contributions.Single(source => source!["id"]!.GetValue<string>() == lostId));
        });
        lost.ReadRequest(6, 1); lost.SourceReply(6, 1);
        Absent(lost, lostId, "missing original body");
        check(lost.IsModel && lost.Context.Contributions.All(source => source.Id != lostId),
            "metadata alone does not restore a lost proposal into actual context");
    }
    private sealed partial class Fixture
    {
        public void OtherSourceReply(string fileName) => Tool(true, JsonSerializer.Serialize(new
        {
            path = Path.Combine(Root, fileName), sha256 = SourceHash(), startLine = 1, totalLines = 20, content = "// line 1"
        }), truncated: true);

        // Fault-injected restored state, using genuine harness-produced exact mappings/bodies.
        // This isolates mapping capacity from the independent context-size compaction threshold.
        public void SeedMappingPressure(int count)
        {
            var state = JsonNode.Parse(Step.State.GetRawText())!.AsObject();
            for (var index = 0; index < count; index++)
            {
                var producer = new Fixture(rootPath: Root, guardFinalization: true);
                producer.sequence = 1000 + index * 10;
                var fileName = "Other" + index + ".cs";
                producer.ReadRequest(1, 1, fileName); producer.OtherSourceReply(fileName);
                var reference = producer.Step.State.GetProperty("readProjectionReferences").EnumerateArray().Single();
                var id = reference.GetProperty("contributionId").GetString();
                var body = producer.Step.State.GetProperty("contributions").EnumerateArray().Single(source => source.GetProperty("id").GetString() == id);
                state["readProjectionReferences"]!.AsArray().Add(JsonNode.Parse(reference.GetRawText()));
                var contribution = JsonNode.Parse(body.GetRawText())!;
                contribution["requirement"] = JsonSerializer.SerializeToNode(ContextRequirement.Reducible, CerebralJson.Options);
                state["contributions"]!.AsArray().Add(contribution);
            }
            Step = new(CerebralJson.Element(state), Step.Next);
        }
    }

}
