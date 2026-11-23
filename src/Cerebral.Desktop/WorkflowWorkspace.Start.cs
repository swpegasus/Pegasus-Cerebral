using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private async Task StartAsync()
    {
        RequireSettledWorkGuidance();
        if (embedded || mutationPending) throw new InvalidOperationException("Finish the current request before starting different work.");
        if (conversation?.Snapshot.MutationUncertain == true)
            throw new InvalidOperationException("Refresh or retry the retained request before starting different work.");
        var expectedGeneration = generation;
        var expectedConnection = connectionEpoch;
        bool Current() => !disposed && expectedGeneration == generation && expectedConnection == connectionEpoch;
        WorkflowStartDraft? draft;
        using (var form = new WorkflowStartForm(Window.GetWindow(this), BoundCommand(), profiles, Current, lifetime.Token))
        {
            void Changed(object? sender, EventArgs args)
            {
                if (!Current()) form.InvalidateConnection();
            }
            SnapshotChanged += Changed;
            try
            {
                await form.InitializeAsync();
                if (!Current()) throw new InvalidOperationException("The service connection changed. Open a new workflow form.");
                form.Window.ShowDialog();
                draft = form.AcceptedDraft;
            }
            finally { SnapshotChanged -= Changed; }
        }
        if (draft is null) return;
        if (!Current()) throw new InvalidOperationException("The service connection changed. Open a new workflow form.");

        // This immutable submission is retained by the existing gateway for an uncertain-outcome retry.
        // Form-read cancellation must never become the submission's lifetime.
        var submission = new WorkflowConversationSubmission(Guid.NewGuid().ToString("N"), draft.ProfileId,
            draft.DefinitionId, draft.DefinitionVersion, draft.UserText, draft.Input, StepLimit: WorkflowContract.MaximumSteps);
        await SubmitNewWorkflowAsync(submission, lifetime.Token);
    }
}

internal sealed record WorkflowStartDraft(string ProfileId, string DefinitionId, string DefinitionVersion,
    string UserText, JsonElement Input);

internal sealed record WorkflowInputReviewTarget(string ProfileId, WorkflowDefinitionDescriptor Definition,
    JsonElement AcceptedInput, string UserText, bool AllowEmptyUserText = false);

/// <summary>A local UI draft. The owner still authorizes the profile, capabilities and input at submission.</summary>
internal sealed class WorkflowStartForm : IDisposable
{
    private const string StatusAutomationId = "WorkflowStartStatus";
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly Func<IReadOnlyList<AgentProfile>> profiles;
    private readonly Func<bool> connectionCurrent;
    private readonly WorkflowInputReviewTarget? reviewTarget;
    private readonly CancellationTokenSource reads;
    private readonly CancellationTokenRegistration lifetimeRegistration;
    private readonly StackPanel inputPanel = new();
    private readonly Dictionary<string, FrameworkElement> requiredFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CheckBox Include, FrameworkElement Editor)> optionalFields = new(StringComparer.Ordinal);
    private readonly Dictionary<DraftKey, InputDraft> drafts = new();
    private DefinitionChoice[] definitions = [];
    private CancellationTokenSource? selectionRead;
    private WorkflowDefinitionDescriptor? definition;
    private InputDraft? inputDraft;
    private long selectionTicket;
    private bool updating;
    private bool initialized;
    private bool invalidated;
    private bool disposed;
    private string? selectionError;

    internal Window Window { get; }
    internal ComboBox AgentSelector { get; } = new() { MinWidth = 260, DisplayMemberPath = nameof(AgentChoice.Label) };
    internal ComboBox DefinitionSelector { get; } = new() { MinWidth = 260, DisplayMemberPath = nameof(DefinitionChoice.Label) };
    internal TextBox RequestBox { get; } = Ui.Input("", true);
    internal Button StartButton { get; } = Ui.Button("Start", () => { }, true);
    internal TextBlock Status { get; } = Ui.Text("", 12, Ui.Muted);
    internal IReadOnlyDictionary<string, FrameworkElement> RequiredFields => requiredFields;
    internal IReadOnlyDictionary<string, (CheckBox Include, FrameworkElement Editor)> OptionalFields => optionalFields;
    internal Task PendingRead { get; private set; } = Task.CompletedTask;
    internal WorkflowStartDraft? AcceptedDraft { get; private set; }
    internal bool CanSubmit
    {
        get { try { _ = BuildDraft(); return true; } catch (Exception ex) when (IsInputError(ex)) { return false; } }
    }

    internal WorkflowStartForm(Window? owner,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command,
        Func<IReadOnlyList<AgentProfile>> profiles, Func<bool> connectionCurrent, CancellationToken lifetime,
        WorkflowInputReviewTarget? reviewTarget = null)
    {
        this.command = command;
        this.profiles = profiles;
        this.connectionCurrent = connectionCurrent;
        this.reviewTarget = reviewTarget;
        reads = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Window = new Window
        {
            Title = "Run with agent · Pegasus Cerebral", Width = 660, Height = 660,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null) Window.Owner = owner;
        if (owner is ConnectedWindow { ExistingWorkerAttachment: not null })
            ExistingWorkerDesktopPresentation.Configure(Window);
        WindowSizing.Attach(Window);
        SetId(Window, "WorkflowStartDialog");
        SetId(AgentSelector, "WorkflowStartAgent");
        SetId(DefinitionSelector, "WorkflowStartDefinition");
        AgentSelector.SetBinding(AutomationProperties.NameProperty,
            new Binding("SelectedItem.Label") { Source = AgentSelector, Mode = BindingMode.OneWay });
        DefinitionSelector.SetBinding(AutomationProperties.NameProperty,
            new Binding("SelectedItem.Label") { Source = DefinitionSelector, Mode = BindingMode.OneWay });
        SetId(RequestBox, "WorkflowComposer");
        SetId(Status, StatusAutomationId);
        SetId(StartButton, "WorkflowStartSubmit");
        if (reviewTarget is not null)
        {
            Window.Title = "Review workflow inputs before sending · Pegasus Cerebral";
            AgentSelector.IsEnabled = false;
            DefinitionSelector.IsEnabled = false;
            RequestBox.IsReadOnly = true;
            RequestBox.Text = reviewTarget.UserText;
            StartButton.Content = "Send with these inputs";
        }
        RequestBox.Height = 110;
        Status.TextWrapping = TextWrapping.Wrap;
        var cancel = Ui.Button("Cancel", () => Window.Close());
        SetId(cancel, "WorkflowStartCancel");
        StartButton.IsEnabled = false;
        var body = new DockPanel { Margin = new Thickness(16) };
        var buttons = Ui.Bar(StartButton, cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        var fields = new StackPanel();
        fields.Children.Add(Ui.Text("Run with agent", 18));
        fields.Children.Add(AgentSelector);
        fields.Children.Add(Ui.Text("Workflow and version", 14));
        fields.Children.Add(DefinitionSelector);
        fields.Children.Add(Ui.Text("What would you like done?", 14));
        fields.Children.Add(RequestBox);
        fields.Children.Add(inputPanel);
        fields.Children.Add(Status);
        body.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Window.Content = body;
        AgentSelector.SelectionChanged += (_, _) => { if (!updating && initialized) ChooseAgent(); };
        DefinitionSelector.SelectionChanged += (_, _) => { if (!updating && initialized) QueueDescription(); };
        RequestBox.TextChanged += (_, _) => UpdateStatus();
        StartButton.Click += (_, _) =>
        {
            if (AcceptedDraft is not null) return;
            try { AcceptedDraft = BuildDraft(); ClearReadyStatus(); Window.Close(); }
            catch (Exception ex) when (IsInputError(ex)) { SetStatus(ex.Message); }
        };
        Window.Closed += (_, _) => Dispose();
        lifetimeRegistration = lifetime.Register(() =>
        {
            if (Window.Dispatcher.CheckAccess()) InvalidateConnection();
            else if (!Window.Dispatcher.HasShutdownStarted)
                _ = Window.Dispatcher.InvokeAsync(InvalidateConnection);
        });
    }

    internal async Task InitializeAsync()
    {
        CheckCurrent();
        if (initialized) { await PendingRead; return; }
        initialized = true;
        updating = true;
        try
        {
            var choices = profiles().Where(Eligible).Select(AgentChoice.Capture).ToArray();
            if (choices.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw new InvalidOperationException("The configured agent identities are ambiguous.");
            AgentSelector.ItemsSource = choices;
            var leads = choices.Where(x => x.Role == "lead").Take(2).ToArray();
            AgentSelector.SelectedItem = reviewTarget is { } target
                ? choices.SingleOrDefault(x => x.Id == target.ProfileId)
                : leads.Length == 1 ? leads[0] : null;
            if (reviewTarget is not null && AgentSelector.SelectedItem is null)
                throw new InvalidOperationException("The accepted workflow profile is unavailable for input review.");
            SetStatus("Reading installed workflows…");
            var response = await ReadAsync(new("sdk.discover", CerebralJson.Element(new { })), reads.Token);
            CheckCurrent();
            if (!response.Success) throw new InvalidOperationException(response.Error);
            ValidateContract(response.Data);
            var workflows = response.Data.GetProperty("workflows");
            if (workflows.ValueKind != JsonValueKind.Array || workflows.GetArrayLength() > 256)
                throw new InvalidDataException("The installed workflow catalog is invalid or too large.");
            definitions = workflows.EnumerateArray().Select(x => new DefinitionChoice(
                RequiredText(x, "id"), RequiredText(x, "version"), RequiredText(x, "description", true))).ToArray();
            if (definitions.Select(x => (x.Id, x.Version)).Distinct().Count() != definitions.Length)
                throw new InvalidDataException("The installed workflow catalog contains duplicate versions.");
            DefinitionSelector.ItemsSource = definitions;
        }
        catch (Exception ex) when (IsInputError(ex) || IsReadError(ex) || ex is OperationCanceledException)
        {
            if (!disposed && !invalidated)
            {
                selectionError = ex is OperationCanceledException ?
                    "Reading installed workflows was cancelled. Reopen the form to try again." : ReadErrorMessage(ex);
                SetStatus(selectionError);
            }
            return;
        }
        finally { updating = false; }
        ChooseAgent();
        await PendingRead;
    }

    internal void SelectAgent(string id)
    {
        CheckCurrent();
        var choices = AgentSelector.ItemsSource as AgentChoice[] ?? [];
        AgentSelector.SelectedItem = choices.Single(x => x.Id == id);
    }

    internal void SelectDefinition(string id, string version)
    {
        CheckCurrent();
        DefinitionSelector.SelectedItem = definitions.Single(x => x.Id == id && x.Version == version);
    }

    private void ChooseAgent()
    {
        CancelSelection();
        selectionError = null;
        updating = true;
        try
        {
            DefinitionSelector.SelectedItem = null;
            if (AgentSelector.SelectedItem is not AgentChoice agent)
            {
                selectionError = "Select a local lead or helper agent.";
                return;
            }
            CheckAgent(agent);
            if (reviewTarget is null && agent.BindingError is not null)
            { selectionError = agent.BindingError; return; }
            if (reviewTarget is { } target)
            {
                DefinitionSelector.SelectedItem = definitions.SingleOrDefault(x =>
                    x.Id == target.Definition.Id && x.Version == target.Definition.Version);
                if (DefinitionSelector.SelectedItem is null)
                    selectionError = "The accepted workflow version is unavailable for input review.";
            }
            else if (agent.Binding is { } binding)
            {
                DefinitionSelector.SelectedItem = definitions.SingleOrDefault(x =>
                    x.Id == binding.DefinitionId && x.Version == binding.DefinitionVersion);
                if (DefinitionSelector.SelectedItem is null)
                    selectionError = "The agent's configured workflow version is unavailable. Select an installed workflow explicitly.";
            }
            else
            {
                DefinitionSelector.SelectedItem = definitions.FirstOrDefault();
                if (DefinitionSelector.SelectedItem is null)
                    selectionError = "No workflow modules are installed. Configure a service module first.";
            }
        }
        catch (Exception ex) when (IsInputError(ex)) { selectionError = ex.Message; }
        finally { updating = false; UpdateStatus(); }
        if (DefinitionSelector.SelectedItem is DefinitionChoice) QueueDescription();
    }

    private void QueueDescription()
    {
        CancelSelection();
        selectionError = null;
        if (AgentSelector.SelectedItem is not AgentChoice agent ||
            DefinitionSelector.SelectedItem is not DefinitionChoice choice) { UpdateStatus(); return; }
        var ticket = selectionTicket;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(reads.Token);
        selectionRead = cancellation;
        PendingRead = DescribeAsync(agent, choice, ticket, cancellation.Token);
    }

    private async Task DescribeAsync(AgentChoice agent, DefinitionChoice choice, long ticket, CancellationToken token)
    {
        SetStatus("Reading workflow inputs…");
        try
        {
            CheckAgent(agent);
            var response = await ReadAsync(new("sdk.describe", CerebralJson.Element(new { id = choice.Id })), token);
            if (disposed || invalidated || ticket != selectionTicket || token.IsCancellationRequested) return;
            CheckCurrent();
            CheckAgent(agent);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            ValidateContract(response.Data);
            var available = response.Data.GetProperty("definitions");
            if (available.ValueKind != JsonValueKind.Array || available.GetArrayLength() > 256)
                throw new InvalidDataException("The workflow description is invalid or too large.");
            definition = available.Deserialize<WorkflowDefinitionDescriptor[]>(CerebralJson.Options)!
                .Single(x => x.Id == choice.Id && x.Version == choice.Version);
            if (definition.RequiredCapabilities is null || definition.RequiredCapabilities.Count > 128 ||
                definition.RequiredCapabilities.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("The workflow capability declaration is invalid.");
            if (reviewTarget is { } target &&
                (!JsonElement.DeepEquals(definition.InputSchema, target.Definition.InputSchema) ||
                 !definition.RequiredCapabilities.SequenceEqual(target.Definition.RequiredCapabilities, StringComparer.Ordinal)))
                throw new InvalidDataException("The exact workflow declaration changed during input review.");
            var key = new DraftKey(agent.Id, choice.Id, choice.Version);
            if (!drafts.TryGetValue(key, out inputDraft))
            {
                var seed = reviewTarget is { } acceptedTarget ? acceptedTarget.AcceptedInput.Clone() :
                    agent.Binding is { } binding && binding.DefinitionId == choice.Id &&
                    binding.DefinitionVersion == choice.Version ? binding.Input.Clone() : WorkflowContract.EmptyObject;
                inputDraft = new InputDraft(seed, definition.InputSchema);
                drafts.Add(key, inputDraft);
            }
            else if (!JsonElement.DeepEquals(inputDraft.Schema, definition.InputSchema))
                throw new InvalidDataException("The workflow input schema changed. Open a new workflow form.");
            BuildFields(definition.InputSchema, inputDraft);
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            if (!disposed && !invalidated && !reads.IsCancellationRequested && ticket == selectionTicket && connectionCurrent())
            {
                selectionError = "Reading workflow inputs was cancelled. Select the workflow again or reopen the form.";
                SetStatus(selectionError);
            }
        }
        catch (Exception ex) when (IsInputError(ex) || IsReadError(ex))
        {
            if (!disposed && !invalidated && ticket == selectionTicket)
            {
                definition = null; inputDraft = null; selectionError = ReadErrorMessage(ex); SetStatus(selectionError);
            }
        }
    }

    internal WorkflowStartDraft BuildDraft()
    {
        CheckCurrent();
        if (AgentSelector.SelectedItem is not AgentChoice agent) throw new InvalidOperationException("Select a local lead or helper agent.");
        CheckAgent(agent);
        if (selectionError is not null) throw new InvalidOperationException(selectionError);
        if (definition is null || inputDraft is null || DefinitionSelector.SelectedItem is not DefinitionChoice choice ||
            definition.Id != choice.Id || definition.Version != choice.Version)
            throw new InvalidOperationException("Wait for the selected workflow inputs.");
        if (reviewTarget is { } target &&
            (agent.Id != target.ProfileId || choice.Id != target.Definition.Id ||
             choice.Version != target.Definition.Version || RequestBox.Text != target.UserText))
            throw new InvalidOperationException("The reviewed workflow identity or visible request changed.");
        var missing = definition.RequiredCapabilities.Where(x => !agent.Capabilities.Contains(x, StringComparer.Ordinal)).ToArray();
        if (missing.Length != 0) throw new InvalidOperationException("This agent lacks required workflow capabilities: " + string.Join(", ", missing));
        if (string.IsNullOrWhiteSpace(RequestBox.Text) && reviewTarget?.AllowEmptyUserText != true)
            throw new InvalidOperationException("Enter what you would like done.");
        if (RequestBox.Text.Contains('\0') || RequestBox.Text.Length > MaximumInputBytes)
            throw new InvalidOperationException("The request must be at most 131072 characters without NUL characters.");
        var changes = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var (name, field) in inputDraft.Fields)
        {
            var seeded = inputDraft.Seed.TryGetProperty(name, out _);
            if (!field.Included)
            {
                if (seeded) changes.Add(name, null);
                continue;
            }
            if (field.Required || field.Dirty || !seeded)
            {
                var value = field.Read();
                if (field.Dirty || !seeded) changes.Add(name, value);
            }
        }
        JsonElement input;
        if (changes.Count == 0)
        {
            // In particular, do not reserialize an untouched preset's unknown fields or numeric tokens.
            input = inputDraft.Seed.Clone();
        }
        else
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in inputDraft.Seed.EnumerateObject())
                {
                    if (changes.ContainsKey(property.Name)) continue;
                    writer.WritePropertyName(property.Name);
                    writer.WriteRawValue(property.Value.GetRawText());
                }
                foreach (var (name, change) in changes)
                {
                    if (change is not { } value) continue;
                    writer.WritePropertyName(name); value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            input = document.RootElement.Clone();
        }
        ValidateInput(input);
        return new(agent.Id, definition.Id, definition.Version, RequestBox.Text, input);
    }

    private void BuildFields(JsonElement schema, InputDraft draft)
    {
        requiredFields.Clear(); optionalFields.Clear(); inputPanel.Children.Clear();
        if (schema.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The workflow input schema is invalid.");
        var hasRequired = schema.TryGetProperty("required", out var required);
        if (hasRequired && (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 64))
            throw new InvalidDataException("The workflow has too many required inputs.");
        if (!schema.TryGetProperty("properties", out var properties) && !hasRequired) return;
        if (properties.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The workflow input properties are invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (hasRequired)
        {
            foreach (var entry in required.EnumerateArray())
            {
                var name = entry.GetString();
                if (string.IsNullOrEmpty(name) || name.Length > 256 || !seen.Add(name))
                    throw new InvalidDataException("The required workflow input names are invalid.");
                AddField(name, properties.GetProperty(name), requiredField: true);
            }
        }
        var needsSavedInput = false;
        foreach (var property in properties.EnumerateObject())
        {
            if (seen.Contains(property.Name)) continue;
            if (property.Name.Length is 0 or > 256 || optionalFields.Count >= 64 || !FieldDraft.CanEdit(property.Value))
            {
                needsSavedInput = true;
                continue;
            }
            AddField(property.Name, property.Value, requiredField: false);
        }
        if (needsSavedInput)
        {
            var note = Ui.Text("Some optional inputs require a workflow-specific editor. This form preserves them from the selected agent's saved workflow settings.", 12, Ui.Muted);
            note.TextWrapping = TextWrapping.Wrap;
            inputPanel.Children.Add(note);
        }

        void AddField(string name, JsonElement property, bool requiredField)
        {
            if (!draft.Fields.TryGetValue(name, out var field))
            {
                field = FieldDraft.Create(name, property, draft.Seed, requiredField);
                draft.Fields.Add(name, field);
            }
            else if (field.Required != requiredField || !JsonElement.DeepEquals(field.Schema, property))
                throw new InvalidDataException("The workflow input schema changed. Open a new workflow form.");
            var label = property.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()! : name;
            var inputId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
            if (requiredField) inputPanel.Children.Add(Ui.Text(label, 13));
            FrameworkElement control;
            if (field.Options is { } options)
            {
                var box = new ComboBox { ItemsSource = options.Select(x => x.ToString()).ToArray(), SelectedIndex = field.Selected };
                box.SelectionChanged += (_, _) => { field.Selected = box.SelectedIndex; field.Dirty = true; UpdateStatus(); };
                control = box;
            }
            else if (field.ArrayChoices is { } choices)
            {
                var group = new StackPanel();
                foreach (var choice in choices)
                {
                    var option = choice;
                    var check = new CheckBox { Content = option, IsChecked = field.ArraySelected!.Contains(option) };
                    var choiceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(option))).ToLowerInvariant();
                    SetId(check, "WorkflowStartInput-" + inputId + "-Choice-" + choiceId);
                    AutomationProperties.SetName(check, label + ": " + option);
                    check.Checked += (_, _) => ChangeChoice(true);
                    check.Unchecked += (_, _) => ChangeChoice(false);
                    group.Children.Add(check);

                    void ChangeChoice(bool selected)
                    {
                        if (selected) field.ArraySelected!.Add(option);
                        else field.ArraySelected!.Remove(option);
                        field.Dirty = true;
                        UpdateStatus();
                    }
                }
                control = group;
            }
            else if (field.Type == "boolean")
            {
                var box = new CheckBox { IsChecked = field.Boolean, IsThreeState = true };
                box.Checked += (_, _) => { field.Boolean = true; field.Dirty = true; UpdateStatus(); };
                box.Unchecked += (_, _) => { field.Boolean = false; field.Dirty = true; UpdateStatus(); };
                box.Indeterminate += (_, _) => { field.Boolean = null; field.Dirty = true; UpdateStatus(); };
                control = box;
            }
            else
            {
                var box = Ui.Input(field.Text);
                box.TextChanged += (_, _) => { field.Text = box.Text; field.Dirty = true; UpdateStatus(); };
                control = box;
            }
            control.Margin = new Thickness(0, 4, 0, 8);
            SetId(control, "WorkflowStartInput-" + inputId);
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, name);
            if (property.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                control.ToolTip = description.GetString();
            if (requiredField) requiredFields.Add(name, control);
            else
            {
                var include = new CheckBox { Content = "Use " + label + " for this run", IsChecked = field.Included };
                SetId(include, "WorkflowStartInclude-" + inputId);
                AutomationProperties.SetHelpText(include, "Optional. Leave unchecked to let the workflow choose its default.");
                control.IsEnabled = field.Included;
                include.Checked += (_, _) => ChangeInclusion(true);
                include.Unchecked += (_, _) => ChangeInclusion(false);
                optionalFields.Add(name, (include, control));
                inputPanel.Children.Add(include);

                void ChangeInclusion(bool included)
                {
                    field.Included = included;
                    control.IsEnabled = included;
                    UpdateStatus();
                }
            }
            inputPanel.Children.Add(control);
        }
    }

    private void CheckCurrent()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WorkflowStartForm));
        if (invalidated || reads.IsCancellationRequested || !connectionCurrent())
            throw new InvalidOperationException("The service connection changed. Open a new workflow form.");
    }

    private void CheckAgent(AgentChoice agent)
    {
        CheckCurrent();
        var current = profiles().Where(x => x.Id == agent.Id).ToArray();
        if (current.Length != 1 || !agent.Matches(current[0]))
            throw new InvalidOperationException("The selected agent changed. Close this form and select it again.");
    }

    internal void InvalidateConnection()
    {
        if (disposed) return;
        invalidated = true;
        AcceptedDraft = null;
        reads.Cancel();
        CancelSelection();
        SetStatus("The service connection changed. Open a new workflow form.");
        if (Window.IsVisible) Window.Close();
    }

    private void CancelSelection()
    {
        selectionTicket++;
        selectionRead?.Cancel(); selectionRead?.Dispose(); selectionRead = null;
        definition = null; inputDraft = null;
        requiredFields.Clear(); optionalFields.Clear(); inputPanel.Children.Clear();
        ClearReadyStatus();
    }

    private void UpdateStatus()
    {
        if (disposed) return;
        try
        {
            var draft = BuildDraft();
            Status.Text = "Ready. The service will validate the selected agent and workflow when submitted.";
            Status.Foreground = Ui.Muted;
            // Keep the complete input private while allowing a client to verify the draft shown by this form.
            var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(draft.Input.GetRawText()))).ToLowerInvariant();
            SetId(Status, StatusAutomationId + "-InputSha256-" + digest);
            StartButton.IsEnabled = true;
        }
        catch (Exception ex) when (IsInputError(ex)) { SetStatus(ex.Message); }
    }

    private void SetStatus(string message)
    {
        if (disposed) return;
        Status.Text = message;
        Status.Foreground = Ui.Muted;
        ClearReadyStatus();
    }

    private void ClearReadyStatus()
    {
        SetId(Status, StatusAutomationId);
        StartButton.IsEnabled = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetimeRegistration.Dispose();
        reads.Cancel();
        selectionRead?.Cancel(); selectionRead?.Dispose(); selectionRead = null;
        reads.Dispose();
        ClearReadyStatus();
        if (Window.IsVisible) Window.Close();
    }

    private const int MaximumInputBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private async Task<CommandResult> ReadAsync(CommandRequest request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await command(request, deadline.Token).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InvalidOperationException("The service did not finish reading the workflow catalog within 30 seconds. Select the workflow again or reopen the form to try again."); }
    }

    private static void ValidateContract(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
            contract.GetString() != WorkflowContract.Id ||
            !data.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) ||
            value != WorkflowContract.Version)
            throw new InvalidDataException("The service returned an incompatible workflow catalog.");
    }

    private static void ValidateInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || StrictUtf8.GetByteCount(input.GetRawText()) > MaximumInputBytes)
            throw new InvalidDataException("Workflow input must be a JSON object of at most 128 KiB.");
        var visited = 0;
        Visit(input, 0);
        void Visit(JsonElement value, int depth)
        {
            if (++visited > MaximumInputBytes || depth > 64)
                throw new InvalidDataException("Workflow input is too deeply nested or contains too many values.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Workflow input contains duplicate property names.");
                    Visit(property.Value, depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item, depth + 1);
        }
    }

    private static bool Eligible(AgentProfile profile) => profile.WorkerId == "local" && profile.Role is "lead" or "helper";
    private static void SetId(DependencyObject target, string id) => AutomationProperties.SetAutomationId(target, id);
    private static bool IsReadError(Exception ex) => ex is IOException or HttpRequestException or TimeoutException;
    private static string ReadErrorMessage(Exception ex) => IsReadError(ex)
        ? "The workflow catalog could not be read. Select the workflow again or reopen the form to try again. " + ex.Message
        : ex.Message;
    private static bool IsInputError(Exception ex) => ex is InvalidOperationException or InvalidDataException or
        ArgumentException or JsonException or FormatException or OverflowException or KeyNotFoundException;
    private static string RequiredText(JsonElement element, string name, bool empty = false)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            (!empty && string.IsNullOrWhiteSpace(text)) || text.Length > 4096)
            throw new InvalidDataException("The workflow catalog contains invalid " + name + ".");
        return text;
    }

    private sealed record DefinitionChoice(string Id, string Version, string Description)
    {
        public string Label => Id + " · " + Version;
        public override string ToString() => Label;
    }

    private sealed record AgentChoice(string Id, string Name, string Role, string WorkerId, int Revision,
        string[] Capabilities, bool HasBinding, string? BindingText, AgentWorkflowBinding? Binding, string? BindingError)
    {
        public string Label => Name + " · " + Role + " · " + Id;
        public override string ToString() => Label;
        internal static AgentChoice Capture(AgentProfile profile)
        {
            var hasBinding = profile.PluginSettings.TryGetValue(AgentWorkflowBinding.SettingsKey, out var setting);
            AgentWorkflowBinding? binding = null;
            string? error = null;
            if (hasBinding)
            {
                try { binding = AgentWorkflowBinding.Read(setting); }
                catch (Exception ex) when (IsInputError(ex)) { error = "The agent's configured workflow is invalid: " + ex.Message; }
            }
            return new(profile.Id, profile.Name, profile.Role, profile.WorkerId, profile.Revision,
                profile.Capabilities.ToArray(), hasBinding, hasBinding ? setting.GetRawText() : null, binding, error);
        }
        internal bool Matches(AgentProfile profile)
        {
            if (!Eligible(profile) || profile.Id != Id || profile.Role != Role || profile.WorkerId != WorkerId ||
                profile.Revision != Revision || !Capabilities.SequenceEqual(profile.Capabilities, StringComparer.Ordinal)) return false;
            var present = profile.PluginSettings.TryGetValue(AgentWorkflowBinding.SettingsKey, out var setting);
            return present == HasBinding && (!present || setting.GetRawText() == BindingText);
        }
    }

    private sealed record DraftKey(string AgentId, string DefinitionId, string DefinitionVersion);
    private sealed class InputDraft
    {
        internal JsonElement Seed { get; }
        internal JsonElement Schema { get; }
        internal Dictionary<string, FieldDraft> Fields { get; } = new(StringComparer.Ordinal);
        internal InputDraft(JsonElement seed, JsonElement schema)
        {
            ValidateInput(seed);
            Seed = seed;
            // Keep the entire descriptor schema with the draft, including absent or unsupported fields.
            Schema = schema.Clone();
        }
    }

    private sealed class FieldDraft
    {
        internal required string Name { get; init; }
        internal required string Type { get; init; }
        internal required JsonElement Schema { get; init; }
        internal JsonElement? Preset { get; init; }
        internal bool HasPreset { get; init; }
        internal JsonElement[]? Options { get; init; }
        internal string[]? ArrayChoices { get; init; }
        internal HashSet<string>? ArraySelected { get; init; }
        internal int ArrayMinimum { get; init; }
        internal int ArrayMaximum { get; init; }
        internal int Selected { get; set; }
        internal string Text { get; set; } = "";
        internal bool? Boolean { get; set; }
        internal bool Dirty { get; set; }
        internal bool Required { get; init; }
        internal bool Included { get; set; }

        internal static bool CanEdit(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) return false;
            if (type.GetString() is "string" or "number" or "integer" or "boolean") return true;
            return type.GetString() == "array" && schema.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Object && items.TryGetProperty("type", out var itemType) &&
                itemType.ValueKind == JsonValueKind.String && itemType.GetString() == "string" &&
                items.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array;
        }

        internal static FieldDraft Create(string name, JsonElement schema, JsonElement input, bool required)
        {
            if (!CanEdit(schema))
                throw new InvalidOperationException("This workflow needs its module's guided input form for " + name + ".");
            var type = schema.GetProperty("type").GetString()!;
            JsonElement? original = input.TryGetProperty(name, out var value) ? value.Clone() : null;
            var preset = original ?? (schema.TryGetProperty("default", out var fallback) ? fallback.Clone() : (JsonElement?)null);
            var array = type == "array" ? ReadArraySchema(schema) : (Choices: (string[]?)null, Minimum: 0, Maximum: 0);
            var arraySelected = array.Choices is null ? null : new HashSet<string>(StringComparer.Ordinal);
            if (array.Choices is not null && preset is { } arrayPreset)
                ValidateArray(arrayPreset, array.Choices, array.Minimum, array.Maximum, name, arraySelected);
            JsonElement[]? options = null;
            if (schema.TryGetProperty("enum", out var enumeration))
            {
                if (enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() is < 1 or > 256)
                    throw new InvalidDataException("The workflow input choices are invalid.");
                options = enumeration.EnumerateArray().Select(x => x.Clone()).ToArray();
            }
            var selected = preset is { } chosen && options is not null ?
                Array.FindIndex(options, x => JsonElement.DeepEquals(x, chosen)) : -1;
            return new FieldDraft
            {
                Name = name, Type = type, Schema = schema.Clone(), Preset = preset, HasPreset = preset is not null, Options = options,
                ArrayChoices = array.Choices, ArraySelected = arraySelected,
                ArrayMinimum = array.Minimum, ArrayMaximum = array.Maximum,
                Required = required, Included = required || original.HasValue,
                Selected = selected, Boolean = preset?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null },
                Text = preset is { } initial ? initial.ValueKind == JsonValueKind.String ? initial.GetString()! : initial.GetRawText() : ""
            };
        }

        internal JsonElement Read()
        {
            if (!Dirty && !HasPreset && Required) throw new InvalidOperationException("Enter a value for " + Name + ".");
            JsonElement value;
            if (!Dirty && Preset is { } preset) value = preset;
            else if (Type == "array")
                value = CerebralJson.Element(ArrayChoices!.Where(choice => ArraySelected!.Contains(choice)).ToArray());
            else if (Options is { } options)
            {
                if (Selected < 0 || Selected >= options.Length) throw new InvalidOperationException("Select a value for " + Name + ".");
                value = options[Selected];
            }
            else if (Type == "boolean")
            {
                if (Boolean is null) throw new InvalidOperationException("Choose true or false for " + Name + ".");
                value = CerebralJson.Element(Boolean.Value);
            }
            else if (Type == "string") value = CerebralJson.Element(Text);
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(Text);
                    if (document.RootElement.ValueKind != JsonValueKind.Number) throw new FormatException();
                    value = document.RootElement.Clone();
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                { throw new InvalidOperationException("Enter a valid " + Type + " for " + Name + "."); }
            }
            var valid = Type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value.GetRawText()),
                "array" => value.ValueKind == JsonValueKind.Array,
                _ => false
            };
            if (!valid) throw new InvalidOperationException("The input " + Name + " has an incompatible value.");
            if (Options is { } allowed && !allowed.Any(x => JsonElement.DeepEquals(x, value)))
                throw new InvalidOperationException("Select a permitted value for " + Name + ".");
            if (Type == "array")
                ValidateArray(value, ArrayChoices!, ArrayMinimum, ArrayMaximum, Name);
            if (Type == "string")
            {
                var length = value.GetString()!.EnumerateRunes().Count();
                if (Schema.TryGetProperty("minLength", out var minimum) && length < minimum.GetInt32())
                    throw new InvalidOperationException($"Enter at least {minimum.GetInt32()} characters for {Name}.");
                if (Schema.TryGetProperty("maxLength", out var maximum) && length > maximum.GetInt32())
                    throw new InvalidOperationException($"Enter at most {maximum.GetInt32()} characters for {Name}.");
            }
            return value;
        }

        private static (string[] Choices, int Minimum, int Maximum) ReadArraySchema(JsonElement schema)
        {
            // This native editor only represents a finite set of unique string choices.
            var supported = new HashSet<string>(StringComparer.Ordinal)
                { "type", "items", "minItems", "maxItems", "uniqueItems", "title", "description", "default" };
            if (schema.EnumerateObject().Any(x => !supported.Contains(x.Name)) ||
                schema.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != schema.EnumerateObject().Count() ||
                !schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object ||
                items.EnumerateObject().Any(x => x.Name is not ("type" or "enum" or "minLength" or "maxLength" or "title" or "description")) ||
                items.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.EnumerateObject().Count() ||
                !items.TryGetProperty("enum", out var enumeration) || enumeration.ValueKind != JsonValueKind.Array ||
                enumeration.GetArrayLength() is < 1 or > 64 ||
                schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("The workflow input array schema is unsupported or invalid.");
            var choices = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in enumeration.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } choice ||
                    choice.Length is 0 or > 256 || choice.Contains('\0') || !seen.Add(choice))
                    throw new InvalidDataException("The workflow input array choices are invalid.");
                choices.Add(choice);
            }
            var minimum = ReadBound(schema, "minItems", 0);
            var maximum = ReadBound(schema, "maxItems", choices.Count);
            if (maximum < minimum || minimum > choices.Count || maximum > 64)
                throw new InvalidDataException("The workflow input array bounds are invalid.");
            var minLength = ReadBound(items, "minLength", 0);
            var maxLength = ReadBound(items, "maxLength", 256);
            if (maxLength < minLength || maxLength > 256 || choices.Any(x =>
                x.EnumerateRunes().Count() < minLength || x.EnumerateRunes().Count() > maxLength))
                throw new InvalidDataException("The workflow input array item bounds are invalid.");
            return (choices.ToArray(), minimum, maximum);
        }

        private static int ReadBound(JsonElement schema, string name, int fallback)
        {
            if (!schema.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 0)
                throw new InvalidDataException("The workflow input array bounds are invalid.");
            return result;
        }

        private static void ValidateArray(JsonElement value, string[] choices, int minimum, int maximum,
            string name, HashSet<string>? selected = null)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum ||
                value.GetArrayLength() > maximum)
                throw new InvalidOperationException($"Select between {minimum} and {maximum} choices for {name}.");
            var allowed = new HashSet<string>(choices, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } choice ||
                    !allowed.Contains(choice) || !seen.Add(choice))
                    throw new InvalidOperationException("The saved choices for " + name + " are invalid or repeated.");
                selected?.Add(choice);
            }
        }

        private static bool IsInteger(string text)
        {
            // Determine integrality from the decimal token without converting an opaque number to double.
            var exponentAt = text.IndexOfAny(['e', 'E']);
            var mantissa = exponentAt < 0 ? text : text[..exponentAt];
            var exponent = 0;
            if (exponentAt >= 0 && !int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) return false;
            var dot = mantissa.IndexOf('.');
            if (dot < 0) return exponent >= 0 || HasTrailingZeros(mantissa, -(long)exponent);
            var fractionDigits = mantissa.Length - dot - 1;
            var neededZeros = (long)fractionDigits - exponent;
            return neededZeros <= 0 || HasTrailingZeros(mantissa.Replace(".", "", StringComparison.Ordinal), neededZeros);
        }

        private static bool HasTrailingZeros(string text, long count)
        {
            var digits = text.TrimStart('-');
            if (digits.All(x => x == '0')) return true;
            return count <= digits.Length && digits.AsSpan(digits.Length - (int)count).IndexOfAnyExcept('0') < 0;
        }
    }
}
