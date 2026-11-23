using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Cerebral.AiDictationPlugin;

/// <summary>Stable v0.1 C ABI. Own and drive this instance on one dedicated speech helper process thread, outside Main.</summary>
internal sealed class NemoNativeAsr : IDisposable
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly nint library;
    private readonly FileStream modelLease;
    private nint recognizer;
    private RecognitionStream? activeStream;
    private readonly int silenceMilliseconds;
    private readonly Destroy destroy;
    private readonly Recognize recognize;
    private readonly Defaults defaults;
    private readonly ResultText resultText;
    private readonly ResultDestroy resultDestroy;
    private readonly LastError lastError;
    private readonly StreamOpen streamOpen;
    private readonly StreamPush streamPush;
    private readonly StreamNext streamNext;
    private readonly StreamSignal streamFinish, forceEndpoint;
    private readonly Destroy streamClose;
    private readonly ResultFinal resultFinal;
    internal string Version { get; }

    internal NemoNativeAsr(string binDirectory, string modelPath, bool streamingRequested = false, int silenceMilliseconds = 1500)
    {
        if (silenceMilliseconds is < 500 or > 10000) throw new ArgumentOutOfRangeException(nameof(silenceMilliseconds));
        this.silenceMilliseconds = silenceMilliseconds;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The selected native speech runtime requires Windows x64.");
        if (!Path.IsPathFullyQualified(binDirectory) || !Path.IsPathFullyQualified(modelPath)) throw new ArgumentException("Absolute owned runtime/model paths are required.");
        modelLease = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Exclude current-directory/PATH lookup. Dependency DLLs come from this selected SDK directory or system directories.
            library = ProcessLibrary.Load(Path.Combine(binDirectory, "nemo_speech_asr_c.dll"));
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            destroy = Export<Destroy>("nemo_speech_asr_destroy"); recognize = Export<Recognize>("nemo_speech_asr_recognize_f32");
            defaults = Export<Defaults>("nemo_speech_asr_recognition_options_default");
            resultText = Export<ResultText>("nemo_speech_asr_result_transcript"); resultDestroy = Export<ResultDestroy>("nemo_speech_asr_result_destroy");
            lastError = Export<LastError>("nemo_speech_asr_last_error");
            streamOpen = Export<StreamOpen>("nemo_speech_asr_streaming_recognize"); streamPush = Export<StreamPush>("nemo_speech_asr_stream_push_f32");
            streamNext = Export<StreamNext>("nemo_speech_asr_stream_next"); streamFinish = Export<StreamSignal>("nemo_speech_asr_stream_finish");
            forceEndpoint = Export<StreamSignal>("nemo_speech_asr_stream_force_endpoint"); streamClose = Export<Destroy>("nemo_speech_asr_stream_close");
            resultFinal = Export<ResultFinal>("nemo_speech_asr_result_is_final");
            Version = ReadUtf8(Export<LastError>("nemo_speech_asr_version")(), 256);
            if (Marshal.SizeOf<Options>() != 72 || Marshal.SizeOf<Backend>() != 16 || Marshal.SizeOf<Config>() != 80)
                throw new InvalidOperationException("The native ASR ABI layout is not the selected x64 contract.");
            using var memory = new NativeMemoryOwner();
            var config = new Config { Size = (nuint)Marshal.SizeOf<Config>(), Backend = memory.Struct(new Backend { Size = 16, Gpu = -1 }),
                Model = memory.Struct(new Model { Size = 24, Path = memory.Utf8(modelPath) }),
                Streaming = streamingRequested ? memory.Struct(new Streaming { Size = 24, Chunk = .16f, Left = 1.92f, Right = 1.92f, RnntRight = 1 }) : 0,
                Endpointing = streamingRequested ? memory.Struct(new Endpointing { Size = 16, Enable = 1, SilenceMilliseconds = silenceMilliseconds }) : 0 };
            Check(Export<Create>("nemo_speech_asr_create")(ref config, out recognizer));
            if (recognizer == 0) throw new IOException("The native ASR SDK returned no recognizer.");
        }
        catch
        {
            try { if (recognizer != 0) destroy?.Invoke(recognizer); }
            finally { modelLease.Dispose(); }
            throw;
        }
    }

    internal string RecognizeOffline(float[] samples)
    {
        EnsureOwner(); CheckSamples(samples);
        if (activeStream is not null) throw new InvalidOperationException("Finish the active stream before recognizing a separate recording.");
        var options = defaults(); options.AutomaticPunctuation = 1;
        nint result = 0;
        try { Check(recognize(recognizer, ref options, samples, (nuint)samples.Length, 16000, out result)); return result == 0 ? "" : ReadUtf8(resultText(result, 0), 32768); }
        finally { if (result != 0) resultDestroy(result); }
    }

    internal RecognitionStream OpenStream()
    {
        EnsureOwner(); var options = defaults(); options.Interim = 1; options.AutomaticPunctuation = 1;
        if (activeStream is not null) throw new InvalidOperationException("Only one recognition stream is admitted per helper recognizer.");
        options.StopHistory = silenceMilliseconds;
        Check(streamOpen(recognizer, ref options, out var stream));
        if (stream == 0) throw new IOException("The native speech runtime returned no stream.");
        return activeStream = new(this, stream);
    }
    private void Check(int status) { if (status != 0) throw new IOException($"Native speech status {status}: {ReadUtf8(lastError(), 2048)}"); }
    private static void CheckSamples(float[] samples)
    { if (samples.Length is < 1 or > 16000 * 120 || samples.Any(value => !float.IsFinite(value) || value is < -1 or > 1)) throw new ArgumentException("Bounded normalized mono16k audio is required."); }
    private void EnsureOwner()
    { if (Environment.CurrentManagedThreadId != thread || recognizer == 0) throw new InvalidOperationException("The native recognizer must be driven by its live owning worker thread."); }
    private static string ReadUtf8(nint pointer, int maximum)
    {
        if (pointer == 0) return "";
        var length = 0; while (length <= maximum && Marshal.ReadByte(pointer, length) != 0) length++;
        if (length > maximum) throw new IOException("Native speech text exceeds its bound.");
        return Marshal.PtrToStringUTF8(pointer, length) ?? "";
    }
    public void Dispose()
    {
        if (recognizer == 0) return;
        EnsureOwner();
        activeStream?.Dispose();
        try { destroy(recognizer); }
        finally { recognizer = 0; modelLease.Dispose(); }
    }

    private static class ProcessLibrary
    {
        private static readonly object gate = new();
        private static string? selectedPath;
        private static nint module;
        internal static nint Load(string path)
        {
            path = Path.GetFullPath(path);
            lock (gate)
            {
                if (module != 0)
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(path, selectedPath))
                        throw new InvalidOperationException("A speech helper process cannot mix native runtime directories.");
                    return module;
                }
                var loaded = LoadLibraryEx(path, 0, 0x00000100 | 0x00001000);
                if (loaded == 0) throw new IOException("The owned native ASR SDK or its packaged dependencies could not be loaded.");
                // The SDK's OpenMP worker threads outlive a recognizer. Dynamic unload
                // caused an observed VCOMP140.DLL_unloaded access violation. One module
                // reference therefore belongs to the dedicated helper PROCESS lifetime;
                // recognizers/results are destroyed normally and process exit reclaims
                // the DLLs only after its native threads have stopped. Never load in Main.
                selectedPath = path; module = loaded;
                return module;
            }
        }
    }

    internal sealed class RecognitionStream(NemoNativeAsr owner, nint handle) : IDisposable
    {
        private nint stream = handle;
        private bool finished;
        private int sampleCount;
        private void EnsureLive() { owner.EnsureOwner(); if (stream == 0) throw new ObjectDisposedException(nameof(RecognitionStream)); }
        internal void Push(float[] samples)
        {
            EnsureLive(); if (finished) throw new InvalidOperationException("The stream no longer accepts audio.");
            CheckSamples(samples);
            if (sampleCount > 16000 * 120 - samples.Length) throw new ArgumentException("A recognition stream is limited to 120 seconds.");
            owner.Check(owner.streamPush(stream, samples, (nuint)samples.Length, 16000)); sampleCount += samples.Length;
        }
        internal void ForceEndpoint() { EnsureLive(); owner.Check(owner.forceEndpoint(stream)); }
        internal void Finish() { EnsureLive(); if (!finished) { owner.Check(owner.streamFinish(stream)); finished = true; } }
        internal (string Text, bool Final)? Next()
        {
            EnsureLive(); nint result = 0;
            try { owner.Check(owner.streamNext(stream, out result)); return result == 0 ? null : (ReadUtf8(owner.resultText(result, 0), 32768), owner.resultFinal(result) != 0); }
            finally { if (result != 0) owner.resultDestroy(result); }
        }
        public void Dispose()
        {
            if (stream == 0) return; owner.EnsureOwner();
            try { owner.streamClose(stream); }
            finally { stream = 0; if (ReferenceEquals(owner.activeStream, this)) owner.activeStream = null; }
        }
    }
    private sealed class NativeMemoryOwner : IDisposable
    {
        private readonly List<nint> blocks = [];
        internal nint Struct<T>(T value) where T : struct { var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); blocks.Add(p); Marshal.StructureToPtr(value, p, false); return p; }
        internal nint Utf8(string value)
        { var bytes = Encoding.UTF8.GetBytes(value + '\0'); var p = Marshal.AllocHGlobal(bytes.Length); blocks.Add(p); Marshal.Copy(bytes, 0, p, bytes.Length); return p; }
        public void Dispose() { foreach (var p in blocks) Marshal.FreeHGlobal(p); }
    }
    // Native payload fields retain exact layout, including zeros and fields filled
    // by the native defaults function rather than managed assignment.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)] private struct Backend { internal nuint Size; internal int Gpu; }
    [StructLayout(LayoutKind.Sequential)] private struct Model { internal nuint Size; internal nint Path, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct Streaming { internal nuint Size; internal float Chunk, Left, Right; internal int RnntRight; }
    [StructLayout(LayoutKind.Sequential)] private struct Endpointing { internal nuint Size; internal byte Enable, VadBased; internal int SilenceMilliseconds; }
    [StructLayout(LayoutKind.Sequential)] private struct Config { internal nuint Size; internal nint Backend, Model, Streaming, Decoder, Vad, Endpointing, Postproc, Diar, Batching; }
    [StructLayout(LayoutKind.Sequential)] private struct Options
    {
        internal nuint Size; internal nint RequestId, Language; internal byte Interim, WordTimes, AutomaticPunctuation, Verbatim, Profanity;
        internal int StopHistory; internal nint SpeechContexts; internal nuint SpeechContextCount;
        internal int MaxAlternatives; internal byte Diarization; internal int MaxSpeakers;
    }
#pragma warning restore CS0649
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(ref Config config, out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate Options Defaults();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Recognize(nint handle, ref Options options, [In] float[] samples, nuint count, int rate, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ResultText(nint result, nuint alternative);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResultDestroy(nint result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LastError();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamOpen(nint recognizer, ref Options options, out nint stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamPush(nint stream, [In] float[] samples, nuint count, int rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamNext(nint stream, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamSignal(nint stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ResultFinal(nint result);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
