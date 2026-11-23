using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

/// <summary>Owner-facing coordinates only; never includes saved arguments, model context or result bodies.</summary>
public sealed record WorkflowAbandonInspection(string RunId, long Revision, WorkflowStatus Status,
    bool CancelRequested, string? EffectId, WorkflowEffectState? EffectState, string? EffectSha256,
    WorkflowReplayPolicy? ReplayPolicy, bool Eligible, string? RefusalReason,
    bool Abandoned, string? AbandonRequestId);

public sealed partial class WorkflowStore
{
    private const string OwnerAbandonRequestEventType = "workflow.abandon.request";
    private const string OwnerAbandonEventType = "workflow.abandoned";
    private sealed record OwnerAbandonDecision(string RequestId, string RequestSha256, string RunId,
        long ExpectedRevision, long AppliedRevision, string EffectId, string EffectSha256,
        string Reason, bool ExternalEffectsSettled);

    public WorkflowAbandonInspection InspectOwnerAbandon(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = Required(connection, transaction, id);
        var decision = ReadOwnerAbandonDecision(connection, transaction, run);
        var refusal = decision is null ? OwnerAbandonRefusal(connection, transaction, run) :
            "Workflow tracking was already abandoned; the effect outcome remains Unknown.";
        return new(run.Id, run.Revision, run.Status, run.CancelRequested, run.Effect?.Id,
            run.Effect?.State, run.Effect is { } effect ? WorkflowPreparedEffectIdentity.Sha256(effect) : null,
            run.Effect?.ReplayPolicy, refusal is null && decision is null, refusal,
            decision is not null, decision?.RequestId);
    }

    /// <summary>Abandon workflow bookkeeping only. The host must hold its scheduler admission gate
    /// and supply a non-mutating live-ownership check, including on idempotent retries.</summary>
    public WorkflowRun AbandonOwner(string requestId, string id, long expectedRevision,
        string effectId, string effectSha256, string reason, Action<WorkflowRun> admit)
    {
        ValidatePreparedCoordinates(requestId, id, expectedRevision, effectId, effectSha256);
        ValidatePreparedReason(reason);
        ArgumentNullException.ThrowIfNull(admit);
        var digest = WorkflowPreparedEffectIdentity.RequestSha256(new
            { requestId, runId = id, expectedRevision, effectId, effectSha256, reason });
        return journal.Transact((connection, transaction) =>
        {
            var run = Required(connection, transaction, id);
            admit(run);
            var decision = ReadOwnerAbandonDecision(connection, transaction, run);
            var prior = ReadOwnerAbandonRecord(connection, transaction,
                OwnerAbandonRequestEventId(requestId), OwnerAbandonRequestEventType);
            if (prior is not null)
            {
                if (prior.RunId != id || prior.RequestSha256 != digest || decision is null ||
                    prior != decision || decision.RequestId != requestId ||
                    decision.AppliedRevision != run.Revision || run.Status != WorkflowStatus.Cancelled ||
                    !run.CancelRequested || run.Effect?.Id != effectId ||
                    run.Effect.State != WorkflowEffectState.Unknown ||
                    WorkflowPreparedEffectIdentity.Sha256(run.Effect) != effectSha256)
                    throw Conflict("Owner request identity already belongs to different or changed work.");
                return run;
            }
            if (decision is not null)
                throw Conflict("This workflow already has a different owner abandonment decision.");
            if (run.Steering.Any(x => x.RequestId == requestId))
                throw Conflict("The owner request identity already belongs to steering.");
            if (run.Revision != expectedRevision || run.Effect?.Id != effectId ||
                WorkflowPreparedEffectIdentity.Sha256(run.Effect) != effectSha256)
                throw Conflict("The exact workflow revision or effect fingerprint changed.");
            if (OwnerAbandonRefusal(connection, transaction, run) is { } refusal)
                throw Conflict(refusal);

            // No effect, receipt, signal, context, outcome, failure count or charged time is rewritten.
            var abandoned = Save(connection, transaction, run with
            {
                Status = WorkflowStatus.Cancelled, CancelRequested = true, WaitingReason = null
            }, "owner.abandoned");
            var saved = new OwnerAbandonDecision(requestId, digest, id, expectedRevision,
                abandoned.Revision, effectId, effectSha256, reason, ExternalEffectsSettled: false);
            AppendOwnerAbandonRecord(connection, transaction, abandoned, OwnerAbandonEventId(id),
                OwnerAbandonEventType, saved, HumanVisibility.Activity);
            AppendOwnerAbandonRecord(connection, transaction, abandoned, OwnerAbandonRequestEventId(requestId),
                OwnerAbandonRequestEventType, saved, HumanVisibility.Hidden);
            return abandoned;
        });
    }

    private static string? OwnerAbandonRefusal(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run)
    {
        if (run.Status != WorkflowStatus.Waiting || !run.CancelRequested ||
            run.Effect?.State != WorkflowEffectState.Unknown)
            return "Only already-cancel-requested Waiting work with an unchanged Unknown effect can be abandoned.";
        using (var children = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM workflow_runs
                WHERE json_extract(submission,'$.parentRunId')=$run
                  AND status NOT IN ('Completed','Failed','Cancelled'));
            """))
        {
            children.Parameters.AddWithValue("$run", run.Id);
            if ((long)children.ExecuteScalar()! != 0)
                return "Nonterminal child work remains; resolve each exact owned child before abandoning its parent.";
        }
        using var dependents = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM workflow_runs p
                WHERE p.id<>$run AND p.status NOT IN ('Completed','Failed','Cancelled')
                  AND COALESCE(json_extract(p.payload,'$.cancelRequested'),0)=0
                  AND (p.id=$parent
                    OR EXISTS(SELECT 1 FROM json_each(p.payload,'$.waitingForRunIds') dependency
                        WHERE dependency.value=$run)
                    OR EXISTS(SELECT 1 FROM collaboration_tasks task
                        WHERE task.target_run_id=$run
                          AND json_extract(task.payload,'$.sourceRunId')=p.id)));
            """);
        dependents.Parameters.AddWithValue("$run", run.Id);
        dependents.Parameters.AddWithValue("$parent", (object?)run.Submission.ParentRunId ?? DBNull.Value);
        return (long)dependents.ExecuteScalar()! == 0 ? null :
            "A non-cancelled parent or dependent could resume; resolve its exact owner cancellation first.";
    }

    private static string OwnerAbandonEventId(string id) => OwnerAbandonIdentity("run", id);
    private static string OwnerAbandonRequestEventId(string id) => OwnerAbandonIdentity("request", id);
    private static string OwnerAbandonIdentity(string kind, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("workflow.owner-abandon/1:" + kind + ":" + id))).ToLowerInvariant();

    private static void AppendOwnerAbandonRecord(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run, string eventId, string type, OwnerAbandonDecision decision, HumanVisibility visibility)
    {
        var item = new CerebralEvent(eventId, run.Submission.ConversationId, run.Id, 0,
            type, new(EventActorKind.User, "owner"), [],
            new(ModelInclusion.OnDemand, visibility),
            new(EventSourceKind.Service, "cerebral.engine", JobId: run.Id), DateTimeOffset.UtcNow,
            Element(decision), CorrelationId: decision.RequestId);
        EventJournal.AppendInTransaction(connection, transaction, item,
            EventJournal.ReadRevision(connection, transaction, item.ConversationId));
    }

    private static OwnerAbandonDecision? ReadOwnerAbandonRecord(SqliteConnection connection,
        SqliteTransaction transaction, string eventId, string type)
    {
        using var query = EventJournal.Command(connection, transaction,
            "SELECT payload FROM conversation_events WHERE event_id=$id;");
        query.Parameters.AddWithValue("$id", eventId);
        if (query.ExecuteScalar() is not string payload) return null;
        var item = Deserialize<CerebralEvent>(payload);
        item.Validate();
        if (item.Id != eventId || item.Type != type ||
            item.Actor is not { Kind: EventActorKind.User, Id: "owner" } ||
            item.Provenance.Source != EventSourceKind.Service ||
            item.Provenance.SourceId != "cerebral.engine" || item.Data is not { } data)
            throw new InvalidDataException("Owner abandonment evidence has invalid provenance.");
        var decision = Deserialize<OwnerAbandonDecision>(data.GetRawText());
        if (decision.ExternalEffectsSettled || item.TurnId != decision.RunId ||
            item.Provenance.JobId != decision.RunId || item.CorrelationId != decision.RequestId)
            throw new InvalidDataException("Owner abandonment evidence does not match its request.");
        return decision;
    }

    private static OwnerAbandonDecision? ReadOwnerAbandonDecision(SqliteConnection connection,
        SqliteTransaction transaction, WorkflowRun run)
    {
        var decision = ReadOwnerAbandonRecord(connection, transaction, OwnerAbandonEventId(run.Id),
            OwnerAbandonEventType);
        if (decision is null) return null;
        var request = ReadOwnerAbandonRecord(connection, transaction,
            OwnerAbandonRequestEventId(decision.RequestId), OwnerAbandonRequestEventType);
        if (decision.RunId != run.Id || request != decision ||
            decision.AppliedRevision != run.Revision || run.Status != WorkflowStatus.Cancelled ||
            !run.CancelRequested || run.Effect?.Id != decision.EffectId ||
            run.Effect.State != WorkflowEffectState.Unknown ||
            WorkflowPreparedEffectIdentity.Sha256(run.Effect) != decision.EffectSha256)
            throw new InvalidDataException("The retained owner abandonment decision or effect changed.");
        return decision;
    }
}
