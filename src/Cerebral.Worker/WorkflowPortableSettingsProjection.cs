using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

// Only these ordinary values can cross the workflow snapshot boundary. The approval record and
// loaded component must be checked separately; a snapshot entry never grants its own approval.
internal sealed record WorkflowApprovedSettingsEntry(string PluginId, string PluginVersion,
    string LoadedGeneration, string PackageFingerprint, string SchemaFingerprint,
    long ApprovalRevision, bool HadProfileEntry, JsonElement ReviewedValues);

internal static class WorkflowPortableSettingsProjection
{
    internal const int MaximumApprovedOwners = 16;
    internal const int MaximumReviewedValuesBytes = 32_768;
    private const string Refusal = "Approved workflow plugin settings are invalid or changed.";
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private static readonly HashSet<string> EntryFields =
    [
        "pluginId", "pluginVersion", "loadedGeneration", "packageFingerprint",
        "schemaFingerprint", "approvalRevision", "hadProfileEntry", "reviewedValues"
    ];

    internal sealed record Projection(bool HadProfileEntry, JsonElement ReviewedValues);

    // Read the raw persisted AgentProfile, never EffectiveProfile, which deliberately strips
    // plugin settings. Unreviewed declared and opaque fields are neither parsed nor copied.
    internal static Projection Project(AgentProfile rawProfile, string pluginId,
        ServicePluginPortableSettingsBinding schema, IReadOnlyList<string> reviewedFields)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(rawProfile);
            if (rawProfile.PluginSettings is null || schema?.Fields is null ||
                !string.Equals(schema.PluginId, pluginId, StringComparison.Ordinal) ||
                reviewedFields is null || reviewedFields.Count is < 1 or > ServicePluginContract.MaxPortableSettings)
                throw Invalid();
            var schemaFields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            var approved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in reviewedFields)
                if (name is null || !approved.Add(name) || !schemaFields.ContainsKey(name)) throw Invalid();
            if (!rawProfile.PluginSettings.TryGetValue(pluginId, out var source))
                return new(false, EmptyObject());
            if (source.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(source, Json).Length > 65_536)
                throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in source.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid();
                if (!approved.Contains(property.Name)) continue;
                ServicePluginPortableSettingsPolicy.ValidateValue(schemaFields[property.Name], property.Value);
                values.Add(property.Name, property.Value.Clone());
            }
            if (reviewedFields.Any(name => schemaFields[name].Required && !values.ContainsKey(name))) throw Invalid();
            var projected = JsonSerializer.SerializeToElement(values, Json);
            ValidateReviewedValues(projected);
            return new(true, projected);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { throw Invalid(); }
    }

    internal static bool Same(Projection left, Projection right) =>
        left.HadProfileEntry == right.HadProfileEntry &&
        JsonElement.DeepEquals(left.ReviewedValues, right.ReviewedValues);

    internal static IReadOnlyList<WorkflowApprovedSettingsEntry> ReadCollection(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumApprovedOwners)
                throw Invalid();
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in entry.EnumerateObject())
                    if (!EntryFields.Contains(field.Name) || !names.Add(field.Name)) throw Invalid();
                if (names.Count != EntryFields.Count) throw Invalid();
            }
            var parsed = value.Deserialize<WorkflowApprovedSettingsEntry[]>(Json) ?? throw Invalid();
            return ValidateCollection(parsed);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { throw Invalid(); }
    }

    internal static IReadOnlyList<WorkflowApprovedSettingsEntry> ValidateCollection(
        IReadOnlyList<WorkflowApprovedSettingsEntry> collection)
    {
        try
        {
            if (collection is null || collection.Count > MaximumApprovedOwners) throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            var frozen = new List<WorkflowApprovedSettingsEntry>(collection.Count);
            foreach (var entry in collection)
            {
                if (entry is null || entry.PluginId is null || !ids.Add(entry.PluginId)) throw Invalid();
                Configuration.ValidateId(entry.PluginId);
                if (!PluginVersion.TryParse(entry.PluginVersion, out _) ||
                    string.IsNullOrWhiteSpace(entry.LoadedGeneration) || entry.LoadedGeneration.Length > 160 ||
                    entry.LoadedGeneration.Any(char.IsControl) ||
                    !Fingerprint(entry.PackageFingerprint) || !Fingerprint(entry.SchemaFingerprint) ||
                    entry.ApprovalRevision < 1)
                    throw Invalid();
                ValidateReviewedValues(entry.ReviewedValues);
                if (!entry.HadProfileEntry && entry.ReviewedValues.EnumerateObject().Any()) throw Invalid();
                total = checked(total + JsonSerializer.SerializeToUtf8Bytes(entry.ReviewedValues, Json).Length);
                if (total > MaximumReviewedValuesBytes) throw Invalid();
                frozen.Add(entry with { ReviewedValues = entry.ReviewedValues.Clone() });
            }
            return Array.AsReadOnly(frozen.OrderBy(entry => entry.PluginId, StringComparer.Ordinal).ToArray());
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { throw Invalid(); }
    }

    private static void ValidateReviewedValues(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            Configuration.ValidateId(field.Name);
            if (!names.Add(field.Name)) throw Invalid();
            if (field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String) continue;
            if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out _)) continue;
            if (field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() <= 128 &&
                field.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) continue;
            throw Invalid();
        }
        if (names.Count > ServicePluginContract.MaxPortableSettings ||
            JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > MaximumReviewedValuesBytes) throw Invalid();
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(
        new Dictionary<string, JsonElement>(), Json);

    private static bool Fingerprint(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static InvalidDataException Invalid() => new(Refusal);
}
