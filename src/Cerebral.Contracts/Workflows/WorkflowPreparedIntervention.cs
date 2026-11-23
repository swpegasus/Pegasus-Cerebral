using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Contracts.Workflows;

/// <summary>Only a versioned definition that understands the rejection signal may resume planning.</summary>
public interface IWorkflowPreparedRejectionSupport
{
    bool CanRejectPrepared(WorkflowRun snapshot);
}

public sealed record WorkflowPreparedRejectRequest(string RequestId, string RunId, long ExpectedRevision,
    string EffectId, string EffectSha256, string Reason, string SteeringText);

public sealed record WorkflowPreparedPermitRequest(string RequestId, string RunId, long ExpectedRevision,
    string EffectId, string EffectSha256, string Reason);

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowPreparedInterventionKind>))]
public enum WorkflowPreparedInterventionKind { Reject, PermitOnce }

/// <summary>Durable request receipt and exact-effect permit. Consumed is set in the dispatch transaction.</summary>
public sealed record WorkflowPreparedInterventionRecord(string RequestId, WorkflowPreparedInterventionKind Kind,
    string RequestSha256, string EffectId, string EffectSha256, long AppliedRevision,
    string? BudgetKey = null, bool Consumed = false);

/// <summary>Engine-owned planning signal; it contains no command result or provider receipt.</summary>
public sealed record WorkflowPreparedRejectionSignal(string EffectId, string Reason, string SteeringRequestId);

public static class WorkflowPreparedEffectIdentity
{
    // Match the existing owner effect-coordinate encoding without depending on the Core assembly.
    private static readonly JsonSerializerOptions IdentityJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Sha256(WorkflowEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.SerializeToElement(effect, IdentityJson).GetRawText()))).ToLowerInvariant();
    }

    public static string RequestSha256<T>(T request) where T : notnull =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.SerializeToElement(request, IdentityJson).GetRawText()))).ToLowerInvariant();
}
