using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Offscreen presentation checks with deterministic service events; no Worker, model or network.</summary>
public static class WorkflowGenerationPreviewChecks
{
    public static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var checks = 0; var calls = 0; var reports = new List<string>(); long sequence = 10;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        var original = new CerebralEvent("preview-original", "preview-chat", "preview-run", 1,
            CerebralEventTypes.Message, new(EventActorKind.User),
            [new TextContentPart(string.Join('\n', Enumerable.Range(1, 65).Select(i => "Request line " + i)))],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now);
        var call = new CerebralEvent("preview-call", "preview-chat", "preview-run", 2,
            CerebralEventTypes.ToolCall, new(EventActorKind.Agent, "lead"), [],
            new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Agent), now,
            CerebralJson.Element(new ToolCallData("effect-a", "cerebral.host:generate", CerebralJson.Element(new { }))));
        var events = new List<CerebralEvent> { original, call };
        var saved = new WorkflowRun
        {
            Id = "preview-run", Revision = 1, Status = WorkflowStatus.Queued,
            Submission = new("preview-run", "preview-chat", "lead", "fixture.preview", "1.0.0",
                ((TextContentPart)original.Content.Single()).Text, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now,
            Effect = new("effect-a", "cerebral.host:generate", 1, WorkflowContract.EmptyObject,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched)
        };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls++;
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(saved),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events, snapshotSequence = events[^1].Sequence, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { original }, snapshotSequence = events[^1].Sequence, nextAfterSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected preview fixture operation " + request.Operation)
            });
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => [], (message, _) => reports.Add(message), embedded: true);
        workspace.BeginConversation("preview-chat");
        await workspace.OpenRunSnapshotAsync("preview-run", deadline.Token);
        using var source = new HwndSource(new HwndSourceParameters("Cerebral generation preview checks")
        { Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = workspace };
        async Task Flush()
        {
            await dispatcher.InvokeAsync(() => { workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout(); }, DispatcherPriority.Loaded);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        await Flush();
        Check(workspace.IsLoaded, "The fixture must attach to an offscreen presentation source.");
        var host = Field<Border>(workspace, "generationPreviewHost");
        var text = Field<RichTextBox>(workspace, "generationPreviewText");
        var previewRun = Field<Run>(workspace, "generationPreviewRun");
        string? copied = null;
        var fallbackCopies = 0;
        // If WPF ever routes around the preview's command binding, cancel its built-in
        // DataObject copy before it can touch the user's system clipboard.
        DataObject.AddCopyingHandler(text, (_, args) => { fallbackCopies++; args.CancelCommand(); });
        var copySink = workspace.GetType().GetField("generationPreviewCopyText", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing private copy sink for the offscreen fixture.");
        copySink.SetValue(workspace, (Action<string>)(value => copied = value));
        string Public() => Field<string>(workspace, "generationPreviewPublicText");
        void Select(int start, int length) => workspace.SelectGenerationPreviewText(start, length);
        (int Start, int Length) Selection() => workspace.GenerationPreviewSelectionRange();
        var caption = Field<TextBlock>(workspace, "generationPreviewCaption");
        var page = Field<ScrollViewer>(workspace, "transcriptScroller");
        var flow = Field<StackPanel>(workspace, "conversationFlow");
        var transcript = Field<StackPanel>(workspace, "transcript");
        var transcriptChildren = transcript.Children.Cast<UIElement>().ToArray();
        var activity = Descendants(workspace).OfType<Button>().First(x => AutomationProperties.GetAutomationId(x) == "WorkflowActivity");
        Check(flow.Children.Count == 2 && AutomationProperties.GetAutomationId(flow.Children[1]) == "WorkflowQuestions",
            "Before response observation the conversation has its saved transcript and question surface.");
        JsonObject Payload(string phase = "draft", string? kind = "final", string? body = "café 😀",
            string? operation = null, string effect = "effect-a", long elapsed = 100, string finish = "Stop") => new()
        {
            ["schema"] = "cerebral.generation-preview/1", ["conversationId"] = "preview-chat",
            ["requestId"] = "preview-run", ["runId"] = "preview-run", ["effectId"] = effect,
            ["agentId"] = "lead", ["phase"] = phase, ["kind"] = kind, ["text"] = body,
            ["operation"] = operation, ["truncated"] = false, ["elapsedMilliseconds"] = elapsed, ["finish"] = phase == "settled" ? finish : null
        };
        ServiceEvent Event(JsonObject data, long? number = null, string? job = "preview-run") =>
            new(number ?? sequence++, "workflow.generation.preview", job, CerebralJson.Element(data), now);
        var beforePreviewCalls = calls;
        workspace.Notify(Event(Payload()));
        await Flush();
        Check(host.Visibility == Visibility.Visible && Public() == "café 😀" && caption.Text.Contains("Responding", StringComparison.Ordinal),
            "A selected accepted run can display a public pending effect before its authoritative effect snapshot catches up.");
        Check(text.IsReadOnly && text.Document.Blocks.FirstBlock is Paragraph { LineHeight: SelectableMarkdown.NormalBodyLineHeight } &&
              text.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && double.IsPositiveInfinity(text.MaxHeight),
            "The bounded public answer uses one selectable normal-body paragraph in the conversation's page scroller.");
        checks += VerifyBodyGeometry(text, previewRun);
        Select(1, 3);
        Check(workspace.GenerationPreviewSelectedPublicText() == "afé", "Copy projection contains exactly the selected public text.");
        text.SelectAll();
        Check(workspace.GenerationPreviewSelectedPublicText() == "café 😀", "Whole-body copy projection excludes document terminators.");
        ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "café 😀", "The actual WPF Copy command emits only bounded public text; the fixture intercepts it before the system clipboard.");
        Select(5, 2);
        Check(workspace.GenerationPreviewSelectedPublicText() == "😀", "A selected surrogate pair remains one complete public character.");
        copied = null; ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "😀", "The actual Copy command keeps a selected surrogate pair intact.");
        Select(6, 1);
        Check(Selection() == (5, 2), "A UTF16 endpoint inside an emoji expands to the whole surrogate pair.");
        Select(0, 0);
        Check(calls == beforePreviewCalls && workspace.Snapshot!.Events.Count == 2 && workspace.DisplayedTranscript.Count == 1,
            "Preview observation makes no commands and changes neither durable events nor transcript count.");
        Check(workspace.GenerationPreviewSnapshot is { Visible: true, Kind: "final", PublicText: "café 😀", PublicTextBytes: 10,
                ConversationId: "preview-chat", RequestId: "preview-run", RunId: "preview-run", EffectId: "effect-a", AgentId: "lead", EventSequence: 10, ElapsedMilliseconds: 100 },
            "Native diagnostics expose only the actual bounded public draft and exact identity.");
        Check(ReferenceEquals(host.Parent, flow) && flow.Children.IndexOf(host) == 1,
            "The one stable answer is mounted after the saved transcript and before questions.");
        JsonObject Metric(string effect = "effect-a", string runId = "preview-run", string status = "active",
            string phase = "output", double? inputRate = null, double? outputRate = 42, long elapsed = 100) => new()
        {
            ["schema"] = "cerebral.generation-metrics/1", ["laneId"] = "lane-a", ["instanceId"] = "instance-a",
            ["modelId"] = "fixture", ["adapterId"] = "fixture.local",
            ["binding"] = new JsonObject { ["conversationId"] = "preview-chat", ["requestId"] = runId,
                ["runId"] = runId, ["effectId"] = effect, ["agentId"] = "lead", ["workerId"] = "local" },
            ["state"] = status, ["phase"] = phase, ["inputTokens"] = 100, ["outputTokens"] = 10,
            ["inputTokensPerSecond"] = inputRate, ["outputTokensPerSecond"] = outputRate,
            ["elapsedMilliseconds"] = elapsed, ["at"] = JsonValue.Create(DateTimeOffset.UtcNow)
        };
        ServiceEvent Measurement(JsonObject data, long? number = null) =>
            new(number ?? sequence++, "workflow.generation.metrics", "preview-run", CerebralJson.Element(data), now);
        workspace.Notify(Measurement(Metric()));
        Check(workspace.GenerationRateSnapshot is { Visible: true, RunId: "preview-run", OutputTokensPerSecond: 42 } &&
            activity.Content?.ToString()!.Contains("42 out tok/s", StringComparison.Ordinal) == true,
            "Per-chat throughput is scoped to the selected saved run/profile, even before pending effect projection.");
        workspace.Notify(Measurement(Metric(runId: "other-run", outputRate: 999)));
        workspace.Notify(Measurement(Metric(outputRate: -1)));
        var privateMetric = Metric(outputRate: 999); privateMetric["reasoning"] = "excluded";
        workspace.Notify(Measurement(privateMetric));
        Check(workspace.GenerationRateSnapshot.OutputTokensPerSecond == 42,
            "Cross-run, invalid and private telemetry cannot overwrite the selected lane.");
        workspace.Notify(Measurement(Metric(outputRate: 999), 1));
        Check(workspace.GenerationRateSnapshot.OutputTokensPerSecond == 42, "Older measurement envelope sequences cannot rewind per-chat rates.");
        Select(1, 3);
        var rowsBeforeMetrics = transcript.Children.Cast<UIElement>().ToArray();
        workspace.Notify(Measurement(Metric(outputRate: 43, elapsed: 200)));
        await Flush();
        Check(Selection().Start == 1 && Selection().Length == 3 && rowsBeforeMetrics.SequenceEqual(transcript.Children.Cast<UIElement>()),
            "Numeric updates change headers only, preserving public text selection and transcript controls.");
        workspace.Notify(Measurement(Metric(status: "Stop", outputRate: 44, elapsed: 300)));
        workspace.Notify(Measurement(Metric(outputRate: 900, elapsed: 400)));
        Check(workspace.GenerationRateSnapshot.OutputTokensPerSecond == 44,
            "A settled lane cannot be resurrected as active by a later same-effect notification.");
        Check(activity.Content?.ToString()!.Contains("Last generation · — in / 44 out tok/s", StringComparison.Ordinal) == true,
            "A settled iteration freezes its own last measured throughput instead of continuing as live generation.");
        Select(0, 0);
        var render = Field<long>(workspace, "generationPreviewRenderRevision");
        workspace.Notify(Event(Payload(elapsed: 150)));
        await Flush();
        Check(Field<long>(workspace, "generationPreviewRenderRevision") == render, "Unchanged text does not render again merely because elapsed time changed.");
        workspace.Notify(Event(Payload(body: "older"), 1));
        Check(Public() == "café 😀", "An older ServiceEvent envelope sequence cannot replace the newer draft.");
        workspace.Notify(Event(Payload(body: "café")));
        // A cumulative answer must not shrink, even if its transport sequence is newer.
        Check(Public() == "café 😀", "A shorter same-effect response cannot rewind the cumulative public draft.");
        var invalid = new List<JsonObject>();
        foreach (var key in new[] { "schema", "conversationId", "requestId", "runId", "agentId" })
        { var value = Payload(); value[key] = "foreign"; invalid.Add(value); }
        var unknown = Payload(); unknown["rawReasoning"] = "excluded"; invalid.Add(unknown);
        var tooLarge = Payload(body: new string('x', 32769)); invalid.Add(tooLarge);
        invalid.Add(Payload(body: string.Concat(Enumerable.Repeat("😀", 16385))));
        invalid.Add(Payload(body: "bad\0control"));
        invalid.Add(Payload(kind: "analysis", body: "excluded"));
        invalid.Add(Payload(kind: "tool", body: null, operation: "file.write --secret"));
        invalid.Add(Payload(elapsed: -1));
        invalid.Add(Payload(phase: "settled", body: "not empty"));
        var badFinish = Payload(phase: "settled", kind: null, body: null); badFinish["finish"] = "invented"; invalid.Add(badFinish);
        var unavailableFinish = Payload(phase: "unavailable", kind: null, body: null); unavailableFinish["finish"] = "Stop"; invalid.Add(unavailableFinish);
        var truncatedMissing = Payload(); truncatedMissing.Remove("truncated"); invalid.Add(truncatedMissing);
        foreach (var payload in invalid)
        {
            workspace.Notify(Event(payload));
            Check(Public() == "café 😀" && host.Visibility == Visibility.Visible, "Malformed, cross-chat or nonpublic preview data must not change current presentation.");
        }
        workspace.Notify(Event(Payload(), job: "other-run"));
        Check(Public() == "café 😀", "The outer JobId must match the preview run.");
        var duplicate = JsonDocument.Parse(Payload().ToJsonString().Replace("\"phase\":\"draft\"", "\"phase\":\"draft\",\"phase\":\"settled\""));
        workspace.Notify(new(sequence++, "workflow.generation.preview", "preview-run", duplicate.RootElement.Clone(), now));
        Check(Public() == "café 😀", "Duplicate payload properties are rejected instead of using the last one.");
        var surrogate = JsonDocument.Parse(Payload(body: "placeholder").ToJsonString().Replace("\"placeholder\"", "\"\\uD800\""));
        workspace.Notify(new(sequence++, "workflow.generation.preview", "preview-run", surrogate.RootElement.Clone(), now));
        Check(Public() == "café 😀", "An unpaired Unicode surrogate is rejected.");
        workspace.Notify(Event(Payload(body: "café 😀\nMore public answer", elapsed: 149)));
        Check(Public() == "café 😀", "Same-effect elapsed time cannot move backward.");

        Select(1, 3); page.ScrollToTop(); await Flush();
        var heldOffset = page.VerticalOffset;
        workspace.Notify(Event(Payload(body: "café 😀\nMore public answer", elapsed: 200)));
        await Flush();
        Check(ReferenceEquals(text, Field<RichTextBox>(workspace, "generationPreviewText")) && Selection().Start == 1 && Selection().Length == 3 &&
              Math.Abs(page.VerticalOffset - heldOffset) < 1,
            "Draft growth preserves the stable control, selected range and manual page position.");
        Check(workspace.GenerationPreviewSelectedPublicText() == "afé", "Cumulative growth keeps the exact selected public substring.");
        copied = null; ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "afé", "The actual Copy command follows the preserved selection after cumulative growth.");
        Check(ReferenceEquals(activity, Descendants(workspace).OfType<Button>().First(x => AutomationProperties.GetAutomationId(x) == "WorkflowActivity")) &&
              transcriptChildren.SequenceEqual(transcript.Children.Cast<UIElement>()),
            "Preview updates do not rebuild transcript cards or replace the embedded activity summary.");
        Select(0, 0); page.ScrollToEnd(); await Flush();
        var full = "café 😀\nMore public answer\r\n" + string.Join('\n', Enumerable.Range(1, 60).Select(i => "Draft line " + i)) + "\nשלום e\u0301";
        workspace.Notify(Event(Payload(body: full, elapsed: 250))); await Flush();
        Check(previewRun.Text == full && Public() == full,
            "A multiline cumulative preview retains exact public text without a FlowDocument terminator.");
        var lineEnding = full.IndexOf("\r\n", StringComparison.Ordinal);
        Select(lineEnding - 3, 9);
        copied = null; ApplicationCommands.Copy.Execute(null, text);
        Check(copied == full.Substring(lineEnding - 3, 9),
            "The real Copy command preserves the selected CRLF span exactly, without a document terminator.");
        var rtl = full.IndexOf("שלום", StringComparison.Ordinal);
        Select(rtl, full.Length - rtl);
        copied = null; ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "שלום e\u0301", "Mixed RTL and combining public characters survive selected WPF Copy exactly.");
        Check(fallbackCopies == 0, "All Copy commands used the preview's exact-public-text binding, never WPF's system clipboard fallback.");
        Select(0, 0);
        Check(page.ScrollableHeight <= page.VerticalOffset + 1, "A page already following newest stays at the growing draft.");
        Check(text.ActualHeight > 160 && text.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "A growing public answer remains full height in the main conversation rather than an inner activity block.");
        text.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        page.ScrollToVerticalOffset(12); Select(1, 3); await Flush();
        var innerOffset = page.VerticalOffset;
        workspace.Notify(Event(Payload(body: full + "\nExtra chunk", elapsed: 275))); await Flush();
        Check(Math.Abs(page.VerticalOffset - innerOffset) < 1 && Selection().Start == 1 && Selection().Length == 3,
            "A reader inspecting an earlier chunk retains page position and text selection across incoming output.");
        Select(0, 0);
        page.ScrollToTop(); await Flush();
        workspace.Notify(Event(Payload(body: full + "\nExtra chunk\nLast draft line", elapsed: 300)));
        // Mark the real user gesture while the changed text still has a pending layout extent.
        page.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        page.ScrollToVerticalOffset(15); await Flush();
        Check(Math.Abs(page.VerticalOffset - 15) < 1, "A manual scroll after notification supersedes the deferred preview restoration.");

        workspace.Notify(Event(Payload(phase: "settled", kind: null, body: null, elapsed: 400)));
        await Flush();
        Check(host.Visibility == Visibility.Visible && Public() == full + "\nExtra chunk\nLast draft line" && workspace.DisplayedTranscript.Count == 1 &&
            caption.Text.Contains("Awaiting saved response", StringComparison.Ordinal),
            "Provider settlement retains the one public answer until canonical adoption without adding a durable message.");
        Check(workspace.GenerationPreviewSnapshot is { Visible: true, RunId: "preview-run", EffectId: "effect-a", State: "Awaiting saved response" },
            "The retained display reports its exact response identity and pending durable adoption.");
        workspace.Notify(Event(Payload(body: full + "\nStale", elapsed: 450)));
        Check(Public() == full + "\nExtra chunk\nLast draft line", "A settled exact effect cannot receive a later stale draft.");
        workspace.Notify(Event(Payload(kind: "tool", body: null, operation: "file.read", effect: "effect-b", elapsed: 10)));
        await Flush();
        Check(host.Visibility == Visibility.Visible && text.Visibility == Visibility.Collapsed &&
              caption.Text == "Preparing operation · file.read · provisional",
            "A new effect after settlement presents only a proposed operation name, never its arguments or a success receipt.");
        workspace.Notify(Event(Payload(body: "unconfirmed replacement", effect: "effect-c", elapsed: 1)));
        Check(caption.Text.Contains("file.read", StringComparison.Ordinal), "An unrelated pending effect cannot displace an active admitted effect.");
        saved = saved with { Revision = 2, Status = WorkflowStatus.Running,
            Effect = new("effect-c", "cerebral.host:generate", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched) };
        await workspace.InspectRunAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: string.Concat(Enumerable.Repeat("😀", 16384)), effect: "effect-c", elapsed: 1)));
        await Flush();
        Check(Public().Length == 32768 && System.Text.Encoding.UTF8.GetByteCount(Public()) == 65536,
            "An authoritative effect admits the existing 32K-character answer contract without splitting Unicode or clipping at 8KiB.");
        workspace.Notify(Event(Payload(phase: "unavailable", kind: null, body: null, effect: "effect-c", elapsed: 5)));
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0, "Unavailable clears the exact provisional effect.");

        workspace.Notify(Event(Payload(body: "Finished answer", effect: "effect-d", elapsed: 1)));
        events.Add(new("preview-final", "preview-chat", "preview-run", 3, "workflow.result",
            new(EventActorKind.Agent, "lead"), [new StructuredContentPart("workflow.result", CerebralJson.Element(new { summary = "Finished answer" }))],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now));
        saved = saved with { Revision = 3, Status = WorkflowStatus.Completed, Effect = null };
        await workspace.InspectRunAsync("preview-run", deadline.Token); await Flush();
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0 && workspace.DisplayedTranscript.Count == 2,
            "An authoritative final response clears the ephemeral preview and appears exactly once in saved transcript.");
        Check(!workspace.GenerationRateSnapshot.Visible &&
              activity.Content?.ToString()!.Contains("Last generation · — in / 44 out tok/s", StringComparison.Ordinal) == true,
            "Clearing live generation leaves the exact completed iteration's frozen throughput on its own activity row.");
        workspace.Notify(Event(Payload(body: "terminal stale", effect: "effect-d", elapsed: 2)));
        Check(host.Visibility == Visibility.Collapsed, "A terminal run cannot receive a stale preview.");
        saved = saved with { Revision = 4, Status = WorkflowStatus.Running,
            Effect = new("effect-e", "cerebral.host:generate", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched) };
        await workspace.InspectRunAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: "Partial response before cancellation", effect: "effect-e", elapsed: 1))); await Flush();
        workspace.Notify(Event(Payload(phase: "settled", kind: null, body: null, effect: "effect-e", elapsed: 2, finish: "Cancelled"))); await Flush();
        Check(caption.Text.Contains("Cancellation result pending", StringComparison.Ordinal) && Public() == "Partial response before cancellation",
            "Provider cancellation retains public text while it awaits authoritative cancellation.");
        saved = saved with { Revision = 5, Status = WorkflowStatus.Cancelled, Effect = null };
        await workspace.InspectRunAsync("preview-run", deadline.Token); await Flush();
        Check(caption.Text.Contains("Cancelled · partial response", StringComparison.Ordinal) && Public() == "Partial response before cancellation",
            "Confirmed run cancellation honestly labels and retains its partial public answer.");
        saved = saved with { Revision = 6, Status = WorkflowStatus.Running,
            Effect = new("effect-f", "cerebral.host:generate", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched) };
        await workspace.InspectRunAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: "Partial response before failure", effect: "effect-f", elapsed: 1))); await Flush();
        saved = saved with { Revision = 7, Status = WorkflowStatus.Failed, Effect = null };
        await workspace.InspectRunAsync("preview-run", deadline.Token); await Flush();
        Check(caption.Text.Contains("Failed · partial response", StringComparison.Ordinal) && Public() == "Partial response before failure",
            "A failed run does not turn its streamed partial answer into a completed response or remove its useful text.");
        saved = saved with { Revision = 8, Status = WorkflowStatus.Running,
            Effect = new("effect-sidebar", "cerebral.host:generate", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched) };
        await workspace.InspectRunAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: "Public draft before canonical output routing", effect: "effect-sidebar", elapsed: 1))); await Flush();
        events.Add(new("preview-sidebar-final", "preview-chat", "preview-run", 4, "workflow.result",
            new(EventActorKind.Agent, "lead"), [new TextContentPart("Canonical sidebar output")],
            new(ModelInclusion.Evidence, HumanVisibility.Sidebar), new(EventSourceKind.Service, "cerebral.engine"), now));
        saved = saved with { Revision = 9, Status = WorkflowStatus.Completed, Effect = null };
        await workspace.InspectRunAsync("preview-run", deadline.Token); await Flush();
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0 && workspace.DisplayedTranscript.Count == 2,
            "A canonical result routed outside the transcript settles the public draft and honors authoritative output visibility.");
        saved = saved with { Revision = 10, Status = WorkflowStatus.Running,
            Effect = new("effect-e", "cerebral.host:generate", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched) };
        await workspace.InspectRunAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: "Lifecycle draft", effect: "effect-e"))); await Flush();
        await workspace.RefreshFromConnectionAsync();
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0, "Connection refresh discards the prior provisional display.");
        workspace.Notify(Event(Payload(body: "Old low sequence", effect: "effect-e", elapsed: 200), 1));
        Check(host.Visibility == Visibility.Collapsed, "An ordinary refresh retains its sequence fence.");
        workspace.ResetGenerationPreviewFeed();
        workspace.Notify(Event(Payload(body: "Restarted feed", effect: "effect-e", elapsed: 1), 1)); await Flush();
        Check(Public() == "Restarted feed" && workspace.GenerationPreviewSnapshot.EventSequence == 1,
            "Explicit authenticated feed reset admits the restarted Worker's lower sequence.");
        var queuedOld = Event(Payload(body: "Restarted feed stale", effect: "effect-e", elapsed: 2), 2);
        var launcher = Task.Run(() => workspace.Notify(queuedOld), deadline.Token);
        Check(launcher.Wait(TimeSpan.FromSeconds(2)), "Background observation must return without blocking its dispatcher callback.");
        workspace.ResetGenerationPreviewFeed();
        workspace.Notify(Event(Payload(body: "Fresh replacement feed", effect: "effect-e", elapsed: 1), 1));
        await launcher; await Flush();
        Check(Public() == "Fresh replacement feed", "A queued event from the previous feed generation cannot replace the fresh feed's draft.");
        workspace.Notify(Event(Payload(body: "Fresh replacement feed Lifecycle draft", effect: "effect-e", elapsed: 200))); await Flush();
        source.RootVisual = null; await Flush();
        Check(!workspace.IsLoaded && host.Visibility == Visibility.Collapsed && Public().Length == 0, "Unload clears provisional text.");
        workspace.Notify(Event(Payload(body: "unloaded", effect: "effect-e", elapsed: 300)));
        Check(host.Visibility == Visibility.Collapsed, "Unloaded views ignore incoming previews.");
        source.RootVisual = workspace; await Flush();
        workspace.Notify(Event(Payload(body: "Reloaded draft", effect: "effect-e", elapsed: 400))); await Flush();
        workspace.Reset();
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0 && workspace.Snapshot is null,
            "Reset clears content and binding without retaining a second history.");
        workspace.Notify(Event(Payload(body: "reset stale", effect: "effect-e", elapsed: 500)));
        Check(host.Visibility == Visibility.Collapsed, "An old conversation event cannot populate a reset view.");
        workspace.BeginConversation("preview-chat"); await workspace.OpenRunSnapshotAsync("preview-run", deadline.Token);
        workspace.Notify(Event(Payload(body: "Before dispose", effect: "effect-e", elapsed: 600))); await Flush();
        workspace.Dispose();
        workspace.Notify(Event(Payload(body: "Disposed stale", effect: "effect-e", elapsed: 700)));
        Check(host.Visibility == Visibility.Collapsed && Public().Length == 0 && reports.Count == 0, "Dispose clears text and makes late events inert.");
        return checks;
    }

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing preview fixture field " + name));

    private static int VerifyBodyGeometry(RichTextBox actualPreview, Run actualRun)
    {
        const string prose = "An ordinary response flows through the conversation while the reader follows its progress. Each additional phrase should keep the same words visible as the saved answer replaces the provisional body. The text stays plain so the comparison measures ordinary wrapping rather than heading or code formatting.";
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var finalAtActualWidth = (RichTextBox)MarkdownView.Render(actualRun.Text);
        finalAtActualWidth.Width = actualPreview.ActualWidth;
        using (var actualSource = new HwndSource(new HwndSourceParameters("Preview actual-width geometry")
        { Width = (int)Math.Ceiling(actualPreview.ActualWidth), Height = 100, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = finalAtActualWidth })
        {
            finalAtActualWidth.Measure(new Size(actualPreview.ActualWidth, 100));
            finalAtActualWidth.Arrange(new Rect(0, 0, actualPreview.ActualWidth, finalAtActualWidth.DesiredSize.Height));
            finalAtActualWidth.UpdateLayout();
            var finalRun = ((Paragraph)finalAtActualWidth.Document.Blocks.FirstBlock!).Inlines.FirstInline as Run;
            Check(finalRun?.Text == actualRun.Text && actualPreview.ActualWidth > 100,
                "The actual preview and equal-width final plain card expose identical public characters.");
            var provisional = actualRun.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var saved = finalRun!.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Check(!provisional.IsEmpty && !saved.IsEmpty && Math.Abs(provisional.X - saved.X) < 1 && Math.Abs(provisional.Y - saved.Y) < 1,
                "The actual provisional body's first glyph aligns with an equal-width final Markdown body.");
        }

        var previewParagraph = SelectableMarkdown.NormalBodyParagraph();
        var previewRun = new Run(prose); previewParagraph.Inlines.Add(previewRun);
        var preview = SelectableMarkdown.View(new FlowDocument(previewParagraph)); preview.Width = 360;
        var final = (RichTextBox)MarkdownView.Render(prose); final.Width = 360;
        var pair = new StackPanel(); pair.Children.Add(preview); pair.Children.Add(final);
        using var source = new HwndSource(new HwndSourceParameters("Preview wrapped geometry")
        { Width = 360, Height = 600, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 })
        { RootVisual = pair };
        pair.Measure(new Size(360, 600)); pair.Arrange(new Rect(0, 0, 360, pair.DesiredSize.Height)); pair.UpdateLayout();
        var finalParagraph = final.Document.Blocks.FirstBlock as Paragraph;
        var finalRunForProse = finalParagraph?.Inlines.FirstInline as Run;
        Check(finalRunForProse?.Text == prose && previewRun.Text == prose && Math.Abs(preview.ActualWidth - final.ActualWidth) < 1,
            "The wrap comparison uses exact identical plain prose at equal measured widths.");
        var previewTops = new HashSet<double>(); var finalTops = new HashSet<double>();
        for (var i = 0; i < prose.Length; i++)
        {
            var previewPosition = previewRun.ContentStart.GetPositionAtOffset(i, LogicalDirection.Forward)
                ?? throw new InvalidOperationException("The provisional run ended before its public characters.");
            var finalPosition = finalRunForProse!.ContentStart.GetPositionAtOffset(i, LogicalDirection.Forward)
                ?? throw new InvalidOperationException("The final run ended before its public characters.");
            var provisional = previewPosition.GetCharacterRect(LogicalDirection.Forward);
            var saved = finalPosition.GetCharacterRect(LogicalDirection.Forward);
            Check(!provisional.IsEmpty && !saved.IsEmpty && Math.Abs(provisional.X - saved.X) < 1 && Math.Abs(provisional.Y - saved.Y) < 1,
                "The same character's leading-edge rectangle agrees in provisional and final plain layout.");
            previewTops.Add(Math.Round(provisional.Top, 2)); finalTops.Add(Math.Round(saved.Top, 2));
        }
        Check(previewTops.Count >= 2 && previewTops.SetEquals(finalTops),
            "The plain response wraps over several lines at the same glyph-line positions.");
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
