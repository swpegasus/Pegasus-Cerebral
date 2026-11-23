using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Native embedded Chat controls and adapter checks with deterministic command delegates.</summary>
public static class WorkflowChatWorkspaceChecks
{
    public static Task<int> VerifyAsync() => VerifyAsync(null);

    public static async Task<int> VerifyAsync(string? previewPath)
    {
        if (previewPath is not null && (!Path.IsPathFullyQualified(previewPath) ||
            !previewPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(previewPath))))
            throw new ArgumentException("Workspace preview requires an absolute PNG path in an existing owned directory.", nameof(previewPath));
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run from an active WPF dispatcher callback.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = WorkflowActivityDetailChecks.Check() + WorkflowReadViewportChecks.Check() +
            WorkflowInspectionTextChecks.Check() +
            await WorkflowImagePreviewChecks.CheckAsync() + await WorkflowCompactActivityChecks.VerifyAsync();
        var calls = new List<CommandRequest>(); var questionMounts = 0; var questionResets = 0;
        var errors = new List<string>(); TaskCompletionSource<CommandResult>? deferredSubmission = null;
        TaskCompletionSource<CommandResult>? deferredOlderPage = null;
        TaskCompletionSource? olderPageStarted = null;
        TaskCompletionSource<CommandResult>? deferredReleaseRun = null;
        TaskCompletionSource? releaseRunStarted = null;
        var failEvents = false; WorkflowRun? saved = null; CerebralEvent? acceptedOriginalEvent = null;
        var now = DateTimeOffset.UnixEpoch;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var clockRun = new WorkflowConversationRun("clock-run", "clock-chat", "builder", "Clock fixture",
            "pegasus.conversation:chat", "1.0.0", 1, WorkflowStatus.Running, null, null, null, null,
            false, "clock-effect", WorkflowEffectState.Dispatched, 1)
        {
            ActiveMilliseconds = 602_750, ReservedExecutionMilliseconds = 600_000
        };
        var clockCall = new CerebralEvent("clock-call-event", "clock-chat", "clock-run", 1,
            CerebralEventTypes.ToolCall, new(EventActorKind.Agent, "builder", "Builder"), [],
            new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent), now,
            CerebralJson.Element(new ToolCallData("clock-call", "cerebral.host:generate", CerebralJson.Element(new { }))));
        var clockTransition = new CerebralEvent("clock-transition", "clock-chat", "clock-run", 2,
            CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service), [],
            new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Service), now.AddSeconds(3));
        var clockItems = new[] { clockCall, clockTransition };
        var clockProfiles = new Dictionary<string, string>();
        string Header(WorkflowConversationRun run, int seconds) =>
            WorkflowWorkspace.ActivityHeader(clockItems, run, clockProfiles, now.AddSeconds(seconds));
        Check(Header(clockRun, 4).Contains("Working 0:04", StringComparison.Ordinal) &&
              Header(clockRun, 5).Contains("Working 0:05", StringComparison.Ordinal),
            "A dispatched running call advances smoothly using elapsed time rather than the ten-minute reservation.");
        var unknownRun = clockRun with { Status = WorkflowStatus.Waiting, EffectState = WorkflowEffectState.Unknown, CancelRequested = true };
        var unknownHeader = Header(unknownRun, 4);
        Check(unknownHeader.Contains("Waiting · outcome unconfirmed 0:03", StringComparison.Ordinal) &&
              !unknownHeader.Contains("Working", StringComparison.Ordinal) && Header(unknownRun, 90) == unknownHeader,
            "An owner-cancelled unknown effect remains visibly waiting and freezes at its recorded transition without charging the timeout reservation.");
        foreach (var settledStatus in new[] { WorkflowStatus.Waiting, WorkflowStatus.Cancelling,
            WorkflowStatus.Completed, WorkflowStatus.Failed, WorkflowStatus.Cancelled })
        {
            var settled = clockRun with { Status = settledStatus };
            var header = Header(settled, 4);
            Check(header.Contains(settledStatus + " 0:03", StringComparison.Ordinal) &&
                  !header.Contains("Working", StringComparison.Ordinal) && Header(settled, 90) == header,
                "An unmatched call cannot keep " + settledStatus + " activity advancing.");
        }
        var cancellingRun = clockRun with { CancelRequested = true };
        Check(Header(cancellingRun, 4).Contains("Cancelling 0:03", StringComparison.Ordinal) &&
              Header(cancellingRun, 4) == Header(cancellingRun, 90),
            "Cancellation requested before the next durable status update freezes active presentation.");
        var unknownWhileRunning = clockRun with { EffectState = WorkflowEffectState.Unknown };
        Check(Header(unknownWhileRunning, 4).Contains("Waiting · outcome unconfirmed", StringComparison.Ordinal) &&
              Header(unknownWhileRunning, 4) == Header(unknownWhileRunning, 90),
            "An unknown effect takes precedence over a stale Running snapshot without replaying or claiming work.");
        var pendingClockRow = WorkflowWorkspace.ProjectActivityRows([clockCall]).Single();
        foreach (var terminalStatus in new[] { WorkflowStatus.Completed, WorkflowStatus.Failed, WorkflowStatus.Cancelled })
        {
            var terminalRun = clockRun with { Status = terminalStatus };
            var caption = WorkflowWorkspace.ActivityRowCaption(pendingClockRow, terminalRun, clockItems, clockProfiles, now.AddSeconds(4));
            Check(!caption.Contains("Running", StringComparison.Ordinal) &&
                  caption == WorkflowWorkspace.ActivityRowCaption(pendingClockRow, terminalRun, clockItems, clockProfiles, now.AddSeconds(90)),
                "An unresolved operation row freezes when the workflow is terminal instead of continuing its smooth clock.");
        }
        var absentTransition = WorkflowWorkspace.ActivityHeader([clockCall], unknownRun, clockProfiles, now.AddSeconds(90));
        Check(absentTransition.Contains("0:02", StringComparison.Ordinal) && !absentTransition.Contains("1:30", StringComparison.Ordinal),
            "Without a recorded end transition, unresolved elapsed time falls back to measured completed work rather than UI wall time.");
        CerebralEvent Item(string id, long sequence, string text, HumanVisibility visibility) => new(id, "chat-fixture", "request-one", sequence,
            "message", new(EventActorKind.User), [new TextContentPart(text)], new(ModelInclusion.Required, visibility), new(EventSourceKind.User), now);
        var longResponse = string.Join('\n', Enumerable.Range(1, 60).Select(index => $"Response line {index}: durable chat content belongs to the conversation page."));
        var doubleEncodedDetails = JsonSerializer.Serialize(JsonSerializer.Serialize(new { path = @"C:\fixture\Program.cs", note = "Prepared" }));
        var events = new[]
        {
            Item("visible", 41, "VISIBLE REQUEST", HumanVisibility.Transcript),
            Item("hidden", 42, "PRIVATE ASSIGNMENT", HumanVisibility.Hidden),
            new CerebralEvent("call", "chat-fixture", "request-one", 43, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "builder", "Builder"), [], new(ModelInclusion.Required, HumanVisibility.Activity),
                new(EventSourceKind.Agent, AgentId: "builder"), now,
                CerebralJson.Element(new ToolCallData("call-one", "cerebral.host:tool", CerebralJson.Element(new
                {
                    name = "file.write",
                    arguments = new { path = @"C:\fixture\Program.cs" }
                })))),
            new CerebralEvent("result", "chat-fixture", "request-one", 44, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool),
                [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                {
                    state = "Succeeded",
                    data = new
                    {
                        prepared = new { inputTokens = 15961 },
                        generation = new { generatedTokens = 497 },
                        elapsedMilliseconds = 750L
                    }
                }))],
                new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), now,
                CerebralJson.Element(new ToolResultData("call-one", "cerebral.host:tool", true, DurationMilliseconds: 750))),
            new CerebralEvent("legacy-result", "chat-fixture", "request-one", 45, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool), [], new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), now,
                CerebralJson.Element(new ToolResultData("legacy-call", "legacy.tool", true))),
            new CerebralEvent("response", "chat-fixture", "request-one", 46, "message",
                new(EventActorKind.Agent, DisplayName: "Cerebral"), [new TextContentPart(longResponse)],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now),
            new CerebralEvent("structured-response", "chat-fixture", "request-one", 47, "message",
                new(EventActorKind.Agent, DisplayName: "Cerebral"),
                [new StructuredContentPart("fixture.details", CerebralJson.Element(doubleEncodedDetails))],
                new(ModelInclusion.OnDemand, HumanVisibility.Transcript), new(EventSourceKind.Agent), now),
            new CerebralEvent("null-usage-result", "chat-fixture", "request-one", 48, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool),
                [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                {
                    state = "Succeeded",
                    data = new
                    {
                        prepared = new { inputTokens = (int?)null },
                        generation = new { generatedTokens = (int?)null },
                        elapsedMilliseconds = (long?)null
                    }
                }))],
                new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), now,
                CerebralJson.Element(new ToolResultData("call-null", "cerebral.host:history", true)))
        };
        var olderEvents = new[] { Item("older-visible", 1, "OLDER TEMPORARY MESSAGE", HumanVisibility.Transcript) };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add(request);
            Check(dispatcher.CheckAccess() && request.ProfileId is null, "Native chat commands must dispatch on the owner UI route without impersonating a profile.");
            if (request.Operation == "jobs.workflow.recent") return Task.FromResult(EmptyRecentWorkPage(request));
            if (request.Operation == "jobs.workflow.submit")
            {
                if (deferredSubmission is not null) return deferredSubmission.Task.WaitAsync(token);
                var value = request.Arguments;
                var submission = new WorkflowSubmission(value.GetProperty("requestId").GetString()!, value.GetProperty("conversationId").GetString()!,
                    value.GetProperty("profileId").GetString()!, value.GetProperty("definitionId").GetString()!, value.GetProperty("definitionVersion").GetString()!,
                    value.GetProperty("userText").GetString()!, value.GetProperty("assignmentText").GetString(), value.GetProperty("input").Clone(), WorkflowContract.EmptyObject)
                    { ConversationSequence = value.GetProperty("requestId").GetString() == "request-one" ? 40 : 48 };
                acceptedOriginalEvent = submission.RequestId == "request-one" ? events[0] :
                    new CerebralEvent("accepted-" + submission.RequestId, submission.ConversationId, submission.RequestId,
                        submission.ConversationSequence + 1, CerebralEventTypes.Message, new(EventActorKind.User),
                        [new TextContentPart(submission.UserText)], new(ModelInclusion.Required, HumanVisibility.Transcript),
                        new(EventSourceKind.User), now);
                saved = new() { Id = submission.RequestId, Submission = submission, Revision = 1, Status = WorkflowStatus.Waiting,
                    Questions = new("questions", 1, [new("first", "First answer"), new("second", "Second answer")], new Dictionary<string, string>()),
                    CreatedAt = now, UpdatedAt = now, ActiveMilliseconds = 2750 };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (request.Operation == "conversations.get") return Task.FromResult(CommandResult.Ok(new ConversationSummary(
                saved!.Submission.ConversationId, "Fixture", saved.Submission.ProfileId, saved.Submission.ProjectId,
                saved.Id, saved.Status, saved.UpdatedAt)));
            if (request.Operation == "jobs.workflow.get")
            {
                if (deferredReleaseRun is not null)
                {
                    releaseRunStarted?.TrySetResult();
                    return deferredReleaseRun.Task.WaitAsync(token);
                }
                return Task.FromResult(saved is null ? CommandResult.Fail("Unknown run") : CommandResult.Ok(saved));
            }
            if (request.Operation == "jobs.workflow.events")
            {
                if (failEvents) throw new IOException("Fixture display refresh failed after a confirmed mutation.");
                var originalEvent = acceptedOriginalEvent ?? throw new InvalidOperationException("No accepted original event.");
                Check(request.Arguments.GetProperty("limit").GetInt32() == 1 &&
                      request.Arguments.GetProperty("afterSequence").GetInt64() == saved!.Submission.ConversationSequence,
                    "Original-message recovery must request one canonical event at the saved submission boundary.");
                var snapshot = request.Arguments.GetProperty("snapshotSequence").GetInt64();
                return Task.FromResult(CommandResult.Ok(new { events = new[] { originalEvent }, snapshotSequence = snapshot,
                    nextAfterSequence = originalEvent.Sequence < snapshot ? (long?)originalEvent.Sequence : null }));
            }
            if (request.Operation == "jobs.workflow.events.previous")
            {
                if (failEvents) throw new IOException("Fixture display refresh failed after a confirmed mutation.");
                var latestEvents = acceptedOriginalEvent is { Sequence: > 48 } originalEvent ? events.Append(originalEvent).ToArray() : events;
                var snapshotSequence = latestEvents[^1].Sequence;
                if (request.Arguments.TryGetProperty("beforeSequence", out _) && deferredOlderPage is not null)
                {
                    olderPageStarted?.TrySetResult();
                    return deferredOlderPage.Task.WaitAsync(token);
                }
                return request.Arguments.TryGetProperty("beforeSequence", out _)
                    ? Task.FromResult(CommandResult.Ok(new { events = olderEvents, snapshotSequence, nextBeforeSequence = (long?)null }))
                    : Task.FromResult(CommandResult.Ok(new { events = latestEvents, snapshotSequence, nextBeforeSequence = (long?)41 }));
            }
            if (request.Operation == "jobs.workflow.answer")
            {
                var answer = CerebralJson.Read<WorkflowAnswerRequest>(request.Arguments);
                Check(answer.ExpectedQuestionRevision == 1 && answer.QuestionSetId == "questions" && answer.Answers.Count == 2 &&
                    answer.Answers["first"] == "One" && answer.Answers["second"] == "Two", "The native form must submit each displayed answer with its exact question revision.");
                saved = saved! with { Revision = saved.Revision + 1, Status = WorkflowStatus.Completed, Questions = null, Result = CerebralJson.Element(new { summary = "Done" }) };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (request.Operation == "jobs.workflow.cancel")
            {
                saved = saved! with { Revision = saved.Revision + 1, Status = WorkflowStatus.Cancelled, Questions = null };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (request.Operation == "jobs.workflow.reconcile")
            {
                saved = saved! with { Revision = saved.Revision + 1, Status = saved.CancelRequested ? WorkflowStatus.Cancelled : WorkflowStatus.Queued,
                    Effect = saved.Effect! with { State = WorkflowEffectState.Failed }, WaitingReason = null };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            throw new InvalidOperationException("Unexpected native chat route: " + request.Operation);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(), (message, error) => { if (error) errors.Add(message); }, embedded: true);
        var attentionCurrent = true;
        var attention = workspace.CreateHostAttentionView(() => attentionCurrent);
        Check(ReferenceEquals(attention, workspace.CreateHostAttentionView(() => true)),
            "Main obtains one cached workspace-owned attention view, not duplicate action instances.");
        attention.Measure(new Size(680, 200)); attention.Arrange(new Rect(0, 0, 680, 200)); attention.UpdateLayout();
        Check(Find<Button>(workspace, "WorkflowRefresh") is null &&
            Find<Button>(attention, "WorkflowRefresh") is not null &&
            Find<Button>(attention, "WorkflowCheckOutcome") is null,
            "Main removes the permanent embedded toolbar and never offers a speculative Check outcome.");
        using (var dedicated = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(), (_, _) => { }))
        {
            Check(Find<Button>(dedicated, "WorkflowRefresh") is { MinHeight: >= 30 } defaultRefresh &&
                defaultRefresh.Padding.Top > 2 && defaultRefresh.Padding.Bottom > 2,
                "Dedicated Work retains its original comfortable toolbar dimensions and default callbacks.");
            var refused = false;
            try { dedicated.CreateHostAttentionView(() => true); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "The optional host seam cannot change standalone Work presentation.");
        }
        workspace.QuestionsResetting = () => questionResets++;
        workspace.QuestionTextEntryActions = (_, id) => { questionMounts++; return new TextBlock { Text = "Dictation hook " + id }; };
        workspace.BeginConversation("chat-fixture");
        await workspace.RefreshAsync();
        Check(calls.Count == 0, "A fresh embedded conversation must not query unrelated saved-work lists.");
        var submission = new WorkflowConversationSubmission("request-one", "lead", ConversationControlClient.ChatDefinitionId,
            ConversationControlClient.ChatDefinitionVersion, "VISIBLE REQUEST",
            CerebralJson.Element(new { workingDirectory = @"C:\Projects\fixture", projectId = "project-fixture", responseSharePercent = 26 }), "PRIVATE ASSIGNMENT");
        await workspace.SubmitConversationAsync(submission, deadline.Token);
        Check(saved?.Submission is { UserText: "VISIBLE REQUEST", AssignmentText: "PRIVATE ASSIGNMENT" } &&
            saved.Submission.Input.GetProperty("responseSharePercent").GetInt32() == 26 && saved.Submission.Input.GetProperty("projectId").GetString() == "project-fixture",
            "Visible text, private plugin assignment, project scope and response balance must be preserved separately.");
        workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
        Check(workspace.Snapshot?.Transcript.Count() == 3 &&
            workspace.Snapshot.Events.Single(item => item.Id == "hidden").Projection.Model == ModelInclusion.Required,
            "Human-hidden context must retain its model inclusion policy in the bounded event page.");
        Check(Find<Button>(workspace, "WorkflowLoadEarlier") is { Content: "Load earlier messages", Visibility: Visibility.Visible, IsEnabled: true },
            "Chat history exposes one bounded Load earlier messages row at the top of the conversation page.");
        var nativeActivity = Find<Button>(workspace, "WorkflowActivity") ??
            throw new InvalidOperationException("The native Activity summary action is missing.");
        const string expectedNativeHeader = "Activity · Builder · Waiting 0:02 · 1 call shown · 3 completed shown · 0 failed shown · 15,961 in / 497 out shown · Earlier activity omitted · — tok/s";
        var nativeHeader = nativeActivity.Content as string;
        var nativeHeaderMatches = string.Equals(nativeHeader, expectedNativeHeader, StringComparison.Ordinal);
        var noInlineExpander = Find<Expander>(workspace, "WorkflowActivity") is null;
        var noInlineScroller = Find<ScrollViewer>(workspace, "WorkflowActivityScroller") is null;
        var noInlineViewport = Find<Border>(workspace, "WorkflowActivityViewport") is null;
        var noInlineHistory = Find<Button>(workspace, "WorkflowActivityHistory") is null;
        Check(nativeHeaderMatches && noInlineExpander && noInlineScroller && noInlineViewport && noInlineHistory,
            "Native Chat exposes its exact measured Activity summary without inline operation or history controls. " +
            JsonSerializer.Serialize(new
            {
                actualHeader = nativeHeader is null ? null : nativeHeader[..Math.Min(nativeHeader.Length, 512)],
                headerTruncated = nativeHeader is { Length: > 512 }, nativeHeaderMatches,
                noInlineExpander, noInlineScroller, noInlineViewport, noInlineHistory
            }));
        var activityTargets = new List<ActivityTurnTarget>(); workspace.ActivityNavigationRequested += activityTargets.Add;
        var callsBeforeActivityNavigation = calls.Count;
        nativeActivity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(activityTargets is [{ ConversationId: "chat-fixture", RunId: "request-one", FirstSequence: 43, LastSequence: 48 }] &&
              calls.Count == callsBeforeActivityNavigation && workspace.Snapshot?.RunId == "request-one",
            "Explicit summary activation requests only its exact accepted turn and journal interval without dispatching work.");

        // Keep inline inspection in standalone Work over the same accepted fake run, never a second submission.
        using var activityWork = new WorkflowWorkspace(ReadOnlyInspectionCommand(Dispatch), () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); });
        using var activitySource = new HwndSource(new HwndSourceParameters("Cerebral inline Work inspection checks")
        { Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = activityWork };
        await activityWork.OpenRunSnapshotAsync(saved!.Id, deadline.Token);
        void LayoutActivityWork()
        { activityWork.Measure(new Size(680, 480)); activityWork.Arrange(new Rect(0, 0, 680, 480)); activityWork.UpdateLayout(); }
        LayoutActivityWork();
        Check(activityWork.Snapshot?.RunId == workspace.Snapshot?.RunId &&
              activityWork.Snapshot?.ConversationId == workspace.Snapshot?.ConversationId &&
              activityWork.Snapshot?.Run?.Revision == workspace.Snapshot?.Run?.Revision &&
              calls.Count(call => call.Operation == "jobs.workflow.submit") == 1,
            "The read-only Work inspector adopts the same accepted head and revision without submitting another request.");
        var activity = Find<Expander>(activityWork, "WorkflowActivity") ??
            throw new InvalidOperationException("The standalone Work activity section is missing.");
        Check(activity?.Header is string activityHeader && activityHeader.Contains("Activity · Builder · Waiting 0:02", StringComparison.Ordinal) &&
            activityHeader.Contains("1 call", StringComparison.Ordinal) && activityHeader.Contains("15,961 in / 497 out", StringComparison.Ordinal),
            "Activity summarizes the current state, measured duration, call count and exact retained token usage.");
        activity!.IsExpanded = true; LayoutActivityWork();
        var rollingRows = Descendants(activity).OfType<Expander>().Where(row => row != activity).ToArray();
        var rollingScroller = Find<ScrollViewer>(activityWork, "WorkflowActivityScroller") ??
            throw new InvalidOperationException("The inline Work rolling viewport is missing.");
        Check(rollingRows.Length == 3 && rollingRows.Count(row => row.Visibility == Visibility.Visible) == 2 &&
              rollingRows.Single(row => AutomationProperties.GetAutomationId(row) == "WorkflowActivityEntry-call").Visibility == Visibility.Collapsed &&
              rollingScroller?.MaxHeight == 150,
            "Standalone Work initially shows exactly the latest two operation rows in its 150-DIP rolling viewport.");
        var review = Find<Button>(activityWork, "WorkflowActivityHistory") ??
            throw new InvalidOperationException("The inline Work history action is missing.");
        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); LayoutActivityWork();
        Check(rollingRows.All(row => row.Visibility == Visibility.Visible) && rollingScroller.MaxHeight == 320,
            "Explicit Review earlier activity reveals all three retained rows within the bounded 320-DIP inspector.");
        var activityScroller = Find<ScrollViewer>(activityWork, "WorkflowActivityScroller");
        var activityViewport = Find<Border>(activityWork, "WorkflowActivityViewport");
        var renderedActivityHeader = activity.Header as string ?? throw new InvalidOperationException("The activity header was not rendered as text.");
        var activitySnapshot = activityWork.ActivityViewportSnapshots.Single();
        var namedSection = Find<StackPanel>(activityWork, "WorkflowActivitySection-call");
        Check(namedSection is not null && Descendants(namedSection).OfType<TextBlock>()
                .Any(block => block.Text.StartsWith("Changing files · Waiting", StringComparison.Ordinal)),
            "Recorded commands render inside named standalone Work sections with the current run outcome.");
        var sectionHeader = namedSection!.Children.OfType<TextBlock>().Single();
        Check(activitySnapshot.RenderedSectionCount == 1 && activitySnapshot.RenderedSections.Single() is
              { Key: "call", RowCount: 3, HeaderTruncated: false } renderedSection &&
              renderedSection.Header == sectionHeader.Text && !activitySnapshot.RenderedSectionsTruncated,
            "Bounded section diagnostics report the actual rendered section header, stable key and projected row count.");
        var savedSectionHeader = sectionHeader.Text;
        sectionHeader.Text = new string('s', WorkflowWorkspace.MaximumActivityDiagnosticHeaderCharacters + 1);
        Check(activityWork.ActivityViewportSnapshots.Single().RenderedSections.Single() is
              { HeaderTruncated: true, Header.Length: WorkflowWorkspace.MaximumActivityDiagnosticHeaderCharacters },
            "Section diagnostics bound actual visual header text and report truncation instead of recomputing its status.");
        sectionHeader.Text = savedSectionHeader;
        Check(activitySnapshot.RenderedHeader == renderedActivityHeader && !activitySnapshot.RenderedHeaderTruncated,
            "Activity diagnostics must report the actual rendered Expander header instead of recomputing elapsed metrics.");
        activity.Header = new string('h', WorkflowWorkspace.MaximumActivityDiagnosticHeaderCharacters + 17);
        activitySnapshot = activityWork.ActivityViewportSnapshots.Single();
        Check(activitySnapshot.RenderedHeader.Length == WorkflowWorkspace.MaximumActivityDiagnosticHeaderCharacters &&
              activitySnapshot.RenderedHeaderTruncated,
            "Activity diagnostics must bound rendered header text and identify truncation.");
        activity.Header = renderedActivityHeader;
        Check(activityScroller is { MaxHeight: 320, VerticalScrollBarVisibility: ScrollBarVisibility.Auto,
                  HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled } &&
              activityViewport?.Effect is System.Windows.Media.Effects.DropShadowEffect,
            "Expanded activity must use one small shadowed operation viewport instead of growing the conversation page without bound.");
        Check(Headers(activity).Count(x => x.Contains("Editing · Program.cs", StringComparison.Ordinal)) == 1 &&
            Headers(activity).Any(x => x.Contains("Builder · Completed · Editing · Program.cs · 750 ms", StringComparison.Ordinal)) &&
            (Find<Expander>(activityWork, "WorkflowActivityEntry-legacy-result")?.Header as string) ==
                "Agent not recorded · Completed · Unpaired tool receipt" &&
            (Find<Expander>(activityWork, "WorkflowActivityEntry-null-usage-result")?.Header as string) ==
                "Agent not recorded · Completed · Unpaired tool receipt" &&
            Headers(activity).All(x => !x.EndsWith(" · 0:00", StringComparison.Ordinal)),
            "Activity identifies the responsible agent, labels concrete operations, tolerates older and null-valued receipts, and pairs only measured completion without exposing raw arguments.");
        var waitingQuestions = saved!.Questions;
        saved = saved with
        {
            Revision = saved.Revision + 1,
            Status = WorkflowStatus.Running,
            Questions = null,
            ActiveMilliseconds = 602_750,
            Effect = new WorkflowEffect("reserved-effect", "cerebral.host:compact", 1,
                CerebralJson.Element(new { }), WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched)
            {
                ReservedExecutionMilliseconds = 600_000
            }
        };
        await workspace.RefreshAsync();
        await activityWork.RefreshAsync(); LayoutActivityWork();
        var reservedHeader = Find<Expander>(activityWork, "WorkflowActivity")?.Header as string;
        Check(reservedHeader is not null && !reservedHeader.Contains("10:", StringComparison.Ordinal) &&
            reservedHeader.Contains("0:02", StringComparison.Ordinal),
            "An in-flight command's timeout reservation protects the execution budget without appearing as elapsed activity time.");
        saved = saved with
        {
            Revision = saved.Revision + 1,
            Status = WorkflowStatus.Waiting,
            Questions = waitingQuestions,
            ActiveMilliseconds = 2750,
            Effect = null
        };
        await workspace.RefreshAsync();
        await activityWork.RefreshAsync(); LayoutActivityWork();
        activity = Find<Expander>(activityWork, "WorkflowActivity") ??
            throw new InvalidOperationException("The retained standalone activity section is missing.");
        activity!.IsExpanded = true; LayoutActivityWork();
        var operation = Descendants(activity).OfType<Expander>().First(x => x != activity);
        operation.IsExpanded = true; LayoutActivityWork();
        Check(!Text(activity).Contains("call-one", StringComparison.Ordinal) && !Text(activity).Contains("\\u0022", StringComparison.Ordinal),
            "Expanded activity keeps raw command envelopes and escaped JSON out of the human-facing operation details.");
        var text = Text(workspace);
        Check(text.Contains("VISIBLE REQUEST", StringComparison.Ordinal) && !text.Contains("PRIVATE ASSIGNMENT", StringComparison.Ordinal),
            "The native transcript must display visible text while keeping private assignment out of the human view.");
        Check(text.Contains("C:\\fixture\\Program.cs", StringComparison.Ordinal) && !text.Contains("\\u0022", StringComparison.Ordinal),
            "Nested structured strings must be decoded for human display instead of leaking escaped envelope text.");
        var transcriptScroller = Find<ScrollViewer>(workspace, "WorkflowTranscript") ?? throw new InvalidOperationException("Workflow transcript is missing.");
        var humanCard = Find<Border>(workspace, "WorkflowTranscriptMessage-visible") ??
            throw new InvalidOperationException("The human transcript card is missing.");
        var humanBubble = Find<Border>(workspace, "WorkflowHumanMessageBubble-visible") ??
            throw new InvalidOperationException("The human message bubble is missing.");
        var assistantCard = Find<Border>(workspace, "WorkflowTranscriptMessage-response") ??
            throw new InvalidOperationException("The assistant transcript card is missing.");
        Check(AutomationProperties.GetName(humanCard) == "Message from you" &&
              AutomationProperties.GetName(assistantCard) == "Message from Cerebral" &&
              !Descendants(assistantCard).OfType<Border>().Any(border =>
                  AutomationProperties.GetAutomationId(border).StartsWith("WorkflowHumanMessageBubble-", StringComparison.Ordinal)),
            "Accessible transcript cards distinguish the human bubble from the assistant's plain reply.");
        Check(ReferenceEquals(humanBubble.Background, Ui.RaisedSurface) &&
              ReferenceEquals(humanBubble.BorderBrush, Ui.Line) && humanBubble.Padding.Left >= 12 &&
              assistantCard.Background is null && humanCard.BorderThickness.Bottom == 1 &&
              assistantCard.BorderThickness.Bottom == 1,
            "Message surfaces use the live palette and subtle separators without boxing in the AI's reply.");
        foreach (var width in new[] { 320d, 680d, 1120d })
        {
            workspace.Measure(new Size(width, 480)); workspace.Arrange(new Rect(0, 0, width, 480)); workspace.UpdateLayout();
            var humanLeft = humanBubble.TranslatePoint(new Point(), transcriptScroller).X;
            var humanRight = humanLeft + humanBubble.ActualWidth;
            var assistantLeft = assistantCard.TranslatePoint(new Point(), transcriptScroller).X;
            Check(humanBubble.ActualWidth > 0 && humanLeft >= assistantLeft + 20 &&
                  humanRight <= transcriptScroller.ViewportWidth - 12 &&
                  assistantCard.ActualWidth <= transcriptScroller.ViewportWidth - 24 &&
                  transcriptScroller.ExtentWidth <= transcriptScroller.ViewportWidth + 1,
                $"At {width}px, human bubbles stay right-aligned, AI prose stays left, and wrapped messages fit the padded page.");
            Check(assistantCard.TranslatePoint(new Point(), transcriptScroller).Y >=
                  humanCard.TranslatePoint(new Point(), transcriptScroller).Y + humanCard.ActualHeight + 12 &&
                  humanCard.Padding.Bottom >= 14 && assistantCard.Padding.Bottom >= 14,
                $"At {width}px, message turns and their horizontal rules retain visible space without overlap.");
        }
        workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
        if (previewPath is not null)
        {
            workspace.Measure(new Size(980, 800)); workspace.Arrange(new Rect(0, 0, 980, 800)); workspace.UpdateLayout();
            transcriptScroller.ScrollToTop(); workspace.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(980, 800, 96, 96, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Ui.Surface, null, new Rect(0, 0, 980, 800));
            bitmap.Render(backdrop); bitmap.Render(workspace);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write)) encoder.Save(output);
            Check(File.Exists(previewPath) && new FileInfo(previewPath).Length > 0,
                "The same-run software preview contains the actual already-rendered synthetic human bubble, separated turns and assistant text.");
            workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
        }
        var humanText = Descendants(humanBubble).OfType<RichTextBox>().Single();
        humanText.SelectAll();
        Check(humanText.IsReadOnly && humanText.Selection.Text.Contains("VISIBLE REQUEST", StringComparison.Ordinal) &&
              humanText.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
              humanText.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "The bubble retains ordinary selectable, copyable message text without nested scrollbars.");
        humanText.Selection.Select(humanText.Document.ContentStart, humanText.Document.ContentStart);
        var messageBodies = Descendants(workspace).OfType<RichTextBox>().ToArray();
        workspace.UpdateLayout();
        Check(messageBodies.Length >= 2 && messageBodies.All(body => body.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                  body.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) && transcriptScroller.ScrollableHeight > 0,
            "Rendered messages must expand into one vertically scrollable conversation page without message-level scrollbars.");
        transcriptScroller.ScrollToVerticalOffset(Math.Min(90, transcriptScroller.ScrollableHeight));
        workspace.UpdateLayout();
        var heldOffset = transcriptScroller.VerticalOffset;
        var heldActivity = activity;
        Check(heldOffset > 0, "The long conversation fixture must establish a nonzero page position before refresh.");
        saved = saved! with { Revision = saved.Revision + 1, UpdatedAt = now.AddSeconds(1) };
        await workspace.RefreshAsync();
        await dispatcher.InvokeAsync(() => workspace.UpdateLayout(), DispatcherPriority.Loaded, deadline.Token);
        Check(Math.Abs(transcriptScroller.VerticalOffset - heldOffset) <= 1,
            "A background workflow refresh must preserve the user's conversation-page position.");
        await activityWork.RefreshAsync(); LayoutActivityWork();
        Check(ReferenceEquals(nativeActivity, Find<Button>(workspace, "WorkflowActivity")) &&
              ReferenceEquals(heldActivity, Find<Expander>(activityWork, "WorkflowActivity")) &&
              ReferenceEquals(namedSection, Find<StackPanel>(activityWork, "WorkflowActivitySection-call")) && operation.IsExpanded,
            "A revision-only refresh retains the same native summary and standalone inspected section and expanded operation.");
        // The next native request changes the shared fake head; this inspector must no longer poll that old selection.
        activitySource.RootVisual = null; activityWork.Dispose();
        await workspace.LoadEarlierMessagesAsync();
        await dispatcher.InvokeAsync(() => workspace.UpdateLayout(), DispatcherPriority.Loaded, deadline.Token);
        Check(workspace.Snapshot?.Events.Any(item => item.Id == "older-visible") == true,
            "The bounded workflow client must replace its event page with the requested older page.");
        Check(workspace.DisplayedEventIds.Contains("older-visible", StringComparer.Ordinal),
            "The chat projection must retain the requested older page alongside its recent rows.");
        Check(Descendants(workspace).OfType<RichTextBox>().Any(body =>
                new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text.Contains("OLDER TEMPORARY MESSAGE", StringComparison.Ordinal)),
            "Loading history must prepend the bounded older page to the current conversation projection.");
        transcriptScroller.ScrollToVerticalOffset(0); workspace.UpdateLayout();
        await dispatcher.InvokeAsync(() => workspace.UpdateLayout(), DispatcherPriority.Loaded, deadline.Token);
        transcriptScroller.ScrollToEnd();
        while (workspace.DisplayedEventIds.Contains("older-visible", StringComparer.Ordinal))
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(10, deadline.Token); }
        Check(workspace.Snapshot?.Events.FirstOrDefault()?.Id == "visible" &&
              Find<Button>(workspace, "WorkflowLoadEarlier") is { Visibility: Visibility.Visible },
            "Returning to the recent-message range releases temporary older rows while leaving durable history available to load again.");
        Check(Find<ListBox>(workspace, "WorkflowRecent")?.Visibility == Visibility.Collapsed && Find<Button>(workspace, "WorkflowStop") is { Visibility: Visibility.Visible, IsEnabled: true },
            "Embedded questions must expose a usable Stop while the outer composer is replaced by the form.");
        Check(Find<Button>(workspace, "WorkflowStop")!.IsDescendantOf(Find<Border>(workspace, "WorkflowQuestions")!),
            "Main's original Stop is physically inside the active question form, independent of the outer composer and attention strip.");
        var questionStop = Find<Button>(workspace, "WorkflowStop")!;
        var questionSubmit = Find<Button>(workspace, "WorkflowSubmitAnswers")!;
        Check(questionStop.MinHeight == 24 && questionSubmit.MinHeight == 24 &&
            questionStop.ActualHeight is >= 24 and <= 26 && questionSubmit.ActualHeight is >= 24 and <= 26 &&
            questionStop.VerticalAlignment == VerticalAlignment.Center && questionSubmit.VerticalAlignment == VerticalAlignment.Center,
            "Main question Stop and Submit keep the same callbacks in compact, fully laid out 24-DIP controls.");
        Check(questionMounts == 4 && Find<Border>(workspace, "WorkflowQuestions")?.Visibility == Visibility.Visible,
            "Each native text question must expose a plugin action mount whenever the active form is remounted.");
        Find<TextBox>(workspace, "WorkflowAnswer-first")!.Text = "One";
        Find<TextBox>(workspace, "WorkflowAnswer-second")!.Text = "Two";
        Find<Button>(workspace, "WorkflowSubmitAnswers")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (workspace.Snapshot?.Run?.Status != WorkflowStatus.Completed)
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(10, deadline.Token); }
        Check(Find<Border>(workspace, "WorkflowQuestions")?.Visibility == Visibility.Collapsed && questionResets >= 2 && errors.Count == 0,
            "An accepted answer must collapse the form and release its plugin text-entry mounts.");

        var second = submission with { RequestId = "request-two" };
        deferredSubmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatching = workspace.SubmitConversationAsync(second, deadline.Token);
        Check(workspace.MutationPending && workspace.Snapshot is { RunId: "request-two", Run: null, MutationUncertain: false },
            "The pre-receipt interval must be marked pending even before uncertainty or an accepted run exists.");
        var pendingCalls = calls.Count;
        try { workspace.BeginConversation("premature-new"); throw new InvalidDataException("Pending chat was discarded."); }
        catch (InvalidOperationException) { }
        try { await workspace.OpenRunAsync("premature-select"); throw new InvalidDataException("Pending chat was replaced."); }
        catch (InvalidOperationException) { }
        try { workspace.Reset(); throw new InvalidDataException("Pending request was reset."); }
        catch (InvalidOperationException) { }
        Check(calls.Count == pendingCalls && workspace.Snapshot?.RunId == "request-two" && workspace.MutationPending,
            "New, select and reset must reject synchronously without dropping or redirecting a pending plugin-origin request.");
        deferredSubmission.SetException(new IOException("Fixture disconnect after dispatch."));
        try { await dispatching; throw new InvalidOperationException("Expected uncertain dispatch."); }
        catch (IOException) { }
        deferredSubmission = null;
        Check(workspace.Snapshot is { RunId: "request-two", MutationUncertain: true }, "A disconnected submission must retain its exact request identity.");
        Check(attention.Visibility == Visibility.Visible &&
            Find<Button>(attention, "WorkflowRetrySubmission") is { Visibility: Visibility.Visible, IsEnabled: true } &&
            Find<Button>(attention, "WorkflowResume") is null,
            "Only the exact retained unaccepted request exposes its original Retry action in Main's attention view.");
        attention.Measure(new Size(680, 200)); attention.Arrange(new Rect(0, 0, 680, 200)); attention.UpdateLayout();
        Check(new[] { "WorkflowRefresh", "WorkflowRetrySubmission" }.All(id =>
            Find<Button>(attention, id) is { MinHeight: 24, ActualHeight: >= 24 and <= 26, VerticalAlignment: VerticalAlignment.Center } button &&
            button.Padding.Top <= 2 && button.Padding.Bottom <= 2),
            "Main's same workspace-owned Refresh and retained-request Retry are compact without shrinking standalone Work.");
        var beforeStaleAttentionClick = calls.Count; attentionCurrent = false;
        Find<Button>(attention, "WorkflowRefresh")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<Button>(attention, "WorkflowRetrySubmission")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls.Count == beforeStaleAttentionClick && workspace.Snapshot.RunId == "request-two",
            "An attention click from an obsolete host owner/selection cannot read or replay a retained request.");
        attentionCurrent = true;
        var before = calls.Count;
        try { workspace.BeginConversation("other"); throw new InvalidDataException("An uncertain conversation was discarded."); }
        catch (InvalidOperationException) { }
        try { await workspace.SubmitConversationAsync(second with { RequestId = "different" }, deadline.Token); throw new InvalidDataException("Different uncertain work was dispatched."); }
        catch (InvalidOperationException) { }
        try { await workspace.SubmitConversationAsync(second with { AssignmentText = "Changed private assignment" }, deadline.Token); throw new InvalidDataException("An edited retry was dispatched."); }
        catch (InvalidOperationException) { }
        Check(calls.Count == before && workspace.Snapshot?.RunId == "request-two", "Uncertain work must reject navigation, different IDs and edited retry bodies before dispatch.");
        failEvents = true;
        try { await workspace.SubmitConversationAsync(second, deadline.Token); throw new InvalidOperationException("Expected display failure."); }
        catch (IOException) { }
        Check(workspace.Snapshot is { MutationUncertain: false, RunId: "request-two" } && saved!.Submission.AssignmentText == "PRIVATE ASSIGNMENT",
            "An accepted identical retry must settle its ID even when only the subsequent event projection fails.");
        try { await workspace.CancelConversationAsync(deadline.Token); throw new InvalidOperationException("Expected display failure."); }
        catch (IOException) { }
        Check(workspace.Snapshot?.Run?.Status == WorkflowStatus.Cancelled && calls.All(x => x.Operation.StartsWith("jobs.workflow.", StringComparison.Ordinal) || x.Operation == "conversations.get"),
            "Accepted Stop must retain its confirmed cancellation through a projection failure and never use the legacy route.");
        failEvents = false;
        saved = saved! with { Revision = saved.Revision + 1, Status = WorkflowStatus.Waiting, CancelRequested = true,
            Effect = new("interrupted-compaction", "context.compact", 1, CerebralJson.Element(new { }),
                WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown), WaitingReason = "Reconcile interrupted compaction." };
        await workspace.RefreshAsync();
        Check(attention.Visibility == Visibility.Visible &&
            Find<Button>(attention, "WorkflowRefresh") is { IsEnabled: true } &&
            Find<Button>(attention, "WorkflowRetrySubmission")?.Visibility == Visibility.Collapsed &&
            Find<Button>(attention, "WorkflowResume") is null &&
            Find<Button>(attention, "WorkflowCheckOutcome") is null,
            "An accepted Unknown effect offers only read-only Refresh in Main: no Retry, Resume or unproven resolver.");
        await workspace.ReconcileConversationAsync(deadline.Token);
        Check(workspace.Snapshot?.Run is { Status: WorkflowStatus.Cancelled, EffectState: WorkflowEffectState.Failed },
            "Embedded Chat can settle an exact interrupted effect through its normal mutation gate.");
        using (var race = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
                   (message, error) => { if (error) errors.Add(message); }, embedded: true))
        {
            async Task OpenHistoryAsync()
            {
                race.BeginConversation("chat-fixture");
                await race.SubmitConversationAsync(submission, deadline.Token);
                Check(race.Snapshot?.HasOlder == true, "The history race requires a pending older page.");
            }
            static bool HistoryArmed(WorkflowWorkspace view) =>
                (bool)typeof(WorkflowWorkspace).GetField("historyReleaseArmed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(view)!;
            static object? HistoryBoundary(WorkflowWorkspace view) =>
                typeof(WorkflowWorkspace).GetField("historyReleaseOffset", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(view);
            static bool ReleasingHistory(WorkflowWorkspace view) =>
                (bool)typeof(WorkflowWorkspace).GetField("releasingHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(view)!;
            static void BeginRelease(WorkflowWorkspace view) =>
                typeof(WorkflowWorkspace).GetField("releasingHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(view, true);
            async Task PendingHistoryAsync(bool fail)
            {
                await OpenHistoryAsync();
                deferredOlderPage = new(TaskCreationOptions.RunContinuationsAsynchronously);
                olderPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = race.LoadEarlierMessagesAsync();
                await olderPageStarted.Task.WaitAsync(deadline.Token);
                race.BeginConversation(fail ? "after-error" : "after-success");
                if (fail) deferredOlderPage.SetException(new IOException("Obsolete history read failed."));
                else deferredOlderPage.SetResult(CommandResult.Ok(new
                {
                    events = olderEvents, snapshotSequence = 48L, nextBeforeSequence = (long?)null
                }));
                await pending;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, deadline.Token);
                Check(!race.IsReadingHistory && !HistoryArmed(race) && HistoryBoundary(race) is null &&
                      race.DisplayedEventIds.Count == 0,
                    "A late older-page result must not scroll, arm history release or render into a new conversation.");
                deferredOlderPage = null; olderPageStarted = null;
            }
            var errorCount = errors.Count;
            await PendingHistoryAsync(fail: false);
            await PendingHistoryAsync(fail: true);
            Check(errors.Count == errorCount, "A failed obsolete history read must not report against the new conversation.");
            await OpenHistoryAsync();
            deferredOlderPage = new(TaskCreationOptions.RunContinuationsAsynchronously);
            olderPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentFailure = race.LoadEarlierMessagesAsync();
            await olderPageStarted.Task.WaitAsync(deadline.Token);
            deferredOlderPage.SetException(new IOException("Current history read failed."));
            try { await currentFailure; throw new InvalidOperationException("Expected the current history read to fail."); }
            catch (IOException) { }
            Check(!race.IsReadingHistory && !HistoryArmed(race) && HistoryBoundary(race) is null,
                "A current failed older-page read must restore the pre-read history state.");
            deferredOlderPage = null; olderPageStarted = null;
            async Task PendingReleaseAsync(bool fail, bool selectRun)
            {
                await OpenHistoryAsync();
                await race.LoadEarlierMessagesAsync();
                Check(race.IsReadingHistory && HistoryArmed(race) && HistoryBoundary(race) is not null,
                    "The release race requires an armed older-page viewport.");
                deferredReleaseRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseRunStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginRelease(race); // The real ScrollChanged handler sets this before dispatching release.
                var release = (Task)typeof(WorkflowWorkspace)
                    .GetMethod("ReleaseEarlierAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(race, null)!;
                await releaseRunStarted.Task.WaitAsync(deadline.Token);
                var held = deferredReleaseRun;
                if (selectRun)
                {
                    deferredReleaseRun = null;
                    var selected = saved!;
                    var recentItem = new WorkflowRunSummary(selected.Id, selected.Submission.ConversationId,
                        selected.Submission.ProfileId, selected.Submission.UserText,
                        selected.Submission.DefinitionId, selected.Status, selected.Revision, selected.UpdatedAt);
                    await (Task)typeof(WorkflowWorkspace)
                        .GetMethod("OpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(race, [recentItem])!;
                }
                else race.BeginConversation(fail ? "after-release-error" : "after-release-success");
                if (fail) held!.SetException(new IOException("Obsolete history release failed."));
                else held!.SetResult(CommandResult.Ok(saved!));
                await release;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, deadline.Token);
                Check(!race.IsReadingHistory && !HistoryArmed(race) && !ReleasingHistory(race) &&
                      HistoryBoundary(race) is null &&
                      (selectRun ? race.Snapshot?.RunId == saved?.Id : race.DisplayedEventIds.Count == 0),
                    "A late history release must not restore scroll or history flags on the new selection.");
                deferredReleaseRun = null; releaseRunStarted = null;
            }
            await PendingReleaseAsync(fail: false, selectRun: false);
            await PendingReleaseAsync(fail: true, selectRun: false);
            await PendingReleaseAsync(fail: false, selectRun: true);
            await PendingReleaseAsync(fail: true, selectRun: true);
            Check(errors.Count == errorCount, "An obsolete history release failure must not report against the new conversation.");
        }
        checks += await VerifyLongActivityTranscriptAsync();
        checks += await VerifyServiceChildFollowAsync();
        checks += await VerifyPassiveInteractionRecoveryAsync();
        checks += await VerifyOptionalQuestionFormsAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, nativeChatChecks = checks,
            evidence = "deterministic native WPF controls and workflow adapter delegates", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
        return checks;
    }

    private static async Task<int> VerifyServiceChildFollowAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        const string conversationId = "service-child-chat", parentId = "completed-parent", childId = "plugin-tests-11111111111111111111111111111111";
        var now = DateTimeOffset.UtcNow;
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var parent = new WorkflowRun
        {
            Id = parentId, Submission = new(parentId, conversationId, "lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, "Build the requested plugin.", null,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject) { ConversationSequence = 4 },
            Revision = 1, Status = WorkflowStatus.Completed, CreatedAt = now, UpdatedAt = now
        };
        var child = parent with
        {
            Id = childId, Status = WorkflowStatus.Running,
            Submission = parent.Submission with { RequestId = childId, UserText = "Check the installed plugin.", ConversationSequence = 10,
                Caller = new(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests") },
            ActiveMilliseconds = 4_000
        };
        CerebralEvent Message(string id, string run, long sequence, string text, EventActor actor,
            HumanVisibility visibility = HumanVisibility.Transcript) =>
            new(id, conversationId, run, sequence, CerebralEventTypes.Message, actor, [new TextContentPart(text)],
                new(ModelInclusion.Required, visibility), new(EventSourceKind.Service), now);
        CerebralEvent GenerationCall(string id, string run, long sequence, string call) =>
            new(id, conversationId, run, sequence, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "lead", "Cerebral"), [], new(ModelInclusion.Required, HumanVisibility.Activity),
                new(EventSourceKind.Agent), now,
                CerebralJson.Element(new ToolCallData(call, "cerebral.host:generate", CerebralJson.Element(new { }))));
        CerebralEvent GenerationResult(string id, string run, long sequence, string call, int input, int output, long elapsed) =>
            new(id, conversationId, run, sequence, CerebralEventTypes.ToolResult, new(EventActorKind.Tool),
                [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                {
                    state = "Succeeded", data = new { prepared = new { inputTokens = input },
                        generation = new { generatedTokens = output }, elapsedMilliseconds = elapsed }
                }))], new(ModelInclusion.Evidence, HumanVisibility.Activity), new(EventSourceKind.Tool), now,
                CerebralJson.Element(new ToolResultData(call, "cerebral.host:generate", true, DurationMilliseconds: elapsed)));
        CerebralEvent Transition(string id, long sequence) =>
            new(id, conversationId, parentId, sequence, CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service), [],
                new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service), now,
                CerebralJson.Element(new { reason = "completed", status = "Completed" }));
        CerebralEvent[] parentEvents = [
            Message("retained-user-request", parentId, 5, parent.Submission.UserText, new(EventActorKind.User)),
            GenerationCall("parent-generation-call", parentId, 6, "parent-call"),
            GenerationResult("parent-generation-result", parentId, 7, "parent-call", 1000, 40, 10_000),
            Transition("parent-before-response", 8),
            Message("retained-agent-response", parentId, 9, "Plugin built. Running approved checks next.", new(EventActorKind.Agent)),
            Transition("parent-after-response", 10)
        ];
        CerebralEvent[] childEvents = [
            .. parentEvents,
            Message("accepted-child-request", childId, 11, child.Submission.UserText, child.Submission.Caller!, HumanVisibility.Activity),
            GenerationCall("child-generation-call", childId, 12, "child-generation"),
            GenerationResult("child-generation-result", childId, 13, "child-generation", 90, 10, 4_000),
            new("child-real-command", conversationId, childId, 14, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "lead", "Cerebral"), [], new(ModelInclusion.Required, HumanVisibility.Activity),
                new(EventSourceKind.Agent), now,
                CerebralJson.Element(new ToolCallData("child-call", "cerebral.host:tool",
                    CerebralJson.Element(new { name = "plugin.verify", arguments = new { manifest = @"C:\fixture\desktop-plugin.json" } })))),
            .. Enumerable.Range(15, 24).Select(sequence => Message("child-progress-" + sequence, childId, sequence,
                "Recorded child progress " + sequence, child.Submission.Caller!, HumanVisibility.Activity))
        ];
        var showChild = false;
        var showOlder = false;
        var foreignHead = false;
        var headReads = 0;
        var calls = new List<CommandRequest>();
        var inspectionCalls = new List<CommandRequest>();
        var errors = new List<string>();
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add(request);
            return Respond(request, token);
        }
        Task<CommandResult> Inspect(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); inspectionCalls.Add(request);
            return Respond(request, token);
        }
        Task<CommandResult> Respond(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(dispatcher.CheckAccess() && request.ProfileId is null, "Native head following keeps the signed-in owner UI route.");
            if (request.Operation == "jobs.workflow.recent") return Task.FromResult(EmptyRecentWorkPage(request));
            if (request.Operation == "jobs.workflow.get")
                return Task.FromResult(CommandResult.Ok(request.Arguments.GetProperty("id").GetString() == parentId ? parent : child));
            if (request.Operation == "conversations.get")
            {
                headReads++;
                Check(request.Arguments.GetProperty("id").GetString() == conversationId,
                    "Native child follow reads only the selected exact conversation head.");
                return Task.FromResult(CommandResult.Ok(new ConversationSummary(foreignHead ? "foreign-chat" : conversationId,
                    "Fixture", "lead", null, showChild ? childId : parentId, showChild ? child.Status : parent.Status, now)));
            }
            if (request.Operation == "jobs.workflow.events.previous")
            {
                var items = showChild ? childEvents : parentEvents;
                var earlier = request.Arguments.TryGetProperty("beforeSequence", out _);
                return Task.FromResult(CommandResult.Ok(new
                {
                    events = earlier ? new[] { Message("older-native-message", parentId, 1, "Older preserved message.", new(EventActorKind.User)) } : items,
                    snapshotSequence = items[^1].Sequence, nextBeforeSequence = earlier || !showOlder ? (long?)null : 5
                }));
            }
            throw new InvalidOperationException("Unexpected native child-follow route: " + request.Operation);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) errors.Add(message); }, embedded: true);
        using var presentationSource = new HwndSource(new HwndSourceParameters("Cerebral service activity checks")
        {
            Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000),
            PositionX = -32000, PositionY = -32000 // No WS_VISIBLE: this fixture has no visible window.
        }) { RootVisual = workspace };
        void Layout()
        {
            workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
        }
        async Task SettleAsync()
        {
            Layout();
            await dispatcher.InvokeAsync(Layout, DispatcherPriority.ContextIdle, deadline.Token);
            Layout();
        }
        await workspace.OpenRunSnapshotAsync(parentId, deadline.Token);
        await SettleAsync();
        Check(workspace.Snapshot?.RunId == parentId && workspace.DisplayedTranscript.Count == 2,
            "Native fixture opens the original completed conversation with both original transcript cards.");
        foreignHead = true;
        await workspace.RefreshFromConnectionAsync();
        Check(errors.Count == 1 && errors[0].Contains("foreign or invalid conversation head", StringComparison.Ordinal) &&
              workspace.Snapshot?.RunId == parentId && workspace.DisplayedTranscript.EventIds.SequenceEqual(
                  new[] { "retained-user-request", "retained-agent-response" }),
            "Foreign head reports an actionable native diagnostic and retains the original visible transcript.");
        foreignHead = false;
        showChild = true;
        await workspace.RefreshAsync();
        await SettleAsync();
        Check(workspace.Snapshot?.Run is { Id: childId, Status: WorkflowStatus.Running } &&
              workspace.DisplayedTranscript.Count == 2 && workspace.DisplayedTranscript.EventIds.SequenceEqual(
                  new[] { "retained-user-request", "retained-agent-response" }),
            "Native embedded Chat follows an accepted QA child without a synthetic user prompt or duplicated original transcript.");
        var nativeTranscriptScroller = Find<ScrollViewer>(workspace, "WorkflowTranscript") ??
            throw new InvalidOperationException("The native child-follow transcript is missing.");
        var nativePanel = ((StackPanel)nativeTranscriptScroller.Content).Children.OfType<StackPanel>().Single();
        var nativeSummaries = nativePanel.Children.OfType<Button>().Where(control =>
            AutomationProperties.GetAutomationId(control) == "WorkflowActivity").ToArray();
        Check(nativeSummaries.Length == 2 && Find<Expander>(workspace, "WorkflowActivity") is null &&
              Find<ScrollViewer>(workspace, "WorkflowActivityScroller") is null &&
              Find<Border>(workspace, "WorkflowActivityViewport") is null && Find<Button>(workspace, "WorkflowActivityHistory") is null,
            "Native child follow presents exactly two summary actions and no inline activity controls.");
        var nativeParentSummary = nativeSummaries[0]; var nativeChildSummary = nativeSummaries[1];
        var nativeResponse = Find<Border>(workspace, "WorkflowTranscriptMessage-retained-agent-response") ??
            throw new InvalidOperationException("The exact retained parent response card is missing.");
        Check(ReferenceEquals(nativeResponse.Parent, nativePanel) &&
              nativePanel.Children.IndexOf(nativeParentSummary) < nativePanel.Children.IndexOf(nativeResponse) &&
              nativePanel.Children.IndexOf(nativeChildSummary) > nativePanel.Children.IndexOf(nativeResponse) &&
              nativeParentSummary.Content is string parentSummary &&
              parentSummary.Contains("Completed 0:10", StringComparison.Ordinal) && parentSummary.Contains("1,000 in / 40 out", StringComparison.Ordinal) &&
              nativeChildSummary.Content is string childSummary && childSummary.Contains("90 in / 10 out", StringComparison.Ordinal) &&
              !childSummary.Contains("1,090", StringComparison.Ordinal),
            "Actual native summaries surround the same retained response card and keep each accepted turn's measured metrics.");
        var navigation = new List<ActivityTurnTarget>(); workspace.ActivityNavigationRequested += navigation.Add;
        var callsBeforeNavigation = calls.Count;
        nativeParentSummary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        nativeChildSummary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(navigation is [
                  { ConversationId: conversationId, RunId: parentId, FirstSequence: 6, LastSequence: 10 },
                  { ConversationId: conversationId, RunId: childId, FirstSequence: 11, LastSequence: 38 }] &&
              calls.Count == callsBeforeNavigation && workspace.Snapshot?.RunId == childId,
            "Each summary navigates only to its own exact recorded turn interval without changing the accepted head or dispatching work.");

        // The same journal is inspected through standalone Work; native head following stays on the embedded workspace.
        using var inspection = new WorkflowWorkspace(ReadOnlyInspectionCommand(Inspect), () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); });
        using var inspectionSource = new HwndSource(new HwndSourceParameters("Cerebral child Work inspection checks")
        { Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = inspection };
        async Task SettleInspectionAsync()
        {
            void LayoutInspection() { inspection.Measure(new Size(680, 480)); inspection.Arrange(new Rect(0, 0, 680, 480)); inspection.UpdateLayout(); }
            LayoutInspection(); await dispatcher.InvokeAsync(LayoutInspection, DispatcherPriority.ContextIdle, deadline.Token); LayoutInspection();
        }
        await inspection.OpenRunSnapshotAsync(childId, deadline.Token); await SettleInspectionAsync();
        Check(inspection.Snapshot?.RunId == childId && inspection.Snapshot?.ConversationId == conversationId &&
              inspection.Snapshot?.Run?.Revision == workspace.Snapshot?.Run?.Revision,
            "Read-only Work inspection starts from the same accepted child head and revision.");
        var viewports = inspection.ActivityViewportSnapshots;
        var parentViewport = viewports.Single(view => view.Key == "activity:retained-user-request");
        var childViewport = viewports.Single(view => view.Key == "activity:turn:" + childId);
        Check(viewports.Count == 2 && parentViewport.RenderedRowKeys.Contains("parent-generation-call") &&
              !parentViewport.RenderedRowKeys.Contains("child-real-command") && childViewport.RenderedRowKeys.Contains("child-real-command") &&
              !childViewport.RenderedRowKeys.Contains("parent-generation-call"),
            "Each accepted turn has its own stable standalone inspection branch and actual recorded commands.");
        Check(parentViewport.RenderedHeader.Contains("Completed 0:10", StringComparison.Ordinal) &&
              parentViewport.RenderedHeader.Contains("1,000 in / 40 out", StringComparison.Ordinal) &&
              childViewport.RenderedHeader.Contains("90 in / 10 out", StringComparison.Ordinal) &&
              !childViewport.RenderedHeader.Contains("1,090", StringComparison.Ordinal),
            "Parent and QA activity headers keep their own measured duration and token totals.");
        var childOnly = childEvents.Where(item => item.TurnId == childId).ToArray();
        var emptyProfiles = new Dictionary<string, string>();
        Check(WorkflowWorkspace.ActivityHeader(childOnly, workspace.Snapshot!.Run!, emptyProfiles, now.AddSeconds(3))
                  .Contains("Working 0:07", StringComparison.Ordinal) &&
              WorkflowWorkspace.ActivityHeader(childOnly, workspace.Snapshot.Run!, emptyProfiles, now.AddSeconds(4))
                  .Contains("Working 0:08", StringComparison.Ordinal),
            "The QA turn's smooth clock advances independently of the parent's completed work.");
        var transcriptScroller = Find<ScrollViewer>(inspection, "WorkflowTranscript")!;
        var panel = ((StackPanel)transcriptScroller.Content).Children.OfType<StackPanel>().Single();
        var activityControls = panel.Children.OfType<Expander>().Where(control =>
            AutomationProperties.GetAutomationId(control) == "WorkflowActivity").ToArray();
        var parentActivity = activityControls[0]; var childActivity = activityControls[1];
        var responseCard = Find<Border>(inspection, "WorkflowTranscriptMessage-retained-agent-response") ??
            throw new InvalidOperationException("The exact inspected parent response card is missing.");
        Check(ReferenceEquals(responseCard.Parent, panel) && panel.Children.IndexOf(parentActivity) < panel.Children.IndexOf(responseCard) &&
              panel.Children.IndexOf(childActivity) > panel.Children.IndexOf(responseCard) &&
              inspection.TranscriptViewportSnapshot.FollowingLatest,
            "QA inspection follows the exact completed parent response in the standalone Work transcript viewport.");
        var childScroller = Find<ScrollViewer>(childActivity, "WorkflowActivityScroller")!;
        var childDetail = Descendants(childActivity).OfType<Expander>().Single(control =>
            AutomationProperties.GetAutomationId(control) == "WorkflowActivityEntry-child-real-command");
        // Inspecting an older command enters its retained Review viewport.
        inspection.SetActivityDetailExpanded("child-real-command", true);
        childScroller.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        childScroller.ScrollToTop();
        await SettleInspectionAsync();
        Check(!inspection.ActivityViewportSnapshots.Single(view => view.Key == childViewport.Key).FollowingLatest,
            "Inspecting the QA branch pauses only its own follow state.");
        childEvents = [.. childEvents, Message("child-later-progress", childId, 39, "New recorded progress.",
            child.Submission.Caller!, HumanVisibility.Activity)];
        await workspace.RefreshAsync();
        await SettleAsync();
        await inspection.RefreshAsync(); await SettleInspectionAsync();
        var refreshedActivity = Descendants((DependencyObject)transcriptScroller.Content).OfType<Expander>().Where(control =>
            AutomationProperties.GetAutomationId(control) == "WorkflowActivity").ToArray();
        Check(ReferenceEquals(refreshedActivity[0], parentActivity) && ReferenceEquals(refreshedActivity[1], childActivity) &&
              childDetail.IsExpanded && childScroller.VerticalOffset <= 1 &&
              !inspection.ActivityViewportSnapshots.Single(view => view.Key == childViewport.Key).FollowingLatest,
            "Later QA events preserve both inspected branch identities, expanded command details and manual inspection position.");
        inspection.ResumeActivityFollowAfterOutsideClick(inspection);
        await SettleInspectionAsync();
        Check(inspection.ActivityViewportSnapshots.Single(view => view.Key == childViewport.Key).FollowingLatest &&
              childScroller.ScrollableHeight > 0 && Math.Abs(childScroller.VerticalOffset - childScroller.ScrollableHeight) <= 1,
            "Clicking outside resumes the QA branch at its latest operation without moving it above the parent response.");
        // A service result can be published after another accepted turn's activity. Both
        // operations must retain their original view and exact call/result identity.
        childEvents = [.. childEvents,
            GenerationCall("parent-late-call", parentId, 40, "interleaved-call"),
            GenerationCall("child-later-call", childId, 41, "interleaved-call"),
            GenerationResult("parent-late-result", parentId, 42, "interleaved-call", 5, 1, 1000),
            GenerationResult("child-later-result", childId, 43, "interleaved-call", 7, 2, 2000),
            Transition("parent-late-transition", 44)];
        await workspace.RefreshAsync();
        await SettleAsync();
        await inspection.RefreshAsync(); await SettleInspectionAsync();
        var interleavedViews = inspection.ActivityViewportSnapshots;
        refreshedActivity = Descendants((DependencyObject)transcriptScroller.Content).OfType<Expander>().Where(control =>
            AutomationProperties.GetAutomationId(control) == "WorkflowActivity").ToArray();
        Check(interleavedViews.Count == 2 && interleavedViews.Select(view => view.Key).Distinct(StringComparer.Ordinal).Count() == 2 &&
              ReferenceEquals(refreshedActivity[0], parentActivity) && ReferenceEquals(refreshedActivity[1], childActivity),
            "Interleaved accepted turns retain exactly one inserted inspection view per turn without duplicate keys or visual parents.");
        Check(Headers(parentActivity).Count(header => header.Contains("Completed · Generating response · 1.0 s", StringComparison.Ordinal)) == 1 &&
              Headers(childActivity).Count(header => header.Contains("Completed · Generating response · 2.0 s", StringComparison.Ordinal)) == 1 &&
              !Headers(parentActivity).Any(header => header.Contains("Generating response · 2.0 s", StringComparison.Ordinal)) &&
              !Headers(childActivity).Any(header => header.Contains("Generating response · 1.0 s", StringComparison.Ordinal)),
            "Interleaved operations with the same call identifier pair only within their exact accepted turn.");
        Check(interleavedViews.Single(view => view.Key == parentViewport.Key).RenderedHeader.Contains("1,005 in / 41 out", StringComparison.Ordinal) &&
              interleavedViews.Single(view => view.Key == childViewport.Key).RenderedHeader.Contains("97 in / 12 out", StringComparison.Ordinal),
            "Late interleaved receipts update only their own turn's measured token totals.");
        await workspace.RefreshAsync();
        await SettleAsync();
        await inspection.RefreshAsync(); await SettleInspectionAsync();
        Check(inspection.ActivityViewportSnapshots.Count == 2 && childDetail.IsExpanded &&
              inspection.ActivityViewportSnapshots.Single(view => view.Key == childViewport.Key).FollowingLatest,
            "Rerendering interleaved turns keeps expansion and following state without recreating or duplicating inspected branches.");
        Check(ReferenceEquals(nativeParentSummary, nativePanel.Children.OfType<Button>().First(control =>
                  AutomationProperties.GetAutomationId(control) == "WorkflowActivity")) &&
              ReferenceEquals(nativeChildSummary, nativePanel.Children.OfType<Button>().Last(control =>
                  AutomationProperties.GetAutomationId(control) == "WorkflowActivity")) &&
              ((string)nativeParentSummary.Content).Contains("1,005 in / 41 out", StringComparison.Ordinal) &&
              ((string)nativeChildSummary.Content).Contains("97 in / 12 out", StringComparison.Ordinal),
            "Native summary identities and exact per-turn metrics survive late interleaved receipts.");
        Check(calls.Count > 0 && calls.All(call => call.Operation is "jobs.workflow.get" or "jobs.workflow.events.previous" or "conversations.get"),
            "Automatic native child follow performs only its exact bounded owner reads and never queries unrelated Recent lists or replays work.");
        Check(inspectionCalls.Count > 0 && inspectionCalls.Any(call => call.Operation == "jobs.workflow.recent") &&
              inspectionCalls.All(call => call.ProfileId is null &&
                  call.Operation is "jobs.workflow.get" or "jobs.workflow.events.previous" or "conversations.get" or "jobs.workflow.recent"),
            "The separate read-only Work stream uses only bound owner reads, including the exact recent page validated by its strict responder.");
        inspectionSource.RootVisual = null; inspection.Dispose();

        // A fresh terminal selection can inspect older history even when the authoritative head is newer.
        using var historyWorkspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); }, embedded: true);
        showChild = false;
        await historyWorkspace.OpenRunSnapshotAsync(parentId, deadline.Token);
        showOlder = true;
        await historyWorkspace.RefreshAsync();
        await historyWorkspace.LoadEarlierMessagesAsync();
        showChild = true;
        var previousHeadReads = headReads;
        await historyWorkspace.RefreshAsync();
        Check(historyWorkspace.IsReadingHistory && historyWorkspace.Snapshot?.RunId == parentId && headReads == previousHeadReads,
            "Native older-history inspection keeps the selected parent run and skips automatic head adoption.");
        return checks;
    }

    private static async Task<int> VerifyLongActivityTranscriptAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        const string conversationId = "long-activity-chat";
        const string runId = "long-activity-request";
        const string originalId = "original-request-event";
        const string originalText = "ORIGINAL LONG-ACTIVITY REQUEST";
        const string hiddenText = "PRIVATE LONG-ACTIVITY ASSIGNMENT";
        var now = DateTimeOffset.UnixEpoch;
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        CerebralEvent Item(string id, long sequence, string text, HumanVisibility human,
            string type = CerebralEventTypes.Message, EventActorKind actor = EventActorKind.User) =>
            new(id, conversationId, runId, sequence, type, new(actor), [new TextContentPart(text)],
                new(ModelInclusion.Required, human), new(actor == EventActorKind.User ? EventSourceKind.User : EventSourceKind.Service), now);
        var original = Item(originalId, 1, originalText, HumanVisibility.Transcript);
        var hidden = Item("private-assignment-event", 2, hiddenText, HumanVisibility.Hidden);
        IReadOnlyList<CerebralEvent> journal = [original, hidden,
            .. Enumerable.Range(3, 1039).Select(sequence => Item("activity-" + sequence, sequence, "Working",
                HumanVisibility.Activity, CerebralEventTypes.ServiceUpdate, EventActorKind.Service))];
        var endSequence = 3L;
        var cancellationSequence = 777L;
        var saved = new WorkflowRun
        {
            Id = runId,
            Submission = new(runId, conversationId, "lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, originalText, hiddenText,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, WorkflowContract.MaximumSteps),
            Revision = 1, Status = WorkflowStatus.Running, CreatedAt = now, UpdatedAt = now
        };
        var pageReads = 0;
        var pinnedReads = 0;
        var originalReads = 0;
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(dispatcher.CheckAccess() && request.ProfileId is null,
                "Long-activity diagnostics must use the owner UI route without profile impersonation.");
            if (request.Operation == "jobs.workflow.recent") return Task.FromResult(EmptyRecentWorkPage(request));
            if (request.Operation == "jobs.workflow.submit") return Task.FromResult(CommandResult.Ok(saved));
            if (request.Operation == "conversations.get")
            {
                Check(request.Arguments.GetProperty("id").GetString() == conversationId,
                    "Ordinary Chat head refresh retains the exact conversation.");
                return Task.FromResult(CommandResult.Ok(new ConversationSummary(conversationId, "Fixture",
                    saved.Submission.ProfileId, saved.Submission.ProjectId, saved.Id, saved.Status, saved.UpdatedAt)));
            }
            Check(request.Arguments.GetProperty("id").GetString() == runId,
                "Long-activity reads and cancellation must retain the selected exact run.");
            if (request.Operation == "jobs.workflow.get") return Task.FromResult(CommandResult.Ok(saved));
            if (request.Operation == "jobs.workflow.cancel")
            {
                endSequence = cancellationSequence;
                saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Cancelled, CancelRequested = true,
                    Steps = cancellationSequence == 777 ? 129 : 173 };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (request.Operation == "jobs.workflow.events")
            {
                originalReads++;
                var limit = request.Arguments.GetProperty("limit").GetInt32();
                var after = request.Arguments.GetProperty("afterSequence").GetInt64();
                var pinned = request.Arguments.GetProperty("snapshotSequence").GetInt64();
                Check(limit == 1 && after == saved.Submission.ConversationSequence && pinned == endSequence,
                    "Canonical original-request recovery must read one forward event at the accepted run boundary and pinned snapshot.");
                return Task.FromResult(CommandResult.Ok(new
                {
                    events = new[] { original }, snapshotSequence = pinned, nextAfterSequence = (long?)original.Sequence
                }));
            }
            if (request.Operation == "jobs.workflow.events.previous")
            {
                pageReads++;
                var limit = request.Arguments.GetProperty("limit").GetInt32();
                var pinned = request.Arguments.TryGetProperty("snapshotSequence", out var snapshot);
                var at = pinned ? snapshot.GetInt64() : endSequence;
                if (pinned) pinnedReads++;
                Check(limit == WorkflowConversationClient.MaximumPageEvents && at <= endSequence,
                    "Each long-activity read must remain one bounded event page at an existing snapshot.");
                var before = request.Arguments.TryGetProperty("beforeSequence", out var cursor) ? cursor.GetInt64() : long.MaxValue;
                var candidates = journal.Where(item => item.Sequence < before && item.Sequence <= at).ToArray();
                var page = candidates.TakeLast(limit).ToArray();
                return Task.FromResult(CommandResult.Ok(new
                {
                    events = page, snapshotSequence = at,
                    nextBeforeSequence = candidates.Length > page.Length ? (long?)page[0].Sequence : null
                }));
            }
            throw new InvalidOperationException("Unexpected long-activity fixture route: " + request.Operation);
        }
        using var nativeWorkspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); }, embedded: true);
        var workspace = nativeWorkspace;
        void CheckOriginal(string phase)
        {
            workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
            var diagnostic = workspace.DisplayedTranscript;
            Check(diagnostic.Count == 1 && diagnostic.EventIds.SequenceEqual([originalId]) && !diagnostic.Truncated,
                phase + ": actual transcript diagnostics must identify the original card exactly once.");
            var bodies = Descendants(workspace).OfType<RichTextBox>()
                .Select(body => new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text).ToArray();
            Check(bodies.Count(text => text.Contains(originalText, StringComparison.Ordinal)) == 1 &&
                  bodies.All(text => !text.Contains(hiddenText, StringComparison.Ordinal)) &&
                  !Text(workspace).Contains(hiddenText, StringComparison.Ordinal),
                phase + ": the visual transcript must retain one original request and omit the private assignment.");
            var maximumRaw = WorkflowConversationClient.MaximumPageEvents * WorkflowConversationClient.MaximumRecentPages;
            var expectedRaw = Math.Min(endSequence, maximumRaw);
            Check(workspace.RecentWindowEventCount == expectedRaw && workspace.RetainedTranscriptEventCount == 1 &&
                  workspace.DisplayedEventIds.Count == expectedRaw + (endSequence > maximumRaw ? 1 : 0) &&
                  workspace.DisplayedEventIds.Distinct(StringComparer.Ordinal).Count() == workspace.DisplayedEventIds.Count &&
                  workspace.DisplayedEventIds.Count <= maximumRaw + WorkflowWorkspace.MaximumRetainedTranscriptEvents,
                phase + ": transcript retention must keep the raw activity cache and semantic card cache independently bounded.");
            var serialized = JsonSerializer.Serialize(diagnostic);
            Check(!serialized.Contains(originalText, StringComparison.Ordinal) && !serialized.Contains(hiddenText, StringComparison.Ordinal),
                phase + ": transcript diagnostics must contain canonical IDs and counts without message text.");
        }
        workspace.BeginConversation(conversationId);
        await workspace.SubmitConversationAsync(new(runId, "lead", ConversationControlClient.ChatDefinitionId,
            ConversationControlClient.ChatDefinitionVersion, originalText, WorkflowContract.EmptyObject, hiddenText,
            WorkflowContract.MaximumSteps), deadline.Token);
        CheckOriginal("Initial submission");
        foreach (var end in new[] { 200L, 400L, 600L, 776L })
        {
            endSequence = end; saved = saved with { Revision = saved.Revision + 1 };
            await workspace.RefreshAsync();
            CheckOriginal("Active refresh " + end);
            if (end > 200)
                Check(workspace.Snapshot?.Transcript.Any() == false && workspace.DisplayedTranscript.Count == 1,
                    "An activity-only newest service page must not be reported as an empty rendered transcript.");
        }
        await workspace.CancelConversationAsync(deadline.Token);
        Check(workspace.Snapshot?.Run?.Status == WorkflowStatus.Cancelled && workspace.Snapshot.Events.Count == 200 &&
              !workspace.Snapshot.Transcript.Any(), "The 777-event cancellation fixture must reproduce the activity-only latest page.");
        CheckOriginal("Cancelled");
        for (var refresh = 0; refresh < 3; refresh++)
        {
            await workspace.RefreshAsync();
            CheckOriginal("Repeated settled refresh " + refresh);
        }
        workspace.Reset();
        Check(workspace.DisplayedTranscript is { Count: 0, Truncated: false } empty && empty.EventIds.Count == 0,
            "Reset must clear diagnostic IDs together with the native visual tree.");
        var readsBeforeReopen = pageReads; var pinsBeforeReopen = pinnedReads;
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        Check(pageReads - readsBeforeReopen == 4 && pinnedReads - pinsBeforeReopen == 3 &&
              workspace.Snapshot?.Events.Count == 777,
            "Reopening the 777-event cancelled run must use exactly four bounded pages and one pinned history snapshot.");
        CheckOriginal("Reopened");

        // Reproduce the live v10 boundary: the original request is older than all 800 recent raw events.
        workspace.BeginConversation(conversationId);
        endSequence = 3; cancellationSequence = 1041;
        saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Running, CancelRequested = false, Steps = 0 };
        await workspace.SubmitConversationAsync(new(runId, "lead", ConversationControlClient.ChatDefinitionId,
            ConversationControlClient.ChatDefinitionVersion, originalText, WorkflowContract.EmptyObject, hiddenText,
            WorkflowContract.MaximumSteps), deadline.Token);
        foreach (var end in new[] { 200L, 400L, 600L, 800L, 1000L, 1040L })
        {
            endSequence = end; saved = saved with { Revision = saved.Revision + 1 };
            await workspace.RefreshAsync();
            CheckOriginal("Beyond raw window " + end);
        }
        await workspace.CancelConversationAsync(deadline.Token);
        CheckOriginal("Cancelled beyond raw window");
        var readsBeforeSettledRefresh = originalReads;
        for (var refresh = 0; refresh < 3; refresh++)
        {
            await workspace.RefreshAsync();
            CheckOriginal("Repeated beyond-window refresh " + refresh);
        }
        Check(originalReads == readsBeforeSettledRefresh && workspace.Snapshot?.Transcript.Any() == false && workspace.DisplayedTranscript.Count == 1 &&
              workspace.DisplayedEventIds.Contains(originalId, StringComparer.Ordinal),
            "The observed original card must survive 1041 events even though the current 200-event page is entirely activity.");

        // Cold reopen keeps raw paging truthful and retrieves the exact original through one pinned forward read.
        workspace.Reset();
        readsBeforeReopen = pageReads; pinsBeforeReopen = pinnedReads;
        var originalsBeforeReopen = originalReads;
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        Check(pageReads - readsBeforeReopen == 4 && pinnedReads - pinsBeforeReopen == 3 &&
              originalReads - originalsBeforeReopen == 1 &&
              workspace.Snapshot is { HasOlder: true, OriginalMessage.Id: originalId } cold && cold.Events.Count == 800 &&
              cold.Events[0].Sequence == 242 && !cold.Events.Any(item => item.Id == originalId),
            "Cold reopen must recover one canonical original separately from the four raw pages without corrupting history cursors.");
        CheckOriginal("Cold reopened beyond raw window");

        // A separate short corpus exercises diagnostic bounds and the renderer's special transcript exclusions.
        journal = [original, hidden, .. Enumerable.Range(3, 44).Select(sequence =>
                Item("message-" + sequence, sequence, "Visible message " + sequence, HumanVisibility.Transcript)),
            Item("sidebar-event", 47, "Sidebar", HumanVisibility.Sidebar),
            Item("transition-event", 48, "Transition", HumanVisibility.Transcript, CerebralEventTypes.WorkflowTransition, EventActorKind.Service),
            Item("questions-event", 49, "Questions", HumanVisibility.Transcript, "workflow.questions", EventActorKind.Service)];
        endSequence = 49;
        workspace.Reset();
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        var bounded = workspace.DisplayedTranscript;
        Check(workspace.RetainedTranscriptEventCount == WorkflowWorkspace.MaximumRetainedTranscriptEvents &&
              bounded.Count == 45 && bounded.EventIds.Count == WorkflowWorkspace.MaximumTranscriptDiagnosticIds && bounded.Truncated &&
              bounded.EventIds.Distinct(StringComparer.Ordinal).Count() == bounded.EventIds.Count,
            "Transcript diagnostics must count all rendered cards while returning at most 40 unique event IDs.");
        Check(bounded.EventIds.SequenceEqual(journal.Where(item => item.Projection.Human == HumanVisibility.Transcript &&
                item.Type is not CerebralEventTypes.WorkflowTransition and not "workflow.questions").Take(40).Select(item => item.Id)),
            "Diagnostic IDs must match card rendering and exclude hidden, sidebar, activity, transition and active-question entries.");
        journal = [.. journal, .. Enumerable.Range(50, 992).Select(sequence =>
            Item("later-activity-" + sequence, sequence, "Working", HumanVisibility.Activity,
                CerebralEventTypes.ServiceUpdate, EventActorKind.Service))];
        foreach (var end in new[] { 249L, 449L, 649L, 849L, 1041L })
        {
            endSequence = end; saved = saved with { Revision = saved.Revision + 1 };
            await workspace.RefreshAsync();
        }
        var retained = workspace.DisplayedTranscript;
        Check(workspace.RecentWindowEventCount == 800 &&
              workspace.RetainedTranscriptEventCount == WorkflowWorkspace.MaximumRetainedTranscriptEvents &&
              workspace.DisplayedEventIds.Count == 800 + WorkflowWorkspace.MaximumRetainedTranscriptEvents &&
              retained.Count == WorkflowWorkspace.MaximumRetainedTranscriptEvents &&
              retained.EventIds.Contains(originalId, StringComparer.Ordinal) && retained.EventIds.Contains("message-46", StringComparer.Ordinal),
            "After many visible messages and more than 800 activity events, retention must pin the original and recent cards while keeping both caches bounded.");

        // Native semantic retention/paging/cancellation above remains embedded. Inline scrolling belongs
        // to a separate read-only Work view over the same deterministic journal; no hardware input is claimed.
        journal = [original, hidden, .. Enumerable.Range(3, 118).Select(sequence =>
            Item("scroll-activity-" + sequence, sequence, "Activity detail " + sequence + "\nSelectable operation evidence.",
                HumanVisibility.Activity, CerebralEventTypes.ServiceUpdate, EventActorKind.Service))];
        endSequence = 32;
        saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Cancelled, CancelRequested = true };
        workspace.Reset();
        using var inlineWork = new WorkflowWorkspace(ReadOnlyInspectionCommand(Dispatch), () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); });
        workspace = inlineWork;
        using var presentationSource = new HwndSource(new HwndSourceParameters("Cerebral activity scroll checks")
        {
            Width = 680, Height = 480,
            WindowStyle = unchecked((int)0x80000000), // No WS_VISIBLE: the fixture never opens a user window.
            PositionX = -32000, PositionY = -32000
        }) { RootVisual = workspace };
        var activityLayoutWidth = 680d;
        void LayoutActivity()
        {
            workspace.Measure(new Size(activityLayoutWidth, 480)); workspace.Arrange(new Rect(0, 0, activityLayoutWidth, 480)); workspace.UpdateLayout();
        }
        async Task SettleActivityAsync()
        {
            deadline.Token.ThrowIfCancellationRequested();
            LayoutActivity();
            await dispatcher.InvokeAsync(LayoutActivity, DispatcherPriority.ContextIdle, deadline.Token);
            LayoutActivity();
        }
        ScrollViewer ActivityScroller() =>
            Find<ScrollViewer>(workspace, "WorkflowActivityScroller") ??
            throw new InvalidOperationException("The activity scrolling fixture requires a rendered native viewport.");
        WorkflowActivityViewportSnapshot ActivityPosition()
        {
            var positions = workspace.ActivityViewportSnapshots;
            Check(positions.Count == 1 && positions[0].Key == "activity:" + originalId,
                "Activity scroll diagnostics must describe the exact original-request activity branch.");
            return positions[0];
        }
        void CheckAtLatest(string phase)
        {
            var viewport = ActivityScroller();
            var position = ActivityPosition();
            Check(viewport.ScrollableHeight >= 0 && viewport.VerticalOffset >= 0 &&
                  Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) <= 1 &&
                  position.FollowingLatest && Math.Abs(position.VerticalOffset - viewport.VerticalOffset) <= 1 &&
                  Math.Abs(position.ScrollableHeight - viewport.ScrollableHeight) <= 1,
                phase + $": the native activity viewport must follow its newest visible entry in pixel coordinates (offset={viewport.VerticalOffset}, scrollable={viewport.ScrollableHeight}, following={position.FollowingLatest}, diagnosticOffset={position.VerticalOffset}, diagnosticScrollable={position.ScrollableHeight}).");
        }
        void Wheel(ScrollViewer viewport)
        {
            viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        }
        void Home(ScrollViewer viewport)
        {
            viewport.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, Key.Home)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        async Task AdvanceActivityAsync(long end)
        {
            endSequence = end; saved = saved with { Revision = saved.Revision + 1 };
            await workspace.RefreshAsync();
        }

        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await SettleActivityAsync();
        // The default rolling window can fit without scrolling. Exercise the retained
        // history viewport with a Review click and the normal outside-click follow action before the
        // existing long-history pixel-offset/follow/manual-position assertions.
        var reviewActivity = Find<Button>(workspace, "WorkflowActivityHistory") ??
            throw new InvalidOperationException("The long activity fixture requires its review control.");
        reviewActivity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleActivityAsync();
        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        Check(ActivityScroller().ScrollableHeight > 0 && ActivityScroller().VerticalOffset > 0,
            "The retained long-history fixture must actually overflow and exercise native pixel scrolling.");
        CheckAtLatest("Initial native activity load");
        Check(!ActivityScroller().CanContentScroll,
            "Activity offsets must use pixels rather than logical StackPanel item coordinates.");
        var positionJson = JsonSerializer.Serialize(workspace.ActivityViewportSnapshots, CerebralJson.Options);
        Check(!positionJson.Contains("Activity detail", StringComparison.Ordinal) &&
              !positionJson.Contains("Selectable operation evidence", StringComparison.Ordinal) &&
              !positionJson.Contains(hiddenText, StringComparison.Ordinal) &&
              !positionJson.Contains(originalText, StringComparison.Ordinal),
            "Activity viewport diagnostics must expose scroll intent and numeric positions without message bodies.");

        var initialViewport = ActivityScroller();
        await AdvanceActivityAsync(48);
        await SettleActivityAsync();
        Check(ReferenceEquals(initialViewport, ActivityScroller()),
            "New activity must retain the same native viewport instead of resetting its reader controls.");
        CheckAtLatest("Refresh from a nonzero bottom");

        Wheel(ActivityScroller());
        ActivityScroller().ScrollToTop();
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && ActivityScroller().VerticalOffset <= 1,
            "Deliberate review of the oldest activity must pause follow-latest.");
        await AdvanceActivityAsync(64);
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && ActivityScroller().VerticalOffset <= 1 &&
              ActivityScroller().ScrollableHeight > 0,
            "A refresh must preserve the owner's top position while older activity is being reviewed.");

        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        CheckAtLatest("Outside click resumes activity");

        var detail = Descendants(ActivityScroller()).OfType<Expander>().Last();
        detail.IsExpanded = true;
        await SettleActivityAsync();
        var selectable = Descendants(detail).OfType<RichTextBox>().Single();
        selectable.SelectAll();
        Check(!selectable.Selection.IsEmpty && !ActivityPosition().FollowingLatest,
            "Selecting the actual rendered activity RichTextBox must pause live follow.");
        workspace.ResumeActivityFollowAfterOutsideClick(selectable);
        var inlineRun = selectable.Document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>()).First();
        workspace.ResumeActivityFollowAfterOutsideClick(inlineRun);
        var linkRun = new Run("Fixture reference");
        var inlineLink = new Hyperlink(linkRun);
        var linkParagraph = (Paragraph)inlineRun.Parent;
        linkParagraph.Inlines.Add(inlineLink);
        workspace.ResumeActivityFollowAfterOutsideClick(linkRun);
        linkParagraph.Inlines.Remove(inlineLink);
        await SettleActivityAsync();
        Check(!selectable.Selection.IsEmpty && !ActivityPosition().FollowingLatest,
            "A click inside activity text must preserve selection and review intent.");
        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        Check(selectable.Selection.IsEmpty,
            "An outside click must clear only activity text selection before following new entries.");
        CheckAtLatest("Outside click after activity selection");
        selectable.SelectAll();
        await AdvanceActivityAsync(80);
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && !selectable.Selection.IsEmpty &&
              ReferenceEquals(selectable, Descendants(detail).OfType<RichTextBox>().Single()),
            "A refresh must retain paused review intent and the exact selected detail control after new activity.");

        Wheel(ActivityScroller());
        ActivityScroller().ScrollToVerticalOffset(70);
        await SettleActivityAsync();
        var pausedOffset = ActivityScroller().VerticalOffset;
        Check(pausedOffset > 1 && pausedOffset < ActivityScroller().ScrollableHeight - 16 &&
              !ActivityPosition().FollowingLatest,
            "The rapid-refresh case requires a genuine paused nonzero native offset.");
        await AdvanceActivityAsync(96);
        await AdvanceActivityAsync(104);
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && Math.Abs(ActivityScroller().VerticalOffset - pausedOffset) <= 1,
            "Two refreshes before deferred layout must preserve the prior review offset rather than capture a temporary zero.");

        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        Wheel(ActivityScroller());
        ActivityScroller().ScrollToVerticalOffset(25);
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && Math.Abs(ActivityScroller().VerticalOffset - 25) <= 1,
            "A newer routed wheel input must supersede a queued outside-click follow operation.");

        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        CheckAtLatest("Before deferred key review");
        await AdvanceActivityAsync(112);
        LayoutActivity();
        Home(ActivityScroller());
        ActivityScroller().ScrollToTop();
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && ActivityScroller().VerticalOffset <= 1,
            "A newer routed Home key must supersede the pending refresh restoration.");

        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        CheckAtLatest("Before deferred wheel review");
        await AdvanceActivityAsync(120);
        LayoutActivity();
        Wheel(ActivityScroller());
        ActivityScroller().ScrollToTop();
        await SettleActivityAsync();
        Check(!ActivityPosition().FollowingLatest && ActivityScroller().VerticalOffset <= 1,
            "A newer routed wheel input must supersede the pending refresh restoration.");

        // Pair a command with its late receipt without replacing the selected command detail.
        var pairCall = new CerebralEvent("paired-command", conversationId, runId, 3, CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent, "builder", "Builder"), [], new(ModelInclusion.Required, HumanVisibility.Activity),
            new(EventSourceKind.Agent, AgentId: "builder"), now,
            CerebralJson.Element(new ToolCallData("paired-call", "cerebral.host:tool", CerebralJson.Element(new
            {
                name = "process.run",
                arguments = new { executableId = "dotnet", arguments = new[] { "build", @"C:\Fixture\Fixture.csproj" } }
            }))));
        var pairResult = new CerebralEvent("paired-result", conversationId, runId, 4, CerebralEventTypes.ToolResult,
            new(EventActorKind.Tool), [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
            {
                state = "Failed", error = "Fixture compiler failure",
                data = new { content = JsonSerializer.Serialize(new { exitCode = 1, stderr = "error CS0246: MissingFixture" }) }
            }))], new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), now.AddMilliseconds(45),
            CerebralJson.Element(new ToolResultData("paired-call", "cerebral.host:tool", false, DurationMilliseconds: 45)));
        journal = [original, hidden, pairCall, pairResult];
        endSequence = 3; saved = saved with { Revision = saved.Revision + 1 };
        workspace.Reset();
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await SettleActivityAsync();
        var pairViewport = ActivityScroller();
        var pairRow = Descendants(pairViewport).OfType<Expander>().Single();
        var pairInline = Descendants(pairRow).OfType<TextBlock>().Single(text =>
            AutomationProperties.GetAutomationId(text) == "WorkflowActivityInlineDetail");
        Check(!pairRow.IsExpanded && pairInline.IsVisible && pairInline.ActualHeight > 0 &&
              pairInline.Text == @"Operation: Executable: dotnet | Arguments: build C:\Fixture\Fixture.csproj",
            "A collapsed pending row must visibly expose its plain-text command target.");
        pairRow.MaxWidth = 260;
        await SettleActivityAsync();
        Check(pairInline.ActualWidth <= 260 && pairInline.ActualHeight > 24,
            "A constrained activity row wraps its bounded operation detail instead of clipping the target horizontally.");
        pairRow.ClearValue(FrameworkElement.MaxWidthProperty);
        workspace.SetActivityDetailExpanded(pairCall.Id, true);
        await SettleActivityAsync();
        var pairCommandText = Descendants(pairRow).OfType<TextBox>().Single();
        pairViewport.ScrollToEnd();
        await SettleActivityAsync();
        pairCommandText.SelectAll();
        Check(!ActivityPosition().FollowingLatest && pairViewport.VerticalOffset == pairViewport.ScrollableHeight,
            "Selecting command details at the viewport bottom must retain deliberate inspection.");
        await AdvanceActivityAsync(4);
        await SettleActivityAsync();
        var pairedPosition = ActivityPosition();
        Check(ReferenceEquals(pairInline, Descendants(pairRow).OfType<TextBlock>().Single(text =>
                  AutomationProperties.GetAutomationId(text) == "WorkflowActivityInlineDetail")) &&
              pairInline.Text == "Result: Error output: error CS0246: MissingFixture" &&
              !pairedPosition.FollowingLatest,
            "The existing inline control must update to the real late failure without resuming deliberate inspection.");
        Check(ReferenceEquals(pairViewport, ActivityScroller()) &&
              ReferenceEquals(pairRow, Descendants(pairViewport).OfType<Expander>().Single()) &&
              pairRow.IsExpanded && pairCommandText.SelectionLength > 0 &&
              Descendants(pairRow).OfType<TextBox>().Contains(pairCommandText),
            "A late terminal receipt must extend one operation row without replacing its selected command details.");
        Check(pairRow.Header is string pairHeader && pairHeader.Contains("Builder · Failed", StringComparison.Ordinal) &&
              pairHeader.Contains("45 ms", StringComparison.Ordinal) && Text(pairRow).Contains("CS0246", StringComparison.Ordinal) &&
              pairedPosition.RenderedRowCount == 1 && pairedPosition.RenderedRowKeys.SequenceEqual([pairCall.Id]) &&
              workspace.DisplayedActivity.Count == 2,
            "The grouped failure must expose its actual diagnostic and measured time while source-event diagnostics remain compatible.");
        pairViewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseDownEvent });
        await SettleActivityAsync();
        Check(ActivityPosition().FollowingLatest && pairCommandText.SelectionLength == 0 &&
              pairViewport.VerticalOffset == pairViewport.ScrollableHeight,
            "A blank viewport click must resume following and clear activity selection even at the exact bottom boundary.");
        workspace.SetActivityDetailExpanded(pairResult.Id, false);
        await SettleActivityAsync();
        Check(!pairRow.IsExpanded, "Both source event IDs must address the same grouped operation detail.");
        Check(pairInline.IsVisible && pairInline.ActualHeight > 0 &&
              pairInline.Text == "Result: Error output: error CS0246: MissingFixture",
            "Recorded failure detail remains visible after its full receipt is collapsed.");

        // The outer chat scroller must distinguish layout clamping from new user input too.
        journal = [original, hidden, .. Enumerable.Range(3, 12).Select(sequence =>
                Item("outer-message-" + sequence, sequence,
                    "Transcript card " + sequence + "\nFirst visible line.\nSecond visible line.\nThird visible line.",
                    HumanVisibility.Transcript)),
            .. Enumerable.Range(15, 160).Select(sequence =>
                Item("outer-activity-" + sequence, sequence, "Outer scroll operation " + sequence,
                    HumanVisibility.Activity, CerebralEventTypes.ServiceUpdate, EventActorKind.Service))];
        endSequence = 48; saved = saved with { Revision = saved.Revision + 1 };
        workspace.Reset();
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await SettleActivityAsync();
        var mainViewport = Find<ScrollViewer>(workspace, "WorkflowTranscript") ??
            throw new InvalidOperationException("The outer scrolling fixture requires the native chat viewport.");
        void CheckMainAtLatest(string phase)
        {
            Check(mainViewport.ScrollableHeight > 0 && mainViewport.VerticalOffset > 0 &&
                  Math.Abs(mainViewport.VerticalOffset - mainViewport.ScrollableHeight) <= 1,
                phase + ": the main chat viewport must remain at its nonzero bottom.");
        }
        mainViewport.ScrollToEnd();
        await SettleActivityAsync();
        CheckMainAtLatest("Before outer activity refresh");
        await AdvanceActivityAsync(64);
        await SettleActivityAsync();
        CheckMainAtLatest("After outer activity refresh");

        await AdvanceActivityAsync(80);
        workspace.Measure(new Size(680, 12000)); workspace.Arrange(new Rect(0, 0, 680, 12000)); workspace.UpdateLayout();
        Check(mainViewport.ScrollableHeight <= 1 && mainViewport.VerticalOffset <= 1,
            "The outer regression must exercise actual WPF viewport enlargement that clamps the old nonzero offset.");
        LayoutActivity();
        await SettleActivityAsync();
        CheckMainAtLatest("After programmatic layout clamping during refresh");

        Wheel(mainViewport);
        mainViewport.ScrollToVerticalOffset(90);
        await SettleActivityAsync();
        var mainReviewOffset = mainViewport.VerticalOffset;
        Check(mainReviewOffset > 1 && mainReviewOffset < mainViewport.ScrollableHeight - 32,
            "The outer rapid-refresh regression requires a genuine paused nonzero review offset.");
        await AdvanceActivityAsync(96);
        await AdvanceActivityAsync(112);
        await SettleActivityAsync();
        Check(Math.Abs(mainViewport.VerticalOffset - mainReviewOffset) <= 1,
            "Two main chat refreshes before deferred layout must preserve the prior review position.");

        mainViewport.ScrollToEnd();
        await SettleActivityAsync();
        CheckMainAtLatest("Before deferred main wheel review");
        await AdvanceActivityAsync(128);
        LayoutActivity();
        Wheel(mainViewport);
        mainViewport.ScrollToVerticalOffset(55);
        await SettleActivityAsync();
        Check(Math.Abs(mainViewport.VerticalOffset - 55) <= 1 &&
              mainViewport.VerticalOffset < mainViewport.ScrollableHeight - 32,
            "A newer routed main-chat wheel input must supersede deferred bottom restoration.");

        mainViewport.ScrollToEnd();
        await SettleActivityAsync();
        CheckMainAtLatest("Before deferred main key review");
        await AdvanceActivityAsync(144);
        LayoutActivity();
        Home(mainViewport);
        mainViewport.ScrollToTop();
        await SettleActivityAsync();
        Check(mainViewport.VerticalOffset <= 1 && mainViewport.ScrollableHeight > 0,
            "A newer routed main-chat Home key must supersede deferred bottom restoration.");

        // Exercise native width changes both without body reflow and with expanded rich text.
        var resizeText = string.Join(" ", Enumerable.Repeat(
            "The operation detail remains selectable while the activity viewport changes width.", 8));
        journal = [original, hidden, .. Enumerable.Range(3, 60).Select(sequence =>
            Item("resize-activity-" + sequence, sequence, resizeText,
                HumanVisibility.Activity, CerebralEventTypes.ServiceUpdate, EventActorKind.Service))];
        endSequence = 24; saved = saved with { Revision = saved.Revision + 1 };
        workspace.Reset();
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await SettleActivityAsync();
        CheckAtLatest("Before native width resize");
        var narrowViewport = ActivityScroller();
        var narrowWidth = narrowViewport.ViewportWidth;
        var widthChanges = 0;
        narrowViewport.ScrollChanged += (_, change) =>
        {
            if (ReferenceEquals(change.OriginalSource, narrowViewport) && change.ViewportWidthChange != 0) widthChanges++;
        };
        activityLayoutWidth = 1360;
        await SettleActivityAsync();
        Check(widthChanges > 0 && ActivityScroller().ViewportWidth > narrowWidth + 300 &&
              ActivityPosition().FollowingLatest,
            "An actual native viewport width change must preserve follow intent without treating layout as owner scrolling.");
        await AdvanceActivityAsync(40);
        await SettleActivityAsync();
        CheckAtLatest("Append after native wide resize");

        // Reflow requires retained documents beyond the default two-row window.
        // Enter through the native Review control before expanding their real bodies.
        reviewActivity = Find<Button>(workspace, "WorkflowActivityHistory") ??
            throw new InvalidOperationException("The width-reflow fixture requires its review control.");
        reviewActivity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleActivityAsync();
        foreach (var entry in Descendants(ActivityScroller()).OfType<Expander>())
            entry.IsExpanded = true;
        await SettleActivityAsync();
        Check(Descendants(ActivityScroller()).OfType<RichTextBox>().Count() >= 30,
            "The width-reflow regression must use actual expanded activity documents.");
        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        CheckAtLatest("Before expanded-document width reflow");
        var wideExtent = ActivityScroller().ExtentHeight;
        var wideWidth = ActivityScroller().ViewportWidth;
        activityLayoutWidth = 680;
        await SettleActivityAsync();
        Check(ActivityScroller().ViewportWidth < wideWidth - 300 &&
              ActivityScroller().ExtentHeight > wideExtent + 100 &&
              ActivityPosition().FollowingLatest,
            "Narrowing actual expanded activity text must cause native reflow while preserving follow intent.");
        await AdvanceActivityAsync(48);
        await SettleActivityAsync();
        CheckAtLatest("Append after native narrow text reflow");

        Wheel(ActivityScroller());
        ActivityScroller().ScrollToVerticalOffset(75);
        await SettleActivityAsync();
        var resizeReviewOffset = ActivityScroller().VerticalOffset;
        Check(resizeReviewOffset > 1 && !ActivityPosition().FollowingLatest,
            "The resize review regression requires a genuine user-owned paused offset.");
        activityLayoutWidth = 1360;
        await SettleActivityAsync();
        Check(ActivityScroller().ScrollableHeight > resizeReviewOffset + 16 &&
              Math.Abs(ActivityScroller().VerticalOffset - resizeReviewOffset) <= 1 &&
              !ActivityPosition().FollowingLatest,
            "Widening the activity viewport must preserve an unclamped paused review position rather than resume follow.");
        await AdvanceActivityAsync(56);
        await SettleActivityAsync();
        Check(Math.Abs(ActivityScroller().VerticalOffset - resizeReviewOffset) <= 1 &&
              !ActivityPosition().FollowingLatest,
            "Appending after a width resize must retain the owner's paused activity review position.");
        activityLayoutWidth = 680;
        await SettleActivityAsync();

        // A full recent page can keep 33 calls while six-event command cycles advance the journal.
        // Assert actual rendered rows and their range, rather than treating a flat page count as staleness.
        CerebralEvent RollingItem(int cycle, int offset)
        {
            var sequence = 3L + cycle * 6 + offset;
            var id = "rolling-" + sequence;
            var callId = "rolling-call-" + cycle;
            if (offset == 0)
                return new(id, conversationId, runId, sequence, CerebralEventTypes.ToolCall,
                    new(EventActorKind.Agent, "builder", "Builder"), [],
                    new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent, AgentId: "builder"), now,
                    CerebralJson.Element(new ToolCallData(callId, "file.read",
                        CerebralJson.Element(new { path = @"C:\fixture\rolling-" + cycle + ".cs" }))));
            if (offset == 3)
                return new(id, conversationId, runId, sequence, CerebralEventTypes.ToolResult,
                    new(EventActorKind.Tool), [], new(ModelInclusion.Never, HumanVisibility.Activity),
                    new(EventSourceKind.Tool), now,
                    CerebralJson.Element(new ToolResultData(callId, "file.read", true)));
            var reason = offset switch { 1 => "effect.dispatched", 2 => "claimed", 4 => "effect.finished", _ => "advanced" };
            return new(id, conversationId, runId, sequence, CerebralEventTypes.WorkflowTransition,
                new(EventActorKind.Service), [], new(ModelInclusion.Never, HumanVisibility.Activity),
                new(EventSourceKind.Service), now, CerebralJson.Element(new { reason }));
        }
        journal = [original, hidden, .. Enumerable.Range(0, 165).SelectMany(cycle =>
            Enumerable.Range(0, 6).Select(offset => RollingItem(cycle, offset)))];
        endSequence = 200;
        saved = saved with { Revision = saved.Revision + 1 };
        workspace.Reset();
        Check(workspace.DisplayedActivity is { Count: 0, FirstSequence: null, LastSequence: null } && !workspace.IsReadingHistory,
            "Reset must clear rendered activity freshness and history-mode diagnostics.");
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await SettleActivityAsync();
        reviewActivity = Find<Button>(workspace, "WorkflowActivityHistory") ??
            throw new InvalidOperationException("The rolling retained-history fixture requires its review control.");
        reviewActivity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleActivityAsync();
        workspace.ResumeActivityFollowAfterOutsideClick(workspace);
        await SettleActivityAsync();
        long? priorLast = null;
        foreach (var end in new[] { 200L, 398L, 596L, 794L, 992L })
        {
            if (end != 200) await AdvanceActivityAsync(end);
            await SettleActivityAsync();
            var page = workspace.Snapshot!;
            var shown = workspace.DisplayedActivity;
            var expectedRows = journal.Where(item => item.Sequence >= Math.Max(1, end - 799) && item.Sequence <= end &&
                item.Type is CerebralEventTypes.ToolCall or CerebralEventTypes.ToolResult).ToArray();
            var entries = Descendants(ActivityScroller()).OfType<Expander>().ToArray();
            Check(page.Events.Count == 200 && page.Events.Count(item => item.Type == CerebralEventTypes.ToolCall) == 33 &&
                  page.SnapshotSequence == end && page.Events[0].Sequence == end - 199 && page.Events[^1].Sequence == end,
                "A rolling latest page must advance its exact range while retaining the same 33-call compatibility count.");
            var groupedRows = WorkflowWorkspace.ProjectActivityRows(expectedRows);
            var viewportDiagnostic = ActivityPosition();
            Check(shown.Count == expectedRows.Length && entries.Length == groupedRows.Count &&
                  viewportDiagnostic.RenderedRowCount == groupedRows.Count &&
                  viewportDiagnostic.RenderedRowKeys.SequenceEqual(groupedRows.TakeLast(WorkflowWorkspace.MaximumActivityDiagnosticViewports).Select(row => row.Key)) &&
                  viewportDiagnostic.RenderedRowKeysTruncated == (groupedRows.Count > WorkflowWorkspace.MaximumActivityDiagnosticViewports) &&
                  shown.FirstSequence == expectedRows[0].Sequence && shown.LastSequence == expectedRows[^1].Sequence &&
                  (priorLast is null || shown.LastSequence > priorLast) && !workspace.IsReadingHistory &&
                  ((string)entries[^1].Header).Contains("rolling-" + ((end - 6) / 6) + ".cs", StringComparison.Ordinal),
                "Rendered activity must advance to the newest command result, omit bookkeeping rows, and retain a bounded range independently of page counts.");
            CheckAtLatest("Rolling 33-call page at " + end);
            var freshnessJson = JsonSerializer.Serialize(new { shown, viewports = workspace.ActivityViewportSnapshots,
                historyMode = workspace.IsReadingHistory }, CerebralJson.Options);
            Check(!freshnessJson.Contains("file.read", StringComparison.Ordinal) &&
                  !freshnessJson.Contains(@"C:\fixture\rolling-", StringComparison.Ordinal) &&
                  !freshnessJson.Contains(originalText, StringComparison.Ordinal) &&
                  !freshnessJson.Contains(hiddenText, StringComparison.Ordinal),
                "Freshness diagnostics must expose numeric ranges and follow intent without commands, arguments or message bodies.");
            priorLast = shown.LastSequence;
        }
        await workspace.LoadEarlierMessagesAsync();
        Check(workspace.IsReadingHistory, "History-mode diagnostics must identify deliberately pinned earlier-message reading.");
        workspace.Reset();
        Check(!workspace.IsReadingHistory && workspace.DisplayedActivity is { Count: 0, FirstSequence: null, LastSequence: null },
            "Reset must remove history mode and the prior rendered activity range.");

        return checks;
    }

    private static async Task<int> VerifyPassiveInteractionRecoveryAsync()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var banners = new List<string>(); var diagnostics = new List<string>(); var recoveries = 0;
        const string privateFailure = @"Private editable fixture value C:\private\fixture.txt --raw-argument";
        foreach (var error in new Exception[] { new ArgumentException(privateFailure),
            new InvalidOperationException(privateFailure), new NotSupportedException(privateFailure) })
            Check(!PassiveDesktopInteraction.TryRun("activity follow", () => throw error, banners.Add,
                    () => recoveries++, diagnostics.Add),
                "Known passive view failures must recover locally rather than leave the WPF dispatcher.");
        Check(banners.Count == 3 && recoveries == 3 && diagnostics.Count == 3 &&
              banners.All(message => message.Contains("Retry the click", StringComparison.Ordinal)) &&
              diagnostics.All(message => !message.Contains(privateFailure, StringComparison.Ordinal) &&
                  !message.Contains(@"C:\private", StringComparison.Ordinal) && !message.Contains("--raw-argument", StringComparison.Ordinal)),
            "Each failed interaction must report one actionable banner and bounded payload-free diagnostic.");
        var unclassifiedEscaped = false; var fatalEscaped = false;
        try { PassiveDesktopInteraction.TryRun("activity follow", () => throw new FormatException(privateFailure), banners.Add, diagnostic: diagnostics.Add); }
        catch (FormatException) { unclassifiedEscaped = true; }
        try { PassiveDesktopInteraction.TryRun("activity follow", () => throw new OutOfMemoryException(privateFailure), banners.Add, diagnostic: diagnostics.Add); }
        catch (OutOfMemoryException) { fatalEscaped = true; }
        Check(unclassifiedEscaped && fatalEscaped && banners.Count == 3 && diagnostics.Count == 3,
            "Unclassified dispatcher errors and fatal runtime errors must remain outside the recoverable passive boundary.");
        var reportAttempts = 0;
        Check(!PassiveDesktopInteraction.TryRun("activity follow", () => throw new InvalidOperationException(privateFailure),
                _ => { reportAttempts++; throw new InvalidOperationException(privateFailure); }, diagnostic: diagnostics.Add) &&
              reportAttempts == 1 && diagnostics.Count == 5,
            "A failed banner must be logged once without recursively reporting or retrying the action.");

        var commandCalls = 0; var callbackCalls = 0; var reported = new List<string>();
        using var workspace = new WorkflowWorkspace((_, _) =>
        {
            commandCalls++;
            throw new InvalidOperationException("A passive view check must not dispatch a command.");
        }, () => Array.Empty<AgentProfile>(), (message, error) => { if (error) reported.Add(message); }, embedded: true);
        var pending = typeof(WorkflowWorkspace).GetField("pendingScrollRestore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var queue = typeof(WorkflowWorkspace).GetMethod("QueuePendingScrollRestoration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        pending.SetValue(workspace, new Action(() => { callbackCalls++; throw new InvalidOperationException(privateFailure); }));
        var originalError = Console.Error; using var retainedDiagnostic = new StringWriter();
        try
        {
            Console.SetError(retainedDiagnostic);
            queue.Invoke(workspace, null); queue.Invoke(workspace, null);
            await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(callbackCalls == 1 && reported.Count == 1 && pending.GetValue(workspace) is null && commandCalls == 0,
                "The actual deferred transcript queue must retire a failed callback so duplicate layout notifications neither replay it nor repeat a banner.");
            Check(!retainedDiagnostic.ToString().Contains(privateFailure, StringComparison.Ordinal) &&
                  retainedDiagnostic.ToString().Contains("desktop.passive-interaction.failed", StringComparison.Ordinal) &&
                  reported[0].Contains("Retry the click", StringComparison.Ordinal),
                "A real deferred view failure must retain safe diagnostics and actionable visible reporting.");
            pending.SetValue(workspace, new Action(() => { callbackCalls++; pending.SetValue(workspace, null); }));
            queue.Invoke(workspace, null);
            await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(callbackCalls == 2 && reported.Count == 1 && commandCalls == 0,
                "A fresh passive restoration can succeed after recovery without submitting or changing a workflow outcome.");
        }
        finally { Console.SetError(originalError); }
        return checks;
    }

    private static async Task<int> VerifyOptionalQuestionFormsAsync()
    {
        var checks = 0; var submissions = 0; var errors = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var now = DateTimeOffset.UtcNow;
        var saved = new WorkflowRun
        {
            Id = "optional-form", Submission = new("optional-form", "optional-chat", "lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, "Review choices.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            Revision = 1, Status = WorkflowStatus.Waiting, CreatedAt = now, UpdatedAt = now,
            Questions = new("mixed-questions", 1, [new("required", "Required choice: " + string.Join(" ", Enumerable.Repeat("A longer question must remain readable when the conversation is resized.", 5)), Choices: ["Keep", "Change"]),
                new("optional", "Optional choice: " + string.Join(" ", Enumerable.Repeat("Leave this blank to skip it while retaining the required answer.", 5)), Required: false, Choices: ["Brief", "Detailed"])], new Dictionary<string, string>())
        };
        var original = new CerebralEvent("optional-original", "optional-chat", "optional-form", 1, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart(saved.Submission.UserText)], new(ModelInclusion.Required, HumanVisibility.Transcript),
            new(EventSourceKind.User), now);
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Operation == "jobs.workflow.get") return Task.FromResult(CommandResult.Ok(saved));
            if (request.Operation == "conversations.get") return Task.FromResult(CommandResult.Ok(new ConversationSummary(
                "optional-chat", "Optional form", "lead", null, saved.Id, saved.Status, saved.UpdatedAt)));
            if (request.Operation == "jobs.workflow.events.previous") return Task.FromResult(CommandResult.Ok(new
                { events = new[] { original }, snapshotSequence = 1, nextBeforeSequence = (long?)null }));
            if (request.Operation == "jobs.workflow.answer")
            {
                var answer = CerebralJson.Read<WorkflowAnswerRequest>(request.Arguments); submissions++;
                Check(answer.QuestionSetId == saved.Questions!.Id && answer.ExpectedQuestionRevision == saved.Questions.Revision &&
                    (submissions == 1 ? answer.Answers.Count == 1 && answer.Answers["required"] == "Keep" :
                        submissions == 2 ? answer.Answers.Count == 0 :
                        submissions == 3 ? answer.Answers.Count == 2 && answer.Answers["items"] == "[\"A\",\"C\"]" && answer.Answers["notes"] == "Keep the files.\nPreserve the data." :
                        answer.Answers.Count == 1 && answer.Answers["optional-items"] == "[\"D\",\"E\"]"),
                    "The routed form submits required values while omitting blank optional choices, including optional-only empty forms.");
                saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Queued,
                    Questions = saved.Questions with { Revision = saved.Questions.Revision + 1, Answers = answer.Answers },
                    Signal = new(WorkflowSignalKind.Answers, CerebralJson.Element(answer.Answers)) };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            throw new InvalidOperationException("Unexpected optional-form fixture route: " + request.Operation);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) errors.Add(message); }, embedded: true);
        // InputHitTest needs a native presentation source. A hidden HWND supplies that
        // boundary without opening or activating a user window or simulating hardware input.
        using var presentationSource = new HwndSource(new HwndSourceParameters("Cerebral question form checks")
        {
            Width = 1100, Height = 640, WindowStyle = unchecked((int)0x80000000),
            PositionX = -32000, PositionY = -32000
        }) { RootVisual = workspace };
        await workspace.OpenRunAsync(saved.Id);
        void Layout(double width = 680, double height = 480) { workspace.Measure(new Size(width, height)); workspace.Arrange(new Rect(0, 0, width, height)); workspace.UpdateLayout(); }
        Layout();
        var submit = Find<Button>(workspace, "WorkflowSubmitAnswers")!;
        var host = Find<Border>(workspace, "WorkflowQuestions")!;
        var page = Find<ScrollViewer>(workspace, "WorkflowTranscript")!;
        Check(page.Content is StackPanel flow && flow.Children.Contains(host) && double.IsPositiveInfinity(host.MaxHeight) &&
            !Descendants(host).OfType<ScrollViewer>().Any(),
            "Questions grow in the main conversation flow without a capped or independently scrolling form.");
        foreach (var size in new[] { new Size(680, 480), new Size(360, 320), new Size(1100, 640) })
        {
            Layout(size.Width, size.Height);
            await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(host.ActualHeight > 180 && host.ActualWidth <= page.ActualWidth && page.ViewportHeight > 0,
                "The complete question set retains its natural height and fits the conversation width at each window size.");
            page.ScrollToEnd(); page.UpdateLayout();
            var submitTop = submit.TranslatePoint(new Point(), page).Y;
            Check(submitTop >= 0 && submitTop + submit.ActualHeight <= page.ViewportHeight + 1,
                "The primary page scroll reaches the whole submit button even in a small window.");
            foreach (var point in new[] { new Point(2, 2), new Point(submit.ActualWidth / 2, submit.ActualHeight / 2), new Point(submit.ActualWidth - 2, submit.ActualHeight - 2) })
            {
                var translated = submit.TranslatePoint(point, workspace);
                var hit = workspace.InputHitTest(translated) as DependencyObject;
                Check(hit is not null && (ReferenceEquals(hit, submit) || submit.IsAncestorOf(hit)),
                    $"The submit button has a native hit surface at {point} in {size}: root point={translated}, hit={hit?.GetType().Name ?? "null"}, visible={workspace.IsVisible}/{submit.IsVisible}, hitEnabled={workspace.IsHitTestVisible}/{submit.IsHitTestVisible}.");
            }
            page.ScrollToVerticalOffset(host.TranslatePoint(new Point(), (UIElement)page.Content).Y); page.UpdateLayout();
            Check(host.TranslatePoint(new Point(), page).Y >= -1,
                "The primary page scroll can reveal the question heading and first full label.");
        }
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 0 && errors.Count == 1, "Submitting a missing required choice reports validation and does not dispatch.");
        var required = Find<ComboBox>(workspace, "WorkflowAnswer-required")!;
        var optional = Find<ComboBox>(workspace, "WorkflowAnswer-optional")!;
        Check(optional.Items.Contains(""), "An optional choice can be explicitly cleared after selection.");
        optional.SelectedItem = "Brief"; optional.SelectedItem = ""; required.SelectedItem = "Keep";
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 1 && workspace.Snapshot?.Run is { Status: WorkflowStatus.Queued, Questions: null },
            "A required answer with an unanswered optional field submits through the actual button handler.");
        Check(host.Visibility == Visibility.Collapsed,
            "Accepted answers remove the prominent form from the conversation page immediately.");
        saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Waiting, Signal = new(WorkflowSignalKind.Start, WorkflowContract.EmptyObject),
            Questions = new("optional-only", 1, [new("optional", "Optional choice", Required: false, Choices: ["Brief", "Detailed"])],
                new Dictionary<string, string>()) };
        await workspace.RefreshAsync(); Layout();
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 2 && errors.Count == 1 && workspace.Snapshot?.Run is { Status: WorkflowStatus.Queued, Questions: null },
            "An optional-only form submits with no selection and leaves no validation error or open form.");
        saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Waiting, Signal = new(WorkflowSignalKind.Start, WorkflowContract.EmptyObject),
            Questions = new("multiple-form", 1, [new("items", "Which plugins should be disabled?", Choices: ["A", "B", "C"])
                { Multiple = true, Description = "| Action | Effect |\n| --- | --- |\n| Disable | Reversible |\n\n[Reference](https://example.com/features)" },
                new("optional-items", "Optional additional items", Required: false, Choices: ["D", "E"]) { Multiple = true },
                new("notes", "Additional notes (optional)", Required: false)], new Dictionary<string, string>()) };
        await workspace.RefreshAsync(); Layout(360, 320);
        Check(Find<ComboBox>(workspace, "WorkflowAnswer-items") is null &&
            Find<CheckBox>(workspace, "WorkflowAnswer-items-0") is { IsChecked: false } &&
            Find<CheckBox>(workspace, "WorkflowAnswer-items-2") is { IsChecked: false },
            "Explicit multiple-choice fields render individually accessible checkboxes with no implicit selections.");
        Check(Descendants(host).OfType<RichTextBox>().Any(view => view.Document.Blocks.OfType<Table>().Any()),
            "A comparison table renders in the prominent question body using the shared Markdown renderer.");
        Check(Descendants(host).OfType<RichTextBox>().SelectMany(view => view.Document.Blocks.OfType<System.Windows.Documents.Paragraph>())
                .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>())
                .Any(link => link.NavigateUri?.AbsoluteUri == "https://example.com/features"),
            "Question descriptions retain actionable web references alongside their comparison table without opening a browser.");
        Check(Descendants(host).OfType<TextBlock>().Count(block => block.Text == "Additional notes (optional)") == 1 &&
            !Descendants(host).OfType<TextBlock>().Any(block => block.Text.Contains("(optional) (optional)", StringComparison.Ordinal)),
            "Already labeled optional notes are not marked optional twice.");
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 2 && errors.Count == 2, "A required multiple choice cannot submit without selecting an item.");
        Find<CheckBox>(workspace, "WorkflowAnswer-items-0")!.IsChecked = true;
        Find<CheckBox>(workspace, "WorkflowAnswer-items-2")!.IsChecked = true;
        var notes = Find<TextBox>(workspace, "WorkflowAnswer-notes")!;
        notes.Text = "Keep the files.\nPreserve the data.";
        Check(notes.AcceptsReturn && notes.TextWrapping == TextWrapping.Wrap,
            "Free-text questions accept multiline notes without sacrificing ordinary typing or wrapping.");
        saved = saved with { Revision = saved.Revision + 1, Questions = saved.Questions! with { Revision = 2 } };
        await workspace.RefreshAsync(); Layout();
        Check(Find<CheckBox>(workspace, "WorkflowAnswer-items-0")!.IsChecked == true &&
            Find<CheckBox>(workspace, "WorkflowAnswer-items-2")!.IsChecked == true &&
            Find<TextBox>(workspace, "WorkflowAnswer-notes")!.Text == "Keep the files.\nPreserve the data.",
            "A newer revision of the same form preserves several selected choices and exact multiline notes.");
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 3 && errors.Count == 2 && host.Visibility == Visibility.Collapsed,
            "All selected items and optional notes submit together once while blank optional multi-selection is omitted.");
        saved = saved with { Revision = saved.Revision + 1, Status = WorkflowStatus.Waiting, Signal = new(WorkflowSignalKind.Start, WorkflowContract.EmptyObject),
            Questions = new("optional-multiple-selected", 1, [new("optional-items", "Additional items", Required: false, Choices: ["D", "E"]) { Multiple = true }],
                new Dictionary<string, string>()) };
        await workspace.RefreshAsync(); Layout();
        Find<CheckBox>(workspace, "WorkflowAnswer-optional-items-0")!.IsChecked = true;
        Find<CheckBox>(workspace, "WorkflowAnswer-optional-items-1")!.IsChecked = true;
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(submissions == 4 && errors.Count == 2 && host.Visibility == Visibility.Collapsed,
            "An optional multi-selection delivers every selected value as the structured array and closes its form.");
        return checks;
    }

    private static CommandResult EmptyRecentWorkPage(CommandRequest request)
    {
        if (request.ProfileId is not null || request.Operation != "jobs.workflow.recent" ||
            request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.EnumerateObject().Count() != 1 ||
            !request.Arguments.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Number ||
            !limit.TryGetInt32(out var count) || count != 40)
            throw new InvalidOperationException("Standalone Work inspection requires only the exact owner-route recent page with limit 40.");
        return CommandResult.Ok(new WorkflowRecentPage([], 0, null));
    }

    private static Func<CommandRequest, CancellationToken, Task<CommandResult>> ReadOnlyInspectionCommand(
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch) => (request, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (request.Operation is not ("jobs.workflow.get" or "conversations.get" or "jobs.workflow.events" or
            "jobs.workflow.events.previous" or "jobs.workflow.recent"))
            throw new InvalidOperationException("Standalone fixture inspection cannot submit, replay, or mutate work.");
        return dispatch(request, token);
    };

    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T item && AutomationProperties.GetAutomationId(item) == id) return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static string Text(DependencyObject root)
    {
        var current = root switch { TextBlock block => block.Text, TextBox box => box.Text, FlowDocumentScrollViewer document when document.Document is not null =>
            new TextRange(document.Document.ContentStart, document.Document.ContentEnd).Text, _ => "" };
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) current += "\n" + Text(VisualTreeHelper.GetChild(root, index));
        return current;
    }
    private static IEnumerable<string> Headers(DependencyObject root)
    {
        if (root is HeaderedContentControl { Header: string header }) yield return header;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var nestedHeader in Headers(VisualTreeHelper.GetChild(root, index))) yield return nestedHeader;
    }
}
