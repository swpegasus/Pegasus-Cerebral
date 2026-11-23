using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

internal static class AgentHelperWorkflowChecks
{
    internal static int Run(string? captures, bool navigationOnly = false)
    {
        if (captures is not null)
        {
            if (!Path.IsPathFullyQualified(captures) || !Directory.Exists(captures)) throw new ArgumentException("Use an existing exact capture directory.");
            ScopedFilePolicy.RejectLinks(captures);
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-helper-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DesktopPreferenceDiagnostics.UseProcessLocalDirectory(root);
        _ = DesktopInstancePaths.UseAttachedExisting(root, AppContext.BaseDirectory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.ApplyTheme(app);
        var window = new IntegratorWindow(false, desktopPluginCatalog: new DesktopPluginCatalog(Path.Combine(root, "plugins")),
            desktopPluginDataDirectory: Path.Combine(root, "plugin-data"), seedBundledDesktopPlugins: false);
        var exit = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                if (!navigationOnly)
                {
                    var checks = await window.CheckAgentHelperWorkflowAsync(timeout.Token, captures);
                    Console.WriteLine("Helper workflow editor: " + checks + " deterministic/native offscreen checks passed; no service/profile/model call.");
                    var regression = await window.CheckExistingAgentDraftRegressionAsync(timeout.Token);
                    Console.WriteLine("Existing agent profile draft: " + regression + " deterministic/native offscreen checks passed.");
                }
                var accessibility = await IntegratorWindow.CheckConfigurationNavigationAccessibilityAsync(timeout.Token);
                Console.WriteLine("Configuration navigation: " + accessibility + " shown-window/render/automation-peer fixture checks passed; no service/model call.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); app.Shutdown(exit); }
        }));
        try { return app.Run(); }
        finally
        {
            window.Close();
            var full = Path.GetFullPath(root);
            if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cerebral-helper-workflow-", StringComparison.Ordinal))
                throw new InvalidOperationException("Helper fixture cleanup escaped its owned root.");
            ScopedFilePolicy.RejectLinks(full);
            Directory.Delete(full, recursive: true);
        }
    }
}

public sealed partial class IntegratorWindow
{
    internal Task<int> CheckExistingAgentDraftRegressionAsync(CancellationToken token) => CheckAgentProfileDraftAsync(token);

    internal async Task<int> CheckAgentHelperWorkflowAsync(CancellationToken token, string? captures)
    {
        const string workflowId = "pegasus.programming:develop";
        const string version = "1.3.13";
        // Fixture copied from the reviewed current feature descriptor, never a production default.
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"rootPath":{"type":"string","minLength":1,"maxLength":32768},"projectFile":{"type":"string","minLength":1,"maxLength":4096},"configuration":{"type":"string","enum":["Debug","Release"]},"selectedTools":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"string","maxLength":128}},"requiredChecks":{"type":"array","minItems":1,"maxItems":3,"items":{"type":"string","enum":["build","test","run"]}},"presentManifest":{"type":"string","minLength":1,"maxLength":4096},"contractTopic":{"type":"string","enum":["desktop-base","mount-view","new-conversation","draft","storage","conversation","response-settings","composer-command"]}},"required":["rootPath","projectFile"],"additionalProperties":false}
            """);
        var schema = schemaDocument.RootElement.Clone();
        var descriptor = new WorkflowDefinitionDescriptor(workflowId, version, "Fixture development workflow", schema,
            ["filesystem.read", "filesystem.write", "process.execute"]);
        var expectedInput = CerebralJson.Element(new
        {
            rootPath = @"C:\Fixture\ServiceR", projectFile = "Cerebral.GeneratedPlugin.Service.csproj", configuration = "Release",
            selectedTools = new[] { "file.read", "program.source.structure", "file.edit-range", "process.run" }, requiredChecks = new[] { "build" }
        });
        static AgentProfile Copy(AgentProfile value) => CerebralJson.Read<AgentProfile>(CerebralJson.Element(value));
        static string Snapshot(AgentProfile value) => JsonSerializer.Serialize(value, CerebralJson.Options);
        AgentProfile Original() => new()
        {
            Id = "workflow-helper", Revision = 8, Name = "Fixture helper", Role = "helper", ModelId = "model",
            FollowsDefaultModel = false, FollowsModelBudgets = false, ContextTokens = 6144, ResponseTokens = 1024,
            Capabilities = ["filesystem.read", "filesystem.write", "process.execute"],
            AllowedTools = ["file.read", "program.source.structure", "file.edit-range", "process.run"], AllowedRoots = [@"C:\Fixture"],
            PluginSettings = new(StringComparer.Ordinal) { ["fixture.unknown"] = CerebralJson.Element(new { untouched = new[] { "one", "two" } }) }
        };
        var model = new ModelRecord { Id = "model", Name = "Fixture model", ContextTokens = 8192, RecommendedContextTokens = 4096 };
        var settings = new HostSettings { DefaultModelId = "model", DefaultModelWorkerId = "local", ProjectsDirectory = @"C:\Fixture" };
        var checks = 0; var saves = 0; var reads = 0; long epoch = 5;
        var failCatalog = false; var revisionConflict = false;
        AgentProfile? submitted = null;
        var describeOverride = (Func<CommandRequest, CancellationToken, Task<CommandResult>>?)null;
        var catalogOverride = (Func<Task<CommandResult>>?)null;
        var saveOverride = (Func<Task<CommandResult>>?)null;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        CommandResult Describe(params WorkflowDefinitionDescriptor[] definitions) =>
            CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version, definitions });
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken cancellation)
        {
            if (request.Operation == "sdk.discover")
            {
                reads++;
                if (catalogOverride is not null) return catalogOverride();
                return Task.FromResult(failCatalog ? CommandResult.Fail("Fixture catalog unavailable") : CommandResult.Ok(new
                {
                    contract = WorkflowContract.Id, version = WorkflowContract.Version,
                    workflows = new[] { new { id = workflowId, version, description = "Current exact edition" },
                        new { id = workflowId, version = "1.3.12", description = "Older exact edition" } }
                }));
            }
            if (request.Operation == "sdk.describe")
            {
                reads++;
                return describeOverride?.Invoke(request, cancellation) ?? Task.FromResult(Describe(descriptor, descriptor with { Version = "1.3.12" }));
            }
            if (request.Operation != "agents.save") throw new InvalidOperationException("Unapproved fixture operation: " + request.Operation);
            saves++; submitted = CerebralJson.Read<AgentProfile>(request.Arguments);
            if (saveOverride is not null) return saveOverride();
            return Task.FromResult(revisionConflict ? CommandResult.Fail("Fixture profile revision conflict") :
                CommandResult.Ok(WithRevision()));
            AgentProfile WithRevision() { var saved = Copy(submitted!); saved.Revision++; return saved; }
        }
        AgentProfileEditor Create(AgentProfile original, Func<AgentWorkflowInputForm, bool?>? edit = null)
        {
            var editor = new AgentProfileEditor(this, original, settings, [], [model], null,
                (_, _) => Task.FromResult(new List<ModelRecord> { model }), Dispatch, () => epoch, epoch, true, token,
                editWorkflowInput: edit);
            new WindowInteropHelper(editor.Form).EnsureHandle();
            return editor;
        }
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        static CheckBox Included(AgentWorkflowInputForm input, string label) =>
            Children(input.Form).OfType<CheckBox>().Single(x => Equals(x.Content, "Include " + label));
        void Fill(AgentWorkflowInputForm input)
        {
            input.Form.SetValue(input.FieldId("rootPath"), @"C:\Fixture\ServiceR");
            input.Form.SetValue(input.FieldId("projectFile"), "Cerebral.GeneratedPlugin.Service.csproj");
            Included(input, "Configuration").IsChecked = true; input.Form.SetSelection(input.FieldId("configuration"), "Release");
            Included(input, "Selected tools").IsChecked = true;
            input.Form.SetValue(input.FieldId("selectedTools"), "file.read\nprogram.source.structure\nfile.edit-range\nprocess.run");
            Included(input, "Required checks").IsChecked = true; input.Form.SetValue(input.FieldId("requiredChecks"), "build");
        }
        async Task Select(AgentProfileEditor editor, string selectedVersion = version)
        {
            editor.Form.SetSelection("helperWorkflowMode", AgentHelperWorkflowEditor.Feature);
            await editor.HelperWorkflow.ReadCatalogAsync();
            Check(editor.Form.Selection("helperWorkflowChoice") == AgentHelperWorkflowEditor.Choose,
                "reading the catalog never chooses a first/latest workflow automatically");
            editor.Form.SetSelection("helperWorkflowChoice", workflowId + " · " + selectedVersion);
            await editor.HelperWorkflow.PendingRead.WaitAsync(token);
        }

        new WindowInteropHelper(this).EnsureHandle();
        var original = Original(); var baseline = Snapshot(original);
        using (var editor = Create(original))
        {
            var candidate = editor.BuildCandidate();
            Check(!candidate.PluginSettings.ContainsKey(AgentHelperWorkflowBinding.Key) && reads == 0,
                "ordinary Chat/default adds no binding or catalog call");
            Check(JsonElement.DeepEquals(candidate.PluginSettings["fixture.unknown"], original.PluginSettings["fixture.unknown"]),
                "unrelated plugin settings stay intact");
            editor.Form.DiscardDraftAndClose();
        }
        using (var editor = Create(original, input => { Fill(input); return true; }))
        {
            await Select(editor);
            Check(!editor.HelperWorkflow.CanSave && saves == 0, "new exact workflow requires configured inputs before a parent save");
            editor.HelperWorkflow.ConfigureInputs();
            var candidate = editor.BuildCandidate();
            var binding = AgentHelperWorkflowBinding.Read(candidate.PluginSettings[AgentHelperWorkflowBinding.Key]);
            Check(binding.DefinitionId == workflowId && binding.DefinitionVersion == version &&
                JsonElement.DeepEquals(binding.Input, expectedInput), "schema-backed controls form the exact selected edition and typed inputs");
            Check(!binding.Input.TryGetProperty("projectId", out _) &&
                candidate.PluginSettings[AgentHelperWorkflowBinding.Key].EnumerateObject().Count() == 4,
                "Develop receives no synthetic project scope and binding has only the closed four fields");
            Check(candidate.Capabilities.SequenceEqual(original.Capabilities) && candidate.AllowedTools.SequenceEqual(original.AllowedTools) &&
                candidate.AllowedRoots.SequenceEqual(original.AllowedRoots), "workflow configuration never grants tools, capabilities or roots");
            if (captures is not null)
            {
                editor.EditorTab("helperWorkflow").IsSelected = true;
                CaptureHelperForm(editor.Form, Path.Combine(captures, "helper-workflow-editor.png"));
            }
            Check(await editor.SaveAsync() && saves == 1 && submitted?.Id == original.Id && submitted.Revision == 8 &&
                editor.Acknowledged?.Revision == 9, "one ordinary agents.save carries exact profile ID/revision and acknowledged state");
            Check(JsonElement.DeepEquals(submitted!.PluginSettings[AgentHelperWorkflowBinding.Key], candidate.PluginSettings[AgentHelperWorkflowBinding.Key]) &&
                JsonElement.DeepEquals(submitted.PluginSettings["fixture.unknown"], original.PluginSettings["fixture.unknown"]),
                "normal save preserves workflow inputs and unrelated plugin values");
            editor.Form.DiscardDraftAndClose();
        }
        Check(Snapshot(original) == baseline, "successful draft save never mutates the supplied profile body");
        var savedProfile = Original(); savedProfile.PluginSettings[AgentHelperWorkflowBinding.Key] =
            new AgentHelperWorkflowBinding(workflowId, version, expectedInput).ToSettings();
        using (var editor = Create(savedProfile, input =>
        {
            Check(Included(input, "Selected tools").IsChecked == true && Included(input, "Required checks").IsChecked == true &&
                Included(input, "Present manifest").IsChecked == false, "saved optional arrays/inclusion round-trip; absent optional values remain omitted");
            Check(JsonElement.DeepEquals(input.ReadInput(), expectedInput), "opening native input controls preserves the complete saved input");
            Included(input, "Required checks").IsChecked = false;
            if (captures is not null) CaptureHelperForm(input.Form, Path.Combine(captures, "helper-workflow-inputs.png"));
            return true;
        }))
        {
            await editor.HelperWorkflow.ReadCatalogAsync();
            editor.HelperWorkflow.ConfigureInputs();
            var input = AgentHelperWorkflowBinding.Read(editor.BuildCandidate().PluginSettings[AgentHelperWorkflowBinding.Key]).Input;
            Check(!input.TryGetProperty("requiredChecks", out _) && input.GetProperty("selectedTools").GetArrayLength() == 4,
                "explicit optional omission changes only that field and retains the other array");
            editor.Form.DiscardDraftAndClose();
        }
        var savedSnapshot = Snapshot(savedProfile); var savesBefore = saves;
        using (var editor = Create(savedProfile, input =>
        {
            Fill(input); input.Form.SetValue(input.FieldId("rootPath"), @"C:\Cancelled"); return false;
        }))
        {
            await editor.HelperWorkflow.ReadCatalogAsync(); editor.HelperWorkflow.ConfigureInputs();
            Check(Snapshot(editor.BuildCandidate()) == savedSnapshot && saves == savesBefore,
                "cancelled nested inputs preserve the exact saved binding without dispatch");
            editor.Form.DiscardDraftAndClose();
        }
        using (var editor = Create(original, input => { Fill(input); return false; }))
        {
            await Select(editor); editor.HelperWorkflow.ConfigureInputs(); editor.Form.DiscardDraftAndClose();
            Check(saves == savesBefore && Snapshot(original) == baseline, "parent discard after a workflow selection never saves or mutates its original");
        }
        var noGrant = Original(); noGrant.Capabilities.Remove("process.execute");
        using (var editor = Create(noGrant, input => { Fill(input); return true; }))
        {
            await Select(editor); editor.HelperWorkflow.ConfigureInputs();
            var refused = false; try { editor.BuildCandidate(); } catch (ArgumentException) { refused = true; }
            Check(refused && saves == savesBefore && !noGrant.Capabilities.Contains("process.execute"),
                "a missing required capability blocks the candidate without granting it");
            editor.Form.DiscardDraftAndClose();
        }
        foreach (var unknown in new[] { false, true })
        {
            var retained = Copy(savedProfile);
            if (unknown) retained.PluginSettings[AgentHelperWorkflowBinding.Key] = CerebralJson.Element(new { schemaVersion = 99, opaqueFuture = "preserve" });
            failCatalog = true;
            using var editor = Create(retained);
            await editor.HelperWorkflow.ReadCatalogAsync();
            Check(Snapshot(editor.BuildCandidate()) == Snapshot(retained),
                "unavailable catalog preserves existing valid or unknown binding exactly");
            editor.Form.SetSelection("helperWorkflowMode", AgentHelperWorkflowEditor.Chat);
            var candidate = editor.BuildCandidate();
            Check(!candidate.PluginSettings.ContainsKey(AgentHelperWorkflowBinding.Key) &&
                JsonElement.DeepEquals(candidate.PluginSettings["fixture.unknown"], retained.PluginSettings["fixture.unknown"]),
                "explicit ordinary Chat removes only the coordination override");
            editor.Form.DiscardDraftAndClose(); failCatalog = false;
        }
        using (var editor = Create(original))
        {
            describeOverride = (_, _) => Task.FromResult(Describe(descriptor with
                { InputSchema = CerebralJson.Element(new { type = "object", properties = new { nested = new { type = "object" } } }) }));
            await Select(editor);
            Check(!editor.HelperWorkflow.CanSave && saves == savesBefore, "unsupported input shapes remain unavailable, never raw JSON or silent field loss");
            editor.Form.DiscardDraftAndClose(); describeOverride = null;
        }
        var delayed = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var editor = Create(original))
        {
            describeOverride = (_, _) => delayed.Task;
            editor.Form.SetSelection("helperWorkflowMode", AgentHelperWorkflowEditor.Feature);
            await editor.HelperWorkflow.ReadCatalogAsync();
            editor.Form.SetSelection("helperWorkflowChoice", workflowId + " · " + version);
            var pending = editor.HelperWorkflow.PendingRead;
            epoch++; delayed.SetResult(Describe(descriptor));
            await pending.WaitAsync(token);
            var refused = false; try { await editor.SaveAsync(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && saves == savesBefore && editor.Submitted is null,
                "a delayed schema from an old owner cannot enable or dispatch a profile save");
            editor.Form.DiscardDraftAndClose(); describeOverride = null;
        }
        var lateSchema = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptionReads = 0;
        using (var editor = Create(original, input => { Fill(input); return true; }))
        {
            describeOverride = (_, _) => ++descriptionReads == 1 ? lateSchema.Task :
                Task.FromResult(Describe(descriptor, descriptor with { Version = "1.3.12" }));
            editor.Form.SetSelection("helperWorkflowMode", AgentHelperWorkflowEditor.Feature);
            await editor.HelperWorkflow.ReadCatalogAsync();
            editor.Form.SetSelection("helperWorkflowChoice", workflowId + " · " + version);
            var first = editor.HelperWorkflow.PendingRead;
            editor.Form.SetSelection("helperWorkflowChoice", workflowId + " · 1.3.12");
            await editor.HelperWorkflow.PendingRead.WaitAsync(token);
            editor.HelperWorkflow.ConfigureInputs();
            lateSchema.SetResult(Describe(descriptor));
            await first.WaitAsync(token);
            Check(AgentHelperWorkflowBinding.Read(editor.BuildCandidate().PluginSettings[AgentHelperWorkflowBinding.Key]).DefinitionVersion == "1.3.12",
                "a superseded description cannot replace the user's exact version selection");
            editor.Form.DiscardDraftAndClose(); describeOverride = null;
        }
        using (var editor = Create(original, input => { Fill(input); return true; }))
        {
            await Select(editor); editor.HelperWorkflow.ConfigureInputs(); revisionConflict = true;
            Check(!await editor.SaveAsync() && editor.SaveUncertain && editor.Submitted?.Revision == 8 &&
                editor.Submitted.PluginSettings.ContainsKey(AgentHelperWorkflowBinding.Key),
                "revision conflict retains submitted helper workflow draft and original revision");
            var attempts = saves;
            Check(!await editor.SaveAsync() && saves == attempts, "a conflicted/unconfirmed parent save is not automatically replayed");
            editor.Form.DiscardDraftAndClose(); revisionConflict = false;
        }
        var lateCatalog = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingSave = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var editor = Create(savedProfile))
        {
            catalogOverride = () => lateCatalog.Task; saveOverride = () => pendingSave.Task;
            var reading = editor.HelperWorkflow.ReadCatalogAsync();
            var saving = editor.SaveAsync();
            var workflowChoice = Children(editor.EditorTab("helperWorkflow")).OfType<ComboBox>().Last();
            Check(!workflowChoice.IsEnabled, "workflow selector freezes while the unchanged parent draft is saving");
            lateCatalog.SetResult(CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version,
                workflows = Array.Empty<object>() }));
            await reading.WaitAsync(token);
            var submitButton = Children(editor.Form).OfType<Button>().Single(x => Equals(x.Content, "Save"));
            Check(!workflowChoice.IsEnabled && !submitButton.IsEnabled,
                "a late catalog completion cannot re-enable inactive workflow controls or Save during the submitted mutation");
            var acknowledged = Copy(submitted!); acknowledged.Revision++;
            pendingSave.SetResult(CommandResult.Ok(acknowledged));
            Check(await saving, "the original pending save retains its authoritative acknowledgement after the ignored catalog read");
            editor.Form.DiscardDraftAndClose(); catalogOverride = null; saveOverride = null;
        }
        using (var input = new DummyInputOwner(this, descriptor))
        {
            Fill(input.Input); input.Input.Form.SetValue(input.Input.FieldId("projectFile"), "");
            var refused = false; try { input.Input.ReadInput(); } catch (ArgumentException) { refused = true; }
            Check(refused, "required field constraints fail before a candidate can be formed");
            Fill(input.Input); input.Input.Form.SetValue(input.Input.FieldId("requiredChecks"), "invented");
            refused = false; try { input.Input.ReadInput(); } catch (ArgumentException) { refused = true; }
            Check(refused, "string-array item enums are enforced locally");
        }
        WorkflowDefinitionDescriptor NumberDefinition(string type, string bounds = "")
        {
            using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"" + type + "\"" +
                (bounds.Length == 0 ? "" : "," + bounds) + "}},\"required\":[\"value\"],\"additionalProperties\":false}");
            return descriptor with { InputSchema = json.RootElement.Clone() };
        }
        static JsonElement NumberInput(string token)
        {
            using var json = JsonDocument.Parse("{\"value\":" + token + "}");
            return json.RootElement.Clone();
        }
        static bool RefusesNumber(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
        using (var input = new DummyInputOwner(this, NumberDefinition("number"), NumberInput("9007199254740993")))
        {
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "9007199254740993",
                "an unchanged number beyond IEEE 754 integer precision round-trips exactly");
            input.Input.Form.SetValue(input.Input.FieldId("value"), "9007199254740995");
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "9007199254740995",
                "an edited large number keeps its exact JSON value");
            input.Input.Form.SetValue(input.Input.FieldId("value"), "NaN");
            Check(RefusesNumber(() => input.Input.ReadInput()), "non-JSON and nonfinite number text remains unavailable");
        }
        foreach (var type in new[] { "integer", "number" })
        {
            using var high = new DummyInputOwner(this, NumberDefinition(type, "\"maximum\":9007199254740992"), NumberInput("9007199254740992"));
            Check(high.Input.ReadInput().GetProperty("value").GetRawText() == "9007199254740992",
                type + " retains the exact inclusive upper boundary");
            high.Input.Form.SetValue(high.Input.FieldId("value"), "9007199254740993");
            Check(RefusesNumber(() => high.Input.ReadInput()), type + " rejects maximum plus one without rounded comparison");
            using var low = new DummyInputOwner(this, NumberDefinition(type, "\"minimum\":-9007199254740992"), NumberInput("-9007199254740992"));
            Check(low.Input.ReadInput().GetProperty("value").GetRawText() == "-9007199254740992",
                type + " retains the exact inclusive negative boundary");
            low.Input.Form.SetValue(low.Input.FieldId("value"), "-9007199254740993");
            Check(RefusesNumber(() => low.Input.ReadInput()), type + " rejects minimum minus one without rounded comparison");
        }
        using (var input = new DummyInputOwner(this, NumberDefinition("number", "\"maximum\":0.10000000000000000000000000001"),
            NumberInput("0.10000000000000000000000000001")))
        {
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "0.10000000000000000000000000001",
                "high-precision fractional boundary round-trips without decimal or double rounding");
            input.Input.Form.SetValue(input.Input.FieldId("value"), "0.10000000000000000000000000002");
            Check(RefusesNumber(() => input.Input.ReadInput()), "fractional boundary plus one final decimal unit is rejected");
        }
        using (var input = new DummyInputOwner(this, NumberDefinition("number"), NumberInput("9.007199254740993e15")))
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "9.007199254740993e15",
                "an unchanged exponent token is retained exactly");
        Check(RefusesNumber(() =>
        {
            using var input = new DummyInputOwner(this, NumberDefinition("number", "\"maximum\":9007199254740992"),
                NumberInput("9.007199254740993e15"));
        }), "equivalent exponent notation cannot bypass the exact maximum");
        using (var input = new DummyInputOwner(this, NumberDefinition("number", "\"maximum\":1e-400"), NumberInput("1e-400")))
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "1e-400",
                "a finite-underflow decimal token stays nonzero and exact");
        Check(RefusesNumber(() =>
        {
            using var input = new DummyInputOwner(this, NumberDefinition("number", "\"maximum\":0"), NumberInput("1e-400"));
        }), "finite-underflow input cannot round down to bypass a zero maximum");
        using (var input = new DummyInputOwner(this, NumberDefinition("number", "\"minimum\":0,\"maximum\":0"), NumberInput("-0.000e2")))
            Check(input.Input.ReadInput().GetProperty("value").GetRawText() == "-0.000e2",
                "signed zero compares as zero while preserving its unchanged token");
        Check(RefusesNumber(() =>
        {
            using var input = new DummyInputOwner(this, NumberDefinition("number", "\"minimum\":9007199254740993,\"maximum\":9007199254740992"));
        }), "an inconsistent large-number schema range is rejected exactly");
        var retainedNumeric = Copy(savedProfile);
        retainedNumeric.PluginSettings[AgentHelperWorkflowBinding.Key] = new AgentHelperWorkflowBinding(workflowId, version, NumberInput("0e-4097")).ToSettings();
        var retainedNumericSnapshot = Snapshot(retainedNumeric);
        using (var editor = Create(retainedNumeric))
        {
            describeOverride = (_, _) => Task.FromResult(Describe(NumberDefinition("number")));
            await editor.HelperWorkflow.ReadCatalogAsync();
            Check(Snapshot(editor.BuildCandidate()) == retainedNumericSnapshot,
                "an unsupported numeric exponent leaves the saved binding and unrelated settings opaque and unchanged");
            editor.Form.DiscardDraftAndClose(); describeOverride = null;
        }
        Check(Snapshot(savedProfile) == savedSnapshot, "all helper fixture paths preserve saved profile and unknown settings");
        return checks;
    }

    private sealed class DummyInputOwner : IDisposable
    {
        internal AgentWorkflowInputForm Input { get; }
        internal DummyInputOwner(Window owner, WorkflowDefinitionDescriptor descriptor, JsonElement? input = null) =>
            Input = new(owner, descriptor, input ?? WorkflowContract.EmptyObject);
        public void Dispose() => Input.Form.DiscardDraftAndClose();
    }

    private static void CaptureHelperForm(FieldsDialog form, string path)
    {
        var layout = (FrameworkElement)form.Content;
        layout.Measure(new Size(680, 670)); layout.Arrange(new Rect(0, 0, 680, 670)); layout.UpdateLayout();
        var bitmap = new RenderTargetBitmap(680, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); encoder.Save(file);
    }
}
