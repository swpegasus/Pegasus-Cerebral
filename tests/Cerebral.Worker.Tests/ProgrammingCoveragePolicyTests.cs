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
    private static void CoverageProgressPolicy(Action<bool, string> check)
    {
        Fixture New() => new(requiredChecks: ["build"], selectedTools: ["file.read", "file.write", "program.source.structure", "process.run"],
            guardFinalization: true);
        JsonElement Coverage(Fixture f) => f.Step.State.GetProperty("lastReadCoverage");
        string Selected(Fixture f) => f.Step.State.GetProperty("workingSource").GetProperty("receiptId").GetString()!;
        void Focus(Fixture f, string receipt) => f.Model(new { action = "focus-source", receiptId = receipt });
        string OriginalBody(Fixture f, string receipt) => ((TextContentPart)f.Context.Contributions.Single(source => source.ExactReadHandle == receipt).Messages.Single().Content.Single()).Text;

        var union = New();
        union.CoverageWindow(400, 18, 18);
        union.CoverageWindow(1, 417, 143); // A bounded provider fixture; actual Core clipping has its separate native/projection checks.
        check(union.Progress().GetProperty("sourceGuidance").GetProperty("physicalVisibleThrough").GetInt32() == 143 &&
            union.Progress().GetProperty("sourceGuidance").GetProperty("requestedLineCount").GetInt32() == 417,
            "displayed physical prefix remains143 despite requested417");
        union.CoverageWindow(144, 60, 60); union.CoverageWindow(204, 60, 60);
        var guidance = union.Progress().GetProperty("sourceGuidance");
        var intervals = guidance.GetProperty("activePhysicalIntervals");
        check(intervals.GetArrayLength() == 2 && intervals[0].GetProperty("startLine").GetInt32() == 1 &&
            intervals[0].GetProperty("endLine").GetInt32() == 263 && intervals[1].GetProperty("startLine").GetInt32() == 400,
            "same-preimage adjacent displayed spans merge while the136-line gap remains");
        check(guidance.GetProperty("nextUncoveredRange").GetProperty("startLine").GetInt32() == 264 &&
            guidance.GetProperty("nextUncoveredRange").GetProperty("lineCount").GetInt32() == 60,
            "next range uses the full active union rather than an already-covered continuation");
        union.CoverageWindow(140, 30, 30);
        check(Coverage(union).GetProperty("newlyPresentPhysicalLines").GetInt32() == 0 &&
            Coverage(union).GetProperty("alreadyPresentPhysicalLines").GetInt32() == 30 &&
            Coverage(union).GetProperty("classification").GetString() == "already-visible-overlap" &&
            union.Step.State.GetProperty("noCoverageGainReads").GetInt32() == 1,
            "differently bounded overlap is admitted but receives no physical-coverage gain");
        check(union.Step.State.GetProperty("editCount").GetInt32() == 0 && union.CheckCount == 0,
            "coverage observations grant no implementation or verification credit");
        union.Dotnet("build"); union.Tool(false, "actual fixture compiler failure CS1022", 1);
        union.ContextRejected(ContextProjectionStatus.NeedsCompaction); union.CompactSuccess();
        check(union.Progress().GetProperty("sourceGuidance").GetProperty("activePhysicalIntervals").GetArrayLength() == 1,
            "compaction guidance prunes vanished original bodies despite historical coverage");
        union.CoverageWindow(1, 150, 143);
        check(Coverage(union).GetProperty("newlyPresentPhysicalLines").GetInt32() == 139 &&
            Coverage(union).GetProperty("reintroducedPhysicalLines").GetInt32() == 139 &&
            Coverage(union).GetProperty("classification").GetString() == "reacquired" &&
            union.Step.State.GetProperty("repeatedReadObservations").GetInt32() == 1,
            "missing head can reacquire139 lost lines while its last4 lines remain visible");
        union.RejectPrepared(ContextProjectionStatus.NeedsCompaction, new("fixture", "fixture", "1", "fixture", 8995, 16384, 4096, "fixture")
            { ReasoningReserveTokens = 1024, SafetyReserveTokens = 128 });
        check(union.IsCompact && union.Step.State.GetProperty("workingSource").GetProperty("startLine").GetInt32() == 1,
            "last read remains saved when preparation requests compaction before another fixture action");
        union.CompactionFailure(WorkflowEffectState.Failed);
        check(union.Step.Next is CompleteWorkflow { Success: false } && union.Step.State.GetProperty("editCount").GetInt32() == 0,
            "host compact failure remains terminal with no plugin retry or invented edit");

        var focus = New(); focus.SourceWindow(1, 5); var head = Selected(focus); var headBody = OriginalBody(focus, head);
        focus.SourceWindow(6, 5); var middle = Selected(focus);
        focus.Dotnet("build"); focus.Tool(false, "compiler failure", 1);
        var receipts = focus.Step.State.GetProperty("readReceipts").GetArrayLength();
        Focus(focus, head);
        check(focus.IsModel && Selected(focus) == head && OriginalBody(focus, head) == headBody &&
            focus.Context.Contributions.Count(source => source.Requirement == ContextRequirement.Required && source.ExactReadHandle is not null &&
                focus.Step.State.GetProperty("readProjectionReferences").EnumerateArray().Any(reference => reference.GetProperty("contributionId").GetString() == source.Id)) == 1,
            "focus selects one original exact body without adding another Required source window");
        check(focus.Context.Contributions.Single(source => source.ExactReadHandle == middle).Requirement == ContextRequirement.Reducible &&
            focus.Context.Contributions.Count(source => source.Requirement == ContextRequirement.Required) == 2 &&
            !focus.Context.Contributions.Any(source => source.Category == "model-output"),
            "focus demotes old source, retains latest non-source result and removes executed focus JSON");
        check(focus.Step.State.GetProperty("readReceipts").GetArrayLength() == receipts && focus.CheckCount == 0 &&
            focus.Step.State.GetProperty("editCount").GetInt32() == 0,
            "selection is a normal next model step with no read/edit/check credit");
        focus.ContextRejected(ContextProjectionStatus.NeedsCompaction); focus.CompactSuccess();
        check(Selected(focus) == head && OriginalBody(focus, head) == headBody, "selected original survives adopted compaction byte-for-byte");
        Focus(focus, middle);
        check(focus.IsModel && Selected(focus) == head && focus.Step.State.GetProperty("repairs").GetInt32() == 1,
            "focus cannot retrieve a lost historical receipt body");
        focus.SourceWindow(11, 5);
        check(Selected(focus) != head, "a genuine later read selects its new source window");
        var noProgress = New(); noProgress.SourceWindow(1, 5); var same = Selected(noProgress);
        for (var i = 0; i < 3; i++) Focus(noProgress, same);
        check(noProgress.Step.Next is CompleteWorkflow { Success: false }, "same-window focus stops through existing bounded correction allowance");

        var forged = New(); forged.SourceWindow(1, 5); var forgedReceipt = Selected(forged); forged.SourceWindow(6, 5);
        forged.ChangeState(state => state["readProjectionReferences"]![0]!["source"]!["startLine"] = 999);
        Focus(forged, forgedReceipt);
        check(forged.IsModel && Selected(forged) != forgedReceipt, "forged span mapping cannot select an original body");
        var mixed = New(); mixed.SourceWindow(1, 5); var old = Selected(mixed); mixed.SourceWindow(6, 5, " changed");
        Focus(mixed, old);
        check(mixed.IsModel && Selected(mixed) != old &&
            mixed.Progress().GetProperty("sourceGuidance").GetProperty("activePhysicalIntervals")[0].GetProperty("startLine").GetInt32() == 6,
            "different whole-file preimages never merge or allow stale focus");
        var legacy = New(); legacy.SourceWindow(1, 5);
        legacy.ChangeState(state => { state["workingSource"] = null; foreach (var reference in state["readProjectionReferences"]!.AsArray()) reference!.AsObject().Remove("source"); });
        legacy.SourceWindow(6, 5);
        check(Coverage(legacy).GetProperty("newlyPresentPhysicalLines").GetInt32() == 5,
            "legacy missing source maps cannot claim current physical coverage");
        legacy.ChangeState(state => { foreach (var observation in state["readHistory"]!.AsArray()) observation!.AsObject().Remove("span"); state["readHistoryTruncated"] = true; });
        legacy.SourceWindow(11, 5);
        check(Coverage(legacy).GetProperty("newToRetainedHistoryPhysicalLines").ValueKind == JsonValueKind.Null &&
            Coverage(legacy).GetProperty("classification").GetString() == "extends-current-coverage-history-unknown",
            "absent or evicted historical span metadata stays unknown rather than globally new");
        var badBody = New(); badBody.SourceWindow(1, 5); badBody.SourceWindow(6, 5);
        badBody.ChangeState(state => state["readProjectionReferences"]![0]!["bodySha256"] = new string('f', 64));
        badBody.SourceWindow(2, 2);
        check(Coverage(badBody).GetProperty("newlyPresentPhysicalLines").GetInt32() == 2,
            "a remembered coordinate with mismatched actual body hash cannot count as present");

        var early = New(); early.SourceWindow(1, 5); early.SourceWindow(6, 5);
        early.RejectPrepared(ContextProjectionStatus.NeedsCompaction, new("fixture", "fixture", "1", "fixture", 8995, 16384, 4096, "fixture")
            { ReasoningReserveTokens = 1024, SafetyReserveTokens = 128 });
        check(early.IsCompact && early.Step.State.GetProperty("compactionReason").GetString()!.Contains("early compaction", StringComparison.Ordinal),
            "typed fitting full reservation is reported as early pressure");
        var hard = New(); hard.SourceWindow(1, 5); hard.SourceWindow(6, 5);
        hard.RejectPrepared(ContextProjectionStatus.NeedsCompaction, new("fixture", "fixture", "1", "fixture", 12000, 16384, 4096, "fixture")
            { ReasoningReserveTokens = 1024, SafetyReserveTokens = 128 });
        check(hard.IsCompact && hard.Step.State.GetProperty("compactionReason").GetString()!.Contains("full request does not fit", StringComparison.OrdinalIgnoreCase),
            "typed full reservation exceeding capacity is reported separately");
        var unknown = New(); unknown.SourceWindow(1, 5); unknown.SourceWindow(6, 5); unknown.RejectPrepared(ContextProjectionStatus.NeedsCompaction, null);
        check(unknown.IsCompact && unknown.Step.State.GetProperty("compactionReason").GetString()!.Contains("unavailable", StringComparison.Ordinal),
            "missing legacy prepared metadata produces a neutral compaction reason");
        var floor = New(); floor.SourceWindow(1, 5); floor.RejectPrepared(ContextProjectionStatus.RequiredOverflow, null);
        check(floor.Step.Next is CompleteWorkflow { Success: false } result && result.Error!.Contains("required floor", StringComparison.Ordinal),
            "RequiredOverflow labels required-floor evidence and remains terminal");

        var builds = New(); builds.SourceWindow(1, 5);
        builds.Dotnet("build"); builds.Tool(false, "compiler failure", 1, error: "CS1002 fixture");
        check(builds.Step.State.GetProperty("lastFailedBuild").GetProperty("matchingFailures").GetInt32() == 1, "confirmed failed build creates one limited observation");
        builds.Process(["build", "Hello.csproj", "--configuration", "Release"]); builds.Tool(false, "compiler failure", 1, error: "CS1002 fixture");
        check(builds.Step.State.GetProperty("lastFailedBuild").GetProperty("matchingFailures").GetInt32() == 2 && builds.CheckCount == 0,
            "equivalent accepted argument aliases at same EditCount advise but do not ban actual execution");
        builds.SourceWindow(6, 5); builds.ContextRejected(ContextProjectionStatus.NeedsCompaction); builds.CompactSuccess();
        check(builds.Progress().GetProperty("failedBuildAdvice").GetProperty("matchingFailures").GetInt32() == 2,
            "reads and compaction preserve limited failed-build advice");
        builds.Dotnet("build", "Debug"); builds.Tool(false, "compiler failure", 1, error: "CS1002 fixture");
        check(builds.Step.State.GetProperty("lastFailedBuild").GetProperty("matchingFailures").GetInt32() == 1,
            "different accepted configuration has a distinct command observation");
        builds.Write(); builds.Dotnet("build", "Debug"); builds.Tool(false, "compiler failure", 1, error: "CS1002 fixture");
        check(builds.Step.State.GetProperty("lastFailedBuild").GetProperty("matchingFailures").GetInt32() == 1 &&
            builds.Step.State.GetProperty("lastFailedBuild").GetProperty("editGeneration").GetInt32() == 1,
            "actual mutation establishes a new source edit generation");
        builds.Dotnet("build"); builds.Tool(true, "Build succeeded", 0);
        check(builds.Step.State.GetProperty("lastFailedBuild").ValueKind == JsonValueKind.Null && builds.CheckCount == 1,
            "real-shaped successful check clears failed-build advice through unchanged admission");
        var partial = New(); partial.Read(); partial.Dotnet("build"); partial.Tool(false, "partial compiler stream", 1, truncated: true);
        check(partial.Step.State.GetProperty("lastFailedBuild").ValueKind == JsonValueKind.Null && partial.CheckCount == 0,
            "partial failed execution does not acquire complete diagnostic-match credit");
    }

    private sealed partial class Fixture
    {
        public JsonElement Progress()
        {
            var text = Command.Arguments.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            const string marker = "Accepted project and host progress:\n";
            using var document = JsonDocument.Parse(text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
            return document.RootElement.Clone();
        }
        public void CoverageWindow(int start, int requested, int returned)
        {
            ReadRequest(start, requested);
            var all = string.Join('\n', Enumerable.Range(1, 417).Select(line => "// line " + line));
            Tool(true, JsonSerializer.Serialize(new
            {
                path = Path.Combine(Root, "Program.cs"), sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all))).ToLowerInvariant(),
                startLine = start, totalLines = 417, content = string.Join('\n', all.Split('\n').Skip(start - 1).Take(returned))
            }), truncated: start - 1 + returned < 417);
        }
        public void RejectPrepared(ContextProjectionStatus status, PreparedModelMetadata? metadata)
        {
            var projection = Projection(status) with { Metadata = metadata };
            Result(WorkflowEffectState.Failed, CerebralJson.Element(new { context = projection }), "typed fixture context preparation");
        }
    }
}
