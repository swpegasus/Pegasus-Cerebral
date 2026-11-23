using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Microsoft.Data.Sqlite;
using static Cerebral.Worker.SourceRecoveryManifest;

namespace Cerebral.Worker;

internal enum SourceRecoveryExpectedState { Held, Resumed }
internal sealed record SourceRecoveryPublicCoordinates(string InstallationId, long SettingsRevision,
    string HoldId, string RequestId, long HoldGeneration, long HoldRevision, string HoldState,
    long EngineEpoch, string SelectedSlot, string KnownGoodSlot, bool EnginePaused,
    string SettingsSha256, string HoldSha256, string SelectionSha256);
internal sealed record SourceRecoveryFingerprintVerdict(bool BaselineMatched,
    IReadOnlyList<SourceRecoveryRowFingerprint> Rows, string ReadScope);
internal sealed record SourceRecoveryPersistedStateVerdict(SourceRecoveryPublicCoordinates Coordinates, string ReadScope);

internal sealed class SourceRecoveryPreflightResult
{
    internal SourceRecoveryPreflightResult(HostSettings settings, SourceRecoveryPublicCoordinates coordinates,
        SourceRecoveryFingerprintVerdict fingerprints, string initializerReview)
    { Settings = settings; Coordinates = coordinates; Fingerprints = fingerprints; InitializerReview = initializerReview; }
    // The raw detached object contains credentials; it is never a public result or log payload.
    [JsonIgnore] public HostSettings Settings { get; }
    public SourceRecoveryPublicCoordinates Coordinates { get; }
    public SourceRecoveryFingerprintVerdict Fingerprints { get; }
    public string InitializerReview { get; }
    public bool HealthVerified => false;
    public override string ToString() => "cerebral.source-recovery-preflight/1:baseline-matched;health-unverified";
}

/// <summary>
/// Raw read-only checks, called after the host owns service.lock and before ordinary composition.
/// These separate file/SQLite snapshots are fences, not an atomic snapshot or authority to replay.
/// </summary>
internal static class SourceRecoveryPreflight
{
    private const int MaximumSettingsBytes = 256 * 1024;
    private const int MaximumHoldBytes = 16 * 1024;
    private const long MaximumDatabaseBytes = 512L * 1024 * 1024;
    private const int MaximumRowBytes = 8 * 1024 * 1024;
    private const int MaximumTotalRowBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Columns = ["id", "request_id", "conversation_id", "profile_id", "status", "revision", "submission", "payload"];

    public static SourceRecoveryPreflightResult Verify(SourceRecoveryManifest manifest, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); manifest.RequireUnchanged(); manifest.ValidateCurrentOwner();
            var baseline = manifest.ReadBaseline();
            VerifyPinnedFiles(manifest, token);
            var state = ReadPersistedState(manifest, SourceRecoveryExpectedState.Held, token, out var settings);
            var rows = ReadRows(manifest, baseline, immutable: true, token);
            VerifyPinnedFiles(manifest, token); manifest.RequireUnchanged();
            // Recheck the held policy after the independent workflow snapshot; do not recapture new defaults.
            var final = ReadPersistedState(manifest, SourceRecoveryExpectedState.Held, token, out _);
            Require(final.Coordinates == state.Coordinates, SourceRecoveryFailure.store_changed);
            return new(settings, state.Coordinates, rows, manifest.PassiveInitializerReview.State);
        }
        catch (SourceRecoveryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { token.ThrowIfCancellationRequested(); throw Error(SourceRecoveryFailure.preflight_unavailable); }
    }

    /// <summary>Read-only stopped-owner admission for the separate service package repair path.
    /// The saved hold may contain completed captured work; each such entry is checked against
    /// its durable row rather than being assumed resolved from the marker alone.</summary>
    internal static void VerifyOfflinePackageRecoveryWork(ServicePackageOfflineRecoveryRequest request)
    {
        using var main = new ReadDatabase(Path.Combine(request.DataRoot, "cerebral.db"), false,
            CancellationToken.None);
        using (var settings = JsonDocument.Parse(main.ReadRecord("settings", "host", MaximumSettingsBytes)))
        {
            RejectDuplicateProperties(settings.RootElement);
            if (settings.RootElement.GetProperty("installationId").GetString() != request.InstallationId)
                throw new InvalidDataException("The saved installation differs from the recovery request.");
        }
        using var hold = JsonDocument.Parse(main.ReadRecord("owner-work-hold", "intentional", MaximumHoldBytes));
        var value = hold.RootElement;
        RejectDuplicateProperties(value);
        RequireObject(value, ["version", "holdId", "requestId", "generation", "revision", "requestedEpoch",
            "priorGeneration", "priorRevision", "mode", "state", "pauseCommitted", "work", "fault"]);
        var expected = request.Hold;
        if (Integer(value, "version", 1, 1) != 1 ||
            Identifier(value, "holdId") != expected.HoldId ||
            Identifier(value, "requestId") != expected.RequestId ||
            Integer(value, "generation", 1, long.MaxValue) != expected.Generation ||
            Integer(value, "revision", 1, long.MaxValue) != expected.Revision ||
            Integer(value, "requestedEpoch", 0, long.MaxValue) != expected.RequestedEpoch ||
            Text(value, "mode", 32) != "finish-current" ||
            Text(value, "state", 32) != "holding" || !Boolean(value, "pauseCommitted") ||
            value.GetProperty("fault").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("The persisted owner hold changed or is not settled.");
        var captured = Array(value, "work", 0, 200).ToArray();
        foreach (var item in captured)
        {
            RequireObject(item, ["kind", "id", "conversationId", "wasExecuting"]);
            if (Text(item, "kind", 16) is not ("job" or "workflow") ||
                item.GetProperty("wasExecuting").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("The saved hold contains an unsupported captured work entry.");
            _ = Identifier(item, "id");
            _ = Identifier(item, "conversationId");
        }
        if (captured.Select(item => item.GetProperty("kind").GetString() + ":" +
                item.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != captured.Length)
            throw new InvalidDataException("The saved hold contains duplicate captured work.");
        using (var active = main.Command("""
            SELECT 1 FROM records WHERE kind='job' AND
                (NOT json_valid(body) OR json_type(body,'$.status') IS NOT 'text' OR
                 json_extract(body,'$.status') NOT IN ('completed','failed','cancelled'))
            LIMIT 1
            """))
            if (active.ExecuteScalar() is not null)
                throw new InvalidOperationException("Legacy work is active or malformed; package recovery is refused.");
        foreach (var item in captured.Where(item => item.GetProperty("kind").GetString() == "job"))
        {
            RequireObject(item, ["kind", "id", "conversationId", "wasExecuting"]);
            var id = Identifier(item, "id");
            using var row = main.Command("""
                SELECT json_extract(body,'$.status'),json_extract(body,'$.sessionId')
                FROM records WHERE kind='job' AND id=$id LIMIT 2
                """);
            row.Parameters.AddWithValue("$id", id);
            using var reader = row.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                reader.GetString(0) is not ("completed" or "failed" or "cancelled") ||
                reader.GetString(1) != Identifier(item, "conversationId") || reader.Read())
                throw new InvalidDataException("Captured legacy work is missing, unresolved or changed.");
        }
        main.Complete();
        using var engine = new ReadDatabase(Path.Combine(request.DataRoot, "engine.db"), false,
            CancellationToken.None);
        using (var identity = engine.Command("PRAGMA application_id"))
            if (Convert.ToInt64(identity.ExecuteScalar(), CultureInfo.InvariantCulture) != 0x50434556)
                throw new InvalidDataException("The workflow database identity changed.");
        using (var version = engine.Command("PRAGMA user_version"))
        {
            var observed = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (observed is < 6 or > Cerebral.Engine.Persistence.EventJournal.SchemaVersion)
                throw new InvalidDataException("The workflow database version is unsupported for offline recovery.");
        }
        using (var active = engine.Command("""
            SELECT 1 FROM workflow_runs WHERE status IS NULL OR
                status NOT IN ('Completed','Failed','Cancelled') OR
                NOT json_valid(payload) OR
                lower(COALESCE(json_extract(payload,'$.effect.state'),''))
                    IN ('prepared','dispatched','unknown') LIMIT 1
            """))
            if (active.ExecuteScalar() is not null)
                throw new InvalidOperationException("Durable workflows are active; package recovery is refused.");
        foreach (var item in captured.Where(item => item.GetProperty("kind").GetString() == "workflow"))
        {
            RequireObject(item, ["kind", "id", "conversationId", "wasExecuting"]);
            var id = Identifier(item, "id");
            using var row = engine.Command("""
                SELECT conversation_id,status,json_extract(payload,'$.effect.state')
                FROM workflow_runs WHERE id=$id LIMIT 2
                """);
            row.Parameters.AddWithValue("$id", id);
            using var reader = row.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                reader.GetString(0) != Identifier(item, "conversationId") ||
                reader.GetString(1) is not ("Completed" or "Failed" or "Cancelled") ||
                !reader.IsDBNull(2) && reader.GetString(2) is { } effectState &&
                    effectState.ToLowerInvariant() is "prepared" or "dispatched" or "unknown" ||
                reader.Read())
                throw new InvalidDataException("Captured durable work is missing, uncertain or changed.");
        }
        engine.Complete();
    }


    /// <summary>Requires owner-staged local GPU policy; this check never writes or probes a worker.</summary>
    internal static LocalResourcePauseRecord VerifyLocalResourcePause(SourceRecoveryManifest manifest, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); manifest.RequireUnchanged(); manifest.ValidateCurrentOwner();
            using var database = new ReadDatabase(Path.Combine(manifest.SourceRoot, "cerebral.db"), false, token);
            var bytes = database.ReadRecord(ResourcePausePersistence.RecordKind, ResourcePausePersistence.RecordId, 16 * 1024);
            using var document = JsonDocument.Parse(bytes);
            RequireObject(document.RootElement, ["version", "pauseRequested", "state", "rerouteEligibleWork",
                "prePauseResidentModelIds", "detail", "updatedAt"]);
            var saved = JsonSerializer.Deserialize<LocalResourcePauseRecord>(bytes, CerebralJson.Options);
            Require(saved is { Version: 1, PauseRequested: true, State: LocalResourceState.Paused, RerouteEligibleWork: false } &&
                saved.PrePauseResidentModelIds is { Count: <= 64 } ids &&
                ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl)) &&
                ids.Distinct(StringComparer.Ordinal).Count() == ids.Count &&
                saved.Detail is { Length: <= 2048 } && !saved.Detail.Contains('\0') && saved.UpdatedAt != default,
                SourceRecoveryFailure.resource_pause_required);
            database.Complete(); token.ThrowIfCancellationRequested(); return saved!;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { throw Error(SourceRecoveryFailure.resource_pause_required); }
    }

    /// <summary>Live committed-WAL read. It never uses immutable=1 on an active journal.</summary>
    public static SourceRecoveryFingerprintVerdict VerifyRows(SourceRecoveryManifest manifest, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); manifest.RequireUnchanged(); manifest.ValidateCurrentOwner();
            var result = ReadRows(manifest, manifest.ReadBaseline(), immutable: false, token);
            manifest.RequireUnchanged(); return result;
        }
        catch (SourceRecoveryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { token.ThrowIfCancellationRequested(); throw Error(SourceRecoveryFailure.preflight_unavailable); }
    }

    /// <summary>The parent explicitly chooses the one permitted target; this method dispatches nothing.</summary>
    public static SourceRecoveryPersistedStateVerdict VerifyPersistedState(SourceRecoveryManifest manifest,
        SourceRecoveryExpectedState expectedState, CancellationToken token)
    {
        try
        {
            Require(expectedState is SourceRecoveryExpectedState.Held or SourceRecoveryExpectedState.Resumed,
                SourceRecoveryFailure.hold_mismatch);
            token.ThrowIfCancellationRequested(); manifest.RequireUnchanged(); manifest.ValidateCurrentOwner();
            var result = ReadPersistedState(manifest, expectedState, token, out _);
            manifest.RequireUnchanged(); return result;
        }
        catch (SourceRecoveryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { token.ThrowIfCancellationRequested(); throw Error(SourceRecoveryFailure.preflight_unavailable); }
    }

    internal static void VerifyPinnedFiles(SourceRecoveryManifest manifest, CancellationToken token)
    {
        long totalBytes = 0; var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.ToDictionary(manifest.Resolve, StringComparer.OrdinalIgnoreCase);
        foreach (var root in manifest.Roots)
        {
            token.ThrowIfCancellationRequested(); CheckExistingPath(root.Path, true);
            var pending = new Stack<string>(); pending.Push(root.Path); var directories = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                Require(++directories <= 2048, SourceRecoveryFailure.pin_inventory_changed);
                var directory = pending.Pop(); CheckExistingPath(directory, true);
                var entries = 0;
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    Require(++entries <= MaximumPins + 2048, SourceRecoveryFailure.pin_inventory_changed);
                    var path = CanonicalPath(item); var attributes = File.GetAttributes(path);
                    Require((attributes & FileAttributes.ReparsePoint) == 0, SourceRecoveryFailure.path_reparse);
                    // Nested independently pinned roots own their inventories; do not count them twice.
                    if (manifest.Roots.Any(x => x.Role != root.Role && SamePath(x.Path, path))) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                    var admitted = expected.TryGetValue(path, out var pin);
                    Require(actual.Add(path) && admitted, SourceRecoveryFailure.pin_inventory_changed);
                    Require(actual.Count <= MaximumPins, SourceRecoveryFailure.pin_inventory_changed);
                    totalBytes += pin!.Length;
                    Require(totalBytes <= 4L * 1024 * 1024 * 1024, SourceRecoveryFailure.pin_invalid);
                    Require(HashFile(path, pin.Length, token) == pin.Sha256, SourceRecoveryFailure.pin_changed);
                }
            }
        }
        Require(actual.SetEquals(expected.Keys), SourceRecoveryFailure.pin_inventory_changed);
        ValidateSelectedEngine(manifest);
        if (manifest.PassiveInitializerReview.State == "reviewed")
        {
            var review = ReadBounded(manifest.PassiveInitializerReview.EvidencePath!, 256 * 1024);
            Require(Digest(review) == manifest.PassiveInitializerReview.EvidenceSha256,
                SourceRecoveryFailure.initializer_review_invalid);
        }
    }

    private static SourceRecoveryPersistedStateVerdict ReadPersistedState(SourceRecoveryManifest manifest,
        SourceRecoveryExpectedState target, CancellationToken token, out HostSettings settings)
    {
        var selectionPath = Path.Combine(manifest.SourceRoot, "Engine", "selection.ini");
        var selectionBytes = ReadBounded(selectionPath, 32 * 1024);
        var selectionHash = Digest(selectionBytes);
        if (target == SourceRecoveryExpectedState.Held)
            Require(selectionHash == manifest.SelectionSha256, SourceRecoveryFailure.selection_mismatch);
        ValidateSelection(selectionBytes, manifest, target);
        using var database = new ReadDatabase(Path.Combine(manifest.SourceRoot, "cerebral.db"), false, token);
        var settingsBytes = database.ReadRecord("settings", "host", MaximumSettingsBytes);
        var settingsHash = Digest(settingsBytes);
        Require(settingsHash == manifest.SettingsSha256, SourceRecoveryFailure.settings_invalid);
        settings = ValidateSettings(settingsBytes, manifest);
        var holdBytes = database.ReadRecord("owner-work-hold", "intentional", MaximumHoldBytes);
        var holdHash = Digest(holdBytes);
        if (target == SourceRecoveryExpectedState.Held)
            Require(holdHash == manifest.HoldSha256, SourceRecoveryFailure.hold_mismatch);
        ValidateHold(holdBytes, target);
        using (var command = database.Command("""
            SELECT 1 FROM records WHERE kind='job' AND
                (NOT json_valid(body) OR json_type(body,'$.status') IS NOT 'text' OR
                 json_extract(body,'$.status') NOT IN ('completed','failed','cancelled'))
            LIMIT 1
            """))
        {
            // The reviewed owner hold has an empty legacy-work ledger; no malformed/unknown job is omitted.
            Require(command.ExecuteScalar() is null, SourceRecoveryFailure.hold_mismatch);
        }
        database.Complete();
        Require(Digest(ReadBounded(selectionPath, 32 * 1024)) == selectionHash, SourceRecoveryFailure.store_changed);
        token.ThrowIfCancellationRequested();
        return new(new(manifest.InstallationId, manifest.SettingsRevision, HoldId, RequestId, 1,
            target == SourceRecoveryExpectedState.Held ? 2 : 4,
            target == SourceRecoveryExpectedState.Held ? "holding" : "resumed", 5, "A", "A",
            target == SourceRecoveryExpectedState.Held, settingsHash, holdHash, selectionHash),
            "main:mode-ro+query-only+read-transaction;selection:separate-file-sample;not-global-atomic");
    }

    private static HostSettings ValidateSettings(byte[] bytes, SourceRecoveryManifest manifest)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var value = document.RootElement; RejectDuplicateProperties(value);
        Require(Identifier(value, "installationId") == manifest.InstallationId &&
            Integer(value, "revision", 1, int.MaxValue) == manifest.SettingsRevision &&
            Boolean(value, "coordinator") && !Boolean(value, "standaloneWorker") &&
            !Boolean(value, "allowLan") && !Boolean(value, "backgroundTasksEnabled") &&
            value.GetProperty("managementControllerId").ValueKind == JsonValueKind.Null,
            SourceRecoveryFailure.settings_invalid);
        _ = Text(value, "accessKey", 4096);
        foreach (var name in new[] { "modelDirectory", "projectsDirectory", "sharedDirectory", "toolsDirectory" })
        {
            var path = CanonicalPath(Text(value, name, 1024));
            CheckExistingPath(path, true);
        }
        Require(SamePath(CanonicalPath(Text(value, "llamaServerPath", 1024)),
            Path.Combine(manifest.Root("runtime"), "llama-server.exe")), SourceRecoveryFailure.settings_invalid);
        foreach (var name in new[] { "workers", "executables", "mcpServers", "backgroundTasks", "allowedLogicalProcessors" })
            _ = Array(value, name, 0, name == "allowedLogicalProcessors" ? 1024 : 64).ToArray();
        // Strict decoding returns exactly the retained object; Configuration.Get never participates.
        var options = new JsonSerializerOptions(CerebralJson.Options)
        {
            PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict, MaxDepth = 32
        };
        var settings = JsonSerializer.Deserialize<HostSettings>(bytes, options);
        Require(settings is not null && settings.InstallationId == manifest.InstallationId &&
            settings.Revision == manifest.SettingsRevision, SourceRecoveryFailure.settings_invalid);
        return settings!;
    }

    private static void ValidateHold(byte[] bytes, SourceRecoveryExpectedState target)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        var value = document.RootElement; RejectDuplicateProperties(value);
        RequireObject(value, ["version", "holdId", "requestId", "generation", "revision", "requestedEpoch",
            "priorGeneration", "priorRevision", "mode", "state", "pauseCommitted", "work", "fault"]);
        Require(Integer(value, "version", 1, 1) == 1 && Identifier(value, "holdId") == HoldId &&
            Identifier(value, "requestId") == RequestId && Integer(value, "generation", 1, 1) == 1 &&
            Integer(value, "revision", target == SourceRecoveryExpectedState.Held ? 2 : 4,
                target == SourceRecoveryExpectedState.Held ? 2 : 4) == (target == SourceRecoveryExpectedState.Held ? 2 : 4) &&
            Integer(value, "requestedEpoch", 5, 5) == 5 && Integer(value, "priorGeneration", 0, 0) == 0 &&
            Integer(value, "priorRevision", 0, 0) == 0 && Text(value, "mode", 32) == "finish-current" &&
            Text(value, "state", 32) == (target == SourceRecoveryExpectedState.Held ? "holding" : "resumed") &&
            Boolean(value, "pauseCommitted") && !Array(value, "work", 0, 0).Any() &&
            value.GetProperty("fault").ValueKind == JsonValueKind.Null, SourceRecoveryFailure.hold_mismatch);
    }

    private static void ValidateSelectedEngine(SourceRecoveryManifest manifest)
    {
        var package = manifest.Files.SingleOrDefault(x => x.Role == "engine" && x.RelativePath == "A/engine.ini");
        var configuration = manifest.Files.SingleOrDefault(x => x.Role == "engine" && x.RelativePath == "A/engine-settings.ini");
        Require(package is not null && package.Sha256 == manifest.SelectedPackageSha256 && package.Length <= 32 * 1024 &&
            configuration is not null && configuration.Sha256 == manifest.SelectedConfigurationSha256 && configuration.Length <= 32 * 1024,
            SourceRecoveryFailure.pin_invalid);
        var values = ReadIni(ReadBounded(manifest.Resolve(package!), 32 * 1024), "engine", SourceRecoveryFailure.pin_invalid);
        var keys = new[] { "id", "version", "protocol", "assembly", "assembly-sha256", "entry-type", "configuration-sha256" };
        Require(values.Keys.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)) &&
            values["protocol"] == "pegasus.cerebral.engine/1" && values["id"].Length is >= 1 and <= 100 &&
            values["id"].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
            Version.TryParse(values["version"], out _) && values["entry-type"].Length is >= 1 and <= 256 &&
            values["entry-type"].All(c => !char.IsControl(c)) &&
            values["configuration-sha256"] == configuration!.Sha256 && IsHash(values["assembly-sha256"]),
            SourceRecoveryFailure.pin_invalid);
        var name = values["assembly"];
        Require(name.Length is >= 5 and <= 100 && Path.GetFileName(name) == name &&
            name.EndsWith(".dll", StringComparison.Ordinal) && !name.Contains(':') && !name.Contains('\\') &&
            !name.Contains('/') && name.All(c => !char.IsControl(c)), SourceRecoveryFailure.pin_invalid);
        Require(manifest.Files.Any(x => x.Role == "engine" && x.RelativePath == "A/" + name &&
            x.Sha256 == values["assembly-sha256"] && x.Length <= 32 * 1024 * 1024), SourceRecoveryFailure.pin_invalid);
    }

    private static Dictionary<string, string> ReadIni(byte[] bytes, string expectedSection, SourceRecoveryFailure failure)
    {
        var text = StrictUtf8.GetString(bytes).TrimStart('\ufeff');
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var section = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line.StartsWith('['))
            { Require(!section && line == "[" + expectedSection + "]", failure); section = true; continue; }
            var index = line.IndexOf('=');
            Require(section && index > 0 && values.Count < 64 && values.TryAdd(line[..index].Trim(), line[(index + 1)..].Trim()), failure);
        }
        Require(section, failure); return values;
    }

    private static void ValidateSelection(byte[] bytes, SourceRecoveryManifest manifest, SourceRecoveryExpectedState target)
    {
        var values = ReadIni(bytes, "selection", SourceRecoveryFailure.selection_mismatch);
        var keys = new[] { "schema", "epoch", "slot", "package", "configuration", "known-slot", "known-package",
            "known-configuration", "pending", "paused" };
        Require(values.Keys.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)) &&
            values["schema"] == "1" && values["epoch"] == "5" && values["slot"] == "A" && values["known-slot"] == "A" &&
            values["package"] == manifest.SelectedPackageSha256 && values["known-package"] == manifest.SelectedPackageSha256 &&
            values["configuration"] == manifest.SelectedConfigurationSha256 &&
            values["known-configuration"] == manifest.SelectedConfigurationSha256 &&
            bool.TryParse(values["pending"], out var pending) && !pending &&
            bool.TryParse(values["paused"], out var paused) && paused == (target == SourceRecoveryExpectedState.Held),
            SourceRecoveryFailure.selection_mismatch);
    }

    private static SourceRecoveryFingerprintVerdict ReadRows(SourceRecoveryManifest manifest,
        IReadOnlyList<SourceRecoveryRowFingerprint> baseline, bool immutable, CancellationToken token)
    {
        using var database = new ReadDatabase(Path.Combine(manifest.SourceRoot, "engine.db"), immutable, token);
        using (var identity = database.Command("PRAGMA application_id"))
            Require(Convert.ToInt64(identity.ExecuteScalar(), CultureInfo.InvariantCulture) == 0x50434556,
                SourceRecoveryFailure.row_storage_invalid);
        using (var version = database.Command("PRAGMA user_version"))
            Require(Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) == 7,
                SourceRecoveryFailure.row_storage_invalid);
        using (var schema = database.Command("PRAGMA table_info(workflow_runs)"))
        using (var reader = schema.ExecuteReader())
        {
            var names = new List<string>();
            while (reader.Read())
            {
                Require(names.Count < Columns.Length, SourceRecoveryFailure.row_storage_invalid);
                names.Add(reader.GetString(1));
            }
            Require(names.SequenceEqual(Columns), SourceRecoveryFailure.row_storage_invalid);
        }
        using (var invalid = database.Command("SELECT 1 FROM workflow_runs WHERE status IS NULL LIMIT 1"))
            Require(invalid.ExecuteScalar() is null, SourceRecoveryFailure.row_storage_invalid);
        // Bound the complete row before SELECT * materializes private bodies. Both queries share one snapshot.
        var lengthSql = "SELECT " + string.Join(",", Columns.Select(x => "length(CAST(\"" + x + "\" AS BLOB))")) +
            " FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled') ORDER BY id LIMIT 15";
        using (var sizes = database.Command(lengthSql))
        using (var reader = sizes.ExecuteReader())
        {
            var count = 0; long total = 0;
            while (reader.Read())
            {
                Require(++count <= 14, SourceRecoveryFailure.rows_mismatch);
                long row = 0;
                for (var i = 0; i < Columns.Length; i++) if (!reader.IsDBNull(i)) row += reader.GetInt64(i);
                // Bound retained input, then enforce the original exact encoded-byte bound below.
                Require(row <= MaximumRowBytes, SourceRecoveryFailure.row_bound);
                total += row;
                Require(total <= MaximumTotalRowBytes, SourceRecoveryFailure.row_bound);
            }
            Require(count == 14, SourceRecoveryFailure.rows_mismatch);
        }
        var fingerprints = new List<SourceRecoveryRowFingerprint>(); var encodedTotal = 0;
        using (var command = database.Command("""
            SELECT * FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled') ORDER BY id LIMIT 15
            """))
        using (var reader = command.ExecuteReader())
        {
            Require(reader.FieldCount == Columns.Length &&
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).SequenceEqual(Columns),
                SourceRecoveryFailure.row_storage_invalid);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); Require(fingerprints.Count < 14, SourceRecoveryFailure.rows_mismatch);
                var values = new object[Columns.Length]; reader.GetValues(values);
                Require(values[0] is string id && id.Length is > 0 and <= 512 && values[4] is string &&
                    values[5] is long revision && revision >= 1, SourceRecoveryFailure.row_storage_invalid);
                var encoded = EncodePythonTriplets(Columns, values);
                encodedTotal += encoded.Length;
                Require(encoded.Length <= MaximumRowBytes && encodedTotal <= MaximumTotalRowBytes, SourceRecoveryFailure.row_bound);
                fingerprints.Add(new((string)values[0], (string)values[4], (long)values[5], Digest(encoded)));
            }
        }
        Require(fingerprints.Count == 14 && fingerprints.SequenceEqual(baseline), SourceRecoveryFailure.rows_mismatch);
        database.Complete(); token.ThrowIfCancellationRequested();
        return new(true, System.Array.AsReadOnly(fingerprints.ToArray()), immutable
            ? "engine:mode-ro+immutable+query-only+read-transaction;no-sidecars;not-global-atomic"
            : "engine:mode-ro+query-only+read-transaction;committed-wal-visible;not-global-atomic");
    }

    internal static byte[] EncodePythonTriplets(IReadOnlyList<string> columns, IReadOnlyList<object> values)
    {
        Require(columns.Count == values.Count && columns.Count <= 64, SourceRecoveryFailure.row_storage_invalid);
        var output = new StringBuilder("[");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) output.Append(',');
            output.Append('['); AppendPythonString(output, columns[i]); output.Append(',');
            switch (values[i])
            {
                case string text:
                    AppendPythonString(output, "str"); output.Append(','); AppendPythonString(output, text); break;
                case long integer:
                    AppendPythonString(output, "int"); output.Append(','); output.Append(integer.ToString(CultureInfo.InvariantCulture)); break;
                case byte[] blob:
                    AppendPythonString(output, "blob"); output.Append(','); AppendPythonString(output, Convert.ToHexStringLower(blob)); break;
                case DBNull:
                    AppendPythonString(output, "NoneType"); output.Append(",null"); break;
                default:
                    // No Python-float approximation or coercion of unexpected SQLite storage classes.
                    throw Error(SourceRecoveryFailure.row_storage_invalid);
            }
            output.Append(']'); Require(output.Length <= MaximumRowBytes, SourceRecoveryFailure.row_bound);
        }
        output.Append(']'); Require(output.Length <= MaximumRowBytes, SourceRecoveryFailure.row_bound);
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static void AppendPythonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c is < ' ' or > '~') output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
            Require(output.Length <= MaximumRowBytes, SourceRecoveryFailure.row_bound);
        }
        output.Append('"');
    }

    private static bool Boolean(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        Require(item.ValueKind is JsonValueKind.True or JsonValueKind.False, SourceRecoveryFailure.manifest_invalid);
        return item.GetBoolean();
    }

    private static string HashFile(string path, long expectedLength, CancellationToken token)
    {
        CheckExistingPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Require(file.Length == expectedLength, SourceRecoveryFailure.pin_changed);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; long total = 0; int read;
        while ((read = file.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); total += read;
            Require(total <= expectedLength, SourceRecoveryFailure.pin_changed); hash.AppendData(buffer, 0, read);
        }
        Require(total == expectedLength && file.Length == expectedLength, SourceRecoveryFailure.pin_changed);
        CheckExistingPath(path);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class ReadDatabase : IDisposable
    {
        private readonly string path;
        private readonly bool immutable;
        private readonly CancellationToken token;
        private readonly Stopwatch deadline = Stopwatch.StartNew();
        private readonly Dictionary<string, string> fileHashes;
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly long beforeVersion;
        private bool completed;

        internal ReadDatabase(string path, bool immutable, CancellationToken token)
        {
            this.path = path; this.immutable = immutable; this.token = token;
            CheckSidecars();
            fileHashes = HashDatabaseFiles();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = immutable ? new Uri(path).AbsoluteUri + "?immutable=1" : path,
                Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
            };
            connection = new(builder.ToString());
            try
            {
                connection.Open();
                SQLitePCL.raw.sqlite3_enable_load_extension(connection.Handle, 0);
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000,
                    _ => token.IsCancellationRequested || deadline.Elapsed > TimeSpan.FromSeconds(8) ? 1 : 0, null);
                using (var queryOnly = connection.CreateCommand())
                { queryOnly.CommandTimeout = 1; queryOnly.CommandText = "PRAGMA query_only=ON"; queryOnly.ExecuteNonQuery(); }
                beforeVersion = DataVersion(); transaction = connection.BeginTransaction(deferred: true);
            }
            catch { connection.Dispose(); throw; }
        }

        internal SqliteCommand Command(string sql)
        {
            token.ThrowIfCancellationRequested(); Require(deadline.Elapsed <= TimeSpan.FromSeconds(8), SourceRecoveryFailure.store_bound);
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 1;
            command.CommandText = sql; return command;
        }

        internal byte[] ReadRecord(string kind, string id, int maximum)
        {
            using var command = Command("""
                SELECT typeof(body), length(CAST(body AS BLOB)), substr(CAST(body AS BLOB),1,$limit)
                FROM records WHERE kind=$kind AND id=$id LIMIT 2
                """);
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$limit", maximum + 1);
            using var reader = command.ExecuteReader();
            Require(reader.Read() && reader.GetString(0) == "text" && !reader.IsDBNull(1) &&
                reader.GetInt64(1) is > 0 && reader.GetInt64(1) <= maximum && !reader.IsDBNull(2),
                SourceRecoveryFailure.store_bound);
            var bytes = (byte[])reader.GetValue(2);
            Require(bytes.Length == reader.GetInt64(1) && !reader.Read(), SourceRecoveryFailure.store_bound);
            _ = StrictUtf8.GetString(bytes);
            return bytes;
        }

        internal void Complete()
        {
            token.ThrowIfCancellationRequested(); Require(deadline.Elapsed <= TimeSpan.FromSeconds(8), SourceRecoveryFailure.store_bound);
            transaction.Rollback(); completed = true;
            Require(DataVersion() == beforeVersion, SourceRecoveryFailure.store_changed);
            CheckSidecars();
            Require(fileHashes.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(
                HashDatabaseFiles().OrderBy(x => x.Key, StringComparer.Ordinal)), SourceRecoveryFailure.store_changed);
        }

        private long DataVersion()
        {
            using var command = connection.CreateCommand(); command.CommandTimeout = 1; command.CommandText = "PRAGMA data_version";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private void CheckSidecars()
        {
            CheckExistingPath(path);
            Require(new FileInfo(path).Length is > 0 and <= MaximumDatabaseBytes, SourceRecoveryFailure.store_bound);
            Require(!EntryExists(path + "-journal"), SourceRecoveryFailure.store_sidecars_invalid);
            var wal = EntryExists(path + "-wal"); var shm = EntryExists(path + "-shm");
            Require(immutable ? !wal && !shm : wal == shm, SourceRecoveryFailure.store_sidecars_invalid);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (EntryExists(path + suffix))
                {
                    CheckExistingPath(path + suffix);
                    Require(new FileInfo(path + suffix).Length <= MaximumDatabaseBytes, SourceRecoveryFailure.store_bound);
                }
        }

        private static bool EntryExists(string file)
        {
            try { _ = File.GetAttributes(file); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private Dictionary<string, string> HashDatabaseFiles()
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var suffix in new[] { "", "-wal" })
            {
                var file = path + suffix;
                if (File.Exists(file)) hashes.Add(suffix, HashFile(file, new FileInfo(file).Length, token));
            }
            // SHM contents contain volatile read-lock bookkeeping; membership/path checks are still mandatory.
            if (File.Exists(path + "-shm")) hashes.Add("-shm", "existing-regular-shm");
            return hashes;
        }

        public void Dispose()
        {
            if (!completed) { try { transaction.Rollback(); } catch (SqliteException) { } }
            transaction.Dispose(); connection.Dispose();
            if (completed)
            {
                CheckSidecars();
                Require(fileHashes.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(
                    HashDatabaseFiles().OrderBy(x => x.Key, StringComparer.Ordinal)), SourceRecoveryFailure.store_changed);
            }
        }
    }
}
