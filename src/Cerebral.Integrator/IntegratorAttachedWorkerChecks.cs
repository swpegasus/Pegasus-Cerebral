using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

// Opt-in deterministic owner fixture. Never starts a Worker executable or contacts installed/Source state.
internal static class IntegratorAttachedWorkerChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-config-attach-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        var desktop = Path.Combine(root, "configuration");
        Directory.CreateDirectory(worker);
        var normalConnection = Path.Combine(InstallationPaths.CurrentUserDataDirectory, "connection.json");
        var before = Fingerprint(normalConnection);
        var checks = 0;
        var exit = 1;
        Application? app = null;
        IntegratorWindow? window = null;
        var fixture = new OwnedConfigurationPipe(worker);
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("Attached Configuration: " + label);
            checks++;
            Console.WriteLine("PASS attached Configuration: " + label);
        }
        try
        {
            var acceptedPreview = new CommandRequest(ModelCapacityProtocol.PreviewOperation,
                CerebralJson.Element(new ModelCapacityPreviewRequest(ModelCapacityProtocol.Version, "fixture-model")));
            Check(OwnedConfigurationPipe.AcceptsUnavailableCapacityPreview(acceptedPreview) &&
                !OwnedConfigurationPipe.AcceptsUnavailableCapacityPreview(new(ModelCapacityProtocol.PreviewOperation,
                    CerebralJson.Element(new { version = ModelCapacityProtocol.Version, modelId = "fixture-model", choice = new { contextMode = "fixed" } }))) &&
                !OwnedConfigurationPipe.AcceptsUnavailableCapacityPreview(new(ModelCapacityProtocol.PreviewOperation,
                    CerebralJson.Element(new { version = ModelCapacityProtocol.Version, modelId = "other-model" }))) &&
                !OwnedConfigurationPipe.AcceptsUnavailableCapacityPreview(new(ModelCapacityProtocol.ApplyOperation, acceptedPreview.Arguments)),
                "fixture capacity admission accepts only this model's read-only default preview and refuses choices, other models and apply");
            string[] args = ["--attach-existing", fixture.Endpoint.PipeName, fixture.Endpoint.InstanceId,
                worker, desktop, fixture.InstallationId];
            var entry = typeof(Program).GetMethod("RunWpf", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var flag in new[] { "--source-local", "--check-service-ui", "--inspect-desktop-instance" })
            {
                var refused = false;
                string[] mixed = [.. args, flag];
                try { entry.Invoke(null, [mixed]); }
                catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { refused = true; }
                Check(refused && Application.Current is null && !Directory.Exists(desktop) && fixture.Commands.IsEmpty,
                    "mixed " + flag + " is refused by actual startup before paths, UI or transport");
            }
            var options = ExistingWorkerDesktopOptions.FromArguments(args)!;
            Check(options.GetApplicationId("Integrator") != options.GetApplicationId("Orchestrator"),
                "Configuration and Chat retain separate single-instance application identities");
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = IntegratorAttachedWorkerStartup.CreateWindow(options);
            Ui.ApplyTheme(app);
            app.MainWindow = window;
            Check(window.ExistingWorkerAttachment is { } binding && binding.Endpoint == fixture.Endpoint &&
                binding.WorkerDataDirectory == worker && binding.InstallationId == fixture.InstallationId,
                "factory retains exact immutable Worker binding before the window is shown");
            Check(!File.Exists(ConnectionStore.FilePath) &&
                ScopedFilePolicy.IsWithin(ConnectionStore.FilePath, desktop) &&
                ScopedFilePolicy.IsWithin(DesktopInstancePaths.Current.PluginDataDirectory, desktop) &&
                !DesktopInstancePaths.Current.SourceLocal,
                "all desktop stores select private non-Source roots without saving a connection");
            var retained = window;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                try
                {
                    await retained.VerifyAttachedRecoveryGuardsAsync(worker, desktop, Check);
                    var rebindRefused = false;
                    try { retained.UseExistingLocalWorker(fixture.Endpoint with { InstanceId = Guid.NewGuid().ToString("N") }, worker, fixture.InstallationId); }
                    catch (InvalidOperationException) { rebindRefused = true; }
                    Check(rebindRefused, "fixed Worker binding rejects replacement before startup");
                    await fixture.StartAsync(deadline.Token);
                    retained.ShowActivated = false;
                    retained.Show();
                    var ready = await retained.AttachedStartupOutcome.WaitAsync(deadline.Token);
                    Check(ready.Connected && retained.ServiceConnected &&
                        retained.IsVisible && new WindowInteropHelper(retained).Handle != IntPtr.Zero,
                        "normal Configuration startup completes on a real owned pipe and shown HWND");
                    retained.VerifyAttachedAvailabilityControls(Check);
                    await using var observation = retained.StartReceiverObservation(options)
                        ?? throw new InvalidOperationException("Attached Configuration observation did not start.");
                    async Task<ReceiverResponse> PageCall(string operation, ReceiverPageCommand? page = null,
                        ReceiverExpected? expected = null)
                    {
                        using var pipe = new NamedPipeClientStream(".", observation.Descriptor.PipeName,
                            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.ConnectAsync(deadline.Token);
                        await LocalWorkerProtocol.WriteAsync(pipe, new ReceiverRequest(
                            ConfigurationReadbackProtocol.Product, ConfigurationReadbackProtocol.Version,
                            observation.Descriptor.InstanceId, Guid.NewGuid().ToString("N"), operation,
                            expected ?? observation.Descriptor.Expected, page), deadline.Token);
                        return await LocalWorkerProtocol.ReadAsync<ReceiverResponse>(pipe, deadline.Token);
                    }
                    var legacy = await PageCall("snapshot.inspect");
                    var legacyJson = JsonSerializer.Serialize(legacy, CerebralJson.Options);
                    Check(legacy.State == "completed" && legacy.Data is not null && legacy.PageData is null &&
                        legacy.Capabilities is null && !legacyJson.Contains("pageData", StringComparison.Ordinal) &&
                        !legacyJson.Contains("capabilities", StringComparison.Ordinal),
                        "legacy snapshot response keeps its exact closed shape");
                    var capabilities = await PageCall("capabilities.inspect");
                    var offered = capabilities.Capabilities;
                    var expectedCapabilities = ConfigurationPageControl.Capabilities;
                    var validCapabilities = capabilities.State == "completed" && capabilities.ErrorCode is null &&
                        capabilities.Data is null && capabilities.PageData is null &&
                        capabilities.PluginOwnerData is null && capabilities.PluginRuntimeData is null &&
                        offered?.Schema == "pegasus.configuration-page-capabilities/2" &&
                        offered.Operations.SequenceEqual(expectedCapabilities.Operations) &&
                        offered.PageTargets.SequenceEqual(expectedCapabilities.PageTargets) &&
                        offered.ExactPluginPanelSelection && offered.ExactPluginGeneration;
                    static string BoundedCapabilityText(string? value) => value is null ? "null" :
                        value.Length <= 80 ? value : value[..80] + "…";
                    var operationsDiagnostic = offered?.Operations is { } operations
                        ? string.Join(",", operations.Take(12).Select(BoundedCapabilityText)) + (operations.Length > 12 ? ",…" : "")
                        : "null";
                    Check(validCapabilities,
                        validCapabilities ? "optional page capabilities are advertised separately" :
                        $"optional page capabilities are advertised separately (state={BoundedCapabilityText(capabilities.State)}, schema={BoundedCapabilityText(offered?.Schema)}, error={BoundedCapabilityText(capabilities.ErrorCode)}, operations={operationsDiagnostic})");
                    var pageBefore = (await PageCall("page.inspect")).PageData
                        ?? throw new InvalidOperationException("Page inspection returned no page state.");
                    Check(pageBefore.State == "ready" && pageBefore.PageId is not null &&
                        pageBefore.Loaded && pageBefore.Visible && pageBefore.Revision > 0,
                        $"shown attached Configuration reports its actual selected page without text (state={pageBefore.State}, page={pageBefore.PageId ?? "none"}, loaded={pageBefore.Loaded}, visible={pageBefore.Visible}, revision={pageBefore.Revision})");
                    var refusedIdentity = await PageCall("page.open", new("ConfigurationTabSettings", null,
                        pageBefore.PageId!, pageBefore.Revision),
                        observation.Descriptor.Expected with { ProcessId = observation.Descriptor.Expected.ProcessId + 1 });
                    Check(refusedIdentity.State == "rejected" && refusedIdentity.ErrorCode == "identity_mismatch",
                        "page navigation refuses a different process identity");
                    var refusedRevision = await PageCall("page.open", new("ConfigurationTabSettings", null,
                        pageBefore.PageId!, pageBefore.Revision + 1));
                    Check(refusedRevision.State == "rejected" && refusedRevision.ErrorCode == "page_changed",
                        "page navigation refuses a stale or forged revision before selecting a page");
                    var unsupported = await PageCall("interface.review");
                    Check(unsupported.State == "rejected" && unsupported.ErrorCode == "operation_not_supported",
                        "Configuration page channel does not admit owner review operations");
                    var extra = JsonSerializer.Deserialize<ReceiverPageCommand>(
                        "{\"targetPageId\":\"ConfigurationTabSettings\",\"pluginId\":null,\"expectedPageId\":\"" +
                        pageBefore.PageId + "\",\"expectedRevision\":" + pageBefore.Revision + ",\"extra\":true}",
                        CerebralJson.Options)!;
                    var refusedExtra = await PageCall("page.open", extra);
                    Check(refusedExtra.State == "rejected" && refusedExtra.ErrorCode == "page_arguments_invalid",
                        "unknown page-command fields cannot expand navigation authority");
                    var selected = pageBefore;
                    foreach (var target in ConfigurationPageControl.PageTargets)
                    {
                        var opened = await PageCall("page.open", new(target, null, selected.PageId!, selected.Revision));
                        var openingPage = opened.PageData;
                        // Selection is synchronous, but WPF may load the selected content after this reply.
                        // An accepted reply must still name the exact target; inspection proves readiness.
                        Check((opened.State is "accepted" or "completed") && opened.ErrorCode is null &&
                            openingPage is not null && openingPage.PageId == target &&
                            openingPage.Revision >= selected.Revision &&
                            (opened.State != "completed" || (openingPage.Loaded && openingPage.Visible)),
                            $"normal Settings rail selects {target} " +
                            $"(reply={BoundedCapabilityText(opened.State)}, error={BoundedCapabilityText(opened.ErrorCode)}, " +
                            $"pageState={BoundedCapabilityText(openingPage?.State)}, page={BoundedCapabilityText(openingPage?.PageId)}, " +
                            $"loaded={openingPage?.Loaded}, visible={openingPage?.Visible}, " +
                            $"revision={openingPage?.Revision}, expectedPage={BoundedCapabilityText(selected.PageId)}, expectedRevision={selected.Revision})");
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var inspected = await PageCall("page.inspect");
                        selected = inspected.PageData
                            ?? throw new InvalidOperationException("Page inspection returned no selected page.");
                        Check(inspected.State == "completed" && inspected.ErrorCode is null &&
                            selected.PageId == target && selected.Loaded && selected.Visible &&
                            selected.Revision >= openingPage!.Revision,
                            $"selected page is actually loaded and visible: {target} " +
                            $"(reply={BoundedCapabilityText(inspected.State)}, error={BoundedCapabilityText(inspected.ErrorCode)}, " +
                            $"pageState={BoundedCapabilityText(selected.State)}, page={BoundedCapabilityText(selected.PageId)}, " +
                            $"loaded={selected.Loaded}, visible={selected.Visible}, revision={selected.Revision}, " +
                            $"openingRevision={openingPage!.Revision})");
                    }
                    var incompletePanel = await PageCall("plugin.panel.open", new(null, "pegasus.missing-fixture-plugin",
                        selected.PageId!, selected.Revision));
                    Check(incompletePanel.State == "rejected" && incompletePanel.ErrorCode == "page_arguments_invalid",
                        "v2 plugin-panel command refuses missing exact panel and generation fields");
                    var missingPanel = await PageCall("plugin.panel.open", new(null, "pegasus.missing-fixture-plugin",
                        selected.PageId!, selected.Revision, PanelId: "fixture-settings",
                        ExpectedPluginDirectory: Path.Combine(desktop, "Plugins", "Desktop", "missing-fixture-generation"),
                        ExpectedPluginVersion: "fixture-v1"));
                    Check(missingPanel.State == "rejected" && missingPanel.ErrorCode == "plugin_panel_not_ready" &&
                        (await PageCall("page.inspect")).PageData?.PluginPanelReady == false,
                        "missing native plugin panel is refused without claiming a mounted view");
                    await UntilAsync(() => fixture.Subscriptions > 0, deadline.Token);
                    retained.VerifyAttachedComputeProjection("Fixture model", Check);
                    fixture.ModelName = "Event-refreshed model";
                    fixture.Publish("model.updated");
                    await UntilAsync(() => retained.AttachedModelName == fixture.ModelName, deadline.Token);
                    Check(retained.ServiceConnected, "normal event watch refreshes the actual model projection");
                    await UntilAsync(() => retained.SetupInventoryTextForChecks.Contains("Models: 1 registered", StringComparison.Ordinal), deadline.Token);
                    Check(retained.SetupInventoryTextForChecks.StartsWith("Setup inventory", StringComparison.Ordinal) &&
                        !fixture.Commands.Any(x => x.Operation == "models.scan"),
                        "model event refreshes first-run inventory without a repository scan");
                    Check((await PageCall("page.inspect")).PageData?.PageId == selected.PageId,
                        "first-run inventory event refresh preserves the selected Configuration page");
                    var publicationPath = Path.Combine(desktop, "Plugins", "Desktop", ".settings", ".catalog-publication.lock");
                    Directory.CreateDirectory(Path.GetDirectoryName(publicationPath)!);
                    var modelReadsBeforeContention = fixture.Commands.Count(x => x.Operation == "models.list");
                    using (var publication = new FileStream(publicationPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        fixture.Publish("model.updated");
                        await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "models.list") >= modelReadsBeforeContention + 2,
                            deadline.Token);
                        await Task.Delay(100, deadline.Token);
                        Check(retained.ServiceConnected && fixture.Errors.IsEmpty,
                            "plugin publication contention cannot turn an inventory event into Worker-feed failure");
                    }
                    fixture.PauseModelListReadAfter(2);
                    fixture.Publish("model.updated");
                    await fixture.ModelListPaused.WaitAsync(deadline.Token);
                    await retained.RetryAttachedConnectionAsync();
                    var replacementInventory = retained.SetupInventoryTextForChecks;
                    fixture.ResumeModelListRead();
                    await fixture.ModelListReplySettled.WaitAsync(deadline.Token);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(100, deadline.Token);
                    Check(retained.ServiceConnected && retained.SetupInventoryTextForChecks == replacementInventory &&
                        replacementInventory == "Cerebral is configured. Helpers and model resources are managed in Agents and Models.",
                        "same-endpoint client replacement rejects the obsolete inventory reply and header commit");
                    retained.SelectFixtureComputeRows();
                    var beforeOwnerReplacement = retained.FixtureComputeActionState();
                    Check(beforeOwnerReplacement.ModelsEnabled && beforeOwnerReplacement.AgentEditEnabled &&
                        beforeOwnerReplacement.ModelName == fixture.ModelName && beforeOwnerReplacement.AgentName == fixture.AgentName,
                        "current owner adoption enables selected model and agent actions");
                    fixture.PauseNextSettingsRead();
                    var delayedHydration = retained.RetryAttachedConnectionAsync();
                    try
                    {
                        await fixture.SettingsReadPaused.WaitAsync(deadline.Token);
                        var duringHydration = retained.FixtureComputeActionState();
                        Check(retained.ServiceConnected && duringHydration.ModelsDisabled && !duringHydration.AgentEditEnabled &&
                            duringHydration.ModelName is null && duringHydration.AgentName is null,
                            "same-endpoint client replacement clears old model and agent rows before delayed hydration");
                    }
                    finally { fixture.ResumeSettingsRead(); }
                    await delayedHydration.WaitAsync(deadline.Token);
                    retained.SelectFixtureComputeRows();
                    var afterHydration = retained.FixtureComputeActionState();
                    Check(afterHydration.ModelsEnabled && afterHydration.AgentEditEnabled &&
                        afterHydration.ModelName == fixture.ModelName && afterHydration.AgentName == fixture.AgentName,
                        "current-owner model and agent adoption restores selected actions after hydration");
                    var oldReads = retained.BeginDeferredComputeReadsForChecks();
                    try
                    {
                        fixture.ModelName = "Current-owner model";
                        fixture.AgentName = "Current-owner lead";
                        await retained.RetryAttachedConnectionAsync();
                        retained.SelectFixtureComputeRows();
                        var beforeLateReplies = retained.FixtureComputeActionState();
                        Check(beforeLateReplies.ModelsEnabled && beforeLateReplies.AgentEditEnabled &&
                            beforeLateReplies.ModelName == fixture.ModelName && beforeLateReplies.AgentName == fixture.AgentName,
                            "new owner adopts its own model and agent rows while old reads remain deferred");
                    }
                    finally { oldReads.Release(); }
                    await Task.WhenAll(oldReads.ModelRead, oldReads.AgentRead).WaitAsync(deadline.Token);
                    var afterLateReplies = retained.FixtureComputeActionState();
                    Check(afterLateReplies.ModelsEnabled && afterLateReplies.AgentEditEnabled &&
                        afterLateReplies.ModelName == fixture.ModelName && afterLateReplies.AgentName == fixture.AgentName,
                        "late prior-client model and agent reads cannot replace current rows or action ownership");
                    foreach (var fail in new[] { false, true })
                    {
                        var setupAttempts = 0;
                        var oldAcknowledgement = retained.BeginDeferredAgentAcknowledgementForChecks(() =>
                        {
                            setupAttempts++;
                            return Task.CompletedTask;
                        });
                        var currentStatus = "";
                        try
                        {
                            await retained.RetryAttachedConnectionAsync();
                            retained.SelectFixtureComputeRows();
                            currentStatus = retained.ServiceStatusText;
                        }
                        finally { oldAcknowledgement.Release(fail); }
                        await oldAcknowledgement.Completion.WaitAsync(deadline.Token);
                        var afterAcknowledgement = retained.FixtureComputeActionState();
                        Check(setupAttempts == 0 && retained.ServiceStatusText == currentStatus &&
                            afterAcknowledgement.ModelsEnabled && afterAcknowledgement.AgentEditEnabled &&
                            afterAcknowledgement.ModelName == fixture.ModelName && afterAcknowledgement.AgentName == fixture.AgentName,
                            fail
                                ? "same-endpoint replacement suppresses stale acknowledged-agent refresh failure and follow-up setup"
                                : "same-endpoint replacement suppresses stale acknowledged-agent refresh success and follow-up setup");
                    }
                    var appearanceWritesBefore = fixture.Commands.Count(x => x.Operation == "interface.preferences.set");
                    bool LatestAppearanceSaveIs(string theme)
                    {
                        var command = fixture.Commands.Last(x => x.Operation == "interface.preferences.set");
                        var value = command.Arguments.GetProperty("value");
                        return command.Arguments.GetProperty("name").GetString() == InterfacePreferencePolicy.AppearanceTheme &&
                            value.ValueKind == JsonValueKind.String && value.GetString() == theme;
                    }
                    retained.SelectFixtureAppearanceTheme(AppearanceThemePolicy.Dark);
                    var darkAppliedImmediately = retained.FixtureAppearanceThemeMatches(AppearanceThemePolicy.Dark);
                    await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "interface.preferences.set") == appearanceWritesBefore + 1,
                        deadline.Token);
                    Check(darkAppliedImmediately && LatestAppearanceSaveIs(AppearanceThemePolicy.Dark),
                        "connected Dark selection immediately applies the shared desktop palette and dispatches the exact preference");
                    retained.SelectFixtureAppearanceTheme(AppearanceThemePolicy.Light);
                    var lightAppliedImmediately = retained.FixtureAppearanceThemeMatches(AppearanceThemePolicy.Light);
                    await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "interface.preferences.set") == appearanceWritesBefore + 2,
                        deadline.Token);
                    Check(lightAppliedImmediately && LatestAppearanceSaveIs(AppearanceThemePolicy.Light),
                        "connected Light selection immediately updates the shared desktop palette and dispatches the exact preference");
                    retained.SelectFixtureAppearanceTheme(AppearanceThemePolicy.System);
                    await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "interface.preferences.set") == appearanceWritesBefore + 3,
                        deadline.Token);
                    Check(LatestAppearanceSaveIs(AppearanceThemePolicy.System),
                        "connected appearance fixture restores and dispatches System before the recent-chat checks");
                    var preferenceWritesBefore = fixture.Commands.Count(x => x.Operation == "interface.preferences.set");
                    retained.EnterFixtureRecentChatLimit("0");
                    Check(retained.ServiceStatusText.Contains("between 1 and 200", StringComparison.Ordinal) &&
                        fixture.Commands.Count(x => x.Operation == "interface.preferences.set") == preferenceWritesBefore,
                        "actual recent-chat field handler refuses invalid zero before fake Worker dispatch");
                    retained.EnterFixtureRecentChatLimit("20");
                    await UntilAsync(() => retained.ServiceStatusText == "Recent chats shown preference saved.", deadline.Token);
                    Check(fixture.Commands.Count(x => x.Operation == "interface.preferences.set") == preferenceWritesBefore + 1,
                        "actual valid field handler dispatches one acknowledged exact-20 save and clears its own validation footer");
                    await retained.BeginFixtureRecentPreferenceSave("Unrelated current footer").WaitAsync(deadline.Token);
                    Check(retained.ServiceStatusText == "Unrelated current footer",
                        "acknowledged exact 20 save preserves a different current footer");
                    var settingsReads = fixture.Commands.Count(x => x.Operation == "settings.get");
                    fixture.PauseNextPreferenceSave(fail: false);
                    var pendingPreferenceSave = retained.BeginFixtureRecentPreferenceSave();
                    await fixture.PreferenceSavePaused.WaitAsync(deadline.Token);
                    fixture.WrongInstallation = true;
                    var refused = false;
                    try { await retained.RetryAttachedConnectionAsync(); }
                    catch (InvalidOperationException) { refused = true; }
                    Check(refused && !retained.ServiceConnected &&
                        fixture.Commands.Count(x => x.Operation == "settings.get") == settingsReads,
                        "wrong installation is rejected before Configuration hydration");
                    var offlineStatus = retained.ServiceStatusText;
                    fixture.ResumePreferenceSave();
                    await pendingPreferenceSave.WaitAsync(deadline.Token);
                    Check(!retained.ServiceConnected && retained.ServiceStatusText == offlineStatus &&
                        !offlineStatus.Contains("preference saved", StringComparison.Ordinal),
                        "late successful preference reply from prior Client leaves newer offline status untouched");
                    var offlinePage = (await PageCall("page.inspect")).PageData;
                    Check(offlinePage?.State == "offline" && offlinePage.PageId == selected.PageId,
                        "offline page inspection reports the native page without claiming connection readiness");
                    var offlineOpen = await PageCall("page.open", new("ConfigurationTabSettings", null,
                        offlinePage!.PageId!, offlinePage.Revision));
                    Check(offlineOpen.State == "rejected" && offlineOpen.ErrorCode == "page_not_ready",
                        "offline navigation refuses before changing the selected page");
                    fixture.WrongInstallation = false;
                    await retained.RetryAttachedConnectionAsync();
                    Check(retained.ServiceConnected && retained.ExistingWorkerAttachment?.Endpoint == fixture.Endpoint,
                        "Configuration recovery reconnects only the captured Worker");
                    fixture.PauseNextPreferenceSave(fail: true);
                    var pendingFailedSave = retained.BeginFixtureRecentPreferenceSave();
                    await fixture.PreferenceSavePaused.WaitAsync(deadline.Token);
                    fixture.WrongInstallation = true;
                    var failedOwnerRefused = false;
                    try { await retained.RetryAttachedConnectionAsync(); }
                    catch (InvalidOperationException) { failedOwnerRefused = true; }
                    var failedSaveOfflineStatus = retained.ServiceStatusText;
                    fixture.ResumePreferenceSave();
                    await pendingFailedSave.WaitAsync(deadline.Token);
                    Check(failedOwnerRefused && !retained.ServiceConnected &&
                        retained.ServiceStatusText == failedSaveOfflineStatus &&
                        !failedSaveOfflineStatus.Contains("could not be saved", StringComparison.Ordinal),
                        "late failed preference reply from prior Client cannot publish over newer offline status");
                    fixture.WrongInstallation = false;
                    await retained.RetryAttachedConnectionAsync();
                    Check(retained.ServiceConnected, "Configuration reconnects after the deliberate late-failure fixture");
                    retained.VerifyAttachedComputeProjection(fixture.ModelName, Check);
                    var capacityReads = fixture.Commands.Where(x => x.Operation == ModelCapacityProtocol.PreviewOperation).ToArray();
                    Check(capacityReads.Length > 0 && capacityReads.All(OwnedConfigurationPipe.AcceptsUnavailableCapacityPreview) &&
                        !fixture.Commands.Any(x => x.Operation == ModelCapacityProtocol.ApplyOperation),
                        "selected compute rows issue only exact read-only capacity previews and no capacity apply");
                    var unexpected = fixture.Unexpected.ToArray();
                    var pipeErrors = fixture.Errors.ToArray();
                    static string BoundedEntries(string[] entries)
                    {
                        const int maxEntries = 8, maxLength = 512;
                        var shown = entries.Take(maxEntries).Select(entry => JsonSerializer.Serialize(entry.Length <= maxLength
                            ? entry : entry[..maxLength] + "… [truncated]"));
                        return "[" + string.Join(", ", shown) + (entries.Length > maxEntries
                            ? $", ... {entries.Length - maxEntries} more" : "") + "]";
                    }
                    var fixtureClean = unexpected.Length == 0 && pipeErrors.Length == 0;
                    Check(fixtureClean, fixtureClean
                        ? "fixture records no unexpected mutation, external-management request or hidden pipe failure after the deliberate fake preference reply"
                        : $"fixture recorded unexpected operations ({unexpected.Length})={BoundedEntries(unexpected)}; " +
                          $"pipe errors ({pipeErrors.Length})={BoundedEntries(pipeErrors)}");
                    Check(!File.Exists(ConnectionStore.FilePath) && Fingerprint(normalConnection) == before,
                        "startup, event refresh and recovery preserve private and normal connection declarations");
                    retained.Close();
                    Check(fixture.Unexpected.IsEmpty, "closing Configuration requests no Worker shutdown or settings mutation");
                    exit = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally
                {
                    retained.Close();
                    await fixture.DisposeAsync();
                    app.Shutdown(exit);
                }
            }));
            var result = app.Run();
            Check(Fingerprint(normalConnection) == before, "normal connection fingerprint remains unchanged after UI teardown");
            if (result != 0) return result;
            Console.WriteLine("Attached Configuration: " + checks + " checks passed; real owned pipe with deterministic replies and one shown HWND; no Worker/model/Source endpoint.");
            return 0;
        }
        finally
        {
            window?.Close();
            fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Fingerprint(normalConnection) != before) throw new InvalidOperationException("Normal connection changed.");
            var full = Path.GetFullPath(root);
            if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cerebral-config-attach-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its exact owned root.");
            ScopedFilePolicy.RejectLinks(full);
            Directory.Delete(full, recursive: true);
        }
    }

    private static string Fingerprint(string path)
    {
        if (!File.Exists(path)) return "absent";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file.Length + ":" + Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(25, token);
    }

    private sealed class OwnedConfigurationPipe(string worker) : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentDictionary<int, NamedPipeServerStream> pipes = new();
        private readonly ConcurrentDictionary<int, Channel<ServiceEvent>> events = new();
        private readonly ConcurrentBag<Task> handlers = [];
        private Task? listener;
        private int next, subscriptions, disposed;
        private long sequence;
        private int modelListReads, pauseModelListAt;
        private readonly TaskCompletionSource modelListPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resumeModelList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource modelListReplySettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource settingsReadPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource resumeSettingsRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int settingsReads, pauseSettingsAt;
        private TaskCompletionSource preferenceSavePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource resumePreferenceSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int preferenceSaveReads, pausePreferenceAt;
        private bool failPreferenceSave;
        internal LocalWorkerEndpoint Endpoint { get; } = new("cerebral-config-fixture-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        internal string InstallationId { get; } = Guid.NewGuid().ToString("N");
        internal ConcurrentQueue<CommandRequest> Commands { get; } = new();
        internal ConcurrentQueue<string> Unexpected { get; } = new();
        internal ConcurrentQueue<string> Errors { get; } = new();
        internal string ModelName { get; set; } = "Fixture model";
        internal string AgentName { get; set; } = "Fixture lead";
        internal bool WrongInstallation { get; set; }
        internal static bool AcceptsUnavailableCapacityPreview(CommandRequest command)
        {
            if (command.Operation != ModelCapacityProtocol.PreviewOperation || command.ProfileId is not null ||
                command.Arguments.ValueKind != JsonValueKind.Object) return false;
            var properties = command.Arguments.EnumerateObject().ToArray();
            if (properties.Length is < 2 or > 4 ||
                properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                properties.Any(x => x.Name is not ("version" or "modelId" or "choice" or "requiredPerSlotContextTokens")))
                return false;
            var args = command.Arguments;
            return args.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out var number) && number == ModelCapacityProtocol.Version &&
                args.TryGetProperty("modelId", out var model) && model.ValueKind == JsonValueKind.String &&
                model.GetString() == "fixture-model" &&
                (!args.TryGetProperty("choice", out var choice) || choice.ValueKind == JsonValueKind.Null) &&
                (!args.TryGetProperty("requiredPerSlotContextTokens", out var context) || context.ValueKind == JsonValueKind.Null);
        }
        internal int Subscriptions => Volatile.Read(ref subscriptions);
        internal Task ModelListPaused => modelListPaused.Task;
        internal Task ModelListReplySettled => modelListReplySettled.Task;
        internal Task SettingsReadPaused => settingsReadPaused.Task;
        internal void PauseNextSettingsRead()
        {
            settingsReadPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            resumeSettingsRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pauseSettingsAt, Volatile.Read(ref settingsReads) + 1);
        }
        internal void ResumeSettingsRead() => resumeSettingsRead.TrySetResult();
        internal Task PreferenceSavePaused => preferenceSavePaused.Task;
        internal void ResumePreferenceSave() => resumePreferenceSave.TrySetResult();
        internal void PauseNextPreferenceSave(bool fail)
        {
            Volatile.Write(ref failPreferenceSave, fail);
            preferenceSavePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            resumePreferenceSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pausePreferenceAt, Volatile.Read(ref preferenceSaveReads) + 1);
        }
        internal void PauseModelListReadAfter(int reads) => Volatile.Write(ref pauseModelListAt, Volatile.Read(ref modelListReads) + reads);
        internal void ResumeModelListRead() => resumeModelList.TrySetResult();

        internal async Task StartAsync(CancellationToken token)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener = Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var id = Interlocked.Increment(ref next);
                        var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 16,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        pipes[id] = pipe;
                        ready.TrySetResult();
                        await pipe.WaitForConnectionAsync(lifetime.Token);
                        handlers.Add(HandleAsync(id, pipe));
                    }
                }
                catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException) { }
                catch (Exception error) { ready.TrySetException(error); Errors.Enqueue(error.Message); }
            }, CancellationToken.None);
            await ready.Task.WaitAsync(token);
        }

        private async Task HandleAsync(int id, NamedPipeServerStream pipe)
        {
            var pausedModelList = false;
            try
            {
                var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, lifetime.Token);
                LocalWorkerProtocol.ValidateHello(hello, Endpoint);
                await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, Endpoint.InstanceId), lifetime.Token);
                var request = await LocalWorkerProtocol.ReadAsync<LocalWorkerRequest>(pipe, lifetime.Token);
                LocalWorkerProtocol.ValidateRequest(request);
                if (request.Kind == "events")
                {
                    await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Ok(new { subscribed = true }), lifetime.Token);
                    var channel = Channel.CreateBounded<ServiceEvent>(8);
                    events[id] = channel;
                    Interlocked.Increment(ref subscriptions);
                    await foreach (var item in channel.Reader.ReadAllAsync(lifetime.Token))
                        await LocalWorkerProtocol.WriteAsync(pipe, item, lifetime.Token);
                }
                else
                {
                    Commands.Enqueue(request.Command!);
                    if (request.Command!.Operation == "models.list" &&
                        Interlocked.Increment(ref modelListReads) == Volatile.Read(ref pauseModelListAt))
                    {
                        pausedModelList = true;
                        modelListPaused.TrySetResult();
                        await resumeModelList.Task.WaitAsync(lifetime.Token);
                    }
                    if (request.Command.Operation == "settings.get" &&
                        Interlocked.Increment(ref settingsReads) == Volatile.Read(ref pauseSettingsAt))
                    {
                        settingsReadPaused.TrySetResult();
                        await resumeSettingsRead.Task.WaitAsync(lifetime.Token);
                    }
                    if (request.Command.Operation == "interface.preferences.set")
                    {
                        var number = Interlocked.Increment(ref preferenceSaveReads);
                        if (number == Volatile.Read(ref pausePreferenceAt))
                        {
                            preferenceSavePaused.TrySetResult();
                            await resumePreferenceSave.Task.WaitAsync(lifetime.Token);
                        }
                    }
                    await LocalWorkerProtocol.WriteAsync(pipe, Reply(request.Command!), lifetime.Token);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception error) { Errors.Enqueue(error.GetType().Name + ": " + error.Message); }
            finally
            {
                if (pausedModelList) modelListReplySettled.TrySetResult();
                events.TryRemove(id, out _); pipes.TryRemove(id, out _); pipe.Dispose();
            }
        }

        private CommandResult Reply(CommandRequest command)
        {
            switch (command.Operation)
            {
                case "health": return CommandResult.Ok(new HostInfo(LocalWorkerProtocol.Product, "0.2.22", LocalWorkerProtocol.Version,
                    "Owned Configuration fixture", "fixture", "Windows", false, worker, true,
                    WrongInstallation ? Guid.NewGuid().ToString("N") : InstallationId));
                case "settings.get": return CommandResult.Ok(new HostSettings
                {
                    Name = "Fixture machine", ProjectsDirectory = Path.Combine(worker, "Projects"),
                    ModelDirectory = Path.Combine(worker, "models"), ToolsDirectory = Path.Combine(worker, "Tools"),
                    SharedDirectory = Path.Combine(worker, "Shared"), DefaultModelId = "fixture-model",
                    Workers = [new() { Id = "fixture-remote", Name = "Registered remote", Url = "http://127.0.0.1:1" }]
                });
                case "models.list": return CommandResult.Ok(new[] { new ModelRecord
                    { Id = "fixture-model", Name = ModelName, ParallelRequests = 2, ContextTokens = 16384, State = "unloaded" } });
                case ModelCapacityProtocol.PreviewOperation:
                    if (!AcceptsUnavailableCapacityPreview(command))
                    {
                        Unexpected.Enqueue("models.capacity.preview:unexpected-arguments");
                        return CommandResult.Fail("Owned Configuration fixture refuses this capacity preview request.");
                    }
                    return CommandResult.Fail("Capacity preview is unavailable in this owned Configuration fixture.");
                case "agents.list": return CommandResult.Ok(new[] { new AgentProfile
                    { Id = "fixture-agent", Name = AgentName, Role = "lead", ModelId = "fixture-model" } });
                case "jobs.workflow.active-summaries": return CommandResult.Ok(new { runs = Array.Empty<object>(), nextAfterRow = (long?)null });
                case "queue.active-summaries": return CommandResult.Ok(new { jobs = Array.Empty<object>(), nextAfterRow = (long?)null });
                case "peer.grants.list": case "plugins.list": case "skills.list": return CommandResult.Ok(Array.Empty<object>());
                case "resources": return CommandResult.Ok(new ResourceInfo(10, 4096, []));
                case "diagnostics": return CommandResult.Ok(new { servicePlugins = Array.Empty<object>(), models = Array.Empty<object>() });
                case "instructions.get": return CommandResult.Ok(new SystemInstructionDocument("fixture", "", Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()))));
                case "navigation.get": return CommandResult.Ok(new NavigationState());
                case "access.full.status": return CommandResult.Ok(new FullAccessStatus(false));
                case "interface.preferences.get": return CommandResult.Ok(new InterfacePreferences
                {
                    Revision = 1, AppearanceTheme = AppearanceThemePolicy.System,
                    MessageTimeFormat = MessageTimeFormatPolicy.TwelveHour, RecentChatLimit = 20
                });
                case "interface.theme.get": case "configuration.presets.describe":
                    return CommandResult.Fail("Unknown command: " + command.Operation);
                case "interface.preferences.set":
                    var preferenceName = command.Arguments.GetProperty("name").GetString();
                    var preferenceValue = command.Arguments.GetProperty("value");
                    if (preferenceName == InterfacePreferencePolicy.AppearanceTheme &&
                        preferenceValue.ValueKind == JsonValueKind.String &&
                        preferenceValue.GetString() is { } theme && AppearanceThemePolicy.Choices.Contains(theme))
                        return CommandResult.Ok(new InterfacePreferences { Revision = 2, AppearanceTheme = theme });
                    if (preferenceName != InterfacePreferencePolicy.RecentChatLimit ||
                        preferenceValue.ValueKind != JsonValueKind.Number || preferenceValue.GetInt32() != 20)
                    {
                        Unexpected.Enqueue("interface.preferences.set:unexpected-value");
                        return CommandResult.Fail("Fixture accepts only built-in appearance choices or recentChatLimit 20.");
                    }
                    return Volatile.Read(ref failPreferenceSave)
                        ? CommandResult.Fail("Fixture preference save rejected.")
                        : CommandResult.Ok(new InterfacePreferences { Revision = 2, RecentChatLimit = 20 });
                default:
                    Unexpected.Enqueue(command.Operation);
                    return CommandResult.Fail("Owned Configuration fixture refuses: " + command.Operation);
            }
        }

        internal void Publish(string type)
        {
            var item = new ServiceEvent(Interlocked.Increment(ref sequence), type, null, CerebralJson.Element(new { }), DateTimeOffset.UtcNow);
            foreach (var channel in events.Values) channel.Writer.TryWrite(item);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            foreach (var channel in events.Values) channel.Writer.TryComplete();
            foreach (var pipe in pipes.Values) pipe.Dispose();
            if (listener is not null) await listener.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(handlers.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Dispose();
        }
    }
}

public sealed partial class IntegratorWindow
{
    internal Task<DesktopStartupOutcome> AttachedStartupOutcome => InitialConnectionSettled;
    internal Task RetryAttachedConnectionAsync() => RetryLocalConnectionAsync();
    internal string? AttachedModelName => configuredModels.FirstOrDefault()?.Name;

    internal async Task VerifyAttachedRecoveryGuardsAsync(string worker, string desktop, Action<bool, string> check)
    {
        check(LocalRecoveryDataDirectory() == worker && localRecoverySummary.Text.Contains(worker, StringComparison.Ordinal),
            "offline recovery reads the immutable Worker root without a saved declaration");
        check(ScopedFilePolicy.IsWithin(desktopCatalog.RootDirectory, desktop) &&
            ScopedFilePolicy.IsWithin(desktopPluginDataRoot, desktop) && !seedBundledDesktopPlugins,
            "private plugin catalog/data are explicit and bundled seeding is disabled");
        // Exercise admission itself without invoking lifecycle implementations, even on a failed test.
        // A missing guard must never let this fixture reach an installed service or an unqualified child app.
        var rejected = false;
        try { RequireWorkerManagementAllowed("manage a Worker"); }
        catch (InvalidOperationException error) when (error.Message.Contains("fixed Worker binding", StringComparison.Ordinal)) { rejected = true; }
        check(rejected, "attached management admission refuses lifecycle and retarget authority");
        var normal = new IntegratorWindow(false, desktopPluginCatalog: new(Path.Combine(desktop, "normal-guard-catalog")),
            desktopPluginDataDirectory: Path.Combine(desktop, "normal-guard-data"), seedBundledDesktopPlugins: false);
        try
        {
            normal.RequireWorkerManagementAllowed("manage a Worker");
            check(normal.ExistingWorkerAttachment is null, "ordinary Configuration retains its management admission");
        }
        finally { normal.Close(); }
        await BeforeInitialConnectAsync(new(null, "invalid", false, "must not discover"));
        check(ExistingWorkerAttachment is not null && !File.Exists(ConnectionStore.FilePath),
            "direct preparation override cannot discover, enroll or save another connection");
    }

    internal void VerifyAttachedComputeProjection(string expected, Action<bool, string> check)
    {
        check(configuredModels.Single().Name == expected && agents.Items.Count == 1,
            "normal hydration adopts real-pipe model/profile replies");
        check(computeMachines.Items.OfType<ComputeMachineRow>().Single(x => x.Id == "fixture-remote").Availability.Contains("unobserved", StringComparison.Ordinal),
            "registered remote machine remains unobserved without remote capability");
        var row = computeModels.Items.OfType<ComputeModelRow>().Single();
        check(row.ConfiguredSlots == "2" && row.MeasuredMaximum == "Not measured" &&
            row.WorkingContexts == "Not observed" && row.ObservedSlots == "Not observed",
            "configured two slots do not manufacture observed allocation, working contexts or measured capacity");
    }
}
