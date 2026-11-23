using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cerebral.Auditing.Laya;

internal interface ILayaChild
{
    bool Alive { get; }
    int? ProcessId { get; }
    void Start(LayaConfiguration config);
    Task<string> ReadLineAsync(CancellationToken token);
    Task WriteLineAsync(string line, CancellationToken token);
    Task<bool> StopAsync(TimeSpan limit);
}

/// <summary>Owns one persistent CPU child. Cancellation never frees a running native call early.</summary>
internal sealed class LayaProcessClient(LayaConfiguration config, ILayaChild child, Func<Task>? beforeStart = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object lifecycle = new();
    private int participants;
    private TaskCompletionSource? drained;
    private Task? disposal;
    private Task<bool>? stopping;
    private int started;
    private int admitted, active, disposed;
    private bool quarantined;
    public LayaReadiness? Readiness { get; private set; }
    public int? ProcessId => child.ProcessId;
    public bool Ready => Volatile.Read(ref disposed) == 0 && Readiness is not null && child.Alive && !quarantined;
    public int ActiveRequests => Volatile.Read(ref active);
    public int QueuedRequests => Math.Max(0, Volatile.Read(ref admitted) - ActiveRequests);
    public string? Failure { get; private set; }

    public async Task StartAsync()
    {
        Enter();
        if (Interlocked.Exchange(ref started, 1) != 0)
        { Leave(); throw new InvalidOperationException("Laya child cannot be started twice."); }
        try
        {
            if (beforeStart is not null) await beforeStart().ConfigureAwait(false);
            // A stop with no child must never be cached while a later startup can still create one.
            // The same lifecycle gate serializes creation with DisposeAsync and StopAsync.
            lock (lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                child.Start(config);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            timeout.CancelAfter(config.StartupTimeoutMilliseconds);
            using var document = LayaProtocol.Parse(await child.ReadLineAsync(timeout.Token).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "unavailable")
            {
                LayaProtocol.Fields(document.RootElement, "protocol", "kind", "errorCode");
                LayaProtocol.Envelope(document.RootElement, "unavailable");
                throw new LayaRejectedException(LayaProtocol.Text(document.RootElement, "errorCode", 64));
            }
            Readiness = LayaProtocol.Ready(document.RootElement, config);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Failure = error is LayaRejectedException rejected ? "Native Laya startup was unavailable: " + rejected.Code + "."
                : error is OperationCanceledException ? "Native Laya CPU startup exceeded its configured readiness deadline."
                : "Native Laya startup failed identity, placement, measurement or protocol validation.";
            await StopAsync().ConfigureAwait(false);
            throw new InvalidDataException(Failure);
        }
        finally { Leave(); }
    }

    public async Task<T> ExchangeAsync<T>(object request, Func<JsonElement, T> parse, CancellationToken token)
    {
        Enter();
        var counted = false; var acquired = false; var release = true; var sent = false; var completed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Ready) throw new IOException("Native Laya CPU child is unavailable.");
            counted = true;
            if (Interlocked.Increment(ref admitted) > config.QueueCapacity + 1)
                throw new IOException("Native Laya request queue is full.");
            using var queued = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            await operation.WaitAsync(queued.Token).ConfigureAwait(false); acquired = true;
            token.ThrowIfCancellationRequested();
            if (!Ready) throw new IOException("Native Laya CPU child changed while queued.");
            Volatile.Write(ref active, 1);
            var line = LayaProtocol.Serialize(request);
            if (Encoding.UTF8.GetByteCount(line) + 1 > LayaProtocol.MaximumLineBytes)
                throw new LayaRejectedException("request-byte-limit");
            // Caller cancellation is checked after delivery. A native synchronous prediction still
            // owns capacity until its reply or an independently confirmed owned-process termination.
            using var hardDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            hardDeadline.CancelAfter(config.RequestTimeoutMilliseconds);
            sent = true;
            await child.WriteLineAsync(line, hardDeadline.Token).ConfigureAwait(false);
            var reply = await child.ReadLineAsync(hardDeadline.Token).ConfigureAwait(false);
            using var document = LayaProtocol.Parse(reply);
            var result = parse(document.RootElement);
            completed = true;
            token.ThrowIfCancellationRequested();
            shutdown.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A reported native rejection is a completed call; queued/pre-dispatch cancellation is
            // also safe. A transport/framing failure cannot prove that native computation stopped.
            if (acquired && sent && error is not LayaRejectedException &&
                !(completed && error is OperationCanceledException && token.IsCancellationRequested))
                release = await StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (acquired && release) { Volatile.Write(ref active, 0); operation.Release(); }
            if (counted) Interlocked.Decrement(ref admitted);
            Leave();
        }
    }

    private Task<bool> StopAsync()
    { lock (lifecycle) return stopping ??= StopCoreAsync(); }
    private async Task<bool> StopCoreAsync()
    {
        var stopped = await child.StopAsync(TimeSpan.FromMilliseconds(config.StopTimeoutMilliseconds)).ConfigureAwait(false);
        if (!stopped)
        {
            quarantined = true;
            Volatile.Write(ref active, 1);
            Failure = "Native Laya process termination was not confirmed; occupied capacity remains quarantined.";
            // Queued calls cannot wait forever behind a quarantined native owner. This cancellation
            // does not claim termination or release its occupied slot.
            shutdown.Cancel();
        }
        return stopped;
    }
    private void Enter()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            participants++;
        }
    }
    private void Leave()
    {
        lock (lifecycle)
            if (--participants == 0) drained?.TrySetResult();
    }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref disposed, 1);
            var wait = participants == 0 ? Task.CompletedTask : (drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            disposal = DisposeCoreAsync(wait);
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task wait)
    {
        shutdown.Cancel();
        await StopAsync().ConfigureAwait(false);
        await wait.ConfigureAwait(false);
        // Every started/queued caller has finished its Release/finally path before handles go away.
        operation.Dispose(); shutdown.Dispose();
    }
}

internal sealed class LayaOwnedChild : ILayaChild
{
    private Process? process;
    private int? processId;
    private volatile bool exited;
    private readonly byte[] buffer = new byte[4096];
    private int position, length;
    private Task? stderrDrain;
    private Task<bool>? stop;
    private readonly object gate = new();
    public bool Alive
    {
        get
        {
            try { return !exited && process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public int? ProcessId => processId;
    public void Start(LayaConfiguration config)
    {
        if (process is not null) throw new InvalidOperationException("A Laya child process already exists.");
        config.VerifyLaunchFiles();
        process = Process.Start(CreateStartInfo(config)) ?? throw new IOException("Laya ordinary-user child did not start.");
        processId = process.Id;
        stderrDrain = DrainAsync(process.StandardError.BaseStream);
    }
    internal static ProcessStartInfo CreateStartInfo(LayaConfiguration config)
    {
        var start = new ProcessStartInfo(config.PythonPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true), WorkingDirectory = config.RootDirectory
        };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-S"); start.ArgumentList.Add("-u"); start.ArgumentList.Add(config.RunnerPath);
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(config.ConfigurationPath);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        // Stock torch/ModernBert imports call getpass.getuser even for CPU inference. Without a
        // Windows user identity Python falls back to Unix pwd, which does not exist on Windows.
        // Use the actual signed-in identity; never forward arbitrary caller environment variables.
        start.Environment["USERNAME"] = Environment.UserName;
        start.Environment["PATH"] = Path.GetDirectoryName(config.PythonPath)!;
        start.Environment["CUDA_VISIBLE_DEVICES"] = "";
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        return start;
    }
    private static async Task DrainAsync(Stream stream)
    {
        var scratch = new byte[4096];
        try { while (await stream.ReadAsync(scratch).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    public async Task<string> ReadLineAsync(CancellationToken token)
    {
        var stream = process?.StandardOutput.BaseStream ?? throw new IOException("Laya stdout is unavailable.");
        using var line = new MemoryStream();
        while (true)
        {
            if (position == length)
            {
                length = await stream.ReadAsync(buffer, token).ConfigureAwait(false); position = 0;
                if (length == 0) throw new IOException("Laya child closed its protocol transport.");
            }
            var end = Array.IndexOf(buffer, (byte)'\n', position, length - position);
            var count = (end < 0 ? length : end) - position;
            if (line.Length + count + 1 > LayaProtocol.MaximumLineBytes) throw new IOException("Laya stdout line exceeded its UTF-8 byte bound.");
            line.Write(buffer, position, count); position += count;
            if (end >= 0) { position++; return new UTF8Encoding(false, true).GetString(line.ToArray()); }
        }
    }
    public async Task WriteLineAsync(string line, CancellationToken token)
    {
        var stream = process?.StandardInput.BaseStream ?? throw new IOException("Laya stdin is unavailable.");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public Task<bool> StopAsync(TimeSpan limit)
    { lock (gate) return stop ??= StopCoreAsync(limit); }
    private async Task<bool> StopCoreAsync(TimeSpan limit)
    {
        var owned = process;
        if (owned is null) return true;
        var confirmed = false;
        try
        {
            if (!owned.HasExited) owned.Kill(); // Exact process object created here, never a name/PID search.
            using var timeout = new CancellationTokenSource(limit);
            await owned.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            confirmed = owned.HasExited;
            if (stderrDrain is not null) await stderrDrain.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { confirmed = owned.HasExited; }
        finally
        {
            if (confirmed)
            {
                exited = true;
                // Exit is confirmed before closing transport handles. Concurrent reads then fail
                // safely; the client waits for their finally paths before disposing its semaphore.
                try { owned.StandardInput.Dispose(); }
                finally
                {
                    try { owned.StandardOutput.Dispose(); }
                    finally
                    {
                        try { owned.StandardError.Dispose(); }
                        finally { owned.Dispose(); }
                    }
                }
            }
        }
        return confirmed;
    }
}
