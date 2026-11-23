using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    // Deterministic policy fixtures only. Literal profile grants and physical/provider validation
    // are tested by the Tools owner, not inferred from this pure harness's selectedTools.
    private static void RangeEditPolicy(Action<bool, string> check)
    {
        const string tool = "file.edit-range";
        var defaults = new Fixture();
        check(!defaults.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(value => value.GetString() == tool),
            "range editing does not enter the existing default tool set");
        check(!defaults.Messages.Contains(tool, StringComparison.Ordinal),
            "unselected range tooling is not advertised in the default policy text");
        var selected = RangeFixture();
        check(selected.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(value => value.GetString() == tool),
            "the exact new semantic name can be explicitly selected");
        check(selected.Messages.Contains("Do not copy oldText.", StringComparison.Ordinal),
            "range-specific guidance appears only for the explicitly selected tool");
        foreach (var name in new[] { "*", "file.edit-range*", "mcp.cortex.pao" })
        {
            var rejected = false;
            try { _ = new Fixture(selectedTools: ["file.read", name, "process.run"]); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "workflow selection never admits wildcard or generic PAO names: " + name);
        }
        defaults.RangeAction();
        check(defaults.IsModel && defaults.Step.State.GetProperty("editCount").GetInt32() == 0,
            "a range tool action without explicit selection is corrected before dispatch");

        (string Label, Action<Dictionary<string, object>> Change)[] invalid =
        [
            ("missing hash", args => args.Remove("expectedSha256")),
            ("malformed hash", args => args["expectedSha256"] = "not-a-sha"),
            ("zero first line", args => args["startLine"] = 0),
            ("first line over bound", args => args["startLine"] = 1_000_001),
            ("fractional first line", args => args["startLine"] = 1.5),
            ("zero line count", args => args["lineCount"] = 0),
            ("line count over bound", args => args["lineCount"] = 513),
            ("non-string replacement", args => args["newText"] = 42),
            ("oversized UTF8 replacement", args => args["newText"] = new string('\u00e9', 32769)),
            ("unexpected oldText", args => args["oldText"] = "invented indentation")
        ];
        foreach (var (label, change) in invalid)
        {
            var fixture = RangeFixture();
            var arguments = fixture.RangeArguments();
            change(arguments);
            fixture.Model(new { action = "tool", name = tool, arguments });
            check(fixture.IsModel && fixture.Step.State.GetProperty("editCount").GetInt32() == 0,
                "range admission rejects " + label + " before dispatch");
        }
        var outside = RangeFixture();
        var outsideArguments = outside.RangeArguments();
        outsideArguments["path"] = Path.Combine(Path.GetDirectoryName(outside.Root)!, "outside.cs");
        outside.Model(new { action = "tool", name = tool, arguments = outsideArguments });
        check(outside.IsModel, "range editing preserves the accepted project path ceiling");
        var empty = RangeFixture();
        empty.RangeAction(newText: "");
        check(empty.IsTool, "empty replacement text is admitted as a possible deletion, not mistaken for missing text");
        var boundary = RangeFixture();
        boundary.RangeAction(newText: new string('x', 64 * 1024));
        check(boundary.IsModel &&
              boundary.LastContributionText.Contains("bounded JSON action object", StringComparison.Ordinal),
            "the 64KiB tool text ceiling never bypasses the existing 64KiB whole-action envelope");
        var invalidUnicode = RangeFixture();
        invalidUnicode.ModelText("{\"action\":\"tool\",\"name\":\"file.edit-range\",\"arguments\":{\"path\":" +
            JsonSerializer.Serialize(Path.Combine(invalidUnicode.Root, "Program.cs")) +
            ",\"expectedSha256\":\"" + new string('a', 64) +
            "\",\"startLine\":28,\"lineCount\":18,\"newText\":\"\\ud800\"}}");
        check(invalidUnicode.IsModel && invalidUnicode.Messages.Contains("well-formed Unicode", StringComparison.Ordinal),
            "malformed Unicode replacement is rejected before dispatch without replacement fallback");

        var positive = RangeFixture();
        positive.Read();
        positive.Dotnet("build");
        positive.Tool(true, "Build succeeded", 0);
        check(positive.CheckCount == 1, "the range fixture begins with a genuine-shaped passing build receipt");
        positive.RangeAction();
        check(positive.IsTool && positive.CheckCount == 0 && positive.Step.State.GetProperty("editCount").GetInt32() == 0,
            "range dispatch invalidates old build credit before any mutation result");
        positive.RangeResult();
        check(positive.IsModel && positive.Step.State.GetProperty("editCount").GetInt32() == 1 &&
              positive.Step.State.GetProperty("readSignatures").GetArrayLength() == 0,
            "only confirmed changed range evidence grants one edit and resets read signatures");
        check(positive.Context.Contributions.Count(source => source.Category == "tool-result") == 1 &&
              !positive.Messages.Contains("Build succeeded", StringComparison.Ordinal),
            "a confirmed range mutation removes stale source and compiler projections");
        check(positive.LastContributionText.Contains(new string('b', 64), StringComparison.Ordinal) &&
              positive.LastContributionText.Contains("sha256Before", StringComparison.Ordinal),
            "the existing payload projection exposes the normalized new hash without a Core mutation-header change");
        positive.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(positive.Root, "Program.cs") } });
        check(positive.IsTool, "a confirmed range edit permits the same source range to be read again");

        var rejectedRange = RangeFixture();
        rejectedRange.Read();
        rejectedRange.RangeAction();
        rejectedRange.RangeResult(new() { Outcome = ToolExecutionOutcome.Rejected });
        check(rejectedRange.IsModel && rejectedRange.Step.State.GetProperty("editCount").GetInt32() == 0 &&
              rejectedRange.Step.State.GetProperty("readSignatures").GetArrayLength() == 1,
            "known preflight rejection earns no edit credit and preserves read signatures");
        rejectedRange.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(rejectedRange.Root, "Program.cs") } });
        check(rejectedRange.IsModel && rejectedRange.Messages.Contains("exact file range was already read", StringComparison.Ordinal),
            "a rejected range edit does not bypass the duplicate-read guard");

        (string Label, RangeReply Reply)[] uncertain =
        [
            ("missing evidence", new() { Outcome = null }),
            ("unknown evidence", new() { Outcome = ToolExecutionOutcome.Unknown }),
            ("opaque failed result", new() { Outcome = ToolExecutionOutcome.Failed }),
            ("partial evidence", new() { Partial = true }),
            ("unspecified partial evidence", new() { Partial = null }),
            ("truncated result", new() { Truncated = true }),
            ("follow-up generation", new() { FollowUp = true }),
            ("foreign request identity", new() { ForeignRequest = true }),
            ("empty provider identity", new() { ProviderId = "" }),
            ("foreign result name", new() { Name = "file.edit" }),
            ("foreign pending arguments", new() { ForeignArguments = true }),
            ("cancelled effect", new() { State = WorkflowEffectState.Cancelled }),
            ("unknown effect", new() { State = WorkflowEffectState.Unknown }),
            ("malformed ToolData envelope", new() { MalformedEnvelope = true })
        ];
        foreach (var (label, reply) in uncertain)
        {
            var fixture = RangeFixture();
            fixture.Read();
            fixture.RangeAction();
            fixture.RangeResult(reply);
            check(fixture.Step.Next is CompleteWorkflow { Success: false } &&
                  fixture.Step.State.GetProperty("editCount").GetInt32() == 0 && fixture.CheckCount == 0,
                "unconfirmed range outcome holds without credit, model continuation or replay: " + label);
        }

        (string Label, Action<JsonObject> Change)[] invalidProof =
        [
            ("foreign path", value => value["path"] = Path.Combine(Path.GetTempPath(), "foreign.cs")),
            ("wrong preimage", value => value["sha256Before"] = new string('c', 64)),
            ("invalid preimage", value => value["sha256Before"] = "invalid"),
            ("unchanged postimage", value => value["sha256"] = new string('a', 64)),
            ("invalid postimage", value => value["sha256"] = "invalid"),
            ("negative bytes", value => value["bytes"] = -1),
            ("oversized result", value => value["bytes"] = 1024 * 1024 + 1),
            ("non-numeric bytes", value => value["bytes"] = "123"),
            ("wrong first line", value => value["startLine"] = 29),
            ("wrong line count", value => value["lineCount"] = 17),
            ("changed false", value => value["changed"] = false),
            ("non-boolean changed", value => value["changed"] = "true"),
            ("missing path", value => value.Remove("path")),
            ("missing preimage", value => value.Remove("sha256Before")),
            ("case-ambiguous path", value => value["Path"] = value["path"]!.GetValue<string>())
        ];
        foreach (var (label, change) in invalidProof)
        {
            var fixture = RangeFixture();
            fixture.RangeAction();
            fixture.RangeResult(new() { Change = change });
            check(fixture.Step.Next is CompleteWorkflow { Success: false } &&
                  fixture.Step.State.GetProperty("editCount").GetInt32() == 0,
                "malformed normalized range proof cannot become source credit: " + label);
        }
        var malformed = RangeFixture();
        malformed.RangeAction();
        malformed.RangeResult(new() { Content = "{" });
        check(malformed.Step.Next is CompleteWorkflow { Success: false }, "malformed semantic JSON is held, not repaired by redispatch");
        var local = RangeFixture();
        local.RangeAction();
        local.RangeResult(new() { ProviderId = "mcp.other-original-registration" });
        check(local.IsModel && local.Step.State.GetProperty("editCount").GetInt32() == 1,
            "Programming does not introduce a hardcoded provider allowlist over Tools-validated semantic evidence");

        var verification = RangeFixture();
        verification.Read();
        verification.Dotnet("build");
        verification.Tool(true, "Build succeeded", 0);
        verification.RangeAction();
        verification.RangeResult();
        verification.Model(new { action = "done", summary = "Edited" });
        check(verification.IsTool && verification.Argv[0] == "build" && verification.CheckCount == 0,
            "a previous build cannot complete a later range edit; the accepted real build must run again");
        verification.Tool(true, "Build succeeded", 0);
        check(verification.Step.Next is CompleteWorkflow { Success: true } &&
              ((CompleteWorkflow)verification.Step.Next).Result.GetProperty("editCount").GetInt32() == 1,
            "a new matching build receipt after the range edit can satisfy the unchanged build-only oracle");

        var legacy = new Fixture(requiredChecks: ["build"]);
        legacy.Read();
        legacy.Model(new { action = "tool", name = "file.edit", arguments = new
        {
            path = Path.Combine(legacy.Root, "Program.cs"), expectedSha256 = new string('a', 64),
            oldText = "before", newText = "after"
        } });
        legacy.LegacyResultWithoutEvidence();
        check(legacy.IsModel && legacy.Step.State.GetProperty("editCount").GetInt32() == 1,
            "strict range evidence does not remove the existing legacy-null evidence behavior of older tools");
    }

    private static Fixture RangeFixture() => new(requiredChecks: ["build"],
        selectedTools: ["file.read", "file.edit-range", "process.run"]);

    private sealed record RangeReply
    {
        public ToolExecutionOutcome? Outcome { get; init; } = ToolExecutionOutcome.Succeeded;
        public bool? Partial { get; init; } = false;
        public bool Truncated { get; init; }
        public bool FollowUp { get; init; }
        public bool ForeignRequest { get; init; }
        public bool ForeignArguments { get; init; }
        public bool MalformedEnvelope { get; init; }
        public string ProviderId { get; init; } = "deterministic-range-fixture";
        public string? Name { get; init; }
        public string? Content { get; init; }
        public WorkflowEffectState? State { get; init; }
        public Action<JsonObject>? Change { get; init; }
    }

    private sealed partial class Fixture
    {
        public Dictionary<string, object> RangeArguments(string newText = "corrected source\n") => new()
        {
            ["path"] = Path.Combine(Root, "Program.cs"), ["expectedSha256"] = new string('a', 64),
            ["startLine"] = 28, ["lineCount"] = 18, ["newText"] = newText
        };
        public void RangeAction(string newText = "corrected source\n") =>
            Model(new { action = "tool", name = "file.edit-range", arguments = RangeArguments(newText) });

        public void RangeResult(RangeReply? options = null)
        {
            options ??= new();
            var arguments = Command.Arguments.GetProperty("arguments");
            var body = new JsonObject
            {
                ["path"] = arguments.GetProperty("path").GetString(),
                ["sha256Before"] = arguments.GetProperty("expectedSha256").GetString(),
                ["sha256"] = new string('b', 64), ["bytes"] = 1234,
                ["startLine"] = arguments.GetProperty("startLine").GetInt32(),
                ["lineCount"] = arguments.GetProperty("lineCount").GetInt32(), ["changed"] = true
            };
            options.Change?.Invoke(body);
            var content = options.Content ?? (options.Outcome == ToolExecutionOutcome.Rejected
                ? "{\"error\":\"range_edit_rejected_before_dispatch\"}" : body.ToJsonString());
            var state = options.State ?? (options.Outcome is ToolExecutionOutcome.Rejected or ToolExecutionOutcome.Failed
                ? WorkflowEffectState.Failed : WorkflowEffectState.Succeeded);
            var requestId = options.ForeignRequest ? "foreign-workflow-effect" : "fixture-effect-" + (sequence + 1);
            ToolExecutionEvidence? evidence = options.Outcome is { } outcome
                ? new(outcome, options.ProviderId, RequestId: requestId, Partial: options.Partial) : null;
            var result = options.MalformedEnvelope
                ? CerebralJson.Element(new { name = "file.edit-range", content = 42, evidence })
                : CerebralJson.Element(new { name = options.Name ?? "file.edit-range", content, truncated = options.Truncated,
                    evidence, followUpRequired = options.FollowUp });
            JsonElement? effectArguments = options.ForeignArguments
                ? CerebralJson.Element(new { name = "file.edit-range", arguments = new { path = "foreign" } }) : null;
            Result(state, result, state == WorkflowEffectState.Failed ? "Range edit was not committed." : null, effectArguments);
        }

        public void LegacyResultWithoutEvidence() => Result(WorkflowEffectState.Succeeded,
            CerebralJson.Element(new { name = PendingTool, content = "Legacy source effect",
                truncated = false, evidence = (ToolExecutionEvidence?)null, followUpRequired = false }));
    }
}

