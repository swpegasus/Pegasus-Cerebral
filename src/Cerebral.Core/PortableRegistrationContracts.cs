using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Portable connection metadata. Credentials, SSH key paths and opaque arguments never travel here.</summary>
public sealed record ConfigurationBackupWorker(string Id, string Name, string? Url,
    string? SshHost = null, string? SshUser = null, int SshPort = 22, string? InstallDirectory = null);
public sealed record ConfigurationBackupExecutable(string Id, string Path, bool ArgumentsOmitted);
[method: JsonConstructor]
public sealed record ConfigurationBackupMcp(string Id, string Executable, string? Url,
    bool ViaUserConnector, bool WasEnabled, bool ArgumentsOmitted, bool EndpointOmitted = false, bool NativeBindingOmitted = false)
{
    // Preserve callers compiled before NativeBindingOmitted was added.
    public ConfigurationBackupMcp(string Id, string Executable, string? Url, bool ViaUserConnector,
        bool WasEnabled, bool ArgumentsOmitted, bool EndpointOmitted)
        : this(Id, Executable, Url, ViaUserConnector, WasEnabled, ArgumentsOmitted, EndpointOmitted, false) { }

    public void Deconstruct(out string Id, out string Executable, out string? Url,
        out bool ViaUserConnector, out bool WasEnabled, out bool ArgumentsOmitted, out bool EndpointOmitted)
    {
        Id = this.Id; Executable = this.Executable; Url = this.Url; ViaUserConnector = this.ViaUserConnector;
        WasEnabled = this.WasEnabled; ArgumentsOmitted = this.ArgumentsOmitted;
        EndpointOmitted = this.EndpointOmitted;
    }
}
