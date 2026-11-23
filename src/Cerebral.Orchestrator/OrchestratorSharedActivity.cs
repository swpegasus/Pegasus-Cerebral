using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Desktop;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private ActivityTimelineView? sharedActivity;
    private Expander? sharedActivityPanel;
    private bool sharedActivityQueued;
    private long sharedActivityOwnerGeneration;
    private bool sharedActivityClosed;
    private TurnActivityView? focusedLegacyActivity;
    private SharedActivityMembersReply? sharedActivityMembers;
    private readonly Dictionary<string, SharedActivityMemberRead> sharedActivityReads = new(StringComparer.Ordinal);
    private bool sharedActivityReading;
    private CancellationTokenSource? sharedActivityReadCancellation;
    private long sharedActivityReadVersion;
    private long sharedActivityPageOrder;
    private int sharedActivityOmittedEvents;
    private string? sharedActivityIssue;
    private DateTimeOffset sharedActivityRosterReadAt;
    private const int MaximumActivityPagesPerRead = 8;
    private const int MaximumActivityEvents = 2000;

    // Local wire projections mirror the versioned owner-only view contract. They
    // deliberately do not expose Worker implementation types or storage ownership.
    private sealed record SharedActivityMember(string ContextId, string ConversationId, string Role, string DisplayName);
    private sealed record SharedActivityMembersReply(int SchemaVersion, string MainConversationId, long RootRevision,
        bool AdmissionClosed, IReadOnlyList<SharedActivityMember> Members);
    private sealed record SharedActivityCursor(string MainConversationId, string ContextId, long RootRevision,
        long AfterSequence, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SnapshotSequence);
    private sealed record SharedActivityPage(int SchemaVersion, string MainConversationId, string ContextId,
        long RootRevision, IReadOnlyList<CerebralEvent> Events, long SnapshotSequence,
        JsonElement? RetentionGap, SharedActivityCursor? NextCursor, int ScannedEvents,
        SharedActivityCursor? TailCursor = null);
    private sealed class SharedActivityMemberRead(SharedActivityMember member)
    {
        internal SharedActivityMember Member { get; } = member;
        internal Dictionary<string, CerebralEvent> Events { get; } = new(StringComparer.Ordinal);
        internal SharedActivityCursor? Cursor { get; set; }
        internal bool Started { get; set; }
        internal bool Complete { get; set; }
        internal bool Refreshing { get; set; }
        internal bool RetentionGap { get; set; }
        internal long PageOrder { get; set; }
    }
    private string SharedActivityOwner => ServiceConnectionIdentity + ":" + sharedActivityOwnerGeneration + ":" +
        (durableConversationId ?? session?.Id ?? "none");

    private const string ActivityWorkPanelId = "activity";
    private bool SharedActivityPageSelected => pages.TryGetValue("activity", out var page) && ReferenceEquals(Tabs.SelectedItem, page);

    private void OpenEmbeddedActivity()
    {
        if (sharedActivityPanel is null) return;
        sharedActivityPanel.IsExpanded = true;
        SelectPage("activity");
        QueueSharedActivityUpdate();
    }

    private void OpenTurnActivityPage(WorkflowWorkspace workspace, ActivityTurnTarget target)
    {
        if (!IsConnected || sharedActivity is null || workspace.Snapshot?.ConversationId != target.ConversationId ||
            string.IsNullOrWhiteSpace(ServiceConnectionIdentity)) return;
        // The emitter is an owner-bound Main/team presenter. Navigation changes only the named
        // Activity page and its read filter; it never selects a chat or rewrites the composer draft.
        RefreshSharedActivity();
        ShowFocusedActivityPage(new(SharedActivityOwner, target.ConversationId, target.RunId)
            { FirstSequence = target.FirstSequence, LastSequence = target.LastSequence });
    }

    private void ShowFocusedActivityPage(ActivityTurnFocus focus)
    {
        sharedActivity!.FocusTurn(focus);
        SelectPage("activity");
    }

    private void OpenLegacyTurnActivityPage(TurnActivityView view)
    {
        if (!IsConnected || session?.Id != view.SessionId || sharedActivityPanel is null ||
            !turnActivity.TryGetValue(TurnKey(view.SessionId, view.TurnSequence), out var current) ||
            !ReferenceEquals(current, view)) return;
        focusedLegacyActivity = view;
        sharedActivityPanel.Content = view.Expander;
        view.Expander.IsExpanded = true;
        UpdateTurnActivity(view);
        SelectPage("activity");
    }

    private void InitializeSharedActivity()
    {
        sharedActivity = new ActivityTimelineView();
        sharedActivity.LoadEarlierRequested += (_, _) => Run(LoadEarlierSharedActivityAsync);
        sharedActivityPanel = new Expander
        {
            Header = "Activity",
            Content = sharedActivity,
            IsExpanded = true,
            Margin = new Thickness(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        AutomationProperties.SetAutomationId(sharedActivityPanel, "SharedActivityWorkspace");
        AutomationProperties.SetName(sharedActivityPanel, "Activity for the selected chat and its linked agent contexts");
        AddPage("activity", "Activity", sharedActivityPanel);
        Closed += (_, _) =>
        {
            sharedActivityClosed = true;
            sharedActivityReadCancellation?.Cancel();
            sharedActivity?.InvalidateOwner();
        };
        QueueSharedActivityUpdate();
    }

    private void QueueSharedActivityUpdate()
    {
        if (sharedActivity is null || sharedActivityQueued || sharedActivityClosed) return;
        sharedActivityQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            sharedActivityQueued = false;
            RefreshSharedActivity();
        }, DispatcherPriority.Background);
    }

    private void ClearSharedActivity(bool ownerChanged = false)
    {
        if (ownerChanged)
        {
            sharedActivityReadCancellation?.Cancel(); sharedActivityReadCancellation = null;
            sharedActivityOwnerGeneration++; sharedActivityReadVersion++;
            sharedActivityMembers = null; sharedActivityReads.Clear(); sharedActivityReading = false;
            sharedActivityOmittedEvents = 0; sharedActivityIssue = null;
            sharedActivityRosterReadAt = DateTimeOffset.MinValue;
        }
        focusedLegacyActivity = null;
        if (sharedActivity is null) return;
        if (sharedActivityPanel is not null) sharedActivityPanel.Content = sharedActivity;
        if (ownerChanged) sharedActivity.InvalidateOwner();
        sharedActivity.Update(new([], false, CoverageNote: ownerChanged
            ? "The connection changed." : "No saved run is selected."), null, null);
        // Its one embedded panel remains available with an honest empty state.
    }

    private void RefreshSharedActivity()
    {
        if (sharedActivity is null || sharedActivityPanel is null || sharedActivityClosed) return;
        if (IsConnected && session?.Id is { } legacyId && focusedLegacyActivity is { } legacy &&
            legacy.SessionId == legacyId && turnActivity.TryGetValue(TurnKey(legacyId, legacy.TurnSequence), out var legacyCurrent) &&
            ReferenceEquals(legacyCurrent, legacy))
        {
            sharedActivityPanel.Content = legacy.Expander;
            UpdateTurnActivity(legacy);
            return;
        }
        if (!IsConnected || !IsNativeChat || nativeChat?.Snapshot is not { } main ||
            main.ConversationId != durableConversationId || string.IsNullOrWhiteSpace(ServiceConnectionIdentity))
        {
            ClearSharedActivity();
            return;
        }
        focusedLegacyActivity = null;
        sharedActivityPanel.Content = sharedActivity;
        RenderSharedActivity(main.ConversationId);
        if (SharedActivityPageSelected && !sharedActivityReading && sharedActivityIssue is null &&
            DateTimeOffset.UtcNow - sharedActivityRosterReadAt >= TimeSpan.FromSeconds(3))
            // This reader owns its own bounded in-flight gate. Background reads
            // must not occupy the window's gate for Send and other user actions.
            _ = ReadSharedActivityAsync(loadMore: sharedActivityMembers is null);
    }

    private void RenderSharedActivity(string mainId)
    {
        if (sharedActivity is null || sharedActivityPanel is null) return;
        var roster = sharedActivityMembers;
        if (roster is null || roster.MainConversationId != mainId)
        {
            sharedActivity.Update(new([], true, CoverageNote: sharedActivityIssue ??
                "Loading this chat's owned agent contexts."), mainId, teamTaskId);
            return;
        }
        var sources = new List<ActivityTimelineSnapshot>();
        foreach (var member in roster.Members)
        {
            var events = sharedActivityReads[member.ContextId].Events.Values.ToDictionary(item => item.Id, StringComparer.Ordinal);
            void Include(IReadOnlyList<CerebralEvent> loaded)
            {
                foreach (var item in loaded.Where(item => item.ConversationId == member.ConversationId &&
                             item.Projection.Human == HumanVisibility.Activity))
                    if (!events.TryGetValue(item.Id, out var prior) || SameActivityEventCoordinates(prior, item))
                        events[item.Id] = item;
            }
            if (member.Role == "main" && nativeChat?.Snapshot is { } main && main.ConversationId == mainId)
                Include(main.Events);
            if (teamPresenterConnection == ServiceConnectionIdentity && ReferenceEquals(teamPresenterClient, Client))
                foreach (var presenter in teamPresenters.Values)
                    if (presenter.Snapshot is { } snapshot && snapshot.ConversationId == member.ConversationId)
                        Include(snapshot.Events);
            foreach (var turn in events.Values.GroupBy(item => item.TurnId, StringComparer.Ordinal))
            {
                var taskId = teamGraph is { Complete: true } graph && graph.SourceConversationId == mainId &&
                    TeamRun(turn.Key)?.ConversationId == member.ConversationId ? graph.TaskId : null;
                var role = member.Role switch { "main" => ActivityTimelineRole.Main,
                    "coordinator" => ActivityTimelineRole.Coordinator, _ => ActivityTimelineRole.Worker };
                sources.Add(WorkflowWorkspace.ProjectActivityTimelineEvents(turn.OrderBy(item => item.Sequence).ToArray(),
                    new(SharedActivityOwner, role, taskId)
                    {
                        OwningMainConversationId = mainId, MemberCoordinate = member.ContextId,
                        ContextAttribution = new(SharedActivityOwner, mainId, member.ContextId,
                            member.ConversationId, turn.Key, role) { DisplayLabel = member.DisplayName },
                        ClassifyOperations = true
                    }));
            }
        }
        var all = sources.SelectMany(source => source.Items)
            .OrderBy(item => item.At).ThenBy(item => item.Sequence).ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var items = all.TakeLast(ActivityTimelineSnapshot.MaximumItems).ToArray();
        var remaining = sharedActivityReads.Values.Count(read => !read.Complete);
        var omitted = sources.Sum(source => (long)source.OmittedCount) + all.Length - items.Length;
        var coverage = $"{roster.Members.Count} owned contexts · {remaining} have unread source pages. " +
            "Opened presenters also supply current activity. Source pages use fixed journal snapshots; completed contexts refresh from their server bookmark." +
            (sharedActivityReads.Values.Any(read => read.RetentionGap) ? " The journal reports a retention gap." : "") +
            (sharedActivityOmittedEvents > 0 ? $" {sharedActivityOmittedEvents} older source events exceeded this window's bound." : "") +
            (sharedActivityIssue is { } issue ? " " + issue : "");
        sharedActivity.Update(new(items, remaining > 0 || sharedActivityIssue is not null,
            (int)Math.Min(int.MaxValue, omitted), coverage)
        { Members = roster.Members.Select(member => new ActivityTimelineMember(mainId, member.ContextId, member.DisplayName)).ToArray() },
            mainId, teamTaskId);
        sharedActivityPanel.Visibility = Visibility.Visible;
    }

    private async Task LoadEarlierSharedActivityAsync()
    {
        await ReadSharedActivityAsync(loadMore: true);
    }

    private async Task ReadSharedActivityAsync(bool loadMore)
    {
        if (sharedActivityReading || sharedActivityClosed || !IsConnected || !IsNativeChat ||
            durableConversationId is not { } mainId || nativeChat?.Snapshot?.ConversationId != mainId || sharedActivity is null) return;
        var owner = SharedActivityOwner;
        var client = Client;
        var selected = conversationSelectionVersion;
        var version = ++sharedActivityReadVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        sharedActivityReadCancellation = cancellation;
        sharedActivityReading = true;
        bool Current() => !sharedActivityClosed && IsConnected && ReferenceEquals(client, Client) &&
            owner == SharedActivityOwner && mainId == durableConversationId && selected == conversationSelectionVersion &&
            version == sharedActivityReadVersion;
        try
        {
            var response = await client.CommandAsync("collaboration.view.members", new { mainConversationId = mainId },
                cancellationToken: cancellation.Token);
            if (!Current()) return;
            if (!response.Success) throw new InvalidDataException("The owned context roster is unavailable.");
            var roster = CerebralJson.Read<SharedActivityMembersReply>(response.Data);
            ValidateSharedActivityMembers(roster, mainId);
            var prior = sharedActivityMembers;
            if (prior is null || prior.RootRevision != roster.RootRevision)
            {
                sharedActivityReads.Clear(); sharedActivityOmittedEvents = 0;
                foreach (var member in roster.Members) sharedActivityReads.Add(member.ContextId, new(member));
                if (prior is not null) sharedActivity.InvalidateOwner();
                loadMore = true;
            }
            else if (!prior.Members.SequenceEqual(roster.Members))
                throw new InvalidDataException("The context roster changed without a new revision.");
            sharedActivityMembers = roster; sharedActivityIssue = null;
            sharedActivityRosterReadAt = DateTimeOffset.UtcNow;
            RenderSharedActivity(mainId);
            var filterVersion = sharedActivity.FilterVersion;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < MaximumActivityPagesPerRead; index++)
            {
                var focused = sharedActivity.FocusedTurn?.ConversationId;
                var selectedMember = sharedActivity.SelectedMemberCoordinate;
                var read = sharedActivityReads.Values.Where(state => state.Complete
                        ? state.Cursor is not null && !visited.Contains(state.Member.ContextId)
                        : loadMore || state.Refreshing)
                    .OrderBy(state => selectedMember is not null && state.Member.ContextId == selectedMember ||
                        focused is not null && state.Member.ConversationId == focused ? 0 : 1)
                    .ThenBy(state => state.PageOrder).FirstOrDefault();
                if (read is null) break;
                var cursor = read.Cursor;
                var refreshing = read.Refreshing || read.Complete;
                response = await client.CommandAsync("collaboration.view.activity", new
                { mainConversationId = mainId, contextId = read.Member.ContextId, limit = 100, cursor },
                    cancellationToken: cancellation.Token);
                if (!Current() || filterVersion != sharedActivity.FilterVersion || !ReferenceEquals(sharedActivityMembers, roster)) return;
                if (!response.Success) throw new InvalidDataException("An owned activity page is unavailable. Refresh the chat if its context membership changed.");
                var page = CerebralJson.Read<SharedActivityPage>(response.Data);
                if (page is null) throw new InvalidDataException("The activity page is empty.");
                if (page.RootRevision != roster.RootRevision)
                {
                    // A real membership transition invalidates every cursor and
                    // retained detail before a single fresh roster read is queued.
                    sharedActivityMembers = null; sharedActivityReads.Clear();
                    sharedActivityRosterReadAt = DateTimeOffset.MinValue;
                    sharedActivity.InvalidateOwner(); RenderSharedActivity(mainId);
                    return;
                }
                ValidateSharedActivityPage(page, roster, read.Member, cursor);
                foreach (var item in page.Events)
                {
                    if (read.Events.TryGetValue(item.Id, out var previous) && !SameActivityEventCoordinates(previous, item))
                        throw new InvalidDataException("An activity event identity was retargeted.");
                    read.Events[item.Id] = item;
                }
                read.Started = true; read.Cursor = page.NextCursor ?? page.TailCursor;
                read.Complete = page.NextCursor is null; read.Refreshing = refreshing && !read.Complete;
                visited.Add(read.Member.ContextId);
                read.RetentionGap |= page.RetentionGap is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
                read.PageOrder = ++sharedActivityPageOrder;
                TrimSharedActivityEvents();
                RenderSharedActivity(mainId);
            }
        }
        catch (Exception) when (!Current()) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            sharedActivityIssue = "Activity source read is unavailable; the table contains only verified loaded evidence. Use Load more to retry.";
            RenderSharedActivity(mainId);
        }
        finally
        {
            if (version == sharedActivityReadVersion) sharedActivityReading = false;
            if (ReferenceEquals(sharedActivityReadCancellation, cancellation)) sharedActivityReadCancellation = null;
            if (Current() && sharedActivityMembers is null && sharedActivityIssue is null) QueueSharedActivityUpdate();
        }
    }

    private void TrimSharedActivityEvents()
    {
        var excess = sharedActivityReads.Values.SelectMany(read => read.Events.Values.Select(item => (Read: read, Event: item)))
            .OrderByDescending(pair => pair.Event.CreatedAt).ThenByDescending(pair => pair.Event.Sequence)
            .Skip(MaximumActivityEvents).ToArray();
        foreach (var pair in excess) pair.Read.Events.Remove(pair.Event.Id);
        sharedActivityOmittedEvents = (int)Math.Min(int.MaxValue, (long)sharedActivityOmittedEvents + excess.Length);
    }

    private static bool SameActivityEventCoordinates(CerebralEvent left, CerebralEvent right) =>
        left.Id == right.Id && left.ConversationId == right.ConversationId && left.TurnId == right.TurnId && left.Sequence == right.Sequence;

    private static void ValidateSharedActivityMembers(SharedActivityMembersReply roster, string mainId)
    {
        static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
        if (roster is null || roster.SchemaVersion != 1 || roster.MainConversationId != mainId || roster.RootRevision < 0 ||
            roster.Members is null || roster.Members.Count is < 1 or > 65 || roster.Members.Any(member => member is null ||
                !Id(member.ContextId) || !Id(member.ConversationId) || member.Role is not ("main" or "coordinator" or "worker") ||
                string.IsNullOrWhiteSpace(member.DisplayName) || member.DisplayName.Length > 512 || member.DisplayName.Any(char.IsControl)) ||
            roster.Members.Select(member => member.ContextId).Distinct(StringComparer.Ordinal).Count() != roster.Members.Count ||
            roster.Members.Select(member => member.ConversationId).Distinct(StringComparer.Ordinal).Count() != roster.Members.Count ||
            roster.Members.Count(member => member.Role == "main") != 1 ||
            !roster.Members.Any(member => member.Role == "main" && member.ContextId == mainId && member.ConversationId == mainId))
            throw new InvalidDataException("The owned context roster is invalid.");
    }

    private static void ValidateSharedActivityPage(SharedActivityPage page, SharedActivityMembersReply roster,
        SharedActivityMember member, SharedActivityCursor? prior)
    {
        if (page is null || page.SchemaVersion != 1 || page.MainConversationId != roster.MainConversationId || page.ContextId != member.ContextId ||
            page.RootRevision != roster.RootRevision || page.SnapshotSequence < 0 || page.ScannedEvents is < 0 or > 100 ||
            page.Events is null || page.Events.Count > page.ScannedEvents ||
            prior is { SnapshotSequence: { } pinned } && page.SnapshotSequence != pinned ||
            prior is not null && (prior.MainConversationId != roster.MainConversationId || prior.ContextId != member.ContextId ||
                prior.RootRevision != roster.RootRevision || prior.AfterSequence < 0 || prior.AfterSequence > page.SnapshotSequence) ||
            page.Events.Any(item => item is null || item.ConversationId != member.ConversationId ||
                string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.TurnId) ||
                item.Actor is null || item.Provenance is null || item.Projection is null || item.Content is null ||
                item.Projection.Human != HumanVisibility.Activity || item.Sequence <= (prior?.AfterSequence ?? 0) || item.Sequence > page.SnapshotSequence) ||
            page.Events.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != page.Events.Count ||
            page.NextCursor is { } next && (next.MainConversationId != roster.MainConversationId || next.ContextId != member.ContextId ||
                next.RootRevision != roster.RootRevision || next.SnapshotSequence != page.SnapshotSequence ||
                next.AfterSequence <= (prior?.AfterSequence ?? 0) || next.AfterSequence > page.SnapshotSequence ||
                page.Events.Any(item => item.Sequence > next.AfterSequence)) ||
            page.NextCursor is null && page.TailCursor is null || page.NextCursor is not null && page.TailCursor is not null ||
            page.TailCursor is { } tail && (tail.MainConversationId != roster.MainConversationId || tail.ContextId != member.ContextId ||
                tail.RootRevision != roster.RootRevision || tail.AfterSequence != page.SnapshotSequence || tail.SnapshotSequence is not null))
            throw new InvalidDataException("The activity page is outside the exact root, member or journal snapshot.");
    }
}
