using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Client;

/// <summary>Native observations and temporary pipe primitives, not application authorization.
/// Production callers must obtain identities from protected enrollment and apply the admission policy.
/// Nothing here enrolls an owner, alters a service, changes privileges or falls back to another transport.</summary>
[SupportedOSPlatform("windows")]
public static class InstalledWorkerWindows
{
    // Individual rights deliberately exclude FILE_APPEND_DATA/FILE_CREATE_PIPE_INSTANCE (0x4).
    // GENERIC_WRITE includes that bit and would let a client create another server instance.
    public const uint ClientPipeAccess = 0x0012019b;
    public const uint ServicePipeAccess = 0x001f01ff;
    private const int MaximumSecurityBytes = 65536;
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>Reads only the fixed Cerebral SCM entry. Its command line is parsed, never executed.</summary>
    public static InstalledWorkerServiceIdentity ReadService()
    {
        using var manager = OpenSCManager(null, null, 0x0001);
        if (manager.IsInvalid) throw NativeFailure("The Windows service manager could not be queried.");
        using var service = OpenService(manager, InstalledWorkerBinding.ExpectedServiceName, 0x0001 | 0x0004);
        if (service.IsInvalid) throw NativeFailure("The installed Cerebral service could not be queried.");
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw NativeFailure("The installed Cerebral service status could not be read.");
        if (status.ServiceType != 0x10)
            throw new UnauthorizedAccessException("Installed Cerebral requires a dedicated Windows service process.");
        var state = status.CurrentState switch
        {
            1 => InstalledWorkerServiceState.Stopped,
            2 => InstalledWorkerServiceState.StartPending,
            3 => InstalledWorkerServiceState.StopPending,
            4 => InstalledWorkerServiceState.Running,
            _ => throw new UnauthorizedAccessException("The installed Cerebral service is not in a supported connection state.")
        };
        if (QueryServiceConfig(service, IntPtr.Zero, 0, out var bytes) || Marshal.GetLastWin32Error() != 122 || bytes is < 40 or > 8192)
            throw new InvalidDataException("The installed service configuration is unavailable or exceeds its supported bound.");
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytes, out _))
                throw NativeFailure("The installed Cerebral service configuration could not be read.");
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            if (config.ServiceType != 0x10)
                throw new UnauthorizedAccessException("Installed Cerebral requires a dedicated Windows service process.");
            var account = BoundedUnicode(config.StartName, buffer, bytes);
            if (!account.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The installed Cerebral service does not use the enrolled LocalService account.");
            var (executable, data, installedPipe) = ParseServiceCommandLine(BoundedUnicode(config.BinaryPath, buffer, bytes));
            if (!installedPipe)
                throw new UnauthorizedAccessException("The installed Cerebral service is not configured for the local owner pipe.");
            if (status.ProcessId > int.MaxValue) throw new InvalidDataException("The service process identity is invalid.");
            return new(InstalledWorkerBinding.ExpectedServiceName, InstalledWorkerBinding.LocalServiceSid,
                executable, data, (int)status.ProcessId, state);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static InstalledWorkerProcessIdentity ReadCurrentProcessIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("The current process has no Windows account identity.");
        var executable = Environment.ProcessPath ?? throw new InvalidDataException("The current process executable is unavailable.");
        return new(Environment.ProcessId, sid, executable, process.SessionId);
    }

    /// <summary>Creates an overlapped local-only server with the exact protected service/owner ACL.
    /// The supplied SIDs describe object access only; this method does not authenticate a service.</summary>
    public static NamedPipeServerStream CreateServer(string pipeName, string serviceSid, string ownerSid,
        bool first = true, int maximumInstances = 9)
    {
        ValidatePipeName(pipeName);
        serviceSid = CanonicalSid(serviceSid); ownerSid = CanonicalSid(ownerSid);
        if (maximumInstances is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(maximumInstances));
        var ownerAce = serviceSid == ownerSid ? "" : $"(A;;0x{ClientPipeAccess:x8};;;{ownerSid})";
        var sddl = $"O:{serviceSid}D:P(A;;0x{ServicePipeAccess:x8};;;{serviceSid}){ownerAce}";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw NativeFailure("The local pipe access descriptor could not be created.");
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName, 0x00000003 | 0x40000000 | (first ? 0x00080000u : 0),
                0x00000008 /* PIPE_REJECT_REMOTE_CLIENTS; byte/read-byte/wait are zero */, (uint)maximumInstances,
                16 * 1024, 16 * 1024, 0, ref security);
            if (handle.IsInvalid)
            {
                var failure = NativeFailure("The local pipe listener could not be created.");
                handle.Dispose(); throw failure;
            }
            try
            {
                RequirePipeSecurity(ReadPipeSecurity(handle), serviceSid, ownerSid);
                return new(PipeDirection.InOut, true, false, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { _ = LocalFree(descriptor); }
    }

    /// <summary>Opens a local pipe with client-only rights and identification SQOS. A successful
    /// return is an unauthenticated connection: verify its OS peer before sending any command.</summary>
    public static async Task<NamedPipeClientStream> ConnectClientAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ValidatePipeName(pipeName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var handle = CreateFile(@"\\.\pipe\" + pipeName, ClientPipeAccess, 0, IntPtr.Zero, 3,
                    0x40000000 | 0x00100000 | 0x00010000 /* overlapped, SQOS present, identification */, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    try
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        return new(PipeDirection.InOut, true, true, handle);
                    }
                    catch { handle.Dispose(); throw; }
                }
                var error = Marshal.GetLastWin32Error(); handle.Dispose();
                if (error is not (2 or 231)) throw new IOException("The local pipe could not be opened with owner client rights.", new Win32Exception(error));
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local pipe did not become available within 10 seconds. No command was sent.");
        }
    }

    public static InstalledWorkerPipePeer ObserveServer(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return ObservePeer(pipe.SafePipeHandle, server: true);
    }

    /// <summary>Reads the server PID/session and pipe ACL without opening the LocalService
    /// process. An enrolled interactive owner may not have PROCESS_QUERY_LIMITED_INFORMATION
    /// on that process; SCM supplies the independently observed service identity.</summary>
    public static InstalledWorkerPipeEndpoint ObserveServerEndpoint(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var handle = pipe.SafePipeHandle;
        if (!GetNamedPipeServerProcessId(handle, out var pid) || pid is 0 or > int.MaxValue)
            throw NativeFailure("The connected pipe server could not be identified.");
        if (!GetNamedPipeServerSessionId(handle, out var session) || session > int.MaxValue)
            throw NativeFailure("The connected pipe server session could not be identified.");
        var protection = ReadPipeSecurity(handle);
        if (!GetNamedPipeServerProcessId(handle, out var confirmedPid) || confirmedPid != pid
            || !GetNamedPipeServerSessionId(handle, out var confirmedSession) || confirmedSession != session)
            throw new IOException("The pipe server changed during identity verification.");
        return new((int)pid, (int)session, protection);
    }

    public static InstalledWorkerPipePeer ObserveClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return ObservePeer(pipe.SafePipeHandle, server: false);
    }

    public static InstalledWorkerPipeEndpoint ObserveClientEndpoint(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var handle = pipe.SafePipeHandle;
        if (!GetNamedPipeClientProcessId(handle, out var pid) || pid is 0 or > int.MaxValue ||
            !GetNamedPipeClientSessionId(handle, out var session) || session > int.MaxValue)
            throw NativeFailure("The connected pipe client endpoint could not be identified.");
        var protection = ReadPipeSecurity(handle);
        if (!GetNamedPipeClientProcessId(handle, out var confirmedPid) || confirmedPid != pid ||
            !GetNamedPipeClientSessionId(handle, out var confirmedSession) || confirmedSession != session)
            throw new IOException("The pipe client changed during endpoint verification.");
        return new((int)pid, (int)session, protection);
    }

    /// <summary>Observe an external product's process without applying Cerebral's service-pipe
    /// ACL schema. The caller must validate this product's executable, account and session.</summary>
    public static InstalledWorkerProcessObservation ObserveServerProcess(NamedPipeClientStream pipe) =>
        ObserveProcess(pipe.SafePipeHandle, server: true);

    private static InstalledWorkerPipePeer ObservePeer(SafePipeHandle pipe, bool server)
        => new(ObserveProcess(pipe, server), ReadPipeSecurity(pipe));

    private static InstalledWorkerProcessObservation ObserveProcess(SafePipeHandle pipe, bool server)
    {
        uint pid; uint session;
        var observed = server ? GetNamedPipeServerProcessId(pipe, out pid) : GetNamedPipeClientProcessId(pipe, out pid);
        if (!observed || pid is 0 or > int.MaxValue) throw NativeFailure("The connected pipe process could not be identified.");
        observed = server ? GetNamedPipeServerSessionId(pipe, out session) : GetNamedPipeClientSessionId(pipe, out session);
        if (!observed || session > int.MaxValue) throw NativeFailure("The connected pipe session could not be identified.");
        using var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process.IsInvalid) throw NativeFailure("The connected pipe process cannot be inspected with ordinary query rights.");
        if (!GetProcessTimes(process, out var creation, out var exit, out _, out _) || creation.Value <= 0 || exit.Value != 0)
            throw new IOException("The connected pipe process exited or its creation identity is unavailable.");
        var image = new StringBuilder(4096); var length = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length) || length == 0 || length >= image.Capacity)
            throw NativeFailure("The connected pipe executable could not be identified.");
        if (!ProcessIdToSessionId(pid, out var processSession) || processSession != session)
            throw new UnauthorizedAccessException("The pipe and process session identities do not match.");
        var accountSid = ReadProcessAccountSid(process);
        uint currentPid;
        var samePeer = server ? GetNamedPipeServerProcessId(pipe, out currentPid) : GetNamedPipeClientProcessId(pipe, out currentPid);
        if (!samePeer || currentPid != pid || !GetProcessTimes(process, out var confirmed, out exit, out _, out _)
            || confirmed.Value != creation.Value || exit.Value != 0)
            throw new IOException("The pipe peer changed or exited during identity verification.");
        return new((int)pid, accountSid, image.ToString(), (int)session, creation.Value);
    }

    public static InstalledWorkerPipeProtection ReadPipeSecurity(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var result = GetSecurityInfo(pipe, 1 /* SE_FILE_OBJECT */, 5 /* OWNER | DACL */,
            out _, out _, out _, out _, out var descriptor);
        if (result != 0) throw new UnauthorizedAccessException("The pipe access policy could not be verified.", new Win32Exception(checked((int)result)));
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > MaximumSecurityBytes) throw new InvalidDataException("The pipe access descriptor is invalid or oversized.");
            var bytes = new byte[checked((int)length)]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var entries = new List<InstalledWorkerPipeAce>();
            if (security.DiscretionaryAcl is { } acl)
            {
                if (acl.Count > 2) throw new UnauthorizedAccessException("The pipe has an unexpected access policy.");
                foreach (var ace in acl)
                {
                    if (ace is not CommonAce common || common.IsCallback || common.AceFlags != AceFlags.None
                        || common.AceQualifier != AceQualifier.AccessAllowed)
                        throw new UnauthorizedAccessException("The pipe has an unsupported access policy.");
                    entries.Add(new(common.SecurityIdentifier.Value, unchecked((uint)common.AccessMask)));
                }
            }
            return new(security.Owner?.Value ?? "", security.DiscretionaryAcl is not null &&
                security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent),
                security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), entries);
        }
        finally { if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor); }
    }

    public static void RequirePipeSecurity(InstalledWorkerPipeProtection protection, string serviceSid, string ownerSid)
    {
        ArgumentNullException.ThrowIfNull(protection);
        serviceSid = CanonicalSid(serviceSid); ownerSid = CanonicalSid(ownerSid);
        var expected = serviceSid == ownerSid ? 1 : 2;
        if (protection.OwnerSid != serviceSid || !protection.DaclPresent || !protection.DaclProtected
            || protection.Entries is null || protection.Entries.Count != expected
            || protection.Entries.Any(x => x is null)
            || protection.Entries.Count(x => x.Sid == serviceSid && x.AccessMask == ServicePipeAccess) != 1
            || (serviceSid != ownerSid && protection.Entries.Count(x => x.Sid == ownerSid && x.AccessMask == ClientPipeAccess) != 1))
            throw new UnauthorizedAccessException("The pipe does not have the exact enrolled owner/service access policy.");
    }

    /// <summary>Call after reading a bounded nonsecret client hello. The last read supplies the caller
    /// security context. No asynchronous work or application command runs while impersonating.</summary>
    public static InstalledWorkerClientIdentity ReadAuthenticatedCaller(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using (var prior = WindowsIdentity.GetCurrent(ifImpersonating: true))
            if (prior is not null) throw new InvalidOperationException("Inspect the pipe caller outside an existing impersonation scope.");
        InstalledWorkerClientIdentity? caller = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true)
                ?? throw new UnauthorizedAccessException("The pipe caller did not provide an identification token.");
            if (identity.ImpersonationLevel < TokenImpersonationLevel.Identification || identity.User is null)
                throw new UnauthorizedAccessException("The pipe caller did not provide an authenticated identification token.");
            if (!GetTokenInformation(identity.AccessToken, 12 /* TokenSessionId */, out var tokenSession, sizeof(uint), out var returned)
                || returned != sizeof(uint) || tokenSession > int.MaxValue)
                throw NativeFailure("The pipe caller token session could not be identified.");
            if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var pipeSession) || tokenSession != pipeSession)
                throw new UnauthorizedAccessException("The pipe caller token and native session do not match.");
            var network = identity.Groups?.Any(x => x.Value == "S-1-5-2") == true;
            caller = new(identity.User.Value, (int)tokenSession, identity.IsAuthenticated, network, identity.IsAnonymous);
        });
        return caller ?? throw new UnauthorizedAccessException("The pipe caller token was not observed.");
    }

    /// <summary>Authenticates the native caller against an already verified enrollment. The caller
    /// supplying the binding remains responsible for its protected-registry provenance.</summary>
    public static InstalledWorkerClientIdentity RequireOwnerCaller(NamedPipeServerStream pipe, InstalledWorkerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding); binding.Validate();
        var caller = ReadAuthenticatedCaller(pipe);
        InstalledWorkerAdmission.RequireOwnerClient(binding, caller);
        return caller;
    }

    private static (string Executable, string Data, bool InstalledPipe) ParseServiceCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > 4096)
            throw new InvalidDataException("The installed service command line is missing or oversized.");
        var pointer = CommandLineToArgv(commandLine, out var count);
        if (pointer == IntPtr.Zero) throw NativeFailure("The installed service command line could not be parsed.");
        try
        {
            if (count is not (3 or 4)) throw new InvalidDataException("The installed service command line must contain its executable, explicit data path and optional installed-pipe switch.");
            var args = new string[count];
            for (var i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "";
            var installed = count == 4;
            if (args[1] != "--data" || (installed && args[3] != "--installed-pipe")
                || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[2])
                || args[0].StartsWith(@"\\", StringComparison.Ordinal) || args[2].StartsWith(@"\\", StringComparison.Ordinal)
                || args[0].Length > 2048 || args[2].Length > 2048
                || args[0].Any(char.IsControl) || args[2].Any(char.IsControl))
                throw new InvalidDataException("The installed service command line does not identify the bounded local Worker and data paths.");
            return (args[0], args[2], installed);
        }
        finally { _ = LocalFree(pointer); }
    }

    private static string ReadProcessAccountSid(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
            throw NativeFailure("The connected pipe process account could not be inspected.");
        using (token)
        {
            _ = GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out var needed);
            if (Marshal.GetLastWin32Error() != 122 || needed is < 16 or > 65_536)
                throw NativeFailure("The connected pipe process account is unavailable or oversized.");
            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetTokenInformation(token, 1, buffer, needed, out var returned) || returned != needed)
                    throw NativeFailure("The connected pipe process account could not be read.");
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero) throw new UnauthorizedAccessException("The connected pipe process has no account SID.");
                return new SecurityIdentifier(sid).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static string BoundedUnicode(IntPtr value, IntPtr buffer, uint bytes)
    {
        var offset = value.ToInt64() - buffer.ToInt64();
        if (value == IntPtr.Zero || offset < 0 || offset >= bytes || (offset & 1) != 0)
            throw new InvalidDataException("The service configuration contains an invalid string reference.");
        var maximum = checked((int)(bytes - offset) / 2);
        for (var length = 0; length < maximum; length++)
            if (Marshal.ReadInt16(value, length * 2) == 0) return Marshal.PtrToStringUni(value, length) ?? "";
        throw new InvalidDataException("The service configuration contains an unterminated string.");
    }

    private static string CanonicalSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184) throw new ArgumentException("A bounded Windows SID is required.");
        var sid = new SecurityIdentifier(value);
        if (sid.Value != value) throw new ArgumentException("Use a canonical Windows SID.");
        return sid.Value;
    }

    private static void ValidatePipeName(string value) => new LocalWorkerEndpoint(value, "11111111111111111111111111111111").Validate();
    private static IOException NativeFailure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPath, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, StartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low, High; public readonly long Value => unchecked((long)(((ulong)High << 32) | Low)); }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW", SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateNamedPipeW", SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, uint length, out uint returned);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint length, out uint returned);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "CommandLineToArgvW", SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
