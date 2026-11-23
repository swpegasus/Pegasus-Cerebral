using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in actual Source owner-pipe test. Uses the existing GB10 model and a separate
/// task-owned helper profile, with the admitted local CPU auditor. It never changes the lead,
/// downloads a model, opens a listener or replays an earlier completed canary.</summary>
internal static class SourceLayaAcceptance
{
    internal const string RunId = "engine-stock-laya-local-cpu-v2-20261008";
    internal const string ProfileId = "00-engine-laya-cpu-check";
    internal const string AdapterId = "laya.stock.criterion";

    internal static async Task RunAsync(CerebralClient client, EngineStatus engine, bool execute, CancellationToken token)
    {
        var status = await client.GetAsync<JsonElement>("laya.status", cancellationToken: token);
        if (execute && (!status.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True ||
            !status.TryGetProperty("adapterId", out var adapter) || adapter.GetString() != AdapterId))
            throw new InvalidOperationException("The stock local CPU auditor is not actually admitted and ready.");
        Console.WriteLine(JsonSerializer.Serialize(new { engine.Epoch, engine.Active?.Slot, auditor = status }, CerebralJson.Options));

        if (execute)
        {
            if (engine.Paused || engine.Error is not null || engine.ActiveEvaluations != 0 || engine.CandidatePending)
                throw new InvalidOperationException("The Source engine must be quiescent and known-good.");
            var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: token);
            var lead = profiles.Single(p => p.Id == "7980ce1190f648a8b832b7516a67adbc");
            if (lead.ModelWorkerId != "worker-3f6ab53f3a5a" || lead.ModelId != "source-qwen35-9b" || lead.WorkerId != "local")
                throw new InvalidOperationException("The verified Source chat route is no longer GB10.");
            var profile = JsonSerializer.Deserialize<AgentProfile>(JsonSerializer.Serialize(lead, CerebralJson.Options), CerebralJson.Options)!;
            profile.Id = ProfileId; profile.Revision = profiles.SingleOrDefault(p => p.Id == ProfileId)?.Revision ?? 0;
            profile.Name = "Local CPU audit acceptance"; profile.Role = "helper";
            profile.Purpose = "Bounded local stock Laya and GB10 runtime acceptance";
            profile.Instructions = "Complete the short requested calculation. Return only the answer requested. Do not call tools or delegate.";
            profile.AllowedRoots = []; profile.AllowedTools = []; profile.Capabilities = [];
            profile.RequiredModelCapabilities = []; profile.PluginSettings = new(StringComparer.Ordinal)
            {
                ["cerebral.audit"] = JsonSerializer.SerializeToElement(new WorkflowAuditPolicy(AdapterId, false, 30000), CerebralJson.Options)
            };
            profile.FollowsDefaultModel = false; profile.FollowsModelBudgets = false;
            profile.ResponseTokens = 512; profile.MaxSteps = 8; profile.MaxSeconds = 120;
            profile.AutomaticCompaction = false;
            await client.GetAsync<AgentProfile>("agents.save", profile, cancellationToken: token);
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest(
                RunId, RunId, ProfileId, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Compute 2 + 3. Reply exactly: AUDIT_CPU_OK: 5. Do not use tools or delegate.", null, WorkflowContract.EmptyObject),
                cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { run.Id, run.Status, run.Revision, run.Submission.AuditPolicy }, CerebralJson.Options));
            return;
        }

        var retained = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = RunId }, cancellationToken: token);
        if (retained.Submission.ProfileId != ProfileId || retained.Submission.AuditPolicy?.AdapterId != AdapterId)
            throw new InvalidOperationException("The retained canary does not have the intended isolated audit policy.");
        var page = await client.GetAsync<JsonElement>("jobs.workflow.events", new { id = RunId, limit = 200 }, cancellationToken: token);
        var audits = page.GetProperty("events").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "workflow.audit")
            .Select(item => new { sequence = item.GetProperty("sequence").GetInt64(), data = item.GetProperty("data").Clone() }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { retained.Id, retained.Status, retained.Revision, retained.Error,
            retained.Result, retained.ActiveMilliseconds, audits }, CerebralJson.Options));
        if (retained.IsTerminal && retained.Status != WorkflowStatus.Completed)
            throw new InvalidOperationException("The native canary ended without ordinary completion.");
        if (retained.Status == WorkflowStatus.Completed && (audits.Length == 0 || audits.Any(item =>
            item.data.GetProperty("result").GetProperty("adapterId").GetString() != AdapterId ||
            item.data.GetProperty("result").GetProperty("disposition").GetString() is not ("accepted" or "gap" or "insufficient_evidence"))))
            throw new InvalidOperationException("The completed canary has no attributed stock Laya audit receipt.");
    }
}
