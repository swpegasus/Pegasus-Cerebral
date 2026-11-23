using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    private WorkerExecutionSnapshot.Snapshot CaptureWorkflowPortableSettings(
        WorkerExecutionSnapshot.Snapshot snapshot, AgentProfile rawProfile)
    {
        if (servicePlugins is null) return snapshot;
        var owners = SelectedWorkflowToolOwners(snapshot);
        var entries = new List<WorkflowApprovedSettingsEntry>();
        foreach (var owner in owners)
        {
            var hasSettings = rawProfile.PluginSettings?.ContainsKey(owner) == true;
            var loaded = servicePlugins.LoadedWorkflowPortableSettingsForPackage(owner);
            var approval = workflowSettingsLifecycle?.ActiveWorkflowSettingsApproval(legacy, owner);
            if (approval is null)
            {
                if (hasSettings)
                    throw new UnauthorizedAccessException("Selected service tool settings have no current owner approval.");
                continue;
            }
            if (loaded is null || loaded.Validator is null ||
                approval.PluginId != loaded.PackageId || approval.PluginVersion != loaded.PackageVersion ||
                approval.LoadedGeneration != loaded.LoadedGeneration ||
                approval.PackageFingerprint != loaded.PackageFingerprint ||
                approval.SchemaFingerprint != loaded.SchemaFingerprint)
                throw new UnauthorizedAccessException("Selected service tool settings approval is unavailable.");

            WorkflowPortableSettingsProjection.Projection projected;
            try
            {
                if (loaded.Validator.WorkflowPortableSettingsValidatorVersion !=
                    ServiceWorkflowPortableSettingsValidatorContract.Version)
                    throw new InvalidOperationException("The selected settings validator version is unsupported.");
                projected = WorkflowPortableSettingsProjection.Project(rawProfile, owner,
                    loaded.Schema, approval.ReviewedFields);
                loaded.Validator.ValidateWorkflowPortableSettings(projected.ReviewedValues.Clone());
            }
            catch (Exception)
            {
                // Plugin exceptions may include values. Do not preserve their messages or inner errors.
                throw new UnauthorizedAccessException("Selected service tool settings failed workflow admission.");
            }
            entries.Add(new WorkflowApprovedSettingsEntry(owner, loaded.PackageVersion,
                loaded.LoadedGeneration, loaded.PackageFingerprint, loaded.SchemaFingerprint,
                approval.Revision, projected.HadProfileEntry, projected.ReviewedValues.Clone()));
        }
        return snapshot with { ApprovedPluginSettings = Array.AsReadOnly(entries.ToArray()) };
    }

    private IReadOnlyList<string> WorkflowPortablePackageRoots(WorkerExecutionSnapshot.Snapshot snapshot)
    {
        var providers = WorkerContextContributionSnapshot.ProviderIds(snapshot.ContextContributions)
            .Select(id => id.Split(':', 2)[0]);
        return Array.AsReadOnly(providers.Concat(SelectedWorkflowToolOwners(snapshot))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private IReadOnlyList<string> SelectedWorkflowToolOwners(WorkerExecutionSnapshot.Snapshot snapshot) =>
        Array.AsReadOnly(snapshot.Tools.Select(tool => servicePlugins?.LoadedWorkflowToolOwner(tool.Name))
            .Where(id => id is not null).Select(id => id!)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());

    private void RecheckWorkflowPortableSettings(WorkerExecutionSnapshot.Snapshot snapshot)
    {
        if (!WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)) return;
        foreach (var entry in snapshot.ApprovedPluginSettings ?? [])
        {
            if (workflowSettingsLifecycle is null || servicePlugins is null)
                throw new UnauthorizedAccessException("Selected service tool settings approval is unavailable.");
            var loaded = servicePlugins.LoadedWorkflowPortableSettingsForPackage(entry.PluginId);
            if (loaded is null || loaded.PackageVersion != entry.PluginVersion ||
                loaded.LoadedGeneration != entry.LoadedGeneration ||
                loaded.PackageFingerprint != entry.PackageFingerprint ||
                loaded.SchemaFingerprint != entry.SchemaFingerprint)
                throw new UnauthorizedAccessException("Selected service tool settings package changed before admission.");
            workflowSettingsLifecycle.RequireWorkflowSettingsApproval(legacy, entry.PluginId,
                entry.LoadedGeneration, entry.PackageFingerprint, entry.SchemaFingerprint,
                entry.ApprovalRevision);
        }
    }
}
