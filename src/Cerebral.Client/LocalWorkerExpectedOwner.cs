using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Reviewed, task-local Worker generation. It is an expected identity, not a credential.</summary>
public sealed class LocalWorkerExpectedOwner
{
    private readonly string[] nativeArgv;

    public LocalWorkerEndpoint Endpoint { get; }
    public int ProcessId { get; }
    public long ProcessStartUtcTicks { get; }
    public string Executable { get; }
    public string AccountSid { get; }
    public int SessionId { get; }
    public string DataDirectory { get; }
    public IReadOnlyList<string> NativeArgv => Array.AsReadOnly(nativeArgv);

    public LocalWorkerExpectedOwner(LocalWorkerEndpoint endpoint, int processId, long processStartUtcTicks,
        string executable, string accountSid, int sessionId, string dataDirectory, IReadOnlyList<string> nativeArgv)
    {
        ArgumentNullException.ThrowIfNull(endpoint); endpoint.Validate();
        ArgumentNullException.ThrowIfNull(nativeArgv);
        Endpoint = endpoint; ProcessId = processId; ProcessStartUtcTicks = processStartUtcTicks;
        Executable = executable; AccountSid = accountSid; SessionId = sessionId;
        DataDirectory = dataDirectory; this.nativeArgv = nativeArgv.ToArray();
        Validate();
    }

    private void Validate()
    {
        if (ProcessId <= 0 || ProcessStartUtcTicks <= 0 || SessionId <= 0 ||
            string.IsNullOrWhiteSpace(AccountSid) || !ExactPath(Executable) || !ExactPath(DataDirectory) ||
            nativeArgv.Length is < 8 or > 32 ||
            !nativeArgv[0].Equals(Executable, StringComparison.OrdinalIgnoreCase) ||
            nativeArgv.Any(value => value is null || value.Length > 2048))
            throw new ArgumentException("The expected local Worker owner is incomplete.");
        var flagStart = 1;
        if (Path.GetFileName(Executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // A framework-dependent Worker has one exact entry assembly after the host,
            // optionally preceded by the literal exec verb. Keep both in the full peer argv.
            flagStart = nativeArgv.Length > 1 && nativeArgv[1] == "exec" ? 3 : 2;
            if (nativeArgv.Length <= flagStart || !ExactPath(nativeArgv[flagStart - 1]) ||
                Path.GetFileName(nativeArgv[flagStart - 1]) != "Cerebral.Worker.dll")
                throw new ArgumentException("The expected dotnet host must select the exact Worker entry assembly.");
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = flagStart; i < nativeArgv.Length; i++)
        {
            var flag = nativeArgv[i];
            if (flag is "--local-run" or "--installed-user-pipe" or "--model-admission-closed")
            {
                if (!fields.TryAdd(flag, "")) throw new ArgumentException("The expected Worker flags repeat.");
                continue;
            }
            if (!flag.StartsWith("--", StringComparison.Ordinal) || i + 1 >= nativeArgv.Length ||
                !fields.TryAdd(flag, nativeArgv[++i]))
                throw new ArgumentException("The expected Worker arguments are malformed or repeated.");
        }
        var installedUser = fields.ContainsKey("--installed-user-pipe");
        if (fields.ContainsKey("--local-run") == installedUser || fields.ContainsKey("--installed-pipe") ||
            !fields.TryGetValue("--data", out var data) || data != DataDirectory ||
            !fields.TryGetValue("--pipe-name", out var pipe) || pipe != Endpoint.PipeName ||
            !fields.TryGetValue("--pipe-instance", out var instance) || instance != Endpoint.InstanceId ||
            (!installedUser && !fields.ContainsKey("--engines")) ||
            (installedUser && fields.Keys.Any(key => key is "--engines" or "--service-plugins" or
                "--desktop-sdk" or "--model-admission-closed")) ||
            fields.Keys.Any(key => key is not ("--local-run" or "--installed-user-pipe" or "--model-admission-closed" or
                "--data" or "--pipe-name" or "--pipe-instance" or "--engines" or
                "--service-plugins" or "--desktop-sdk")))
            throw new ArgumentException("The expected Worker arguments do not bind its data root and pipe.");
    }

    internal bool IsInstalledUser => nativeArgv.Contains("--installed-user-pipe", StringComparer.Ordinal);

    internal void RequireProcess(InstalledWorkerProcessObservation peer)
    {
        if (peer.ProcessId != ProcessId ||
            DateTime.FromFileTimeUtc(peer.CreationTimeFileTime).Ticks != ProcessStartUtcTicks ||
            !peer.Executable.Equals(Executable, StringComparison.OrdinalIgnoreCase) ||
            peer.AccountSid != AccountSid || peer.SessionId != SessionId)
            throw new UnauthorizedAccessException("The connected pipe server differs from the reviewed Worker generation.");
    }

    internal void RequirePeer(InstalledWorkerProcessObservation peer, string[] argv)
    {
        RequireProcess(peer);
        if (!argv.SequenceEqual(nativeArgv, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The connected Worker arguments differ from the reviewed data root and pipe.");
    }

    private static bool ExactPath(string path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path &&
        !path.StartsWith("\\\\", StringComparison.Ordinal);
}
