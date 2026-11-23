namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private CancellationTokenSource? playbackCancellation;
    private Task? playbackWork;
    private async Task StopPlaybackAsync()
    {
        playbackCancellation?.Cancel();
        if (playbackWork is { } work)
            try { await work; } catch (OperationCanceledException) { }
    }
    private async Task PlayRecordingAsync()
    {
        if (!CallbacksAvailable) return;
        if (playbackWork is { IsCompleted: false }) { await StopPlaybackAsync(); return; }
        if (session?.IsCapturing == true || session?.RetainedAudioPath is not { } path) return;
        using var attempt = new CancellationTokenSource(); playbackCancellation = attempt;
        try
        {
            playbackWork = playbackFactory().PlayAsync(path, attempt.Token);
            RefreshTargetViews(); await playbackWork;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportControlError(error); }
        finally
        {
            if (ReferenceEquals(playbackCancellation, attempt)) { playbackCancellation = null; playbackWork = null; }
            RefreshTargetViews();
        }
    }
}
