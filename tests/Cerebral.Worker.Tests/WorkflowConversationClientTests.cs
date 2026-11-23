using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class WorkflowConversationClientTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new Exception("FAIL: " + label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or InvalidDataException or OperationCanceledException)
            { checks++; return; }
            throw new Exception("FAIL: " + label);
        }

        var script = new Commands();
        var client = new WorkflowConversationClient("conversation", script.Execute);
        var input = new WorkflowConversationSubmission("run", "agent", "example.review:review", "1.0.0", "Review my request.",
            CerebralJson.Element(new { topic = "sample" }), "Private plugin assignment.");
        var accepted = Run(input);
        script.Reply("jobs.workflow.submit", accepted, request =>
        {
            Check(request.ProfileId is null && request.Arguments.GetProperty("requestId").GetString() == "run" &&
                request.Arguments.GetProperty("conversationId").GetString() == "conversation", "submit uses a stable run ID and the owner transport route");
            Check(request.Arguments.GetProperty("assignmentText").GetString() == input.AssignmentText &&
                !request.Arguments.TryGetProperty("settingsSnapshot", out _), "private assignment travels separately and the client never supplies host grants");
        });
        script.Previous([Event(1), Event(2, HumanVisibility.Hidden, ModelInclusion.Required, input.AssignmentText!),
            Event(3, HumanVisibility.Activity, ModelInclusion.Evidence, "Actual tool output")], 3);
        var submitted = await client.SubmitAsync(input);
        Check(submitted.Run?.Id == "run" && submitted.Run.UserText == input.UserText && !submitted.MutationUncertain,
            "accepted run identity is available for reconnect");
        Check(submitted.Events.Count == 3 && submitted.Transcript.Count() == 1 && submitted.Activity.Count() == 1 &&
            EventProjection.IncludeInModel(submitted.Events[1]) && !submitted.Events[1].ExcludedFromContext,
            "human visibility does not erase source events or alter model context policy");
        Check(!JsonSerializer.Serialize(submitted.Run).Contains(input.AssignmentText!, StringComparison.Ordinal),
            "lean run presentation does not expose the private assignment or harness state");
        var callCount = script.Operations.Count;
        Check(client.IsRelevant(new(999, "workflow.updated", "run", CerebralJson.Element(new { status = "Failed" }), DateTimeOffset.UtcNow)) &&
            !client.IsRelevant(new(1000, "workflow.updated", "other", CerebralJson.Element(new { }), DateTimeOffset.UtcNow)) &&
            script.Operations.Count == callCount && client.Snapshot.Run?.Status == WorkflowStatus.Queued,
            "notification payloads only indicate relevance and never mutate state or start background reads");
        await Reject(() => client.SubmitAsync(input with { UserText = "Changed same ID" }), "changed reuse of the current submission ID is rejected locally");
        Check(script.Operations.Count == callCount, "invalid submission retry never reaches transport");

        script.Reply("jobs.workflow.get", accepted with { Revision = 3, Status = WorkflowStatus.Running });
        await client.RefreshAsync(latestEvents: false);
        script.Reply("jobs.workflow.get", accepted with { Revision = 2 });
        await client.RefreshAsync(latestEvents: false);
        Check(client.Snapshot.Run?.Revision == 3 && client.Snapshot.Run.Status == WorkflowStatus.Running && client.Snapshot.Events.Count == 3,
            "a stale read never rolls back current status and refresh can preserve the displayed page");
        script.AssertConsumed();

        var questions = new WorkflowQuestionSet("questions", 2,
            [new("audience", "Audience", Choices: ["General", "Technical"]), new("goal", "Goal")], new Dictionary<string, string>());
        var waiting = accepted with { Status = WorkflowStatus.Waiting, Revision = 4, Questions = questions };
        script.Reply("jobs.workflow.get", waiting); script.Previous([], 0);
        await client.OpenAsync("run");
        callCount = script.Operations.Count;
        await Reject(() => client.AnswerAsync(new("stale", "run", "questions", 1, new Dictionary<string, string> { ["audience"] = "General" })),
            "stale form revision is rejected without replacing it with the latest revision");
        await Reject(() => client.AnswerAsync(new("foreign", "run", "questions", 2, new Dictionary<string, string> { ["unknown"] = "value" })),
            "unknown question fields are rejected before transport");
        await Reject(() => client.AnswerAsync(new("choice", "run", "questions", 2, new Dictionary<string, string> { ["audience"] = "Invented" })),
            "choice answers use the exact offered values");
        Check(script.Operations.Count == callCount, "invalid forms send no command");
        await Reject(() => client.AnswerAsync(new("empty-required", "run", "questions", 2, new Dictionary<string, string>())),
            "empty answers cannot skip required fields or reach transport");
        Check(script.Operations.Count == callCount, "an empty required form sends no command");
        var answer = new WorkflowAnswerRequest("answer-1", "run", "questions", 2,
            new Dictionary<string, string> { ["audience"] = "General" });
        var partial = waiting with
        {
            Revision = 5,
            Questions = questions with { Revision = 3, Answers = new Dictionary<string, string> { ["audience"] = "General" } }
        };
        script.Reply("jobs.workflow.answer", partial, request => Check(request.ProfileId is null &&
            request.Arguments.GetProperty("expectedQuestionRevision").GetInt64() == 2,
            "partial answer carries the owner route and exact displayed form revision"));
        script.Reply("jobs.workflow.get", partial); script.Previous([Event(1)], 1);
        await client.AnswerAsync(answer);
        Check(client.Snapshot.Run?.Questions?.Revision == 3 && client.Snapshot.Run.Questions.Answers["audience"] == "General" &&
            !client.Snapshot.Run.Questions.Answers.ContainsKey("goal"), "partial answer refresh retains the remaining required field");
        var completed = partial with { Revision = 7, Status = WorkflowStatus.Completed, Result = CerebralJson.Element(new { summary = "Reviewed" }) };
        script.Reply("jobs.workflow.get", completed); await client.RefreshAsync(latestEvents: false);
        Check(client.Snapshot.Run?.Questions is null, "question form disappears when the workflow moves past waiting");
        script.Reply("jobs.workflow.answer", partial); script.Reply("jobs.workflow.get", completed); script.Previous([Event(1)], 1);
        await client.AnswerAsync(answer);
        Check(client.Snapshot.Run?.Revision == 7 && client.Snapshot.Run.Questions is null,
            "identical answer retry may receive an old receipt but never reopens a completed form");
        await Reject(() => client.AnswerAsync(answer with { Answers = new Dictionary<string, string> { ["audience"] = "Technical" } }),
            "answer request ID cannot be reused with changed field values");
        script.AssertConsumed();

        var optionalScript = new Commands();
        var optionalClient = new WorkflowConversationClient("conversation", optionalScript.Execute);
        var optionalQuestions = new WorkflowQuestionSet("optional-questions", 1,
            [new("notes", "Optional notes", Required: false, Choices: ["Brief", "Detailed"])], new Dictionary<string, string>());
        var optionalRun = accepted with { Status = WorkflowStatus.Waiting, Questions = optionalQuestions, Revision = 10 };
        optionalScript.Reply("jobs.workflow.get", optionalRun); optionalScript.Previous([Event(1)], 1);
        await optionalClient.OpenAsync("run");
        Check(optionalClient.Snapshot.Run?.Questions is not null, "a fresh optional-only form remains available for explicit submission");
        var skippedOptional = optionalRun with { Revision = 11, Status = WorkflowStatus.Queued,
            Questions = optionalQuestions with { Revision = 2 }, Signal = new(WorkflowSignalKind.Answers, WorkflowContract.EmptyObject) };
        var optionalAnswer = new WorkflowAnswerRequest("skip-optional", "run", optionalQuestions.Id, 1, new Dictionary<string, string>());
        optionalScript.Reply("jobs.workflow.answer", skippedOptional, request => Check(
            !request.Arguments.GetProperty("answers").EnumerateObject().Any(), "optional omission is sent as an empty object, without fabricated choices"));
        optionalScript.Reply("jobs.workflow.get", skippedOptional); optionalScript.Previous([Event(1)], 1);
        await optionalClient.AnswerAsync(optionalAnswer);
        Check(optionalClient.Snapshot.Run is { Status: WorkflowStatus.Queued, Questions: null }, "empty optional submission hides the completed form");
        optionalScript.Reply("jobs.workflow.get", skippedOptional with { Revision = 12, Status = WorkflowStatus.Waiting,
            WaitingReason = "A later effect needs review.", Signal = new(WorkflowSignalKind.EffectResult, WorkflowContract.EmptyObject) });
        await optionalClient.RefreshAsync(latestEvents: false);
        Check(optionalClient.Snapshot.Run?.Questions is null, "a later unrelated wait does not reopen an explicitly skipped optional form");
        optionalScript.AssertConsumed();

        var reopenedScript = new Commands();
        var reopened = new WorkflowConversationClient("conversation", reopenedScript.Execute);
        reopenedScript.Reply("jobs.workflow.get", completed); reopenedScript.Previous([Event(1)], 1);
        await reopened.OpenAsync("run");
        reopenedScript.Reply("jobs.workflow.answer", partial, request => Check(
            request.Arguments.GetProperty("requestId").GetString() == answer.RequestId &&
            request.Arguments.GetProperty("questionSetId").GetString() == answer.QuestionSetId &&
            request.Arguments.GetProperty("expectedQuestionRevision").GetInt64() == answer.ExpectedQuestionRevision,
            "fresh adapter replays the caller's complete immutable answer identity"));
        reopenedScript.Reply("jobs.workflow.get", completed); reopenedScript.Previous([Event(1)], 1);
        await reopened.RetryAnswerAsync(answer);
        Check(reopened.Snapshot.Run is { Status: WorkflowStatus.Completed, Revision: 7, Questions: null },
            "fresh adapter accepts a durable receipt without rolling the completed projection backwards");
        var reopenedCalls = reopenedScript.Operations.Count;
        await Reject(() => reopened.RetryAnswerAsync(answer with
        {
            Answers = new Dictionary<string, string> { ["audience"] = "Technical" }
        }), "fresh adapter rejects changed local reuse of the durable retry ID");
        Check(reopenedScript.Operations.Count == reopenedCalls, "changed durable retry never reaches transport twice");
        reopenedScript.Enqueue("jobs.workflow.answer", _ => CommandResult.Fail("Question set is stale, foreign, cancelled, or already answered."));
        await Reject(() => reopened.RetryAnswerAsync(answer with { RequestId = "new-answer" }),
            "a new request for the completed question reaches the authoritative service and is rejected");
        Check(!reopened.Snapshot.MutationUncertain, "an explicit stale-answer rejection remains confirmed rather than uncertain");
        reopenedScript.AssertConsumed();

        var activeRetryScript = new Commands();
        var activeRetry = new WorkflowConversationClient("conversation", activeRetryScript.Execute);
        activeRetryScript.Reply("jobs.workflow.get", waiting); activeRetryScript.Previous([], 0);
        await activeRetry.OpenAsync("run");
        await Reject(() => activeRetry.RetryAnswerAsync(answer),
            "durable retry path cannot bypass validation while an active form is displayed");
        activeRetryScript.AssertConsumed();

        var uncertainScript = new Commands();
        var uncertain = new WorkflowConversationClient("conversation", uncertainScript.Execute);
        using var cancelledAfterDispatch = new CancellationTokenSource();
        uncertainScript.Enqueue("jobs.workflow.submit", _ =>
        {
            cancelledAfterDispatch.Cancel();
            throw new OperationCanceledException(cancelledAfterDispatch.Token);
        });
        await Reject(() => uncertain.SubmitAsync(input, cancelledAfterDispatch.Token), "cancellation after transport may represent an accepted submission");
        Check(uncertain.Snapshot.MutationUncertain && uncertain.Snapshot.RunId == input.RequestId &&
            uncertain.Snapshot.LastMutationRequestId == input.RequestId, "uncertain submission retains its exact request/run ID");
        await Reject(() => uncertain.SubmitAsync(input with { RequestId = "replacement" }), "different work cannot replace an uncertain submission");
        uncertainScript.Reply("jobs.workflow.get", accepted); uncertainScript.Previous([], 0);
        await uncertain.RefreshAsync();
        Check(!uncertain.Snapshot.MutationUncertain && uncertain.Snapshot.Run?.Id == "run" &&
            uncertainScript.Operations.Count(x => x == "jobs.workflow.submit") == 1,
            "reconnect recovers saved work by query without resubmission");
        uncertainScript.Enqueue("jobs.workflow.cancel", _ => throw new IOException("connection dropped"));
        await Reject(() => uncertain.CancelAsync(), "disconnected cancellation remains uncertain");
        Check(uncertain.Snapshot.MutationUncertain && uncertain.Snapshot.Run?.Status == WorkflowStatus.Queued,
            "uncertain cancel never invents a cancelled outcome");
        var unknown = accepted with
        {
            Revision = 8, Status = WorkflowStatus.Waiting, CancelRequested = true, Questions = questions,
            Effect = new("effect", "example.review:measure", 1, CerebralJson.Element(new { }), WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown)
        };
        uncertainScript.Reply("jobs.workflow.get", unknown); uncertainScript.Previous([], 0);
        await uncertain.RefreshAsync();
        Check(uncertain.Snapshot.Run?.EffectState == WorkflowEffectState.Unknown && uncertain.Snapshot.Run.Questions is null &&
            !uncertain.Snapshot.MutationUncertain && !uncertainScript.Operations.Contains("jobs.workflow.reconcile"),
            "cancelled unknown effect stays visible for explicit reconciliation and cannot display an answer form");
        await Reject(() => uncertain.ResumeAsync(8), "unknown cancelled work cannot be resumed by the adapter");
        uncertainScript.AssertConsumed();

        var recentScript = new Commands();
        var recentWindow = new WorkflowConversationClient("conversation", recentScript.Execute, pageSize: 2);
        recentScript.Reply("jobs.workflow.get", accepted);
        recentScript.Previous([Event(9, HumanVisibility.Activity), Event(10)], 10, 9);
        await recentWindow.OpenAsync("run");
        recentScript.Reply("jobs.workflow.events", new
        { events = new[] { Event(1, text: input.UserText) with { Actor = new(EventActorKind.User) } }, snapshotSequence = 10, nextAfterSequence = (long?)1 });
        recentScript.Previous([Event(7), Event(8, HumanVisibility.Activity)], 10, 7);
        recentScript.Previous([Event(5), Event(6)], 10, 5);
        await recentWindow.ExpandRecentTranscriptAsync(minimumTranscriptEvents: 4, maximumPages: 3);
        Check(recentWindow.Snapshot.Events.Select(item => item.Sequence).SequenceEqual([5L, 6L, 7L, 8L, 9L, 10L]) &&
              recentWindow.Snapshot.Transcript.Count() == 4 && recentWindow.Snapshot.HasOlder && !recentWindow.Snapshot.HasNewer,
            "the recent desktop window joins a bounded number of pinned pages until activity-heavy history contains useful transcript rows");
        Check(recentWindow.Snapshot.OriginalMessage is { Sequence: 1, TurnId: "run" } &&
              recentWindow.Snapshot.OriginalMessage.Content.OfType<TextContentPart>().Single().Text == input.UserText,
            "the exact original message is retained separately without changing the recent page boundary");
        var retainedOriginalCalls = recentScript.Operations.Count;
        await recentWindow.ExpandRecentTranscriptAsync(minimumTranscriptEvents: 4, maximumPages: 3);
        Check(recentScript.Operations.Count == retainedOriginalCalls, "a retained original is not fetched again during settled refresh");
        recentScript.AssertConsumed();

        foreach (var invalidOriginal in new[]
        {
            Event(1, text: input.UserText) with { Actor = new(EventActorKind.User), ConversationId = "foreign" },
            Event(1, text: input.UserText) with { Actor = new(EventActorKind.User), TurnId = "foreign" },
            Event(1, text: input.UserText) with { Actor = new(EventActorKind.Agent) },
            Event(1, text: "changed request") with { Actor = new(EventActorKind.User) },
            Event(1, HumanVisibility.Hidden, text: input.UserText) with { Actor = new(EventActorKind.User) },
            Event(2, text: input.UserText) with { Actor = new(EventActorKind.User) }
        })
        {
            var anchorScript = new Commands();
            var anchorClient = new WorkflowConversationClient("conversation", anchorScript.Execute, pageSize: 2);
            anchorScript.Reply("jobs.workflow.get", accepted);
            anchorScript.Previous([Event(100, HumanVisibility.Activity), Event(101, HumanVisibility.Activity)], 101, 100);
            await anchorClient.OpenAsync("run");
            var beforeAnchor = anchorClient.Snapshot;
            anchorScript.Reply("jobs.workflow.events", new
            { events = new[] { invalidOriginal }, snapshotSequence = 101, nextAfterSequence = (long?)1 },
                request => Check(request.Arguments.GetProperty("afterSequence").GetInt64() == 0 &&
                    request.Arguments.GetProperty("limit").GetInt32() == 1 &&
                    request.Arguments.GetProperty("snapshotSequence").GetInt64() == 101,
                    "original retrieval is one exact forward event pinned to the recent snapshot"));
            await Reject(() => anchorClient.ExpandRecentTranscriptAsync(), "foreign, changed, hidden or displaced original is rejected");
            Check(ReferenceEquals(beforeAnchor, anchorClient.Snapshot) && anchorClient.Snapshot.OriginalMessage is null,
                "invalid original cannot mutate the retained presentation or fabricate a request card");
            anchorScript.AssertConsumed();
        }

        var pages = new Commands(); var browser = new WorkflowConversationClient("conversation", pages.Execute, pageSize: 2);
        pages.Reply("jobs.workflow.get", accepted);
        pages.Previous([Event(9), Event(10)], 10, 9);
        await browser.OpenAsync("run");
        Check(browser.Snapshot.HasOlder && !browser.Snapshot.HasNewer && browser.Snapshot.Events.Count == 2,
            "opening starts at one recent bounded page rather than materializing all history");
        pages.Previous([Event(7), Event(8)], 10, 7, request => Check(
            request.Arguments.GetProperty("beforeSequence").GetInt64() == 9 && request.Arguments.GetProperty("snapshotSequence").GetInt64() == 10 &&
            request.Arguments.GetProperty("limit").GetInt32() == 2, "older-page cursor keeps the original stable snapshot"));
        await browser.LoadOlderAsync();
        Check(browser.Snapshot.Events.Select(x => x.Sequence).SequenceEqual([7L, 8L]) && browser.Snapshot.HasNewer,
            "older navigation replaces the page and stays chronologically ordered");
        pages.Reply("jobs.workflow.events", new { events = new[] { Event(9), Event(10) }, snapshotSequence = 10, nextAfterSequence = (long?)null }, request =>
            Check(request.Arguments.GetProperty("afterSequence").GetInt64() == 8 && request.Arguments.GetProperty("snapshotSequence").GetInt64() == 10,
                "newer-page cursor cannot drift into later appended events"));
        await browser.LoadNewerAsync();
        Check(browser.Snapshot.Events.Count == 2 && !browser.Snapshot.HasNewer, "newer navigation remains a single bounded page");
        var goodPage = browser.Snapshot;
        pages.Previous([Event(7) with { ConversationId = "other-conversation" }, Event(8)], 10, 7);
        await Reject(() => browser.LoadOlderAsync(), "foreign conversation event is rejected");
        Check(ReferenceEquals(goodPage, browser.Snapshot), "invalid page does not partially replace the displayed state");
        pages.Previous([Event(7), Event(8)], 11, 7);
        await Reject(() => browser.LoadOlderAsync(), "server cannot change a pinned history snapshot");
        pages.Previous([Event(7), Event(8)], 10, 6);
        await Reject(() => browser.LoadOlderAsync(), "server continuation must match the returned page boundary");
        pages.Previous([Event(6), Event(7), Event(8)], 10, 6);
        await Reject(() => browser.LoadOlderAsync(), "event count remains limited to the requested page size");
        pages.Reply("jobs.workflow.get", accepted with { Submission = accepted.Submission with { ConversationId = "foreign" } });
        await Reject(() => browser.RefreshAsync(latestEvents: false), "foreign run identity never binds to this conversation");
        pages.Reply("jobs.workflow.get", new string('x', 1024 * 1024 + 1));
        await Reject(() => browser.RefreshAsync(latestEvents: false), "oversized response is rejected before run deserialization or retention");
        Check(ReferenceEquals(goodPage, browser.Snapshot), "rejected run and event responses preserve the complete previous projection");
        pages.AssertConsumed();

        var heldScript = new Commands(); var held = new WorkflowConversationClient("conversation", heldScript.Execute);
        var answeredHold = waiting with
        {
            Revision = 9, WaitingReason = "An admitted package is unavailable.",
            Questions = questions with { Revision = 3, Answers = new Dictionary<string, string> { ["audience"] = "General", ["goal"] = "Review" } }
        };
        heldScript.Reply("jobs.workflow.get", answeredHold); heldScript.Previous([], 0);
        await held.OpenAsync("run");
        Check(held.Snapshot.Run is { Questions: null, Status: WorkflowStatus.Waiting },
            "an already answered question set does not reappear when a later package hold waits");
        await Reject(() => held.ResumeAsync(8), "resume requires the exact currently displayed run revision");
        heldScript.Reply("jobs.workflow.resume", answeredHold with { Revision = 10, Status = WorkflowStatus.Queued }, request =>
            Check(request.Arguments.GetProperty("expectedRevision").GetInt64() == 9, "explicit resume forwards the owner's exact revision"));
        heldScript.Previous([], 0); await held.ResumeAsync(9);
        Check(held.Snapshot.Run?.Status == WorkflowStatus.Queued && held.Snapshot.Run.Questions is null,
            "explicit service resume refreshes status without creating another scheduler");
        heldScript.AssertConsumed();

        var rejectionScript = new Commands(); var rejection = new WorkflowConversationClient("conversation", rejectionScript.Execute);
        rejectionScript.Enqueue("jobs.workflow.submit", _ => CommandResult.Fail("Profile lacks required capability."));
        await Reject(() => rejection.SubmitAsync(input), "service authority rejection is propagated");
        Check(!rejection.Snapshot.MutationUncertain && rejection.Snapshot.Run is null,
            "an explicit service rejection is not displayed as accepted work");
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        await Reject(() => rejection.SubmitAsync(input, preCancelled.Token), "pre-dispatch cancellation stops before transport");
        Check(rejectionScript.Operations.Count == 1, "cancelled-before-dispatch operation is never sent");
        rejectionScript.AssertConsumed();

        var metadata = new Dictionary<string, string> { ["label"] = "original" };
        var image = new ImageContentPart(new("image", 1, "image/png", "view.png", 32, metadata));
        var parts = new List<ContentPart> { image };
        var imageInput = input with { RequestId = "image-run", AcceptedContent = parts, UserText = "" };
        var exactImageInput = WorkflowConversationClient.FreezeSubmission(imageInput);
        var imageRun = Run(exactImageInput);
        var oldRun = Run(input with { RequestId = "old" }) with { Status = WorkflowStatus.Completed };
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageCommands = 0;
        var corruptReceipt = false;
        async Task<CommandResult> ImageCommands(CommandRequest request, CancellationToken token)
        {
            if (request.Operation == "jobs.workflow.get")
            {
                readEntered.TrySetResult(); await readRelease.Task.WaitAsync(token);
                return CommandResult.Ok(oldRun);
            }
            if (request.Operation == "jobs.workflow.events.previous")
                return CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0, nextBeforeSequence = (long?)null });
            if (request.Operation == "jobs.workflow.submit")
            {
                imageCommands++;
                var sent = request.Arguments.GetProperty("acceptedContent").Deserialize<ContentPart[]>(CerebralJson.Options)!;
                Check(sent.OfType<ImageContentPart>().Single().Attachment.Metadata!["label"] == "original" &&
                    request.Arguments.GetProperty("userText").GetString() == "", "client sends frozen typed content and exact empty text");
                return CommandResult.Ok(corruptReceipt ? imageRun with { Submission = imageRun.Submission with { AcceptedContent = [] } } : imageRun);
            }
            throw new InvalidOperationException("Unexpected image client operation.");
        }
        var imageClient = new WorkflowConversationClient("conversation", ImageCommands);
        var opening = imageClient.OpenAsync("old");
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedImage = imageClient.SubmitAsync(imageInput);
        metadata["label"] = "mutated"; parts.Clear(); readRelease.TrySetResult();
        await opening; await queuedImage;
        Check(imageCommands == 1 && imageClient.Snapshot.Run?.Id == "image-run",
            "queued submission freezes references before waiting for an earlier serialized action");
        await Reject(() => imageClient.SubmitAsync(imageInput), "changed reference collection cannot alter a retained retry");
        Check(imageCommands == 1, "changed typed retry is rejected before transport");
        await imageClient.SubmitAsync(exactImageInput);
        Check(imageCommands == 2, "identical typed retry retains its original request identity");
        corruptReceipt = true;
        await Reject(() => imageClient.SubmitAsync(exactImageInput), "service receipt cannot silently omit accepted content");
        Check(imageClient.Snapshot.MutationUncertain, "mismatched accepted-content receipt remains explicitly uncertain");

        checks += await WorkflowConversationHeadTests.RunAsync();
        Console.WriteLine($"Workflow conversation client: {checks} deterministic adapter assertions passed for IDs, questions, cancellation uncertainty, bounded history and display projection. No HTTP, pipes, models or service execution.");
    }

    private static WorkflowRun Run(WorkflowConversationSubmission input) => new()
    {
        Id = input.RequestId,
        Submission = new(input.RequestId, "conversation", input.ProfileId, input.DefinitionId, input.DefinitionVersion,
            input.UserText, input.AssignmentText, input.Input, CerebralJson.Element(new { capabilities = new[] { "fixture" } }), input.StepLimit)
            { AcceptedContent = WorkflowAcceptedContent.Freeze(input.AcceptedContent) },
        Revision = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CerebralEvent Event(long sequence, HumanVisibility human = HumanVisibility.Transcript,
        ModelInclusion model = ModelInclusion.Recent, string text = "Visible message") =>
        new("event-" + sequence, "conversation", "run", sequence, CerebralEventTypes.Message,
            new(EventActorKind.User, "owner", "You"), [new TextContentPart(text)], new(model, human),
            new(EventSourceKind.User), DateTimeOffset.UtcNow);

    private sealed class Commands
    {
        private readonly Queue<(string Operation, Func<CommandRequest, CommandResult> Response)> planned = new();
        public List<string> Operations { get; } = [];
        public void Enqueue(string operation, Func<CommandRequest, CommandResult> response) => planned.Enqueue((operation, response));
        public void Reply(string operation, object response, Action<CommandRequest>? inspect = null) =>
            Enqueue(operation, request => { inspect?.Invoke(request); return CommandResult.Ok(response); });
        public void Previous(IReadOnlyList<CerebralEvent> events, long snapshot, long? next = null, Action<CommandRequest>? inspect = null) =>
            Reply("jobs.workflow.events.previous", new { events, snapshotSequence = snapshot, nextBeforeSequence = next }, inspect);
        public Task<CommandResult> Execute(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Operations.Add(request.Operation);
            if (planned.Count == 0) throw new Exception("Unexpected adapter command: " + request.Operation);
            var next = planned.Dequeue();
            if (next.Operation != request.Operation) throw new Exception($"Expected {next.Operation}, got {request.Operation}.");
            return Task.FromResult(next.Response(request));
        }
        public void AssertConsumed()
        { if (planned.Count != 0) throw new Exception("Adapter did not issue all expected commands."); }
    }
}
