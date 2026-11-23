using System.Text.Json;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    private const string IdleContract = "cerebral.model.idle";

    public async Task<ModelRecord> SetIdleMinutesAsync(string id, int minutes, CancellationToken token)
    {
        if (minutes is < -1 or > 10080) throw new ArgumentOutOfRangeException(nameof(minutes));
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            token.ThrowIfCancellationRequested();
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            if (model.IdleMinutes != minutes)
            {
                model.IdleMinutes = minutes;
                store.Put("model", id, model);
                if (instances.TryGetValue(id, out var instance)) instance.Idle.Touch();
                events.Publish("model.updated", null, model);
            }
            return model;
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>The scheduler supplies durable workflow interest; the runtime owns final physical release.</summary>
    public async Task UnloadIdleAsync(CancellationToken token, Func<string, bool>? additionalActiveWork = null,
        EngineController? engine = null)
    {
        if (engine?.IsPaused != true && ModelOperationIdleSweep is { } operations)
            await operations(token).ConfigureAwait(false);
        foreach (var (id, instance) in instances)
        {
            token.ThrowIfCancellationRequested();
            // Dead children must be reaped even with idle release disabled or outstanding work.
            if (HasExited(instance)) { await CleanupExitedInstanceAsync(id, instance, token); continue; }
            var observation = instance.Idle.Observe(Active(id, instance));
            var minutes = IdleMinutes(id);
            if (minutes <= 0 || observation.Milliseconds < minutes * 60_000L) continue;

            // Engines predating this optional adapter retain the same explicit host idle policy.
            // Pausing/faulting an engine never discards a resident model while its work is held.
            if (engine?.IsPaused == true) continue;
            using var decision = engine?.SupportsWork(IdleContract, 1) == true
                ? await engine.EvaluateAsync(instance.Id, observation.Revision, IdleContract, 1,
                    CerebralJson.Element(new { idleMinutes = minutes, idleMilliseconds = observation.Milliseconds }),
                    _ => Task.FromResult(CerebralJson.Element(new { unload = true })), token).ConfigureAwait(false)
                : null;
            var unload = decision is null || ReadIdleDecision(decision);
            if (!unload) continue;

            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfOwnerRuntimeDisposed();
                token.ThrowIfCancellationRequested();
                if (!instances.TryGetValue(id, out var current) || !ReferenceEquals(current, instance) || HasExited(current)) continue;
                var active = Active(id, current);
                var latest = current.Idle.Observe(active);
                if (active || latest.Revision != observation.Revision || IdleMinutes(id) != minutes ||
                    latest.Milliseconds < minutes * 60_000L) continue;

                // Only mark the exact current generation for removal inside the epoch fence.
                // Inference admission shares lifecycle; the strategy cannot ignore work or shorten
                // the owner's grace period. Stop/disposal occurs afterwards, still under lifecycle.
                if (decision is not null)
                    decision.Commit(_ => { current.Stopped = true; return true; });
                else current.Stopped = true;
                await StopAsync(id, current).ConfigureAwait(false);
                var model = store.Get<ModelRecord>("model", id);
                if (model is not null) { model.State = "unloaded"; model.Error = null; Save(model); }
            }
            finally { lifecycle.Release(); }
        }

        bool Active(string id, Instance instance) => Volatile.Read(ref instance.Busy) != 0 ||
            store.ActiveJobs().Any(job => JobScheduler.IsActive(job) && job.Profile.ModelWorkerId == "local" && job.Profile.ModelId == id) ||
            additionalActiveWork?.Invoke(id) == true;
    }

    private int IdleMinutes(string id)
    {
        var model = store.Get<ModelRecord>("model", id);
        return model is null ? 0 : model.IdleMinutes < 0 ? settings().DefaultModelIdleMinutes : model.IdleMinutes;
    }

    private static bool ReadIdleDecision(EngineController.DecisionLease decision)
    {
        var payload = decision.Decision.Payload;
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1 ||
            !payload.TryGetProperty("unload", out var unload) || unload.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            decision.Reject("Model idle decision must contain only a boolean unload field.");
            throw new EngineEvaluationException("Invalid model idle decision; model remains resident.");
        }
        return unload.GetBoolean();
    }
}
