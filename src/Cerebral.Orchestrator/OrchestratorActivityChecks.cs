using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Deterministic renderer acceptance; no connection, model, provider, clipboard or desktop launch.
    public int CheckActivityUi(string? previewPath = null, bool summaryOnly = false)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        ActivityEntry Copy(ActivityEntry entry) => CerebralJson.Read<ActivityEntry>(CerebralJson.Element(entry));
        HwndSource? activitySource = null;
        object? detachedContent = null;
        Action? restoreActivityOwner = null;
        IDisposable? ownedActivityClient = null; IDisposable? ownedActivityHandler = null;
        var ownerDispatches = 0;
        var rejectedOperations = new List<string>(8); var rejectedOperationsGate = new object();
        string[] RejectedOperationNames() { lock (rejectedOperationsGate) return rejectedOperations.ToArray(); }
        try
        {
            if (summaryOnly)
            {
                var owner = SharedActivityOwner;
                var item = new ActivityTimelineItem("summary-target", owner, "summary-chat", "summary-run", null,
                    ActivityTimelineRole.Main, "Cerebral", "Read", "file.read", "Complete", "Recorded read", "Recorded receipt",
                    "Fixture evidence", ["summary-event"], 2, DateTimeOffset.UnixEpoch)
                    { OwningMainConversationId = "summary-chat", MemberCoordinate = "summary-chat" };
                var other = item with { Key = "other-turn", RunId = "other-run" };
                var supplied = new ActivityTimelineSnapshot([item, other], true, CoverageNote: "Only loaded evidence.");
                sharedActivity!.Update(supplied, item.ConversationId, null);
                var retained = prompt.Text; var selectedChat = CurrentConversationId;
                ShowFocusedActivityPage(new(owner, item.ConversationId, item.RunId));
                Check(ReferenceEquals(Tabs.SelectedItem, pages["chat"]) && sharedActivityPanel?.IsExpanded == true,
                    "Explicit activation opens Activity inside the existing Chat surface.");
                Check(sharedActivity.FocusedTurn == new ActivityTurnFocus(owner, item.ConversationId, item.RunId) &&
                    sharedActivity.Children.OfType<ListView>().Single().Items.Count == 1,
                    "The Activity page is focused to the exact owner/chat/accepted turn and excludes a neighbouring turn.");
                Check(prompt.Text == retained && CurrentConversationId == selectedChat && mainWorkPanel?.IsExpanded == false,
                    "Activity navigation preserves draft and chat identity while selecting its embedded docking bar.");
                SelectPage("chat"); var focusedElement = System.Windows.Input.Keyboard.FocusedElement;
                sharedActivity.Update(supplied, item.ConversationId, null);
                Check(ReferenceEquals(Tabs.SelectedItem, pages["chat"]) && prompt.Text == retained &&
                    ReferenceEquals(focusedElement, System.Windows.Input.Keyboard.FocusedElement),
                    "A same-owner timeline refresh never navigates or takes focus.");
                OpenTurnActivityPage(nativeChat!, new("foreign-chat", "foreign-run"));
                Check(ReferenceEquals(Tabs.SelectedItem, pages["chat"]), "A disconnected or mismatched presenter cannot navigate.");
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, activitySummaryChecks = checks,
                    serviceCommandsExecuted = false, modelInvoked = false, windowShown = false }));
                return 0;
            }
            var now = DateTimeOffset.UtcNow;
            profiles = [new() { Id = "lead", Name = "Cerebral", Role = "lead" }, new() { Id = "helper", Name = "Project review", Role = "helper" }];
            leadProfile = profiles[0];
            session = new TaskSession
            {
                Id = "activity-render-fixture", ProfileId = "lead", Title = "Project activity",
                Messages =
                [
                    new() { Sequence = 1, At = now.AddMinutes(-3), Content = "Read the project guidance and ask a helper to review the application." },
                    new() { Sequence = 2, At = now.AddMinutes(-2), Role = "assistant", Content = "{\"action\":\"tool\",\"name\":\"file.read\",\"arguments\":{\"path\":\"sample.cs\"}}" },
                    new() { Sequence = 3, At = now.AddMinutes(-2), Role = "user", Origin = "tool", ToolName = "file.read", Content = "Tool file.read; success=True; truncated=False\nSaved file result." },
                    new() { Sequence = 4, At = now.AddMinutes(-1), Content = "Now prepare the project locations and check the build." }
                ]
            };
            var rootJob = new AgentJob { Id = "lead-job", SessionId = session.Id, ProfileId = "lead", Profile = profiles[0], Status = "running", StartSequence = 1, CreatedAt = now.AddMinutes(-3) };
            var helperJob = new AgentJob { Id = "helper-job", ParentJobId = rootJob.Id, SessionId = "helper-session", ProfileId = "helper", Profile = profiles[1], RootTurnSequence = 1, Status = "completed", Result = "Reviewed the project layout. Two locations need checking.", Prompt = "Review the existing project layout.", CreatedAt = now.AddMinutes(-2), FinishedAt = now.AddSeconds(-30) };
            rootJob.ConsumedHelperJobIds.Add(helperJob.Id);
            activeJobs = [rootJob, helperJob];
            RenderSession();
            var first = TurnView(session.Id, 1); var current = TurnView(session.Id, 4);
            Check(messages.Children.Contains(first.SummaryButton) && messages.Children.Contains(current.SummaryButton) &&
                !messages.Children.Contains(first.Expander) && !messages.Children.Contains(current.Expander),
                "Each legacy chat turn has only a summary action; expandable details are reserved for the Activity page.");
            var firstMessage = messages.Children.OfType<Border>().Single(x => AutomationProperties.GetAutomationId(x) == "Message-1");
            var firstTimestamp = AutomationDescendants(firstMessage).OfType<TextBlock>().Single(x => AutomationProperties.GetAutomationId(x) == "MessageTime-1");
            Check(firstTimestamp.Text == MessageTimeFormatPolicy.Format(session.Messages[0].At, MessageTimeFormatPolicy.TwelveHour) &&
                  (firstTimestamp.Text.EndsWith("AM", StringComparison.Ordinal) || firstTimestamp.Text.EndsWith("PM", StringComparison.Ordinal)),
                "Message footers must show local time in the default 12-hour AM/PM format.");
            Check(first.SummaryButton.BorderThickness == new Thickness(0) &&
                   !AutomationDescendants(first.SummaryButton).OfType<Expander>().Any() &&
                   !AutomationDescendants(first.SummaryButton).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("MessageTime-", StringComparison.Ordinal)),
                "The legacy summary has no expandable box, chevron or message timestamp.");
            Check(ReferenceEquals(first.Expander.Content, first.ActivityBox) && ReferenceEquals(first.ActivityBox.Child, first.ActivityScroller) &&
                  first.ActivityScroller.MaxHeight is >= 180 and <= 240 &&
                  first.ActivityScroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                  first.ActivityScroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
                  first.ActivityBox.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "Expanded activity must use one bounded, shadowed viewport with both navigation directions available.");
            messages.Children.Clear(); DateOnly? renderedDay = null;
            AppendTranscriptDay(now.AddDays(-1), ref renderedDay); AppendTranscriptDay(now.AddDays(-1).AddHours(1), ref renderedDay); AppendTranscriptDay(now, ref renderedDay);
            Check(messages.Children.OfType<Grid>().Count(x => AutomationProperties.GetAutomationId(x).StartsWith("TranscriptDay-", StringComparison.Ordinal)) == 2,
                "Transcript date separators must appear once per local calendar day.");
            RenderSession(); first = TurnView(session.Id, 1); current = TurnView(session.Id, 4);
            Check(JobsForTurn(current).Contains(rootJob) && !JobsForTurn(current).Contains(helperJob),
                "Steering must retain the active lead while keeping the existing helper under its original turn.");

            var read = new ActivityEntry { Id = "read", Sequence = 1, SessionId = session.Id, ActorSessionId = session.Id, JobId = rootJob.Id,
                RootJobId = rootJob.Id, TurnSequence = 1, ProfileId = "lead", ToolName = "mcp.cortex.cortex_read", Title = "Cortex read - project.fixture",
                Arguments = "{\"namespace\":\"project.fixture\",\"recordType\":\"knowledge\",\"recordId\":\"guidance\"}", StartedAt = now.AddMinutes(-2), UpdatedAt = now.AddMinutes(-2) };
            var summary = Copy(read); summary.Arguments = ""; summary.Result = "";
            MergeTurnSummary(first, summary); first.Count = 1;
            Check(first.Entries.Count == 0 && first.Latest?.Title == read.Title, "Summary metadata must not hydrate or replace command details.");
            MergeTurnEntry(first, read);
            var uncertain = Copy(read); uncertain.State = "uncertain"; uncertain.Result = "Completion was not recorded.";
            MergeTurnEntry(first, uncertain); MergeTurnEntry(first, read);
            Check(first.Entries["read"].State == "uncertain", "Equal-time terminal projection must settle a running row without regressing.");
            var stale = Copy(read); stale.UpdatedAt = read.UpdatedAt.AddSeconds(-1); stale.State = "failed";
            MergeTurnEntry(first, stale);
            Check(first.Entries["read"].State == "uncertain", "Older events must not replace the current operation state.");
            var confirmed = Copy(uncertain); confirmed.State = "completed";
            MergeTurnEntry(first, confirmed);
            Check(first.Entries["read"].State == "completed", "An authoritative completion at the same timestamp must supersede a projected uncertain state.");
            var completed = Copy(read); completed.State = "completed"; completed.UpdatedAt = now.AddSeconds(-40); completed.Result = "Read the exact guidance record at revision 3.";
            MergeTurnEntry(first, completed);
            var metadata = Copy(completed); metadata.Arguments = ""; metadata.Result = "";
            MergeTurnSummary(first, metadata);
            Check(first.Entries["read"].Arguments == read.Arguments && first.Entries["read"].Result == completed.Result,
                "A metadata refresh must preserve already loaded arguments and result.");

            var helperCall = new ActivityEntry { Id = "helper-read", Sequence = 2, SessionId = session.Id, ActorSessionId = helperJob.SessionId, JobId = helperJob.Id,
                RootJobId = rootJob.Id, TurnSequence = 1, ProfileId = "helper", ToolName = "file.read", Title = "file.read - Application.cs", State = "completed",
                StartedAt = now.AddMinutes(-2), UpdatedAt = now.AddSeconds(-30), Arguments = "{\"path\":\"Application.cs\"}", Result = "Recorded source excerpt." };
            MergeTurnEntry(first, helperCall); first.Count = 2; first.Loaded = true; first.Loading = true;
            first.Expander.IsExpanded = true; UpdateTurnActivity(first);
            Check(!transcriptFollowEnd, "Expanding activity must hold the clicked position instead of jumping to the end.");
            var helperControl = first.HelperViews[helperJob.Id]; helperControl.Expander.IsExpanded = true;
            Check(helperControl.Details.Text.Contains(helperJob.Prompt) && helperControl.Details.Text.Contains("supplied to the lead") && helperControl.Rows.Children.Contains(first.EntryViews[helperCall.Id].Expander),
                "The helper expander must contain its actual assignment, integration receipt and recorded commands.");
            Check(helperControl.Title.Text == "Project review · Read · 1 call",
                "A delegated iteration must use the configured agent name and a compact call summary.");
            var readControl = first.EntryViews["read"]; readControl.Expander.IsExpanded = true;
            Check(readControl.Text.IsReadOnly && readControl.Text.Text.Contains("project.fixture") && readControl.Text.Text.Contains(completed.Result) &&
                  !readControl.Text.Text.Contains("Started:") && !readControl.Text.Text.Contains("Updated:") && !readControl.Text.Text.Contains("Agent:") && !readControl.Text.Text.Contains("State:"),
                "Call details must retain selectable arguments and results without repeating stored diagnostic metadata.");
            var encodedToolEnvelope = JsonSerializer.Serialize(JsonSerializer.Serialize(new
            {
                action = "tool", name = "file.write", arguments = new
                {
                    path = @"C:\CerebralProjects\Plugins\NewChatIdeas\Desktop\GeneratedDesktopPlugin.cs",
                    content = new string('x', 900)
                }
            }, CerebralJson.Options), CerebralJson.Options);
            var readableEnvelope = ReadableActivityJson(encodedToolEnvelope);
            Check(readableEnvelope.Contains("Tool: file.write", StringComparison.Ordinal) &&
                  readableEnvelope.Contains(@"path: C:\CerebralProjects\Plugins\NewChatIdeas\Desktop\GeneratedDesktopPlugin.cs", StringComparison.Ordinal) &&
                  readableEnvelope.Contains("900 characters omitted", StringComparison.Ordinal) &&
                  !readableEnvelope.Contains("\\u0022", StringComparison.Ordinal) &&
                  !readableEnvelope.Contains(@"C:\\CerebralProjects", StringComparison.Ordinal),
                "String-encoded tool envelopes must render as a normal tool and Windows path without escaped transport JSON or a source-file dump.");
            readControl.Text.Select(0, 5); UpdateTurnActivity(first);
            Check(readControl.Text.SelectionLength == 5, "Unchanged activity refresh must preserve selected detail text.");
            RenderSession();
            Check(ReferenceEquals(TurnView(session.Id, 1).Expander, first.Expander) && first.Expander.IsExpanded && readControl.Expander.IsExpanded,
                "Transcript reconstruction must retain existing expanded controls.");

            Check(readControl.Text.SelectionLength == 5, "Transcript reconstruction must preserve selected activity text.");
            ActivityEntry Operation(string id, long sequence, string name) => new()
            {
                Id = id, Sequence = sequence, SessionId = session.Id, ActorSessionId = session.Id, JobId = rootJob.Id, RootJobId = rootJob.Id,
                TurnSequence = 4, ProfileId = "lead", ToolName = name, Title = name == "process.run" ? "Run dotnet build" : "Prepare project locations",
                State = "completed", StartedAt = now.AddSeconds(-15 + sequence), UpdatedAt = now.AddSeconds(-10 + sequence), Arguments = "{}",
                Result = name == "process.run" ? "Build completed with 0 errors (fixture result)." : "Recorded operation result."
            };
            var location = Operation("locations", 3, "file.write");
            var build = Operation("build", 4, "process.run"); build.State = "running"; build.Result = "";
            ApplyTurnPage(current, new() { Entries = [location], NextAfterSequence = 3, TotalCount = 2 });
            Check(current.Next == 3 && current.Entries.Count == 1, "A partial page must expose its continuation cursor.");
            ApplyTurnPage(current, new() { Entries = [build], NextAfterSequence = null, TotalCount = 2 });
            Check(current.Next is null && current.Entries.Count == 2 && current.LoadedThrough == 4, "Loading the next page must retain all prior entries and end paging.");
            current.Loading = true; current.Expander.IsExpanded = true; UpdateTurnActivity(current);
            Check(current.Headline.Text == "Working · Write, run · 2 calls",
                "The live headline must summarize the current iteration, task state and call count on one line.");
            TurnActivityEventAsync(new(1, "token", rootJob.Id, CerebralJson.Element(new { text = "PRIVATE_RAW_ACTION" }), now)).GetAwaiter().GetResult();
            Check(current.Headline.Text.Contains("Generating a response") && !current.Headline.Text.Contains("PRIVATE_RAW_ACTION"),
                "Generation progress must not display raw action JSON or private model text.");
            ActivityEventAsync(new(2, "context.budget", rootJob.Id, CerebralJson.Element(new
            {
                sessionId = session.Id, promptTokens = 3_300, requestedResponseTokens = 1_024,
                availableResponseTokens = 668, modelMemoryChanged = false
            }), now)).GetAwaiter().GetResult();
            Check(activityRows.TryGetValue("context-budget:" + rootJob.Id, out var budget) && budget.Body.Contains("3,300 tokens") &&
                  budget.Body.Contains("668 tokens") && budget.Body.Contains("Model memory", StringComparison.Ordinal),
                "A runtime context clamp must appear as compact activity with the actual prompt and response allowances.");
            var longFormSnapshot = CerebralJson.Element(new
            {
                tracked = true, sourceJobId = rootJob.Id, revision = 5, path = @"C:\Projects\Novel\draft.txt",
                targetWords = 1_000, verifiedWords = 640, currentSectionIndex = 2, sectionCount = 4, complete = false,
                pendingAppend = new { step = 7, contentCharacters = 2_400 }, updatedAt = now
            });
            RecordLongFormActivity(session.Id, longFormSnapshot, now, rootJob.Id, [rootJob, helperJob], false);
            var longFormKey = LongFormActivityKey(session.Id);
            Check(activityRows.ContainsKey(longFormKey), "A reconnect snapshot must create the long-form activity row.");
            var longForm = activityRows[longFormKey];
            Check(longForm.State == "Recovery pending" &&
                  longForm.Title.Contains("640 / 1,000 words") && longForm.Body.Contains(@"C:\Projects\Novel\draft.txt") &&
                  longForm.Body.Contains("2 / 4") && longForm.Body.Contains("2,400 characters") && !longForm.Body.Contains("content", StringComparison.OrdinalIgnoreCase),
                "A reconnect snapshot must restore one compact long-form row with measured progress, allocation thresholds and bounded recovery metadata.");
            var hydratedBody = longForm.Body;
            RecordLongFormActivity(session.Id, CerebralJson.Element(new
            {
                revision = 4, path = @"C:\Projects\Novel\stale.txt", targetWords = 1_000,
                verifiedWords = 200, reachedAllocations = 1, allocationCount = 4
            }), now.AddSeconds(-1), rootJob.Id, [rootJob, helperJob], false);
            Check(activityRows[longFormKey].Body == hydratedBody,
                "An older streamed progress event must not regress the hydrated service snapshot.");
            RecordLongFormActivity(session.Id, CerebralJson.Element(new
            {
                revision = 6, path = @"C:\Projects\Novel\draft.txt", targetWords = 1_000,
                verifiedWords = 1_000, reachedAllocations = 4, allocationCount = 4
            }), now.AddSeconds(1), rootJob.Id, [rootJob, helperJob], false);
            Check(activityRows[longFormKey].State == "Completed" && activityRows.Values.Count(x => x.Key == longFormKey) == 1 &&
                  activityRows[longFormKey].Body.Contains("1,000 / 1,000 words"),
                "A newer progress event must update the same conversation row without creating a duplicate.");
            RemoveUntrackedLongFormActivity(session.Id, now);
            Check(activityRows.ContainsKey(longFormKey), "An older untracked response must not erase newer streamed progress.");
            build = Copy(build); build.State = "failed"; build.Result = "Fixture build error: project file not found."; build.UpdatedAt = now.AddSeconds(1);
            TurnActivityEventAsync(new(2, "activity.updated", rootJob.Id, CerebralJson.Element(build), now.AddSeconds(1))).GetAwaiter().GetResult();
            Check(current.Headline.Text.StartsWith("Working · ", StringComparison.Ordinal) && current.Progress is null &&
                  current.EntryViews["build"].Title.Foreground == Ui.ErrorText &&
                  string.Equals(current.EntryViews["build"].Title.Text, "Failed · Agent not recorded · Run · [Process route not recorded] · Fixture build error: project file not found.", StringComparison.Ordinal),
                "A failed call remains red with its operation and actionable reason without poisoning the active task headline while Cerebral can repair it.");
            var taskFailure = new TaskFailure { Id = "fixture-failure", JobId = rootJob.Id, SessionId = session.Id,
                Source = "process.run", Message = "Project file not found. Select the correct project path before retrying.",
                CreatedAt = now.AddMilliseconds(1500) };
            RecordActivityFailure(taskFailure);
            var failureControl = current.FailureViews[taskFailure.Id];
            failureControl.Text.Select(0, 7);
            RecordActivityFailure(CerebralJson.Read<TaskFailure>(CerebralJson.Element(taskFailure)));
            Check(ReferenceEquals(failureControl, current.FailureViews[taskFailure.Id]) && failureControl.Text.SelectionLength == 7 &&
                  failureControl.Title.Text.StartsWith("Failed · process.run · Project file not found", StringComparison.Ordinal),
                "Failure refresh must retain the existing selectable row while keeping its source and actionable reason visible.");
            var retry = Operation("build-retry", 5, "process.run"); retry.State = "completed";
            retry.StartedAt = now.AddSeconds(2); retry.UpdatedAt = now.AddSeconds(2);
            MergeTurnEntry(current, retry); current.Count = 3;
            rootJob.Status = "completed"; rootJob.Outcome = "completed"; rootJob.FinishedAt = now.AddSeconds(3); UpdateTurnActivity(current);
            Check(current.Headline.Text == "Completed · Write, run · 3 calls" && current.Headline.Foreground == Brushes.SeaGreen &&
                  current.HelperViews[rootJob.Id].Title.Foreground == Brushes.SeaGreen && current.EntryViews["build"].Title.Foreground == Ui.ErrorText,
                "A successful repair makes the task and agent iteration green while preserving the failed call as red evidence.");
            var chronologicalRows = current.HelperViews[rootJob.Id].Rows.Children.Cast<UIElement>().ToList();
            Check(chronologicalRows.IndexOf(current.EntryViews["build"].Expander) < chronologicalRows.IndexOf(failureControl.Expander) &&
                  chronologicalRows.IndexOf(failureControl.Expander) < chronologicalRows.IndexOf(current.EntryViews["build-retry"].Expander),
                "A service failure must remain at its chronological point while repaired activity continues below it.");
            current.History.Add(new() { Sequence = 7, At = now.AddMilliseconds(-11_500), Role = "assistant", Origin = "service", Content = "Project Cortex retrieval loaded three exact records." });
            current.History.Add(new() { Sequence = 8, At = now.AddMilliseconds(-10_500), Role = "assistant", Origin = "service", Content = "Model context was adjusted to fit the current request." });
            UpdateTurnActivity(current);
            current.HelperViews[rootJob.Id].Expander.IsExpanded = true;
            var rootRows = current.HelperViews[rootJob.Id].Rows.Children.OfType<Expander>().ToList();
            Check(current.ServiceViews.Count == 2 && current.Rows.Children.OfType<Expander>().All(x => x.Header is not TextBlock header || !header.Text.StartsWith("Service update", StringComparison.Ordinal)) &&
                  rootRows.Count(x => x.Header is TextBlock header && header.Text.StartsWith("Service update · ", StringComparison.Ordinal)) == 2 &&
                   rootRows.Select(x => (x.Header as TextBlock)?.Text).SequenceEqual(new[]
                   {
                       "Agent not recorded · Write · [file.write]", "Service update · Project Cortex retrieval loaded three exact records.",
                       current.EntryViews["build"].Title.Text, "Service update · Model context was adjusted to fit the current request.",
                       failureControl.Title.Text, "Agent not recorded · Run · [Process route not recorded]"
                   }),
                "Service notes and failures render chronologically as ordinary line items inside the Cerebral iteration that produced them.");
            var wrong = Copy(build); wrong.SessionId = "another-conversation"; wrong.Result = "Wrong conversation result.";
            MergeTurnEntry(current, wrong);
            Check(current.Entries["build"].Result == build.Result, "Another conversation's event must not alter this turn.");
            TurnActivityEventAsync(new(3, "activity.error", rootJob.Id, CerebralJson.Element(new { sessionId = session.Id }), now)).GetAwaiter().GetResult();
            Check(current.Failures.ContainsKey("activity-recording:3") &&
                  current.FailureViews["activity-recording:3"].Title.Text.StartsWith("Failed · Activity recording", StringComparison.Ordinal),
                "Missing activity evidence must remain as a chronological failed row without changing the job outcome.");

            var savedJobs = activeJobs;
            var priorFailure = new AgentJob { Id = "prior-failure", SessionId = session.Id, ProfileId = "lead", Profile = profiles[0], Kind = "agent",
                Status = "failed", StartSequence = 4, CreatedAt = now.AddSeconds(-8), FinishedAt = now.AddSeconds(-6), Error = "Fixture assignment failure." };
            var manualCompaction = new AgentJob { Id = "manual-compaction", SessionId = session.Id, ProfileId = "lead", Profile = profiles[0], Kind = "compact",
                Status = "completed", Outcome = "completed", StartSequence = 4, CreatedAt = now.AddSeconds(-5), FinishedAt = now.AddSeconds(-2) };
            var compactEntry = new ActivityEntry { Id = "manual-compact-entry", Sequence = 5, SessionId = session.Id, ActorSessionId = session.Id,
                JobId = manualCompaction.Id, RootJobId = manualCompaction.Id, TurnSequence = 4, ProfileId = "lead", ToolName = "context.compact",
                Title = "Context compaction", State = "completed", StartedAt = now.AddSeconds(-4), UpdatedAt = now.AddSeconds(-2),
                Result = "A context checkpoint was adopted. Original saved messages remain available." };
            var standalone = new TurnActivityView(session.Id, 4) { Count = 1, Loaded = true };
            standalone.Expander.IsExpanded = true; activeJobs = [priorFailure, manualCompaction]; MergeTurnEntry(standalone, compactEntry); UpdateTurnActivity(standalone);
            Check(standalone.Headline.Text == "Completed · Compact context · 1 call" && standalone.Headline.Foreground == Brushes.SeaGreen &&
                  standalone.HelperViews[manualCompaction.Id].Title.Text == "Cerebral · Compact context · 1 call" &&
                  standalone.HelperViews[manualCompaction.Id].Title.Foreground == Brushes.SeaGreen,
                "A newer successful standalone compaction must supersede an older failed assignment and render as one compact agent iteration.");

            manualCompaction.Status = "failed"; manualCompaction.Outcome = "failed";
            var failedCompaction = Copy(compactEntry); failedCompaction.State = "failed"; failedCompaction.UpdatedAt = now.AddSeconds(-1); failedCompaction.Result = "Fixture compaction failure.";
            MergeTurnEntry(standalone, failedCompaction); UpdateTurnActivity(standalone);
            Check(standalone.Headline.Text == "Failed · Compact context · 1 call" &&
                  standalone.HelperViews[manualCompaction.Id].Title.Foreground == Ui.ErrorText,
                "A genuine standalone compaction failure must remain visibly failed in the turn and iteration rows.");

            var automatic = new TurnActivityView(session.Id, 4) { Count = 1, Loaded = true };
            var automaticEntry = Copy(compactEntry); automaticEntry.Id = "automatic-compact-entry"; automaticEntry.JobId = priorFailure.Id;
            automaticEntry.RootJobId = priorFailure.Id; automaticEntry.Sequence = 6; automaticEntry.UpdatedAt = now;
            automatic.Expander.IsExpanded = true; activeJobs = [priorFailure]; MergeTurnEntry(automatic, automaticEntry); UpdateTurnActivity(automatic);
            Check(automatic.Headline.Text == "Failed · Compact context · 1 call" &&
                  automatic.HelperViews[priorFailure.Id].Title.Text == "Cerebral · Compact context · 1 call",
                "A completed automatic compaction inside a failed assignment must keep the concise call trace while the turn remains failed.");
            Check(automatic.Rows.Children.OfType<TextBlock>().Any(x => x.Text == "Error · " + priorFailure.Error) &&
                  !automatic.Headline.Text.Contains(priorFailure.Error, StringComparison.Ordinal),
                "A failed Cerebral outcome must expose its bounded error below the footer without repeating it in the headline.");
            activeJobs = savedJobs;

            var legacy = new TurnActivityView(session.Id, 0) { Loaded = true, Loading = true,
                History = [new() { Sequence = 0, Origin = "tool", ToolName = "process.run", Content = "Older retained result." }] };
            legacy.Expander.IsExpanded = true; UpdateTurnActivity(legacy);
            Check(legacy.LegacyViews.Count == 1, "A confirmed empty journal must retain the older saved-message fallback.");
            first.Loading = false; current.Loading = false;
            readControl.Expander.IsExpanded = false; helperControl.Expander.IsExpanded = false;
            UpdateTurnActivity(first); UpdateTurnActivity(current);
            current.EntryViews["build"].Expander.IsExpanded = true;
            Check(!current.Loading && current.Error is null, "Expanding a nested command must not bubble into a new history request.");
            var container = (FrameworkElement)Content;
            // Scroll commands need a presentation source even though this fixture never shows
            // the application. Use the same offscreen HWND pattern as the Desktop checks.
            detachedContent = Content; Content = null;
            activitySource = new HwndSource(new HwndSourceParameters("Cerebral Activity-page fixture")
            { Width = 1280, Height = 960, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000) }) { RootVisual = container };
            container.Measure(new Size(1280, 960)); container.Arrange(new Rect(0, 0, 1280, 960)); container.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { })); container.UpdateLayout();
            Check(!IsConnected && ReferenceEquals(activitySource.RootVisual, container) && PresentationSource.FromVisual(container) is not null,
                "Startup Loaded/background work settles in the original disconnected offscreen host before the controlled Activity interaction.");
            var retainedDraft = prompt.Text;
            var retainedMainExpanded = mainWorkPanel!.IsExpanded; var retainedChatId = CurrentConversationId;
            var activitySession = session ?? throw new InvalidOperationException("Missing controlled Activity session.");
            var activityHost = sharedActivityPanel ?? throw new InvalidOperationException("Missing controlled Activity host.");
            var ownerFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var connectedField = typeof(ConnectedWindow).GetField("<IsConnected>k__BackingField", ownerFlags) ??
                throw new InvalidOperationException("Missing controlled connection field.");
            var clientField = typeof(ConnectedWindow).GetField("client", ownerFlags) ??
                throw new InvalidOperationException("Missing controlled client field.");
            var priorClient = clientField.GetValue(this); var priorConnected = IsConnected;
            var priorFocus = focusedLegacyActivity; var priorHostContent = activityHost.Content;
            var handler = new AgentSidebarMetricsHandler((request, token) =>
            {
                token.ThrowIfCancellationRequested(); Interlocked.Increment(ref ownerDispatches);
                lock (rejectedOperationsGate)
                    if (rejectedOperations.Count < 8)
                    {
                        var name = request.Operation;
                        rejectedOperations.Add(name[..Math.Min(name.Length, 80)]);
                    }
                throw new InvalidOperationException("Unexpected Activity fixture owner operation.");
            });
            ownedActivityHandler = handler;
            var fixtureClient = new Cerebral.Client.CerebralClient(); ownedActivityClient = fixtureClient;
            var httpField = typeof(Cerebral.Client.CerebralClient).GetField("http", ownerFlags) ??
                throw new InvalidOperationException("Missing controlled HTTP field.");
            ((System.Net.Http.HttpClient)httpField.GetValue(fixtureClient)!).Dispose();
            httpField.SetValue(fixtureClient, new System.Net.Http.HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("http://activity-fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan });
            restoreActivityOwner = () =>
            {
                clientField.SetValue(this, priorClient); connectedField.SetValue(this, priorConnected);
                focusedLegacyActivity = priorFocus; activityHost.Content = priorHostContent;
            };
            clientField.SetValue(this, fixtureClient); connectedField.SetValue(this, true);
            OpenLegacyTurnActivityPage(current);
            string ActivityViewportDiagnostic() =>
                JsonSerializer.Serialize(new
                {
                    follow = current.ActivityFollowEnd, expanded = current.Expander.IsExpanded,
                    sessionMatch = ReferenceEquals(session, activitySession) && activitySession.Id == current.SessionId, IsConnected,
                    focusedLegacyReference = ReferenceEquals(focusedLegacyActivity, current),
                    currentRegistered = ReferenceEquals(turnActivity.GetValueOrDefault(TurnKey(current.SessionId, current.TurnSequence)), current),
                    sameHost = ReferenceEquals(sharedActivityPanel, activityHost), ownerDispatches = Volatile.Read(ref ownerDispatches),
                    ownerOperationNames = RejectedOperationNames(),
                    pageSelected = ReferenceEquals(Tabs.SelectedItem, pages["activity"]),
                    fakeClientReferenceSame = ReferenceEquals(clientField.GetValue(this), fixtureClient),
                    panelContentCurrent = ReferenceEquals(activityHost.Content, current.Expander),
                    panelContentShared = ReferenceEquals(activityHost.Content, sharedActivity),
                    visualDescendant = current.ActivityScroller.IsDescendantOf(activityHost),
                    presentationSourcePresent = PresentationSource.FromVisual(current.ActivityScroller) is not null,
                    measureValid = current.ActivityScroller.IsMeasureValid, arrangeValid = current.ActivityScroller.IsArrangeValid,
                    hasSelectedActivityText = current.ActivityTexts.Any(text => text.SelectionLength > 0),
                    offset = double.IsFinite(current.ActivityScroller.VerticalOffset) ? current.ActivityScroller.VerticalOffset : (double?)null,
                    scrollable = double.IsFinite(current.ActivityScroller.ScrollableHeight) ? current.ActivityScroller.ScrollableHeight : (double?)null,
                    viewport = double.IsFinite(current.ActivityScroller.ViewportHeight) ? current.ActivityScroller.ViewportHeight : (double?)null,
                    extent = double.IsFinite(current.ActivityScroller.ExtentHeight) ? current.ActivityScroller.ExtentHeight : (double?)null,
                    allFinite = double.IsFinite(current.ActivityScroller.VerticalOffset) && double.IsFinite(current.ActivityScroller.ScrollableHeight) &&
                        double.IsFinite(current.ActivityScroller.ViewportHeight) && double.IsFinite(current.ActivityScroller.ExtentHeight)
                });

            Check(!messages.Children.Contains(current.Expander) && prompt.Text == retainedDraft &&
                !workPanelLayout!.Children.Contains(sharedActivityPanel) && ReferenceEquals(Tabs.SelectedItem, pages["activity"]) &&
                sharedActivityPanel.IsExpanded && mainWorkPanel!.IsExpanded == retainedMainExpanded && CurrentConversationId == retainedChatId,
                "Legacy details use the dedicated Activity page without changing Main, chat identity, draft or transcript controls.");

            container.Measure(new Size(1280, 960)); container.Arrange(new Rect(0, 0, 1280, 960)); container.UpdateLayout();
            Check(container.ActualWidth == 1280 && current.Expander.ActualWidth > 0, "The actual WPF Activity page and its selected turn must lay out.");
            Check(current.ActivityScroller.ViewportHeight <= 210 && current.ActivityScroller.ScrollableHeight > 0,
                $"Five or more activity lines must scroll inside the bounded activity box instead of growing the chat turn indefinitely (viewport {current.ActivityScroller.ViewportHeight}, scrollable {current.ActivityScroller.ScrollableHeight}).");
            ResumeActivityFollowAfterOutsideClick(prompt); container.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); container.UpdateLayout();
            Check(IsConnected && ReferenceEquals(session, activitySession) && current.SessionId == activitySession.Id &&
                ReferenceEquals(focusedLegacyActivity, current) &&
                ReferenceEquals(turnActivity.GetValueOrDefault(TurnKey(current.SessionId, current.TurnSequence)), current) &&
                ReferenceEquals(sharedActivityPanel, activityHost) && ReferenceEquals(activityHost.Content, current.Expander) &&
                current.ActivityScroller.IsDescendantOf(activityHost) && PresentationSource.FromVisual(current.ActivityScroller) is not null &&
                current.ActivityScroller.IsMeasureValid && current.ActivityScroller.IsArrangeValid && Volatile.Read(ref ownerDispatches) == 0,
                "The exact registered legacy view retains its controlled owner, focus, physical host and valid viewport through background refresh without dispatch. " +
                ActivityViewportDiagnostic());
            Check(ActivityAtEnd(current.ActivityScroller),
                "An activity viewport that follows live work must settle on the latest line. " + ActivityViewportDiagnostic());
            current.ActivityScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, 120)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            current.ActivityScroller.ScrollToTop(); container.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); container.UpdateLayout();
            Check(!current.ActivityFollowEnd, "Deliberately scrolling to older activity must pause automatic follow. " + ActivityViewportDiagnostic());
            var continued = Operation("continued-after-failure", 6, "file.write"); continued.Title = "Save repaired project";
            continued.StartedAt = now.AddSeconds(4); continued.UpdatedAt = now.AddSeconds(4);
            MergeTurnEntry(current, continued); current.Count = 4; UpdateTurnActivity(current); container.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); container.UpdateLayout();
            Check(current.ActivityScroller.VerticalOffset <= 1 && current.EntryViews.ContainsKey(continued.Id),
                "New activity must append without pulling a user away from deliberately selected older lines. " + ActivityViewportDiagnostic());
            current.EntryViews["build"].Text.Select(0, Math.Min(5, current.EntryViews["build"].Text.Text.Length));
            Check(!current.ActivityFollowEnd, "Selecting older activity text must pause automatic follow. " + ActivityViewportDiagnostic());
            var activityRun = new System.Windows.Documents.Run("Fixture document link");
            var activityLink = new System.Windows.Documents.Hyperlink(activityRun);
            var documentText = new RichTextBox
            {
                IsReadOnly = true,
                Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(activityLink))
            };
            current.Rows.Children.Add(documentText);
            ResumeActivityFollowAfterOutsideClick(activityRun);
            Check(!current.ActivityFollowEnd,
                "A routed Run inside an activity hyperlink must preserve review without calling visual-only ancestry APIs. " + ActivityViewportDiagnostic());
            current.Rows.Children.Remove(documentText);
            ResumeActivityFollowAfterOutsideClick(activityRun);
            Check(current.ActivityFollowEnd,
                "A document Run outside the activity viewport must safely resume following. " + ActivityViewportDiagnostic());
            var draftBeforeResume = prompt.Text;
            ResumeActivityFollowAfterOutsideClick(prompt); container.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); container.UpdateLayout();
            Check(current.ActivityFollowEnd && ActivityAtEnd(current.ActivityScroller) && prompt.Text == draftBeforeResume,
                "Clicking outside activity must resume the latest line without changing the composer used by dictation. " + ActivityViewportDiagnostic());
            current.ActivityScroller.ScrollToTop(); container.UpdateLayout();
            current.ActivityScroller.ScrollToEnd(); container.UpdateLayout();
            Check(current.ActivityFollowEnd, "Returning the activity scrollbar to the bottom must resume automatic follow. " + ActivityViewportDiagnostic());

            var preservedSession = session; var preservedContent = Content;
            var retainedPrompt = prompt.Text; prompt.Text = "Nonempty retained composer draft";
            var preservedPrompt = prompt.Text; var preservedFocus = System.Windows.Input.Keyboard.FocusedElement;
            var originalDiagnostic = Console.Error; using var interactionDiagnostic = new StringWriter();
            var deferredCalls = 0; var recoveredCallbacks = 0;
            try
            {
                Console.SetError(interactionDiagnostic);
                Check(!PassiveDesktopInteraction.TryRun("activity click",
                        () => VisualTreeHelper.GetParent(activityRun), message => SetStatus(message, true)) &&
                      Status.Text.Contains("Retry the click", StringComparison.Ordinal),
                    "The actual WPF content-versus-visual failure must leave an actionable error in the open desktop instead of closing it.");
                PassiveDesktopInteraction.Defer(Dispatcher, "activity position", () =>
                {
                    deferredCalls++;
                    VisualTreeHelper.GetParent(activityRun);
                }, message => SetStatus(message, true), () => recoveredCallbacks++);
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { })); container.UpdateLayout();
                Check(deferredCalls == 1 && recoveredCallbacks == 1 && Status.Text.Contains("Retry the click", StringComparison.Ordinal) &&
                      !Status.Text.Contains("stopped", StringComparison.OrdinalIgnoreCase) &&
                      !Status.Text.Contains("completed", StringComparison.OrdinalIgnoreCase),
                    "A known deferred view callback failure must report once without claiming that active work stopped or completed.");
                Check(ReferenceEquals(session, preservedSession) && ReferenceEquals(Content, preservedContent) &&
                      prompt.Text == preservedPrompt && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, preservedFocus),
                    "Passive click and deferred view recovery must preserve the open conversation, composer draft and keyboard focus.");
                var safeDiagnostic = interactionDiagnostic.ToString();
                Check(safeDiagnostic.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length == 2 &&
                      safeDiagnostic.Contains("desktop.passive-interaction.failed", StringComparison.Ordinal) &&
                      !safeDiagnostic.Contains(activityRun.Text, StringComparison.Ordinal) &&
                      !safeDiagnostic.Contains(" is not a Visual", StringComparison.Ordinal),
                    "The two actual passive WPF failures must log bounded coordinates without document text or raw exception payloads.");
            }
            finally { Console.SetError(originalDiagnostic); prompt.Text = retainedPrompt; }

            // A live event can be newer than the persisted service snapshot returned after a
            // reconnect. Switching away clears the visible projection; switching back must let
            // that older snapshot recreate the row instead of leaving activity blank forever.
            var selectedSession = session;
            var selectedJobs = activeJobs;
            ActivityEventAsync(new(4, "longform.progress", rootJob.Id, CerebralJson.Element(new
            {
                sessionId = selectedSession.Id,
                revision = 12, path = @"C:\Projects\Novel\draft.txt", targetWords = 1_000,
                verifiedWords = 900, reachedAllocations = 3, allocationCount = 4,
                updatedAt = now.AddSeconds(12)
            }), now.AddSeconds(12))).GetAwaiter().GetResult();
            Check(activityRows[LongFormActivityKey(selectedSession.Id)].Body.Contains("900 / 1,000 words"),
                "A newer live long-form event must be recorded before the reconnect regression is exercised.");
            session = new TaskSession
            {
                Id = "activity-other-fixture", ProfileId = "lead", Title = "Other conversation",
                Messages = [new() { Sequence = 1, At = now.AddSeconds(13), Content = "Review another conversation." }]
            };
            activeJobs = [];
            EnsureActivitySession();
            session = selectedSession;
            activeJobs = selectedJobs;
            EnsureActivitySession();
            Check(!activityRows.ContainsKey(LongFormActivityKey(selectedSession.Id)),
                "Switching away and back must start with an empty activity projection.");
            RecordLongFormActivity(selectedSession.Id, CerebralJson.Element(new
            {
                tracked = true, sourceJobId = rootJob.Id, revision = 11,
                path = @"C:\Projects\Novel\draft.txt", targetWords = 1_000,
                verifiedWords = 880, reachedAllocations = 3, allocationCount = 4,
                updatedAt = now.AddSeconds(11)
            }), now.AddSeconds(11), rootJob.Id, selectedJobs, false);
            Check(activityRows.TryGetValue(LongFormActivityKey(selectedSession.Id), out var rehydratedLongForm) &&
                  rehydratedLongForm.Body.Contains("880 / 1,000 words"),
                "After a conversation projection reset, an older persisted long-form snapshot must restore the activity row.");
            if (previewPath is not null)
            {
                if (!Path.IsPathFullyQualified(previewPath) || !previewPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Preview needs an absolute PNG path.");
                var bitmap = new RenderTargetBitmap(1280, 960, 96, 96, PixelFormats.Pbgra32);
                var backdrop = new DrawingVisual();
                using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, 1280, 960));
                bitmap.Render(backdrop);
                current.EntryViews["build"].Expander.IsExpanded = false;
                container.UpdateLayout(); transcript.ScrollToEnd(); container.UpdateLayout();
                bitmap.Render(container);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new FileStream(previewPath, FileMode.Create, FileAccess.Write); encoder.Save(output);
            }
            Check(Volatile.Read(ref ownerDispatches) == 0, "The entire controlled legacy inspection and preview performs no owner dispatch.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, activityChecks = checks, renderedTurns = 2, serviceCommandsExecuted = false,
                modelInvoked = false, windowShown = false, previewPath }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.ToString(), activityChecks = checks })); return 1; }
        finally
        {
            restoreActivityOwner?.Invoke();
            ownedActivityClient?.Dispose(); ownedActivityHandler?.Dispose();
            if (activitySource is not null) { activitySource.RootVisual = null; activitySource.Dispose(); }
            if (detachedContent is not null) Content = detachedContent;
            Close();
        }
    }

    private static IEnumerable<DependencyObject> AutomationDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in AutomationDescendants(child)) yield return descendant;
        }
    }
}
