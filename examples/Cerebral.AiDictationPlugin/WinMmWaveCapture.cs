using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cerebral.Speech;

namespace Cerebral.AiDictationPlugin;

internal interface IAiDictationCapture
{
    Task<AiDictationCaptureResult> CaptureAsync(string wavPath, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level);
}
internal interface IAiDictationIncrementalCapture : IAiDictationCapture
{
    Task<AiDictationCaptureResult> CaptureIncrementalAsync(string wavPath, AiDictationCaptureOptions options,
        CancellationToken finish, CancellationToken cancel, Action<double> level, Action<byte[]> pcm);
}

internal sealed record AiDictationCaptureOptions(string InputDeviceName, double BoostDb, bool NormalizeQuietAudio = false,
    double NormalizationCalibrationSeconds = 5, bool AutoTranscribeOnSilence = false, double SilenceWaitSeconds = 1.5);
internal sealed record AiDictationNormalization(bool Applied, double SourcePeak, double ResultPeak, double GainDb);
internal sealed record AiDictationCaptureResult(double DurationSeconds, AiDictationNormalization Normalization);
internal sealed record AiDictationInputDevice(int Id, string Name, int Channels)
{
    public string Label => Channels == 1 ? Name : $"{Name} · {Channels} channels";
}

/// <summary>Signed-in desktop capture only. Fixed native buffers are polled without callback lifetime hazards.</summary>
internal sealed class WinMmWaveCapture : IAiDictationIncrementalCapture
{
    private const int BufferBytes = 3200;
    private const uint Done = 1;
    private static readonly uint HeaderBytes = (uint)Marshal.SizeOf<WaveHeader>();
    internal static int InputDeviceCount => checked((int)waveInGetNumDevs());
    internal static IReadOnlyList<AiDictationInputDevice> InputDevices
    {
        get
        {
            var devices = new List<AiDictationInputDevice>();
            for (var id = 0; id < InputDeviceCount; id++)
            {
                if (waveInGetDevCaps((UIntPtr)(uint)id, out var capabilities, (uint)Marshal.SizeOf<WaveInCapabilities>()) == 0)
                    devices.Add(new(id, capabilities.ProductName.TrimEnd('\0'), capabilities.Channels));
            }
            return devices;
        }
    }

    public Task<AiDictationCaptureResult> CaptureAsync(string wavPath, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level) =>
        Task.Run(() => Capture(wavPath, options, finish, cancel, level), CancellationToken.None);

    public Task<AiDictationCaptureResult> CaptureIncrementalAsync(string wavPath, AiDictationCaptureOptions options,
        CancellationToken finish, CancellationToken cancel, Action<double> level, Action<byte[]> pcm) =>
        Task.Run(() => Capture(wavPath, options, finish, cancel, level, pcm), CancellationToken.None);

    private static AiDictationCaptureResult Capture(string wavPath, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level, Action<byte[]>? pcm = null)
    {
        cancel.ThrowIfCancellationRequested();
        if (finish.IsCancellationRequested) return new(0, new(false, 0, 0, 0));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AI dictation capture requires Windows.");
        if (!double.IsFinite(options.BoostDb) || options.BoostDb is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.NormalizationCalibrationSeconds) || options.NormalizationCalibrationSeconds is < 5 or > 10)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.SilenceWaitSeconds) || options.SilenceWaitSeconds is < .5 or > 10)
            throw new ArgumentOutOfRangeException(nameof(options));
        var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = 16000, AverageBytesPerSecond = 32000, BlockAlign = 2, BitsPerSample = 16 };
        var device = ResolveDevice(options.InputDeviceName);
        Check(waveInOpen(out var handle, device, ref format, IntPtr.Zero, IntPtr.Zero, 0),
            string.IsNullOrWhiteSpace(options.InputDeviceName) ? "open the Windows default microphone" : $"open microphone '{options.InputDeviceName}'");
        var buffers = new List<NativeBuffer>();
        Exception? cleanupError = null;
        var written = 0;
        var normalization = new AiDictationNormalization(false, 0, 0, 0);
        try
        {
            using var stream = new FileStream(wavPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(new byte[44]);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var buffer = new NativeBuffer(); buffers.Add(buffer);
                    Check(waveInPrepareHeader(handle, buffer.Header, HeaderBytes), "prepare microphone input"); buffer.Prepared = true;
                    Check(waveInAddBuffer(handle, buffer.Header, HeaderBytes), "queue microphone input");
                }
                if (!finish.IsCancellationRequested) Check(waveInStart(handle), "start the microphone");
                level(0);
                var watch = Stopwatch.StartNew();
                var next = 0;
                var managed = new byte[BufferBytes];
                var calibrationBytes = checked((int)Math.Round(options.NormalizationCalibrationSeconds * 32000));
                using var calibration = options.NormalizeQuietAudio ? new MemoryStream(Math.Min(calibrationBytes + BufferBytes, 320_000)) : null;
                var normalizationDecided = !options.NormalizeQuietAudio;
                var normalizationGainDb = 0d;
                var speechDetected = false;
                var lastSpeech = TimeSpan.Zero;
                var autoStop = false;
                void CommitCalibration()
                {
                    if (normalizationDecided || calibration is null) return;
                    var buffered = calibration.GetBuffer().AsSpan(0, checked((int)calibration.Length));
                    normalization = NormalizePcm16(buffered);
                    normalizationGainDb = normalization.GainDb;
                    // Batch recordings retain their original whole-calibration behavior.
                    // Streaming already emitted these samples: apply the measured gain
                    // only to future chunks, with no5-second first-word hold or replay.
                    if (pcm is null) writer.Write(buffered);
                    normalizationDecided = true;
                }
                void Consume(NativeBuffer buffer, bool requeue)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                    if ((header.Flags & Done) == 0) return;
                    if (header.BytesRecorded > BufferBytes || header.BytesRecorded % 2 != 0) throw new IOException("The microphone returned an invalid audio buffer.");
                    var count = Math.Min((int)header.BytesRecorded, NemoCliSpeechProvider.MaximumAudioSeconds * 32000 - written);
                    if (count > 0)
                    {
                        Marshal.Copy(buffer.Data, managed, 0, count);
                        var peak = ApplySoftwareGain(managed.AsSpan(0, count), options.BoostDb);
                        if (peak >= .02) { speechDetected = true; lastSpeech = watch.Elapsed; }
                        else if (options.AutoTranscribeOnSilence && speechDetected && watch.Elapsed - lastSpeech >= TimeSpan.FromSeconds(options.SilenceWaitSeconds))
                            autoStop = true;
                        if (!normalizationDecided)
                        {
                            calibration!.Write(managed, 0, count);
                            if (pcm is not null) { writer.Write(managed, 0, count); pcm(managed.AsSpan(0, count).ToArray()); }
                            if (calibration.Length >= calibrationBytes) CommitCalibration();
                        }
                        else
                        {
                            if (normalizationGainDb > .01) peak = ApplySoftwareGain(managed.AsSpan(0, count), normalizationGainDb);
                            writer.Write(managed, 0, count);
                            pcm?.Invoke(managed.AsSpan(0, count).ToArray());
                        }
                        written += count;
                        level(!normalizationDecided || normalizationGainDb <= .01 ? peak : Math.Min(1, peak));
                    }
                    if (requeue) Check(waveInAddBuffer(handle, buffer.Header, HeaderBytes), "continue microphone capture");
                }
                while (!finish.IsCancellationRequested && !autoStop && watch.Elapsed.TotalSeconds < NemoCliSpeechProvider.MaximumAudioSeconds
                    && written < NemoCliSpeechProvider.MaximumAudioSeconds * 32000)
                {
                    cancel.ThrowIfCancellationRequested();
                    if ((Marshal.PtrToStructure<WaveHeader>(buffers[next].Header).Flags & Done) == 0) { Thread.Sleep(20); continue; }
                    Consume(buffers[next], requeue: true); next = (next + 1) % buffers.Count;
                }
                Check(waveInReset(handle), "finish microphone capture");
                for (var i = 0; i < buffers.Count; i++) Consume(buffers[(next + i) % buffers.Count], requeue: false);
                CommitCalibration();
                cancel.ThrowIfCancellationRequested();
                return new(written / 32000d, normalization);
            }
            finally
            {
                // PCM already written before a cancellation or segment/backlog
                // refusal must remain a playable recovery recording. Do not emit
                // more recognition callbacks while unwinding a failed capture.
                var bytes = checked((int)stream.Length - 44);
                stream.Position = 0; DictationPcmWave.WriteHeader(writer, bytes);
                writer.Flush(); stream.Flush();
            }
        }
        finally
        {
            // Reset returns queued buffers before they are unprepared/freed. Never free memory still owned by a failing driver.
            var reset = waveInReset(handle);
            foreach (var buffer in buffers)
            {
                var released = !buffer.Prepared || waveInUnprepareHeader(handle, buffer.Header, HeaderBytes) == 0;
                if (released) buffer.Free();
                else cleanupError ??= new IOException("The microphone driver did not release an input buffer; restart Cerebral before trying again.");
            }
            var close = waveInClose(handle);
            if (reset != 0 || close != 0) cleanupError ??= new IOException("The microphone did not confirm shutdown; restart Cerebral before trying again.");
            if (cleanupError is not null) throw cleanupError;
        }
    }

    private static uint ResolveDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return uint.MaxValue;
        var matches = InputDevices.Where(device => string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) throw new IOException($"The selected microphone '{name}' is no longer available. Refresh AI Dictation inputs or choose Windows default input.");
        return checked((uint)matches[0].Id);
    }

    internal static double ApplySoftwareGain(Span<byte> pcm16, double boostDb)
    {
        if (pcm16.Length % 2 != 0) throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(pcm16));
        if (!double.IsFinite(boostDb) || boostDb is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(boostDb));
        var multiplier = Math.Pow(10, boostDb / 20d);
        var peak = 0;
        for (var offset = 0; offset < pcm16.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..]);
            var scaled = Math.Clamp((int)Math.Round(sample * multiplier, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16[offset..], (short)scaled);
            peak = Math.Max(peak, Math.Abs(scaled));
        }
        return peak / 32768d;
    }

    internal static AiDictationNormalization NormalizeWaveFile(string path, double targetPeak = .72, double maximumGainDb = 18)
    {
        if (!double.IsFinite(targetPeak) || targetPeak is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(targetPeak));
        if (!double.IsFinite(maximumGainDb) || maximumGainDb is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(maximumGainDb));
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" ||
            Encoding.ASCII.GetString(bytes, 36, 4) != "data") throw new InvalidDataException("AI dictation produced an unsupported WAV recording.");
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        if (dataLength < 0 || dataLength % 2 != 0 || dataLength > bytes.Length - 44) throw new InvalidDataException("AI dictation produced an invalid PCM data length.");
        var result = NormalizePcm16(bytes.AsSpan(44, dataLength), targetPeak, maximumGainDb);
        if (!result.Applied) return result;
        var temporary = path + ".normalize-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }

    internal static AiDictationNormalization NormalizePcm16(Span<byte> pcm16, double targetPeak = .72, double maximumGainDb = 18)
    {
        if (pcm16.Length % 2 != 0) throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(pcm16));
        if (!double.IsFinite(targetPeak) || targetPeak is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(targetPeak));
        if (!double.IsFinite(maximumGainDb) || maximumGainDb is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(maximumGainDb));
        var peak = 0;
        for (var offset = 0; offset < pcm16.Length; offset += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..])));
        var source = peak / 32768d;
        // Near-silence is usually an unplugged/muted device or room noise. Amplifying it produces misleading noise, not speech.
        if (source < .005 || source >= targetPeak) return new(false, source, source, 0);
        var desiredDb = 20 * Math.Log10(targetPeak / source);
        var appliedDb = Math.Min(desiredDb, maximumGainDb);
        var result = ApplySoftwareGain(pcm16, appliedDb);
        return new(appliedDb > .01, source, result, appliedDb);
    }

    private static void Check(uint result, string operation)
    {
        if (result == 0) return;
        var text = new StringBuilder(256); _ = waveInGetErrorText(result, text, (uint)text.Capacity);
        throw new IOException($"Could not {operation}: {text} (WinMM {result}). Check Windows microphone access and default input settings.");
    }

    private sealed class NativeBuffer
    {
        public IntPtr Data { get; } = Marshal.AllocHGlobal(BufferBytes);
        public IntPtr Header { get; } = Marshal.AllocHGlobal((int)HeaderBytes);
        public bool Prepared { get; set; }
        public NativeBuffer() => Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = BufferBytes }, Header, false);
        public void Free() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCapabilities
    {
        public ushort ManufacturerId, ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint Formats;
        public ushort Channels, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint bytes);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint bytes);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint bytes);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")]
    private static extern uint waveInGetDevCaps(UIntPtr device, out WaveInCapabilities capabilities, uint bytes);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetErrorTextW")] private static extern uint waveInGetErrorText(uint code, StringBuilder text, uint chars);
}
