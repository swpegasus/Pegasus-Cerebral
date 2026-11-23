using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

// Owner-authored approval data is distinct from a package's self-declared portable schema.
public sealed record WorkflowPortableSettingsApprovalRequest(
    string PluginId, string PackageVersion, string LoadedGeneration,
    string PackageFingerprint, string SchemaFingerprint,
    IReadOnlyList<string> ReviewedFields, long ExpectedRevision, string RequestId);

public sealed record WorkflowPortableSettingsApproval(
    int SchemaVersion, string PluginId, string PluginVersion, string LoadedGeneration,
    string PackageFingerprint, string SchemaFingerprint,
    IReadOnlyList<string> ReviewedFields, long Revision, string RequestId);

public sealed record WorkflowPortableSettingsApprovalResult(
    WorkflowPortableSettingsApproval Approval, bool Replayed, bool CurrentlyActive);

internal sealed record WorkflowPortableSettingsApprovalReceipt(
    int SchemaVersion, string RequestId, string RequestDigest,
    WorkflowPortableSettingsApproval AppliedApproval);

internal static class WorkflowPortableSettingsApprovals
{
    internal const string ApprovalKind = "service-workflow-settings-approval";
    internal const string ReceiptKind = "service-workflow-settings-approval-receipt";
    internal const int SchemaVersion = 1;

    internal static string Digest(WorkflowPortableSettingsApprovalRequest request, IReadOnlyList<string> sortedFields)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = SchemaVersion,
            pluginId = request.PluginId,
            pluginVersion = request.PackageVersion,
            loadedGeneration = request.LoadedGeneration,
            packageFingerprint = request.PackageFingerprint,
            schemaFingerprint = request.SchemaFingerprint,
            reviewedFields = sortedFields,
            expectedRevision = request.ExpectedRevision
        });
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    internal static string SchemaFingerprint(ServicePluginPortableSettingsBinding schema)
    {
        var fields = schema.Fields.OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => new
            {
                field.Name, field.Type, field.Required, field.MaximumUtf8Bytes, field.MaximumItems,
                field.Minimum, field.Maximum,
                AllowedValues = field.AllowedValues.Order(StringComparer.Ordinal).ToArray()
            }).ToArray();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fingerprintFormatVersion = 1, schema.PluginId, schema.PackageVersion, schema.Version, Fields = fields
        });
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    internal static IReadOnlyList<string> ValidateAndSortFields(IReadOnlyList<string>? fields,
        ServicePluginPortableSettingsBinding schema)
    {
        if (fields is null || fields.Count is < 1 or > ServicePluginContract.MaxPortableSettings)
            throw new ArgumentException("Reviewed portable setting fields must be a bounded nonempty subset.");
        var declared = schema.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
            if (string.IsNullOrWhiteSpace(field) || !declared.Contains(field) || !selected.Add(field))
                throw new ArgumentException("Reviewed portable setting fields must be unique names in the loaded schema.");
        return Array.AsReadOnly(selected.Order(StringComparer.Ordinal).ToArray());
    }

    internal static void ValidateIdentity(WorkflowPortableSettingsApprovalRequest request)
    {
        ServicePluginRuntime.ValidatePackageId(request.PluginId);
        if (!PluginVersion.TryParse(request.PackageVersion, out _) ||
            string.IsNullOrWhiteSpace(request.LoadedGeneration) || request.LoadedGeneration.Length > 128 ||
            request.LoadedGeneration.Any(char.IsControl) ||
            !HexDigest(request.PackageFingerprint) || !HexDigest(request.SchemaFingerprint) ||
            request.ExpectedRevision < 0 || !ValidRequestId(request.RequestId))
            throw new ArgumentException("Workflow portable settings approval has invalid identity or revision.");
    }

    internal static void ValidateStored(WorkflowPortableSettingsApproval approval)
    {
        if (approval.SchemaVersion != SchemaVersion || approval.Revision < 1 ||
            approval.ReviewedFields is null || approval.ReviewedFields.Count is < 1 or > ServicePluginContract.MaxPortableSettings)
            throw new InvalidDataException("Stored workflow settings approval is invalid.");
        ValidateIdentity(new(approval.PluginId, approval.PluginVersion, approval.LoadedGeneration,
            approval.PackageFingerprint, approval.SchemaFingerprint, approval.ReviewedFields,
            approval.Revision - 1, approval.RequestId));
        if (approval.ReviewedFields.Distinct(StringComparer.Ordinal).Count() != approval.ReviewedFields.Count ||
            !approval.ReviewedFields.SequenceEqual(approval.ReviewedFields.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Stored workflow settings approval fields are invalid.");
    }

    internal static void ValidateReceipt(WorkflowPortableSettingsApprovalReceipt receipt)
    {
        if (receipt.SchemaVersion != SchemaVersion || !ValidRequestId(receipt.RequestId) ||
            !HexDigest(receipt.RequestDigest) || receipt.AppliedApproval is null ||
            receipt.RequestId != receipt.AppliedApproval.RequestId)
            throw new InvalidDataException("Stored workflow settings approval receipt is invalid.");
        ValidateStored(receipt.AppliedApproval);
        var applied = receipt.AppliedApproval;
        var source = new WorkflowPortableSettingsApprovalRequest(applied.PluginId, applied.PluginVersion,
            applied.LoadedGeneration, applied.PackageFingerprint, applied.SchemaFingerprint,
            applied.ReviewedFields, applied.Revision - 1, receipt.RequestId);
        if (receipt.RequestDigest != Digest(source, applied.ReviewedFields))
            throw new InvalidDataException("Stored workflow settings approval receipt does not match its applied record.");
    }

    internal static bool HexDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidRequestId(string? value) => value is { Length: > 0 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= 256;
}
