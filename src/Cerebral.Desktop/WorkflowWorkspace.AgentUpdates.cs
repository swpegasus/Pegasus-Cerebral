using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private const string CollaborationResultType = "collaboration.result";
    private const string CollaborationServiceId = "cerebral.collaboration";
    private const int MaximumAdoptedResultCharacters = 32 * 1024 + 256;
    private const int MaximumAgentUpdatePreviewCharacters = 512;
    private const int MaximumCachedAgentUpdateCards = WorkflowConversationClient.MaximumPageEvents *
        WorkflowConversationClient.MaximumRecentPages + MaximumRetainedTranscriptEvents;
    private readonly Dictionary<AgentUpdateCardIdentity, AgentUpdateCardView> agentUpdateCards = [];
    private readonly HashSet<AgentUpdateCardIdentity> agentUpdateCacheWindow = [];

    internal int AgentUpdateCardCount => agentUpdateCards.Count;

    internal sealed record PublicAgentUpdate(string EventId, string ConversationId, string TaskId,
        string AttemptId, string TargetConversationId, string TargetRunId, long TargetRunRevision,
        string Sender, string? SenderRole, string Text);

    /// <summary>Only the service's adopted public result is an update card. Agent answers and
    /// card-looking prose do not acquire collaboration identity through their content.</summary>
    internal static bool TryProjectAgentUpdate(CerebralEvent item, string expectedConversationId,
        out PublicAgentUpdate update)
    {
        update = null!;
        if (item.Type != CollaborationResultType || item.SchemaVersion != CerebralEventContract.Version ||
            item.Sequence <= 0 || item.ConversationId != expectedConversationId ||
            item.Projection is not { Model: ModelInclusion.Evidence, Human: HumanVisibility.Transcript, Retention: RetentionPolicy.Durable } ||
            item.Actor is not { Kind: EventActorKind.Service, Id: CollaborationServiceId } ||
            item.Provenance is not { Source: EventSourceKind.Service, SourceId: CollaborationServiceId } ||
            item.Content is not { Count: 1 } || item.Content[0] is not TextContentPart body ||
            body.Text is null || body.Text.Length > MaximumAdoptedResultCharacters ||
            !AgentUpdateUtf16(body.Text) || item.Data is not { ValueKind: JsonValueKind.Object } data ||
            data.GetRawText().Length > 8192 || !AgentUpdateIdentifier(item.Id, 128) ||
            !AgentUpdateIdentifier(item.ConversationId, 128) || !AgentUpdateIdentifier(item.Provenance.JobId, 128))
            return false;

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value)) return false;
        if (!AgentUpdateString(fields, "taskId", 128, out var taskId) ||
            !AgentUpdateUuid(taskId) || taskId != item.TurnId || taskId != item.CorrelationId ||
            !AgentUpdateString(fields, "attemptId", 128, out var attemptId) || !AgentUpdateUuid(attemptId) ||
            !AgentUpdateString(fields, "targetConversationId", 128, out var targetConversationId) ||
            targetConversationId == expectedConversationId ||
            !fields.TryGetValue("targetRunRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var targetRunRevision) || targetRunRevision < 1)
            return false;

        // Optional attribution is an immutable adopted-event field, never a lookup of today's
        // profile name/role. A missing or inconsistent enrichment retains the service fallback.
        var sender = "Collaboration service";
        string? role = null;
        var agentId = item.Provenance.AgentId;
        if (AgentUpdateIdentifier(agentId, 128))
        {
            sender = "Agent " + agentId;
            if (AgentUpdateString(fields, "targetProfileId", 128, out var profileId) && profileId == agentId &&
                AgentUpdateString(fields, "targetProfileName", 256, out var profileName))
            {
                sender = profileName;
                if (AgentUpdateString(fields, "targetProfileRole", 64, out var profileRole)) role = profileRole;
            }
        }
        update = new(item.Id, item.ConversationId, taskId, attemptId, targetConversationId,
            item.Provenance.JobId!, targetRunRevision, sender, role, body.Text);
        return true;
    }

    private static bool AgentUpdateUuid(string value) => Guid.TryParseExact(value, "N", out var id) &&
        id != Guid.Empty && value == id.ToString("N");

    private static bool AgentUpdateIdentifier(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl) && AgentUpdateUtf16(value);

    private static bool AgentUpdateString(IReadOnlyDictionary<string, JsonElement> fields, string name,
        int maximum, out string value)
    {
        value = "";
        if (!fields.TryGetValue(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        var text = field.GetString();
        if (!AgentUpdateIdentifier(text, maximum)) return false;
        value = text!;
        return true;
    }

    private static bool AgentUpdateUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    internal static string AgentUpdatePreview(string text)
    {
        var prose = new StringBuilder();
        var fenced = false;
        foreach (var original in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = original.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            { fenced = !fenced; continue; }
            if (fenced) continue;
            if (line.Length == 0) { if (prose.Length > 0) break; continue; }
            line = line.TrimStart('#', '>', '-', '*', ' ', '\t');
            if (line.Length == 0 || AgentUpdateTechnicalLine(line)) continue;
            if (prose.Length > 0) prose.Append(' ');
            prose.Append(line);
            if (prose.Length >= MaximumAgentUpdatePreviewCharacters) break;
        }
        if (prose.Length == 0) return "Result received. Show more to read the recorded content.";
        var preview = string.Join(" ", prose.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length <= MaximumAgentUpdatePreviewCharacters) return preview;
        // The visual limit is three wrapped lines; the text budget also preserves complete
        // Unicode text elements (including combining marks and joined emoji).
        var elements = StringInfo.ParseCombiningCharacters(preview);
        var end = elements.LastOrDefault(offset => offset <= MaximumAgentUpdatePreviewCharacters - 1);
        return preview[..end].TrimEnd() + "…";
    }

    private static bool AgentUpdateTechnicalLine(string line)
    {
        var text = line.Trim('`', ' ');
        static bool PathLike(string value) => value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith(".tmp/", StringComparison.OrdinalIgnoreCase) || value.StartsWith(".tmp\\", StringComparison.OrdinalIgnoreCase) ||
            value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
        static bool Digest(string value) => value.Length is >= 40 and <= 128 && value.All(Uri.IsHexDigit);
        if (PathLike(text) || Digest(text) || text.StartsWith("[Result text truncated;", StringComparison.Ordinal) ||
            text.StartsWith('{') || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        var separator = text.IndexOfAny([':', '=']);
        if (separator > 0)
        {
            var name = text[..separator].Trim().ToLowerInvariant();
            var value = text[(separator + 1)..].Trim().Trim('`');
            if ((name is "sha256" or "sha-256" or "hash" or "digest") && Digest(value)) return true;
            if ((name is "path" or "manifest" or "artifact" or "output") && PathLike(value)) return true;
        }
        var link = text.IndexOf("](", StringComparison.Ordinal);
        return text.StartsWith('[') && link > 0 && text.EndsWith(')') && PathLike(text[(link + 2)..^1].Trim('<', '>'));
    }

    private readonly record struct AgentUpdateCardIdentity(long ConnectionEpoch, string OwnerProfileId,
        string ConversationId, string EventId);

    private AgentUpdateCardIdentity AgentUpdateKey(CerebralEvent item) =>
        new(connectionEpoch, Snapshot!.Run!.ProfileId, item.ConversationId, item.Id);

    private FrameworkElement TranscriptCard(CerebralEvent item, IReadOnlyDictionary<string, string> profileNames)
    {
        if (Snapshot?.Run is not null && TryProjectAgentUpdate(item, Snapshot.ConversationId, out var update))
        {
            var key = AgentUpdateKey(item);
            AgentUpdateCardView? saved = agentUpdateCards.GetValueOrDefault(key);
            if (saved is not null && saved.Model != update)
            { saved.Dispose(); agentUpdateCards.Remove(key); saved = null; }
            if (saved is null)
            {
                saved = CreateAgentUpdateCard(key, update, item);
                if (agentUpdateCacheWindow.Contains(key)) agentUpdateCards.Add(key, saved);
            }
            if (saved.Root.Parent is Panel parent) parent.Children.Remove(saved.Root);
            return saved.Root;
        }
        var human = item.Actor.Kind == EventActorKind.User;
        var speaker = TranscriptSpeaker(item, profileNames);
        var caption = Ui.Text(speaker, 12, Ui.Muted);
        caption.Margin = new Thickness(0, 0, 0, 6);
        var ordinary = new StackPanel();
        ordinary.Children.Add(caption);
        // Keep the existing selectable content and attachment controls. Only their
        // presentation changes; internal adoption envelopes remain out of public prose.
        ordinary.Children.Add(EventBody(item.Type == CollaborationResultType ? item with { Data = null } : item));
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new GridLength(18, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(82, GridUnitType.Star) });
        if (human)
        {
            var bubble = new Border { Child = ordinary, Background = Ui.RaisedSurface, BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14, 14, 4, 14),
                Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Right };
            // A proportional column leaves a readable gutter at every window width,
            // rather than imposing a fixed message width or a nested scroll viewport.
            Grid.SetColumn(bubble, 1); row.Children.Add(bubble);
            AutomationProperties.SetAutomationId(bubble, "WorkflowHumanMessageBubble-" + item.Id);
            AutomationProperties.SetName(bubble, "Your message");
        }
        else
        {
            Grid.SetColumnSpan(ordinary, 2); row.Children.Add(ordinary);
        }
        var card = new Border { Child = row, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 16), Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetAutomationId(card, "WorkflowTranscriptMessage-" + item.Id);
        AutomationProperties.SetName(card, human ? "Message from you" : "Message from " + speaker);
        return card;
    }

    private AgentUpdateCardView CreateAgentUpdateCard(AgentUpdateCardIdentity key, PublicAgentUpdate model, CerebralEvent item)
    {
        var sender = model.SenderRole is null ? model.Sender : model.Sender + " · " + model.SenderRole;
        var origin = Ui.Text("Sent by " + sender + " from another conversation", 12, Ui.Muted);
        origin.Margin = new Thickness(2, 0, 2, 5);
        var preview = new TextBlock { Text = AgentUpdatePreview(model.Text), FontSize = 14, LineHeight = 21,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 63, Foreground = Ui.Ink };
        var body = new StackPanel { Visibility = Visibility.Collapsed };
        var toggle = new Button { Content = "Show more", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4, 2, 4, 2), FontSize = 12 };
        var state = Ui.Text("Completed result received", 11, Ui.Muted);
        state.Margin = new Thickness(0, 0, 0, 5);
        var content = new StackPanel();
        content.Children.Add(state); content.Children.Add(preview); content.Children.Add(body); content.Children.Add(toggle);
        var surface = new Border { Background = Ui.RaisedSurface, BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Child = content };
        var flow = new StackPanel(); flow.Children.Add(origin); flow.Children.Add(surface);
        var root = new Border { Child = flow, HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 520, Margin = new Thickness(24, 8, 0, 10) };
        var view = new AgentUpdateCardView(key, model, item with { Data = null }, root, preview, body, toggle);
        root.Tag = view;
        AutomationProperties.SetAutomationId(root, "WorkflowAgentUpdate-" + model.EventId);
        AutomationProperties.SetName(root, "Public collaboration result from " + sender);
        AutomationProperties.SetAutomationId(preview, "WorkflowAgentUpdatePreview-" + model.EventId);
        AutomationProperties.SetAutomationId(body, "WorkflowAgentUpdateBody-" + model.EventId);
        AutomationProperties.SetAutomationId(toggle, "WorkflowAgentUpdateToggle-" + model.EventId);
        AutomationProperties.SetName(toggle, "Show full adopted result from " + sender);
        RoutedEventHandler click = (_, _) =>
        {
            if (!AgentUpdateCardCurrent(view)) return;
            // Expansion is reader input. It invalidates any queued automatic transcript
            // restoration, without scrolling, stealing focus or changing follow intent.
            transcriptInputRevision++; transcriptRestoring = false; pendingQuestionFocus = null;
            if (view.Body.Children.Count == 0) view.Body.Children.Add(EventBody(view.Event));
            view.Expanded = !view.Expanded;
            view.Preview.Visibility = view.Expanded ? Visibility.Collapsed : Visibility.Visible;
            view.Body.Visibility = view.Expanded ? Visibility.Visible : Visibility.Collapsed;
            view.Toggle.Content = view.Expanded ? "Show less" : "Show more";
            AutomationProperties.SetName(view.Toggle, (view.Expanded ? "Collapse" : "Show full") + " adopted result from " + sender);
        };
        view.Click = click;
        toggle.Click += click;
        return view;
    }

    private bool AgentUpdateCardCurrent(AgentUpdateCardView view) => !disposed && !view.Disposed && IsLoaded &&
        view.Key.ConnectionEpoch == connectionEpoch && view.Key.OwnerProfileId == Snapshot?.Run?.ProfileId &&
        view.Key.ConversationId == Snapshot?.ConversationId && view.Root.IsDescendantOf(this) &&
        displayedEvents.Any(item => item.Id == view.Key.EventId &&
            TryProjectAgentUpdate(item, view.Key.ConversationId, out var update) && update == view.Model);

    private void ReconcileAgentUpdateCardWindow()
    {
        agentUpdateCacheWindow.Clear();
        if (Snapshot?.Run is not null)
        {
            var candidates = displayedEvents.Where(item => TryProjectAgentUpdate(item, Snapshot.ConversationId, out _))
                .Select(item => (Key: AgentUpdateKey(item), item.Sequence))
                .OrderByDescending(item => agentUpdateCards.TryGetValue(item.Key, out var view) &&
                    (view.Expanded || view.HasSelection)).ThenByDescending(item => item.Sequence)
                .Take(MaximumCachedAgentUpdateCards);
            foreach (var item in candidates) agentUpdateCacheWindow.Add(item.Key);
        }
        foreach (var key in agentUpdateCards.Keys.Where(key => !agentUpdateCacheWindow.Contains(key)).ToArray())
        { agentUpdateCards[key].Dispose(); agentUpdateCards.Remove(key); }
    }

    private void ClearAgentUpdateCards()
    {
        // Explicitly read older history can exceed the recent cache budget. Those uncached
        // cards remain fully readable in the current visual tree and have inert stale callbacks.
        var views = agentUpdateCards.Values.Concat(transcript.Children.OfType<Border>()
            .Select(root => root.Tag).OfType<AgentUpdateCardView>()).Distinct().ToArray();
        foreach (var view in views) view.Dispose();
        agentUpdateCards.Clear(); agentUpdateCacheWindow.Clear();
        renderedVersion = null;
    }

    private sealed class AgentUpdateCardView(AgentUpdateCardIdentity key, PublicAgentUpdate model, CerebralEvent item,
        Border root, TextBlock preview, StackPanel body, Button toggle) : IDisposable
    {
        internal AgentUpdateCardIdentity Key { get; } = key;
        internal PublicAgentUpdate Model { get; } = model;
        internal CerebralEvent Event { get; } = item;
        internal Border Root { get; } = root;
        internal TextBlock Preview { get; } = preview;
        internal StackPanel Body { get; } = body;
        internal Button Toggle { get; } = toggle;
        internal RoutedEventHandler? Click { get; set; }
        internal bool Expanded { get; set; }
        internal bool Disposed { get; private set; }
        internal bool HasSelection => Body.Children.Count > 0 && AgentUpdateTextControls(Body).Any(control =>
            control is RichTextBox rich && !rich.Selection.IsEmpty || control is TextBox text && text.SelectionLength > 0);

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Click is not null) Toggle.Click -= Click;
            Click = null; Root.Tag = null; Root.Child = null; Preview.Text = ""; Body.Children.Clear();
        }
    }

    private static IEnumerable<DependencyObject> AgentUpdateTextControls(DependencyObject root)
    {
        if (root is TextBox or RichTextBox) yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in AgentUpdateTextControls(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
