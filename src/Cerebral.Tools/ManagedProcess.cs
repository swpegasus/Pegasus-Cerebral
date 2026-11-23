using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Tools;

public sealed class ManagedProcess : IDisposable
{
    public Process Process { get; }
    private SafeFileHandle? _job;
    private readonly bool _processGroup;
    private readonly object cleanupGate = new();
    private ManagedProcess(Process process, SafeFileHandle? job, bool group) { Process = process; _job = job; _processGroup = group; }

    public static ManagedProcess Start(
        string executable,
        IEnumerable<string> arguments,
        string? cwd,
        IReadOnlyDictionary<string, string>? environment = null)
        => StartCore(executable, arguments, cwd, environment, trustedDotnetEnvironment: false);

    private static ManagedProcess StartCore(string executable, IEnumerable<string> arguments,
        string? cwd, IReadOnlyDictionary<string, string>? environment, bool trustedDotnetEnvironment)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new ArgumentException("The registered executable must be an existing absolute file path.");
        ToolPolicy.RejectLinks(executable);
        var group = OperatingSystem.IsLinux();
        if (group && !File.Exists("/usr/bin/setsid")) throw new InvalidOperationException("Process execution requires /usr/bin/setsid for descendant cleanup.");
        var info = new ProcessStartInfo(group ? "/usr/bin/setsid" : executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Path.GetDirectoryName(executable)! };
        if (group) info.ArgumentList.Add(executable);
        foreach (var argument in arguments) { if (argument.Contains('\0')) throw new ArgumentException("Arguments may not contain NUL."); info.ArgumentList.Add(argument); }
        if (trustedDotnetEnvironment)
        {
            // The reviewed checker receives no inherited .NET startup hooks, additional-deps,
            // runtime store or profiling switches. This does not make its process an OS sandbox.
            foreach (var key in info.Environment.Keys.Where(key =>
                         key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(key);
            info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!;
            info.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            info.Environment["PATH"] = Path.GetDirectoryName(executable)! + Path.PathSeparator +
                Environment.GetFolderPath(Environment.SpecialFolder.System);
        }
        // This is only for product-owned launch policy such as the adjacent Linux native-library
        // directory. Tool requests never supply environment variables or a command string.
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Contains('=') || item.Key.Contains('\0') || item.Value.Contains('\0'))
                    throw new ArgumentException("Environment entries must have a non-empty name and contain no NUL or equals sign in the name.");
                info.Environment[item.Key] = item.Value;
            }
        }
        var process = new Process { StartInfo = info };
        SafeFileHandle? job = null;
        if (OperatingSystem.IsWindows())
        {
            job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) { job.Dispose(); throw new InvalidOperationException("Cannot establish process cleanup job."); }
        }
        try
        {
            process.Start();
            if (job is not null && !AssignProcessToJobObject(job, process.Handle)) throw new InvalidOperationException("Cannot attach process to cleanup job.");
            return new(process, job, group);
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); job?.Dispose(); throw; }
    }

    public void Stop()
    {
        lock (cleanupGate)
        {
            if (_processGroup) { try { kill(-Process.Id, 9); } catch { } }
            _job?.Dispose(); _job = null;
            try { if (!Process.HasExited) Process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// Requests cleanup and proves the still-owned job/group has no processes, including descendants,
    /// and that the leader has completed. False means unknown; a kill request or leader exit alone
    /// is insufficient. The private deadline is independent of a cancelled caller. The owned job
    /// remains available for a later bounded retry until ordinary Stop/Dispose closes it.
    /// </summary>
    public async Task<bool> StopAndConfirmAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) return false;
        var elapsed = Stopwatch.StartNew();
        SafeFileHandle? job = null;
        var retained = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr handle;
                lock (cleanupGate)
                {
                    job = _job;
                    if (job is null || job.IsInvalid || job.IsClosed) return false;
                    // Keep the actual owned native handle alive even if a concurrent legacy Stop
                    // disposes its wrapper. Native queries below use this explicitly pinned handle.
                    job.DangerousAddRef(ref retained);
                    handle = job.DangerousGetHandle();
                }
                var initialKnown = TryOwnedJobCount(handle, out var initial);
                if (!TerminateJobObject(handle, 1) && !(initialKnown && initial == 0)) return false;
                if (!initialKnown) return false;
                while (elapsed.Elapsed < timeout)
                {
                    if (!TryOwnedJobCount(handle, out var active)) return false;
                    if (active == 0)
                        return await LeaderCompletedAsync(timeout - elapsed.Elapsed).ConfigureAwait(false) &&
                            elapsed.Elapsed < timeout && TryOwnedJobCount(handle, out active) && active == 0;
                    await Task.Delay(MinimumDelay(timeout - elapsed.Elapsed)).ConfigureAwait(false);
                }
                return false;
            }
            if (_processGroup && OperatingSystem.IsLinux())
            {
                var group = Process.Id;
                if (group <= 0) return false;
                if (kill(-group, 9) != 0 && Marshal.GetLastPInvokeError() != 3) return false; // ESRCH: already absent.
                while (elapsed.Elapsed < timeout)
                {
                    var present = kill(-group, 0);
                    var error = present == 0 ? 0 : Marshal.GetLastPInvokeError();
                    if (present != 0 && error == 3)
                        return await LeaderCompletedAsync(timeout - elapsed.Elapsed).ConfigureAwait(false) &&
                            elapsed.Elapsed < timeout && kill(-group, 0) != 0 && Marshal.GetLastPInvokeError() == 3;
                    if (present != 0) return false; // Permission/error is not absence.
                    await Task.Delay(MinimumDelay(timeout - elapsed.Elapsed)).ConfigureAwait(false);
                }
            }
            return false;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return false; }
        finally { if (retained) job!.DangerousRelease(); }
    }

    private async Task<bool> LeaderCompletedAsync(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return false;
        using var deadline = new CancellationTokenSource(remaining);
        await Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        return Process.HasExited;
    }
    private static TimeSpan MinimumDelay(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20);
    private static bool TryOwnedJobCount(IntPtr handle, out uint active)
    {
        active = 0;
        var size = (uint)Marshal.SizeOf<JobAccounting>();
        if (!QueryInformationJobObject(handle, 1, out var accounting, size, out var returned) || returned != size) return false;
        active = accounting.ActiveProcesses;
        return true;
    }
    public void Dispose() { Stop(); Process.Dispose(); }

    public static async Task<(int ExitCode, string Output, string Error, bool Truncated)> RunAsync(string path, IEnumerable<string> args, string cwd, CancellationToken ct)
        => await RunCoreAsync(path, args, cwd, ct, trustedDotnetEnvironment: false);

    /// <summary>Only a host-admitted private checker may request the scrubbed .NET launch.</summary>
    public static Task<(int ExitCode, string Output, string Error, bool Truncated)> RunOwnerVerificationAsync(
        string path, IEnumerable<string> args, string cwd, CancellationToken ct)
        => RunCoreAsync(path, args, cwd, ct, trustedDotnetEnvironment: true);

    private static async Task<(int ExitCode, string Output, string Error, bool Truncated)> RunCoreAsync(
        string path, IEnumerable<string> args, string cwd, CancellationToken ct, bool trustedDotnetEnvironment)
    {
        using var child = StartCore(path, args, cwd, null, trustedDotnetEnvironment);
        using var registration = ct.Register(child.Stop);
        child.Process.StandardInput.Close();
        var output = DrainAsync(child.Process.StandardOutput, ct);
        var error = DrainAsync(child.Process.StandardError, ct);
        try
        {
            await child.Process.WaitForExitAsync(ct);
            if (trustedDotnetEnvironment)
            {
                if (!await child.StopAndConfirmAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    throw new InvalidOperationException("The checker child and descendants did not reach a confirmed exit.");
            }
            else child.Stop(); // Preserve the ordinary process route's existing cleanup behavior.
            var streams = await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5), ct);
            return (child.Process.ExitCode, streams[0].Text, streams[1].Text, streams.Any(x => x.Truncated));
        }
        finally { child.Stop(); }
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        const string omitted = "\n[output omitted; showing beginning and end]\n";
        var capacity = ToolPolicy.MaxOutput / 2;
        var headCapacity = (capacity - omitted.Length) / 2;
        var tailCapacity = capacity - omitted.Length - headCapacity;
        var builder = new StringBuilder();
        var buffer = new char[4096];
        char[]? tail = null;
        var tailPosition = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            var offset = 0;
            if (tail is null)
            {
                var keep = Math.Min(read, capacity - builder.Length);
                if (keep > 0) builder.Append(buffer, 0, keep);
                if (keep == read) continue;
                // Build warnings often precede the actual compiler failure. Preserve a bounded
                // prefix and rolling tail while draining both streams completely to avoid deadlock.
                tail = new char[tailCapacity];
                builder.CopyTo(builder.Length - tailCapacity, tail, 0, tailCapacity);
                builder.Length = headCapacity;
                offset = keep;
            }
            while (offset < read)
            {
                var copy = Math.Min(read - offset, tail.Length - tailPosition);
                Array.Copy(buffer, offset, tail, tailPosition, copy);
                tailPosition = (tailPosition + copy) % tail.Length;
                offset += copy;
            }
        }
        if (tail is not null)
            builder.Append(omitted).Append(tail, tailPosition, tail.Length - tailPosition).Append(tail, 0, tailPosition);
        return (builder.ToString(), tail is not null);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JobAccounting info, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
}
