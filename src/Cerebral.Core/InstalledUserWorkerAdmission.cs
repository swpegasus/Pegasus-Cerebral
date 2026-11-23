using System.Security.Principal;

namespace Cerebral.Core;

/// <summary>Desktop Worker identity derived from its distinct protected user enrollment.
/// The headless service has a separate binding and admission path.</summary>
public static class InstalledUserWorkerAdmission
{
    public static void RequireOrdinaryUserToken()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The installed desktop Worker requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("The installed desktop Worker must run in the ordinary signed-in user context.");
    }

    public static void RequireStartupArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 7 || args[0] != "--installed-user-pipe" || args[1] != "--data" ||
            args[3] != "--pipe-name" || args[5] != "--pipe-instance" ||
            string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[4]) ||
            string.IsNullOrWhiteSpace(args[6]))
            throw new ArgumentException("The installed desktop Worker requires one exact startup argument shape.");
    }

    public static void RequireHost(InstalledUserWorkerBinding enrollment, InstalledWorkerProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(process);
        enrollment.Validate();
        if (process.ProcessId <= 0 || process.SessionId <= 0 || process.AccountSid != enrollment.OwnerSid ||
            !InstalledWorkerBinding.PathEquals(process.Executable, enrollment.WorkerExecutable, false))
            throw new UnauthorizedAccessException("The desktop Worker is not the enrolled signed-in user and executable.");
    }

    public static void RequireOwnerClient(InstalledUserWorkerBinding enrollment, InstalledWorkerProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(process);
        enrollment.Validate();
        if (process.ProcessId <= 0 || process.SessionId <= 0 || process.AccountSid != enrollment.OwnerSid)
            throw new UnauthorizedAccessException("The desktop Worker client is not the enrolled signed-in user.");
    }

    public static void RequireOwnerClient(InstalledUserWorkerBinding enrollment,
        InstalledWorkerClientIdentity caller, int workerSession)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(caller);
        enrollment.Validate();
        if (!caller.Authenticated || caller.Anonymous || caller.NetworkLogon || workerSession <= 0 ||
            caller.SessionId != workerSession || caller.UserSid != enrollment.OwnerSid)
            throw new UnauthorizedAccessException("The installed desktop pipe requires its enrolled user in the Worker's session.");
    }

    public static void RequireEndpoint(InstalledUserWorkerBinding enrollment, LocalWorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(endpoint);
        enrollment.Validate();
        endpoint.Validate();
        if (endpoint.PipeName != "Pegasus.Cerebral.User." + enrollment.BindingId ||
            !Guid.TryParseExact(endpoint.InstanceId, "N", out var instance) || instance == Guid.Empty ||
            endpoint.InstanceId != instance.ToString("N"))
            throw new UnauthorizedAccessException("The desktop Worker pipe is not bound to this installation.");
    }

    /// <summary>Only an exact nonsecret identity tuple can retain an older service data root
    /// when a desktop owner's saved connection is moved to the distinct user transport.</summary>
    public static void RequireLegacyTransition(InstalledWorkerBinding service,
        InstalledUserWorkerBinding desktop)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(desktop);
        service.Validate();
        desktop.Validate();
        if (service.BindingId != desktop.BindingId || service.OwnerSid != desktop.OwnerSid ||
            !InstalledWorkerBinding.PathEquals(service.WorkerExecutable, desktop.WorkerExecutable, false) ||
            !InstalledWorkerBinding.PathEquals(service.DataDirectory, desktop.DataDirectory, true))
            throw new UnauthorizedAccessException("The desktop Worker enrollment does not retain the prior owner and data root.");
    }
}
