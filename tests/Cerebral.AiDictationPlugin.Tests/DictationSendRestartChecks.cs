using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

internal static partial class Program
{
    private static async Task<int> RunSendRestartChecksAsync(Dispatcher dispatcher, bool baseline)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-send-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? primary = null;
        try
        {
            Check(typeof(AiDictationPlugin).Assembly.GetName().Version?.ToString() == (baseline ? "1.2.7.0" : "1.2.8.0"),
                "The test process loaded the exact selected baseline/candidate plugin version.");
            if (baseline)
            {
                await using var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, "baseline"));
                await f.StartAsync(); await f.PhraseAsync("first");
                var prepared = await f.Plugin.PrepareSubmissionAsync(new SubmissionView(f.State.Entry), default);
                Check(prepared.State == DesktopOperationState.Completed && f.State.Capture.Capturing,
                    "Installed baseline bypasses preparation for a scoped composer view while still capturing.");
                f.State.Entry.Box.Clear(); EmitPhrase(f.State.Capture, 2001);
                await WaitAsync(() => f.State.Provider.Calls == 2); f.State.Provider.Reply(2, "pending");
                await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Session.HasPendingPhraseAudio && f.Session.Review.HasText && f.State.Entry.Text.Length == 0,
                    "Baseline Send clear invalidates the old run and retains its pending phrase/audio.");
                string? error = null;
                try { f.Session.BeginDestinationRun(f.State.Entry); } catch (InvalidOperationException failure) { error = failure.Message; }
                Check(error == "Stop dictation and review or Discard the pending transcript / run audio before starting again.",
                    "Baseline subsequent Start reproduces the exact retained-run guard.");
                Console.WriteLine(JsonSerializer.Serialize(new { baselineError = error, retainedPhrase = true, retainedAudio = true }));
                return checks;
            }

            foreach (var mode in new[] { "legacy", "scoped", "ack" })
            {
                await using var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, mode));
                await f.StartAsync(); await f.PhraseAsync("First.");
                f.State.Capture.Emit(PhrasePcm(2001)); f.State.Capture.Emit(PhrasePcm(2001)); f.State.Capture.Emit(PhrasePcm(2001));
                f.State.Provider.DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var view = mode == "legacy" ? (IDesktopTextEntry)f.State.Entry : new SubmissionView(f.State.Entry);
                var preparing = mode == "ack" ? f.Lifecycle.PrepareSubmissionAsync("send-1", view) : f.Plugin.PrepareSubmissionAsync(view);
                await WaitAsync(() => f.State.Provider.Calls == 2);
                Check(!preparing.IsCompleted && !f.State.Capture.Capturing, mode + ": Send stops capture and waits for the trailing phrase.");
                f.State.Provider.Reply(2, "Last."); await WaitAsync(() => f.State.Provider.Disposals == 1);
                Check(!preparing.IsCompleted, mode + ": Send waits for helper settlement before acknowledging preparation.");
                f.State.Provider.DisposalGate.SetResult();
                Check((await preparing.WaitAsync(TimeSpan.FromSeconds(5))).State == DesktopOperationState.Completed &&
                    f.State.Entry.Text == "Keep. First. Last. Tail" && !f.Session.IsActive && !f.Session.HasPendingPhraseAudio &&
                    !f.Session.Review.HasText && f.State.Provider.Calls == 2,
                    mode + ": final phrases and spacing appear exactly once; successful handoff leaves no pending run.");
                var submitted = f.State.Host.GetDraft(); f.State.Entry.Box.Clear();
                if (mode == "ack")
                {
                    Check(f.State.Capture.Calls == 1, "Composer emptiness alone never resumes recording.");
                    f.Lifecycle.SubmissionAcknowledged(new("other-request", submitted.Binding, submitted.Revision, submitted.Revision + 1));
                    Check(f.State.Capture.Calls == 1, "Another request's acknowledgment cannot resume recording.");
                    f.Lifecycle.SubmissionAcknowledged(new("send-1", submitted.Binding, submitted.Revision, submitted.Revision + 1));
                    await WaitAsync(() => f.State.Capture.Calls == 2);
                    f.Lifecycle.SubmissionAcknowledged(new("send-1", submitted.Binding, submitted.Revision, submitted.Revision + 1));
                }
                else await f.StartAsync();
                Check(f.State.Capture.Calls == 2 && f.State.Capture.Capturing && f.Session.LastState != DesktopTextEntryActivityState.Error,
                    mode + ": a second recording starts on the empty draft without an extra capture or stale-run error.");
                await f.PhraseAsync("New message.");
                Check(f.State.Entry.Text == "New message." && f.State.Host.Submissions == 0,
                    mode + ": the new run contains only new speech and never submits a message.");
                f.Session.FinishCurrent(); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.State.Provider.Disposals == 2, mode + ": both owned helpers settle once.");
            }

            foreach (var conflict in new[] { "revision", "edit-clear", "binding", "stop", "focus", "new-start", "cancelled-send" })
            {
                await using var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, conflict));
                await f.StartAsync(); await f.PhraseAsync("First.");
                var result = await f.Lifecycle.PrepareSubmissionAsync("guarded", new SubmissionView(f.State.Entry));
                Check(result.State == DesktopOperationState.Completed, conflict + ": preparation settles the first run.");
                var submitted = f.State.Host.GetDraft(); f.State.Entry.Box.Clear();
                var acknowledgment = new DesktopTextEntrySubmissionAcknowledgment("guarded", submitted.Binding, submitted.Revision, submitted.Revision + 1);
                if (conflict == "revision") acknowledgment = acknowledgment with { EmptyDraftRevision = submitted.Revision + 2 };
                if (conflict == "edit-clear") { f.State.Entry.Box.Text = "human edit"; f.State.Entry.Box.Clear(); }
                if (conflict == "binding") f.State.Host.Binding = new("replacement", null);
                if (conflict == "stop") f.Stop();
                if (conflict == "focus") f.State.Host.Focus(new SubmissionView(f.State.Entry));
                if (conflict == "new-start") { await f.StartAsync(); f.Stop(); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
                if (conflict == "cancelled-send")
                {
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    try { await f.Lifecycle.PrepareSubmissionAsync("cancelled", new SubmissionView(f.State.Entry), cancelled.Token); }
                    catch (OperationCanceledException) { }
                    // No accepted receipt is emitted for a cancelled Send.
                }
                var captures = f.State.Capture.Calls;
                if (conflict != "cancelled-send") f.Lifecycle.SubmissionAcknowledged(acknowledgment);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(f.State.Capture.Calls == captures && !f.Session.IsActive && f.State.Host.Submissions == 0,
                    conflict + ": no automatic recording or submission follows a missing/stale acknowledgment.");
            }

            foreach (var failure in new[] { "uncertain-write", "changed-draft", "shutdown", "cancel-wait" })
            {
                await using var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, failure));
                await f.StartAsync(); EmitPhrase(f.State.Capture, 1001); await WaitAsync(() => f.State.Provider.Calls == 1);
                if (failure == "uncertain-write") f.State.UncertainWrite = true;
                if (failure == "changed-draft") f.State.Entry.Box.AppendText(" human edit");
                if (failure == "shutdown") { f.State.Provider.FailDisposal = true; f.State.Provider.ShutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                using var cancellation = new CancellationTokenSource();
                var preparing = f.Lifecycle.PrepareSubmissionAsync("failed", new SubmissionView(f.State.Entry), cancellation.Token);
                if (failure == "cancel-wait")
                {
                    cancellation.Cancel();
                    var cancelled = false; try { await preparing; } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "Cancelled preparation reports cancellation without cancelling or replaying the owned final phrase.");
                }
                f.State.Provider.Reply(1, "pending"); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                if (failure != "cancel-wait")
                    Check((await preparing.WaitAsync(TimeSpan.FromSeconds(5))).State == DesktopOperationState.Rejected,
                        failure + ": Send remains local when insertion/helper settlement is not confirmed.");
                if (failure is "uncertain-write" or "changed-draft")
                    Check(f.Session.HasPendingPhraseAudio && f.Session.Review.HasText &&
                        (failure != "uncertain-write" || f.Session.Review.InsertionUnconfirmed),
                        failure + ": recovery audio and pending/inspection-only text remain truthful.");
                if (failure == "shutdown")
                {
                    var blocked = false;
                    try { await f.Session.ResumeAsync(f.State.Entry, default); } catch (InvalidOperationException error) { blocked = error.Message.Contains("previous speech helper is still settling"); }
                    Check(blocked && f.State.Capture.Calls == 1, "Unconfirmed helper exit keeps restart disabled without another capture.");
                    f.State.Provider.ShutdownSignal!.SetResult();
                }
                Check(f.State.Capture.Calls == 1 && f.State.Host.Submissions == 0, failure + ": failure never rearms or auto-sends.");
            }

            await using (var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, "other-target")))
            {
                await f.StartAsync();
                var main = f.State.Entry;
                f.State.Host.DefaultTextEntry = new SubmissionView(main);
                var result = await f.Lifecycle.PrepareSubmissionAsync("unrelated", f.State.Host.DefaultTextEntry);
                Check(result.State == DesktopOperationState.Completed && f.State.Capture.Capturing && f.State.Capture.Calls == 1,
                    "An unrelated Main Send cannot end or redirect a recording that owns another exact target.");
                f.State.Host.DefaultTextEntry = main;
            }
            await using (var f = await SendFixture.CreateAsync(dispatcher, Path.Combine(root, "default-focus-after-send"), checkFocus: true))
            {
                await f.StartAsync(); await f.PhraseAsync("First.");
                var result = await f.Plugin.PrepareSubmissionAsync(new SubmissionView(f.State.Host.DefaultTextEntry!));
                Check(result.State == DesktopOperationState.Completed, "The owned default prompt finishes before the accepted Send fixture.");
                var submitted = f.State.Host.GetDraft(); f.State.Entry.Box.Clear();
                f.DefaultFocus!.KeyboardFocus = false;
                f.State.Entry.Box.Visibility = System.Windows.Visibility.Collapsed;
                Check(f.State.Host.GetDraft().Revision == submitted.Revision + 1 && f.State.Entry.SelectionStart == 0,
                    "Collapsed-after-Send fixture keeps the exact empty composer, caret and next revision.");
                await f.StartAsync();
                Check(f.State.Capture.Calls == 2 && f.DefaultFocus.FocusRequests == 0 && f.State.Entry.Text.Length == 0,
                    "An explicit second Start on the exact default prompt succeeds without the reported keyboard-focus request or a caret change.");
                await f.PhraseAsync("Next.");
                Check(f.State.Entry.Text == "Next." && f.State.Host.Submissions == 0,
                    "New speech uses the owned Main draft while the collapsed editor never receives focus or auto-send.");
            }
            checks += await RunSubmissionRuntimeChecksAsync(dispatcher, root);
            checks += RunSubmissionPackageChecks(root);
            return checks;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try
            {
                var resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("cerebral-send-fixture-", StringComparison.Ordinal)) throw new IOException("Unsafe send fixture cleanup.");
                if (baseline) Directory.Delete(resolved, true);
                else
                {
                    // Collectible probe assemblies may remain mapped until process
                    // exit. The observing parent owns exact-path deletion after EOF;
                    // never let that expected file lifetime mask a primary assertion.
                    using var marker = new FileStream(Path.Combine(AppContext.BaseDirectory, "send-restart.cleanup.json"), FileMode.CreateNew);
                    JsonSerializer.Serialize(marker, new { schema = "cerebral.send-fixture-cleanup/1", pid = Environment.ProcessId, root = resolved });
                }
            }
            catch (Exception cleanup) when (primary is not null) { throw new AggregateException("Fixture assertion and cleanup both failed.", primary, cleanup); }
        }
    }

    private sealed class SubmissionView(IDesktopTextEntry target) : IDesktopTextEntry
    {
        public string Id => target.Id; public string Purpose => target.Purpose; public string Text => target.Text;
        public int SelectionStart => target.SelectionStart; public int SelectionLength => target.SelectionLength;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A scoped submission view must never become a recording destination.");
    }

    private sealed class DefaultFocusEntry(NativeEntry target) : IDesktopTextEntry, IDesktopTextEntryLifetime, IDesktopTextEntryFocus
    {
        internal bool KeyboardFocus = true;
        internal int FocusRequests;
        public string Id => target.Id; public string Purpose => target.Purpose; public string Text => target.Text;
        public int SelectionStart => target.SelectionStart; public int SelectionLength => target.SelectionLength;
        public CancellationToken AvailabilityToken => target.AvailabilityToken;
        public bool IsKeyboardFocusWithin => KeyboardFocus;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default) => target.InsertAsync(text, cancellationToken);
        public Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default)
        { FocusRequests++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "The text entry could not receive keyboard focus.")); }
    }

    private sealed class SendFixture : IAsyncDisposable
    {
        internal required PhraseFixture State;
        internal required AiDictationPlugin Plugin;
        internal DefaultFocusEntry? DefaultFocus;
        internal IDesktopTextEntrySubmissionLifecyclePlugin Lifecycle => (IDesktopTextEntrySubmissionLifecyclePlugin)(object)Plugin;
        internal AiDictationSession Session => (AiDictationSession)typeof(AiDictationPlugin).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Plugin)!;
        internal static async Task<SendFixture> CreateAsync(Dispatcher dispatcher, string directory, bool checkFocus = false)
        {
            var state = new PhraseFixture(dispatcher, directory);
            await state.Host.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
                new PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(new() { AutoTranscribeOnSilence = true, SilenceWaitSeconds = .5 }), 0, OwnerValidated: true)));
            var focus = checkFocus ? new DefaultFocusEntry(state.Entry) : null;
            if (focus is not null) state.Host.DefaultTextEntry = focus;
            var plugin = new AiDictationPlugin(state.Capture, _ => state.Provider);
            plugin.Initialize(state.Host); await plugin.WaitForStartupAsync(default);
            return new() { State = state, Plugin = plugin, DefaultFocus = focus };
        }
        internal async Task StartAsync()
        {
            var prior = State.Capture.Calls;
            await (Task)typeof(AiDictationPlugin).GetMethod("ToggleStatusMicrophoneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Plugin, null)!;
            await WaitAsync(() => State.Capture.Capturing && State.Capture.Calls > prior);
        }
        internal void Stop() => typeof(AiDictationPlugin).GetMethod("FinishControlMicrophone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Plugin, null);
        internal async Task PhraseAsync(string text)
        {
            await WaitAsync(() => State.Capture.Capturing);
            var call = State.Provider.Calls + 1; EmitPhrase(State.Capture, 1001);
            await WaitAsync(() => State.Provider.Calls == call); State.Provider.Reply(call, text);
            await WaitAsync(() => State.Entry.Text.Contains(text, StringComparison.Ordinal));
        }
        public async ValueTask DisposeAsync()
        {
            State.Provider.DisposalGate?.TrySetResult(); State.Provider.ShutdownSignal?.TrySetResult(); State.Provider.FailDisposal = false;
            Session.Cancel(discardReview: true); await Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Session.ClearRetainedAudio(); Plugin.Dispose(); await State.DisposeAsync();
        }
    }

    private static async Task<int> RunSubmissionRuntimeChecksAsync(Dispatcher dispatcher, string root)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        var source = Path.Combine(root, "probe"); Directory.CreateDirectory(source);
        foreach (var name in new[] { "Cerebral.AiDictationPlugin.Tests.dll", "Cerebral.AiDictationPlugin.Tests.deps.json", "Cerebral.AiDictationPlugin.dll", "Cerebral.Speech.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(source, name));
        File.WriteAllText(Path.Combine(source, "desktop-plugin.json"), JsonSerializer.Serialize(new DesktopPluginManifest
        { Id = "fixture.submission", Name = "Submission fixture", Assembly = "Cerebral.AiDictationPlugin.Tests.dll", EntryType = nameof(SubmissionLifecycleProbe) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var catalog = new DesktopPluginCatalog(Path.Combine(root, "catalog"));
        catalog.Install(Path.Combine(source, "desktop-plugin.json")); catalog.SetEnabled("fixture.submission", true);
        var statuses = new List<string>(); var binding = new DesktopConversationBinding("fixture", null);
        var draft = new DesktopDraftSnapshot(binding, 4, "fixture words");
        using var entry = new NativeEntry(dispatcher);
        var host = new DesktopPluginHostAdapter(dispatcher, () => new(binding, "fixture", null, null, false), () => draft,
            (_, _) => throw new InvalidOperationException("No fixture draft writes."), (_, _) => throw new InvalidOperationException("No fixture sends."),
            (_, _) => throw new InvalidOperationException("No fixture steering."), (_, _) => throw new InvalidOperationException("No fixture stop."),
            statuses.Add, pluginDataRoot: Path.Combine(root, "probe-data"));
        using var runtime = new DesktopPluginRuntime(catalog, host, dispatcher); runtime.LoadEnabled();
        Check(runtime.Status.Single() is { Loaded: true, Error: null }, "The real runtime loads the disposable callback probe.");
        await runtime.TextEntrySubmissionPreparations.Single().PrepareAsync(entry, default);
        Check(statuses.Contains("Submission fixture: probe:legacy"), "The existing preparation property keeps the old overload through the named scoped host.");
        foreach (var mode in new[] { "accepted", "rejected", "replacement", "wrong-request", "intervening-edit" })
        {
            draft = new(binding, 4, "fixture words");
            var result = await runtime.TextEntrySubmissionPreparationsFor(mode).Single().PrepareAsync(entry, default);
            var confirmed = runtime.ConfirmTextEntrySubmissionPreparation(mode, binding, 4);
            Check(confirmed == (mode != "rejected") && (result.State == DesktopOperationState.Rejected) == (mode == "rejected"),
                mode + ": only a complete successful participant set confirms preparation.");
            if (mode == "accepted")
                Check(runtime.TextEntrySubmissionPreparations.Count == 1 && runtime.ConfirmTextEntrySubmissionPreparation(mode, binding, 4),
                    "Reading the legacy preparation list cannot clear an exact lifecycle preparation awaiting acknowledgment.");
            if (mode == "replacement") { catalog.SetEnabled("fixture.submission", false); runtime.LoadEnabled(); catalog.SetEnabled("fixture.submission", true); runtime.LoadEnabled(); }
            draft = new(binding, mode == "intervening-edit" ? 7 : 5, "");
            var before = statuses.Count(value => value.StartsWith("Submission fixture: probe:ack:", StringComparison.Ordinal));
            runtime.NotifyTextEntrySubmissionAcknowledged(new(mode == "wrong-request" ? "different" : mode, binding, 4, 5));
            var after = statuses.Count(value => value.StartsWith("Submission fixture: probe:ack:", StringComparison.Ordinal));
            Check(after - before == (mode == "accepted" ? 1 : 0), mode + ": callbacks reach only the exact successful, still-loaded generation.");
            runtime.NotifyTextEntrySubmissionAcknowledged(new(mode, binding, 4, 5));
            Check(statuses.Count(value => value.StartsWith("Submission fixture: probe:ack:", StringComparison.Ordinal)) == after + (mode == "wrong-request" ? 1 : 0),
                mode + ": a matching callback is consumed once; another RID cannot consume it.");
        }
        return checks;
    }

    private static int RunSubmissionPackageChecks(string root)
    {
        var checks = 0;
        var payload = Path.Combine(root, "package-payload"); Directory.CreateDirectory(payload);
        foreach (var name in new[] { "Cerebral.AiDictationPlugin.dll", "Cerebral.Speech.dll", "desktop-plugin.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(payload, name));
        var native = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(Path.Combine(payload, "desktop-plugin.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var info = new DesktopPluginInfo(native.Id, native.Name, native.Version, native.Description, false, payload,
            Path.Combine(payload, "desktop-plugin.json"), null);
        var package = Path.Combine(root, "dictation-candidate.CerebralPlugin");
        CerebralPluginPackage.Export(info, package, "none", new(root, root, root, root, root, root, root));
        using (var prepared = CerebralPluginPackage.Prepare(package))
        {
            if (prepared.Inspection.Package.SchemaVersion != 3 ||
                prepared.Inspection.Package.RequiredHostFacets is not [CerebralPluginPackage.SubmissionLifecycleHostFacet])
                throw new InvalidOperationException("The package writer did not retain its SDK/host requirement.");
            checks++;
        }
        var reader = typeof(CerebralPluginPackage).GetMethod("ReadManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        static object Read(MethodInfo method, JsonObject manifest, string directory)
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var stream = new StreamWriter(archive.CreateEntry("cerebral-plugin.json").Open(), new UTF8Encoding(false))) stream.Write(manifest.ToJsonString());
            using var input = ZipFile.OpenRead(path);
            return method.Invoke(null, [input.Entries.Single()])!;
        }
        static JsonObject Manifest(int schema) => new()
        { ["schemaVersion"] = schema, ["contract"] = "pegasus.cerebral.plugin", ["kind"] = "desktop", ["id"] = "fixture.submission",
            ["name"] = "Fixture", ["version"] = "1.0.0", ["description"] = "Fixture", ["payload"] = "plugin/desktop-plugin.json", ["settingsMode"] = "none" };
        foreach (var schema in new[] { 1, 2, 3 })
        {
            var manifest = Manifest(schema); if (schema >= 2) manifest["resources"] = new JsonArray();
            if (schema == 3) manifest["requiredHostFacets"] = new JsonArray(CerebralPluginPackage.SubmissionLifecycleHostFacet);
            Read(reader, manifest, root); checks++;
        }
        foreach (var facets in new JsonNode?[] { null, new JsonArray(), new JsonArray("unknown/1"),
            new JsonArray(CerebralPluginPackage.SubmissionLifecycleHostFacet, CerebralPluginPackage.SubmissionLifecycleHostFacet),
            new JsonArray(7), new JsonArray(new string('x', 97)), new JsonArray(Enumerable.Range(0, 9).Select(_ => (JsonNode?)JsonValue.Create("facet/1")).ToArray()) })
        {
            var manifest = Manifest(3); manifest["resources"] = new JsonArray(); manifest["requiredHostFacets"] = facets;
            try { Read(reader, manifest, root); throw new InvalidOperationException("Unsupported host facets were admitted."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { checks++; }
        }
        var oldPath = Path.Combine(AppContext.BaseDirectory, "baseline-libraries", "Cerebral.Desktop.dll");
        var context = new AssemblyLoadContext("old-package-reader", isCollectible: true);
        try
        {
            var old = context.LoadFromAssemblyPath(oldPath).GetType(typeof(CerebralPluginPackage).FullName!)!
                .GetMethod("ReadManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
            var manifest = Manifest(3); manifest["resources"] = new JsonArray();
            manifest["requiredHostFacets"] = new JsonArray(CerebralPluginPackage.SubmissionLifecycleHostFacet);
            try { Read(old, manifest, root); throw new InvalidOperationException("The old host admitted new schema 3."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { checks++; }
        }
        finally { context.Unload(); }
        return checks;
    }
}

public sealed class SubmissionLifecycleProbe : IDesktopPlugin, IDesktopTextEntrySubmissionLifecyclePlugin
{
    private IDesktopPluginHost? host;
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
    public void Initialize(IDesktopPluginHost value) => host = value;
    public void Dispose() => host = null;
    public Task<DesktopOperationResult> PrepareSubmissionAsync(IDesktopTextEntry target, CancellationToken cancellationToken = default)
    { host!.ReportStatus("probe:legacy"); return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed)); }
    public Task<DesktopOperationResult> PrepareSubmissionAsync(string requestId, IDesktopTextEntry target, CancellationToken cancellationToken = default)
    { host!.ReportStatus("probe:request:" + requestId); return Task.FromResult(new DesktopOperationResult(requestId == "rejected" ? DesktopOperationState.Rejected : DesktopOperationState.Completed)); }
    public void SubmissionAcknowledged(DesktopTextEntrySubmissionAcknowledgment acknowledgment) => host!.ReportStatus("probe:ack:" + acknowledgment.RequestId);
}
