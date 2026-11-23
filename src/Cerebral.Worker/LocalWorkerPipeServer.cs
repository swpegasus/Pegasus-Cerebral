using System.Collections.Concurrent;
using System.IO.Pipes;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Local user-host adapter for the shared dispatcher. The Windows pipe ACL is the owner boundary;
/// a profile in the request can only narrow that owner's saved grants. This is not the privileged
/// Cortex application pipe, and must not be enabled for a LocalSystem host as a desktop endpoint.
/// </summary>
public sealed class LocalWorkerPipeServer : BackgroundService, IAsyncDisposable
{
    private const int MaxClients = 8;
    private readonly LocalWorkerEndpoint endpoint;
    private readonly InstalledWorkerBinding? installedBinding;
    private readonly InstalledUserWorkerBinding? installedUserEnrollment;
    private readonly LocalUserOwnerBinding? localOwnerBinding;
    private readonly ICommandDispatcher commands;
    private readonly EventHub events;
    private readonly Func<NamedPipeServerStream, CancellationToken, Task> waitForConnection;
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly SemaphoreSlim capacity = new(MaxClients, MaxClients);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextClient;
    private int disposed;

    public LocalWorkerPipeServer(LocalWorkerEndpoint endpoint, ICommandDispatcher commands, EventHub events)
        : this(endpoint, commands, events, null,
            static (pipe, token) => pipe.WaitForConnectionAsync(token)) { }

    internal LocalWorkerPipeServer(LocalWorkerEndpoint endpoint, ICommandDispatcher commands, EventHub events,
        Func<NamedPipeServerStream, CancellationToken, Task> waitForConnection)
        : this(endpoint, commands, events, null, waitForConnection) { }

    internal LocalWorkerPipeServer(LocalWorkerEndpoint endpoint, ICommandDispatcher commands, EventHub events,
        LocalUserOwnerBinding? localOwnerBinding)
        : this(endpoint, commands, events, localOwnerBinding,
            static (pipe, token) => pipe.WaitForConnectionAsync(token)) { }

    internal LocalWorkerPipeServer(LocalWorkerEndpoint endpoint, ICommandDispatcher commands, EventHub events,
        LocalUserOwnerBinding? localOwnerBinding, InstalledUserWorkerBinding installedUserEnrollment)
        : this(endpoint, commands, events, localOwnerBinding)
    {
        InstalledUserWorkerAdmission.RequireEndpoint(installedUserEnrollment, endpoint);
        this.installedUserEnrollment = installedUserEnrollment;
    }

    internal LocalWorkerPipeServer(LocalWorkerEndpoint endpoint, ICommandDispatcher commands, EventHub events,
        LocalUserOwnerBinding? localOwnerBinding,
        Func<NamedPipeServerStream, CancellationToken, Task> waitForConnection)
    {
        endpoint.Validate();
        this.endpoint = endpoint;
        this.commands = commands;
        this.events = events;
        this.localOwnerBinding = localOwnerBinding;
        this.waitForConnection = waitForConnection ?? throw new ArgumentNullException(nameof(waitForConnection));
    }

    public LocalWorkerPipeServer(InstalledWorkerBinding binding, ICommandDispatcher commands, EventHub events)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        installedBinding = binding;
        endpoint = binding.PipeEndpoint();
        this.commands = commands;
        this.events = events;
        waitForConnection = static (pipe, token) => pipe.WaitForConnectionAsync(token);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (installedBinding is null) ValidateHostingIdentity();
        else ValidateInstalledHostingIdentity(installedBinding);
        if (installedUserEnrollment is { } desktop)
        {
            if (InstalledWorkerBindingStore.ReadRegisteredUser() != desktop)
                throw new UnauthorizedAccessException("The installed desktop Worker enrollment changed during startup.");
            InstalledUserWorkerAdmission.RequireHost(desktop, InstalledWorkerWindows.ReadCurrentProcessIdentity());
        }
        await base.StartAsync(cancellationToken);
        await ready.Task.WaitAsync(cancellationToken);
    }

    internal static void ValidateHostingIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The local application pipe requires Windows.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.IsSystem) throw new InvalidOperationException("The user application pipe cannot run as LocalSystem. Installed service owner binding is not configured for this transport.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var connections = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        NamedPipeServerStream? listener = null;
        try
        {
            listener = CreateListener(first: true);
            ready.TrySetResult();
            var consecutiveClosedAccepts = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await capacity.WaitAsync(stoppingToken);
                var capacityOwned = true;
                try
                {
                    try { await waitForConnection(listener, stoppingToken); }
                    catch (IOException error) when (IsClosedAccept(error, listener, stoppingToken))
                    {
                        capacity.Release();
                        capacityOwned = false;
                        // Retain the old handle until a new listener exists so another host
                        // cannot claim the first-instance pipe name during recovery.
                        var failed = listener;
                        listener = CreateListener(first: false);
                        failed.Dispose();
                        consecutiveClosedAccepts = Math.Min(consecutiveClosedAccepts + 1, 5);
                        await Task.Delay(TimeSpan.FromMilliseconds(
                            Math.Min(1000, 50 << (consecutiveClosedAccepts - 1))), stoppingToken);
                        continue;
                    }
                    // Always retain a listening instance while servicing clients. A second host's
                    // FirstPipeInstance creation then fails instead of sharing our pipe namespace.
                    var connected = listener;
                    listener = CreateListener(first: false);
                    var id = Interlocked.Increment(ref nextClient);
                    var task = ServeAndReleaseAsync(connected, connections.Token);
                    capacityOwned = false;
                    clients[id] = task;
                    _ = RetireAsync(id, task);
                    consecutiveClosedAccepts = 0;
                }
                catch
                {
                    if (capacityOwned) capacity.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            throw;
        }
        finally
        {
            await connections.CancelAsync();
            listener?.Dispose();
            ready.TrySetCanceled(stoppingToken.IsCancellationRequested ? stoppingToken : new CancellationToken(true));
            await Task.WhenAll(clients.Values);
        }
    }

    private static bool IsClosedAccept(IOException error, NamedPipeServerStream? listener,
        CancellationToken stoppingToken)
    {
        // HRESULT_FROM_WIN32(ERROR_NO_DATA): a peer closed during ConnectNamedPipe.
        // Other IO, access, listener creation and shutdown errors remain fatal.
        return OperatingSystem.IsWindows() && !stoppingToken.IsCancellationRequested &&
            error.HResult == unchecked((int)0x800700E8) && listener is not null &&
            !listener.SafePipeHandle.IsClosed && !listener.SafePipeHandle.IsInvalid;
    }

    private NamedPipeServerStream CreateListener(bool first)
    {
        if (installedBinding is { } binding)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installed owner pipe requires Windows.");
            return InstalledWorkerWindows.CreateServer(endpoint.PipeName, binding.ServiceAccountSid, binding.OwnerSid, first, MaxClients + 1);
        }
        return new(endpoint.PipeName, PipeDirection.InOut, MaxClients + 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            16 * 1024, 16 * 1024);
    }

    private async Task RetireAsync(long id, Task task)
    {
        try { await task; }
        finally { clients.TryRemove(id, out _); }
    }

    private async Task ServeAndReleaseAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try { await ServeAsync(pipe, token); }
        finally { capacity.Release(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            using var abort = lifetime.Token.Register(static value => ((NamedPipeServerStream)value!).Dispose(), pipe);
            Task? disconnected = null;
            try
            {
                using (var admission = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    admission.CancelAfter(TimeSpan.FromSeconds(10));
                    var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, admission.Token);
                    LocalWorkerProtocol.ValidateHello(hello, endpoint);
                    if (installedBinding is { } binding)
                        RequireInstalledOwnerCaller(pipe, binding);
                    if (installedUserEnrollment is { } desktop)
                    {
                        if (InstalledWorkerBindingStore.ReadRegisteredUser() != desktop)
                            throw new UnauthorizedAccessException("The installed desktop Worker enrollment changed.");
                        var caller = InstalledWorkerWindows.ReadAuthenticatedCaller(pipe);
                        InstalledUserWorkerAdmission.RequireOwnerClient(desktop, caller,
                            InstalledWorkerWindows.ReadCurrentProcessIdentity().SessionId);
                    }
                    await LocalWorkerProtocol.WriteAsync(pipe,
                        new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, endpoint.InstanceId), admission.Token);
                }
                var requestStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                LocalWorkerRequest request;
                var frameReader = new OwnerSnapshotFrameReader(pipe);
                using (var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    request = await LocalWorkerProtocol.ReadAsync<LocalWorkerRequest>(frameReader, requestDeadline.Token);
                    LocalWorkerProtocol.ValidateRequest(request);
                }
                if (request.Kind == "command" && request.Command is { } command)
                {
                    disconnected = ObserveDisconnectAsync(pipe, lifetime);
                    CommandResult result;
                    if (OwnerModelSnapshotService.IsOwnerOperation(command.Operation))
                    {
                        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var remaining = OwnerModelSnapshotService.Deadline - System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted);
                        if (remaining <= TimeSpan.Zero) inspection.Cancel();
                        else inspection.CancelAfter(remaining);
                        try
                        {
                            result = commands is IOwnerModelSnapshotDispatcher owner
                                ? await owner.ExecuteOwnerSnapshotPipeAsync(pipe, installedBinding, command, frameReader.FrameBytes, inspection.Token)
                                : CommandResult.Fail("snapshot_unsupported");
                        }
                        catch (UnauthorizedAccessException) { result = CommandResult.Fail("owner_admission_denied"); }
                        catch (OwnerSnapshotUnavailableException error) { result = CommandResult.Fail(error.Code); }
                        catch (OwnerRuntimeSnapshotUnavailableException) { result = CommandResult.Fail("runtime_snapshot_unavailable"); }
                        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                        { result = CommandResult.Fail("snapshot_deadline"); }
                        // Preserve the remaining request deadline through backpressure. A reader
                        // that stops consuming a bounded reply must not retain a host client slot.
                        // Once the deadline expires, closing this pipe is the truthful outcome.
                        await LocalWorkerProtocol.WriteAsync(pipe, result, inspection.Token);
                    }
                    else if (CommandDispatcher.HasOwnerVerificationField(command))
                    {
                        result = commands is IOwnerWorkflowSubmissionDispatcher owner
                            ? await owner.ExecuteOwnerVerificationPipeAsync(pipe, installedBinding,
                                localOwnerBinding, command, lifetime.Token)
                            : CommandResult.Fail("owner_verification_unsupported");
                        await LocalWorkerProtocol.WriteAsync(pipe, result, lifetime.Token);
                    }
                    else
                    {
                        result = await commands.ExecuteAsync(command, lifetime.Token);
                        await LocalWorkerProtocol.WriteAsync(pipe, result, lifetime.Token);
                    }
                }
                else if (request.Kind == "events" && request.Command is null)
                {
                    disconnected = ObserveDisconnectAsync(pipe, lifetime);
                    var subscription = events.Subscribe();
                    try
                    {
                        await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Ok(new { subscribed = true }), lifetime.Token);
                        await foreach (var item in subscription.Reader.ReadAllAsync(lifetime.Token))
                            await LocalWorkerProtocol.WriteAsync(pipe, item, lifetime.Token);
                    }
                    finally { events.Unsubscribe(subscription.Id); }
                }
                else throw new InvalidDataException("Use a command request or an event subscription.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { }
            catch (Exception)
            {
                // A dispatcher bug affects this caller, not every connected window. Return no
                // implementation detail, and make the unsuccessful outcome explicit when possible.
                try { await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Fail("The local command failed. Inspect the current job before retrying."), lifetime.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            finally
            {
                // Closing the interface cancels this command's wait, not jobs already accepted by
                // the service scheduler. A disconnected mutation is queried, never blindly replayed.
                await lifetime.CancelAsync();
                pipe.Dispose();
                if (disconnected is not null) await disconnected;
            }
        }
    }

    private static void ValidateInstalledHostingIdentity(InstalledWorkerBinding binding)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installed owner pipe requires Windows.");
        var registered = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new UnauthorizedAccessException("The installed Worker owner binding is not registered.");
        if (registered != binding)
            throw new UnauthorizedAccessException("The installed Worker binding changed during startup.");
        InstalledWorkerAdmission.RequireServiceHost(binding, InstalledWorkerWindows.ReadService(),
            InstalledWorkerWindows.ReadCurrentProcessIdentity());
    }

    private static void RequireInstalledOwnerCaller(NamedPipeServerStream pipe, InstalledWorkerBinding binding)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installed owner pipe requires Windows.");
        _ = InstalledWorkerWindows.RequireOwnerCaller(pipe, binding);
    }

    private static async Task ObserveDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try
        {
            // Exactly one request per connection. EOF or another byte both end this connection;
            // pipelining and unbounded unread request buffers are deliberately unsupported.
            await pipe.ReadExactlyAsync(new byte[1], lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { await lifetime.CancelAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await StopAsync(deadline.Token);
        Dispose();
        // An ill-behaved plugin may ignore cancellation. Do not hang application shutdown or
        // dispose a semaphore its eventual cleanup still owns. Its command outcome stays unknown.
        if (ExecuteTask is { IsCompleted: false } pending)
            _ = pending.ContinueWith(_ => capacity.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else capacity.Dispose();
    }
}
