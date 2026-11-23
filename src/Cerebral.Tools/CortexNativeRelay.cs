using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Tools;

internal static class CortexNativeRelayClient
{
    public static async Task<JsonElement> RequestAsync(McpRegistration server, string key, string action,
        string? tool, JsonElement? arguments, CancellationToken token, Action? beforeDispatch)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex relay requires a local Windows pipe.");
        var relay = server.CortexNative?.Relay ?? throw new ArgumentException("The Cortex user relay has not been provisioned.");
        relay.Validate(); ConnectorProtocol.ValidateKey(key);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(65));
        using var pipe = await InstalledWorkerWindows.ConnectClientAsync(relay.PipeName, deadline.Token);
        var observed = InstalledWorkerWindows.ObserveServer(pipe);
        InstalledWorkerWindows.RequirePipeSecurity(observed.Protection, relay.OwnerSid, InstalledWorkerBinding.LocalServiceSid);
        if (observed.Process.AccountSid != relay.OwnerSid || observed.Process.SessionId != relay.SessionId ||
            !Path.GetFullPath(observed.Process.Executable).Equals(Path.GetFullPath(relay.Executable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Cortex user relay identity changed. No credential or operation was sent.");
        var id = Guid.NewGuid().ToString("N");
        using var close = deadline.Token.Register(pipe.Dispose);
        await CortexNativeProtocol.WriteAsync(pipe, new { version = 1, id, key, serverId = server.Id,
            expectedExecutionFingerprint = ConnectorProtocol.ExecutionFingerprint(server), action, tool, arguments },
            CortexNativeProtocol.RequestBytes, deadline.Token, beforeDispatch);
        var reply = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.RelayResponseBytes, deadline.Token);
        if (!reply.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
            !reply.TryGetProperty("id", out var returnedId) || returnedId.GetString() != id ||
            !reply.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Cortex relay response is incompatible or belongs to a different request.");
        if (!ok.GetBoolean())
        {
            var code = reply.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
            if (code == "denied") throw new UnauthorizedAccessException("Cortex user relay denied its exact registration or authentication. No alternate adapter will be used.");
            throw new IOException("Cortex user relay could not confirm the request. Its outcome may be unknown; no operation was replayed.");
        }
        return reply.GetProperty("result").Clone();
    }
}

internal static class CortexNativeRelayServer
{
    public static async Task RunAsync(ConnectorConfiguration configuration, string key,
        IReadOnlyDictionary<string, CortexNativePipeClient> clients, SemaphoreSlim capacity, CancellationToken token)
    {
        if (configuration.CortexRelay is not { } relay) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex relay requires Windows.");
        relay.Validate();
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        if (owner.AccountSid != relay.OwnerSid || owner.SessionId != relay.SessionId ||
            !Path.GetFullPath(owner.Executable).Equals(Path.GetFullPath(relay.Executable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The user connector does not match its Cortex relay binding.");
        var sessions = new List<Task>(); var first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = InstalledWorkerWindows.CreateServer(relay.PipeName, relay.OwnerSid, InstalledWorkerBinding.LocalServiceSid, first);
                first = false;
                try { await pipe.WaitForConnectionAsync(token); }
                catch { pipe.Dispose(); throw; }
                if (!capacity.Wait(0)) { pipe.Dispose(); continue; }
                sessions.RemoveAll(task => task.IsCompleted);
                sessions.Add(ServeAsync(pipe, relay, key, configuration, clients, capacity, token));
            }
        }
        finally { await Task.WhenAll(sessions); }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, CortexNativeRelay relay, string key,
        ConnectorConfiguration configuration, IReadOnlyDictionary<string, CortexNativePipeClient> clients,
        SemaphoreSlim capacity, CancellationToken lifetime)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            using var close = deadline.Token.Register(pipe.Dispose);
            string? id = null;
            Task<int>? disconnected = null;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Cortex relay requires Windows.");
                // Gather pipe PID/session before reading private request data. An installed
                // LocalService caller is identified independently through its fixed SCM entry.
                var endpoint = InstalledWorkerWindows.ObserveClientEndpoint(pipe);
                if (endpoint.SessionId == relay.SessionId)
                {
                    var peer = InstalledWorkerWindows.ObserveClient(pipe).Process;
                    if (peer.AccountSid != relay.OwnerSid || !Path.GetFullPath(peer.Executable).Equals(Path.GetFullPath(relay.Executable), StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException();
                }
                else
                {
                    var service = InstalledWorkerWindows.ReadService();
                    if (endpoint.SessionId != 0 || endpoint.ProcessId != service.ProcessId || service.State != InstalledWorkerServiceState.Running ||
                        !Path.GetFullPath(service.Executable).Equals(Path.GetFullPath(relay.Executable), StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException();
                }
                var request = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.RequestBytes, deadline.Token);
                var caller = InstalledWorkerWindows.ReadAuthenticatedCaller(pipe);
                if (!caller.Authenticated || caller.Anonymous || caller.NetworkLogon ||
                    (caller.UserSid != relay.OwnerSid || caller.SessionId != relay.SessionId) &&
                    (caller.UserSid != InstalledWorkerBinding.LocalServiceSid || caller.SessionId != 0))
                    throw new UnauthorizedAccessException();
                id = RequiredText(request, "id");
                if (id.Length > 64 || request.GetProperty("version").GetInt32() != 1 ||
                    request.EnumerateObject().Any(x => x.Name is not ("version" or "id" or "key" or "serverId" or "expectedExecutionFingerprint" or "action" or "tool" or "arguments")))
                    throw new UnauthorizedAccessException();
                var supplied = RequiredText(request, "key");
                if (supplied.Length != key.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(key)))
                    throw new UnauthorizedAccessException();
                var serverId = RequiredText(request, "serverId");
                var server = configuration.McpServers.SingleOrDefault(x => x.Id == serverId && x.CortexNative is not null)
                    ?? throw new UnauthorizedAccessException();
                if (RequiredText(request, "expectedExecutionFingerprint") != ConnectorProtocol.ExecutionFingerprint(server) || !clients.TryGetValue(serverId, out var client))
                    throw new UnauthorizedAccessException();
                var action = RequiredText(request, "action");
                var tool = request.TryGetProperty("tool", out var toolValue) && toolValue.ValueKind == JsonValueKind.String ? toolValue.GetString() : null;
                JsonElement? arguments = request.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null ? args : null;
                if (action == "call")
                {
                    var list = await client.RequestAsync(serverId, "list", null, null, deadline.Token);
                    var descriptor = CortexNativePipeClient.ParseCatalog(serverId, list).SingleOrDefault(x => x.Name == $"mcp.{serverId}.{tool}")
                        ?? throw new UnauthorizedAccessException();
                    ToolDispatcher.ValidateSchema(arguments ?? throw new ArgumentException(), descriptor.InputSchema);
                }
                // Observe disconnects during a slow operation. Cancellation retires the native
                // session, but cannot undo a committed effect and never resubmits it.
                var extra = new byte[1]; disconnected = pipe.ReadAsync(extra, deadline.Token).AsTask();
                var executing = client.RequestAsync(serverId, action, tool, arguments, deadline.Token);
                if (await Task.WhenAny(executing, disconnected) == disconnected)
                {
                    deadline.Cancel();
                    try { await executing; } catch (Exception) { }
                    return;
                }
                var result = await executing;
                await CortexNativeProtocol.WriteAsync(pipe, new { version = 1, id, ok = true, result }, CortexNativeProtocol.RelayResponseBytes, deadline.Token);
                deadline.Cancel();
                try { await disconnected; } catch (Exception) { }
            }
            catch (Exception error)
            {
                if (id is not null && !deadline.IsCancellationRequested)
                    try { await CortexNativeProtocol.WriteAsync(pipe, new { version = 1, id, ok = false, code = error is UnauthorizedAccessException ? "denied" : "unconfirmed" }, CortexNativeProtocol.ResponseBytes, deadline.Token); }
                    catch (Exception) { }
            }
            finally
            {
                deadline.Cancel();
                if (disconnected is not null) try { await disconnected; } catch (Exception) { }
                capacity.Release();
            }
        }
    }
    private static string RequiredText(JsonElement request, string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : throw new UnauthorizedAccessException();
}
