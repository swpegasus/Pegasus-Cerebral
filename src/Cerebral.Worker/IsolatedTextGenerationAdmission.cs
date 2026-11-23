using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

internal static class IsolatedTextGenerationAdmission
{
    internal static void RequireProfile(IsolatedTextGenerationRequest request, AgentProfile profile)
    {
        if (profile.Id != request.ProfileId || profile.Revision != request.ProfileRevision || profile.Role != "helper" ||
            profile.Purpose != request.Purpose || profile.WorkerId != "local" || profile.ModelWorkerId != "local" ||
            string.IsNullOrWhiteSpace(profile.ModelId) || profile.ContextTokens is < 512 or > 1048576 || profile.ResponseTokens < 1)
            throw new UnauthorizedAccessException("Select the current exact revision of a dedicated local suggestion profile.");
    }

    internal static WorkerExecutionSnapshot.Snapshot Snapshot(IsolatedTextGenerationRequest request, AgentProfile current,
        ModelRecord model, IReadOnlyList<ServicePluginPackageBinding> packages, string routingFingerprint)
    {
        RequireProfile(request, current);
        if (model.Id != current.ModelId) throw new UnauthorizedAccessException("The isolated model binding changed.");
        // Construct from a whitelist. No clone of the source profile or ordinary context-capture path:
        // added profile fields, plugin settings, grants and instructions cannot silently flow here.
        var profile = new AgentProfile
        {
            Id = current.Id, Revision = current.Revision, Name = "Isolated text", Role = "helper", Purpose = current.Purpose,
            ModelId = current.ModelId, WorkerId = "local", ModelWorkerId = "local", FollowsDefaultModel = false,
            FollowsModelBudgets = false, ContextTokens = current.ContextTokens,
            ResponseTokens = Math.Min(request.MaximumOutputTokens, current.ResponseTokens), ReasoningMode = "off",
            MaxSteps = 2, MaxSeconds = 30, AutomaticCompaction = false,
            AllowedRoots = [], AllowedTools = [], Capabilities = [], RequiredModelCapabilities = [], PluginSettings = []
        };
        return new(WorkerExecutionSnapshot.Version, profile, "", "", WorkerExecutionSnapshot.Bind(model),
            [], [], [], [], false, routingFingerprint, packages, []);
    }

    internal static IsolatedTextGenerationRequest RequireSubmission(WorkflowSubmission submission)
    {
        if (submission.DefinitionId != IsolatedTextGenerationContract.DefinitionId ||
            submission.DefinitionVersion != IsolatedTextGenerationContract.DefinitionVersion ||
            submission.IsolatedText is not { SchemaVersion: 1 } admission ||
            submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.isolated-text" } ||
            submission.ParentRunId is not null || submission.ProjectId is not null || submission.LocalGeneration is not null ||
            submission.AcceptedContent.Count != 0 || submission.AssignmentText is not null)
            throw new UnauthorizedAccessException("This effect requires isolated owner admission.");
        var request = WorkerWorkflows.Read<IsolatedTextGenerationRequest>(submission.Input);
        request.Validate(DateTimeOffset.UtcNow, retained: true);
        if (request.RequestId != submission.RequestId || request.ProfileId != submission.ProfileId ||
            request.ProfileRevision != admission.ProfileRevision || submission.ConversationId != "isolated-" + request.RequestId)
            throw new UnauthorizedAccessException("The isolated request identity changed.");
        return request;
    }

    internal static ModelGenerationRequest ModelRequest(IsolatedTextGenerationRequest input, WorkerExecutionSnapshot.Snapshot snapshot)
    {
        RequireProfile(input, snapshot.Profile);
        if (snapshot.SharedInstructions.Length != 0 || snapshot.RoleInstructions.Length != 0 || snapshot.Profile.Instructions.Length != 0 ||
            snapshot.FullAccess || snapshot.Tools.Count != 0 || snapshot.Skills.Count != 0 || snapshot.AllowedRoots.Count != 0 ||
            snapshot.AllowedTools.Count != 0 || snapshot.Capabilities.Count != 0 || snapshot.Profile.PluginSettings.Count != 0 ||
            snapshot.Profile.AllowedRoots.Count != 0 || snapshot.Profile.AllowedTools.Count != 0 || snapshot.Profile.Capabilities.Count != 0 ||
            snapshot.Compactor is not null || snapshot.CortexScope is not null || snapshot.NativeContinuity is not null ||
            snapshot.ContextContributions.Count != 0 || snapshot.ConversationPaths is not null || snapshot.Profile.AutomaticCompaction ||
            snapshot.Profile.RequiredModelCapabilities.Count != 0 || snapshot.Profile.CompactionProfileId is not null ||
            snapshot.Profile.RemoteProfileId is not null || snapshot.Profile.ReasoningMode != "off" ||
            snapshot.ContextContributionInstallationId is not null || snapshot.ApprovedPluginSettings?.Count != 0 ||
            snapshot.SelectedToolOwners?.Count != 0 || snapshot.SourceBoundBackground is not null || snapshot.ChatPermissions is not null ||
            snapshot.OwnerVerification is not null || snapshot.WorkflowData is not null)
            throw new UnauthorizedAccessException("Isolated generation cannot inherit context or grants.");
        var request = new ModelGenerationRequest(snapshot.Profile.ModelId,
            [new("system", [new TextContentPart(input.Policy)]), new("user", [new TextContentPart(input.Text)])],
            snapshot.Profile.ContextTokens, Math.Min(input.MaximumOutputTokens, snapshot.Profile.ResponseTokens), Temperature: .1);
        request.Validate();
        return request;
    }
}
