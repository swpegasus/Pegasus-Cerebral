namespace Cerebral.Core;

/// <summary>Pure portable projections and local binding adoption. Does not probe, launch, provision or persist.
/// Omitted argv is local-only data, not an empty argv claim. An inactive restored declaration is not execution authority.</summary>
public static class PortableRegistrationPolicy
{
    public static ConfigurationBackupWorker ExportWorker(WorkerRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.Id, value.Name, SafeEndpoint(value.Url) is null ? null : value.Url,
            value.SshHost, value.SshUser, value.SshPort, value.InstallDirectory);
    }

    public static ConfigurationBackupExecutable ExportExecutable(ExecutableRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.Id, value.Path, value.PrefixArguments.Count > 0);
    }

    public static ConfigurationBackupMcp ExportMcp(McpRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.Id, value.Executable, SafeConnectorEndpoint(value.Url) is null ? null : value.Url,
            value.ViaUserConnector, value.Enabled, value.Arguments.Count > 0,
            value.Url is not null && SafeConnectorEndpoint(value.Url) is null, value.CortexNative is not null || value.CortexNativeRequired);
    }

    public static WorkerRegistration RestoreWorker(ConfigurationBackupWorker value, WorkerRegistration? current)
    {
        Validate(value);
        var sameEndpoint = current is { RequiresConfiguration: false } && current.Id == value.Id &&
            SameEndpoint(value.Url, current.Url);
        var sameSsh = sameEndpoint && SameSsh(value, current!);
        var same = sameEndpoint && sameSsh && value.InstallDirectory == current!.InstallDirectory;
        return new()
        {
            Id = value.Id, Name = value.Name, Url = value.Url ?? "",
            SshHost = value.SshHost, SshUser = value.SshUser, SshPort = value.SshPort, InstallDirectory = value.InstallDirectory,
            AccessKey = sameEndpoint ? current!.AccessKey : "",
            PeerToken = sameEndpoint ? current!.PeerToken : "",
            SshIdentityFile = sameSsh ? current!.SshIdentityFile : null,
            SshKnownHostsFile = sameSsh ? current!.SshKnownHostsFile : null,
            RequiresConfiguration = !same
        };
    }

    public static ExecutableRegistration RestoreExecutable(ConfigurationBackupExecutable value, ExecutableRegistration? current)
    {
        Validate(value);
        var same = current is { RequiresConfiguration: false } && current.Id == value.Id &&
            SamePath(value.Path, current.Path) && value.ArgumentsOmitted == (current.PrefixArguments.Count > 0);
        return new()
        {
            Id = value.Id, Path = value.Path,
            PrefixArguments = same && value.ArgumentsOmitted ? [.. current!.PrefixArguments] : [],
            RequiresConfiguration = !same
        };
    }

    public static McpRegistration RestoreMcp(ConfigurationBackupMcp value, McpRegistration? current)
    {
        Validate(value);
        var sameTransport = value.NativeBindingOmitted && current?.CortexNative is not null
            ? value.Url is null && current.Url is null
            : value.ViaUserConnector
            ? SameEndpoint(value.Url, current?.Url)
            : value.Url is null && string.IsNullOrEmpty(current?.Url);
        var same = !value.EndpointOmitted && current is { RequiresConfiguration: false } && current.Id == value.Id &&
            SamePath(value.Executable, current.Executable) && value.ViaUserConnector == current.ViaUserConnector &&
            sameTransport && value.NativeBindingOmitted == (current.CortexNative is not null) &&
            value.ArgumentsOmitted == (current.Arguments.Count > 0) && value.WasEnabled == current.Enabled;
        return new()
        {
            Id = value.Id, Executable = value.Executable, Url = value.Url, ViaUserConnector = value.ViaUserConnector,
            Arguments = same && value.ArgumentsOmitted ? [.. current!.Arguments] : [],
            Enabled = same && current!.Enabled, RequiresConfiguration = !same,
            CortexNative = same ? current!.CortexNative : null, CortexNativeRequired = value.NativeBindingOmitted
        };
    }

    public static void Validate(ConfigurationBackupWorker value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id, 100); Text(value.Name, 256, false);
        if (value.Url is not null && SafeEndpoint(value.Url) is null)
            throw new ArgumentException("Worker URLs must be bounded HTTP(S) endpoints without user information, query strings or fragments.");
        OptionalText(value.SshHost, 253); OptionalText(value.SshUser, 128); OptionalText(value.InstallDirectory, 4096);
        if (value.SshPort is < 1 or > 65535) throw new ArgumentException("SSH port is outside the supported range.");
        if (!string.IsNullOrEmpty(value.SshHost) &&
            (value.SshHost.StartsWith('-') || value.SshHost.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or ':' or '-'))))
            throw new ArgumentException("SSH host must be a bounded host name or address.");
        if (!string.IsNullOrEmpty(value.SshUser) &&
            (value.SshUser.StartsWith('-') || value.SshUser.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
            throw new ArgumentException("SSH user must be a bounded account name.");
    }

    public static void Validate(ConfigurationBackupExecutable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id, 100); Text(value.Path, 4096, false);
    }

    public static void Validate(ConfigurationBackupMcp value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id, 64); if (value.Id.Contains('.')) throw new ArgumentException("MCP registration identifier is invalid.");
        Text(value.Executable, 4096, false);
        if (value.EndpointOmitted && value.Url is not null) throw new ArgumentException("An omitted MCP endpoint cannot also supply a URL.");
        if (value.Url is not null && SafeConnectorEndpoint(value.Url) is null)
            throw new ArgumentException("MCP connector URL must be an explicit loopback HTTP origin without credentials, paths, queries or fragments.");
        if (!value.ViaUserConnector && value.Url is not null)
            throw new ArgumentException("This MCP adapter supports direct stdio or a local user connector, not direct network MCP.");
    }

    public static void RequireReady(WorkerRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RequiresConfiguration) throw new InvalidOperationException("Worker registration requires owner review in Configuration before connecting. No request was sent.");
    }

    public static void RequireReady(ExecutableRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RequiresConfiguration) throw new InvalidOperationException("Executable registration requires owner review in Configuration before execution. No process was launched.");
    }

    public static void RequireReady(McpRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RequiresConfiguration) throw new InvalidOperationException("MCP registration requires owner review in Configuration before discovery or execution. No provider was launched.");
    }

    private static bool SameSsh(ConfigurationBackupWorker value, WorkerRegistration current) =>
        string.Equals(value.SshHost, current.SshHost, StringComparison.OrdinalIgnoreCase) &&
        value.SshUser == current.SshUser && value.SshPort == current.SshPort;
    private static bool SameEndpoint(string? left, string? right) =>
        SafeEndpoint(left) is { } first && SafeEndpoint(right) is { } second && first == second;
    private static bool SamePath(string left, string right) =>
        CanonicalPath(left) is { } first && CanonicalPath(right) is { } second &&
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string? CanonicalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    private static string? SafeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/') + "/";
    }
    private static string? SafeConnectorEndpoint(string? value)
    {
        if (value is null) return null;
        try { return ConnectorProtocol.ValidateUrl(value).AbsoluteUri; }
        catch (ArgumentException) { return null; }
    }
    private static void Id(string? value, int maximum)
    {
        if (value is null || value.Length is < 1 || value.Length > maximum ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Registration identifier is invalid.");
    }
    private static void Text(string? value, int maximum, bool empty)
    {
        if (value is null || value.Length > maximum || (!empty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl))
            throw new ArgumentException("Registration text is missing or outside its bounds.");
    }
    private static void OptionalText(string? value, int maximum) { if (value is not null) Text(value, maximum, true); }
}
