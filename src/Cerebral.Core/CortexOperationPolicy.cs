using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

public static class CortexOperationPolicy
{
    public static bool IsDurableWrite(string tool, JsonElement arguments)
    {
        var name = tool.Split('.').Last();
        if (name == "cortex_knowledge")
        {
            var action = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("action", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return action is not ("read" or "analyze");
        }
        return name == "cortex_finish" && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("knowledge", out var knowledge) && knowledge.ValueKind != JsonValueKind.Null &&
            (knowledge.ValueKind != JsonValueKind.Array || knowledge.GetArrayLength() > 0);
    }

    public static bool IsMutation(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Cortex arguments must be an object.");
        return tool.Split('.').Last() switch
        {
            "cortex_task" => Choice(arguments, "action", "create", "list", "get", "assign", "claim", "update", "block", "complete", "reopen", "archive", "dependencyAdd", "dependencyRemove", "history") is not ("list" or "get" or "history"),
            "cortex_knowledge" => Choice(arguments, "action", "create", "read", "revise", "supersede", "archive", "analyze") is not ("read" or "analyze"),
            "cortex_finish" => true,
            "cortex_start" or "cortex_search" or "cortex_read" or "cortex_status" or "cortex_namespaces" => false,
            _ => throw new UnauthorizedAccessException("This Cortex operation needs an explicit policy mapping before agent access.")
        };
    }

    // Mutations remain exactly the payload that was reviewed or constructed by the service.
    // A missing identity, revision, or retry key must be corrected before approval, never filled in afterwards.
    public static void ValidateExplicitMutation(string tool, JsonElement arguments, string sessionId)
    {
        if (!IsMutation(tool, arguments)) return;
        var name = tool.Split('.').Last();
        Required(arguments, "namespace");
        Required(arguments, "idempotencyKey");
        if (name == "cortex_knowledge")
        {
            Required(arguments, "recordId");
            var action = Required(arguments, "action");
            Revision(arguments, required: action != "create");
            Provenance(arguments);
            if (action == "create") KnowledgeBody(arguments);
            if (action == "revise" && !new[] { "kind", "tier", "title", "content", "tags" }.Any(key => arguments.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null))
                throw new ArgumentException("Cortex knowledge revise must change a field.");
            if (action == "supersede") Required(arguments, "replacementRecordId");
        }
        else
        {
            if (Required(arguments, "taskId") != sessionId) throw new UnauthorizedAccessException("Cortex taskId must match the current Cerebral session.");
            if (name == "cortex_task")
            {
                var action = Required(arguments, "action");
                Revision(arguments, required: action != "create");
                if (action == "create") { Required(arguments, "title"); Text(arguments, "content"); }
                if (action == "update" && !arguments.TryGetProperty("title", out _) && !arguments.TryGetProperty("content", out _)) throw new ArgumentException("Cortex task update requires title or content.");
                if (action == "assign") Required(arguments, "assignee");
                if (action == "claim") Required(arguments, "claimedBy");
                if (action == "block") Required(arguments, "reason");
                if (action is "dependencyAdd" or "dependencyRemove") Required(arguments, "dependencyId");
            }
            else
            {
                Revision(arguments, required: true);
                if (Choice(arguments, "taskAction", "none", "update", "block", "complete") == "block") Required(arguments, "blockedReason");
                Required(arguments, "checkpoint"); Required(arguments, "evidence");
                if (!arguments.TryGetProperty("knowledge", out var records) || records.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Cortex finish requires an explicit knowledge array; use [] for task-only continuity.");
                if (records.GetArrayLength() > 8) throw new ArgumentException("Cortex batches are limited to eight records.");
                foreach (var record in records.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object) throw new ArgumentException("Cortex finish knowledge entries must be objects.");
                    Required(record, "recordId"); Required(record, "idempotencyKey");
                    Revision(record, required: false); KnowledgeBody(record);
                    if (!record.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        throw new ArgumentException("Each Cortex finish knowledge entry requires a tags array of strings.");
                }
            }
        }
    }


    public static string ProviderFingerprint(McpRegistration registration)
    {
        // Preserve existing saved bindings for providers whose transport has not changed.
        object identity = registration.CortexNative is null
            ? new { registration.Id, registration.Executable, registration.Arguments, registration.Url, registration.ViaUserConnector }
            : new { registration.Id, registration.Executable, registration.Arguments, registration.Url, registration.ViaUserConnector, registration.CortexNative };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, CerebralJson.Options))).ToLowerInvariant();
    }

    // The installed Cortex SQLite backend can return a no-commit disposition with isError=false.
    // Accept only a complete receipt for the exact coordinates and resulting revisions.
    public static ToolResult ConfirmMutation(string tool, JsonElement arguments, ToolResult result)
    {
        if (!result.Success || !IsMutation(tool, arguments)) return result;
        try
        {
            if (result.Truncated) throw new InvalidDataException("The mutation receipt was truncated.");
            using var document = JsonDocument.Parse(result.Content);
            var envelope = document.RootElement;
            if (!envelope.TryGetProperty("structuredContent", out var receipt) || receipt.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The mutation omitted its structured receipt.");
            var disposition = Required(receipt, "disposition");
            var expectedNamespace = Required(arguments, "namespace");
            if (!receipt.TryGetProperty("effectiveNamespaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array ||
                spaces.GetArrayLength() != 1 || spaces[0].ValueKind != JsonValueKind.String || spaces[0].GetString() != expectedNamespace)
                throw new InvalidDataException("The mutation receipt did not match its exact namespace.");
            if (disposition is not ("appended" or "idempotent"))
                throw new InvalidDataException($"Cortex did not confirm a committed write; disposition={disposition}.");
            var name = tool.Split('.').Last();
            if (name == "cortex_knowledge") VerifyRecord(receipt.GetProperty("record"), arguments, "recordId", expectedNamespace);
            else
            {
                VerifyRecord(receipt.GetProperty("task"), arguments, "taskId", expectedNamespace);
                if (name == "cortex_finish")
                {
                    var expected = arguments.GetProperty("knowledge");
                    var actual = receipt.GetProperty("knowledge");
                    if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.GetArrayLength())
                        throw new InvalidDataException("The finish receipt did not include each proposed knowledge record.");
                    foreach (var record in expected.EnumerateArray())
                    {
                        var id = Required(record, "recordId");
                        var matches = actual.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object &&
                            value.TryGetProperty("recordId", out var coordinate) && coordinate.ValueKind == JsonValueKind.String && coordinate.GetString() == id).ToArray();
                        if (matches.Length != 1) throw new InvalidDataException("The finish knowledge receipt has missing or duplicate coordinates.");
                        VerifyRecord(matches[0], record, "recordId", expectedNamespace);
                    }
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException or OverflowException)
        {
            return result with { Success = false, Content = "Cortex write outcome was not confirmed: " + ex.Message + "\n" + result.Content };
        }
    }

    public static bool HasNoCommitReceipt(ToolResult result)
    {
        try
        {
            // ConfirmMutation may prepend an explanation; inspect only a retained complete JSON envelope.
            var offset = result.Content.IndexOf('{');
            if (offset < 0 || result.Truncated) return false;
            using var document = JsonDocument.Parse(result.Content[offset..]);
            return document.RootElement.TryGetProperty("structuredContent", out var receipt) &&
                receipt.TryGetProperty("disposition", out var disposition) &&
                disposition.ValueKind == JsonValueKind.String && disposition.GetString() is "conflict" or "stale-revision" or "not-found" or "rejected";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    private static void VerifyRecord(JsonElement receipt, JsonElement arguments, string idName, string expectedNamespace)
    {
        if (receipt.ValueKind != JsonValueKind.Object || Required(receipt, "namespace") != expectedNamespace || Required(receipt, idName) != Required(arguments, idName) ||
            !receipt.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var value))
            throw new InvalidDataException("The mutation receipt did not match its record coordinates.");
        var expected = arguments.TryGetProperty("expectedRevision", out var prior) && prior.ValueKind == JsonValueKind.Number ? prior.GetInt64() : 0;
        if (value != checked(expected + 1)) throw new InvalidDataException("The mutation receipt did not confirm the expected resulting revision.");
    }

    private static void KnowledgeBody(JsonElement arguments)
    {
        Required(arguments, "title"); Text(arguments, "content");
        Choice(arguments, "kind", "correction", "preference", "workaround", "failure", "regression", "decision", "tenet", "observation");
        Choice(arguments, "tier", "explicit", "verified", "inferred");
        Provenance(arguments);
    }

    private static void Provenance(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("provenance", out var provenance) || provenance.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Cortex knowledge requires explicit provenance.");
        Choice(provenance, "source", "user", "agent", "imported", "system");
    }

    private static void Revision(JsonElement arguments, bool required)
    {
        if (!arguments.TryGetProperty("expectedRevision", out var revision))
        {
            if (required) throw new ArgumentException("Cortex mutation requires explicit expectedRevision from an exact retained read.");
            return;
        }
        if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var value) || value < 1)
            throw new ArgumentException("Cortex expectedRevision must identify the current retained revision.");
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException($"Cortex mutation requires explicit {name} text.");

    private static string Required(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"Cortex mutation requires explicit nonempty {name}.");

    private static string Choice(JsonElement arguments, string name, params string[] choices)
    {
        var value = Required(arguments, name);
        return choices.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException($"Unsupported Cortex {name} '{value}'; use {string.Join(", ", choices)}.");
    }
}
