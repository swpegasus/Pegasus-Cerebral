using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

public static class WorkflowGenerationMetricChecks
{
    public static int Verify()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        JsonObject Lane(string id, string state = "active", string phase = "output", double? input = null, double? output = 50) => new()
        {
            ["schema"] = "cerebral.generation-metrics/1", ["laneId"] = id, ["instanceId"] = "model-instance",
            ["modelId"] = "fixture", ["adapterId"] = "fixture.local", ["binding"] = null,
            ["state"] = state, ["phase"] = phase, ["inputTokens"] = 100, ["outputTokens"] = 50,
            ["inputTokensPerSecond"] = input, ["outputTokensPerSecond"] = output,
            ["elapsedMilliseconds"] = 1000, ["at"] = JsonValue.Create(now)
        };
        JsonObject Envelope(JsonArray active, JsonArray? recent = null) => new()
        {
            ["schema"] = "cerebral.generation-metrics/1", ["at"] = JsonValue.Create(now),
            ["active"] = active, ["recent"] = recent ?? new JsonArray(), ["truncated"] = false
        };
        GenerationThroughputPresentation Project(JsonObject value) => GenerationThroughput.Project(CerebralJson.Element(value));
        var two = Project(Envelope(new JsonArray(Lane("lane-a", output: 50), Lane("lane-b", output: 75))));
        Check(two.Input == "0" && two.Output == "125" && two.ActiveLanes == 2 && two.MeasuredLanes == 2,
            "Concurrent measured output lanes sum their rates, while inactive prefill is zero.");
        var overlap = Project(Envelope(new JsonArray(Lane("lane-a", output: 50)),
            new JsonArray(Lane("lane-a", state: "Stop", output: 50), Lane("finished", state: "Stop", output: 900))));
        Check(overlap.Output == "50" && overlap.ActiveLanes == 1,
            "Recent/current overlap and completed high-rate samples never double count the current total.");
        var mixed = Project(Envelope(new JsonArray(Lane("prefill", phase: "prefill", input: 1200, output: null),
            Lane("output", output: 75), Lane("remote", phase: "unknown", output: null))));
        Check(mixed.Input == "1200+" && mixed.Output == "75+" && mixed.MeasuredLanes == 2 &&
            mixed.Details.Contains("unavailable lanes: 1", StringComparison.Ordinal),
            "Input/output phases stay separate; unmeasured remote lanes mark a confirmed partial total.");
        var unknown = Project(Envelope(new JsonArray(Lane("remote", phase: "unknown", output: null))));
        Check(unknown.Input == "—" && unknown.Output == "—", "Unavailable provider counters do not invent zero rates.");
        var idle = Project(Envelope(new JsonArray(), new JsonArray(Lane("last", state: "Stop"))));
        Check(idle.Input == "0" && idle.Output == "0", "Idle throughput is a measured no-active-lanes state, independent of recent usage.");
        var stale = Lane("stale"); stale["at"] = JsonValue.Create(now - TimeSpan.FromSeconds(5));
        Check(Project(Envelope(new JsonArray(stale))).Output == "—", "Old live samples cannot masquerade as current output.");
        foreach (var lane in new[] { Lane("duplicate"), Lane("negative", output: -1), Lane("wrong-phase", phase: "prefill"),
            Lane("private", phase: "analysis"), Lane("bad-state", state: "done") })
        {
            var malformed = Envelope(new JsonArray(lane));
            if (lane["laneId"]!.GetValue<string>() == "duplicate") malformed["active"]!.AsArray().Add(lane.DeepClone());
            Check(Project(malformed).Output == "—", "Duplicate, invalid, private, terminal-in-active and mismatched phase samples fail closed.");
        }
        var privateField = Lane("private-field"); privateField["reasoning"] = "not allowed";
        Check(Project(Envelope(new JsonArray(privateField))).Output == "—", "Unknown/private numeric envelope fields are not rendered.");
        var textToken = Lane("no-estimate", phase: "unknown", output: null); textToken["outputTokens"] = null;
        Check(Project(Envelope(new JsonArray(textToken))).Output == "—", "Absent exact tokens never use text characters as a proxy.");
        var duplicateJson = """{"schema":"cerebral.generation-metrics/1","at":"2026-10-11T20:00:00Z","active":[],"active":[],"recent":[],"truncated":false}""";
        using var duplicate = JsonDocument.Parse(duplicateJson);
        Check(GenerationThroughput.Project(duplicate.RootElement).Output == "—", "Duplicate envelope fields cannot change aggregation semantics.");
        CerebralEvent Receipt(string state, string finish) => new("fixture-limit", "fixture-chat", "fixture-run", 1,
            CerebralEventTypes.ToolResult, new(EventActorKind.Agent, "lead"),
            [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                { state, data = new { generation = new { finish, text = "" } } }))],
            new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Agent), now);
        Check(WorkflowWorkspace.GenerationLimitState(Receipt("Succeeded", "Length")) == "Response limit reached" &&
            WorkflowWorkspace.GenerationLimitState(Receipt("succeeded", "length")) == "Response limit reached",
            "Confirmed provider length is an incomplete generation attempt, separate from the workflow's final outcome.");
        Check(WorkflowWorkspace.GenerationLimitState(Receipt("Failed", "Length")) is null &&
            WorkflowWorkspace.GenerationLimitState(Receipt("Unknown", "Length")) is null &&
            WorkflowWorkspace.GenerationLimitState(Receipt("Succeeded", "Stop")) is null,
            "Failed/unknown receipts or normal Stop cannot be mislabeled as a completed response-limit attempt.");
        return checks;
    }
}
