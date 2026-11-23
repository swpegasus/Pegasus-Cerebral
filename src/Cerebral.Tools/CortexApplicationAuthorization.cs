using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Tools;

/// <summary>
/// Owns the private service-to-service PAO session. Credential material is issued and
/// consumed only on this boundary; models, agent tool arguments, HTTP commands, and
/// activity records never receive it.
/// </summary>
public sealed class CortexApplicationAuthorization : IAsyncDisposable
{
    public const string PipeName = "pegasus-cortex-application-system-v1";
    public const string ApplicationId = "pegasus-cerebral";
    private const int ProtocolMajor = 1;
    private const int ProtocolMinor = 0;
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Pegasus Cerebral application authorization v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly bool _productionTransport;
    private readonly string? _credentialPath;
    private Stream? _session;
    private string? _credentialId;
    private long? _generation;
    private DateTimeOffset? _enabledAt;

    public CortexApplicationAuthorization(string? stateDirectory, Func<CancellationToken, Task<Stream>>? connect = null)
    {
        _credentialPath = string.IsNullOrWhiteSpace(stateDirectory)
            ? null
            : Path.Combine(Path.GetFullPath(stateDirectory), "cerebral-application-authorization.dat");
        _connect = connect ?? ConnectPipeAsync;
        _productionTransport = connect is null;
    }

    /// <summary>Local eligibility only; Cortex still verifies the live SCM binding and signed publisher.</summary>
    public string? UnavailableReason => _productionTransport ? ProductionUnavailableReason() : null;

    public bool Configured => UnavailableReason is null &&
        (_session is not null || _credentialPath is not null && File.Exists(_credentialPath));

    public FullAccessSnapshot Snapshot()
    {
        var remembered = _credentialPath is not null && File.Exists(_credentialPath);
        var unavailable = UnavailableReason;
        if (unavailable is not null) return new(false, remembered, null, null, null, unavailable);
        var active = _session is not null || remembered;
        return new(active, remembered, _credentialId, _generation, _enabledAt,
            active
                ? _session is not null
                    ? "Pegasus Cerebral Full Access is connected through the private Cortex system-service pipe."
                    : "Pegasus Cerebral Full Access is protected for service restarts; the private Cortex pipe connects when PAO is needed."
                : "Pegasus Cerebral Full Access is off.");
    }

    public async Task<FullAccessSnapshot> EnableAsync(CancellationToken cancellationToken)
    {
        EnsureWindowsAndState();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null) return Snapshot();
            if (File.Exists(_credentialPath!))
            {
                try
                {
                    await ConnectSavedAsync(cancellationToken);
                    return Snapshot();
                }
                catch (UnauthorizedAccessException)
                {
                    // The server may have been reinstalled or the application authorization
                    // revoked. Rotate through the independently authenticated service pipe.
                }
            }

            var issued = await RequestAsync("issue", null, cancellationToken);
            if (!issued.Ok && issued.Err == "precondition") issued = await RequestAsync("rotate", null, cancellationToken);
            RequireSuccess(issued, "Cortex could not issue the Cerebral application authorization.");
            PersistIssuedCredential(issued);
            await ConnectSavedAsync(cancellationToken);
            return Snapshot();
        }
        finally { _gate.Release(); }
    }

    public async Task<FullAccessSnapshot> RotateAsync(CancellationToken cancellationToken)
    {
        EnsureWindowsAndState();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisposeSessionAsync();
            var issued = await RequestAsync("rotate", null, cancellationToken);
            if (!issued.Ok && issued.Err == "precondition") issued = await RequestAsync("issue", null, cancellationToken);
            RequireSuccess(issued, "Cortex could not rotate the Cerebral application authorization.");
            PersistIssuedCredential(issued);
            await ConnectSavedAsync(cancellationToken);
            return Snapshot();
        }
        finally { _gate.Release(); }
    }

    public async Task<FullAccessSnapshot> DisableAsync(CancellationToken cancellationToken)
    {
        EnsureWindowsAndState();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisposeSessionAsync();
            var revoked = await RequestAsync("revoke", null, cancellationToken);
            RequireSuccess(revoked, "Cortex could not revoke the Cerebral application authorization.");
            if (File.Exists(_credentialPath!)) File.Delete(_credentialPath!);
            _credentialId = null;
            _generation = null;
            _enabledAt = null;
            return Snapshot();
        }
        finally { _gate.Release(); }
    }

    public Task<JsonElement> ExecuteUnifiedAsync(JsonElement arguments, string? defaultWorkingDirectory,
        CancellationToken cancellationToken) =>
        ExecuteUnifiedAsync(arguments, defaultWorkingDirectory, cancellationToken, beforeDispatch: null);

    public async Task<JsonElement> ExecuteUnifiedAsync(JsonElement arguments, string? defaultWorkingDirectory,
        CancellationToken cancellationToken, Action? beforeDispatch)
    {
        EnsureWindowsAndState();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var request = UnifiedPaoRequest.From(arguments, defaultWorkingDirectory);
            try
            {
                // Callers may recheck mutable host admission after connection acquisition and
                // immediately before the first frame can reach the authorized application.
                beforeDispatch?.Invoke();
                await WriteFrameAsync(_session!, request, cancellationToken);
                return await ReadFrameAsync<JsonElement>(_session!, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or JsonException)
            {
                // Submission may have reached Cortex. Drop the session, but never replay an
                // operation automatically when its effects are uncertain.
                await DisposeSessionAsync();
                throw new IOException("The Cortex application pipe disconnected; the PAO outcome may require inspection before retrying.", ex);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        EnsureWindowsAndState();
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureConnectedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null) return;
        if (!File.Exists(_credentialPath!)) throw new UnauthorizedAccessException("Pegasus Cerebral Full Access is not enabled.");
        await ConnectSavedAsync(cancellationToken);
    }

    private async Task ConnectSavedAsync(CancellationToken cancellationToken)
    {
        var protectedBytes = await File.ReadAllBytesAsync(_credentialPath!, cancellationToken);
        var credentialBytes = WindowsDataProtection.Unprotect(protectedBytes, Entropy);
        try
        {
            var credential = Encoding.UTF8.GetString(credentialBytes);
            var pipe = await _connect(cancellationToken);
            try
            {
                await WriteFrameAsync(pipe, new AuthorizationRequest(1, ApplicationId, "connect", credential), cancellationToken);
                var result = await ReadFrameAsync<AuthorizationResult>(pipe, cancellationToken);
                RequireSuccess(result, "Cortex rejected the protected Cerebral application authorization.");
                if (result.State != "ready") throw new UnauthorizedAccessException("Cortex did not establish a ready Cerebral application session.");
                _session = pipe;
                _credentialId = result.CredentialId;
                _generation = result.Generation;
                _enabledAt ??= File.GetLastWriteTimeUtc(_credentialPath!);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(credentialBytes); }
    }

    private async Task<AuthorizationResult> RequestAsync(string action, string? credential, CancellationToken cancellationToken)
    {
        await using var pipe = await _connect(cancellationToken);
        await WriteFrameAsync(pipe, new AuthorizationRequest(1, ApplicationId, action, credential), cancellationToken);
        return await ReadFrameAsync<AuthorizationResult>(pipe, cancellationToken);
    }

    private void PersistIssuedCredential(AuthorizationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Credential) || result.Credential.Length is < 40 or > 128)
            throw new InvalidDataException("Cortex returned an invalid application credential.");
        Directory.CreateDirectory(Path.GetDirectoryName(_credentialPath!)!);
        var credentialBytes = Encoding.UTF8.GetBytes(result.Credential);
        try
        {
            var protectedBytes = WindowsDataProtection.Protect(credentialBytes, Entropy);
            var temporary = _credentialPath + ".new";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _credentialPath!, true);
        }
        finally { CryptographicOperations.ZeroMemory(credentialBytes); }
        _credentialId = result.CredentialId;
        _generation = result.Generation;
        _enabledAt = DateTimeOffset.UtcNow;
    }

    private static void RequireSuccess(AuthorizationResult result, string message)
    {
        if (!result.Ok) throw new UnauthorizedAccessException(message + (string.IsNullOrWhiteSpace(result.Err) ? "" : $" ({result.Err})"));
    }

    private void EnsureWindowsAndState()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex application authorization requires the Windows system service.");
        if (_credentialPath is null) throw new InvalidOperationException("Cerebral application authorization requires the Worker service data directory.");
        if (UnavailableReason is { } unavailable) throw new UnauthorizedAccessException(unavailable);
    }

    private static string? ProductionUnavailableReason()
    {
        if (!OperatingSystem.IsWindows())
            return "Full Access requires the installed PegasusCerebral Windows service Worker.";
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        // This preflight explains an unsupported user/source Worker. It is not proof
        // of enrollment: the Cortex broker owns the authoritative SCM and signature checks.
        return identity.User?.IsWellKnown(WellKnownSidType.LocalServiceSid) == true && process.SessionId == 0
            ? null
            : "Full Access requires the installed PegasusCerebral Windows service Worker running as LocalService. " +
              "It is unavailable for this Source or user-session Worker. Ordinary user PAO operations and Cortex references remain available.";
    }

    private static async Task<Stream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Cortex application frame is too large.");
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), ProtocolMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), ProtocolMinor);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (major != ProtocolMajor || minor > ProtocolMinor) throw new InvalidDataException("Cortex application protocol version is incompatible.");
        if (length is < 1 or > MaxFrameBytes) throw new InvalidDataException("Cortex application frame size is invalid.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        try { return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Cortex returned an empty application frame."); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is null) return;
        await _session.DisposeAsync();
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await DisposeSessionAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private sealed record AuthorizationRequest(int Version, string ApplicationId, string Action, string? Credential = null);
    private sealed record AuthorizationResult(bool Ok, string State, string? Err = null, string? ApplicationId = null,
        string? Label = null, string? CredentialId = null, long? Generation = null, string? Credential = null, string? SessionId = null);
}

public sealed record FullAccessSnapshot(bool Active, bool Remembered, string? CredentialId, long? Generation,
    DateTimeOffset? EnabledAt, string Message);

internal static class UnifiedPaoRequest
{
    private sealed record Route(string Operation, string? SubOperation = null, string? Mode = null);

    private static readonly IReadOnlyDictionary<string, Route> Routes = new Dictionary<string, Route>(StringComparer.Ordinal)
    {
        ["ctx_ls"] = new("ctx", "ls"), ["ctx_cur"] = new("ctx", "cur"), ["ctx_add"] = new("ctx"),
        ["stat"] = new("stat"), ["ls"] = new("ls"), ["find"] = new("find"), ["search"] = new("search"),
        ["head"] = new("slice", Mode: "head"), ["tail"] = new("slice", Mode: "tail"), ["diff"] = new("diff"),
        ["count"] = new("count"), ["realpath"] = new("path", "realpath"), ["basename"] = new("path", "basename"), ["dirname"] = new("path", "dirname"),
        ["sort"] = new("lines", "sort"), ["uniq"] = new("lines", "uniq"), ["du"] = new("disk", "du"), ["df"] = new("disk", "df"),
        ["rd"] = new("rd"), ["read_lines"] = new("read_lines"), ["encoding_inspect"] = new("encoding", "inspect"), ["encoding_convert"] = new("encoding", "convert"),
        ["wr"] = new("wr", "write"), ["replace"] = new("wr", "replace"), ["edit"] = new("edit"), ["patch"] = new("patch"),
        ["mkdir"] = new("mkdir"), ["cp"] = new("cp"), ["mv"] = new("mv"), ["rm"] = new("rm"), ["sha"] = new("sha"),
        ["zip_create"] = new("zip", "create"), ["zip_list"] = new("zip", "list"), ["zip_test"] = new("zip", "test"), ["zip_extract"] = new("zip", "extract"),
        ["7z_list"] = new("7z", "list"), ["7z_test"] = new("7z", "test"), ["7z_extract"] = new("7z", "extract"),
        ["run"] = new("run"), ["pwsh"] = new("pwsh"),
        ["app_start"] = new("app", "start"), ["app_status"] = new("app", "status"), ["app_stop"] = new("app", "stop"),
        ["git_status"] = new("git", "status"), ["git_diff"] = new("git", "diff"), ["git_log"] = new("git", "log"), ["git_show"] = new("git", "show"), ["git_rev_parse"] = new("git", "rev-parse"),
        ["dotnet_build"] = new("dotnet", "build"), ["dotnet_test"] = new("dotnet", "test"), ["dotnet_restore"] = new("dotnet", "restore"),
        ["dotnet_clean"] = new("dotnet", "clean"), ["dotnet_pack"] = new("dotnet", "pack"), ["dotnet_publish"] = new("dotnet", "publish"),
        ["proc_ls"] = new("proc", "ls"), ["proc_inspect"] = new("proc", "inspect"), ["proc_wait"] = new("proc", "wait"), ["proc_stop"] = new("proc", "stop"), ["proc_tree"] = new("proc", "tree"),
        ["net_list"] = new("net", "list"), ["job_stat"] = new("job", "stat"), ["job_wait"] = new("job", "wait"), ["job_ls"] = new("job", "ls"),
        ["job_cancel"] = new("job", "cancel"), ["job_retry"] = new("job", "retry"), ["job_log"] = new("job", "log"),
        ["feedback_submit"] = new("feedback", "submit"), ["feedback_ls"] = new("feedback", "ls"), ["feedback_get"] = new("feedback", "get"),
        ["feedback_set"] = new("feedback", "set"), ["feedback_export"] = new("feedback", "export"),
        ["tool_ls"] = new("tool", "ls"), ["recipe_ls"] = new("recipe", "ls"),
        ["ui_status"] = new("ui", "status"), ["ui_find"] = new("ui", "find"), ["ui_window"] = new("ui", "window"), ["ui_control"] = new("ui", "control"),
        ["ui_activate"] = new("ui", "activate"), ["ui_move"] = new("ui", "move"), ["ui_focus"] = new("ui", "focus"), ["ui_click"] = new("ui", "click"),
        ["ui_set_text"] = new("ui", "set_text"), ["ui_capture"] = new("ui", "capture"),
        ["nsis_status"] = new("nsis", "status"), ["nsis_validate"] = new("nsis", "validate"), ["nsis_build"] = new("nsis", "build"),
        ["do"] = new("do"), ["diag"] = new("diag"), ["help"] = new("help")
    };

    public static object From(JsonElement arguments, string? defaultWorkingDirectory)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("op", out var selector) || selector.ValueKind != JsonValueKind.String ||
            selector.GetString() is not { } publicOperation || !Routes.TryGetValue(publicOperation, out var route))
            throw new ArgumentException("PAO requires a recognized operation.");
        if (arguments.TryGetProperty("root", out _) || arguments.TryGetProperty("root_code", out _) || arguments.TryGetProperty("credential", out _) ||
            route.Operation is "root" or "queue")
            throw new UnauthorizedAccessException("Authorization material and PAO administration are not admitted in application operation frames.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? body = null, rid = null, ws = null, cwd = null;
        var detail = false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is "op" or "output") continue;
            if (property.Name is "root" or "root_code" or "credential" or "pathRoots")
                throw new UnauthorizedAccessException("Authorization fields are owned by the Cerebral service boundary.");
            if (property.Name is "text" or "vars")
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new ArgumentException($"PAO field '{property.Name}' must be text.");
                body = property.Value.GetString();
                continue;
            }
            if (property.Name == "arg")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 256) throw new ArgumentException("PAO arg must be a bounded string array.");
                var index = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("PAO arg entries must be text.");
                    values[$"arg{index++}"] = item.GetString()!;
                }
                continue;
            }
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => throw new ArgumentException($"PAO field '{property.Name}' has an unsupported value.")
            };
            switch (property.Name)
            {
                case "rid": rid = value; break;
                case "ws": ws = value; break;
                case "cwd": cwd = value; break;
                case "detail": detail = property.Value.GetBoolean(); break;
                default: values[property.Name] = value; break;
            }
        }

        if (publicOperation == "ctx_add")
        {
            if (!values.Remove("name", out var name) || !values.Remove("src", out var path)) throw new ArgumentException("ctx_add requires name and src.");
            values["register"] = name + ":" + path;
        }
        if (route.SubOperation is not null) values["op"] = route.SubOperation;
        if (route.Mode is not null) values["mode"] = route.Mode;
        if (body is not null && Encoding.UTF8.GetByteCount(body) > 1024 * 1024) throw new ArgumentException("PAO body exceeds 1 MiB.");
        cwd ??= defaultWorkingDirectory;
        return new { op = route.Operation, arg = values, rid, ws, cwd, root = (string?)null, quiet = false, detail, pathRoots = (object?)null, body };
    }
}
