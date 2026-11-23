using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Desktop.ModelOperations;

/// <summary>
/// Reusable normal Cerebral panel. The host injects its admitted import/download workflows;
/// this view never reads a server path, stores credentials, starts a model or changes idle policy.
/// </summary>
public sealed class ModelOperationsPanel : ScrollViewer, IDisposable
{
    private sealed record Choice<T>(string Label, T Value) { public override string ToString() => Label; }
    private readonly ModelOperationsViewModel viewModel;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ModelOperationInputSelection>>> selectInputs;
    private readonly Func<ModelOperationArtifactReference, CancellationToken, Task> download;
    private readonly StackPanel root = new() { Margin = new Thickness(16) };
    private readonly ComboBox models = new() { MinWidth = 260, DisplayMemberPath = "Name", SelectedValuePath = "ModelId" };
    private readonly ComboBox operations = new() { MinWidth = 180 };
    private readonly ComboBox textures = new() { MinWidth = 155 };
    private readonly ComboBox pipeline = new() { MinWidth = 160 };
    private readonly ComboBox geometry = new() { MinWidth = 150 };
    private readonly TextBox seed = Ui.Input();
    private readonly TextBox samples = Ui.Input();
    private readonly TextBox tokens = Ui.Input();
    private readonly TextBox decimation = Ui.Input();
    private readonly TextBox projection = Ui.Input();
    private readonly CheckBox preprocess = new() { Content = "Foreground preprocessing", IsThreeState = true, IsChecked = null };
    private readonly CheckBox remesh = new() { Content = "Remesh for export", IsThreeState = true, IsChecked = null };
    private readonly SamplerEditor sparse = new("Sparse structure");
    private readonly SamplerEditor shape = new("Shape");
    private readonly SamplerEditor texture = new("Texture");
    private readonly TextBlock readiness = Ui.Text("", 13, Ui.Muted);
    private readonly TextBlock batch = Ui.Text("", 13);
    private readonly TextBlock error = Ui.Text("", 13, Ui.ErrorText);
    private readonly TextBlock operationMeaning = Ui.Text("", 12, Ui.Muted);
    private readonly DataGrid drafts = Grid("ModelOperationDrafts");
    private readonly DataGrid items = Grid("ModelOperationItems");
    private readonly ComboBox history = new() { MinWidth = 300, DisplayMemberPath = "Id" };
    private readonly ListBox outputs = new() { MinHeight = 60, MaxHeight = 180 };
    private readonly Button submit;
    private readonly Button cancel;
    private readonly Button retry;
    private readonly Button add;
    private readonly Button remove;
    private readonly Button save;
    private readonly DispatcherTimer poll;
    private bool polling;
    private bool presenting;
    private bool subscribed;
    private bool disposed;
    private string? localError;

    public ModelOperationsPanel(ModelOperationsViewModel viewModel,
        Func<string, CancellationToken, Task<IReadOnlyList<ModelOperationInputSelection>>> selectInputs,
        Func<ModelOperationArtifactReference, CancellationToken, Task> download)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.selectInputs = selectInputs ?? throw new ArgumentNullException(nameof(selectInputs));
        this.download = download ?? throw new ArgumentNullException(nameof(download));
        AutomationProperties.SetAutomationId(this, "CerebralModelOperations");
        Background = Ui.Paper; Foreground = Ui.Ink;
        Content = root; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        root.Children.Add(Ui.Text("Model operations", 22));
        root.Children.Add(Ui.Text("Run admitted model batches on qualified Cerebral workers. Texture output is limited to 4K; geometry resolution is a separate setting.", 13, Ui.Muted));
        var selectors = new WrapPanel();
        selectors.Children.Add(Field("Model", models)); selectors.Children.Add(Field("Operation", operations));
        selectors.Children.Add(Ui.Button("Refresh models and history", () => _ = RunAsync(() => viewModel.RefreshCatalogAsync())));
        root.Children.Add(selectors); root.Children.Add(readiness);
        models.ItemsSource = viewModel.Models;
        operations.ItemsSource = new[] { new Choice<string>("Image to 3D", ModelOperationContract.ImageTo3D), new("Retexture mesh", ModelOperationContract.MeshRetexture) };
        operations.SelectedIndex = 0;
        models.SelectionChanged += (_, _) =>
        {
            if (presenting) return;
            viewModel.SelectModel(models.SelectedValue as string);
            _ = RunAsync(() => viewModel.DescribeSelectedAsync());
        };
        operations.SelectionChanged += (_, _) =>
        {
            if (presenting || operations.SelectedItem is not Choice<string> selected) return;
            viewModel.Operation = selected.Value; RefreshPresentation();
        };
        textures.ItemsSource = new[] { new Choice<int?>("Provider default", null), new("1K (1024)", 1024), new("2K (2048)", 2048), new("3K (3072)", 3072), new("4K (4096)", 4096) };
        pipeline.ItemsSource = new[] { new Choice<ModelOperationPipelineType?>("Provider default", null), new("512", ModelOperationPipelineType.Resolution512),
            new("1024", ModelOperationPipelineType.Resolution1024), new("1024 cascade", ModelOperationPipelineType.Cascade1024), new("1536 cascade", ModelOperationPipelineType.Cascade1536) };
        geometry.ItemsSource = new[] { new Choice<int?>("Provider default", null), new("512", 512), new("1024", 1024), new("1536", 1536) };
        textures.SelectedIndex = pipeline.SelectedIndex = geometry.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(textures, "ModelOperationTextureResolution");
        AutomationProperties.SetAutomationId(pipeline, "ModelOperationPipeline");
        AutomationProperties.SetAutomationId(geometry, "ModelOperationRetextureResolution");
        AutomationProperties.SetAutomationId(seed, "ModelOperationSeed");
        AutomationProperties.SetAutomationId(samples, "ModelOperationSamples");
        AutomationProperties.SetAutomationId(tokens, "ModelOperationTokenCeiling");
        AutomationProperties.SetAutomationId(decimation, "ModelOperationPolygonTarget");
        AutomationProperties.SetAutomationId(projection, "ModelOperationRemeshProjection");
        var settings = new WrapPanel();
        settings.Children.Add(Field("Texture size", textures)); settings.Children.Add(Field("Image-to-3D pipeline", pipeline)); settings.Children.Add(Field("Retexture geometry", geometry));
        settings.Children.Add(Field("Seed (blank = default)", seed)); settings.Children.Add(Field("Samples per image (1–4)", samples)); settings.Children.Add(Field("Token ceiling (≤49152)", tokens));
        root.Children.Add(settings); root.Children.Add(preprocess);
        var advanced = new StackPanel(); advanced.Children.Add(sparse.View); advanced.Children.Add(shape.View); advanced.Children.Add(texture.View);
        var exports = new WrapPanel(); exports.Children.Add(Field("Export target (100000–1000000)", decimation)); exports.Children.Add(Field("Remesh projection (0–1)", projection)); exports.Children.Add(remesh); advanced.Children.Add(exports);
        root.Children.Add(new Expander { Header = "Advanced sampling and export", Content = advanced, Margin = new Thickness(0, 8, 0, 8) });
        root.Children.Add(Ui.Text("Blank settings use the selected provider profile's declared defaults. Cerebral validates the complete settings combination before running it.", 12, Ui.Muted));
        root.Children.Add(operationMeaning);
        preprocess.ToolTip = remesh.ToolTip = "Checked enables this setting, unchecked disables it, and the intermediate state uses the provider default.";
        root.Children.Add(Ui.Text("Batch inputs · maximum 64 items", 16));
        var inputButtons = new WrapPanel();
        add = Ui.Button("Add admitted inputs", () => _ = RunAsync(async () => viewModel.AddInputs(await this.selectInputs(viewModel.Operation, CancellationToken.None))));
        remove = Ui.Button("Remove selected input", () => { if (drafts.SelectedItem is ModelOperationDraft selected) viewModel.RemoveInput(selected.ItemId); });
        submit = Ui.Button("Submit batch", () => _ = RunAsync(async () => { viewModel.Options = ReadOptions(); await viewModel.SubmitAsync(); }), true);
        inputButtons.Children.Add(add); inputButtons.Children.Add(remove); inputButtons.Children.Add(submit); root.Children.Add(inputButtons);
        drafts.ItemsSource = viewModel.Drafts;
        drafts.Columns.Add(new DataGridTextColumn { Header = "Input", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 250 });
        drafts.Columns.Add(new DataGridTextColumn { Header = "References", Binding = new Binding("Inputs.Count"), Width = 100 });
        root.Children.Add(drafts);
        root.Children.Add(Ui.Text("Batch progress and results", 16)); root.Children.Add(batch);
        var controls = new WrapPanel();
        controls.Children.Add(Ui.Button("Refresh status", () => _ = RunAsync(() => viewModel.RefreshStatusAsync())));
        cancel = Ui.Button("Request cancellation", () => _ = RunAsync(() => viewModel.CancelAsync()));
        retry = Ui.Button("Retry failed items only", () => _ = RunAsync(() => viewModel.RetryFailedAsync()));
        controls.Children.Add(cancel); controls.Children.Add(retry); root.Children.Add(controls);
        items.ItemsSource = viewModel.Items;
        foreach (var (header, path, width) in new[] { ("Item", "Name", 170d), ("State", "State", 105d), ("Stage", "Stage", 170d), ("Error", "Error", 400d) })
            items.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
        root.Children.Add(items);
        var historyRow = new WrapPanel(); history.ItemsSource = viewModel.History; historyRow.Children.Add(Field("Saved batches", history));
        historyRow.Children.Add(Ui.Button("Open selected batch", () => _ = RunAsync(async () => { if (history.SelectedItem is ModelOperationBatchStatus selected) await viewModel.OpenAsync(selected.Id); })));
        root.Children.Add(historyRow); root.Children.Add(outputs);
        save = Ui.Button("Download selected output", () => _ = RunAsync(async () =>
        {
            if (!viewModel.Client.ConnectionCurrent) throw new ModelOperationConnectionChangedException();
            if (outputs.SelectedItem is ListBoxItem { Tag: ModelOperationArtifactReference selected }) await this.download(selected, CancellationToken.None);
        }));
        root.Children.Add(save); root.Children.Add(error);
        outputs.SelectionChanged += (_, _) => save.IsEnabled = outputs.SelectedItem is not null && viewModel.Client.ConnectionCurrent;
        drafts.SelectionChanged += (_, _) => remove.IsEnabled = drafts.SelectedItem is not null && !viewModel.Busy;
        poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
        {
            if (polling || !IsVisible || !viewModel.Client.ConnectionCurrent || viewModel.Snapshot.Batch is null && !viewModel.Snapshot.Uncertain) return;
            if (viewModel.Snapshot.Batch is { State: "succeeded" or "failed" or "cancelled" }) return;
            polling = true;
            try { await RunAsync(() => viewModel.RefreshStatusAsync()); }
            finally { polling = false; }
        }, Dispatcher);
        poll.Stop(); Subscribe();
        Loaded += (_, _) => { Subscribe(); poll.Start(); };
        Unloaded += (_, _) => { poll.Stop(); Unsubscribe(); };
        RefreshPresentation();
    }

    /// <summary>The owner calls this after mounting the panel on its captured connection. Construction is effect-free.</summary>
    public Task InitializeAsync(CancellationToken token = default) => RunAsync(() => viewModel.RefreshCatalogAsync(token));

    public ModelOperationOptions ReadOptions()
    {
        var image = viewModel.Operation == ModelOperationContract.ImageTo3D;
        var options = new ModelOperationOptions
        {
            TextureResolution = (textures.SelectedItem as Choice<int?>)?.Value,
            PipelineType = image ? (pipeline.SelectedItem as Choice<ModelOperationPipelineType?>)?.Value : null,
            Resolution = image ? null : (geometry.SelectedItem as Choice<int?>)?.Value,
            Seed = Integer(seed), NumSamples = image ? Integer(samples) : null, MaxNumTokens = image ? Integer(tokens) : null,
            PreprocessImage = preprocess.IsChecked,
            SparseStructure = image ? sparse.Read() : null, Shape = image ? shape.Read() : null, Texture = texture.Read(),
            Export = image && (decimation.Text.Length > 0 || projection.Text.Length > 0 || remesh.IsChecked is not null)
                ? new(DecimationTarget: Integer(decimation), Remesh: remesh.IsChecked, RemeshProjection: Number(projection)) : null
        };
        ModelOperationContract.Validate(options); return options;
    }

    public void RefreshPresentation()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshPresentation); return; }
        presenting = true;
        try
        {
            models.SelectedValue = viewModel.SelectedModel?.ModelId;
            operations.SelectedItem = ((IEnumerable<Choice<string>>)operations.ItemsSource).Single(x => x.Value == viewModel.Operation);
            readiness.Text = viewModel.Readiness; batch.Text = viewModel.BatchSummary; error.Text = localError ?? viewModel.Error ?? "";
            submit.IsEnabled = viewModel.CanSubmit; cancel.IsEnabled = viewModel.CanCancel; retry.IsEnabled = viewModel.CanRetryFailed;
            add.IsEnabled = !viewModel.Busy && !viewModel.Snapshot.Busy && viewModel.Client.ConnectionCurrent && viewModel.SelectedModel is not null && viewModel.Drafts.Count < 64;
            remove.IsEnabled = !viewModel.Busy && drafts.SelectedItem is not null;
            var image = viewModel.Operation == ModelOperationContract.ImageTo3D;
            pipeline.IsEnabled = samples.IsEnabled = tokens.IsEnabled = sparse.View.IsEnabled = shape.View.IsEnabled = decimation.IsEnabled = projection.IsEnabled = remesh.IsEnabled = image;
            geometry.IsEnabled = !image;
            operationMeaning.Text = image ? "Geometry resolution and polygon budget are independent of texture size." :
                "Retexturing normalizes the input surface and generates new PBR textures and UVs. Original scale, UVs and vertex arrays are not claimed unchanged; the original input is preserved.";
            var selected = (outputs.SelectedItem as ListBoxItem)?.Tag as ModelOperationArtifactReference;
            outputs.Items.Clear();
            foreach (var item in viewModel.Outputs)
            {
                var row = new ListBoxItem { Tag = item, Content = item.Role + " · " + item.MediaType + " · " + (item.Bytes / 1048576d).ToString("N2", CultureInfo.CurrentCulture) + " MiB" };
                outputs.Items.Add(row); if (item == selected) outputs.SelectedItem = row;
            }
            save.IsEnabled = outputs.SelectedItem is not null && viewModel.Client.ConnectionCurrent;
        }
        finally { presenting = false; }
    }
    private async Task RunAsync(Func<Task> action)
    {
        localError = null;
        try { await action(); }
        catch (Exception failure) { localError = OperatorMcpRegistry.RedactDiagnostic(failure.Message); }
        finally { RefreshPresentation(); }
    }
    private void Subscribe() { if (subscribed || disposed) return; subscribed = true; viewModel.PropertyChanged += Changed; }
    private void Unsubscribe() { if (!subscribed) return; subscribed = false; viewModel.PropertyChanged -= Changed; }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => RefreshPresentation();
    private static FrameworkElement Field(string label, FrameworkElement value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 5, 12, 7) };
        panel.Children.Add(Ui.Text(label, 12, Ui.Muted)); panel.Children.Add(value); return panel;
    }
    private static DataGrid Grid(string id)
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 65, MaxHeight = 240,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(grid, id); return grid;
    }
    private static int? Integer(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new ArgumentException("Enter a valid integer or leave the setting blank for the provider default.");
    private static double? Number(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("Enter a finite number or leave the setting blank for the provider default.");
    public void Dispose() { if (disposed) return; disposed = true; poll.Stop(); Unsubscribe(); }

    private sealed class SamplerEditor
    {
        private readonly TextBox steps = Ui.Input(), guidance = Ui.Input(), rescale = Ui.Input(), intervalStart = Ui.Input(), intervalEnd = Ui.Input(), time = Ui.Input();
        public FrameworkElement View { get; }
        public SamplerEditor(string label)
        {
            var fields = new WrapPanel();
            fields.Children.Add(Field("Steps (1–50)", steps)); fields.Children.Add(Field("Guidance (1–10)", guidance)); fields.Children.Add(Field("Guidance rescale (0–1)", rescale));
            fields.Children.Add(Field("Guidance interval start", intervalStart)); fields.Children.Add(Field("Guidance interval end", intervalEnd)); fields.Children.Add(Field("Time rescale (1–6)", time));
            View = new GroupBox { Header = label, Content = fields, Margin = new Thickness(0, 4, 0, 4) };
        }
        public ModelOperationSamplerOptions? Read()
        {
            if (new[] { steps, guidance, rescale, intervalStart, intervalEnd, time }.All(x => string.IsNullOrWhiteSpace(x.Text))) return null;
            var start = Number(intervalStart); var end = Number(intervalEnd);
            if ((start is null) != (end is null)) throw new ArgumentException("Specify both guidance interval endpoints or leave both blank.");
            return new(Integer(steps), Number(guidance), Number(rescale), start is null ? null : new(start.Value, end!.Value), Number(time));
        }
    }
}
