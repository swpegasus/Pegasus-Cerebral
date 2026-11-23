using System.Text.Json;
using Cerebral.Core;
namespace Cerebral.Worker.Tests;
public static partial class ProgrammingHarnessTests
{
    private static void StructurePolicy(Action<bool, string> check)
    {
        const string name = "program.source.structure";
        Fixture Selected() => new(requiredChecks: ["build"], selectedTools: ["file.read", "file.write", name, "process.run"]);
        object Args(Fixture f) => new { path = Path.Combine(f.Root, "Program.cs"), expectedSha256 = new string('a', 64), focusLine = 3 };
        var defaults = new Fixture();
        check(!defaults.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(x => x.GetString() == name) &&
            !defaults.Messages.Contains(name, StringComparison.Ordinal), "structure tool remains opt-in without default guidance");
        defaults.Model(new { action = "tool", name, arguments = Args(defaults) });
        check(defaults.IsModel, "unselected structure action is rejected before dispatch");
        var selected = Selected();
        check(selected.Messages.Contains("structure metadata grants no", StringComparison.Ordinal), "selected structure guidance states its evidence limits");
        selected.Model(new { action = "tool", name, arguments = Args(selected) });
        check(selected.IsTool, "explicitly selected closed structure action is admitted");
        selected.Tool(true, "{\"schema\":\"program.source.structure/1\",\"sha256\":\"" + new string('a', 64) + "\",\"nodes\":[]}");
        check(selected.IsModel && selected.Step.State.GetProperty("editCount").GetInt32() == 0 &&
            selected.Step.State.GetProperty("readReceipts").GetArrayLength() == 0 && selected.CheckCount == 0,
            "outline cannot grant source-read, mutation or check credit");
        check(selected.LastContributionText.Contains("program.source.structure/1", StringComparison.Ordinal) &&
            selected.Context.Contributions.Last().Complete, "bounded plugin payload reaches the model through unchanged Core projection");
        selected.Model(new { action = "done", summary = "Done from outline" });
        check(selected.IsTool && selected.PendingTool == "file.read" &&
            selected.Step.State.GetProperty("readReceipts").GetArrayLength() == 0 && selected.CheckCount == 0,
            "outline earns no source/check credit; completion requires a real accepted-project read before checks");
        foreach (var args in new object[] {
            new { path = Path.Combine(selected.Root, "Program.cs"), expectedSha256 = "bad" },
            new { path = Path.Combine(selected.Root, "Program.cs"), expectedSha256 = new string('a',64), focusLine = 0 },
            new { path = Path.Combine(selected.Root, "Program.cs"), expectedSha256 = new string('a',64), extra = true },
            new { path = Path.Combine(Path.GetDirectoryName(selected.Root)!, "outside.cs"), expectedSha256 = new string('a',64) } })
        {
            var invalid = Selected();
            invalid.Model(new { action = "tool", name, arguments = args });
            check(invalid.IsModel && invalid.Step.State.GetProperty("editCount").GetInt32() == 0,
                "invalid structure arguments stay inside correction without dispatch or credits");
        }
        var retained = Selected();
        retained.Read(); retained.Dotnet("build"); retained.Tool(true, "Build succeeded", 0);
        retained.Model(new { action = "tool", name, arguments = Args(retained) });
        retained.Tool(true, "{\"schema\":\"program.source.structure/1\",\"nodes\":[]}");
        check(retained.CheckCount == 1 && retained.Step.State.GetProperty("readReceipts").GetArrayLength() == 1 &&
            retained.Step.State.GetProperty("editCount").GetInt32() == 0, "read-only outline preserves prior check and source-read counts");
        retained.Write();
        check(!retained.Messages.Contains("program.source.structure/1", StringComparison.Ordinal) && retained.CheckCount == 0,
            "source mutation removes stale outline and passing checks");
    }
}
