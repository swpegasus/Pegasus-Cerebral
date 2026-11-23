using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

public sealed record ServicePackageLifecycleStatus(
    string Id,
    long Revision,
    bool DesiredActive,
    string? DesiredVersion,
    string? DesiredGeneration,
    string? StagedVersion,
    string? StagedGeneration,
    bool ObservedLoaded,
    string? ObservedVersion,
    string? ObservedGeneration,
    int ActiveRuns,
    bool RestartRequired,
    string State,
    string? Error = null,
    WorkflowPortableSettingsReview? WorkflowSettings = null);

public sealed record WorkflowPortableSettingsReview(
    string PluginId, string PackageVersion, string LoadedGeneration,
    string PackageFingerprint, string SchemaFingerprint,
    IReadOnlyList<string> DeclaredFields, WorkflowPortableSettingsApproval? ActiveApproval,
    long ApprovalRevision);

/// <summary>
/// Host-owned lifecycle for service packages. Candidate bytes are staged into an immutable generation;
/// selecting a generation changes desired state only. The executing Worker keeps reporting its observed
/// generation until a normal restart composes the selected repository graph.
/// </summary>
public sealed partial class ServicePackageLifecycle
{
    private const int StateSchema = 1;
    private const string LegacyGeneration = "legacy";
    private const string LifecycleDirectoryName = ".lifecycle";
    private const string PackagesDirectoryName = ".packages";
    private readonly object gate = new();
    private readonly string root;
    private readonly ServicePluginRuntime observedRuntime;
    private readonly EventHub events;
    private readonly IReadOnlyDictionary<string, ServicePluginPackageBinding> hostBuiltInPackages;
    private WorkflowStore? workflows;

    private static readonly JsonSerializerOptions StateJson = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = true
    };

    public static IReadOnlyList<WorkerWorkflows.OperationDescriptor> OperationDescriptors { get; } = BuildOperationDescriptors();

    public ServicePackageLifecycle(string repositoryRoot, ServicePluginRuntime observedRuntime, EventHub events,
        IReadOnlyList<ServicePluginPackageBinding>? hostBuiltInPackages = null)
    {
        root = Path.GetFullPath(repositoryRoot);
        this.observedRuntime = observedRuntime;
        this.events = events;
        var builtIns = (hostBuiltInPackages ?? []).ToArray();
        if (builtIns.Length > 16 || builtIns.Any(binding => binding is null || !binding.Loaded ||
                binding.Dependencies is null || NormalizeGeneration(binding.Generation) != LegacyGeneration))
            throw new ArgumentException("Host built-in package metadata must describe bounded loaded fallback bindings.", nameof(hostBuiltInPackages));
        foreach (var binding in builtIns)
        {
            ServicePluginRuntime.ValidatePackageId(binding.Id);
            if (!PluginVersion.TryParse(binding.Version, out _))
                throw new ArgumentException("Host built-in package metadata has an invalid version.", nameof(hostBuiltInPackages));
        }
        this.hostBuiltInPackages = builtIns.ToDictionary(binding => binding.Id, binding => binding with
        {
            Dependencies = Array.AsReadOnly(binding.Dependencies.ToArray())
        }, StringComparer.Ordinal);
    }

    internal void Attach(WorkflowStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (gate)
        {
            if (workflows is not null && !ReferenceEquals(workflows, store))
                throw new InvalidOperationException("The service package lifecycle is already bound to another workflow store.");
            workflows = store;
        }
    }

    public ServicePackageLifecycleStatus Inspect(string id, StateStore? store = null)
    {
        lock (gate)
        {
            ServicePluginRuntime.ValidatePackageId(id);
            var status = Status(id, ReadState(id) ?? InitialState(id));
            var loaded = store is null ? null : observedRuntime.LoadedWorkflowPortableSettingsForPackage(id);
            return loaded is null || !IsCurrentLoadedSelection(loaded) ? status :
                status with { WorkflowSettings = InspectWorkflowSettings(store!, id) };
        }
    }

    /// <summary>Owner review facts come only from a successfully published loaded package.</summary>
    private WorkflowPortableSettingsReview InspectWorkflowSettings(StateStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (gate)
        {
            ServicePluginRuntime.ValidatePackageId(id);
            var loaded = observedRuntime.LoadedWorkflowPortableSettingsForPackage(id)
                ?? throw new InvalidOperationException("The selected service package has no loaded workflow portable settings schema.");
            if (!IsCurrentLoadedSelection(loaded))
                throw new InvalidOperationException("The selected service package is not the loaded generation. Restart before review.");
            var current = ReadWorkflowApproval(store, id);
            return new(id, loaded.PackageVersion, loaded.LoadedGeneration, loaded.PackageFingerprint,
                loaded.SchemaFingerprint, Array.AsReadOnly(loaded.Schema.Fields.Select(field => field.Name).Order(StringComparer.Ordinal).ToArray()),
                CurrentApproval(loaded, current), current?.Revision ?? 0);
        }
    }

    /// <summary>Approval and its exact request receipt commit atomically under the package lifecycle gate.</summary>
    public WorkflowPortableSettingsApprovalResult ApproveWorkflowSettings(StateStore store,
        WorkflowPortableSettingsApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        WorkflowPortableSettingsApprovals.ValidateIdentity(request);
        if (request.ReviewedFields is null || request.ReviewedFields.Count is < 1 or > ServicePluginContract.MaxPortableSettings ||
            request.ReviewedFields.Any(string.IsNullOrWhiteSpace) ||
            request.ReviewedFields.Distinct(StringComparer.Ordinal).Count() != request.ReviewedFields.Count)
            throw new ArgumentException("Reviewed portable settings fields are invalid.");
        var sortedFields = Array.AsReadOnly(request.ReviewedFields.Order(StringComparer.Ordinal).ToArray());
        var digest = WorkflowPortableSettingsApprovals.Digest(request, sortedFields);
        lock (gate)
        {
            return store.Locked(() =>
            {
                var receipt = store.Get<WorkflowPortableSettingsApprovalReceipt>(
                    WorkflowPortableSettingsApprovals.ReceiptKind, request.RequestId);
                if (receipt is not null)
                {
                    WorkflowPortableSettingsApprovals.ValidateReceipt(receipt);
                    if (receipt.RequestDigest != digest)
                        throw new InvalidOperationException("Workflow settings approval request ID was already used for different input.");
                    var active = ActiveWorkflowSettingsApproval(store, receipt.AppliedApproval.PluginId);
                    return new WorkflowPortableSettingsApprovalResult(receipt.AppliedApproval, true,
                        active?.Revision == receipt.AppliedApproval.Revision && active.RequestId == receipt.RequestId);
                }
                var loaded = observedRuntime.LoadedWorkflowPortableSettingsForPackage(request.PluginId)
                    ?? throw new InvalidOperationException("The service package has no successfully loaded portable settings schema.");
                if (!IsCurrentLoadedSelection(loaded) || loaded.Validator is null ||
                    loaded.PackageVersion != request.PackageVersion || loaded.LoadedGeneration != request.LoadedGeneration ||
                    loaded.PackageFingerprint != request.PackageFingerprint || loaded.SchemaFingerprint != request.SchemaFingerprint)
                    throw new InvalidOperationException("Loaded service package settings identity changed. Refresh the owner review before approval.");
                // Rehash once at approval so externally changed package bytes cannot be blessed as the loaded instance.
                if (FingerprintPackage(loaded.PackageDirectory) != loaded.PackageFingerprint)
                    throw new InvalidOperationException("Service package bytes changed after loading. Restart before approval.");
                var reviewed = WorkflowPortableSettingsApprovals.ValidateAndSortFields(sortedFields, loaded.Schema);
                var current = ReadWorkflowApproval(store, request.PluginId);
                if ((current?.Revision ?? 0) != request.ExpectedRevision)
                    throw new InvalidOperationException("Workflow settings approval revision changed. Refresh before applying.");
                var approval = new WorkflowPortableSettingsApproval(WorkflowPortableSettingsApprovals.SchemaVersion,
                    request.PluginId, request.PackageVersion, request.LoadedGeneration, request.PackageFingerprint,
                    request.SchemaFingerprint, reviewed, checked(request.ExpectedRevision + 1), request.RequestId);
                WorkflowPortableSettingsApprovals.ValidateStored(approval);
                var applied = new WorkflowPortableSettingsApprovalReceipt(WorkflowPortableSettingsApprovals.SchemaVersion,
                    request.RequestId, digest, approval);
                store.Put(WorkflowPortableSettingsApprovals.ApprovalKind, request.PluginId, approval);
                store.Put(WorkflowPortableSettingsApprovals.ReceiptKind, request.RequestId, applied);
                var savedApproval = ReadWorkflowApproval(store, request.PluginId);
                var savedReceipt = store.Get<WorkflowPortableSettingsApprovalReceipt>(
                    WorkflowPortableSettingsApprovals.ReceiptKind, request.RequestId);
                if (savedApproval is null ||
                    !JsonElement.DeepEquals(CerebralJson.Element(savedApproval), CerebralJson.Element(approval)) ||
                    savedReceipt is null)
                    throw new InvalidDataException("Workflow settings approval readback failed.");
                WorkflowPortableSettingsApprovals.ValidateReceipt(savedReceipt);
                return new WorkflowPortableSettingsApprovalResult(approval, false, true);
            });
        }
    }

    internal WorkflowPortableSettingsApproval? ActiveWorkflowSettingsApproval(StateStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (gate)
        {
            ServicePluginRuntime.ValidatePackageId(id);
            var loaded = observedRuntime.LoadedWorkflowPortableSettingsForPackage(id);
            return loaded is not null && IsCurrentLoadedSelection(loaded)
                ? CurrentApproval(loaded, ReadWorkflowApproval(store, id)) : null;
        }
    }

    internal void RequireWorkflowSettingsApproval(StateStore store, string pluginId, string loadedGeneration,
        string packageFingerprint, string schemaFingerprint, long approvalRevision)
    {
        lock (gate)
        {
            var active = ActiveWorkflowSettingsApproval(store, pluginId);
            if (active is null || active.LoadedGeneration != loadedGeneration ||
                active.PackageFingerprint != packageFingerprint || active.SchemaFingerprint != schemaFingerprint ||
                active.Revision != approvalRevision)
                throw new InvalidOperationException("Accepted service workflow settings approval changed.");
        }
    }

    private static WorkflowPortableSettingsApproval? ReadWorkflowApproval(StateStore store, string id)
    {
        var value = store.Get<WorkflowPortableSettingsApproval>(WorkflowPortableSettingsApprovals.ApprovalKind, id);
        if (value is not null) WorkflowPortableSettingsApprovals.ValidateStored(value);
        if (value is not null && value.PluginId != id)
            throw new InvalidDataException("Stored workflow settings approval has a mismatched package identity.");
        return value;
    }

    private WorkflowPortableSettingsApproval? CurrentApproval(LoadedWorkflowPortableSettings loaded,
        WorkflowPortableSettingsApproval? approval) => approval is not null && IsCurrentLoadedSelection(loaded) &&
        loaded.Validator is not null && approval.PluginId == loaded.PackageId &&
        approval.PluginVersion == loaded.PackageVersion && approval.LoadedGeneration == loaded.LoadedGeneration &&
        approval.PackageFingerprint == loaded.PackageFingerprint && approval.SchemaFingerprint == loaded.SchemaFingerprint &&
        approval.ReviewedFields.All(field => loaded.Schema.Fields.Any(declared => declared.Name == field))
            ? approval : null;

    private bool IsCurrentLoadedSelection(LoadedWorkflowPortableSettings loaded)
    {
        var observed = Observed(loaded.PackageId);
        if (observed is not { Loaded: true } || observed.Version != loaded.PackageVersion ||
            NormalizeGeneration(observed.Generation) != NormalizeGeneration(loaded.LoadedGeneration)) return false;
        var state = ReadState(loaded.PackageId) ?? InitialState(loaded.PackageId);
        if (UsesBuiltInDefault(loaded.PackageId, state)) return true;
        return state.DesiredActive && state.DesiredVersion == loaded.PackageVersion &&
               NormalizeGeneration(state.DesiredGeneration) == NormalizeGeneration(loaded.LoadedGeneration);
    }

    public ServicePackageLifecycleStatus Stage(string manifestPath, long expectedRevision) =>
        Stage(manifestPath, expectedRevision, null);

    // A reviewed ID constrains the manifest before any staging write. It does not pin all package bytes.
    public ServicePackageLifecycleStatus Stage(string manifestPath, long expectedRevision, string? expectedId)
    {
        lock (gate)
        {
            var sourceManifest = Path.GetFullPath(manifestPath);
            if (!Path.GetFileName(sourceManifest).Equals(ServicePluginRuntime.ManifestFileName, StringComparison.Ordinal))
                throw new InvalidDataException("Select the package's exact service-plugin.json file.");
            var source = Path.GetDirectoryName(sourceManifest)!;
            ValidateExternalSource(source);
            var manifest = ServicePluginRuntime.InspectPackage(source, sourceManifest, expectedId, requireMatchingDirectory: false);
            var current = ReadState(manifest.Id) ?? InitialState(manifest.Id);
            RequireRevision(current, expectedRevision);
            var baseline = current.DesiredVersion ?? Observed(manifest.Id)?.Version;
            if (baseline is not null && PluginVersion.Parse(manifest.Version).CompareTo(PluginVersion.Parse(baseline)) < 0)
                throw new InvalidOperationException("A staged service package cannot lower the selected version. Remove it explicitly before installing an older version.");
            ValidateCandidateGraph(manifest);

            Directory.CreateDirectory(root);
            RejectLinks(root);
            var incoming = Path.Combine(root, ".incoming-" + manifest.Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incoming);
            string? destination = null;
            try
            {
                foreach (var file in PackageFiles(source))
                {
                    var target = Path.Combine(incoming, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, false);
                }
                var copied = ServicePluginRuntime.InspectPackage(incoming,
                    Path.Combine(incoming, ServicePluginRuntime.ManifestFileName), manifest.Id, requireMatchingDirectory: false);
                if (copied.Id != manifest.Id || copied.Version != manifest.Version)
                    throw new IOException("The service package changed while it was being staged. Review it again.");
                var generation = SafeGeneration(manifest.Version + "-" + Guid.NewGuid().ToString("N"));
                var packageStore = PackageStore(manifest.Id);
                Directory.CreateDirectory(packageStore);
                RejectLinks(packageStore);
                destination = Path.Combine(packageStore, generation);
                Directory.Move(incoming, destination);
                var next = current with
                {
                    Revision = checked(current.Revision + 1),
                    StagedVersion = manifest.Version,
                    StagedGeneration = generation
                };
                try { WriteState(next); }
                catch
                {
                    try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
                    throw;
                }
                Prune(manifest.Id, next);
                var status = Status(manifest.Id, next);
                events.Publish("service-packages.updated", null, status);
                return status;
            }
            finally { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }
        }
    }

    public ServicePackageLifecycleStatus Activate(string id, long expectedRevision)
    {
        lock (gate)
        {
            RequireWorkflowStore();
            ServicePluginRuntime.ValidatePackageId(id);
            var current = ReadState(id) ?? throw new InvalidOperationException("Stage a service package before activating it.");
            RequireRevision(current, expectedRevision);
            if (current.StagedVersion is null || current.StagedGeneration is null)
                throw new InvalidOperationException("No staged service package candidate is available.");
            var activeRuns = ActiveRunCount(id);
            if (activeRuns != 0)
                throw new InvalidOperationException($"{activeRuns} active durable workflow run(s) pin this service package. Drain or cancel them before changing the desired generation.");
            var candidateDirectory = GenerationDirectory(id, current.StagedGeneration);
            var manifest = ServicePluginRuntime.InspectPackage(candidateDirectory,
                Path.Combine(candidateDirectory, ServicePluginRuntime.ManifestFileName), id, requireMatchingDirectory: false);
            if (manifest.Id != id || manifest.Version != current.StagedVersion)
                throw new InvalidDataException("The staged service package no longer matches its retained lifecycle state.");
            ValidateCandidateGraph(manifest);
            var next = current with
            {
                Revision = checked(current.Revision + 1),
                DesiredActive = true,
                DesiredVersion = current.StagedVersion,
                DesiredGeneration = current.StagedGeneration,
                StagedVersion = null,
                StagedGeneration = null
            };
            WriteState(next);
            Prune(id, next);
            var status = Status(id, next);
            events.Publish("service-packages.updated", null, status);
            return status;
        }
    }

    public ServicePackageLifecycleStatus Deactivate(string id, long expectedRevision)
    {
        lock (gate)
        {
            RequireWorkflowStore();
            ServicePluginRuntime.ValidatePackageId(id);
            var current = ReadState(id) ?? InitialState(id);
            RequireRevision(current, expectedRevision);
            if (UsesBuiltInDefault(id, current))
                throw new InvalidOperationException("The selected built-in policy cannot be deactivated through native package lifecycle. Leave it selected or activate a native replacement after durable pins drain.");
            var activeRuns = ActiveRunCount(id);
            if (activeRuns != 0)
                throw new InvalidOperationException($"{activeRuns} active durable workflow run(s) pin this service package. Drain or cancel them before deactivating it.");
            if (!current.DesiredActive) return Status(id, current);
            var next = current with { Revision = checked(current.Revision + 1), DesiredActive = false };
            WriteState(next);
            var status = Status(id, next);
            events.Publish("service-packages.updated", null, status);
            return status;
        }
    }

    public ServicePackageLifecycleStatus Remove(string id, long expectedRevision)
    {
        lock (gate)
        {
            RequireWorkflowStore();
            ServicePluginRuntime.ValidatePackageId(id);
            var current = ReadState(id) ?? InitialState(id);
            RequireRevision(current, expectedRevision);
            var discardBuiltInCandidate = UsesBuiltInDefault(id, current) && current.StagedGeneration is not null;
            if (UsesBuiltInDefault(id, current) && !discardBuiltInCandidate)
                throw new InvalidOperationException("There is no staged native candidate to discard. The selected built-in policy cannot be removed through native package lifecycle.");
            if (current.DesiredActive)
                throw new InvalidOperationException("Deactivate the service package before removing it.");
            var activeRuns = ActiveRunCount(id);
            if (activeRuns != 0)
                throw new InvalidOperationException($"{activeRuns} active durable workflow run(s) pin this service package. Drain or cancel them before removing it.");
            if (NativeObserved(id) is { Loaded: true })
                throw new InvalidOperationException("Restart the Worker to observe deactivation before removing this service package.");

            if (discardBuiltInCandidate)
            {
                // This generation was never selected. Once existing pins permit removal,
                // discard only its candidate bytes and retain the host's built-in policy.
                var stagedDirectory = GenerationDirectory(id, current.StagedGeneration!);
                if (Directory.Exists(stagedDirectory)) { _ = PackageFiles(stagedDirectory); Directory.Delete(stagedDirectory, true); }
            }
            else
            {
                var legacy = Path.Combine(root, id);
                if (Directory.Exists(legacy)) { _ = PackageFiles(legacy); Directory.Delete(legacy, true); }
                var packageStore = PackageStore(id);
                if (Directory.Exists(packageStore)) { RejectLinks(packageStore); Directory.Delete(packageStore, true); }
            }
            var statePath = StatePath(id);
            RejectLinks(Path.GetDirectoryName(statePath)!);
            if (File.Exists(statePath)) File.Delete(statePath);
            var status = discardBuiltInCandidate
                ? Status(id, InitialState(id) with { Revision = checked(current.Revision + 1) })
                : new ServicePackageLifecycleStatus(id, checked(current.Revision + 1), false, null, null,
                    null, null, false, null, null, 0, false, "absent");
            events.Publish("service-packages.updated", null, status);
            return status;
        }
    }

    /// <summary>Returns an actionable reason when current desired state cannot accept a fresh run.</summary>
    internal string? AdmissionProblem(string definitionId) => AdmissionProblem(definitionId, []);

    internal string? AdmissionProblem(string definitionId, IReadOnlyList<string> additionalContributions)
    {
        var roots = WorkflowPackageBindings.AdditionalRoots(additionalContributions);
        lock (gate)
        {
            var graph = new HashSet<string>(StringComparer.Ordinal);
            var strictGraph = new HashSet<string>(StringComparer.Ordinal);
            string? unavailable = null;
            void Include(string id, bool strict)
            {
                if (strict ? !strictGraph.Add(id) : !graph.Add(id)) return;
                graph.Add(id);
                var binding = Observed(id);
                if (strict && (binding is not { Loaded: true } || binding.Dependencies is null ||
                    binding.Dependencies.Count > ServicePluginContract.MaxDependencies ||
                    binding.Dependencies.Any(dependency => !dependency.Optional && !dependency.Available)))
                {
                    unavailable ??= $"Service package '{id}' or its required dependency graph is unavailable. Fresh work cannot omit an explicitly accepted contribution.";
                    return;
                }
                if (binding is null) return;
                foreach (var dependency in binding.Dependencies.Where(item => item.Available))
                {
                    if (strict && dependency.ResolvedVersion != Observed(dependency.Id)?.Version)
                        unavailable ??= $"Service package '{id}' has a mismatched dependency graph. Fresh work cannot omit an explicitly accepted contribution.";
                    Include(dependency.Id, strict);
                }
            }
            Include(Owner(definitionId), false);
            foreach (var id in roots) Include(id, true);
            if (unavailable is not null) return unavailable;
            foreach (var id in graph)
            {
                var state = ReadState(id);
                if (state is null) continue;
                // Staging the first native replacement records no desired override. Only the
                // host's actually selected fallback can keep serving that staged-only owner.
                if (UsesBuiltInDefault(id, state)) continue;
                if (!state.DesiredActive)
                    return $"Service package '{id}' is deactivated. Restart the Worker before accepting new work from this contribution graph.";
                var observed = Observed(id);
                if (observed is null || !observed.Loaded || state.DesiredVersion != observed.Version ||
                    NormalizeGeneration(state.DesiredGeneration) != NormalizeGeneration(observed.Generation))
                    return $"Service package '{id}' has a selected generation that is not yet observed. Restart the Worker before accepting new work.";
            }
            return null;
        }
    }

    /// <summary>
    /// Holds the same lifecycle gate used by mutation through the durable workflow create. This closes the
    /// check/create race: either the run is committed first and mutation observes its pin, or desired state
    /// changes first and the fresh admission sees the restart/deactivation gate.
    /// </summary>
    internal T Admit<T>(string definitionId, string requestId, Func<T> durableCreate) =>
        Admit(definitionId, requestId, [], durableCreate);

    internal T Admit<T>(string definitionId, string requestId, IReadOnlyList<string> additionalContributions,
        Func<T> durableCreate)
    {
        ArgumentNullException.ThrowIfNull(durableCreate);
        var roots = WorkflowPackageBindings.AdditionalRoots(additionalContributions);
        lock (gate)
        {
            // A duplicate request returns its original accepted work even if desired state changed;
            // the host callback must preserve that saved snapshot rather than recapture live defaults.
            if (workflows?.TryGet(requestId) is not null) return durableCreate();
            if (AdmissionProblem(definitionId, roots) is { } problem) throw new InvalidOperationException(problem);
            return durableCreate();
        }
    }

    private ServicePackageLifecycleStatus Status(string id, LifecycleState state)
    {
        var observed = Observed(id);
        if (UsesBuiltInDefault(id, state))
            state = state with { DesiredActive = true, DesiredVersion = observed!.Version,
                DesiredGeneration = NormalizeGeneration(observed.Generation) };
        var activeRuns = ActiveRunCount(id);
        var observedGeneration = NormalizeGeneration(observed?.Generation);
        var desiredGeneration = NormalizeGeneration(state.DesiredGeneration);
        var restart = state.DesiredActive != (observed?.Loaded == true) || state.DesiredActive &&
            (state.DesiredVersion != observed?.Version || desiredGeneration != observedGeneration);
        var label = state.StagedVersion is not null ? "staged" : restart ? "restart_required" :
            state.DesiredActive ? "active" : observed?.Loaded == true ? "deactivating" : "inactive";
        return new(id, state.Revision, state.DesiredActive, state.DesiredVersion, state.DesiredGeneration,
            state.StagedVersion, state.StagedGeneration, observed?.Loaded == true, observed?.Version,
            observed?.Generation, activeRuns, restart, label, observed?.Error);
    }

    private LifecycleState InitialState(string id)
    {
        // Built-in policy is not a filesystem generation. Keep its initial desired tuple unset
        // so a staged-only repository still resolves to the same fallback after restart.
        var observed = NativeObserved(id);
        if (observed is null && !Directory.Exists(Path.Combine(root, id)))
            return new(StateSchema, id, 0, false, null, null, null, null);
        return new(StateSchema, id, 0, observed?.Loaded == true, observed?.Version,
            NormalizeGeneration(observed?.Generation), null, null);
    }

    private ServicePluginPackageBinding? NativeObserved(string id) =>
        observedRuntime.PackageBindings.SingleOrDefault(item => item.Id == id);

    private ServicePluginPackageBinding? Observed(string id)
    {
        // A selected native binding, including a failed one, always wins over fallback metadata.
        if (NativeObserved(id) is { } native) return native;
        if (!hostBuiltInPackages.TryGetValue(id, out var builtIn)) return null;
        var desired = ReadState(id);
        return desired is null || desired.DesiredActive || NoDesiredOverride(desired) ? builtIn : null;
    }

    private bool UsesBuiltInDefault(string id, LifecycleState state) =>
        NoDesiredOverride(state) && NativeObserved(id) is null && hostBuiltInPackages.ContainsKey(id);

    private static bool NoDesiredOverride(LifecycleState state) =>
        !state.DesiredActive && state.DesiredVersion is null && state.DesiredGeneration is null;

    private int ActiveRunCount(string id)
    {
        var store = workflows;
        if (store is null) return 0;
        var count = 0; long cursor = 0;
        while (true)
        {
            var page = store.ListActivePage(200, cursor);
            foreach (var run in page.Runs)
            {
                if (Owner(run.Submission.DefinitionId) == id) { count++; continue; }
                if (!run.Submission.SettingsSnapshot.TryGetProperty("packageBindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("An active durable workflow has no valid package binding; package mutation was not attempted.");
                ServicePluginPackageBinding[] saved;
                try { saved = bindings.Deserialize<ServicePluginPackageBinding[]>(CerebralJson.Options) ?? []; }
                catch (JsonException error) { throw new InvalidDataException("An active durable workflow has an invalid package binding; package mutation was not attempted.", error); }
                if (saved.Any(item => item.Id == id)) count++;
            }
            if (page.NextAfterRow is not { } next) break;
            cursor = next;
        }
        return count;
    }

    private void RequireWorkflowStore()
    {
        if (workflows is null)
            throw new InvalidOperationException("The durable workflow store is not attached to service package lifecycle. No desired state was changed.");
    }

    private void ValidateCandidateGraph(ServicePluginManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies.Where(item => !item.Optional))
        {
            var target = Observed(dependency.Id);
            if (target is not { Loaded: true } || !ServicePluginRuntime.DependencyCompatible(dependency, target.Version))
                throw new InvalidOperationException("Required service package dependency '" + dependency.Id + "' is not currently loaded at a compatible version.");
        }
        foreach (var dependent in observedRuntime.PackageBindings.Where(item => item.Loaded && item.Id != manifest.Id))
            foreach (var dependency in dependent.Dependencies.Where(item => !item.Optional && item.Id == manifest.Id))
                if (!DependencyCompatible(dependency, manifest.Version))
                    throw new InvalidOperationException("The candidate is incompatible with loaded dependent service package '" + dependent.Id + "'.");
    }

    private static bool DependencyCompatible(ServicePluginDependencyBinding dependency, string version)
    {
        if (!PluginVersion.TryParse(version, out var parsed)) return false;
        if (dependency.ExactVersion is not null) return dependency.ExactVersion == version;
        return (dependency.MinimumVersion is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MinimumVersion)) >= 0) &&
               (dependency.MaximumVersionExclusive is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MaximumVersionExclusive)) < 0);
    }

    private void ValidateExternalSource(string source)
    {
        var exact = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var repository = Path.TrimEndingDirectorySeparator(root);
        if (exact.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            exact.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            repository.StartsWith(exact + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a service package folder outside its managed repository.");
    }

    private static IReadOnlyList<string> PackageFiles(string directory)
    {
        RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        long bytes = 0; var folders = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 16 || ++folders > 256) throw new IOException("A service package has too many folders.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Service packages cannot contain links or junctions.");
                if (Directory.Exists(entry)) { pending.Push((entry, current.Depth + 1)); continue; }
                result.Add(entry); bytes += new FileInfo(entry).Length;
                if (result.Count > 512 || bytes > 268_435_456)
                    throw new IOException("Service packages are limited to 512 files and 256 MiB. Keep models and user data outside the package.");
            }
        }
        return result;
    }

    // The random lifecycle generation is not a digest. File names, lengths, and exact contents
    // are framed in a sorted bounded closure before this identity can be owner-approved.
    internal static string FingerprintPackage(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = PackageFiles(root).Select(path => new
        {
            AbsolutePath = path,
            RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/')
        }).OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(digest, files.Length);
        long totalBytes = 0;
        foreach (var file in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath);
            AppendInt32(digest, pathBytes.Length);
            digest.AppendData(pathBytes);
            using var input = File.OpenRead(file.AbsolutePath);
            totalBytes = checked(totalBytes + input.Length);
            if (totalBytes > 268_435_456)
                throw new IOException("Service package bytes exceed the bounded package closure.");
            AppendInt64(digest, input.Length);
            var contentHash = SHA256.HashData(input);
            if (input.Position != input.Length)
                throw new IOException("A service package file changed during fingerprinting.");
            digest.AppendData(contentHash);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void AppendInt32(IncrementalHash digest, int value) =>
        digest.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(value)));

    private static void AppendInt64(IncrementalHash digest, long value) =>
        digest.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(value)));

    private void Prune(string id, LifecycleState state)
    {
        var store = PackageStore(id);
        if (!Directory.Exists(store)) return;
        var observed = NormalizeGeneration(Observed(id)?.Generation);
        var retained = new[] { state.DesiredGeneration, state.StagedGeneration, observed }
            .Where(value => value is not null && value != LegacyGeneration).ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(store))
        {
            if (retained.Contains(Path.GetFileName(directory))) continue;
            try { _ = PackageFiles(directory); Directory.Delete(directory, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private LifecycleState? ReadState(string id) => ReadStateAt(root, id);

    private static LifecycleState? ReadStateAt(string repositoryRoot, string id)
    {
        var path = Path.Combine(repositoryRoot, LifecycleDirectoryName, id + ".json");
        if (!File.Exists(path)) return null;
        RejectLinks(path);
        using var input = File.OpenRead(path);
        if (input.Length is < 2 or > 16_384) throw new InvalidDataException("Service package lifecycle state is invalid or too large.");
        LifecycleState state;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(property => property.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                document.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("Service package lifecycle state has duplicate or invalid fields.");
            state = document.RootElement.Deserialize<LifecycleState>(StateJson)
                ?? throw new InvalidDataException("Service package lifecycle state is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("Service package lifecycle state is invalid.", error); }
        ValidateState(state, id);
        return state;
    }

    private void WriteState(LifecycleState state) => WriteStateAt(root, state);

    private static void WriteStateAt(string repositoryRoot, LifecycleState state)
    {
        ValidateState(state, state.Id);
        var path = Path.Combine(repositoryRoot, LifecycleDirectoryName, state.Id + ".json");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory); RejectLinks(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJson));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateState(LifecycleState state, string expectedId)
    {
        if (state.SchemaVersion != StateSchema || state.Id != expectedId || state.Revision < 0)
            throw new InvalidDataException("Service package lifecycle state has an unsupported identity or revision.");
        ServicePluginRuntime.ValidatePackageId(state.Id);
        if ((state.DesiredVersion is null) != (state.DesiredGeneration is null) ||
            (state.StagedVersion is null) != (state.StagedGeneration is null) ||
            state.DesiredActive && state.DesiredVersion is null)
            throw new InvalidDataException("Service package lifecycle version and generation fields are inconsistent.");
        foreach (var version in new[] { state.DesiredVersion, state.StagedVersion }.Where(value => value is not null))
            if (!PluginVersion.TryParse(version, out _)) throw new InvalidDataException("Service package lifecycle contains an invalid version.");
        foreach (var generation in new[] { state.DesiredGeneration, state.StagedGeneration }.Where(value => value is not null))
            _ = SafeGeneration(generation!);
    }

    private static void RequireRevision(LifecycleState state, long expected)
    {
        if (expected != state.Revision)
            throw new InvalidOperationException("Service package lifecycle state changed. Refresh it before applying this operation.");
    }

    internal static IReadOnlyList<RepositorySelection> ResolveRepository(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var selections = new Dictionary<string, RepositorySelection>(StringComparer.Ordinal);
        if (Directory.Exists(root))
            foreach (var directory in Directory.EnumerateDirectories(root).Where(path => !Path.GetFileName(path).StartsWith('.')))
            {
                var id = Path.GetFileName(directory);
                selections[id] = new(id, directory, LegacyGeneration, null);
            }
        var states = Path.Combine(root, LifecycleDirectoryName);
        if (!Directory.Exists(states)) return selections.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        RejectLinks(states);
        foreach (var path in Directory.EnumerateFiles(states, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                ServicePluginRuntime.ValidatePackageId(id);
                var state = ReadStateAt(root, id) ?? throw new InvalidDataException("Lifecycle state is empty.");
                selections.Remove(id);
                if (!state.DesiredActive) continue;
                var directory = state.DesiredGeneration == LegacyGeneration ? Path.Combine(root, id) :
                    Path.Combine(root, PackagesDirectoryName, id, state.DesiredGeneration!);
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The selected service package generation is unavailable.");
                selections[id] = new(id, directory, state.DesiredGeneration!, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                selections[id] = new(id, "", "", error.Message);
            }
        }
        return selections.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private string StatePath(string id) => Path.Combine(root, LifecycleDirectoryName, id + ".json");
    private string PackageStore(string id) => Path.Combine(root, PackagesDirectoryName, id);
    private string GenerationDirectory(string id, string generation) => Path.Combine(PackageStore(id), SafeGeneration(generation));
    private static string NormalizeGeneration(string? generation) => string.IsNullOrWhiteSpace(generation) ? LegacyGeneration : generation;
    private static string Owner(string contributionId) => contributionId.Split(':', 2)[0];
    private static string SafeGeneration(string generation)
    {
        if (string.IsNullOrWhiteSpace(generation) || generation.Length > 160 || generation is "." or ".." ||
            generation.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new InvalidDataException("Service package generation is invalid.");
        return generation;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Service package repositories cannot traverse links or junctions.");
    }

    internal sealed record RepositorySelection(string Id, string Directory, string Generation, string? Error);
    private sealed record LifecycleState(int SchemaVersion, string Id, long Revision, bool DesiredActive,
        string? DesiredVersion, string? DesiredGeneration, string? StagedVersion, string? StagedGeneration);

    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> BuildOperationDescriptors()
    {
        static JsonElement Schema(string fields, string required)
        {
            using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + fields +
                "},\"required\":[" + required + "],\"additionalProperties\":false}");
            return document.RootElement.Clone();
        }
        const string id = "\"id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":100}";
        const string revision = "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":0}";
        return Array.AsReadOnly(new[]
        {
            new WorkerWorkflows.OperationDescriptor("service-packages.inspect", 1,
                "Inspect one service package's staged, desired and currently observed version/generation without loading code.",
                Schema(id, "\"id\""), true),
            new WorkerWorkflows.OperationDescriptor("service-packages.stage", 1,
                "Copy and validate one local service-plugin.json package into an immutable candidate generation. Optional expectedId binds the reviewed identity; omitted or null preserves legacy behavior. It does not pin all package bytes or activate code.",
                Schema("\"manifestPath\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}," +
                    "\"expectedId\":{\"type\":[\"string\",\"null\"],\"minLength\":1,\"maxLength\":100}," + revision,
                    "\"manifestPath\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("service-packages.activate", 1,
                "Select the staged service generation after active durable pins drain. A normal Worker restart is required before it is observed.",
                Schema(id + "," + revision, "\"id\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("service-packages.deactivate", 1,
                "Block fresh admissions and select inactive state after active durable pins drain. Restart before removal.",
                Schema(id + "," + revision, "\"id\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("service-packages.remove", 1,
                "Remove an observed-inactive service package and its staged generation while preserving plugin data.",
                Schema(id + "," + revision, "\"id\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("service-packages.workflow-settings.approve", 1,
                "Owner-approve one reviewed nonsecret subset for the exact selected loaded service package and portable schema. Existing service-packages.inspect returns review coordinates. A matching request ID replays its original receipt without a new revision.",
                Schema("\"pluginId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":100}," +
                    "\"packageVersion\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}," +
                    "\"loadedGeneration\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"packageFingerprint\":{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}," +
                    "\"schemaFingerprint\":{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}," +
                    "\"reviewedFields\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":64,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":100}}," +
                    "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":0}," +
                    "\"requestId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}",
                    "\"pluginId\",\"packageVersion\",\"loadedGeneration\",\"packageFingerprint\",\"schemaFingerprint\",\"reviewedFields\",\"expectedRevision\",\"requestId\""), true)
        });
    }
}
