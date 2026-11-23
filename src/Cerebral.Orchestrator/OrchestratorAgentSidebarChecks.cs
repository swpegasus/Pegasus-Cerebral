using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Pure observation projection and actual offscreen WPF pulse/fence lifecycle.
    /// Deterministic data only; no Worker connection, provider, model or hardware measurements.</summary>
    public static async Task<int> CheckAgentSidebarUiAsync(string? previewPath = null)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        const string installation = "11111111111111111111111111111111";
        const string otherInstallation = "33333333333333333333333333333333";
        const string generation = "22222222222222222222222222222222";
        var stamp = DateTimeOffset.Parse("2026-10-13T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        AgentSidebarProfile[] profiles = [new("lead", "Cerebral", "local"), new("builder", "Builder", "local"),
            new("remote", "Remote reviewer", "peer"), new("compactor", "Compactor", "local")];
        var sample = new AgentSidebarSample(installation, generation, stamp, true, false,
            [new("workflow:first", "lead", false), new("workflow:second", "lead", false), new("legacy-builder", "builder", true)]);
        IReadOnlyList<AgentSidebarEntry> Project(AgentSidebarSample value, bool connected = true) =>
            AgentSidebarProjection.Project(value, installation, profiles, connected);
        AgentSidebarEntry Entry(IReadOnlyList<AgentSidebarEntry> value, string id) => value.Single(row => row.AgentId == id);
        var projected = Project(sample);
        Check(Entry(projected, "lead") is { State: AgentSidebarState.Active, OwnedContexts: 2 },
            "Two independent actual local ownership contexts stay one accepted agent with the actual context count.");
        Check(Entry(projected, "builder") is { State: AgentSidebarState.Stopping, OwnedContexts: 1 },
            "Cancellation preserves its actual ownership until release and does not pulse as active work.");
        Check(Entry(projected, "compactor") is { State: AgentSidebarState.Idle, OwnedContexts: 0 },
            "Complete local absence establishes only sampled local idle.");
        Check(Entry(projected, "remote") is { State: AgentSidebarState.Unknown, OwnedContexts: null },
            "Configured remote presence never becomes active or a global remote zero.");
        var partial = Project(sample with { Partial = true });
        Check(Entry(partial, "lead").State == AgentSidebarState.Active && Entry(partial, "lead").Text.Contains("≥2", StringComparison.Ordinal),
            "A partial sample preserves exact positive ownership as an explicit lower bound.");
        Check(Entry(partial, "compactor") is { State: AgentSidebarState.Unknown, OwnedContexts: null },
            "Absence in a partial sample cannot prove idle or zero contexts.");
        Check(Project(sample with { IssuingInstallationId = otherInstallation }).All(row => row.State == AgentSidebarState.Unknown),
            "Another issuing installation cannot enter this owner's agent display.");
        Check(Project(sample, connected: false).All(row => row.State == AgentSidebarState.Disconnected && row.OwnedContexts is null),
            "Disconnected state comes from connection state and clears all activity counts.");
        Check(Project(AgentSidebarProjection.Unavailable(installation)).All(row => row.State == AgentSidebarState.Unknown),
            "A failed or unsupported metric read is unknown and never agent failure.");
        Check(Project(sample with { Ownership = [sample.Ownership[0], sample.Ownership[0]] }).All(row => row.State == AgentSidebarState.Unknown),
            "Duplicate execution identities cannot manufacture simultaneous owned contexts.");
        var retired = Project(sample with { Ownership = [new("workflow:accepted-old-revision", "retired-agent", false)] });
        Check(Entry(retired, "retired-agent") is { State: AgentSidebarState.Active, Name: "retired-agent", OwnedContexts: 1 },
            "An actually accepted agent remains identifiable even when its current configured profile is absent.");
        Check(Entry(projected, "lead").Details.Contains("slot wait and cleanup", StringComparison.Ordinal) &&
            Entry(projected, "remote").Details.Contains("Remote activity remains unobserved", StringComparison.Ordinal),
            "Accessible text explains the observation scope and remote uncertainty.");
        foreach (var (state, expected) in new[] { (AgentSidebarCallerState.Waiting, AgentSidebarState.Waiting),
            (AgentSidebarCallerState.Failed, AgentSidebarState.Failed), (AgentSidebarCallerState.Completed, AgentSidebarState.Completed) })
        {
            var caller = new AgentSidebarCallerProof(installation, "lead", "workflow:first", state);
            var lifecycle = sample with { Ownership = [sample.Ownership[0]], Callers = [caller] };
            Check(Entry(Project(lifecycle), "lead").State == expected,
                "An explicit exact accepted caller proof can represent " + expected + " without inferring an outcome from release.");
            Check(Entry(Project(lifecycle with { Callers = [caller with { IssuingInstallationId = otherInstallation }] }), "lead").State == AgentSidebarState.Active,
                "A caller proof from another installation cannot override this observed local context.");
        }
        Check(Entry(Project(sample with { Ownership = [] }), "lead").State == AgentSidebarState.Idle,
            "Ownership release alone is local idle, never fabricated completion or failure.");

        JsonElement Row(string execution, string? agent, string kind = "workflow-execution", string? owner = installation,
            string? worker = "local", string projection = "observed", string scheduler = installation) => CerebralJson.Element(new
            { executionId = execution, kind, schedulerOwnerInstallationId = scheduler, executionOwnerInstallationId = owner,
                executionWorkerId = worker, agentId = agent, cancellationRequested = false, projectionState = projection,
                acceptedModelRoutes = new[] { new { agentId = "compactor", modelId = "loaded-model", purpose = "compactor" } } });
        JsonElement Envelope(JsonElement[] rows, int contexts, int agents, bool incomplete = false, int unprojectable = 0,
            int drift = 0, string issuer = installation) => CerebralJson.Element(new
            { schema = "cerebral.execution-metrics/1", issuingInstallationId = issuer, processGenerationId = generation,
                processGenerationScope = "issuing-worker-scheduler-instance; not ModelRuntime", sampledAt = stamp,
                state = incomplete ? "partial" : "observed", partial = incomplete, truncated = incomplete && unprojectable == 0,
                maximumObservations = 256, capturedOwnershipCount = rows.Length, unprojectableCount = unprojectable,
                ownershipChangedCount = drift, observedLocalExecutionCount = contexts, observedLocalWorkingAgentCount = agents,
                localWorkingAgentCount = incomplete ? (int?)null : agents, confirmedRemoteWorkingAgentCount = (int?)null,
                observations = rows });
        var accepted = Envelope([Row("workflow:first", "lead"), Row("workflow:second", "lead"),
            Row("maintenance", "compactor", kind: "maintenance"), Row("remote-dispatch", "remote", kind: "remote-dispatch-or-reconciliation", owner: null, worker: "peer")], 2, 1);
        var parsed = AgentSidebarProjection.ReadSample(accepted, installation);
        Check(parsed.Available && parsed.Ownership.Count == 2 && parsed.Ownership.All(row => row.AgentId == "lead") && parsed.Callers is null,
            "The real public metric shape confirms only exact local contexts, excluding maintenance, remote dispatch and route agent IDs.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead", scheduler: otherInstallation)], 1, 1), installation).Available,
            "Wrong scheduler-owner rows are rejected.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead", owner: otherInstallation)], 1, 1), installation).Available,
            "Wrong execution-owner rows are rejected.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead", worker: "peer")], 1, 1), installation).Available,
            "A nonlocal execution-worker row cannot create a local active agent.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead"), Row("first", "lead")], 2, 1), installation).Available,
            "Duplicate actual metric context identities are rejected at projection.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead")], 2, 1), installation).Available,
            "Aggregate ownership counts must reconcile with the actual observations.");
        Check(!AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead")], 1, 1, issuer: otherInstallation), installation).Available,
            "The actual envelope issuer is bound to the current installation.");
        var unknownRow = Row("unprojectable", null, kind: "unprojectable", owner: null, worker: null, projection: "unavailable");
        var knownPartial = AgentSidebarProjection.ReadSample(Envelope([Row("first", "lead"), unknownRow], 1, 1, true, 1, 1), installation);
        Check(knownPartial.Available && knownPartial.Partial && knownPartial.Ownership.Count == 1 &&
            Entry(Project(knownPartial), "lead").State == AgentSidebarState.Active && Entry(Project(knownPartial), "builder").State == AgentSidebarState.Unknown,
            "Ownership drift preserves separately confirmed local positives while keeping partial absence unknown.");
        Check(!AgentSidebarProjection.ReadSample(CerebralJson.Element(new { configuredAgents = 8, modelBusy = true }), installation).Available,
            "Configured counts and model Busy cannot be consumed as current accepted-agent observations.");
        Check(!AgentSidebarProjection.ReadSample(CerebralJson.Element(Array.Empty<object>()), installation).Available,
            "A nonobject observation payload is safely unavailable.");
        Check(!AgentSidebarProjection.ReadSample(Envelope(Enumerable.Range(0, 257).Select(index => Row("context-" + index, "lead")).ToArray(), 257, 1), installation).Available,
            "Oversized context samples are rejected rather than partially trusted outside the public 256 observation bound.");
        var emptyPartial = AgentSidebarProjection.ReadSample(Envelope([], 0, 0, incomplete: true), installation);
        Check(emptyPartial.Available && Project(emptyPartial).All(row => row.State == AgentSidebarState.Unknown && row.OwnedContexts is null),
            "An empty truncated sample cannot manufacture idle agents or remote zeroes.");
        Check(AgentSidebarView.MotionPermitted(true, false, false) && !AgentSidebarView.MotionPermitted(false, false, false) &&
            !AgentSidebarView.MotionPermitted(true, true, false) && !AgentSidebarView.MotionPermitted(true, false, true),
            "System animation, high contrast and reduced motion independently govern the marker pulse.");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-agent-sidebar-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "plugins")), root, Path.Combine(root, "drafts"));
        using var currentClient = new CerebralClient();
        using var previousClient = new CerebralClient();
        try
        {
            window.EnterInspectionMode(); // This offscreen fixture must not start automatic connection/monitoring.
            var connectedField = typeof(ConnectedWindow).GetField("<IsConnected>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var clientField = typeof(ConnectedWindow).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((CerebralClient)clientField.GetValue(window)!).Dispose(); clientField.SetValue(window, currentClient);
            connectedField.SetValue(window, true);
            window.systemWorkMetricsClient = currentClient; window.systemWorkMetricsInstallationId = installation;
            window.profiles = profiles.Select(profile => new AgentProfile { Id = profile.AgentId, Name = profile.Name, WorkerId = profile.WorkerId }).ToList();
            var view = window.agentSidebarView ?? throw new InvalidOperationException("Sidebar factory hook was not mounted.");
            if (view.Parent is Panel parent) parent.Children.Remove(view);
            view.VerticalAlignment = VerticalAlignment.Top;
            view.MotionPolicyForChecks = () => true;
            var frame = new Border { Background = Ui.Surface, Padding = new Thickness(16), Child = view };
            using var source = new HwndSource(new HwndSourceParameters("Cerebral accepted agent sidebar fixture")
                { Width = 290, Height = 360, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = frame };
            async Task Flush()
            {
                await view.Dispatcher.InvokeAsync(() => { frame.Measure(new Size(290, 360)); frame.Arrange(new Rect(0, 0, 290, 360)); frame.UpdateLayout(); }, DispatcherPriority.Loaded);
                await view.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            TextBlock Marker(string id) => Descendants(view).OfType<TextBlock>().Single(item => AutomationProperties.GetAutomationId(item) == "AgentSidebarMarker-" + id);
            TextBlock State(string id) => Descendants(view).OfType<TextBlock>().Single(item => AutomationProperties.GetAutomationId(item) == "AgentSidebarState-" + id);
            static bool Animated(TextBlock dot) => dot.HasAnimatedProperties;
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            var dot = Marker("lead");
            Check(dot.Text == "●" && Animated(dot) && State("lead").Text == "Active · 2 local contexts",
                "The native matched active agent has a small pulsing solid marker and actual independent context count.");
            Check(Marker("builder").Text == "■" && !Animated(Marker("builder")) && State("builder").Text.StartsWith("Stopping", StringComparison.Ordinal),
                "A cancelling owned agent uses an informative static stopping marker.");
            Check(Marker("remote").Text == "?" && !Animated(Marker("remote")) && State("remote").Text == "Unknown",
                "An unobserved remote agent remains explicit unknown in the actual native view.");
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            Check(ReferenceEquals(dot, Marker("lead")) && Animated(dot), "Repeated samples retain the same native marker and clock ownership without rebuilding rows.");
            window.ApplyAgentSidebarSnapshot(previousClient, installation, AgentSidebarProjection.Unavailable(installation)); await Flush();
            Check(ReferenceEquals(window.agentSidebarSample, sample) && Animated(dot), "An old client response cannot overwrite the current owner's display even with the same installation ID.");
            window.ApplyAgentSidebarSnapshot(currentClient, otherInstallation, AgentSidebarProjection.Unavailable(otherInstallation)); await Flush();
            Check(ReferenceEquals(window.agentSidebarSample, sample) && Animated(dot), "A different installation cannot overwrite the bound current sample.");
            view.MotionPolicyForChecks = () => false; view.RefreshMotion();
            Check(!Animated(dot) && dot.Text == "●" && State("lead").Text == "Active · 2 local contexts",
                "Disabled motion retains a readable exact agent state and solid marker.");
            view.MotionPolicyForChecks = () => true; view.RefreshMotion();
            Check(Animated(dot), "Reenabled motion restarts only the confirmed native active marker.");
            if (previewPath is not null)
            {
                var bitmap = new RenderTargetBitmap(290, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); encoder.Save(file);
            }
            frame.Visibility = Visibility.Collapsed; await Flush();
            Check(!Animated(dot) && window.agentSidebarSample is null, "A hidden sidebar drops its sample and does not retain an invisible pulse clock.");
            frame.Visibility = Visibility.Visible; await Flush();
            Check(!Animated(dot) && State("lead").Text == "Unknown", "Showing a sidebar stays unknown until a current observation replaces the hidden sample.");
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            Check(Animated(dot) && ReferenceEquals(dot, Marker("lead")), "A current visible sample resumes the marker without replacing the row.");
            connectedField.SetValue(window, false); window.AgentSidebarConnectionChanged(window, EventArgs.Empty); await Flush();
            Check(!Animated(dot) && State("lead").Text == "Disconnected" && window.agentSidebarSample is null,
                "Disconnect clears the sample immediately and marks the actual controls disconnected.");
            connectedField.SetValue(window, true); window.systemWorkMetricsClient = previousClient;
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            Check(State("lead").Text != "Active · 2 local contexts" && !Animated(dot), "The captured current client must also match the metrics incarnation before publication.");
            window.systemWorkMetricsClient = currentClient;
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            Check(Animated(dot), "A new current bound sample can start the active marker after reconnect.");
            frame.Child = null; await Flush();
            Check(!Animated(dot) && window.agentSidebarSample is null, "Unload removes the sample and clocks so reload cannot reuse a stale ownership observation.");
            frame.Child = view; await Flush();
            Check(!Animated(dot) && State("lead").Text == "Unknown", "Reload stays unknown until an actual new owner-bound sample arrives.");
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            Check(Animated(dot), "A fresh postreload observation can start the same active row.");
            window.ResetAgentSidebarSnapshot(); await Flush();
            Check(!Animated(dot) && State("lead").Text == "Unknown", "An explicit polling/feed reset clears active evidence rather than retaining a pulse.");

            // Exercise the real client/query/refresh path through a unit transport handler. No socket is opened.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executionResult = CommandResult.Ok(accepted);
            var holdExecution = true;
            using var handler = new AgentSidebarMetricsHandler(async (request, token) =>
            {
                if (request.Operation == "metrics.execution.status")
                {
                    entered.TrySetResult();
                    if (holdExecution) await release.Task.WaitAsync(token);
                    return executionResult;
                }
                return CommandResult.Fail(request.Operation == "metrics.slots.status"
                    ? "Unknown command: metrics.slots.status" : "Deterministic resource observation failure.");
            });
            var httpField = typeof(CerebralClient).GetField("http", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)httpField.GetValue(currentClient)!).Dispose();
            httpField.SetValue(currentClient, new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("http://agent-sidebar-fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan });
            var hiddenStatusRefresh = window.RefreshSystemStatusAsync(statusVisible: false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await window.RefreshSystemStatusAsync(statusVisible: false);
            Check(handler.Count("metrics.execution.status") == 1 && handler.Count("metrics.status") == 0 &&
                handler.Count("metrics.slots.status") == 0 && window.systemStatusRefreshActive,
                "A visible sidebar with a hidden status bar uses exactly one execution query and no resource/slot query; a second tick cannot duplicate it.");
            release.TrySetResult(); await hiddenStatusRefresh; await Flush();
            Check(Animated(dot) && State("lead").Text == "Active · 2 local contexts" && !window.systemStatusRefreshActive,
                "The actual hidden-status refresh publishes its accepted owner-bound sample into the native sidebar.");
            release = new(TaskCreationOptions.RunContinuationsAsynchronously); entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedResourcesRefresh = window.RefreshSystemStatusAsync(statusVisible: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await window.RefreshSystemStatusAsync(statusVisible: true);
            Check(handler.Count("metrics.execution.status") == 2 && handler.Count("metrics.status") == 1 && window.systemStatusRefreshActive,
                "A resource failure still observes its one concurrent execution query before allowing another refresh.");
            release.TrySetResult(); await failedResourcesRefresh; await Flush();
            Check(Animated(dot) && State("lead").Text == "Active · 2 local contexts" && !window.systemStatusRefreshActive,
                "A failed metrics.status request cannot suppress a successful actual execution observation.");
            holdExecution = false; executionResult = CommandResult.Fail("Unknown command: metrics.execution.status");
            await window.RefreshSystemStatusAsync(statusVisible: false); await Flush();
            var unsupportedQueries = handler.Count("metrics.execution.status");
            await window.RefreshSystemStatusAsync(statusVisible: false); await Flush();
            Check(window.systemWorkMetricsUnsupported && !Animated(dot) && State("lead").Text == "Unknown" &&
                handler.Count("metrics.execution.status") == unsupportedQueries && !window.systemSlotMetricsUnsupported,
                "Only exact unsupported execution discovery is cached and clears stale sidebar activity without poisoning the slot cache.");
            await window.ReadSystemSlotMetricsAsync(currentClient, installation);
            Check(window.systemSlotMetricsUnsupported && window.systemWorkMetricsUnsupported,
                "Unsupported slot discovery has its own independent cache.");
            window.ResetSystemWorkMetrics(new("Cerebral", "fixture", 1, "Fixture", "fixture", "fixture", false, root, InstallationId: installation));
            Check(!window.systemWorkMetricsUnsupported && !window.systemSlotMetricsUnsupported && window.agentSidebarSample is null,
                "A new captured metrics binding resets both discovery caches and the old agent display.");
            executionResult = CommandResult.Fail("Deterministic denied execution observation.");
            await window.RefreshSystemStatusAsync(statusVisible: false); await Flush();
            Check(!window.systemWorkMetricsUnsupported && !Animated(dot) && State("lead").Text == "Unknown",
                "An ordinary execution observation failure clears activity but is not cached as unsupported or agent failure.");
            executionResult = CommandResult.Ok(new { configuredAgentCount = 4, modelBusy = true });
            await window.RefreshSystemStatusAsync(statusVisible: false); await Flush();
            Check(!Animated(dot) && State("lead").Text == "Unknown", "A malformed successful execution response cannot retain stale active state.");
            executionResult = CommandResult.Ok(accepted); holdExecution = true;
            release = new(TaskCreationOptions.RunContinuationsAsynchronously); entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleRefresh = window.RefreshSystemStatusAsync(statusVisible: false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clientField.SetValue(window, previousClient); window.systemWorkMetricsClient = previousClient; window.ResetAgentSidebarSnapshot();
            release.TrySetResult(); await staleRefresh; await Flush();
            Check(!Animated(dot) && State("lead").Text == "Unknown" && window.agentSidebarSample is null,
                "A response arriving after a same-installation client replacement cannot publish its old ownership sample.");
            clientField.SetValue(window, currentClient); window.systemWorkMetricsClient = currentClient;
            window.ApplyAgentSidebarSnapshot(currentClient, installation, sample); await Flush();
            window.Close();
            Check(!Animated(dot), "Closing the owning window disposes native marker clocks and subscriptions.");
            source.RootVisual = null; frame.Child = null;
        }
        finally
        {
            window.Close();
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-agent-sidebar-fixture-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid sidebar fixture cleanup root.");
            Directory.Delete(resolved, true);
        }
        return checks;

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private sealed class AgentSidebarMetricsHandler(Func<CommandRequest, CancellationToken, Task<CommandResult>> respond) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> calls = new(StringComparer.Ordinal);
        internal int Count(string operation) => calls.GetValueOrDefault(operation);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var command = JsonSerializer.Deserialize<CommandRequest>(await request.Content!.ReadAsStringAsync(token), CerebralJson.Options)
                ?? throw new InvalidOperationException("Missing deterministic command fixture.");
            calls.AddOrUpdate(command.Operation, 1, (_, count) => count + 1);
            var result = await respond(command, token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, CerebralJson.Options), Encoding.UTF8, "application/json") };
        }
    }
}
