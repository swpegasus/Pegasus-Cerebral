using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Persistence;

/// <summary>A bounded view of immutable recorded outcomes, not an exhaustive operation inventory.</summary>
public sealed record ConversationCommandOutcomes(IReadOnlyList<CerebralEvent> Events,
    int ScannedEvents, bool ScanLimited, bool ByteLimited, bool ItemLimited, int OmittedOutcomes);

public sealed partial class WorkflowStore
{
    public const int MaximumConversationOperationScan = 512;
    public const int MaximumConversationOperationItems = 16;

    /// <summary>Read recorded command outcomes at an accepted conversation boundary. The current
    /// mutable receipt table is deliberately excluded: reconciliation must not change an older snapshot.
    /// Projection exclusions are applied after selecting the newest outcome for each effect.</summary>
    public ConversationCommandOutcomes ReadConversationCommandOutcomes(string conversationId,
        long snapshotSequence, string commandId, int limit = MaximumConversationOperationItems,
        CancellationToken cancellationToken = default)
    {
        Identifier(conversationId, nameof(conversationId)); Identifier(commandId, nameof(commandId));
        if (snapshotSequence < 0) throw new ArgumentOutOfRangeException(nameof(snapshotSequence));
        if (limit is < 1 or > MaximumConversationOperationItems) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (snapshotSequence > EventJournal.ReadRevision(connection, transaction, conversationId))
            throw new ArgumentOutOfRangeException(nameof(snapshotSequence));
        // Bound the indexed event range before inspecting JSON. Filtering the whole history and
        // then applying LIMIT would bound returned rows but could still scan an unbounded history.
        using var command = EventJournal.Command(connection, transaction, """
            SELECT sequence,event_id,
                json_extract(payload,'$.type'),json_extract(payload,'$.data.toolName'),
                json_extract(payload,'$.data.callId'),length(CAST(payload AS BLOB)),
                CASE WHEN json_extract(payload,'$.type')=$type
                    AND json_extract(payload,'$.data.toolName')=$command
                    AND length(CAST(payload AS BLOB)) <= $maximum THEN payload END,
                EXISTS(SELECT 1 FROM workflow_runs r WHERE r.id=json_extract(e.payload,'$.turnId')
                    AND r.conversation_id=$conversation)
            FROM conversation_events e
            WHERE conversation_id=$conversation AND sequence <= $snapshot
            ORDER BY sequence DESC LIMIT $scan;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$snapshot", snapshotSequence);
        command.Parameters.AddWithValue("$scan", MaximumConversationOperationScan + 1);
        command.Parameters.AddWithValue("$type", CerebralEventTypes.ToolResult);
        command.Parameters.AddWithValue("$command", commandId);
        command.Parameters.AddWithValue("$maximum", EventJournal.MaximumEventBytes);
        var items = new List<CerebralEvent>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0; var bytes = 0L; var omitted = 0;
        var scanLimited = false; var byteLimited = false; var itemLimited = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned == MaximumConversationOperationScan) { scanLimited = true; break; }
            scanned++;
            if (reader.IsDBNull(2) || reader.GetString(2) != CerebralEventTypes.ToolResult ||
                reader.IsDBNull(3) || reader.GetString(3) != commandId) continue;
            var effectId = reader.IsDBNull(4) ? null : reader.GetString(4);
            // An excluded, oversized or invalid newer outcome suppresses older outcomes for the
            // same effect too. Otherwise a later exclusion could resurrect stale Unknown evidence.
            if (effectId is not { Length: > 0 and <= 128 }) { omitted++; continue; }
            if (!seen.Add(effectId)) continue;
            var length = reader.GetInt64(5);
            if (reader.IsDBNull(6) || bytes + length > EventJournal.MaximumPageBytes)
            { omitted++; byteLimited = true; continue; }
            bytes += length;
            try
            {
                var item = Deserialize<CerebralEvent>(reader.GetString(6));
                item.Validate();
                if (item.Id != reader.GetString(1) || item.Sequence != reader.GetInt64(0) ||
                    item.ConversationId != conversationId || item.Type != CerebralEventTypes.ToolResult ||
                    item.Actor is not { Kind: EventActorKind.Tool } || item.Actor.Id != commandId ||
                    item.Provenance.Source != EventSourceKind.Tool || item.Provenance.SourceId != commandId ||
                    item.Provenance.JobId != item.TurnId || item.CorrelationId != effectId || reader.GetInt64(7) == 0 ||
                    item.Data is not { } data || Deserialize<ToolResultData>(data.GetRawText()) is not { } details ||
                    details.CallId != effectId || details.ToolName != commandId ||
                    item.Content.Count != 1 || item.Content[0] is not StructuredContentPart { Schema: "workflow.command.result" } part)
                { omitted++; continue; }
                var result = Deserialize<WorkflowCommandResult>(part.Value.GetRawText());
                ValidateResult(result);
                if (details.Success != (result.State == WorkflowEffectState.Succeeded)) { omitted++; continue; }
                // Excluded identities and contents stay hidden. A count-only gap prevents an
                // older different-effect artifact from appearing to have complete later coverage.
                if (!EventProjection.IncludeInModel(item)) { omitted++; continue; }
                if (items.Count == limit) { omitted++; itemLimited = true; continue; }
                items.Add(item);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
            { omitted++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        items.Reverse();
        return new(items.AsReadOnly(), scanned, scanLimited, byteLimited, itemLimited, omitted);
    }
}
