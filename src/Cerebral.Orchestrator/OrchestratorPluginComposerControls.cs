using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly ComposerRefinementSlot composerRefinementLeases = new();
    private ComposerDraftPresentation? composerDraftPresentation;
    private static bool IsPromptReviewMode(DesktopHostedInputMode mode) =>
        mode.PluginId == "pegasus.prompt-review" && mode.Id == "understanding";

    private ComposerRefinementLease BeginComposerRefinement(DesktopSubmission submission, CancellationToken cancellationToken)
    {
        ReleaseComposerRefinement();
        if (submission.Binding != PluginBinding())
            throw new InvalidOperationException("The composer owner changed before refinement.");
        return composerRefinementLeases.Begin(prompt, () => new(PluginBinding(), pluginDraftRevision, prompt.Text),
            () =>
            {
                ClearDesktopInputGuidance();
                SetStatus("The draft changed during refinement. Current text was preserved; select Refine again when ready.", true);
            }, cancellationToken);
    }

    private Task<DesktopOperationResult> ApplyPluginDraftWithReviewLease(DesktopDraftUpdate update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (update.Presentation is { } presentation)
        {
            if (update.Binding != PluginBinding() || update.ExpectedRevision != pluginDraftRevision ||
                composerRefinementLeases.Current is not null || composerCompositionActive ||
                !prompt.IsVisible || !prompt.IsEnabled || prompt.IsReadOnly)
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,
                    "The composer changed, is hidden, locked, or is being edited by another input operation."));
            composerDraftPresentation ??= new(prompt);
            return Task.FromResult(composerDraftPresentation.Apply(update.Text, presentation));
        }
        if (composerRefinementLeases.Current is { } lease) return Task.FromResult(lease.Apply(update));
        if (prompt.IsReadOnly) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "The composer is locked."));
        prompt.Text = update.Text;
        return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
    }

    private void ReleaseComposerRefinement(ComposerRefinementLease? expected = null)
    {
        composerRefinementLeases.Release(expected);
    }

    private void MountVisibleComposerInputTools()
    {
        if (composerDictationTools is null || composerTextEntryTools is null) return;
        if (composerTextEntryTools.Parent is Panel parent) parent.Children.Remove(composerTextEntryTools);
        composerDictationTools.Content = composerTextEntryTools;
        System.Windows.Automation.AutomationProperties.SetName(composerTextEntryTools, "Text-entry actions for this message");
    }
}
