namespace Cerebral.Speech;

/// <summary>Provider contract v1: a bounded recording becomes text without entering an agent conversation.</summary>
public interface ISpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }
    SpeechProviderAvailability Inspect(SpeechModelBinding model);
    Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken cancellationToken);
}

public sealed record SpeechProviderDescriptor(string Id, string Version, string DisplayName, bool SupportsPartialResults);
public sealed record SpeechProviderAvailability(bool Available, string? Reason);

/// <summary>The capture owner retains this file until completion. V1 accepts mono, 16 kHz, PCM16 RIFF/WAV only.</summary>
public sealed record SpeechAudioInput(string WavPath);
public sealed record SpeechModelBinding(string ModelId, string ModelRevision, string ModelPath);
public sealed record SpeechTranscript(string Text, double AudioSeconds, long ElapsedMilliseconds, string ProviderId, string ModelId);

/// <summary>Diagnostics are bounded and remain outside the model prompt and transcript.</summary>
public sealed class SpeechProviderException(string code, string message, string? diagnostics = null, int? exitCode = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string? Diagnostics { get; } = diagnostics;
    public int? ExitCode { get; } = exitCode;
}
