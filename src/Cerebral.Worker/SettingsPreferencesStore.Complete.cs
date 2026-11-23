using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

// This sidecar is private database/preset state. It is never accepted from a settings file or returned by v2 get/save.
internal sealed record PluginSettingsCompleteCache(string SourceDigest, PluginSettingsCompleteProjection Projection);
internal sealed record PluginSettingsTransferSnapshot(PluginSettingsTransferState Transfer,
    IReadOnlyList<PluginSettingsCompleteCache> CompleteProjections);

internal sealed partial class SettingsPreferencesStore
{
    private sealed record PluginTransferMetadata(int SchemaVersion,
        IReadOnlyList<PluginSettingsPendingPurpose> PendingPurposes,
        IReadOnlyList<PluginSettingsCompleteCache> CompleteProjections);

    internal PluginSettingsTransferState GetTransfer(PluginSettingsCoordinate coordinate)
    {
        ValidateCoordinate(coordinate);
        return store.Locked(() => GetTransferCore(coordinate));
    }

    internal PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request, bool owner = true) =>
        SaveTransferCore(request, owner, legacy: false);

    private PluginSettingsTransferSaveResult SaveTransferCore(PluginSettingsTransferSaveRequest request, bool owner, bool legacy)
    {
        RequireOwner(owner);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Save);
        var save = request.Save;
        var incoming = FreezeDocument(save.Document, legacy: false);
        if (save.ExpectedRevision < 0 || save.OmittedPortableFields is < 0 or > 1_000_000)
            throw new ArgumentException("Plugin settings revision or projection is invalid.");
        // A declaration is checked independently of whether a live service policy supersedes it.
        var suppliedComplete = request.CompleteProjection is null ? null :
            FreezeCompleteProjection(incoming, request.CompleteProjection);
        return store.Locked<PluginSettingsTransferSaveResult>(() =>
        {
            var captured = GetTransferSnapshotCore(incoming.Coordinate);
            var current = captured.Transfer;
            if (legacy) RequireLegacy(current);
            if (incoming.Scope == SettingsPreferenceContract.AgentScope &&
                store.GetSettingsAgentProfile(incoming.ScopeId!) is null)
                throw new InvalidOperationException("The agent settings scope is unavailable.");
            if (current.State.Revision != save.ExpectedRevision)
                throw new InvalidOperationException("Plugin settings changed; reload before saving.");
            var selected = save.PendingId is null ? null :
                current.State.Pending.FirstOrDefault(item => item.Id == save.PendingId);
            if (save.PendingId is not null && selected is null)
                throw new InvalidOperationException("The selected deferred settings changed; reload before saving.");
            var purpose = selected is null ? SettingsTransferPurpose.Local : Purpose(selected, PurposeMap(current));
            if (request.CompleteAdmitted && (legacy || selected is null ||
                purpose != SettingsTransferPurpose.Complete || !save.OwnerValidated))
                throw new ArgumentException("Complete admission reporting requires an owner-validated version-2 selected complete input.");
            PluginSettingsState proposed;
            PluginSettingsCompleteProjection? complete = null;
            var binding = Binding(incoming.PluginId);
            if (binding is not null)
            {
                PluginSettingsDocument? active;
                PluginSettingsProjection? portable;
                var accepted = purpose == SettingsTransferPurpose.Complete
                    ? TryAdmitComplete(binding, incoming, current.State.Active?.Values, out active, out portable, out complete)
                    : TryAdmit(binding, incoming, current.State.Active?.Values, purpose == SettingsTransferPurpose.Portable,
                        out active, out portable);
                if (!accepted)
                    throw new InvalidOperationException("Plugin settings policy rejected the settings.");
                proposed = current.State with { Active = active, ValidationAuthority = "service-policy",
                    PortableValues = portable!.Values, OmittedPortableFields = portable.OmittedFields };
                complete ??= TryProjectComplete(binding, active!);
            }
            else
            {
                if (!save.OwnerValidated)
                    throw new InvalidOperationException("An available package policy or explicit owner-client validation is required.");
                if (purpose == SettingsTransferPurpose.Complete && !request.CompleteAdmitted)
                    throw new InvalidOperationException("Complete deferred settings require explicit complete package admission.");
                proposed = current.State with { Active = incoming, ValidationAuthority = "owner-client",
                    PortableValues = save.PortableValues is { } values ? FreezeValues(values, ValuesLimit(incoming.Coordinate)) : null,
                    OmittedPortableFields = save.OmittedPortableFields };
                complete = suppliedComplete;
            }
            var pending = save.PendingId is null ? current.State.Pending :
                current.State.Pending.Where(item => item.Id != save.PendingId).ToArray();
            proposed = proposed with { Pending = pending, PendingReason = PendingReason(pending) };
            var transfer = FreezeTransfer(new(proposed,
                current.PendingPurposes.Where(item => pending.Any(value => value.Id == item.PendingId)).ToArray()));
            var caches = MatchingCaches(transfer, captured.CompleteProjections).ToList();
            if (complete is not null)
                PutCache(caches, new(SourceDigest(transfer.State.Active!), complete));
            var candidate = FitSnapshotToSavedBudget(FreezeSnapshot(new(transfer, caches)));
            if (SameTransfer(current, transfer) && SameCaches(captured.CompleteProjections, candidate.CompleteProjections))
                return new(current, false);
            transfer = transfer with { State = transfer.State with { Revision = checked(current.State.Revision + 1) } };
            candidate = FitSnapshotToSavedBudget(candidate with { Transfer = transfer });
            if (SameTransfer(current, transfer) && SameCaches(captured.CompleteProjections, candidate.CompleteProjections))
                return new(current, false);
            CommitTransferStates([transfer], projectionSnapshots: [candidate]);
            return new(FreezeTransfer(transfer), true);
        });
    }

    internal IReadOnlyList<PluginSettingsTransferState> AllTransfers(bool includeLegacy = true) =>
        store.Locked(() => AllTransfersCore(includeLegacy));

    internal IReadOnlyList<PluginSettingsTransferSnapshot> CaptureTransferSnapshots(bool includeLegacy = true) =>
        store.Locked(() => AllTransferSnapshotsCore(includeLegacy));

    internal static PluginSettingsTransferSnapshot ValidateSnapshotTransfer(PluginSettingsTransferSnapshot snapshot) =>
        FreezeSnapshot(snapshot);

    internal void ValidateTransferRestoreCapacity(IEnumerable<PluginSettingsTransferState> states, IEnumerable<InterfaceLayoutState> layouts,
        IEnumerable<PluginSettingsCoordinate>? candidateAgentCoordinates = null) =>
        ValidateRestoreCoordinates(states.Select(FreezeTransfer).Select(transfer => transfer.State.Coordinate).ToArray(),
            layouts.Select(FreezeLayout).ToArray(), candidateAgentCoordinates);

    internal PluginSettingsTransferState PlanCompleteIncoming(PluginSettingsDocument document,
        PluginSettingsTransferState? planned = null) => PlanCompleteIncoming(document, planned, out _);

    internal PluginSettingsTransferState PlanCompleteIncoming(PluginSettingsDocument document,
        PluginSettingsTransferState? planned, out bool admitted) =>
        PlanTransferIncoming(document, SettingsTransferPurpose.Complete, planned, out admitted);

    internal PluginSettingsTransferState PlanCompletePendingIncoming(PluginSettingsDocument document,
        PluginSettingsTransferState? planned = null) =>
        PlanTransferPendingIncoming(document, SettingsTransferPurpose.Complete, planned: planned);

    internal PluginSettingsTransferState PlanTransferIncoming(PluginSettingsDocument document, string purpose,
        PluginSettingsTransferState? planned = null) => PlanTransferIncoming(document, purpose, planned, out _);

    internal PluginSettingsTransferState PlanTransferIncoming(PluginSettingsDocument document, string purpose,
        PluginSettingsTransferState? planned, out bool admitted)
    {
        ValidatePurpose(purpose);
        var incoming = FreezeDocument(document, legacy: purpose != SettingsTransferPurpose.Complete);
        var outcome = store.Locked(() =>
        {
            var current = PlanningTransfer(incoming.Coordinate, planned);
            SettingsPolicyBinding? binding;
            try { binding = Binding(incoming.PluginId); }
            catch (InvalidOperationException)
            { return (State: AppendTransferPending(current, incoming, purpose, "policy-unavailable"), Admitted: false); }
            PluginSettingsDocument? active = null;
            PluginSettingsProjection? portable = null;
            var accepted = binding is not null && (purpose == SettingsTransferPurpose.Complete
                ? TryAdmitComplete(binding, incoming, current.State.Active?.Values, out active, out portable, out _)
                : TryAdmit(binding, incoming, current.State.Active?.Values, purpose == SettingsTransferPurpose.Portable,
                    out active, out portable));
            if (accepted)
                return (State: FreezeTransfer(current with { State = current.State with { Active = active,
                    ValidationAuthority = "service-policy", PortableValues = portable!.Values,
                    OmittedPortableFields = portable.OmittedFields } }), Admitted: true);
            return (State: AppendTransferPending(current, incoming, purpose,
                binding is null || purpose == SettingsTransferPurpose.Complete && binding.Policy is not IPluginCompleteSettingsPolicy
                    ? "policy-unavailable" : "policy-rejected"), Admitted: false);
        });
        admitted = outcome.Admitted;
        return outcome.State;
    }

    internal PluginSettingsTransferState PlanTransferPendingIncoming(PluginSettingsDocument document, string purpose,
        string reason = "retained-pending", PluginSettingsTransferState? planned = null)
    {
        ValidatePurpose(purpose);
        var incoming = FreezeDocument(document, legacy: purpose != SettingsTransferPurpose.Complete);
        return store.Locked(() => AppendTransferPending(PlanningTransfer(incoming.Coordinate, planned), incoming, purpose,
            reason is "policy-unavailable" or "policy-rejected" ? reason : "retained-pending"));
    }

    internal void CommitTransferStates(IEnumerable<PluginSettingsTransferState> states, bool updateAgentBacking = true,
        IEnumerable<PluginSettingsTransferSnapshot>? projectionSnapshots = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        var proposed = states.Select(FreezeTransfer).ToArray();
        ValidateUnique(proposed.Select(transfer => Key(transfer.State.Coordinate)), SettingsPreferenceContract.MaximumDocuments);
        var captured = projectionSnapshots?.Select(FreezeSnapshot).ToArray() ?? [];
        ValidateUnique(captured.Select(snapshot => Key(snapshot.Transfer.State.Coordinate)), SettingsPreferenceContract.MaximumDocuments);
        store.Locked(() =>
        {
            var existing = AllPluginsCore().Select(state => Key(state.Coordinate)).ToHashSet(StringComparer.Ordinal);
            foreach (var transfer in proposed) existing.Add(Key(transfer.State.Coordinate));
            if (existing.Count > SettingsPreferenceContract.MaximumDocuments)
                throw new InvalidOperationException("Plugin settings document capacity is full.");
            var records = proposed.Select(transfer => BuildTransferRecord(transfer,
                captured.FirstOrDefault(snapshot => snapshot.Transfer.State.Coordinate == transfer.State.Coordinate))).ToArray();
            // Budget includes state, purposes and all projections. No separate record can silently evade the old bound.
            foreach (var record in records) ValidateSavedSize(record, MaximumSavedBytes);
            foreach (var state in proposed.Select(transfer => transfer.State)
                .Where(state => state.Coordinate.Scope == SettingsPreferenceContract.GlobalScope))
            {
                var current = GetPluginCore(state.Coordinate);
                if (state.Revision != checked(current.Revision + 1))
                    throw new InvalidOperationException("Plugin settings changed during commit.");
            }
            foreach (var group in proposed.Select(transfer => transfer.State)
                .Where(state => state.Coordinate.Scope == SettingsPreferenceContract.AgentScope)
                .GroupBy(state => state.Coordinate.ScopeId!, StringComparer.Ordinal))
            {
                var profile = store.GetSettingsAgentProfile(group.Key) ??
                    throw new InvalidOperationException("The agent settings scope is unavailable.");
                var revision = updateAgentBacking ? checked(profile.Revision + 1) : profile.Revision;
                if (group.Any(state => state.Revision != revision))
                    throw new InvalidOperationException("Agent settings changed during commit.");
                foreach (var state in group)
                {
                    if (updateAgentBacking)
                    {
                        if (state.Active is { } active) profile.PluginSettings[state.Coordinate.PluginId] = active.Values.Clone();
                        else profile.PluginSettings.Remove(state.Coordinate.PluginId);
                    }
                    else
                    {
                        var has = profile.PluginSettings.TryGetValue(state.Coordinate.PluginId, out var actual);
                        if (has != (state.Active is not null) || has && !Equal(actual, state.Active!.Values))
                            throw new InvalidOperationException("Planned agent settings do not match the committed profile.");
                    }
                }
                if (profile.PluginSettings.Count > 32)
                    throw new InvalidOperationException("Agent plugin settings capacity is full.");
                foreach (var value in profile.PluginSettings.Values) Configuration.ValidatePluginSettingsValue(value);
                if (updateAgentBacking)
                {
                    profile.Revision = revision;
                    ValidateSavedSize(profile, 4 * 1024 * 1024);
                    store.Put("agent", profile.Id, profile);
                }
            }
            foreach (var record in records) store.Put(PluginKind, Key(record.State.Coordinate), record);
            return true;
        });
    }

    // An exact frozen sidecar takes priority. Otherwise only a supported pure policy can cover the document.
    // A purpose label, an owner-client authority string or raw values alone never constitute a projection.
    internal PluginSettingsCompleteProjection? ResolveCompleteProjection(PluginSettingsDocument document,
        PluginSettingsTransferSnapshot? snapshot = null)
    {
        var source = FreezeDocument(document, legacy: true);
        return store.Locked(() =>
        {
            var captured = snapshot is null ? GetTransferSnapshotCore(source.Coordinate) : FreezeSnapshot(snapshot);
            if (captured.Transfer.State.Coordinate != source.Coordinate)
                throw new ArgumentException("Complete projection snapshot coordinate is invalid.");
            var cached = captured.CompleteProjections.FirstOrDefault(item => item.SourceDigest == SourceDigest(source));
            if (cached is not null) return FreezeCompleteProjection(source, cached.Projection);
            SettingsPolicyBinding? binding;
            try { binding = Binding(source.PluginId); }
            catch (InvalidOperationException) { return null; }
            return binding is null ? null : TryProjectComplete(binding, source);
        });
    }

    private PluginSettingsTransferState GetTransferCore(PluginSettingsCoordinate coordinate) =>
        GetTransferSnapshotCore(coordinate).Transfer;

    private PluginSettingsTransferSnapshot GetTransferSnapshotCore(PluginSettingsCoordinate coordinate)
    {
        var raw = store.ReadSettingsRecord(PluginKind, Key(coordinate), MaximumSavedBytes);
        PluginSettingsTransferSnapshot snapshot;
        if (raw is null) snapshot = new(LegacyTransfer(new(coordinate, 0, null, [], "none")), []);
        else
        {
            var record = ReadSaved<PluginRecord>(raw.Value);
            if (record.SchemaVersion != 1 || record.State is null || record.State.Coordinate != coordinate)
                throw new InvalidDataException("Saved plugin settings envelope is invalid.");
            snapshot = ReadTransferRecord(record);
        }
        var state = snapshot.Transfer.State;
        if (coordinate.Scope == SettingsPreferenceContract.GlobalScope) return snapshot;
        var profile = store.GetSettingsAgentProfile(coordinate.ScopeId!);
        if (profile is null)
        {
            if (raw is not null) throw new InvalidDataException("Saved agent settings have no backing profile.");
            return snapshot;
        }
        if (state.Revision > profile.Revision)
            throw new InvalidDataException("Saved agent settings revision exceeds its profile.");
        var has = profile.PluginSettings.TryGetValue(coordinate.PluginId, out var actual);
        if (!has) state = state with { Revision = profile.Revision, Active = null,
            ValidationAuthority = "none", PortableValues = null, OmittedPortableFields = 0 };
        else if (state.Active is null || !Equal(state.Active.Values, actual))
            state = state with { Revision = profile.Revision,
                Active = new(coordinate.PluginId, coordinate.Scope, coordinate.ScopeId, null, null,
                    FreezeValues(actual, SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes)),
                ValidationAuthority = "unversioned", PortableValues = null, OmittedPortableFields = 0 };
        else state = state with { Revision = profile.Revision };
        var transfer = FreezeTransfer(snapshot.Transfer with { State = state });
        // Independent legacy writes invalidate only cache bodies which no longer match the backing profile.
        return FreezeSnapshot(new(transfer, MatchingCaches(transfer, snapshot.CompleteProjections)));
    }

    private IReadOnlyList<PluginSettingsTransferState> AllTransfersCore(bool includeLegacy) =>
        AllTransferSnapshotsCore(includeLegacy).Select(snapshot => snapshot.Transfer).ToArray();

    private IReadOnlyList<PluginSettingsTransferSnapshot> AllTransferSnapshotsCore(bool includeLegacy)
    {
        var coordinates = new Dictionary<string, PluginSettingsCoordinate>(StringComparer.Ordinal);
        foreach (var id in store.ListSettingsRecordIds(PluginKind, SettingsPreferenceContract.MaximumDocuments))
        {
            var raw = store.ReadSettingsRecord(PluginKind, id, MaximumSavedBytes) ??
                throw new InvalidDataException("Saved plugin settings changed during capture.");
            var record = ReadSaved<PluginRecord>(raw);
            if (record.SchemaVersion != 1 || record.State is null)
                throw new InvalidDataException("Saved plugin settings envelope is invalid.");
            var snapshot = ReadTransferRecord(record);
            if (Key(snapshot.Transfer.State.Coordinate) != id)
                throw new InvalidDataException("Saved plugin settings coordinate is invalid.");
            coordinates.Add(id, snapshot.Transfer.State.Coordinate);
        }
        foreach (var profile in includeLegacy ? store.SettingsAgentProfiles() : [])
        foreach (var plugin in profile.PluginSettings.Keys)
        {
            var coordinate = new PluginSettingsCoordinate(plugin, SettingsPreferenceContract.AgentScope, profile.Id);
            coordinates.TryAdd(Key(coordinate), coordinate);
            if (coordinates.Count > SettingsPreferenceContract.MaximumDocuments)
                throw new InvalidDataException("Saved plugin settings exceed the document bound.");
        }
        return coordinates.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => GetTransferSnapshotCore(item.Value)).ToArray();
    }

    private PluginSettingsTransferSnapshot ReadTransferRecord(PluginRecord record)
    {
        try
        {
            if (record.Transfer is null) return FreezeSnapshot(new(LegacyTransfer(record.State), []));
            if (record.Transfer.SchemaVersion != 1)
                throw new ArgumentException("Saved plugin transfer metadata schema is invalid.");
            return FreezeSnapshot(new(new(record.State, record.Transfer.PendingPurposes), record.Transfer.CompleteProjections));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Saved plugin settings transfer state is invalid."); }
    }

    private PluginRecord BuildTransferRecord(PluginSettingsTransferState transfer, PluginSettingsTransferSnapshot? captured)
    {
        var current = GetTransferSnapshotCore(transfer.State.Coordinate);
        var caches = MatchingCaches(transfer, current.CompleteProjections).ToList();
        if (captured is not null)
        {
            // The private captured view replaces cache metadata for its exact source bodies.
            // Absence must clear an old cache too (for example a new refusal which cannot fit).
            var capturedSources = Documents(captured.Transfer).Select(SourceDigest).ToHashSet(StringComparer.Ordinal);
            caches.RemoveAll(cache => capturedSources.Contains(cache.SourceDigest));
            foreach (var cache in MatchingCaches(transfer, captured.CompleteProjections)) PutCache(caches, cache);
        }
        SettingsPolicyBinding? binding;
        try { binding = Binding(transfer.State.Coordinate.PluginId); }
        catch (InvalidOperationException) { binding = null; }
        if (binding is not null)
        foreach (var document in Documents(transfer))
        {
            if (caches.Any(cache => cache.SourceDigest == SourceDigest(document))) continue;
            var projection = TryProjectComplete(binding, document);
            if (projection is not null) PutCache(caches, new(SourceDigest(document), projection));
        }
        return RecordFromSnapshot(FitSnapshotToSavedBudget(FreezeSnapshot(new(transfer, caches))));
    }

    private static PluginRecord RecordFromSnapshot(PluginSettingsTransferSnapshot snapshot)
    {
        // Preserve the old private envelope when it needs neither new purposes nor a complete cache.
        var needsMetadata = snapshot.CompleteProjections.Count != 0 ||
            snapshot.Transfer.PendingPurposes.Any(item => item.Purpose == SettingsTransferPurpose.Complete);
        return new(1, snapshot.Transfer.State) { Transfer = needsMetadata
            ? new(1, snapshot.Transfer.PendingPurposes, snapshot.CompleteProjections) : null };
    }

    private static PluginSettingsTransferSnapshot FitSnapshotToSavedBudget(PluginSettingsTransferSnapshot snapshot)
    {
        var fitted = snapshot with { CompleteProjections = [] };
        ValidateSavedSize(RecordFromSnapshot(fitted), MaximumSavedBytes);
        // Cache is optional acceleration. Mandatory state and purpose metadata may never be dropped.
        // Stable document order gives active values priority, followed by retained pending order.
        // A replaced/refused projection is already the sole candidate for its source; an older
        // complete projection cannot reappear merely because the new candidate does not fit.
        var available = snapshot.CompleteProjections.ToDictionary(cache => cache.SourceDigest, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in Documents(snapshot.Transfer))
        {
            var digest = SourceDigest(document);
            if (!seen.Add(digest) || !available.TryGetValue(digest, out var cache)) continue;
            var candidate = fitted with { CompleteProjections = fitted.CompleteProjections.Append(cache).ToArray() };
            try { ValidateSavedSize(RecordFromSnapshot(candidate), MaximumSavedBytes); }
            catch (ArgumentException) { continue; }
            fitted = candidate;
        }
        return fitted;
    }

    private PluginSettingsTransferState PlanningTransfer(PluginSettingsCoordinate coordinate, PluginSettingsTransferState? planned)
    {
        var current = GetTransferCore(coordinate);
        var revision = checked(current.State.Revision + 1);
        if (planned is null) return current with { State = current.State with { Revision = revision } };
        var proposed = FreezeTransfer(planned);
        if (proposed.State.Coordinate != coordinate || proposed.State.Revision != revision)
            throw new InvalidOperationException("Deferred settings planning revision changed.");
        return proposed;
    }

    private static PluginSettingsTransferState AppendTransferPending(PluginSettingsTransferState transfer,
        PluginSettingsDocument incoming, string purpose, string reason)
    {
        var purposes = PurposeMap(transfer);
        if (transfer.State.Pending.Any(item => Purpose(item, purposes) == purpose && SameDocument(item.Document, incoming)))
            return FreezeTransfer(transfer);
        if (transfer.State.Pending.Count >= SettingsPreferenceContract.MaximumPendingPerDocument)
            throw new InvalidOperationException("Deferred plugin settings capacity is full; no saved document was evicted.");
        var id = Guid.NewGuid().ToString("N");
        var pending = transfer.State.Pending.Append(new PluginSettingsDeferredDocument(id, incoming,
            purpose == SettingsTransferPurpose.Portable, reason)).ToArray();
        return FreezeTransfer(new(transfer.State with { Pending = pending, PendingReason = PendingReason(pending) },
            transfer.PendingPurposes.Append(new(id, purpose)).ToArray()));
    }

    private static bool TryAdmitComplete(SettingsPolicyBinding binding, PluginSettingsDocument incoming, JsonElement? current,
        out PluginSettingsDocument? active, out PluginSettingsProjection? portable, out PluginSettingsCompleteProjection? complete)
    {
        active = null; portable = null; complete = null;
        if (binding.Policy is not IPluginCompleteSettingsPolicy policy) return false;
        try
        {
            var admission = policy.AdmitComplete(FreezeDocument(incoming, legacy: false), current?.Clone());
            if (admission is null || !admission.Accepted || admission.SettingsVersion is < 1 or > 1_000_000) return false;
            active = new(incoming.PluginId, incoming.Scope, incoming.ScopeId, binding.PackageVersion,
                admission.SettingsVersion, FreezeValues(admission.Values, ValuesLimit(incoming.Coordinate)));
            var projected = binding.Policy.ExportPortable(FreezeDocument(active, legacy: false));
            if (projected is null || projected.SettingsVersion != admission.SettingsVersion ||
                projected.OmittedFields is < 0 or > 1_000_000) return false;
            portable = projected with { Values = FreezeValues(projected.Values, ValuesLimit(incoming.Coordinate)) };
            // Actual complete admission governs acceptance. Preserved destination exclusions
            // may make the resulting active state unsuitable for complete export.
            complete = TryProjectComplete(binding, active);
            return true;
        }
        catch (Exception error) when (Ordinary(error)) { active = null; portable = null; complete = null; return false; }
    }

    private static PluginSettingsCompleteProjection? TryProjectComplete(SettingsPolicyBinding binding, PluginSettingsDocument document)
    {
        if (binding.Policy is not IPluginCompleteSettingsPolicy policy || document.SettingsVersion is null ||
            document.SourcePackageVersion is null) return null;
        try
        {
            var projection = FreezeCompleteProjection(document, policy.ExportComplete(FreezeDocument(document, legacy: false)));
            return projection;
        }
        catch (Exception error) when (Ordinary(error)) { return null; }
    }

    private static PluginSettingsCompleteProjection FreezeCompleteProjection(PluginSettingsDocument document,
        PluginSettingsCompleteProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (document.SettingsVersion is null || document.SourcePackageVersion is null ||
            projection.SettingsVersion != document.SettingsVersion || projection.SettingsVersion is < 1 or > 1_000_000 ||
            projection.ExcludedFields is < 0 or > 1_000_000 ||
            projection.UnsupportedFields is null || projection.UnsupportedFields.Count > 64 ||
            projection.UnsupportedFields.Any(field => string.IsNullOrEmpty(field) || field.Length > 128 ||
                field.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))) ||
            projection.UnsupportedFields.Distinct(StringComparer.Ordinal).Count() != projection.UnsupportedFields.Count ||
            projection.Complete && projection.UnsupportedFields.Count != 0)
            throw new ArgumentException("Complete settings projection metadata is invalid.");
        return projection with { Values = FreezeValues(projection.Values, ValuesLimit(document.Coordinate)),
            UnsupportedFields = projection.UnsupportedFields.ToArray() };
    }

    private static PluginSettingsTransferState FreezeTransfer(PluginSettingsTransferState transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(transfer.State);
        if (transfer.SchemaVersion != CompleteSettingsContract.PluginEnvelopeVersion)
            throw new ArgumentException("Settings transfer envelope version is invalid.");
        if (transfer.PendingPurposes is null || transfer.State.Pending is null ||
            transfer.PendingPurposes.Count != transfer.State.Pending.Count)
            throw new ArgumentException("Every deferred settings document requires one purpose.");
        var purposes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in transfer.PendingPurposes)
        {
            if (item is null || !purposes.TryAdd(item.PendingId, item.Purpose))
                throw new ArgumentException("Deferred settings purpose metadata is invalid.");
            ValidatePurpose(item.Purpose);
        }
        foreach (var item in transfer.State.Pending)
        {
            if (item is null || !purposes.TryGetValue(item.Id, out var purpose) ||
                item.Portable != (purpose == SettingsTransferPurpose.Portable))
                throw new ArgumentException("Deferred settings purpose metadata is invalid.");
            if (purpose == SettingsTransferPurpose.Complete) FreezeDocument(item.Document, legacy: false);
        }
        var state = FreezeState(transfer.State, purposes);
        return new(state, state.Pending.Select(item => new PluginSettingsPendingPurpose(item.Id, purposes[item.Id])).ToArray());
    }

    private static PluginSettingsTransferSnapshot FreezeSnapshot(PluginSettingsTransferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var transfer = FreezeTransfer(snapshot.Transfer);
        if (snapshot.CompleteProjections is null ||
            snapshot.CompleteProjections.Count > SettingsPreferenceContract.MaximumPendingPerDocument + 1)
            throw new ArgumentException("Complete settings projection cache is invalid.");
        var documents = Documents(transfer).GroupBy(SourceDigest, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var caches = snapshot.CompleteProjections.Select(item =>
        {
            if (item is null || item.SourceDigest is null || !seen.Add(item.SourceDigest) ||
                !documents.TryGetValue(item.SourceDigest, out var source))
                throw new ArgumentException("Complete settings projection cache is not bound to its exact source.");
            var projection = FreezeCompleteProjection(source, item.Projection);
            return item with { Projection = projection };
        }).ToArray();
        return new(transfer, caches);
    }

    private static PluginSettingsTransferState LegacyTransfer(PluginSettingsState state) =>
        FreezeTransfer(new(state, state.Pending.Select(item => new PluginSettingsPendingPurpose(item.Id,
            item.Portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local)).ToArray()));

    private static PluginSettingsTransferState WithLegacyState(PluginSettingsTransferState current, PluginSettingsState state)
    {
        var purposes = PurposeMap(current);
        return FreezeTransfer(new(state, state.Pending.Select(item => new PluginSettingsPendingPurpose(item.Id,
            purposes.TryGetValue(item.Id, out var retained) ? retained :
                item.Portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local)).ToArray()));
    }

    private static void RequireLegacy(PluginSettingsTransferState transfer)
    {
        if (transfer.PendingPurposes.Any(item => item.Purpose == SettingsTransferPurpose.Complete))
            throw new InvalidOperationException("Complete deferred settings require the version-2 settings interface.");
    }

    private static Dictionary<string, string> PurposeMap(PluginSettingsTransferState transfer) =>
        transfer.PendingPurposes.ToDictionary(item => item.PendingId, item => item.Purpose, StringComparer.Ordinal);

    private static string Purpose(PluginSettingsDeferredDocument item, IReadOnlyDictionary<string, string>? purposes) =>
        purposes is not null && purposes.TryGetValue(item.Id, out var purpose) ? purpose :
            item.Portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local;

    private static void ValidatePurpose(string purpose)
    {
        if (purpose is not (SettingsTransferPurpose.Portable or SettingsTransferPurpose.Complete or SettingsTransferPurpose.Local))
            throw new ArgumentException("Settings transfer purpose is invalid.");
    }

    private static IEnumerable<PluginSettingsDocument> Documents(PluginSettingsTransferState transfer)
    {
        if (transfer.State.Active is { } active) yield return active;
        foreach (var pending in transfer.State.Pending) yield return pending.Document;
    }

    private static PluginSettingsCompleteCache[] MatchingCaches(PluginSettingsTransferState transfer,
        IEnumerable<PluginSettingsCompleteCache> caches)
    {
        var sources = Documents(transfer).Select(SourceDigest).ToHashSet(StringComparer.Ordinal);
        return caches.Where(item => sources.Contains(item.SourceDigest)).ToArray();
    }

    private static void PutCache(List<PluginSettingsCompleteCache> caches, PluginSettingsCompleteCache cache)
    {
        caches.RemoveAll(item => item.SourceDigest == cache.SourceDigest);
        caches.Add(cache);
    }

    private static bool SameTransfer(PluginSettingsTransferState left, PluginSettingsTransferState right) =>
        SameState(left.State, right.State) && left.PendingPurposes.SequenceEqual(right.PendingPurposes);

    private static bool SameCaches(IReadOnlyList<PluginSettingsCompleteCache> left, IReadOnlyList<PluginSettingsCompleteCache> right) =>
        left.Count == right.Count && left.All(item => right.Any(other => item.SourceDigest == other.SourceDigest &&
            item.Projection.SettingsVersion == other.Projection.SettingsVersion &&
            item.Projection.ExcludedFields == other.Projection.ExcludedFields && item.Projection.Complete == other.Projection.Complete &&
            Equal(item.Projection.Values, other.Projection.Values) &&
            item.Projection.UnsupportedFields.SequenceEqual(other.Projection.UnsupportedFields)));

    private static string SourceDigest(PluginSettingsDocument document)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("purpose", SettingsTransferPurpose.Complete);
            writer.WriteString("pluginId", document.PluginId);
            writer.WriteString("scope", document.Scope);
            writer.WriteString("scopeId", document.ScopeId);
            writer.WriteString("sourcePackageVersion", document.SourcePackageVersion);
            if (document.SettingsVersion is { } version) writer.WriteNumber("settingsVersion", version);
            else writer.WriteNull("settingsVersion");
            writer.WritePropertyName("values");
            WriteCanonicalValues(writer, document.Values);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalValues(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(field.Name);
                WriteCanonicalValues(writer, field.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonicalValues(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
