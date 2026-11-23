using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cerebral.Client;

/// <summary>Bounded read-only command-line observation for an already identified local process.
/// Callers must bind its PID and creation time to the same connected pipe before and after this read.</summary>
public static class LocalWorkerNativeArguments
{
    public static string[] ReadNativeArgv(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = Process.GetProcessById(processId);
        if (process.HasExited) throw new IOException("The selected Worker exited before argument observation.");
        var buffer = Marshal.AllocHGlobal(32768);
        try
        {
            var status = NtQueryInformationProcess(process.SafeHandle, 60, buffer, 32768, out var returned);
            if (status < 0 || returned is < 16 or > 32768)
                throw new IOException("The selected Worker native command line cannot be read.");
            var length = (ushort)Marshal.ReadInt16(buffer);
            var pointer = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            var baseAddress = buffer.ToInt64();
            if (length is < 2 or > 16384 || length % 2 != 0 || pointer.ToInt64() < baseAddress ||
                pointer.ToInt64() + length > baseAddress + returned)
                throw new InvalidDataException("The selected Worker native command line is invalid or oversized.");
            return ParseNativeCommandLine(Marshal.PtrToStringUni(pointer, length / 2)
                ?? throw new InvalidDataException("The selected Worker native command line is unavailable."));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string[] ParseNativeCommandLine(string commandLine)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var argvPointer = CommandLineToArgv(commandLine, out var count);
        if (argvPointer == IntPtr.Zero) throw new InvalidDataException("The selected Worker argument vector cannot be parsed.");
        try
        {
            if (count is < 1 or > 32) throw new InvalidDataException("The selected Worker argument vector is invalid.");
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argvPointer, i * IntPtr.Size))
                    ?? throw new InvalidDataException("The selected Worker argument vector contains null.");
                if (result[i].Length > 2048) throw new InvalidDataException("A selected Worker argument is oversized.");
            }
            return result;
        }
        finally { _ = LocalFree(argvPointer); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        int informationClass, IntPtr information, int length, out int returned);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "CommandLineToArgvW", SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
