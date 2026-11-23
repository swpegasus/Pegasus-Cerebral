using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private AgentProfile PrepareWorkflowPortableToolProfile(
        WorkerExecutionSnapshot.Snapshot snapshot, AgentProfile effective, string toolName)
    {
        // Edition five never acquired workflow plugin settings. Keep its historical empty context.
        if (!WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)) return effective;
        if (workflowSettingsRuntime is null)
        {
            if ((snapshot.ApprovedPluginSettings?.Count ?? 0) != 0)
                throw new AdmissionException("The accepted service tool settings are unavailable.");
            return effective;
        }
        var owner = workflowSettingsRuntime.LoadedWorkflowToolOwner(toolName);
        if (owner is null) return effective;
        var accepted = snapshot.ApprovedPluginSettings?.SingleOrDefault(entry => entry.PluginId == owner);
        if (accepted is null)
        {
            // A defaults-only acceptance cannot silently consume settings added afterward.
            var rawProfile = store.Get<AgentProfile>("agent", snapshot.Profile.Id)
                ?? throw new AdmissionException("The selected service tool profile is unavailable.");
            if (rawProfile.PluginSettings?.ContainsKey(owner) == true)
                throw new AdmissionException("Service tool settings changed after workflow acceptance.");
            return effective;
        }

        var loaded = workflowSettingsRuntime.LoadedWorkflowPortableSettingsForPackage(owner);
        var approval = workflowSettingsLifecycle?.ActiveWorkflowSettingsApproval(store, owner);
        if (loaded is null || approval is null || loaded.Validator is null ||
            loaded.PackageVersion != accepted.PluginVersion ||
            loaded.LoadedGeneration != accepted.LoadedGeneration ||
            loaded.PackageFingerprint != accepted.PackageFingerprint ||
            loaded.SchemaFingerprint != accepted.SchemaFingerprint ||
            approval.Revision != accepted.ApprovalRevision ||
            approval.PluginVersion != accepted.PluginVersion ||
            approval.LoadedGeneration != accepted.LoadedGeneration ||
            approval.PackageFingerprint != accepted.PackageFingerprint ||
            approval.SchemaFingerprint != accepted.SchemaFingerprint)
            throw new AdmissionException("The accepted service tool settings approval changed.");
        try
        {
            if (loaded.Validator.WorkflowPortableSettingsValidatorVersion !=
                ServiceWorkflowPortableSettingsValidatorContract.Version)
                throw new AdmissionException("The selected settings validator version is unsupported.");
            workflowSettingsLifecycle!.RequireWorkflowSettingsApproval(store, owner,
                accepted.LoadedGeneration, accepted.PackageFingerprint, accepted.SchemaFingerprint,
                accepted.ApprovalRevision);
            loaded.Validator.ValidateWorkflowPortableSettings(accepted.ReviewedValues.Clone());
            // Read the persisted source after discovery and approval recheck, as close as possible
            // to dispatch. The copied accepted-only context cannot change after this comparison.
            var rawProfile = store.Get<AgentProfile>("agent", snapshot.Profile.Id)
                ?? throw new AdmissionException("The selected service tool profile is unavailable.");
            var projected = WorkflowPortableSettingsProjection.Project(rawProfile, owner,
                loaded.Schema, approval.ReviewedFields);
            loaded.Validator.ValidateWorkflowPortableSettings(projected.ReviewedValues.Clone());
            if (!WorkflowPortableSettingsProjection.Same(
                new(accepted.HadProfileEntry, accepted.ReviewedValues), projected))
                throw new AdmissionException("Service tool settings changed after workflow acceptance.");
        }
        catch (Exception)
        {
            // Neither validator exceptions nor rejected values are safe to include in a tool receipt.
            throw new AdmissionException("Service tool settings changed or failed validation.");
        }

        var selected = CerebralJson.Read<AgentProfile>(CerebralJson.Element(effective));
        if (accepted.HadProfileEntry)
            selected.PluginSettings[owner] = accepted.ReviewedValues.Clone();
        return selected;
    }

    // Reconciliation is an exact receipt lookup for the already accepted effect. It never
    // dispatches a new tool action, so later profile/approval edits must not hide its receipt.
    private AgentProfile PrepareWorkflowPortableReconcileProfile(
        WorkerExecutionSnapshot.Snapshot snapshot, AgentProfile effective, string toolName,
        ToolExecutionEvidence? previousEvidence)
    {
        if (!WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)) return effective;
        if (workflowSettingsRuntime is null)
        {
            if ((snapshot.ApprovedPluginSettings?.Count ?? 0) != 0)
                throw new AdmissionException("The accepted service tool package is unavailable for reconciliation.");
            return effective;
        }
        var owner = workflowSettingsRuntime.LoadedWorkflowToolOwner(toolName);
        if (owner is null) return effective;
        var accepted = snapshot.ApprovedPluginSettings?.SingleOrDefault(entry => entry.PluginId == owner);
        if (accepted is null) return effective;
        if (previousEvidence is null)
            throw new AdmissionException("The accepted service tool receipt evidence is unavailable.");
        var loaded = workflowSettingsRuntime.LoadedWorkflowPortableSettingsForPackage(owner);
        if (loaded is null || loaded.Validator is null ||
            loaded.PackageVersion != accepted.PluginVersion ||
            loaded.LoadedGeneration != accepted.LoadedGeneration ||
            loaded.PackageFingerprint != accepted.PackageFingerprint ||
            loaded.SchemaFingerprint != accepted.SchemaFingerprint)
            throw new AdmissionException("The accepted service tool package changed before reconciliation.");
        var selected = CerebralJson.Read<AgentProfile>(CerebralJson.Element(effective));
        if (accepted.HadProfileEntry)
            selected.PluginSettings[owner] = accepted.ReviewedValues.Clone();
        return selected;
    }
}
