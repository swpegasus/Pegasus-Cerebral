using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed record ModelWorkerChoice(string Id, string Name);
    private readonly ComboBox modelWorkerPicker = new()
    {
        MinWidth = 260, DisplayMemberPath = nameof(ModelWorkerChoice.Name),
        SelectedValuePath = nameof(ModelWorkerChoice.Id), Margin = new Thickness(8, 0, 0, 8)
    };
    private readonly TextBlock modelWorkerNetwork = Ui.Text("", 12);
    private readonly TextBlock modelSelectionHelp = Ui.Text("", 12, Ui.Muted);
    private Button? modelUse, modelLoad, modelDetails;
    private bool changingModelWorkerChoices;
    private bool refreshingModelLibrary;
    private int modelLibraryRefreshVersion;
    private string loadedModelWorkerId = "local";
    private string SelectedModelWorkerId => modelWorkerPicker.SelectedValue as string ?? "local";

    private UIElement ModelLibraryContent()
    {
        var panel = new DockPanel();
        var machines = new StackPanel { Orientation = Orientation.Horizontal };
        machines.Children.Add(Ui.Text("Model location", 13));
        machines.Children.Add(modelWorkerPicker);
        var header = new StackPanel();
        header.Children.Add(machines);
        modelWorkerNetwork.Margin = new Thickness(0, 0, 0, 8);
        modelWorkerNetwork.TextWrapping = TextWrapping.Wrap;
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelWorkerNetwork, "ModelLibraryNetwork");
        header.Children.Add(modelWorkerNetwork);
        setupHint.TextWrapping = TextWrapping.Wrap;
        setupHint.Margin = new Thickness(0, 4, 0, 8);
        System.Windows.Automation.AutomationProperties.SetAutomationId(setupHint, "ConfigurationFirstRunInventory");
        header.Children.Add(setupHint);
        modelSelectionHelp.TextWrapping = TextWrapping.Wrap;
        modelSelectionHelp.Margin = new Thickness(0, 0, 0, 8);
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelSelectionHelp, "ModelLibrarySelectionHelp");
        header.Children.Add(modelSelectionHelp);
        header.Children.Add(Ui.Button("Refresh setup inventory", () => Run(() => OfferSetupIfNeeded(true))));
        header.Children.Add(CreateModelCapacityContent());
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(models);
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelWorkerPicker, "ModelLibraryWorker");
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelUse!, "ModelLibraryUse");
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelLoad!, "ModelLibraryLoad");
        System.Windows.Automation.AutomationProperties.SetAutomationId(modelDetails!, "ModelLibraryDetails");
        models.SelectionChanged += (_, _) => RefreshModelSelectionActions();
        ServiceStatusChanged += (_, _) => RefreshModelSelectionActions();
        modelWorkerPicker.SelectionChanged += (_, _) =>
        {
            InvalidateModelCapacity();
            RefreshModelWorkerNetwork();
            RefreshModelSelectionActions();
            if (!changingModelWorkerChoices && IsConnected)
                Run(async () => { await RefreshModels(); await RefreshAgents(); });
        };
        RefreshModelWorkerChoices();
        RefreshModelSelectionActions();
        return panel;
    }

    private void RefreshModelSelectionActions()
    {
        var selected = models.SelectedItem is ModelRow row &&
            (models.ItemsSource as IEnumerable<ModelRow>)?.Contains(row) == true;
        var reason = !IsConnected ? "Connect to the Worker to use or manage a model." :
            !ComputeProjectionCurrent ? "Wait for this Worker's model library to load." :
            refreshingModelLibrary || loadedModelWorkerId != SelectedModelWorkerId
                ? "Wait for the selected machine's model library to load." :
            !selected ? "Select a model in the library to use, load, or inspect it." : "";
        var available = reason.Length == 0;
        modelSelectionHelp.Text = reason;
        modelSelectionHelp.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { modelUse, modelLoad, modelDetails })
        {
            if (button is null) continue;
            button.IsEnabled = available;
            button.ToolTip = available ? null : reason;
            ToolTipService.SetShowOnDisabled(button, true);
            System.Windows.Automation.AutomationProperties.SetHelpText(button, reason);
        }
    }

    private void RefreshModelWorkerChoices()
    {
        var selected = SelectedModelWorkerId;
        var choices = AgentModelMachineChoices(settings, "local")
            .Select(x => new ModelWorkerChoice(x.Value, x.Key)).ToList();
        changingModelWorkerChoices = true;
        try
        {
            modelWorkerPicker.ItemsSource = choices;
            modelWorkerPicker.SelectedValue = choices.Any(x => x.Id == selected) ? selected : "local";
        }
        finally { changingModelWorkerChoices = false; }
        RefreshModelWorkerNetwork();
        RefreshComputeOverview();
    }

    private void RefreshModelWorkerNetwork()
    {
        var workerId = SelectedModelWorkerId;
        modelWorkerNetwork.Text = workerId == "local"
            ? "Models on this machine use the local worker pipe. No network subnet is needed."
            : settings.Workers.FirstOrDefault(item => item.Id == workerId) is { } worker
                ? "Worker " + WorkerDisplayName(worker) + " · " + WorkerNetworkRoute.Describe(worker.Url) : "";
        modelWorkerPicker.ToolTip = modelWorkerNetwork.Text;
    }

    private static string WorkerDisplayName(WorkerRegistration worker) =>
        string.IsNullOrWhiteSpace(worker.Name) ? worker.Id : worker.Name.Trim();

    private static string WorkerChoiceLabel(WorkerRegistration worker) =>
        WorkerDisplayName(worker) + " · " + WorkerNetworkRoute.ParseEndpoint(worker.Url).Display;

    private string ModelWorkerName(string workerId) => workerId == "local" ? "this machine" :
        settings.Workers.FirstOrDefault(x => x.Id == workerId) is { } worker ? WorkerDisplayName(worker) : workerId;

    // The coordinator owns the connection and validates remote assignment. The interface sends IDs,
    // never a management credential or an arbitrary remote command.
    private async Task<List<ModelRecord>> ModelLibraryAsync(string workerId) => workerId == "local"
        ? await Get<List<ModelRecord>>("models.list")
        : await Get<List<ModelRecord>>("workers.models.list", new { workerId });

    private static object WorkerModelArguments(string workerId, object? arguments)
    {
        var values = arguments is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) :
            CerebralJson.Element(arguments).EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
        values.Add("workerId", CerebralJson.Element(workerId));
        return values;
    }

    private async Task<T> ModelLibraryGet<T>(string operation, object? arguments = null, string? workerId = null)
    {
        workerId ??= loadedModelWorkerId;
        if (workerId == "local") return await Get<T>(operation, arguments);
        if (operation is not ("models.list" or "models.inspect" or "models.scan" or "models.save" or "models.download" or "models.diagnostics" or "models.idle.set" or "models.capacity.preview" or "models.capacity.apply"))
            throw new InvalidOperationException("This model operation has no coordinator-managed remote route.");
        if (operation == "models.save") arguments = new { model = arguments };
        return await Get<T>("workers." + operation, WorkerModelArguments(workerId, arguments));
    }

    private async Task<CommandResult> ModelLibraryCommand(string operation, object? arguments = null, string? workerId = null)
    {
        workerId ??= loadedModelWorkerId;
        if (workerId == "local") return await Command(operation, arguments);
        if (operation is not ("models.load" or "models.unload" or "models.import" or "models.download"))
            throw new InvalidOperationException("This model action is not available for a remote model library.");
        if (operation is "models.import" or "models.download") arguments = new { request = arguments };
        return await Command("workers." + operation, WorkerModelArguments(workerId, arguments));
    }

    private async Task AddRemoteModels()
    {
        var workerId = loadedModelWorkerId;
        const string scan = "Scan the worker's model folder";
        const string import = "Register an existing file on the worker";
        const string download = "Download a model on the worker";
        var form = new FieldsDialog(this, "Add models · " + ModelWorkerName(workerId),
            "Paths belong to the selected worker. Downloads and imports run there; models remain unloaded until selected.", "Continue")
            .Choice("source", "Source", [scan, import, download], scan);
        if (form.ShowDialog() != true) return;
        if (form.Selection("source") == scan)
        {
            var result = await ModelLibraryGet<ModelRepositoryScan>("models.scan", workerId: workerId);
            var choices = result.Models.Where(x => x.Error is null && x.RegisteredId is null)
                .ToDictionary(x => x.Name + " · " + x.Size + " · " + x.Path, x => x);
            if (choices.Count == 0)
            {
                Ui.ShowText(this, "Worker model folder", result.Directory + "\n\n" +
                    string.Join("\n", result.Models.Select(x => x.Name + " · " + x.Status)) + "\n" + string.Join("\n", result.Issues));
                return;
            }
            var select = new FieldsDialog(this, "Discovered worker models", result.Directory, "Register")
                .Choice("model", "Model", choices.Keys, choices.Keys.First());
            if (select.ShowDialog() != true) return;
            var item = choices[select.Selection("model")];
            await ModelLibraryCommand("models.import", new { path = item.Path, name = item.Name, copy = false }, workerId);
        }
        else
        {
            var isDownload = form.Selection("source") == download;
            var source = new FieldsDialog(this, isDownload ? "Worker download" : "Register worker model",
                "The selected worker validates this request and stores the model in its own repository.", isDownload ? "Download" : "Register")
                .Add("source", isDownload ? "Direct HTTP/HTTPS model URL" : "Absolute GGUF path on the worker", "")
                .Add("name", "Model display name", "");
            if (isDownload) source.Add("file", "Repository file name", "model.gguf");
            if (source.ShowDialog() != true) return;
            SetStatus(isDownload ? "Downloading on the selected worker…" : "Registering the worker's model…");
            await ModelLibraryCommand(isDownload ? "models.download" : "models.import",
                isDownload ? new { url = source.Value("source"), name = source.Value("name"), fileName = source.Value("file") } :
                    (object)new { path = source.Value("source"), name = source.Value("name"), copy = false }, workerId);
        }
        await RefreshModels(); SetStatus("Worker model registered.");
    }
}
