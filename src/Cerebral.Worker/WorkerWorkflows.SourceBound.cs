using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    private static readonly UTF8Encoding SourceBoundUtf8 = new(false, true);
    internal void RequireSourceBoundSchedulerCandidate(WorkflowRun run)
    {
        var marker = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).SourceBoundBackground
            ?? throw new InvalidDataException("Queued source-bound work has no valid host marker.");
        if (run.Submission.Caller is not { Kind: EventActorKind.Service } caller ||
            caller.Id != marker.Source.PackageId || run.Id != marker.WorkRequestId ||
            run.Submission.ConversationId != marker.WorkConversationId)
            throw new InvalidDataException("Queued source-bound work has mismatched provenance.");
        RequireSourceBoundPublicProjection(run.Submission, marker);
    }

    internal async Task<SourceBoundBackgroundAdmission> SubmitSourceBoundBackgroundAsync(
        CommittedRootUserInputObservation observation, SourceBoundBackgroundIntent intent,
        ServicePluginPackageBinding package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        if (!package.Loaded || string.IsNullOrWhiteSpace(package.Generation) ||
            string.IsNullOrWhiteSpace(intent.DefinitionId) ||
            !intent.DefinitionId.StartsWith(package.Id + ":", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(intent.DefinitionVersion) ||
            string.IsNullOrWhiteSpace(intent.WorkRequestId) ||
            string.IsNullOrWhiteSpace(intent.WorkConversationId) ||
            string.IsNullOrWhiteSpace(intent.ProfileId) ||
            intent.WorkConversationId == observation.Source.ConversationId ||
            intent.WorkRequestId == observation.Source.RequestId ||
            string.IsNullOrWhiteSpace(intent.UserText) || intent.UserText.Length > 128 * 1024 ||
            intent.UserText.Contains('\0') || intent.ExpectedSettingsRevision < 1 ||
            intent.ExpectedProfileRevision < 1 || intent.StepLimit is < 1 or > WorkflowContract.MaximumSteps ||
            intent.PackageInput.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(intent.PackageInput).Length > WorkflowContract.MaximumStateBytes)
            throw new ArgumentException("Source-bound workflow intent is incomplete or exceeds its bounds.");
        var existing = Store.TryGet(intent.WorkRequestId);
        if (existing is not null)
        {
            RequireEquivalentSourceBoundRetainedIntent(existing, observation, intent, package);
            return new(intent.WorkRequestId, SourceBoundBackgroundAdmissionState.Existing,
                existing.Status, existing.Revision);
        }
        var marker = CaptureSourceBoundBinding(observation, intent, package);
        var input = new SourceBoundWorkflowInput(SourceBoundWorkflowInputContract.SchemaVersion,
            marker.Source, intent.PackageInput.Clone());
        var request = new SubmitWorkflowRequest(intent.WorkRequestId, intent.WorkConversationId,
            intent.ProfileId, intent.DefinitionId, intent.DefinitionVersion, intent.UserText, null,
            CerebralJson.Element(input), intent.StepLimit);
        WorkflowRun run;
        try
        {
            run = await SubmitAsyncCore(request, null, new(EventActorKind.Service, package.Id), null,
                cancellationToken, sourceBound: marker).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or IOException or OperationCanceledException)
        {
            // Inspect the one retained RID; never recapture/retry an uncertain
            // source, profile, settings or model selection under the same RID.
            try
            {
                if (Store.TryGet(intent.WorkRequestId) is { } retained)
                {
                    RequireEquivalentSourceBoundRetained(retained, marker);
                    return new(intent.WorkRequestId, SourceBoundBackgroundAdmissionState.Existing,
                        retained.Status, retained.Revision);
                }
            }
            catch (Exception lookup) when (lookup is SqliteException or IOException) { }
            return new(intent.WorkRequestId, SourceBoundBackgroundAdmissionState.Uncertain, null, null);
        }
        RequireEquivalentSourceBoundRetained(run, marker);
        return new(run.Id, SourceBoundBackgroundAdmissionState.Admitted, run.Status, run.Revision);
    }

    internal SourceBoundBackgroundAdmission InspectSourceBoundBackground(string workRequestId,
        ServicePluginPackageBinding package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var run = Store.TryGet(workRequestId);
        if (run is null) return new(workRequestId, SourceBoundBackgroundAdmissionState.Missing, null, null);
        var marker = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).SourceBoundBackground;
        if (marker is null || run.Submission.Caller is not { Kind: EventActorKind.Service } caller ||
            caller.Id != package.Id || marker.Source.PackageId != package.Id ||
            marker.Source.PackageVersion != package.Version ||
            marker.Source.PackageGeneration != package.Generation ||
            marker.WorkRequestId != run.Id || marker.WorkConversationId != run.Submission.ConversationId)
            throw new UnauthorizedAccessException("Request ID belongs to other work.");
        return new(workRequestId, SourceBoundBackgroundAdmissionState.Existing, run.Status, run.Revision);
    }

    private SourceBoundWorkflowBinding CaptureSourceBoundBinding(
        CommittedRootUserInputObservation observation, SourceBoundBackgroundIntent intent,
        ServicePluginPackageBinding package)
    {
        var packageGeneration = package.Generation;
        if (string.IsNullOrWhiteSpace(packageGeneration))
            throw new WorkflowConflictException("The exact source-bound package generation is unavailable.");
        if (servicePlugins is null || !servicePlugins.PackageBindings.Any(current =>
                current.Id == package.Id && current.Version == package.Version &&
                current.Generation == packageGeneration && current.Loaded))
            throw new WorkflowConflictException("The exact source-bound package is unavailable.");
        var source = observation.Source;
        var receipt = Store.ReadFirstCommittedRootUserInput(source.ConversationId)
            ?? throw new WorkflowConflictException("The first committed User input is unavailable.");
        if (!receipt.HasCoherentRootUserEventBinding || receipt.RequestId != source.RequestId ||
            receipt.ConversationId != source.ConversationId || receipt.ProfileId != source.ProfileId ||
            receipt.DefinitionId != source.DefinitionId || receipt.DefinitionVersion != source.DefinitionVersion ||
            receipt.ProjectId != source.ProjectId || receipt.EventId != source.EventId ||
            receipt.EventSequence != source.EventSequence || receipt.UserText != observation.UserText)
            throw new WorkflowConflictException("The input callback differs from the retained first User event.");
        var conversation = Store.GetConversation(source.DefinitionId, source.ConversationId)
            ?? throw new WorkflowConflictException("The source conversation is unavailable.");
        if (conversation.ProfileId != source.ProfileId || conversation.ProjectId != source.ProjectId)
            throw new WorkflowConflictException("The source conversation scope changed.");
        var selected = legacy.Get<AgentProfile>("agent", intent.ProfileId)
            ?? throw new WorkflowConflictException("The selected local profile is unavailable.");
        if (selected.Revision != intent.ExpectedProfileRevision ||
            selected.WorkerId != "local")
            throw new WorkflowConflictException("The selected local profile changed.");
        var preferences = new SettingsPreferencesStore(legacy, () => servicePlugins.SettingsPolicies)
            .GetTransfer(new(package.Id, SettingsPreferenceContract.GlobalScope)).State;
        if (preferences.Revision != intent.ExpectedSettingsRevision ||
            preferences.Active?.SourcePackageVersion != package.Version)
            throw new WorkflowConflictException("The active package settings changed.");
        var facts = new SourceBoundWorkflowSourceFacts(SourceBoundWorkflowInputContract.SchemaVersion,
            configuration.Get().InstallationId, package.Id, package.Version, packageGeneration,
            source.DefinitionId, source.DefinitionVersion, source.ConversationId, source.ProfileId,
            source.RequestId, source.EventId, source.EventSequence,
            Convert.ToHexStringLower(SHA256.HashData(SourceBoundUtf8.GetBytes(receipt.UserText))),
            source.ProjectId, conversation.Title, conversation.TitleRevision,
            preferences.Revision, intent.ProfileId, selected.Revision);
        return new(SourceBoundWorkflowInputContract.SchemaVersion, intent.WorkRequestId,
            intent.WorkConversationId, facts);
    }

    private static void RequireEquivalentSourceBoundRetained(WorkflowRun run,
        SourceBoundWorkflowBinding expected)
    {
        var marker = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).SourceBoundBackground;
        if (marker != expected || run.Id != expected.WorkRequestId ||
            run.Submission.ConversationId != expected.WorkConversationId ||
            run.Submission.Caller is not { Kind: EventActorKind.Service } caller ||
            caller.Id != expected.Source.PackageId ||
            run.Submission.ProfileId != expected.Source.SelectedProfileId ||
            run.Submission.ProjectId != expected.Source.SourceProjectId)
            throw new WorkflowConflictException("The retained request belongs to different source-bound work.");
        RequireSourceBoundPublicProjection(run.Submission, marker);
    }

    private static void RequireEquivalentSourceBoundRetainedIntent(WorkflowRun run,
        CommittedRootUserInputObservation observation, SourceBoundBackgroundIntent intent,
        ServicePluginPackageBinding package)
    {
        var marker = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).SourceBoundBackground;
        if (marker is null || marker.WorkRequestId != intent.WorkRequestId ||
            marker.WorkConversationId != intent.WorkConversationId ||
            marker.Source.PackageId != package.Id || marker.Source.PackageVersion != package.Version ||
            marker.Source.PackageGeneration != package.Generation ||
            marker.Source.SourceConversationId != observation.Source.ConversationId ||
            marker.Source.SourceRequestId != observation.Source.RequestId ||
            marker.Source.SourceEventId != observation.Source.EventId ||
            marker.Source.SourceEventSequence != observation.Source.EventSequence ||
            marker.Source.SourceProfileId != observation.Source.ProfileId ||
            marker.Source.SourceProjectId != observation.Source.ProjectId ||
            marker.Source.SelectedProfileId != intent.ProfileId ||
            marker.Source.SelectedProfileRevision != intent.ExpectedProfileRevision ||
            marker.Source.SettingsRevision != intent.ExpectedSettingsRevision ||
            marker.Source.SourceInputSha256 != Convert.ToHexStringLower(SHA256.HashData(
                SourceBoundUtf8.GetBytes(observation.UserText))) ||
            run.Submission.DefinitionId != intent.DefinitionId ||
            run.Submission.DefinitionVersion != intent.DefinitionVersion ||
            run.Submission.UserText != intent.UserText ||
            run.Submission.StepLimit != intent.StepLimit)
            throw new WorkflowConflictException("Request ID belongs to different source-bound input.");
        RequireSourceBoundPublicProjection(run.Submission, marker);
        var projected = run.Submission.Input.Deserialize<SourceBoundWorkflowInput>(CerebralJson.Options)
            ?? throw new InvalidDataException("Retained source-bound input is missing.");
        if (!JsonElement.DeepEquals(projected.PackageInput, intent.PackageInput))
            throw new WorkflowConflictException("Request ID belongs to different package input.");
    }

    private void RequireSourceBoundCurrentForCommit(SourceBoundWorkflowBinding marker,
        WorkerExecutionSnapshot.Snapshot snapshot)
    {
        if (snapshot.SourceBoundBackground != marker ||
            snapshot.Profile.Id != marker.Source.SelectedProfileId ||
            snapshot.Profile.Revision != marker.Source.SelectedProfileRevision ||
            snapshot.Model is null ||
            snapshot.PackageBindings.Count(package => package.Id == marker.Source.PackageId &&
                package.Version == marker.Source.PackageVersion &&
                package.Generation == marker.Source.PackageGeneration && package.Loaded) != 1)
            throw new WorkflowConflictException("The source-bound execution snapshot changed.");
        var source = marker.Source;
        var receipt = Store.ReadFirstCommittedRootUserInput(source.SourceConversationId)
            ?? throw new WorkflowConflictException("The source event is unavailable before admission.");
        if (!receipt.HasCoherentRootUserEventBinding || receipt.RequestId != source.SourceRequestId ||
            receipt.EventId != source.SourceEventId || receipt.EventSequence != source.SourceEventSequence ||
            receipt.DefinitionId != source.SourceDefinitionId ||
            receipt.DefinitionVersion != source.SourceDefinitionVersion ||
            receipt.ProfileId != source.SourceProfileId || receipt.ProjectId != source.SourceProjectId ||
            Convert.ToHexStringLower(SHA256.HashData(SourceBoundUtf8.GetBytes(receipt.UserText))) != source.SourceInputSha256)
            throw new WorkflowConflictException("The source event changed before admission.");
        var conversation = Store.GetConversation(source.SourceDefinitionId, source.SourceConversationId)
            ?? throw new WorkflowConflictException("The source conversation is unavailable before admission.");
        if (conversation.ProfileId != source.SourceProfileId || conversation.ProjectId != source.SourceProjectId ||
            conversation.Title != source.SourceTitle || conversation.TitleRevision != source.SourceTitleRevision ||
            configuration.Get().InstallationId != source.OwnerBinding ||
            legacy.Get<AgentProfile>("agent", source.SelectedProfileId)?.Revision != source.SelectedProfileRevision ||
            servicePlugins is null ||
            new SettingsPreferencesStore(legacy, () => servicePlugins.SettingsPolicies)
                .GetTransfer(new(source.PackageId, SettingsPreferenceContract.GlobalScope)).State.Revision != source.SettingsRevision)
            throw new WorkflowConflictException("The source scope changed before admission.");
    }

    private static void RequireSourceBoundPublicProjection(WorkflowSubmission submission,
        SourceBoundWorkflowBinding marker)
    {
        static void Exact(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
                value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
                value.EnumerateObject().Any(item => !names.Contains(item.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("Public source-bound input has unexpected or duplicate fields.");
        }
        Exact(submission.Input, "schemaVersion", "source", "packageInput");
        Exact(submission.Input.GetProperty("source"), "schemaVersion", "ownerBinding", "packageId",
            "packageVersion", "packageGeneration", "sourceDefinitionId", "sourceDefinitionVersion",
            "sourceConversationId", "sourceProfileId", "sourceRequestId", "sourceEventId",
            "sourceEventSequence", "sourceInputSha256", "sourceProjectId", "sourceTitle",
            "sourceTitleRevision", "settingsRevision", "selectedProfileId", "selectedProfileRevision");
        SourceBoundWorkflowInput projected;
        try
        {
            projected = submission.Input.Deserialize<SourceBoundWorkflowInput>(new JsonSerializerOptions(CerebralJson.Options)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true,
                RespectNullableAnnotations = true
            }) ?? throw new InvalidDataException("Source-bound public input is missing.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Source-bound public input is malformed.", error);
        }
        if (projected.SchemaVersion != SourceBoundWorkflowInputContract.SchemaVersion ||
            projected.Source != marker.Source || marker.WorkRequestId != submission.RequestId ||
            marker.WorkConversationId != submission.ConversationId)
            throw new UnauthorizedAccessException("Public source facts do not match host-frozen provenance.");
    }

    internal SourceBoundConditionalRenameResult RenameConversationFromSourceEffect(
        WorkflowCommandContext context, SourceBoundConditionalRenameArguments arguments)
    {
        var marker = RequireSourceBoundRenameContext(context, arguments);
        var source = marker.Source;
        // StateStore serializes settings/profile changes through the Engine's
        // guarded SQLite transaction. No package or model callback runs here.
        var result = legacy.Locked(() =>
        {
            if (servicePlugins is null || !servicePlugins.PackageBindings.Any(package =>
                    package.Id == source.PackageId && package.Version == source.PackageVersion &&
                    package.Generation == source.PackageGeneration && package.Loaded) ||
                configuration.Get().InstallationId != source.OwnerBinding ||
                legacy.Get<AgentProfile>("agent", source.SelectedProfileId)?.Revision != source.SelectedProfileRevision ||
                new SettingsPreferencesStore(legacy, () => servicePlugins.SettingsPolicies)
                    .GetTransfer(new(source.PackageId, SettingsPreferenceContract.GlobalScope)).State.Revision != source.SettingsRevision)
                return new SourceBoundConditionalRenameResult(SourceBoundConditionalRenameDisposition.Skipped,
                    SkipCode: "source-scope-changed");
            return Store.RenameConversationFromSource(Guard(marker, arguments.Rename));
        });
        if (result.Disposition == SourceBoundConditionalRenameDisposition.Applied && result.Receipt is { } receipt)
            events.Publish("conversation.updated", receipt.ConversationId, receipt);
        return result;
    }

    internal SourceBoundConditionalRenameResult? ReadSourceBoundRenameEffectReceipt(
        WorkflowCommandContext context, SourceBoundConditionalRenameArguments arguments)
    {
        var marker = RequireSourceBoundRenameContext(context, arguments);
        return Store.ReadSourceBoundConversationRenameReceipt(Guard(marker, arguments.Rename));
    }

    private static SourceBoundWorkflowBinding RequireSourceBoundRenameContext(
        WorkflowCommandContext context, SourceBoundConditionalRenameArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(arguments.Rename);
        var submission = context.Submission;
        var marker = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).SourceBoundBackground
            ?? throw new UnauthorizedAccessException("Host source-bound provenance is absent.");
        RequireSourceBoundPublicProjection(submission, marker);
        if (context.RunId != submission.RequestId ||
            submission.Caller is not { Kind: EventActorKind.Service } caller ||
            caller.Id != marker.Source.PackageId ||
            submission.RequestId != marker.WorkRequestId ||
            submission.ConversationId != marker.WorkConversationId ||
            submission.ProfileId != marker.Source.SelectedProfileId ||
            submission.ProjectId != marker.Source.SourceProjectId ||
            arguments.Rename.DefinitionId != marker.Source.SourceDefinitionId ||
            arguments.Rename.ConversationId != marker.Source.SourceConversationId ||
            arguments.Rename.ExpectedTitleRevision != marker.Source.SourceTitleRevision)
            throw new UnauthorizedAccessException("Conditional rename does not match admitted source work.");
        return marker;
    }

    private static SourceBoundConversationRenameGuard Guard(SourceBoundWorkflowBinding marker,
        ConversationRenameRequest rename)
    {
        var source = marker.Source;
        return new(rename, marker.WorkRequestId, marker.WorkConversationId,
            source.SourceConversationId, source.SourceRequestId, source.SourceEventId,
            source.SourceEventSequence, source.SourceInputSha256,
            source.SourceDefinitionId, source.SourceDefinitionVersion,
            source.SourceProjectId, source.SourceTitle, source.SourceTitleRevision);
    }
}
