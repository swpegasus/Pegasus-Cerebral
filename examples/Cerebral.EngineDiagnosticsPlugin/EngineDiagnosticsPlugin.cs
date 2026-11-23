using System.Text.Json;
using System.Security.Cryptography;
using Cerebral.Core;

namespace Cerebral.EngineDiagnosticsPlugin;

/// <summary>Removable diagnostics only: no tools, agents, workflows or execution handles.</summary>
public sealed partial class EngineDiagnosticsPlugin : IServicePlugin, IServiceCommandExtension
{
    public const string Id = "pegasus.engine-debug";
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private IServiceEngineDiagnosticsHost? source;
    private IServicePluginHost? host;
    private RollingEngineLog? log;
    private IEngineDiagnosticSubscription? subscription;
    private CancellationTokenSource? stop;
    private Task? pump, maintenance;
    private bool initialized, disposed, enabled, desiredEnabled;
    private EngineDiagnosticsSettingsConsumer? preferences;
    private LegacyPreparation? preparedLegacy;
    private sealed record LegacyPreparation(long CentralRevision, string FileSha256, bool Enabled);
    private string? fault;
    private long dropped;
    internal TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(1);
    internal Func<string, RollingEngineLog> LogFactory { get; init; } = root => new(root);
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [this];
    public IReadOnlyList<string> Operations => ["engine-debug.status", "engine-debug.set",
        "engine-debug.preferences.get", "engine-debug.preferences.legacy.prepare", "engine-debug.preferences.legacy.apply"];

    public void Initialize(IServicePluginHost pluginHost)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized || pluginHost.PluginId != Id) throw new InvalidOperationException("Diagnostic plugin identity/lifecycle mismatch.");
            initialized = true; host = pluginHost; source = pluginHost as IServiceEngineDiagnosticsHost;
            log = LogFactory(pluginHost.DataDirectory);
            log.GuardSettingsRoot();
            try
            {
                preferences = EngineDiagnosticsSettingsConsumer.Connect(pluginHost);
                desiredEnabled = ReadDesired();
            }
            catch (Exception error) when (Ordinary(error)) { fault = "diagnostic-preferences-unavailable"; }
            try { log.Initialize(); } catch (Exception error) when (Ordinary(error)) { fault = "diagnostic-storage-unavailable"; }
            // Retention maintenance remains active while this package is loaded, even if capture is disabled.
            maintenance = Task.Run(() => MaintainAsync(lifetime.Token));
            // Hydration/import changes desired state only. Capture starts only through the explicit owner command.
        }
    }
    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!initialized) throw new InvalidOperationException("Diagnostic plugin has not initialized.");
            }
            if (operation == "engine-debug.status") return Status();
            if (operation.StartsWith("engine-debug.preferences.", StringComparison.Ordinal)) return PreferenceCommand(operation, arguments);
            if (operation != "engine-debug.set" || arguments.ValueKind != JsonValueKind.Object ||
                arguments.EnumerateObject().Count() != 1 ||
                !arguments.TryGetProperty("enabled", out var selected) || selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("The diagnostic setting requires exactly one boolean enabled value.");
            var requested = selected.GetBoolean();
            if (requested && source is null) return Reply("rejected", "host-diagnostic-stream-unavailable");
            // Disabling must detach capture even if a settings/log file has become inaccessible.
            if (!requested) await StopAsync().ConfigureAwait(false);
            Persist(requested);
            if (requested) Start();
            return Status();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (Ordinary(error))
        { lock (gate) fault = "diagnostic-command-failed"; return Reply("failed", fault); }
        finally { lifecycle.Release(); }
    }
    private object Status()
    {
        lock (gate)
        {
            try { desiredEnabled = ReadDesired(); } catch (Exception error) when (Ordinary(error)) { fault = "diagnostic-preferences-unavailable"; }
            var issue = fault ?? log?.Fault;
            // Only framework-owned shapes cross the host serializer boundary. Caching a private
            // anonymous plugin type in shared serializer metadata would prevent collectible unload.
            return new Dictionary<string, object?>
            {
                ["state"] = pump is { IsCompleted: false } && !enabled ? "transitioning" : issue is not null ? "faulted" : enabled ? "active" : "inactive",
                ["enabled"] = enabled, ["desiredEnabled"] = desiredEnabled, ["available"] = source is not null,
                ["droppedRecords"] = dropped + (subscription?.DroppedRecords ?? 0),
                ["fault"] = issue, ["log"] = log?.Snapshot()
            };
        }
    }
    private void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled) return;
            if (pump is { IsCompleted: false }) throw new InvalidOperationException("Diagnostic capture is still stopping.");
            if (source is null) throw new InvalidOperationException("The host does not expose engine diagnostics.");
            log!.Initialize();
            dropped += subscription?.DroppedRecords ?? 0;
            subscription?.Dispose();
            subscription = source.OpenEngineDiagnostics();
            stop?.Dispose(); stop = new(); enabled = true; fault = null;
            var lease = subscription; var token = stop.Token;
            pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in lease.ReadAsync(token).ConfigureAwait(false))
                        log.Append(item, lease.DroppedRecords);
                    if (!token.IsCancellationRequested) lock (gate) fault = "diagnostic-stream-ended";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) when (Ordinary(error)) { lock (gate) fault = "diagnostic-stream-failed"; }
                finally
                {
                    lease.Dispose();
                    lock (gate) if (ReferenceEquals(subscription, lease)) enabled = false;
                }
            });
        }
    }
    private async Task MaintainAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);
        try { while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) log!.Maintain(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task StopAsync()
    {
        Task? task;
        lock (gate) { enabled = false; subscription?.Dispose(); stop?.Cancel(); task = pump; }
        if (task is not null)
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { return; }
        lock (gate)
        {
            dropped += subscription?.DroppedRecords ?? 0;
            subscription = null; stop?.Dispose(); stop = null; pump = null;
        }
    }
    private bool ReadDesired()
    {
        var state = (preferences ?? throw new InvalidOperationException("Central diagnostic preferences are unavailable.")).Read();
        return DesiredFromState(state);
    }
    private static bool DesiredFromState(PluginSettingsState state)
    {
        if (state.Active is null) return false;
        var admitted = new EngineDiagnosticsSettingsPolicy().Admit(state.Active, null, false);
        if (!admitted.Accepted) throw new InvalidDataException("Diagnostic preference schema is unsupported.");
        return admitted.Values.GetProperty("enabled").GetBoolean();
    }
    private void Persist(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var consumer = preferences ?? throw new InvalidOperationException("Central diagnostic preferences are unavailable.");
            var state = consumer.Read(); // An explicit read reconciles any previous uncertain save.
            _ = DesiredFromState(state); // Refuse newer/private active schemas rather than replacing their fields.
            _ = consumer.SaveDesiredEnabled(value, state.Revision);
            desiredEnabled = value;
            preparedLegacy = null;
        }
    }
    private object PreferenceCommand(string operation, JsonElement arguments)
    {
        var consumer = preferences ?? throw new InvalidOperationException("Central diagnostic preferences are unavailable.");
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Diagnostic preference arguments are invalid.");
        if (operation == "engine-debug.preferences.get")
        {
            if (arguments.EnumerateObject().Any()) throw new ArgumentException("Diagnostic preference read requires no arguments.");
            return consumer.Read();
        }
        if (operation == "engine-debug.preferences.legacy.prepare")
        {
            if (arguments.EnumerateObject().Any()) throw new ArgumentException("Diagnostic legacy preparation requires no arguments.");
            preparedLegacy = null;
            var state = consumer.Read();
            if (state.Active is not null || state.Pending.Count != 0)
                throw new InvalidOperationException("Central diagnostic preferences already exist; preserve them.");
            var bytes = ReadLegacy();
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var document = EngineDiagnosticsSettingsPolicy.PrepareLegacy(json.RootElement, "1.0.0");
            var stamp = Convert.ToHexStringLower(SHA256.HashData(bytes));
            preparedLegacy = new(state.Revision, stamp, document.Values.GetProperty("enabled").GetBoolean());
            return new Dictionary<string, object?> { ["expectedRevision"] = state.Revision,
                ["expectedLegacySha256"] = stamp, ["values"] = document.Values };
        }
        if (operation == "engine-debug.preferences.legacy.apply")
        {
            var fields = arguments.EnumerateObject().Select(value => value.Name).ToArray();
            if (fields.Length != 2 || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(["expectedRevision", "expectedLegacySha256"]))
                throw new ArgumentException("Diagnostic legacy apply requires its exact prepared revision and file preimage.");
            var prepared = preparedLegacy ?? throw new InvalidOperationException("Prepare the existing diagnostic file before applying it.");
            preparedLegacy = null; // Never replay an attempted migration, including an uncertain acknowledgement.
            if (!arguments.GetProperty("expectedRevision").TryGetInt64(out var revision) || revision != prepared.CentralRevision ||
                arguments.GetProperty("expectedLegacySha256").ValueKind != JsonValueKind.String ||
                arguments.GetProperty("expectedLegacySha256").GetString() != prepared.FileSha256 ||
                Convert.ToHexStringLower(SHA256.HashData(ReadLegacy())) != prepared.FileSha256)
                throw new InvalidOperationException("The legacy preparation changed. Prepare and review it again.");
            var current = consumer.Read();
            if (current.Revision != revision || current.Active is not null || current.Pending.Count != 0)
                throw new InvalidOperationException("Central preferences changed; the original file was preserved.");
            var saved = consumer.SaveDesiredEnabled(prepared.Enabled, revision);
            desiredEnabled = prepared.Enabled;
            return saved;
        }
        throw new ArgumentException("Unknown diagnostic preference operation.");
    }
    private byte[] ReadLegacy()
    {
        log!.GuardSettingsRoot();
        var path = System.IO.Path.Combine(host!.DataDirectory, "engine-debug-settings.json");
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The existing diagnostic preference file is unavailable.");
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > EngineDiagnosticsSettingsPolicy.MaximumValuesUtf8Bytes)
            throw new InvalidDataException("The existing diagnostic preference file exceeds its bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The diagnostic preference file changed while reading.");
        return bytes;
    }
    public void Dispose()
    {
        Task pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; enabled = false; preparedLegacy = null; preferences = null;
            subscription?.Dispose(); stop?.Cancel(); lifetime.Cancel();
            pending = Task.WhenAll(pump ?? Task.CompletedTask, maintenance ?? Task.CompletedTask);
        }
        // Logical detach is immediate. A bounded in-flight disk write may finish after this deadline.
        try { pending.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _ = pending.ContinueWith(_ =>
        {
            lock (gate) { stop?.Dispose(); lifetime.Dispose(); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static Dictionary<string, object?> Reply(string state, string? reason) =>
        new() { ["state"] = state, ["reason"] = reason };
    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
