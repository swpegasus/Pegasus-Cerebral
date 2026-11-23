using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class PluginPublicationLeaseTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.PublicationLease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "Fixture.dll"), [0x4d, 0x5a, 0x01, 0x02]);
            var manifest = new DesktopPluginManifest
            {
                Id = "lease.fixture", Name = "Lease fixture", Version = "1.0.0",
                Description = "Code-free metadata fixture", Assembly = "Fixture.dll", EntryType = "Fixture.Plugin"
            };
            var manifestPath = Path.Combine(source, DesktopPluginCatalog.ManifestFileName);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, CerebralJson.Options));
            var catalog = new DesktopPluginCatalog(Path.Combine(root, "catalog"));
            catalog.Install(manifestPath);
            var marker = Path.Combine(catalog.RootDirectory, ".settings", ".catalog-publication.lock");

            Check(catalog.List().Single() is { Version: "1.0.0", Enabled: false, Error: null }, "Initial import is coherent.");
            Check(File.Exists(marker) && new FileInfo(marker).Length == 0, "An empty persistent lock file carries no lease status.");

            using (var firstRead = Observe(catalog))
            {
                using (var secondRead = Observe(new DesktopPluginCatalog(catalog.RootDirectory.ToUpperInvariant())))
                {
                    Check(Probe("read", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Two processes can observe a stable catalog.");
                    Check(!Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Readers exclude a separate publisher.");
                    Check(!Probe("mutate", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Reader ownership excludes a real enablement mutation.");
                    Check(catalog.List().Single().Enabled == false, "A rejected competing mutation preserves enablement.");
                    var upgradeRejected = false;
                    try { using var upgrade = Publish(catalog); } catch (IOException) { upgradeRejected = true; }
                    Check(upgradeRejected, "A reader cannot upgrade its lease into a publisher.");
                }
                Check(!Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Releasing a nested reader retains the outer lease.");
            }
            Check(Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "The last reader release permits a publisher.");

            using (var publisher = Publish(catalog))
            {
                using var nestedPublisher = Publish(new DesktopPluginCatalog(catalog.RootDirectory));
                using var nestedReader = Observe(catalog);
                Check(catalog.List().Count == 1, "Publication can inspect its own selected state through nested calls.");
                Check(!Probe("read", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "A publisher excludes a separate reader.");
                Check(!Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "A publisher excludes a separate publisher.");
                Check(!Probe("mutate", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "A publisher excludes a real competing mutation.");
            }
            Check(Probe("read", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Readers resume after publication releases every nested lease.");

            using (var child = HoldPublisher(catalog.RootDirectory))
            {
                Check(!child.HasExited, "The cross-process publication holder is confirmed live.");
                var denied = false;
                try { _ = catalog.List(); } catch (IOException) { denied = true; }
                Check(denied, "The real parent catalog cannot observe a live publisher's intermediate state.");
                Check(!catalog.TryList(out var busyProjection) && busyProjection.Count == 0,
                    "Nonblocking observation reports busy without projecting partially published packages.");
                child.Kill(entireProcessTree: true);
                Check(child.WaitForExit(5000), "The exact test-owned publisher terminated.");
            }
            Check(File.Exists(marker) && Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(),
                "OS process death releases ownership even though the empty marker remains.");

            using (var abandoned = HoldPublisher(catalog.RootDirectory)) { }
            Check(Probe("write", catalog.RootDirectory).GetProperty("allowed").GetBoolean(),
                "Disposing an unreleased test-owned holder terminates it and frees its lease.");

            using (var child = HoldPublisher(catalog.RootDirectory))
            {
                var binding = new DesktopConversationBinding("lease-chat", "lease-fixture");
                var draft = new DesktopDraftSnapshot(binding, 0, "");
                Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
                var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                    () => new(binding, "Lease fixture", null, null, false), () => draft,
                    (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { });
                using var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
                var changed = 0;
                runtime.Changed += (_, _) => changed++;
                runtime.LoadEnabled();
                var timerField = typeof(DesktopPluginRuntime).GetField("publicationRefreshTimer", PrivateInstance)!;
                var timer = (DispatcherTimer?)timerField.GetValue(runtime);
                Check(timer is { IsEnabled: true } && timer.Interval == TimeSpan.FromSeconds(1),
                    "A busy runtime schedules one bounded one-second refresh timer.");
                Check(changed == 0 && runtime.Status.Count == 0, "A busy runtime preserves its previous coherent contributions without reporting partial load errors.");
                Release(child);
                PumpUntil(() => changed != 0, TimeSpan.FromSeconds(5));
                Check(changed != 0 && timer is { IsEnabled: false }, "The runtime automatically refreshes after publication releases.");
                Check(runtime.Status.Count == 0, "Deferred refresh leaves disabled code unexecuted.");
            }

            using (var child = HoldPublisher(catalog.RootDirectory))
            {
                var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, _ => { }, Path.Combine(root, "plugin-data"));
                var runtime = new DesktopPluginRuntime(catalog, host, Dispatcher.CurrentDispatcher);
                runtime.LoadEnabled();
                var timer = (DispatcherTimer?)typeof(DesktopPluginRuntime).GetField("publicationRefreshTimer", PrivateInstance)!.GetValue(runtime);
                runtime.Dispose();
                Check(timer is { IsEnabled: false }, "Runtime disposal stops its publication retry timer.");
                Release(child);
            }

            // Exercise the real reviewed package entry point, not only the private lease helper.
            var products = new DesktopProductPaths(Path.Combine(root, "install"), Path.Combine(root, "data"),
                Path.Combine(root, "user"), Path.Combine(root, "projects"), Path.Combine(root, "models"),
                Path.Combine(root, "tools"), Path.Combine(root, "shared"));
            var sourceProducts = products with { ModelsDirectory = Path.Combine(root, "source-models") };
            var resource = new CerebralPluginPackageResource
            {
                Id = "lease.fixture.model", Kind = "model", Source = "resources/model", Destination = "lease.fixture/model"
            };
            File.WriteAllText(Path.Combine(source, CerebralPluginPackageContract.ResourceManifestFileName),
                JsonSerializer.Serialize(new CerebralPluginResourceManifest { Resources = [resource] }, CerebralJson.Options));
            File.WriteAllText(Path.Combine(source, CerebralPluginPackageContract.DefaultSettingsFileName), "{\"version\":\"1.1.0\"}");
            var modelSource = Path.Combine(sourceProducts.ModelsDirectory, "lease.fixture", "model");
            Directory.CreateDirectory(modelSource); File.WriteAllText(Path.Combine(modelSource, "weights.gguf"), "candidate resource");
            manifest.Version = "1.1.0";
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, CerebralJson.Options));
            var package = Path.Combine(root, "candidate.CerebralPlugin");
            CerebralPluginPackage.Export(new(manifest.Id, manifest.Name, manifest.Version, manifest.Description,
                false, source, manifestPath, null), package, "defaults", sourceProducts, Path.Combine(root, "source-data"));
            var pluginData = Path.Combine(root, "plugin-data");
            var presetPath = Path.Combine(pluginData, manifest.Id, CerebralPluginPackageContract.CurrentSettingsFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(presetPath)!); File.WriteAllText(presetPath, "owner preset");
            var modelPath = Path.Combine(products.ModelsDirectory, "lease.fixture", "model", "weights.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!); File.WriteAllText(modelPath, "owner resource");
            var activePath = Path.Combine(catalog.RootDirectory, ".settings", "lease.fixture.active.json");
            var priorActive = File.ReadAllBytes(activePath);
            using (var prepared = CerebralPluginPackage.Prepare(package))
            {
                using (var child = HoldPublisher(catalog.RootDirectory))
                {
                    var rejected = false;
                    try { prepared.InstallReviewed(catalog, products, pluginData); } catch (IOException) { rejected = true; }
                    Check(rejected, "The reviewed package entry point rejects a competing live publication lease.");
                    Check(File.ReadAllText(presetPath) == "owner preset" && File.ReadAllText(modelPath) == "owner resource" &&
                        File.ReadAllBytes(activePath).SequenceEqual(priorActive),
                        "A blocked reviewed update does not displace code selection, presets or resources.");
                    Check(!Directory.EnumerateFileSystemEntries(root, "*cerebral-staging-*", SearchOption.AllDirectories).Any() &&
                        !Directory.EnumerateFileSystemEntries(root, "*cerebral-previous-*", SearchOption.AllDirectories).Any(),
                        "A blocked reviewed update performs no participant staging.");
                    Release(child);
                }
                var updated = prepared.InstallReviewed(catalog, products, pluginData);
                Check(updated.Version == "1.1.0" && !updated.Enabled && File.ReadAllText(modelPath) == "candidate resource" &&
                    File.ReadAllText(presetPath).Contains("1.1.0", StringComparison.Ordinal),
                    "After publication releases, the explicit reviewed update commits its complete candidate.");
                Check(catalog.List().Single().Directory == updated.Directory,
                    "The caller's final selection agrees with the observed immutable catalog generation.");
            }

            Check(catalog.TryList(out var completeProjection) && completeProjection.Single().Version == "1.1.0",
                "Nonblocking observation resumes with the complete committed candidate.");
            File.WriteAllText(marker, "invalid marker");
            var invalid = false;
            try { _ = catalog.List(); } catch (InvalidDataException) { invalid = true; }
            Check(invalid, "Malformed lock data is a real diagnostic, not an inferred busy lease.");
            var invalidNonblocking = false;
            try { _ = catalog.TryList(out _); } catch (InvalidDataException) { invalidNonblocking = true; }
            Check(invalidNonblocking, "Nonblocking observation preserves real storage diagnostics.");
            File.WriteAllBytes(marker, []);
            Check(Probe("read", catalog.RootDirectory).GetProperty("allowed").GetBoolean(), "Corrected marker data allows observation.");
            return checks;
        }
        finally
        {
            // The test owns this exact random direct child of Temp; no owner catalogs are used.
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static void RunChild(string[] args)
    {
        var mode = args[1];
        var catalog = new DesktopPluginCatalog(args[2]);
        try
        {
            if (mode == "read") _ = catalog.List();
            else if (mode == "mutate") catalog.SetEnabled("lease.fixture", true);
            else
            {
                using var publisher = Publish(catalog);
                if (mode == "hold")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pid = Environment.ProcessId }));
                    Console.Out.Flush();
                    if (Console.ReadLine() != "release") throw new IOException("The publication holder was not released normally.");
                    return;
                }
                if (mode != "write") throw new ArgumentException("Unknown lease fixture mode.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { allowed = true }));
        }
        catch (IOException error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { allowed = false, error = error.Message }));
        }
    }

    private static IDisposable Publish(DesktopPluginCatalog catalog) => InvokeLease(catalog, "BeginPublication");
    private static IDisposable Observe(DesktopPluginCatalog catalog) => InvokeLease(catalog, "TryBeginObservation");
    private static IDisposable InvokeLease(DesktopPluginCatalog catalog, string method)
    {
        try
        {
            return (IDisposable?)typeof(DesktopPluginCatalog).GetMethod(method, PrivateInstance)!.Invoke(catalog, null)
                ?? throw new IOException("Catalog observation is busy.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static Process Start(string mode, string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The fixture executable is unknown.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--plugin-publication-lease-child", mode, root }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("The test-owned child did not start.");
    }

    private static JsonElement Probe(string mode, string root)
    {
        using var child = Start(mode, root);
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20_000)) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); throw new TimeoutException("A lease probe timed out."); }
        if (child.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
        using var json = JsonDocument.Parse(output.GetAwaiter().GetResult());
        return json.RootElement.Clone();
    }

    private static OwnedPublisher HoldPublisher(string root)
    {
        var child = Start("hold", root);
        try
        {
            var ready = child.StandardOutput.ReadLineAsync();
            if (!ready.Wait(20_000)) throw new TimeoutException("The lease holder did not acknowledge readiness.");
            using var json = JsonDocument.Parse(ready.Result ?? throw new IOException("The lease holder exited before readiness."));
            if (!json.RootElement.GetProperty("ready").GetBoolean() || json.RootElement.GetProperty("pid").GetInt32() != child.Id)
                throw new IOException("The lease holder identity did not match the started process.");
            return new OwnedPublisher(child);
        }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } child.Dispose(); throw; }
    }

    private static void Release(OwnedPublisher child)
    {
        child.StandardInput.WriteLine("release"); child.StandardInput.Flush();
        if (!child.WaitForExit(5000)) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); throw new TimeoutException("The exact holder did not exit."); }
        if (child.ExitCode != 0) throw new InvalidOperationException(child.StandardError.ReadToEnd());
    }

    private sealed class OwnedPublisher(Process child) : IDisposable
    {
        private bool disposed;
        public bool HasExited => child.HasExited;
        public int ExitCode => child.ExitCode;
        public StreamWriter StandardInput => child.StandardInput;
        public StreamReader StandardError => child.StandardError;
        public void Kill(bool entireProcessTree) => child.Kill(entireProcessTree);
        public bool WaitForExit(int milliseconds) => child.WaitForExit(milliseconds);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                // Dispose must also clean up when an assertion interrupts a holder's normal release.
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    if (!child.WaitForExit(5000))
                        throw new TimeoutException("The exact test-owned holder did not terminate during cleanup.");
                }
            }
            finally { child.Dispose(); }
        }
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        if (!condition()) throw new TimeoutException("The deferred catalog refresh did not execute.");
    }
}
