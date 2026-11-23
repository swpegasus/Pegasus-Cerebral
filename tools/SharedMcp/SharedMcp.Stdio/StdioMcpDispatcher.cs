using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.SharedMcp;

/// <summary>
/// Optional development bridge to one existing product STDIO backend. This does
/// not compose product state, duplicate catalogs, acquire privileges or replay
/// requests after a lost connection. Products can instead inject their dispatcher.
/// </summary>
public sealed class StdioMcpDispatcher : IMcpDispatcher
{
    private readonly StdioBackendOptions _options;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Connection? _connection;
    private long _nextId;
    private int _disposed;

    public StdioMcpDispatcher(StdioBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.Executable) || !File.Exists(options.Executable) ||
            !Path.IsPathFullyQualified(options.WorkingDirectory) || !Directory.Exists(options.WorkingDirectory))
            throw new ArgumentException("Backend executable and working directory must be existing absolute paths.", nameof(options));
        if (options.Arguments is null || options.Arguments.Any(value => value is null || value.Contains('\0')) ||
            options.MaximumPendingRequests is < 1 or > 32 || options.MaximumRequestBytes is < 1024 or > 16 * 1024 * 1024 ||
            options.MaximumResponseBytes is < 1024 or > 16 * 1024 * 1024 || options.StartupTimeout <= TimeSpan.Zero ||
            options.RequestTimeout <= TimeSpan.Zero || string.IsNullOrWhiteSpace(options.ClientName))
            throw new ArgumentException("Backend limits, arguments or client name are invalid.", nameof(options));
        _options = options with { Arguments = options.Arguments.ToArray() };
    }

    public async Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (JsonObject)(await connection.Ready.ConfigureAwait(false)).DeepClone();
    }

    public async Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var originalId = request["id"]?.DeepClone();
        if (originalId is not JsonValue || request["method"] is not JsonValue methodValue ||
            !methodValue.TryGetValue<string>(out var method) || string.IsNullOrWhiteSpace(method) ||
            request["jsonrpc"]?.GetValue<string>() != "2.0")
            return Error(originalId, "backend_request_invalid", "Invalid backend request.", "NotDispatched");
        if (method is "initialize" or "notifications/initialized")
            return Error(originalId, "backend_lifecycle_owned", "Backend initialization is owned by the shared dispatcher.", "NotDispatched");

        Connection connection;
        try { connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        { return Error(originalId, "backend_unavailable", "The local product backend is unavailable.", "NotDispatched"); }

        var outbound = (JsonObject)request.DeepClone();
        if (_options.CortexMultiplex)
        {
            if (outbound["params"] is null) outbound["params"] = new JsonObject();
            if (outbound["params"] is not JsonObject parameters)
                return Error(originalId, "backend_request_invalid", "Backend parameters must be an object.", "NotDispatched");
            if (parameters["_meta"] is null) parameters["_meta"] = new JsonObject();
            if (parameters["_meta"] is not JsonObject metadata)
                return Error(originalId, "backend_request_invalid", "Backend metadata must be an object.", "NotDispatched");
            var stream = context.SessionId + ":" + context.StreamId.ToString(CultureInfo.InvariantCulture);
            metadata["pegasus.cortex/streamId"] = stream.Length <= 64
                ? stream : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stream))).ToLowerInvariant();
        }
        return await SendRequestAsync(connection, outbound, originalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Connection connection;
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_connection is { IsClosed: false } current) connection = current;
            else
            {
                if (_connection is { } previous)
                {
                    await previous.Cleanup.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!previous.ChildExited) throw new IOException("backend_child_stop_unknown");
                    await Task.WhenAll(previous.Reader, previous.Diagnostics).WaitAsync(cancellationToken).ConfigureAwait(false);
                    previous.Stop.Dispose();
                }
                var start = new ProcessStartInfo(_options.Executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _options.WorkingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false, true),
                    StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false, false)
                };
                foreach (var argument in _options.Arguments) start.ArgumentList.Add(argument);
                var process = new Process { StartInfo = start };
                try
                {
                    if (!process.Start()) throw new IOException("backend_start_failed");
                    connection = new Connection(process, _stop.Token);
                }
                catch { process.Dispose(); throw new IOException("backend_start_failed"); }
                _connection = connection;
                connection.Reader = ReadResponsesAsync(connection);
                connection.Diagnostics = DrainDiagnosticsAsync(connection);
                connection.Ready = InitializeBackendAsync(connection);
            }
        }
        finally { _connect.Release(); }
        await connection.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<JsonObject> InitializeBackendAsync(Connection connection)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        startup.CancelAfter(_options.StartupTimeout);
        try
        {
            var capabilities = new JsonObject();
            if (_options.CortexMultiplex)
                capabilities["experimental"] = new JsonObject { ["pegasus.cortex/multiplex"] = true };
            var response = await SendRequestAsync(connection, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "initialize", ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25", ["capabilities"] = capabilities,
                    ["clientInfo"] = new JsonObject { ["name"] = _options.ClientName, ["version"] = "0.1" }
                }
            }, JsonValue.Create("initialize"), startup.Token).ConfigureAwait(false);
            if (response["result"] is not JsonObject result ||
                result["protocolVersion"] is not JsonValue version || !version.TryGetValue<string>(out var protocol) ||
                protocol != "2025-11-25" || result["capabilities"] is not JsonObject)
                throw new IOException("backend_initialize_failed");
            if (_options.CortexMultiplex)
            {
                var extension = result["capabilities"]?["experimental"]?["pegasus.cortex/multiplex"];
                // Cortex declares supported experimental extensions as objects;
                // accept the older explicit true declaration, never false/null.
                if (extension is not JsonObject && !(extension is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled))
                    throw new IOException("backend_multiplex_unavailable");
            }
            await WriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, startup.Token).ConfigureAwait(false);
            return (JsonObject)result.DeepClone();
        }
        catch
        {
            Close(connection);
            throw new IOException("backend_initialize_failed");
        }
    }

    private async Task<JsonObject> SendRequestAsync(Connection connection, JsonObject request, JsonNode? originalId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backendId = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        request["id"] = backendId;
        Pending pending;
        lock (connection.Gate)
        {
            if (connection.IsClosed) return Error(originalId, "backend_disconnected", "Backend disconnected before dispatch.", "NotDispatched");
            if (connection.Pending.Count >= _options.MaximumPendingRequests)
                return Error(originalId, "backend_pending_limit", "The local backend pending-request limit was reached.", "NotDispatched");
            pending = new Pending(originalId);
            connection.Pending.Add(backendId, pending);
            // Establish the deadline task before another thread can remove the
            // reservation. Its clock begins only once the full frame is sent.
            pending.Deadline = WatchDeadlineAsync(connection, backendId, pending);
        }
        var dispatched = false;
        try
        {
            // A write exception can occur after some bytes reached the child;
            // closing all pending calls is safer than claiming no dispatch.
            await WriteAsync(connection, request, cancellationToken, () => dispatched = true).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            RemovePending(connection, backendId);
            return Error(originalId, "backend_request_too_large", "Backend request exceeds the configured byte limit.", "NotDispatched");
        }
        catch (OperationCanceledException) when (!dispatched && cancellationToken.IsCancellationRequested)
        {
            RemovePending(connection, backendId);
            throw;
        }
        catch
        {
            Close(connection);
            return Error(originalId, "backend_disconnected", "Backend write failed; outcome is unknown and must not be replayed automatically.", "Unknown");
        }
        pending.Started.TrySetResult();
        using var registration = cancellationToken.Register(() => Observe(CancelAsync(connection, backendId)));
        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WatchDeadlineAsync(Connection connection, string backendId, Pending pending)
    {
        try
        {
            await pending.Started.Task.WaitAsync(pending.Token).ConfigureAwait(false);
            await Task.Delay(_options.RequestTimeout, pending.Token).ConfigureAwait(false);
            pending.Completion.TrySetResult(Error(pending.OriginalId, "backend_timeout", "Backend request timed out; outcome is unknown and must not be replayed automatically.", "Unknown"));
            await CancelAsync(connection, backendId).ConfigureAwait(false);
            // Keep the reservation until the actual reply or EOF. Otherwise an
            // uncooperative child could accumulate unbounded outstanding work.
        }
        catch (OperationCanceledException) { }
    }

    private async Task CancelAsync(Connection connection, string backendId)
    {
        lock (connection.Gate)
            if (connection.IsClosed || !connection.Pending.ContainsKey(backendId)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await WriteAsync(connection, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = backendId }
            }, timeout.Token).ConfigureAwait(false);
        }
        catch { Close(connection); }
    }

    private async Task WriteAsync(Connection connection, JsonObject request, CancellationToken cancellationToken, Action? beforeWrite = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > _options.MaximumRequestBytes) throw new InvalidDataException("backend_request_too_large");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Stop.Token);
        await connection.WriteGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            beforeWrite?.Invoke();
            // Canceling a caller cannot cut a shared JSON frame in half. Finish
            // this bounded write, then send its mapped cancellation separately.
            using var write = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token);
            write.CancelAfter(TimeSpan.FromSeconds(5));
            await connection.Process.StandardInput.BaseStream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
            await connection.Process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), write.Token).ConfigureAwait(false);
            await connection.Process.StandardInput.BaseStream.FlushAsync(write.Token).ConfigureAwait(false);
        }
        finally { connection.WriteGate.Release(); }
    }

    private async Task ReadResponsesAsync(Connection connection)
    {
        try
        {
            var stream = connection.Process.StandardOutput.BaseStream;
            var buffer = new byte[8192];
            using var line = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, connection.Stop.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("backend_eof");
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        if (line.Length >= _options.MaximumResponseBytes) throw new InvalidDataException("backend_response_too_large");
                        line.WriteByte(buffer[i]);
                        continue;
                    }
                    var response = JsonNode.Parse(line.GetBuffer().AsSpan(0, checked((int)line.Length)),
                        documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                        ?? throw new InvalidDataException("backend_response_invalid");
                    line.SetLength(0);
                    if (response["jsonrpc"]?.GetValue<string>() != "2.0") throw new InvalidDataException("backend_response_invalid");
                    // This bridge offers request/response dispatch only. Product
                    // notifications are not assigned to an arbitrary HTTP session.
                    if (response["method"] is not null && response["id"] is null) continue;
                    if (response["method"] is not null || response["id"] is not JsonValue idValue ||
                        !idValue.TryGetValue<string>(out var id) || response.ContainsKey("result") == response.ContainsKey("error"))
                        throw new InvalidDataException("backend_response_invalid");
                    var pending = RemovePending(connection, id) ?? throw new InvalidDataException("backend_response_unexpected");
                    response["id"] = pending.OriginalId?.DeepClone();
                    pending.Completion.TrySetResult(response);
                }
            }
        }
        catch { Close(connection); }
    }

    private static Pending? RemovePending(Connection connection, string id)
    {
        Pending? pending;
        lock (connection.Gate)
        {
            if (!connection.Pending.Remove(id, out pending)) return null;
        }
        ReleasePending(pending);
        return pending;
    }

    private static void ReleasePending(Pending pending)
    {
        pending.Stop.Cancel();
        // Delay/cancellation callbacks may still own the source. Dispose only
        // after the actual deadline task settles, including early responses.
        Observe(pending.Deadline.ContinueWith(_ => pending.Stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    private static async Task DrainDiagnosticsAsync(Connection connection)
    {
        try
        {
            var buffer = new char[2048];
            // Drain without retaining messages: backend diagnostics can contain
            // request bodies or credentials. No unbounded stderr buffer is kept.
            while (await connection.Process.StandardError.ReadAsync(buffer, connection.Stop.Token).ConfigureAwait(false) != 0) { }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static void Close(Connection connection)
    {
        Pending[] pending;
        lock (connection.Gate)
        {
            if (connection.IsClosed) return;
            connection.IsClosed = true;
            pending = connection.Pending.Values.ToArray();
            connection.Pending.Clear();
        }
        foreach (var item in pending)
        {
            ReleasePending(item);
            item.Completion.TrySetResult(Error(item.OriginalId, "backend_disconnected", "Backend disconnected; dispatched outcomes are unknown and must not be replayed automatically.", "Unknown"));
        }
        connection.Stop.Cancel();
        Observe(StopChildAsync(connection));
    }

    private static async Task StopChildAsync(Connection connection)
    {
        try
        {
            connection.Process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await connection.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Only this dispatcher's exact newly-created child tree is owned;
                // the preexisting resident product host is never a descendant.
                if (!connection.Process.HasExited) connection.Process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connection.Process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        finally
        {
            try { connection.ChildExited = connection.Process.HasExited; }
            catch (InvalidOperationException) { }
            connection.Process.Dispose();
            connection.Cleanup.TrySetResult();
        }
    }

    private static JsonObject Error(JsonNode? id, string code, string message, string outcome) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = -32000, ["message"] = message,
            ["data"] = new JsonObject { ["code"] = code, ["outcome"] = outcome }
        }
    };

    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _connect.WaitAsync().ConfigureAwait(false);
        Connection? connection;
        try { connection = _connection; if (connection is not null) Close(connection); _stop.Cancel(); }
        finally { _connect.Release(); }
        if (connection is not null)
        {
            await connection.Cleanup.Task.ConfigureAwait(false);
            await Task.WhenAll(connection.Reader, connection.Diagnostics).ConfigureAwait(false);
            connection.Stop.Dispose();
        }
        _stop.Dispose();
    }

    private sealed class Connection(Process process, CancellationToken parent)
    {
        public Process Process { get; } = process;
        public object Gate { get; } = new();
        public Dictionary<string, Pending> Pending { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(parent);
        public volatile bool IsClosed;
        public Task<JsonObject> Ready { get; set; } = null!;
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task Diagnostics { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ChildExited;
    }

    private sealed class Pending
    {
        public Pending(JsonNode? originalId)
        {
            OriginalId = originalId?.DeepClone();
            Token = Stop.Token;
        }
        public JsonNode? OriginalId { get; }
        public TaskCompletionSource<JsonObject> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Stop { get; } = new();
        public CancellationToken Token { get; }
        public Task Deadline { get; set; } = Task.CompletedTask;
    }
}
