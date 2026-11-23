using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;

namespace Cerebral.Worker;

internal sealed record WorkerContextContributionAdmission(int SchemaVersion, string PackageId,
    string PackageVersion, string Generation, WorkflowContextContributionDescriptor Descriptor,
    WorkflowContextSelection Selection)
{
    // Absence is omitted so descriptor-1 admissions retain their exact six-field wire shape.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowToolContextProjectionDescriptor? ProjectionDescriptor { get; init; }
}

/// <summary>Freezes the host-owned envelope. Payload meaning and its digest remain owned by the package codec.</summary>
internal static class WorkerContextContributionSnapshot
{
    private const string Refusal = "Workflow context contribution snapshot is invalid.";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = false, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private static readonly HashSet<string> ReservedLegacyFields =
        typeof(WorkerExecutionSnapshot.Snapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Concat(["resolvedSettingsVersion", "contextContributions", "schemaVersion"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static WorkerContextContributionAdmission Freeze(ServicePluginPackageBinding binding,
        WorkflowContextContributionDescriptor descriptor, WorkflowContextSelection selection,
        WorkflowToolContextProjectionDescriptor? projectionDescriptor = null)
    {
        try
        {
            var frozenDescriptor = FreezeDescriptor(binding, descriptor);
            var frozenSelection = FreezeSelection(frozenDescriptor, selection);
            var frozen = new WorkerContextContributionAdmission(WorkflowContextContributionContract.SchemaVersion,
                binding.Id, binding.Version, binding.Generation!, frozenDescriptor, frozenSelection)
            {
                ProjectionDescriptor = WorkerToolContextProjectionAdmission.FreezeDescriptor(frozenDescriptor, projectionDescriptor)
            };
            BoundSerialized(frozen, WorkflowContextContributionContract.MaximumCollectionUtf8Bytes);
            return frozen;
        }
        catch (Exception error) when (BoundaryFailure(error)) { throw Invalid(); }
    }

    internal static WorkflowContextContributionDescriptor FreezeDescriptor(ServicePluginPackageBinding binding,
        WorkflowContextContributionDescriptor descriptor)
    {
        try
        {
            ValidateBinding(binding);
            ArgumentNullException.ThrowIfNull(descriptor);
            if (descriptor.SchemaVersion is not (WorkflowContextContributionContract.SchemaVersion or
                    WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion) ||
                !Enum.IsDefined(descriptor.HistoryProjection)) throw Invalid();
            OwnedId(descriptor.Id, binding.Id);
            var provider = FreezeProvider(descriptor.Provider, binding.Id);
            if (descriptor.LegacyFormats is null || descriptor.LegacyFormats.Count > WorkflowContextContributionContract.MaximumLegacyFormats)
                throw Invalid();
            var versions = new HashSet<int>();
            var legacy = descriptor.LegacyFormats.Select(format =>
            {
                if (format is null || format.SnapshotVersion < 1 || !versions.Add(format.SnapshotVersion) ||
                    format.Fields is null || format.Fields.Count is < 1 or > WorkflowContextContributionContract.MaximumLegacyFields)
                    throw Invalid();
                var fields = format.Fields.ToArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in fields)
                {
                    Token(field, 128);
                    if (!seen.Add(field)) throw Invalid();
                }
                return format with { Fields = Array.AsReadOnly(fields) };
            }).ToArray();
            var frozen = descriptor with { Provider = provider, LegacyFormats = Array.AsReadOnly(legacy) };
            BoundSerialized(frozen, WorkflowContextContributionContract.MaximumCollectionUtf8Bytes);
            return frozen;
        }
        catch (Exception error) when (BoundaryFailure(error)) { throw Invalid(); }
    }

    // Only the host's edition-specific legacy dispatcher invokes this guard before handing over exact slots.
    internal static bool IsReservedLegacyField(string field) => ReservedLegacyFields.Contains(field);

    internal static IReadOnlyList<WorkerContextContributionAdmission> ValidateCollection(
        IReadOnlyList<WorkerContextContributionAdmission> collection)
    {
        try
        {
            if (collection is null || collection.Count > WorkflowContextContributionContract.MaximumContributions)
                throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var packages = new Dictionary<string, (string Version, string Generation)>(StringComparer.Ordinal);
            var providers = new Dictionary<string, ContextProviderDescriptor>(StringComparer.Ordinal);
            var templates = new List<JsonElement>();
            var frozen = collection.Select(admission =>
            {
                if (admission is null || admission.SchemaVersion != WorkflowContextContributionContract.SchemaVersion)
                    throw Invalid();
                var binding = new ServicePluginPackageBinding(admission.PackageId, admission.PackageVersion,
                    true, [], Generation: admission.Generation);
                var item = Freeze(binding, admission.Descriptor, admission.Selection, admission.ProjectionDescriptor);
                if (!ids.Add(item.Descriptor.Id)) throw Invalid();
                if (packages.TryGetValue(item.PackageId, out var identity) &&
                    identity != (item.PackageVersion, item.Generation)) throw Invalid();
                packages[item.PackageId] = (item.PackageVersion, item.Generation);
                if (providers.TryGetValue(item.Descriptor.Provider.Id, out var prior) &&
                    !SameProvider(prior, item.Descriptor.Provider)) throw Invalid();
                providers[item.Descriptor.Provider.Id] = item.Descriptor.Provider;
                if (item.Selection.Disposition == WorkflowContextDisposition.Ready)
                {
                    var template = Element(item.Selection.Request!);
                    if (templates.Any(priorTemplate => JsonElement.DeepEquals(priorTemplate, template))) throw Invalid();
                    templates.Add(template);
                }
                return item;
            }).ToArray();
            // Descriptor 2 owns one fixed publication slot. Its slot cannot collide with another
            // receipt facet or an actually scheduled v1 template; v1-only history keeps its old rules.
            var receiptSlots = frozen.Where(item => item.ProjectionDescriptor is not null)
                .Select(item => item.ProjectionDescriptor!.PublicationTemplate.References.Single().Id).ToArray();
            if (receiptSlots.Distinct(StringComparer.Ordinal).Count() != receiptSlots.Length ||
                frozen.Where(item => item.Descriptor.SchemaVersion == WorkflowContextContributionContract.SchemaVersion &&
                    item.Selection.Disposition == WorkflowContextDisposition.Ready)
                    .SelectMany(item => item.Selection.Request!.References)
                    .Any(reference => receiptSlots.Contains(reference.Id, StringComparer.Ordinal)))
                throw Invalid();
            BoundSerialized(frozen, WorkflowContextContributionContract.MaximumCollectionUtf8Bytes);
            return Array.AsReadOnly(frozen);
        }
        catch (Exception error) when (BoundaryFailure(error)) { throw Invalid(); }
    }

    // Callers select the contribution array from their edition-specific host snapshot.
    internal static IReadOnlyList<WorkerContextContributionAdmission> ReadCollection(JsonElement collection)
    {
        try
        {
            if (collection.ValueKind != JsonValueKind.Array ||
                collection.GetArrayLength() > WorkflowContextContributionContract.MaximumContributions ||
                Utf8.GetByteCount(collection.GetRawText()) > WorkflowContract.MaximumStateBytes)
                throw Invalid();
            BoundSerialized(collection, WorkflowContextContributionContract.MaximumCollectionUtf8Bytes);
            foreach (var admission in collection.EnumerateArray()) ClosedAdmission(admission);
            var parsed = collection.Deserialize<WorkerContextContributionAdmission[]>(Json) ?? throw Invalid();
            return ValidateCollection(parsed);
        }
        catch (Exception error) when (BoundaryFailure(error)) { throw Invalid(); }
    }

    internal static IReadOnlyList<string> ProviderIds(IReadOnlyList<WorkerContextContributionAdmission> collection) =>
        Array.AsReadOnly(ValidateCollection(collection)
            .Where(admission => admission.Selection.Disposition == WorkflowContextDisposition.Ready)
            .Select(admission => admission.Descriptor.Provider.Id).Distinct(StringComparer.Ordinal).ToArray());

    internal static ContextProviderRequest Template(WorkerContextContributionAdmission admission,
        long contextRevision, long exclusionRevision)
    {
        try
        {
            var item = ValidateCollection([admission]).Single();
            if (contextRevision < 0 || exclusionRevision < 0 ||
                item.Selection.Disposition != WorkflowContextDisposition.Ready || item.Selection.Request is null)
                throw Invalid();
            // No provider/reference/body field is substituted during restoration.
            var request = item.Selection.Request with
                { ContextRevision = contextRevision, ExclusionRevision = exclusionRevision };
            return ContextProviderNormalizer.ReadRequest(item.Descriptor.Provider, Element(request));
        }
        catch (Exception error) when (BoundaryFailure(error)) { throw Invalid(); }
    }

    private static WorkflowContextSelection FreezeSelection(WorkflowContextContributionDescriptor descriptor,
        WorkflowContextSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Disposition)) throw Invalid();
        if (selection.Disposition != WorkflowContextDisposition.Ready)
        {
            if (selection.Payload is not null || selection.PayloadDigest is not null || selection.Request is not null)
                throw Invalid();
            if (selection.Reason is { } reason)
            {
                if (WorkerToolContextProjectionAdmission.IsReceipt(descriptor))
                    Text(reason, WorkflowToolContextProjectionContract.MaximumReasonCharacters);
                else if (reason.Length is < 1 or > 64 ||
                    !Regex.IsMatch(reason, "^[a-z0-9]+(?:[-.][a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    throw Invalid();
            }
            return selection with { Payload = null, Request = null };
        }
        if (selection.Reason is not null || selection.Request is null ||
            (selection.Payload is null) != (selection.PayloadDigest is null))
            throw Invalid();
        if (selection.Payload is { } payload)
        {
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw Invalid();
            Token(selection.PayloadDigest!, 256);
            // Do not traverse, normalize or recompute this package-private codec value/digest pair.
            if (Utf8.GetByteCount(payload.GetRawText()) > WorkflowContract.MaximumStateBytes) throw Invalid();
            BoundSerialized(payload, WorkflowContextContributionContract.MaximumPayloadUtf8Bytes);
        }
        if (selection.Request.ContextRevision != 0 || selection.Request.ExclusionRevision != 0) throw Invalid();
        var requestJson = Element(selection.Request);
        ClosedRequest(requestJson);
        var request = ContextProviderNormalizer.ReadRequest(descriptor.Provider, requestJson);
        return selection with { Payload = selection.Payload?.Clone(), Request = request };
    }

    private static void ValidateBinding(ServicePluginPackageBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ServicePluginRuntime.ValidatePackageId(binding.Id);
        if (!binding.Loaded || binding.Error is not null || binding.Version is null || binding.Version.Length > 128 ||
            !PluginVersion.TryParse(binding.Version, out _) || binding.Generation is null ||
            binding.Generation.Length is < 1 or > 160 || binding.Generation is "." or ".." ||
            binding.Generation.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            binding.Dependencies is null || binding.Dependencies.Count > ServicePluginContract.MaxDependencies ||
            binding.Dependencies.Any(dependency => dependency is null || !dependency.Optional && !dependency.Available))
            throw Invalid();
    }

    private static ContextProviderDescriptor FreezeProvider(ContextProviderDescriptor provider, string owner)
    {
        ArgumentNullException.ThrowIfNull(provider);
        OwnedId(provider.Id, owner);
        if (provider.Version < 1 || provider.MaxReferences is < 1 or > ContextProviderContract.MaximumReferences ||
            provider.MaxUtf8Bytes is < 1 or > ContextProviderContract.MaximumUtf8Bytes ||
            provider.SupportedReferenceKinds is null ||
            provider.SupportedReferenceKinds.Count is < 1 or > ContextProviderContract.MaximumReferenceKinds)
            throw Invalid();
        Text(provider.Description, 4096);
        var kinds = provider.SupportedReferenceKinds.ToArray();
        if (kinds.Any(kind => kind is null || kind.Length > 64 ||
                !Regex.IsMatch(kind, "^[a-z][a-z0-9]*(?:[./:_+-][a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) ||
            kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length) throw Invalid();
        return provider with { SupportedReferenceKinds = Array.AsReadOnly(kinds) };
    }

    private static void OwnedId(string id, string owner)
    {
        if (id is null || id.Length > 256 || !id.StartsWith(owner + ":", StringComparison.Ordinal) ||
            !Regex.IsMatch(id[(owner.Length + 1)..], "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw Invalid();
    }

    private static bool SameProvider(ContextProviderDescriptor left, ContextProviderDescriptor right) =>
        left.Id == right.Id && left.Version == right.Version && left.Description == right.Description &&
        left.MaxReferences == right.MaxReferences && left.MaxUtf8Bytes == right.MaxUtf8Bytes &&
        left.SupportedReferenceKinds.SequenceEqual(right.SupportedReferenceKinds, StringComparer.Ordinal);

    private static void ClosedAdmission(JsonElement admission)
    {
        if (admission.ValueKind != JsonValueKind.Object ||
            !admission.TryGetProperty("descriptor", out var descriptor) ||
            descriptor.ValueKind != JsonValueKind.Object ||
            !descriptor.TryGetProperty("schemaVersion", out var descriptorVersion) ||
            !descriptorVersion.TryGetInt32(out var edition)) throw Invalid();
        if (edition == WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion)
        {
            Fields(admission, ["schemaVersion", "packageId", "packageVersion", "generation", "descriptor", "selection", "projectionDescriptor"]);
            var projection = admission.GetProperty("projectionDescriptor");
            Fields(projection, ["schemaVersion", "toolNames", "publicationTemplate"]);
            ClosedRequest(projection.GetProperty("publicationTemplate"));
        }
        else Fields(admission, ["schemaVersion", "packageId", "packageVersion", "generation", "descriptor", "selection"]);
        Fields(descriptor, ["schemaVersion", "id", "provider", "historyProjection", "legacyFormats"]);
        EnumName<WorkflowContextHistoryProjection>(descriptor.GetProperty("historyProjection"));
        var provider = descriptor.GetProperty("provider");
        Fields(provider, ["id", "version", "description", "supportedReferenceKinds", "maxReferences", "maxUtf8Bytes"]);
        var legacyFormats = descriptor.GetProperty("legacyFormats");
        if (legacyFormats.ValueKind != JsonValueKind.Array || legacyFormats.GetArrayLength() > WorkflowContextContributionContract.MaximumLegacyFormats)
            throw Invalid();
        foreach (var legacy in legacyFormats.EnumerateArray()) Fields(legacy, ["snapshotVersion", "fields"]);
        var selection = admission.GetProperty("selection");
        Fields(selection, ["disposition", "reason", "payload", "payloadDigest", "request"]);
        EnumName<WorkflowContextDisposition>(selection.GetProperty("disposition"));
        var request = selection.GetProperty("request");
        if (request.ValueKind != JsonValueKind.Null) ClosedRequest(request);
    }

    private static void ClosedRequest(JsonElement request)
    {
        Fields(request, ["providerId", "providerVersion", "references", "maximumUtf8Bytes", "contextRevision", "exclusionRevision"]);
        var references = request.GetProperty("references");
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > ContextProviderContract.MaximumReferences)
            throw Invalid();
        foreach (var reference in references.EnumerateArray())
        {
            Fields(reference, ["id", "kind", "sourceId", "expectedRevision", "requirement", "priority", "beforeCurrentInput"]);
            EnumName<ContextRequirement>(reference.GetProperty("requirement"));
        }
    }

    private static void Fields(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        if (names.Count != expected.Count) throw Invalid();
    }

    private static void EnumName<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String || !Enum.GetValues<T>().Any(item =>
                JsonNamingPolicy.CamelCase.ConvertName(item.ToString()) == value.GetString()))
            throw Invalid();
    }

    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0')) throw Invalid();
        _ = Utf8.GetByteCount(value);
    }

    private static void Token(string value, int maximum)
    {
        Text(value, maximum);
        if (value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) throw Invalid();
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static void BoundSerialized<T>(T value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximum) throw Invalid();
        using var bounded = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static bool BoundaryFailure(Exception error) => error is ArgumentException or InvalidDataException or
        InvalidOperationException or JsonException or KeyNotFoundException or FormatException or OverflowException or
        EncoderFallbackException or RegexMatchTimeoutException;

    private static InvalidDataException Invalid() => new(Refusal);
}
