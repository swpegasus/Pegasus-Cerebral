using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker;

/// <summary>
/// Starts only the existing authenticated user pipe until one exact owner resume requests
/// reviewed composition. It neither constructs a StateStore nor normalizes configuration.
/// </summary>
internal sealed class SourceRecoveryStartup : ICommandDispatcher, IOwnerModelSnapshotDispatcher,
    IOwnerWorkflowSubmissionDispatcher, IAsyncDisposable
{
    private readonly SourceRecoveryPreflightResult preflight;
    private readonly LocalRunControl localRun;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource<CommandResult> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim inspection = new(1, 1);
    private LocalWorkerPipeServer? pipe;
    private CommandDispatcher? normal;
    private EngineController? engine;
    private bool disposed;
    private readonly object runtimeGate = new();
    private Task? stopAfterReply;

    internal SourceRecoveryStartup(SourceRecoveryManifest manifest, SourceRecoveryPreflightResult preflight,
        LocalRunControl localRun)
    {
        Manifest = manifest;
        this.preflight = preflight;
        this.localRun = localRun;
    }

    internal SourceRecoveryManifest Manifest { get; }
    internal SourceRecoveryFence Fence { get; } = new();
    internal CancellationToken Shutdown => shutdown.Token;
    internal bool RuntimeAdmissionOpen => !Volatile.Read(ref disposed) && !shutdown.IsCancellationRequested && Fence.IsOpen;

    internal async Task StartPipeAsync(LocalWorkerEndpoint endpoint, EventHub events, CancellationToken token)
    {
        if (pipe is not null) throw new InvalidOperationException("source_recovery_pipe_already_started");
        if (endpoint.PipeName != Manifest.PipeName || endpoint.InstanceId != Manifest.PipeInstanceId)
            throw new InvalidOperationException("source_recovery_pipe_identity_changed");
        Manifest.RequireUnchanged();
        pipe = new(endpoint, this, events);
        await pipe.StartAsync(token).ConfigureAwait(false);
    }

    internal Task<SourceRecoveryResumeTicket> WaitForOwnerResumeAsync(CancellationToken token) =>
        Fence.WaitForRequestAsync(token);

    internal HostSettings? ReadDetachedSettingsWhileClosed() => Fence.IsOpen ? null :
        CerebralJson.Read<HostSettings>(CerebralJson.Element(preflight.Settings));

    internal void AttachRuntime(CommandDispatcher dispatcher, EngineController controller)
    {
        lock (runtimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Fence.State != "validating" || shutdown.IsCancellationRequested)
                throw new InvalidOperationException("source_recovery_runtime_attachment_invalid");
            if (normal is not null || engine is not null) throw new InvalidOperationException("source_recovery_runtime_already_attached");
            Volatile.Write(ref engine, controller);
            Volatile.Write(ref normal, dispatcher);
        }
    }

    internal void BeginValidation(SourceRecoveryResumeTicket ticket) => Fence.BeginValidation(ticket);

    internal void CompleteResume(SourceRecoveryResumeTicket ticket, SourceRecoveryReleaseEvidence evidence, object result,
        CancellationToken validationToken)
    {
        lock (runtimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (normal is null || engine is null || shutdown.IsCancellationRequested)
            {
                Fence.Fail(ticket, "source_recovery_validation_failed");
                throw new InvalidOperationException("source_recovery_runtime_unconfirmed");
            }
            validationToken.ThrowIfCancellationRequested();
            Fence.Release(ticket, evidence, validationToken);
            outcome.TrySetResult(CommandResult.Ok(result));
        }
    }

    internal void FailResume(SourceRecoveryResumeTicket ticket, string code)
    {
        lock (runtimeGate)
        {
            if (Fence.IsOpen) return;
            Fence.Fail(ticket, code);
            outcome.TrySetResult(CommandResult.Fail("source_recovery_resume_unconfirmed"));
        }
    }

    internal void RequestShutdown()
    {
        try { if (!shutdown.IsCancellationRequested) shutdown.Cancel(); }
        catch (AggregateException) { /* Shutdown remains requested even if an observer failed. */ }
        catch (ObjectDisposedException) { /* A late console/host callback cannot revive this disposed fence. */ }
    }

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) || shutdown.IsCancellationRequested)
            return CommandResult.Fail("source_recovery_stopped");
        if (Fence.IsOpen && normal is { } dispatcher)
            return await dispatcher.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.ProfileId is not null) return CommandResult.Fail("source_recovery_owner_required");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireArguments(request.Arguments, request.Operation == "engine.work-hold.resume");
            if (request.Operation == "engine.work-hold.resume")
            {
                await inspection.WaitAsync(cancellationToken).ConfigureAwait(false);
                SourceRecoveryResumeTicket ticket;
                try
                {
                    if (Manifest.PassiveInitializerReview.State != "reviewed")
                        return CommandResult.Fail("source_recovery_initializer_review_required");
                    SourceRecoveryPreflight.VerifyPinnedFiles(Manifest, cancellationToken);
                    _ = SourceRecoveryPreflight.VerifyPersistedState(Manifest, SourceRecoveryExpectedState.Held, cancellationToken);
                    _ = SourceRecoveryPreflight.VerifyRows(Manifest, cancellationToken);
                    ticket = Fence.AcceptResume(cancellationToken);
                }
                finally { inspection.Release(); }
                try { return await outcome.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Fence.Fail(ticket, "source_recovery_resume_cancelled");
                    return CommandResult.Fail("source_recovery_resume_unconfirmed");
                }
            }
            if (request.Operation == "health")
                return CommandResult.Ok(new HostInfo("Pegasus Cerebral",
                    typeof(SourceRecoveryStartup).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", 1,
                    preflight.Settings.Name, RuntimeInformation.ProcessArchitecture.ToString(),
                    RuntimeInformation.OSDescription, preflight.Settings.Coordinator,
                    Manifest.SourceRoot, true, Manifest.InstallationId));
            if (request.Operation == "source.recovery.status")
                return CommandResult.Ok(new
                {
                    schema = "cerebral.source-recovery-status/1",
                    Manifest.ManifestSha256, installationId = Manifest.InstallationId,
                    phase = Fence.State, admissionOpen = Fence.IsOpen, failure = Fence.Failure,
                    runtimeValidation = engine is null ? "deferred" : "initializing-or-observed; inspect engine.status",
                    originalPriorEnginePaused = false, coordinates = preflight.Coordinates,
                    baselineMatched = preflight.Fingerprints.BaselineMatched,
                    localModelAdmission = "fenced; existing durable local resource pause required before composition",
                    scope = "process-owned source recovery; initial coordinates are a verified startup sample"
                });
            if (request.Operation == "engine.status")
                return engine is { } controller ? CommandResult.Ok(controller.Status()) : CommandResult.Ok(new
                {
                    paused = true, epoch = 5, evaluations = 0, committed = 0, pending = false,
                    active = (object?)null, knownGoodSlot = "A", error = (string?)null,
                    slots = Array.Empty<object>(), unreleasedContexts = 0, recovery = "source-recovery-runtime-validation-deferred",
                    validation = "deferred; saved pause is verified, active engine health is unobserved"
                });
            if (request.Operation == "engine.work-hold.status")
            {
                if (normal is { } attached) return await attached.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                _ = SourceRecoveryPreflight.VerifyPersistedState(Manifest, SourceRecoveryExpectedState.Held, cancellationToken);
                // This process has started no scheduler or feature evaluation. The detail retains
                // the distinction between the verified saved barrier and deferred runtime health.
                return CommandResult.Ok(new IntentionalWorkHoldStatus("cerebral.owner-work-hold/1",
                    SourceRecoveryManifest.HoldId, SourceRecoveryManifest.RequestId, 1, 2, "finish-current",
                    "held", 5, true, false, [], null,
                    "Verified persisted empty hold; process admission is fenced. Active Engine validation is deferred until the exact reviewed owner resume."));
            }
            if (request.Operation == "service.stop-local")
            {
                lock (runtimeGate) stopAfterReply ??= StopAfterReplyAsync();
                return CommandResult.Ok(new { stopping = true, localRun = true });
            }
            return CommandResult.Fail("source_recovery_held");
        }
        catch (SourceRecoveryException error) { return CommandResult.Fail(error.Code); }
        catch (OperationCanceledException) { return CommandResult.Fail("source_recovery_cancelled"); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return CommandResult.Fail("source_recovery_command_invalid"); }
    }

    private async Task StopAfterReplyAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        if (Volatile.Read(ref disposed)) return;
        RequestShutdown();
        try { localRun.Stop?.Invoke(); }
        catch (Exception) { /* Admission is already closed and shutdown requested; no callback detail is public. */ }
    }

    internal bool AllowsAttachedInspection(CommandRequest request)
    {
        if (Volatile.Read(ref disposed) || shutdown.IsCancellationRequested || request.ProfileId is not null ||
            request.Operation is not ("health" or "engine.status" or "engine.work-hold.status" or "resources.local.status")) return false;
        try { RequireArguments(request.Arguments, resume: false); return true; }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { return false; }
    }

    private static void RequireArguments(JsonElement arguments, bool resume)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 2048)
            throw new ArgumentException("source_recovery_command_invalid");
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new ArgumentException("source_recovery_command_invalid");
        if (!resume)
        {
            if (fields.Length != 0) throw new ArgumentException("source_recovery_command_invalid");
            return;
        }
        if (fields.Length != 4 || fields.Any(field => field.Name is not
            ("holdId" or "expectedEpoch" or "expectedGeneration" or "expectedRevision")) ||
            arguments.GetProperty("holdId").GetString() != SourceRecoveryManifest.HoldId ||
            arguments.GetProperty("expectedEpoch").GetInt64() != 5 ||
            arguments.GetProperty("expectedGeneration").GetInt64() != 1 ||
            arguments.GetProperty("expectedRevision").GetInt64() != 2)
            throw new ArgumentException("source_recovery_command_invalid");
    }

    Task<CommandResult> IOwnerModelSnapshotDispatcher.ExecuteOwnerSnapshotPipeAsync(NamedPipeServerStream stream,
        InstalledWorkerBinding? binding, CommandRequest request, int frameBytes, CancellationToken token) =>
        !Volatile.Read(ref disposed) && !shutdown.IsCancellationRequested && Fence.IsOpen && normal is IOwnerModelSnapshotDispatcher owner
            ? owner.ExecuteOwnerSnapshotPipeAsync(stream, binding, request, frameBytes, token)
            : Task.FromResult(CommandResult.Fail("source_recovery_held"));

    Task<CommandResult> IOwnerWorkflowSubmissionDispatcher.ExecuteOwnerVerificationPipeAsync(
        NamedPipeServerStream stream, InstalledWorkerBinding? binding, LocalUserOwnerBinding? localBinding,
        CommandRequest request, CancellationToken token) =>
        !Volatile.Read(ref disposed) && !shutdown.IsCancellationRequested && Fence.IsOpen &&
        normal is IOwnerWorkflowSubmissionDispatcher owner
            ? owner.ExecuteOwnerVerificationPipeAsync(stream, binding, localBinding, request, token)
            : Task.FromResult(CommandResult.Fail("source_recovery_held"));

    public async ValueTask DisposeAsync()
    {
        Task? stopping;
        lock (runtimeGate)
        {
            if (disposed) return;
            disposed = true;
            stopping = stopAfterReply;
        }
        var cleanupUnconfirmed = false;
        try
        {
            RequestShutdown();
            outcome.TrySetResult(CommandResult.Fail("source_recovery_stopped"));
            if (pipe is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await pipe.StopAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception) { cleanupUnconfirmed = true; }
                finally
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { cleanupUnconfirmed = true; }
                }
            }
            if (stopping is not null) await stopping.ConfigureAwait(false);
        }
        finally
        {
            Fence.Dispose();
            inspection.Dispose();
            shutdown.Dispose();
        }
        // A bounded diagnostic must not replace the original uncertain operation exception.
        if (cleanupUnconfirmed) Console.Error.WriteLine("Pegasus Cerebral: source_recovery_cleanup_unconfirmed");
    }
}
