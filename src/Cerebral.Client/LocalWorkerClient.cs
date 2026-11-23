using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Transport failed after a command frame began dispatch. The Worker may have accepted it;
/// callers must query the existing durable request rather than replaying the command.</summary>
public sealed class LocalWorkerCommandUncertainException : IOException
{
    public string Operation { get; }
    public LocalWorkerCommandUncertainException(string operation, Exception innerException)
        : base($"The outcome of local Cerebral command '{operation}' could not be confirmed after dispatch began. " +
            "Query the existing request or current service state before taking another action. The command was not replayed.", innerException)
        => Operation = operation;
}

/// <summary>Windows, same-user local transport. Every operation verifies the expected Worker instance
/// on a fresh pipe. It never falls back to another transport or retries a dispatched command.</summary>
public sealed class LocalWorkerClient : IDisposable
{
    private readonly LocalWorkerEndpoint? endpoint;
    private readonly LocalWorkerExpectedOwner? expectedOwner;
    private readonly bool installed;
    private readonly bool installedUser;
    private readonly InstalledBindingCapture? installedBinding;
    private readonly InstalledUserWorkerBinding? installedUserEnrollment;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public LocalWorkerClient(LocalWorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local Cerebral named-pipe client requires Windows same-user pipe security.");
        this.endpoint = endpoint;
    }

    public LocalWorkerClient(LocalWorkerExpectedOwner expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The verified local owner client requires Windows.");
        this.expectedOwner = expectedOwner;
        endpoint = expectedOwner.Endpoint;
    }

    private LocalWorkerClient(LocalWorkerExpectedOwner expectedOwner, bool installedUser) : this(expectedOwner)
    {
        if (!installedUser || !expectedOwner.IsInstalledUser)
            throw new ArgumentException("The installed user client requires an exact installed-user Worker owner.");
        this.installedUser = true;
        installedUserEnrollment = InstalledWorkerBindingStore.ReadRegisteredUser()
            ?? throw new UnauthorizedAccessException("The installed desktop Worker enrollment is unavailable.");
    }

    internal bool HasExpectedOwner => expectedOwner is not null;

    private LocalWorkerClient(bool installed)
    {
        if (!installed) throw new ArgumentException("Use the endpoint constructor for a development pipe.", nameof(installed));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The installed Cerebral named-pipe client requires Windows.");
        this.installed = true;
        installedBinding = InstalledBindingCapture.Capture(InstalledWorkerBindingStore.ReadRegistered);
    }

    public static LocalWorkerClient ForInstalledWorker() => new(installed: true);
    public static LocalWorkerClient ForInstalledUserWorker(LocalWorkerExpectedOwner expectedOwner) =>
        new(expectedOwner, installedUser: true);

    public async Task<CommandResult> CommandAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        var message = new LocalWorkerRequest("command", request);
        LocalWorkerProtocol.ValidateRequest(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var pipe = await ConnectAsync(linked.Token).ConfigureAwait(false);
        using var cancellation = linked.Token.Register(static state => Close((NamedPipeClientStream)state!), pipe);
        var dispatched = false;
        try
        {
            // WriteAsync validates synchronously; only a returned write task can have sent bytes.
            var writing = LocalWorkerProtocol.WriteAsync(pipe, message, linked.Token);
            dispatched = true;
            await writing.ConfigureAwait(false);
            return await LocalWorkerProtocol.ReadAsync<CommandResult>(pipe, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (dispatched && IsTransportFailure(error))
        {
            throw new LocalWorkerCommandUncertainException(request.Operation, error);
        }
        catch (Exception error) when (linked.IsCancellationRequested && IsTransportFailure(error))
        {
            throw new OperationCanceledException("The local Cerebral connection was cancelled before command dispatch.", error, linked.Token);
        }
    }

    public async IAsyncEnumerable<ServiceEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var pipe = await ConnectAsync(linked.Token).ConfigureAwait(false);
        using var cancellation = linked.Token.Register(static state => Close((NamedPipeClientStream)state!), pipe);
        CommandResult acknowledgement;
        try
        {
            await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerRequest("events"), linked.Token).ConfigureAwait(false);
            acknowledgement = await LocalWorkerProtocol.ReadAsync<CommandResult>(pipe, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (linked.IsCancellationRequested && IsTransportFailure(error))
        {
            throw new OperationCanceledException("The local Cerebral event subscription was cancelled.", error, linked.Token);
        }
        if (!acknowledgement.Success)
            throw new IOException("The local Worker rejected the event subscription: " + (acknowledgement.Error ?? "No explanation was supplied."));
        while (true)
        {
            ServiceEvent item;
            try { item = await LocalWorkerProtocol.ReadAsync<ServiceEvent>(pipe, linked.Token).ConfigureAwait(false); }
            catch (Exception error) when (linked.IsCancellationRequested && IsTransportFailure(error))
            {
                throw new OperationCanceledException("The local Cerebral event stream was cancelled.", error, linked.Token);
            }
            catch (Exception error) when (IsTransportFailure(error))
            {
                throw new IOException("The local Cerebral event stream disconnected or returned an invalid frame. Refresh its connection; no automatic reconnection was attempted.", error);
            }
            yield return item;
        }
    }

    private NamedPipeClientStream CreatePipe()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Cerebral pipes require Windows.");
        return new(".", endpoint!.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Cerebral pipes require Windows.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        NamedPipeClientStream? pipe = null;
        try
        {
            LocalWorkerEndpoint expected;
            InstalledWorkerBinding? binding = null;
            if (installed)
            {
                binding = installedBinding!.RequireCurrent(InstalledWorkerBindingStore.ReadRegistered);
                expected = binding.PipeEndpoint();
                pipe = await InstalledWorkerWindows.ConnectClientAsync(expected.PipeName, timeout.Token).ConfigureAwait(false);
                var peer = InstalledWorkerWindows.ObserveServerEndpoint(pipe);
                InstalledWorkerWindows.RequirePipeSecurity(peer.Protection, binding.ServiceAccountSid, binding.OwnerSid);
                var service = InstalledWorkerWindows.ReadService();
                InstalledWorkerAdmission.RequirePipeServer(binding, service, peer);
            }
            else
            {
                expected = endpoint!;
                if (installedUser)
                {
                    var enrollment = RequireCurrentInstalledUserEnrollment();
                    InstalledUserWorkerAdmission.RequireEndpoint(enrollment, expected);
                    if (expectedOwner!.AccountSid != enrollment.OwnerSid ||
                        !InstalledWorkerBinding.PathEquals(expectedOwner.Executable, enrollment.WorkerExecutable, false) ||
                        !InstalledWorkerBinding.PathEquals(expectedOwner.DataDirectory, enrollment.DataDirectory, true))
                        throw new UnauthorizedAccessException("The installed user Worker differs from protected enrollment.");
                }
                pipe = CreatePipe();
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (expectedOwner is { } owner)
                {
                    var peer = InstalledWorkerWindows.ObserveServerProcess(pipe);
                    owner.RequireProcess(peer);
                    owner.RequirePeer(peer, LocalWorkerNativeArguments.ReadNativeArgv(owner.ProcessId));
                }
            }
            using var abort = timeout.Token.Register(static state => Close((NamedPipeClientStream)state!), pipe);
            await LocalWorkerProtocol.WriteAsync(pipe, LocalWorkerProtocol.CreateHello(expected), timeout.Token).ConfigureAwait(false);
            var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, timeout.Token).ConfigureAwait(false);
            LocalWorkerProtocol.ValidateHello(hello, expected);
            if (expectedOwner is { } reviewed)
            {
                var peer = InstalledWorkerWindows.ObserveServerProcess(pipe);
                reviewed.RequireProcess(peer);
                reviewed.RequirePeer(peer, LocalWorkerNativeArguments.ReadNativeArgv(reviewed.ProcessId));
            }
            if (binding is not null)
                _ = installedBinding!.RequireCurrent(InstalledWorkerBindingStore.ReadRegistered);
            if (installedUser) _ = RequireCurrentInstalledUserEnrollment();
            timeout.Token.ThrowIfCancellationRequested();
            return pipe;
        }
        catch (Exception error) when (timeout.IsCancellationRequested && IsTransportFailure(error))
        {
            pipe?.Dispose();
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("The local Cerebral connection was cancelled before identity verification completed.", error, cancellationToken);
            throw new TimeoutException("The local Cerebral Worker did not connect and verify its identity within 10 seconds. Check that the expected Worker instance is running; no command was sent.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            pipe?.Dispose();
            throw new IOException(installed
                ? "The installed Worker connection could not verify this client's captured enrollment, service and local owner. " +
                    "Repair the installation in Configuration, then reconnect explicitly after resolving any unconfirmed request. No command was sent."
                : installedUser
                    ? "The installed user Worker connection could not verify protected enrollment and its exact signed-in process. Reconnect after resolving any unconfirmed request. No command was sent."
                : "The local Worker pipe is not accessible as this Windows user. This transport requires the Worker and client to use the same user identity.", error);
        }
        catch { pipe?.Dispose(); throw; }
    }

    private InstalledUserWorkerBinding RequireCurrentInstalledUserEnrollment()
    {
        if (InstalledWorkerBindingStore.ReadRegisteredUser() != installedUserEnrollment)
            throw new UnauthorizedAccessException("The installed desktop Worker enrollment changed after this client was created. No command was sent.");
        return installedUserEnrollment!;
    }

    // One client owns one protected enrollment snapshot. An unavailable capture stays unavailable
    // until an explicitly created replacement client; shell construction must still work offline.
    private sealed class InstalledBindingCapture
    {
        private readonly InstalledWorkerBinding? binding;
        private readonly Exception? failure;

        private InstalledBindingCapture(InstalledWorkerBinding? binding, Exception? failure)
        {
            this.binding = binding;
            this.failure = failure;
        }

        public static InstalledBindingCapture Capture(Func<InstalledWorkerBinding?> read)
        {
            try
            {
                var binding = read();
                binding?.Validate();
                return new(binding, null);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Defer enrollment-read failures to the operation boundary. This never admits
                // a connection or substitutes a later registration for this failed capture.
                return new(null, error);
            }
        }

        public InstalledWorkerBinding RequireCurrent(Func<InstalledWorkerBinding?> read)
        {
            if (binding is null)
                throw new UnauthorizedAccessException("The installed Cerebral owner binding could not be captured. " +
                    "Repair the installation in Configuration, then reconnect with a new client. No command was sent.", failure);
            if (read() != binding)
                throw new UnauthorizedAccessException("The installed Cerebral owner binding changed after this client was created. " +
                    "Reconnect explicitly after resolving any unconfirmed request. No command was sent.");
            return binding;
        }
    }

    private static bool IsTransportFailure(Exception error) =>
        error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static void Close(NamedPipeClientStream pipe)
    {
        try { pipe.Dispose(); }
        catch (IOException) { /* Cancellation still belongs to the operation's linked token. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
