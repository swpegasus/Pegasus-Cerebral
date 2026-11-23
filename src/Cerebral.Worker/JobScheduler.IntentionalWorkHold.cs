using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>One bounded owner marker, separate from ordinary engine pause and model resource policy.</summary>
public sealed record IntentionalWorkHoldRecord
{
    public int Version { get; init; } = 1;
    public string HoldId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public long Generation { get; init; }
    public long Revision { get; init; }
    public long RequestedEpoch { get; init; }
    public long PriorGeneration { get; init; }
    public long PriorRevision { get; init; }
    public string Mode { get; init; } = "";
    public string State { get; init; } = "holding";
    public bool PauseCommitted { get; init; }
    public IReadOnlyList<IntentionalHeldWork> Work { get; init; } = [];
    public string? Fault { get; init; }
}

public sealed record IntentionalHeldWork(string Kind, string Id, string ConversationId,
    bool WasExecuting);
public sealed record IntentionalHeldWorkStatus(string Kind, string Id, string ConversationId,
    string Status, long? Revision, string? EffectState, bool IsExecuting, bool RequiresReview);
public sealed record IntentionalWorkHoldStatus(string Contract, string? HoldId, string? RequestId,
    long Generation, long Revision, string? Mode, string State, long EngineEpoch, bool EnginePaused,
    bool StartupPromptRequired, IReadOnlyList<IntentionalHeldWorkStatus> Work, string? Fault, string Detail);

public sealed partial class JobScheduler
{
    private const string WorkHoldKind = "owner-work-hold";
    private const string WorkHoldId = "intentional";
    private const int MaximumHeldWork = 200;
    private const string InterruptedWorkflowReason = "Worker execution was interrupted before the next effect. The accepted work can be resumed.";
    private readonly Dictionary<string, CancellationTokenSource> intentionalStops = new(StringComparer.Ordinal);

    private static bool ActiveHold(IntentionalWorkHoldRecord? record) =>
        record?.State is "holding" or "resuming" or "cancelling" or "faulted";

    // Malformed durable intent fails closed rather than opening the scheduler after restart.
    private IntentionalWorkHoldRecord? ReadIntentionalHold()
    {
        var value = store.Get<IntentionalWorkHoldRecord>(WorkHoldKind, WorkHoldId);
        if (value is null) return null;
        if (value.Version != 1 || !Guid.TryParseExact(value.HoldId, "N", out _) ||
            !Guid.TryParseExact(value.RequestId, "N", out _) || value.Generation < 1 || value.Revision < 1 ||
            value.RequestedEpoch < 0 || value.PriorGeneration < 0 || value.PriorRevision < 0 ||
            value.Mode is not ("stop-now" or "finish-current") ||
            value.State is not ("holding" or "resuming" or "cancelling" or "resumed" or "cancelled" or "faulted") ||
            value.Work is null || value.Work.Count > MaximumHeldWork ||
            value.Work.Any(x => x is null || x.Kind is not ("workflow" or "job") ||
                !ValidHoldText(x.Id) || !ValidHoldText(x.ConversationId)) ||
            value.Work.Select(x => x.Kind + ":" + x.Id).Distinct(StringComparer.Ordinal).Count() != value.Work.Count ||
            value.Fault?.Length > 256)
            throw new InvalidDataException("The intentional work hold is invalid; admission remains closed. Inspect its saved owner record.");
        return value;
    }

    private static bool ValidHoldText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Contains('\0');
    private bool IntentionalHoldBlocksAdmission() => ActiveHold(ReadIntentionalHold());
    public bool HasIntentionalWorkHold() => store.Locked(IntentionalHoldBlocksAdmission);

    public IntentionalWorkHoldStatus GetIntentionalWorkHoldStatus()
    {
        var controller = workflows?.Engine ?? throw new InvalidOperationException("Intentional work holds require the replaceable engine.");
        var engineStatus = controller.Status();
        return store.Locked(() => ProjectIntentionalHold(ReadIntentionalHold(), engineStatus.Epoch, engineStatus.Paused));
    }

    private IntentionalWorkHoldStatus ProjectIntentionalHold(IntentionalWorkHoldRecord? marker, long epoch, bool paused)
    {
        if (marker is null)
            return new("cerebral.owner-work-hold/1", null, null, 0, 0, null, "none", epoch, paused, false, [], null,
                "There is no intentionally held work.");
        var work = marker.Work.Select(HeldWorkStatus).ToArray();
        var state = marker.State == "holding" && marker.PauseCommitted && work.All(x => !x.IsExecuting)
            ? "held" : marker.State;
        var remaining = work.Any(x => x.Status is not ("Completed" or "Failed" or "Cancelled" or "completed" or "failed" or "cancelled"));
        return new("cerebral.owner-work-hold/1", marker.HoldId, marker.RequestId, marker.Generation, marker.Revision,
            marker.Mode, state, epoch, paused, ActiveHold(marker) && remaining, Array.AsReadOnly(work), marker.Fault,
            marker.State == "cancelled"
                ? "Captured work was explicitly cancelled. The engine remains paused; uncertain effects retain their receipts."
                : marker.State == "resumed"
                    ? "The hold was released. Uncertain effects and interrupted legacy work still require review; they were not replayed."
                    : "Modern workflows finish the current operation before holding. Legacy work can only finish its current assignment. " +
                      "Stop now interrupts local execution; it does not confirm that remote work stopped. Uncertain receipts require review.");
    }

    private IntentionalHeldWorkStatus HeldWorkStatus(IntentionalHeldWork captured)
    {
        var key = captured.Kind == "workflow" ? "workflow:" + captured.Id : captured.Id;
        var executing = running.ContainsKey(key);
        if (captured.Kind == "workflow")
        {
            var run = workflows!.Store.TryGet(captured.Id);
            var uncertain = run?.Effect?.State is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown;
            var review = run is null || uncertain || run.Status == WorkflowStatus.Waiting &&
                run.WaitingReason != InterruptedWorkflowReason;
            return new(captured.Kind, captured.Id, captured.ConversationId, run?.Status.ToString() ?? "missing",
                run?.Revision, run?.Effect?.State.ToString(), executing, review);
        }
        var job = store.Get<AgentJob>("job", captured.Id);
        return new(captured.Kind, captured.Id, captured.ConversationId, job?.Status ?? "missing",
            null, null, executing, job is null || job.Status is "interrupted" or "remote-unconfirmed" or "waiting-human" or "waiting-helper");
    }

    private IReadOnlyList<IntentionalHeldWork> CaptureIntentionalWork()
    {
        var captured = new Dictionary<string, IntentionalHeldWork>(StringComparer.Ordinal);
        void Add(IntentionalHeldWork value)
        {
            captured[value.Kind + ":" + value.Id] = value;
            if (captured.Count > MaximumHeldWork)
                throw new InvalidOperationException("More than 200 ready/running assignments need holding. Narrow the queue before retrying; no hold was created.");
        }
        // Compact pages exclude private context and result payloads. Inactive waiting work is not a startup candidate.
        long after = 0;
        for (var pageNumber = 0; ; pageNumber++)
        {
            if (pageNumber == 50) throw new InvalidOperationException("Active-work inspection exceeded its bounded page limit; no hold was created.");
            var page = workflows!.Store.ListActiveSummaries(200, after);
            foreach (var run in page.Runs.Where(x => x.Status is WorkflowStatus.Queued or WorkflowStatus.Running))
                Add(new("workflow", run.Id, run.ConversationId, running.ContainsKey("workflow:" + run.Id)));
            if (page.NextAfterRow is not { } next) break;
            after = next;
        }
        after = 0;
        for (var pageNumber = 0; ; pageNumber++)
        {
            if (pageNumber == 50) throw new InvalidOperationException("Legacy-work inspection exceeded its bounded page limit; no hold was created.");
            var page = store.ActiveJobSummaries(200, after);
            foreach (var job in page.Jobs.Where(x => x.Status is "queued" or "running" || running.ContainsKey(x.Id)))
                Add(new("job", job.Id, job.SessionId, running.ContainsKey(job.Id)));
            if (page.NextAfterRow is not { } next) break;
            after = next;
        }
        // Includes a reconciliation execution already admitted before the barrier, never old inactive Unknown rows.
        foreach (var key in running.Keys.Where(x => x != WorkerWorkflows.IdleCompactionExecutionKey))
        {
            if (key.StartsWith("workflow:", StringComparison.Ordinal))
            {
                var id = key["workflow:".Length..]; var run = workflows!.Store.Get(id);
                Add(new("workflow", id, run.Submission.ConversationId, true));
            }
            else if (store.Get<AgentJob>("job", key) is { } job)
                Add(new("job", key, job.SessionId, true));
        }
        return captured.Values.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public IntentionalWorkHoldStatus HoldIntentionalWork(string requestId, string mode, long expectedEpoch,
        long expectedGeneration, long expectedRevision)
    {
        if (!Guid.TryParseExact(requestId, "N", out _) || mode is not ("stop-now" or "finish-current") ||
            expectedEpoch < 0 || expectedGeneration < 0 || expectedRevision < 0)
            throw new ArgumentException("Intentional hold requires UUID-N requestId, stop-now/finish-current mode and nonnegative comparisons.");
        var controller = workflows?.Engine ?? throw new InvalidOperationException("Intentional work holds require the replaceable engine.");
        var engineStatus = controller.Status();
        List<KeyValuePair<string, CancellationTokenSource>> capturedTokens = [];
        var marker = store.Locked(() =>
        {
            var prior = ReadIntentionalHold();
            if (prior?.RequestId == requestId)
            {
                if (prior.Mode != mode || prior.RequestedEpoch != expectedEpoch ||
                    prior.PriorGeneration != expectedGeneration || prior.PriorRevision != expectedRevision)
                    throw new InvalidOperationException("Hold requestId was already used for different arguments.");
                return prior;
            }
            if (engineStatus.Epoch != expectedEpoch || (prior?.Generation ?? 0) != expectedGeneration ||
                (prior?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("Intentional hold changed; read engine.work-hold.status before retrying.");
            if (ActiveHold(prior)) throw new InvalidOperationException("An intentional work hold already owns the admission barrier.");
            var value = new IntentionalWorkHoldRecord
            {
                HoldId = Guid.NewGuid().ToString("N"), RequestId = requestId,
                Generation = checked(expectedGeneration + 1), Revision = 1, RequestedEpoch = expectedEpoch,
                PriorGeneration = expectedGeneration, PriorRevision = expectedRevision,
                Mode = mode, Work = CaptureIntentionalWork()
            };
            capturedTokens = running.ToList();
            // Same lock as Claim/StartWorkflows closes the check-then-claim race before engine persistence.
            store.Put(WorkHoldKind, WorkHoldId, value);
            return value;
        });
        if (marker.PauseCommitted || marker.State != "holding") return GetIntentionalWorkHoldStatus();
        try
        {
            controller.Pause(expectedEpoch);
            marker = store.Locked(() => WriteIntentionalHold(marker with { PauseCommitted = true, Revision = marker.Revision + 1 }));
        }
        catch (Exception error) when (IsWorkflowRunnerFailure(error))
        {
            store.Locked(() => WriteIntentionalHold(marker with { State = "faulted", Revision = marker.Revision + 1,
                Fault = "Engine pause failed (" + error.GetType().Name + "); admission stays held." }));
            return GetIntentionalWorkHoldStatus();
        }
        if (mode == "stop-now")
        {
            foreach (var pair in capturedTokens)
            {
                var cancel = store.Locked(() =>
                {
                    if (!running.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value)) return false;
                    if (pair.Key != WorkerWorkflows.IdleCompactionExecutionKey)
                        intentionalStops[pair.Key] = pair.Value;
                    return true;
                });
                if (!cancel) continue;
                // Cancellation callbacks can touch the store; never run them inside its transaction.
                try { pair.Value.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (AggregateException error)
                {
                    logger.LogError("Intentional stop callback failed ({FailureKind}).", WorkflowRunnerFailureKind(error));
                }
            }
        }
        return GetIntentionalWorkHoldStatus();
    }

    private IntentionalWorkHoldRecord WriteIntentionalHold(IntentionalWorkHoldRecord value)
    { store.Put(WorkHoldKind, WorkHoldId, value); return value; }

    private bool IsIntentionalStop(string key, CancellationTokenSource cts) =>
        store.Locked(() => intentionalStops.TryGetValue(key, out var owned) && ReferenceEquals(owned, cts));
    private void ReleaseIntentionalStop(string key, CancellationTokenSource cts)
    {
        if (intentionalStops.TryGetValue(key, out var owned) && ReferenceEquals(owned, cts))
            intentionalStops.Remove(key);
    }

    public IntentionalWorkHoldStatus ResolveIntentionalWorkHold(string holdId, long expectedEpoch,
        long expectedGeneration, long expectedRevision, bool cancel)
    {
        var controller = workflows?.Engine ?? throw new InvalidOperationException("Intentional work holds require the replaceable engine.");
        var engineStatus = controller.Status();
        var marker = store.Locked(() =>
        {
            var current = ReadIntentionalHold() ?? throw new InvalidOperationException("No intentional work hold exists.");
            if (current.HoldId != holdId || current.Generation != expectedGeneration ||
                current.Revision != expectedRevision || engineStatus.Epoch != expectedEpoch || !ActiveHold(current))
                throw new InvalidOperationException("Intentional hold changed; read engine.work-hold.status before resolving it.");
            if (!cancel && current.Work.Any(x => running.ContainsKey(x.Kind == "workflow" ? "workflow:" + x.Id : x.Id)))
                throw new InvalidOperationException("Captured execution is still holding. Wait for held state before resuming.");
            return WriteIntentionalHold(current with { State = cancel ? "cancelling" : "resuming",
                Revision = current.Revision + 1, Fault = null });
        });
        try
        {
            // Reassert the durable engine barrier when recovering a crash/failure between marker and pause writes.
            controller.Pause(expectedEpoch);
            if (cancel)
            {
                foreach (var item in marker.Work)
                {
                    if (item.Kind == "workflow") CancelWorkflow(item.Id);
                    else
                    {
                        var job = Cancel(item.Id);
                        if (job.Status == "interrupted")
                            store.Locked(() => {
                                var saved = Get(item.Id);
                                saved.CancelRequested = true; saved.Status = "cancelled";
                                saved.WaitingReason = "Owner cancelled intentionally held legacy work; prior tool effects still require inspection.";
                                saved.FinishedAt = DateTimeOffset.UtcNow; Save(saved); return true;
                            });
                    }
                }
            }
            else
            {
                foreach (var item in marker.Work.Where(x => x.Kind == "workflow" && x.WasExecuting))
                {
                    var run = workflows!.Store.TryGet(item.Id);
                    // Resume only our normal cancellation yield. Legitimate questions/dependencies/errors remain waiting.
                    if (run is { Status: WorkflowStatus.Waiting, CancelRequested: false } &&
                        run.WaitingReason == InterruptedWorkflowReason &&
                        run.Effect?.State is not (WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched))
                        workflows.Publish(workflows.Store.Resume(run.Id, run.Revision));
                }
                if (marker.Mode == "stop-now")
                    foreach (var item in marker.Work.Where(x => x.Kind == "job" && x.WasExecuting))
                        store.Locked(() => {
                            var job = Get(item.Id);
                            if (job.Status == "remote-unconfirmed") { job.NextReconcileAt = null; Save(job); }
                            return true;
                        });
                controller.Resume(expectedEpoch);
            }
            store.Locked(() => WriteIntentionalHold(marker with { State = cancel ? "cancelled" : "resumed",
                PauseCommitted = true, Revision = marker.Revision + 1 }));
        }
        catch (Exception error) when (IsWorkflowRunnerFailure(error))
        {
            store.Locked(() => WriteIntentionalHold(marker with { State = "faulted", Revision = marker.Revision + 1,
                Fault = "Hold resolution failed (" + error.GetType().Name + "); admission stays held." }));
        }
        return GetIntentionalWorkHoldStatus();
    }
}
