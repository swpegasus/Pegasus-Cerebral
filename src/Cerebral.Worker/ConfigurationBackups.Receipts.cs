using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ConfigurationBackups
{
    private const string ReceiptKind = "configuration.restore.receipt";
    private const string ReceiptIndexKind = "configuration.restore.receipt-index";
    public const int MaximumRetainedReceipts = 128;
    public const int ReceiptRetentionDays = 30;
    private const int MaximumReceiptBytes = 256 * 1024;
    private sealed record ReceiptIndex(IReadOnlyList<ReceiptCoordinate> Entries);
    private sealed record ReceiptCoordinate(string PreviewId, DateTimeOffset AppliedAt);

    public ConfigurationRestoreStatus Status(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<ConfigurationRestoreStatusRequest>(input); ValidatePreviewId(request.PreviewId);
        var receipt = store.Get<ConfigurationRestoreReceipt>(ReceiptKind, request.PreviewId);
        return new(request.PreviewId, receipt is null ? "unknown" : "committed", receipt,
            MaximumRetainedReceipts, ReceiptRetentionDays);
    }

    /// <summary>Read-only exact-retry admission before loaded-model and file-operation gates.</summary>
    public ConfigurationRestoreResult? CommittedResult(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<ConfigurationRestoreApplyRequest>(input); ValidateApply(request);
        return MatchingReceipt(request)?.Result;
    }

    private static void ValidatePreviewId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("Restore preview ID must be an exact nonempty GUID in N format.");
    }
    private static void ValidateApply(ConfigurationRestoreApplyRequest request)
    {
        ValidatePreviewId(request.PreviewId);
        if (request.ExpectedHostRevision < 0) throw new ArgumentException("Expected host revision cannot be negative.");
        if (!request.ReviewedFreeText) throw new ArgumentException("Review the backup's free text and ordinary grants before approving this restore.");
    }
    private ConfigurationRestoreReceipt? MatchingReceipt(ConfigurationRestoreApplyRequest request)
    {
        var receipt = store.Get<ConfigurationRestoreReceipt>(ReceiptKind, request.PreviewId);
        if (receipt is not null && (receipt.PreviewId != request.PreviewId ||
            receipt.ExpectedHostRevision != request.ExpectedHostRevision || receipt.ReviewedFreeText != request.ReviewedFreeText))
            throw new InvalidOperationException("The retained restore ID was applied with different request fields.");
        return receipt;
    }
    private void SaveReceipt(ConfigurationRestoreReceipt receipt)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(receipt, Json).Length > MaximumReceiptBytes)
            throw new InvalidOperationException("The compact restore receipt exceeds its supported size. No configuration changes were committed.");
        var index = store.Get<ReceiptIndex>(ReceiptIndexKind, "recent") ?? new([]);
        if (index.Entries.Count > MaximumRetainedReceipts)
            throw new InvalidOperationException("Restore receipt index exceeds its bounded retention contract.");
        var entries = index.Entries.Where(x => x.PreviewId != receipt.PreviewId).Append(new(receipt.PreviewId, receipt.AppliedAt))
            .OrderByDescending(x => x.AppliedAt).ThenByDescending(x => x.PreviewId, StringComparer.Ordinal).ToArray();
        var cutoff = time.GetUtcNow().AddDays(-ReceiptRetentionDays);
        var kept = entries.Where(x => x.PreviewId == receipt.PreviewId || x.AppliedAt >= cutoff).Take(MaximumRetainedReceipts).ToList();
        // Always retain the operation currently being acknowledged, including after a clock adjustment.
        if (kept.All(x => x.PreviewId != receipt.PreviewId))
        { if (kept.Count == MaximumRetainedReceipts) kept.RemoveAt(kept.Count - 1); kept.Add(new(receipt.PreviewId, receipt.AppliedAt)); }
        var ids = kept.Select(x => x.PreviewId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in entries.Where(x => !ids.Contains(x.PreviewId))) store.Delete(ReceiptKind, stale.PreviewId);
        store.Put(ReceiptKind, receipt.PreviewId, receipt);
        store.Put(ReceiptIndexKind, "recent", new ReceiptIndex(kept));
    }
    private void PublishRestoreNotification(string previewId)
    {
        var notification = "not-requested";
        if (onApplied is not null)
        {
            try { onApplied(); notification = "delivered"; }
            catch (Exception) { notification = "failed"; }
        }
        try
        {
            store.Locked(() =>
            {
                if (store.Get<ConfigurationRestoreReceipt>(ReceiptKind, previewId) is { } receipt)
                    store.Put(ReceiptKind, previewId, receipt with { NotificationOutcome = notification });
                return true;
            });
        }
        catch (Exception)
        {
            // The committed receipt already says "pending". Status therefore continues to expose
            // an unconfirmed notification outcome without losing or reclassifying the settings commit.
        }
    }
}
