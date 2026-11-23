using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Integrator;

internal enum ServicePackageStageOutcome { NotSubmitted, Confirmed, Unknown }

/// <summary>A single reviewed stage attempt; the caller supplies a captured owner dispatch and connection guard.</summary>
internal sealed class ServicePackageStageOperation
{
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch;
    private readonly Action requireConnection;
    private readonly string id;
    private readonly string manifestPath;
    private readonly long revision;
    private int started;

    public ServicePackageStageOutcome Outcome { get; private set; }
    public bool WasDispatched { get; private set; }

    public ServicePackageStageOperation(string id, string manifestPath, long revision,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch, Action requireConnection)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id is "." or ".." ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Choose one valid reviewed package ID.", nameof(id));
        if (string.IsNullOrWhiteSpace(manifestPath) || manifestPath.Length > 4096 || manifestPath.Contains('\0'))
            throw new ArgumentException("Choose the exact manifest path on the selected Worker.", nameof(manifestPath));
        if (revision < 0 || revision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(revision));
        this.id = id; this.manifestPath = manifestPath; this.revision = revision;
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.requireConnection = requireConnection ?? throw new ArgumentNullException(nameof(requireConnection));
    }

    internal static bool SupportsReviewedIdentity(JsonElement descriptor)
    {
        if (descriptor.ValueKind != JsonValueKind.Object ||
            !descriptor.TryGetProperty("id", out var operation) || operation.ValueKind != JsonValueKind.String ||
            operation.GetString() != "service-packages.stage" ||
            !descriptor.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !descriptor.TryGetProperty("ownerOnly", out var owner) || owner.ValueKind != JsonValueKind.True ||
            !descriptor.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty("expectedId", out var identity) || identity.ValueKind != JsonValueKind.Object ||
            !identity.TryGetProperty("type", out var type))
            return false;
        return type.ValueKind == JsonValueKind.String && type.GetString() == "string" ||
            type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && item.GetString() == "string");
    }

    public async Task<CommandResult> StageOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            throw new InvalidOperationException("This stage attempt was already used. Inspect the current state before a new owner review.");
        requireConnection();
        cancellationToken.ThrowIfCancellationRequested();
        var description = await dispatch(new("sdk.describe",
            CerebralJson.Element(new { id = "service-packages.stage" })), cancellationToken);
        requireConnection();
        if (!description.Success || !SupportsReviewedIdentity(description.Data))
            throw new InvalidOperationException("The selected Worker does not advertise reviewed-identity staging. Update it before using this action.");
        cancellationToken.ThrowIfCancellationRequested();
        requireConnection();
        // A lost reply may follow a committed stage. Keep the attempt consumed; never send the mutation twice.
        WasDispatched = true; Outcome = ServicePackageStageOutcome.Unknown;
        var result = await dispatch(new("service-packages.stage",
            CerebralJson.Element(new { manifestPath, expectedRevision = revision, expectedId = id })), cancellationToken);
        requireConnection();
        // A generic failure carries no no-mutation proof: status calculation can fail after a committed stage.
        if (!result.Success) return result;
        var data = result.Data;
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("id", out var returnedId) || returnedId.ValueKind != JsonValueKind.String || returnedId.GetString() != id ||
            !data.TryGetProperty("revision", out var returnedRevision) || !returnedRevision.TryGetInt64(out var next) || next != revision + 1 ||
            !data.TryGetProperty("stagedGeneration", out var generation) || generation.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(generation.GetString()) || generation.GetString()!.Length > 160)
            throw new InvalidDataException("The Worker returned an unconfirmed staging result. Inspect the current package state; do not retry this attempt.");
        Outcome = ServicePackageStageOutcome.Confirmed;
        return result;
    }

    /// <summary>Reads present state only. It cannot attribute a lost mutation reply or change its Unknown outcome.</summary>
    public async Task<CommandResult> InspectCurrentAsync(CancellationToken cancellationToken)
    {
        requireConnection();
        var result = await dispatch(new("service-packages.inspect", CerebralJson.Element(new { id })), cancellationToken);
        requireConnection();
        return result;
    }
}
