using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Contracts.Models;

/// <summary>Owner-only, single-use text generation. Policy and public input are the complete model
/// context; profile selection supplies an exact existing local model and bounded capacity only.</summary>
public static class IsolatedTextGenerationContract
{
    public const string Id = "cerebral.isolated-text/1";
    public const string CapabilityOperation = "inference.isolated.capabilities";
    public const string GenerateOperation = "inference.isolated.generate";
    public const string CancelOperation = "inference.isolated.cancel";
    public const string DefinitionId = "cerebral.host:isolated-text";
    public const string DefinitionVersion = "1.0.0";
    public const string EffectId = "cerebral.host:isolated-text.generate";
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumOutputCharacters = 2048;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IsolatedTextGenerationRequest(int SchemaVersion, string RequestId, string ProfileId,
    long ProfileRevision, string Purpose, string Policy, string Text, int MaximumOutputTokens, long ExpiresAtUnixMilliseconds)
{
    public void Validate(DateTimeOffset now, bool retained = false)
    {
        if (SchemaVersion != 1 || !Guid.TryParseExact(RequestId, "N", out _) || ProfileRevision < 1 ||
            !Identifier(ProfileId, 128) || !Identifier(Purpose, 100) ||
            string.IsNullOrWhiteSpace(Policy) || Policy.Length > 8192 || Policy.Contains('\0') ||
            string.IsNullOrWhiteSpace(Text) || Text.Length > 64000 || Text.Contains('\0') ||
            MaximumOutputTokens is < 1 or > 256 || ExpiresAtUnixMilliseconds < 1 ||
            !retained && (ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds() ||
                ExpiresAtUnixMilliseconds > now.AddSeconds(35).ToUnixTimeMilliseconds()) ||
            JsonSerializer.SerializeToUtf8Bytes(this).Length > IsolatedTextGenerationContract.MaximumRequestBytes)
            throw new ArgumentException("Isolated generation requires bounded text, policy, exact profile revision and a fresh short deadline.");
    }
    private static bool Identifier(string? value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

/// <summary>Injected by the owner admission path, absent from generic workflow transport input.</summary>
public sealed record WorkflowIsolatedTextAdmission(int SchemaVersion, long ProfileRevision);

public sealed record IsolatedTextGenerationResult(string Contract, string RequestId, string State,
    string? Text = null, string? Message = null);
