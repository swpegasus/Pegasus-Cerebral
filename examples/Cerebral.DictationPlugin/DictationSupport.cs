using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Desktop.Plugins;

namespace Cerebral.DictationPlugin;

internal sealed class DictationPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public string? RecognizerId { get; set; }
    public double MinimumConfidence { get; set; } = .45;
    public bool VoiceControlsEnabled { get; set; }
    public List<string> Vocabulary { get; set; } = [];
    public List<CorrectionPair> Corrections { get; set; } = [];
}

internal sealed class CorrectionPair
{
    public string Heard { get; set; } = "";
    public string Replacement { get; set; } = "";
}

internal sealed class DictationSettingsStore(string directory)
{
    private const int MaximumBytes = 131_072;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "settings.json");

    public DictationPreferences Load()
    {
        if (!File.Exists(path)) return new();
        using var input = File.OpenRead(path);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Dictation settings exceed the 128 KiB limit.");
        var value = JsonSerializer.Deserialize<DictationPreferences>(input, Json) ?? throw new InvalidDataException("Dictation settings are empty.");
        return Normalize(value);
    }

    public void Save(DictationPreferences value)
    {
        value = Normalize(value);
        var root = Path.GetDirectoryName(path)!; Directory.CreateDirectory(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Dictation settings exceed the 128 KiB limit.");
            File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static DictationPreferences Normalize(DictationPreferences value)
    {
        if (value.SchemaVersion != 1) throw new InvalidDataException("This dictation settings version is not supported.");
        if (value.MinimumConfidence is < .20 or > .90) throw new InvalidDataException("Recognition confidence must be between 0.20 and 0.90.");
        value.RecognizerId = string.IsNullOrWhiteSpace(value.RecognizerId) ? null : Bounded(value.RecognizerId.Trim(), 512, "recognizer ID");
        value.Vocabulary = (value.Vocabulary ?? []).Select(x => Bounded((x ?? "").Trim(), 100, "vocabulary phrase"))
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToList();
        if (value.Corrections is null || value.Corrections.Count > 250) throw new InvalidDataException("Use at most 250 correction pairs.");
        value.Corrections = value.Corrections.Select(x => new CorrectionPair
        {
            Heard = Bounded((x?.Heard ?? "").Trim(), 100, "heard phrase"),
            Replacement = Bounded((x?.Replacement ?? "").Trim(), 200, "replacement")
        }).Where(x => x.Heard.Length > 0).GroupBy(x => x.Heard, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).ToList();
        return value;
    }

    private static string Bounded(string value, int maximum, string name)
    {
        if (value.Length > maximum || value.Any(char.IsControl)) throw new InvalidDataException($"Each {name} must be at most {maximum} characters without control characters.");
        return value;
    }
}

internal static class DictationText
{
    public static IReadOnlyList<CorrectionPair> ParseCorrections(string text)
    {
        var result = new List<CorrectionPair>(); var lineNumber = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++; var line = raw.Trim(); if (line.Length == 0) continue;
            var marker = line.IndexOf("=>", StringComparison.Ordinal);
            if (marker < 1) throw new InvalidDataException($"Correction line {lineNumber} must use heard phrase => replacement.");
            var heard = line[..marker].Trim(); var replacement = line[(marker + 2)..].Trim();
            if (heard.Length == 0 || replacement.Length == 0) throw new InvalidDataException($"Correction line {lineNumber} needs text on both sides of =>.");
            result.Add(new() { Heard = heard, Replacement = replacement });
        }
        return DictationSettingsStore.Normalize(new DictationPreferences { Corrections = result }).Corrections;
    }

    public static string ApplyCorrections(string text, IReadOnlyList<CorrectionPair> corrections)
    {
        var value = Regex.Replace(text.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        foreach (var correction in corrections.OrderByDescending(x => x.Heard.Length))
        {
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(correction.Heard) + @"(?![\p{L}\p{N}])";
            value = Regex.Replace(value, pattern, correction.Replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return value;
    }

    public static string PrepareInsertion(IDesktopTextEntry target, string recognized)
    {
        return Cerebral.Dictation.DictatedTextInsertion.Capture(target).Prepare(recognized);
    }
}

internal enum VoiceControlCommand { SendMessage, SteerMessage, StopWork, StopDictation }

internal readonly record struct VoiceControlMatch(bool ExactPhrase, bool Confident, VoiceControlCommand Command);

internal static class VoiceControls
{
    public const double MinimumConfidence = .75;
    public const string SendPhrase = "Cerebral command send message";
    public const string SteerPhrase = "Cerebral command steer message";
    public const string StopWorkPhrase = "Cerebral command stop work";
    public const string StopDictationPhrase = "Cerebral command stop dictation";
    public static IReadOnlyList<string> Phrases { get; } = [SendPhrase, SteerPhrase, StopWorkPhrase, StopDictationPhrase];
    public const string ConfigurationGuidance =
        "While Dictate is listening in the main message box, say exactly: “Cerebral command send message,” “Cerebral command steer message,” “Cerebral command stop work,” or “Cerebral command stop dictation.” Commands require at least 75% confidence, stop listening before they act, and are handled by the interface instead of being sent to the model.";

    public static VoiceControlMatch Match(string text, double confidence, double dictationThreshold)
    {
        var normalized = Normalize(text);
        var command = normalized switch
        {
            "cerebral command send message" => VoiceControlCommand.SendMessage,
            "cerebral command steer message" => VoiceControlCommand.SteerMessage,
            "cerebral command stop work" => VoiceControlCommand.StopWork,
            "cerebral command stop dictation" => VoiceControlCommand.StopDictation,
            _ => (VoiceControlCommand?)null
        };
        return command is { } exact
            ? new(true, confidence >= Math.Max(MinimumConfidence, dictationThreshold), exact)
            : default;
    }

    public static string Label(VoiceControlCommand command) => command switch
    {
        VoiceControlCommand.SendMessage => "send message",
        VoiceControlCommand.SteerMessage => "steer message",
        VoiceControlCommand.StopWork => "stop work",
        VoiceControlCommand.StopDictation => "stop dictation",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public static string RequestId(VoiceControlCommand command, DesktopDraftSnapshot draft)
    {
        var material = $"{command}\n{draft.Binding.Generation}\n{draft.Binding.SessionId}\n{draft.Revision}\n{draft.Text}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"dictation-{Label(command).Replace(' ', '-')}-{hash[..24]}";
    }

    private static string Normalize(string text) => Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"[\s\p{P}\p{S}]+", " ", RegexOptions.CultureInvariant).Trim();
}

internal static class VoiceControlExecutor
{
    public static async Task<DesktopOperationResult> ExecuteAsync(IDesktopPluginHost host, VoiceControlCommand command,
        IDesktopTextEntry source, DesktopConversationBinding expectedBinding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (command == VoiceControlCommand.StopDictation)
            return new(DesktopOperationState.Completed, "Dictation stopped. No agent work was changed.");
        if (!source.Id.Equals("composer", StringComparison.Ordinal))
            return new(DesktopOperationState.Rejected, "Voice controls for agent work are available only from the main message box. This field remains available for dictation.");

        var conversation = host.GetConversation();
        if (conversation.Binding != expectedBinding)
            return new(DesktopOperationState.Rejected, "The conversation changed before the voice command could run.");
        if (command == VoiceControlCommand.StopWork)
            return conversation.IsRunning
                ? await host.StopAsync(conversation.Binding, cancellationToken)
                : new(DesktopOperationState.Rejected, "There is no active work to stop.");

        var draft = host.GetDraft();
        if (draft.Binding != conversation.Binding)
            return new(DesktopOperationState.Rejected, "The conversation changed before the voice command could run.");
        if (string.IsNullOrWhiteSpace(draft.Text))
            return new(DesktopOperationState.Rejected, "The message box is empty. Dictate a message before using this voice command.");

        var submission = new DesktopSubmission(draft.Binding, VoiceControls.RequestId(command, draft), draft.Text);
        return command switch
        {
            VoiceControlCommand.SendMessage when conversation.IsRunning =>
                new(DesktopOperationState.Rejected, $"Work is already running. Say “{VoiceControls.SteerPhrase}” to add this message to it."),
            VoiceControlCommand.SendMessage => await host.SubmitAsync(submission, cancellationToken),
            VoiceControlCommand.SteerMessage when !conversation.IsRunning =>
                new(DesktopOperationState.Rejected, $"There is no active work to steer. Say “{VoiceControls.SendPhrase}” to start it."),
            VoiceControlCommand.SteerMessage => await host.SteerAsync(submission, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}

internal sealed record SpeechRecognizerChoice(string Id, string Label, CultureInfo Culture);
internal sealed record SpeechEnvironment(IReadOnlyList<SpeechRecognizerChoice> Recognizers, string Message)
{
    public bool Available => Recognizers.Count > 0;
}

internal static class WindowsSpeechSupport
{
    public static SpeechEnvironment Inspect()
    {
        if (!OperatingSystem.IsWindows()) return new([], "Windows desktop speech recognition is available only on Windows.");
        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers().Select(x =>
                new SpeechRecognizerChoice(x.Id, $"{x.Description} ({x.Culture.DisplayName})", x.Culture)).ToList();
            return recognizers.Count == 0
                ? new([], "No compatible Windows desktop speech recognizer is installed for this user. Add a Windows speech language, then reopen the plugin.")
                : new(recognizers, $"{recognizers.Count} offline Windows speech recognizer{(recognizers.Count == 1 ? "" : "s")} available.");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { return new([], "Windows speech recognition is unavailable: " + ex.Message); }
    }
}

internal sealed record SpeechTrainingLaunch(string FileName, string Argument);
internal sealed record SpeechCenterLaunch(string FileName, string Argument, bool UsesCanonicalName);

internal static class WindowsSpeechTraining
{
    internal const string UserTrainingArgument = "UserTraining";
    internal const string TrainActionLabel = "Train Windows speech profile";
    internal const string TestActionLabel = "Test microphone and profile";
    internal const string CenterActionLabel = "Open Speech Recognition Center";
    internal const string SpeechRecognitionCanonicalName = "Microsoft.SpeechRecognition";
    internal const string InitialGuidance = "Windows applies completed voice training to the current speech profile; Cerebral has no separate training save. After a completed pass, close the Windows wizard and select Test microphone and profile here.";
    internal const string OpenedGuidance = "Windows voice training is open. Complete the pass in the wizard. Windows applies completed training to the current speech profile; Cerebral has no separate Save step. If the wizard returns to its first page after you finish, close it, then select Test microphone and profile here.";
    internal const string ClosedGuidance = "Windows voice training closed. Windows applies completed training to the current speech profile; Cerebral has no separate Save step or completion check. Select Test microphone and profile now. Reopen training only if recognition remains inaccurate.";
    internal const string OwnershipGuidance = "Windows owns the training profile and does not expose a documented completion flag that Cerebral can verify. Run another training pass only if recognition remains inaccurate. Save dictation settings stores Cerebral's speech language, filtering, vocabulary, and corrections.";

    public static SpeechTrainingLaunch Resolve() => Resolve(Environment.SystemDirectory, File.Exists);

    public static SpeechCenterLaunch ResolveCenter() => ResolveCenter(Environment.SystemDirectory, File.Exists);

    internal static SpeechTrainingLaunch Resolve(string systemDirectory, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(systemDirectory)) throw new ArgumentException("Windows did not provide its system directory.", nameof(systemDirectory));
        ArgumentNullException.ThrowIfNull(fileExists);
        var wizard = Path.Combine(Path.GetFullPath(systemDirectory), "Speech", "SpeechUX", "SpeechUXWiz.exe");
        if (!fileExists(wizard))
            throw new FileNotFoundException($"Windows Speech Recognition voice training is not installed at {wizard}. Open Control Panel > Ease of Access > Speech Recognition and confirm that Train your computer to better understand you is available.", wizard);
        return new(wizard, UserTrainingArgument);
    }

    internal static SpeechCenterLaunch ResolveCenter(string systemDirectory, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(systemDirectory)) throw new ArgumentException("Windows did not provide its system directory.", nameof(systemDirectory));
        ArgumentNullException.ThrowIfNull(fileExists);
        var controlPanel = Path.Combine(Path.GetFullPath(systemDirectory), "control.exe");
        if (!fileExists(controlPanel))
            throw new FileNotFoundException($"Windows Control Panel was not found at {controlPanel}.", controlPanel);
        var speechProperties = Path.Combine(Path.GetFullPath(systemDirectory), "Speech", "SpeechUX", "sapi.cpl");
        return fileExists(speechProperties)
            ? new(controlPanel, speechProperties, false)
            : new(controlPanel, SpeechRecognitionCanonicalName, true);
    }

    internal static ProcessStartInfo StartInfo(SpeechTrainingLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var start = new ProcessStartInfo { FileName = launch.FileName, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launch.FileName)! };
        start.ArgumentList.Add(launch.Argument);
        return start;
    }

    internal static ProcessStartInfo StartInfo(SpeechCenterLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var start = new ProcessStartInfo { FileName = launch.FileName, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launch.FileName)! };
        if (launch.UsesCanonicalName) start.ArgumentList.Add("/name");
        start.ArgumentList.Add(launch.Argument);
        return start;
    }
}

internal static class DictationFeedback
{
    public const string PluginId = "pegasus.dictation";
    public const string ActionId = "dictate";

    public static void Publish(IDesktopTextEntry target, DesktopTextEntryActivityState state, string message,
        bool active, string? buttonLabel = null)
    {
        if (target is not IDesktopTextEntryActivitySink sink) return;
        try { sink.ReportActivity(new(PluginId, ActionId, state, message, active, buttonLabel)); }
        catch (ObjectDisposedException) { }
    }
}

internal sealed class WindowsDictationSession(Func<DictationPreferences> getPreferences,
    Func<DesktopConversationBinding> getVoiceControlBinding,
    Func<VoiceControlCommand, IDesktopTextEntry, DesktopConversationBinding, CancellationToken, Task<DesktopOperationResult>> executeVoiceControl,
    Action<string, bool> report,
    Action<int, bool> reportAudioLevel,
    Action<string?, bool> reportAudioSignal,
    Func<Func<Task<DesktopOperationResult>>, Task<DesktopOperationResult>>? dispatchInsertion = null) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim recognitionGate = new(1, 1);
    private SpeechRecognitionEngine? engine;
    private IDesktopTextEntry? target;
    private TaskCompletionSource? stopCompletion;
    private CancellationTokenSource? listeningCancellation;
    private CancellationTokenRegistration targetAvailability;
    private DictationPreferences preferences = new();
    private SpeechRecognitionEngine? commandEngine;
    private DesktopConversationBinding? voiceControlBinding;
    private long lastAudioReportTick;
    private int lastAudioLevel = -1;
    private bool isStopping;
    private bool disposed;

    public async Task ToggleAsync(IDesktopTextEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry); cancellationToken.ThrowIfCancellationRequested();
        SpeechRecognitionEngine? stopping; IDesktopTextEntry? previous; Task? stopped;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stopping = engine; previous = target; stopped = stopCompletion?.Task;
        }
        if (stopping is not null)
        {
            RequestStop(stopping, previous, "Stopping dictation…");
            if (stopped is not null)
            {
                try { await stopped.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
                catch (TimeoutException) { Finish(stopping, "Dictation stopped."); }
            }
            if (previous?.Id == entry.Id) return;
        }

        DictationFeedback.Publish(entry, DesktopTextEntryActivityState.Starting, "Starting dictation for this field…", false, "Starting dictation…");
        try { Start(entry); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = "Dictation could not start. Check the Windows speech language, default microphone, and microphone privacy setting. " + ex.Message;
            DictationFeedback.Publish(entry, DesktopTextEntryActivityState.Error, message, false);
            report(message, false);
        }
    }

    private void Start(IDesktopTextEntry entry)
    {
        var environment = WindowsSpeechSupport.Inspect();
        if (!environment.Available) throw new InvalidOperationException(environment.Message);
        preferences = getPreferences();
        var choice = environment.Recognizers.FirstOrDefault(x => x.Id == preferences.RecognizerId) ?? environment.Recognizers[0];
        var controlBinding = preferences.VoiceControlsEnabled && entry.Id.Equals("composer", StringComparison.Ordinal)
            ? getVoiceControlBinding() : (DesktopConversationBinding?)null;
        var next = new SpeechRecognitionEngine(choice.Id);
        var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            next.LoadGrammar(new DictationGrammar { Name = "Free dictation" });
            if (preferences.Vocabulary.Count > 0)
            {
                var builder = new GrammarBuilder(new Choices(preferences.Vocabulary.ToArray())) { Culture = choice.Culture };
                next.LoadGrammar(new Grammar(builder) { Name = "Custom vocabulary" });
            }
            if (preferences.VoiceControlsEnabled && entry.Id.Equals("composer", StringComparison.Ordinal))
            {
                var commands = new GrammarBuilder(new Choices(VoiceControls.Phrases.ToArray())) { Culture = choice.Culture };
                next.LoadGrammar(new Grammar(commands) { Name = "Cerebral voice controls" });
            }
            next.SpeechRecognized += OnSpeechRecognized;
            next.SpeechRecognitionRejected += OnSpeechRejected;
            next.AudioLevelUpdated += OnAudioLevelUpdated;
            next.AudioSignalProblemOccurred += OnAudioSignalProblemOccurred;
            next.RecognizeCompleted += OnRecognizeCompleted;
            next.SetInputToDefaultAudioDevice();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (engine is not null) throw new InvalidOperationException("Dictation is already listening.");
                engine = next; target = entry; stopCompletion = completion; listeningCancellation = nextCancellation; voiceControlBinding = controlBinding;
                isStopping = false;
                lastAudioReportTick = 0; lastAudioLevel = -1;
            }
            if (entry is IDesktopTextEntryLifetime entryLifetime)
            {
                var registration = entryLifetime.AvailabilityToken.Register(() => OnTargetUnavailable(next, entry));
                lock (gate)
                {
                    if (ReferenceEquals(engine, next)) targetAvailability = registration;
                    else registration.Dispose();
                }
            }
            next.RecognizeAsync(RecognizeMode.Multiple);
            reportAudioLevel(0, true);
            reportAudioSignal(null, true);
            var controls = preferences.VoiceControlsEnabled && entry.Id.Equals("composer", StringComparison.Ordinal)
                ? " Explicit Cerebral commands are enabled." : "";
            var message = $"Listening with {choice.Label}.{controls} Speak, pause briefly, or select Stop dictation.";
            DictationFeedback.Publish(entry, DesktopTextEntryActivityState.Listening, message, true, "⏹ Stop dictation");
            report(message, true);
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(engine, next))
                {
                    engine = null; target = null; stopCompletion?.TrySetResult(); stopCompletion = null;
                    listeningCancellation = null; voiceControlBinding = null; isStopping = false;
                }
            }
            nextCancellation.Cancel(); nextCancellation.Dispose();
            targetAvailability.Dispose(); targetAvailability = default; reportAudioLevel(0, false); reportAudioSignal(null, false);
            Detach(next); next.Dispose(); throw;
        }
    }

    private async void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs args)
    {
        try { await recognitionGate.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        IDesktopTextEntry? destination = null; SpeechRecognitionEngine? active = null; DesktopConversationBinding? expectedBinding = null;
        CancellationToken listeningToken = default;
        try
        {
            lock (gate)
            {
                destination = target; active = engine; expectedBinding = voiceControlBinding;
                listeningToken = listeningCancellation?.Token ?? new CancellationToken(true);
            }
            if (destination is null || active is null || !ReferenceEquals(sender, active)) return;
            listeningToken.ThrowIfCancellationRequested();
            if (preferences.VoiceControlsEnabled && destination.Id.Equals("composer", StringComparison.Ordinal))
            {
                var control = VoiceControls.Match(args.Result.Text, args.Result.Confidence, preferences.MinimumConfidence);
                if (control.ExactPhrase)
                {
                    if (!control.Confident)
                    {
                        var uncertain = $"Possible voice command heard at {args.Result.Confidence:P0} confidence. No command ran and no command text was inserted; repeat it clearly.";
                        DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Result, uncertain, true, "⏹ Stop dictation");
                        report(uncertain, true); return;
                    }
                    if (expectedBinding is null)
                        throw new InvalidOperationException("The voice command has no captured conversation binding.");
                    lock (gate)
                    {
                        if (isStopping || !ReferenceEquals(engine, active) || ReferenceEquals(commandEngine, active)) return;
                        commandEngine = active;
                    }
                    await ExecuteControlAsync(active, destination, expectedBinding, control.Command);
                    return;
                }
            }
            if (args.Result.Confidence < preferences.MinimumConfidence)
            {
                var low = $"Speech was heard at {args.Result.Confidence:P0} confidence and was not inserted. Keep listening or lower the recognition filter in Configuration.";
                DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Result, low, true, "⏹ Stop dictation");
                report(low, true); return;
            }
            var corrected = DictationText.ApplyCorrections(args.Result.Text, preferences.Corrections);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(listeningToken,
                destination is IDesktopTextEntryLifetime entryLifetime ? entryLifetime.AvailabilityToken : CancellationToken.None);
            Task<DesktopOperationResult> InsertPhrase()
            {
                linked.Token.ThrowIfCancellationRequested();
                var span = Cerebral.Dictation.DictatedTextInsertion.Capture(destination);
                var insertion = span.Prepare(corrected);
                if (!span.Matches(destination))
                    return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,
                        "The text or cursor changed before dictation could insert. Existing text was preserved."));
                return insertion.Length == 0
                    ? Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed))
                    : destination.InsertAsync(insertion, linked.Token);
            }
            // Read the selection and perform the replacement in one owner-dispatcher turn.
            var outcome = await (dispatchInsertion is null ? InsertPhrase() : dispatchInsertion(InsertPhrase));
            var success = outcome.State == DesktopOperationState.Completed;
            var message = success ? $"Inserted “{corrected}” ({args.Result.Confidence:P0}). Listening…" :
                outcome.Message ?? "The dictated phrase could not be inserted.";
            DictationFeedback.Publish(destination, success ? DesktopTextEntryActivityState.Result : DesktopTextEntryActivityState.Error,
                message, true, "⏹ Stop dictation");
            report(message, true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || listeningToken.IsCancellationRequested ||
            destination is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true }) { }
        catch (Exception ex)
        {
            var message = "Dictation result failed: " + ex.Message;
            if (destination is not null)
                DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Error, message, true, "⏹ Stop dictation");
            report(message, true);
        }
        finally { recognitionGate.Release(); }
    }

    private async Task ExecuteControlAsync(SpeechRecognitionEngine active, IDesktopTextEntry destination,
        DesktopConversationBinding expectedBinding, VoiceControlCommand command)
    {
        Task? stopped;
        lock (gate) stopped = stopCompletion?.Task;
        var label = VoiceControls.Label(command);
        try
        {
            RequestStop(active, destination, $"Voice command recognized: {label}. Stopping dictation before it runs…");
            if (stopped is not null)
            {
                try { await stopped.WaitAsync(TimeSpan.FromSeconds(3), lifetime.Token); }
                catch (TimeoutException) { Finish(active, "Dictation stopped for the voice command."); }
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,
                destination is IDesktopTextEntryLifetime entryLifetime ? entryLifetime.AvailabilityToken : CancellationToken.None);
            var outcome = await executeVoiceControl(command, destination, expectedBinding, linked.Token);
            var detail = (outcome.Message ?? outcome.State.ToString()).Trim();
            var message = $"Voice command {label}: {detail}";
            var success = outcome.State is DesktopOperationState.Accepted or DesktopOperationState.Completed;
            DictationFeedback.Publish(destination, success ? DesktopTextEntryActivityState.Result : DesktopTextEntryActivityState.Error, message, false);
            report(message, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested ||
            destination is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true }) { }
        catch (Exception ex)
        {
            var message = $"Voice command {label} failed: {ex.Message}";
            DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Error, message, false);
            report(message, false);
        }
        finally
        {
            lock (gate) if (ReferenceEquals(commandEngine, active)) commandEngine = null;
        }
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs args)
    {
        IDesktopTextEntry? destination;
        lock (gate)
        {
            if (isStopping || commandEngine is not null || !ReferenceEquals(sender, engine)) return;
            destination = target;
        }
        if (destination is null) return;
        const string message = "Speech was heard but not recognized. Keep listening, try a shorter phrase, or select Stop dictation.";
        DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Result, message, true, "⏹ Stop dictation");
        report(message, true);
    }

    private void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs args)
    {
        var level = Math.Clamp(args.AudioLevel, 0, 100);
        var now = Environment.TickCount64;
        lock (gate)
        {
            if (isStopping || !ReferenceEquals(sender, engine) || commandEngine is not null) return;
            if (lastAudioReportTick != 0 && now - lastAudioReportTick < 80 && Math.Abs(level - lastAudioLevel) < 8) return;
            lastAudioReportTick = now; lastAudioLevel = level;
        }
        reportAudioLevel(level, true);
    }

    private void OnAudioSignalProblemOccurred(object? sender, AudioSignalProblemOccurredEventArgs args)
    {
        lock (gate) if (isStopping || !ReferenceEquals(sender, engine) || commandEngine is not null) return;
        var detail = args.AudioSignalProblem switch
        {
            AudioSignalProblem.None => null,
            AudioSignalProblem.NoSignal => "No microphone signal",
            AudioSignalProblem.TooSoft => "Input is too quiet",
            AudioSignalProblem.TooLoud => "Input is too loud",
            AudioSignalProblem.TooNoisy => "Background noise is too high",
            AudioSignalProblem.TooFast => "Speech is too fast",
            AudioSignalProblem.TooSlow => "Speech is too slow",
            _ => args.AudioSignalProblem.ToString()
        };
        reportAudioSignal(detail, true);
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        if (sender is not SpeechRecognitionEngine completed) return;
        var message = args.Error is null ? "Dictation stopped." : "Dictation stopped: " + args.Error.Message;
        Finish(completed, message);
    }

    private void OnTargetUnavailable(SpeechRecognitionEngine active, IDesktopTextEntry destination)
    {
        lock (gate) if (!ReferenceEquals(engine, active)) return;
        const string message = "This text field closed or refreshed, so dictation stopped.";
        DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Error, message, false);
        RequestStop(active, destination, message);
    }

    private void RequestStop(SpeechRecognitionEngine active, IDesktopTextEntry? destination, string message)
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (ReferenceEquals(active, engine)) isStopping = true;
            cancellation = ReferenceEquals(active, engine) ? listeningCancellation : null;
        }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        if (destination is not null)
            DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Stopping, message, true, "Stopping dictation…");
        report(message, true);
        try { active.RecognizeAsyncCancel(); }
        catch (ObjectDisposedException) { Finish(active, "Dictation stopped."); }
        catch (InvalidOperationException) { Finish(active, "Dictation stopped."); }
    }

    private void Finish(SpeechRecognitionEngine completed, string message)
    {
        IDesktopTextEntry? destination; TaskCompletionSource? completion; CancellationTokenRegistration availability; CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (!ReferenceEquals(engine, completed)) return;
            destination = target; completion = stopCompletion; availability = targetAvailability; cancellation = listeningCancellation;
            engine = null; target = null; stopCompletion = null; listeningCancellation = null; targetAvailability = default;
            voiceControlBinding = null; isStopping = false;
        }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        cancellation?.Dispose(); availability.Dispose(); Detach(completed); completed.Dispose();
        if (destination is not null) DictationFeedback.Publish(destination, DesktopTextEntryActivityState.Idle, message, false);
        completion?.TrySetResult(); reportAudioLevel(0, false); reportAudioSignal(null, false); report(message, false);
    }

    private void Detach(SpeechRecognitionEngine value)
    {
        value.SpeechRecognized -= OnSpeechRecognized;
        value.SpeechRecognitionRejected -= OnSpeechRejected;
        value.AudioLevelUpdated -= OnAudioLevelUpdated;
        value.AudioSignalProblemOccurred -= OnAudioSignalProblemOccurred;
        value.RecognizeCompleted -= OnRecognizeCompleted;
    }

    public void Stop(string message = "Stopping dictation because the conversation changed…")
    {
        SpeechRecognitionEngine? active; IDesktopTextEntry? destination;
        lock (gate) { active = engine; destination = target; }
        if (active is not null) RequestStop(active, destination, message);
    }

    public void Dispose()
    {
        SpeechRecognitionEngine? active; TaskCompletionSource? completion; CancellationTokenRegistration availability; CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; active = engine; completion = stopCompletion; availability = targetAvailability; cancellation = listeningCancellation;
            engine = null; target = null; stopCompletion = null; listeningCancellation = null; targetAvailability = default;
            voiceControlBinding = null; isStopping = true;
        }
        lifetime.Cancel();
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        cancellation?.Dispose(); availability.Dispose();
        if (active is not null)
        {
            try { active.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
            Detach(active); active.Dispose();
        }
        completion?.TrySetResult(); reportAudioLevel(0, false); reportAudioSignal(null, false);
    }
}

/// <summary>Bounded deterministic checks used by the repository's desktop-plugin acceptance executable.</summary>
public static class DictationPluginDiagnostics
{
    public static string RunDeterministicChecks(string directory)
    {
        var checks = 0; var store = new DictationSettingsStore(directory);
        var corrections = DictationText.ParseCorrections("see sharp => C#\ncue when => Qwen\nSEE SHARP => C Sharp");
        Require(corrections.Count == 2 && corrections.Single(x => x.Heard.Equals("see sharp", StringComparison.OrdinalIgnoreCase)).Replacement == "C Sharp", "Correction parsing must be case-insensitive and last-write-wins.", ref checks);
        Require(DictationText.ApplyCorrections("Use see sharp and cue when.", corrections) == "Use C Sharp and Qwen.", "Corrections must replace complete phrases.", ref checks);
        var value = new DictationPreferences { MinimumConfidence = .60, VoiceControlsEnabled = true,
            Vocabulary = [" Pegasus ", "Cerebral", "pegasus"], Corrections = corrections.ToList() };
        store.Save(value); var loaded = store.Load();
        Require(loaded.MinimumConfidence == .60 && loaded.VoiceControlsEnabled && loaded.Vocabulary.SequenceEqual(["Pegasus", "Cerebral"]),
            "Settings must round-trip with normalized vocabulary and the explicit voice-control choice.", ref checks);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"schemaVersion\":1,\"minimumConfidence\":0.45,\"vocabulary\":[],\"corrections\":[]}");
        Require(!store.Load().VoiceControlsEnabled, "Existing version-one settings without the additive voice-control field must remain disabled.", ref checks);
        store.Save(value);
        var sendCommand = VoiceControls.Match("  CEREBRAL command send message. ", .84, .45);
        var lowCommand = VoiceControls.Match(VoiceControls.StopWorkPhrase, .70, .45);
        Require(sendCommand is { ExactPhrase: true, Confident: true, Command: VoiceControlCommand.SendMessage },
            "An exact, confident, punctuation-normalized activation phrase must select one command.", ref checks);
        Require(lowCommand is { ExactPhrase: true, Confident: false, Command: VoiceControlCommand.StopWork },
            "A command-shaped phrase below the independent command threshold must be recognized but never admitted.", ref checks);
        Require(!VoiceControls.Match("Please " + VoiceControls.SendPhrase, .99, .45).ExactPhrase &&
            !VoiceControls.Match(VoiceControls.SendPhrase + " right now", .99, .45).ExactPhrase,
            "Words before or after the exact activation phrase must remain ordinary dictation.", ref checks);
        Require(!new SpeechEnvironment([], "missing").Available && new SpeechEnvironment([new("id", "label", CultureInfo.GetCultureInfo("en-US"))], "ready").Available,
            "Availability must follow the installed-recognizer set.", ref checks);
        var systemDirectory = Path.GetFullPath(Path.Combine(directory, "diagnostic-system32"));
        var expectedWizard = Path.Combine(systemDirectory, "Speech", "SpeechUX", "SpeechUXWiz.exe");
        var training = WindowsSpeechTraining.Resolve(systemDirectory, path => path.Equals(expectedWizard, StringComparison.OrdinalIgnoreCase));
        var trainingStart = WindowsSpeechTraining.StartInfo(training);
        Require(training.FileName == expectedWizard && training.Argument == "UserTraining" && trainingStart.FileName == expectedWizard &&
            trainingStart.UseShellExecute && trainingStart.ArgumentList.SequenceEqual(["UserTraining"]),
            "Speech training must launch the installed SpeechUXWiz executable in UserTraining mode.", ref checks);
        var expectedControlPanel = Path.Combine(systemDirectory, "control.exe");
        var expectedSpeechProperties = Path.Combine(systemDirectory, "Speech", "SpeechUX", "sapi.cpl");
        var speechCenter = WindowsSpeechTraining.ResolveCenter(systemDirectory,
            path => path.Equals(expectedControlPanel, StringComparison.OrdinalIgnoreCase) || path.Equals(expectedSpeechProperties, StringComparison.OrdinalIgnoreCase));
        var speechCenterStart = WindowsSpeechTraining.StartInfo(speechCenter);
        Require(speechCenter.FileName == expectedControlPanel && speechCenter.Argument == expectedSpeechProperties && !speechCenter.UsesCanonicalName &&
            speechCenterStart.UseShellExecute && speechCenterStart.ArgumentList.SequenceEqual([expectedSpeechProperties]),
            "Speech profile management must prefer the installed classic SAPI Speech Properties applet.", ref checks);
        Require(WindowsSpeechTraining.InitialGuidance.Contains("current speech profile", StringComparison.OrdinalIgnoreCase) &&
            WindowsSpeechTraining.InitialGuidance.Contains("no separate training save", StringComparison.OrdinalIgnoreCase) &&
            WindowsSpeechTraining.OpenedGuidance.Contains(WindowsSpeechTraining.TestActionLabel, StringComparison.Ordinal) &&
            WindowsSpeechTraining.ClosedGuidance.Contains(WindowsSpeechTraining.TestActionLabel, StringComparison.Ordinal) &&
            WindowsSpeechTraining.OwnershipGuidance.Contains("completion flag", StringComparison.OrdinalIgnoreCase),
            "Speech training guidance must explain Windows profile ownership, Cerebral's save boundary, the next microphone test, and the verification limit.", ref checks);
        var missingTrainingReported = false;
        try { _ = WindowsSpeechTraining.Resolve(systemDirectory, _ => false); }
        catch (FileNotFoundException ex)
        {
            missingTrainingReported = ex.FileName == expectedWizard && ex.Message.Contains("Control Panel > Ease of Access > Speech Recognition", StringComparison.Ordinal);
        }
        Require(missingTrainingReported, "A missing Windows training wizard must return its exact expected path and actionable Control Panel guidance.", ref checks);
        var malformedRejected = false;
        try { _ = DictationText.ParseCorrections("missing separator"); } catch (InvalidDataException) { malformedRejected = true; }
        Require(malformedRejected, "Malformed correction lines must be rejected.", ref checks);
        var binding = new DesktopConversationBinding("dictation-diagnostic", "session-one");
        using var composer = new DiagnosticTextEntry("", 0, 0, "composer");
        var voiceHost = new DiagnosticPluginHost(binding, false, "Build the requested feature.");
        var send = VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SendMessage, composer, binding, CancellationToken.None).GetAwaiter().GetResult();
        Require(send.State == DesktopOperationState.Accepted && voiceHost.Submissions == 1 && voiceHost.Steers == 0 && voiceHost.LastSubmission?.Text == "Build the requested feature.",
            "Send message must use the current bound draft and the host's structured submit hook exactly once.", ref checks);
        var firstRequest = voiceHost.LastSubmission!.RequestId;
        _ = VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SendMessage, composer, binding, CancellationToken.None).GetAwaiter().GetResult();
        Require(voiceHost.LastSubmission!.RequestId == firstRequest,
            "Repeating an unchanged send after an uncertain receipt must derive the same request identity for reconciliation.", ref checks);
        voiceHost.IsRunning = true; voiceHost.DraftText = "Add the failure-path test."; voiceHost.DraftRevision++;
        var steer = VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SteerMessage, composer, binding, CancellationToken.None).GetAwaiter().GetResult();
        var stop = VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.StopWork, composer, binding, CancellationToken.None).GetAwaiter().GetResult();
        Require(steer.State == DesktopOperationState.Accepted && stop.State == DesktopOperationState.Accepted && voiceHost.Steers == 1 && voiceHost.Stops == 1,
            "Steer and stop work must use their existing structured host hooks when work is active.", ref checks);
        voiceHost.IsRunning = false;
        Require(VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SteerMessage, composer, binding, CancellationToken.None).Result.State == DesktopOperationState.Rejected &&
            VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.StopWork, composer, binding, CancellationToken.None).Result.State == DesktopOperationState.Rejected,
            "Steer and stop work must reject clearly when no assignment is active.", ref checks);
        var staleBinding = new DesktopConversationBinding(binding.Generation, "session-stale");
        var callsBeforeStaleCommand = voiceHost.Submissions + voiceHost.Steers + voiceHost.Stops;
        var stale = VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SendMessage, composer, staleBinding, CancellationToken.None).Result;
        Require(stale.State == DesktopOperationState.Rejected && voiceHost.Submissions + voiceHost.Steers + voiceHost.Stops == callsBeforeStaleCommand,
            "A voice command captured for a previous conversation must be rejected without invoking a host operation.", ref checks);
        using var question = new DiagnosticTextEntry("Answer text", 0, 0, "question-one");
        Require(VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.SendMessage, question, binding, CancellationToken.None).Result.State == DesktopOperationState.Rejected,
            "Agent-work voice controls must not guess how to submit a question-answer field.", ref checks);
        Require(VoiceControlExecutor.ExecuteAsync(voiceHost, VoiceControlCommand.StopDictation, composer, binding, CancellationToken.None).Result is
            { State: DesktopOperationState.Completed } && voiceHost.Stops == 1,
            "Stop dictation must remain local and must not stop agent work.", ref checks);
        using var target = new DiagnosticTextEntry("Existing text", 8, 5);
        var insertion = DictationText.PrepareInsertion(target, "new words");
        Require(insertion == " new words", "Insertion must preserve word spacing at the selected destination.", ref checks);
        var inserted = target.InsertAsync(insertion).GetAwaiter().GetResult();
        Require(inserted.State == DesktopOperationState.Completed && target.Text == "Existing new words",
            "Recognized text must replace the exact target selection.", ref checks);
        DictationFeedback.Publish(target, DesktopTextEntryActivityState.Listening, "Listening for the fixture…", true, "Stop fixture");
        Require(target.Activity is { PluginId: DictationFeedback.PluginId, ActionId: DictationFeedback.ActionId,
            State: DesktopTextEntryActivityState.Listening, Active: true, ButtonLabel: "Stop fixture" },
            "Listening feedback must stay bound to the exact text entry action.", ref checks);
        target.Close();
        Require(target.AvailabilityToken.IsCancellationRequested, "A transient answer target must advertise when it closes.", ref checks);
        return JsonSerializer.Serialize(new { success = true, checks });
    }

    public static string InspectSpeechEnvironment()
    {
        var value = WindowsSpeechSupport.Inspect();
        return JsonSerializer.Serialize(new { available = value.Available, value.Message,
            recognizers = value.Recognizers.Select(x => new { x.Id, x.Label, culture = x.Culture.Name }).ToList(), microphoneOpened = false });
    }

    private static void Require(bool value, string message, ref int checks)
    {
        if (!value) throw new InvalidOperationException(message); checks++;
    }

    private sealed class DiagnosticTextEntry(string text, int selectionStart, int selectionLength, string id = "fixture-entry") :
        IDesktopTextEntry, IDesktopTextEntryActivitySink, IDesktopTextEntryLifetime, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        public string Id => id;
        public string Purpose => "Fixture text entry";
        public string Text { get; private set; } = text;
        public int SelectionStart { get; private set; } = selectionStart;
        public int SelectionLength { get; private set; } = selectionLength;
        public CancellationToken AvailabilityToken => lifetime.Token;
        public DesktopTextEntryActivity? Activity { get; private set; }
        public void ReportActivity(DesktopTextEntryActivity activity) => Activity = activity;
        public Task<DesktopOperationResult> InsertAsync(string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Fixture closed."));
            Text = Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, value);
            SelectionStart += value.Length; SelectionLength = 0;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public void Close() { if (!lifetime.IsCancellationRequested) lifetime.Cancel(); }
        public void Dispose() { Close(); lifetime.Dispose(); }
    }

    private sealed class DiagnosticPluginHost(DesktopConversationBinding binding, bool running, string draftText) : IDesktopPluginHost
    {
        public bool IsRunning { get; set; } = running;
        public string DraftText { get; set; } = draftText;
        public long DraftRevision { get; set; } = 4;
        public int Submissions { get; private set; }
        public int Steers { get; private set; }
        public int Stops { get; private set; }
        public DesktopSubmission? LastSubmission { get; private set; }
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public DesktopConversationSnapshot GetConversation() => new(binding, "Diagnostic conversation", null, null, IsRunning);
        public DesktopDraftSnapshot GetDraft() => new(binding, DraftRevision, DraftText);
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Not used by this fixture."));
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Submissions++; LastSubmission = submission;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted, "Submitted.", submission.RequestId, "job-one"));
        }
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Steers++; LastSubmission = submission;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted, "Steered.", submission.RequestId, "job-one"));
        }
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding requested, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requested != binding) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Wrong binding."));
            Stops++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted, "Stopped."));
        }
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) { }
        public void RefreshPresentation() { }
    }
}
