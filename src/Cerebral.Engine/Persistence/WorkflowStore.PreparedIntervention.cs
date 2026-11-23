using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    private const int MaximumPreparedInterventions = 16;

    /// <summary>Withdraws only a saved intent that has never crossed the durable dispatch boundary.</summary>
    internal WorkflowRun RejectPrepared(WorkflowPreparedRejectRequest request, Action<WorkflowRun> admit)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admit);
        ValidatePreparedCoordinates(request.RequestId, request.RunId, request.ExpectedRevision,
            request.EffectId, request.EffectSha256);
        ValidatePreparedReason(request.Reason);
        ValidateSteeringText(request.SteeringText, true);
        var accepted = Freeze(request);
        var digest = WorkflowPreparedEffectIdentity.RequestSha256(accepted);
        return journal.Transact((connection, transaction) =>
        {
            var run = Required(connection, transaction, accepted.RunId);
            EventJournal.RequirePreparedInterventionFormat(connection, transaction);
            if (PriorPreparedRequest(connection, transaction, run, accepted.RequestId,
                    WorkflowPreparedInterventionKind.Reject, digest))
                return run;
            if (run.Steering.Any(x => x.RequestId == accepted.RequestId))
                throw Conflict("Request ID already belongs to steering.");
            RequireIdlePrepared(connection, transaction, run, accepted.ExpectedRevision,
                accepted.EffectId, accepted.EffectSha256, needsPlanningStep: true);
            RequireInterventionCapacity(run);
            admit(run); // Pure definition/owner admission, rechecked under this transaction.
            var (steered, _) = AppendSteering(connection, transaction, run,
                accepted.RequestId, accepted.SteeringText, null);
            var signal = new WorkflowPreparedRejectionSignal(accepted.EffectId,
                accepted.Reason, accepted.RequestId);
            var rejected = steered with
            {
                Effect = steered.Effect! with { State = WorkflowEffectState.RejectedBeforeDispatch,
                    Error = accepted.Reason },
                Signal = new(WorkflowSignalKind.PreparedRejected, Element(signal)),
                Status = WorkflowStatus.Queued,
                WaitingReason = null,
                Error = null,
                PreparedInterventions = [.. (steered.PreparedInterventions ?? []),
                    new(accepted.RequestId, WorkflowPreparedInterventionKind.Reject, digest,
                        accepted.EffectId, accepted.EffectSha256, checked(steered.Revision + 1))]
            };
            InsertPreparedRequest(connection, transaction, accepted.RequestId, rejected.Id,
                WorkflowPreparedInterventionKind.Reject, digest);
            Append(connection, transaction, rejected, "workflow.effect.rejected-before-dispatch",
                new(EventActorKind.Service, "cerebral.engine"), [],
                new(ModelInclusion.Evidence, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine"), Element(new
                {
                    runId = rejected.Id, effectId = accepted.EffectId,
                    requestId = accepted.RequestId, accepted.Reason,
                    dispatched = false, providerReceipt = false
                }));
            return Save(connection, transaction, rejected, "effect.rejected-before-dispatch");
        });
    }

    /// <summary>Allows one separately reviewed exact prepared effect to cross an exhausted budget.</summary>
    internal WorkflowRun PermitPreparedOnce(WorkflowPreparedPermitRequest request,
        Func<WorkflowRun, string?> classifyBudgetKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(classifyBudgetKey);
        ValidatePreparedCoordinates(request.RequestId, request.RunId, request.ExpectedRevision,
            request.EffectId, request.EffectSha256);
        ValidatePreparedReason(request.Reason);
        var accepted = Freeze(request);
        var digest = WorkflowPreparedEffectIdentity.RequestSha256(accepted);
        return journal.Transact((connection, transaction) =>
        {
            var run = Required(connection, transaction, accepted.RunId);
            EventJournal.RequirePreparedInterventionFormat(connection, transaction);
            if (PriorPreparedRequest(connection, transaction, run, accepted.RequestId,
                    WorkflowPreparedInterventionKind.PermitOnce, digest))
                return run;
            if (run.Steering.Any(x => x.RequestId == accepted.RequestId))
                throw Conflict("Request ID already belongs to steering.");
            RequireIdlePrepared(connection, transaction, run, accepted.ExpectedRevision,
                accepted.EffectId, accepted.EffectSha256, needsPlanningStep: false);
            RequireInterventionCapacity(run);
            if (run.PreparedInterventions?.Any(x =>
                x.Kind == WorkflowPreparedInterventionKind.Reject &&
                x.EffectId != accepted.EffectId && x.AppliedRevision < run.Revision) != true)
                throw Conflict("A distinct newly planned effect is required after prepared rejection.");
            var key = classifyBudgetKey(run);
            if (key is null || !run.Submission.FailureLimits.TryGetValue(key, out var limit) ||
                run.FailedEffects.GetValueOrDefault(key) < limit)
                throw Conflict("Only the exact currently exhausted admitted failure category may receive one permit.");
            if (run.PreparedInterventions?.Any(x => x.Kind == WorkflowPreparedInterventionKind.PermitOnce &&
                x.EffectId == accepted.EffectId) == true)
                throw Conflict("This exact effect already has a one-use permit.");
            var permitted = run with
            {
                WaitingReason = "One exact prepared effect is permitted; resume this revision to dispatch it.",
                PreparedInterventions = [.. (run.PreparedInterventions ?? []),
                    new(accepted.RequestId, WorkflowPreparedInterventionKind.PermitOnce, digest,
                        accepted.EffectId, accepted.EffectSha256, checked(run.Revision + 1), key)]
            };
            InsertPreparedRequest(connection, transaction, accepted.RequestId, permitted.Id,
                WorkflowPreparedInterventionKind.PermitOnce, digest);
            Append(connection, transaction, permitted, "workflow.effect.permit-once",
                new(EventActorKind.Service, "cerebral.engine"), [],
                new(ModelInclusion.Evidence, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine"), Element(new
                {
                    runId = permitted.Id, effectId = accepted.EffectId,
                    requestId = accepted.RequestId, budgetKey = key,
                    originalLimit = limit, failedEffects = run.FailedEffects.GetValueOrDefault(key),
                    accepted.Reason
                }));
            return Save(connection, transaction, permitted, "effect.permit-once");
        });
    }

    internal static bool HasAvailablePreparedPermit(WorkflowRun run, string key)
    {
        if (run.Effect is not { State: WorkflowEffectState.Prepared } effect) return false;
        var hash = WorkflowPreparedEffectIdentity.Sha256(effect);
        return run.PreparedInterventions?.Any(x => x.Kind == WorkflowPreparedInterventionKind.PermitOnce &&
            !x.Consumed && x.EffectId == effect.Id && x.EffectSha256 == hash && x.BudgetKey == key) == true;
    }

    private static bool PriorPreparedRequest(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run, string requestId,
        WorkflowPreparedInterventionKind kind, string digest)
    {
        using var query = EventJournal.Command(connection, transaction,
            "SELECT run_id,kind,request_sha256 FROM workflow_prepared_requests WHERE request_id=$id;");
        query.Parameters.AddWithValue("$id", requestId);
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            if (run.PreparedInterventions?.Any(x => x.RequestId == requestId) == true)
                throw new InvalidDataException("Prepared request ledger is inconsistent with its run.");
            return false;
        }
        var prior = run.PreparedInterventions?.SingleOrDefault(x => x.RequestId == requestId);
        if (reader.GetString(0) != run.Id || reader.GetString(1) != kind.ToString() ||
            reader.GetString(2) != digest || prior is null || prior.Kind != kind ||
            prior.RequestSha256 != digest)
            throw Conflict("Prepared-effect request ID already belongs to different input.");
        return true;
    }

    private static void InsertPreparedRequest(SqliteConnection connection, SqliteTransaction transaction,
        string requestId, string runId, WorkflowPreparedInterventionKind kind, string digest)
    {
        using var insert = EventJournal.Command(connection, transaction, """
            INSERT INTO workflow_prepared_requests(request_id,run_id,kind,request_sha256)
            VALUES($id,$run,$kind,$digest);
            """);
        insert.Parameters.AddWithValue("$id", requestId);
        insert.Parameters.AddWithValue("$run", runId);
        insert.Parameters.AddWithValue("$kind", kind.ToString());
        insert.Parameters.AddWithValue("$digest", digest);
        insert.ExecuteNonQuery();
    }

    private static void RequireIdlePrepared(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run, long expectedRevision, string effectId, string effectSha256,
        bool needsPlanningStep)
    {
        if (run.Revision != expectedRevision || run.Status != WorkflowStatus.Waiting || run.CancelRequested ||
            run.Effect is not { State: WorkflowEffectState.Prepared } effect ||
            effect.Id != effectId || WorkflowPreparedEffectIdentity.Sha256(effect) != effectSha256 ||
            effect.ReservedExecutionMilliseconds != 0 || effect.FailureBudgetKey is not null ||
            effect.Result is not null || effect.Error is not null || effect.ProviderReceiptId is not null ||
            run.Questions is not null || run.WaitingForRunIds.Count != 0 ||
            (needsPlanningStep && run.Steps >= run.Submission.EffectiveStepLimit) ||
            run.ActiveMilliseconds >= (long)run.Submission.ActiveTimeLimitSeconds * 1000)
            throw Conflict("The exact inactive, never-dispatched prepared effect is unavailable.");
        using var receipt = EventJournal.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM workflow_effect_results WHERE effect_id=$id);");
        receipt.Parameters.AddWithValue("$id", effectId);
        if ((long)receipt.ExecuteScalar()! != 0)
            throw Conflict("The prepared effect already has a committed result.");
    }

    private static void RequireInterventionCapacity(WorkflowRun run)
    {
        if ((run.PreparedInterventions?.Count ?? 0) >= MaximumPreparedInterventions)
            throw Conflict("This run has reached its prepared-intervention limit.");
    }

    private static void ValidatePreparedCoordinates(string requestId, string runId, long revision,
        string effectId, string effectSha256)
    {
        Identifier(requestId, nameof(requestId)); Identifier(runId, nameof(runId));
        Identifier(effectId, nameof(effectId));
        if (revision < 1 || revision == long.MaxValue || effectSha256 is not { Length: 64 } ||
            effectSha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("Supply exact prepared-effect coordinates.");
    }

    private static void ValidatePreparedReason(string reason)
    {
        Text(reason, nameof(reason), 3072, required: true);
        if (Encoding.UTF8.GetByteCount(reason) > 4096)
            throw new ArgumentException("Prepared-effect reason exceeds 4 KiB UTF-8.");
    }
}
