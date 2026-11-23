using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

namespace Cerebral.Engine.Tests;

/// <summary>Local SQLite and controlled handlers; no model, process, service or network execution.</summary>
public static class WorkflowSteeringTests
{
    private const string DefinitionId = "fixture.steering:conversation";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Schema(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public static void Run() => RunCoreAsync().GetAwaiter().GetResult();

    private static async Task RunCoreAsync()
    {
        var checks = new Assertions();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-steering-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "engine.db");
            var journal = new EventJournal(path);
            var store = new WorkflowStore(journal);
            DurableAdmission(store, journal, path, checks);
            Bounds(store, journal, checks);
            await WaitingAndBudgetsAsync(store, checks);
            await AdvanceRaceAsync(store, checks);
            await PreparedRaceAsync(store, checks);
            await DispatchedRaceAsync(store, checks, "generate", succeeded: true);
            await DispatchedRaceAsync(store, checks, "mutate", succeeded: false);
            await TerminalTransitionRacesAsync(store, checks);
            Console.WriteLine($"Workflow steering: {checks.Count} deterministic fixture / local SQLite assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WorkflowSubmission Submission(string id) => new(id, "conversation-" + id, "lead",
        DefinitionId, "1.0.0", "Original exact request.", "PRIVATE-ORIGINAL-ASSIGNMENT",
        Data(new { }), Data(new { acceptedSetting = "fixed" }), StepLimit: 12)
    {
        ActiveTimeLimitSeconds = 30,
        PolicyStepLimit = 10,
        FailureLimits = new Dictionary<string, int> { ["execution"] = 1 }
    };

    private static void DurableAdmission(WorkflowStore store, EventJournal journal, string path, Assertions check)
    {
        var original = store.Submit(Submission("durable"));
        var request = new WorkflowSteeringRequest("steer-first", original.Id, "Use the blue theme.\nKeep the layout.",
            "PRIVATE-STEERING-ASSIGNMENT");
        var receipt = store.Steer(request);
        var accepted = store.Get(original.Id);
        check.That(receipt.RunId == original.Id && receipt.RequestId == request.RequestId &&
            receipt.Sequence == 1 && receipt.AcceptedAt != default &&
            accepted.Steering.Single().Text == request.Text,
            "steering records exact owner input with a stable ordered receipt");
        check.That(accepted.Status == WorkflowStatus.Queued && accepted.Steps == original.Steps &&
            Serialize(accepted.Submission) == Serialize(original.Submission) && SameExecutionState(original, accepted),
            "steering preserves accepted settings, original prompt, private assignment and execution state");
        var revision = accepted.Revision;
        var eventSnapshot = Serialize(journal.ReadPage(original.Submission.ConversationId));
        check.That(store.Steer(request) == receipt && store.Get(original.Id).Revision == revision &&
            Serialize(journal.ReadPage(original.Submission.ConversationId)) == eventSnapshot,
            "lost-response retry returns the original receipt without duplicate events or revision");
        check.Reject<WorkflowConflictException>(() => store.Steer(request with { Text = "Different text." }),
            "same steering request ID with changed visible text conflicts");
        check.Reject<WorkflowConflictException>(() => store.Steer(request with { AssignmentText = "Different assignment." }),
            "same steering request ID with changed hidden assignment conflicts");
        check.That(store.Get(original.Id).Revision == revision &&
            Serialize(journal.ReadPage(original.Submission.ConversationId)) == eventSnapshot,
            "conflicting steering leaves both durable state and events unchanged");

        var reopenedJournal = new EventJournal(path);
        var reopened = new WorkflowStore(reopenedJournal);
        check.That(reopened.Steer(request) == receipt &&
            Serialize(reopened.Get(original.Id).Steering) == Serialize(accepted.Steering),
            "reopening SQLite preserves exact steering and request-id retry receipts");
        var events = reopenedJournal.ReadPage(original.Submission.ConversationId).Events;
        var visible = events.Single(item => item.Type == CerebralEventTypes.Message &&
            item.Content.OfType<TextContentPart>().Any(part => part.Text == request.Text));
        var hidden = events.Single(item => item.Type == "workflow.steering.assignment");
        check.That(visible.Actor.Kind == EventActorKind.User && EventProjection.ShowInTranscript(visible) &&
            visible.Projection.Model == ModelInclusion.Required &&
            visible.Data?.GetProperty("steeringRequestId").GetString() == request.RequestId,
            "visible steering remains a required user event with its request identity");
        check.That(hidden.Projection is { Model: ModelInclusion.Required, Human: HumanVisibility.Hidden } &&
            hidden.Content.OfType<TextContentPart>().Single().Text == request.AssignmentText &&
            events.Single(item => item.Type == "workflow.assignment").Projection.Human == HumanVisibility.Hidden &&
            !Serialize(events.Where(EventProjection.IsVisibleToHuman).ToArray()).Contains("PRIVATE-", StringComparison.Ordinal),
            "hidden steering and original assignments remain model context without appearing in human projections");

        var running = reopened.TryClaim(original.Id)!;
        var completed = reopened.ApplyStep(running.Id, running.Revision,
            new(Data(new { }), new CompleteWorkflow(Data(new { summary = "done" }))));
        check.Reject<WorkflowConflictException>(() => reopened.Steer(new("late-steer", completed.Id, "Too late.")),
            "completed workflows reject new steering");
        check.That(reopened.Steer(request) == receipt && reopened.Get(completed.Id).Revision == completed.Revision,
            "a successful steering retry remains idempotent after completion");
        var cancelled = reopened.Submit(Submission("cancelled"));
        reopened.Cancel(cancelled.Id);
        check.Reject<WorkflowConflictException>(() => reopened.Steer(new("cancelled-steer", cancelled.Id, "Too late.")),
            "cancelled workflows reject new steering");
        var failed = reopened.Submit(Submission("already-failed"));
        failed = reopened.TryClaim(failed.Id)!;
        reopened.Fail(failed.Id, failed.Revision, "Fixture failure.");
        check.Reject<WorkflowConflictException>(() => reopened.Steer(new("failed-steer", failed.Id, "Too late.")),
            "failed workflows reject new steering");
    }

    private static void Bounds(WorkflowStore store, EventJournal journal, Assertions check)
    {
        var run = store.Submit(Submission("invalid"));
        var priorRun = Serialize(store.Get(run.Id));
        var priorEvents = Serialize(journal.ReadPage(run.Submission.ConversationId));
        foreach (var text in new[] { "", " \t\r\n", "bad\0input", "\ud800", new string('x', 8193),
            string.Concat(Enumerable.Repeat("🚀", 2048)) + "x" })
            check.Reject<ArgumentException>(() => store.Steer(new("invalid-text", run.Id, text)),
                "blank, NUL, malformed Unicode or over-limit visible steering is rejected");
        foreach (var assignment in new[] { "bad\0assignment", "\udc00", new string('x', 8193) })
            check.Reject<ArgumentException>(() => store.Steer(new("invalid-assignment", run.Id, "Visible text.", assignment)),
                "invalid hidden steering assignment is rejected before persistence");
        check.Reject<ArgumentException>(() => store.Steer(new("bad request id", run.Id, "Visible text.")),
            "steering request IDs obey bounded identifier validation");
        check.That(Serialize(store.Get(run.Id)) == priorRun &&
            Serialize(journal.ReadPage(run.Submission.ConversationId)) == priorEvents,
            "invalid steering changes neither run state nor canonical event history");
        var unicode = string.Concat(Enumerable.Repeat("🚀", 2048));
        store.Steer(new("unicode-limit", run.Id, unicode));
        check.That(store.Get(run.Id).Steering.Single().Text == unicode,
            "the exact 8192-byte UTF-8 boundary preserves supplementary Unicode characters");

        var count = store.Submit(Submission("count-bound"));
        for (var index = 1; index <= 32; index++)
            store.Steer(new("count-" + index, count.Id, "Steer " + index));
        check.That(store.Get(count.Id).Steering.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, 32)),
            "32 steering entries retain strictly increasing durable sequence numbers");
        check.Reject<WorkflowConflictException>(() => store.Steer(new("count-33", count.Id, "Overflow.")),
            "a 33rd steering entry is refused without silently discarding earlier instructions");
        check.That(store.Get(count.Id).Steering.Count == 32 &&
            store.Steer(new("count-1", count.Id, "Steer 1")).Sequence == 1,
            "entry-count exhaustion still permits an exact lost-response retry");

        var bytes = store.Submit(Submission("byte-bound"));
        for (var index = 0; index < 4; index++)
            store.Steer(new("bytes-" + index, bytes.Id, new string('x', 8192), new string('y', 8192)));
        var beforeOverflow = store.Get(bytes.Id);
        check.Reject<ArgumentException>(() => store.Steer(new("byte-overflow", bytes.Id, "x")),
            "combined visible and hidden steering has a 64 KiB cumulative bound");
        check.That(store.Get(bytes.Id).Revision == beforeOverflow.Revision && beforeOverflow.Steering.Count == 4,
            "cumulative byte overflow does not increment a revision or drop earlier steering");
    }

    private static async Task WaitingAndBudgetsAsync(WorkflowStore store, Assertions check)
    {
        var questionDefinition = new QuestionDefinition();
        var registry = new WorkflowRegistry([new FixtureModule(questionDefinition, [])]);
        var runtime = new WorkflowRuntime(store, registry);
        var waiting = await runtime.RunAsync(store.Submit(Submission("questions")).Id);
        var partial = store.Answer(new("partial-answer", waiting.Id, waiting.Questions!.Id, waiting.Questions.Revision,
            new Dictionary<string, string> { ["first"] = "One" }));
        store.Steer(new("question-steer", partial.Id, "Use concise wording.", "PRIVATE-QUESTION-GUIDANCE"));
        var steered = store.Get(partial.Id);
        check.That(steered.Status == WorkflowStatus.Waiting && Serialize(steered.Questions) == Serialize(partial.Questions) &&
            Serialize(steered.Signal) == Serialize(partial.Signal) && steered.Steps == partial.Steps &&
            steered.WaitingReason == partial.WaitingReason,
            "steering a pending form preserves answers, question revision and the waiting state");
        var stayedWaiting = await runtime.RunAsync(steered.Id);
        check.That(stayedWaiting.Revision == steered.Revision && questionDefinition.Advances == 1,
            "steering cannot bypass required questions or implicitly resume a waiting workflow");
        store.Answer(new("remaining-answer", steered.Id, steered.Questions!.Id, steered.Questions.Revision,
            new Dictionary<string, string> { ["second"] = "Two" }));
        var answered = await runtime.RunAsync(steered.Id);
        check.That(answered.Status == WorkflowStatus.Completed &&
            answered.Result?.GetProperty("steeringCount").GetInt32() == 1 &&
            answered.Result?.GetProperty("first").GetString() == "One" &&
            answered.Result?.GetProperty("second").GetString() == "Two",
            "the next authorized transition receives both saved steering and completed form answers");

        var handler = new ProbeHandler("effect") { FailureBudgetKey = "execution" };
        var prepared = Prepare(store, "failed-budget", handler.Descriptor);
        var dispatched = store.DispatchEffect(prepared.Id, prepared.Revision, 500, "execution");
        var failure = new WorkflowCommandResult(WorkflowEffectState.Failed, Data(new { marker = "actual-failure" }),
            "Actual fixture operation failed.", "failure-receipt");
        var failed = store.CompleteEffect(dispatched.Id, dispatched.Effect!.Id, failure, elapsedMilliseconds: 123);
        store.Steer(new("budget-steer", failed.Id, "Try another approach."));
        var after = store.Get(failed.Id);
        check.That(SameExecutionState(failed, after) && after.ActiveMilliseconds == 123 &&
            after.FailedEffects.GetValueOrDefault("execution") == 1 &&
            Serialize(after.Submission) == Serialize(failed.Submission),
            "steering preserves measured time, failed-attempt counters, step counts and admitted limits");
        check.That(Serialize(store.GetEffectResult(after.Id, dispatched.Effect.Id)) == Serialize(failure),
            "steering retains the exact failed operation receipt rather than replacing it with owner text");
        after = store.TryClaim(after.Id)!;
        after = PrepareExisting(store, after, handler.Descriptor);
        check.Reject<WorkflowConflictException>(() => store.DispatchEffect(after.Id, after.Revision, 100, "execution"),
            "steering does not replenish an exhausted failed-attempt budget");

        var timed = Prepare(store, "time-budget", handler.Descriptor);
        timed = store.DispatchEffect(timed.Id, timed.Revision, 500);
        timed = store.CompleteEffect(timed.Id, timed.Effect!.Id,
            new(WorkflowEffectState.Succeeded, Data(new { marker = "long-operation" })), 30_000);
        store.Steer(new("time-steer", timed.Id, "Continue."));
        timed = store.TryClaim(timed.Id)!;
        timed = PrepareExisting(store, timed, handler.Descriptor);
        check.Reject<WorkflowConflictException>(() => store.DispatchEffect(timed.Id, timed.Revision, 1),
            "steering cannot replenish an exhausted active-time budget");

        var stepLimited = store.Submit(Submission("step-budget") with { StepLimit = 1 });
        stepLimited = store.TryClaim(stepLimited.Id)!;
        stepLimited = store.ApplyStep(stepLimited.Id, stepLimited.Revision,
            new(Data(new { }), new AskWorkflowQuestions([new("required", "Required answer")])));
        store.Steer(new("step-steer", stepLimited.Id, "Continue."));
        store.Answer(new("step-answer", stepLimited.Id, stepLimited.Questions!.Id, stepLimited.Questions.Revision,
            new Dictionary<string, string> { ["required"] = "Answered" }));
        var exhausted = await runtime.RunAsync(stepLimited.Id);
        check.That(exhausted.Status == WorkflowStatus.Failed && exhausted.Steps == 1 && questionDefinition.Advances == 2,
            "steering cannot replenish the pure-transition step budget");

        var unknown = Prepare(store, "unknown", handler.Descriptor);
        unknown = store.DispatchEffect(unknown.Id, unknown.Revision, 500, "execution");
        unknown = store.CompleteEffect(unknown.Id, unknown.Effect!.Id,
            new(WorkflowEffectState.Unknown, Error: "Outcome not known."), elapsedMilliseconds: 1);
        store.Steer(new("unknown-steer", unknown.Id, "Keep the existing result."));
        var unknownSteered = store.Get(unknown.Id);
        var unknownRuntime = new WorkflowRuntime(store, new WorkflowRegistry([
            new FixtureModule(new EffectDefinition(handler.Descriptor.Id), [handler])]));
        var held = await unknownRuntime.RunAsync(unknown.Id);
        check.That(held.Status == WorkflowStatus.Waiting && held.Effect?.State == WorkflowEffectState.Unknown &&
            SameExecutionState(unknown, unknownSteered) && held.ActiveMilliseconds == 500 && handler.Executions == 0,
            "steering preserves an uncertain effect and its time reservation without replay or implicit reconciliation");

        var cancelling = Prepare(store, "cancelling", handler.Descriptor);
        cancelling = store.DispatchEffect(cancelling.Id, cancelling.Revision, 500);
        cancelling = store.Cancel(cancelling.Id);
        check.That(cancelling.Status == WorkflowStatus.Cancelling && cancelling.CancelRequested,
            "fixture cancellation remains pending while an operation is dispatched");
        check.Reject<WorkflowConflictException>(() => store.Steer(new("cancelling-steer", cancelling.Id, "Continue anyway.")),
            "steering cannot reverse pending cancellation or append new instructions to stopping work");
    }

    private static async Task AdvanceRaceAsync(WorkflowStore store, Assertions check)
    {
        var handler = new ProbeHandler("advance");
        var definition = new EffectDefinition(handler.Descriptor.Id);
        definition.DuringAdvance = snapshot =>
        {
            if (definition.Advances == 1)
                store.Steer(new("advance-race-steer", snapshot.Id, "Arrived during pure Advance."));
        };
        var registry = new WorkflowRegistry([new FixtureModule(definition, [handler])]);
        var run = await new WorkflowRuntime(store, registry).RunAsync(store.Submit(Submission("advance-race")).Id);
        check.That(run.Status == WorkflowStatus.Completed && run.Steps == 2 && definition.Advances == 3,
            "a stale pure transition retries against steering without charging a duplicate step");
        check.That(handler.Executions == 1 && handler.LastArguments?.GetProperty("steeringSeen").GetInt32() == 1 &&
            run.Result?.GetProperty("steeringCount").GetInt32() == 1,
            "the retried transition dispatches once using the newly accepted steering");
    }

    private static async Task PreparedRaceAsync(WorkflowStore store, Assertions check)
    {
        var handler = new ProbeHandler("prepared");
        var definition = new EffectDefinition(handler.Descriptor.Id);
        string? preparedId = null;
        handler.DuringAdmission = context =>
        {
            if (handler.Admissions == 1)
            {
                preparedId = store.Get(context.RunId!).Effect!.Id;
                store.Steer(new("prepared-race-steer", context.RunId!, "Arrived before dispatch."));
            }
        };
        var registry = new WorkflowRegistry([new FixtureModule(definition, [handler])]);
        var run = await new WorkflowRuntime(store, registry).RunAsync(store.Submit(Submission("prepared-race")).Id);
        check.That(run.Status == WorkflowStatus.Completed && run.Steps == 2 && definition.Advances == 2 &&
            handler.Admissions == 2 && handler.Executions == 1 && handler.LastEffectId == preparedId,
            "steering before dispatch retries admission of the same prepared effect without replaying Advance");
        check.That(handler.LastArguments?.GetProperty("steeringSeen").GetInt32() == 0 &&
            run.Result?.GetProperty("steeringCount").GetInt32() == 1,
            "already prepared arguments remain immutable and later continuation sees the accepted steering");
    }

    private static async Task DispatchedRaceAsync(WorkflowStore store, Assertions check, string command, bool succeeded)
    {
        var handler = new ControlledHandler(command) { FailureBudgetKey = "execution" };
        var definition = new EffectDefinition(handler.Descriptor.Id);
        var registry = new WorkflowRegistry([new FixtureModule(definition, [handler])]);
        var runtime = new WorkflowRuntime(store, registry);
        var run = store.Submit(Submission("dispatched-" + command));
        var execution = runtime.RunAsync(run.Id);
        var result = new WorkflowCommandResult(succeeded ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
            Data(new { marker = "real-" + command + "-result" }), succeeded ? null : "Confirmed operation failure.",
            "provider-" + command);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = store.Get(run.Id);
            store.Steer(new("dispatched-" + command + "-steer", run.Id, "Arrived while " + command + " was executing.",
                "PRIVATE-DISPATCHED-GUIDANCE"));
            var during = store.Get(run.Id);
            check.That(before.Effect?.State == WorkflowEffectState.Dispatched &&
                SameExecutionState(before, during) && during.Status == WorkflowStatus.Running &&
                during.ActiveMilliseconds == before.ActiveMilliseconds &&
                during.Effect?.ReservedExecutionMilliseconds == before.Effect!.ReservedExecutionMilliseconds,
                command + " steering preserves the active claim, effect identity and reserved execution budget");
            var competing = await runtime.RunAsync(run.Id);
            check.That(competing.Status == WorkflowStatus.Running && handler.Executions == 1,
                command + " steering cannot give a second runtime ownership of an active operation");
            handler.Release.TrySetResult(result);
            var finished = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            check.That(finished.Status == (succeeded ? WorkflowStatus.Completed : WorkflowStatus.Failed) &&
                finished.Steering.Count == 1 && finished.Result?.GetProperty("marker").GetString() == "real-" + command + "-result" &&
                finished.Result?.GetProperty("steeringCount").GetInt32() == 1,
                command + " completion preserves the actual returned evidence and the concurrent steering");
            check.That(Serialize(store.GetEffectResult(run.Id, handler.LastEffectId!)) == Serialize(result) &&
                finished.FailedEffects.GetValueOrDefault("execution") == (succeeded ? 0 : 1),
                command + " retains the exact provider receipt and truthful failure accounting");
            check.That((await runtime.RunAsync(run.Id)).Revision == finished.Revision && handler.Executions == 1 &&
                definition.Advances == 2,
                command + " completion and later refresh never execute the operation twice");
        }
        finally
        {
            handler.Release.TrySetResult(result);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task TerminalTransitionRacesAsync(WorkflowStore store, Assertions check)
    {
        var handler = new ProbeHandler("denied");
        var definition = new EffectDefinition(handler.Descriptor.Id);
        var registry = new WorkflowRegistry([new FixtureModule(definition, [handler])]);
        var denialRun = store.Submit(Submission("denial-race"));
        var granted = false;
        var denialRuntime = new WorkflowRuntime(store, registry, (submission, _) =>
        {
            if (!granted)
            {
                granted = true;
                store.Steer(new("denial-race-steer", submission.RequestId, "Arrived during capability denial."));
            }
            return false;
        });
        var denied = await denialRuntime.RunAsync(denialRun.Id);
        check.That(denied.Status == WorkflowStatus.Waiting && denied.Steering.Count == 1 &&
            denied.Steps == 0 && handler.Executions == 0,
            "a steering revision race during capability denial still holds work instead of stranding Running");

        var failureDefinition = new EffectDefinition(handler.Descriptor.Id);
        failureDefinition.DuringAdvance = snapshot =>
        {
            store.Steer(new("exception-race-steer", snapshot.Id, "Arrived before the pure transition failed."));
            throw new InvalidOperationException("Intentional pure transition failure.");
        };
        var failureRegistry = new WorkflowRegistry([new FixtureModule(failureDefinition, [handler])]);
        var failed = await new WorkflowRuntime(store, failureRegistry).RunAsync(store.Submit(Submission("exception-race")).Id);
        check.That(failed.Status == WorkflowStatus.Failed && failed.Steering.Count == 1 && failed.Steps == 0 &&
            failed.Error == "Intentional pure transition failure." && handler.Executions == 0,
            "a steering revision race during a pure transition exception preserves failure and releases the claim");
    }

    private static WorkflowRun Prepare(WorkflowStore store, string id, WorkflowCommandDescriptor descriptor)
    {
        var run = store.Submit(Submission(id));
        return PrepareExisting(store, store.TryClaim(run.Id)!, descriptor);
    }

    private static WorkflowRun PrepareExisting(WorkflowStore store, WorkflowRun run, WorkflowCommandDescriptor descriptor) =>
        store.ApplyStep(run.Id, run.Revision, new(Data(new { stage = "pending" }),
            new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, Data(new { steeringSeen = run.Steering.Count }))), descriptor);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static bool SameExecutionState(WorkflowRun left, WorkflowRun right) =>
        left.Status == right.Status && left.CancelRequested == right.CancelRequested &&
        left.Steps == right.Steps && left.ActiveMilliseconds == right.ActiveMilliseconds &&
        Serialize(left.FailedEffects) == Serialize(right.FailedEffects) &&
        Serialize(left.State) == Serialize(right.State) && Serialize(left.Effect) == Serialize(right.Effect) &&
        Serialize(left.Signal) == Serialize(right.Signal) && Serialize(left.Questions) == Serialize(right.Questions);

    private sealed record FixtureModule(IWorkflowDefinition Definition, IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule
    {
        public string Id => "fixture.steering";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition];
    }

    private sealed class EffectDefinition(string commandId) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1.0.0", "Steering fixture.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), []) { SupportsSteering = true };
        public int Advances { get; private set; }
        public Action<WorkflowRun>? DuringAdvance { get; set; }
        public WorkflowStep Advance(WorkflowRun snapshot)
        {
            Advances++;
            DuringAdvance?.Invoke(snapshot);
            if (snapshot.Signal.Kind == WorkflowSignalKind.Start)
                return new(Data(new { stage = "effect" }), new InvokeWorkflowCommand(commandId, 1,
                    Data(new { steeringSeen = snapshot.Steering.Count })));
            var result = snapshot.Signal.Data.Deserialize<WorkflowCommandResult>(Json)!;
            return new(Data(new { stage = "done" }), new CompleteWorkflow(
                Data(new { marker = result.Data?.GetProperty("marker").GetString(), steeringCount = snapshot.Steering.Count }),
                result.State == WorkflowEffectState.Succeeded, result.Error));
        }
    }

    private sealed class QuestionDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1.0.0", "Question fixture.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), []) { SupportsSteering = true };
        public int Advances { get; private set; }
        public WorkflowStep Advance(WorkflowRun snapshot)
        {
            Advances++;
            if (snapshot.Signal.Kind == WorkflowSignalKind.Start)
                return new(Data(new { stage = "questions" }), new AskWorkflowQuestions([
                    new("first", "First answer"), new("second", "Second answer")]));
            return new(Data(new { stage = "done" }), new CompleteWorkflow(Data(new
            {
                first = snapshot.Signal.Data.GetProperty("first").GetString(),
                second = snapshot.Signal.Data.GetProperty("second").GetString(),
                steeringCount = snapshot.Steering.Count
            })));
        }
    }

    private class ProbeHandler(string command) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new("fixture.steering:" + command, 1, "Controlled fixture effect.",
            Schema("""{"type":"object","properties":{"steeringSeen":{"type":"integer","minimum":0}},"required":["steeringSeen"],"additionalProperties":false}"""),
            Schema("""{"type":"object","properties":{"marker":{"type":"string"}},"required":["marker"],"additionalProperties":false}"""),
            [], WorkflowReplayPolicy.Never, TimeoutSeconds: 15);
        public int Executions { get; private set; }
        public int Admissions { get; private set; }
        public string? LastEffectId { get; private set; }
        public JsonElement? LastArguments { get; private set; }
        public string? FailureBudgetKey { get; init; }
        public Action<WorkflowCommandContext>? DuringAdmission { get; set; }
        public string? GetFailureBudgetKey(WorkflowCommandContext context)
        {
            Admissions++;
            DuringAdmission?.Invoke(context);
            return FailureBudgetKey;
        }
        public virtual Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            Executions++;
            LastEffectId = context.EffectId;
            LastArguments = context.Arguments.Clone();
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, Data(new { marker = "actual-result" })));
        }
    }

    private sealed class ControlledHandler(string command) : ProbeHandler(command)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkflowCommandResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            await base.ExecuteAsync(context, cancellationToken);
            Started.TrySetResult();
            return await Release.Task;
        }
    }

    private sealed class Assertions
    {
        public int Count { get; private set; }
        public void That(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            Count++;
            Console.WriteLine("PASS: " + message);
        }
        public void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { That(true, message); return; }
            throw new Exception("FAIL: " + message);
        }
    }
}
