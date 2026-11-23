using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Speech;

namespace Cerebral.AiDictationPlugin;

internal interface IDictationPlayback
{
    Task PlayAsync(string ownedWavPath, CancellationToken cancellationToken);
}

/// <summary>Lazy playback of the plugin's one retained local clip; invoked only by Play.</summary>
internal sealed class DictationPlayback : IDictationPlayback
{
    public async Task PlayAsync(string ownedWavPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(ownedWavPath) || !File.Exists(ownedWavPath))
            throw new IOException("The one-off recording is no longer available. Record a new clip.");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var player = new MediaPlayer();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler ended = (_, _) => finished.TrySetResult();
        EventHandler<ExceptionEventArgs> failed = (_, _) => finished.TrySetException(new IOException("The recording could not be played. Check the Windows output device."));
        player.MediaEnded += ended; player.MediaFailed += failed;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(NemoCliSpeechProvider.MaximumAudioSeconds + 10));
        using var registration = deadline.Token.Register(() =>
        {
            void Stop()
            {
                try { player.Stop(); player.Close(); finished.TrySetCanceled(deadline.Token); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { finished.TrySetException(new IOException("Playback could not stop cleanly.", error)); }
            }
            if (dispatcher.CheckAccess()) Stop();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Stop);
            else finished.TrySetCanceled(deadline.Token);
        });
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            player.Open(new Uri(ownedWavPath, UriKind.Absolute)); player.Play();
            await finished.Task;
        }
        finally { player.MediaEnded -= ended; player.MediaFailed -= failed; player.Close(); }
    }
}
