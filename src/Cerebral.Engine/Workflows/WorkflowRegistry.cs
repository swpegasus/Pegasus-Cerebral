using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Workflows;

/// <summary>
/// An immutable, all-or-nothing registration snapshot. Native implementations are trusted code;
/// descriptors are frozen here so a plugin cannot change admitted versions or grants later.
/// </summary>
public sealed partial class WorkflowRegistry
{
    private const int MaximumContributions = 1024;
    private const int MaximumSchemaBytes = 64 * 1024;
    private readonly ImmutableDictionary<(string Id, string Version), IWorkflowDefinition> definitions;
    private readonly ImmutableDictionary<(string Id, int Version), IWorkflowCommandHandler> commands;
    private readonly ImmutableDictionary<(string Id, int Version), IContextProviderCommandHandler> contextProviders;

    public WorkflowRegistry(IEnumerable<IWorkflowModule> modules)
        : this(modules, requireConsumerCommands: true) { }

    /// <summary>Freeze one package through the normal descriptor and duplicate checks before
    /// the Worker's host commands exist. The returned module is metadata only; constructing the
    /// final runtime registry still requires each consumer's exact real command.</summary>
    public static IWorkflowModule FreezeCandidateShape(IEnumerable<IWorkflowModule> existingModules,
        IWorkflowModule candidateModule)
    {
        ArgumentNullException.ThrowIfNull(existingModules);
        ArgumentNullException.ThrowIfNull(candidateModule);
        // Read untrusted getters once. The registry validates this fixed identity and these
        // captured contribution lists, so a changing ID cannot select another owner's snapshot.
        var captured = new CapturedCandidateModule(candidateModule.Id,
            (candidateModule.Workflows ?? throw new ArgumentException("Candidate has no workflow list.", nameof(candidateModule)))
                .Take(MaximumContributions + 1).ToImmutableArray(),
            (candidateModule.Commands ?? throw new ArgumentException("Candidate has no command list.", nameof(candidateModule)))
                .Take(MaximumContributions + 1).ToImmutableArray());
        var checkedRegistry = new WorkflowRegistry(existingModules.Append(captured),
            requireConsumerCommands: false);
        var prefix = captured.Id + ":";
        return new FrozenCandidateModule(captured.Id,
            checkedRegistry.Definitions.Where(x => x.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(x => checkedRegistry.GetDefinition(x.Id, x.Version)).ToImmutableArray(),
            checkedRegistry.Commands.Where(x => x.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(x => checkedRegistry.GetCommand(x.Id, x.Version)).ToImmutableArray());
    }

    private WorkflowRegistry(IEnumerable<IWorkflowModule> modules, bool requireConsumerCommands)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var candidates = modules.Take(MaximumContributions + 1).ToArray();
        if (candidates.Length > MaximumContributions) throw new ArgumentException("Too many workflow modules.", nameof(modules));
        var definitionBuilder = ImmutableDictionary.CreateBuilder<(string, string), IWorkflowDefinition>();
        var commandBuilder = ImmutableDictionary.CreateBuilder<(string, int), IWorkflowCommandHandler>();
        var providerBuilder = ImmutableDictionary.CreateBuilder<(string, int), IContextProviderCommandHandler>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in candidates)
        {
            ArgumentNullException.ThrowIfNull(module);
            var owner = module.Id;
            if (owner is null || !PackageId().IsMatch(owner) || owner.Length > 128)
                throw new ArgumentException("Workflow module has an invalid package ID.", nameof(modules));
            if (!owners.Add(owner)) throw new ArgumentException($"Duplicate workflow module '{owner}'.", nameof(modules));
            var workflows = module.Workflows ?? throw new ArgumentException($"Module '{owner}' has no workflow list.", nameof(modules));
            var handlers = module.Commands ?? throw new ArgumentException($"Module '{owner}' has no command list.", nameof(modules));
            if (workflows.Count + handlers.Count > MaximumContributions - definitionBuilder.Count - commandBuilder.Count)
                throw new ArgumentException("Too many workflow contributions.", nameof(modules));
            foreach (var workflow in workflows)
            {
                ArgumentNullException.ThrowIfNull(workflow);
                var descriptor = Freeze(workflow.Descriptor, owner);
                IWorkflowDefinition frozen = workflow is IWorkflowPreparedRejectionSupport support
                    ? new FrozenPreparedDefinition(workflow, descriptor, support)
                    : new FrozenDefinition(workflow, descriptor);
                if (!definitionBuilder.TryAdd((descriptor.Id, descriptor.Version), frozen))
                    throw new ArgumentException($"Duplicate workflow '{descriptor.Id}' version '{descriptor.Version}'.", nameof(modules));
            }
            foreach (var handler in handlers)
            {
                ArgumentNullException.ThrowIfNull(handler);
                var descriptor = Freeze(handler.Descriptor, owner);
                IWorkflowCommandHandler frozen = new FrozenCommand(handler, descriptor);
                if (handler is IContextProviderCommandHandler provider)
                {
                    var providerDescriptor = Freeze(provider.Provider, descriptor, owner);
                    var contextProvider = new FrozenContextProviderCommand(handler, descriptor, providerDescriptor);
                    frozen = contextProvider;
                    providerBuilder.Add((providerDescriptor.Id, providerDescriptor.Version), contextProvider);
                }
                if (!commandBuilder.TryAdd((descriptor.Id, descriptor.Version), frozen))
                    throw new ArgumentException($"Duplicate command '{descriptor.Id}' version {descriptor.Version}.", nameof(modules));
            }
        }
        // Declarations may reference a command supplied by a later module. Resolve only after
        // all contributions are frozen; never infer consumer support from a workflow identity.
        if (requireConsumerCommands)
            foreach (var definition in definitionBuilder.Values)
                if (definition.Descriptor.LocalGenerationConsumer is { } consumer &&
                    !commandBuilder.ContainsKey((consumer.CommandId, consumer.CommandVersion)))
                    throw new ArgumentException("A local-generation consumer requires its exact registered command version.", nameof(modules));
        definitions = definitionBuilder.ToImmutable();
        commands = commandBuilder.ToImmutable();
        contextProviders = providerBuilder.ToImmutable();
        Definitions = definitions.Values.Select(x => x.Descriptor).OrderBy(x => x.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal).ToImmutableArray();
        Commands = commands.Values.Select(x => x.Descriptor).OrderBy(x => x.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Version).ToImmutableArray();
        ContextProviders = contextProviders.Values.Select(x => x.Provider).OrderBy(x => x.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Version).ToImmutableArray();
    }

    public IReadOnlyList<WorkflowDefinitionDescriptor> Definitions { get; }
    public IReadOnlyList<WorkflowCommandDescriptor> Commands { get; }
    public IReadOnlyList<ContextProviderDescriptor> ContextProviders { get; }

    public bool TryGetDefinition(string id, string version, out IWorkflowDefinition definition) =>
        definitions.TryGetValue((id, version), out definition!);
    public bool TryGetCommand(string id, int version, out IWorkflowCommandHandler handler) =>
        commands.TryGetValue((id, version), out handler!);
    public bool TryGetContextProvider(string id, int version, out IContextProviderCommandHandler provider) =>
        contextProviders.TryGetValue((id, version), out provider!);
    public IWorkflowDefinition GetDefinition(string id, string version) => TryGetDefinition(id, version, out var value)
        ? value : throw new KeyNotFoundException($"Workflow '{id}' version '{version}' is unavailable.");
    public IWorkflowCommandHandler GetCommand(string id, int version) => TryGetCommand(id, version, out var value)
        ? value : throw new KeyNotFoundException($"Command '{id}' version {version} is unavailable.");
    public IContextProviderCommandHandler GetContextProvider(string id, int version) =>
        TryGetContextProvider(id, version, out var value) ? value
            : throw new KeyNotFoundException($"Context provider '{id}' version {version} is unavailable.");

    private static WorkflowDefinitionDescriptor Freeze(WorkflowDefinitionDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateId(descriptor.Id, owner);
        if (descriptor.Version is null || descriptor.Version.Length > 64 || !VersionId().IsMatch(descriptor.Version))
            throw new ArgumentException($"Workflow '{descriptor.Id}' has an invalid version.");
        return descriptor with
        {
            Description = ValidateDescription(descriptor.Description), InputSchema = FreezeSchema(descriptor.InputSchema),
            RequiredCapabilities = FreezeCapabilities(descriptor.RequiredCapabilities),
            ToolReceiptConsumer = descriptor.ToolReceiptConsumer is { } consumer
                ? consumer.SchemaVersion == WorkflowToolReceiptContextConsumerContract.SchemaVersion
                    ? new(consumer.SchemaVersion, consumer.Supported)
                    : throw new ArgumentException($"Workflow '{descriptor.Id}' has an unsupported receipt consumer schema.")
                : null,
            LocalGenerationConsumer = WorkflowLocalGenerationAdmission.Freeze(descriptor.LocalGenerationConsumer)
        };
    }

    private static WorkflowCommandDescriptor Freeze(WorkflowCommandDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateId(descriptor.Id, owner);
        if (descriptor.Version < 1) throw new ArgumentException($"Command '{descriptor.Id}' has an invalid version.");
        if (descriptor.TimeoutSeconds is < 1 or > 3600) throw new ArgumentException($"Command '{descriptor.Id}' timeout must be 1–3600 seconds.");
        if (!Enum.IsDefined(descriptor.ReplayPolicy)) throw new ArgumentException($"Command '{descriptor.Id}' has an invalid replay policy.");
        return descriptor with
        {
            Description = ValidateDescription(descriptor.Description), InputSchema = FreezeSchema(descriptor.InputSchema),
            OutputSchema = FreezeSchema(descriptor.OutputSchema), RequiredCapabilities = FreezeCapabilities(descriptor.RequiredCapabilities)
        };
    }

    private static ContextProviderDescriptor Freeze(ContextProviderDescriptor descriptor,
        WorkflowCommandDescriptor command, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateId(descriptor.Id, owner);
        if (descriptor.Id != command.Id || descriptor.Version != command.Version)
            throw new ArgumentException("Context provider identity and version must match its workflow command.");
        if (command.ReplayPolicy != WorkflowReplayPolicy.Idempotent)
            throw new ArgumentException($"Read-only context provider '{descriptor.Id}' must use the idempotent replay classification.");
        var description = ValidateDescription(descriptor.Description);
        if (!string.Equals(description, command.Description, StringComparison.Ordinal))
            throw new ArgumentException($"Context provider '{descriptor.Id}' description must match its workflow command.");
        if (descriptor.MaxReferences is < 1 or > ContextProviderContract.MaximumReferences)
            throw new ArgumentException($"Context provider '{descriptor.Id}' reference bound must be 1–{ContextProviderContract.MaximumReferences}.");
        if (descriptor.MaxUtf8Bytes is < 1 or > ContextProviderContract.MaximumUtf8Bytes)
            throw new ArgumentException($"Context provider '{descriptor.Id}' byte bound must be 1–{ContextProviderContract.MaximumUtf8Bytes}.");
        if (descriptor.SupportedReferenceKinds is null || descriptor.SupportedReferenceKinds.Count is < 1 or > ContextProviderContract.MaximumReferenceKinds)
            throw new ArgumentException($"Context provider '{descriptor.Id}' requires a bounded reference-kind list.");
        var kinds = descriptor.SupportedReferenceKinds.ToArray();
        if (kinds.Any(kind => kind is null || kind.Length > 64 || !ReferenceKind().IsMatch(kind)) ||
            kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length)
            throw new ArgumentException($"Context provider '{descriptor.Id}' reference kinds must be distinct valid names.");
        return descriptor with { Description = description, SupportedReferenceKinds = kinds.ToImmutableArray() };
    }

    private static void ValidateId(string id, string owner)
    {
        if (id is null || id.Length > 256 || !id.StartsWith(owner + ":", StringComparison.Ordinal) ||
            !LocalId().IsMatch(id[(owner.Length + 1)..]))
            throw new ArgumentException($"Contribution ID must belong to '{owner}:' and have a valid local name.");
    }

    private static string ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 4096 || description.Contains('\0'))
            throw new ArgumentException("Workflow contribution description must contain 1–4096 characters.");
        return description;
    }

    private static ImmutableArray<string> FreezeCapabilities(IReadOnlyList<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count > 64) throw new ArgumentException("Too many required capabilities.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
            if (capability is null || capability.Length > 128 || !CapabilityId().IsMatch(capability) || !seen.Add(capability))
                throw new ArgumentException("Required capabilities must have distinct valid names.");
        return capabilities.ToImmutableArray();
    }

    private static JsonElement FreezeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Workflow schemas must be JSON objects.");
        var raw = schema.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MaximumSchemaBytes) throw new ArgumentException("Workflow schema exceeds 64 KiB.");
        using var bounded = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        WorkflowSchema.ValidateSchema(bounded.RootElement);
        return bounded.RootElement.Clone();
    }

    private sealed record CapturedCandidateModule(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private sealed record FrozenCandidateModule(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private sealed record FrozenDefinition(IWorkflowDefinition Inner, WorkflowDefinitionDescriptor Descriptor) : IWorkflowDefinition
    {
        public WorkflowStep Advance(WorkflowRun snapshot) => Inner.Advance(snapshot);
    }
    private sealed record FrozenPreparedDefinition(IWorkflowDefinition Inner,
        WorkflowDefinitionDescriptor Descriptor, IWorkflowPreparedRejectionSupport Support)
        : IWorkflowDefinition, IWorkflowPreparedRejectionSupport
    {
        public WorkflowStep Advance(WorkflowRun snapshot) => Inner.Advance(snapshot);
        public bool CanRejectPrepared(WorkflowRun snapshot) => Support.CanRejectPrepared(snapshot);
    }
    private sealed record FrozenCommand(IWorkflowCommandHandler Inner, WorkflowCommandDescriptor Descriptor) : IWorkflowCommandHandler
    {
        public string? GetFailureBudgetKey(WorkflowCommandContext context) => Inner.GetFailureBudgetKey(context);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Inner.ExecuteAsync(context, cancellationToken);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Inner.ReconcileAsync(context, cancellationToken);
    }
    private sealed record FrozenContextProviderCommand(IWorkflowCommandHandler Inner,
        WorkflowCommandDescriptor Descriptor, ContextProviderDescriptor Provider) : IContextProviderCommandHandler
    {
        public string? GetFailureBudgetKey(WorkflowCommandContext context) => Inner.GetFailureBudgetKey(context);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Inner.ExecuteAsync(context, cancellationToken);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Inner.ReconcileAsync(context, cancellationToken);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalId();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.+_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionId();
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.:_-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityId();
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[./:_+-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceKind();
}
