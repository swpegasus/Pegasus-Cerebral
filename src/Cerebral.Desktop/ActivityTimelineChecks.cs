using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Pure projections and unshown WPF controls. No transport, database, clipboard or window.</summary>
internal static class ActivityTimelineChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); count++; }
        const string path = @"C:\Fixture\AgentRouting.cs";
        var context = new ActivityTimelineContext("fixture-owner", ActivityTimelineRole.Worker, "task-1", "Activity fixture")
        { OwningMainConversationId = "chat-1", MemberCoordinate = "member-worker-2" };
        JsonElement Json(object? value) => CerebralJson.Element(value);
        CerebralEvent Call(string id, long sequence, object? arguments = null) => new(id, "chat-1", "run-1", sequence,
            CerebralEventTypes.ToolCall, new(EventActorKind.Agent, "worker-2", "Worker 2"), [],
            new(ModelInclusion.Required, HumanVisibility.Activity),
            new(EventSourceKind.Agent, AgentId: "worker-2", JobId: "job-1"), DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Json(new ToolCallData(id, "cerebral.host:tool", Json(new { name = "file.read", arguments = arguments ?? new { path } }))));
        CerebralEvent Receipt(string callId, long sequence, string state = "Succeeded", bool success = true) =>
            new("receipt-" + sequence, "chat-1", "run-1", sequence, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool, "cerebral.host:tool"),
                [new StructuredContentPart("workflow.command.result", Json(new { state, data = new { stdout = "Read completed", receiptId = "receipt-safe" } }))],
                new(ModelInclusion.Evidence, HumanVisibility.Activity),
                new(EventSourceKind.Tool, AgentId: "worker-2", JobId: "job-1"), DateTimeOffset.UnixEpoch.AddSeconds(sequence),
                Json(new ToolResultData(callId, "cerebral.host:tool", success)));

        var read = WorkflowWorkspace.DescribeRecordedActivity("mcp.pegasus_cortex.pao", Json(new { op = "read_lines", src = path }));
        Check(read.OperationType == "Read" && read.FileName == "AgentRouting.cs" && read.FilePath == path,
            "A typed PAO file read exposes operation, filename and full recorded path independently.");
        Check(read.Route == "PAO over MCP" && read.OperationType != "MCP" && read.Runtime == "Not recorded",
            "Transport is not guessed to be the operation or runtime.");
        var pwsh = WorkflowWorkspace.DescribeRecordedActivity("mcp.pegasus_cortex.pao", Json(new { op = "pwsh", src = @"C:\Fixture\Inspect.ps1" }));
        Check(pwsh.Runtime == "PowerShell" && pwsh.Route == "PAO over MCP" && pwsh.FileName == "Inspect.ps1",
            "PowerShell runtime, PAO route and typed script target remain separate.");
        foreach (var args in new[] { default(JsonElement), Json("malformed"), Json(new { op = (string?)null }), Json(new { op = 5 }), Json(new { op = "unknown", src = path }) })
        {
            var missing = WorkflowWorkspace.DescribeRecordedActivity("mcp.pegasus_cortex.pao", args);
            Check(missing.OperationType == "Operation not recorded" && missing.FileName == "—" && missing.FilePath == "—",
                "Missing, malformed and unknown operations do not fabricate a target or throw.");
        }
        var noFile = WorkflowWorkspace.DescribeRecordedActivity("agents.delegate", Json(new { path, target = "worker" }));
        Check(noFile.OperationType == "Delegate task" && noFile.FilePath == "—", "A non-file contract cannot borrow an arbitrary path field.");
        var copy = WorkflowWorkspace.DescribeRecordedActivity("mcp.pegasus_cortex.pao", Json(new { op = "cp", src = path, dst = @"C:\Fixture\Copy.cs" }));
        Check(copy.FileName == "Copy.cs" && copy.TargetRole.Contains("destination", StringComparison.Ordinal),
            "A multi-target operation explicitly identifies its primary destination.");
        var missingDestination = WorkflowWorkspace.DescribeRecordedActivity("mcp.pegasus_cortex.pao", Json(new { op = "cp", src = path }));
        Check(missingDestination.FilePath == "—" && missingDestination.TargetRole.StartsWith("Destination not recorded", StringComparison.Ordinal),
            "A missing copy destination cannot be replaced by the recorded source as its primary target.");
        var missingReadTarget = WorkflowWorkspace.DescribeRecordedActivity("file.read", Json(new { }));
        Check(missingReadTarget.TargetRole == "File target not recorded" && missingReadTarget.FilePath == "—",
            "A file operation with missing target evidence is explicitly unknown rather than a no-file operation.");

        var events = new[] { Call("a", 1), Call("b", 2), Receipt("b", 3), Receipt("a", 4) };
        var projected = WorkflowWorkspace.ProjectActivityTimelineEvents(events, context);
        Check(projected.Items.All(item => item.Agent == "Worker 2" && item.OwningMainConversationId == "chat-1" &&
            item.ConversationId == "chat-1" && item.MemberCoordinate == "member-worker-2"),
            "Rows preserve recorded actor names, owning Main, source conversation and stable member separately.");
        foreach (var name in new[] { "Main agent", "Coordinator agent", "Named worker" })
            Check(WorkflowWorkspace.RecordedActivityAgent(events[0] with { Actor = new(EventActorKind.Agent, "worker-2", name) }) == name,
                "Every role uses the actual named dispatch actor rather than its panel or tool caption.");
        foreach (var actor in new[] { new EventActor(EventActorKind.Agent, "worker-2"),
            new EventActor(EventActorKind.Agent, "foreign", "Misleading name"), new EventActor(EventActorKind.Tool, "worker-2", "Tool label"),
            new EventActor(EventActorKind.Agent, "worker-2", "Bad\nname"), new EventActor(EventActorKind.Agent, "worker-2", new string('x', 129)) })
            Check(WorkflowWorkspace.RecordedActivityAgent(events[0] with { Actor = actor }) == "Agent not recorded",
                "Missing, conflicting, malformed and tool actor labels do not invent an agent.");
        Check(WorkflowWorkspace.ProjectActivityTimelineEvents(events, context, names: new Dictionary<string, string>
            { ["worker-2"] = "Renamed current profile" }).Items.All(item => item.Agent == "Worker 2"),
            "Renaming a mutable profile cannot rewrite recorded dispatch identity.");
        Check(projected.Items.Count == 2 && projected.Items.Single(item => item.EventIds.Contains("a")).EventIds.Contains("receipt-4") &&
            projected.Items.Single(item => item.EventIds.Contains("b")).EventIds.Contains("receipt-3"),
            "Two same-tool calls pair only with their exact typed call IDs even when receipts arrive out of order.");
        var first = WorkflowWorkspace.ProjectActivityTimelineEvents([events[0]], context).Items.Single();
        Check(first.Key == projected.Items.Single(item => item.EventIds.Contains("a")).Key,
            "A late receipt keeps the original operation row identity.");
        Check(first.Key != WorkflowWorkspace.ProjectActivityTimelineEvents([events[0]], context with { TaskId = "other-task" }).Items.Single().Key,
            "Distinct accepted task scopes cannot collide even when a chat/run/event is shared.");
        var conflicting = WorkflowWorkspace.ProjectActivityTimelineEvents(events.Append(Receipt("a", 5, "Failed", false)).ToArray(), context);
        Check(conflicting.Items.Count == 3 && conflicting.Items.Last().State == "Unpaired receipt",
            "Additional conflicting terminal evidence stays reviewable and cannot silently replace completion.");
        var backwards = WorkflowWorkspace.ProjectActivityTimelineEvents([Receipt("late", 1), Call("late", 2)], context);
        Check(backwards.Items.Count == 2 && backwards.Items[0].State == "Unpaired receipt",
            "A result recorded before a call is not retroactively upgraded to an exact pair.");
        var good = Receipt("a", 2);
        foreach (var wrong in new[]
        {
            good with { ConversationId = "other-chat" }, good with { TurnId = "other-run" },
            good with { Actor = new(EventActorKind.Agent, "intruder") },
            good with { Actor = new(EventActorKind.Tool, "other-tool") },
            good with { Provenance = good.Provenance with { AgentId = "other-agent" } },
            good with { Provenance = good.Provenance with { JobId = "other-job" } }
        })
            Check(WorkflowWorkspace.ProjectActivityTimelineEvents([events[0], wrong], context).Items.Count == 2,
                "Foreign actor/chat/run/job provenance never creates a correlated receipt.");
        Check(WorkflowWorkspace.ProjectActivityTimelineEvents([Call("u", 1), Receipt("u", 2, "Unknown", false)], context).Items.Single().State == "Unconfirmed",
            "Unknown effects remain distinct from failure and success.");
        Check(WorkflowWorkspace.ProjectActivityTimelineEvents([Call("u", 1), Receipt("u", 2, "Succeeded", false)], context).Items.Single().State == "Outcome unknown",
            "Conflicting typed success fields cannot establish completion.");
        Check(WorkflowWorkspace.ProjectActivityTimelineEvents([events[0] with { Actor = new(EventActorKind.User, "other") }, good], context).Items.Count == 2,
            "A user-authored call envelope cannot be silently promoted to a tool execution pair.");
        const string secret = "fixture-secret-value";
        var secretCall = Call("secret", 1, new { path, apiKey = secret, content = "PRIVATE SOURCE BODY" });
        var secretReceipt = Receipt("secret", 2) with { Content = [new StructuredContentPart("workflow.command.result", Json(new
            { state = "Succeeded", data = new { stdout = "Echo " + secret, receiptId = "safe-receipt" } }))] };
        var safe = WorkflowWorkspace.ProjectActivityTimelineEvents([secretCall, secretReceipt], context).Items.Single();
        Check(!safe.RequestedAction.Contains(secret, StringComparison.Ordinal) && !safe.RequestedAction.Contains("PRIVATE SOURCE BODY", StringComparison.Ordinal) &&
            !safe.Outcome.Contains(secret, StringComparison.Ordinal) && safe.Outcome.Contains("safe-receipt", StringComparison.Ordinal),
            "Shared inspection reuses redaction and body omission while retaining safe receipt evidence.");
        const string returnedSecret = "synthetic-returned-credential-901";
        const string nestedSecret = "synthetic-nested-credential-902";
        var returnedPayload = new
        {
            stdout = "Echo " + returnedSecret + " and " + nestedSecret,
            token = returnedSecret,
            content = Json(new { stdout = "Nested echo " + nestedSecret, apiKey = nestedSecret }).GetRawText(),
            receiptId = "safe-returned-receipt"
        };
        var returnedReceipt = Receipt("returned", 2) with { Content = [new StructuredContentPart("workflow.command.result",
            Json(new { state = "Succeeded", data = returnedPayload }))] };
        var returned = WorkflowWorkspace.ProjectActivityTimelineEvents([Call("returned", 1), returnedReceipt], context).Items.Single();
        Check(!returned.Outcome.Contains(returnedSecret, StringComparison.Ordinal) && !returned.Outcome.Contains(nestedSecret, StringComparison.Ordinal) &&
            returned.Outcome.Contains("safe-returned-receipt", StringComparison.Ordinal),
            "Returned structured credentials and decoded content credentials redact echoes regardless of field order.");
        Check(!WorkflowWorkspace.ActivityResultText(returnedReceipt).Contains(returnedSecret, StringComparison.Ordinal),
            "An unpaired receipt redacts its own returned credential without request arguments.");

        var legacy = new ActivityEntry { Id = "legacy", SessionId = "chat-1", ActorSessionId = "actor-chat", JobId = "job", RootJobId = "root-a",
            ProfileId = "worker", ToolName = "file.read", Arguments = Json(new { path }).GetRawText(), State = "completed" };
        var old = WorkflowWorkspace.ProjectRecordedActivity(legacy, context);
        legacy.RootJobId = "root-b";
        var foreignRoot = WorkflowWorkspace.ProjectRecordedActivity(legacy, context);
        Check(old.Key != foreignRoot.Key && old.Correlation.StartsWith("Legacy", StringComparison.Ordinal) && old.State.Contains("legacy", StringComparison.Ordinal),
            "Legacy root-job correlation remains explicit and never becomes canonical request/receipt proof.");
        legacy.Result = Json(returnedPayload).GetRawText();
        var legacyReturned = WorkflowWorkspace.ProjectRecordedActivity(legacy, context);
        Check(!legacyReturned.Outcome.Contains(returnedSecret, StringComparison.Ordinal) && !legacyReturned.Outcome.Contains(nestedSecret, StringComparison.Ordinal) &&
            legacyReturned.Outcome.Contains("safe-returned-receipt", StringComparison.Ordinal),
            "Legacy typed results apply returned-credential redaction before displaying safe receipt/output fields.");
        legacy.Result = "Malformed synthetic result " + returnedSecret + " {";
        var malformedLegacy = WorkflowWorkspace.ProjectRecordedActivity(legacy, context);
        Check(!malformedLegacy.Outcome.Contains(returnedSecret, StringComparison.Ordinal) &&
            malformedLegacy.Outcome.Contains("Unstructured legacy result is not displayed", StringComparison.Ordinal),
            "Malformed legacy output cannot disclose an unclassified returned credential through a raw-text fallback.");

        var many = Enumerable.Range(1, 205).Select(index => Call("page-" + index, index)).ToArray();
        var snapshot = WorkflowWorkspace.ProjectActivityTimelineEvents(many, context, hasEarlier: true) with { TotalCount = 350, PrunedCount = 4 };
        var view = new ActivityTimelineView();
        view.LoadEarlierRequested += (_, _) => { };
        view.Update(snapshot, "chat-1", "task-1");
        view.Measure(new Size(800, 250)); view.Arrange(new Rect(0, 0, 800, 250)); view.UpdateLayout();
        T Find<T>(string id) where T : DependencyObject => Descendants(view).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
        var list = Find<ListView>("ActivityTimelineRows");
        var pager = Find<Button>("ActivityNextPage");
        Check(view.RowDefinitions[0].Height.IsAuto && view.RowDefinitions[1].Height.IsStar && view.RowDefinitions[2].Height.IsAuto &&
            double.IsFinite(list.ActualHeight) && list.ActualHeight is > 40 and < 250 && pager.ActualHeight >= 20 &&
            pager.TranslatePoint(new Point(0, 0), view).Y + pager.ActualHeight <= 251,
            $"A constrained Activity dock gives the table a finite scrolling viewport and keeps paging usable. " +
            $"Rows={view.RowDefinitions[0].Height}/{view.RowDefinitions[1].Height}/{view.RowDefinitions[2].Height}; " +
            $"list={list.ActualWidth}x{list.ActualHeight}; pager={pager.ActualWidth}x{pager.ActualHeight}; " +
            $"pagerBottom={pager.TranslatePoint(new Point(0, 0), view).Y + pager.ActualHeight}; " +
            $"items={list.Items.Count}; view={view.ActualWidth}x{view.ActualHeight}");
        var columns = ((GridView)list.View).Columns;
        Check(columns[^2].Header.ToString() == "File name" && columns[^1].Header.ToString() == "File path" &&
            columns.Any(column => column.Header.ToString() == "Operation type") && !((GridView)list.View).AllowsColumnReorder,
            "The real table has Operation type and mandatory File name then File path as the final columns.");
        Check(list.Items.Count == ActivityTimelineView.PageSize && VirtualizingPanel.GetIsVirtualizing(list),
            "The loaded window is paged and uses virtualized row presentation.");
        Find<Button>("ActivityNextPage").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(list.Items.Count == 200, "More supplied rows remain reviewable instead of silently dropped.");
        var windowsBeforeSelection = Application.Current.Windows.Count;
        list.SelectedIndex = 0;
        var selected = view.SelectedItem!;
        Check(Application.Current.Windows.Count == windowsBeforeSelection && Descendants(view).OfType<TextBox>().All(box =>
            AutomationProperties.GetAutomationId(box) == "ActivitySearch"),
            "Single click selects only; the bottom Activity table contains no embedded inspector or automatic window.");
        var detail = view.CreateDetailForInspection(selected);
        var detailPanel = (StackPanel)((ScrollViewer)detail.Content).Content;
        var requested = detailPanel.Children.OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "ActivityRequested");
        requested.Select(2, 4);
        var capturedOutcome = detailPanel.Children.OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "ActivityOutcome");
        var oldOutcome = capturedOutcome.Text;
        Check(!detail.IsVisible && detail.Tag is ActivityTimelineItem captured && captured.Key == selected.Key &&
            captured.OwnerIdentity == context.OwnerIdentity && requested.IsReadOnly && capturedOutcome.IsReadOnly,
            "An unshown ordinary detail window binds an immutable exact owner/operation snapshot with read-only fields.");
        var row = list.SelectedItem;
        view.Update(snapshot with { Items = snapshot.Items.Select(item => item.Key == selected.Key ? item with { Outcome = "Late safe receipt" } : item).ToArray() }, "chat-1", "task-1");
        Check(ReferenceEquals(row, list.SelectedItem) && requested.SelectionStart == 2 && requested.SelectionLength == 4,
            "A late receipt keeps stable row selection and does not retarget or alter selected detail text.");
        Check(capturedOutcome.Text == oldOutcome && detailPanel.Children.OfType<TextBlock>().Any(block => block.Text.Contains("newer evidence", StringComparison.Ordinal)),
            "A detail snapshot stays immutable and explicitly labels newer available evidence.");
        Check(view.SelectedItem!.RowText.EndsWith("AgentRouting.cs\t" + path, StringComparison.Ordinal),
            "Keyboard row copying exposes the full safe penultimate filename and final path.");
        var bad = false;
        try { view.Update(new([selected, selected], false), "chat-1", "task-1"); } catch (ArgumentException) { bad = true; }
        Check(bad && list.Items.Count == 200, "Duplicate identities fail before mutating visible evidence.");
        Find<ComboBox>("ActivityScope").SelectedItem = "All workers";
        Check(list.Items.Count == 100, "All workers is a role filter over the supplied window, not a new data read.");
        view.Update(snapshot with { Items = snapshot.Items.Where(item => item.Key != selected.Key).ToArray() }, "chat-1", "task-1");
        Check(detail.Tag is ActivityTimelineItem retained && retained.Key == selected.Key &&
            detailPanel.Children.OfType<TextBlock>().Any(block => block.Text.Contains("outside the currently loaded", StringComparison.Ordinal)),
            "Paging/filtering never retargets a detail; evidence leaving the loaded window is labelled explicitly.");
        view.Update(new([], false), "chat-1", "task-1");
        Check(detail.Tag is ActivityTimelineItem emptyWindowRetained && emptyWindowRetained.Key == selected.Key &&
            detailPanel.Children.OfType<TextBlock>().Any(block => block.Text.Contains("outside the currently loaded", StringComparison.Ordinal)),
            "An empty loaded window retains its labelled detail snapshot until explicit owner invalidation.");
        view.Update(snapshot, "chat-1", "task-1");
        var agentFilter = Find<ComboBox>("ActivityAgentFilter");
        var toolFilter = Find<ComboBox>("ActivityToolFilter");
        var stateFilter = Find<ComboBox>("ActivityStateFilter");
        agentFilter.SelectedValue = first.MemberCoordinate; var chosenAgent = agentFilter.SelectedItem;
        toolFilter.SelectedItem = first.Tool; stateFilter.SelectedItem = first.State;
        view.Update(snapshot, "chat-1", "task-1");
        Check(Equals(agentFilter.SelectedItem, chosenAgent) && Equals(agentFilter.SelectedValue, first.MemberCoordinate) &&
            Equals(toolFilter.SelectedItem, first.Tool) && Equals(stateFilter.SelectedItem, first.State),
            "Same-owner receipt refreshes preserve deliberate agent, tool and state filters.");
        var closed = false; detail.Closed += (_, _) => closed = true;
        view.InvalidateOwner();
        Check(closed && list.Items.Count == 0 && view.SelectedItem is null,
            "Explicit connection invalidation closes old-owner details and clears the Activity scope.");
        var newOwner = first with { Key = "new-owner-row", OwnerIdentity = "new-owner", Agent = "New agent", Tool = "New tool", State = "New state" };
        view.Update(new([newOwner], false), "chat-1", "task-1");
        Check(list.Items.Count == 1 && agentFilter.SelectedIndex == 0 &&
            new[] { toolFilter, stateFilter }.All(filter => Equals(filter.SelectedItem, "All")) &&
            !agentFilter.Items.Contains(chosenAgent) && !toolFilter.Items.Contains(first.Tool) && !stateFilter.Items.Contains(first.State),
            "A new owner receives fresh filters without old-owner choices hiding its supplied evidence.");
        var main = first with { Key = "mixed-main", OwnerIdentity = "mixed-owner", ConversationId = "main-chat", TaskId = null, TaskTitle = null, Role = ActivityTimelineRole.Main,
            OwningMainConversationId = "main-chat", MemberCoordinate = "member-main" };
        var coordinator = first with { Key = "mixed-coordinator", OwnerIdentity = "mixed-owner", ConversationId = "coordinator-chat", Role = ActivityTimelineRole.Coordinator,
            OwningMainConversationId = "main-chat", MemberCoordinate = "member-coordinator" };
        var worker = first with { Key = "mixed-worker", OwnerIdentity = "mixed-owner", ConversationId = "worker-chat",
            OwningMainConversationId = "main-chat", MemberCoordinate = "member-worker" };
        view.Update(new([main, coordinator, worker], true, CoverageNote: "One team chat unopened; total unknown."), "main-chat", "task-1");
        var scopeFilter = Find<ComboBox>("ActivityScope");
        Check(Equals(scopeFilter.SelectedItem, "All linked work") && list.Items.Count == 3 && main.TaskId is null,
            "The default shared scope includes supplied Main, Coordinator and Worker rows without inventing a Main task ID.");
        scopeFilter.SelectedItem = "Current task";
        Check(list.Items.Count == 2, "Current task filters exact recorded task IDs and does not relabel Main activity.");
        scopeFilter.SelectedItem = "All workers";
        Check(list.Items.Count == 1, "All workers filters only the supplied verified Worker role.");
        scopeFilter.SelectedItem = "All linked work"; agentFilter.SelectedValue = "member-main";
        list.SelectedIndex = 0;
        Check(list.Items.Count == 1 && view.SelectedItem?.ConversationId == "main-chat",
            "The stable member filter narrows the owning Main scope without changing a chat recipient.");
        view.InvalidateOwner();
        var capped = WorkflowWorkspace.ProjectActivityTimelineEvents(Enumerable.Range(1, 1001).Select(index => Call("cap-" + index, index)).ToArray(), context);
        Check(capped.Items.Count == 1000 && capped.OmittedCount == 1, "The view bound reports omissions and does not purge the source journal.");
        var focused = new ActivityTimelineView();
        var focusedList = Descendants(focused).OfType<ListView>().Single();
        var neighbour = first with { Key = "neighbour-turn", RunId = "other-run" };
        focused.Update(new([first, neighbour], true, CoverageNote: "Only loaded evidence."), "chat-1", "task-1");
        focused.FocusTurn(new(first.OwnerIdentity, first.ConversationId, first.RunId));
        Check(focusedList.Items.Count == 1 && focused.FocusedTurn?.RunId == first.RunId,
            "Summary navigation selects only the exact owner/chat/journal-turn interval, excluding its neighbour.");
        var late = first with { Key = "late-exact-turn", Sequence = first.Sequence + 1000 };
        focused.Update(new([first, neighbour, late], true), "chat-1", "task-1");
        Check(focusedList.Items.Count == 2 && focused.FocusedTurn?.RunId == first.RunId,
            "Polling retains the explicit turn filter and includes late evidence from that same turn only.");
        focused.Update(new([], true, CoverageNote: "The requested turn is outside the loaded window."), "chat-1", "task-1");
        Check(focusedList.Items.Count == 0 && focused.FocusedTurn is not null &&
            Descendants(focused).OfType<TextBlock>().Any(block => block.Text.Contains("outside the loaded window", StringComparison.Ordinal)),
            "An unloaded historical turn stays honestly empty and does not substitute another turn's evidence.");
        focused.Update(new([neighbour with { Key = "foreign-owner", OwnerIdentity = "foreign-owner" }], false), "chat-1", "task-1");
        Check(focused.FocusedTurn is null && focused.SelectedItem is null,
            "Owner replacement invalidates the old exact turn focus instead of inheriting its selection.");
        focused.Update(new([first, first with { Key = "same-name-other-member", MemberCoordinate = "another-member" }], false), "chat-1", "task-1");
        var focusedAgent = Descendants(focused).OfType<ComboBox>().Single(box => AutomationProperties.GetAutomationId(box) == "ActivityAgentFilter");
        focusedAgent.SelectedValue = first.MemberCoordinate;
        Check(focusedList.Items.Count == 1, "Identical readable names remain distinct stable member choices.");
        focused.FocusTurn(new(first.OwnerIdentity, first.ConversationId, first.RunId)
            { FirstSequence = first.FirstSequence, LastSequence = first.FirstSequence });
        var lateReceipt = projected.Items.Single(item => item.EventIds.Contains("a"));
        focused.Update(new([lateReceipt, projected.Items.Single(item => item.EventIds.Contains("b"))], false), "chat-1", "task-1");
        Check(focusedList.Items.Count == 1 && lateReceipt.Sequence > first.FirstSequence,
            "A late receipt stays with the summary's original call interval while a neighbouring call is excluded.");
        focused.Update(new([first], false), "empty-new-main", null);
        Check(focusedList.Items.Count == 0 && focused.FocusedTurn is null && focusedAgent.Items.Count == 1 &&
            !ActivityTimelineView.BelongsToMain(first, "empty-new-main"),
            "Switching to an empty root clears rows, focus and agent choices before any new response.");
        Check(columns.Any(column => column.Header.ToString() == "Agent") && columns.All(column => column.Header.ToString() != "Chat"),
            "Chat scope is implicit and the table keeps the actual Agent column.");
        Check(ActivityTimelineView.WithinInterval(DateTimeOffset.UnixEpoch.AddMinutes(50), "Last 15 minutes", DateTimeOffset.UnixEpoch.AddHours(1)) &&
            !ActivityTimelineView.WithinInterval(DateTimeOffset.UnixEpoch, "Last 15 minutes", DateTimeOffset.UnixEpoch.AddHours(1)),
            "The time filter intersects the loaded exact-root evidence.");
        return count;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, index))) yield return child;
    }
}
