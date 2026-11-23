using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

internal sealed class ModelResponseLimitException(string message) : IOException(message);

public sealed record ModelMessage(string Role, string Content);

internal readonly record struct ModelLoadResult(ModelRecord Model, bool Started);

public sealed partial class ModelRuntime(StateStore store, Func<HostSettings> settings, EventHub events, TimeProvider? clock = null) : IAsyncDisposable, IModelRuntimeAdapter, IResourcePauseModelControl
{
    internal ModelExecutionAccounting Accounting { get; } = ModelExecutionAccounting.For(store);
    private readonly bool nativeCacheObservationEnabled = NativeCacheDevelopmentSetup.Capture(ModelExecutionAccounting.For(store));
    internal ModelRuntime(StateStore store, Func<HostSettings> settings, EventHub events,
        ModelExecutionAccounting accounting, TimeProvider? clock = null) : this(store, settings, events, BindAccounting(store, accounting, clock)) { }
    // Constructor-only protocol seam: ordinary runtimes retain the owner-accounted PrepareAsync path.
    internal ModelRuntime(StateStore store, Func<HostSettings> settings, EventHub events, TimeProvider? clock,
        Func<ModelGenerationRequest, CancellationToken, Task<IPreparedModelGeneration>> directTextPreparation)
        : this(store, settings, events, clock)
    { this.directTextPreparation = directTextPreparation ?? throw new ArgumentNullException(nameof(directTextPreparation)); }
    private readonly Func<ModelGenerationRequest, CancellationToken, Task<IPreparedModelGeneration>>? directTextPreparation;

    private static TimeProvider? BindAccounting(StateStore store, ModelExecutionAccounting accounting, TimeProvider? clock)
    { ModelExecutionAccounting.Bind(store, accounting); return clock; }

    private const int MaximumExitDiagnosticCharacters = 4096;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private sealed class Instance(ManagedProcess child, HttpClient http, int parallel, ModelCapacityPlan capacityPlan, ResourceInfo memoryBeforeLoad, TimeProvider clock)
    {
        public ManagedProcess Child = child;
        public Process Process => Child.Process;
        public HttpClient Http = http;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public LlamaSlotPool Slots = new(parallel);
        public bool Stopped;
        public string? VisionProjectorPath;
        public NativeImageVerification? Images;
        public NativeExecutableIdentity NativeCacheIdentity = NativeExecutableIdentity.Unobserved("unobserved");
        public int Busy;
        public readonly ModelIdleHold Idle = new(clock);
        public DateTimeOffset LastUsed => Idle.LastUsed;
        public ConcurrentQueue<string> Log = new();
        public ModelCapacityPlan CapacityPlan = capacityPlan;
        public ResourceInfo MemoryBeforeLoad = memoryBeforeLoad;
        public ResourceInfo? MemoryAfterLoad;
        public OwnerRuntimeSuccessfulLaunch? OwnerLaunch;
        public ModelExecutionAccounting.ModelOwnership? Ownership;
        public NativeRegistrationLifetime? RegisteredText;
    }
    private readonly ConcurrentDictionary<string, Instance> instances = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    // Read the durable owner intent during construction so every load path starts closed after a restart.
    private bool resourcePauseRequested = ResourcePausePersistence.Read(store).PauseRequested;

    internal async Task<T> WithNoLoadedModelsAsync<T>(Func<T> action, CancellationToken token)
    {
        // Restore and model loading must share the lifecycle lock. Checking Diagnostics first
        // would allow a load to start with bindings that the restore is about to replace.
        if (!await lifecycle.WaitAsync(0, token))
            throw new InvalidOperationException("Finish model loading or unloading before restoring configuration. No settings were changed.");
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            if (!instances.IsEmpty || Accounting.Snapshot().Entries.Any(value => value.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.FittingProcess))
                throw new InvalidOperationException("Unload models and confirm pending cleanup before restoring configuration. No settings were changed.");
            token.ThrowIfCancellationRequested();
            return action();
        }
        finally { lifecycle.Release(); }
    }

    public async Task<ModelRecord> LoadAsync(string id, CancellationToken token) =>
        (await LoadTrackedAsync(id, token)).Model;

    internal async Task<ModelLoadResult> LoadTrackedAsync(string id, CancellationToken token, bool allowResourceResume = false,
        IReadOnlySet<string>? deferredProfileBudgetIds = null)
    {
        using var ownership = Accounting.Begin(ModelOwnershipKind.LocalLoad, id);
        var gateHeld = false;
        try
        {
            await lifecycle.WaitAsync(token); gateHeld = true;
            return await LoadTrackedUnderLifecycleAsync(id, token, ownership, allowResourceResume,
                deferredProfileBudgetIds).ConfigureAwait(false);
        }
        finally { ownership.Complete(); if (gateHeld) lifecycle.Release(); }
    }

    // Both ordinary loading and full-binding readiness use this one body. The caller holds
    // lifecycle throughout; only readiness supplies the already validated exact model record.
    private async Task<ModelLoadResult> LoadTrackedUnderLifecycleAsync(string id, CancellationToken token,
        ModelExecutionAccounting.ModelOwnership ownership, bool allowResourceResume = false,
        IReadOnlySet<string>? deferredProfileBudgetIds = null, ModelRecord? selectedModel = null)
    {
        var loadAdmitted = false;
        try
        {
            ownership.Phase(ModelOwnershipPhase.Loading);
            ThrowIfOwnerRuntimeDisposed();
            if (resourcePauseRequested && !allowResourceResume)
                throw new InvalidOperationException("Local model admission is paused while this installation reserves its GPU.");
            var previous = Accounting.Snapshot().Entries.FirstOrDefault(value => value.ModelId == id &&
                value.Phase == ModelOwnershipPhase.Unknown && value.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.FittingProcess);
            if (previous is not null)
            {
                NativeReadinessOwnershipScope.BlockedBy(Accounting, previous);
                throw new InvalidOperationException("Previous native model cleanup is unconfirmed. Inspect its accounting before starting another instance.");
            }
            RequireNoRetainedNativeRegistration(id);
            loadAdmitted = true;
            var model = selectedModel ?? store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            if (instances.TryGetValue(id, out var existing) && !existing.Process.HasExited) return new(model, false);
            if (existing is not null) await StopAsync(id, existing);
            if (!File.Exists(model.Path)) throw new FileNotFoundException("Configured model file is unavailable.", model.Path);
            await using (var file = File.OpenRead(model.Path))
            {
                var magic = new byte[4]; if (await file.ReadAsync(magic, token) != 4 || Encoding.ASCII.GetString(magic) != "GGUF") throw new InvalidDataException("The configured file is not a GGUF model.");
            }
            ValidateProjector(model.VisionProjectorPath);
            var configuration = settings();
            if (!File.Exists(configuration.LlamaServerPath)) throw new FileNotFoundException("Set llamaServerPath in Integrator settings to the installed llama-server executable.");
            if (model.ContextTokens < 512 || model.ParallelRequests is < 1 or > 16) throw new ArgumentException("Invalid model context or concurrency settings.");
            var additionalCalibrationMb = 0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var memoryBeforeLoad = await HardwareProbe.ReadAsync(token);
                var effectiveReserveMb = EffectiveReservedMemoryMb(configuration);
                var operationPromisesMb = checked(effectiveReserveMb - configuration.ReservedMemoryMb);
                var capacityPlan = ModelCapacity.Plan(model, memoryBeforeLoad, effectiveReserveMb);
                var fitTargetMb = (long)Math.Max(0, effectiveReserveMb);
                if (model.AutomaticContextSizing)
                {
                    var effectiveCalibrationMb = checked(additionalCalibrationMb + operationPromisesMb);
                    fitTargetMb = LlamaCapacityFitter.FitTargetMemoryMb(configuration, effectiveCalibrationMb);
                    var runtimeFit = await LlamaCapacityFitter.FitAsync(model, configuration, token, effectiveCalibrationMb, Accounting);
                    if (runtimeFit.ContextTokens is { } fittedContext)
                        capacityPlan = capacityPlan with
                        {
                            ContextTokens = fittedContext,
                            Changed = fittedContext != model.ContextTokens,
                            EstimatedPlacementMb = null,
                            EstimatedRemainingMb = null,
                            Basis = runtimeFit.Basis
                        };
                    else capacityPlan = capacityPlan with { Basis = runtimeFit.Basis + " " + capacityPlan.Basis };
                }
                if (capacityPlan.Changed) model = ApplyCapacityPlan(model, capacityPlan.ContextTokens, deferredProfileBudgetIds);
                model.State = "loading"; model.Error = null; Save(model);
                var started = await StartInstanceAsync(model, configuration, capacityPlan, memoryBeforeLoad, fitTargetMb, token, deferredProfileBudgetIds);
                var instance = started.Instance;
                model = started.Model;
                var observed = instance.CapacityPlan.MeasuredAvailableAfterLoadMb;
                if (model.AutomaticContextSizing && effectiveReserveMb > 0 && observed is { } free && free < effectiveReserveMb && attempt == 0)
                {
                    additionalCalibrationMb = (int)Math.Min(1_048_576L, (long)effectiveReserveMb - free + 512);
                    await StopAsync(id, instance);
                    model.State = "unloaded"; model.Error = null; Save(model);
                    continue;
                }
                model.State = "loaded"; Save(model); return new(model, true);
            }
            throw new InvalidOperationException("Automatic model fitting did not produce a load result.");
        }
        catch (Exception ex)
        {
            NativeReadinessOwnershipScope.CaptureLoaderFailure(Accounting, id, ex);
            if (loadAdmitted)
            {
                var failed = store.Get<ModelRecord>("model", id);
                if (failed is not null) { failed.State = "failed"; failed.Error = ex.Message; Save(failed); }
            }
            throw;
        }
    }

    public bool IsLoaded(string id)
    {
        if (!instances.TryGetValue(id, out var instance)) return false;
        try { return !instance.Process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
    // Stage large file transfers before entering this gate. The callback must be a short synchronous
    // metadata/file commit and must not call another runtime lifecycle operation.
    public async Task<T> WithUnloadedModelAsync<T>(string id, Func<ModelRecord, T> mutation, CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            token.ThrowIfCancellationRequested();
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var modelPath = Path.GetFullPath(model.Path);
            var sameFile = store.List<ModelRecord>("model").Where(x => Path.GetFullPath(x.Path).Equals(modelPath, comparison)).ToList();
            var ownership = Accounting.Snapshot().Entries;
            if (sameFile.Any(x => instances.ContainsKey(x.Id) || x.State is "loaded" or "loading" ||
                ownership.Any(value => value.ModelId == x.Id && value.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.FittingProcess)))
                throw new InvalidOperationException("Unload every model registration using this weight file before changing it.");
            return mutation(model);
        }
        finally { lifecycle.Release(); }
    }

    private static void AddLog(Instance instance, string? line)
    {
        if (line is null) return;
        instance.Log.Enqueue(line.Length > 1000 ? line[..1000] : line);
        while (instance.Log.Count > 60) instance.Log.TryDequeue(out _);
    }
    private async Task<(Instance Instance, ModelRecord Model)> StartInstanceAsync(ModelRecord model, HostSettings configuration,
        ModelCapacityPlan capacityPlan, ResourceInfo memoryBeforeLoad, long fitTargetMb, CancellationToken token,
        IReadOnlySet<string>? deferredProfileBudgetIds)
    {
        var executable = configuration.LlamaServerPath;
        var launch = OwnerRuntimeLaunchCapture.Capture(model, executable, configuration.InstallationId);
        var launchModel = launch.CreateLaunchInputs();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        // Context comes from llama-fit-params when available. llama-server still owns exact layer
        // placement and receives the same bounded target and minimum context used by that fit
        // calculation. Without --fit-ctx the server may silently trade nearly all context for
        // GPU placement under transient pressure, leaving the agent unable to process its prompt.
        foreach (var arg in ServerArguments(launchModel, port, fitTargetMb)) start.ArgumentList.Add(arg);
        var ownership = Accounting.Begin(ModelOwnershipKind.LocalContext, model.Id, requiresAdmission: false);
        Instance? instance = null;
        ManagedProcess? child = null;
        NativeImageVerification? images = null;
        int? measuredContextTokens = null;
        try
        {
            if (launchModel.VisionProjectorPath is not null) images = new(launchModel, executable);
            ownership.Phase(ModelOwnershipPhase.Loading, effectStarted: true);
            using var nativeExecutable = nativeCacheObservationEnabled ? NativeExecutableCapture.Open(start.FileName) : null;
            child = ManagedProcess.Start(
                start.FileName,
                start.ArgumentList,
                start.WorkingDirectory,
                LlamaProcessEnvironment.ForExecutable(start.FileName));
            var process = child.Process; process.StandardInput.Close();
            var nativeChild = nativeExecutable is null ? default : NativeExecutableCapture.CaptureOwnedProcess(process);
            instance = new(child, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan }, launchModel.ParallelRequests, capacityPlan, memoryBeforeLoad, time);
            ownership.Bind(instanceId: instance.Id, processId: process.Id);
            instance.Ownership = ownership;
            instance.VisionProjectorPath = launchModel.VisionProjectorPath;
            instance.Images = images;
            InstallOwnerObservedInstance(launchModel.Id, instance);
            var captured = instance;
            process.OutputDataReceived += (_, e) => AddLog(captured, e.Data);
            process.ErrorDataReceived += (_, e) => AddLog(captured, e.Data);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("llama-server exited: " + string.Join('\n', instance.Log.TakeLast(12)));
                try { using var response = await instance.Http.GetAsync("health", timeout.Token); if (response.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(250, timeout.Token);
            }
            if (LlamaCapacityFitter.ParseLoadedContext(instance.Log) is { } loadedContext)
            {
                measuredContextTokens = loadedContext;
                var minimumContext = launch.MinimumContextTokens;
                if (loadedContext < minimumContext)
                    throw new IOException($"llama-server loaded only {loadedContext:N0} context tokens per request, below the required {minimumContext:N0}. Reduce concurrency or allow more CPU/system-memory placement before retrying.");
                if (loadedContext != model.ContextTokens)
                {
                    model = ApplyCapacityPlan(model, loadedContext, deferredProfileBudgetIds);
                    instance.CapacityPlan = instance.CapacityPlan with
                    {
                        ContextTokens = loadedContext,
                        Changed = true,
                        Basis = instance.CapacityPlan.Basis + $" llama-server reported an effective {loadedContext:N0}-token context per slot."
                    };
                }
            }
            instance.MemoryAfterLoad = await HardwareProbe.ReadAsync(token);
            var effectiveReserveMb = EffectiveReservedMemoryMb(configuration);
            var observed = ModelCapacity.Plan(model, instance.MemoryAfterLoad, effectiveReserveMb).AvailableMemoryMb;
            instance.CapacityPlan = instance.CapacityPlan with
            {
                MeasuredAvailableAfterLoadMb = observed,
                ReservedMemorySatisfied = observed is { } free ? free >= effectiveReserveMb : null
            };
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("llama-server exited before startup completed.");
            // A newly spawned process may not expose its main module yet. Keep the hashed file
            // handle through readiness; publish one immutable identity for this exact owned child.
            instance.NativeCacheIdentity = nativeExecutable is null ? NativeExecutableIdentity.Unobserved("disabled") :
                await nativeExecutable.BindReadyAsync(process, nativeChild, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("llama-server exited during identity capture.");
            nativeExecutable?.Dispose();
            PublishOwnerSuccessfulLaunch(instance, launch.SuccessfulStartup(measuredContextTokens));
            ownership.Phase(ModelOwnershipPhase.Ready);
            return (instance, model);
        }
        catch (Exception failure)
        {
            NativeReadinessOwnershipScope.CaptureLoaderFailure(Accounting, model.Id, failure);
            if (instance is not null) await StopAsync(model.Id, instance);
            else
            {
                // A process can fail before Instance takes ownership (for example, closing
                // stdin on an already-exited child). Retire every admitted launch resource.
                var confirmed = child is not null && await child.StopAndConfirmAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                try { child?.Dispose(); images?.Dispose(); }
                finally
                {
                    if (confirmed) ownership.Complete();
                    else ownership.Unknown("native-launch-cleanup-not-confirmed");
                }
            }
            throw;
        }
    }

    internal static IReadOnlyList<string> ServerArguments(ModelRecord model, int port, long fitTargetMb)
    {
        var gpuLayers = model.GpuLayers == -1 ? "auto" : model.GpuLayers.ToString();
        var minimumContext = LlamaCapacityFitter.MinimumContextTokens(model, model.AutomaticContextSizing);
        // A previous runtime may have persisted a context reduced by an older llama.cpp fit. Treat
        // the recommendation as the next automatic request floor as well as the fit floor so a
        // restart can repair that state instead of asking the server to preserve the bad value.
        var requestedContext = model.AutomaticContextSizing
            ? Math.Max(model.ContextTokens, minimumContext)
            : model.ContextTokens;
        var arguments = new List<string>
        {
            "--model", model.Path,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--ctx-size", ((long)requestedContext * model.ParallelRequests).ToString(),
            "--parallel", model.ParallelRequests.ToString(),
            "--n-gpu-layers", gpuLayers,
            "--fit", "on",
            "--fit-target", fitTargetMb.ToString(),
            "--fit-ctx", LlamaCapacityFitter.MinimumTotalContextTokens(model, model.AutomaticContextSizing).ToString(),
            "--no-context-shift"
        };
        if (model.VisionProjectorPath is { } projector) { arguments.Add("--mmproj"); arguments.Add(projector); }
        if (!string.IsNullOrWhiteSpace(model.ChatTemplate))
        {
            var custom = model.ChatTemplate.Contains("{{", StringComparison.Ordinal) || model.ChatTemplate.Contains("{%", StringComparison.Ordinal);
            arguments.Add(custom ? "--jinja" : "--no-jinja");
            arguments.Add("--chat-template"); arguments.Add(model.ChatTemplate);
        }
        return arguments;
    }
    // A default switch commits its affected profiles only after readiness and owner-revision
    // checks. Defer those exact budget writes so runtime fitting cannot alter their preimages.
    // Ordinary loads and profiles outside that switch retain the normal fitting behavior.
    internal ModelRecord ApplyCapacityPlan(ModelRecord model, int contextTokens, IReadOnlySet<string>? deferredProfileBudgetIds = null)
    {
        var profilesChanged = false;
        var updated = store.Locked(() =>
        {
            var current = store.Get<ModelRecord>("model", model.Id) ?? throw new ArgumentException("Model not found.");
            current.ContextTokens = contextTokens;
            var profiles = ModelProfileBudgets.ResolveForRuntime(store, current, contextTokens);
            store.Put("model", current.Id, current);
            foreach (var profile in profiles)
            {
                if (deferredProfileBudgetIds?.Contains(profile.Id) == true) continue;
                store.Put("agent", profile.Id, profile);
                profilesChanged = true;
            }
            return current;
        });
        events.Publish("model.updated", null, updated);
        if (profilesChanged)
            events.Publish("agents.updated", null, new { modelId = updated.Id, budgetsResolved = true, automaticContext = true });
        return updated;
    }
    private void Save(ModelRecord model) { store.Put("model", model.Id, model); events.Publish("model.updated", null, model); }
    public object Diagnostics() => instances.Select(x =>
    {
        var (resolvedLayers, totalLayers) = ParseGpuLayers(x.Value.Log);
        return new
        {
            Id = x.Key,
            ProcessId = x.Value.Process.Id,
            WorkingSetMb = WorkingSetMb(x.Value.Process),
            ResolvedGpuLayers = resolvedLayers,
            TotalGpuLayers = totalLayers,
            Busy = x.Value.Busy,
            AvailableSlots = x.Value.Slots.AvailableCount,
            QuarantinedSlots = x.Value.Slots.Quarantined,
            x.Value.LastUsed,
            x.Value.CapacityPlan,
            x.Value.MemoryBeforeLoad,
            x.Value.MemoryAfterLoad,
            Log = x.Value.Log.TakeLast(12).ToArray()
        };
    }).ToArray();

    private static long? WorkingSetMb(Process process)
    {
        try { process.Refresh(); return process.HasExited ? null : process.WorkingSet64 / 1024 / 1024; }
        catch (InvalidOperationException) { return null; }
    }

    private static (int? Resolved, int? Total) ParseGpuLayers(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var match = Regex.Match(line, @"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var resolved) && int.TryParse(match.Groups[2].Value, out var total))
                return (resolved, total);
        }
        return (null, null);
    }
    public async Task UnloadAsync(string id, CancellationToken token, bool force = false)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            if (instances.TryGetValue(id, out var instance))
            {
                if (!force && instance.Busy > instance.Slots.Quarantined.Count) throw new InvalidOperationException("Model is serving an active request; cancel or wait before unloading.");
                await StopAsync(id, instance);
            }
            var model = store.Get<ModelRecord>("model", id); if (model is not null) { model.State = "unloaded"; model.Error = null; Save(model); }
        }
        finally { lifecycle.Release(); }
    }
    private async Task StopAsync(string id, Instance instance)
    {
        // Do not let cleanup of an observed stale process remove a newer instance installed
        // under the same model id. All normal callers hold lifecycle; the exact remove also
        // protects shutdown and diagnostic callers that enumerate before acquiring it.
        // Close logical admission before removing membership. Never drain callbacks here:
        // a callback may already be waiting for this same lifecycle gate.
        instance.RegisteredText?.CloseAdmission();
        instance.Ownership?.Phase(ModelOwnershipPhase.Stopping);
        RemoveOwnerObservedInstance(id, instance);
        instance.Slots.Complete();
        var confirmed = false;
        var errors = new List<Exception>();
        try { confirmed = await instance.Child.StopAndConfirmAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            instance.Stopped = true;
            try { instance.Images?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { instance.Http.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { instance.Child.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (confirmed && errors.Count == 0)
            {
                Accounting.CompleteStoppedInstance(instance.Id);
                instance.Ownership?.Complete();
            }
            else instance.Ownership?.Unknown("native-owned-job-or-group-cleanup-unconfirmed");
        }
        // Physical child confirmation above remains independent of logical registry retirement.
        // The retirement task owns its exact accounting row and observes failures after the
        // gate is released. A pending callback is retained, never treated as a release.
        var registration = instance.RegisteredText?.BeginRetirement();
        Exception? registrationError = registration is null ? null :
            !registration.IsCompletedSuccessfully ? new IOException("Registered native callbacks are still settling.") :
            registration.Result;
        if (!confirmed || errors.Count != 0)
            throw new IOException("Model cleanup was not confirmed; accounting retains uncertain ownership.",
                registrationError is null ? errors.FirstOrDefault() :
                    new AggregateException(errors.Append(registrationError)));
        if (registrationError is not null)
            throw new IOException("Native stop was confirmed; registered ownership remains unconfirmed.", registrationError);
    }
    public async Task<string> GenerateAsync(string id, IReadOnlyList<ModelMessage> messages, int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token, JsonElement? responseSchema = null, string reasoningMode = "off", int reasoningTokens = 1024)
    {
        ValidateDirectInferenceArguments(messages, maxTokens, temperature, reasoningMode);
        if (reasoningMode == "on")
            return await GenerateReasoningAsync(id, messages, maxTokens, temperature, json, onToken, token, responseSchema, reasoningTokens);
        return await GenerateTextAsync(id, messages, null, maxTokens, temperature, json, onToken, token, responseSchema);
    }

    internal Task<string> GenerateAcceptedTextAsync(string id, IReadOnlyList<ModelMessage> messages, int acceptedContextTokens,
        int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token, JsonElement? responseSchema = null)
    {
        ValidateDirectInferenceArguments(messages, maxTokens, temperature, "off");
        if (acceptedContextTokens < AgentTokenLimits.MinimumContextTokens)
            throw new ArgumentOutOfRangeException(nameof(acceptedContextTokens));
        return GenerateTextAsync(id, messages, acceptedContextTokens, maxTokens, temperature, json, onToken, token, responseSchema);
    }

    private async Task<string> GenerateTextAsync(string id, IReadOnlyList<ModelMessage> messages, int? acceptedContextTokens,
        int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token, JsonElement? responseSchema)
    {
        var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
        // Accepted work may narrow with current capacity, but later model growth cannot enlarge its preparation.
        var context = Math.Min(acceptedContextTokens ?? model.ContextTokens, model.ContextTokens);
        var request = BuildDirectTextRequest(id, messages, context, maxTokens, temperature, json, responseSchema);
        await using var prepared = await (directTextPreparation is null
            ? PrepareAsync(request, token) : directTextPreparation(request, token));
        SkillDispatchFence.RequireCurrent();
        var result = await prepared.GenerateAsync(onToken, token);
        return InterpretDirectTextResult(result, token);
    }

    internal static void ValidateDirectInferenceArguments(IReadOnlyList<ModelMessage> messages,
        int maxTokens, double temperature, string reasoningMode)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages.Count > 10000 || messages.Any(x => x is null || x.Role is not ("system" or "user" or "assistant") || x.Content is null || x.Content.Contains('\0')) ||
            maxTokens is < 1 or > AgentTokenLimits.MaximumResponseTokens || !double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new ArgumentException("Invalid inference messages or generation limits.");
        ValidateReasoningMode(reasoningMode);
    }

    internal static ModelGenerationRequest BuildDirectTextRequest(string id, IReadOnlyList<ModelMessage> messages,
        int contextTokens, int maxTokens, double temperature, bool json, JsonElement? responseSchema)
    {
        ValidateDirectInferenceArguments(messages, maxTokens, temperature, "off");
        var schema = responseSchema?.Clone() ?? (json ? CerebralJson.Element(new { type = "object" }) : (JsonElement?)null);
        var input = messages.Select(message => new ModelInputMessage(message.Role, [new TextContentPart(message.Content)])).ToArray();
        var request = new ModelGenerationRequest(id, input, contextTokens, maxTokens, Temperature: temperature, ResponseSchema: schema);
        request.Validate();
        return request;
    }

    // Pure pre-factory validation/snapshot. The minimum structural context is not an
    // estimated capacity or count; only the later actual slot/template/tokenizer admits input.
    internal static RegisteredNativeRequestSnapshot SnapshotRegisteredNativeRequest(string id,
        IReadOnlyList<ModelMessage> messages, int maxTokens, double temperature, bool json,
        JsonElement? responseSchema = null, string reasoningMode = "off", int reasoningTokens = 1024)
    {
        ValidateDirectInferenceArguments(messages, maxTokens, temperature, reasoningMode);
        if (reasoningMode != "off")
            throw new NotSupportedException("Registered native inference currently supports reasoning-off text only.");
        var structural = BuildDirectTextRequest(id, messages, 512, maxTokens, temperature, json, responseSchema);
        var frozen = structural.Messages.Select(message =>
            new ModelMessage(message.Role, ((TextContentPart)message.Content[0]).Text)).ToArray();
        return new(id, Array.AsReadOnly(frozen), maxTokens, temperature, json,
            structural.ResponseSchema, reasoningMode, reasoningTokens);
    }

    internal static string InterpretDirectTextResult(ModelGenerationResult result, CancellationToken token) =>
        result.Finish switch
        {
            ModelGenerationFinish.Stop => result.Text,
            ModelGenerationFinish.Length => throw new ModelResponseLimitException("Model reached the response token limit. Increase responseTokens; partial output was not adopted."),
            ModelGenerationFinish.Cancelled => throw new OperationCanceledException("Model generation was cancelled and the slot is idle.", token),
            _ => throw new IOException(result.Error ?? "Model did not return a confirmed completion; partial output was not adopted.")
        };
    public async Task<int> CountTokensAsync(string id, IReadOnlyList<ModelMessage> messages, CancellationToken token, string reasoningMode = "off")
    {
        using var ownership = Accounting.Begin(ModelOwnershipKind.LocalMeasurement, id);
        ValidateReasoningMode(reasoningMode);
        Instance? instance = null;
        try { instance = await AcquireAsync(id, token); ownership.Bind(instanceId: instance.Id); }
        catch { ownership.Complete(); throw; }
        ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
        var completed = false;
        try
        {
        var prompt = await ApplyTemplateAsync(instance, messages, token, reasoningMode);
        using var result = await LlamaProtocol.PostAsync(instance.Http, "tokenize", new { content = prompt, add_special = true, parse_special = true }, token);
        var count = result.RootElement.GetProperty("tokens").GetArrayLength();
        completed = true; return count;
        }
        finally
        {
            instance.Idle.Touch(); Interlocked.Decrement(ref instance.Busy);
            if (completed) ownership.Complete();
            else ownership.Unknown("local-measurement-terminal-unconfirmed");
        }
    }
    private static async Task<string> ApplyTemplateAsync(Instance instance, IReadOnlyList<ModelMessage> messages, CancellationToken token, string reasoningMode = "off")
    {
        // Token accounting must use the same thinking setting as the selected generation endpoint.
        using var template = await LlamaProtocol.PostAsync(instance.Http, "apply-template", new { messages, add_generation_prompt = true, chat_template_kwargs = new { enable_thinking = reasoningMode == "on" } }, token);
        return template.RootElement.GetProperty("prompt").GetString() ?? throw new InvalidDataException("Chat template returned no prompt.");
    }
    private static void ValidateReasoningMode(string mode)
    {
        if (mode is not ("off" or "on")) throw new ArgumentException("Reasoning mode must be off or on.");
    }
    private async Task<Instance> AcquireAsync(string id, CancellationToken token)
    {
        while (true)
        {
            await LoadAsync(id, token); await lifecycle.WaitAsync(token);
            try
            {
                ThrowIfOwnerRuntimeDisposed();
                if (resourcePauseRequested)
                    throw new InvalidOperationException("Local model admission is paused while this installation reserves its GPU.");
                if (instances.TryGetValue(id, out var instance) && !instance.Process.HasExited) { Interlocked.Increment(ref instance.Busy); return instance; }
            }
            finally { lifecycle.Release(); }
        }
    }
    private async Task CleanupExitedInstanceAsync(string id, Instance observed, CancellationToken token)
    {
        ModelRecord? failed = null;
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfOwnerRuntimeDisposed();
            token.ThrowIfCancellationRequested();
            if (!instances.TryGetValue(id, out var current) || !ReferenceEquals(current, observed) || !HasExited(current)) return;

            // The child is already terminal, so this cannot wait for live work. The synchronous
            // completion also drains redirected async output before the bounded diagnostic is built.
            current.Process.WaitForExit();
            var error = DescribeUnexpectedExit(current);
            await StopAsync(id, current);
            failed = store.Locked(() =>
            {
                var model = store.Get<ModelRecord>("model", id);
                // A deliberate unload/removal or another state transition wins. Read the latest
                // record and change only runtime state so every owner-configured field survives.
                if (model is null || model.State is not ("loaded" or "loading")) return null;
                model.State = "failed";
                model.Error = error;
                store.Put("model", id, model);
                return model;
            });
        }
        finally { lifecycle.Release(); }

        if (failed is not null) events.Publish("model.updated", null, failed);
    }

    private static string DescribeUnexpectedExit(Instance instance)
    {
        int? exitCode = null;
        try { if (instance.Process.HasExited) exitCode = instance.Process.ExitCode; }
        catch (InvalidOperationException) { }

        var boundedLog = instance.Log
            .Select(SanitizeDiagnosticLine)
            .Where(line => line.Length != 0)
            .ToArray();
        var outOfMemoryLine = boundedLog.LastOrDefault(IsOutOfMemoryDiagnostic);
        var lines = boundedLog.TakeLast(12).ToList();
        if (outOfMemoryLine is not null && !lines.Contains(outOfMemoryLine, StringComparer.Ordinal))
            lines.Add(outOfMemoryLine);
        var outOfMemory = outOfMemoryLine is not null;
        var summary = outOfMemory
            ? "The local llama-server exited after reporting GPU/CUDA out of memory"
            : "The local llama-server exited unexpectedly";
        if (exitCode is { } code) summary += $" (exit code {code})";
        summary += outOfMemory
            ? ". Free GPU memory or reduce context, concurrency, or GPU-layer placement before reloading."
            : ". Review the recent server log and reload the model after correcting the failure.";

        if (lines.Count == 0) return summary;
        const string label = "\nRecent server log:\n";
        var remaining = MaximumExitDiagnosticCharacters - summary.Length - label.Length;
        if (remaining <= 0) return summary[..MaximumExitDiagnosticCharacters];
        var kept = new List<string>();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var added = lines[index].Length + (kept.Count == 0 ? 0 : 1);
            if (added > remaining) break;
            kept.Add(lines[index]);
            remaining -= added;
        }
        kept.Reverse();
        return kept.Count == 0 ? summary : summary + label + string.Join('\n', kept);
    }

    private static string SanitizeDiagnosticLine(string line)
    {
        if (line.Length > 1000) line = line[..1000];
        return new string(line.Select(character => character is '\t' || !char.IsControl(character) ? character : ' ').ToArray()).Trim();
    }

    private static bool IsOutOfMemoryDiagnostic(string line) =>
        line.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("CUDA_ERROR_OUT_OF_MEMORY", StringComparison.OrdinalIgnoreCase);
    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        using var reader = new StreamReader(await content.ReadAsStreamAsync(token)); var chars = new char[4096]; var count = await reader.ReadAsync(chars.AsMemory(), token); return new string(chars, 0, count);
    }
    public async ValueTask DisposeAsync()
    {
        Accounting.Close("runtime-shutdown");
        MarkOwnerRuntimeDisposed();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            foreach (var (id, instance) in instances.ToArray())
                try { await StopAsync(id, instance); } catch (Exception error) { errors.Add(error); }
            if (Accounting.Snapshot().Entries.Any(IsRetainedNativeRegistration))
                errors.Add(new IOException("Registered native retirement is pending or failed; exact accounting ownership is retained."));
            if (errors.Count != 0) throw new AggregateException("One or more model cleanups remain unconfirmed.", errors);
        }
        // Keep the managed semaphore usable by already-waiting callers so they can observe
        // shutdown/cancellation and release it. No native wait handle is allocated.
        finally { lifecycle.Release(); }
    }
}
