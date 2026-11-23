using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.GoalCardPlugin;

/// <summary>Optional journal reader. The host retains all authoritative messages; this package stores no shadow goal.</summary>
public sealed class GoalCardPlugin : IDesktopPlugin, IDesktopMountPlugin
{
    private IDesktopPluginHost? host;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<(Expander Expander, StackPanel Body)> views = [];
    private readonly HashSet<Window> windows = [];
    private bool disposed;
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new("goal", DesktopMountSlots.ComposerAccessories)
            { Order = -100, Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } },
            "Conversation goal", CreateView),
        new DesktopMountAction(new("show-goal", DesktopMountSlots.WorkspaceActions)
            { Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } },
            "Goal", _ => { ShowGoal(); return Task.CompletedTask; }, "View the original accepted request and later instructions.")
    ];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    public void Initialize(IDesktopPluginHost pluginHost)
    {
        if (host is not null || disposed) throw new InvalidOperationException("Goal card is already initialized or disposed.");
        host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
        host.ConversationChanged += Changed;
        host.LeadMessageCommitted += Committed;
    }

    private void ShowGoal()
    {
        if (disposed || host is null) return;
        var card = (Expander)CreateView();
        var window = new Window { Title = "Conversation goal", Width = 640, Height = 560,
            MinWidth = 360, MinHeight = 250, Content = new ScrollViewer { Content = card,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16) }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive);
        windows.Add(window);
        window.Closed += (_, _) => windows.Remove(window);
        window.Show();
        card.IsExpanded = true;
    }

    private FrameworkElement CreateView()
    {
        if (host is null || disposed) throw new InvalidOperationException("Goal card is unavailable.");
        var body = new StackPanel();
        var expander = new Expander { Header = "Goal · original request and updates", Content = body };
        AutomationProperties.SetAutomationId(expander, "ConversationGoalCard");
        views.Add((expander, body));
        expander.Loaded += (_, _) => { if (!disposed && !views.Any(item => ReferenceEquals(item.Expander, expander))) views.Add((expander, body)); };
        expander.Expanded += async (_, _) => await RefreshAsync(body);
        expander.Unloaded += (_, _) => views.RemoveAll(item => ReferenceEquals(item.Expander, expander));
        return expander;
    }

    private void Changed(object? sender, DesktopConversationChangedEventArgs args)
    {
        foreach (var view in views.ToArray())
        {
            view.Body.Children.Clear();
            if (view.Expander.IsExpanded) _ = RefreshAsync(view.Body);
        }
    }
    private void Committed(object? sender, DesktopLeadMessageEventArgs args)
    {
        foreach (var view in views.Where(view => view.Expander.IsExpanded).ToArray()) _ = RefreshAsync(view.Body);
    }

    private async Task RefreshAsync(StackPanel body)
    {
        if (disposed || host is null) return;
        var binding = host.GetConversation().Binding;
        var ticket = new object(); body.Tag = ticket;
        body.Children.Clear();
        if (binding.SessionId is null) { body.Children.Add(Text("The first accepted request will appear here.")); return; }
        if (host is not IDesktopServiceHost service) { body.Children.Add(Text("The connected host does not provide journal access.")); return; }
        body.Children.Add(Text("Reading the accepted request…"));
        try
        {
            var result = await service.ExecuteServiceCommandAsync("conversations.goal", new { id = binding.SessionId }, lifetime.Token);
            if (disposed || host.GetConversation().Binding != binding || !ReferenceEquals(body.Tag, ticket)) return;
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "The journal could not be read.");
            var snapshot = result.Data.Deserialize<GoalSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Goal display response was empty.");
            if (snapshot.ConversationId != binding.SessionId || snapshot.Revision < 0 || snapshot.Original is null ||
                snapshot.LaterInstructions is null || snapshot.LaterInstructions.Length > 8)
                throw new InvalidDataException("Goal display response has invalid ownership or bounds.");
            body.Children.Clear();
            body.Children.Add(Text("Original accepted request", true));
            AddEntry(body, snapshot.Original);
            if (snapshot.LaterInstructions.Length > 0)
            {
                body.Children.Add(Text("Later messages and corrections", true));
                foreach (var entry in snapshot.LaterInstructions) AddEntry(body, entry);
            }
            if (snapshot.HasOlderInstructions) body.Children.Add(Text("Showing the latest eight updates. Earlier messages remain in chat history."));
            body.Children.Add(Text("This card reads your messages. It does not infer a new objective or change the conversation."));
            var actions = new WrapPanel();
            var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 6, 6, 0) };
            refresh.Click += async (_, _) => await RefreshAsync(body);
            var correct = new Button { Content = "Add goal correction", Margin = new Thickness(0, 6, 6, 0) };
            AutomationProperties.SetAutomationId(correct, "ConversationGoalCorrection");
            correct.Click += async (_, _) => await CorrectAsync(binding);
            actions.Children.Add(refresh); actions.Children.Add(correct); body.Children.Add(actions);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (disposed || host.GetConversation().Binding != binding || !ReferenceEquals(body.Tag, ticket)) return;
            body.Children.Clear();
            var diagnostic = Text("Goal display unavailable: " + error.Message);
            diagnostic.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.ErrorTextBrush);
            body.Children.Add(diagnostic);
        }
    }

    private static void AddEntry(Panel panel, GoalEntry entry)
    {
        if (entry.Text is null || entry.Text.Length > 8192 || string.IsNullOrWhiteSpace(entry.EventId) ||
            entry.EventId.Length > 128 || string.IsNullOrWhiteSpace(entry.RunId) || entry.RunId.Length > 128)
            throw new InvalidDataException("Goal message has invalid bounds.");
        panel.Children.Add(Text((entry.Steering ? "Correction · " : "") + entry.CreatedAt.ToLocalTime().ToString("g")));
        panel.Children.Add(Text(entry.Text.Length == 0 ? "(Attachment-only request)" : entry.Text));
        if (entry.Truncated) panel.Children.Add(Text("Excerpt only. The full accepted message remains in chat history."));
    }

    private async Task CorrectAsync(DesktopConversationBinding binding)
    {
        if (disposed || host is null) return;
        try
        {
            var draft = host.GetDraft();
            if (host.GetConversation().Binding != binding || draft.Binding != binding) throw new InvalidOperationException("The conversation changed. Reopen its goal card.");
            var text = draft.Text + (string.IsNullOrWhiteSpace(draft.Text) ? "" : "\n\n") + "Goal correction: ";
            var result = await host.ReplaceDraftAsync(new(binding, draft.Revision, text), lifetime.Token);
            if (result.State is not (DesktopOperationState.Accepted or DesktopOperationState.Completed))
                throw new InvalidOperationException(result.Message ?? "The current draft changed.");
            host.ReportStatus("Write the correction in the composer, then send or steer it when ready.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) host.ReportStatus("Goal correction: " + error.Message); }
    }

    private static TextBlock Text(string text, bool heading = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 4, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        return block;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (host is not null) { host.ConversationChanged -= Changed; host.LeadMessageCommitted -= Committed; }
        foreach (var window in windows.ToArray()) window.Close();
        windows.Clear(); views.Clear(); lifetime.Dispose(); host = null;
    }

    private sealed record GoalSnapshot(string ConversationId, long Revision, GoalEntry Original,
        GoalEntry[] LaterInstructions, bool HasOlderInstructions);
    private sealed record GoalEntry(string EventId, string RunId, long Sequence, string Text, bool Truncated,
        bool Steering, DateTimeOffset CreatedAt);
}
