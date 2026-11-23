using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Contracts;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException) { checks++; return; }
    throw new InvalidOperationException(message);
}
string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var workspace = args.Single();
Directory.CreateDirectory(workspace);
var root = Path.Combine(workspace, "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Pack(string folder, string slot, string mode = "echo", bool defaultEngine = false, string? entry = null)
{
    var directory = Path.Combine(folder, slot); Directory.CreateDirectory(directory);
    var dll = defaultEngine ? "Cerebral.Engine.Default.dll" : "Cerebral.EngineFixture.dll";
    File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", dll), Path.Combine(directory, dll), true);
    var config = Encoding.UTF8.GetBytes("[settings]\nmode=" + mode + "\n");
    File.WriteAllBytes(Path.Combine(directory, "engine-settings.ini"), config);
    File.WriteAllText(Path.Combine(directory, "engine.ini"), $"[engine]\nid={(defaultEngine ? "pegasus.default-engine" : "fixture.engine")}\nversion=1.0.0\nprotocol=pegasus.cerebral.engine/1\nassembly={dll}\nassembly-sha256={Sha(File.ReadAllBytes(Path.Combine(directory, dll)))}\nentry-type={entry ?? (defaultEngine ? "Cerebral.Engine.Default.DefaultEnginePlugin" : "Cerebral.EngineFixture.FixtureEngine")}\nconfiguration-sha256={Sha(config)}\n", new UTF8Encoding(false));
    return directory;
}
WorkflowSubmission Submission(string id) => new(id, "chat", "profile", "fixture:work", "1", "test", null,
    WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
try
{
    // Actual separately compiled default and fixture DLLs; actual collectible load contexts and local SQLite.
    var packages = Path.Combine(root, "packages"); var data = Path.Combine(root, "data");
    Pack(packages, "A", defaultEngine: true); Pack(packages, "B");
    await using var controller = new EngineController(packages, data);
    await controller.InitializeAsync();
    Check(!controller.IsPaused && controller.Status().Active?.Slot == "A", "Default engine must bootstrap ready.");
    Check(controller.Status().Epoch == 1 && !controller.Status().CandidatePending, "Bootstrap is one known-good generation.");
    var journal = new EventJournal(Path.Combine(root, "journal.db"));
    var store = new WorkflowStore(journal);
    var handler = new Handler(); var definition = new Definition();
    var registry = new WorkflowRegistry([new Module(definition, handler)]);
    var runtime = new WorkflowRuntime(store, registry, engine: controller);
    store.Submit(Submission("paused-work"));
    controller.Pause(1);
    Check((await runtime.RunAsync("paused-work")).Status == WorkflowStatus.Queued && handler.Calls == 0, "Paused work must remain durably queued without execution.");
    await Reject(() => Task.FromResult(controller.Resume(0)), "Stale owner epoch must reject.");
    controller.Resume(1);
    var completed = await runtime.RunAsync("paused-work");
    Check(completed.Status == WorkflowStatus.Completed && handler.Calls == 1, "Default engine must retain existing workflow behavior.");
    Check(controller.Status().CommittedDecisions == 2, "A command and completion must each commit through the engine fence.");
    controller.Pause(1);
    await controller.ActivateAsync("B", 1);
    Check(controller.Status().Epoch == 2 && controller.Status().CandidatePending, "Candidate activation must advance epoch and preserve fallback.");
    Check(controller.Status().Slots.All(x => !x.BuildAllowed), "Both active candidate and fallback must be protected.");
    await Reject(() => Task.FromResult(controller.Promote(2)), "Startup must not count as successful canary work.");
    await Reject(() => controller.ActivateAsync("A", 2), "Unconfirmed candidate must not overwrite fallback.");
    controller.Resume(2); store.Submit(Submission("candidate-work"));
    Check((await runtime.RunAsync("candidate-work")).Status == WorkflowStatus.Completed, "Alternate compiled engine must use the same feature module.");
    controller.Promote(2);
    Check(!controller.Status().CandidatePending && controller.Status().KnownGoodSlot == "B", "Explicit promotion must make candidate known good.");
    Check(controller.Status().Slots.Single(x => x.Slot == "A").BuildAllowed, "Prior slot is reusable only after promotion.");
    controller.Pause(2); await controller.ActivateAsync("A", 2); controller.Resume(3);
    store.Submit(Submission("latest-before-rollback")); await runtime.RunAsync("latest-before-rollback");
    var latestRevision = store.Get("latest-before-rollback").Revision;
    controller.Pause(3); await controller.RollbackAsync(3);
    Check(controller.Status().Active?.Slot == "B" && controller.Status().Epoch == 4, "Rollback must reload the pinned fallback.");
    Check(store.Get("latest-before-rollback").Revision == latestRevision, "Rollback must preserve the latest host journal.");
    foreach (var mode in new[] { "fail-initialize", "fail-health" })
    {
        Pack(packages, "A", mode);
        await Reject(() => controller.ActivateAsync("A", 4), "Bad candidate must fail preflight.");
        Check(controller.Status().Active?.Slot == "B" && controller.Status().Epoch == 4, "Preflight failure must preserve active generation.");
    }
    Pack(packages, "A", entry: "Cerebral.EngineFixture.IncompatibleEngine");
    await Reject(() => controller.ActivateAsync("A", 4), "Incompatible SPI/checkpoint must reject before initialization.");
    var badPack = Pack(packages, "A");
    File.AppendAllText(Path.Combine(badPack, "engine-settings.ini"), "changed=yes\n");
    await Reject(() => controller.ActivateAsync("A", 4), "Unpinned configuration changes must reject.");
    var manifest = Path.Combine(Pack(packages, "A"), "engine.ini");
    File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("assembly=Cerebral.EngineFixture.dll", "assembly=../Cerebral.EngineFixture.dll"));
    await Reject(() => controller.ActivateAsync("A", 4), "Traversal manifest must reject.");

    // A paused in-flight evaluation cannot be discarded or overlapped by a replacement.
    Pack(packages, "A"); controller.Resume(4);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var evaluation = controller.EvaluateAsync("lease", 9, "cerebral.workflow.transition", 1, WorkflowContract.EmptyObject, async token =>
    { entered.SetResult(); await release.Task; return WorkflowContract.EmptyObject; }, CancellationToken.None);
    await entered.Task; controller.Pause(4);
    Check(controller.Status().ActiveEvaluations == 1, "The actual evaluation lease must remain visible.");
    await Reject(() => controller.ActivateAsync("A", 4), "An active evaluation must prevent switching.");
    release.SetResult();
    using (var lease = await evaluation)
    {
        lease.Commit(_ => true);
        await Reject(() => Task.FromResult(lease.Commit(_ => true)), "A decision may commit only once.");
        await Reject(() => controller.ActivateAsync("A", 4), "An un-released commit lease must still prevent switching.");
    }
    Check(controller.Status().ActiveEvaluations == 0, "Disposed lease must release its issue binding.");

    // Sibling decisions must not publish after one candidate evaluation faults. Repeated faults do not
    // impersonate an owner pause; automatic recovery resumes only when the owner did not pause it.
    await controller.ActivateAsync("A", 4); controller.Resume(5);
    using (var sibling = await controller.EvaluateAsync("sibling", 1, "cerebral.workflow.transition", 1, WorkflowContract.EmptyObject, _ => Task.FromResult(WorkflowContract.EmptyObject), CancellationToken.None))
    using (var failed = await controller.EvaluateAsync("failure", 1, "cerebral.workflow.transition", 1, WorkflowContract.EmptyObject, _ => Task.FromResult(WorkflowContract.EmptyObject), CancellationToken.None))
    {
        failed.Reject("First candidate failure."); failed.Reject("Concurrent candidate failure.");
        await Reject(() => Task.FromResult(sibling.Commit(_ => true)), "A sibling cannot commit after its generation faults.");
        Check(!await controller.RecoverAsync(), "Recovery must wait for every outstanding evaluation lease.");
    }
    Check(await controller.RecoverAsync() && !controller.IsPaused && controller.Status().Epoch == 6, "Concurrent faults must preserve automatic resume intent.");
    controller.Pause(6);

    // Malfunctioning candidates hold work, rather than falsifying success or losing the user's request.
    foreach (var mode in new[] { "fail-evaluate", "stale-epoch", "alter-request", "bad-payload" })
    {
        Pack(packages, "A", mode);
        var epoch = controller.Status().Epoch;
        await controller.ActivateAsync("A", epoch); controller.Resume(epoch + 1);
        var id = "bad-" + mode; store.Submit(Submission(id)); var before = handler.Calls;
        var held = await runtime.RunAsync(id);
        Check(held.Status == WorkflowStatus.Queued && held.Effect is null && handler.Calls == before, "Engine failure must preserve accepted work without dispatch.");
        Check(await controller.RecoverAsync(), "Host lifetime recovery must restore a failed candidate.");
        Check(!controller.IsPaused && controller.Status().Recovery is not null && controller.Status().ActiveEvaluations == 0 && controller.Status().Active?.Slot == "B" && controller.Status().Epoch == epoch + 2, "Faulted candidate must restore its pinned fallback after leases settle and retain recovery diagnostics.");
        Check((await runtime.RunAsync(id)).Status == WorkflowStatus.Completed && handler.Calls == before + 1, "Pinned fallback must resume the same request exactly once.");
        controller.Pause(epoch + 2);
    }

    // Pause after decision, before dispatch: retain prepared effect, do not ask a new engine to recreate it.
    Pack(packages, "A"); var currentEpoch = controller.Status().Epoch; controller.Resume(currentEpoch);
    definition.BeforeReturn = () => { definition.BeforeReturn = null; controller.Pause(currentEpoch); };
    store.Submit(Submission("prepared-boundary")); var prepared = await runtime.RunAsync("prepared-boundary");
    var preparedId = prepared.Effect?.Id; var beforePrepared = handler.Calls;
    Check(prepared.Status == WorkflowStatus.Queued && prepared.Effect?.State == WorkflowEffectState.Prepared, "Safe pause must preserve a prepared intent.");
    await controller.ActivateAsync("A", currentEpoch); controller.Resume(currentEpoch + 1);
    var afterPrepared = await runtime.RunAsync("prepared-boundary");
    Check(afterPrepared.Status == WorkflowStatus.Completed && handler.Calls == beforePrepared + 1 && handler.LastEffect == preparedId, "Replacement must dispatch the exact old prepared effect once.");
    controller.Pause(currentEpoch + 1); await controller.RollbackAsync(currentEpoch + 1); controller.Resume(currentEpoch + 2);

    // Real host handler task outlives the engine. Its late receipt is still authoritative.
    handler.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    handler.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    store.Submit(Submission("late-receipt")); var lateRun = runtime.RunAsync("late-receipt");
    await handler.Entered.Task; var dispatchedId = store.Get("late-receipt").Effect!.Id;
    currentEpoch = controller.Status().Epoch; controller.Pause(currentEpoch);
    Check(controller.Status().ActiveEvaluations == 0 && store.Get("late-receipt").Effect!.State == WorkflowEffectState.Dispatched, "Host effect and engine evaluation leases must remain distinct.");
    await controller.ActivateAsync("A", currentEpoch); controller.Resume(currentEpoch + 1);
    handler.Release.SetResult(); var lateCompleted = await lateRun;
    Check(lateCompleted.Status == WorkflowStatus.Completed && handler.LastEffect == dispatchedId, "Late receipt must settle the same effect under the new engine.");
    Check(lateCompleted.ActiveMilliseconds > 0, "Switch must retain actual effect accounting.");
    handler.Release = null; handler.Entered = null;
    controller.Pause(currentEpoch + 1); await controller.RollbackAsync(currentEpoch + 1);

    // In-memory code does not hold a file lock; ALC release is observed after its references disappear.
    var replacementFile = Path.Combine(packages, "A", "Cerebral.EngineFixture.dll");
    using (var unlocked = new FileStream(replacementFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(unlocked.Length > 0, "Retired code must be available for rebuilding without closing the host.");
    // Let synchronous async continuations unwind before probing collection. Unload is cooperative;
    // probing from inside the completing activation call's stack would keep its old generation alive.
    await Task.Delay(20);
    for (var i = 0; i < 12 && controller.Status().UnreleasedContexts > 0; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    Check(controller.Status().UnreleasedContexts == 0, "Retired engine load contexts must actually become collectible.");

    // Reopen unpromoted candidate must prefer exact last known-good code, while retaining host data.
    var restartPackages = Path.Combine(root, "restart-packages"); var restartData = Path.Combine(root, "restart-data");
    Pack(restartPackages, "A", defaultEngine: true); Pack(restartPackages, "B");
    await using (var first = new EngineController(restartPackages, restartData))
    { await first.InitializeAsync(); first.Pause(1); await first.ActivateAsync("B", 1); }
    await using (var reopened = new EngineController(restartPackages, restartData))
    {
        await reopened.InitializeAsync();
        Check(reopened.Status().Active?.Slot == "A" && !reopened.Status().CandidatePending, "Restart must not promote an unconfirmed candidate.");
        Check(reopened.Status().Epoch == 3 && reopened.IsPaused, "Restart fallback must advance epoch and preserve owner pause.");
    }
    // Feature failures are ordinary work evidence, and an explicit owner pause wins over fault recovery.
    var boundaryPackages = Path.Combine(root, "boundary-packages");
    Pack(boundaryPackages, "A"); Pack(boundaryPackages, "B");
    await using (var boundary = new EngineController(boundaryPackages, Path.Combine(root, "boundary-data")))
    {
        await boundary.InitializeAsync(); boundary.Pause(1); await boundary.ActivateAsync("B", 1); boundary.Resume(2);
        await Reject(async () => { using var lease = await boundary.EvaluateAsync("feature-failure", 1,
            "cerebral.workflow.transition", 1, WorkflowContract.EmptyObject,
            _ => Task.FromException<JsonElement>(new InvalidOperationException("Ordinary feature failure.")), CancellationToken.None); },
            "Feature failure must remain a failure rather than yielding a successful decision.");
        Check(!boundary.IsPaused && boundary.Status().Error is null && boundary.Status().CandidatePending && boundary.Status().Epoch == 2,
            "An ordinary feature failure must not fault or replace the selected engine.");
        Check(!await boundary.RecoverAsync(), "An ordinary feature failure must not trigger automatic engine recovery.");
        using (var lease = await boundary.EvaluateAsync("owner-pause", 1, "cerebral.workflow.transition", 1,
                   WorkflowContract.EmptyObject, _ => Task.FromResult(WorkflowContract.EmptyObject), CancellationToken.None))
            lease.Reject("Candidate failure before owner pause.");
        boundary.Pause(2);
        Check(await boundary.RecoverAsync() && boundary.Status().Active?.Slot == "A" && boundary.IsPaused,
            "An explicit owner pause must survive automatic fallback instead of being silently resumed.");
    }

    // Cancellation during loading the fallback must not consume the single automatic recovery attempt.
    var cancellationPackages = Path.Combine(root, "cancellation-packages");
    Pack(cancellationPackages, "A", "slow-health"); Pack(cancellationPackages, "B", "fail-evaluate");
    await using (var cancellation = new EngineController(cancellationPackages, Path.Combine(root, "cancellation-data")))
    {
        await cancellation.InitializeAsync(); cancellation.Pause(1); await cancellation.ActivateAsync("B", 1); cancellation.Resume(2);
        await Reject(async () => { using var lease = await cancellation.EvaluateAsync("cancelled-recovery", 1,
            "cerebral.workflow.transition", 1, WorkflowContract.EmptyObject,
            _ => Task.FromResult(WorkflowContract.EmptyObject), CancellationToken.None); }, "The candidate must fault before testing recovery cancellation.");
        var wasCancelled = false;
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            try { await cancellation.RecoverAsync(stop.Token); } catch (OperationCanceledException) { wasCancelled = true; }
        Check(wasCancelled && cancellation.Status().Epoch == 2 && cancellation.Status().CandidatePending && cancellation.IsPaused,
            "Cancelled fallback loading must preserve the failed candidate and the pinned fallback for retry.");
        Check(await cancellation.RecoverAsync() && cancellation.Status().Active?.Slot == "A" &&
              cancellation.Status().Epoch == 3 && !cancellation.IsPaused,
            "A new host-lifetime recovery attempt must succeed after prepublication cancellation.");
    }
    var missingPackages = Path.Combine(root, "missing-packages");
    await using (var missing = new EngineController(missingPackages, Path.Combine(root, "missing-data")))
    { await missing.InitializeAsync(); Check(missing.IsPaused && missing.Status().Active is null && missing.Status().Error is not null, "Missing engine must remain diagnosable without failing host construction."); }
    Console.WriteLine($"Engine activation checks passed: {checks} (actual compiled engine loading/unloading and local SQLite; deterministic feature/handler fixtures, no model inference or installed UI evidence).");
}
finally { Directory.Delete(root, true); }

sealed class Definition : IWorkflowDefinition
{
    public Action? BeforeReturn;
    public WorkflowDefinitionDescriptor Descriptor => new("fixture:work", "1", "Engine acceptance fixture", WorkflowContract.EmptyObject, []);
    public WorkflowStep Advance(WorkflowRun run)
    {
        var step = run.Signal.Kind == WorkflowSignalKind.Start
            ? new WorkflowStep(WorkflowContract.EmptyObject, new InvokeWorkflowCommand("fixture:effect", 1, WorkflowContract.EmptyObject))
            : new WorkflowStep(WorkflowContract.EmptyObject, new CompleteWorkflow(JsonSerializer.SerializeToElement(new { completed = true })));
        BeforeReturn?.Invoke(); return step;
    }
}
sealed class Handler : IWorkflowCommandHandler
{
    public int Calls; public string? LastEffect;
    public TaskCompletionSource? Entered; public TaskCompletionSource? Release;
    public WorkflowCommandDescriptor Descriptor => new("fixture:effect", 1, "Acceptance effect", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], TimeoutSeconds: 30);
    public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
    { Calls++; LastEffect = context.EffectId; Entered?.SetResult(); if (Release is not null) await Release.Task; return new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject); }
}
sealed class Module(Definition definition, Handler handler) : IWorkflowModule
{ public string Id => "fixture"; public IReadOnlyList<IWorkflowDefinition> Workflows => [definition]; public IReadOnlyList<IWorkflowCommandHandler> Commands => [handler]; }
