using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker;

internal sealed record RemoteModelRecoveryRun(string Id, long Revision, string Fingerprint);
internal sealed record RemoteModelRecoveryBarrier(long EngineEpoch, string WorkerId, string WorkerInstallationId,
    string ModelId, IReadOnlyList<RemoteModelRecoveryRun> Runs);

public sealed partial class CommandDispatcher
{
    private const int MaximumRemoteRecoveryRuns = 256;

    internal static WorkerWorkflows.OperationDescriptor RemoteModelRecoveryOperation { get; } =
        new("workers.models.recover", 1,
            "Owner-only recovery for one exact paired remote model after uncertain provider work. The engine must already be paused and drained; retained Waiting workflows remain byte-for-byte unchanged.",
            RecoverySchema(), true);

    private static JsonElement RecoverySchema()
    {
        using var document = JsonDocument.Parse("""
            {"type":"object","properties":{
              "workerId":{"type":"string","minLength":1,"maxLength":100},
              "id":{"type":"string","minLength":1,"maxLength":100},
              "expectedEngineEpoch":{"type":"integer","minimum":0}
            },"required":["workerId","id","expectedEngineEpoch"],"additionalProperties":false}
            """);
        return document.RootElement.Clone();
    }

    private async Task<object> RecoverRemoteModelAsync(JsonElement args, CancellationToken cancellationToken)
    {
        RemoteWorkerManagement.RequireFields(args, ["workerId", "id", "expectedEngineEpoch"]);
        var workerId = Text(args, "workerId");
        var modelId = Text(args, "id");
        Configuration.ValidateId(workerId); Configuration.ValidateId(modelId);
        if (args.GetProperty("expectedEngineEpoch") is not { ValueKind: JsonValueKind.Number } epochValue ||
            !epochValue.TryGetInt64(out var expectedEpoch) || expectedEpoch < 0)
            throw new ArgumentException("expectedEngineEpoch must be a non-negative 64-bit integer.");
        var host = workflows ?? throw new InvalidOperationException("Workflow recovery is unavailable on this Worker.");
        var engine = host.Engine ?? throw new InvalidOperationException("This Worker has no replaceable engine controller.");

        return await WithOwnerEngineMutationAsync<object>(async token =>
        {
            RequireRemoteRecoveryEngine(engine, expectedEpoch);
            RemoteModelRecoveryBarrier? retained = null;
            var stopped = await remoteModels.RecoverStoppedModelAsync(workerId, modelId,
                installationId =>
                {
                    RequireRemoteRecoveryEngine(engine, expectedEpoch);
                    retained = CaptureRemoteRecoveryBarrier(host, engine, expectedEpoch, workerId, installationId, modelId);
                    return retained;
                },
                barrier =>
                {
                    token.ThrowIfCancellationRequested();
                    RequireRemoteRecoveryEngine(engine, expectedEpoch);
                    var current = CaptureRemoteRecoveryBarrier(host, engine, expectedEpoch, workerId,
                        barrier.WorkerInstallationId, modelId);
                    if (!SameRemoteRecoveryBarrier(barrier, current))
                        throw new InvalidOperationException("A retained workflow changed during remote model recovery. The verified unload remains fenced; refresh and retry only after the run is quiescent.");
                }, token).ConfigureAwait(false);
            var captured = retained ?? throw new InvalidOperationException("Remote recovery did not capture its retained workflow barrier.");
            return new
            {
                recovered = true,
                workerId = stopped.WorkerId,
                workerInstallationId = stopped.WorkerInstallationId,
                modelId = stopped.ModelId,
                state = stopped.State,
                expectedEngineEpoch = expectedEpoch,
                retainedWorkflowIds = captured.Runs.Select(run => run.Id).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireRemoteRecoveryEngine(EngineController engine, long expectedEpoch)
    {
        var status = engine.Status();
        if (status.Epoch != expectedEpoch)
            throw new InvalidOperationException("Engine activation changed; refresh before retrying remote model recovery.");
        if (!status.Paused || status.ActiveEvaluations != 0)
            throw new InvalidOperationException("Pause the engine and wait for all evaluations before recovering a remote model.");
        if (status.CandidatePending)
            throw new InvalidOperationException("Promote or roll back the pending engine candidate before recovering a remote model.");
        if (status.Active is null || status.Error is not null)
            throw new InvalidOperationException("Restore a healthy active engine before recovering a remote model.");
    }

    private RemoteModelRecoveryBarrier CaptureRemoteRecoveryBarrier(WorkerWorkflows host, EngineController engine,
        long expectedEpoch, string workerId, string installationId, string modelId)
    {
        RequireRemoteRecoveryEngine(engine, expectedEpoch);
        var activeJobs = store.List<AgentJob>("job").Where(JobScheduler.IsActive).Take(MaximumRemoteRecoveryRuns + 1).ToArray();
        if (activeJobs.Length > MaximumRemoteRecoveryRuns)
            throw new InvalidOperationException("Too many active legacy assignments exist to prove a bounded remote recovery barrier.");
        foreach (var job in activeJobs)
        {
            if (job.Profile is null || job.WorkerId == workerId ||
                job.Profile.ModelWorkerId == workerId && job.Profile.ModelId == modelId ||
                job.Profile.AutomaticCompaction)
                throw new InvalidOperationException("A legacy or remotely executing assignment may still own this model. Finish it before remote model recovery.");
        }

        var retained = new List<RemoteModelRecoveryRun>();
        var activeWorkflowCount = 0;
        long afterRow = 0;
        while (true)
        {
            var page = host.Store.ListActivePage(100, afterRow);
            activeWorkflowCount = checked(activeWorkflowCount + page.Runs.Count);
            if (activeWorkflowCount > MaximumRemoteRecoveryRuns)
                throw new InvalidOperationException("Too many active workflows exist to prove a bounded remote recovery barrier.");
            foreach (var run in page.Runs)
            {
                WorkerExecutionSnapshot.Snapshot snapshot;
                try { snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
                {
                    throw new InvalidOperationException("An active workflow has unsupported or malformed frozen model settings. Finish it before remote model recovery.", error);
                }
                if (!TargetsRemoteModel(snapshot, workerId, installationId, modelId)) continue;
                if (run.Status != WorkflowStatus.Waiting || run.Effect is { State: not WorkflowEffectState.Unknown })
                    throw new InvalidOperationException("Only retained Waiting workflows with no effect or an Unknown effect may remain during remote model recovery.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(run, CerebralJson.Options);
                retained.Add(new(run.Id, run.Revision, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            if (page.NextAfterRow is not { } next) break;
            afterRow = next;
        }
        retained.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return new(expectedEpoch, workerId, installationId, modelId, retained.ToArray());
    }

    private static bool TargetsRemoteModel(WorkerExecutionSnapshot.Snapshot snapshot, string workerId,
        string installationId, string modelId)
    {
        var targeted = false;
        var primaryClaim = snapshot.Profile.ModelWorkerId == workerId && snapshot.Profile.ModelId == modelId ||
            snapshot.Model is { } primaryModel && primaryModel.ModelWorkerId == workerId && primaryModel.Id == modelId;
        if (primaryClaim)
        {
            if (snapshot.Profile.ModelWorkerId != workerId || snapshot.Profile.ModelId != modelId ||
                snapshot.Model is not { } binding || binding.ModelWorkerId != workerId || binding.Id != modelId ||
                binding.ModelWorkerInstallationId != installationId)
                throw new InvalidOperationException("An active workflow has a stale or inconsistent primary remote model binding.");
            targeted = true;
        }

        if (snapshot.Compactor is not { } compactor) return targeted;
        var compactorClaim = compactor.Profile.ModelWorkerId == workerId && compactor.Profile.ModelId == modelId ||
            compactor.Model.ModelWorkerId == workerId && compactor.Model.Id == modelId;
        if (compactorClaim)
        {
            if (compactor.Profile.ModelWorkerId != workerId || compactor.Profile.ModelId != modelId ||
                compactor.Model.ModelWorkerId != workerId || compactor.Model.Id != modelId ||
                compactor.Model.ModelWorkerInstallationId != installationId)
                throw new InvalidOperationException("An active workflow has a stale or inconsistent compactor remote model binding.");
            targeted = true;
        }
        return targeted;
    }

    private static bool SameRemoteRecoveryBarrier(RemoteModelRecoveryBarrier expected,
        RemoteModelRecoveryBarrier current) =>
        expected.EngineEpoch == current.EngineEpoch && expected.WorkerId == current.WorkerId &&
        expected.WorkerInstallationId == current.WorkerInstallationId && expected.ModelId == current.ModelId &&
        expected.Runs.SequenceEqual(current.Runs);
}
