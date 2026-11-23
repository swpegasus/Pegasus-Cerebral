using System.Windows.Controls;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private async Task<int> CheckComputeProjectionRacesAsync(CancellationToken token)
    {
        var checks = 0;
        static IEnumerable<T> Rows<T>(DataGrid grid) => grid.ItemsSource as IEnumerable<T>
            ?? throw new InvalidOperationException("The expected projection rows were not adopted.");
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var priorSettings = settings; var priorEpoch = queueConnectionEpoch;
        var priorLoaded = loadedModelWorkerId; var priorModels = configuredModels;
        var priorRefreshVersion = modelLibraryRefreshVersion;
        var priorProfileData = computeProfileData;
        var priorQueueOverride = queueCommandOverride;
        var priorRegistrationOverride = registrationCommandOverride;
        try
        {
            Check(!IsConnected, "Projection race checks must not connect to a Worker.");
            queueConnectionEpoch = 310;
            settings = new HostSettings { Name = "Owner A", DefaultModelId = "shared", DefaultModelWorkerId = "local" };
            loadedModelWorkerId = "local";
            EnsureComputeConnection(); RefreshModelWorkerChoices();
            ModelRecord Model(string name, int slots) => new() { Id = "shared", Name = name, ParallelRequests = slots };
            AgentProfile Profile(string id) => new() { Id = id, Name = id, WorkerId = "local", FollowsDefaultModel = true };
            var model = Model("Owner A model", 2);
            RememberComputeLibrary(310, "local", [model]);
            AdoptComputeProfiles(310, [Profile("prior-owner-profile")]);
            Check(Rows<ModelRow>(models).Single().AssignedProfiles == "1",
                "The initial current-owner profile snapshot must populate model assignment counts.");

            // Reconnect retires previous-owner rows before even the first settings reply.
            var pendingSettings = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            registrationCommandOverride = (_, _) => pendingSettings.Task;
            queueConnectionEpoch = 311;
            var reconnect = RefreshAll();
            Check(CurrentComputeProfiles is null && agents.ItemsSource is null && models.ItemsSource is null,
                "The full reconnect refresh must retire prior-owner rows before its initial settings await.");
            pendingSettings.SetResult(CommandResult.Fail("Fixture stopped before other commands."));
            var refreshStopped = false;
            try { await reconnect.WaitAsync(token); }
            catch (InvalidOperationException error) when (error.Message == "Fixture stopped before other commands.") { refreshStopped = true; }
            Check(refreshStopped, "The bounded reconnect fixture must terminate before any subsequent command.");
            registrationCommandOverride = priorRegistrationOverride;
            settings = new HostSettings { Name = "Owner B", DefaultModelId = "shared", DefaultModelWorkerId = "local" };
            model = Model("Owner B model", 4);
            RememberComputeLibrary(311, "local", [model]);
            Check(CurrentComputeProfiles is null && !Rows<AgentRow>(agents).Any() &&
                !Rows<AgentRow>(computeProfiles).Any(),
                "Catalog-first reconnect must discard prior-owner profiles from both profile panes.");
            Check(Rows<ModelRow>(models).Single().AssignedProfiles == "Not observed" &&
                Rows<ComputeModelRow>(computeModels).Single().Profiles == "Not observed" &&
                Rows<ComputeMachineRow>(computeMachines).Single().Profiles == "Not observed",
                "Before a current-owner profile read, assignment counts must be unknown rather than zero or prior-owner counts.");
            Check(!AdoptComputeProfiles(310, [Profile("late-owner-a")]) && CurrentComputeProfiles is null,
                "Late profile bodies from another owner must not hydrate the new owner's projection.");
            AdoptComputeProfiles(311, [Profile("owner-b-one"), Profile("owner-b-two")]);
            Check(Rows<ModelRow>(models).Single().AssignedProfiles == "2" &&
                Rows<ComputeModelRow>(computeModels).Single().Profiles == "2",
                "A current-owner profile adoption must immediately rebuild library and machine assignment counts.");
            model = Model("Owner B renamed model", 5);
            RememberComputeLibrary(311, "local", [model]);
            Check(Rows<AgentRow>(agents).All(row => row.Model == model.Name && row.ConfiguredSlots == "5") &&
                Rows<AgentRow>(computeProfiles).All(row => row.Model == model.Name && row.ConfiguredSlots == "5"),
                "Catalog refresh must immediately rebuild model names and configured-slot strings in both profile panes.");
            var profileCalls = 0;
            Task<CommandResult> SavedProfiles(CommandRequest request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); profileCalls++;
                Check(request.Operation == "agents.list", "The acknowledged refresh must retain its existing profile read operation.");
                return Task.FromResult(CommandResult.Ok(new[] { Profile("acknowledged-profile") }));
            }
            await RefreshAgentRowsOnConnectionAsync(311, SavedProfiles, token);
            Check(profileCalls == 1 && Rows<ModelRow>(models).Single().AssignedProfiles == "1" &&
                CurrentComputeProfiles?.Single().Id == "acknowledged-profile",
                "The existing guarded post-ack profile read must adopt the owner snapshot and rebuild dependent counts.");
            await RefreshAgentRowsOnConnectionAsync(311, (_, cancellation) => {
                cancellation.ThrowIfCancellationRequested(); return Task.FromResult(CommandResult.Ok(Array.Empty<AgentProfile>()));
            }, token);
            Check(CurrentComputeProfiles is { Count: 0 } &&
                Rows<ModelRow>(models).Single().AssignedProfiles == "0" &&
                Rows<ComputeModelRow>(computeModels).Single().Profiles == "0",
                "A verified empty profile result must establish zero assignments without becoming an unknown read.");
            AdoptComputeProfiles(311, [Profile("current-b")]);
            var pendingProfiles = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateProfiles = RefreshAgentRowsOnConnectionAsync(311, (_, _) => pendingProfiles.Task, token);
            queueConnectionEpoch = 312;
            settings = new HostSettings { Name = "Owner C", DefaultModelId = "shared", DefaultModelWorkerId = "local" };
            model = Model("Owner C model", 2);
            RememberComputeLibrary(312, "local", [model]); AdoptComputeProfiles(312, [Profile("current-c")]);
            pendingProfiles.SetResult(CommandResult.Ok(new[] { Profile("late-b") }));
            await lateProfiles.WaitAsync(token);
            Check(CurrentComputeProfiles?.Single().Id == "current-c" &&
                Rows<AgentRow>(agents).Single().Id == "current-c",
                "The original post-ack owner guard must preserve newly adopted owner rows after a delayed completion.");

            static System.Text.Json.JsonElement Diagnostic(int slots) => CerebralJson.Element(new { models = new[] {
                new { id = "shared", availableSlots = slots, busy = 1, quarantinedSlots = new Dictionary<string, string>() } } });
            var capture = CaptureComputeDiagnostics("local");
            var originalPin = capture.ModelPins["shared"];
            var pending = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnosticCalls = 0;
            var read = ReadComputeDiagnosticsAsync(capture, (request, cancellation) => {
                cancellation.ThrowIfCancellationRequested(); diagnosticCalls++;
                Check(request.Operation == "diagnostics", "The captured local diagnostic read must retain its exact route.");
                return pending.Task;
            }, token);
            configuredModels[0].ParallelRequests = 7;
            Check(capture.ModelPins["shared"] == originalPin,
                "Mutating active DTOs during a read must not mutate the pre-dispatch configuration pins.");
            pending.SetResult(CommandResult.Ok(Diagnostic(8)));
            Check(await read.WaitAsync(token) is null && diagnosticCalls == 1 && !computeRuntime.ContainsKey(("local", "shared")),
                "A same-owner/same-machine configuration drift must reject the old reply before allocation adoption.");
            model = Model("Owner C changed model", 7);
            RememberComputeLibrary(312, "local", [model]);
            capture = CaptureComputeDiagnostics("local");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            read = ReadComputeDiagnosticsAsync(capture, (_, _) => pending.Task, token);
            RememberComputeLibrary(312, "local", [model]);
            var currentCapture = CaptureComputeDiagnostics("local");
            RememberComputeDiagnostics(currentCapture, Diagnostic(1), DateTimeOffset.UtcNow);
            pending.SetResult(CommandResult.Ok(Diagnostic(8)));
            Check(await read.WaitAsync(token) is null && computeRuntime[("local", "shared")].AvailableSlots == 1,
                "A replaced catalog generation with identical pins must reject the older reply and retain the newer observation.");
            capture = CaptureComputeDiagnostics("local");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            read = ReadComputeDiagnosticsAsync(capture, (_, _) => pending.Task, token);
            modelLibraryRefreshVersion++;
            pending.SetResult(CommandResult.Ok(Diagnostic(9)));
            Check(await read.WaitAsync(token) is null && computeRuntime[("local", "shared")].AvailableSlots == 1,
                "A refresh generation change without catalog adoption must reject an older diagnostic completion.");
            capture = CaptureComputeDiagnostics("local");
            var sends = 0; modelLibraryRefreshVersion++;
            var notSent = await ReadComputeDiagnosticsAsync(capture, (_, _) => {
                sends++; return Task.FromResult(CommandResult.Ok(Diagnostic(9)));
            }, token);
            Check(notSent is null && sends == 0, "A capture already stale before dispatch must perform no diagnostic command.");
            capture = CaptureComputeDiagnostics("local");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            read = ReadComputeDiagnosticsAsync(capture, (_, _) => pending.Task, token);
            queueConnectionEpoch = 313;
            RememberComputeLibrary(313, "local", [Model("Owner D model", 3)]);
            AdoptComputeProfiles(313, [Profile("current-d")]);
            RememberComputeDiagnostics(CaptureComputeDiagnostics("local"), Diagnostic(2), DateTimeOffset.UtcNow);
            pending.SetResult(CommandResult.Ok(Diagnostic(9)));
            Check(await read.WaitAsync(token) is null && computeRuntime[("local", "shared")].AvailableSlots == 2,
                "An old-owner diagnostic completion must neither replace nor erase a newer owner's observation.");

            // Exercise the real caller as well as the adoption helper; stale cases never open its modal text window.
            var callerProfiles = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerDiagnosticSends = 0;
            queueCommandOverride = (request, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (request.Operation == "agents.list") return callerProfiles.Task;
                callerDiagnosticSends++; return Task.FromResult(CommandResult.Ok(Diagnostic(9)));
            };
            model = CerebralJson.Read<ModelRecord>(CerebralJson.Element(computeLibraries["local"].Models.Single()));
            var caller = ShowModelDetails(model);
            RememberComputeLibrary(313, "local", [Model("Owner D first-await change", 4)]);
            callerProfiles.SetResult(CommandResult.Ok(new[] { Profile("current-d") }));
            var callerRejected = false;
            try { await caller.WaitAsync(token); }
            catch (InvalidOperationException error) when (error.Message.Contains("configuration or connection changed", StringComparison.Ordinal)) { callerRejected = true; }
            Check(callerRejected && callerDiagnosticSends == 0,
                "The actual details caller must capture before its profile await and refuse a stale diagnostic dispatch.");

            var callerDiagnostics = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            queueCommandOverride = (request, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                return request.Operation == "agents.list" ? Task.FromResult(CommandResult.Ok(new[] { Profile("current-d") }))
                    : callerDiagnostics.Task;
            };
            model = CerebralJson.Read<ModelRecord>(CerebralJson.Element(computeLibraries["local"].Models.Single()));
            caller = ShowModelDetails(model);
            RememberComputeLibrary(313, "local", [Model("Owner D diagnostic-await change", 5)]);
            RememberComputeDiagnostics(CaptureComputeDiagnostics("local"), Diagnostic(1), DateTimeOffset.UtcNow);
            callerDiagnostics.SetResult(CommandResult.Ok(Diagnostic(9)));
            callerRejected = false;
            try { await caller.WaitAsync(token); }
            catch (InvalidOperationException error) when (error.Message.Contains("configuration or connection changed", StringComparison.Ordinal)) { callerRejected = true; }
            Check(callerRejected && computeRuntime[("local", "shared")].AvailableSlots == 1,
                "The actual details caller must reject configuration drift during diagnostics without pinning it to the newer model or erasing its sample.");

            var mixed = ComputeMemoryPool(new ResourceInfo(0, 98_304, [
                new GpuInfo("Unified one", 131_072, 98_304, true),
                new GpuInfo("Unified alias", 131_072, 98_304, true),
                new GpuInfo("Discrete A", 16_384, 12_288, false),
                new GpuInfo("Discrete B", 8_192, 0, false)
            ]) { SystemTotalMb = 131_072 });
            Check(mixed.Split("Shared CPU/GPU:", StringSplitOptions.None).Length == 2 &&
                mixed.Contains("Discrete A VRAM:", StringComparison.Ordinal) && mixed.Contains("Discrete B VRAM:", StringComparison.Ordinal),
                "Mixed memory must emit one shared pool plus every non-unified GPU pool.");
            Check(!mixed.StartsWith("RAM:", StringComparison.Ordinal) && !mixed.Contains(" / RAM:", StringComparison.Ordinal) && mixed.Contains("0 MiB", StringComparison.Ordinal),
                "The shared physical pool must not be counted again as RAM and a measured discrete zero must remain visible.");
        }
        finally
        {
            queueCommandOverride = priorQueueOverride; registrationCommandOverride = priorRegistrationOverride;
            settings = priorSettings; queueConnectionEpoch = priorEpoch; loadedModelWorkerId = priorLoaded;
            modelLibraryRefreshVersion = priorRefreshVersion; computeConnection = -1;
            EnsureComputeConnection(); configuredModels = priorModels; computeProfileData = priorProfileData;
            RefreshComputeOverview();
        }
        return checks;
    }
}
