using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>SQLite, real host scheduler, deterministic definitions. No model, network or Laya inference.</summary>
internal static class CoordinationWorkflowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-coordination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message)
        {
            try { action(); } catch (Exception error) when (error is WorkflowConflictException or ArgumentException or KeyNotFoundException)
            { checks++; return; }
            throw new InvalidOperationException(message);
        }
        try
        {
            var state = new StateStore(root); var configuration = new Configuration(state); var events = new EventHub();
            var settings = configuration.Get(); settings.MaxConcurrentJobs = 1; settings.QueueCapacity = 200; configuration.Save(settings);
            state.Put("agent", "lead", new AgentProfile { Id = "lead", Name = "Lead", Role = "lead" });
            state.Put("agent", "helper", new AgentProfile { Id = "helper", Name = "Helper", Role = "helper" });
            var workflows = new WorkerWorkflows(state, configuration, events, [new Module()], [new("fixture.coordination", "1.0.0", true, [])]);
            var store = workflows.Store;
            WorkflowRun Parent(string id) => workflows.Submit(new(id, id + "-conversation", "lead", "fixture.coordination:parent",
                "1.0.0", "Coordinate", null, WorkflowContract.EmptyObject), null);
            WorkflowSubmission ChildInput(WorkflowRun parent, string suffix) => parent.Submission with
            {
                RequestId = parent.Id + "-" + suffix, ConversationId = parent.Id + "-" + suffix + "-context",
                ProfileId = "helper", DefinitionId = "fixture.coordination:child", ParentRunId = parent.Id,
                Caller = new(EventActorKind.Service, "cerebral.coordination"), AssignmentText = "Child-only task context",
                UserText = "Child task"
            };
            WorkflowRun Child(WorkflowRun parent, string suffix) => store.Submit(ChildInput(parent, suffix));
            WorkflowRun Wait(WorkflowRun parent, params string[] ids)
            {
                var claimed = store.TryClaim(parent.Id)!;
                return store.ApplyStep(claimed.Id, claimed.Revision, new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns(ids)));
            }
            void Complete(WorkflowRun child, bool success = true)
            {
                var claimed = store.TryClaim(child.Id)!;
                store.ApplyStep(claimed.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(CerebralJson.Element(new { text = "Child result" }), success, success ? null : "Fixture failure")));
            }

            var parent = Parent("join"); var one = Child(parent, "one"); var two = Child(parent, "two");
            var retry = store.Submit(ChildInput(parent, "one"));
            Check(retry.Id == one.Id && retry.Revision == one.Revision, "Child lost-response admission is idempotent.");
            var waiting = Wait(parent, one.Id, two.Id);
            Check(waiting.Status == WorkflowStatus.Waiting, "Parent commits a durable wait.");
            Complete(one);
            Check(store.ResumeCompletedDependencies().Count == 0, "Incomplete child keeps barrier waiting.");
            Reject(() => store.Resume(parent.Id, waiting.Revision), "Owner resume cannot bypass pending children.");
            Complete(two, false);
            var collected = store.ResumeCompletedDependencies().Single();
            Check(collected.Id == parent.Id && collected.Signal.Kind == WorkflowSignalKind.DependencyResults,
                "Terminal outcomes queue the parent with attributed evidence.");
            var outcomes = collected.Signal.Data.GetProperty("children").EnumerateArray().ToArray();
            Check(outcomes.Any(item => item.GetProperty("status").GetString() == "Failed"), "Child failure remains visible.");
            Check(outcomes.All(item => item.GetProperty("profileId").GetString() == "helper" && item.GetProperty("revision").GetInt64() > 1), "Results retain profile and revision.");
            Check(store.ResumeCompletedDependencies().Count == 0, "Collection occurs once.");
            var questionParent = Parent("local-question");
            var questionChild = Child(questionParent, "asking");
            var finishedSibling = Child(questionParent, "finished");
            var questionClaim = store.TryClaim(questionChild.Id)!;
            var childQuestion = store.ApplyStep(questionClaim.Id, questionClaim.Revision,
                new(WorkflowContract.EmptyObject, new AskWorkflowQuestions(
                    [new("plan", "Plan details"), new("scope", "Task scope")])));
            Complete(finishedSibling);
            var questionWait = Wait(questionParent, questionChild.Id, finishedSibling.Id);
            var proxy = store.ResumeCompletedDependencies().Single(run => run.Id == questionParent.Id);
            Check(proxy.Status == WorkflowStatus.Waiting && proxy.Signal.Kind != WorkflowSignalKind.Answers &&
                proxy.WaitingForRunIds.SequenceEqual(questionWait.WaitingForRunIds) &&
                proxy.Questions?.Fields.SequenceEqual(childQuestion.Questions!.Fields) == true &&
                proxy.LocalChildQuestionRelay is { ChildRunId: var target } && target == questionChild.Id,
                "A local child question appears on its exact parent without releasing the barrier.");
            Check(store.ResumeCompletedDependencies().Count == 0, "An existing parent question is not projected twice.");
            var firstAnswer = new WorkflowAnswerRequest("local-answer-partial", proxy.Id,
                proxy.Questions!.Id, proxy.Questions.Revision,
                new Dictionary<string, string> { ["plan"] = "Plan A" });
            var partialParent = store.Answer(firstAnswer);
            Check(partialParent.Status == WorkflowStatus.Waiting &&
                partialParent.Questions?.Answers["plan"] == "Plan A" &&
                partialParent.LocalChildQuestionRelay?.ParentQuestionRevision == partialParent.Questions.Revision &&
                store.Get(questionChild.Id).Revision == childQuestion.Revision,
                "A partial Main answer persists without touching the child.");
            var finalAnswer = new WorkflowAnswerRequest("local-answer-complete", partialParent.Id,
                partialParent.Questions!.Id, partialParent.Questions.Revision,
                new Dictionary<string, string> { ["scope"] = "Two files" });
            var answeredParent = store.Answer(finalAnswer);
            var answeredChild = store.Get(questionChild.Id);
            Check(answeredParent.Status == WorkflowStatus.Waiting && answeredParent.Questions is null &&
                answeredParent.LocalChildQuestionRelay is null &&
                answeredParent.WaitingForRunIds.SequenceEqual(questionWait.WaitingForRunIds) &&
                answeredChild.Status == WorkflowStatus.Queued &&
                answeredChild.Signal.Kind == WorkflowSignalKind.Answers &&
                answeredChild.Questions?.Answers["plan"] == "Plan A" &&
                answeredChild.Questions.Answers["scope"] == "Two files",
                "The exact child receives complete owner answers while its parent keeps waiting.");
            Check(store.Answer(finalAnswer).Revision == answeredParent.Revision,
                "A lost-response answer retry returns its saved parent receipt.");
            Reject(() => store.Answer(finalAnswer with
                { Answers = new Dictionary<string, string> { ["scope"] = "Changed" } }),
                "The same request ID cannot adopt different owner answers.");
            Complete(answeredChild);
            var afterAnswer = store.ResumeCompletedDependencies().Single(run => run.Id == questionParent.Id);
            Check(afterAnswer.Status == WorkflowStatus.Queued &&
                afterAnswer.Signal.Kind == WorkflowSignalKind.DependencyResults &&
                afterAnswer.Signal.Data.GetProperty("children").GetArrayLength() == 2,
                "The original barrier collects both terminal child outcomes after the relay.");

            var priorAnswerParent = Parent("prior-child-answer");
            var namedChild = store.Submit(ChildInput(priorAnswerParent, "asking") with
                { Caller = new(EventActorKind.Service, "cerebral.coordination", "Coordinator") });
            var namedClaim = store.TryClaim(namedChild.Id)!;
            var namedQuestion = store.ApplyStep(namedClaim.Id, namedClaim.Revision,
                new(WorkflowContract.EmptyObject, new AskWorkflowQuestions(
                    [new("known", "Already answered"), new("remaining", "Still needed")])));
            var childPartial = store.Answer(new("child-prior-partial", namedChild.Id,
                namedQuestion.Questions!.Id, namedQuestion.Questions.Revision,
                new Dictionary<string, string> { ["known"] = "Original value" }));
            Wait(priorAnswerParent, namedChild.Id);
            var namedProxy = store.ResumeCompletedDependencies().Single(run => run.Id == priorAnswerParent.Id);
            Check(namedProxy.Questions?.Answers["known"] == "Original value" &&
                namedProxy.LocalChildQuestionRelay?.ChildCaller.DisplayName == "Coordinator" &&
                namedProxy.LocalChildQuestionRelay.ChildRunRevision == childPartial.Revision,
                "Main shows the exact existing child answer and retains a valid named host caller.");
            var namedAnswered = store.Answer(new("named-child-owner-answer", priorAnswerParent.Id,
                namedProxy.Questions!.Id, namedProxy.Questions.Revision,
                new Dictionary<string, string> { ["remaining"] = "New owner value" }));
            var namedResult = store.Get(namedChild.Id);
            Check(namedAnswered.Status == WorkflowStatus.Waiting && namedAnswered.Questions is null &&
                namedResult.Status == WorkflowStatus.Queued &&
                namedResult.Questions?.Answers["known"] == "Original value" &&
                namedResult.Questions.Answers["remaining"] == "New owner value",
                "The owner supplies only the missing field and the exact child receives both values.");
            Complete(namedResult);
            Check(store.ResumeCompletedDependencies().Single().Id == priorAnswerParent.Id,
                "A previously partial child completes the normal parent barrier after its answer.");

            var staleParent = Parent("stale-question");
            var staleChild = Child(staleParent, "asking");
            var staleClaim = store.TryClaim(staleChild.Id)!;
            staleChild = store.ApplyStep(staleClaim.Id, staleClaim.Revision,
                new(WorkflowContract.EmptyObject, new AskWorkflowQuestions([new("choice", "Choose")])));
            Wait(staleParent, staleChild.Id);
            var staleProxy = store.ResumeCompletedDependencies().Single(run => run.Id == staleParent.Id);
            _ = store.Answer(new("direct-child-answer", staleChild.Id, staleChild.Questions!.Id,
                staleChild.Questions.Revision, new Dictionary<string, string> { ["choice"] = "Answered elsewhere" }));
            Reject(() => store.Answer(new("stale-parent-answer", staleProxy.Id, staleProxy.Questions!.Id,
                staleProxy.Questions.Revision, new Dictionary<string, string> { ["choice"] = "Too late" })),
                "A changed child cannot receive a stale parent answer.");
            Check(store.Get(staleParent.Id).Questions?.Id == staleProxy.Questions!.Id &&
                store.Get(staleParent.Id).LocalChildQuestionRelay is not null,
                "A rejected stale answer leaves the owner form and binding visible.");
            var supersededParent = store.ResumeCompletedDependencies().Single(run => run.Id == staleParent.Id);
            Check(supersededParent.Status == WorkflowStatus.Waiting && supersededParent.Questions is null &&
                supersededParent.WaitingForRunIds.SequenceEqual([staleChild.Id]) &&
                store.JournalRevision(staleParent.Submission.ConversationId) > 0,
                "A changed child explicitly supersedes the old Main form while retaining the barrier.");
            store.Cancel(staleParent.Id);

            var sequentialParent = Parent("two-questions");
            var firstChild = Child(sequentialParent, "first");
            var secondChild = Child(sequentialParent, "second");
            foreach (var child in new[] { firstChild, secondChild })
            {
                var claimed = store.TryClaim(child.Id)!;
                store.ApplyStep(claimed.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new AskWorkflowQuestions([new("response", "Provide one answer")])));
            }
            Wait(sequentialParent, firstChild.Id, secondChild.Id);
            var firstForm = store.ResumeCompletedDependencies().Single(run => run.Id == sequentialParent.Id);
            Check(firstForm.LocalChildQuestionRelay?.ChildRunId == firstChild.Id,
                "The first waiting child in the saved barrier owns the first Main question.");
            _ = store.Answer(new("first-child-owner-answer", sequentialParent.Id, firstForm.Questions!.Id,
                firstForm.Questions.Revision, new Dictionary<string, string> { ["response"] = "First" }));
            Complete(store.Get(firstChild.Id));
            var secondForm = store.ResumeCompletedDependencies().Single(run => run.Id == sequentialParent.Id);
            Check(secondForm.LocalChildQuestionRelay?.ChildRunId == secondChild.Id &&
                secondForm.Status == WorkflowStatus.Waiting && secondForm.Questions is not null,
                "A second pending child gets its own question after the first answer; parent never re-delegates.");
            store.Cancel(sequentialParent.Id);
            Reject(() => Child(one, "recursive"), "Recursive child admission is denied.");
            Reject(() => store.Submit(ChildInput(parent, "foreign") with { Caller = new(EventActorKind.User) }), "Parent identity cannot be supplied without host provenance.");
            Reject(() => store.Submit(ChildInput(parent, "same") with { ConversationId = parent.Submission.ConversationId }), "Child cannot reuse parent context.");

            var navigationParent = Parent("navigation-owner");
            var navigationChild = store.Submit(ChildInput(navigationParent, "agent") with
                { DefinitionId = navigationParent.Submission.DefinitionId });
            Complete(navigationChild);
            var childBeforeNavigation = CerebralJson.Element(store.Get(navigationChild.Id)).GetRawText();
            var childJournalBeforeNavigation = store.JournalRevision(navigationChild.Submission.ConversationId);
            var projectParent = store.Submit(navigationParent.Submission with
            {
                RequestId = "navigation-project-owner", ConversationId = "navigation-project-context",
                ProjectId = "navigation-project", UserText = "Visible project owner task"
            });
            var projectChild = Child(projectParent, "agent");
            Complete(projectChild);
            // A retained owner turn followed by a child turn exercises the grouping predicate:
            // discovery must select root MIN/MAX rather than discard the whole conversation at its latest child.
            var mixedOwner = store.Submit(ChildInput(navigationParent, "mixed-owner") with
            {
                ParentRunId = null, Caller = new(EventActorKind.User, "owner"), AssignmentText = null,
                UserText = "Visible owner title"
            });
            Complete(mixedOwner);
            var mixedChild = store.Submit(ChildInput(navigationParent, "mixed-agent") with
                { ConversationId = mixedOwner.Submission.ConversationId });
            Complete(mixedChild);
            var recent = store.ListConversations(navigationParent.Submission.DefinitionId).Conversations;
            Check(recent.Any(summary => summary.Id == navigationParent.Submission.ConversationId) &&
                recent.Any(summary => summary.Id == projectParent.Submission.ConversationId) &&
                recent.All(summary => summary.Id != navigationChild.Submission.ConversationId),
                "Recent discovery retains normal owner/project conversations and excludes child contexts of the same definition.");
            string[] navigationPins = [navigationParent.Submission.ConversationId, navigationChild.Submission.ConversationId];
            var protectedNavigation = store.ListProtectedConversations(navigationParent.Submission.DefinitionId, navigationPins).Conversations;
            Check(protectedNavigation.Any(summary => summary.Id == navigationParent.Submission.ConversationId) &&
                protectedNavigation.Any(summary => summary.Id == projectParent.Submission.ConversationId) &&
                protectedNavigation.All(summary => summary.Id != navigationChild.Submission.ConversationId),
                "Protected discovery retains pinned owners and project owners while a supplied child pin cannot promote a child into navigation.");
            var generalNavigation = store.ListGeneralConversations(navigationParent.Submission.DefinitionId, navigationPins).Conversations;
            Check(generalNavigation.Any(summary => summary.Id == parent.Submission.ConversationId) &&
                generalNavigation.All(summary => summary.Id != navigationParent.Submission.ConversationId &&
                    summary.Id != navigationChild.Submission.ConversationId && summary.ProjectId is null),
                "General discovery preserves unpinned owner conversations while excluding protected owners and child contexts.");
            Check(store.HasConversationForProject(projectParent.Submission.DefinitionId, "navigation-project") &&
                !store.HasConversationForProject(projectChild.Submission.DefinitionId, "navigation-project"),
                "Project discovery is supported by its root owner, never only a matching child definition.");
            Check(store.ListConversations(projectChild.Submission.DefinitionId).Conversations.Single() is { } rootSummary &&
                rootSummary.Id == mixedOwner.Submission.ConversationId && rootSummary.LatestRunId == mixedOwner.Id &&
                rootSummary.Title == "Visible owner title",
                "Root filtering before first/last grouping preserves an owner's title and latest root turn despite a later retained child turn.");
            Check(store.GetConversation(navigationChild.Submission.DefinitionId, navigationChild.Submission.ConversationId)?.LatestRunId == navigationChild.Id &&
                store.GetConversation(projectChild.Submission.DefinitionId, projectChild.Submission.ConversationId)?.ProjectId == "navigation-project" &&
                store.GetConversation(mixedChild.Submission.DefinitionId, mixedChild.Submission.ConversationId)?.LatestRunId == mixedChild.Id,
                "Exact conversation reads still expose retained child and project-child logs, including the actual latest mixed turn.");
            Check(CerebralJson.Element(store.Get(navigationChild.Id)).GetRawText() == childBeforeNavigation &&
                store.JournalRevision(navigationChild.Submission.ConversationId) == childJournalBeforeNavigation &&
                navigationPins.SequenceEqual(new[] { navigationParent.Submission.ConversationId, navigationChild.Submission.ConversationId }),
                "Navigation queries alter neither child state/history nor the supplied pin collection.");
            Complete(navigationParent); Complete(projectParent);

            var foreign = Parent("foreign-parent");
            Reject(() => Wait(foreign, one.Id), "Barrier cannot collect a foreign child.");
            var cancelled = Parent("cancel"); var cancelChild = Child(cancelled, "one");
            store.Cancel(cancelled.Id);
            Check(store.Get(cancelChild.Id).CancelRequested && store.Get(cancelChild.Id).Status == WorkflowStatus.Cancelled, "Parent cancellation atomically drains queued children.");
            Reject(() => Child(cancelled, "late"), "Cancelled parent cannot admit late children.");

            var uncertainParent = Parent("uncertain-parent"); var uncertainChild = Child(uncertainParent, "one");
            var claimedChild = store.TryClaim(uncertainChild.Id)!;
            var uncertainCommand = new WorkflowCommandDescriptor("fixture.coordination:uncertain", 1, "Fixture intent",
                CerebralJson.Element(new { type = "object" }), CerebralJson.Element(new { type = "object" }), []);
            var preparedChild = store.ApplyStep(claimedChild.Id, claimedChild.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(uncertainCommand.Id, 1, WorkflowContract.EmptyObject)), uncertainCommand);
            var dispatchedChild = store.DispatchEffect(preparedChild.Id, preparedChild.Revision);
            store.CompleteEffect(dispatchedChild.Id, dispatchedChild.Effect!.Id,
                new(WorkflowEffectState.Unknown, Error: "Fixture unconfirmed effect; no external call was made."));
            Wait(uncertainParent, uncertainChild.Id);
            Check(store.ResumeCompletedDependencies().Count == 0, "An uncertain child is not a terminal outcome and cannot release its parent.");
            store.Cancel(uncertainParent.Id);
            Check(store.Get(uncertainChild.Id).CancelRequested && !store.Get(uncertainChild.Id).IsTerminal,
                "Cancellation preserves an uncertain child for reconciliation instead of fabricating termination.");

            for (var index = 0; index < 35; index++)
            {
                var blocked = Parent("blocked-" + index); var pending = Child(blocked, "one"); Wait(blocked, pending.Id);
            }
            var readyParent = Parent("after-blocked"); var readyChild = Child(readyParent, "one"); Complete(readyChild); Wait(readyParent, readyChild.Id);
            Check(store.ResumeCompletedDependencies(1).Single().Id == readyParent.Id, "Older blocked barriers cannot starve a ready parent.");
            var restart = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            restart.RecoverInterrupted();
            Check(restart.Get("blocked-0").Status == WorkflowStatus.Waiting && restart.Get("blocked-0").WaitingForRunIds.Count == 1,
                "Restart retains pending barrier and child identities.");

            // Remove all fixture background candidates from scheduling; preserve receipts.
            foreach (var id in Enumerable.Range(0, 35).Select(index => "blocked-" + index).Concat(new[] { "join", "foreign-parent", "after-blocked" }))
                store.Cancel(id);
            var single = Parent("single-slot"); Child(single, "one"); Child(single, "two");
            await using var models = new ModelRuntime(state, configuration.Get, events);
            using var scheduler = new JobScheduler(state, configuration, new UnusedExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, workflows);
            await scheduler.StartAsync(default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!store.Get(single.Id).IsTerminal)
            {
                if (deadline.IsCancellationRequested)
                    throw new InvalidOperationException("Scheduler fixture did not settle: " + string.Join("; ",
                        store.ChildRunIds(single.Id).Prepend(single.Id).Select(id =>
                        { var run = store.Get(id); return id + ": " + run.Status + " " + run.WaitingReason + " " + run.Error; })));
                await Task.Delay(25);
            }
            Check(store.Get(single.Id).Status == WorkflowStatus.Completed, "One real scheduler slot finishes parent and two children without deadlock.");
            Check(store.ChildRunIds(single.Id).All(id => store.Get(id).Status == WorkflowStatus.Completed), "Both separate child contexts complete.");
            await scheduler.StopAsync(default);
            checks += ConversationHarnessTests.RunDuplicateDelegationChecks();
            Console.WriteLine($"Coordination workflow checks passed: {checks} (deterministic SQLite and real scheduler; no model/auditor inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-coordination-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private sealed class Module : IWorkflowModule
    {
        public string Id => "fixture.coordination";
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition(true), new Definition(false)];
    }
    private sealed class Definition(bool parent) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.coordination:" + (parent ? "parent" : "child"),
            "1.0.0", "Deterministic coordination", CerebralJson.Element(new { type = "object" }), []);
        public WorkflowStep Advance(WorkflowRun run) => parent && run.Signal.Kind != WorkflowSignalKind.DependencyResults
            ? new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns([run.Id + "-one", run.Id + "-two"]))
            : new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new { text = "Fixture complete" })));
    }
    private sealed class UnusedExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken cancellationToken) => throw new InvalidOperationException("No legacy agent expected.");
    }
}
