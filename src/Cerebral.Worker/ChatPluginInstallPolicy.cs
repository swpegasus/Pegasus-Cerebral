using System.IO;
using Cerebral.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Worker;

// Private owner protocol and policy. No public SDK type or agent tool grants installation.
internal static class ChatPluginInstallPolicy
{
    internal const int Schema = 1;
    internal const string OwnerProvenance = "installation-owner/chat-properties";
    internal const string DefaultProvenance = "default-off";
    internal const string PermissionKind = "conversation-plugin-install";
    internal const string BindingKind = "interface-chat-plugin-install";

    internal sealed record Scope(string ConversationId, string ProfileId, string? ProjectId);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Permission(int SchemaVersion, string ConversationId, string ProfileId,
        string? ProjectId, long Revision, bool AllowAutomaticInstall, DateTimeOffset UpdatedAt,
        string Provenance, string DecisionId);
    internal sealed record Snapshot(int SchemaVersion, string Id, string ProfileId, string? ProjectId,
        long Revision, bool AllowAutomaticInstall, DateTimeOffset? UpdatedAt, string Provenance, string? DecisionId);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Presentation(int SchemaVersion, string RequestId, string ConversationId,
        string ProfileId, string? ProjectId, string SourceRunId, string ManifestPath, string PackageFingerprint,
        DateTimeOffset RequestCreatedAt, long PermissionRevision, string OwnerDecisionId,
        string AcceptedSnapshotSha256, string EffectId);
    // Created only by the private runtime adapter after durable root-origin/profile/path validation.
    // This object is never deserialized from command or model input.
    internal sealed record Origin(Scope Scope, string RunId, string SnapshotSha256, string EffectId,
        bool RootUser, bool Eligible, bool CurrentGrantsValid, string SourceStatus);
    internal sealed record Request(string Id, Scope Scope, string RunId, string ManifestPath,
        string Fingerprint, DateTimeOffset CreatedAt, string Status, string? ClaimId);
    internal sealed record ReadDecision(int SchemaVersion, string RequestId, string ConversationId,
        string ProfileId, string? ProjectId, bool AutomaticInstallAllowed, long? PermissionRevision, string? OwnerDecisionId,
        string? PackageFingerprint, string Reason);

    internal static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new InvalidDataException("A bounded exact identity is required.");
    }

    internal static void ValidateScope(Scope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateId(scope.ConversationId); ValidateId(scope.ProfileId);
        if (scope.ProjectId is not null) ValidateId(scope.ProjectId);
    }

    internal static void Validate(Permission value, Scope scope)
    {
        ValidateScope(scope);
        if (value.SchemaVersion != Schema || value.ConversationId != scope.ConversationId ||
            value.ProfileId != scope.ProfileId || value.ProjectId != scope.ProjectId ||
            value.Revision < 1 || value.UpdatedAt == default || value.Provenance != OwnerProvenance ||
            !Guid.TryParseExact(value.DecisionId, "N", out var decision) || decision == Guid.Empty)
            throw new InvalidDataException("Stored native-plugin permission has invalid identity, revision or provenance.");
    }

    internal static Snapshot Project(Scope scope, Permission? permission)
    {
        ValidateScope(scope);
        if (permission is not null) Validate(permission, scope);
        return new(Schema, scope.ConversationId, scope.ProfileId, scope.ProjectId, permission?.Revision ?? 0,
            permission?.AllowAutomaticInstall ?? false, permission?.UpdatedAt,
            permission?.Provenance ?? DefaultProvenance, permission?.DecisionId);
    }

    internal static void ValidateRequest(Request request)
    {
        ValidateId(request.Id); ValidateId(request.RunId); ValidateScope(request.Scope);
        if (!Path.IsPathFullyQualified(request.ManifestPath) || request.ManifestPath.Contains('\0') ||
            !Sha256(request.Fingerprint) || request.CreatedAt == default)
            throw new InvalidDataException("An exact native package presentation identity is required.");
    }

    internal static Presentation? Capture(Request request, Permission? permission, Origin origin, DateTimeOffset now)
    {
        ValidateRequest(request);
        if (permission is null) return null;
        Validate(permission, request.Scope);
        if (!permission.AllowAutomaticInstall) return null;
        if (request.Status != "pending" || request.ClaimId is not null || request.CreatedAt < permission.UpdatedAt ||
            now < request.CreatedAt || now - request.CreatedAt > TimeSpan.FromSeconds(5))
            return null; // Capture is only the new-record transaction, never an old pending-card migration.
        RequireOrigin(request, origin, requireCompleted: false);
        if (origin.SourceStatus != "running") return null;
        return new(Schema, request.Id, request.Scope.ConversationId, request.Scope.ProfileId, request.Scope.ProjectId,
            request.RunId, request.ManifestPath, request.Fingerprint, request.CreatedAt,
            permission.Revision, permission.DecisionId, origin.SnapshotSha256, origin.EffectId);
    }

    internal static void Require(Request request, Permission? permission, Presentation? presentation,
        Origin origin, long expectedPermissionRevision, string expectedDecisionId, bool claimed, bool requireCompleted = true)
    {
        ValidateRequest(request);
        if (permission is null || presentation is null) throw new UnauthorizedAccessException("No captured chat install authorization; use manual review.");
        Validate(permission, request.Scope);
        RequireOrigin(request, origin, requireCompleted);
        if (!permission.AllowAutomaticInstall || permission.Revision != expectedPermissionRevision ||
            permission.DecisionId != expectedDecisionId ||
            presentation.SchemaVersion != Schema || presentation.RequestId != request.Id ||
            presentation.ConversationId != request.Scope.ConversationId || presentation.ProfileId != request.Scope.ProfileId ||
            presentation.ProjectId != request.Scope.ProjectId || presentation.SourceRunId != request.RunId ||
            !Paths.Equals(presentation.ManifestPath, request.ManifestPath) || presentation.PackageFingerprint != request.Fingerprint ||
            presentation.RequestCreatedAt != request.CreatedAt || presentation.PermissionRevision != permission.Revision ||
            presentation.OwnerDecisionId != permission.DecisionId || presentation.AcceptedSnapshotSha256 != origin.SnapshotSha256 ||
            presentation.EffectId != origin.EffectId ||
            (claimed ? request.Status != "claimed" || string.IsNullOrWhiteSpace(request.ClaimId)
                     : request.Status != "pending" || request.ClaimId is not null))
            throw new UnauthorizedAccessException("Chat installation permission or its exact presentation binding changed; do not automatically install or replay.");
    }

    private static void RequireOrigin(Request request, Origin origin, bool requireCompleted)
    {
        if (!origin.RootUser || !origin.Eligible || !origin.CurrentGrantsValid ||
            origin.Scope != request.Scope || origin.RunId != request.RunId ||
            !Sha256(origin.SnapshotSha256) || string.IsNullOrWhiteSpace(origin.EffectId) ||
            requireCompleted && origin.SourceStatus != "completed")
            throw new UnauthorizedAccessException("Automatic installation requires this eligible root user run and current accepted profile/tool/path grants.");
    }

    internal static bool Sha256(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static void RequireOwnerProfile(AgentProfile? actor)
    {
        if (actor is not null) RequireOwner(actor.Id ?? string.Empty);
    }

    internal static void RequireOwner(string? actorProfileId)
    {
        if (actorProfileId is not null) throw new UnauthorizedAccessException("Chat plugin-install permission requires the installation owner, including for administration agents.");
    }

    internal static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An object is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !fields.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Duplicate or unsupported chat installation permission field.");
        if (seen.Count != fields.Length) throw new InvalidDataException("Every exact chat installation permission field is required.");
    }
}
