using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Lazy, independently saved plugin draft for one captured Properties target.</summary>
internal sealed class ConversationPluginSettingsEditor : UserControl, IDisposable
{
    private readonly DesktopHostedConversationSettings contribution;
    private readonly DesktopConversationSettingsContext context;
    private readonly Func<bool> current;
    private readonly ConversationPluginCancellation lifetime;
    private readonly ContentControl editorContent = new();
    private IDesktopConversationSettingsEditor? editor;
    private bool opening;
    private bool saving;
    private bool invalidBinding;
    private bool uncertain;
    private bool acceptedPending;
    private bool disposed;
    internal Button OpenButton { get; }
    internal Button SaveButton { get; }
    internal Button CancelButton { get; }
    internal TextBlock Feedback { get; } = Ui.Text("", 13);
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
    internal bool HasEditor => editor is not null;
    internal Task PendingCleanup { get; private set; } = Task.CompletedTask;
    internal DesktopConversationSettingsContext Target => context;

    internal ConversationPluginSettingsEditor(DesktopHostedConversationSettings contribution,
        DesktopConversationSettingsContext context, Func<bool> current, CancellationToken ownerLifetime)
    {
        this.contribution = contribution; this.current = current;
        lifetime = new ConversationPluginCancellation(null, ownerLifetime, context.AvailabilityToken);
        this.context = context with { AvailabilityToken = lifetime.Token };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Ui.Text(contribution.Title, 17));
        OpenButton = Ui.Button("Configure", () => { _ = OpenAsync(); });
        SaveButton = Ui.Button("Save plugin settings", () => { _ = SaveAsync(); }, true);
        CancelButton = Ui.Button("Cancel edits", CancelEdits);
        var identity = contribution.PluginId + "." + contribution.Id;
        var details = new StackPanel();
        details.Children.Add(Ui.Text("Plugin ID", 12, Ui.Muted));
        var pluginId = Ui.Input(contribution.PluginId); pluginId.IsReadOnly = true;
        AutomationProperties.SetName(pluginId, "Plugin ID");
        details.Children.Add(pluginId);
        details.Children.Add(Ui.Text("Settings section ID", 12, Ui.Muted));
        var sectionId = Ui.Input(contribution.Id); sectionId.IsReadOnly = true;
        AutomationProperties.SetName(sectionId, "Settings section ID");
        details.Children.Add(sectionId);
        var detailPanel = new Expander { Header = "Details", Content = details, IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(detailPanel, "ConversationPluginSettings.Details." + identity);
        panel.Children.Add(detailPanel);
        AutomationProperties.SetAutomationId(this, "ConversationPluginSettings." + identity);
        AutomationProperties.SetAutomationId(OpenButton, "ConversationPluginSettings.Open." + identity);
        AutomationProperties.SetAutomationId(SaveButton, "ConversationPluginSettings.Save." + identity);
        AutomationProperties.SetAutomationId(CancelButton, "ConversationPluginSettings.Cancel." + identity);
        AutomationProperties.SetAutomationId(Feedback, "ConversationPluginSettings.Feedback." + identity);
        panel.Children.Add(Ui.Bar(OpenButton, SaveButton, CancelButton));
        editorContent.Margin = new Thickness(0, 12, 0, 8);
        panel.Children.Add(editorContent); panel.Children.Add(Feedback);
        Content = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
            Background = Ui.Surface, Margin = new Thickness(0, 10, 0, 0), Child = panel };
        RefreshBinding();
    }

    internal void RefreshBinding()
    {
        if (disposed) return;
        if (!current() || lifetime.IsCancellationRequested)
        {
            if (!invalidBinding)
            {
                invalidBinding = true; lifetime.Cancel();
                SetFeedback("This settings target is no longer available. Close and reopen Properties.", true);
            }
        }
        if (lifetime.CancellationFailure is not null)
            SetFeedback("A plugin cancellation callback failed. Cleanup will continue; reopen Properties before editing again.", true);
        var available = !invalidBinding && !uncertain && !acceptedPending;
        OpenButton.IsEnabled = available && !opening && !saving && editor is null;
        SaveButton.IsEnabled = available && !opening && !saving && editor is not null;
        CancelButton.IsEnabled = !opening && !saving && editor is not null;
        if (editorContent.Content is FrameworkElement view) view.IsEnabled = available && !saving;
        OpenButton.Visibility = editor is null ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = CancelButton.Visibility = editor is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal Task OpenAsync()
    {
        if (opening || saving || disposed || editor is not null || uncertain || acceptedPending) return PendingLoad;
        RefreshBinding();
        if (invalidBinding) return PendingLoad;
        PendingLoad = OpenCoreAsync();
        return PendingLoad;
    }

    private async Task OpenCoreAsync()
    {
        opening = true; RefreshBinding(); SetFeedback("Loading plugin settings…");
        IDesktopConversationSettingsEditor? created = null;
        try
        {
            using var requestLifetime = new ConversationPluginCancellation(TimeSpan.FromSeconds(30), lifetime.Token);
            created = await contribution.OpenAsync(context, requestLifetime.Token);
            requestLifetime.Token.ThrowIfCancellationRequested();
            if (disposed || !current() || lifetime.IsCancellationRequested)
                throw new OperationCanceledException("The Properties target changed.");
            if (created is null) throw new InvalidOperationException("The plugin returned no settings editor.");
            // The runtime validates the fresh view and loaded instance before exposing this wrapper.
            editorContent.Content = created.View;
            editor = created; created = null;
            SetFeedback("Edits are a local draft. Save applies only this plugin's settings.");
        }
        catch (Exception error) when (Recoverable(error))
        {
            SetFeedback("Plugin settings could not open. " + error.Message, true);
        }
        finally
        {
            if (created is not null) Release(created);
            opening = false; RefreshBinding();
        }
    }

    internal Task SaveAsync()
    {
        if (saving || opening || disposed || editor is null || uncertain || acceptedPending) return PendingSave;
        RefreshBinding();
        if (invalidBinding) return PendingSave;
        PendingSave = SaveCoreAsync(editor);
        return PendingSave;
    }

    private async Task SaveCoreAsync(IDesktopConversationSettingsEditor captured)
    {
        saving = true; RefreshBinding(); SetFeedback("Saving this plugin's settings…");
        var dispatched = false;
        try
        {
            using var requestLifetime = new ConversationPluginCancellation(TimeSpan.FromSeconds(30), lifetime.Token);
            requestLifetime.Token.ThrowIfCancellationRequested();
            if (!current()) { RefreshBinding(); return; }
            dispatched = true;
            var result = await captured.SaveAsync(requestLifetime.Token);
            if (result is null || !Enum.IsDefined(result.State) ||
                result.Message?.Length > DesktopConversationSettingsContract.MaximumStatusChars)
                throw new InvalidOperationException("The plugin returned an invalid save outcome.");
            uncertain = result.State == DesktopOperationState.Uncertain;
            acceptedPending = result.State == DesktopOperationState.Accepted;
            var prefix = result.State switch
            {
                DesktopOperationState.Completed => "Plugin settings saved.",
                DesktopOperationState.Accepted => "Plugin settings save accepted; persistence is not yet confirmed. Reopen Properties and inspect the saved state before another save.",
                DesktopOperationState.Rejected => "Plugin settings were rejected.",
                DesktopOperationState.Failed => "Plugin settings save failed.",
                _ => "Save acceptance is unconfirmed. Reopen Properties and inspect the saved settings before retrying."
            };
            if (disposed || !current() || lifetime.IsCancellationRequested)
                prefix += " The Properties target is no longer available.";
            SetFeedback(prefix + (string.IsNullOrWhiteSpace(result.Message) ? "" : " " + result.Message),
                result.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain);
        }
        catch (Exception error) when (Recoverable(error))
        {
            uncertain = dispatched;
            SetFeedback(dispatched
                ? "Save acceptance is unconfirmed. Reopen Properties and inspect the saved settings before retrying. " + error.Message
                : "Plugin settings were not sent. " + error.Message, true);
        }
        finally { saving = false; RefreshBinding(); }
    }

    internal void CancelEdits()
    {
        if (disposed || saving || opening || editor is null) return;
        var captured = editor; editor = null; editorContent.Content = null;
        var released = Release(captured);
        if (released) SetFeedback(uncertain || acceptedPending
            ? "Unsaved local edits discarded. The earlier save remains unconfirmed; reopen Properties before another save."
            : "Unsaved local edits discarded.");
        RefreshBinding();
    }

    private bool Release(IDesktopConversationSettingsEditor captured)
    {
        var released = true;
        try { captured.Cancel(); }
        catch (Exception error) when (Recoverable(error)) { released = false; SetFeedback("The plugin could not discard its local draft. " + error.Message, true); }
        try { captured.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { released = false; SetFeedback("The plugin could not finish editor cleanup. " + error.Message, true); }
        return released;
    }

    private void SetFeedback(string message, bool error = false)
    {
        Feedback.Text = message.Length <= DesktopConversationSettingsContract.MaximumStatusChars
            ? message : message[..(DesktopConversationSettingsContract.MaximumStatusChars - 1)] + "…";
        Feedback.Foreground = error ? Ui.ErrorText : Ui.Muted;
    }

    private static bool Recoverable(Exception error) =>
        error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (lifetime.CancellationFailure is not null)
        {
            SetFeedback("A plugin cancellation callback failed. Cleanup continued.", true);
            Console.Error.WriteLine("Plugin settings cancellation callback failed; cleanup continued.");
        }
        OpenButton.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        // A close can occur during the modal dispatcher pump. Settle the captured operation before
        // releasing its editor/availability token; cancellation is not proof a save was rolled back.
        PendingCleanup = DisposeAfterOperationsAsync(Task.WhenAll(PendingLoad, PendingSave));
    }

    private async Task DisposeAfterOperationsAsync(Task pending)
    {
        try { await pending; }
        catch (Exception error) when (Recoverable(error)) { SetFeedback("Plugin settings cleanup followed a failed operation. " + error.Message, true); }
        finally
        {
            var captured = editor; editor = null; editorContent.Content = null;
            if (captured is not null) Release(captured);
            lifetime.Dispose();
        }
    }
}
