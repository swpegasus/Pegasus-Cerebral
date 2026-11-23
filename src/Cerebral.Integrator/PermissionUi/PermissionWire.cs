using System.IO;
using System.Text.Json;

namespace Cerebral.PermissionUi;

// Private owner-protocol parsing, compiled into UI consumers; not a public SDK facet.
internal static class PermissionWire
{
    internal static void Need(bool value, string reason)
    { if (!value) throw new InvalidDataException("Permission data is unavailable or changed (" + reason + "). Reopen this editor."); }
    internal static void Fields(JsonElement value, params string[] fields)
    {
        Need(value.ValueKind == JsonValueKind.Object, "object");
        var allowed = fields.ToHashSet(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject()) Need(allowed.Contains(p.Name) && seen.Add(p.Name), "fields");
        Need(seen.SetEquals(allowed), "missing-field");
    }
    internal static string Text(JsonElement value, int max = 256)
    {
        Need(value.ValueKind == JsonValueKind.String, "text");
        var text = value.GetString()!;
        Need(!string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl), "text-bound");
        return text;
    }
    internal static string? OptionalText(JsonElement value, int max = 256) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, max);
    internal static string Description(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return "";
        Need(value.ValueKind == JsonValueKind.String, "description");
        var text = value.GetString()!;
        Need(text.Length <= 4000 && !text.Contains('\0'), "description-bound");
        return text;
    }
    internal static string Hash(JsonElement value)
    { var text = Text(value, 64); Need(text.Length == 64 && text.All(Uri.IsHexDigit), "hash"); return text; }
    internal static string? OptionalHash(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Hash(value);
    internal static long Revision(JsonElement value)
    { Need(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "revision"); var n = value.GetInt64(); Need(n >= 0, "revision-bound"); return n; }
    internal static int Offset(JsonElement value)
    { var n = Revision(value); Need(n <= 4096, "offset-bound"); return (int)n; }
    internal static bool Boolean(JsonElement value)
    { Need(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "boolean"); return value.GetBoolean(); }
    internal static bool? OptionalBoolean(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Boolean(value);
    internal static string[] Strings(JsonElement value, int maxCount = 64)
    {
        Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maxCount, "list");
        return value.EnumerateArray().Select(x => Text(x, 1024)).ToArray();
    }
    internal static string Decision(JsonElement value)
    { var text = Text(value, 16); Need(text is "inherit" or "allow" or "deny", "decision"); return text; }
}

internal sealed record RegisteredPermissionRow(string Kind, string Id, string? Capability, string Description,
    string? Source, string? DescriptorFingerprint, string[] SecondaryRequirements, bool? ModelVisible)
{
    internal static readonly string[] FieldNames = ["kind", "id", "capability", "description", "source",
        "descriptorFingerprint", "secondaryRequirements", "modelVisible"];
    internal static RegisteredPermissionRow Read(JsonElement body, bool fields = true, bool requireToolDescriptor = true)
    {
        if (fields) PermissionWire.Fields(body, FieldNames);
        var kind = PermissionWire.Text(body.GetProperty("kind"), 16);
        PermissionWire.Need(kind is "tool" or "capability", "kind");
        var id = PermissionWire.Text(body.GetProperty("id"));
        PermissionWire.Need(id != "*", "catalog-wildcard");
        var row = new RegisteredPermissionRow(kind, id,
            PermissionWire.OptionalText(body.GetProperty("capability")),
            PermissionWire.Description(body.GetProperty("description")),
            PermissionWire.OptionalText(body.GetProperty("source"), 1024),
            PermissionWire.OptionalHash(body.GetProperty("descriptorFingerprint")),
            PermissionWire.Strings(body.GetProperty("secondaryRequirements"), 32),
            PermissionWire.OptionalBoolean(body.GetProperty("modelVisible")));
        PermissionWire.Need(!requireToolDescriptor || kind != "tool" || row.Capability is not null && row.Source is not null &&
            row.DescriptorFingerprint is not null && row.ModelVisible is not null, "tool-descriptor");
        return row;
    }
    internal string Detail => Description + (Source is null ? "" : "\nSource: " + Source) +
        (DescriptorFingerprint is null ? "" : "\nRegistration: " + DescriptorFingerprint) +
        (SecondaryRequirements.Length == 0 ? "" : "\nOther requirements: " + string.Join(", ", SecondaryRequirements)) +
        (ModelVisible == false ? "\nStructured/workflow use only; not advertised as a model action." : "");
}

internal sealed record RegisteredPermissionCatalog(string WorkerInstallationId, string Generation,
    RegisteredPermissionRow[] Items, string[] UnavailableServers)
{
    internal static async Task<RegisteredPermissionCatalog> ReadAsync(
        Func<object, CancellationToken, Task<JsonElement>> read, Func<bool> current, CancellationToken token)
    {
        string? generation = null, installation = null; string[]? unavailable = null;
        var offset = 0; var rows = new List<RegisteredPermissionRow>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 64; page++)
        {
            token.ThrowIfCancellationRequested(); PermissionWire.Need(current(), "owner-binding");
            var args = new Dictionary<string, object> { ["offset"] = offset, ["limit"] = 100 };
            if (generation is not null) args["expectedGeneration"] = generation;
            var body = await read(args, token).WaitAsync(token);
            PermissionWire.Need(current(), "owner-binding");
            PermissionWire.Need(System.Text.Encoding.UTF8.GetByteCount(body.GetRawText()) <= 262144, "page-bound");
            PermissionWire.Fields(body, "workerId", "workerInstallationId", "generation", "offset", "items", "nextOffset", "unavailableServers");
            PermissionWire.Need(PermissionWire.Text(body.GetProperty("workerId")) == "local", "local-owner");
            var worker = PermissionWire.Text(body.GetProperty("workerInstallationId"), 128);
            var gen = PermissionWire.Hash(body.GetProperty("generation"));
            var missing = PermissionWire.Strings(body.GetProperty("unavailableServers"));
            installation ??= worker; generation ??= gen; unavailable ??= missing;
            PermissionWire.Need(worker == installation && gen == generation && missing.SequenceEqual(unavailable, StringComparer.Ordinal), "page-generation");
            PermissionWire.Need(PermissionWire.Offset(body.GetProperty("offset")) == offset, "page-offset");
            var items = body.GetProperty("items");
            PermissionWire.Need(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 100, "page-items");
            foreach (var value in items.EnumerateArray())
            {
                var row = RegisteredPermissionRow.Read(value);
                PermissionWire.Need(keys.Add(row.Kind + "\0" + row.Id) && rows.Count < 4096, "duplicate-or-total");
                rows.Add(row);
            }
            var next = body.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) return new(installation, generation, rows.ToArray(), unavailable);
            var n = PermissionWire.Offset(next);
            PermissionWire.Need(items.GetArrayLength() > 0 && n == offset + items.GetArrayLength(), "next-offset");
            offset = n;
        }
        throw new InvalidDataException("The registered permission catalog exceeds the supported page bound.");
    }
}
