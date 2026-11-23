using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.WorkspacePlugin;

/// <summary>Native workspace actions with one centrally stored global display preference.</summary>
public sealed class WorkspacePlugin : IDesktopPlugin, IDesktopMountPlugin, IDesktopAgentTemplatePlugin
{
    private IDesktopPluginHost? host;
    private readonly TextBlock workspace = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock workspaceTree = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private DesktopMessageSnapshot? lastAnswer;
    private bool plainResponses;
    private PackagePreferenceSession<WorkspacePreferences>? preferences;
    private bool disposed;
    // Required API-v1 members stay present for binary compatibility. New code uses the descriptor-first registry.
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(Descriptor("workspace", DesktopMountSlots.SidebarTabs, 100), "Workspace", CreatePanel),
        new DesktopMountView(Descriptor("project-branch", DesktopMountSlots.WorkspaceTree, 100), "Project details", CreateWorkspaceTree),
        new DesktopMountAction(Descriptor("quote-draft", DesktopMountSlots.ComposerActions, 100), "Quote draft", QuoteDraftAsync,
            "Wrap the current draft in Markdown quotation markers; does not send it."),
        new DesktopMountAction(Descriptor("copy-answer", DesktopMountSlots.ComposerActions, 110), "Copy last answer",
            CopyLastAnswerAsync),
        new DesktopMountMessageDecoration(Descriptor("copy-original", DesktopMountSlots.MessageDecorations, 100),
            DecorateMessage)
    ];
    public IReadOnlyList<DesktopAgentTemplate> AgentTemplates =>
    [
        new("project-planner", "Project planning helper", "helper",
            "Develop a bounded implementation or writing plan through explicit assumptions, choices, dependencies, and verification steps.",
            "Help Cerebral build a concrete plan. Ask only questions whose answers materially change the result. Offer clear alternatives when a choice matters, keep accepted decisions stable, identify dependencies and verification, and return a concise plan that can be executed or revised. Do not grant yourself tools or paths.")
    ];

    public void Initialize(IDesktopPluginHost host)
    {
        this.host = host;
        preferences = new(host, WorkspaceSettingsPolicy.PluginId, WorkspaceSettingsPolicy.PackageVersion,
            new WorkspaceSettingsPolicy(), WorkspaceSettingsPolicy.Decode, WorkspaceSettingsPolicy.Encode, () => new());
        _ = ReadPreferencesAsync();
        host.ConversationChanged += OnConversationChanged;
        host.LeadMessageCommitted += OnLeadMessage;
        UpdateWorkspace();
    }

    private FrameworkElement CreatePanel()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(workspace);
        var plain = new CheckBox { Content = "Display assistant replies as plain text", IsChecked = plainResponses, Margin = new Thickness(0, 0, 0, 12) };
        // This checkbox edits a draft. Only acknowledged central state changes presentation.
        panel.Children.Add(plain);
        var copy = new Button { Content = "Copy project path", HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += async (_, _) =>
        {
            if (host?.GetConversation().ProjectPath is { Length: > 0 } path)
                await CopyTextAsync(path, "Copied project path.", CancellationToken.None);
            else host?.ReportStatus("This conversation has no project folder.");
        };
        panel.Children.Add(copy);
        panel.Children.Add(new TextBlock { Text = "The quote action edits the draft only. Message copy always uses the original content.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var editor = new PackagePreferenceEditor<WorkspacePreferences>(preferences!,
            () => new(plain.IsChecked == true), value => plain.IsChecked = value.PlainResponses,
            null, "Workspace", ApplyPreferences, "Preferences saved. The display preference is active.");
        return editor.Create(panel);
    }

    private void ApplyPreferences(WorkspacePreferences value)
    {
        if (disposed) return;
        plainResponses = value.PlainResponses; host?.RefreshPresentation();
    }
    private async Task ReadPreferencesAsync()
    {
        try
        {
            var value = await preferences!.ReadAsync();
            if (disposed) return;
            if (workspace.Dispatcher.CheckAccess()) ApplyPreferences(value);
            else await workspace.Dispatcher.InvokeAsync(() => ApplyPreferences(value));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!disposed) host?.ReportStatus("Workspace display preferences are unavailable. Open Workspace and load saved preferences before saving."); }
    }

    private FrameworkElement CreateWorkspaceTree() => new Border
    {
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(4),
        Child = workspaceTree
    };

    private static DesktopMountDescriptor Descriptor(string id, string slot, int order,
        IReadOnlyList<string>? messageRoles = null) => new(id, slot)
    {
        Order = order,
        Applicability = new()
        {
            Surfaces = DesktopMountSurfaces.Orchestrator,
            MessageRoles = messageRoles ?? []
        }
    };

    private async Task QuoteDraftAsync(CancellationToken cancellationToken)
    {
        if (disposed || host is null) return;
        var draft = host.GetDraft();
        var quote = string.Join(Environment.NewLine, draft.Text.Replace("\r\n", "\n").Split('\n').Select(line => "> " + line));
        var result = await host.ReplaceDraftAsync(new(draft.Binding, draft.Revision, quote), cancellationToken);
        if (!disposed) host?.ReportStatus(result.State is DesktopOperationState.Completed ? "Draft quoted. Review it before sending." : result.Message ?? result.State.ToString());
    }

    // The original API-v1 presentation callback remains intentionally exercised beside descriptor-first mounts.
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) =>
        new(RenderPlainAssistant(message), null);

    private FrameworkElement? RenderPlainAssistant(DesktopMessageSnapshot message)
    {
        if (disposed || message.Truncated || !plainResponses) return null;
        return new TextBox { Text = message.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
    }

    private FrameworkElement? DecorateMessage(DesktopMessageSnapshot message)
    {
        if (disposed || message.Truncated) return null;
        var copy = new Button { Content = "Copy original", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        copy.Click += async (_, _) => { if (!disposed) await CopyTextAsync(message.Text, "Copied original answer.", CancellationToken.None); };
        return copy;
    }

    private async Task CopyLastAnswerAsync(CancellationToken cancellationToken)
    {
        if (host is null || lastAnswer is null || lastAnswer.Binding != host.GetConversation().Binding || lastAnswer.Truncated)
        { host?.ReportStatus("No complete answer has arrived in this conversation while this plugin was enabled."); return; }
        await CopyTextAsync(lastAnswer.Text, "Copied last answer.", cancellationToken);
    }
    private async Task CopyTextAsync(string text, string status, CancellationToken cancellationToken)
    {
        if (host is IDesktopClipboardHost clipboard)
        {
            var result = await clipboard.SetClipboardTextAsync(text, cancellationToken);
            if (result.State is not (DesktopOperationState.Accepted or DesktopOperationState.Completed))
            {
                host.ReportStatus(result.Message ?? "The clipboard request failed.");
                return;
            }
        }
        else Clipboard.SetText(text);
        host?.ReportStatus(status);
    }
    private void OnConversationChanged(object? sender, DesktopConversationChangedEventArgs args) { if (lastAnswer?.Binding != args.Conversation.Binding) lastAnswer = null; UpdateWorkspace(); }
    private void OnLeadMessage(object? sender, DesktopLeadMessageEventArgs args) => lastAnswer = args.Message;
    private void UpdateWorkspace()
    {
        if (host is null) return;
        var current = host.GetConversation();
        workspace.Text = current.Title + Environment.NewLine + (current.ProjectPath ?? "No project folder selected");
        workspaceTree.Text = current.Title + Environment.NewLine + (current.ProjectPath ?? "No project folder selected");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (host is not null)
        {
            host.ConversationChanged -= OnConversationChanged;
            host.LeadMessageCommitted -= OnLeadMessage;
        }
        preferences?.Dispose(); preferences = null;
        host = null; lastAnswer = null;
    }
}
