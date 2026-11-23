using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Reads the versioned owner setting for UI defaults; the service remains authoritative for execution admission.</summary>
public sealed record AgentWorkflowBinding(string DefinitionId, string DefinitionVersion, JsonElement Input)
{
    public const string SettingsKey = "cerebral.coordination";
    public const int MaximumBytes = 8192;

    /// <summary>Reads a present binding without adding defaults, resolving versions or changing its input.</summary>
    public static AgentWorkflowBinding Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumBytes)
            throw new ArgumentException("Workflow binding is unavailable or exceeds its size limit.");
        if (value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new ArgumentException("Workflow binding contains duplicate fields.");
        var fields = value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        string[] expected = ["schemaVersion", "definitionId", "definitionVersion", "input"];
        if (fields.Count != 4 || fields.Keys.Except(expected, StringComparer.Ordinal).Any() ||
            fields["schemaVersion"].ValueKind != JsonValueKind.Number || !fields["schemaVersion"].TryGetInt32(out var schema) || schema != 1 ||
            !Text(fields["definitionId"], 256) || !Text(fields["definitionVersion"], 64) ||
            fields["input"].ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Saved workflow requires schema version 1, exact workflow/version and object inputs.");
        return new(fields["definitionId"].GetString()!, fields["definitionVersion"].GetString()!, fields["input"].Clone());
    }

    public JsonElement ToSettings()
    {
        var value = CerebralJson.Element(new { schemaVersion = 1, definitionId = DefinitionId, definitionVersion = DefinitionVersion, input = Input });
        _ = Read(value);
        return value;
    }

    private static bool Text(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Contains('\0');
}
