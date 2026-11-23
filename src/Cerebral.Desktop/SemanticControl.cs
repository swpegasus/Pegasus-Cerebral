using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Client;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Desktop;

public sealed record SemanticControlEndpoint(string Product, int Protocol, string PipeName, string InstanceId,
    int ProcessId, DateTimeOffset StartedAt, string ProcessStartUtcTicks, int SessionId);
public sealed record SemanticControlRequest(string InstanceId, string RequestId, string Operation, JsonElement Arguments)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record SemanticControlResponse(string RequestId, string Operation, string State, JsonElement Data, string? Error = null)
{
    public bool Success => State is "completed" or "accepted";
}

/// <summary>Shared framing and identity contract. Application registries own operation admission and effects.</summary>
public static class SemanticControlProtocol
{
    public const int Version = 2;
    public const int MaximumArgumentCharacters = 65_536;
    public const int MaximumRequestIdCharacters = 128;
    public const int MaximumDescriptorBytes = 8192;

    public static SemanticControlEndpoint CreateEndpoint(string product, string pipePrefix)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.IsSystem || process.SessionId <= 0)
            throw new InvalidOperationException("Desktop control requires the signed-in user's session.");
        var instance = Guid.NewGuid().ToString("N");
        var endpoint = new SemanticControlEndpoint(product, Version, pipePrefix + instance, instance,
            process.Id, DateTimeOffset.UtcNow, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), process.SessionId);
        Validate(endpoint, product, pipePrefix);
        return endpoint;
    }

    public static void Validate(SemanticControlEndpoint endpoint, string product, string pipePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(pipePrefix) || endpoint.Product != product || endpoint.Protocol != Version)
            throw new InvalidDataException("The desktop-control endpoint uses an incompatible product or protocol.");
        if (!Guid.TryParseExact(endpoint.InstanceId, "N", out var instance) || instance == Guid.Empty ||
            endpoint.PipeName != pipePrefix + endpoint.InstanceId || endpoint.PipeName.Length > 160 ||
            endpoint.PipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            endpoint.ProcessId <= 0 || endpoint.SessionId <= 0 || endpoint.StartedAt == default ||
            !long.TryParse(endpoint.ProcessStartUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("The desktop-control endpoint has an invalid native identity.");
    }

    public static void Validate(SemanticControlRequest request, string instance)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId != instance || !Guid.TryParseExact(instance, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("The desktop-control generation changed. Inspect before another action.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > MaximumRequestIdCharacters ||
            request.RequestId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 128 || request.Operation.Any(char.IsControl))
            throw new ArgumentException("A bounded request ID and operation are required.");
        if (request.Extra is { Count: > 0 } || request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.GetRawText().Length > MaximumArgumentCharacters)
            throw new ArgumentException("Desktop-control arguments require a bounded JSON object.");
    }

    public static void Validate(SemanticControlResponse response, SemanticControlRequest request)
    {
        if (response.RequestId != request.RequestId || response.Operation != request.Operation ||
            response.State is not ("completed" or "accepted" or "rejected" or "uncertain" or "failed" or "conflict") || response.Data.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("The desktop-control response is invalid or unrelated.");
    }

    public static SemanticControlResponse Result(SemanticControlRequest request, string state, object? data = null, string? error = null) =>
        new(request.RequestId, request.Operation, state, CerebralJson.Element(data ?? new { }), error);

    public static string CanonicalInput(SemanticControlRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("operation", request.Operation);
            writer.WritePropertyName("arguments"); WriteCanonical(writer, request.Arguments); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

/// <summary>Same-user/session normal UI transport. No Worker discovery, launch, configuration or application action is implied.</summary>
public sealed class SemanticControlServer : IAsyncDisposable
{
    public const int MaximumClients = 4;
    public const int CompletedRetentionTarget = 1024;
    public static object ReceiptCapabilities => new { scope = "endpoint-generation-memory", completedRetentionTarget = CompletedRetentionTarget,
        maximumInFlight = MaximumClients, durable = false, retry = "Reuse identical request IDs only while retained; inspect uncertain effects before another action." };
    private readonly SemanticControlEndpoint endpoint;
    private readonly Func<SemanticControlRequest, SemanticControlResponse?> admission;
    private readonly Func<SemanticControlRequest, CancellationToken, Task<SemanticControlResponse>> dispatch;
    private readonly Func<string, bool, NamedPipeServerStream>? listenerFactory;
    private readonly string ownerSid;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim capacity = new(MaximumClients, MaximumClients);
    private readonly ConcurrentDictionary<string, Receipt> receipts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> order = new();
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object publicationGate = new();
    private FileStream? publication;
    private Task? listener;
    private bool stopped;
    private int disposed;
    private long nextClient;
    public string DescriptorPath { get; }
    public SemanticControlEndpoint Endpoint => endpoint;
    public Task Completion => listener ?? throw new InvalidOperationException("Desktop control has not started.");
    public bool IsStopping => Volatile.Read(ref disposed) != 0 || lifetime.IsCancellationRequested;

    private SemanticControlServer(SemanticControlEndpoint endpoint, string userDirectory, string descriptorName,
        Func<SemanticControlRequest, SemanticControlResponse?> admission,
        Func<SemanticControlRequest, CancellationToken, Task<SemanticControlResponse>> dispatch,
        Func<string, bool, NamedPipeServerStream>? listenerFactory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.IsSystem || process.SessionId <= 0 || endpoint.ProcessId != process.Id || endpoint.SessionId != process.SessionId ||
            endpoint.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
            throw new UnauthorizedAccessException("Normal control must be owned by the exact signed-in process.");
        ownerSid = identity.User?.Value ?? throw new UnauthorizedAccessException("The desktop owner SID is unavailable.");
        if (string.IsNullOrWhiteSpace(descriptorName) || descriptorName != Path.GetFileName(descriptorName) || descriptorName.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) || descriptorName is "." or "..")
            throw new ArgumentException("Use a fixed descriptor filename.");
        DescriptorPath = Path.Combine(ExistingWorkerDesktopOptions.NormalizeLocalDirectory(userDirectory), "Desktop", descriptorName);
        this.endpoint = endpoint; this.admission = admission; this.dispatch = dispatch; this.listenerFactory = listenerFactory;
    }

    public static async Task<SemanticControlServer> StartAsync(SemanticControlEndpoint endpoint, string userDirectory, string descriptorName,
        Func<SemanticControlRequest, SemanticControlResponse?> admission,
        Func<SemanticControlRequest, CancellationToken, Task<SemanticControlResponse>> dispatch,
        CancellationToken token = default, Func<string, bool, NamedPipeServerStream>? listenerFactory = null)
    {
        var server = new SemanticControlServer(endpoint, userDirectory, descriptorName, admission, dispatch, listenerFactory);
        try
        {
            server.listener = Task.Run(server.ListenAsync);
            await server.ready.Task.WaitAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            server.Publish(); token.ThrowIfCancellationRequested();
            if (server.listener.IsCompleted) { await server.listener.ConfigureAwait(false); throw new IOException("Normal control stopped during startup."); }
            return server;
        }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void Publish()
    {
        lock (publicationGate)
        {
            if (stopped || IsStopping) throw new IOException("Normal control stopped before descriptor publication.");
            var directory = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(Path.GetDirectoryName(DescriptorPath)!);
            Directory.CreateDirectory(directory); _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(endpoint, CerebralJson.Options);
            if (bytes.Length > SemanticControlProtocol.MaximumDescriptorBytes) throw new InvalidDataException("The descriptor exceeds its bound.");
            try { publication = OpenPublication(); }
            catch (IOException error) when (error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
            {
                // Only an existing name enters recovery. Every other publication failure
                // retains the original error, and no descriptor is ever overwritten.
                ReclaimStaleAndPublish(directory, bytes);
                return;
            }
            publication.Write(bytes); publication.Flush(true);
        }
    }

    private FileStream OpenPublication() => new(DescriptorPath, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.Read, 4096, FileOptions.DeleteOnClose);

    private void ReclaimStaleAndPublish(string directory, byte[] nextBytes)
    {
        // The normal Main already holds its application-instance lease. This file gate also
        // serves Configuration's same-user endpoint; it never claims application ownership.
        using var parent = OpenNative(directory, 0x000200A3, 3, 0x02200000);
        var parentId = RequireParent(parent, directory);
        using var old = OpenNative(DescriptorPath, 0x80010000, 0, 0x00200000);
        var oldId = RequireOldFile(old, DescriptorPath);
        var original = ReadExact(old);
        if (string.IsNullOrEmpty(endpoint.InstanceId) || !endpoint.PipeName.EndsWith(endpoint.InstanceId, StringComparison.Ordinal))
            throw new InvalidDataException("The new desktop-control endpoint has an invalid pipe identity.");
        var prefix = endpoint.PipeName[..^endpoint.InstanceId.Length];
        SemanticControlProtocol.Validate(endpoint, endpoint.Product, prefix);
        SemanticControlEndpoint previous;
        try { previous = SemanticControlIdentity.ParseEndpoint(original, endpoint.Product, prefix); }
        catch (JsonException error)
        { throw new InvalidDataException("The prior desktop-control descriptor is malformed.", error); }
        if (previous.InstanceId == endpoint.InstanceId)
            throw new InvalidDataException("The prior descriptor reused the new endpoint generation.");
        RequireOldProcessAbsent(previous.ProcessId);
        RequireParent(parent, directory, parentId);
        RequireOldFile(old, DescriptorPath, oldId, original);
        RequireOldProcessAbsent(previous.ProcessId);

        var quarantineName = Path.GetFileName(DescriptorPath) + ".stale." + previous.InstanceId + "." +
            Guid.NewGuid().ToString("N") + ".quarantine";
        var quarantinePath = Path.Combine(directory, quarantineName);
        var renamed = false;
        try
        {
            RenameHeldFile(old, parent, directory, quarantineName);
            renamed = true;
            RequireParent(parent, directory, parentId);
            RequireOldFile(old, quarantinePath, oldId, original);
            publication = OpenPublication();
            publication.Write(nextBytes); publication.Flush(true);
            RequireParent(parent, directory, parentId);
            RequireNewPublication(publication.SafeFileHandle, nextBytes);
            RequireOldFile(old, quarantinePath, oldId, original);
        }
        catch (Exception error) when (renamed && error is (IOException or UnauthorizedAccessException or InvalidDataException))
        {
            throw new IOException("The prior desktop-control descriptor was preserved as " + quarantineName +
                ", but the new endpoint was not confirmed. Inspect this exact generation before retrying.", error);
        }
    }

    private static void RequireOldProcessAbsent(int oldPid)
    {
        Process? process;
        try { process = Process.GetProcessById(oldPid); }
        catch (ArgumentException) { return; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { throw new IOException("The prior desktop-control process could not be inspected.", error); }
        using (process)
            throw new IOException("The prior desktop-control process ID is still present or reused. Its descriptor was preserved.");
    }

    private static SafeFileHandle OpenNative(string path, uint access, uint share, uint flags)
    {
        var handle = NativeCreateFile(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var code = Marshal.GetLastPInvokeError(); handle.Dispose();
        throw new IOException("The desktop-control descriptor or its directory could not be exclusively admitted.",
            new Win32Exception(code));
    }

    private NativeFileIdentity RequireParent(SafeFileHandle handle, string path, NativeFileIdentity? expected = null)
    {
        var full = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(path);
        var info = Information(handle);
        var id = Identity(info);
        if ((info.Attributes & (uint)FileAttributes.Directory) == 0 ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            expected is { } prior && id != prior || !FinalPath(handle).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The desktop-control parent directory changed or is linked.");
        RequireCurrentOwner(handle);
        return id;
    }

    private NativeFileIdentity RequireOldFile(SafeFileHandle handle, string path,
        NativeFileIdentity? expected = null, byte[]? expectedBytes = null)
    {
        var info = Information(handle);
        var id = Identity(info);
        var length = ((ulong)info.SizeHigh << 32) | info.SizeLow;
        var standard = StandardInformation(handle);
        if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0 ||
            info.Links != 1 || standard.Directory || standard.DeletePending || standard.Links != 1 ||
            standard.Length != (long)length || length is < 1 or > SemanticControlProtocol.MaximumDescriptorBytes ||
            expected is { } prior && id != prior ||
            !FinalPath(handle).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The old desktop-control descriptor is not one stable regular file.");
        RequireUnnamedStream(handle);
        RequireCurrentOwner(handle);
        if (expectedBytes is not null && !ReadExact(handle).AsSpan().SequenceEqual(expectedBytes))
            throw new IOException("The old desktop-control descriptor changed under observation.");
        return id;
    }

    private void RequireCurrentOwner(SafeFileHandle handle)
    {
        var code = NativeGetSecurityInfo(handle, 1, 1, out var owner, out _, out _, out _, out var descriptor);
        try
        {
            if (code != 0 || owner == IntPtr.Zero ||
                !new SecurityIdentifier(owner).Value.Equals(ownerSid, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The old desktop-control descriptor is not owned by this signed-in user.");
        }
        finally { if (descriptor != IntPtr.Zero) _ = NativeLocalFree(descriptor); }
    }

    private void RequireNewPublication(SafeFileHandle handle, byte[] expectedBytes)
    {
        var info = Information(handle);
        var standard = StandardInformation(handle);
        if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0 ||
            info.Links != 1 || standard.Directory || standard.Links != 1 ||
            !FinalPath(handle).Equals(Path.GetFullPath(DescriptorPath), StringComparison.OrdinalIgnoreCase) ||
            standard.Length != expectedBytes.Length)
            throw new IOException("The new desktop-control publication changed before startup completed.");
        RequireUnnamedStream(handle);
        RequireCurrentOwner(handle);
        if (!ReadExact(handle).AsSpan().SequenceEqual(expectedBytes))
            throw new IOException("The new desktop-control publication bytes changed before startup completed.");
    }

    private static byte[] ReadExact(SafeFileHandle handle)
    {
        var standard = StandardInformation(handle);
        if (standard.Length is < 1 or > SemanticControlProtocol.MaximumDescriptorBytes)
            throw new InvalidDataException("The desktop-control descriptor size is invalid.");
        var bytes = new byte[checked((int)standard.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (count <= 0) throw new IOException("The desktop-control descriptor changed while reading.");
            read += count;
        }
        return bytes;
    }

    private static void RequireUnnamedStream(SafeFileHandle handle)
    {
        const int capacity = 512;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            if (!NativeGetStreams(handle, 7, buffer, (uint)capacity))
                throw new IOException("Desktop-control stream inventory is unavailable.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            var next = Marshal.ReadInt32(buffer, 0);
            var nameBytes = Marshal.ReadInt32(buffer, 4);
            if (next != 0 || nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > capacity - 24 ||
                Marshal.PtrToStringUni(IntPtr.Add(buffer, 24), nameBytes / 2) is not ("::$DATA" or ""))
                throw new IOException("The old desktop-control descriptor has an alternate stream.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static NativeFileInformation Information(SafeFileHandle handle)
    {
        if (!NativeGetInformation(handle, out var info))
            throw new IOException("Desktop-control file identity is unavailable.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return info;
    }

    private static NativeStandardInformation StandardInformation(SafeFileHandle handle)
    {
        if (!NativeGetStandard(handle, 1, out var info, (uint)Marshal.SizeOf<NativeStandardInformation>()))
            throw new IOException("Desktop-control file state is unavailable.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return info;
    }

    private static NativeFileIdentity Identity(NativeFileInformation info) =>
        new(info.Volume, info.IndexHigh, info.IndexLow);

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(4096);
        var length = NativeGetFinalPath(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity || !buffer.ToString().StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException("Desktop-control final file path is unavailable.");
        var path = buffer.ToString()[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Desktop-control file left the selected local directory.");
        return Path.GetFullPath(path);
    }

    private static void RenameHeldFile(SafeFileHandle old, SafeFileHandle parent, string directory, string name)
    {
        if (name != Path.GetFileName(name) || name.Length > 180 || name.Any(char.IsControl))
            throw new InvalidDataException("The stale-descriptor quarantine name is invalid.");
        var normalizedDirectory = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
        if (!FinalPath(parent).Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The held desktop-control parent directory changed before quarantine.");
        var destination = Path.GetFullPath(Path.Combine(normalizedDirectory, name));
        if (!Path.GetDirectoryName(destination)!.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The stale-descriptor quarantine left the held parent directory.");
        var filename = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        // Leave room beyond the native fixed header and UTF-16 name for the
        // terminator and alignment on either pointer size.
        var nativeFixedHeaderSize = IntPtr.Size == 8 ? 24 : 16;
        var bufferSize = checked(nativeFixedHeaderSize + filename.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 0); // ReplaceIfExists=false.
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, filename.Length);
            Marshal.Copy(filename, 0, IntPtr.Add(buffer, nameOffset), filename.Length);
            if (!NativeRenameHeld(old, 3, buffer, (uint)bufferSize))
                throw new IOException("The stale desktop-control descriptor could not be preserved without replacement.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private readonly record struct NativeFileIdentity(uint Volume, uint High, uint Low);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStandardInformation
    {
        internal long Allocation, Length;
        internal uint Links;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetInformation(SafeFileHandle handle, out NativeFileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetStandard(SafeFileHandle handle, int kind,
        out NativeStandardInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetStreams(SafeFileHandle handle, int kind, IntPtr information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint NativeGetFinalPath(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeRenameHeld(SafeFileHandle handle, int kind, IntPtr information, uint size);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    private static extern uint NativeGetSecurityInfo(SafeFileHandle handle, int objectType, uint security,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr NativeLocalFree(IntPtr memory);

    private NamedPipeServerStream CreateListener(bool first) => listenerFactory?.Invoke(endpoint.PipeName, first) ?? new(endpoint.PipeName,
        PipeDirection.InOut, MaximumClients + 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 16 * 1024, 16 * 1024);

    private async Task ListenAsync()
    {
        NamedPipeServerStream? pending = null;
        try
        {
            pending = CreateListener(true); ready.TrySetResult();
            while (!lifetime.IsCancellationRequested)
            {
                await capacity.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await pending.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    var connected = pending; pending = CreateListener(false);
                    var id = Interlocked.Increment(ref nextClient);
                    var task = ServeAndReleaseAsync(connected); clients[id] = task; _ = RetireAsync(id, task);
                }
                catch { capacity.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ready.TrySetException(error); throw; }
        finally
        {
            pending?.Dispose();
            lock (publicationGate) { stopped = true; publication?.Dispose(); publication = null; }
            ready.TrySetCanceled();
        }
    }

    private async Task RetireAsync(long id, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* One failed peer never stops the listener or becomes a replay. */ }
        finally { clients.TryRemove(id, out _); }
    }
    private async Task ServeAndReleaseAsync(NamedPipeServerStream pipe)
    {
        try { await ServeAsync(pipe).ConfigureAwait(false); }
        finally { capacity.Release(); }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var request = await LocalWorkerProtocol.ReadAsync<SemanticControlRequest>(pipe, deadline.Token).ConfigureAwait(false);
                SemanticControlProtocol.Validate(request, endpoint.InstanceId);
                SemanticControlResponse response;
                InstalledWorkerClientIdentity? caller = null;
                try
                {
                    caller = InstalledWorkerWindows.ReadAuthenticatedCaller(pipe);
                    response = SemanticControlProtocol.Result(request, "rejected", new { code = "caller_not_authorized" }, "The local owner/session did not match. No operation was dispatched.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    response = SemanticControlProtocol.Result(request, "rejected", new { code = "caller_identity_unavailable", failureType = error.GetType().Name },
                        "The native caller identity was unavailable. No operation was dispatched.");
                }
                if (caller is not null && CallerMatches(caller, ownerSid, endpoint.SessionId))
                    response = await ExecuteOnceAsync(request, deadline.Token).ConfigureAwait(false);
                SemanticControlProtocol.Validate(response, request);
                await LocalWorkerProtocol.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ArgumentException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
        }
    }

    public static bool CallerMatches(InstalledWorkerClientIdentity caller, string? ownerSid, int ownerSession) =>
        !string.IsNullOrEmpty(ownerSid) && ownerSession > 0 && caller.Authenticated && !caller.Anonymous && !caller.NetworkLogon &&
        caller.UserSid == ownerSid && caller.SessionId == ownerSession;

    private Task<SemanticControlResponse> ExecuteOnceAsync(SemanticControlRequest request, CancellationToken token)
    {
        if (admission(request) is { } rejection) return Task.FromResult(rejection);
        var candidate = new Receipt(SemanticControlProtocol.CanonicalInput(request), new(() => DispatchAsync(request, token), LazyThreadSafetyMode.ExecutionAndPublication));
        var chosen = receipts.GetOrAdd(request.RequestId, candidate);
        if (chosen.Canonical != candidate.Canonical) return Task.FromResult(SemanticControlProtocol.Result(request, "conflict", error: "This request ID was accepted with different input."));
        if (ReferenceEquals(chosen, candidate))
        {
            order.Enqueue(request.RequestId);
            for (var remaining = order.Count; receipts.Count > CompletedRetentionTarget && remaining-- > 0 && order.TryDequeue(out var expired);)
            {
                if (!receipts.TryGetValue(expired, out var receipt)) continue;
                if (receipt.Response.IsValueCreated && receipt.Response.Value.IsCompleted)
                    ((ICollection<KeyValuePair<string, Receipt>>)receipts).Remove(new(expired, receipt));
                else order.Enqueue(expired);
            }
        }
        return chosen.Response.Value;
    }
    private async Task<SemanticControlResponse> DispatchAsync(SemanticControlRequest request, CancellationToken token)
    {
        try { return await dispatch(request, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return SemanticControlProtocol.Result(request, "uncertain", error: "The operation was canceled; inspect before another action."); }
        catch (Exception) { return SemanticControlProtocol.Result(request, "uncertain", error: "Dispatch failed and its effect is unconfirmed; inspect the original owner before another action."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        lock (publicationGate) { publication?.Dispose(); publication = null; }
        if (listener is not null) { try { await listener.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { } }
        var remaining = Task.WhenAll(clients.Values);
        try { await remaining.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ReleaseAfterClientsAsync(remaining); return; }
        catch (Exception) { }
        capacity.Dispose(); lifetime.Dispose();
    }
    private async Task ReleaseAfterClientsAsync(Task remaining)
    {
        try { await remaining.ConfigureAwait(false); } catch (Exception) { }
        finally { capacity.Dispose(); lifetime.Dispose(); }
    }
    private sealed record Receipt(string Canonical, Lazy<Task<SemanticControlResponse>> Response);
}

public static class SemanticControlClient
{
    public static async Task<SemanticControlResponse> ExecuteAsync(string descriptorPath, string product, string pipePrefix, string managedEntry,
        string operation, string requestId, string argumentsJson, CancellationToken token = default)
    {
        var endpoint = await SemanticControlIdentity.ReadEndpointAsync(descriptorPath, product, pipePrefix, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
        var request = new SemanticControlRequest(endpoint.InstanceId, requestId, operation, document.RootElement.Clone());
        SemanticControlProtocol.Validate(request, endpoint.InstanceId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        SemanticControlIdentity.RequireServer(pipe, endpoint, managedEntry);
        await LocalWorkerProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        var response = await LocalWorkerProtocol.ReadAsync<SemanticControlResponse>(pipe, deadline.Token).ConfigureAwait(false);
        SemanticControlProtocol.Validate(response, request); return response;
    }
}

public static class SemanticControlCommand
{
    public static async Task<int> RunAsync(string[] args, string flag, string product, string pipePrefix, string entry)
    {
        if (args.Length != 5 || args[0] != flag)
        {
            Console.Error.WriteLine($"Usage: {flag} <absolute-descriptor.json> <operation> <request-id> <arguments-json>");
            return 2;
        }
        var json = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        try
        {
            var response = await SemanticControlClient.ExecuteAsync(args[1], product, pipePrefix, entry, args[2], args[3], args[4]);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = response.Success, id = response.RequestId, op = response.Operation,
                state = response.State, data = response.Data, error = response.Error }, json));
            return response.Success ? 0 : response.State == "uncertain" ? 3 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }, json)); return 1;
        }
    }
}
