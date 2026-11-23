using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Core;
using Cerebral.PluginLifecycle;
using Cerebral.Tools;
using static Cerebral.Worker.ChatPluginInstallPolicy;

namespace Cerebral.Worker;

/// <summary>Trusted private adapter. Neither Origin nor capture is exposed through a command or tool.</summary>
internal sealed class ChatPluginInstallRuntime(StateStore store, WorkflowStore workflows,
    EventHub events, Func<bool> fullAccess)
{
    internal static ChatPluginInstallRuntime? ForExisting(StateStore store, EventHub events, Func<bool> fullAccess)
    {
        var database = Path.Combine(store.DirectoryPath, "engine.db");
        // Match the existing WorkflowUiOrigin retained-store route; never create an engine database.
        return File.Exists(database) ? new(store, new WorkflowStore(new EventJournal(database)), events, fullAccess) : null;
    }

    internal ChatPluginInstallAuthorizationStore Policies => new(store, events, SavedScope);

    private Scope SavedScope(string id)
    {
        ValidateId(id);
        if (store.GetSessionMetadata(id) is not null)
            throw new NotSupportedException("Automatic native-plugin installation requires a saved native chat.");
        var conversation = workflows.GetConversation(ConversationWorkflowModule.DefinitionId, id) ??
            throw new KeyNotFoundException("The saved native chat was not found.");
        var original = workflows.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id) ??
            throw new NotSupportedException("Inherited chats cannot acquire an independent install permission.");
        WorkflowUiOrigin.RequireRoot(original);
        if (original.ConversationId != id || original.ProfileId != conversation.ProfileId ||
            original.ProjectId != conversation.ProjectId)
            throw new InvalidDataException("The chat's original owner identity changed.");
        return new(id, conversation.ProfileId, conversation.ProjectId);
    }

    internal bool CaptureNew(InterfaceRequest request, ToolExecutionContext context, string? expectedFingerprint)
    {
        if (request.Kind != "plugin" || request.Source != "workflow") return false;
        try
        {
            if (!Policies.Get(request.SessionId, actorProfileId: null).AllowAutomaticInstall) return false;
            // Reuse durable dispatched-effect origin and actual narrowed path/profile grants.
            var scoped = WorkflowUiOrigin.ResolvePresentation(store, context, "plugin", request.Path,
                expectedFingerprint, fullAccess());
            var run = workflows.TryGet(context.JobId) ??
                throw new UnauthorizedAccessException("The accepted presentation run is unavailable.");
            var origin = OriginFrom(request, run, context.EffectId ?? throw new UnauthorizedAccessException("The source effect identity is unavailable."), scoped);
            return Policies.CaptureNew(RequestFor(request), origin);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException or JsonException or
                                            NotSupportedException or KeyNotFoundException or ArgumentException or IOException)
        {
            // Optional auto-install is unavailable, not proof of a failed manual review.
            // Normal CreateForWorkflow already owns all mandatory package/path admission failures.
            return false;
        }
    }

    internal Origin ResolveCurrent(Request request, Presentation binding)
    {
        var actual = store.Get<InterfaceRequest>("interface", request.Id) ??
            throw new UnauthorizedAccessException("The exact presentation is unavailable.");
        if (RequestFor(actual) != request) throw new UnauthorizedAccessException("The presentation changed.");
        // Existing retained-origin resolution intersects accepted/current tool, capability, roots
        // and path authority; this permission cannot widen any of them or promote a helper/QA child.
        var scoped = WorkflowUiOrigin.ResolveRetainedPresentation(store, actual, fullAccess());
        var run = workflows.TryGet(actual.JobId) ??
            throw new UnauthorizedAccessException("The accepted presentation run is unavailable.");
        return OriginFrom(actual, run, binding.EffectId, scoped);
    }

    private Origin OriginFrom(InterfaceRequest request, WorkflowRun run, string effectId, ToolExecutionContext scoped)
    {
        WorkflowUiOrigin.RequireRoot(run.Submission);
        if (!ChatPluginInstallDefinitionCompatibility.Supports(run.Submission.DefinitionId, run.Submission.DefinitionVersion))
            throw new UnauthorizedAccessException(ChatPluginInstallDefinitionCompatibility.UnsupportedMessage);
        if (run.CancelRequested || run.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.Cancelling ||
            run.Id != request.JobId || run.Submission.RequestId != request.JobId ||
            run.Submission.ConversationId != request.SessionId || run.Submission.ProfileId != request.ProfileId)
            throw new UnauthorizedAccessException("The source run is ineligible for chat-authorized installation.");
        var scope = SavedScope(request.SessionId);
        if (run.Submission.ProfileId != scope.ProfileId || run.Submission.ProjectId != scope.ProjectId) throw new UnauthorizedAccessException("The source project changed.");
        // Metadata/path admission and immutable bytes remain mandatory at capture, claim and effect.
        var validated = InterfacePathPolicy.ValidateNativePluginPresentation(scoped, request.Path);
        var source = validated.GeneratedSourceRoot;
        if (!string.Equals(source, request.GeneratedSourceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The presented generated source changed.");
        if (NativeDesktopPluginPackage.ComputeFingerprint(request.Path) != request.PackageFingerprint)
            throw new UnauthorizedAccessException("The authorized native package bytes changed.");
        var snapshotHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(run.Submission.SettingsSnapshot.GetRawText()))).ToLowerInvariant();
        return new(scope, run.Id, snapshotHash, effectId, RootUser: true, Eligible: true, CurrentGrantsValid: true,
            SourceStatus: run.Status.ToString().ToLowerInvariant());
    }

    private Request RequestFor(InterfaceRequest request) => new(request.Id, SavedScope(request.SessionId),
        request.JobId, request.Path, request.PackageFingerprint ??
            throw new UnauthorizedAccessException("The exact package fingerprint is missing."),
        request.CreatedAt, request.Status, request.ClaimId);
}
