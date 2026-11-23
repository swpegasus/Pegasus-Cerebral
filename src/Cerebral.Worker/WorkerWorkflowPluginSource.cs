using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    /// <summary>A retained owner-review coordinate, not an inspected recipe, file preimage or grant.</summary>
    public sealed record PluginSourceCoordinate(int SchemaVersion, string PresentationId, string Root);

    private PluginSourceCoordinate? ReadPluginSourceCoordinate(WorkflowCommandContext context,
        WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot, AgentProfile effective)
    {
        if (accepted.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } ||
            !PluginControlReferences.TryResolveReviewedPresentationId(context.RunId, out var presentationId))
            return null;
        return store.Locked(() =>
        {
            var presentation = store.Get<InterfaceRequest>("interface", presentationId)
                ?? throw new AdmissionException("The automatic QA owner-review receipt is unavailable.");
            if (presentation.Id != presentationId || presentation.Source != "workflow" || presentation.Kind != "plugin" ||
                presentation.SessionId != accepted.ConversationId || presentation.ProfileId != accepted.ProfileId ||
                presentation.PluginTests is not { } tests || tests.RequestId != context.RunId ||
                tests.RunId is not null && tests.RunId != context.RunId ||
                tests.Round is < 1 or > InterfaceRequests.MaximumPluginTestRounds ||
                !(tests.Mode is null && presentation.Status == "completed" &&
                    presentation.Plugin is { SchemaVersion: 1, ControlTestsAllowed: true } ||
                  tests.Mode == InterfacePluginTestModes.RepairBeforeInstall && presentation.Status == "failed" &&
                    presentation.Plugin is null && presentation.ClaimId is not null && presentation.CompletedAt is not null))
                throw new AdmissionException("The source coordinate has no exact owner-approved automatic QA receipt.");
            var workflows = new WorkflowStore(new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")));
            var source = workflows.Get(presentation.JobId).Submission;
            var original = workflows.Get(tests.OriginRunId).Submission;
            if (source.ConversationId != accepted.ConversationId || original.ConversationId != accepted.ConversationId ||
                source.ProfileId != accepted.ProfileId || original.ProfileId != accepted.ProfileId ||
                source.DefinitionId != ConversationWorkflowModule.DefinitionId ||
                original.DefinitionId != ConversationWorkflowModule.DefinitionId ||
                accepted.DefinitionId != ConversationWorkflowModule.DefinitionId ||
                source.DefinitionVersion != accepted.DefinitionVersion || original.DefinitionVersion != accepted.DefinitionVersion)
                throw new AdmissionException("The owner-review source coordinate belongs to a different workflow lineage.");
            if (presentation.GeneratedSourceRoot is not { } root) return null;
            if (root.Length is < 1 or > 2048 || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root) ||
                !string.Equals(Path.GetFullPath(root), root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new AdmissionException("The owner-review source coordinate is not a bounded canonical root.");
            // Even reference coordinates remain confined to accepted/current path authority.
            // No source is read and no recipe or old build credit is imported here.
            var scoped = ScopedFilePolicy.PathInScope(root,
                new(effective, accepted.ConversationId, accepted.RequestId, FullAccess: snapshot.FullAccess && tools.FullAccessActive)
                    { EnforceProfileScope = true, PathAccess = ConversationPathAuthority.Resolve(store, workflows, accepted, snapshot, tools.FullAccessActive) },
                ScopedPathAccess.Read, mustExist: false);
            return new PluginSourceCoordinate(1, presentationId, scoped);
        });
    }
}
