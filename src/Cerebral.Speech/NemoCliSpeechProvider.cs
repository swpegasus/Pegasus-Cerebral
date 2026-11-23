using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Cerebral.Speech;

/// <summary>
/// Pinned NeMo-Speech.cpp 0.1.0 CPU adapter. Each bounded recording owns one hidden process;
/// killing that exact child confirms cancellation even when native inference cannot be interrupted.
/// No microphone, shell, HTTP server, model download, or Chat tool is started here.
/// </summary>
public sealed class NemoCliSpeechProvider : ISpeechProvider
{
    public const int MaximumAudioSeconds = 120;
    public const int MaximumTranscriptBytes = 32 * 1024;
    private const int MaximumOutputBytes = 512 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim serial = new(1, 1);

    public NemoCliSpeechProvider(string executablePath, TimeSpan? timeout = null)
    {
        this.executablePath = AbsolutePath(executablePath, "runtime");
        this.timeout = timeout ?? TimeSpan.FromSeconds(60);
        if (this.timeout < TimeSpan.FromMilliseconds(100) || this.timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public SpeechProviderDescriptor Descriptor { get; } = new("nvidia.nemo-speech.cpu", "0.1.0", "Parakeet · CPU", false);

    public SpeechProviderAvailability Inspect(SpeechModelBinding model)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new(false, "This package requires a Windows x64 process.");
        if (!File.Exists(executablePath)) return new(false, "The selected NeMo speech runtime is missing.");
        try
        {
            ValidateBinding(model);
            return File.Exists(model.ModelPath) ? new(true, null) : new(false, "The selected speech model is missing.");
        }
        catch (ArgumentException) { return new(false, "The selected speech model registration is incomplete."); }
    }

    public async Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(audio);
        var availability = Inspect(model);
        if (!availability.Available) throw new SpeechProviderException("unavailable", availability.Reason!);
        var wavPath = AbsolutePath(audio.WavPath, "recording");
        if (!await serial.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new SpeechProviderException("busy", "This speech provider is already processing a recording.");
        try
        {
            // On Windows these handles prevent replacement while the child reads both files.
            using var wav = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var audioSeconds = ValidateWav(wav);
            using var modelFile = new FileStream(model.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var deadline = new CancellationTokenSource(timeout);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using var child = new Process { StartInfo = StartInfo(wavPath, model.ModelPath) };
            var watch = Stopwatch.StartNew();
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!child.Start()) throw new SpeechProviderException("start-failed", "The speech runtime did not start.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new SpeechProviderException("start-failed", "The speech runtime could not start. Check its package dependencies.", ex.Message);
            }

            var stdout = ReadBoundedAsync(child.StandardOutput.BaseStream, MaximumOutputBytes, lifetime.Token);
            var stderr = ReadBoundedAsync(child.StandardError.BaseStream, MaximumErrorBytes, lifetime.Token);
            // Faulting either reader must stop inference immediately rather than wait on a full pipe.
            var exit = child.WaitForExitAsync(lifetime.Token);
            try
            {
                var pending = new List<Task> { exit, stdout, stderr };
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    await finished.ConfigureAwait(false);
                    pending.Remove(finished);
                }
                var diagnostics = DiagnosticTail(await stderr.ConfigureAwait(false));
                if (child.ExitCode != 0)
                    throw new SpeechProviderException("transcription-failed", "The speech runtime could not transcribe this recording.", diagnostics, child.ExitCode);
                cancellationToken.ThrowIfCancellationRequested();
                return Parse(await stdout.ConfigureAwait(false), wavPath, audioSeconds, watch.ElapsedMilliseconds, model);
            }
            catch
            {
                lifetime.Cancel();
                await StopOwnedChildAsync(child).ConfigureAwait(false);
                await ObserveReadersAsync(stdout, stderr).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (deadline.IsCancellationRequested) throw new SpeechProviderException("timeout", "Speech recognition exceeded its time limit.");
                throw;
            }
        }
        finally { serial.Release(); }
    }

    private ProcessStartInfo StartInfo(string wavPath, string modelPath)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        foreach (var arg in new[] { "transcribe", wavPath, "--model", modelPath, "--device", "cpu", "--format", "json", "--no-warmup" })
            start.ArgumentList.Add(arg);
        return start;
    }

    private SpeechTranscript Parse(string output, string wavPath, double audioSeconds, long elapsed, SpeechModelBinding model)
    {
        try
        {
            using var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!keys.Add(property.Name)) throw new JsonException();
            var text = root.GetProperty("text").GetString() ?? throw new JsonException();
            var file = root.GetProperty("file").GetString() ?? throw new JsonException();
            var duration = root.GetProperty("duration").GetDouble();
            if (!Path.IsPathFullyQualified(file) || !Path.GetFullPath(file).Equals(wavPath, StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(duration) || Math.Abs(duration - audioSeconds) > .1
                || Encoding.UTF8.GetByteCount(text) > MaximumTranscriptBytes || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                throw new JsonException();
            // Native v0.1.0 emits confidence=1 even without calibrated confidence; do not promise it to callers.
            return new(text.Trim(), audioSeconds, elapsed, Descriptor.Id, model.ModelId);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new SpeechProviderException("invalid-output", "The speech runtime returned an invalid or mismatched transcript.");
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes) throw new SpeechProviderException("output-limit", "The speech runtime exceeded its output limit.");
            output.Write(buffer, 0, count);
        }
        try { return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length)); }
        catch (DecoderFallbackException) { throw new SpeechProviderException("invalid-output", "The speech runtime returned invalid text encoding."); }
    }

    private static async Task StopOwnedChildAsync(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (child.HasExited) { }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await child.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new SpeechProviderException("stop-unconfirmed", "The owned speech process did not confirm shutdown."); }
    }

    private static async Task ObserveReadersAsync(params Task[] readers)
    {
        try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SpeechProviderException or TimeoutException) { }
    }

    private static string? DiagnosticTail(string value)
    {
        var text = value.Length <= 4096 ? value : value[^4096..];
        if (text.Length > 0 && char.IsLowSurrogate(text[0])) text = text[1..];
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string AbsolutePath(string path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException($"Use an absolute {kind} path.");
        return Path.GetFullPath(path);
    }

    private static void ValidateBinding(SpeechModelBinding model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.ModelId) || model.ModelId.Length > 200 || model.ModelId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(model.ModelRevision) || model.ModelRevision.Length > 200 || model.ModelRevision.Any(char.IsControl))
            throw new ArgumentException("Select a versioned speech model registration.");
        _ = AbsolutePath(model.ModelPath, "model");
        if (!model.ModelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a GGUF speech model.");
    }

    private static double ValidateWav(FileStream stream)
    {
        const int maximumFileBytes = MaximumAudioSeconds * 16000 * 2 + 65536;
        if (stream.Length < 44 || stream.Length > maximumFileBytes) throw InvalidAudio();
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != stream.Length - 8 || reader.ReadUInt32() != 0x45564157) throw InvalidAudio();
        var formatSeen = false;
        long? dataBytes = null;
        var chunks = 0;
        while (stream.Position < stream.Length)
        {
            if (++chunks > 64 || stream.Length - stream.Position < 8) throw InvalidAudio();
            var kind = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > stream.Length) throw InvalidAudio();
            if (kind == 0x20746d66)
            {
                if (formatSeen || size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1
                    || reader.ReadUInt32() != 16000 || reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16) throw InvalidAudio();
                formatSeen = true;
            }
            else if (kind == 0x61746164)
            {
                if (dataBytes is not null || size == 0 || size % 2 != 0) throw InvalidAudio();
                dataBytes = size;
            }
            stream.Position = end + size % 2;
            if (stream.Position > stream.Length) throw InvalidAudio();
        }
        if (!formatSeen || dataBytes is null || dataBytes.Value > MaximumAudioSeconds * 32000) throw InvalidAudio();
        return dataBytes.Value / 32000d;
    }

    private static SpeechProviderException InvalidAudio() => new("invalid-audio", "Use a mono 16 kHz PCM16 WAV recording of at most 120 seconds.");
}
