using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static class LongFormEngineTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-longform-engine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new StateStore(root); var configuration = new Configuration(store); var events = new EventHub();
        await using var runtime = new ModelRuntime(store, configuration.Get, events);
        try
        {
            var profile = new AgentProfile
            {
                Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture", MaxSteps = 1,
                AllowedRoots = [root], Capabilities = ["filesystem.write"], AllowedTools = ["file.append"]
            };
            store.Put("agent", profile.Id, profile);

            var completedTools = new PendingAppendTools(true);
            var completed = CreatePendingFixture(store, profile, root, "completed");
            var completedEngine = new AgentEngine(store, configuration, runtime, completedTools, events);
            await completedEngine.RunAsync(completed.Job, default);
            var completedState = store.Get<LongFormState>("longform", completed.Session.Id)!;
            var completedSession = store.Get<TaskSession>("session", completed.Session.Id)!;
            Check(completedTools.Calls == 1 && completedTools.Content == completed.Content,
                "engine replays exactly one durable pending append with its original bounded payload");
            Check(completedState.PendingAppend is null && completedState.VerifiedWords == 500 && LongFormWorkflow.Complete(completedState),
                "a successful recovered receipt advances measured state and atomically clears the pending payload");
            Check(completed.Job.Outcome == "completed" && completed.Job.Result?.Contains("Verified artifact", StringComparison.Ordinal) == true,
                "completion on the last available action returns a service-verified completed outcome without another model step");
            Check(completedSession.Messages.Any(x => x.Role == "assistant" && x.Content.Contains("dispatched text omitted", StringComparison.Ordinal) &&
                    !x.Content.Contains(completed.Content, StringComparison.Ordinal)),
                "recovered append prose stays out of the active model-facing action history");

            var failedTools = new PendingAppendTools(false);
            var failed = CreatePendingFixture(store, profile, root, "failed");
            var failedEngine = new AgentEngine(store, configuration, runtime, failedTools, events);
            await failedEngine.RunAsync(failed.Job, default);
            var failedState = store.Get<LongFormState>("longform", failed.Session.Id)!;
            Check(failedTools.Calls == 1 && failed.Job.Outcome == "incomplete",
                "an unconfirmed recovery failure stops truthfully instead of entering an automatic replay loop");
            Check(failedState.PendingAppend is not null && failedState.VerifiedWords == 400,
                "failed reconciliation retains the exact pending record and prior measured state for diagnosis");
            Check(store.Get<AgentJob>("job", failed.Job.Id)!.Evidence.Any(x => !x.Success && x.Name == "file.append"),
                "failed recovery remains visible as durable execution evidence");

            var receipts = CreatePendingFixture(store, profile, root, "receipts",
                "Use file.inspect and file.append to continue the existing 2 page story.");
            var receiptTools = new PendingAppendTools(true);
            var receiptEngine = new AgentEngine(store, configuration, runtime, receiptTools, events);
            await receiptEngine.RunAsync(receipts.Job, default);
            Check(receipts.Job.Outcome == "incomplete" && receipts.Job.Result?.Contains("file.inspect", StringComparison.Ordinal) == true &&
                receiptTools.Calls == 1,
                "pending recovery still enforces explicit requested tool receipts in the final action-limit fallback");

            var guardPath = Path.Combine(root, "guard.txt");
            var guardSession = new TaskSession { Id = "guard-session", ProfileId = profile.Id, Revision = 3 };
            var guardState = new LongFormState
            {
                SessionId = guardSession.Id, SourceJobId = "guard-job", SourceRequestSequence = 1, Revision = 1,
                Path = guardPath, TargetWords = 500, TargetLabel = "500 words", VerifiedWords = 100,
                VerifiedBytes = 400, Sha256 = new string('d', 64),
                Sections = [new() { Title = "Draft", Purpose = "Continue.", TargetWords = 500 }]
            };
            var guardJob = new AgentJob
            {
                Id = "guard-job", SessionId = guardSession.Id, ProfileId = profile.Id, Status = "running",
                Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            store.Put("session", guardSession.Id, guardSession); store.Put("job", guardJob.Id, guardJob);
            store.Put("longform", guardSession.Id, guardState);
            var guardObjective = new LongFormObjective(500, "500 words", guardPath) { SourceRequestSequence = 1 };
            var guardArguments = CerebralJson.Element(new
            {
                path = guardPath, content = "Next bounded passage.", expectedSha256 = guardState.Sha256, expectedBytes = guardState.VerifiedBytes
            });
            const string guardedAction = "{\"action\":\"tool\",\"name\":\"file.append\",\"arguments\":{}}";
            var staleReservation = AgentExecutionGuard.Reserve(store, guardSession.Id, 2, guardedAction,
                "guard-job", 1, "file.append", guardArguments, guardObjective);
            Check(staleReservation is null && store.Get<LongFormState>("longform", guardSession.Id)!.PendingAppend is null &&
                store.Get<TaskSession>("session", guardSession.Id)!.Messages.Count == 0,
                "a changed session revision prevents both pending replay state and an official tool start");
            var currentReservation = AgentExecutionGuard.Reserve(store, guardSession.Id, 3, guardedAction,
                "guard-job", 1, "file.append", guardArguments, guardObjective);
            Check(currentReservation is { PreparedLongFormAppend: true } &&
                store.Get<LongFormState>("longform", guardSession.Id)!.PendingAppend is not null &&
                store.Get<TaskSession>("session", guardSession.Id)!.Messages.Single().Sequence == currentReservation.MessageSequence,
                "one short reservation atomically commits pending append replay state and the official tool action");

            using var guardScheduler = new JobScheduler(store, configuration, completedEngine, runtime, events,
                NullLogger<JobScheduler>.Instance);
            var beforeSession = new TaskSession { Id = "input-before-session", ProfileId = profile.Id, Revision = 1 };
            var beforeJob = new AgentJob
            {
                Id = "input-before-job", SessionId = beforeSession.Id, ProfileId = profile.Id, Status = "running",
                Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            store.Put("session", beforeSession.Id, beforeSession); store.Put("job", beforeJob.Id, beforeJob);
            using var gateEntered = new ManualResetEventSlim(); using var allowSteer = new ManualResetEventSlim();
            var steering = Task.Run(() => store.LinearizedSession(beforeSession.Id, () =>
            {
                gateEntered.Set(); allowSteer.Wait(); return guardScheduler.Steer(beforeJob.Id, "Input committed before reservation.", "before-reservation");
            }));
            Check(gateEntered.Wait(TimeSpan.FromSeconds(5)), "the deterministic steering race acquired the conversation boundary first");
            var blockedReservation = Task.Run(() => AgentExecutionGuard.Reserve(store, beforeSession.Id, beforeSession.Revision,
                guardedAction, beforeJob.Id, 1, "file.read", CerebralJson.Element(new { })));
            allowSteer.Set(); await steering; var invalidated = await blockedReservation;
            var beforeAfter = store.Get<TaskSession>("session", beforeSession.Id)!;
            Check(invalidated is null && beforeAfter.Messages.Count == 1 && beforeAfter.Messages[0].Content == "Input committed before reservation.",
                "steering committed before the reservation invalidates the model action without recording a start");

            var afterSession = new TaskSession { Id = "input-after-session", ProfileId = profile.Id, Revision = 1 };
            var afterJob = new AgentJob
            {
                Id = "input-after-job", SessionId = afterSession.Id, ProfileId = profile.Id, Status = "running",
                Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            var afterQuestion = new UserQuestion
            {
                Id = "input-after-question", JobId = afterJob.Id, SessionId = afterSession.Id,
                Prompt = "Supply the late decision.", Required = true
            };
            store.Put("session", afterSession.Id, afterSession); store.Put("job", afterJob.Id, afterJob);
            store.Put("question", afterQuestion.Id, afterQuestion);
            var officiallyStarted = AgentExecutionGuard.Reserve(store, afterSession.Id, afterSession.Revision,
                guardedAction, afterJob.Id, 1, "file.read", CerebralJson.Element(new { }))!;
            guardScheduler.Answer(afterQuestion.Id, "Late answer after start.");
            var afterAfter = store.Get<TaskSession>("session", afterSession.Id)!;
            var answerMessage = afterAfter.Messages.Single(x => x.SourceQuestionId == afterQuestion.Id);
            Check(afterAfter.Messages.Any(x => x.Sequence == officiallyStarted.MessageSequence && x.Content == guardedAction) &&
                answerMessage.Sequence > officiallyStarted.MessageSequence,
                "an answer committed after the reservation observes the durable official start and follows it in context");

            var cancelBeforePath = Path.Combine(root, "cancel-before.txt");
            var cancelBeforeSession = new TaskSession { Id = "cancel-before-session", ProfileId = profile.Id, Revision = 1 };
            var cancelBeforeJob = new AgentJob
            {
                Id = "cancel-before-job", SessionId = cancelBeforeSession.Id, ProfileId = profile.Id, Status = "running",
                Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            var cancelBeforeState = new LongFormState
            {
                SessionId = cancelBeforeSession.Id, SourceJobId = cancelBeforeJob.Id, SourceRequestSequence = 1,
                Path = cancelBeforePath, TargetWords = 500, TargetLabel = "500 words", VerifiedWords = 100,
                VerifiedBytes = 400, Sha256 = new string('c', 64),
                Sections = [new() { Title = "Draft", Purpose = "Continue.", TargetWords = 500 }]
            };
            var cancelBeforeObjective = new LongFormObjective(500, "500 words", cancelBeforePath) { SourceRequestSequence = 1 };
            var cancelBeforeArguments = CerebralJson.Element(new
            {
                path = cancelBeforePath, content = "Cancelled passage.",
                expectedSha256 = cancelBeforeState.Sha256, expectedBytes = cancelBeforeState.VerifiedBytes
            });
            store.Put("session", cancelBeforeSession.Id, cancelBeforeSession); store.Put("job", cancelBeforeJob.Id, cancelBeforeJob);
            store.Put("longform", cancelBeforeSession.Id, cancelBeforeState);
            using var cancelGateEntered = new ManualResetEventSlim(); using var allowCancelCommit = new ManualResetEventSlim();
            var cancellation = Task.Run(() => store.Locked(() =>
            {
                cancelGateEntered.Set(); allowCancelCommit.Wait(); return guardScheduler.Cancel(cancelBeforeJob.Id);
            }));
            Check(cancelGateEntered.Wait(TimeSpan.FromSeconds(5)), "the deterministic tool cancellation race acquired the durable state boundary first");
            var cancelledReservation = Task.Run(() =>
            {
                try
                {
                    AgentExecutionGuard.Reserve(store, cancelBeforeSession.Id, cancelBeforeSession.Revision, guardedAction,
                        cancelBeforeJob.Id, 1, "file.append", cancelBeforeArguments, cancelBeforeObjective);
                    return false;
                }
                catch (OperationCanceledException) { return true; }
            });
            allowCancelCommit.Set(); await cancellation;
            Check(await cancelledReservation && store.Get<LongFormState>("longform", cancelBeforeSession.Id)!.PendingAppend is null &&
                  store.Get<TaskSession>("session", cancelBeforeSession.Id)!.Messages.Count == 0 &&
                  store.Get<AgentJob>("job", cancelBeforeJob.Id) is { Status: "cancelled", CancelRequested: true },
                "cancellation committed first rejects pending append preparation and the official tool start atomically");

            var actionBeforePath = Path.Combine(root, "action-before-cancel.txt");
            var actionBeforeSession = new TaskSession { Id = "action-before-cancel-session", ProfileId = profile.Id, Revision = 1 };
            var actionBeforeJob = new AgentJob
            {
                Id = "action-before-cancel-job", SessionId = actionBeforeSession.Id, ProfileId = profile.Id, Status = "running",
                Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            var actionBeforeState = new LongFormState
            {
                SessionId = actionBeforeSession.Id, SourceJobId = actionBeforeJob.Id, SourceRequestSequence = 1,
                Path = actionBeforePath, TargetWords = 500, TargetLabel = "500 words", VerifiedWords = 100,
                VerifiedBytes = 400, Sha256 = new string('a', 64),
                Sections = [new() { Title = "Draft", Purpose = "Continue.", TargetWords = 500 }]
            };
            var actionBeforeObjective = new LongFormObjective(500, "500 words", actionBeforePath) { SourceRequestSequence = 1 };
            var actionBeforeArguments = CerebralJson.Element(new
            {
                path = actionBeforePath, content = "Official passage.",
                expectedSha256 = actionBeforeState.Sha256, expectedBytes = actionBeforeState.VerifiedBytes
            });
            store.Put("session", actionBeforeSession.Id, actionBeforeSession); store.Put("job", actionBeforeJob.Id, actionBeforeJob);
            store.Put("longform", actionBeforeSession.Id, actionBeforeState);
            var actionBeforeReservation = AgentExecutionGuard.Reserve(store, actionBeforeSession.Id, actionBeforeSession.Revision,
                guardedAction, actionBeforeJob.Id, 1, "file.append", actionBeforeArguments, actionBeforeObjective);
            guardScheduler.Cancel(actionBeforeJob.Id);
            Check(actionBeforeReservation is { PreparedLongFormAppend: true } &&
                  store.Get<LongFormState>("longform", actionBeforeSession.Id)!.PendingAppend is not null &&
                  store.Get<TaskSession>("session", actionBeforeSession.Id)!.Messages.Single().Content == guardedAction &&
                  store.Get<AgentJob>("job", actionBeforeJob.Id) is { Status: "cancelled", CancelRequested: true },
                "an official tool action committed first remains durable while later cancellation stops the assignment");

            var freshObjective = new LongFormObjective(500, "500 words", null) { RequiresNewArtifact = true };
            var existingObjective = new LongFormObjective(500, "500 words", guardPath);
            var unverified = new LongFormState { Path = Path.Combine(root, "not-created.txt"), TargetWords = 500, StartsNewArtifact = true };
            var existingUnverifiedPath = Path.Combine(root, "existing.txt"); File.WriteAllText(existingUnverifiedPath, "existing");
            var existingUnverified = new LongFormState { Path = existingUnverifiedPath, TargetWords = 500 };
            var verifiedWithoutTail = new LongFormState { Path = existingUnverifiedPath, TargetWords = 500, VerifiedWords = 100, Sha256 = new string('e', 64) };
            Check(AgentEngine.RequiredLongFormTools(freshObjective, null).ToHashSet(StringComparer.Ordinal).SetEquals(["file.write", "file.append"]) &&
                AgentEngine.RequiredLongFormTools(existingObjective, null).ToHashSet(StringComparer.Ordinal).SetEquals(["file.inspect", "file.append"]) &&
                AgentEngine.RequiredLongFormTools(freshObjective, unverified).ToHashSet(StringComparer.Ordinal).SetEquals(["file.write", "file.append"]) &&
                AgentEngine.RequiredLongFormTools(existingObjective, existingUnverified).ToHashSet(StringComparer.Ordinal).SetEquals(["file.inspect", "file.append"]) &&
                AgentEngine.RequiredLongFormTools(existingObjective, verifiedWithoutTail).ToHashSet(StringComparer.Ordinal).SetEquals(["file.read", "file.append"]),
                "fresh artifacts receive write and append only while existing artifacts add inspect or bounded read as state requires");
            var emptySchema = CerebralJson.Element(new { type = "object" });
            Check(!AgentEngine.SupportsGenericLongForm(
                    [new("book.chapter.append", "Plugin-owned chapter append.", emptySchema, "book.chapter.write")], null) &&
                  AgentEngine.SupportsGenericLongForm(
                    [new("file.write", "Write.", emptySchema, "filesystem.write"), new("file.append", "Append.", emptySchema, "filesystem.write")], null) &&
                  !AgentEngine.SupportsGenericLongForm(
                    [new("file.write", "Write.", emptySchema, "filesystem.write"), new("file.append", "Append.", emptySchema, "filesystem.write"),
                     new("book.chapter.rewrite", "Rewrite.", emptySchema, "book.chapter.write")], null, ["book.chapter.rewrite"]) &&
                  AgentEngine.SupportsGenericLongForm(
                    [new("file.write", "Write.", emptySchema, "filesystem.write"), new("file.append", "Append.", emptySchema, "filesystem.write"),
                     new("book.chapter.rewrite", "Rewrite.", emptySchema, "book.chapter.write")], null, ["book.chapter.rewrite", "file.write"]) &&
                  AgentEngine.SupportsGenericLongForm(
                    [new("book.chapter.append", "Plugin-owned chapter append.", emptySchema, "book.chapter.write")], verifiedWithoutTail),
                "generic long-form activates only for its file contract, yields to an exact extension workflow unless file mutation is explicit, and retains recovery ownership of existing state");

            Console.WriteLine($"Long-form engine recovery: {assertions} checks passed (real SQLite state and deterministic tools; no model inference)." );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(full).StartsWith("cerebral-longform-engine-tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }

    private static PendingFixture CreatePendingFixture(StateStore store, AgentProfile profile, string root, string suffix, string? prompt = null)
    {
        var path = Path.Combine(root, suffix + ".txt");
        var content = "\nRecovered final passage for " + suffix + ".";
        prompt ??= "Continue the existing 2 page story.";
        var session = new TaskSession
        {
            Id = "session-" + suffix, ProfileId = profile.Id, Revision = 1,
            Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = prompt }]
        };
        var oldHash = new string('a', 64);
        var state = new LongFormState
        {
            SessionId = session.Id, SourceJobId = "prior-" + suffix, SourceRequestSequence = 1, Revision = 1,
            Path = path, TargetWords = 500, TargetLabel = "approximately 2 pages (500 words at 250 words per page)",
            VerifiedWords = 400, VerifiedLines = 20, VerifiedBytes = 2000, Sha256 = oldHash, CurrentSectionIndex = 2,
            Sections =
            [
                new() { Title = "Opening", Purpose = "Begin.", TargetWords = 167, CompletedAtWords = 400 },
                new() { Title = "Middle", Purpose = "Develop.", TargetWords = 167, CompletedAtWords = 400 },
                new() { Title = "Ending", Purpose = "Resolve.", TargetWords = 166 }
            ]
        };
        var job = new AgentJob
        {
            Id = "job-" + suffix, SessionId = session.Id, ProfileId = profile.Id, Prompt = prompt,
            StartSequence = 1, Status = "running", Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
        };
        var arguments = CerebralJson.Element(new { path, content, expectedSha256 = oldHash, expectedBytes = 2000 });
        LongFormWorkflow.PreparePendingAppend(state, job.Id, 1, "file.append", arguments);
        store.Put("session", session.Id, session); store.Put("longform", session.Id, state); store.Put("job", job.Id, job);
        return new(session, job, content);
    }

    private sealed record PendingFixture(TaskSession Session, AgentJob Job, string Content);

    private sealed class PendingAppendTools(bool success) : IToolDispatcher
    {
        public int Calls { get; private set; }
        public string? Content { get; private set; }
        public string NewHash { get; } = new string(success ? 'b' : 'c', 64);

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("file.append", "Append text.", CerebralJson.Element(new { type = "object" }), "filesystem.write"),
                new("file.inspect", "Inspect text.", CerebralJson.Element(new { type = "object" }), "filesystem.read")]);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Calls++;
            Content = call.Arguments.GetProperty("content").GetString();
            if (!success) return Task.FromResult(new ToolResult(false, "Connection interrupted; the external effect is uncertain."));
            return Task.FromResult(new ToolResult(true, JsonSerializer.Serialize(new
            {
                path = call.Arguments.GetProperty("path").GetString(), alreadyApplied = true,
                appendedCharacters = Content!.Length, bytes = 2200, lines = 25, words = 500, sha256 = NewHash
            })));
        }
    }
}
