using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Desktop.ModelOperations;

/// <summary>Normal import owns original bytes and grants. This selection carries opaque admitted references only.</summary>
public sealed record ModelOperationInputSelection(string Name, IReadOnlyList<ModelOperationInput> Inputs);
public sealed record ModelOperationDraft(string ItemId, string Name, IReadOnlyList<ModelOperationInput> Inputs);
public sealed record ModelOperationItemRow(string ItemId, string Name, string State, string Stage, string Error);

/// <summary>Presentation state for one captured service connection. No model policy, credentials or filesystem access.</summary>
public sealed class ModelOperationsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ModelOperationClient client;
    private ModelOperationCatalogEntry? selectedModel;
    private string operation = ModelOperationContract.ImageTo3D;
    private ModelOperationOptions options = new();
    private string? error;
    private bool busy;
    private bool catalogReady;
    private readonly Dispatcher dispatcher;
    private readonly EventHandler clientChanged;
    private bool disposed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelOperationsViewModel(ModelOperationClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        dispatcher = Dispatcher.CurrentDispatcher;
        clientChanged = (_, _) =>
        {
            void Present() { if (!disposed) { PresentBatch(); Changed(); } }
            if (dispatcher.CheckAccess()) Present();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Present);
        };
        client.Changed += clientChanged;
    }
    public ObservableCollection<ModelOperationCatalogEntry> Models { get; } = [];
    public ObservableCollection<ModelOperationBatchStatus> History { get; } = [];
    public ObservableCollection<ModelOperationDraft> Drafts { get; } = [];
    public ObservableCollection<ModelOperationItemRow> Items { get; } = [];
    public ObservableCollection<ModelOperationArtifactReference> Outputs { get; } = [];
    public ModelOperationClient Client => client;
    public ModelOperationClientSnapshot Snapshot => client.Snapshot;
    public ModelOperationDescription? Description { get; private set; }
    public ModelOperationCatalogEntry? SelectedModel => selectedModel;
    public bool Busy => busy;
    public string? Error => error ?? Snapshot.Error;
    public string Operation
    {
        get => operation;
        set
        {
            if (!ModelOperationContract.Operations.Contains(value)) throw new ArgumentException("Unsupported model operation.");
            if (operation == value) return;
            operation = value; Drafts.Clear();
            options = value == ModelOperationContract.MeshRetexture
                ? options with { PipelineType = null, NumSamples = null, MaxNumTokens = null, SparseStructure = null, Shape = null, Export = null }
                : options with { Resolution = null };
            Changed();
        }
    }
    public ModelOperationOptions Options
    {
        get => options;
        set { ModelOperationContract.Validate(value); options = value; Changed(); }
    }
    public bool CanSubmit => catalogReady && Description?.Model.ModelId == selectedModel?.ModelId && !busy && !Snapshot.Busy && !Snapshot.Uncertain && Drafts.Count is > 0 and <= ModelOperationContract.MaximumBatchItems && Eligible(selectedModel, Operation);
    public bool CanCancel => !Snapshot.Busy && Snapshot.Batch is { CancelRequested: false } batch &&
        batch.Items.Any(x => x.State is ModelOperationState.Queued or ModelOperationState.Loading or ModelOperationState.Running or ModelOperationState.Exporting or ModelOperationState.Unknown);
    public bool CanRetryFailed => catalogReady && !busy && !Snapshot.Busy && !Snapshot.Uncertain && Snapshot.Batch is { } batch &&
        batch.Items.Any(x => x.State == ModelOperationState.Failed) && Eligible(Models.FirstOrDefault(x => x.ModelId == batch.ModelId), batch.Operation);
    public string Readiness => !client.ConnectionCurrent ? "Owner connection changed. Reconnect through Cerebral; this panel will not reroute pending work." :
        selectedModel is null ? "Select an installed model to inspect its capabilities and qualification." :
        !Eligible(selectedModel, Operation) ? selectedModel.UnavailableReason ?? selectedModel.Profiles.FirstOrDefault(x => !x.Qualified)?.UnavailableReason ?? "This model/profile is not qualified for the selected operation." :
        selectedModel.Name + " · Worker " + selectedModel.WorkerInstallationId + " · " + selectedModel.State + ". Cerebral rechecks resources and holds at admission.";
    public string BatchSummary => Snapshot.Batch is { } batch ? "Batch " + batch.Id + " · " + (batch.Operation == ModelOperationContract.ImageTo3D ? "Image to 3D" : "Retexture mesh") + " · " + batch.State +
        (batch.CancelRequested ? " · cancellation requested; physical stop is not implied" : "") +
        (batch.WaitingReason is null ? "" : " · " + batch.WaitingReason) :
        Snapshot.Uncertain && Snapshot.Request is { } request ? "Submission " + request.RequestId + " has an uncertain acknowledgement. Refresh reads the original request; it does not resubmit." : "No batch selected.";

    public void SelectModel(string? modelId)
    {
        selectedModel = modelId is null ? null : Models.SingleOrDefault(x => x.ModelId == modelId);
        Description = null; Changed();
    }
    public async Task RefreshCatalogAsync(CancellationToken token = default)
    {
        busy = true; catalogReady = false; error = null; Changed();
        try
        {
            var catalog = await client.ListAsync(token);
            var history = await client.HistoryAsync(100, token);
            var selected = selectedModel?.ModelId;
            Models.Clear(); foreach (var entry in catalog.Models) Models.Add(entry);
            selectedModel = Models.FirstOrDefault(x => x.ModelId == selected) ?? Models.FirstOrDefault();
            History.Clear(); foreach (var batch in history) History.Add(batch);
            if (selectedModel is not null) await DescribeSelectedAsync(token);
            catalogReady = true;
        }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { busy = false; Changed(); }
    }
    public async Task DescribeSelectedAsync(CancellationToken token = default)
    {
        var selected = selectedModel?.ModelId;
        if (selected is null) return;
        var description = await client.DescribeAsync(selected, token);
        if (selectedModel?.ModelId != selected) return;
        Description = description; selectedModel = description.Model; Changed();
    }
    public void AddInputs(IEnumerable<ModelOperationInputSelection> values)
    {
        var incoming = values.ToArray();
        if (Drafts.Count + incoming.Length > ModelOperationContract.MaximumBatchItems) throw new ArgumentException("A batch supports at most 64 inputs.");
        var prepared = new List<ModelOperationDraft>();
        foreach (var input in incoming)
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 255 || input.Name.Any(x => char.IsControl(x) || x is '/' or '\\' or ':'))
                throw new ArgumentException("Input selections require a bounded display name, not a server path.");
            var item = new ModelOperationSubmissionItem(Guid.NewGuid().ToString("N"), input.Inputs, Options);
            var frozen = ModelOperationContract.Freeze(new ModelOperationSubmissionRequest(1, "selection-validation", "model", Operation, [item]));
            prepared.Add(new(frozen.Items[0].ItemId, input.Name, Array.AsReadOnly(frozen.Items[0].Inputs.ToArray())));
        }
        foreach (var item in prepared) Drafts.Add(item);
        Changed();
    }
    public void RemoveInput(string itemId)
    { var item = Drafts.SingleOrDefault(x => x.ItemId == itemId); if (item is not null) Drafts.Remove(item); Changed(); }
    public ModelOperationSubmissionRequest BuildSubmission(string requestId)
    {
        if (!CanSubmit) throw new InvalidOperationException("Select a qualified model and 1–64 admitted inputs on the current connection.");
        ModelOperationContract.Validate(Options);
        return ModelOperationContract.Freeze(new ModelOperationSubmissionRequest(ModelOperationContract.Version, requestId,
            selectedModel!.ModelId, Operation, Drafts.Select(x => new ModelOperationSubmissionItem(x.ItemId, x.Inputs, Options)).ToArray()));
    }
    public async Task SubmitAsync(CancellationToken token = default)
    {
        var request = BuildSubmission(Guid.NewGuid().ToString("N"));
        error = null; Changed();
        try { await client.SubmitAsync(request, token); }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { Changed(); }
    }
    public async Task RefreshStatusAsync(CancellationToken token = default)
    {
        if (Snapshot.Request is null && Snapshot.Batch is null) return;
        try { await client.RefreshAsync(token); error = null; }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { Changed(); }
    }
    public async Task OpenAsync(string batchId, CancellationToken token = default)
    {
        try { await client.OpenAsync(batchId, token); error = null; }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { Changed(); }
    }
    public async Task CancelAsync(CancellationToken token = default)
    {
        try { await client.CancelAsync(token); error = null; }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { Changed(); }
    }
    public async Task RetryFailedAsync(CancellationToken token = default)
    {
        if (!CanRetryFailed) throw new InvalidOperationException("Only explicitly failed items on a currently qualified model can be retried.");
        busy = true; error = null; Changed();
        try
        {
            var request = await client.CreateFailedRetryAsync(Guid.NewGuid().ToString("N"), token);
            if (!Eligible(Models.FirstOrDefault(x => x.ModelId == request.ModelId), request.Operation)) throw new InvalidOperationException("Original model readiness changed.");
            await client.SubmitAsync(request, token);
        }
        catch (Exception failure) { error = Safe(failure); throw; }
        finally { busy = false; Changed(); }
    }
    private bool Eligible(ModelOperationCatalogEntry? model, string capability) => client.ConnectionCurrent && model is not null &&
        model.State is "ready" or "loaded" or "busy" or "held" or "queued" && model.Runtime?.Capabilities.Any(x => x.Id == capability) == true &&
        model.Profiles.Any(x => x.Qualified && x.Profile.Operations.Contains(capability));
    private void PresentBatch()
    {
        Items.Clear(); Outputs.Clear();
        var index = 0;
        foreach (var item in Snapshot.Batch?.Items ?? [])
        {
            index++;
            Items.Add(new(item.ItemId, Drafts.FirstOrDefault(x => x.ItemId == item.ItemId)?.Name ?? "Item " + index,
                item.State.ToString(), item.Stage ?? "", OperatorMcpRegistry.RedactDiagnostic(item.Error?.Message ?? item.Result?.Error?.Message ?? "")));
            foreach (var output in item.Result?.Outputs ?? []) Outputs.Add(output.Artifact);
        }
    }
    private static string Safe(Exception failure) => OperatorMcpRegistry.RedactDiagnostic(failure.Message);
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; client.Changed -= clientChanged; }
}
