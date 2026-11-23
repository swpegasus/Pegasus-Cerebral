using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed record DesktopControlEndpoint(
    string Product,
    int Protocol,
    string PipeName,
    string InstanceId,
    int ProcessId,
    DateTimeOffset StartedAt,
    string ProcessStartUtcTicks = "",
    int SessionId = -1);

public sealed record DesktopControlRequest(
    string InstanceId,
    string RequestId,
    string Operation,
    JsonElement Arguments);

public sealed record DesktopControlResponse(
    string RequestId,
    string Operation,
    string State,
    JsonElement Data,
    string? Error = null)
{
    public bool Success => State is "completed" or "accepted";
}

internal static class DesktopControlProtocol
{
    public const string Product = "Pegasus Cerebral Desktop Control";
    // Version 2 requires native process creation/session identity. Version 1
    // descriptors cannot be accepted without weakening connected-peer admission.
    public const int Version = SemanticControlProtocol.Version;
    public const string PipePrefix = "Pegasus.Cerebral.Desktop.";
    public const int MaximumArgumentCharacters = 65_536;
    public const int MaximumRequestIdCharacters = 128;
    public const string InputGuidanceLayoutInspect = "input-guidance.layout.inspect";
    public const string ConfigurationOpen = "configuration.open";
    public const string CapabilitiesInspect = "capabilities.inspect";
    public const string FullExitPrepare = "application.exit.prepare";
    public const string FullExitCommit = "application.exit.commit";
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot.inspect", "instance.inspect", CapabilitiesInspect, "window.activate", "window.capture", "composer.set", "composer.drafts.restore", "composer.attach", "conversation.new", "conversation.list", "conversation.open", "conversation.history.back", "conversation.history.forward", "conversation.workflow.select",
        "input-guidance.inspect", "input-guidance.invoke", "input-guidance.setText", InputGuidanceLayoutInspect,
        "native-plugins.inspect", "native-plugins.review",
        "plugin.surface.inspect", "plugin.surface.open", "plugin.surface.controls", "plugin.surface.invoke", "plugin.surface.setText", "input-mode.set",
        "surface.open", "activity.inspect", "activity.detail", "interface.inspect", "interface.correct", "interface.review", "interface.repair", "interface.owner.inspect", "interface.owner.review", "service-package.inspect", "service-package.stage",
        "service-package.activate", "plugin.command", "plugin.action", "message.send", "question.answer", "task.stop", "wait.settled",
        "workflow.open", "workflow.inspect", "workflow.resume", "workflow.reconcile", "workflow.answer", ConfigurationOpen,
        FullExitPrepare, FullExitCommit
    };

    internal static readonly IReadOnlySet<string> SemanticAttachOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot.inspect", "instance.inspect", "window.activate",
        "conversation.new", "conversation.open", "conversation.list", "conversation.history.back", "conversation.history.forward", "conversation.workflow.select", "composer.set", "message.send",
        "activity.inspect", "activity.detail", "workflow.open", "workflow.inspect", "wait.settled", "plugin.surface.inspect", ConfigurationOpen, "plugin.action",
        "interface.owner.inspect", "interface.owner.review"
    };

    // Ordinary Main exposes a deliberately bounded semantic subset. Attached owner review,
    // Configuration launch mapping, arbitrary native controls and administrative operations
    // retain their separate admission routes.
    internal static readonly IReadOnlySet<string> NormalSemanticOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot.inspect", "instance.inspect", CapabilitiesInspect, "window.activate", "window.capture",
        "conversation.new", "conversation.open", "conversation.list", "conversation.history.back", "conversation.history.forward",
        "conversation.workflow.select", "composer.set", "message.send", "activity.inspect", "activity.detail",
        "workflow.open", "workflow.inspect", "workflow.answer", "wait.settled", "plugin.surface.inspect", FullExitPrepare, FullExitCommit
    };

    internal static bool RequiresSemanticSelection(string operation) => operation is
        "window.activate" or "conversation.new" or "conversation.open" or "conversation.history.back" or "conversation.history.forward" or
        "composer.set" or "message.send" or "workflow.open" or "workflow.answer" or "activity.detail" or "conversation.workflow.select" or
        ConfigurationOpen or "plugin.action" or "interface.owner.review";

    internal static object NormalSemanticCommands() => NormalSemanticOperations.Order(StringComparer.Ordinal).Select(operation => new
    {
        operation,
        requiresExpectedSelection = RequiresSemanticSelection(operation),
        arguments = operation switch
        {
            "snapshot.inspect" => new[] { "includeLastResponse?" },
            "conversation.new" => new[] { "projectId?", "profileId?", "profileRevision?", "initialPathGrants?" },
            "conversation.open" => new[] { "id" },
            "conversation.workflow.select" => new[] { "detailedToolFeedback?", "definitionId?", "definitionVersion?" },
            "conversation.list" or "activity.inspect" => new[] { "limit?" },
            "composer.set" => new[] { "text" },
            "message.send" => new[] { "text?", "definitionInput?" },
            "workflow.open" or "workflow.inspect" => new[] { "runId" },
            "workflow.answer" => new[] { "runId", "questionId", "questionRevision", "answers" },
            "activity.detail" => new[] { "id", "expanded" },
            "wait.settled" => new[] { "timeoutSeconds?" },
            FullExitCommit => new[] { "instanceId", "admissionToken" },
            _ => Array.Empty<string>()
        },
        note = operation switch
        {
            "conversation.workflow.select" => "Choose detailedToolFeedback OR an exact definitionId/definitionVersion pair.",
            "message.send" => "Exact workflows require complete definitionInput. Current selection, pending effects and ordinary owner admission still apply.",
            "workflow.answer" => "Only the current conversation's selected parent form for an exact local helper question is admitted; answer by displayed question ID and revision.",
            "window.capture" => "Mounted WPF content only; not native window pixels or independent screen evidence.",
            FullExitPrepare => "Normal Main only. Read the one-use admission reply before sending commit; prepare never exits.",
            FullExitCommit => "Normal Main only. Return the unexpired admission token for this instance. Uses ordinary tray Exit; never retry an unknown dispatch automatically.",
            _ => "Handler validation and current readiness apply; ? denotes an optional argument."
        }
    }).ToArray();

    public static void Validate(DesktopControlEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Product != Product || endpoint.Protocol != Version)
            throw new InvalidDataException("The desktop-control endpoint uses an incompatible product or protocol.");
        ValidatePipeName(endpoint.PipeName);
        ValidateInstance(endpoint.InstanceId);
        if (endpoint.ProcessId <= 0 || endpoint.StartedAt == default || endpoint.SessionId <= 0 ||
            !long.TryParse(endpoint.ProcessStartUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks <= 0 || ticks > DateTime.MaxValue.Ticks || endpoint.PipeName != "Pegasus.Cerebral.Desktop." + endpoint.InstanceId)
            throw new InvalidDataException("The desktop-control endpoint has an invalid process identity.");
    }

    public static void Validate(DesktopControlRequest request, string expectedInstance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInstance(request.InstanceId);
        if (!string.Equals(request.InstanceId, expectedInstance, StringComparison.Ordinal))
            throw new InvalidDataException("The desktop-control generation changed. Read the current endpoint and retry with a new request ID.");
        ValidateRequestId(request.RequestId);
        if (!Operations.Contains(request.Operation))
            throw new ArgumentException("Unknown desktop-control operation.", nameof(request.Operation));
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Desktop-control arguments must be a JSON object.", nameof(request.Arguments));
        if (request.Arguments.GetRawText().Length > MaximumArgumentCharacters)
            throw new ArgumentException("Desktop-control arguments exceed the 65,536-character bound.", nameof(request.Arguments));
    }

    public static void Validate(DesktopControlResponse response, DesktopControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.RequestId != request.RequestId || response.Operation != request.Operation)
            throw new InvalidDataException("The desktop-control response does not match its request.");
        if (response.State is not ("completed" or "accepted" or "rejected" or "uncertain" or "failed" or "conflict"))
            throw new InvalidDataException("The desktop-control response state is invalid.");
        if (response.Data.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("The desktop-control response has no data value.");
    }

    public static string CanonicalInput(DesktopControlRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", request.Operation);
            writer.WritePropertyName("arguments");
            WriteCanonical(writer, request.Arguments);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static DesktopControlResponse Result(DesktopControlRequest request, string state, object? data = null, string? error = null) =>
        new(request.RequestId, request.Operation, state, CerebralJson.Element(data ?? new { }), error);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }

    private static void ValidatePipeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new InvalidDataException("The desktop-control pipe name is invalid.");
    }

    private static void ValidateInstance(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("The desktop-control instance must be a nonempty GUID in N format.");
    }

    private static void ValidateRequestId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumRequestIdCharacters || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("A bounded desktop-control request ID is required.", nameof(value));
    }
}

internal sealed class DesktopControlServer : IAsyncDisposable
{
    private const int MaximumClients = 4;
    private const int MaximumReceipts = 1024;
    private const int MaximumDescriptorBytes = 8192;
    private readonly OrchestratorWindow window;
    private readonly string descriptorPath;
    private readonly bool readOnlyObservation;
    private readonly bool semanticAttach;
    private readonly bool normalSemantic;
    private SemanticControlServer? sharedNormal;
    private bool PrivateAttach => readOnlyObservation || semanticAttach;
    private bool OwnedPublication => PrivateAttach;
    private readonly object observationPublicationGate = new();
    private bool observationStopped;
    private byte[]? ownedDescriptorBytes;
    private FileAttributes? ownedDescriptorAttributes;
    private readonly DesktopControlEndpoint endpoint;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, AcceptedRequest> accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> receiptOrder = new();
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly SemaphoreSlim capacity = new(MaximumClients, MaximumClients);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? listener;
    private long nextClient;
    private int disposed;

    private DesktopControlServer(OrchestratorWindow window, string dataDirectory, bool readOnlyObservation = false)
        : this(window, dataDirectory, readOnlyObservation, semanticAttach: false) { }

    private DesktopControlServer(OrchestratorWindow window, string dataDirectory, bool readOnlyObservation, bool semanticAttach,
        bool normalSemantic = false)
    {
        if ((readOnlyObservation ? 1 : 0) + (semanticAttach ? 1 : 0) + (normalSemantic ? 1 : 0) > 1)
            throw new ArgumentException("Desktop control modes are mutually exclusive.");
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        if (!Path.IsPathFullyQualified(dataDirectory) || dataDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Desktop control requires a local absolute data directory.", nameof(dataDirectory));
        this.readOnlyObservation = readOnlyObservation;
        this.semanticAttach = semanticAttach;
        this.normalSemantic = normalSemantic;
        var desktop = Path.Combine(OwnedPublication
            ? ExistingWorkerDesktopOptions.NormalizeLocalDirectory(dataDirectory) : Path.GetFullPath(dataDirectory), "Desktop");
        if (OwnedPublication) _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(desktop);
        descriptorPath = Path.Combine(desktop, semanticAttach ? "main-semantic-control.json" : readOnlyObservation ? "input-guidance-layout-control.json" : "control.json");
        var instance = Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        endpoint = new(DesktopControlProtocol.Product, DesktopControlProtocol.Version,
            "Pegasus.Cerebral.Desktop." + instance, instance, Environment.ProcessId, DateTimeOffset.UtcNow,
            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), process.SessionId);
        DesktopControlProtocol.Validate(endpoint);
    }

    public string DescriptorPath => descriptorPath;
    public DesktopControlEndpoint Endpoint => endpoint;
    internal Task Completion => sharedNormal?.Completion ?? listener ?? throw new InvalidOperationException("Desktop control has not started.");
    internal bool IsStopping => sharedNormal?.IsStopping ?? (lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0);

    internal static async Task<DesktopControlServer> StartNormalSemanticAsync(OrchestratorWindow window,
        string userDataDirectory, CancellationToken cancellationToken = default,
        Func<string, bool, NamedPipeServerStream>? listenerFactory = null)
    {
        if (window.ExistingWorkerAttachment is not null)
            throw new InvalidOperationException("Ordinary semantic control cannot adopt a private Worker attachment.");
        var server = new DesktopControlServer(window, userDataDirectory, false, false, normalSemantic: true);
        try
        {
            server.sharedNormal = await SemanticControlServer.StartAsync(DesktopControlIdentity.Shared(server.endpoint),
                userDataDirectory, "control.json",
                request => DesktopControlIdentity.Shared(AdmissionRejection(DesktopControlIdentity.ToMain(request),
                    server.endpoint.InstanceId, false, false, normalSemantic: true)),
                async (request, token) => DesktopControlIdentity.Shared(await server.DispatchAsync(DesktopControlIdentity.ToMain(request), token))!,
                cancellationToken, listenerFactory).ConfigureAwait(false);
            return server;
        }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static async Task<DesktopControlServer> StartAsync(OrchestratorWindow window, string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control requires Windows same-user named pipes.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) throw new InvalidOperationException("Desktop control must run in the signed-in user's session.");
        var server = new DesktopControlServer(window, dataDirectory);
        try
        {
            server.listener = server.ListenAsync(server.lifetime.Token);
            await server.ready.Task.WaitAsync(cancellationToken);
            server.WriteDescriptor();
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    internal static async Task<DesktopControlServer> StartReadOnlyObservationAsync(OrchestratorWindow window,
        string desktopDataDirectory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control requires Windows same-user named pipes.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) throw new InvalidOperationException("Desktop control must run in the signed-in user's session.");
        var attachment = window.ExistingWorkerAttachment
            ?? throw new InvalidOperationException("Layout observation requires an explicit existing Worker attachment.");
        // Reuse private-state admission; this mode never publishes the full-control descriptor.
        var options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", attachment.Endpoint.PipeName,
            attachment.Endpoint.InstanceId, attachment.WorkerDataDirectory, desktopDataDirectory, attachment.InstallationId])!;
        var server = new DesktopControlServer(window, options.DesktopDataDirectory, readOnlyObservation: true);
        try
        {
            server.listener = Task.Run(() => server.ListenAsync(server.lifetime.Token));
            await server.ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (server.listener.IsCompleted)
            {
                await server.listener.ConfigureAwait(false);
                throw new IOException("The requested layout-observation listener stopped during startup.");
            }
            server.WriteDescriptor();
            cancellationToken.ThrowIfCancellationRequested();
            if (server.listener.IsCompleted)
            {
                await server.listener.ConfigureAwait(false);
                throw new IOException("The requested layout-observation listener stopped during descriptor publication.");
            }
            return server;
        }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task<DesktopControlServer> StartSemanticAttachAsync(OrchestratorWindow window,
        string desktopDataDirectory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control requires Windows same-user named pipes.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) throw new InvalidOperationException("Desktop control must run in the signed-in user's session.");
        var attachment = window.ExistingWorkerAttachment
            ?? throw new InvalidOperationException("Semantic control requires an explicit existing Worker attachment.");
        var options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", attachment.Endpoint.PipeName,
            attachment.Endpoint.InstanceId, attachment.WorkerDataDirectory, desktopDataDirectory, attachment.InstallationId])!;
        var server = new DesktopControlServer(window, options.DesktopDataDirectory, readOnlyObservation: false, semanticAttach: true);
        try
        {
            server.listener = Task.Run(() => server.ListenAsync(server.lifetime.Token));
            await server.ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (server.listener.IsCompleted)
            {
                await server.listener.ConfigureAwait(false);
                throw new IOException("The requested semantic-control listener stopped during startup.");
            }
            server.WriteDescriptor();
            cancellationToken.ThrowIfCancellationRequested();
            if (server.listener.IsCompleted)
            {
                await server.listener.ConfigureAwait(false);
                throw new IOException("The requested semantic-control listener stopped during descriptor publication.");
            }
            return server;
        }
        catch (Exception primary)
        {
            try { await server.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { AttachExistingStartup.PreserveSemanticCleanupFailure(primary, "startup-dispose", cleanup); }
            throw;
        }
    }

    internal static void ValidateAdmission(DesktopControlRequest request, string expectedInstance, bool readOnlyObservation,
        bool semanticAttach = false)
    {
        if (AdmissionRejection(request, expectedInstance, readOnlyObservation, semanticAttach) is { } rejection)
            throw new ArgumentException(rejection.Error, nameof(request.Operation));
    }

    private static IEnumerable<string> AdmittedOperations(bool readOnlyObservation, bool semanticAttach, bool normalSemantic = false)
    {
        if ((readOnlyObservation ? 1 : 0) + (semanticAttach ? 1 : 0) + (normalSemantic ? 1 : 0) > 1)
            throw new ArgumentException("Desktop control modes are mutually exclusive.");
        return normalSemantic ? DesktopControlProtocol.NormalSemanticOperations :
            semanticAttach ? DesktopControlProtocol.SemanticAttachOperations :
            readOnlyObservation ? [DesktopControlProtocol.InputGuidanceLayoutInspect] :
            DesktopControlProtocol.Operations.Where(operation => operation is not (
                DesktopControlProtocol.ConfigurationOpen or "conversation.history.back" or "conversation.history.forward" or
                "interface.owner.inspect" or "interface.owner.review" or DesktopControlProtocol.FullExitPrepare or DesktopControlProtocol.FullExitCommit));
    }

    internal static JsonElement ModeCapabilities(bool readOnlyObservation, bool semanticAttach, bool normalSemantic = false)
    {
        var operations = AdmittedOperations(readOnlyObservation, semanticAttach, normalSemantic).Order(StringComparer.Ordinal).ToArray();
        return CerebralJson.Element(new
        {
            schema = "pegasus.desktop-control-capabilities/1",
            protocol = DesktopControlProtocol.Version,
            mode = normalSemantic ? "normal-semantic" : semanticAttach ? "semantic-attach" : readOnlyObservation ? "layout-observation" : "full",
            scope = "mode-admission",
            admittedOperations = operations,
            coverage = normalSemantic ? "bounded-main-subset" : "mode-specific",
            commands = normalSemantic ? DesktopControlProtocol.NormalSemanticCommands() : null,
            expectedSelectionFields = normalSemantic ? new[] { "conversationId", "projectId", "selectionVersion", "connectionVersion", "connectionIdentity" } : null,
            unsupportedOperations = normalSemantic ? DesktopControlProtocol.Operations.Except(operations).Order(StringComparer.Ordinal).ToArray() : null,
            pending = normalSemantic ? new[] { "Configuration domain actions", "AutoIt/native capture and independent screenshot alignment" } : null,
            receipts = new { scope = "endpoint-generation-memory", completedRetentionTarget = MaximumReceipts, maximumInFlight = MaximumClients,
                durable = false, retry = "Reuse identical request IDs only while retained; inspect uncertain effects before another action." },
            note = "Admission is not readiness or authorization. Individual handlers validate arguments, current selection and available surfaces."
        });
    }

    internal static DesktopControlResponse? AdmissionRejection(DesktopControlRequest request, string expectedInstance,
        bool readOnlyObservation, bool semanticAttach, bool normalSemantic = false)
    {
        // Malformed requests and stale identities retain the existing fail-closed path.
        // Only a valid, known operation disabled by this mode receives this rejection.
        DesktopControlProtocol.Validate(request, expectedInstance);
        if (AdmittedOperations(readOnlyObservation, semanticAttach, normalSemantic).Contains(request.Operation, StringComparer.Ordinal)) return null;
        return DesktopControlProtocol.Result(request, "rejected", new
        {
            code = "operation_not_supported_in_mode",
            desktopControl = ModeCapabilities(readOnlyObservation, semanticAttach, normalSemantic)
        }, "This desktop-control mode does not admit the requested operation. See desktopControl.admittedOperations.");
    }

    private DesktopControlResponse WithModeCapabilities(DesktopControlResponse response)
    {
        if (response.Operation is not ("instance.inspect" or DesktopControlProtocol.CapabilitiesInspect) || !response.Success) return response;
        if (response.Data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Instance inspection must return an object.");
        var data = response.Data.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Clone(), StringComparer.Ordinal);
        if (!data.TryAdd("desktopControl", ModeCapabilities(readOnlyObservation, semanticAttach, normalSemantic)))
            throw new InvalidDataException("Instance inspection already contains server-owned control capabilities.");
        data.Add("endpoint", CerebralJson.Element(endpoint));
        return response with { Data = CerebralJson.Element(data) };
    }

    private async Task ListenAsync(CancellationToken token)
    {
        NamedPipeServerStream? listenerPipe = null;
        try
        {
            listenerPipe = CreateListener(first: true); ready.TrySetResult();
            while (!token.IsCancellationRequested)
            {
                await capacity.WaitAsync(token);
                try
                {
                    await listenerPipe.WaitForConnectionAsync(token);
                    var connected = listenerPipe;
                    listenerPipe = CreateListener(first: false);
                    var id = Interlocked.Increment(ref nextClient);
                    var task = ServeAndReleaseAsync(connected, token);
                    clients[id] = task; _ = RetireAsync(id, task);
                }
                catch { capacity.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ready.TrySetException(error);
            if (OwnedPublication) throw;
        }
        finally
        {
            listenerPipe?.Dispose();
            if (OwnedPublication)
            {
                lock (observationPublicationGate)
                {
                    observationStopped = true;
                    RemoveOwnedObservationDescriptor();
                }
            }
            ready.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
        }
    }

    private NamedPipeServerStream CreateListener(bool first) => new(endpoint.PipeName, PipeDirection.InOut,
        MaximumClients + 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        16 * 1024, 16 * 1024);

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

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        using (var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            requestLifetime.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var request = await LocalWorkerProtocol.ReadAsync<DesktopControlRequest>(pipe, requestLifetime.Token);
                DesktopControlProtocol.Validate(request, endpoint.InstanceId);
                var response = await ExecuteOnceAsync(request, requestLifetime.Token);
                DesktopControlProtocol.Validate(response, request);
                await LocalWorkerProtocol.WriteAsync(pipe, response, requestLifetime.Token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // Invalid or disconnected same-user clients do not affect the desktop listener.
            }
        }
    }

    internal static bool NormalCallerMatches(InstalledWorkerClientIdentity caller, string? ownerSid, int ownerSession) =>
        SemanticControlServer.CallerMatches(caller, ownerSid, ownerSession);

    private Task<DesktopControlResponse> ExecuteOnceAsync(DesktopControlRequest request, CancellationToken token)
    {
        if (AdmissionRejection(request, endpoint.InstanceId, readOnlyObservation, semanticAttach, normalSemantic) is { } rejection)
            return Task.FromResult(rejection);
        var canonical = DesktopControlProtocol.CanonicalInput(request);
        var candidate = new AcceptedRequest(canonical, new Lazy<Task<DesktopControlResponse>>(
            () => DispatchAsync(request, token), LazyThreadSafetyMode.ExecutionAndPublication));
        var chosen = accepted.GetOrAdd(request.RequestId, candidate);
        if (!string.Equals(chosen.Canonical, canonical, StringComparison.Ordinal))
            return Task.FromResult(DesktopControlProtocol.Result(request, "conflict", error:
                "This request ID was already accepted with different input."));
        if (ReferenceEquals(chosen, candidate))
        {
            receiptOrder.Enqueue(request.RequestId);
            // Active effects are never evicted by inspection traffic. Retention remains
            // bounded by completed receipts plus the four admitted in-flight clients.
            for (var remaining = receiptOrder.Count; accepted.Count > MaximumReceipts && remaining-- > 0 &&
                receiptOrder.TryDequeue(out var expired);)
            {
                if (!accepted.TryGetValue(expired, out var receipt)) continue;
                if (receipt.Response.IsValueCreated && receipt.Response.Value.IsCompleted)
                    ((ICollection<KeyValuePair<string, AcceptedRequest>>)accepted).Remove(new(expired, receipt));
                else receiptOrder.Enqueue(expired);
            }
        }
        return chosen.Response.Value;
    }

    private async Task<DesktopControlResponse> DispatchAsync(DesktopControlRequest request, CancellationToken token)
    {
        try
        {
            if (request.Operation is DesktopControlProtocol.FullExitPrepare or DesktopControlProtocol.FullExitCommit)
            {
                if (!normalSemantic)
                    return DesktopControlProtocol.Result(request, "rejected", error: "Command-line Exit is supported only by ordinary Main's semantic endpoint.");
                return await window.Dispatcher.InvokeAsync(() => exitAdmission.Handle(request, endpoint.InstanceId,
                    window.SemanticFullExitAvailable, window.RequestSemanticFullExit),
                    System.Windows.Threading.DispatcherPriority.Normal, token);
            }
            var operation = semanticAttach || normalSemantic
                ? window.Dispatcher.InvokeAsync(() => normalSemantic ? window.HandleNormalSemanticControlAsync(request, token) :
                    window.HandleSemanticAttachControlAsync(request, token),
                    System.Windows.Threading.DispatcherPriority.Normal, token)
                : readOnlyObservation
                ? window.Dispatcher.InvokeAsync(() => window.HandleDesktopControlAsync(request, token),
                    System.Windows.Threading.DispatcherPriority.Normal, token)
                : window.Dispatcher.InvokeAsync(() => window.HandleDesktopControlAsync(request, token));
            return WithModeCapabilities(await operation.Task.Unwrap());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or OperationCanceledException)
        {
            return DesktopControlProtocol.Result(request,
                error is OperationCanceledException ? "uncertain" : "rejected", error: error.Message);
        }
        catch (Exception)
        {
            return DesktopControlProtocol.Result(request, "failed", error:
                "The desktop-control operation failed. Inspect the current desktop snapshot before retrying.");
        }
    }

    private readonly MainExitAdmission exitAdmission = new();

    private void WriteDescriptor()
    {
        if (OwnedPublication)
        {
            // Publication and listener termination share this gate, so a stopped listener
            // cannot leave a newly published descriptor behind.
            lock (observationPublicationGate)
            {
                if (observationStopped || lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
                    throw new IOException(semanticAttach || normalSemantic ? "The semantic-control listener has stopped; its descriptor cannot be published." : "The layout-observation listener has stopped; its descriptor cannot be published.");
                WriteObservationDescriptor();
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
        var temporary = descriptorPath + "." + endpoint.InstanceId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(endpoint, CerebralJson.Options), new UTF8Encoding(false));
        File.Move(temporary, descriptorPath, true);
    }

    private void RemoveOwnedDescriptor()
    {
        if (OwnedPublication)
        {
            lock (observationPublicationGate) RemoveOwnedObservationDescriptor();
            return;
        }
        try
        {
            if (!File.Exists(descriptorPath)) return;
            var current = JsonSerializer.Deserialize<DesktopControlEndpoint>(File.ReadAllText(descriptorPath), CerebralJson.Options);
            if (current?.InstanceId == endpoint.InstanceId) File.Delete(descriptorPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void WriteObservationDescriptor()
    {
        var directory = Path.GetDirectoryName(descriptorPath)!;
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
        Directory.CreateDirectory(directory);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(endpoint, CerebralJson.Options);
        if (bytes.Length > MaximumDescriptorBytes) throw new InvalidDataException(semanticAttach ? "The semantic-control descriptor exceeds its bound." : "The layout-observation descriptor exceeds its bound.");
        var temporary = descriptorPath + "." + endpoint.InstanceId + ".tmp";
        var ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
            var attributes = File.GetAttributes(temporary);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(semanticAttach ? "Semantic-control descriptors cannot be links." : "Layout-observation descriptors cannot be links.");
            // A foreign or stale descriptor is a collision; it is never replaced or recovered here.
            File.Move(temporary, descriptorPath, overwrite: false);
            ownsTemporary = false;
            ownedDescriptorBytes = bytes;
            ownedDescriptorAttributes = attributes;
        }
        finally
        {
            if (ownsTemporary)
            {
                try
                {
                    _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
                    if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
    }



    private void RemoveOwnedObservationDescriptor()
    {
        if (ownedDescriptorBytes is not { } expected || ownedDescriptorAttributes is not { } attributes) return;
        try
        {
            var directory = Path.GetDirectoryName(descriptorPath)!;
            _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
            if (File.GetAttributes(descriptorPath) != attributes || (attributes & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(descriptorPath).Length != expected.Length || expected.Length > MaximumDescriptorBytes) return;
            var actual = new byte[expected.Length];
            using (var input = new FileStream(descriptorPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                input.ReadExactly(actual);
                if (input.ReadByte() != -1 || !actual.AsSpan().SequenceEqual(expected)) return;
            }
            _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory);
            if (File.GetAttributes(descriptorPath) != attributes) return;
            File.Delete(descriptorPath);
            ownedDescriptorBytes = null;
            ownedDescriptorAttributes = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (normalSemantic)
        {
            if (sharedNormal is not null) await sharedNormal.DisposeAsync();
            capacity.Dispose(); lifetime.Dispose();
            return;
        }
        await lifetime.CancelAsync(); RemoveOwnedDescriptor();
        if (listener is not null)
        {
            try { await listener.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            catch (Exception) when (OwnedPublication) { /* Completion is reported by the startup owner. */ }
        }
        try { await Task.WhenAll(clients.Values).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        capacity.Dispose(); lifetime.Dispose();
    }

    private sealed record AcceptedRequest(string Canonical, Lazy<Task<DesktopControlResponse>> Response);
}

internal static class DesktopControlClient
{
    public static async Task<DesktopControlResponse> ExecuteAsync(string descriptorPath, string operation,
        string requestId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        if (!DesktopControlProtocol.Operations.Contains(operation)) throw new ArgumentException("Unknown desktop-control operation.");
        var response = await SemanticControlClient.ExecuteAsync(descriptorPath, DesktopControlProtocol.Product,
            DesktopControlProtocol.PipePrefix, typeof(Program).Assembly.Location, operation, requestId, argumentsJson, cancellationToken);
        return DesktopControlIdentity.ToMain(response);
    }
}
