using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.PersonalityServicePlugin;

internal static class SuccessorTests
{
    internal static async Task RunAsync(string directory)
    {
        var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build", "Plugins", "SettingsPresets"));
        var requested = Path.GetFullPath(directory);
        if (!requested.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Successor fixtures must stay in the existing Plugins build component.");
        var fixture = Path.Combine(requested, "working");
        if (Directory.Exists(fixture)) throw new IOException("Prior owned fixtures exist; inspect them before rerunning.");
        Directory.CreateDirectory(fixture);
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException or JsonException or NotSupportedException)
            { rejected = true; }
            Check(rejected, label);
        }
        async Task RejectAsync(Func<Task> action, string label)
        {
            var rejected = false;
            try { await action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException or JsonException or NotSupportedException)
            { rejected = true; }
            Check(rejected, label);
        }
        var success = false;
        try
        {
            CompleteSettingsPolicyTests.Run(Check);
            var data = Path.Combine(fixture, "data");
            using var plugin = new PersonalityServicePlugin();
            var central = new MemorySettings(plugin);
            plugin.Initialize(new Host(data, central));
            var facts = Facts();
            using (var unsupported = new PersonalityServicePlugin())
            {
                unsupported.Initialize(new FixtureHost(Path.Combine(fixture, "no-settings")));
                var noSettings = unsupported.ContextContributions.Single();
                Check((await noSettings.CaptureAsync(facts, default)).Disposition == WorkflowContextDisposition.Omitted,
                    "absent chat is None without requiring a global-settings fallback");
                Check((await noSettings.CaptureAsync(facts with { ProfileRole = "helper" }, default)).Disposition == WorkflowContextDisposition.Omitted,
                    "scope exclusion does not depend on central settings capability");
            }
            var production = plugin.ContextContributions.Single();
            Check(plugin is IPluginCompleteSettingsPolicy && production.Descriptor.Id == PersonalityContract.ContributionId,
                "real successor exposes complete policy and one stable optional facet");
            Check(production.Descriptor.LegacyFormats.Single().SnapshotVersion == 4,
                "package legacy reader declares only original v4 slots");
            var absent = await production.CaptureAsync(facts, default);
            Check(absent.Disposition == WorkflowContextDisposition.Omitted && absent.Request is null,
                "absent central preferences are disabled without legacy selection fallback");
            var file = Path.Combine(data, PersonalityStore.FileName);
            Check(!File.Exists(file), "disabled capture does not materialize sources");
            foreach (var route in new[] { "personality.save", "personality.select",
                "personality.admission.capture", "personality.admission.restore" })
                await RejectAsync(async () => { await plugin.ExecuteAsync(route, J(new { }), default); },
                    "legacy route refuses before effects: " + route);
            Check(!File.Exists(file), "refused old routes leave domain file absent");

            central.Set(new(true, "cerebral", Array.Empty<PersonalityCustomDefinition>()));
            Check((await production.CaptureAsync(facts, default)).Disposition == WorkflowContextDisposition.Omitted,
                "legacy global enable does not silently enable any chat");
            await ChooseChat(plugin, facts.Identity.ConversationId, "packaged", "cerebral", false);
            var ready = await production.CaptureAsync(facts, default);
            Check(ready.Disposition == WorkflowContextDisposition.Ready && ready.Payload is { } payload &&
                payload.GetProperty("contract").GetString() == PersonalityChatContributionCodec.Contract &&
                ready.PayloadDigest is { Length: 64 } && ready.Request is not null,
                "production constructor captures a valid builtin without an injected observer");
            foreach (var outside in new[]
            {
                facts with { ProfileRole = "helper" },
                facts with { Identity = facts.Identity with { Caller = new(EventActorKind.User, "other-owner") } },
                facts with { Identity = facts.Identity with { ParentRunId = "parent-fixture" } },
                facts with { WorkerId = "remote" }
            })
            {
                var omitted = await production.CaptureAsync(outside, default);
                Check(omitted.Disposition == WorkflowContextDisposition.Omitted && omitted.Payload is null &&
                    omitted.PayloadDigest is null && omitted.Request is null,
                    "scope exclusion remains provider-free: " + outside.ProfileRole + "/" + outside.WorkerId +
                    "/" + outside.Identity.Caller?.Id + "/" + outside.Identity.ParentRunId);
            }
            Check(!File.Exists(file), "builtin production capture appends no source");
            var preferencesReply = await Command(plugin, "personality.preferences.get", new { });
            Check(preferencesReply.GetProperty("schemaVersion").GetInt32() == 2 &&
                !preferencesReply.TryGetProperty("readiness", out _),
                "preference reply reports central values without a synthetic migration diagnostic");
            var missingOrigin = await production.CaptureAsync(facts with { InstallationId = null }, default);
            Check(missingOrigin.Disposition == WorkflowContextDisposition.Unavailable &&
                missingOrigin.Payload is null && missingOrigin.PayloadDigest is null && missingOrigin.Request is null,
                "production capture refuses unknown origin without creating an accepted request");
            production.Validate(facts, facts with { Identity = facts.Identity with
                { Caller = new(EventActorKind.User, "owner", "Different presentation") } }, ready, default);
            Check(true, "DisplayName-only change preserves caller authority");
            var accepted = new WorkflowContextAcceptedFacts(facts, "project-fixture", 8, 3, 4);
            var request = await production.RestoreAsync(accepted, ready, default);
            Check(request.ContextRevision == 3 && request.ExclusionRevision == 4 &&
                request.References.Single().SourceId == ready.Request!.References.Single().SourceId,
                "final target restore rebases only context/exclusion revisions");
            var reads = central.Reads;
            central.FailReads = true;
            _ = await production.RestoreAsync(accepted, ready, default);
            Check(central.Reads == reads, "accepted restore never reads live central selection");
            production.Validate(facts, facts, ready, default);
            Check(central.Reads == reads, "accepted validation never reads live central selection");
            central.FailReads = false;
            Reject(() => production.Validate(facts, facts with { InstallationId = "other-installation" }, ready, default),
                "changed installation refuses accepted execution");
            Reject(() => production.Validate(facts, facts, ready with { PayloadDigest = new string('0', 64) }, default),
                "corrupt ready digest refuses without recapture");
            await RejectAsync(async () => { await production.RestoreAsync(accepted with
                { Accepted = facts with { InstallationId = null } }, ready, default); },
                "unknown accepted header refuses ready restore");
            Reject(() => production.Validate(facts, facts with { Identity = facts.Identity with
                { RequestId = "other-request" } }, ready, default), "changed request identity refuses accepted execution");
            Reject(() => production.Validate(facts, facts with { Identity = facts.Identity with
                { ConversationId = "other-conversation" } }, ready, default), "changed conversation identity refuses accepted execution");

            var beforeCaptureReads = central.Reads;
            var racedCapture = await production.CaptureAsync(facts, default);
            Check(racedCapture.Disposition == WorkflowContextDisposition.Ready && central.Reads == beforeCaptureReads,
                "new capture uses persisted chat pins without reading mutable global preferences");
            Check(!File.Exists(file), "chat capture does not append legacy source history");

            central.Set(new(true, "custom-one", [new("custom-one", "Original name", "Use compact fixture responses.")]));
            var unprepared = await production.CaptureAsync(facts, default);
            Check(unprepared.Disposition == WorkflowContextDisposition.Ready &&
                unprepared.Request!.References.Single().SourceId == ready.Request!.References.Single().SourceId,
                "global desired custom source does not change the chat's packaged selection");
            var sourceState = await Command(plugin, "personality.sources.get", new { });
            var expected = central.Get().Revision;
            var preparation = await Command(plugin, "personality.source.prepare", new { schemaVersion = 1, expectedSettingsRevision = expected,
                expectedStoreRevision = sourceState.GetProperty("revision").GetInt64(),
                expectedStoreSha256 = sourceState.GetProperty("sha256").GetString() });
            Check(preparation.GetProperty("settingsRevision").GetInt64() == expected &&
                !preparation.TryGetProperty("readiness", out _),
                "preparation confirms the selected source revision without claiming migration readiness");
            Check(File.Exists(file), "explicit preparation writes the selected immutable source");
            await ChooseChat(plugin, facts.Identity.ConversationId, "custom", "custom-one", false);
            var custom = await production.CaptureAsync(facts, default);
            Check(custom.Disposition == WorkflowContextDisposition.Ready && custom.Request is not null,
                "production constructor captures the exact explicitly prepared custom source");
            var customRequest = await production.RestoreAsync(accepted, custom, default);
            var firstId = customRequest.References.Single().SourceId;
            var bytes = File.ReadAllBytes(file);
            central.Set(new(true, "custom-one", [new("custom-one", "New name", "Use a later fixture style.")]));
            Check((await production.CaptureAsync(facts, default)).Request!.References.Single().SourceId == firstId,
                "global editing does not silently retarget the saved chat's exact definition pin");
            Check(File.ReadAllBytes(file).SequenceEqual(bytes), "capture does not append preparation");
            Check((await production.RestoreAsync(accepted, custom, default)).References.Single().SourceId == firstId,
                "current preference edits do not replace an accepted source");
            sourceState = await Command(plugin, "personality.sources.get", new { });
            var stale = sourceState;
            expected = central.Get().Revision;
            await Command(plugin, "personality.source.prepare", new { schemaVersion = 1, expectedSettingsRevision = expected,
                expectedStoreRevision = sourceState.GetProperty("revision").GetInt64(),
                expectedStoreSha256 = sourceState.GetProperty("sha256").GetString() });
            Check(plugin.Resolve(firstId, 1).Text == "Use compact fixture responses.", "earlier accepted effective source stays exactly readable");
            await RejectAsync(async () => { await Command(plugin, "personality.source.prepare", new { schemaVersion = 1,
                expectedSettingsRevision = central.Get().Revision, expectedStoreRevision = stale.GetProperty("revision").GetInt64(),
                expectedStoreSha256 = stale.GetProperty("sha256").GetString() }); }, "stale source CAS refuses preparation");
            bytes = File.ReadAllBytes(file);
            var save = await plugin.ExecuteAsync("personality.preferences.save", J(new
            { schemaVersion = 2, expectedRevision = central.Get().Revision, values = J(new
                { enabled = false, selectedPresetId = "custom-one", customPresets = Array.Empty<object>() }) }), default);
            Check(save is PluginSettingsSaveResult && File.ReadAllBytes(file).SequenceEqual(bytes),
                "complete clear edits current preferences while retained source bytes stay unchanged");
            Check((await production.CaptureAsync(facts, default)).Disposition == WorkflowContextDisposition.Ready,
                "global disable is independent from the saved chat selection");
            await DisableChat(plugin, facts.Identity.ConversationId);
            Check((await production.CaptureAsync(facts, default)).Disposition == WorkflowContextDisposition.Omitted,
                "per-chat None omits without looking up the removed global definition");
            Check(plugin.Resolve(firstId, 1).Text == "Use compact fixture responses.", "cleared current definitions preserve accepted pins");

            var sidecar = Path.Combine(data, "personality-context.lock");
            var chatSidecar = Path.Combine(data, "personality-chat-state.lock");
            await ChooseChat(plugin, facts.Identity.ConversationId, "packaged", "cerebral", false);
            File.Delete(sidecar);
            File.Delete(chatSidecar);
            central.Set(new(true, "cerebral", []));
            Check((await production.CaptureAsync(facts, default)).Disposition == WorkflowContextDisposition.Unavailable &&
                !File.Exists(chatSidecar), "capture refuses a missing chat read lock without recreating it");
            Reject(() => plugin.Resolve(firstId, 1), "exact provider source read does not recreate a lost sidecar");
            Check(!File.Exists(sidecar), "provider read leaves missing sidecar absent");
            // Task-owned diagnostic cleanup restores only this fixture's empty coordination sidecar.
            using (var replacement = new FileStream(sidecar, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            using (var replacement = new FileStream(chatSidecar, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            central.Set(new(true, "race", [new("race", "Race", "Preserve unused prepared sources.")]));
            sourceState = await Command(plugin, "personality.sources.get", new { });
            expected = central.Get().Revision;
            central.AfterGet = () => central.Set(new(false, "cerebral", []));
            await RejectAsync(async () => { await Command(plugin, "personality.source.prepare", new { schemaVersion = 1,
                expectedSettingsRevision = expected, expectedStoreRevision = sourceState.GetProperty("revision").GetInt64(),
                expectedStoreSha256 = sourceState.GetProperty("sha256").GetString() }); },
                "central revision race refuses completed preparation acknowledgement");
            Check((await Command(plugin, "personality.get", new { id = "race", revision = 1 })).GetProperty("text").GetString()
                == "Preserve unused prepared sources.", "unused append survives central revision conflict");

            // Construct a truthful old 1.2.2 seed independently of new capture; old readers remain pure.
            var oldPreset = PersonalityContract.BuiltIns[0];
            var oldAdmission = new PersonalityAdmission(PersonalityContributionCodec.Submission(facts),
                PersonalityContributionCodec.Consumer(facts), PersonalityContract.SourceId(oldPreset), oldPreset.Revision,
                oldPreset.Sha256, 0, PersonalityContract.Digest([]));
            var inner = oldAdmission.ToSnapshot();
            var originalDigest = PersonalityContributionCodec.Hash(inner);
            var oldPayload = J(new { contract = PersonalityContributionCodec.Contract, version = 1,
                settingsRevision = 0, settingsSha256 = PersonalityContract.Digest([]), seed = inner, seedSha256 = originalDigest });
            var oldSelection = new WorkflowContextSelection(WorkflowContextDisposition.Ready, null, oldPayload,
                PersonalityContributionCodec.Hash(oldPayload), PersonalityContributionCodec.Template(oldAdmission));
            central.FailReads = true;
            Check((await production.RestoreAsync(accepted, oldSelection, default)).References.Single().SourceId == PersonalityContract.SourceId(oldPreset),
                "1.2.2 accepted envelope restores its original source without recapturing new chat state");
            central.FailReads = false;
            var witness = J(new { sharedToken = new string('a', 64), leadToken = new string('b', 64),
                profileRevision = facts.ProfileRevision, profileInstructionsSha256 = new string('c', 64),
                installationId = facts.InstallationId });
            var proof = J(new { retiredWriterGeneration = new string('d', 64), retirementEvidenceId = "fixture-retirement",
                instructions = witness, ownerId = "owner" });
            var fragments = J(new { personalityAdmission = new { schemaVersion = 1, status = "ready", reason = (string?)null,
                seed = inner, seedSha256 = originalDigest }, personalityInstructions = witness, personalityMigration = proof });
            var unknown = accepted with { Accepted = facts with { InstallationId = null } };
            var historical = production.ReadLegacy(new(unknown, 4, fragments), default);
            Check(historical.Disposition == WorkflowContextDisposition.Ready && historical.PayloadDigest == originalDigest &&
                JsonElement.DeepEquals(historical.Payload!.Value, inner),
                "pure legacy conversion preserves original inner seed and digest with unknown header");
            await RejectAsync(async () => { await production.RestoreAsync(unknown, historical, default); },
                "private witness does not manufacture execution origin");
            foreach (var (status, reason, disposition) in new[]
            {
                ("disabled", (string?)null, WorkflowContextDisposition.Omitted),
                ("notApplicable", "scope-excluded", WorkflowContextDisposition.Omitted),
                ("unavailable", "selection-unavailable", WorkflowContextDisposition.Unavailable),
                ("migrationPending", "migration-pending", WorkflowContextDisposition.Blocked),
                ("legacy", "legacy-snapshot", WorkflowContextDisposition.Unsupported)
            })
            {
                var old = J(new { personalityAdmission = new { schemaVersion = 1, status, reason,
                    seed = (object?)null, seedSha256 = (string?)null } });
                var converted = production.ReadLegacy(new(unknown, 4, old), default);
                Check(converted.Disposition == disposition && converted.Reason == reason && converted.Request is null &&
                    converted.Payload is null && converted.PayloadDigest is null,
                    "original legacy outcome is preserved: " + status);
                if (disposition == WorkflowContextDisposition.Blocked)
                    await RejectAsync(async () => { await production.RestoreAsync(accepted, converted, default); },
                        "historical migration-pending cannot be restored as a Ready request");
            }
            var damaged = J(new { personalityAdmission = new { schemaVersion = 1, status = "ready", reason = (string?)null,
                seed = inner, seedSha256 = new string('0', 64) }, personalityInstructions = witness, personalityMigration = proof });
            Reject(() => production.ReadLegacy(new(unknown, 4, damaged), default), "corrupt legacy Ready refuses conversion");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var wasCanceled = false;
            try { await production.CaptureAsync(facts, canceled.Token); } catch (OperationCanceledException) { wasCanceled = true; }
            Check(wasCanceled, "capture propagates cancellation");
            await ChatChecks(plugin, central, data, facts, Check, RejectAsync);
            plugin.Dispose();
            await RejectAsync(async () => { await production.RestoreAsync(accepted, ready, default); },
                "disposed package refuses accepted restore");
            success = true;
            Console.WriteLine($"PASS: {checks} successor package checks; pure policy and production contribution with deterministic central settings and real owned files; no live host/model.");
        }
        finally
        {
            // Delete only this exact successfully verified fixture; failed fixtures stay available for diagnosis.
            if (success && Path.GetFullPath(fixture).StartsWith(requested + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fixture, recursive: true);
        }
    }

    private static WorkflowContextFacts Facts() => new(new("request-fixture", "conversation-fixture", "profile-fixture",
        ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
        new(EventActorKind.User, "owner", "Presentation"), null), "lead", 7, "local", "installation-fixture");
    private static JsonElement J(object value) => PersonalityContract.Element(value);
    private static async Task<JsonElement> Command(PersonalityServicePlugin plugin, string route, object value) =>
        J((await plugin.ExecuteAsync(route, J(value), default))!);

    private static Task<JsonElement> GetChat(PersonalityServicePlugin plugin, string id) =>
        Command(plugin, "personality.chat.get", new { schemaVersion = 1, conversationId = id });
    private static async Task<JsonElement> ChooseChat(PersonalityServicePlugin plugin, string chat, string kind, string id, bool blunt)
    {
        var view = await GetChat(plugin, chat);
        var entry = view.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == kind && e.GetProperty("id").GetString() == id);
        var reply = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = chat,
            expectedRevision = view.GetProperty("revision").GetInt64(), enabled = true,
            baseSelection = new { kind, id, revision = entry.GetProperty("revision").Deserialize<long?>(),
                sha256 = entry.GetProperty("sha256").GetString() }, bluntMode = blunt, catalogToken = view.GetProperty("catalogToken").GetString() });
        if (reply.GetProperty("outcome").GetString() is not ("committed" or "unchanged"))
            throw new InvalidOperationException("Fixture chat selection was not confirmed: " + reply.GetProperty("diagnostic").GetProperty("code").GetString());
        return reply.GetProperty("state");
    }
    private static async Task<JsonElement> DisableChat(PersonalityServicePlugin plugin, string id)
    {
        var view = await GetChat(plugin, id);
        return await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = id,
            expectedRevision = view.GetProperty("revision").GetInt64(), enabled = false, bluntMode = false });
    }

    private static async Task ChatChecks(PersonalityServicePlugin plugin, MemorySettings central, string data,
        WorkflowContextFacts facts, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        const string x = "chat-x";
        const string y = "chat-y";
        var absent = await GetChat(plugin, x);
        check(absent.GetProperty("revision").GetInt64() == 0 && !absent.GetProperty("enabled").GetBoolean() &&
            absent.GetProperty("baseSelection").ValueKind == JsonValueKind.Null && absent.GetProperty("availability").GetString() == "none",
            "new chat has explicit absent None state at revision zero");
        var catalog = absent.GetProperty("catalog").EnumerateArray().ToArray();
        check(catalog.Count(e => e.GetProperty("kind").GetString() == "packaged") == 14 &&
            catalog.Any(e => e.GetProperty("id").GetString() == "gary_2") &&
            !catalog.Any(e => e.GetProperty("id").GetString() is "blunt" or "pegasus"),
            "fourteen packaged choices preserve package gary_2 identity with no fabricated private or blunt persona");
        foreach (var template in Cerebral.PersonalityPlugin.PersonalityTemplates.All)
        {
            var row = catalog.Single(e => e.GetProperty("kind").GetString() == "packaged" && e.GetProperty("id").GetString() == template.PackageKey);
            check(row.GetProperty("revision").GetInt64() == template.Revision &&
                row.GetProperty("sha256").GetString() == PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(template.Text)),
                template.Name + " catalog pin uses its exact per-definition revision and body hash");
            PersonalityContract.ValidatePreset(PersonalityContract.Preset("character-validation", template.Name, 1, template.Text));
            var effectiveText = template.Text + "\n" + Cerebral.PersonalityPlugin.PersonalityTemplates.BluntModeText;
            var effective = new PersonalityEffectiveSource(new("packaged", template.PackageKey, template.Revision,
                PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(template.Text))), PersonalityChatContract.BluntPin,
                template.Text, Cerebral.PersonalityPlugin.PersonalityTemplates.BluntModeText,
                PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(effectiveText)));
            PersonalityChatContract.ValidateSource(effective);
            check(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(PersonalityChatContract.Resolution(effective), PersonalityContract.Json).Length <= 2048,
                template.Name + " exact base plus mode serializes within unchanged provider budget");
        }
        var sourceBefore = await Command(plugin, "personality.sources.get", new { });
        var xSelected = await ChooseChat(plugin, x, "packaged", "gary_2", true);
        var stableToken = xSelected.GetProperty("catalogToken").GetString();
        var ySelected = await ChooseChat(plugin, y, "packaged", "analytical", false);
        var xAfterY = await GetChat(plugin, x);
        check(xAfterY.GetProperty("revision").GetInt64() == xSelected.GetProperty("revision").GetInt64() &&
            xAfterY.GetProperty("catalogToken").GetString() == stableToken && xAfterY.GetProperty("bluntMode").GetBoolean(),
            "chat Y changes neither X's CAS nor the catalog token");
        check((await Command(plugin, "personality.sources.get", new { })).GetProperty("revision").GetInt64() == sourceBefore.GetProperty("revision").GetInt64(),
            "packaged base and mode do not consume custom preparation/history slots");
        var production = plugin.ContextContributions.Single();
        var xf = facts with { Identity = facts.Identity with { RequestId = "request-x", ConversationId = x } };
        var pinned = await production.CaptureAsync(xf, default);
        var accepted = new WorkflowContextAcceptedFacts(xf, null, 11, 2, 3);
        var frozenRequest = await production.RestoreAsync(accepted, pinned, default);
        var effectiveId = frozenRequest.References.Single().SourceId;
        var effectiveText = plugin.Resolve(effectiveId, 1).Text;
        check(pinned.Disposition == WorkflowContextDisposition.Ready && frozenRequest.References.Count == 1 &&
            effectiveText.EndsWith(Cerebral.PersonalityPlugin.PersonalityTemplates.BluntModeText, StringComparison.Ordinal) &&
            J(PersonalityContract.Resolution(plugin.Resolve(effectiveId, 1))).GetRawText().Length > 0,
            "one pinned effective contribution includes base and exact modifier text");
        var changed = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = x,
            expectedRevision = 0, enabled = false, bluntMode = false });
        check(changed.GetProperty("outcome").GetString() == "rejected" &&
            changed.GetProperty("changed").ValueKind == JsonValueKind.False &&
            changed.GetProperty("diagnostic").GetProperty("code").GetString() == "chat-revision-conflict",
            "stale chat CAS is rejected without touching another chat");
        await reject(async () => { await Command(plugin, "personality.chat.save", new { schemaVersion = 1,
            conversationId = x, expectedRevision = 1, enabled = false, bluntMode = false, unexpected = true }); },
            "closed None schema refuses extra fields before effects");
        await reject(async () => { await plugin.ExecuteAsync("personality.chat.get",
            PersonalityContract.Parse("{\"schemaVersion\":1,\"conversationId\":\"chat-x\",\"conversationId\":\"chat-y\"}"), default); },
            "duplicate chat identity fields are rejected");
        await reject(async () => { await Command(plugin, "personality.chat.get", new { schemaVersion = 1, conversationId = " " }); },
            "missing stable identity never becomes global selection");
        central.FailReads = true;
        var disabled = await DisableChat(plugin, x);
        var none = disabled.GetProperty("state");
        check(disabled.GetProperty("outcome").GetString() == "committed" && !none.GetProperty("enabled").GetBoolean() &&
            !none.GetProperty("bluntMode").GetBoolean() && none.GetProperty("bluntPin").ValueKind == JsonValueKind.Null &&
            none.GetProperty("effectiveSource").ValueKind == JsonValueKind.Null &&
            none.GetProperty("baseSelection").GetProperty("id").GetString() == "gary_2" &&
            none.GetProperty("catalogToken").ValueKind == JsonValueKind.Null && none.GetProperty("catalog").GetArrayLength() == 0,
            "None preserves remembered base and clears mode even when catalog/central settings are unavailable");
        check((await production.CaptureAsync(xf, default)).Disposition == WorkflowContextDisposition.Omitted &&
            (await production.RestoreAsync(accepted, pinned, default)).References.Single().SourceId == effectiveId,
            "None affects future capture while accepted work restores the original base plus mode");
        production.Validate(xf, xf, pinned, default);
        central.FailReads = false;
        using (var reopened = new PersonalityServicePlugin())
        {
            reopened.Initialize(new Host(data, central));
            var restoredX = await GetChat(reopened, x);
            var restoredY = await GetChat(reopened, y);
            check(!restoredX.GetProperty("enabled").GetBoolean() && restoredX.GetProperty("baseSelection").GetProperty("id").GetString() == "gary_2" &&
                restoredX.GetProperty("revision").GetInt64() == none.GetProperty("revision").GetInt64() && restoredY.GetProperty("enabled").GetBoolean() &&
                restoredY.GetProperty("baseSelection").GetProperty("id").GetString() == "analytical",
                "new plugin instance restores independent X/None and Y/Analytical selections");
        }
        var unchanged = await DisableChat(plugin, x);
        check(unchanged.GetProperty("outcome").GetString() == "unchanged" &&
            unchanged.GetProperty("state").GetProperty("revision").GetInt64() == none.GetProperty("revision").GetInt64(),
            "exact repeated None at fresh CAS is unchanged without revision inflation");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var wasCancelled = false;
            try { await plugin.ExecuteAsync("personality.chat.save", J(new { schemaVersion = 1, conversationId = y,
                expectedRevision = ySelected.GetProperty("revision").GetInt64(), enabled = false, bluntMode = false }), cancelled.Token); }
            catch (OperationCanceledException) { wasCancelled = true; }
            check(wasCancelled && (await GetChat(plugin, y)).GetProperty("revision").GetInt64() == ySelected.GetProperty("revision").GetInt64(),
                "cancellation before dispatch leaves persisted chat unchanged");
        }
        var capText = string.Join(" ", Enumerable.Repeat("word", 120));
        central.Set(new(false, "word-limit", [new("word-limit", "120 word base", capText)]));
        var beforePreparation = await GetChat(plugin, "other-custom-chat");
        var unpreparedRow = beforePreparation.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "custom");
        var composed = await ChooseChat(plugin, "word-chat", "custom", "word-limit", true);
        check(plugin.Resolve(composed.GetProperty("effectiveSource").GetProperty("sourceId").GetString()!, 1).Text
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 120,
            "valid 120-word base composes mode under serialized provider budget without raising the base bound");
        var preparedElsewhere = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = "other-custom-chat",
            expectedRevision = 0, enabled = true, baseSelection = new { kind = "custom", id = "word-limit", revision = (long?)null,
                sha256 = unpreparedRow.GetProperty("sha256").GetString() }, bluntMode = false,
            catalogToken = beforePreparation.GetProperty("catalogToken").GetString() });
        check(preparedElsewhere.GetProperty("outcome").GetString() == "committed" &&
            preparedElsewhere.GetProperty("state").GetProperty("catalogToken").GetString() == beforePreparation.GetProperty("catalogToken").GetString(),
            "another chat preparing identical custom text changes neither catalog token nor this chat's valid null-revision request");
        string? escaping = null;
        for (var n = 1024; n >= 256 && escaping is null; n--)
        {
            var candidate = new string('\\', n);
            try { PersonalityContract.ValidatePreset(PersonalityContract.Preset("escaping", "Escaped", 1, candidate)); escaping = candidate; }
            catch (InvalidDataException) { }
        }
        check(escaping is not null, "escaping fixture has a valid admitted base");
        central.Set(new(false, "escaping", [new("escaping", "Escaped", escaping!)]));
        var escapeView = await GetChat(plugin, "escape-chat");
        var escapeRow = escapeView.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "custom");
        var oversized = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = "escape-chat",
            expectedRevision = 0, enabled = true, baseSelection = new { kind = "custom", id = "escaping", revision = (long?)null,
                sha256 = escapeRow.GetProperty("sha256").GetString() }, bluntMode = true, catalogToken = escapeView.GetProperty("catalogToken").GetString() });
        check(oversized.GetProperty("outcome").GetString() == "rejected" &&
            oversized.GetProperty("diagnostic").GetProperty("code").GetString() == "effective-style-too-large" &&
            (await GetChat(plugin, "escape-chat")).GetProperty("revision").GetInt64() == 0,
            "serialized effective overflow rejects cleanly without truncating or committing a chat");

        const string privateText = "Use the synthetic owner-only fixture avatar style.";
        var privateBefore = await Command(plugin, "personality.private.get", new { schemaVersion = 1 });
        var imported = await Command(plugin, "personality.private.import", new { schemaVersion = 1,
            expectedLibraryRevision = privateBefore.GetProperty("libraryRevision").GetInt64(),
            definition = new { id = "fixture-avatar", name = "Fixture private avatar", text = privateText },
            provenance = new { @namespace = "knowledge.user", recordType = "knowledge", recordId = "fixture.avatar", revision = 1, sha256 = new string('a', 64) } });
        check(imported.GetProperty("outcome").GetString() == "committed" && !imported.GetRawText().Contains(privateText, StringComparison.Ordinal),
            "explicit synthetic private import acknowledges metadata only");
        var privateView = await ChooseChat(plugin, x, "private", "fixture-avatar", false);
        var privateFirstId = privateView.GetProperty("effectiveSource").GetProperty("sourceId").GetString()!;
        var ordinaryDocument = new PluginSettingsDocument(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope,
            null, PersonalitySettingsPolicy.PackageVersion, 2, PersonalitySettingsCodec.Write(new(false, "escaping", [new("escaping", "Escaped", escaping!)])));
        check(!J(plugin.ExportComplete(ordinaryDocument)).GetRawText().Contains(privateText, StringComparison.Ordinal) &&
            !J(plugin.ExportPortable(ordinaryDocument)).GetRawText().Contains("fixture-avatar", StringComparison.Ordinal) &&
            !(await GetChat(plugin, x)).GetRawText().Contains(privateText, StringComparison.Ordinal),
            "ordinary exports and chat catalog exclude private text and provenance bodies");
        var updatedImport = await Command(plugin, "personality.private.import", new { schemaVersion = 1,
            expectedLibraryRevision = imported.GetProperty("libraryRevision").GetInt64(),
            definition = new { id = "fixture-avatar", name = "Fixture private avatar", text = "Use the revised synthetic owner-only fixture style." },
            provenance = new { @namespace = "knowledge.user", recordType = "knowledge", recordId = "fixture.avatar", revision = 2, sha256 = new string('b', 64) } });
        check(updatedImport.GetProperty("outcome").GetString() == "committed" && plugin.Resolve(privateFirstId, 1).Text == privateText &&
            (await GetChat(plugin, x)).GetProperty("baseSelection").GetProperty("revision").GetInt64() == 1,
            "updating private library preserves saved chat and prior immutable source pins");
        central.Set(new(false, "fixture-avatar", [new("fixture-avatar", "Ordinary custom avatar", "Use an ordinary fixture custom style.")]));
        var collision = await GetChat(plugin, x);
        check(collision.GetProperty("catalog").EnumerateArray().Count(e => e.GetProperty("id").GetString() == "fixture-avatar") == 2,
            "qualified custom and private identities can share a bare ID without reinterpretation");
        var beforeLostReply = await GetChat(plugin, y);
        _ = await ChooseChat(plugin, y, "packaged", "succinct", false); // Deliberately discard the mutation acknowledgement.
        var reconciled = await GetChat(plugin, y);
        check(reconciled.GetProperty("revision").GetInt64() == beforeLostReply.GetProperty("revision").GetInt64() + 1 &&
            reconciled.GetProperty("baseSelection").GetProperty("id").GetString() == "succinct",
            "simulated lost acknowledgement reconciles through readback without replaying a mutation");
        var chatPath = Path.Combine(data, PersonalityChatStore.FileName);
        var savedBytes = File.ReadAllBytes(chatPath);
        try
        {
            File.WriteAllText(chatPath, "{\"schemaVersion\":1}");
            await reject(async () => { await GetChat(plugin, x); }, "malformed persisted chat data is not treated as absent None");
            check((await production.CaptureAsync(xf, default)).Disposition == WorkflowContextDisposition.Unavailable &&
                (await production.RestoreAsync(accepted, pinned, default)).References.Single().SourceId == effectiveId,
                "corrupt live store cannot trigger recapture of an accepted seed");
        }
        finally { File.WriteAllBytes(chatPath, savedBytes); }
        await CapacityChecks(Path.Combine(data, "capacity-fixture"), check);
        await ArchivedCharacterChecks(Path.Combine(data, "archived-character-fixture"), facts, check);
    }

    private static async Task ArchivedCharacterChecks(string data, WorkflowContextFacts facts, Action<bool, string> check)
    {
        using var plugin = new PersonalityServicePlugin();
        var central = new MemorySettings(plugin);
        plugin.Initialize(new Host(data, central));
        const string chat = "archived-kvn-chat";
        var old = Cerebral.PersonalityPlugin.PersonalityTemplates.RevisionOne.Single(t => t.PackageKey == "kvn");
        var oldHash = PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(old.Text));
        var oldPin = new PersonalityDefinitionPin("packaged", "kvn", 1, oldHash);
        var oldSource = new PersonalityEffectiveSource(oldPin, null, old.Text, null, oldHash);
        // Controlled reconstruction of the frozen 1.2.3 user-data format, not installed data or an
        // implementation shortcut. An earlier save would already have retained this exact body.
        var previous = new PersonalityChatDocument(1, 0, [], [oldSource],
            [new(chat, 1, true, oldPin, false, null, oldSource.Pin)]);
        File.WriteAllBytes(Path.Combine(data, PersonalityChatStore.FileName), JsonSerializer.SerializeToUtf8Bytes(previous, PersonalityContract.Json));
        var view = await GetChat(plugin, chat);
        check(view.GetProperty("availability").GetString() == "ready" &&
            view.GetProperty("baseSelection").GetProperty("revision").GetInt64() == 1 &&
            view.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "packaged" &&
                e.GetProperty("id").GetString() == "kvn").GetProperty("revision").GetInt64() == 2,
            "earlier saved KVN stays ready at revision one while catalog offers revision two");
        var facet = plugin.ContextContributions.Single();
        var identity = facts with { Identity = facts.Identity with { RequestId = "archived-request", ConversationId = chat } };
        var acceptedSeed = await facet.CaptureAsync(identity, default);
        var accepted = new WorkflowContextAcceptedFacts(identity, null, 1, 0, 0);
        var oldRequest = await facet.RestoreAsync(accepted, acceptedSeed, default);
        check(plugin.Resolve(oldRequest.References.Single().SourceId, 1).Text == old.Text &&
            Cerebral.PersonalityPlugin.PersonalityTemplates.Exact("kvn", 1, oldHash)?.Text == old.Text,
            "retained accepted source and public preview resolver preserve exact original KVN wording");
        var mode = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = chat,
            expectedRevision = 1, enabled = true, baseSelection = oldPin, bluntMode = true,
            catalogToken = view.GetProperty("catalogToken").GetString() });
        check(mode.GetProperty("outcome").GetString() == "committed" &&
            mode.GetProperty("state").GetProperty("baseSelection").GetProperty("revision").GetInt64() == 1 &&
            plugin.Resolve(mode.GetProperty("state").GetProperty("effectiveSource").GetProperty("sourceId").GetString()!, 1).Text ==
                old.Text + "\n" + Cerebral.PersonalityPlugin.PersonalityTemplates.BluntModeText,
            "mode-only save on earlier selected definition never silently upgrades its body");
        var other = await GetChat(plugin, "foreign-archive-chat");
        var rejected = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = "foreign-archive-chat",
            expectedRevision = 0, enabled = true, baseSelection = oldPin, bluntMode = false,
            catalogToken = other.GetProperty("catalogToken").GetString() });
        check(rejected.GetProperty("outcome").GetString() == "rejected" &&
            rejected.GetProperty("diagnostic").GetProperty("code").GetString() == "selection-unavailable",
            "historical-pin exception cannot select an unoffered revision into another chat");
        var updated = await ChooseChat(plugin, chat, "packaged", "kvn", false);
        check(updated.GetProperty("baseSelection").GetProperty("revision").GetInt64() == 2 &&
            (await facet.RestoreAsync(accepted, acceptedSeed, default)).References.Single().SourceId == oldRequest.References.Single().SourceId &&
            plugin.Resolve(oldRequest.References.Single().SourceId, 1).Text == old.Text,
            "explicit enriched selection changes future turns while accepted original source remains unchanged");
    }

    private static async Task CapacityChecks(string data, Action<bool, string> check)
    {
        using var plugin = new PersonalityServicePlugin();
        var central = new MemorySettings(plugin);
        plugin.Initialize(new Host(data, central));
        for (var index = 0; index < 7; index++)
        {
            var key = "capacity-" + index;
            central.Set(new(false, key, [new(key, key, "Use a bounded capacity fixture style.")]));
            await ChooseChat(plugin, key, "custom", key, false);
        }
        var imported = await Command(plugin, "personality.private.import", new { schemaVersion = 1, expectedLibraryRevision = 0,
            definition = new { id = "private-capacity", name = "Private capacity fixture", text = "Use the synthetic private capacity style." },
            provenance = new { @namespace = "knowledge.user", recordType = "knowledge", recordId = "fixture.capacity", revision = 1, sha256 = new string('c', 64) } });
        check(imported.GetProperty("outcome").GetString() == "committed", "private import consumes the eighth original retained custom identity slot");
        central.Set(new(false, "capacity-overflow", [new("capacity-overflow", "Overflow", "Use an extra fixture style.")]));
        var view = await GetChat(plugin, "overflow-chat");
        var row = view.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "custom");
        var rejected = await Command(plugin, "personality.chat.save", new { schemaVersion = 1, conversationId = "overflow-chat",
            expectedRevision = 0, enabled = true, baseSelection = new { kind = "custom", id = "capacity-overflow", revision = (long?)null,
                sha256 = row.GetProperty("sha256").GetString() }, bluntMode = false, catalogToken = view.GetProperty("catalogToken").GetString() });
        check(rejected.GetProperty("outcome").GetString() == "rejected" && rejected.GetProperty("diagnostic").GetProperty("code").GetString() == "source-capacity",
            "combined custom/private identity quota rejects overflow with no eviction");
        var legacy = await Command(plugin, "personality.sources.get", new { });
        check(legacy.GetProperty("retention").GetProperty("customPresetCount").GetInt32() == 8 &&
            legacy.GetProperty("retention").GetProperty("maximumCustomPresets").GetInt32() == 8,
            "legacy source metadata reports combined private quota consumption without raising caps");
        using var versionsPlugin = new PersonalityServicePlugin();
        var versionsCentral = new MemorySettings(versionsPlugin);
        versionsPlugin.Initialize(new Host(Path.Combine(data, "version-capacity"), versionsCentral));
        for (var version = 1; version <= 31; version++)
        {
            versionsCentral.Set(new(false, "version-style", [new("version-style", "Version style", "Use bounded version " + version + " fixture responses.")]));
            await ChooseChat(versionsPlugin, "version-chat", "custom", "version-style", false);
        }
        var lastSlot = await Command(versionsPlugin, "personality.private.import", new { schemaVersion = 1, expectedLibraryRevision = 0,
            definition = new { id = "version-private", name = "Private version fixture", text = "Use a synthetic private final version slot." },
            provenance = new { @namespace = "knowledge.user", recordType = "knowledge", recordId = "fixture.versions", revision = 1, sha256 = new string('d', 64) } });
        check(lastSlot.GetProperty("outcome").GetString() == "committed", "private import consumes the thirty-second retained version slot");
        versionsCentral.Set(new(false, "version-style", [new("version-style", "Version style", "Use a new revision after all retained slots are full.")]));
        var versionView = await GetChat(versionsPlugin, "version-chat");
        var versionRow = versionView.GetProperty("catalog").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "custom");
        var overflow = await Command(versionsPlugin, "personality.chat.save", new { schemaVersion = 1, conversationId = "version-chat",
            expectedRevision = versionView.GetProperty("revision").GetInt64(), enabled = true,
            baseSelection = new { kind = "custom", id = "version-style", revision = (long?)null, sha256 = versionRow.GetProperty("sha256").GetString() },
            bluntMode = false, catalogToken = versionView.GetProperty("catalogToken").GetString() });
        check(overflow.GetProperty("outcome").GetString() == "rejected" &&
            overflow.GetProperty("diagnostic").GetProperty("code").GetString() == "source-capacity" &&
            (await Command(versionsPlugin, "personality.sources.get", new { })).GetProperty("retention").GetProperty("retainedCustomVersions").GetInt32() == 32,
            "combined retained-version overflow preserves all 32 versions and the previous chat pin");
    }

    private sealed class Host(string path, MemorySettings preferences) : IServicePluginHost, IServicePluginSettingsHost
    {
        public string PluginId => PersonalityContract.PluginId;
        public string PackageDirectory => path;
        public string DataDirectory => path;
        public IScopedPluginSettings Settings => preferences;
        public void Publish(string eventType, string? jobId, object? data) { }
    }

    // Deterministic central transport fixture. Actual generic SQLite/pipe evidence is separate.
    private sealed class MemorySettings(IPluginCompleteSettingsPolicy policy) : IScopedPluginSettings
    {
        private PluginSettingsDocument? active;
        private long revision;
        internal int Reads;
        internal bool FailReads;
        internal Action? AfterGet;
        public PluginSettingsState Get(string scope = "global", string? scopeId = null)
        {
            if (FailReads) throw new IOException("Injected central read failure.");
            Reads++;
            var snapshot = new PluginSettingsState(new(PersonalityContract.PluginId, scope, scopeId), revision, active, [], "service");
            var callback = AfterGet; AfterGet = null; callback?.Invoke();
            return snapshot;
        }
        public PluginSettingsSaveResult Save(int version, JsonElement values, long expectedRevision,
            string scope = "global", string? scopeId = null)
        {
            if (expectedRevision != revision) throw new InvalidOperationException("Fixture CAS mismatch.");
            var document = new PluginSettingsDocument(PersonalityContract.PluginId, scope, scopeId,
                PersonalitySettingsPolicy.PackageVersion, version, values);
            var admitted = policy.AdmitComplete(document, active?.Values);
            if (!admitted.Accepted) throw new InvalidDataException("Fixture policy rejected.");
            var changed = active is null || !JsonElement.DeepEquals(active.Values, admitted.Values);
            if (changed) { active = document with { SettingsVersion = admitted.SettingsVersion, Values = admitted.Values }; revision++; }
            return new(Get(scope, scopeId), changed);
        }
        internal void Set(PersonalitySettingsPreferences preferences)
        { _ = Save(2, PersonalitySettingsCodec.Write(preferences), revision); }
    }
}
