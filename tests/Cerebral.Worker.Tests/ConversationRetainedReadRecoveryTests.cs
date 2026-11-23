using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunRetainedReadRecoveryChecks(Action<bool, string> check)
    {
        static JsonElement Recovery(Fixture fixture) => fixture.Step.State.GetProperty("readRecovery");
        static int Reuses(Fixture fixture) => Recovery(fixture).GetProperty("reuses").GetInt32();
        static object Read(string path, int startLine = 1, int lineCount = 200) =>
            new { action = "tool", name = "file.read", arguments = new { path, startLine, lineCount } };
        static Fixture ReadFixture(string content = "Retained immutable evidence.")
        {
            var fixture = new Fixture(); fixture.Describe("file.read"); fixture.Catalog(exact: true);
            fixture.Model(Read("existing.txt")); fixture.Tool(true, content);
            return fixture;
        }

        var reuse = ReadFixture();
        var original = reuse.Context.Contributions.Single(source => source.Id.StartsWith("tool.", StringComparison.Ordinal));
        reuse.ModelRaw("{invalid");
        var repairs = reuse.Step.State.GetProperty("repairs").GetInt32();
        for (var count = 1; count <= 3; count++)
        {
            reuse.Model(Read("existing.txt"));
            check(reuse.Is("generate") && Reuses(reuse) == count &&
                reuse.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
                "new-run cached reuse " + count + " continues without redispatch or a third-hit terminal failure");
        }
        var represented = reuse.Context.Contributions.Single(source => source.Id == original.Id);
        check(reuse.Step.State.GetProperty("repairs").GetInt32() == repairs &&
            reuse.Step.State.GetProperty("repeatedReadCorrections").GetInt32() == 0 &&
            reuse.Step.State.GetProperty("completionMilestones").EnumerateObject().Count() == 0,
            "cache reuse neither spends/reset malformed-action attempts nor creates execution milestones");
        check(represented.SourceRevision == original.SourceRevision && represented.Complete == original.Complete &&
            represented.ExactReadHandle == original.ExactReadHandle &&
            JsonElement.DeepEquals(CerebralJson.Element(represented.Messages), CerebralJson.Element(original.Messages)) &&
            represented.Requirement == ContextRequirement.Required &&
            reuse.LastContributionText.Contains("\"freshRead\":false", StringComparison.Ordinal) &&
            reuse.LastContributionText.Contains("\"sameEvidenceUses\":3", StringComparison.Ordinal),
            "reuse presents the original required body once with unchanged provenance and explicit unchanged-evidence status");

        var restored = reuse.Fork();
        restored.RestoreState(JsonDocument.Parse(reuse.Step.State.GetRawText()).RootElement.Clone());
        restored.Model(Read("existing.txt"));
        check(restored.Is("generate") && Reuses(restored) == 4,
            "durable reopen carries spent reuse attempts rather than replenishing a window");
        for (var count = 4; count <= 8; count++) reuse.Model(Read("existing.txt"));
        check(reuse.Is("generate") && Reuses(reuse) == 8 &&
            reuse.LastContributionText.Contains("\"remaining\":0", StringComparison.Ordinal) &&
            reuse.LastContributionText.Contains("return blocked", StringComparison.Ordinal),
            "the final bounded reuse still offers an actionable recovery or explicit blocker decision");
        reuse.Model(Read("existing.txt"));
        check(reuse.Step.Next is CompleteWorkflow { Success: false } && Reuses(reuse) == 8 &&
            reuse.Summary.Contains("recovery window is exhausted", StringComparison.Ordinal) &&
            reuse.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "persistently unchanged reuse eventually stops truthfully with its original receipt and spent window");

        var corrected = ReadFixture("{\"value\":1}");
        for (var count = 0; count < 3; count++) corrected.Model(Read("existing.txt"));
        corrected.Model(Read("different.txt"));
        check(corrected.Is("tool"), "corrected arguments requesting different needed evidence still dispatch normally");
        corrected.Tool(true, " { \"value\" : 1 } ");
        check(Reuses(corrected) == 3,
            "different arguments and cosmetic JSON returning the same outcome do not replenish recovery");
        corrected.Model(Read("different.txt"));
        corrected.Model(Read("existing.txt"));
        check(corrected.Is("generate") && Reuses(corrected) == 5 &&
            Recovery(corrected).GetProperty("evidenceUses").GetArrayLength() == 2,
            "alternating retained keys shares a bounded no-progress window while tracking each original receipt");
        corrected.Model(Read("new-evidence.txt")); corrected.Tool(true, "{\"value\":2}");
        check(Reuses(corrected) == 0 && Recovery(corrected).GetProperty("lastProgress").GetString() == "new-read-evidence",
            "a novel actual tool result permits a fresh correction window without erasing effect history");
        corrected.Model(Read("new-evidence.txt"));
        check(corrected.Step.State.GetProperty("toolReceipts").GetArrayLength() == 3 && Reuses(corrected) == 1,
            "reusing newly obtained evidence still creates no additional tool success");

        var cosmetic = ReadFixture("unchanged data");
        cosmetic.Model(Read("existing.txt"));
        cosmetic.Describe("file.inspect"); cosmetic.Catalog(exact: true);
        cosmetic.Model(new { action = "tool", name = "file.inspect", arguments = new { path = "existing.txt" } });
        cosmetic.Tool(true, "Inspection does not prove a changed source revision.");
        cosmetic.Model(Read("existing.txt")); cosmetic.Tool(true, "unchanged data");
        check(Reuses(cosmetic) == 1,
            "schema discovery, explicit inspection and unchanged fresh outcome preserve the spent recovery window");
        cosmetic.Model(Read("existing.txt"));
        check(Reuses(cosmetic) == 2, "an invalidated then reloaded identical read remains part of the same stalled window");

        var saturated = ReadFixture("novel-0");
        for (var index = 1; index <= 7; index++)
        {
            saturated.Model(Read("novel-" + index + ".txt")); saturated.Tool(true, "novel-" + index);
        }
        saturated.Model(Read("novel-7.txt"));
        saturated.Model(Read("novel-8.txt")); saturated.Tool(true, "novel-8");
        check(Reuses(saturated) == 1 && Recovery(saturated).GetProperty("observedOutcomes").GetArrayLength() == 8,
            "the bounded outcome history never evicts old digests to grant endless refresh-based recovery windows");

        var covered = new Fixture(); covered.Describe("file.read"); covered.Catalog(exact: true);
        var path = Path.Combine(Path.GetTempPath(), "RecoveryCoverage.cs");
        covered.Model(Read(path, 1, 2));
        covered.Tool(true, JsonSerializer.Serialize(new { path, sha256 = new string('a', 64), startLine = 1, totalLines = 4, content = "one\ntwo" }));
        covered.Model(Read(path, 2, 1)); covered.Model(Read(path, 1, 1));
        check(covered.Is("generate") && Reuses(covered) == 2 &&
            Recovery(covered).GetProperty("evidenceUses").GetArrayLength() == 1 &&
            covered.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "different covered subranges resolve to one immutable evidence identity without new reads");
        covered.Model(Read(path, 3, 2));
        check(covered.Is("tool"), "the real missing source range remains dispatchable after covered-range reuse");

        var limited = new Fixture(); limited.Describe("plugin.projects"); limited.Catalog(exact: true);
        limited.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        limited.Tool(true, JsonSerializer.Serialize(new
        {
            discoverySummaryOnly = true, scaffoldReady = false, limited = true,
            incompleteReason = "Directory scan limit reached.", count = 0, projects = Array.Empty<object>()
        }), provider: "cerebral.plugin-sdk");
        for (var count = 0; count < 3; count++) limited.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        check(limited.Is("generate") && Reuses(limited) == 3 &&
            !limited.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean() &&
            limited.LastContributionText.Contains("complete identity coverage", StringComparison.Ordinal) &&
            limited.LastContributionText.Contains("supplied by the actual result", StringComparison.Ordinal) &&
            limited.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "limited inventory reuse permits corrections but preserves the true discovery blocker and denied scaffold readiness");
        foreach (var field in new[] { "count", "projects" })
        {
            var duplicate = new Fixture(); duplicate.Describe("plugin.projects"); duplicate.Catalog(exact: true);
            duplicate.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
            var extra = field == "count" ? "\"count\":0," : "\"projects\":[],";
            duplicate.Tool(true, "{" + extra + "\"count\":0,\"projects\":[],\"limited\":false,\"scaffoldReady\":true,\"discoverySummaryOnly\":true}",
                provider: "cerebral.plugin-sdk");
            check(duplicate.Is("generate") && !duplicate.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean() &&
                duplicate.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
                "duplicate inventory " + field + " keeps readiness denied and preserves the actual receipt through opaque outcome hashing");
        }

        var continuation = new Fixture(); continuation.Describe("plugin.projects"); continuation.Catalog(exact: true);
        var firstToken = new string('a', 32); var nextToken = new string('b', 32);
        continuation.Model(new { action = "tool", name = "plugin.projects", arguments = new { continuation = firstToken } });
        continuation.Tool(true, "Actual first continuation page.");
        continuation.Model(new { action = "tool", name = "plugin.projects", arguments = new { continuation = firstToken } });
        check(continuation.Is("generate") && Reuses(continuation) == 1 &&
            continuation.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "an exact retained continuation token reuses its page rather than redispatching it");
        continuation.Model(new { action = "tool", name = "plugin.projects", arguments = new { continuation = nextToken } });
        check(continuation.Is("tool"), "a distinct supported continuation token remains a new provider request");

        foreach (var corruption in new[] { "version", "run", "limit", "sum", "outcome" })
        {
            var corrupt = restored.Fork();
            var node = JsonNode.Parse(corrupt.Step.State.GetRawText())!.AsObject();
            var policy = node["readRecovery"]!;
            if (corruption == "version") policy["schemaVersion"] = 2;
            if (corruption == "run") policy["runId"] = "another-run";
            if (corruption == "limit") policy["reuseLimit"] = 999;
            if (corruption == "sum") policy["reuses"] = 0;
            if (corruption == "outcome") policy["observedOutcomes"]!.AsArray().Add("invalid-digest");
            corrupt.RestoreState(CerebralJson.Element(node)); corrupt.Model(Read("existing.txt"));
            check(corrupt.Step.Next is CompleteWorkflow { Success: false } &&
                corrupt.Summary.Contains("retained-read recovery", StringComparison.Ordinal),
                "invalid saved recovery " + corruption + " refuses before any next tool dispatch");
        }

        var required = new Fixture(input: new { requiredSuccessfulTools = new[] { "file.write" } });
        required.Describe("file.read"); required.Catalog(exact: true);
        required.Model(Read("existing.txt")); required.Tool(true, "Retained evidence.");
        required.Model(Read("existing.txt")); required.Model(new { action = "final", text = "Completed." });
        check(required.Is("generate") && required.LastContributionText.Contains("required tools", StringComparison.Ordinal) &&
            required.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "evidence reuse never satisfies a required mutation or completion obligation");
        RunRetainedReadWorkProgressChecks(check);
    }

    private static void RunRetainedReadWorkProgressChecks(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RecoveryWork", "Plugin"));
        var directory = Path.Combine(root, "Desktop");
        var source = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var built = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var buildArgs = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" };
        var schema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var fixture = new Fixture(warm: false);
        fixture.History([]);
        fixture.Catalog(forcedNames: ["plugin.projects", "plugin.contract", "file.read", "file.edit", "process.run"]);
        fixture.Select("plugin.projects", schema);
        fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        fixture.Tool(true, JsonSerializer.Serialize(new
        {
            count = 1, limited = false, desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            projects = new[] { new { root, components = new[] { new
            {
                component = "desktop", source, manifest = Path.Combine(directory, "desktop-plugin.json"),
                build = new { executableId = "dotnet", arguments = buildArgs, workingDirectory = directory, builtManifest = built }
            } } } }
        }), provider: "cerebral.plugin-sdk");
        fixture.Select("file.read", schema);
        fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        fixture.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('a', 64), startLine = 1, totalLines = 1, content = "old" }));
        fixture.Select("plugin.contract", schema);
        var contract = new { action = "tool", name = "plugin.contract", arguments = new { topic = "storage" } };
        fixture.Model(contract); fixture.Tool(true, "Stable storage guidance.");
        fixture.Model(contract); fixture.Model(contract); fixture.Model(contract);
        fixture.Select("file.edit", schema);
        fixture.Model(new { action = "tool", name = "file.edit", arguments = new
            { path = source, oldText = "old", newText = "new", expectedSha256 = new string('a', 64) } });
        fixture.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 3 }));
        check(fixture.Is("generate") && fixture.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1 &&
            fixture.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32() == 0 &&
            fixture.Step.State.GetProperty("readRecovery").GetProperty("lastProgress").GetString() == "source-change",
            "a confirmed actual bound-source mutation replenishes recovery while retaining all prior receipts");
        fixture.Model(contract); fixture.Tool(true, "Stable storage guidance."); fixture.Model(contract);
        fixture.Select("process.run", schema);
        fixture.Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = buildArgs, workingDirectory = directory } });
        fixture.Tool(true, "Actual fixture canonical build succeeded.", exitCode: 0);
        check(fixture.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32() == 0 &&
            fixture.Step.State.GetProperty("readRecovery").GetProperty("lastProgress").GetString() == "verified-milestone",
            "a newly proved current-generation canonical build opens a recovery window without altering earlier failure history");
        fixture.Model(contract); fixture.Tool(true, "Stable storage guidance."); fixture.Model(contract);
        fixture.Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = new[] { "--info" }, workingDirectory = directory } });
        fixture.Tool(true, "Unrelated successful process observation.", exitCode: 0);
        check(fixture.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null &&
            fixture.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32() == 1,
            "unrelated success that invalidates prior build proof is not a new verified milestone and cannot replenish recovery");
    }
}
