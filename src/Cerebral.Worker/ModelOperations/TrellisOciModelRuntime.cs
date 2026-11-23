using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Worker.ModelOperations;

/// <summary>One admitted activation, not an alternate scheduler, authorization service or idle owner.
/// A stopped/uncertain activation cannot be recycled into a new generation behind the same binding.</summary>
public sealed class TrellisOciModelRuntime : IModelOperationRuntime
{
    private const string EventSchema = "pegasus.trellis2.runtime-event.v1";
    private const string SourceRevision = "75fbf0183001ed9876c8dbb35de6b68552ee08bd";
    private const int MaximumFrameBytes = 128 * 1024;
    private const long MaximumStderrBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(20);
    private readonly ModelOperationRuntimeBinding binding;
    private readonly ModelOperationRuntimeDescriptor descriptor;
    private readonly ITrellisContainerControl container;
    private readonly string inputsRoot, outputsRoot, journalRoot;
    private readonly Func<ModelOperationInput, string, CancellationToken, Task> snapshotInput;
    private readonly Func<string, string?> ownerProfile;
    private readonly ModelOperationArtifactStore artifacts;
    private readonly SemaphoreSlim serial = new(1, 1), stopping = new(1, 1);
    private readonly object stateGate = new();
    private readonly CancellationTokenSource transportFailure = new();
    private readonly Dictionary<string, (string RequestSha256, ModelOperationResult Result)> completed = new(StringComparer.Ordinal);
    private Process? attached;
    private Task? stderrDrain;
    private OciModelContainerIdentity? identity;
    private ModelOperationRequest? active;
    private string state = "unloaded";
    private bool activated, physicalRelease, disposed;

    public ModelOperationRuntimeDescriptor Descriptor => ModelOperationContract.Freeze(descriptor);

    public TrellisOciModelRuntime(ModelOperationRuntimeDescriptor descriptor, ModelOperationRuntimeBinding binding,
        OciModelContainer container, string instanceInputRoot, string instanceOutputRoot,
        Func<ModelOperationInput, string, CancellationToken, Task> snapshotInput,
        Func<string, string?> ownerProfileForJob, ModelOperationArtifactStore artifacts)
        : this(descriptor, binding, new TrellisContainerControl(container), instanceInputRoot, instanceOutputRoot,
            snapshotInput, ownerProfileForJob, artifacts) { }

    // CPU-only fixtures replace only the daemon/control seam; production always wraps the exact OCI control.
    internal TrellisOciModelRuntime(ModelOperationRuntimeDescriptor descriptor, ModelOperationRuntimeBinding binding,
        ITrellisContainerControl container, string instanceInputRoot, string instanceOutputRoot,
        Func<ModelOperationInput, string, CancellationToken, Task> snapshotInput,
        Func<string, string?> ownerProfileForJob, ModelOperationArtifactStore artifacts)
    {
        this.descriptor = ModelOperationContract.Freeze(descriptor);
        this.binding = ModelOperationContract.Freeze(binding);
        this.container = container ?? throw new ArgumentNullException(nameof(container));
        this.snapshotInput = snapshotInput ?? throw new ArgumentNullException(nameof(snapshotInput));
        ownerProfile = ownerProfileForJob ?? throw new ArgumentNullException(nameof(ownerProfileForJob));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        inputsRoot = Root(instanceInputRoot); outputsRoot = Root(instanceOutputRoot);
        if (inputsRoot == outputsRoot || Within(inputsRoot, outputsRoot) || Within(outputsRoot, inputsRoot))
            throw new ArgumentException("Instance input and output roots must be separate, non-overlapping directories.");
        var spec = container.Specification;
        if (spec.WorkerInstallationId != this.binding.WorkerInstallationId || spec.InstanceId != this.binding.InstanceId ||
            Root(spec.InputDirectory) != inputsRoot || Root(spec.OutputDirectory) != outputsRoot ||
            this.descriptor.RuntimeId != this.binding.RuntimeId || this.descriptor.RuntimeVersion != this.binding.RuntimeVersion ||
            !this.descriptor.Profiles.Any(x => x.Id == this.binding.ProfileId && x.Sha256 == this.binding.ProfileSha256 && x.Mode == "container"))
            throw new InvalidDataException("Runtime descriptor, binding, OCI owner or mount identity differs.");
        journalRoot = Path.Combine(inputsRoot, ".runtime-bridge");
        ModelOperationArtifactStore.Ordinary(journalRoot);
        Directory.CreateDirectory(journalRoot);
    }

    public async Task<ModelOperationRuntimeState> LoadAsync(ModelOperationRuntimeBinding requested, CancellationToken token)
    {
        RequireBinding(requested);
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                ThrowIfDisposed();
                if (state == "loaded") return new(binding, "loaded", false);
                if (activated || state != "unloaded") return new(binding, "unknown", false,
                    new("activation_requires_reconciliation", "This exact activation cannot be replayed or replaced behind its original binding."));
                activated = true; state = "loading";
            }
            using var deadline = Deadline(token);
            try
            {
                identity = await container.CreateAsync(deadline.Token).ConfigureAwait(false);
                if (identity.WorkerInstallationId != binding.WorkerInstallationId || identity.InstanceId != binding.InstanceId)
                    throw new InvalidDataException("Created container identity differs from the admitted runtime binding.");
                attached = await container.StartAttachedAsync(deadline.Token).ConfigureAwait(false);
                stderrDrain = DrainStderrAsync(attached.StandardError, Path.Combine(journalRoot, "activation.stderr.log"));
                var ready = await ReadEventAsync(deadline.Token).ConfigureAwait(false);
                RequireEvent(ready, "ready", 0);
                if (Text(ready, "sourceRevision") != SourceRevision || Flag(ready, "validationOnly") ||
                    Number(ready, "maxTextureSize") != 4096 || Number(ready, "pipelineCacheMax") != 1)
                    throw new InvalidDataException("Runner readiness identity or initial profile differs.");
                var controlId = Guid.NewGuid().ToString("N");
                await SendAsync(new { schema = "pegasus.trellis2.runtime-control.v1", action = "load", controlId,
                    kind = ModelOperationContract.ImageTo3D, lowVram = true }, deadline.Token).ConfigureAwait(false);
                var sequence = 0;
                while (true)
                {
                    var message = await ReadEventAsync(deadline.Token).ConfigureAwait(false);
                    RequireIdentity(message, "controlId", controlId, sequence++);
                    var kind = Text(message, "event");
                    if (kind == "progress") continue;
                    if (kind != "loaded") throw new InvalidDataException("Runner did not confirm model materialization.");
                    var result = message.GetProperty("result");
                    if (!Flag(result, "modelMaterialized") || Text(result, "sourceRevision") != SourceRevision ||
                        Text(result, "kind") != ModelOperationContract.ImageTo3D ||
                        Text(result, "modelLockSha256") != container.Specification.ModelManifestSha256 || Number(result, "pipelineCacheMax") != 1)
                        throw new InvalidDataException("Loaded pipeline identity or model-lock attestation differs.");
                    var observed = await container.InspectAsync(deadline.Token).ConfigureAwait(false);
                    if (!observed.IdentityVerified || !observed.Running || observed.ContainerId != identity.ContainerId)
                        throw new InvalidDataException("The materialized runtime is not the exact live owned container.");
                    lock (stateGate)
                    {
                        if (state != "loading") throw new InvalidOperationException("Runtime changed during model load.");
                        state = "loaded";
                    }
                    return new(binding, "loaded", false);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                MarkUnknown();
                return new(binding, "unknown", false, new("load_unconfirmed",
                    "Model materialization or container ownership is unconfirmed; retain the activation and reconcile it before reuse."));
            }
        }
        finally { serial.Release(); }
    }

    public async Task<ModelOperationResult> RunAsync(ModelOperationRequest supplied,
        IProgress<ModelOperationProgress>? progress, CancellationToken token)
    {
        var request = ModelOperationContract.Freeze(supplied);
        RequireBinding(request.Binding);
        RequireSupported(request);
        var requestBytes = ModelOperationContract.Write(request);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(requestBytes));
        await serial.WaitAsync(token).ConfigureAwait(false);
        var dispatched = false; var claimed = false;
        try
        {
            var previous = Previous(request, requestHash);
            if (previous is not null) return previous;
            lock (stateGate)
            {
                ThrowIfDisposed();
                if (state != "loaded") return Result(request, ModelOperationState.Failed, false, "runtime_not_loaded", "The exact admitted runtime is not loaded.");
                active = request;
            }
            var owner = ownerProfile(request.JobId);
            if (owner is not null) ModelOperationArtifactStore.Identifier(owner);
            var inputJob = Child(inputsRoot, request.JobId);
            var claim = Path.Combine(journalRoot, request.JobId + ".request.json");
            WriteNew(claim, requestBytes);
            claimed = true;
            if (Directory.Exists(inputJob) || File.Exists(inputJob)) throw new IOException("A prior input snapshot exists; reconcile, never overwrite.");
            Directory.CreateDirectory(inputJob);
            var image = request.Inputs.Single(x => x.Role is "image" or "appearance");
            var imageFile = image.Artifact.MediaType == "image/png" ? "image.png" : "image.jpg";
            await SnapshotAsync(image, Path.Combine(inputJob, imageFile), token).ConfigureAwait(false);
            ModelOperationInput? mesh = request.Inputs.SingleOrDefault(x => x.Role == "mesh");
            if (mesh is not null) await SnapshotAsync(mesh, Path.Combine(inputJob, "mesh.glb"), token).ConfigureAwait(false);
            var payload = new JsonObject { ["schema"] = "pegasus.trellis2.runtime-request.v1", ["jobId"] = request.JobId,
                ["effectId"] = request.EffectId, ["kind"] = request.Operation,
                ["image"] = JsonSerializer.SerializeToNode(new { path = imageFile, sha256 = image.Artifact.Sha256 }),
                ["parameters"] = Parameters(request.Operation, request.Options) };
            if (mesh is not null) payload["mesh"] = JsonSerializer.SerializeToNode(new { path = "mesh.glb", sha256 = mesh.Artifact.Sha256 });
            using var deadline = Deadline(token);
            deadline.Token.ThrowIfCancellationRequested();
            lock (stateGate) { if (state != "loaded") throw new OperationCanceledException("Runtime is draining; no new dispatch is admitted."); }
            dispatched = true; // Lost/partial pipe writes are not permission to replay.
            await SendAsync(payload, deadline.Token).ConfigureAwait(false);
            var sequence = 0;
            while (true)
            {
                var message = await ReadEventAsync(deadline.Token).ConfigureAwait(false);
                RequireIdentity(message, "jobId", request.JobId, sequence++);
                if (Text(message, "effectId") != request.EffectId) throw new InvalidDataException("Runner effect identity differs.");
                var kind = Text(message, "event");
                if (kind == "progress")
                {
                    var stage = Text(message, "stage");
                    var phase = stage == "export" ? ModelOperationState.Exporting :
                        stage is "verifying_models" or "preparing-pipeline" or "loading_model" ? ModelOperationState.Loading : ModelOperationState.Running;
                    var update = new ModelOperationProgress(1, request.JobId, request.EffectId, phase, stage);
                    ModelOperationContract.Validate(update); progress?.Report(update); continue;
                }
                if (kind == "error")
                {
                    var uncertain = Flag(message, "partial") || Flag(message, "reconciliationRequired") || Text(message, "outcome") == "unknown";
                    if (uncertain) MarkUnknown();
                    var result = Result(request, uncertain ? ModelOperationState.Unknown : ModelOperationState.Failed, uncertain,
                        uncertain ? "runtime_outcome_unconfirmed" : "runtime_rejected", "Runtime rejected or could not confirm the original execution; inspect its exact retained job before retrying.");
                    return Save(requestHash, result);
                }
                if (kind != "result" || Text(message, "outcome") != "succeeded")
                    throw new InvalidDataException("Runner returned an unexpected terminal event.");
                var resultElement = message.GetProperty("result");
                var resultOutputs = await AdmitOutputsAsync(request, owner, resultElement, deadline.Token).ConfigureAwait(false);
                var completedResult = new ModelOperationResult(1, request.RequestId, request.JobId, request.EffectId, binding,
                    ModelOperationState.Succeeded, false, resultOutputs);
                ModelOperationContract.Validate(completedResult);
                return Save(requestHash, completedResult);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (dispatched) MarkUnknown();
            var confirmedCancellation = !dispatched && error is OperationCanceledException && physicalRelease;
            var result = Result(request, confirmedCancellation ? ModelOperationState.Cancelled : dispatched ? ModelOperationState.Unknown : ModelOperationState.Failed, dispatched,
                dispatched ? "runtime_outcome_unconfirmed" : "runtime_preflight_failed",
                dispatched ? "Execution, output admission or reply is unconfirmed. Preserve the exact job and CID; do not replay." :
                "The admitted job could not reach runtime dispatch. Inspect its retained claim before another request.");
            return claimed ? Save(requestHash, result) : result;
        }
        finally
        {
            lock (stateGate) { if (active?.JobId == request.JobId) active = null; }
            serial.Release();
        }
    }

    public async Task<ModelOperationResult> ExportAsync(ModelOperationExportRequest supplied,
        IProgress<ModelOperationProgress>? progress, CancellationToken token)
    {
        var request = ModelOperationContract.Freeze(supplied); RequireBinding(request.Binding);
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var originalPath = Path.Combine(journalRoot, request.JobId + ".request.json");
            if (!File.Exists(originalPath)) throw new InvalidOperationException("No exact original runtime request exists for this export.");
            var original = ModelOperationContract.Read<ModelOperationRequest>(ReadBounded(originalPath, ModelOperationContract.MaximumRequestBytes));
            RequireBinding(original.Binding);
            if (original.EffectId != request.EffectId || request.Options != new ModelOperationExportOptions() && request.Options != original.Options.Export)
                throw new NotSupportedException("Separate parameter-changing re-export is unsupported; the original model will not be run again.");
            var result = Previous(original, Convert.ToHexStringLower(SHA256.HashData(ModelOperationContract.Write(original))));
            if (result?.State != ModelOperationState.Succeeded)
                throw new InvalidOperationException("The exact original result is not confirmed complete; no generation/export was replayed.");
            return result;
        }
        finally { serial.Release(); }
    }

    public async Task<ModelOperationCancellation> CancelAsync(ModelOperationCancelRequest supplied, CancellationToken token)
    {
        var request = ModelOperationContract.Freeze(supplied); RequireBinding(request.Binding);
        lock (stateGate)
        {
            if (active is not null && (active.JobId != request.JobId || active.EffectId != request.EffectId))
                throw new InvalidOperationException("Cancellation does not identify the exact active job/effect.");
        }
        if (active is null)
        {
            var path = Path.Combine(journalRoot, request.JobId + ".request.json");
            if (!File.Exists(path)) throw new InvalidOperationException("No exact original job exists; no container stop was admitted.");
            var original = ModelOperationContract.Read<ModelOperationRequest>(ReadBounded(path, ModelOperationContract.MaximumRequestBytes));
            RequireBinding(original.Binding);
            if (original.JobId != request.JobId || original.EffectId != request.EffectId)
                throw new InvalidOperationException("Cancellation does not identify the retained original job/effect.");
        }
        var stopped = await StopOwnedAsync(token, request.JobId, request.EffectId).ConfigureAwait(false);
        return new(1, request.JobId, request.EffectId, true, stopped.PhysicalReleaseConfirmed, stopped.PhysicalReleaseConfirmed);
    }

    public Task<ModelOperationRuntimeState> UnloadAsync(ModelOperationRuntimeBinding requested, CancellationToken token)
    { RequireBinding(requested); return StopOwnedAsync(token); }

    public async ValueTask DisposeAsync()
    {
        lock (stateGate) { if (disposed) return; disposed = true; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await StopOwnedAsync(timeout.Token).ConfigureAwait(false);
        // The exact OCI ownership journal is retained, including unknown outcomes.
    }

    private async Task<ModelOperationRuntimeState> StopOwnedAsync(CancellationToken token, string? cancelJob = null, string? cancelEffect = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await stopping.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                lock (stateGate)
                {
                    if (cancelJob is not null && active is not null && (active.JobId != cancelJob || active.EffectId != cancelEffect))
                        throw new InvalidOperationException("A different job became active; no container stop was admitted.");
                    if (physicalRelease) return new(binding, "unloaded", true);
                    state = "draining";
                }
                OciModelContainerObservation observed;
                try { observed = await container.StopAsync(deadline.Token).ConfigureAwait(false); }
                catch (InvalidOperationException) when (identity is null && activated)
                {
                    // Read-only recovery validates the original creating intent,
                    // deterministic name and exact labels; it never creates again.
                    identity = await container.ReconcileCreatingAsync(deadline.Token).ConfigureAwait(false);
                    observed = await container.StopAsync(deadline.Token).ConfigureAwait(false);
                }
                if (!observed.IdentityVerified || !observed.SafeToRemove || observed.Running || identity is not null && observed.ContainerId != identity.ContainerId)
                    throw new IOException("Exact container termination is not confirmed.");
                lock (stateGate) { physicalRelease = true; state = "unloaded"; }
                transportFailure.Cancel();
                if (attached is not null)
                {
                    try
                    {
                        using var cliWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await attached.WaitForExitAsync(cliWait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            if (!attached.HasExited) attached.Kill(entireProcessTree: true);
                            using var killedWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await attached.WaitForExitAsync(killedWait.Token).ConfigureAwait(false);
                        }
                        if (stderrDrain is not null) await stderrDrain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        attached.Dispose(); attached = null;
                    }
                    catch { return new(binding, "unloaded", true, new("attach_cleanup_unconfirmed", "Owned CID is confirmed stopped; attached CLI/log cleanup still requires inspection.")); }
                }
                return new(binding, "unloaded", true);
            }
            finally { stopping.Release(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { MarkUnknown(); return new(binding, "unknown", false, new("container_stop_unconfirmed", "Retain this exact CID and residency reservation; CLI termination is not physical container release.")); }
    }

    private async Task SnapshotAsync(ModelOperationInput input, string destination, CancellationToken token)
    {
        ModelOperationArtifactStore.Ordinary(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Input snapshot already exists.");
        await snapshotInput(input, destination, token).ConfigureAwait(false);
        ModelOperationArtifactStore.Ordinary(destination);
        await using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != input.Artifact.Bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != input.Artifact.Sha256)
            throw new InvalidDataException("Host snapshot differs from the exact admitted input artifact.");
    }

    private async Task<IReadOnlyList<ModelOperationOutput>> AdmitOutputsAsync(ModelOperationRequest request, string? owner,
        JsonElement result, CancellationToken token)
    {
        if (Text(result, "schema") != "pegasus.trellis2.artifact-manifest.v1" || Text(result, "jobId") != request.JobId ||
            Text(result, "effectId") != request.EffectId || Text(result, "kind") != request.Operation)
            throw new InvalidDataException("Runner manifest identity differs.");
        var provenance = result.GetProperty("provenance");
        if (Text(provenance, "sourceRevision") != SourceRevision ||
            Text(provenance, "modelLockSha256") != container.Specification.ModelManifestSha256 || Number(provenance, "textureLimit") != 4096)
            throw new InvalidDataException("Runner source/model/profile attestation differs.");
        var parameters = result.GetProperty("parameters");
        RequireSubset(Parameters(request.Operation, request.Options), parameters);
        var inputs = result.GetProperty("inputs");
        foreach (var input in request.Inputs)
        {
            var key = input.Role == "mesh" ? "mesh" : "image";
            var expectedPath = input.Role == "mesh" ? "mesh.glb" : input.Artifact.MediaType == "image/png" ? "image.png" : "image.jpg";
            if (Text(inputs.GetProperty(key), "path") != expectedPath || Text(inputs.GetProperty(key), "sha256") != input.Artifact.Sha256)
                throw new InvalidDataException("Runtime manifest input snapshot identity differs.");
        }
        if (inputs.EnumerateObject().Count() != request.Inputs.Count) throw new InvalidDataException("Runtime manifest adds an unexpected input.");
        if (Number(parameters.GetProperty("export"), "textureSize") > 4096 || Number(parameters.GetProperty("export"), "textureSize") != (request.Options.TextureResolution ?? 2048))
            throw new InvalidDataException("Runtime texture settings differ from the admitted request.");
        var entries = result.GetProperty("artifacts").EnumerateArray().Select(x => x.Clone()).ToList();
        if (entries.Count != (request.Operation == ModelOperationContract.ImageTo3D ? request.Options.NumSamples ?? 1 : 1))
            throw new InvalidDataException("Runtime omitted or added model samples.");
        entries.Add(result.GetProperty("manifestArtifact").Clone());
        var jobRoot = Child(outputsRoot, request.JobId);
        var seen = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        var verified = new List<(JsonElement Entry, string Path, string Profile)>();
        foreach (var entry in entries)
        {
            var relative = Text(entry, "path");
            if (!seen.Add(relative)) throw new InvalidDataException("Repeated runtime artifact path.");
            var path = ArtifactPath(jobRoot, relative);
            var length = entry.GetProperty("bytes").GetInt64(); var sha = Text(entry, "sha256");
            if (length is <= 0 or > ModelOperationContract.MaximumArtifactBytes || !ModelOperationArtifactStore.IsDigest(sha))
                throw new InvalidDataException("Runtime artifact exceeds the admitted length/hash profile.");
            total = checked(total + length);
            if (total > ModelOperationContract.MaximumJobArtifactBytes) throw new InvalidDataException("Runtime job artifact budget exceeded.");
            var media = Text(entry, "mediaType"); var role = Text(entry, "role");
            var profile = media == "model/gltf-binary" && role == "mesh" ? "cerebral.gltf.pbr/1" :
                media == "application/json" && role == "manifest" ? "cerebral.model.provenance/1" : throw new InvalidDataException("Unexpected runtime artifact role/media.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != sha)
                throw new InvalidDataException("Runtime artifact bytes differ from its manifest.");
            verified.Add((entry, path, profile));
        }
        // The separate JSON artifact is the durable original; IPC must agree with
        // it before any bytes become visible in the host artifact store.
        var manifestEntry = verified.Single(x => Text(x.Entry, "role") == "manifest");
        var saved = Parse(ReadBounded(manifestEntry.Path, MaximumFrameBytes));
        foreach (var property in saved.EnumerateObject())
            if (!result.TryGetProperty(property.Name, out var actual) || property.Value.GetRawText() != actual.GetRawText())
                throw new InvalidDataException("IPC result differs from the durable artifact manifest.");
        var outputs = new List<ModelOperationOutput>();
        foreach (var item in verified)
        {
            var entry = item.Entry; var role = Text(entry, "role"); var sha = Text(entry, "sha256");
            var artifactId = "mop_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.InstanceId + "\n" + request.JobId + "\n" + request.EffectId + "\n" + Text(entry, "path") + "\n" + sha)));
            await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var admitted = await artifacts.CommitAsync(artifactId, request.JobId, request.EffectId, owner, role,
                Text(entry, "mediaType"), item.Profile, entry.GetProperty("bytes").GetInt64(), sha, stream, token).ConfigureAwait(false);
            outputs.Add(new(role, new(admitted.WorkerInstallationId, admitted.JobId, admitted.EffectId, admitted.Id,
                admitted.Revision, admitted.MediaType, admitted.MediaProfile, admitted.Role, admitted.Length, admitted.Sha256)));
        }
        return outputs;
    }

    private ModelOperationResult? Previous(ModelOperationRequest request, string sha)
    {
        if (completed.TryGetValue(request.JobId, out var cached))
        {
            if (cached.RequestSha256 != sha) throw new InvalidOperationException("Job identity was already bound to different request bytes.");
            VerifyCommitted(cached.Result); return cached.Result;
        }
        var claim = Path.Combine(journalRoot, request.JobId + ".request.json");
        if (!File.Exists(claim)) return null;
        var bytes = ReadBounded(claim, ModelOperationContract.MaximumRequestBytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new InvalidOperationException("Original runtime claim differs. No replay was sent.");
        var resultPath = Path.Combine(journalRoot, request.JobId + ".result.json");
        var result = File.Exists(resultPath) ? ModelOperationContract.Read<ModelOperationResult>(ReadBounded(resultPath, ModelOperationContract.MaximumRequestBytes)) :
            Result(request, ModelOperationState.Unknown, true, "runtime_outcome_unconfirmed", "An original dispatch claim exists without a confirmed result. Reconcile it; never replay automatically.");
        RequireBinding(result.Binding);
        if (result.JobId != request.JobId || result.EffectId != request.EffectId || result.RequestId != request.RequestId)
            throw new InvalidDataException("Stored runtime result identity differs.");
        VerifyCommitted(result); completed[request.JobId] = (sha, result);
        if (result.State == ModelOperationState.Unknown) MarkUnknown();
        TrimCache();
        return result;
    }

    private void VerifyCommitted(ModelOperationResult result)
    {
        var owner = ownerProfile(result.JobId);
        foreach (var item in result.Outputs)
        {
            var reference = item.Artifact;
            var stored = artifacts.Describe(reference.ArtifactId, checked((int)reference.Revision), owner);
            if (stored.WorkerInstallationId != binding.WorkerInstallationId || stored.JobId != result.JobId || stored.EffectId != result.EffectId ||
                stored.Sha256 != reference.Sha256 || stored.Length != reference.Bytes || stored.MediaProfile != reference.MediaProfile ||
                stored.MediaType != reference.MediaType || stored.Role != reference.Role)
                throw new InvalidDataException("Cached result no longer binds the exact authorized artifact metadata.");
        }
    }

    private ModelOperationResult Save(string sha, ModelOperationResult result)
    {
        ModelOperationContract.Validate(result);
        var path = Path.Combine(journalRoot, result.JobId + ".result.json");
        var bytes = ModelOperationContract.Write(result);
        if (File.Exists(path))
        {
            if (!ReadBounded(path, ModelOperationContract.MaximumRequestBytes).AsSpan().SequenceEqual(bytes))
                throw new IOException("A different result is already retained; preserve both execution identity and uncertainty.");
        }
        else WriteNew(path, bytes);
        completed[result.JobId] = (sha, result); TrimCache(); return result;
    }

    private async Task SendAsync<T>(T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length + 1 > MaximumFrameBytes) throw new InvalidDataException("IPC request exceeds its frame budget.");
        var process = attached ?? throw new IOException("Runtime is not attached.");
        await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
        await process.StandardInput.BaseStream.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadEventAsync(CancellationToken token)
    {
        var reader = attached?.StandardOutput ?? throw new IOException("Runtime is not attached.");
        var text = new StringBuilder(); var single = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(single.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Runtime IPC ended without the exact expected terminal event.");
            if (single[0] == '\n') break;
            if (text.Length >= MaximumFrameBytes) throw new InvalidDataException("Runtime frame exceeded its character budget.");
            text.Append(single[0]);
        }
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        if (bytes.Length == 0 || bytes.Length + 1 > MaximumFrameBytes) throw new InvalidDataException("Runtime frame exceeded its UTF-8 budget.");
        var message = Parse(bytes);
        if (Text(message, "schema") != EventSchema) throw new InvalidDataException("Unexpected runtime event schema.");
        return message;
    }

    private async Task DrainStderrAsync(StreamReader reader, string path)
    {
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var buffer = new char[4096]; long written = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                var bytes = Encoding.UTF8.GetBytes(buffer.AsSpan(0, count).ToString());
                var accepted = (int)Math.Min(bytes.Length, MaximumStderrBytes - written);
                if (accepted > 0) { await output.WriteAsync(bytes.AsMemory(0, accepted)).ConfigureAwait(false); written += accepted; }
                if (accepted != bytes.Length) transportFailure.Cancel(); // Continue draining/discarding until CID exits.
            }
            output.Flush(true);
        }
        catch { transportFailure.Cancel(); }
    }

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(token, transportFailure.Token);
        result.CancelAfter(OperationDeadline); return result;
    }
    private void RequireBinding(ModelOperationRuntimeBinding value)
    { ModelOperationContract.Validate(value); if (value != binding) throw new InvalidOperationException("Runtime binding/generation differs."); }
    private void MarkUnknown() { lock (stateGate) { if (!physicalRelease) state = "unknown"; } }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(TrellisOciModelRuntime)); }
    private void RequireSupported(ModelOperationRequest request)
    {
        if (!Regex.IsMatch(request.JobId, "^[A-Za-z0-9][A-Za-z0-9_-]{0,95}$") || !Regex.IsMatch(request.EffectId, "^[A-Za-z0-9][A-Za-z0-9_-]{0,95}$"))
            throw new ArgumentException("Runtime requires opaque host job/effect IDs within its 96-character path profile.");
        var profile = descriptor.Profiles.Single(x => x.Id == binding.ProfileId);
        if (!profile.Operations.Contains(request.Operation) || !descriptor.Capabilities.Any(x => x.Id == request.Operation))
            throw new NotSupportedException("Operation is not declared by the admitted runtime profile.");
        foreach (var input in request.Inputs)
            if (input.Artifact.WorkerInstallationId != binding.WorkerInstallationId || input.Artifact.Bytes > (input.Role == "mesh" ? 128L : 32L) * 1024 * 1024)
                throw new InvalidDataException("Input worker identity or provider byte limit differs.");
        if (request.Operation == ModelOperationContract.ImageTo3D && request.Options.Resolution is not null ||
            request.Operation == ModelOperationContract.MeshRetexture && (request.Options.PipelineType is not null || request.Options.NumSamples is not null ||
                request.Options.MaxNumTokens is not null || request.Options.SparseStructure is not null || request.Options.Shape is not null || request.Options.Export is not null))
            throw new NotSupportedException("Selected options are outside this operation's initial profile.");
    }
    private static JsonObject Parameters(string operation, ModelOperationOptions value)
    {
        var result = new JsonObject { ["seed"] = value.Seed ?? 42, ["preprocessImage"] = value.PreprocessImage ?? true,
            ["lowVram"] = true, ["export"] = new JsonObject { ["textureSize"] = value.TextureResolution ?? 2048, ["webp"] = true } };
        if (value.Texture is not null) result["texture"] = Sampler(value.Texture);
        if (operation == ModelOperationContract.ImageTo3D)
        {
            result["pipelineType"] = value.PipelineType switch { ModelOperationPipelineType.Resolution512 => "512", ModelOperationPipelineType.Resolution1024 => "1024",
                ModelOperationPipelineType.Cascade1536 => "1536_cascade", _ => "1024_cascade" };
            result["numSamples"] = value.NumSamples ?? 1; result["maxNumTokens"] = value.MaxNumTokens ?? 49152;
            if (value.SparseStructure is not null) result["sparseStructure"] = Sampler(value.SparseStructure);
            if (value.Shape is not null) result["shape"] = Sampler(value.Shape);
            if (value.Export is { } export)
            {
                var settings = result["export"]!.AsObject();
                if (export.DecimationTarget is { } target) settings["decimationTarget"] = target;
                if (export.Remesh is { } remesh) settings["remesh"] = remesh;
                if (export.RemeshBand is { } band) settings["remeshBand"] = band;
                if (export.RemeshProjection is { } projection) settings["remeshProject"] = projection;
            }
        }
        else result["resolution"] = value.Resolution ?? 1024;
        return result;
    }
    private static JsonObject Sampler(ModelOperationSamplerOptions value)
    {
        var result = new JsonObject();
        if (value.Steps is { } steps) result["steps"] = steps;
        if (value.GuidanceStrength is { } strength) result["guidanceStrength"] = strength;
        if (value.GuidanceRescale is { } rescale) result["guidanceRescale"] = rescale;
        if (value.TimeRescale is { } time) result["rescaleT"] = time;
        if (value.GuidanceInterval is { } interval) result["guidanceInterval"] = new JsonArray(interval.Start, interval.End);
        return result;
    }
    private void TrimCache() { while (completed.Count > 256) completed.Remove(completed.Keys.First()); }
    private static void RequireSubset(JsonNode expected, JsonElement actual)
    {
        if (expected is JsonObject obj)
        {
            if (actual.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Runtime option object differs.");
            foreach (var entry in obj)
            {
                if (entry.Value is null || !actual.TryGetProperty(entry.Key, out var child)) throw new InvalidDataException("Runtime omitted an admitted option.");
                RequireSubset(entry.Value, child);
            }
            return;
        }
        using var encoded = JsonDocument.Parse(expected.ToJsonString());
        if (!JsonElement.DeepEquals(encoded.RootElement, actual)) throw new InvalidDataException("Runtime effective options differ from the admitted request.");
    }
    private ModelOperationResult Result(ModelOperationRequest request, ModelOperationState outcome, bool partial, string code, string message)
        => new(1, request.RequestId, request.JobId, request.EffectId, binding, outcome, partial, [], new(code, message));
    private static void RequireEvent(JsonElement message, string kind, int sequence)
    { if (Text(message, "event") != kind || Number(message, "sequence") != sequence) throw new InvalidDataException("Runtime event/state sequence differs."); }
    private static void RequireIdentity(JsonElement message, string field, string expected, int sequence)
    { if (Text(message, field) != expected || Number(message, "sequence") != sequence) throw new InvalidDataException("Runtime event identity or sequence differs."); }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing runtime text field.");
    private static bool Flag(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static int Number(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static JsonElement Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Runtime JSON is outside its bound.");
        // Reject duplicate names at every object depth before interpreting identity.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate runtime JSON property.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Runtime event must be an object.");
        return document.RootElement.Clone();
    }
    private static string Root(string path)
    { ModelOperationArtifactStore.Ordinary(path); var result = Path.GetFullPath(path); if (!Directory.Exists(result)) throw new DirectoryNotFoundException("Host must prepare the exact instance roots."); return Path.TrimEndingDirectorySeparator(result); }
    private static bool Within(string root, string path) => Path.GetRelativePath(root, path) is { } relative && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    private static string Child(string root, string name)
    { ModelOperationArtifactStore.Identifier(name); var result = Path.GetFullPath(Path.Combine(root, name)); ModelOperationArtifactStore.Ordinary(result); if (!Within(root, result) || result == root) throw new InvalidDataException("Runtime path leaves its owned root."); return result; }
    private static string ArtifactPath(string root, string path)
    {
        if (path.Length is 0 or > 512 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(x => x is "" or "." or "..") || !path.StartsWith("outputs/", StringComparison.Ordinal))
            throw new InvalidDataException("Artifact must name an ordinary relative output file.");
        var result = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        ModelOperationArtifactStore.Ordinary(result); if (!Within(root, result)) throw new InvalidDataException("Artifact escapes its job result root."); return result;
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        ModelOperationArtifactStore.Ordinary(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Retained runtime document exceeds its bound.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static void WriteNew(string path, byte[] bytes)
    { ModelOperationArtifactStore.Ordinary(path); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes); output.Flush(true); }
}

internal interface ITrellisContainerControl
{
    OciModelContainerSpec Specification { get; }
    Task<OciModelContainerIdentity> CreateAsync(CancellationToken token);
    Task<OciModelContainerIdentity> ReconcileCreatingAsync(CancellationToken token);
    Task<Process> StartAttachedAsync(CancellationToken token);
    Task<OciModelContainerObservation> InspectAsync(CancellationToken token);
    Task<OciModelContainerObservation> StopAsync(CancellationToken token);
}

internal sealed class TrellisContainerControl(OciModelContainer value) : ITrellisContainerControl
{
    public OciModelContainerSpec Specification => value.Specification;
    public Task<OciModelContainerIdentity> CreateAsync(CancellationToken token) => value.CreateAsync(token);
    public Task<OciModelContainerIdentity> ReconcileCreatingAsync(CancellationToken token) => value.ReconcileCreatingAsync(token);
    public Task<Process> StartAttachedAsync(CancellationToken token) => value.StartAttachedAsync(token);
    public Task<OciModelContainerObservation> InspectAsync(CancellationToken token) => value.InspectAsync(token);
    public Task<OciModelContainerObservation> StopAsync(CancellationToken token) => value.StopAsync(token);
}
