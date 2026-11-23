using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal event EventHandler? FullExitRequested;

    private void RequestFullExit() => FullExitRequested?.Invoke(this, EventArgs.Empty);

    internal bool SemanticFullExitAvailable => FullExitRequested is not null && !Dispatcher.HasShutdownStarted;

    internal void RequestSemanticFullExit()
    {
        Dispatcher.VerifyAccess();
        if (!SemanticFullExitAvailable)
            throw new InvalidOperationException("Main's ordinary Exit handler is not ready. Use the tray Exit after startup finishes.");
        RequestFullExit();
    }

    internal async Task PrepareForFullExitAsync(CancellationToken token)
    {
        // Required content is saved before asking its Worker to stop. The tray freezes editing
        // for this preparation, so a later keystroke cannot invalidate the acknowledged save.
        if (CurrentConversationId is { } id) RememberComposerDraft(id, prompt.Text, flush: false);
        composerDraftTimer.Stop();
        if (!FlushComposerDrafts())
            throw new InvalidOperationException("Your draft could not be saved. Cerebral stayed open with your text.");
        if (mainLayoutOwner is { IsCurrent: true } owner && mainLayoutSession is { Supported: true, Blocked: false, Dirty: true })
        {
            try { await FlushMainLayoutAsync(owner); }
            catch (Exception error) { ReportMainLayoutSaveFailure(error); }
        }
        // Native settings and host callbacks may still need Worker and the UI dispatcher.
        // Retire them before either owner is stopped; a pending or failed retirement blocks Exit.
        await AwaitDesktopPluginsForFullExitAsync(token);
        // Explicit Exit controls the enrolled signed-in-user Worker, never a remote host or
        // the separately managed Windows service. X does not call this method.
        if (ServiceConnection.Transport != "installed-user-pipe") return;
        var worker = await LocalDevelopment.DiscoverInstalledUserPipeAsync(token);
        if (worker is null) return;
        await LocalDevelopment.StopPipeAsync(worker.Endpoint, token, worker.DataDirectory);
    }

    internal void ReportFullExitBlocked(Exception error) => SetStatus(
        "Cerebral stayed open because Exit could not finish. " + error.Message, true);
}
