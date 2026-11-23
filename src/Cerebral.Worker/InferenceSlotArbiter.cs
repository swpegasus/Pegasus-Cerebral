using System.Threading.Channels;

namespace Cerebral.Worker;

/// <summary>Idle slots remain fully borrowable. Context affinity selects only an already idle slot;
/// it never reserves capacity, reorders waiters, proves native cache contents or authorizes inference.</summary>
internal sealed class InferenceSlotArbiter
{
    private readonly object gate = new();
    private readonly List<int> available;
    private readonly LocalContextAffinityKey?[] owners;
    private readonly bool[] borrowed;
    private readonly LinkedList<Waiter> interactive = new(), normal = new();
    private Exception? closed;

    internal InferenceSlotArbiter(int count)
    {
        if (count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(count));
        available = new(count); owners = new LocalContextAffinityKey?[count]; borrowed = new bool[count];
        for (var id = 0; id < count; id++) available.Add(id);
    }
    internal int AvailableCount { get { lock (gate) return available.Count; } }

    // Preserve the original entry and reflection contract for unbound/direct callers.
    internal Task<int> AcquireAsync(InferencePriority priority, CancellationToken token) => AcquireCore(priority, token, null);
    internal Task<int> AcquireForContextAsync(InferencePriority priority, CancellationToken token, LocalContextAffinityKey key)
    { key.Validate(); return AcquireCore(priority, token, key); }
    private Task<int> AcquireCore(InferencePriority priority, CancellationToken token, LocalContextAffinityKey? owner)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested) return Task.FromCanceled<int>(token);
            if (closed is not null) return Task.FromException<int>(closed);
            if (available.Count != 0)
            {
                var at = 0;
                if (owner is { } key)
                    for (var i = 0; i < available.Count; i++)
                        if (owners[available[i]] == key) { at = i; break; }
                var id = available[at]; available.RemoveAt(at);
                borrowed[id] = true; owners[id] = owner;
                return Task.FromResult(id);
            }
            var queue = priority == InferencePriority.Interactive ? interactive : normal;
            var waiter = new Waiter(queue, owner); waiter.Node = queue.AddLast(waiter);
            waiter.Cancellation = token.Register(() => Cancel(waiter, token));
            if (waiter.Node is null) waiter.Cancellation.Unregister();
            return waiter.Completion.Task;
        }
    }

    // Invalidating a preference never lends or returns capacity. A quarantined lease stays consumed.
    internal void InvalidateContext(int id)
    {
        lock (gate)
        {
            if (closed is not null) return;
            if ((uint)id >= (uint)owners.Length) throw new ArgumentOutOfRangeException(nameof(id));
            owners[id] = null;
        }
    }
    internal void Return(int id)
    {
        lock (gate)
        {
            if (closed is not null) return;
            if ((uint)id >= (uint)owners.Length) throw new ArgumentOutOfRangeException(nameof(id));
            if (!borrowed[id]) throw new InvalidOperationException("Inference slot is not borrowed.");
            var waiter = interactive.First?.Value ?? normal.First?.Value;
            if (waiter is null) { borrowed[id] = false; available.Add(id); return; }
            // Keep the exact previous priority/FIFO waiter policy. Matching a different waiter
            // is not a reason to bypass the head; ownership changes to the admitted borrower.
            Remove(waiter); owners[id] = waiter.Owner;
            waiter.Completion.TrySetResult(id);
        }
    }
    internal void Complete(Exception? error = null)
    {
        lock (gate)
        {
            if (closed is not null) return;
            closed = error ?? new ChannelClosedException(); available.Clear(); Array.Clear(owners);
            foreach (var waiter in interactive.Concat(normal).ToArray())
            { Remove(waiter); waiter.Completion.TrySetException(closed); }
        }
    }
    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (gate)
        {
            if (waiter.Node is null) return; // A granted slot remains owned by its lease.
            Remove(waiter); waiter.Completion.TrySetCanceled(token);
        }
    }
    private static void Remove(Waiter waiter)
    {
        waiter.Queue.Remove(waiter.Node!); waiter.Node = null; waiter.Cancellation.Unregister();
    }
    private sealed class Waiter(LinkedList<Waiter> queue, LocalContextAffinityKey? owner)
    {
        internal readonly LinkedList<Waiter> Queue = queue;
        internal readonly LocalContextAffinityKey? Owner = owner;
        internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
        internal CancellationTokenRegistration Cancellation;
    }
}
