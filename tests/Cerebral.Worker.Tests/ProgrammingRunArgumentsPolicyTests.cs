using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic policy/effect fixtures only; no console application or model executes here.</summary>
public static partial class ProgrammingHarnessTests
{
    static partial void RunArgumentsPolicy(Action<bool, string> check)
    {
        const string version = ProgrammingVerificationRetryHarness.RunArgumentsVersion;
        Fixture New(string[] tail, string[]? checks = null) => new(CerebralJson.Element(tail), checks);
        bool Failed(Fixture f) => f.Step.Next is CompleteWorkflow { Success: false };
        string[] Tail(Fixture f) => f.Argv.SkipWhile(argument => argument != "--").Skip(1).ToArray();
        void Build(Fixture f) { f.Read(); f.Dotnet("build"); f.Tool(true, "Built", 0); }
        void Done(Fixture f) => f.Model(new { action = "done", summary = "Run the configured console suite" });
        string Failure(Fixture f) => ((CompleteWorkflow)f.Step.Next).Result.GetProperty("summary").GetString()!;
        JsonElement Value(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        string[] automaticHead = ["run", "--project", "Hello.csproj", "--configuration", "Release",
            "--disable-build-servers", "--verbosity", "minimal", "--no-build", "--"];

        using var plugin = new ProgrammingServicePlugin();
        var exports = ((IAgentHarnessModule)plugin).Harnesses;
        var added = exports.Single(item => item.Descriptor.Version == version);
        check(added is ProgrammingVerificationRetryHarness && added.Descriptor.Id == ProgrammingAgentHarness.Id,
            "application argv uses an additive edition of the same retry harness class");
        var legacySchema = new ProgrammingAgentHarness().Descriptor.InputSchema;
        check(JsonElement.DeepEquals(exports.Single(item => item.Descriptor.Version == ProgrammingVerificationRetryHarness.Version)
            .Descriptor.InputSchema, legacySchema) && !legacySchema.GetProperty("properties").TryGetProperty("runArguments", out _),
            "old retry input schema remains exactly the original Develop schema");
        var schema = added.Descriptor.InputSchema.GetProperty("properties").GetProperty("runArguments");
        check(schema.GetProperty("maxItems").GetInt32() == 256 && !schema.GetProperty("items").TryGetProperty("maxLength", out _),
            "new outer schema mirrors process.run without a new per-argument cap");
        var old = new Fixture(guardFailedChecks: true); old.Process(["run", "--project", "Hello.csproj", "--", "policies"]);
        check(old.IsModel && old.Messages.Contains("Unsupported verification argument", StringComparison.Ordinal),
            "accepted old edition still rejects application separators");
        var twentyFive = new[] { "run", "--project", "Hello.csproj", "-c", "Release" }
            .Concat(Enumerable.Repeat("--nologo", 20)).ToArray();
        var oldCount = new Fixture(guardFailedChecks: true); oldCount.Process(twentyFive);
        var newCount = New([]); newCount.Process(twentyFive);
        check(oldCount.IsModel && newCount.IsTool, "old24-supplied-argument gate is preserved while the new edition uses process capacity");

        string[] opaque = ["", "two words", "a\"b", "--project", "Other.csproj", "--no-build", "--", ";|$()", "\U0001F680"];
        var manual = New(opaque, ["run"]);
        manual.Process(["run", "--project", "Hello.csproj", "-c", "Release", "--", .. opaque]);
        check(manual.IsTool && Tail(manual).SequenceEqual(opaque) && manual.Argv[2] == "Hello.csproj" &&
            manual.WorkingDirectory == manual.Root, "first separator preserves exact opaque tail without changing .NET project or root");
        manual.Tool(true, "Console finished", 0);
        check(manual.CheckCount == 1, "application data --no-build does not invent a CLI build prerequisite for a run-only contract");
        var automatic = New(opaque); Build(automatic); Done(automatic);
        check(automatic.IsTool && automatic.Argv.Take(automaticHead.Length).SequenceEqual(automaticHead) &&
            Tail(automatic).SequenceEqual(opaque), "automatic verification appends the same frozen tail after its admitted CLI head");
        var omitted = New(opaque); Build(omitted); omitted.Dotnet("run");
        check(omitted.IsTool && Tail(omitted).SequenceEqual(opaque), "a model need not restate the immutable application argv");
        var changedName = New(["--result-output", "first.json"]);
        changedName.Process(["run", "--project", "Hello.csproj", "--", "--result-output", "second.json"]);
        check(changedName.IsModel && changedName.Messages.Contains("immutable accepted runArguments", StringComparison.Ordinal),
            "changed output filename is a mismatched invocation, not new authority or a retry-lane reset");
        foreach (var kind in new[] { "build", "test" })
        {
            var delimiter = New([]); delimiter.Process([kind, "Hello.csproj", "--", "anything"]);
            check(delimiter.IsModel, "application separator is not a new protocol for " + kind);
        }
        var foreign = New([]); foreign.Process(["run", "--project", "Other.csproj"]);
        check(foreign.IsModel && foreign.Messages.Contains("exact accepted project", StringComparison.Ordinal),
            "foreign .NET head project is rejected before dispatch");
        var cwd = New([]); cwd.RunArgumentsWrongCwd();
        check(cwd.IsModel, "a different directory cannot replace the accepted root/project directory");

        foreach (var count in new[] { 245, 246, 247 })
        {
            var bounded = New(Enumerable.Repeat("", count).ToArray());
            if (count == 247)
                check(Failed(bounded) && Failure(bounded).Contains("257 arguments", StringComparison.Ordinal),
                    "frozen tail that exceeds its required automatic head is refused with measured count before any effect");
            else
            {
                Build(bounded); Done(bounded);
                check(bounded.IsTool && bounded.Argv.Length == automaticHead.Length + count &&
                    Tail(bounded).Length == count, "full normalized255/256 argument boundary retains every empty string: " + count);
            }
        }
        foreach (var total in new[] { 32767, 32768, 32769 })
        {
            var bounded = New([new string('x', total - automaticHead.Sum(argument => argument.Length))]);
            if (total == 32769)
                check(Failed(bounded) && Failure(bounded).Contains("32769 UTF-16", StringComparison.Ordinal),
                    "full normalized character overflow is measured before effect without an arbitrary per-item cap");
            else
            {
                Build(bounded); Done(bounded);
                check(bounded.IsTool && bounded.Argv.Sum(argument => argument.Length) == total,
                    "one large item remains valid at the actual full normalized character boundary: " + total);
            }
        }
        var unicode = New(["\U0001F680"]); Build(unicode); Done(unicode);
        check(unicode.Argv.Last().Length == 2 && Encoding.UTF8.GetByteCount(unicode.Argv.Last()) == 4,
            "process character count remains UTF-16, separate from serialized UTF-8 envelopes");
        var noBuild = New([]); noBuild.Process(["run", "Hello.csproj", "-c", "Release", "--no-build"]);
        check(noBuild.IsModel, "actual CLI no-build still requires a current matching build");

        foreach (var bad in new[] { "null", "{}", "[1]", "[\"a\\u0000b\"]" })
        {
            var invalid = new Fixture(Value(bad));
            check(Failed(invalid), "invalid configured argv fails before generation: " + bad);
        }
        check(Failed(New(["policies"], ["build"])), "nonempty run arguments require an accepted console run check");
        var largeInput = New([new string('\u0080', 22000)]);
        check(Failed(largeInput) && Failure(largeInput).Contains("workflow input", StringComparison.Ordinal),
            "actual escaped input serialization enforces existing128KiB envelope");
        var largeCommand = New([new string('\u0080', 20000)]);
        check(Failed(largeCommand) && Failure(largeCommand).Contains("model command", StringComparison.Ordinal),
            "an input that fits can still refuse its measured model-command envelope before effect");
        var pending = New([new string('x', 8192)]);
        pending.OwnerSeedState(node =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }).Length;
            node["completionDraft"] = new string('z', WorkflowContract.MaximumStateBytes - bytes - 2048);
        });
        pending.Dotnet("run");
        check(Failed(pending) && Failure(pending).Contains("pendingState=", StringComparison.Ordinal) &&
            Failure(pending).Contains("No argument was truncated", StringComparison.Ordinal),
            "near-full saved-state fixture refuses oversized pending invocation with actual byte diagnostics");

        foreach (var shape in new[] { "truncated", "partial", "followup", "request", "missing-exit", "nonzero", "outcome", "prepared", "unknown" })
        {
            var receipt = New(["policies"]); Build(receipt); receipt.Dotnet("run"); receipt.RunArgumentsReceipt(shape);
            check(receipt.CheckCount == 1 && receipt.Step.Next is not CompleteWorkflow { Success: true },
                "nonpassing/incomplete run cannot earn verification credit: " + shape);
        }
        var mismatch = New(["policies"]); Build(mismatch); mismatch.Dotnet("run"); mismatch.RunArgumentsReceipt("arguments");
        check(Failed(mismatch) && mismatch.CheckCount == 1, "same-effect receipt must match the exact normalized command arguments");
        foreach (var expectation in new[] { "1", "\"0\"", "null" })
        {
            var overridden = New([]); overridden.RunArgumentsExpectation(Value(expectation));
            check(overridden.IsModel && overridden.Messages.Contains("expectedExitCode", StringComparison.Ordinal),
                "verification cannot alter failed-launch accounting with an exit expectation: " + expectation);
        }
        var zeroExpectation = New([]); zeroExpectation.RunArgumentsExpectation(CerebralJson.Element(0));
        check(zeroExpectation.IsTool, "explicit exit-zero expectation retains ordinary verification semantics");
        var passed = New(["policies"]); Build(passed); Done(passed);
        check(passed.CheckCount == 1 && passed.IsTool, "a claimed console pass leaves the real configured run pending");
        passed.Tool(true, "PASS policy=804 tier=deterministic-fixtures", 0);
        check(passed.Step.Next is CompleteWorkflow { Success: true } completed &&
            completed.Result.GetProperty("checks").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "run")
                .GetProperty("testsTotal").ValueKind == JsonValueKind.Null,
            "actual console completion is a run receipt and does not invent a VSTest count");
        var vstest = New([], ["build", "test"]); Build(vstest); Done(vstest); vstest.Tool(true, "No tests found", 0);
        check(vstest.CheckCount == 1, "dotnet test still needs supported positive VSTest evidence");

        var guarded = New(["--result-output", "first.json"]); guarded.Dotnet("run"); guarded.VerificationFailure();
        guarded.Process(["run", "Hello.csproj", "--", "--result-output", "second.json"]);
        guarded.Dotnet("run");
        check(guarded.IsModel && guarded.Messages.Contains("Another equivalent check was not dispatched", StringComparison.Ordinal) &&
            guarded.Step.State.GetProperty("failedVerificationChecks").GetArrayLength() == 1,
            "mismatched output filename and matching retry both preserve the same failed-run lane without another launch");
        guarded.InputWitness(); guarded.VerificationSourceEdit(); guarded.Dotnet("run");
        check(guarded.IsTool && Tail(guarded).SequenceEqual(new[] { "--result-output", "first.json" }),
            "proved source repair unlocks only the unchanged accepted invocation");
        var compact = New(["path with spaces", "policies"]); compact.ReadRange(1, 1); compact.ReadRange(2, 1);
        compact.ContextRejected(ContextProjectionStatus.NeedsCompaction); compact.CompactSuccess();
        compact.OwnerSeedState(_ => { }); compact.Dotnet("run");
        check(compact.IsTool && Tail(compact).SequenceEqual(new[] { "path with spaces", "policies" }),
            "compaction and serialized continuation roundtrip preserve frozen invocation without another durable argv copy");
    }

    private sealed partial class Fixture
    {
        public Fixture(JsonElement runArguments, string[]? checks = null)
            : this(requiredChecks: checks ?? ["build", "run"], guardFailedChecks: true)
        {
            using var module = new ProgrammingServicePlugin();
            harness = ((IAgentHarnessModule)module).Harnesses.Single(item => item.Descriptor.Version == ProgrammingVerificationRetryHarness.RunArgumentsVersion);
            var input = JsonNode.Parse(submission.Input.GetRawText())!.AsObject();
            input["runArguments"] = JsonNode.Parse(runArguments.GetRawText());
            submission = submission with { DefinitionVersion = harness.Descriptor.Version, Input = CerebralJson.Element(input) };
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
        }

        public void RunArgumentsWrongCwd() => Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = new[] { "run", "Hello.csproj" }, workingDirectory = Path.GetDirectoryName(Root)! } });
        public void RunArgumentsExpectation(JsonElement expectedExitCode) => Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = new[] { "run", "Hello.csproj" }, workingDirectory = Root, expectedExitCode } });
        public void RunArgumentsReceipt(string shape)
        {
            var effectState = shape == "unknown" ? WorkflowEffectState.Unknown : shape == "prepared" ? WorkflowEffectState.Prepared : WorkflowEffectState.Succeeded;
            var arguments = shape == "arguments" ? CerebralJson.Element(new { name = "process.run", arguments = new { } }) : (JsonElement?)null;
            Result(effectState, CerebralJson.Element(new
            {
                name = "process.run", content = "Console result", truncated = shape == "truncated", followUpRequired = shape == "followup",
                evidence = new ToolExecutionEvidence(shape == "outcome" ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded,
                    "deterministic-fixture", RequestId: shape == "request" ? "wrong-effect" : null,
                    ExitCode: shape == "missing-exit" ? null : shape == "nonzero" ? 1 : 0, Partial: shape == "partial")
            }), effectArguments: arguments);
        }
    }
}
