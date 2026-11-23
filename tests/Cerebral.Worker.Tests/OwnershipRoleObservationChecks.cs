using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

// Authored deterministic source fixtures. No StateStore, runtime, provider, process or filesystem construction.
internal static class OwnershipRoleObservationChecks
{
    internal static string[] Run()
    {
        var labels = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); labels.Add(label); }
        const string model = "fixture-model", instance = "fixture-instance", account = "fixture-accounting", runtime = "fixture-runtime";
        var capability = new ModelOwnershipEntry("fixture-capability", ModelOwnershipKind.CapabilityLease, ModelOwnershipPhase.Ready,
            model, instance, null, null, null, null, true, null);
        var context = capability with { Id = "fixture-context", Kind = ModelOwnershipKind.LocalContext, ProcessId = 123 };
        var snapshot = new ModelAccountingSnapshot(1, account, 7, true, "fixture", true, "fixture-current-owner",
            1, 0, 0, 0, 0, 0, [context, capability]);
        var registration = new ModelOwnershipRoleRegistration(model, instance, runtime, account, true, 0, "Active", 0, 0, 1, true);
        var member = new ModelOwnershipRoleMember(model, instance, capability.Id, true, 0, 0, registration);
        var live = new ModelOwnershipRoleRuntime(runtime, 5, true, [member]);
        var capture = new ModelAccountingRoleCapture(snapshot, [new(capability.Id, ModelOwnershipRole.CachedNativeRegistration)]);
        ModelOwnershipRolesFacet Project(ModelAccountingRoleCapture? input = null, ModelAccountingSnapshot? after = null,
            ModelOwnershipRoleRuntime? first = null, ModelOwnershipRoleRuntime? last = null) =>
            ModelOwnershipRoleProjection.Build(input ?? capture, after ?? snapshot, first ?? live, last ?? live, DateTimeOffset.UnixEpoch);
        var cached = Project();
        Check(cached.Complete && cached.Rows.Single().State == "cached-registration-ready", "exact_cached_registration_membership_observed");
        var temporaryInput = capture with { Bindings = [new(capability.Id, ModelOwnershipRole.TemporaryNativeReadiness)] };
        var temporary = Project(temporaryInput);
        Check(JsonSerializer.Serialize(capture.Accounting, CerebralJson.Options) == JsonSerializer.Serialize(temporaryInput.Accounting, CerebralJson.Options) &&
            temporary.Rows.Single().State == "temporary-readiness", "identical_base_shape_distinguished_only_by_exact_creation_provenance");
        Check(temporary.Rows.Single().State != "cached-registration-ready", "temporary_ready_pin_with_zero_work_counts_never_cached");
        var unknown = snapshot with { UnknownOwnership = 1, Entries = [context, capability with { Phase = ModelOwnershipPhase.Unknown, Uncertainty = "retirement-pending" }] };
        Check(Project(capture with { Accounting = unknown }, unknown).Rows.Single().State == "retained-cleanup", "unknown_retirement_keeps_origin_without_residency_or_release_credit");
        var closed = live with { Members = [member with { Registration = registration with { RegistrationOpen = false } }] };
        Check(Project(first: closed, last: closed).Rows.Single().State == "retained-cleanup", "closed_fence_blocks_even_before_accounting_unknown_translation");
        foreach (var state in new[] { "Draining", "Retiring", "RegistryRetired", "CleanupFaulted" })
        {
            var view = live with { Members = [member with { Registration = registration with { RegistryAdmission = state } }] };
            Check(Project(first: view, last: view).Rows.Single().State == "retained-cleanup", "registry_" + state + "_is_not_cached_ready");
        }
        var busy = live with { Members = [member with { Busy = 1 }] };
        Check(Project(first: busy, last: busy).Rows.Single().State == "cached-registration-busy", "busy_pin_not_hidden_by_cached_role");
        var reserved = live with { Members = [member with { Registration = registration with { RegistryReservations = 1 } }] };
        Check(Project(first: reserved, last: reserved).Rows.Single().State == "cached-registration-busy", "registry_reservation_not_idle");
        var faulted = live with { Members = [member with { Registration = registration with { UnconfirmedCleanupCount = 1 } }] };
        Check(Project(first: faulted, last: faulted).Rows.Single().State == "retained-cleanup", "unconfirmed_registry_cleanup_blocks");
        var replaced = live with { RuntimeEpoch = 6, Members = [member with { InstanceId = "replacement" }] };
        Check(!Project(last: replaced).Complete && Project(last: replaced).State == "stale", "runtime_replacement_during_capture_is_stale");
        Check(!Project(after: snapshot with { Revision = 8 }).Complete, "accounting_revision_capture_race_is_incomplete");
        Check(!Project(after: snapshot with { ProcessGenerationId = "replacement-accounting" }).Complete, "accounting_generation_capture_race_is_incomplete");
        var aba = live with { Members = [member with { Registration = registration with { FenceRevision = 3 } }] };
        Check(!Project(last: aba).Complete, "callback_aba_same_counts_new_revision_is_stale");
        var unstable = live with { Members = [member with { Registration = registration with { CaptureStable = false } }], Complete = false };
        Check(!Project(first: unstable, last: unstable).Complete, "registry_race_inside_service_observation_is_incomplete");
        var detached = live with { Members = [member with { CachedRegistrationEntryId = "other-entry" }] };
        Check(!Project(first: detached, last: detached).Complete, "same_model_instance_but_wrong_cached_entry_rejected");
        var foreign = live with { Members = [member with { Registration = registration with { AccountingGenerationId = "foreign" } }] };
        Check(!Project(first: foreign, last: foreign).Complete, "cached_service_accounting_generation_must_join");
        Check(!Project(capture with { Bindings = [] }).Complete, "untagged_ready_capability_stays_unclassified");
        Check(!Project(capture with { Bindings = [capture.Bindings[0], capture.Bindings[0]] }).Complete, "duplicate_role_binding_refused");
        Check(!Project(capture with { Bindings = [new("orphan", ModelOwnershipRole.CachedNativeRegistration)] }).Complete, "orphan_role_binding_refused");
        Check(!Project(first: live with { Members = [] }, last: live with { Members = [] }).Complete, "missing_current_instance_not_promoted");
        var many = snapshot with { Entries = Enumerable.Range(0, 257).Select(i => capability with { Id = "cap-" + i }).ToArray() };
        var bounded = Project(new(many, []), many);
        Check(bounded.Truncated && !bounded.Complete && bounded.Rows.Length == 256, "detail_bound_is_visible_and_fail_closed");
        var baseline = CommandResult.Ok(new { accounting = snapshot, detailComplete = true });
        var attached = ModelOwnershipRoleProjection.Attach(baseline, cached, 256 * 1024);
        Check(attached.Data.TryGetProperty("ownershipRoles", out var facet) && facet.GetProperty("accountingRevision").GetInt64() == snapshot.Revision,
            "facet_attaches_to_exact_returned_accounting_revision");
        Check(attached.Data.GetProperty("accounting").GetRawText() == baseline.Data.GetProperty("accounting").GetRawText(), "base_entry_wire_unchanged");
        Check(ModelOwnershipRoleProjection.Attach(baseline, cached, 1).Data.GetRawText() == baseline.Data.GetRawText(), "optional_facet_cannot_break_base_byte_budget");
        // Isolate the facet-size branch from aggregate-response fallback and admission semantics.
        var originalBaselineWire = baseline.Data.GetRawText();
        var oversized = cached with
        {
            Rows = Enumerable.Range(1, 256).Select(i => cached.Rows.Single() with { EntryId = i.ToString("x32") }).ToArray()
        };
        Check(JsonSerializer.SerializeToUtf8Bytes(oversized, CerebralJson.Options).Length > ModelOwnershipRoleProjection.MaximumFacetBytes,
            "direct_facet_fixture_exceeds_64k_before_attachment");
        var collapsed = ModelOwnershipRoleProjection.Attach(baseline, oversized, 1024 * 1024);
        Check(collapsed.Data.TryGetProperty("ownershipRoles", out var collapsedFacet) &&
            collapsedFacet.GetProperty("state").GetString() == "unavailable" &&
            !collapsedFacet.GetProperty("complete").GetBoolean() && collapsedFacet.GetProperty("truncated").GetBoolean() &&
            collapsedFacet.GetProperty("rows").GetArrayLength() == 0 &&
            collapsedFacet.GetProperty("reasons").EnumerateArray().Any(x => x.GetString() == "facet_byte_bound"),
            "direct_oversized_facet_collapses_to_explicit_unavailable");
        Check(collapsed.Data.GetProperty("accounting").GetRawText() == baseline.Data.GetProperty("accounting").GetRawText() &&
            baseline.Data.GetRawText() == originalBaselineWire, "facet_collapse_preserves_returned_and_input_baseline");
        Check(!cached.Atomic && !cached.PhysicalReleaseProof, "no_atomic_or_physical_kv_release_claim");
        var owner = new ModelExecutionAccounting(coverageComplete: true);
        var tagged = owner.Begin(ModelOwnershipKind.CapabilityLease, model, false, ModelOwnershipRole.CachedNativeRegistration);
        tagged.Bind(instanceId: instance); tagged.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
        Check(owner.SnapshotWithOwnershipRoles().Bindings.Single().EntryId == tagged.Id, "purpose_assigned_to_exact_live_entry");
        tagged.Unknown("fixture-retained"); tagged.Dispose();
        Check(owner.SnapshotWithOwnershipRoles().Bindings.Single().EntryId == tagged.Id && owner.Snapshot().Entries.Single().Phase == ModelOwnershipPhase.Unknown,
            "uncertain_cleanup_retains_role_until_confirmed_accounting_retirement");
        tagged.Complete();
        Check(owner.SnapshotWithOwnershipRoles().Bindings.Length == 0 && owner.Snapshot().Entries.Length == 0, "confirmed_retirement_removes_role_without_history_marker");
        var noEffect = owner.Begin(ModelOwnershipKind.CapabilityLease, model, false, ModelOwnershipRole.TemporaryNativeReadiness);
        noEffect.Dispose();
        Check(owner.SnapshotWithOwnershipRoles().Bindings.Length == 0, "no_effect_scope_exit_does_not_leak_role");
        var fence = new NativeRegistrationFence(); var first = fence.SnapshotVersioned();
        using (fence.Enter()) { }
        var last = fence.SnapshotVersioned();
        Check(first.Open == last.Open && first.Callbacks == last.Callbacks && first.Revision != last.Revision, "fence_tracks_callback_aba_without_provider_work");
        Check(JsonSerializer.Serialize(baseline.Data.GetProperty("accounting").GetProperty("entries"), CerebralJson.Options).Contains("ownershipRole", StringComparison.Ordinal) == false,
            "no_role_fields_added_to_base_entry_dto");
        return labels.ToArray();
    }
}
