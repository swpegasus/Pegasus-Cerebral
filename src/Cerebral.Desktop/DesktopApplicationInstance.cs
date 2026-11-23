using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cerebral.Desktop;

/// <summary>The connection identity requested by one desktop launch.</summary>
public sealed record DesktopApplicationIdentity
{
    private static readonly string[] LocalModes = ["source-local", "installed-service", "installed-user", "local-worker", "desktop-instance"];
    private static readonly string[] KnownModes = [.. LocalModes, "network-worker", "application"];

    public string Mode { get; }
    public string? DataRoot { get; }
    public string? Endpoint { get; }

    [JsonConstructor]
    public DesktopApplicationIdentity(string mode, string? dataRoot, string? endpoint)
    {
        if (!KnownModes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentException("The desktop launch mode is unsupported.", nameof(mode));
        Mode = mode;
        if (LocalModes.Contains(mode, StringComparer.Ordinal))
        {
            if (mode is not ("installed-service" or "installed-user") || dataRoot is not null)
            {
                if (string.IsNullOrWhiteSpace(dataRoot))
                    throw new ArgumentException("The desktop data root must be an absolute local path.", nameof(dataRoot));
                if (!Path.IsPathFullyQualified(dataRoot) || dataRoot.Contains('\0'))
                    throw new ArgumentException("The desktop data root must be an absolute local path.", nameof(dataRoot));
                dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
            }
            if (endpoint is not null) throw new ArgumentException("A local desktop identity cannot contain a network endpoint.", nameof(endpoint));
        }
        else
        {
            if (dataRoot is not null) throw new ArgumentException("A non-local desktop identity cannot contain a data root.", nameof(dataRoot));
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048 || endpoint.Any(char.IsControl))
                throw new ArgumentException("The desktop endpoint identity is invalid.", nameof(endpoint));
        }
        DataRoot = dataRoot;
        Endpoint = endpoint;
    }

    public static DesktopApplicationIdentity DesktopInstance(string userDataRoot) => new("desktop-instance", userDataRoot, null);
    public static DesktopApplicationIdentity SourceLocal(string dataRoot) => new("source-local", dataRoot, null);
    public static DesktopApplicationIdentity InstalledService(string? dataRoot) => new("installed-service", dataRoot, null);
    public static DesktopApplicationIdentity InstalledUser(string? dataRoot) => new("installed-user", dataRoot, null);
    public static DesktopApplicationIdentity LocalWorker(string dataRoot) => new("local-worker", dataRoot, null);
    public static DesktopApplicationIdentity NetworkWorker(string endpoint) => new("network-worker", null, endpoint);
    public static DesktopApplicationIdentity Application(string applicationId) => new("application", null, applicationId);

    public bool Matches(DesktopApplicationIdentity other) =>
        Mode.Equals(other.Mode, StringComparison.Ordinal) &&
        string.Equals(DataRoot, other.DataRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Endpoint, other.Endpoint, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Mode switch
    {
        "desktop-instance" => $"Desktop instance | user data root {DataRoot}",
        "source-local" => $"Source local | data root {DataRoot}",
        "installed-service" => DataRoot is null
            ? "Installed service | registered data root unavailable"
            : $"Installed service | data root {DataRoot}",
        "installed-user" => DataRoot is null
            ? "Installed desktop Worker | registered data root unavailable"
            : $"Installed desktop Worker | data root {DataRoot}",
        "local-worker" => $"Local Worker | data root {DataRoot}",
        "network-worker" => $"Network Worker | endpoint {Endpoint}",
        _ => $"Application | {Endpoint}"
    };
}

/// <summary>A point-in-time observation of an existing desktop ownership object, not owner or code proof.</summary>
public enum DesktopApplicationPresence { Absent, PresentOrUnconfirmed, Unavailable }

/// <summary>One visible desktop application instance per signed-in user and Windows session.</summary>
public sealed class DesktopApplicationInstance : IDisposable
{
    private const int RestoreWindow = 9;
    private const int IdentityCapacity = 8192;
    private const int DescriptorVersion = 1;
    private static readonly JsonSerializerOptions DescriptorJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };
    private readonly Mutex mutex;
    private readonly EventWaitHandle activate;
    private readonly EventWaitHandle identityReady;
    private readonly Mutex activationRequestGate;
    private readonly MemoryMappedFile identityMap;
    private readonly MemoryMappedFile activationRequestMap;
    private readonly DesktopApplicationIdentity requested;
    private RegisteredWaitHandle? activationWait;
    private OwnerDescriptor? owner;
    private bool disposed;

    public bool IsPrimary { get; }
    public string RequestedIdentity => requested.ToString();
    public string OwnerIdentity => owner?.Identity.ToString() ?? "Unknown owner identity";
    public string RequestedMode => requested.Mode;
    public string? RequestedDataRoot => requested.DataRoot;
    public string? OwnerMode => owner?.Identity.Mode;
    public string? OwnerDataRoot => owner?.Identity.DataRoot;
    public int? OwnerProcessId => owner?.ProcessId;
    public DateTimeOffset? OwnerStartedAt => owner is null ? null : new DateTimeOffset(owner.ProcessStartUtcTicks, TimeSpan.Zero);
    public bool OwnerIsCurrent => OwnerMatchesLiveProcess(owner);
    public bool IdentityMatches => owner is not null && requested.Matches(owner.Identity);

    public DesktopApplicationInstance(string applicationId)
        : this(applicationId, DesktopApplicationIdentity.Application(applicationId)) { }

    public DesktopApplicationInstance(string applicationId, DesktopApplicationIdentity requestedIdentity)
    {
        ArgumentNullException.ThrowIfNull(requestedIdentity);
        var name = GetInstanceObjectName(applicationId);
        requested = requestedIdentity;
        Mutex? createdMutex = null;
        EventWaitHandle? createdActivate = null;
        EventWaitHandle? createdReady = null;
        Mutex? createdRequestGate = null;
        MemoryMappedFile? createdIdentityMap = null;
        MemoryMappedFile? createdRequestMap = null;
        var primary = false;
        try
        {
            createdActivate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
            createdReady = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".IdentityReady");
            createdRequestGate = new Mutex(false, name + ".ActivationRequestGate");
            createdMutex = new Mutex(false, name + ".Owner");
            try { primary = createdMutex.WaitOne(0); }
            catch (AbandonedMutexException) { primary = true; }
            createdIdentityMap = MemoryMappedFile.CreateOrOpen(name + ".Identity", IdentityCapacity, MemoryMappedFileAccess.ReadWrite);
            createdRequestMap = MemoryMappedFile.CreateOrOpen(name + ".ActivationRequest", IdentityCapacity, MemoryMappedFileAccess.ReadWrite);

            mutex = createdMutex;
            activate = createdActivate;
            identityReady = createdReady;
            activationRequestGate = createdRequestGate;
            identityMap = createdIdentityMap;
            activationRequestMap = createdRequestMap;
            IsPrimary = primary;
            if (IsPrimary)
            {
                identityReady.Reset();
                ClearEnvelope(activationRequestMap);
                using var process = Process.GetCurrentProcess();
                owner = new(DescriptorVersion, requested, process.Id, process.StartTime.ToUniversalTime().Ticks);
                WriteEnvelope(identityMap, owner);
                identityReady.Set();
            }
            else
            {
                owner = ReadCurrentOwner(identityReady, identityMap);
            }
        }
        catch
        {
            createdRequestMap?.Dispose();
            createdIdentityMap?.Dispose();
            createdRequestGate?.Dispose();
            createdReady?.Dispose();
            createdActivate?.Dispose();
            if (primary) try { createdMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            createdMutex?.Dispose();
            throw;
        }
    }

    /// <summary>Requests activation only when the live owner reports the same mode and canonical data root.</summary>
    public bool RequestActivation()
    {
        if (IsPrimary) return false;
        owner = ReadCurrentOwner(identityReady, identityMap);
        if (!OwnerIsCurrent || !IdentityMatches) return false;
        var entered = false;
        try
        {
            try { entered = activationRequestGate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) return false;
            WriteEnvelope(activationRequestMap, new ActivationRequest(DescriptorVersion, requested, Guid.NewGuid().ToString("N")));
        }
        finally { if (entered) activationRequestGate.ReleaseMutex(); }
        if (OperatingSystem.IsWindows()) _ = AllowSetForegroundWindow(uint.MaxValue);
        activate.Set();
        return true;
    }

    /// <summary>
    /// Signals an already-running matching instance without creating instance resources or claiming
    /// enduring ownership. True acknowledges signal submission, not foreground focus or callback completion.
    /// False means unavailable or unconfirmed; it does not authorize a launch or an automatic retry.
    /// </summary>
    public static bool TryActivateExisting(string applicationId, DesktopApplicationIdentity requestedIdentity)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return TryActivateExistingCore(applicationId, requestedIdentity,
            id => ExistingActivationChannel.Open(GetInstanceObjectName(id)));
    }

    /// <summary>Observes only an already-existing owner mutex without creating, acquiring,
    /// waiting on or signaling instance resources. Absent is a non-atomic object observation,
    /// not permission to replace an owner. PresentOrUnconfirmed does not identify its PID or code.
    /// The caller must qualify the actual desktop process separately and refuse unknown owners.</summary>
    public static DesktopApplicationPresence ObserveExisting(string applicationId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop ownership observation requires Windows.");
        if (applicationId is null || applicationId.Length > 256)
            throw new ArgumentException("Supply a bounded desktop application ID.", nameof(applicationId));
        try
        {
            var name = GetInstanceObjectName(applicationId) + ".Owner";
            // Open with synchronization access only; do not request modification or acquire ownership.
            using var existing = OpenExistingOwnerMutex(0x00100000, false, name);
            if (!existing.IsInvalid) return DesktopApplicationPresence.PresentOrUnconfirmed;
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            // OpenExisting/TryOpenExisting collapse missing and incompatible object types. Only
            // ERROR_FILE_NOT_FOUND proves this named owner object was absent at the observation.
            return error == 2 ? DesktopApplicationPresence.Absent : DesktopApplicationPresence.Unavailable;
        }
        catch (UnauthorizedAccessException) { return DesktopApplicationPresence.Unavailable; }
        catch (IOException) { return DesktopApplicationPresence.Unavailable; }
        catch (System.Security.SecurityException) { return DesktopApplicationPresence.Unavailable; }
    }

    private static string GetInstanceObjectName(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '.' and not '-'))
            throw new ArgumentException("The application ID may contain only ASCII letters, digits, periods, and hyphens.", nameof(applicationId));
        using var currentUser = WindowsIdentity.GetCurrent();
        var user = currentUser.User?.Value ?? Environment.UserName.Replace('\\', '.');
        return @"Local\" + applicationId + "." + user;
    }

    // The channel seam exercises this exact protocol without OS objects, process probes or focus.
    internal static bool TryActivateExistingCore(string applicationId, DesktopApplicationIdentity? requestedIdentity,
        Func<string, IExistingActivationChannel> open)
    {
        if (requestedIdentity is null || string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 ||
            applicationId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '.' and not '-')) return false;
        try
        {
            using var channel = open(applicationId);
            var observed = channel.ReadOwner(waitForReady: true);
            if (!MatchesRequestedOwner(observed, requestedIdentity) || !channel.OwnerIsCurrent(observed!) ||
                !channel.OwnerIsHeld()) return false;
            if (!channel.TryEnterRequestGate()) return false;
            try
            {
                var current = channel.ReadOwner(waitForReady: false);
                if (!SameOwner(observed!, current) || !MatchesRequestedOwner(current, requestedIdentity) ||
                    !channel.OwnerIsCurrent(current!) || !channel.OwnerIsHeld()) return false;
                channel.WriteRequest(requestedIdentity);
                // The receiver also validates the requested identity. Do not signal a changed owner
                // after writing; a request may remain unconsumed, so failure never implies non-delivery.
                var beforeSignal = channel.ReadOwner(waitForReady: false);
                if (!SameOwner(current!, beforeSignal) || !MatchesRequestedOwner(beforeSignal, requestedIdentity) ||
                    !channel.OwnerIsCurrent(beforeSignal!) || !channel.OwnerIsHeld()) return false;
                return channel.Signal(current!.ProcessId);
            }
            finally { channel.ExitRequestGate(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or WaitHandleCannotBeOpenedException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool MatchesRequestedOwner(OwnerDescriptor? owner, DesktopApplicationIdentity requestedIdentity) =>
        owner is { Version: DescriptorVersion, ProcessId: > 0, ProcessStartUtcTicks: > 0, Identity: not null } &&
        requestedIdentity.Matches(owner.Identity);

    private static bool SameOwner(OwnerDescriptor expected, OwnerDescriptor? current) =>
        current is { Identity: not null } && expected.Version == current.Version &&
        expected.ProcessId == current.ProcessId && expected.ProcessStartUtcTicks == current.ProcessStartUtcTicks &&
        expected.Identity.Matches(current.Identity);

    internal static bool ProbeHeldOwner(Func<bool> tryAcquire, Action release)
    {
        var acquired = false;
        try
        {
            try { acquired = tryAcquire(); }
            catch (AbandonedMutexException) { acquired = true; }
            return !acquired;
        }
        finally { if (acquired) release(); }
    }

    internal interface IExistingActivationChannel : IDisposable
    {
        OwnerDescriptor? ReadOwner(bool waitForReady);
        bool OwnerIsCurrent(OwnerDescriptor owner);
        bool OwnerIsHeld();
        bool TryEnterRequestGate();
        void ExitRequestGate();
        void WriteRequest(DesktopApplicationIdentity requestedIdentity);
        bool Signal(int ownerProcessId);
    }

    private sealed class ExistingActivationChannel : IExistingActivationChannel
    {
        private readonly EventWaitHandle activate;
        private readonly EventWaitHandle ready;
        private readonly Mutex requestGate;
        private readonly Mutex ownerGate;
        private readonly MemoryMappedFile identity;
        private readonly MemoryMappedFile requests;

        private ExistingActivationChannel(EventWaitHandle activate, EventWaitHandle ready, Mutex requestGate, Mutex ownerGate,
            MemoryMappedFile identity, MemoryMappedFile requests)
        {
            this.activate = activate; this.ready = ready; this.requestGate = requestGate;
            this.ownerGate = ownerGate;
            this.identity = identity; this.requests = requests;
        }

        internal static ExistingActivationChannel Open(string name)
        {
            EventWaitHandle? activate = null, ready = null;
            Mutex? gate = null, owner = null;
            MemoryMappedFile? identity = null, requests = null;
            try
            {
                // OpenExisting is intentional at every site. This path never establishes primary
                // ownership, publishes identity, clears a request or creates a missing object.
                activate = EventWaitHandle.OpenExisting(name + ".Activate");
                ready = EventWaitHandle.OpenExisting(name + ".IdentityReady");
                gate = Mutex.OpenExisting(name + ".ActivationRequestGate");
                owner = Mutex.OpenExisting(name + ".Owner");
                identity = MemoryMappedFile.OpenExisting(name + ".Identity", MemoryMappedFileRights.Read);
                requests = MemoryMappedFile.OpenExisting(name + ".ActivationRequest", MemoryMappedFileRights.ReadWrite);
                return new(activate, ready, gate, owner, identity, requests);
            }
            catch
            {
                requests?.Dispose(); identity?.Dispose(); owner?.Dispose(); gate?.Dispose(); ready?.Dispose(); activate?.Dispose();
                throw;
            }
        }

        public OwnerDescriptor? ReadOwner(bool waitForReady) => waitForReady
            ? ReadCurrentOwner(ready, identity)
            : ready.WaitOne(0) ? ReadEnvelope<OwnerDescriptor>(identity) : null;

        public bool OwnerIsCurrent(OwnerDescriptor owner) => OwnerMatchesLiveProcess(owner);

        // A descriptor can outlive its primary. Available/abandoned is unavailable, and a
        // successful probe acquisition is released immediately without publishing an owner.
        public bool OwnerIsHeld() => ProbeHeldOwner(() => ownerGate.WaitOne(0), () => ownerGate.ReleaseMutex());

        public bool TryEnterRequestGate()
        {
            try { return requestGate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { return true; }
        }

        public void ExitRequestGate() => requestGate.ReleaseMutex();

        public void WriteRequest(DesktopApplicationIdentity requestedIdentity) =>
            WriteEnvelope(requests, new ActivationRequest(DescriptorVersion, requestedIdentity, Guid.NewGuid().ToString("N")));

        public bool Signal(int ownerProcessId)
        {
            _ = AllowSetForegroundWindow((uint)ownerProcessId);
            return activate.Set();
        }

        public void Dispose()
        {
            requests.Dispose(); identity.Dispose(); ownerGate.Dispose(); requestGate.Dispose(); ready.Dispose(); activate.Dispose();
        }
    }

    public void Listen(Dispatcher dispatcher, Action show)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the owning instance can receive activation requests.");
        activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
        {
            if (disposed) return;
            var request = TakeActivationRequest();
            if (request?.Version != DescriptorVersion || !requested.Matches(request.Identity)) return;
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                dispatcher.BeginInvoke(new Action(() => { if (!disposed) show(); }));
        }, null, Timeout.Infinite, false);
    }

    private ActivationRequest? TakeActivationRequest()
    {
        var entered = false;
        try
        {
            try { entered = activationRequestGate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) return null;
            var request = ReadEnvelope<ActivationRequest>(activationRequestMap);
            ClearEnvelope(activationRequestMap);
            return request;
        }
        finally { if (entered) activationRequestGate.ReleaseMutex(); }
    }

    private static bool OwnerMatchesLiveProcess(OwnerDescriptor? value)
    {
        if (value?.Version != DescriptorVersion || value.ProcessId <= 0 || value.ProcessStartUtcTicks <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(value.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == value.ProcessStartUtcTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static OwnerDescriptor? ReadCurrentOwner(EventWaitHandle ready, MemoryMappedFile map)
    {
        var deadline = Stopwatch.StartNew();
        OwnerDescriptor? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var remaining = TimeSpan.FromSeconds(2) - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero || !ready.WaitOne(remaining)) break;
            last = ReadEnvelope<OwnerDescriptor>(map);
            if (OwnerMatchesLiveProcess(last)) return last;
            Thread.Sleep(20);
        }
        return last;
    }

    private static void WriteEnvelope<T>(MemoryMappedFile map, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DescriptorJson);
        if (bytes.Length <= 0 || bytes.Length > IdentityCapacity - sizeof(int))
            throw new InvalidOperationException("The desktop instance descriptor exceeds its bounded shared-memory envelope.");
        using var view = map.CreateViewAccessor(0, IdentityCapacity, MemoryMappedFileAccess.Write);
        view.Write(0, bytes.Length);
        view.WriteArray(sizeof(int), bytes, 0, bytes.Length);
        view.Flush();
    }

    private static T? ReadEnvelope<T>(MemoryMappedFile map) where T : class
    {
        try
        {
            using var view = map.CreateViewAccessor(0, IdentityCapacity, MemoryMappedFileAccess.Read);
            var length = view.ReadInt32(0);
            if (length <= 0 || length > IdentityCapacity - sizeof(int)) return null;
            var bytes = new byte[length];
            if (view.ReadArray(sizeof(int), bytes, 0, length) != length) return null;
            return JsonSerializer.Deserialize<T>(bytes, DescriptorJson);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or IOException or NotSupportedException)
        { return null; }
    }

    private static void ClearEnvelope(MemoryMappedFile map)
    {
        using var view = map.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Write);
        view.Write(0, 0);
        view.Flush();
    }

    public static void Show(Window window)
    {
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Show(window)));
            return;
        }
        window.ShowActivated = true;
        if (!window.IsVisible) window.Show();
        var minimized = RestoreMinimizedState(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (minimized && handle != IntPtr.Zero) _ = ShowWindowAsync(handle, RestoreWindow);
        if (handle != IntPtr.Zero) _ = SetForegroundWindow(handle);
        _ = window.Activate(); _ = window.Focus();
    }

    internal static bool RestoreMinimizedState(Window window)
    {
        var minimized = window.WindowState == WindowState.Minimized;
        if (minimized) window.WindowState = WindowState.Normal;
        return minimized;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        activationWait?.Unregister(null);
        activationRequestMap.Dispose();
        identityMap.Dispose();
        activationRequestGate.Dispose();
        identityReady.Dispose();
        activate.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    internal sealed record OwnerDescriptor(int Version, DesktopApplicationIdentity Identity, int ProcessId, long ProcessStartUtcTicks);
    private sealed record ActivationRequest(int Version, DesktopApplicationIdentity Identity, string RequestId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "OpenMutexW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeWaitHandle OpenExistingOwnerMutex(uint desiredAccess,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inheritHandle, string name);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
