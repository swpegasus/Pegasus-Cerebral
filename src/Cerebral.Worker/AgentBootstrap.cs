using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<object> BootstrapAgentsAsync(string modelId, string? cortexServerId, CancellationToken token)
    {
        // Acquire the asynchronous model gate before the short SQLite transaction, in the same
        // order as individual agent saves. Bootstrap's fixed templates require text only.
        await modelFiles.WaitAsync(token);
        try
        {
        token.ThrowIfCancellationRequested();
        return store.Locked(() =>
        {
            if (store.List<AgentProfile>("agent").Any(x => x.Role == "lead"))
                throw new InvalidOperationException("Cerebral is already configured. Edit its profile in Agents.");
            var model = store.Get<ModelRecord>("model", modelId) ?? throw new ArgumentException("Import the selected model first.");
            if (model.State != "loaded") throw new InvalidOperationException("Load the model successfully before creating the default agents.");
            var current = configuration.Get();
            if (cortexServerId is not null && !current.McpServers.Any(x => x.Id == cortexServerId && x.Enabled))
                throw new ArgumentException("Choose an enabled exact Cortex connection ID, or omit Cortex for now.");
            var profiles = new List<AgentProfile>();
            foreach (var (name, role, purpose, instructions) in new[]
            {
                (
                    "Cerebral",
                    "lead",
                    "Own the conversation, handle straightforward work directly, delegate only useful bounded assignments, and integrate verified results.",
                    "Own the user-facing task. Handle straightforward work directly. Delegate only bounded work that benefits from a specialist or parallel effort, give each helper exact scope, and integrate verified results. Continue until the requested outcome is usable. Surface material questions, failures, and uncertainty without routine command narration."),
                (
                    "Coding helper",
                    "helper",
                    "Read, implement, build, and test the assigned code change. Return the result, exact changed paths, evidence, and remaining blockers concisely.",
                    "Inspect relevant source and project instructions before changing code. Fix the root cause, preserve unrelated work, use configured development tools, and run focused checks. Return the outcome first, followed by changed paths, evidence level, blockers, and only the implementation detail Cerebral needs."),
                (
                    "Analysis helper",
                    "helper",
                    "Check calculations and technical reasoning; return conclusions with only the supporting steps Cerebral needs.",
                    "Solve the assigned calculation or technical analysis explicitly. Check units, assumptions, and boundary cases, using configured tools when useful. Return the conclusion and only the supporting steps and provenance Cerebral needs. State uncertainty rather than filling gaps with assumptions."),
                (
                    "Writing helper",
                    "helper",
                    "Draft and revise clear English for the assigned audience; preserve facts and return usable text.",
                    "Write or revise for the requested audience, voice, and format. Preserve established facts and project terminology. Treat retrieved Cortex material as reference; propose changes to established story or project knowledge and ask the author before adopting them. Return usable text plus any choices that need an answer."),
                (
                    "Context compactor",
                    "compactor",
                    "Produce a concise checkpoint preserving user intent, decisions, evidence, unresolved work, and exact references.",
                    "Return one concise checkpoint for the session manager. Preserve user intent, accepted requirements, decisions, evidence, changed paths, current state, blockers, next action, and exact references. Remove duplicate narration and raw logs. Do not add decisions, expose secrets or private reasoning, invoke tools, or modify source.")
            })
            {
                var profile = new AgentProfile { Id = Guid.NewGuid().ToString("N"), Name = name, Role = role, Purpose = purpose, Instructions = instructions, ModelId = model.Id,
                    FollowsDefaultModel = true, FollowsModelBudgets = true,
                    MaxSteps = role == "lead" ? 80 : 30, MaxSeconds = role == "lead" ? 3600 : 1200, AllowedRoots = [current.ProjectsDirectory],
                    AllowedTools = ["*"], Capabilities = ["diagnostics", "filesystem.read", "filesystem.write", "process.execute", "git.read", "git.write", "math.calculate", "network.http", "shared.read", "shared.write"] };
                if (cortexServerId is not null) profile.Capabilities.Add("mcp." + cortexServerId);
                if (role == "lead")
                {
                    profile.Capabilities.Add("agents.delegate");
                    profile.Capabilities.Add("desktop.plugins.manage");
                }
                profile = DefaultModelProfileSettings.ForModel(profile, model);
                profiles.Add(profile);
            }
            var compactor = profiles.Single(x => x.Role == "compactor");
            current.DefaultModelId = model.Id;
            current.DefaultModelWorkerId = "local";
            configuration.Save(current);
            foreach (var profile in profiles)
            {
                if (profile != compactor) profile.CompactionProfileId = compactor.Id;
                else { profile.AutomaticCompaction = false; profile.Capabilities = ["diagnostics"]; profile.AllowedTools = []; }
                CommitAgent(profile, requireImageVerification: false);
            }
            return profiles;
        });
        }
        finally { modelFiles.Release(); }
    }
}
