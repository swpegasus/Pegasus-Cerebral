using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    internal sealed record ActivityRow(string Key, CerebralEvent Event, CerebralEvent? Call = null)
    {
        internal long FirstSequence => Call?.Sequence ?? Event.Sequence;
        internal long LastSequence => Math.Max(FirstSequence, Event.Sequence);
    }

    private readonly record struct ActivityOperationIdentity(string ConversationId, string TurnId, string CallId, string ToolName);

    /// <summary>
    /// Pairs one recorded dispatch and its first terminal receipt. Tool names, turns and conversations
    /// are part of the identity; an orphan or additional receipt remains visible as its own evidence.
    /// This projection does not rewrite the durable journal or model-visible context.
    /// </summary>
    internal static IReadOnlyList<ActivityRow> ProjectActivityRows(IEnumerable<CerebralEvent> events)
    {
        var rows = new List<ActivityRow>();
        var pending = new Dictionary<ActivityOperationIdentity, Queue<int>>();
        foreach (var item in events.OrderBy(item => item.Sequence))
        {
            if (!ShowActivityEntry(item)) continue;
            if (TryActivityOperationIdentity(item, out var identity))
            {
                if (item.Type == CerebralEventTypes.ToolCall)
                {
                    if (!pending.TryGetValue(identity, out var candidates)) pending[identity] = candidates = new();
                    candidates.Enqueue(rows.Count);
                }
                else if (pending.TryGetValue(identity, out var candidates) && candidates.TryPeek(out var index) &&
                    CanPairActivityReceipt(rows[index].Event, item, identity.ToolName))
                {
                    candidates.Dequeue();
                    var call = rows[index].Event;
                    rows[index] = new(call.Id, item, call);
                    continue;
                }
            }
            rows.Add(new(item.Id, item));
        }
        return rows;
    }

    private static bool CanPairActivityReceipt(CerebralEvent call, CerebralEvent receipt, string tool) =>
        (call.Actor.Kind is EventActorKind.Agent or EventActorKind.Service or EventActorKind.Plugin or EventActorKind.System) &&
        receipt.Actor.Kind == EventActorKind.Tool && (receipt.Actor.Id is null || receipt.Actor.Id == tool) &&
        (call.Provenance.JobId is null || receipt.Provenance.JobId is null || call.Provenance.JobId == receipt.Provenance.JobId) &&
        (call.Provenance.AgentId is null || receipt.Provenance.AgentId is null || call.Provenance.AgentId == receipt.Provenance.AgentId) &&
        (call.Actor.Kind != EventActorKind.Agent || call.Actor.Id is null || receipt.Provenance.AgentId is null ||
            call.Actor.Id == receipt.Provenance.AgentId);

    private static bool TryActivityOperationIdentity(CerebralEvent item, out ActivityOperationIdentity identity)
    {
        identity = default;
        if (item.Type is not (CerebralEventTypes.ToolCall or CerebralEventTypes.ToolResult) ||
            item.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("callId", out var callId) || callId.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("toolName", out var toolName) || toolName.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(callId.GetString()) || string.IsNullOrWhiteSpace(toolName.GetString())) return false;
        identity = new(item.ConversationId, item.TurnId, callId.GetString()!, toolName.GetString()!);
        return true;
    }

    internal static bool ActivityRowFailed(ActivityRow row)
    {
        var state = ActivityReceiptState(row.Event);
        // An uncertain/cancelled effect is a distinct recorded outcome, even when the
        // generic success flag is false. It must not be presented as a confirmed failure.
        return state is not ("Unknown" or "Cancelled" or "Prepared" or "Dispatched") &&
            (state == "Failed" || ToolResult(row.Event).Success == false);
    }

    private static string? ActivityReceiptState(CerebralEvent item)
    {
        var receipt = item.Content.OfType<StructuredContentPart>()
            .FirstOrDefault(part => part.Schema == "workflow.command.result")?.Value;
        return receipt is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                ? state.GetString() : null;
    }

    internal static string ActivityRowCaption(ActivityRow row, WorkflowConversationRun run,
        IReadOnlyList<CerebralEvent> items, IReadOnlyDictionary<string, string> profiles, DateTimeOffset? measuredAt = null,
        bool currentIteration = true)
    {
        if (row.Call is { } call && TryToolCall(call, out _, out var label))
        {
            var result = ToolResult(row.Event);
            var state = GenerationLimitState(row.Event) ?? ActivityResultState(row.Event);
            return ActivityOriginPrefix(call, profiles) + state + " · " + label +
                (result.DurationMilliseconds is > 0 ? " · " + FormatCallElapsed(result.DurationMilliseconds.Value) : "");
        }
        if (TryToolCall(row.Event, out _, out var pendingLabel))
        {
            var current = currentIteration && row.Event.TurnId == run.Id;
            var working = current && ActivityRunExecuting(run);
            var state = working ? ActivityRecoveryState(row, items) ?? "Running" :
                current && run.EffectState == WorkflowEffectState.Unknown ? "Outcome unconfirmed" : "Awaiting receipt";
            var end = working ? measuredAt ?? DateTimeOffset.UtcNow :
                items.LastOrDefault(item => item.Type == CerebralEventTypes.WorkflowTransition &&
                    item.TurnId == row.Event.TurnId && item.CreatedAt >= row.Event.CreatedAt)?.CreatedAt ?? row.Event.CreatedAt;
            var elapsed = Math.Max(0, (long)(end - row.Event.CreatedAt).TotalMilliseconds);
            return ActivityOriginPrefix(row.Event, profiles) + state + " · " + pendingLabel +
                (elapsed > 0 ? " · " + FormatCallElapsed(elapsed) : "");
        }
        if (row.Event.Type == CerebralEventTypes.ToolResult)
            return RecordedActivityAgent(row.Event) + " · " + ActivityResultState(row.Event) + " · Unpaired tool receipt";
        return Caption(row.Event, new Dictionary<string, ActivityCall>(StringComparer.Ordinal), profiles);
    }

    private static string ActivityResultState(CerebralEvent item) => ActivityReceiptState(item) switch
    {
        "Unknown" => "Outcome unconfirmed",
        "Cancelled" => "Cancelled",
        "Prepared" => "Prepared",
        "Dispatched" => "Dispatched",
        _ => ActivityRowFailed(new(item.Id, item)) ? "Failed" : ToolResult(item).Success == true ? "Completed" : "Outcome unknown"
    };

    private static bool ActivityRunExecuting(WorkflowConversationRun run) =>
        run.Status == WorkflowStatus.Running && !run.CancelRequested &&
        run.EffectState is null or WorkflowEffectState.Prepared or WorkflowEffectState.Dispatched;

    internal static string? ActivityRecoveryState(ActivityRow row, IReadOnlyList<CerebralEvent> items)
    {
        if (row.Event.Type != CerebralEventTypes.ToolCall ||
            !ActivityCommand(row.Event, out var tool, out _, out _) ||
            tool is not ("cerebral.host:generate" or "cerebral.host:receipt")) return null;
        // A model invocation immediately following the latest failed receipt is the
        // observable review window. This labels its role without exposing private reasoning.
        var previous = items.Where(item => item.TurnId == row.Event.TurnId &&
                item.ConversationId == row.Event.ConversationId && item.Sequence < row.FirstSequence &&
                item.Type == CerebralEventTypes.ToolResult)
            .OrderBy(item => item.Sequence).LastOrDefault();
        return previous is not null && ActivityRowFailed(new(previous.Id, previous))
            ? tool == "cerebral.host:receipt" ? "Reading failure details" : "Reviewing failed call" : null;
    }

    internal sealed record ActivitySection(string Key, string Label, IReadOnlyList<ActivityRow> Rows);

    internal static IReadOnlyList<ActivitySection> ProjectActivitySections(IReadOnlyList<ActivityRow> rows)
    {
        var sections = new List<ActivitySection>();
        List<ActivityRow>? entries = null;
        string? turn = null; string? conversation = null;
        foreach (var row in rows)
        {
            var source = row.Call ?? row.Event;
            var label = ActivitySectionLabel(source);
            // Preparation/review receipts belong with the operation they support. Named
            // operation sections remain chronological, and never merge across saved turns.
            if (entries is null || turn != source.TurnId || conversation != source.ConversationId ||
                label is not null && sections[^1].Label != label)
            {
                entries = [];
                sections.Add(new(row.Key, label ?? (source.Type == CerebralEventTypes.ToolCall ? "Preparing response" : "Run updates"), entries));
                turn = source.TurnId; conversation = source.ConversationId;
            }
            entries.Add(row);
        }
        return sections;
    }

    private static string? ActivitySectionLabel(CerebralEvent item)
    {
        if (!ActivityCommand(item, out var tool, out var arguments, out _)) return null;
        if (tool.StartsWith("cerebral.host:", StringComparison.Ordinal)) return null;
        var operation = tool.StartsWith("mcp.", StringComparison.Ordinal) ? ActivityString(arguments, "op") : "";
        if (tool == "process.run" && ActivityString(arguments, "executableId") == "dotnet" &&
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("arguments", out var argv) &&
            argv.ValueKind == JsonValueKind.Array && argv.GetArrayLength() > 0 && argv[0].ValueKind == JsonValueKind.String)
        {
            var purpose = argv[0].GetString() switch
            {
                "build" => "Building",
                "test" => "Testing",
                "restore" => "Restoring dependencies",
                _ => null
            };
            if (purpose is not null) return purpose;
        }
        var action = operation.Length > 0 ? operation : tool;
        return action switch
        {
            "file.read" or "file.list" or "file.search" or "rd" or "read_lines" or "ls" or "search" or "stat" or "sha" => "Inspecting files",
            "file.write" or "file.edit" or "file.append" or "file.copy" or "file.move" or "wr" or "edit" or "patch" or "replace" or "mkdir" or "cp" or "mv" => "Changing files",
            "plugin.scaffold" or "plugin.create" or "plugin.pack" or "plugin.package" => "Preparing plug-ins",
            "plugin.verify" or "plugin.inspect" or "plugin.validate" => "Checking plug-ins",
            "plugin.present" => "Presenting plug-ins",
            "dotnet_build" => "Building",
            "dotnet_test" => "Testing",
            "dotnet_restore" => "Restoring dependencies",
            "process.run" or "run" or "pwsh" => "Running commands",
            "context.compact" => "Managing context",
            _ when tool.StartsWith("plugin.", StringComparison.Ordinal) => "Working with plug-ins",
            _ when tool.StartsWith("file.", StringComparison.Ordinal) => "Working with files",
            _ when tool.StartsWith("process.", StringComparison.Ordinal) => "Running commands",
            _ when tool.StartsWith("mcp.", StringComparison.Ordinal) => "Using MCP tools",
            _ => "Using " + BoundActivitySummary(tool)
        };
    }

    internal static string ActivitySectionHeader(ActivitySection section, bool latest,
        IReadOnlyList<CerebralEvent> items, WorkflowConversationRun run, bool currentIteration = true)
    {
        var failures = section.Rows.Count(ActivityRowFailed);
        var calls = section.Rows.Count(row => row.Call is not null || row.Event.Type == CerebralEventTypes.ToolCall);
        var pending = section.Rows.Any(row => row.Event.Type == CerebralEventTypes.ToolCall);
        var state = latest ? ActivityState(items, run, currentIteration) : pending ? "Awaiting receipt" : "Finished";
        var parts = new List<string> { section.Label, state };
        if (calls > 0) parts.Add(calls + (calls == 1 ? " call" : " calls"));
        if (failures > 0) parts.Add(failures + (failures == 1 ? " failed call" : " failed calls"));
        return string.Join(" · ", parts);
    }

    private void ReconcileActivityRows(ActivityView view, WorkflowConversationRun run, IReadOnlyDictionary<string, string> profiles)
    {
        var currentIteration = ReferenceEquals(CurrentGenerationActivity(), view);
        var rows = ProjectActivityRows(view.Items);
        var panel = (StackPanel)view.Scroller.Content;
        var sections = ProjectActivitySections(rows);
        var retained = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in view.Rows.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            RemoveActivityAnimation(view.Rows[stale].Expander);
            if (view.Rows[stale].Expander.Parent is Panel parent) parent.Children.Remove(view.Rows[stale].Expander);
            view.Rows.Remove(stale);
        }
        foreach (var model in rows)
        {
            if (!view.Rows.TryGetValue(model.Key, out var visual))
            {
                visual = new(model, new Expander { Margin = new Thickness(8, 3, 0, 3),
                    HeaderTemplate = ActivityRowHeaderTemplate(),
                    IsExpanded = expanded.Contains(model.Key) || expanded.Contains(model.Event.Id) });
                var entry = visual;
                entry.Expander.Expanded += (_, _) =>
                {
                    expanded.Add(entry.Model.Key);
                    entry.Expander.Content ??= ActivityEventBody(entry.Model.Event, entry.Model.Call);
                };
                entry.Expander.Collapsed += (_, _) =>
                {
                    expanded.Remove(entry.Model.Key);
                    entry.Expander.Content = null;
                };
                visual.Expander.Tag = ActivityInlineDetail(model);
                view.Rows[model.Key] = visual;
            }
            else if (visual.Model.Event.Id != model.Event.Id || visual.Model.Call?.Id != model.Call?.Id)
            {
                // A receipt extends the inspected operation in place: retain its selected command text.
                if (visual.Expander.Content is StackPanel body && visual.Model.Event.Type == CerebralEventTypes.ToolCall &&
                    model.Call?.Id == visual.Model.Event.Id && model.Event.Type == CerebralEventTypes.ToolResult)
                {
                    body.Children.Add(Ui.Text("Completed: " + model.Event.CreatedAt.ToLocalTime().ToString("h:mm:ss tt"), 11, Ui.Muted));
                    var result = ActivityResultText(model.Event, model.Call);
                    if (result.Length > 0) body.Children.Add(ActivityDetailBox(result, formatted: true));
                }
                else if (visual.Expander.IsExpanded) visual.Expander.Content = ActivityEventBody(model.Event, model.Call);
                visual.Model = model;
                visual.Expander.Tag = ActivityInlineDetail(model);
            }
            RenderActivityImageHeader(visual.Expander, model);
            visual.Expander.Header = ActivityRowCaption(model, run, view.Items, profiles,
                currentIteration: currentIteration);
            visual.Expander.Foreground = ActivityRowFailed(model) ? Ui.ErrorText : Ui.Ink;
            visual.Expander.Background = ActivityRowFailed(model) ? Ui.ErrorBackground : System.Windows.Media.Brushes.Transparent;
            UpdateActivityAnimation(visual, run);
            AutomationProperties.SetAutomationId(visual.Expander, "WorkflowActivityEntry-" + model.Key);
            if (visual.Expander.IsExpanded && visual.Expander.Content is null)
                visual.Expander.Content = ActivityEventBody(model.Event, model.Call);
            activityEntries[model.Event.Id] = visual.Expander;
            if (model.Call is not null) activityEntries[model.Call.Id] = visual.Expander;
        }
        var retainedSections = sections.Select(section => section.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in view.Sections.Keys.Where(key => !retainedSections.Contains(key)).ToArray())
        {
            panel.Children.Remove(view.Sections[stale].Container);
            view.Sections.Remove(stale);
        }
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            if (!view.Sections.TryGetValue(section.Key, out var visual))
            {
                visual = new(section, new StackPanel(), new StackPanel(), Ui.Text("", 12, Ui.Muted));
                visual.Header.Margin = new Thickness(8, 7, 0, 3);
                visual.Container.Children.Add(visual.Header);
                visual.Container.Children.Add(visual.Rows);
                AutomationProperties.SetAutomationId(visual.Container, "WorkflowActivitySection-" + section.Key);
                view.Sections[section.Key] = visual;
            }
            visual.Model = section;
            for (var rowIndex = 0; rowIndex < section.Rows.Count; rowIndex++)
            {
                var entry = view.Rows[section.Rows[rowIndex].Key].Expander;
                if (rowIndex < visual.Rows.Children.Count && ReferenceEquals(visual.Rows.Children[rowIndex], entry)) continue;
                if (entry.Parent is Panel parent) parent.Children.Remove(entry);
                visual.Rows.Children.Insert(rowIndex, entry);
            }
            if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], visual.Container)) continue;
            panel.Children.Remove(visual.Container);
            panel.Children.Insert(index, visual.Container);
        }
        RefreshActivitySectionHeaders(view, run, currentIteration);
        view.Expander.Header = ActivityHeader(view.Items, run, profiles,
            olderActivityOmitted: Snapshot?.HasOlder == true, currentIteration: currentIteration) +
            GenerationRateCaption(view, run, currentIteration);
        view.Expander.Foreground = ActivityHeaderBrush(view.Items, run, currentIteration);
        ApplyCompactActivity(view);
    }

    private static DataTemplate ActivityRowHeaderTemplate()
    {
        // Keep Header as the caption string for automation and timer updates. The template and
        // Tag detail remain on the same expander when a late receipt extends the operation.
        var layout = new FrameworkElementFactory(typeof(StackPanel));
        var step = new FrameworkElementFactory(typeof(DockPanel));
        step.AppendChild(ActivityStepMarkerFactory());
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding());
        caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        caption.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        caption.SetBinding(ToolTipProperty, new Binding());
        ConfigureActivityCaptionAnimation(caption);
        step.AppendChild(caption);
        layout.AppendChild(step);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetValue(AutomationProperties.AutomationIdProperty, "WorkflowActivityInlineDetail");
        detail.SetValue(TextBlock.FontSizeProperty, 12d);
        detail.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 2));
        detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(FrameworkElement.Tag))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Expander), 1)
        });
        layout.AppendChild(detail);
        AppendActivityImageHeader(layout);
        return new DataTemplate { VisualTree = layout };
    }

    private static System.Windows.Media.Brush ActivityHeaderBrush(IReadOnlyList<CerebralEvent> items,
        WorkflowConversationRun run, bool currentIteration) =>
        ActivityState(items, run, currentIteration) switch { "Failed" => Ui.ErrorText, "Completed" => Ui.Accent, _ => Ui.Ink };

    private static void RefreshActivitySectionHeaders(ActivityView view, WorkflowConversationRun run, bool currentIteration)
    {
        var lastKey = view.Sections.Values.OrderBy(section => section.Model.Rows[0].FirstSequence).LastOrDefault()?.Model.Key;
        foreach (var section in view.Sections.Values)
            section.Header.Text = ActivitySectionHeader(section.Model, section.Model.Key == lastKey,
                view.Items, run, currentIteration);
    }

    private sealed class ActivitySectionView(ActivitySection model, StackPanel container, StackPanel rows, TextBlock header)
    {
        internal ActivitySection Model = model;
        internal StackPanel Container { get; } = container;
        internal StackPanel Rows { get; } = rows;
        internal TextBlock Header { get; } = header;
    }

    private sealed class ActivityRowView(ActivityRow model, Expander expander)
    {
        internal ActivityRow Model = model;
        internal Expander Expander { get; } = expander;
    }
}
