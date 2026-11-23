using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.CollaborationPlugin;

/// <summary>
/// Presents the service-owned collaboration task envelope in a removable sidebar. It never owns or replaces Chat.
/// </summary>
public sealed class CollaborationPlugin : IDesktopPlugin, IDesktopMountPlugin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource lifetime = new();
    private readonly ListBox tasks = new() { MinHeight = 150 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock events = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox steering = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 58,
        MaxHeight = 120,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button openSource = ActionButton("Open source chat", "Collaboration.OpenSource");
    private readonly Button openTarget = ActionButton("Open worker chat", "Collaboration.OpenTarget");
    private readonly Button cancel = ActionButton("Cancel", "Collaboration.Cancel");
    private readonly Button reconcile = ActionButton("Reconcile", "Collaboration.Reconcile");
    private readonly Button adopt = ActionButton("Adopt result", "Collaboration.Adopt");
    private readonly Button sendSteering = ActionButton("Send steering", "Collaboration.SendSteering");
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private IDesktopWorkspaceHost? workspace;
    private bool refreshing;
    private bool disposed;

    // API-v1 compatibility members. The descriptor-first mount below owns the sidebar placement.
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new DesktopMountDescriptor("tasks", DesktopMountSlots.SidebarTabs)
        {
            Order = 40,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Collaboration", CreatePanel)
    ];

    public void Initialize(IDesktopPluginHost pluginHost)
    {
        host = pluginHost;
        service = pluginHost as IDesktopServiceHost ??
            throw new InvalidOperationException("Pegasus Collaboration requires the structured service-command host.");
        workspace = pluginHost as IDesktopWorkspaceHost;
    }

    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    private FrameworkElement CreatePanel()
    {
        var root = new Grid { Margin = new Thickness(10) };
        AutomationProperties.SetAutomationId(root, "Collaboration.Panel");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 150 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 190 });

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Collaboration tasks", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var explanation = new TextBlock
        {
            Text = "Inspect and direct bounded tasks between chats. The conversation transcript stays in the main Chat area.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8)
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.MutedTextBrush);
        header.Children.Add(explanation);
        var refresh = ActionButton("Refresh", "Collaboration.Refresh");
        refresh.Click += async (_, _) => await RefreshAsync();
        header.Children.Add(refresh);
        status.Margin = new Thickness(0, 7, 0, 7);
        status.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.MutedTextBrush);
        AutomationProperties.SetAutomationId(status, "Collaboration.Status");
        header.Children.Add(status);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        tasks.BorderThickness = new Thickness(0);
        tasks.Background = Brushes.Transparent;
        tasks.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        tasks.SelectionChanged += async (_, _) => await ShowSelectedAsync();
        AutomationProperties.SetAutomationId(tasks, "Collaboration.TaskList");
        Grid.SetRow(tasks, 1);
        root.Children.Add(tasks);

        var selected = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        details.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(details, "Collaboration.Details");
        selected.Children.Add(details);
        var navigation = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        navigation.Children.Add(openSource);
        navigation.Children.Add(openTarget);
        selected.Children.Add(navigation);
        var operations = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        operations.Children.Add(cancel);
        operations.Children.Add(reconcile);
        operations.Children.Add(adopt);
        selected.Children.Add(operations);
        var steeringLabel = new TextBlock { Text = "Steer this task", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 4) };
        selected.Children.Add(steeringLabel);
        AutomationProperties.SetAutomationId(steering, "Collaboration.Steering");
        selected.Children.Add(steering);
        selected.Children.Add(sendSteering);
        var eventsLabel = new TextBlock { Text = "Recent task activity", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        selected.Children.Add(eventsLabel);
        events.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.MutedTextBrush);
        AutomationProperties.SetAutomationId(events, "Collaboration.Events");
        selected.Children.Add(events);

        openSource.Click += async (_, _) => await OpenConversationAsync(source: true);
        openTarget.Click += async (_, _) => await OpenConversationAsync(source: false);
        cancel.Click += async (_, _) => await ChangeTaskAsync("collaboration.cancel", item => new
        {
            taskId = item.Id,
            expectedTaskRevision = item.Revision
        });
        reconcile.Click += async (_, _) => await ChangeTaskAsync("collaboration.reconcile", item => new
        {
            taskId = item.Id,
            expectedTaskRevision = item.Revision
        });
        adopt.Click += async (_, _) => await ChangeTaskAsync("collaboration.adopt", item => new
        {
            requestId = "desktop-adopt-" + Guid.NewGuid().ToString("N"),
            taskId = item.Id,
            expectedTaskRevision = item.Revision,
            attemptId = item.AttemptId,
            expectedTargetRunRevision = item.TargetRunRevision
        });
        sendSteering.Click += async (_, _) => await SendSteeringAsync();

        var scroll = new ScrollViewer
        {
            Content = selected,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        ClearSelection();
        _ = RefreshAsync();
        return root;
    }

    private async Task RefreshAsync()
    {
        if (disposed || refreshing || service is null) return;
        refreshing = true;
        var selectedId = Selected?.Id;
        SetStatus("Refreshing recent collaboration tasks…");
        try
        {
            var page = await CommandAsync<TaskListPage>("collaboration.list", new { limit = 40 }, lifetime.Token);
            tasks.Items.Clear();
            foreach (var task in page.Tasks)
            {
                var choice = new TaskChoice(task);
                tasks.Items.Add(choice);
                if (task.Id == selectedId) tasks.SelectedItem = choice;
            }
            if (tasks.SelectedItem is null && tasks.Items.Count > 0) tasks.SelectedIndex = 0;
            SetStatus(page.Tasks.Count == 0 ? "No collaboration tasks have been created yet." :
                $"Showing {page.Tasks.Count} recent task{(page.Tasks.Count == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            tasks.Items.Clear();
            ClearSelection();
            SetStatus("Collaboration tasks are unavailable: " + ex.Message, error: true);
        }
        finally { refreshing = false; }
    }

    private async Task ShowSelectedAsync()
    {
        var item = Selected;
        if (disposed || item is null)
        {
            ClearSelection();
            return;
        }
        var waiting = string.IsNullOrWhiteSpace(item.WaitingReason) ? "" : Environment.NewLine + "Waiting: " + item.WaitingReason;
        var error = string.IsNullOrWhiteSpace(item.Error) ? "" : Environment.NewLine + "Error: " + item.Error;
        var adopted = item.AdoptedAt is null ? "" : Environment.NewLine + "Adopted into the source chat.";
        details.Text = $"{item.Status} · revision {item.Revision}{Environment.NewLine}{item.Request.Instruction}{Environment.NewLine}" +
            $"Source: {item.Request.SourceConversationId}{Environment.NewLine}Worker: {item.Request.TargetConversationId}" +
            waiting + error + adopted;
        var terminal = item.Status is "Completed" or "Failed" or "Cancelled";
        openSource.IsEnabled = workspace?.WorkspaceAvailable == true;
        openTarget.IsEnabled = workspace?.WorkspaceAvailable == true;
        cancel.IsEnabled = !terminal && item.Status != "Cancelling";
        reconcile.IsEnabled = item.Status == "ReconcileRequired";
        adopt.IsEnabled = item.Status == "Completed" && item.AdoptedAt is null && item.TargetRunRevision is not null;
        steering.IsEnabled = !terminal;
        sendSteering.IsEnabled = !terminal;
        events.Text = "Loading…";
        try
        {
            var page = await CommandAsync<TaskEventPage>("collaboration.events", new
            {
                taskId = item.Id,
                afterSequence = 0,
                limit = 40
            }, lifetime.Token);
            events.Text = page.Events.Count == 0 ? "No retained task activity." : string.Join(Environment.NewLine,
                page.Events.Select(value => $"{value.At.ToLocalTime():h:mm tt} · {Friendly(value.Type)} · {value.Status}"));
            if (item.Status == "Completed" && item.TargetRunRevision is not null)
            {
                var result = await CommandAsync<ResultPage>("collaboration.result", new
                {
                    taskId = item.Id,
                    expectedTargetRunRevision = item.TargetRunRevision.Value,
                    offset = 0,
                    maximumBytes = 16 * 1024
                }, lifetime.Token);
                if (!string.IsNullOrWhiteSpace(result.Content))
                    details.Text += Environment.NewLine + Environment.NewLine + "Result" + Environment.NewLine + result.Content +
                        (result.Complete ? "" : Environment.NewLine + "Result preview is bounded; more content remains.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { events.Text = "Task activity is unavailable: " + ex.Message; }
    }

    private async Task OpenConversationAsync(bool source)
    {
        var item = Selected;
        if (disposed || item is null || host is null || workspace?.WorkspaceAvailable != true) return;
        var id = source ? item.Request.SourceConversationId : item.Request.TargetConversationId;
        var result = await workspace.SelectConversationAsync(host.GetConversation().Binding, id, lifetime.Token);
        host.ReportStatus(result.Message ?? (result.State is DesktopOperationState.Accepted or DesktopOperationState.Completed
            ? $"Opened {(source ? "source" : "worker")} chat."
            : result.State.ToString()));
    }

    private async Task SendSteeringAsync()
    {
        var text = steering.Text.Trim();
        if (text.Length == 0) { SetStatus("Enter steering before sending it.", error: true); return; }
        if (text.Length > 16_000) { SetStatus("Steering is limited to 16,000 characters.", error: true); return; }
        await ChangeTaskAsync("collaboration.steer", item => new
        {
            requestId = "desktop-steer-" + Guid.NewGuid().ToString("N"),
            taskId = item.Id,
            expectedTaskRevision = item.Revision,
            text,
            assignmentText = (string?)null
        }, () => steering.Clear());
    }

    private async Task ChangeTaskAsync(string operation, Func<TaskItem, object> arguments, Action? completed = null)
    {
        var item = Selected;
        if (disposed || item is null) return;
        SetStatus("Applying " + Friendly(operation) + "…");
        try
        {
            var updated = await CommandAsync<TaskItem>(operation, arguments(item), lifetime.Token);
            completed?.Invoke();
            ReplaceTask(updated);
            SetStatus(Friendly(operation) + " completed.");
            await ShowSelectedAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetStatus(Friendly(operation) + " failed: " + ex.Message, error: true);
            await RefreshAsync();
        }
    }

    private void ReplaceTask(TaskItem task)
    {
        for (var index = 0; index < tasks.Items.Count; index++)
        {
            if (tasks.Items[index] is not TaskChoice existing || existing.Task.Id != task.Id) continue;
            var replacement = new TaskChoice(task);
            tasks.Items[index] = replacement;
            tasks.SelectedItem = replacement;
            return;
        }
        var added = new TaskChoice(task);
        tasks.Items.Insert(0, added);
        tasks.SelectedItem = added;
    }

    private async Task<T> CommandAsync<T>(string operation, object arguments, CancellationToken cancellationToken)
    {
        var current = service ?? throw new InvalidOperationException("The collaboration service connection is unavailable.");
        var result = await current.ExecuteServiceCommandAsync(operation, arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? operation + " failed.");
        return result.Data.Deserialize<T>(Json) ?? throw new InvalidDataException(operation + " returned no usable data.");
    }

    private TaskItem? Selected => (tasks.SelectedItem as TaskChoice)?.Task;

    private void ClearSelection()
    {
        details.Text = "Select a collaboration task to inspect its bounded status, activity, and result.";
        events.Text = "";
        openSource.IsEnabled = false;
        openTarget.IsEnabled = false;
        cancel.IsEnabled = false;
        reconcile.IsEnabled = false;
        adopt.IsEnabled = false;
        steering.IsEnabled = false;
        sendSteering.IsEnabled = false;
    }

    private void SetStatus(string text, bool error = false)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty,
            error ? DesktopThemeResourceKeys.ErrorTextBrush : DesktopThemeResourceKeys.MutedTextBrush);
    }

    private static Button ActionButton(string label, string automationId)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 5), MinHeight = 30 };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static string Friendly(string value)
    {
        var tail = value[(value.LastIndexOf('.') + 1)..].Replace('-', ' ').Replace('_', ' ');
        return tail.Length == 0 ? value : char.ToUpperInvariant(tail[0]) + tail[1..];
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        host = null;
        service = null;
        workspace = null;
    }

    private sealed record TaskChoice(TaskItem Task)
    {
        public override string ToString()
        {
            var text = Task.Request.Instruction.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 72) text = text[..69] + "…";
            return $"{Task.Status} · {text}";
        }
    }

    private sealed class TaskListPage
    {
        public IReadOnlyList<TaskItem> Tasks { get; init; } = [];
    }

    private sealed class TaskEventPage
    {
        public IReadOnlyList<TaskEventItem> Events { get; init; } = [];
    }

    private sealed class TaskEventItem
    {
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset At { get; init; }
    }

    private sealed class ResultPage
    {
        public string Content { get; init; } = "";
        public bool Complete { get; init; }
    }

    private sealed class TaskItem
    {
        public string Id { get; init; } = "";
        public TaskRequest Request { get; init; } = new();
        public string AttemptId { get; init; } = "";
        public string Status { get; init; } = "";
        public long Revision { get; init; }
        public long? TargetRunRevision { get; init; }
        public string? WaitingReason { get; init; }
        public string? Error { get; init; }
        public DateTimeOffset? AdoptedAt { get; init; }
    }

    private sealed class TaskRequest
    {
        public string SourceConversationId { get; init; } = "";
        public string TargetConversationId { get; init; } = "";
        public string Instruction { get; init; } = "";
    }
}
