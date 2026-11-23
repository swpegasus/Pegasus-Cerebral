namespace Cerebral.Contracts.Workflows;

public sealed record WorkflowReceiptRequest(string RunId, string EffectId, int Offset = 0, int Limit = 4096,
    long? ExpectedRevision = null);

/// <summary>
/// Json is a fragment of the compact result envelope. Concatenate decoded fragments before parsing JSON.
/// Offset/Limit/TotalBytes/NextOffset count UTF-8 bytes in that envelope, excluding transport metadata/escaping.
/// Continue an Unknown result with its RunRevision as ExpectedRevision; restart at zero if it changes.
/// Confirmed results are immutable and need no revision pin.
/// </summary>
public sealed record WorkflowReceiptPage(string RunId, string EffectId, WorkflowEffectState State,
    string? ProviderReceiptId, string? ErrorPreview, long RunRevision, int Offset, int Limit, int TotalBytes,
    string Json, int? NextOffset, bool Truncated)
{
    public const int MaximumPageBytes = 16 * 1024;
}
