using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

public sealed record WorkflowDataStage(string Id, string Tool, JsonElement Arguments, string? WorkingDirectory = null);
public sealed record WorkflowDataEdition(string Schema, string Id, string Version, string Description,
    JsonElement InputSchema, JsonElement OutputSchema, IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<WorkflowDataStage> Stages, JsonElement Output);
public sealed record WorkflowDataEntry(string ProjectId, string Hash, WorkflowDataEdition Edition);
public sealed record WorkflowDataRegistration(WorkflowDataEntry Entry, bool Created);
public sealed record WorkflowDataSummary(string Id, string Version, string Description, string Hash,
    IReadOnlyList<string> RequiredCapabilities, int StageCount);
public sealed record WorkflowDataPage(IReadOnlyList<WorkflowDataSummary> Entries, string? NextAfter);

/// <summary>
/// Private immutable DATA editions. The host resolves project authority and allowed tools separately;
/// recording an edition grants neither tool access nor permission to load native code.
/// </summary>
public sealed partial class WorkflowDataCatalog
{
    private readonly StateStore store;
    public WorkflowDataCatalog(StateStore store) => this.store = store ?? throw new ArgumentNullException(nameof(store));
    public const string Schema = "cerebral.workflow-data/1";
    public const int MaximumEditionBytes = 32 * 1024;
    public const int MaximumStages = 16;
    public const int MaximumResolvedBytes = WorkflowContract.MaximumStateBytes;
    public const int MaximumEditionsPerProject = 128;
    private const string RecordKind = "workflow-data";
    private const string IndexSchema = "cerebral.workflow-data-index/1";
    private const int MaximumIndexBytes = 1024 * 1024;
    private const int MaximumNodes = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private sealed record Index(string Schema, string ProjectId, IReadOnlyList<WorkflowDataSummary> Entries);

    public WorkflowDataRegistration Register(string projectId, WorkflowDataEdition edition)
    {
        ValidateIdentifier(projectId, "project");
        var frozen = Freeze(edition);
        var hash = Hash(frozen);
        return store.Locked(() =>
        {
            var index = ReadIndex(projectId);
            var key = EditionKey(projectId, frozen.Id, frozen.Version);
            var prior = ReadEntry(key);
            var summary = index.Entries.SingleOrDefault(x => x.Id == frozen.Id && x.Version == frozen.Version);
            if (prior is not null)
            {
                VerifyEntry(prior, projectId, frozen.Id, frozen.Version, prior.Hash);
                if (summary is null || summary.Hash != prior.Hash)
                    throw new InvalidDataException("The workflow edition index does not match its saved entry.");
                if (prior.Hash != hash)
                    throw new WorkflowConflictException("This exact workflow ID/version already has different immutable content. Register a new version.");
                return new WorkflowDataRegistration(FreezeEntry(prior), false);
            }
            if (summary is not null)
                throw new InvalidDataException("The indexed workflow edition is missing; registration cannot replace it.");
            if (index.Entries.Count >= MaximumEditionsPerProject)
                throw new InvalidOperationException("The project already has 128 immutable workflow editions.");
            var entry = new WorkflowDataEntry(projectId, hash, frozen);
            var next = index with
            {
                Entries = index.Entries.Append(new WorkflowDataSummary(frozen.Id, frozen.Version,
                    frozen.Description, hash, frozen.RequiredCapabilities, frozen.Stages.Count))
                    .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray()
            };
            // Both rows use the existing SQLite transaction. A failed index write leaves no orphan edition.
            store.Put(RecordKind, key, entry);
            store.Put(RecordKind, IndexKey(projectId), next);
            return new WorkflowDataRegistration(FreezeEntry(entry), true);
        });
    }

    public WorkflowDataEntry Get(string projectId, string id, string version, string hash)
    {
        ValidateIdentifier(projectId, "project"); ValidateCoordinate(id, version); ValidateHash(hash);
        var entry = ReadEntry(EditionKey(projectId, id, version))
            ?? throw new KeyNotFoundException("The exact project workflow edition is unavailable.");
        VerifyEntry(entry, projectId, id, version, hash);
        return FreezeEntry(entry);
    }

    public WorkflowDataPage List(string projectId, int limit = 40, string? after = null)
    {
        ValidateIdentifier(projectId, "project");
        if (limit is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(limit), "Workflow pages contain 1–40 summaries.");
        var cursor = after is null ? null : DecodeCursor(projectId, after);
        var entries = ReadIndex(projectId).Entries
            .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal)
            .Where(x => cursor is null || StringComparer.Ordinal.Compare(x.Id, cursor.Value.Id) > 0 ||
                x.Id == cursor.Value.Id && StringComparer.Ordinal.Compare(x.Version, cursor.Value.Version) > 0)
            .Take(limit + 1).ToArray();
        var page = entries.Take(limit).Select(x => x with
            { RequiredCapabilities = Array.AsReadOnly(x.RequiredCapabilities.ToArray()) }).ToArray();
        return new(Array.AsReadOnly(page), entries.Length > limit ? EncodeCursor(projectId, page[^1].Id, page[^1].Version) : null);
    }

    public static void Validate(WorkflowDataEdition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);
        if (edition.Schema != Schema) throw new ArgumentException("Unsupported workflow DATA schema.");
        ValidateCoordinate(edition.Id, edition.Version);
        if (edition.Description is null || edition.Description.Length == 0 ||
            Encoding.UTF8.GetByteCount(edition.Description) > 1024 || edition.Description.Contains('\0'))
            throw new ArgumentException("Workflow description requires 1–1024 UTF-8 bytes without NUL.");
        ValidateCapabilities(edition.RequiredCapabilities);
        WorkflowSchema.ValidateSchema(edition.InputSchema); WorkflowSchema.ValidateSchema(edition.OutputSchema);
        if (edition.InputSchema.TryGetProperty("type", out var inputType) && inputType.GetString() != "object")
            throw new ArgumentException("Workflow DATA run input is an object; a declared input type must be object.");
        if (edition.Stages is null || edition.Stages.Count is < 1 or > MaximumStages)
            throw new ArgumentException("Workflow DATA requires 1–16 linear stages.");
        var earlier = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in edition.Stages)
        {
            if (stage is null) throw new ArgumentException("Workflow stages cannot be null.");
            ValidateIdentifier(stage.Id, "stage"); ValidateToolName(stage.Tool);
            if (earlier.Contains(stage.Id)) throw new ArgumentException("Workflow stage IDs must be distinct.");
            if (stage.Arguments.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A stage arguments template must be an object.");
            if (stage.WorkingDirectory is { } directory && (directory.Length == 0 ||
                directory.Length > 32768 || directory.Contains('\0')))
                throw new ArgumentException("WorkingDirectory must be absent or bounded nonempty text without NUL.");
            ValidateTemplate(stage.Arguments, earlier);
            earlier.Add(stage.Id);
        }
        ValidateTemplate(edition.Output, earlier);
        if (!HasReference(edition.Output)) WorkflowSchema.Validate(edition.Output, edition.OutputSchema);
        // Duplicate fields, deep/undefined JSON and non-finite numbers fail before canonical hashing.
        CheckJson(edition.InputSchema); CheckJson(edition.OutputSchema);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(edition, Json);
            if (bytes.Length > MaximumEditionBytes) throw new ArgumentException("Workflow DATA edition exceeds 32 KiB.");
        }
        catch (JsonException error) { throw new ArgumentException("Workflow DATA edition exceeds its JSON nesting bound.", error); }
    }

    public static void ValidateToolNames(WorkflowDataEdition edition, IReadOnlyList<ToolDescriptor> allowed)
    {
        Validate(edition); ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count > 4096) throw new ArgumentException("The supplied tool catalog exceeds its bound.");
        var names = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in allowed)
        {
            if (descriptor is null || string.IsNullOrEmpty(descriptor.Name) || !names.TryAdd(descriptor.Name, descriptor))
                throw new ArgumentException("The supplied tool catalog has null or ambiguous duplicate descriptors.");
        }
        foreach (var stage in edition.Stages)
        {
            if (!names.TryGetValue(stage.Tool, out var descriptor))
                throw new UnauthorizedAccessException("The exact workflow stage tool is unavailable under the current grants.");
            if (string.IsNullOrEmpty(descriptor.Capability) ||
                !edition.RequiredCapabilities.Contains(descriptor.Capability, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Every stage tool capability must be declared by the edition.");
            if (descriptor.InputSchema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The admitted stage tool has no object input schema.");
        }
        // Tool-specific schema dialects and dynamic resolved arguments stay at normal tool admission.
        // The supplied catalog must already be filtered by the host; names cannot prove arbitrary plugin semantics.
    }

    public static JsonElement ResolveTemplate(JsonElement template, JsonElement input,
        IReadOnlyDictionary<string, JsonElement> stageResults)
    {
        ArgumentNullException.ThrowIfNull(stageResults);
        if (stageResults.Count > MaximumStages) throw new ArgumentException("Too many prior stage results.");
        CheckJson(input);
        foreach (var pair in stageResults) { ValidateIdentifier(pair.Key, "stage"); CheckJson(pair.Value); }
        ValidateTemplate(template, stageResults.Keys.ToHashSet(StringComparer.Ordinal));
        using var stream = new LimitedStream(MaximumResolvedBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteResolved(writer, template, input, stageResults);
            writer.Flush();
        }
        using var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return document.RootElement.Clone();
    }

    public static string Hash(WorkflowDataEdition edition)
    {
        var frozen = Freeze(edition);
        frozen = frozen with { RequiredCapabilities = frozen.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray() };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, JsonSerializer.SerializeToElement(frozen, Json));
            writer.Flush();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private Index ReadIndex(string projectId)
    {
        var raw = store.Get<JsonElement>(RecordKind, IndexKey(projectId));
        if (raw.ValueKind == JsonValueKind.Undefined) return new(IndexSchema, projectId, []);
        var index = Read<Index>(raw, MaximumIndexBytes);
        if (index.Schema != IndexSchema || index.ProjectId != projectId || index.Entries is null ||
            index.Entries.Count > MaximumEditionsPerProject)
            throw new InvalidDataException("The saved project workflow index is incompatible.");
        var seen = new HashSet<(string, string)>();
        foreach (var item in index.Entries)
        {
            if (item is null) throw new InvalidDataException("The saved workflow index contains a null entry.");
            ValidateCoordinate(item.Id, item.Version); ValidateHash(item.Hash); ValidateCapabilities(item.RequiredCapabilities);
            if (!seen.Add((item.Id, item.Version)) || item.StageCount is < 1 or > MaximumStages ||
                item.Description is null || item.Description.Length == 0 || Encoding.UTF8.GetByteCount(item.Description) > 1024 ||
                item.Description.Contains('\0'))
                throw new InvalidDataException("The saved workflow index contains an invalid or duplicate summary.");
        }
        return index;
    }

    private WorkflowDataEntry? ReadEntry(string key)
    {
        var raw = store.Get<JsonElement>(RecordKind, key);
        return raw.ValueKind == JsonValueKind.Undefined ? null : Read<WorkflowDataEntry>(raw, MaximumEditionBytes + 2048);
    }

    private static T Read<T>(JsonElement raw, int maximum)
    {
        CheckJson(raw, maximum);
        try { return raw.Deserialize<T>(Json) ?? throw new InvalidDataException("The saved workflow record is null."); }
        catch (JsonException error) { throw new InvalidDataException("The saved workflow record is incompatible.", error); }
    }

    private static void VerifyEntry(WorkflowDataEntry entry, string projectId, string id, string version, string hash)
    {
        if (entry.ProjectId != projectId || entry.Edition is null || entry.Edition.Id != id ||
            entry.Edition.Version != version || entry.Hash != hash)
            throw new InvalidDataException("The saved workflow edition does not match its exact project/ID/version/hash.");
        try
        {
            if (Hash(entry.Edition) != hash) throw new InvalidDataException("The saved workflow content hash does not match.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { throw new InvalidDataException("The saved workflow edition has invalid content.", error); }
    }

    private static WorkflowDataEntry FreezeEntry(WorkflowDataEntry entry) => entry with { Edition = Freeze(entry.Edition) };
    private static WorkflowDataEdition Freeze(WorkflowDataEdition edition)
    {
        Validate(edition);
        var frozen = JsonSerializer.Deserialize<WorkflowDataEdition>(JsonSerializer.SerializeToUtf8Bytes(edition, Json), Json)!;
        return frozen with
        {
            Stages = Array.AsReadOnly(frozen.Stages.ToArray()),
            RequiredCapabilities = Array.AsReadOnly(frozen.RequiredCapabilities.ToArray())
        };
    }

    private static void ValidateCoordinate(string id, string version)
    {
        ValidateIdentifier(id, "workflow");
        if (version is null || version.Length is < 1 or > 64 || !VersionIdentity().IsMatch(version))
            throw new ArgumentException("Workflow version is an exact bounded identity, not a latest/version-range selector.");
    }
    private static void ValidateIdentifier(string value, string kind)
    {
        if (value is null) throw new ArgumentException("Missing " + kind + " identifier.");
        Configuration.ValidateId(value);
    }
    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
            throw new ArgumentException("An exact lowercase SHA-256 workflow hash is required.");
    }
    private static void ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        if (capabilities is null || capabilities.Count > 32 || capabilities.Any(x => x is null ||
            x.Length is < 1 or > 128 || !CapabilityIdentity().IsMatch(x)) ||
            capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count)
            throw new ArgumentException("Workflow capabilities require at most 32 distinct bounded exact names.");
    }
    private static void ValidateToolName(string tool)
    {
        if (tool is null || tool.Length is < 1 or > 256 || !ToolIdentity().IsMatch(tool))
            throw new ArgumentException("Stage tools must be fixed exact bounded names.");
        var segments = tool.ToLowerInvariant().Split(['.', ':', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "model" or "models" or "engine" or "engines" or "install" or "installer" or
            "deploy" or "deployment" or "present" or "generate" or "workflow" or "workflows" or "delegate" or "collaboration") ||
            tool.StartsWith("workflow-data.", StringComparison.Ordinal) ||
            tool.StartsWith("shared.", StringComparison.Ordinal) || tool.StartsWith("shell.", StringComparison.Ordinal) ||
            tool.StartsWith("configuration.", StringComparison.Ordinal) || tool.StartsWith("service-packages.", StringComparison.Ordinal))
            throw new ArgumentException("This first workflow DATA slice excludes model, engine, lifecycle and recursive execution tools.");
    }

    private static void ValidateTemplate(JsonElement template, IReadOnlySet<string> stages)
    {
        CheckJson(template, MaximumEditionBytes);
        Visit(template);
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().ToArray();
                if (properties.Any(x => x.Name.StartsWith('$')))
                {
                    if (properties.Length != 1) throw new ArgumentException("A template reference must be a singleton object.");
                    if (properties[0].Name == "$input" && properties[0].Value.ValueKind == JsonValueKind.String)
                        _ = PointerTokens(properties[0].Value.GetString()!);
                    else if (properties[0].Name == "$stage" && properties[0].Value.ValueKind == JsonValueKind.Object)
                    {
                        var reference = properties[0].Value;
                        if (reference.EnumerateObject().Count() != 2 || !reference.TryGetProperty("id", out var id) ||
                            id.ValueKind != JsonValueKind.String || !reference.TryGetProperty("pointer", out var pointer) ||
                            pointer.ValueKind != JsonValueKind.String || !stages.Contains(id.GetString()!))
                            throw new ArgumentException("A stage reference requires one earlier exact id and pointer.");
                        _ = PointerTokens(pointer.GetString()!);
                    }
                    else throw new ArgumentException("Unsupported reserved-dollar template form.");
                    return;
                }
                foreach (var property in properties) Visit(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    private static bool HasReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(x => x.Name.StartsWith('$') || HasReference(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasReference),
        _ => false
    };
    private static string[] PointerTokens(string pointer)
    {
        if (pointer.Length > 2048 || pointer.Contains('\0')) throw new ArgumentException("Template pointer exceeds its bound.");
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new ArgumentException("Template pointers use RFC6901 strings, not URI fragments.");
        var tokens = pointer[1..].Split('/');
        foreach (var token in tokens)
            for (var i = 0; i < token.Length; i++)
                if (token[i] == '~' && (++i >= token.Length || token[i] is not ('0' or '1')))
                    throw new ArgumentException("Template pointer has an invalid RFC6901 escape.");
        return tokens.Select(x => x.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
    }

    private static JsonElement Select(JsonElement value, string pointer)
    {
        foreach (var token in PointerTokens(pointer))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out var field)) value = field;
            else if (value.ValueKind == JsonValueKind.Array && token.Length > 0 &&
                (token == "0" || token[0] is >= '1' and <= '9') && token.All(char.IsAsciiDigit) &&
                int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength())
                value = value[index];
            else throw new ArgumentException("The exact template pointer does not select existing data.");
        }
        return value;
    }

    private static void WriteResolved(Utf8JsonWriter writer, JsonElement value, JsonElement input,
        IReadOnlyDictionary<string, JsonElement> results)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$input", out var pointer))
            {
                Select(input, pointer.GetString()!).WriteTo(writer); return;
            }
            if (value.TryGetProperty("$stage", out var reference))
            {
                Select(results[reference.GetProperty("id").GetString()!], reference.GetProperty("pointer").GetString()!).WriteTo(writer); return;
            }
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name); WriteResolved(writer, property.Value, input, results);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteResolved(writer, item, input, results); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void CheckJson(JsonElement value, int maximum = MaximumResolvedBytes)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Workflow JSON cannot be undefined.");
        var nodes = 0;
        Visit(value, 0);
        using var stream = new LimitedStream(maximum);
        using (var writer = new Utf8JsonWriter(stream)) { value.WriteTo(writer); writer.Flush(); }
        void Visit(JsonElement element, int depth)
        {
            if (depth > 32 || ++nodes > MaximumNodes) throw new ArgumentException("Workflow JSON exceeds its depth/node bound.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in element.EnumerateObject())
                {
                    if (field.Name.Contains('\0') || !seen.Add(field.Name)) throw new ArgumentException("Workflow JSON has duplicate or invalid fields.");
                    Visit(field.Value, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item, depth + 1);
            else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var number) || !double.IsFinite(number)))
                throw new ArgumentException("Workflow JSON numbers must be finite.");
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(field.Name); WriteCanonical(writer, field.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Number) writer.WriteRawValue(CanonicalNumber(value.GetRawText()), skipInputValidation: false);
        else value.WriteTo(writer);
    }

    // Preserve exact decimal value without rounding through double; 1, 1.0 and 1e0 share identity.
    private static string CanonicalNumber(string raw)
    {
        var negative = raw[0] == '-'; var number = negative ? raw[1..] : raw;
        var e = number.IndexOfAny(['e', 'E']);
        var mantissa = e < 0 ? number : number[..e];
        var exponent = e < 0 ? BigInteger.Zero : BigInteger.Parse(number[(e + 1)..], CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        if (dot >= 0) exponent -= mantissa.Length - dot - 1;
        var trimmed = digits.TrimEnd('0'); exponent += digits.Length - trimmed.Length;
        return (negative ? "-" : "") + trimmed + (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }

    private static string IndexKey(string projectId) => "project-" + CoordinateHash(projectId);
    private static string EditionKey(string projectId, string id, string version) => "edition-" + CoordinateHash(projectId, id, version);
    private static string CoordinateHash(params string[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static string EncodeCursor(string projectId, string id, string version) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { CoordinateHash(projectId), id, version }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static (string Id, string Version)? DecodeCursor(string projectId, string cursor)
    {
        if (cursor.Length is < 1 or > 512 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid workflow page cursor.");
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/'); base64 += new string('=', (4 - base64.Length % 4) % 4);
            var coordinates = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(base64), Json);
            if (coordinates is null || coordinates.Length != 3 || coordinates[0] != CoordinateHash(projectId))
                throw new ArgumentException("Workflow page cursor belongs to another scope.");
            ValidateCoordinate(coordinates[1], coordinates[2]);
            if (EncodeCursor(projectId, coordinates[1], coordinates[2]) != cursor) throw new ArgumentException("Noncanonical workflow page cursor.");
            return (coordinates[1], coordinates[2]);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("Invalid workflow page cursor.", error); }
    }

    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count)
        { if (Length + count > maximum) throw new ArgumentException("Workflow JSON exceeds its bounded byte limit."); }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdentity();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdentity();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolIdentity();
}
