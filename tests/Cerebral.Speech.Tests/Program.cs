using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Speech;

// The child mode is a deterministic executable fixture, never a speech provider or live inference claim.
if (args.Length > 0 && args[0] == "transcribe") return await FixtureChildAsync(args);
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Speech package checks require Windows x64.");
var checks = await CheckProviderAsync();
Console.WriteLine(JsonSerializer.Serialize(new { suite = "speech-provider", evidence = "deterministic native-child fixture", checks }));
if (args.Length > 0 && args[0] == "--live") await CheckLiveAsync(args.Skip(1).ToArray());
return 0;

static async Task<int> CheckProviderAsync()
{
    var checks = 0;
    void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks++; }
    var scratchParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/.scratch"));
    var root = Path.Combine(scratchParent, "speech-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var model = new SpeechModelBinding("fixture", "fixture-only", Path.Combine(root, "fixture.gguf"));
        File.WriteAllBytes(model.ModelPath, [0, 1, 2, 3]);
        var child = Path.Combine(AppContext.BaseDirectory, "Cerebral.Speech.Tests.exe");
        var provider = new NemoCliSpeechProvider(child, TimeSpan.FromSeconds(10));
        Check(provider.Inspect(model).Available, "fixture executable availability");
        var good = Wav(root, "success.wav");
        var result = await provider.TranscribeAsync(new(good), model, CancellationToken.None);
        Check(result.Text == "A bounded fixture transcript." && result.AudioSeconds == 1, "accept exact fixture transcript");
        Check(result.ProviderId == provider.Descriptor.Id && result.ModelId == model.ModelId, "preserve provider/model identity");
        foreach (var entry in new[] { ("malformed.wav", "invalid-output"), ("duplicate.wav", "invalid-output"),
            ("wrongfile.wav", "invalid-output"), ("failure.wav", "transcription-failed"), ("oversized.wav", "output-limit") })
        {
            var file = Wav(root, entry.Item1);
            try { await provider.TranscribeAsync(new(file), model, CancellationToken.None); throw new InvalidOperationException("Expected bounded failure."); }
            catch (SpeechProviderException ex) { Check(ex.Code == entry.Item2, "specific failure: " + entry.Item1); }
        }
        var malformed = Path.Combine(root, "not-wave.wav");
        File.WriteAllText(malformed, "Not a recording");
        try { await provider.TranscribeAsync(new(malformed), model, CancellationToken.None); throw new InvalidOperationException("Expected input rejection."); }
        catch (SpeechProviderException ex) { Check(ex.Code == "invalid-audio", "reject invalid input before child launch"); }

        using var cancelled = new CancellationTokenSource();
        var waiting = Wav(root, "wait.wav");
        var pending = provider.TranscribeAsync(new(waiting), model, cancelled.Token);
        var pid = await WaitForPidAsync(waiting + ".pid", pending);
        using (var process = Process.GetProcessById(pid)) Check(process.MainWindowHandle == IntPtr.Zero, "fixture child has no top-level window");
        try { await provider.TranscribeAsync(new(good), model, CancellationToken.None); throw new InvalidOperationException("Expected busy rejection."); }
        catch (SpeechProviderException ex) { Check(ex.Code == "busy", "overlapping recording is rejected without an unbounded queue"); }
        cancelled.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(8)); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { Check(!Running(pid), "cancellation confirms exact child exited"); }

        var timed = Wav(root, "timeout.wav");
        // Process startup can be delayed on a busy Windows host (for example while Defender is
        // scanning a freshly rebuilt test executable). Keep the provider deadline comfortably
        // beyond the PID handshake so this check proves owned-child timeout cleanup rather than
        // racing executable launch.
        var timeoutProvider = new NemoCliSpeechProvider(child, TimeSpan.FromSeconds(8));
        var timeoutPending = timeoutProvider.TranscribeAsync(new(timed), model, CancellationToken.None);
        var timeoutPid = await WaitForPidAsync(timed + ".pid", timeoutPending);
        try { await timeoutPending; throw new InvalidOperationException("Expected timeout."); }
        catch (SpeechProviderException ex)
        {
            Check(ex.Code == "timeout" && !Running(timeoutPid), "deadline confirms exact child exited");
        }
        var recovered = await provider.TranscribeAsync(new(good), model, CancellationToken.None);
        Check(recovered.Text == result.Text, "provider remains usable after cancellation/failure");
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        try { await provider.TranscribeAsync(new(good), model, preCancelled.Token); throw new InvalidOperationException("Expected precancellation."); }
        catch (OperationCanceledException) { Check(true, "already cancelled request never starts"); }
        return checks;
    }
    finally
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(scratchParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup target.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

static async Task CheckLiveAsync(string[] paths)
{
    var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    var assets = Path.Combine(repository, ".runtime", "speech");
    var exe = paths.Length > 0 ? Path.GetFullPath(paths[0]) : Path.Combine(assets, "nemo-0.1.0-win-x64-cpu", "bin", "nemo-speech.exe");
    var model = paths.Length > 1 ? Path.GetFullPath(paths[1]) : Path.Combine(assets, "models", "nvidia-parakeet-tdt-0.6b-v3", "parakeet-tdt-0.6b-v3.q8_0.gguf");
    var wav = paths.Length > 2 ? Path.GetFullPath(paths[2]) : Path.Combine(assets, "fixtures", "jfk.wav");
    var binding = new SpeechModelBinding("nvidia/parakeet-tdt-0.6b-v3", "541d1f99c6b0c3cd0b11a95167540bb8edefd82b", model);
    var provider = new NemoCliSpeechProvider(exe);
    var result = await provider.TranscribeAsync(new(wav), binding, CancellationToken.None);
    const string expected = "and so my fellow americans ask not what your country can do for you ask what you can do for your country";
    var normalized = Regex.Replace(result.Text.ToLowerInvariant(), "[^a-z ]", "");
    if (normalized != expected || Math.Abs(result.AudioSeconds - 11) > .01)
        throw new InvalidOperationException("Actual Parakeet output did not match the known JFK speech reference.");
    Console.WriteLine(JsonSerializer.Serialize(new { suite = "speech-parakeet-live", evidence = "real official native Windows CPU inference on pinned official WAV", result }));
}

static string Wav(string root, string name)
{
    var path = Path.Combine(root, name);
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, Encoding.ASCII);
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000); writer.Write(new byte[32000]);
    return path;
}

static async Task<int> WaitForPidAsync(string path, Task child)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    while (!File.Exists(path))
    {
        if (child.IsCompleted) { await child; throw new InvalidOperationException("Fixture exited before recording identity."); }
        await Task.Delay(20, deadline.Token);
    }
    return int.Parse(await File.ReadAllTextAsync(path, deadline.Token), System.Globalization.CultureInfo.InvariantCulture);
}

static bool Running(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static async Task<int> FixtureChildAsync(string[] args)
{
    if (args.Length < 2) return 2;
    var file = args[1];
    var name = Path.GetFileName(file);
    if (name is "wait.wav" or "timeout.wav")
    {
        await File.WriteAllTextAsync(file + ".pid.partial", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(file + ".pid.partial", file + ".pid");
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
    if (name == "malformed.wav") { Console.WriteLine("not-json"); return 0; }
    if (name == "duplicate.wav") { Console.WriteLine("{\"text\":\"first\",\"text\":\"second\"}"); return 0; }
    if (name == "failure.wav") { Console.Error.WriteLine("Fixture native failure"); return 7; }
    if (name == "oversized.wav") { Console.Write(new string('x', 600 * 1024)); return 0; }
    Console.WriteLine(JsonSerializer.Serialize(new { file = name == "wrongfile.wav" ? file + ".other" : file, text = "A bounded fixture transcript.", duration = 1 }));
    return 0;
}
