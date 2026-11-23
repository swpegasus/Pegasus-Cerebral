using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cerebral.Worker.Tests;

public static class WorkflowPortableSettingsApprovalInputTests
{
    private const string Valid = """
        {"pluginId":"example.plugin","packageVersion":"1.0.0","loadedGeneration":"generation-a","packageFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","schemaFingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","reviewedFields":["style"],"expectedRevision":0,"requestId":"request-a"}
        """;

    public static void Run()
    {
        var valid = WorkflowPortableSettingsApprovalInput.Parse(Parse(Valid));
        Check(valid.PluginId == "example.plugin" && valid.ReviewedFields.SequenceEqual(["style"]) &&
              valid.ExpectedRevision == 0 && valid.RequestId == "request-a", "exact approval input admitted");
        Reject(Valid.Replace("\"packageVersion\"", "\"pluginId\":\"other\",\"packageVersion\""),
            "duplicate key");
        Reject(Valid.Replace("\"packageVersion\"", "\"PluginId\":\"other\",\"packageVersion\""),
            "case-alias duplicate");
        Reject(Valid.Replace("\"packageVersion\"", "\"\\u0070luginId\":\"other\",\"packageVersion\""),
            "escaped duplicate");
        Reject(Valid.Replace("\"requestId\"", "\"unexpected\":true,\"requestId\""), "unknown ninth key");
        var baseObject = JsonNode.Parse(Valid)!.AsObject();
        foreach (var field in new[] { "pluginId", "packageVersion", "loadedGeneration", "packageFingerprint",
                     "schemaFingerprint", "reviewedFields", "expectedRevision", "requestId" })
        {
            var missing = (JsonObject)baseObject.DeepClone();
            missing.Remove(field);
            Reject(missing.ToJsonString(), "missing " + field);
        }
        Reject(Valid.Replace("[\"style\"]", "null"), "null reviewed fields");
        Reject(Valid.Replace("[\"style\"]", "{}"), "object reviewed fields");
        Reject(Valid.Replace("[\"style\"]", "[42]"), "non-string reviewed field");
        Reject(Valid.Replace("\"expectedRevision\":0", "\"expectedRevision\":1.5"), "fractional revision");
        Reject(Valid.Replace("\"expectedRevision\":0", "\"expectedRevision\":-1"), "negative revision");
        Reject(Valid.Replace("\"expectedRevision\":0", "\"expectedRevision\":9223372036854775808"),
            "overflow revision");
        Reject(Valid.Replace("\"pluginId\"", "\"PluginId\""), "PascalCase-only key");
        Reject(Valid.Replace(",\"packageVersion\"", "," + new string(' ', 16_385) + "\"packageVersion\""),
            "oversized whitespace body");
        Reject(Valid.Replace("[\"style\"]", "[[[[\"style\"]]]]"), "excessive depth");
        Console.WriteLine("Workflow portable-settings strict approval input checks passed (deterministic parser; no state access).");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Reject(string json, string label)
    {
        try { WorkflowPortableSettingsApprovalInput.Parse(Parse(json)); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return; }
        throw new Exception("FAIL: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
    }
}
