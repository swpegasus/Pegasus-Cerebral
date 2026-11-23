using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

internal sealed record DurableInferenceReadRequest(string Id, string Projection, string ExpectedRequestId,
    string ExpectedConversationId, string? ExpectedProjectId, long? AfterSequence, long? SnapshotSequence, int Limit);
internal sealed record DurableInferenceReadReply(string Schema, DurableInferenceReadRequest Request,
    long SnapshotSequence, long ScannedThroughSequence, long? JournalNextAfterSequence, bool JournalEndReached,
    bool SelectedRetentionGap, int PagesRead, int EventsRead, long SerializedEventBytes,
    int MaximumPages, long MaximumJournalPayloadBytes, int MaximumReplyUtf8Bytes, bool ByteLimited,
    int OmittedEffects, IReadOnlyList<string> Reasons, DurableInferenceProjection Projection)
{
    public string Mode => DurableInferenceOwnerRead.Selector;
    public bool Complete => JournalEndReached && !ByteLimited && OmittedEffects == 0 && Projection.Complete;
    public bool DisplayNormalizationApplied => false;
    public bool RunAndJournalSnapshotAtomic => false;
    public string CursorSemantics => "JournalNextAfterSequence is a journal cursor, not a metrics-row cursor. A later-start window may lack earlier effect intents; coverage and omissions remain explicit. Byte/row omissions are not recovered by blindly advancing this cursor.";
}

/// <summary>Owner-only, body-free opt-in on the existing generic events read; no effects or configuration normalization.</summary>
internal static class DurableInferenceOwnerRead
{
    internal const string Selector = "durable-inference-v1";
    internal const int MaximumPages = 4;
    internal const int MaximumReplyUtf8Bytes = 128 * 1024;
    internal const long MaximumJournalPayloadBytes = (long)MaximumPages * EventJournal.MaximumPageBytes;
    internal delegate JournalPage ReadJournalPage(string conversationId, long afterSequence, int limit,
        long? snapshotSequence, CancellationToken cancellationToken);

    internal static bool IsSelected(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw InvalidRequest();
        var found = false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!property.Name.Equals("projection", StringComparison.OrdinalIgnoreCase)) continue;
            if (found || property.Name != "projection" || property.Value.ValueKind != JsonValueKind.String ||
                property.Value.GetString() != Selector) throw InvalidRequest();
            found = true;
        }
        return found;
    }

    internal static DurableInferenceReadReply Read(JsonElement arguments, bool installationOwner,
        Func<string, WorkflowRun> getOwned, ReadJournalPage readPage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(getOwned);
        ArgumentNullException.ThrowIfNull(readPage);
        if (!IsSelected(arguments)) throw InvalidRequest();
        if (!installationOwner) throw new UnauthorizedAccessException("Durable inference view requires the installation owner.");
        var request = Parse(arguments);
        // The supplied production delegate is exactly host.GetOwned(id, actor). Scope checks occur
        // after canonical admission and before any journal read; request labels grant no access.
        var run = getOwned(request.Id);
        cancellationToken.ThrowIfCancellationRequested();
        if (run.Id != request.Id || run.Submission.RequestId != request.ExpectedRequestId ||
            run.Submission.ConversationId != request.ExpectedConversationId ||
            run.Submission.ProjectId != request.ExpectedProjectId)
            throw new ArgumentException("Durable inference expectations do not match the frozen run.");
        var anchor = run.Submission.ConversationSequence;
        var after = request.AfterSequence ?? anchor;
        if (anchor < 0 || after < anchor || request.SnapshotSequence is { } supplied && supplied < after)
            throw InvalidRequest();

        var items = new List<CerebralEvent>();
        var reasons = new List<string>();
        long cursor = after, bytes = 0;
        long? snapshot = request.SnapshotSequence, next = null;
        var pages = 0; var end = false; var retention = false; var byteLimited = false;
        while (pages < MaximumPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = readPage(run.Submission.ConversationId, cursor, request.Limit, snapshot, cancellationToken);
            pages++;
            if (page.SnapshotSequence < cursor || snapshot is { } pin && page.SnapshotSequence != pin ||
                page.Events.Count > request.Limit)
                throw InvalidPage();
            snapshot ??= page.SnapshotSequence;
            if (page.RetentionGap is { } gap && gap.LastPrunedSequence > after &&
                gap.FirstPrunedSequence <= snapshot.Value) retention = true;
            var last = cursor;
            foreach (var item in page.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null || item.ConversationId != run.Submission.ConversationId ||
                    item.Sequence <= last || item.Sequence > snapshot.Value) throw InvalidPage();
                var encoded = JsonSerializer.SerializeToUtf8Bytes(item, CerebralJson.Options);
                var length = encoded.Length;
                Array.Clear(encoded);
                if (length > CerebralEventContract.MaximumSerializedUtf8Bytes ||
                    bytes + length > MaximumJournalPayloadBytes ||
                    items.Count == ModelRuntimeDurableInferenceMetrics.MaximumInputEvents)
                {
                    byteLimited = true;
                    break;
                }
                bytes += length; items.Add(item); last = item.Sequence;
            }
            if (byteLimited) { cursor = last; next = cursor; reasons.Add("input-byte-or-event-bound"); break; }
            if (page.NextAfterSequence is { } following &&
                (page.Events.Count == 0 || following != last || following <= cursor || following > snapshot.Value))
                throw InvalidPage();
            cursor = last; next = page.NextAfterSequence;
            if (next is null) { end = true; break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!end && !byteLimited) reasons.Add("journal-page-bound");
        if (retention) reasons.Add("selected-retention-gap");
        if (after != anchor) reasons.Add("window-starts-after-run-anchor");
        var scope = new DurableInferenceScope(run.Id, run.Submission.RequestId,
            run.Submission.ConversationId, run.Submission.ProjectId);
        var projection = ModelRuntimeDurableInferenceMetrics.Project(scope, run, items,
            new(after, snapshot ?? after, end && !byteLimited, retention), cancellationToken);
        if (!projection.Complete) reasons.Add("projection-incomplete");
        if (projection.OmittedMeasurements != 0) reasons.Add("projection-measurement-bound");
        var reply = new DurableInferenceReadReply("cerebral.durable-inference-owner-view/1", request,
            snapshot ?? after, cursor, next, end, retention, pages, items.Count, bytes,
            MaximumPages, MaximumJournalPayloadBytes, MaximumReplyUtf8Bytes, byteLimited, 0,
            reasons.AsReadOnly(), projection);
        return BoundReply(reply, cancellationToken);
    }

    private static DurableInferenceReadReply BoundReply(DurableInferenceReadReply original, CancellationToken token)
    {
        if (Fits(original)) return original;
        // Retain a prefix of whole effects, including all of each effect's returned usage rows.
        // This bounds actual serialized UTF-8, not a character estimate; no raw body is returned.
        var low = 0; var high = original.Projection.Effects.Count; DurableInferenceReadReply? best = null;
        while (low <= high)
        {
            token.ThrowIfCancellationRequested();
            var count = low + (high - low) / 2;
            var effects = original.Projection.Effects.Take(count).ToArray();
            var ids = effects.Select(effect => effect.EffectId).ToHashSet(StringComparer.Ordinal);
            var measurements = original.Projection.Measurements.Where(row => ids.Contains(row.EffectId)).ToArray();
            var candidate = original with
            {
                ByteLimited = true, OmittedEffects = original.Projection.Effects.Count - count,
                Reasons = original.Reasons.Concat(["response-byte-bound"]).ToArray(),
                Projection = original.Projection with
                {
                    Complete = false, Truncated = true, Effects = effects, Measurements = measurements,
                    OmittedMeasurements = original.Projection.OmittedMeasurements +
                        original.Projection.Measurements.Count - measurements.Length
                }
            };
            if (Fits(candidate)) { best = candidate; low = count + 1; }
            else high = count - 1;
        }
        token.ThrowIfCancellationRequested();
        return best ?? throw new InvalidDataException("Durable inference metadata exceeds its fixed response bound.");
    }
    private static bool Fits(DurableInferenceReadReply reply) =>
        JsonSerializer.SerializeToUtf8Bytes(reply, CerebralJson.Options).Length <= MaximumReplyUtf8Bytes;

    private static DurableInferenceReadRequest Parse(JsonElement arguments)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string[] allowed = ["id", "projection", "expectedRequestId", "expectedConversationId",
            "expectedProjectId", "afterSequence", "snapshotSequence", "limit"];
        foreach (var property in arguments.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw InvalidRequest();
        foreach (var required in allowed.Take(5))
            if (!fields.ContainsKey(required)) throw InvalidRequest();
        var project = fields["expectedProjectId"].ValueKind == JsonValueKind.Null
            ? null : Text(fields["expectedProjectId"]);
        var after = OptionalSequence("afterSequence"); var snapshot = OptionalSequence("snapshotSequence");
        var limit = 128;
        if (fields.TryGetValue("limit", out var requestedLimit) &&
            (requestedLimit.ValueKind != JsonValueKind.Number || !requestedLimit.TryGetInt32(out limit) || limit is < 1 or > 200))
            throw InvalidRequest();
        return new(Text(fields["id"]), Selector, Text(fields["expectedRequestId"]),
            Text(fields["expectedConversationId"]), project, after, snapshot, limit);

        long? OptionalSequence(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0)
                throw InvalidRequest();
            return number;
        }
    }
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } text ||
            string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw InvalidRequest();
        return text;
    }
    private static ArgumentException InvalidRequest() => new("Invalid durable-inference-v1 owner-view request.");
    private static InvalidDataException InvalidPage() => new("Trusted durable inference journal page is inconsistent.");
}
