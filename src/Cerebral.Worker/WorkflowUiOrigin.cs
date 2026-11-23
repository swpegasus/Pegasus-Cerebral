using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker;

// UI origin is private host evidence. Neither a role, ToolExecutionContext nor a QA-looking
// request ID proves that a workflow owns the signed-in user's conversation.
internal static class WorkflowUiOrigin
{
    private static readonly JsonSerializerOptions ReceiptJson = new(CerebralJson.Options)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    private sealed record Binding(WorkflowRun Run, WorkerExecutionSnapshot.Snapshot Snapshot,
        ToolExecutionContext Context, string? ReviewedPresentationId, IReadOnlyList<string>? SuppliedRootCeiling);

    internal static bool LocalAuthor(AgentProfile profile) =>
        profile.WorkerId == "local" && profile.Role is "lead" or "helper";

    internal static void RequireRoot(WorkflowSubmission submission) {
        if (submission.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            submission.ParentRunId is not null || submission.Caller?.Kind != EventActorKind.User)
            throw new UnauthorizedAccessException("UI access requires the owner's root conversation or its exact approved plugin continuation.");
    }

    internal static ToolExecutionContext ResolveRead(StateStore store, ToolExecutionContext context,
        string toolName, string capability, bool rootOnly = false) =>
        Resolve(store, context, toolName, capability, rootOnly).Context;

    internal static void RequireReceipt(StateStore store, ToolExecutionContext context,
        InterfaceRequest request, string toolName, string capability)
    {
        var binding = Resolve(store, context, toolName, capability, rootOnly: false);
        if (request.SessionId != binding.Context.SessionId || request.ProfileId != binding.Context.Profile.Id ||
            binding.ReviewedPresentationId is { } reviewed && request.Id != reviewed)
            throw new UnauthorizedAccessException("No exact approved presentation receipt belongs to this workflow and conversation.");
    }

    internal static ToolExecutionContext ResolvePresentation(StateStore store, ToolExecutionContext supplied,
        string kind, string path, string? expectedFingerprint, bool currentFullAccess)
        => ResolvePresentation(store, supplied, kind, path, expectedFingerprint, currentFullAccess, out _);

    internal static ToolExecutionContext ResolvePresentation(StateStore store, ToolExecutionContext supplied,
        string kind, string path, string? expectedFingerprint, bool currentFullAccess, out string? verifiedWorkFingerprint)
    {
        var journal = Journal(store);
        var workflows = new WorkflowStore(journal);
        var run = Run(workflows, supplied.JobId);
        var effect = run.Effect;
        if (run.CancelRequested || run.Status != WorkflowStatus.Running || effect is null ||
            effect.Id != supplied.EffectId || effect.State != WorkflowEffectState.Dispatched ||
            effect.CommandId != WorkerWorkflowEffects.ToolCommand || effect.CommandVersion != 1)
            throw new UnauthorizedAccessException("The exact presentation effect is no longer executing.");
        var input = effect.Arguments.Deserialize<WorkerWorkflowEffects.ToolArguments>(CerebralJson.Options)
            ?? throw new UnauthorizedAccessException("The presentation effect arguments are unavailable.");
        var plugin = input.Name == InterfaceToolExtension.PluginToolName;
        if ((!plugin && input.Name != InterfaceToolExtension.ToolName) || plugin && kind != "plugin" ||
            input.Arguments.ValueKind != JsonValueKind.Object)
            throw new UnauthorizedAccessException("The executing effect is not this presentation operation.");
        var fields = input.Arguments.EnumerateObject().ToArray();
        if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(field => plugin ? field.Name is not ("path" or "expectedPackageFingerprint") :
                field.Name is not ("kind" or "path")) ||
            !Text(input.Arguments, "path", path) || !plugin && !Text(input.Arguments, "kind", kind) ||
            (input.Arguments.TryGetProperty("expectedPackageFingerprint", out var fingerprint)
                ? fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() != expectedFingerprint
                : expectedFingerprint is not null))
            throw new UnauthorizedAccessException("The presentation does not match its exact dispatched arguments.");
        var work = plugin && run.Submission.DefinitionId != ConversationWorkflowModule.DefinitionId;
        var binding = Resolve(store, supplied, input.Name, "filesystem.read", rootOnly: false, allowWorkPlugin: work);
        verifiedWorkFingerprint = work ? VerifyWorkPlugin(journal, workflows, run, path) : null;
        return WithPathScope(store, binding, currentFullAccess && !work);
    }

    internal static ToolExecutionContext ResolveRetainedPresentation(StateStore store, InterfaceRequest request,
        bool currentFullAccess) => ResolveRetainedPresentation(store, request, currentFullAccess, out _);

    internal static ToolExecutionContext ResolveRetainedPresentation(StateStore store, InterfaceRequest request,
        bool currentFullAccess, out bool verifiedWork)
    {
        var run = Run(Store(store), request.JobId);
        if (run.CancelRequested || run.Status is WorkflowStatus.Cancelled or WorkflowStatus.Failed)
            throw new UnauthorizedAccessException("The presenting workflow was cancelled or failed.");
        var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
        var tool = request.Kind == "plugin" &&
            snapshot.Tools.Any(item => item.Name == InterfaceToolExtension.PluginToolName) &&
            WorkerExecutionSnapshot.AllowsTool(snapshot.Profile, InterfaceToolExtension.PluginToolName)
                ? InterfaceToolExtension.PluginToolName : InterfaceToolExtension.ToolName;
        var context = new ToolExecutionContext(snapshot.Profile, request.SessionId, request.JobId,
            FullAccess: snapshot.FullAccess && currentFullAccess);
        verifiedWork = request.Kind == "plugin" && tool == InterfaceToolExtension.PluginToolName &&
            run.Submission.DefinitionId != ConversationWorkflowModule.DefinitionId;
        var binding = Resolve(store, context, tool, "filesystem.read", rootOnly: false, suppliedContext: false,
            allowWorkPlugin: verifiedWork);
        if (verifiedWork)
            _ = WorkPluginRoot(run.Submission, request.Path);
        return WithPathScope(store, binding, currentFullAccess && !verifiedWork);
    }

    private static ToolExecutionContext WithPathScope(StateStore store, Binding binding, bool currentFullAccess)
    {
        var derived = ConversationPathAuthority.Resolve(store, Store(store), binding.Run.Submission,
            binding.Snapshot, currentFullAccess);
        // A supplied context is never a broader authority, but a host may already have
        // narrowed it. Preserve that ceiling and any earlier Full Access refusal.
        if (binding.Context.PathAccess is { } supplied)
            derived = derived is null ? ScopedPathAccessPolicy.Normalize(supplied) :
                ConversationPathAuthority.Intersect(derived, supplied);
        var fullAccess = binding.Context.FullAccess && binding.Snapshot.FullAccess && currentFullAccess;
        if (binding.SuppliedRootCeiling is { } roots)
        {
            var actualRoots = ConversationPathGrants.Normalize(roots.Where(Path.IsPathFullyQualified)
                .Select(path => new ScopedPathGrant(path, ScopedPathAccess.Write)).ToArray());
            derived = derived is null ? actualRoots : ConversationPathAuthority.Intersect(derived, actualRoots);
        }
        return binding.Context with
        {
            FullAccess = fullAccess,
            EnforceProfileScope = true,
            PathAccess = derived
        };
    }

    private static Binding Resolve(StateStore store, ToolExecutionContext context,
        string toolName, string capability, bool rootOnly, bool suppliedContext = true, bool allowWorkPlugin = false)
    {
        var workflows = Store(store);
        var run = Run(workflows, context.JobId);
        var accepted = run.Submission;
        var work = allowWorkPlugin && accepted.DefinitionId != ConversationWorkflowModule.DefinitionId;
        if (accepted.ConversationId != context.SessionId || accepted.ProfileId != context.Profile.Id ||
            (!work && accepted.DefinitionId != ConversationWorkflowModule.DefinitionId) ||
            store.GetSessionMetadata(context.SessionId) is not null)
            throw new UnauthorizedAccessException("The UI context does not match its native accepted conversation.");
        if (work && (accepted.ParentRunId is not null || accepted.Caller?.Kind != EventActorKind.User ||
            accepted.RequestId != run.Id))
            throw new UnauthorizedAccessException("Plugin presentation requires the exact owner-started Work run.");
        ConversationSummary? conversation;
        try
        {
            conversation = work
                ? workflows.GetOwnerWorkflowConversation(context.SessionId, run.Id, accepted.DefinitionId, accepted.DefinitionVersion).Summary
                : workflows.GetConversation(ConversationWorkflowModule.DefinitionId, context.SessionId);
        }
        catch (KeyNotFoundException)
        {
            throw new UnauthorizedAccessException("The UI workflow no longer owns this conversation and project.");
        }
        if (conversation is null || conversation.ProfileId != accepted.ProfileId || conversation.ProjectId != accepted.ProjectId)
            throw new UnauthorizedAccessException("The UI workflow no longer owns this conversation and project.");
        if (work)
        {
            // The selected Work run may be a later direct owner turn. Keep the original
            // owner-started root as the immutable conversation anchor; current accepted
            // input, effect, verification and path scope still come from the selected run.
            var original = workflows.ReadOriginalConversationSubmission(accepted.DefinitionId, context.SessionId);
            if (original is null || original.RequestId != context.SessionId ||
                original.ConversationId != context.SessionId || original.ProfileId != accepted.ProfileId ||
                original.ProjectId != accepted.ProjectId || original.DefinitionVersion != accepted.DefinitionVersion ||
                original.ParentRunId is not null || original.Caller?.Kind != EventActorKind.User ||
                original.Caller.Id != accepted.Caller?.Id)
                throw new UnauthorizedAccessException("Plugin presentation requires the original owner-started Work conversation.");
        }
        string? reviewed = null;
        if (!work && accepted.ParentRunId is null && accepted.Caller?.Kind == EventActorKind.User)
            RequireRoot(accepted);
        else if (!work)
        {
            if (rootOnly) throw new UnauthorizedAccessException("Installed plugin management requires the root owner conversation.");
            // Reuses the exact durable submission, review, project and direct-lineage proof.
            try { RequireRoot(PluginContinuationGoal.PathAuthoritySource(store, workflows, accepted)); }
            catch (KeyNotFoundException) { throw new UnauthorizedAccessException("The approved plugin lineage is unavailable."); }
            if (!PluginControlReferences.TryResolveReviewedPresentationId(accepted.RequestId, out reviewed))
                throw new UnauthorizedAccessException("The approved plugin continuation has no exact review identity.");
        }
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        var current = store.Get<AgentProfile>("agent", accepted.ProfileId)
            ?? throw new UnauthorizedAccessException("The UI profile is no longer configured.");
        var effective = WorkerExecutionSnapshot.EffectiveProfile(store, snapshot, accepted.ProfileId);
        if (!LocalAuthor(snapshot.Profile) || !LocalAuthor(current) || !LocalAuthor(context.Profile) ||
            !snapshot.Tools.Any(tool => tool.Name == toolName && tool.Capability == capability) ||
            !(snapshot.AllowedTools.Contains("*", StringComparer.Ordinal) ||
              snapshot.AllowedTools.Contains(toolName, StringComparer.Ordinal)) ||
            !snapshot.Capabilities.Contains(capability, StringComparer.Ordinal) ||
            !effective.Capabilities.Contains(capability, StringComparer.Ordinal) ||
            !context.Profile.Capabilities.Contains(capability, StringComparer.Ordinal) ||
            !WorkerExecutionSnapshot.AllowsTool(effective, toolName) ||
            !WorkerExecutionSnapshot.AllowsTool(context.Profile, toolName))
            throw new UnauthorizedAccessException("The accepted and current local profile do not grant this UI operation.");
        // Match ordinary ScopedFilePolicy: PathAccess is operative when present; otherwise
        // non-FullAccess callers are bounded by their actual profile roots. Retained delivery
        // has no supplied execution context and rederives the current durable scope instead.
        var suppliedRoots = suppliedContext && context.PathAccess is null && !context.FullAccess
            ? context.Profile.AllowedRoots.ToArray() : null;
        return new(run, snapshot, context with { Profile = effective, EnforceProfileScope = true }, reviewed, suppliedRoots);
    }

    private static string VerifyWorkPlugin(EventJournal journal, WorkflowStore workflows, WorkflowRun run, string path)
    {
        var root = WorkPluginRoot(run.Submission, path);
        const string message = "Work must verify this exact built plugin before requesting owner review.";
        var id = LatestWorkToolReceipt(journal, workflows, run, message);
        WorkflowCommandResult result;
        long revision;
        try { result = workflows.GetEffectResult(run.Id, id, out revision); }
        catch (KeyNotFoundException) { throw new UnauthorizedAccessException(message); }
        var data = result.Data?.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options);
        if (revision != run.Revision || result.State != WorkflowEffectState.Succeeded ||
            data is not { Name: PluginScaffoldToolExtension.VerifyToolName, Truncated: false, FollowUpRequired: false,
                Evidence: { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded,
                    State: "verified", Partial: not true } })
            throw new UnauthorizedAccessException(message);
        using var proof = JsonDocument.Parse(data.Content);
        var receipt = proof.RootElement;
        if (receipt.ValueKind != JsonValueKind.Object || !Text(receipt, "root", root) ||
            !receipt.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
            !receipt.TryGetProperty("desktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object ||
            !Text(desktop, "manifest", path) || !desktop.TryGetProperty("packageFingerprint", out var fingerprint) ||
            fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() is not { Length: 64 } hash ||
            hash.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new UnauthorizedAccessException(message);
        return hash;
    }

    private static string LatestWorkToolReceipt(EventJournal journal, WorkflowStore workflows,
        WorkflowRun run, string message)
    {
        // The Engine also records model/compaction and read-only receipt/history/catalog
        // controls as ToolResult. They do not act on the built artifact. Only these known
        // host controls may be skipped; unknown or incomplete evidence must not allow a
        // search past it to resurrect an older verification receipt.
        var page = journal.ReadPreviousPage(run.Submission.ConversationId, limit: 32);
        var expected = page.SnapshotSequence;
        foreach (var item in page.Events.Reverse())
        {
            if (item.Sequence != expected-- || item.ConversationId != run.Submission.ConversationId ||
                page.RetentionGap is { } gap && gap.LastPrunedSequence >= item.Sequence &&
                    gap.FirstPrunedSequence <= page.SnapshotSequence)
                throw new UnauthorizedAccessException(message);
            if (item.TurnId != run.Id || item.Type != CerebralEventTypes.ToolResult) continue;
            try
            {
                var details = item.Data?.Deserialize<ToolResultData>(CerebralJson.Options);
                if (details is null || item.Actor is not { Kind: EventActorKind.Tool } ||
                    item.Actor.Id != details.ToolName || item.Provenance.Source != EventSourceKind.Tool ||
                    item.Provenance.SourceId != details.ToolName || item.Provenance.JobId != run.Id ||
                    item.CorrelationId != details.CallId || details.Truncated || item.Content.Count != 1 ||
                    item.Content[0] is not StructuredContentPart { Schema: "workflow.command.result" } part)
                    throw new UnauthorizedAccessException(message);
                var result = workflows.GetEffectResult(run.Id, details.CallId, out var revision);
                // Engine and Core serializers have different enum spelling policies. Compare
                // strictly parsed typed receipts, retaining their exact embedded JSON payloads.
                var recorded = part.Value.Deserialize<WorkflowCommandResult>(ReceiptJson);
                if (revision != run.Revision || details.Success != (result.State == WorkflowEffectState.Succeeded) ||
                    recorded is null || !JsonElement.DeepEquals(CerebralJson.Element(recorded), CerebralJson.Element(result)))
                    throw new UnauthorizedAccessException(message);
                if (details.ToolName == WorkerWorkflowEffects.ToolCommand) return details.CallId;
                if (details.ToolName is not (WorkerWorkflowEffects.GenerateCommand or WorkerWorkflowEffects.CompactCommand or
                    WorkerWorkflowEffects.ReceiptCommand or WorkerWorkflowEffects.HistoryCommand or WorkerWorkflowEffects.CatalogCommand))
                    throw new UnauthorizedAccessException(message);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or
                KeyNotFoundException or InvalidDataException)
            { throw new UnauthorizedAccessException(message, error); }
        }
        // Keep the existing bounded observation window. Absence within it is never proof
        // that an older verification remains current, even if more history is available.
        throw new UnauthorizedAccessException(message);
    }

    private static string WorkPluginRoot(WorkflowSubmission submission, string path)
    {
        var input = submission.Input;
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("rootPath", out var rootValue) || rootValue.ValueKind != JsonValueKind.String ||
            rootValue.GetString() is not { } root || !Path.IsPathFullyQualified(root) ||
            !input.TryGetProperty("presentManifest", out var manifestValue) || manifestValue.ValueKind != JsonValueKind.String ||
            manifestValue.GetString() is not { Length: > 0 } relative || Path.IsPathRooted(relative) ||
            relative.Split(['/', '\\']).Contains("..") || !ScopedFilePolicy.IsWithin(path, Path.GetFullPath(root)) ||
            !string.Equals(Path.GetFullPath(relative, root), path, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The built manifest does not match the owner-approved Work input.");
        return Path.GetFullPath(root);
    }

    private static WorkflowStore Store(StateStore store) => new(Journal(store));

    private static EventJournal Journal(StateStore store)
    {
        var path = Path.Combine(store.DirectoryPath, "engine.db");
        if (!File.Exists(path)) throw new UnauthorizedAccessException("No durable workflow admission exists for UI access.");
        return new(path);
    }

    private static WorkflowRun Run(WorkflowStore store, string id) => store.TryGet(id)
        ?? throw new UnauthorizedAccessException("No durable workflow admission exists for this UI context.");

    private static bool Text(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() == expected;
}
