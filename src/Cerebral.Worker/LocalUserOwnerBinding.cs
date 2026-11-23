using System.Diagnostics;
using System.Security.Principal;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Process-local development owner facts captured from explicit startup inputs.
/// This is neither an installed binding nor a credential and cannot authorize another process.</summary>
internal sealed record LocalUserOwnerBinding(LocalWorkerEndpoint Endpoint, string DataDirectory,
    int ProcessId, long ProcessStartUtcTicks, string Executable, string OwnerSid, int SessionId)
{
    internal static LocalUserOwnerBinding? TryCapture(string[] args, string dataDirectory,
        LocalWorkerEndpoint endpoint)
    {
        if (!OperatingSystem.IsWindows() ||
            !args.Contains("--local-run", StringComparer.Ordinal) &&
            !args.Contains("--installed-user-pipe", StringComparer.Ordinal) ||
            args.Contains("--installed-pipe", StringComparer.Ordinal)) return null;
        static string? Value(string[] args, string name)
        {
            var positions = Enumerable.Range(0, args.Length).Where(index => args[index] == name).ToArray();
            return positions.Length == 1 && positions[0] + 1 < args.Length ? args[positions[0] + 1] : null;
        }
        if (Value(args, "--data") != dataDirectory || Value(args, "--pipe-name") != endpoint.PipeName ||
            Value(args, "--pipe-instance") != endpoint.InstanceId) return null;
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.IsSystem || identity.User is null || process.SessionId <= 0 ||
            Environment.ProcessPath is not { } executable) return null;
        return new(endpoint, dataDirectory, process.Id, process.StartTime.ToUniversalTime().Ticks,
            executable, identity.User.Value, process.SessionId);
    }

    internal void RequireCurrent(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (dataDirectory != DataDirectory || process.Id != ProcessId ||
            process.StartTime.ToUniversalTime().Ticks != ProcessStartUtcTicks ||
            !string.Equals(Environment.ProcessPath, Executable, StringComparison.OrdinalIgnoreCase) ||
            identity.User?.Value != OwnerSid || identity.IsSystem || process.SessionId != SessionId)
            throw new UnauthorizedAccessException("The task-local owner binding changed.");
    }
}
