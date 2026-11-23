using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Cerebral.Client;

namespace Cerebral.Orchestrator;

internal sealed class ChatPluginInstallPermissionEditor : UserControl, IDisposable
{
    private readonly ChatPluginInstallPermissionEditorState state;
    private readonly CancellationTokenSource lifetime;
    private readonly CheckBox allow = new()
    {
        Content = "Automatically install and update native plugins presented in this chat",
        IsChecked = false, IsEnabled = false, Margin = new Thickness(0, 8, 0, 8)
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button refresh = new() { Content = "Refresh permission", Margin = new Thickness(0, 6, 8, 0) };
    private readonly Button save = new() { Content = "Save permission", IsEnabled = false, Margin = new Thickness(0, 6, 0, 0) };
    private Task pending = Task.CompletedTask;
    private bool disposed;
    private bool loadedOnce;
    internal Task PendingOperations => pending;

    internal ChatPluginInstallPermissionEditor(ChatPluginInstallPermissionEditorState state, CancellationToken token)
    {
        this.state = state; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Native plugin installation", FontSize = 16 });
        panel.Children.Add(new TextBlock
        {
            Text = "Native plugins run with your Windows user privileges. This choice applies only to new requests in this chat. " +
                "Profile, tool, path, source and package checks still apply. It does not authorize control testing, repairs, " +
                "additional filesystem roots, machine permissions or credentials. Saving this as off blocks future installation authorizations; " +
                "an installation that already passed its final authorization check may finish.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
        });
        panel.Children.Add(allow); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(refresh); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        AutomationProperties.SetAutomationId(allow, "ChatPluginAutomaticInstall");
        AutomationProperties.SetAutomationId(save, "ChatPluginInstallPermissionSave");
        allow.Click += (_, _) => { if (state.CanSave) state.DesiredAllow = allow.IsChecked == true; };
        refresh.Click += (_, _) => Start(load: true);
        save.Click += (_, _) => Start(load: false);
        Loaded += (_, _) => { if (!loadedOnce) { loadedOnce = true; Start(load: true); } };
        Render();
    }

    private void Start(bool load)
    {
        if (disposed || !pending.IsCompleted) return;
        state.DesiredAllow = allow.IsChecked == true;
        pending = UpdateAsync(load);
    }
    private async Task UpdateAsync(bool load)
    {
        var operation = load ? state.LoadAsync(lifetime.Token) : state.SaveAsync(lifetime.Token);
        Render();
        await operation;
        if (!disposed) Render();
    }
    internal void RefreshBinding() => Render();
    private void Render()
    {
        allow.IsChecked = state.DesiredAllow;
        allow.IsEnabled = state.CanSave; save.IsEnabled = state.CanSave;
        refresh.IsEnabled = !state.Busy; status.Text = state.Status;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); lifetime.Dispose();
    }
}

internal static class ChatPluginInstallOwnerUi
{
    internal static ChatPluginInstallPermissionEditor Create(string id, string profileId, string? projectId,
        CerebralClient capturedOwner, Func<bool> current, CancellationToken token)
    {
        // These delegates close over the owner-selected saved chat. No editable/model target field exists.
        var state = new ChatPluginInstallPermissionEditorState(id, profileId, projectId, current,
            cancellation => capturedOwner.GetAsync<System.Text.Json.JsonElement>("conversations.plugin-install.get",
                new { id }, cancellationToken: cancellation),
            async (revision, allow, cancellation) =>
            {
                if (!current()) throw new InvalidOperationException("Reopen Chat Properties after its binding changes.");
                return await capturedOwner.CommandAsync("conversations.plugin-install.save",
                    new { id, expectedRevision = revision, allowAutomaticInstall = allow }, cancellationToken: cancellation);
            });
        return new(state, token);
    }
}
