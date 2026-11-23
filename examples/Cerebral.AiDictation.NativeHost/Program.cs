using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Cerebral.AiDictationPlugin;

// All C ABI operations stay on this single synchronous owner thread. The helper
// owns no desktop capability. EOF/close ends its process; native DLLs stay mapped
// until that exit because their OpenMP workers outlive a recognizer.
if (!OperatingSystem.IsWindows() || args.Length != 4 || args[2] is not ("offline" or "streaming") ||
    !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var silence) || silence is < 500 or > 10000)
    return 2;
using var process = Process.GetCurrentProcess();
var available = unchecked((ulong)process.ProcessorAffinity.ToInt64()); ulong chosen = 0;
for (var count = 0; count < 2 && available != 0; count++) { var bit = available & (~available + 1); chosen |= bit; available &= ~bit; }
if (chosen == 0) return 2;
process.ProcessorAffinity = unchecked((nint)chosen); process.PriorityClass = ProcessPriorityClass.BelowNormal;
using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
long requestId = 0;
try
{
    var streaming = args[2] == "streaming";
    // Verify the approved streaming artifact through a read lease held throughout
    // native use. Neither path discovery nor this helper downloads anything.
    using var modelLease = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
    if (streaming && (modelLease.Length != 699872960 ||
        Convert.ToHexString(SHA256.HashData(modelLease)).ToLowerInvariant() != "d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d"))
        throw new InvalidDataException("The installed streaming model does not match the approved NVIDIA artifact.");
    using var sdk = new NemoNativeAsr(args[0], args[1], streaming, silence);
    Send(new(1, 0, "ready", SdkVersion: sdk.Version));
    NemoNativeAsr.RecognitionStream? stream = null;
    List<float>? recording = null; var samples = 0;
    try
    {
        while (true)
        {
            var command = DictationNativeProtocol.ReadAsync<DictationNativeRequest>(input, default).GetAwaiter().GetResult();
            if (command.Version != 1 || command.Id <= requestId || command.Id <= 0)
                throw new InvalidDataException("A stale or incompatible speech request was refused.");
            requestId = command.Id;
            var results = new List<DictationNativeResult>();
            switch (command.Operation)
            {
                case "begin" when stream is null && recording is null && command.Pcm is null:
                    samples = 0;
                    if (streaming) stream = sdk.OpenStream(); else recording = new();
                    break;
                case "audio" when stream is not null || recording is not null:
                    DictationNativeProtocol.CheckPcm(command.Pcm);
                    var pcm = command.Pcm!; var floats = new float[pcm.Length / 2];
                    if (samples > 16000 * 120 - floats.Length) throw new InvalidDataException("A speech recording exceeds120 seconds.");
                    for (var i = 0; i < floats.Length; i++) floats[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
                    samples += floats.Length;
                    if (stream is not null) { stream.Push(floats); Drain(stream, results); } else recording!.AddRange(floats);
                    break;
                case "finish" when (stream is not null || recording is not null) && command.Pcm is null:
                    if (stream is not null) { stream.Finish(); Drain(stream, results); stream.Dispose(); stream = null; }
                    else { if (recording!.Count != 0) results.Add(new(sdk.RecognizeOffline(recording.ToArray()), true)); recording = null; }
                    break;
                case "close" when stream is null && recording is null && command.Pcm is null:
                    sdk.Dispose(); Send(new(1, requestId, "closed")); return 0;
                default: throw new InvalidDataException("The private speech operation is invalid for the current recording state.");
            }
            Send(new(1, requestId, "ok", Results: results.ToArray()));
        }
    }
    finally { stream?.Dispose(); }
}
catch (EndOfStreamException) { return 1; } // Only an acknowledged close confirms graceful completion; never replay lost input.
catch (Exception error) when (error is not OutOfMemoryException)
{
    try { Send(new(1, requestId, "error", Message: error is InvalidDataException ? error.Message : "The local native speech helper could not complete recognition. Check the installed runtime/model.")); }
    catch (Exception sendError) when (sendError is not OutOfMemoryException) { }
    return 1;
}

void Send(DictationNativeResponse response) => DictationNativeProtocol.WriteAsync(output, response, default).GetAwaiter().GetResult();
static void Drain(NemoNativeAsr.RecognitionStream stream, List<DictationNativeResult> results)
{
    var textCharacters = 0;
    while (stream.Next() is { } result)
    {
        textCharacters = checked(textCharacters + result.Text.Length);
        if (results.Count >= 32 || textCharacters > DictationNativeProtocol.MaximumTextCharacters)
            throw new InvalidDataException("The native speech result burst exceeds its bound.");
        results.Add(new(result.Text, result.Final));
    }
}
