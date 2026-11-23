using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class AgentProfileNoOpSaveTests
{
    public static async Task<int> RunAsync(string privateParent)
    {
        if (!Path.IsPathFullyQualified(privateParent))
            throw new ArgumentException("An absolute private fixture parent is required.");
        var root = Path.Combine(Path.GetFullPath(privateParent), "profile-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(root, "fixture.gguf");
        await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
        var store = new StateStore(Path.Combine(root, "state"));
        var configuration = new Configuration(store);
        var events = new EventHub();
        var (subscription, reader) = events.Subscribe();
        await using var runtime = new ModelRuntime(store, configuration.Get, events);
        await using var tools = new ToolDispatcher(configuration.Get);
        using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events,
            NullLogger<JobScheduler>.Instance);
        var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
        store.Put("model", "fixture-model", new ModelRecord
        {
            Id = "fixture-model", Name = "Fixture model", Path = modelPath, ContextTokens = 8192
        });
        // A fresh host with no default model runs a separate legacy migration when its first
        // lead appears. Pin the normal explicit default so this fixture isolates agents.save.
        var defaults = configuration.Get();
        defaults.DefaultModelId = "fixture-model";
        configuration.Save(defaults);
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
            checks++;
        }
        int Updates()
        {
            var count = 0;
            while (reader.TryRead(out var item)) if (item.Type == "agents.updated") count++;
            return count;
        }
        async Task<CommandResult> Save(AgentProfile profile) =>
            await commands.ExecuteAsync(new("agents.save", CerebralJson.Element(profile)));
        AgentProfile Clone(AgentProfile profile) => CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));

        try
        {
            var input = new AgentProfile { Id = "profile-a", Name = "Helper", ModelId = "fixture-model" };
            var created = await Save(input);
            var current = store.Get<AgentProfile>("agent", input.Id)!;
            Check(created.Success && current.Revision == 1 && Updates() == 1,
                "A new profile must still be written at revision one and publish an update.");

            var identical = await Save(Clone(current));
            var returned = CerebralJson.Read<AgentProfile>(identical.Data);
            Check(identical.Success && returned.Revision == 1 &&
                JsonElement.DeepEquals(CerebralJson.Element(returned), CerebralJson.Element(current)) &&
                store.Get<AgentProfile>("agent", input.Id)!.Revision == 1 && Updates() == 0,
                "An identical full-profile save must return the current record without a revision or event.");

            var changed = Clone(current); changed.Name = "Edited helper";
            var edited = await Save(changed);
            current = store.Get<AgentProfile>("agent", input.Id)!;
            Check(edited.Success && current.Revision == 2 && current.Name == "Edited helper" && Updates() == 1,
                "A real field edit must advance the revision and publish an update.");

            var stale = Clone(current); stale.Revision = 1;
            var refused = await Save(stale);
            Check(!refused.Success && store.Get<AgentProfile>("agent", input.Id)!.Revision == 2 && Updates() == 0,
                "An identical but stale expected revision must still refuse.");

            var granted = Clone(current); granted.Capabilities.Add("filesystem.read");
            var grantSave = await Save(granted);
            current = store.Get<AgentProfile>("agent", input.Id)!;
            Check(grantSave.Success && current.Revision == 3 &&
                current.Capabilities.Contains("filesystem.read", StringComparer.Ordinal) && Updates() == 1,
                "A changed grant must advance the revision and publish an update.");

            var second = Clone(current); second.Id = "profile-b"; second.Role = "lead"; second.Revision = 0;
            var newProfile = await Save(second);
            var lead = store.Get<AgentProfile>("agent", second.Id)!;
            Check(newProfile.Success && lead.Revision == 1 && lead.FollowsDefaultModel && Updates() == 1,
                "A new lead must remain a create and retain its normal default-model normalization.");
            var repeatedLead = await Save(Clone(lead));
            Check(repeatedLead.Success && store.Get<AgentProfile>("agent", second.Id)!.Revision == 1 && Updates() == 0,
                "A normalized, identical lead save must not stale an exact profile reference.");

            var helperRevisionAfterLead = store.Get<AgentProfile>("agent", input.Id)!.Revision;
            Check(helperRevisionAfterLead == current.Revision,
                $"Creating and repeating a separate lead must not edit this helper profile (stored={helperRevisionAfterLead}, captured={current.Revision}).");

            var left = Clone(current); left.Name = "First concurrent edit";
            var right = Clone(current); right.Name = "Second concurrent edit";
            var race = await Task.WhenAll(Save(left), Save(right));
            var raceRevision = store.Get<AgentProfile>("agent", input.Id)!.Revision;
            var raceUpdates = Updates();
            Check(race.Count(result => result.Success) == 1 && raceRevision == 4 && raceUpdates == 1,
                $"Two meaningful writes from one revision must still have one winner and one stale refusal " +
                $"(successes={race.Count(result => result.Success)}, revision={raceRevision}, updates={raceUpdates}, " +
                $"errors={string.Join(" | ", race.Select(result => result.Error))}).");
            return checks;
        }
        finally { events.Unsubscribe(subscription); }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Profile-save checks never run model work.");
    }
}
