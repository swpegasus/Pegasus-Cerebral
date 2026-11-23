using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private async Task<int> CheckAgentModelEditorAsync(CancellationToken token)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        // Establish a hidden native owner handle for modal ownership without showing or activating a window.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var local = new ModelRecord { Id = "local-model", Name = "Local text model" };
        var a = new ModelRecord { Id = "a-model", Name = "Remote A model" };
        var b = new ModelRecord { Id = "b-model", Name = "Remote B model" };
        var profile = new AgentProfile { ModelId = local.Id, ModelWorkerId = "local", WorkerId = "execution-owner", FollowsDefaultModel = false };
        var choices = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["Local"] = "local", ["Machine A"] = "a", ["Machine B"] = "b", ["Unavailable machine"] = "fail" };
        var form = new FieldsDialog(this, "Agent routing fixture")
            .Tab("general", "General").Add("draft", "Draft", "café\nsecond line", true)
            .Tab("model", "Model & routing")
            .Choice("modelMode", "Model selection", [AgentModelBindingEditor.FollowDefault, AgentModelBindingEditor.PinnedModel], AgentModelBindingEditor.PinnedModel)
            .Choice("modelWorker", "Model inference machine", choices.Keys, "Local")
            .Choice("model", "Model", [AgentModelBindingEditor.ChooseModel], AgentModelBindingEditor.ChooseModel)
            .Add("routing", "Effective route", "", true).SetReadOnly("routing")
            .Tab("context", "Context").Add("budget", "Draft budget", "3072");
        var pendingA = new TaskCompletionSource<List<ModelRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingB = new TaskCompletionSource<List<ModelRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        long epoch = 7;
        Task<List<ModelRecord>> Read(string id) => id switch
        {
            "local" => Task.FromResult(new List<ModelRecord> { local }),
            "a" => pendingA.Task,
            "b" => pendingB.Task,
            _ => Task.FromException<List<ModelRecord>>(new IOException("Fixture machine is offline"))
        };
        using var binding = new AgentModelBindingEditor(form, profile, choices, "local", local, [local], null, Read, () => epoch, epoch);
        try
        {
            Check(binding.CanSave && binding.ModelId == local.Id && binding.ModelWorkerId == "local", "An exact saved model must remain selected on initial hydration.");
            var draft = form.Raw("draft");
            form.SetSelection("modelWorker", "Machine A");
            var loadA = binding.PendingLoad;
            Check(!binding.CanSave && binding.SelectedModel is null, "A model-library read must block Save and retire the previous route.");
            form.SetSelection("modelWorker", "Machine B");
            pendingB.SetResult([b]); await binding.PendingLoad.WaitAsync(token);
            Check(binding.SelectedModel is null && !binding.CanSave, "A newly selected machine must not silently select its first model.");
            form.SetSelection("model", ModelChoices([b]).Keys.Single());
            Check(binding.CanSave && binding.ModelWorkerId == "b" && binding.ModelId == b.Id, "A pinned route must use the selected machine and exact model ID.");
            pendingA.SetResult([a]); await loadA.WaitAsync(token);
            Check(binding.ModelWorkerId == "b" && binding.ModelId == b.Id && binding.CanSave, "A late result from another machine must not replace the selected model.");
            Check(form.Raw("draft") == draft && form.Raw("budget") == "3072" && profile.WorkerId == "execution-owner" &&
                profile.ModelWorkerId == "local" && profile.ModelId == local.Id, "Selection changes must retain field drafts, leave the original profile untouched, and keep execution placement separate.");
            form.SetSelection("modelWorker", "Unavailable machine"); await binding.PendingLoad.WaitAsync(token);
            Check(!binding.CanSave && form.InlineStatusIsError && form.InlineStatusText.Contains("offline", StringComparison.Ordinal),
                "Failed library reads must visibly block Save without discarding the draft.");
            form.SetSelection("modelMode", AgentModelBindingEditor.FollowDefault);
            Check(binding.CanSave && binding.ModelId == local.Id && binding.ModelWorkerId == "local", "Following the coordinator default must recover the exact default route without pinning a remote selection.");
            epoch++;
            form.SetSelection("modelMode", AgentModelBindingEditor.PinnedModel);
            await binding.PendingLoad.WaitAsync(token);
            Check(!binding.CanSave && form.InlineStatusText.Contains("connection changed", StringComparison.Ordinal),
                "A service-generation change must reject adoption and saving of a stale route.");
            form.SetSelection("modelMode", AgentModelBindingEditor.FollowDefault);
            Check(!binding.CanSave, "Changing selection must not bypass a changed service identity.");

            static IEnumerable<DependencyObject> Children(DependencyObject parent)
            {
                foreach (var item in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                { yield return item; foreach (var nested in Children(item)) yield return nested; }
            }
            var tabs = Children(form).OfType<TabControl>().Single();
            Check(tabs.Items.OfType<TabItem>().Select(AutomationProperties.GetAutomationId).SequenceEqual([
                "FieldsDialogTab.general", "FieldsDialogTab.model", "FieldsDialogTab.context"]),
                "Tabbed fields must keep stable group identities and one shared draft.");
            var priorTheme = Ui.AppearanceTheme;
            var args = Environment.GetCommandLineArgs();
            var captureIndex = Array.IndexOf(args, "--configuration-flow-captures");
            var captureDirectory = captureIndex >= 0 ? Path.GetFullPath(args.ElementAtOrDefault(captureIndex + 1)
                ?? throw new ArgumentException("Configuration flow captures require an existing task-owned output directory.")) : null;
            if (captureDirectory is not null && !Directory.Exists(captureDirectory)) throw new DirectoryNotFoundException(captureDirectory);
            try
            {
                foreach (var theme in new[] { AppearanceThemePolicy.Light, AppearanceThemePolicy.Dark })
                {
                    Ui.ApplyAppearanceTheme(theme);
                    foreach (var size in new[] { new Size(1024, 768), new Size(1360, 880) })
                    {
                        var layout = (FrameworkElement)Content;
                        Tabs.SelectedIndex = 1;
                        SelectTab("Models");
                        layout.Measure(size); layout.Arrange(new Rect(new Point(), size)); layout.UpdateLayout();
                        Check(layout.ActualWidth == size.Width && layout.ActualHeight == size.Height,
                            "The rearranged configuration content must fit each tested viewport.");
                        tabs.SelectedIndex = 1;
                        var editor = (FrameworkElement)form.Content;
                        editor.Measure(new Size(680, 670)); editor.Arrange(new Rect(0, 0, 680, 670)); editor.UpdateLayout();
                        static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
                        {
                            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
                            {
                                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                                yield return child; foreach (var nested in VisualChildren(child)) yield return nested;
                            }
                        }
                        modelWorkerPicker.ApplyTemplate(); layout.UpdateLayout();
                        var presenter = (ContentPresenter)modelWorkerPicker.Template.FindName("SelectionPresenter", modelWorkerPicker);
                        presenter.ApplyTemplate(); presenter.UpdateLayout();
                        Check(VisualChildren(presenter).OfType<TextBlock>().Any(x => x.Text == "This machine · local pipe"),
                            "DisplayMemberPath must render the readable machine name in the collapsed common ComboBox template.");
                        var dropdownItem = new ComboBoxItem { Content = modelWorkerPicker.SelectedItem,
                            ContentTemplate = modelWorkerPicker.ItemTemplate, ContentTemplateSelector = modelWorkerPicker.ItemTemplateSelector };
                        dropdownItem.Measure(new Size(400, 80)); dropdownItem.Arrange(new Rect(0, 0, 400, 80)); dropdownItem.UpdateLayout();
                        Check(VisualChildren(dropdownItem).OfType<TextBlock>().Any(x => x.Text == "This machine · local pipe"),
                            "The common dropdown item template must retain the same DisplayMemberPath label.");
                        foreach (var combo in new[]
                        {
                            new ComboBox { ItemsSource = new[] { "Plain label" }, SelectedIndex = 0 },
                            new ComboBox { Items = { new ComboBoxItem { Content = "Content label" } }, SelectedIndex = 0 }
                        })
                        {
                            // Standalone fixtures need the same implicit application style resolved explicitly.
                            combo.Style = (Style)Application.Current.Resources[typeof(ComboBox)];
                            combo.ApplyTemplate();
                            combo.Measure(new Size(300, 40)); combo.Arrange(new Rect(0, 0, 300, 40));
                            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.DataBind); combo.UpdateLayout();
                            var text = VisualChildren(combo).OfType<TextBlock>().Select(x => x.Text).ToArray();
                            Check(text.Contains(combo.SelectedItem is ComboBoxItem ? "Content label" : "Plain label", StringComparer.Ordinal),
                                $"The shared selection presenter must preserve ordinary string and ComboBoxItem content labels. Selected type: {combo.SelectedItem?.GetType().Name}; selection box: {combo.SelectionBoxItem}; rendered text: {string.Join(" | ", text)}.");
                        }
                        var selected = (TabItem)tabs.SelectedItem;
                        var scroller = (ScrollViewer)selected.Content;
                        Check(scroller.ActualHeight > 0 && scroller.VerticalContentAlignment == VerticalAlignment.Top,
                            "A selected editor tab must have a measured, top-aligned scrolling viewport.");
                        if (captureDirectory is null) continue;
                        static void Capture(FrameworkElement view, int width, int height, string destination)
                        {
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            var background = new System.Windows.Media.DrawingVisual();
                            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Ui.Paper, null, new Rect(0, 0, width, height));
                            bitmap.Render(background); bitmap.Render(view);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                            encoder.Save(output);
                        }
                        Capture(layout, (int)size.Width, (int)size.Height, Path.Combine(captureDirectory, $"configuration-{theme}-{(int)size.Width}.png"));
                        if (size.Width == 1024) Capture(editor, 680, 670, Path.Combine(captureDirectory, $"agent-editor-{theme}.png"));
                    }
                }
            }
            finally { Ui.ApplyAppearanceTheme(priorTheme); }
        }
        finally { form.DiscardDraftAndClose(); }
        Check(profile.ModelId == local.Id && profile.ModelWorkerId == "local", "Closing the draft editor must not mutate its source profile.");
        return checks;
    }

    /// <summary>Native controls against the already-connected owned-pipe fixture; injected rows and engine
    /// snapshots exercise availability only and never invoke an action or claim a real Worker result.</summary>
    internal void VerifyAttachedAvailabilityControls(Action<bool, string> check)
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null)
            throw new InvalidOperationException("Attached availability checks require the connected fixture owner.");
        check(new[] { connectorProvision, connectorStart, connectorStop, connectorStatus }
                .All(button => button is { IsEnabled: false } && button.ToolTip?.ToString()?.Contains("owning workspace", StringComparison.Ordinal) == true),
            "attached connector actions are disabled with the owning-workspace reason");

        var originalPlugins = desktopPluginItems;
        try
        {
            desktopPluginItems = [new DesktopPluginInfo("pegasus.appearance", "Appearance", "1.0.0", "Fixture", false,
                "fixture-directory", "fixture-manifest", null)];
            RefreshAppearanceThemeAction();
            check(appearanceThemeSettings?.IsEnabled == false &&
                  appearanceThemeSettingsHelp.Text.Contains("Enable the Appearance plugin", StringComparison.Ordinal) &&
                  appearanceTheme.Items.Count == 3,
                "disabled Appearance package removes only its settings action, not core color-mode choices");
        }
        finally { desktopPluginItems = originalPlugins; RefreshAppearanceThemeAction(); }

        var priorEngineState = engineActionState;
        var priorEngineClient = engineStatusClient;
        var priorEpoch = engineEpoch;
        var priorSlot = engineSlot.SelectedItem;
        try
        {
            engineSlot.SelectedItem = "B";
            engineStatusClient = Client;
            engineEpoch = 11;
            engineActionState = new(false, true, false, "A", false, "A", 0, 0, 0,
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = true, ["B"] = false },
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = false, ["B"] = true });
            UpdateEngineActions();
            check(enginePause?.IsEnabled == true && engineResume?.IsEnabled == false &&
                  engineActivate?.IsEnabled == false && engineRollback?.IsEnabled == false &&
                  enginePromote?.IsEnabled == false && engineActionHelp.Text.Contains("B is unavailable", StringComparison.Ordinal),
                "running slot A with unavailable B offers pause but no invalid candidate action");
            engineActionState = new(true, true, false, "A", false, "A", 0, 0, 0,
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = true, ["B"] = true },
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = false, ["B"] = true });
            UpdateEngineActions();
            check(engineResume?.IsEnabled == true && engineActivate?.IsEnabled == true &&
                  engineRollback?.IsEnabled == false && enginePromote?.IsEnabled == false,
                "paused healthy engine offers only valid resume and verified inactive-slot activation");
            engineActionState = engineActionState with { ResumeHoldBlocked = true };
            UpdateEngineActions();
            check(engineResume?.IsEnabled == false && engineActionHelp.Text.Contains("intentional work hold", StringComparison.OrdinalIgnoreCase),
                "active intentional hold disables Resume while retaining verified slot availability");
            engineActionState = engineActionState with { ResumeHoldBlocked = null };
            UpdateEngineActions();
            check(engineResume?.IsEnabled == false && engineActionHelp.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                "unknown intentional hold conservatively disables Resume");
            engineActionState = new(true, false, false, null, false, null, 0, 0, 0,
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = true, ["B"] = true },
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = false, ["B"] = true });
            engineSlot.SelectedItem = "A";
            UpdateEngineActions();
            check(engineActivate?.IsEnabled == false && engineActionHelp.Text.Contains("current or protected", StringComparison.Ordinal),
                "no-active recovery still protects the persisted selected A slot even when its package verifies");
            engineSlot.SelectedItem = "B";
            UpdateEngineActions();
            check(engineActivate?.IsEnabled == true && engineResume?.IsEnabled == false &&
                  engineActionHelp.Text.Contains("No engine is active", StringComparison.Ordinal),
                "quiescent engine with no active generation may activate a verified recovery slot");
            engineActionState = new(true, true, false, "B", true, "A", 0, 0, 0,
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = true, ["B"] = true },
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = false, ["B"] = false });
            UpdateEngineActions();
            check(engineRollback?.IsEnabled == true && engineActivate?.IsEnabled == false && enginePromote?.IsEnabled == false,
                "unconfirmed candidate allows rollback while untested confirmation stays disabled");
            engineActionState = engineActionState with { Paused = false, CommittedDecisions = 1 };
            UpdateEngineActions();
            check(engineRollback?.IsEnabled == false && enginePromote?.IsEnabled == true,
                "host-committed candidate enables confirmation but running engine blocks rollback");
            using var oldClient = new CerebralClient();
            engineStatusClient = oldClient;
            engineEpoch = 12;
            UpdateEngineActions();
            check(engineStatusClient is null && engineActionState is null && engineEpoch is null &&
                  enginePause?.IsEnabled == false && engineResume?.IsEnabled == false &&
                  engineActivate?.IsEnabled == false && engineRollback?.IsEnabled == false &&
                  enginePromote?.IsEnabled == false,
                "new connected Client retires cached actions and epoch from a different Client");
        }
        finally
        {
            engineActionState = priorEngineState; engineStatusClient = priorEngineClient; engineEpoch = priorEpoch;
            engineSlot.SelectedItem = priorSlot; UpdateEngineActions();
        }

        var priorJobs = jobs.ItemsSource; var priorJobSelection = jobs.SelectedItem;
        var priorSkills = skills.ItemsSource; var priorSkillSelection = skills.SelectedItem;
        var priorExtensions = serviceExtensions.ItemsSource; var priorExtensionSelection = serviceExtensions.SelectedItem;
        try
        {
            jobs.SelectedItem = null; RefreshWorkSelectionActions();
            check(workDetails?.IsEnabled == false && workStop?.IsEnabled == false,
                "empty active-work selection disables Details and Stop selected");
            var work = new WorkQueueRow(WorkSource.Workflow, "fixture-run", "Fixture", "fixture-profile", "fixture", "Waiting", DateTimeOffset.UtcNow);
            jobs.ItemsSource = new[] { work }; jobs.SelectedItem = work; RefreshWorkSelectionActions();
            check(workDetails?.IsEnabled == true && workStop?.IsEnabled == true,
                "selected unfinished fixture row enables the existing guarded work actions");

            skills.SelectedItem = null; RefreshSkillSelectionActions();
            check(skillConfigure?.IsEnabled == false && skillInspect?.IsEnabled == false && skillRemove?.IsEnabled == false,
                "empty skill selection disables all selected-skill actions");
            var builtin = new SkillRow(new SkillSummary("fixture-skill", "Fixture", "1.0.0", "Fixture", true, true, 1, [], [], "built-in"));
            skills.ItemsSource = new[] { builtin }; skills.SelectedItem = builtin; RefreshSkillSelectionActions();
            check(skillConfigure?.IsEnabled == true && skillInspect?.IsEnabled == true && skillRemove?.IsEnabled == false,
                "selected built-in skill permits inspection/configuration but not removal");

            serviceExtensions.SelectedItem = null; RefreshServiceExtensionSelectionAction();
            check(serviceExtensionInspect?.IsEnabled == false,
                "empty service-extension selection disables Inspect");
            var extension = new ServiceExtensionInventory("fixture-extension", "Fixture", "1.0.0", true, [], [], null);
            serviceExtensions.ItemsSource = new[] { extension }; serviceExtensions.SelectedItem = extension;
            RefreshServiceExtensionSelectionAction();
            check(serviceExtensionInspect?.IsEnabled == true,
                "selected fixture service extension enables Inspect without invoking it");
        }
        finally
        {
            jobs.ItemsSource = priorJobs; jobs.SelectedItem = priorJobSelection; RefreshWorkSelectionActions();
            skills.ItemsSource = priorSkills; skills.SelectedItem = priorSkillSelection; RefreshSkillSelectionActions();
            serviceExtensions.ItemsSource = priorExtensions; serviceExtensions.SelectedItem = priorExtensionSelection;
            RefreshServiceExtensionSelectionAction();
        }
    }

    internal Task BeginFixtureRecentPreferenceSave(string? currentFooter = null)
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null)
            throw new InvalidOperationException("Preference completion check requires the connected attached fixture.");
        recentChatLimitInput.Text = "20";
        SetStatus(currentFooter ?? InvalidRecentChatLimitStatus, true);
        return SaveInterfacePreferenceAsync(CreateIndependentClient(), Client,
            InterfacePreferencePolicy.RecentChatLimit, 20, "recent chat limit");
    }

    internal void EnterFixtureRecentChatLimit(string text)
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null || !CanEditInterfacePreferences)
            throw new InvalidOperationException("Recent-chat handler check requires supported preferences on the connected fixture.");
        recentChatLimitInput.Text = text;
        SaveRecentChatLimit();
    }

    internal void SelectFixtureAppearanceTheme(string theme)
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null || !CanEditInterfacePreferences ||
            !AppearanceThemePolicy.Choices.Contains(theme))
            throw new InvalidOperationException("Appearance selection check requires a supported theme on the connected fixture.");
        appearanceTheme.SelectedValue = theme;
    }

    internal bool FixtureAppearanceThemeMatches(string theme) =>
        appearanceTheme.SelectedValue as string == theme && Ui.AppearanceTheme == theme && Ui.ResolvedTheme == theme;

    internal void SelectFixtureComputeRows()
    {
        Dispatcher.VerifyAccess();
        models.SelectedItem = (models.ItemsSource as IEnumerable<ModelRow>)?.FirstOrDefault();
        agents.SelectedItem = (agents.ItemsSource as IEnumerable<AgentRow>)?.FirstOrDefault();
        RefreshModelSelectionActions();
        RefreshAgentSelectionAction();
    }

    internal (bool ModelsEnabled, bool ModelsDisabled, bool AgentEditEnabled, string? ModelName, string? AgentName)
        FixtureComputeActionState()
    {
        Dispatcher.VerifyAccess();
        RefreshModelSelectionActions();
        RefreshAgentSelectionAction();
        return (new[] { modelUse, modelLoad, modelDetails }.All(button => button is { IsEnabled: true }),
            new[] { modelUse, modelLoad, modelDetails }.All(button => button is { IsEnabled: false }),
            agentEdit?.IsEnabled == true,
            (models.SelectedItem as ModelRow)?.Name, (agents.SelectedItem as AgentRow)?.Name);
    }

    internal (Task ModelRead, Task AgentRead, Action Release) BeginDeferredComputeReadsForChecks()
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null || queueCommandOverride is not null)
            throw new InvalidOperationException("Deferred compute reads require the connected owned fixture without another dispatch override.");
        var modelReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queueCommandOverride = (request, _) => request.Operation switch
        {
            "models.list" => modelReply.Task,
            "agents.list" => agentReply.Task,
            _ => throw new InvalidOperationException("Unexpected deferred compute read: " + request.Operation)
        };
        try
        {
            var modelRead = RefreshModels();
            var agentRead = RefreshAgents();
            void Release()
            {
                modelReply.TrySetResult(CommandResult.Ok(new[] { new ModelRecord
                    { Id = "fixture-model", Name = "Previous-owner model", ParallelRequests = 2, ContextTokens = 16384, State = "unloaded" } }));
                agentReply.TrySetResult(CommandResult.Ok(new[] { new AgentProfile
                    { Id = "fixture-agent", Name = "Previous-owner lead", Role = "lead", ModelId = "fixture-model" } }));
            }
            return (modelRead, agentRead, Release);
        }
        finally { queueCommandOverride = null; }
    }

    /// <summary>Deterministic editor, CAS and native tab checks; never uses saved preferences, a Worker or installed data.</summary>
    public async Task<int> CheckConfigurationPreferencesAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run Configuration preference checks on the active WPF dispatcher.");
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        checks += CheckModelWorkerLibrary();
        checks += await CheckAgentModelEditorAsync(deadline.Token);
        checks += await CheckAgentProfileDraftAsync(deadline.Token);
        checks += CheckComputeOverview();
        checks += await CheckComputeProjectionRacesAsync(deadline.Token);
        checks += CheckSettingsHierarchy();
        checks += CheckSystemAccessControls();
        checks += CheckConfigurationNavigationLayout();
        checks += CheckDesktopPluginSelectionLayout();
        var topLevel = Tabs.Items.OfType<TabItem>().ToArray();
        Check(new[] { "ConfigurationAreaSystem", "ConfigurationAreaModelsAndAgents", "ConfigurationAreaToolsExtensionsAndSkills", "ConfigurationAreaDataAndDiagnostics" }
                .All(id => topLevel.Any(tab => AutomationProperties.GetAutomationId(tab) == id)),
            "The Settings rail must preserve existing category mount IDs across page relocation and label changes.");
        Check(topLevel.Select(AutomationProperties.GetAutomationId).Distinct(StringComparer.Ordinal).Count() == topLevel.Length,
            "Every top-level Configuration group must have a distinct stable automation ID.");
        SelectTab("Agents");
        Check(AutomationProperties.GetAutomationId(((TabControl)((TabItem)Tabs.SelectedItem).Content).SelectedItem as TabItem) == "ConfigurationTabAgents",
            "Existing navigation commands must find a renamed tab through its stable ID.");
        var systemTabs = (TabControl)topLevel.Single(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationAreaSystem").Content;
        var modelAndAgentTabs = (TabControl)topLevel.Single(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationAreaModelsAndAgents").Content;
        var dataAndDiagnosticsTabs = (TabControl)topLevel.Single(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationAreaDataAndDiagnostics").Content;
        Check(topLevel.Select(tab => (TabControl)tab.Content)
                  .All(tabs => tabs.VerticalContentAlignment == VerticalAlignment.Stretch && tabs.HorizontalContentAlignment == HorizontalAlignment.Stretch) &&
              topLevel.Single(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationAreaToolsExtensionsAndSkills").Content == extensionTabs,
            "Each Settings category must retain its native detail viewport and the existing plugin management mount.");
        static string[] Actions(TabControl tabs, string tabTitle)
        {
            var page = (DockPanel)tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, tabTitle)).Content;
            var heading = page.Children.OfType<StackPanel>().Single(panel => panel.Children.OfType<WrapPanel>().Any());
            return heading.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
                .Select(button => button.Content?.ToString() ?? "").ToArray();
        }
        var engineMount = topLevel.Select(tab => (TabControl)tab.Content)
            .Single(tabs => tabs.Items.OfType<TabItem>().Any(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationTabEngine"));
        Check(Actions(engineMount, "Engine").SequenceEqual([
                "Refresh", "Pause", "Resume", "Activate selected", "Roll back", "Confirm tested candidate"]),
            "Engine lifecycle controls must remain visible on their owning System tab.");
        Check(engineSlot.Items.Cast<string>().SequenceEqual(["A", "B"]) && Equals(engineSlot.SelectedItem, "B"),
            "The Engine tab must expose the two code slots without inventing an additional slot.");
        Check(!IsConnected, "Configuration preference checks must not connect to a Worker.");
        var fixtureHoldId = Guid.NewGuid().ToString("N");
        Check(HoldBlocksResume(CerebralJson.Element(new { contract = "cerebral.owner-work-hold/1", state = "held",
                holdId = fixtureHoldId, startupPromptRequired = false })) == true &&
              HoldBlocksResume(CerebralJson.Element(new { contract = "cerebral.owner-work-hold/1", state = "none",
                holdId = (string?)null })) == false,
            "a held marker blocks Resume even when all captured work is terminal and no startup prompt is required");
        Check(HoldBlocksResume(CerebralJson.Element(new { contract = "wrong", state = "none", holdId = (string?)null })) is null &&
              HoldBlocksResume(CerebralJson.Element(new { contract = "cerebral.owner-work-hold/1", state = "held",
                holdId = "invalid" })) is null,
            "unknown hold contract or invalid active identity leaves Resume unavailable");
        using (var priorClient = new CerebralClient())
        {
            engineStatusClient = priorClient;
            engineActionState = new(false, true, false, "A", false, "A", 0, 0, 0,
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = true, ["B"] = false },
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["A"] = false, ["B"] = true });
            engineEpoch = 9;
            UpdateEngineActions();
            Check(engineStatusClient is null && engineActionState is null && engineEpoch is null &&
                  enginePause?.IsEnabled == false && engineResume?.IsEnabled == false &&
                  engineActivate?.IsEnabled == false && engineRollback?.IsEnabled == false &&
                  enginePromote?.IsEnabled == false,
                "A disconnected page must retire a previously cached engine generation without reading its unavailable Client.");
        }
        engineEpoch = 9;
        await RefreshEngine();
        Check(engineEpoch is null && engineSummary.Text == "Connect and refresh to inspect the current engine.",
            "Refreshing a disconnected Engine tab must clear its cached generation without accessing the throwing Client property.");
        var disconnectedMutationRejected = false;
        try { await ChangeEngine("engine.pause"); }
        catch (InvalidOperationException error) when (error.Message == "Connect to a Worker first.") { disconnectedMutationRejected = true; }
        Check(disconnectedMutationRejected, "A disconnected engine mutation must report an actionable connection error.");
        Check(appearanceThemeSettings?.IsEnabled == false && workDetails?.IsEnabled == false &&
              workStop?.IsEnabled == false && serviceExtensionInspect?.IsEnabled == false &&
              skillConfigure?.IsEnabled == false && skillInspect?.IsEnabled == false && skillRemove?.IsEnabled == false,
            "Disconnected Configuration must not offer unavailable plugin or selection-dependent actions.");
        var appearancePackage = new DesktopPluginInfo("pegasus.appearance", "Appearance", "1.0.0", "Fixture", true,
            "C:\\fixture\\appearance", "C:\\fixture\\appearance\\desktop-plugin.json", null);
        var activeAppearance = new DesktopPluginLoadStatus("pegasus.appearance", true, null);
        var loadedAppearance = (Directory: appearancePackage.Directory, Version: appearancePackage.Version);
        Check(!AppearanceThemeSettingsAvailable(appearancePackage with { Enabled = false }, activeAppearance, loadedAppearance, true) &&
              !AppearanceThemeSettingsAvailable(appearancePackage with { Error = "invalid package" }, activeAppearance, loadedAppearance, true),
            "disabled or invalid catalog selection cannot expose Appearance settings even with a retained panel");
        Check(!AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance with { Loaded = false }, loadedAppearance, true) &&
              !AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance with { Error = "runtime failed" }, loadedAppearance, true),
            "unloaded or failed runtime generation cannot expose Appearance settings");
        Check(!AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance,
                  (Directory: "C:\\fixture\\old-appearance", Version: "1.0.0"), true) &&
              !AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance,
                  (Directory: appearancePackage.Directory, Version: "0.9.0"), true) &&
              !AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance, loadedAppearance, false),
            "stale loaded directory/version or missing panel cannot expose Appearance settings");
        Check(AppearanceThemeSettingsAvailable(appearancePackage, activeAppearance, loadedAppearance, true),
            "only the matching loaded error-free Appearance generation with a panel exposes settings");
        var catalogPluginRows = new[]
        {
            new DesktopPluginInfo("plugin.runtime", "Runtime plugin", "1.0.0", "Runtime failure", true,
                "runtime-directory", "runtime-manifest", null),
            new DesktopPluginInfo("plugin.catalog", "Catalog plugin", "1.0.0", "Catalog failure", false,
                "catalog-directory", "catalog-manifest", "Catalog validation failed.")
        };
        var failedPluginRows = CreateDesktopPluginDisplayRows(catalogPluginRows,
        [
            new DesktopPluginLoadStatus("plugin.catalog", false, "Runtime error must not replace the catalog error."),
            new DesktopPluginLoadStatus("plugin.runtime", false, "Runtime initialization failed."),
            new DesktopPluginLoadStatus("plugin.unknown", false, "Unknown runtime status must not create a row.")
        ]);
        Check(failedPluginRows.Count == catalogPluginRows.Length &&
              failedPluginRows.Single(item => item.Id == "plugin.runtime").Error == "Runtime initialization failed." &&
              failedPluginRows.Single(item => item.Id == "plugin.catalog").Error == "Catalog validation failed." &&
              failedPluginRows.All(item => item.Id != "plugin.unknown"),
            "Desktop plugin display rows must overlay current runtime failures by ID while keeping catalog errors authoritative.");
        var recoveredPluginRows = CreateDesktopPluginDisplayRows(catalogPluginRows,
            [new DesktopPluginLoadStatus("plugin.runtime", true, null)]);
        Check(recoveredPluginRows.Single(item => item.Id == "plugin.runtime").Error is null && catalogPluginRows[0].Error is null,
            "A recovered plugin must clear its transient runtime error without changing the cached catalog row.");
        var originalPluginItems = desktopPluginItems;
        var originalPluginSearch = desktopPluginSearch.Text;
        var originalPluginFilter = desktopPluginFilter.SelectedItem;
        var originalSelectedPluginId = (desktopPluginList.SelectedItem as DesktopPluginInfo)?.Id;
        try
        {
            var retainedPlugin = new DesktopPluginInfo("plugin.retained", "Retained plugin", "1.0.0", "Existing row", true,
                "retained-directory", "retained-manifest", null);
            desktopPluginSearch.Text = "";
            desktopPluginFilter.SelectedIndex = 0;
            desktopPluginItems = [retainedPlugin];
            ApplyDesktopPluginFilter();
            desktopPluginList.SelectedItem = desktopPluginItems[0];
            RefreshConfigurationDesktopPlugins(() =>
            [
                retainedPlugin with { Version = "1.1.0" },
                new DesktopPluginInfo("plugin.cross-process", "Cross-process plugin", "1.0.0", "New catalog row", true,
                    "cross-process-directory", "cross-process-manifest", null)
            ]);
            var refreshedPluginRows = desktopPluginList.ItemsSource.Cast<DesktopPluginInfo>().ToArray();
            Check(refreshedPluginRows.Select(item => item.Id).SequenceEqual(["plugin.retained", "plugin.cross-process"]) &&
                  refreshedPluginRows[0].Version == "1.1.0" &&
                  (desktopPluginList.SelectedItem as DesktopPluginInfo)?.Id == "plugin.retained" &&
                  desktopPluginSearch.Text.Length == 0 && Equals(desktopPluginFilter.SelectedItem, "All plugins"),
                "A runtime catalog change from another desktop process must refresh visible plugin rows while preserving selection and filters.");
            desktopPluginSearch.Text = "Retained";
            desktopPluginFilter.SelectedItem = "Enabled";
            desktopPluginList.SelectedItem = desktopPluginItems.Single(item => item.Id == "plugin.retained");
            RefreshConfigurationDesktopPlugins(() =>
            [
                retainedPlugin with { Version = "1.2.0" },
                new DesktopPluginInfo("plugin.other", "Other plugin", "1.0.0", "Does not match search", true,
                    "other-directory", "other-manifest", null),
                new DesktopPluginInfo("plugin.retained-disabled", "Retained disabled plugin", "1.0.0", "Matches search but is disabled", false,
                    "disabled-directory", "disabled-manifest", null)
            ]);
            var filteredRefreshRows = desktopPluginList.ItemsSource.Cast<DesktopPluginInfo>().ToArray();
            Check(filteredRefreshRows.Length == 1 && filteredRefreshRows[0].Id == "plugin.retained" &&
                  filteredRefreshRows[0].Version == "1.2.0" &&
                  (desktopPluginList.SelectedItem as DesktopPluginInfo)?.Id == "plugin.retained" &&
                  desktopPluginSearch.Text == "Retained" && Equals(desktopPluginFilter.SelectedItem, "Enabled"),
                "A runtime catalog refresh must preserve a nondefault search and filter while replacing the selected catalog row.");
        }
        finally
        {
            desktopPluginItems = originalPluginItems;
            desktopPluginSearch.Text = originalPluginSearch;
            desktopPluginFilter.SelectedItem = originalPluginFilter;
            ApplyDesktopPluginFilter();
            desktopPluginList.SelectedItem = desktopPluginItems.FirstOrDefault(item => item.Id == originalSelectedPluginId);
        }
        var pluginPanelOne = new TextBlock { Text = "First plugin settings" };
        var pluginPanelTwo = new TextBlock { Text = "Second plugin settings" };
        var pluginConfiguration = CreateDesktopPluginConfigurationTabs("plugin.test",
        [
            new DesktopHostedPanel("plugin.test", "first", "General", pluginPanelOne),
            new DesktopHostedPanel("plugin.test", "second", "Advanced", pluginPanelTwo)
        ]);
        Check(pluginConfiguration.Items.OfType<TabItem>().Select(x => x.Header?.ToString()).SequenceEqual(["General", "Advanced"]) &&
              pluginConfiguration.Items.OfType<TabItem>().Select(x => x.Content).SequenceEqual<object?>([pluginPanelOne, pluginPanelTwo]) &&
              pluginConfiguration.TabStripPlacement == Dock.Top &&
              pluginConfiguration.Items.OfType<TabItem>().Select(AutomationProperties.GetAutomationId).Distinct(StringComparer.Ordinal).Count() == 2 &&
              extensionTabs.Items.OfType<TabItem>().Select(AutomationProperties.GetAutomationId).SequenceEqual(["ConfigurationTabDesktopPlugins", "ConfigurationTabServiceExtensions", "ConfigurationTabSkills", "ConfigurationTabToolsAndMcp"]),
            "Plugin configuration must reuse the plugin's existing panels inside a popup surface without adding permanent sidebar tabs.");
        foreach (var tab in pluginConfiguration.Items.OfType<TabItem>()) tab.Content = null;
        var originalAppearance = Ui.AppearanceTheme;
        var originalResolvedTheme = Ui.ResolvedTheme;
        Check(AutomationProperties.GetAutomationId(appearanceTheme) == "ConfigurationAppearanceTheme" &&
              appearanceTheme.Items.Count == 3 && appearanceTheme.SelectedValue as string == AppearanceThemePolicy.System,
            "Configuration must expose the three stable core appearance choices with System selected by default.");
        Check(AutomationProperties.GetAutomationId(messageTimeFormat) == "ConfigurationMessageTimeFormat" &&
              messageTimeFormat.Items.Count == 2 && messageTimeFormat.SelectedValue as string == MessageTimeFormatPolicy.TwelveHour,
            "Configuration must expose 12-hour message timestamps by default and an explicit 24-hour option.");
        Check(AutomationProperties.GetAutomationId(recentChatLimitInput) == "ConfigurationRecentChatLimit" &&
              recentChatLimitInput.Text == "20",
            "Configuration must expose a 20-chat default recent general-chat limit.");
        Check(!CanEditInterfacePreferences && new FrameworkElement[] { appearanceTheme, messageTimeFormat, recentChatLimitInput }
                .All(control => !control.IsEnabled && AutomationProperties.GetHelpText(control).Contains("connect to the Worker", StringComparison.Ordinal)),
            "Disconnected Configuration must disable Worker-owned interface preferences and explain how to make them available.");
        var disconnectedStatus = Status.Text;
        recentChatLimitInput.Text = "0";
        SaveRecentChatLimit();
        Check(Status.Text == disconnectedStatus,
            "Disconnected recent-chat input must not validate or attempt to save a Worker-owned preference.");
        recentChatLimitInput.Text = "20";
        appearanceTheme.SelectedValue = AppearanceThemePolicy.Dark;
        Check(Ui.AppearanceTheme == originalAppearance && Ui.ResolvedTheme == originalResolvedTheme &&
              Status.Text == disconnectedStatus,
            "Disconnected appearance selection must not preview or save a Worker-owned preference.");
        appearanceTheme.SelectedValue = AppearanceThemePolicy.Light;
        Check(Ui.AppearanceTheme == originalAppearance && Ui.ResolvedTheme == originalResolvedTheme &&
              Status.Text == disconnectedStatus,
            "A second disconnected appearance selection must leave the shared desktop palette unchanged.");
        hydratingAppearanceTheme = true;
        appearanceTheme.SelectedValue = originalAppearance;
        hydratingAppearanceTheme = false;
        var snapshot = new InstructionSnapshot("original", "revision-1", "SQLite test source");
        var draft = new InstructionDraft("role:system", "Shared", 1, snapshot);
        var writes = 0;
        Task<InstructionSnapshot> Save(string content, string expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); writes++;
            return Task.FromResult(new InstructionSnapshot(content, expected + "-next", "SQLite saved source"));
        }
        await draft.SaveAsync(Save, () => true, deadline.Token);
        Check(writes == 0 && !draft.NeedsAttention, "Hydrating unchanged text must never save it.");
        draft.Edit("edited");
        await draft.SaveAsync(Save, () => true, deadline.Token);
        Check(writes == 1 && draft.Baseline.Content == "edited" && !draft.Dirty, "A successful save must adopt only the submitted content and receipt.");
        var gate = new TaskCompletionSource<InstructionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        draft.Edit("first queued edit");
        var inflight = draft.SaveAsync((_, _, _) => { writes++; return gate.Task; }, () => true, deadline.Token);
        Check(draft.SavePending, "An in-flight save must be visible before a second edit.");
        draft.Edit("second queued edit");
        Check(ReferenceEquals(inflight, draft.SaveAsync(Save, () => true, deadline.Token)), "A second save must join the existing mutation, not dispatch a duplicate.");
        gate.SetResult(new("first queued edit", "revision-3", "SQLite"));
        await inflight.WaitAsync(deadline.Token);
        Check(draft.Content == "second queued edit" && draft.Baseline.Content == "first queued edit" && draft.CanAutosave,
            "Edits typed during a save must remain dirty against the acknowledged revision.");
        await draft.SaveAsync(Save, () => true, deadline.Token);
        Check(!draft.NeedsAttention && writes == 3, "The next edit must save once against the new receipt.");

        draft.Edit("conflicting local draft");
        await draft.SaveAsync((_, _, _) => throw new InvalidOperationException("Changed elsewhere"), () => true, deadline.Token);
        var beforeBlocked = writes;
        await draft.SaveAsync(Save, () => true, deadline.Token);
        Check(draft.Blocked && !draft.Uncertain && writes == beforeBlocked && draft.Content == "conflicting local draft",
            "A rejected CAS must retain the draft and stop automatic retries.");
        Check(!draft.Review(new("external content", "external-revision", "SQLite")) && draft.Baseline.Token != "external-revision",
            "A fresh read must not silently rebase a conflicting draft.");
        draft.KeepDraftAgainst(new("external content", "external-revision", "SQLite"));
        Check(draft.CanAutosave && draft.Content == "conflicting local draft", "Explicitly keeping a reviewed draft may use the reviewed revision.");
        await draft.SaveAsync((_, _, _) => throw new IOException("Disconnected"), () => true, deadline.Token);
        Check(draft.Uncertain && draft.NeedsAttention, "A lost mutation response must remain unconfirmed even if the editor is later changed.");
        draft.Edit(draft.Baseline.Content);
        Check(draft.NeedsAttention && !draft.Dirty, "Returning to the original text must not erase an uncertain mutation.");
        Check(draft.Review(draft.Baseline) && !draft.NeedsAttention, "An exact saved read can resolve unchanged unconfirmed content.");

        draft.Edit(new string('x', 32 * 1024 + 1));
        var beforeInvalid = writes;
        await draft.SaveAsync(Save, () => true, deadline.Token);
        Check(draft.Blocked && writes == beforeInvalid && draft.Content.Length == 32 * 1024 + 1,
            "Oversized content must stay visible and never be dispatched.");
        draft.Edit("corrected");
        Check(draft.CanAutosave, "Correcting a local validation error must allow the normal debounce again.");
        await draft.SaveAsync(Save, () => true, deadline.Token);
        var unicodeRejected = false;
        try { InstructionDraft.Validate("\ud800"); } catch (ArgumentException) { unicodeRejected = true; }
        Check(unicodeRejected, "Incomplete Unicode must be rejected before network dispatch.");
        var nulRejected = false;
        try { InstructionDraft.Validate("bad\0text"); } catch (ArgumentException) { nulRejected = true; }
        Check(nulRejected, "NUL prompts must be rejected before dispatch.");

        var oldConnection = new InstructionDraft("old", "Old service", 2, snapshot);
        var oldGate = new TaskCompletionSource<InstructionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationMatches = true;
        oldConnection.Edit("old-service edit");
        var oldSave = oldConnection.SaveAsync((_, _, _) => oldGate.Task, () => generationMatches, deadline.Token);
        generationMatches = false; oldConnection.InvalidateConnection();
        oldGate.SetResult(new("old-service edit", "receipt", "old database"));
        await oldSave.WaitAsync(deadline.Token);
        Check(oldConnection.Blocked && oldConnection.Uncertain && oldConnection.Baseline.Token == "revision-1" && oldConnection.Content == "old-service edit",
            "A stale save cannot adopt its receipt into a new connection generation or lose the old draft.");

        var preferences = new Dictionary<string, string> { ["Integrator.NestedTab.CheckMount"] = "tab-second" };
        var preferenceWrites = 0;
        var tabs = new TabControl(); AutomationProperties.SetAutomationId(tabs, "CheckMount");
        TabItem Tab(string id, string title) { var tab = new TabItem { Header = title }; AutomationProperties.SetAutomationId(tab, id); return tab; }
        var first = Tab("tab-first", "First"); var second = Tab("tab-second", "Renamed label");
        tabs.Items.Add(second); tabs.Items.Add(first);
        using (var state = new ConfigurationTabPreferences(tabs, key => preferences.GetValueOrDefault(key),
            (key, value) => { preferences[key] = value; preferenceWrites++; }, error => throw new InvalidOperationException(error)))
        {
            state.Restore(); state.Flush();
            Check(tabs.SelectedItem == second && preferenceWrites == 0, "Hydration must use stable tab IDs across label/order changes without a write.");
            tabs.SelectedItem = first; state.Flush();
            Check(preferences["Integrator.NestedTab.CheckMount"] == "tab-first" && preferenceWrites == 1,
                "A selected nested tab must persist only its own scoped mount preference.");
            tabs.Items.Remove(first); state.Restore(); state.Flush();
            Check(preferences["Integrator.NestedTab.CheckMount"] == "tab-first" && preferenceWrites == 1,
                "Temporary plugin removal must not overwrite its desired selection with a fallback.");
            var replacement = Tab("tab-first", "Plugin remounted"); tabs.Items.Add(replacement); state.Restore();
            Check(tabs.SelectedItem == replacement, "A remounted plugin tab must recover its stable selection.");
            tabs.SelectedItem = second;
            tabs.Items.Remove(second);
            var remountedSecond = Tab("tab-second", "Second remount"); tabs.Items.Add(remountedSecond);
            state.Restore(); state.Flush();
            Check(tabs.SelectedItem == remountedSecond && preferences["Integrator.NestedTab.CheckMount"] == "tab-second" && preferenceWrites == 2,
                "A plugin remount during debounce must preserve and save the user's pending selection, never the temporary fallback.");
        }

        var originalDispatch = instructionCommandOverride;
        var commands = new List<CommandRequest>();
        var savedRole = "server-original"; var roleRevision = 1;
        var savedProfile = "profile-original"; var profileRevision = 7;
        TaskCompletionSource<CommandResult>? heldLoad = null;
        TaskCompletionSource<CommandResult>? heldSave = null;
        Task? outstanding = null;
        instructionCommandOverride = (request, token) =>
        {
            token.ThrowIfCancellationRequested(); commands.Add(request);
            Check(request.ProfileId is null, "Configuration prompt commands must use the owner route.");
            return request.Operation switch
            {
                "instructions.get" => heldLoad is not null ? heldLoad.Task.WaitAsync(token) : Task.FromResult(CommandResult.Ok(new SystemInstructionDocument("SQLite test role", savedRole, "role-" + roleRevision))),
                "instructions.save" => SaveRole(request),
                "agents.instructions.get" => Task.FromResult(CommandResult.Ok(new AgentInstructionDocument("profile", savedProfile, profileRevision))),
                "agents.instructions.save" => heldSave is null ? SaveProfile(request) : heldSave.Task.WaitAsync(token),
                _ => throw new InvalidOperationException("Unexpected editor command " + request.Operation)
            };
        };
        Task<CommandResult> SaveRole(CommandRequest request)
        {
            Check(request.Arguments.GetProperty("expectedSha256").GetString() == "role-" + roleRevision, "Autosave must retain the loaded opaque role CAS token.");
            savedRole = request.Arguments.GetProperty("content").GetString()!; roleRevision++;
            return Task.FromResult(CommandResult.Ok(new SystemInstructionDocument("SQLite saved role", savedRole, "role-" + roleRevision)));
        }
        Task<CommandResult> SaveProfile(CommandRequest request)
        {
            Check(request.Arguments.GetProperty("expectedRevision").GetInt32() == profileRevision, "Profile autosave must use the exact loaded profile revision.");
            savedProfile = request.Arguments.GetProperty("content").GetString()!; profileRevision++;
            return Task.FromResult(CommandResult.Ok(new AgentInstructionDocument("profile", savedProfile, profileRevision)));
        }
        try
        {
            instructionSaveTimer.Stop(); instructionDrafts.Clear();
            var shared = new InstructionTarget("Shared", Role: "system"); var profile = new InstructionTarget("Profile", "profile");
            hydratingInstructions = true;
            instructionTarget.ItemsSource = new[] { shared, profile }; instructionTarget.SelectedItem = shared;
            hydratingInstructions = false;
            await ActivateInstructionsAsync(false).WaitAsync(deadline.Token);
            Check(commands.Count == 1 && instructionEditor.Text == "server-original", "Native editor hydration must issue one read and no save.");
            instructionEditor.Text = "typed in shared"; instructionSaveTimer.Stop();
            hydratingInstructions = true; instructionTarget.SelectedItem = profile; hydratingInstructions = false;
            await ActivateInstructionsAsync(false).WaitAsync(deadline.Token);
            instructionEditor.Text = "typed in profile"; instructionSaveTimer.Stop();
            await FlushInstructionDraftsAsync().WaitAsync(deadline.Token);
            Check(savedRole == "typed in shared" && savedProfile == "typed in profile" && commands.All(x => x.Operation != "agents.save"),
                "Switching sources must retain both drafts and save the profile through the narrow CAS route.");
            instructionEditor.Text = "debounced profile edit";
            await Task.Delay(950, deadline.Token);
            Check(savedProfile == "debounced profile edit" && !activeInstructions!.Draft.NeedsAttention,
                "The actual WPF debounce must persist an edit without a Save click.");
            instructionEditor.Text = "old connection draft"; instructionSaveTimer.Stop();
            heldLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
            hydratingInstructions = true; instructionTarget.SelectedItem = shared; hydratingInstructions = false;
            outstanding = ActivateInstructionsAsync(true);
            InstructionsConnectionChanged();
            heldLoad.TrySetResult(CommandResult.Ok(new SystemInstructionDocument("stale", "STALE CONTENT", "stale")));
            try { await outstanding.WaitAsync(deadline.Token); } catch (OperationCanceledException) { }
            outstanding = null;
            Check(activeInstructions is null && instructionEditor.IsReadOnly && instructionEditor.Text != "STALE CONTENT" &&
                instructionDrafts.Values.Any(x => x.Draft.Content == "old connection draft" && x.Draft.Blocked),
                "Changing connections must cancel stale hydration while retaining dirty drafts for their original service.");
            heldLoad = null;
            hydratingInstructions = true; instructionTarget.SelectedItem = profile; hydratingInstructions = false;
            await ActivateInstructionsAsync(false).WaitAsync(deadline.Token);
            instructionEditor.Text = "held profile write"; instructionSaveTimer.Stop();
            heldSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var flushHeldSave = FlushInstructionDraftsAsync();
            var reviewHeldSave = LoadInstructions();
            var beforeConnectionSwitchReads = commands.Count(x => x.Operation.EndsWith(".get", StringComparison.Ordinal));
            InstructionsConnectionChanged();
            heldSave.TrySetResult(CommandResult.Ok(new AgentInstructionDocument("profile", "held profile write", profileRevision + 1)));
            await Task.WhenAll(flushHeldSave, reviewHeldSave).WaitAsync(deadline.Token);
            Check(commands.Count(x => x.Operation.EndsWith(".get", StringComparison.Ordinal)) == beforeConnectionSwitchReads &&
                instructionDrafts.Values.Any(x => x.Draft.Content == "held profile write" && x.Draft.Uncertain),
                "Review waiting on a save must not read an old profile through a new connection, and the unconfirmed draft must remain retained.");
            heldSave = null;
            await ActivateInstructionsAsync(false).WaitAsync(deadline.Token);
            instructionEditor.Text = "draft before comparison dialog"; instructionSaveTimer.Stop();
            savedProfile = "external change"; profileRevision++;
            instructionShowSavedOverride = (_, _) => InstructionsConnectionChanged();
            instructionConflictChoiceOverride = () => throw new InvalidOperationException("A second dialog must not open after its service changed.");
            await LoadInstructions().WaitAsync(deadline.Token);
            Check(activeInstructions is null && instructionEditor.IsReadOnly && instructionDrafts.Values.Any(x =>
                x.Draft.Content == "draft before comparison dialog" && x.Draft.Blocked),
                "Connection changes inside the comparison dialog must prevent subsequent conflict choices and stale editor adoption.");
            await ActivateInstructionsAsync(false).WaitAsync(deadline.Token);
            instructionEditor.Text = "draft before choice dialog"; instructionSaveTimer.Stop();
            savedProfile = "another external change"; profileRevision++;
            instructionShowSavedOverride = (_, _) => { };
            instructionConflictChoiceOverride = () => { InstructionsConnectionChanged(); return MessageBoxResult.Yes; };
            await LoadInstructions().WaitAsync(deadline.Token);
            Check(activeInstructions is null && instructionEditor.IsReadOnly && instructionDrafts.Values.Any(x =>
                x.Draft.Content == "draft before choice dialog" && x.Draft.Blocked),
                "Connection changes inside the choice dialog must not rebase or reactivate an old-service draft.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, configurationPreferenceChecks = checks,
                evidence = "deterministic tabbed agent route/draft/stale-load checks, offscreen light/dark layout and common ComboBox labels, stable navigation IDs, cross-process catalog/error overlay, isolated plugin configuration, prompt CAS and editor debounce", persistentPreferencesWritten = false, serviceCommandsExecuted = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel(); instructionSaveTimer.Stop(); heldLoad?.TrySetCanceled(); heldSave?.TrySetCanceled();
            if (outstanding is not null) { try { await outstanding.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { } }
            instructionCommandOverride = originalDispatch; instructionDrafts.Clear(); activeInstructions = null;
            instructionShowSavedOverride = null; instructionConflictChoiceOverride = null;
        }
    }
}
