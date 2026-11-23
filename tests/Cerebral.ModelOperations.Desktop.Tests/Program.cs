using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.ModelOperations;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.ApplyTheme(application);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = RunAsync();
        _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
        Dispatcher.Run();
        task.GetAwaiter().GetResult();
        application.Shutdown(); return 0;
    }
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAILED: " + name); passed++; }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or ArgumentException) { Check(true, name); return; }
        throw new InvalidOperationException("FAILED: " + name);
    }
    private static async Task RunAsync()
    {
        var owner = new FixtureOwner();
        var client = owner.Client();
        var request = owner.Request("request-one", 3);
        await client.SubmitAsync(request);
        Check(client.Snapshot.Batch?.RequestId == request.RequestId && !client.Snapshot.Uncertain, "typed asynchronous acknowledgement matches immutable request");
        Check(owner.Calls.All(x => x.ProfileId is null), "owner authority is transport-owned, never a target profile field");
        var submitted = owner.SubmitCount;
        await Reject(() => client.SubmitAsync(request with { RequestId = "invalid-texture", Items = [request.Items[0] with { Options = new() { TextureResolution = 8192 } }] }), "8K rejects before service dispatch");
        Check(owner.SubmitCount == submitted, "invalid settings do not call the service");
        var batch = client.Snapshot.Batch!;
        owner.MakeMixed(batch.Id);
        await client.RefreshAsync();
        var retry = await client.CreateFailedRetryAsync("failed-retry");
        Check(retry.Items.Count == 1 && retry.Items[0].Inputs[0].Artifact == request.Items[0].Inputs[0].Artifact, "failed-item retry preserves exact original input");
        Check(retry.RequestId != request.RequestId && retry.Items[0].ItemId != request.Items[0].ItemId, "retry has fresh request/item identities");
        Check(!retry.Items.Any(x => x.ItemId == request.Items[1].ItemId || x.ItemId == request.Items[2].ItemId), "unknown/successful items are not retried");
        await Reject(() => client.CreateFailedRetryAsync(request.RequestId), "retry cannot reuse prior request identity");
        var reopened = owner.Client();
        await reopened.OpenAsync(batch.Id);
        var historical = await reopened.CreateFailedRetryAsync("historical-retry");
        Check(historical.Items.Count == 1 && owner.Calls.Any(x => x.Operation == "models.operations.request"), "historical retry reads the frozen original request");
        owner.Current = false;
        var count = owner.Calls.Count;
        await Reject(() => client.ListAsync(), "changed connection refuses inspection");
        Check(owner.Calls.Count == count, "changed connection does not reroute to a replacement service");
        owner.Current = true;

        var uncertainOwner = new FixtureOwner { LoseSubmitReply = true };
        var uncertain = uncertainOwner.Client();
        await Reject(() => uncertain.SubmitAsync(uncertainOwner.Request("lost-ack", 1)), "lost submit acknowledgement remains uncertain");
        Check(uncertain.Snapshot.Uncertain && uncertain.Snapshot.Request?.RequestId == "lost-ack", "uncertain client retains exact original request");
        await Reject(() => uncertain.SubmitAsync(uncertainOwner.Request("must-not-submit", 1)), "uncertain request prevents another submission");
        Check(uncertain.Snapshot.Uncertain && uncertainOwner.SubmitCount == 1, "refusal preserves uncertainty and does not submit again");
        await uncertain.RefreshAsync();
        Check(!uncertain.Snapshot.Uncertain && uncertain.Snapshot.Batch is not null && uncertainOwner.SubmitCount == 1, "request-index reconciliation reads without resubmission");
        var switched = new FixtureOwner { SwitchAfterSubmit = true };
        var switchedClient = switched.Client();
        await Reject(() => switchedClient.SubmitAsync(switched.Request("changed-after-effect", 1)), "post-dispatch connection change is uncertain");
        Check(switchedClient.Snapshot.Uncertain && switched.SubmitCount == 1, "old effect identity remains retained after connection replacement");

        var orderedOwner = new FixtureOwner(); var ordered = orderedOwner.Client();
        await ordered.SubmitAsync(orderedOwner.Request("older", 1));
        orderedOwner.DelayedGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = orderedOwner.Batches[ordered.Snapshot.Batch!.Id];
        var staleRead = ordered.RefreshAsync();
        await ordered.SubmitAsync(orderedOwner.Request("newer", 1));
        var newerId = ordered.Snapshot.Batch!.Id;
        orderedOwner.DelayedGet.SetResult(older); orderedOwner.DelayedGet = null;
        await Reject(() => staleRead, "old status response cannot replace a newer selection");
        Check(ordered.Snapshot.Batch!.Id == newerId && ordered.Snapshot.Request!.RequestId == "newer", "new request and status stay paired after response race");

        var downloadOwner = new FixtureOwner();
        var downloadClient = downloadOwner.Client();
        using var bytes = new MemoryStream();
        await downloadClient.DownloadAsync(downloadOwner.DownloadReference, bytes);
        Check(bytes.CanWrite && bytes.ToArray().SequenceEqual(downloadOwner.DownloadBytes), "download preserves caller stream ownership and exact bytes");
        Check(downloadOwner.Calls.Count(x => x.Operation == "resources.model-artifacts.read") == 3, "artifact bytes use bounded range reads");
        Check(downloadOwner.Calls.All(x => !x.Arguments.GetRawText().Contains("serverPath", StringComparison.Ordinal)), "artifact requests contain no Worker storage path");
        downloadOwner.CorruptDownload = true;
        using var corrupt = new MemoryStream();
        await Reject(() => downloadClient.DownloadAsync(downloadOwner.DownloadReference, corrupt), "corrupt artifact hash refuses publication");
        Check(corrupt.CanWrite, "failed download leaves caller-owned staging available for diagnosis");

        var uiOwner = new FixtureOwner(); var uiClient = uiOwner.Client();
        using var model = new ModelOperationsViewModel(uiClient);
        await model.RefreshCatalogAsync();
        Check(model.SelectedModel?.ModelId == "fixture-model" && model.Readiness.Contains("Cerebral rechecks"), "qualified catalog and Worker readiness are displayed without a capacity grant");
        var selections = Enumerable.Range(0, 64).Select(i => new ModelOperationInputSelection("image-" + i + ".png", [new("image", uiOwner.Image)])).ToArray();
        model.AddInputs(selections);
        Check(model.Drafts.Count == 64 && model.CanSubmit, "UI accepts exactly 64 admitted input selections");
        await Reject(() => { model.AddInputs([selections[0]]); return Task.CompletedTask; }, "65th item is refused without truncation");
        await Reject(() => { model.Options = new() { TextureResolution = 8192 }; return Task.CompletedTask; }, "view model hard-refuses 8K");
        Check(model.Drafts.Count == 64 && uiOwner.SubmitCount == 0, "UI validation has no execution effects");
        model.Operation = ModelOperationContract.MeshRetexture;
        Check(model.Drafts.Count == 0, "changing operation clears incompatible draft selections only");
        model.AddInputs([new("mesh-and-appearance", [new("mesh", uiOwner.Mesh), new("appearance", uiOwner.Image)])]);
        Check(model.BuildSubmission("retexture-preview").Items[0].Inputs.Count == 2, "retexture has typed mesh plus appearance references");
        model.Operation = ModelOperationContract.ImageTo3D;
        model.AddInputs([new("fixture.png", [new("image", uiOwner.Image)])]);
        using var panel = new ModelOperationsPanel(model,
            (_, _) => Task.FromResult<IReadOnlyList<ModelOperationInputSelection>>([]),
            (_, _) => Task.CompletedTask);
        panel.Measure(new Size(1280, 960)); panel.Arrange(new Rect(0, 0, 1280, 960)); panel.UpdateLayout(); panel.RefreshPresentation();
        var textureBox = Descendants(panel).OfType<ComboBox>().Single(x => AutomationProperties.GetAutomationId(x) == "ModelOperationTextureResolution");
        Check(textureBox.Items.Count == 5 && !textureBox.IsEditable && textureBox.Items.Cast<object>().All(x => !x.ToString()!.Contains("8192")), "native texture selector exposes default through 4K only");
        textureBox.SelectedIndex = 4;
        Check(panel.ReadOptions().TextureResolution == 4096, "native panel maps 4K to the validated 4096 option");
        Check(Descendants(panel).OfType<DataGrid>().Count() == 2, "native batch input and progress grids are mounted");
        var submitButton = Descendants(panel).OfType<Button>().Single(x => Equals(x.Content, "Submit batch"));
        var seedBox = Descendants(panel).OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "ModelOperationSeed");
        seedBox.Text = "not-an-integer";
        submitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(uiOwner.SubmitCount == 0 && Descendants(panel).OfType<TextBlock>().Any(x => x.Text.Contains("valid integer")), "routed UI validation displays its error and dispatches no batch");
        seedBox.Text = "";
        Check(submitButton.IsEnabled, "qualified native submit action is enabled");
        submitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.Background);
        var uiSubmission = uiOwner.Calls.Last(x => x.Operation == "models.operations.submit");
        Check(uiSubmission.Arguments.GetProperty("items")[0].GetProperty("options").GetProperty("textureResolution").GetInt32() == 4096,
            "routed native submit uses the selected validated 4K setting");
        Check(model.Items.Count == 1 && model.BatchSummary.Contains("queued"), "accepted work is shown as queued rather than completed");
        await model.CancelAsync();
        Check(model.BatchSummary.Contains("physical stop is not implied"), "cancellation presentation does not claim physical release");
        uiOwner.Unavailable = true;
        await model.RefreshCatalogAsync();
        Check(!model.CanSubmit && model.Readiness.Contains("fixture runtime unavailable"), "unsupported runtime blocks submission with its owning reason");

        panel.RefreshPresentation(); panel.Measure(new Size(1280, 960)); panel.Arrange(new Rect(0, 0, 1280, 960)); panel.UpdateLayout();
        var image = new RenderTargetBitmap(1280, 960, 96, 96, PixelFormats.Pbgra32); image.Render(panel);
        var preview = Path.GetFullPath(Path.Combine("build", "ModelOperationsDesktop", Guid.NewGuid().ToString("N"), "panel-fixture.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
        using (var output = new FileStream(preview, FileMode.CreateNew, FileAccess.Write))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output); }
        Check(new FileInfo(preview).Length > 2048, "software-rendered native panel fixture is nonempty");
        Check(Application.Current.Windows.Count == 0, "fixture created no real application Window");
        Console.WriteLine(JsonSerializer.Serialize(new { passed, preview,
            evidence = "deterministic owner-command/transfer checks and software-rendered WPF control fixture; no installed UI, Worker connection, model, GPU or network execution" }));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class FixtureOwner
    {
        private const string Digest = "1111111111111111111111111111111111111111111111111111111111111111";
        public bool Current = true, LoseSubmitReply, SwitchAfterSubmit, CorruptDownload, Unavailable;
        public int SubmitCount;
        public TaskCompletionSource<ModelOperationBatchStatus>? DelayedGet;
        public List<CommandRequest> Calls { get; } = [];
        public Dictionary<string, ModelOperationBatchStatus> Batches { get; } = [];
        private readonly Dictionary<string, ModelOperationSubmissionRequest> requests = [];
        public ModelOperationArtifactReference Image => new("fixture-worker", "input-job", "input-effect", "image-artifact", 1, "image/png", "cerebral.png/1", "conditioning", 3, Digest);
        public ModelOperationArtifactReference Mesh => Image with { ArtifactId = "mesh-artifact", MediaType = "model/gltf-binary", MediaProfile = "cerebral.glb/1", Role = "mesh" };
        public byte[] DownloadBytes { get; } = Enumerable.Range(0, 600_000).Select(x => (byte)(x % 251)).ToArray();
        public ModelOperationArtifactReference DownloadReference => Image with { ArtifactId = "output-artifact", MediaType = "application/json", MediaProfile = "cerebral.fixture/1", Role = "report", Bytes = DownloadBytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(DownloadBytes)) };
        public ModelOperationClient Client() => new(Call, new("fixture-owner", 1), () => Current);
        public ModelOperationSubmissionRequest Request(string id, int count) => new(1, id, "fixture-model", ModelOperationContract.ImageTo3D,
            Enumerable.Range(0, count).Select(x => new ModelOperationSubmissionItem("item-" + x, [new("image", Image)], new() { TextureResolution = 4096 })).ToArray());
        private ModelOperationRuntimeBinding Binding => new("fixture-worker", "fixture-runtime", "1.0", Digest, "fixture-model", "fixture-package", "1.0", Digest, "fixture-profile", Digest, "fixture-instance");
        public void MakeMixed(string id)
        {
            var batch = Batches[id];
            var items = batch.Items.ToArray();
            items[0] = items[0] with { State = ModelOperationState.Failed, Error = new("fixture_failure", "Expected fixture failure") };
            items[1] = items[1] with { State = ModelOperationState.Unknown, Error = new("fixture_unknown", "Unknown fixture effect") };
            var success = items[2];
            var output = DownloadReference with { JobId = success.JobId, EffectId = success.EffectId };
            items[2] = success with { State = ModelOperationState.Succeeded, Result = new(1, batch.RequestId, success.JobId, success.EffectId, Binding,
                ModelOperationState.Succeeded, false, [new("report", output)]) };
            Batches[id] = batch with { State = "unknown", Items = items, UpdatedAt = batch.CreatedAt.AddSeconds(1) };
        }
        private ModelOperationCatalogEntry CatalogModel()
        {
            var operations = ModelOperationContract.Operations;
            var profile = new ModelOperationProfile("fixture-profile", "native", "win-x64", "cpu", Digest, operations);
            var runtime = new ModelOperationRuntimeDescriptor("fixture-runtime", "1.0", operations.Select(x => new ModelOperationCapability(x, 1,
                ["image/png", "model/gltf-binary"], ["cerebral.fixture/1"])).ToArray(), [profile]);
            return new("fixture-model", "Fixture model (no runtime)", "fixture-worker", Unavailable ? "unavailable" : "ready",
                Unavailable ? "fixture runtime unavailable" : null, runtime, [new(profile, !Unavailable, Unavailable ? null : "fixture-qualification", Unavailable ? "fixture runtime unavailable" : null)]);
        }
        private async Task<CommandResult> Call(CommandRequest request, CancellationToken token)
        {
            Calls.Add(request);
            switch (request.Operation)
            {
                case "models.operations.list": return CommandResult.Ok(new ModelOperationCatalog(1, [CatalogModel()]));
                case "models.operations.describe": return CommandResult.Ok(new ModelOperationDescription(1, CatalogModel(), CerebralJson.Element(new { type = "object" })));
                case "models.operations.history": return CommandResult.Ok(Batches.Values.ToArray());
                case "models.operations.submit":
                {
                    SubmitCount++;
                    var submission = ModelOperationContract.Read<ModelOperationSubmissionRequest>(System.Text.Encoding.UTF8.GetBytes(request.Arguments.GetRawText()));
                    var id = "batch-" + SubmitCount;
                    requests[id] = submission;
                    var now = DateTimeOffset.UtcNow;
                    var batch = new ModelOperationBatchStatus(id, submission.RequestId, Convert.ToHexStringLower(SHA256.HashData(ModelOperationContract.Write(submission))),
                        submission.ModelId, submission.Operation, null, "queued", false, now, now,
                        submission.Items.Select(x => new ModelOperationItemStatus(x.ItemId, "job-" + x.ItemId, "effect-" + x.ItemId, ModelOperationState.Queued)).ToArray());
                    Batches[id] = batch;
                    if (SwitchAfterSubmit) Current = false;
                    if (LoseSubmitReply) { LoseSubmitReply = false; throw new IOException("Fixture lost acknowledgement"); }
                    return CommandResult.Ok(batch);
                }
                case "models.operations.get":
                {
                    if (DelayedGet is { } delayed) return CommandResult.Ok(await delayed.Task.WaitAsync(token));
                    if (request.Arguments.TryGetProperty("id", out var id)) return CommandResult.Ok(Batches[id.GetString()!]);
                    return CommandResult.Ok(Batches.Values.SingleOrDefault(x => x.RequestId == request.Arguments.GetProperty("requestId").GetString()));
                }
                case "models.operations.request": return CommandResult.Ok(requests[request.Arguments.GetProperty("id").GetString()!]);
                case "models.operations.cancel":
                {
                    var id = request.Arguments.GetProperty("id").GetString()!;
                    return CommandResult.Ok(Batches[id] = Batches[id] with { CancelRequested = true });
                }
                case "resources.model-artifacts.describe": return CommandResult.Ok(DownloadReference);
                case "resources.model-artifacts.read":
                {
                    var offset = request.Arguments.GetProperty("offset").GetInt32(); var count = request.Arguments.GetProperty("count").GetInt32();
                    var bytes = DownloadBytes.AsSpan(offset, count).ToArray();
                    if (CorruptDownload && offset == 0) bytes[0] ^= 1;
                    return CommandResult.Ok(bytes);
                }
                default: throw new InvalidOperationException("Unexpected fixture command: " + request.Operation);
            }
        }
    }
}
