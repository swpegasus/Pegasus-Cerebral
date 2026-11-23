using System.Text.Json;
using Cerebral.Core;
using Cerebral.PersonalityServicePlugin;

/// <summary>Actual package commands with a deterministic scoped transfer host and owned files; no live Core store or Worker.</summary>
internal static class PendingTransferTests
{
    private const string Get = "personality.preferences.transfer.get";
    private const string Save = "personality.preferences.transfer.save";
    private const string CurrentPrepare = "personality.preferences.transfer.prepare";
    private const string Prepare = "personality.preferences.pending.prepare";
    private const string Apply = "personality.preferences.pending.apply";

    internal static async Task RunAsync(string directory)
    {
        var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build", "Plugins", "SettingsPresets"));
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!requested.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfer fixtures must stay in the existing Plugins build component.");
        var fixture = Path.Combine(requested, "transfer-working");
        if (Directory.Exists(fixture)) throw new IOException("Prior transfer fixtures exist; inspect them before rerunning.");
        Directory.CreateDirectory(fixture);
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            var rejected = false;
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
                IOException or JsonException or NotSupportedException) { rejected = true; }
            Check(rejected, label);
        }
        var success = false;
        try
        {
            foreach (var purpose in new[] { SettingsTransferPurpose.Complete, SettingsTransferPurpose.Local })
            {
                using var context = new Fixture(Path.Combine(fixture, purpose));
                var selectedId = purpose + "-selected";
                var definition = new PersonalityCustomDefinition(purpose + "-style", "Current editable style", "Use precise bounded fixture answers.");
                context.Central.Add(selectedId, purpose, Document(Values(true, definition.Id, definition,
                    new PersonalityCustomDefinition("unused-definition", "Unused editable choice", "Do not materialize this unselected fixture definition.")), 2, "1.0.2"));
                context.Central.Add("keep-other", SettingsTransferPurpose.Portable, Document(Pair(false, "cerebral"), 1, "0.9.0"));
                var observed = await context.Plugin.ExecuteAsync(Get, J(new { }), default);
                Check(observed is PluginSettingsTransferState transfer && transfer.SchemaVersion == 2 &&
                    transfer.PendingPurposes.Count == 2 && transfer.State.Pending.Count == 2,
                    purpose + " get returns the purpose-aware transfer envelope");
                var before = J(context.Central.Snapshot);
                var prepared = await PreparePending(context, selectedId, purpose);
                Check(Fields(prepared, "schemaVersion", "pendingId", "purpose", "settingsRevision", "pendingSha256",
                    "valuesSha256", "values", "preset", "sources") && prepared.GetProperty("schemaVersion").GetInt32() == 1 &&
                    prepared.GetProperty("pendingId").GetString() == selectedId && prepared.GetProperty("purpose").GetString() == purpose &&
                    IsHash(prepared.GetProperty("pendingSha256")) && IsHash(prepared.GetProperty("valuesSha256")),
                    purpose + " preparation has the closed identity/digest reply");
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && context.Central.TransferSaves == 0,
                    purpose + " preparation leaves active pending and purpose state unchanged");
                Check(File.Exists(context.SourcePath) && prepared.GetProperty("preset").GetProperty("id").GetString() == definition.Id &&
                    prepared.GetProperty("preset").GetProperty("text").GetString() == definition.Text &&
                    prepared.GetProperty("sources").GetProperty("retention").GetProperty("retainedCustomVersions").GetInt32() == 1 &&
                    prepared.GetProperty("sources").GetProperty("retention").GetProperty("customPresetCount").GetInt32() == 1,
                    purpose + " preparation writes only the selected exact immutable source");
                var applied = await context.Plugin.ExecuteAsync(Apply, J(ApplyArguments(prepared)), default);
                Check(applied is PluginSettingsTransferSaveResult result && result.Changed && result.State.SchemaVersion == 2 &&
                    result.State.State.Pending.Count == 1 && result.State.State.Pending[0].Id == "keep-other" &&
                    result.State.PendingPurposes.Single().PendingId == "keep-other" &&
                    result.State.PendingPurposes.Single().Purpose == SettingsTransferPurpose.Portable,
                    purpose + " apply removes exactly one selected pending document and purpose");
                Check(context.Central.Snapshot.State.Pending.Single().Document.SourcePackageVersion == "0.9.0" &&
                    context.Central.Snapshot.State.Pending.Single().Document.SettingsVersion == 1,
                    purpose + " apply preserves unrelated pending provenance");
                var request = Submitted(context);
                Check(JsonElement.DeepEquals(context.Central.Snapshot.State.Active!.Values, prepared.GetProperty("values")) &&
                    request.Save.Document.SourcePackageVersion == PersonalitySettingsPolicy.PackageVersion &&
                    request.Save.Document.SettingsVersion == PersonalitySettingsPolicy.SettingsVersion &&
                    Fields(request.Save.Document.Values, "enabled", "selectedPresetId", "customPresets"),
                    purpose + " apply submits the bound current-version full canonical proposal");
                Check(NoOwnerClaims(request) && context.Central.LastPurpose == purpose,
                    purpose + " service policy remains authoritative without owner claims or projections");
                Check(context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    purpose + " pending flow never falls back to legacy access");
            }

            using (var context = new Fixture(Path.Combine(fixture, "older")))
            {
                context.Central.Add("old-pair", SettingsTransferPurpose.Complete, Document(Pair(true, "succinct"), 1, "1.0.2"));
                var before = J(context.Central.Snapshot);
                var prepared = await PreparePending(context, "old-pair", SettingsTransferPurpose.Complete);
                Check(Fields(prepared.GetProperty("values"), "enabled", "selectedPresetId", "customPresets") &&
                    prepared.GetProperty("values").GetProperty("customPresets").GetArrayLength() == 0 &&
                    JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath),
                    "explicit older version-one complete input prepares a canonical two draft without retagging pending or appending builtin");
                _ = await context.Plugin.ExecuteAsync(Apply, J(ApplyArguments(prepared)), default);
                var request = Submitted(context);
                Check(request.Save.Document.SettingsVersion == 2 &&
                    request.Save.Document.SourcePackageVersion == PersonalitySettingsPolicy.PackageVersion &&
                    context.Central.Snapshot.State.Pending.Count == 0,
                    "older input applies through current package binding rather than forwarding original source version");
            }

            using (var context = new Fixture(Path.Combine(fixture, "portable")))
            {
                var retained = new PersonalityCustomDefinition("destination", "Destination choice", "Keep destination fixture preferences.");
                context.Central.SetActive(Values(false, retained.Id, retained));
                context.Central.Add("portable-pair", SettingsTransferPurpose.Portable, Document(Pair(true, "succinct"), 1, "1.0.2"));
                var before = J(context.Central.Snapshot);
                var prepared = await PreparePending(context, "portable-pair", SettingsTransferPurpose.Portable);
                Check(PersonalitySettingsCodec.Read(prepared.GetProperty("values")).CustomPresets.Single() == retained &&
                    JsonElement.DeepEquals(before, J(context.Central.Snapshot)),
                    "portable preparation computes the merged canonical draft while leaving destination state untouched");
                _ = await context.Plugin.ExecuteAsync(Apply, J(ApplyArguments(prepared)), default);
                var request = Submitted(context);
                Check(Fields(request.Save.Document.Values, "enabled", "selectedPresetId") &&
                    context.Central.LastPurpose == SettingsTransferPurpose.Portable,
                    "portable apply submits the portable pair rather than the merged full draft");
                var activeDocument = context.Central.Snapshot.State.Active ?? throw new InvalidOperationException("Fixture portable active document is absent.");
                var active = PersonalitySettingsCodec.Read(activeDocument.Values);
                Check(active.Enabled && active.SelectedPresetId == "succinct" && active.CustomPresets.Single() == retained &&
                    JsonElement.DeepEquals(activeDocument.Values, prepared.GetProperty("values")),
                    "portable Core-like revalidation preserves destination definitions and removes only its selected item");
                Check(NoOwnerClaims(request) && context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    "portable transfer submits no owner claim and uses no legacy fallback");
            }

            using (var context = new Fixture(Path.Combine(fixture, "disabled-clear")))
            {
                context.Central.SetActive(Values(true, "previous", new PersonalityCustomDefinition("previous", "Previous", "Previous editable fixture style.")));
                context.Central.Add("disabled-clear", SettingsTransferPurpose.Complete,
                    Document(Values(false, "missing-desired"), 2, "1.0.2"));
                var before = J(context.Central.Snapshot);
                var prepared = await PreparePending(context, "disabled-clear", SettingsTransferPurpose.Complete);
                Check(prepared.GetProperty("preset").ValueKind == JsonValueKind.Null &&
                    prepared.GetProperty("values").GetProperty("customPresets").GetArrayLength() == 0 &&
                    JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath),
                    "disabled complete clear preserves an unavailable desired ID without manufacturing source or default");
                var arguments = ApplyArguments(prepared);
                Check(arguments["expectedSourceId"] is null && arguments["expectedSourceRevision"] is null &&
                    arguments["expectedSourceSha256"] is null, "disabled missing-source apply uses the complete null source triple");
                await Reject(async () => { await context.Plugin.ExecuteAsync(Apply,
                    J(new Dictionary<string, object?>(arguments) { ["expectedSourceRevision"] = 1L }), default); },
                    "disabled apply rejects a mixed-null source triple");
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && context.Central.TransferSaves == 0,
                    "mixed-null disabled refusal preserves active and pending settings");
                var applied = await context.Plugin.ExecuteAsync(Apply, J(arguments), default);
                var active = PersonalitySettingsCodec.Read(context.Central.Snapshot.State.Active!.Values);
                Check(applied is PluginSettingsTransferSaveResult result && result.Changed && !active.Enabled &&
                    active.SelectedPresetId == "missing-desired" && active.CustomPresets.Count == 0 &&
                    context.Central.Snapshot.State.Pending.Count == 0 && !File.Exists(context.SourcePath),
                    "disabled complete admission clears current definitions and selected pending without requiring a source");
            }

            using (var context = new Fixture(Path.Combine(fixture, "no-id-save")))
            {
                context.Central.SetActive(Values(true, "cerebral"));
                context.Central.Add("complete-retained", SettingsTransferPurpose.Complete,
                    Document(Values(true, "cerebral"), 2, "1.0.2"));
                context.Central.Add("portable-retained", SettingsTransferPurpose.Portable,
                    Document(Pair(false, "succinct"), 1, "0.9.0"));
                var pending = J(context.Central.Snapshot.State.Pending);
                var purposes = J(context.Central.Snapshot.PendingPurposes);
                var disabled = await context.Plugin.ExecuteAsync(Save, J(new
                { schemaVersion = 2, expectedRevision = context.Central.Snapshot.State.Revision, values = Values(false, "missing-desired") }), default);
                Check(disabled is PluginSettingsTransferSaveResult result && result.Changed &&
                    !PersonalitySettingsCodec.Read(result.State.State.Active!.Values).Enabled &&
                    JsonElement.DeepEquals(pending, J(result.State.State.Pending)) &&
                    JsonElement.DeepEquals(purposes, J(result.State.PendingPurposes)) && !File.Exists(context.SourcePath),
                    "no-ID transfer disable preserves every deferred input and purpose without requiring source");
                var request = Submitted(context);
                Check(request.Save.PendingId is null && NoOwnerClaims(request) &&
                    request.Save.Document.SourcePackageVersion == PersonalitySettingsPolicy.PackageVersion &&
                    context.Central.LastPurpose == SettingsTransferPurpose.Local && context.Central.LegacyGets == 0 &&
                    context.Central.LegacySaves == 0, "no-ID transfer save uses bound local policy with no pending removal or owner claims");
                var before = J(context.Central.Snapshot);
                var saves = context.Central.TransferSaves;
                await Reject(async () => { await context.Plugin.ExecuteAsync(Save, J(new
                { schemaVersion = 2, expectedRevision = context.Central.Snapshot.State.Revision,
                    values = Values(true, "unprepared", new PersonalityCustomDefinition("unprepared", "Unprepared", "Unprepared editable fixture style.")) }), default); },
                    "no-ID transfer enable rejects an unprepared full definition");
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath) &&
                    context.Central.TransferSaves == saves, "refused no-ID enable preserves pending purposes and appends no source");
            }

            using (var context = new Fixture(Path.Combine(fixture, "portable-empty-current-preparation")))
            {
                var definition = new PersonalityCustomDefinition("restored-current", "Restored current", "Preserve the restored current fixture style.");
                context.Central.SetActive(Values(true, definition.Id, definition));
                context.Central.Add("portable-empty", SettingsTransferPurpose.Portable, Document(J(new { }), 2, "1.0.2"));
                context.Central.Add("complete-retained", SettingsTransferPurpose.Complete, Document(Values(false, "cerebral"), 2, "1.0.2"));
                var original = context.Central.Snapshot.State.Active!.Values;
                var originalHash = PersonalityContributionCodec.Hash(original);
                var portableOriginal = context.Central.Snapshot.State.Pending.Single(item => item.Id == "portable-empty").Document;
                var reviewed = context.Plugin.Admit(portableOriginal, original, portable: true);
                var pending = J(context.Central.Snapshot.State.Pending);
                var purposes = J(context.Central.Snapshot.PendingPurposes);
                Check(reviewed.Accepted && PersonalityContributionCodec.Hash(reviewed.Values) == originalHash &&
                    !File.Exists(context.SourcePath), "portable empty projection reviews original enabled values without implicit source preparation");

                var disabled = PersonalitySettingsCodec.Write(PersonalitySettingsCodec.Read(original) with { Enabled = false });
                var disabledResult = await context.Plugin.ExecuteAsync(Save, J(new
                { schemaVersion = 2, expectedRevision = context.Central.Snapshot.State.Revision, values = disabled }), default);
                Check(disabledResult is PluginSettingsTransferSaveResult disabledSave && disabledSave.Changed &&
                    JsonElement.DeepEquals(disabledSave.State.State.Active!.Values, disabled) &&
                    JsonElement.DeepEquals(pending, J(disabledSave.State.State.Pending)) &&
                    JsonElement.DeepEquals(purposes, J(disabledSave.State.PendingPurposes)) && !File.Exists(context.SourcePath),
                    "portable empty review can disable current selection without draining either pending purpose or preparing a source");

                // A native instructions CAS/readback may occur while disabled. It is outside this package
                // fixture; disabled central state is a prerequisite, not evidence of actual native cleanup.
                var beforePreparation = J(context.Central.Snapshot);
                var savesBeforePreparation = context.Central.TransferSaves;
                var currentPrepared = await Command(context.Plugin, CurrentPrepare, await CurrentPrepareArguments(context));
                Check(Fields(currentPrepared, "schemaVersion", "settingsRevision", "valuesSha256", "values", "preset", "sources") &&
                    currentPrepared.GetProperty("schemaVersion").GetInt32() == 1 &&
                    currentPrepared.GetProperty("settingsRevision").GetInt64() == context.Central.Snapshot.State.Revision &&
                    JsonElement.DeepEquals(currentPrepared.GetProperty("values"), disabled) &&
                    currentPrepared.GetProperty("valuesSha256").GetString() == PersonalityContributionCodec.Hash(disabled),
                    "current transfer preparation returns the closed canonical disabled-values and revision reply");
                Check(currentPrepared.GetProperty("preset").GetProperty("id").GetString() == definition.Id &&
                    currentPrepared.GetProperty("preset").GetProperty("text").GetString() == definition.Text &&
                    currentPrepared.GetProperty("sources").GetProperty("retention").GetProperty("retainedCustomVersions").GetInt32() == 1 &&
                    JsonElement.DeepEquals(beforePreparation, J(context.Central.Snapshot)) &&
                    context.Central.TransferSaves == savesBeforePreparation,
                    "current transfer preparation appends only the selected immutable source and preserves all central state");

                var restored = await context.Plugin.ExecuteAsync(Save, J(new
                { schemaVersion = 2, expectedRevision = context.Central.Snapshot.State.Revision, values = original }), default);
                Check(restored is PluginSettingsTransferSaveResult restoredSave && restoredSave.Changed &&
                    PersonalityContributionCodec.Hash(restoredSave.State.State.Active!.Values) == originalHash &&
                    JsonElement.DeepEquals(pending, J(restoredSave.State.State.Pending)) &&
                    JsonElement.DeepEquals(purposes, J(restoredSave.State.PendingPurposes)) && Submitted(context).Save.PendingId is null,
                    "exact prepared source permits no-ID restoration of original enabled values with all pending purposes intact");

                var beforeReview = J(context.Central.Snapshot);
                var sourceBytes = File.ReadAllBytes(context.SourcePath);
                var prepared = await PreparePending(context, "portable-empty", SettingsTransferPurpose.Portable);
                Check(prepared.GetProperty("valuesSha256").GetString() == originalHash &&
                    JsonElement.DeepEquals(prepared.GetProperty("values"), original) &&
                    prepared.GetProperty("settingsRevision").GetInt64() == context.Central.Snapshot.State.Revision &&
                    prepared.GetProperty("preset").GetProperty("sourceId").GetString() == currentPrepared.GetProperty("preset").GetProperty("sourceId").GetString() &&
                    JsonElement.DeepEquals(beforeReview, J(context.Central.Snapshot)) &&
                    File.ReadAllBytes(context.SourcePath).SequenceEqual(sourceBytes),
                    "latest-revision portable empty review preserves the original full-values hash and exact current prepared source");
                var applied = await context.Plugin.ExecuteAsync(Apply, J(ApplyArguments(prepared)), default);
                Check(applied is PluginSettingsTransferSaveResult appliedSave && appliedSave.Changed &&
                    PersonalityContributionCodec.Hash(appliedSave.State.State.Active!.Values) == originalHash &&
                    appliedSave.State.State.Pending.Single().Id == "complete-retained" &&
                    appliedSave.State.PendingPurposes.Single().PendingId == "complete-retained" &&
                    appliedSave.State.PendingPurposes.Single().Purpose == SettingsTransferPurpose.Complete &&
                    appliedSave.State.State.Pending.Single().Document.SourcePackageVersion == "1.0.2",
                    "portable empty apply consumes only selected portable input while preserving original enabled values and unrelated complete provenance");
                Check(Fields(Submitted(context).Save.Document.Values) && NoOwnerClaims(Submitted(context)) &&
                    context.Central.LastPurpose == SettingsTransferPurpose.Portable && context.Central.LegacyGets == 0 &&
                    context.Central.LegacySaves == 0, "portable empty current-preparation flow submits the empty projection without owner claims or legacy access");
            }

            using (var context = new Fixture(Path.Combine(fixture, "current-preparation-mismatches")))
            {
                var definition = new PersonalityCustomDefinition("current-cas", "Current CAS", "Protect current preparation with both revisions.");
                context.Central.SetActive(Values(true, definition.Id, definition));
                context.Central.Add("complete-retained", SettingsTransferPurpose.Complete, Document(Values(false, "cerebral"), 2, "1.0.2"));
                var before = J(context.Central.Snapshot);
                var arguments = await CurrentPrepareArguments(context);
                foreach (var (field, value) in new (string, object?)[]
                {
                    ("expectedRevision", context.Central.Snapshot.State.Revision + 1),
                    ("expectedStoreRevision", (long)arguments["expectedStoreRevision"]! + 1),
                    ("expectedStoreSha256", new string('0', 64))
                })
                {
                    await Reject(async () => { await context.Plugin.ExecuteAsync(CurrentPrepare,
                        J(new Dictionary<string, object?>(arguments) { [field] = value }), default); },
                        "current transfer preparation refuses stale/mismatched " + field);
                    Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath) &&
                        context.Central.TransferSaves == 0 && context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                        "refused current preparation preserves all central state and source absence: " + field);
                }
            }

            foreach (var absent in new[] { true, false })
            {
                using var context = new Fixture(Path.Combine(fixture, absent ? "current-absent" : "current-future"));
                context.Central.SetRetainedActive(absent ? null : Document(Values(false, "cerebral"), 3, "3.0.0"));
                context.Central.Add("complete-retained", SettingsTransferPurpose.Complete, Document(Values(false, "cerebral"), 2, "1.0.2"));
                var before = J(context.Central.Snapshot);
                await Reject(async () => { await context.Plugin.ExecuteAsync(CurrentPrepare,
                    J(await CurrentPrepareArguments(context)), default); },
                    "current transfer preparation refuses " + (absent ? "absent active values" : "future active schema"));
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath) &&
                    context.Central.TransferSaves == 0 && context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    "unsupported current preparation retains pending purpose and invents no active/default/source");
            }

            using (var context = new Fixture(Path.Combine(fixture, "current-missing-definition")))
            {
                context.Central.SetActive(Values(false, "missing-current"));
                var before = J(context.Central.Snapshot);
                await Reject(async () => { await context.Plugin.ExecuteAsync(CurrentPrepare,
                    J(await CurrentPrepareArguments(context)), default); },
                    "current transfer preparation remains strict for a disabled missing desired definition");
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath) &&
                    context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    "strict missing-current-source refusal manufactures no source or default and changes no preferences");
            }

            using (var context = new Fixture(Path.Combine(fixture, "current-preparation-race")))
            {
                var definition = new PersonalityCustomDefinition("current-race", "Current race", "Retain the unused current preparation source.");
                context.Central.SetActive(Values(true, definition.Id, definition));
                context.Central.Add("complete-retained", SettingsTransferPurpose.Complete, Document(Values(false, "cerebral"), 2, "1.0.2"));
                var active = J(context.Central.Snapshot.State.Active!);
                var arguments = await CurrentPrepareArguments(context);
                context.Central.AfterTransferGet = () => context.Central.Add("racing-other", SettingsTransferPurpose.Local,
                    Document(Values(false, "cerebral"), 2, "1.0.2"));
                await Reject(async () => { await context.Plugin.ExecuteAsync(CurrentPrepare, J(arguments), default); },
                    "current transfer preparation refuses a post-read central revision race");
                var retained = await Command(context.Plugin, "personality.get", new { id = definition.Id, revision = 1 });
                Check(retained.GetProperty("text").GetString() == definition.Text &&
                    JsonElement.DeepEquals(active, J(context.Central.Snapshot.State.Active!)) &&
                    context.Central.Snapshot.State.Pending.Count == 2 && context.Central.Snapshot.PendingPurposes.Count == 2 &&
                    context.Central.TransferSaves == 0 && context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    "current preparation race retains its unused immutable append and both pending purposes without acknowledgement");
            }

            using (var context = new Fixture(Path.Combine(fixture, "mismatches")))
            {
                context.Central.Add("selected", SettingsTransferPurpose.Complete,
                    Document(Values(true, "checked", new PersonalityCustomDefinition("checked", "Checked", "Retain exact fixture source identity.")), 2, "1.0.2"));
                var original = J(context.Central.Snapshot);
                var prepareArguments = await PrepareArguments(context, "selected", SettingsTransferPurpose.Complete);
                foreach (var (field, value) in new (string, object?)[]
                {
                    ("expectedRevision", context.Central.Snapshot.State.Revision + 1),
                    ("expectedPurpose", SettingsTransferPurpose.Portable),
                    ("expectedStoreRevision", (long)prepareArguments["expectedStoreRevision"]! + 1),
                    ("expectedStoreSha256", new string('0', 64))
                })
                {
                    var wrong = new Dictionary<string, object?>(prepareArguments) { [field] = value };
                    await Reject(async () => { await context.Plugin.ExecuteAsync(Prepare, J(wrong), default); },
                        "pending prepare refuses stale/mismatched " + field);
                    Check(JsonElement.DeepEquals(original, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath),
                        "refused pending preparation preserves state and writes no source: " + field);
                }
                var prepared = await PreparePending(context, "selected", SettingsTransferPurpose.Complete);
                var preparedState = J(context.Central.Snapshot);
                var sourceBytes = File.ReadAllBytes(context.SourcePath);
                var validApply = ApplyArguments(prepared);
                foreach (var (field, value) in new (string, object?)[]
                {
                    ("expectedRevision", context.Central.Snapshot.State.Revision + 1),
                    ("expectedPurpose", SettingsTransferPurpose.Local),
                    ("pendingSha256", new string('0', 64)),
                    ("valuesSha256", new string('0', 64)),
                    ("expectedSourceId", "personality/v1/missing/1/" + new string('0', 64)),
                    ("expectedSourceRevision", prepared.GetProperty("preset").GetProperty("revision").GetInt64() + 1),
                    ("expectedSourceSha256", new string('0', 64)),
                    ("pendingId", "not-retained")
                })
                {
                    var wrong = new Dictionary<string, object?>(validApply) { [field] = value };
                    await Reject(async () => { await context.Plugin.ExecuteAsync(Apply, J(wrong), default); },
                        "pending apply refuses stale/mismatched " + field);
                    Check(JsonElement.DeepEquals(preparedState, J(context.Central.Snapshot)) &&
                        File.ReadAllBytes(context.SourcePath).SequenceEqual(sourceBytes) && context.Central.TransferSaves == 0,
                        "refused pending apply preserves active pending purpose and source: " + field);
                }
                var noSource = new Dictionary<string, object?>(validApply)
                    { ["expectedSourceId"] = null, ["expectedSourceRevision"] = null, ["expectedSourceSha256"] = null };
                await Reject(async () => { await context.Plugin.ExecuteAsync(Apply, J(noSource), default); },
                    "enabled pending apply rejects an all-null source triple");
                Check(JsonElement.DeepEquals(preparedState, J(context.Central.Snapshot)) &&
                    File.ReadAllBytes(context.SourcePath).SequenceEqual(sourceBytes) && context.Central.TransferSaves == 0,
                    "enabled null-source refusal preserves the exact prepared bytes and pending state");
                context.Central.Add("concurrent", SettingsTransferPurpose.Local, Document(Values(false, "cerebral"), 2, "1.0.2"));
                var advanced = J(context.Central.Snapshot);
                await Reject(async () => { await context.Plugin.ExecuteAsync(Apply, J(validApply), default); },
                    "apply refuses an actually advanced central revision after preparation");
                Check(JsonElement.DeepEquals(advanced, J(context.Central.Snapshot)) && context.Central.TransferSaves == 0,
                    "stale apply retains both selected and unrelated new pending inputs");
            }

            using (var context = new Fixture(Path.Combine(fixture, "rejected")))
            {
                context.Central.Add("future", SettingsTransferPurpose.Complete, Document(Values(true, "cerebral"), 3, "3.0.0"));
                context.Central.Add("invalid", SettingsTransferPurpose.Local,
                    Document(J(new { enabled = true, selectedPresetId = "cerebral", customPresets = Array.Empty<object>(), unknown = true }), 2, "1.0.2"));
                var before = J(context.Central.Snapshot);
                foreach (var (id, purpose) in new[]
                { ("future", SettingsTransferPurpose.Complete), ("invalid", SettingsTransferPurpose.Local), ("absent", SettingsTransferPurpose.Complete) })
                {
                    await Reject(async () => { await PreparePending(context, id, purpose); },
                        "unknown/rejected pending preparation refuses: " + id);
                    await Reject(async () => { await context.Plugin.ExecuteAsync(Apply,
                        J(UnpreparedApplyArguments(context.Central.Snapshot.State.Revision, id, purpose)), default); },
                        "unknown/rejected pending apply refuses: " + id);
                }
                Check(JsonElement.DeepEquals(before, J(context.Central.Snapshot)) && !File.Exists(context.SourcePath) &&
                    context.Central.TransferSaves == 0 && context.Central.LegacyGets == 0 && context.Central.LegacySaves == 0,
                    "unknown/future/rejected pending remains retained without automatic drain or legacy fallback");
            }

            using (var context = new Fixture(Path.Combine(fixture, "prepare-race")))
            {
                context.Central.Add("race-selected", SettingsTransferPurpose.Complete,
                    Document(Values(true, "race-source", new PersonalityCustomDefinition("race-source", "Race", "Keep the unused immutable race source.")), 2, "1.0.2"));
                var active = J(context.Central.Snapshot.State.Active!);
                var arguments = await PrepareArguments(context, "race-selected", SettingsTransferPurpose.Complete);
                context.Central.AfterTransferGet = () => context.Central.Add("racing-other", SettingsTransferPurpose.Local,
                    Document(Values(false, "cerebral"), 2, "1.0.2"));
                await Reject(async () => { await context.Plugin.ExecuteAsync(Prepare, J(arguments), default); },
                    "pending preparation refuses central revision change without acknowledging stale preparation");
                var retained = await Command(context.Plugin, "personality.get", new { id = "race-source", revision = 1 });
                Check(retained.GetProperty("text").GetString() == "Keep the unused immutable race source." &&
                    JsonElement.DeepEquals(active, J(context.Central.Snapshot.State.Active!)) &&
                    context.Central.Snapshot.State.Pending.Count == 2 && context.Central.TransferSaves == 0,
                    "unused preparation append is retained while active and both pending inputs survive the race");
            }

            foreach (var throwing in new[] { false, true })
            {
                var path = Path.Combine(fixture, throwing ? "throwing-transfer" : "absent-transfer");
                using var plugin = new PersonalityServicePlugin();
                var central = new MemoryTransfer(plugin);
                IScopedPluginSettings access = throwing ? new ThrowingTransfer(central) : new LegacyOnly(central);
                plugin.Initialize(new Host(path, access));
                await Reject(async () => { await plugin.ExecuteAsync(Get, J(new { }), default); },
                    "optional transfer get refuses " + (throwing ? "an exposed throwing host" : "an absent interface"));
                await Reject(async () => { await plugin.ExecuteAsync(Save, J(new
                    { schemaVersion = 2, expectedRevision = central.Snapshot.State.Revision, values = Values(false, "cerebral") }), default); },
                    "optional no-ID transfer save has no legacy capability fallback");
                await Reject(async () => { await plugin.ExecuteAsync(CurrentPrepare, J(new
                    { schemaVersion = 1, expectedRevision = central.Snapshot.State.Revision,
                        expectedStoreRevision = 0, expectedStoreSha256 = new string('0', 64) }), default); },
                    "optional current transfer preparation has no legacy capability fallback");
                await Reject(async () => { await plugin.ExecuteAsync(Prepare, J(new
                {
                    schemaVersion = 1, expectedRevision = central.Snapshot.State.Revision, pendingId = "missing",
                    expectedPurpose = SettingsTransferPurpose.Complete, expectedStoreRevision = 0, expectedStoreSha256 = new string('0', 64)
                }), default); }, "optional pending preparation has no legacy capability fallback");
                await Reject(async () => { await plugin.ExecuteAsync(Apply,
                    J(UnpreparedApplyArguments(central.Snapshot.State.Revision, "missing", SettingsTransferPurpose.Complete)), default); },
                    "optional pending apply has no legacy capability fallback");
                Check(central.LegacyGets == 0 && central.LegacySaves == 0 && central.TransferSaves == 0 &&
                    !File.Exists(Path.Combine(path, PersonalityStore.FileName)),
                    "unsupported transfer capability preserves central state and source bytes without legacy access");
            }

            using (var context = new Fixture(Path.Combine(fixture, "normal-shapes")))
            {
                var preferences = await Command(context.Plugin, "personality.preferences.get", new { });
                var alias = await Command(context.Plugin, "personality.settings", new { });
                Check(Fields(preferences, "schemaVersion", "settingsVersion", "revision", "values", "state", "provider") &&
                    JsonElement.DeepEquals(preferences, alias), "normal preference get and alias keep their closed schema-two shape");
                var saved = await Command(context.Plugin, "personality.preferences.save", new
                { schemaVersion = 2, expectedRevision = context.Central.Snapshot.State.Revision,
                    values = Values(false, "staged", new PersonalityCustomDefinition("staged", "Staged", "Prepare before enabling fixture style.")) });
                Check(Fields(saved, "state", "changed"), "normal preference save retains its central save-result shape");
                var sources = await Command(context.Plugin, "personality.sources.get", new { });
                var prepared = await Command(context.Plugin, "personality.source.prepare", new
                {
                    schemaVersion = 1, expectedSettingsRevision = context.Central.Snapshot.State.Revision,
                    expectedStoreRevision = sources.GetProperty("revision").GetInt64(),
                    expectedStoreSha256 = sources.GetProperty("sha256").GetString()
                });
                Check(Fields(prepared, "schemaVersion", "settingsRevision", "preset", "sources") &&
                    !PersonalitySettingsCodec.Read(context.Central.Snapshot.State.Active!.Values).Enabled,
                    "normal source preparation retains its reply shape and can run while disabled");
                var transfer = await context.Plugin.ExecuteAsync(Get, J(new { }), default);
                Check(transfer is PluginSettingsTransferState result && result.SchemaVersion == 2 &&
                    Fields(J(result), "schemaVersion", "state", "pendingPurposes"),
                    "new transfer get returns the existing envelope rather than changing normal route replies");
            }

            success = true;
            Console.WriteLine($"PASS: {checks} pending transfer package checks; deterministic scoped transfer/service policy and real owned files; no actual Core SQLite/Worker/pipe/network/model.");
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (success && resolved.StartsWith(requested + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(resolved), requested, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved) == "transfer-working")
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task<Dictionary<string, object?>> PrepareArguments(Fixture context, string id, string purpose)
    {
        var sources = await Command(context.Plugin, "personality.sources.get", new { });
        return new() { ["schemaVersion"] = 1, ["expectedRevision"] = context.Central.Snapshot.State.Revision,
            ["pendingId"] = id, ["expectedPurpose"] = purpose,
            ["expectedStoreRevision"] = sources.GetProperty("revision").GetInt64(),
            ["expectedStoreSha256"] = sources.GetProperty("sha256").GetString() };
    }
    private static async Task<Dictionary<string, object?>> CurrentPrepareArguments(Fixture context)
    {
        var sources = await Command(context.Plugin, "personality.sources.get", new { });
        return new() { ["schemaVersion"] = 1, ["expectedRevision"] = context.Central.Snapshot.State.Revision,
            ["expectedStoreRevision"] = sources.GetProperty("revision").GetInt64(),
            ["expectedStoreSha256"] = sources.GetProperty("sha256").GetString() };
    }
    private static async Task<JsonElement> PreparePending(Fixture context, string id, string purpose) =>
        await Command(context.Plugin, Prepare, await PrepareArguments(context, id, purpose));
    private static Dictionary<string, object?> ApplyArguments(JsonElement prepared)
    {
        var preset = prepared.GetProperty("preset");
        return new()
        {
            ["schemaVersion"] = 1, ["expectedRevision"] = prepared.GetProperty("settingsRevision").GetInt64(),
            ["pendingId"] = prepared.GetProperty("pendingId").GetString(), ["expectedPurpose"] = prepared.GetProperty("purpose").GetString(),
            ["pendingSha256"] = prepared.GetProperty("pendingSha256").GetString(), ["valuesSha256"] = prepared.GetProperty("valuesSha256").GetString(),
            ["expectedSourceId"] = preset.ValueKind == JsonValueKind.Null ? null : preset.GetProperty("sourceId").GetString(),
            ["expectedSourceRevision"] = preset.ValueKind == JsonValueKind.Null ? null : preset.GetProperty("revision").GetInt64(),
            ["expectedSourceSha256"] = preset.ValueKind == JsonValueKind.Null ? null : preset.GetProperty("sha256").GetString()
        };
    }
    private static Dictionary<string, object?> UnpreparedApplyArguments(long revision, string id, string purpose) => new()
    {
        ["schemaVersion"] = 1, ["expectedRevision"] = revision, ["pendingId"] = id, ["expectedPurpose"] = purpose,
        ["pendingSha256"] = new string('0', 64), ["valuesSha256"] = new string('0', 64),
        ["expectedSourceId"] = "personality/v1/cerebral/1/" + new string('0', 64),
        ["expectedSourceRevision"] = 1, ["expectedSourceSha256"] = new string('0', 64)
    };
    private static bool NoOwnerClaims(PluginSettingsTransferSaveRequest request) => !request.CompleteAdmitted &&
        !request.Save.OwnerValidated && request.CompleteProjection is null && request.Save.PortableValues is null &&
        request.Save.OmittedPortableFields == 0;
    private static PluginSettingsTransferSaveRequest Submitted(Fixture context) => context.Central.LastRequest ??
        throw new InvalidOperationException("Fixture service did not submit the expected transfer save.");
    private static bool IsHash(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: 64 } hash && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Fields(JsonElement value, params string[] fields) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal));
    private static JsonElement Values(bool enabled, string id, params PersonalityCustomDefinition[] definitions) =>
        PersonalitySettingsCodec.Write(new(enabled, id, Array.AsReadOnly(definitions)));
    private static JsonElement Pair(bool enabled, string id) => J(new { enabled, selectedPresetId = id });
    private static PluginSettingsDocument Document(JsonElement values, int version, string sourceVersion) =>
        new(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope, null, sourceVersion, version, values);
    private static JsonElement J(object value) => PersonalityContract.Element(value);
    private static async Task<JsonElement> Command(PersonalityServicePlugin plugin, string operation, object args) =>
        J((await plugin.ExecuteAsync(operation, J(args), default))!);

    private sealed class Fixture : IDisposable
    {
        internal PersonalityServicePlugin Plugin { get; } = new();
        internal MemoryTransfer Central { get; }
        internal string SourcePath { get; }
        internal Fixture(string path)
        {
            Central = new(Plugin);
            Plugin.Initialize(new Host(path, Central));
            SourcePath = Path.Combine(path, PersonalityStore.FileName);
        }
        public void Dispose() => Plugin.Dispose();
    }
    private sealed class Host(string path, IScopedPluginSettings preferences) : IServicePluginHost, IServicePluginSettingsHost
    {
        public string PluginId => PersonalityContract.PluginId;
        public string PackageDirectory => path;
        public string DataDirectory => path;
        public IScopedPluginSettings Settings => preferences;
        public void Publish(string eventType, string? jobId, object? data) { }
    }
    private sealed class LegacyOnly(MemoryTransfer inner) : IScopedPluginSettings
    {
        public PluginSettingsState Get(string scope = "global", string? scopeId = null) => inner.Get(scope, scopeId);
        public PluginSettingsSaveResult Save(int version, JsonElement values, long expectedRevision, string scope = "global", string? scopeId = null) =>
            inner.Save(version, values, expectedRevision, scope, scopeId);
    }
    private sealed class ThrowingTransfer(MemoryTransfer inner) : IScopedPluginSettings, IScopedPluginSettingsTransfer
    {
        public PluginSettingsState Get(string scope = "global", string? scopeId = null) => inner.Get(scope, scopeId);
        public PluginSettingsSaveResult Save(int version, JsonElement values, long expectedRevision, string scope = "global", string? scopeId = null) =>
            inner.Save(version, values, expectedRevision, scope, scopeId);
        public PluginSettingsTransferState GetTransfer(string scope = "global", string? scopeId = null) =>
            throw new NotSupportedException("Fixture transfer wrapper has no underlying capability.");
        public PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request) =>
            throw new NotSupportedException("Fixture transfer wrapper has no underlying capability.");
    }

    // Mimics the released Core CAS/purpose/bound-policy path only. It is not actual SQLite or transport integration.
    private sealed class MemoryTransfer : IScopedPluginSettings, IScopedPluginSettingsTransfer
    {
        private readonly IPluginCompleteSettingsPolicy policy;
        private PluginSettingsTransferState transfer;
        internal int LegacyGets, LegacySaves, TransferSaves;
        internal Action? AfterTransferGet;
        internal PluginSettingsTransferSaveRequest? LastRequest;
        internal string? LastPurpose;
        internal PluginSettingsTransferState Snapshot => Copy(transfer);
        internal MemoryTransfer(IPluginCompleteSettingsPolicy policy)
        {
            this.policy = policy;
            var active = Document(Values(false, "cerebral"), 2, PersonalitySettingsPolicy.PackageVersion);
            transfer = new(new(new(PersonalityContract.PluginId, "global"), 0, active, [], "service-policy", Pair(false, "cerebral"), 1), []);
        }
        internal void Add(string id, string purpose, PluginSettingsDocument document)
        {
            if (transfer.State.Pending.Count >= 8 || transfer.State.Pending.Any(item => item.Id == id) ||
                purpose is not (SettingsTransferPurpose.Complete or SettingsTransferPurpose.Local or SettingsTransferPurpose.Portable))
                throw new ArgumentException("Invalid fixture pending addition.");
            var pending = transfer.State.Pending.Append(new PluginSettingsDeferredDocument(id,
                document with { Values = document.Values.Clone() }, purpose == SettingsTransferPurpose.Portable, "fixture-deferred")).ToArray();
            transfer = transfer with { State = transfer.State with { Revision = transfer.State.Revision + 1,
                Pending = Array.AsReadOnly(pending), PendingReason = "fixture-deferred" },
                PendingPurposes = Array.AsReadOnly(transfer.PendingPurposes.Append(new PluginSettingsPendingPurpose(id, purpose)).ToArray()) };
        }
        internal void SetActive(JsonElement values)
        {
            var document = Document(values, 2, PersonalitySettingsPolicy.PackageVersion);
            var admitted = policy.AdmitComplete(document, transfer.State.Active?.Values);
            if (!admitted.Accepted) throw new InvalidDataException("Fixture active policy rejected.");
            var active = document with { SettingsVersion = admitted.SettingsVersion, Values = admitted.Values.Clone() };
            var portable = policy.ExportPortable(active);
            transfer = transfer with { State = transfer.State with { Revision = transfer.State.Revision + 1, Active = active,
                PortableValues = portable.Values.Clone(), OmittedPortableFields = portable.OmittedFields } };
        }
        internal void SetRetainedActive(PluginSettingsDocument? document)
        {
            // Retained absent/future state is supplied by this fixture, never inferred or admitted as current.
            transfer = transfer with { State = transfer.State with { Revision = transfer.State.Revision + 1,
                Active = document is { } active ? active with { Values = active.Values.Clone() } : null,
                PortableValues = null, OmittedPortableFields = 0 } };
        }
        public PluginSettingsState Get(string scope = "global", string? scopeId = null)
        {
            LegacyGets++; RequireScope(scope, scopeId); RequireLegacy(); return Snapshot.State;
        }
        public PluginSettingsSaveResult Save(int version, JsonElement values, long expectedRevision, string scope = "global", string? scopeId = null)
        {
            LegacySaves++; RequireScope(scope, scopeId); RequireLegacy();
            var result = Commit(new(new(Document(values, version, PersonalitySettingsPolicy.PackageVersion), expectedRevision)));
            return new(result.State.State, result.Changed);
        }
        public PluginSettingsTransferState GetTransfer(string scope = "global", string? scopeId = null)
        {
            RequireScope(scope, scopeId);
            var snapshot = Snapshot;
            var callback = AfterTransferGet; AfterTransferGet = null; callback?.Invoke();
            return snapshot;
        }
        public PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request)
        {
            TransferSaves++; LastRequest = request;
            return Commit(request);
        }
        private PluginSettingsTransferSaveResult Commit(PluginSettingsTransferSaveRequest request)
        {
            var save = request.Save;
            if (save.Document.PluginId != PersonalityContract.PluginId ||
                save.Document.SourcePackageVersion != PersonalitySettingsPolicy.PackageVersion || !NoOwnerClaims(request))
                throw new ArgumentException("Fixture scoped package binding or service-only declaration mismatch.");
            RequireScope(save.Document.Scope, save.Document.ScopeId);
            if (save.ExpectedRevision != transfer.State.Revision) throw new InvalidOperationException("Fixture transfer CAS changed.");
            var selected = save.PendingId is null ? null : (transfer.State.Pending.SingleOrDefault(item => item.Id == save.PendingId)
                ?? throw new InvalidOperationException("Fixture selected pending changed."));
            var purpose = selected is null ? SettingsTransferPurpose.Local : transfer.PendingPurposes.Single(item => item.PendingId == selected.Id).Purpose;
            LastPurpose = purpose;
            var admitted = purpose == SettingsTransferPurpose.Complete
                ? policy.AdmitComplete(save.Document, transfer.State.Active?.Values)
                : policy.Admit(save.Document, transfer.State.Active?.Values, purpose == SettingsTransferPurpose.Portable);
            if (!admitted.Accepted) throw new InvalidOperationException("Fixture registered service policy rejected.");
            var active = save.Document with { SourcePackageVersion = PersonalitySettingsPolicy.PackageVersion,
                SettingsVersion = admitted.SettingsVersion, Values = admitted.Values.Clone() };
            var portable = policy.ExportPortable(active);
            var pending = transfer.State.Pending.Where(item => item.Id != save.PendingId).ToArray();
            var purposes = transfer.PendingPurposes.Where(item => item.PendingId != save.PendingId).ToArray();
            var changed = transfer.State.Active is null || !JsonElement.DeepEquals(transfer.State.Active.Values, active.Values) || selected is not null;
            if (changed)
                transfer = new(transfer.State with { Revision = transfer.State.Revision + 1, Active = active,
                    Pending = Array.AsReadOnly(pending), PendingReason = pending.Length == 0 ? null : "fixture-deferred",
                    ValidationAuthority = "service-policy", PortableValues = portable.Values.Clone(), OmittedPortableFields = portable.OmittedFields },
                    Array.AsReadOnly(purposes));
            return new(Snapshot, changed);
        }
        private void RequireLegacy()
        { if (transfer.PendingPurposes.Any(item => item.Purpose == SettingsTransferPurpose.Complete))
            throw new InvalidOperationException("Fixture complete pending requires transfer capability."); }
        private static void RequireScope(string scope, string? id)
        { if (scope != "global" || id is not null) throw new ArgumentException("Fixture package scope mismatch."); }
        private static PluginSettingsTransferState Copy(PluginSettingsTransferState value) => new(value.State with
        {
            Active = value.State.Active is { } active ? active with { Values = active.Values.Clone() } : null,
            Pending = Array.AsReadOnly(value.State.Pending.Select(item => item with
                { Document = item.Document with { Values = item.Document.Values.Clone() } }).ToArray()),
            PortableValues = value.State.PortableValues?.Clone()
        }, Array.AsReadOnly(value.PendingPurposes.ToArray())) { SchemaVersion = value.SchemaVersion };
    }
}
