using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private ContentControl? conversationPrimarySurface;
    private Grid? defaultConversationSurface;
    private TabItem? conversationFallbackTab;
    private CancellationTokenSource? conversationWorkspaceMount;
    private long conversationWorkspaceMountVersion;
    private readonly Dictionary<string, ConversationWorkspaceBinding> recentConversationWorkspaceBindings = new(StringComparer.Ordinal);
    private readonly Queue<string> recentConversationWorkspaceOrder = new();

    private void InitializeConversationWorkspaceSurface(Grid center)
    {
        defaultConversationSurface = new Grid { ClipToBounds = true };
        // The host Chat page supplies the decorative base beneath chrome and content.
        // A selected plugin workspace keeps its own surface and may cover or hide that layer.
        defaultConversationSurface.Children.Add(transcript);
        InitializeNativeChat(defaultConversationSurface);
        conversationPrimarySurface = new ContentControl
        {
            Content = defaultConversationSurface,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(conversationPrimarySurface, "ConversationPrimarySurface");
        AutomationProperties.SetName(conversationPrimarySurface, "Conversation or plugin workflow workspace");
        var workArea = new Grid { ClipToBounds = true };
        workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        workArea.Children.Add(conversationPrimarySurface);
        InitializeTeamMonitor(workArea);
        Grid.SetRow(workArea, 2); center.Children.Add(workArea);
        Closed += (_, _) => { conversationWorkspaceMount?.Cancel(); conversationWorkspaceMount?.Dispose(); };
    }

    private async Task RefreshConversationWorkspaceAsync()
    {
        Dispatcher.VerifyAccess();
        if (conversationPrimarySurface is null || defaultConversationSurface is null) return;
        conversationWorkspaceMount?.Cancel(); conversationWorkspaceMount?.Dispose();
        conversationWorkspaceMount = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        var token = conversationWorkspaceMount.Token;
        var version = ++conversationWorkspaceMountVersion;
        var conversation = PluginConversation();
        if (conversation.Binding.SessionId is not { } conversationId)
        {
            ShowDefaultConversationSurface(); return;
        }

        ConversationWorkspaceBinding? binding;
        try
        {
            var response = await Client.CommandAsync("conversations.workspace.get", new { conversationId }, cancellationToken: token);
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "Conversation workspace lookup failed.");
            binding = response.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null : response.Data.Deserialize<ConversationWorkspaceBinding>(CerebralJson.Options);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (version != conversationWorkspaceMountVersion || conversation.Binding != PluginBinding()) return;
            // A brand-new unsaved chat has no service row yet. It remains the ordinary conversation surface.
            if (error.Message.Contains("Conversation not found", StringComparison.OrdinalIgnoreCase))
            { ShowDefaultConversationSurface(); return; }
            ShowConversationWorkspaceUnavailable("Workflow layout unavailable", error.Message); return;
        }
        if (version != conversationWorkspaceMountVersion || token.IsCancellationRequested || conversation.Binding != PluginBinding()) return;
        if (binding is null) { ShowDefaultConversationSurface(); return; }
        RememberConversationWorkspaceBinding(binding);

        var provider = desktopPlugins?.ConversationWorkspaces.FirstOrDefault(item =>
            item.PluginId == binding.PluginId && item.Id == binding.SurfaceId);
        if (provider is null)
        {
            ShowConversationWorkspaceUnavailable("Workflow plugin unavailable",
                $"This conversation uses {binding.PluginId} / {binding.SurfaceId}. Enable or repair that plugin to restore its workspace. The saved conversation remains available.");
            return;
        }

        FrameworkElement view;
        try
        {
            var desktopBinding = new DesktopConversationWorkspaceBinding(binding.ConversationId, binding.PluginId,
                binding.SurfaceId, binding.ResourceId, binding.Revision, binding.UpdatedAt);
            view = await provider.CreateViewAsync(new(conversation, desktopBinding), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (version != conversationWorkspaceMountVersion || conversation.Binding != PluginBinding()) return;
            ShowConversationWorkspaceUnavailable(provider.Title + " could not open", error.Message); return;
        }
        if (version != conversationWorkspaceMountVersion || token.IsCancellationRequested || conversation.Binding != PluginBinding()) return;
        ShowPluginConversationWorkspace(provider.Title, view);
    }

    private void ShowPluginConversationWorkspace(string title, FrameworkElement view)
    {
        if (conversationPrimarySurface is null || defaultConversationSurface is null) return;
        DetachDefaultConversationSurface();
        conversationPrimarySurface.Content = null;
        var tabs = new TabControl { BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        var workspace = Ui.TabPage(title, view, new Thickness(10, 6, 10, 6));
        conversationFallbackTab = Ui.TabPage("Conversation", defaultConversationSurface, new Thickness(10, 6, 10, 6));
        AutomationProperties.SetAutomationId(workspace, "ConversationWorkspace.Plugin");
        AutomationProperties.SetAutomationId(conversationFallbackTab, "ConversationWorkspace.Conversation");
        tabs.Items.Add(workspace); tabs.Items.Add(conversationFallbackTab); tabs.SelectedItem = workspace;
        conversationPrimarySurface.Content = tabs;
    }

    private void ShowConversationWorkspaceUnavailable(string title, string detail)
    {
        // The ordinary conversation remains usable while a plugin layout is still loading,
        // disabled or temporarily unavailable. Surface the diagnostic in the shared status line
        // instead of replacing Chat with a dead workflow tab.
        ShowDefaultConversationSurface();
        SetStatus(title + ": " + detail, true);
    }

    private void ShowDefaultConversationSurface()
    {
        if (conversationPrimarySurface is null || defaultConversationSurface is null) return;
        DetachDefaultConversationSurface(); conversationPrimarySurface.Content = null;
        conversationPrimarySurface.Content = defaultConversationSurface;
    }

    private void DetachDefaultConversationSurface()
    {
        if (conversationFallbackTab is not null) conversationFallbackTab.Content = null;
        conversationFallbackTab = null;
        if (defaultConversationSurface?.Parent is ContentControl parent) parent.Content = null;
    }

    private void RememberConversationWorkspaceBinding(ConversationWorkspaceBinding binding)
    {
        if (!recentConversationWorkspaceBindings.ContainsKey(binding.ConversationId))
            recentConversationWorkspaceOrder.Enqueue(binding.ConversationId);
        recentConversationWorkspaceBindings[binding.ConversationId] = binding;
        while (recentConversationWorkspaceOrder.Count > 8)
        {
            var old = recentConversationWorkspaceOrder.Dequeue();
            if (old != binding.ConversationId) recentConversationWorkspaceBindings.Remove(old);
        }
    }
}
