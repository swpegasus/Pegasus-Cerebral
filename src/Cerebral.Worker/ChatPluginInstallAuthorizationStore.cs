using System.IO;
using System.Text.Json;
using Cerebral.Core;
using static Cerebral.Worker.ChatPluginInstallPolicy;

namespace Cerebral.Worker;

/// <summary>Private owner CAS policy and per-new-presentation receipts in the existing StateStore.</summary>
internal sealed class ChatPluginInstallAuthorizationStore(StateStore store, EventHub events,
    Func<string, Scope> savedNativeScope)
{
    internal Snapshot Get(string id, string? actorProfileId)
    {
        RequireOwner(actorProfileId); ValidateId(id);
        return store.Locked(() => { var scope = savedNativeScope(id); return Project(scope, ReadPermission(scope)); });
    }

    internal Snapshot Save(string id, string? actorProfileId, long expectedRevision, bool allow)
    {
        RequireOwner(actorProfileId); ValidateId(id);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var saved = store.Locked(() =>
        {
            var scope = savedNativeScope(id);
            var prior = ReadPermission(scope);
            if ((prior?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("Chat plugin-install permission changed. Refresh before saving.");
            if (prior is not null && prior.AllowAutomaticInstall == allow) return (Value: Project(scope, prior), Changed: false);
            var next = new Permission(Schema, scope.ConversationId, scope.ProfileId, scope.ProjectId,
                checked(expectedRevision + 1), allow, DateTimeOffset.UtcNow, OwnerProvenance, Guid.NewGuid().ToString("N"));
            Validate(next, scope);
            store.Put(PermissionKind, id, next);
            return (Value: Project(scope, next), Changed: true);
        });
        if (saved.Changed) events.Publish("conversation.plugin-install.updated", null,
            new { conversationId = id, revision = saved.Value.Revision, automaticInstall = saved.Value.AllowAutomaticInstall });
        return saved.Value;
    }

    // Called only inside InterfaceRequests' new-record transaction, after ordinary origin/path/package checks.
    // There is deliberately no owner or model command that captures/migrates historical requests.
    internal bool CaptureNew(Request request, Origin origin)
    {
        return store.Locked(() =>
        {
            var current = ReadRequest(request.Id, request.Scope.ConversationId);
            if (current != request) throw new InvalidOperationException("The new presentation changed before permission capture.");
            if (ReadPresentation(request.Id) is not null) return false; // never replace an earlier decision
            Permission? permission;
            try { permission = ReadPermission(savedNativeScope(request.Scope.ConversationId)); }
            catch (Exception error) when (error is InvalidDataException or JsonException or NotSupportedException or KeyNotFoundException)
            {
                return false; // optional automatic permission is unavailable; the normal pending review remains
            }
            var binding = Capture(request, permission, origin, DateTimeOffset.UtcNow);
            if (binding is null) return false;
            store.Put(BindingKind, request.Id, binding);
            return true;
        });
    }

    internal ReadDecision Inspect(string requestId, string conversationId, string? actorProfileId,
        Func<Request, Presentation, Origin> resolveCurrentOrigin)
    {
        RequireOwner(actorProfileId);
        return store.Locked(() =>
        {
            var request = ReadRequest(requestId, conversationId);
            try
            {
                var permission = ReadPermission(savedNativeScope(conversationId));
                var binding = ReadPresentation(requestId);
                if (permission is null || binding is null)
                    return Manual(request, "manual-review-required");
                var origin = resolveCurrentOrigin(request, binding);
                Require(request, permission, binding, origin,
                    permission.Revision, permission.DecisionId, claimed: false, requireCompleted: false);
                if (origin.SourceStatus != "completed")
                    return new ReadDecision(Schema, requestId, conversationId, request.Scope.ProfileId, request.Scope.ProjectId,
                        false, permission.Revision, permission.DecisionId, request.Fingerprint, "source-run-not-completed");
                return new ReadDecision(Schema, requestId, conversationId, request.Scope.ProfileId, request.Scope.ProjectId, true, permission.Revision,
                    permission.DecisionId, request.Fingerprint, "owner-chat-permission");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException or JsonException or
                                                NotSupportedException or KeyNotFoundException)
            {
                return Manual(request, "permission-unavailable-denied-or-stale");
            }
        });
    }

    // Normal Claim runs inside the same gate/transaction as the final permission revision check.
    // A known denial returns the untouched pending request, allowing the normal journal to discard
    // its unstarted reservation. A denied permission must not consume a manual review.
    internal InterfaceClaimResult Claim(string requestId, string conversationId, string claimId,
        long expectedRevision, string decisionId, string? actorProfileId,
        Func<Request, Presentation, Origin> resolveCurrentOrigin, Func<string, string, InterfaceClaimResult> normalClaim)
    {
        RequireOwner(actorProfileId);
        return store.Locked(() =>
        {
            var request = ReadRequest(requestId, conversationId);
            try { Recheck(request, expectedRevision, decisionId, claimed: false, resolveCurrentOrigin); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException or JsonException or
                                                NotSupportedException or KeyNotFoundException)
            {
                return new InterfaceClaimResult(false, store.Get<InterfaceRequest>("interface", requestId)!);
            }
            return normalClaim(requestId, claimId); // retains normal current grants, immutable fingerprint and claim checks
        });
    }

    internal void BeforeEffect(string requestId, string conversationId, string claimId,
        long expectedRevision, string decisionId, string? actorProfileId,
        Func<Request, Presentation, Origin> resolveCurrentOrigin)
    {
        RequireOwner(actorProfileId);
        store.Locked(() =>
        {
            var request = ReadRequest(requestId, conversationId);
            if (request.ClaimId != claimId) throw new UnauthorizedAccessException("The exact automatic-install claim changed.");
            Recheck(request, expectedRevision, decisionId, claimed: true, resolveCurrentOrigin);
            return true;
        });
    }

    private void Recheck(Request request, long revision, string decisionId, bool claimed,
        Func<Request, Presentation, Origin> resolveCurrentOrigin)
    {
        var permission = ReadPermission(savedNativeScope(request.Scope.ConversationId));
        var presentation = ReadPresentation(request.Id) ??
            throw new UnauthorizedAccessException("This presentation has no new-request install authorization.");
        Require(request, permission, presentation, resolveCurrentOrigin(request, presentation), revision, decisionId, claimed);
    }

    private Permission? ReadPermission(Scope scope)
    {
        var value = store.Get<JsonElement>(PermissionKind, scope.ConversationId);
        if (value.ValueKind == JsonValueKind.Undefined) return null;
        if (value.GetRawText().Length > 8192) throw new InvalidDataException("Stored chat install permission exceeds its bound.");
        Fields(value, "schemaVersion", "conversationId", "profileId", "projectId", "revision",
            "allowAutomaticInstall", "updatedAt", "provenance", "decisionId");
        var result = value.Deserialize<Permission>(CerebralJson.Options) ??
            throw new InvalidDataException("Stored chat install permission is empty.");
        Validate(result, scope);
        return result;
    }

    private Presentation? ReadPresentation(string requestId)
    {
        var value = store.Get<JsonElement>(BindingKind, requestId);
        if (value.ValueKind == JsonValueKind.Undefined) return null;
        if (value.GetRawText().Length > 65536) throw new InvalidDataException("Stored install authorization exceeds its bound.");
        Fields(value, "schemaVersion", "requestId", "conversationId", "profileId", "projectId", "sourceRunId",
            "manifestPath", "packageFingerprint", "requestCreatedAt", "permissionRevision", "ownerDecisionId",
            "acceptedSnapshotSha256", "effectId");
        return value.Deserialize<Presentation>(CerebralJson.Options) ??
            throw new InvalidDataException("Stored install authorization is empty.");
    }

    private Request ReadRequest(string requestId, string conversationId)
    {
        ValidateId(requestId); ValidateId(conversationId);
        var request = store.Get<InterfaceRequest>("interface", requestId) ??
            throw new KeyNotFoundException("The exact native plugin presentation was not found.");
        if (request.Id != requestId || request.SessionId != conversationId || request.Kind != "plugin" ||
            request.Source != "workflow" || request.PackageFingerprint is null)
            throw new UnauthorizedAccessException("The presentation does not belong to this native chat.");
        var scope = savedNativeScope(conversationId);
        if (scope.ProfileId != request.ProfileId) throw new UnauthorizedAccessException("The presenting profile changed.");
        return new(request.Id, scope, request.JobId, request.Path, request.PackageFingerprint,
            request.CreatedAt, request.Status, request.ClaimId);
    }

    private static ReadDecision Manual(Request request, string reason) =>
        new(Schema, request.Id, request.Scope.ConversationId, request.Scope.ProfileId, request.Scope.ProjectId, false, null, null, null, reason);
}
