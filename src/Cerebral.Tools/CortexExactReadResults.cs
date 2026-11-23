using System.Text.Json;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    // A transport acknowledgement is not proof that the requested complete typed record was read.
    // Keep this private to dispatch admission; the original provider JSON remains in the receipt.
    private static void ValidateExactCortexRead(JsonElement arguments, JsonElement envelope)
    {
        RequireExactReadKeys(envelope);
        RequireCompleteRead(envelope);
        var payload = ExactReadPayload(envelope);
        RequireCompleteRead(payload);
        var requested = arguments.TryGetProperty("records", out var records)
            ? records.EnumerateArray().ToArray() : [arguments];
        if (requested.Length is < 1 or > 8) throw InvalidExactRead("invalid requested coordinate count");
        if (requested.Any(value => string.IsNullOrWhiteSpace(ExactReadString(value, "namespace")) ||
            string.IsNullOrWhiteSpace(ExactReadString(value, "recordId")) ||
            ExactReadString(value, "recordType") is not ("knowledge" or "task")))
            throw InvalidExactRead("requested typed coordinate is incomplete");
        var expectedSpaces = requested.Select(value => ExactReadString(value, "namespace")).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!payload.TryGetProperty("effectiveNamespaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array ||
            spaces.GetArrayLength() != expectedSpaces.Count || spaces.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) ||
            !spaces.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedSpaces))
            throw InvalidExactRead("returned namespace selection does not match the request");

        if (arguments.TryGetProperty("records", out _))
        {
            if (!payload.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != requested.Length)
                throw InvalidExactRead("batch omitted or added a requested record");
            var remaining = items.EnumerateArray().ToList();
            foreach (var coordinate in requested)
            {
                var matches = remaining.Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("coordinate", out var actual) && ExactReadIdentity(actual, coordinate)).ToArray();
                if (matches.Length != 1) throw InvalidExactRead("batch has a missing or duplicate typed coordinate");
                var item = matches[0];
                RequireCompleteRead(item);
                var actual = item.GetProperty("coordinate");
                RequireCompleteRead(actual);
                ValidateExactReadRecord(item, actual, coordinate);
                remaining.Remove(item);
            }
        }
        else
        {
            if (!ExactReadIdentity(payload, arguments, requireNamespace: false))
                throw InvalidExactRead("returned type or record ID does not match the request");
            ValidateExactReadRecord(payload, payload, arguments);
        }
    }

    private static void ValidateExactReadRecord(JsonElement owner, JsonElement returned, JsonElement requested)
    {
        var revision = ExactReadRevision(returned);
        if (requested.TryGetProperty("revision", out var pin) && pin.ValueKind != JsonValueKind.Null &&
            (pin.ValueKind != JsonValueKind.Number || !pin.TryGetInt64(out var expected) || expected < 1 || expected != revision))
            throw InvalidExactRead("returned revision does not match the explicit request");
        if (!owner.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
            throw InvalidExactRead("complete record body is missing");
        RequireCompleteRead(record, taskRecord: ExactReadString(requested, "recordType").Equals("task", StringComparison.OrdinalIgnoreCase));
        var type = ExactReadString(requested, "recordType");
        var identity = type.Equals("knowledge", StringComparison.OrdinalIgnoreCase) ? "recordId"
            : type.Equals("task", StringComparison.OrdinalIgnoreCase) ? "taskId" : throw InvalidExactRead("unsupported typed coordinate");
        if (ExactReadString(record, "namespace") != ExactReadString(requested, "namespace") ||
            ExactReadString(record, identity) != ExactReadString(requested, "recordId") || ExactReadRevision(record) != revision ||
            !record.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw InvalidExactRead("record identity, revision or full content is missing or inconsistent");
        // Task lifecycle is not knowledge activity. Archived/completed task bodies remain exact reads.
        var lifecycle = identity == "taskId" ? "status" : "state";
        if (string.IsNullOrWhiteSpace(ExactReadString(record, lifecycle))) throw InvalidExactRead("record lifecycle is missing");
    }

    private static bool ExactReadIdentity(JsonElement actual, JsonElement requested, bool requireNamespace = true) =>
        actual.ValueKind == JsonValueKind.Object &&
        (!requireNamespace || ExactReadString(actual, "namespace") == ExactReadString(requested, "namespace")) &&
        ExactReadString(actual, "recordId") == ExactReadString(requested, "recordId") &&
        ExactReadString(actual, "recordType").Equals(ExactReadString(requested, "recordType"), StringComparison.OrdinalIgnoreCase);

    private static JsonElement ExactReadPayload(JsonElement envelope)
    {
        var hasStructured = envelope.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object;
        if (envelope.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in blocks.EnumerateArray().Take(4))
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    try
                    {
                        using var parsed = JsonDocument.Parse(text.GetString()!, new() { MaxDepth = 32 });
                        if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
                        RequireExactReadKeys(parsed.RootElement);
                        if (!hasStructured) return parsed.RootElement.Clone();
                        // Plain provider summaries are not full-record copies. Conflicting typed copies are not evidence.
                        if ((parsed.RootElement.TryGetProperty("record", out _) || parsed.RootElement.TryGetProperty("items", out _)) &&
                            !JsonElement.DeepEquals(structured, parsed.RootElement)) throw InvalidExactRead("conflicting typed response copies");
                    }
                    catch (JsonException) { }
        if (hasStructured) return structured;
        throw InvalidExactRead("typed response envelope is missing");
    }

    private static void RequireCompleteRead(JsonElement value, bool taskRecord = false)
    {
        foreach (var name in new[] { "truncated", "partial", "excerpt", "metadataOnly", "bodyOmitted" })
            if (value.TryGetProperty(name, out var flag) && flag.ValueKind != JsonValueKind.False)
                throw InvalidExactRead("provider marked the body incomplete: " + name);
        foreach (var name in new[] { "complete", "bodyIncluded", "fullBodyIncluded" })
            if (value.TryGetProperty(name, out var flag) && flag.ValueKind != JsonValueKind.True)
                throw InvalidExactRead("provider did not confirm the complete body: " + name);
        foreach (var name in new[] { "bodyStatus", "contentStatus", "disposition", "mode", "status" })
            if (!(taskRecord && name == "status") && value.TryGetProperty(name, out var state) && state.ValueKind == JsonValueKind.String &&
                state.GetString() is "partial" or "truncated" or "excerpt" or "incomplete" or "metadata-only" or "metadata_only" or "metadataOnly")
                throw InvalidExactRead("provider returned incomplete body status: " + name);
        if (value.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw InvalidExactRead("provider returned an error in the successful envelope");
    }

    private static string ExactReadString(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : "";
    private static long ExactReadRevision(JsonElement value) =>
        value.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var number) && number > 0
            ? number : throw InvalidExactRead("positive returned revision is missing");
    private static void RequireExactReadKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidExactRead("duplicate response field");
                RequireExactReadKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireExactReadKeys(item);
    }
    private static InvalidDataException InvalidExactRead(string reason) => new("Cortex exact read was not confirmed: " + reason + ".");
}
