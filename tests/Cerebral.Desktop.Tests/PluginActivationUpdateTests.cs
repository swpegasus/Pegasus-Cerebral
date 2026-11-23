using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

/// <summary>Real private native assemblies/dispatcher/catalog locks; no normal app, Worker,
/// model, provider or network. Each fault lane exits its own child before filesystem cleanup.</summary>
internal static class PluginActivationUpdateTests
{
    private static readonly string[] Cases = ["success", "initialize-failure", "cleanup-failure",
        "restore-load-failure", "stale-selection", "stale-owner", "retention", "cancellation",
        "capacity", "disabled-update", "manual-repair", "archive-initialize-failure", "stale-enablement", "invalid-record", "candidate-background", "configuration-initialize-failure", "configuration-archive-failure"];
    private static readonly List<string> labels = [];
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); labels.Add(label); }

    public static string Run()
    {
        var total = 0;
        var evidence = new List<string>();
        foreach (var lane in Cases)
        {
            var root = Path.Combine(Path.GetTempPath(), "Cerebral.ActivationRecovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--plugin-activation-update-child"); start.ArgumentList.Add(lane); start.ArgumentList.Add(root);
            using var child = Process.Start(start) ?? throw new IOException("The private activation fixture child did not start.");
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            try
            {
                if (!child.WaitForExit(70_000)) throw new TimeoutException("The owned activation child exceeded its bound.");
                var text = output.GetAwaiter().GetResult(); var error = errors.GetAwaiter().GetResult();
                if (child.ExitCode != 0) throw new InvalidOperationException(lane + " child failed: " + text + error);
                using var result = JsonDocument.Parse(text);
                total += result.RootElement.GetProperty("checks").GetInt32();
                evidence.Add(text.Trim());
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5_000); }
                var full = Path.GetFullPath(root);
                if (!child.HasExited || Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                    throw new IOException("The exact activation fixture child/root cleanup boundary could not be verified.");
                CerebralPluginPackage.RejectLinks(full);
                Directory.Delete(full, true);
            }
        }
        return JsonSerializer.Serialize(new { checks = total, cases = evidence.Count, tier = "private-real-native-runtime-and-filesystem-fixtures",
            normalRuntimeChanged = false, results = evidence.Select(text => JsonSerializer.Deserialize<JsonElement>(text)).ToArray() });
    }

    public static void RunChild(string lane, string root)
    {
        var frame = new DispatcherFrame(); Exception? failure = null;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunCase(lane, root); }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) throw new InvalidOperationException("Private native activation fixture failed.", failure);
        Console.WriteLine(JsonSerializer.Serialize(new { lane, checks = labels.Count, labels }));
    }

    private static async Task RunCase(string lane, string root)
    {
        var oldType = lane == "restore-load-failure" ? typeof(ActivationRecoveryFailsPlugin) : typeof(ActivationWorkingPlugin);
        using var fixture = new Fixture(root, oldType, enabled: lane != "disabled-update", configuration: lane.StartsWith("configuration-", StringComparison.Ordinal));
        var prior = fixture.Catalog.List().Single(item => item.Id == Fixture.Id);
        var oldLoaded = fixture.Runtime.GetLoadedPackage(Fixture.Id);
        Check(lane == "disabled-update" ? oldLoaded is null : oldLoaded?.Directory == prior.Directory, "previous generation admitted exactly");
        var candidateType = lane is "initialize-failure" or "restore-load-failure" or "manual-repair" or "archive-initialize-failure" or "configuration-initialize-failure" or "configuration-archive-failure"
            ? typeof(ActivationInitializeFailsPlugin) : lane == "cleanup-failure" ? typeof(ActivationCleanupFailsPlugin) : typeof(ActivationNewPlugin);
        var candidate = fixture.Package("1.0.1", candidateType);
        var beforeData = File.ReadAllText(fixture.OwnerNote);
        var enabledPath = Path.Combine(fixture.Catalog.RootDirectory, ".settings", Fixture.Id + ".json");
        var beforeEnablement = File.ReadAllBytes(enabledPath);
        var owner = true; using var cancellation = new CancellationTokenSource();
        var validateCalls = 0;
        void Validate()
        {
            validateCalls++;
            if (lane == "stale-selection")
            { fixture.Catalog.Update(fixture.Package("1.0.2", typeof(ActivationNewPlugin))); throw new InvalidOperationException("injected later actor"); }
            if (lane == "stale-enablement")
            {
                fixture.Catalog.SetEnabled(Fixture.Id, false); fixture.Catalog.SetEnabled(Fixture.Id, true);
                throw new InvalidOperationException("injected enablement ABA");
            }
            if (lane == "stale-owner") { owner = false; throw new InvalidOperationException("injected owner replacement"); }
            if (lane == "cancellation" && validateCalls == 1) cancellation.Cancel();
            if (lane == "candidate-background")
            {
                _ = fixture.Runtime.ComposerActions.Single(item => item.PluginId == Fixture.Id).ExecuteAsync(CancellationToken.None);
                Check(fixture.WarmupStarted && !fixture.WarmupCompleted, "candidate callback reader remains admitted during acceptance commit");
            }
            if (lane == "retention")
            {
                var marker = Path.Combine(prior.Directory, "retention-marker.txt");
                var locked = false;
                try { using var writer = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { locked = true; }
                Check(locked, "independent previous non-image file handle denies write/delete");
                var cleanup = typeof(DesktopPluginCatalog).GetMethod("TryDeleteInactivePackage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var result = (string?)cleanup.Invoke(fixture.Catalog, [Fixture.Id, prior.Directory]);
                Check(result?.Contains("activation", StringComparison.Ordinal) == true && File.Exists(marker),
                    "ordinary inactive cleanup honors activation recovery retention");
            }
            if (fixture.Runtime.Panels.SingleOrDefault(item => item.PluginId == Fixture.Id)?.View is Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(button.Content?.ToString() == "Counter 1", "actual native contribution remains usable");
            }
        }

        if (lane == "invalid-record")
            File.WriteAllText(fixture.RecordPath, JsonSerializer.Serialize(new {
                SchemaVersion = 999, Operation = Guid.NewGuid().ToString("N"), Id = Fixture.Id,
                PreviousDirectory = Path.GetRelativePath(fixture.Catalog.RootDirectory, prior.Directory),
                PreviousFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prior.ManifestPath),
                State = "incomplete"
            }));
        var reservations = new List<DesktopPluginLoadReservation>();
        try
        {
            if (lane == "capacity")
                for (var i = 0; i < 6; i++) reservations.Add(fixture.Runtime.ReserveForPublication(candidate, true));
            Exception? failure = null; DesktopPluginInfo? result = null;
            try
            {
                if (lane is "archive-initialize-failure" or "configuration-archive-failure")
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(candidate)!, CerebralPluginPackageContract.DefaultSettingsFileName),
                        "{\"value\":\"candidate-preset\"}");
                    var products = new DesktopProductPaths(Path.Combine(root, "install"), Path.Combine(root, "product-data"),
                        Path.Combine(root, "user"), Path.Combine(root, "projects"), Path.Combine(root, "models"),
                        Path.Combine(root, "tools"), Path.Combine(root, "shared"));
                    var archive = Path.Combine(root, "candidate" + CerebralPluginPackageContract.Extension);
                    CerebralPluginPackage.Export(new(Fixture.Id, "Activation fixture", "1.0.1", "", false,
                        Path.GetDirectoryName(candidate)!, candidate, null), archive, "defaults", products, Path.Combine(root, "data"));
                    using var prepared = CerebralPluginPackage.Prepare(archive);
                    result = await prepared.UpdateReviewedAsync(fixture.Catalog, fixture.Runtime, products,
                        Path.Combine(root, "data"), Fixture.Id, null, () => owner, Validate, cancellation.Token);
                }
                else
                    result = await fixture.Runtime.UpdateReviewedAsync(candidate,
                        NativeDesktopPluginPackage.ComputeFingerprint(candidate), false, () => owner, Validate, cancellation.Token);
            }
            catch (Exception error) { failure = error; }
            var recovery = failure switch
            {
                DesktopPluginActivationException error => error.Recovery,
                DesktopPluginActivationCanceledException error => error.Recovery,
                _ => null
            };
            var selection = fixture.Catalog.List().Single(item => item.Id == Fixture.Id);
            if (lane is "success" or "retention" or "candidate-background")
            {
                Check(failure is null && result?.Version == "1.0.1", "higher-version hot update succeeds");
                Check(selection.Directory == fixture.Runtime.GetLoadedPackage(Fixture.Id)?.Directory, "candidate catalog and runtime agree");
                Check(fixture.Count(nameof(ActivationNewPlugin) + ".initialize") == 1, "candidate native activation delivered once");
                Check(fixture.RecordState == "committed", "native acceptance commits recovery boundary");
            }
            else if (lane == "disabled-update")
            {
                Check(failure is null && selection is { Version: "1.0.1", Enabled: false }, "disabled update preserves owner choice");
                Check(fixture.Runtime.GetLoadedPackage(Fixture.Id) is null && validateCalls == 0, "disabled update executes no native candidate");
            }
            else if (lane is "capacity" or "invalid-record")
            {
                Check((lane == "capacity" ? failure is InvalidOperationException : failure is InvalidDataException) && recovery is null,
                    "capacity or invalid recovery format rejects before publication");
                Check(selection.Directory == prior.Directory && fixture.Runtime.GetLoadedPackage(Fixture.Id)?.Directory == prior.Directory,
                    "capacity refusal preserves working selection and instance");
                Check(fixture.Count(nameof(ActivationNewPlugin) + ".initialize") == 0, "capacity refusal performs no candidate native attempt");
            }
            else if (lane is "initialize-failure" or "cancellation" or "manual-repair" or "archive-initialize-failure" or "configuration-initialize-failure" or "configuration-archive-failure")
            {
                Check(recovery is { PreviousSelectionRestored: true, PreviousRuntimeRestored: true, RecoveryError: null },
                    "failed candidate separately confirms previous selection and native recovery");
                Check(failure is not null && result is null && selection.Directory == prior.Directory, "failed update never becomes success");
                Check(fixture.RecordState == "restored", "failed candidate evidence records successful recovery");
                Check(File.Exists(Path.Combine(recovery!.CandidateDirectory, DesktopPluginCatalog.ManifestFileName)), "failed candidate package evidence retained");
                Check(fixture.Runtime.GetLoadedPackage(Fixture.Id)?.Directory == prior.Directory, "previous exact generation usable");
                if (lane == "cancellation")
                    Check(failure is DesktopPluginActivationCanceledException error && error.CancellationToken == cancellation.Token,
                        "post-publication cancellation retains token and recovery outcome");
                else
                    Check(File.Exists(Path.Combine(fixture.Data, "candidate-written-data.txt")), "candidate-written plugin data is preserved");
                if (lane is "archive-initialize-failure" or "configuration-archive-failure")
                    Check(File.ReadAllText(Path.Combine(fixture.Data, CerebralPluginPackageContract.CurrentSettingsFileName))
                        .Contains("candidate-preset", StringComparison.Ordinal), "native compensation preserves committed archive preset");
                if (lane == "manual-repair")
                {
                    fixture.Catalog.SetEnabled(Fixture.Id, false); fixture.Runtime.Disable(Fixture.Id);
                    fixture.Catalog.RemoveWithResult(Fixture.Id);
                    var reinstalled = fixture.Catalog.Install(fixture.Package("1.0.0", typeof(ActivationWorkingPlugin)));
                    Check(!reinstalled.Enabled && !File.Exists(fixture.RecordPath), "explicit fresh owner reinstall clears old recovery blocker");
                    Check(File.ReadAllText(fixture.OwnerNote) == beforeData, "manual repair retains unrelated plugin data");
                }
            }
            else
            {
                Check(recovery is not null && recovery.RecoveryError is not null, "compensation uncertainty is explicit failure evidence");
                if (lane == "restore-load-failure")
                    Check(recovery!.PreviousSelectionRestored && !recovery.PreviousRuntimeRestored &&
                        selection.Directory == prior.Directory, "selection recovery is distinguished from failed native recreation");
                else if (lane == "stale-selection")
                    Check(!recovery!.PreviousSelectionRestored && selection.Version == "1.0.2", "stale candidate recovery preserves later actor selection");
                else if (lane == "stale-enablement")
                    Check(!recovery!.PreviousSelectionRestored && selection is { Version: "1.0.1", Enabled: true },
                        "same-Boolean owner enablement ABA rejects stale compensation");
                else
                    Check(!recovery!.PreviousRuntimeRestored, "unsafe native cleanup or lost owner is not reported restored");
                Check(fixture.RecordState == "incomplete", "unconfirmed recovery remains durable and actionable");
                var starts = fixture.TotalInitializations;
                fixture.Runtime.LoadEnabled();
                Check(fixture.TotalInitializations == starts, "ordinary refresh never replays unresolved native activation");
                using (var ticket = fixture.Runtime.ReserveForPublication(candidate, true))
                    fixture.Runtime.LoadEnabled(ticket);
                Check(fixture.TotalInitializations == starts, "public admission ticket cannot bypass unresolved activation blocker");
                Check(File.Exists(prior.ManifestPath), "uncertain recovery preserves previous package bytes");
                Check(recovery?.CandidateDirectory is { } directory && Directory.Exists(directory), "uncertain recovery preserves candidate package bytes");
            }
            if (lane == "candidate-background")
            {
                fixture.ReleaseWarmup();
                for (var spin = 0; spin < 100 && !fixture.WarmupCompleted; spin++) await Task.Delay(1);
                Check(fixture.WarmupCompleted, "healthy admitted candidate callback completes after successful update");
            }
            Check(File.ReadAllText(fixture.OwnerNote) == beforeData, "owner plugin data retained");
            Check(fixture.ForeignUnchanged(), "unrelated installed package and choice retained");
            if (lane != "manual-repair")
                Check(File.ReadAllBytes(enabledPath).SequenceEqual(beforeEnablement), "exact prior enablement choice retained");
        }
        finally { foreach (var reservation in reservations) reservation.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Id = "activation.fixture";
        private readonly string root;
        private string foreignState = "";
        private readonly TaskCompletionSource warmup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WarmupStarted { get; private set; }
        internal bool WarmupCompleted { get; private set; }
        internal void ReleaseWarmup() => warmup.TrySetResult();
        private async Task<DesktopServiceCommandResult> Warmup(CancellationToken token)
        {
            WarmupStarted = true;
            await warmup.Task.WaitAsync(token);
            WarmupCompleted = true;
            return new(true, JsonSerializer.SerializeToElement(new { complete = true }));
        }
        internal DesktopPluginCatalog Catalog { get; }
        internal DesktopPluginRuntime Runtime { get; }
        internal string Data => Path.Combine(root, "data", Id);
        internal string OwnerNote => Path.Combine(Data, "owner-note.txt");
        internal string RecordPath => Path.Combine(Catalog.RootDirectory, ".settings", Id + ".activation-update.json");
        internal string RecordState => JsonDocument.Parse(File.ReadAllText(RecordPath)).RootElement.GetProperty("State").GetString()!;
        internal int Count(string name) => File.Exists(Path.Combine(Data, name)) ? File.ReadAllLines(Path.Combine(Data, name)).Length : 0;
        internal int TotalInitializations => Directory.EnumerateFiles(Data, "*.initialize").Sum(path => File.ReadAllLines(path).Length);
        internal Fixture(string root, Type type, bool enabled, bool configuration = false)
        {
            this.root = root; Directory.CreateDirectory(Data); File.WriteAllText(OwnerNote, "owner data");
            Catalog = new(Path.Combine(root, "catalog"));
            Catalog.Install(Package("5.0.0", typeof(ActivationWorkingPlugin), "activation.foreign"));
            foreignState = ForeignState();
            Catalog.Install(Package("1.0.0", type)); Catalog.SetEnabled(Id, enabled);
            var binding = new DesktopConversationBinding("fixture", "saved-chat");
            Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            var host = configuration
                ? new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, _ => { }, pluginDataRoot: Path.Combine(root, "data"), serviceCommand: (_, _, token) => Warmup(token))
                : new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(binding, "private recovery fixture", null, null, false), () => new(binding, 0, ""),
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { }, pluginDataRoot: Path.Combine(root, "data"), serviceCommand: (_, _, token) => Warmup(token));
            Runtime = new(Catalog, host, Dispatcher.CurrentDispatcher); Runtime.LoadEnabled();
        }
        internal string Package(string version, Type type, string? id = null)
        {
            var directory = Path.Combine(root, "sources", version + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(directory, "Fixture.dll"));
            File.WriteAllText(Path.Combine(directory, "retention-marker.txt"), "previous independent retention evidence");
            var manifest = Path.Combine(directory, DesktopPluginCatalog.ManifestFileName);
            File.WriteAllText(manifest, JsonSerializer.Serialize(new DesktopPluginManifest {
                SchemaVersion = 2, Id = id ?? Id, Name = "Activation fixture", Version = version,
                Assembly = "Fixture.dll", EntryType = type.FullName!, Dependencies = []
            }, CerebralJson.Options));
            return manifest;
        }
        private string ForeignState()
        {
            var selected = Catalog.List().Single(item => item.Id == "activation.foreign");
            return selected.Directory + "|" + selected.Enabled + "|" + NativeDesktopPluginPackage.ComputeFingerprint(selected.ManifestPath);
        }
        internal bool ForeignUnchanged() => foreignState == ForeignState();
        public void Dispose() => Runtime.Dispose();
    }
}

public class ActivationWorkingPlugin : DesktopPluginBase, IDesktopConfigurationPlugin
{
    protected string Data = "";
    protected virtual bool FailInitialization => false;
    protected virtual bool FailRecovery => false;
    public override void Initialize(IDesktopPluginHost host)
    {
        base.Initialize(host);
        Data = ((IDesktopPluginEnvironment)host).DataDirectory;
        Directory.CreateDirectory(Data);
        var log = Path.Combine(Data, GetType().Name + ".initialize");
        var repeated = File.Exists(log);
        File.AppendAllText(log, "initialize" + System.Environment.NewLine);
        if (FailInitialization || (FailRecovery && repeated))
        {
            File.WriteAllText(Path.Combine(Data, "candidate-written-data.txt"), "preserve this plugin effect");
            throw new InvalidOperationException("Injected native initialization failure.");
        }
    }
    public override IReadOnlyList<DesktopAction> ComposerActions =>
        [new("hold", "Hold", token => ((IDesktopServiceHost)Host).ExecuteServiceCommandAsync("fixture.hold", new { }, token))];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => Panels;
    public override IReadOnlyList<DesktopPanel> Panels => [new("counter", "Recovery counter", () => {
        var count = 0; var button = new Button { Content = "Counter 0" };
        button.Click += (_, _) => button.Content = "Counter " + (++count);
        return button;
    })];
    public override void Dispose()
    {
        if (Data.Length != 0) File.AppendAllText(Path.Combine(Data, GetType().Name + ".dispose"), "dispose" + System.Environment.NewLine);
        base.Dispose();
    }
}
public sealed class ActivationNewPlugin : ActivationWorkingPlugin;
public class ActivationInitializeFailsPlugin : ActivationWorkingPlugin { protected override bool FailInitialization => true; }
public sealed class ActivationRecoveryFailsPlugin : ActivationWorkingPlugin { protected override bool FailRecovery => true; }
public sealed class ActivationCleanupFailsPlugin : ActivationInitializeFailsPlugin
{
    public override void Dispose() { base.Dispose(); throw new InvalidOperationException("Injected native cleanup uncertainty."); }
}
