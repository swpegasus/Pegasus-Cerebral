using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real SQLite fixtures with pure fake package policies; no provider initialization or execution.</summary>
public static class SettingsPreferencesTests
{
    public static int Run(string? fixtureParent = null)
    {
        var root = Path.Combine(Path.GetFullPath(fixtureParent ?? Path.GetTempPath()),
            "settings-preferences-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new Checks();
        try
        {
            GlobalAndLayouts(Path.Combine(root, "global"), checks);
            CaseDistinctOpaqueValues(Path.Combine(root, "case-distinct"), checks);
            DeferredAdmission(Path.Combine(root, "deferred"), checks);
            AgentBackingAndRollback(Path.Combine(root, "agents"), checks);
            CorruptRecords(Path.Combine(root, "corrupt"), checks);
            Capacity(Path.Combine(root, "capacity"), checks);
            return checks.Count;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // The fixture is a newly created unique child, never the caller's parent directory.
            Directory.Delete(root, recursive: true);
        }
    }

    private static void GlobalAndLayouts(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        var preferences = new SettingsPreferencesStore(database);
        var coordinate = Global("module");
        var before = Rows(database);
        var absent = preferences.GetPlugin(coordinate);
        checks.That(absent is { Revision: 0, Active: null, Pending.Count: 0, ValidationAuthority: "none" } &&
            Rows(database) == before, "absent plugin reads do not create a placeholder");

        PluginSettingsSaveResult first;
        using (var input = JsonDocument.Parse("""{"mode":"fast","nested":{"left":1,"right":2}}"""))
            first = preferences.SavePlugin(new(new("module", "global", null, "1.0.0", 1, input.RootElement), 0, true));
        checks.That(first.Changed && first.State.Revision == 1 && first.State.Active!.Values.GetProperty("mode").GetString() == "fast" &&
            first.State.ValidationAuthority == "owner-client" && first.State.PortableValues is null,
            "owner-client save detaches values without claiming a portable or service projection");
        var unchanged = Rows(database);
        var noOp = preferences.SavePlugin(new(Document("module", """{"nested":{"right":2,"left":1},"mode":"fast"}"""), 1, true));
        checks.That(!noOp.Changed && noOp.State.Revision == 1 && Rows(database) == unchanged,
            "structural no-op preserves revision and exact saved rows");

        var secondDatabase = new StateStore(directory);
        var second = new SettingsPreferencesStore(secondDatabase);
        var updated = second.SavePlugin(new(Document("module", """{"mode":"current"}"""), 1, true));
        checks.That(updated.Changed && updated.State.Revision == 2, "a second StateStore commits the current revision");
        var currentRows = Rows(database);
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(Document("module", """{"mode":"stale"}"""), 1, true)),
            "stale CAS from the first StateStore is refused");
        checks.That(Rows(database) == currentRows && preferences.GetPlugin(coordinate).Active!.Values.GetProperty("mode").GetString() == "current",
            "stale CAS preserves the winning state");
        var reopened = new SettingsPreferencesStore(new StateStore(directory)).GetPlugin(coordinate);
        checks.That(reopened.Revision == 2 && reopened.ValidationAuthority == "owner-client" &&
            JsonElement.DeepEquals(reopened.Active!.Values, updated.State.Active!.Values), "global settings reopen with their metadata and values");

        var maximum = Payload(SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes);
        checks.That(JsonSerializer.SerializeToUtf8Bytes(maximum).Length == SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes,
            "global boundary fixture reaches the exact declared byte limit");
        var boundary = preferences.SavePlugin(new(Document("boundary", maximum), 0, true));
        checks.That(boundary.Changed && boundary.State.Active!.Values.GetProperty("payload").GetString()!.Length > 100_000,
            "global object at the exact byte limit persists");
        var boundaryRows = Rows(database);
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("too-large",
            Payload(SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes + 1)), 0, true)), "global object one byte over the limit is refused");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("utf8-large",
            JsonSerializer.SerializeToElement(new { payload = new string('界', 30_000) })), 0, true)),
            "global bound measures serialized bytes rather than string characters");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("array", Parse("[]")), 0, true)), "plugin values require an object");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("duplicates", Parse("""{"key":1,"key":2}""")), 0, true)),
            "exact duplicate value fields are refused");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("deep", DeepObject(35)), 0, true)), "excessive values depth is refused");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(new("bad", "Global", null, "1.0.0", 1, Parse("{}")), 0, true)),
            "scope names are exact");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(new("bad", "global", "agent", "1.0.0", 1, Parse("{}")), 0, true)),
            "global scope cannot carry an agent identity");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(new("bad", "global", null, "not-a-version", 1, Parse("{}")), 0, true)),
            "package versions are validated");
        checks.Throws<UnauthorizedAccessException>(() => preferences.SavePlugin(new(Document("denied", "{}"), 0, true), owner: false),
            "non-owner plugin save is refused");
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(Document("not-validated", "{}"), 0)),
            "missing policy does not fabricate owner-client validation");
        checks.That(Rows(database) == boundaryRows, "rejected global inputs leave all rows unchanged");

        var layoutRows = Rows(database);
        var layout = preferences.GetLayout("Integrator");
        checks.That(layout is { Revision: 0, SchemaVersion: null, Values: null } && Rows(database) == layoutRows,
            "absent layout remains distinguishable without creating a record");
        var reset = preferences.SaveLayout(new("Integrator", 0, 1, Parse("{}")));
        checks.That(reset.Changed && reset.State is { Revision: 1, SchemaVersion: 1 } &&
            reset.State.Values!.Value.EnumerateObject().Count() == 0, "saved empty layout is an explicit renderer reset");
        var resetRows = Rows(database);
        var resetNoOp = preferences.SaveLayout(new("Integrator", 1, 1, Parse("{}")));
        checks.That(!resetNoOp.Changed && Rows(database) == resetRows, "explicit empty layout has a structural no-op");
        var layoutWriter = new SettingsPreferencesStore(new StateStore(directory));
        _ = layoutWriter.SaveLayout(new("Integrator", 1, 2, Parse("""{"bounds":{"width":720},"selectedTab":"models"}""")));
        var winnerRows = Rows(database);
        checks.Throws<InvalidOperationException>(() => preferences.SaveLayout(new("Integrator", 1, 1, Parse("{}"))), "layout stale CAS is refused");
        checks.Throws<UnauthorizedAccessException>(() => preferences.SaveLayout(new("Other", 0, 1, Parse("{}")), owner: false),
            "non-owner layout save is refused");
        checks.Throws<ArgumentException>(() => preferences.SaveLayout(new("Oversized", 0, 1,
            Payload(SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes + 1))), "layout object byte limit is enforced");
        checks.That(Rows(database) == winnerRows, "rejected layout inputs preserve saved state");
        var layoutReopened = new SettingsPreferencesStore(new StateStore(directory)).GetLayout("Integrator");
        checks.That(layoutReopened.Revision == 2 && layoutReopened.SchemaVersion == 2 &&
            layoutReopened.Values!.Value.GetProperty("selectedTab").GetString() == "models", "renderer state survives SQLite reopen");
        preferences.CommitLayouts([new("Integrator", 999, 3, Parse("{}"))]);
        checks.That(preferences.GetLayout("Integrator").Revision == 3, "restore imports layout content using the destination revision");

        var atomicBefore = Rows(database);
        checks.Throws<FixtureInterruption>(() => database.Locked<bool>(() =>
        {
            _ = preferences.SaveLayout(new("Atomic", 0, 1, Parse("{}")));
            _ = preferences.SavePlugin(new(Document("atomic", "{}"), 0, true));
            throw new FixtureInterruption();
        }), "nested settings writes can be interrupted inside the enclosing transaction");
        checks.That(Rows(database) == atomicBefore && preferences.GetLayout("Atomic").Values is null &&
            preferences.GetPlugin(Global("atomic")).Active is null, "enclosing rollback restores plugin and layout records together");
    }

    private static void CaseDistinctOpaqueValues(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        var preferences = new SettingsPreferencesStore(database);
        var values = Parse("""{"a":1,"A":2,"nested":{"a":"lower","A":"upper"},"items":[{"a":false,"A":true}],"schemaVersion":3,"SchemaVersion":4}""");
        var document = Document("case.global", values);
        var saved = preferences.SavePlugin(new(document, 0, true, values));
        checks.That(saved.Changed && JsonElement.DeepEquals(saved.State.Active!.Values, values) &&
            JsonElement.DeepEquals(saved.State.PortableValues!.Value, values),
            "global save preserves case-distinct opaque active and projection fields");
        var read = preferences.GetPlugin(document.Coordinate);
        checks.That(read.Active!.Values.GetProperty("a").GetInt32() == 1 && read.Active.Values.GetProperty("A").GetInt32() == 2 &&
            read.Active.Values.GetProperty("nested").EnumerateObject().Count() == 2 &&
            read.Active.Values.GetProperty("schemaVersion").GetInt32() == 3 && read.Active.Values.GetProperty("SchemaVersion").GetInt32() == 4,
            "opaque typed-looking names remain distinct from outer envelope metadata");

        var pendingValues = Parse("""{"a":10,"A":20,"nested":{"a":1,"A":2}}""");
        var pendingDocument = Document("case.global", pendingValues);
        var planned = preferences.PlanPendingIncoming(pendingDocument, true);
        checks.That(JsonElement.DeepEquals(planned.Active!.Values, values) &&
            JsonElement.DeepEquals(planned.Pending.Single().Document.Values, pendingValues),
            "pending planning preserves exact case-distinct values beside active settings");
        var swappedPending = Document("case.global", Parse("""{"a":20,"A":10,"nested":{"a":1,"A":2}}"""));
        planned = preferences.PlanPendingIncoming(swappedPending, true, planned: planned);
        checks.That(planned.Pending.Count == 2 && planned.Pending[0].Document.Values.GetProperty("a").GetInt32() == 10 &&
            planned.Pending[1].Document.Values.GetProperty("a").GetInt32() == 20,
            "case-distinct field values do not collapse two different deferred documents");
        preferences.CommitPluginStates([planned]);
        var reopened = new SettingsPreferencesStore(new StateStore(directory)).GetPlugin(document.Coordinate);
        checks.That(reopened.Revision == 2 && JsonElement.DeepEquals(reopened.Active!.Values, values) &&
            reopened.Pending.Count == 2 && JsonElement.DeepEquals(reopened.Pending[0].Document.Values, pendingValues) &&
            JsonElement.DeepEquals(reopened.Pending[1].Document.Values, swappedPending.Values),
            "global active and multiple case-distinct pending documents survive reopen");
        var raw = database.ReadSettingsRecord("plugin-settings", "global:case.global", 2 * 1024 * 1024)!.Value.GetProperty("state");
        checks.That(raw.GetProperty("active").GetProperty("values").GetProperty("a").GetInt32() == 1 &&
            raw.GetProperty("active").GetProperty("values").GetProperty("A").GetInt32() == 2 &&
            raw.GetProperty("pending")[0].GetProperty("document").GetProperty("values").GetProperty("a").GetInt32() == 10 &&
            raw.GetProperty("pending")[0].GetProperty("document").GetProperty("values").GetProperty("A").GetInt32() == 20,
            "strict direct saved-record read preserves case-distinct active and pending keys");
        var beforeNoOp = Rows(database);
        var reordered = Document("case.global", Parse("""{"SchemaVersion":4,"schemaVersion":3,"items":[{"A":true,"a":false}],"nested":{"A":"upper","a":"lower"},"A":2,"a":1}"""));
        var noOp = preferences.SavePlugin(new(reordered, reopened.Revision, true, reordered.Values));
        checks.That(!noOp.Changed && noOp.State.Revision == 2 && Rows(database) == beforeNoOp,
            "case-distinct objects keep structural no-op behavior across property order changes");

        database.Put("agent", "case-agent", new AgentProfile
        {
            Id = "case-agent", Revision = 7, Name = "Case-sensitive fixture", ModelId = "fixture-model",
            PluginSettings = new(StringComparer.Ordinal) { ["mod"] = values, ["Mod"] = pendingValues }
        });
        var profile = database.GetSettingsAgentProfile("case-agent")!;
        checks.That(profile.PluginSettings.Count == 2 && JsonElement.DeepEquals(profile.PluginSettings["mod"], values) &&
            JsonElement.DeepEquals(profile.PluginSettings["Mod"], pendingValues),
            "strict agent reader preserves case-distinct plugin map IDs and opaque objects");
        var lower = new PluginSettingsCoordinate("mod", "agent", "case-agent");
        var upper = new PluginSettingsCoordinate("Mod", "agent", "case-agent");
        checks.That(JsonElement.DeepEquals(preferences.GetPlugin(lower).Active!.Values, values) &&
            JsonElement.DeepEquals(preferences.GetPlugin(upper).Active!.Values, pendingValues) &&
            preferences.AllPlugins().Count(state => state.Coordinate.Scope == "agent" && state.Coordinate.ScopeId == "case-agent") == 2,
            "legacy enumeration retains both case-distinct plugin coordinates");
        var agentValues = Parse("""{"a":30,"A":40,"nested":{"a":"updated-lower","A":"updated-upper"}}""");
        var agentDocument = Document("mod", agentValues, "agent", "case-agent");
        var agentSave = preferences.SavePlugin(new(agentDocument, 7, true));
        checks.That(agentSave.Changed && agentSave.State.Revision == 8 &&
            JsonElement.DeepEquals(agentSave.State.Active!.Values, agentValues) &&
            JsonElement.DeepEquals(database.GetSettingsAgentProfile("case-agent")!.PluginSettings["Mod"], pendingValues),
            "agent settings save preserves all exact keys without overwriting a case-distinct peer");
        var agentPending = preferences.PlanPendingIncoming(Document("mod", values, "agent", "case-agent"), false);
        preferences.CommitPluginStates([agentPending]);
        var reopenedDatabase = new StateStore(directory);
        var reopenedPreferences = new SettingsPreferencesStore(reopenedDatabase);
        var reopenedAgent = reopenedPreferences.GetPlugin(lower);
        checks.That(reopenedAgent.Revision == 9 && JsonElement.DeepEquals(reopenedAgent.Active!.Values, agentValues) &&
            JsonElement.DeepEquals(reopenedAgent.Pending.Single().Document.Values, values) &&
            JsonElement.DeepEquals(reopenedDatabase.GetSettingsAgentProfile("case-agent")!.PluginSettings["Mod"], pendingValues),
            "agent case-distinct backing and pending objects survive commit and reopen");

        var layout = preferences.SaveLayout(new("case.renderer", 0, 1, values));
        checks.That(layout.Changed && JsonElement.DeepEquals(layout.State.Values!.Value, values),
            "layout save preserves case-distinct opaque fields");
        var layoutRead = reopenedPreferences.GetLayout("case.renderer");
        checks.That(layoutRead.Revision == 1 && layoutRead.Values is { } hydratedValues && JsonElement.DeepEquals(hydratedValues, values) &&
            hydratedValues.GetProperty("a").GetInt32() == 1 && hydratedValues.GetProperty("A").GetInt32() == 2,
            "layout get and reopen preserve both cases and all nested keys");
        var rawLayout = database.ReadSettingsRecord("interface-layout", "case.renderer", 80 * 1024)!.Value.GetProperty("state").GetProperty("values");
        checks.That(JsonElement.DeepEquals(rawLayout, values), "strict direct layout read preserves every opaque key");

        var beforeDuplicates = Rows(database);
        checks.Throws<ArgumentException>(() => preferences.PlanPendingIncoming(Document("case.global", """{"a":1,"a":2}"""), true),
            "exact duplicate identical names cannot enter a deferred document");
        checks.Throws<ArgumentException>(() => preferences.SavePlugin(new(Document("mod", """{"a":1,"a":2}""", "agent", "case-agent"), 9, true)),
            "agent values still reject exact duplicate identical names");
        checks.Throws<ArgumentException>(() => preferences.SaveLayout(new("case.renderer", 1, 1, Parse("""{"a":1,"a":2}"""))),
            "layout values still reject exact duplicate identical names");
        checks.That(Rows(database) == beforeDuplicates, "exact duplicate input refusals preserve active pending agent and layout rows");

        var profileBody = Body(database, "agent", "case-agent");
        var duplicateMap = Mutate(profileBody, node => node["pluginSettings"] = null)
            .Replace("\"pluginSettings\":null", "\"pluginSettings\":{\"mod\":{\"a\":1},\"mod\":{\"A\":2}}", StringComparison.Ordinal);
        WriteBody(database, "agent", "case-agent", duplicateMap);
        var duplicateMapRows = Rows(database);
        checks.Throws<InvalidDataException>(() => database.GetSettingsAgentProfile("case-agent"),
            "strict agent reader rejects exact duplicate plugin map IDs");
        checks.Throws<InvalidDataException>(() => preferences.GetPlugin(lower),
            "settings get refuses an exact duplicate backing map without repairing it");
        checks.That(Rows(database) == duplicateMapRows, "corrupt exact duplicate agent map remains preserved");
        WriteBody(database, "agent", "case-agent", profileBody);
    }

    private static void DeferredAdmission(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        var bindings = new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal);
        var preferences = new SettingsPreferencesStore(database, () => bindings);
        var active = preferences.SavePlugin(new(Document("module", """{"value":"destination","localOnly":"local-fixture-value"}"""), 0, true)).State;
        var firstIncoming = Document("module", """{"value":"first-import"}""");
        var secondIncoming = Document("module", """{"value":"second-import","reject":true}""");
        var thirdIncoming = Document("module", """{"value":"third-import"}""");
        var planned = preferences.PlanIncoming(firstIncoming, portable: true, planned: null, out var admitted);
        checks.That(!admitted && planned.Active!.Values.GetProperty("value").GetString() == "destination" &&
            planned.Pending.Count == 1, "unavailable incoming Active becomes deferred while destination Active survives");
        planned = preferences.PlanPendingIncoming(secondIncoming, true, planned: planned);
        planned = preferences.PlanPendingIncoming(thirdIncoming, true, planned: planned);
        var originalIds = planned.Pending.Select(item => item.Id).ToArray();
        var deduped = preferences.PlanPendingIncoming(thirdIncoming with { Values = Parse("""{"value":"third-import"}""") }, true, planned: planned);
        checks.That(deduped.Pending.Count == 3 && deduped.Pending.Select(item => item.Id).SequenceEqual(originalIds),
            "exact deferred metadata and values deduplicate without changing local IDs");
        preferences.CommitPluginStates([deduped]);
        var reopened = new SettingsPreferencesStore(new StateStore(directory), () => bindings).GetPlugin(Global("module"));
        checks.That(reopened.Revision == active.Revision + 1 && reopened.Pending.Count == 3 &&
            reopened.Pending.Select(item => item.Id).SequenceEqual(originalIds) && reopened.Active!.Values.GetProperty("localOnly").GetString() == "local-fixture-value",
            "Active plus multiple deferred role documents survive reopen");
        var summaries = preferences.PendingSummaries();
        checks.That(summaries.Count == 3 && summaries.Select(item => item.Id).SequenceEqual(originalIds) &&
            !JsonSerializer.Serialize(summaries).Contains("first-import", StringComparison.Ordinal), "pending summaries return metadata without document values");

        var policy = new FakePolicy();
        bindings.Add("module", new("module", "2.4.1", policy));
        var calls = policy.AdmissionCalls;
        var pendingRole = preferences.PlanPendingIncoming(Document("module", """{"value":"pending-role"}"""), true);
        checks.That(policy.AdmissionCalls == calls && pendingRole.Pending.Count == 4 &&
            pendingRole.Active!.Values.GetProperty("value").GetString() == "destination", "pending-role import never automatically invokes admission");
        preferences.CommitPluginStates([pendingRole]);
        var selected = pendingRole.Pending[0];
        var adoption = preferences.SavePlugin(new(selected.Document, pendingRole.Revision) { PendingId = selected.Id });
        checks.That(adoption.Changed && policy.LastPortable && adoption.State.ValidationAuthority == "service-policy" &&
            adoption.State.Active!.SourcePackageVersion == "2.4.1" && adoption.State.Active.SettingsVersion == 3,
            "selected portable document uses package admission and the actual bound package version");
        checks.That(adoption.State.Active!.Values.GetProperty("localOnly").GetString() == "local-fixture-value" &&
            adoption.State.Active.Values.GetProperty("value").GetString() == "first-import" &&
            adoption.State.PortableValues!.Value.GetProperty("value").GetString() == "first-import" &&
            !adoption.State.PortableValues.Value.TryGetProperty("localOnly", out _) && adoption.State.OmittedPortableFields == 1,
            "portable adoption preserves local fields and retains the package-owned nonsecret projection");
        checks.That(adoption.State.Pending.Count == 3 && adoption.State.Pending.All(item => item.Id != selected.Id) &&
            adoption.State.Pending.Select(item => item.Id).SequenceEqual(pendingRole.Pending.Skip(1).Select(item => item.Id)),
            "successful admission removes only the selected deferred document");
        var rejectionRows = Rows(database);
        var rejected = adoption.State.Pending.Single(item => item.Document.Values.TryGetProperty("reject", out _));
        var rejection = checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(rejected.Document, adoption.State.Revision) { PendingId = rejected.Id }),
            "policy rejection preserves the selected deferred document");
        checks.That(!rejection.ToString().Contains(FakePolicy.PrivateDiagnostic, StringComparison.Ordinal) && Rows(database) == rejectionRows,
            "policy rejection diagnostic is fixed and no settings rows change");
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(firstIncoming, adoption.State.Revision)
            { PendingId = Guid.NewGuid().ToString("N") }), "unknown deferred ID cannot clear another document");
        checks.That(Rows(database) == rejectionRows, "unknown deferred ID leaves all documents unchanged");

        var accepted = preferences.PlanIncoming(Document("module", """{"value":"accepted-active"}"""), false, null, out admitted);
        checks.That(admitted && accepted.Active!.Values.GetProperty("value").GetString() == "accepted-active" &&
            accepted.Pending.Count == 3, "actual Active admission is counted while unrelated pending survives");
        preferences.CommitPluginStates([accepted]);
        var failedPlan = preferences.PlanIncoming(rejected.Document, true, null, out admitted);
        checks.That(!admitted && failedPlan.Active!.Values.GetProperty("value").GetString() == "accepted-active" &&
            failedPlan.Pending.Count == accepted.Pending.Count, "rejected duplicate import is retained without falsely counting preserved Active as admitted");

        var ordinary = preferences.SavePlugin(new(Document("module", """{"value":"ordinary-save"}"""), accepted.Revision));
        checks.That(!policy.LastPortable && ordinary.State.Pending.Count == 3, "ordinary package save has local origin and leaves deferred inputs retained");
        bindings.Clear();
        var ownerAdoption = preferences.SavePlugin(new(rejected.Document, ordinary.State.Revision, true,
            Parse("""{"value":"desktop-projection"}""")) { PendingId = rejected.Id });
        checks.That(ownerAdoption.State.ValidationAuthority == "owner-client" && ownerAdoption.State.Pending.Count == 2 &&
            ownerAdoption.State.Pending.All(item => item.Id != rejected.Id) &&
            ownerAdoption.State.PortableValues!.Value.GetProperty("value").GetString() == "desktop-projection",
            "explicit desktop-client adoption clears only its selected ID and never claims service proof");

        var capped = preferences.PlanPendingIncoming(Document("module", """{"value":"same-content"}"""), false);
        capped = preferences.PlanPendingIncoming(Document("module", """{"value":"same-content"}"""), true, planned: capped);
        checks.That(capped.Pending.Count == 4, "portable origin is part of deferred identity");
        for (var index = capped.Pending.Count; index < SettingsPreferenceContract.MaximumPendingPerDocument; index++)
            capped = preferences.PlanPendingIncoming(Document("module", JsonSerializer.SerializeToElement(new { value = "extra-" + index })), true, planned: capped);
        preferences.CommitPluginStates([capped]);
        var fullRows = Rows(database);
        var fullIds = capped.Pending.Select(item => item.Id).ToArray();
        checks.Throws<InvalidOperationException>(() => preferences.PlanPendingIncoming(Document("module", """{"value":"ninth"}"""), true),
            "deferred retention overflow is refused without eviction");
        checks.That(Rows(database) == fullRows &&
            preferences.GetPlugin(Global("module")).Pending.Select(item => item.Id).SequenceEqual(fullIds), "all eight deferred documents survive overflow");

        var failedDatabase = new StateStore(Path.Combine(directory, "policy-failures"));
        var failing = new FakePolicy { ThrowAdmission = true };
        var failingPreferences = new SettingsPreferencesStore(failedDatabase, () =>
            new Dictionary<string, SettingsPolicyBinding> { ["module"] = new("module", "2.4.1", failing) });
        var failedRows = Rows(failedDatabase);
        var diagnostic = checks.Throws<InvalidOperationException>(() => failingPreferences.SavePlugin(new(firstIncoming, 0)),
            "throwing package policy fails the save");
        checks.That(!diagnostic.ToString().Contains(FakePolicy.PrivateDiagnostic, StringComparison.Ordinal) && Rows(failedDatabase) == failedRows,
            "throwing package callback neither leaks its diagnostic nor creates settings");
        failing.ThrowAdmission = false; failing.ThrowProjection = true;
        checks.Throws<InvalidOperationException>(() => failingPreferences.SavePlugin(new(firstIncoming, 0)), "failed portable projection refuses the whole admission");
        checks.That(Rows(failedDatabase) == failedRows, "projection failure does not leave a partially accepted Active document");
    }

    private static void AgentBackingAndRollback(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        database.Put("agent", "agent", new AgentProfile
        {
            Id = "agent", Revision = 4, Name = "Fixture agent", ModelId = "fixture-model",
            PluginSettings = new(StringComparer.Ordinal) { ["untouched"] = Parse("""{"retained":true}""") }
        });
        var preferences = new SettingsPreferencesStore(database);
        var incoming = Document("module", """{"mode":"saved"}""", "agent", "agent");
        var saved = preferences.SavePlugin(new(incoming, 4, true));
        var profile = database.GetSettingsAgentProfile("agent")!;
        checks.That(saved.State.Revision == 5 && profile.Revision == 5 &&
            JsonElement.DeepEquals(profile.PluginSettings["module"], saved.State.Active!.Values) &&
            profile.PluginSettings["untouched"].GetProperty("retained").GetBoolean(), "agent save uses profile CAS and preserves unrelated backing");
        var savedRows = Rows(database);
        var noOp = preferences.SavePlugin(new(incoming, 5, true));
        checks.That(!noOp.Changed && Rows(database) == savedRows && database.GetSettingsAgentProfile("agent")!.Revision == 5,
            "agent no-op does not increment profile revision");
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(incoming, 4, true)), "agent stale CAS is refused");
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(Document("module", "{}", "agent", "missing"), 0, true)),
            "normal save cannot create a missing agent scope");
        checks.That(Rows(database) == savedRows, "rejected agent saves do not alter profiles or metadata");

        profile.Revision++;
        profile.PluginSettings["module"] = Parse("""{"mode":"legacy-change"}""");
        database.Put("agent", profile.Id, profile);
        var legacyRows = Rows(database);
        var effective = preferences.GetPlugin(incoming.Coordinate);
        checks.That(effective.Revision == 6 && effective.ValidationAuthority == "unversioned" &&
            effective.Active is { SourcePackageVersion: null, SettingsVersion: null } &&
            effective.Active.Values.GetProperty("mode").GetString() == "legacy-change" &&
            effective.PortableValues is null && Rows(database) == legacyRows, "legacy profile changes downgrade stale schema metadata without rewriting it");
        checks.That(preferences.AllPlugins().Count == 2 &&
            preferences.GetPlugin(new("untouched", "agent", "agent")).ValidationAuthority == "unversioned",
            "enumeration includes legacy profile settings without inventing package schema");

        var pending = preferences.PlanIncoming(Document("module", """{"mode":"deferred"}""", "agent", "agent"), false);
        preferences.CommitPluginStates([pending]);
        checks.That(database.GetSettingsAgentProfile("agent")!.Revision == 7 && pending.Pending.Count == 1 &&
            preferences.GetPlugin(incoming.Coordinate).Active!.Values.GetProperty("mode").GetString() == "legacy-change",
            "pending-only commit advances profile CAS while preserving backing");
        var restorePlan = preferences.PlanPendingIncoming(Document("module", """{"mode":"pending-role"}""", "agent", "agent"), false);
        database.Locked(() =>
        {
            var staged = database.GetSettingsAgentProfile("agent")!;
            staged.Revision++;
            database.Put("agent", staged.Id, staged);
            preferences.CommitPluginStates([restorePlan], updateAgentBacking: false);
            return true;
        });
        checks.That(database.GetSettingsAgentProfile("agent")!.Revision == 8 &&
            preferences.GetPlugin(incoming.Coordinate).Revision == 8 && preferences.GetPlugin(incoming.Coordinate).Pending.Count == 2,
            "metadata-only restore checks staged backing and avoids a second profile revision increment");

        var mismatchPlan = preferences.PlanPendingIncoming(Document("module", """{"mode":"mismatch-plan"}""", "agent", "agent"), false);
        var beforeMismatch = Rows(database);
        checks.Throws<InvalidOperationException>(() => database.Locked(() =>
        {
            var staged = database.GetSettingsAgentProfile("agent")!;
            staged.Revision++;
            staged.PluginSettings["module"] = Parse("""{"mode":"wrong-staged-backing"}""");
            database.Put("agent", staged.Id, staged);
            preferences.CommitPluginStates([mismatchPlan], updateAgentBacking: false);
            return true;
        }), "metadata-only commit refuses a profile whose backing differs from the plan");
        checks.That(Rows(database) == beforeMismatch, "backing mismatch rolls staged profile and metadata back together");

        Execute(database, """
            CREATE TRIGGER reject_settings_metadata BEFORE UPDATE ON records
            WHEN NEW.kind='plugin-settings' AND NEW.id='agent:agent:module'
            BEGIN SELECT RAISE(ABORT,'fixture metadata failure'); END
            """);
        var rollbackRows = Rows(database);
        checks.Throws<SqliteException>(() => preferences.SavePlugin(new(Document("module", """{"mode":"must-rollback"}""", "agent", "agent"), 8, true)),
            "real SQLite metadata write failure interrupts an agent settings commit");
        checks.That(Rows(database) == rollbackRows && database.GetSettingsAgentProfile("agent")!.Revision == 8,
            "metadata failure rolls back the already-staged profile backing");
        Execute(database, "DROP TRIGGER reject_settings_metadata");

        var policy = new FakePolicy { NormalizedValues = Payload(SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes + 1) };
        var policyPreferences = new SettingsPreferencesStore(database, () =>
            new Dictionary<string, SettingsPolicyBinding> { ["module"] = new("module", "2.4.1", policy) });
        var normalizedRows = Rows(database);
        checks.Throws<InvalidOperationException>(() => policyPreferences.SavePlugin(new(incoming, 8)), "oversized normalized agent values are refused");
        checks.That(Rows(database) == normalizedRows && database.GetSettingsAgentProfile("agent")!.Revision == 8,
            "callback normalization cannot bypass the existing agent backing limit");
        var persistedTooLarge = JsonSerializer.SerializeToElement(Enumerable.Range(0, 6_000)
            .ToDictionary(index => "k" + index, _ => 0));
        policy.NormalizedValues = persistedTooLarge;
        checks.That(JsonSerializer.SerializeToUtf8Bytes(persistedTooLarge).Length <= SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes &&
            JsonSerializer.SerializeToUtf8Bytes(persistedTooLarge, CerebralJson.Options).Length > SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes,
            "normalized backing fixture separates compact size from the existing persisted agent limit");
        var normalizedPlan = policyPreferences.PlanIncoming(incoming, false, null, out var admitted);
        checks.That(!admitted && normalizedPlan.Pending.Count == 3 && normalizedPlan.Active!.Values.GetProperty("mode").GetString() == "legacy-change" &&
            Rows(database) == normalizedRows, "invalid persisted-size normalization remains deferred without claiming incoming admission");
        checks.Throws<InvalidOperationException>(() => policyPreferences.SavePlugin(new(incoming, 8)),
            "package save enforces the actual persisted agent value limit after normalization");
        checks.That(Rows(database) == normalizedRows, "persisted-size normalization refusal preserves agent and metadata rows");
    }

    private static void CorruptRecords(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        var preferences = new SettingsPreferencesStore(database);
        var coordinate = Global("module");
        _ = preferences.SavePlugin(new(Document("module", """{"value":"stable"}"""), 0, true));
        var original = Body(database, "plugin-settings", "global:module");
        var malformed = new List<string>
        {
            "{",
            "null",
            Mutate(original, node => node["extraEnvelopeField"] = true),
            Mutate(original, node => node.AsObject().Remove("schemaVersion")),
            Mutate(original, node => node["state"]!["extraMetadata"] = true),
            Mutate(original, node => node["SchemaVersion"] = 1),
            Mutate(original, node => node["state"] = null),
            Mutate(original, node => node["state"]!.AsObject().Remove("pending")),
            Mutate(original, node => node["state"]!["revision"] = -1),
            Mutate(original, node => node["state"]!["coordinate"]!["pluginId"] = "other"),
            Mutate(original, node => node["state"]!["validationAuthority"] = "pretend-service"),
            Mutate(original, node => node["state"]!["active"]!["sourcePackageVersion"] = "bad-version"),
            Mutate(original, node => node["state"]!["active"]!["values"] = null)
                .Replace("\"values\":null", "\"values\":{\"value\":\"stable\",\"value\":\"duplicate\"}", StringComparison.Ordinal),
            Mutate(original, node => node["state"]!["active"]!["values"] = JsonNode.Parse(DeepObject(35).GetRawText()))
        };
        foreach (var raw in malformed)
        {
            WriteBody(database, "plugin-settings", "global:module", raw);
            var fingerprint = Rows(database);
            checks.Throws<InvalidDataException>(() => preferences.GetPlugin(coordinate), "corrupt plugin envelope is refused");
            checks.Throws<InvalidDataException>(() => preferences.AllPlugins(), "enumeration does not skip a corrupt plugin envelope");
            checks.Throws<InvalidDataException>(() => preferences.SavePlugin(new(Document("module", "{}"), 1, true)),
                "save cannot repair or overwrite a corrupt plugin envelope");
            checks.That(Rows(database) == fingerprint, "corrupt plugin rows remain exactly preserved after refusal");
        }
        foreach (var raw in new object[] { new byte[] { 0x7B, 0x7D }, new string(' ', 2 * 1024 * 1024 + 1) })
        {
            WriteBody(database, "plugin-settings", "global:module", raw);
            var fingerprint = Rows(database);
            checks.Throws<InvalidDataException>(() => preferences.GetPlugin(coordinate), "non-text or oversized saved body is refused");
            checks.That(Rows(database) == fingerprint, "non-text or oversized body is preserved without repair");
        }
        WriteBody(database, "plugin-settings", "global:module", new byte[] { 0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D }, castText: true);
        var invalidUtf8Rows = Rows(database);
        checks.Throws<InvalidDataException>(() => preferences.GetPlugin(coordinate), "malformed UTF-8 stored as SQLite text is refused");
        checks.That(Rows(database) == invalidUtf8Rows, "invalid UTF-8 text is not rewritten");
        WriteBody(database, "plugin-settings", "global:module", original);

        _ = preferences.SaveLayout(new("Integrator", 0, 1, Parse("{}")));
        var layout = Body(database, "interface-layout", "Integrator");
        WriteBody(database, "interface-layout", "Integrator", Mutate(layout, node => node["state"]!["values"] = null));
        var invalidLayoutRows = Rows(database);
        checks.Throws<InvalidDataException>(() => preferences.GetLayout("Integrator"), "partial saved layout is not treated as absent");
        checks.Throws<InvalidDataException>(() => preferences.SaveLayout(new("Integrator", 1, 1, Parse("{}"))),
            "layout save refuses partial saved data");
        checks.That(Rows(database) == invalidLayoutRows, "partial layout remains preserved rather than repaired");
        WriteBody(database, "interface-layout", "Integrator", layout);
        var beforeArguments = Rows(database);
        checks.Throws<ArgumentException>(() => database.ReadSettingsRecord("arbitrary", "id", 100), "raw settings reader has a fixed kind allowlist");
        checks.Throws<ArgumentException>(() => database.ReadSettingsRecord("settings", "host", 100), "legacy settings reader admits only appearance-preset");
        checks.Throws<ArgumentOutOfRangeException>(() => database.ReadSettingsRecord("plugin-settings", "global:module", 0), "raw reader requires a positive byte bound");
        checks.That(Rows(database) == beforeArguments, "invalid raw reader arguments cannot mutate saved records");
    }

    private static void Capacity(string directory, Checks checks)
    {
        var database = new StateStore(directory);
        var preferences = new SettingsPreferencesStore(database);
        var states = Enumerable.Range(0, SettingsPreferenceContract.MaximumDocuments).Select(index =>
        {
            var document = Document("module-" + index, "{}");
            return new PluginSettingsState(document.Coordinate, 1, document, [], "owner-client");
        }).ToArray();
        preferences.CommitPluginStates(states);
        checks.That(preferences.AllPlugins().Count == SettingsPreferenceContract.MaximumDocuments, "all declared plugin document slots are retained");
        var fullRows = Rows(database);
        checks.Throws<InvalidOperationException>(() => preferences.SavePlugin(new(Document("one-too-many", "{}"), 0, true)),
            "plugin document capacity refuses an additional coordinate");
        checks.That(Rows(database) == fullRows && preferences.AllPlugins().Count == SettingsPreferenceContract.MaximumDocuments,
            "plugin capacity refusal never evicts existing documents");

        var layouts = Enumerable.Range(0, SettingsPreferenceContract.MaximumLayouts)
            .Select(index => new InterfaceLayoutState("app-" + index, 1, 1, Parse("{}"))).ToArray();
        preferences.CommitLayouts(layouts);
        checks.That(preferences.AllLayouts().Count == SettingsPreferenceContract.MaximumLayouts, "all declared renderer layout slots are retained");
        var layoutRows = Rows(database);
        checks.Throws<InvalidOperationException>(() => preferences.SaveLayout(new("one-too-many", 0, 1, Parse("{}"))),
            "renderer layout capacity refuses an additional application");
        checks.That(Rows(database) == layoutRows && preferences.AllLayouts().Count == SettingsPreferenceContract.MaximumLayouts,
            "layout capacity refusal never evicts existing renderer state");
    }

    private static PluginSettingsCoordinate Global(string plugin) => new(plugin, "global");
    private static PluginSettingsDocument Document(string plugin, string json, string scope = "global", string? scopeId = null) =>
        Document(plugin, Parse(json), scope, scopeId);
    private static PluginSettingsDocument Document(string plugin, JsonElement values, string scope = "global", string? scopeId = null) =>
        new(plugin, scope, scopeId, "1.0.0", 1, values);
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static JsonElement DeepObject(int depth) => Parse(string.Concat(Enumerable.Repeat("{\"child\":", depth)) + "{}" + new string('}', depth));
    private static JsonElement Payload(int totalBytes)
    {
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new { payload = "" }).Length;
        return JsonSerializer.SerializeToElement(new { payload = new string('x', totalBytes - overhead) });
    }
    private static string Mutate(string json, Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        mutation(node);
        return node.ToJsonString();
    }
    private static SqliteConnection Open(StateStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Execute(StateStore store, string sql)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void WriteBody(StateStore store, string kind, string id, object body, bool castText = false)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = castText
            ? "UPDATE records SET body=CAST($body AS TEXT) WHERE kind=$kind AND id=$id"
            : "UPDATE records SET body=$body WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$body", body);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Fixture row was unavailable.");
    }
    private static string Body(StateStore store, string kind, string id)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
        return (string)(command.ExecuteScalar() ?? throw new InvalidOperationException("Fixture row was unavailable."));
    }
    private static string Rows(StateStore store)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind,id,typeof(body),CAST(body AS BLOB) FROM records ORDER BY kind,id";
        using var reader = command.ExecuteReader();
        var fingerprints = new List<string>();
        long total = 0;
        while (reader.Read())
        {
            if (reader.GetValue(3) is not byte[] bytes || bytes.Length > 4 * 1024 * 1024 ||
                (total += bytes.Length) > 16 * 1024 * 1024) throw new InvalidOperationException("Fixture fingerprint bound exceeded.");
            fingerprints.Add(reader.GetString(0) + "/" + reader.GetString(1) + "/" + reader.GetString(2) + "/" +
                bytes.Length + "/" + Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return string.Join("\n", fingerprints);
    }

    private sealed class Checks
    {
        public int Count { get; private set; }
        public void That(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Count++;
            Console.WriteLine("PASS: " + description);
        }
        public T Throws<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T error) { That(true, description); return error; }
            throw new InvalidOperationException("FAIL: " + description + " (expected " + typeof(T).Name + ")");
        }
    }
    private sealed class FixtureInterruption : Exception { }

    private sealed class FakePolicy : IPluginSettingsPolicy
    {
        public const string PrivateDiagnostic = "fixture-package-private-diagnostic";
        public int AdmissionCalls { get; private set; }
        public bool LastPortable { get; private set; }
        public bool ThrowAdmission { get; set; }
        public bool ThrowProjection { get; set; }
        public JsonElement? NormalizedValues { get; set; }
        public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
        {
            AdmissionCalls++; LastPortable = portable;
            if (ThrowAdmission) throw new InvalidOperationException(PrivateDiagnostic);
            if (incoming.Values.TryGetProperty("reject", out var reject) && reject.ValueKind == JsonValueKind.True)
                return new(false, 3, default, PrivateDiagnostic);
            var fields = incoming.Values.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            if (portable && currentValues is { } current && current.TryGetProperty("localOnly", out var local))
                fields["localOnly"] = local.Clone();
            fields["normalized"] = JsonSerializer.SerializeToElement(true);
            return new(true, 3, NormalizedValues ?? JsonSerializer.SerializeToElement(fields));
        }
        public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
        {
            if (ThrowProjection) throw new InvalidOperationException(PrivateDiagnostic);
            var fields = current.Values.EnumerateObject().Where(item => item.Name != "localOnly")
                .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            return new(3, JsonSerializer.SerializeToElement(fields), current.Values.TryGetProperty("localOnly", out _) ? 1 : 0);
        }
    }
}
