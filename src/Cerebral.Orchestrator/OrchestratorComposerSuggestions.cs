using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly ComposerSuggestionState composerSuggestion = new();
    private ComposerSuggestionSurface? composerSuggestionSurface;
    private WorkflowDisplayedFinalResponse? composerSuggestionResponse;
    private string? observedComposerResponse;
    private bool composerCompositionActive;

    private FrameworkElement CreateComposerSuggestionSurface()
    {
        var surface = new ComposerSuggestionSurface(prompt, Ui.Muted);
        composerSuggestionSurface = surface;
        prompt.TextChanged += (_, _) => CancelComposerSuggestionForInput();
        prompt.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Tab or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt))
                CancelComposerSuggestionForInput();
            if (ComposerSuggestionInput.TryAccept(prompt, args.Key, Keyboard.Modifiers, composerCompositionActive,
                args.Handled, composerSuggestion, CurrentComposerSuggestionAnchor(), ComposerSuggestionAvailable())) args.Handled = true;
            RenderComposerSuggestion();
        };
        prompt.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => { composerCompositionActive = true; CancelComposerSuggestionForInput(); }), handledEventsToo: true);
        prompt.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, _) => InvalidateComposerSuggestion()), handledEventsToo: true);
        prompt.AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler((_, _) => composerCompositionActive = false), handledEventsToo: true);
        prompt.LostKeyboardFocus += (_, _) => composerCompositionActive = false;
        prompt.IsVisibleChanged += (_, _) => { if (prompt.IsVisible) ObserveComposerFinalResponse(); else SuspendComposerSuggestion(); };
        prompt.IsEnabledChanged += (_, _) => { if (!prompt.IsEnabled) InvalidateComposerSuggestion(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ObserveComposerFinalResponse(); else SuspendComposerSuggestion(); };
        Closed += (_, _) => InvalidateComposerSuggestion();
        // The same editor remains mounted; this supplies a local layer for brief changed-span effects.
        return new AdornerDecorator { Child = surface };
    }

    private bool ComposerSuggestionAvailable() => !Lifetime.IsCancellationRequested && IsVisible &&
        prompt.IsVisible && prompt.IsEnabled && !prompt.IsReadOnly && prompt.Text.Length == 0 &&
        !composerCompositionActive && !ComposerQuestionIsActive &&
        desktopInputGuidanceHost.Visibility != Visibility.Visible &&
        desktopPluginOwner?.ObserveMatchesOwner() == true && IsNativeChat &&
        nativeChat is { IsVisible: true, Snapshot: { MutationUncertain: false, Run: { IsTerminal: true } } };

    private ComposerSuggestionAnchor? CurrentComposerSuggestionAnchor() =>
        desktopPluginOwner is { } owner && composerSuggestionResponse is { } response &&
        response.ConversationId == CurrentConversationId
            ? new(owner, PluginBinding(), response.MessageId, pluginDraftRevision) : null;

    private void InvalidateComposerSuggestion()
    {
        composerSuggestion.Invalidate(); RenderComposerSuggestion();
    }

    private void CancelComposerSuggestionForInput()
    {
        observedComposerResponse = composerSuggestionResponse?.MessageId;
        InvalidateComposerSuggestion();
    }

    private void SuspendComposerSuggestion()
    {
        // A newly arrived final left unseen while Main was collapsed may run when it reopens.
        // Do not re-arm an already dispatched reply: cancellation can have an Unknown receipt.
        InvalidateComposerSuggestion();
    }

    private void RenderComposerSuggestion()
    {
        composerSuggestionSurface?.Show(ComposerSuggestionAvailable() ? composerSuggestion.Text : null);
    }

    private void ObserveComposerFinalResponse()
    {
        var response = nativeChat?.DisplayedFinalResponse;
        if (response?.ConversationId != CurrentConversationId) response = null;
        if (response is null || nativeChat?.Snapshot?.Run is not { IsTerminal: true })
        { InvalidateComposerSuggestion(); return; }
        if (observedComposerResponse == response.MessageId) return;
        composerSuggestionResponse = response;
        InvalidateComposerSuggestion(); // A newer response always supersedes an earlier completion.
        if (!pluginMessageBaseline || prompt.Text.Length != 0 || composerCompositionActive)
        { observedComposerResponse = response.MessageId; return; }
        if (!ComposerSuggestionAvailable() || desktopPlugins is not { } runtime ||
            CurrentComposerSuggestionAnchor() is not { } anchor) return;
        observedComposerResponse = response.MessageId;
        var request = composerSuggestion.Begin(anchor);
        _ = CompleteComposerSuggestionAsync(runtime, request, response.Text);
    }

    private async Task CompleteComposerSuggestionAsync(DesktopPluginRuntime runtime,
        ComposerSuggestionState.Request request, string responseText)
    {
        try
        {
            var text = await runtime.SuggestComposerAsync(responseText, request.Cancellation.Token);
            if (ReferenceEquals(runtime, desktopPlugins) && CurrentComposerSuggestionAnchor() is { } current)
                composerSuggestion.Complete(request, current, ComposerSuggestionAvailable(), text);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Advisory failure cannot change a draft or disturb normal input. Do not surface
            // provider text, a response body or plugin-private settings as a host diagnostic.
            if (!request.Cancellation.IsCancellationRequested && ReferenceEquals(runtime, desktopPlugins))
                SetStatus("A next-message suggestion is unavailable. Check the plugin's settings.");
        }
        finally { composerSuggestion.Finish(request); RenderComposerSuggestion(); }
    }
}
