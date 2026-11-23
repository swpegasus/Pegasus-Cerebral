using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed partial class InterfaceRequests(StateStore store, EventHub events, Func<bool>? fullAccess = null, Func<int>? executionAttemptLimit = null)
{
    private const string RecordKind = "interface";
    internal const string ReviewedReference = PluginControlReferences.ReviewedReference;

    public InterfaceRequest Create(AgentJob job, AgentProfile effective, string kind, string path)
    {
        if (job.ParentJobId is not null || effective.Id != job.ProfileId) throw new UnauthorizedAccessException("Only the lead can present work in its user conversation.");
        var canonical = InterfacePathPolicy.Validate(effective, kind, path, fullAccess?.Invoke() == true);
        var created = false;
        var request = store.Locked(() =>
        {
            var current = store.Get<AgentProfile>("agent", job.ProfileId) ?? throw new UnauthorizedAccessException("The presenting profile is no longer configured.");
            var validated = InterfacePathPolicy.ValidatePresentation(current, kind, canonical, fullAccess?.Invoke() == true);
            var session = store.GetSessionMetadata(job.SessionId) ?? throw new ArgumentException("Session not found.");
            if (session.ProfileId != job.ProfileId) throw new UnauthorizedAccessException("The presenting job does not own this conversation.");
            var packageFingerprint = Fingerprint(kind, canonical);
            var existing = store.List<InterfaceRequest>(RecordKind);
            var matchingAttempts = existing.Count(x => x.JobId == job.Id && x.Kind == kind && x.Path.Equals(canonical, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (kind == "application" && matchingAttempts >= Math.Clamp(executionAttemptLimit?.Invoke() ?? 2, 1, 20))
                throw new InvalidOperationException("Cerebral stopped this assignment at the configured application launch-attempt limit. Review the recorded failures before starting another assignment or raise the limit in Configuration.");
            // A model repeating the same request in one job must not launch the application twice.
            var same = existing.FirstOrDefault(x => x.JobId == job.Id && x.Kind == kind && x.Path.Equals(canonical, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                (kind != "plugin" || x.PackageFingerprint == packageFingerprint));
            if (same is not null && same.ProcessStatus != "failed")
            {
                ValidateRetainedSource(same, validated);
                if (same.Status == "pending")
                    store.Append(job.SessionId, "user", $"Existing interface request {same.Id} is still pending the user interface. Do not repeat or poll the presentation action. Finish by accurately reporting that the request is queued, unless other requested work remains.", origin: "service");
                return same;
            }
            if (existing.Count(x => x.SessionId == job.SessionId && x.Status is "pending" or "claimed") >= 32)
                throw new InvalidOperationException("This conversation already has 32 unfinished interface requests.");
            var value = new InterfaceRequest { SessionId = job.SessionId, JobId = job.Id, ProfileId = job.ProfileId, Kind = kind, Path = canonical, PackageFingerprint = packageFingerprint, GeneratedSourceRoot = validated.GeneratedSourceRoot };
            store.Put(RecordKind, value.Id, value);
            store.Append(job.SessionId, "user", $"Interface request {value.Id}; kind={value.Kind}; path={Limit(value.Path)}; status=pending. Queued for the user interface. This is not proof that it opened. Do not submit or poll this request again. Finish by reporting it is queued, unless other requested work remains. The interface will report its outcome.", origin: "service");
            created = true; return value;
        });
        if (created) events.Publish("interface.updated", request.JobId, request);
        return request;
    }

    public InterfaceRequest CreateForWorkflow(ToolExecutionContext context, string kind, string path, string? expectedPackageFingerprint = null)
    {
        var created = false;
        var request = store.Locked(() =>
        {
            var scoped = WorkflowUiOrigin.ResolvePresentation(store, context, kind, path,
                expectedPackageFingerprint, fullAccess?.Invoke() == true, out var verifiedWorkFingerprint);
            var validated = ValidateWorkflowPresentation(scoped, kind, path);
            var canonical = validated.Path;
            var packageFingerprint = Fingerprint(kind, canonical);
            if (verifiedWorkFingerprint is not null && verifiedWorkFingerprint != packageFingerprint)
                throw new InvalidOperationException("The Work plugin package changed after verification. No review was queued.");
            if (expectedPackageFingerprint is not null)
            {
                if (kind != "plugin" || expectedPackageFingerprint.Length != 64 ||
                    expectedPackageFingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new ArgumentException("expectedPackageFingerprint requires a plugin's exact 64-character lowercase package fingerprint.");
                if (expectedPackageFingerprint != packageFingerprint)
                    throw new InvalidOperationException("The plugin package changed after verification. Rebuild if necessary, run plugin.verify again, and present its exact packageFingerprint; no review was queued.");
            }
            var existing = store.List<InterfaceRequest>(RecordKind);
            var matchingAttempts = existing.Count(x => x.JobId == context.JobId && x.Kind == kind &&
                x.Path.Equals(canonical, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (kind == "application" && matchingAttempts >= Math.Clamp(executionAttemptLimit?.Invoke() ?? 2, 1, 20))
                throw new InvalidOperationException("Cerebral stopped this assignment at the configured application launch-attempt limit. Review the recorded failures before starting another assignment or raise the limit in Configuration.");
            var same = existing.FirstOrDefault(x => x.JobId == context.JobId && x.Kind == kind &&
                x.Path.Equals(canonical, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                (kind != "plugin" || x.PackageFingerprint == packageFingerprint));
            if (same is not null && same.ProcessStatus != "failed")
            {
                ValidateRetainedSource(same, validated);
                return same;
            }
            if (existing.Count(x => x.SessionId == context.SessionId && x.Status is "pending" or "claimed") >= 32)
                throw new InvalidOperationException("This conversation already has 32 unfinished interface requests.");
            var value = new InterfaceRequest
            {
                SessionId = context.SessionId, JobId = context.JobId, ProfileId = context.Profile.Id,
                Source = "workflow", Kind = kind, Path = canonical, PackageFingerprint = packageFingerprint,
                GeneratedSourceRoot = validated.GeneratedSourceRoot
            };
            store.Put(RecordKind, value.Id, value);
            // Only this new-record branch may capture owner authorization; recovered/old requests never acquire it.
            if (verifiedWorkFingerprint is null)
                ChatPluginInstallRuntime.ForExisting(store, events, () => fullAccess?.Invoke() == true)?
                    .CaptureNew(value, context, expectedPackageFingerprint);
            created = true; return value;
        });
        if (created) events.Publish("interface.updated", request.JobId, request);
        return request;
    }

    public List<InterfaceRequest> List(string sessionId, int limit = 50)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("List between 1 and 100 interface requests.");
        return store.List<InterfaceRequest>(RecordKind).Where(x => x.SessionId == sessionId).OrderByDescending(x => x.CreatedAt).Take(limit).ToList();
    }

    public InterfaceRequest RetryPlugin(string id)
    {
        var created = false;
        var result = store.Locked(() =>
        {
            var prior = Get(id);
            if (prior.Kind != "plugin" || prior.Status != "failed")
                throw new InvalidOperationException("Only a failed native plugin review can be retried explicitly.");
            var profile = store.Get<AgentProfile>("agent", prior.ProfileId) ??
                throw new UnauthorizedAccessException("The presenting profile was removed.");
            // Workflow conversations intentionally have no legacy TaskSession row. The retained request owns
            // the durable conversation coordinate; current profile and path policy are the retry boundary.
            var validated = ValidatePresentationSource(prior, profile, out var verifiedWork);
            ValidateRetainedSource(prior, validated);
            var packageFingerprint = Fingerprint(prior.Kind, prior.Path);
            if (verifiedWork && (prior.PackageFingerprint is null || prior.PackageFingerprint != packageFingerprint))
                throw new InvalidOperationException("The Work plugin package changed after verification. Run Work again to verify and present the new package.");
            var existing = store.List<InterfaceRequest>(RecordKind).FirstOrDefault(item => item.Id != prior.Id &&
                item.SessionId == prior.SessionId && item.JobId == prior.JobId && item.Kind == prior.Kind &&
                item.Path.Equals(prior.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                item.PackageFingerprint == packageFingerprint && item.Status is "pending" or "claimed");
            if (existing is not null)
            {
                ValidateRetainedSource(existing, validated);
                return existing;
            }
            if (store.List<InterfaceRequest>(RecordKind).Count(item => item.SessionId == prior.SessionId && item.Status is "pending" or "claimed") >= 32)
                throw new InvalidOperationException("This conversation already has 32 unfinished interface requests.");
            var retry = new InterfaceRequest
            {
                SessionId = prior.SessionId,
                JobId = prior.JobId,
                ProfileId = prior.ProfileId,
                Source = prior.Source,
                Kind = prior.Kind,
                Path = prior.Path,
                PackageFingerprint = packageFingerprint,
                GeneratedSourceRoot = validated.GeneratedSourceRoot
            };
            store.Put(RecordKind, retry.Id, retry);
            created = true;
            return retry;
        });
        if (created) Publish(result);
        return result;
    }

    public InterfaceClaimResult Claim(string id, string claimId)
    {
        ValidateClaimId(claimId);
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = Get(id);
            // Claimed means a user interface may already have acted. Never automatically replay it.
            if (request.Status != "pending") return new InterfaceClaimResult(false, request);
            try
            {
                var profile = store.Get<AgentProfile>("agent", request.ProfileId) ?? throw new UnauthorizedAccessException("The presenting profile was removed.");
                if (request.Source != "workflow")
                {
                    var job = store.Get<AgentJob>("job", request.JobId) ?? throw new InvalidOperationException("The presenting job is no longer available.");
                    if (job.Status == "cancelled" || job.CancelRequested) throw new InvalidOperationException("The presenting assignment was cancelled.");
                }
                var validated = ValidatePresentationSource(request, profile, out _);
                ValidateRetainedSource(request, validated);
                if (request.Kind == "plugin" && (request.PackageFingerprint is null ||
                    request.PackageFingerprint != Fingerprint(request.Kind, request.Path)))
                    throw new InvalidOperationException("This plugin review is stale or predates package fingerprinting. Verify the current built package and queue a new owner review; no installation was started.");
                request.Status = "claimed"; request.ClaimId = claimId; request.ClaimedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                request.Status = "failed"; request.Error = Limit(ex.Message); request.CompletedAt = DateTimeOffset.UtcNow;
                if (request.Source != "workflow") AppendReceipt(request);
            }
            store.Put(RecordKind, id, request); changed = true;
            return new InterfaceClaimResult(request.Status == "claimed", request);
        });
        if (changed) Publish(result.Request);
        return result;
    }

    public InterfaceRequest Complete(string id, string claimId, bool success, string? error = null, string? message = null,
        InterfaceResourceReceipt? resource = null, InterfacePluginReceipt? plugin = null,
        bool controlTestsAllowed = false, InterfaceFailureStage? failureStage = null)
    {
        ValidateClaimId(claimId);
        if (failureStage is { } stage && !Enum.IsDefined(stage))
            throw new ArgumentException("The native plugin failure stage is unsupported.", nameof(failureStage));
        if (error?.Length > 2000 || message?.Length > 2000 || error?.Contains('\0') == true || message?.Contains('\0') == true)
            throw new ArgumentException("Interface outcome text must be at most 2,000 characters without NUL.");
        if (!success && string.IsNullOrWhiteSpace(error)) throw new ArgumentException("A failed interface action needs its actual error.");
        if (success && !string.IsNullOrEmpty(error)) throw new ArgumentException("A completed interface action cannot also contain an error.");
        if (success && failureStage is not null) throw new ArgumentException("A completed interface action cannot report a failure stage.");
        if (!success && resource is not null) throw new ArgumentException("A failed interface action cannot publish a resource receipt.");
        if (!success && plugin is not null) throw new ArgumentException("A failed interface action cannot publish an installed plugin receipt.");
        ValidateResource(resource);
        var changed = false;
        var value = store.Locked(() =>
        {
            var request = Get(id);
            if (failureStage is not null && request.Kind != "plugin")
                throw new ArgumentException("Only a failed native plugin review can report a failure stage.");
            if (controlTestsAllowed && request.Kind != "plugin")
                throw new ArgumentException("Only a native plugin review can authorize automatic plugin test or repair work.");
            if (request.ClaimId != claimId) throw new UnauthorizedAccessException("The interface claim does not match.");
            var status = success ? "completed" : "failed";
            if (request.Status is "completed" or "failed")
            {
                if (request.Status == status && request.Error == error && request.FailureStage == failureStage &&
                    request.Result == message && request.Resource == resource &&
                    InterfacePluginReceiptPolicy.Equivalent(request.Plugin, plugin))
                {
                    if (!success && controlTestsAllowed && request.PluginTests is null &&
                        IsSourceRepairFailureStage(failureStage))
                    {
                        request.PluginTests = CreatePluginRepairContinuation(request, true);
                        store.Put(RecordKind, id, request); changed = true;
                    }
                    return request;
                }
                throw new InvalidOperationException("This interface request already has a different reported outcome.");
            }
            if (request.Status != "claimed") throw new InvalidOperationException("Claim the pending interface request before reporting its outcome.");
            var installedPlugin = plugin is null ? null : InterfacePluginReceiptPolicy.Validate(request, plugin);
            if (installedPlugin is not null && request.PackageFingerprint is not null &&
                request.PackageFingerprint != NativeDesktopPluginPackage.ComputeFingerprint(installedPlugin.ManifestPath))
                throw new InvalidOperationException("The owner-installed plugin bytes do not match this reviewed package fingerprint. Report the installation failure, verify the current package, and queue a new review; do not replay this claim.");
            request.Status = status; request.Error = error; request.FailureStage = failureStage;
            request.Result = message; request.Resource = resource;
            request.Plugin = installedPlugin; request.CompletedAt = DateTimeOffset.UtcNow;
            if (success) request.PluginTests = CreatePluginTestContinuation(request);
            else if (controlTestsAllowed && IsSourceRepairFailureStage(failureStage))
            {
                try { request.PluginTests = CreatePluginRepairContinuation(request, true); }
                catch (Exception repairError) when (repairError is ArgumentException or InvalidDataException or
                    InvalidOperationException or UnauthorizedAccessException or IOException or System.Text.Json.JsonException)
                {
                    // The actual owner review outcome is authoritative and must remain durable even when
                    // its optional repair continuation cannot be admitted safely.
                    request.PluginTests = CreateBlockedPluginRepairContinuation(request, repairError.Message);
                }
            }
            else request.PluginTests = null;
            store.Put(RecordKind, id, request); if (request.Source != "workflow") AppendReceipt(request); changed = true; return request;
        });
        if (changed) Publish(value);
        return value;
    }

    // Read only the host-bound conversation's receipt. Native workflow conversations
    // have no legacy TaskSession row, so ownership comes from the retained request,
    // never from a model-supplied profile/conversation or the presentation's prose.
    internal InterfaceRequest GetForContext(string id, ToolExecutionContext context,
        string toolName = InterfaceToolExtension.StatusToolName, string capability = "filesystem.read") => store.Locked(() =>
    {
        // Closed host-operation pairs preserve the fixed authorization boundary. Model arguments never
        // select these values, and no arbitrary tool name or caller-chosen capability can admit a receipt.
        var statusRead = toolName == InterfaceToolExtension.StatusToolName && capability == "filesystem.read";
        var pluginControls = toolName == "plugin.controls" && capability == "desktop.plugins.manage";
        if (!statusRead && !pluginControls)
            throw new ArgumentException("Receipt access supports only interface.status/filesystem.read or plugin.controls/desktop.plugins.manage.");
        var requiredTool = pluginControls ? "plugin.controls" : InterfaceToolExtension.StatusToolName;
        var requiredCapability = pluginControls ? "desktop.plugins.manage" : "filesystem.read";
        var request = store.Get<InterfaceRequest>(RecordKind, id);
        if (string.IsNullOrWhiteSpace(context.SessionId) || request is null ||
            request.SessionId != context.SessionId || request.ProfileId != context.Profile.Id)
            throw new UnauthorizedAccessException("No presentation receipt belongs to this profile and conversation with that ID.");
        if (request.Source == "workflow")
            WorkflowUiOrigin.RequireReceipt(store, context, request, requiredTool, requiredCapability);
        else
        {
            // The legacy job/session route remains separate. A workflow cannot use an old
            // presentation to fall back to a fabricated profile-only authorization context.
            var job = store.Get<AgentJob>("job", context.JobId);
            var session = store.GetSessionMetadata(context.SessionId);
            var current = store.Get<AgentProfile>("agent", context.Profile.Id);
            if (job is null || job.ParentJobId is not null || job.SessionId != context.SessionId ||
                job.ProfileId != context.Profile.Id || session?.ProfileId != context.Profile.Id ||
                context.Profile.Role != "lead" || context.Profile.WorkerId != "local" ||
                current is null || current.Role != "lead" || current.WorkerId != "local" ||
                !context.Profile.Capabilities.Contains(requiredCapability, StringComparer.Ordinal) ||
                !current.Capabilities.Contains(requiredCapability, StringComparer.Ordinal) ||
                !WorkerExecutionSnapshot.AllowsTool(context.Profile, requiredTool) ||
                !WorkerExecutionSnapshot.AllowsTool(current, requiredTool))
                throw new UnauthorizedAccessException("The retained legacy receipt requires its root job, session and current grants.");
        }
        return request;
    });

    // `reviewed` is a convenience for the service-owned automatic QA run only. It derives one
    // presentation ID from that run's fixed identity and then crosses the same receipt-access
    // boundary as a concrete ID; it never searches for a recent or otherwise matching receipt.
    internal string ResolvePresentationReference(string reference, ToolExecutionContext context,
        string toolName = InterfaceToolExtension.StatusToolName, string capability = "filesystem.read")
    {
        if (!string.Equals(reference, ReviewedReference, StringComparison.Ordinal)) return reference;
        if (!PluginControlReferences.TryResolveReviewedPresentationId(context.JobId, out var concreteId))
            throw new UnauthorizedAccessException("The reviewed presentation reference is available only inside its bound automatic plugin QA run.");

        var request = GetForContext(concreteId, context, toolName, capability);
        if (request.Source != "workflow" || request.Kind != "plugin" || request.Status != "completed" ||
            request.Plugin is not { SchemaVersion: 1, ControlTestsAllowed: true } ||
            request.PluginTests is not { } tests ||
            tests.RequestId != context.JobId || tests.RunId != context.JobId ||
            tests.RequestId != PluginTestPrefix + request.Id)
            throw new UnauthorizedAccessException("The reviewed presentation reference is not bound to this admitted automatic plugin QA run.");
        return request.Id;
    }

    public InterfaceRequest GetForOwner(string id) => Get(id);
    public InterfaceRequest GetForOwner(string id, string sessionId)
    {
        if (!Guid.TryParseExact(id, "N", out var requestId) || requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128 || sessionId.Any(char.IsControl))
            throw new ArgumentException("Use an exact bounded owner request and conversation.");
        var request = Get(id);
        if (request.SessionId != sessionId)
            throw new UnauthorizedAccessException("The retained request belongs to another conversation.");
        return request;
    }
    private InterfaceRequest Get(string id) => store.Get<InterfaceRequest>(RecordKind, id) ?? throw new ArgumentException("Interface request not found.");

    private InterfacePresentationPath ValidatePresentationSource(InterfaceRequest request, AgentProfile current,
        out bool verifiedWork)
    {
        verifiedWork = false;
        if (request.Source != "workflow")
            return InterfacePathPolicy.ValidatePresentation(current, request.Kind, request.Path, fullAccess?.Invoke() == true);
        var scoped = WorkflowUiOrigin.ResolveRetainedPresentation(store, request, fullAccess?.Invoke() == true, out verifiedWork);
        return ValidateWorkflowPresentation(scoped, request.Kind, request.Path);
    }

    private static InterfacePresentationPath ValidateWorkflowPresentation(ToolExecutionContext scoped, string kind, string path)
    {
        if (kind == "plugin") return InterfacePathPolicy.ValidateNativePluginPresentation(scoped, path);
        if (path.Length > 32768 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Interface requests require an absolute path inside a selected project root.");
        var exact = ScopedFilePolicy.PathInScope(path, scoped, ScopedPathAccess.Read, mustExist: true);
        // Keep the existing non-plugin kind/lead/metadata policy after enforcing the real ceiling.
        return InterfacePathPolicy.ValidatePresentation(scoped.Profile, kind, exact, scoped.FullAccess);
    }

    private static void ValidateRetainedSource(InterfaceRequest request, InterfacePresentationPath validated)
    {
        // Historical absence stays absent on replay. A known admission coordinate cannot be
        // replaced by a fresh guess or by metadata for a different source package.
        if (request.GeneratedSourceRoot is not null &&
            (request.Kind != "plugin" || request.PackageFingerprint is null ||
             !request.GeneratedSourceRoot.Equals(validated.GeneratedSourceRoot,
                 OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new InvalidOperationException("The retained generated source coordinate no longer matches this exact plugin review. Verify and queue a new owner review.");
    }
    private static string? Fingerprint(string kind, string path) => kind == "plugin" ? NativeDesktopPluginPackage.ComputeFingerprint(path) : null;
    private static void ValidateResource(InterfaceResourceReceipt? resource)
    {
        if (resource is null) return;
        if (!Guid.TryParseExact(resource.Id, "N", out var id) || id == Guid.Empty || resource.Revision < 1 ||
            resource.MediaType is not ("image/png" or "image/jpeg") || resource.Length is < 1 or > 16 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(resource.OriginalName) || resource.OriginalName.Length > 255 ||
            resource.OriginalName.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
            throw new ArgumentException("An interface resource must be a bounded immutable PNG or JPEG attachment receipt.");
    }
    private static void ValidateClaimId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Contains('\0')) throw new ArgumentException("Use a nonempty interface claim identifier up to 128 characters.");
    }
    private static string Limit(string value) => value[..Math.Min(value.Length, 2000)];
    private void AppendReceipt(InterfaceRequest request)
    {
        var result = request.Status == "completed"
            ? "The user interface reported completing the requested presentation. " + (request.Result ?? "No further application outcome was reported.")
            : "The user interface request failed: " + request.Error;
        if (request.Resource is { } resource)
            result += $" Capture attachment: id={resource.Id}; revision={resource.Revision}; mediaType={resource.MediaType}; name={Limit(resource.OriginalName)}; bytes={resource.Length}.";
        store.Append(request.SessionId, "user", $"Interface request {request.Id}; {request.Kind}; path={Limit(request.Path)}; status={request.Status}. {result}", origin: "service");
    }
    private void Publish(InterfaceRequest request)
    {
        events.Publish("interface.updated", request.JobId, request);
        if (request.Status is "completed" or "failed") events.Publish("session.updated", request.JobId, new { id = request.SessionId });
    }
}
