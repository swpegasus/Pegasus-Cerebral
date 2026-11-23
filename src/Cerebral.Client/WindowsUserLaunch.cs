using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Client;

/// <summary>Launch only the installed Cerebral Configuration in the installer's own ordinary user session.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsUserLaunch
{
    public sealed record LaunchInspection(string Executable, int SessionId, bool CallerElevated, int CallerIntegrity, bool UsesLinkedToken, int LaunchIntegrity);
    public sealed record LaunchResult(int ProcessId, int SessionId, bool UsedLinkedToken, string Application = "Pegasus Cerebral Configuration");

    public static string ValidateConfigurationTarget(string installDirectory)
    {
        var install = InstalledCortexDiscovery.ValidateLocalDirectory(installDirectory);
        var marker = Path.Combine(install, "installation.ini");
        RequirePlainFile(marker);
        if (new FileInfo(marker).Length > 16384) throw new InvalidDataException("The Cerebral installation marker is too large.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = false;
        foreach (var raw in File.ReadLines(marker))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Equals("[Cerebral]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!section || line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var split = line.IndexOf('=');
            if (split <= 0 || !values.TryAdd(line[..split].Trim(), line[(split + 1)..].Trim()))
                throw new InvalidDataException("The Cerebral installation marker has invalid or duplicate fields.");
        }
        var expectedVersion = typeof(WindowsUserLaunch).Assembly.GetName().Version!.ToString(3);
        if (values.GetValueOrDefault("Product") != "PegasusCerebral" || values.GetValueOrDefault("Portable") != "0" ||
            values.GetValueOrDefault("Version") != expectedVersion)
            throw new InvalidDataException("Open Configuration from a matching completed Cerebral service installation.");
        var directory = InstalledCortexDiscovery.ValidateLocalDirectory(Path.Combine(install, "Applications", "Desktop"));
        var executable = Path.Combine(directory, "Cerebral.Integrator.exe");
        var assemblyPath = Path.Combine(directory, "Cerebral.Integrator.dll");
        RequirePlainFile(executable); RequirePlainFile(assemblyPath);
        var assembly = AssemblyName.GetAssemblyName(assemblyPath);
        if (assembly.Name != "Cerebral.Integrator" || assembly.Version?.ToString(3) != expectedVersion)
            throw new InvalidDataException("The installed Configuration application does not match this installer version.");
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.ProductName != "Pegasus Cerebral" || version.FileMajorPart != assembly.Version.Major ||
            version.FileMinorPart != assembly.Version.Minor || version.FileBuildPart != assembly.Version.Build)
            throw new InvalidDataException("The Configuration executable does not match its installed application identity.");
        return executable;
    }

    public static LaunchInspection Inspect(string installDirectory)
    {
        var executable = ValidateConfigurationTarget(installDirectory);
        using var tokens = SelectUserToken();
        return new(executable, tokens.Caller.Session, tokens.Caller.Elevated, tokens.Caller.Integrity, tokens.Linked, tokens.Target.Integrity);
    }

    // A read-only native probe for focused verification; no executable path, environment or process is created.
    public static (int SessionId, bool Elevated, int Integrity) InspectCurrentToken()
    {
        using var token = OpenOwnToken();
        var facts = ReadFacts(token);
        return (facts.Session, facts.Elevated, facts.Integrity);
    }

    public static LaunchResult OpenConfiguration(string installDirectory)
    {
        var executable = ValidateConfigurationTarget(installDirectory);
        using var tokens = SelectUserToken();
        if (!tokens.Linked)
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!,
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException("Pegasus Cerebral Configuration did not start.");
            return new(process.Id, tokens.Target.Session, false);
        }
        // CreateProcessWithTokenW requires TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY.
        // No token fields or caller privileges are adjusted; Windows failures remain explicit.
        if (!DuplicateTokenEx(tokens.Handle, 0x000B, IntPtr.Zero, 2, 1, out var primary))
            throw NativeFailure("Windows could not prepare the installer's linked user token");
        using (primary)
        {
            RequireOrdinaryUser(tokens.Caller, ReadFacts(primary));
            if (!CreateEnvironmentBlock(out var environment, primary, false))
                throw NativeFailure("Windows could not load the Configuration user's environment");
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 1 };
                // Exact application name and no arguments. The mutable command buffer holds argv[0] only.
                var command = new StringBuilder("\"" + executable + "\"");
                if (!CreateProcessWithTokenW(primary, 1, executable, command, 0x00000400, environment,
                    Path.GetDirectoryName(executable)!, ref startup, out var process))
                    throw NativeFailure("Windows could not start Configuration without elevation");
                try { return new(process.ProcessId, tokens.Target.Session, true); }
                finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }

    private static void RequirePlainFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A required plain Cerebral application file is missing or linked: " + Path.GetFileName(path));
    }

    private sealed record TokenFacts(string UserSid, int Session, int ElevationType, bool Elevated, int Integrity, bool UiAccess);
    private sealed class TokenSelection(SafeAccessTokenHandle own, TokenFacts caller, SafeAccessTokenHandle? linked, TokenFacts target) : IDisposable
    {
        public SafeAccessTokenHandle Handle => linked ?? own;
        public TokenFacts Caller => caller;
        public TokenFacts Target => target;
        public bool Linked => linked is not null;
        public void Dispose() { linked?.Dispose(); own.Dispose(); }
    }
    private static TokenSelection SelectUserToken()
    {
        var own = OpenOwnToken();
        SafeAccessTokenHandle? linked = null;
        try
        {
            var caller = ReadFacts(own);
            if (caller.Session <= 0 || caller.UserSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
                throw new InvalidOperationException("Configuration must be opened from the signed-in user's interactive session.");
            if (!caller.Elevated)
            {
                RequireOrdinaryUser(caller, caller);
                return new(own, caller, null, caller);
            }
            if (caller.ElevationType != 2)
                throw new InvalidOperationException("This installer has no UAC-linked ordinary user token. Open Pegasus Cerebral Configuration from the Start menu after setup.");
            linked = ReadToken(own, 19, pointer => new SafeAccessTokenHandle(Marshal.ReadIntPtr(pointer)));
            if (linked.IsInvalid) throw new InvalidOperationException("Windows returned no linked user token.");
            var target = ReadFacts(linked); RequireOrdinaryUser(caller, target);
            return new(own, caller, linked, target);
        }
        catch { linked?.Dispose(); own.Dispose(); throw; }
    }
    private static void RequireOrdinaryUser(TokenFacts caller, TokenFacts target)
    {
        if (target.UserSid != caller.UserSid || target.Session != caller.Session || target.Session <= 0 ||
            target.Integrity != 0x2000 || target.Elevated || target.UiAccess)
            throw new InvalidOperationException("Windows did not provide a matching medium-integrity user token. Open Pegasus Cerebral Configuration from the Start menu after setup.");
    }
    private static SafeAccessTokenHandle OpenOwnToken()
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x000A, out var token))
            throw NativeFailure("Windows could not inspect the current process token");
        return token;
    }
    private static TokenFacts ReadFacts(SafeAccessTokenHandle token)
    {
        var user = ReadToken(token, 1, pointer => new SecurityIdentifier(Marshal.ReadIntPtr(pointer)).Value);
        var integrity = ReadToken(token, 25, pointer =>
        {
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(pointer)).Value;
            return int.Parse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        });
        return new(user, ReadToken(token, 12, Marshal.ReadInt32), ReadToken(token, 18, Marshal.ReadInt32),
            ReadToken(token, 20, Marshal.ReadInt32) != 0, integrity, ReadToken(token, 26, Marshal.ReadInt32) != 0);
    }
    private static T ReadToken<T>(SafeAccessTokenHandle token, int informationClass, Func<IntPtr, T> read)
    {
        // Fixed-size classes need their exact native size; variable SID structures start with a bounded buffer.
        var size = informationClass is 12 or 18 or 20 or 26 ? sizeof(int) : informationClass == 19 ? IntPtr.Size : 256;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, size, out var required))
            {
                if (Marshal.GetLastWin32Error() != 122 || required <= size || required > 65536)
                    throw NativeFailure($"Windows could not inspect token information class {informationClass}");
                buffer = Marshal.ReAllocHGlobal(buffer, new IntPtr(required));
                if (!GetTokenInformation(token, informationClass, buffer, required, out _))
                    throw NativeFailure($"Windows could not inspect token information class {informationClass}");
            }
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static Win32Exception NativeFailure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        var advice = error == 1314 ? " Windows did not grant the required process-launch privilege." : "";
        return new Win32Exception(error, message + "." + advice + " Open Pegasus Cerebral Configuration from the Start menu after setup. No elevated fallback was used.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    // API contracts: https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw
    // https://learn.microsoft.com/windows/win32/api/userenv/nf-userenv-createenvironmentblock
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr data, int size, out int required);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, int access, IntPtr attributes, int impersonationLevel, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string application, StringBuilder command,
        int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
