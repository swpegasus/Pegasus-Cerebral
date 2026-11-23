using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed class McpRemoteException(string message) : Exception(message);

/// <summary>A bounded, short-lived MCP stdio connection. Server text is data and never executable guidance.</summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    private readonly ManagedProcess _child;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderr;
    private int _id;
    private int _disposed;
    private int _interrupted;
    public McpServerIdentity ServerIdentity { get; private set; } = new("", "", "");
    private McpStdioClient(ManagedProcess child)
    {
        _child = child;
        _stderr = DrainErrorAsync();
    }
    public static async Task<McpStdioClient> ConnectAsync(McpRegistration registration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PortableRegistrationPolicy.RequireReady(registration);
        var client = new McpStdioClient(ManagedProcess.Start(registration.Executable, registration.Arguments, null));
        try
        {
            var result = await client.RequestAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "Pegasus Cerebral", version = ApplicationVersion.Current } }, ct);
            if (!result.TryGetProperty("protocolVersion", out var version) || version.GetString() is not ("2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"))
                throw new InvalidOperationException("MCP server negotiated an unsupported protocol version: " + (version.ValueKind == JsonValueKind.String ? version.GetString() : "missing"));
            client.ServerIdentity = new(version.GetString()!, ServerInfoText(result, "name"), ServerInfoText(result, "version"));
            await client.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyList<ToolDescriptor>> ListToolsAsync(string serverId, CancellationToken ct)
    {
        var tools = new List<ToolDescriptor>(); string? cursor = null;
        for (var page = 0; page < 8; page++)
        {
            var result = await RequestAsync("tools/list", cursor is null ? new { } : (object)new { cursor }, ct);
            if (result.TryGetProperty("tools", out var array))
                foreach (var tool in array.EnumerateArray())
                {
                    if (tools.Count >= 256) throw new InvalidOperationException("MCP tool catalog exceeds 256 tools.");
                    var name = ToolPolicy.Text(tool, "name");
                    if (name.Length > 160) throw new InvalidOperationException("MCP tool names must be bounded.");
                    tools.Add(new($"mcp.{serverId}.{name}", ToolPolicy.Text(tool, "description", "MCP tool"), tool.GetProperty("inputSchema").Clone(), $"mcp.{serverId}"));
                }
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) return tools;
        }
        throw new InvalidOperationException("MCP catalog pagination exceeds the bounded discovery limit.");
    }

    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) => RequestAsync("tools/call", new { name, arguments }, ct);

    public async Task<JsonElement> RequestAsync(string method, object arguments, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await _gate.WaitAsync(timeout.Token);
        try
        {
            if (Volatile.Read(ref _interrupted) != 0)
                throw new IOException("The MCP stdio connection was interrupted. Reconnect before making another request.");
            // Redirected Windows anonymous-pipe reads may not stop when their token is cancelled.
            // Abort only this owned transport so pending reads/writes unblock. This is not evidence
            // that an external service operation stopped; its execution receipt may remain unknown.
            using var abort = timeout.Token.Register(static state => ((McpStdioClient)state!).InterruptTransport(), this);
            timeout.Token.ThrowIfCancellationRequested();
            var id = Interlocked.Increment(ref _id);
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = arguments }, timeout.Token);
            for (var messages = 0; messages < 512; messages++)
            {
                var line = await ReadLineAsync(_child.Process.StandardOutput, timeout.Token);
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var serverRequest))
                {
                    // No sampling, roots disclosure, elicitation, or server-driven execution is implicitly authorized.
                    await SendAsync(new { jsonrpc = "2.0", id = serverRequest.Clone(), error = new { code = -32601, message = "Client capability is not enabled." } }, timeout.Token);
                    continue;
                }
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id) continue;
                if (root.TryGetProperty("error", out var error)) throw new McpRemoteException("MCP request rejected: " + ToolPolicy.Limit(error.GetRawText()).Content);
                return root.GetProperty("result").Clone();
            }
            throw new InvalidOperationException("MCP message count exceeded its bounded limit.");
        }
        catch (Exception error) when (timeout.IsCancellationRequested && error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            throw new OperationCanceledException("MCP request was interrupted; inspect any previously dispatched operation before retrying.", error, timeout.Token);
        }
        finally { _gate.Release(); }
    }

    private void InterruptTransport()
    {
        if (Interlocked.Exchange(ref _interrupted, 1) != 0) return;
        try { _child.Stop(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        var text = JsonSerializer.Serialize(message);
        if (Encoding.UTF8.GetByteCount(text) > ToolPolicy.MaxInput) throw new ArgumentException("MCP request exceeds 1 MiB.");
        await _child.Process.StandardInput.WriteLineAsync(text.AsMemory(), ct);
        await _child.Process.StandardInput.FlushAsync(ct);
    }
    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1]; var line = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (buffer[0] == '\n') return line.ToString();
            if (line.Length >= ToolPolicy.MaxInput) throw new InvalidOperationException("MCP response exceeds 1 MiB.");
            line.Append(buffer[0]);
        }
        throw new IOException("MCP server disconnected before returning a response.");
    }
    private async Task DrainErrorAsync()
    {
        var buffer = new char[2048];
        try { while (await _child.Process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token) != 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static string ServerInfoText(JsonElement initialize, string field)
    {
        if (initialize.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text is not null && text.Length <= 256 && !text.Any(char.IsControl)) return text;
        }
        return "";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var acquired = _gate.Wait(0);
        Task? output = null;
        try
        {
            if (!acquired)
            {
                await _lifetime.CancelAsync(); // Interrupt an outstanding request before closing its stdin.
                acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(1));
            }
            try { _child.Process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            if (acquired) output = DrainOnCloseAsync();
            // MCP stdio shutdown is EOF, not a kill signal. Give the owned child time to flush its store.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _child.Process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            _child.Stop(); // Bounded fallback and descendant cleanup; never target an unrelated provider process.
            await _lifetime.CancelAsync();
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { }
            if (output is not null)
                try { await output.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { }
            _child.Dispose(); _lifetime.Dispose();
            if (acquired) _gate.Dispose();
        }
    }

    private async Task DrainOnCloseAsync()
    {
        var buffer = new char[2048];
        try { while (await _child.Process.StandardOutput.ReadAsync(buffer.AsMemory(), _lifetime.Token) != 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
