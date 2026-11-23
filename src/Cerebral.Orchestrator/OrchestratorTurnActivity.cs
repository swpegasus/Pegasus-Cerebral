using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private const int TurnActivityPageSize = 40;
    private readonly Dictionary<string, TurnActivityView> turnActivity = new(StringComparer.Ordinal);
    private int turnActivityEpoch;
    private int conversationSelectionVersion;
    private int transcriptRenderVersion;
    private bool transcriptRestorePending;
    private double transcriptSavedOffset;
    private bool transcriptFollowEnd;
    private string? questionFocusKey;
    private bool questionFocusPending;

    private static bool ActualUserTurn(SessionMessage message) => message.Role == "user" && message.Origin == "user" && message.ToolName is null;
    private string TurnKey(string id, int sequence) => id + ":" + sequence;
    private TurnActivityView TurnView(string id, int sequence)
    {
        var key = TurnKey(id, sequence);
        if (turnActivity.TryGetValue(key, out var found)) return found;
        var view = new TurnActivityView(id, sequence);
        view.ActivityScroller.ScrollChanged += (_, change) =>
        {
            // Extent-only changes are produced while new rows are laid out. Keep the prior follow
            // choice until an actual scroll occurs so active output cannot disable its own follow.
            if (change.VerticalChange != 0)
                view.ActivityFollowEnd = ActivityAtEnd(view.ActivityScroller);
        };
        view.ActivityScroller.PreviewMouseWheel += (_, change) =>
        {
            if (change.Delta > 0) view.ActivityFollowEnd = false;
        };
        view.Expander.Expanded += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, view.Expander)) return;
            HoldTranscriptPosition();
            UpdateTurnActivity(view);
            if (!view.Loading) _ = LoadTurnActivityPageAsync(view, refresh: view.Loaded);
        };
        view.Expander.MouseDoubleClick += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, view.Expander.Header) && e.OriginalSource is not TextBlock) return;
            CopyActivityText(TurnActivityClipboard(view), "Turn activity copied."); e.Handled = true;
        };
        view.SummaryButton.Click += (_, _) => OpenLegacyTurnActivityPage(view);
        turnActivity.Add(key, view); return view;
    }
    private FrameworkElement InlineTurnActivity(int sequence, List<SessionMessage>? history)
    {
        var view = TurnView(session!.Id, sequence);
        view.History = history ?? [];
        UpdateTurnActivity(view);
        return view.SummaryButton;
    }
    private void ResetTurnActivity()
    {
        turnActivityEpoch++; conversationSelectionVersion++; transcriptRenderVersion++;
        turnActivity.Clear(); transcriptRestorePending = false; questionFocusKey = null; questionFocusPending = false;
    }
    private void HoldTranscriptPosition()
    {
        transcriptSavedOffset = transcript.VerticalOffset;
        transcriptFollowEnd = false;
        transcriptRestorePending = true;
    }
    private void CaptureTranscriptPosition()
    {
        if (transcriptRestorePending) return;
        transcriptSavedOffset = transcript.VerticalOffset;
        transcriptFollowEnd = transcript.ScrollableHeight <= transcript.VerticalOffset + 32;
        transcriptRestorePending = true;
    }
    private void RestoreTranscriptPosition(string? selectedId)
    {
        var version = ++transcriptRenderVersion;
        PassiveDesktopInteraction.Defer(Dispatcher, "transcript position", () =>
        {
            if (version != transcriptRenderVersion || selectedId != session?.Id) return;
            try
            {
                if (questionFocusPending && questionHost is { Visibility: Visibility.Visible } && transcript.Content is UIElement flow)
                {
                    transcript.ScrollToVerticalOffset(questionHost.TranslatePoint(new Point(), flow).Y);
                    questionFocusPending = false;
                }
                else if (transcriptFollowEnd) transcript.ScrollToEnd(); else transcript.ScrollToVerticalOffset(transcriptSavedOffset);
            }
            finally { transcriptRestorePending = false; }
        }, message => SetStatus(message, true), () =>
        {
            if (version == transcriptRenderVersion && selectedId == session?.Id) transcriptRestorePending = false;
        });
    }
    private static bool ActivityAtEnd(ScrollViewer scroller) =>
        scroller.ScrollableHeight <= scroller.VerticalOffset + 18;
    private static void TrackActivityText(TurnActivityView view, TextBox text)
    {
        if (!view.ActivityTexts.Add(text)) return;
        text.SelectionChanged += (_, _) =>
        {
            if (text.SelectionLength > 0) view.ActivityFollowEnd = false;
        };
    }
    private void ResumeActivityFollowAfterOutsideClick(DependencyObject source)
    {
        PassiveDesktopInteraction.TryRun("activity follow", () =>
        {
            foreach (var view in turnActivity.Values.Where(item => item.SessionId == session?.Id && item.Expander.IsExpanded))
            {
                if (UiTree.IsWithin(source, view.ActivityScroller)) continue;
                foreach (var text in view.ActivityTexts.Where(item => item.SelectionLength > 0)) text.Select(0, 0);
                view.ActivityFollowEnd = true;
                FollowActivityEnd(view);
            }
        }, message => SetStatus(message, true));
    }
    private void FollowActivityEnd(TurnActivityView view)
    {
        PassiveDesktopInteraction.Defer(view.ActivityScroller.Dispatcher, "activity position", () =>
        {
            if (session?.Id != view.SessionId || !view.ActivityFollowEnd || !view.Expander.IsExpanded) return;
            view.ActivityScroller.ScrollToEnd();
        }, message => SetStatus(message, true), () => view.ActivityFollowEnd = false);
    }
    private async Task RefreshTurnActivityAsync()
    {
        if (session is null) return;
        var id = session.Id; var epoch = turnActivityEpoch;
        try
        {
            var summaries = await Get<List<ActivityTurnSummary>>("activity.turns", new { sessionId = id });
            if (session?.Id != id || epoch != turnActivityEpoch) return;
            foreach (var summary in summaries)
            {
                var view = TurnView(id, summary.TurnSequence);
                view.Count = Math.Max(view.Count, summary.Count);
                MergeTurnSummary(view, summary.Latest);
                view.Error = null; UpdateTurnActivity(view);
            }
            RenderActivity();
            // Older turns can be absent from the bounded summary response. Expansion still asks
            // activity.list for that exact turn; absence here never establishes an empty journal.
            foreach (var view in turnActivity.Values.Where(x => x.SessionId == id && x.Expander.IsExpanded).ToArray())
                await LoadTurnActivityPageAsync(view, refresh: view.Loaded);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (session?.Id != id || epoch != turnActivityEpoch) return;
            foreach (var view in turnActivity.Values.Where(x => x.SessionId == id))
            { view.Error = "Recorded activity could not be refreshed: " + ex.Message; UpdateTurnActivity(view); }
        }
    }
    private async Task LoadTurnActivityPageAsync(TurnActivityView view, bool refresh = false)
    {
        if (view.Loading || session?.Id != view.SessionId) return;
        var epoch = turnActivityEpoch;
        view.Loading = true; view.Error = null; UpdateTurnActivity(view);
        var cursor = refresh ? 0L : view.Next ?? 0L;
        var refreshThrough = refresh ? Math.Max(view.LoadedThrough, view.Entries.Values.Select(x => x.Sequence).DefaultIfEmpty(0).Max()) : 0;
        try
        {
            do
            {
                var page = await Get<ActivityPage>("activity.list", new { sessionId = view.SessionId, turnSequence = view.TurnSequence,
                    afterSequence = cursor, limit = TurnActivityPageSize });
                if (session?.Id != view.SessionId || epoch != turnActivityEpoch) return;
                ApplyTurnPage(view, page, refresh);
                var last = page.Entries.LastOrDefault()?.Sequence ?? cursor;
                if (!refresh || page.NextAfterSequence is null || last >= refreshThrough || last <= cursor) break;
                cursor = page.NextAfterSequence.Value;
            } while (true);
            view.Loaded = true;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (session?.Id == view.SessionId && epoch == turnActivityEpoch)
                view.Error = "Recorded activity is unavailable: " + ex.Message;
        }
        finally
        {
            view.Loading = false;
            if (session?.Id == view.SessionId && epoch == turnActivityEpoch) UpdateTurnActivity(view);
        }
    }
    private void ApplyTurnPage(TurnActivityView view, ActivityPage page, bool refresh = false)
    {
        view.Count = Math.Max(view.Count, page.TotalCount);
        foreach (var entry in page.Entries) MergeTurnEntry(view, entry);
        var last = page.Entries.LastOrDefault()?.Sequence ?? 0;
        if (!refresh || last >= view.LoadedThrough)
        { view.Next = page.NextAfterSequence; view.LoadedThrough = Math.Max(view.LoadedThrough, last); }
        view.Loaded = true; view.Error = null;
    }
    private static bool NewerActivity(ActivityEntry current, ActivityEntry incoming) =>
        incoming.UpdatedAt > current.UpdatedAt || incoming.UpdatedAt == current.UpdatedAt &&
        (incoming.Id == current.Id ? current.State == "running" && incoming.State != "running" ||
            current.State == "uncertain" && incoming.State is "completed" or "failed" or "cancelled" : incoming.Sequence > current.Sequence);
    private static void MergeTurnSummary(TurnActivityView view, ActivityEntry entry)
    {
        if (entry.SessionId != view.SessionId || entry.TurnSequence != view.TurnSequence) return;
        // Summary responses omit arguments/results. They update the headline, never hydrated details.
        if (view.Latest is null || NewerActivity(view.Latest, entry)) view.Latest = entry;
    }
    private static void MergeTurnEntry(TurnActivityView view, ActivityEntry entry)
    {
        if (entry.SessionId != view.SessionId || entry.TurnSequence != view.TurnSequence) return;
        if (view.Entries.TryGetValue(entry.Id, out var previous) && !NewerActivity(previous, entry)) return;
        view.Entries[entry.Id] = entry;
        MergeTurnSummary(view, entry);
    }
    private async Task TurnActivityEventAsync(ServiceEvent item)
    {
        if (session is null) return;
        if (item.Type == "activity.updated")
        {
            var entry = item.Data.Deserialize<ActivityEntry>(CerebralJson.Options);
            if (entry is null || entry.SessionId != session.Id) return;
            var view = TurnView(entry.SessionId, entry.TurnSequence);
            if (!view.Entries.ContainsKey(entry.Id) && (view.Latest is null || entry.Sequence > view.Latest.Sequence)) view.Count++;
            MergeTurnEntry(view, entry); view.Progress = null; UpdateTurnActivity(view); RenderActivity(); return;
        }
        if (item.Type == "activity.error")
        {
            var id = ActivityString(item.Data, "sessionId");
            var job = activeJobs.FirstOrDefault(x => x.Id == item.JobId);
            if (id == session.Id || job is not null && JobsForSelectedConversation(job))
            {
                var sequence = job is null ? session.Messages.LastOrDefault(ActualUserTurn)?.Sequence ?? 0 : ActivityTurnForJob(job, item.At);
                var view = TurnView(session.Id, sequence);
                var message = ActivityString(item.Data, "message") ??
                    "Some activity could not be recorded. Check the job result before repeating an operation.";
                if (job is not null)
                    view.Failures["activity-recording:" + item.Sequence] = new TaskFailure
                    {
                        Id = "activity-recording:" + item.Sequence,
                        JobId = job.Id,
                        SessionId = session.Id,
                        Source = "Activity recording",
                        Message = message,
                        CreatedAt = item.At
                    };
                else view.RecordingError = message;
                UpdateTurnActivity(view);
            }
            return;
        }
        if (item.Type == "token" && activeJobs.FirstOrDefault(x => x.Id == item.JobId) is { } generating && JobsForSelectedConversation(generating))
        {
            var view = TurnView(session.Id, ActivityTurnForJob(generating, item.At));
            var progress = generating.ParentJobId is null ? "Generating a response" : ActivityActor(generating.ProfileId) + " is generating a response";
            if (view.Progress != progress) { view.Progress = progress; UpdateTurnActivity(view); }
            return;
        }
        if (item.Type == "job.updated")
        {
            var job = item.Data.Deserialize<AgentJob>(CerebralJson.Options);
            if (job is null) return;
            foreach (var view in turnActivity.Values.Where(x => x.SessionId == session.Id)) { view.Progress = null; UpdateTurnActivity(view); }
            if (!Active(job)) await RefreshTurnActivityAsync();
        }
    }
    private AgentJob RootActivityJob(AgentJob job)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (job.ParentJobId is { } parent && visited.Add(job.Id))
        {
            var found = activeJobs.FirstOrDefault(x => x.Id == parent);
            if (found is null) break;
            job = found;
        }
        return job;
    }
    private bool JobsForSelectedConversation(AgentJob job) => RootActivityJob(job).SessionId == session?.Id;
    private int ActivityTurnForJob(AgentJob job, DateTimeOffset at)
    {
        if (job.ParentJobId is not null && job.RootTurnSequence is { } pinned) return pinned;
        var root = RootActivityJob(job);
        var cutoff = job.ParentJobId is not null ? job.CreatedAt : at;
        return Math.Max(root.StartSequence, session?.Messages.Where(x => ActualUserTurn(x) && !x.ExcludedFromContext && x.At <= cutoff)
            .Select(x => x.Sequence).DefaultIfEmpty(root.StartSequence).Max() ?? root.StartSequence);
    }
    private List<AgentJob> JobsForTurn(TurnActivityView view)
    {
        if (session?.Id != view.SessionId) return [];
        var roots = activeJobs.Where(x => x.SessionId == view.SessionId && x.ParentJobId is null).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var result = new List<AgentJob>();
        foreach (var job in activeJobs)
        {
            var root = job; var visited = new HashSet<string>(StringComparer.Ordinal);
            while (root.ParentJobId is { } parent && visited.Add(root.Id))
            {
                var owner = activeJobs.FirstOrDefault(x => x.Id == parent);
                if (owner is null) break; root = owner;
            }
            if (!roots.ContainsKey(root.Id)) continue;
            var turn = job.ParentJobId is not null && job.RootTurnSequence is { } pinned ? pinned :
                Math.Max(root.StartSequence, session.Messages.Where(x => ActualUserTurn(x) && !x.ExcludedFromContext && x.At <= job.CreatedAt)
                    .Select(x => x.Sequence).DefaultIfEmpty(root.StartSequence).Max());
            var leadTurn = job.ParentJobId is null && view.TurnSequence >= job.StartSequence &&
                view.TurnSequence <= ActivityTurnForJob(job, job.FinishedAt ?? DateTimeOffset.UtcNow);
            if (turn == view.TurnSequence || leadTurn || view.Entries.Values.Any(x => x.JobId == job.Id)) result.Add(job);
        }
        return result;
    }
    private void UpdateTurnActivity(TurnActivityView view)
    {
        if (session?.Id != view.SessionId) return;
        CaptureTranscriptPosition();
        var followActivityEnd = view.ActivityFollowEnd;
        try
        {
            var related = JobsForTurn(view);
            var running = related.FirstOrDefault(x => Active(x) && (x.ParentJobId is not null || ActivityTurnForJob(x, DateTimeOffset.UtcNow) == view.TurnSequence));
            var lastJob = related.Where(x => x.ParentJobId is null).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            var lastJobState = lastJob is null ? null : JobActivityState(lastJob);
            var state = running is not null ? JobActivityState(running) : lastJob is not null ? Active(lastJob) ? "continued" : lastJobState! : view.Latest?.State ?? "ready";
            IEnumerable<ActivityEntry> headlineEntries = view.Entries.Count > 0 ? view.Entries.Values :
                view.Latest is { } latest ? [latest] : Array.Empty<ActivityEntry>();
            var failed = FailedActivityState(state);
            var completed = !failed && state == "completed";
            var summary = headlineEntries.Any() ? ActivitySummary(headlineEntries) :
                running?.Kind == "compact" ? "Compacting context" : running is not null ? "Working on this request" :
                view.History.Count > 0 ? "Saved activity" : "Activity";
            if (running is not null && view.Progress is not null) summary = view.Progress;
            var statePrefix = failed ? TurnStateLabel(state) + " · " : completed ? "Completed · " : running is not null ? "Working · " : "";
            view.Headline.Text = statePrefix + ExecutionLimit(summary, 180) + CallCount(view.Count);
            view.Headline.Foreground = failed ? Ui.ErrorBorder : completed ? Brushes.SeaGreen : running is not null || state == "running" ? Ui.Accent : Ui.Muted;
            view.Expander.Background = failed ? Ui.ErrorBackground : running is not null || state == "running" ? Ui.RaisedSurface : Ui.Surface;
            view.Expander.BorderBrush = failed ? Ui.ErrorBorder : completed ? Brushes.SeaGreen : running is not null || state == "running" ? Ui.Accent : Ui.Line;
            AutomationProperties.SetName(view.Expander, $"Activity for message {view.TurnSequence}: {view.Headline.Text}");
            if (!view.Expander.IsExpanded) return;
            var controls = new List<UIElement>();
            if (view.Error is not null) controls.Add(Ui.Text(ActivityBound(view.Error), 11, Ui.ErrorBorder));
            if (view.RecordingError is not null) controls.Add(Ui.Text(view.RecordingError, 11, Ui.ErrorBorder));
            if (view.Loading) controls.Add(Ui.Text("Loading recorded activity…", 11, Ui.Muted));
            var jobsById = related.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var entryGroups = view.Entries.Values.GroupBy(x => string.IsNullOrWhiteSpace(x.JobId) ? "profile:" + x.ProfileId : x.JobId)
                .OrderBy(x => x.Min(e => e.Sequence)).ToList();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var notices = view.History.Where(x => x.Origin == "service").OrderBy(x => x.At).ThenBy(x => x.Sequence).ToList();
            var failures = view.Failures.Values.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            var noticesAttached = false;
            foreach (var group in entryGroups)
            {
                var entries = group.OrderBy(x => x.Sequence).ToList();
                emitted.Add(group.Key);
                var iterationNotices = group.Key == lastJob?.Id ? notices : [];
                noticesAttached |= iterationNotices.Count > 0;
                controls.Add(HelperActivityControl(view, group.Key, jobsById.GetValueOrDefault(group.Key), entries, iterationNotices,
                    failures.Where(x => x.JobId == group.Key).ToList()));
            }
            foreach (var helper in related.Where(x => x.ParentJobId is not null).OrderBy(x => x.CreatedAt))
                if (emitted.Add(helper.Id)) controls.Add(HelperActivityControl(view, helper.Id, helper, [], [],
                    failures.Where(x => x.JobId == helper.Id).ToList()));
            if (lastJob is not null && !emitted.Contains(lastJob.Id) &&
                (notices.Count > 0 && !noticesAttached || failures.Any(x => x.JobId == lastJob.Id)))
            {
                controls.Add(HelperActivityControl(view, lastJob.Id, lastJob, [], notices,
                    failures.Where(x => x.JobId == lastJob.Id).ToList()));
                emitted.Add(lastJob.Id);
                noticesAttached = true;
            }
            if (lastJob is not null)
            {
                if (!string.IsNullOrEmpty(lastJob.WaitingReason))
                    controls.Add(Ui.Text(ActivityBound(lastJob.WaitingReason), 11, Ui.Muted));
                if (lastJobState is "failed" or "incomplete" or "interrupted" && !string.IsNullOrWhiteSpace(lastJob.Error))
                    controls.Add(Ui.Text("Error · " + ExecutionLimit(ActivityBound(lastJob.Error).Replace('\r', ' ').Replace('\n', ' '), 500), 11, Ui.ErrorBorder));
            }
            if (view.Loaded && view.Next is not null)
            {
                var more = Ui.Button($"Load more recorded activity ({view.Entries.Count} loaded of {view.Count})", () => { HoldTranscriptPosition(); _ = LoadTurnActivityPageAsync(view); });
                more.IsEnabled = !view.Loading; AutomationProperties.SetAutomationId(more, $"TurnActivityMore-{view.TurnSequence}"); controls.Add(more);
            }
            if (!view.Loaded && !view.Loading)
            {
                var retry = Ui.Button(view.Error is null ? "Load recorded activity" : "Retry recorded activity", () => { HoldTranscriptPosition(); _ = LoadTurnActivityPageAsync(view); });
                controls.Add(retry);
            }
            if (view.Count == 0 && (view.Loaded || view.Error is not null))
            {
                var history = view.History.Where(x => x.Origin != "service").ToList();
                if (history.Count > 0) controls.Add(LegacyTurnControl(view, "Saved-message activity", history));
            }
            else if (view.Loaded && view.Entries.Count > 0)
            {
                // A turn that began before journaling can have completed legacy commands. Keep only
                // results conclusively older than the first recorded start, including their preceding call.
                var first = view.Entries.Values.Min(x => x.StartedAt);
                var old = view.History.Where(x => x.Origin != "service" && x.At < first && x.ToolName is not null).ToList();
                if (old.Count > 0) controls.Add(LegacyTurnControl(view, "Earlier saved tool results", old));
            }
            if (!noticesAttached) controls.AddRange(notices.Select(x => ServiceUpdateControl(view, x)));
            if (view.Loaded && controls.Count == 0) controls.Add(Ui.Text("No activity was recorded for this turn.", 11, Ui.Muted));
            SyncActivityChildren(view.Rows, controls);
            if (followActivityEnd) FollowActivityEnd(view);
        }
        finally { RestoreTranscriptPosition(view.SessionId); }
    }
    private FrameworkElement TurnEntryControl(TurnActivityView view, ActivityEntry entry)
    {
        if (!view.EntryViews.TryGetValue(entry.Id, out var row))
        {
            row = new RecordedActivityView();
            view.EntryViews.Add(entry.Id, row);
            TrackActivityText(view, row.Text);
            var captured = row;
            row.Expander.Expanded += (_, _) => UpdateRecordedDetails(captured);
            row.Expander.MouseDoubleClick += (_, e) =>
            {
                UpdateRecordedDetails(captured); CopyActivityText(captured.Text.Text, "Call details copied."); e.Handled = true;
            };
            AutomationProperties.SetAutomationId(row.Expander, "TurnActivityEntry-" + entry.Id);
        }
        row.Entry = entry;
        var compact = WorkflowWorkspace.ProjectRecordedActivity(entry,
            new(ServiceConnectionIdentity ?? "Owner not recorded"));
        var operation = compact.Agent + " · " + compact.Action + " · [" + compact.Tool + "]";
        var failed = entry.State is "failed" or "uncertain";
        var reason = failed ? ActivityFailureReason(entry.Result) : "";
        var failureLabel = entry.State == "uncertain" ? "Unconfirmed" : "Failed";
        row.Title.Text = ExecutionLimit(failed
            ? failureLabel + " · " + operation + (reason.Length > 0 ? " · " + reason : "")
            : operation, 220);
        row.Title.Foreground = failed ? Ui.ErrorText : entry.State == "running" ? Ui.Accent : Ui.Ink;
        StyleActivityCard(row.Expander, failed, entry.State == "running");
        row.Expander.ToolTip = entry.ToolName;
        AutomationProperties.SetName(row.Expander, failed ? "Failed call: " + row.Title.Text : "Call: " + row.Title.Text);
        if (row.Expander.IsExpanded) UpdateRecordedDetails(row);
        return row.Expander;
    }
    private void UpdateRecordedDetails(RecordedActivityView row)
    {
        if (row.Entry is not { } entry) return;
        var arguments = ExecutionLimit(ReadableActivityJson(entry.Arguments), 8192);
        var result = entry.Result.Length > 0 ? ExecutionLimit(ReadableActivityJson(entry.Result), 16384) : entry.State == "running" ? "Running; no result has been recorded yet." : "No result text was recorded.";
        if (entry.Truncated) result = "Arguments or result were shortened for stored activity.\n\n" + result;
        var details = (arguments.Length > 0 ? "Arguments\n" + arguments + "\n\n" : "") + "Result\n" + result;
        if (row.Text.Text != details) row.Text.Text = details;
        row.Expander.Content ??= row.Text;
    }
    private FrameworkElement HelperActivityControl(TurnActivityView view, string jobId, AgentJob? job, List<ActivityEntry> entries,
        IReadOnlyList<SessionMessage> serviceUpdates, IReadOnlyList<TaskFailure> failures)
    {
        if (!view.HelperViews.TryGetValue(jobId, out var helper))
        {
            helper = new HelperActivityView(); view.HelperViews.Add(jobId, helper);
            TrackActivityText(view, helper.Details);
            var captured = helper;
            helper.Expander.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, captured.Expander)) return;
                if (captured.Job is null && !captured.Loading) _ = LoadHelperActivityJobAsync(view, jobId, captured);
            };
            helper.Expander.MouseDoubleClick += (_, e) =>
            {
                CopyActivityText(captured.Details.Text + (captured.Details.Text.Length > 0 ? "\n\n" : "") +
                    string.Join("\n\n", view.Entries.Values.Where(item =>
                            (string.IsNullOrWhiteSpace(item.JobId) ? "profile:" + item.ProfileId : item.JobId) == jobId)
                        .OrderBy(item => item.Sequence).Select(RecordedActivityClipboard)), "Agent iteration copied.");
                e.Handled = true;
            };
            AutomationProperties.SetAutomationId(helper.Expander, "TurnActivityHelper-" + jobId);
        }
        helper.Job = job ?? helper.Job;
        var profileId = helper.Job?.ProfileId ?? entries.FirstOrDefault()?.ProfileId;
        var status = helper.Job is { } known ? JobActivityState(known) : entries.LastOrDefault()?.State ?? "unknown";
        var callSummary = entries.Count > 0 ? ActivitySummary(entries) : helper.Job?.ParentJobId is not null ? "Delegated work" : "Agent work";
        helper.Title.Text = $"{ActivityActor(profileId)} · {callSummary}" + CallCount(entries.Count);
        var failed = FailedActivityState(status);
        var active = helper.Job is { } activeJob && Active(activeJob);
        helper.Title.Foreground = failed ? Ui.ErrorText : status == "completed" ? Brushes.SeaGreen : active ? Ui.Accent : Ui.Ink;
        StyleActivityCard(helper.Expander, failed, active);
        AutomationProperties.SetName(helper.Expander, failed ? "Failed agent iteration: " + helper.Title.Text : "Agent iteration: " + helper.Title.Text);
        var children = new List<UIElement>();
        if (helper.Job is { ParentJobId: not null } recorded)
        {
            var details = $"Assignment\n{ActivityBound(recorded.Prompt)}" +
                (!string.IsNullOrWhiteSpace(recorded.WaitingReason) ? "\n\n" + ActivityBound(recorded.WaitingReason) : "") +
                (!string.IsNullOrWhiteSpace(recorded.Error) ? "\n\nError\n" + ActivityBound(recorded.Error) : "") +
                (!string.IsNullOrWhiteSpace(recorded.Result) ? "\n\nReturned result\n" + ActivityBound(recorded.Result) : "");
            var parent = activeJobs.FirstOrDefault(x => x.Id == recorded.ParentJobId);
            if (parent?.ConsumedHelperJobIds.Contains(recorded.Id) == true) details += "\n\nThis result was supplied to the lead for integration; this does not establish independent verification.";
            if (recorded.WorkerId != "local") details += "\n\nCommand history remains on the remote worker; this is the recorded assignment/status/result.";
            if (helper.Details.Text != details) helper.Details.Text = details;
        }
        else if (helper.Job is null && entries.Count == 0) helper.Details.Text = helper.Error ?? "Expand to load the saved helper assignment and result.";
        if (helper.Job?.ParentJobId is not null || entries.Count == 0)
            children.Add(helper.Details);
        children.AddRange(entries.Select(entry => (At: entry.StartedAt, Sequence: entry.Sequence, Control: (UIElement)TurnEntryControl(view, entry)))
            .Concat(serviceUpdates.Select(entry => (At: entry.At, Sequence: (long)entry.Sequence, Control: (UIElement)ServiceUpdateControl(view, entry))))
            .Concat(failures.Select(entry => (At: entry.CreatedAt, Sequence: long.MaxValue, Control: (UIElement)FailureActivityControl(view, entry))))
            .OrderBy(item => item.At).ThenBy(item => item.Sequence).Select(item => item.Control));
        SyncActivityChildren(helper.Rows, children);
        return helper.Expander;
    }

    private FrameworkElement FailureActivityControl(TurnActivityView view, TaskFailure failure)
    {
        if (!view.FailureViews.TryGetValue(failure.Id, out var item))
        {
            item = new FailureActivityView(); view.FailureViews.Add(failure.Id, item);
            TrackActivityText(view, item.Text);
            var captured = item;
            captured.Expander.MouseDoubleClick += (_, e) =>
            {
                CopyActivityText(captured.Text.Text, "Failure details copied."); e.Handled = true;
            };
            AutomationProperties.SetAutomationId(captured.Expander, "TurnActivityFailure-" + failure.Id);
        }
        var resolved = failure.Status == "resolved";
        var reason = ExecutionLimit(failure.Message.Replace('\r', ' ').Replace('\n', ' ').Trim(), 140);
        item.Title.Text = (resolved ? "Resolved" : "Failed") + " · " +
            ExecutionLimit(string.IsNullOrWhiteSpace(failure.Source) ? "Service" : failure.Source, 80) +
            (reason.Length > 0 ? " · " + reason : " · No actionable failure reason was recorded.");
        item.Title.Foreground = resolved ? Brushes.SeaGreen : Ui.ErrorText;
        StyleActivityCard(item.Expander, !resolved, false);
        var details = failure.Source + "\n" + failure.Message +
            (string.IsNullOrWhiteSpace(failure.Resolution) ? "" : "\n\nResolution\n" + failure.Resolution);
        if (item.Text.Text != details) item.Text.Text = details;
        AutomationProperties.SetName(item.Expander, item.Title.Text);
        return item.Expander;
    }

    private FrameworkElement ServiceUpdateControl(TurnActivityView view, SessionMessage entry)
    {
        var key = "service:" + entry.Sequence;
        if (!view.ServiceViews.TryGetValue(key, out var service))
        {
            service = new ServiceActivityView(); view.ServiceViews.Add(key, service);
            TrackActivityText(view, service.Text);
            var captured = service;
            captured.Expander.MouseDoubleClick += (_, e) =>
            {
                CopyActivityText(captured.Text.Text, "Service update copied."); e.Handled = true;
            };
            AutomationProperties.SetAutomationId(captured.Expander, "TurnActivityService-" + entry.Sequence);
        }
        var detail = ActivityBound(entry.Content);
        var firstLine = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        service.Title.Text = "Service update" + (firstLine.Length > 0 ? " · " + ExecutionLimit(firstLine, 120) : "");
        if (service.Text.Text != detail) service.Text.Text = detail;
        return service.Expander;
    }
    private async Task LoadHelperActivityJobAsync(TurnActivityView view, string jobId, HelperActivityView helper)
    {
        if (session?.Id != view.SessionId) return;
        var epoch = turnActivityEpoch; helper.Loading = true;
        try
        {
            var job = await Get<AgentJob>("jobs.get", new { id = jobId });
            if (session?.Id != view.SessionId || epoch != turnActivityEpoch) return;
            if (!view.Entries.Values.Any(x => x.JobId == job.Id && x.ActorSessionId == job.SessionId))
                throw new InvalidOperationException("Helper result does not match this turn's recorded activity.");
            helper.Job = job;
        }
        catch (Exception ex) { if (session?.Id == view.SessionId && epoch == turnActivityEpoch) helper.Error = "Helper details unavailable: " + ex.Message; }
        finally { helper.Loading = false; if (session?.Id == view.SessionId && epoch == turnActivityEpoch) UpdateTurnActivity(view); }
    }
    private FrameworkElement LegacyTurnControl(TurnActivityView view, string title, List<SessionMessage> history, bool serviceOnly = false)
    {
        var key = serviceOnly ? "service" : title;
        if (!view.LegacyViews.TryGetValue(key, out var legacy))
        {
            legacy = new LegacyActivityView(); view.LegacyViews.Add(key, legacy);
            legacy.Expander.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, legacy.Expander)) return;
                HoldTranscriptPosition(); UpdateTurnActivity(view);
            };
        }
        legacy.Expander.Header = $"{title} · {history.Count}";
        if (!legacy.Expander.IsExpanded) return legacy.Expander;
        var controls = new List<UIElement>();
        foreach (var entry in history.Take(legacy.VisibleCount))
        {
            if (!legacy.Messages.TryGetValue(entry.Sequence, out var row))
            {
                var label = entry.ToolName ?? (entry.Origin == "helper" ? "Helper result" : serviceOnly ? "Service update" : "Saved command");
                var text = ActivityText(ActivityBound(entry.Content));
                TrackActivityText(view, text);
                row = new Expander { Header = label, Content = text, Margin = new Thickness(0, 0, 0, 5) };
                row.MouseDoubleClick += (_, e) => { CopyActivityText(text.Text, "Saved details copied."); e.Handled = true; };
                legacy.Messages.Add(entry.Sequence, row);
            }
            controls.Add(row);
        }
        if (history.Count > legacy.VisibleCount) controls.Add(Ui.Button("Load more saved activity", () => { HoldTranscriptPosition(); legacy.VisibleCount += TurnActivityPageSize; UpdateTurnActivity(view); }));
        SyncActivityChildren(legacy.Rows, controls); return legacy.Expander;
    }
    private static void SyncActivityChildren(Panel panel, IReadOnlyList<UIElement> desired)
    {
        for (var index = panel.Children.Count - 1; index >= 0; index--) if (!desired.Contains(panel.Children[index])) panel.Children.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var control = desired[index];
            if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], control)) continue;
            if (control is FrameworkElement element && element.Parent is Panel oldParent) oldParent.Children.Remove(control);
            panel.Children.Insert(index, control);
        }
    }
    private static string JobActivityState(AgentJob job) => Active(job) ? job.Status : job.Outcome ?? job.Status;
    private static bool FailedActivityState(string state) => state is "failed" or "uncertain" or "remote-unconfirmed" or "incomplete" or "interrupted";
    private static string TurnStateLabel(string state) => state switch
    {
        "running" => "Working", "waiting-helper" => "Waiting for helpers", "waiting-human" => "Answer needed", "queued" => "Queued",
        "completed" => "Completed", "failed" => "Failed", "incomplete" => "Incomplete", "cancelled" => "Cancelled",
        "uncertain" or "remote-unconfirmed" => "Unconfirmed", "interrupted" => "Interrupted", "ready" => "Ready", "continued" => "Continued with next input", "waiting" => "Waiting", "remote-waiting" => "Working on remote worker", _ => state
    };
    private static string CallCount(int count) => count > 0 ? $" · {count} call{(count == 1 ? "" : "s")}" : "";
    private static string ActivitySummary(IEnumerable<ActivityEntry> entries)
    {
        var labels = new List<string>();
        void Add(string value) { if (!labels.Contains(value, StringComparer.Ordinal)) labels.Add(value); }
        foreach (var entry in entries.OrderBy(x => x.Sequence))
        {
            JsonElement arguments = default;
            if (TryParseActivityJson(entry.Arguments, out var recorded)) arguments = recorded;
            Add(WorkflowWorkspace.DescribeRecordedActivity(entry.ToolName, arguments).OperationType);
            if (labels.Count == 3) break;
        }
        return labels.Count == 0 ? "Activity" : string.Join(", ", labels.Select((x, i) => i == 0 ? x : char.ToLowerInvariant(x[0]) + x[1..]));
    }
    private void CopyActivityText(string text, string status)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); SetStatus(status); }
        catch (Exception ex) { SetStatus("Clipboard is unavailable: " + ex.Message, true); }
    }
    private static string TurnActivityClipboard(TurnActivityView view)
    {
        var parts = new List<string> { view.Headline.Text };
        parts.AddRange(view.Entries.Values.OrderBy(x => x.Sequence).Select(RecordedActivityClipboard));
        if (view.RecordingError is not null) parts.Add(view.RecordingError);
        if (view.Error is not null) parts.Add(view.Error);
        return string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    private static string RecordedActivityClipboard(ActivityEntry entry)
    {
        var arguments = ReadableActivityJson(entry.Arguments);
        var result = ReadableActivityJson(entry.Result);
        return (entry.Title.Length > 0 ? entry.Title : entry.ToolName) +
            (arguments.Length > 0 ? "\nArguments\n" + arguments : "") +
            (result.Length > 0 ? "\nResult\n" + result : "");
    }
    private static string ReadableActivityJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (!TryParseActivityJson(text, out var value)) return text;
        var lines = new List<string>();
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
            action.GetString() == "tool")
        {
            var name = value.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String
                ? toolName.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name)) lines.Add("Tool: " + name);
            if (value.TryGetProperty("arguments", out var arguments)) AppendActivityValue(lines, "Arguments", arguments, 0);
            foreach (var property in value.EnumerateObject().Where(property => property.Name is not ("action" or "name" or "arguments")))
                AppendActivityValue(lines, property.Name, property.Value, 0);
        }
        else AppendActivityValue(lines, null, value, 0);
        return lines.Count == 0 ? text : string.Join('\n', lines);
    }
    private static bool TryParseActivityJson(string text, out JsonElement value)
    {
        value = default;
        var candidate = text.Trim();
        for (var depth = 0; depth < 3; depth++)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                value = document.RootElement.Clone();
            }
            catch (JsonException) { return depth > 0; }
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } inner) return true;
            var trimmed = inner.Trim();
            if (trimmed.Length < 2 || trimmed[0] is not ('{' or '[' or '"')) return true;
            candidate = trimmed;
        }
        return true;
    }
    private static void AppendActivityValue(List<string> lines, string? name, JsonElement value, int depth)
    {
        var indent = new string(' ', depth * 2);
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (name is not null) { lines.Add(indent + name + ":"); depth++; indent += "  "; }
            foreach (var property in value.EnumerateObject()) AppendActivityValue(lines, property.Name, property.Value, depth);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length == 0) { lines.Add(indent + (name is null ? "[]" : name + ": []")); return; }
            if (items.Length <= 8 && items.All(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                lines.Add(indent + (name is null ? "" : name + ": ") + string.Join(", ", items.Select(item => ActivityScalar(name, item))));
                return;
            }
            if (name is not null) { lines.Add(indent + name + ":"); depth++; indent += "  "; }
            foreach (var item in items.Take(64)) AppendActivityValue(lines, "-", item, depth);
            if (items.Length > 64) lines.Add(indent + $"[{items.Length - 64:N0} more items omitted]");
            return;
        }
        lines.Add(indent + (name is null ? "" : name + ": ") + ActivityScalar(name, value));
    }
    private static string ActivityScalar(string? name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            if ((name is "content" or "newText" or "oldText") && text.Length > 240)
                return $"[{text.Length:N0} characters omitted from the activity display; the retained transaction remains available]";
            return text;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => value.GetRawText()
        };
    }
    private static string ExecutionLimit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
    private static string ActivityFailureReason(string result)
    {
        var readable = ReadableActivityJson(result).Replace('\r', '\n');
        foreach (var candidate in readable.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate is "Result" or "Error" or "Arguments" ||
                candidate.StartsWith("Tool ", StringComparison.Ordinal) && candidate.Contains("success=", StringComparison.OrdinalIgnoreCase)) continue;
            return ExecutionLimit(candidate, 120);
        }
        return "No actionable failure reason was recorded.";
    }
    private static void StyleActivityCard(Expander expander, bool failed, bool running)
    {
        expander.Background = failed ? Ui.ErrorBackground : running ? Ui.RaisedSurface : Ui.Surface;
        expander.BorderBrush = failed ? Ui.ErrorBorder : running ? Ui.Accent : Ui.Line;
        expander.BorderThickness = new Thickness(1);
        expander.Padding = new Thickness(8, 5, 8, 5);
        expander.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = .13, Color = Colors.Black };
    }
    private static TextBox ActivityText(string text = "") => new()
    {
        Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, Foreground = Ui.Ink, Padding = new Thickness(4, 6, 4, 6), MaxHeight = 340,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private sealed class TurnActivityView
    {
        public string SessionId { get; }
        public int TurnSequence { get; }
        public int Count;
        public ActivityEntry? Latest;
        public bool Loaded, Loading;
        public long? Next;
        public long LoadedThrough;
        public string? Error;
        public string? RecordingError;
        public string? Progress;
        public List<SessionMessage> History = [];
        public Dictionary<string, ActivityEntry> Entries = new(StringComparer.Ordinal);
        public Dictionary<string, RecordedActivityView> EntryViews = new(StringComparer.Ordinal);
        public Dictionary<string, HelperActivityView> HelperViews = new(StringComparer.Ordinal);
        public Dictionary<string, ServiceActivityView> ServiceViews = new(StringComparer.Ordinal);
        public Dictionary<string, TaskFailure> Failures = new(StringComparer.Ordinal);
        public Dictionary<string, FailureActivityView> FailureViews = new(StringComparer.Ordinal);
        public Dictionary<string, LegacyActivityView> LegacyViews = new(StringComparer.Ordinal);
        public HashSet<TextBox> ActivityTexts = [];
        public bool ActivityFollowEnd = true;
        public TextBlock Headline { get; } = Ui.Text("Activity", 12, Ui.Muted);
        public StackPanel Rows { get; } = new() { Margin = new Thickness(6) };
        public ScrollViewer ActivityScroller { get; }
        public Border ActivityBox { get; }
        public Expander Expander { get; }
        public Button SummaryButton { get; }
        public TurnActivityView(string sessionId, int turnSequence)
        {
            SessionId = sessionId; TurnSequence = turnSequence;
            Headline.TextWrapping = TextWrapping.NoWrap; Headline.TextTrimming = TextTrimming.CharacterEllipsis; Headline.Margin = new Thickness(0, 3, 0, 3);
            ActivityScroller = new ScrollViewer
            {
                Content = Rows,
                MaxHeight = 210,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                // Pixel scrolling is required because one expanded agent iteration can contain
                // many chronological rows; logical StackPanel scrolling would see only one item.
                CanContentScroll = false,
                PanningMode = PanningMode.Both
            };
            ActivityBox = new Border
            {
                Child = ActivityScroller,
                Margin = new Thickness(12, 6, 2, 6),
                Padding = new Thickness(3),
                Background = Ui.RaisedSurface,
                BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = .18, Color = Colors.Black }
            };
            Expander = new Expander
            {
                Header = Headline,
                Content = ActivityBox,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(11, 7, 11, 7),
                MinHeight = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Ui.Surface,
                BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1)
            };
            AutomationProperties.SetAutomationId(Expander, "TurnActivity-" + turnSequence);
            SummaryButton = Ui.Button("Activity", static () => { });
            SummaryButton.SetBinding(ContentControl.ContentProperty,
                new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = Headline });
            SummaryButton.SetBinding(Control.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = Headline });
            SummaryButton.Background = Brushes.Transparent; SummaryButton.BorderThickness = new Thickness(0);
            SummaryButton.Padding = new Thickness(0); SummaryButton.Margin = new Thickness(0, 3, 0, 6);
            SummaryButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            SummaryButton.ToolTip = "Open this turn on the Activity page";
            AutomationProperties.SetAutomationId(SummaryButton, "TurnActivitySummary-" + turnSequence);
            AutomationProperties.SetName(SummaryButton, "Open this turn on the Activity page");
        }
    }
    private sealed class RecordedActivityView
    {
        public ActivityEntry? Entry;
        public TextBlock Title { get; } = Ui.Text("", 12);
        public TextBox Text { get; } = ActivityText();
        public Expander Expander { get; }
        public RecordedActivityView() { Expander = new Expander { Header = Title, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch }; }
    }
    private sealed class ServiceActivityView
    {
        public TextBlock Title { get; } = Ui.Text("Service update", 12, Ui.Muted);
        public TextBox Text { get; } = ActivityText();
        public Expander Expander { get; }
        public ServiceActivityView()
        {
            Expander = new Expander { Header = Title, Content = Text, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }
    }
    private sealed class FailureActivityView
    {
        public TextBlock Title { get; } = Ui.Text("Failed", 12, Ui.ErrorText);
        public TextBox Text { get; } = ActivityText();
        public Expander Expander { get; }
        public FailureActivityView()
        {
            Expander = new Expander { Header = Title, Content = Text, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }
    }
    private sealed class HelperActivityView
    {
        public AgentJob? Job;
        public bool Loading;
        public string? Error;
        public TextBlock Title { get; } = Ui.Text("", 12);
        public TextBox Details { get; } = ActivityText();
        public StackPanel Rows { get; } = new() { Margin = new Thickness(14, 4, 0, 8) };
        public Expander Expander { get; }
        public HelperActivityView() { Expander = new Expander { Header = Title, Content = Rows, Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch }; }
    }
    private sealed class LegacyActivityView
    {
        public int VisibleCount = TurnActivityPageSize;
        public Dictionary<int, Expander> Messages = [];
        public StackPanel Rows { get; } = new();
        public Expander Expander { get; }
        public LegacyActivityView() { Expander = new Expander { Content = Rows, Margin = new Thickness(0, 6, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch }; }
    }
}
