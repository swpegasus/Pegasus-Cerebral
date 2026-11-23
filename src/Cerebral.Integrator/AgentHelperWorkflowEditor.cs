using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>Only the helper workflow setting is owned here; the parent retains all other profile data.</summary>
internal sealed class AgentHelperWorkflowEditor : IDisposable
{
    internal const string Chat = "Ordinary Chat (default)";
    internal const string Feature = "Installed feature workflow";
    internal const string Choose = "Choose an installed workflow and exact version";
    private const string Mode = "helperWorkflowMode";
    private const string Choice = "helperWorkflowChoice";
    private const string Status = "helperWorkflowStatus";
    private readonly FieldsDialog form;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch;
    private readonly Func<bool> connectionCurrent;
    private readonly Func<bool> canEdit;
    private readonly CancellationTokenSource lifetime;
    private readonly Func<AgentWorkflowInputForm, bool?> showInputs;
    private readonly Dictionary<string, WorkflowChoice> choices = new(StringComparer.Ordinal);
    private AgentHelperWorkflowBinding? inputBinding;
    private WorkflowDefinitionDescriptor? definition;
    private CancellationTokenSource? request;
    private long generation;
    private bool updating, changed, loading, describing, inputConfirmed, disposed;
    private string message = "";
    private readonly string? savedLabel;
    private readonly AgentHelperWorkflowBinding? savedBinding;
    internal Action? Changed { get; set; }
    internal Task PendingRead { get; private set; } = Task.CompletedTask;
    internal bool IsChanged => changed;
    internal bool CanSave => !changed || form.Selection(Mode) == Chat ||
        !loading && !describing && definition is not null && inputConfirmed && inputBinding is not null &&
        definition.Id == inputBinding.DefinitionId && definition.Version == inputBinding.DefinitionVersion;
    internal string? SaveStatus => CanSave ? null : message;

    private sealed record WorkflowChoice(string Id, string Version, string Description)
    {
        internal string Label => Id + " · " + Version;
    }

    internal AgentHelperWorkflowEditor(FieldsDialog form, AgentProfile original,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch, Func<bool> connectionCurrent,
        Func<bool> canEdit, CancellationToken token, Func<AgentWorkflowInputForm, bool?>? showInputs = null)
    {
        this.form = form; this.dispatch = dispatch; this.connectionCurrent = connectionCurrent; this.canEdit = canEdit;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        this.showInputs = showInputs ?? (input => input.Form.ShowDialog());
        var hasSaved = original.PluginSettings.TryGetValue(AgentHelperWorkflowBinding.Key, out var saved);
        if (hasSaved)
        {
            try
            {
                savedBinding = AgentHelperWorkflowBinding.Read(saved);
                inputBinding = savedBinding;
                savedLabel = "Saved: " + savedBinding.DefinitionId + " · " + savedBinding.DefinitionVersion + " (not read)";
                message = "Saved exact workflow is retained. Read installed workflows to inspect or change its inputs.";
            }
            catch (ArgumentException error)
            {
                savedLabel = "Saved workflow binding (unavailable)";
                message = error.Message + " It will be preserved unchanged unless you explicitly choose another binding or ordinary Chat.";
            }
        }
        else message = "Delegated work uses the Worker's ordinary Chat default. No workflow setting is added.";
        var initialChoices = savedLabel is null ? new[] { Choose } : new[] { Choose, savedLabel };
        form.Tab("helperWorkflow", "Helper workflow")
            .Choice(Mode, "When this agent receives delegated work", [Chat, Feature], hasSaved ? Feature : Chat)
            .Choice(Choice, "Workflow and exact installed version", initialChoices, savedLabel ?? Choose)
            .Add(Status, "Workflow inputs and availability", message, true).SetReadOnly(Status)
            .ActionButton("Read installed workflows", _ => { PendingRead = ReadCatalogAsync(); })
            .ActionButton("Configure workflow inputs…", _ => ConfigureInputs());
        updating = true;
        form.WhenChoiceChanged(Mode, _ => ModeChanged()).WhenChoiceChanged(Choice, _ => SelectionChanged());
        updating = false;
        UpdateControls();
    }

    internal void RefreshControls() => UpdateControls();

    private void UpdateControls()
    {
        if (disposed) return;
        form.SetEnabled(Choice, canEdit() && form.Selection(Mode) == Feature && !loading);
        form.SetValue(Status, message);
        Changed?.Invoke();
    }

    private void ModeChanged()
    {
        if (updating || disposed) return;
        changed = true;
        CancelRead();
        definition = null; inputConfirmed = false;
        message = form.Selection(Mode) == Chat
            ? "Save removes only the helper workflow override. Tools, capabilities and other plugin settings stay unchanged."
            : "Read installed workflows and select an exact version, then configure its inputs.";
        UpdateControls();
        if (form.Selection(Mode) == Feature && choices.ContainsKey(form.Selection(Choice)))
            PendingRead = DescribeSelectionAsync();
    }

    private void SelectionChanged()
    {
        if (updating || disposed || form.Selection(Mode) != Feature) return;
        changed = true;
        PendingRead = DescribeSelectionAsync();
    }

    private void RequireEditable()
    {
        if (disposed || lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
        if (!connectionCurrent()) throw new InvalidOperationException("The service connection changed. Reopen the agent editor; its draft has not been sent.");
        if (!canEdit()) throw new InvalidOperationException("Wait for the current profile save before changing its workflow draft.");
    }

    private void CancelRead()
    {
        generation++;
        request?.Cancel(); request?.Dispose(); request = null;
        loading = false; describing = false;
    }

    private (long Ticket, CancellationToken Token) StartRead()
    {
        CancelRead();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(30));
        return (generation, request.Token);
    }

    private bool Current(long ticket) => !disposed && !lifetime.IsCancellationRequested &&
        ticket == generation && connectionCurrent() && canEdit();

    private async Task<JsonElement> ReadAsync(string operation, object arguments, CancellationToken token)
    {
        var result = await dispatch(new(operation, CerebralJson.Element(arguments)), token).WaitAsync(token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The workflow catalog is unavailable.");
        AgentHelperWorkflowBinding.RequireObject(result.Data, 1024 * 1024, "Workflow catalog response");
        if (!result.Data.TryGetProperty("contract", out var contract) || contract.GetString() != WorkflowContract.Id ||
            !result.Data.TryGetProperty("version", out var version) || !version.TryGetInt32(out var schema) || schema != WorkflowContract.Version)
            throw new ArgumentException("The service returned an incompatible workflow catalog.");
        return result.Data;
    }

    internal async Task ReadCatalogAsync()
    {
        if (disposed) return;
        long ticket = -1;
        try
        {
            RequireEditable();
            var previous = choices.GetValueOrDefault(form.Selection(Choice));
            var read = StartRead(); ticket = read.Ticket;
            loading = true; definition = null; inputConfirmed = false;
            message = "Reading installed workflows from the original service…"; UpdateControls();
            var result = await ReadAsync("sdk.discover", new { }, read.Token);
            if (!Current(ticket)) return;
            if (!result.TryGetProperty("workflows", out var workflows) || workflows.ValueKind != JsonValueKind.Array ||
                workflows.GetArrayLength() > 256) throw new ArgumentException("The workflow list is invalid or exceeds the form limit.");
            var found = new List<WorkflowChoice>();
            foreach (var row in workflows.EnumerateArray())
            {
                AgentHelperWorkflowBinding.RequireObject(row, 8192, "Workflow catalog entry");
                if (!row.TryGetProperty("id", out var id) || !AgentHelperWorkflowBinding.Text(id, 256) ||
                    !row.TryGetProperty("version", out var version) || !AgentHelperWorkflowBinding.Text(version, 64) ||
                    !row.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
                    description.GetString()!.Length > 4096)
                    throw new ArgumentException("The service returned an invalid workflow catalog entry.");
                found.Add(new(id.GetString()!, version.GetString()!, description.GetString()!));
            }
            if (found.Select(x => (x.Id, x.Version)).Distinct().Count() != found.Count)
                throw new ArgumentException("The service returned duplicate workflow versions.");
            choices.Clear();
            foreach (var value in found.OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal))
                choices.Add(value.Label, value);
            var wantedId = previous?.Id ?? inputBinding?.DefinitionId ?? savedBinding?.DefinitionId;
            var wantedVersion = previous?.Version ?? inputBinding?.DefinitionVersion ?? savedBinding?.DefinitionVersion;
            var selected = choices.Values.FirstOrDefault(x => x.Id == wantedId && x.Version == wantedVersion)?.Label;
            var labels = new List<string> { Choose };
            labels.AddRange(choices.Keys);
            if (selected is null && savedLabel is not null) { labels.Add(savedLabel); selected = savedLabel; }
            updating = true;
            try { form.SetChoices(Choice, labels, selected ?? Choose); }
            finally { updating = false; }
            loading = false;
            message = "Catalog read. Select an exact installed version; no latest-version fallback is used.";
            UpdateControls();
            if (form.Selection(Mode) == Feature && choices.ContainsKey(form.Selection(Choice)))
                await DescribeSelectionAsync();
        }
        catch (Exception error)
        {
            if (!disposed && (ticket == -1 || ticket == generation))
            {
                message = "Workflow catalog unavailable. The saved binding has not been cleared. " + error.Message;
                UpdateControls();
            }
        }
        finally
        {
            if (!disposed && ticket == generation)
            {
                loading = false;
                if (!connectionCurrent()) message = "The service connection changed. Reopen the editor; this draft has not been sent.";
                UpdateControls();
            }
        }
    }

    internal async Task DescribeSelectionAsync()
    {
        if (disposed) return;
        long ticket = -1;
        try
        {
            RequireEditable();
            var read = StartRead(); ticket = read.Ticket;
            definition = null; inputConfirmed = false;
            if (!choices.TryGetValue(form.Selection(Choice), out var choice))
            {
                message = "Select an exact installed workflow. Unavailable saved versions are preserved, never replaced automatically.";
                UpdateControls(); return;
            }
            describing = true;
            message = "Reading " + choice.Label + " input fields…"; UpdateControls();
            var result = await ReadAsync("sdk.describe", new { id = choice.Id }, read.Token);
            if (!Current(ticket)) return;
            if (!result.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array ||
                definitions.GetArrayLength() > 256) throw new ArgumentException("Workflow definitions are unavailable.");
            var matching = definitions.EnumerateArray().Where(x =>
                x.TryGetProperty("id", out var id) && id.GetString() == choice.Id &&
                x.TryGetProperty("version", out var version) && version.GetString() == choice.Version).ToArray();
            if (matching.Length != 1) throw new ArgumentException("The selected exact workflow version is no longer installed.");
            var described = CerebralJson.Read<WorkflowDefinitionDescriptor>(matching[0]);
            if (described.Id != choice.Id || described.Version != choice.Version || described.RequiredCapabilities is null ||
                described.RequiredCapabilities.Count > 64 || described.RequiredCapabilities.Any(x => string.IsNullOrEmpty(x) || x.Length > 256 || x.Contains('\0')) ||
                described.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() != described.RequiredCapabilities.Count)
                throw new ArgumentException("The selected workflow definition is invalid.");
            var seed = inputBinding is not null && inputBinding.DefinitionId == choice.Id && inputBinding.DefinitionVersion == choice.Version
                ? inputBinding.Input : WorkflowContract.EmptyObject;
            var probe = new AgentWorkflowInputForm(form, described, seed);
            probe.Form.DiscardDraftAndClose();
            definition = described;
            if (inputBinding is null || inputBinding.DefinitionId != choice.Id || inputBinding.DefinitionVersion != choice.Version)
                inputBinding = new(choice.Id, choice.Version, WorkflowContract.EmptyObject);
            describing = false;
            message = choice.Label + "\nConfigure inputs before saving a changed binding. Required capabilities: " +
                string.Join(", ", described.RequiredCapabilities) +
                ".\nThe Worker revalidates the workflow when work is delegated. Saving does not start it or add grants.";
            UpdateControls();
        }
        catch (Exception error)
        {
            if (!disposed && (ticket == -1 || ticket == generation))
            {
                definition = null;
                message = "Workflow inputs unavailable; the saved binding is retained. " + error.Message;
                UpdateControls();
            }
        }
        finally
        {
            if (!disposed && ticket == generation)
            {
                describing = false;
                if (!connectionCurrent()) message = "The service connection changed. Reopen the editor; this draft has not been sent.";
                UpdateControls();
            }
        }
    }

    internal void ConfigureInputs()
    {
        try
        {
            RequireEditable();
            if (form.Selection(Mode) != Feature || definition is null || inputBinding is null || loading || describing)
                throw new InvalidOperationException("Read and select an installed exact workflow before configuring inputs.");
            var selected = definition;
            var expected = generation;
            var editor = new AgentWorkflowInputForm(form, selected, inputBinding.Input);
            JsonElement? accepted = null;
            editor.Form.ConfigureSubmitAsync("Use inputs", () =>
            {
                accepted = editor.ReadInput();
                return Task.FromResult(true);
            }, saveOnClose: false);
            try
            {
                if (showInputs(editor) != true) return;
                accepted ??= editor.ReadInput();
                RequireEditable();
                if (expected != generation || !ReferenceEquals(selected, definition))
                    throw new InvalidOperationException("The workflow selection changed. Reopen its input form.");
                var candidate = new AgentHelperWorkflowBinding(selected.Id, selected.Version, accepted.Value.Clone());
                _ = candidate.ToSettings();
                inputBinding = candidate;
                changed = true; inputConfirmed = true;
                message = selected.Id + " · " + selected.Version + "\nInputs are retained in this profile draft. Save applies the binding; no work has started.";
                UpdateControls();
            }
            finally { editor.Form.DiscardDraftAndClose(); }
        }
        catch (Exception error)
        {
            if (!disposed) { message = error.Message; UpdateControls(); }
        }
    }

    internal void ApplyTo(AgentProfile candidate)
    {
        if (!changed) return;
        RequireEditable();
        if (form.Selection(Mode) == Chat) { candidate.PluginSettings.Remove(AgentHelperWorkflowBinding.Key); return; }
        if (!CanSave || definition is null || inputBinding is null) throw new InvalidOperationException(message);
        if (definition.RequiredCapabilities.Any(x => x is "agents.delegate" or "agents.collaborate" ||
            !candidate.Capabilities.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("This helper lacks required workflow capabilities. Review its existing grants; the workflow editor does not add them.");
        candidate.PluginSettings[AgentHelperWorkflowBinding.Key] = inputBinding.ToSettings();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Changed = null;
        CancelRead(); lifetime.Cancel(); lifetime.Dispose();
    }
}
