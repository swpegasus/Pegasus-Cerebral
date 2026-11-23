using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>The owner approval boundary accepts one exact, bounded command shape.</summary>
internal static class WorkflowPortableSettingsApprovalInput
{
    private const int MaximumJsonBytes = 16_384;
    private const int MaximumDepth = 4;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "pluginId", "packageVersion", "loadedGeneration", "packageFingerprint",
        "schemaFingerprint", "reviewedFields", "expectedRevision", "requestId"
    };
    private static readonly JsonSerializerOptions StrictJson = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumDepth,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    internal static WorkflowPortableSettingsApprovalRequest Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var raw = arguments.GetRawText();
        if (raw.Length > MaximumJsonBytes || Encoding.UTF8.GetByteCount(raw) > MaximumJsonBytes)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !Fields.Contains(property.Name)) throw Invalid();
                switch (property.Name)
                {
                    case "pluginId": RequireString(property.Value, 100); break;
                    case "packageVersion": RequireString(property.Value, 64); break;
                    case "loadedGeneration": RequireString(property.Value, 128); break;
                    case "packageFingerprint": RequireString(property.Value, 64, exact: true); break;
                    case "schemaFingerprint": RequireString(property.Value, 64, exact: true); break;
                    case "requestId": RequireString(property.Value, 128); break;
                    case "expectedRevision":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt64(out var revision) || revision < 0) throw Invalid();
                        break;
                    case "reviewedFields":
                        if (property.Value.ValueKind != JsonValueKind.Array) throw Invalid();
                        var count = 0;
                        foreach (var field in property.Value.EnumerateArray())
                        {
                            if (++count > ServicePluginContract.MaxPortableSettings) throw Invalid();
                            RequireString(field, 100);
                        }
                        if (count == 0) throw Invalid();
                        break;
                }
            }
            if (seen.Count != Fields.Count) throw Invalid();
            return root.Deserialize<WorkflowPortableSettingsApprovalRequest>(StrictJson) ?? throw Invalid();
        }
        catch (JsonException)
        {
            // Parser diagnostics may include snippets or paths; the command boundary returns no input text.
            throw Invalid();
        }
    }

    private static void RequireString(JsonElement value, int maximumLength, bool exact = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > maximumLength || exact && text.Length != maximumLength)
            throw Invalid();
    }

    private static ArgumentException Invalid() => new("Workflow settings approval arguments are invalid.");
}
