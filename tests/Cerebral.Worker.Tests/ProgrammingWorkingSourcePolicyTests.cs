using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    public static int RunWorkingSourcePolicyChecks()
    {
        var checks = 0;
        WorkingSourcePolicy((passed, message) =>
        {
            if (!passed) throw new InvalidOperationException("Programming working source: " + message);
            checks++;
        });
        return checks;
    }

    // Deterministic state-transition fixtures, never model/runtime/source-file evidence.
    private static void WorkingSourcePolicy(Action<bool, string> check)
    {
        Fixture New() => new(requiredChecks: ["build"], selectedTools: ["file.read", "file.write", "program.source.structure", "process.run"],
            guardFinalization: true);
        JsonElement Working(Fixture f) => f.Step.State.GetProperty("workingSource");
        string Body(Fixture f, string id) => ((TextContentPart)f.Context.Contributions.Single(s => s.Id == id).Messages.Single().Content.Single()).Text;
        void Compact(Fixture f) { f.ContextRejected(ContextProjectionStatus.NeedsCompaction); check(f.IsCompact, "fixture has reducible history before compaction"); f.CompactSuccess(); check(f.IsModel, "candidate compaction adopted"); }

        var previousEdition = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "file.write", "program.source.structure", "process.run"]);
        previousEdition.SourceWindow(1, 5);
        check(Working(previousEdition).ValueKind == JsonValueKind.Null,
            "the admitted legacy edition retains its historical bounded-range parser behavior");
        var keep = New(); keep.SourceWindow(1, 5); keep.SourceWindow(6, 5);
        check(Working(keep).ValueKind == JsonValueKind.Object, "current typed bounded read seeds a working slot; payload=" + keep.LastContributionText[..Math.Min(400, keep.LastContributionText.Length)]);
        var working = Working(keep).Clone();
        var id = working.GetProperty("contributionId").GetString()!;
        var exact = Body(keep, id);
        check(working.GetProperty("startLine").GetInt32() == 6 && working.GetProperty("visibleLineCount").GetInt32() == 5 &&
            working.GetProperty("physicalLineCount").GetInt32() == 5 && working.GetProperty("requestedLineCount").GetInt32() == 5,
            "slot maps actual typed source path/hash/visible/requested physical coordinates");
        check(working.GetProperty("receiptId").GetString() == keep.Context.Contributions.Single(s => s.Id == id).ExactReadHandle &&
            working.GetProperty("sha256").GetString() == Fixture.SourceHash(),
            "working slot is bound to the real-shaped read receipt and whole-file preimage");
        keep.Dotnet("build"); keep.Tool(false, "Build FAILED. 1 Error(s)", 1);
        check(Body(keep, id) == exact && keep.Context.Contributions.Single(s => s.Id == id).Requirement == ContextRequirement.Required,
            "failed build retains the exact current source body as Required");
        keep.StructureResult();
        check(Body(keep, id) == exact && keep.Context.Contributions.Single(s => s.Id == id).Requirement == ContextRequirement.Required &&
            keep.Context.Contributions.Count(s => s.Requirement == ContextRequirement.Required) == 2,
            "structure result keeps one working body plus the latest result, not all historical reads");
        var reads = keep.Step.State.GetProperty("readReceipts").GetArrayLength();
        Compact(keep);
        check(Body(keep, id) == exact && Working(keep).GetProperty("contributionId").GetString() == id &&
            keep.Step.State.GetProperty("readSignatures").GetArrayLength() == 1, "adoption retains body and only surviving read signatures");
        check(keep.Step.State.GetProperty("readReceipts").GetArrayLength() == reads && keep.CheckCount == 0 &&
            keep.Step.State.GetProperty("editCount").GetInt32() == 0, "retention grants no read/edit/check credit");
        keep.ReadRequest(6, 5);
        check(keep.IsModel && keep.Step.State.GetProperty("repeatedReadAttempts").GetInt32() == 1,
            "same visible request is rejected and repeat attempts are recorded without a new cap");

        var replacement = New(); replacement.SourceWindow(1, 5); var old = Working(replacement).GetProperty("contributionId").GetString()!;
        replacement.SourceWindow(6, 5);
        check(Working(replacement).GetProperty("startLine").GetInt32() == 6 &&
            replacement.Context.Contributions.Single(s => s.Id == old).Requirement == ContextRequirement.Reducible,
            "selecting another window replaces the slot and releases the old body to ordinary compaction");
        Compact(replacement);
        check(!replacement.Context.Contributions.Any(s => s.Id == old) && replacement.Step.State.GetProperty("readHistory").GetArrayLength() == 2,
            "discarded body is absent while logical read history survives");
        replacement.SourceWindow(1, 5);
        check(replacement.IsModel && replacement.Step.State.GetProperty("repeatedReadObservations").GetInt32() == 1 &&
            replacement.Step.State.GetProperty("noNewEvidenceReads").GetInt32() == 1 &&
            replacement.Step.State.GetProperty("lastReadObservation").GetString() == "reacquired-previously-observed-source",
            "legitimate lost-body reacquisition is allowed and is not recorded as new visible evidence");
        replacement.StructureResult(); Compact(replacement);
        check(replacement.Step.State.GetProperty("repeatedReadObservations").GetInt32() == 1 &&
            replacement.Step.State.GetProperty("noNewEvidenceReads").GetInt32() == 1,
            "compaction does not erase no-progress/reacquisition history");

        var alias = New();
        alias.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); alias.Tool(true, alias.ProjectionResult());
        check(Working(alias).ValueKind == JsonValueKind.Object, "broad typed receipt seeds an honest visible prefix");
        var broad = Working(alias);
        var visible = broad.GetProperty("visibleLineCount").GetInt32();
        check(visible > 0 && visible < 176 && broad.GetProperty("totalReadLines").GetInt32() == 176 &&
            !alias.Context.Contributions.Last().Complete, "complete raw read retains only its actual bounded incomplete visible projection");
        alias.ReadRequest(1, visible, "GeneratedServicePlugin.cs"); alias.Tool(true, alias.ProjectionResult(1, visible), truncated: true);
        check(alias.Step.State.GetProperty("repeatedReadObservations").GetInt32() == 1,
            "broad and explicit requests returning identical displayed bytes are the same observed source evidence");
        Compact(alias);
        alias.ChangeState(state => state["lastDiagnosticBuildEditCount"] = 0); // Guard fixture follows an already attempted diagnostic check.
        alias.ReadRequest(1, 1000, "GeneratedServicePlugin.cs");
        check(alias.IsModel && alias.Step.State.GetProperty("repeatedReadAttempts").GetInt32() == 1,
            "historical broad-read alias is blocked only because its exact visible source body is present");
        alias.StructureResult(); Compact(alias);
        check(alias.Step.State.GetProperty("repeatedReadAttempts").GetInt32() == 1,
            "blocked repeat history survives compaction rather than resetting progress");

        var lost = New(); lost.SourceWindow(1, 5);
        var lostId = Working(lost).GetProperty("contributionId").GetString();
        lost.ChangeState(state =>
        {
            var contributions = state["contributions"]!.AsArray();
            contributions.Remove(contributions.Single(node => node!["id"]!.GetValue<string>() == lostId));
        });
        lost.ReadRequest(1, 5);
        check(lost.IsTool, "remembered hash/signature/receipt without its actual active body cannot prohibit needed reacquisition");
        lost.SourceReply(1, 5);
        check(lost.Step.State.GetProperty("repeatedReadObservations").GetInt32() == 1 && Working(lost).ValueKind == JsonValueKind.Object,
            "actual fresh read restores visibility while preserving the repeated-evidence observation");

        var legacy = New(); legacy.SourceWindow(1, 5);
        legacy.ChangeState(state => { foreach (var key in new[] { "workingSource", "readProjectionReferences", "readHistory", "readObservations",
            "repeatedReadObservations", "repeatedReadAttempts", "noNewEvidenceReads", "lastReadObservation" }) state.Remove(key); });
        legacy.ReadRequest(1, 5);
        check(legacy.IsTool, "old saved state without optional mappings permits conservative reacquisition");
        legacy.SourceReply(1, 5);
        check(Working(legacy).ValueKind == JsonValueKind.Object, "new actual evidence seeds a working slot after legacy recovery");

        var invalid = New(); invalid.SourceWindow(1, 5);
        invalid.ChangeState(state => state["readProjectionReferences"]![0]!["bodySha256"] = new string('f', 64));
        invalid.ReadRequest(1, 5);
        check(invalid.IsTool, "mismatched active-body hash is not visibility proof");

        var mutation = New(); mutation.SourceWindow(6, 5); mutation.SourceWindow(1, 5); var mutationId = Working(mutation).GetProperty("contributionId").GetString();
        mutation.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(mutation.Root, "Program.cs"), content = "changed" } });
        check(mutation.IsTool && Working(mutation).ValueKind == JsonValueKind.Null &&
            !mutation.Step.State.GetProperty("contributions").EnumerateArray().Any(s => s.GetProperty("id").GetString() == mutationId) &&
            mutation.Step.State.GetProperty("readProjectionReferences").GetArrayLength() == 0,
            "source mutation invalidates working body and visibility mappings BEFORE dispatch");
        mutation.Uncertain();
        check(mutation.Step.Next is CompleteWorkflow { Success: false } && Working(mutation).ValueKind == JsonValueKind.Null &&
            mutation.Step.State.GetProperty("readHistory").GetArrayLength() == 2,
            "uncertain mutation stays terminal/unreplayed without destroying logical history or restoring stale visibility");

        var metadata = New(); metadata.SourceWindow(1, 5);
        metadata.ChangeState(state => state["workingSource"]!["sha256"] = new string('f', 64));
        metadata.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = metadata.Root, arguments = new[] { "build", "Hello.csproj", "-c", "Release" } } });
        check(Working(metadata).ValueKind == JsonValueKind.Null,
            "metadata checksum mismatch cannot promote a remembered identity into current exact-source evidence");

        var changed = New(); changed.SourceWindow(1, 5);
        changed.SourceWindow(6, 5, suffix: " changed");
        check(changed.Step.State.GetProperty("readProjectionReferences").GetArrayLength() == 1 &&
            Working(changed).GetProperty("sha256").GetString() == Fixture.SourceHash(" changed"),
            "observed changed whole-file preimage clears older visibility mappings");
        changed.ReadRequest(1, 5);
        check(changed.IsTool, "old-preimage signatures do not prevent reading the now-current source");

        var forge = New(); forge.SourceWindow(1, 5); forge.SourceWindow(6, 5);
        var protectedId = Working(forge).GetProperty("contributionId").GetString()!;
        forge.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        forge.CompactSuccess(cp => cp with { CoveredSources = cp.CoveredSources.Append(new(protectedId, 1)).ToArray() });
        check(forge.Step.Next is CompleteWorkflow { Success: false }, "compactor cannot adopt a candidate claiming to cover Required working text");

        var unknown = New();
        unknown.ReadRequest(1, 5); unknown.Tool(true, "{\"path\":\"different\",\"sha256\":\"" + new string('a', 64) + "\",\"startLine\":1,\"totalLines\":20,\"content\":\"x\"}");
        check(Working(unknown).ValueKind == JsonValueKind.Null, "inconsistent typed identity/presentation stays unpinned");
        foreach (var count in new[] { 100, 1000 })
        {
            var whole = New(); whole.SourceWindow(1, 20);
            var original = Working(whole).GetProperty("contributionId").GetString()!;
            var originalBody = Body(whole, original);
            var before = whole.Step.State.GetProperty("readReceipts").GetArrayLength();
            whole.ChangeState(state => state["lastDiagnosticBuildEditCount"] = 0);
            whole.ReadRequest(1, count);
            check(whole.IsModel && whole.Step.State.GetProperty("readReceipts").GetArrayLength() == before &&
                whole.Step.State.GetProperty("repeatedReadAttempts").GetInt32() == 1 && Body(whole, original) == originalBody,
                "a complete retained whole-file body blocks an equivalent larger-range read without new evidence");
        }
        var terminal = New(); terminal.ReadRequest(1, 10);
        terminal.Tool(true, JsonSerializer.Serialize(new { path = Path.Combine(terminal.Root, "Program.cs"), sha256 = new string('a', 64), startLine = 1, totalLines = 2, content = "x\n" }));
        check(Working(terminal).GetProperty("visibleLineCount").GetInt32() == 2 && Working(terminal).GetProperty("physicalLineCount").GetInt32() == 1,
            "terminal empty read segment never becomes an extra physical edit line");
        check(Working(keep).GetRawText().Length < 2048 && !Working(keep).GetRawText().Contains("// line", StringComparison.Ordinal) &&
            keep.Context.Contributions.Count(s => s.Id == id) == 1,
            "working metadata contains no second source-body cache and active source is stored once");
    }

    private sealed partial class Fixture
    {
        private static string Source(string suffix) => string.Join('\n', Enumerable.Range(1, 20).Select(i => "// line " + i + suffix));
        public static string SourceHash(string suffix = "") => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source(suffix)))).ToLowerInvariant();
        public string ProjectionResult(int start = 1, int count = 200)
        {
            var raw = JsonNode.Parse(ProgrammingReadProjectionFixture.Result(start, count))!.AsObject();
            raw["path"] = Path.Combine(Root, "GeneratedServicePlugin.cs");
            return raw.ToJsonString(CerebralJson.Options);
        }
        public void ReadRequest(int start, int count, string fileName = "Program.cs") => Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(Root, fileName), startLine = start, lineCount = count } });
        public void SourceReply(int start, int count, string suffix = "") => Tool(true, JsonSerializer.Serialize(new
        {
            path = Path.Combine(Root, "Program.cs"), sha256 = SourceHash(suffix), startLine = start, totalLines = 20,
            content = string.Join('\n', Source(suffix).Split('\n').Skip(start - 1).Take(count))
        }), truncated: start - 1 + count < 20);
        public void SourceWindow(int start, int count, string suffix = "") { ReadRequest(start, count); SourceReply(start, count, suffix); }
        public void StructureResult()
        {
            Model(new { action = "tool", name = "program.source.structure", arguments = new { path = Path.Combine(Root, "Program.cs"), expectedSha256 = SourceHash(), focusLine = 6 } });
            Tool(true, "{\"schema\":\"program.source.structure/1\",\"recovered\":true}");
        }
        public void ChangeState(Action<JsonObject> change)
        {
            var state = JsonNode.Parse(Step.State.GetRawText())!.AsObject(); change(state);
            var command = Command;
            var arguments = JsonNode.Parse(command.Arguments.GetRawText())!.AsObject();
            arguments["context"] = new JsonObject
            {
                ["revision"] = state["contextRevision"]!.DeepClone(), ["exclusionRevision"] = state["exclusionRevision"]!.DeepClone(),
                ["contributions"] = state["contributions"]!.DeepClone(), ["checkpoint"] = state["checkpoint"]?.DeepClone()
            };
            Step = new(CerebralJson.Element(state), command with { Arguments = CerebralJson.Element(arguments) });
        }
    }
}
