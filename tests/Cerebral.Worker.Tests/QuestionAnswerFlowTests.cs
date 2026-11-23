using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static class QuestionAnswerFlowTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-question-answer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new StateStore(root); var configuration = new Configuration(store); var events = new EventHub();
        await using var runtime = new ModelRuntime(store, configuration.Get, events);
        await using var tools = new ToolDispatcher(configuration.Get);
        var actionEngine = new AgentEngine(store, configuration, runtime, tools, events);
        using var scheduler = new JobScheduler(store, configuration, new QuestionFlowExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
        try
        {
            var fixtureModel = Path.Combine(root, "fixture.gguf"); File.WriteAllBytes(fixtureModel, "GGUF"u8.ToArray());
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Question fixture", Path = fixtureModel, DeclaredType = "model", MetadataSource = "fixture" });
            store.Put("agent", "lead", new AgentProfile { Id = "lead", Name = "lead", ModelId = "fixture", Role = "lead", AllowedRoots = [root], Capabilities = ["filesystem.read"], AllowedTools = ["file.read"] });

            var adoptionSession = new TaskSession { Id = "question-adoption", ProfileId = "lead", Revision = 4 };
            var adoptionJob = new AgentJob
            {
                Id = "adoption-job", SessionId = adoptionSession.Id, ProfileId = "lead", Status = "running",
                Profile = store.Get<AgentProfile>("agent", "lead")!
            };
            store.Put("session", adoptionSession.Id, adoptionSession); store.Put("job", adoptionJob.Id, adoptionJob);
            const string adoptedAssumption = "Continue with the reversible blue theme until the user answers.";
            var adoptedQuestions = new List<UserQuestion>
            {
                new() { Id = "adopt-required", JobId = "adoption-job", SessionId = adoptionSession.Id, Prompt = "Which existing target should be changed?", Required = true },
                new() { Id = "adopt-optional", JobId = "adoption-job", SessionId = adoptionSession.Id, Prompt = "Should the draft use the blue theme?", CanAssume = true, Assumption = adoptedAssumption }
            };
            var adoptedContext = AgentPrompt.OptionalAssumptionContext(adoptedQuestions)!;
            Check(!AgentQuestionAdoption.TryAdopt(store, adoptionSession.Id, 3, adoptionJob.Id, adoptedQuestions, adoptedContext) &&
                adoptedQuestions.All(x => store.Get<UserQuestion>("question", x.Id) is null) &&
                store.Get<TaskSession>("session", adoptionSession.Id)!.Messages.Count == 0,
                "a stale model-facing revision adopts neither question cards nor hidden assumption context");
            Check(AgentQuestionAdoption.TryAdopt(store, adoptionSession.Id, 4, adoptionJob.Id, adoptedQuestions, adoptedContext),
                "the exact model-facing revision adopts a mixed question batch in one transaction");
            var adoptedSession = store.Get<TaskSession>("session", adoptionSession.Id)!;
            Check(adoptedQuestions.All(x => store.Get<UserQuestion>("question", x.Id) is not null) &&
                 adoptedSession.Messages.Count(x => x.Role == "assistant" && x.Content.StartsWith("Question: ", StringComparison.Ordinal)) == 2 &&
                 adoptedSession.Messages.Count(x => x.Origin == "service" && x.Content.Contains(adoptedAssumption, StringComparison.Ordinal)) == 1,
                "accepted cards and one bounded model-facing optional assumption are committed without duplicate visible questions");

            const string explicitBatchRequest = "Ask me three required questions and wait for every answer before you continue.";
            var batchSession = new TaskSession
            {
                Id = "explicit-question-batch", ProfileId = "lead", Revision = 1,
                Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = explicitBatchRequest }]
            };
            var batchJob = new AgentJob
            {
                Id = "explicit-question-batch-job", SessionId = batchSession.Id, ProfileId = "lead", Status = "running",
                Prompt = explicitBatchRequest, StartSequence = 1, Profile = store.Get<AgentProfile>("agent", "lead")!
            };
            store.Put("session", batchSession.Id, batchSession); store.Put("job", batchJob.Id, batchJob);
            var partialBatch = new List<UserQuestion>
            {
                new() { Id = "explicit-partial", JobId = batchJob.Id, SessionId = batchSession.Id, Prompt = "First decision?", Required = true }
            };
            Check(!actionEngine.TryAdoptQuestions(batchJob, batchSession.Revision, partialBatch, null, explicitBatchRequest) &&
                  store.Get<UserQuestion>("question", partialBatch[0].Id) is null &&
                  store.Get<AgentJob>("job", batchJob.Id)!.Status == "running",
                "an explicit three-question request cannot adopt one card and create sequential blockers");
            var completeBatch = Enumerable.Range(1, 3).Select(index => new UserQuestion
            {
                Id = "explicit-complete-" + index, JobId = batchJob.Id, SessionId = batchSession.Id,
                Prompt = $"Decision {index}?", Required = true
            }).ToList();
            Check(actionEngine.TryAdoptQuestions(batchJob, batchSession.Revision, completeBatch, null, explicitBatchRequest) &&
                  completeBatch.All(x => store.Get<UserQuestion>("question", x.Id) is not null) &&
                  store.Get<TaskSession>("session", batchSession.Id)!.Messages.Count(x => x.Role == "assistant") == 3 &&
                  store.Get<AgentJob>("job", batchJob.Id) is { Status: "waiting-human" },
                "one explicit three-card batch is adopted atomically with separate answer records and one waiting state");
            scheduler.Cancel(batchJob.Id);
            Check(store.Get<AgentJob>("job", batchJob.Id) is { Status: "cancelled", CancelRequested: true } &&
                  completeBatch.All(x => store.Get<UserQuestion>("question", x.Id) is not null),
                "cancellation after official batch adoption preserves the recorded cards while stopping their assignment");

            var adoptionRaceSession = new TaskSession { Id = "question-adoption-race", ProfileId = "lead", Revision = 1 };
            var adoptionRaceJob = new AgentJob
            {
                Id = "question-adoption-race-job", SessionId = adoptionRaceSession.Id, ProfileId = "lead", Status = "running",
                Profile = store.Get<AgentProfile>("agent", "lead")!
            };
            var staleRaceQuestion = new UserQuestion
            {
                Id = "question-adoption-race-card", JobId = adoptionRaceJob.Id, SessionId = adoptionRaceSession.Id,
                Prompt = "Which project should be changed?", Required = true
            };
            store.Put("session", adoptionRaceSession.Id, adoptionRaceSession); store.Put("job", adoptionRaceJob.Id, adoptionRaceJob);
            using var adoptionGateEntered = new ManualResetEventSlim(); using var allowAdoptionSteer = new ManualResetEventSlim();
            var adoptionSteer = Task.Run(() => store.LinearizedSession(adoptionRaceSession.Id, () =>
            {
                adoptionGateEntered.Set(); allowAdoptionSteer.Wait();
                return scheduler.Steer(adoptionRaceJob.Id, "A newer project selection arrived.", "question-adoption-steer");
            }));
            Check(adoptionGateEntered.Wait(TimeSpan.FromSeconds(5)), "the deterministic question-adoption race acquired the input boundary first");
            var staleAdoption = Task.Run(() => AgentQuestionAdoption.TryAdopt(store, adoptionRaceSession.Id,
                adoptionRaceSession.Revision, adoptionRaceJob.Id, [staleRaceQuestion], null));
            allowAdoptionSteer.Set(); await adoptionSteer;
            Check(!await staleAdoption && store.Get<UserQuestion>("question", staleRaceQuestion.Id) is null &&
                store.Get<TaskSession>("session", adoptionRaceSession.Id)!.Messages.Single().Content == "A newer project selection arrived.",
                "steering committed first prevents stale question publication or a waiting-human transition candidate");

            var cancelSession = new TaskSession { Id = "question-cancel-race", ProfileId = "lead", Revision = 1 };
            var cancelJob = new AgentJob
            {
                Id = "question-cancel-race-job", SessionId = cancelSession.Id, ProfileId = "lead", Status = "running",
                Profile = store.Get<AgentProfile>("agent", "lead")!
            };
            var cancelledQuestion = new UserQuestion
            {
                Id = "question-cancel-race-card", JobId = cancelJob.Id, SessionId = cancelSession.Id,
                Prompt = "This card must never appear.", Required = true
            };
            store.Put("session", cancelSession.Id, cancelSession); store.Put("job", cancelJob.Id, cancelJob);
            using var cancelGateEntered = new ManualResetEventSlim(); using var allowCancelCommit = new ManualResetEventSlim();
            var cancellation = Task.Run(() => store.Locked(() =>
            {
                cancelGateEntered.Set(); allowCancelCommit.Wait(); return scheduler.Cancel(cancelJob.Id);
            }));
            Check(cancelGateEntered.Wait(TimeSpan.FromSeconds(5)), "the deterministic question cancellation race acquired the durable state boundary first");
            var cancelledAdoption = Task.Run(() =>
            {
                try
                {
                    AgentQuestionAdoption.TryAdopt(store, cancelSession.Id, cancelSession.Revision, cancelJob.Id,
                        [cancelledQuestion], null);
                    return false;
                }
                catch (OperationCanceledException) { return true; }
            });
            allowCancelCommit.Set(); await cancellation;
            Check(await cancelledAdoption && store.Get<UserQuestion>("question", cancelledQuestion.Id) is null &&
                  store.Get<TaskSession>("session", cancelSession.Id)!.Messages.Count == 0 &&
                  store.Get<AgentJob>("job", cancelJob.Id) is { Status: "cancelled", CancelRequested: true },
                "cancellation committed first rejects question cards, assumptions, and a waiting-human transition atomically");

            var current = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Ask two necessary questions." });
            current.Status = "waiting-human"; store.Put("job", current.Id, current);
            store.Put("question", "current-one", new UserQuestion { Id = "current-one", JobId = current.Id, SessionId = current.SessionId, Prompt = "First choice?", Required = true });
            store.Put("question", "current-two", new UserQuestion { Id = "current-two", JobId = current.Id, SessionId = current.SessionId, Prompt = "Second choice?", Required = true });
            scheduler.Answer("current-one", "Alpha"); scheduler.Answer("current-one", "Alpha");
            Check(scheduler.Get(current.Id).Status == "waiting-human" && store.Get<UserQuestion>("question", "current-one")!.Answer == "Alpha" && store.Get<UserQuestion>("question", "current-two")!.Answer is null,
                "distinct required questions retain independent answer state");
            scheduler.Answer("current-two", "Beta");
            var currentMessages = store.Get<TaskSession>("session", current.SessionId)!.Messages.Where(x => x.Origin == "question-answer").ToList();
            Check(scheduler.Get(current.Id).Status == "queued" && currentMessages.Count == 2 && currentMessages.Select(x => x.SourceQuestionId).Distinct(StringComparer.Ordinal).Count() == 2,
                "the job resumes only after every required question is answered once");
            scheduler.Cancel(current.Id);

            var timeoutSettings = configuration.Get();
            timeoutSettings.QuestionTimeoutSeconds = 1; timeoutSettings.QuestionTimeoutAction = "assume";
            configuration.Save(timeoutSettings);
            var timeoutJob = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Exercise optional clarification ordering." });
            var expired = DateTimeOffset.UtcNow.AddMinutes(-2);
            foreach (var question in new[]
            {
                new UserQuestion { Id = "timeout-first-equal", JobId = timeoutJob.Id, SessionId = timeoutJob.SessionId, Prompt = "Equal after timeout?", CanAssume = true, Assumption = "Blue", CreatedAt = expired },
                new UserQuestion { Id = "timeout-first-different", JobId = timeoutJob.Id, SessionId = timeoutJob.SessionId, Prompt = "Different after timeout?", CanAssume = true, Assumption = "Harbor", CreatedAt = expired },
                new UserQuestion { Id = "answer-first-equal", JobId = timeoutJob.Id, SessionId = timeoutJob.SessionId, Prompt = "Equal before timeout?", CanAssume = true, Assumption = "North", CreatedAt = expired },
                new UserQuestion { Id = "answer-first-different", JobId = timeoutJob.Id, SessionId = timeoutJob.SessionId, Prompt = "Different before timeout?", CanAssume = true, Assumption = "Draft", CreatedAt = expired }
            }) store.Put("question", question.Id, question);
            scheduler.Answer("answer-first-equal", "North");
            scheduler.Answer("answer-first-different", "Publish");
            scheduler.ProcessQuestionTimeouts();
            var afterTimeout = store.Get<TaskSession>("session", timeoutJob.SessionId)!;
            Check(store.Get<UserQuestion>("question", "timeout-first-equal")!.Assumed &&
                store.Get<UserQuestion>("question", "timeout-first-different")!.Assumed &&
                !store.Get<UserQuestion>("question", "answer-first-equal")!.Assumed &&
                !store.Get<UserQuestion>("question", "answer-first-different")!.Assumed &&
                afterTimeout.Messages.Count(x => x.SourceQuestionId is "timeout-first-equal" or "timeout-first-different" && x.Origin == "service") == 2,
                "timeouts apply only to still-unanswered optional questions and save one source-bound service assumption each");
            scheduler.Answer("timeout-first-equal", "Blue");
            scheduler.Answer("timeout-first-different", "Lighthouse");
            var explicitTimeoutAnswers = store.Get<TaskSession>("session", timeoutJob.SessionId)!.Messages
                .Where(x => x.SourceQuestionId is "timeout-first-equal" or "timeout-first-different" or "answer-first-equal" or "answer-first-different").ToList();
            Check(new[] { "timeout-first-equal", "timeout-first-different", "answer-first-equal", "answer-first-different" }
                    .All(questionId => explicitTimeoutAnswers.Count(x => x.SourceQuestionId == questionId) == 1) &&
                explicitTimeoutAnswers.All(x => x.Role == "user" && x.Origin == "question-answer") &&
                !store.Get<UserQuestion>("question", "timeout-first-equal")!.Assumed &&
                !store.Get<UserQuestion>("question", "timeout-first-different")!.Assumed &&
                store.Get<UserQuestion>("question", "timeout-first-different")!.Answer == "Lighthouse",
                "equal or different explicit answers supersede assumptions while answer-first ordering remains explicit and idempotent");
            var explicitCorrectionRejected = false;
            try { scheduler.Answer("timeout-first-different", "Pier"); } catch (InvalidOperationException) { explicitCorrectionRejected = true; }
            Check(explicitCorrectionRejected && store.Get<UserQuestion>("question", "timeout-first-different")!.Answer == "Lighthouse",
                "a prior explicit answer still rejects a different answer after an assumption was superseded");
            scheduler.Cancel(timeoutJob.Id);

            var older = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Finish an earlier stage." });
            older.Status = "completed"; older.Result = "earlier stage complete"; older.FinishedAt = DateTimeOffset.UtcNow; store.Put("job", older.Id, older);
            var sharedSession = store.Get<TaskSession>("session", older.SessionId)!;
            sharedSession.RemoteSessionIds["remote"] = "stable-remote-session"; store.Put("session", sharedSession.Id, sharedSession);
            store.Put("question", "older-one", new UserQuestion { Id = "older-one", JobId = older.Id, SessionId = older.SessionId, Prompt = "What color should the next section use?" });
            store.Put("question", "older-two", new UserQuestion { Id = "older-two", JobId = older.Id, SessionId = older.SessionId, Prompt = "Which location should the next section favor?" });
            store.Put("question", "stale-required", new UserQuestion { Id = "stale-required", JobId = older.Id, SessionId = older.SessionId, Prompt = "Authorize the finished operation?", Required = true });

            var active = scheduler.Submit(new() { ProfileId = "lead", SessionId = older.SessionId, Prompt = "Continue with independent work." });
            active.WorkerId = "remote"; active.RemoteJobId = "active-remote-job"; active.RemoteSessionId = "stable-remote-session";
            active.Status = "remote-waiting"; active.NextReconcileAt = DateTimeOffset.UtcNow.AddHours(1); store.Put("job", active.Id, active);
            var revisionBeforeAnswers = store.Get<TaskSession>("session", older.SessionId)!.Revision;
            scheduler.Answer("older-one", "Blue"); scheduler.Answer("older-two", "Harbor"); scheduler.Answer("older-one", "Blue");

            var after = store.Get<TaskSession>("session", older.SessionId)!;
            var answerMessages = after.Messages.Where(x => x.Origin == "question-answer" && x.SourceQuestionId is "older-one" or "older-two").ToList();
            Check(scheduler.Get(older.Id).Status == "completed" && scheduler.Get(active.Id).Status == "remote-waiting",
                "answering older optional questions never restarts their completed job or replaces active work");
            Check(store.Get<UserQuestion>("question", "older-one")!.Answer == "Blue" && store.Get<UserQuestion>("question", "older-two")!.Answer == "Harbor" &&
                answerMessages.Count == 2 && after.Revision == revisionBeforeAnswers + 2,
                "multiple optional answers are saved and injected exactly once into shared session context");
            Check(after.RemoteSessionIds.Count == 1 && after.RemoteSessionIds["remote"] == "stable-remote-session",
                "active remote conversation mapping survives older optional answers");

            var deliveries = store.List<JsonElement>("steering").Where(x => x.GetProperty("jobId").GetString() == active.Id).ToList();
            Check(deliveries.Count == 2 && deliveries.All(x => !x.GetProperty("delivered").GetBoolean()) &&
                deliveries.Any(x => x.GetProperty("prompt").GetString()!.Contains("Blue", StringComparison.Ordinal)) &&
                deliveries.Any(x => x.GetProperty("prompt").GetString()!.Contains("Harbor", StringComparison.Ordinal)) &&
                scheduler.Get(active.Id).NextReconcileAt <= DateTimeOffset.UtcNow,
                "each answer has one pending delivery for a reused remote active context");

            var conflicting = false;
            try { scheduler.Answer("older-one", "Red"); } catch (InvalidOperationException) { conflicting = true; }
            Check(conflicting && store.Get<UserQuestion>("question", "older-one")!.Answer == "Blue" &&
                store.List<JsonElement>("steering").Count(x => x.GetProperty("jobId").GetString() == active.Id) == 2,
                "an answered question rejects a different retry without changing history or delivery state");
            var staleRequiredRejected = false;
            try { scheduler.Answer("stale-required", "Yes"); } catch (InvalidOperationException) { staleRequiredRejected = true; }
            Check(staleRequiredRejected && store.Get<UserQuestion>("question", "stale-required")!.Answer is null && store.Get<TaskSession>("session", older.SessionId)!.RemoteSessionIds["remote"] == "stable-remote-session",
                "a stale required decision cannot be applied to a different active assignment");

            var relayOwner = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Relay an optional remote question." });
            relayOwner.WorkerId = "remote"; relayOwner.RemoteJobId = "remote-question-job";
            relayOwner.RemoteSessionId = "remote-question-session"; relayOwner.Status = "remote-waiting";
            relayOwner.NextReconcileAt = DateTimeOffset.UtcNow.AddHours(1); store.Put("job", relayOwner.Id, relayOwner);
            var remoteCreated = new DateTimeOffset(2026, 9, 14, 19, 0, 0, TimeSpan.Zero);
            const string remoteAssumption = "Continue with the harbor location until the author responds.";
            var remoteOptional = new UserQuestion
            {
                Id = "remote-optional-timeout", JobId = relayOwner.RemoteJobId, SessionId = relayOwner.RemoteSessionId,
                Prompt = "Should the next scene remain at the harbor?", Reason = "The next section can continue independently.",
                Choices = ["Harbor", "Lighthouse"], CanAssume = true, Assumption = remoteAssumption,
                CreatedAt = remoteCreated, KnowledgeProposalId = "remote-proposal"
            };
            var questionEvents = events.Subscribe();
            var relayedOptional = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteOptional);
            Check(relayedOptional.Id == relayOwner.Id + "." + remoteOptional.Id && relayedOptional.JobId == relayOwner.Id &&
                relayedOptional.SessionId == relayOwner.SessionId && relayedOptional.RemoteQuestionId == remoteOptional.Id &&
                relayedOptional.CanAssume && relayedOptional.Assumption == remoteAssumption && relayedOptional.CreatedAt == remoteCreated &&
                relayedOptional.KnowledgeProposalId == remoteOptional.KnowledgeProposalId &&
                questionEvents.Reader.TryRead(out var createdEvent) && createdEvent.Type == "question.updated",
                "LAN question import preserves coordinator identity and copies optional, creation, and knowledge metadata");
            remoteOptional.DeadlineAt = remoteCreated.AddMinutes(2);
            var deadlineUpdate = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteOptional);
            Check(deadlineUpdate.DeadlineAt == remoteOptional.DeadlineAt &&
                questionEvents.Reader.TryRead(out var deadlineEvent) && deadlineEvent.Type == "question.updated",
                "a remote optional timeout deadline advances the relayed card and publishes its update");
            scheduler.ProcessQuestionTimeouts();
            Check(store.Get<UserQuestion>("question", relayedOptional.Id)!.Answer is null &&
                store.Get<TaskSession>("session", relayOwner.SessionId)!.Messages.All(x => x.SourceQuestionId != relayedOptional.Id),
                "the coordinator never applies its own timeout assumption to a mirrored worker question");
            remoteOptional.Answer = remoteAssumption; remoteOptional.Assumed = true;
            var assumptionUpdate = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteOptional);
            Check(assumptionUpdate.Answer == remoteAssumption && assumptionUpdate.Assumed && assumptionUpdate.RemoteAnswerDelivered &&
                store.Get<TaskSession>("session", relayOwner.SessionId)!.Messages.Count(x => x.SourceQuestionId == assumptionUpdate.Id && x.Origin == "service") == 1 &&
                questionEvents.Reader.TryRead(out var assumptionEvent) && assumptionEvent.Type == "question.updated",
                "a remote timeout assumption advances answer state and projects one service-provenance context record");
            while (questionEvents.Reader.TryRead(out _)) { }
            remoteOptional.Answer = "Use the lighthouse location."; remoteOptional.Assumed = false;
            var explicitRemoteUpdate = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteOptional);
            var explicitRemoteContext = store.Get<TaskSession>("session", relayOwner.SessionId)!.Messages.Where(x => x.SourceQuestionId == assumptionUpdate.Id).ToList();
            Check(explicitRemoteUpdate.Answer == remoteOptional.Answer && !explicitRemoteUpdate.Assumed && explicitRemoteUpdate.RemoteAnswerDelivered &&
                explicitRemoteContext.Count == 1 && explicitRemoteContext[0].Origin == "question-answer" && explicitRemoteContext[0].Role == "user" &&
                explicitRemoteContext[0].Content.Contains("lighthouse", StringComparison.OrdinalIgnoreCase),
                "a later remote explicit answer replaces the mirrored assumption and its single context projection");
            while (questionEvents.Reader.TryRead(out _)) { }
            remoteOptional.Prompt = "Changed remote prompt";
            var immutableRejected = false;
            try { scheduler.SynchronizeRemoteQuestion(relayOwner, remoteOptional); } catch (InvalidDataException) { immutableRejected = true; }
            var unchangedRelay = store.Get<UserQuestion>("question", relayedOptional.Id)!;
            Check(immutableRejected && unchangedRelay.Prompt == "Should the next scene remain at the harbor?" &&
                unchangedRelay.Answer == "Use the lighthouse location." && !questionEvents.Reader.TryRead(out _),
                "a changed remote immutable field is rejected without mutating or republishing the coordinator card");
            events.Unsubscribe(questionEvents.Id);

            var remoteDifferent = new UserQuestion
            {
                Id = "remote-race-different", JobId = relayOwner.RemoteJobId, SessionId = relayOwner.RemoteSessionId,
                Prompt = "Which route should the scene take?", CanAssume = true, Assumption = "Use the harbor route.", CreatedAt = remoteCreated.AddMinutes(1)
            };
            var localDifferent = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteDifferent);
            scheduler.Answer(localDifferent.Id, "Use the lighthouse route.");
            remoteDifferent.Answer = remoteDifferent.Assumption; remoteDifferent.Assumed = true;
            var differentRace = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteDifferent);
            Check(differentRace.Answer == "Use the lighthouse route." && !differentRace.Assumed && !differentRace.RemoteAnswerDelivered &&
                store.Get<TaskSession>("session", relayOwner.SessionId)!.Messages.Count(x => x.SourceQuestionId == differentRace.Id && x.Origin == "question-answer") == 1 &&
                scheduler.HasPendingRemoteFollowUp(relayOwner),
                "a coordinator explicit answer wins a different remote timeout assumption and remains a truthful terminal follow-up");
            remoteDifferent.Answer = "Use the lighthouse route."; remoteDifferent.Assumed = false;
            Check(scheduler.SynchronizeRemoteQuestion(relayOwner, remoteDifferent).RemoteAnswerDelivered,
                "a matching remote explicit answer confirms delivery after the different-text timeout race");

            var remoteEqual = new UserQuestion
            {
                Id = "remote-race-equal", JobId = relayOwner.RemoteJobId, SessionId = relayOwner.RemoteSessionId,
                Prompt = "Which color should the scene use?", CanAssume = true, Assumption = "Blue", CreatedAt = remoteCreated.AddMinutes(2)
            };
            var localEqual = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteEqual);
            scheduler.Answer(localEqual.Id, "Blue");
            remoteEqual.Answer = "Blue"; remoteEqual.Assumed = true;
            var equalRace = scheduler.SynchronizeRemoteQuestion(relayOwner, remoteEqual);
            Check(equalRace.Answer == "Blue" && !equalRace.Assumed && !equalRace.RemoteAnswerDelivered,
                "matching text from a remote assumption is not treated as delivery of the coordinator explicit answer");
            remoteEqual.Assumed = false;
            Check(scheduler.SynchronizeRemoteQuestion(relayOwner, remoteEqual).RemoteAnswerDelivered && !scheduler.HasPendingRemoteFollowUp(relayOwner),
                "matching text becomes delivered only after the worker reports explicit-answer provenance, clearing terminal follow-up state");

            var delegationParent = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Coordinate a delegated clarification." });
            var delegationHelper = scheduler.Submit(new() { ProfileId = "lead", ParentJobId = delegationParent.Id, Prompt = "Ask the remote clarification." });
            delegationHelper.WorkerId = "remote"; delegationHelper.RemoteJobId = "delegated-remote-job";
            delegationHelper.RemoteSessionId = "delegated-remote-session"; delegationHelper.Status = "remote-waiting";
            store.Put("job", delegationHelper.Id, delegationHelper);
            var delegatedRemote = new UserQuestion
            {
                Id = "delegated-remote-assumption", JobId = delegationHelper.RemoteJobId, SessionId = delegationHelper.RemoteSessionId,
                Prompt = "Should the chapter stay in the city?", CanAssume = true, Assumption = "Keep the city setting.",
                Answer = "Keep the city setting.", Assumed = true, CreatedAt = remoteCreated.AddMinutes(3)
            };
            var delegatedImported = scheduler.SynchronizeRemoteQuestion(delegationHelper, delegatedRemote);
            var helperProjection = store.Get<TaskSession>("session", delegationHelper.SessionId)!.Messages.Where(x => x.SourceQuestionId == delegatedImported.Id).ToList();
            var leadProjection = store.Get<TaskSession>("session", delegationParent.SessionId)!.Messages.Where(x => x.SourceQuestionId == delegatedImported.Id).ToList();
            Check(helperProjection.Count == 1 && leadProjection.Count == 1 && helperProjection[0].Origin == "service" && leadProjection[0].Origin == "service" &&
                helperProjection[0].Content.Contains("not user approval", StringComparison.Ordinal) && leadProjection[0].Content.Contains("not user approval", StringComparison.Ordinal),
                "a first-imported remote assumption projects once with service provenance into helper and lead continuity");
            scheduler.SynchronizeRemoteQuestion(delegationHelper, delegatedRemote);
            Check(store.Get<TaskSession>("session", delegationHelper.SessionId)!.Messages.Count(x => x.SourceQuestionId == delegatedImported.Id) == 1 &&
                store.Get<TaskSession>("session", delegationParent.SessionId)!.Messages.Count(x => x.SourceQuestionId == delegatedImported.Id) == 1,
                "repeated remote synchronization cannot duplicate helper or lead question context");
            delegatedRemote.Answer = "Move the chapter to the coast."; delegatedRemote.Assumed = false;
            var delegatedExplicit = scheduler.SynchronizeRemoteQuestion(delegationHelper, delegatedRemote);
            Check(!delegatedExplicit.Assumed &&
                store.Get<TaskSession>("session", delegationHelper.SessionId)!.Messages.Single(x => x.SourceQuestionId == delegatedImported.Id).Origin == "question-answer" &&
                store.Get<TaskSession>("session", delegationParent.SessionId)!.Messages.Single(x => x.SourceQuestionId == delegatedImported.Id).Content.Contains("coast", StringComparison.Ordinal),
                "a remote explicit answer replaces both delegated assumption projections without duplicating either one");
            scheduler.Cancel(delegationParent.Id);

            var completedParent = scheduler.Submit(new() { ProfileId = "lead", Prompt = "Complete an older lead turn." });
            var completedHelper = scheduler.Submit(new() { ProfileId = "lead", ParentJobId = completedParent.Id, Prompt = "Complete an older helper turn." });
            completedParent.Status = "completed"; completedParent.FinishedAt = DateTimeOffset.UtcNow; store.Put("job", completedParent.Id, completedParent);
            completedHelper.Status = "completed"; completedHelper.FinishedAt = DateTimeOffset.UtcNow; store.Put("job", completedHelper.Id, completedHelper);
            var lateHelperQuestion = new UserQuestion
            {
                Id = "completed-helper-late-answer", JobId = completedHelper.Id, SessionId = completedHelper.SessionId,
                Prompt = "Which ending should the later work use?", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            store.Put("question", lateHelperQuestion.Id, lateHelperQuestion);
            var newerLead = scheduler.Submit(new() { ProfileId = "lead", SessionId = completedParent.SessionId, Prompt = "Continue the lead conversation remotely." });
            newerLead.WorkerId = "remote"; newerLead.RemoteJobId = "newer-lead-remote-job"; newerLead.RemoteSessionId = "newer-lead-remote-session";
            newerLead.Status = "remote-waiting"; newerLead.NextReconcileAt = DateTimeOffset.UtcNow.AddHours(1); store.Put("job", newerLead.Id, newerLead);
            scheduler.Answer(lateHelperQuestion.Id, "Use the hopeful ending.");
            scheduler.Answer(lateHelperQuestion.Id, "Use the hopeful ending.");
            var lateLeadContext = store.Get<TaskSession>("session", completedParent.SessionId)!.Messages.Where(x => x.SourceQuestionId == lateHelperQuestion.Id).ToList();
            var lateDeliveries = store.List<JsonElement>("steering").Where(x => x.GetProperty("jobId").GetString() == newerLead.Id &&
                x.GetProperty("prompt").GetString()!.Contains("hopeful ending", StringComparison.Ordinal)).ToList();
            Check(lateLeadContext.Count == 1 && lateLeadContext[0].Content.Contains("Question from", StringComparison.Ordinal) && lateDeliveries.Count == 1 &&
                !lateDeliveries[0].GetProperty("delivered").GetBoolean() && scheduler.Get(newerLead.Id).NextReconcileAt <= DateTimeOffset.UtcNow,
                "a completed helper's late optional answer projects once and queues one delivery to newer active remote lead work");
            scheduler.Cancel(newerLead.Id);
            scheduler.Cancel(relayOwner.Id);

            Console.WriteLine($"Question answer flow: {assertions} checks passed (real SQLite state and remote-delivery records; no model inference or network).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(full).StartsWith("cerebral-question-answer-tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }

    private sealed class QuestionFlowExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => Task.CompletedTask;
    }
}
