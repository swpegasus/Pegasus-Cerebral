using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.PersonalityPlugin;
using ServicePolicy = Cerebral.PersonalityServicePlugin.PersonalitySettingsPolicy;

namespace Cerebral.PersonalityPlugin.Tests;
internal static partial class Program
{
    private static readonly JsonSerializerOptions TransferJson = new(JsonSerializerDefaults.Web);
    private static JsonElement JsonValue(object value) => JsonSerializer.SerializeToElement(value, TransferJson);
    private static JsonElement Full(bool enabled, string id = "succinct", params object[] definitions) =>
        JsonValue(new { enabled, selectedPresetId = id, customPresets = definitions });
    private static object Definition(string id, string text = "Use precise practical language.") => new { id, name = "Imported " + id, text };
    private static PersonalityEditor PendingEditor(Host host) => new(host, new(host.DataDirectory), () => host.Binding);
    private static async Task TransferChecks()
    {
        var before = checks;
        using (var host = new Host(Path.Combine(root, "pending-mixed")))
        {
            host.Central.Stage("complete", SettingsTransferPurpose.Complete, JsonValue(new { enabled = true, selectedPresetId = "succinct" }), 1);
            host.Central.Stage("portable", SettingsTransferPurpose.Portable, JsonValue(new { enabled = false, selectedPresetId = "cerebral" }));
            host.Central.Stage("local", SettingsTransferPurpose.Local, Full(false, "style-local", Definition("style-local")));
            var editor = PendingEditor(host); var snapshot = await editor.LoadPendingAsync(); var initial = host.Central.GetTransfer();
            Check(snapshot.Inputs.Count == 3 && host.Mutations.Count == 0, "pending load is read-only and lists complete/portable/local purposes");
            await Reject(() => editor.LoadAsync(), "normal schema2 getter keeps complete pending refusal");
            var prepared = await editor.PreparePendingAsync(snapshot, "complete");
            Check(prepared.Values.Enabled && prepared.Values.SelectedPresetId == "succinct" && prepared.Values.CustomPresets.Count == 0, "selected schema1 complete input migrates into full current preferences");
            Check(JsonElement.DeepEquals(JsonValue(initial), JsonValue(host.Central.GetTransfer())), "preparation leaves active and all pending documents/purposes untouched");
            Check(host.Content == "Owner instructions.", "preparation leaves instructions untouched");
            var result = await editor.ApplyPendingAsync(prepared);
            Check(result.Inputs.Select(x => x.Id).SequenceEqual(["portable", "local"]), "successful Apply consumes only selected complete input and purpose");
            Check(host.CurrentPreferences.Enabled && host.CurrentPreferences.SelectedPresetId == "succinct", "Apply stores exact reviewed full values");
            Check(host.Mutations.SequenceEqual(["personality.preferences.pending.prepare", "personality.preferences.pending.apply"]), "no legacy section needs no local disable/restore/instructions mutation");
            var count = host.Mutations.Count; await Reject(() => editor.ApplyPendingAsync(prepared), "successful Apply cannot replay");
            Check(host.Mutations.Count == count, "consumed Apply refuses before dispatch");
            prepared = await editor.PreparePendingAsync(result, "local"); result = await editor.ApplyPendingAsync(prepared);
            Check(result.Inputs.Single().Id == "portable" && host.CurrentPreferences.CustomPresets.Single().Id == "style-local", "selected local input stores definitions and preserves portable input");
            prepared = await editor.PreparePendingAsync(result, "portable"); result = await editor.ApplyPendingAsync(prepared);
            Check(result.Inputs.Count == 0 && host.CurrentPreferences.CustomPresets.Single().Id == "style-local", "portable pair preserves destination-owned custom definitions");
        }
        foreach (var purpose in new[] { SettingsTransferPurpose.Portable, SettingsTransferPurpose.Complete })
        {
            using var host = new Host(Path.Combine(root, "legacy-" + purpose)); var store = new PersonalitySettingsStore(host.DataDirectory);
            var legacy = new PersonalityPreset("previous", "Previous", "Use the previous marked style.");
            store.Save(new(true, legacy.Id, [legacy]), store.Load().Revision);
            var prefix = "Preserve original prefix.\r\n"; var suffix = "\r\nPreserve original suffix.";
            host.Content = PersonalityInstructionLayer.Apply(prefix, legacy, true) + suffix;
            await host.SeedDefinition("style-original", "Original destination", "Use exact original destination preferences.");
            await host.Prepare(); await host.Select(true, "style-original");
            var original = host.Central.Current.Active!.Values.Clone(); var file = File.ReadAllBytes(store.Path);
            host.Central.Stage("chosen", purpose, purpose == SettingsTransferPurpose.Portable ? JsonValue(new { }) : Full(true, "succinct"));
            host.Central.Stage("retained", SettingsTransferPurpose.Local, Full(false, "cerebral"));
            var editor = PendingEditor(host); var snapshot = await editor.LoadPendingAsync(); host.Mutations.Clear();
            var prepared = await editor.PreparePendingAsync(snapshot, "chosen"); var revision = host.Central.Current.Revision;
            Check(host.Central.Current.Revision == revision && host.Content.Contains(PersonalityInstructionLayer.StartMarker, StringComparison.Ordinal), purpose + " review has no preference or legacy cleanup mutation");
            var result = await editor.ApplyPendingAsync(prepared);
            Check(host.EnabledAtInstructionsSave == false, purpose + " legacy instructions CAS occurs with desired preference disabled");
            Check(host.Content == prefix + suffix && File.ReadAllBytes(store.Path).SequenceEqual(file), purpose + " cleanup preserves exact surrounding instructions and local legacy bytes");
            Check(result.Inputs.Single().Id == "retained", purpose + " cleanup and restoration preserve unrelated pending entry");
            Check(host.Central.LocalSaves.Count == 2 && !host.Central.LocalSaves[0].GetProperty("enabled").GetBoolean() &&
                JsonElement.DeepEquals(host.Central.LocalSaves[1], original), purpose + " cleanup restores the captured full original destination before selected Apply");
            Check(JsonElement.DeepEquals(host.Central.Current.Active!.Values, prepared.RawValues), purpose + " final state equals reviewed admitted full values");
            Check(host.CurrentPreferences.Enabled, purpose + " reviewed enabled state remains enabled");
        }
        using (var host = new Host(Path.Combine(root, "disabled-missing")))
        {
            host.Central.Stage("missing", SettingsTransferPurpose.Complete, Full(false, "style-unavailable"));
            var editor = PendingEditor(host); var p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "missing");
            Check(p.Preset is null && !p.Values.Enabled, "disabled missing desired definition prepares no replacement source");
            await editor.ApplyPendingAsync(p);
            Check(host.CurrentPreferences.SelectedPresetId == "style-unavailable" && !host.CurrentPreferences.Enabled, "disabled source-free Apply preserves unavailable selection");
        }
        using (var host = new Host(Path.Combine(root, "enabled-missing")))
        {
            host.Central.Stage("missing", SettingsTransferPurpose.Complete, Full(true, "style-unavailable"));
            var editor = PendingEditor(host); var s = await editor.LoadPendingAsync(); var rev = host.Central.Current.Revision;
            await Reject(() => editor.PreparePendingAsync(s, "missing"), "enabled missing definition cannot prepare");
            Check(host.Central.Current.Revision == rev && host.Central.Current.Pending.Count == 1, "enabled missing definition preserves active and pending state");
        }
        using (var host = new Host(Path.Combine(root, "large-retained")))
        {
            for (var i = 0; i < 6; i++) host.Central.Stage("future-" + i, SettingsTransferPurpose.Complete, JsonValue(new { opaque = new string('x', 120 * 1024) }), 9);
            host.Central.Stage("valid", SettingsTransferPurpose.Complete, Full(false));
            var editor = PendingEditor(host); var s = await editor.LoadPendingAsync(); var p = await editor.PreparePendingAsync(s, "valid");
            Check(s.Inputs.Count == 7 && p.Input.Id == "valid", "large retained unsupported originals do not block preparing a small valid selected entry");
            var result = await editor.ApplyPendingAsync(p);
            Check(result.Inputs.Count == 6 && result.Inputs.All(x => x.SettingsVersion == 9), "large unsupported originals remain exact and pending after valid selected Apply");
        }
        foreach (var fault in new[] { "map-missing", "map-duplicate", "map-unknown", "foreign", "wrong-envelope", "wrong-portable" })
        {
            using var host = PendingHost("load-" + fault); host.TransferFaultName = fault;
            await Reject(() => PendingEditor(host).LoadPendingAsync(), "malformed transfer load refuses " + fault);
            Check(host.Mutations.Count == 0, fault + " malformed load performs no mutation");
        }
        foreach (var fault in new[] { "prepared-id", "prepared-purpose", "prepared-revision", "prepared-values-hash", "prepared-pending-hash", "prepared-source", "prepared-null-source" })
        {
            using var host = PendingHost("prepare-" + fault); var editor = PendingEditor(host); var s = await editor.LoadPendingAsync(); host.TransferFaultName = fault;
            await Reject(() => editor.PreparePendingAsync(s, "chosen"), "prepared reply refuses " + fault);
            Check(host.Central.Current.Pending.Count == 2 && host.Central.Current.Active is null, fault + " no selected admission after malformed preparation");
            var count = host.Mutations.Count; await Reject(() => editor.PreparePendingAsync(s, "chosen"), fault + " consumed preparation cannot replay"); Check(count == host.Mutations.Count, fault + " replay does not dispatch");
        }
        foreach (var fault in new[] { "save-revision", "save-values", "save-pending", "save-map" })
        {
            using var host = PendingHost("ack-" + fault); var editor = PendingEditor(host); var p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "chosen"); host.TransferFaultName = fault;
            await Reject(() => editor.ApplyPendingAsync(p), "selected save acknowledgement refuses " + fault);
            var count = host.Mutations.Count; await Reject(() => editor.ApplyPendingAsync(p), fault + " unknown acknowledgement cannot replay");
            Check(count == host.Mutations.Count && host.Central.Current.Pending.Single().Id == "other", fault + " committed state reconciles without replay");
            Check((await editor.LoadPendingAsync()).Inputs.Single().Id == "other", fault + " explicit reload exposes actual selected removal");
        }
        foreach (var phase in new[] { "disable", "instructions", "restore", "apply" })
        {
            using var host = PendingHost("lost-" + phase); var store = new PersonalitySettingsStore(host.DataDirectory);
            var legacy = new PersonalityPreset("previous", "Previous", "Use an old marked style."); store.Save(new(true, legacy.Id, [legacy]), store.Load().Revision);
            host.Content = PersonalityInstructionLayer.Apply("Original instructions.", legacy, true);
            // Current built-in preparation is independent of the selected incoming style.
            host.Central.SetActive(Full(true, "cerebral"));
            var editor = PendingEditor(host); var p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "chosen");
            if (phase == "disable") host.LostLocalSave = 1;
            else if (phase == "restore") host.LostLocalSave = 2;
            else host.FailAfter = phase == "instructions" ? "instructions.save" : "personality.preferences.pending.apply";
            await Reject(() => editor.ApplyPendingAsync(p), "commit then lost acknowledgement at " + phase);
            var count = host.Mutations.Count; await Reject(() => editor.ApplyPendingAsync(p), "no replay after lost " + phase);
            Check(count == host.Mutations.Count, "lost " + phase + " baseline remains consumed");
            var actual = await editor.LoadPendingAsync();
            Check(actual.Inputs.Count == (phase == "apply" ? 1 : 2) &&
                host.CurrentPreferences.Enabled == (phase is "restore" or "apply"), "lost " + phase + " explicit reload shows actual pending and enable state");
        }
        foreach (var change in new[] { "central", "sources", "lead", "owner", "local" })
        {
            using var host = PendingHost("stale-" + change); var editor = PendingEditor(host);
            var p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "chosen");
            if (change == "central") host.Central.Stage("new", SettingsTransferPurpose.Local, Full(false));
            if (change == "sources") {
                host.Central.Stage("custom", SettingsTransferPurpose.Local, Full(false, "style-race", Definition("style-race")));
                // Re-review establishes identical central state for both preparations, then only sources append.
                p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "chosen");
                await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "custom");
            }
            if (change == "lead") host.Content += " Changed externally.";
            if (change == "owner") host.Binding = new("configuration-owner-other", null);
            if (change == "local") { var store = new PersonalitySettingsStore(host.DataDirectory); store.Save(new(false,"previous",[new PersonalityPreset("previous","Previous","Use a changed local selection.")]),store.Load().Revision); }
            var count = host.Mutations.Count;
            await Reject(() => editor.ApplyPendingAsync(p), "stale " + change + " refuses before preference mutation");
            Check(count == host.Mutations.Count && host.Central.Current.Active is null, "stale " + change + " does not apply or clean instructions");
        }
        using (var host = PendingHost("unknown-legacy"))
        {
            host.Content = "Owner instructions.\n" + PersonalityInstructionLayer.StartMarker + "\nUnknown edited body.\n" + PersonalityInstructionLayer.EndMarker;
            var editor = PendingEditor(host); var p = await editor.PreparePendingAsync(await editor.LoadPendingAsync(), "chosen"); var count = host.Mutations.Count;
            await Reject(() => editor.ApplyPendingAsync(p), "unknown edited legacy section refuses explicit Apply");
            Check(host.Mutations.Count == count && host.Central.Current.Active is null, "unknown legacy refusal preserves instructions and pending state");
        }

        using (var host = new Host(Path.Combine(root, "unprepared-enabled-legacy")))
        {
            var store = new PersonalitySettingsStore(host.DataDirectory);
            var legacy = new PersonalityPreset("previous", "Previous", "Use a marked old style.");
            store.Save(new(true,legacy.Id,[legacy]),store.Load().Revision);
            host.Content = PersonalityInstructionLayer.Apply("Original owner text.",legacy,true);
            host.Central.SetActive(Full(true,"style-unprepared",Definition("style-unprepared")));
            host.Central.Stage("portable",SettingsTransferPurpose.Portable,JsonValue(new {}));
            var original = host.Central.Current.Active!.Values.Clone(); var editor = PendingEditor(host);
            var snapshot = await editor.LoadPendingAsync(); var count = host.Mutations.Count;
            Check(!host.CurrentSourceAvailable, "enabled destination definition starts without a prepared source");
            await Reject(()=>editor.PreparePendingAsync(snapshot,"portable"), "Review refuses activating an unavailable enabled source beside legacy instructions");
            Check(host.Mutations.Count==count && !host.CurrentSourceAvailable && JsonElement.DeepEquals(host.Central.Current.Active!.Values,original),
                "refused Review preserves source availability and exact enabled destination");
            var cleaned = await editor.CleanPendingLegacyAsync(snapshot);
            Check(host.Mutations.SequenceEqual(["personality.preferences.transfer.save","personality.preferences.transfer.prepare","instructions.save","personality.preferences.transfer.save"]),
                "explicit cleanup disables before current-source append then removes section and restores original");
            Check(host.SourceBoundaries.All(x=>!x.Enabled || !x.Legacy || !x.Available),
                "no observed cleanup mutation boundary has enabled available current source beside legacy instructions");
            Check(host.CurrentSourceAvailable && host.Content=="Original owner text." &&
                JsonElement.DeepEquals(host.Central.Current.Active!.Values,original) && cleaned.Inputs.Single().Id=="portable",
                "explicit cleanup leaves exact original enabled values, prepared source and retained pending import");
            var prepared = await editor.PreparePendingAsync(cleaned,"portable"); await editor.ApplyPendingAsync(prepared);
            Check(JsonElement.DeepEquals(host.Central.Current.Active!.Values,original) && host.Central.Current.Pending.Count==0,
                "portable empty after explicit cleanup preserves reviewed original enabled full destination");
        }
        using (var host = new Host(Path.Combine(root,"disabled-legacy-cleanup")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory); var legacy=new PersonalityPreset("previous","Previous","Use an old style.");
            store.Save(new(true,legacy.Id,[legacy]),store.Load().Revision); host.Content=PersonalityInstructionLayer.Apply("Owner text.",legacy,true);
            host.Central.SetActive(Full(false)); host.Central.Stage("chosen",SettingsTransferPurpose.Complete,Full(false));
            var editor=PendingEditor(host); var snapshot=await editor.LoadPendingAsync(); var result=await editor.CleanPendingLegacyAsync(snapshot);
            Check(host.Mutations.SequenceEqual(["instructions.save"]) && host.Central.LocalSaves.Count==0,
                "already-disabled cleanup performs no disable/restore/source preparation");
            var count=host.Mutations.Count; await Reject(()=>editor.CleanPendingLegacyAsync(snapshot),"explicit cleanup baseline cannot replay");
            Check(count==host.Mutations.Count && result.Inputs.Count==1,"consumed cleanup preserves pending queue without replay");
        }


        using(var host=new Host(Path.Combine(root,"legacy-added-before-review")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var old=new PersonalityPreset("previous","Previous","Use an old marked style.");
            store.Save(new(true,old.Id,[old]),store.Load().Revision);
            host.Central.SetActive(Full(true,"style-current",Definition("style-current")));host.Central.Stage("portable",SettingsTransferPurpose.Portable,JsonValue(new{}));
            var editor=PendingEditor(host);var snapshot=await editor.LoadPendingAsync();var count=host.Mutations.Count;
            host.Content=PersonalityInstructionLayer.Apply(host.Content,old,true);
            await Reject(()=>editor.PreparePendingAsync(snapshot,"portable"),"legacy section added after Load refuses Review before preparation");
            Check(host.Mutations.Count==count&&!host.CurrentSourceAvailable&&host.Central.Current.Active!.Values.GetProperty("enabled").GetBoolean(),
                "stale lead Review makes no source append and preserves enabled current values");
        }

        Console.WriteLine("TRANSFER IN-PROCESS CHECKS: " + (checks - before));
        await PendingNativeChecks();
        await PendingCleanupNativeChecks();
    }
    private static Host PendingHost(string path)
    {
        var host = new Host(Path.Combine(root, path));
        host.Central.Stage("chosen", SettingsTransferPurpose.Complete, Full(true, "succinct"));
        host.Central.Stage("other", SettingsTransferPurpose.Local, Full(false, "cerebral")); return host;
    }
    private sealed partial class Host
    {
        internal string? TransferFaultName; internal int LostLocalSave;
        internal readonly List<(bool Enabled,bool Legacy,bool Available)> SourceBoundaries=[];
        internal bool CurrentSourceAvailable {
            get {
                try {
                    var result=JsonValue(Service.ExecuteAsync("personality.get",JsonValue(new{id=CurrentPreferences.SelectedPresetId}),CancellationToken.None).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Source unavailable."));
                    var definition=CurrentPreferences.CustomPresets.SingleOrDefault(x=>x.Id==CurrentPreferences.SelectedPresetId);
                    return definition is null ? result.GetProperty("readOnly").GetBoolean() :
                        !result.GetProperty("readOnly").GetBoolean() && result.GetProperty("name").GetString()==definition.Name && result.GetProperty("text").GetString()==definition.Text;
                }
                catch(Exception error)when(error is ArgumentException or InvalidOperationException or InvalidDataException){return false;}
            }
        }
        private JsonElement TransferFault(string operation, JsonElement input, JsonElement data)
        {
            if(operation is "personality.preferences.transfer.save" or "personality.preferences.transfer.prepare" or "instructions.save" or "personality.preferences.pending.prepare" or "personality.preferences.pending.apply")
                SourceBoundaries.Add((CurrentPreferences.Enabled,Content.Contains(PersonalityInstructionLayer.StartMarker,StringComparison.Ordinal),CurrentSourceAvailable));
            if (operation == "personality.preferences.transfer.save" && LostLocalSave > 0 && --LostLocalSave == 0)
                throw new IOException("Committed local transfer save acknowledgement lost.");
            if (TransferFaultName is not { } fault) return data;
            var node = JsonNode.Parse(data.GetRawText())!;
            var handled = true;
            if (operation == "personality.preferences.transfer.get")
            {
                switch (fault) {
                    case "map-missing": node["pendingPurposes"]!.AsArray().RemoveAt(0); break;
                    case "map-duplicate": node["pendingPurposes"]!.AsArray()[1] = node["pendingPurposes"]![0]!.DeepClone(); break;
                    case "map-unknown": node["pendingPurposes"]![0]!["purpose"] = "foreign-purpose"; break;
                    case "foreign": node["state"]!["coordinate"]!["pluginId"] = "foreign"; break;
                    case "wrong-envelope": node["schemaVersion"] = 1; break;
                    case "wrong-portable": node["state"]!["pending"]![0]!["portable"] = true; break;
                    default: handled = false; break;
                }
            }
            else if (operation == "personality.preferences.pending.prepare")
            {
                switch (fault) {
                    case "prepared-id": node["pendingId"] = "other"; break;
                    case "prepared-purpose": node["purpose"] = "portable"; break;
                    case "prepared-revision": node["settingsRevision"] = Central.Current.Revision + 1; break;
                    case "prepared-values-hash": node["valuesSha256"] = new string('a',64); break;
                    case "prepared-pending-hash": node["pendingSha256"] = new string('a',64); break;
                    case "prepared-source": node["preset"]!["text"] = "Foreign source text."; break;
                    case "prepared-null-source": node["preset"] = null; break;
                    default: handled = false; break;
                }
            }
            else if (operation == "personality.preferences.pending.apply")
            {
                switch (fault) {
                    case "save-revision": node["state"]!["state"]!["revision"] = Central.Current.Revision + 1; break;
                    case "save-values": node["state"]!["state"]!["active"]!["values"]!["enabled"] = false; break;
                    case "save-pending": node["state"]!["state"]!["pending"]!.AsArray().Clear(); break;
                    case "save-map": node["state"]!["pendingPurposes"]!.AsArray().Clear(); break;
                    default: handled = false; break;
                }
            }
            else handled = false;
            if (handled) TransferFaultName = null;
            return JsonValue(node);
        }
    }
    private sealed partial class Central : IScopedPluginSettingsTransfer
    {
        internal readonly List<PluginSettingsPendingPurpose> Purposes = [];
        internal readonly List<JsonElement> LocalSaves = [];
        internal void SetActive(JsonElement values)
        {
            Current = Current with { Revision = Current.Revision + 1, Active = new("pegasus.personality","global",null,ServicePolicy.PackageVersion,2,values) };
        }
        internal void Stage(string id, string purpose, JsonElement values, int version = 2)
        {
            var document = new PluginSettingsDocument("pegasus.personality","global",null,"old-1.0.0",version,values);
            Current = Current with { Revision = Current.Revision + 1,
                Pending = Current.Pending.Append(new PluginSettingsDeferredDocument(id,document,purpose==SettingsTransferPurpose.Portable,"package-unavailable")).ToArray() };
            Purposes.Add(new(id,purpose));
        }
        public PluginSettingsTransferState GetTransfer(string scope="global", string? scopeId=null) => new(Current, Purposes.ToArray());
        public PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request)
        {
            var save = request.Save;
            if (save.OwnerValidated || save.PortableValues is not null || save.OmittedPortableFields != 0 ||
                request.CompleteAdmitted || request.CompleteProjection is not null) throw new InvalidDataException("Unexpected owner validation/projection claim.");
            if (save.ExpectedRevision != Current.Revision) throw new InvalidOperationException("Scoped transfer CAS conflict.");
            var purpose = save.PendingId is null ? SettingsTransferPurpose.Local : Purposes.Single(x => x.PendingId == save.PendingId).Purpose;
            if (save.PendingId is { } id && !Current.Pending.Any(x=>x.Id==id)) throw new InvalidDataException("Unknown selected pending ID.");
            var admitted = purpose == SettingsTransferPurpose.Complete ? Policy.AdmitComplete(save.Document,Current.Active?.Values)
                : Policy.Admit(save.Document,Current.Active?.Values,purpose==SettingsTransferPurpose.Portable);
            if (!admitted.Accepted) throw new InvalidDataException("Real registered service policy refused.");
            var active = save.Document with { SettingsVersion = admitted.SettingsVersion, Values = admitted.Values };
            var changed = save.PendingId is not null || Current.Active is null || !JsonElement.DeepEquals(Current.Active.Values,active.Values);
            var projection = Policy.ExportPortable(active);
            Current = Current with { Revision = Current.Revision + (changed?1:0), Active=active,
                Pending = Current.Pending.Where(x=>x.Id!=save.PendingId).ToArray(), PortableValues=projection.Values, OmittedPortableFields=projection.OmittedFields };
            if (save.PendingId is null) LocalSaves.Add(active.Values.Clone());
            else Purposes.RemoveAll(x=>x.PendingId==save.PendingId);
            return new(GetTransfer(),changed);
        }
    }


    private static async Task PendingCleanupNativeChecks()
    {
        var before=checks; using var host=new Host(Path.Combine(root,"native-pending-cleanup"));
        var store=new PersonalitySettingsStore(host.DataDirectory); var old=new PersonalityPreset("previous","Previous","Use an old marked style.");
        store.Save(new(true,old.Id,[old]),store.Load().Revision); var marked=PersonalityInstructionLayer.Apply("Owner instructions.",old,true);
        host.Content=marked; host.Central.SetActive(Full(true,"style-current",Definition("style-current")));
        host.Central.Stage("portable",SettingsTransferPurpose.Portable,JsonValue(new{}));
        using var plugin=new PersonalityPlugin();plugin.Initialize(host);var view=plugin.ConfigurationPanels.Single().CreateView();((ScrollViewer)view).Background=Brushes.White;
        T Control<T>(string id)where T:FrameworkElement=>(T)Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
        var cleanup=Control<Button>("PersonalityPendingCleanup");var prepare=Control<Button>("PersonalityPendingPrepare");
        var reload=Control<Button>("PersonalityPendingReload");var apply=Control<Button>("PersonalityPendingApply");var status=Control<TextBlock>("PersonalityPendingStatus");
        var window=new Window{Title="Personality legacy cleanup — task-owned fake-host verification",Content=view,Width=820,Height=900};window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(cleanup.IsEnabled&&prepare.IsEnabled&&!apply.IsEnabled,"shown recognized legacy section offers separate explicit cleanup");
        var original=host.Central.Current.Active!.Values.Clone();var count=host.Mutations.Count;
        prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(host.Mutations.Count==count&&!host.CurrentSourceAvailable&&status.Text.Contains("Remove the previous style section",StringComparison.Ordinal),
            "shown Review refuses unavailable enabled source beside legacy without mutation");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!cleanup.IsEnabled&&prepare.IsEnabled&&!apply.IsEnabled&&host.Content=="Owner instructions."&&host.CurrentSourceAvailable,
            "shown explicit cleanup removes section and requires fresh import review");
        Check(host.Central.Current.Pending.Count==1&&JsonElement.DeepEquals(host.Central.Current.Active!.Values,original),
            "shown cleanup preserves exact enabled destination and pending import");
        prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(apply.IsEnabled&&Control<TextBox>("PersonalityPendingReview").Text.Contains("Style enabled: Yes",StringComparison.Ordinal),
            "shown portable empty review retains restored original enabled values");
        host.Content=marked;host.Central.SetActive(Full(true,"style-second",Definition("style-second")));
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);host.LostLocalSave=1;
        cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!cleanup.IsEnabled&&!prepare.IsEnabled&&!apply.IsEnabled&&reload.IsEnabled&&status.Text.Contains("do not replay",StringComparison.Ordinal),
            "shown lost cleanup disable acknowledgement requires explicit reconciliation");
        count=host.Mutations.Count;cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Mutations.Count==count,"shown consumed cleanup cannot replay");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(cleanup.IsEnabled&&!host.CurrentPreferences.Enabled&&host.Central.Current.Pending.Count==1,
            "shown cleanup Reload exposes committed disabled values and retained import");
        window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!cleanup.IsEnabled&&!reload.IsEnabled,"shown cleanup controls stop on unload");
        Console.WriteLine("TRANSFER CLEANUP SHOWN SOFTWARE-WPF CHECKS: "+(checks-before));
    }

    private static async Task PendingNativeChecks()
    {
        var before = checks;
        using var host = PendingHost("native-pending"); using var plugin = new PersonalityPlugin(); plugin.Initialize(host);
        var view = plugin.ConfigurationPanels.Single().CreateView(); ((ScrollViewer)view).Background = Brushes.White;
        T Control<T>(string id) where T:FrameworkElement => (T)Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
        var selector = Control<ComboBox>("PersonalityPending"); var prepare = Control<Button>("PersonalityPendingPrepare");
        var apply = Control<Button>("PersonalityPendingApply"); var reload = Control<Button>("PersonalityPendingReload");
        var review = Control<TextBox>("PersonalityPendingReview"); var status = Control<TextBlock>("PersonalityPendingStatus");
        var window = new Window { Title="Personality imports — task-owned fake-host verification", Content=view, Width=820, Height=900 };
        window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.IsVisible && selector.Items.Count==2 && prepare.IsEnabled && !apply.IsEnabled, "shown native pending pane loads while normal getter refuses complete input");
        Check(selector.Text.Contains("Complete preferences",StringComparison.Ordinal) && selector.Text.Contains("chosen",StringComparison.Ordinal), "shown pending choice displays purpose and distinguishes retained entry");
        Check(!Control<Button>("PersonalitySave").IsEnabled && host.Mutations.Count==0, "complete pending keeps normal save disabled without blocking retained import review");
        prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(apply.IsEnabled && review.IsReadOnly && review.Text.Contains("Style enabled: Yes",StringComparison.Ordinal), "shown preparation displays full reviewed enabled result and enables explicit Apply");
        Check(host.Central.Current.Active is null && host.Central.Current.Pending.Count==2, "shown Review does not activate or consume pending state");
        ((ScrollViewer)view).ScrollToEnd(); window.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.ChangeExtension(preview,"pending.png")))png.Save(file);
        host.FailAfter = "personality.preferences.pending.apply";
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!apply.IsEnabled && !prepare.IsEnabled && reload.IsEnabled && status.Text.Contains("do not replay",StringComparison.Ordinal), "shown lost Apply acknowledgement requires explicit Reload");
        var count = host.Mutations.Count; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Mutations.Count==count, "shown uncertain Apply cannot replay");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(selector.Items.Count==1 && ((PersonalityPendingInput)selector.SelectedItem).Id=="other" && !apply.IsEnabled, "shown Reload reconciles actual selected removal and requires fresh review");
        prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(apply.IsEnabled && review.Text.Contains("Style enabled: No",StringComparison.Ordinal), "shown second selected input has its own disabled review");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(selector.Items.Count==0 && status.Text.Contains("Only the selected import",StringComparison.Ordinal) && Control<Button>("PersonalitySave").IsEnabled, "shown successful Apply updates both import list and current editor");
        window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!reload.IsEnabled && !apply.IsEnabled && !selector.IsEnabled, "pending unload disables controls");
        host.Central.Stage("late",SettingsTransferPurpose.Complete,Full(false)); host.DelayAfter="personality.preferences.transfer.get";
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var pane = Nodes(view).OfType<StackPanel>().Single(x=>x.Children.OfType<ComboBox>().Any(y=>AutomationProperties.GetAutomationId(y)=="PersonalityPending"));
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await host.Dispatched.Task;
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); host.Release.TrySetResult(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!reload.IsEnabled && !apply.IsEnabled, "late pending load cannot revive unloaded pane");
        plugin.Dispose();
        Console.WriteLine("TRANSFER SHOWN SOFTWARE-WPF CHECKS: " + (checks-before));
    }
}
