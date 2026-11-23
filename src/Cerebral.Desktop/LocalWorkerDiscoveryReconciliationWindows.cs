using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Cerebral.Client;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Desktop;

/// <summary>Bounded, read-only native arguments for an observed local Windows process.
/// Callers must separately bind its PID and creation time to their authenticated peer.</summary>
public static class WindowsProcessArguments
{
    public static string[] ReadNativeArgv(int processId) => LocalWorkerReconcileWindows.ReadNativeArgv(processId);
}

/// <summary>Read-only native observations for the separately reviewed discovery repair.
/// Unavailable identity data is refusal, never permission to infer a Worker from a saved PID.</summary>
internal sealed class LocalWorkerReconcileWindows : ILocalWorkerReconcileObserver
{
    public async Task<LocalWorkerReconcileObservation> ObserveAsync(
        LocalWorkerDiscoveryReconcileRequest request, CancellationToken token)
    {
        var lockPath = Path.Combine(request.DataDirectory, "service.lock");
        var lockId = ObserveServiceLock(lockPath, request.CurrentGeneration.ProcessId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = await InstalledWorkerWindows.ConnectClientAsync(
            request.CurrentGeneration.Endpoint.PipeName, timeout.Token).ConfigureAwait(false);
        var first = InstalledWorkerWindows.ObserveServerProcess(pipe);
        RequirePeer(first, request);
        var argv = ReadNativeArgv(first.ProcessId);
        RequireArguments(argv, request);
        await LocalWorkerProtocol.WriteAsync(pipe, LocalWorkerProtocol.CreateHello(
            request.CurrentGeneration.Endpoint), timeout.Token).ConfigureAwait(false);
        var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, timeout.Token).ConfigureAwait(false);
        LocalWorkerProtocol.ValidateHello(hello, request.CurrentGeneration.Endpoint);
        await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerRequest("command",
            new CommandRequest("health", CerebralJson.Element(new { }))), timeout.Token).ConfigureAwait(false);
        var response = await LocalWorkerProtocol.ReadAsync<CommandResult>(pipe, timeout.Token).ConfigureAwait(false);
        if (!response.Success) throw new IOException("The exact pipe peer refused health.");
        var host = CerebralJson.Read<HostInfo>(response.Data);
        if (host.Product != LocalWorkerProtocol.Product || host.Protocol != 1 || !host.LocalRun ||
            host.DataDirectory != request.DataDirectory || host.InstallationId != request.InstallationId)
            throw new InvalidOperationException("The exact pipe peer reports a different product, data root or installation.");
        var second = InstalledWorkerWindows.ObserveServerProcess(pipe);
        RequirePeer(second, request);
        if (second != first || ObserveServiceLock(lockPath, request.CurrentGeneration.ProcessId) != lockId ||
            !ReadNativeArgv(first.ProcessId).SequenceEqual(argv, StringComparer.Ordinal))
            throw new IOException("The current Worker or service lock changed during health verification.");
        return new(first.ProcessId, DateTime.FromFileTimeUtc(first.CreationTimeFileTime).Ticks,
            first.Executable, first.AccountSid, first.SessionId, argv, request.SelectedEnginesDirectory,
            host.InstallationId!, request.CurrentGeneration.Endpoint, host.DataDirectory, lockId);
    }

    private static void RequirePeer(InstalledWorkerProcessObservation peer, LocalWorkerDiscoveryReconcileRequest request)
    {
        if (peer.ProcessId != request.CurrentGeneration.ProcessId ||
            DateTime.FromFileTimeUtc(peer.CreationTimeFileTime).Ticks != request.CurrentGeneration.ProcessStartUtcTicks ||
            !peer.Executable.Equals(request.Executable, StringComparison.OrdinalIgnoreCase) ||
            peer.AccountSid != request.AccountSid || peer.SessionId != request.SessionId)
            throw new UnauthorizedAccessException("The connected OS pipe peer differs from the exact reviewed Worker generation.");
        using var current = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        if (current.User?.Value != peer.AccountSid || self.SessionId != peer.SessionId)
            throw new UnauthorizedAccessException("Discovery reconciliation requires the same signed-in account and desktop session.");
    }

    private static void RequireArguments(string[] argv, LocalWorkerDiscoveryReconcileRequest request)
    {
        if (!argv.SequenceEqual(request.NativeArgv, StringComparer.Ordinal) ||
            !argv[0].Equals(request.Executable, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The native Worker command line differs from the reviewed binding.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < argv.Length; i++)
        {
            if (argv[i] == "--local-run")
            {
                if (!fields.TryAdd(argv[i], "")) throw new InvalidDataException("Duplicate local-run flag.");
                continue;
            }
            if (!argv[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= argv.Length ||
                !fields.TryAdd(argv[i], argv[++i]))
                throw new InvalidDataException("The Worker command line has an unknown or duplicate argument shape.");
        }
        if (fields.Count is < 5 or > 7 || !fields.ContainsKey("--local-run") ||
            !fields.TryGetValue("--data", out var data) || data != request.DataDirectory ||
            !fields.TryGetValue("--pipe-name", out var pipeName) || pipeName != request.CurrentGeneration.Endpoint.PipeName ||
            !fields.TryGetValue("--pipe-instance", out var instance) || instance != request.CurrentGeneration.Endpoint.InstanceId ||
            !fields.TryGetValue("--engines", out var engines) || engines != request.SelectedEnginesDirectory ||
            fields.Keys.Any(key => key is not ("--local-run" or "--data" or "--pipe-name" or "--pipe-instance"
                or "--engines" or "--service-plugins" or "--desktop-sdk")))
            throw new UnauthorizedAccessException("The native Worker flags do not bind the exact selected data, endpoint and engine root.");
    }

    internal static string FileId(SafeFileHandle file)
    {
        var bytes = new byte[24]; // FILE_ID_INFO: volume serial followed by 128-bit file ID.
        if (!GetFileInformationByHandleEx(file, 18, bytes, (uint)bytes.Length))
            throw new IOException("The exact discovery file identity cannot be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string ObserveServiceLock(string path, int expectedPid)
    {
        LocalWorkerDiscovery.RejectLink(path);
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The service lock file cannot be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        var fileId = FileId(handle);
        var bytes = new byte[4096];
        var status = NtQueryInformationFile(handle, out _, bytes, (uint)bytes.Length, 47);
        if (status < 0)
            throw new IOException($"The service lock holder process list is unavailable (NTSTATUS 0x{status:x8}).");
        var count = BitConverter.ToUInt32(bytes, 0);
        var offset = IntPtr.Size;
        if (count is < 1 or > 128 || offset + (long)count * IntPtr.Size > bytes.Length)
            throw new InvalidDataException("The service lock holder list is invalid or oversized.");
        using var self = Process.GetCurrentProcess();
        var found = false;
        for (var i = 0; i < count; i++)
        {
            var pid = IntPtr.Size == 8 ? BitConverter.ToInt64(bytes, offset + i * 8)
                : BitConverter.ToInt32(bytes, offset + i * 4);
            if (pid == expectedPid) found = true;
            else if (pid != self.Id) throw new UnauthorizedAccessException("Another process has the selected service lock file open.");
        }
        if (!found) throw new UnauthorizedAccessException("The exact Worker is not observed as a service lock holder.");
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new UnauthorizedAccessException("The service lock is not held exclusively by the Worker.");
        }
        catch (IOException error) when (IsExactSharingViolation(error))
        { /* Only an exact sharing violation establishes the exclusive-open refusal. */ }
        if (FileId(handle) != fileId || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The service lock file changed during observation.");
        return fileId;
    }

    internal static string[] ReadNativeArgv(int pid) => LocalWorkerNativeArguments.ReadNativeArgv(pid);

    internal static string[] ParseNativeCommandLine(string commandLine) =>
        LocalWorkerNativeArguments.ParseNativeCommandLine(commandLine);

    internal static bool IsExactSharingViolation(IOException error) =>
        error.HResult == unchecked((int)0x80070020);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        [Out] byte[] data, uint length);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status; internal IntPtr Information; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status,
        [Out] byte[] information, uint length, int informationClass);
}
