using System.Text;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ActivityJournalTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-activity-" + Guid.NewGuid().ToString("N")));
        var assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); assertions++; Console.WriteLine("PASS: " + message); }
        static bool Rejected<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        try
        {
            var store = new StateStore(root); var configuration = new Configuration(store); var events = new EventHub();
            var journal = new ActivityJournal(store, events, null, TimeSpan.FromMilliseconds(25)); var fake = new FixtureTools();
            var recording = new RecordingToolDispatcher(fake, journal);
            var modelPath = Path.Combine(root, "fixture.gguf");
            await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
            store.Put("model", "fixture", new ModelRecord
            {
                Id = "fixture", Name = "Activity fixture model", Path = modelPath,
                DeclaredType = "model", MetadataSource = "fixture"
            });
            var profile = new AgentProfile { Id = "lead", Name = "Activity fixture lead", ModelId = "fixture", Role = "lead" };
            var helper = new AgentProfile { Id = "helper", Name = "Activity fixture helper", ModelId = "fixture", Role = "helper" };
            store.Put("agent", profile.Id, profile); store.Put("agent", helper.Id, helper);
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            using var scheduler = new JobScheduler(store, configuration, new NoExecution(), runtime, events, NullLogger<JobScheduler>.Instance);
            var lead = scheduler.Submit(new() { ProfileId = profile.Id, Prompt = "Inspect the project." });
            var child = scheduler.Submit(new() { ProfileId = helper.Id, ParentJobId = lead.Id, Prompt = "Inspect one file." });
            var sessionId = lead.SessionId; var turn = lead.StartSequence;
            var context = new ToolExecutionContext(profile, sessionId, lead.Id);
            var subscribed = events.Subscribe();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Execute = (_, _, _) => { entered.TrySetResult(); return release.Task; };
            var call = new ToolCall("file.read", CerebralJson.Element(new { path = "sample.cs" }));
            var pending = recording.ExecuteAsync(call, context, default);
            await entered.Task;
            var start = journal.List(sessionId, turn).Entries.Single();
            Check(start.State == "running" && start.Result == "" && start.Arguments.Contains("sample.cs", StringComparison.Ordinal),
                "a real dispatch starts one durable running activity row before the provider completes");
            await Task.Delay(80);
            var running = journal.List(sessionId, turn).Entries.Single();
            Check(running.Id == start.Id && running.State == "running" && running.Result.StartsWith("Still running", StringComparison.Ordinal),
                "a long-running tool refreshes its existing activity row without creating another call");
            var result = new ToolResult(true, "Actual file content.");
            release.SetResult(result);
            Check(ReferenceEquals(await pending, result), "recording preserves the original tool result object");
            var finished = journal.List(sessionId, turn).Entries.Single();
            Check(finished.Id == start.Id && finished.Sequence == start.Sequence && finished.State == "completed" && finished.Result == result.Content,
                "completion updates the original activity row without changing its paging sequence");
            var notifications = new List<ServiceEvent>(); while (subscribed.Reader.TryRead(out var item)) notifications.Add(item);
            Check(notifications.Count(x => x.Type == "activity.updated") >= 3, "start, bounded progress, and completion publish activity.updated");
            events.Unsubscribe(subscribed.Id);

            fake.Execute = (_, _, _) => Task.FromResult(new ToolResult(false, "Build failed with a real error."));
            await recording.ExecuteAsync(new("process.run", CerebralJson.Element(new { executableId = "dotnet", arguments = new[] { "build" } })), context, default);
            Check(journal.List(sessionId, turn).Entries[^1].State == "failed" && journal.List(sessionId, turn).Entries[^1].Title == "Run dotnet build", "a returned tool failure remains a failed activity with a safe readable command title");
            var originalError = new IOException("Fixture read failed.");
            fake.Execute = (_, _, _) => Task.FromException<ToolResult>(originalError);
            Exception? observed = null;
            try { await recording.ExecuteAsync(call, context, default); } catch (Exception ex) { observed = ex; }
            Check(ReferenceEquals(observed, originalError) && journal.List(sessionId, turn).Entries[^1].State == "failed",
                "thrown provider errors propagate unchanged while their activity records the failure");
            using var cancel = new CancellationTokenSource();
            fake.Execute = (_, _, token) => { cancel.Cancel(); return Task.FromCanceled<ToolResult>(token); };
            var cancelled = false;
            try { await recording.ExecuteAsync(call, context, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && journal.List(sessionId, turn).Entries[^1].State == "cancelled",
                "tool cancellation remains cancellation and updates its running row");

            fake.Execute = (_, _, _) => Task.FromResult(new ToolResult(false, "Cortex connection closed without a receipt."));
            var mutation = new ToolCall("mcp.cortex.cortex_task", CerebralJson.Element(new { action = "update", taskId = "fixture-task", expectedRevision = 1 }));
            await recording.ExecuteAsync(mutation, context, default);
            Check(journal.List(sessionId, turn).Entries[^1].State == "uncertain", "an unconfirmed Cortex write is uncertain rather than a no-effect failure");
            fake.Execute = (_, _, _) => Task.FromResult(new ToolResult(false, "{\"structuredContent\":{\"disposition\":\"stale-revision\"}}"));
            await recording.ExecuteAsync(mutation, context, default);
            Check(journal.List(sessionId, turn).Entries[^1].State == "failed", "an explicit Cortex no-commit receipt permits a failed activity outcome");

            const string password = "PRIVATE_ACTIVITY_PASSWORD";
            const string apiKey = "PRIVATE_ACTIVITY_API_KEY";
            fake.Execute = (_, _, _) => Task.FromResult(new ToolResult(true, password + " " + apiKey + " " +
                string.Concat(Enumerable.Repeat("\u03bb\U0001F600", 10000))));
            var sensitive = new ToolCall("http.request", CerebralJson.Element(new { url = "http://localhost/", password,
                headers = new { api_key = apiKey }, settings = new { AccessKey = "PRIVATE_CONFIG_KEY" },
                note = new string('x', 30000) }));
            var complete = await recording.ExecuteAsync(sensitive, context, default);
            var sanitized = journal.List(sessionId, turn).Entries[^1];
            Check(sanitized.Truncated && new UTF8Encoding(false, true).GetByteCount(sanitized.Arguments) <= ActivityJournal.MaxArgumentBytes &&
                new UTF8Encoding(false, true).GetByteCount(sanitized.Result) <= ActivityJournal.MaxResultBytes,
                "activity arguments and results remain within independent UTF-8 bounds");
            Check(!sanitized.Arguments.Contains(password, StringComparison.Ordinal) && !sanitized.Arguments.Contains(apiKey, StringComparison.Ordinal) &&
                !sanitized.Arguments.Contains("PRIVATE_CONFIG_KEY", StringComparison.Ordinal) &&
                !sanitized.Result.Contains(password, StringComparison.Ordinal) && !sanitized.Result.Contains(apiKey, StringComparison.Ordinal) &&
                complete.Content.Contains(password, StringComparison.Ordinal),
                "known secret-bearing fields and echoed secrets are redacted only from the journal");

            fake.Execute = (_, _, _) => Task.FromResult(new ToolResult(true, "Small actual result."));
            await recording.ExecuteAsync(new("mcp.cortex.cortex_search", CerebralJson.Element(new { namespaces = new[] { "project.fixture" }, text = "guidance" })), context, default);
            await recording.ExecuteAsync(new("mcp.cortex.cortex_read", CerebralJson.Element(new { @namespace = "project.fixture", recordType = "knowledge", recordId = "guide" })), context, default);
            await recording.ExecuteAsync(new("mcp.cortex.cortex_start", CerebralJson.Element(new { namespaces = new[] { "project.fixture" }, taskId = sessionId })), context, default);
            Check(journal.List(sessionId, turn).Entries.Count(x => x.ToolName.StartsWith("mcp.cortex.", StringComparison.Ordinal)) == 5 &&
                !store.Get<TaskSession>("session", sessionId)!.Messages.Any(x => x.Content.Contains("Small actual result.", StringComparison.Ordinal)),
                "service-style Cortex calls are journaled independently of model-context messages");

            scheduler.Steer(lead.Id, "Now inspect another part.");
            var newTurn = store.Get<TaskSession>("session", sessionId)!.Messages.Last(x => WorkingContext.IsUserRequest(x)).Sequence;
            var laterChild = scheduler.Submit(new() { ProfileId = helper.Id, ParentJobId = lead.Id, Prompt = "Inspect the new part." });
            Check(store.Get<AgentJob>("job", child.Id)!.RootTurnSequence == turn && laterChild.RootTurnSequence == newTurn,
                "helper admission retains its original turn while a helper admitted after steering receives the new turn");
            await recording.ExecuteAsync(call, new(helper, child.SessionId, child.Id), default);
            await recording.ExecuteAsync(call, context, default);
            var helperEntry = journal.List(sessionId, turn).Entries.Last(x => x.JobId == child.Id);
            Check(helperEntry.RootJobId == lead.Id && helperEntry.ActorSessionId == child.SessionId && helperEntry.ProfileId == helper.Id &&
                journal.List(sessionId, newTurn).Entries.Single().JobId == lead.Id,
                "late helper work remains under its admitted turn while the lead follows current user steering");
            Check(journal.Turns(child.SessionId).Count == 0, "helper actor sessions do not duplicate the root conversation journal");
            Check(journal.Turns(sessionId).All(x => x.Latest.Arguments.Length == 0 && x.Latest.Result.Length == 0) && journal.List(sessionId, newTurn).Entries[0].Result.Length > 0,
                "turn summaries omit details while exact activity pages retain their saved results");

            var firstPage = journal.List(sessionId, turn, limit: 3);
            var secondPage = journal.List(sessionId, turn, firstPage.NextAfterSequence!.Value, 3);
            Check(firstPage.Entries.Count == 3 && secondPage.Entries.Count == 3 && firstPage.TotalCount > 6 &&
                firstPage.Entries.All(x => secondPage.Entries.All(y => x.Id != y.Id)) &&
                firstPage.Entries[^1].Sequence < secondPage.Entries[0].Sequence,
                "SQL paging uses stable monotonic sequences without duplicate rows");
            var all = new List<ActivityEntry>(); long cursor = 0;
            do
            {
                var page = journal.List(sessionId, turn, cursor, 3); all.AddRange(page.Entries);
                if (page.NextAfterSequence is not { } next) break;
                cursor = next;
            } while (true);
            Check(all.Count == firstPage.TotalCount && all.Select(x => x.Id).Distinct().Count() == all.Count,
                "paging reaches all stored activity exactly once and ends with a null cursor");
            var another = scheduler.Submit(new() { ProfileId = profile.Id, Prompt = "Separate conversation." });
            Check(journal.Turns(another.SessionId).Count == 0 && journal.List(another.SessionId, turn).TotalCount == 0,
                "conversation activity queries cannot include another conversation's history");
            const string retentionSession = "activity-retention-fixture";
            for (var index = 0; index < 102; index++) store.InsertActivity(new ActivityEntry
            {
                SessionId = retentionSession, ActorSessionId = retentionSession, JobId = "retention", RootJobId = "retention",
                TurnSequence = 1, ProfileId = profile.Id, ToolName = "fixture.retention", Title = "Retention fixture " + index,
                State = "completed", Result = index.ToString()
            });
            store.InsertActivity(new ActivityEntry
            {
                SessionId = retentionSession, ActorSessionId = retentionSession, JobId = "running", RootJobId = "running",
                TurnSequence = 1, ProfileId = profile.Id, ToolName = "fixture.running", Title = "Running fixture", State = "running"
            });
            var removedByCount = store.TrimActivity(retentionSession, 100, 30, trimByAge: false);
            var retainedPage = store.ReadActivity(retentionSession, 1, 0, 200);
            Check(removedByCount == 2 && retainedPage.Entries is { Count: 101 } retained &&
                retained.Count(x => x.State == "running") == 1 && retained.Min(x => x.Result == "" ? int.MaxValue : int.Parse(x.Result)) == 2 &&
                retainedPage.Retention is { PrunedEntries: 2, FirstPrunedSequence: > 0, MaximumEntries: 100, MaximumDays: 30 },
                "rolling activity retention keeps the newest bounded terminal receipts without deleting a running operation");
            var ownershipCutoff = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
            foreach (var ownedSession in new[] { "activity-owned-a", "activity-owned-b", "activity-cancelled" })
                store.InsertActivity(new ActivityEntry
                {
                    SessionId = ownedSession, ActorSessionId = ownedSession, JobId = ownedSession, RootJobId = ownedSession,
                    TurnSequence = 1, ProfileId = profile.Id, ToolName = "fixture.old", Title = "Old fixture", State = "completed",
                    UpdatedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z")
                });
            Check(store.TrimActivity("activity-owned-a", 100, 30, trimByAge: true, now: ownershipCutoff) == 1 &&
                store.ReadActivity("activity-owned-a", 1, 0, 10) is { TotalCount: 0, Retention.PrunedEntries: 1 } &&
                store.ReadActivity("activity-owned-b", 1, 0, 10) is { TotalCount: 1, Retention: null },
                "age cleanup and its bounded metadata are owned by the selected conversation only");
            using (var cancelledTrim = new CancellationTokenSource())
            {
                cancelledTrim.Cancel();
                Check(Rejected<OperationCanceledException>(() => store.TrimActivity("activity-cancelled", 100, 30,
                    trimByAge: true, cancelledTrim.Token, ownershipCutoff)) &&
                    store.ReadActivity("activity-cancelled", 1, 0, 10) is { TotalCount: 1, Retention: null },
                    "cancelled activity cleanup rolls back rows and prune metadata together");
            }
            var retentionRunning = store.ReadActivity(retentionSession, 1, 0, 200).Entries.Single(x => x.State == "running");
            retentionRunning.State = "completed"; store.Put("activity", retentionRunning.Id, retentionRunning);
            await using var concrete = new ToolDispatcher(configuration.Get);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, concrete, events, recordedTools: recording);
            var denied = await commands.ExecuteAsync(new("activity.list", CerebralJson.Element(new { sessionId, turnSequence = turn }), helper.Id));
            var allowed = await commands.ExecuteAsync(new("activity.turns", CerebralJson.Element(new { sessionId }), profile.Id));
            Check(!denied.Success && allowed.Success, "activity commands require the owning root profile or installation authority");
            var invalid = await commands.ExecuteAsync(new("activity.list", CerebralJson.Element(new { sessionId, turnSequence = turn, afterSequence = -1 }), profile.Id));
            Check(!invalid.Success, "activity paging rejects negative cursors");
            var missingContext = await recording.ExecuteAsync(call, new(profile, "direct", "direct"), default);
            Check(missingContext.Success && journal.Turns(sessionId).Count == 2, "unbound direct calls do not invent journal conversations");
            await recording.ExecuteAsync(call, new(profile, sessionId, "direct"), default);
            Check(journal.List(sessionId, newTurn).Entries[^1].JobId == "direct", "a direct call with a real owned conversation is recorded");

            var orphan = journal.Start(call, new(helper, laterChild.SessionId, laterChild.Id))!;
            var interruptedJob = store.Get<AgentJob>("job", laterChild.Id)!; interruptedJob.Status = "interrupted"; store.Put("job", laterChild.Id, interruptedJob);
            Check(journal.List(sessionId, newTurn).Entries.Single(x => x.Id == orphan.Id).State == "uncertain",
                "an interrupted assignment cannot leave a misleading running tool row on reads");
            var reopened = new StateStore(root); var recoveredJournal = new ActivityJournal(reopened, events);
            Check(recoveredJournal.RecoverInterrupted() == 1 &&
                recoveredJournal.List(sessionId, newTurn).Entries.Single(x => x.Id == orphan.Id).State == "uncertain",
                "reopening SQLite preserves history and marks unfinished calls uncertain without replay");
            var afterRestart = recoveredJournal.Start(call, new(profile, sessionId, "direct"))!;
            recoveredJournal.Finish(afterRestart, "completed", "after restart");
            Check(afterRestart.Sequence > all[^1].Sequence && recoveredJournal.Turns(sessionId).Count == 2,
                "activity sequences remain monotonic across store reopen and turn summaries retain both turns");

            var compactOwner = new AgentProfile { Id = "compact-owner", Name = "Missing compactor fixture", ModelId = "fixture", Role = "lead", CompactionProfileId = "missing-compactor" };
            store.Put("agent", compactOwner.Id, compactOwner);
            var compactSession = new TaskSession { ProfileId = compactOwner.Id, Checkpoint = "Preserve existing checkpoint.",
                Messages = [new() { Sequence = 1, Content = "Compact this task." }] };
            store.Put("session", compactSession.Id, compactSession);
            var compactJob = scheduler.Submit(new() { ProfileId = compactOwner.Id, SessionId = compactSession.Id, Kind = "compact", Prompt = "Compact saved context." });
            var engine = new AgentEngine(store, configuration, runtime, recording, events);
            var compactFailed = false;
            try { await engine.RunAsync(compactJob, default); } catch (ArgumentException ex) { compactFailed = ex.Message.Contains("Compaction profile", StringComparison.Ordinal); }
            var compactActivity = journal.List(compactSession.Id, 1).Entries.Single();
            Check(compactFailed && compactActivity.ToolName == "context.compact" && compactActivity.Title == "Context compaction" &&
                compactActivity.State == "failed" && store.Get<TaskSession>("session", compactSession.Id)!.Checkpoint == compactSession.Checkpoint,
                "real compaction entry records startup failure without invoking a model or replacing the saved checkpoint");
            Console.WriteLine($"Activity journal: {assertions} checks passed with temporary SQLite and deterministic tool providers; no models or live providers invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the test workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FixtureTools : IToolDispatcher
    {
        public Func<ToolCall, ToolExecutionContext, CancellationToken, Task<ToolResult>> Execute { get; set; } = (_, _, _) => Task.FromResult(new ToolResult(true, "fixture"));
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) => Execute(call, context, token);
    }
    private sealed class NoExecution : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("This fixture never starts the scheduler.");
    }
}
