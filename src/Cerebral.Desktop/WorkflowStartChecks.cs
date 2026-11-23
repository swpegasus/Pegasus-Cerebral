using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Actual launch-form and Work-adapter fixtures with controlled owner transport; no service or model execution.</summary>
internal static class WorkflowStartChecks
{
    private const string Definition = "fixture.workflow:run";
    private const string OtherDefinition = "fixture.other:run";
    private const string OriginalRequest = "\r\nKeep GeneratedServicePlugin.cs and its complete behavior.\nRepair one coherent unit — do not rename it.\r\n";
    private const string SeedJson = """{"title":"saved","requiredChecks":["build"],"opaque":{"nested":[null,false,0,{"big":123456789012345678901234567890,"tiny":0e-4097}]},"optionalNull":null,"optionalFalse":false}""";

    internal static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Workflow start: " + label); checks++; }
        void Reject(Action body, string label)
        {
            var refused = false;
            try { body(); } catch (ArgumentException) { refused = true; } catch (InvalidOperationException) { refused = true; }
            Check(refused, label);
        }

        AgentWorkflowBinding detached;
        using (var document = JsonDocument.Parse(Envelope(SeedJson))) detached = AgentWorkflowBinding.Read(document.RootElement);
        Check(detached.DefinitionId == Definition && detached.DefinitionVersion == "2.0.0" && detached.Input.GetRawText() == SeedJson,
            "the public codec preserves and detaches the complete exact configured input");
        var roundTrip = AgentWorkflowBinding.Read(detached.ToSettings()).Input;
        Check(JsonElement.DeepEquals(roundTrip, detached.Input) &&
            roundTrip.GetProperty("opaque").GetProperty("nested")[3].GetProperty("tiny").GetRawText() == "0e-4097" &&
            roundTrip.GetProperty("opaque").GetProperty("nested")[3].GetProperty("big").GetRawText() == "123456789012345678901234567890",
            "the existing formatted setting codec retains complete values and exact numeric tokens");
        foreach (var invalid in new[]
        {
            "[]", "null", "{}",
            """{"schemaVersion":2,"definitionId":"fixture.workflow:run","definitionVersion":"2.0.0","input":{}}""",
            """{"schemaVersion":"1","definitionId":"fixture.workflow:run","definitionVersion":"2.0.0","input":{}}""",
            """{"schemaVersion":1,"definitionId":"fixture.workflow:run","definitionVersion":"2.0.0","input":[]}""",
            """{"schemaVersion":1,"definitionId":"fixture.workflow:run","definitionVersion":"2.0.0","input":{},"extra":true}""",
            """{"schemaVersion":1,"definitionId":"fixture.workflow:run","definitionVersion":"2.0.0","input":{},"input":{}}""",
            """{"schemaVersion":1,"definitionId":"","definitionVersion":"2.0.0","input":{}}""",
            """{"schemaVersion":1,"definitionId":"fixture.workflow:run","definitionVersion":"","input":{}}""",
            """{"schemaVersion":1,"definitionId":"fixture\u0000run","definitionVersion":"2.0.0","input":{}}"""
        })
        {
            using var document = JsonDocument.Parse(invalid);
            Reject(() => AgentWorkflowBinding.Read(document.RootElement), "malformed or unsupported binding is refused");
        }
        var boundary = Envelope("{\"pad\":\"\"}");
        var padding = AgentWorkflowBinding.MaximumBytes - Encoding.UTF8.GetByteCount(boundary);
        using (var document = JsonDocument.Parse(Envelope("{\"pad\":\"" + new string('x', padding) + "\"}")))
            Check(AgentWorkflowBinding.Read(document.RootElement).Input.GetProperty("pad").GetString()!.Length == padding,
                "the exact saved-binding byte boundary is admitted");
        using (var document = JsonDocument.Parse(Envelope("{\"pad\":\"" + new string('x', padding + 1) + "\"}")))
            Reject(() => AgentWorkflowBinding.Read(document.RootElement), "one byte beyond the saved-binding bound is refused");

        var profiles = Profiles(detached);
        var configuredInput = AgentWorkflowBinding.Read(profiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey]).Input;
        var catalog = new FixtureCatalog();
        var before = JsonSerializer.Serialize(profiles, CerebralJson.Options);
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync();
            form.RequestBox.Text = OriginalRequest;
            Check(AutomationProperties.GetAutomationId(form.RequestBox) == "WorkflowComposer", "the request stays in the protected user-input control");
            ((TextBox)form.RequiredFields["title"]).Text = "lead draft";
            Check(form.BuildDraft().ProfileId == "lead", "the existing lead remains the initial agent without borrowing helper grants");
            form.SelectAgent("helper"); await form.PendingRead;
            var untouched = form.BuildDraft();
            Check(untouched.ProfileId == "helper" && untouched.DefinitionId == Definition && untouched.DefinitionVersion == "2.0.0",
                "selecting a configured helper chooses the exact workflow edition");
            Check(untouched.UserText == OriginalRequest && untouched.Input.GetRawText() == configuredInput.GetRawText(),
                "unmodified request bytes and complete configured input reach the actual form result");
            var retainedSeedId = AutomationProperties.GetAutomationId(form.Status);
            Check(retainedSeedId == DraftStatusId(untouched.Input) && !retainedSeedId.Contains("optionalFalse", StringComparison.Ordinal),
                "the ready automation identity fingerprints the entire draft without exposing saved input fields");
            var title = (TextBox)form.RequiredFields["title"];
            Check(AutomationProperties.GetAutomationId(title).StartsWith("WorkflowStartInput-", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(title)), "required controls have stable identifiers and accessible names");
            title.Text = "edited";
            var edited = form.BuildDraft();
            Check(edited.Input.GetProperty("title").GetString() == "edited", "the edited required field is replaced");
            Check(AutomationProperties.GetAutomationId(form.Status) == DraftStatusId(edited.Input) &&
                AutomationProperties.GetAutomationId(form.Status) != retainedSeedId,
                "a changed draft has a new automation identity including untouched structured inputs");
            foreach (var name in new[] { "requiredChecks", "opaque", "optionalNull", "optionalFalse" })
                Check(edited.Input.GetProperty(name).GetRawText() == configuredInput.GetProperty(name).GetRawText(),
                    "required-field edits preserve optional property " + name);
            Check(!edited.Input.TryGetProperty("omitted", out _), "an omitted optional field remains omitted");
            title.Text = "";
            Reject(() => form.BuildDraft(), "an empty required string is refused without discarding its optional input");
            Check(AutomationProperties.GetAutomationId(form.Status) == "WorkflowStartStatus" && !form.StartButton.IsEnabled,
                "an invalid draft clears its previous ready automation identity");
            title.Text = new string('x', AgentWorkflowBinding.MaximumBytes + 1);
            Check(Encoding.UTF8.GetByteCount(form.BuildDraft().Input.GetRawText()) > AgentWorkflowBinding.MaximumBytes,
                "a one-time run retains the submission bound rather than the smaller saved-setting bound");
            title.Text = "edited";
            form.SelectDefinition(Definition, "1.0.0"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "manual";
            Check(!form.BuildDraft().Input.TryGetProperty("opaque", out _), "a different exact version cannot inherit another version's optional input");
            form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            Check(form.BuildDraft().Input.GetProperty("title").GetString() == "edited", "switching back restores only the matching agent/version draft");
            Check(form.BuildDraft().UserText == OriginalRequest, "selection changes do not rewrite the entered request");
            profiles[1].Revision++;
            Reject(() => form.BuildDraft(), "an observed profile revision change invalidates the form before dispatch");
        }
        profiles[1].Revision--;
        Check(JsonSerializer.Serialize(profiles, CerebralJson.Options) == before, "the form does not change profile grants, roles, budgets or settings");
        Check(catalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"), "form discovery performs no profile saves or execution calls");

        var optionalCatalog = new FixtureCatalog
        {
            SchemaOverride = Parse("""
                {"type":"object","properties":{
                  "title":{"type":"string","minLength":1},
                  "presentManifest":{"type":"string","title":"Plugin manifest","description":"Path relative to the project root.","minLength":1,"maxLength":4096},
                  "configuration":{"type":"string","enum":["Debug","Release"],"default":"Release"},
                  "optionalFalse":{"type":"boolean"},
                  "requiredChecks":{"type":"array","items":{"type":"string"}}
                },"required":["title"],"additionalProperties":true}
                """)
        };
        using (var form = new WorkflowStartForm(null, optionalCatalog.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync();
            form.RequestBox.Text = OriginalRequest;
            ((TextBox)form.RequiredFields["title"]).Text = "one-time plugin build";
            var manifest = form.OptionalFields["presentManifest"];
            var configuration = form.OptionalFields["configuration"];
            Check(manifest.Include.IsChecked == false && !manifest.Editor.IsEnabled &&
                !form.BuildDraft().Input.TryGetProperty("presentManifest", out _) &&
                !form.BuildDraft().Input.TryGetProperty("configuration", out _),
                "unselected optional fields are omitted even when the schema advertises a default");
            Check(AutomationProperties.GetName(manifest.Editor) == "Plugin manifest" &&
                (string)manifest.Editor.ToolTip == "Path relative to the project root." &&
                AutomationProperties.GetAutomationId(manifest.Include).StartsWith("WorkflowStartInclude-", StringComparison.Ordinal),
                "the per-run editor exposes the supplied label, help and a stable inclusion control");
            manifest.Include.IsChecked = true;
            Check(manifest.Editor.IsEnabled && !form.CanSubmit && form.Status.Text.Contains("presentManifest", StringComparison.Ordinal),
                "including an empty manifest blocks submit with an input-specific message");
            const string builtManifest = "bin/Release/net10.0-windows/desktop-plugin.json";
            ((TextBox)manifest.Editor).Text = builtManifest;
            configuration.Include.IsChecked = true;
            var oneTime = form.BuildDraft().Input;
            Check(oneTime.GetProperty("presentManifest").GetString() == builtManifest &&
                oneTime.GetProperty("configuration").GetString() == "Release",
                "the exact optional manifest and selected default are included in the submission draft");
            manifest.Include.IsChecked = false;
            Check(!form.BuildDraft().Input.TryGetProperty("presentManifest", out _), "unchecking excludes a value edited for this run");
            manifest.Include.IsChecked = true;
            Check(form.BuildDraft().Input.GetProperty("presentManifest").GetString() == builtManifest,
                "checking again restores the per-run edit");

            form.SelectAgent("helper"); await form.PendingRead;
            Check(form.OptionalFields["presentManifest"].Include.IsChecked == false &&
                form.BuildDraft().Input.GetRawText() == configuredInput.GetRawText(),
                "another agent has its own per-run draft and retains the exact unmodified preset");
            form.OptionalFields["optionalFalse"].Include.IsChecked = false;
            var omitted = form.BuildDraft().Input;
            Check(!omitted.TryGetProperty("optionalFalse", out _) && !form.OptionalFields.ContainsKey("requiredChecks") &&
                omitted.GetProperty("requiredChecks").GetRawText() == configuredInput.GetProperty("requiredChecks").GetRawText() &&
                omitted.GetProperty("opaque").GetRawText() == configuredInput.GetProperty("opaque").GetRawText(),
                "removing one saved scalar for this run preserves untouched structured values and their numeric tokens");
            form.OptionalFields["optionalFalse"].Include.IsChecked = true;
            Check(form.BuildDraft().Input.GetRawText() == configuredInput.GetRawText(),
                "including an unedited saved false again restores the exact preset bytes");
        }
        Check(JsonSerializer.Serialize(profiles, CerebralJson.Options) == before &&
            optionalCatalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
            "per-run optional inputs do not save agent settings or dispatch work");

        var closedArraySchema = Parse("""
            {"type":"object","properties":{
              "title":{"type":"string","minLength":1},
              "requiredChecks":{"type":"array","title":"Required checks","minItems":1,"maxItems":3,
                "items":{"type":"string","enum":["build","test","run"]}},
              "selectedTools":{"type":"array","items":{"type":"string"}}
            },"required":["title"],"additionalProperties":true}
            """);
        var closedArrayCatalog = new FixtureCatalog { SchemaOverride = closedArraySchema };
        var freshProfiles = Profiles(new(Definition, "2.0.0", WorkflowContract.EmptyObject));
        using (var form = new WorkflowStartForm(null, closedArrayCatalog.Dispatch, () => freshProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest;
            ((TextBox)form.RequiredFields["title"]).Text = "One coherent run";
            Check(form.OptionalFields.ContainsKey("requiredChecks") && !form.OptionalFields.ContainsKey("selectedTools"),
                "a finite string enum array has native controls while an open string array still requires saved input");
            var field = form.OptionalFields["requiredChecks"];
            var choices = ((StackPanel)field.Editor).Children.Cast<CheckBox>().ToArray();
            Check(choices.Select(x => x.Content as string).SequenceEqual(new[] { "build", "test", "run" }) &&
                choices.All(x => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(x)) &&
                    AutomationProperties.GetAutomationId(x).Contains("-Choice-", StringComparison.Ordinal)),
                "closed choices are labeled accessible checkboxes in schema order");
            field.Include.IsChecked = true;
            Check(!form.CanSubmit && form.Status.Text.Contains("requiredChecks", StringComparison.Ordinal),
                "including an empty min-one array blocks the start");
            choices[0].IsChecked = true;
            var buildOnly = form.BuildDraft().Input;
            Check(buildOnly.GetProperty("requiredChecks").GetRawText() == "[\"build\"]" &&
                !buildOnly.TryGetProperty("selectedTools", out _),
                "selecting build only emits the exact bounded array and does not choose run by default");
            choices[0].IsChecked = false;
            Check(!form.CanSubmit, "removing the last required check blocks the draft again");
            choices[1].IsChecked = true;
            choices[2].IsChecked = true;
            Check(form.BuildDraft().Input.GetProperty("requiredChecks").GetRawText() == "[\"test\",\"run\"]",
                "two selected checks serialize once each in declared order");
            field.Include.IsChecked = false;
            Check(!form.BuildDraft().Input.TryGetProperty("requiredChecks", out _),
                "deselecting an optional array leaves the workflow default to the backend");
            field.Include.IsChecked = true;
            Check(form.BuildDraft().Input.GetProperty("requiredChecks").GetRawText() == "[\"test\",\"run\"]",
                "reselecting restores the same per-run choices");
            var oldChoice = choices[0];
            form.SelectAgent("helper"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "Helper-only draft";
            var helperBefore = form.BuildDraft();
            Check(helperBefore.ProfileId == "helper" && helperBefore.Input.GetProperty("title").GetString() == "Helper-only draft" &&
                !helperBefore.Input.TryGetProperty("requiredChecks", out _),
                "the newly selected helper has its own valid title and no selected checks");
            oldChoice.IsChecked = true;
            var helperAfter = form.BuildDraft();
            Check(helperAfter.ProfileId == "helper" && helperAfter.Input.GetRawText() == helperBefore.Input.GetRawText() &&
                !helperAfter.Input.TryGetProperty("requiredChecks", out _),
                "a late event from the previous agent's detached control cannot edit the current agent draft");
        }
        Check(closedArrayCatalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
            "closed array editing issues only local fixture discovery reads");

        var savedArray = Parse("""{"title":"saved","requiredChecks":["run","build"],"opaque":{"keep":1e+4097}}""");
        var savedArrayProfiles = Profiles(new(Definition, "2.0.0", savedArray));
        var acceptedSavedArray = AgentWorkflowBinding.Read(
            savedArrayProfiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey]).Input;
        using (var form = new WorkflowStartForm(null, closedArrayCatalog.Dispatch, () => savedArrayProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            var field = form.OptionalFields["requiredChecks"];
            var choices = ((StackPanel)field.Editor).Children.Cast<CheckBox>().ToArray();
            Check(field.Include.IsChecked == true && choices[0].IsChecked == true && choices[1].IsChecked == false &&
                choices[2].IsChecked == true, "saved enum array selects its exact known choices");
            var untouchedSavedArray = form.BuildDraft().Input;
            Check(untouchedSavedArray.GetRawText() == acceptedSavedArray.GetRawText() &&
                untouchedSavedArray.GetProperty("requiredChecks").EnumerateArray().Select(x => x.GetString() ?? "")
                    .SequenceEqual(new[] { "run", "build" }) &&
                untouchedSavedArray.GetProperty("opaque").GetProperty("keep").GetRawText() ==
                    acceptedSavedArray.GetProperty("opaque").GetProperty("keep").GetRawText(),
                "untouched saved enum array keeps accepted binding bytes, choice order and opaque numeric token");
            field.Include.IsChecked = false;
            Check(!form.BuildDraft().Input.TryGetProperty("requiredChecks", out _),
                "deselecting a saved array removes that property only");
            field.Include.IsChecked = true;
            Check(form.BuildDraft().Input.GetRawText() == acceptedSavedArray.GetRawText(),
                "reselecting an untouched saved array restores its exact accepted binding bytes");
        }
        var reviewDescriptor = new WorkflowDefinitionDescriptor(Definition, "2.0.0", "Accepted workflow", closedArraySchema, []);
        var reviewProfilesBefore = JsonSerializer.Serialize(freshProfiles, CerebralJson.Options);
        var reviewCurrent = true;
        using (var form = new WorkflowStartForm(null, closedArrayCatalog.Dispatch, () => freshProfiles,
            () => reviewCurrent, deadline.Token,
            new("helper", reviewDescriptor, acceptedSavedArray, OriginalRequest)))
        {
            await form.InitializeAsync();
            var reviewed = form.BuildDraft();
            Check(!form.AgentSelector.IsEnabled && !form.DefinitionSelector.IsEnabled && form.RequestBox.IsReadOnly &&
                form.RequestBox.Text == OriginalRequest && reviewed.ProfileId == "helper" &&
                reviewed.DefinitionId == Definition && reviewed.DefinitionVersion == "2.0.0" &&
                reviewed.Input.GetRawText() == acceptedSavedArray.GetRawText(),
                "exact continuation review locks identity and visible text and uses the accepted input instead of current profile settings");
            Check(form.OptionalFields["requiredChecks"].Include.IsChecked == true &&
                ((StackPanel)form.OptionalFields["requiredChecks"].Editor).Children.Cast<CheckBox>()
                    .Select(x => x.IsChecked).SequenceEqual(new bool?[] { true, false, true }),
                "exact continuation review exposes the accepted enum-array choices for visible editing");
            ((CheckBox)((StackPanel)form.OptionalFields["requiredChecks"].Editor).Children[2]).IsChecked = false;
            form.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(form.AcceptedDraft is { } acceptedReview &&
                acceptedReview.Input.GetProperty("requiredChecks").GetRawText() == "[\"build\"]" &&
                acceptedReview.UserText == OriginalRequest,
                "the visible review accepts only the newly selected check and keeps the ordinary composer text");
        }
        using (var form = new WorkflowStartForm(null, closedArrayCatalog.Dispatch, () => freshProfiles,
            () => reviewCurrent, deadline.Token,
            new("helper", reviewDescriptor, acceptedSavedArray, OriginalRequest)))
        {
            await form.InitializeAsync();
            reviewCurrent = false;
            Check(!form.CanSubmit && form.AcceptedDraft is null,
                "a changed continuation owner cannot accept the displayed input or create a submission");
        }
        var reviewDrift = new FixtureCatalog { SchemaOverride = Parse(closedArraySchema.GetRawText()
            .Replace("\"maxItems\":3", "\"maxItems\":2", StringComparison.Ordinal)) };
        using (var form = new WorkflowStartForm(null, reviewDrift.Dispatch, () => freshProfiles, () => true,
            deadline.Token, new("helper", reviewDescriptor, acceptedSavedArray, OriginalRequest)))
        {
            await form.InitializeAsync();
            Check(!form.CanSubmit && form.Status.Text.Contains("declaration changed", StringComparison.Ordinal) &&
                form.AcceptedDraft is null, "a changed reviewed schema fails before a follow-up input is accepted");
        }
        Check(reviewDrift.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
            "exact input review performs catalog reads without a workflow or profile effect");
        Check(JsonSerializer.Serialize(freshProfiles, CerebralJson.Options) == reviewProfilesBefore &&
            closedArrayCatalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
            "accepted or cancelled input review changes no profile and issues no submission command");

        foreach (var invalid in new[] { "[\"unknown\"]", "[\"build\",\"build\"]", "[]", "[\"build\",\"test\",\"run\",\"build\"]" })
        {
            var input = Parse("{\"title\":\"saved\",\"requiredChecks\":" + invalid + "}");
            var invalidProfiles = Profiles(new(Definition, "2.0.0", input));
            using var form = new WorkflowStartForm(null, closedArrayCatalog.Dispatch, () => invalidProfiles, () => true, deadline.Token);
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Check(!form.CanSubmit && form.Status.Text.Contains("requiredChecks", StringComparison.Ordinal),
                "an invalid or duplicate saved array fails visibly without truncation");
        }
        var boundedArrayCatalog = new FixtureCatalog { SchemaOverride = Parse(closedArraySchema.GetRawText().Replace("\"maxItems\":3", "\"maxItems\":2", StringComparison.Ordinal)) };
        using (var form = new WorkflowStartForm(null, boundedArrayCatalog.Dispatch, () => freshProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            form.RequestBox.Text = OriginalRequest;
            ((TextBox)form.RequiredFields["title"]).Text = "Bounded run";
            var field = form.OptionalFields["requiredChecks"];
            field.Include.IsChecked = true;
            var choices = ((StackPanel)field.Editor).Children.Cast<CheckBox>().ToArray();
            choices[0].IsChecked = true;
            Check(form.BuildDraft().DefinitionVersion == "2.0.0", "the bounded drift fixture first caches the exact target edition");
            choices[1].IsChecked = true;
            choices[2].IsChecked = true;
            Check(!form.CanSubmit && form.Status.Text.Contains("requiredChecks", StringComparison.Ordinal),
                "a selected array over maxItems cannot be submitted");
            form.SelectDefinition(Definition, "1.0.0"); await form.PendingRead;
            boundedArrayCatalog.SchemaOverride = closedArraySchema;
            form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            Check(!form.CanSubmit && form.Status.Text.Contains("schema changed", StringComparison.Ordinal),
                "a changed schema cannot reuse choices from an earlier descriptor for the same draft");
        }
        foreach (var changedSchema in new[]
        {
            (Label: "removed", Schema: Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1}},"required":["title"],"additionalProperties":true}""")),
            (Label: "unsupported", Schema: Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1},"requiredChecks":{"type":"array","items":{"type":"string"}}},"required":["title"],"additionalProperties":true}"""))
        })
        {
            var driftCatalog = new FixtureCatalog { SchemaOverride = closedArraySchema };
            using var form = new WorkflowStartForm(null, driftCatalog.Dispatch, () => freshProfiles, () => true, deadline.Token);
            await form.InitializeAsync(); form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            form.RequestBox.Text = OriginalRequest;
            ((TextBox)form.RequiredFields["title"]).Text = "Before drift";
            var field = form.OptionalFields["requiredChecks"];
            field.Include.IsChecked = true;
            ((CheckBox)((StackPanel)field.Editor).Children[0]).IsChecked = true;
            var startingDraft = form.BuildDraft();
            Check(startingDraft.DefinitionVersion == "2.0.0" && startingDraft.Input.GetProperty("requiredChecks").GetRawText() == "[\"build\"]",
                "a selected build check exists before the " + changedSchema.Label + " descriptor change");
            form.SelectDefinition(Definition, "1.0.0"); await form.PendingRead;
            driftCatalog.SchemaOverride = changedSchema.Schema;
            form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            Check(!form.CanSubmit && form.Status.Text.Contains("schema changed", StringComparison.Ordinal),
                "a " + changedSchema.Label + " optional field invalidates the complete cached schema");
            Reject(() => form.BuildDraft(), "a " + changedSchema.Label + " field cannot silently submit the previous build selection");
            Check(driftCatalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
                "the " + changedSchema.Label + " drift check performs no workflow or profile effect");
        }

        var multipleLeads = Profiles(detached);
        multipleLeads.Insert(1, new AgentProfile { Id = "second-lead", Name = "Second local lead", Role = "lead", WorkerId = "local", Revision = 1 });
        var multipleLeadsBefore = JsonSerializer.Serialize(multipleLeads, CerebralJson.Options);
        var multipleLeadCatalog = new FixtureCatalog();
        using (var form = new WorkflowStartForm(null, multipleLeadCatalog.Dispatch, () => multipleLeads, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest;
            Check(form.AgentSelector.SelectedItem is null && !form.StartButton.IsEnabled && !form.CanSubmit,
                "multiple eligible leads require an explicit agent choice without selecting a helper implicitly");
            Check(multipleLeadCatalog.Calls.Count(x => x.Operation == "sdk.discover") == 1 && form.DefinitionSelector.Items.Count == 3 &&
                multipleLeadCatalog.Calls.All(x => x.Operation != "sdk.describe"),
                "multiple leads still load the workflow catalog without describing an unselected agent's workflow");
            Reject(() => form.BuildDraft(), "multiple leads cannot produce a draft before an explicit choice");
            form.SelectAgent("second-lead"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "second lead request";
            Check(form.BuildDraft().ProfileId == "second-lead", "the second local lead can be selected explicitly after discovery");
            form.SelectAgent("lead"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "first lead request";
            Check(form.BuildDraft().ProfileId == "lead", "the first local lead remains an explicit selectable agent");
            form.SelectAgent("helper"); await form.PendingRead;
            var helper = form.BuildDraft();
            Check(helper.ProfileId == "helper" && helper.DefinitionVersion == "2.0.0" && helper.Input.GetRawText() == configuredInput.GetRawText(),
                "a helper selected among multiple leads retains its exact configured workflow and input");
        }
        Check(JsonSerializer.Serialize(multipleLeads, CerebralJson.Options) == multipleLeadsBefore &&
            multipleLeadCatalog.Calls.All(x => x.Operation is "sdk.discover" or "sdk.describe"),
            "explicit multi-lead selection changes no profile and performs only catalog reads");

        var helpersOnly = Profiles(detached).Where(x => x.Role == "helper").ToList();
        var noLeadCatalog = new FixtureCatalog();
        using (var form = new WorkflowStartForm(null, noLeadCatalog.Dispatch, () => helpersOnly, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest;
            Check(form.AgentSelector.SelectedItem is null && !form.CanSubmit && form.DefinitionSelector.Items.Count == 3 &&
                noLeadCatalog.Calls.Count(x => x.Operation == "sdk.discover") == 1,
                "zero eligible leads also load the catalog and wait for an explicit agent choice");
            form.SelectAgent("helper"); await form.PendingRead;
            var helper = form.BuildDraft();
            Check(helper.ProfileId == "helper" && helper.DefinitionVersion == "2.0.0" && helper.Input.GetRawText() == configuredInput.GetRawText(),
                "an explicitly selected helper works when no lead is available");
        }

        var missing = Profiles(new(Definition, "9.0.0", detached.Input));
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => missing, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest;
            form.SelectAgent("helper"); await form.PendingRead;
            Check(!form.CanSubmit && !string.IsNullOrWhiteSpace(form.Status.Text), "an unavailable exact saved version blocks its preset visibly");
            Reject(() => form.BuildDraft(), "an unavailable saved edition cannot silently become an installed version");
            form.SelectDefinition(Definition, "1.0.0"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "explicit alternative";
            Check(!form.BuildDraft().Input.TryGetProperty("opaque", out _), "an explicitly selected alternative begins without stale preset input");
        }
        var malformed = Profiles(detached);
        malformed[1].PluginSettings[AgentWorkflowBinding.SettingsKey] = CerebralJson.Element(new { schemaVersion = 99, future = "keep" });
        var malformedBefore = malformed[1].PluginSettings[AgentWorkflowBinding.SettingsKey].GetRawText();
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => malformed, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest;
            form.SelectAgent("helper"); await form.PendingRead;
            Check(!form.CanSubmit, "an unsupported binding does not fall back to the lead or ordinary Chat");
            Reject(() => form.BuildDraft(), "an unsupported binding cannot be used as launch input");
            form.SelectDefinition(Definition, "1.0.0"); await form.PendingRead;
            ((TextBox)form.RequiredFields["title"]).Text = "explicit manual launch";
            Check(!form.BuildDraft().Input.TryGetProperty("opaque", out _), "explicit manual work after an invalid preset starts from an empty input");
        }
        Check(malformed[1].PluginSettings[AgentWorkflowBinding.SettingsKey].GetRawText() == malformedBefore,
            "an unavailable saved setting remains untouched");

        var escapedSeed = Parse("""{"title":"saved","opaque":{"unicode":"λ","escaped":"\u0061","huge":1e+4097}}""");
        var escapedProfiles = Profiles(detached);
        escapedProfiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey] = Parse(Envelope(escapedSeed.GetRawText()));
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => escapedProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            ((TextBox)form.RequiredFields["title"]).Text = "changed";
            Check(form.BuildDraft().Input.GetProperty("opaque").GetRawText() == escapedSeed.GetProperty("opaque").GetRawText(),
                "an edited required field retains opaque Unicode, string escapes and numeric lexemes exactly");
        }

        var enumSchema = Parse("""{"type":"object","properties":{"count":{"type":"number","enum":[1,2]},"mode":{"type":"string","enum":["a","b"]}},"required":["count","mode"],"additionalProperties":true}""");
        var enumProfiles = Profiles(detached);
        enumProfiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey] = Parse(Envelope("""{"count":1.0,"mode":"\u0061","opaque":{"keep":1e+4097}}"""));
        var enumInput = AgentWorkflowBinding.Read(enumProfiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey]).Input;
        var enumCatalog = new FixtureCatalog { SchemaOverride = enumSchema };
        using (var form = new WorkflowStartForm(null, enumCatalog.Dispatch, () => enumProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            var count = (ComboBox)form.RequiredFields["count"];
            var mode = (ComboBox)form.RequiredFields["mode"];
            Check(count.SelectedIndex == 0 && mode.SelectedIndex == 0,
                "numeric aliases and equivalent escaped strings select their semantic enum choices");
            Check(form.BuildDraft().Input.GetRawText() == enumInput.GetRawText(),
                "untouched semantic enum aliases retain the original numeric and escaped-string tokens");
            count.SelectedIndex = 1;
            var changedCount = form.BuildDraft().Input;
            Check(changedCount.GetProperty("count").GetRawText() == "2" &&
                changedCount.GetProperty("mode").GetRawText() == enumInput.GetProperty("mode").GetRawText(),
                "editing a numeric enum retains the untouched escaped string token");
            mode.SelectedIndex = 1;
            var changedMode = form.BuildDraft().Input;
            Check(changedMode.GetProperty("mode").GetString() == "b" &&
                changedMode.GetProperty("opaque").GetRawText() == enumInput.GetProperty("opaque").GetRawText(),
                "explicit enum edits retain opaque optional input");
        }
        var invalidEnumProfiles = Profiles(new(Definition, "2.0.0", Parse("""{"count":3,"mode":"a"}""")));
        using (var form = new WorkflowStartForm(null, enumCatalog.Dispatch, () => invalidEnumProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Reject(() => form.BuildDraft(), "semantic enum matching still refuses a nonmember preset");
        }

        const string astral = "\U0001F680";
        var scalarProfiles = Profiles(new(Definition, "2.0.0", Parse("""{"title":"\ud83d\ude80"}""")));
        var scalarInput = AgentWorkflowBinding.Read(scalarProfiles[1].PluginSettings[AgentWorkflowBinding.SettingsKey]).Input;
        var scalarCatalog = new FixtureCatalog
        {
            SchemaOverride = Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":1}},"required":["title"],"additionalProperties":true}""")
        };
        using (var form = new WorkflowStartForm(null, scalarCatalog.Dispatch, () => scalarProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Check(form.BuildDraft().Input.GetRawText() == scalarInput.GetRawText(),
                "one astral scalar satisfies a one-character limit without changing its original token");
            var title = (TextBox)form.RequiredFields["title"];
            title.Text = "";
            Reject(() => form.BuildDraft(), "an empty string remains below a one-scalar minimum");
            title.Text = astral + "a";
            Reject(() => form.BuildDraft(), "two scalars exceed a one-scalar maximum");
            title.Text = astral;
            Check(form.BuildDraft().Input.GetProperty("title").GetString() == astral,
                "an explicitly entered astral scalar also satisfies a one-character limit");
        }
        scalarCatalog.SchemaOverride = Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":2,"maxLength":2}},"required":["title"],"additionalProperties":true}""");
        using (var form = new WorkflowStartForm(null, scalarCatalog.Dispatch, () => scalarProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Reject(() => form.BuildDraft(), "one astral scalar remains below a two-character minimum");
            ((TextBox)form.RequiredFields["title"]).Text = astral + "a";
            Check(form.BuildDraft().Input.GetProperty("title").GetString() == astral + "a",
                "two scalars satisfy a two-character limit despite occupying three UTF-16 units");
        }

        var requiredChoices = new FixtureCatalog
        {
            SchemaOverride = Parse("""{"type":"object","properties":{"flag":{"type":"boolean"},"mode":{"type":"string","enum":["one","two"]},"value":{"type":"string"}},"required":["flag","mode","value"],"additionalProperties":true}""")
        };
        var emptyProfiles = Profiles(new(Definition, "2.0.0", WorkflowContract.EmptyObject));
        using (var form = new WorkflowStartForm(null, requiredChoices.Dispatch, () => emptyProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Check(((CheckBox)form.RequiredFields["flag"]).IsChecked is null && ((ComboBox)form.RequiredFields["mode"]).SelectedIndex == -1,
                "missing required boolean and enum values are not manufactured as false or the first option");
            Reject(() => form.BuildDraft(), "required values without configured or schema defaults require explicit input");
            ((CheckBox)form.RequiredFields["flag"]).IsChecked = false;
            ((ComboBox)form.RequiredFields["mode"]).SelectedIndex = 0;
            ((TextBox)form.RequiredFields["value"]).Text = "intentional";
            ((TextBox)form.RequiredFields["value"]).Text = "";
            var chosen = form.BuildDraft().Input;
            Check(chosen.GetProperty("flag").ValueKind == JsonValueKind.False && chosen.GetProperty("mode").GetString() == "one" &&
                chosen.GetProperty("value").GetString() == "", "explicit false, first enum choice and a permitted empty string remain valid choices");
        }
        var duplicateProfiles = Profiles(new(Definition, "2.0.0", Parse("""{"title":"one","title":"two"}""")));
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => duplicateProfiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead; form.RequestBox.Text = OriginalRequest;
            Reject(() => form.BuildDraft(), "ambiguous duplicate input keys are refused before any overlay can collapse them");
        }

        var currentConnection = true;
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => profiles, () => currentConnection, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead;
            form.RequestBox.Text = OriginalRequest; currentConnection = false;
            Reject(() => form.BuildDraft(), "a changed connection cannot dispatch the retained form");
        }
        var incompatible = new FixtureCatalog
        {
            Override = (request, _) => request.Operation == "sdk.discover"
                ? Task.FromResult(CommandResult.Ok(new { contract = "other-contract", version = WorkflowContract.Version, workflows = Array.Empty<object>() })) : null
        };
        using (var form = new WorkflowStartForm(null, incompatible.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync();
            Check(!form.CanSubmit, "an incompatible catalog is held before workflow selection or submission");
        }
        var failedRead = new FixtureCatalog
        {
            Override = (request, _) => request.Operation == "sdk.describe"
                ? Task.FromException<CommandResult>(new IOException("Fixture description transport unavailable.")) : null
        };
        using (var form = new WorkflowStartForm(null, failedRead.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead;
            Check(!form.CanSubmit && form.Status.Text.Contains("unavailable", StringComparison.Ordinal),
                "a description transport failure leaves an actionable held form instead of a faulted unobserved read");
        }
        var late = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = new FixtureCatalog();
        racing.Override = (request, _) => request.Operation == "sdk.describe" &&
            request.Arguments.GetProperty("id").GetString() == OtherDefinition ? late.Task : null;
        using (var form = new WorkflowStartForm(null, racing.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.RequestBox.Text = OriginalRequest; form.SelectAgent("helper"); await form.PendingRead;
            form.SelectDefinition(OtherDefinition, "1.0.0"); var stale = form.PendingRead;
            form.SelectDefinition(Definition, "2.0.0"); await form.PendingRead;
            late.SetResult(racing.Describe(OtherDefinition));
            try { await stale; } catch (OperationCanceledException) { }
            Check(form.BuildDraft().DefinitionId == Definition && form.BuildDraft().DefinitionVersion == "2.0.0",
                "a late description cannot replace a newer agent/workflow selection");
        }
        var closing = new FixtureCatalog();
        var afterClose = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken = default;
        closing.Override = (request, token) =>
        {
            if (request.Operation != "sdk.describe" || request.Arguments.GetProperty("id").GetString() != OtherDefinition) return null;
            pendingToken = token; return afterClose.Task;
        };
        var closedForm = new WorkflowStartForm(null, closing.Dispatch, () => profiles, () => true, deadline.Token);
        await closedForm.InitializeAsync(); closedForm.SelectDefinition(OtherDefinition, "1.0.0");
        var closedRead = closedForm.PendingRead; closedForm.Dispose();
        Check(pendingToken.IsCancellationRequested, "closing a form cancels its pending read independently of the workspace lifetime");
        await closedRead;
        afterClose.SetResult(closing.Describe(OtherDefinition));
        Check(!deadline.IsCancellationRequested, "form-read cancellation does not cancel the workspace submission token");

        WorkflowStartDraft acceptedDraft;
        using (var form = new WorkflowStartForm(null, catalog.Dispatch, () => profiles, () => true, deadline.Token))
        {
            await form.InitializeAsync(); form.SelectAgent("helper"); await form.PendingRead;
            form.RequestBox.Text = OriginalRequest;
            form.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            acceptedDraft = form.AcceptedDraft ?? throw new InvalidOperationException("The actual Start handler did not capture its draft.");
            form.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(acceptedDraft, form.AcceptedDraft), "double Start retains one immutable form result");
        }
        Check(acceptedDraft.UserText == OriginalRequest && acceptedDraft.Input.GetRawText() == configuredInput.GetRawText(),
            "closing and disposing the accepted form retains its detached original request and input");

        // Exercise the existing Work owner adapter after the form has closed, including an uncertain helper submission and exact retry.
        var commands = new List<CommandRequest>(); WorkflowRun? saved = null; var loseAcknowledgement = true;
        Task<CommandResult> Owner(CommandRequest command, CancellationToken _)
        {
            commands.Add(command);
            if (command.Operation == "jobs.workflow.submit")
            {
                var value = command.Arguments;
                saved ??= new WorkflowRun
                {
                    Id = value.GetProperty("requestId").GetString()!, Revision = 1,
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                    Submission = new(value.GetProperty("requestId").GetString()!, value.GetProperty("conversationId").GetString()!,
                        value.GetProperty("profileId").GetString()!, value.GetProperty("definitionId").GetString()!, value.GetProperty("definitionVersion").GetString()!,
                        value.GetProperty("userText").GetString()!, value.GetProperty("assignmentText").GetString(), value.GetProperty("input").Clone(), WorkflowContract.EmptyObject,
                        value.GetProperty("stepLimit").GetInt32())
                };
                if (loseAcknowledgement) { loseAcknowledgement = false; throw new IOException("Fixture lost acknowledgement."); }
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (command.Operation == "jobs.workflow.get") return Task.FromResult(CommandResult.Ok(saved!));
            if (command.Operation == "jobs.workflow.events.previous")
                return Task.FromResult(CommandResult.Ok(new { events = Array.Empty<Cerebral.Contracts.CerebralEvent>(), snapshotSequence = 0, nextBeforeSequence = (long?)null }));
            throw new InvalidOperationException("Unexpected launch fixture operation: " + command.Operation);
        }
        using (var workspace = new WorkflowWorkspace(Owner, () => profiles, (_, _) => { }))
        {
            var request = new WorkflowConversationSubmission("fixture-helper-launch", acceptedDraft.ProfileId, acceptedDraft.DefinitionId,
                acceptedDraft.DefinitionVersion, acceptedDraft.UserText, acceptedDraft.Input, StepLimit: WorkflowContract.MaximumSteps);
            try { await workspace.SubmitNewWorkflowAsync(request, deadline.Token); } catch (IOException) { }
            Check(workspace.Snapshot?.MutationUncertain == true, "a lost helper acknowledgement retains the exact unconfirmed request");
            var dispatches = commands.Count;
            var rejected = false;
            try { await workspace.SubmitNewWorkflowAsync(request with { ProfileId = "lead" }, deadline.Token); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && commands.Count == dispatches, "the same request ID cannot be rebound to a different agent");
            await workspace.SubmitNewWorkflowAsync(request, deadline.Token);
            var submits = commands.Where(x => x.Operation == "jobs.workflow.submit").ToArray();
            Check(submits.Length == 2 && submits[0].Arguments.GetRawText() == submits[1].Arguments.GetRawText(),
                "retry retains identical request coordinates, helper, version, request bytes and complete input");
            Check(submits.All(x => x.Arguments.GetProperty("stepLimit").GetInt32() == WorkflowContract.MaximumSteps) &&
                saved!.Submission.StepLimit == WorkflowContract.MaximumSteps,
                "the owner adapter retains the public transition ceiling on the original request and exact retry");
            Check(saved!.Submission.ProfileId == "helper" && saved.Submission.UserText == OriginalRequest &&
                saved.Submission.Input.GetProperty("requiredChecks")[0].GetString() == "build", "the captured helper input reaches the owner adapter without delegation");
            Check(commands.All(x => x.ProfileId is null && x.Operation != "agents.save"), "the native launch uses the owner channel without saving a profile");
        }
        var presentationProfiles = Profiles(detached);
        checks += await WorkflowStartPresentationChecks.VerifyAsync(owner =>
            new WorkflowStartForm(owner, new FixtureCatalog().Dispatch, () => presentationProfiles, () => true, deadline.Token), deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { workflowStartChecks = checks, evidence = "deterministic actual WPF form and Work adapter with local command delegates and isolated presentation/automation-peer fixtures", serviceCommandsExecuted = false, modelCalls = 0, networkUsed = false, windowShown = WorkflowStartPresentationChecks.ShowsWindows }));
        return checks;
    }

    private static string Envelope(string input) => "{\"schemaVersion\":1,\"definitionId\":\"" + Definition + "\",\"definitionVersion\":\"2.0.0\",\"input\":" + input + "}";

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string DraftStatusId(JsonElement input) => "WorkflowStartStatus-InputSha256-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.GetRawText()))).ToLowerInvariant();

    private static List<AgentProfile> Profiles(AgentWorkflowBinding binding) =>
    [
        new() { Id = "lead", Name = "Existing lead", Role = "lead", WorkerId = "local", Revision = 3, ModelId = "fixture-model", ResponseTokens = 1024, MaxSteps = 12 },
        new() { Id = "helper", Name = "Configured helper", Role = "helper", WorkerId = "local", ModelWorkerId = "remote-model", Revision = 7, ModelId = "fixture-model", ResponseTokens = 4096, ReasoningMode = "on", MaxSteps = 64,
            AllowedRoots = ["C:\\fixture"], AllowedTools = ["file.read", "process.run"], Capabilities = ["filesystem.read", "process.execute"],
            PluginSettings = new(StringComparer.Ordinal) { [AgentWorkflowBinding.SettingsKey] = binding.ToSettings() } }
    ];

    private sealed class FixtureCatalog
    {
        private static readonly JsonElement Schema = ReadSchema();
        private static readonly WorkflowDefinitionDescriptor[] Definitions =
        [new(Definition, "1.0.0", "First fixture edition", Schema, []), new(Definition, "2.0.0", "Configured fixture edition", Schema, []), new(OtherDefinition, "1.0.0", "Other fixture workflow", Schema, [])];
        internal readonly List<CommandRequest> Calls = [];
        internal Func<CommandRequest, CancellationToken, Task<CommandResult>?>? Override;
        internal JsonElement? SchemaOverride;
        internal CommandResult Describe(string id) => CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version,
            definitions = Definitions.Where(x => x.Id == id).Select(x => x with { InputSchema = SchemaOverride ?? x.InputSchema }).ToArray() });
        internal Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            Calls.Add(request);
            if (Override?.Invoke(request, token) is { } custom) return custom;
            return Task.FromResult(request.Operation switch
            {
                "sdk.discover" => CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version, workflows = Definitions.Select(x => new { id = x.Id, version = x.Version, description = x.Description }).ToArray() }),
                "sdk.describe" => Describe(request.Arguments.GetProperty("id").GetString()!),
                _ => throw new InvalidOperationException("Unexpected form fixture operation: " + request.Operation)
            });
        }
        private static JsonElement ReadSchema()
        {
            using var document = JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string","minLength":1}},"required":["title"],"additionalProperties":true}""");
            return document.RootElement.Clone();
        }
    }
}
