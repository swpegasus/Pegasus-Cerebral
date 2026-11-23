using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    // The captured dispatch never follows a later UI connection selection.
    private static async Task<T> ReadAgentOwnerAsync<T>(
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch,
        string operation, object? arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await dispatch(new(operation, CerebralJson.Element(arguments ?? new { })), timeout.Token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Could not read the original service.");
        return CerebralJson.Read<T>(result.Data);
    }

    private sealed class AgentProfileEditor : IDisposable
    {
        private readonly IntegratorWindow owner;
        private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch;
        private readonly Func<long> connectionEpoch;
        private readonly long acceptedEpoch;
        private readonly CancellationToken lifetime;
        private readonly CancellationTokenSource readLifetime;
        private readonly bool localPipe;
        private readonly Action<AgentProfile> editBrowser;
        private readonly Dictionary<string, string> roles = new(StringComparer.Ordinal)
        {
            ["Cerebral — talks to the user"] = "lead",
            ["Helper — reports to Cerebral"] = "helper",
            ["Compactor — creates context checkpoints"] = "compactor"
        };
        private readonly Dictionary<string, string> compactors = new(StringComparer.Ordinal) { ["Use this agent"] = "" };
        private readonly AgentModelBindingEditor binding;
        private readonly AgentHelperWorkflowEditor helperWorkflow;
        private readonly TabControl fieldSurface;
        private Button permissionAction = null!;
        private readonly bool initialIdleEnabled;
        private readonly int initialIdleTrigger;
        private string? uncertainty;
        private bool saving;
        internal FieldsDialog Form { get; }
        internal AgentProfile Baseline { get; }
        internal AgentProfile Draft { get; private set; }
        internal AgentProfile? Submitted { get; private set; }
        internal AgentProfile? Acknowledged { get; private set; }
        internal bool SaveUncertain => uncertainty is not null;
        internal bool OriginalConnectionIsCurrent => connectionEpoch() == acceptedEpoch;
        internal bool IsNew { get; }
        internal bool FieldsAreEnabled => fieldSurface.IsEnabled;
        internal Task PendingModelRead => binding.PendingLoad;
        internal AgentHelperWorkflowEditor HelperWorkflow => helperWorkflow;

        private static AgentProfile Copy(AgentProfile profile) => CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
        private static bool IsRequirement(string value, string requirement) => value.Trim().Equals(requirement, StringComparison.OrdinalIgnoreCase);

        internal AgentProfileEditor(IntegratorWindow owner, AgentProfile original, HostSettings configuration,
            List<AgentProfile> configuredAgents, List<ModelRecord> defaultModels, string? defaultError,
            Func<string, CancellationToken, Task<List<ModelRecord>>> readModels,
            Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch,
            Func<long> connectionEpoch, long acceptedEpoch, bool localPipe, CancellationToken lifetime,
            bool startAdvanced = false, Action<AgentProfile>? editBrowser = null,
            Func<AgentWorkflowInputForm, bool?>? editWorkflowInput = null, bool startPermissions = false)
        {
            this.owner = owner; this.dispatch = dispatch; this.connectionEpoch = connectionEpoch;
            this.acceptedEpoch = acceptedEpoch; this.localPipe = localPipe; this.lifetime = lifetime;
            readLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            this.editBrowser = editBrowser ?? owner.EditBrowserSettings;
            Baseline = Copy(original); Draft = Copy(original); IsNew = string.IsNullOrEmpty(original.Id);
            var defaultModel = defaultModels.FirstOrDefault(model => model.Id == configuration.DefaultModelId);
            if (IsNew)
            {
                Draft.Id = Guid.NewGuid().ToString("N");
                Draft.AllowedRoots = [configuration.ProjectsDirectory];
                Draft.FollowsDefaultModel = true; Draft.FollowsModelBudgets = true;
                Draft.ModelId = configuration.DefaultModelId; Draft.ModelWorkerId = configuration.DefaultModelWorkerId;
                if (defaultModel is not null)
                {
                    Draft.ContextTokens = PreferredContext(defaultModel);
                    Draft.ResponseTokens = PreferredResponse(Draft.ContextTokens);
                }
            }
            foreach (var candidate in configuredAgents.Where(profile => profile.Id != Draft.Id)
                         .OrderBy(profile => profile.Role == "compactor" ? 0 : 1)
                         .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var basis = candidate.Name + " · " + FriendlyAgentRole(candidate.Role);
                var label = basis;
                for (var suffix = 2; compactors.ContainsKey(label); suffix++) label = basis + $" ({suffix})";
                compactors[label] = candidate.Id;
            }
            if (!string.IsNullOrWhiteSpace(Draft.CompactionProfileId) && !compactors.ContainsValue(Draft.CompactionProfileId))
                compactors["Missing saved agent · " + Draft.CompactionProfileId] = Draft.CompactionProfileId;

            var workers = AgentModelMachineChoices(configuration, Draft.ModelWorkerId);
            Form = new FieldsDialog(owner, IsNew ? "New agent" : "Edit agent",
                "Choose behavior and model routing. Advanced controls share this draft. Save applies the complete profile; discard leaves the saved profile unchanged.")
                .Tab("general", "General")
                .Add("name", "Name", Draft.Name).Add("purpose", "Purpose / specialization", Draft.Purpose)
                .Choice("role", "Execution role", roles.Keys,
                    roles.FirstOrDefault(pair => pair.Value == Draft.Role).Key ?? roles.Keys.ElementAt(1))
                .Tab("model", "Model & routing")
                .Choice("modelMode", "Model selection", [AgentModelBindingEditor.FollowDefault, AgentModelBindingEditor.PinnedModel],
                    Draft.FollowsDefaultModel || Draft.Role == "lead" ? AgentModelBindingEditor.FollowDefault : AgentModelBindingEditor.PinnedModel)
                .Choice("modelWorker", "Model inference machine", workers.Keys, workers.First(pair => pair.Value == Draft.ModelWorkerId).Key)
                .Choice("model", "Model on that machine", [AgentModelBindingEditor.ChooseModel], AgentModelBindingEditor.ChooseModel)
                .Add("routing", "Effective model route and capabilities", "", true).SetReadOnly("routing");
            Form.Tab("needs", "Model needs")
                .Check("requiresImages", "Understand attached images", ModelCapabilityPolicy.Requirements(Draft).Contains(ModelCapabilityIds.ImageInput))
                .Add("modelNeedsHelp", "Model capabilities",
                    "Text generation is automatic. Image understanding requires the selected model's verified support. Other saved requirements remain in Advanced.", true)
                .SetReadOnly("modelNeedsHelp")
                .Tab("context", "Context")
                .Choice("budgetMode", "Token budgets", ["Automatic — follow the selected model", "Custom — keep limits with this agent"],
                    Draft.FollowsModelBudgets ? "Automatic — follow the selected model" : "Custom — keep limits with this agent")
                .TokenBalance("balance", "Prompt/history ↔ response balance", Math.Max(AgentTokenLimits.MinimumContextTokens, Draft.ContextTokens),
                    Math.Max(AgentTokenLimits.MinimumResponseTokens, Draft.ResponseTokens))
                .Add("effective", "Effective token limits", "", true).SetReadOnly("effective")
                .Add("context", "Custom total context tokens", Draft.ContextTokens.ToString())
                .Add("response", "Exact response reservation (tokens)", Draft.ResponseTokens.ToString());

            var idle = Draft.PluginSettings.GetValueOrDefault("cerebral.idle-compaction");
            initialIdleEnabled = idle.ValueKind == JsonValueKind.Object && idle.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            initialIdleTrigger = idle.ValueKind == JsonValueKind.Object && idle.TryGetProperty("triggerPercent", out var trigger) && trigger.TryGetInt32(out var percent)
                ? Math.Clamp(percent, 50, 95) : 70;
            helperWorkflow = new(Form, Draft, dispatch, () => OriginalConnectionIsCurrent,
                () => !saving && !SaveUncertain && Acknowledged is null, readLifetime.Token, editWorkflowInput);
            AddAdvancedFields();
            AddPermissionFields();
            fieldSurface = (TabControl)EditorTab("general").Parent;
            var permissionTab = EditorTab("permissions");
            fieldSurface.Items.Remove(permissionTab); fieldSurface.Items.Insert(2, permissionTab);
            binding = new(Form, Draft, workers, configuration.DefaultModelWorkerId, defaultModel, defaultModels, defaultError,
                worker => readModels(worker, readLifetime.Token).WaitAsync(readLifetime.Token), connectionEpoch, acceptedEpoch, allowUnchangedConfiguration: !IsNew,
                configuredDefaultModelId: configuration.DefaultModelId);
            binding.ExternalSaveAllowed = () => !saving && !SaveUncertain && Acknowledged is null && helperWorkflow.CanSave;
            binding.ExternalSaveStatus = () => uncertainty ?? helperWorkflow.SaveStatus;
            helperWorkflow.Changed = binding.RefreshSaveEligibility;
            binding.Changed = RefreshBudgetPreview;
            Form.WhenChoiceChanged("budgetMode", _ => RefreshBudgetPreview())
                .WhenTokenBalanceChanged("balance", (dialog, response) => dialog.SetValue("response", response.ToString()))
                .WhenTextChanged("context", _ => RefreshBudgetPreview())
                .WhenTextChanged("response", _ => RefreshBudgetPreview())
                .WhenChoiceChanged("reasoning", _ => RefreshBudgetPreview())
                .WhenTextChanged("reasoningTokens", _ => RefreshBudgetPreview())
                .WhenChoiceChanged("role", dialog =>
                {
                    var lead = roles[dialog.Selection("role")] == "lead";
                    if (lead) dialog.SetSelection("modelMode", AgentModelBindingEditor.FollowDefault);
                    dialog.SetEnabled("modelMode", !lead);
                    RefreshBudgetPreview();
                });
            Form.SetEnabled("modelMode", Draft.Role != "lead");
            Form.ConfigureSubmitAsync("Save", SaveAsync, saveOnClose: false);
            if (startPermissions) EditorTab("permissions").IsSelected = true;
            else if (startAdvanced) EditorTab("advanced").IsSelected = true;
            binding.RefreshSaveEligibility();
            RefreshPermissionAction();
            Form.Activated += OnPermissionFormActivated;
        }

        private void AddPermissionFields()
        {
            Form.Tab("permissions", "Permissions")
                .Add("permissionScope", "Applies to this shared agent profile",
                    "Every chat using this profile is affected when the agent is saved. Chat-specific permissions are a separate policy.", true)
                .SetReadOnly("permissionScope")
                .ActionButton("Edit tool and capability permissions…", _ => OpenPermissionDraft())
                .Add("permissionSummary", "Current draft",
                    $"{Draft.AllowedTools.Count} saved tool entries · {Draft.Capabilities.Count} capability entries. Exact IDs remain in Advanced.", true)
                .SetReadOnly("permissionSummary");
            permissionAction = EditorChildren(EditorTab("permissions")).OfType<Button>().Single();
            AutomationProperties.SetAutomationId(permissionAction, "ProfilePermissionOpen");
        }

        private string? PermissionOpenRefusal() =>
            SaveUncertain ? "The last Save is unconfirmed on the original service. Keep this draft, read back the saved profile there, then reopen this editor before editing permissions. This request will not be resent." :
            Acknowledged is not null ? "This profile was saved. Reopen the saved profile before editing permissions. Your draft is retained." :
            saving ? "Wait for the current Save result before editing permissions. Your draft is retained." :
            readLifetime.IsCancellationRequested ? "This editor's owner session ended. Keep the draft and reopen the saved profile before editing permissions." :
            !OriginalConnectionIsCurrent ? "The service connection changed. Keep this draft and reopen the profile on the intended service before editing permissions." : null;

        private bool RefreshPermissionAction(bool showRefusal = false)
        {
            var refusal = PermissionOpenRefusal();
            permissionAction.IsEnabled = refusal is null;
            permissionAction.ToolTip = refusal ?? "Edit this shared profile's permission draft.";
            if (showRefusal && refusal is not null) Form.SetInlineStatus(refusal, true);
            return refusal is null;
        }

        private void OnPermissionFormActivated(object? sender, EventArgs args) => RefreshPermissionAction(showRefusal: true);

        private void OpenPermissionDraft()
        {
            // Recheck at the actual action boundary: a queued click can outlive its enabled state.
            if (!RefreshPermissionAction(showRefusal: true)) return;
            ProfilePermissionEditor editor;
            try { editor = CreatePermissionDraftEditor(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                var recovery = error is ArgumentException
                    ? "Review the exact permission IDs in Advanced, or read back the saved profile and reopen this editor."
                    : "Read back the saved profile on the original service and reopen this editor.";
                Form.SetInlineStatus("Permissions could not be opened. Your draft is retained. " + recovery + " " +
                    error.Message[..Math.Min(error.Message.Length, 500)], true);
                RefreshPermissionAction();
                return;
            }
            using (editor) editor.ShowDialog();
        }

        internal ProfilePermissionEditor CreatePermissionDraftEditor()
        {
            if (saving || SaveUncertain || Acknowledged is not null || readLifetime.IsCancellationRequested || !OriginalConnectionIsCurrent)
                throw new InvalidOperationException("Reopen the current profile before editing permissions.");
            // Validate before constructing a Window; invalid saved data must not leave a child window behind.
            var permissions = new ProfilePermissionDraft(Form.Lines("tools"), Form.Lines("capabilities"));
            return new ProfilePermissionEditor(Form, Form.Value("name"),
                permissions.Tools, permissions.Capabilities,
                () => OriginalConnectionIsCurrent && !saving && !SaveUncertain && Acknowledged is null,
                async token =>
                {
                    if (!OriginalConnectionIsCurrent) throw new InvalidOperationException("The service binding changed.");
                    if (Draft.WorkerId == "local")
                        return await ProfileRegisteredCatalog.ReadAsync(dispatch, () => OriginalConnectionIsCurrent, token);
                    var result = await dispatch(new("tools.list", CerebralJson.Element(new { profileId = Draft.Id })), token);
                    if (!OriginalConnectionIsCurrent) throw new InvalidOperationException("The service binding changed.");
                    if (!result.Success) throw new InvalidOperationException(result.Error ?? "The current tool catalog is unavailable.");
                    var descriptors = CerebralJson.Read<List<ToolDescriptor>>(result.Data);
                    return new ProfilePermissionCatalog(descriptors, Complete: false,
                        "Showing only current permitted local/shared tools. The remote receiver catalog is unavailable on this connection.");
                },
                changes =>
                {
                    if (!OriginalConnectionIsCurrent || saving || SaveUncertain || Acknowledged is not null)
                        throw new InvalidOperationException("The profile binding changed. No permission draft was applied.");
                    Form.SetValue("tools", string.Join('\n', changes.Tools));
                    Form.SetValue("capabilities", string.Join('\n', changes.Capabilities));
                    Form.SetValue("permissionSummary",
                        $"{changes.Tools.Count} tool entries · {changes.Capabilities.Count} capability entries. Applied to draft; select Save to persist.");
                }, readLifetime.Token);
        }

        private void AddAdvancedFields()
        {
            Form.Tab("advanced", "Advanced")
                .ActionButton("Show stable identity", dialog => MessageBox.Show(dialog,
                    "Stable ID: " + Draft.Id + "\n\nRenaming preserves saved conversation and tool-grant references.", "Agent identity"))
                .Add("worker", "Agent execution machine ID (distinct from model inference)", Draft.WorkerId)
                .Add("instructions", "Role specialization / per-agent instructions", Draft.Instructions, true)
                .Add("roots", "Allowed project and output roots", string.Join('\n', Draft.AllowedRoots), true)
                .ActionButton("Allow shared files (read / write)", dialog =>
                {
                    var capabilities = dialog.Lines("capabilities");
                    foreach (var grant in new[] { "shared.read", "shared.write" })
                        if (!capabilities.Contains(grant, StringComparer.Ordinal)) capabilities.Add(grant);
                    var tools = dialog.Lines("tools");
                    foreach (var tool in new[] { "shared.list", "shared.read", "shared.write", "shared.edit", "shared.mkdir" })
                        if (!tools.Contains(tool, StringComparer.Ordinal)) tools.Add(tool);
                    dialog.SetValue("capabilities", string.Join(Environment.NewLine, capabilities));
                    dialog.SetValue("tools", string.Join(Environment.NewLine, tools));
                })
                .Add("tools", "Allowed tool names (explicit * permits all granted tools)", string.Join('\n', Draft.AllowedTools), true)
                .Add("capabilities", "Capabilities, one per line", string.Join('\n', Draft.Capabilities), true)
                .Add("modelCapabilities", "Other required model capabilities (image understanding uses Model needs)",
                    string.Join('\n', Draft.RequiredModelCapabilities.Where(id => !IsRequirement(id, ModelCapabilityIds.TextGeneration) && !IsRequirement(id, ModelCapabilityIds.ImageInput))), true)
                .Choice("reasoning", "Reasoning mode", ["off", "on"], Draft.ReasoningMode)
                .Add("reasoningTokens", "Reasoning token budget (used when on)", Draft.ReasoningTokens.ToString())
                .Add("seconds", "Maximum execution seconds", Draft.MaxSeconds.ToString())
                .Add("steps", "Maximum agent steps", Draft.MaxSteps.ToString())
                .ActionButton("Browser capacity and safety limits…", _ => EditBrowserDraft())
                .Check("compact", "Automatically compact context", Draft.AutomaticCompaction)
                .Add("compactTrigger", "Compact at input capacity (%)", Draft.CompactionTriggerPercent.ToString())
                .Add("compactReduction", "Target context reduction (%)", Draft.CompactionReductionPercent.ToString())
                .Choice("compactor", "Compaction agent", compactors.Keys, compactors.First(pair => pair.Value == (Draft.CompactionProfileId ?? "")).Key)
                .Check("idleCompact", "Prepare context after final completion while I compose", initialIdleEnabled)
                .Add("idleCompactTrigger", "Idle preparation input capacity (%) · 50–95", initialIdleTrigger.ToString());
        }

        private static IEnumerable<DependencyObject> EditorChildren(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var descendant in EditorChildren(child)) yield return descendant;
            }
        }

        internal TabItem EditorTab(string id) => EditorChildren(Form).OfType<TabItem>()
            .Single(tab => AutomationProperties.GetAutomationId(tab) == "FieldsDialogTab." + id);

        internal void EditBrowserDraft()
        {
            var nested = Copy(Draft);
            try { editBrowser(nested); Draft = nested; }
            catch (Exception error) { Form.SetInlineStatus(error.Message, true); }
        }

        private AgentProfile ReadAdvancedCandidate()
        {
            var candidate = Copy(Draft);
            candidate.WorkerId = Form.Value("worker"); candidate.Instructions = Form.Raw("instructions");
            candidate.AllowedRoots = Form.Lines("roots"); candidate.AllowedTools = Form.Lines("tools"); candidate.Capabilities = Form.Lines("capabilities");
            // One control owns image-input. Preserve explicit text-generation and unrelated requirements.
            var originalExtras = Draft.RequiredModelCapabilities
                .Where(id => !IsRequirement(id, ModelCapabilityIds.TextGeneration) && !IsRequirement(id, ModelCapabilityIds.ImageInput)).ToList();
            var extras = Form.Lines("modelCapabilities");
            if (extras.Any(id => string.Equals(id.Trim(), ModelCapabilityIds.ImageInput, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(id.Trim(), ModelCapabilityIds.TextGeneration, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Use Model needs for image understanding; text generation is automatic.");
            var images = Form.Checked("requiresImages");
            if (!extras.SequenceEqual(originalExtras, StringComparer.Ordinal) ||
                images != Draft.RequiredModelCapabilities.Any(id => IsRequirement(id, ModelCapabilityIds.ImageInput)))
            {
                candidate.RequiredModelCapabilities = extras;
                var savedText = Draft.RequiredModelCapabilities.FirstOrDefault(id => IsRequirement(id, ModelCapabilityIds.TextGeneration));
                if (savedText is not null) candidate.RequiredModelCapabilities.Add(savedText);
                if (images) candidate.RequiredModelCapabilities.Add(
                    Draft.RequiredModelCapabilities.FirstOrDefault(id => IsRequirement(id, ModelCapabilityIds.ImageInput)) ?? ModelCapabilityIds.ImageInput);
            }
            candidate.ReasoningMode = Form.Selection("reasoning");
            candidate.ReasoningTokens = Form.Number("reasoningTokens", AgentTokenLimits.MinimumReasoningTokens, AgentTokenLimits.MaximumReasoningTokens);
            candidate.MaxSeconds = Form.Number("seconds", 1, 86400); candidate.MaxSteps = Form.Number("steps", 1, 10000);
            candidate.AutomaticCompaction = Form.Checked("compact");
            candidate.CompactionTriggerPercent = Form.Number("compactTrigger", AgentTokenLimits.MinimumCompactionTriggerPercent, AgentTokenLimits.MaximumCompactionTriggerPercent);
            candidate.CompactionReductionPercent = Form.Number("compactReduction", AgentTokenLimits.MinimumCompactionReductionPercent, AgentTokenLimits.MaximumCompactionReductionPercent);
            candidate.CompactionProfileId = compactors[Form.Selection("compactor")] is { Length: > 0 } id ? id : null;
            var idleEnabled = Form.Checked("idleCompact");
            var idleTrigger = Form.Number("idleCompactTrigger", 50, 95);
            // Merely visiting Advanced must not materialize or replace an optional module's saved object.
            if (idleEnabled != initialIdleEnabled || idleTrigger != initialIdleTrigger)
            {
                var prior = candidate.PluginSettings.GetValueOrDefault("cerebral.idle-compaction");
                var fields = prior.ValueKind == JsonValueKind.Object
                    ? prior.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                fields["schemaVersion"] = CerebralJson.Element(1); fields["enabled"] = CerebralJson.Element(idleEnabled);
                fields["triggerPercent"] = CerebralJson.Element(idleTrigger);
                candidate.PluginSettings["cerebral.idle-compaction"] = CerebralJson.Element(fields);
            }
            return candidate;
        }

        private bool ConfigurationRouteUnchanged(AgentProfile candidate) => !IsNew &&
            candidate.Role == Baseline.Role && candidate.FollowsDefaultModel == Baseline.FollowsDefaultModel &&
            candidate.WorkerId == Baseline.WorkerId && candidate.RemoteProfileId == Baseline.RemoteProfileId &&
            candidate.ModelId == Baseline.ModelId && candidate.ModelWorkerId == Baseline.ModelWorkerId &&
            candidate.RequiredModelCapabilities.SequenceEqual(Baseline.RequiredModelCapabilities, StringComparer.Ordinal);

        internal AgentProfile BuildCandidate()
        {
            if (!OriginalConnectionIsCurrent) throw new InvalidOperationException("The service connection changed. Reopen this editor; the draft has not been sent.");
            if (!binding.CanSave) throw new InvalidOperationException("The selected model route is unavailable. Keep the saved binding or choose an available model.");
            var candidate = ReadAdvancedCandidate();
            candidate.Name = Form.Value("name"); candidate.Purpose = Form.Value("purpose"); candidate.Role = roles[Form.Selection("role")];
            if (candidate.Name.Length == 0) throw new ArgumentException("Agent name is required.");
            candidate.FollowsDefaultModel = candidate.Role == "lead" || binding.FollowsDefault;
            candidate.ModelId = binding.ModelId; candidate.ModelWorkerId = binding.ModelWorkerId;
            ModelCapabilityPolicy.ValidateRequirements(candidate);
            if (binding.SelectedModel is null && !ConfigurationRouteUnchanged(candidate))
                throw new InvalidOperationException("Read the selected model before changing its route or required capabilities. Unchanged saved configuration can still be edited.");
            candidate.FollowsModelBudgets = Form.Selection("budgetMode") == "Automatic — follow the selected model";
            var context = candidate.FollowsModelBudgets ? candidate.ContextTokens : Form.Number("context", 512, 1_048_576);
            var response = candidate.FollowsModelBudgets ? candidate.ResponseTokens :
                Form.Number("response", AgentTokenLimits.MinimumResponseTokens, AgentTokenLimits.MaximumResponseTokens);
            if (binding.SelectedModel is { } model) ClampAgentBudgets(candidate, model, context, response);
            else
            {
                // Keep automatic saved budgets when metadata is unavailable; the Worker owns final validation.
                candidate.ContextTokens = context; candidate.ResponseTokens = response;
            }
            helperWorkflow.ApplyTo(candidate);
            return candidate;
        }

        private void RefreshBudgetPreview()
        {
            var automatic = Form.Selection("budgetMode") == "Automatic — follow the selected model";
            Form.SetVisible("context", !automatic).SetVisible("response", !automatic);
            if (binding.SelectedModel is not { } model)
            {
                Form.SetValue("effective", $"Saved limits: {Draft.ContextTokens:N0} context · {Draft.ResponseTokens:N0} response. Model information is unavailable; the Worker revalidates unchanged configuration on Save.");
                return;
            }
            var preview = Copy(Draft); preview.FollowsModelBudgets = automatic; preview.ReasoningMode = Form.Selection("reasoning");
            if (int.TryParse(Form.Raw("reasoningTokens"), out var reasoning)) preview.ReasoningTokens = reasoning;
            if (!automatic)
            {
                if (int.TryParse(Form.Raw("context"), out var context)) preview.ContextTokens = context;
                if (int.TryParse(Form.Raw("response"), out var response)) preview.ResponseTokens = response;
            }
            try
            {
                var resolved = AgentBudgetPolicy.Resolve(preview, model);
                var reserved = resolved.ReasoningMode == "on" ? resolved.ReasoningTokens : 0;
                Form.SetTokenBalance("balance", resolved.ContextTokens, resolved.ResponseTokens, reserved, !automatic,
                    "Preview only. The Worker returns the authoritative limits when saved.");
                Form.SetValue("effective", $"{resolved.ContextTokens:N0} context · {resolved.ResponseTokens:N0} response · {reserved:N0} reasoning. Preview; final saved limits may be normalized by the Worker.");
            }
            catch (ArgumentException error) { Form.SetValue("effective", error.Message); }
        }

        private bool Unknown(string detail)
        {
            uncertainty = "Save outcome is unknown on the original service. Keep this draft and inspect the saved profile before any new save; this request will not be resent. " + detail;
            binding.RefreshSaveEligibility();
            Form.SetCanSubmit(false).SetInlineStatus(uncertainty, true);
            return false;
        }

        internal async Task<bool> SaveAsync()
        {
            if (SaveUncertain || Acknowledged is not null || saving) return false;
            var candidate = BuildCandidate();
            Submitted = Copy(candidate);
            saving = true;
            // Inactive tab content may not inherit the selected visual parent's disabled state.
            var fieldEnableState = EditorChildren(fieldSurface).OfType<Control>().Distinct()
                .ToDictionary(control => control, control => control.IsEnabled);
            foreach (var control in fieldEnableState.Keys) control.IsEnabled = false;
            fieldSurface.IsEnabled = false;
            try
            {
                CommandResult result;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try { result = await dispatch(new("agents.save", CerebralJson.Element(candidate)), timeout.Token); }
                catch (Exception error)
                {
                    // Local transport wraps post-dispatch failures. Only its known pre-dispatch classes
                    // permit a repair/retry; HTTP and unclassified failures remain conservative unknowns.
                    if (localPipe && error is not LocalWorkerCommandUncertainException &&
                        error is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException or TimeoutException or UnauthorizedAccessException)
                        throw new InvalidOperationException("Save was not dispatched. The draft is retained. " + error.Message, error);
                    return Unknown("No acknowledged result was received.");
                }
                if (!result.Success) return Unknown(result.Error ?? "The service returned an unsuccessful result.");
                try
                {
                    var saved = CerebralJson.Read<AgentProfile>(result.Data);
                    if (saved.Id != candidate.Id || saved.Revision != checked(candidate.Revision + 1))
                        return Unknown("The returned profile identity or revision did not match this save.");
                    Acknowledged = Copy(saved);
                    Draft = Copy(saved);
                    return true;
                }
                catch (Exception)
                { return Unknown("The save acknowledgement could not be validated."); }
            }
            finally
            {
                saving = false; fieldSurface.IsEnabled = true;
                foreach (var pair in fieldEnableState) pair.Key.IsEnabled = pair.Value;
                helperWorkflow.RefreshControls();
                RefreshPermissionAction();
            }
        }

        public void Dispose()
        {
            Form.Activated -= OnPermissionFormActivated;
            helperWorkflow.Dispose(); binding.Dispose(); readLifetime.Cancel(); readLifetime.Dispose();
        }
    }

    private async Task RefreshAgentRowsOnConnectionAsync(long connection,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch, CancellationToken cancellationToken,
        CerebralClient? owner = null)
    {
        var profiles = await ReadAgentOwnerAsync<List<AgentProfile>>(dispatch, "agents.list", null, cancellationToken);
        if (connection == queueConnectionEpoch)
            AdoptComputeProfiles(connection, profiles, owner);
    }

    private bool AcknowledgedAgentOwnerCurrent(long connection, CerebralClient? owner) =>
        connection == queueConnectionEpoch && (owner is null ? !IsConnected : IsConnected && ReferenceEquals(owner, Client));

    private async Task RefreshAcknowledgedAgentAsync(AgentProfile saved, long connection, Func<Task> refresh,
        CerebralClient? owner = null)
    {
        if (!AcknowledgedAgentOwnerCurrent(connection, owner)) return;
        try { await refresh(); }
        catch (Exception error)
        {
            if (!AcknowledgedAgentOwnerCurrent(connection, owner)) return;
            SetStatus($"Agent {saved.Name} was saved at revision {saved.Revision}, but the list could not refresh: " + error.Message, true);
            return;
        }
        // A same-endpoint reconnect keeps the queue epoch, but replaces the Client.
        if (!AcknowledgedAgentOwnerCurrent(connection, owner)) return;
        SetStatus($"Agent {saved.Name} saved at revision {saved.Revision}.");
    }

    private async Task CompleteAcknowledgedAgentAsync(AgentProfile saved, long connection, CerebralClient? owner,
        Func<Task> refresh, Func<Task> resumeSetup)
    {
        await RefreshAcknowledgedAgentAsync(saved, connection, refresh, owner);
        if (!AcknowledgedAgentOwnerCurrent(connection, owner)) return;
        try { await resumeSetup(); }
        catch (Exception error)
        {
            if (AcknowledgedAgentOwnerCurrent(connection, owner))
                SetStatus($"Agent {saved.Name} was saved, but subsequent setup could not finish: " + error.Message, true);
        }
    }
}

