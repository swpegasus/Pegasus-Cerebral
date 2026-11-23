using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

/// <summary>Offscreen deterministic source fixture; no Worker, model, network, or physical-frame claim.</summary>
public static class MainChatInterfaceTimingChecks
{
    public static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("WPF dispatcher required.");
        dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        const string caseId = "0123456789abcdef0123456789abcdef";
        var now = DateTimeOffset.UtcNow;
        var original = new CerebralEvent("timing-original", "timing-chat", "timing-run", 1,
            CerebralEventTypes.Message, new(EventActorKind.User), [new TextContentPart("private prompt")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now);
        var events = new List<CerebralEvent> { original };
        var saved = new WorkflowRun
        {
            Id = "timing-run", Revision = 1, Status = WorkflowStatus.Queued,
            Submission = new("timing-run", "timing-chat", "lead", "fixture.timing", "1.0.0",
                "private prompt", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now
        };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(saved),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events, snapshotSequence = events[^1].Sequence, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { original }, snapshotSequence = events[^1].Sequence, nextAfterSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected fixture operation")
            });
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => [], (_, _) => { }, embedded: true);
        workspace.BeginConversation("timing-chat");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await workspace.OpenRunSnapshotAsync("timing-run", deadline.Token);
        using var source = new HwndSource(new HwndSourceParameters("Main chat timing checks")
        { Width = 640, Height = 480, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = workspace };
        async Task Flush()
        {
            await dispatcher.InvokeAsync(() => { workspace.Measure(new Size(640, 480));
                workspace.Arrange(new Rect(0, 0, 640, 480)); workspace.UpdateLayout(); }, DispatcherPriority.Loaded);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        await Flush();
        Check(workspace.IsLoaded, "The source fixture must have an offscreen presentation tree.");

        var recorded = new List<MainChatTimingResult>();
        var marks = new List<WorkflowPresentationMark>();
        var detached = 0;
        Action<WorkflowPresentationMark>? receiver = null;
        using var probe = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
            result => recorded.Add(result), () => { detached++; if (receiver is not null) workspace.PresentationMarked -= receiver; });
        probe.Bind("timing-chat", "timing-run", "lead");
        probe.MarkSubmitCall("timing-run");
        workspace.PresentationMarked += _ => throw new InvalidOperationException("observer fixture failure");
        receiver = mark =>
        {
            marks.Add(mark);
            if (probe.ObservePresentation(mark)) probe.MarkRenderSchedulingCallback();
        };
        workspace.PresentationMarked += receiver;
        ServiceEvent Preview(long sequence, string request = "timing-run", string effect = "effect-a") =>
            new(sequence, "workflow.generation.preview", request,
                CerebralJson.Element(new { schema = "cerebral.generation-preview/1", conversationId = "timing-chat",
                    requestId = request, runId = request, effectId = effect, agentId = "lead", phase = "draft",
                    kind = "final", text = "PRIVATE-PREVIEW-BODY", operation = (string?)null,
                    truncated = false, elapsedMilliseconds = 1, finish = (string?)null }), now);
        var first = Preview(10);
        probe.ObservePreviewEnvelope(first);
        workspace.Notify(first); await Flush();
        Check(marks.Count(x => x.Kind == WorkflowPresentationMarkKind.PreviewAdmitted) == 1 &&
              marks.Count(x => x.Kind == WorkflowPresentationMarkKind.PublicPreviewContentMutated) == 1,
            "A throwing observer cannot prevent the following scalar observer or UI mutation.");
        Check(probe.RequestId == "timing-run", "The source probe retains only the bound request coordinates.");
        workspace.Notify(first); workspace.Notify(Preview(9)); workspace.Notify(Preview(11, "foreign-run"));
        probe.ObservePreviewEnvelope(Preview(12, "foreign-run"));
        await Flush();
        Check(marks.Count(x => x.Kind == WorkflowPresentationMarkKind.PublicPreviewContentMutated) == 1,
            "Duplicate, stale, and foreign preview events do not create a second content mutation.");
        Check(!probe.ObservePresentation(new(WorkflowPresentationMarkKind.PublicPreviewContentMutated,
                "other-chat", "timing-run", "timing-run", "effect-a", "lead", null, Stopwatch.GetTimestamp())),
            "A foreign scalar observer coordinate cannot join the request.");
        probe.MarkConfirmedAck("timing-run"); // A preview can legitimately precede the submit await.
        events.Add(new("timing-sidebar-result", "timing-chat", "timing-run", 2, "workflow.result",
            new(EventActorKind.Agent, "lead"), [new TextContentPart("PRIVATE-SIDEBAR-BODY")],
            new(ModelInclusion.Evidence, HumanVisibility.Sidebar), new(EventSourceKind.Service, "fixture"), now));
        saved = saved with { Revision = 2, Status = WorkflowStatus.Completed };
        await workspace.InspectRunAsync("timing-run", deadline.Token); await Flush();
        Check(marks.All(x => x.Kind != WorkflowPresentationMarkKind.FinalCardMutated),
            "A sidebar-only canonical result cannot claim a transcript card mutation.");
        probe.MarkNonTranscriptResult("timing-chat", "timing-run");
        probe.Finish(MainChatTimingEnd.Completed);
        Check(recorded.Count == 1 && detached == 1 && recorded[0].SendToSubmitAckMs is not null &&
              recorded[0].SendToFirstPreviewEnvelopeMs is not null &&
              recorded[0].SendToFirstPublicPreviewContentMutationMs is not null &&
              recorded[0].SendToFinalCardMutationMs is null &&
              recorded[0].MissingFinalCardReason == "canonical_result_not_in_transcript" &&
              !recorded[0].ClosedBeforeAck,
            "An early public event and UI mutation retain independent offsets; absent sidebar card stays null.");
        var safeJson = JsonSerializer.Serialize(recorded[0]);
        Check(!safeJson.Contains("PRIVATE-", StringComparison.Ordinal) &&
              !safeJson.Contains("private prompt", StringComparison.Ordinal) &&
              !safeJson.Contains("observer fixture failure", StringComparison.Ordinal),
            "The fixed output record contains no prompt, preview, result, or observer exception text.");
        var laterMarks = new List<WorkflowPresentationMark>();
        Action<WorkflowPresentationMark> later = laterMarks.Add;
        workspace.PresentationMarked += later;
        events.Add(new("timing-transcript-result", "timing-chat", "timing-run", 3, "workflow.result",
            new(EventActorKind.Agent, "lead"), [new TextContentPart("PRIVATE-TRANSCRIPT-BODY")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now));
        saved = saved with { Revision = 3 };
        await workspace.InspectRunAsync("timing-run", deadline.Token); await Flush();
        workspace.PresentationMarked -= later;
        Check(laterMarks.Count(x => x.Kind == WorkflowPresentationMarkKind.FinalCardMutated &&
              x.EventId == "timing-transcript-result") == 1 && marks.All(x => x.Kind != WorkflowPresentationMarkKind.FinalCardMutated),
            "Only a real transcript card mutation emits its scalar ID; the retired request observer stays detached.");

        // Exercise the actual submit path: MutateAsync publishes a terminal snapshot before
        // SubmitConversationAsync's outer finally renders the canonical final card.
        var fastInput = new WorkflowConversationSubmission("fast-run", "lead",
            ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion,
            "PRIVATE-FAST-PROMPT", WorkflowContract.EmptyObject);
        var fastSubmission = new WorkflowSubmission("fast-run", "fast-chat", "lead",
            fastInput.DefinitionId, fastInput.DefinitionVersion, fastInput.UserText, null,
            fastInput.Input, WorkflowContract.EmptyObject) { ConversationSequence = 0 };
        var fastRun = new WorkflowRun { Id = "fast-run", Submission = fastSubmission, Revision = 1,
            Status = WorkflowStatus.Completed, CreatedAt = now, UpdatedAt = now,
            Result = CerebralJson.Element(new { summary = "PRIVATE-FAST-RESULT" }) };
        CerebralEvent[] fastEvents =
        [
            new("fast-user", "fast-chat", "fast-run", 1, CerebralEventTypes.Message,
                new(EventActorKind.User), [new TextContentPart(fastInput.UserText)],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now),
            new("fast-final", "fast-chat", "fast-run", 2, "workflow.result",
                new(EventActorKind.Agent, "lead"),
                [new StructuredContentPart("workflow.result", CerebralJson.Element(new { summary = "PRIVATE-FAST-RESULT" }))],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now)
        ];
        Task<CommandResult> FastDispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.submit" or "jobs.workflow.get" => CommandResult.Ok(fastRun),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events = fastEvents,
                    snapshotSequence = 2L, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { fastEvents[0] },
                    snapshotSequence = 2L, nextAfterSequence = (long?)1 }),
                _ => throw new InvalidOperationException("Unexpected fast-terminal fixture operation")
            });
        }
        using var fastWorkspace = new WorkflowWorkspace(FastDispatch, () => [], (_, _) => { }, embedded: true);
        fastWorkspace.BeginConversation("fast-chat");
        using var fastSource = new HwndSource(new HwndSourceParameters("Fast Main timing checks")
        { Width = 640, Height = 480, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = fastWorkspace };
        MainChatTimingResult? fastResult = null;
        var fastMarks = new List<WorkflowPresentationMark>();
        var terminalSeenBeforeCard = false;
        var terminalQueued = false;
        using var fastProbe = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
            result => fastResult = result, () => { });
        fastProbe.Bind("fast-chat", "fast-run", "lead");
        fastWorkspace.PresentationMarked += mark =>
        {
            fastMarks.Add(mark);
            fastProbe.ObservePresentation(mark);
        };
        fastWorkspace.SnapshotChanged += (_, _) =>
        {
            if (terminalQueued || fastWorkspace.Snapshot?.Run is not { IsTerminal: true }) return;
            terminalQueued = true;
            terminalSeenBeforeCard = fastMarks.All(mark => mark.Kind != WorkflowPresentationMarkKind.FinalCardMutated);
            MainChatTimingTerminalDeferral.Queue(dispatcher, () => true, () => fastWorkspace.Snapshot,
                "fast-chat", "fast-run", fastProbe.Finish,
                () => fastProbe.Finish(MainChatTimingEnd.Disposed));
        };
        fastProbe.MarkSubmitCall("fast-run");
        var fastAccepted = await fastWorkspace.SubmitConversationAsync(fastInput, deadline.Token);
        fastProbe.MarkConfirmedAck("fast-run");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(fastAccepted.Run is { IsTerminal: true } && terminalQueued && terminalSeenBeforeCard,
            "The real submit path publishes a fast terminal snapshot before its final-card render.");
        Check(fastMarks.Any(mark => mark.Kind == WorkflowPresentationMarkKind.FinalCardMutated &&
              mark.EventId == "fast-final") && fastResult is { SendToFinalCardMutationMs: not null, End: "Completed" },
            "Deferred terminal closure retains the actual canonical final-card mutation.");
        Check(fastResult is not null && !JsonSerializer.Serialize(fastResult).Contains("PRIVATE-FAST-", StringComparison.Ordinal),
            "The fast-terminal receipt remains scalar-only even when the submitted and result text are private.");

        MainChatTimingResult? earlyTerminal = null;
        using (var racing = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
            result => earlyTerminal = result, () => { }))
        {
            racing.Bind("timing-chat", "timing-run", "lead");
            racing.MarkSubmitCall("timing-run");
            racing.ObservePreviewEnvelope(first);
            racing.Finish(MainChatTimingEnd.Completed);
            racing.MarkConfirmedAck("timing-run");
        }
        Check(earlyTerminal is { ClosedBeforeAck: true, SendToSubmitAckMs: null,
                SendToFirstPreviewEnvelopeMs: not null },
            "A terminal callback before the submit await does not synthesize or backfill acknowledgement.");

        var timeout = new TaskCompletionSource<MainChatTimingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutDetach = 0;
        using (var timed = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
            result => timeout.TrySetResult(result), () => Interlocked.Increment(ref timeoutDetach),
            TimeSpan.FromMilliseconds(20)))
        {
            var result = await timeout.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.End == nameof(MainChatTimingEnd.Timeout) && timeoutDetach == 1,
                "The deadline closes once and detaches without a Worker or UI event.");
        }
        var disposed = 0;
        var disposeResults = 0;
        using (var retiring = new MainChatInterfaceTimingProbe(caseId, Stopwatch.GetTimestamp,
            _ => disposeResults++, () => disposed++))
        {
            retiring.Dispose(); retiring.Finish(MainChatTimingEnd.Completed);
        }
        Check(disposed == 1 && disposeResults == 1, "Disposal and later terminal callbacks are idempotent.");
        return checks;
    }
}
