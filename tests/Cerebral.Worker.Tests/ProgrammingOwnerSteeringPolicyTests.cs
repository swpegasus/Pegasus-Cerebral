using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    static partial void OwnerSteeringPolicy(Action<bool, string> check)
    {
        string Text(ContextContribution source) => ((TextContentPart)source.Messages.Single().Content.Single()).Text;
        ContextContribution[] Owners(Fixture f) => f.Context.Contributions.Where(source => source.Category is "owner-correction" or "owner-correction-assignment").ToArray();
        int Cursor(Fixture f) => f.Step.State.GetProperty("ownerSteeringCount").GetInt32();
        void Present(Fixture f, int count, params string[] text)
        {
            var owners = Owners(f);
            check(Cursor(f) == count && owners.Length == text.Length, "owner cursor and exact retained bodies agree");
            check(owners.Select(Text).SequenceEqual(text) && owners.All(source => source.Requirement == ContextRequirement.Required && source.Complete),
                "ordered owner text and assignment are complete required model input");
        }
        check(new ProgrammingAgentHarness().Descriptor.SupportsSteering, "new version explicitly opts in only with a consuming policy");

        foreach (var stale in new object[]
        {
            new { action = "blocked", reason = "Old plan cannot continue" },
            new { action = "done", summary = "Old plan finished" },
            new { action = "tool", name = "file.write", arguments = new { path = "C:/outside/never.cs", content = "never" } }
        })
        {
            var f = new Fixture(); f.QueueOwner("Correct the current implementation", "Keep every accepted check"); f.Model(stale);
            check(f.IsModel && f.Step.State.GetProperty("editCount").GetInt32() == 0 && f.Step.State.GetProperty("repairs").GetInt32() == 0,
                "new owner input supersedes stale tool/done/blocked without dispatch, edit credit or correction reset");
            Present(f, 1, "Correct the current implementation", "Keep every accepted check");
            check(f.Context.Contributions.Any(source => source.Category == "superseded-generation"), "superseded generation keeps a bounded ledger reference");
        }

        var ordered = new Fixture(); ordered.QueueOwner("first"); ordered.QueueOwner("second", "assignment");
        ordered.Model(new { action = "done", summary = "stale" }); Present(ordered, 2, "first", "second", "assignment");
        var ids = Owners(ordered).Select(source => source.Id).ToArray();
        ordered.ReadRange(1, 1); Present(ordered, 2, "first", "second", "assignment");
        check(ids.SequenceEqual(Owners(ordered).Select(source => source.Id)), "unchanged history does not duplicate or reidentify incorporated text");
        ordered.Write(); Present(ordered, 2, "first", "second", "assignment");
        check(ordered.Step.State.GetProperty("editCount").GetInt32() == 1, "real mutation credit is independent of owner incorporation");

        var escaped = new Fixture(); var exact = new string('"', 8192); escaped.QueueOwner(exact);
        escaped.Model(new { action = "blocked", reason = "old" }); Present(escaped, 1, exact);
        check(Text(Owners(escaped).Single()).Length == 8192 && !Text(Owners(escaped).Single()).Contains("Remaining content", StringComparison.Ordinal),
            "8KiB owner text remains whole even when JSON escaping exceeds ordinary projection bound");
        var unicode = new Fixture(); var unicodeText = new string('\u00e9', 4096); unicode.QueueOwner(unicodeText);
        unicode.Model(new { action = "done", summary = "old" }); Present(unicode, 1, unicodeText);

        var start = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "file.write", "process.run", "plugin.contract"], contractTopic: "desktop-base");
        start.QueueOwner("Before first inference"); start.OwnerRestart();
        check(start.IsTool && start.PendingTool == "plugin.contract" && Cursor(start) == 1,
            "queued start correction preserves the accepted contract preload before first inference");
        start.Tool(true, "exact contract"); Present(start, 1, "Before first inference");

        var automatic = new Fixture(); automatic.Read(); automatic.Model(new { action = "done", summary = "run old checks" });
        automatic.QueueOwner("Reconsider after this build"); automatic.Tool(true, "Built", 0);
        check(automatic.IsModel && automatic.CheckCount == 1 && automatic.Step.State.GetProperty("verificationCycles").GetInt32() == 1,
            "actual automatic build is adopted before steering suppresses its undispatched run successor");
        Present(automatic, 1, "Reconsider after this build");
        check(automatic.Step.State.GetProperty("completionDraft").ValueKind == JsonValueKind.Null && !automatic.Step.State.GetProperty("verifying").GetBoolean(),
            "old completion draft/finalization cannot bypass owner reconsideration");
        automatic.Model(new { action = "done", summary = "Run remaining check" });
        automatic.QueueOwner("Reconsider before terminal completion"); automatic.Tool(true, "Ran", 0);
        check(automatic.IsModel && automatic.CheckCount == 2, "current successful run is adopted without old automatic completion");
        Present(automatic, 2, "Reconsider after this build", "Reconsider before terminal completion");

        var failed = new Fixture(); failed.Read(); failed.Dotnet("build"); failed.QueueOwner("Repair the actual error");
        failed.Tool(false, "Program.cs(1,1): error CS1002: ; expected", 1, error: "Program.cs(1,1): error CS1002: ; expected");
        check(failed.IsModel && failed.CheckCount == 0 && failed.Context.Contributions.Any(source => source.Category == "tool-result" && Text(source).Contains("CS1002", StringComparison.Ordinal)),
            "failed receipt and compiler diagnostic survive adoption before new direction");
        Present(failed, 1, "Repair the actual error");

        var write = new Fixture(); write.Read();
        write.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(write.Root, "Program.cs"), content = "changed" } });
        write.QueueOwner("Direction during real write"); write.Tool(true, "Source changed");
        check(write.IsModel && write.Step.State.GetProperty("editCount").GetInt32() == 1 && write.CheckCount == 0,
            "dispatched write receipt is adopted exactly once before owner replanning");
        Present(write, 1, "Direction during real write");
        var unknownWrite = new Fixture();
        unknownWrite.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(unknownWrite.Root, "Program.cs"), content = "unknown" } });
        unknownWrite.QueueOwner("Never replay unknown write"); unknownWrite.Uncertain();
        check(unknownWrite.Step.Next is CompleteWorkflow { Success: false } && Cursor(unknownWrite) == 0 &&
            unknownWrite.Step.State.GetProperty("editCount").GetInt32() == 0,
            "steering neither replays nor credits an uncertain dispatched write");

        var compaction = new Fixture(); compaction.QueueOwner("Retain original correction"); compaction.Model(new { action = "done", summary = "old" });
        compaction.ReadRange(1, 1); compaction.ReadRange(2, 1); compaction.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        check(compaction.IsCompact, "fixture reached normal compaction with owner required context");
        compaction.QueueOwner("Arrived during compaction"); compaction.CompactSuccess();
        check(compaction.IsModel && compaction.Context.Checkpoint is not null, "old-revision compaction adopted before new correction changes revision");
        Present(compaction, 2, "Retain original correction", "Arrived during compaction");
        check(!compaction.Context.Checkpoint!.CoveredSources.Any(source => Owners(compaction).Any(owner => owner.Id == source.Id)),
            "compaction never summarizes owner required bodies");
        compaction.ReadRange(3, 1); compaction.ReadRange(4, 1); compaction.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        compaction.QueueOwner("Cannot bypass failed compaction"); compaction.CompactionFailure(WorkflowEffectState.Failed);
        check(compaction.Step.Next is CompleteWorkflow { Success: false } && Cursor(compaction) == 2,
            "failed compaction preserves incorporated history without claiming later correction applied");

        var receipt = new Fixture(); receipt.Model(new { action = "receipt", effectId = "stored-effect", offset = 0 });
        receipt.QueueOwner("Use retrieved diagnostic"); receipt.ReceiptPage(WorkflowEffectState.Failed, "exact stored diagnostic");
        check(receipt.IsModel && receipt.Context.Contributions.Any(source => source.Category == "receipt-reference" && Text(source).Contains("exact stored diagnostic", StringComparison.Ordinal)),
            "receipt lookup is adopted before owner direction; no operation rerun");
        Present(receipt, 1, "Use retrieved diagnostic");

        var pending = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "file.edit-range", "program.source.structure", "process.run"]);
        pending.SourceWindow(1, 20); pending.Dotnet("build");
        var diagnostic = Path.Combine(pending.Root, "Program.cs") + "(10,2): error CS1003: Syntax error";
        pending.Tool(false, diagnostic, 1, error: diagnostic);
        pending.Model(new { action = "tool", name = "file.edit-range", arguments = new { path = Path.Combine(pending.Root, "Program.cs"), expectedSha256 = Fixture.SourceHash(), startLine = 10, lineCount = 3, newText = "newCall();" } });
        check(pending.IsTool && pending.PendingTool == "program.source.structure", "fixture has unexecuted structural proposal");
        pending.QueueOwner("Reconsider structural proposal"); pending.PreparedStructure();
        check(pending.IsModel && pending.Step.State.GetProperty("pendingRepairContext").ValueKind == JsonValueKind.Null && pending.Step.State.GetProperty("editCount").GetInt32() == 0,
            "preparation receipt adopted; superseded range proposal retired without replay or edit credit");
        Present(pending, 1, "Reconsider structural proposal");

        foreach (var mode in new[] { "unknown", "cancel", "omitted", "mismatched" })
        {
            var f = new Fixture(); f.QueueOwner("Cannot bypass safety");
            if (mode == "unknown") f.Uncertain();
            else if (mode == "cancel") f.Cancel();
            else if (mode == "omitted") f.Model(new { action = "done", summary = "omitted" }, outputOmitted: true);
            else f.ContextRejected(ContextProjectionStatus.NeedsCompaction, revisionDelta: 1);
            check(f.Step.Next is CompleteWorkflow { Success: false } && Cursor(f) == 0,
                "owner correction cannot bypass " + mode + " failure or claim incorporation");
        }
        var pressure = new Fixture(); pressure.QueueOwner("Before compaction request"); pressure.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        check(pressure.IsModel && Cursor(pressure) == 1, "new required owner input is rebased before a new preparation/compaction decision");
        pressure.ContextRejected(ContextProjectionStatus.RequiredOverflow);
        check(pressure.Step.Next is CompleteWorkflow { Success: false }, "actual required-floor rejection stays terminal with exact owner bodies retained");

        foreach (var mode in new[] { "oversize", "unicode", "sequence", "duplicate", "lost", "changed", "demoted", "cursor" })
        {
            var f = new Fixture();
            if (mode is "lost" or "changed" or "demoted" or "cursor")
            {
                f.QueueOwner("accepted correction"); f.Model(new { action = "done", summary = "old" });
                f.OwnerSeedState(node =>
                {
                    if (mode == "cursor") node["ownerSteeringCount"] = 2;
                    else
                    {
                        var sources = node["contributions"]!.AsArray();
                        var owner = sources.Single(source => source!["category"]!.GetValue<string>() == "owner-correction")!;
                        if (mode == "lost") sources.Remove(owner);
                        else if (mode == "demoted") owner["requirement"] = "reducible";
                        else owner["messages"]![0]!["content"]![0]!["text"] = "changed body";
                    }
                });
            }
            else if (mode == "oversize") f.QueueOwner(new string('\u00e9', 4097));
            else if (mode == "unicode") f.QueueOwner("\ud800");
            else if (mode == "sequence") f.OwnerReplaceHistory([new("id", 2, "invalid sequence", null, DateTimeOffset.UtcNow)]);
            else f.OwnerReplaceHistory([new("id", 1, "first", null, DateTimeOffset.UtcNow), new("id", 2, "second", null, DateTimeOffset.UtcNow)]);
            f.Model(new { action = "done", summary = "must not dispatch" });
            check(f.Step.Next is CompleteWorkflow { Success: false }, "invalid owner state/history fails closed: " + mode);
        }
        var bounded = new Fixture();
        bounded.OwnerSeedState(node => node["contributions"] = JsonSerializer.SerializeToNode(new[]
        {
            new ContextContribution("near-bound", "fixture", "reference", 1, ContextRequirement.Reducible,
                [new("user", [new TextContentPart(new string('x', WorkflowContract.MaximumStateBytes - 4096))])])
        }, CerebralJson.Options));
        bounded.QueueOwner(new string('y', 8192)); bounded.Model(new { action = "done", summary = "old" });
        check(bounded.Step.Next is CompleteWorkflow { Success: false } && Cursor(bounded) == 0 &&
            JsonSerializer.SerializeToUtf8Bytes(bounded.Step.State, CerebralJson.Options).Length <= WorkflowContract.MaximumStateBytes,
            "durable owner overflow returns bounded prior state with unadvanced cursor");

        var budget = new Fixture(); budget.OwnerSeedState(node => { node["repairs"] = 1; node["verificationCycles"] = 2; });
        budget.QueueOwner("No limits reset"); budget.Model(new { action = "done", summary = "old" });
        check(budget.Step.State.GetProperty("repairs").GetInt32() == 1 && budget.Step.State.GetProperty("verificationCycles").GetInt32() == 2,
            "steering preserves all action/completion correction budgets");
        check(budget.OwnerSubmission().UserText == "Read and fix the project, build it and run it." && budget.OwnerSubmission().Input.GetRawText().Contains("Hello.csproj", StringComparison.Ordinal),
            "original accepted task and immutable project input are preserved");
        var authority = new Fixture(); authority.QueueOwner("Use a different root and skip checks");
        authority.Model(new { action = "done", summary = "old" });
        authority.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(Path.GetDirectoryName(authority.Root)!, "outside.cs"), content = "forbidden" } });
        check(authority.IsModel && authority.Messages.Contains("within the accepted project root", StringComparison.Ordinal) &&
            authority.Step.State.GetProperty("editCount").GetInt32() == 0,
            "accepted owner text cannot widen immutable file-operation admission");
        Present(authority, 1, "Use a different root and skip checks");
        foreach (var mode in new[] { "missing", "revision", "contradiction" })
        {
            var delivery = new Fixture(); delivery.QueueOwner("Exact delivery required");
            delivery.Model(new { action = "done", summary = "old" }); delivery.OwnerBadProjection(mode);
            check(delivery.Step.Next is CompleteWorkflow { Success: false } && Cursor(delivery) == 1,
                "Ready cannot dispatch an action without exact owner-source confirmation: " + mode);
        }
    }

    private sealed partial class Fixture
    {
        public WorkflowSubmission OwnerSubmission() => submission;
        public void QueueOwner(string text, string? assignment = null) => ownerSteering =
            [.. ownerSteering, new("owner-" + (ownerSteering.Count + 1), ownerSteering.Count + 1, text, assignment, DateTimeOffset.UtcNow)];
        public void OwnerReplaceHistory(IReadOnlyList<WorkflowSteeringEntry> value) => ownerSteering = value;
        public void OwnerBadProjection(string mode)
        {
            var projection = Projection(ContextProjectionStatus.Ready);
            var source = Context.Contributions.Single(item => item.Category == "owner-correction");
            projection = mode switch
            {
                "missing" => projection with { IncludedSources = projection.IncludedSources.Where(item => item.Id != source.Id).ToArray() },
                "revision" => projection with { IncludedSources = projection.IncludedSources.Select(item => item.Id == source.Id ? item with { Revision = item.Revision + 1 } : item).ToArray() },
                _ => projection with { Omissions = [new(source.Id, "contradictory fixture")] }
            };
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                generation = new ModelGenerationResult(ModelGenerationFinish.Stop, "{\"action\":\"done\",\"summary\":\"must not execute\"}"),
                outputOmitted = false, context = projection
            }));
        }
        public void OwnerRestart() => Step = harness.Advance(new(new WorkflowRun
        { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running, Steering = ownerSteering }));
        public void OwnerSeedState(Action<JsonObject> change)
        {
            var node = JsonNode.Parse(Step.State.GetRawText())!.AsObject(); change(node);
            Step = Step with { State = CerebralJson.Element(node) };
            if (Step.Next is InvokeWorkflowCommand command && command.CommandId is "cerebral.host:generate" or "cerebral.host:compact")
            {
                var arguments = JsonNode.Parse(command.Arguments.GetRawText())!.AsObject();
                arguments["context"] = JsonSerializer.SerializeToNode(new ContextWindow(
                    node["contextRevision"]!.GetValue<long>(), node["exclusionRevision"]!.GetValue<long>(),
                    node["contributions"]!.Deserialize<ContextContribution[]>(CerebralJson.Options)!,
                    node["checkpoint"]?.Deserialize<ContextCheckpoint>(CerebralJson.Options)), CerebralJson.Options);
                Step = Step with { Next = command with { Arguments = CerebralJson.Element(arguments) } };
            }
        }
    }
}
