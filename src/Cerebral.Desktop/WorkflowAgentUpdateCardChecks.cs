using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Deterministic journal fixtures on actual offscreen WPF controls. No Worker,
/// collaboration result paging, model, provider, Source or network is invoked.</summary>
public static class WorkflowAgentUpdateCardChecks
{
    public static async Task<int> VerifyAsync(string? previewPath = null)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        const string taskId = "15cad059a6394ab397847892685c8719";
        const string attemptId = "71a28fa35dd64112a15c9983d594c250";
        Dictionary<string, object?> Metadata() => new(StringComparer.Ordinal)
        {
            ["taskId"] = taskId, ["attemptId"] = attemptId,
            ["targetConversationId"] = "card-target", ["targetRunRevision"] = 7L
        };
        var fullText = @"C:\reviewed\artifact\handoff.json" + "\n" + new string('a', 64) +
            "\n\nThe private candidate is ready for review. The recorded checks passed; Source adoption remains pending. café 😀 👩‍💻 e\u0301" +
            "\n\n" + string.Join('\n', Enumerable.Range(1, 160).Select(index => "Recorded public detail " + index + ": the accepted result stays selectable and readable.")) +
            "\n[Result text truncated; inspect the bounded result pages.]";
        var adopted = new CerebralEvent("card-adopted", "card-source", taskId, 3, "collaboration.result",
            new(EventActorKind.Service, "cerebral.collaboration"), [new TextContentPart(fullText)],
            new(ModelInclusion.Evidence, HumanVisibility.Transcript, RetentionPolicy.Durable),
            new(EventSourceKind.Service, "cerebral.collaboration", JobId: "card-target-run"), now,
            CerebralJson.Element(Metadata()), CorrelationId: taskId);
        bool Project(CerebralEvent item) => WorkflowWorkspace.TryProjectAgentUpdate(item, "card-source", out _);
        Check(Project(adopted), "An exact durable adopted result must have a specialized card.");
        Check(WorkflowWorkspace.TryProjectAgentUpdate(adopted, "card-source", out var projected) &&
            projected.Sender == "Collaboration service" && projected.SenderRole is null && projected.Text == fullText &&
            projected.TargetRunId == "card-target-run" && projected.TargetRunRevision == 7,
            "The older event uses true service provenance and retains complete public content.");
        foreach (var malformed in new[]
        {
            adopted with { Type = CerebralEventTypes.Message },
            adopted with { Type = "workflow.result" },
            adopted with { Type = CerebralEventTypes.ToolResult },
            adopted with { SchemaVersion = 2 },
            adopted with { Sequence = 0 },
            adopted with { ConversationId = "another-owner" },
            adopted with { TurnId = "another-task" },
            adopted with { CorrelationId = "another-task" },
            adopted with { Actor = new(EventActorKind.Agent, "cerebral.collaboration") },
            adopted with { Actor = new(EventActorKind.Service, "another.service") },
            adopted with { Provenance = new(EventSourceKind.Agent, "cerebral.collaboration", JobId: "card-target-run") },
            adopted with { Provenance = new(EventSourceKind.Service, "another.service", JobId: "card-target-run") },
            adopted with { Provenance = new(EventSourceKind.Service, "cerebral.collaboration") },
            adopted with { Projection = new(ModelInclusion.Evidence, HumanVisibility.Hidden, RetentionPolicy.Durable) },
            adopted with { Projection = new(ModelInclusion.Evidence, HumanVisibility.Sidebar, RetentionPolicy.Durable) },
            adopted with { Projection = new(ModelInclusion.Evidence, HumanVisibility.Activity, RetentionPolicy.Durable) },
            adopted with { Projection = new(ModelInclusion.Never, HumanVisibility.Transcript, RetentionPolicy.Durable) },
            adopted with { Projection = new(ModelInclusion.Evidence, HumanVisibility.Transcript, RetentionPolicy.Rolling) },
            adopted with { Content = [] },
            adopted with { Content = [new StructuredContentPart("private.result", CerebralJson.Element(new { reasoning = "private" }))] },
            adopted with { Content = [new TextContentPart("first"), new TextContentPart("second")] },
            adopted with { Content = [new TextContentPart(new string('x', 33 * 1024))] },
            adopted with { Content = [new TextContentPart("invalid\uD800")] },
            adopted with { Data = CerebralJson.Element("card-looking prose") }
        }) Check(!Project(malformed), "Unrelated, private or incorrectly correlated events must not become collaboration cards.");
        foreach (var change in new (string Key, object? Value)[]
        {
            ("taskId", "not-a-task"), ("taskId", Guid.Empty.ToString("N")),
            ("attemptId", "not-an-attempt"), ("attemptId", Guid.Empty.ToString("N")),
            ("targetConversationId", "card-source"), ("targetConversationId", ""),
            ("targetRunRevision", 0), ("targetRunRevision", 1.5), ("targetRunRevision", "7")
        })
        {
            var metadata = Metadata(); metadata[change.Key] = change.Value;
            Check(!Project(adopted with { Data = CerebralJson.Element(metadata) }), "Required adopted-result metadata must retain its exact typed coordinates.");
        }
        var missing = Metadata(); missing.Remove("attemptId");
        Check(!Project(adopted with { Data = CerebralJson.Element(missing) }), "An incomplete attempt binding must not become a card.");
        using (var duplicate = JsonDocument.Parse("{\"taskId\":\"" + taskId + "\",\"taskId\":\"" + taskId +
            "\",\"attemptId\":\"" + attemptId + "\",\"targetConversationId\":\"card-target\",\"targetRunRevision\":7}"))
            Check(!Project(adopted with { Data = duplicate.RootElement.Clone() }), "Duplicate correlation fields must be rejected.");

        var namedMetadata = Metadata();
        namedMetadata["targetProfileId"] = "accepted-agent";
        namedMetadata["targetProfileName"] = "Accepted Builder";
        namedMetadata["targetProfileRole"] = "builder";
        var named = adopted with { Id = "card-named", Sequence = 4, Data = CerebralJson.Element(namedMetadata),
            Provenance = adopted.Provenance with { AgentId = "accepted-agent" } };
        Check(WorkflowWorkspace.TryProjectAgentUpdate(named, "card-source", out var namedProjection) &&
            namedProjection.Sender == "Accepted Builder" && namedProjection.SenderRole == "builder",
            "Only matching immutable event metadata may supply the accepted sender name and role.");
        var wrongName = new Dictionary<string, object?>(namedMetadata) { ["targetProfileId"] = "another-agent" };
        Check(WorkflowWorkspace.TryProjectAgentUpdate(named with { Data = CerebralJson.Element(wrongName) }, "card-source", out var mismatch) &&
            mismatch.Sender == "Agent accepted-agent" && mismatch.SenderRole is null,
            "A mismatched profile enrichment may not lend another agent its name or role.");
        var unboundedName = new Dictionary<string, object?>(namedMetadata) { ["targetProfileName"] = new string('n', 257) };
        Check(WorkflowWorkspace.TryProjectAgentUpdate(named with { Data = CerebralJson.Element(unboundedName) }, "card-source", out var bounded) &&
            bounded.Sender == "Agent accepted-agent", "Unbounded sender metadata must fall back to the exact recorded agent ID.");
        var controlName = new Dictionary<string, object?>(namedMetadata) { ["targetProfileName"] = "Wrong\nname" };
        Check(WorkflowWorkspace.TryProjectAgentUpdate(named with { Data = CerebralJson.Element(controlName) }, "card-source", out var control) &&
            control.Sender == "Agent accepted-agent", "Control characters in attribution must not create fake label lines.");
        Check(WorkflowWorkspace.TryProjectAgentUpdate(adopted with { Actor = adopted.Actor with { DisplayName = "Invented sender" } }, "card-source", out var service) &&
            service.Sender == "Collaboration service", "A service actor's display name is not target-agent attribution.");
        var privateMetadata = Metadata();
        privateMetadata["reasoning"] = "PRIVATE_DECISION_SENTINEL";
        privateMetadata["summary"] = "PRIVATE_DATA_SUMMARY_SENTINEL";
        privateMetadata["configuration"] = new { accessKey = "PRIVATE_KEY_SENTINEL" };
        adopted = adopted with { Data = CerebralJson.Element(privateMetadata) };
        Check(Project(adopted), "Extra internal envelope data must not suppress an otherwise exact public adoption.");

        var preview = WorkflowWorkspace.AgentUpdatePreview(fullText);
        Check(preview.StartsWith("The private candidate is ready for review.", StringComparison.Ordinal) &&
            !preview.Contains("handoff.json", StringComparison.Ordinal) && !preview.Contains(new string('a', 64), StringComparison.Ordinal),
            "A card preview starts with actual outcome prose instead of a path or fingerprint.");
        Check(preview.Contains("café 😀 👩‍💻 e\u0301", StringComparison.Ordinal), "Public Unicode prose must survive preview projection.");
        Check(WorkflowWorkspace.AgentUpdatePreview("SHA256: " + new string('b', 64) + "\nArtifact: C:\\proof\\candidate.json") ==
            "Result received. Show more to read the recorded content.", "An artifact-only update needs an honest generic preview.");
        Check(WorkflowWorkspace.AgentUpdatePreview("```json\n{\"reasoning\":\"not-preview-prose\"}\n```\n\nRecorded outcome is available.") ==
            "Recorded outcome is available.", "Code envelopes must not become the card's first readable summary.");
        var unicodePreview = WorkflowWorkspace.AgentUpdatePreview(new string('x', 510) + "👩‍💻 e\u0301 more");
        Check(unicodePreview.Length <= 512 && !unicodePreview.Contains('\uD83D') && unicodePreview.EndsWith('…'),
            "The preview budget must not split a joined Unicode text element.");

        var original = new CerebralEvent("card-original", "card-source", "card-run", 1,
            CerebralEventTypes.Message, new(EventActorKind.User), [new TextContentPart("Review the delegated public result.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now);
        var ordinaryAnswer = new CerebralEvent("card-answer", "card-source", "card-run", 2,
            CerebralEventTypes.Message, new(EventActorKind.Agent, "lead"),
            [new TextContentPart(string.Join('\n', Enumerable.Range(1, 65).Select(index => "Full ordinary answer line " + index)))],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now);
        var events = new List<CerebralEvent> { original, ordinaryAnswer, adopted, named };
        var saved = new WorkflowRun { Id = "card-run", Revision = 1, Status = WorkflowStatus.Running,
            Submission = new("card-run", "card-source", "lead", "fixture:cards", "1.0.0",
                "Review the delegated public result.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now };
        var calls = 0;
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls++;
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(saved),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events, snapshotSequence = events[^1].Sequence, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { original }, snapshotSequence = events[^1].Sequence, nextAfterSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected update-card fixture command " + request.Operation)
            });
        }
        var reports = new List<string>();
        using var workspace = new WorkflowWorkspace(Dispatch,
            () => [new() { Id = "accepted-agent", Name = "MUTABLE_PROFILE_SENTINEL", Role = "lead" }],
            (message, _) => reports.Add(message), embedded: true);
        workspace.BeginConversation("card-source");
        await workspace.OpenRunSnapshotAsync("card-run", deadline.Token);
        var frame = new Border { Background = Ui.Surface, Padding = new Thickness(12), Child = workspace };
        using var source = new HwndSource(new HwndSourceParameters("Cerebral adopted public update cards")
        { Width = 710, Height = 490, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 }) { RootVisual = frame };
        async Task Flush(double width = 710)
        {
            await dispatcher.InvokeAsync(() => { frame.Measure(new Size(width, 490)); frame.Arrange(new Rect(0, 0, width, 490)); frame.UpdateLayout(); }, DispatcherPriority.Loaded);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        await Flush();
        Border Card(string id) => Descendants(workspace).OfType<Border>().Single(view => AutomationProperties.GetAutomationId(view) == "WorkflowAgentUpdate-" + id);
        Button Toggle(Border card) => Descendants(card).OfType<Button>().Single();
        var card = Card("card-adopted");
        var namedCard = Card("card-named");
        var toggle = Toggle(card);
        var page = Field<ScrollViewer>(workspace, "transcriptScroller");
        var collapsedHeight = card.ActualHeight;
        Check(workspace.IsLoaded && workspace.AgentUpdateCardCount == 2, "Actual offscreen WPF must mount exactly the two typed public update cards.");
        Check(card.HorizontalAlignment == HorizontalAlignment.Right && card.MaxWidth == 520 && card.ActualWidth <= 520,
            "Public update cards use a compact right-aligned surface within the single page timeline.");
        Check(Descendants(card).OfType<TextBlock>().Any(text => text.Text == "Completed result received"),
            "The completion marker records result adoption, without claiming acceptance or execution verification.");
        Check(Descendants(namedCard).OfType<TextBlock>().Any(text => text.Text.Contains("Accepted Builder · builder", StringComparison.Ordinal)) &&
            !VisibleText(namedCard).Contains("MUTABLE_PROFILE_SENTINEL", StringComparison.Ordinal),
            "Card attribution comes from accepted event metadata instead of the mutable profile list.");
        Check(toggle.IsTabStop && toggle.Focusable && toggle.Content?.ToString() == "Show more" &&
            AutomationProperties.GetName(toggle).Contains("full adopted result", StringComparison.Ordinal),
            "Expansion has a normal keyboard button and a descriptive accessibility name.");
        var callsBeforeExpansion = calls;
        page.ScrollToVerticalOffset(130); await Flush();
        var manualOffset = page.VerticalOffset;
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Flush();
        var body = Descendants(card).OfType<StackPanel>().Single(panel => AutomationProperties.GetAutomationId(panel) == "WorkflowAgentUpdateBody-card-adopted");
        var rich = Descendants(body).OfType<RichTextBox>().Single();
        var textRange = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
        Check(card.ActualHeight > collapsedHeight + 100 && body.Visibility == Visibility.Visible && toggle.Content?.ToString() == "Show less",
            "Show more expands the actual full adopted content in the existing page scroller.");
        Check(textRange.Contains("Recorded public detail 160", StringComparison.Ordinal) &&
            textRange.Contains("[Result text truncated; inspect the bounded result pages.]", StringComparison.Ordinal),
            "Expansion retains the complete adopted text and the original producer truncation notice.");
        Check(!VisibleText(card).Contains("PRIVATE_", StringComparison.Ordinal), "Internal event Data must never become preview or expanded public text.");
        Check(calls == callsBeforeExpansion && System.Windows.Input.Keyboard.FocusedElement == focused &&
            Math.Abs(page.VerticalOffset - manualOffset) < 1,
            "Expansion performs no command or result paging and does not force focus or follow-to-end.");
        rich.Selection.Select(rich.Document.ContentStart, rich.Document.ContentStart.GetPositionAtOffset(30)!);
        var selection = rich.Selection.Text;
        Check(selection.Length > 0, "The complete adopted result remains selectable.");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Flush();
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Flush();
        Check(ReferenceEquals(rich, Descendants(body).OfType<RichTextBox>().Single()) && rich.Selection.Text == selection,
            "Show less/more preserves the same full-text control and selection.");
        page.ScrollToVerticalOffset(130); await Flush();
        manualOffset = page.VerticalOffset;

        events.Add(new("card-activity", "card-source", "card-run", 5, CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent, "lead"), [], new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Agent), now,
            CerebralJson.Element(new ToolCallData("card-call", "cerebral.host:catalog", CerebralJson.Element(new { names = new[] { "file.read" } })))));
        await workspace.InspectRunAsync("card-run", deadline.Token); await Flush();
        Check(ReferenceEquals(card, Card("card-adopted")) && ReferenceEquals(toggle, Toggle(card)) &&
            ReferenceEquals(rich, Descendants(body).OfType<RichTextBox>().Single()) && rich.Selection.Text == selection,
            "An unrelated operational receipt preserves card, expansion and full-text identity.");
        Check(Math.Abs(page.VerticalOffset - manualOffset) < 2, "An unrelated activity render must preserve the reader's manual page position.");
        await Flush(340);
        Check(card.ActualWidth < 340 && rich.ActualWidth < 340 && rich.Selection.Text == selection,
            "Narrow reflow wraps the existing full-text surface without losing selection or clipping it horizontally.");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Flush(340);
        var cardPreview = Descendants(card).OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "WorkflowAgentUpdatePreview-card-adopted");
        Check(cardPreview.TextWrapping == TextWrapping.Wrap && cardPreview.ActualHeight <= 63.1 && cardPreview.TextTrimming == TextTrimming.CharacterEllipsis,
            "The collapsed preview stays within three readable wrapped lines at narrow width.");
        var normal = Descendants(workspace).OfType<RichTextBox>().First(text =>
            new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text.Contains("Full ordinary answer line 65", StringComparison.Ordinal));
        Check(normal.IsReadOnly && normal.ActualHeight > 300 && double.IsPositiveInfinity(normal.MaxHeight),
            "Normal public answers retain their complete, unconstrained conversation presentation.");

        // Exercise the bounded card cache independently from transport pagination. The rows
        // remain typed deterministic events; no native/Worker history is fabricated as proof.
        var displayed = Field<List<CerebralEvent>>(workspace, "displayedEvents");
        displayed.Clear();
        var factory = Method("TranscriptCard");
        var reconcile = Method("ReconcileAgentUpdateCardWindow");
        var unsupported = (FrameworkElement)factory.Invoke(workspace,
            [adopted with { CorrelationId = "unsupported-correlation" }, new Dictionary<string, string>()])!;
        Check(!AutomationProperties.GetAutomationId(unsupported).StartsWith("WorkflowAgentUpdate-", StringComparison.Ordinal) &&
            !VisibleText(unsupported).Contains("PRIVATE_", StringComparison.Ordinal),
            "An unsupported adoption keeps its ordinary public body without exposing its private correlation envelope.");
        const int cacheBudget = WorkflowConversationClient.MaximumPageEvents * WorkflowConversationClient.MaximumRecentPages + WorkflowWorkspace.MaximumRetainedTranscriptEvents;
        for (var index = 1; index <= cacheBudget + 1; index++)
        {
            var id = index.ToString("x32");
            displayed.Add(adopted with { Id = "bounded-card-" + index, TurnId = id, CorrelationId = id, Sequence = index,
                Content = [new TextContentPart("Recorded bounded card " + index)],
                Data = CerebralJson.Element(new { taskId = id, attemptId, targetConversationId = "card-target", targetRunRevision = 7 }) });
        }
        reconcile.Invoke(workspace, null);
        foreach (var item in displayed) _ = factory.Invoke(workspace, [item, new Dictionary<string, string>()]);
        Check(workspace.AgentUpdateCardCount == cacheBudget && card.Child is null,
            "The cache stays within the existing recent-window budget and disposes rows outside the exact window.");
        var callsBeforeClear = calls;
        workspace.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(workspace.AgentUpdateCardCount == 0, "Unload removes cached expansion state and controls.");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == callsBeforeClear && card.Child is null, "A retained stale button cannot perform an action after its card is pruned or unloaded.");
        reconcile.Invoke(workspace, null);
        _ = factory.Invoke(workspace, [displayed[^1], new Dictionary<string, string>()]);
        Check(workspace.AgentUpdateCardCount == 1, "A fresh bound card can be rebuilt after unload cleanup.");
        workspace.Reset();
        Check(workspace.AgentUpdateCardCount == 0, "Conversation reset clears sender/content/expansion state.");
        workspace.BeginConversation("card-source");
        await workspace.OpenRunSnapshotAsync("card-run", deadline.Token); await Flush();
        Check(workspace.AgentUpdateCardCount == 2, "Reopening the selected conversation creates fresh correct cards.");
        workspace.Dispose();
        Check(workspace.AgentUpdateCardCount == 0 && reports.Count == 0, "Disposal removes card state without errors or background work.");
        if (previewPath is not null) checks += await CapturePreviewAsync(previewPath, deadline.Token);
        return checks;
    }

    private static async Task<int> CapturePreviewAsync(string previewPath, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "agent-card-preview-chat";
        const string runId = "agent-card-preview-run";
        const string builderTask = "f1e7736d3c0d4d82a4486534757f4ba4";
        const string reviewerTask = "340b33f29b9140ec9253c67450daf06e";
        const string attemptId = "ebca285003844d7e80c52e31d3475157";
        var original = new CerebralEvent("agent-card-preview-user", conversationId, runId, 1,
            CerebralEventTypes.Message, new(EventActorKind.User),
            [new TextContentPart("Show the public handoff beside the regular conversation answer.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now);
        var answer = new CerebralEvent("agent-card-preview-answer", conversationId, runId, 2,
            CerebralEventTypes.Message, new(EventActorKind.Agent, "fixture-lead", "Cerebral"),
            [new TextContentPart("The component handoff is available for review. The final answer stays in this conversation.\n\nExpand an agent's result to read its recorded details.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.Agent), now);
        CerebralEvent Result(string id, string taskId, long sequence, string agentId, string name, string role, string text) =>
            new(id, conversationId, taskId, sequence, "collaboration.result", new(EventActorKind.Service, "cerebral.collaboration"),
                [new TextContentPart(text)], new(ModelInclusion.Evidence, HumanVisibility.Transcript, RetentionPolicy.Durable),
                new(EventSourceKind.Service, "cerebral.collaboration", AgentId: agentId, JobId: "fixture-target-" + taskId), now,
                CerebralJson.Element(new { taskId, attemptId, targetConversationId = "fixture-target-chat-" + taskId,
                    targetRunRevision = 7, targetProfileId = agentId, targetProfileName = name, targetProfileRole = role,
                    reasoning = "PRIVATE_PREVIEW_SENTINEL" }), CorrelationId: taskId);
        var events = new[]
        {
            original, answer,
            Result("agent-card-preview-builder", builderTask, 3, "fixture-builder", "Accepted Builder", "builder",
                "The component changes are ready for the owner to inspect. The public handoff records what changed and the next review step.\n\nThe complete adopted result remains available through Show more."),
            Result("agent-card-preview-reviewer", reviewerTask, 4, "fixture-reviewer", "Accepted Reviewer", "reviewer",
                "The public review result is available.\n\nObserved: sender attribution, expansion and ordinary answers stay in one conversation timeline.\n\nNext: the owner can review the recorded result. This is a deterministic UI fixture.")
        };
        var saved = new WorkflowRun { Id = runId, Revision = 1, Status = WorkflowStatus.Completed,
            Submission = new(runId, conversationId, "fixture-lead", "fixture:agent-card-preview", "1.0.0",
                ((TextContentPart)original.Content[0]).Text, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(saved),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events, snapshotSequence = 4L, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { original }, snapshotSequence = 4L, nextAfterSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected preview-scene fixture command " + request.Operation)
            });
        }
        var reports = new List<string>();
        using var workspace = new WorkflowWorkspace(Dispatch, () => [], (message, _) => reports.Add(message), embedded: true);
        workspace.BeginConversation(conversationId);
        await workspace.OpenRunSnapshotAsync(runId, token);
        const int width = 760, height = 810;
        var frame = new Border { Background = Ui.Surface, Padding = new Thickness(16), Child = workspace };
        using var source = new HwndSource(new HwndSourceParameters("Cerebral public agent-card preview fixture")
        { Width = width, Height = height, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 }) { RootVisual = frame };
        async Task Flush()
        {
            await workspace.Dispatcher.InvokeAsync(() => { frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout(); }, DispatcherPriority.Loaded, token);
            await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, token);
        }
        await Flush();
        var reviewer = Descendants(workspace).OfType<Border>().Single(view =>
            AutomationProperties.GetAutomationId(view) == "WorkflowAgentUpdate-agent-card-preview-reviewer");
        var toggle = Descendants(reviewer).OfType<Button>().Single();
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var page = Field<ScrollViewer>(workspace, "transcriptScroller");
        page.ScrollToTop();
        await Flush();
        var builder = Descendants(workspace).OfType<Border>().Single(view =>
            AutomationProperties.GetAutomationId(view) == "WorkflowAgentUpdate-agent-card-preview-builder");
        if (Descendants(builder).OfType<Button>().Single().Content?.ToString() != "Show more" ||
            toggle.Content?.ToString() != "Show less" || !VisibleText(builder).Contains("Accepted Builder · builder", StringComparison.Ordinal) ||
            !VisibleText(reviewer).Contains("Accepted Reviewer · reviewer", StringComparison.Ordinal))
            throw new InvalidOperationException("The preview scene did not retain the actual fixture attribution and expansion state.");
        var visible = VisibleText(frame);
        if (visible.Contains("PRIVATE_", StringComparison.Ordinal) || !visible.Contains("The final answer stays in this conversation.", StringComparison.Ordinal) ||
            !visible.Contains("Observed: sender attribution", StringComparison.Ordinal) || reports.Count != 0)
            throw new InvalidOperationException("The preview scene did not expose only the intended adopted public bodies and normal answer.");
        if (page.ScrollableHeight > 1)
            throw new InvalidOperationException("The preview scene must fit the compact and expanded cards together in one viewport.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(Path.GetFullPath(previewPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        encoder.Save(file);
        return 3;
    }

    private static MethodInfo Method(string name) => typeof(WorkflowWorkspace).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(WorkflowWorkspace workspace, string name) => (T)typeof(WorkflowWorkspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
    private static string VisibleText(DependencyObject root) => string.Join('\n', Descendants(root).Select(value => value switch
    {
        TextBlock text => text.Text,
        TextBox text => text.Text,
        RichTextBox rich => new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text,
        _ => ""
    }));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
