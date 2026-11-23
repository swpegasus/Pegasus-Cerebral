using System.Text;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    /// <summary>
    /// Real native dispatcher reads/build in an isolated directory, real Harness.Advance transitions.
    /// Generation/compaction effects are deterministic fixtures, not model or live Worker evidence.
    /// </summary>
    public static async Task<int> RunNativeReadChecksAsync(string current, bool reproduce154 = false)
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Native read boundary: " + message);
            checks++;
        }
        var scratch = Path.GetFullPath(Path.Combine(current, "artifacts", "NativeReadEvidenceFixtures"));
        var owner = Path.GetFullPath(Path.Combine(current, "artifacts")) + Path.DirectorySeparatorChar;
        if (!scratch.StartsWith(owner, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new IOException("The exact task fixture directory must be absent.");
        Directory.CreateDirectory(scratch);
        try
        {
            var sourcePath = Path.Combine(scratch, "Program.cs");
            var source = string.Join('\n', Enumerable.Range(1, 20).Select(i => "// native read line " + i)) +
                "\npublic static class NativeReadFixture { public static int Value => ; }\n";
            await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false, true));
            Directory.CreateDirectory(Path.Combine(scratch, "packages"));
            await File.WriteAllTextAsync(Path.Combine(scratch, "Hello.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                    <TargetFramework>net10.0</TargetFramework>
                    <RestoreSources>$(MSBuildThisFileDirectory)packages</RestoreSources>
                    <NuGetAudit>false</NuGetAudit>
                    <UseSharedCompilation>false</UseSharedCompilation>
                  </PropertyGroup>
                </Project>
                """, new UTF8Encoding(false));
            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            var settings = new HostSettings { McpServers = [], Executables = [new() { Id = "dotnet", Path = dotnet }] };
            var profile = new AgentProfile { AllowedRoots = [scratch], AllowedTools = ["file.read", "process.run"], Capabilities = ["filesystem.read", "process.execute"] };
            await using var dispatcher = new ToolDispatcher(() => settings, stateDirectory: Path.Combine(scratch, "dispatcher-state"));
            var fixture = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"], rootPath: scratch);
            async Task<ToolResult> Dispatch(int start)
            {
                // >512 deliberately uses the actual native branch, not PAO, without changing global settings.
                fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = sourcePath, startLine = start, lineCount = 1000 } });
                Check(fixture.IsTool, "real source request admitted by actual harness");
                var arguments = fixture.Command.Arguments.GetProperty("arguments");
                var result = await dispatcher.ExecuteAsync(new("file.read", arguments),
                    new(profile, "native-read-fixture", "native-read-fixture", scratch) { EnforceProfileScope = true }, CancellationToken.None);
                Check(result.Success && result.Evidence is null && result.EffectiveOutcome == ToolExecutionOutcome.Succeeded,
                    "production native ToolDispatcher read returns successful null Evidence");
                fixture.HostEquivalentTool(result);
                Check(fixture.IsModel && fixture.Step.State.GetProperty("readReceipts").GetArrayLength() > 0,
                    "actual host-shaped null-evidence envelope is accepted as a read");
                return result;
            }
            var native = await Dispatch(1);
            var working = fixture.Step.State.GetProperty("workingSource");
            if (reproduce154)
            {
                Check(working.ValueKind == System.Text.Json.JsonValueKind.Null,
                    "consumed154 reproduces missing working source despite an actual successful native read");
                Console.WriteLine("REPRODUCED154 native successful null-evidence read accepted but WorkingSource=null; checks=" + checks);
                return checks;
            }
            Check(working.ValueKind == System.Text.Json.JsonValueKind.Object &&
                working.GetProperty("sha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))).ToLowerInvariant(),
                "native null-evidence read seeds actual whole-preimage identity and working body");
            await Dispatch(20);
            var slot = fixture.Step.State.GetProperty("workingSource").Clone();
            var id = slot.GetProperty("contributionId").GetString()!;
            var body = fixture.Context.Contributions.Single(item => item.Id == id);
            Check(slot.GetProperty("startLine").GetInt32() == 20 && slot.GetProperty("visibleLineCount").GetInt32() > 0,
                "second native read replaces the current slot with real displayed coordinates");

            fixture.Model(new { action = "tool", name = "process.run", arguments = new
            {
                executableId = "dotnet", workingDirectory = scratch,
                arguments = new[] { "build", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                timeoutSeconds = 60
            } });
            Check(fixture.IsTool, "real build-before-edit request is admitted");
            var build = await dispatcher.ExecuteAsync(new("process.run", fixture.Command.Arguments.GetProperty("arguments")),
                new(profile, "native-build-fixture", "native-build-fixture", scratch) { EnforceProfileScope = true }, CancellationToken.None);
            Check(!build.Success && build.Evidence is { ExitCode: 1 } && build.Content.Contains("error CS", StringComparison.Ordinal),
                "isolated native dotnet build actually rejects intentionally invalid fixture source");
            fixture.HostEquivalentTool(build);
            Check(fixture.IsModel && fixture.CheckCount == 0 &&
                System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(fixture.Context.Contributions.Single(item => item.Id == id)), CerebralJson.Element(body)) &&
                fixture.Context.Contributions.Single(item => item.Id == id).Requirement == ContextRequirement.Required,
                "actual failed compiler result preserves exact native-read working projection before any edit");
            fixture.ContextRejected(ContextProjectionStatus.NeedsCompaction);
            Check(fixture.IsCompact, "deterministic context rejection requests compaction of older read evidence");
            fixture.CompactSuccess();
            Check(fixture.IsModel && fixture.Context.Checkpoint is not null &&
                System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(fixture.Context.Contributions.Single(item => item.Id == id)), CerebralJson.Element(body)) &&
                fixture.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString() == id,
                "adopted fixture compaction preserves actual native-read body and identity");
            Check(fixture.Step.State.GetProperty("editCount").GetInt32() == 0 && fixture.CheckCount == 0 &&
                fixture.Step.State.GetProperty("readReceipts").GetArrayLength() == 2,
                "native continuity does not create edit, successful-build or extra read credit");

            foreach (var outcome in new[] { ToolExecutionOutcome.Failed, ToolExecutionOutcome.Rejected, ToolExecutionOutcome.Unknown, ToolExecutionOutcome.Cancelled })
            {
                var refused = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"], rootPath: scratch);
                refused.Model(new { action = "tool", name = "file.read", arguments = new { path = sourcePath, startLine = 1, lineCount = 1000 } });
                refused.HostEquivalentTool(native with { Evidence = new(outcome, "negative-fixture") });
                Check(refused.Step.State.GetProperty("workingSource").ValueKind == System.Text.Json.JsonValueKind.Null &&
                    refused.Step.State.GetProperty("readReceipts").GetArrayLength() == 0,
                    "explicit non-success evidence remains unpinned and uncredited: " + outcome);
            }
            var blankProvider = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"], rootPath: scratch);
            blankProvider.Model(new { action = "tool", name = "file.read", arguments = new { path = sourcePath, startLine = 1, lineCount = 1000 } });
            blankProvider.HostEquivalentTool(native with { Evidence = new(ToolExecutionOutcome.Succeeded, "") });
            Check(blankProvider.Step.State.GetProperty("workingSource").ValueKind == System.Text.Json.JsonValueKind.Null,
                "explicit blank provider metadata retains the previous conservative rejection");

            var missing = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"], rootPath: scratch);
            missing.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(scratch, "missing.cs"), startLine = 1, lineCount = 1000 } });
            var absent = await dispatcher.ExecuteAsync(new("file.read", missing.Command.Arguments.GetProperty("arguments")),
                new(profile, "native-refusal-fixture", "native-refusal-fixture", scratch) { EnforceProfileScope = true }, CancellationToken.None);
            Check(!absent.Success && absent.EffectiveOutcome != ToolExecutionOutcome.Succeeded, "real native read refusal is not success");
            missing.HostEquivalentTool(absent);
            Check(missing.Step.State.GetProperty("workingSource").ValueKind == System.Text.Json.JsonValueKind.Null &&
                missing.Step.State.GetProperty("readReceipts").GetArrayLength() == 0, "real native refusal cannot establish working visibility");

            var altered = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"], rootPath: scratch);
            altered.Model(new { action = "tool", name = "file.read", arguments = new { path = sourcePath, startLine = 1, lineCount = 1000 } });
            var wrongPath = JsonNode.Parse(native.Content)!.AsObject(); wrongPath["path"] = Path.Combine(scratch, "foreign.cs");
            altered.HostEquivalentTool(native with { Content = wrongPath.ToJsonString(CerebralJson.Options) });
            Check(altered.Step.State.GetProperty("workingSource").ValueKind == System.Text.Json.JsonValueKind.Null,
                "null-evidence compatibility does not relax source identity/presentation matching");
            Console.WriteLine("Native dispatcher/read + actual host-equivalent envelope + Harness checks passed: " + checks +
                "; fixtureCompilerExit=1(expected); compaction=deterministic fixture; modelCalls=0");
            return checks;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private sealed partial class Fixture
    {
        // Same outcome/envelope contract as WorkerWorkflowEffects470-494. No live Worker or model.
        public void HostEquivalentTool(ToolResult result)
        {
            var name = Command.Arguments.GetProperty("name").GetString()!;
            var state = result.EffectiveOutcome switch
            {
                ToolExecutionOutcome.Succeeded => WorkflowEffectState.Succeeded,
                ToolExecutionOutcome.Failed or ToolExecutionOutcome.Rejected => WorkflowEffectState.Failed,
                ToolExecutionOutcome.Cancelled => WorkflowEffectState.Cancelled,
                _ => WorkflowEffectState.Unknown
            };
            var wire = CerebralJson.Element(new { name, content = result.Content, truncated = result.Truncated,
                evidence = result.Evidence, followUpRequired = result.Generation is not null,
                requiredSuccessfulTools = result.RequiredSuccessfulTools, generation = result.Generation });
            Result(state, wire, state == WorkflowEffectState.Succeeded ? null : "The tool returned " + result.EffectiveOutcome.ToString().ToLowerInvariant() + ".");
        }
    }
}
