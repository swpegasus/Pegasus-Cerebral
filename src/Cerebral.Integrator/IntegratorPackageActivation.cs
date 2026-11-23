using System.IO;
using System.Text.Json;
using System.Windows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

internal sealed record IntegratorPackageOpenRequest(string Kind, string Path, DateTimeOffset RequestedAt);
internal sealed record PendingIntegratorPackageOpen(string QueueFile, IntegratorPackageOpenRequest Request);

internal static class IntegratorPackageActivationReadiness
{
    public static async Task RequireAsync(Task<DesktopStartupOutcome> initialConnection, Func<bool> isConnected)
    {
        ArgumentNullException.ThrowIfNull(initialConnection);
        ArgumentNullException.ThrowIfNull(isConnected);
        var outcome = await initialConnection;
        if (isConnected()) return;
        if (!outcome.Connected)
            throw new InvalidOperationException(outcome.Diagnostic ??
                "The configured service did not become ready. Reconnect before opening the package again.");
        throw new InvalidOperationException(
            "The configured service connection changed before the package could be reviewed. Reconnect and open the package again.");
    }
}

/// <summary>
/// Durable same-user handoff for file associations. A secondary Configuration process writes only one validated
/// package path, signals the primary instance, and exits. The primary remains the sole owner of review dialogs.
/// </summary>
internal sealed class IntegratorPackageActivationQueue
{
    private const int MaximumPending = 32;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };

    public IntegratorPackageActivationQueue(string? root = null) => directory = Path.Combine(
        Path.GetFullPath(root ?? DesktopInstancePaths.Current.UserDataDirectory), "Activation", "Configuration");

    public static IntegratorPackageOpenRequest? Parse(string[] args)
    {
        static string? Value(string[] values, string option)
        {
            var index = Array.FindIndex(values, x => x.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
        }
        var plugin = Value(args, "--import-plugin");
        var model = Value(args, "--import-model");
        var worker = Value(args, "--import-worker");
        if (new[] { plugin, model, worker }.Count(value => value is not null) > 1)
            throw new ArgumentException("Open one Cerebral package or Worker invitation at a time.");
        if (plugin is null && model is null && worker is null) return null;
        var kind = plugin is not null ? "plugin" : model is not null ? "model" : "worker";
        var raw = plugin ?? model ?? worker!;
        var path = Path.GetFullPath(raw);
        var valid = kind == "plugin"
            ? path.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase)
            : kind == "model" ? ModelPackageContract.IsPackagePath(path)
            : path.EndsWith(WorkerProvisioningPackage.Extension, StringComparison.OrdinalIgnoreCase);
        if (!valid || !File.Exists(path)) throw new FileNotFoundException($"The selected Cerebral {kind} package is missing or has the wrong extension.", path);
        return new(kind, path, DateTimeOffset.UtcNow);
    }

    public void Enqueue(IntegratorPackageOpenRequest request)
    {
        Directory.CreateDirectory(directory);
        Prune();
        if (Directory.EnumerateFiles(directory, "*.request.json", SearchOption.TopDirectoryOnly).Take(MaximumPending).Count() >= MaximumPending)
            throw new IOException("Cerebral Configuration already has 32 package requests waiting for review.");
        var temporary = Path.Combine(directory, ".incoming-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".request.json");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(request, Json));
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<PendingIntegratorPackageOpen> Pending()
    {
        if (!Directory.Exists(directory)) return [];
        Prune();
        var result = new List<PendingIntegratorPackageOpen>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.request.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).Take(MaximumPending))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length is < 2 or > 65_536) throw new InvalidDataException("Invalid package activation request size.");
                var request = JsonSerializer.Deserialize<IntegratorPackageOpenRequest>(File.ReadAllText(file), Json)
                    ?? throw new InvalidDataException("Package activation request is empty.");
                var reparsed = Parse(request.Kind switch
                {
                    "plugin" => ["--import-plugin", request.Path],
                    "model" => ["--import-model", request.Path],
                    "worker" => ["--import-worker", request.Path],
                    _ => []
                });
                if (reparsed is null || request.RequestedAt < DateTimeOffset.UtcNow - MaximumAge)
                    throw new InvalidDataException("Package activation request is stale.");
                result.Add(new(file, request));
            }
            catch
            {
                File.Delete(file);
            }
        }
        return result;
    }

    public void Complete(PendingIntegratorPackageOpen pending)
    {
        var full = Path.GetFullPath(pending.QueueFile);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).EndsWith(".request.json", StringComparison.Ordinal))
            throw new IOException("Package activation receipt escaped its owned queue.");
        if (File.Exists(full)) File.Delete(full);
    }

    private void Prune()
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow - MaximumAge;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if ((name.StartsWith(".incoming-", StringComparison.Ordinal) || name.EndsWith(".request.json", StringComparison.Ordinal)) &&
                File.GetLastWriteTimeUtc(file) < cutoff)
                File.Delete(file);
        }
    }

    internal static int Check()
    {
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.ActivationTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plugin = Path.Combine(root, "Fixture.CerebralPlugin");
            var model = Path.Combine(root, "Fixture.CerebralLLM");
            var worker = Path.Combine(root, "Fixture.CerebralWorker");
            File.WriteAllText(plugin, "fixture"); File.WriteAllText(model, "fixture"); File.WriteAllText(worker, "fixture");
            var queue = new IntegratorPackageActivationQueue(Path.Combine(root, "state"));
            queue.Enqueue(Parse(["--import-plugin", plugin])!);
            queue.Enqueue(Parse(["--import-model", model])!);
            queue.Enqueue(Parse(["--import-worker", worker])!);
            var pending = queue.Pending();
            if (pending.Count != 3 || pending.Select(x => x.Request.Kind).ToHashSet(StringComparer.Ordinal).SetEquals(["plugin", "model", "worker"]) is false)
                throw new InvalidOperationException("Package activation queue did not preserve all typed requests.");
            foreach (var item in pending) queue.Complete(item);
            if (queue.Pending().Count != 0) throw new InvalidOperationException("Package activation queue did not retire reviewed requests.");
            var rejected = false;
            try { _ = Parse(["--import-plugin", model]); } catch (FileNotFoundException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Package activation accepted a mismatched extension.");
            CheckReadinessAsync().GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks = 7, kinds = new[] { "plugin", "model", "worker" } }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CheckReadinessAsync()
    {
        var startup = new TaskCompletionSource<DesktopStartupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = IntegratorPackageActivationReadiness.RequireAsync(startup.Task, () => true);
        if (wait.IsCompleted) throw new InvalidOperationException("Package activation did not wait for initial connection readiness.");
        startup.SetResult(new(true));
        await wait;

        var offlineRejected = false;
        try
        {
            await IntegratorPackageActivationReadiness.RequireAsync(
                Task.FromResult(new DesktopStartupOutcome(false, "fixture service unavailable")), () => false);
        }
        catch (InvalidOperationException error) { offlineRejected = error.Message == "fixture service unavailable"; }
        if (!offlineRejected) throw new InvalidOperationException("Package activation discarded the initial connection diagnostic.");

        await IntegratorPackageActivationReadiness.RequireAsync(
            Task.FromResult(new DesktopStartupOutcome(false, "superseded startup diagnostic")), () => true);

        var changedRejected = false;
        try
        {
            await IntegratorPackageActivationReadiness.RequireAsync(
                Task.FromResult(new DesktopStartupOutcome(true)), () => false);
        }
        catch (InvalidOperationException error)
        {
            changedRejected = error.Message.Contains("connection changed", StringComparison.OrdinalIgnoreCase);
        }
        if (!changedRejected) throw new InvalidOperationException("Package activation ignored a connection change after initial readiness.");
    }
}

public sealed partial class IntegratorWindow
{
    private readonly SemaphoreSlim packageReview = new(1, 1);

    internal async Task OpenPackageRequestAsync(IntegratorPackageOpenRequest request)
    {
        await packageReview.WaitAsync();
        try
        {
            // The base window and Program both react to Loaded. Package review may therefore be queued
            // while the saved service is still connecting; bind it only after that startup generation settles.
            await IntegratorPackageActivationReadiness.RequireAsync(InitialConnectionSettled, () => ServiceConnected);
            Cerebral.Desktop.DesktopApplicationInstance.Show(this);
            SetStatus("Reviewing " + Path.GetFileName(request.Path) + "…");
            if (request.Kind == "plugin") await ImportCerebralPluginPackage(request.Path);
            else if (request.Kind == "model") await ImportModelPackage(request.Path);
            else await ImportProvisionedWorker(request.Path);
        }
        catch (Exception ex)
        {
            SetStatus("Package import did not complete: " + ex.Message, true);
            MessageBox.Show(this, ex.Message, "Pegasus Cerebral package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { packageReview.Release(); }
    }
}
