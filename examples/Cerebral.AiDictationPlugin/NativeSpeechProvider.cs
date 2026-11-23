using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using Cerebral.Speech;

namespace Cerebral.AiDictationPlugin;

internal sealed record DictationRecognition(long Sequence, string Text, bool Final);
internal interface IResidentDictationProvider : ISpeechProvider, IAsyncDisposable
{
    Task ShutdownCompletion => Task.CompletedTask;
    SpeechProviderAvailability InspectStreaming();
    Task PrepareAsync(bool streaming, CancellationToken token);
    Task RecognizeStreamAsync(ChannelReader<byte[]> pcm, Func<DictationRecognition, Task> result, CancellationToken token);
}

/// <summary>One private CPU helper per explicitly active dictation run, reused across its automatic phrases.</summary>
internal sealed class NativeSpeechProvider(AiDictationSettings settings) : IResidentDictationProvider
{
    private DictationNativeWorker? worker;
    private bool streaming;
    private bool prepared;
    private long sequence;
    private Task shutdownCompletion = Task.CompletedTask;
    public Task ShutdownCompletion => shutdownCompletion;
    private readonly string helper = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "NativeSpeech", "Cerebral.AiDictation.NativeHost.exe");
    public SpeechProviderDescriptor Descriptor { get; } = new("nemo-native-cpu", "1", "Local resident CPU speech", true);
    public SpeechProviderAvailability Inspect(SpeechModelBinding model) => InspectPaths(model.ModelPath);
    public SpeechProviderAvailability InspectStreaming() => settings.StreamingModelPath.Length == 0
        ? new(false, "Live words need the approved Nemotron English streaming model. It is not installed. Open the one-off controls to Record, Stop, Play and Insert with the existing offline model.")
        : InspectPaths(settings.StreamingModelPath);
    private SpeechProviderAvailability InspectPaths(string model)
    {
        if (!Path.IsPathFullyQualified(settings.RuntimePath) || !File.Exists(settings.RuntimePath) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(settings.RuntimePath)!, "nemo_speech_asr_c.dll")))
            return new(false, "The selected runtime does not contain the native CPU speech SDK. Select the installed NeMo-Speech runtime in Dictation Settings.");
        if (!Path.IsPathFullyQualified(model) || !File.Exists(model)) return new(false, "The selected local speech model is missing. Check Dictation Settings.");
        if (!File.Exists(helper)) return new(false, "The private CPU speech helper is missing from this AI Dictation package. Repair or update the plugin package.");
        return new(true, null);
    }
    public async Task PrepareAsync(bool streaming, CancellationToken token)
    {
        if (worker is not null)
        {
            if (!prepared) throw new IOException("The previous speech startup did not complete. Wait for its helper to close.");
            if (this.streaming != streaming) throw new InvalidOperationException("Stop dictation before changing speech mode.");
            return;
        }
        var availability = streaming ? InspectStreaming() : Inspect(settings.Binding);
        if (!availability.Available) throw new SpeechProviderException("unavailable", availability.Reason!);
        var next = new DictationNativeWorker(helper);
        worker = next; this.streaming = streaming;
        await next.StartAsync(Path.GetDirectoryName(settings.RuntimePath)!, streaming ? settings.StreamingModelPath : settings.ModelPath,
            streaming, checked((int)Math.Round(settings.SilenceWaitSeconds * 1000)), token).ConfigureAwait(false);
        prepared = true;
    }
    public async Task RecognizeStreamAsync(ChannelReader<byte[]> pcm, Func<DictationRecognition, Task> result, CancellationToken token)
    {
        await PrepareAsync(true, token).ConfigureAwait(false);
        await worker!.ExchangeAsync("begin", null, token).ConfigureAwait(false);
        await foreach (var chunk in pcm.ReadAllAsync(token).ConfigureAwait(false))
            await Deliver(await worker.ExchangeAsync("audio", chunk, token).ConfigureAwait(false)).ConfigureAwait(false);
        await Deliver(await worker.ExchangeAsync("finish", null, token).ConfigureAwait(false)).ConfigureAwait(false);
        async Task Deliver(DictationNativeResponse response)
        {
            foreach (var item in response.Results ?? [])
                await result(new(checked(++sequence), item.Text, item.Final)).ConfigureAwait(false);
        }
    }
    public async Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken token)
    {
        if (model != settings.Binding) throw new SpeechProviderException("binding", "The recording's model binding changed. Start a new recording.");
        await PrepareAsync(false, token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        using var file = new FileStream(audio.WavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        var header = new byte[44]; await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var bytes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40));
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)) != 16 || BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(20)) != 1 ||
            BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(22)) != 1 || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) != 16000 ||
            BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(34)) != 16 || !header.AsSpan(36, 4).SequenceEqual("data"u8) ||
            bytes is < 2 or > 120 * 32000 || bytes % 2 != 0 || file.Length != bytes + 44L)
            throw new SpeechProviderException("audio", "The owned recording is not bounded mono16k PCM16 audio.");
        await worker!.ExchangeAsync("begin", null, token).ConfigureAwait(false);
        for (var left = bytes; left > 0;)
        {
            var chunk = new byte[Math.Min(left, DictationNativeProtocol.MaximumPcmBytes)];
            await file.ReadExactlyAsync(chunk, token).ConfigureAwait(false); left -= chunk.Length;
            await worker.ExchangeAsync("audio", chunk, token).ConfigureAwait(false);
        }
        var final = await worker.ExchangeAsync("finish", null, token).ConfigureAwait(false);
        var text = string.Join(" ", (final.Results ?? []).Where(value => value.Final).Select(value => value.Text));
        return new(text, bytes / 32000d, watch.ElapsedMilliseconds, Descriptor.Id, model.ModelId);
    }
    public async ValueTask DisposeAsync()
    {
        var prior = worker; worker = null; prepared = false;
        if (prior is not null) { shutdownCompletion = prior.ShutdownCompletion; await prior.DisposeAsync().ConfigureAwait(false); }
    }
}

internal sealed class DictationNativeWorker(string executable) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? child;
    private Task? stderr;
    private long id;
    private bool failed, disposed, started;
    private Task? settlement;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? exitCode;
    internal Task ShutdownCompletion => stopped.Task;
    internal async Task StartAsync(string bin, string model, bool streaming, int silence, CancellationToken token)
    {
        if (child is not null || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(bin) || !Path.IsPathFullyQualified(model))
            throw new InvalidOperationException("One exact private speech executable and absolute asset paths are required.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in new[] { bin, model, streaming ? "streaming" : "offline", silence.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment.Clear(); start.Environment["SystemRoot"] = windows; start.Environment["WINDIR"] = windows;
        start.Environment["TEMP"] = Path.GetTempPath(); start.Environment["TMP"] = Path.GetTempPath();
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        child = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!child.Start()) throw new IOException("The private CPU speech helper did not start.");
        started = true;
        _ = child.SafeHandle; // Keep the owned process handle; never attach to/kill another process by name or PID.
        stderr = DrainErrorsAsync(child.StandardError);
        using var deadline = Deadline(token);
        try
        {
            var ready = await DictationNativeProtocol.ReadAsync<DictationNativeResponse>(child.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
            Check(ready, 0, "ready");
            if (ready.SdkVersion != "nemo-speech-asr 0.1.0") throw new InvalidDataException("The speech SDK version is incompatible with this package.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { failed = true; BeginSettlement(); throw new SpeechProviderException("uncertain", "The speech helper exceeded its startup deadline. Recording stopped without retrying; wait for its actual exit."); }
        catch { failed = true; BeginSettlement(); throw; }
    }
    internal async Task<DictationNativeResponse> ExchangeAsync(string operation, byte[]? pcm, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed || failed || child is null) throw new IOException("The private speech helper is unavailable. Start dictation again.");
            if (operation == "audio") DictationNativeProtocol.CheckPcm(pcm);
            using var deadline = Deadline(token);
            var request = new DictationNativeRequest(1, checked(++id), operation, pcm);
            await DictationNativeProtocol.WriteAsync(child.StandardInput.BaseStream, request, deadline.Token).ConfigureAwait(false);
            var response = await DictationNativeProtocol.ReadAsync<DictationNativeResponse>(child.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
            Check(response, request.Id, operation == "close" ? "closed" : "ok"); return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { failed = true; BeginSettlement(); throw new SpeechProviderException("uncertain", "The speech helper exceeded its recognition deadline. Pending words were retained without retrying; wait for its actual exit."); }
        catch { failed = true; BeginSettlement(); throw; }
        finally { gate.Release(); }
    }
    private static void Check(DictationNativeResponse response, long expected, string state)
    {
        if (response.Version != 1 || response.Id != expected || response.Message?.Length > 512 ||
            response.Results?.Length > 32 || (response.Results ?? []).Any(value => value.Text is null || value.Text.Length > 32768) ||
            (response.Results ?? []).Sum(value => value.Text.Length) > 32768)
            throw new InvalidDataException("The speech helper returned an incompatible or oversized response.");
        if (response.State != state) throw new SpeechProviderException("native", response.Message ?? "The local speech helper refused recognition.");
    }
    private CancellationTokenSource Deadline(CancellationToken token)
    { var result = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); result.CancelAfter(TimeSpan.FromSeconds(45)); return result; }
    private static async Task DrainErrorsAsync(StreamReader input)
    {
        // Native diagnostics are never transcript/model input or unbounded retained logs.
        var buffer = new char[1024]; while (await input.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
    }
    private void BeginSettlement()
    {
        if (settlement is not null) return;
        lifetime.Cancel();
        // A timeout/cancel may follow an uncertain native call. End only our input,
        // discard late output and retain the exact child/handle until natural exit.
        // Never kill it, resend audio or publish an abandoned late transcript.
        try { child?.StandardInput.Close(); } catch (InvalidOperationException) { }
        settlement = SettleAsync();
    }
    private async Task SettleAsync()
    {
        try
        {
            if (started && child is { } process)
            {
                var buffer = new byte[8192];
                while (await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = process.ExitCode;
                if (stderr is not null) await stderr.ConfigureAwait(false);
            }
            stopped.TrySetResult();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            stopped.TrySetException(new IOException("The exact speech helper's exit could not be confirmed.", error));
            _ = stopped.Task.Exception; // Observed even if an earlier bounded caller wait already expired.
        }
        finally { child?.Dispose(); lifetime.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        Exception? cleanup = null;
        try
        {
            if (started && child is { } process)
            {
                if (!failed && gate.CurrentCount == 1 && !process.HasExited)
                    try { using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await ExchangeAsync("close", null, finish.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OutOfMemoryException) { cleanup = error; }
                BeginSettlement();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!failed && exitCode != 0) cleanup ??= new IOException("The speech helper exited with a failure after its close acknowledgement.");
            }
            else BeginSettlement();
        }
        finally { disposed = true; }
        if (cleanup is not null) throw new IOException("The local speech helper did not acknowledge close. Its actual exit must settle before another run.", cleanup);
    }
}
