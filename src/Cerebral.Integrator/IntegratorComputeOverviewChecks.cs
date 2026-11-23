using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private int CheckComputeOverview()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var priorSettings = settings; var priorRows = agents.ItemsSource; var priorEpoch = queueConnectionEpoch;
        var priorLoaded = loadedModelWorkerId; var priorModels = configuredModels; var priorTheme = Cerebral.Desktop.Ui.AppearanceTheme; var priorStatus = Status.Text;
        try
        {
            Check(!IsConnected, "Compute checks must remain disconnected from all Workers.");
            queueConnectionEpoch = 80;
            settings = new HostSettings { Name = "Local fixture", DefaultModelId = "same-id", DefaultModelWorkerId = "gb10", MaxConcurrentJobs = 2 };
            RefreshComputeOverview();
            var machineRows = ((IEnumerable<ComputeMachineRow>)computeMachines.ItemsSource).ToArray();
            Check(machineRows.Length == 1 && machineRows[0].Id == "local", "Local must appear once even with no remote registrations.");
            Check(workers.ItemsSource is not IEnumerable<ComputeMachineRow>, "Synthetic local rows must never enter remote registration actions.");
            settings.Workers = [
                new WorkerRegistration { Id = "gb10", Name = "Repeated display name", Url = "http://fixture-a:2668", AccessKey = "fixture-only" },
                new WorkerRegistration { Id = "other", Name = "Repeated display name", Url = "http://fixture-b:2668", RequiresConfiguration = true }
            ];
            var local = new ModelRecord { Id = "same-id", Name = "Repeated model name", ParallelRequests = 3, State = "loaded" };
            var remote = new ModelRecord { Id = "same-id", Name = "Repeated model name", ParallelRequests = 4, State = "loaded" };
            Check(RememberComputeLibrary(80, "local", [local]) && RememberComputeLibrary(80, "gb10", [remote]),
                "Catalogs must be accepted for the original owner and exact machine.");
            local.ParallelRequests = 9;
            Check(computeLibraries["local"].Models[0].ParallelRequests == 3, "A caller must not mutate the cached model configuration after adoption.");
            configuredModels = []; loadedModelWorkerId = "local";
            var profiles = new[] {
                new AgentProfile { Id = "lead", Name = "Lead", Role = "lead", WorkerId = "local", FollowsDefaultModel = true },
                new AgentProfile { Id = "helper", Name = "Helper", WorkerId = "gb10", ModelId = "same-id", ModelWorkerId = "local" },
                new AgentProfile { Id = "00-engine-check-alpha", Name = "Historical alpha", WorkerId = "local", ModelId = "same-id", ModelWorkerId = "gb10" },
                new AgentProfile { Id = "00-engine-check-beta", Name = "Historical beta", WorkerId = "local", ModelId = "same-id", ModelWorkerId = "gb10" }
            };
            AdoptComputeProfiles(80, profiles);
            RefreshComputeOverview();
            machineRows = ((IEnumerable<ComputeMachineRow>)computeMachines.ItemsSource).ToArray();
            Check(machineRows.Select(row => row.Id).SequenceEqual(["local", "gb10", "other"]), "Repeated names must preserve distinct stable machine identities.");
            Check(machineRows.Single(row => row.Id == "gb10").Availability.Contains("unobserved", StringComparison.Ordinal) &&
                machineRows.Single(row => row.Id == "other").Availability == "Needs setup", "Registration must not be presented as measured reachability.");
            computeMachines.SelectedItem = machineRows.Single(row => row.Id == "local");
            var localRow = ((IEnumerable<ComputeModelRow>)computeModels.ItemsSource).Single();
            Check(localRow.ConfiguredSlots == "3" && localRow.MeasuredMaximum == "Not measured" && localRow.ObservedSlots == "Not observed",
                "A saved loaded state and configured slots must not become measured capability or loaded readiness.");
            Check(localRow.Profiles == "1" && localRow.WorkingContexts == "Not observed", "Assigned profiles must not become working contexts.");
            Check(computeMachineSummary.Text.Contains($"Configured job ceiling: {settings.MaxConcurrentJobs:N0}.", StringComparison.Ordinal) &&
                computeMachineSummary.Text.Contains("Runtime allocation has not been observed.", StringComparison.Ordinal) &&
                computeMachineSummary.Text.Contains("Configured slots belong to a model; job limits belong to a machine.", StringComparison.Ordinal),
                "The configured machine job ceiling must remain distinct from unobserved runtime and per-model slots.");
            computeMachines.SelectedItem = machineRows.Single(row => row.Id == "gb10");
            var remoteRow = ((IEnumerable<ComputeModelRow>)computeModels.ItemsSource).Single();
            Check(remoteRow.ConfiguredSlots == "4" && remoteRow.Profiles == "3", "The same model ID on another machine must use that machine's configuration and assignments.");
            var projectedProfiles = ((IEnumerable<AgentRow>)computeProfiles.ItemsSource).ToArray();
            Check(projectedProfiles.Single(row => row.Id == "lead").ExecutionMachine == "this machine" &&
                projectedProfiles.Single(row => row.Id == "lead").InferenceMachine == "Repeated display name" &&
                projectedProfiles.Single(row => row.Id == "lead").Binding == "Follows default",
                "Execution, inference and default binding must remain separately visible.");
            Check(projectedProfiles.Any(row => row.Id == "00-engine-check-alpha") && projectedProfiles.Any(row => row.Id == "00-engine-check-beta"),
                "Historical independent helper IDs must not be deleted, merged or interpreted as A/B engine slots.");
            var resource = new ResourceInfo(512, 98_304, [new GpuInfo("GB10", 131_072, 98_304, true)])
                { SystemTotalMb = 131_072 };
            var observedAt = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
            var diagnostics = CerebralJson.Element(new { resources = resource, models = new[] {
                new { id = "same-id", availableSlots = 2, busy = 6, quarantinedSlots = new Dictionary<string, string> { ["1"] = "fixture" } } } });
            Check(RememberComputeDiagnostics(CaptureComputeDiagnostics("gb10"), diagnostics, observedAt), "Original-owner diagnostic observations must be accepted.");
            remoteRow = ((IEnumerable<ComputeModelRow>)computeModels.ItemsSource).Single();
            Check(remoteRow.ObservedSlots == "2 available / 1 quarantined" && remoteRow.RuntimeUsers == "6" &&
                remoteRow.MeasuredMaximum == "Not measured" && remoteRow.WorkingContexts == "Not observed",
                "Dictionary-shaped quarantine and busy users must remain allocation facts, not capacity or working-agent counts.");
            Check(computeMachineSummary.Text.Contains("does not prove current readiness", StringComparison.Ordinal),
                "An old observation must retain its timestamp and cannot claim current admission readiness.");
            var memory = ComputeMemoryPool(resource);
            Check(memory.Contains("128.0 GiB", StringComparison.Ordinal) && !memory.Contains("VRAM", StringComparison.Ordinal) &&
                !memory.Contains("256", StringComparison.Ordinal), "Unified RAM and GPU values must display as one physical pool.");
            var discrete = ComputeMemoryPool(new ResourceInfo(0, 0, [new GpuInfo("GPU", 8192, 0, false)]) { SystemTotalMb = 16384 });
            Check(discrete.Contains("RAM:", StringComparison.Ordinal) && discrete.Contains("GPU VRAM:", StringComparison.Ordinal) &&
                discrete.Contains("0 MiB", StringComparison.Ordinal), "Discrete pools must remain separate and measured zero must not become unknown.");
            var malformed = CerebralJson.Element(new { models = new[] { new { id = "same-id", availableSlots = -1, busy = "old-schema", quarantinedSlots = new[] { 1 } } } });
            RememberComputeDiagnostics(CaptureComputeDiagnostics("gb10"), malformed, observedAt);
            remoteRow = ((IEnumerable<ComputeModelRow>)computeModels.ItemsSource).Single();
            Check(remoteRow.ObservedSlots == "? available / ? quarantined" && remoteRow.RuntimeUsers == "?",
                "Malformed or older diagnostics must clear old allocation values and preserve unknowns.");
            RememberComputeDiagnostics(CaptureComputeDiagnostics("gb10"), diagnostics, observedAt);
            remote.ParallelRequests = 5;
            RememberComputeLibrary(80, "gb10", [remote]);
            Check(!computeRuntime.ContainsKey(("gb10", "same-id")), "Changed model configuration must invalidate its older allocation sample.");
            Check(!RememberComputeLibrary(79, "gb10", [new ModelRecord { Id = "stale" }]) &&
                !RememberComputeDiagnostics(CaptureComputeDiagnostics("gb10") with { Connection = 79 }, diagnostics, observedAt), "Late replies from another owner connection must be rejected.");
            var args = Environment.GetCommandLineArgs();
            var previewIndex = Array.IndexOf(args, "--compute-public-preview");
            if (previewIndex >= 0)
            {
                var previewPath = Path.GetFullPath(args.ElementAtOrDefault(previewIndex + 1) ?? throw new ArgumentException("Public preview requires an exact file."));
                if (new FileInfo(previewPath).Length > 65_536) throw new InvalidOperationException("Public preview exceeds its bounded size.");
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(previewPath));
                var root = document.RootElement;
                Check(root.GetProperty("schema").GetString() == "cerebral.core.public-configuration-projection/1" &&
                    root.GetProperty("queryOnly").GetBoolean() && root.GetProperty("productCommandsSent").GetInt32() == 0,
                    "Native previews must use the explicit bounded read-only public configuration projection.");
                if (root.GetProperty("workers").GetArrayLength() > 16 || root.GetProperty("models").GetArrayLength() > 32 ||
                    root.GetProperty("profiles").GetArrayLength() > 64) throw new InvalidOperationException("Public preview exceeds its record bounds.");
                var host = root.GetProperty("host");
                settings = new HostSettings {
                    Name = host.GetProperty("name").GetString()!,
                    MaxConcurrentJobs = host.GetProperty("maxConcurrentJobs").GetInt32(),
                    DefaultModelId = host.GetProperty("defaultModelId").GetString()!,
                    DefaultModelWorkerId = host.GetProperty("defaultModelWorkerId").GetString()!,
                    Workers = root.GetProperty("workers").EnumerateArray().Select(worker => new WorkerRegistration {
                        Id = worker.GetProperty("id").GetString()!, Name = worker.GetProperty("name").GetString()!
                    }).ToList()
                };
                configuredModels = root.GetProperty("models").EnumerateArray().Select(model => new ModelRecord {
                    Id = model.GetProperty("id").GetString()!, Name = model.GetProperty("name").GetString()!,
                    ParallelRequests = model.GetProperty("parallelRequests").GetInt32(),
                    ContextTokens = model.GetProperty("contextTokens").GetInt32(),
                    AutomaticContextSizing = model.GetProperty("automaticContextSizing").GetInt32() == 1,
                    State = model.GetProperty("retainedState").GetString() ?? "unknown"
                }).ToList();
                // This owner DB projects the local model catalog only. Equal IDs are not a remote catalog.
                loadedModelWorkerId = "local";
                computeLibraries.Clear(); computeRuntime.Clear(); computeResources.Clear();
                RememberComputeLibrary(queueConnectionEpoch, "local", configuredModels);
                var snapshotAt = DateTimeOffset.Parse(root.GetProperty("sampledUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                computeLibraries["local"] = computeLibraries["local"] with { ReadAt = snapshotAt };
                Status.Text = $"Read-only Source configuration snapshot · {snapshotAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · runtime unobserved";
                AdoptComputeProfiles(queueConnectionEpoch, root.GetProperty("profiles").EnumerateArray().Select(profile => new AgentProfile {
                    Id = profile.GetProperty("id").GetString()!, Name = profile.GetProperty("name").GetString()!,
                    Role = profile.GetProperty("role").GetString()!, Revision = profile.GetProperty("revision").GetInt32(),
                    WorkerId = profile.GetProperty("executionWorkerId").GetString()!,
                    ModelWorkerId = profile.GetProperty("modelWorkerId").GetString()!, ModelId = profile.GetProperty("modelId").GetString()!,
                    FollowsDefaultModel = profile.GetProperty("followsDefaultModel").GetInt32() == 1,
                    FollowsModelBudgets = profile.GetProperty("followsModelBudgets").GetInt32() == 1,
                    ContextTokens = profile.GetProperty("contextTokens").GetInt32(), ResponseTokens = profile.GetProperty("responseTokens").GetInt32()
                }).ToArray());
                RefreshComputeOverview();
                computeMachines.SelectedItem = ((IEnumerable<ComputeMachineRow>)computeMachines.ItemsSource).Single(machine => machine.Id == "local");
                var publicModels = ((IEnumerable<ComputeModelRow>)computeModels.ItemsSource).ToArray();
                Check(publicModels.All(model => model.ObservedSlots == "Not observed" && model.MeasuredMaximum == "Not measured" &&
                    model.WorkingContexts == "Not observed"), "A real retained configuration preview cannot invent runtime or capacity telemetry.");
            }
            var captureIndex = Array.IndexOf(args, "--compute-flow-captures");
            if (captureIndex >= 0)
            {
                var directory = Path.GetFullPath(args.ElementAtOrDefault(captureIndex + 1) ?? throw new ArgumentException("Compute captures need a task-owned directory."));
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
                Cerebral.Desktop.Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Light);
                SelectTab("Machines");
                var view = (FrameworkElement)Content;
                foreach (var size in new[] { new Size(1024, 768), new Size(1360, 880) })
                {
                    view.Measure(size); view.Arrange(new Rect(new Point(), size)); view.UpdateLayout();
                    Check(computeMachines.ActualHeight >= 120 && computeModels.ActualHeight >= 100,
                        "Both machine and selected-model panes must remain usable in the native viewport.");
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Cerebral.Desktop.Ui.Paper, null, new Rect(new Point(), size));
                    bitmap.Render(background); bitmap.Render(view);
                    Check(computeMachines.Columns.All(column => column.ActualWidth >= 110) &&
                        computeModels.Columns.All(column => column.ActualWidth >= 120),
                        "Native columns must keep readable minimum widths and permit horizontal scrolling instead of compressing labels.");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = new FileStream(Path.Combine(directory, $"machines-{(int)size.Width}.png"), FileMode.CreateNew, FileAccess.Write);
                    encoder.Save(output);
                    if (previewIndex >= 0 && size.Width == 1360)
                    {
                        SelectTab("Agents"); view.Measure(size); view.Arrange(new Rect(new Point(), size)); view.UpdateLayout();
                        var profilesBitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                        profilesBitmap.Render(background); profilesBitmap.Render(view);
                        var profilesEncoder = new PngBitmapEncoder(); profilesEncoder.Frames.Add(BitmapFrame.Create(profilesBitmap));
                        using var profilesOutput = new FileStream(Path.Combine(directory, "profiles-1360.png"), FileMode.CreateNew, FileAccess.Write);
                        profilesEncoder.Save(profilesOutput);
                        SelectTab("Machines");
                    }
                }
            }
            queueConnectionEpoch++;
            RefreshComputeOverview();
            Check(computeLibraries.Count == 0 && computeRuntime.Count == 0 && computeResources.Count == 0 && CurrentComputeProfiles is null,
                "A changed owner must discard all former catalog and runtime observations.");
        }
        finally
        {
            settings = priorSettings; agents.ItemsSource = priorRows; queueConnectionEpoch = priorEpoch;
            configuredModels = priorModels; loadedModelWorkerId = priorLoaded;
            Cerebral.Desktop.Ui.ApplyAppearanceTheme(priorTheme); Status.Text = priorStatus;
            computeConnection = -1; RefreshComputeOverview();
        }
        return checks;
    }
}
