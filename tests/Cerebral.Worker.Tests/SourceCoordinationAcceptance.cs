using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in Source owner-pipe check with actual existing remote inference. Never acquires,
/// reloads or unloads a model; retains one visible chat and narrows two task-owned helper profiles.</summary>
internal static class SourceCoordinationAcceptance
{
    public static async Task RunAsync(CerebralClient client, EngineStatus status, bool execute, CancellationToken token)
    {
        var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: token);
        var template = profiles.Single(profile => profile.Id == "7980ce1190f648a8b832b7516a67adbc");
        if (template.ModelWorkerId == "local" || template.WorkerId != "local" || template.ModelId != "source-qwen35-9b")
            throw new InvalidOperationException("The verified Source profile no longer uses the intended GB10 model route.");
        Console.WriteLine(JsonSerializer.Serialize(new { status.Epoch, status.Active?.Slot, template.ModelId, template.ModelWorkerId,
            helpers = profiles.Where(profile => profile.Role == "helper" && profile.WorkerId == "local")
                .Select(profile => new { profile.Id, profile.Name, profile.Revision, profile.ModelId, profile.ModelWorkerId }).Take(16) }, CerebralJson.Options));
        if (!execute) return;
        if (status.Paused || status.Error is not null || status.ActiveEvaluations != 0)
            throw new InvalidOperationException("The Source engine must be healthy and quiescent before the coordination canary.");

        const string alpha = "00-engine-check-alpha", beta = "00-engine-check-beta";
        if (template.Role != "lead" || !template.Capabilities.Contains("agents.delegate", StringComparer.Ordinal))
            throw new InvalidOperationException("The existing Source lead has no delegation grant.");
        async Task Configure(string id, string role, string instruction)
        {
            var existing = profiles.SingleOrDefault(profile => profile.Id == id);
            var profile = JsonSerializer.Deserialize<AgentProfile>(JsonSerializer.Serialize(template, CerebralJson.Options), CerebralJson.Options)!;
            profile.Id = id; profile.Revision = existing?.Revision ?? 0; profile.Name = id;
            profile.Role = role; profile.Purpose = "Bounded source engine coordination acceptance"; profile.Instructions = instruction;
            profile.AllowedTools = []; profile.AllowedRoots = []; profile.Capabilities = role == "lead" ? ["agents.delegate"] : [];
            profile.RequiredModelCapabilities = []; profile.PluginSettings = [];
            profile.FollowsDefaultModel = false; profile.FollowsModelBudgets = false;
            profile.ContextTokens = template.ContextTokens; profile.ResponseTokens = 1024;
            profile.MaxSteps = 16; profile.MaxSeconds = 180; profile.AutomaticCompaction = false;
            await client.GetAsync<AgentProfile>("agents.save", profile, cancellationToken: token);
        }
        await Configure(alpha, "helper", "Complete your independent arithmetic assignment and return a short evidence statement. Do not delegate or infer another agent's answer.");
        await Configure(beta, "helper", "Complete your independent word-count assignment and return a short evidence statement. Do not delegate or infer another agent's answer.");
        var id = "engine-coordination-gb10-20261008";
        var submitted = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest(id, id, template.Id,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Use two independent helpers. Delegate to 00-engine-check-alpha to compute 2 + 3 and report ALPHA_RESULT: 5. " +
            "Delegate to 00-engine-check-beta to count the words in 'small stable engine' and report BETA_RESULT: 3. " +
            "Use their exact current profile revisions from availableHelpers. After both actual helper results return, combine them in a short final response. " +
            "This is a coordination test: directly answering without creating both child contexts does not satisfy it.",
            null, WorkflowContract.EmptyObject), cancellationToken: token);
        Console.WriteLine(JsonSerializer.Serialize(new { submitted.Id, submitted.Submission.ConversationId, submitted.Revision, submitted.Status }, CerebralJson.Options));
        // Bounded snapshots leave the retained chat inspectable and let the caller continue monitoring without resubmission.
        var run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id }, cancellationToken: token);
        Console.WriteLine(JsonSerializer.Serialize(new { run.Id, run.Revision, run.Status, run.WaitingReason, run.Error }, CerebralJson.Options));
    }
}
