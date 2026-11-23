using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>One authenticated user-session connection to the shared resident Cortex.
/// Semantic requests are never replayed. Only a read-only identity lease probe can
/// authenticate one replacement session after proven pipe loss.</summary>
internal sealed class CortexNativePipeClient(CortexNativeConnection binding) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private JsonElement _identity;
    private bool _disposed;

    // Private exception metadata carries only closed diagnostic values, never reply/credential data.
    private static readonly object HandshakeFailureKey = new();
    private enum HandshakeStage { PipeConnect, PipePeer, CredentialRead, CredentialUnprotect, CredentialSize,
        CredentialJson, CredentialFields, HelloWrite, HelloRead, HelloDecode, HelloIdentity }
    private enum HandshakeCode { IoError, InvalidData, AccessDenied, ArgumentInvalid, JsonInvalid,
        InvalidOperation, MissingField, Disposed, WindowsError, HelloShapeInvalid, HelloOkInvalid,
        HelloDeniedUnknown, ClientAuthFailed, ContractMismatch, IdentityMismatch }
    private sealed record HandshakeFailure(HandshakeStage Stage, HandshakeCode Code, int? WindowsError, int HResult);

    internal static bool HasHandshakeFailure(Exception error) => error.Data[HandshakeFailureKey] is HandshakeFailure;

    internal static string HandshakeFailureSummary(Exception error)
    {
        if (error.Data[HandshakeFailureKey] is not HandshakeFailure failure) return "";
        var windowsError = failure.WindowsError is { } code
            ? "; win32=" + code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var hresult = failure.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $" Native handshake stage={failure.Stage}; code={failure.Code}{windowsError}; hresult={hresult}.";
    }

    // Keep the original numeric failure only. Never traverse or expose exception messages.
    private static (int? WindowsError, int HResult) ClosedFailureNumbers(Exception error)
    {
        int? windowsError = error switch
        {
            System.ComponentModel.Win32Exception windows => windows.NativeErrorCode,
            IOException { InnerException: System.ComponentModel.Win32Exception windows } => windows.NativeErrorCode,
            _ => null
        };
        return (windowsError, error.HResult);
    }

    private static HandshakeCode? HelloFailureCode(JsonElement hello)
    {
        if (hello.ValueKind != JsonValueKind.Object) return HandshakeCode.HelloShapeInvalid;
        if (!hello.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return HandshakeCode.HelloOkInvalid;
        if (ok.ValueKind == JsonValueKind.True) return null;
        if (hello.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
        {
            // ValueEquals does not copy an arbitrary server-provided string into public output.
            if (code.ValueEquals("client_auth_failed")) return HandshakeCode.ClientAuthFailed;
            if (code.ValueEquals("contract_mismatch")) return HandshakeCode.ContractMismatch;
        }
        return HandshakeCode.HelloDeniedUnknown;
    }

    private static HandshakeCode ExceptionFailureCode(Exception error) => error switch
    {
        InvalidDataException => HandshakeCode.InvalidData,
        IOException => HandshakeCode.IoError,
        UnauthorizedAccessException => HandshakeCode.AccessDenied,
        ArgumentException => HandshakeCode.ArgumentInvalid,
        JsonException => HandshakeCode.JsonInvalid,
        ObjectDisposedException => HandshakeCode.Disposed,
        InvalidOperationException => HandshakeCode.InvalidOperation,
        KeyNotFoundException => HandshakeCode.MissingField,
        _ => HandshakeCode.WindowsError
    };

    public async Task<JsonElement> RequestAsync(string serverId, string action, string? tool,
        JsonElement? arguments, CancellationToken token, Action? beforeDispatch = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var reused = _pipe is not null;
            await EnsureConnectedAsync(deadline.Token);
            if (action == "identity")
            {
                if (reused) await ProveIdentityLeaseAsync(deadline.Token);
                return _identity;
            }
            var id = Guid.NewGuid().ToString("N");
            object request = action switch
            {
                "list" => new { id, op = "catalog" },
                "call" when !string.IsNullOrWhiteSpace(tool) && arguments is { ValueKind: JsonValueKind.Object } args =>
                    new { id, op = "call", name = tool, arguments = args },
                "resource" when arguments is { } resource => new { id, op = "resource", uri = resource.GetProperty("uri").GetString() },
                _ => throw new ArgumentException("Unsupported native Cortex action.")
            };
            // A cancellation after dispatch closes the stream so a late reply cannot be
            // consumed by another request. It does not assert cancellation of a committed write.
            using var close = deadline.Token.Register(() => _pipe?.Dispose());
            await CortexNativeProtocol.WriteAsync(_pipe!, request, CortexNativeProtocol.RequestBytes, deadline.Token, beforeDispatch);
            var reply = await CortexNativeProtocol.ReadAsync(_pipe!, CortexNativeProtocol.ResponseBytes, deadline.Token);
            RequireReply(reply, id);
            if (action == "list")
            {
                if (!reply.GetProperty("ok").GetBoolean()) throw new UnauthorizedAccessException("Native Cortex catalog was denied; inspect its local client enrollment.");
                // Keep provider metadata in its original representation across the relay.
                // Alias expansion happens locally, after transport bounds have been checked.
                return reply.GetProperty("result").Clone();
            }
            if (action == "resource") return reply;
            // Preserve typed receipts for existing scope/commit/effect validators. Only this
            // in-process projection uses structuredContent; the native wire carries result/error.
            if (!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("error").ValueKind == JsonValueKind.Object &&
                reply.GetProperty("error").TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is "client_auth_failed" or "client_scope_denied" or "client_revoked_or_expired")
                Retire();
            return reply.GetProperty("ok").GetBoolean()
                ? CerebralJson.Element(new { isError = false, structuredContent = reply.GetProperty("result") })
                : CerebralJson.Element(new { isError = true, structuredContent = reply.GetProperty("error") });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Retire();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (error is OperationCanceledException) throw new IOException("Native Cortex request timed out. Its outcome may be unknown; no request was replayed.");
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task ProveIdentityLeaseAsync(CancellationToken token)
    {
        try { await PingLeaseAsync(token); }
        catch (IOException error) when (!token.IsCancellationRequested &&
            (error is EndOfStreamException || (error.HResult & 0xffff) is 109 or 232 or 233 || _pipe is { IsConnected: false }))
        {
            // This fixed ping cannot perform semantic work. Retry only its lost session,
            // once, with normal executable/session/credential admission on the fresh pipe.
            // A rejected lease, malformed reply or cancelled probe never reaches this path.
            Retire();
            token.ThrowIfCancellationRequested();
            await EnsureConnectedAsync(token);
            await PingLeaseAsync(token);
        }
    }

    private async Task PingLeaseAsync(CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        using var close = token.Register(() => _pipe?.Dispose());
        await CortexNativeProtocol.WriteAsync(_pipe!, new { id, op = "ping" }, CortexNativeProtocol.RequestBytes, token);
        var reply = await CortexNativeProtocol.ReadAsync(_pipe!, CortexNativeProtocol.ResponseBytes, token);
        RequireReply(reply, id);
        if (!reply.GetProperty("ok").GetBoolean())
            throw new UnauthorizedAccessException("Native Cortex refused the current authenticated lease check. Inspect its existing client enrollment; no semantic operation was replayed.");
        var result = reply.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("state", out var state) ||
            state.ValueKind != JsonValueKind.String || state.GetString() != "ready")
            throw new InvalidDataException("Native Cortex returned an incompatible lease-check reply. No semantic operation was replayed.");
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_pipe is not null) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Cortex uses a local Windows owner-session pipe.");
        binding.Validate();
        if (binding.Relay is not null) throw new ArgumentException("The Cortex native client cannot recurse through a relay.");
        ToolPolicy.RejectLinks(binding.HostExecutable);
        if (!File.Exists(binding.HostExecutable)) throw new FileNotFoundException("The enrolled Cortex resident host executable is unavailable.");
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        if (owner.SessionId != binding.SessionId || owner.AccountSid is InstalledWorkerAdmission.SystemSid or InstalledWorkerBinding.LocalServiceSid)
            throw new UnauthorizedAccessException("Cortex native access requires its enrolled signed-in owner session. Use the user connector from a service.");
        NamedPipeClientStream? pipe = null;
        var stage = HandshakeStage.PipeConnect;
        HandshakeCode? helloFailure = null;
        try
        {
            pipe = await InstalledWorkerWindows.ConnectClientAsync(binding.PipeName ?? CortexNativeProtocol.PipeName(binding.SessionId), token);
            stage = HandshakeStage.PipePeer;
            var observed = InstalledWorkerWindows.ObserveServerProcess(pipe);
            if (observed.AccountSid != owner.AccountSid || observed.SessionId != owner.SessionId ||
                !Path.GetFullPath(observed.Executable).Equals(Path.GetFullPath(binding.HostExecutable), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Native Cortex pipe server does not match the enrolled executable, owner and session. No credential was sent.");
            stage = HandshakeStage.CredentialRead;
            var credentialLocation = CortexManagedCredentialLocator.Resolve(binding, owner.AccountSid);
            ToolPolicy.RejectLinks(credentialLocation.EffectivePath);
            var protectedBytes = await ReadCredentialAsync(credentialLocation.EffectivePath, token);
            byte[]? clear = null;
            try
            {
                stage = HandshakeStage.CredentialUnprotect;
                clear = WindowsDataProtection.Unprotect(protectedBytes);
                stage = HandshakeStage.CredentialSize;
                if (clear.Length is < 1 or > 4096) throw new InvalidDataException("Native Cortex protected credential is invalid.");
                stage = HandshakeStage.CredentialJson;
                using var document = ReadCredentialDocument(clear);
                var credential = document.RootElement;
                stage = HandshakeStage.CredentialFields;
                RequireCredential(credential);
                var clientId = credential.GetProperty("clientId").GetString()!;
                CortexManagedCredentialLocator.RequireManagedClientId(credentialLocation, clientId);
                stage = HandshakeStage.HelloWrite;
                await CortexNativeProtocol.WriteAsync(pipe, new { op = "hello", contract = CortexNativeProtocol.Contract,
                    clientId, keyId = credential.GetProperty("keyId").GetString(), apiKey = credential.GetProperty("apiKey").GetString() },
                    CortexNativeProtocol.HelloBytes, token);
                stage = HandshakeStage.HelloRead;
                var hello = await CortexNativeProtocol.ReadAsync(pipe, CortexNativeProtocol.ResponseBytes, token);
                helloFailure = HelloFailureCode(hello);
                stage = HandshakeStage.HelloDecode;
                var identity = hello.Deserialize<CortexNativeIdentity>(CerebralJson.Options);
                stage = HandshakeStage.HelloIdentity;
                if (!hello.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True || identity is null ||
                    identity.Contract != CortexNativeProtocol.Contract || identity.Product != CortexConnectionContract.Product ||
                    string.IsNullOrWhiteSpace(identity.Version) || identity.Version.Length > 256 || identity.Version.Any(char.IsControl) ||
                    identity.OwnerSid != owner.AccountSid || identity.Session != owner.SessionId || identity.ClientId != clientId ||
                    identity.State != "ready" || identity.StorageKind != "sqlite-v2" || !hello.TryGetProperty("network", out var network) || network.ValueKind != JsonValueKind.False ||
                    identity.MaximumRequestBytes != CortexNativeProtocol.RequestBytes ||
                    identity.MaximumResponseBytes != CortexNativeProtocol.ResponseBytes || identity.MaximumConnections is < 1 or > 24)
                {
                    helloFailure ??= HandshakeCode.IdentityMismatch;
                    throw new UnauthorizedAccessException("Native Cortex did not confirm the compatible authenticated client and server identity.");
                }
                _identity = hello; _pipe = pipe; pipe = null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            // Keep the original exception/stack and all existing retirement/cancellation behavior.
            var (windowsError, hresult) = ClosedFailureNumbers(error);
            error.Data[HandshakeFailureKey] = new HandshakeFailure(stage, helloFailure ?? ExceptionFailureCode(error),
                windowsError, hresult);
            throw;
        }
        finally { pipe?.Dispose(); }
    }

    private static async Task<byte[]> ReadCredentialAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 4096) throw new InvalidDataException("Native Cortex credential file is invalid.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token); return bytes;
    }

    private static void RequireCredential(JsonElement credential)
    {
        if (credential.ValueKind != JsonValueKind.Object || credential.EnumerateObject().Count() != 3 ||
            credential.EnumerateObject().Any(x => x.Name is not ("clientId" or "keyId" or "apiKey") || x.Value.ValueKind != JsonValueKind.String) ||
            credential.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Native Cortex credential shape is invalid.");
        var id = credential.GetProperty("clientId").GetString()!;
        var keyId = credential.GetProperty("keyId").GetString()!;
        var key = credential.GetProperty("apiKey").GetString()!;
        if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            keyId.Length != 32 || keyId.Any(c => !char.IsAsciiHexDigit(c)) || key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new InvalidDataException("Native Cortex credential fields are invalid.");
    }

    private static JsonDocument ReadCredentialDocument(byte[] clear)
    {
        try { return JsonDocument.Parse(clear, new() { MaxDepth = 8 }); }
        // Never expose a parser excerpt or inner exception from decrypted credential data.
        catch (JsonException) { throw new InvalidDataException("Native Cortex protected credential is not valid JSON. Re-enroll the local client through Cortex."); }
    }

    private static void RequireReply(JsonElement reply, string id)
    {
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.String || replyId.GetString() != id ||
            !reply.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !reply.TryGetProperty(ok.GetBoolean() ? "result" : "error", out _))
            throw new InvalidDataException("Native Cortex response does not match the outstanding request. No request was replayed.");
    }

    internal static List<ToolDescriptor> ParseCatalog(string serverId, JsonElement catalog)
    {
        var operations = catalog.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() > 256)
            throw new InvalidDataException("Native Cortex catalog exceeds its operation bounds.");
        var tools = new List<ToolDescriptor>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in operations.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (name is null || name.Length is < 1 or > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) || !seen.Add(name))
                throw new InvalidDataException("Native Cortex catalog contains invalid or duplicate operation names.");
            var description = entry.TryGetProperty("description", out var text) ? text.GetString() ?? "" : "";
            var schema = entry.GetProperty("inputSchema");
            if (description.Length > 32768 || schema.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Native Cortex operation metadata is invalid.");
            // These aliases preserve existing grants and saved context; they are not MCP wire messages.
            tools.Add(new("mcp." + serverId + "." + name, description, schema.Clone(), "mcp." + serverId));
        }
        return tools;
    }

    private void Retire() { _pipe?.Dispose(); _pipe = null; _identity = default; }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; Retire(); }
        finally { _gate.Release(); }
    }
}
