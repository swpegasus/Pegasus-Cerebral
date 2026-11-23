using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker.Tests;

public static class RetentionCoordinatorTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            assertions++; Console.WriteLine("PASS: " + message);
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-retention-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // WorkflowStore.Submit uses the host clock. Anchor this fixture to that clock so
            // its +10-day sweep really ages the newly admitted active run as time advances.
            var now = DateTimeOffset.UtcNow;
            await DormantCountPinAndIsolation(Path.Combine(root, "dormant"), now, Check);
            await Pagination(Path.Combine(root, "pagination"), now, Check);
            await SettingsChange(Path.Combine(root, "settings"), now, Check);
            await Cancellation(Path.Combine(root, "cancellation"), now, Check);
            Console.WriteLine($"Retention coordinator: {assertions} deterministic SQLite assertions passed; no live service or provider invoked.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task DormantCountPinAndIsolation(string root, DateTimeOffset now, Action<bool, string> check)
    {
        var (state, configuration, journal, events) = Fixture(root);
        var settings = configuration.Get();
        settings.ActivityHistoryLimit = 100; settings.ActivityHistoryDays = 1;
        settings.EngineEventHistoryLimit = 1; settings.EngineEventHistoryMegabytes = 1; settings.EngineEventHistoryDays = 1;
        configuration.Save(settings);

        var durableSession = new TaskSession { Id = "durable-session", ProfileId = "lead", Messages =
        [
            new() { Sequence = 1, Role = "user", Origin = "user", Content = "Durable request" },
            new() { Sequence = 2, Role = "assistant", Origin = "agent", Content = "Durable response" }
        ] };
        state.Put("session", durableSession.Id, durableSession);
        InsertActivity(state, durableSession.Id, "old-terminal", "completed", now.AddDays(-10));
        InsertActivity(state, "recent-session", "recent-terminal", "completed", now.AddDays(10));
        InsertActivity(state, "running-session", "old-running", "running", now.AddDays(-10));
        for (var index = 0; index < 102; index++)
            InsertActivity(state, "count-session", "count-" + index, "completed", now.AddDays(10));

        journal.Append(Accepted("durable-input", "durable-conversation", now.AddDays(-10)), 0);
        journal.Append(Rolling("durable-rolling", "durable-conversation", now.AddDays(-10)), 1);
        var workflow = new WorkflowStore(journal);
        var active = workflow.Submit(Submission("active-run", "active-conversation"));
        check(active.Status == WorkflowStatus.Queued, "fixture establishes an active workflow before retention runs");

        var coordinator = new RetentionCoordinator(journal, state, configuration, events,
            interval: TimeSpan.FromHours(1), pageSize: 64, maximumPagesPerSweep: 8);
        var result = await coordinator.RunSweepAsync(now.AddDays(10));

        check(result.ActivityEntriesRemoved == 3 &&
              state.ReadActivity(durableSession.Id, 1, 0, 10).Entries.Count == 0 &&
              state.ReadActivity("count-session", 1, 0, 200).TotalCount == 100,
            "a dormant session is age-pruned and a busy legacy activity session is count-pruned without a new tool call");
        check(state.ReadActivity("recent-session", 1, 0, 10).TotalCount == 1 &&
              state.ReadActivity("running-session", 1, 0, 10).Entries.Single().State == "running",
            "recent and running activity rows remain pinned");
        check(state.GetSession(durableSession.Id, messageLimit: 10)!.Messages.Count == 2,
            "retention never removes durable conversation messages");
        check(journal.ReadPage("durable-conversation").Events.Select(x => x.Id).SequenceEqual(["durable-input"]),
            "Engine cleanup removes only rolling non-model activity and preserves accepted model context");
        check(journal.ReadPage("active-conversation").Events.Any(x => x.Type == CerebralEventTypes.WorkflowTransition) &&
              result.PressureCount > 0,
            "active workflow evidence stays pinned and reports bounded retention pressure");
    }

    private static async Task Pagination(string root, DateTimeOffset now, Action<bool, string> check)
    {
        var (state, configuration, journal, events) = Fixture(root);
        var settings = configuration.Get();
        settings.ActivityHistoryLimit = 100; settings.ActivityHistoryDays = 1;
        settings.EngineEventHistoryLimit = 1; settings.EngineEventHistoryMegabytes = 1; settings.EngineEventHistoryDays = 1;
        configuration.Save(settings);
        foreach (var id in new[] { "a", "b", "c" })
        {
            InsertActivity(state, id, "activity-" + id, "completed", now.AddDays(-5));
            journal.Append(Rolling("event-" + id, id, now.AddDays(-5)), 0);
        }
        var coordinator = new RetentionCoordinator(journal, state, configuration, events,
            interval: TimeSpan.FromHours(1), pageSize: 1, maximumPagesPerSweep: 1);
        var visited = new List<RetentionSweepResult>();
        for (var index = 0; index < 3; index++) visited.Add(await coordinator.RunSweepAsync(now));
        check(visited.All(x => x.EngineConversationsVisited == 1 && x.ActivitySessionsVisited == 1) &&
              new[] { "a", "b", "c" }.All(id => journal.ReadPage(id).Events.Count == 0 &&
                  state.ReadActivity(id, 1, 0, 10).TotalCount == 0),
            "bounded one-item pages resume across sweeps without skipping conversations or activity sessions");
    }

    private static async Task SettingsChange(string root, DateTimeOffset now, Action<bool, string> check)
    {
        var (state, configuration, journal, events) = Fixture(root);
        var settings = configuration.Get();
        settings.EngineEventHistoryDays = 30; configuration.Save(settings);
        journal.Append(Rolling("settings-event", "settings-chat", now.AddDays(-5)), 0);
        var coordinator = new RetentionCoordinator(journal, state, configuration, events,
            interval: TimeSpan.FromHours(1), pageSize: 64, maximumPagesPerSweep: 8);
        var first = await coordinator.RunSweepAsync(now);
        settings = configuration.Get(); settings.EngineEventHistoryDays = 1; configuration.Save(settings);
        var second = await coordinator.RunSweepAsync(now);
        check(first.EngineEventsRemoved == 0 && second.EngineEventsRemoved == 1 && journal.ReadPage("settings-chat").Events.Count == 0,
            "each sweep reloads saved settings and applies a changed age limit without restarting the Worker");
    }

    private static async Task Cancellation(string root, DateTimeOffset now, Action<bool, string> check)
    {
        var (state, configuration, journal, events) = Fixture(root);
        var settings = configuration.Get(); settings.EngineEventHistoryDays = 1; settings.ActivityHistoryDays = 1;
        configuration.Save(settings);
        journal.Append(Rolling("cancel-event", "cancel-chat", now.AddDays(-5)), 0);
        InsertActivity(state, "cancel-chat", "cancel-activity", "completed", now.AddDays(-5));
        var coordinator = new RetentionCoordinator(journal, state, configuration, events,
            interval: TimeSpan.FromHours(1), pageSize: 64, maximumPagesPerSweep: 8);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var rejected = false;
        try { await coordinator.RunSweepAsync(now, cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected && journal.ReadPage("cancel-chat").Events.Count == 1 &&
              state.ReadActivity("cancel-chat", 1, 0, 10).TotalCount == 1,
            "cancellation before a sweep leaves both journals unchanged");
    }

    private static (StateStore State, Configuration Configuration, EventJournal Journal, EventHub Events) Fixture(string root)
    {
        Directory.CreateDirectory(root);
        var state = new StateStore(root);
        return (state, new Configuration(state), new EventJournal(Path.Combine(root, "engine.db")), new EventHub());
    }

    private static void InsertActivity(StateStore store, string session, string id, string state, DateTimeOffset at) =>
        store.InsertActivity(new ActivityEntry
        {
            Id = id, SessionId = session, ActorSessionId = session, JobId = id, RootJobId = id,
            TurnSequence = 1, ProfileId = "lead", ToolName = "fixture", Title = "Fixture", State = state,
            StartedAt = at, UpdatedAt = at
        });

    private static CerebralEvent Rolling(string id, string conversation, DateTimeOffset at) => new(id, conversation, "turn", 0,
        CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service, "retention-fixture"), [new TextContentPart("diagnostic")],
        new(ModelInclusion.OnDemand, HumanVisibility.Activity, RetentionPolicy.Rolling),
        new(EventSourceKind.Service, "retention-fixture"), at, JsonSerializer.SerializeToElement(new { reason = "fixture" }));

    private static CerebralEvent Accepted(string id, string conversation, DateTimeOffset at) => new(id, conversation, "turn", 0,
        CerebralEventTypes.Message, new(EventActorKind.User, "owner"), [new TextContentPart("accepted input")],
        new(ModelInclusion.Required, HumanVisibility.Transcript, RetentionPolicy.Durable),
        new(EventSourceKind.User, "owner"), at);

    private static WorkflowSubmission Submission(string id, string conversation) => new(id, conversation, "lead",
        "test.review:review", "1.0.0", "Review.", "Keep active evidence.", JsonSerializer.SerializeToElement(new { text = "fixture" }),
        JsonSerializer.SerializeToElement(new { capabilities = new[] { "diagnostics" } }));
}
