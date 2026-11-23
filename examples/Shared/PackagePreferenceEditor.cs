using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;

namespace Cerebral.PackageSettings;

/// <summary>Explicit load/prepare/save workflow shared by preference panels; opening never saves or reads legacy data.</summary>
internal sealed class PackagePreferenceEditor<T>(PackagePreferenceSession<T> session, Func<T> draft, Action<T> hydrate,
    Func<T>? prepareLegacy, string prefix, Action<T>? committed = null,
    string savedMessage = "Preferences saved. The next recording uses them.") where T : class
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox pending = new() { MinWidth = 200, DisplayMemberPath = nameof(PluginSettingsDeferredDocument.Id) };
    private readonly Button save = new() { Content = "Save preferences", IsEnabled = false };
    private readonly List<Button> commands = [];
    private FrameworkElement? fields;
    private FrameworkElement? pendingRow;
    private CancellationTokenSource mount = new();
    private string? selectedPending;
    internal Task Work { get; private set; } = Task.CompletedTask;
    internal FrameworkElement Create(FrameworkElement content, Func<T, T>? preparePaths = null)
    {
        fields = content; fields.IsEnabled = false;
        var toolbar = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
        content.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(status, prefix + "PreferenceStatus");
        toolbar.Children.Add(status);
        var actions = new WrapPanel();
        Add(actions, "Load saved preferences", prefix + "LoadPreferences", () => Run(ReloadAsync));
        if (prepareLegacy is not null)
            Add(actions, "Prepare existing local preferences", prefix + "PrepareLegacy", () => Prepare(prepareLegacy));
        if (preparePaths is not null)
            Add(actions, "Find installed runtime and model", prefix + "PreparePaths", () => Prepare(() => preparePaths(draft())));
        AutomationProperties.SetAutomationId(save, prefix + "SavePreferences");
        save.Margin = new Thickness(0, 0, 8, 8); save.Padding = new Thickness(10, 5, 10, 5);
        save.Click += (_, _) => Run(SaveAsync); commands.Add(save); actions.Children.Add(save);
        toolbar.Children.Add(actions);
        var retained = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetAutomationId(pending, prefix + "PendingPreferences");
        retained.Children.Add(pending);
        Add(retained, "Prepare selected retained preferences", prefix + "PreparePending", () =>
        {
            selectedPending = null;
            if (pending.SelectedItem is not PluginSettingsDeferredDocument selected) return;
            try { hydrate(session.PreparePending(selected.Id)); selectedPending = selected.Id; status.Text = "Retained preferences prepared. Review them and save to apply."; }
            catch (Exception error) when (error is not OutOfMemoryException) { status.Text = "This retained preference format is unsupported; it is preserved."; }
        });
        pendingRow = retained;
        toolbar.Children.Add(retained); toolbar.Children.Add(content);
        toolbar.Unloaded += (_, _) => mount.Cancel();
        toolbar.Loaded += async (_, _) =>
        {
            if (!mount.IsCancellationRequested) return;
            mount.Dispose(); mount = new();
            var currentMount = mount;
            await Work;
            if (ReferenceEquals(mount, currentMount) && !mount.IsCancellationRequested) Run(ReloadAsync);
        };
        Run(ReloadAsync);
        return toolbar;
    }
    private void Add(Panel panel, string label, string id, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 5, 10, 5) };
        AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action();
        commands.Add(button); panel.Children.Add(button);
    }
    private void Prepare(Func<T> preparation)
    {
        if (session.State is null) return;
        selectedPending = null;
        try { var value = preparation(); hydrate(value); selectedPending = null; status.Text = "Local preferences prepared. Review them and save to apply. Existing files are preserved."; }
        catch (Exception error) when (error is not OutOfMemoryException) { status.Text = "Local preferences could not be prepared. Existing files and your draft are preserved."; }
    }
    private void Run(Func<Task> operation)
    {
        if (!Work.IsCompleted) return;
        Work = ExecuteAsync(operation);
    }
    private async Task ExecuteAsync(Func<Task> operation)
    {
        foreach (var button in commands) button.IsEnabled = false;
        try { await operation(); }
        catch (OperationCanceledException) when (mount.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!mount.IsCancellationRequested) status.Text = "Saved preferences are unavailable or unsupported. Your draft is preserved. Load saved preferences before saving."; }
        finally
        {
            if (!mount.IsCancellationRequested)
            {
                foreach (var button in commands) button.IsEnabled = session.State is not null;
                commands[0].IsEnabled = true;
                if (fields is not null && session.State is not null) fields.IsEnabled = true;
                pending.ItemsSource = session.State?.Pending ?? [];
                if (pendingRow is not null) pendingRow.Visibility = session.State?.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
    private async Task ReloadAsync()
    {
        var value = await session.ReadAsync(mount.Token);
        if (mount.IsCancellationRequested) return;
        hydrate(value); selectedPending = null; committed?.Invoke(value);
        status.Text = "Saved preferences loaded. Edit or prepare local preferences, then save to apply.";
    }
    private async Task SaveAsync()
    {
        var value = await session.SaveAsync(draft(), selectedPending, mount.Token);
        if (mount.IsCancellationRequested) return;
        committed?.Invoke(value); selectedPending = null;
        status.Text = savedMessage;
    }
}
