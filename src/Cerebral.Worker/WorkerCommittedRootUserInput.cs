namespace Cerebral.Worker;

/// <summary>Optional post-commit delivery owned by this Worker host instance.</summary>
public sealed partial class WorkerWorkflows : IAsyncDisposable
{
    private readonly object committedRootUserInputGate = new();
    private CommittedRootUserInputDelivery? committedRootUserInputDelivery;
    private Task committedRootUserInputSettlement = Task.CompletedTask;
    private bool committedRootUserInputStopped;

    /// <summary>Actual drain proof; stopping the queue alone is not package retirement.</summary>
    internal Task CommittedRootUserInputSettlement => Volatile.Read(ref committedRootUserInputSettlement);

    /// <summary>
    /// Attach once after normal construction and before accepting requests. The caller
    /// owns a rejected delivery; WorkerWorkflows owns an attached delivery thereafter.
    /// </summary>
    internal void AttachCommittedRootUserInputDelivery(CommittedRootUserInputDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        lock (committedRootUserInputGate)
        {
            if (committedRootUserInputStopped)
                throw new ObjectDisposedException(nameof(WorkerWorkflows));
            if (committedRootUserInputDelivery is not null)
                throw new InvalidOperationException("Committed-input delivery is already attached.");
            committedRootUserInputDelivery = delivery;
            committedRootUserInputSettlement = delivery.Settlement;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CommittedRootUserInputDelivery? delivery;
        lock (committedRootUserInputGate)
        {
            if (committedRootUserInputStopped) return;
            committedRootUserInputStopped = true;
            delivery = committedRootUserInputDelivery;
            committedRootUserInputDelivery = null;
        }
        if (delivery is not null) await delivery.DisposeAsync().ConfigureAwait(false);
        // ServicePluginRuntime owns actual package retirement. It must defer unloading
        // while an acquired callback lease remains live, even after this returns.
    }
}
