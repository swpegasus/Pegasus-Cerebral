using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.PersonalityPlugin;
using ServicePlugin = Cerebral.PersonalityServicePlugin.PersonalityServicePlugin;
using ServicePolicy = Cerebral.PersonalityServicePlugin.PersonalitySettingsPolicy;
namespace Cerebral.PersonalityPlugin.Tests;
internal static partial class Program
{
    private static int checks; private static string fixtureParent="",root="",preview="";
    [STAThread] private static int Main(string[] args)
    {
#if WORKER_PIPE
        if(args.Length==1 && args[0]=="--environment-observation-checks")return WorkerPipeRun.EnvironmentObservationChecksMain();
        if(args.Length==1 && args[0]=="--isolated-profile-reader")return WorkerPipeRun.IsolatedReaderMain();
        if(args.Length==2 && args[0]=="--worker-pipe")return WorkerPipeMain(args[1]);
#endif
        if(args.Length!=2)throw new ArgumentException("Exact fixture parent and preview PNG required.");
        fixtureParent=Path.GetFullPath(args[0]);preview=Path.GetFullPath(args[1]);
        Directory.CreateDirectory(fixtureParent);
        root=Path.GetFullPath(Path.Combine(fixtureParent,"owned-"+Guid.NewGuid().ToString("N")));
        if(!root.StartsWith(fixtureParent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture target.");
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources[DesktopThemeResourceKeys.TextBrush]=Brushes.Black;
        app.Resources[DesktopThemeResourceKeys.MutedTextBrush]=Brushes.DimGray;
        app.Resources[DesktopThemeResourceKeys.ErrorTextBrush]=Brushes.DarkRed;
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            try{Directory.CreateDirectory(root);await Checks();Console.WriteLine("PASS: Personality editor "+checks+" actual in-process service/deterministic scoped-settings/synthetic instructions CAS and shown software-WPF assertions; no Worker/provider/model/Source.");app.Shutdown(0);}
            catch(Exception failure){Console.Error.WriteLine(failure);app.Shutdown(1);}
            finally{if(!root.StartsWith(fixtureParent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("owned-",StringComparison.Ordinal))throw new Exception("Unsafe cleanup.");Directory.Delete(root,true);}
        }));
        return app.Run();
    }
    private static async Task Checks()
    {
        using(var host=new Host(Path.Combine(root,"main")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);
            var legacy=new PersonalityPreset("previous","Previous choice","Use clear practical language and include material evidence.");
            store.Save(new(true,legacy.Id,[legacy]),store.Load().Revision);var localBytes=File.ReadAllBytes(store.Path);
            var original="Unrelated owner lead text.\r\nKeep exactly.";var legacyBlock=PersonalityInstructionLayer.Apply(original,legacy,true)+"\r\nUnrelated suffix.";
            host.Content=legacyBlock;var editor=new PersonalityEditor(host,store,()=>host.Binding);
            host.Binding=new("configuration",null);await Reject(()=>editor.LoadAsync(),"anonymous fallback owner refused");Check(host.Mutations.Count==0,"anonymous owner performs no mutation");
            host.Binding=new("configuration-owner-a",null);var initial=await editor.LoadAsync();
            Check(host.Mutations.Count==0,"opening current schema2 is read-only");
            var choice=initial.Presets.Single(p=>p.Id==initial.SelectedPresetId);
            Check(choice.Legacy&&choice.Text==legacy.Instructions,"selected local legacy style is offered as explicit import");
            var saved=await editor.SaveAsync(initial,true,choice.Id,choice.Name,choice.Text);
            Check(host.Content==original+"\r\nUnrelated suffix.","recognized legacy removal preserves exact surrounding lead text");
            Check(saved.Settings.Enabled&&saved.Settings.SelectedPresetId==choice.Id,"acknowledged central selection is enabled only after preparation");
            Check(saved.Settings.CustomPresets.Single().Text==legacy.Instructions,"complete central custom definition is saved");
            Check(host.Mutations.SequenceEqual(["instructions.save","personality.preferences.save","personality.source.prepare","personality.preferences.save"]),"legacy CAS/readback then disabled full save preparation and final enable");
            Check(File.ReadAllBytes(store.Path).SequenceEqual(localBytes),"original local file remains byte-for-byte");
            Check(host.PreparedWhileEnabled==false,"source preparation occurs while current desired preference is disabled");
            Check(host.LastPreparedId==choice.Id,"exact selected custom source is materialized");
            var count=host.Mutations.Count;await Reject(()=>editor.SaveAsync(initial,true,choice.Id,choice.Name,choice.Text),"consumed save cannot replay");
            Check(host.Mutations.Count==count,"consumed baseline is refused before dispatch");
            var builtin=saved.Presets.Single(p=>p.Id=="cerebral");
            await Reject(()=>editor.SaveAsync(saved,true,builtin.Id,"Edited builtin",builtin.Text),"built-in name cannot be changed");
            await Reject(()=>editor.SaveAsync(saved,true,"style-bad","Large",new string('\u754c',400)),"UTF8 draft bound");
            Check(host.Mutations.Count==count,"invalid local edits are nonmutating");
            var edited=await editor.LoadAsync();await editor.SaveAsync(edited,false,"style-extra","Second editable","Use a concise practical style.");
            var withTwo=await editor.LoadAsync();var first=withTwo.Presets.Single(p=>p.Id==choice.Id);
            var renamed=await editor.SaveAsync(withTwo,false,first.Id,new string('\u754c',80),first.Text);
            Check(renamed.Settings.CustomPresets.Count==2&&renamed.Settings.CustomPresets.Single(x=>x.Id=="style-extra").Name=="Second editable","editing one definition preserves every other central definition");
            Check(renamed.Presets.Single(x=>x.Id==first.Id).Name==new string('\u754c',80),"name allowance matches80UTF16/320UTF8 service codec");
            var stale=await editor.LoadAsync();await host.Select(false,"cerebral");count=host.Mutations.Count;
            await Reject(()=>editor.SaveAsync(stale,true,"succinct","Succinct",stale.Presets.Single(p=>p.Id=="succinct").Text),"stale central CAS");
            Check(host.Mutations.Count==count+1&&!host.CurrentPreferences.Enabled,"stale save preserves newer central preference");
            host.Content=legacyBlock;await host.Select(true,"cerebral");host.Mutations.Clear();var enabledLegacy=await editor.LoadAsync();
            builtin=enabledLegacy.Presets.Single(p=>p.Id=="cerebral");await editor.SaveAsync(enabledLegacy,true,builtin.Id,builtin.Name,builtin.Text);
            Check(host.Mutations[0]=="personality.preferences.save"&&host.Mutations[1]=="instructions.save","enabled central selection is disabled before legacy removal");
            Check(host.EnabledAtInstructionsSave==false,"instructions CAS executes with desired style disabled");
            Check(host.Operations.All(x=>x is not ("personality.select" or "personality.save")),"editor never calls retired mutators");
            Check(host.Central.Policy.ExportComplete(host.Central.Current.Active!).Values.GetProperty("customPresets").GetArrayLength()==2,"complete projection retains all current editable definitions");
            var portable=host.Central.Policy.ExportPortable(host.Central.Current.Active!);
            Check(portable.OmittedFields==1&&portable.Values.GetProperty("selectedPresetId").GetString()=="cerebral","built-in portable projection remains selection-only");
            Check(File.ReadAllBytes(store.Path).SequenceEqual(localBytes),"all editor saves preserve local legacy data");
        }
        await CurrentDefinitionChecks();
        await FailureCases();
        await NativeChecks();
        await NativeCapacityChecks();
        await TemplateChecks();
        await TemplateSourceCapacityChecks();
        await TemplateAmbiguityAndFailureChecks();
        PackageChecks();
        await TransferChecks();
        await ComposerChecks();
        await ComposerPreviewChecks();
    }
    private static async Task CurrentDefinitionChecks()
    {
        using(var host=new Host(Path.Combine(root,"current-definition")))
        {
            await host.SeedDefinition("style-history","Retained old name","Use the old reference.");await host.Prepare();
            await host.SeedDefinition("style-history","Current editable name","Use the current editable definition.");
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);var baseline=await editor.LoadAsync();
            var selected=baseline.Presets.Single(x=>x.Id=="style-history");
            Check(selected.Name=="Current editable name"&&selected.Text=="Use the current editable definition."&&selected.Revision==0,
                "current unprepared definition is loaded from central preferences rather than retained history");
            var old=(await host.ExecuteServiceCommandAsync("personality.get",new{id="style-history"})).Data;
            Check(old.GetProperty("name").GetString()=="Retained old name","retained immutable source is distinct from current editable definition");
            var result=await editor.SaveAsync(baseline,true,selected.Id,selected.Name,selected.Text);
            var exact=(await host.ExecuteServiceCommandAsync("personality.get",new{id="style-history"})).Data;
            Check(result.Settings.Enabled&&exact.GetProperty("revision").GetInt64()==2&&exact.GetProperty("text").GetString()==selected.Text,
                "explicit save prepares the exact changed definition as a new retained version");
            var custom=host.Central.Policy.ExportPortable(host.Central.Current.Active!);
            Check(custom.OmittedFields==3&&!custom.Values.EnumerateObject().Any(),"custom portable selection remains empty with three omissions");
        }
        using(var host=new Host(Path.Combine(root,"local-preimage")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var old=new PersonalityPreset("old","Old","Use an old style.");
            store.Save(new(false,"old",[old]),store.Load().Revision);var editor=new PersonalityEditor(host,store,()=>host.Binding);
            var baseline=await editor.LoadAsync();var next=new PersonalityPreset("next","Next","Use a changed local style.");
            store.Save(new(false,"next",[next]),store.Load().Revision);var bytes=File.ReadAllBytes(store.Path);var count=host.Mutations.Count;
            var builtin=baseline.Presets.Single(x=>x.Id=="cerebral");await Reject(()=>editor.SaveAsync(baseline,true,builtin.Id,builtin.Name,builtin.Text),"changed local legacy preimage refused");
            Check(host.Mutations.Count==count&&File.ReadAllBytes(store.Path).SequenceEqual(bytes),"changed local legacy data is preserved without owner mutation");
        }
        Check(typeof(PersonalityPlugin).Assembly.GetName().Version?.ToString()=="1.2.4.0","native accessible quick-selector edition is1.2.4");
    }
    private static async Task FailureCases()
    {
        foreach(var phase in new[]{"instructions.save","personality.preferences.save","personality.source.prepare"})
        {
            using var host=new Host(Path.Combine(root,"lost-"+phase));var store=new PersonalitySettingsStore(host.DataDirectory);
            var legacy=new PersonalityPreset("previous","Previous","Use a compact direct style.");
            if(phase=="instructions.save"){store.Save(new(true,legacy.Id,[legacy]),store.Load().Revision);host.Content=PersonalityInstructionLayer.Apply("Owner text.",legacy,true);}
            var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();host.FailAfter=phase;
            var id=phase=="instructions.save"?baseline.SelectedPresetId:"style-lost";
            var choice=baseline.Presets.SingleOrDefault(x=>x.Id==id);
            await Reject(()=>editor.SaveAsync(baseline,true,id,choice?.Name??"Lost acknowledgement",choice?.Text??"Use clear evidence.",default),"lost "+phase+" acknowledgement");
            var count=host.Mutations.Count;await Reject(()=>editor.SaveAsync(baseline,true,id,choice?.Name??"Lost acknowledgement",choice?.Text??"Use clear evidence.",default),"no replay after uncertain "+phase);
            Check(host.Mutations.Count==count&&!host.CurrentPreferences.Enabled,"uncertain "+phase+" never enables or replays");
            _=await editor.LoadAsync();Check(host.Mutations.Count==count,"explicit reconciliation load is read-only after "+phase);
        }
        using(var host=new Host(Path.Combine(root,"stale-lead")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var legacy=new PersonalityPreset("previous","Previous","Use clear evidence.");
            store.Save(new(true,legacy.Id,[legacy]),store.Load().Revision);var marked=PersonalityInstructionLayer.Apply("Original.",legacy,true);host.Content=marked;
            var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();host.Content="External lead edit.";
            await Reject(()=>editor.SaveAsync(baseline,true,"succinct","Succinct",baseline.Presets.Single(x=>x.Id=="succinct").Text),"stale instructions SHA CAS");
            Check(host.Content=="External lead edit."&&host.Mutations.SequenceEqual(["instructions.save"]),"stale instructions CAS preserves external edit and prevents preferences save");
            host.Content=marked.Replace(legacy.Instructions,"Unknown hand-edited style",StringComparison.Ordinal);var count=host.Mutations.Count;
            await Reject(()=>editor.LoadAsync(),"hand-edited legacy body refused");Check(host.Mutations.Count==count&&host.Content.Contains("Unknown hand-edited",StringComparison.Ordinal),"hand-edited region is preserved");
            host.Content=marked+"\n"+PersonalityInstructionLayer.StartMarker;await Reject(()=>editor.LoadAsync(),"duplicate legacy marker refused");
            Check(host.Mutations.Count==count,"malformed legacy region is nonmutating");
        }
        using(var host=new Host(Path.Combine(root,"owner-race")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();
            host.DelayAfter="personality.source.prepare";var save=editor.SaveAsync(baseline,true,"style-owner","Owner race","Use direct evidence.");
            await host.Dispatched.Task;host.Binding=new("configuration-owner-b",null);host.Release.TrySetResult();
            await Reject(()=>save,"owner changes during source acknowledgement");
            Check(!host.CurrentPreferences.Enabled&&host.Mutations.SequenceEqual(["personality.preferences.save","personality.source.prepare"]),"retired owner cannot enable after preparation");
        }
        using(var host=new Host(Path.Combine(root,"source-race")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();
            await host.SeedDefinition("style-other","Other","Use another compact style.");await host.Prepare();
            await host.Select(false,"cerebral");baseline=await editor.LoadAsync();
            await host.SeedDefinition("style-other2","Other two","Use another clear style.");await host.Prepare();
            // Keep the central baseline revision current while the captured source CAS is stale.
            var current=await editor.LoadAsync();
            var staleSource=new PersonalityEditorSnapshot(current.Legacy,current.Lead,current.Settings,baseline.Sources,current.Presets,current.Owner,null);
            await Reject(()=>editor.SaveAsync(staleSource,true,"style-new","New","Use direct evidence."),"stale source-store revision SHA");
            Check(!host.CurrentPreferences.Enabled&&host.LastPreparedId=="style-other2","source conflict cannot enable a substituted or unprepared definition");
        }
        using(var host=new Host(Path.Combine(root,"reintroduced")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);var legacy=new PersonalityPreset("old","Old","Use an old style.");store.Save(new(false,"old",[legacy]),store.Load().Revision);
            var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();host.ReintroduceAfterPrepare=PersonalityInstructionLayer.Apply("Owner text.",legacy,true);
            await Reject(()=>editor.SaveAsync(baseline,true,"style-new","New","Use direct evidence."),"legacy region reintroduced during preparation");
            Check(!host.CurrentPreferences.Enabled&&PersonalityInstructionLayer.HasLayer(host.Content),"reintroduced region is preserved and new style remains disabled");
        }
        using(var host=new Host(Path.Combine(root,"missing")))
        {
            var store=new PersonalitySettingsStore(host.DataDirectory);await host.Select(false,"style-missing");
            var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();
            Check(baseline.SelectedPresetId=="style-missing"&&baseline.Presets.Single(x=>x.Id=="style-missing").Name=="Unavailable saved style","missing desired definition is displayed without substitution");
            var count=host.Mutations.Count;await Reject(()=>editor.SaveAsync(baseline,true,"style-missing","Unavailable saved style",""),"unavailable placeholder cannot be enabled");
            Check(host.Mutations.Count==count,"unavailable placeholder produces no save or preparation");
            var builtin=baseline.Presets.Single(x=>x.Id=="cerebral");var result=await editor.SaveAsync(baseline,false,builtin.Id,builtin.Name,builtin.Text);
            Check(result.SelectedPresetId=="cerebral","explicit owner choice recovers from missing definition");
        }
        using(var host=new Host(Path.Combine(root,"capacity")))
        {
            for(var i=0;i<8;i++)await host.SeedDefinition("style-"+i,"Style "+i,"Use a compact clear style.");
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);var baseline=await editor.LoadAsync();var count=host.Mutations.Count;
            await Reject(()=>editor.SaveAsync(baseline,false,"style-ninth","Ninth","Use direct evidence."),"ninth current definition refused");
            Check(host.Mutations.Count==count&&host.CurrentPreferences.CustomPresets.Count==8,"capacity refusal preserves all existing custom definitions");
        }
        foreach(var fault in new[]{"wrong-coordinate","wrong-ack-values","wrong-ack-revision","wrong-source-content","wrong-source-id","wrong-source-readback","wrong-lead-readback","aliased-preferences"})
        {
            using var host=new Host(Path.Combine(root,fault));var store=new PersonalitySettingsStore(host.DataDirectory);
            if(fault=="wrong-lead-readback"){var old=new PersonalityPreset("old","Old","Use old evidence.");store.Save(new(true,"old",[old]),store.Load().Revision);host.Content=PersonalityInstructionLayer.Apply("Owner text.",old,true);}
            var editor=new PersonalityEditor(host,store,()=>host.Binding);var baseline=await editor.LoadAsync();host.Fault=fault;
            await Reject(()=>editor.SaveAsync(baseline,true,"style-check","Check","Use direct evidence."),"malformed "+fault+" reply");
            Check(!host.CurrentPreferences.Enabled,"malformed "+fault+" reply cannot enable");
        }
        using(var host=new Host(Path.Combine(root,"absent-default-ack")))
        {
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);var baseline=await editor.LoadAsync();
            var builtin=baseline.Presets.Single(x=>x.Id=="cerebral");host.Fault="absent-default-ack";
            await Reject(()=>editor.SaveAsync(baseline,false,builtin.Id,builtin.Name,builtin.Text),"absent active default-shaped save acknowledgement refused");
            Check(!host.Mutations.Contains("personality.source.prepare"),"missing active save acknowledgement prevents source preparation");
        }
        using(var host=new Host(Path.Combine(root,"complete-pending")))
        {
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);host.Central.CompletePendingUnavailable=true;
            await Reject(()=>editor.LoadAsync(),"existing service legacy scoped API refuses complete pending");
            Check(host.Mutations.Count==0,"native editor does not bypass or drain complete pending");
        }
    }
    private static void PackageChecks()
    {
        var input=Path.Combine(root,"package-input");Directory.CreateDirectory(input);
        File.Copy(typeof(PersonalityPlugin).Assembly.Location,Path.Combine(input,"Cerebral.PersonalityPlugin.dll"));
        var manifest=Path.Combine(input,"desktop-plugin.json");
        // The fixed tests output copies the candidate DLL; its exact current manifest is supplied explicitly.
        var manifestSource=Path.Combine(AppContext.BaseDirectory,"desktop-plugin.json");
        File.Copy(manifestSource,manifest);
        var identity=NativeDesktopPluginPackage.Inspect(manifest);
        NativeDesktopPluginPackage.ValidateEntryPoint(identity);
        Check(identity.Id==PersonalityPlugin.PluginId&&identity.Version=="1.2.4"&&identity.EntryType==typeof(PersonalityPlugin).FullName,
            "actual native manifest entry and compiled1.2.4 identity are admitted");
        var paths=new DesktopProductPaths(Path.Combine(root,"program"),Path.Combine(root,"data"),Path.Combine(root,"user"),
            Path.Combine(root,"projects"),Path.Combine(root,"models"),Path.Combine(root,"tools"),Path.Combine(root,"shared"));
        var info=new DesktopPluginInfo(identity.Id,identity.Name,identity.Version,identity.Description,false,input,manifest,null);
        var archive=Path.Combine(root,"personality-fixture.CerebralPlugin");
        CerebralPluginPackage.Export(info,archive,"none",paths);
        using var prepared=CerebralPluginPackage.Prepare(archive);
        var preparedRoot=Path.GetDirectoryName(prepared.DesktopManifestPath)!;
        Check(prepared.Inspection.Package.SchemaVersion==2&&prepared.Inspection.Package.SettingsMode=="none",
            "existing actual private settings-free package round trip is retained");
        Check(!Directory.EnumerateFiles(preparedRoot,PersonalitySettingsStore.FileName,SearchOption.AllDirectories).Any()&&
            !Directory.EnumerateFiles(preparedRoot,"personality-context.json",SearchOption.AllDirectories).Any(),
            "private package round trip excludes legacy preference and source data");
        Check(!File.Exists(Path.Combine(preparedRoot,"Cerebral.Desktop.Plugins.dll")),"private package does not ship frozen shared SDK");
    }
    private static async Task NativeCapacityChecks()
    {
        using var host=new Host(Path.Combine(root,"native-capacity"));
        for(var index=0;index<7;index++)await host.SeedDefinition("style-cap"+index,"Custom "+index,"Use clear practical wording.");
        using var plugin=new PersonalityPlugin();plugin.Initialize(host);
        var view=plugin.ConfigurationPanels.Single().CreateView();
        T Control<T>(string id)where T:FrameworkElement=>(T)Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
        var add=Control<Button>("PersonalityNew");var save=Control<Button>("PersonalitySave");var reload=Control<Button>("PersonalityCancel");
        var selector=Control<ComboBox>("PersonalityPreset");var name=Control<TextBox>("PersonalityName");var text=Control<TextBox>("PersonalityPreview");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(add.IsEnabled,"native New remains available below eight saved custom presets");
        await host.SeedDefinition("style-cap7","Custom 7","Use clear practical wording.");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!add.IsEnabled&&add.ToolTip?.ToString()?.Contains("Eight custom presets",StringComparison.Ordinal)==true,
            "native New explains the saved custom preset capacity without a dead-end draft");
        var selected=selector.SelectedValue;var previousName=name.Text;var previousText=text.Text;var mutations=host.Mutations.Count;
        Control<ComboBox>("PersonalityTemplate").SelectedValue="hue";
        Check(Equals(selector.SelectedValue,selected)&&name.Text==previousName&&text.Text==previousText&&host.Mutations.Count==mutations,
            "template at eight saved definitions preserves selection and text without a new draft");
        Check(Control<TextBlock>("PersonalityCapacity").Text.Contains("8 / 8",StringComparison.Ordinal),
            "native capacity reports all eight current custom slots");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Equals(selector.SelectedValue,selected)&&name.Text==previousName&&text.Text==previousText,
            "forced New at capacity preserves the selected preset and editor text");
        Check(host.Mutations.Count==mutations&&host.CurrentPreferences.CustomPresets.Count==8,
            "forced New at capacity does not mutate or delete saved definitions");
        selector.SelectedValue="style-cap0";
        Check(save.IsEnabled&&!name.IsReadOnly&&!text.IsReadOnly,"existing custom definitions remain editable at capacity");
        name.Text="Edited at capacity";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(host.CurrentPreferences.CustomPresets.Count==8&&host.CurrentPreferences.CustomPresets.Single(x=>x.Id=="style-cap0").Name=="Edited at capacity"&&!add.IsEnabled,
            "saving an existing definition at capacity preserves all eight identities");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
    private static async Task NativeChecks()
    {
        using var host=new Host(Path.Combine(root,"native"));using var plugin=new PersonalityPlugin();plugin.Initialize(host);
        var view=plugin.ConfigurationPanels.Single().CreateView();
        ((ScrollViewer)view).Background=Brushes.White;
        T Control<T>(string id)where T:FrameworkElement=>(T)Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
        var selector=Control<ComboBox>("PersonalityPreset");var save=Control<Button>("PersonalitySave");var reload=Control<Button>("PersonalityCancel");
        var name=Control<TextBox>("PersonalityName");var text=Control<TextBox>("PersonalityPreview");
        var window=new Window{Title="Personality schema2 — task-owned fake-host verification",Content=view,Width=780,Height=920,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        var before=host.Mutations.Count;window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.IsVisible&&selector.IsEnabled&&save.IsEnabled&&host.Mutations.Count==before,"actual shown software-WPF panel loads schema2 without mutation");
        selector.SelectedValue="cerebral";Check(text.IsReadOnly&&name.IsReadOnly,"native built-in fields are read-only");
        Control<Button>("PersonalityNew").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!text.IsReadOnly&&!name.IsReadOnly&&((string)selector.SelectedValue).StartsWith("style-",StringComparison.Ordinal),"native New creates editable custom draft");
        name.Text="Native custom";text.Text=new string('x',1200);before=host.Mutations.Count;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(save.IsEnabled&&text.Text.Length==1200&&host.Mutations.Count==before,"native local size refusal preserves unsaved draft and makes no mutation");
        text.Text="Answer clearly and include verified evidence.";Control<CheckBox>("PersonalityEnabled").IsChecked=true;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(save.IsEnabled&&selector.SelectedItem is PersonalityOwnerPreset{Name:"Native custom",Revision:0}&&host.CurrentPreferences.Enabled,"native Save shows acknowledged current definition after exact source preparation");
        Check(host.LastPreparedId==(string)selector.SelectedValue,"native selection matches actual prepared immutable source");
        var chosen=(PersonalityOwnerPreset)selector.SelectedItem;name.Text="Unsaved";
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(name.Text==chosen.Name,"native Reload discards unsaved edit using current central preferences");
        host.FailAfter="personality.source.prepare";text.Text="Use the newly edited concise style.";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!save.IsEnabled&&reload.IsEnabled&&Control<TextBlock>("PersonalityStatus").Text.Contains("Reload",StringComparison.Ordinal),"uncertain native preparation disables save until explicit reconciliation");
        before=host.Mutations.Count;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Mutations.Count==before,"uncertain native UI cannot replay its consumed baseline");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Check(save.IsEnabled&&!Control<CheckBox>("PersonalityEnabled").IsChecked.GetValueOrDefault()&&!host.CurrentPreferences.Enabled,"explicit native Reload presents acknowledged disabled preference after partial save");
        Control<ComboBox>("PersonalityTemplate").SelectedValue="gary_2";
        Check(name.Text=="Gary 2.0"&&text.Text==PersonalityTemplates.All.Single(x=>x.PackageKey=="gary_2").Text&&
            Control<TextBlock>("PersonalityStatus").Text.Contains("unsaved custom draft",StringComparison.Ordinal),
            "normal shown preview stages the supplied Gary 2.0 text with explicit unsaved status");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(name.Text=="Gary 2.0"&&host.CurrentPreferences.SelectedPresetId==(string)selector.SelectedValue&&
            Control<TextBlock>("PersonalityStatus").Text.Contains("Saved shared style",StringComparison.Ordinal),
            "normal preview shows the acknowledged Gary 2.0 shared definition with honest per-chat guidance");
        Control<TextBlock>("PersonalityStatus").BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();var width=(int)view.ActualWidth;var height=(int)view.ActualHeight;
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(preview))png.Save(file);
        Check(width>600&&height>600,"shown native panel has completed software rendering");
        window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Check(!save.IsEnabled&&!selector.IsEnabled,"unload disables native controls");
        host.DelayAfter="personality.preferences.get";view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await host.Dispatched.Task;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));host.Release.TrySetResult();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!save.IsEnabled&&!selector.IsEnabled,"late load cannot revive unloaded view");
        plugin.Dispose();view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Check(!save.IsEnabled,"disposed native plugin cannot restart");
    }
    private static T TemplateControl<T>(FrameworkElement view,string id)where T:FrameworkElement=>
        (T)Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
    private static async Task TemplateChecks()
    {
        var expectedBytes=new[]{628,670,694,688,642,639,628};var expectedWords=new[]{81,82,87,90,80,78,85};
        Check(PersonalityTemplates.All.Select(x=>x.PackageKey).SequenceEqual(new[]{"hue","kvn","gary","gary_2","pragmatic","analytical","teacher-coach","avocato","quinn","lord-commander","tribore","ava"}),
            "the corrected twelve-profile roster contains four original characters, three practical styles and five selected additions");
        Check(PersonalityTemplates.RevisionOne.Count==7&&PersonalityTemplates.RevisionOne.Select(x=>x.Revision).All(x=>x==1),
            "the original seven definition revisions remain separately addressable");
        for(var index=0;index<PersonalityTemplates.RevisionOne.Count;index++)
        {
            var original=PersonalityTemplates.RevisionOne[index];
            Check(Encoding.UTF8.GetByteCount(original.Text)==expectedBytes[index]&&
                original.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length==expectedWords[index],
                original.Name+" revision-one payload retains its original measured byte/word bounds");
        }
        Check(PersonalityTemplates.All.All(x=>!x.PackageKey.Contains("pegasus",StringComparison.OrdinalIgnoreCase)&&x.PackageKey!="blunt")&&
            PersonalityTemplates.BluntModeRevision==1&&PersonalityTemplates.BluntModeText=="Be blunt and candid. Challenge weak assumptions and disagree when warranted; avoid automatic agreement and praise. Keep criticism specific and useful.",
            "private Pegasus is absent from public definitions and candid mode is a separately versioned modifier");
        for(var index=0;index<PersonalityTemplates.All.Count;index++)
        {
            var template=PersonalityTemplates.All[index];
            PersonalityEditor.ValidateDraft("style-template",template.Name,template.Text);
            Check(template.Revision>=1&&!string.IsNullOrWhiteSpace(template.Description)&&template.Description.Length<=320&&
                Encoding.UTF8.GetByteCount(template.Text)<=1024&&
                template.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length<=120,
                template.Name+" has a human description and bounded versioned payload");
            var effective=template.Text+"\n"+PersonalityTemplates.BluntModeText;
            Check(Encoding.UTF8.GetByteCount(effective)<=1024&&effective.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length<=120,
                template.Name+" raw base-plus-mode text fits the reported bounds before provider serialization");
        }
        using var host=new Host(Path.Combine(root,"templates"));
        await host.SeedDefinition("gary","Owner Gary","Keep the owner's separate original wording.");
        await host.Select(true,"cerebral");
        using var plugin=new PersonalityPlugin();plugin.Initialize(host);
        var view=plugin.ConfigurationPanels.Single().CreateView();
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var templates=TemplateControl<ComboBox>(view,"PersonalityTemplate");
        var selector=TemplateControl<ComboBox>(view,"PersonalityPreset");
        var name=TemplateControl<TextBox>(view,"PersonalityName");var text=TemplateControl<TextBox>(view,"PersonalityPreview");
        var enabled=TemplateControl<CheckBox>(view,"PersonalityEnabled");var save=TemplateControl<Button>(view,"PersonalitySave");
        var reload=TemplateControl<Button>(view,"PersonalityCancel");
        Check(templates.Items.Count==12&&AutomationProperties.GetName(templates).Length>0,
            "native template dropdown discovers all twelve named public choices without saving them or exposing private content");
        Check(selector.Items.OfType<PersonalityOwnerPreset>().Count(x=>x.ReadOnly)==2,
            "template discovery leaves Cerebral and Succinct as the only service built-ins");
        // Explicit copying still has eight slots, including the pre-existing owner's Gary.
        // Discovery and packaged selection cover all twelve without filling custom storage.
        foreach(var template in PersonalityTemplates.All.Take(7))
        {
            var before=host.CurrentPreferences.CustomPresets.ToArray();var mutations=host.Mutations.Count;
            var previousSelected=host.CurrentPreferences.SelectedPresetId;
            templates.SelectedValue=template.PackageKey;
            var id=(string)selector.SelectedValue;
            Check(id.StartsWith("style-",StringComparison.Ordinal)&&Guid.TryParseExact(id["style-".Length..],"N",out _),
                template.Name+" stages a fresh valid custom identity rather than its package key");
            Check(name.Text==template.Name&&text.Text==template.Text&&enabled.IsChecked==true&&
                host.Mutations.Count==mutations&&host.CurrentPreferences.SelectedPresetId==previousSelected,
                template.Name+" selection fills only a local draft and preserves explicit enablement");
            templates.SelectedValue=template.PackageKey;
            Check((string)selector.SelectedValue==id&&host.Mutations.Count==mutations,
                template.Name+" repeated unsaved selection keeps the same single draft identity");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(host.CurrentPreferences.SelectedPresetId==id&&host.CurrentPreferences.Enabled&&
                host.CurrentPreferences.CustomPresets.Single(x=>x.Id==id).Text==template.Text,
                template.Name+" explicit Save persists exact central definition and selection");
            Check(before.All(old=>host.CurrentPreferences.CustomPresets.Any(x=>x.Id==old.Id&&x.Name==old.Name&&x.Text==old.Text)),
                template.Name+" save preserves every previous custom definition including bare gary");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check((string)selector.SelectedValue==id&&name.Text==template.Name&&text.Text==template.Text,
                template.Name+" saved selection and exact preview survive Reload");
            var sourcesBefore=(await host.ExecuteServiceCommandAsync("personality.sources.get",new{})).Data;
            var count=host.CurrentPreferences.CustomPresets.Count;mutations=host.Mutations.Count;
            templates.SelectedValue=template.PackageKey;
            Check((string)selector.SelectedValue==id&&host.CurrentPreferences.CustomPresets.Count==count&&host.Mutations.Count==mutations,
                template.Name+" exact current-owner definition is reused without a duplicate");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var sourcesAfter=(await host.ExecuteServiceCommandAsync("personality.sources.get",new{})).Data;
            Check(sourcesBefore.GetProperty("sha256").GetString()==sourcesAfter.GetProperty("sha256").GetString(),
                template.Name+" repeated explicit selection adds no retained source version");
        }
        Check(host.CurrentPreferences.CustomPresets.Single(x=>x.Id=="gary").Name=="Owner Gary",
            "package template never overwrites an existing same-name source key");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
    private static async Task TemplateSourceCapacityChecks()
    {
        foreach(var fullVersions in new[]{false,true})
        {
            using var host=new Host(Path.Combine(root,fullVersions?"template-version-capacity":"template-source-capacity"));
            for(var index=0;index<(fullVersions?32:8);index++)
            {
                await host.SeedDefinition(fullVersions?"style-history":"style-retained"+index,"Retained "+index,"Use retained wording number "+index+".");
                await host.Prepare();
            }
            await host.ExecuteServiceCommandAsync("personality.preferences.save",new{schemaVersion=2,expectedRevision=host.Central.Current.Revision,
                values=new{enabled=false,selectedPresetId="cerebral",customPresets=Array.Empty<object>()}});
            using var plugin=new PersonalityPlugin();plugin.Initialize(host);
            var view=plugin.ConfigurationPanels.Single().CreateView();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var selector=TemplateControl<ComboBox>(view,"PersonalityPreset");var name=TemplateControl<TextBox>(view,"PersonalityName");
            var text=TemplateControl<TextBox>(view,"PersonalityPreview");var add=TemplateControl<Button>(view,"PersonalityNew");
            var selected=selector.SelectedValue;var previousName=name.Text;var previousText=text.Text;var mutations=host.Mutations.Count;
            TemplateControl<ComboBox>(view,"PersonalityTemplate").SelectedValue="hue";
            Check(!add.IsEnabled&&Equals(selected,selector.SelectedValue)&&name.Text==previousName&&text.Text==previousText&&host.Mutations.Count==mutations,
                (fullVersions?"32 retained versions":"eight retained custom IDs")+" blocks a fresh template without changing state");
            Check(TemplateControl<TextBlock>(view,"PersonalityStatus").Text.Contains(fullVersions?"32 retained":"Eight prepared",StringComparison.Ordinal),
                "exhausted source capacity is diagnosed separately from available central definition slots");
            Check(host.CurrentPreferences.CustomPresets.Count==0&&TemplateControl<ComboBox>(view,"PersonalityTemplate").IsEnabled,
                "source-capacity refusal preserves definitions and keeps exact-reuse template choices available");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.PluginSurface=DesktopPluginSurface.Orchestrator;host.Binding=new("main-capacity","capacity-chat");
            var main=ComposerView(plugin);main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var mainChoice=TemplateControl<ComboBox>(main,"PersonalityComposerChoice");mutations=host.Mutations.Count;
            mainChoice.SelectedValue="packaged:hue";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var chat=await Chat(host,"capacity-chat");
            Check(chat.GetProperty("enabled").GetBoolean()&&chat.GetProperty("baseSelection").GetProperty("kind").GetString()=="packaged"&&
                host.CurrentPreferences.CustomPresets.Count==0&&!host.Mutations.Skip(mutations).Contains("personality.source.prepare"),
                "packaged Main selection consumes no legacy custom identity/version capacity");
            mainChoice.SelectedValue="none";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!(await Chat(host,"capacity-chat")).GetProperty("enabled").GetBoolean()&&
                !host.Mutations.Skip(mutations).Contains("personality.preferences.save"),
                "per-chat None works at full legacy capacity without global preference changes");
            main.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }
    private static async Task TemplateAmbiguityAndFailureChecks()
    {
        var hue=PersonalityTemplates.All.Single(x=>x.PackageKey=="hue");
        using(var host=new Host(Path.Combine(root,"template-ambiguous")))
        {
            await host.SeedDefinition("style-match-a",hue.Name,hue.Text);
            await host.SeedDefinition("style-match-b",hue.Name,hue.Text);
            await host.Select(false,"cerebral");
            using var plugin=new PersonalityPlugin();plugin.Initialize(host);
            var view=plugin.ConfigurationPanels.Single().CreateView();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var selector=TemplateControl<ComboBox>(view,"PersonalityPreset");var templates=TemplateControl<ComboBox>(view,"PersonalityTemplate");
            var text=TemplateControl<TextBox>(view,"PersonalityPreview");var previousText=text.Text;var mutations=host.Mutations.Count;
            templates.SelectedValue="hue";
            Check((string)selector.SelectedValue=="cerebral"&&text.Text==previousText&&host.Mutations.Count==mutations&&
                TemplateControl<TextBlock>(view,"PersonalityStatus").Text.Contains("More than one",StringComparison.Ordinal),
                "ambiguous exact template matches preserve the current choice and diagnose explicit selection");
            selector.SelectedValue="style-match-b";templates.SelectedValue="hue";
            Check((string)selector.SelectedValue=="style-match-b"&&host.Mutations.Count==mutations,
                "explicitly selected matching ID disambiguates without creating or overwriting a definition");
            host.Binding=new("configuration-owner-b",null);previousText=text.Text;templates.SelectedValue="kvn";
            Check((string)selector.SelectedValue=="style-match-b"&&text.Text==previousText&&host.Mutations.Count==mutations&&
                TemplateControl<TextBlock>(view,"PersonalityStatus").Text.Contains("owner changed",StringComparison.Ordinal),
                "changed owner cannot stage a template against the previous owner's snapshot");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
        using(var host=new Host(Path.Combine(root,"template-lost-ack")))
        {
            using var plugin=new PersonalityPlugin();plugin.Initialize(host);
            var view=plugin.ConfigurationPanels.Single().CreateView();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var templates=TemplateControl<ComboBox>(view,"PersonalityTemplate");var selector=TemplateControl<ComboBox>(view,"PersonalityPreset");
            var save=TemplateControl<Button>(view,"PersonalitySave");var reload=TemplateControl<Button>(view,"PersonalityCancel");
            templates.SelectedValue="kvn";var id=(string)selector.SelectedValue;
            TemplateControl<CheckBox>(view,"PersonalityEnabled").IsChecked=true;
            host.FailAfter="personality.source.prepare";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!save.IsEnabled&&!templates.IsEnabled&&reload.IsEnabled&&!host.CurrentPreferences.Enabled,
                "lost template preparation acknowledgement keeps new selection disabled and requires reconciliation");
            var mutations=host.Mutations.Count;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(host.Mutations.Count==mutations,"uncertain template save never replays its consumed baseline");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            templates.SelectedValue="kvn";
            Check((string)selector.SelectedValue==id&&host.CurrentPreferences.CustomPresets.Count==1&&host.Mutations.Count==mutations&&
                !TemplateControl<CheckBox>(view,"PersonalityEnabled").IsChecked.GetValueOrDefault(),
                "explicit reload preserves the acknowledged disabled definition and reuses it without another copy");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }
    private static FrameworkElement ComposerView(PersonalityPlugin plugin)=>((DesktopMountView)plugin.MountContributions
        .Single(x=>x.Descriptor.Slot==DesktopMountSlots.ComposerAccessories)).CreateView();
    private static async Task<JsonElement> Chat(Host host,string id)=>
        (await host.ExecuteServiceCommandAsync("personality.chat.get",new{schemaVersion=1,conversationId=id})).Data;
    private static async Task ComposerChecks()
    {
        using(var host=new Host(Path.Combine(root,"composer-main")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            // A shared default is deliberately enabled: new chat state must still begin at None.
            await host.Select(true,"cerebral");
            var globalBefore=host.Central.Current;var mutations=host.Mutations.Count;
            host.Binding=new("main-owner-a","chat-a");plugin.Initialize(host);
            var mount=plugin.MountContributions.Single();
            Check(mount.Descriptor.Applicability.Surfaces==DesktopMountSurfaces.Orchestrator&&mount.Descriptor.Slot==DesktopMountSlots.ComposerAccessories,
                "quick selector uses only the existing Main composer accessory contract");
            var view=ComposerView(plugin);
            // Controlled host-style pressure stays local to this accessory, not other fixtures.
            foreach(var controlType in new[]{typeof(ComboBox),typeof(Button),typeof(System.Windows.Controls.Primitives.ToggleButton)})
            {
                var hostStyle=new Style(controlType);
                hostStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty,34d));
                hostStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty,80d));
                hostStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(8,4,8,4)));
                view.Resources[controlType]=hostStyle;
            }
            var combo=TemplateControl<ComboBox>(view,"PersonalityComposerChoice");
            var mode=TemplateControl<System.Windows.Controls.Primitives.ToggleButton>(view,"PersonalityComposerBlunt");
            var reload=TemplateControl<Button>(view,"PersonalityComposerReload");
            var status=TemplateControl<TextBlock>(view,"PersonalityComposerStatus");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&(string)combo.SelectedValue=="none"&&combo.Items.Count==15&&!mode.IsEnabled&&host.Mutations.Count==mutations,
                "new chat shows None plus two built-ins and twelve public profiles without inheriting or changing the shared default");
            Check(AutomationProperties.GetName(combo).Length>0&&AutomationProperties.GetName(reload).Length>0&&
                AutomationProperties.GetName(mode).Contains("A**hole",StringComparison.Ordinal)&&AutomationProperties.GetHelpText(mode).Contains("constructive",StringComparison.Ordinal),
                "selector, Reload and optional mode have accessible names and a nearby constructive-pushback explanation");
            combo.SelectedValue="packaged:gary_2";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var aState=await Chat(host,"chat-a");
            Check(aState.GetProperty("enabled").GetBoolean()&&aState.GetProperty("baseSelection").GetProperty("id").GetString()=="gary_2"&&
                aState.GetProperty("baseSelection").GetProperty("kind").GetString()=="packaged"&&host.CurrentPreferences.CustomPresets.Count==0&&mode.IsEnabled,
                "explicit packaged selection persists only chat A and consumes no custom definition slots");
            Check(status.Text.Contains("Saved: Gary 2.0",StringComparison.Ordinal)&&!status.Text.Contains("injected",StringComparison.OrdinalIgnoreCase),
                "Main reports saved selected intent without claiming model injection");
            mode.IsChecked=true;mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            aState=await Chat(host,"chat-a");
            Check(aState.GetProperty("bluntMode").GetBoolean()&&aState.GetProperty("bluntPin").GetProperty("id").GetString()=="blunt"&&
                aState.GetProperty("effectiveSource").ValueKind==JsonValueKind.Object,
                "mode saves on the same chat selection with one effective source pin");
            var aSaved=aState;
            host.ChangeConversation("main-owner-b","chat-b");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&(string)combo.SelectedValue=="none"&&mode.IsChecked==false&&
                !(await Chat(host,"chat-b")).GetProperty("enabled").GetBoolean(),
                "switching to chat B reads its independent None state");
            combo.SelectedValue="packaged:analytical";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bSaved=await Chat(host,"chat-b");
            Check(bSaved.GetProperty("enabled").GetBoolean()&&!bSaved.GetProperty("bluntMode").GetBoolean()&&
                JsonElement.DeepEquals(aSaved,await Chat(host,"chat-a")),
                "chat B can select Analytical without changing chat A or its candid mode");
            host.ChangeConversation("main-owner-a-reopened","chat-a");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check((string)combo.SelectedValue=="packaged:gary_2"&&mode.IsChecked==true&&
                JsonElement.DeepEquals(aSaved,await Chat(host,"chat-a")),
                "reopening the same stable chat ID restores the saved persona and mode despite a new view generation");
            mutations=host.Mutations.Count;combo.SelectedValue="none";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var off=await Chat(host,"chat-a");
            Check(!off.GetProperty("enabled").GetBoolean()&&!off.GetProperty("bluntMode").GetBoolean()&&
                off.GetProperty("bluntPin").ValueKind==JsonValueKind.Null&&off.GetProperty("effectiveSource").ValueKind==JsonValueKind.Null&&
                JsonElement.DeepEquals(off.GetProperty("baseSelection"),aSaved.GetProperty("baseSelection"))&&
                JsonElement.DeepEquals(bSaved,await Chat(host,"chat-b")),
                "No personality disables both layers for A, remembers its base, and preserves B");
            Check(host.LastChatSave is {} noneRequest&&!noneRequest.TryGetProperty("baseSelection",out _)&&
                !noneRequest.TryGetProperty("catalogToken",out _)&&!host.Mutations.Skip(mutations).Contains("personality.source.prepare")&&
                !host.Mutations.Skip(mutations).Contains("instructions.save")&&
                !host.Mutations.Skip(mutations).Contains("personality.preferences.save"),
                "None omits catalog/base and never uses global save, lead cleanup or legacy source preparation");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check((string)combo.SelectedValue=="none"&&mode.IsChecked==false&&!mode.IsEnabled,
                "explicit Reload restores this chat’s disabled mode and remembered base without enabling it");
            Check(ReferenceEquals(host.Central.Current,globalBefore)&&host.Content=="Owner instructions.",
                "per-chat interaction preserves global preferences and never appends or removes lead instruction blocks");
            combo.SelectedValue="packaged:gary_2";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.Fault="chat-catalog-unavailable";reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&status.Text.Contains("Unavailable:",StringComparison.Ordinal)&&!mode.IsEnabled,
                "unavailable catalog remains diagnosed without invented choices or a selected-style substitute");
            combo.SelectedValue="none";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!(await Chat(host,"chat-a")).GetProperty("enabled").GetBoolean(),
                "None remains usable when the loaded active state has no admitted catalog");
            host.Fault="chat-private-catalog";reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            combo.SelectedValue="private:fixture-private";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(host.LastChatSave?.GetProperty("baseSelection").GetProperty("kind").GetString()=="private",
                "private choices are discovered through qualified metadata rather than public hardcoded persona bodies");
            // The injected private metadata is a deterministic reader fixture, not an imported service definition.
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            combo.SelectedValue="packaged:gary_2";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            mode.IsChecked=true;mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&(string)combo.SelectedValue=="packaged:gary_2"&&mode.IsChecked==true,
                "accessory unload/remount reads the persisted chat selection without an automatic mutation");
            var body=new StackPanel{Margin=new Thickness(16),Background=Brushes.White};
            body.Children.Add(new TextBlock{Text="Controlled style fixture — per-chat personality",Foreground=Brushes.Black,Margin=new Thickness(0,0,0,12)});
            var accessoryFrame=DesktopMountLayout.AccessoryFrame(view);
            body.Children.Add(accessoryFrame);
            body.Children.Add(new TextBox{Text="Main message draft (fixture only)",Margin=new Thickness(0,12,0,0),MinHeight=48});
            var window=new Window{Title="Personality controlled style fixture — software WPF",Content=body,Width=560,Height=210,
                Background=Brushes.White,Foreground=Brushes.Black};
            window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
            Check(view.ActualWidth<=280&&view.ActualHeight<=44&&accessoryFrame.ActualWidth<=280&&accessoryFrame.ActualHeight<=44&&
                combo.IsEnabled&&mode.IsChecked==true&&status.Text.Contains("Gary 2.0",StringComparison.Ordinal),
                "shown controlled-style selector retains its saved selection inside the existing280x44 frame");
            Check(new Control[]{combo,mode,reload}.All(value=>Math.Abs(value.ActualHeight-24)<0.1)&&
                combo.ActualWidth>=112&&Math.Abs(mode.ActualWidth-29)<0.1&&Math.Abs(reload.ActualWidth-25)<0.1,
                $"host implicit minimums/margins cannot expand compact controls; heights={combo.ActualHeight}/{mode.ActualHeight}/{reload.ActualHeight}; widths={combo.ActualWidth}/{mode.ActualWidth}/{reload.ActualWidth}");
            var personalityLabel=((Grid)view).Children.OfType<Label>().Single();
            Rect InFrame(FrameworkElement value)=>value.TransformToAncestor(accessoryFrame)
                .TransformBounds(new Rect(new Point(0,0),value.RenderSize));
            var previewContext=TemplateControl<Button>(view,"PersonalityComposerPreview");
            Check(Math.Abs(previewContext.ActualHeight-14)<0.1&&previewContext.ActualWidth<=82.1&&
                AutomationProperties.GetName(previewContext).Contains("Preview",StringComparison.Ordinal),
                "compact Preview context button overrides adversarial host minimums and has an accessible name");
            foreach(var child in new FrameworkElement[]{personalityLabel,combo,mode,reload,status,previewContext})
            {
                var bounds=InFrame(child);
                Check(bounds.Width>0&&bounds.Height>0&&bounds.Left>=-0.1&&bounds.Top>=-0.1&&
                    bounds.Right<=accessoryFrame.ActualWidth+0.1&&bounds.Bottom<=accessoryFrame.ActualHeight+0.1,
                    "controlled-style child bounds fit frame: "+(child is Label?"Personality label":AutomationProperties.GetAutomationId(child)));
            }
            var labelBounds=InFrame(personalityLabel);var comboBounds=InFrame(combo);
            var modeBounds=InFrame(mode);var reloadBounds=InFrame(reload);var statusBounds=InFrame(status);
            Check(labelBounds.Right<=comboBounds.Left+0.1&&comboBounds.Right<=modeBounds.Left+0.1&&modeBounds.Right<=reloadBounds.Left+0.1&&
                statusBounds.Top>=new[]{labelBounds.Bottom,comboBounds.Bottom,modeBounds.Bottom,reloadBounds.Bottom}.Max()-0.1&&
                statusBounds.Right<=InFrame(previewContext).Left+0.1,
                "shown compact controls neither overlap horizontally nor cover the status row");
            combo.SelectedValue="packaged:pragmatic";
            using(var savedSelection=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(!combo.IsEnabled)await Task.Delay(20,savedSelection.Token);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
            var savedTooltip=status.ToolTip as string??"";
            Check(status.Text.Contains("Saved: Pragmatic",StringComparison.Ordinal)&&mode.IsChecked==true&&
                savedTooltip.StartsWith("Saved for this chat; application not confirmed.",StringComparison.Ordinal)&&
                savedTooltip.Contains("Choosing 'No personality'",StringComparison.Ordinal)&&
                !savedTooltip.Contains(". No personality is saved",StringComparison.Ordinal),
                "saved Pragmatic tooltip describes No personality as a choice and preserves the use-unverified qualification");
            combo.IsDropDownOpen=true;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Loaded);window.UpdateLayout();
                var pickerPeer=UIElementAutomationPeer.CreatePeerForElement(combo)
                    ??throw new InvalidOperationException("The shown personality dropdown has no WPF automation peer.");
                var peers=new Queue<(AutomationPeer Peer,int Depth)>();
                var visited=new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
                var itemNames=new List<string>();peers.Enqueue((pickerPeer,0));
                while(peers.Count>0)
                {
                    var (peer,depth)=peers.Dequeue();
                    if(!visited.Add(peer))continue;
                    if(depth>32||visited.Count>256)throw new InvalidOperationException("Personality item automation tree exceeded its bound.");
                    if(peer.GetAutomationControlType()==AutomationControlType.ListItem)itemNames.Add(peer.GetName()??"");
                    foreach(var child in peer.GetChildren()??[])peers.Enqueue((child,depth+1));
                }
                var expectedNames=new[]{"No personality","Cerebral","Succinct","HUE","KVN","Gary","Gary 2.0","Pragmatic","Analytical","Teacher / Coach","Avocato","Quinn","Lord Commander","Tribore","AVA"};
                var namesSummary=string.Join(" | ",itemNames);
                Check(itemNames.Count==expectedNames.Length&&expectedNames.All(name=>itemNames.Count(actual=>actual==name)==1),
                    "actual WPF dropdown item peers expose only human labels, not records, qualified DTOs or hashes; observed="+
                    namesSummary[..Math.Min(namesSummary.Length,512)]);
            }
            finally
            {
                combo.IsDropDownOpen=false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
            }
            var beforePreviewMutations=host.Mutations.Count;
            previewContext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using(var previewReady=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(!previewContext.IsEnabled)await Task.Delay(20,previewReady.Token);
            var contextWindow=Application.Current.Windows.OfType<Window>().Single(value=>
                AutomationProperties.GetAutomationId(value)=="PersonalityContextPreview");
            var contextText=TemplateControl<TextBox>(contextWindow,"PersonalityContextText");
            Check(contextText.IsReadOnly&&contextText.Text.EndsWith(PersonalityTemplates.All.Single(value=>value.PackageKey=="pragmatic").Text+"\n"+PersonalityTemplates.BluntModeText,StringComparison.Ordinal)&&
                TemplateControl<TextBlock>(contextWindow,"PersonalityContextDescription").Text.Contains("Practical recommendations",StringComparison.Ordinal)&&
                AutomationProperties.GetName(contextText).Length>0&&host.Mutations.Count==beforePreviewMutations,
                "shown Preview context has a selected description and exact read-only saved base-plus-mode without mutations");
            host.ChangeConversation("main-preview-retired","other-chat");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!contextWindow.IsVisible,"changing chat closes the old preview instead of retargeting its content");
            host.ChangeConversation("main-preview-restored","chat-a");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bitmap=new RenderTargetBitmap((int)body.ActualWidth,(int)body.ActualHeight,96,96,PixelFormats.Pbgra32);
            bitmap.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(preview))png.Save(file);
            window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!combo.IsEnabled&&!reload.IsEnabled&&!mode.IsEnabled,"closing the fixture retires all accessory controls");
        }
        using(var host=new Host(Path.Combine(root,"composer-uncertain")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            host.Binding=new("main-owner-a","chat-a");plugin.Initialize(host);
            var view=ComposerView(plugin);var combo=TemplateControl<ComboBox>(view,"PersonalityComposerChoice");
            var reload=TemplateControl<Button>(view,"PersonalityComposerReload");var status=TemplateControl<TextBlock>(view,"PersonalityComposerStatus");
            var previewContext=TemplateControl<Button>(view,"PersonalityComposerPreview");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.FailAfter="personality.chat.save";combo.SelectedValue="packaged:hue";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!combo.IsEnabled&&!previewContext.IsEnabled&&reload.IsEnabled&&status.Text.Contains("not confirmed",StringComparison.Ordinal),
                "lost chat save acknowledgement exposes unknown outcome and requires explicit Reload");
            var mutations=host.Mutations.Count;combo.SelectedValue="packaged:kvn";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(host.Mutations.Count==mutations,"uncertain chat selector cannot replay a consumed mutation baseline");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&(string)combo.SelectedValue=="packaged:hue"&&(await Chat(host,"chat-a")).GetProperty("enabled").GetBoolean(),
                "explicit reconciliation shows the actual committed selection after lost acknowledgement");
            host.DelayAfter="personality.chat.save";combo.SelectedValue="packaged:kvn";await host.Dispatched.Task;
            mutations=host.Mutations.Count;
            string LateOwnerDiagnostic()=>
                $"selectorEnabled={combo.IsEnabled}; reloadEnabled={reload.IsEnabled}; mutations={host.Mutations.Count}/{mutations}; status={status.Text[..Math.Min(status.Text.Length,160)].Replace('\r',' ').Replace('\n',' ')}";
            host.ChangeConversation("main-owner-b","chat-b");
            Check(!combo.IsEnabled&&!reload.IsEnabled&&host.Mutations.Count==mutations,
                "owner change invalidates controls without another mutation while A's acknowledgement is held; "+LateOwnerDiagnostic());
            host.Release.TrySetResult();
            // One dispatcher yield need not await the released asynchronous acknowledgement's
            // later dispatcher continuation. Observe bounded settlement; never replay the save.
            using(var settling=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    while(!reload.IsEnabled)await Task.Delay(20,settling.Token);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                catch(OperationCanceledException error)when(settling.IsCancellationRequested)
                {throw new InvalidOperationException("Late owner acknowledgement did not settle within five seconds; "+LateOwnerDiagnostic(),error);}
            }
            var chatBEnabled=(await Chat(host,"chat-b")).GetProperty("enabled").GetBoolean();
            var lateSelectorEnabled=combo.IsEnabled;var lateReloadEnabled=reload.IsEnabled;
            var lateMutationCount=host.Mutations.Count;
            Check(!lateSelectorEnabled&&lateReloadEnabled&&lateMutationCount==mutations&&!chatBEnabled,
                "a late chat A acknowledgement neither updates B’s UI nor writes B; "+
                $"selectorEnabled={lateSelectorEnabled}; reloadEnabled={lateReloadEnabled}; mutations={lateMutationCount}/{mutations}; chatBEnabled={chatBEnabled}; "+LateOwnerDiagnostic());
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsEnabled&&(string)combo.SelectedValue=="none","Reload explicitly binds B after the prior operation settles");
            host.Fault="chat-wrong-identity";reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!combo.IsEnabled&&reload.IsEnabled&&status.Text.Contains("different chat",StringComparison.Ordinal),
                "a reply for another chat is refused without fallback or mutation");
            host.DelayAfter="personality.chat.get";reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await host.Dispatched.Task;
            plugin.Dispose();host.Release.TrySetResult();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!combo.IsEnabled&&!reload.IsEnabled,"disposal cannot revive controls after a delayed chat read");
        }
        using(var host=new Host(Path.Combine(root,"composer-capacity")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            for(var index=0;index<8;index++)await host.SeedDefinition("style-main-"+index,index==0?string.Concat(Enumerable.Repeat("解析",30)):"Saved "+index,"Keep the existing definition.");
            host.Binding=new("main-capacity","capacity-chat");var before=host.Central.Current;
            plugin.Initialize(host);var view=ComposerView(plugin);
            var combo=TemplateControl<ComboBox>(view,"PersonalityComposerChoice");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            combo.SelectedValue="packaged:teacher-coach";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check((await Chat(host,"capacity-chat")).GetProperty("baseSelection").GetProperty("id").GetString()=="teacher-coach"&&
                ReferenceEquals(host.Central.Current,before)&&host.CurrentPreferences.CustomPresets.Count==8,
                "eight custom slots including a valid non-ASCII name stay intact while a packaged practical profile is selected");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
        using(var host=new Host(Path.Combine(root,"composer-no-chat")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            host.Binding=new("main-unsaved",null);plugin.Initialize(host);var view=ComposerView(plugin);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!TemplateControl<ComboBox>(view,"PersonalityComposerChoice").IsEnabled&&host.Operations.Count==0&&
                TemplateControl<TextBlock>(view,"PersonalityComposerStatus").Text.Contains("stable",StringComparison.Ordinal),
                "missing stable chat identity diagnoses unavailable without calling a global or invented route");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }
    private static async Task ComposerPreviewChecks()
    {
        static string HashText(string value)=>Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false,true).GetBytes(value)));
        using(var host=new Host(Path.Combine(root,"preview-exact")){PluginSurface=DesktopPluginSurface.Orchestrator})
        {
            host.Binding=new("preview-owner","preview-chat");
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);
            var state=await editor.LoadChatAsync(host.Binding,default);
            foreach(var template in PersonalityTemplates.All)
            {
                state=await editor.SaveChatAsync(state,true,state.Choices.Single(value=>value.Key=="packaged:"+template.PackageKey).Base,true,default);
                var before=host.Mutations.Count;var result=await editor.PreviewChatAsync(state);
                var effective=template.Text+"\n"+PersonalityTemplates.BluntModeText;
                Check(result.Context=="Optional communication style reference 'effective', revision 1, SHA-256 "+HashText(effective)+":\n"+effective&&
                    result.Sha256==state.EffectiveSource!.Sha256&&result.Description.Contains(template.Description,StringComparison.Ordinal)&&
                    host.Mutations.Count==before&&host.CurrentPreferences.CustomPresets.Count==0,
                    template.Name+" packaged preview reconstructs the exact admitted base-plus-mode wrapper without consuming custom slots or mutating state");
            }
            foreach(var builtin in new[]{"cerebral","succinct"})
            {
                state=await editor.SaveChatAsync(state,true,state.Choices.Single(value=>value.Key=="packaged:"+builtin).Base,false,default);
                var operationStart=host.Operations.Count;var before=host.Mutations.Count;
                var result=await editor.PreviewChatAsync(state);
                Check(result.Sha256==state.Base!.Sha256&&!result.Context.Contains(PersonalityTemplates.BluntModeText,StringComparison.Ordinal)&&
                    host.Operations.Skip(operationStart).SequenceEqual(new[]{"personality.chat.get","personality.get","personality.chat.get"})&&host.Mutations.Count==before,
                    builtin+" preview uses the existing exact-revision owner read and omits the disabled modifier");
            }
            state=await editor.SaveChatAsync(state,false,null,false,default);
            var noneStart=host.Operations.Count;var noneMutations=host.Mutations.Count;
            var none=await editor.PreviewChatAsync(state);
            Check(none.Name=="No personality"&&none.Sha256 is null&&!none.Context.Contains("Optional communication style reference",StringComparison.Ordinal)&&
                host.Operations.Skip(noneStart).SequenceEqual(new[]{"personality.chat.get"})&&host.Mutations.Count==noneMutations,
                "None preview states both layers are disabled and performs no source lookup, preparation or mutation");

            await host.SeedDefinition("style-preview","Owner wording","Use the exact old custom wording.");
            state=await editor.LoadChatAsync(host.Binding,default);
            state=await editor.SaveChatAsync(state,true,state.Choices.Single(value=>value.Key=="custom:style-preview").Base,false,default);
            await host.SeedDefinition("style-preview","Owner wording","New editable wording must not replace the saved revision.");
            await host.Prepare();
            var customMutations=host.Mutations.Count;var old=await editor.PreviewChatAsync(state);
            Check(old.Context.EndsWith("Use the exact old custom wording.",StringComparison.Ordinal)&&
                !old.Context.Contains("New editable wording",StringComparison.Ordinal)&&host.Mutations.Count==customMutations,
                "custom preview reads the exact retained saved revision after the current editable definition changes");
            host.Fault="preview-wrong-source";
            await Reject(()=>editor.PreviewChatAsync(state),"preview refuses a mismatched exact source body/hash without displaying a substitute");
            host.DelayAfter="personality.get";var late=editor.PreviewChatAsync(state);await host.Dispatched.Task;
            host.ChangeConversation("preview-owner-replaced","another-chat");host.Release.TrySetResult();
            await Reject(()=>late,"late exact-source reply is refused after owner/chat replacement");
            Check(host.Mutations.Count==customMutations,"failed and retired previews never prepare, save, submit, or replay a mutation");
        }

        // Synthetic metadata isolates old saved pins from the installed library's current rows.
        // The actual service revision/archive behavior is covered by its separate owner fixture.
        using(var host=new Host(Path.Combine(root,"preview-archived")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            host.Binding=new("archive-owner","archive-chat");
            var current=await Chat(host,"archive-chat");
            var original=PersonalityTemplates.RevisionOne.Single(value=>value.PackageKey=="hue");
            var selection=new{kind="packaged",id=original.PackageKey,revision=1L,sha256=HashText(original.Text)};
            var mode=new{id="blunt",revision=1L,sha256=HashText(PersonalityTemplates.BluntModeText)};
            var effective=original.Text+"\n"+PersonalityTemplates.BluntModeText;var sha=HashText(effective);
            var descriptor=HashText(JsonSerializer.Serialize(new{baseSelection=selection,bluntPin=mode},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            JsonElement Archived()=>JsonSerializer.SerializeToElement(new{schemaVersion=1,conversationId="archive-chat",revision=5L,enabled=true,
                baseSelection=selection,bluntMode=true,bluntPin=mode,availability="ready",diagnostic=(object?)null,
                effectiveSource=new{sourceId="personality/v2/"+descriptor+"/"+sha,revision=1L,sha256=sha},
                catalogToken=current.GetProperty("catalogToken").GetString(),catalog=current.GetProperty("catalog")});
            host.ChatReplyOverride=Archived();
            var editor=new PersonalityEditor(host,new(host.DataDirectory),()=>host.Binding);
            var state=await editor.LoadChatAsync(host.Binding,default);var before=host.Mutations.Count;
            var result=await editor.PreviewChatAsync(state);
            Check(state.Available&&result.Context.EndsWith(effective,StringComparison.Ordinal)&&
                !result.Context.Contains(PersonalityTemplates.All.Single(value=>value.PackageKey=="hue").Text,StringComparison.Ordinal)&&host.Mutations.Count==before,
                "saved revision-one preview resolves the retained original body rather than silently using enriched revision two");
            plugin.Initialize(host);var view=ComposerView(plugin);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var combo=TemplateControl<ComboBox>(view,"PersonalityComposerChoice");
            Check((string)combo.SelectedValue=="saved:hue:1"&&combo.SelectedItem!.ToString()=="Hue (saved earlier version)"&&
                combo.Items.Cast<object>().Any(value=>value.ToString()=="HUE")&&
                TemplateControl<System.Windows.Controls.Primitives.ToggleButton>(view,"PersonalityComposerBlunt").IsEnabled&&host.Mutations.Count==before,
                "old saved choice remains visibly distinct from current HUE and its mode stays available without an automatic upgrade");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            async Task RefuseMetadata(Action<System.Text.Json.Nodes.JsonNode> alter,string label)
            {
                var node=System.Text.Json.Nodes.JsonNode.Parse(Archived().GetRawText())!;alter(node);
                host.ChatReplyOverride=JsonSerializer.SerializeToElement(node);
                var invalid=await editor.LoadChatAsync(host.Binding,default);var operationStart=host.Operations.Count;
                await Reject(()=>editor.PreviewChatAsync(invalid),label);
                Check(!host.Operations.Skip(operationStart).Any(value=>value is "personality.get" or "personality.private.get" or "personality.private.import")&&host.Mutations.Count==before,
                    label+" uses no guessed/private source retrieval or mutation");
            }
            await RefuseMetadata(node=>node["effectiveSource"]!["sha256"]=new string('a',64),"preview rejects an effective content hash mismatch");
            await RefuseMetadata(node=>node["effectiveSource"]!["sourceId"]="personality/v2/"+new string('b',64)+"/"+sha,"preview rejects an effective descriptor identity mismatch");
            await RefuseMetadata(node=>node["bluntPin"]!["revision"]=99L,"preview rejects an unknown modifier revision rather than using the current text");
            await RefuseMetadata(node=>node["baseSelection"]!["revision"]=99L,"preview refuses an unknown packaged revision without using latest");
            await RefuseMetadata(node=>node["baseSelection"]!["kind"]="private","private preview is explicitly unavailable and never reads a private body");
            host.ChatReplyOverride=null;
            await Reject(()=>editor.PreviewChatAsync(state),"preview refuses a changed saved chat revision instead of presenting its old snapshot as current");
        }
        using(var host=new Host(Path.Combine(root,"preview-lifetime")){PluginSurface=DesktopPluginSurface.Orchestrator})
        using(var plugin=new PersonalityPlugin())
        {
            host.Binding=new("preview-lifetime-a","preview-a");plugin.Initialize(host);var view=ComposerView(plugin);
            var reload=TemplateControl<Button>(view,"PersonalityComposerReload");
            var button=TemplateControl<Button>(view,"PersonalityComposerPreview");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.DelayAfter="personality.chat.get";var before=host.Mutations.Count;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await host.Dispatched.Task;
            Check(!button.IsEnabled&&!reload.IsEnabled,"preview disables interaction while its read is pending");
            host.ChangeConversation("preview-lifetime-b","preview-b");host.Release.TrySetResult();
            using(var settled=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(!reload.IsEnabled)await Task.Delay(20,settled.Token);
            Check(!button.IsEnabled&&!Application.Current.Windows.OfType<Window>().Any(value=>
                AutomationProperties.GetAutomationId(value)=="PersonalityContextPreview")&&host.Mutations.Count==before,
                "late preview for chat A cannot open a window or retarget to B and requires explicit Reload");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Check(!button.IsEnabled&&!reload.IsEnabled&&!Application.Current.Windows.OfType<Window>().Any(value=>
                AutomationProperties.GetAutomationId(value)=="PersonalityContextPreview")&&host.Mutations.Count==before,
                "unload retires preview interaction without opening a window or mutating anything");
        }
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var nested in Nodes(child))yield return nested;}
    private static void Check(bool value,string label){if(!value)throw new InvalidOperationException("Failed: "+label);checks++;Console.WriteLine("PASS: "+label);}
    private static async Task Reject(Func<Task> action,string label){try{await action();}catch(Exception failure)when(failure is InvalidOperationException or InvalidDataException or IOException or ArgumentException or NotSupportedException){checks++;Console.WriteLine("PASS: "+label);return;}throw new InvalidOperationException("Expected refusal: "+label);}
    private sealed partial class Host:IDesktopPluginHost,IDesktopPluginEnvironment,IDesktopServiceHost,IDesktopMountHost,IServicePluginHost,IServicePluginSettingsHost,IDisposable
    {
        internal readonly ServicePlugin Service=new();internal readonly Central Central=new();internal string Content="Owner instructions.";
        internal DesktopConversationBinding Binding=new("configuration-owner-a",null);internal readonly List<string> Mutations=[],Operations=[];
        internal string? FailAfter,DelayAfter,ReintroduceAfterPrepare,Fault,LastPreparedId;internal JsonElement? LastChatSave,ChatReplyOverride;internal bool? EnabledAtInstructionsSave,PreparedWhileEnabled;
        internal TaskCompletionSource Dispatched=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DataDirectory{get;}public string PackageDirectory=>AppContext.BaseDirectory;public string PluginId=>"pegasus.personality";
        internal DesktopPluginSurface PluginSurface=DesktopPluginSurface.Configuration;
        public DesktopPluginSurface Surface=>PluginSurface;public IScopedPluginSettings Settings=>Central;
        public IReadOnlyList<DesktopMountCapability> GetMountCapabilities()=>[new(DesktopMountSlots.ComposerAccessories,1,
            Surface==DesktopPluginSurface.Orchestrator,[DesktopMountKind.View],null)];
        internal Cerebral.PersonalityServicePlugin.PersonalitySettingsPreferences CurrentPreferences=>Cerebral.PersonalityServicePlugin.PersonalitySettingsCodec.Read(Central.Current.Active?.Values??Element(new{enabled=false,selectedPresetId="cerebral",customPresets=Array.Empty<object>()}));
        internal Cerebral.PersonalityServicePlugin.PersonalitySettingsPreferences SettingsValues=>CurrentPreferences;
        internal Host(string path){DataDirectory=Path.GetFullPath(path);Directory.CreateDirectory(DataDirectory);Service.Initialize(this);}
        internal async Task Select(bool enabled,string id)=>await Save(new(enabled,id,CurrentPreferences.CustomPresets));
        internal async Task SeedDefinition(string id,string name,string text)
        {var defs=CurrentPreferences.CustomPresets.Where(x=>x.Id!=id).Append(new Cerebral.PersonalityServicePlugin.PersonalityCustomDefinition(id,name,text)).ToArray();await Save(new(false,id,defs));}
        private async Task Save(Cerebral.PersonalityServicePlugin.PersonalitySettingsPreferences values)=>await ExecuteServiceCommandAsync("personality.preferences.save",new{schemaVersion=2,expectedRevision=Central.Current.Revision,values=Cerebral.PersonalityServicePlugin.PersonalitySettingsCodec.Write(values)});
        internal async Task Prepare(){var sources=(await ExecuteServiceCommandAsync("personality.sources.get",new{})).Data;await ExecuteServiceCommandAsync("personality.source.prepare",new{schemaVersion=1,expectedSettingsRevision=Central.Current.Revision,expectedStoreRevision=sources.GetProperty("revision").GetInt64(),expectedStoreSha256=sources.GetProperty("sha256").GetString()});}
        public async Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation,object arguments,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();Operations.Add(operation);var input=Element(arguments);JsonElement data;
            if(operation=="instructions.get")data=Lead();
            else if(operation=="instructions.save")
            {
                Mutations.Add(operation);if(input.GetProperty("role").GetString()!="lead")throw new Exception("Wrong role");
                if(input.GetProperty("expectedSha256").GetString()!=Sha(Content))return new(false,default,"Synthetic instructions SHA CAS conflict.");
                EnabledAtInstructionsSave=CurrentPreferences.Enabled;Content=input.GetProperty("content").GetString()!;data=Lead();
                if(Fault=="wrong-lead-readback"){Content+="External concurrent edit.";Fault=null;}
            }
            else
            {
                if(operation is "personality.chat.save" or "personality.preferences.save" or "personality.source.prepare" or
                    "personality.preferences.transfer.save" or "personality.preferences.pending.prepare" or
                    "personality.preferences.pending.apply" or "personality.preferences.transfer.prepare")Mutations.Add(operation);
                if(operation=="personality.source.prepare")PreparedWhileEnabled=CurrentPreferences.Enabled;
                if(operation=="personality.chat.save")LastChatSave=input.Clone();
                data=Element(await Service.ExecuteAsync(operation,input,token));
                if(operation=="personality.source.prepare"){LastPreparedId=data.GetProperty("preset").GetProperty("id").GetString();if(ReintroduceAfterPrepare is {} legacy){Content=legacy;ReintroduceAfterPrepare=null;}}
            }
            data=TransferFault(operation,input,data);
            if(operation=="personality.chat.get"&&ChatReplyOverride is {} chatOverride)data=chatOverride;
            if(Fault is {} fault)
            {
                var node=System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!;
                if(operation=="personality.chat.get"&&fault=="chat-wrong-identity"){node["conversationId"]="foreign-chat";Fault=null;}
                else if(operation=="personality.chat.get"&&fault=="chat-catalog-unavailable")
                {node["catalogToken"]=null;node["catalog"]=new System.Text.Json.Nodes.JsonArray();node["availability"]="unavailable";node["diagnostic"]=System.Text.Json.Nodes.JsonNode.Parse("{\"code\":\"catalog-unavailable\",\"message\":\"Fixture library is unavailable.\"}");Fault=null;}
                else if(operation=="personality.chat.get"&&fault=="chat-private-catalog")
                {((System.Text.Json.Nodes.JsonArray)node["catalog"]!).Add(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"private\",\"id\":\"fixture-private\",\"revision\":1,\"sha256\":\""+new string('a',64)+"\",\"name\":\"Private reader fixture\",\"availability\":\"ready\",\"diagnostic\":null}"));Fault=null;}
                else if(operation=="personality.preferences.save"&&fault=="absent-default-ack"){node["state"]!["active"]=null;Fault=null;}
                else if(operation=="personality.preferences.save"&&fault=="wrong-coordinate"){node["state"]!["coordinate"]!["pluginId"]="foreign";Fault=null;}
                else if(operation=="personality.preferences.save"&&fault=="wrong-ack-values"){node["state"]!["active"]!["values"]!["enabled"]=true;Fault=null;}
                else if(operation=="personality.preferences.save"&&fault=="wrong-ack-revision"){node["state"]!["revision"]=Central.Current.Revision+5;Fault=null;}
                else if(operation=="personality.source.prepare"&&fault=="wrong-source-content"){node["preset"]!["text"]="Different content";Fault=null;}
                else if(operation=="personality.get"&&fault=="preview-wrong-source"){node["text"]="Mismatched fixture content.";Fault=null;}
                else if(operation=="personality.source.prepare"&&fault=="wrong-source-id"){node["preset"]!["sourceId"]="personality/v1/foreign/1/"+new string('a',64);Fault=null;}
                else if(operation=="personality.sources.get"&&fault=="wrong-source-readback"){node["sha256"]=new string('a',64);Fault=null;}
                else if(operation=="personality.preferences.get"&&fault=="aliased-preferences"){node["SchemaVersion"]=2;Fault=null;}
                data=Element(node);
            }
            if(FailAfter==operation){FailAfter=null;throw new IOException("Acknowledgement lost after committed fixture mutation.");}
            if(DelayAfter==operation){DelayAfter=null;Dispatched.TrySetResult();await Release.Task;Dispatched=new(TaskCreationOptions.RunContinuationsAsynchronously);Release=new(TaskCreationOptions.RunContinuationsAsynchronously);}
            return new(true,data,null);
        }
        private JsonElement Lead()=>Element(new{path="task-owned synthetic lead",content=Content,sha256=Sha(Content)});
        private static string Sha(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
        private static JsonElement Element(object? value)=>JsonSerializer.SerializeToElement(value,Json);
        public DesktopConversationSnapshot GetConversation()=>new(Binding,"Configuration",null,null,false);public DesktopDraftSnapshot GetDraft()=>new(Binding,0,"");
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted{add{}remove{}}
        internal void ChangeConversation(string generation,string? session)
        {Binding=new(generation,session);ConversationChanged?.Invoke(this,new(GetConversation()));}
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate a,CancellationToken t=default)=>throw new NotSupportedException();
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission a,CancellationToken t=default)=>throw new NotSupportedException();
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission a,CancellationToken t=default)=>throw new NotSupportedException();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding a,CancellationToken t=default)=>throw new NotSupportedException();
        public void DismissInputGuidance(){}public void ReportStatus(string text){}public void RefreshPresentation(){}public void Publish(string type,string? id,object? data){}
        public void Dispose()=>Service.Dispose();
    }
    private sealed partial class Central:IScopedPluginSettings
    {
        internal readonly ServicePolicy Policy=new();internal bool CompletePendingUnavailable;
        internal PluginSettingsState Current=new(new("pegasus.personality","global"),0,null,[],"service-policy");
        public PluginSettingsState Get(string scope="global",string? scopeId=null){if(CompletePendingUnavailable || Purposes.Any(x=>x.Purpose==SettingsTransferPurpose.Complete))throw new NotSupportedException("Legacy scoped body is unavailable with complete pending.");return Current;}
        public PluginSettingsSaveResult Save(int version,JsonElement values,long expected,string scope="global",string? scopeId=null)
        {
            if(CompletePendingUnavailable)throw new NotSupportedException("Complete pending cannot be bypassed.");
            if(expected!=Current.Revision)throw new InvalidOperationException("Synthetic central CAS conflict.");
            var document=new PluginSettingsDocument("pegasus.personality",scope,scopeId,ServicePolicy.PackageVersion,version,values);
            var admitted=Policy.Admit(document,Current.Active?.Values,false);if(!admitted.Accepted)throw new InvalidDataException("Actual package settings policy refused.");
            var changed=Current.Active is null||!JsonElement.DeepEquals(Current.Active.Values,admitted.Values);
            var portable=Policy.ExportPortable(document);Current=Current with{Revision=Current.Revision+(changed?1:0),Active=document,PortableValues=portable.Values,OmittedPortableFields=portable.OmittedFields};
            return new(Current,changed);
        }
    }
}
