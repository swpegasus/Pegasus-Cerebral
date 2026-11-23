using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static class AgentMetricsToolTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Agent metrics: " + label);
            checks++;
        }
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);

        var profile = new AgentProfile { Id = "lead", Capabilities = ["diagnostics"],
            AllowedTools = [AgentMetricsToolExtension.ToolName] };
        var valid = new ToolExecutionContext(profile, "own-chat", "own-request", EffectId: "own-effect")
        { EnforceProfileScope = true };
        AgentMetricsToolExtension.RequireHostContext(valid);
        Check(true, "host-owned effect accepted structurally");
        Check(AgentMetricsToolExtension.HasGrant(profile, profile), "named accepted and current grant");
        var wildcard = new AgentProfile { Id = "lead", Capabilities = ["diagnostics"], AllowedTools = ["*"] };
        Check(AgentMetricsToolExtension.HasGrant(wildcard, profile) &&
            AgentMetricsToolExtension.HasGrant(profile, wildcard) &&
            AgentMetricsToolExtension.HasGrant(wildcard, wildcard),
            "ordinary wildcard grants work at either side of the grant intersection");
        var noCapability = new AgentProfile { Id = "lead", Capabilities = [],
            AllowedTools = [AgentMetricsToolExtension.ToolName] };
        Check(!AgentMetricsToolExtension.HasGrant(noCapability, profile) &&
            !AgentMetricsToolExtension.HasGrant(profile, noCapability),
            "diagnostics capability required at admission and now");
        var noTool = new AgentProfile { Id = "lead", Capabilities = ["diagnostics"], AllowedTools = [] };
        Check(!AgentMetricsToolExtension.HasGrant(noTool, profile) &&
            !AgentMetricsToolExtension.HasGrant(profile, noTool),
            "removed tool grant blocks admitted and current access");
        await using (var dispatcher = new ToolDispatcher(() => new HostSettings(), [new MetricsDescriptorOnly()]))
        {
            var wildcardCatalog = await dispatcher.ListAsync(wildcard, default);
            var exactCatalog = await dispatcher.ListAsync(profile, default);
            var missingCapabilityCatalog = await dispatcher.ListAsync(noCapability, default);
            Check(wildcardCatalog.Count(item => item.Name == AgentMetricsToolExtension.ToolName) == 1 &&
                exactCatalog.Count(item => item.Name == AgentMetricsToolExtension.ToolName) == 1,
                "ordinary wildcard and named grants discover the metric tool");
            Check(!missingCapabilityCatalog.Any(item => item.Name == AgentMetricsToolExtension.ToolName),
                "diagnostics capability is still required for discovery");
        }
        foreach (var refused in new[]
        {
            valid with { EnforceProfileScope = false }, valid with { SessionId = "" },
            valid with { JobId = "" }, valid with { EffectId = null }
        })
        {
            var rejected = false;
            try { AgentMetricsToolExtension.RequireHostContext(refused); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "missing host-owned durable coordinate refused");
        }

        var empty = CerebralJson.Element(new { });
        var submission = new WorkflowSubmission("own-request", "own-chat", "lead", "fixture.workflow", "1.0.0",
            "fixture", null, empty, empty);
        var effect = new WorkflowEffect("own-effect", WorkerWorkflowEffects.ToolCommand, 1,
            CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(AgentMetricsToolExtension.ToolName, empty)),
            WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched);
        var run = new WorkflowRun { Id = "own-request", Submission = submission,
            Status = WorkflowStatus.Running, Effect = effect };
        AgentMetricsToolExtension.ValidateRunContext(run, valid);
        Check(true, "exact current accepted tool effect admitted");
        foreach (var refused in new[]
        {
            run with { Submission = submission with { ConversationId = "other-chat" } },
            run with { Submission = submission with { ProfileId = "other-agent" } },
            run with { Effect = effect with { Id = "other-effect" } },
            run with { Effect = effect with { CommandId = "other.command" } },
            run with { Effect = effect with { State = WorkflowEffectState.Succeeded } },
            run with { Effect = effect with { Arguments = CerebralJson.Element(
                new WorkerWorkflowEffects.ToolArguments("file.read", empty)) } },
            run with { Status = WorkflowStatus.Completed },
            run with { CancelRequested = true }
        })
        {
            var rejected = false;
            try { AgentMetricsToolExtension.ValidateRunContext(refused, valid); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "foreign, spent or changed accepted effect refused");
        }

        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var execution = JobScheduler.ProjectExecutionMetrics("local-installation", "scheduler-generation", at, []);
        var slots = new ManagedSlotObservationPage("cerebral.managed-model-slots/1", at,
            "runtime-generation", 1, "observed", false, true,
            [new("secret-model-id", "secret-instance-id", "present", 3, 2, 0)]);
        var inference = new InferenceObservationRegistry("accounting-generation", true).Snapshot();
        InferenceCaptureRow Captured(string request, int output, string conversation = "own-chat",
            DateTimeOffset? observed = null, string countBasis = "exact-native-template-token-ids",
            string tokenScope = "provider-unspecified") => new(
            "secret-row", "secret-entry", "accounting-generation", 1, null, "LocalPreparation",
            "secret-model-id", "secret-instance-id", "secret-correlation", "secret-attempt",
            "task-generation", new("secret-execution", "lead", 1, conversation, request),
            "observed", null, null, null, 0, "generating", false, null,
            new(100, 16384, 512, 512, 0, 128, countBasis),
            new("stop", true, false, 100, output, tokenScope, null, observed ?? at),
            new(100, 80, 20, 25), "observed", 20, output,
            new("observed", "secret-provider-message", observed ?? at), "unobserved", null, null, observed ?? at);
        inference = inference with { Rows = [
            Captured("prior-request", 21, observed: at.AddMinutes(-1),
                countBasis: "secret-count-basis", tokenScope: "secret-provider-scope"),
            Captured("own-request", 34), Captured("foreign-request", 99, conversation: "foreign-chat")
        ] };
        GenerationTelemetrySample Lane(string request, int output, double? rate,
            string conversation = "own-chat", DateTimeOffset? observed = null) =>
            new("cerebral.generation-metrics/1", "secret-lane-id", "secret-instance-id", "secret-model-id",
                "llama", new(conversation, request, "secret-run", "secret-effect", "lead", "local"),
                "active", "output", null, output, null, rate, 1200, observed ?? at);
        var generation = new GenerationTelemetrySnapshot("cerebral.generation-metrics/1", at,
            [Lane("prior-request", 21, 19, observed: at.AddMinutes(-1)),
                Lane("own-request", 34, 28.5), Lane("foreign-request", 99, 99, conversation: "foreign-chat")],
            [Lane("prior-request", 18, 17, observed: at.AddMinutes(-1)), Lane("own-request", 20, null)], false);
        var projected = Json(AgentMetricsToolExtension.Project("own-chat", "own-request",
            execution, slots, inference, generation));
        Check(projected.GetProperty("schema").GetString() == "cerebral.agent-metrics/1",
            "versioned narrow bridge");
        Check(projected.GetProperty("slots").GetProperty("models")[0].GetProperty("allocatedSlots").GetInt32() == 3 &&
            projected.GetProperty("slots").GetProperty("models")[0].GetProperty("managedAvailableSlots").GetInt32() == 2,
            "actual managed counters retained without inferring active generation");
        Check(projected.GetProperty("execution").GetProperty("sampledAt").GetDateTimeOffset() == at &&
            projected.GetProperty("slots").GetProperty("sampledAt").GetDateTimeOffset() == at,
            "independent source timestamps retained");
        var owned = projected.GetProperty("inference").GetProperty("chatRows");
        Check(owned.GetArrayLength() == 2 &&
            owned[0].GetProperty("budget").GetProperty("effectiveContext").GetInt32() == 16384 &&
            owned[0].GetProperty("cache").GetProperty("cachedTokens").GetInt32() == 80 &&
            owned[0].GetProperty("terminal").GetProperty("generatedTokens").GetInt32() == 34,
            "newest same-chat budget, cache and terminal measurements retained");
        Check(owned[1].GetProperty("terminal").GetProperty("generatedTokens").GetInt32() == 21,
            "a previous request in the same chat remains visible");
        Check(owned[0].GetProperty("requestScope").GetString() == "current" &&
            owned[1].GetProperty("requestScope").GetString() == "earlier-in-chat" &&
            owned[0].GetProperty("observedAt").GetDateTimeOffset() == at,
            "current and recent rows are distinguished without raw request identifiers");
        Check(owned[0].GetProperty("budget").GetProperty("countBasis").GetString() ==
                "exact-native-template-token-ids" &&
            owned[0].GetProperty("terminal").GetProperty("generatedTokenScope").GetString() ==
                "provider-unspecified" &&
            owned[1].GetProperty("budget").GetProperty("countBasis").GetString() == "unavailable" &&
            owned[1].GetProperty("terminal").GetProperty("generatedTokenScope").GetString() == "unavailable",
            "known token-count meaning retained and unknown labels withheld");
        var active = projected.GetProperty("generation").GetProperty("active");
        Check(active.GetArrayLength() == 2 && active[0].GetProperty("outputTokens").GetInt32() == 34 &&
            active[0].GetProperty("outputTokensPerSecond").GetDouble() == 28.5,
            "newest same-chat numeric rate returned first");
        Check(active[1].GetProperty("outputTokens").GetInt32() == 21 &&
            active[1].GetProperty("requestScope").GetString() == "earlier-in-chat" &&
            projected.GetProperty("generation").GetProperty("recent").GetArrayLength() == 2,
            "retained earlier same-chat generation samples included");
        Check(projected.GetProperty("generation").GetProperty("recent")[0]
            .GetProperty("outputTokensPerSecond").ValueKind == JsonValueKind.Null,
            "missing rate remains unavailable rather than zero");
        var text = projected.GetRawText();
        Check(!text.Contains("secret-model-id", StringComparison.Ordinal) &&
            !text.Contains("secret-instance-id", StringComparison.Ordinal) &&
            !text.Contains("foreign-request", StringComparison.Ordinal) &&
            !text.Contains("secret-effect", StringComparison.Ordinal) &&
            !text.Contains("secret-provider", StringComparison.Ordinal) &&
            !text.Contains("secret-count-basis", StringComparison.Ordinal),
            "model, instance, foreign chat and unreviewed provider strings excluded");
        var missing = Json(AgentMetricsToolExtension.Project("own-chat", "own-request", null, null, null, null));
        Check(missing.GetProperty("execution").GetProperty("state").GetString() == "unavailable" &&
            missing.GetProperty("generation").GetProperty("state").GetString() == "unavailable",
            "unavailable sources represented explicitly");
        var noSamples = Json(AgentMetricsToolExtension.Project("own-chat", "own-request",
            execution, slots, inference, new("cerebral.generation-metrics/1", at, [], [], false)));
        Check(noSamples.GetProperty("generation").GetProperty("state").GetString() == "no-local-bound-samples",
            "no local sample does not claim remote activity absent");
        var many = Json(AgentMetricsToolExtension.Project("own-chat", "own-request",
            execution, slots, inference with { Rows = Enumerable.Repeat(Captured("own-request", 34), 17).ToArray() },
            generation with { Active = Enumerable.Repeat(Lane("own-request", 34, 28.5), 17).ToArray() }));
        Check(many.GetProperty("inference").GetProperty("chatRows").GetArrayLength() == 16 &&
            many.GetProperty("inference").GetProperty("truncated").GetBoolean() &&
            many.GetProperty("inference").GetProperty("omittedChatRows").GetInt32() == 1 &&
            many.GetProperty("generation").GetProperty("active").GetArrayLength() == 16 &&
            many.GetProperty("generation").GetProperty("truncated").GetBoolean() &&
            many.GetProperty("generation").GetProperty("omittedActive").GetInt32() == 1,
            "bounded same-chat rows and telemetry expose omissions");
        var bad = false;
        try { _ = AgentMetricsToolExtension.Project("", "own-request", null, null, null, null); }
        catch (ArgumentException) { bad = true; }
        Check(bad, "no conversation identity refused");
        return checks;
    }

    private sealed class MetricsDescriptorOnly : IToolExtension
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [new(AgentMetricsToolExtension.ToolName, "fixture", CerebralJson.Element(new
            { type = "object", properties = new { }, additionalProperties = false }), "diagnostics")];

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Fixture must not execute.");
    }
}
