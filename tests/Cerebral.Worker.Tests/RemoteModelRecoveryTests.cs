using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class RemoteWorkerManagementTests
{
    private static async Task<int> RunRemoteModelRecoveryChecksAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            checks++; Console.WriteLine("PASS: " + name);
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = AddRemoteRun(h, "recovery-waiting", "waiting");
            var unknown = AddRemoteRun(h, "recovery-unknown", "unknown");
            unknown = h.Workflows!.Store.Cancel(unknown.Id);
            var unrelated = AddRemoteRun(h, "recovery-unrelated", "waiting", workerId: "local", installationId: null, modelId: "old");
            var waitingBefore = CerebralJson.Element(waiting).GetRawText();
            var unknownBefore = CerebralJson.Element(unknown).GetRawText();
            var unrelatedBefore = CerebralJson.Element(unrelated).GetRawText();
            var epoch = await Pause(h);

            var ordinary = await h.Call("workers.models.unload", new { workerId = "gb10", id = "remote-model" });
            Check(!ordinary.Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "ordinary unload remains conservative while exact frozen Waiting workflows retain the model");

            var described = await h.Call("sdk.describe", new { id = "workers.models.recover" });
            Check(described.Success && described.Data.GetProperty("id").GetString() == "workers.models.recover" &&
                described.Data.GetProperty("ownerOnly").GetBoolean(),
                "owner SDK describes the closed recovery operation without exposing it as agent authority");
            var actor = new AgentProfile { Id = "recovery-admin", Capabilities = ["administration"] };
            h.Store.Put("agent", actor.Id, actor);
            var denied = await h.Commands.ExecuteAsync(new("workers.models.recover", CerebralJson.Element(new
                { workerId = "gb10", id = "remote-model", expectedEngineEpoch = epoch }), actor.Id));
            Check(!denied.Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "administration agents cannot invoke owner remote recovery");

            var recovered = await Recover(h, epoch);
            var retained = recovered.Data.GetProperty("retainedWorkflowIds").EnumerateArray().Select(item => item.GetString()).ToArray();
            Check(recovered.Success && recovered.Data.GetProperty("workerInstallationId").GetString() == "remote-installation" &&
                recovered.Data.GetProperty("state").GetString() == "unloaded" &&
                retained.SequenceEqual(new[] { unknown.Id, waiting.Id }.Order(StringComparer.Ordinal)) &&
                h.Remote.Calls.Count(call => call == "models.unload") == 1,
                "one exact unload and independent inspection clear quarantine while reporting only retained targeted runs");
            Check(CerebralJson.Element(h.Workflows!.Store.Get(waiting.Id)).GetRawText() == waitingBefore &&
                CerebralJson.Element(h.Workflows.Store.Get(unknown.Id)).GetRawText() == unknownBefore &&
                CerebralJson.Element(h.Workflows.Store.Get(unrelated.Id)).GetRawText() == unrelatedBefore,
                "successful recovery preserves Waiting questions, cancel-requested Unknown evidence, settings and unrelated workflow bytes exactly");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var initial = h.Engine!.Status();
            Check(!(await Recover(h, initial.Epoch)).Success && h.FactoryCalls == 0,
                "recovery rejects an unpaused engine before opening the remote owner route");
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch + 1)).Success && h.FactoryCalls == 0,
                "recovery rejects a stale engine epoch before remote connection");
            foreach (var invalid in new object[]
            {
                new { workerId = "gb10", id = "remote-model", expectedEngineEpoch = "1" },
                new { workerId = "gb10", id = "remote-model", expectedEngineEpoch = epoch, unexpected = true },
                new { workerId = "../gb10", id = "remote-model", expectedEngineEpoch = epoch }
            })
                Check(!(await h.Call("workers.models.recover", invalid)).Success && h.FactoryCalls == 0,
                    "closed recovery arguments reject wrong types, extra fields and invalid identifiers before transport");
            using var duplicate = JsonDocument.Parse($"{{\"workerId\":\"gb10\",\"id\":\"remote-model\",\"expectedEngineEpoch\":{epoch},\"expectedEngineEpoch\":{epoch}}}");
            Check(!(await h.Commands.ExecuteAsync(new("workers.models.recover", duplicate.RootElement.Clone()))).Success && h.FactoryCalls == 0,
                "duplicate recovery arguments fail closed before transport");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var epoch = await Pause(h);
            var candidate = await h.Call("engine.activate", new { slot = "B", expectedEpoch = epoch });
            Check(candidate.Success && !(await Recover(h, candidate.Data.GetProperty("epoch").GetInt64())).Success && h.FactoryCalls == 0,
                "a pending engine candidate blocks remote recovery");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var evaluation = h.Engine!.EvaluateAsync("recovery-evaluation", 1, "cerebral.workflow.transition", 1,
                WorkflowContract.EmptyObject, async token =>
                {
                    entered.TrySetResult(true);
                    return await release.Task.WaitAsync(token);
                }, default);
            await entered.Task;
            var paused = h.Engine.Pause(h.Engine.Status().Epoch);
            Check(!(await Recover(h, paused.Epoch)).Success && h.FactoryCalls == 0,
                "an active engine evaluation blocks remote recovery even after pause");
            release.TrySetResult(WorkflowContract.EmptyObject);
            using var lease = await evaluation;
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            try
            {
                _ = await h.Engine!.EvaluateAsync("recovery-fault", 1, "cerebral.model.idle", 1,
                    WorkflowContract.EmptyObject, _ => Task.FromResult(WorkflowContract.EmptyObject), default);
            }
            catch (EngineEvaluationException) { }
            var status = h.Engine!.Status();
            Check(status.Paused && status.Error is not null && !(await Recover(h, status.Epoch)).Success && h.FactoryCalls == 0,
                "a faulted engine cannot authorize remote recovery");
        }

        foreach (var state in new[] { "queued", "running", "prepared", "waiting-prepared", "dispatched" })
        {
            await using var h = new Harness(withWorkflows: true, withEngine: true);
            _ = AddRemoteRun(h, "recovery-" + state, state);
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "remote recovery rejects a targeted " + state + " workflow before unload");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            _ = AddRemoteRun(h, "recovery-stale-installation", "waiting", installationId: "retired-installation");
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "a stale frozen remote installation blocks recovery before unload");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var malformed = h.Workflows!.Store.Submit(new("recovery-malformed", "chat-recovery-malformed", "lead",
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Retain malformed legacy state.", null, WorkflowContract.EmptyObject,
                CerebralJson.Element(new { resolvedSettingsVersion = int.MaxValue })));
            _ = h.Workflows.Store.TryClaim(malformed.Id);
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "malformed active workflow settings fail closed before remote unload");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var legacy = RecoverySnapshot("gb10", "remote-installation", "remote-model") with
                { ResolvedSettingsVersion = 2 };
            var submitted = h.Workflows!.Store.Submit(new("recovery-legacy-snapshot", "chat-recovery-legacy-snapshot", "lead",
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Retain unsupported remote legacy state.", null, WorkflowContract.EmptyObject, CerebralJson.Element(legacy)));
            var claimed = h.Workflows.Store.TryClaim(submitted.Id)!;
            _ = h.Workflows.Store.ApplyStep(submitted.Id, claimed.Revision,
                new(claimed.State, new AskWorkflowQuestions([new("answer", "Answer")])));
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "a version-two remote snapshot cannot borrow current installation identity for recovery");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var compactor = AddRemoteRun(h, "recovery-compactor", "waiting", compactorTarget: true);
            var before = CerebralJson.Element(compactor).GetRawText();
            var epoch = await Pause(h);
            var recovered = await Recover(h, epoch);
            Check(recovered.Success && recovered.Data.GetProperty("retainedWorkflowIds").EnumerateArray()
                    .Any(item => item.GetString() == compactor.Id) &&
                CerebralJson.Element(h.Workflows!.Store.Get(compactor.Id)).GetRawText() == before,
                "an exact current compactor binding is retained byte-for-byte in the Waiting barrier");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            _ = AddRemoteRun(h, "recovery-bounded-target", "waiting");
            for (var index = 0; index < 256; index++)
                _ = AddRemoteRun(h, "recovery-bounded-local-" + index, "queued", workerId: "local",
                    installationId: null, modelId: "old");
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "more than 256 active workflows fail closed before an unbounded recovery scan or unload");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var queued = AddRemoteRun(h, "recovery-legacy", "waiting");
            h.Store.Put("job", "legacy-target", new AgentJob
            {
                Id = "legacy-target", Status = "queued", WorkerId = "local",
                ProfileId = "legacy-profile", Profile = new AgentProfile
                    { Id = "legacy-profile", WorkerId = "local", ModelWorkerId = "gb10", ModelId = "remote-model" }
            });
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Workflows!.Store.Get(queued.Id).Status == WorkflowStatus.Waiting &&
                h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "a targeted active legacy assignment blocks exceptional recovery without altering the retained workflow");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            h.Store.Put("job", "legacy-same-worker-other-model", new AgentJob
            {
                Id = "legacy-same-worker-other-model", Status = "queued", WorkerId = "gb10",
                ProfileId = "legacy-other-profile", Profile = new AgentProfile
                    { Id = "legacy-other-profile", WorkerId = "gb10", ModelWorkerId = "gb10", ModelId = "other-model", AutomaticCompaction = false }
            });
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "same-worker legacy execution blocks recovery because its provider ownership is not frozen to the target route");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            h.Store.Put("job", "legacy-unrelated-compaction", new AgentJob
            {
                Id = "legacy-unrelated-compaction", Status = "queued", WorkerId = "local",
                ProfileId = "legacy-compaction-profile", Profile = new AgentProfile
                    { Id = "legacy-compaction-profile", WorkerId = "local", ModelWorkerId = "local", ModelId = "old", AutomaticCompaction = true }
            });
            var epoch = await Pause(h);
            Check(!(await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 0,
                "legacy automatic compaction blocks recovery when no exact frozen compactor installation can be proved");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = AddRemoteRun(h, "recovery-barrier-change", "waiting");
            var epoch = await Pause(h);
            h.Remote.BeforeUnload = () => h.Answer(waiting.Id);
            var result = await Recover(h, epoch);
            Check(!result.Success && h.Workflows!.Store.Get(waiting.Id).Status == WorkflowStatus.Queued &&
                h.Remote.Calls.Count(call => call == "models.unload") == 1,
                "a retained run change after unload prevents route-fence clearance without replay");
            var blocked = await h.Call("workers.inference.generate", GenerationRequest());
            Check(!blocked.Success && h.Remote.Calls.Count(call => call == "inference.generate") == 0,
                "a failed pre-clear barrier revalidation leaves the durable provider route quarantined");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var epoch = await Pause(h);
            h.Remote.BeforeUnload = () => AddRemoteRun(h, "recovery-new-retained-run", "waiting");
            var result = await Recover(h, epoch);
            Check(!result.Success && h.Remote.Calls.Count(call => call == "models.unload") == 1,
                "a newly admitted targeted Waiting run changes the retained barrier after unload without replay");
            Check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success &&
                h.Remote.Calls.Count(call => call == "inference.generate") == 0,
                "new targeted admission before validation keeps the provider route quarantined");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            var waiting = AddRemoteRun(h, "recovery-gate", "unknown");
            var frozen = CerebralJson.Element(waiting).GetRawText();
            var epoch = await Pause(h);
            h.Remote.UnloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Remote.ReleaseUnload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = Recover(h, epoch);
            await h.Remote.UnloadStarted.Task;
            var statusRead = h.Call("engine.status", new { });
            var resume = h.Call("engine.resume", new { expectedEpoch = epoch });
            var status = await statusRead;
            await Task.Delay(40);
            Check(status.Success && status.Data.GetProperty("paused").GetBoolean() && !resume.IsCompleted,
                "engine status remains readable while resume is serialized behind remote recovery");
            h.Remote.ReleaseUnload.TrySetResult(true);
            var recovered = await recovery;
            var resumed = await resume;
            Check(recovered.Success && resumed.Success && !resumed.Data.GetProperty("paused").GetBoolean() &&
                CerebralJson.Element(h.Workflows!.Store.Get(waiting.Id)).GetRawText() == frozen,
                "one shared owner-mutation gate completes recovery before resume without changing the Unknown run");
        }

        await using (var h = new Harness(withWorkflows: true, withEngine: true))
        {
            _ = AddRemoteRun(h, "recovery-cancelled", "unknown");
            var epoch = await Pause(h);
            h.Remote.UnloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Remote.ReleaseUnload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = new CancellationTokenSource();
            var attempt = h.Commands.ExecuteAsync(new("workers.models.recover", CerebralJson.Element(new
                { workerId = "gb10", id = "remote-model", expectedEngineEpoch = epoch })), cancelled.Token);
            await h.Remote.UnloadStarted.Task; cancelled.Cancel();
            var cancelledResult = await attempt;
            h.Remote.UnloadStarted = null; h.Remote.ReleaseUnload = null;
            Check(!cancelledResult.Success && h.Remote.Calls.Count(call => call == "models.unload") == 1,
                "cancellation after dispatch returns failure without a second unload");
            Check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success &&
                h.Remote.Calls.Count(call => call == "inference.generate") == 0,
                "cancelled recovery retains the durable route fence");
            Check((await Recover(h, epoch)).Success && h.Remote.Calls.Count(call => call == "models.unload") == 2,
                "an explicit later recovery can independently unload and clear the retained fence");
        }

        await using (var h = new Harness())
        {
            using var cancelled = new CancellationTokenSource();
            var management = new RemoteWorkerManagement(h.Configuration, _ => h.Remote);
            var barrier = new RemoteModelRecoveryBarrier(1, "gb10", "remote-installation", "remote-model", []);
            var validationReached = false;
            try
            {
                _ = await management.RecoverStoppedModelAsync("gb10", "remote-model", _ => barrier, _ =>
                {
                    validationReached = true;
                    cancelled.Cancel();
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { }
            Check(validationReached && h.Remote.Calls.Count(call => call == "models.unload") == 1,
                "cancellation during the final retained-run validation cannot complete recovery or replay unload");
            Check(!(await h.Call("workers.inference.generate", GenerationRequest())).Success &&
                h.Remote.Calls.Count(call => call == "inference.generate") == 0,
                "cancellation at the destructive boundary retains the durable route fence");
        }

        Console.WriteLine($"Remote model recovery: {checks} deterministic real-engine/SQLite and fake-owner checks passed; no model inference or remote connection was used.");
        return checks;
    }

    private static Task<CommandResult> Recover(Harness harness, long epoch) => harness.Call("workers.models.recover",
        new { workerId = "gb10", id = "remote-model", expectedEngineEpoch = epoch });

    private static async Task<long> Pause(Harness harness)
    {
        var current = harness.Engine!.Status();
        var paused = await harness.Call("engine.pause", new { expectedEpoch = current.Epoch });
        if (!paused.Success) throw new InvalidOperationException(paused.Error);
        return paused.Data.GetProperty("epoch").GetInt64();
    }

    private static WorkflowRun AddRemoteRun(Harness harness, string id, string state,
        string workerId = "gb10", string? installationId = "remote-installation", string modelId = "remote-model",
        bool compactorTarget = false)
    {
        var snapshot = RecoverySnapshot(workerId, installationId, modelId, compactorTarget);
        var profile = snapshot.Profile;
        var submitted = harness.Workflows!.Store.Submit(new(id, "chat-" + id, profile.Id,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Retain exact remote recovery state.", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot)));
        if (state == "queued") return submitted;
        var claimed = harness.Workflows.Store.TryClaim(id) ?? throw new InvalidOperationException("Fixture was not claimable.");
        if (state == "running") return claimed;
        if (state == "waiting")
            return harness.Workflows.Store.ApplyStep(id, claimed.Revision,
                new(claimed.State, new AskWorkflowQuestions([new("answer", "Answer")])));
        var descriptor = new WorkflowCommandDescriptor("fixture.remote", 1, "Remote fixture",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Never);
        var prepared = harness.Workflows.Store.ApplyStep(id, claimed.Revision,
            new(claimed.State, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, WorkflowContract.EmptyObject)), descriptor);
        if (state == "prepared") return prepared;
        if (state == "waiting-prepared")
            return harness.Workflows.Store.Hold(id, prepared.Revision,
                "Retain prepared intent while the engine is unavailable.");
        var dispatched = harness.Workflows.Store.DispatchEffect(id, prepared.Revision);
        if (state == "dispatched") return dispatched;
        if (state == "unknown")
            return harness.Workflows.Store.CompleteEffect(id, dispatched.Effect!.Id,
                new(WorkflowEffectState.Unknown, Error: "Fixture provider outcome is unknown."));
        throw new ArgumentException("Unknown fixture state: " + state);
    }

    private static WorkerExecutionSnapshot.Snapshot RecoverySnapshot(string workerId, string? installationId,
        string modelId, bool compactorTarget = false)
    {
        var profile = new AgentProfile { Id = "lead", Name = "Lead", Role = "lead", WorkerId = "local",
            ModelId = compactorTarget ? "old" : modelId, ModelWorkerId = compactorTarget ? "local" : workerId };
        var model = new WorkerExecutionSnapshot.ModelBinding(compactorTarget ? "old" : modelId,
            compactorTarget ? "old.gguf" : "/srv/cerebral/models/model.gguf", "alpaca", [], 8192, 1, -1,
            ModelWorkerId: compactorTarget ? "local" : workerId,
            ModelWorkerInstallationId: compactorTarget ? null : installationId);
        WorkerExecutionSnapshot.CompactorBinding? compactor = null;
        if (compactorTarget)
        {
            var compactorProfile = new AgentProfile { Id = "compactor", Name = "Compactor", Role = "compactor",
                WorkerId = "local", ModelId = modelId, ModelWorkerId = workerId };
            compactor = new(compactorProfile, "Compact retained context.", new(modelId,
                "/srv/cerebral/models/model.gguf", "alpaca", [], 8192, 1, -1,
                ModelWorkerId: workerId, ModelWorkerInstallationId: installationId));
        }
        return new(WorkerExecutionSnapshot.Version, profile, "Shared", "Role", model, [], [], [], [], false,
            "fixture-routing", [], [], compactor);
    }
}
