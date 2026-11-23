using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Opt-in real-pipe/shown-window checks with a deterministic, task-owned endpoint.
/// No Worker executable, model, provider, installed endpoint or Source dataset is used.</summary>
public static class AttachExistingStartupChecks
{
    public static int Run(string? previewPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These current-user pipe/WPF checks require Windows.");
        if (previewPath is not null)
        {
            if (!Path.IsPathFullyQualified(previewPath) || previewPath.Any(char.IsControl) ||
                !Path.GetExtension(previewPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(previewPath).Equals(previewPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The optional preview must be an exact absolute PNG path.", nameof(previewPath));
            ScopedFilePolicy.RejectLinks(previewPath);
            if (!Directory.Exists(Path.GetDirectoryName(previewPath)))
                throw new DirectoryNotFoundException("The preview's caller-owned parent directory must already exist.");
            if (File.Exists(previewPath)) throw new IOException("The preview already exists; preserve it and select another exact path.");
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-attach-existing-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        var desktop = Path.Combine(root, "desktop");
        Directory.CreateDirectory(worker);
        var checks = 0;
        var skipped = 0;
        var exit = 1;
        OrchestratorWindow? window = null;
        OwnedPipeFixture? fixture = null;
        Application? app = null;
        try
        {
            fixture = new OwnedPipeFixture(worker);
            var arguments = new[] { "--attach-existing", fixture.Endpoint.PipeName, fixture.Endpoint.InstanceId,
                worker, desktop, fixture.InstallationId };
            var sharedIdentityChecks = ExistingWorkerDesktopOptionsChecks.Verify();
            Console.WriteLine($"Shared attachment identity: {sharedIdentityChecks} pure compatibility/component checks passed.");
            VerifyArguments(arguments, root, Check, ref skipped);
            var runWpf = typeof(Program).GetMethod("RunWpf", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(typeof(Program).FullName, "RunWpf");
            foreach (var diagnostic in new[] { "--check-local-pipe-ui", "--check-source-local-options" })
            {
                // The absent --worker operand also fences a future ordering regression before any Worker launch.
                string[] mixed = [.. arguments, diagnostic, "--worker", Path.Combine(root, "must-not-launch.dll")];
                var refused = false;
                try { _ = runWpf.Invoke(null, [mixed]); }
                catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { refused = true; }
                Check(refused && Application.Current is null && fixture.Commands.IsEmpty && !Directory.Exists(desktop),
                    "the actual startup entry refuses mixed attach/diagnostic flags before Application, Worker or connection work");
            }
            var options = ExistingWorkerDesktopOptions.FromArguments(arguments)!;
            Check(options.Endpoint == fixture.Endpoint && options.WorkerDataDirectory == worker &&
                  options.DesktopDataDirectory == desktop && options.InstallationId == fixture.InstallationId,
                "the parsed binding retains all exact caller-selected identities and independent roots");
            Check(!Directory.Exists(desktop), "parsing does not create persistent desktop state");

            // Seed the private attached store before construction. The selected existing chat
            // has no draft-map entry, matching a blank composer after a previous response.
            var paths = DesktopInstancePaths.UseAttachedExisting(desktop, AppContext.BaseDirectory);
            using var userIdentity = WindowsIdentity.GetCurrent();
            var user = userIdentity.User?.Value
                ?? throw new InvalidOperationException("The fixture needs the signed-in desktop identity.");
            var userFolder = ComposerDraftStore.Hash(user)[..24];
            var drafts = new ComposerDraftStore(Path.Combine(paths.UserDataDirectory, "Desktop", "Composer", userFolder),
                paths.ApplicationIdentity.Mode + "\n" + paths.UserDataDirectory.ToUpperInvariant() + "\n" + user);
            var selectedId = Guid.NewGuid().ToString("N");
            var selectedRunId = Guid.NewGuid().ToString("N");
            const string selectedProject = "fixture-project";
            var retainedDrafts = new Dictionary<string, string>
            {
                ["other-one"] = "Retained one", ["other-two"] = "Retained two", ["other-three"] = "Retained three"
            };
            drafts.Write(retainedDrafts, selectedId, false, selectedProject);
            var savedDraftBytes = File.ReadAllBytes(drafts.FilePath);
            fixture.SavedConversation = new(selectedId, "Attached saved conversation", "fixture-lead", selectedProject,
                selectedRunId, WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            fixture.SavedRun = new()
            {
                Id = selectedRunId, Revision = 1, Status = WorkflowStatus.Completed,
                Submission = new(selectedRunId, selectedId, "fixture-lead", ConversationControlClient.ChatDefinitionId,
                    ConversationControlClient.ChatDefinitionVersion, "Fixture prior request", null,
                    WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
                CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
            };
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // This order is intentional: attach selects private stores before theme/preferences/window access.
            window = AttachExistingStartup.CreateWindow(options);
            Ui.ApplyTheme(app);
            app.MainWindow = window;
            _ = ExistingWorkerDesktopPresentationChecks.Verify();
            var capturedAttachment = window.ExistingWorkerAttachment;
            Check(capturedAttachment is not null && capturedAttachment.Endpoint == fixture.Endpoint &&
                  capturedAttachment.WorkerDataDirectory == worker && capturedAttachment.InstallationId == fixture.InstallationId,
                "the fixed attachment is readable before loading and retains only the exact endpoint/root/installation");
            Check(ScopedFilePolicy.IsWithin(ConnectionStore.FilePath, desktop) &&
                  ScopedFilePolicy.IsWithin(DesktopInstancePaths.Current.UserDataDirectory, desktop) &&
                  !DesktopInstancePaths.Current.SourceLocal,
                "the attach route selects private non-Source preferences, plugins, drafts and connection storage");
            Check(!File.Exists(ConnectionStore.FilePath), "creating the attached window does not persist a connection declaration");
            var rebound = false;
            try { window.UseExistingLocalWorker(fixture.Endpoint with { InstanceId = Guid.NewGuid().ToString("N") }, worker, fixture.InstallationId); }
            catch (InvalidOperationException) { rebound = true; }
            Check(rebound && ReferenceEquals(capturedAttachment, window.ExistingWorkerAttachment),
                "an already captured existing Worker binding and read-only accessor cannot be replaced before startup");
            var retainedWindow = window;
            var retainedFixture = fixture;
            var retainedApp = app;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var connectionChecks = ConnectionStoreChecks.Verify();
                    Console.WriteLine($"Connection-store regression: {connectionChecks} isolated file/transport-declaration checks passed.");
                    _ = await InferenceOwnershipConsumerChecks.RunAsync();
                    var workspaceChecks = await WorkflowWorkspaceConnectionChecks.VerifyAsync();
                    Console.WriteLine($"Workspace connection regression: {workspaceChecks} deterministic dispatcher/owner-fence checks passed; no RPC.");
                    await VerifyPipeAndWindowAsync(retainedWindow, retainedFixture, worker, selectedId,
                        drafts.FilePath, savedDraftBytes, Check, previewPath);
                    var surfaceChecks = await retainedWindow.VerifyWorkflowSurfacePresentationAsync();
                    Console.WriteLine($"Workflow surface regression: {surfaceChecks} deterministic shown-window presentation/automation checks passed.");
                    await CloseFixtureWindowAsync(retainedWindow);
                    await VerifyUnshownOutcomesAsync(options, retainedFixture, Check);
                    retainedFixture.FailSavedConversationLookup = true;
                    var failedId = Guid.NewGuid().ToString("N");
                    drafts.Write(retainedDrafts, failedId, false, selectedProject);
                    var failedDraftBytes = File.ReadAllBytes(drafts.FilePath);
                    // Preference selection is process-wide and one-shot. Reuse the exact
                    // authenticated private binding for this second fixture window.
                    var failedWindow = CreateFollowupWindow(options);
                    try
                    {
                        retainedApp.MainWindow = failedWindow;
                        Check(failedWindow.ExistingWorkerAttachment is { } reboundAttachment &&
                              reboundAttachment.Endpoint == fixture.Endpoint &&
                              reboundAttachment.WorkerDataDirectory == worker &&
                              reboundAttachment.InstallationId == fixture.InstallationId,
                            "the failed-reopen window retains the same authenticated private Worker binding");
                        failedWindow.ShowActivated = false;
                        failedWindow.Show();
                        using var failedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        await failedWindow.WaitForInitialReadyAsync(failedDeadline.Token);
                        var failedSnapshot = CerebralJson.Element(typeof(OrchestratorWindow)
                            .GetMethod("ComposerDraftControlSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(failedWindow, null)!);
                        Check(failedWindow.ServiceConnected &&
                              failedSnapshot.GetProperty("selectedConversationId").GetString() != failedId &&
                              failedSnapshot.GetProperty("persistedSelectedConversationId").GetString() == failedId &&
                              failedSnapshot.GetProperty("selectionError").GetString()?.Contains("could not reopen", StringComparison.Ordinal) == true &&
                              failedSnapshot.GetProperty("retained").GetInt32() == retainedDrafts.Count &&
                              failedDraftBytes.SequenceEqual(File.ReadAllBytes(drafts.FilePath)),
                            "a failed attached reopen leaves its exact blank selected-chat metadata and other drafts on disk");
                    }
                    finally { failedWindow.Close(); }
                    Check(failedDraftBytes.SequenceEqual(File.ReadAllBytes(drafts.FilePath)),
                        "closing after failed reopen does not persist a temporary conversation identity");
                    Check(!File.Exists(ConnectionStore.FilePath), "normal attach/watch/reconnect never writes even the private connection declaration");
                    Console.WriteLine($"Attach-existing checks passed ({checks}; {skipped} skipped; task-owned Windows pipe and WPF/Win32 startup visibility checks, deterministic replies; no Worker/model/Source).");
                    exit = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally
                {
                    try { retainedWindow.Close(); await retainedFixture.DisposeAsync(); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    finally { retainedApp.Shutdown(exit); }
                }
            }));
            return app.Run();
        }
        finally
        {
            if (window is not null && window.IsVisible) window.Close();
            fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var full = Path.GetFullPath(root);
            if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cerebral-attach-existing-", StringComparison.Ordinal))
                throw new InvalidOperationException("The attach fixture cleanup target changed.");
            Directory.Delete(full, recursive: true);
        }

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Attach-existing: " + description);
            checks++;
        }
    }

    private static void VerifyArguments(string[] valid, string root, Action<bool, string> check, ref int skipped)
    {
        check(ExistingWorkerDesktopOptions.FromArguments([]) is null &&
              ExistingWorkerDesktopOptions.FromArguments(["--source-local"]) is null,
            "ordinary and Source startup remain outside the explicit attach route");
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        List<string[]> cases =
        [
            valid[..^1], [.. valid, "extra"], [.. valid, "--source-local"], [.. valid, "--check-local-pipe-ui"],
            [.. valid, "--model", "anything"], [.. valid, "--attach-existing"], ["ignored", .. valid]
        ];
        foreach (var (position, value) in new (int, string)[]
        {
            (1, "bad pipe"), (1, "\\\\.\\pipe\\foreign"), (2, "not-an-instance"), (2, Guid.Empty.ToString("N")),
            (3, "relative"), (4, "relative"), (3, Path.GetPathRoot(root)!), (4, Path.GetPathRoot(root)!),
            (3, "\\\\server\\share\\worker"), (4, root + ":alternate"),
            (3, Path.Combine(root, "absent-worker")), (4, valid[3]), (4, Path.Combine(valid[3], "desktop")),
            (4, Path.GetDirectoryName(valid[3])!),
            (5, ""), (5, "bad\nidentity"), (5, new string('i', 257))
        })
        {
            var invalid = (string[])valid.Clone(); invalid[position] = value; cases.Add(invalid);
        }
        foreach (var invalid in cases)
        {
            var refused = false;
            try { _ = ExistingWorkerDesktopOptions.FromArguments(invalid); }
            catch (Exception error) when (error is ArgumentException or IOException) { refused = true; }
            check(refused, "mixed, duplicate, malformed or conflicting startup arguments fail before startup");
        }
        check(before.SequenceEqual(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)),
            "all rejected parser inputs leave task-owned directories unchanged");
        var link = Path.Combine(root, "linked-worker");
        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(link, valid[3]); linked = true;
            var invalid = (string[])valid.Clone(); invalid[3] = link;
            var refused = false;
            try { _ = ExistingWorkerDesktopOptions.FromArguments(invalid); }
            catch (IOException) { refused = true; }
            check(refused, "a linked Worker directory is rejected rather than adopted");
        }
        catch (Exception error) when (!linked && error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            skipped++;
            var detail = error.Message.Replace('\r', ' ').Replace('\n', ' ');
            Console.WriteLine($"SKIP: attach symlink fixture {error.GetType().Name} (0x{error.HResult:X8}): " + detail[..Math.Min(256, detail.Length)]);
        }
        finally { if (linked) Directory.Delete(link); }
    }

    private static async Task VerifyPipeAndWindowAsync(OrchestratorWindow window, OwnedPipeFixture fixture,
        string worker, string selectedId, string draftPath, byte[] savedDraftBytes,
        Action<bool, string> check, string? previewPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        await fixture.StartAsync(deadline.Token);
        foreach (var fault in new[] { "hello-product", "hello-protocol", "hello-instance" })
        {
            fixture.Fault = fault;
            var commands = fixture.Commands.Count;
            using var client = new LocalWorkerClient(fixture.Endpoint);
            var refused = false;
            try { _ = await client.CommandAsync(new("health", CerebralJson.Element(new { })), deadline.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException) { refused = true; }
            check(refused && fixture.Commands.Count == commands, fault + " refuses before any command dispatch");
        }
        foreach (var fault in new[] { "health-product", "health-protocol", "health-root", "health-installation" })
        {
            fixture.Fault = fault;
            var commands = fixture.Commands.Count;
            var probe = new ConnectionProbe();
            try
            {
                probe.UseExistingLocalWorker(fixture.Endpoint, worker, fixture.InstallationId);
                var refused = false;
                try { await probe.ConnectNowAsync(); }
                catch (InvalidOperationException) { refused = true; }
                check(refused && !probe.ServiceConnected && probe.Hydrations == 0 &&
                      fixture.Commands.Skip(commands).Select(x => x.Operation).SequenceEqual(["health"]),
                    fault + " refuses before hydration, event subscription or fallback");
            }
            finally { probe.Close(); }
        }
        fixture.Fault = "";
        window.Width = 1024; window.Height = 768; window.ShowActivated = false;
        await VerifyUnshownPreparationAsync(window, fixture, check, deadline.Token);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
        check(window.IsVisible && window.ServiceConnected && new WindowInteropHelper(window).Handle != IntPtr.Zero,
            "the actual shown Orchestrator HWND completes normal initial attach");
        var selectedDraft = CerebralJson.Element(typeof(OrchestratorWindow)
            .GetMethod("ComposerDraftControlSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!);
        check(selectedDraft.GetProperty("selectedConversationId").GetString() == selectedId &&
              selectedDraft.GetProperty("persistedSelectedConversationId").GetString() == selectedId &&
              selectedDraft.GetProperty("retained").GetInt32() == 3 &&
              savedDraftBytes.SequenceEqual(File.ReadAllBytes(draftPath)),
            "the attached startup reopens a blank selected existing chat without rewriting its three other drafts");
        var shownSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        check(shownSource?.CompositionTarget.RenderMode == RenderMode.SoftwareOnly,
            "the production attach path configures only its own shown source for the development presentation fallback");
        check(window.Title.Length > 0 && CapturedConnection(window).LocalEndpoint == fixture.Endpoint &&
              CapturedConnection(window).LocalDataDirectory == worker && !CapturedConnection(window).AutoStartLocalServices,
            "normal startup captures the exact pipe instance and root with automatic service startup disabled");
        await UntilAsync(() => fixture.EventSubscriptions >= 1, deadline.Token);
        var initialSubscriptions = fixture.EventSubscriptions;
        var priorAgents = fixture.Commands.Count(x => x.Operation == "agents.list");
        fixture.Publish("agents.updated", new { fixture = true });
        await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "agents.list") > priorAgents, deadline.Token);
        check(window.ServiceConnected, "the production event watch consumes a real pipe notification and refreshes the attached window");
        if (previewPath is not null)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, deadline.Token);
            window.UpdateLayout();
            SavePreview(window, previewPath);
            check(new FileInfo(previewPath).Length > 0, "the explicitly requested shown-window content preview is saved as a new PNG");
        }

        // Simulate one admitted command whose reply is lost. The fixture changes only its own counter.
        var activeClient = (CerebralClient)typeof(ConnectedWindow).GetProperty("Client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var uncertain = false;
        try { _ = await activeClient.CommandAsync("fixture.accept-once", new { requestId = "owned-once" }, cancellationToken: deadline.Token); }
        catch (LocalWorkerCommandUncertainException) { uncertain = true; }
        check(uncertain && fixture.OnceCommands == 1, "a lost reply remains uncertain after exactly one task-owned command dispatch");
        var priorHealth = fixture.Commands.Count(x => x.Operation == "health");
        fixture.DisconnectEvents();
        await UntilAsync(() => !window.ServiceConnected, deadline.Token);
        await UntilAsync(() => window.ServiceConnected && fixture.EventSubscriptions > initialSubscriptions, deadline.Token);
        check(fixture.Commands.Count(x => x.Operation == "health") > priorHealth &&
              CapturedConnection(window).LocalEndpoint == fixture.Endpoint && CapturedConnection(window).LocalDataDirectory == worker,
            "the normal monitor reconnects and revalidates only its captured endpoint/root");
        priorAgents = fixture.Commands.Count(x => x.Operation == "agents.list");
        fixture.Publish("agents.updated", new { fixture = "after-reconnect" });
        await UntilAsync(() => fixture.Commands.Count(x => x.Operation == "agents.list") > priorAgents, deadline.Token);
        check(fixture.OnceCommands == 1, "event reconnection never replays the uncertain command");
        check(fixture.UnexpectedOperations.IsEmpty, "startup, watch and reconnect submit no mutation or unapproved operation: " +
            string.Join(", ", fixture.UnexpectedOperations.Take(8)));
        check(fixture.Errors.IsEmpty, "the owned pipe fixture has no hidden transport failure: " + string.Join("; ", fixture.Errors.Take(3)));
        check(fixture.Commands.All(x => x.ProfileId is null), "owner connection checks retain their expected same-user command identity");
    }

    private static ConnectionPreferences CapturedConnection(ConnectedWindow window) =>
        (ConnectionPreferences)typeof(ConnectedWindow).GetProperty("ServiceConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static async Task VerifyUnshownPreparationAsync(OrchestratorWindow window, OwnedPipeFixture fixture,
        Action<bool, string> check, CancellationToken token)
    {
        var splash = CerebralStartupWindow.ForAttached();
        var splashClosed = false;
        nint splashHandle = 0;
        var earlyReveal = false;
        var shows = 0;
        void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (window.IsVisible)
            {
                shows++;
                earlyReveal |= !splashClosed || splash.IsVisible || IsWindowVisible(splashHandle);
            }
        }
        splash.Closed += (_, _) => splashClosed = true;
        window.IsVisibleChanged += VisibleChanged;
        var gate = fixture.DelayNextHealth();
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            splash.ShowActivated = false; splash.ShowInTaskbar = false; splash.Show();
            splashHandle = new WindowInteropHelper(splash).Handle;
            await splash.WaitForFirstContentRenderedAsync(token);
            var prime = Program.PrimeMainWindowAsync(window, splash, token);
            await gate.Entered.Task.WaitAsync(token);
            var initial = window.BeginStartupBeforeShowAsync();
            check(ReferenceEquals(initial, window.BeginStartupBeforeShowAsync()),
                "repeated pre-show startup shares the exact one-shot connection task");
            CheckUnshown(window, check, "while the owned readiness reply is delayed");
            check(!prime.IsCompleted && !window.PreparedForStartupReveal && splash.IsVisible,
                "the delayed Worker readiness keeps Splash active and Main unprepared");
            gate.Release.TrySetResult();
            await prime;
            Program.RequirePreparedWindow(window);
            CheckUnshown(window, check, "after readiness and before Splash closes");
            check(!window.IsLoaded && shows == 0, "preparing Main does not synthesize Loaded or perform a transient Show");
            var runtimeField = typeof(OrchestratorWindow).GetField("desktopPlugins", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var runtime = runtimeField.GetValue(window);
            Program.RevealPreparedWindow(window, splash, startHidden: false);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            check(splashClosed && !splash.IsVisible && !IsWindowVisible(splashHandle) && !earlyReveal &&
                shows == 1 && window.IsLoaded && window.IsVisible && IsWindowVisible(new WindowInteropHelper(window).Handle),
                "the real Splash Closed callback finishes before Main's first WPF/Win32 reveal");
            check(ReferenceEquals(initial, window.BeginStartupBeforeShowAsync()) && ReferenceEquals(runtime, runtimeField.GetValue(window)),
                "the later real Loaded event preserves the connection task and plugin generation");
        }
        finally
        {
            gate.Release.TrySetResult();
            window.IsVisibleChanged -= VisibleChanged;
            if (!splashClosed) splash.Close();
        }
    }

    private static async Task VerifyUnshownOutcomesAsync(ExistingWorkerDesktopOptions options, OwnedPipeFixture fixture,
        Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var outcome in new[] { "tray", "cancel", "late-cancel", "failure" })
        {
            var window = CreateFollowupWindow(options);
            var splash = CerebralStartupWindow.ForAttached();
            var splashClosed = false;
            var windowClosed = false;
            var shows = 0;
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.IsVisibleChanged += (_, _) => { if (window.IsVisible) shows++; };
            window.Closed += (_, _) => windowClosed = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            splash.Closed += (_, _) =>
            {
                splashClosed = true;
                if (outcome is "cancel" or "late-cancel") cancellation.Cancel();
            };
            OwnedStartupReadGate? gate = null;
            Task<DesktopStartupOutcome>? prime = null;
            try
            {
                splash.ShowActivated = false; splash.ShowInTaskbar = false; splash.Show();
                await splash.WaitForFirstContentRenderedAsync(deadline.Token);
                if (outcome == "cancel") gate = fixture.DelayNextHealth();
                if (outcome == "failure") fixture.Fault = "health-product";
                prime = Program.PrimeMainWindowAsync(window, splash, cancellation.Token);
                if (outcome == "cancel")
                {
                    await gate!.Entered.Task.WaitAsync(deadline.Token);
                    CheckUnshown(window, check, "before cancelling delayed startup");
                    splash.Close();
                    var cancelled = false;
                    try { await prime; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
                    check(cancelled && !window.PreparedForStartupReveal, "Splash cancellation cannot prepare or reveal Main");
                }
                else if (outcome == "failure")
                {
                    MainStartupException? failure = null;
                    try { await prime; }
                    catch (MainStartupException error) { failure = error; }
                    check(failure is not null && !window.PreparedForStartupReveal, "failed Worker readiness cannot prepare Main");
                    splash.ShowFailure(failure!);
                    check(splash.IsVisible, "startup failure remains on Splash");
                }
                else if (outcome == "late-cancel")
                {
                    await prime;
                    splash.Close();
                    check(!window.PreparedForStartupReveal, "cancellation after readiness still prevents the first Main reveal");
                }
                else
                {
                    await prime;
                    Program.RevealPreparedWindow(window, splash, startHidden: true);
                    check(splashClosed && window.PreparedForStartupReveal && !window.IsLoaded,
                        "--tray closes Splash after preparation without loading Main");
                }
                CheckUnshown(window, check, outcome + " startup outcome");
                check(shows == 0, outcome + " never transiently shows Main");
                if (outcome != "tray")
                {
                    var refused = false;
                    try { Program.RevealPreparedWindow(window, splash, startHidden: false); }
                    catch (MainStartupException) { refused = true; }
                    check(refused && shows == 0, "an unprepared " + outcome + " window cannot pass the reveal guard");
                }
            }
            finally
            {
                fixture.Fault = "";
                cancellation.Cancel();
                gate?.Release.TrySetResult();
                if (!windowClosed) await CloseFixtureWindowAsync(window);
                if (prime is not null)
                {
                    // Preserve the owned task through natural cancellation/close; never
                    // leave a delayed fixture callback waiting on a discarded window.
                    try { await prime.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (MainStartupException) when (outcome == "failure") { }
                }
                if (!splashClosed) splash.Close();
            }
        }
        check(fixture.UnexpectedOperations.IsEmpty && fixture.Errors.IsEmpty,
            "visibility outcome checks use only the owned fixture's admitted operations");
    }

    private static void CheckUnshown(Window window, Action<bool, string> check, string stage)
    {
        var handle = new WindowInteropHelper(window).Handle;
        check(handle != nint.Zero && !window.IsVisible && !IsWindowVisible(handle) && window.Opacity == 1,
            "Main stays genuinely WPF/Win32 unshown, without opacity masking, " + stage);
    }

    private static async Task CloseFixtureWindowAsync(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        window.Closed += OnClosed;
        try { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { window.Closed -= OnClosed; }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    private sealed class OwnedStartupReadGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static OrchestratorWindow CreateFollowupWindow(ExistingWorkerDesktopOptions options)
    {
        // The first window has already exercised the production attach setup. A second
        // UseProcessLocalDirectory call is intentionally forbidden by Desktop.
        var validated = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", options.Endpoint.PipeName,
            options.Endpoint.InstanceId, options.WorkerDataDirectory, options.DesktopDataDirectory,
            options.InstallationId])!;
        var selected = DesktopInstancePaths.Current;
        var desktop = validated.DesktopDataDirectory;
        if (selected.SourceLocal ||
            !Path.TrimEndingDirectorySeparator(selected.UserDataDirectory).Equals(desktop, StringComparison.OrdinalIgnoreCase) ||
            !ConnectionStore.FilePath.Equals(Path.Combine(desktop, "connection.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The failed-reopen fixture lost its first window's exact private desktop binding.");
        var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(desktop, "Plugins", "Desktop")),
            selected.PluginDataDirectory, composerDraftPaths: selected, attachedComposerStartup: validated);
        window.UseExistingLocalWorker(validated.Endpoint, validated.WorkerDataDirectory, validated.InstallationId);
        return window;
    }

    private static void SavePreview(OrchestratorWindow window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var pixelWidth = Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var pixelHeight = Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight) ||
            pixelWidth is < 1 or > 8192 || pixelHeight is < 1 or > 8192)
            throw new InvalidOperationException("The shown-window preview must fit within 8192 pixels on each axis.");
        var image = new RenderTargetBitmap((int)pixelWidth, (int)pixelHeight,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        // A caller must opt in with an exact path. Existing evidence is never replaced.
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        output.Flush(flushToDisk: true);
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(30, token);
    }

    private sealed class ConnectionProbe() : ConnectedWindow("Orchestrator", "Owned attach admission fixture", persistWindowPreferences: false)
    {
        internal int Hydrations { get; private set; }
        internal Task ConnectNowAsync() => ConnectAsync();
        protected override Task ConnectedAsync(HostInfo info) { Hydrations++; return Task.CompletedTask; }
        protected override Task<object> InspectLoadedStateAsync(string? sessionId) => Task.FromResult<object>(new { });
    }

    private sealed class OwnedPipeFixture(string workerRoot) : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentDictionary<int, NamedPipeServerStream> pipes = new();
        private readonly ConcurrentDictionary<int, Channel<ServiceEvent>> subscribers = new();
        private readonly ConcurrentBag<Task> handlers = [];
        private Task? acceptLoop;
        private int nextPipe, subscriptions, once, disposed, failSaved;
        private long sequence;
        private string fault = "";
        private OwnedStartupReadGate? startupReadGate;
        internal LocalWorkerEndpoint Endpoint { get; } = new("cerebral-attach-fixture-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        internal string InstallationId { get; } = Guid.NewGuid().ToString("N");
        internal ConcurrentQueue<CommandRequest> Commands { get; } = new();
        internal ConcurrentQueue<string> UnexpectedOperations { get; } = new();
        internal ConcurrentQueue<string> Errors { get; } = new();
        internal int EventSubscriptions => Volatile.Read(ref subscriptions);
        internal int OnceCommands => Volatile.Read(ref once);
        internal string Fault { get => Volatile.Read(ref fault); set => Volatile.Write(ref fault, value); }
        internal ConversationSummary? SavedConversation { get; set; }
        internal WorkflowRun? SavedRun { get; set; }
        internal bool FailSavedConversationLookup
        {
            get => Volatile.Read(ref failSaved) != 0;
            set => Volatile.Write(ref failSaved, value ? 1 : 0);
        }

        internal OwnedStartupReadGate DelayNextHealth()
        {
            var gate = new OwnedStartupReadGate();
            if (Interlocked.CompareExchange(ref startupReadGate, gate, null) is not null)
                throw new InvalidOperationException("Only one owned readiness reply may be delayed.");
            return gate;
        }

        internal async Task StartAsync(CancellationToken token)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptLoop = Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var id = Interlocked.Increment(ref nextPipe);
                        var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        pipes[id] = pipe; ready.TrySetResult();
                        await pipe.WaitForConnectionAsync(lifetime.Token);
                        handlers.Add(HandleAsync(id, pipe));
                    }
                }
                catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException) { }
                catch (Exception error) { ready.TrySetException(error); Errors.Enqueue(error.GetType().Name + ": " + error.Message); }
            }, CancellationToken.None);
            await ready.Task.WaitAsync(token);
        }

        private async Task HandleAsync(int id, NamedPipeServerStream pipe)
        {
            try
            {
                var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, lifetime.Token);
                LocalWorkerProtocol.ValidateHello(hello, Endpoint);
                var selectedFault = Fault;
                await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerHello(
                    selectedFault == "hello-product" ? "Fixture foreign product" : LocalWorkerProtocol.Product,
                    selectedFault == "hello-protocol" ? LocalWorkerProtocol.Version + 1 : LocalWorkerProtocol.Version,
                    selectedFault == "hello-instance" ? Guid.NewGuid().ToString("N") : Endpoint.InstanceId), lifetime.Token);
                var request = await LocalWorkerProtocol.ReadAsync<LocalWorkerRequest>(pipe, lifetime.Token);
                LocalWorkerProtocol.ValidateRequest(request);
                if (request.Kind == "events")
                {
                    await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Ok(new { subscribed = true }), lifetime.Token);
                    var events = Channel.CreateBounded<ServiceEvent>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });
                    subscribers[id] = events; Interlocked.Increment(ref subscriptions);
                    await foreach (var item in events.Reader.ReadAllAsync(lifetime.Token))
                        await LocalWorkerProtocol.WriteAsync(pipe, item, lifetime.Token);
                }
                else
                {
                    var command = request.Command!; Commands.Enqueue(command);
                    if (command.Operation == "fixture.accept-once") { Interlocked.Increment(ref once); return; }
                    if (command.Operation == "health" && Interlocked.Exchange(ref startupReadGate, null) is { } gate)
                    {
                        gate.Entered.TrySetResult();
                        await gate.Release.Task.WaitAsync(lifetime.Token);
                    }
                    await LocalWorkerProtocol.WriteAsync(pipe, Respond(command.Operation, selectedFault), lifetime.Token);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception error) { Errors.Enqueue(error.GetType().Name + ": " + error.Message); }
            finally
            {
                subscribers.TryRemove(id, out _); pipes.TryRemove(id, out _); pipe.Dispose();
            }
        }

        private CommandResult Respond(string operation, string selectedFault)
        {
            switch (operation)
            {
                case "health": return CommandResult.Ok(new HostInfo(
                    selectedFault == "health-product" ? "Fixture foreign product" : LocalWorkerProtocol.Product, "0.2.22",
                    selectedFault == "health-protocol" ? LocalWorkerProtocol.Version + 1 : LocalWorkerProtocol.Version,
                    "Owned attach fixture", "fixture", "Windows", false,
                    selectedFault == "health-root" ? Path.Combine(workerRoot, "foreign") : workerRoot, true,
                    selectedFault == "health-installation" ? Guid.NewGuid().ToString("N") : InstallationId));
                case "settings.get": return CommandResult.Ok(new HostSettings
                { ProjectsDirectory = Path.Combine(workerRoot, "Projects"), ModelDirectory = Path.Combine(workerRoot, "models"),
                    ToolsDirectory = Path.Combine(workerRoot, "Tools"), SharedDirectory = Path.Combine(workerRoot, "Shared") });
                case "models.list": return CommandResult.Ok(Array.Empty<ModelRecord>());
                case "agents.list": return CommandResult.Ok(Array.Empty<AgentProfile>());
                case "queue.list": case "jobs.history": return CommandResult.Ok(Array.Empty<AgentJob>());
                case "conversations.recent": case "conversations.protected": return CommandResult.Ok(new ConversationRecentPage([], 0, null));
                case "conversations.get": return FailSavedConversationLookup || SavedConversation is null
                    ? CommandResult.Fail("Fixture saved conversation could not reopen.")
                    : CommandResult.Ok(SavedConversation);
                case "jobs.workflow.get": return SavedRun is null
                    ? CommandResult.Fail("Fixture saved run is unavailable.") : CommandResult.Ok(SavedRun);
                case "jobs.workflow.events.previous": return CommandResult.Ok(new
                { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0L, nextBeforeSequence = (long?)null });
                case "conversations.properties.get": return CommandResult.Fail("Unknown command: conversations.properties.get");
                case "projects.knowledge.get": return CommandResult.Ok(new ProjectKnowledgeSettings { ProjectId = "fixture-project" });
                case "interface.list": return CommandResult.Ok(Array.Empty<InterfaceRequest>());
                case "jobs.workflow.recent": return CommandResult.Ok(new WorkflowRecentPage([], 0, null));
                case "conversations.workspace.get": return CommandResult.Fail("Conversation not found: fixture unsaved conversation");
                case "navigation.get": return CommandResult.Ok(new NavigationState());
                case "interface.preferences.get": case "interface.theme.get":
                case "metrics.execution.status": case "metrics.slots.status": case "metrics.inference.status":
                case "resources.local.status": case "resources.status": case "resources.get":
                case "engine.status": case "engine.work-hold.status":
                    return CommandResult.Fail("Unknown command: " + operation);
                default:
                    UnexpectedOperations.Enqueue(operation);
                    return CommandResult.Fail("The task-owned fixture refuses unapproved operation: " + operation);
            }
        }

        internal void Publish(string type, object data)
        {
            var item = new ServiceEvent(Interlocked.Increment(ref sequence), type, null, CerebralJson.Element(data), DateTimeOffset.UtcNow);
            foreach (var target in subscribers.Values) target.Writer.TryWrite(item);
        }
        internal void DisconnectEvents()
        {
            foreach (var pair in subscribers)
            {
                pair.Value.Writer.TryComplete();
                if (pipes.TryGetValue(pair.Key, out var pipe)) pipe.Dispose();
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            foreach (var channel in subscribers.Values) channel.Writer.TryComplete();
            foreach (var pipe in pipes.Values) pipe.Dispose();
            if (acceptLoop is not null) await acceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(handlers.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Dispose();
        }
    }
}
