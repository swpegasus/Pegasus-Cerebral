using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using System.Runtime.CompilerServices;

namespace Cerebral.Worker.ModelAdapters;

// Worker-private runtime binding data. Transport/session types need no model or vendor fields.
internal sealed record AdapterBinding(string BackendId, string ModelId, string InstanceId,
    string AdapterId, string AdapterVersion);
internal sealed record AdapterRequirements(IReadOnlyList<string> ContentKinds, bool Reasoning = false);
internal enum PreparationKind { ExactPreparedTokens, EstimatedTokens }
internal enum InstanceAdmission { Active, Draining, Retiring, RegistryRetired, CleanupFaulted }
internal sealed class RegistryException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
internal sealed record InstanceObservation(string BackendId, string ModelId, string InstanceId,
    InstanceAdmission Admission, int RegistryReservations, int LogicalRetirements,
    int UnconfirmedCleanupCount, bool InstanceDisposeReturned, bool? PhysicalStopConfirmed = null);
internal sealed record LeaseObservation(bool GenerationStarted, bool LogicalRetirement,
    bool DelegatedDisposeReturned, ModelGenerationFinish? ProviderFinish,
    bool? ProviderCancellationConfirmed, bool? PhysicalReleaseConfirmed = null);

/// <summary>Private selection/lifecycle mechanism. Providers retain their existing slot allocator,
/// tokenization, native cancellation and quarantine behavior. This class has no inference queue.</summary>
internal sealed class AdapterRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<(string Backend, string Model, string Instance), Entry> entries = [];
    // A provider must return a fresh one-use object. Weak tombstones prevent borrowing or reusing
    // a still-reachable preparation, without retaining an unbounded history of completed leases.
    private readonly ConditionalWeakTable<IPreparedModelGeneration, object> preparations = new();
    private static readonly HashSet<string> ContentKinds = new(StringComparer.Ordinal)
        { "text", "file", "image", "audio", "structured", "resource" };

    internal sealed class Entry(string backend, string model, string instance, IModelRuntimeAdapter adapter,
        IAsyncDisposable owner, ModelAdapterDescriptor facade, ModelAdapterDescriptor[] facets)
    {
        public readonly string Backend = backend, Model = model, Instance = instance;
        public readonly IModelRuntimeAdapter Adapter = adapter;
        public readonly IAsyncDisposable Owner = owner;
        public readonly ModelAdapterDescriptor Facade = facade;
        public readonly ModelAdapterDescriptor[] Facets = facets;
        internal ModelRuntimeSupportSnapshot? RuntimeSupport { get; init; }
        public InstanceAdmission State;
        public int Reservations, Retirements, CleanupUnknown;
        public bool InstanceDisposeReturned;
    }

    public void Register(string backend, string model, string instance, IModelRuntimeAdapter adapter,
        IAsyncDisposable instanceOwner, IReadOnlyList<ModelAdapterDescriptor> preparedFacets,
        PreparationKind preparation = PreparationKind.ExactPreparedTokens) =>
        RegisterCore(backend, model, instance, adapter, instanceOwner, preparedFacets, preparation, null, []);

    internal void RegisterWithRuntimeSupport(string backend, string model, string instance, IModelRuntimeAdapter adapter,
        IAsyncDisposable instanceOwner, IReadOnlyList<ModelAdapterDescriptor> preparedFacets,
        ModelRuntimeSupportBinding supportBinding, IReadOnlyList<RuntimeCapabilityImplementation> implementations,
        PreparationKind preparation = PreparationKind.ExactPreparedTokens)
    {
        ArgumentNullException.ThrowIfNull(supportBinding);
        RegisterCore(backend, model, instance, adapter, instanceOwner, preparedFacets, preparation, supportBinding, implementations);
    }

    private void RegisterCore(string backend, string model, string instance, IModelRuntimeAdapter adapter,
        IAsyncDisposable instanceOwner, IReadOnlyList<ModelAdapterDescriptor> preparedFacets, PreparationKind preparation,
        ModelRuntimeSupportBinding? supportBinding, IReadOnlyList<RuntimeCapabilityImplementation> implementations)
    {
        Id(backend); Id(model); Id(instance);
        ArgumentNullException.ThrowIfNull(adapter); ArgumentNullException.ThrowIfNull(instanceOwner);
        if (preparation != PreparationKind.ExactPreparedTokens) throw new RegistryException("exact_preparation_required");
        var facade = Freeze(adapter.Descriptor);
        if (preparedFacets is null || preparedFacets.Count is < 1 or > 8) throw new RegistryException("facet_bound");
        var facets = preparedFacets.Select(Freeze).ToArray();
        if (facets.Select(x => (x.AdapterId, x.Version)).Distinct().Count() != facets.Length)
            throw new RegistryException("duplicate_facet");
        if (facets.Any(x => x.ContentCapabilities.Any(kind => !facade.ContentCapabilities.Contains(kind, StringComparer.Ordinal)) ||
            x.SupportsReasoning && !facade.SupportsReasoning)) throw new RegistryException("facet_not_supported_by_facade");
        var runtimeSupport = ModelRuntimeSupportAdmission.Capture(adapter, facade,
            new(backend, model, instance, facade.AdapterId, facade.Version), supportBinding, implementations);
        lock (gate)
        {
            // Retired identities remain tombstones for this bounded registry lifetime. A stale binding
            // never starts targeting a replacement that reused the same backend/model/instance tuple.
            if (entries.ContainsKey((backend, model, instance))) throw new RegistryException("instance_identity_reused");
            if (entries.Count >= 64) throw new RegistryException("registration_bound");
            if (entries.Values.Any(x => ReferenceEquals(x.Owner, instanceOwner)))
                throw new RegistryException("instance_owner_reused");
            entries.Add((backend, model, instance), new(backend, model, instance, adapter, instanceOwner, facade, facets) { RuntimeSupport = runtimeSupport });
        }
    }

    // Passive internal snapshot only; no public operation or expanded request admission is added.
    internal ModelRuntimeSupportSnapshot? RuntimeSupport(AdapterBinding binding)
    {
        Validate(binding);
        lock (gate)
        {
            var entry = Find(binding); RequireActive(entry); _ = Facet(entry, binding);
            var support = entry.RuntimeSupport;
            return support?.Descriptor.Binding.AdapterId == binding.AdapterId &&
                support.Descriptor.Binding.AdapterVersion == binding.AdapterVersion ? support : null;
        }
    }

    public IModelRuntimeAdapter CreateAdapter(AdapterBinding binding, AdapterRequirements requirements)
    {
        Validate(binding); ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.ContentKinds is null || requirements.ContentKinds.Count > 6)
            throw new RegistryException("required_capabilities_invalid");
        var kinds = requirements.ContentKinds.ToArray();
        if (kinds.Any(x => !ContentKinds.Contains(x))) throw new RegistryException("required_capabilities_invalid");
        lock (gate)
        {
            var entry = Find(binding);
            RequireActive(entry); RequireDescriptor(entry);
            var facet = Facet(entry, binding);
            RequireCapabilities(facet, kinds, requirements.Reasoning);
            return new BoundAdapter(this, entry, binding, facet, new(Array.AsReadOnly(kinds), requirements.Reasoning));
        }
    }

    // Optional structural validation belongs to the same registered handler. Never invoke its
    // getter or validator under the short registry gate, and never reserve an owner for a refusal.
    // Legacy Register and support handlers without this optional facet retain their old path.
    internal bool TryValidateRequestShape(AdapterBinding binding, ModelGenerationRequest request)
        => TryValidateRequestShape(binding, request, null);

    internal bool TryValidateRequestShape(AdapterBinding binding, ModelGenerationRequest request,
        ModelExecutionPolicy? selected)
    {
        Validate(binding); ArgumentNullException.ThrowIfNull(request);
        Entry entry; ModelRuntimeSupportSnapshot? support; IRegisteredNativeRequestShapeFacet? shape;
        lock (gate)
        {
            entry = Find(binding); RequireActive(entry); RequireDescriptor(entry); _ = Facet(entry, binding);
            support = entry.RuntimeSupport;
            shape = support is null ? null : entry.Adapter as IRegisteredNativeRequestShapeFacet;
        }
        if (shape is null) return false;
        var claimed = shape.RequestShapeBinding;
        if (claimed != support!.Descriptor.Binding) throw new RegistryException("request_shape_binding_mismatch");
        if (selected is null) shape.ValidateRequestShape(request);
        else
        {
            if (entry.Adapter is not IRegisteredNativePolicyExecutor executor ||
                executor.ExecutionPolicy != selected || selected.Binding != claimed)
                throw new RegistryException("selected_policy_handler_mismatch");
            RegisteredNativeTextRequestShape.Require(request, binding.ModelId, selected);
        }
        if (shape.RequestShapeBinding != claimed) throw new RegistryException("request_shape_binding_changed");
        lock (gate)
        {
            RequireActive(entry); RequireDescriptor(entry); _ = Facet(entry, binding);
            if (!ReferenceEquals(entry.Adapter, shape) || !ReferenceEquals(entry.RuntimeSupport, support))
                throw new RegistryException("request_shape_handler_changed");
        }
        return true;
    }

    internal async Task<RegistryLease> PrepareSelectedAsync(AdapterBinding binding,
        AdapterRequirements requirements, ModelGenerationRequest request,
        ModelExecutionPolicy selected, CancellationToken token)
    {
        Validate(binding); ArgumentNullException.ThrowIfNull(requirements);
        selected.Validate();
        Entry entry; ModelAdapterDescriptor facet;
        lock (gate)
        {
            entry = Find(binding); RequireActive(entry); RequireDescriptor(entry);
            facet = Facet(entry, binding);
            RequireCapabilities(facet, requirements.ContentKinds, requirements.Reasoning);
            if (requirements.Reasoning || entry.Adapter is not IRegisteredNativePolicyExecutor)
                throw new RegistryException("selected_policy_handler_unavailable");
        }
        return (RegistryLease)await PrepareAsync(entry, binding, facet, requirements,
            request, token, selected).ConfigureAwait(false);
    }

    public InstanceObservation Observe(AdapterBinding binding)
    {
        Validate(binding);
        lock (gate) { var e = Find(binding); _ = Facet(e, binding); return View(e); }
    }

    public async Task<InstanceObservation> RetireAsync(AdapterBinding binding)
    {
        Validate(binding); Entry entry;
        lock (gate)
        {
            entry = Find(binding); _ = Facet(entry, binding);
            if (entry.State == InstanceAdmission.RegistryRetired) return View(entry);
            if (entry.State == InstanceAdmission.CleanupFaulted) throw new RegistryException("cleanup_unconfirmed");
            if (entry.State == InstanceAdmission.Retiring) throw new RegistryException("instance_retirement_in_progress");
            entry.State = InstanceAdmission.Draining;
            if (entry.Reservations != 0) throw new RegistryException("instance_has_registry_owners");
            entry.State = InstanceAdmission.Retiring;
        }
        try { await entry.Owner.DisposeAsync().ConfigureAwait(false); }
        catch
        {
            lock (gate) { entry.State = InstanceAdmission.CleanupFaulted; entry.CleanupUnknown++; }
            throw new RegistryException("instance_cleanup_unconfirmed");
        }
        lock (gate)
        {
            entry.InstanceDisposeReturned = true; entry.State = InstanceAdmission.RegistryRetired;
            return View(entry); // A returned Dispose does not prove a native process or allocation stopped.
        }
    }

    private sealed class BoundAdapter(AdapterRegistry registry, Entry entry, AdapterBinding binding,
        ModelAdapterDescriptor facet, AdapterRequirements requirements) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => facet;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            registry.PrepareAsync(entry, binding, facet, requirements, request, token);
    }

    private async Task<IPreparedModelGeneration> PrepareAsync(Entry entry, AdapterBinding binding,
        ModelAdapterDescriptor facet, AdapterRequirements requirements, ModelGenerationRequest request, CancellationToken token,
        ModelExecutionPolicy? selected = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (selected is null) request.Validate(); else request.Validate(selected);
        token.ThrowIfCancellationRequested();
        // Reuse the same admitted DTO snapshot utility as RemoteModelRuntimeAdapter. No caller-owned
        // list, metadata dictionary or JsonDocument lifetime crosses the first provider await.
        request = CerebralJson.Read<ModelGenerationRequest>(CerebralJson.Element(request));
        if (selected is null) request.Validate(); else request.Validate(selected);
        if (request.ModelId != binding.ModelId) throw new RegistryException("request_model_mismatch");
        RequireCapabilities(facet, requirements.ContentKinds.Concat(request.Messages.SelectMany(x => x.Content).Select(Kind)),
            requirements.Reasoning || request.ReasoningTokens != 0);
        _ = TryValidateRequestShape(binding, request, selected);
        lock (gate)
        {
            RequireActive(entry); RequireDescriptor(entry);
            if (entry.Reservations >= 256) throw new RegistryException("registry_owner_bound");
            entry.Reservations++;
        }
        IPreparedModelGeneration? prepared = null;
        var transferred = false; var cleanupUnconfirmed = false; var claimedReturnedObject = false;
        try
        {
            // Forward one frozen request to the existing seam. Providers own template/token counting
            // and capacity. The registry never estimates, changes budgets or retries preparation.
            try
            {
                prepared = selected is null
                    ? await entry.Adapter.PrepareAsync(request, token).ConfigureAwait(false)
                    : await ((IRegisteredNativePolicyExecutor)entry.Adapter)
                        .PrepareSelectedAsync(request, selected, token).ConfigureAwait(false);
            }
            catch (ModelContextLimitException error)
            {
                Metadata(binding, request, error.Metadata, true, selected); throw;
            }
            if (prepared is null) throw new RegistryException("provider_returned_no_preparation");
            if (prepared is RegistryLease)
            {
                // A registry-issued wrapper already belongs to a registry owner, active or retired.
                // Its interface does not grant this provider call authority to dispose it.
                prepared = null;
                throw new RegistryException("provider_returned_registry_owned_lease");
            }
            lock (gate)
            {
                if (preparations.TryGetValue(prepared, out _))
                {
                    // This call acquired no ownership of the existing object. In particular, its
                    // rejection path must not dispose another instance's live preparation.
                    prepared = null;
                    throw new RegistryException("provider_preparation_object_reused");
                }
                preparations.Add(prepared, new object());
                claimedReturnedObject = true;
            }
            var metadata = prepared.Metadata;
            Metadata(binding, request, metadata, false, selected);
            if (selected is not null) LocalModelExecutionPolicy.Require(prepared, selected);
            token.ThrowIfCancellationRequested();
            lock (gate) { RequireDescriptor(entry); }
            RegistryLease result = selected is null
                ? new RegistryLease(this, entry, binding, prepared, metadata)
                : new PolicyRegistryLease(this, entry, binding, prepared, metadata, selected);
            transferred = true; return result;
        }
        finally
        {
            if (!transferred)
            {
                if (prepared is not null && claimedReturnedObject)
                {
                    try { await prepared.DisposeAsync().ConfigureAwait(false); }
                    catch { cleanupUnconfirmed = true; CleanupFailed(entry); }
                }
                if (!cleanupUnconfirmed) Retired(entry);
                else throw new RegistryException("rejected_preparation_cleanup_unconfirmed");
            }
        }
    }

    private void Retired(Entry entry) { lock (gate) { entry.Reservations--; entry.Retirements++; } }
    private void CleanupFailed(Entry entry)
    { lock (gate) { entry.CleanupUnknown++; entry.State = InstanceAdmission.CleanupFaulted; } }
    private static InstanceObservation View(Entry e) => new(e.Backend, e.Model, e.Instance, e.State,
        e.Reservations, e.Retirements, e.CleanupUnknown, e.InstanceDisposeReturned);
    private Entry Find(AdapterBinding binding) => entries.TryGetValue((binding.BackendId, binding.ModelId, binding.InstanceId), out var entry)
        ? entry : throw new RegistryException("instance_binding_unknown");
    private static ModelAdapterDescriptor Facet(Entry entry, AdapterBinding b) => entry.Facets.SingleOrDefault(x =>
        x.AdapterId == b.AdapterId && x.Version == b.AdapterVersion) ?? throw new RegistryException("prepared_adapter_mismatch");
    private static void RequireActive(Entry e)
    { if (e.State != InstanceAdmission.Active) throw new RegistryException("instance_admission_closed"); }
    private static void RequireDescriptor(Entry e)
    {
        var now = Freeze(e.Adapter.Descriptor);
        if (now.AdapterId != e.Facade.AdapterId || now.Version != e.Facade.Version || now.SupportsReasoning != e.Facade.SupportsReasoning ||
            !now.ContentCapabilities.SequenceEqual(e.Facade.ContentCapabilities, StringComparer.Ordinal))
            throw new RegistryException("adapter_descriptor_changed");
    }
    private static void RequireCapabilities(ModelAdapterDescriptor descriptor, IEnumerable<string> kinds, bool reasoning)
    {
        if (kinds.Any(x => !descriptor.ContentCapabilities.Contains(x, StringComparer.Ordinal)) || reasoning && !descriptor.SupportsReasoning)
            throw new RegistryException("capability_unsupported");
    }
    private static ModelAdapterDescriptor Freeze(ModelAdapterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor); Id(descriptor.AdapterId); Id(descriptor.Version);
        if (descriptor.ContentCapabilities is null || descriptor.ContentCapabilities.Count is < 1 or > 6 ||
            descriptor.ContentCapabilities.Any(x => !ContentKinds.Contains(x)) ||
            descriptor.ContentCapabilities.Distinct(StringComparer.Ordinal).Count() != descriptor.ContentCapabilities.Count)
            throw new RegistryException("descriptor_capabilities_invalid");
        return descriptor with { ContentCapabilities = Array.AsReadOnly(descriptor.ContentCapabilities.Order(StringComparer.Ordinal).ToArray()) };
    }
    private static void Validate(AdapterBinding binding)
    { ArgumentNullException.ThrowIfNull(binding); Id(binding.BackendId); Id(binding.ModelId); Id(binding.InstanceId); Id(binding.AdapterId); Id(binding.AdapterVersion); }
    private static void Id(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '-' or '_' or ':')))
            throw new RegistryException("binding_identifier_invalid");
    }
    private static string Kind(ContentPart part) => part switch
    {
        TextContentPart => "text", FileContentPart => "file", ImageContentPart => "image", AudioContentPart => "audio",
        StructuredContentPart => "structured", ResourceContentPart => "resource", _ => throw new RegistryException("content_unsupported")
    };
    private static void Metadata(AdapterBinding b, ModelGenerationRequest request, PreparedModelMetadata metadata,
        bool limited, ModelExecutionPolicy? selected = null)
    {
        if (selected is null) RemotePreparedProtocol.ValidateMetadata(metadata, request, limited);
        else LocalModelExecutionPolicy.ValidateMetadata(selected, request, metadata, limited);
        if (metadata.InstanceId != b.InstanceId || metadata.ModelId != b.ModelId || metadata.AdapterId != b.AdapterId || metadata.AdapterVersion != b.AdapterVersion)
            throw new RegistryException("prepared_identity_mismatch");
    }

    public class RegistryLease : IPreparedModelGeneration
    {
        private readonly AdapterRegistry registry;
        private readonly Entry entry;
        private readonly IPreparedModelGeneration inner;
        private readonly object leaseGate = new();
        private Task<ModelGenerationResult>? generation;
        private Task? disposal;
        private bool started, retired, disposeReturned;
        private ModelGenerationFinish? finish;
        private bool? cancellationConfirmed;
        public AdapterBinding Binding { get; }
        public PreparedModelMetadata Metadata { get; }
        internal RegistryLease(AdapterRegistry registry, Entry entry, AdapterBinding binding, IPreparedModelGeneration inner,
            PreparedModelMetadata validatedMetadata)
        { this.registry = registry; this.entry = entry; this.inner = inner; Binding = binding; Metadata = validatedMetadata; }
        public LeaseObservation Observe() { lock (leaseGate) return new(started, retired, disposeReturned, finish, cancellationConfirmed); }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            lock (leaseGate)
            {
                if (disposal is not null) throw new RegistryException("lease_disposing_or_retired");
                if (started) throw new RegistryException("lease_already_consumed");
                started = true;
                // Publish before calling any provider getter or callback. Monitor locks are
                // reentrant, so a synchronous onText may request disposal on this same thread.
                var completion = new TaskCompletionSource<ModelGenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = completion.Task;
                _ = GenerateCoreAsync(completion, onText, token);
                return completion.Task;
            }
        }
        private async Task GenerateCoreAsync(TaskCompletionSource<ModelGenerationResult> completion,
            Action<string>? onText, CancellationToken token)
        {
            try
            {
                lock (registry.gate) { RequireDescriptor(entry); }
                if (inner.Metadata != Metadata) throw new RegistryException("prepared_metadata_changed");
                lock (leaseGate) { if (disposal is not null) throw new RegistryException("lease_disposing_or_retired"); }
                var result = await inner.GenerateAsync(onText, token).ConfigureAwait(false);
                lock (leaseGate) { finish = result.Finish; cancellationConfirmed = result.CancellationConfirmed; }
                completion.TrySetResult(result); // Cancellation intent never upgrades Unknown to Cancelled.
            }
            catch (OperationCanceledException error)
            {
                lock (leaseGate) { finish = ModelGenerationFinish.Unknown; cancellationConfirmed = null; }
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                lock (leaseGate) { finish = ModelGenerationFinish.Unknown; cancellationConfirmed = null; }
                completion.TrySetException(error);
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (leaseGate)
            {
                if (disposal is null)
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposal = completion.Task;
                    _ = DisposeCoreAsync(completion, generation);
                }
                return new(disposal);
            }
        }
        private async Task DisposeCoreAsync(TaskCompletionSource completion, Task<ModelGenerationResult>? active)
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                // A provider returning early from Dispose cannot retire a still-running invocation.
                if (active is not null) { try { await active.ConfigureAwait(false); } catch { /* Original caller retains the failure. */ } }
                registry.Retired(entry);
                lock (leaseGate) { disposeReturned = true; retired = true; }
                completion.TrySetResult();
            }
            catch { registry.CleanupFailed(entry); completion.TrySetException(new RegistryException("lease_cleanup_unconfirmed")); }
        }
    }

    private sealed class PolicyRegistryLease(AdapterRegistry registry, Entry entry, AdapterBinding binding,
        IPreparedModelGeneration inner, PreparedModelMetadata metadata, ModelExecutionPolicy selected)
        : RegistryLease(registry, entry, binding, inner, metadata), IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = selected;
    }
}

/// <summary>Transport-neutral join owned by an admitted host callback/session. Core maps opaque
/// handles in that scope; neither a session ID nor a vendor operation is added to Core's frames.</summary>
internal sealed class AdapterCommandDispatch(AdapterRegistry registry)
{
    public sealed class Preparation
    {
        internal readonly AdapterCommandDispatch Owner;
        internal readonly AdapterRegistry.RegistryLease Lease;
        public string HandleId { get; } = Guid.NewGuid().ToString("N");
        public AdapterBinding Binding => Lease.Binding;
        public PreparedModelMetadata Metadata => Lease.Metadata;
        internal Preparation(AdapterCommandDispatch owner, AdapterRegistry.RegistryLease lease) { Owner = owner; Lease = lease; }
        public LeaseObservation Observe() => Lease.Observe();
    }
    public async Task<Preparation> PrepareAsync(AdapterBinding admittedBinding, AdapterRequirements required,
        ModelGenerationRequest request, CancellationToken token)
    {
        var adapter = registry.CreateAdapter(admittedBinding, required);
        var prepared = await adapter.PrepareAsync(request, token).ConfigureAwait(false);
        return new(this, (AdapterRegistry.RegistryLease)prepared);
    }
    internal async Task<Preparation> PrepareSelectedAsync(AdapterBinding admittedBinding,
        AdapterRequirements required, ModelGenerationRequest request, ModelExecutionPolicy selected,
        CancellationToken token)
    {
        var lease = await registry.PrepareSelectedAsync(admittedBinding, required, request,
            selected, token).ConfigureAwait(false);
        return new(this, lease);
    }
    public Task<ModelGenerationResult> GenerateAsync(Preparation handle, AdapterBinding admittedBinding,
        Action<string>? onText, CancellationToken token)
    { RequireOwned(handle, admittedBinding); return handle.Lease.GenerateAsync(onText, token); }
    public ValueTask ReleaseAsync(Preparation handle, AdapterBinding admittedBinding)
    { RequireOwned(handle, admittedBinding); return handle.Lease.DisposeAsync(); }
    private void RequireOwned(Preparation handle, AdapterBinding binding)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!ReferenceEquals(handle.Owner, this) || handle.Binding != binding) throw new RegistryException("preparation_owner_or_binding_mismatch");
    }
}

