using Cerebral.Engine.Contracts;

namespace Cerebral.Engine.Default;

/// <summary>Preserves today's feature-defined transitions through the optional workflow compatibility adapter.</summary>
public sealed class DefaultEnginePlugin : IEnginePlugin
{
    private IEngineFeatureHost? host;
    private int maximumChildren = 8;
    public EngineDescriptor Descriptor => new("pegasus.default-engine", "1.0.0", EngineProtocol.Version,
        EngineProtocol.StatelessFormat, [new("cerebral.workflow.transition", 1, "cerebral.workflow.step", 1),
            new("cerebral.model.idle", 1, "cerebral.model.idle-decision", 1),
            new("cerebral.agents.plan", 1, "cerebral.agents.assignment-plan", 1)]);

    public Task InitializeAsync(IEngineFeatureHost host, EngineConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        if (configuration.Values.TryGetValue("maximum-children", out var limit))
            maximumChildren = int.TryParse(limit, out var parsed) && parsed is >= 1 and <= 8 ? parsed
                : throw new ArgumentException("maximum-children must be an integer from 1 through 8.");
        return Task.CompletedTask;
    }
    public Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is null) throw new InvalidOperationException("Engine has not been initialized.");
        return Task.CompletedTask;
    }
    public async Task<EngineDecision> EvaluateAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Contract == "cerebral.agents.plan" && request.Version == 1)
        {
            var assignments = request.Payload.GetProperty("assignments");
            if (assignments.ValueKind != System.Text.Json.JsonValueKind.Array || assignments.GetArrayLength() is < 1 ||
                assignments.GetArrayLength() > maximumChildren)
            {
                // A rejected assignment is ordinary work evidence, not an engine health failure.
                using var rejected = System.Text.Json.JsonDocument.Parse(
                    "{\"assignments\":[],\"rejectedReason\":\"Delegation exceeds the engine's child limit of " + maximumChildren + ".\"}");
                return new(request.OperationId, request.WorkId, request.ExpectedRevision, request.ActivationEpoch,
                    request.ConfigurationFingerprint, "cerebral.agents.assignment-plan", 1, rejected.RootElement.Clone());
            }
            // Detached data only; host admission rechecks profiles, lineage, grants and capacity.
            using var plan = System.Text.Json.JsonDocument.Parse("{\"assignments\":" + assignments.GetRawText() + "}");
            return new(request.OperationId, request.WorkId, request.ExpectedRevision, request.ActivationEpoch,
                request.ConfigurationFingerprint, "cerebral.agents.assignment-plan", 1, plan.RootElement.Clone());
        }
        if (request.Contract == "cerebral.model.idle" && request.Version == 1)
        {
            var minutes = request.Payload.GetProperty("idleMinutes").GetInt32();
            var elapsed = request.Payload.GetProperty("idleMilliseconds").GetInt64();
            if (minutes is < 0 or > 10080 || elapsed < 0)
                throw new ArgumentException("Invalid model idle policy input.");
            // No engine-private types enter shared serializer caches: they would pin this load context.
            using var idleResult = System.Text.Json.JsonDocument.Parse(minutes > 0 && elapsed >= minutes * 60_000L
                ? "{\"unload\":true}" : "{\"unload\":false}");
            return new(request.OperationId, request.WorkId, request.ExpectedRevision, request.ActivationEpoch,
                request.ConfigurationFingerprint, "cerebral.model.idle-decision", 1, idleResult.RootElement.Clone());
        }
        if (request.Contract != "cerebral.workflow.transition" || request.Version != 1)
            throw new NotSupportedException("This engine does not support the requested work contract/version.");
        var result = await (host ?? throw new InvalidOperationException("Engine is not initialized."))
            .EvaluateFeatureAsync(request, cancellationToken).ConfigureAwait(false);
        return new(request.OperationId, request.WorkId, request.ExpectedRevision, request.ActivationEpoch,
            request.ConfigurationFingerprint, "cerebral.workflow.step", 1, result);
    }
    public ValueTask DisposeAsync() { host = null; return ValueTask.CompletedTask; }
}
