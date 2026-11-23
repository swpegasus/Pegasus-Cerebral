using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Controlled observer-gate fixtures against the compiled Worker delivery implementation.</summary>
public static class CommittedRootUserInputDeliveryChecks
{
    private static int checks;
    public static async Task<int> RunAsync()
    {
        checks = 0;
        await SourceAdmission();
        await HostScopeAndUnicode();
        await FanOutIndependentLeases();
        await TimeoutKeepsExactLease();
        await FullQueueDoesNotWait();
        await StopRetainsActiveLeaseAndDrainsPending();
        InvalidDeadlineStartsNoCallback();
        return checks;
        
    }

static CommittedRootUserInputReceipt Receipt(string suffix = "1", string? text = null) => new(
    "request-" + suffix, "conversation", "profile", "pegasus.conversation:chat", "1.0.1", null,
    10, "event-" + suffix, "request-" + suffix, 11, CerebralEventTypes.Message,
    EventActorKind.User, EventSourceKind.User, null, text ?? "hello");

static void Require(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    Interlocked.Increment(ref checks);
}

static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static async Task StopAndSettle(CommittedRootUserInputDelivery delivery)
{
    await delivery.DisposeAsync();
    await delivery.Settlement.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task SourceAdmission()
{
    var seen = Signal();
    var gate = new Gate((_, _) => { seen.TrySetResult(true); return null; });
    await using var delivery = new CommittedRootUserInputDelivery(gate);
    var valid = Receipt();
    Require(delivery.TryEnqueue(valid) == CommittedRootUserInputEnqueueResult.Queued, "winning User queued");
    Require(delivery.TryEnqueue(valid) == CommittedRootUserInputEnqueueResult.Duplicate, "same event duplicate omitted");
    Require(delivery.TryEnqueue(valid with { ParentRunId = "parent", EventId = "child" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "child omitted");
    Require(delivery.TryEnqueue(valid with { ActorKind = EventActorKind.Agent, EventId = "agent" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "Agent omitted");
    Require(delivery.TryEnqueue(valid with { SourceKind = EventSourceKind.Service, EventId = "service" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "Service source omitted");
    Require(delivery.TryEnqueue(valid with { EventType = "workflow.assignment", EventId = "assignment" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "non-message omitted");
    Require(delivery.TryEnqueue(valid with { EventTurnId = "wrong", EventId = "turn" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "wrong turn omitted");
    Require(delivery.TryEnqueue(valid with { EventSequence = 12, EventId = "later" }) ==
        CommittedRootUserInputEnqueueResult.Ineligible, "non-adjacent event omitted");
    // Coherent binding alone does not decide first-in-conversation. Engine must
    // return no receipt for a later root User even when this predicate is true.
    var laterUser = valid with { RequestId = "later-request", EventId = "later-event",
        EventTurnId = "later-request", ConversationSequenceBeforeMessage = 20, EventSequence = 21 };
    Require(laterUser.HasCoherentRootUserEventBinding, "later User remains source-coherent");
    var afterSystemHistory = valid with { ConversationSequenceBeforeMessage = 5, EventSequence = 6 };
    Require(afterSystemHistory.HasCoherentRootUserEventBinding,
        "first User may follow Service or system history");
    await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Require(gate.Seen == 1, "only winning event offered to host gate");
    await StopAndSettle(delivery);
}

static async Task HostScopeAndUnicode()
{
    var observed = Signal();
    var text = string.Concat(Enumerable.Repeat("😀", 65_536)); // 128K UTF-16 chars, over 128 KiB UTF-8.
    var enabled = false;
    var gate = new Gate((_, source) =>
        enabled && source.ProfileId == "profile"
            ? new Lease((observation, _) =>
            {
                Require(observation.UserText == text, "accepted Unicode text preserved without a new byte cap");
                observed.TrySetResult(true);
                return Task.CompletedTask;
            })
            : null);
    await using var delivery = new CommittedRootUserInputDelivery(gate);
    Require(delivery.TryEnqueue(Receipt("disabled", text)) == CommittedRootUserInputEnqueueResult.Queued,
        "disabled event accepted for best-effort host check");
    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Require(!observed.Task.IsCompleted, "disabled package received no body");
    enabled = true;
    Require(delivery.TryEnqueue(Receipt("enabled", text)) == CommittedRootUserInputEnqueueResult.Queued,
        "enabled event queued");
    await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await StopAndSettle(delivery);
}

static async Task FanOutIndependentLeases()
{
    var slowStarted = Signal();
    var slowRelease = Signal();
    var slowDisposed = Signal();
    var fastDone = Signal();
    var gate = new Gate((target, _) => target.PackageId switch
    {
        "slow" => new Lease(async (_, _) =>
        {
            slowStarted.TrySetResult(true);
            await slowRelease.Task;
        }, () => slowDisposed.TrySetResult(true)),
        "fast" => new Lease((_, _) =>
        {
            fastDone.TrySetResult(true);
            return Task.CompletedTask;
        }),
        _ => throw new InvalidOperationException("unexpected observer")
    }, ["slow", "fast"]);
    await using var delivery = new CommittedRootUserInputDelivery(gate);
    Require(delivery.TryEnqueue(Receipt("fanout")) == CommittedRootUserInputEnqueueResult.Queued,
        "fan-out receipt queued");
    await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Require(!slowDisposed.Task.IsCompleted, "fast observer does not release slow package");
    slowRelease.TrySetResult(true);
    await slowDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await StopAndSettle(delivery);
}

static async Task TimeoutKeepsExactLease()
{
    var started = Signal();
    var release = Signal();
    var disposed = Signal();
    var timeout = Signal();
    var lease = new Lease(async (_, _) =>
    {
        started.TrySetResult(true);
        await release.Task;
    }, () => disposed.TrySetResult(true));
    var gate = new Gate((_, _) => lease);
    await using var delivery = new CommittedRootUserInputDelivery(gate,
        (code, _, _) => { if (code == "delivery-timeout") timeout.TrySetResult(true); },
        TimeSpan.FromMilliseconds(30));
    Require(delivery.TryEnqueue(Receipt("timeout")) == CommittedRootUserInputEnqueueResult.Queued,
        "timeout event queued");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await timeout.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Require(!disposed.Task.IsCompleted, "deadline did not retire active package lease");
    release.TrySetResult(true);
    await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await StopAndSettle(delivery);
}

static async Task FullQueueDoesNotWait()
{
    var started = Signal();
    var release = Signal();
    var gate = new Gate((_, _) => new Lease(async (_, _) =>
    {
        started.TrySetResult(true);
        await release.Task;
    }));
    await using var delivery = new CommittedRootUserInputDelivery(gate, callbackDeadline: TimeSpan.FromMilliseconds(30));
    Require(delivery.TryEnqueue(Receipt("fill-0")) == CommittedRootUserInputEnqueueResult.Queued,
        "first queued");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var full = 0;
    for (var i = 1; i <= 100; i++)
        if (delivery.TryEnqueue(Receipt("fill-" + i)) == CommittedRootUserInputEnqueueResult.Full) full++;
    Require(full > 0 && delivery.FullCount == full, "bounded queue drops instead of waiting");
    release.TrySetResult(true);
    await StopAndSettle(delivery);
}

static async Task StopRetainsActiveLeaseAndDrainsPending()
{
    var started = Signal();
    var release = Signal();
    var disposed = Signal();
    var invoked = 0;
    var acquired = 0;
    var gate = new Gate((_, _) =>
    {
        var index = Interlocked.Increment(ref acquired);
        return new Lease(async (_, _) =>
        {
            Interlocked.Increment(ref invoked);
            started.TrySetResult(true);
            await release.Task;
        }, () => { if (index == 1) disposed.TrySetResult(true); });
    });
    var delivery = new CommittedRootUserInputDelivery(gate, callbackDeadline: TimeSpan.FromMilliseconds(30));
    Require(delivery.TryEnqueue(Receipt("stop-active")) == CommittedRootUserInputEnqueueResult.Queued,
        "active callback queued");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    for (var i = 0; i < CommittedRootUserInputDelivery.QueueCapacity; i++)
        _ = delivery.TryEnqueue(Receipt("stop-pending-" + i));
    await delivery.DisposeAsync();
    Require(delivery.TryEnqueue(Receipt("after-stop")) == CommittedRootUserInputEnqueueResult.Stopped,
        "stop rejects new text receipts");
    Require(!delivery.Settlement.IsCompleted && !disposed.Task.IsCompleted,
        "stop return is not callback retirement or settlement");
    release.TrySetResult(true);
    await delivery.Settlement.WaitAsync(TimeSpan.FromSeconds(2));
    Require(disposed.Task.IsCompleted && invoked >= 1,
        "eventual settlement waits for the actual callback and releases its lease");
}

static void InvalidDeadlineStartsNoCallback()
{
    var invoked = 0;
    var gate = new Gate((_, _) =>
    {
        Interlocked.Increment(ref invoked);
        return null;
    });
    try
    {
        _ = new CommittedRootUserInputDelivery(gate, callbackDeadline: TimeSpan.MaxValue);
        throw new InvalidOperationException("invalid deadline was accepted");
    }
    catch (ArgumentOutOfRangeException) { }
    Require(invoked == 0, "invalid deadline starts no host acquisition or callback");
}

sealed class Gate(Func<CommittedRootUserInputObserverTarget, CommittedRootUserInputSource,
        ICommittedRootUserInputCallbackLease?> acquire,
    string[]? observers = null)
    : ICommittedRootUserInputDeliveryGate
{
    private readonly IReadOnlyList<CommittedRootUserInputObserverTarget> targets =
        (observers ?? new[] { "one" }).Select(id =>
            new CommittedRootUserInputObserverTarget(id, "generation-1", new object())).ToArray();
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Seen;
    public IReadOnlyList<CommittedRootUserInputObserverTarget> EligibleObservers(CommittedRootUserInputSource source) =>
        targets;
    public ICommittedRootUserInputCallbackLease? TryAcquire(
        CommittedRootUserInputObserverTarget target, CommittedRootUserInputSource source)
    {
        Interlocked.Increment(ref Seen);
        var result = acquire(target, source);
        Entered.TrySetResult(true);
        return result;
    }
}

sealed class Lease(Func<CommittedRootUserInputObservation, CancellationToken, Task> invoke,
    Action? onDispose = null) : ICommittedRootUserInputCallbackLease
{
    private int disposed;
    public ValueTask InvokeAsync(CommittedRootUserInputObservation observation, CancellationToken token) =>
        new(invoke(observation, token));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) onDispose?.Invoke();
    }
}

}
