using System.Text.Json;

namespace Cerebral.Worker;

/// <summary>On-demand transport schemas. Descriptions stay compact; source bytes never enter discovery.</summary>
internal static class WorkerAttachmentOperationDescriptors
{
    public static IReadOnlyList<WorkerWorkflows.OperationDescriptor> All { get; } = Build();

    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> Build()
    {
        var id = new { type = "string", minLength = 1, maxLength = 128 };
        var scope = new Dictionary<string, object> { ["profileId"] = id, ["conversationId"] = id };
        Dictionary<string, object> Request() => new(scope) { ["requestId"] = id };
        Dictionary<string, object> Reference() => new(scope) { ["id"] = new { type = "string", pattern = "^[0-9a-fA-F]{32}$" },
            ["revision"] = new { type = "integer", minimum = 1 } };
        var begin = Request();
        begin["originalName"] = new { type = "string", minLength = 1, maxLength = 255, description = "Basename only; at most 255 UTF-8 bytes. Originals stay opaque until a compatible text, image, archive or plugin handler resolves them." };
        begin["length"] = new { type = "integer", minimum = 0, maximum = 16 * 1024 * 1024 };
        var upload = Request(); upload["uploadId"] = new { type = "string", pattern = "^[0-9a-fA-F]{32}$" };
        var chunk = new Dictionary<string, object>(upload)
        {
            ["offset"] = new { type = "integer", minimum = 0 },
            ["bytes"] = new { type = "string", contentEncoding = "base64", minLength = 4,
                maxLength = ((WorkerAttachmentTransfers.MaximumChunkBytes + 2) / 3) * 4 }
        };
        var range = Reference();
        range["offset"] = new { type = "integer", minimum = 0 };
        range["count"] = new { type = "integer", minimum = 0, maximum = WorkerAttachmentTransfers.MaximumChunkBytes };
        var text = Reference();
        text["maximumUtf8Bytes"] = new { type = "integer", minimum = 0, maximum = 1024 * 1024, @default = 1024 * 1024 };
        var archiveText = Reference();
        archiveText["entryPath"] = new { type = "string", minLength = 1, maxLength = 512 };
        archiveText["maximumUtf8Bytes"] = new { type = "integer", minimum = 0, maximum = 1024 * 1024, @default = 1024 * 1024 };
        WorkerWorkflows.OperationDescriptor Item(string suffix, string description, Dictionary<string, object> fields,
            bool owner = false, string? optional = null) => new(WorkerAttachmentTransfers.Prefix + suffix, 1, description,
                JsonSerializer.SerializeToElement(new
                {
                    type = "object", properties = fields,
                    required = fields.Keys.Where(x => x != optional).ToArray(), additionalProperties = false
                }), owner);
        return Array.AsReadOnly(new[]
        {
            Item("import.begin", "Owner: start an exact scoped file import. Reuse requestId; no message or job is created.", begin, true),
            Item("import.chunk", "Owner: transfer up to 256 KiB at the next exact offset. Repeated chunks must match.", chunk, true),
            Item("import.commit", "Owner: commit the complete original once. Query import.status if the reply is lost.", upload, true),
            Item("import.abort", "Owner: discard uncommitted staging. A completed original is retained.", upload, true),
            Item("import.status", "Read a scoped import receipt or current staging position without replaying a mutation.", Request()),
            Item("describe", "Read immutable attachment metadata by scoped ID and revision; no file bytes.", Reference()),
            Item("read", "Read at most 256 KiB of exact original bytes by scoped ID, revision and byte range.", range),
            Item("text", "Read complete UTF-8 reference text or report overflow; use ranges for larger transport output.",
                text, optional: "maximumUtf8Bytes"),
            Item("archive.inspect", "List a bounded archive without extracting, executing, or adding members to context.", Reference()),
            Item("archive.extract", "Create one disposable bounded archive derivative through Cortex 7-Zip. Originals remain immutable.", Reference()),
            Item("archive.status", "Read the derivative state without repeating extraction.", Reference()),
            Item("archive.text", "Read one exact strictly UTF-8 extracted member; paths remain archive identities, never host paths.",
                archiveText, optional: "maximumUtf8Bytes")
        });
    }
}
