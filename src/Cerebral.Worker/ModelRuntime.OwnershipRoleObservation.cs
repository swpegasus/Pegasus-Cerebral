using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

internal enum ModelOwnershipRole { Unclassified, CachedNativeRegistration, TemporaryNativeReadiness }
internal sealed record ModelOwnershipRoleBinding(string EntryId, ModelOwnershipRole Role);
internal sealed record ModelAccountingRoleCapture(ModelAccountingSnapshot Accounting, ModelOwnershipRoleBinding[] Bindings);
internal sealed record ModelOwnershipRoleRegistration(string ModelId, string InstanceId, string RuntimeGenerationId,
    string AccountingGenerationId, bool RegistrationOpen, int ActiveCallbacks, string RegistryAdmission,
    int RegistryReservations, int UnconfirmedCleanupCount, long FenceRevision, bool CaptureStable);
internal sealed record ModelOwnershipRoleMember(string ModelId, string InstanceId, string? CachedRegistrationEntryId,
    bool Current, int Busy, int QuarantinedSlots, ModelOwnershipRoleRegistration? Registration);
internal sealed record ModelOwnershipRoleRuntime(string ProcessGenerationId, long RuntimeEpoch, bool Complete,
    ModelOwnershipRoleMember[] Members);
internal sealed record ModelOwnershipRoleRow(string EntryId, string ModelId, string? InstanceId, string Role,
    string State, bool CurrentInstanceMatch, ModelOwnershipRoleMember? Membership);
internal sealed record ModelOwnershipRolesFacet(string Schema, string State, bool Complete, bool Truncated,
    DateTimeOffset SampledAt, string AccountingGenerationId, long AccountingRevision, string RuntimeGenerationId,
    long RuntimeEpoch, ModelOwnershipRoleRow[] Rows, string[] Reasons,
    bool Atomic = false, bool PhysicalReleaseProof = false);
internal sealed record ModelAccountingRoleObservation(ModelAccountingSnapshot Accounting, ModelOwnershipRolesFacet OwnershipRoles);

// Purpose comes only from an exact accounting Begin site. Neither kind/Ready/null fields nor
// a client-supplied role label can establish cached membership. This facet grants no admission.
internal static class ModelOwnershipRoleProjection
{
    internal const int MaximumEntries = 256;
    internal const int MaximumFacetBytes = 65536;
    internal static ModelOwnershipRolesFacet Build(ModelAccountingRoleCapture captured, ModelAccountingSnapshot after,
        ModelOwnershipRoleRuntime beforeRuntime, ModelOwnershipRoleRuntime afterRuntime, DateTimeOffset sampledAt)
    {
        var snapshot = captured.Accounting;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var stable = snapshot.ProcessGenerationId == after.ProcessGenerationId && snapshot.Revision == after.Revision;
        if (!stable) reasons.Add("accounting_changed_during_capture");
        var runtimeStable = beforeRuntime.Complete && afterRuntime.Complete &&
            beforeRuntime.ProcessGenerationId == afterRuntime.ProcessGenerationId &&
            beforeRuntime.RuntimeEpoch == afterRuntime.RuntimeEpoch &&
            beforeRuntime.Members.SequenceEqual(afterRuntime.Members);
        if (!runtimeStable) reasons.Add("runtime_or_registration_changed_during_capture");
        var truncated = snapshot.Entries.Length > MaximumEntries;
        if (truncated) reasons.Add("entry_details_truncated");
        if (!snapshot.CoverageComplete) reasons.Add("accounting_coverage_incomplete");
        var entryIds = snapshot.Entries.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (entryIds.Count != snapshot.Entries.Length) reasons.Add("accounting_entry_duplicate");
        var roleGroups = captured.Bindings.GroupBy(x => x.EntryId, StringComparer.Ordinal).ToArray();
        if (roleGroups.Any(x => x.Count() != 1)) reasons.Add("role_binding_duplicate");
        if (roleGroups.Any(x => !entryIds.Contains(x.Key))) reasons.Add("role_binding_unjoined");
        if (roleGroups.Any(x => snapshot.Entries.Any(e => e.Id == x.Key && e.Kind != ModelOwnershipKind.CapabilityLease)))
            reasons.Add("role_binding_kind_mismatch");
        var bindings = roleGroups.Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single().Role, StringComparer.Ordinal);
        var rows = new List<ModelOwnershipRoleRow>();
        foreach (var entry in snapshot.Entries.Take(MaximumEntries).Where(x => x.Kind == ModelOwnershipKind.CapabilityLease))
        {
            var role = bindings.GetValueOrDefault(entry.Id);
            var wireRole = role switch
            {
                ModelOwnershipRole.CachedNativeRegistration => "cached-native-registration",
                ModelOwnershipRole.TemporaryNativeReadiness => "temporary-native-readiness",
                _ => "unclassified"
            };
            var members = beforeRuntime.Members.Where(x => x.ModelId == entry.ModelId && x.InstanceId == entry.InstanceId).ToArray();
            var member = members.Length == 1 ? members[0] : null;
            var current = member?.Current == true;
            string state;
            if (wireRole == "unclassified")
            { state = "unclassified"; reasons.Add("capability_role_unclassified"); }
            else if (!stable || !runtimeStable)
                state = "unavailable";
            else if (entry.Phase == ModelOwnershipPhase.Unknown || entry.Uncertainty is not null)
                state = "retained-cleanup";
            else if (!current || entry.Phase != ModelOwnershipPhase.Ready || !entry.EffectStarted ||
                entry.ReservationId is not null || entry.LeaseId is not null || entry.ProcessId is not null)
            { state = "unavailable"; reasons.Add("current_role_membership_unavailable"); }
            else if (role == ModelOwnershipRole.TemporaryNativeReadiness)
                state = "temporary-readiness"; // It remains a blocker even if Busy has just been decremented.
            else if (member!.CachedRegistrationEntryId != entry.Id || member.Registration is not { } registration ||
                registration.ModelId != entry.ModelId || registration.InstanceId != entry.InstanceId ||
                registration.RuntimeGenerationId != beforeRuntime.ProcessGenerationId ||
                registration.AccountingGenerationId != snapshot.ProcessGenerationId || !registration.CaptureStable)
            { state = "unavailable"; reasons.Add("cached_registration_binding_unavailable"); }
            else if (!registration.RegistrationOpen || registration.RegistryAdmission != "Active" ||
                registration.UnconfirmedCleanupCount != 0 || member.QuarantinedSlots != 0)
                state = "retained-cleanup";
            else if (member.Busy != 0 || registration.ActiveCallbacks != 0 || registration.RegistryReservations != 0)
                state = "cached-registration-busy";
            else
                state = "cached-registration-ready";
            rows.Add(new(entry.Id, entry.ModelId, entry.InstanceId, wireRole, state, current, member));
        }
        var complete = reasons.Count == 0;
        return new("cerebral.accounting-ownership-roles/1", !stable || !runtimeStable ? "stale" : complete ? "observed" : "partial",
            complete, truncated, sampledAt, snapshot.ProcessGenerationId, snapshot.Revision,
            beforeRuntime.ProcessGenerationId, beforeRuntime.RuntimeEpoch, rows.ToArray(), reasons.Order(StringComparer.Ordinal).ToArray());
    }

    // Optional facet must not make an otherwise valid base response exceed its established bound.
    // Missing facet is explicitly unavailable to new callers, never an idle/cached assertion.
    internal static CommandResult Attach(CommandResult baseline, ModelOwnershipRolesFacet facet, int maximumResponseBytes)
    {
        if (!baseline.Success || baseline.Data.ValueKind != JsonValueKind.Object) return baseline;
        if (JsonSerializer.SerializeToUtf8Bytes(facet, CerebralJson.Options).Length > MaximumFacetBytes)
            facet = facet with { State = "unavailable", Complete = false, Truncated = true, Rows = [], Reasons = ["facet_byte_bound"] };
        var fields = baseline.Data.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone(), StringComparer.Ordinal);
        fields.Add("ownershipRoles", facet);
        var candidate = CommandResult.Ok(fields);
        return JsonSerializer.SerializeToUtf8Bytes(candidate, CerebralJson.Options).Length <= maximumResponseBytes ? candidate : baseline;
    }
}

public sealed partial class ModelRuntime
{
    // Store -> short owner/accounting/fence/registry observations only. No lifecycle wait,
    // RequireUsable, process/provider access, generation callback, state repair or retirement.
    internal ModelAccountingRoleObservation ObserveAccountingOwnershipRoles()
    {
        var captured = Accounting.SnapshotWithOwnershipRoles();
        var ids = captured.Accounting.Entries.Take(ModelOwnershipRoleProjection.MaximumEntries)
            .Where(x => x.Kind == ModelOwnershipKind.CapabilityLease).Select(x => x.ModelId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var before = CaptureOwnershipRoleRuntime(ids);
        var after = CaptureOwnershipRoleRuntime(ids);
        var accountingAfter = Accounting.Snapshot();
        lock (ownerSnapshotGate)
            if (ownerRuntimeDisposed || ownerRuntimeEpoch != after.RuntimeEpoch)
                after = after with { Complete = false };
        return new(captured.Accounting, ModelOwnershipRoleProjection.Build(captured, accountingAfter, before, after, DateTimeOffset.UtcNow));
    }

    private ModelOwnershipRoleRuntime CaptureOwnershipRoleRuntime(string[] modelIds)
    {
        long epoch; bool valid;
        var references = new List<(string ModelId, Instance Instance, NativeRegistrationLifetime? Registration)>();
        lock (ownerSnapshotGate)
        {
            epoch = ownerRuntimeEpoch; valid = !ownerRuntimeDisposed;
            foreach (var id in modelIds)
                if (instances.TryGetValue(id, out var instance)) references.Add((id, instance, instance.RegisteredText));
        }
        var members = new List<ModelOwnershipRoleMember>();
        foreach (var item in references)
        {
            ModelOwnershipRoleRegistration? registration = null;
            try { registration = item.Registration?.ObserveOwnershipRole(); }
            catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { valid = false; }
            if (registration?.CaptureStable == false || item.Registration is { } cache && !ReferenceEquals(cache.Ownership.Owner, Accounting))
                valid = false;
            var instance = item.Instance;
            members.Add(new(item.ModelId, instance.Id, item.Registration?.Ownership.Id,
                !instance.Stopped && instance.OwnerLaunch is not null,
                Volatile.Read(ref instance.Busy), instance.Slots.Quarantined.Count, registration));
        }
        lock (ownerSnapshotGate)
        {
            valid &= !ownerRuntimeDisposed && ownerRuntimeEpoch == epoch && references.All(x =>
                instances.TryGetValue(x.ModelId, out var current) && ReferenceEquals(current, x.Instance) &&
                ReferenceEquals(current.RegisteredText, x.Registration));
        }
        return new(ownerProcessGenerationId, epoch, valid, members.ToArray());
    }
}
