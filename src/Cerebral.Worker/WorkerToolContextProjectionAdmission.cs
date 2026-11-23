using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;

namespace Cerebral.Worker;

internal sealed record WorkerReceiptProjectionAdmissionData(string Id, string Status, string? Reason,
    ContextProviderDescriptor Provider, WorkflowToolContextProjectionDescriptor ProjectionDescriptor);

internal sealed record WorkerReceiptProjectionAdmissionsData(int SchemaVersion,
    IReadOnlyList<WorkerReceiptProjectionAdmissionData> Items);

/// <summary>A host-issued invocation coordinate, never an admission or authority supplied by a command.</summary>
internal sealed class WorkerToolProjectionContext
{
    internal WorkerToolProjectionContext(WorkerContextContributionHost owner, WorkflowSubmission accepted,
        WorkerContextContributionAdmission admission, WorkflowContextAcceptedFacts facts,
        IWorkflowToolContextProjectionContribution source, WorkflowContextFacts currentFacts, string toolName)
    {
        Owner = owner; Accepted = accepted; Admission = admission; Facts = facts;
        Source = source; CurrentFacts = currentFacts; ToolName = toolName;
    }

    public WorkerContextContributionAdmission Admission { get; }
    public WorkflowContextAcceptedFacts Facts { get; }
    public IWorkflowToolContextProjectionContribution Source { get; }
    public WorkflowContextFacts CurrentFacts { get; }
    internal WorkerContextContributionHost Owner { get; }
    internal WorkflowSubmission Accepted { get; }
    internal string ToolName { get; }
}

/// <summary>Receipt-facet metadata only. Receipt lookup, digesting and own-slot proof remain in the host command.</summary>
internal static class WorkerToolContextProjectionAdmission
{
    internal const string DefinitionId = "pegasus.conversation:chat";
    internal const string DefinitionVersion = "1.0.3-tool-context.1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
        { WriteIndented = false, MaxDepth = 32 };

    internal static bool IsReceipt(WorkflowContextContributionDescriptor descriptor) =>
        descriptor.SchemaVersion == WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion;

    internal static WorkflowToolReceiptContextConsumer FreezeConsumer(WorkflowDefinitionDescriptor definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var declared = definition.ToolReceiptConsumer;
        if (declared is not null && declared.SchemaVersion != WorkflowToolReceiptContextConsumerContract.SchemaVersion)
            throw Invalid("The loaded workflow declares an unsupported receipt consumer schema.");
        return new(WorkflowToolReceiptContextConsumerContract.SchemaVersion, declared?.Supported == true);
    }

    internal static bool SupportsConsumer(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot) =>
        WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)
            ? snapshot.ToolReceiptConsumer is { SchemaVersion: WorkflowToolReceiptContextConsumerContract.SchemaVersion,
                Supported: true }
            : snapshot.SourceSettingsVersion == 5 && accepted.DefinitionId == DefinitionId &&
                accepted.DefinitionVersion == DefinitionVersion;

    internal static void RequireConsumer(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot,
        IReadOnlyList<WorkerContextContributionAdmission> admissions)
    {
        if (!SupportsConsumer(accepted, snapshot) && admissions.Any(admission => IsReceipt(admission.Descriptor) &&
            admission.Selection.Disposition != WorkflowContextDisposition.Omitted))
            throw Invalid("The accepted workflow edition does not support receipt context projections.");
    }

    // Read the optional getter once on the SAME admitted object; callers invoke this outside their gates.
    internal static WorkflowToolContextProjectionDescriptor? CaptureDescriptor(
        WorkflowContextContributionDescriptor descriptor, IWorkflowContextContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var facet = contribution as IWorkflowToolContextProjectionContribution;
        if (IsReceipt(descriptor) != (facet is not null))
            throw Invalid("Receipt projection requires descriptor 2 and its facet on the same contribution.");
        return FreezeDescriptor(descriptor, facet?.ProjectionDescriptor);
    }

    internal static WorkflowToolContextProjectionDescriptor? FreezeDescriptor(
        WorkflowContextContributionDescriptor descriptor, WorkflowToolContextProjectionDescriptor? projection)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SchemaVersion is not (WorkflowContextContributionContract.SchemaVersion or
            WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion))
            throw Invalid("Unsupported context contribution descriptor edition.");
        if (!IsReceipt(descriptor))
        {
            if (projection is not null) throw Invalid("Descriptor 1 cannot carry receipt projection metadata.");
            return null;
        }
        if (projection is null || projection.SchemaVersion != WorkflowToolContextProjectionContract.SchemaVersion ||
            projection.ToolNames is null ||
            projection.ToolNames.Count is < 1 or > WorkflowToolContextProjectionContract.MaximumToolNames ||
            projection.PublicationTemplate is null)
            throw Invalid("Receipt projection descriptor is invalid.");
        var names = projection.ToolNames.Take(WorkflowToolContextProjectionContract.MaximumToolNames + 1).ToArray();
        if (names.Length != projection.ToolNames.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !ToolName(name)))
            throw Invalid("Receipt projection tool names must be bounded, distinct, exact semantic names.");
        var template = projection.PublicationTemplate;
        var references = template.References?.Take(WorkflowToolContextProjectionContract.MaximumSlots + 1).ToArray();
        if (references is null || references.Length != template.References!.Count ||
            template.ContextRevision != 0 || template.ExclusionRevision != 0 ||
            template.MaximumUtf8Bytes is < 1 or > WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes ||
            references.Length != WorkflowToolContextProjectionContract.MaximumSlots ||
            references[0] is null || references[0].ExpectedRevision is not null)
            throw Invalid("Receipt projection requires one fixed publication template without a current revision.");
        var normalized = ContextProviderNormalizer.ReadRequest(descriptor.Provider,
            Element(template with { References = Array.AsReadOnly(references) }));
        var frozen = projection with { ToolNames = Array.AsReadOnly(names), PublicationTemplate = normalized };
        Bound(frozen, WorkflowContextContributionContract.MaximumCollectionUtf8Bytes);
        return frozen;
    }

    internal static WorkerReceiptProjectionAdmissionsData View(
        IReadOnlyList<WorkerContextContributionAdmission> admissions)
    {
        var items = admissions.Where(admission => IsReceipt(admission.Descriptor)).Select(admission =>
        {
            var projection = admission.ProjectionDescriptor
                ?? throw Invalid("Receipt projection admission has no frozen descriptor.");
            var reason = admission.Selection.Reason;
            if (reason is { Length: > WorkflowToolContextProjectionContract.MaximumReasonCharacters } ||
                admission.Selection.Disposition == WorkflowContextDisposition.Ready && reason is not null)
                throw Invalid("Receipt projection admission reason is invalid.");
            return new WorkerReceiptProjectionAdmissionData(admission.Descriptor.Id,
                JsonNamingPolicy.CamelCase.ConvertName(admission.Selection.Disposition.ToString()),
                reason, admission.Descriptor.Provider, projection);
        }).ToArray();
        var view = new WorkerReceiptProjectionAdmissionsData(WorkflowToolContextProjectionContract.SchemaVersion,
            Array.AsReadOnly(items));
        Bound(view, WorkerWorkflowEffects.MaximumContextContributionProjectionBytes);
        return view;
    }

    internal static bool ToolName(string? name)
    {
        // The dispatcher can expose non-service/MCP names. Keep exact ordinal identity without
        // inventing an ASCII namespace restriction; wildcard and whitespace filters are not names.
        if (name is not { Length: > 0 and <= 256 } ||
            name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '*' or '?'))
            return false;
        try { _ = Utf8.GetByteCount(name); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static WorkflowToolContextProjectionResolution FreezeResolution(WorkflowToolContextProjectionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resolution, Json);
        if (bytes.Length > WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes)
            throw Invalid("Receipt projection callback result exceeds its bound.");
        return JsonSerializer.Deserialize<WorkflowToolContextProjectionResolution>(bytes, Json)
            ?? throw Invalid("Receipt projection callback result is absent.");
    }

    internal static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    internal static void Bound<T>(T value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximum) throw Invalid("Receipt projection metadata exceeds its bound.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
