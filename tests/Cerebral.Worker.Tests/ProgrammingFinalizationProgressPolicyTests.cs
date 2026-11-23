using System.Security.Cryptography;
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

/// <summary>Deterministic finalization policy fixtures; no SDK, process or filesystem operation executes.</summary>
public static partial class ProgrammingHarnessTests
{
    public static int RunFinalizationProgressChecks()
    {
        var checks = 0;
        FinalizationProgressPolicy((passed, message) =>
        {
            if (!passed) throw new InvalidOperationException("Programming finalization: " + message);
            checks++;
        });
        InterfacePresentationPolicy((passed, message) =>
        {
            if (!passed) throw new InvalidOperationException("Programming interface presentation: " + message);
            checks++;
        });
        return checks;
    }

    static partial void InterfacePresentationPolicy(Action<bool, string> check);

    static partial void FinalizationProgressPolicy(Action<bool, string> check)
    {
        const string manifest = "Desktop/bin/Release/net10.0-windows/desktop-plugin.json";
        Fixture Ready()
        {
            var f = new Fixture(requiredChecks: ["build"],
                selectedTools: ["file.read", "file.list", "file.write", "file.edit", "process.run", "plugin.contract"],
                presentManifest: manifest, guardFinalization: true);
            f.FinalizationSourceRead();
            check(f.Step.State.GetProperty("readHistory").EnumerateArray().Any(item =>
                item.TryGetProperty("span", out var span) && span.ValueKind == JsonValueKind.Object),
                "a real-shaped exact source read freezes a relevant repair path");
            f.Dotnet("build"); f.Tool(true, "Build succeeded", 0);
            return f;
        }
        Fixture RejectedVerify()
        {
            var f = Ready();
            f.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = f.Root } });
            check(f.IsTool && f.PendingTool == "plugin.verify", "direct verification uses the exact accepted root");
            f.FinalizationRejected("The desktop source is an inert scaffold. Implement the contribution, rebuild, and verify again.");
            check(f.IsModel && f.Step.State.GetProperty("pluginVerified").GetBoolean() is false,
                "SDK Rejected with no child exit is a failure and gives no verification credit");
            return f;
        }
        Fixture ProjectOnlyRejectedVerify()
        {
            var f = new Fixture(requiredChecks: ["build"],
                selectedTools: ["file.read", "file.list", "file.write", "file.edit", "process.run", "plugin.contract"],
                presentManifest: manifest, guardFinalization: true);
            f.FinalizationProjectRead();
            f.Dotnet("build"); f.Tool(true, "Build succeeded", 0);
            f.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = f.Root } });
            f.FinalizationRejected("The inert desktop source needs a real contribution.");
            return f;
        }
        JsonElement Failure(Fixture f) => f.Step.State.GetProperty("finalizationFailure");
        ContextContribution Feedback(Fixture f) => f.Step.State.GetProperty("contributions")
            .Deserialize<ContextContribution[]>(CerebralJson.Options)!
            .Single(item => item.Category == "finalization-feedback");
        void RetryVerify(Fixture f) => f.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = f.Root } });

        using var plugin = new ProgrammingServicePlugin();
        var editions = ((IAgentHarnessModule)plugin).Harnesses.Where(h => h.Descriptor.Id == ProgrammingAgentHarness.Id).ToArray();
        check(editions.Count(h => h.Descriptor.Version == ProgrammingFinalizationProgressHarness.Version) == 1 &&
            editions.Any(h => h.Descriptor.Version == ProgrammingAgentHarness.Version) &&
            editions.Any(h => h.Descriptor.Version == ProgrammingVerificationRetryHarness.Version) &&
            editions.Any(h => h.Descriptor.Version == ProgrammingVerificationRetryHarness.RunArgumentsVersion),
            "new Develop edition is separate from immutable 1.3.21/23/24 editions");
        foreach (var solution in new[] { "Workspace.sln", "Workspace.slnx" })
        {
            var refused = new Fixture(requiredChecks: ["build"], projectFile: solution,
                presentManifest: manifest, guardFinalization: true);
            check(refused.Step.Next is CompleteWorkflow { Success: false } rejected &&
                rejected.Result.GetProperty("summary").GetString()!.Contains("exact .csproj", StringComparison.Ordinal),
                "guarded plugin finalization refuses ambiguous solution ownership before any host effect: " + solution);
        }
        Fixture BoundedRead(bool guarded, int returnedStart = 1, int totalLines = 20, bool includeContent = true,
            string? body = null, int requestedCount = 5, int returnedCount = 5)
        {
            var f = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"],
                guardFinalization: guarded);
            var path = Path.Combine(f.Root, "Program.cs");
            f.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = requestedCount } });
            var source = body ?? string.Join('\n', Enumerable.Range(1, returnedCount).Select(index => "// line " + index));
            var result = includeContent
                ? JsonSerializer.Serialize(new { path, sha256 = new string('a', 64), startLine = returnedStart,
                    totalLines, content = source })
                : JsonSerializer.Serialize(new { path, sha256 = new string('a', 64), startLine = returnedStart,
                    totalLines });
            f.Tool(true, result, truncated: true);
            return f;
        }
        var bounded = BoundedRead(guarded: true);
        check(bounded.Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Object &&
            bounded.Step.State.GetProperty("workingSource").GetProperty("visibleLineCount").GetInt32() == 5 &&
            bounded.LastContributionText.Contains("Additional file lines 6-20 exist outside this returned range", StringComparison.Ordinal),
            "current public readable projection seeds an exact bounded source mapping in the new edition");
        check(BoundedRead(guarded: false).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null,
            "the legacy edition retains its admitted ranged-read parser");
        var shortReturned = BoundedRead(guarded: true, totalLines: 417, requestedCount: 417, returnedCount: 143);
        var observed = shortReturned.Step.State.GetProperty("workingSource");
        check(observed.ValueKind == JsonValueKind.Object && observed.GetProperty("visibleLineCount").GetInt32() == 143 &&
            observed.GetProperty("physicalLineCount").GetInt32() == 143 &&
            observed.GetProperty("requestedLineCount").GetInt32() == 417 &&
            shortReturned.Step.State.GetProperty("lastReadCoverage").GetProperty("physicalVisibleThrough").GetInt32() == 143 &&
            shortReturned.LastContributionText.Contains("Additional file lines 144-417", StringComparison.Ordinal),
            "a short returned provider prefix records only 143 observed physical lines from a 417-line request");
        check(BoundedRead(guarded: true, returnedStart: 2).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null &&
            BoundedRead(guarded: true, totalLines: 4).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null &&
            BoundedRead(guarded: true, requestedCount: 5, returnedCount: 6).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null &&
            BoundedRead(guarded: true, includeContent: false).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null &&
            BoundedRead(guarded: true, body: new string('x', 9000)).Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null,
            "wrong range, wrong total, over-request return, missing typed marker and clipped source body cannot seed a mapping");
        void CorruptBounded(string originalText, string changedText)
        {
            var f = BoundedRead(guarded: true);
            var receiptId = f.Step.State.GetProperty("workingSource").GetProperty("receiptId").GetString()!;
            var contributionId = f.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString()!;
            f.ChangeState(state =>
            {
                var contribution = state["contributions"]!.AsArray().Single(item =>
                    item!["id"]!.GetValue<string>() == contributionId)!;
                var text = contribution["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
                contribution["messages"]![0]!["content"]![0]!["text"] = text.Replace(originalText, changedText, StringComparison.Ordinal);
            });
            f.Model(new { action = "focus-source", receiptId });
            check(f.IsModel && f.Messages.Contains("original source body still present", StringComparison.Ordinal),
                "altered bounded projection body or marker loses its active mapping before source selection");
        }
        CorruptBounded("Additional file lines 6-20", "Additional file lines 6-21");
        CorruptBounded("// line 1", "// altered line");
        var legacy = new Fixture(requiredChecks: ["build"], presentManifest: manifest);
        legacy.Read(); legacy.Dotnet("build"); legacy.Tool(true, "Built", 0);
        legacy.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = legacy.Root } });
        legacy.FinalizationRejected("Legacy edition result");
        RetryVerify(legacy);
        check(legacy.IsTool && !legacy.Step.State.TryGetProperty("finalizationFailure", out _),
            "the legacy edition retains its historical behavior and continuation shape");
        check(new ProgrammingFinalizationProgressHarness().Advance(new(new WorkflowRun
        {
            Id = legacy.OwnerSubmission().RequestId, Submission = legacy.OwnerSubmission(),
            State = legacy.Step.State, Status = WorkflowStatus.Running
        })).Next is CompleteWorkflow { Success: false }, "guarded edition refuses a different accepted definition version");
        var baseline = RejectedVerify();
        var original = Failure(baseline);
        var receipt = original.GetProperty("effectId").GetString();
        var feedback = Feedback(baseline);
        check(original.GetProperty("root").GetString() == baseline.Root &&
            original.GetProperty("configuration").GetString() == "Release" &&
            original.GetProperty("manifest").GetString()!.EndsWith("desktop-plugin.json", StringComparison.Ordinal) &&
            original.GetProperty("sourceMutationGeneration").GetInt32() == 0 &&
            original.GetProperty("repairEffectId").ValueKind == JsonValueKind.Null &&
            feedback.Requirement == ContextRequirement.Required && feedback.ExactReadHandle == receipt &&
            ((TextContentPart)feedback.Messages.Single().Content.Single()).Text.Contains("inert scaffold", StringComparison.Ordinal),
            "original SDK diagnostic and assignment/source coordinates are retained as required context");
        RetryVerify(baseline);
        check(baseline.IsModel && Failure(baseline).GetProperty("effectId").GetString() == receipt,
            "direct unchanged verify is stopped before another SDK effect");
        baseline.Model(new { action = "done", summary = "Try finalization again" });
        check(baseline.IsModel && Failure(baseline).GetProperty("effectId").GetString() == receipt,
            "automatic done-to-finalization uses the same no-progress gate");
        var compacted = RejectedVerify();
        var compactReceipt = Failure(compacted).GetProperty("effectId").GetString();
        compacted.ContextRejected(ContextProjectionStatus.NeedsCompaction); compacted.CompactSuccess();
        check(compacted.IsModel && Feedback(compacted).Requirement == ContextRequirement.Required &&
            Feedback(compacted).ExactReadHandle == compactReceipt,
            "compaction keeps the original failed finalization receipt and required diagnostic");
        var steered = RejectedVerify();
        var steeredReceipt = Failure(steered).GetProperty("effectId").GetString();
        steered.QueueOwner("Inspect the original rejection and repair the actual source.");
        steered.Model(new { action = "done", summary = "Superseded by owner correction" });
        check(steered.IsModel && Feedback(steered).Requirement == ContextRequirement.Required &&
            Feedback(steered).ExactReadHandle == steeredReceipt,
            "owner steering preserves the original SDK failure instead of granting retry credit");
        baseline.Model(new { action = "tool", name = "file.list", arguments = new { path = baseline.Root } });
        baseline.Tool(true, "[]");
        check(Feedback(baseline).Requirement == ContextRequirement.Required &&
            Feedback(baseline).ExactReadHandle == receipt, "unrelated listing does not demote the original diagnostic");
        baseline.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(baseline.Root, "Missing.cs"), startLine = 1, lineCount = 1 } });
        check(baseline.IsTool && baseline.PendingTool == "file.read", "missing source may still be inspected after verification rejection");
        baseline.Tool(true, "class Missing { }");
        baseline.Model(new { action = "tool", name = "plugin.contract", arguments = new { topic = "desktop-base" } });
        check(baseline.IsTool && baseline.PendingTool == "plugin.contract", "SDK contract remains readable for source repair");
        baseline.Tool(true, "SDK contract reference");
        RetryVerify(baseline);
        check(baseline.IsModel && Failure(baseline).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null &&
            Feedback(baseline).Requirement == ContextRequirement.Required,
            "source and contract reads preserve the original diagnostic without granting repair credit");

        var noop = RejectedVerify(); noop.GuardWriteResult("identical");
        noop.Dotnet("build"); noop.Tool(true, "Unchanged build succeeded", 0); RetryVerify(noop);
        check(noop.IsModel && Failure(noop).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "no-op write and an unchanged passing rebuild cannot unlock verify");
        var unrelated = RejectedVerify(); unrelated.FinalizationWrite("Unobserved.cs");
        unrelated.Dotnet("build"); unrelated.Tool(true, "Unrelated build succeeded", 0); RetryVerify(unrelated);
        check(unrelated.IsModel && Failure(unrelated).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "changed but unobserved source has no relevant repair credit");

        var lateSource = ProjectOnlyRejectedVerify();
        check(Failure(lateSource).GetProperty("relevantPaths").GetArrayLength() == 1 &&
            Failure(lateSource).GetProperty("laterSources").GetArrayLength() == 0,
            "project-only source knowledge initially binds only the accepted project file");
        lateSource.FinalizationSourceRead(Path.Combine("bin", "Generated.cs"));
        check(Failure(lateSource).GetProperty("laterSources").GetArrayLength() == 0,
            "a generated build-output path is not admitted as a project source candidate");
        lateSource.FinalizationSourceRead();
        check(Failure(lateSource).GetProperty("laterSources").GetArrayLength() == 1 &&
            Failure(lateSource).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "a post-rejection exact source read adds a scoped candidate without repair credit");
        RetryVerify(lateSource);
        check(lateSource.IsModel, "post-rejection source inspection alone cannot retry SDK verification");
        lateSource.GuardWriteResult("identical");
        RetryVerify(lateSource);
        check(lateSource.IsModel && Failure(lateSource).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "a no-op at a newly inspected source still cannot unlock verification");
        lateSource.FinalizationWrite("Unobserved.cs");
        RetryVerify(lateSource);
        check(lateSource.IsModel && Failure(lateSource).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "an unobserved unrelated .cs write cannot borrow the scoped source read");
        lateSource.FinalizationCreate("NewContribution.cs");
        RetryVerify(lateSource);
        check(lateSource.IsModel && Failure(lateSource).GetProperty("repairEffectId").ValueKind == JsonValueKind.Null,
            "new file creation without observed project membership is not repair credit");
        lateSource.InputWitness(); lateSource.GuardWriteResult("changed");
        RetryVerify(lateSource);
        check(lateSource.IsModel, "proved late source repair still requires a subsequent current build");
        lateSource.Dotnet("build"); lateSource.Tool(true, "New source build succeeded", 0);
        RetryVerify(lateSource);
        check(lateSource.IsTool && lateSource.PendingTool == "plugin.verify",
            "project-only rejection can recover after trusted source inspection, proved repair and fresh build");

        var repaired = RejectedVerify(); repaired.InputWitness(); repaired.GuardWriteResult("changed");
        check(Failure(repaired).GetProperty("repairEffectId").ValueKind == JsonValueKind.String &&
            Failure(repaired).GetProperty("repairMutationGeneration").GetInt32() == 1 &&
            Feedback(repaired).Requirement == ContextRequirement.Required,
            "only proved changed bytes at the observed source record a repair, preserving original feedback");
        RetryVerify(repaired);
        check(repaired.IsModel, "a proved repair still needs a current passing build");
        repaired.Dotnet("build"); repaired.Tool(true, "Repaired build succeeded", 0);
        RetryVerify(repaired);
        check(repaired.IsTool && repaired.PendingTool == "plugin.verify", "relevant repair plus fresh build unlocks one SDK verification");
        repaired.Tool(true, "Plugin verified");
        check(repaired.IsModel && !repaired.Step.State.TryGetProperty("finalizationFailure", out _) &&
            !repaired.Step.State.GetProperty("contributions").EnumerateArray().Any(item =>
                item.GetProperty("category").GetString() == "finalization-feedback"),
            "a newly confirmed verify retires the historical failure without assuming presentation");

        var intervening = Ready();
        RetryVerify(intervening); intervening.Tool(true, "Verified exact manifest");
        check(intervening.Step.State.GetProperty("pluginVerified").GetBoolean(),
            "a confirmed verification is current until a semantic host tool intervenes");
        intervening.Model(new { action = "receipt", effectId = "stored-effect", offset = 0 });
        intervening.ReceiptPage(WorkflowEffectState.Failed, "Original diagnostic");
        check(intervening.Step.State.GetProperty("pluginVerified").GetBoolean(),
            "an exact receipt lookup does not invalidate a successful verification");
        intervening.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(intervening.Root, "Settings.cs"), startLine = 1, lineCount = 1 } });
        check(intervening.IsTool && !intervening.Step.State.GetProperty("pluginVerified").GetBoolean(),
            "actual semantic read dispatch invalidates the current verify proof");
        intervening.Tool(true, "class Settings { }");
        RetryVerify(intervening);
        check(intervening.IsTool && intervening.PendingTool == "plugin.verify",
            "a prior successful verify may be repeated after semantic intervention without a failed-verify repair claim");

        var present = Ready(); present.Model(new { action = "done", summary = "Finish" });
        check(present.IsTool && present.PendingTool == "plugin.verify", "automatic finalization starts with verify");
        present.Tool(true, "Verified");
        check(present.IsTool && present.PendingTool == "plugin.present", "confirmed verify requests presentation once");
        present.FinalizationRejected("The immutable owner/interface authority rejected presentation.");
        check(present.Step.Next is CompleteWorkflow { Success: false } &&
            Failure(present).GetProperty("name").GetString() == "plugin.present" &&
            !present.Step.State.GetProperty("pluginPresented").GetBoolean(),
            "presentation rejection stops with original receipt and no queued credit");
        var unknown = Ready(); unknown.Model(new { action = "done", summary = "Finish" }); unknown.Tool(true, "Verified"); unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } &&
            Failure(unknown).GetProperty("uncertain").GetBoolean() &&
            !unknown.Step.State.GetProperty("pluginPresented").GetBoolean(),
            "unknown presentation remains terminal and may be queued; no replay is inferred");
        var partial = Ready(); partial.Model(new { action = "done", summary = "Finish" }); partial.Tool(true, "Verified");
        partial.FinalizationPartial();
        check(partial.Step.Next is CompleteWorkflow { Success: false } &&
            Failure(partial).GetProperty("uncertain").GetBoolean() &&
            !partial.Step.State.GetProperty("pluginPresented").GetBoolean(),
            "partial presentation stays uncertain and never gains presentation credit");

        var corrupted = RejectedVerify();
        var changedState = JsonNode.Parse(corrupted.Step.State.GetRawText())!.AsObject();
        changedState["finalizationFailure"]!["manifest"] = Path.Combine(corrupted.Root, "other", "desktop-plugin.json");
        var mismatched = new ProgrammingFinalizationProgressHarness().Advance(new(new WorkflowRun
        {
            Id = corrupted.OwnerSubmission().RequestId, Submission = corrupted.OwnerSubmission(),
            State = CerebralJson.Element(changedState), Status = WorkflowStatus.Running
        }));
        check(mismatched.Next is CompleteWorkflow { Success: false } &&
            ((CompleteWorkflow)mismatched.Next).Result.GetProperty("summary").GetString()!.Contains("inconsistent", StringComparison.Ordinal),
            "restored failure cannot move to a different accepted manifest");
    }

    private sealed partial class Fixture
    {
        public void FinalizationSourceRead(string file = "Program.cs")
        {
            var path = Path.Combine(Root, file);
            Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1 } });
            Tool(true, JsonSerializer.Serialize(new { path, sha256 = new string('a', 64), startLine = 1,
                totalLines = 1, content = "class Plugin { }" }));
        }

        public void FinalizationProjectRead()
        {
            var path = Path.Combine(Root, "Hello.csproj");
            Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1 } });
            Tool(true, JsonSerializer.Serialize(new { path, sha256 = new string('c', 64), startLine = 1,
                totalLines = 1, content = "<Project Sdk=\"Microsoft.NET.Sdk\" />" }));
        }

        public void FinalizationRejected(string diagnostic) => Result(WorkflowEffectState.Failed, CerebralJson.Element(new
        {
            name = PendingTool, content = diagnostic, truncated = false, followUpRequired = false,
            evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Rejected, "cerebral.plugin-sdk")
        }), diagnostic);

        public void FinalizationPartial() => Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
        {
            name = PendingTool, content = "Presentation may have been queued", truncated = false, followUpRequired = false,
            evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", Partial: true)
        }));

        public void FinalizationWrite(string file)
        {
            const string content = "class Changed { }\n";
            var path = Path.Combine(Root, file);
            QueueGuardWrite(path, content, "replace", new string('a', 64));
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                name = "file.write", content = JsonSerializer.Serialize(new { path,
                    bytes = Encoding.UTF8.GetByteCount(content), sha256 = new string('b', 64) }),
                truncated = false, followUpRequired = false,
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "deterministic-fixture", Partial: false)
            }));
        }

        public void FinalizationCreate(string file)
        {
            const string content = "class NewContribution { }\n";
            var path = Path.Combine(Root, file);
            QueueGuardWrite(path, content, "fail", null);
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                name = "file.write", content = JsonSerializer.Serialize(new { path,
                    bytes = Encoding.UTF8.GetByteCount(content),
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant() }),
                truncated = false, followUpRequired = false,
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "deterministic-fixture", Partial: false)
            }));
        }
    }
}
