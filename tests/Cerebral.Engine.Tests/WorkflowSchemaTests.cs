using System.Text.Json;
using Cerebral.Engine.Workflows;

namespace Cerebral.Engine.Tests;

/// <summary>Pure schema-boundary tests, without SQLite, providers, model inference or tool dispatch.</summary>
public static class WorkflowSchemaTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            count++; Console.WriteLine("PASS: " + description);
        }
        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            return document.RootElement.Clone();
        }
        static string? Rejection(Action action)
        {
            try { action(); return null; }
            catch (Exception error) when (error is ArgumentException or JsonException) { return error.Message; }
        }
        static bool Reject(Action action) => Rejection(action) is not null;

        var union = Json("""
            {"oneOf":[
              {"type":"object","properties":{"generation":{"type":"object"},"prepared":{"type":"object"}},"required":["generation","prepared"],"additionalProperties":false},
              {"type":"object","properties":{"context":{"type":"object","properties":{"status":{"type":"string","enum":["needsCompaction","requiredOverflow"]}},"required":["status"],"additionalProperties":false}},"required":["context"],"additionalProperties":false}
            ]}
            """);
        WorkflowSchema.ValidateSchema(union);
        WorkflowSchema.Validate(Json("""{"generation":{},"prepared":{}}"""), union);
        WorkflowSchema.Validate(Json("""{"context":{"status":"needsCompaction"}}"""), union);
        Check(true, "oneOf accepts separate strict success and context-rejection result shapes");
        Check(Reject(() => WorkflowSchema.Validate(Json("{}"), union)) &&
            Reject(() => WorkflowSchema.Validate(Json("""{"generation":{}}"""), union)) &&
            Reject(() => WorkflowSchema.Validate(Json("""{"context":{}}"""), union)),
            "each alternative retains its own required fields; empty or incomplete results cannot pass");
        Check(Reject(() => WorkflowSchema.Validate(Json("""{"generation":{},"prepared":{},"context":{"status":"needsCompaction"}}"""), union)) &&
            Reject(() => WorkflowSchema.Validate(Json("""{"context":{"status":"ready"}}"""), union)) &&
            Reject(() => WorkflowSchema.Validate(Json("""{"context":{"status":"needsCompaction","extra":true}}"""), union)),
            "mixed result shapes, invalid statuses and nested extra fields remain rejected");

        var ambiguous = Json("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""");
        WorkflowSchema.Validate(Json("1.5"), ambiguous);
        Check(Reject(() => WorkflowSchema.Validate(Json("1"), ambiguous)) &&
            Reject(() => WorkflowSchema.Validate(Json("false"), ambiguous)),
            "oneOf requires exactly one matching alternative, rejecting both ambiguity and no match");
        Check(Reject(() => WorkflowSchema.Validate(Json("null"), Json("""{"oneOf":[{},{}]}"""))),
            "duplicate permissive alternatives do not weaken exclusive matching");

        var siblings = Json("""{"type":"number","minimum":5,"oneOf":[{"maximum":10},{"minimum":20}]}""");
        WorkflowSchema.Validate(Json("7"), siblings);
        WorkflowSchema.Validate(Json("25"), siblings);
        Check(Reject(() => WorkflowSchema.Validate(Json("2"), siblings)) &&
            Reject(() => WorkflowSchema.Validate(Json("15"), siblings)) &&
            Reject(() => WorkflowSchema.Validate(Json("\"7\""), siblings)),
            "ordinary sibling constraints remain conjunctive with oneOf");

        var nested = Json("""
            {"type":"array","minItems":1,"maxItems":2,"items":{"oneOf":[
              {"type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":3}},"required":["value"],"additionalProperties":false},
              {"type":"integer","minimum":1,"maximum":2}
            ]}}
            """);
        WorkflowSchema.Validate(Json("""[{"value":"ok"},2]"""), nested);
        Check(Reject(() => WorkflowSchema.Validate(Json("[]"), nested)) &&
            Reject(() => WorkflowSchema.Validate(Json("[1,2,2]"), nested)) &&
            Reject(() => WorkflowSchema.Validate(Json("[3]"), nested)) &&
            Reject(() => WorkflowSchema.Validate(Json("""[{"value":"long"}]"""), nested)),
            "nested item alternatives retain array, numeric and string bounds");
        Check(Reject(() => WorkflowSchema.Validate(Json("""[{"value":"one","value":"two"}]"""), nested)) &&
            Reject(() => WorkflowSchema.Validate(Json("""{"context":{"status":"needsCompaction","status":"requiredOverflow"}}"""), union)),
            "duplicate data keys at nested union boundaries cannot select or conceal a branch");

        var eight = Json("{\"oneOf\":[" + string.Join(',', Enumerable.Range(0, 8).Select(value => "{\"enum\":[" + value + "]}")) + "]}");
        WorkflowSchema.ValidateSchema(eight);
        WorkflowSchema.Validate(Json("6"), eight);
        Check(true, "the bounded union supports up to eight alternatives");
        Check(Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[]}"""))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":{}}"""))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("{\"oneOf\":[" + string.Join(',', Enumerable.Repeat("{}", 9)) + "]}"))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[null]}"""))),
            "empty, oversized, nonarray and nonobject union schemas fail admission");
        Check(Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[{}, {"type":"string","type":"number"}]}"""))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[{}, {"minimum":5,"maximum":4}]}"""))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[{}, {"properties":{"nested":{"properties":{"duplicate":{},"duplicate":{}}}}}]}"""))) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("""{"oneOf":[{}, {"anyOf":[]}]}"""))),
            "every branch is recursively validated for duplicates, inconsistent bounds and unsupported keywords");
        Check(Reject(() => WorkflowSchema.Validate(Json("true"), Json("""{"oneOf":[{}, {"maxLength":"bad"}]}"""))),
            "direct validation cannot skip an invalid alternative merely because another branch matches");

        var deeplyNested = "{}";
        for (var index = 0; index < 34; index++) deeplyNested = "{\"oneOf\":[" + deeplyNested + "]}";
        Check(Reject(() => WorkflowSchema.ValidateSchema(Json(deeplyNested))),
            "schema nesting is bounded independently of the input document depth");
        var wide = Json("{\"properties\":{" + string.Join(',', Enumerable.Range(0, 2050).Select(index => "\"p" + index + "\":{}")) + "}}");
        Check(Reject(() => WorkflowSchema.ValidateSchema(wide)) &&
            Reject(() => WorkflowSchema.ValidateSchema(Json("{\"description\":\"" + new string('x', 65536) + "\"}"))),
            "schema nodes and serialized schema size have explicit admission bounds");
        Check(Reject(() => WorkflowSchema.Validate(Json("\"" + new string('x', 131072) + "\""), Json("""{"oneOf":[{"type":"string"}]}"""))),
            "union validation retains the existing overall data-size bound");
        var deepData = new string('[', 34) + "0" + new string(']', 34);
        Check(Reject(() => WorkflowSchema.Validate(Json(deepData), Json("""{"oneOf":[{}]}"""))),
            "union validation retains the existing data-depth bound");

        var costlyBranch = """{"type":"array","items":{"oneOf":[{"type":"integer"},{"type":"string"},{"type":"boolean"}]}}""";
        var costly = Json("{\"oneOf\":[" + costlyBranch + "," + costlyBranch + "]}");
        var manyValues = Json("[" + string.Join(',', Enumerable.Repeat("0", 40000)) + "]");
        var exhausted = Rejection(() => WorkflowSchema.Validate(manyValues, costly));
        Check(exhausted?.Contains("evaluation limit", StringComparison.Ordinal) == true,
            "alternatives share a bounded evaluation budget and exhaustion cannot masquerade as a branch mismatch");

        Console.WriteLine($"Workflow schema: {count} pure validation assertions passed.");
    }
}
