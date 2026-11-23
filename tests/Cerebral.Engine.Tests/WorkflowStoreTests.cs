using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class WorkflowStoreTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        static bool Rejected<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
        static WorkflowSubmission Submission(string id, string? chat = null) => new(id, chat ?? id, "lead", "test.review:review", "1.0.0",
            "Review this source.", "Read the actual source before reporting the result.", J(new { text = "one two three" }), J(new { capabilities = new[] { "diagnostics" } }));
        var descriptor = new WorkflowCommandDescriptor("test.review:measure", 1, "Measure text", J(new { type = "object" }), J(new { type = "object" }), ["diagnostics"]);
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "events.db");
            var journal = new EventJournal(database);
            var store = new WorkflowStore(journal);
            CheckCollaborationQuestionWake(root, Check);
            CheckOwnedCoordinatorSourceEditions(root, Check);
            CheckLocalChildQuestionProxy(root, Check);
            var submission = Submission("review");
            var submitted = store.Submit(submission);
            Check(submitted.Revision == 1 && submitted.Status == WorkflowStatus.Queued && store.CountActive() == 1 && store.HasReady(), "workflow admission persists queued state");
            var admittedSequence = journal.GetRevision("review");
            Check(store.Submit(submission).Id == submitted.Id && journal.GetRevision("review") == admittedSequence, "submission retry returns the same run without duplicate messages or transitions");
            Check(Rejected<WorkflowConflictException>(() => store.Submit(Submission("overlap", "review"))) && store.TryGet("overlap") is null,
                "conversation admits one active run while exact retries still succeed");
            Check(Rejected<WorkflowConflictException>(() => store.Submit(submission with { Input = J(new { text = "changed" }) })), "request ID cannot be reused for changed input");
            Check(Rejected<WorkflowConflictException>(() => store.Submit(Submission("foreign", "review") with { ProfileId = "other" })), "conversation profile ownership cannot be rebound");
            var events = journal.ReadPage("review").Events;
            Check(events.Count(x => x.Type == "workflow.assignment") == 1 && events.Single(x => x.Type == "workflow.assignment").Projection is { Human: HumanVisibility.Hidden, Model: ModelInclusion.Required },
                "hidden plugin assignment remains required model context");
            Check(events.First().Actor is { Kind: EventActorKind.User, Id: null } &&
                events.First().Provenance is { Source: EventSourceKind.User, SourceId: null, AgentId: null, JobId: "review" },
                "default owner input retains user provenance without a service or agent identity");
            var agentRun = store.Submit(Submission("agent") with { Caller = new(EventActorKind.Agent, "helper", "Helper") });
            var agentEvent = journal.ReadPage("agent").Events.First();
            Check(agentEvent.Actor is { Kind: EventActorKind.Agent, Id: "helper" } &&
                agentEvent.Provenance is { Source: EventSourceKind.Agent, SourceId: null, AgentId: "helper", JobId: "agent" },
                "host-supplied agent input retains agent provenance rather than human ownership");
            store.Cancel(agentRun.Id);
            var serviceInput = Submission("service") with
            {
                Caller = new(EventActorKind.Service, "cerebral.interface", "Plugin review"),
                HostAddedRequiredSuccessfulTools = ["plugin.controls"]
            };
            var serviceRun = store.Submit(serviceInput);
            var serviceEvent = journal.ReadPage("service").Events.First();
            Check(serviceEvent.Actor == serviceInput.Caller &&
                serviceEvent.Provenance is { Source: EventSourceKind.Service, SourceId: "cerebral.interface", AgentId: null, JobId: "service" },
                "host service submission preserves its service identity in both actor and provenance");
            var serviceSequence = journal.GetRevision("service");
            var reopenedServiceStore = new WorkflowStore(new EventJournal(database));
            var retriedServiceRun = reopenedServiceStore.Submit(serviceInput);
            var retainedServiceEvent = journal.ReadPage("service").Events.First();
            Check(retriedServiceRun.Id == serviceRun.Id && journal.GetRevision("service") == serviceSequence &&
                retainedServiceEvent.Id == serviceEvent.Id && retainedServiceEvent.Actor == serviceEvent.Actor &&
                retainedServiceEvent.Provenance == serviceEvent.Provenance &&
                retriedServiceRun.Submission.HostAddedRequiredSuccessfulTools.SequenceEqual(["plugin.controls"]),
                "reopened service submission retry preserves completion provenance without replacing its event or appending a message");
            Check(Rejected<WorkflowConflictException>(() => store.Submit(serviceInput with { Caller = new(EventActorKind.Service, "another.service") })),
                "service request identity cannot be reused by a different service caller");
            Check(Rejected<WorkflowConflictException>(() => store.Submit(serviceInput with
                { HostAddedRequiredSuccessfulTools = ["interface.status"] })),
                "service request identity cannot be reused with changed host-added completion provenance");
            store.Cancel(serviceRun.Id);
            Check(Rejected<ArgumentException>(() => store.Submit(Submission("forged-host-requirement") with
                { HostAddedRequiredSuccessfulTools = ["plugin.controls"] })) &&
                store.TryGet("forged-host-requirement") is null,
                "ordinary user workflow submission cannot set service-owned completion provenance");
            foreach (var invalidHostRequirements in new IReadOnlyList<string>[]
            {
                ["plugin.controls", "plugin.controls"],
                Enumerable.Range(0, 9).Select(index => "fixture.tool" + index).ToArray()
            })
                Check(Rejected<ArgumentException>(() => store.Submit(Submission("invalid-host-" + invalidHostRequirements.Count) with
                    {
                        Caller = new(EventActorKind.Service, "cerebral.plugin-tests"),
                        HostAddedRequiredSuccessfulTools = invalidHostRequirements
                    })),
                    "duplicate or oversized service completion provenance is rejected before persistence: " + invalidHostRequirements.Count);
            Check(Rejected<ArgumentException>(() => store.Submit(Submission("system") with { Caller = new(EventActorKind.System, "cerebral.engine") })) &&
                store.TryGet("system") is null && journal.GetRevision("system") == 0,
                "system actor remains outside admitted workflow caller kinds and creates no retained input");
            var activeRetention = journal.PruneRolling("review", new(1, 4 * 1024 * 1024, 1), DateTimeOffset.UtcNow.AddDays(2));
            Check(activeRetention.RemovedEvents == 0 && activeRetention.PinnedRollingEvents > 0 && !activeRetention.QuotaSatisfied &&
                journal.ReadPage("review").Events.Any(x => x.Type == CerebralEventTypes.WorkflowTransition),
                "rolling workflow transitions stay pinned while their run is active and expose unmet retention pressure");
            var running = store.TryClaim(submitted.Id)!;
            Check(running.Status == WorkflowStatus.Running && store.TryClaim(submitted.Id) is null, "only one claim acquires a queued run");
            var questionRun = store.ApplyStep(running.Id, running.Revision, new(WorkflowContract.EmptyObject,
                new AskWorkflowQuestions([new("label", "Review label"), new("style", "Style", Choices: ["concise", "detailed"])])));
            var questions = questionRun.Questions!;
            Check(Rejected<ArgumentException>(() => store.Answer(new("empty-required", questionRun.Id, questions.Id,
                questions.Revision, new Dictionary<string, string>()))) && store.TryGet(questionRun.Id)!.Revision == questionRun.Revision,
                "an empty answer cannot skip required questions or commit a transition");
            var answer = new WorkflowAnswerRequest("answer-label", questionRun.Id, questions.Id, questions.Revision,
                new Dictionary<string, string> { ["label"] = "Introduction" });
            var partial = store.Answer(answer);
            Check(partial.Status == WorkflowStatus.Waiting && partial.Questions!.Answers["label"] == "Introduction", "individual required fields can be answered independently");
            Check(Rejected<WorkflowConflictException>(() => store.Resume(partial.Id, partial.Revision)), "explicit resume cannot skip unanswered required fields");
            Check(Rejected<WorkflowConflictException>(() => store.Answer(answer with { RequestId = "stale" })), "new stale question answer is rejected");
            Check(Rejected<WorkflowConflictException>(() => store.Answer(answer with { RequestId = "foreign-answer", QuestionSetId = "other" })), "foreign question set is rejected");
            Check(Rejected<ArgumentException>(() => store.Answer(new("bad-choice", partial.Id, questions.Id, partial.Questions!.Revision, new Dictionary<string, string> { ["style"] = "invented" }))),
                "question choice values are validated before commit");
            var answered = store.Answer(new("answer-style", partial.Id, questions.Id, partial.Questions!.Revision,
                new Dictionary<string, string> { ["style"] = "concise" }));
            Check(answered.Status == WorkflowStatus.Queued && answered.Signal.Kind == WorkflowSignalKind.Answers && answered.Signal.Data.GetProperty("label").GetString() == "Introduction",
                "required answer completion and flat answer signal are committed together");
            var relayForm = store.ReadCollaborationOwnerAnswer(answered.Id, questions.Id,
                answered.Questions!.Revision);
            Check(relayForm.Answers["label"] == "Introduction" && relayForm.Answers["style"] == "concise" &&
                Rejected<WorkflowConflictException>(() => store.ReadCollaborationOwnerAnswer(answered.Id,
                    "foreign-question", answered.Questions.Revision)),
                "collaboration relay reads only the exact completed saved owner form");
            var beforeReplay = journal.GetRevision("review");
            Check(store.Answer(answer).Revision == partial.Revision && journal.GetRevision("review") == beforeReplay,
                "lost answer response returns saved receipt before checking current question revision");
            Check(Rejected<WorkflowConflictException>(() => store.Answer(answer with { Answers = new Dictionary<string, string> { ["label"] = "changed" } })),
                "answer retry with changed content conflicts");
            var prepared = Prepare(store, answered.Id, descriptor);
            var dispatched = store.DispatchEffect(prepared.Id, prepared.Revision);
            Check(dispatched.Effect!.State == WorkflowEffectState.Dispatched, "dispatch intent is durable before a handler can run");
            var succeeded = new WorkflowCommandResult(WorkflowEffectState.Succeeded, J(new { words = 3 }), ProviderReceiptId: "measure-1");
            var effectDone = store.CompleteEffect(dispatched.Id, dispatched.Effect.Id, succeeded);
            Check(effectDone.Status == WorkflowStatus.Queued && effectDone.Signal.Data.GetProperty("state").GetString() == "Succeeded", "effect receipt and resumable continuation commit together");
            var finalRun = store.TryClaim(effectDone.Id)!;
            finalRun = store.ApplyStep(finalRun.Id, finalRun.Revision, new(WorkflowContract.EmptyObject, new CompleteWorkflow(J(new { words = 3 }))));
            var finalSequence = journal.GetRevision("review");
            Check(store.CompleteEffect(finalRun.Id, dispatched.Effect.Id, succeeded).Status == WorkflowStatus.Completed && journal.GetRevision("review") == finalSequence,
                "late effect retry does not replay or alter a completed run");
            Check(Rejected<WorkflowConflictException>(() => store.CompleteEffect(finalRun.Id, dispatched.Effect.Id, succeeded with { Data = J(new { words = 5 }) })), "confirmed effect receipt cannot change");
            var completedRetention = journal.PruneRolling("review", new(1, 4 * 1024 * 1024, 1), DateTimeOffset.UtcNow.AddDays(2));
            var retainedConversation = journal.ReadPage("review", limit: 200);
            Check(completedRetention.RemovedEvents > 0 && completedRetention.PinnedRollingEvents == 0 && completedRetention.QuotaSatisfied &&
                retainedConversation.RetentionGap is not null &&
                retainedConversation.Events.All(x => x.Type != CerebralEventTypes.WorkflowTransition) &&
                retainedConversation.Events.Any(x => x.Type == CerebralEventTypes.Message) &&
                retainedConversation.Events.Any(x => x.Type == CerebralEventTypes.ToolResult),
                "terminal diagnostic transitions roll while accepted input and required effect receipts remain durable");
            var nextTurn = store.Submit(Submission("review-next", "review"));
            Check(nextTurn.Status == WorkflowStatus.Queued && store.Submit(submission).Status == WorkflowStatus.Completed,
                "completed conversation accepts its next run and prior request retry still returns its original result");
            store.Cancel(nextTurn.Id);

            var optionalSkip = store.Submit(Submission("optional-review"));
            optionalSkip = store.TryClaim(optionalSkip.Id)!;
            optionalSkip = store.ApplyStep(optionalSkip.Id, optionalSkip.Revision, new(WorkflowContract.EmptyObject,
                new AskWorkflowQuestions([new("notes", "Optional notes", Required: false, Choices: ["Brief", "Detailed"])])));
            var skipOptional = new WorkflowAnswerRequest("skip-optional", optionalSkip.Id, optionalSkip.Questions!.Id,
                optionalSkip.Questions.Revision, new Dictionary<string, string>());
            var skipped = store.Answer(skipOptional);
            Check(skipped.Status == WorkflowStatus.Queued && skipped.Questions!.Revision == 2 && skipped.Questions.Answers.Count == 0 &&
                skipped.Signal.Kind == WorkflowSignalKind.Answers && !skipped.Signal.Data.EnumerateObject().Any(),
                "an optional-only form can submit no choices and resumes with an empty answer signal");
            var optionalSequence = journal.GetRevision(optionalSkip.Id);
            Check(new WorkflowStore(new EventJournal(database)).Answer(skipOptional).Revision == skipped.Revision &&
                journal.GetRevision(optionalSkip.Id) == optionalSequence,
                "a skipped optional response survives reopening and exact retries without duplicate effects");
            Check(Rejected<WorkflowConflictException>(() => store.Answer(skipOptional with { RequestId = "stale-optional" })),
                "an empty optional response still enforces the exact waiting question identity");
            store.Cancel(optionalSkip.Id);

            var multiRun = store.TryClaim(store.Submit(Submission("multi-review")).Id)!;
            multiRun = store.ApplyStep(multiRun.Id, multiRun.Revision, new(WorkflowContract.EmptyObject,
                new AskWorkflowQuestions([new("items", "Select plugins to disable", Choices: ["A", "B", "C"]) { Multiple = true, Description = "**Reversible**" },
                    new("notes", "Additional notes", Required: false)])));
            var multiQuestions = multiRun.Questions!;
            foreach (var invalid in new[] { "A", "[]", "[\"A\",\"A\"]", "[\"foreign\"]", "[1]", "[[\"A\"]]" })
                Check(Rejected<ArgumentException>(() => store.Answer(new("bad-multi", multiRun.Id, multiQuestions.Id, 1,
                    new Dictionary<string, string> { ["items"] = invalid }))) &&
                    store.TryGet(multiRun.Id)!.Revision == multiRun.Revision,
                    "invalid multi-answer is rejected atomically: " + invalid);
            var selectedChoices = WorkflowQuestionAnswers.EncodeSelections(multiQuestions.Fields[0], ["C", "A"]);
            var multiRequest = new WorkflowAnswerRequest("multi-answer", multiRun.Id, multiQuestions.Id, 1,
                new Dictionary<string, string> { ["items"] = selectedChoices, ["notes"] = "Preserve files." });
            var multipleCompleted = store.Answer(multiRequest);
            Check(multipleCompleted.Status == WorkflowStatus.Queued && selectedChoices == "[\"A\",\"C\"]" &&
                new WorkflowStore(new EventJournal(database)).TryGet(multiRun.Id)!.Questions!.Fields[0] is { Multiple: true, Description: "**Reversible**" },
                "multiple choices and explanation persist across store reopen and resume together");
            var multiSequence = journal.GetRevision(multiRun.Submission.ConversationId);
            Check(store.Answer(multiRequest).Revision == multipleCompleted.Revision && journal.GetRevision(multiRun.Submission.ConversationId) == multiSequence,
                "multiple answer retries retain exact request identity without duplicate events");
            var answerEvent = journal.ReadPage(multiRun.Submission.ConversationId).Events.Single(item => item.Type == "workflow.answers");
            Check(((StructuredContentPart)answerEvent.Content.Single()).Value.GetProperty("items").GetArrayLength() == 2,
                "retained multi-answer content uses a structured array rather than escaped JSON text");
            store.Cancel(multiRun.Id);

            var compactBase = store.Submit(Submission("compact-base", "compact-chat"));
            compactBase = store.TryClaim(compactBase.Id)!;
            compactBase = store.ApplyStep(compactBase.Id, compactBase.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(J(new { summary = "ready" }))));
            var compactRevision = journal.GetRevision("compact-chat");
            var compactRequest = new ConversationCompactionRequest("compact-request", "test.review:review",
                "compact-chat", compactRevision);
            var compactReceipt = store.RequestConversationCompaction(compactRequest);
            Check(compactReceipt.RequestedAtRevision == compactRevision &&
                store.GetPendingConversationCompaction("compact-chat")?.RequestId == compactRequest.RequestId &&
                journal.ReadPage("compact-chat").Events.Last().Type == CerebralEventTypes.ContextCompactionRequested,
                "owner compaction request is durable service state and activity, not a user message");
            var compactRequestSequence = journal.GetRevision("compact-chat");
            Check(store.RequestConversationCompaction(compactRequest) == compactReceipt &&
                journal.GetRevision("compact-chat") == compactRequestSequence,
                "lost compaction response returns its existing receipt without another event");
            Check(Rejected<WorkflowConflictException>(() => store.RequestConversationCompaction(compactRequest with { RequestId = "compact-other", ExpectedConversationRevision = compactRequestSequence })) &&
                Rejected<JournalRevisionException>(() => store.RequestConversationCompaction(compactRequest with { RequestId = "compact-stale" })),
                "pending and stale compaction requests fail without replacing retained intent");
            var compactNext = store.Submit(Submission("compact-next", "compact-chat") with
                { ConversationCompactionRequestId = compactRequest.RequestId });
            Check(compactNext.Submission.ConversationCompactionRequestId == compactRequest.RequestId &&
                store.GetPendingConversationCompaction("compact-chat") is null &&
                store.Submit(Submission("compact-next", "compact-chat") with
                    { ConversationCompactionRequestId = compactRequest.RequestId }).Id == compactNext.Id,
                "next accepted turn atomically consumes and retains the exact one-time compaction request");
            var compactDescriptor = descriptor with { Id = "cerebral.host:compact" };
            var compactPrepared = Prepare(store, compactNext.Id, compactDescriptor);
            var compactDispatched = store.DispatchEffect(compactPrepared.Id, compactPrepared.Revision);
            var checkpoint = new ContextCheckpoint(compactDispatched.Effect!.Id,
                compactNext.Submission.ConversationSequence, 0, [new("compact-base", 1)],
                "Durable summary of the earlier conversation.", compactDispatched.Effect.Id, true)
                { SourceLocators = [new("compact-base", 1, "provider", "evidence", "exact-base", false)] };
            var compactResult = new WorkflowCommandResult(WorkflowEffectState.Succeeded, J(new { compacted = true }))
                { ConversationCheckpoint = checkpoint };
            var compactEffectDone = store.CompleteEffect(compactDispatched.Id, compactDispatched.Effect.Id, compactResult);
            var savedCheckpoint = store.GetConversationCheckpoint("compact-chat", compactNext.Submission.ConversationSequence);
            var reopenedCheckpoint = new WorkflowStore(new EventJournal(database))
                .GetConversationCheckpoint("compact-chat", compactNext.Submission.ConversationSequence);
            Check(compactEffectDone.Status == WorkflowStatus.Queued &&
                savedCheckpoint is not null && reopenedCheckpoint is not null &&
                savedCheckpoint.SourceRevision == compactNext.Submission.ConversationSequence &&
                savedCheckpoint.Checkpoint.Id == checkpoint.Id && savedCheckpoint.Checkpoint.Summary == checkpoint.Summary &&
                reopenedCheckpoint.Checkpoint.Id == checkpoint.Id && reopenedCheckpoint.Checkpoint.CoveredSources.Single().Id == "compact-base" &&
                reopenedCheckpoint.Checkpoint.SourceLocators.SequenceEqual(checkpoint.SourceLocators),
                "successful compaction receipt atomically adopts a restart-safe conversation checkpoint");
            var exactCheckpoint = journal.ReadExact("compact-chat", checkpoint.Id, journal.GetRevision("compact-chat"));
            Check(exactCheckpoint.Type == "context.compaction" && exactCheckpoint.Sequence > savedCheckpoint!.SourceRevision &&
                exactCheckpoint.Projection.Model == ModelInclusion.OnDemand &&
                exactCheckpoint.Data!.Value.GetProperty("checkpoint").Deserialize<ContextCheckpoint>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                    .SourceLocators.SequenceEqual(checkpoint.SourceLocators),
                "normal adoption atomically saves an exact checkpoint document with its real event sequence and separate coverage cutoff");
            Check(Rejected<KeyNotFoundException>(() => journal.ReadExact("compact-chat", checkpoint.Id, compactNext.Submission.ConversationSequence)) &&
                store.GetLegacyCheckpointSource("compact-chat", checkpoint.Id, compactNext.Submission.ConversationSequence) is null &&
                store.GetLegacyCheckpointSource("other-chat", checkpoint.Id, long.MaxValue) is null,
                "saved checkpoint source cannot bypass the accepted boundary or conversation identity");
            var compactCommittedRevision = journal.GetRevision("compact-chat");
            Check(store.CompleteEffect(compactDispatched.Id, compactDispatched.Effect.Id, compactResult).Revision == compactEffectDone.Revision &&
                journal.GetRevision("compact-chat") == compactCommittedRevision,
                "lost compaction-effect response returns the retained checkpoint without another event");
            Check(Rejected<WorkflowConflictException>(() => store.CompleteEffect(compactDispatched.Id, compactDispatched.Effect.Id,
                compactResult with { ConversationCheckpoint = checkpoint with { ContextRevision = checkpoint.ContextRevision + 1 } })),
                "a confirmed compaction receipt cannot be replaced with a different checkpoint");
            var refinedPrepared = Prepare(store, compactNext.Id, compactDescriptor);
            var refinedDispatched = store.DispatchEffect(refinedPrepared.Id, refinedPrepared.Revision);
            var refinedCheckpoint = checkpoint with { Id = refinedDispatched.Effect!.Id,
                CompactionEffectId = refinedDispatched.Effect.Id, ContextRevision = checkpoint.ContextRevision + 1,
                CoveredSources = [new(checkpoint.Id, checkpoint.ContextRevision), new("current-tool", 1)],
                Summary = "The earlier checkpoint plus newly completed tool evidence.",
                SourceLocators = [new(checkpoint.Id, checkpoint.ContextRevision, "cerebral.engine", "context.checkpoint", checkpoint.Id, true),
                    new("current-tool", 1, "provider", "evidence", "tool-source", true)] };
            var refinedResult = compactResult with { ConversationCheckpoint = refinedCheckpoint };
            var refinedDone = store.CompleteEffect(refinedDispatched.Id, refinedDispatched.Effect.Id, refinedResult);
            var reopenedRefined = new WorkflowStore(new EventJournal(database))
                .GetConversationCheckpoint("compact-chat", compactNext.Submission.ConversationSequence);
            Check(refinedDone.Status == WorkflowStatus.Queued && reopenedRefined is not null && reopenedRefined.Checkpoint.Id == refinedCheckpoint.Id &&
                reopenedRefined.SourceRevision == savedCheckpoint!.SourceRevision &&
                store.GetEffectResult(compactNext.Id, checkpoint.Id).ConversationCheckpoint?.Id == checkpoint.Id,
                "later same-run compaction advances the checkpoint chain at the fixed admission boundary while preserving the original receipt");
            var refinedSequence = journal.GetRevision("compact-chat");
            Check(store.CompleteEffect(compactNext.Id, checkpoint.Id, compactResult).Revision == refinedDone.Revision &&
                store.CompleteEffect(compactNext.Id, refinedCheckpoint.Id, refinedResult).Revision == refinedDone.Revision &&
                journal.GetRevision("compact-chat") == refinedSequence &&
                store.GetConversationCheckpoint("compact-chat", compactNext.Submission.ConversationSequence)?.Checkpoint.Id == refinedCheckpoint.Id,
                "exact retries of both checkpoint effects preserve the latest baseline without replaying or regressing it");
            var nextPrepared = Prepare(store, compactNext.Id, compactDescriptor);
            var nextDispatched = store.DispatchEffect(nextPrepared.Id, nextPrepared.Revision);
            var nextCheckpoint = refinedCheckpoint with { Id = nextDispatched.Effect!.Id,
                CompactionEffectId = nextDispatched.Effect.Id, ContextRevision = refinedCheckpoint.ContextRevision + 1,
                CoveredSources = [new(refinedCheckpoint.Id, refinedCheckpoint.ContextRevision)], Summary = "Next refinement.",
                SourceLocators = [new(refinedCheckpoint.Id, refinedCheckpoint.ContextRevision, "cerebral.engine", "context.checkpoint", refinedCheckpoint.Id, true)] };
            foreach (var invalidCheckpoint in new[]
            {
                nextCheckpoint with { ContextRevision = refinedCheckpoint.ContextRevision },
                nextCheckpoint with { ContextRevision = refinedCheckpoint.ContextRevision - 1 },
                nextCheckpoint with { ExclusionRevision = refinedCheckpoint.ExclusionRevision + 1 },
                nextCheckpoint with { CoveredSources = [new(refinedCheckpoint.Id, refinedCheckpoint.ContextRevision + 1)],
                    SourceLocators = [nextCheckpoint.SourceLocators[0] with { Revision = refinedCheckpoint.ContextRevision + 1 }] }
            })
                Check(Rejected<WorkflowConflictException>(() => store.CompleteEffect(nextDispatched.Id, nextDispatched.Effect.Id,
                    compactResult with { ConversationCheckpoint = invalidCheckpoint })) &&
                    store.Get(nextDispatched.Id).Revision == nextDispatched.Revision &&
                    store.GetConversationCheckpoint("compact-chat", compactNext.Submission.ConversationSequence)?.Checkpoint.Id == refinedCheckpoint.Id,
                    "checkpoint refinement rejects stale context, changed exclusions or a mismatched predecessor without altering durable state");
            var nextDone = store.CompleteEffect(nextDispatched.Id, nextDispatched.Effect.Id,
                compactResult with { ConversationCheckpoint = nextCheckpoint });
            store.Cancel(nextDone.Id);

            store.Submit(Submission("checkpoint-collision", "collision-chat"));
            var collisionPrepared = Prepare(store, "checkpoint-collision", compactDescriptor);
            var collisionDispatched = store.DispatchEffect(collisionPrepared.Id, collisionPrepared.Revision);
            var colliding = checkpoint with { Id = collisionDispatched.Effect!.Id,
                CompactionEffectId = collisionDispatched.Effect.Id,
                ContextRevision = collisionDispatched.Submission.ConversationSequence };
            journal.Append(new(colliding.Id, "collision-chat", collisionDispatched.Id, 0, "fixture.collision",
                new(EventActorKind.Service, "fixture"), [], new(ModelInclusion.OnDemand, HumanVisibility.Activity),
                new(EventSourceKind.Service, "fixture"), DateTimeOffset.UtcNow), journal.GetRevision("collision-chat"));
            var collisionHead = journal.GetRevision("collision-chat");
            Check(Rejected<InvalidOperationException>(() => store.CompleteEffect(collisionDispatched.Id, colliding.Id,
                    compactResult with { ConversationCheckpoint = colliding })) &&
                store.Get(collisionDispatched.Id).Revision == collisionDispatched.Revision &&
                store.GetConversationCheckpoint("collision-chat", long.MaxValue) is null &&
                journal.GetRevision("collision-chat") == collisionHead &&
                Rejected<KeyNotFoundException>(() => store.GetEffectResult(collisionDispatched.Id, colliding.Id)),
                "checkpoint ID collision rolls back the baseline, effect receipt and journal mutation together");

            var collisionFailed = store.CompleteEffect(collisionDispatched.Id, colliding.Id,
                new(WorkflowEffectState.Failed, Error: "Intentional isolated checkpoint ID collision."));
            store.Cancel(collisionFailed.Id);

            store.Submit(Submission("restart-prepared"));
            var recoverPrepared = Prepare(store, "restart-prepared", descriptor);
            store.Submit(Submission("restart-dispatched"));
            var recoverDispatched = Prepare(store, "restart-dispatched", descriptor);
            recoverDispatched = store.DispatchEffect(recoverDispatched.Id, recoverDispatched.Revision);
            store.Submit(Submission("hold-dispatched"));
            var holdDispatched = Prepare(store, "hold-dispatched", descriptor);
            holdDispatched = store.DispatchEffect(holdDispatched.Id, holdDispatched.Revision);
            var held = store.Hold(holdDispatched.Id, holdDispatched.Revision, "The handler stopped before confirming its effect.");
            Check(held is { Status: WorkflowStatus.Waiting, Effect.State: WorkflowEffectState.Unknown },
                "holding an inactive dispatched effect exposes it for reconciliation instead of stranding every recovery control");

            store.Submit(Submission("legacy-waiting-dispatched"));
            var legacyDispatched = Prepare(store, "legacy-waiting-dispatched", descriptor);
            legacyDispatched = store.DispatchEffect(legacyDispatched.Id, legacyDispatched.Revision);
            using (var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_runs SET status='Waiting', payload=json_set(payload,'$.status','Waiting','$.waitingReason','legacy stranded') WHERE id=$id;";
                command.Parameters.AddWithValue("$id", legacyDispatched.Id);
                Check(command.ExecuteNonQuery() == 1, "legacy waiting-dispatched fixture is persisted exactly once");
            }
            var reopened = new WorkflowStore(new EventJournal(database));
            Check(reopened.RecoverInterrupted() == 3 && reopened.Get(recoverPrepared.Id) is { Status: WorkflowStatus.Queued, Effect.State: WorkflowEffectState.Prepared },
                "restart makes undispatched intent runnable without inventing an effect");
            var unknown = reopened.Get(recoverDispatched.Id);
            Check(unknown is { Status: WorkflowStatus.Waiting, Effect.State: WorkflowEffectState.Unknown } && Rejected<WorkflowConflictException>(() => reopened.Resume(unknown.Id, unknown.Revision)),
                "restart holds dispatched work as unknown and refuses blind replay");
            Check(reopened.Get(legacyDispatched.Id) is { Status: WorkflowStatus.Waiting, Effect.State: WorkflowEffectState.Unknown },
                "restart repairs a previously persisted waiting-dispatched run into a reconcilable unknown effect");
            var reconciled = reopened.CompleteEffect(unknown.Id, unknown.Effect!.Id, succeeded, elapsedMilliseconds: 37);
            Check(reconciled.Status == WorkflowStatus.Queued && reconciled.Effect!.ProviderReceiptId == "measure-1" &&
                reconciled.ActiveMilliseconds == 37,
                "confirmed reconciliation restores continuation using the existing effect ID and replaces its timeout reservation with measured work");

            store.Submit(Submission("cancel-prepared"));
            var toCancel = Prepare(store, "cancel-prepared", descriptor);
            var cancelled = store.Cancel(toCancel.Id);
            Check(cancelled is { Status: WorkflowStatus.Cancelled, Effect.State: WorkflowEffectState.Cancelled } &&
                Rejected<WorkflowConflictException>(() => store.DispatchEffect(toCancel.Id, toCancel.Revision)), "cancel wins before dispatch and prevents handler admission");
            store.Submit(Submission("cancel-dispatched"));
            var inFlight = Prepare(store, "cancel-dispatched", descriptor);
            inFlight = store.DispatchEffect(inFlight.Id, inFlight.Revision);
            Check(store.Cancel(inFlight.Id).Status == WorkflowStatus.Cancelling, "cancelling in-flight effect is not falsely reported stopped");
            var shutdownBlockers = store.CountShutdownBlocking();
            var uncertain = store.CompleteEffect(inFlight.Id, inFlight.Effect!.Id, new(WorkflowEffectState.Unknown, Error: "Connection lost."));
            Check(uncertain.Status == WorkflowStatus.Waiting && uncertain.CancelRequested &&
                  store.CountActive() > store.CountShutdownBlocking() && store.CountShutdownBlocking() == shutdownBlockers - 1,
                "uncertain cancellation stays visible for reconciliation without permanently blocking graceful local shutdown");
            var late = store.CompleteEffect(inFlight.Id, inFlight.Effect.Id, succeeded);
            Check(late.Status == WorkflowStatus.Cancelled && late.Effect!.State == WorkflowEffectState.Succeeded, "late confirmed effect is retained but cancelled workflow never advances");

            store.Submit(Submission("atomic"));
            var atomic = store.TryClaim("atomic")!;
            var sequence = journal.GetRevision("atomic");
            Check(Rejected<ArgumentException>(() => store.ApplyStep(atomic.Id, atomic.Revision,
                new(J(new { text = new string('x', WorkflowContract.MaximumStateBytes) }), new CompleteWorkflow(J(new { }))))), "oversized continuation is rejected");
            Check(store.Get(atomic.Id).Revision == atomic.Revision && journal.GetRevision("atomic") == sequence, "rejected transition changes neither run nor canonical event stream");
            var failed = store.ApplyStep(atomic.Id, atomic.Revision, new(WorkflowContract.EmptyObject, new CompleteWorkflow(J(new { }), Success: false, Error: "Review rejected.")));
            Check(failed.Status == WorkflowStatus.Failed && failed.Error == "Review rejected.", "explicit unsuccessful completion reports failed outcome");

            store.Submit(Submission("race"));
            var race = Prepare(store, "race", descriptor);
            await Task.WhenAll(Task.Run(() => { try { new WorkflowStore(new EventJournal(database)).DispatchEffect(race.Id, race.Revision); } catch (WorkflowConflictException) { } }),
                Task.Run(() => new WorkflowStore(new EventJournal(database)).Cancel(race.Id)));
            var raced = store.Get(race.Id);
            Check(raced.CancelRequested && (raced is { Status: WorkflowStatus.Cancelled, Effect.State: WorkflowEffectState.Cancelled } || raced is { Status: WorkflowStatus.Cancelling, Effect.State: WorkflowEffectState.Dispatched }),
                "independent connections serialize cancellation versus dispatch without false stop claims");
            Check(Rejected<ArgumentOutOfRangeException>(() => store.ListReady(201)) && store.TryGet("missing") is null, "run queries expose bounded lists and explicit missing lookup");

            // Force the event insert to fail after the workflow row was written in the same transaction.
            using (var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_test_transition BEFORE INSERT ON conversation_events WHEN NEW.conversation_id='rollback' AND json_extract(NEW.payload,'$.type')='workflow.transition' BEGIN SELECT RAISE(ABORT,'test interruption'); END;";
                command.ExecuteNonQuery();
            }
            Check(Rejected<SqliteException>(() => store.Submit(Submission("rollback"))) && store.TryGet("rollback") is null && journal.GetRevision("rollback") == 0,
                "database failure after run write rolls back run, ownership and all event inserts together");
            using (var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER reject_test_transition;"; command.ExecuteNonQuery();
            }
            Check(store.Submit(Submission("rollback") with { ProfileId = "other" }).Submission.ProfileId == "other", "rolled-back conversation binding does not prevent a valid later admission");
            store.Submit(Submission("optional"));
            var optional = store.TryClaim("optional")!;
            optional = store.ApplyStep(optional.Id, optional.Revision, new(WorkflowContract.EmptyObject,
                new AskWorkflowQuestions([new("note", "Optional note", Required: false)])));
            optional = store.Resume(optional.Id, optional.Revision);
            Check(optional.Status == WorkflowStatus.Queued && optional.Signal.Kind == WorkflowSignalKind.Answers && !optional.Signal.Data.EnumerateObject().Any(),
                "explicit resume past optional questions emits the accepted empty answer set instead of restarting the form");

            var older = Path.Combine(root, "schema1.db");
            using (var connection = new SqliteConnection("Data Source=" + older + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE event_streams(conversation_id TEXT PRIMARY KEY,revision INTEGER NOT NULL); CREATE TABLE conversation_events(conversation_id TEXT NOT NULL,sequence INTEGER NOT NULL,event_id TEXT NOT NULL UNIQUE,payload TEXT NOT NULL,PRIMARY KEY(conversation_id,sequence)); PRAGMA application_id=1346585942; PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
            var upgraded = new WorkflowStore(new EventJournal(older));
            Check(upgraded.Submit(Submission("upgraded")).Status == WorkflowStatus.Queued, "existing schema-one journal upgrades additively for workflow storage");
            var pagedStore = new WorkflowStore(new EventJournal(Path.Combine(root, "pages.db")));
            var largeText = new string('x', 100_000);
            for (var index = 0; index < 12; index++) pagedStore.Submit(Submission("page-" + index) with
            {
                UserText = largeText, AssignmentText = largeText, Input = J(new { text = largeText }), SettingsSnapshot = J(new { text = largeText })
            });
            var firstPage = pagedStore.ListActivePage(3);
            Check(firstPage.Runs.Count == 3 && firstPage.NextAfterRow is not null && pagedStore.ListActivePage(3, firstPage.NextAfterRow.Value).Runs.First().Id == "page-3",
                "active-run count limit returns a usable monotonic row cursor");
            var bytePage = pagedStore.ListActivePage(200);
            var rest = pagedStore.ListActivePage(200, bytePage.NextAfterRow!.Value);
            Check(bytePage.Runs.Count < 12 && bytePage.Runs.Concat(rest.Runs).Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 12).Select(x => "page-" + x)) && rest.NextAfterRow is null,
                "active-run byte limit preserves every remaining run through explicit pagination");
            Check(Rejected<ArgumentOutOfRangeException>(() => pagedStore.ListActivePage(afterRow: -1)), "negative active-run cursor is rejected");
            Console.WriteLine($"Workflow store: {checks} local SQLite integration assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckLocalChildQuestionProxy(string root, Action<bool, string> check)
    {
        static bool Rejected<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }
        var path = Path.Combine(root, "local-child-question.db");
        var workflows = new WorkflowStore(new EventJournal(path));
        static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
        var parent = workflows.Submit(new WorkflowSubmission("local-parent", "local-parent-chat", "lead",
            "test.review:review", "1.0.0", "Coordinate", null, J(new { }), J(new { }),
            Caller: new(EventActorKind.User, "owner")));
        check(parent.LocalChildQuestionRelay is null, "old-shape root has no local question binding");
        var child = workflows.Submit(parent.Submission with
        {
            RequestId = "local-child", ConversationId = "local-child-context", ProfileId = "helper",
            ParentRunId = parent.Id, Caller = new(EventActorKind.Service, "cerebral.coordination")
        });
        var runningChild = workflows.TryClaim(child.Id)!;
        var asking = workflows.ApplyStep(runningChild.Id, runningChild.Revision,
            new(WorkflowContract.EmptyObject, new AskWorkflowQuestions(
                [new("choice", "Choose a plan", true, ["one", "two"])])));
        var runningParent = workflows.TryClaim(parent.Id)!;
        _ = workflows.ApplyStep(runningParent.Id, runningParent.Revision,
            new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns([child.Id])));
        var projected = workflows.ResumeCompletedDependencies().Single();
        var reopened = new WorkflowStore(new EventJournal(path));
        var saved = reopened.Get(parent.Id);
        check(projected.Status == WorkflowStatus.Waiting && saved.Questions?.Fields[0].Id == "choice" &&
            saved.LocalChildQuestionRelay is { ChildRunId: "local-child" } &&
            saved.WaitingForRunIds.SequenceEqual([child.Id]),
            "exact local child question and original barrier survive a fresh store read");
        var bad = new WorkflowAnswerRequest("local-bad-choice", saved.Id, saved.Questions!.Id,
            saved.Questions.Revision, new Dictionary<string, string> { ["choice"] = "invented" });
        check(Rejected<ArgumentException>(() => reopened.Answer(bad)) &&
            reopened.Get(child.Id).Revision == asking.Revision,
            "invalid owner selection never reaches the child");
        var accepted = bad with { RequestId = "local-good-choice",
            Answers = new Dictionary<string, string> { ["choice"] = "one" } };
        var waiting = reopened.Answer(accepted);
        check(waiting.Status == WorkflowStatus.Waiting && waiting.Questions is null &&
            waiting.WaitingForRunIds.SequenceEqual([child.Id]) &&
            reopened.Get(child.Id) is { Status: WorkflowStatus.Queued,
                Signal.Kind: WorkflowSignalKind.Answers },
            "only the exact child queues after the owner answer; parent keeps its barrier");
        check(reopened.Answer(accepted).Revision == waiting.Revision &&
            Rejected<WorkflowConflictException>(() => reopened.Answer(accepted with
                { Answers = new Dictionary<string, string> { ["choice"] = "two" } })),
            "stored answer receipt supports exact retry and rejects changed input");
    }

    private static void CheckOwnedCoordinatorSourceEditions(string root, Action<bool, string> check)
    {
        static bool Rejected<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }
        static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
        static WorkflowSubmission Main(string id, string version) => new(id, "chat-" + id, "lead",
            ConversationWorkflowContract.DefinitionId, version, "Coordinate one synthetic check.",
            null, J(new { }), J(new { }), Caller: new(EventActorKind.User, "owner"));
        static ConversationSummary Summary(WorkflowRun run) => new(run.Submission.ConversationId,
            "Source", run.Submission.ProfileId, run.Submission.ProjectId, run.Id,
            run.Status, DateTimeOffset.UtcNow);
        static ConversationSummary Target(string id) => new(id, "Coordinator", "lead", null,
            "", WorkflowStatus.Queued, DateTimeOffset.UtcNow);
        static WorkflowSubmission Attempt(WorkflowRun source, CollaborationTask task) =>
            source.Submission with
            {
                RequestId = task.AttemptId, ConversationId = task.Request.TargetConversationId,
                DefinitionVersion = ConversationWorkflowContract.DefinitionVersion,
                UserText = task.Request.Instruction, ParentRunId = null,
                Caller = new(EventActorKind.Service, "cerebral.collaboration")
            };

        var journal = new EventJournal(Path.Combine(root, "owned-coordinator-editions.db"));
        var workflows = new WorkflowStore(journal);
        var collaboration = new CollaborationStore(journal);
        var permissions = new ConversationCommunicationStore(journal);
        string[] editions = [ConversationWorkflowContract.DefinitionVersion,
            "1.0.2-generic-context.1", "1.0.3-tool-context.1"];
        for (var index = 0; index < editions.Length; index++)
        {
            var source = workflows.Submit(Main("owned-edition-" + index, editions[index]));
            var target = Target("owned-coordinator-" + index);
            collaboration.RegisterOwnedCoordinator(source.Id, target.Id, target.ProfileId);
            collaboration.RegisterOwnedCoordinator(source.Id, target.Id, target.ProfileId);
            check(workflows.Get(source.Id).Revision == source.Revision &&
                !permissions.Get(source.Submission.ConversationId, target.Id).Allowed,
                "owned registration is idempotent without changing its source or granting a pair permission: " + editions[index]);
            var task = collaboration.Submit(new("owned-request-" + index,
                source.Submission.ConversationId, target.Id, "Perform only this synthetic check.", [], []),
                Summary(source), target, null, source.Id);
            var attempt = workflows.Submit(Attempt(source, task));
            var attached = collaboration.AttachAttempt(task.Id, task.Revision, attempt);
            check(attached.SourceRunId == source.Id && attached.TargetRunId == task.AttemptId &&
                attempt.Id == task.AttemptId && attempt.Submission.ParentRunId is null &&
                attempt.Submission.DefinitionVersion == ConversationWorkflowContract.DefinitionVersion &&
                attempt.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.collaboration" } &&
                !permissions.Get(source.Submission.ConversationId, target.Id).Allowed,
                "qualified Main source edition admits only its exact built-in owned attempt without an independent grant: " + editions[index]);
            check(workflows.Get(source.Id).Submission.DefinitionVersion == editions[index] &&
                collaboration.ListForSource(source.Submission.ConversationId).Tasks.Single().Id == task.Id,
                "owned target admission preserves its source edition and one durable task: " + editions[index]);
        }

        var legacy = workflows.Submit(Main("owned-null-caller", ConversationWorkflowContract.DefinitionVersion)
            with { Caller = null });
        collaboration.RegisterOwnedCoordinator(legacy.Id, "owned-null-caller-target", legacy.Submission.ProfileId);
        check(workflows.Get(legacy.Id).Revision == legacy.Revision,
            "legacy absent-caller owner registration remains supported");

        string[] unsupported = ["1.0.8-owner-verified-prepared.1", "1.0.2-generic-context.2",
            "1.0.2-GENERIC-context.1", "1.0.3-tool-context.1-extra"];
        for (var index = 0; index < unsupported.Length; index++)
        {
            var source = workflows.Submit(Main("owned-rejected-edition-" + index, unsupported[index]));
            check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                    source.Id, "owned-rejected-target-" + index, source.Submission.ProfileId)) &&
                collaboration.ListForSource(source.Submission.ConversationId).Tasks.Count == 0 &&
                workflows.Get(source.Id).Revision == source.Revision,
                "unqualified or nonexact Main edition cannot register owned coordination: " + unsupported[index]);
        }

        var foreignDefinition = workflows.Submit(Main("owned-foreign-definition", editions[1]) with
            { DefinitionId = "test.review:review" });
        check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                foreignDefinition.Id, "owned-foreign-definition-target", foreignDefinition.Submission.ProfileId)),
            "a qualified version string does not authorize a foreign workflow definition");
        foreach (var actor in new[] { new EventActor(EventActorKind.Agent, "lead"),
            new EventActor(EventActorKind.Service, "cerebral.fixture-service") })
        {
            var source = workflows.Submit(Main("owned-caller-" + actor.Kind, editions[1]) with { Caller = actor });
            check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                    source.Id, "owned-caller-target-" + actor.Kind, source.Submission.ProfileId)),
                "an agent or service cannot impersonate an owner Main: " + actor.Kind);
        }

        var parent = workflows.Submit(Main("owned-parent", editions[1]));
        var child = workflows.Submit(Main("owned-child-source", editions[1]) with
            { ParentRunId = parent.Id, Caller = new(EventActorKind.Service, "cerebral.coordination") });
        check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                child.Id, "owned-child-source-target", child.Submission.ProfileId)),
            "an admitted child cannot become an owner Main source");

        var owner = workflows.Submit(Main("owned-profile-owner", editions[1]));
        check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                owner.Id, "owned-profile-target", "helper")) &&
            Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                owner.Id, owner.Submission.ConversationId, owner.Submission.ProfileId)),
            "owned registration retains exact profile and separate-conversation guards");
        var independent = workflows.Submit(Main("owned-independent", editions[0]));
        workflows.Cancel(independent.Id);
        check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                owner.Id, independent.Submission.ConversationId, owner.Submission.ProfileId)),
            "an independently existing conversation cannot be adopted as an owned coordinator");

        var cancelled = workflows.Submit(Main("owned-cancelled-before-register", editions[1]));
        workflows.Cancel(cancelled.Id);
        check(Rejected<UnauthorizedAccessException>(() => collaboration.RegisterOwnedCoordinator(
                cancelled.Id, "owned-cancelled-before-register-target", cancelled.Submission.ProfileId)),
            "a cancelled source cannot register owned coordination");
        var interrupted = workflows.Submit(Main("owned-cancelled-before-attempt", editions[1]));
        var interruptedTarget = Target("owned-cancelled-before-attempt-target");
        collaboration.RegisterOwnedCoordinator(interrupted.Id, interruptedTarget.Id, interruptedTarget.ProfileId);
        var interruptedTask = collaboration.Submit(new("owned-cancelled-request",
            interrupted.Submission.ConversationId, interruptedTarget.Id, "Synthetic cancelled-source check.", [], []),
            Summary(interrupted), interruptedTarget, null, interrupted.Id);
        workflows.Cancel(interrupted.Id);
        check(Rejected<UnauthorizedAccessException>(() => workflows.Submit(Attempt(interrupted, interruptedTask))) &&
            workflows.TryGet(interruptedTask.AttemptId) is null &&
            collaboration.Get(interruptedTask.Id).TargetRunId is null,
            "source cancellation after task persistence still refuses target admission without dispatch");
    }

    private static void CheckCollaborationQuestionWake(string root, Action<bool, string> check)
    {
        var journal = new EventJournal(Path.Combine(root, "collaboration-question.db"));
        var workflows = new WorkflowStore(journal);
        var collaboration = new CollaborationStore(journal);
        var permissions = new ConversationCommunicationStore(journal);
        static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
        static WorkflowSubmission Submission(string id, string chat, string profile,
            EventActor caller) => new(id, chat, profile, "test.review:review", "1.0.0",
                "Inspect the delegated task.", null, J(new { }), J(new { }), Caller: caller);
        var source = workflows.Submit(Submission("relay-source-run", "relay-source-chat", "lead",
            new(EventActorKind.User, "owner")));
        var registration = workflows.Submit(Submission("relay-target-registration", "relay-target-chat", "helper",
            new(EventActorKind.User, "owner")));
        workflows.Cancel(registration.Id);
        _ = permissions.SetAllowed(new(new("relay-source-chat", "relay-target-chat"), 0, true));
        var task = collaboration.Submit(new("relay-task", "relay-source-chat", "relay-target-chat",
                "Find the answer, and ask if the choice is ambiguous.", [], []),
            new("relay-source-chat", "Source", "lead", null, source.Id,
                WorkflowStatus.Running, DateTimeOffset.UtcNow),
            new("relay-target-chat", "Target", "helper", null, registration.Id,
                WorkflowStatus.Cancelled, DateTimeOffset.UtcNow), null, source.Id);
        var target = workflows.Submit(Submission(task.AttemptId, "relay-target-chat", "helper",
            new(EventActorKind.Service, "cerebral.collaboration")));
        task = collaboration.AttachAttempt(task.Id, task.Revision, target);
        var runningTarget = workflows.TryClaim(target.Id)!;
        var waitingTarget = workflows.ApplyStep(runningTarget.Id, runningTarget.Revision,
            new(WorkflowContract.EmptyObject, new AskWorkflowQuestions(
                [new("choice", "Which choice?", true, ["one", "two"])])));
        var runningSource = workflows.TryClaim(source.Id)!;
        _ = workflows.ApplyStep(runningSource.Id, runningSource.Revision,
            new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns([target.Id])));
        var wake = workflows.ResumeCompletedDependencies();
        var delivered = wake.Single(run => run.Id == source.Id);
        var clarification = delivered.Signal.Data.GetProperty("clarification");
        var projected = collaboration.Get(task.Id);
        check(delivered.Status == WorkflowStatus.Queued &&
            delivered.Signal.Kind == WorkflowSignalKind.DependencyResults &&
            clarification.GetProperty("taskId").GetString() == task.Id &&
            clarification.GetProperty("attemptId").GetString() == task.AttemptId &&
            clarification.GetProperty("questionSet").GetProperty("id").GetString() == waitingTarget.Questions!.Id &&
            projected.Questions?.Id == waitingTarget.Questions.Id &&
            projected.TargetRunRevision == waitingTarget.Revision,
            "bound target question wakes only its source run with synchronized exact question identity");
        check(workflows.ResumeCompletedDependencies().Count == 0,
            "the question wake cannot be delivered twice without a new wait barrier");
        var resumedSource = workflows.TryClaim(source.Id)!;
        _ = workflows.ApplyStep(resumedSource.Id, resumedSource.Revision,
            new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns([target.Id])));
        _ = permissions.SetAllowed(new(new("relay-source-chat", "relay-target-chat"), 1, false));
        check(workflows.ResumeCompletedDependencies().Count == 0 &&
            workflows.Get(source.Id).Status == WorkflowStatus.Waiting,
            "revoked pair cannot deliver a pending target question into the source conversation");
    }

    private static WorkflowRun Prepare(WorkflowStore store, string id, WorkflowCommandDescriptor descriptor)
    {
        var run = store.TryClaim(id) ?? throw new Exception("Run was not claimable.");
        return store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
            new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, JsonSerializer.SerializeToElement(new { text = "one two three" }))), descriptor);
    }
}
