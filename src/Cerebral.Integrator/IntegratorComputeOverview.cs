using System.Globalization;
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
    // Catalogs and diagnostic samples are scoped to the owner connection and exact machine.
    // Configured limits, runtime allocation, hardware capacity and working contexts are distinct.
    private sealed record ComputeLibrary(long Connection, long Generation, DateTimeOffset ReadAt, IReadOnlyList<ModelRecord> Models);
    private sealed record ComputeProfileSnapshot(long Connection, IReadOnlyList<AgentProfile> Profiles);
    private sealed record ComputeDiagnosticsCapture(long Connection, string MachineId, long LibraryGeneration,
        int RefreshVersion, int SettingsRevision, IReadOnlyDictionary<string, string> ModelPins);
    private sealed record ComputeRuntimeSample(long Connection, string ConfigurationPin, DateTimeOffset ReadAt,
        int? AvailableSlots, int? RuntimeUsers, int? QuarantinedSlots);
    private sealed record ComputeResourceSample(long Connection, DateTimeOffset ReadAt, ResourceInfo Resources);
    private sealed record ComputeMachineRow(string Id, string Name, string Availability, string Models, string Profiles,
        string Memory, string JobLimit);
    private sealed record ComputeModelRow(string Id, string Name, string ConfiguredSlots, string MeasuredMaximum,
        string ObservedSlots, string RuntimeUsers, string WorkingContexts, string Profiles, string RecordedState);
    private readonly Dictionary<string, ComputeLibrary> computeLibraries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Machine, string Model), ComputeRuntimeSample> computeRuntime = [];
    private readonly Dictionary<string, ComputeResourceSample> computeResources = new(StringComparer.Ordinal);
    private RowDefinition? computeListRow;
    private RowDefinition? computeDetailRow;
    private double computePreferredTopShare = IntegratorSettingsLayoutCodec.DefaultMachinesTopShare;
    private long computeConnection = -1;
    private CerebralClient? computeClient;
    private long computeCatalogGeneration;
    private ComputeProfileSnapshot? computeProfileData;
    private bool ComputeProjectionCurrent => IsConnected ? ReferenceEquals(computeClient, Client) : computeClient is null;
    private bool CanAdoptComputeRead(CerebralClient? owner) => IsConnected
        ? owner is not null && ReferenceEquals(owner, Client)
        : owner is null && computeClient is null;
    private IReadOnlyList<AgentProfile>? CurrentComputeProfiles =>
        ComputeProjectionCurrent && computeProfileData is { } snapshot && snapshot.Connection == queueConnectionEpoch
            ? snapshot.Profiles : null;
    private readonly DataGrid computeMachines = Ui.Grid(("Machine", "Name", 1.5), ("Availability", "Availability", 2),
        ("Models", "Models", 1), ("Profiles", "Profiles", .8), ("Memory pool", "Memory", 1.7), ("Job limit", "JobLimit", 1));
    private readonly DataGrid computeModels = Ui.Grid(("Model", "Name", 1.6), ("Configured slots", "ConfiguredSlots", 1),
        ("Measured maximum", "MeasuredMaximum", 1.2), ("Observed allocation", "ObservedSlots", 1.8),
        ("Runtime users", "RuntimeUsers", 1), ("Working contexts", "WorkingContexts", 1.2),
        ("Assigned profiles", "Profiles", 1), ("Saved state", "RecordedState", 1));
    private readonly DataGrid computeProfiles = Ui.Grid(("Profile", "Name", 1.5), ("Role", "Role", 1),
        ("Execution machine", "ExecutionMachine", 1.2), ("Inference machine", "InferenceMachine", 1.2),
        ("Model", "Model", 1.5), ("Binding", "Binding", 1));
    private readonly TextBlock computeMachineSummary = Ui.Text("Select a machine to inspect its model and profile relationships.", 13);

    private UIElement ComputeMachinesContent()
    {
        AutomationProperties.SetAutomationId(computeMachines, "ComputeMachines");
        AutomationProperties.SetAutomationId(computeModels, "ComputeMachineModels");
        AutomationProperties.SetAutomationId(computeProfiles, "ComputeMachineProfiles");
        SetComputeColumnMinimums(computeMachines, [180, 210, 110, 190, 260, 150]);
        SetComputeColumnMinimums(computeModels, [180, 130, 150, 230, 130, 160, 150, 120]);
        SetComputeColumnMinimums(computeProfiles, [180, 120, 180, 180, 180, 140]);
        SetComputeColumnMinimums(agents, [180, 110, 170, 170, 180, 130, 130, 110, 190]);
        SetComputeColumnMinimums(models, [180, 130, 140, 250, 110, 160, 180, 130, 150, 120]);
        computeMachines.SelectionMode = DataGridSelectionMode.Single;
        computeMachines.SelectionChanged += (_, _) => RefreshComputeMachineDetails();
        // A saved profile can refresh while another page is visible. Reproject when this page returns,
        // without adding a command or changing the accepted editor's acknowledgement path.
        computeMachines.IsVisibleChanged += (_, change) => { if (change.NewValue is true) RefreshComputeOverview(); };
        computeMachineSummary.TextWrapping = TextWrapping.Wrap;
        computeMachineSummary.Margin = new Thickness(0, 8, 0, 8);
        var details = new TabControl();
        details.Items.Add(new TabItem { Header = "Models & slots", Content = computeModels });
        details.Items.Add(new TabItem { Header = "Profiles using this machine", Content = computeProfiles });
        AutomationProperties.SetAutomationId(details, "ComputeMachineDetails");
        var panel = new Grid();
        computeListRow = new() { Height = new GridLength(computePreferredTopShare, GridUnitType.Star), MinHeight = 120 };
        computeDetailRow = new() { Height = new GridLength(1 - computePreferredTopShare, GridUnitType.Star), MinHeight = 150 };
        panel.RowDefinitions.Add(computeListRow);
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(computeDetailRow);
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(computeMachines);
        Grid.SetRow(computeMachineSummary, 1); panel.Children.Add(computeMachineSummary);
        // The summary's bottom margin leaves a native divider without moving existing row identities.
        var divider = CreateSettingsRowDivider("ComputeMachineDivider", "Resize machine list and details",
            computeListRow, computeDetailRow, share =>
            {
                if (Math.Abs(computePreferredTopShare - share) < .000001) return;
                computePreferredTopShare = share; SettingsPaneChanged();
            });
        Grid.SetRow(divider, 1); Panel.SetZIndex(divider, 1); panel.Children.Add(divider);
        Grid.SetRow(details, 2); panel.Children.Add(details);
        var registrationPanel = new Expander { Header = "Remote machine connections", Content = workers,
            MaxHeight = 220, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(registrationPanel, "ComputeRemoteConnections");
        Grid.SetRow(registrationPanel, 3); panel.Children.Add(registrationPanel);
        RefreshComputeOverview();
        return panel;
    }

    private static void SetComputeColumnMinimums(DataGrid grid, IReadOnlyList<double> widths)
    {
        for (var index = 0; index < grid.Columns.Count; index++)
        {
            grid.Columns[index].MinWidth = widths[index];
            if (grid.Columns[index] is DataGridTextColumn text)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 3, 8, 3)));
                style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                    new System.Windows.Data.Binding(text.Binding is System.Windows.Data.Binding binding ? binding.Path.Path : "")));
                text.ElementStyle = style;
            }
        }
    }

    private void EnsureComputeConnection()
    {
        var owner = IsConnected ? Client : null;
        if (computeConnection == queueConnectionEpoch && ReferenceEquals(computeClient, owner)) return;
        computeClient = owner;
        computeConnection = queueConnectionEpoch;
        computeLibraries.Clear(); computeRuntime.Clear(); computeResources.Clear();
        computeProfileData = null;
        agents.ItemsSource = null; models.ItemsSource = null; configuredModels = [];
    }

    private bool AdoptComputeProfiles(long connection, IReadOnlyList<AgentProfile> profiles, CerebralClient? owner = null)
    {
        if (connection != queueConnectionEpoch || !CanAdoptComputeRead(owner)) return false;
        EnsureComputeConnection();
        computeProfileData = new(connection,
            profiles.Select(profile => CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))).ToArray());
        RefreshComputeOverview();
        return true;
    }

    private bool RememberComputeLibrary(long connection, string machineId, IReadOnlyList<ModelRecord> library,
        CerebralClient? owner = null)
    {
        if (connection != queueConnectionEpoch || !CanAdoptComputeRead(owner)) return false;
        EnsureComputeConnection();
        var copied = library.Select(model => CerebralJson.Read<ModelRecord>(CerebralJson.Element(model))).ToArray();
        computeLibraries[machineId] = new(connection, ++computeCatalogGeneration, DateTimeOffset.UtcNow, copied);
        if (machineId == loadedModelWorkerId) configuredModels = copied.Select(model => CerebralJson.Read<ModelRecord>(CerebralJson.Element(model))).ToList();
        foreach (var key in computeRuntime.Keys.Where(key => key.Machine == machineId).ToArray())
            if (!copied.Any(model => model.Id == key.Model && ComputeConfigurationPin(model) == computeRuntime[key].ConfigurationPin))
                computeRuntime.Remove(key);
        RefreshComputeOverview();
        return true;
    }

    private ComputeDiagnosticsCapture CaptureComputeDiagnostics(string machineId)
    {
        EnsureComputeConnection();
        if (!computeLibraries.TryGetValue(machineId, out var library) || library.Connection != queueConnectionEpoch)
            throw new InvalidOperationException("Refresh this machine's model library before inspecting runtime allocation.");
        var pins = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            library.Models.ToDictionary(model => model.Id, ComputeConfigurationPin, StringComparer.Ordinal));
        var capture = new ComputeDiagnosticsCapture(queueConnectionEpoch, machineId, library.Generation, modelLibraryRefreshVersion, settings.Revision, pins);
        if (!IsComputeDiagnosticsCurrent(capture))
            throw new InvalidOperationException("The model library is refreshing or changed. Refresh and reopen its details.");
        return capture;
    }

    private bool IsComputeDiagnosticsCurrent(ComputeDiagnosticsCapture capture)
    {
        if (!ComputeProjectionCurrent || capture.Connection != queueConnectionEpoch || computeConnection != queueConnectionEpoch ||
            capture.RefreshVersion != modelLibraryRefreshVersion || capture.SettingsRevision != settings.Revision ||
            !computeLibraries.TryGetValue(capture.MachineId, out var library) ||
            library.Connection != capture.Connection || library.Generation != capture.LibraryGeneration ||
            library.Models.Count != capture.ModelPins.Count) return false;
        if (library.Models.Any(model => !capture.ModelPins.TryGetValue(model.Id, out var pin) ||
            pin != ComputeConfigurationPin(model))) return false;
        // The caller's active DTO list must not have changed without a catalog adoption.
        return loadedModelWorkerId != capture.MachineId || (configuredModels.Count == capture.ModelPins.Count &&
            configuredModels.All(model => capture.ModelPins.TryGetValue(model.Id, out var pin) && pin == ComputeConfigurationPin(model)));
    }

    private async Task<JsonElement?> ReadComputeDiagnosticsAsync(ComputeDiagnosticsCapture capture,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch, CancellationToken token)
    {
        if (!IsComputeDiagnosticsCurrent(capture)) return null;
        var diagnostics = await ReadAgentOwnerAsync<JsonElement>(dispatch,
            capture.MachineId == "local" ? "diagnostics" : "workers.models.diagnostics",
            capture.MachineId == "local" ? null : new { workerId = capture.MachineId }, token);
        return RememberComputeDiagnostics(capture, diagnostics, DateTimeOffset.UtcNow) ? diagnostics : null;
    }

    private bool RememberComputeDiagnostics(ComputeDiagnosticsCapture capture, JsonElement diagnostics, DateTimeOffset readAt)
    {
        // Check before clearing samples: a stale completion cannot erase a newer accepted observation.
        if (!IsComputeDiagnosticsCurrent(capture)) return false;
        var machineId = capture.MachineId;
        foreach (var key in computeRuntime.Keys.Where(key => key.Machine == machineId).ToArray()) computeRuntime.Remove(key);
        if (diagnostics.ValueKind == JsonValueKind.Object &&
            diagnostics.TryGetProperty("models", out var samples) && samples.ValueKind == JsonValueKind.Array)
        {
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { } modelId ||
                    !capture.ModelPins.TryGetValue(modelId, out var pin)) continue;
                int? Count(string name) => sample.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;
                int? quarantined = sample.TryGetProperty("quarantinedSlots", out var quarantine) &&
                    quarantine.ValueKind == JsonValueKind.Object ? quarantine.EnumerateObject().Count() : null;
                computeRuntime[(machineId, modelId)] = new(capture.Connection, pin, readAt,
                    Count("availableSlots"), Count("busy"), quarantined);
            }
        }
        if (diagnostics.ValueKind == JsonValueKind.Object &&
            diagnostics.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
            computeResources[machineId] = new(capture.Connection, readAt, CerebralJson.Read<ResourceInfo>(resources));
        RefreshComputeOverview();
        return true;
    }

    private void ReprojectComputeDependents()
    {
        var selectedProfile = (agents.SelectedItem as AgentRow)?.Id;
        var rows = CurrentComputeProfiles?.Select(CreateAgentRow).ToArray() ?? [];
        agents.ItemsSource = rows;
        agents.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedProfile);
        if (refreshingModelLibrary) return;
        var selectedModel = (models.SelectedItem as ModelRow)?.Record.Id;
        var catalog = ComputeProjectionCurrent && loadedModelWorkerId == SelectedModelWorkerId
            ? computeLibraries.GetValueOrDefault(loadedModelWorkerId) : null;
        var profiles = CurrentComputeProfiles;
        var modelRows = catalog?.Models.Select(model => new ModelRow(model,
            model.Id == settings.DefaultModelId && loadedModelWorkerId == settings.DefaultModelWorkerId,
            profiles is null ? null : ProfilesUsingModel(model, profiles, settings.DefaultModelId, loadedModelWorkerId).Count())).ToArray() ?? [];
        models.ItemsSource = modelRows;
        models.SelectedItem = modelRows.FirstOrDefault(row => row.Record.Id == selectedModel);
    }

    private void RefreshComputeOverview()
    {
        EnsureComputeConnection();
        ReprojectComputeDependents();
        var selectedId = (computeMachines.SelectedItem as ComputeMachineRow)?.Id ?? "local";
        var profiles = CurrentComputeProfiles;
        ComputeMachineRow Row(string id, string name, bool requiresSetup)
        {
            var catalog = computeLibraries.GetValueOrDefault(id);
            var profileCounts = profiles is null ? "Not observed" :
                $"{profiles.Count(profile => profile.WorkerId == id):N0} execution / {profiles.Count(profile => ResolveProfileModelWorker(profile) == id):N0} inference";
            var availability = id == "local" ? (IsConnected ? "Owner connected; runtime unobserved" : "Owner offline; runtime unobserved")
                : requiresSetup ? "Needs setup" : "Registered; reachability unobserved";
            return new(id, name, availability, catalog is null ? "Not inspected" : $"{catalog.Models.Count:N0} inspected",
                profileCounts,
                computeResources.TryGetValue(id, out var resource) ? ComputeMemoryPool(resource.Resources) + $" (read {resource.ReadAt.LocalDateTime:HH:mm:ss})" : "Not observed",
                id == "local" ? $"{settings.MaxConcurrentJobs:N0} configured jobs" : "Not inspected");
        }
        var machines = new List<ComputeMachineRow> { Row("local", "This machine · " + settings.Name, false) };
        machines.AddRange(settings.Workers.Where(worker => worker.Id != "local").Select(worker =>
            Row(worker.Id, WorkerDisplayName(worker), worker.RequiresConfiguration)));
        computeMachines.ItemsSource = machines;
        computeMachines.SelectedItem = machines.FirstOrDefault(row => row.Id == selectedId) ?? machines[0];
        RefreshComputeMachineDetails();
    }

    private string ResolveProfileModelWorker(AgentProfile profile) =>
        profile.FollowsDefaultModel ? settings.DefaultModelWorkerId : profile.ModelWorkerId;

    private void RefreshComputeMachineDetails()
    {
        if (computeMachines.SelectedItem is not ComputeMachineRow machine) return;
        var profiles = CurrentComputeProfiles;
        var profileRows = profiles?.Select(CreateAgentRow).ToArray() ?? [];
        var catalog = computeLibraries.GetValueOrDefault(machine.Id);
        computeProfiles.ItemsSource = profileRows.Where(row => row.Record.WorkerId == machine.Id ||
            ResolveProfileModelWorker(row.Record) == machine.Id).ToArray();
        computeModels.ItemsSource = catalog?.Models.Select(model =>
        {
            var sample = computeRuntime.GetValueOrDefault((machine.Id, model.Id));
            var observed = sample is null ? "Not observed" :
                $"{CountText(sample.AvailableSlots)} available / {CountText(sample.QuarantinedSlots)} quarantined";
            var assigned = profileRows.Count(row => ResolveProfileModelWorker(row.Record) == machine.Id &&
                (row.Record.FollowsDefaultModel ? settings.DefaultModelId : row.Record.ModelId) == model.Id);
            return new ComputeModelRow(model.Id, model.Name, model.ParallelRequests.ToString("N0"), "Not measured",
                observed, sample is null ? "Not observed" : CountText(sample.RuntimeUsers), "Not observed",
                profiles is null ? "Not observed" : assigned.ToString("N0"), FriendlyState(model.State));
        }).ToArray() ?? [];
        var sampled = computeRuntime.Where(pair => pair.Key.Machine == machine.Id).Select(pair => pair.Value.ReadAt)
            .DefaultIfEmpty().Max();
        var observation = sampled == default ? "Runtime allocation has not been observed." :
            $"Last allocation read {sampled.LocalDateTime:yyyy-MM-dd HH:mm:ss}; it does not prove current readiness.";
        computeMachineSummary.Text = machine.Name + "\n" +
            (machine.Id == "local" ? $"Configured job ceiling: {settings.MaxConcurrentJobs:N0}. " : "") +
            (catalog is null ? "Open the model library to inspect this machine's configuration. " :
                $"Model configuration read {catalog.ReadAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}. ") +
            observation + "\nConfigured slots belong to a model; job limits belong to a machine. " +
            "Profiles can have multiple independent contexts. Runtime users include callers waiting for a slot; working contexts require accepted-work telemetry.";
    }

    private static string ComputeConfigurationPin(ModelRecord model) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model, CerebralJson.Options)));

    private static string CountText(int? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "?";

    private static string ComputeMemoryPool(ResourceInfo resources)
    {
        var unified = resources.Gpus.FirstOrDefault(gpu => gpu.Unified);
        var pools = new List<string> { unified is not null
            ? $"Shared CPU/GPU: {FormatNullableMemory(resources.SystemTotalMb ?? unified.TotalMb)} total · {FormatNullableMemory(resources.SystemAvailableMb ?? unified.FreeMb)} available"
            : $"RAM: {FormatNullableMemory(resources.SystemTotalMb)} total · {FormatNullableMemory(resources.SystemAvailableMb)} available" };
        pools.AddRange(resources.Gpus.Where(gpu => !gpu.Unified)
            .Select(gpu => $"{gpu.Name} VRAM: {FormatNullableMemory(gpu.TotalMb)} total · {FormatNullableMemory(gpu.FreeMb)} free"));
        return string.Join(" / ", pools);
    }

    private async Task OpenComputeMachineModels()
    {
        if (computeMachines.SelectedItem is not ComputeMachineRow machine) throw new InvalidOperationException("Select a machine first.");
        if ((modelWorkerPicker.ItemsSource as IEnumerable<ModelWorkerChoice>)?.Any(choice => choice.Id == machine.Id) != true)
            throw new InvalidOperationException("This machine needs an admitted owner connection before its model library can be inspected.");
        changingModelWorkerChoices = true;
        try { modelWorkerPicker.SelectedValue = machine.Id; }
        finally { changingModelWorkerChoices = false; }
        SelectTab("Models");
        if (IsConnected) { await RefreshModels(); await RefreshAgents(); }
    }
}

