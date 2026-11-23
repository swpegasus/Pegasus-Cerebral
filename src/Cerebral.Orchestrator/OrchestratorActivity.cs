using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private const int ActivityLimit = 40;
    private const int ActivityTextLimit = 12000;
    private readonly StackPanel activityList = new();
    private readonly TextBlock activitySummary = Ui.Text("Recent commands and helper work appear here.", 11, Ui.Muted);
    private readonly Dictionary<string, ActivityRow> activityRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> activitySessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> activityGeneratingJobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LongFormActivityVersion> longFormActivityVersions = new(StringComparer.Ordinal);
    private string? activitySessionId;
    private string? activityRootJobId;
    private int activityTurnSequence;
    private bool activityFrozen;

    private UIElement ActivityView()
    {
        var root = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var title = new StackPanel();
        title.Children.Add(Ui.Text("Current activity", 16));
        activitySummary.Margin = new Thickness(0, 0, 0, 7);
        title.Children.Add(activitySummary);
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        // The ScrollViewer has a native automation peer; StackPanel does not.
        var scroll = new ScrollViewer
        {
            Content = activityList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetAutomationId(scroll, "ActivityList");
        AutomationProperties.SetName(scroll, "Recent conversation and helper activity");
        root.Children.Add(scroll);
        return root;
    }

    private void EnsureActivitySession()
    {
        var latest = session is null ? null : activeJobs.Where(x => x.SessionId == session.Id && x.ParentJobId is null && x.Kind != "compact")
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var latestInput = session?.Messages.LastOrDefault(x => x.Role == "user" && x.Origin == "user" && x.ToolName is null)?.Sequence ?? 0;
        var turn = Math.Max(latest?.StartSequence ?? 0, latestInput);
        if (activitySessionId == session?.Id && activityRootJobId == latest?.Id && activityTurnSequence == turn) return;
        activitySessionId = session?.Id; activityRootJobId = latest?.Id; activityTurnSequence = turn;
        activityRows.Clear(); activityList.Children.Clear(); activitySessionIds.Clear(); activityGeneratingJobs.Clear();
        // Version guards only describe rows in the current activity projection. When selecting
        // another conversation or turn clears those rows, clear the guards too so an older
        // persisted service snapshot can hydrate the newly selected projection after reconnect.
        longFormActivityVersions.Clear();
        activityFrozen = latest is null || !Active(latest);
        if (activitySessionId is not null) activitySessionIds.Add(activitySessionId);
    }

    private List<AgentJob> RelatedActivityJobs()
    {
        EnsureActivitySession();
        if (session is null || activityRootJobId is null) return [];
        var selected = new HashSet<string>(StringComparer.Ordinal) { activityRootJobId };
        bool added;
        do
        {
            added = false;
            foreach (var job in activeJobs)
                if (job.ParentJobId is string parent && selected.Contains(parent)) added |= selected.Add(job.Id);
        } while (added);
        var result = activeJobs.Where(x => selected.Contains(x.Id)).ToList();
        activitySessionIds.Clear(); activitySessionIds.Add(session.Id);
        foreach (var job in result) activitySessionIds.Add(job.SessionId);
        activityFrozen = !result.Any(Active);
        return result;
    }

    private async Task RefreshActivityAsync()
    {
        EnsureActivitySession();
        if (session is null) { RenderActivity(); return; }
        var selectedId = session.Id;
        var related = RelatedActivityJobs();
        var selectedRoot = activityRootJobId; var selectedTurn = activityTurnSequence;
        foreach (var job in related.OrderByDescending(x => x.CreatedAt).Take(ActivityLimit)) RecordActivityJob(job);
        RecordActivitySession(session, related);
        RenderActivity();
        // Current service contracts return a session snapshot; bound the number of helper reads
        // and the messages retained by this view. The activity pane is not another history store.
        var helperIds = related.Where(x => x.SessionId != selectedId)
            .OrderByDescending(x => Active(x)).ThenByDescending(x => x.CreatedAt)
            .Select(x => x.SessionId).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        var helpers = await Task.WhenAll(helperIds.Select(async id =>
        {
            try { return await Get<TaskSession>("sessions.get", new { id }); }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { return null; }
            catch (Exception ex)
            {
                if (session?.Id == selectedId)
                    PutActivity("unavailable:" + id, DateTimeOffset.UtcNow, "Unavailable", "Helper activity",
                        ActivityActor(related.FirstOrDefault(x => x.SessionId == id)?.ProfileId), ActivityBound(ex.Message));
                return null;
            }
        }));
        if (session?.Id != selectedId || activityRootJobId != selectedRoot || activityTurnSequence != selectedTurn) return;
        foreach (var helper in helpers)
            if (helper is not null) RecordActivitySession(helper, related);
        await Task.WhenAll(RefreshActivityFailuresAsync(selectedId, related), RefreshActivityPresentationsAsync(selectedId, related),
            RefreshLongFormActivityAsync(selectedId, related, selectedRoot, selectedTurn));
        if (session?.Id != selectedId || activityRootJobId != selectedRoot || activityTurnSequence != selectedTurn) return;
        RenderActivity();
    }

    private async Task ActivityEventAsync(ServiceEvent item)
    {
        EnsureActivitySession();
        if (session is null) return;
        var selectedId = session.Id;
        // Events arrive before the main window refreshes its jobs grid. Adopt this authoritative
        // snapshot so a newly delegated helper can be recognized immediately.
        if (item.Type == "job.updated")
        {
            var changed = item.Data.Deserialize<AgentJob>(CerebralJson.Options);
            if (changed is not null)
            {
                var index = activeJobs.FindIndex(x => x.Id == changed.Id);
                if (index < 0) activeJobs.Add(changed); else activeJobs[index] = changed;
            }
        }
        var related = RelatedActivityJobs();
        var job = related.FirstOrDefault(x => x.Id == item.JobId);
        var eventSessionId = ActivityString(item.Data, "sessionId");
        if (item.Type == "session.updated") eventSessionId = ActivityString(item.Data, "id");
        var isRelated = job is not null || eventSessionId is not null && activitySessionIds.Contains(eventSessionId);

        if (activityFrozen && item.Type is "token" or "tool.started" or "model.updated") return;
        if (item.Type == "model.updated")
        {
            var model = item.Data.Deserialize<ModelRecord>(CerebralJson.Options);
            if (model is null || !related.Any(x => Active(x) && x.Profile.ModelId == model.Id)) return;
            PutActivity("model:" + model.Id, item.At, model.State, model.Name, "Model runtime",
                ActivityBound(model.Error ?? $"Model {model.Name} is {model.State}."));
            RenderActivity(); return;
        }
        if (!isRelated) return;
        if (item.Type == "failures.updated") { await RefreshActivityFailuresAsync(selectedId, related); RenderActivity(); return; }
        switch (item.Type)
        {
            case "job.updated" when job is not null:
                activityGeneratingJobs.Remove(job.Id); RecordActivityJob(job, at: item.At);
                if (!Active(job))
                {
                    var finished = await Get<TaskSession>("sessions.get", new { id = job.SessionId });
                    if (session?.Id != selectedId) return;
                    RecordActivitySession(finished, related);
                    await RefreshActivityFailuresAsync(selectedId, related);
                }
                break;
            case "token" when job is not null:
                if (!activityGeneratingJobs.Add(job.Id)) return;
                RecordActivityJob(job, "Generating", item.At);
                break;
            case "tool.started":
            case "tool":
            {
                if (eventSessionId is null) break;
                var name = ActivityString(item.Data, "name") ?? "Tool";
                var sequence = item.Data.TryGetProperty("messageSequence", out var sequenceValue) && sequenceValue.TryGetInt32(out var number) ? number : (int?)null;
                var key = sequence is int actionSequence ? $"tool:{eventSessionId}:{actionSequence}" : $"live-tool:{item.JobId}:{name}";
                activityRows.TryGetValue(key, out var previous);
                var arguments = item.Data.TryGetProperty("arguments", out var value) ? value : default;
                var argumentsText = arguments.ValueKind == JsonValueKind.Object ? ActivityBound(Ui.Serialize(arguments)) : previous?.Arguments ?? "";
                var state = "Running";
                var resultText = "";
                if (item.Type == "tool" && item.Data.TryGetProperty("result", out var resultValue))
                {
                    var result = resultValue.Deserialize<ToolResult>(CerebralJson.Options);
                    state = result?.Success == true ? "Completed" : "Failed";
                    resultText = result is null ? "The service did not provide a readable result." :
                        (result.Truncated ? "The service bounded this result.\n\n" : "") + ActivityBound(result.Content);
                }
                if (item.JobId is not null) activityGeneratingJobs.Remove(item.JobId);
                var row = PutActivity(key, item.At, state, ActivityToolCaption(name, arguments), ActivityActor(job?.ProfileId, job?.Profile.Name),
                    ActivityToolBody(name, argumentsText, resultText, state));
                row.Arguments = argumentsText;
                break;
            }
            case "session.updated" when eventSessionId is not null:
            {
                var fresh = await Get<TaskSession>("sessions.get", new { id = eventSessionId });
                if (session?.Id != selectedId) return;
                RecordActivitySession(fresh, related);
                break;
            }
            case "interface.updated":
            {
                var request = item.Data.Deserialize<InterfaceRequest>(CerebralJson.Options);
                if (request is not null) RecordActivityPresentation(request, item.At);
                break;
            }
            case "question.updated":
            {
                var question = item.Data.Deserialize<UserQuestion>(CerebralJson.Options);
                if (question is null) break;
                PutActivity("question:" + question.Id, item.At, question.Answer is not null ? "Answered" : question.Required ? "Answer needed" : "Question",
                    question.Prompt, ActivityActor(job?.ProfileId, job?.Profile.Name), ActivityBound(question.Prompt + "\n\n" + question.Reason +
                        (question.Answer is not null ? "\n\n**Answer**\n" + question.Answer : "")));
                break;
            }
            case "context.budget":
            {
                var promptTokens = ActivityInt(item.Data, "promptTokens");
                var requested = ActivityInt(item.Data, "requestedResponseTokens");
                var available = ActivityInt(item.Data, "availableResponseTokens");
                if (promptTokens is null || requested is null || available is null) break;
                PutActivity("context-budget:" + (item.JobId ?? eventSessionId), item.At, "Adjusted", "Context allocation",
                    ActivityActor(job?.ProfileId, job?.Profile.Name),
                    $"Prompt and working context: {promptTokens.Value:N0} tokens\nRequested response allowance: {requested.Value:N0} tokens\nAvailable response allowance: {available.Value:N0} tokens\n\n" +
                    "The service shortened this response allowance to fit the loaded model's fixed context. Model memory and the loaded weights did not change.");
                break;
            }
            case "longform.updated":
            case "longform.progress":
            {
                if (eventSessionId is not null)
                    RecordLongFormActivity(eventSessionId, item.Data, item.At, item.JobId, related, item.Type == "longform.updated");
                break;
            }
            default: return;
        }
        if (item.Type is "tool" or "interface.updated") await RefreshActivityFailuresAsync(selectedId, related);
        RenderActivity();
    }

    private static int? ActivityInt(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private async Task RefreshLongFormActivityAsync(string selectedId, List<AgentJob> related, string? selectedRoot, int selectedTurn)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        try
        {
            // One bounded service-owned snapshot restores progress that event streaming cannot replay
            // after reconnect. Older services do not expose this command, so absence is an optional
            // capability rather than a connection failure in the conversation UI.
            var snapshot = await Get<JsonElement>("sessions.longform", new { id = selectedId });
            if (session?.Id != selectedId || activityRootJobId != selectedRoot || activityTurnSequence != selectedTurn) return;
            if (snapshot.ValueKind != JsonValueKind.Object) return;
            if (!snapshot.TryGetProperty("tracked", out var tracked) || tracked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            if (!tracked.GetBoolean())
            {
                RemoveUntrackedLongFormActivity(selectedId, requestedAt);
                return;
            }
            if (ActivityInt(snapshot, "targetWords") is not > 0) return;
            RecordLongFormActivity(selectedId, snapshot, requestedAt, ActivityString(snapshot, "sourceJobId"), related, false);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // The event-backed activity UI remains usable when this optional command is unavailable,
            // including while connected to a service version from before long-form diagnostics.
        }
    }

    private void RecordLongFormActivity(string sessionId, JsonElement data, DateTimeOffset fallbackAt,
        string? sourceJobId, List<AgentJob> related, bool planned)
    {
        var path = ActivityString(data, "path") ?? "saved artifact";
        var target = Math.Max(0, ActivityInt(data, "targetWords") ?? 0);
        var verified = Math.Max(0, ActivityInt(data, "verifiedWords") ?? 0);
        var reached = Math.Max(0, ActivityInt(data, "reachedAllocations") ?? ActivityInt(data, "currentSectionIndex") ?? 0);
        var allocationCount = Math.Max(0, ActivityInt(data, "allocationCount") ?? ActivityInt(data, "sectionCount") ?? 0);
        var revision = Math.Max(0, ActivityInt(data, "revision") ?? 0);
        var updatedAt = ActivityDate(data, "updatedAt") ?? fallbackAt;
        var key = LongFormActivityKey(sessionId);
        if (!AdvanceLongFormActivity(sessionId, sourceJobId, revision, updatedAt)) return;

        var complete = ActivityBool(data, "complete") ?? target > 0 && verified >= target;
        var pending = data.TryGetProperty("pendingAppend", out var pendingValue) && pendingValue.ValueKind == JsonValueKind.Object ? pendingValue : default;
        var hasPending = pending.ValueKind == JsonValueKind.Object;
        var active = related.Any(x => Active(x) && (x.SessionId == sessionId || x.ParentJobId is null && x.SessionId == session?.Id));
        var state = hasPending ? "Recovery pending" : complete ? "Completed" : active ? "Working" : planned || verified == 0 ? "Planned" : "Paused";
        var title = planned && verified == 0 ? $"Long-form plan · {target:N0} words" : $"Writing · {verified:N0} / {target:N0} words";
        var body = $"Artifact: {ActivityBound(path)}\nMeasured progress: {verified:N0} / {target:N0} words\n" +
            $"Word-allocation thresholds reached: {Math.Min(reached, allocationCount):N0} / {allocationCount:N0}";
        if (hasPending)
        {
            var step = Math.Max(0, ActivityInt(pending, "step") ?? 0);
            var characters = Math.Max(0, ActivityInt(pending, "contentCharacters") ?? 0);
            body += $"\nPending append recovery: step {step:N0}, {characters:N0} characters saved for reconciliation before writing continues.";
        }
        var sourceJob = related.FirstOrDefault(x => x.Id == sourceJobId);
        var actorProfile = sourceJob?.ProfileId;
        PutActivity(key, updatedAt, state, title, ActivityActor(actorProfile, sourceJob?.Profile.Name), body);
    }

    private bool AdvanceLongFormActivity(string sessionId, string? sourceJobId, int revision, DateTimeOffset updatedAt)
    {
        if (longFormActivityVersions.TryGetValue(sessionId, out var current))
        {
            if (updatedAt < current.UpdatedAt) return false;
            var sameWorkflow = sourceJobId is null || current.SourceJobId is null || string.Equals(sourceJobId, current.SourceJobId, StringComparison.Ordinal);
            if (updatedAt == current.UpdatedAt && sameWorkflow && revision < current.Revision) return false;
        }
        longFormActivityVersions[sessionId] = new(sourceJobId, revision, updatedAt);
        return true;
    }

    private void RemoveUntrackedLongFormActivity(string sessionId, DateTimeOffset requestedAt)
    {
        if (longFormActivityVersions.TryGetValue(sessionId, out var current) && current.UpdatedAt > requestedAt) return;
        activityRows.Remove(LongFormActivityKey(sessionId));
        longFormActivityVersions.Remove(sessionId);
    }

    private static string LongFormActivityKey(string sessionId) => "longform:" + sessionId;
    private static bool? ActivityBool(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static DateTimeOffset? ActivityDate(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at) ? at : null;

    private void RecordActivityJob(AgentJob job, string? activity = null, DateTimeOffset? at = null)
    {
        var state = activity ?? job.Status switch
        {
            "running" => "Working", "queued" => "Queued", "waiting-helper" => "Waiting for helpers",
            "waiting-human" => "Answer needed", "completed" => "Completed", "cancelled" => "Cancelled",
            "failed" => "Failed", "incomplete" => "Incomplete", "interrupted" => "Interrupted", _ => "Waiting"
        };
        var profile = job.Profile;
        var assignment = job.ParentJobId is null ? session?.Messages.LastOrDefault(x => x.Role == "user" && x.Origin == "user" && x.ToolName is null)?.Content ?? job.Prompt : job.Prompt;
        var body = $"**Assignment**\n{ActivityBound(assignment)}\n\n**Status**\n{state}" +
            (!string.IsNullOrWhiteSpace(job.WaitingReason) ? "\n\n" + ActivityBound(job.WaitingReason) : "") +
            $"\n\n**Execution**\nAgent: {ActivityActor(job.ProfileId, job.Profile.Name)}\nWorker: {job.WorkerId}\nModel: {profile.ModelId}\nSteps: {job.StepCount}" +
            (!string.IsNullOrEmpty(job.Error) ? "\n\n**Error**\n" + ActivityBound(job.Error) : "") +
            (!string.IsNullOrEmpty(job.Result) ? "\n\n**Result**\n" + ActivityBound(job.Result) : "");
        var title = job.Kind == "compact" ? "Context compaction" : job.ParentJobId is null ? "Cerebral assignment" : "Delegated assignment";
        var timestamp = at ?? job.FinishedAt ?? job.ExecutionStartedAt ?? job.CreatedAt;
        if (activityRows.TryGetValue("job:" + job.Id, out var existing) && existing.At > timestamp) timestamp = existing.At;
        PutActivity("job:" + job.Id, timestamp, state, title, ActivityActor(job.ProfileId, job.Profile.Name), body);
    }

    private void RecordActivitySession(TaskSession snapshot, List<AgentJob> related)
    {
        var history = snapshot.Messages.Where(x => snapshot.Id != session?.Id || x.Sequence >= activityTurnSequence).TakeLast(80).ToList();
        var actor = ActivityActor(snapshot.ProfileId);
        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            if (message.Role != "assistant" || message.ExcludedFromContext || !TryActivityAction(message.Content, out var action)) continue;
            var name = ActivityString(action, "name") ?? "Tool";
            var arguments = action.TryGetProperty("arguments", out var value) ? value : default;
            SessionMessage? result = null;
            for (var next = index + 1; next < history.Count; next++)
            {
                if (history[next].Role == "assistant" && TryActivityAction(history[next].Content, out _)) break;
                if (history[next].ToolName == name) { result = history[next]; break; }
            }
            // History has no exact call/receipt coordinates. Preserve it as legacy evidence,
            // never infer business completion from the next same-tool message or its prose.
            var state = result is null ? "Legacy call · no paired receipt" : "Legacy result · correlation unverified";
            var argumentsText = arguments.ValueKind == JsonValueKind.Object ? ActivityBound(Ui.Serialize(arguments)) : "";
            var row = PutActivity($"tool:{snapshot.Id}:{message.Sequence}", result?.At ?? message.At, state,
                ActivityToolCaption(name, arguments), actor,
                ActivityToolBody(name, argumentsText, result is null ? "" : ActivityBound(result.Content), state));
            row.Arguments = argumentsText;
        }
    }

    private ActivityRow PutActivity(string key, DateTimeOffset at, string state, string title, string actor, string body)
    {
        if (!activityRows.TryGetValue(key, out var row))
        {
            row = new ActivityRow(key); activityRows.Add(key, row);
            row.Expander.Expanded += (_, _) => RenderActivityDetails(row);
        }
        row.At = at; row.State = state; row.Title = title; row.Actor = actor;
        if (row.Body != body)
        {
            row.Body = body;
            if (row.Expander.IsExpanded) RenderActivityDetails(row);
        }
        row.TitleText.Text = $"{actor} · {state} · {title}";
        row.SubtitleText.Text = $"{actor} · {at.LocalDateTime:t}";
        row.TitleText.Foreground = state is "Failed" or "Unavailable" or "No recorded result" or "Needs attention" or "Incomplete" or "Interrupted" ? Ui.ErrorBorder :
            state is "Running" or "Generating" or "Working" ? Ui.Accent : Ui.Ink;
        row.Expander.ToolTip = title;
        return row;
    }

    private void RenderActivityDetails(ActivityRow row)
    {
        var details = new StackPanel();
        details.Children.Add(MarkdownView.Render(row.Body, error: error => SetStatus(error, true)));
        if (row.FailureId is string failureId)
        {
            var resolve = Ui.Button("Mark resolved", () => Run(async () =>
            {
                await Command("failures.resolve", new { id = failureId, note = "Marked resolved by the user in this conversation." });
                await RefreshActivityAsync();
            }));
            AutomationProperties.SetAutomationId(resolve, "ResolveFailure-" + failureId); details.Children.Add(resolve);
        }
        row.Expander.Content = new Border { Padding = new Thickness(5, 6, 4, 10), Child = details };
    }

    private void RenderActivity()
    {
        EnsureActivitySession();
        if (session is null)
        {
            activityList.Children.Clear();
            activitySummary.Text = "Recent commands and helper work appear here.";
            return;
        }
        if (turnActivity.TryGetValue(TurnKey(session.Id, activityTurnSequence), out var turn) && turn.Count > 0)
        {
            // The inline journal is authoritative for tool operations; keep the right pane a recent view.
            foreach (var key in activityRows.Keys.Where(x => x.StartsWith("tool:", StringComparison.Ordinal) || x.StartsWith("live-tool:", StringComparison.Ordinal)).ToArray())
                activityRows.Remove(key);
            var recent = turn.Entries.Values.OrderByDescending(x => x.UpdatedAt).Take(ActivityLimit).ToList();
            if (turn.Latest is { } latest && !recent.Any(x => x.Id == latest.Id)) recent.Add(latest);
            foreach (var entry in recent)
            {
                var result = entry.Result.Length == 0 && entry.State != "running" ? "Expand the conversation activity to load the saved details." : entry.Result;
                var row = PutActivity("recorded:" + entry.Id, entry.UpdatedAt, TurnStateLabel(entry.State), entry.Title,
                    ActivityActor(entry.ProfileId), ActivityToolBody(entry.ToolName, entry.Arguments, result, TurnStateLabel(entry.State)));
                row.Arguments = entry.Arguments;
            }
        }
        var rows = activityRows.Values.OrderByDescending(x => x.At).Take(ActivityLimit).ToList();
        var keep = rows.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in activityRows.Keys.Where(key => !keep.Contains(key)).ToArray()) activityRows.Remove(key);
        // Reuse the existing expanders to retain expansion, focus, and selected result text.
        var controls = rows.Select(x => x.Expander).ToList();
        for (var index = activityList.Children.Count - 1; index >= 0; index--)
            if (activityList.Children[index] is not Expander control || !controls.Contains(control)) activityList.Children.RemoveAt(index);
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            if (index < activityList.Children.Count && ReferenceEquals(activityList.Children[index], control)) continue;
            activityList.Children.Remove(control); activityList.Children.Insert(index, control);
        }
        activitySummary.Text = rows.Count == 0 ? "Activity starts with your next message." : activityFrozen ? "Turn finished · activity is paused" : "This message only · expand for details";
    }

    private async Task RefreshActivityPresentationsAsync(string selectedId, List<AgentJob> related)
    {
        if (activityRootJobId is null || !related.Any(x => x.Id == activityRootJobId)) return;
        var expectedRoot = activityRootJobId; var expectedTurn = activityTurnSequence;
        var requests = await Get<List<InterfaceRequest>>("interface.list", new { sessionId = selectedId, limit = ActivityLimit });
        if (session?.Id != selectedId || activityRootJobId != expectedRoot || activityTurnSequence != expectedTurn) return;
        var jobIds = related.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var request in requests.Where(x => jobIds.Contains(x.JobId))) RecordActivityPresentation(request);
    }

    private void RecordActivityPresentation(InterfaceRequest request, DateTimeOffset? observedAt = null)
    {
        // Interface completion only reports that presentation was attempted successfully.
        // A process outcome must come from the service's actual observation fields.
        var state = request.Kind == "application" ? request.ProcessStatus ?? (request.Status is "failed" or "cancelled" ? "Presentation " + request.Status : "Awaiting process observation") : request.Status;
        var actor = ActivityActor(request.ProfileId);
        var body = "**Path**\n" + request.Path + "\n\n**Interface request**\n" + request.Status +
            "\nRequested: " + request.CreatedAt.ToString("O") +
            (request.Kind == "application" ? "\n\n**Observed process state**\n" + (request.ProcessStatus ?? "No process observation has been recorded.") : "") +
            (request.ProcessId is int processId ? "\nProcess ID: " + processId : "") +
            (request.ProcessStartedAt is { } started ? "\nProcess start: " + started.ToString("O") : "") +
            (request.ExitCode is int exitCode ? "\nExit code: " + exitCode : "") +
            (request.StopRequested ? "\nStop has been requested." : "") +
            (!string.IsNullOrEmpty(request.Error) ? "\n\n**Error**\n" + ActivityBound(request.Error) : "") +
            (!string.IsNullOrEmpty(request.Output) ? "\n\n**Output**\n" + ActivityBound(request.Output) : "") +
            (!string.IsNullOrEmpty(request.Result) ? "\n\n**Presentation receipt**\n" + ActivityBound(request.Result) : "");
        var key = "interface:" + request.Id;
        var at = observedAt ?? (activityRows.TryGetValue(key, out var existing) ? existing.At : request.CreatedAt);
        var row = PutActivity(key, at, state, request.Kind, actor, body);
        if (observedAt is null) row.SubtitleText.Text = $"{actor} · requested {request.CreatedAt.LocalDateTime:t}";
    }

    private async Task RefreshActivityFailuresAsync(string selectedId, List<AgentJob> related)
    {
        if (!related.Any(x => x.Id == activityRootJobId)) return;
        var expectedTurn = activityTurnSequence;
        var failures = await Get<List<TaskFailure>>("failures.list", new { sessionId = selectedId });
        if (session?.Id != selectedId || activityTurnSequence != expectedTurn) return;
        var turnAt = session.Messages.FirstOrDefault(x => x.Sequence == expectedTurn)?.At ?? DateTimeOffset.MinValue;
        foreach (var failure in failures.Where(x => x.CreatedAt >= turnAt && related.Any(job => job.Id == x.JobId))) RecordActivityFailure(failure);
    }

    private void RecordActivityFailure(TaskFailure failure)
    {
        var job = activeJobs.FirstOrDefault(x => x.Id == failure.JobId);
        var row = PutActivity("failure:" + failure.Id, failure.CreatedAt, failure.Status == "resolved" ? "Resolved" : "Needs attention",
            failure.Source, ActivityActor(job?.ProfileId, job?.Profile.Name), "**" + failure.Source + "**\n\n" + ActivityBound(failure.Message));
        row.FailureId = failure.Status == "resolved" ? null : failure.Id;
        if (row.Expander.IsExpanded) RenderActivityDetails(row);
        if (job is not null && session is not null && JobsForSelectedConversation(job))
        {
            var turn = TurnView(session.Id, ActivityTurnForJob(job, failure.CreatedAt));
            turn.Failures[failure.Id] = failure;
            UpdateTurnActivity(turn);
        }
    }

    private string ActivityActor(string? profileId, string? recordedName = null)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return "Agent not recorded";
        if (string.IsNullOrWhiteSpace(recordedName))
        {
            // Legacy jobs carry frozen execution profiles. Conflicting snapshots
            // cannot be replaced with today's editable profile name.
            var names = RelatedActivityJobs().Where(job => job.ProfileId == profileId)
                .Select(job => job.Profile.Name).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (names.Length != 1) return "Agent not recorded";
            recordedName = names[0];
        }
        return recordedName.Length > 128 || recordedName.Any(char.IsControl)
            ? "Agent not recorded" : recordedName.Trim();
    }
    private static string ActivityBound(string text) => text.Length <= ActivityTextLimit ? text : text[..ActivityTextLimit] + "\n\n[Activity display limited; the saved conversation contains the service result.]";
    private static string? ActivityString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool TryActivityAction(string content, out JsonElement action)
    {
        action = default;
        if (!TryParseActivityJson(content, out var parsed) || ActivityString(parsed, "action") != "tool") return false;
        action = parsed; return true;
    }
    private static string ActivityToolCaption(string name, JsonElement arguments)
    {
        var operation = WorkflowWorkspace.DescribeRecordedActivity(name, arguments);
        return operation.Action + " · [" + operation.Tool + "]";
    }
    private static string ActivityToolBody(string name, string arguments, string result, string state)
    {
        var readableArguments = ReadableActivityJson(arguments);
        var readableResult = ReadableActivityJson(result);
        return $"**{name}**\n{state}\n\n" + (readableArguments.Length > 0 ? "**Arguments**\n" + readableArguments + "\n\n" : "") +
            (readableResult.Length > 0 ? "**Result**\n" + readableResult : state == "Running" ? "The service started this tool; no result has been reported yet." : "No result is recorded for this action.");
    }

    private sealed class ActivityRow
    {
        public string Key { get; }
        public DateTimeOffset At { get; set; }
        public string State { get; set; } = "";
        public string Title { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Body { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string? FailureId { get; set; }
        public TextBlock TitleText { get; } = Ui.Text("", 12);
        public TextBlock SubtitleText { get; } = Ui.Text("", 10, Ui.Muted);
        public Expander Expander { get; }
        public ActivityRow(string key)
        {
            Key = key;
            TitleText.TextWrapping = TextWrapping.NoWrap; TitleText.TextTrimming = TextTrimming.CharacterEllipsis; TitleText.Margin = new Thickness(0, 0, 0, 2);
            SubtitleText.TextWrapping = TextWrapping.NoWrap; SubtitleText.TextTrimming = TextTrimming.CharacterEllipsis; SubtitleText.Margin = new Thickness(0);
            var heading = new StackPanel { Margin = new Thickness(0, 4, 0, 4) }; heading.Children.Add(TitleText); heading.Children.Add(SubtitleText);
            Expander = new Expander
            {
                Header = heading, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 3)
            };
            AutomationProperties.SetAutomationId(Expander, "Activity-" + key);
        }
    }

    private sealed record LongFormActivityVersion(string? SourceJobId, int Revision, DateTimeOffset UpdatedAt);
}
