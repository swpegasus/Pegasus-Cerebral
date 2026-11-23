using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class WorkflowPortableSettingsTests
{
    public static void Run()
    {
        var schema = ServicePluginPortableSettingsPolicy.Freeze("example.plugin", "1.0.0",
            new ServicePluginPortableSettings
            {
                Version = 1,
                Fields =
                [
                    new() { Name = "style", Type = "string", Required = false, MaximumUtf8Bytes = 64 },
                    new() { Name = "secret", Type = "string", Required = false, MaximumUtf8Bytes = 64 }
                ]
            });
        var profile = new AgentProfile { Id = "portable-check", WorkerId = "local" };
        var absent = WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"]);
        Check(!absent.HadProfileEntry && absent.ReviewedValues.EnumerateObject().Count() == 0,
            "an absent profile entry freezes an explicit absence marker");
        profile.PluginSettings["example.plugin"] = Parse("{}");
        var empty = WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"]);
        Check(empty.HadProfileEntry && !WorkflowPortableSettingsProjection.Same(absent, empty),
            "present empty settings differ from absence");
        profile.PluginSettings["example.plugin"] = Parse("""{"style":"brief","secret":"first"}""");
        var selected = WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"]);
        Check(selected.ReviewedValues.TryGetProperty("style", out var style) && style.GetString() == "brief" &&
              !selected.ReviewedValues.TryGetProperty("secret", out _),
            "projection copies only the reviewed declared field");
        profile.PluginSettings["example.plugin"] = Parse("""{"style":"brief","secret":"rotated","opaque":"new"}""");
        var opaqueChanged = WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"]);
        Check(WorkflowPortableSettingsProjection.Same(selected, opaqueChanged),
            "unreviewed and opaque rotations do not change the accepted projection");
        profile.PluginSettings["example.plugin"] = Parse("""{"style":"other","secret":"rotated"}""");
        Check(!WorkflowPortableSettingsProjection.Same(selected,
            WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"])),
            "reviewed value drift is observable before dispatch");
        profile.PluginSettings["example.plugin"] = Parse("""{"style":"brief","style":"other"}""");
        Reject(() => WorkflowPortableSettingsProjection.Project(profile, "example.plugin", schema, ["style"]),
            "duplicate raw fields refuse before projection");

        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
            new AgentProfile { Id = "portable-check", WorkerId = "local" }, "", "", null,
            [], [], [], [], false, "fixture-routing", [], []);
        var current = WorkerExecutionSnapshot.Bounded(snapshot);
        Check(WorkerExecutionSnapshot.Read(current).ApprovedPluginSettings?.Count == 0,
            "edition six writes and reads an explicit empty approved collection");
        var legacy = JsonNode.Parse(current.GetRawText())!.AsObject();
        legacy["resolvedSettingsVersion"] = 5;
        legacy.Remove("approvedPluginSettings");
        Check(WorkerExecutionSnapshot.Read(Parse(legacy.ToJsonString())).SourceSettingsVersion == 5,
            "edition five retains its original empty-settings reading");
        legacy["approvedPluginSettings"] = new JsonArray();
        Reject(() => WorkerExecutionSnapshot.Read(Parse(legacy.ToJsonString())),
            "edition five refuses the new field even when empty");
        var missing = JsonNode.Parse(current.GetRawText())!.AsObject();
        missing.Remove("approvedPluginSettings");
        Reject(() => WorkerExecutionSnapshot.Read(Parse(missing.ToJsonString())),
            "edition six refuses a missing approved collection");
        var exactJson = "{\"field\":\"" + new string('a', 32_768 - 12) + "\"}";
        var overflowJson = "{\"field\":\"" + new string('a', 32_769 - 12) + "\"}";
        var compact = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        Check(JsonSerializer.SerializeToUtf8Bytes(Parse(exactJson), compact).Length == 32_768 &&
              JsonSerializer.SerializeToUtf8Bytes(Parse(overflowJson), compact).Length == 32_769,
            "approved-value byte fixtures meet exact serialized boundaries");
        var entry = new WorkflowApprovedSettingsEntry("example.plugin", "1.0.0", "loaded-fixture",
            new string('a', 64), new string('b', 64), 1, true, Parse(exactJson));
        Check(WorkflowPortableSettingsProjection.ValidateCollection([entry]).Count == 1,
            "exactly 32 KiB of reviewed values fit the closed collection");
        Reject(() => WorkflowPortableSettingsProjection.ValidateCollection(
            [entry with { ReviewedValues = Parse(overflowJson) }]),
            "32 KiB plus one byte of reviewed values refuses");
        Console.WriteLine("Workflow portable-settings projection and snapshot checks passed (deterministic, no plugin or runtime call).");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
    }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or JsonException)
        { return; }
        throw new Exception("FAIL: " + label);
    }
}
