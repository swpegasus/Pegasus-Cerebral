namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    public async Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            resourcePauseRequested = true;
            return instances.Where(item => !HasExited(item.Value))
                .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        }
        finally { lifecycle.Release(); }
    }

    public async Task UnloadPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfOwnerRuntimeDisposed();
                if (!resourcePauseRequested)
                    throw new InvalidOperationException("Local model admission was reopened before the unload boundary was reached.");
                if (instances.Values.Any(instance => instance.Busy > instance.Slots.Quarantined.Count))
                {
                    // A request that crossed admission before the pause owns its slot until completion. Never cancel
                    // or steal that ownership merely to release GPU memory.
                }
                else
                {
                    foreach (var (id, instance) in instances.ToArray()) await StopAsync(id, instance).ConfigureAwait(false);
                    if (Accounting.Snapshot().Entries.Any(value => value.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.FittingProcess))
                        throw new InvalidOperationException("Local native cleanup remains unconfirmed; resource admission stays paused.");
                    foreach (var model in store.List<Cerebral.Core.ModelRecord>("model").Where(model => model.State == "loaded" || model.State == "loading"))
                    {
                        model.State = "unloaded";
                        model.Error = null;
                        Save(model);
                    }
                    return;
                }
            }
            finally { lifecycle.Release(); }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResumePausedAsync(IReadOnlyList<string> modelIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            if (!resourcePauseRequested)
                throw new InvalidOperationException("Local model admission is already active.");
        }
        finally { lifecycle.Release(); }

        foreach (var id in modelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            _ = await LoadTrackedAsync(id, cancellationToken, allowResourceResume: true).ConfigureAwait(false);
    }

    public async Task ReopenPausedAdmissionAsync(Action markDurablyActive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markDurablyActive);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            if (!resourcePauseRequested)
                throw new InvalidOperationException("Local model admission is already active.");
            // Commit the durable active state while admission is still closed. If persistence fails,
            // the exception leaves both this runtime and the next process generation paused.
            markDurablyActive();
            resourcePauseRequested = false;
        }
        finally { lifecycle.Release(); }
    }

}
