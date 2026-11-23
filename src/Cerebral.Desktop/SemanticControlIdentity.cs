using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

public static class SemanticControlIdentity
{
    private const int MaximumDescriptorBytes = 8192;
    private static readonly JsonSerializerOptions DescriptorJson = new(CerebralJson.Options)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<SemanticControlEndpoint> ReadEndpointAsync(string path, string product, string pipePrefix, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Use an absolute local desktop-control descriptor path.", nameof(path));
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The desktop-control descriptor cannot be a link.");
        // Windows sharing is reciprocal: this read handle must share the publisher's
        // existing Write/Delete access. The publisher's own FileShare.Read lease still
        // denies every other handle requesting write/delete access to that file object.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumDescriptorBytes)
            throw new InvalidDataException("The desktop-control descriptor has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The desktop-control descriptor changed while reading.");
        return ParseEndpoint(bytes, product, pipePrefix);
    }

    public static SemanticControlEndpoint ParseEndpoint(byte[] bytes, string product, string pipePrefix)
    {
        if (bytes.Length is < 1 or > MaximumDescriptorBytes)
            throw new InvalidDataException("The desktop-control descriptor has an invalid size.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("The desktop-control descriptor must be one closed object with unique properties.");
        var endpoint = document.RootElement.Deserialize<SemanticControlEndpoint>(DescriptorJson)
            ?? throw new InvalidDataException("The desktop-control descriptor is empty.");
        SemanticControlProtocol.Validate(endpoint, product, pipePrefix);
        return endpoint;
    }

    public static void RequireServer(NamedPipeClientStream pipe, SemanticControlEndpoint endpoint, string entry)
    {
        var first = InstalledWorkerWindows.ObserveServerProcess(pipe);
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        if (string.IsNullOrEmpty(entry) || !Path.IsPathFullyQualified(entry))
            throw new UnauthorizedAccessException("The current Cerebral managed entry cannot be pinned.");
        var appHost = Path.ChangeExtension(entry, ".exe");
        // Resolve a managed host only for an actual managed-host peer. Self-contained
        // apphost clients do not acquire a dependency on a machine-wide dotnet install.
        var managedHost = SameLocalFilePath(first.Executable, appHost)
            ? null : ResolveManagedHost();
        var argv = WindowsProcessArguments.ReadNativeArgv(first.ProcessId);
        if (!PeerMatches(endpoint, first, argv, identity.User?.Value, current.SessionId, appHost, managedHost, entry) ||
            InstalledWorkerWindows.ObserveServerProcess(pipe) != first ||
            !WindowsProcessArguments.ReadNativeArgv(first.ProcessId).SequenceEqual(argv, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The connected desktop-control server is not this Cerebral payload's exact process in the current user session.");
    }

    // Admission seam: descriptor PID/creation/session are checked against native
    // pipe facts; executable/entry trust comes from this client payload, never the file.
    public static bool PeerMatches(SemanticControlEndpoint endpoint, InstalledWorkerProcessObservation peer,
        string[] argv, string? currentSid, int currentSession, string appHost, string? managedHost, string entry)
    {
        if (string.IsNullOrEmpty(currentSid) || currentSession <= 0 ||
            peer.AccountSid != currentSid || peer.SessionId != currentSession || peer.SessionId != endpoint.SessionId ||
            peer.ProcessId != endpoint.ProcessId ||
            !long.TryParse(endpoint.ProcessStartUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        try { if (DateTime.FromFileTimeUtc(peer.CreationTimeFileTime).Ticks != ticks) return false; }
        catch (ArgumentOutOfRangeException) { return false; }
        if (argv.Length == 0 || !SameLocalFilePath(argv[0], peer.Executable)) return false;
        return SameLocalFilePath(peer.Executable, appHost) ||
            managedHost is not null && SameLocalFilePath(peer.Executable, managedHost) &&
            argv.Length >= 2 && SameLocalFilePath(argv[1], entry);
    }

    public static string ResolveManagedHost()
    {
        var current = Environment.ProcessPath;
        var candidate = current is not null && Path.GetFileName(current).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? current : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
            throw new FileNotFoundException("The fixed managed application host is unavailable.");
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(Path.GetDirectoryName(candidate)!);
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The managed application host cannot be a linked file.");
        return candidate;
    }

    private static bool SameLocalFilePath(string supplied, string expected) =>
        TryLocalFilePath(supplied, out var actual) && TryLocalFilePath(expected, out var pinned) &&
        actual.Equals(pinned, StringComparison.OrdinalIgnoreCase);

    private static bool TryLocalFilePath(string value, out string normalized)
    {
        normalized = "";
        // Native argv may use either Windows separator. Admit only an absolute drive
        // path, then reuse the existing no-stream/no-link local-directory admission.
        // Relative, UNC and device spellings never acquire an ambient-directory meaning.
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4 || value.Any(char.IsControl) ||
            !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] is not ('\\' or '/') ||
            value.AsSpan(2).Contains(':') || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            var full = Path.GetFullPath(value);
            _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(Path.GetDirectoryName(full)!);
            if (string.IsNullOrEmpty(Path.GetFileName(full))) return false;
            normalized = full;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
