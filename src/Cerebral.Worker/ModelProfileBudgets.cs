using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

internal static class ModelProfileBudgets
{
    public static IReadOnlyList<AgentProfile> Resolve(StateStore store, ModelRecord model, int? automaticContextTokens = null)
    {
        var profiles = ResolveDetached(store.List<AgentProfile>("agent"), model, automaticContextTokens);
        foreach (var profile in profiles) ModelCapabilityPolicy.EnsureConfigured(profile, model);
        return profiles;
    }

    public static IReadOnlyList<AgentProfile> ResolveForRuntime(StateStore store, ModelRecord model, int? automaticContextTokens = null)
    {
        var source = store.List<AgentProfile>("agent");
        // Unchanged profiles still require capability admission before they are omitted from writes.
        foreach (var profile in source.Where(x => x.ModelWorkerId == "local" && x.ModelId == model.Id))
            ModelCapabilityPolicy.EnsureConfigured(profile, model);
        return ResolveRuntimeDetached(source, model, automaticContextTokens);
    }

    // Explicit model edits invalidate every local target profile, including unchanged pinned budgets.
    internal static IReadOnlyList<AgentProfile> ResolveDetached(IEnumerable<AgentProfile> source,
        ModelRecord model, int? automaticContextTokens = null) =>
        ResolveDetached(source, model, automaticContextTokens, reviseUnchanged: true);

    internal static IReadOnlyList<AgentProfile> ResolveRuntimeDetached(IEnumerable<AgentProfile> source,
        ModelRecord model, int? automaticContextTokens = null) =>
        ResolveDetached(source, model, automaticContextTokens, reviseUnchanged: false);

    private static IReadOnlyList<AgentProfile> ResolveDetached(IEnumerable<AgentProfile> source,
        ModelRecord model, int? automaticContextTokens, bool reviseUnchanged)
    {
        var profiles = new List<AgentProfile>();
        foreach (var current in source.Where(x => x.ModelWorkerId == "local" && x.ModelId == model.Id))
        {
            var profile = PreparedModelSave.Copy(current);
            var budget = AgentBudgetPolicy.Resolve(profile, model, automaticContextTokens);
            profile.ContextTokens = budget.ContextTokens;
            profile.ResponseTokens = budget.ResponseTokens;
            profile.ReasoningMode = budget.ReasoningMode;
            profile.ReasoningTokens = budget.ReasoningTokens;
            Configuration.ValidateProfile(profile);
            // Compare all persisted fields while the candidate still carries the expected revision.
            if (!reviseUnchanged &&
                JsonElement.DeepEquals(CerebralJson.Element(current), CerebralJson.Element(profile)))
                continue;
            profile.Revision = checked(profile.Revision + 1);
            profiles.Add(profile);
        }
        return profiles;
    }
}
