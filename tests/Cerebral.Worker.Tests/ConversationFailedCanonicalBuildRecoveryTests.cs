using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunFailedCanonicalBuildRecoveryChecks(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FailedBuildReuse", "Plugin"));
        var directory = Path.Combine(root, "Desktop");
        var source = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var schema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var hash = new string('a', 64);
        string[] BuildArgs(string target) => ["build", Path.Combine(target, "Desktop", "Cerebral.GeneratedPlugin.Desktop.csproj"),
            "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal"];
        object BuildAt(string target, bool alias = false) => new { action = "tool", name = "process.run", arguments = new
        {
            executableId = "dotnet", arguments = alias ? new[] { "build", Path.GetFileName(project), "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" } : BuildArgs(target),
            workingDirectory = alias ? Path.Combine(target, "Desktop", ".") : Path.Combine(target, "Desktop")
        } };
        object Build(bool alias = false) => BuildAt(root, alias);
        string SourceBody(string sha = "", string content = "before", string? path = null) => JsonSerializer.Serialize(new
            { path = path ?? source, sha256 = sha.Length == 0 ? hash : sha, startLine = 1, totalLines = 1, content });
        void Bind(Fixture fixture, string target, bool readSource, bool omitSource = false, string? sourceOverride = null)
        {
            var targetDirectory = Path.Combine(target, "Desktop");
            var targetSource = sourceOverride ?? Path.Combine(targetDirectory, "GeneratedDesktopPlugin.cs");
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root = target } });
            if (fixture.Is("tool"))
            {
                var inventory = JsonSerializer.SerializeToNode(new
            {
                count = 1, limited = false, desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
                projects = new[] { new { root = target, components = new[] { new
                {
                    component = "desktop", source = targetSource, manifest = Path.Combine(targetDirectory, "desktop-plugin.json"),
                    build = new { executableId = "dotnet", arguments = BuildArgs(target), workingDirectory = targetDirectory,
                        builtManifest = Path.Combine(targetDirectory, "bin", "Release", "net10.0-windows", "desktop-plugin.json") }
                } } } }
            })!;
                if (omitSource) inventory["projects"]![0]!["components"]![0]!.AsObject().Remove("source");
                fixture.Tool(true, inventory.ToJsonString(), provider: "cerebral.plugin-sdk");
            }
            if (readSource)
            {
                fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = targetSource } });
                fixture.Tool(true, SourceBody(path: targetSource), omitEvidence: true);
            }
        }
        Fixture Ready(string requestId = "failed-build-run", bool sourceProof = true, bool omitSource = false,
            string? sourceOverride = null)
        {
            var fixture = new Fixture(warm: false, input: new { workingDirectory = Path.GetDirectoryName(root)! }, requestId: requestId);
            fixture.History([]);
            fixture.Catalog(forcedNames: ["plugin.projects", "file.read", "file.edit", "file.copy", "process.run", "plugin.verify", "plugin.present"]);
            fixture.Select("plugin.projects", schema);
            fixture.Select("file.read", schema);
            Bind(fixture, root, sourceProof, omitSource, sourceOverride);
            fixture.Select("process.run", schema);
            fixture.Model(Build());
            check(fixture.Is("tool"), "the first canonical build is dispatched through ordinary admission");
            return fixture;
        }
        void Fail(Fixture fixture, string kind = "complete", string? diagnosticSource = null)
        {
            var body = kind == "malformed" ? "{\"exitCode\":1,\"exitCode\":0,\"stdout\":\"x\",\"stderr\":\"\"}" :
                JsonSerializer.Serialize(new { exitCode = kind == "wrong-exit" ? 2 : 1,
                    stdout = (diagnosticSource ?? source) + "(12,20): error CS0246: FrameworkElement could not be found\n0 Warning(s)\n1 Error(s)", stderr = "" });
            fixture.ActualToolResult(new ToolResult(false, body)
            {
                Truncated = kind == "truncated",
                Evidence = new(kind == "rejected" ? ToolExecutionOutcome.Rejected : ToolExecutionOutcome.Failed,
                    kind == "provider" ? "unrelated.process" : "cerebral.process", State: "completed",
                    Partial: kind == "partial" ? true : null, ExitCode: 1)
            });
        }
        static int Count(Fixture fixture) => fixture.Step.State.GetProperty("toolReceipts").GetArrayLength();
        static int Reuses(Fixture fixture) => fixture.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32();
        static JsonElement Observation(Fixture fixture) => fixture.Step.State.GetProperty("pluginWork").GetProperty("failedBuildObservation");

        var scopedLocator = Ready("failed-build-scope-locator");
        Fail(scopedLocator);
        scopedLocator.Model(new { action = "tool", name = "file.read", arguments = new
        {
            path = Path.Combine(Path.GetTempPath(), "wrong-project", "GeneratedDesktopPlugin.cs"), startLine = 12, lineCount = 20
        } });
        scopedLocator.Tool(false, "The path is outside this conversation's current access scope.",
            outcome: ToolExecutionOutcome.Rejected, partial: false);
        check(scopedLocator.LastContributionText.Contains(JsonSerializer.Serialize(source, CerebralJson.Options), StringComparison.Ordinal),
            "a scoped denial names the selected inspected source locator");
        check(scopedLocator.LastContributionText.Contains("locator only, not a current hash or a new path grant", StringComparison.Ordinal),
            "a scoped source locator makes no current hash or path grant claim");
        check(scopedLocator.LastContributionText.Contains("\"action\":\"receipt\"", StringComparison.Ordinal),
            "a scoped denial offers the exact same-run failed command receipt");

        var longSource = Path.Combine(directory,
            string.Concat(Enumerable.Repeat("界\\", 950)), "GeneratedDesktopPlugin.cs");
        var serializedSource = JsonSerializer.Serialize(source, CerebralJson.Options);
        var serializedLongSource = JsonSerializer.Serialize(longSource, CerebralJson.Options);
        check(longSource.Length <= 2048 &&
            scopedLocator.LastContributionText.Contains(serializedSource, StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(scopedLocator.LastContributionText.Replace(
                serializedSource, serializedLongSource, StringComparison.Ordinal)) > 8192,
            "the admitted escaped source coordinate would exceed the complete tool-result projection budget");
        var longLocator = Ready("failed-build-long-scope-locator", sourceProof: false, sourceOverride: longSource);
        check(longLocator.Step.State.GetProperty("pluginWork").GetProperty("build").GetProperty("sourceFile").GetString() == longSource,
            "the long source coordinate is admitted into the selected component before the denied read");
        Fail(longLocator);
        var longReceipt = longLocator.Step.State.GetProperty("toolReceipts").EnumerateArray().Last()
            .GetProperty("effectId").GetString()!;
        longLocator.Model(new { action = "tool", name = "file.read", arguments = new
        {
            path = Path.Combine(Path.GetTempPath(), "wrong-project", "GeneratedDesktopPlugin.cs"), startLine = 12, lineCount = 20
        } });
        longLocator.Tool(false, "The path is outside this conversation's current access scope.",
            outcome: ToolExecutionOutcome.Rejected, partial: false);
        check(Encoding.UTF8.GetByteCount(longLocator.LastContributionText) <= 8192 &&
            longLocator.LastContributionText.Contains("\"action\":\"receipt\"", StringComparison.Ordinal) &&
            longLocator.LastContributionText.Contains(longReceipt, StringComparison.Ordinal) &&
            !longLocator.LastContributionText.Contains(serializedLongSource, StringComparison.Ordinal) &&
            !longLocator.LastContributionText.Contains("Previously inspected project source locator", StringComparison.Ordinal) &&
            !longLocator.LastContributionText.Contains("new path grant", StringComparison.Ordinal),
            "an oversized escaped source locator is omitted without losing bounded receipt guidance or adding a grant");

        var unchanged = Ready(); Fail(unchanged);
        var failure = Observation(unchanged).Clone();
        var receiptId = failure.GetProperty("effectId").GetString()!;
        var count = Count(unchanged);
        var originalReceipt = unchanged.Step.State.GetProperty("toolReceipts").EnumerateArray().Single(x => x.GetProperty("effectId").GetString() == receiptId).Clone();
        check(failure.GetProperty("sourceRevision").GetProperty("sha256").GetString() == hash &&
            failure.GetProperty("bodySha256").GetString() == originalReceipt.GetProperty("canonicalProof").GetProperty("bodySha256").GetString() &&
            originalReceipt.GetProperty("canonicalProof").GetProperty("completeFailure").GetBoolean(),
            "complete failure pins its exact raw-body digest, original receipt, recipe and successful source fingerprint");
        check(unchanged.Step.State.GetProperty("toolReceipts").EnumerateArray().Any(receipt =>
            receipt.GetProperty("name").GetString() == "file.read" && receipt.GetProperty("outcome").ValueKind == JsonValueKind.Null &&
            receipt.TryGetProperty("canonicalProof", out var proof) && proof.GetProperty("sourceRevision").GetProperty("sha256").GetString() == hash),
            "the actual stock file-tool shape with null Evidence/Outcome supplies a pinned successful source fingerprint");
        unchanged.Model(Build());
        check(unchanged.Is("generate") && Count(unchanged) == count && Reuses(unchanged) == 1 &&
            unchanged.LastContributionText.Contains("\"freshExecution\":false", StringComparison.Ordinal) &&
            unchanged.LastContributionText.Contains(receiptId, StringComparison.Ordinal),
            "unchanged failed build A is represented as failure without a new tool effect or receipt");
        unchanged.Model(Build(alias: true));
        check(unchanged.Is("generate") && Count(unchanged) == count && Reuses(unchanged) == 2,
            "canonical project and working-directory aliases cannot bypass failed-build reuse");
        check(unchanged.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null &&
            JsonElement.DeepEquals(originalReceipt, unchanged.Step.State.GetProperty("toolReceipts").EnumerateArray().Single(x => x.GetProperty("effectId").GetString() == receiptId)) &&
            unchanged.Context.Contributions.Single(x => x.Id == "known-build-failure." + receiptId).ExactReadHandle == receiptId,
            "failure reuse preserves the immutable failed receipt and exact provenance without success or milestone credit");
        var reopened = unchanged.Fork(); reopened.RestoreState(unchanged.Step.State.Clone()); reopened.Model(Build());
        check(reopened.Is("generate") && Reuses(reopened) == 3 && Count(reopened) == count,
            "durable reopen retains spent failed-build reuse counters");
        for (var reuse = 3; reuse <= 8; reuse++) unchanged.Model(Build(alias: reuse % 2 == 0));
        check(unchanged.Is("generate") && Reuses(unchanged) == 8 && Count(unchanged) == count,
            "the eighth unchanged failure representation still offers a corrective model turn");
        unchanged.Model(Build());
        check(unchanged.Step.Next is CompleteWorkflow { Success: false } && Reuses(unchanged) == 8 && Count(unchanged) == count,
            "a ninth unchanged failed-build request stops at the shared bounded no-progress window");

        var noop = Ready(); Fail(noop); noop.Model(Build());
        noop.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        noop.Tool(true, SourceBody(), omitEvidence: true);
        noop.Select("file.edit", schema);
        noop.Model(new { action = "tool", name = "file.edit", arguments = new { path = source, oldText = "before", newText = "after", expectedSha256 = hash } });
        noop.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 6, lines = 1, words = 1 }), omitEvidence: true);
        var noopCount = Count(noop); noop.Model(Build());
        check(noop.Is("generate") && Count(noop) == noopCount && Reuses(noop) == 2 &&
            noop.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 0,
            "a successful edit returning unchanged source bytes neither grants progress nor repeats the failed build");

        var corrected = Ready(); Fail(corrected); corrected.Model(Build());
        corrected.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        corrected.Tool(true, SourceBody(), omitEvidence: true);
        corrected.Select("file.edit", schema);
        corrected.Model(new { action = "tool", name = "file.edit", arguments = new { path = source, oldText = "before", newText = "after", expectedSha256 = hash } });
        corrected.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 5, lines = 1, words = 1 }), omitEvidence: true);
        check(Reuses(corrected) == 0 && corrected.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1,
            "a real confirmed source correction replenishes the existing recovery window");
        corrected.Model(Build());
        check(corrected.Is("tool"), "changed source generation permits a new canonical compiler observation");
        corrected.ActualToolResult(new ToolResult(true, "{\"exitCode\":0,\"stdout\":\"Build succeeded\",\"stderr\":\"\"}")
            { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.process", State: "completed", ExitCode: 0) });
        check(corrected.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").GetInt32() == 1 &&
            (!corrected.Step.State.GetProperty("pluginWork").TryGetProperty("failedBuildObservation", out var cleared) || cleared.ValueKind == JsonValueKind.Null),
            "only a real successful compiler receipt establishes build success and clears the pending failure guard");

        // Successful B does not erase the earlier failed A fingerprint. Reverting to A
        // is a genuine edit but still cannot justify repeating the known failed compiler work.
        corrected.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        corrected.Tool(true, SourceBody(new string('b', 64), "after"), omitEvidence: true);
        corrected.Model(new { action = "tool", name = "file.edit", arguments = new
            { path = source, oldText = "after", newText = "before", expectedSha256 = new string('b', 64) } });
        corrected.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 6, lines = 1, words = 1 }), omitEvidence: true);
        var revertedCount = Count(corrected); corrected.Model(Build());
        check(corrected.Is("generate") && Count(corrected) == revertedCount && Reuses(corrected) == 1 &&
            corrected.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 2 &&
            corrected.LastContributionText.Contains(receiptId, StringComparison.Ordinal),
            "source A-to-B-to-A uses the original failed A receipt despite a newer generation and successful B build");

        var missing = Ready(sourceProof: false); Fail(missing); var missingCount = Count(missing);
        for (var turn = 1; turn <= 8; turn++) missing.Model(Build());
        check(missing.Is("generate") && Reuses(missing) == 8 && Count(missing) == missingCount &&
            missing.LastContributionText.Contains("\"sourceProofMissing\":true", StringComparison.Ordinal) &&
            missing.LastContributionText.Contains(JsonSerializer.Serialize(source), StringComparison.Ordinal),
            "a complete failed compiler result without a source fingerprint offers eight precise non-dispatch source-proof corrections");
        missing.Model(Build());
        check(missing.Step.Next is CompleteWorkflow { Success: false } && Reuses(missing) == 8 && Count(missing) == missingCount,
            "missing-source corrections spend the same bounded window and never manufacture another compiler execution");

        var alternating = Ready(); Fail(alternating);
        var otherRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FailedBuildReuse", "OtherPlugin"));
        Bind(alternating, otherRoot, readSource: true); alternating.Model(BuildAt(otherRoot));
        check(alternating.Is("tool"), "an independent canonical target obtains its own first compiler observation");
        Fail(alternating, diagnosticSource: Path.Combine(otherRoot, "Desktop", "GeneratedDesktopPlugin.cs"));
        Bind(alternating, root, readSource: false);
        var targetCount = Count(alternating);
        for (var turn = 1; turn <= 8; turn++) alternating.Model(BuildAt(turn % 2 == 0 ? otherRoot : root));
        check(alternating.Is("generate") && Reuses(alternating) == 8 && Count(alternating) == targetCount &&
            alternating.Step.State.GetProperty("failedBuildRecovery").GetProperty("failures").GetArrayLength() == 2,
            "A/B project selection preserves both exact failed targets and shares one eight-use no-progress window");
        alternating.Model(BuildAt(otherRoot));
        check(alternating.Step.Next is CompleteWorkflow { Success: false } && Count(alternating) == targetCount,
            "alternating known failed targets cannot evade the exhausted shared recovery window");

        void ReadInput(Fixture fixture, string path, string sha, string content)
        {
            fixture.Model(new { action = "tool", name = "file.read", arguments = new { path } });
            fixture.Tool(true, SourceBody(sha, content, path), omitEvidence: true);
        }
        void EditInput(Fixture fixture, string path, string beforeSha, string afterSha, string beforeText, string afterText)
        {
            if (!fixture.Step.State.GetProperty("selected").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "file.edit"))
                fixture.Select("file.edit", schema);
            fixture.Model(new { action = "tool", name = "file.edit", arguments = new { path, oldText = beforeText, newText = afterText, expectedSha256 = beforeSha } });
            fixture.Tool(true, JsonSerializer.Serialize(new { path, sha256 = afterSha, bytes = afterText.Length, lines = 1, words = 1 }), omitEvidence: true);
        }
        var projectRepair = Ready(); Fail(projectRepair);
        var plainProject = "<Project Sdk=\"Microsoft.NET.Sdk\" />";
        var changedProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>";
        ReadInput(projectRepair, project, new string('e', 64), plainProject);
        EditInput(projectRepair, project, new string('e', 64), new string('f', 64), plainProject, changedProject);
        projectRepair.Model(Build());
        check(projectRepair.Is("tool"), "a producer-proved csproj repair permits a compiler observation with unchanged primary C# bytes");
        Fail(projectRepair);
        ReadInput(projectRepair, project, new string('f', 64), changedProject);
        EditInput(projectRepair, project, new string('f', 64), new string('e', 64), changedProject, plainProject);
        var projectRevertCount = Count(projectRepair); projectRepair.Model(Build());
        check(projectRepair.Is("generate") && Count(projectRepair) == projectRevertCount &&
            projectRepair.LastContributionText.Contains(receiptId, StringComparison.Ordinal),
            "reverting project input B to A restores the original observed input identity and reuses A's failed receipt");

        foreach (var linked in new[] { false, true })
        {
            var dependency = Ready(); Fail(dependency);
            var dependentSource = Path.Combine(root, "Shared", linked ? "Linked.cs" : "Helper.cs");
            var declaration = linked ? "<Project><ItemGroup><Compile Include=\"../Shared/Linked.cs\" /></ItemGroup></Project>" :
                "<Project><ItemGroup><ProjectReference Include=\"../Shared/Helper.csproj\" /></ItemGroup></Project>";
            ReadInput(dependency, project, new string('e', 64), declaration);
            ReadInput(dependency, dependentSource, new string('c', 64), "before");
            EditInput(dependency, dependentSource, new string('c', 64), new string('d', 64), "before", "after");
            dependency.Model(Build());
            check(dependency.Is("tool"), "a literal " + (linked ? "linked Compile" : "ProjectReference") + " source repair permits a build without changing the primary file");
            Fail(dependency);
            ReadInput(dependency, dependentSource, new string('d', 64), "after");
            EditInput(dependency, dependentSource, new string('d', 64), new string('c', 64), "after", "before");
            var dependencyCount = Count(dependency); dependency.Model(Build());
            check(dependency.Is("generate") && Count(dependency) == dependencyCount &&
                dependency.LastContributionText.Contains(receiptId, StringComparison.Ordinal),
                "a real dependent-input revert restores the original failed identity for " + (linked ? "linked Compile" : "ProjectReference"));
        }
        foreach (var extension in new[] { "props", "targets" })
        {
            var ancestor = Ready(); Fail(ancestor);
            var ancestorPath = Path.Combine(Path.GetDirectoryName(root)!, "Directory.Build." + extension);
            ReadInput(ancestor, ancestorPath, new string('c', 64), "before");
            ancestor.Model(Build());
            check(Reuses(ancestor) == 1, "ancestor discovery alone does not grant a fresh compiler observation");
            EditInput(ancestor, ancestorPath, new string('c', 64), new string('d', 64), "before", "after");
            check(Reuses(ancestor) == 0 && ancestor.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1,
                "nullable-evidence ancestor " + extension + " repair resets shared recovery through the recognized related-input invalidation path");
            ancestor.Model(Build()); check(ancestor.Is("tool"), "authorized ancestor " + extension + " correction permits the canonical build");
            Fail(ancestor);
            ReadInput(ancestor, ancestorPath, new string('d', 64), "after");
            EditInput(ancestor, ancestorPath, new string('d', 64), new string('c', 64), "after", "before");
            var ancestorCount = Count(ancestor); ancestor.Model(Build());
            check(ancestor.Is("generate") && Count(ancestor) == ancestorCount,
                "ancestor " + extension + " input A-to-B-to-A retains the original failed identity");
        }
        var copied = Ready(); Fail(copied);
        var copiedSource = Path.Combine(directory, "ImportedHelper.cs");
        copied.Select("file.copy", schema);
        copied.Model(new { action = "tool", name = "file.copy", arguments = new { sourcePath = Path.Combine(root, "Template.cs"), destinationPath = copiedSource } });
        copied.Tool(true, JsonSerializer.Serialize(new { sourcePath = Path.Combine(root, "Template.cs"), destinationPath = copiedSource, bytes = 7, sha256 = new string('d', 64) }), omitEvidence: true);
        var copyProof = copied.Step.State.GetProperty("toolReceipts").EnumerateArray().Last().GetProperty("canonicalProof");
        check(copyProof.GetProperty("sourceRevision").GetProperty("path").GetString() == copiedSource &&
            copyProof.GetProperty("sourceRevision").GetProperty("sha256").GetString() == new string('d', 64),
            "actual file.copy destinationPath/bytes/sha256 shape supplies exact destination provenance with null Evidence");
        var copiedRestored = copied.Fork(); copiedRestored.RestoreState(copied.Step.State.Clone()); copiedRestored.Model(Build());
        check(copiedRestored.Is("tool"), "a genuine copied input repair survives snapshot validation and permits a build");

        var manyInputs = Ready(); Fail(manyInputs);
        string lastExtra = "";
        for (var index = 0; index < 17; index++)
        {
            lastExtra = Path.Combine(directory, "Extra" + index + ".cs");
            ReadInput(manyInputs, lastExtra, new string('c', 64), "before");
        }
        EditInput(manyInputs, lastExtra, new string('c', 64), new string('d', 64), "before", "after");
        manyInputs.Model(Build());
        check(manyInputs.Is("tool"), "a genuine correction beyond sixteen related files remains buildable under the existing workflow resource bounds");
        Fail(manyInputs);
        ReadInput(manyInputs, lastExtra, new string('d', 64), "after");
        EditInput(manyInputs, lastExtra, new string('d', 64), new string('c', 64), "after", "before");
        var manyCount = Count(manyInputs); manyInputs.Model(Build());
        check(manyInputs.Is("generate") && Count(manyInputs) == manyCount,
            "receipt-derived identity remains bounded and recognizes a full-input revert beyond sixteen coordinates");

        var projectionOverflow = Ready(); Fail(projectionOverflow);
        var previousHash = hash;
        var previousText = "before";
        for (var revision = 1; revision <= 8; revision++)
        {
            ReadInput(projectionOverflow, source, previousHash, previousText);
            var nextHash = revision.ToString("x64");
            var nextText = "correction-" + revision;
            EditInput(projectionOverflow, source, previousHash, nextHash, previousText, nextText);
            projectionOverflow.Model(Build());
            check(projectionOverflow.Is("tool"), "projection bookkeeping capacity does not prohibit genuine correction " + revision);
            Fail(projectionOverflow); previousHash = nextHash; previousText = nextText;
        }
        var overflowCount = Count(projectionOverflow); projectionOverflow.Model(Build());
        check(projectionOverflow.Is("generate") && Count(projectionOverflow) == overflowCount &&
            projectionOverflow.Step.State.GetProperty("failedBuildRecovery").GetProperty("failures").GetArrayLength() == 8 &&
            projectionOverflow.Step.State.GetProperty("failedBuildRecovery").GetProperty("projectionCacheDegraded").GetBoolean() &&
            projectionOverflow.LastContributionText.Contains("\"diagnosticOmitted\":true", StringComparison.Ordinal),
            "full projection cache degrades explicitly to exact receipt-derived failure feedback without blocking projects or redispatching unchanged work");

        var crossSelected = Ready(); Fail(crossSelected);
        var siblingRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FailedBuildReuse", "OtherPlugin"));
        var siblingSource = Path.Combine(siblingRoot, "Desktop", "GeneratedDesktopPlugin.cs");
        ReadInput(crossSelected, project, new string('e', 64),
            "<Project><ItemGroup><ProjectReference Include=\"../../OtherPlugin/Desktop/Cerebral.GeneratedPlugin.Desktop.csproj\" /></ItemGroup></Project>");
        Bind(crossSelected, siblingRoot, readSource: true);
        EditInput(crossSelected, siblingSource, hash, new string('b', 64), "before", "after");
        Bind(crossSelected, root, readSource: false);
        crossSelected.Model(Build());
        check(crossSelected.Is("tool") && crossSelected.Command.Arguments.GetProperty("name").GetString() == "process.run",
            "a dependency repair while B is selected changes A's declared input identity when selection returns to A");

        var completedInput = Ready();
        completedInput.ActualToolResult(new ToolResult(true, "{\"exitCode\":0,\"stdout\":\"Build succeeded\",\"stderr\":\"\"}")
            { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.process", State: "completed", ExitCode: 0) });
        var builtManifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var packageHash = new string('4', 64);
        completedInput.Select("plugin.verify", schema);
        completedInput.Model(new { action = "tool", name = "plugin.verify", arguments = new { root } });
        completedInput.Tool(true, JsonSerializer.Serialize(new { root, verified = true,
            desktop = new { manifest = builtManifest, packageFingerprint = packageHash } }), provider: "cerebral.plugin-sdk");
        completedInput.Select("plugin.present", CerebralJson.Element(new { type = "object", properties = new
            { path = new { type = "string" }, expectedPackageFingerprint = new { type = "string", minLength = 64, maxLength = 64 } },
            required = new[] { "path" }, additionalProperties = false }));
        completedInput.Model(new { action = "tool", name = "plugin.present", arguments = new { path = builtManifest, expectedPackageFingerprint = packageHash } });
        completedInput.Tool(true, JsonSerializer.Serialize(new { requestId = new string('a', 32), status = "pending", packageFingerprint = packageHash }),
            provider: "cerebral.interface", requestId: new string('a', 32));
        var completedWork = completedInput.Step.State.GetProperty("pluginWork");
        check(completedWork.GetProperty("successfulBuildGeneration").GetInt32() == 0 &&
            completedWork.GetProperty("verifiedGeneration").GetInt32() == 0 &&
            completedWork.GetProperty("presentedGeneration").GetInt32() == 0,
            "the ancestor-input invalidation fixture first records successful build, verification and presentation evidence");
        var outsideInput = Path.Combine(Path.GetDirectoryName(root)!, "Directory.Build.props");
        ReadInput(completedInput, outsideInput, new string('c', 64), "before");
        EditInput(completedInput, outsideInput, new string('c', 64), new string('d', 64), "before", "after");
        var invalidated = completedInput.Step.State.GetProperty("pluginWork");
        check(invalidated.GetProperty("sourceGeneration").GetInt32() == 1 &&
            invalidated.GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null &&
            invalidated.GetProperty("verifiedGeneration").ValueKind == JsonValueKind.Null &&
            invalidated.GetProperty("presentedGeneration").ValueKind == JsonValueKind.Null,
            "a proven ancestor input correction invalidates prior successful build/verify/present evidence and advances the existing generation path");
        var invalidationCount = Count(completedInput);
        completedInput.Model(new { action = "tool", name = "plugin.verify", arguments = new { root } });
        check(completedInput.Is("generate") && Count(completedInput) == invalidationCount,
            "verification cannot consume the stale pre-correction build after a related input mutation");
        completedInput.Model(Build());
        check(completedInput.Is("tool") && completedInput.Command.Arguments.GetProperty("name").GetString() == "process.run",
            "completed-build duplicate protection cannot block the needed fresh build after a genuine recognized related-input correction");

        var optionalSource = Ready(omitSource: true);
        check((!optionalSource.Step.State.GetProperty("pluginWork").GetProperty("build").TryGetProperty("sourceFile", out var omittedCoordinate) || omittedCoordinate.ValueKind == JsonValueKind.Null),
            "inventory recipes without a source coordinate remain valid optional-source recipes");
        optionalSource.ActualToolResult(new ToolResult(true, "{\"exitCode\":0,\"stdout\":\"Build succeeded\",\"stderr\":\"\"}")
            { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.process", State: "completed", ExitCode: 0) });
        ReadInput(optionalSource, outsideInput, new string('c', 64), "before");
        EditInput(optionalSource, outsideInput, new string('c', 64), new string('d', 64), "before", "after");
        optionalSource.Model(Build());
        check(optionalSource.Is("tool"), "an optional-source recipe allows a proven ancestor correction and fresh canonical build");
        Fail(optionalSource);
        var optionalRestored = optionalSource.Fork(); optionalRestored.RestoreState(optionalSource.Step.State.Clone());
        var optionalCount = Count(optionalRestored); optionalRestored.Model(Build());
        check(optionalRestored.Is("generate") && Count(optionalRestored) == optionalCount && Reuses(optionalRestored) == 1,
            "a failed optional-source build with exact outside-root mutation provenance survives restore and offers correction instead of rejecting its own observation");
        ReadInput(optionalRestored, outsideInput, new string('d', 64), "after");
        EditInput(optionalRestored, outsideInput, new string('d', 64), new string('e', 64), "after", "corrected");
        optionalRestored.Model(Build());
        check(optionalRestored.Is("tool"), "a further genuine correction remains executable after optional-source failure recovery");

        var compacted = Ready(); Fail(compacted);
        compacted.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        compacted.Tool(true, SourceBody(), omitEvidence: true);
        var compactedCount = Count(compacted);
        compacted.RejectContext(ContextProjectionStatus.NeedsCompaction); compacted.CompactSuccess();
        compacted.Model(Build());
        check(compacted.Is("generate") && Reuses(compacted) == 1 && Count(compacted) == compactedCount &&
            compacted.Context.Contributions.Any(x => x.Id.StartsWith("known-build-failure.", StringComparison.Ordinal)),
            "compaction preserves the pinned complete failure even if its ordinary contribution was removed");

        foreach (var kind in new[] { "partial", "truncated", "provider", "wrong-exit", "malformed" })
        {
            var incomplete = Ready(); Fail(incomplete, kind); var before = Count(incomplete); incomplete.Model(Build());
            check(incomplete.Step.Next is CompleteWorkflow { Success: false } && Count(incomplete) == before && Reuses(incomplete) == 0,
                "unconfirmed " + kind + " build evidence is never cached as success or automatically replayed");
        }
        var uncertain = Ready(); uncertain.Uncertain();
        check(uncertain.Step.Next is CompleteWorkflow { Success: false }, "an unknown/dispatched compiler outcome stops without another build request");
        var refusal = Ready(); Fail(refusal, "rejected"); var refusalCount = Count(refusal); refusal.Model(Build());
        check(refusal.Is("generate") && Count(refusal) == refusalCount && Reuses(refusal) == 0,
            "an admission refusal retains the existing rejection policy instead of becoming a cached execution failure");

        var snapshot = Ready(); Fail(snapshot);
        foreach (var field in new[] { "projection", "bodySha256", "recipeSha256", "sourceSha256" })
        {
            var corrupt = snapshot.Fork(); var node = JsonNode.Parse(snapshot.Step.State.GetRawText())!.AsObject();
            var observation = node["pluginWork"]!["failedBuildObservation"]!;
            if (field == "sourceSha256") observation["sourceRevision"]!["sha256"] = new string('f', 64);
            else observation[field] = field == "projection" ? "Substituted diagnostic" : new string('f', 64);
            corrupt.RestoreState(CerebralJson.Element(node)); corrupt.Model(Build());
            check(corrupt.Step.Next is CompleteWorkflow { Success: false } && corrupt.Summary.Contains("could not be restored", StringComparison.Ordinal),
                "tampered failed-build " + field + " refuses before any tool dispatch");
        }
        var foreign = new Fixture(requestId: "other-failed-build-run"); foreign.RestoreState(snapshot.Step.State.Clone()); foreign.Model(Build());
        check(foreign.Step.Next is CompleteWorkflow { Success: false } && foreign.Summary.Contains("another run", StringComparison.Ordinal),
            "failed-build policy and provenance cannot be imported into a different run");
        var fresh = Ready("independent-fresh-build-run");
        check(fresh.Is("tool") && fresh.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32() == 0,
            "an independent fresh run owns its own compiler observation and starts without borrowed failure state");

        var legacy = snapshot.Fork(); var old = JsonNode.Parse(snapshot.Step.State.GetRawText())!.AsObject();
        RemoveFailedBuildPolicy(old); legacy.RestoreState(CerebralJson.Element(old)); legacy.Model(Build());
        check(legacy.Is("tool"), "a saved pre-107 run without the new marker keeps its original canonical-build policy");
    }

    private static void RemoveFailedBuildPolicy(JsonObject state)
    {
        state.Remove("failedBuildRecovery");
        foreach (var receipt in state["toolReceipts"]!.AsArray()) receipt!.AsObject().Remove("canonicalProof");
        if (state["pluginWork"] is JsonObject work)
        {
            work.Remove("buildSourceRevision"); work.Remove("failedBuildObservation");
        }
    }
}
