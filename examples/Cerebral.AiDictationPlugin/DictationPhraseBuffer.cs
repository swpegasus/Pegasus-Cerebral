using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Cerebral.AiDictationPlugin;

internal sealed record DictationPcmPhrase(long Sequence, byte[] Pcm);

/// <summary>Single capture-thread segmenter. Samples belong to exactly one phrase; no text-based deduplication.</summary>
internal sealed class DictationPhraseSegmenter : IDisposable
{
    internal const int BytesPerSecond = 32000;
    internal const int PreRollBytes = BytesPerSecond / 5;
    internal const int MaximumPhraseBytes = 30 * BytesPerSecond;
    private readonly int silenceBytes;
    private readonly Action<DictationPcmPhrase> publish;
    private readonly byte[] preRoll = new byte[PreRollBytes];
    private readonly MemoryStream phrase = new();
    private int preRollCount, preRollNext, quietBytes;
    private long sequence;
    private bool speaking, finished;

    internal DictationPhraseSegmenter(double silenceWaitSeconds, Action<DictationPcmPhrase> publish)
    {
        if (!double.IsFinite(silenceWaitSeconds) || silenceWaitSeconds is < .5 or > 10)
            throw new ArgumentOutOfRangeException(nameof(silenceWaitSeconds));
        silenceBytes = checked((int)Math.Ceiling(silenceWaitSeconds * BytesPerSecond / 2) * 2);
        this.publish = publish;
    }

    internal void Push(byte[] pcm)
    {
        if (finished) throw new InvalidOperationException("The phrase capture has already finished.");
        DictationNativeProtocol.CheckPcm(pcm);
        var speech = false;
        for (var offset = 0; offset < pcm.Length; offset += 2)
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2))) >= 656)
            { speech = true; break; }
        if (!speaking)
        {
            if (!speech)
            {
                foreach (var value in pcm)
                { preRoll[preRollNext] = value; preRollNext = (preRollNext + 1) % PreRollBytes; preRollCount = Math.Min(PreRollBytes, preRollCount + 1); }
                return;
            }
            var first = (preRollNext - preRollCount + PreRollBytes) % PreRollBytes;
            var head = Math.Min(preRollCount, PreRollBytes - first);
            phrase.Write(preRoll.AsSpan(first, head));
            phrase.Write(preRoll.AsSpan(0, preRollCount - head));
            preRollCount = preRollNext = 0;
            speaking = true;
        }
        // Do not hard-split a word or guess overlap ownership. The complete capture
        // remains in the run WAV for explicit Play/recovery when this bound stops us.
        if (phrase.Length + pcm.Length > MaximumPhraseBytes)
            throw new IOException("Speech continued for 30 seconds without a complete pause. Recording stopped; the run audio was retained for Play. Earlier inserted phrases remain in the destination.");
        phrase.Write(pcm);
        quietBytes = speech ? 0 : checked(quietBytes + pcm.Length);
        if (quietBytes >= silenceBytes) Publish();
    }

    internal void Finish()
    {
        if (finished) return;
        finished = true;
        if (speaking) Publish();
    }

    private void Publish()
    {
        publish(new(checked(++sequence), phrase.ToArray()));
        phrase.SetLength(0); phrase.Position = 0;
        speaking = false; quietBytes = 0;
        // The previous phrase owns its quiet tail. Never replay that tail as preroll.
        preRollCount = preRollNext = 0;
    }
    public void Dispose() => phrase.Dispose();
}

/// <summary>One consumer; two waiting phrases and at most 30 seconds of waiting PCM, in addition to the active decode.</summary>
internal sealed class DictationPhraseQueue
{
    internal const int MaximumWaitingPhrases = 2;
    internal const int MaximumWaitingBytes = 30 * DictationPhraseSegmenter.BytesPerSecond;
    private readonly object sync = new();
    private readonly Channel<DictationPcmPhrase> queue = Channel.CreateBounded<DictationPcmPhrase>(
        new BoundedChannelOptions(MaximumWaitingPhrases) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private int waitingBytes;
    internal void Add(DictationPcmPhrase phrase)
    {
        lock (sync)
        {
            if (phrase.Pcm.Length > MaximumWaitingBytes - waitingBytes || !queue.Writer.TryWrite(phrase))
                throw new IOException("Phrase processing fell behind (two waiting phrases or 30 seconds of waiting audio). Recording stopped without dropping or replaying phrases. Play the retained run audio and inspect earlier inserted text before recovery.");
            waitingBytes += phrase.Pcm.Length;
        }
    }
    internal async Task<DictationPcmPhrase?> TakeAsync(CancellationToken token)
    {
        while (await queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            lock (sync)
                if (queue.Reader.TryRead(out var phrase))
                { waitingBytes -= phrase.Pcm.Length; return phrase; }
        }
        return null;
    }
    internal void Complete() => queue.Writer.TryComplete();
}

internal static class DictationPcmWave
{
    internal static void WriteHeader(BinaryWriter writer, int bytes)
    {
        if (bytes < 0 || bytes % 2 != 0 || bytes > 120 * DictationPhraseSegmenter.BytesPerSecond)
            throw new InvalidDataException("The recording exceeds its bounded PCM format.");
        writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
    }
    internal static void Write(string path, byte[] pcm)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file, Encoding.ASCII);
        WriteHeader(writer, pcm.Length); writer.Write(pcm);
    }
}
