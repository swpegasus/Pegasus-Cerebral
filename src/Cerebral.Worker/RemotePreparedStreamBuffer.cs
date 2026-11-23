using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

/// <summary>Single-reader, lossless bounded callback coalescing. Provider callbacks never wait for HTTP writes.</summary>
internal sealed class RemotePreparedStreamBuffer : IDisposable
{
    private readonly RemotePreparedStreamRequest request;
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly StringBuilder pending = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private PreparedModelMetadata? metadata;
    private RemoteGeneratedResponse? terminal;
    private Exception? failure;
    private long totalCharacters, lastPublished = -250;
    private int sequence, reader;
    private bool started, startPublished, completed, disposed;

    internal RemotePreparedStreamBuffer(RemotePreparedStreamRequest request)
    { RemotePreparedStreamingCodec.ValidateRequest(request); this.request = request; }

    internal void Start(PreparedModelMetadata value)
    {
        _ = RemotePreparedStreamingCodec.Start(request, value);
        lock (gate)
        {
            if (disposed) return;
            if (started || completed) throw new RemoteModelProtocolException("Prepared stream repeated its start.");
            metadata = value; started = true; Signal();
        }
    }

    internal void Append(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        lock (gate)
        {
            if (disposed) return;
            if (!started || completed) throw new RemoteModelProtocolException("Prepared callback arrived outside its owned generation.");
            if (fragment.Length == 0) return;
            if (fragment.Length > RemotePreparedProtocol.MaximumOutputCharacters - totalCharacters)
            {
                failure ??= new RemoteModelProtocolException("Prepared callback text exceeded its output bound.");
                Signal();
                throw new RemoteModelProtocolException("Prepared callback text exceeded its output bound.");
            }
            totalCharacters += fragment.Length;
            pending.Append(fragment); Signal();
        }
    }

    internal void Complete(RemoteGeneratedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (gate)
        {
            if (disposed) return;
            if (!started || completed) throw new RemoteModelProtocolException("Prepared terminal arrived outside its one-use generation.");
            terminal = response; completed = true; Signal();
        }
    }

    internal void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            if (disposed || failure is not null) return;
            failure = error; completed = true; Signal();
        }
    }

    internal async IAsyncEnumerable<RemotePreparedStreamFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref reader, 1, 0) != 0)
            throw new InvalidOperationException("A prepared stream has exactly one reader.");
        while (true)
        {
            RemotePreparedStreamFrame? frame = null;
            bool done = false, failed = false;
            var delay = Timeout.InfiniteTimeSpan;
            lock (gate)
            {
                if (failure is not null) failed = true;
                else if (disposed) done = true;
                else if (started && !startPublished)
                {
                    frame = RemotePreparedStreamingCodec.Start(request, metadata!);
                    startPublished = true;
                }
                else if (pending.Length > 0)
                {
                    var milliseconds = elapsed.ElapsedMilliseconds;
                    var ready = completed || pending.Length >= RemotePreparedStreamingLimits.FragmentCharacters ||
                        milliseconds - lastPublished >= 250;
                    var count = Math.Min(pending.Length, RemotePreparedStreamingLimits.FragmentCharacters);
                    // UTF-16 pairs may straddle actual provider callbacks or our transport chunks.
                    if (count > 0 && char.IsHighSurrogate(pending[count - 1])) count--;
                    if (ready && count > 0)
                    {
                        var text = pending.ToString(0, count); pending.Remove(0, count);
                        frame = RemotePreparedStreamingCodec.TextFrame(request, ++sequence, text);
                        lastPublished = milliseconds;
                    }
                    else if (completed)
                        failed = true; // A terminal unpaired scalar cannot become replacement text.
                    else if (count > 0)
                        delay = TimeSpan.FromMilliseconds(Math.Max(1, 250 - (milliseconds - lastPublished)));
                }
                else if (completed)
                {
                    if (terminal is null) failed = true;
                    else frame = RemotePreparedStreamingCodec.Terminal(request, ++sequence, terminal);
                    done = true;
                }
            }
            if (failed) throw new RemoteModelProtocolException("Prepared streaming producer did not return a valid complete stream.");
            if (frame is not null)
            {
                if (frame.Sequence >= RemotePreparedStreamingLimits.Frames)
                    throw new RemoteModelProtocolException("Prepared stream exceeded its frame count.");
                yield return frame;
            }
            if (done) yield break;
            if (frame is null) _ = await wake.WaitAsync(delay, token).ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        // Called under gate. A pending signal represents all buffered characters, never dropped content.
        if (wake.CurrentCount == 0) wake.Release();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending.Clear(); metadata = null; terminal = null; failure = null; Signal();
            // Do not dispose the semaphore beneath a waiting reader. It is managed and becomes
            // collectible after the owner has observed producer and writer completion.
        }
    }
}
