using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Workflows;

/// <summary>
/// Small explicit JSON-schema subset for module boundaries. Unsupported keywords fail registration;
/// this deliberately does not pretend to implement a complete JSON Schema dialect.
/// </summary>
public static class WorkflowSchema
{
    private const int MaximumSchemaNodes = 2048;
    private const int MaximumEvaluationSteps = 262144;
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "enum", "minimum", "maximum",
        "minLength", "maxLength", "items", "minItems", "maxItems", "title", "description", "default", "oneOf"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "object", "array", "string", "number", "integer", "boolean", "null" };

    public static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schemas must be objects.");
        if (Encoding.UTF8.GetByteCount(schema.GetRawText()) > 64 * 1024)
            throw new ArgumentException("Workflow schema exceeds 64 KiB.");
        var nodes = 0;
        ValidateSchema(schema, 0, ref nodes);
    }

    private static void ValidateSchema(JsonElement schema, int depth, ref int nodes)
    {
        if (depth > 32 || ++nodes > MaximumSchemaNodes)
            throw new ArgumentException("Workflow schema exceeds its nesting or node limit.");
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schemas must be objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException($"Duplicate schema keyword '{property.Name}'.");
            if (!Keywords.Contains(property.Name)) throw new ArgumentException($"Unsupported workflow schema keyword '{property.Name}'.");
            var value = property.Value;
            switch (property.Name)
            {
                case "type":
                    if (value.ValueKind != JsonValueKind.String || !Types.Contains(value.GetString()!))
                        throw new ArgumentException("Schema type must name one supported primitive type.");
                    break;
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema properties must be an object.");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in value.EnumerateObject())
                    {
                        if (field.Name.Length > 256 || field.Name.Contains('\0') || !names.Add(field.Name))
                            throw new ArgumentException("Invalid or duplicate schema property name.");
                        ValidateSchema(field.Value, depth + 1, ref nodes);
                    }
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Schema required must be a string array.");
                    var required = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in value.EnumerateArray())
                        if (field.ValueKind != JsonValueKind.String || !required.Add(field.GetString()!))
                            throw new ArgumentException("Schema required must contain distinct strings.");
                    break;
                case "additionalProperties":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ArgumentException("Only boolean additionalProperties is supported.");
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 64)
                        throw new ArgumentException("Schema enum must contain 1–64 values.");
                    break;
                case "minimum": case "maximum":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var bound) || !double.IsFinite(bound))
                        throw new ArgumentException("Numeric bounds must be finite numbers.");
                    break;
                case "minLength": case "maxLength": case "minItems": case "maxItems":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
                        throw new ArgumentException("Length bounds must be nonnegative integers.");
                    break;
                case "items": ValidateSchema(value, depth + 1, ref nodes); break;
                case "oneOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 8)
                        throw new ArgumentException("Schema oneOf must contain 1–8 object schemas.");
                    foreach (var alternative in value.EnumerateArray())
                        ValidateSchema(alternative, depth + 1, ref nodes);
                    break;
                case "title": case "description":
                    if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Schema text metadata must be strings.");
                    break;
            }
        }
        ValidateRange(schema, "minimum", "maximum");
        ValidateRange(schema, "minLength", "maxLength");
        ValidateRange(schema, "minItems", "maxItems");
    }

    public static void Validate(JsonElement value, JsonElement schema)
    {
        ValidateSchema(schema);
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Command data is undefined.");
        var raw = value.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > WorkflowContract.MaximumStateBytes)
            throw new ArgumentException("Workflow data exceeds 128 KiB.");
        using var bounded = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateValue(bounded.RootElement, schema, "$", 0, new EvaluationBudget());
    }

    private static void ValidateRange(JsonElement schema, string minimum, string maximum)
    {
        if (schema.TryGetProperty(minimum, out var min) && schema.TryGetProperty(maximum, out var max) && min.GetDouble() > max.GetDouble())
            throw new ArgumentException($"Schema {minimum} exceeds {maximum}.");
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string path, int depth, EvaluationBudget budget)
    {
        budget.Step();
        if (depth > 32) throw new ArgumentException("Workflow data nesting exceeds 32.");
        if (schema.TryGetProperty("type", out var type) && !IsType(value, type.GetString()!))
            throw new ArgumentException($"{path} must be {type.GetString()}.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice =>
                JsonNode.DeepEquals(JsonNode.Parse(choice.GetRawText()), JsonNode.Parse(value.GetRawText()))))
            throw new ArgumentException($"{path} is not one of the supported values.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var additional = !schema.TryGetProperty("additionalProperties", out var allowed) || allowed.GetBoolean();
            foreach (var field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw new ArgumentException($"{path} contains a duplicate property.");
                if (hasProperties && properties.TryGetProperty(field.Name, out var childSchema))
                    ValidateValue(field.Value, childSchema, path + "." + field.Name, depth + 1, budget);
                else if (!additional) throw new ArgumentException($"{path}.{field.Name} is not supported.");
                else ValidateValue(field.Value, WorkflowContract.EmptyObject, path + "." + field.Name, depth + 1, budget);
            }
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!fields.Contains(field.GetString()!)) throw new ArgumentException($"{path}.{field.GetString()} is required.");
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            ValidateLength(value.GetArrayLength(), schema, "minItems", "maxItems", path);
            var childSchema = schema.TryGetProperty("items", out var items) ? items : WorkflowContract.EmptyObject;
            foreach (var item in value.EnumerateArray()) ValidateValue(item, childSchema, path + "[]", depth + 1, budget);
        }
        else if (value.ValueKind == JsonValueKind.String)
            ValidateLength(value.GetString()!.EnumerateRunes().Count(), schema, "minLength", "maxLength", path);
        else if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new ArgumentException($"{path} is outside supported numeric range.");
            if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble() ||
                schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble())
                throw new ArgumentException($"{path} is outside the allowed numeric range.");
        }
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                try { ValidateValue(value, alternative, path, depth, budget); }
                catch (ArgumentException error) when (error is not SchemaEvaluationLimitException) { continue; }
                if (++matches > 1) break;
            }
            if (matches != 1) throw new ArgumentException($"{path} must match exactly one oneOf alternative; matched {matches}.");
        }
    }

    // All alternatives share this budget. Exhaustion must escape branch mismatch handling: treating
    // an unfinished branch as a non-match could wrongly accept an otherwise ambiguous value.
    private sealed class EvaluationBudget
    {
        private int steps;
        public void Step()
        {
            if (++steps > MaximumEvaluationSteps)
                throw new SchemaEvaluationLimitException("Workflow schema evaluation limit exceeded; simplify the schema or narrow the payload.");
        }
    }
    private sealed class SchemaEvaluationLimitException(string message) : ArgumentException(message);

    private static void ValidateLength(int length, JsonElement schema, string minKey, string maxKey, string path)
    {
        if (schema.TryGetProperty(minKey, out var min) && length < min.GetInt32() ||
            schema.TryGetProperty(maxKey, out var max) && length > max.GetInt32())
            throw new ArgumentException($"{path} has an unsupported length.");
    }

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && Math.Truncate(number) == number,
        _ => false
    };
}
