using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PackageSettings;

/// <summary>Package-internal owner envelopes. Domain classification and admission stay with the package.</summary>
internal sealed class PackageSettingsOwnerClient(IDesktopServiceHost service, PluginSettingsCoordinate coordinate,
    string sourcePackageVersion)
{
    private const int MaximumReplyBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 32
    };
    private readonly SemaphoreSlim capabilityGate = new(1, 1);
    private readonly ConditionalWeakTable<PluginSettingsState, PurposeFrame> purposeFrames = new();
    private readonly object stateGate = new();
    private int envelopeVersion;
    private PluginSettingsState? lastObserved;
    private PurposeFrame? lastPurposes;

    internal async Task<PluginSettingsState> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var transfer = await SupportsTransferAsync(token);
            var reply = await service.ExecuteServiceCommandAsync(transfer ? CompleteSettingsContract.PluginGetOperation :
                "settings.plugins.get", coordinate, token).WaitAsync(token);
            if (!reply.Success) throw new InvalidDataException();
            PluginSettingsState state;
            PurposeFrame purposes;
            if (transfer)
            {
                RequireVersion(reply.Data, "schemaVersion", CompleteSettingsContract.PluginEnvelopeVersion);
                var envelope = CheckTransfer(Read<PluginSettingsTransferState>(reply.Data));
                state = envelope.State; purposes = Frame(envelope);
            }
            else { state = Check(Read<PluginSettingsState>(reply.Data)); purposes = LegacyFrame(state); }
            Remember(state, purposes);
            return state;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Ordinary(error))
        { throw new PackageSettingsUnavailableException("Saved settings are unavailable. Your draft and existing local files are preserved.", error); }
    }

    internal string PendingPurpose(PluginSettingsState state, string id)
    {
        Check(state);
        var frame = BaselineFrame(state, envelopeVersion == CompleteSettingsContract.PluginEnvelopeVersion);
        return frame.Purposes.TryGetValue(id, out var purpose) ? purpose :
            throw new InvalidDataException("Reload saved settings before applying this retained document.");
    }

    internal async Task<PluginSettingsSaveResult> SaveAsync(PluginSettingsState baseline, int settingsVersion,
        JsonElement values, PluginSettingsProjection projection, string? pendingId, CancellationToken token,
        PluginSettingsCompleteProjection? completeProjection = null, bool completeAdmitted = false)
    {
        token.ThrowIfCancellationRequested(); Check(baseline);
        if (settingsVersion < 1 || baseline.Revision == long.MaxValue ||
            values.ValueKind != JsonValueKind.Object || projection.SettingsVersion != settingsVersion ||
            projection.Values.ValueKind != JsonValueKind.Object || projection.OmittedFields < 0 ||
            !PluginVersion.TryParse(sourcePackageVersion, out _) ||
            (pendingId is not null && !baseline.Pending.Any(item => item.Id == pendingId)))
            throw new InvalidDataException("Reload saved settings before applying these choices.");
        var transfer = await SupportsTransferAsync(token);
        var priorPurposes = BaselineFrame(baseline, transfer);
        var completeSelected = pendingId is not null && priorPurposes.Purposes.TryGetValue(pendingId, out var selectedPurpose) &&
            selectedPurpose == SettingsTransferPurpose.Complete;
        if (completeAdmitted != completeSelected)
            throw new InvalidDataException("Review and admit the selected complete preferences before saving.");
        // Unsupported optional complete coverage never prevents an ordinary/portable preference write.
        var frozenComplete = transfer && completeProjection is not null ? FreezeComplete(completeProjection, settingsVersion) : null;
        var document = new PluginSettingsDocument(coordinate.PluginId, coordinate.Scope, coordinate.ScopeId,
            sourcePackageVersion, settingsVersion, values.Clone());
        var request = new PluginSettingsSaveRequest(document, baseline.Revision, true,
            projection.Values.Clone(), projection.OmittedFields) { PendingId = pendingId };
        object payload = transfer ? new PluginSettingsTransferSaveRequest(request, frozenComplete) { CompleteAdmitted = completeAdmitted } : request;
        // No route fallback or replay is permitted once a mutation dispatch starts.
        try
        {
            var reply = await service.ExecuteServiceCommandAsync(transfer ? CompleteSettingsContract.PluginSaveOperation :
                "settings.plugins.save", payload, token).WaitAsync(token);
            if (!reply.Success) throw new InvalidDataException();
            PluginSettingsSaveResult result;
            PurposeFrame nextPurposes;
            if (transfer)
            {
                if (!reply.Data.TryGetProperty("state", out var stateRaw)) throw new InvalidDataException();
                RequireVersion(stateRaw, "schemaVersion", CompleteSettingsContract.PluginEnvelopeVersion);
                var envelope = Read<PluginSettingsTransferSaveResult>(reply.Data);
                var state = CheckTransfer(envelope.State);
                result = new(state.State, envelope.Changed); nextPurposes = Frame(state);
            }
            else { result = Read<PluginSettingsSaveResult>(reply.Data); nextPurposes = LegacyFrame(Check(result.State)); }
            var saved = Check(result.State);
            if (saved.Active is not { } active || active.SettingsVersion != settingsVersion ||
                active.SourcePackageVersion != sourcePackageVersion || !JsonElement.DeepEquals(active.Values, document.Values) ||
                saved.Revision != (result.Changed ? baseline.Revision + 1 : baseline.Revision) ||
                !PendingEqual(baseline.Pending.Where(item => item.Id != pendingId).ToArray(), saved.Pending) ||
                !PurposesEqual(priorPurposes, nextPurposes, pendingId) ||
                (!result.Changed && (!StateEqual(baseline, saved) || !PurposesEqual(priorPurposes, nextPurposes))))
                throw new InvalidDataException();
            Remember(saved, nextPurposes);
            return result;
        }
        catch (Exception error) when (Ordinary(error))
        { throw new PackageSettingsSaveUncertainException("Save was not confirmed. Your draft is preserved. Reload saved choices before another save; this request will not be repeated.", error); }
    }

    private async Task<bool> SupportsTransferAsync(CancellationToken token)
    {
        if (envelopeVersion != 0) return envelopeVersion == CompleteSettingsContract.PluginEnvelopeVersion;
        await capabilityGate.WaitAsync(token);
        try
        {
            if (envelopeVersion != 0) return envelopeVersion == CompleteSettingsContract.PluginEnvelopeVersion;
            var reply = await service.ExecuteServiceCommandAsync(CompleteSettingsContract.DescribeOperation, new { }, token).WaitAsync(token);
            if (!reply.Success)
            {
                // These exact probe errors are the Core contract for an older owner. Other errors stay unavailable.
                if (reply.Error is not ("Unknown configuration operation." or "Unknown command: configuration.settings.describe"))
                    throw new InvalidDataException();
                envelopeVersion = 1; return false;
            }
            RequireVersion(reply.Data, "pluginEnvelopeVersion", CompleteSettingsContract.PluginEnvelopeVersion);
            var capability = Read<CompleteSettingsCapabilities>(reply.Data);
            if (capability.SchemaVersion != CompleteSettingsContract.SchemaVersion ||
                capability.DefaultBackupSchemaVersion != ConfigurationBackupContract.SchemaVersion ||
                // Complete backup schema 16 adds owner task-budget data. This client uses the
                // unchanged plugin transfer envelope, so only these two reviewed schemas fit.
                capability.CompleteBackupSchemaVersion is not (15 or 16) ||
                capability.PluginEnvelopeVersion != CompleteSettingsContract.PluginEnvelopeVersion ||
                capability.Purposes.Count != 2 || capability.Purposes.Distinct(StringComparer.Ordinal).Count() != 2 ||
                !capability.Purposes.Contains(SettingsTransferPurpose.Portable, StringComparer.Ordinal) ||
                !capability.Purposes.Contains(SettingsTransferPurpose.Complete, StringComparer.Ordinal) ||
                capability.PresetExportOperation != "configuration.presets.export" ||
                capability.BackupExportOperation != "configuration.backup.export" ||
                capability.PluginGetOperation != CompleteSettingsContract.PluginGetOperation ||
                capability.PluginSaveOperation != CompleteSettingsContract.PluginSaveOperation ||
                capability.LayoutCoverage != "opaque-renderer-state")
                throw new InvalidDataException();
            envelopeVersion = CompleteSettingsContract.PluginEnvelopeVersion;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Ordinary(error))
        { throw new PackageSettingsUnavailableException("Saved settings capabilities are unavailable. No save or route fallback was attempted.", error); }
        finally { capabilityGate.Release(); }
    }

    private PluginSettingsTransferState CheckTransfer(PluginSettingsTransferState envelope)
    {
        var state = Check(envelope.State);
        if (envelope.SchemaVersion != CompleteSettingsContract.PluginEnvelopeVersion ||
            envelope.PendingPurposes.Count != state.Pending.Count ||
            envelope.PendingPurposes.Select(p => p.PendingId).Distinct(StringComparer.Ordinal).Count() != state.Pending.Count)
            throw new InvalidDataException();
        foreach (var item in envelope.PendingPurposes)
        {
            var pending = state.Pending.SingleOrDefault(p => p.Id == item.PendingId);
            if (pending is null || item.Purpose is not (SettingsTransferPurpose.Local or
                SettingsTransferPurpose.Portable or SettingsTransferPurpose.Complete) ||
                pending.Portable != (item.Purpose == SettingsTransferPurpose.Portable))
                throw new InvalidDataException();
        }
        return envelope;
    }

    private static void RequireVersion(JsonElement raw, string name, int version)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var actual) || actual != version)
            throw new InvalidDataException();
    }

    private PurposeFrame BaselineFrame(PluginSettingsState state, bool transfer)
    {
        if (purposeFrames.TryGetValue(state, out var frame)) return frame;
        if (!transfer && envelopeVersion == 1) return LegacyFrame(state);
        if (state.Pending.Count == 0) return new(new Dictionary<string, string>(StringComparer.Ordinal));
        throw new InvalidDataException("Reload saved settings before selecting a retained document.");
    }
    private static PurposeFrame Frame(PluginSettingsTransferState transfer) =>
        new(transfer.PendingPurposes.ToDictionary(p => p.PendingId, p => p.Purpose, StringComparer.Ordinal));
    private static PurposeFrame LegacyFrame(PluginSettingsState state) =>
        new(state.Pending.ToDictionary(p => p.Id, p => p.Portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local, StringComparer.Ordinal));
    private void Remember(PluginSettingsState state, PurposeFrame purposes)
    {
        lock (stateGate)
        {
            if (lastObserved is { } prior && (state.Revision < prior.Revision || state.Revision == prior.Revision &&
                (!StateEqual(prior, state) || !PurposesEqual(lastPurposes!, purposes))))
                throw new InvalidDataException("The settings revision is inconsistent; reload after the owner state is corrected.");
            purposeFrames.Remove(state); purposeFrames.Add(state, purposes);
            lastObserved = state; lastPurposes = purposes;
        }
    }
    private static bool PurposesEqual(PurposeFrame expected, PurposeFrame actual, string? removed = null) =>
        expected.Purposes.Count - (removed is not null && expected.Purposes.ContainsKey(removed) ? 1 : 0) == actual.Purposes.Count &&
        expected.Purposes.Where(p => p.Key != removed).All(p => actual.Purposes.TryGetValue(p.Key, out var value) && value == p.Value);
    private sealed class PurposeFrame(Dictionary<string, string> values)
    {
        internal IReadOnlyDictionary<string, string> Purposes { get; } = new ReadOnlyDictionary<string, string>(values);
    }

    internal PluginSettingsCompleteProjection? ProjectComplete(IPluginSettingsPolicy policy, PluginSettingsDocument document)
    {
        if (policy is not IPluginCompleteSettingsPolicy complete) return null;
        try
        {
            if (document.Coordinate != coordinate || document.SettingsVersion is not { } version) throw new InvalidDataException();
            return FreezeComplete(complete.ExportComplete(document), version);
        }
        catch (Exception error) when (Ordinary(error))
        {
            // A broken optional projection disables full export, never an otherwise valid preference save.
            return new(document.SettingsVersion ?? 1, JsonSerializer.SerializeToElement(new { }), Complete: false);
        }
    }

    private PluginSettingsCompleteProjection FreezeComplete(PluginSettingsCompleteProjection projection, int version)
    {
        var maximum = coordinate.Scope == SettingsPreferenceContract.AgentScope ?
            SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes : SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes;
        if (projection.SettingsVersion != version || projection.Values.ValueKind != JsonValueKind.Object ||
            Utf8.GetByteCount(projection.Values.GetRawText()) > maximum || projection.ExcludedFields is < 0 or > 1_000_000 ||
            projection.UnsupportedFields is null || projection.UnsupportedFields.Count > 64 ||
            projection.UnsupportedFields.Any(field => string.IsNullOrEmpty(field) || field.Length > 128 ||
                field.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) ||
            projection.UnsupportedFields.Distinct(StringComparer.Ordinal).Count() != projection.UnsupportedFields.Count ||
            projection.Complete && projection.UnsupportedFields.Count != 0)
            throw new InvalidDataException("Complete preference metadata is unsupported; your draft is preserved.");
        RejectDuplicates(projection.Values);
        return projection with { Values = projection.Values.Clone(), UnsupportedFields = projection.UnsupportedFields.ToArray() };
    }

    private PluginSettingsState Check(PluginSettingsState state)
    {
        if (state.Coordinate != coordinate || state.Revision < 0 ||
            state.Pending.Count > SettingsPreferenceContract.MaximumPendingPerDocument ||
            (state.Active is { } active && active.Coordinate != coordinate) ||
            state.Pending.Any(item => string.IsNullOrEmpty(item.Id) || item.Document.Coordinate != coordinate) ||
            state.Pending.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Pending.Count)
            throw new InvalidDataException("The saved settings reply does not match this package.");
        // Pending schemas and values remain opaque; identity checking never admits them.
        return state;
    }

    private static T Read<T>(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || Utf8.GetByteCount(raw.GetRawText()) > MaximumReplyBytes)
            throw new InvalidDataException();
        RejectDuplicates(raw);
        return raw.Deserialize<T>(Json) ?? throw new InvalidDataException();
    }

    private static void RejectDuplicates(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in raw.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                RejectDuplicates(property.Value);
            }
        }
        else if (raw.ValueKind == JsonValueKind.Array)
            foreach (var item in raw.EnumerateArray()) RejectDuplicates(item);
    }

    // A no-op acknowledges the observed state unchanged, including opaque values/provenance.
    // Desired values matching the request alone cannot prove that the store performed no mutation.
    private static bool StateEqual(PluginSettingsState left, PluginSettingsState right) =>
        left.Coordinate == right.Coordinate && left.Revision == right.Revision &&
        DocumentEqual(left.Active, right.Active) && PendingEqual(left.Pending, right.Pending) &&
        left.ValidationAuthority == right.ValidationAuthority &&
        OptionalValuesEqual(left.PortableValues, right.PortableValues) &&
        left.OmittedPortableFields == right.OmittedPortableFields && left.PendingReason == right.PendingReason;

    private static bool DocumentEqual(PluginSettingsDocument? left, PluginSettingsDocument? right) =>
        left is null ? right is null : right is not null &&
        left.Coordinate == right.Coordinate && left.SourcePackageVersion == right.SourcePackageVersion &&
        left.SettingsVersion == right.SettingsVersion && JsonElement.DeepEquals(left.Values, right.Values);

    private static bool OptionalValuesEqual(JsonElement? left, JsonElement? right) =>
        left is { } leftValues ? right is { } rightValues && JsonElement.DeepEquals(leftValues, rightValues) : right is null;

    private static bool PendingEqual(IReadOnlyList<PluginSettingsDeferredDocument> expected,
        IReadOnlyList<PluginSettingsDeferredDocument> actual) =>
        expected.Count == actual.Count && expected.All(item => actual.Any(other =>
            other.Id == item.Id && other.Portable == item.Portable && other.Reason == item.Reason &&
            DocumentEqual(other.Document, item.Document)));
    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
internal sealed class PackageSettingsUnavailableException(string message, Exception error) : Exception(message, error);
internal sealed class PackageSettingsSaveUncertainException(string message, Exception error) : Exception(message, error);
