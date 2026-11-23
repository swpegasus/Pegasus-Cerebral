using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    static partial void CurrentBuildPolicy(Action<bool, string> check)
    {
        foreach (var kind in new[] { "run", "test" })
        {
            string[] NoBuild(string configuration = "Release") => [kind, "--project", "Hello.csproj", "--configuration", configuration, "--no-build"];
            var empty = new Fixture(requiredChecks: [kind]); empty.Read(); empty.Process(NoBuild());
            check(empty.IsModel && empty.CheckCount == 0 && empty.Step.State.GetProperty("repairs").GetInt32() == 1,
                "explicit no-build requires a current build even for a run-only/test-only contract");

            var matching = new Fixture(requiredChecks: [kind]); matching.Read(); matching.Dotnet("build"); matching.Tool(true, "Built", 0);
            matching.Process(NoBuild());
            check(matching.IsTool && matching.PendingTool == "process.run" && matching.Argv.Contains("--no-build"),
                "a current matching build allows the explicit no-build check");
            matching.Tool(true, kind == "test" ? "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms" : "Ran", 0);
            check(matching.CheckCount == 2, "the matching explicit check earns a distinct receipt only after confirmed success");

            var different = new Fixture(requiredChecks: [kind]); different.Read(); different.Dotnet("build", "Debug"); different.Tool(true, "Debug built", 0);
            different.Process(NoBuild());
            check(different.IsModel && different.CheckCount == 1 && different.Step.State.GetProperty("repairs").GetInt32() == 1,
                "another configuration cannot authorize no-build output reuse");

            foreach (var success in new[] { true, false })
            {
                var mutation = new Fixture(requiredChecks: [kind]); mutation.Read(); mutation.Dotnet("build"); mutation.Tool(true, "Built", 0);
                mutation.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(mutation.Root, "Program.cs"), content = "changed", collision = "replace" } });
                mutation.Tool(success, success ? "Changed" : "Write failed");
                mutation.Process(NoBuild());
                check(mutation.IsModel && mutation.CheckCount == 0 && mutation.Step.State.GetProperty("repairs").GetInt32() == 1,
                    "successful or failed mutation dispatch invalidates no-build permission before another check");
            }

            var defensive = new Fixture(requiredChecks: [kind]); defensive.Read(); defensive.Dotnet("build"); defensive.Tool(true, "Built", 0);
            defensive.Process(NoBuild()); defensive.DropCurrentBuildForReceiptPolicy();
            defensive.Tool(true, kind == "test" ? "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms" : "Ran", 0);
            check(defensive.IsModel && defensive.CheckCount == 0,
                "receipt processing independently withholds stale no-build credit when current build proof is absent");

            var implicitBuild = new Fixture(requiredChecks: [kind]); implicitBuild.Read(); implicitBuild.Dotnet(kind);
            check(implicitBuild.IsTool && !implicitBuild.Argv.Contains("--no-build"),
                "a run-only/test-only command that may build does not invent a separate build prerequisite");
            implicitBuild.Tool(true, kind == "test" ? "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms" : "Ran", 0);
            check(implicitBuild.CheckCount == 1, "a confirmed build-capable check retains its accepted contract");
        }
    }
    private sealed partial class Fixture
    {
        public void DropCurrentBuildForReceiptPolicy()
        {
            var state = JsonNode.Parse(Step.State.GetRawText())!.AsObject();
            state["checks"] = new JsonArray();
            Step = new(CerebralJson.Element(state), Step.Next);
        }
    }
}
