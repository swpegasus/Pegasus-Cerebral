using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

/// <summary>Shared bounded projection. The caller must establish run ownership before invoking it.</summary>
internal static class WorkflowReceiptReader
{
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
        { WriteIndented = false, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    internal static WorkflowReceiptPage Read(WorkflowStore store, WorkflowReceiptRequest input,
        Func<WorkflowCommandResult, WorkflowCommandResult>? projection = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Limit is < 4 or > WorkflowReceiptPage.MaximumPageBytes)
            throw new ArgumentOutOfRangeException(nameof(input), "Receipt page limit must be 4–16384 UTF-8 bytes.");
        if (input.Offset < 0 || input.ExpectedRevision is < 1) throw new ArgumentOutOfRangeException(nameof(input));
        var result = store.GetEffectResult(input.RunId, input.EffectId, out var revision);
        if (input.ExpectedRevision is { } expected && expected != revision)
            throw new WorkflowConflictException("Workflow receipt revision changed. Restart receipt retrieval at offset zero.");
        if (result.State == WorkflowEffectState.Unknown && input.Offset > 0 && input.ExpectedRevision is null)
            throw new ArgumentException("Continue an uncertain receipt using expectedRevision from its first page.");
        // Model-facing callers may omit a proven optional context body. Apply that view before
        // byte paging so offsets and totalBytes describe the returned projection, never the raw body.
        if (projection is not null)
        {
            var projected = projection(result) ?? throw new InvalidDataException("The model receipt projection returned no result.");
            if (projected.State != result.State || projected.ProviderReceiptId != result.ProviderReceiptId ||
                !JsonElement.DeepEquals(CerebralJson.Element(projected.ConversationCheckpoint), CerebralJson.Element(result.ConversationCheckpoint)) ||
                projected.Error?.Length > 8192 || projected.Data is { } data &&
                JsonSerializer.SerializeToUtf8Bytes(data, Json).Length > WorkflowContract.MaximumStateBytes)
                throw new InvalidDataException("The model receipt projection changed outcome authority or exceeded its bound.");
            result = projected;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        if (input.Offset > bytes.Length || input.Offset < bytes.Length && IsContinuation(bytes[input.Offset]))
            throw new ArgumentOutOfRangeException(nameof(input), "Receipt offset must be a UTF-8 character boundary within the result.");
        var end = (int)Math.Min(bytes.Length, (long)input.Offset + input.Limit);
        while (end < bytes.Length && IsContinuation(bytes[end])) end--;
        var more = end < bytes.Length;
        var previewLength = Math.Min(256, result.Error?.Length ?? 0);
        if (result.Error is { } error && previewLength < error.Length && previewLength > 0 && char.IsHighSurrogate(error[previewLength - 1]))
            previewLength--;
        return new(input.RunId, input.EffectId, result.State, result.ProviderReceiptId,
            result.Error?[..previewLength], revision, input.Offset, input.Limit, bytes.Length,
            Encoding.UTF8.GetString(bytes, input.Offset, end - input.Offset), more ? end : null, more);
    }

    private static bool IsContinuation(byte value) => (value & 0xc0) == 0x80;
}
