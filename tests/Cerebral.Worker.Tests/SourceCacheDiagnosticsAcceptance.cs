using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in bounded generation on the existing GB10 route; no model or settings mutation.</summary>
internal static class SourceCacheDiagnosticsAcceptance
{
    internal static async Task RunAsync(CerebralClient client, EngineStatus status, bool execute, CancellationToken token)
    {
        const string id = "engine-cache-diagnostics-gb10-20261008";
        if (execute)
        {
            if (status.Paused || status.Error is not null || status.ActiveEvaluations != 0 || status.CandidatePending)
                throw new InvalidOperationException("The Source engine must be healthy and known-good before the cache canary.");
            var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: token);
            var profile = profiles.Single(value => value.Id == "7980ce1190f648a8b832b7516a67adbc");
            if (profile.ModelWorkerId != "worker-3f6ab53f3a5a" || profile.ModelId != "source-qwen35-9b" || profile.WorkerId != "local")
                throw new InvalidOperationException("The Source profile no longer uses the verified GB10 route.");
            var submitted = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest(id, id, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Reply with CACHE_DIAGNOSTICS_OK. This is a short runtime diagnostics check; do not call tools or delegate.",
                null, WorkflowContract.EmptyObject), cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { submitted.Id, submitted.Revision, submitted.Status }, CerebralJson.Options));
        }
        var metrics = await client.GetAsync<PromptCacheDiagnosticsSnapshot>("diagnostics.prompt-cache.get", new { limit = 100 }, cancellationToken: token);
        Console.WriteLine(JsonSerializer.Serialize(metrics, CerebralJson.Options));
        if (!execute)
        {
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id }, cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { run.Id, run.Revision, run.Status, run.Result, run.Error }, CerebralJson.Options));
        }
    }
}
