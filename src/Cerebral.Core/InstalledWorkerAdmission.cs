namespace Cerebral.Core;

/// <summary>Observed OS facts, not fields accepted from a pipe request or a model.</summary>
public enum InstalledWorkerServiceState { Stopped, StartPending, Running, StopPending }
public sealed record InstalledWorkerServiceIdentity(string ServiceName, string AccountSid,
    string Executable, string DataDirectory, int ProcessId, InstalledWorkerServiceState State);
public sealed record InstalledWorkerProcessIdentity(int ProcessId, string AccountSid, string Executable, int SessionId);
public sealed record InstalledWorkerClientIdentity(string UserSid, int SessionId, bool Authenticated, bool NetworkLogon, bool Anonymous);
public sealed record InstalledWorkerRegistryAce(string Sid, uint AccessMask, bool Allow);
public sealed record InstalledWorkerRegistryProtection(string OwnerSid, bool DaclPresent, bool DaclProtected,
    IReadOnlyList<InstalledWorkerRegistryAce> Entries);

/// <summary>Pure admission rules. The Windows boundary must gather every identity fact itself.
/// Passing a record to these rules does not authenticate a process or authorize a command.</summary>
public static class InstalledWorkerAdmission
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    // KEY_READ, GENERIC_READ and GENERIC_EXECUTE only. Reject unknown rights as well as
    // write/delete/ownership rights for principals outside the machine administrator boundary.
    private const uint RegistryReadMask = 0x00020019 | 0x80000000 | 0x20000000;

    public static void RequireProtectedRegistry(InstalledWorkerRegistryProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (protection.OwnerSid is not (SystemSid or AdministratorsSid) || !protection.DaclPresent
            || !protection.DaclProtected || protection.Entries is null || protection.Entries.Count > 128)
            throw new UnauthorizedAccessException("Installed Worker enrollment is not protected by the machine administrator boundary.");
        foreach (var entry in protection.Entries)
            if (entry is null || string.IsNullOrWhiteSpace(entry.Sid) || entry.Allow && (entry.AccessMask & ~RegistryReadMask) != 0
                && entry.Sid is not (SystemSid or AdministratorsSid))
                throw new UnauthorizedAccessException("Installed Worker enrollment permits changes outside the machine administrator boundary.");
    }

    public static void RequireServiceHost(InstalledWorkerBinding binding, InstalledWorkerServiceIdentity service,
        InstalledWorkerProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(process);
        binding.Validate();
        if (service.State != InstalledWorkerServiceState.Running
            || service.ProcessId <= 0 || process.ProcessId != service.ProcessId || process.SessionId != 0
            || service.ServiceName != binding.ServiceName || service.AccountSid != binding.ServiceAccountSid
            || process.AccountSid != binding.ServiceAccountSid || !InstalledWorkerBinding.PathEquals(service.Executable, binding.WorkerExecutable, false)
            || !InstalledWorkerBinding.PathEquals(process.Executable, binding.WorkerExecutable, false)
            || !InstalledWorkerBinding.PathEquals(service.DataDirectory, binding.DataDirectory, true))
            throw new UnauthorizedAccessException("The observed process is not the enrolled Cerebral Windows service.");
    }

    public static void RequirePipeServer(InstalledWorkerBinding binding, InstalledWorkerServiceIdentity service,
        InstalledWorkerProcessIdentity process, int pipeServerProcessId)
    {
        RequireServiceHost(binding, service, process);
        if (service.State != InstalledWorkerServiceState.Running || pipeServerProcessId <= 0 || pipeServerProcessId != process.ProcessId)
            throw new UnauthorizedAccessException("The connected pipe does not belong to the enrolled Cerebral Windows service.");
    }

    /// <summary>Admission path for an unelevated enrolled owner. Windows may deny that user a
    /// process handle to LocalService, so the service PID/configuration is read from SCM and
    /// matched to the native named-pipe server PID/session. The caller must also validate the
    /// pipe object's exact LocalService/owner ACL before calling this method.</summary>
    public static void RequirePipeServer(InstalledWorkerBinding binding, InstalledWorkerServiceIdentity service,
        InstalledWorkerPipeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(endpoint);
        binding.Validate();
        if (service.State != InstalledWorkerServiceState.Running || service.ProcessId <= 0
            || endpoint.ProcessId != service.ProcessId || endpoint.SessionId != 0
            || service.ServiceName != binding.ServiceName || service.AccountSid != binding.ServiceAccountSid
            || !InstalledWorkerBinding.PathEquals(service.Executable, binding.WorkerExecutable, false)
            || !InstalledWorkerBinding.PathEquals(service.DataDirectory, binding.DataDirectory, true))
            throw new UnauthorizedAccessException("The connected pipe does not belong to the enrolled Cerebral Windows service.");
    }

    public static void RequireOwnerClient(InstalledWorkerBinding binding, InstalledWorkerClientIdentity client)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(client); binding.Validate();
        if (!client.Authenticated || client.Anonymous || client.NetworkLogon || client.SessionId <= 0 || client.UserSid != binding.OwnerSid)
            throw new UnauthorizedAccessException("The installed Cerebral pipe requires its enrolled local interactive owner.");
    }
}
