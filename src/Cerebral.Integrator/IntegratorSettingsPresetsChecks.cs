using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    internal async Task CheckSettingsPresetsAsync(string captureDirectory)
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); count++; }
        async Task Reject<T>(Func<Task> action, string label) where T : Exception
        {
            try { await action(); }
            catch (T) { count++; return; }
            throw new InvalidOperationException(label);
        }
        static Dictionary<string, JsonElement> FixtureMembers(JsonElement value) => value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        static CommandResult Ok(object? value) => new(true, CerebralJson.Element(value));
        static CommandResult Fail(string error) => new(false, CerebralJson.Element(new { }), error);
        static SettingsPresetSummary Summary(string id = "preset-a", string name = "Workspace", long revision = 1) =>
            new(id, name, revision, new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero),
                new(true, false, ["preferences", "plugin-settings", "layout"], 2, 1));
        static SettingsPresetCapabilities Capabilities() => new(SettingsPreferenceContract.SchemaVersion,
            ConfigurationBackupContract.SchemaVersion, SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes,
            SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes, 128, 32, 128, 128, 512,
            ["preferences", "plugin-settings", "layout"], "configuration.restore.apply", "configuration.restore.status");

        Check(SettingsPresetClient.NormalizeName("  Workspace  ") == "Workspace", "Names trim edges.");
        Check(SettingsPresetClient.NormalizeName(new string('界', 128)).Length == 128, "Valid Unicode names fit the exact UTF16/UTF8 limits.");
        await Reject<ArgumentException>(() => Task.FromResult(SettingsPresetClient.NormalizeName(new string('x', 129))), "Oversized name was admitted.");
        await Reject<ArgumentException>(() => Task.FromResult(SettingsPresetClient.NormalizeName("x\0y")), "NUL name was admitted.");
        await Reject<ArgumentException>(() => Task.FromResult(SettingsPresetClient.NormalizeName("\ud800")), "Invalid surrogate was admitted.");

        var calls = 0;
        var current = false;
        var session = new SettingsPresetClient((_, _, _) => { calls++; return Task.FromResult(Ok(new[] { Summary() })); }, () => current);
        await Reject<InvalidOperationException>(() => session.ListAsync(default), "Stale owner dispatched.");
        Check(calls == 0, "Pre-dispatch owner change makes no call.");
        current = true;
        var delayed = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session = new((_, _, _) => delayed.Task, () => current);
        var late = session.ListAsync(default); current = false; delayed.SetResult(Ok(new[] { Summary() }));
        await Reject<InvalidOperationException>(() => late, "Late old-owner data was adopted.");

        session = new((_, _, _) => Task.FromResult(Fail("Unknown command: configuration.presets.describe")), () => true);
        await Reject<SettingsPresetsUnavailableException>(() => session.DescribeAsync(default), "Absent capability was reported available.");
        session = new((_, _, _) => Task.FromResult(Ok(Capabilities())), () => true);
        Check((await session.DescribeAsync(default)).DockingImplemented == false, "Descriptor does not claim docking.");

        session = new((_, _, _) => Task.FromResult(Ok(new[] { Summary(), Summary() })), () => true);
        await Reject<InvalidDataException>(() => session.ListAsync(default), "Duplicate preset identities were admitted.");
        session = new((_, _, _) => Task.FromResult(Ok(Summary())), () => true);
        Check((await session.CaptureAsync("preset-a", "Workspace", 0, default)).Revision == 1, "Exact new-preset acknowledgement accepted.");
        Check((await session.CaptureAsync("preset-a", "Workspace", 1, default)).Revision == 1, "Known no-op acknowledgement accepted.");
        session = new((_, _, _) => Task.FromResult(Ok(Summary("wrong"))), () => true);
        await Reject<InvalidOperationException>(() => session.CaptureAsync("preset-a", "Workspace", 0, default), "Wrong acknowledgement was admitted.");
        Check(session.Uncertainty?.Id == "preset-a", "Wrong acknowledgement retains submitted identity.");

        calls = 0;
        session = new((operation, _, _) =>
        {
            calls++;
            return operation == "configuration.presets.list" ? Task.FromResult(Ok(new[] { Summary() })) : Task.FromException<CommandResult>(new IOException("fixture loss"));
        }, () => true);
        await Reject<InvalidOperationException>(() => session.CaptureAsync("preset-a", "Workspace", 0, default), "Lost reply was reported committed.");
        Check(calls == 1 && session.Uncertainty?.Name == "Workspace", "Lost reply retains name without a retry.");
        await Reject<InvalidOperationException>(() => session.CaptureAsync("preset-a", "Workspace", 0, default), "Uncertain mutation was resent.");
        Check(calls == 1, "Blocked retry does not dispatch.");
        Check((await session.ReconcileAsync(default))?.Revision == 1, "Explicit reconciliation reads metadata.");
        await Reject<InvalidOperationException>(() => session.CaptureAsync("preset-a", "Workspace", 0, default), "Original consumed request was replayed after read.");
        Check(calls == 2, "Read reconciliation did not replay mutation.");

        calls = 0;
        var reconciliationMode = "absent";
        var reconciliationCurrent = true;
        var absent = new SettingsPresetClient((operation, _, _) =>
        {
            calls++;
            if (operation != "configuration.presets.list") return Task.FromException<CommandResult>(new IOException("fixture lost before commit"));
            return reconciliationMode switch
            {
                "failed" => Task.FromResult(Fail("Fixture authentication failure")),
                "malformed" => Task.FromResult(Ok(new[] { Summary("duplicate"), Summary("duplicate") })),
                _ => Task.FromResult(Ok(Array.Empty<SettingsPresetSummary>()))
            };
        }, () => reconciliationCurrent);
        await Reject<InvalidOperationException>(() => absent.CaptureAsync("absent-original", "Workspace", 0, default), "Lost-before-commit reply passed.");
        reconciliationMode = "failed";
        await Reject<InvalidOperationException>(() => absent.ReconcileAsync(default), "Failed read cleared uncertainty.");
        Check(absent.Uncertainty is not null, "Authentication/read failure retains uncertainty.");
        reconciliationMode = "malformed";
        await Reject<InvalidDataException>(() => absent.ReconcileAsync(default), "Malformed list cleared uncertainty.");
        Check(absent.Uncertainty is not null, "Malformed list retains uncertainty.");
        reconciliationCurrent = false;
        var beforeSwitch = calls;
        await Reject<InvalidOperationException>(() => absent.ReconcileAsync(default), "Switched owner cleared uncertainty.");
        Check(calls == beforeSwitch && absent.Uncertainty is not null, "Owner switch makes no reconciliation call.");
        reconciliationCurrent = true; reconciliationMode = "absent";
        Check(await absent.ReconcileAsync(default) is null && absent.LastReconciliationWasAbsent && absent.Uncertainty is null,
            "Complete same-owner empty list establishes current absence.");
        Check(absent.UnconfirmedHistory.Single().Id == "absent-original", "Absent reconciliation preserves original identity/history.");
        var beforeReplay = calls;
        await Reject<InvalidOperationException>(() => absent.CaptureAsync("absent-original", "Workspace", 0, default), "Absent original mutation replayed.");
        Check(calls == beforeReplay, "Spent absent original remains blocked.");
        await Reject<InvalidOperationException>(() => absent.CaptureAsync("deliberately-new", "Workspace", 0, default), "New fixture loss passed.");
        Check(calls == beforeReplay + 1 && absent.Uncertainty?.Id == "deliberately-new",
            "Deliberately new identity dispatches without replaying original.");

        session = new((_, _, _) => Task.FromResult(Fail("settings_preset_rejected: Fixture size bound")), () => true);
        await Reject<SettingsPresetRejectedException>(() => session.CaptureAsync("preset-a", "Workspace", 0, default), "Fixed known rejection was uncertain.");
        Check(session.Uncertainty is null, "Known transaction rejection preserves repairable state.");
        var rawRetained = false;
        session = new((_, arguments, _) =>
        {
            rawRetained = CerebralJson.Element(arguments).GetProperty("document").GetProperty("unknownFixtureField").GetBoolean();
            return Task.FromResult(Ok(Summary("import", "Imported")));
        }, () => true);
        using (var raw = JsonDocument.Parse("{\"unknownFixtureField\":true}"))
            await session.ImportAsync("import", "Imported", 0, raw.RootElement, default);
        Check(rawRetained, "Import preserves original unknown JSON for server admission.");

        var values = IntegratorSettingsLayoutCodec.Write(new(new(new(-100, 20, 1024, 768), false), "ConfigurationTabBackup",
            new Dictionary<string, string> { ["ApplicationTabs"] = "ConfigurationAreaSystem", ["ConfigurationSystemTabs"] = "ConfigurationTabBackup" }));
        Check(IntegratorSettingsLayoutCodec.Read(values)?.Window.NormalBounds.Left == -100, "Negative monitor coordinates round-trip.");
        Check(IntegratorSettingsLayoutCodec.Read(CerebralJson.Element(new { })) is null, "Stored empty layout is an explicit reset.");
        using (var duplicate = JsonDocument.Parse("{\"window\":{},\"window\":{}}"))
            await Reject<InvalidDataException>(() => Task.FromResult(IntegratorSettingsLayoutCodec.Read(duplicate.RootElement)), "Duplicate layout fields were admitted.");
        await Reject<InvalidDataException>(() => Task.FromResult(IntegratorSettingsLayoutCodec.Read(CerebralJson.Element(new[] { 1 }))), "Array layout was admitted.");
        await Reject<InvalidDataException>(() => Task.FromResult(IntegratorSettingsLayoutCodec.Write(new(new(new(0, 0, -1, 768), false), null, new Dictionary<string, string>()))), "Negative size was admitted.");

        var writes = 0; var applies = 0;
        var stored = new InterfaceLayoutState("Integrator", 1, IntegratorSettingsLayoutCodec.SchemaVersion, values);
        session = new((operation, arguments, _) =>
        {
            if (operation == "interface.layout.get") return Task.FromResult(Ok(stored));
            writes++;
            var request = (InterfaceLayoutSaveRequest)arguments!;
            stored = new("Integrator", request.ExpectedRevision + 1, request.SchemaVersion, request.Values);
            return Task.FromResult(Ok(new InterfaceLayoutSaveResult(stored, true)));
        }, () => true);
        SettingsLayoutSession? layout = null;
        layout = new(() => values, _ => { applies++; layout!.Changed(); return Task.CompletedTask; });
        await layout.LoadAsync(session, false, default);
        Check(applies == 1 && writes == 0 && !layout.Dirty, "Hydration causes no write or dirty default.");
        await layout.FlushAsync(session, false, default);
        Check(writes == 0, "Unchanged automatic flush makes no call.");

        stored = new("Integrator", 2, 99, CerebralJson.Element(new { future = true }));
        var future = new SettingsLayoutSession(() => values, _ => { applies++; return Task.CompletedTask; });
        await future.LoadAsync(session, false, default);
        await Reject<InvalidOperationException>(() => future.FlushAsync(session, true, default), "Future layout was overwritten.");
        Check(!future.Supported && future.Blocked && writes == 0, "Unsupported layout remains retained.");

        stored = new("Integrator", 3, 1, CerebralJson.Element(new { invalid = true }));
        var invalid = new SettingsLayoutSession(() => values, _ => Task.CompletedTask);
        await Reject<JsonException>(() => invalid.LoadAsync(session, false, default), "Invalid layout was hydrated.");
        Check(!invalid.Supported && invalid.Blocked && writes == 0, "Invalid layout blocks fallback overwrite.");

        var gate = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raceClient = new SettingsPresetClient((_, _, _) => gate.Task, () => true);
        var raceApplied = 0;
        var race = new SettingsLayoutSession(() => values, _ => { raceApplied++; return Task.CompletedTask; });
        var loading = race.LoadAsync(raceClient, false, default);
        race.Changed(); gate.SetResult(Ok(new InterfaceLayoutState("Integrator", 1, IntegratorSettingsLayoutCodec.SchemaVersion,
            IntegratorSettingsLayoutCodec.Write(new(new(new(50, 50, 1360, 880), false), null, new Dictionary<string, string>())))));
        await loading;
        Check(raceApplied == 0 && race.Dirty, "User change wins delayed hydration.");

        writes = 0; stored = new("Integrator", 0, null, null);
        var lostLayoutClient = new SettingsPresetClient((operation, _, _) =>
        {
            if (operation == "interface.layout.get") return Task.FromResult(Ok(stored));
            writes++; return Task.FromException<CommandResult>(new IOException("fixture loss"));
        }, () => true);
        var lostLayout = new SettingsLayoutSession(() => values, _ => Task.CompletedTask);
        await lostLayout.LoadAsync(lostLayoutClient, false, default); lostLayout.Changed();
        await Reject<IOException>(() => lostLayout.FlushAsync(lostLayoutClient, false, default), "Lost layout save passed.");
        await Reject<InvalidOperationException>(() => lostLayout.FlushAsync(lostLayoutClient, false, default), "Lost layout was retried.");
        Check(writes == 1 && lostLayout.Blocked, "Uncertain layout stops automatic retries.");

        var fixtureHost = new ConfigurationBackupHost(2, 8, 100, 30, 100, 30, 512, 10, 2, "", 60, "wait");
        var marker = "opaque-fixture-must-not-render";
        var fixtureAgent = new ConfigurationBackupAgent { Id = "agent-a", Name = "Example", ModelId = "fixture-model",
            Instructions = "Public instruction fixture.", AllowedRoots = [@"C:\fixture"], AllowedTools = ["fixture.read"],
            PluginSettings = [new("fixture.private", "1.0.0", 1, CerebralJson.Element(new { marker }))] };
        var fixtureDocument = new ConfigurationBackupDocument(ConfigurationBackupContract.Format, ConfigurationBackupContract.SchemaVersion,
            "fixture", DateTimeOffset.UnixEpoch, fixtureHost, new(@"C:\fixture\models", @"C:\fixture\projects", @"C:\fixture\shared"),
            [fixtureAgent], [])
        {
            Settings = new([new("active", new("fixture.private", "global", null, "1.0.0", 1, CerebralJson.Element(new { marker })))],
                [], CerebralJson.Element(new { marker }))
        };
        var completeCapabilities = new CompleteSettingsCapabilities(CompleteSettingsContract.SchemaVersion,
            ConfigurationBackupContract.SchemaVersion, CompleteSettingsContract.BackupSchemaVersion,
            [SettingsTransferPurpose.Portable, SettingsTransferPurpose.Complete],
            "configuration.presets.export", "configuration.backup.export",
            CompleteSettingsContract.PluginGetOperation, CompleteSettingsContract.PluginSaveOperation);
        foreach (var unavailable in new[] { "Unknown command: configuration.settings.describe", "Unknown configuration operation." })
        {
            session = new((_, _, _) => Task.FromResult(Fail(unavailable)), () => true);
            Check(!(await session.DescribeCompleteSettingsAsync(default)).CompleteAvailable, "Exact older-owner capability absence preserves portable choice.");
        }
        session = new((_, _, _) => Task.FromResult(Fail("Unknown configuration operation. other failure")), () => true);
        await Reject<InvalidOperationException>(() => session.DescribeCompleteSettingsAsync(default), "A broader failure became an unsupported fallback.");
        var capabilityJson = CerebralJson.Element(completeCapabilities);
        var missingCapabilityField = FixtureMembers(capabilityJson);
        if (!missingCapabilityField.Remove("pluginEnvelopeVersion")) throw new InvalidOperationException("Fixture lacks its required source member.");
        session = new((_, _, _) => Task.FromResult(Ok(missingCapabilityField)), () => true);
        Check(!(await session.DescribeCompleteSettingsAsync(default)).CompleteAvailable, "A missing required raw capability cannot be masked by a DTO default.");
        var duplicateCapabilityField = FixtureMembers(capabilityJson);
        duplicateCapabilityField.Add("PluginEnvelopeVersion", duplicateCapabilityField["pluginEnvelopeVersion"]);
        session = new((_, _, _) => Task.FromResult(Ok(duplicateCapabilityField)), () => true);
        Check(!(await session.DescribeCompleteSettingsAsync(default)).CompleteAvailable, "Ambiguous raw capability members cannot enable complete export.");
        var exportCalls = 0;
        session = new((_, _, _) => { exportCalls++; return Task.FromResult(Ok(new { })); }, () => true);
        await Reject<InvalidOperationException>(() => session.ExportAsync("preset-a", SettingsTransferPurpose.Complete, default), "Complete export bypassed capability gating.");
        Check(exportCalls == 0, "Unadvertised complete export dispatches nothing.");

        var portableExport = new ConfigurationBackupExport(fixtureDocument, []);
        var namedPortableShape = false;
        var backupPortableShape = false;
        session = new((operation, arguments, _) =>
        {
            var request = CerebralJson.Element(arguments);
            if (operation == "configuration.presets.export")
                namedPortableShape = request.EnumerateObject().Count() == 1 && request.GetProperty("id").GetString() == "preset-a";
            else if (operation == "configuration.backup.export") backupPortableShape = request.EnumerateObject().Count() == 0;
            else throw new InvalidOperationException("Unexpected export fixture operation.");
            return Task.FromResult(Ok(portableExport));
        }, () => true);
        await session.ExportAsync("preset-a", default); await session.ExportBackupAsync(SettingsTransferPurpose.Portable, default);
        Check(namedPortableShape && backupPortableShape, "Portable requests retain exact old ID-only and empty shapes.");
        var completeDocument = fixtureDocument with
        {
            SchemaVersion = CompleteSettingsContract.BackupSchemaVersion,
            SettingsTransferMode = SettingsTransferPurpose.Complete
        };
        var completeExport = new ConfigurationBackupExport(completeDocument, [])
        {
            CompleteSettingsCoverage = new(true, 1, 0, 2, "opaque-renderer-state", ["core-preferences", "plugin-settings", "layout"])
        };
        var completePurposeSent = false;
        session = new((operation, arguments, _) =>
        {
            if (operation == CompleteSettingsContract.DescribeOperation) return Task.FromResult(Ok(completeCapabilities));
            completePurposeSent = CerebralJson.Element(arguments).GetProperty("purpose").GetString() == SettingsTransferPurpose.Complete;
            return Task.FromResult(Ok(completeExport));
        }, () => true);
        Check((await session.DescribeCompleteSettingsAsync(default)).CompleteAvailable, "Exact advertised complete contract is available.");
        await session.ExportAsync("preset-a", SettingsTransferPurpose.Complete, default);
        Check(completePurposeSent, "Only explicit complete selection sends the complete purpose.");
        var incompleteExport = completeExport with { CompleteSettingsCoverage = completeExport.CompleteSettingsCoverage! with { Complete = false } };
        session = new((operation, _, _) => Task.FromResult(Ok(operation == CompleteSettingsContract.DescribeOperation ? (object)completeCapabilities : incompleteExport)), () => true);
        await session.DescribeCompleteSettingsAsync(default);
        await Reject<InvalidDataException>(() => session.ExportAsync("preset-a", SettingsTransferPurpose.Complete, default), "A partial complete envelope was accepted.");
        var missingCoverage = FixtureMembers(CerebralJson.Element(completeExport));
        var coverageMembers = FixtureMembers(missingCoverage["completeSettingsCoverage"]);
        if (!coverageMembers.Remove("activePluginDocuments")) throw new InvalidOperationException("Fixture lacks its required coverage source member.");
        missingCoverage["completeSettingsCoverage"] = CerebralJson.Element(coverageMembers);
        session = new((operation, _, _) => Task.FromResult(Ok(operation == CompleteSettingsContract.DescribeOperation ? (object)completeCapabilities : missingCoverage)), () => true);
        await session.DescribeCompleteSettingsAsync(default);
        await Reject<InvalidDataException>(() => session.ExportAsync("preset-a", SettingsTransferPurpose.Complete, default), "Missing complete coverage was masked by constructor defaults.");
        current = true;
        session = new((_, _, _) => { current = false; return Task.FromResult(Ok(completeCapabilities)); }, () => current);
        await Reject<InvalidOperationException>(() => session.DescribeCompleteSettingsAsync(default), "Changed-owner capability was adopted.");
        current = true;
        Check(SettingsExportReview.Purpose(fixtureDocument) == SettingsTransferPurpose.Portable &&
            SettingsExportReview.Purpose(completeDocument) == SettingsTransferPurpose.Complete, "Labels derive only from document schema and purpose.");
        await Reject<InvalidDataException>(() => Task.FromResult(SettingsExportReview.Purpose(completeDocument with { SettingsTransferMode = null })), "Schema15 without its purpose was labeled complete.");
        await Reject<InvalidDataException>(() => Task.FromResult(SettingsExportReview.Purpose(fixtureDocument with { SettingsTransferMode = SettingsTransferPurpose.Complete })), "A legacy document could assert complete purpose.");
        Check(SettingsExportReview.Description(completeExport).Contains("Credential or domain exclusions: 2", StringComparison.Ordinal),
            "Complete coverage explains credential/domain exclusions separately.");
        var review = PresetCoreReviewText(fixtureDocument);
        Check(!review.Contains("\"host\"", StringComparison.Ordinal) && review.Contains(@"Models: C:\fixture\models", StringComparison.Ordinal),
            "Core review uses readable labeled text instead of JSON.");


        Check(!review.Contains(marker, StringComparison.Ordinal), "Generic review hides agent/global/appearance package bodies.");
        Check(review.Contains("Public instruction fixture.", StringComparison.Ordinal) && review.Contains("fixture.read", StringComparison.Ordinal),
            "Existing public instructions and grants remain reviewable.");

        Directory.CreateDirectory(captureDirectory);
        using (var unavailableChoice = new SettingsDialogOwner(CreateSettingsExportPurposeDialog(new(false, "Complete settings unavailable."), null, out var disabledComplete)))
        {
            var choices = ((StackPanel)((ScrollViewer)unavailableChoice.Window.Content).Content).Children.OfType<RadioButton>().ToArray();
            Check(choices.Single(x => Equals(x.Content, "Portable preset")).IsChecked == true && !disabledComplete.IsEnabled,
                "Native older-owner chooser preserves Portable and disables Complete.");
        }
        using (var availableChoice = new SettingsDialogOwner(CreateSettingsExportPurposeDialog(new(true), null, out var enabledComplete)))
        {
            var choices = ((StackPanel)((ScrollViewer)availableChoice.Window.Content).Content).Children.OfType<RadioButton>().ToArray();
            Check(choices.Single(x => Equals(x.Content, "Portable preset")).IsChecked == true && enabledComplete.IsEnabled && enabledComplete.IsChecked != true,
                "Native supported chooser still starts Portable without a persisted mode.");
            SaveOffscreenSettingsCapture((FrameworkElement)availableChoice.Window.Content, new Size(590, 350),
                Path.Combine(captureDirectory, "settings-export-choice.png"));
        }
        using (var completeReview = new SettingsDialogOwner(CreateSettingsPresetReviewDialog("Review complete settings export", [], SettingsExportReview.Description(completeExport),
            "Save export…", PresetCoreReviewText(completeDocument), null)))
        {
            var root = (DockPanel)completeReview.Window.Content;
            var footer = root.Children.OfType<StackPanel>().Single();
            var content = (StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
            var reviewed = footer.Children.OfType<CheckBox>().Single();
            var submit = footer.Children.OfType<Panel>().SelectMany(panel => panel.Children.OfType<Button>()).Single(button => Equals(button.Content, "Save export…"));
            Check(!submit.IsEnabled, "Native complete review requires explicit scope/text acknowledgement.");
            reviewed.IsChecked = true;
            Check(submit.IsEnabled, "Native review acknowledgement enables its existing save action.");
            var included = content.Children.OfType<Expander>().Single(); included.IsExpanded = true;
            Check(included.Content is TextBox { Text: var includedText } && includedText.Contains("Public instruction fixture.", StringComparison.Ordinal) &&
                !includedText.Contains(marker, StringComparison.Ordinal), "Native review displays included public text while keeping package bodies opaque.");
            SaveOffscreenSettingsCapture((FrameworkElement)completeReview.Window.Content, new Size(660, 440),
                Path.Combine(captureDirectory, "settings-complete-review.png"));
            var actionBounds = submit.TransformToAncestor(root).TransformBounds(new Rect(new Point(), submit.RenderSize));
            Check(actionBounds.Top >= 0 && actionBounds.Bottom <= root.ActualHeight && DockPanel.GetDock(footer) == Dock.Bottom,
                "Expanded native review keeps acknowledgement and save actions inside the visible footer.");
        }

        var tabWrites = 0;
        var fixtureTabs = new TabControl();
        AutomationProperties.SetAutomationId(fixtureTabs, "FixtureStableMount");
        var first = new TabItem { Header = "First" }; AutomationProperties.SetAutomationId(first, "first");
        var second = new TabItem { Header = "Second" }; AutomationProperties.SetAutomationId(second, "second");
        fixtureTabs.Items.Add(first); fixtureTabs.Items.Add(second);
        using (var tabState = new ConfigurationTabPreferences(fixtureTabs, _ => "second", (_, _) => tabWrites++, _ => { }))
        {
            tabState.Restore();
            Check(ReferenceEquals(fixtureTabs.SelectedItem, second), "Stable tab ID is restored.");
            tabState.HydrateSelection(null); tabState.Flush();
            Check(ReferenceEquals(fixtureTabs.SelectedItem, first) && tabWrites == 0, "Explicit reset selects native default without a write.");
            tabState.HydrateSelection("missing-plugin-tab");
            Check(tabState.DesiredSelection == "missing-plugin-tab", "Missing plugin tab identity is retained.");
        }

        typeof(ConnectedWindow).GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
        settingsPresetCapabilities = Capabilities();
        settingsPresetClient = new((_, _, _) => Task.FromResult(Ok(new { })), () => true);
        settingsPresetPicker.ItemsSource = new[] { Summary() }; settingsPresetPicker.SelectedIndex = 0;
        UpdateSettingsPresetDetails();
        Check(settingsPresetDetails.Text.Contains("Settings awaiting compatible plugin support: 2", StringComparison.Ordinal), "Native details show pending count.");
        Check(settingsPresetDetails.Text.Contains("Export leaves out 1", StringComparison.Ordinal), "Native details disclose portability omission.");
        Check(settingsPresetActions.Count == 4 && settingsPresetActions.All(x => x.IsEnabled), "Native actions use known capability and selection.");
        var backup = Tabs.Items.OfType<TabItem>().Select(x => x.Content).OfType<TabControl>()
            .SelectMany(x => x.Items.OfType<TabItem>()).Single(x => AutomationProperties.GetAutomationId(x) == "ConfigurationTabBackup");
        Check(settingsNavigation.SelectPage(backup), "Stable Backup page routes to native presets.");
        SetStatus("Fixture preview · no live Worker or settings writes");
        if (Content is Panel previewPanel) previewPanel.Background = Background;
        Directory.CreateDirectory(captureDirectory);
        foreach (var size in new[] { new Size(1024, 768), new Size(1360, 900) })
        {
            var view = (FrameworkElement)Content;
            SaveOffscreenSettingsCapture(view, size, Path.Combine(captureDirectory, $"settings-presets-{(int)size.Width}.png"));
            Check(settingsPresetPicker.ActualWidth >= 260 && settingsPresetPicker.ActualHeight > 0, "Native preset selector remains usable at " + size.Width);
        }
        Console.WriteLine($"Settings presets: {count} deterministic fake-RPC/native offscreen checks passed. No Worker, provider, Source or user preference writes.");
    }
    private sealed class SettingsDialogOwner(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose() => Window.Close();
    }

    private static void SaveOffscreenSettingsCapture(FrameworkElement view, Size size, string path)
    {
        if (view is Control control) control.Background = Ui.Surface;
        else if (view is Panel panel) panel.Background = Ui.Surface;
        view.Measure(size); view.Arrange(new Rect(new Point(), size)); view.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }

}
