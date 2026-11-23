using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal sealed class ConfigurationReadbackServer : IAsyncDisposable
{
    internal const string DescriptorName = "configuration-readback.json";
    private readonly ReceiverDescriptor descriptor;
    private readonly Func<ReceiverExpected, ReceiverSnapshot> capture;
    private readonly Func<ReceiverExpected, ReceiverPageSnapshot> inspectPage;
    private readonly Func<ReceiverExpected, ReceiverPluginOwnerSnapshot>? capturePluginOwner;
    private readonly Func<ReceiverExpected, string, ReceiverPluginRuntimeSnapshot>? capturePluginRuntime;
    private readonly Func<ReceiverExpected, string, ReceiverPageCommand, ReceiverPageSnapshot> openPage;
    private readonly Dispatcher dispatcher;
    private readonly Action unavailable;
    private readonly CancellationTokenSource lifetime;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object pipeGate = new();
    private NamedPipeServerStream? activePipe;
    private FileStream? publication;
    private Task? listener;
    private int disposed;

    internal ReceiverDescriptor Descriptor => descriptor;
    internal string DescriptorPath { get; }

    private ConfigurationReadbackServer(ExistingWorkerDesktopOptions options, Func<ReceiverExpected, ReceiverSnapshot> capture,
        Func<ReceiverExpected, ReceiverPageSnapshot> inspectPage,
        Func<ReceiverExpected, string, ReceiverPageCommand, ReceiverPageSnapshot> openPage,
        Dispatcher dispatcher, CancellationToken token, Action unavailable,
        Func<ReceiverExpected, ReceiverPluginOwnerSnapshot>? capturePluginOwner,
        Func<ReceiverExpected, string, ReceiverPluginRuntimeSnapshot>? capturePluginRuntime)
    {
        this.capturePluginRuntime = capturePluginRuntime;
        this.capturePluginOwner = capturePluginOwner;
        this.capture = capture; this.inspectPage = inspectPage; this.openPage = openPage;
        this.dispatcher = dispatcher; this.unavailable = unavailable;
        using var process = Process.GetCurrentProcess();
        var attachment = new ReceiverAttachment(options.Endpoint.PipeName, options.Endpoint.InstanceId,
            options.WorkerDataDirectory, options.InstallationId);
        var expected = new ReceiverExpected(process.Id, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            attachment, options.DesktopDataDirectory);
        var generation = Guid.NewGuid().ToString("N");
        descriptor = new(ConfigurationReadbackProtocol.Product, ConfigurationReadbackProtocol.Version,
            "Pegasus.Cerebral.Configuration.Readback." + generation, generation, expected);
        DescriptorPath = Path.Combine(options.DesktopDataDirectory, "Desktop", DescriptorName);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    internal static ConfigurationReadbackServer? TryStart(ExistingWorkerDesktopOptions options,
        Func<ReceiverExpected, ReceiverSnapshot> capture,
        Func<ReceiverExpected, ReceiverPageSnapshot> inspectPage,
        Func<ReceiverExpected, string, ReceiverPageCommand, ReceiverPageSnapshot> openPage,
        Dispatcher dispatcher, CancellationToken token, Action unavailable,
        Func<ReceiverExpected, ReceiverPluginOwnerSnapshot>? capturePluginOwner = null,
        Func<ReceiverExpected, string, ReceiverPluginRuntimeSnapshot>? capturePluginRuntime = null)
    {
        ConfigurationReadbackServer? server = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            token.ThrowIfCancellationRequested();
            server = new(options, capture, inspectPage, openPage, dispatcher, token, unavailable, capturePluginOwner, capturePluginRuntime);
            server.listener = Task.Run(server.ListenAsync);
            server.ready.Task.WaitAsync(TimeSpan.FromSeconds(2), token).GetAwaiter().GetResult();
            server.Publish();
            return server;
        }
        catch (Exception)
        {
            if (server is not null)
            {
                try { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception) { }
            }
            Report(unavailable);
            return null;
        }
    }

    private void Publish()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(DescriptorPath)!;
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(parent);
        Directory.CreateDirectory(parent);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, CerebralJson.Options);
        if (bytes.Length > 16384) throw new InvalidDataException("Observation descriptor exceeds its bound.");
        lock (pipeGate)
        {
            if (lifetime.IsCancellationRequested || listener!.IsCompleted || activePipe is null || disposed != 0)
                throw new IOException("Observation listener is not available.");
            // CreateNew never replaces another owner. The retained lease denies writes/deletes by others;
            // DeleteOnClose removes this exact file object, avoiding a compare-then-delete path race.
            publication = new FileStream(DescriptorPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.DeleteOnClose);
            publication.Write(bytes); publication.Flush(true);
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(descriptor.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                lock (pipeGate)
                {
                    if (lifetime.IsCancellationRequested) return;
                    activePipe = pipe;
                }
                ready.TrySetResult();
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    using var peer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    peer.CancelAfter(TimeSpan.FromSeconds(3));
                    // Core owns strict UTF-8, duplicate-property rejection and the fixed4MiB allocation ceiling.
                    // The smaller closed-schema size check is application admission AFTER that bounded read.
                    var request = await LocalWorkerProtocol.ReadAsync<ReceiverRequest>(pipe, peer.Token).ConfigureAwait(false);
                    var response = await ConfigurationReadbackProtocol.DispatchAsync(request, descriptor,
                        token => dispatcher.InvokeAsync(() => capture(descriptor.Expected),
                            DispatcherPriority.Background, token).Task,
                        token => dispatcher.InvokeAsync(() => inspectPage(descriptor.Expected),
                            DispatcherPriority.Background, token).Task,
                        (operation, page, token) => dispatcher.InvokeAsync(() => openPage(descriptor.Expected, operation, page),
                            DispatcherPriority.Normal, token).Task,
                        capturePluginOwner is null ? null : token => dispatcher.InvokeAsync(
                            () => capturePluginOwner(descriptor.Expected), DispatcherPriority.Background, token).Task,
                        capturePluginRuntime is null ? null : (pluginId, token) => dispatcher.InvokeAsync(
                            () => capturePluginRuntime(descriptor.Expected, pluginId), DispatcherPriority.Background, token).Task,
                        peer.Token).ConfigureAwait(false);
                    await LocalWorkerProtocol.WriteAsync(pipe, response, peer.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or
                    JsonException or ArgumentException or ObjectDisposedException)
                {
                    // One bad/slow peer closes only its connection. There is no operation replay or Worker dispatch.
                }
                finally { lock (pipeGate) { if (ReferenceEquals(activePipe, pipe)) activePipe = null; } }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            ready.TrySetException(new IOException("Observation listener unavailable."));
            Report(unavailable);
        }
        finally
        {
            lock (pipeGate)
            {
                activePipe = null;
                ClosePublication();
            }
        }
    }

    private static void Report(Action report)
    {
        try { report(); }
        catch (Exception) { /* Observation diagnostics must not break ordinary Configuration. */ }
    }

    private void ClosePublication()
    {
        var owned = publication; publication = null;
        try { owned?.Dispose(); }
        catch (Exception) { Report(unavailable); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            lock (pipeGate)
            {
                try { activePipe?.Dispose(); }
                catch (Exception) { Report(unavailable); }
            }
            if (listener is not null) await listener.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) { Report(unavailable); }
        finally
        {
            lock (pipeGate) { ClosePublication(); }
            lifetime.Dispose();
        }
    }
}
