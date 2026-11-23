using System.Text.Json;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private readonly SemaphoreSlim ownerEngineMutations = new(1, 1);

    private async Task<T> WithOwnerEngineMutationAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await ownerEngineMutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(cancellationToken).ConfigureAwait(false); }
        finally { ownerEngineMutations.Release(); }
    }

    private async Task<object> EngineCommandAsync(string operation, JsonElement args, CancellationToken ct)
    {
        var engine = workflows?.Engine ?? throw new InvalidOperationException("This Worker has no replaceable engine controller.");
        if (operation == "engine.work-hold" || operation.StartsWith("engine.work-hold.", StringComparison.Ordinal))
            return await IntentionalWorkHoldCommandAsync(operation, args, ct).ConfigureAwait(false);
        var keys = operation == "engine.status" ? Array.Empty<string>() : operation == "engine.activate" ? new[] { "slot", "expectedEpoch" } : new[] { "expectedEpoch" };
        if (args.ValueKind != JsonValueKind.Object ||
            !args.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            throw new ArgumentException("Engine commands require exactly their declared arguments, with no duplicates.");
        if (operation == "engine.status") return engine.Status();
        var epoch = args.GetProperty("expectedEpoch").GetInt64();
        var result = await WithOwnerEngineMutationAsync(async token => operation switch
        {
            "engine.pause" => engine.Pause(epoch),
            "engine.resume" => scheduler.HasIntentionalWorkHold()
                ? throw new InvalidOperationException("An intentional work hold owns this pause. Use engine.work-hold.resume with its exact comparison fields.")
                : engine.Resume(epoch),
            "engine.activate" => await engine.ActivateAsync(Text(args, "slot"), epoch, token).ConfigureAwait(false),
            "engine.rollback" => await engine.RollbackAsync(epoch, token).ConfigureAwait(false),
            "engine.promote" => engine.Promote(epoch),
            _ => throw new ArgumentException("Unknown engine operation.")
        }, ct).ConfigureAwait(false);
        events.Publish("engine.updated", null, result);
        return result;
    }
}
