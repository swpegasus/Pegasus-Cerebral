using System.Text.Json;
using Cerebral.Engine.Contracts;

namespace Cerebral.EngineFixture;

public sealed class FixtureEngine : IEnginePlugin
{
    private IEngineFeatureHost? host;
    private string mode = "echo";
    public EngineDescriptor Descriptor => new("fixture.engine", "1.0.0", 1, EngineProtocol.StatelessFormat,
        [new("cerebral.workflow.transition", 1, "cerebral.workflow.step", 1),
         new("cerebral.model.idle", 1, "cerebral.model.idle-decision", 1)]);
    public Task InitializeAsync(IEngineFeatureHost host, EngineConfiguration configuration, CancellationToken ct)
    {
        this.host = host;
        mode = configuration.Values.GetValueOrDefault("mode") ?? "echo";
        if (mode == "fail-initialize") throw new InvalidOperationException("Fixture initialization failure.");
        return Task.CompletedTask;
    }
    public async Task CheckHealthAsync(CancellationToken ct)
    {
        if (mode == "fail-health") throw new InvalidOperationException("Fixture health failure.");
        if (mode == "slow-health") await Task.Delay(200, ct);
    }
    public async Task<EngineDecision> EvaluateAsync(EngineRequest request, CancellationToken ct)
    {
        if (request.Contract == "cerebral.model.idle")
        {
            if (mode == "slow-idle") await Task.Delay(500, ct);
            using var idle = JsonDocument.Parse(mode == "bad-idle" ? "{\"unload\":\"yes\"}"
                : mode == "idle-retain" ? "{\"unload\":false}" : "{\"unload\":true}");
            return new(request.OperationId, request.WorkId, request.ExpectedRevision, request.ActivationEpoch,
                request.ConfigurationFingerprint, "cerebral.model.idle-decision", 1, idle.RootElement.Clone());
        }
        if (mode == "fail-evaluate") throw new InvalidOperationException("Fixture evaluation failure.");
        var result = await host!.EvaluateFeatureAsync(mode == "alter-request"
            ? request with { WorkId = "another-work-item" } : request, ct);
        if (mode == "bad-payload")
        {
            // Do not register a collectible fixture-private anonymous type in process-wide serializer caches.
            using var document = JsonDocument.Parse("{\"unexpected\":true}");
            result = document.RootElement.Clone();
        }
        return new(request.OperationId, request.WorkId, request.ExpectedRevision,
            request.ActivationEpoch + (mode == "stale-epoch" ? 1 : 0), request.ConfigurationFingerprint,
            "cerebral.workflow.step", 1, result);
    }
    public ValueTask DisposeAsync() { host = null; return ValueTask.CompletedTask; }
}

public sealed class IncompatibleEngine : IEnginePlugin
{
    public EngineDescriptor Descriptor => new("fixture.engine", "1.0.0", 2, "private-checkpoint/2", []);
    public Task InitializeAsync(IEngineFeatureHost host, EngineConfiguration configuration, CancellationToken ct) => throw new InvalidOperationException("Must not initialize an incompatible engine.");
    public Task CheckHealthAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<EngineDecision> EvaluateAsync(EngineRequest request, CancellationToken ct) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
