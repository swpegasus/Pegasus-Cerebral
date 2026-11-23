using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Client;

namespace Cerebral.Desktop;

public sealed class ConnectionPreferences
{
    public int SchemaVersion { get; set; } = 1;
    // Old declarations remain explicit HTTP connections. A pipe declaration never uses Url as fallback.
    public string Transport { get; set; } = "http";
    public string Url { get; set; } = CerebralDefaults.WorkerUrl;
    public string AccessKey { get; set; } = "";
    public LocalWorkerEndpoint? LocalEndpoint { get; set; }
    public string? LocalDataDirectory { get; set; }
    public bool AutoStartLocalServices { get; set; } = true;
    public static string FilePath => ConnectionStore.FilePath;
    public static ConnectionPreferences Load() => ConnectionStore.Read().Preferences ?? throw new InvalidOperationException("Open Integrator to repair the saved connection.");
    public CerebralClient CreateClient()
    {
        ConnectionStore.Validate(this);
        return Transport switch
        {
            "pipe" => new(LocalEndpoint!),
            "installed-pipe" => CerebralClient.ForInstalledWorker(),
            "installed-user-pipe" => LocalDevelopment.CreateInstalledUserClient(),
            _ => new(Url, AccessKey)
        };
    }
    public void ValidateHost(HostInfo host)
    {
        ConnectionStore.Validate(this);
        if (host.Product != LocalWorkerProtocol.Product || host.Protocol != LocalWorkerProtocol.Version)
            throw new InvalidOperationException("The connection returned a different product or incompatible protocol.");
        if (Transport == "pipe" && (!host.LocalRun || !Path.GetFullPath(host.DataDirectory).Equals(
            LocalWorkerDiscovery.NormalizeDataDirectory(LocalDataDirectory!), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The pipe Worker does not own the configured local data directory.");
        if (Transport == "installed-pipe")
        {
            var binding = InstalledWorkerBindingStore.ReadRegistered()
                ?? throw new InvalidOperationException("The installed Worker owner binding is unavailable. Repair it in Configuration.");
            if (host.LocalRun || !InstalledWorkerBinding.PathEquals(host.DataDirectory, binding.DataDirectory, true))
                throw new InvalidOperationException("The installed pipe Worker does not match the enrolled service data directory.");
        }
        if (Transport == "installed-user-pipe")
        {
            var enrollment = InstalledWorkerBindingStore.ReadRegisteredUser()
                ?? throw new InvalidOperationException("The installed desktop Worker enrollment is unavailable.");
            if (!host.LocalRun || !InstalledWorkerBinding.PathEquals(host.DataDirectory, enrollment.DataDirectory, true))
                throw new InvalidOperationException("The installed user Worker does not own the enrolled data directory.");
        }
    }
    public static ConnectionPreferences ForLocalPipe(LocalWorkerEndpoint endpoint, string dataDirectory, bool autoStart = true) => new()
    {
        Transport = "pipe", Url = "", AccessKey = "", LocalEndpoint = endpoint,
        LocalDataDirectory = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory), AutoStartLocalServices = autoStart
    };
    public static ConnectionPreferences ForInstalledPipe(bool autoStart = true) => new()
    {
        Transport = "installed-pipe", Url = "", AccessKey = "", LocalEndpoint = null,
        LocalDataDirectory = null, AutoStartLocalServices = autoStart
    };
    public static ConnectionPreferences ForInstalledUserPipe(bool autoStart = true) => new()
    {
        Transport = "installed-user-pipe", Url = "", AccessKey = "", LocalEndpoint = null,
        LocalDataDirectory = null, AutoStartLocalServices = autoStart
    };
}
public sealed record ConnectionSnapshot(ConnectionPreferences? Preferences, string Revision, bool Saved, string? Error);

/// <summary>One per-user connection declaration, written by Integrator and observed by the desktop consumers.</summary>
public static class ConnectionStore
{
    public const string ProcessLocalDataEnvironmentVariable = "PEGASUS_CEREBRAL_CONNECTION_DATA";
    private static readonly object Gate = new();
    private const int MaxBytes = 16_384;
    private static string? processLocalFilePath;
    private static readonly JsonSerializerOptions ReadOptions = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 8
    };
    public static string FilePath
    {
        get
        {
            lock (Gate)
                return DesktopPreferenceDiagnostics.ConnectionPath(processLocalFilePath ?? Path.Combine(InstallationPaths.CurrentUserDataDirectory, "connection.json"));
        }
    }

    /// <summary>
    /// Selects one isolated connection declaration for the lifetime of this process. Source-build
    /// launchers use this before constructing a desktop window so normal connection monitoring can
    /// exercise the complete application without reading or changing the installed user's connection.
    /// </summary>
    public static string UseProcessLocalDataDirectory(string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || dataDirectory.Contains('\0'))
            throw new ArgumentException("Process-local connection data requires an absolute directory.");
        var path = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)), "connection.json");
        lock (Gate)
        {
            if (processLocalFilePath is not null && !processLocalFilePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This process already selected a different connection data directory.");
            processLocalFilePath = path;
            return path;
        }
    }

    /// <summary>Adopts an explicit source-launch connection directory inherited by a child desktop process.</summary>
    public static bool UseInheritedProcessLocalDataDirectory()
    {
        var directory = Environment.GetEnvironmentVariable(ProcessLocalDataEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        UseProcessLocalDataDirectory(directory);
        return true;
    }

    public static ConnectionSnapshot Read(string? path = null)
    {
        path ??= FilePath;
        var revision = "unavailable";
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxBytes) throw new ArgumentException("The saved connection file exceeds 16 KiB.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            revision = Convert.ToHexString(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            LocalWorkerDiscovery.RejectDuplicateProperties(document.RootElement);
            var value = JsonSerializer.Deserialize<ConnectionPreferences>(bytes, ReadOptions) ?? throw new ArgumentException("The saved connection is empty.");
            Validate(value);
            return new(value, revision, true, null);
        }
        catch (FileNotFoundException) { return new(new(), "default", false, null); }
        catch (DirectoryNotFoundException) { return new(new(), "default", false, null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            var detail = ex is ArgumentException ? ex.Message : $"The saved connection could not be read ({ex.GetType().Name}).";
            return new(null, revision, true, detail + " Open Integrator to configure the connection.");
        }
    }

    public static ConnectionSnapshot SaveFromIntegrator(ConnectionPreferences value, string expectedRevision, string? path = null)
    {
        path ??= FilePath;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Connection storage requires an absolute path.");
        path = Path.GetFullPath(path);
        Validate(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options);
        if (bytes.Length > MaxBytes) throw new ArgumentException("Connection settings exceed 16 KiB.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Gate)
        {
            // The process-local gate alone cannot serialize Main and Configuration. Keep this
            // ordinary file handle for the full read/revision/replace sequence across sessions.
            using var lease = AcquireSaveLease(path);
            var current = Read(path);
            if (current.Revision == "unavailable")
                throw new IOException("The saved connection revision could not be verified. No replacement was written.");
            if (current.Revision != expectedRevision) throw new InvalidOperationException("Saved connection settings changed. Reload the saved connection before saving your edits.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read(path);
        }
    }

    private static FileStream AcquireSaveLease(string canonicalPath)
    {
        var leasePath = canonicalPath + ".save.lock";
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The connection save lease cannot be a linked file.");
            try { return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new IOException("Another connection writer did not release the save lease within ten seconds. No replacement was written.", error);
                Thread.Sleep(25);
            }
        }
    }

    internal static void Validate(ConnectionPreferences value)
    {
        if (value.SchemaVersion != 1) throw new ArgumentException("Unsupported connection schema version.");
        if (value.Transport == "pipe")
        {
            if (value.LocalEndpoint is null) throw new ArgumentException("A pipe connection requires its exact Worker endpoint and instance identity.");
            value.LocalEndpoint.Validate();
            _ = LocalWorkerDiscovery.NormalizeDataDirectory(value.LocalDataDirectory ?? "");
            if (value.Url != "" || value.AccessKey != "") throw new ArgumentException("A pipe connection must not contain an HTTP address or access key.");
            return;
        }
        if (value.Transport == "installed-pipe")
        {
            if (value.Url != "" || value.AccessKey != "" || value.LocalEndpoint is not null || value.LocalDataDirectory is not null)
                throw new ArgumentException("An installed pipe connection is discovered from protected machine enrollment and carries no user-supplied endpoint or key.");
            return;
        }
        if (value.Transport == "installed-user-pipe")
        {
            if (value.Url != "" || value.AccessKey != "" || value.LocalEndpoint is not null || value.LocalDataDirectory is not null)
                throw new ArgumentException("An installed user pipe is discovered from protected enrollment and carries no user-supplied endpoint or key.");
            return;
        }
        if (value.Transport != "http" || value.LocalEndpoint is not null || value.LocalDataDirectory is not null)
            throw new ArgumentException("Choose exactly one supported connection transport: HTTP, development pipe, installed user pipe, or installed service pipe.");
        if (value.Url is null || value.Url.Length > 2048 || !Uri.TryCreate(value.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/"
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use an absolute HTTP or HTTPS service origin without credentials, query or path.");
        if (value.AccessKey is null || value.AccessKey.Length > 4096 || value.AccessKey.Any(char.IsControl))
            throw new ArgumentException("The access key is missing or contains invalid characters.");
        if (!uri.IsLoopback && string.IsNullOrWhiteSpace(value.AccessKey))
            throw new ArgumentException("A non-loopback service requires its configured access key.");
    }
}
