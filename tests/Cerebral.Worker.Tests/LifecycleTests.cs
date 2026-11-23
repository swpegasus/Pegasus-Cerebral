using System.Diagnostics;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class LifecycleTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var assertions = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); assertions++; Console.WriteLine("PASS: " + label); }
        bool Rejects(Action call) { try { call(); return false; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException) { return true; } }
        var store = new StateStore(Path.Combine(root, "data")); var config = new Configuration(store); var events = new EventHub();
        await using var runtime = new ModelRuntime(store, config.Get, events);
        await using var tools = new ToolDispatcher(config.Get);
        var executor = new Executor();
        using var scheduler = new JobScheduler(store, config, executor, runtime, events, NullLogger<JobScheduler>.Instance);
        var host = Environment.ProcessPath!;
        var settings = config.Get(); settings.ModelDirectory = Path.Combine(root, "models"); settings.MaxConcurrentJobs = 1;
        settings.Executables = [new() { Id = "testhost", Path = host, PrefixArguments = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [typeof(LifecycleTests).Assembly.Location] : [] }];
        config.Save(settings);
        var fixtureModelPath = Path.Combine(root, "fixture.gguf"); await File.WriteAllBytesAsync(fixtureModelPath, "GGUF"u8.ToArray());
        store.Put("model", "fixture", new ModelRecord
        {
            Id = "fixture", Name = "Lifecycle fixture model", Path = fixtureModelPath,
            DeclaredType = "model", MetadataSource = "fixture"
        });
        var lead = new AgentProfile { Id = "lead", Name = "Fixture lead", ModelId = "fixture", Role = "lead", MaxSteps = 20, MaxSeconds = 60,
            AllowedRoots = [root, AppContext.BaseDirectory], AllowedTools = ["file.read", "file.list", "process.run"], Capabilities = ["filesystem.read", "process.execute"] };
        var helper = CerebralJson.Read<AgentProfile>(CerebralJson.Element(lead)); helper.Id = "helper"; helper.Role = "helper";
        store.Put("agent", lead.Id, lead); store.Put("agent", helper.Id, helper);
        try
        {
            var parent = scheduler.Submit(new() { ProfileId = "lead", Prompt = "fixture parent" });
            var child = scheduler.Submit(new() { ProfileId = "helper", ParentJobId = parent.Id, Prompt = "fixture child" });
            parent.Status = "completed"; store.Put("job", parent.Id, parent);
            scheduler.Cancel(parent.Id);
            Check(scheduler.Get(child.Id).Status == "cancelled", "cancel finished parent still cancels queued descendants");
            scheduler.StopConversation(parent.SessionId); scheduler.StopConversation(parent.SessionId);
            Check(!store.List<AgentJob>("job").Any(JobScheduler.IsActive), "conversation Stop is idempotent and includes descendants");
            Check(parent.StartSequence == 1, "assignment captures the initiating message sequence");

            var questionParent = scheduler.Submit(new() { ProfileId = "lead", Prompt = "delegate a design choice" });
            var questionHelper = scheduler.Submit(new() { ProfileId = "helper", ParentJobId = questionParent.Id, Prompt = "resolve the palette" });
            questionParent.Status = "waiting-helper"; store.Put("job", questionParent.Id, questionParent);
            questionHelper.Status = "waiting-human"; questionHelper.WaitingReason = "Which color?"; store.Put("job", questionHelper.Id, questionHelper);
            var delegatedQuestion = new UserQuestion { Id = "delegated-answer", JobId = questionHelper.Id, SessionId = questionHelper.SessionId,
                Prompt = "Which color should the cover use?", Required = true };
            store.Put("question", delegatedQuestion.Id, delegatedQuestion);
            scheduler.Answer(delegatedQuestion.Id, "Use blue"); scheduler.Answer(delegatedQuestion.Id, "Use blue");
            var questionLeadSession = store.Get<TaskSession>("session", questionParent.SessionId)!;
            var projectedAnswers = questionLeadSession.Messages.Where(x => x.Origin == "question-answer" && x.SourceQuestionId == delegatedQuestion.Id).ToList();
            Check(projectedAnswers.Count == 1 && projectedAnswers[0].Content.Contains("Which color should the cover use?", StringComparison.Ordinal) &&
                projectedAnswers[0].Content.Contains("Use blue", StringComparison.Ordinal) &&
                questionLeadSession.UpdatedAt is { } updatedAt && updatedAt >= projectedAnswers[0].At &&
                store.Get<TaskSession>("session", questionHelper.SessionId)!.Messages.Count(x => x.Content.Contains("Use blue", StringComparison.Ordinal)) == 1,
                "delegated question answer projects once, refreshes lead recency, and remains idempotent");
            questionLeadSession.Messages.RemoveAll(x => x.SourceQuestionId == delegatedQuestion.Id); store.Put("session", questionLeadSession.Id, questionLeadSession);
            scheduler.Answer(delegatedQuestion.Id, "Use blue"); scheduler.Answer(delegatedQuestion.Id, "Use blue");
            Check(store.Get<TaskSession>("session", questionParent.SessionId)!.Messages.Count(x => x.SourceQuestionId == delegatedQuestion.Id) == 1,
                "an idempotent answer retry backfills a missing lead projection without duplicating it");
            scheduler.Cancel(questionParent.Id);

            var workspace = new WorkspaceManagement(store, events, scheduler);
            var navigation = workspace.SaveNavigation(new() { Nodes = [
                new() { Id = "folder", Kind = "folder", Title = "Writing" },
                new() { Id = "project", Kind = "project", ParentId = "folder", Title = "Book", Pinned = true, Path = root },
                new() { Id = "chat", Kind = "conversation", ParentId = "project", Title = "Scene", TargetId = parent.SessionId }] });
            Check(navigation.Revision == 1 && new WorkspaceManagement(new StateStore(store.DirectoryPath), events, scheduler).GetNavigation().Nodes.Count == 3, "sidebar hierarchy and pins persist in existing SQLite");
            Check(Rejects(() => workspace.SaveNavigation(new())), "stale sidebar save preserves current hierarchy");
            var cycle = CerebralJson.Read<NavigationState>(CerebralJson.Element(navigation)); cycle.Nodes[0].ParentId = "project";
            Check(Rejects(() => workspace.SaveNavigation(cycle)), "sidebar cycles rejected");
            Check(workspace.Rename(parent.SessionId, "Renamed").Title == "Renamed" && workspace.GetNavigation().Nodes.Single(x => x.Id == "chat").Title == "Renamed", "rename updates conversation and sidebar reference");

            var pending = scheduler.Submit(new() { ProfileId = "lead", Prompt = "clarification fixture" });
            // Saving the legacy-style lead above lets Configuration.Get migrate its implicit default model.
            // Re-read after that service-owned revision instead of overwriting it with the fixture's stale copy.
            settings = config.Get();
            settings.QuestionTimeoutSeconds = 1; settings.QuestionTimeoutAction = "assume"; config.Save(settings);
            store.Put("question", "required", new UserQuestion { Id = "required", JobId = pending.Id, SessionId = pending.SessionId, Required = true, CanAssume = true, Assumption = "do it", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            store.Put("question", "optional", new UserQuestion { Id = "optional", JobId = pending.Id, SessionId = pending.SessionId, CanAssume = true, Assumption = "use blue", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            scheduler.ProcessQuestionTimeouts();
            Check(store.Get<UserQuestion>("question", "required")!.Answer is null, "timeout never answers required questions or grants approval");
            Check(store.Get<UserQuestion>("question", "optional")!.Assumed && store.Get<TaskSession>("session", pending.SessionId)!.Messages[^1].Origin == "service", "optional timeout records explicit assumption with service provenance");
            scheduler.Cancel(pending.Id);

            if (OperatingSystem.IsWindows())
            {
                parent = scheduler.Submit(new() { ProfileId = "lead", Prompt = "application feedback fixture" });
                var app = Path.Combine(root, "fixture.exe"); await File.WriteAllTextAsync(app, "admission fixture; not executable");
                var requests = new InterfaceRequests(store, events); var request = requests.Create(parent, lead, "application", app); requests.Claim(request.Id, "window");
                var start = DateTimeOffset.UtcNow;
                Check(Rejects(() => requests.Observe(new(request.Id, "wrong", 123, start, "running"))), "application feedback requires exact interface claim");
                requests.Observe(new(request.Id, "window", 123, start, "running"));
                requests.Complete(request.Id, "window", true, message: "Presentation started.");
                requests.Observe(new(request.Id, "window", 123, start, "failed", 7, "Fixture startup failed"));
                var revision = store.Get<TaskSession>("session", parent.SessionId)!.Revision;
                requests.Observe(new(request.Id, "window", 123, start, "failed", 7, "Fixture startup failed"));
                Check(store.Get<TaskSession>("session", parent.SessionId)!.Revision == revision, "duplicate application exit observation is idempotent");
                Check(store.Get<AgentJob>("job", parent.Id)!.Evidence.Any(x => !x.Success && x.ExitCode == 7) && workspace.Failures(parent.SessionId).Any(x => x.Message.Contains("Fixture startup failed")), "application crash reaches conversation and failure list");
                Check(Rejects(() => requests.Observe(new(request.Id, "window", 123, start, "running"))), "terminal crash cannot be overwritten by delayed startup");
                var failure = workspace.Failures(parent.SessionId).First(); workspace.ResolveFailure(failure.Id, "Fixture reviewed");
                Check(workspace.Failures(parent.SessionId).First().Status == "resolved", "failure resolution retains original error with explicit note");
                var duplicateChild = scheduler.Submit(new() { ProfileId = "helper", ParentJobId = parent.Id, Prompt = "merged failure fixture" });
                var sharedFailure = new ExecutionEvidence { Kind = "tool", Name = "process.run", Success = false, Summary = "Helper build failed",
                    SourceSessionId = duplicateChild.SessionId, MessageSequence = 1 };
                var unrelatedEvidence = new ExecutionEvidence { Kind = "tool", Name = "file.read", Success = false, Summary = "Separate failure" };
                duplicateChild.Evidence.Add(sharedFailure); duplicateChild.Evidence.Add(unrelatedEvidence); store.Put("job", duplicateChild.Id, duplicateChild);
                var mergedParent = scheduler.Get(parent.Id);
                mergedParent.Evidence.Add(CerebralJson.Read<ExecutionEvidence>(CerebralJson.Element(sharedFailure))); store.Put("job", mergedParent.Id, mergedParent);
                var aggregated = workspace.Failures(parent.SessionId).Where(x => x.Id == sharedFailure.Id).ToList();
                Check(aggregated.Count == 1 && aggregated[0].JobId == duplicateChild.Id && aggregated[0].SessionId == duplicateChild.SessionId,
                    "merged helper failure appears once at its source job and session");
                workspace.ResolveFailure(sharedFailure.Id, "Merged fixture reviewed");
                Check(new[] { mergedParent.Id, duplicateChild.Id }.All(id => scheduler.Get(id).Evidence.Single(x => x.Id == sharedFailure.Id).Resolution == "User reviewed: Merged fixture reviewed") &&
                    workspace.Failures(parent.SessionId).Single(x => x.Id == sharedFailure.Id).Status == "resolved",
                    "resolving merged failure updates both retained copies and one displayed failure");
                Check(scheduler.Get(duplicateChild.Id).Evidence.Single(x => x.Id == unrelatedEvidence.Id).Resolution is null &&
                    scheduler.Get(duplicateChild.Id).Status == duplicateChild.Status && scheduler.Get(mergedParent.Id).Status == mergedParent.Status,
                    "merged failure resolution preserves unrelated evidence and job status");
                var agentObservation = await new CommandDispatcher(store, config, runtime, scheduler, tools, events).ExecuteAsync(new("interface.observe", CerebralJson.Element(new ApplicationObservation(request.Id, "window", 123, start, "failed", 7)), lead.Id));
                Check(!agentObservation.Success, "ordinary agents cannot manufacture process evidence");
            }

            scheduler.StopConversation(parent.SessionId);
            var pidPath = Path.Combine(root, "child.pid");
            executor.Action = async (job, token) =>
            {
                if (job.ProfileId == "lead")
                {
                    executor.SubmitHelper!(new() { ProfileId = "helper", ParentJobId = job.Id, Prompt = "real cancellable process" });
                    job.Status = "waiting-helper"; return;
                }
                await tools.ExecuteAsync(new("process.run", CerebralJson.Element(new { executableId = "testhost", workingDirectory = root,
                    arguments = new[] { "--lifecycle-child", pidPath }, timeoutSeconds = 45 })), new(job.Profile, job.SessionId, job.Id), token);
            };
            var running = scheduler.Submit(new() { ProfileId = "lead", Prompt = "cascade live child" });
            await scheduler.StartAsync(CancellationToken.None);
            await Until(() => File.Exists(pidPath), "real child starts");
            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            scheduler.StopConversation(running.SessionId);
            await Until(() => !scheduler.ConversationJobIds(running.SessionId).Select(scheduler.Get).Any(JobScheduler.IsActive), "descendant cancellation completes");
            await Until(() => !Alive(childPid), "owned command child exits");
            Check(scheduler.Get(running.Id).Status == "cancelled" && !Alive(childPid), "real process.run child terminates when its lead conversation is stopped");
            await scheduler.StopAsync(CancellationToken.None);
            Console.WriteLine($"Lifecycle: {assertions} checks passed (SQLite/command tests with deterministic executor; one real child process cancellation).");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(root, true);
        }
    }
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Until(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Timeout: " + label); await Task.Delay(30); }
    }
    private sealed class Executor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Func<AgentJob, CancellationToken, Task> Action { get; set; } = (_, _) => Task.CompletedTask;
        public Task RunAsync(AgentJob job, CancellationToken token) => Action(job, token);
    }
}
