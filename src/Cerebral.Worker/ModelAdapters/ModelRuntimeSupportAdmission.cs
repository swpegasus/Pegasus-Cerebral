using System.Text.Json.Serialization.Metadata;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker.ModelAdapters;

// Supplied by the host registration call, never by a package's support declaration.
// The existing typed handler and actual read-only framework codec metadata are witnesses only:
// admission never invokes them and does not create an expanded execution path.
internal sealed record RuntimeCapabilityImplementation(ModelRuntimeCapability Capability,
    IModelRuntimeAdapter Handler, JsonTypeInfo<ModelGenerationRequest> RequestCodec,
    JsonTypeInfo<ModelGenerationResult> ResultCodec);

internal sealed record ModelRuntimeSupportSnapshot(ModelRuntimeSupportDescriptor Descriptor)
{
    internal bool ExpandedPathSupported => false;
}

internal sealed record RuntimeKnownBoundCheck(long AggregateContextTokens,
    bool WithinKnownBounds, bool HasUnknownMaximum);

internal static class ModelRuntimeSupportAdmission
{
    internal static ModelRuntimeSupportSnapshot? Capture(IModelRuntimeAdapter adapter,
        ModelAdapterDescriptor facade, AdapterBinding actual, ModelRuntimeSupportBinding? expected,
        IReadOnlyList<RuntimeCapabilityImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        // The unchanged v1 registration route does not inspect or consume the optional facet.
        if (expected is null)
        {
            if (implementations.Count != 0) throw Invalid("runtime_support_binding_required");
            return null;
        }
        if (adapter is not IModelRuntimeSupportFacet source)
            throw Invalid("runtime_support_facet_required");
        var claim = source.RuntimeSupport ?? throw Invalid("runtime_support_missing");
        if (claim.SchemaVersion != ModelRuntimeSupportContract.SchemaVersion)
            throw Invalid("runtime_support_schema");
        ValidateBinding(expected);
        ValidateBinding(claim.Binding);
        if (expected.BackendId != actual.BackendId || expected.ModelId != actual.ModelId ||
            expected.InstanceId != actual.InstanceId || expected.AdapterId != actual.AdapterId ||
            expected.AdapterVersion != actual.AdapterVersion || expected.AdapterId != facade.AdapterId ||
            expected.AdapterVersion != facade.Version || claim.Binding != expected)
            throw Invalid("runtime_support_binding_mismatch");

        var capacity = FreezeCapacity(claim.Capacity);
        var capabilities = Bounded(claim.Capabilities, ModelRuntimeSupportContract.MaximumCapabilities)
            .Select(FreezeCapability).ToArray();
        if (capabilities.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != capabilities.Length)
            throw Invalid("runtime_support_duplicate_capability");
        var witnesses = Bounded(implementations, ModelRuntimeSupportContract.MaximumCapabilities);
        if (witnesses.Select(item => item?.Capability?.Id).Distinct(StringComparer.Ordinal).Count() != witnesses.Length)
            throw Invalid("runtime_support_duplicate_implementation");
        foreach (var capability in capabilities)
        {
            var witness = witnesses.SingleOrDefault(item => item?.Capability?.Id == capability.Id);
            if (witness is null || !ReferenceEquals(witness.Handler, adapter) ||
                witness.RequestCodec is null || witness.ResultCodec is null ||
                !witness.RequestCodec.IsReadOnly || !witness.ResultCodec.IsReadOnly ||
                !witness.RequestCodec.Options.IsReadOnly || !witness.ResultCodec.Options.IsReadOnly)
                throw Invalid("runtime_support_implementation_required");
            var implemented = FreezeCapability(witness.Capability);
            if (implemented.ContractId != capability.ContractId ||
                implemented.ContractVersion != capability.ContractVersion ||
                !implemented.InputKinds.SequenceEqual(capability.InputKinds, StringComparer.Ordinal) ||
                !implemented.OutputKinds.SequenceEqual(capability.OutputKinds, StringComparer.Ordinal))
                throw Invalid("runtime_support_contract_mismatch");
        }
        if (witnesses.Length != capabilities.Length) throw Invalid("runtime_support_unused_implementation");
        return new(claim with
        {
            Binding = expected with { },
            Capacity = capacity,
            Capabilities = Array.AsReadOnly(capabilities)
        });
    }

    // Necessary checks against reported outer bounds only. A single interval per dimension
    // cannot represent disabled-plus-enabled reasoning, intersected provenance, or coupled
    // input/visible/reasoning/safety fit. Existing validation and exact preparation remain authoritative.
    internal static RuntimeKnownBoundCheck CheckKnownCapacity(ModelRuntimeSupportSnapshot support,
        long inferenceSlots, long contextPerSlotTokens)
    {
        ArgumentNullException.ThrowIfNull(support);
        if (inferenceSlots <= 0 || contextPerSlotTokens <= 0) throw Invalid("runtime_support_choice");
        var aggregate = checked(inferenceSlots * contextPerSlotTokens);
        var capacity = support.Descriptor.Capacity;
        return new(aggregate, Contains(capacity.InferenceSlots, inferenceSlots) &&
            Contains(capacity.ContextPerSlotTokens, contextPerSlotTokens) &&
            Contains(capacity.AggregateContextTokens, aggregate),
            capacity.InferenceSlots.Maximum is null || capacity.ContextPerSlotTokens.Maximum is null ||
            capacity.AggregateContextTokens.Maximum is null);
    }

    private static bool Contains(RuntimeLimitRange range, long value) =>
        value >= range.Minimum && (range.Maximum is null || value <= range.Maximum.Value) &&
        (value - range.Minimum) % range.Step == 0;

    private static ModelRuntimeCapacityConstraints FreezeCapacity(ModelRuntimeCapacityConstraints value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with
        {
            InferenceSlots = FreezeRange(value.InferenceSlots),
            ContextPerSlotTokens = FreezeRange(value.ContextPerSlotTokens),
            VisibleOutputTokens = FreezeRange(value.VisibleOutputTokens),
            ReasoningTokens = FreezeRange(value.ReasoningTokens),
            CombinedGenerationTokens = FreezeRange(value.CombinedGenerationTokens),
            AggregateContextTokens = FreezeRange(value.AggregateContextTokens)
        };
    }

    private static RuntimeLimitRange FreezeRange(RuntimeLimitRange value)
    {
        if (value is null || value.Minimum < 0 || value.Step <= 0 ||
            value.Maximum is { } maximum && maximum < value.Minimum || value.Provenance is null ||
            !Enum.IsDefined(value.Provenance.Basis))
            throw Invalid("runtime_support_range");
        Id(value.Provenance.SourceId); Id(value.Provenance.Revision);
        return value with { Provenance = value.Provenance with { } };
    }

    private static ModelRuntimeCapability FreezeCapability(ModelRuntimeCapability value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id); Id(value.ContractId);
        if (value.ContractVersion <= 0) throw Invalid("runtime_support_contract_version");
        return value with { InputKinds = Names(value.InputKinds), OutputKinds = Names(value.OutputKinds) };
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<string> values)
    {
        var names = Bounded(values, ModelRuntimeSupportContract.MaximumKinds);
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw Invalid("runtime_support_kinds");
        foreach (var name in names) Id(name);
        return Array.AsReadOnly(names);
    }

    private static T[] Bounded<T>(IReadOnlyList<T> values, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum) throw Invalid("runtime_support_collection_bound");
        var snapshot = values.Take(maximum + 1).ToArray();
        if (snapshot.Length != values.Count || snapshot.Length > maximum)
            throw Invalid("runtime_support_collection_bound");
        return snapshot;
    }

    private static void ValidateBinding(ModelRuntimeSupportBinding binding)
    {
        if (binding is null) throw Invalid("runtime_support_binding_missing");
        Id(binding.WorkerInstallationId); Id(binding.BackendId); Id(binding.AdapterId);
        Id(binding.AdapterVersion); Id(binding.ModelId);
        ScopeId(binding.ConfigurationScopeId);
        if (binding.InstanceId is not null) Id(binding.InstanceId);
        if (binding.ConfigurationSha256 is not { Length: 64 } digest ||
            digest.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
            throw Invalid("runtime_support_configuration_digest");
    }

    private static void ScopeId(string value)
    {
        // Versioned codec identities such as Cerebral.RemotePreparedModelBinding.canonical/1
        // name digest scope, not a filesystem path or a claim about all Worker configuration.
        if (string.IsNullOrEmpty(value) || value.Length > ModelRuntimeSupportContract.MaximumIdentifierCharacters)
            throw Invalid("runtime_support_configuration_scope");
        var separator = value.LastIndexOf('/');
        if (separator < 1 || separator == value.Length - 1 ||
            value.AsSpan(0, separator).IndexOf('/') >= 0 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[separator - 1]) ||
            value[..separator].Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '-' or '_' or ':')) ||
            value[(separator + 1)..].Any(character => !char.IsAsciiDigit(character)) ||
            !int.TryParse(value.AsSpan(separator + 1), out var version) || version <= 0)
            throw Invalid("runtime_support_configuration_scope");
    }

    private static void Id(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ModelRuntimeSupportContract.MaximumIdentifierCharacters ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':')))
            throw Invalid("runtime_support_identifier");
    }

    private static RegistryException Invalid(string code) => new(code);
}
