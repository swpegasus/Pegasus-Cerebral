using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    public static async Task<int> RunNativeReadDriftChecksAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Native read drift: " + message);
        }
        var parent = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\build\Programming\TaskStages\artifacts\ReadReuseDriftFixtures");
        var scratch = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        if (!scratch.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new InvalidOperationException("Unexpected drift fixture directory.");
        Directory.CreateDirectory(scratch);
        try
        {
            var path = Path.Combine(scratch, "Program.cs");
            const string original = "class DriftFixture { public const int Value = 1; }\n";
            const string external = "class DriftFixture { public const int Value = 2; }\n";
            await File.WriteAllTextAsync(path, original, new UTF8Encoding(false, true));
            var settings = new HostSettings { McpServers = [], Executables = [] };
            var profile = new AgentProfile { AllowedRoots = [scratch], AllowedTools = ["file.read", "file.edit"],
                Capabilities = ["filesystem.read", "filesystem.write"] };
            await using var dispatcher = new ToolDispatcher(() => settings, stateDirectory: Path.Combine(scratch, "dispatcher-state"));
            var fixture = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "file.edit", "process.run"], rootPath: scratch);
            var context = new ToolExecutionContext(profile, "private-drift-fixture", "private-drift-fixture", scratch) { EnforceProfileScope = true };
            var dispatches = 0;
            async Task<ToolResult> Dispatch(string name)
            {
                Check(fixture.IsTool && fixture.Command.Arguments.GetProperty("name").GetString() == name, "expected exact native request " + name);
                dispatches++;
                var result = await dispatcher.ExecuteAsync(new(name, fixture.Command.Arguments.GetProperty("arguments")), context, CancellationToken.None);
                fixture.HostEquivalentTool(result);
                return result;
            }
            fixture.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1000 } });
            var initial = await Dispatch("file.read");
            Check(initial.Success && fixture.IsModel && fixture.Step.State.GetProperty("readReceipts").GetArrayLength() == 1, "real native read establishes one source observation");
            var before = fixture.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString()!;
            Check(before == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant(), "cached hash is the actual original file hash");

            await File.WriteAllTextAsync(path, external, new UTF8Encoding(false, true));
            fixture.Model(new { action = "tool", name = "file.edit", arguments = new { path, oldText = original, newText = "MUST NOT BE WRITTEN", expectedSha256 = before } });
            Check(fixture.IsTool && fixture.Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null &&
                fixture.Step.State.GetProperty("readProjectionReferences").GetArrayLength() == 0, "target mappings retire before a dispatch using the previously valid stale SHA");
            var conflict = await Dispatch("file.edit");
            Check(!conflict.Success && conflict.Content.Contains("Preimage hash changed", StringComparison.Ordinal) &&
                fixture.IsModel && await File.ReadAllTextAsync(path) == external,
                "actual tool-level preimage conflict refuses the write and preserves external bytes");
            Check(fixture.Step.State.GetProperty("editCount").GetInt32() == 0 &&
                fixture.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0 &&
                fixture.Step.State.GetProperty("readReceipts").GetArrayLength() == 1 && fixture.CheckCount == 0 &&
                fixture.Step.State.GetProperty("workingSource").ValueKind == JsonValueKind.Null,
                "conflict promotes no cached source, read, mutation or verification evidence");

            fixture.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1000 } });
            Check(fixture.IsTool, "retired body permits the same whole-file request as a real refresh");
            var refreshed = await Dispatch("file.read");
            var after = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
            Check(refreshed.Success && after != before &&
                fixture.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString() == after &&
                fixture.Step.State.GetProperty("readReceipts").GetArrayLength() == 2 &&
                fixture.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0 && fixture.CheckCount == 0,
                "only actual new native bytes establish the refreshed source mapping");
            Check(dispatches == 3 && await File.ReadAllTextAsync(path) == external, "exactly two reads and one refused edit, with no mutation replay");
            Console.WriteLine(JsonSerializer.Serialize(new { evidenceTier = "local native filesystem integration with deterministic model turns",
                reads = 2, refusedEdits = 1, externalDriftObserved = true, refreshedByActualRead = true,
                processLaunches = 0, liveModelCalls = 0, appCalls = 0 }));
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(scratch).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Drift fixture cleanup escaped its exact parent.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
}
