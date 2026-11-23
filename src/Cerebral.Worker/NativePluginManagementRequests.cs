using System.Text.Json;
using Cerebral.Core;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

/// <summary>Durable owner mediation for the Desktop-owned catalog. No package paths or code run here.</summary>
public sealed class NativePluginManagementRequests(StateStore store, EventHub events)
{
    private const string Kind = "native-plugin-management";
    public const string EventType = "desktop.plugins.management.updated";
    public const string Capability = "desktop.plugins.manage";
    public const string ListTool = "desktop.plugins.list";
    public const string SetTool = "desktop.plugins.set-enabled";
    public const string StatusTool = "desktop.plugins.status";

    public static string ToolFor(string operation) => operation switch
    {
        "list" => ListTool, "set-enabled" => SetTool,
        _ => throw new ArgumentException("Use list or set-enabled.")
    };

    public NativePluginManagementRequest Create(ToolExecutionContext context, string operation,
        string? query = null, string? catalogFingerprint = null, NativePluginSelection? selection = null, bool? enabled = null,
        string? catalogRequestId = null)
    {
        ValidateInput(operation, query, catalogFingerprint, selection, enabled);
        RequireId(context.EffectId);
        var created = false;
        var result = store.Locked(() =>
        {
            RequireContext(context, ToolFor(operation));
            if (operation == "set-enabled")
            {
                RequireId(catalogRequestId);
                var proof = Read(context, catalogRequestId!, SetTool);
                var row = proof.Result?.Catalog.Plugins.SingleOrDefault(x => x.Id == selection!.Id);
                if (proof.Operation != "list" || proof.Status != "completed" || row is null ||
                    row.Error is not null || row.Generation != selection!.Generation ||
                    row.PackageFingerprint != selection.PackageFingerprint || row.Enabled != selection.ExpectedEnabled ||
                    proof.Result!.Catalog.CatalogFingerprint != catalogFingerprint)
                    throw new InvalidOperationException("Use an exact valid installed plugin from this conversation's completed catalog receipt.");
            }
            else if (catalogRequestId is not null) throw new ArgumentException("Only a plugin change uses a catalog receipt.");
            if (store.Get<NativePluginManagementRequest>(Kind, context.EffectId!) is { } existing)
            {
                if (existing.SchemaVersion != 1 || existing.SessionId != context.SessionId || existing.ProfileId != context.Profile.Id ||
                    existing.ProfileRevision != context.Profile.Revision || existing.JobId != context.JobId ||
                    existing.Operation != operation || existing.Query != query || existing.CatalogFingerprint != catalogFingerprint ||
                    existing.CatalogRequestId != catalogRequestId ||
                    existing.Selection != selection || existing.Enabled != enabled)
                    throw new InvalidOperationException("This retained plugin-management effect has different input; no action was replayed.");
                return existing;
            }
            foreach (var expired in store.List<NativePluginManagementRequest>(Kind).Where(x => x.SessionId == context.SessionId &&
                x.Status is "pending" or "claimed" && x.ExpiresAt <= DateTimeOffset.UtcNow))
            {
                expired.Status = expired.Status == "pending" ? "rejected" : "uncertain";
                expired.Error = "The request expired. Inspect a claimed receipt; never replay it.";
                store.Put(Kind, expired.Id, expired);
            }
            if (store.List<NativePluginManagementRequest>(Kind).Count(x => x.SessionId == context.SessionId && x.Status is "pending" or "claimed") >= 8)
                throw new InvalidOperationException("This conversation has eight unfinished native plugin-management requests.");
            var request = new NativePluginManagementRequest
            {
                Id = context.EffectId!, SessionId = context.SessionId, ProfileId = context.Profile.Id,
                ProfileRevision = context.Profile.Revision, JobId = context.JobId, Operation = operation,
                Query = query, CatalogFingerprint = catalogFingerprint, CatalogRequestId = catalogRequestId,
                Selection = selection, Enabled = enabled,
                ExpiresAt = DateTimeOffset.UtcNow.Add(operation == "list" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10))
            };
            store.Put(Kind, request.Id, request); created = true; return request;
        });
        if (created) Publish(result);
        return result;
    }

    public NativePluginManagementRequest Read(ToolExecutionContext context, string id, string toolName)
        => store.Locked(() =>
        {
            RequireContext(context, toolName);
            var request = Get(id);
            if (request.SessionId != context.SessionId || request.ProfileId != context.Profile.Id ||
                request.ProfileRevision != context.Profile.Revision)
                throw new UnauthorizedAccessException("This plugin-management receipt is not owned by the current conversation and profile revision.");
            return request;
        });

    public NativePluginManagementClaim Claim(string id, string sessionId, string claimId,
        Action<NativePluginManagementRequest> validateExecution)
    {
        RequireId(claimId);
        var changed = false;
        var claim = store.Locked(() =>
        {
            var request = GetForOwner(id, sessionId);
            if (request.Status != "pending") return new NativePluginManagementClaim(false, request);
            try
            {
                RequireCurrent(request);
                validateExecution(request);
                request.Status = "claimed"; request.ClaimId = claimId;
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
            { request.Status = "rejected"; request.Error = Bound(error.Message); }
            store.Put(Kind, request.Id, request); changed = true;
            return new NativePluginManagementClaim(request.Status == "claimed", request);
        });
        if (changed) Publish(claim.Request);
        return claim;
    }

    public NativePluginManagementRequest Ready(string id, string sessionId, string claimId,
        Action<NativePluginManagementRequest> validateExecution) => store.Locked(() =>
    {
        var request = GetForOwner(id, sessionId);
        RequireId(claimId);
        if (request.Status != "claimed" || request.ClaimId != claimId)
            throw new UnauthorizedAccessException("The plugin-management owner claim is not current.");
        RequireCurrent(request); validateExecution(request); return request;
    });

    public NativePluginManagementRequest Reject(string id, string sessionId)
    {
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = GetForOwner(id, sessionId);
            if (request.Status == "rejected" && request.Error == "The owner declined this plugin change.") return request;
            if (request.Status != "pending") throw new InvalidOperationException("Only an unclaimed plugin change can be declined.");
            request.Status = "rejected"; request.Error = "The owner declined this plugin change.";
            store.Put(Kind, id, request); changed = true; return request;
        });
        if (changed) Publish(result);
        return result;
    }

    public NativePluginManagementRequest Complete(string id, string sessionId, string claimId,
        string status, NativePluginManagementResult? result, string? error)
    {
        RequireId(claimId);
        if (status is not ("completed" or "rejected" or "uncertain") ||
            status == "completed" && (result is null || !string.IsNullOrEmpty(error)) ||
            status != "completed" && string.IsNullOrWhiteSpace(error) ||
            error?.Length > 2000 || error?.Any(char.IsControl) == true)
            throw new ArgumentException("Report one bounded, consistent plugin-management outcome.");
        ValidateResult(result);
        var changed = false;
        var value = store.Locked(() =>
        {
            var request = GetForOwner(id, sessionId);
            if (request.ClaimId != claimId) throw new UnauthorizedAccessException("The exact owner claim does not match.");
            if (request.Status is not ("claimed" or "uncertain"))
            {
                if (request.Status == status && request.Error == error &&
                    JsonSerializer.Serialize(request.Result, CerebralJson.Options) == JsonSerializer.Serialize(result, CerebralJson.Options)) return request;
                throw new InvalidOperationException("The plugin-management request already has another outcome.");
            }
            if (status == "completed" && request.Operation == "set-enabled")
            {
                var selected = result!.Catalog.Plugins.SingleOrDefault(x => x.Id == request.Selection!.Id);
                if (selected is null || selected.Enabled != request.Enabled || selected.Loaded != request.Enabled ||
                    selected.Error is not null || selected.Generation != request.Selection!.Generation ||
                    selected.PackageFingerprint != request.Selection.PackageFingerprint)
                    throw new ArgumentException("Completion requires the exact selected generation and actual desired catalog/runtime state.");
            }
            if (status == "completed" && request.Operation == "list" && result!.ChangedPluginIds.Count != 0)
                throw new ArgumentException("A catalog read cannot report plugin mutations.");
            request.Status = status; request.Result = result; request.Error = error;
            store.Put(Kind, id, request); changed = true; return request;
        });
        if (changed) Publish(value);
        return value;
    }

    public NativePluginManagementRequest Interrupt(ToolExecutionContext context, string id, bool cancelled)
    {
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = Read(context, id, ToolFor(Get(id).Operation));
            if (request.Status is not ("pending" or "claimed")) return request;
            request.Error = request.Status == "claimed"
                ? "The Desktop claimed this effect but its outcome is unconfirmed. Inspect this receipt; do not repeat it."
                : cancelled ? "Cancelled before owner delivery." : "The Desktop did not claim this request before its deadline.";
            request.Status = request.Status == "claimed" ? "uncertain" : cancelled ? "cancelled" : "rejected";
            store.Put(Kind, id, request); changed = true; return request;
        });
        if (changed) Publish(result);
        return result;
    }

    public async Task<NativePluginManagementRequest> WaitAsync(ToolExecutionContext context, string id, CancellationToken token)
    {
        while (true)
        {
            var request = Read(context, id, ToolFor(Get(id).Operation));
            if (request.Status is not ("pending" or "claimed")) return request;
            if (token.IsCancellationRequested) return Interrupt(context, id, true);
            if (request.ExpiresAt <= DateTimeOffset.UtcNow) return Interrupt(context, id, false);
            try { await Task.Delay(150, token); }
            catch (OperationCanceledException) { return Interrupt(context, id, true); }
        }
    }

    public static void RequireExecuting(WorkerWorkflows host, ToolExecutionContext context, string toolName, bool requireEffect = true)
    {
        var run = host.Store.Get(context.JobId);
        if (run.Submission.ConversationId != context.SessionId || run.Submission.ProfileId != context.Profile.Id ||
            run.Submission.ParentRunId is not null || run.Submission.Caller?.Kind != EventActorKind.User)
            throw new UnauthorizedAccessException("Installed plugin management requires this owner's root conversation workflow.");
        WorkflowUiOrigin.RequireRoot(run.Submission);
        var accepted = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
        if (accepted.Profile.Id != context.Profile.Id || accepted.Profile.Revision != context.Profile.Revision ||
            !WorkflowUiOrigin.LocalAuthor(accepted.Profile) ||
            !WorkerExecutionSnapshot.AllowsTool(accepted.Profile, toolName) ||
            !(accepted.AllowedTools.Contains("*", StringComparer.Ordinal) || accepted.AllowedTools.Contains(toolName, StringComparer.Ordinal)) ||
            !accepted.Tools.Any(x => x.Name == toolName && x.Capability == Capability) ||
            !accepted.Capabilities.Contains(Capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This tool was not granted when the current root workflow was accepted.");
        if (requireEffect)
        {
            var effect = run.Effect;
            if (run.CancelRequested || run.Status != WorkflowStatus.Running || effect is null ||
                effect.Id != context.EffectId || effect.State != WorkflowEffectState.Dispatched ||
                effect.CommandId != WorkerWorkflowEffects.ToolCommand || effect.CommandVersion != 1 ||
                effect.Arguments.ValueKind != JsonValueKind.Object ||
                !effect.Arguments.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != toolName)
                throw new InvalidOperationException("The exact plugin-management effect is no longer executing.");
        }
    }

    public NativePluginManagementRequest GetForOwner(string id, string sessionId)
    {
        var request = Get(id);
        if (request.SessionId != sessionId) throw new UnauthorizedAccessException("This conversation does not own the plugin-management request.");
        return request;
    }

    private NativePluginManagementRequest Get(string id)
    {
        RequireId(id);
        var value = store.Get<NativePluginManagementRequest>(Kind, id) ?? throw new ArgumentException("Native plugin-management receipt not found.");
        if (value.SchemaVersion != 1) throw new InvalidOperationException("Unsupported native plugin-management receipt version.");
        ValidateInput(value.Operation, value.Query, value.CatalogFingerprint, value.Selection, value.Enabled);
        return value;
    }

    private void RequireCurrent(NativePluginManagementRequest request)
    {
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("The native plugin request expired before delivery.");
        var current = store.Get<AgentProfile>("agent", request.ProfileId) ?? throw new UnauthorizedAccessException("The profile is no longer configured.");
        RequireContext(new(current, request.SessionId, request.JobId), ToolFor(request.Operation));
        if (current.Revision != request.ProfileRevision) throw new UnauthorizedAccessException("The requesting profile revision changed.");
    }

    private void RequireContext(ToolExecutionContext context, string toolName)
    {
        if (toolName is not (ListTool or SetTool or StatusTool)) throw new ArgumentException("Unknown native plugin-management tool.");
        var current = store.Get<AgentProfile>("agent", context.Profile.Id);
        bool Allowed(AgentProfile profile) => WorkflowUiOrigin.LocalAuthor(profile) &&
            profile.Capabilities.Contains(Capability, StringComparer.Ordinal) && WorkerExecutionSnapshot.AllowsTool(profile, toolName);
        // Receipt reads and owner claim preflights also prove their own durable root binding.
        // A service-owned QA continuation cannot acquire installed-catalog management authority.
        _ = WorkflowUiOrigin.ResolveRead(store, context, toolName, Capability, rootOnly: true);
        if (!Allowed(context.Profile) || current is null || !Allowed(current) ||
            current.Revision != context.Profile.Revision)
            throw new UnauthorizedAccessException("Native plugin management requires the current local owner profile, exact revision and tool grant.");
    }

    public static void ValidateInput(string operation, string? query, string? fingerprint, NativePluginSelection? selection, bool? enabled)
    {
        _ = ToolFor(operation);
        if (operation == "list")
        {
            if (fingerprint is not null || selection is not null || enabled is not null ||
                query?.Length > 100 || query?.Any(char.IsControl) == true)
                throw new ArgumentException("Installed plugin list accepts only a bounded optional query.");
            return;
        }
        if (query is not null || selection is null || enabled is null || selection.Generation is null ||
            selection.Generation.Length is < 1 or > 128 || selection.Generation.Any(char.IsControl))
            throw new ArgumentException("Use the exact installed plugin selection and desired enabled state.");
        RequirePluginId(selection.Id);
        RequireHash(fingerprint); RequireHash(selection.PackageFingerprint);
    }

    public static void ValidateResult(NativePluginManagementResult? result)
    {
        if (result is null) return;
        if (result.Catalog is null || result.Catalog.Plugins is null || result.ChangedPluginIds is null ||
            result.Catalog.Plugins.Any(x => x is null))
            throw new ArgumentException("The installed plugin observation requires its catalog and nonnull collections.");
        RequireHash(result.Catalog.CatalogFingerprint);
        if (result.Catalog.Plugins.Count > 100 || result.Catalog.Omitted < 0 || result.ChangedPluginIds.Count > 100 ||
            result.Catalog.Plugins.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Catalog.Plugins.Count ||
            result.ChangedPluginIds.Distinct(StringComparer.Ordinal).Count() != result.ChangedPluginIds.Count ||
            JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > 64 * 1024)
            throw new ArgumentException("The installed plugin observation exceeds its bounded contract.");
        foreach (var row in result.Catalog.Plugins)
        {
            RequirePluginId(row.Id);
            if (row.Name is null || row.Version is null || row.Name.Length > 200 || row.Version.Length > 100 ||
                row.Generation?.Length > 128 || row.Error?.Length > 2000 ||
                new[] { row.Name, row.Version, row.Generation, row.Error }.Any(x => x?.Any(char.IsControl) == true))
                throw new ArgumentException("The installed plugin observation has invalid bounded metadata.");
            if (row.PackageFingerprint is not null) RequireHash(row.PackageFingerprint);
        }
        foreach (var id in result.ChangedPluginIds) RequirePluginId(id);
    }

    private static void RequirePluginId(string? id)
    {
        try { NativeDesktopPluginPackage.ValidateId(id!); }
        catch (InvalidDataException error) { throw new ArgumentException(error.Message, nameof(id)); }
    }

    private static void RequireId(string? id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid) || guid == Guid.Empty) throw new ArgumentException("Use the exact nonempty 32-character receipt identifier.");
    }
    private static void RequireHash(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("Use the exact lowercase SHA-256 fingerprint returned by installed plugin list.");
    }
    private static string Bound(string value) => new(value.Where(c => !char.IsControl(c)).Take(2000).ToArray());
    private void Publish(NativePluginManagementRequest request) => events.Publish(EventType, request.JobId, request);
}
