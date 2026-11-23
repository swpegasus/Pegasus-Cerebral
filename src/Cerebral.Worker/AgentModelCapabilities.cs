using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<AgentProfile> SaveTaskBudgetPolicyAsync(JsonElement args, CancellationToken token)
    {
        var fields = args.EnumerateObject().Select(x => x.Name).ToArray();
        if (fields.Length != 3 || fields.Distinct(StringComparer.Ordinal).Count() != 3 ||
            fields.Any(x => x is not ("id" or "expectedRevision" or "policy")))
            throw new ArgumentException("Task budget policy update requires exactly id, expectedRevision and policy.");
        var id = Text(args, "id");
        Configuration.ValidateId(id);
        if (!args.GetProperty("expectedRevision").TryGetInt32(out var revision) || revision < 0)
            throw new ArgumentException("A nonnegative agent revision is required.");
        var policyValue = args.GetProperty("policy");
        if (policyValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            throw new ArgumentException("Policy must be a closed object or null to clear it.");
        if (policyValue.ValueKind == JsonValueKind.Object)
        {
            var names = policyValue.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Length < 2 || names.Length > 4 ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(x => x is not ("schemaVersion" or "maxContextTokens" or
                    "maxResponseTokens" or "allowAutonomousSelection")) ||
                !policyValue.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
                !policyValue.TryGetProperty("allowAutonomousSelection", out var selection) ||
                selection.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Task budget policy has an unsupported or ambiguous shape.");
            foreach (var name in new[] { "maxContextTokens", "maxResponseTokens" })
                if (policyValue.TryGetProperty(name, out var ceiling) &&
                    ceiling.ValueKind is not JsonValueKind.Null &&
                    (ceiling.ValueKind != JsonValueKind.Number || !ceiling.TryGetInt32(out _)))
                    throw new ArgumentException("Task budget ceilings must be integer tokens or null.");
        }
        var policy = policyValue.ValueKind == JsonValueKind.Null ? null :
            CerebralJson.Read<AgentTaskBudgetPolicy>(policyValue);
        await modelFiles.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = store.Get<AgentProfile>("agent", id) ?? throw new ArgumentException("The agent does not exist.");
            if (before.Revision != revision)
                throw new InvalidOperationException("The agent changed while this policy was open. Refresh before saving.");
            ModelRecord model;
            Action? revalidate = null;
            if (before.ModelWorkerId == "local")
                model = store.Get<ModelRecord>("model", before.ModelId) ?? throw new ArgumentException("The selected model is unavailable.");
            else
            {
                using var owner = await remoteModels.ConnectAsync(before.ModelWorkerId, token).ConfigureAwait(false);
                model = await owner.InspectAsync(before.ModelId, token).ConfigureAwait(false);
                await owner.RevalidateAsync(token).ConfigureAwait(false);
                revalidate = owner.EnsureCurrent;
                return Publish(CommitPolicy());
            }
            return Publish(CommitPolicy());

            AgentProfile Publish((AgentProfile Profile, bool Changed) result)
            {
                if (result.Changed) events.Publish("agents.updated", null, new { profileId = id });
                return result.Profile;
            }

            (AgentProfile Profile, bool Changed) CommitPolicy() => store.Locked(() =>
            {
                revalidate?.Invoke();
                var current = store.Get<AgentProfile>("agent", id) ?? throw new InvalidOperationException("The agent was removed.");
                if (current.Revision != revision || current.ModelId != before.ModelId ||
                    current.ModelWorkerId != before.ModelWorkerId)
                    throw new InvalidOperationException("The agent changed while this policy was open. Refresh before saving.");
                var proposed = CerebralJson.Read<AgentProfile>(CerebralJson.Element(current));
                proposed.TaskBudgetPolicy = policy;
                Configuration.ValidateProfile(proposed);
                if (policy is { } grant)
                {
                    var native = model.NativeContextTokens ?? model.ContextTokens;
                    var registered = model.MaximumContextTokens > 0 ? model.MaximumContextTokens : native;
                    var maximum = Math.Min(1_048_576, Math.Min(native, registered));
                    if (grant.MaxContextTokens is { } context && context > maximum)
                        throw new ArgumentException("The owner context ceiling exceeds the registered model limit.");
                    if (grant.MaxResponseTokens is { } output && output >= (grant.MaxContextTokens ?? maximum))
                        throw new ArgumentException("The owner output ceiling leaves no input room under this model.");
                }
                if (policy == current.TaskBudgetPolicy) return (current, false);
                proposed.Revision = checked(current.Revision + 1);
                store.Put("agent", id, proposed);
                return (proposed, true);
            });
        }
        finally { modelFiles.Release(); }
    }

    private async Task<AgentProfile> SaveAgentAsync(AgentProfile profile, CancellationToken token)
    {
        Configuration.ValidateProfile(profile);
        await modelFiles.WaitAsync(token);
        try
        {
            var current = store.Get<AgentProfile>("agent", profile.Id);
            if (profile.Revision != (current?.Revision ?? 0))
                throw new InvalidOperationException("The agent changed while this copy was open. Refresh before saving; current settings were preserved.");
            var needsImage = ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput);
            var unchangedImageBinding = current is not null && current.ModelId == profile.ModelId &&
                current.WorkerId == profile.WorkerId && current.ModelWorkerId == profile.ModelWorkerId &&
                ModelCapabilityPolicy.Requirements(current).Contains(ModelCapabilityIds.ImageInput);
            // An existing/restored role remains editable while its runtime is idle. New image
            // assignments prove the actual instance before the short revision-bound commit.
            var verify = needsImage && !unchangedImageBinding;
            if (profile.ModelWorkerId != "local")
            {
                using var owner = await remoteModels.ConnectAsync(profile.ModelWorkerId, token);
                var model = await owner.InspectAsync(profile.ModelId, token);
                if (verify)
                {
                    var proof = await owner.VerifyImageAsync(profile.ModelId, token);
                    if (proof.ModelId != profile.ModelId) throw new InvalidOperationException("Remote image readiness returned a different model identity.");
                    model = await owner.InspectAsync(profile.ModelId, token);
                }
                RemoteManagedWorkerSession.EnsureCapabilities(profile, model, configurationOnly: !verify);
                await owner.RevalidateAsync(token);
                token.ThrowIfCancellationRequested();
                return CommitAgent(profile, verify, model, owner.EnsureCurrent, noOpWhenUnchanged: true);
            }
            using var readiness = verify ? await runtime.AcquireRequiredCapabilitiesAsync(profile, token) : null;
            token.ThrowIfCancellationRequested();
            return CommitAgent(profile, verify, noOpWhenUnchanged: true);
        }
        finally { modelFiles.Release(); }
    }

    private async Task<ModelImageVerification> VerifyModelImageAsync(JsonElement args, CancellationToken token)
    {
        if (args.EnumerateObject().Any(x => x.Name != "id") || args.EnumerateObject().Count() != 1)
            throw new ArgumentException("Image verification accepts only a model id.");
        var id = Text(args, "id"); Configuration.ValidateId(id);
        await modelFiles.WaitAsync(token);
        try { return await runtime.VerifyImageInputAsync(id, token); }
        finally { modelFiles.Release(); }
    }
}
